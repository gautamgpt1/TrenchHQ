using TrenchHQ.Core.OnChain;
using TrenchHQ.Core.OnChain.Evm;
using TrenchHQ.Core.OnChain.Solana;
using TrenchHQ.Core.Providers;
using TrenchHQ.Core.Wallets;
using TrenchHQ.Core.Widgets;
using TrenchHQ.Infrastructure.Diagnostics;
using TrenchHQ.Infrastructure.OnChain;
using TrenchHQ.Infrastructure.OnChain.Evm;
using TrenchHQ.Infrastructure.OnChain.Solana;
using TrenchHQ.Infrastructure.Providers;
using TrenchHQ.Yellowstone;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Xunit;
using TrenchHQ.TestSupport;
using static TrenchHQ.TestSupport.OnChainFixtures;

namespace TrenchHQ.OnChainIntegrationTests;

public partial class OnChainTests
{
    static async Task VerifyEvmRecoveryPrimitivesAsync()
    {
        var tracker = new EvmHeadTracker();
        var head10 = CreateEvmHead(10, Hash(10), Hash(9));
        var head11 = CreateEvmHead(11, Hash(11), Hash(10));
        var head12 = CreateEvmHead(12, Hash(12), Hash(11));
        var head13 = CreateEvmHead(13, Hash(13), Hash(12));
        AssertEqual(EvmHeadTransitionKind.First, tracker.Apply(head10).Kind,
            "The first EVM head was not accepted.");
        AssertEqual(EvmHeadTransitionKind.Extended, tracker.Apply(head11).Kind,
            "A canonical EVM head extension was not accepted.");
        var gap = tracker.Apply(head13);
        AssertEqual(EvmHeadTransitionKind.Gap, gap.Kind,
            "A missing EVM block was not detected.");
        AssertEqual<ulong?>(12, gap.MissingFrom, "The EVM gap start is wrong.");
        AssertEqual<ulong?>(12, gap.MissingTo, "The EVM gap end is wrong.");
        AssertEqual(11UL, tracker.Tip!.Number, "A gapped head mutated canonical state.");
        tracker.Apply(head12);
        tracker.Apply(head13);

        var replacement12 = CreateEvmHead(12, Hash(112), Hash(11));
        var reorg = tracker.Apply(replacement12);
        AssertEqual(EvmHeadTransitionKind.Reorg, reorg.Kind,
            "A shallow EVM reorg was not detected.");
        AssertEqual<ulong?>(11, reorg.CommonAncestorNumber,
            "The EVM reorg common ancestor is wrong.");
        var unknownParent = CreateEvmHead(13, Hash(113), Hash(99));
        AssertEqual(EvmHeadTransitionKind.SnapshotRequired, tracker.Apply(unknownParent).Kind,
            "An EVM fork outside the canonical ring did not require a snapshot.");

        tracker.Reset(CreateEvmHead(1, Hash(1), Hash(0)));
        for (ulong number = 2; number <= EvmHeadTracker.Capacity + 2UL; number++)
        {
            tracker.Apply(CreateEvmHead(number, Hash(number), Hash(number - 1)));
        }
        AssertEqual(EvmHeadTransitionKind.SnapshotRequired,
            tracker.Apply(CreateEvmHead(3, Hash(1003), Hash(2))).Kind,
            "A fork deeper than the canonical ring was treated as reversible.");

        var finality = new EvmFinalityUpdate
        {
            ChainId = "1",
            Head = new EvmBlockReference { Number = 130, Hash = Hash(130) },
            Safe = new EvmBlockReference { Number = 125, Hash = Hash(125) },
            Finalized = new EvmBlockReference { Number = 120, Hash = Hash(120) }
        };
        Assert(tracker.ApplyFinality(finality), "Valid EVM finality was rejected.");
        Assert(!tracker.ApplyFinality(new EvmFinalityUpdate
            {
                ChainId = "1",
                Head = finality.Head,
                Safe = new EvmBlockReference { Number = 124, Hash = Hash(124) },
                Finalized = finality.Finalized
            }),
            "Regressing EVM safe finality was accepted.");
        tracker.SetSampledTip(CreateEvmHead(200, Hash(200), Hash(199)));
        AssertEqual(200UL, tracker.Tip!.Number,
            "A sampled EVM head did not replace the sparse canonical head window.");
        AssertEqual<ulong?>(125, tracker.Safe?.Number,
            "Advancing a sampled EVM head discarded safe-finality monotonicity.");
        AssertEqual<ulong?>(120, tracker.Finalized?.Number,
            "Advancing a sampled EVM head discarded finalized monotonicity.");

        var deduplicator = new EvmLogDeduplicator(2);
        var first = CreateEvmLog(10, Hash(10), Hash(100), 1);
        AssertEqual(EvmLogAcceptance.Accepted, deduplicator.Accept(first),
            "The first EVM log was rejected.");
        AssertEqual(EvmLogAcceptance.Duplicate, deduplicator.Accept(first),
            "A duplicate EVM log was accepted.");
        first.Removed = true;
        AssertEqual(EvmLogAcceptance.Removed, deduplicator.Accept(first),
            "A known removed EVM log was not reversed.");
        AssertEqual(EvmLogAcceptance.UnknownRemoval, deduplicator.Accept(first),
            "A duplicate removal was treated as a canonical event.");
        first.Removed = false;
        AssertEqual(EvmLogAcceptance.Accepted, deduplicator.Accept(first),
            "A reintroduced canonical EVM log was blocked by stale dedup state.");
        deduplicator.Accept(CreateEvmLog(11, Hash(11), Hash(101), 1));
        deduplicator.RollbackAfter(10);
        AssertEqual(EvmLogAcceptance.Accepted,
            deduplicator.Accept(CreateEvmLog(11, Hash(11), Hash(101), 1)),
            "Rollback did not release orphaned EVM log IDs.");

        var ranges = EvmRecoveryPlanner.BuildRanges(10, 260);
        AssertEqual(3, ranges.Length, "EVM recovery paging produced the wrong number of ranges.");
        AssertEqual(new EvmBlockRange(10, 109), ranges[0], "The first EVM recovery range is wrong.");
        AssertEqual(new EvmBlockRange(210, 260), ranges[2], "The last EVM recovery range is wrong.");
        AssertEqual(10UL,
            EvmStreamCoordinator.GetReplayPageSize(
                EvmChainDefinitions.RobinhoodMainnetChainId,
                OnChainProviderTypes.PublicNodeRobinhood),
            "Robinhood Chain recovery no longer respects its ten-block eth_getLogs limit.");
        AssertEqual(EvmRecoveryPlanner.DefaultPageSize,
            EvmStreamCoordinator.GetReplayPageSize(
                EvmChainDefinitions.EthereumMainnetChainId,
                OnChainProviderTypes.PublicNodeEthereum),
            "The Robinhood Chain range limit leaked into other EVM chains.");
        foreach (var (chainId, providerType) in new[]
                 {
                     (EvmChainDefinitions.EthereumMainnetChainId, OnChainProviderTypes.AlchemyEthereum),
                     (EvmChainDefinitions.BaseMainnetChainId, OnChainProviderTypes.AlchemyBase),
                     (EvmChainDefinitions.BnbMainnetChainId, OnChainProviderTypes.AlchemyBnb),
                     (EvmChainDefinitions.RobinhoodMainnetChainId, OnChainProviderTypes.AlchemyRobinhood)
                 })
        {
            var pageSize = EvmStreamCoordinator.GetReplayPageSize(chainId, providerType);
            AssertEqual(10UL, pageSize,
                $"Alchemy {chainId} replay exceeded its Free-tier eth_getLogs range.");
            var alchemyRanges = EvmRecoveryPlanner.BuildRanges(10, 109, pageSize);
            AssertEqual(10, alchemyRanges.Length,
                $"Alchemy {chainId} replay discarded history instead of paging it.");
            AssertEqual(new EvmBlockRange(100, 109), alchemyRanges[^1],
                $"Alchemy {chainId} replay lost its final ten-block page.");
        }
        foreach (var chain in EvmChainDefinitions.Supported)
        {
            Assert(EvmStreamCoordinator.UsesSampledHeads(chain.ChainId),
                $"{chain.DisplayName} returned to its quota-exhausting per-block head stream.");
            Assert(!EvmWebSocketStreamSource.ShouldSubscribeToHeads(chain.ChainId),
                $"{chain.DisplayName} still requests the metered chain-wide newHeads stream.");
            AssertEqual(TimeSpan.FromMinutes(1),
                EvmStreamCoordinator.GetHeadSampleInterval(chain.ChainId),
                $"{chain.DisplayName} canonical-head sampling is no longer quota bounded.");
            AssertEqual(TimeSpan.FromMinutes(5),
                EvmStreamCoordinator.GetSampledSnapshotInterval(chain.ChainId),
                $"{chain.DisplayName} state reconciliation is no longer quota bounded.");
        }
        Assert(!EvmStreamCoordinator.UsesSampledHeads("999999"),
            "Sampled-head behavior was enabled for an unsupported EVM chain.");
        Assert(EvmWebSocketStreamSource.ShouldSubscribeToHeads("999999"),
            "An unsupported/custom EVM chain unexpectedly lost its head subscription.");
        AssertEqual(TimeSpan.FromMinutes(1), EvmWalletActivityStreamSource.FinalityRefreshInterval,
            "EVM Wallet Watcher finality polling returned to its quota-heavy cadence.");
        AssertEqual(TimeSpan.FromSeconds(30), SolanaWalletActivityStreamSource.MaintenanceInterval,
            "Solana Wallet Watcher pending-signature maintenance returned to its quota-heavy cadence.");
        AssertEqual(512, EvmWalletActivityStreamSource.MaximumTokenMetadataEntries,
            "The EVM Wallet Watcher token metadata cache lost its resource bound.");
        AssertEqual(TimeSpan.FromSeconds(5), EvmWalletActivityStreamSource.TraceBatchInterval,
            "EVM Wallet Watcher live trace batching returned to a per-block cadence.");
        AssertEqual(20, EvmWalletActivityStreamSource.MaximumTraceBatchBlocks,
            "EVM Wallet Watcher live trace batching lost its reconnect-safe block bound.");
        Assert(SolanaWebSocketStreamSource.GetRuntimeSubscriptionMethods(
                ["account-a", "account-b"],
                ["pool-a"])
            .SequenceEqual(["accountSubscribe", "accountSubscribe", "logsSubscribe"]),
            "The retained exact Solana WebSocket adapter reintroduced the unused slot firehose.");
        AssertEqual(TimeSpan.FromSeconds(1), SolanaRpcSampleStreamSource.SampleInterval,
            "Solana price-only sampling lost its bounded cadence.");
        var solanaPreset = OnChainProviderConfigurationStore.CreateConfiguration(
            OnChainProviderCatalog.Get(OnChainProviderTypes.AlchemyWebSocket));
        Assert(OnChainStreamCoordinator.CreateSource(solanaPreset, "fixture-key", true)
               is SolanaWebSocketStreamSource,
            "The explicit Solana WebSocket mode did not select the exact stream source.");
        var traceBatch = new EvmWalletTraceBatch(
            EvmWalletActivityStreamSource.MaximumTraceBatchBlocks);
        for (ulong block = 100; block < 120; block++)
        {
            traceBatch.Add(block, checked((long)block * 1000));
        }
        AssertEqual(20, traceBatch.Count, "EVM wallet traces were not batched to the configured bound.");
        Assert(!traceBatch.CanAdd(120, 120),
            "EVM wallet trace batching accepted a range beyond its reconnect-safe bound.");
        traceBatch.RemoveAfter(109);
        AssertEqual(10, traceBatch.Count, "EVM wallet trace reorg rollback retained orphaned blocks.");
        Assert(traceBatch.CanAdd(110, 119),
            "EVM wallet trace reorg rollback did not release the canonical replacement range.");
        traceBatch.Clear();
        AssertEqual(0, traceBatch.Count, "EVM wallet trace batch did not clear after publication.");
        var monthlyHeadAndFinalityComputeUnits =
            TimeSpan.FromDays(30).TotalMinutes * 4 * 20;
        Assert(monthlyHeadAndFinalityComputeUnits < 4_000_000,
            "The per-chain sampled-head policy exceeds its bounded monthly RPC budget.");
        AssertEqual(94UL, EvmRecoveryPlanner.GetRecoveryStart(100, 110),
            "The EVM checkpoint overlap is wrong.");
        AssertEqual(100UL, EvmRecoveryPlanner.ApplyReplayDepthLimit(100, 599, 500),
            "A replay range inside the provider depth was discarded.");
        AssertEqual(600UL, EvmRecoveryPlanner.ApplyReplayDepthLimit(100, 600, 500),
            "An unavailable deep replay range did not fall back to a current snapshot.");
        AssertEqual(61UL, EvmStreamCoordinator.GetProviderReplayStart(
                OnChainProviderTypes.PublicNodeRobinhood, 0, 100),
            "Robinhood PublicNode startup replay exceeded its verified recent-log window.");
        AssertEqual(95UL, EvmStreamCoordinator.GetProviderReplayStart(
                OnChainProviderTypes.PublicNodeRobinhood, 95, 100),
            "A recent Robinhood PublicNode checkpoint was widened unnecessarily.");
        AssertEqual(0UL, EvmStreamCoordinator.GetProviderReplayStart(
                OnChainProviderTypes.PublicNodeRobinhood, 0, 20),
            "Robinhood PublicNode replay underflowed near genesis.");
        AssertEqual(0UL, EvmStreamCoordinator.GetProviderReplayStart(
                OnChainProviderTypes.AlchemyRobinhood, 0, 100),
            "The public-endpoint replay limit changed the saved Alchemy route.");

        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "TrenchHQEvmCheckpoints",
            Guid.NewGuid().ToString("N"));
        try
        {
            var checkpoint = new EvmCheckpoint
            {
                ChainId = "1",
                SafeBlockNumber = 120,
                SafeBlockHash = Hash(120),
                ObservedAtUnixMs = 1000
            };
            await EvmCheckpointStore.SaveAsync(temporaryRoot, "profile-1", checkpoint);
            var loaded = await EvmCheckpointStore.LoadAsync(temporaryRoot, "profile-1");
            AssertEqual(120UL, loaded!.SafeBlockNumber, "The EVM checkpoint block changed.");
            AssertEqual(Hash(120), loaded.SafeBlockHash, "The EVM checkpoint hash changed.");
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, true);
            }
        }
    }
    static async Task VerifyEvmCoordinatorCollectionAsync(string enginePath)
    {
        var client = new OnChainEngineClient(enginePath);
        var coordinators = new EvmStreamCoordinatorCollection(
            client,
            Path.GetTempPath());
        AssertEqual(4, coordinators.Coordinators.Count,
            "The fixed EVM coordinator collection did not register all supported chains.");
        AssertEqual(EvmChainDefinitions.EthereumMainnetChainId,
            coordinators.Get(EvmChainDefinitions.EthereumMainnetChainId).ChainDefinition.ChainId,
            "The coordinator collection returned the wrong Ethereum owner.");
        AssertEqual(EvmChainDefinitions.BaseMainnetChainId,
            coordinators.Get(EvmChainDefinitions.BaseMainnetChainId).ChainDefinition.ChainId,
            "The coordinator collection returned the wrong Base owner.");
        AssertEqual(EvmChainDefinitions.BnbMainnetChainId,
            coordinators.Get(EvmChainDefinitions.BnbMainnetChainId).ChainDefinition.ChainId,
            "The coordinator collection returned the wrong BNB owner.");
        AssertEqual(EvmChainDefinitions.RobinhoodMainnetChainId,
            coordinators.Get(EvmChainDefinitions.RobinhoodMainnetChainId).ChainDefinition.ChainId,
            "The coordinator collection returned the wrong Robinhood Chain owner.");
        await coordinators.StopAsync();
        await client.StopAsync();
    }
    static async Task VerifyEthereumReferencePricingAsync(string enginePath)
    {
        const string selectedToken = "0x1111111111111111111111111111111111111111";
        const string selectedPool = "0x4444444444444444444444444444444444444444";
        var selected = new OnChainWatchedPoolSelection
        {
            SelectedMint = selectedToken,
            Descriptor = new OnChainPoolDescriptor
            {
                PoolKey = new OnChainPoolKey
                {
                    DeploymentKey = EthereumDeploymentRegistry.UniswapV3Deployment(),
                    PoolId = selectedPool
                },
                PoolType = "concentratedLiquidity",
                ProgramId = EthereumDeploymentRegistry.UniswapV3Factory,
                BaseMint = selectedToken,
                QuoteMint = EthereumDeploymentRegistry.WrappedEther,
                BaseDecimals = 18,
                QuoteDecimals = 18,
                PairOrientation = "selectedAsToken0",
                SupportStatus = OnChainSupportStatus.Supported,
                Asset0 = new OnChainAssetKey
                {
                    ChainNamespace = ChainNamespaces.Eip155,
                    ChainId = EvmChainDefinitions.EthereumMainnet.ChainId,
                    Address = selectedToken
                },
                Asset1 = new OnChainAssetKey
                {
                    ChainNamespace = ChainNamespaces.Eip155,
                    ChainId = EvmChainDefinitions.EthereumMainnet.ChainId,
                    Address = EthereumDeploymentRegistry.WrappedEther
                },
                FeeTier = 3000,
                TickSpacing = 60
            }
        };
        var reference = EthereumDeploymentRegistry.CreateEthUsdReferenceSelection();
        var prices = new ConcurrentQueue<OnChainPriceUpdate>();
        var client = new OnChainEngineClient(enginePath);
        client.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
        try
        {
            await client.ReplaceWatchedPoolsAsync([selected, reference]);
            await client.SetEvmProviderContextAsync("ethereum-profile", "ethereum-json-rpc:test");
            await client.PublishEvmHeadAsync(CreateEvmHead(20, Hash(20), Hash(19)));
            var q96 = System.Numerics.BigInteger.One << 96;
            var observedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await client.PublishEvmLogAsync(new EvmLogUpdate
            {
                ChainId = EvmChainDefinitions.EthereumMainnet.ChainId,
                ConnectionEpoch = 11,
                Address = selectedPool,
                Topics = [EvmEventTopics.UniswapV3SwapTopic, Hash(4000), Hash(4001)],
                Data = AbiBigWords(
                    2_000_000_000_000_000_000,
                    -4_000_000_000_000_000_000,
                    q96,
                    100,
                    0),
                BlockNumber = 20,
                BlockHash = Hash(20),
                TransactionHash = Hash(4002),
                TransactionIndex = 1,
                LogIndex = 1,
                ObservedAtUnixMs = observedAt
            });
            await WaitUntilAsync(
                () => prices.Any(update => update.PoolKey.PoolId == selectedPool),
                TimeSpan.FromSeconds(5),
                "ETH-quoted target price");
            var native = prices.Last(update => update.PoolKey.PoolId == selectedPool);
            AssertDecimalValue(native.PriceNative, 2,
                "An ETH-quoted Uniswap price was not exposed in native ETH units.");
            Assert(native.PriceUsd == null,
                "An ETH-quoted price was labeled USD before a reference price existed.");

            await client.PublishEvmLogAsync(new EvmLogUpdate
            {
                ChainId = EvmChainDefinitions.EthereumMainnet.ChainId,
                ConnectionEpoch = 11,
                Address = EthereumDeploymentRegistry.WrappedEtherUsdcReferencePool,
                Topics = [EvmEventTopics.UniswapV3SwapTopic, Hash(4010), Hash(4011)],
                Data = AbiBigWords(
                    2_000_000_000,
                    -1_000_000_000_000_000_000,
                    q96,
                    100,
                    0),
                BlockNumber = 20,
                BlockHash = Hash(20),
                TransactionHash = Hash(4012),
                TransactionIndex = 2,
                LogIndex = 2,
                ObservedAtUnixMs = observedAt
            });
            await WaitUntilAsync(
                () => prices.Any(update => update.PoolKey.PoolId == selectedPool
                                           && update.PriceUsd != null),
                TimeSpan.FromSeconds(5),
                "on-chain ETH/USD conversion");
            var onChainUsd = prices.Last(update => update.PoolKey.PoolId == selectedPool
                                                    && update.PriceUsd != null);
            AssertDecimalValue(onChainUsd.PriceUsd, 4000,
                "The on-chain WETH/USDC reference conversion was wrong.");
            Assert(onChainUsd.ReferenceSourceId?.StartsWith(
                       "ethereum:uniswap-v3:WETH/USDC:500:",
                       StringComparison.Ordinal) == true,
                "The on-chain ETH/USD reference provenance was lost.");
            AssertEqual("ethereum-profile", onChainUsd.ProviderProfileId,
                "The Ethereum provider profile identity was lost.");
            AssertEqual("ethereum-json-rpc:test", onChainUsd.SourceId,
                "The Ethereum source identity was lost.");

            await client.ResetEvmForSnapshotAsync(EvmChainDefinitions.EthereumMainnet.ChainId);
            await client.PublishEvmReferencePriceAsync(
                EvmChainDefinitions.EthereumMainnet.ChainId,
                "ethUsd",
                new OnChainDecimalValue { Coefficient = "2100", Scale = 0 },
                "ccxt:binance:ETH/USDT",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await client.PublishEvmHeadAsync(CreateEvmHead(21, Hash(21), Hash(20)));
            await client.PublishEvmLogAsync(new EvmLogUpdate
            {
                ChainId = EvmChainDefinitions.EthereumMainnet.ChainId,
                ConnectionEpoch = 12,
                Address = selectedPool,
                Topics = [EvmEventTopics.UniswapV3SwapTopic, Hash(4020), Hash(4021)],
                Data = AbiBigWords(
                    2_000_000_000_000_000_000,
                    -4_000_000_000_000_000_000,
                    q96,
                    100,
                    0),
                BlockNumber = 21,
                BlockHash = Hash(21),
                TransactionHash = Hash(4022),
                TransactionIndex = 1,
                LogIndex = 1,
                ObservedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
            await WaitUntilAsync(
                () => prices.Any(update => update.PoolKey.PoolId == selectedPool
                                           && update.ReferenceSourceId == "ccxt:binance:ETH/USDT"),
                TimeSpan.FromSeconds(5),
                "ETH/USD fallback conversion");
            var fallbackUsd = prices.Last(update => update.PoolKey.PoolId == selectedPool
                                                   && update.ReferenceSourceId == "ccxt:binance:ETH/USDT");
            AssertDecimalValue(fallbackUsd.PriceUsd, 4200,
                "The ETH/USDT fallback conversion was wrong.");
        }
        finally
        {
            await client.StopAsync();
        }
    }
    static async Task VerifyEvmStreamCoordinatorAsync(string enginePath)
    {
        const string selectedToken = "0x1111111111111111111111111111111111111111";
        const string poolAddress = "0x2222222222222222222222222222222222222222";
        var checkpointRoot = Path.Combine(
            Path.GetTempPath(),
            "TrenchHQEvmCheckpoint",
            Guid.NewGuid().ToString("N"));
        var baselineProcessIds = GetEngineProcessIds();
        var client = new OnChainEngineClient(enginePath);
        var prices = new ConcurrentQueue<OnChainPriceUpdate>();
        client.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
        var evmPool = new OnChainWatchedPoolSelection
        {
            SelectedMint = selectedToken,
            Descriptor = new OnChainPoolDescriptor
            {
                PoolKey = new OnChainPoolKey
                {
                    DeploymentKey = EthereumDeploymentRegistry.UniswapV2Deployment(),
                    PoolId = poolAddress
                },
                PoolType = "constantProduct",
                ProgramId = EthereumDeploymentRegistry.UniswapV2Factory,
                BaseMint = selectedToken,
                QuoteMint = EthereumDeploymentRegistry.Usdc,
                BaseDecimals = 18,
                QuoteDecimals = 6,
                PairOrientation = "selectedAsToken0",
                SupportStatus = OnChainSupportStatus.Supported,
                Asset0 = new OnChainAssetKey
                {
                    ChainNamespace = ChainNamespaces.Eip155,
                    ChainId = EvmChainDefinitions.EthereumMainnet.ChainId,
                    Address = selectedToken
                },
                Asset1 = new OnChainAssetKey
                {
                    ChainNamespace = ChainNamespaces.Eip155,
                    ChainId = EvmChainDefinitions.EthereumMainnet.ChainId,
                    Address = EthereumDeploymentRegistry.Usdc
                }
            }
        };
        var mintBytes = Enumerable.Repeat((byte)3, 32).ToArray();
        var solanaMint = EncodeBase58(mintBytes);
        var solanaPool = new OnChainWatchedPoolSelection
        {
            SelectedMint = solanaMint,
            Descriptor = new OnChainPoolDescriptor
            {
                PoolKey = new OnChainPoolKey
                {
                    ProtocolId = OnChainProtocolIds.PumpBondingCurve,
                    PoolAddress = "shared-engine-curve"
                },
                PoolType = "bondingCurve",
                ProgramId = "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P",
                BaseMint = solanaMint,
                QuoteMint = "So11111111111111111111111111111111111111112",
                BaseDecimals = 6,
                QuoteDecimals = 9,
                SupportStatus = OnChainSupportStatus.Supported
            }
        };
        var configuration = CreateProviderConfiguration(
            OnChainProviderTypes.AlchemyEthereum,
            "wss://fixture.invalid",
            "https://fixture.invalid");
        configuration.Id = "evm-coordinator-profile";
        configuration.CapabilitySnapshot = new OnChainProviderCapabilitySnapshot
        {
            SafeBlock = OnChainProviderCapabilityState.Supported,
            FinalizedBlock = OnChainProviderCapabilityState.Supported
        };
        using var handler = new EvmCoordinatorRpcFixtureHandler(poolAddress);
        using var httpClient = new HttpClient(handler);
        var source = new CoordinatedEvmSource(poolAddress);
        var coordinator = new EvmStreamCoordinator(
            client,
            checkpointRoot,
            EvmChainDefinitions.EthereumMainnet,
            httpClient,
            (_, _) => source);
        try
        {
            await client.ReplaceWatchedPoolsAsync("solana:test", [solanaPool]);
            await coordinator.StartAsync(configuration, "fixture-key", [evmPool]);
            await WaitUntilAsync(
                () => coordinator.State == OnChainRecoveryState.Live
                      && prices.Any(update => update.PoolKey.PoolId == poolAddress
                                              && update.ChainPosition?.BlockNumber == 17),
                TimeSpan.FromSeconds(10),
                "Ethereum coordinator live delivery");
            await coordinator.StartAsync(configuration, "fixture-key", [evmPool]);
            await Task.Delay(100);
            AssertEqual(1, source.RunCount,
                "An unchanged Ethereum panel refresh restarted the WebSocket source.");
            AssertEqual(1, source.ObservedSubscription?.Pools.Length ?? 0,
                "The Ethereum coordinator subscribed beyond the selected non-ETH-quoted pool.");
            Assert(handler.GetLogsCount > 0,
                "The Ethereum coordinator did not run its bounded replay query.");
            var websocketRuns = source.RunCount;
            var replayCalls = handler.GetLogsCount;
            var historyReads = handler.NumericBlockRequestCount;
            var finalityReads = handler.FinalizedBlockRequestCount;
            await coordinator.StartAsync(configuration, "fixture-key", [evmPool],
                webSocketPoolIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            await WaitUntilAsync(() => coordinator.State == OnChainRecoveryState.Live,
                TimeSpan.FromSeconds(5), "Ethereum polling coordinator live state");
            var latestReads = handler.LatestBlockRequestCount;
            await WaitUntilAsync(() => handler.LatestBlockRequestCount > latestReads,
                TimeSpan.FromSeconds(3), "Ethereum one-second head poll");
            AssertEqual(websocketRuns, source.RunCount,
                "Polling opened the selected-pool WebSocket source.");
            AssertEqual(replayCalls, handler.GetLogsCount,
                "Polling replayed selected-pool swap logs.");
            AssertEqual(historyReads, handler.NumericBlockRequestCount,
                "Polling bootstrap fetched an event-recovery header window.");
            AssertEqual(finalityReads, handler.FinalizedBlockRequestCount,
                "Polling fetched finality for current-state prices.");
            await WaitUntilAsync(() => handler.BlockNumberRequestCount > 0,
                TimeSpan.FromSeconds(4), "unchanged-head number probe");
            var fullHeaderReads = handler.LatestBlockRequestCount;
            var numberProbes = handler.BlockNumberRequestCount;
            await WaitUntilAsync(() => handler.BlockNumberRequestCount > numberProbes,
                TimeSpan.FromSeconds(3), "second unchanged-head number probe");
            AssertEqual(fullHeaderReads, handler.LatestBlockRequestCount,
                "An unchanged EVM block still spent a full header request each second.");
            await WaitUntilAsync(() => handler.LatestBlockRequestCount > fullHeaderReads,
                TimeSpan.FromSeconds(6), "five-second same-height reorg check");
            var canonicalReads = handler.NumericBlockRequestCount;
            var headersBeforeActivity = handler.LatestBlockRequestCount;
            handler.EnableContiguousLatest();
            await WaitUntilAsync(() => handler.LatestBlockRequestCount >= headersBeforeActivity + 3,
                TimeSpan.FromSeconds(5), "contiguous Ethereum head polls");
            var activeHeadReads = handler.LatestBlockRequestCount;
            var activeNumberProbes = handler.BlockNumberRequestCount;
            await WaitUntilAsync(() => handler.LatestBlockRequestCount > activeHeadReads,
                TimeSpan.FromSeconds(3), "active-chain full header poll");
            AssertEqual(activeNumberProbes, handler.BlockNumberRequestCount,
                "An active chain kept making a redundant block-number probe.");
            AssertEqual(canonicalReads, handler.NumericBlockRequestCount,
                "A directly linked polled head redundantly rechecked its parent by RPC.");
            Assert(handler.SawPinnedCall,
                "The Ethereum coordinator snapshot was not pinned to a canonical block hash.");
            var evmPrice = prices.Last(update => update.PoolKey.PoolId == poolAddress);
            AssertEqual("evm-coordinator-profile", evmPrice.ProviderProfileId,
                "The coordinator did not attach its provider profile to EVM prices.");
            AssertEqual("ethereum-json-rpc:alchemyEthereum", evmPrice.SourceId,
                "The coordinator did not attach its provider source to EVM prices.");
            var checkpoint = await EvmCheckpointStore.LoadAsync(checkpointRoot, configuration.Id);
            AssertEqual(15UL, checkpoint?.SafeBlockNumber ?? 0,
                "The Ethereum coordinator did not persist its safe checkpoint.");

            await client.PublishTransactionUpdateAsync(CreatePumpTrade("shared-solana-1", mintBytes));
            await WaitUntilAsync(
                () => prices.Any(update => update.PoolKey.PoolId == "shared-engine-curve"),
                TimeSpan.FromSeconds(5),
                "simultaneous Solana price");
            await WaitUntilAsync(
                () => GetEngineProcessIds().Except(baselineProcessIds).Count() == 1,
                TimeSpan.FromSeconds(5),
                "single shared Solana/Ethereum engine process");
            var sharedProcessId = GetSingleNewEngineProcessId(baselineProcessIds);
            await coordinator.StopAsync();
            Assert(Process.GetProcessById(sharedProcessId).HasExited == false,
                "Stopping Ethereum terminated the shared engine while Solana was still active.");
            await client.PublishTransactionUpdateAsync(CreatePumpTrade("shared-solana-2", mintBytes));
            await WaitUntilAsync(
                () => prices.Count(update => update.PoolKey.PoolId == "shared-engine-curve") >= 2,
                TimeSpan.FromSeconds(5),
                "Solana delivery after Ethereum stop");
            await client.ReplaceWatchedPoolsAsync("solana:test", []);
        }
        finally
        {
            await coordinator.StopAsync();
            await client.StopAsync();
            if (Directory.Exists(checkpointRoot))
            {
                Directory.Delete(checkpointRoot, true);
            }
        }
        await WaitUntilAsync(
            () => !GetEngineProcessIds().Except(baselineProcessIds).Any(),
            TimeSpan.FromSeconds(5),
            "shared Solana/Ethereum engine stop");
    }
    static async Task VerifyHiddenEvmReferenceSamplingAsync(string enginePath)
    {
        const string selectedToken = "0x1111111111111111111111111111111111111111";
        const string selectedPool = "0x2222222222222222222222222222222222222222";
        foreach (var chain in EvmChainDefinitions.Supported)
        {
            var selected = new OnChainWatchedPoolSelection
            {
                SelectedMint = selectedToken,
                Descriptor = new OnChainPoolDescriptor
                {
                    PoolKey = new OnChainPoolKey
                    {
                        DeploymentKey = new OnChainDeploymentKey
                        {
                            ChainNamespace = chain.ChainNamespace,
                            ChainId = chain.ChainId,
                            ProtocolId = OnChainProtocolIds.UniswapV2,
                            ContractAddress = "0x3333333333333333333333333333333333333333"
                        },
                        PoolId = selectedPool
                    },
                    BaseMint = selectedToken,
                    QuoteMint = chain.WrappedNativeAssetAddress,
                    SupportStatus = OnChainSupportStatus.Supported
                }
            };
            var (watched, hidden) = EvmStreamCoordinator.AddRequiredReferencePools(chain, [selected]);
            AssertEqual(2, watched.Length,
                $"{chain.DisplayName} did not retain the hidden USD reference for snapshots.");
            AssertEqual(chain.NativeUsdReferencePoolId, hidden?.Descriptor.PoolKey.PoolId,
                $"{chain.DisplayName} selected the wrong hidden USD reference.");
            var explicitReference = chain.CreateNativeUsdReferenceSelection!();
            var (explicitWatched, explicitHidden) = EvmStreamCoordinator.AddRequiredReferencePools(
                chain, [selected, explicitReference]);
            AssertEqual(2, explicitWatched.Length,
                $"{chain.DisplayName} duplicated an explicitly selected reference pool.");
            Assert(explicitHidden == null,
                $"{chain.DisplayName} would suppress a user-selected reference stream.");
            selected.Descriptor.QuoteMint = "0x4444444444444444444444444444444444444444";
            var (stableWatched, stableHidden) = EvmStreamCoordinator.AddRequiredReferencePools(
                chain, [selected]);
            AssertEqual(1, stableWatched.Length,
                $"{chain.DisplayName} added a reference without a native-quoted pool.");
            Assert(stableHidden == null,
                $"{chain.DisplayName} would sample an unused USD reference.");
        }

        var root = Path.Combine(Path.GetTempPath(), "TrenchHQHiddenReference", Guid.NewGuid().ToString("N"));
        var baselineProcessIds = GetEngineProcessIds();
        var client = new OnChainEngineClient(enginePath);
        var prices = new ConcurrentQueue<OnChainPriceUpdate>();
        client.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
        var pool = new OnChainWatchedPoolSelection
        {
            SelectedMint = selectedToken,
            Descriptor = new OnChainPoolDescriptor
            {
                PoolKey = new OnChainPoolKey
                {
                    DeploymentKey = EthereumDeploymentRegistry.UniswapV2Deployment(),
                    PoolId = selectedPool
                },
                PoolType = "constantProduct",
                ProgramId = EthereumDeploymentRegistry.UniswapV2Factory,
                BaseMint = selectedToken,
                QuoteMint = EthereumDeploymentRegistry.WrappedEther,
                BaseDecimals = 18,
                QuoteDecimals = 18,
                PairOrientation = "selectedAsToken0",
                SupportStatus = OnChainSupportStatus.Supported,
                Asset0 = new OnChainAssetKey
                {
                    ChainNamespace = ChainNamespaces.Eip155,
                    ChainId = EvmChainDefinitions.EthereumMainnetChainId,
                    Address = selectedToken
                },
                Asset1 = new OnChainAssetKey
                {
                    ChainNamespace = ChainNamespaces.Eip155,
                    ChainId = EvmChainDefinitions.EthereumMainnetChainId,
                    Address = EthereumDeploymentRegistry.WrappedEther
                }
            }
        };
        var configuration = CreateProviderConfiguration(
            OnChainProviderTypes.PublicNodeEthereum,
            "wss://fixture.invalid",
            "https://fixture.invalid");
        configuration.Id = "hidden-reference-sampling-profile";
        configuration.CapabilitySnapshot = new OnChainProviderCapabilitySnapshot
        {
            SafeBlock = OnChainProviderCapabilityState.Supported,
            FinalizedBlock = OnChainProviderCapabilityState.Supported
        };
        using var handler = new EvmCoordinatorRpcFixtureHandler(
            selectedPool, advanceLatest: true,
            referenceAddress: EthereumDeploymentRegistry.WrappedEtherUsdcReferencePool);
        using var httpClient = new HttpClient(handler);
        var source = new CoordinatedEvmSource(
            selectedPool, EvmChainDefinitions.EthereumMainnetChainId, emitHead: false);
        var coordinator = new EvmStreamCoordinator(
            client, root, EvmChainDefinitions.EthereumMainnet,
            httpClient, (_, _) => source,
            headSampleInterval: TimeSpan.FromMilliseconds(50),
            sampledSnapshotInterval: TimeSpan.FromHours(1),
            referenceSampleInterval: TimeSpan.FromMilliseconds(100));
        try
        {
            await coordinator.StartAsync(configuration, null, [pool]);
            await WaitUntilAsync(
                () => coordinator.State == OnChainRecoveryState.Live
                      && prices.Any(update => update.PoolKey.PoolId == selectedPool
                                              && update.PriceUsd != null),
                TimeSpan.FromSeconds(10),
                "hidden reference bootstrap conversion");
            AssertEqual(1, source.ObservedSubscription?.Pools.Length ?? 0,
                "The hidden reference was included in the paid WebSocket subscription.");
            AssertEqual(selectedPool, source.ObservedSubscription?.Pools[0].Descriptor.PoolKey.PoolId,
                "The selected pool was removed from the live WebSocket subscription.");
            Assert(handler.GetLogsCount > 0,
                "The hidden-reference test did not exercise RPC log replay.");
            Assert(handler.LogFilters.All(filter => !filter.Contains(
                       EthereumDeploymentRegistry.WrappedEtherUsdcReferencePool,
                       StringComparison.OrdinalIgnoreCase)),
                "The hidden reference was included in RPC log replay.");
            var bootstrapReferenceCalls = handler.ReferenceCallCount;
            await WaitUntilAsync(
                () => handler.ReferenceCallCount >= bootstrapReferenceCalls + 2,
                TimeSpan.FromSeconds(5),
                "periodic pinned hidden-reference snapshot");
            Assert(handler.SawPinnedCall,
                "The hidden reference snapshot was not pinned to a canonical block.");
            Assert(handler.MulticallRequestCount >= 2 && handler.DirectReferenceCallCount == 0,
                "The hidden reference still spends two separate RPC calls per snapshot.");
            AssertEqual(0, handler.DirectSelectedCallCount,
                "The selected pool was fetched separately from its hidden reference.");
            Assert(prices.Any(update => update.PoolKey.PoolId == selectedPool
                                        && update.ReferenceSourceId?.StartsWith(
                                            "ethereum:uniswap-v3:WETH/USDC:500:",
                                            StringComparison.Ordinal) == true),
                "A sampled hidden reference lost on-chain price provenance.");
            await coordinator.StartAsync(configuration, null,
                [pool, EthereumDeploymentRegistry.CreateEthUsdReferenceSelection()]);
            await WaitUntilAsync(
                () => source.RunCount == 2 && coordinator.State == OnChainRecoveryState.Live,
                TimeSpan.FromSeconds(10),
                "explicit reference restored live subscription");
            AssertEqual(2, source.ObservedSubscription?.Pools.Length ?? 0,
                "Changing from hidden to explicitly selected reference did not restart the stream.");
            Assert(source.ObservedSubscription?.Pools.Any(selection => string.Equals(
                       selection.Descriptor.PoolKey.PoolId,
                       EthereumDeploymentRegistry.WrappedEtherUsdcReferencePool,
                       StringComparison.OrdinalIgnoreCase)) == true,
                "An explicitly selected reference pool was not included in the live stream.");
            // Drain the previous stream before measuring polling-only requests.
            await coordinator.StopAsync();
            var numericReads = handler.NumericBlockRequestCount;
            var finalityReads = handler.FinalizedBlockRequestCount;
            var replayReads = handler.GetLogsCount;
            await coordinator.StartAsync(configuration, null, [pool],
                webSocketPoolIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            await WaitUntilAsync(() => coordinator.State == OnChainRecoveryState.Live,
                TimeSpan.FromSeconds(5), "fast-chain polling bootstrap");
            var selectedReads = handler.DirectSelectedCallCount;
            var batchReads = handler.MulticallRequestCount;
            await WaitUntilAsync(() => handler.MulticallRequestCount >= batchReads + 2,
                TimeSpan.FromSeconds(5), "fast-chain selected and reference polling batches");
            AssertEqual(selectedReads, handler.DirectSelectedCallCount,
                "A due reference used a separate RPC from selected state.");
            AssertEqual(numericReads, handler.NumericBlockRequestCount,
                "Polling skipped blocks triggered historical canonical-tip reads.");
            AssertEqual(finalityReads, handler.FinalizedBlockRequestCount,
                "Polling used event finality requests.");
            AssertEqual(replayReads, handler.GetLogsCount, "Polling performed event replay.");
            Console.WriteLine("POLL REQUESTS | fast chain | each due selected+reference sample=1 latest header + 1 Multicall | history=0 | finality=0 | replay=0");
        }
        finally
        {
            await coordinator.StopAsync();
            await client.StopAsync();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
        await WaitUntilAsync(
            () => !GetEngineProcessIds().Except(baselineProcessIds).Any(),
            TimeSpan.FromSeconds(5),
            "hidden-reference engine stop");
    }
    static void VerifyEvmMulticall3Codec()
    {
        const string target = "0x1111111111111111111111111111111111111111";
        var call = EvmMulticall3.EncodePair(
            target, EthereumAbi.Slot0Selector, EthereumAbi.LiquiditySelector);
        Assert(call.StartsWith("0xbce38bd7", StringComparison.Ordinal)
               && call.Split(target[2..], StringSplitOptions.None).Length == 3,
            "The paired state call did not encode both calls to the selected contract.");
        var v4First = EthereumAbi.UniswapV4StateViewSlot0Selector + new string('a', 64);
        var v4Second = EthereumAbi.UniswapV4StateViewLiquiditySelector + new string('a', 64);
        var v4Call = EvmMulticall3.EncodePair(target, v4First, v4Second);
        Assert(v4Call.Contains(v4First[2..], StringComparison.Ordinal)
               && v4Call.Contains(v4Second[2..], StringComparison.Ordinal)
               && v4Call.Substring(10 + 64 * 4, 64).EndsWith("e0", StringComparison.Ordinal),
            "The paired V4 state call lost its bytes32 pool ID or padded call offset.");

        var first = "0x" + new string('a', 448);
        var second = "0x" + new string('b', 64);
        var response = EvmCoordinatorRpcFixtureHandler.EncodePairResult(first, second);
        Assert(EvmMulticall3.TryDecodePair(response, out var decodedFirst, out var decodedSecond)
               && decodedFirst == first && decodedSecond == second,
            "A valid paired state response lost a result or its order.");
        Assert(!EvmMulticall3.TryDecodePair(ReplaceWord(response, 128, 0), out _, out _),
            "A failed first subcall was accepted as a snapshot.");
        Assert(!EvmMulticall3.TryDecodePair(ReplaceWord(response, 128 + 96 + 224, 0), out _, out _),
            "A failed second subcall was accepted as a snapshot.");
        Assert(!EvmMulticall3.TryDecodePair(ReplaceWord(response, 32, 3), out _, out _),
            "A malformed paired result count was accepted.");
        Assert(!EvmMulticall3.TryDecodePair(ReplaceWord(response, 64, 4096), out _, out _),
            "An out-of-range paired result offset was accepted.");
        Assert(!EvmMulticall3.TryDecodePair(ReplaceWord(response, 96, 64), out _, out _),
            "Overlapping paired results were accepted.");
        Assert(!EvmMulticall3.TryDecodePair(response[..^64], out _, out _),
            "A truncated paired state response was accepted.");
        Assert(!EvmMulticall3.TryDecodePair("0xnot-hex", out _, out _),
            "A non-hex paired state response was accepted.");

        var threeCalls = new (string Target, string Data)[]
        {
            (target, EthereumAbi.GetReservesSelector),
            (target, EthereumAbi.Slot0Selector),
            (target, EthereumAbi.LiquiditySelector)
        };
        var grouped = EvmMulticall3.EncodeCalls(threeCalls);
        Assert(grouped.StartsWith("0xbce38bd7", StringComparison.Ordinal)
               && grouped.Split(target[2..], StringSplitOptions.None).Length == 4,
            "A three-call aggregate omitted or reordered a pool read.");
        var threeResults = EvmCoordinatorRpcFixtureHandler.EncodeResults(first, second, first);
        Assert(EvmMulticall3.TryDecodeResults(threeResults, 3, out var decoded)
               && decoded.SequenceEqual(new[] { first, second, first }),
            "A mixed three-call aggregate lost state or ordering.");
        Assert(!EvmMulticall3.TryDecodeResults(threeResults, 2, out _)
               && !EvmMulticall3.TryDecodeResults(ReplaceWord(threeResults, 64, 32), 3, out _)
               && !EvmMulticall3.TryDecodeResults(ReplaceWord(threeResults, 160, 0), 3, out _)
               && !EvmMulticall3.TryDecodeResults(threeResults[..^64], 3, out _),
            "A count mismatch, overlapping offset, failed subcall, or truncated batch was accepted.");
        var oversizedRejected = false;
        try
        {
            EvmMulticall3.EncodeCalls(Enumerable.Repeat(threeCalls[0], EvmMulticall3.MaximumCalls + 1).ToArray());
        }
        catch (ArgumentException)
        {
            oversizedRejected = true;
        }
        Assert(oversizedRejected, "An unbounded EVM aggregate was accepted.");

        static string ReplaceWord(string data, int byteOffset, int value) =>
            data[..(2 + byteOffset * 2)]
            + value.ToString("x64")
            + data[(2 + (byteOffset + 32) * 2)..];
    }
    static async Task VerifyRobinhoodSampledHeadCoordinatorAsync(string enginePath)
    {
        const string selectedToken = "0x1111111111111111111111111111111111111111";
        const string poolAddress = "0x2222222222222222222222222222222222222222";
        var checkpointRoot = Path.Combine(
            Path.GetTempPath(),
            "TrenchHQRobinhoodSampledHeadCheckpoint",
            Guid.NewGuid().ToString("N"));
        var baselineProcessIds = GetEngineProcessIds();
        var client = new OnChainEngineClient(enginePath);
        var prices = new ConcurrentQueue<OnChainPriceUpdate>();
        client.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
        var pool = new OnChainWatchedPoolSelection
        {
            SelectedMint = selectedToken,
            Descriptor = new OnChainPoolDescriptor
            {
                PoolKey = new OnChainPoolKey
                {
                    DeploymentKey = RobinhoodDeploymentRegistry.Catalog.Deployment(
                        OnChainProtocolIds.UniswapV2,
                        RobinhoodDeploymentRegistry.UniswapV2Factory),
                    PoolId = poolAddress
                },
                PoolType = "constantProduct",
                ProgramId = RobinhoodDeploymentRegistry.UniswapV2Factory,
                BaseMint = selectedToken,
                QuoteMint = RobinhoodDeploymentRegistry.Usdg,
                BaseDecimals = 18,
                QuoteDecimals = 18,
                PairOrientation = "selectedAsToken0",
                SupportStatus = OnChainSupportStatus.Supported,
                Asset0 = new OnChainAssetKey
                {
                    ChainNamespace = ChainNamespaces.Eip155,
                    ChainId = EvmChainDefinitions.RobinhoodMainnetChainId,
                    Address = selectedToken
                },
                Asset1 = new OnChainAssetKey
                {
                    ChainNamespace = ChainNamespaces.Eip155,
                    ChainId = EvmChainDefinitions.RobinhoodMainnetChainId,
                    Address = RobinhoodDeploymentRegistry.Usdg
                }
            }
        };
        var configuration = CreateProviderConfiguration(
            OnChainProviderTypes.CustomRobinhood,
            "wss://fixture.invalid",
            "https://fixture.invalid");
        configuration.Id = "robinhood-sampled-head-profile";
        configuration.CapabilitySnapshot = new OnChainProviderCapabilitySnapshot
        {
            SafeBlock = OnChainProviderCapabilityState.Supported,
            FinalizedBlock = OnChainProviderCapabilityState.Supported
        };
        using var handler = new EvmCoordinatorRpcFixtureHandler(
            poolAddress, advanceLatest: true, inconsistentFinality: true);
        using var httpClient = new HttpClient(handler);
        var source = new CoordinatedEvmSource(
            poolAddress,
            EvmChainDefinitions.RobinhoodMainnetChainId,
            emitHead: false);
        var coordinator = new EvmStreamCoordinator(
            client,
            checkpointRoot,
            EvmChainDefinitions.RobinhoodMainnet,
            httpClient,
            (_, _) => source,
            headSampleInterval: TimeSpan.FromMilliseconds(50),
            sampledSnapshotInterval: TimeSpan.FromMilliseconds(150));
        try
        {
            await coordinator.StartAsync(configuration, null, [pool]);
            await WaitUntilAsync(
                () => coordinator.State == OnChainRecoveryState.Live
                      && prices.Any(update => update.PoolKey.PoolId == poolAddress
                                              && update.ChainPosition?.BlockNumber == 17),
                TimeSpan.FromSeconds(10),
                "Robinhood log-only live delivery");
            var bootstrapLogRequests = handler.GetLogsCount;
            var bootstrapPinnedCalls = handler.PinnedCallCount;
            await WaitUntilAsync(
                () => handler.LatestBlockRequestCount >= 4
                      && handler.PinnedCallCount > bootstrapPinnedCalls,
                TimeSpan.FromSeconds(5),
                "Robinhood sampled canonical heads and periodic pinned snapshot");
            AssertEqual(1, source.RunCount,
                "Robinhood sampled heads restarted the log-only WebSocket source.");
            AssertEqual(bootstrapLogRequests, handler.GetLogsCount,
                "A sampled Robinhood head replayed every skipped high-frequency block.");
            Assert(handler.SawPinnedCall,
                "Robinhood sampled-head processing did not retain canonical pinned snapshots.");
            Assert(handler.FinalizedBlockRequestCount > 0
                   && coordinator.State == OnChainRecoveryState.Live,
                "Inconsistent public finality restarted an otherwise live price stream.");
            Assert(await EvmCheckpointStore.LoadAsync(checkpointRoot, configuration.Id) == null,
                "An inconsistent provider finality sample became a safe checkpoint.");
        }
        finally
        {
            await coordinator.StopAsync();
            await client.StopAsync();
            if (Directory.Exists(checkpointRoot))
            {
                Directory.Delete(checkpointRoot, true);
            }
        }
        await WaitUntilAsync(
            () => !GetEngineProcessIds().Except(baselineProcessIds).Any(),
            TimeSpan.FromSeconds(5),
            "Robinhood sampled-head engine stop");
    }
}

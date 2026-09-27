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
using TrenchHQ.TestSupport;
using static TrenchHQ.TestSupport.OnChainFixtures;

namespace TrenchHQ.OnChainValidation;

internal static partial class Validation
{
    static async Task VerifyLiveEvmStreamsAsync(string enginePath)
    {
        const string selectedMint = "0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2";
        var expectedPools = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [OnChainProtocolIds.UniswapV2] = "0xb4e16d0168e52d35cacd2c6185b44281ec28c9dc",
            [OnChainProtocolIds.UniswapV3] = "0x88e6a0c2ddd26feeb64f039a2c41296fcb3f5640"
        };
        using var catalogHttp = new HttpClient
        {
            BaseAddress = new Uri("https://api.dexscreener.com/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        using var rpcHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var discoveryConfiguration = CreateProviderConfiguration(
            OnChainProviderTypes.CustomEthereum,
            "wss://eth-mainnet.public.blastapi.io",
            "https://eth-mainnet.public.blastapi.io");
        var discoveryRpc = new EvmJsonRpcClient(rpcHttp, discoveryConfiguration, null);
        var catalog = await new DexScreenerPoolCatalogClient(catalogHttp)
            .SearchAsync("ethereum", selectedMint);
        var selectedCatalog = catalog.Pools
            .Where(pool => expectedPools.Values.Contains(
                               pool.PoolAddress,
                               StringComparer.OrdinalIgnoreCase)
                           || pool.ProtocolId.Equals("uniswap", StringComparison.OrdinalIgnoreCase)
                           && pool.Labels.Contains("v4", StringComparer.OrdinalIgnoreCase))
            .ToArray();
        Assert(expectedPools.Values.All(poolId => selectedCatalog.Any(pool =>
                pool.PoolAddress.Equals(poolId, StringComparison.OrdinalIgnoreCase))),
            "The live catalog did not return both canonical V2/V3 smoke pools.");
        var discoveryService = new EthereumPoolDiscoveryService(discoveryRpc, rpcHttp);
        var discovery = await RetryLiveTransientResultAsync(
            async () =>
            {
                var candidate = await discoveryService.DiscoverCatalogPoolsSharedAsync(
                    selectedMint,
                    selectedCatalog,
                    CancellationToken.None);
                if (candidate.Pools.Any(pool =>
                        pool.SupportReason?.Contains("HTTP 5", StringComparison.Ordinal) == true))
                {
                    throw new EvmJsonRpcException(
                        EvmRpcFailureKind.RpcError,
                        "The key-free Ethereum endpoint returned a transient server failure.");
                }
                return candidate;
            },
            "key-free Ethereum live pool discovery");
        foreach (var catalogPool in selectedCatalog.Where(pool =>
                     pool.Labels.Contains("v4", StringComparer.OrdinalIgnoreCase)))
        {
            var descriptor = discovery.Pools.FirstOrDefault(pool =>
                pool.PoolKey.PoolId.Equals(catalogPool.PoolAddress, StringComparison.OrdinalIgnoreCase));
            Console.WriteLine(
                $"LIVE V4 CANDIDATE | {catalogPool.PoolAddress} | liquidityUsd={catalogPool.LiquidityUsd} | {descriptor?.SupportStatus.ToString() ?? "missing"} | hook={descriptor?.HookAddress ?? "unknown"} | {descriptor?.SupportReason ?? "validated"}");
        }
        var supportedV4Pool = selectedCatalog
            .Where(pool => pool.Labels.Contains("v4", StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(static pool => pool.LiquidityUsd ?? double.MinValue)
            .Select(catalogPool => discovery.Pools.FirstOrDefault(pool =>
                pool.PoolKey.ProtocolId == OnChainProtocolIds.UniswapV4
                && pool.PoolKey.PoolId.Equals(
                    catalogPool.PoolAddress,
                    StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(static pool => pool?.SupportStatus == OnChainSupportStatus.Supported);
        Assert(supportedV4Pool != null,
            "The live WETH catalog did not contain a supported Uniswap V4 pool.");
        expectedPools[OnChainProtocolIds.UniswapV4] = supportedV4Pool!.PoolKey.PoolId;
        var selections = expectedPools
            .Select(expected => new OnChainWatchedPoolSelection
            {
                SelectedMint = selectedMint,
                Descriptor = discovery.Pools.Single(pool =>
                    pool.PoolKey.ProtocolId == expected.Key
                    && pool.PoolKey.PoolId.Equals(expected.Value, StringComparison.OrdinalIgnoreCase))
            })
            .ToArray();
        foreach (var selection in selections)
        {
            Console.WriteLine(
                $"LIVE SELECT | {selection.Descriptor.PoolKey.ProtocolId} | {selection.Descriptor.PoolKey.PoolId} | {selection.Descriptor.SupportStatus} | hook={selection.Descriptor.HookAddress ?? "n/a"} | {selection.Descriptor.SupportReason ?? "validated"}");
        }
        Assert(selections.All(selection =>
                selection.Descriptor.SupportStatus == OnChainSupportStatus.Supported),
            "At least one live V2/V3/V4 smoke pool failed on-chain validation.");

        var configuration = CreateProviderConfiguration(
            OnChainProviderTypes.PublicNodeEthereum,
            "wss://ethereum-rpc.publicnode.com",
            "https://ethereum-rpc.publicnode.com");
        configuration.Id = "live-publicnode-ethereum";
        configuration.CapabilitySnapshot = new OnChainProviderCapabilitySnapshot
        {
            ObservedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ChainId = OnChainProviderCapabilityState.Supported,
            SafeBlock = OnChainProviderCapabilityState.Supported,
            FinalizedBlock = OnChainProviderCapabilityState.Supported,
            BlockHashCall = OnChainProviderCapabilityState.Supported,
            BlockHashLogs = OnChainProviderCapabilityState.Supported,
            WebSocketHeads = OnChainProviderCapabilityState.Supported,
            WebSocketLogs = OnChainProviderCapabilityState.Supported
        };
        var rpc = new EvmJsonRpcClient(rpcHttp, configuration, null);
        var activeStreamProtocols = new HashSet<string>(StringComparer.Ordinal)
        {
            OnChainProtocolIds.UniswapV2,
            OnChainProtocolIds.UniswapV3
        };

        var checkpointRoot = Path.Combine(
            Path.GetTempPath(),
            "TrenchHQLiveEvmCheckpoint",
            Guid.NewGuid().ToString("N"));
        var baselineProcessIds = GetEngineProcessIds();
        var prices = new ConcurrentQueue<OnChainPriceUpdate>();
        var sourceLogs = new ConcurrentQueue<EvmLogUpdate>();
        var sourceRunCount = 0;
        var client = new OnChainEngineClient(enginePath);
        client.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
        var coordinator = new EvmStreamCoordinator(
            client,
            checkpointRoot,
            EvmChainDefinitions.EthereumMainnet,
            rpcHttp,
            (profile, apiKey) => new ObservingEvmStreamSource(
                new EvmWebSocketStreamSource(profile, apiKey),
                sourceLogs,
                () => Interlocked.Increment(ref sourceRunCount)));
        try
        {
            var streamStartedAfter = (await rpc.GetBlockAsync("latest", CancellationToken.None)).Number;
            await coordinator.StartAsync(configuration, null, selections);
            await WaitUntilAsync(
                () => coordinator.State == OnChainRecoveryState.Live
                      && expectedPools.Where(expected => activeStreamProtocols.Contains(expected.Key)).All(expected =>
                          sourceLogs.Any(log => LiveLogMatchesPool(log, expected.Key, expected.Value)
                                                && log.BlockNumber > streamStartedAfter)
                          && prices.Any(update =>
                              update.PoolKey.ProtocolId == expected.Key
                              && update.PoolKey.PoolId.Equals(
                                  expected.Value,
                                  StringComparison.OrdinalIgnoreCase)
                              && update.ChainPosition?.BlockNumber > streamStartedAfter
                              && update.LastTradeEventId != null)),
                TimeSpan.FromMinutes(4),
                "natural PublicNode V2/V3 WebSocket prices while V4 remains subscribed");
            await WaitUntilAsync(
                () => GetEngineProcessIds().Except(baselineProcessIds).Count() == 1,
                TimeSpan.FromSeconds(5),
                "one shared engine during the live EVM smoke");
            var firstEngineProcessId = GetSingleNewEngineProcessId(baselineProcessIds);
            foreach (var expected in expectedPools.Where(expected => activeStreamProtocols.Contains(expected.Key)))
            {
                var update = prices.Last(item =>
                    item.PoolKey.ProtocolId == expected.Key
                    && item.PoolKey.PoolId.Equals(expected.Value, StringComparison.OrdinalIgnoreCase)
                    && item.ChainPosition?.BlockNumber > streamStartedAfter
                    && item.LastTradeEventId != null);
                Console.WriteLine(
                    $"LIVE STREAM | {expected.Key} | block={update.ChainPosition?.BlockNumber} | event={update.LastTradeEventId} | source={update.SourceId}");
            }
            var checkpoint = await EvmCheckpointStore.LoadAsync(checkpointRoot, configuration.Id);
            Assert(checkpoint?.SafeBlockNumber > 0,
                "The live EVM coordinator did not persist a safe checkpoint.");

            await coordinator.StopAsync();
            await WaitUntilAsync(
                () => !GetEngineProcessIds().Except(baselineProcessIds).Any(),
                TimeSpan.FromSeconds(10),
                "live EVM engine shutdown before restart");
            var restartStartedAfter = (await rpc.GetBlockAsync("latest", CancellationToken.None)).Number;
            await coordinator.StartAsync(configuration, null, selections);
            await WaitUntilAsync(
                () => coordinator.State == OnChainRecoveryState.Live
                      && Volatile.Read(ref sourceRunCount) >= 2
                      && prices.Any(update =>
                          update.ChainPosition?.BlockNumber > restartStartedAfter
                          && update.LastTradeEventId != null),
                TimeSpan.FromMinutes(3),
                "live EVM checkpoint restart and post-restart WebSocket price");
            var secondEngineProcessId = GetSingleNewEngineProcessId(baselineProcessIds);
            Assert(firstEngineProcessId != secondEngineProcessId,
                "The stopped EVM engine process was reused during restart validation.");
            Console.WriteLine(
                $"LIVE RESTART | safe={checkpoint!.SafeBlockNumber} | engine={firstEngineProcessId}->{secondEngineProcessId} | sourceRuns={sourceRunCount}");
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
            TimeSpan.FromSeconds(10),
            "live EVM engine cleanup");
    }
}

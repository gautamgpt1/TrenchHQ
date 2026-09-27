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
    static async Task VerifyPipelineBenchmarksAsync(string enginePath)
    {
        foreach (var poolCount in new[] { 1, 10, 100 })
        {
            var updatesPerPool = poolCount == 100 ? 20 : 100;
            var expectedUpdates = poolCount * updatesPerPool;
            OnChainPipelineDiagnostics.Reset();
            var existingProcessIds = GetEngineProcessIds();
            var client = new OnChainEngineClient(enginePath);
            var delivered = 0;
            client.PriceUpdated += (_, args) =>
            {
                OnChainPipelineDiagnostics.RecordUiRender(args.Update.ObservedAtUnixMs);
                Interlocked.Increment(ref delivered);
            };
            try
            {
                var selections = new OnChainWatchedPoolSelection[poolCount];
                var mints = new byte[poolCount][];
                for (var index = 0; index < poolCount; index++)
                {
                    var mintBytes = new byte[32];
                    BitConverter.TryWriteBytes(mintBytes.AsSpan(0, sizeof(int)), index + 1000);
                    mintBytes[31] = 7;
                    mints[index] = mintBytes;
                    var mint = EncodeBase58(mintBytes);
                    selections[index] = new OnChainWatchedPoolSelection
                    {
                        SelectedMint = mint,
                        Descriptor = new OnChainPoolDescriptor
                        {
                            PoolKey = new OnChainPoolKey
                            {
                                ProtocolId = OnChainProtocolIds.PumpBondingCurve,
                                PoolAddress = $"benchmark-curve-{index}"
                            },
                            PoolType = "bondingCurve",
                            ProgramId = "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P",
                            BaseMint = mint,
                            QuoteMint = "So11111111111111111111111111111111111111112",
                            BaseDecimals = 6,
                            QuoteDecimals = 9,
                            SupportStatus = OnChainSupportStatus.Supported
                        }
                    };
                }

                await client.ReplaceWatchedPoolsAsync(selections);
                var engineProcess = Process.GetProcessById(GetSingleNewEngineProcessId(existingProcessIds));
                using (engineProcess)
                using (var appProcess = Process.GetCurrentProcess())
                {
                    appProcess.Refresh();
                    engineProcess.Refresh();
                    var cpuBefore = appProcess.TotalProcessorTime + engineProcess.TotalProcessorTime;
                    var stopwatch = Stopwatch.StartNew();
                    await Task.WhenAll(mints.SelectMany((mint, poolIndex) =>
                        Enumerable.Range(0, updatesPerPool).Select(updateIndex =>
                            client.PublishTransactionUpdateAsync(CreatePumpTrade(
                                $"benchmark-{poolCount}-{poolIndex}-{updateIndex}",
                                mint)))));
                    await WaitUntilAsync(
                        () => Volatile.Read(ref delivered) == expectedUpdates,
                        TimeSpan.FromSeconds(20),
                        $"{poolCount}-pool benchmark delivery");
                    stopwatch.Stop();
                    appProcess.Refresh();
                    engineProcess.Refresh();
                    var cpuUsed = appProcess.TotalProcessorTime + engineProcess.TotalProcessorTime - cpuBefore;
                    var workingSetMb = (appProcess.WorkingSet64 + engineProcess.WorkingSet64) / 1024d / 1024d;
                    var diagnostics = OnChainPipelineDiagnostics.Snapshot();
                    AssertEqual((long)expectedUpdates, diagnostics.SourceUpdates,
                        $"The {poolCount}-pool benchmark lost source updates.");
                    AssertEqual((long)expectedUpdates, diagnostics.EnginePriceUpdates,
                        $"The {poolCount}-pool benchmark lost engine prices.");
                    AssertEqual((long)expectedUpdates, diagnostics.UiRenders,
                        $"The {poolCount}-pool benchmark lost delivery-boundary updates.");
                    Assert(!double.IsInfinity(diagnostics.EngineLatencyP95Ms)
                           && diagnostics.EngineLatencyP95Ms <= 500,
                        $"The {poolCount}-pool engine p95 exceeded 500 ms.");
                    Console.WriteLine(
                        $"BENCH | pools={poolCount} | updates={expectedUpdates}"
                        + $" | elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F1}"
                        + $" | updatesPerSecond={expectedUpdates / stopwatch.Elapsed.TotalSeconds:F0}"
                        + $" | engineP50Ms={diagnostics.EngineLatencyP50Ms:F0}"
                        + $" | engineP95Ms={diagnostics.EngineLatencyP95Ms:F0}"
                        + $" | deliveryP95Ms={diagnostics.RenderLatencyP95Ms:F0}"
                        + $" | cpuMs={cpuUsed.TotalMilliseconds:F1} | combinedWorkingSetMb={workingSetMb:F1}");
                }
            }
            finally
            {
                await client.StopAsync();
            }
        }
    }
    static async Task VerifyMixedChainWatchSetMatrixAsync(string enginePath)
    {
        var solanaMintBytes = Enumerable.Repeat((byte)37, 32).ToArray();
        var solana = CreateMixedMatrixSolanaSelection(solanaMintBytes);
        const string stockQuote = "0x3333333333333333333333333333333333333333";
        var pools = new Dictionary<string, (EvmChainDefinition Chain, OnChainWatchedPoolSelection Selection, int QuoteDecimals)>
        {
            ["ETH/USDC"] = (EvmChainDefinitions.EthereumMainnet, CreatePool(
                EvmChainDefinitions.EthereumMainnet, OnChainProtocolIds.UniswapV2,
                EthereumDeploymentRegistry.UniswapV2Factory, EthereumDeploymentRegistry.Usdc,
                "0x0000000000000000000000000000000000000101"), 6),
            ["BASE/USDC"] = (EvmChainDefinitions.BaseMainnet, CreatePool(
                EvmChainDefinitions.BaseMainnet, OnChainProtocolIds.UniswapV2,
                BaseDeploymentRegistry.UniswapV2Factory, BaseDeploymentRegistry.Usdc,
                "0x0000000000000000000000000000000000000102"), 6),
            ["BNB/USDT"] = (EvmChainDefinitions.BnbMainnet, CreatePool(
                EvmChainDefinitions.BnbMainnet, OnChainProtocolIds.PancakeV2,
                BnbDeploymentRegistry.PancakeV2Factory, BnbDeploymentRegistry.Usdt,
                "0x0000000000000000000000000000000000000103"), 18),
            ["RH-STOCK/USDG"] = (EvmChainDefinitions.RobinhoodMainnet, CreatePool(
                EvmChainDefinitions.RobinhoodMainnet, OnChainProtocolIds.UniswapV2,
                RobinhoodDeploymentRegistry.UniswapV2Factory, RobinhoodDeploymentRegistry.Usdg,
                "0x0000000000000000000000000000000000000104"), 18),
            ["RH-STOCK/STOCK"] = (EvmChainDefinitions.RobinhoodMainnet, CreatePool(
                EvmChainDefinitions.RobinhoodMainnet, OnChainProtocolIds.UniswapV2,
                RobinhoodDeploymentRegistry.UniswapV2Factory, stockQuote,
                "0x0000000000000000000000000000000000000105"), 18)
        };
        var stages = new (string Name, string[] Names)[]
        {
            ("2", ["SOL", "RH-STOCK/USDG"]),
            ("3", ["SOL", "ETH/USDC", "BNB/USDT"]),
            ("5-usd", ["SOL", "ETH/USDC", "BASE/USDC", "BNB/USDT", "RH-STOCK/USDG"]),
            ("5-stock", ["SOL", "ETH/USDC", "BASE/USDC", "RH-STOCK/USDG", "RH-STOCK/STOCK"])
        };
        var baselineProcessIds = GetEngineProcessIds();
        var client = new OnChainEngineClient(enginePath);
        var prices = new ConcurrentQueue<OnChainPriceUpdate>();
        client.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
        try
        {
            int? sharedProcessId = null;
            for (var stageIndex = 0; stageIndex < stages.Length; stageIndex++)
            {
                var (stageName, names) = stages[stageIndex];
                foreach (var chain in EvmChainDefinitions.Supported)
                {
                    var selectedOnChain = names.Where(pools.ContainsKey)
                        .Select(name => pools[name])
                        .Where(pool => pool.Chain.ChainId == chain.ChainId)
                        .Select(pool => pool.Selection).ToArray();
                    var filters = JsonSerializer.Serialize(
                        EvmWebSocketStreamSource.CreateRpcLogFilters(selectedOnChain, 1, 2));
                    Assert(pools.Values.Where(pool => pool.Chain.ChainId == chain.ChainId)
                            .All(pool => filters.Contains(pool.Selection.Descriptor.PoolKey.PoolId,
                                             StringComparison.OrdinalIgnoreCase)
                                         == selectedOnChain.Contains(pool.Selection)),
                        $"The {stageName}-coin {chain.DisplayName} subscription includes a deselected pool or omits a selected pool.");
                    await client.ReplaceWatchedPoolsAsync(chain.EnginePoolOwner, selectedOnChain);
                    if (selectedOnChain.Length > 0)
                    {
                        await client.SetEvmProviderContextAsync(chain.ChainId,
                            $"matrix-{chain.ChainId}", $"fixture:{chain.ChainId}");
                    }
                }
                await client.ReplaceWatchedPoolsAsync("solana:mainnet-beta", [solana]);
                var processId = GetSingleNewEngineProcessId(baselineProcessIds);
                sharedProcessId ??= processId;
                AssertEqual(sharedProcessId.Value, processId,
                    $"The {stageName}-coin change restarted the shared engine.");

                var before = prices.Count;
                await client.PublishTransactionUpdateAsync(CreatePumpTrade($"matrix-{stageName}", solanaMintBytes));
                foreach (var (name, pool) in pools)
                {
                    if (!names.Contains(name, StringComparer.Ordinal))
                    {
                        continue;
                    }
                    await client.PublishEvmSnapshotAsync(new EvmSnapshotResponse
                    {
                        RequestId = $"matrix-{stageName}-{name}",
                        PoolKey = pool.Selection.Descriptor.PoolKey,
                        BlockNumber = (ulong)(20 + stageIndex),
                        BlockHash = Hash((ulong)(20 + stageIndex)),
                        Calls =
                        [
                            new EvmSnapshotCall
                            {
                                Id = "getReserves",
                                Success = true,
                                ReturnData = AbiBigWords(
                                    System.Numerics.BigInteger.Pow(10, 18),
                                    2 * System.Numerics.BigInteger.Pow(10, pool.QuoteDecimals),
                                    123)
                            }
                        ]
                    });
                }
                await WaitUntilAsync(() => prices.Count >= before + names.Length,
                    TimeSpan.FromSeconds(5), $"{stageName}-coin mixed-chain prices");
                await Task.Delay(100);
                var stagePrices = prices.Skip(before).ToArray();
                AssertEqual(names.Length, stagePrices.Length,
                    $"The {stageName}-coin watch set emitted missing or deselected prices.");
                foreach (var name in names.Where(name => name != "SOL"))
                {
                    var pool = pools[name];
                    var update = stagePrices.Single(price =>
                        price.PoolKey.ChainId == pool.Chain.ChainId
                        && price.PoolKey.PoolId == pool.Selection.Descriptor.PoolKey.PoolId);
                    AssertDecimalValue(update.SpotPriceQuote, 2,
                        $"The {stageName}-coin {name} quote price was wrong.");
                    AssertEqual($"matrix-{pool.Chain.ChainId}", update.ProviderProfileId,
                        $"The {stageName}-coin {name} price used another chain's provider.");
                    AssertEqual($"fixture:{pool.Chain.ChainId}", update.SourceId,
                        $"The {stageName}-coin {name} price lost its chain source.");
                    if (name == "RH-STOCK/STOCK")
                    {
                        Assert(update.PriceUsd == null,
                            "A stock-token quote was incorrectly treated as USD.");
                    }
                    else
                    {
                        AssertDecimalValue(update.PriceUsd, 2,
                            $"The {stageName}-coin {name} stable-quoted USD price was wrong.");
                    }
                }
                Assert(stagePrices.Count(price => price.PoolKey.PoolAddress == solana.Descriptor.PoolKey.PoolAddress) == 1,
                    $"The {stageName}-coin Solana selection was lost or duplicated.");
                Console.WriteLine($"MIXED MATRIX | stage={stageName} | prices={stagePrices.Length} | chains={string.Join(',', names)} | engine={processId}");
            }
            foreach (var chain in EvmChainDefinitions.Supported)
            {
                await client.ReplaceWatchedPoolsAsync(chain.EnginePoolOwner, []);
            }
            await client.ReplaceWatchedPoolsAsync("solana:mainnet-beta", []);
            await WaitUntilAsync(() => !GetEngineProcessIds().Except(baselineProcessIds).Any(),
                TimeSpan.FromSeconds(5), "mixed-chain lazy engine stop");
        }
        finally
        {
            await client.StopAsync();
        }

        static OnChainWatchedPoolSelection CreatePool(
            EvmChainDefinition chain, string protocol, string factory, string quote, string address)
        {
            var token = "0x1111111111111111111111111111111111111111";
            var catalog = chain.ChainId switch
            {
                EvmChainDefinitions.EthereumMainnetChainId => EthereumDeploymentRegistry.Catalog,
                EvmChainDefinitions.BaseMainnetChainId => BaseDeploymentRegistry.Catalog,
                EvmChainDefinitions.BnbMainnetChainId => BnbDeploymentRegistry.Catalog,
                _ => RobinhoodDeploymentRegistry.Catalog
            };
            return new OnChainWatchedPoolSelection
            {
                SelectedMint = token,
                Descriptor = new OnChainPoolDescriptor
                {
                    PoolKey = new OnChainPoolKey
                    {
                        DeploymentKey = catalog.Deployment(protocol, factory),
                        PoolId = address
                    },
                    PoolType = "constantProduct",
                    ProgramId = factory,
                    BaseMint = token,
                    QuoteMint = quote,
                    BaseDecimals = 18,
                    QuoteDecimals = (byte)(quote == EthereumDeploymentRegistry.Usdc
                                           || quote == BaseDeploymentRegistry.Usdc ? 6 : 18),
                    PairOrientation = "selectedAsToken0",
                    SupportStatus = OnChainSupportStatus.Supported,
                    Asset0 = new OnChainAssetKey
                    {
                        ChainNamespace = ChainNamespaces.Eip155,
                        ChainId = chain.ChainId,
                        Address = token
                    },
                    Asset1 = new OnChainAssetKey
                    {
                        ChainNamespace = ChainNamespaces.Eip155,
                        ChainId = chain.ChainId,
                        Address = quote
                    }
                }
            };
        }
    }
    static async Task VerifyStreamCoordinatorAsync(string enginePath)
    {
        var checkpointRoot = Path.Combine(Path.GetTempPath(), "TrenchHQOnChainCheckpoint", Guid.NewGuid().ToString("N"));
        var baselineProcessIds = GetEngineProcessIds();
        var client = new OnChainEngineClient(enginePath);
        var prices = new ConcurrentQueue<OnChainPriceUpdate>();
        client.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
        var mintBytes = Enumerable.Repeat((byte)3, 32).ToArray();
        var mint = EncodeBase58(mintBytes);
        var pool = new OnChainWatchedPoolSelection
        {
            SelectedMint = mint,
            Descriptor = new OnChainPoolDescriptor
            {
                PoolKey = new OnChainPoolKey
                {
                    ProtocolId = OnChainProtocolIds.PumpBondingCurve,
                    PoolAddress = "coordinator-curve"
                },
                PoolType = "bondingCurve",
                ProgramId = "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P",
                BaseMint = mint,
                QuoteMint = "So11111111111111111111111111111111111111112",
                BaseDecimals = 6,
                QuoteDecimals = 9,
                SupportStatus = OnChainSupportStatus.Supported
            }
        };
        var configuration = new OnChainProviderConfiguration
        {
            Id = "coordinator-configuration",
            StreamEndpoint = "wss://stream.test",
            RpcEndpoint = "https://rpc.test",
            Commitment = OnChainCommitment.Processed,
            ReplayEnabled = false
        };
        var source = new ScriptedOnChainSource(mintBytes);
        var coordinator = new OnChainStreamCoordinator(
            client,
            checkpointRoot,
            (_, _, _) => source,
            (_, _, _, _) => Task.CompletedTask);
        try
        {
            await coordinator.StartAsync(configuration, "checkpoint-secret", [pool]);
            await WaitUntilAsync(() => prices.Count == 1, TimeSpan.FromSeconds(5), "coordinated price");
            await coordinator.StartAsync(configuration, "checkpoint-secret", [pool]);
            await Task.Delay(100);
            AssertEqual(1, source.RunCount,
                "An unchanged panel refresh restarted the on-chain stream.");
            var firstProcessId = GetSingleNewEngineProcessId(baselineProcessIds);
            using (var process = Process.GetProcessById(firstProcessId))
            {
                process.Kill(true);
                process.WaitForExit(2000);
            }
            await WaitUntilAsync(() => source.RunCount >= 2, TimeSpan.FromSeconds(10),
                "coordinator stream restart after engine crash");
            await WaitUntilAsync(() => prices.Count >= 2, TimeSpan.FromSeconds(10),
                "post-crash snapshot epoch price");
            await WaitUntilAsync(() => coordinator.State == OnChainRecoveryState.Live,
                TimeSpan.FromSeconds(10), "coordinator live recovery after engine crash");
            var secondProcessId = GetSingleNewEngineProcessId(baselineProcessIds);
            Assert(firstProcessId != secondProcessId,
                "Coordinator recovery reused the terminated engine process.");
            await WaitUntilAsync(
                () => OnChainCheckpointStore.LoadAsync(checkpointRoot, configuration.Id).GetAwaiter().GetResult() == 42UL,
                TimeSpan.FromSeconds(5),
                "confirmed checkpoint");
            AssertEqual(1, source.ObservedSubscription?.Pools.Length ?? 0,
                "Coordinator did not preserve the selected pool subscription.");
            await coordinator.StopAsync();
            Assert(!File.ReadAllText(Path.Combine(checkpointRoot, "onchain-checkpoints-v1.json"))
                    .Contains("checkpoint-secret", StringComparison.Ordinal),
                "Checkpoint persistence exposed the provider API key.");
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
            "coordinator lazy engine stop");
    }
    static async Task VerifyMixedSourceFailureAsync()
    {
        var waitingSource = new WaitingOnChainSource();
        var failedSource = new FailedOnChainSource();
        var channel = Channel.CreateUnbounded<OnChainSourceUpdate>();
        var subscription = new OnChainStreamSubscription();
        try
        {
            await OnChainStreamCoordinator.RunSourcesAsync(
                    [(waitingSource, subscription), (failedSource, subscription)],
                    channel.Writer,
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(3));
            throw new InvalidOperationException("A failed mixed-mode source was ignored.");
        }
        catch (IOException exception) when (exception.Message == "simulated source failure")
        {
        }
        Assert(waitingSource.Cancelled,
            "The healthy mixed-mode source was not cancelled after its peer failed.");
        Assert(channel.Reader.Completion.IsFaulted,
            "The mixed-mode consumer was not notified of its source failure.");
    }
}

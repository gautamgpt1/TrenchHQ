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
    static async Task VerifyLiveMixedMatrixAsync(string enginePath)
    {
        const string schiffy = "0x42aFA2124ca5a2B83898E46B2dA9a190995b1E18";
        const string stockUsdPoolId =
            "0xf66ccb1c5e6b579f94311131724a8daf0d96b0ebdbe53ab9fa27b98a8abaeb8b";
        const string stockGldPoolId =
            "0xc749412e31087a6e6f9210af575bc9100159fbc9f45da3ca8b26b31f3bf5777e";
        using var discoveryHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var catalogHttp = new HttpClient
        {
            BaseAddress = new Uri("https://api.dexscreener.com/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        var discoveryConfiguration = CreateProviderConfiguration(
            OnChainProviderTypes.CustomRobinhood, "wss://unused.invalid",
            RobinhoodDeploymentRegistry.PublicRpcEndpoint);
        var catalog = await new DexScreenerPoolCatalogClient(catalogHttp)
            .SearchAsync("robinhood", schiffy);
        var selectedCatalog = catalog.Pools.Where(pool =>
            pool.PoolAddress.Equals(stockUsdPoolId, StringComparison.OrdinalIgnoreCase)
            || pool.PoolAddress.Equals(stockGldPoolId, StringComparison.OrdinalIgnoreCase)).ToArray();
        AssertEqual(2, selectedCatalog.Length,
            "The live SCHIFFY catalog no longer contains its USDG and GLD quote pools.");
        var discovered = await new EthereumPoolDiscoveryService(
                new EvmJsonRpcClient(discoveryHttp, discoveryConfiguration, null),
                EvmChainDefinitions.RobinhoodMainnet,
                RobinhoodDeploymentRegistry.Catalog,
                discoveryHttp)
            .DiscoverCatalogPoolsSharedAsync(schiffy, selectedCatalog, CancellationToken.None);
        var stockUsd = new OnChainWatchedPoolSelection
        {
            SelectedMint = schiffy,
            Descriptor = discovered.Pools.Single(pool => pool.PoolKey.PoolId.Equals(
                stockUsdPoolId, StringComparison.OrdinalIgnoreCase))
        };
        var stockGld = new OnChainWatchedPoolSelection
        {
            SelectedMint = schiffy,
            Descriptor = discovered.Pools.Single(pool => pool.PoolKey.PoolId.Equals(
                stockGldPoolId, StringComparison.OrdinalIgnoreCase))
        };
        Assert(stockUsd.Descriptor.SupportStatus == OnChainSupportStatus.Supported
               && stockGld.Descriptor.SupportStatus == OnChainSupportStatus.Supported
               && stockUsd.Descriptor.QuoteMint.Equals(
                   RobinhoodDeploymentRegistry.Usdg, StringComparison.OrdinalIgnoreCase)
               && stockGld.Descriptor.QuoteMint.Equals(
                   "0xC9a981FEE1F9DEc688bb123ccDeCc63D0deBFC4e",
                   StringComparison.OrdinalIgnoreCase),
            "The live Robinhood stock/USDG and stock/GLD selections lost validated quote identity.");
        var gldReference = await new RobinhoodStockTokenCatalogClient(discoveryHttp)
            .GetUsdReferenceAsync(stockGld.Descriptor.QuoteMint);
        Assert(gldReference?.SourceId == "robinhood-stock-token-api:GLD",
            "The official GLD Stock Token reference was unavailable before the live test.");
        var livePools = new (EvmChainDefinition Chain, string ProviderType, OnChainWatchedPoolSelection Pool)[]
        {
            (EvmChainDefinitions.RobinhoodMainnet, OnChainProviderTypes.PublicNodeRobinhood,
                stockUsd),
            (EvmChainDefinitions.BnbMainnet, OnChainProviderTypes.PublicNodeBnb,
                BnbDeploymentRegistry.CreateBnbUsdReferenceSelection()),
            (EvmChainDefinitions.EthereumMainnet, OnChainProviderTypes.PublicNodeEthereum,
                EthereumDeploymentRegistry.CreateEthUsdReferenceSelection()),
            (EvmChainDefinitions.BaseMainnet, OnChainProviderTypes.BasePublic,
                BaseDeploymentRegistry.CreateEthUsdReferenceSelection())
        };
        var stages = new[] { 1, 2, 4 };
        var solanaMintBytes = Enumerable.Repeat((byte)37, 32).ToArray();
        var solana = CreateMixedMatrixSolanaSelection(solanaMintBytes);
        var checkpointRoot = Path.Combine(Path.GetTempPath(), "TrenchHQLiveMixedCheckpoint",
            Guid.NewGuid().ToString("N"));
        var baselineProcessIds = GetEngineProcessIds();
        var client = new OnChainEngineClient(enginePath);
        var prices = new ConcurrentQueue<OnChainPriceUpdate>();
        var logs = new ConcurrentQueue<EvmLogUpdate>();
        var streamErrors = new ConcurrentQueue<string>();
        using var rpcHttp = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
        var coordinators = new EvmStreamCoordinatorCollection(client, checkpointRoot,
            chain => new EvmStreamCoordinator(client, checkpointRoot, chain, rpcHttp,
                sourceFactory: (profile, key) => new ObservingEvmStreamSource(
                    new EvmWebSocketStreamSource(profile, key), logs, static () => { })));
        coordinators.StreamError += (sender, message) => streamErrors.Enqueue(
            $"{(sender as EvmStreamCoordinator)?.ChainDefinition.CatalogChainId}: {message}");
        try
        {
            await client.ReplaceWatchedPoolsAsync("solana:mainnet-beta", [solana]);
            var sharedProcessId = GetSingleNewEngineProcessId(baselineProcessIds);
            for (var stageIndex = 0; stageIndex < stages.Length; stageIndex++)
            {
                var active = livePools.Take(stages[stageIndex]).ToArray();
                foreach (var (chain, providerType, pool) in active)
                {
                    await coordinators.StartAsync(chain.ChainId,
                        CreateLiveConfiguration(chain, providerType), null, [pool]);
                }
                await client.PublishTransactionUpdateAsync(CreatePumpTrade(
                    $"live-matrix-{stageIndex}", solanaMintBytes));
                try
                {
                    await WaitUntilAsync(
                        () => coordinators.GetStateFor(active.Select(item => item.Chain.ChainId))
                                  == OnChainRecoveryState.Live
                              && prices.Count(update => update.PoolKey.PoolAddress
                                  == solana.Descriptor.PoolKey.PoolAddress) >= stageIndex + 1
                              && active.All(item => prices.Any(update =>
                                  update.PoolKey.ChainId == item.Chain.ChainId
                                  && update.PoolKey.PoolId == item.Pool.Descriptor.PoolKey.PoolId
                                  && update.SpotPriceQuote != null && update.PriceUsd != null)),
                        TimeSpan.FromSeconds(90),
                        $"live {stages[stageIndex] + 1}-coin mixed-chain prices");
                }
                catch (TimeoutException)
                {
                    Console.WriteLine(
                        $"LIVE MIXED TIMEOUT | stage={stages[stageIndex] + 1} | state={coordinators.State} | prices={prices.Count} | logs={logs.Count} | errors={string.Join(" | ", streamErrors)}");
                    throw;
                }
                AssertEqual(sharedProcessId, GetSingleNewEngineProcessId(baselineProcessIds),
                    "The live mixed-chain stage restarted the shared engine.");
                Console.WriteLine(
                    $"LIVE MIXED MATRIX | coins={stages[stageIndex] + 1} | evmChains={string.Join(',', active.Select(item => item.Chain.CatalogChainId))} | prices={prices.Count} | logs={logs.Count} | engine={sharedProcessId}");
            }
            await coordinators.StopAsync(EvmChainDefinitions.BnbMainnetChainId);
            var stockFilters = JsonSerializer.Serialize(EvmWebSocketStreamSource.CreateRpcLogFilters(
                [stockUsd, stockGld], 1, 2));
            Assert(stockFilters.Contains(stockUsdPoolId, StringComparison.OrdinalIgnoreCase)
                   && stockFilters.Contains(stockGldPoolId, StringComparison.OrdinalIgnoreCase)
                   && !stockFilters.Contains(
                       RobinhoodDeploymentRegistry.WrappedEtherUsdgReferencePool,
                       StringComparison.OrdinalIgnoreCase),
                "The stock-pair subscription added the unselected WETH/USDG reference.");
            await coordinators.StartAsync(EvmChainDefinitions.RobinhoodMainnetChainId,
                CreateLiveConfiguration(EvmChainDefinitions.RobinhoodMainnet,
                    OnChainProviderTypes.PublicNodeRobinhood), null, [stockUsd, stockGld]);
            await client.PublishTransactionUpdateAsync(CreatePumpTrade(
                "live-matrix-5-stock", solanaMintBytes));
            try
            {
                await WaitUntilAsync(
                    () => coordinators.GetStateFor(
                              [EvmChainDefinitions.RobinhoodMainnetChainId,
                                  EvmChainDefinitions.EthereumMainnetChainId,
                                  EvmChainDefinitions.BaseMainnetChainId]) == OnChainRecoveryState.Live
                          && prices.Any(update => update.PoolKey.PoolId.Equals(
                              stockGldPoolId, StringComparison.OrdinalIgnoreCase)
                              && update.PriceUsd != null
                              && update.ReferenceSourceId?.StartsWith(
                                  "robinhood-stock-token-api:GLD", StringComparison.Ordinal) == true),
                    TimeSpan.FromSeconds(90), "live five-coin stock-quoted USD price");
            }
            catch (TimeoutException)
            {
                var stockUpdates = prices.Where(update => update.PoolKey.PoolId.Equals(
                    stockGldPoolId, StringComparison.OrdinalIgnoreCase)).ToArray();
                Console.WriteLine(
                    $"LIVE STOCK TIMEOUT | state={coordinators.Get(EvmChainDefinitions.RobinhoodMainnetChainId).State} | stockUpdates={stockUpdates.Length} | latestQuote={stockUpdates.LastOrDefault()?.SpotPriceQuote != null} | latestUsd={stockUpdates.LastOrDefault()?.PriceUsd != null} | latestReference={stockUpdates.LastOrDefault()?.ReferenceSourceId ?? "none"} | errors={string.Join(" | ", streamErrors)}");
                throw;
            }
            AssertEqual(sharedProcessId, GetSingleNewEngineProcessId(baselineProcessIds),
                "The live stock-pair switch restarted the shared engine.");
            Console.WriteLine(
                $"LIVE MIXED MATRIX | coins=5-stock | evmChains=robinhood,ethereum,base | stockQuote=GLD | prices={prices.Count} | logs={logs.Count} | engine={sharedProcessId}");
        }
        finally
        {
            await coordinators.StopAsync();
            await client.ReplaceWatchedPoolsAsync("solana:mainnet-beta", []);
            await client.StopAsync();
            if (Directory.Exists(checkpointRoot))
            {
                Directory.Delete(checkpointRoot, true);
            }
        }
        await WaitUntilAsync(() => !GetEngineProcessIds().Except(baselineProcessIds).Any(),
            TimeSpan.FromSeconds(10), "live mixed-chain engine stop");

        static OnChainProviderConfiguration CreateLiveConfiguration(
            EvmChainDefinition chain, string providerType)
        {
            var preset = OnChainProviderCatalog.Get(providerType);
            var configuration = CreateProviderConfiguration(providerType,
                preset.DefaultStreamEndpoint, preset.DefaultRpcEndpoint);
            configuration.Id = $"live-matrix-{chain.ChainId}";
            configuration.CapabilitySnapshot = new OnChainProviderCapabilitySnapshot
            {
                ObservedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ChainId = OnChainProviderCapabilityState.Supported,
                SafeBlock = OnChainProviderCapabilityState.Supported,
                FinalizedBlock = OnChainProviderCapabilityState.Supported,
                BlockHashCall = OnChainProviderCapabilityState.Supported,
                BlockHashLogs = OnChainProviderCapabilityState.Supported,
                WebSocketLogs = OnChainProviderCapabilityState.Supported
            };
            return configuration;
        }
    }
}

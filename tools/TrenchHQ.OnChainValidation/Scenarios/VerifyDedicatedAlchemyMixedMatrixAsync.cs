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
    static async Task VerifyDedicatedAlchemyMixedMatrixAsync(
        string enginePath, string credential, string scenario, TimeSpan observationWindow,
        bool webSocket)
    {
        const string solanaPoolId = "58oQChx4yWmvKdwLLZzBi4ChoCc2fqCUWBkwMihLYQo2";
        var checkpointRoot = Path.Combine(Path.GetTempPath(), "TrenchHQAlchemyMixedCheckpoint",
            Guid.NewGuid().ToString("N"));
        var baselineProcessIds = GetEngineProcessIds();
        var client = new OnChainEngineClient(enginePath);
        using var rpcHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var prices = new ConcurrentQueue<OnChainPriceUpdate>();
        var logs = new ConcurrentQueue<EvmLogUpdate>();
        var errors = new ConcurrentQueue<string>();
        client.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
        var solanaConfiguration = OnChainProviderConfigurationStore.CreateConfiguration(
            OnChainProviderCatalog.Get(OnChainProviderTypes.AlchemyWebSocket));
        var solana = new OnChainStreamCoordinator(client, checkpointRoot);
        solana.StreamError += (_, message) => errors.Enqueue(
            "solana:" + message.Replace(credential, "[redacted]", StringComparison.Ordinal));
        var evm = new EvmStreamCoordinatorCollection(client, checkpointRoot,
            chain => new EvmStreamCoordinator(client, checkpointRoot, chain, rpcHttp,
                sourceFactory: (profile, key) => new ObservingEvmStreamSource(
                    new EvmWebSocketStreamSource(profile, key), logs, static () => { })));
        evm.StreamError += (sender, message) => errors.Enqueue(
            $"{(sender as EvmStreamCoordinator)?.ChainDefinition.CatalogChainId}:"
            + message.Replace(credential, "[redacted]", StringComparison.Ordinal));
        try
        {
            var includeSolana = scenario is "solana" or "2" or "3" or "5" or "5-stock";
            var solanaPool = includeSolana
                ? await DiscoverDedicatedAlchemySolanaPoolAsync(
                    rpcHttp, solanaConfiguration, credential, client)
                : null;
            var pools = new List<(EvmChainDefinition Chain, string ProviderType,
                OnChainWatchedPoolSelection[] Selections)>();
            if (scenario is "robinhood" or "2" or "3" or "5" or "5-stock")
            {
                const string schiffy = "0x42aFA2124ca5a2B83898E46B2dA9a190995b1E18";
                const string usdgPool =
                    "0xf66ccb1c5e6b579f94311131724a8daf0d96b0ebdbe53ab9fa27b98a8abaeb8b";
                const string gldPool =
                    "0xc749412e31087a6e6f9210af575bc9100159fbc9f45da3ca8b26b31f3bf5777e";
                using var catalogHttp = new HttpClient
                {
                    BaseAddress = new Uri("https://api.dexscreener.com/"),
                    Timeout = TimeSpan.FromSeconds(30)
                };
                var catalog = await new DexScreenerPoolCatalogClient(catalogHttp)
                    .SearchAsync("robinhood", schiffy);
                var poolIds = scenario == "5-stock"
                    ? new[] { usdgPool, gldPool }
                    : [usdgPool];
                var candidates = catalog.Pools.Where(pool => poolIds.Contains(
                    pool.PoolAddress, StringComparer.OrdinalIgnoreCase)).ToArray();
                AssertEqual(poolIds.Length, candidates.Length,
                    "The Alchemy stock test lost a selected SCHIFFY catalog pool.");
                var robinhoodPreset = OnChainProviderCatalog.Get(OnChainProviderTypes.AlchemyRobinhood);
                var robinhoodRpc = new EvmJsonRpcClient(rpcHttp,
                    CreateProviderConfiguration(robinhoodPreset.ProviderType,
                        robinhoodPreset.DefaultStreamEndpoint, robinhoodPreset.DefaultRpcEndpoint),
                    credential);
                var discovered = await new EthereumPoolDiscoveryService(
                        robinhoodRpc, EvmChainDefinitions.RobinhoodMainnet,
                        RobinhoodDeploymentRegistry.Catalog, rpcHttp)
                    .DiscoverCatalogPoolsSharedAsync(schiffy, candidates, CancellationToken.None);
                pools.Add((EvmChainDefinitions.RobinhoodMainnet,
                    OnChainProviderTypes.AlchemyRobinhood,
                    poolIds.Select(poolId => new OnChainWatchedPoolSelection
                    {
                        SelectedMint = schiffy,
                        Descriptor = discovered.Pools.Single(pool =>
                            pool.PoolKey.PoolId.Equals(poolId, StringComparison.OrdinalIgnoreCase)
                            && pool.SupportStatus == OnChainSupportStatus.Supported)
                    }).ToArray()));
            }
            if (scenario is "bnb" or "3" or "5")
            {
                pools.Add((EvmChainDefinitions.BnbMainnet, OnChainProviderTypes.AlchemyBnb,
                    [BnbDeploymentRegistry.CreateBnbUsdReferenceSelection()]));
            }
            if (scenario is "ethereum" or "5" or "5-stock")
            {
                pools.Add((EvmChainDefinitions.EthereumMainnet, OnChainProviderTypes.AlchemyEthereum,
                    [EthereumDeploymentRegistry.CreateEthUsdReferenceSelection()]));
            }
            if (scenario is "base" or "5" or "5-stock")
            {
                pools.Add((EvmChainDefinitions.BaseMainnet, OnChainProviderTypes.AlchemyBase,
                    [BaseDeploymentRegistry.CreateEthUsdReferenceSelection()]));
            }
            var expectedCount = scenario switch
            {
                "solana" or "robinhood" or "bnb" or "ethereum" or "base" => 1,
                "5-stock" => 5,
                _ => int.Parse(scenario)
            };
            AssertEqual(expectedCount,
                (includeSolana ? 1 : 0) + pools.Sum(item => item.Selections.Length),
                "The Alchemy live scenario selected the wrong number of pools.");
            if (solanaPool != null)
            {
                await solana.StartAsync(solanaConfiguration, credential, [solanaPool],
                    webSocketPoolAddresses: webSocket
                        ? new HashSet<string>(StringComparer.Ordinal) { solanaPoolId }
                        : new HashSet<string>(StringComparer.Ordinal));
            }
            foreach (var (chain, providerType, selections) in pools)
            {
                var preset = OnChainProviderCatalog.Get(providerType);
                var profile = CreateProviderConfiguration(providerType,
                    preset.DefaultStreamEndpoint, preset.DefaultRpcEndpoint);
                profile.CapabilitySnapshot = new OnChainProviderCapabilitySnapshot
                {
                    ObservedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ChainId = OnChainProviderCapabilityState.Supported,
                    SafeBlock = OnChainProviderCapabilityState.Supported,
                    FinalizedBlock = OnChainProviderCapabilityState.Supported,
                    BlockHashCall = OnChainProviderCapabilityState.Supported,
                    BlockHashLogs = OnChainProviderCapabilityState.Supported,
                    WebSocketLogs = OnChainProviderCapabilityState.Supported
                };
                await evm.StartAsync(chain.ChainId, profile, credential, selections,
                    webSocketPoolIds: webSocket
                        ? selections.Select(static item => item.Descriptor.PoolKey.PoolId)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase)
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
            var selected = pools.SelectMany(item => item.Selections).ToArray();
            try
            {
                await WaitUntilAsync(
                () => (!includeSolana || solana.State == OnChainRecoveryState.Live)
                      && (pools.Count == 0 || evm.GetStateFor(pools.Select(item => item.Chain.ChainId))
                          == OnChainRecoveryState.Live)
                      && (!includeSolana || prices.Any(price => price.PoolKey.PoolAddress == solanaPoolId
                          && price.PriceUsd != null)
                          )
                      && selected.All(selection => prices.Any(price =>
                          price.PoolKey.ChainId == selection.Descriptor.PoolKey.ChainId
                          && price.PoolKey.PoolId == selection.Descriptor.PoolKey.PoolId
                          && price.PriceUsd != null)),
                TimeSpan.FromSeconds(30), $"Alchemy {scenario} live prices");
            }
            catch (TimeoutException)
            {
                Console.WriteLine($"ALCHEMY MATRIX TIMEOUT | scenario={scenario} | solanaState={solana.State} | evmStates={string.Join(',', pools.Select(item => $"{item.Chain.CatalogChainId}:{evm.Get(item.Chain.ChainId).State}"))} | prices={prices.Count} | solanaUsd={prices.Count(price => price.PoolKey.PoolAddress == solanaPoolId && price.PriceUsd != null)} | evmUsd={prices.Count(price => price.PoolKey.ChainNamespace == ChainNamespaces.Eip155 && price.PriceUsd != null)} | logs={logs.Count} | errors={string.Join(" | ", errors.Take(4))}");
                throw;
            }
            var engineId = GetSingleNewEngineProcessId(baselineProcessIds);
            Console.WriteLine($"ALCHEMY MATRIX START | scenario={scenario} | mode={(webSocket ? "websocket" : "poll-1s")} | utc={DateTimeOffset.UtcNow:O} | engine={engineId} | prices={prices.Count} | logs={logs.Count}");
            await Task.Delay(observationWindow);
            var solanaSampledPrices = prices.Count(price =>
                price.PoolKey.PoolAddress == solanaPoolId
                && price.SourceId.StartsWith("rpcSample:", StringComparison.Ordinal));
            var stockUsdPrices = prices.Count(price =>
                price.ReferenceSourceId?.StartsWith(
                    "robinhood-stock-token-api:GLD", StringComparison.Ordinal) == true);
            var logsByChain = string.Join(',', logs.GroupBy(static log => log.ChainId)
                .OrderBy(static group => group.Key, StringComparer.Ordinal)
                .Select(static group => $"{group.Key}:{group.Count()}"));
            Console.WriteLine($"ALCHEMY MATRIX END | scenario={scenario} | mode={(webSocket ? "websocket" : "poll-1s")} | utc={DateTimeOffset.UtcNow:O} | prices={prices.Count} | logs={logs.Count} | logsByChain={logsByChain} | solanaSampledPrices={solanaSampledPrices} | stockUsdPrices={stockUsdPrices} | errors={errors.Count}");
            AssertEqual(engineId, GetSingleNewEngineProcessId(baselineProcessIds),
                "The Alchemy live selection restarted the shared engine.");
            if (includeSolana && !webSocket)
            {
                Assert(solanaSampledPrices > 0,
                    "The live Solana selection never delivered a sampled USD price.");
            }
            if (scenario == "5-stock")
            {
                Assert(stockUsdPrices > 0,
                    "The live Alchemy stock-quoted pool never received official GLD USD conversion.");
            }
            AssertEqual(0, errors.Count,
                "A live Alchemy stream reported a provider error during the observation window.");
        }
        finally
        {
            await evm.StopAsync();
            await solana.StopAsync();
            await client.StopAsync();
            if (Directory.Exists(checkpointRoot))
            {
                Directory.Delete(checkpointRoot, true);
            }
        }
        await WaitUntilAsync(() => !GetEngineProcessIds().Except(baselineProcessIds).Any(),
            TimeSpan.FromSeconds(10), "Alchemy mixed-chain engine stop");
    }
}

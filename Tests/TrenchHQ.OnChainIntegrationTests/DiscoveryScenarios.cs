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
    static async Task VerifyUniswapV2DiscoveryAndEngineAsync(string enginePath)
    {
        const string selectedToken = "0x1111111111111111111111111111111111111111";
        const string pair = "0x2222222222222222222222222222222222222222";
        const string v3Pool = "0x3333333333333333333333333333333333333333";
        using var handler = new UniswapDiscoveryFixtureHandler(
            EvmChainDefinitions.EthereumMainnetChainId,
            EthereumDeploymentRegistry.Catalog,
            selectedToken,
            pair,
            v3Pool);
        using var httpClient = new HttpClient(handler);
        var configuration = CreateProviderConfiguration(
            OnChainProviderTypes.PublicNodeEthereum,
            "wss://ethereum-rpc.publicnode.com",
            "https://ethereum-rpc.publicnode.com");
        var rpc = new EvmJsonRpcClient(httpClient, configuration, null);
        var discovery = new EthereumPoolDiscoveryService(rpc);
        var result = await discovery.DiscoverAsync(selectedToken);
        AssertEqual(2, result.Pools.Length,
            "Bounded Uniswap factory discovery returned the wrong number of pools.");
        var descriptor = result.Pools.Single(pool =>
            pool.PoolKey.ProtocolId == OnChainProtocolIds.UniswapV2);
        AssertEqual(OnChainProtocolIds.UniswapV2, descriptor.PoolKey.ProtocolId,
            "The discovered Uniswap v2 protocol identity is wrong.");
        AssertEqual(pair, descriptor.PoolKey.PoolId,
            "The discovered Uniswap v2 pair identity is wrong.");
        AssertEqual(OnChainSupportStatus.Supported, descriptor.SupportStatus,
            "A fully validated Uniswap v2 pair was not marked supported.");
        var v3Descriptor = result.Pools.Single(pool =>
            pool.PoolKey.ProtocolId == OnChainProtocolIds.UniswapV3);
        AssertEqual(v3Pool, v3Descriptor.PoolKey.PoolId,
            "The discovered Uniswap v3 pool identity is wrong.");
        AssertEqual(500U, v3Descriptor.FeeTier,
            "The discovered Uniswap v3 fee tier is wrong.");
        AssertEqual(10, v3Descriptor.TickSpacing,
            "The discovered Uniswap v3 tick spacing is wrong.");
        Assert(handler.CallCount <= 50,
            "Uniswap discovery exceeded its bounded factory-call budget.");
        Assert(!handler.SawUnpinnedStateRead,
            "Uniswap v2 discovery performed a mutable-latest state read.");

        var client = new OnChainEngineClient(enginePath);
        var prices = new ConcurrentQueue<OnChainPriceUpdate>();
        client.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
        try
        {
            await client.ReplaceWatchedPoolsAsync(
            [
                new OnChainWatchedPoolSelection
                {
                    Descriptor = descriptor,
                    SelectedMint = selectedToken
                }
            ]);
            var blockHash = Hash(16);
            await client.PublishEvmHeadAsync(CreateEvmHead(16, blockHash, Hash(15)));
            await client.PublishEvmSnapshotAsync(new EvmSnapshotResponse
            {
                RequestId = "v2-snapshot-16",
                PoolKey = descriptor.PoolKey,
                BlockNumber = 16,
                BlockHash = blockHash,
                Calls =
                [
                    new EvmSnapshotCall
                    {
                        Id = "getReserves",
                        Success = true,
                        ReturnData = AbiWords(
                            8_000_000_000_000_000_000UL,
                            16_000_000_000_000_000_000UL,
                            123)
                    }
                ]
            });
            await WaitUntilAsync(() => prices.Count == 1, TimeSpan.FromSeconds(5),
                "Uniswap v2 reserve snapshot price");
            AssertEqual("2000000000000000000", prices.Last().SpotPriceQuote?.Coefficient,
                "The Uniswap v2 reserve spot price is wrong.");

            var swap = new EvmLogUpdate
            {
                ChainId = "1",
                ConnectionEpoch = 7,
                Address = pair,
                Topics =
                [
                    EvmEventTopics.UniswapV2SwapTopic,
                    Hash(1000),
                    Hash(1001)
                ],
                Data = AbiWords(
                    2_000_000_000_000_000_000UL,
                    0,
                    0,
                    4_000_000_000_000_000_000UL),
                BlockNumber = 16,
                BlockHash = blockHash,
                TransactionHash = Hash(2000),
                TransactionIndex = 3,
                LogIndex = 4,
                ObservedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            await client.PublishEvmLogAsync(swap);
            await WaitUntilAsync(() => prices.Count == 2, TimeSpan.FromSeconds(5),
                "Uniswap v2 executed trade price");
            var trade = prices.Last();
            AssertEqual("2000000000000000000", trade.LastTradePriceQuote?.Coefficient,
                "The Uniswap v2 executed price is wrong.");
            AssertEqual(EvmFinality.Head, trade.Finality?.Status,
                "A new EVM trade was not marked as head/tentative.");
            AssertEqual(16UL, trade.ChainPosition?.BlockNumber,
                "The EVM block position was lost at the engine boundary.");

            await client.PublishEvmLogAsync(swap);
            await Task.Delay(150);
            AssertEqual(2, prices.Count,
                "A duplicate Uniswap v2 log produced another price update.");

            await client.PublishEvmFinalityAsync(new EvmFinalityUpdate
            {
                ChainId = "1",
                Head = new EvmBlockReference { Number = 16, Hash = blockHash },
                Safe = new EvmBlockReference { Number = 16, Hash = blockHash },
                ObservedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
            await WaitUntilAsync(() => prices.Count == 3, TimeSpan.FromSeconds(5),
                "Uniswap v2 safe finality promotion");
            AssertEqual(EvmFinality.Safe, prices.Last().Finality?.Status,
                "The Uniswap v2 price did not advance to safe finality.");

            swap.Removed = true;
            await client.PublishEvmLogAsync(swap);
            await WaitUntilAsync(() => prices.Count == 4, TimeSpan.FromSeconds(5),
                "Uniswap v2 removed-log rollback");
            Assert(prices.Last().LastTradePriceQuote == null
                   && prices.Last().SpotPriceQuote != null,
                "A removed Uniswap v2 trade did not restore the prior reserve snapshot.");

            while (prices.TryDequeue(out _))
            {
            }
            await client.ReplaceWatchedPoolsAsync(
            [
                new OnChainWatchedPoolSelection
                {
                    Descriptor = v3Descriptor,
                    SelectedMint = selectedToken
                }
            ]);
            var q96 = System.Numerics.BigInteger.One << 96;
            await client.PublishEvmSnapshotAsync(new EvmSnapshotResponse
            {
                RequestId = "v3-snapshot-16",
                PoolKey = v3Descriptor.PoolKey,
                BlockNumber = 16,
                BlockHash = blockHash,
                Calls =
                [
                    new EvmSnapshotCall
                    {
                        Id = "slot0",
                        Success = true,
                        ReturnData = AbiBigWords(q96, 0, 0, 0, 0, 0, 1)
                    },
                    new EvmSnapshotCall
                    {
                        Id = "liquidity",
                        Success = true,
                        ReturnData = AbiBigWords(100)
                    }
                ]
            });
            await WaitUntilAsync(() => prices.Count == 1, TimeSpan.FromSeconds(5),
                "Uniswap v3 slot0 snapshot price");
            AssertEqual("1000000000000000000", prices.Last().SpotPriceQuote?.Coefficient,
                "The Uniswap v3 Q64.96 snapshot price is wrong.");

            await client.PublishEvmLogAsync(new EvmLogUpdate
            {
                ChainId = "1",
                ConnectionEpoch = 8,
                Address = v3Pool,
                Topics =
                [
                    EvmEventTopics.UniswapV3SwapTopic,
                    Hash(3000),
                    Hash(3001)
                ],
                Data = AbiBigWords(
                    2_000_000_000_000_000_000,
                    -4_000_000_000_000_000_000,
                    q96,
                    100,
                    0),
                BlockNumber = 17,
                BlockHash = Hash(17),
                TransactionHash = Hash(3002),
                TransactionIndex = 1,
                LogIndex = 2,
                ObservedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
            await WaitUntilAsync(() => prices.Count == 2, TimeSpan.FromSeconds(5),
                "Uniswap v3 swap price");
            AssertEqual("2000000000000000000", prices.Last().LastTradePriceQuote?.Coefficient,
                "The Uniswap v3 signed-delta execution price is wrong.");
            AssertEqual("1000000000000000000", prices.Last().SpotPriceQuote?.Coefficient,
                "The Uniswap v3 post-swap sqrt price is wrong.");
        }
        finally
        {
            await client.StopAsync();
        }
    }
    static async Task VerifyBaseUniswapDiscoveryAsync()
    {
        const string selectedToken = "0x1111111111111111111111111111111111111111";
        const string pair = "0x2222222222222222222222222222222222222222";
        const string v3Pool = "0x3333333333333333333333333333333333333333";
        using var handler = new UniswapDiscoveryFixtureHandler(
            EvmChainDefinitions.BaseMainnetChainId,
            BaseDeploymentRegistry.Catalog,
            selectedToken,
            pair,
            v3Pool);
        using var httpClient = new HttpClient(handler);
        var configuration = CreateProviderConfiguration(
            OnChainProviderTypes.BasePublic,
            "wss://mainnet.base.org",
            "https://mainnet.base.org");
        var discovery = new EthereumPoolDiscoveryService(
            new EvmJsonRpcClient(httpClient, configuration, null),
            EvmChainDefinitions.BaseMainnet,
            BaseDeploymentRegistry.Catalog,
            httpClient);
        var v2Result = await discovery.DiscoverUniswapV2Async(selectedToken);
        var v3Result = await discovery.DiscoverUniswapV3Async(selectedToken);
        var discoveredPools = v2Result.Pools.Concat(v3Result.Pools).ToArray();

        AssertEqual(2, discoveredPools.Length,
            "Bounded Base Uniswap discovery returned the wrong number of pools.");
        Assert(discoveredPools.All(pool => pool.PoolKey.ChainId == EvmChainDefinitions.BaseMainnetChainId),
            "Base discovery emitted a pool with the wrong chain identity.");
        AssertEqual(pair, discoveredPools.Single(pool =>
                pool.PoolKey.ProtocolId == OnChainProtocolIds.UniswapV2).PoolKey.PoolId,
            "Base V2 factory discovery returned the wrong pair.");
        var v3 = discoveredPools.Single(pool => pool.PoolKey.ProtocolId == OnChainProtocolIds.UniswapV3);
        AssertEqual(v3Pool, v3.PoolKey.PoolId,
            "Base V3 factory discovery returned the wrong pool.");
        AssertEqual(500U, v3.FeeTier,
            "Base V3 discovery returned the wrong fee tier.");
        Assert(handler.CallCount <= 50,
            "Base Uniswap discovery exceeded its bounded factory-call budget.");
        Assert(!handler.SawUnpinnedStateRead,
            "Base discovery performed a mutable-latest state read.");

        using var rateLimitedHandler = new UniswapDiscoveryFixtureHandler(
            EvmChainDefinitions.BaseMainnetChainId,
            BaseDeploymentRegistry.Catalog,
            selectedToken,
            pair,
            v3Pool,
            aerodromeRateLimited: true);
        using var rateLimitedHttpClient = new HttpClient(rateLimitedHandler);
        var partialResult = await new EthereumPoolDiscoveryService(
                new EvmJsonRpcClient(rateLimitedHttpClient, configuration, null),
                EvmChainDefinitions.BaseMainnet,
                BaseDeploymentRegistry.Catalog,
                rateLimitedHttpClient)
            .DiscoverAsync(selectedToken);
        AssertEqual(2, partialResult.Pools.Length,
            "An Aerodrome rate limit discarded independently validated Base Uniswap pools.");
        Assert(partialResult.Warnings.Any(static warning =>
                warning.Contains("Aerodrome discovery was incomplete", StringComparison.Ordinal)
                && warning.Contains("rate limit", StringComparison.OrdinalIgnoreCase)),
            "A partial Base discovery result did not explain the Aerodrome rate limit.");
    }
    static async Task VerifyRobinhoodStockTokenCatalogAsync()
    {
        const string nvda = "0x1111111111111111111111111111111111111111";
        const string gld = "0x3333333333333333333333333333333333333333";
        using var handler = new RobinhoodStockTokenApiFixtureHandler(nvda, gld);
        using var httpClient = new HttpClient(handler);
        var catalog = new RobinhoodStockTokenCatalogClient(httpClient);

        var bySymbol = await catalog.ResolveAsync("nvda")
                       ?? throw new InvalidOperationException(
                           "The official Robinhood Stock Token symbol did not resolve.");
        AssertEqual(nvda, bySymbol.Address, "Stock Token search returned the wrong deployment address.");
        AssertEqual("NVDA", bySymbol.Symbol, "Stock Token search changed the official symbol.");
        AssertEqual("NVIDIA Stock Token", bySymbol.Name, "Stock Token search changed the official name.");
        var byName = await catalog.ResolveAsync("NVIDIA Stock Token");
        AssertEqual(nvda, byName?.Address, "Exact Stock Token name search did not reuse the catalog.");
        Assert(catalog.TryGetCachedByAddress(nvda, out var cached) && cached.Symbol == "NVDA",
            "The Stock Token catalog did not expose the validated address cache.");

        var reference = await catalog.GetUsdReferenceAsync(nvda)
                        ?? throw new InvalidOperationException(
                            "The official Stock Token price did not resolve.");
        AssertEqual("501", reference.Value.Coefficient,
            "The bid/ask midpoint and currentMultiplier were not applied exactly.");
        AssertEqual<uint>(1, reference.Value.Scale,
            "The Stock Token reference decimal was not normalized exactly.");
        AssertEqual("robinhood-stock-token-api:NVDA", reference.SourceId,
            "The Stock Token price lost its source identity.");
        var cachedReference = await catalog.GetUsdReferenceAsync(nvda);
        AssertEqual(reference, cachedReference, "The 15-second Stock Token price cache changed the value.");
        AssertEqual(1, handler.AssetRequests, "The five-minute Stock Token asset cache was bypassed.");
        AssertEqual(1, handler.PriceRequests, "The 15-second Stock Token price cache was bypassed.");

        var wideSpreadReference = await catalog.GetUsdReferenceAsync(gld)
                                  ?? throw new InvalidOperationException(
                                      "The corroborated side of a wide Stock Token spread did not resolve.");
        AssertEqual("40122", wideSpreadReference.Value.Coefficient,
            "A wide bid/ask spread used the distorted midpoint instead of the side inside the daily range.");
        AssertEqual<uint>(2, wideSpreadReference.Value.Scale,
            "The corroborated wide-spread Stock Token reference was not normalized exactly.");
        AssertEqual(2, handler.PriceRequests, "The second Stock Token price was not requested exactly once.");

        var ponsCurve = new OnChainPoolDescriptor
        {
            PoolKey = new OnChainPoolKey
            {
                DeploymentKey = RobinhoodDeploymentRegistry.Catalog.Deployment(
                    OnChainProtocolIds.PonsV2Curve,
                    RobinhoodDeploymentRegistry.PonsV2Factory),
                PoolId = "0x2222222222222222222222222222222222222222"
            },
            PoolType = "constantProductBondingCurve",
            ProgramId = RobinhoodDeploymentRegistry.PonsV2Factory,
            BaseMint = nvda,
            QuoteMint = RobinhoodDeploymentRegistry.WrappedEther,
            BaseDecimals = 18,
            QuoteDecimals = 18,
            SupportStatus = OnChainSupportStatus.Supported
        };
        var filters = JsonSerializer.Serialize(EvmWebSocketStreamSource.CreateRpcLogFilters(
        [
            new OnChainWatchedPoolSelection { SelectedMint = nvda, Descriptor = ponsCurve }
        ],
        1,
        2));
        Assert(filters.Contains(ponsCurve.PoolKey.PoolId, StringComparison.OrdinalIgnoreCase),
            "Pons v2 curve replay did not target the exact launched curve.");
        Assert(filters.Contains(EvmEventTopics.PonsV2CurveBuyTopic, StringComparison.Ordinal)
               && filters.Contains(EvmEventTopics.PonsV2CurveSellTopic, StringComparison.Ordinal),
            "Pons v2 curve replay omitted an official trade event.");
        Assert(RobinhoodDeploymentRegistry.PonsV2Factory.Equals(
                "0x7eD598BcEf8bd9Edd8C97A195C6d13f40801EC7e",
                StringComparison.OrdinalIgnoreCase),
            "The allowlisted pons v2 factory changed.");
        Assert(RobinhoodDeploymentRegistry.DopplerAirlock.Equals(
                "0xeb7c034704ef8dcd2d32324c1545f62fb4ad0862",
                StringComparison.OrdinalIgnoreCase),
            "The allowlisted Doppler Airlock changed.");
    }
    static async Task VerifyRobinhoodUniswapDiscoveryAndEngineAsync(string enginePath)
    {
        const string selectedToken = "0x1111111111111111111111111111111111111111";
        const string v2Pair = "0x2222222222222222222222222222222222222222";
        const string v3Pool = "0x3333333333333333333333333333333333333333";
        const string v4PoolId =
            "0x4444444444444444444444444444444444444444444444444444444444444444";
        var fixtures = new[]
        {
            new EvmCatalogPoolFixture(
                v2Pair,
                RobinhoodDeploymentRegistry.WrappedEther,
                OnChainProtocolIds.UniswapV2,
                RobinhoodDeploymentRegistry.UniswapV2Factory),
            new EvmCatalogPoolFixture(
                v3Pool,
                RobinhoodDeploymentRegistry.Usdg,
                OnChainProtocolIds.UniswapV3,
                RobinhoodDeploymentRegistry.UniswapV3Factory,
                FeeTier: 100,
                TickSpacing: 1),
            new EvmCatalogPoolFixture(
                v4PoolId,
                RobinhoodDeploymentRegistry.Usdg,
                OnChainProtocolIds.UniswapV4,
                RobinhoodDeploymentRegistry.UniswapV4PoolManager,
                FeeTier: 100,
                TickSpacing: 1,
                CreatedAtUnixMs: 32000)
        };
        var v4Catalog = new PoolCatalogEntry
        {
            SourceId = "dexscreener",
            ChainId = "robinhood",
            ProtocolId = "uniswap",
            PoolAddress = v4PoolId,
            Labels = ["v4"],
            BaseAsset = new PoolCatalogAsset { Address = selectedToken },
            QuoteAsset = new PoolCatalogAsset { Address = RobinhoodDeploymentRegistry.Usdg },
            CreatedAtUnixMs = 32000
        };

        using var handler = new CatalogProtocolDiscoveryFixtureHandler(
            selectedToken,
            fixtures,
            chain: EvmChainDefinitions.RobinhoodMainnet,
            deployments: RobinhoodDeploymentRegistry.Catalog);
        using var httpClient = new HttpClient(handler);
        var configuration = CreateProviderConfiguration(
            OnChainProviderTypes.CustomRobinhood,
            "wss://robinhood.example",
            RobinhoodDeploymentRegistry.PublicRpcEndpoint);
        var result = await new EthereumPoolDiscoveryService(
                new EvmJsonRpcClient(httpClient, configuration, null),
                EvmChainDefinitions.RobinhoodMainnet,
                RobinhoodDeploymentRegistry.Catalog,
                httpClient)
            .DiscoverAsync(selectedToken, [v4Catalog]);

        AssertEqual(3, result.Pools.Length,
            "Robinhood Chain discovery did not retain Uniswap V2, V3, and V4.");
        Assert(result.Pools.All(pool =>
                pool.PoolKey.ChainId == EvmChainDefinitions.RobinhoodMainnetChainId),
            "Robinhood Chain discovery emitted a pool with the wrong chain identity.");
        AssertEqual(v2Pair, result.Pools.Single(pool =>
                pool.PoolKey.ProtocolId == OnChainProtocolIds.UniswapV2).PoolKey.PoolId,
            "Robinhood Chain V2 factory discovery returned the wrong pair.");
        AssertEqual(v3Pool, result.Pools.Single(pool =>
                pool.PoolKey.ProtocolId == OnChainProtocolIds.UniswapV3).PoolKey.PoolId,
            "Robinhood Chain V3 factory discovery returned the wrong pool.");
        AssertEqual(v4PoolId, result.Pools.Single(pool =>
                pool.PoolKey.ProtocolId == OnChainProtocolIds.UniswapV4).PoolKey.PoolId,
            "Robinhood Chain V4 discovery returned the wrong PoolId.");
        Assert(!handler.SawUnpinnedStateRead,
            "Robinhood Chain discovery performed a mutable-latest state read.");
        Assert(handler.SawExpectedBlockscoutApiRoot,
            "Robinhood Chain V4 discovery did not use the official Blockscout API root.");

        using var fallbackHandler = new CatalogProtocolDiscoveryFixtureHandler(
            selectedToken,
            fixtures,
            chain: EvmChainDefinitions.RobinhoodMainnet,
            deployments: RobinhoodDeploymentRegistry.Catalog,
            blockExplorerUnavailable: true);
        using var fallbackHttpClient = new HttpClient(fallbackHandler);
        var fallbackResult = await new EthereumPoolDiscoveryService(
                new EvmJsonRpcClient(fallbackHttpClient, configuration, null),
                EvmChainDefinitions.RobinhoodMainnet,
                RobinhoodDeploymentRegistry.Catalog,
                fallbackHttpClient)
            .DiscoverAsync(selectedToken, [v4Catalog]);
        AssertEqual(OnChainSupportStatus.Supported, fallbackResult.Pools.Single(pool =>
                pool.PoolKey.PoolId == v4PoolId).SupportStatus,
            "Robinhood Chain V4 discovery did not recover from an unavailable explorer.");
        Assert(fallbackHandler.SawRpcV4Lookup,
            "Robinhood Chain V4 discovery did not fall back to a bounded RPC lookup.");

        using var rangeLimitedHandler = new CatalogProtocolDiscoveryFixtureHandler(
            selectedToken,
            fixtures,
            chain: EvmChainDefinitions.RobinhoodMainnet,
            deployments: RobinhoodDeploymentRegistry.Catalog,
            blockExplorerUnavailable: true,
            maximumRpcLogBlockCount: 10);
        using var rangeLimitedHttpClient = new HttpClient(rangeLimitedHandler);
        var rangeLimitedResult = await new EthereumPoolDiscoveryService(
                new EvmJsonRpcClient(rangeLimitedHttpClient, configuration, null),
                EvmChainDefinitions.RobinhoodMainnet,
                RobinhoodDeploymentRegistry.Catalog,
                rangeLimitedHttpClient)
            .DiscoverAsync(selectedToken, [v4Catalog]);
        AssertEqual(OnChainSupportStatus.Supported, rangeLimitedResult.Pools.Single(pool =>
                pool.PoolKey.PoolId == v4PoolId).SupportStatus,
            "Robinhood Chain V4 discovery did not respect a provider's ten-block eth_getLogs limit.");
        Assert(rangeLimitedHandler.MaximumObservedRpcLogBlockCount <= 10,
            "Robinhood Chain V4 discovery requested more than ten blocks from eth_getLogs.");

        using var rateLimitedExplorerHandler = new CatalogProtocolDiscoveryFixtureHandler(
            selectedToken,
            fixtures,
            chain: EvmChainDefinitions.RobinhoodMainnet,
            deployments: RobinhoodDeploymentRegistry.Catalog,
            blockExplorerRateLimitedOnce: true);
        using var rateLimitedExplorerHttpClient = new HttpClient(rateLimitedExplorerHandler);
        var rateLimitedExplorerResult = await new EthereumPoolDiscoveryService(
                new EvmJsonRpcClient(rateLimitedExplorerHttpClient, configuration, null),
                EvmChainDefinitions.RobinhoodMainnet,
                RobinhoodDeploymentRegistry.Catalog,
                rateLimitedExplorerHttpClient)
            .DiscoverAsync(selectedToken, [v4Catalog]);
        AssertEqual(OnChainSupportStatus.Supported, rateLimitedExplorerResult.Pools.Single(pool =>
                pool.PoolKey.PoolId == v4PoolId).SupportStatus,
            "Robinhood Chain V4 discovery did not recover from a transient Blockscout rate limit.");
        Assert(!rateLimitedExplorerHandler.SawRpcV4Lookup,
            "Robinhood Chain V4 discovery used the slow RPC timestamp scan after a transient explorer rate limit.");

        using var logUnavailableHandler = new CatalogProtocolDiscoveryFixtureHandler(
            selectedToken,
            fixtures,
            chain: EvmChainDefinitions.RobinhoodMainnet,
            deployments: RobinhoodDeploymentRegistry.Catalog,
            blockExplorerLogsUnavailable: true);
        using var logUnavailableHttpClient = new HttpClient(logUnavailableHandler);
        var logUnavailableResult = await new EthereumPoolDiscoveryService(
                new EvmJsonRpcClient(logUnavailableHttpClient, configuration, null),
                EvmChainDefinitions.RobinhoodMainnet,
                RobinhoodDeploymentRegistry.Catalog,
                logUnavailableHttpClient)
            .DiscoverCatalogPoolsAsync(selectedToken, [v4Catalog], CancellationToken.None);
        AssertEqual(OnChainSupportStatus.Supported, logUnavailableResult.Pools.Single(pool =>
                pool.PoolKey.PoolId == v4PoolId).SupportStatus,
            "Robinhood Chain V4 discovery did not reuse the explorer block after its log endpoint failed.");
        AssertEqual(3, logUnavailableHandler.RpcBlockLookupCount,
            "Robinhood Chain V4 discovery repeated the slow RPC timestamp search despite an explorer block hint.");

        var creationTransaction = await new EthereumV4PoolLookupClient(
                rateLimitedExplorerHttpClient,
                EvmChainDefinitions.RobinhoodMainnet.BlockscoutApiRoot,
                RobinhoodDeploymentRegistry.UniswapV4PoolManager,
                EvmChainDefinitions.RobinhoodMainnet.DisplayName,
                null)
            .FindContractCreationTransactionAsync(
                selectedToken,
                CancellationToken.None);
        AssertEqual(v4PoolId, creationTransaction,
            "The Blockscout contract lookup returned the wrong creation transaction.");
        var indexedLog = await new EthereumV4PoolLookupClient(
                rateLimitedExplorerHttpClient,
                EvmChainDefinitions.RobinhoodMainnet.BlockscoutApiRoot,
                RobinhoodDeploymentRegistry.UniswapV4PoolManager,
                EvmChainDefinitions.RobinhoodMainnet.DisplayName,
                null)
            .FindAddressLogAsync(
                RobinhoodDeploymentRegistry.PonsV2Factory,
                v4PoolId,
                [EvmEventTopics.UniswapV4InitializeTopic, v4PoolId],
                CancellationToken.None);
        Assert(indexedLog != null
               && indexedLog.TransactionHash == v4PoolId
               && indexedLog.BlockNumber == 16
               && indexedLog.BlockHash == CatalogProtocolDiscoveryFixtureHandler.BlockHash,
            "The Blockscout indexed-log lookup lost transaction or canonical-block identity.");

        var filters = EvmWebSocketStreamSource.CreateRpcLogFilters(
            result.Pools.Select(pool => new OnChainWatchedPoolSelection
            {
                Descriptor = pool,
                SelectedMint = selectedToken
            }).ToArray(),
            1,
            2);
        var filterJson = JsonSerializer.Serialize(filters);
        AssertEqual(2, filters.Length,
            "Robinhood Chain stream filters did not separate address pools from V4 PoolIds.");
        Assert(filterJson.Contains(EvmEventTopics.UniswapV2SwapTopic, StringComparison.Ordinal)
               && filterJson.Contains(EvmEventTopics.UniswapV3SwapTopic, StringComparison.Ordinal)
               && filterJson.Contains(EvmEventTopics.UniswapV4SwapTopic, StringComparison.Ordinal)
               && filterJson.Contains(v4PoolId, StringComparison.Ordinal),
            "Robinhood Chain stream filters lost a canonical Uniswap event family.");

        var client = new OnChainEngineClient(enginePath);
        try
        {
            await client.ReplaceWatchedPoolsAsync(result.Pools.Select(pool =>
                new OnChainWatchedPoolSelection
                {
                    Descriptor = pool,
                    SelectedMint = selectedToken
                }).ToArray());
            AssertEqual(OnChainEngineState.Ready, client.State,
                "The shared engine rejected validated Robinhood Chain Uniswap deployments.");
        }
        finally
        {
            await client.StopAsync();
        }
    }
    static async Task VerifyBasePancakeDiscoveryAndEngineAsync(string enginePath)
    {
        const string selectedToken = "0x1111111111111111111111111111111111111111";
        const string v2Pair = "0x2222222222222222222222222222222222222222";
        const string v3Pool = "0x3333333333333333333333333333333333333333";
        const string clPoolId =
            "0x4444444444444444444444444444444444444444444444444444444444444444";
        const string binPoolId =
            "0x5555555555555555555555555555555555555555555555555555555555555555";
        var fixtures = new[]
        {
            new EvmCatalogPoolFixture(
                v2Pair,
                BaseDeploymentRegistry.WrappedEther,
                OnChainProtocolIds.PancakeV2,
                BaseDeploymentRegistry.PancakeV2Factory),
            new EvmCatalogPoolFixture(
                v3Pool,
                BaseDeploymentRegistry.Usdc,
                OnChainProtocolIds.PancakeV3,
                BaseDeploymentRegistry.PancakeV3Factory,
                FeeTier: 2500,
                TickSpacing: 50),
            new EvmCatalogPoolFixture(
                clPoolId,
                BaseDeploymentRegistry.Usdc,
                OnChainProtocolIds.PancakeInfinityCl,
                BaseDeploymentRegistry.PancakeInfinityClPoolManager,
                FeeTier: 1,
                TickSpacing: 1),
            new EvmCatalogPoolFixture(
                binPoolId,
                BaseDeploymentRegistry.Usdc,
                OnChainProtocolIds.PancakeInfinityBin,
                BaseDeploymentRegistry.PancakeInfinityBinPoolManager,
                FeeTier: 4,
                TickSpacing: 1)
        };
        var catalogPools = fixtures
            .Where(static fixture => fixture.ProtocolId is OnChainProtocolIds.PancakeInfinityCl
                or OnChainProtocolIds.PancakeInfinityBin)
            .Select(fixture => new PoolCatalogEntry
            {
                SourceId = "pancake-explorer",
                ChainId = "base",
                ProtocolId = fixture.ProtocolId,
                PoolAddress = fixture.PoolAddress,
                Labels = [fixture.ProtocolId],
                BaseAsset = new PoolCatalogAsset
                {
                    Address = selectedToken,
                    Symbol = "TOKEN"
                },
                QuoteAsset = new PoolCatalogAsset
                {
                    Address = fixture.QuoteAddress,
                    Symbol = "USDC"
                },
                LiquidityUsd = 1000
            })
            .ToArray();

        using var handler = new CatalogProtocolDiscoveryFixtureHandler(
            selectedToken,
            fixtures,
            chain: EvmChainDefinitions.BaseMainnet,
            deployments: BaseDeploymentRegistry.Catalog);
        using var httpClient = new HttpClient(handler);
        var configuration = CreateProviderConfiguration(
            OnChainProviderTypes.BasePublic,
            "wss://mainnet.base.org",
            "https://mainnet.base.org");
        var result = await new EthereumPoolDiscoveryService(
                new EvmJsonRpcClient(httpClient, configuration, null),
                EvmChainDefinitions.BaseMainnet,
                BaseDeploymentRegistry.Catalog,
                httpClient)
            .DiscoverAsync(selectedToken, catalogPools);

        AssertEqual(4, result.Pools.Length,
            "Base discovery did not retain PancakeSwap V2, V3, Infinity CL, and Infinity bin.");
        AssertEqual(v2Pair, result.Pools.Single(pool =>
                pool.PoolKey.ProtocolId == OnChainProtocolIds.PancakeV2).PoolKey.PoolId,
            "PancakeSwap V2 factory discovery returned the wrong Base pair.");
        var v3 = result.Pools.Single(pool => pool.PoolKey.ProtocolId == OnChainProtocolIds.PancakeV3);
        AssertEqual(v3Pool, v3.PoolKey.PoolId,
            "PancakeSwap V3 factory discovery returned the wrong Base pool.");
        AssertEqual(2500U, v3.FeeTier,
            "Base PancakeSwap V3 discovery did not use the protocol-specific 0.25% fee tier.");
        AssertEqual(clPoolId, result.Pools.Single(pool =>
                pool.PoolKey.ProtocolId == OnChainProtocolIds.PancakeInfinityCl).PoolKey.PoolId,
            "PancakeSwap Infinity CL discovery returned the wrong Base PoolId.");
        AssertEqual(binPoolId, result.Pools.Single(pool =>
                pool.PoolKey.ProtocolId == OnChainProtocolIds.PancakeInfinityBin).PoolKey.PoolId,
            "PancakeSwap Infinity bin discovery returned the wrong Base PoolId.");
        Assert(!handler.SawUnpinnedStateRead,
            "Base PancakeSwap discovery performed a mutable-latest state read.");
        Assert(result.Pools.All(pool => pool.SupportStatus == OnChainSupportStatus.Supported),
            "Base PancakeSwap discovery returned an unsupported fixture.");

        var filters = EvmWebSocketStreamSource.CreateRpcLogFilters(
            result.Pools.Select(pool => new OnChainWatchedPoolSelection
            {
                Descriptor = pool,
                SelectedMint = selectedToken
            }).ToArray(),
            1,
            2);
        var filterJson = JsonSerializer.Serialize(filters);
        AssertEqual(3, filters.Length,
            "Base PancakeSwap filters did not split address pools from both singleton managers.");
        Assert(filterJson.Contains(EvmEventTopics.UniswapV2SwapTopic, StringComparison.Ordinal)
               && filterJson.Contains(EvmEventTopics.PancakeV3SwapTopic, StringComparison.Ordinal)
               && filterJson.Contains(EvmEventTopics.PancakeInfinityClSwapTopic, StringComparison.Ordinal)
               && filterJson.Contains(EvmEventTopics.PancakeInfinityBinSwapTopic, StringComparison.Ordinal)
               && filterJson.Contains(clPoolId, StringComparison.Ordinal)
               && filterJson.Contains(binPoolId, StringComparison.Ordinal),
            "Base stream filters lost a PancakeSwap event family or indexed PoolId.");

        var catalogPayload = System.Text.Encoding.UTF8.GetBytes($$"""
            {
              "rows": [
                {
                  "chainId": 8453,
                  "protocol": "infinityCl",
                  "id": "{{clPoolId}}",
                  "token0": { "id": "{{selectedToken}}", "name": "Token", "symbol": "TOKEN" },
                  "token1": { "id": "{{BaseDeploymentRegistry.Usdc}}", "name": "USD Coin", "symbol": "USDC" },
                  "tvlUSD": "1000.5",
                  "volumeUSD24h": "42"
                },
                {
                  "chainId": 56,
                  "protocol": "infinityCl",
                  "id": "{{binPoolId}}",
                  "token0": { "id": "{{selectedToken}}" },
                  "token1": { "id": "{{BaseDeploymentRegistry.Usdc}}" }
                }
              ]
            }
            """);
        var parsedCatalog = PancakeInfinityPoolCatalogClient.Parse(
            catalogPayload,
            "infinityCl",
            selectedToken,
            BaseDeploymentRegistry.Catalog);
        AssertEqual(1, parsedCatalog.Length,
            "PancakeSwap Explorer parsing did not constrain Base chain, protocol, and token.");
        AssertEqual("base", parsedCatalog[0].ChainId,
            "PancakeSwap Explorer parsing returned the wrong Base catalog identity.");
        AssertEqual(1, BaseDeploymentRegistry.GetCatalogPoolFamilies(parsedCatalog[0]).Length,
            "Parsed Base Infinity entries did not route to an allowlisted manager.");

        using (var pancakeHandler = new PancakeCatalogFixtureHandler(
                   selectedToken,
                   BaseDeploymentRegistry.Catalog))
        using (var pancakeHttpClient = new HttpClient(pancakeHandler)
               {
                   BaseAddress = new Uri("https://explorer.pancakeswap.com/")
               })
        {
            var pancakeClient = new PancakeInfinityPoolCatalogClient(pancakeHttpClient);
            var pagedCatalog = await pancakeClient.SearchAsync(
                BaseDeploymentRegistry.Catalog,
                selectedToken);
            AssertEqual(2, pagedCatalog.Pools.Length,
                "Bounded PancakeSwap Explorer pagination did not find both Base Infinity families.");
            Assert(pancakeHandler.SawExpectedChainQuery,
                "PancakeSwap Explorer search did not request the Base catalog.");
        }

        var client = new OnChainEngineClient(enginePath);
        try
        {
            await client.ReplaceWatchedPoolsAsync(result.Pools.Select(pool =>
                new OnChainWatchedPoolSelection
                {
                    Descriptor = pool,
                    SelectedMint = selectedToken
                }).ToArray());
            AssertEqual(OnChainEngineState.Ready, client.State,
                "The shared engine rejected validated Base PancakeSwap deployments.");
        }
        finally
        {
            await client.StopAsync();
        }
    }
    static async Task VerifyBnbPancakeDiscoveryAndEngineAsync(string enginePath)
    {
        const string selectedToken = "0x1111111111111111111111111111111111111111";
        const string v2Pair = "0x2222222222222222222222222222222222222222";
        const string v3Pool = "0x3333333333333333333333333333333333333333";
        const string clPoolId =
            "0x4444444444444444444444444444444444444444444444444444444444444444";
        const string binPoolId =
            "0x5555555555555555555555555555555555555555555555555555555555555555";
        const string uniswapV2Pair = "0x6666666666666666666666666666666666666666";
        const string uniswapV3Pool = "0x7777777777777777777777777777777777777777";
        const string uniswapV4PoolId =
            "0x8888888888888888888888888888888888888888888888888888888888888888";
        var fixtures = new[]
        {
            new EvmCatalogPoolFixture(
                v2Pair,
                BnbDeploymentRegistry.WrappedBnb,
                OnChainProtocolIds.PancakeV2,
                BnbDeploymentRegistry.PancakeV2Factory),
            new EvmCatalogPoolFixture(
                v3Pool,
                BnbDeploymentRegistry.WrappedBnb,
                OnChainProtocolIds.PancakeV3,
                BnbDeploymentRegistry.PancakeV3Factory,
                FeeTier: 500,
                TickSpacing: 10),
            new EvmCatalogPoolFixture(
                clPoolId,
                BnbDeploymentRegistry.Usdt,
                OnChainProtocolIds.PancakeInfinityCl,
                BnbDeploymentRegistry.PancakeInfinityClPoolManager,
                FeeTier: 1,
                TickSpacing: 1),
            new EvmCatalogPoolFixture(
                binPoolId,
                BnbDeploymentRegistry.Usdt,
                OnChainProtocolIds.PancakeInfinityBin,
                BnbDeploymentRegistry.PancakeInfinityBinPoolManager,
                FeeTier: 4,
                TickSpacing: 1),
            new EvmCatalogPoolFixture(
                uniswapV2Pair,
                BnbDeploymentRegistry.WrappedBnb,
                OnChainProtocolIds.UniswapV2,
                BnbDeploymentRegistry.UniswapV2Factory),
            new EvmCatalogPoolFixture(
                uniswapV3Pool,
                BnbDeploymentRegistry.WrappedBnb,
                OnChainProtocolIds.UniswapV3,
                BnbDeploymentRegistry.UniswapV3Factory,
                FeeTier: 3000,
                TickSpacing: 60),
            new EvmCatalogPoolFixture(
                uniswapV4PoolId,
                BnbDeploymentRegistry.Usdt,
                OnChainProtocolIds.UniswapV4,
                BnbDeploymentRegistry.UniswapV4PoolManager,
                FeeTier: 500,
                TickSpacing: 10,
                CreatedAtUnixMs: 1000)
        };
        var catalogPools = fixtures
            .Where(static fixture => fixture.ProtocolId is OnChainProtocolIds.PancakeInfinityCl
                or OnChainProtocolIds.PancakeInfinityBin
                or OnChainProtocolIds.UniswapV4)
            .Select(fixture => new PoolCatalogEntry
            {
                SourceId = "pancake-explorer",
                ChainId = "bsc",
                ProtocolId = fixture.ProtocolId == OnChainProtocolIds.UniswapV4
                    ? "uniswap"
                    : fixture.ProtocolId,
                PoolAddress = fixture.PoolAddress,
                Labels = fixture.ProtocolId == OnChainProtocolIds.UniswapV4
                    ? ["v4"]
                    : [fixture.ProtocolId],
                BaseAsset = new PoolCatalogAsset
                {
                    Address = selectedToken,
                    Symbol = "TOKEN"
                },
                QuoteAsset = new PoolCatalogAsset
                {
                    Address = fixture.QuoteAddress,
                    Symbol = "USDT"
                },
                LiquidityUsd = 1000,
                CreatedAtUnixMs = fixture.CreatedAtUnixMs > 0 ? fixture.CreatedAtUnixMs : null
            })
            .ToArray();

        using var handler = new CatalogProtocolDiscoveryFixtureHandler(
            selectedToken,
            fixtures,
            chain: EvmChainDefinitions.BnbMainnet,
            deployments: BnbDeploymentRegistry.Catalog);
        using var httpClient = new HttpClient(handler);
        var configuration = CreateProviderConfiguration(
            OnChainProviderTypes.PublicNodeBnb,
            "wss://bsc-rpc.publicnode.com",
            "https://bsc-rpc.publicnode.com");
        var result = await new EthereumPoolDiscoveryService(
                new EvmJsonRpcClient(httpClient, configuration, null),
                EvmChainDefinitions.BnbMainnet,
                BnbDeploymentRegistry.Catalog,
                httpClient)
            .DiscoverAsync(selectedToken, catalogPools);

        AssertEqual(7, result.Pools.Length,
            "BNB discovery did not retain PancakeSwap V2, V3, Infinity CL/bin and Uniswap V2-V4.");
        Assert(result.Pools.All(pool => pool.PoolKey.ChainId == EvmChainDefinitions.BnbMainnetChainId),
            "BNB discovery emitted a pool with the wrong chain identity.");
        AssertEqual(v2Pair, result.Pools.Single(pool =>
                pool.PoolKey.ProtocolId == OnChainProtocolIds.PancakeV2).PoolKey.PoolId,
            "PancakeSwap V2 factory discovery returned the wrong pair.");
        AssertEqual(v3Pool, result.Pools.Single(pool =>
                pool.PoolKey.ProtocolId == OnChainProtocolIds.PancakeV3).PoolKey.PoolId,
            "PancakeSwap V3 factory discovery returned the wrong pool.");
        var cl = result.Pools.Single(pool =>
            pool.PoolKey.ProtocolId == OnChainProtocolIds.PancakeInfinityCl);
        AssertEqual(1, cl.TickSpacing,
            "PancakeSwap Infinity CL pool-key tick spacing was not preserved.");
        var bin = result.Pools.Single(pool =>
            pool.PoolKey.ProtocolId == OnChainProtocolIds.PancakeInfinityBin);
        AssertEqual<ushort?>(1, bin.BinStep,
            "PancakeSwap Infinity bin pool-key step was not preserved.");
        AssertEqual(uniswapV2Pair, result.Pools.Single(pool =>
                pool.PoolKey.ProtocolId == OnChainProtocolIds.UniswapV2).PoolKey.PoolId,
            "Uniswap V2 factory discovery returned the wrong BNB pair.");
        AssertEqual(uniswapV3Pool, result.Pools.Single(pool =>
                pool.PoolKey.ProtocolId == OnChainProtocolIds.UniswapV3).PoolKey.PoolId,
            "Uniswap V3 factory discovery returned the wrong BNB pool.");
        AssertEqual(uniswapV4PoolId, result.Pools.Single(pool =>
                pool.PoolKey.ProtocolId == OnChainProtocolIds.UniswapV4).PoolKey.PoolId,
            "Uniswap V4 catalog discovery returned the wrong BNB PoolId.");
        Assert(!handler.SawUnpinnedStateRead,
            "BNB PancakeSwap discovery performed a mutable-latest state read.");
        Assert(result.Pools.All(pool => pool.SupportStatus == OnChainSupportStatus.Supported),
            "BNB discovery returned an unsupported fixture: " + string.Join(" | ", result.Pools
                .Where(pool => pool.SupportStatus != OnChainSupportStatus.Supported)
                .Select(pool => $"{pool.PoolKey.ProtocolId}: {pool.SupportReason}")));

        var filters = EvmWebSocketStreamSource.CreateRpcLogFilters(
            result.Pools.Select(pool => new OnChainWatchedPoolSelection
            {
                Descriptor = pool,
                SelectedMint = selectedToken
            }).ToArray(),
            1,
            2);
        var filterJson = JsonSerializer.Serialize(filters);
        AssertEqual(4, filters.Length,
            "BNB stream filters did not split address pools from all three singleton managers.");
        Assert(filterJson.Contains(EvmEventTopics.UniswapV2SwapTopic, StringComparison.Ordinal)
               && filterJson.Contains(EvmEventTopics.UniswapV3SwapTopic, StringComparison.Ordinal)
               && filterJson.Contains(EvmEventTopics.UniswapV4SwapTopic, StringComparison.Ordinal)
               && filterJson.Contains(EvmEventTopics.PancakeV3SwapTopic, StringComparison.Ordinal)
               && filterJson.Contains(EvmEventTopics.PancakeInfinityClSwapTopic, StringComparison.Ordinal)
               && filterJson.Contains(EvmEventTopics.PancakeInfinityBinSwapTopic, StringComparison.Ordinal)
               && filterJson.Contains(clPoolId, StringComparison.Ordinal)
               && filterJson.Contains(binPoolId, StringComparison.Ordinal)
               && filterJson.Contains(uniswapV4PoolId, StringComparison.Ordinal),
            "BNB stream filters lost a PancakeSwap or Uniswap event family or indexed PoolId.");

        var catalogPayload = System.Text.Encoding.UTF8.GetBytes($$"""
            {
              "rows": [
                {
                  "chainId": 56,
                  "protocol": "infinityCl",
                  "id": "{{clPoolId}}",
                  "token0": { "id": "{{selectedToken}}", "name": "Token", "symbol": "TOKEN" },
                  "token1": { "id": "{{BnbDeploymentRegistry.Usdt}}", "name": "Tether USD", "symbol": "USDT" },
                  "tvlUSD": "1000.5",
                  "volumeUSD24h": "42"
                },
                {
                  "chainId": 1,
                  "protocol": "infinityCl",
                  "id": "{{binPoolId}}",
                  "token0": { "id": "{{selectedToken}}" },
                  "token1": { "id": "{{BnbDeploymentRegistry.Usdt}}" }
                }
              ]
            }
            """);
        var parsedCatalog = PancakeInfinityPoolCatalogClient.Parse(
            catalogPayload,
            "infinityCl",
            selectedToken);
        AssertEqual(1, parsedCatalog.Length,
            "PancakeSwap Explorer parsing did not constrain chain, protocol, and selected token.");
        AssertEqual(OnChainProtocolIds.PancakeInfinityCl, parsedCatalog[0].ProtocolId,
            "PancakeSwap Explorer parsing returned the wrong internal protocol identity.");
        AssertEqual(1000.5, parsedCatalog[0].LiquidityUsd,
            "PancakeSwap Explorer parsing lost invariant-culture TVL metadata.");
        AssertEqual(1, BnbDeploymentRegistry.GetCatalogPoolFamilies(parsedCatalog[0]).Length,
            "Parsed PancakeSwap Infinity entries did not route to an allowlisted manager.");

        using (var pancakeHandler = new PancakeCatalogFixtureHandler(selectedToken))
        using (var pancakeHttpClient = new HttpClient(pancakeHandler)
               {
                   BaseAddress = new Uri("https://explorer.pancakeswap.com/")
               })
        {
            var pancakeClient = new PancakeInfinityPoolCatalogClient(pancakeHttpClient);
            var concurrentCatalogs = await Task.WhenAll(
                pancakeClient.SearchAsync(selectedToken),
                pancakeClient.SearchAsync(selectedToken));
            var pagedCatalog = concurrentCatalogs[0];
            AssertEqual(2, pagedCatalog.Pools.Length,
                "Bounded PancakeSwap Explorer pagination did not find both Infinity families.");
            AssertEqual(4, pancakeHandler.RequestCount,
                "Concurrent PancakeSwap Explorer searches were not coalesced or bounded to matching pages.");
            _ = await pancakeClient.SearchAsync(selectedToken);
            AssertEqual(4, pancakeHandler.RequestCount,
                "PancakeSwap Explorer search did not reuse its bounded cache.");
        }

        var client = new OnChainEngineClient(enginePath);
        try
        {
            await client.ReplaceWatchedPoolsAsync(result.Pools.Select(pool =>
                new OnChainWatchedPoolSelection
                {
                    Descriptor = pool,
                    SelectedMint = selectedToken
                }).ToArray());
            AssertEqual(OnChainEngineState.Ready, client.State,
                "The shared engine rejected a validated BNB PancakeSwap deployment.");
        }
        finally
        {
            await client.StopAsync();
        }
    }
    static async Task VerifyBaseAerodromeDiscoveryAndEngineAsync(string enginePath)
    {
        var fixtures = new[]
        {
            new EvmCatalogPoolFixture(
                "0xcdac0d6c6c59727a65f871236188350531885c43",
                BaseDeploymentRegistry.Usdc,
                OnChainProtocolIds.AerodromeClassic,
                BaseDeploymentRegistry.AerodromeClassicFactory,
                Stable: false),
            new EvmCatalogPoolFixture(
                "0x3548029694fbb241d45fb24ba0cd9c9d4e745f16",
                BaseDeploymentRegistry.Usdc,
                OnChainProtocolIds.AerodromeClassic,
                BaseDeploymentRegistry.AerodromeClassicFactory,
                Stable: true),
            new EvmCatalogPoolFixture(
                "0xb2cc224c1c9fee385f8ad6a55b4d94e92359dc59",
                BaseDeploymentRegistry.Usdc,
                OnChainProtocolIds.AerodromeSlipstream,
                BaseDeploymentRegistry.AerodromeSlipstreamInitialFactory,
                TickSpacing: 100),
            new EvmCatalogPoolFixture(
                "0xc758d81b9b81a6fcdad075bd471874a2c46b54e0",
                BaseDeploymentRegistry.Usdc,
                OnChainProtocolIds.AerodromeSlipstream,
                BaseDeploymentRegistry.AerodromeSlipstreamGaugeCapsFactory,
                TickSpacing: 50),
            new EvmCatalogPoolFixture(
                "0x3fe04a59ebd38cf06080a6f60a98d124eb59392a",
                BaseDeploymentRegistry.Usdc,
                OnChainProtocolIds.AerodromeSlipstream,
                BaseDeploymentRegistry.AerodromeSlipstreamGaugesV3Factory,
                TickSpacing: 50)
        };
        using var handler = new CatalogProtocolDiscoveryFixtureHandler(
            BaseDeploymentRegistry.WrappedEther,
            fixtures,
            chain: EvmChainDefinitions.BaseMainnet,
            deployments: BaseDeploymentRegistry.Catalog);
        using var httpClient = new HttpClient(handler);
        var configuration = CreateProviderConfiguration(
            OnChainProviderTypes.BasePublic,
            "wss://mainnet.base.org",
            "https://mainnet.base.org");
        var result = await new EthereumPoolDiscoveryService(
                new EvmJsonRpcClient(httpClient, configuration, null),
                EvmChainDefinitions.BaseMainnet,
                BaseDeploymentRegistry.Catalog,
                httpClient)
            .DiscoverAerodromeAsync(BaseDeploymentRegistry.WrappedEther);

        AssertEqual(5, result.Pools.Length,
            "Base Aerodrome discovery did not return both classic curves and all Slipstream generations.");
        var classic = result.Pools
            .Where(pool => pool.PoolKey.ProtocolId == OnChainProtocolIds.AerodromeClassic)
            .ToArray();
        AssertEqual(2, classic.Length, "Aerodrome classic discovery returned the wrong pool count.");
        Assert(classic.Select(pool => pool.PoolType).ToHashSet(StringComparer.Ordinal)
                .SetEquals(["volatile", "stable"]),
            "Aerodrome classic descriptors did not retain exact curve identity.");
        var slipstream = result.Pools
            .Where(pool => pool.PoolKey.ProtocolId == OnChainProtocolIds.AerodromeSlipstream)
            .ToArray();
        AssertEqual(3, slipstream.Length, "Slipstream discovery returned the wrong generation count.");
        Assert(slipstream.All(pool => pool.FeeTier == null && pool.TickSpacing > 0),
            "Slipstream dynamic fee leaked into descriptor identity or tick spacing was lost.");
        Assert(slipstream.Select(pool => pool.ProgramId).ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(BaseDeploymentRegistry.AerodromeSlipstreamFamilies.Select(family => family.FactoryAddress)),
            "Slipstream discovery did not retain every fixed factory generation.");
        Assert(!handler.SawUnpinnedStateRead,
            "Aerodrome discovery performed a mutable-latest state read.");
        var filters = EvmWebSocketStreamSource.CreateRpcLogFilters(
            result.Pools.Select(pool => new OnChainWatchedPoolSelection
            {
                Descriptor = pool,
                SelectedMint = BaseDeploymentRegistry.WrappedEther
            }).ToArray(),
            1,
            2);
        var filterJson = JsonSerializer.Serialize(filters);
        Assert(filters.Length == 1
               && filterJson.Contains(EvmEventTopics.AerodromeClassicSwapTopic, StringComparison.Ordinal)
               && filterJson.Contains(EvmEventTopics.AerodromeClassicSyncTopic, StringComparison.Ordinal)
               && filterJson.Contains(EvmEventTopics.UniswapV3SwapTopic, StringComparison.Ordinal),
            "Aerodrome stream filters did not retain the classic and Slipstream event families.");

        var sixWordSlot0 = "0x" + string.Concat(new[]
        {
            (System.Numerics.BigInteger.One << 96).ToString("x64"),
            0.ToString("x64"),
            0.ToString("x64"),
            0.ToString("x64"),
            0.ToString("x64"),
            1.ToString("x64")
        });
        Assert(EthereumAbi.TryDecodeAerodromeSlipstreamSlot0(sixWordSlot0, out _, out _),
            "The exact six-word Slipstream slot0 shape was rejected.");
        Assert(!EthereumAbi.TryDecodeAerodromeSlipstreamSlot0(
                sixWordSlot0 + 0.ToString("x64"), out _, out _),
            "The Slipstream slot0 decoder accepted a seventh word.");

        var client = new OnChainEngineClient(enginePath);
        var prices = new ConcurrentQueue<OnChainPriceUpdate>();
        client.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
        try
        {
            await client.ReplaceWatchedPoolsAsync(result.Pools.Select(pool =>
                new OnChainWatchedPoolSelection
                {
                    Descriptor = pool,
                    SelectedMint = BaseDeploymentRegistry.WrappedEther
                }).ToArray());
            AssertEqual(OnChainEngineState.Ready, client.State,
                "The engine rejected a mixed five-pool Aerodrome watch set.");

            var stable = classic.Single(pool => pool.PoolType == "stable");
            await client.PublishEvmSnapshotAsync(new EvmSnapshotResponse
            {
                RequestId = "aerodrome-stable-snapshot",
                PoolKey = stable.PoolKey,
                BlockNumber = 20,
                BlockHash = Hash(20),
                Calls =
                [
                    new EvmSnapshotCall
                    {
                        Id = "getReserves",
                        Success = true,
                        ReturnData = AbiBigWords(
                            System.Numerics.BigInteger.Pow(10, 18),
                            System.Numerics.BigInteger.Pow(10, 6),
                            1)
                    }
                ]
            });
            await WaitUntilAsync(() => prices.Count == 1, TimeSpan.FromSeconds(5),
                "Aerodrome stable snapshot price");
            AssertEqual("1000000000000000000", prices.Last().SpotPriceQuote?.Coefficient,
                "Aerodrome stable marginal spot price is wrong at peg.");

            await client.PublishEvmLogAsync(new EvmLogUpdate
            {
                ChainId = EvmChainDefinitions.BaseMainnetChainId,
                ConnectionEpoch = 10,
                Address = stable.PoolKey.PoolId,
                Topics =
                [
                    EvmEventTopics.AerodromeClassicSwapTopic,
                    Hash(5000),
                    Hash(5001)
                ],
                Data = AbiBigWords(
                    System.Numerics.BigInteger.Pow(10, 18),
                    0,
                    0,
                    2 * System.Numerics.BigInteger.Pow(10, 6)),
                BlockNumber = 21,
                BlockHash = Hash(21),
                TransactionHash = Hash(5002),
                TransactionIndex = 1,
                LogIndex = 2,
                ObservedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
            await WaitUntilAsync(() => prices.Count == 2, TimeSpan.FromSeconds(5),
                "Aerodrome classic swap price");
            AssertEqual("2000000000000000000", prices.Last().LastTradePriceQuote?.Coefficient,
                "Aerodrome classic executed price is wrong.");

            var initial = slipstream.Single(pool =>
                pool.ProgramId.Equals(
                    BaseDeploymentRegistry.AerodromeSlipstreamInitialFactory,
                    StringComparison.OrdinalIgnoreCase));
            var q96 = System.Numerics.BigInteger.One << 96;
            await client.PublishEvmSnapshotAsync(new EvmSnapshotResponse
            {
                RequestId = "slipstream-snapshot",
                PoolKey = initial.PoolKey,
                BlockNumber = 22,
                BlockHash = Hash(22),
                Calls =
                [
                    new EvmSnapshotCall
                    {
                        Id = "slot0",
                        Success = true,
                        ReturnData = AbiBigWords(q96, 0, 0, 0, 0, 1)
                    },
                    new EvmSnapshotCall
                    {
                        Id = "liquidity",
                        Success = true,
                        ReturnData = AbiBigWords(100)
                    }
                ]
            });
            await WaitUntilAsync(() => prices.Count == 3, TimeSpan.FromSeconds(5),
                "Slipstream snapshot price");
            AssertEqual("1000000000000000000000000000000", prices.Last().SpotPriceQuote?.Coefficient,
                "Slipstream six-word slot0 spot price is wrong.");

            await client.PublishEvmLogAsync(new EvmLogUpdate
            {
                ChainId = EvmChainDefinitions.BaseMainnetChainId,
                ConnectionEpoch = 10,
                Address = initial.PoolKey.PoolId,
                Topics = [EvmEventTopics.UniswapV3SwapTopic, Hash(5010), Hash(5011)],
                Data = AbiBigWords(
                    System.Numerics.BigInteger.Pow(10, 18),
                    -2 * System.Numerics.BigInteger.Pow(10, 6),
                    q96,
                    100,
                    0),
                BlockNumber = 23,
                BlockHash = Hash(23),
                TransactionHash = Hash(5012),
                TransactionIndex = 1,
                LogIndex = 2,
                ObservedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
            await WaitUntilAsync(() => prices.Count == 4, TimeSpan.FromSeconds(5),
                "Slipstream swap price");
            AssertEqual("2000000000000000000", prices.Last().LastTradePriceQuote?.Coefficient,
                "Slipstream executed price is wrong.");
        }
        finally
        {
            await client.StopAsync();
        }
    }
    static async Task VerifyBaseUniswapV4DiscoveryAsync()
    {
        const string selectedToken = "0x1111111111111111111111111111111111111111";
        const string poolId = "0xeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        var fixture = new EvmCatalogPoolFixture(
            poolId,
            BaseDeploymentRegistry.NativeEther,
            OnChainProtocolIds.UniswapV4,
            BaseDeploymentRegistry.UniswapV4PoolManager,
            3000,
            60,
            32000);
        var catalog = new PoolCatalogEntry
        {
            SourceId = "dexscreener",
            ChainId = "base",
            ProtocolId = "uniswap",
            PoolAddress = poolId,
            Labels = ["v4"],
            BaseAsset = new PoolCatalogAsset { Address = selectedToken },
            QuoteAsset = new PoolCatalogAsset { Address = BaseDeploymentRegistry.NativeEther },
            CreatedAtUnixMs = fixture.CreatedAtUnixMs
        };
        using var handler = new CatalogProtocolDiscoveryFixtureHandler(
            selectedToken,
            [fixture],
            chain: EvmChainDefinitions.BaseMainnet,
            deployments: BaseDeploymentRegistry.Catalog);
        using var httpClient = new HttpClient(handler);
        var configuration = CreateProviderConfiguration(
            OnChainProviderTypes.BasePublic,
            "wss://mainnet.base.org",
            "https://mainnet.base.org");
        var result = await new EthereumPoolDiscoveryService(
                new EvmJsonRpcClient(httpClient, configuration, null),
                EvmChainDefinitions.BaseMainnet,
                BaseDeploymentRegistry.Catalog,
                httpClient)
            .DiscoverAsync(selectedToken, [catalog]);
        var descriptor = result.Pools.Single(pool =>
            pool.PoolKey.ProtocolId == OnChainProtocolIds.UniswapV4);
        AssertEqual(EvmChainDefinitions.BaseMainnetChainId, descriptor.PoolKey.ChainId,
            "Base V4 discovery emitted the wrong chain identity.");
        AssertEqual(BaseDeploymentRegistry.UniswapV4PoolManager, descriptor.ProgramId,
            "Base V4 discovery emitted the wrong PoolManager.");
        AssertEqual(OnChainSupportStatus.Supported, descriptor.SupportStatus,
            "A zero-hook Base V4 pool was not selectable.");
        AssertEqual(BaseDeploymentRegistry.NativeEther, descriptor.HookAddress,
            "The Base V4 zero-hook identity was not preserved.");
        Assert(handler.SawExpectedBlockscoutApiRoot,
            "Base V4 discovery did not use the documented Base Blockscout API root.");

        var hookedFixture = fixture with
        {
            HookAddress = "0x0000000000000000000000000000000000000001"
        };
        using var hookedHandler = new CatalogProtocolDiscoveryFixtureHandler(
            selectedToken,
            [hookedFixture],
            chain: EvmChainDefinitions.BaseMainnet,
            deployments: BaseDeploymentRegistry.Catalog);
        using var hookedHttpClient = new HttpClient(hookedHandler);
        var hookedResult = await new EthereumPoolDiscoveryService(
                new EvmJsonRpcClient(hookedHttpClient, configuration, null),
                EvmChainDefinitions.BaseMainnet,
                BaseDeploymentRegistry.Catalog,
                hookedHttpClient)
            .DiscoverAsync(selectedToken, [catalog]);
        var hookedDescriptor = hookedResult.Pools.Single(pool =>
            pool.PoolKey.ProtocolId == OnChainProtocolIds.UniswapV4);
        AssertEqual(OnChainSupportStatus.DiscoveredUnsupported, hookedDescriptor.SupportStatus,
            "A nonzero-hook Base V4 pool was not rejected.");
        Assert(hookedDescriptor.SupportReason?.Contains("zero-hook", StringComparison.Ordinal) == true,
            "A nonzero-hook Base V4 pool did not report the strict Base rule.");
    }
    static async Task VerifyCatalogProtocolDiscoveryAndEngineAsync(string enginePath)
    {
        const string selectedToken = "0x1111111111111111111111111111111111111111";
        var fixtures = new[]
        {
            new EvmCatalogPoolFixture(
                "0x2222222222222222222222222222222222222222",
                "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                OnChainProtocolIds.UniswapV2,
                EthereumDeploymentRegistry.UniswapV2Factory),
            new EvmCatalogPoolFixture(
                "0x3333333333333333333333333333333333333333",
                "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                OnChainProtocolIds.UniswapV3,
                EthereumDeploymentRegistry.UniswapV3Factory,
                500,
                10),
            new EvmCatalogPoolFixture(
                "0x4444444444444444444444444444444444444444",
                "0xcccccccccccccccccccccccccccccccccccccccc",
                OnChainProtocolIds.UniswapV2,
                EthereumDeploymentRegistry.ShibaSwapV1Factory),
            new EvmCatalogPoolFixture(
                "0x5555555555555555555555555555555555555555",
                "0xdddddddddddddddddddddddddddddddddddddddd",
                OnChainProtocolIds.UniswapV3,
                EthereumDeploymentRegistry.ShibaSwapV2Factory,
                3000,
                60),
            new EvmCatalogPoolFixture(
                "0xeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                EthereumDeploymentRegistry.NativeEther,
                OnChainProtocolIds.UniswapV4,
                EthereumDeploymentRegistry.UniswapV4PoolManager,
                10000,
                200,
                32000)
        };
        using var handler = new CatalogProtocolDiscoveryFixtureHandler(
            selectedToken,
            fixtures,
            poolCodeDelay: TimeSpan.FromMilliseconds(30));
        using var httpClient = new HttpClient(handler);
        var configuration = CreateProviderConfiguration(
            OnChainProviderTypes.PublicNodeEthereum,
            "wss://ethereum-rpc.publicnode.com",
            "https://ethereum-rpc.publicnode.com");
        var discovery = new EthereumPoolDiscoveryService(
            new EvmJsonRpcClient(httpClient, configuration, null),
            httpClient);
        var catalog = new[]
        {
            CatalogPool("uniswap", "v2", fixtures[0]),
            CatalogPool("uniswap", "v3", fixtures[1]),
            CatalogPool("shibaswap", null, fixtures[2]),
            CatalogPool(EthereumDeploymentRegistry.ShibaSwapV2Factory, null, fixtures[3]),
            CatalogPool("uniswap", "v4", fixtures[4])
        };

        var result = await discovery.DiscoverCatalogPoolsSharedAsync(
            selectedToken,
            catalog,
            CancellationToken.None);
        AssertEqual(1, handler.GetRpcMethodCount("eth_chainId"),
            "Catalog validation repeated the chain preflight for individual pools.");
        AssertEqual(2, handler.RpcBlockLookupCount,
            "Catalog validation repeated the latest-block preflight for individual pools.");
        Assert(handler.MaximumConcurrentPoolCodeRequests is >= 2 and <= 4,
            "Catalog validation did not overlap pool checks within the four-request bound.");
        AssertEqual(5, result.Pools.Length,
            "Catalog validation did not return every supported Uniswap deployment.");
        foreach (var fixture in fixtures)
        {
            var descriptor = result.Pools.Single(pool =>
                pool.PoolKey.PoolId.Equals(fixture.PoolAddress, StringComparison.OrdinalIgnoreCase));
            AssertEqual(fixture.ProtocolId, descriptor.PoolKey.ProtocolId,
                "Catalog validation selected the wrong pricing family.");
            AssertEqual(fixture.FactoryAddress, descriptor.PoolKey.DeploymentKey.ContractAddress,
                "Catalog validation lost the exact factory identity.");
            AssertEqual(OnChainSupportStatus.Supported, descriptor.SupportStatus,
                "A validated catalog pool was not selectable.");
        }
        var v4Descriptor = result.Pools.Single(pool =>
            pool.PoolKey.ProtocolId == OnChainProtocolIds.UniswapV4);
        AssertEqual("selectedAsToken1", v4Descriptor.PairOrientation,
            "Native ETH and selected-token orientation was lost for V4.");
        AssertEqual(EthereumDeploymentRegistry.NativeEther, v4Descriptor.Asset0?.Address,
            "The V4 native currency was not preserved as currency0.");
        AssertEqual(10000U, v4Descriptor.FeeTier,
            "The V4 initialization fee was not preserved.");
        AssertEqual(200, v4Descriptor.TickSpacing,
            "The V4 initialization tick spacing was not preserved.");
        Assert(!EthereumAbi.UniswapV4HookReturnsSwapDelta(v4Descriptor.HookAddress!),
            "A no-hook V4 pool was incorrectly classified as delta-modifying.");
        Assert(EthereumAbi.UniswapV4HookReturnsSwapDelta(
                "0x000000000000000000000000000000000000000c"),
            "V4 swap-delta hook permissions were not detected.");
        Assert(!handler.SawUnpinnedStateRead,
            "Catalog protocol discovery performed a mutable-latest state read.");

        using var partialHandler = new CatalogProtocolDiscoveryFixtureHandler(
            selectedToken,
            fixtures,
            poolCodeDelay: TimeSpan.FromSeconds(5),
            undelayedPoolAddress: fixtures[0].PoolAddress);
        using var partialHttpClient = new HttpClient(partialHandler);
        var partialDiscovery = new EthereumPoolDiscoveryService(
            new EvmJsonRpcClient(partialHttpClient, configuration, null),
            partialHttpClient);
        using var partialTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var partialResult = await partialDiscovery.DiscoverCatalogPoolsSharedAsync(
            selectedToken,
            catalog,
            partialTimeout.Token);
        Assert(partialResult.Pools.Length is >= 1 and < 5,
            "The catalog deadline discarded completed pools or returned the entire delayed catalog.");
        Assert(partialResult.Truncated,
            "The incomplete catalog validation was not marked as truncated.");
        Assert(partialResult.Pools.Any(pool => pool.PoolKey.PoolId == fixtures[0].PoolAddress),
            "The catalog deadline did not preserve the completed highest-priority pool.");
        Assert(partialResult.Pools.All(pool => fixtures.Any(fixture => fixture.PoolAddress == pool.PoolKey.PoolId)),
            "The catalog deadline fabricated a pool absent from the catalog.");

        var filters = EvmWebSocketStreamSource.CreateRpcLogFilters(
            result.Pools.Select(pool => new OnChainWatchedPoolSelection
            {
                Descriptor = pool,
                SelectedMint = selectedToken
            }).ToArray(),
            1,
            2);
        using (var filterDocument = JsonDocument.Parse(JsonSerializer.Serialize(filters)))
        {
            var v4Filter = filterDocument.RootElement.EnumerateArray().Single(filter =>
                filter.GetProperty("address").ValueKind == JsonValueKind.String
                && filter.GetProperty("address").GetString()
                == EthereumDeploymentRegistry.UniswapV4PoolManager);
            AssertEqual(EvmEventTopics.UniswapV4SwapTopic,
                v4Filter.GetProperty("topics")[0].GetString(),
                "The V4 stream filter used the wrong event topic.");
            AssertEqual(v4Descriptor.PoolKey.PoolId,
                v4Filter.GetProperty("topics")[1].GetString(),
                "The V4 stream filter did not constrain the indexed PoolId.");
        }

        var client = new OnChainEngineClient(enginePath);
        var prices = new ConcurrentQueue<OnChainPriceUpdate>();
        client.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
        try
        {
            await client.ReplaceWatchedPoolsAsync(result.Pools.Select(pool =>
                new OnChainWatchedPoolSelection
                {
                    Descriptor = pool,
                    SelectedMint = selectedToken
                }).ToArray());
            AssertEqual(OnChainEngineState.Ready, client.State,
                "The shared engine rejected a validated V4 deployment.");
            var q96 = System.Numerics.BigInteger.One << 96;
            await client.PublishEvmSnapshotAsync(new EvmSnapshotResponse
            {
                RequestId = "v4-snapshot-16",
                PoolKey = v4Descriptor.PoolKey,
                BlockNumber = 16,
                BlockHash = Hash(16),
                Calls =
                [
                    new EvmSnapshotCall
                    {
                        Id = "slot0",
                        Success = true,
                        ReturnData = AbiBigWords(q96, 0, 0, 10000)
                    },
                    new EvmSnapshotCall
                    {
                        Id = "liquidity",
                        Success = true,
                        ReturnData = AbiBigWords(100)
                    }
                ]
            });
            await WaitUntilAsync(() => prices.Count == 1, TimeSpan.FromSeconds(5),
                "Uniswap v4 StateView snapshot price");
            AssertDecimalValue(prices.Last().SpotPriceQuote, 1,
                "The V4 StateView spot price is wrong.");

            await client.PublishEvmLogAsync(new EvmLogUpdate
            {
                ChainId = EvmChainDefinitions.EthereumMainnet.ChainId,
                ConnectionEpoch = 9,
                Address = EthereumDeploymentRegistry.UniswapV4PoolManager,
                Topics =
                [
                    EvmEventTopics.UniswapV4SwapTopic,
                    v4Descriptor.PoolKey.PoolId,
                    Hash(7000)
                ],
                Data = AbiBigWords(
                    -4_000_000_000_000_000_000,
                    2_000_000_000_000_000_000,
                    q96,
                    100,
                    0,
                    10000),
                BlockNumber = 17,
                BlockHash = Hash(17),
                TransactionHash = Hash(7001),
                TransactionIndex = 1,
                LogIndex = 2,
                ObservedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
            await WaitUntilAsync(() => prices.Count == 2, TimeSpan.FromSeconds(5),
                "Uniswap v4 PoolManager swap price");
            AssertDecimalValue(prices.Last().LastTradePriceQuote, 2,
                "The V4 signed pool-delta execution price is wrong.");
            AssertDecimalValue(prices.Last().PriceNative, 2,
                "A native-ETH V4 quote was not exposed in ETH units.");
        }
        finally
        {
            await client.StopAsync();
        }

        static PoolCatalogEntry CatalogPool(
            string protocolId,
            string? label,
            EvmCatalogPoolFixture fixture)
        {
            return new PoolCatalogEntry
            {
                SourceId = "dexscreener",
                ChainId = "ethereum",
                ProtocolId = protocolId,
                PoolAddress = fixture.PoolAddress,
                Labels = label == null ? [] : [label],
                BaseAsset = new PoolCatalogAsset { Address = selectedToken },
                QuoteAsset = new PoolCatalogAsset { Address = fixture.QuoteAddress },
                CreatedAtUnixMs = fixture.CreatedAtUnixMs > 0 ? fixture.CreatedAtUnixMs : null
            };
        }
    }
    static async Task VerifyCatalogTimeoutIsolationAsync()
    {
        const string selectedToken = "0x1111111111111111111111111111111111111111";
        var fixtures = new[]
        {
            new EvmCatalogPoolFixture(
                "0x2222222222222222222222222222222222222222",
                "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                OnChainProtocolIds.UniswapV2,
                EthereumDeploymentRegistry.UniswapV2Factory),
            new EvmCatalogPoolFixture(
                "0x3333333333333333333333333333333333333333",
                "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                OnChainProtocolIds.UniswapV2,
                EthereumDeploymentRegistry.UniswapV2Factory)
        };
        using var handler = new CatalogProtocolDiscoveryFixtureHandler(
            selectedToken,
            fixtures,
            fixtures[0].PoolAddress);
        using var httpClient = new HttpClient(handler);
        var configuration = CreateProviderConfiguration(
            OnChainProviderTypes.PublicNodeEthereum,
            "wss://ethereum-rpc.publicnode.com",
            "https://ethereum-rpc.publicnode.com");
        var catalog = fixtures.Select(fixture => new PoolCatalogEntry
        {
            SourceId = "dexscreener",
            ChainId = "ethereum",
            ProtocolId = "uniswap",
            PoolAddress = fixture.PoolAddress,
            Labels = ["v2"],
            BaseAsset = new PoolCatalogAsset { Address = selectedToken },
            QuoteAsset = new PoolCatalogAsset { Address = fixture.QuoteAddress }
        }).ToArray();

        var result = await new EthereumPoolDiscoveryService(
                new EvmJsonRpcClient(httpClient, configuration, null),
                httpClient)
            .DiscoverAsync(selectedToken, catalog);
        AssertEqual(2, result.Pools.Length,
            "One timed-out pool discarded other catalog validation results.");
        AssertEqual(
            OnChainSupportStatus.TemporarilyUnavailable,
            result.Pools.Single(pool => pool.PoolKey.PoolId == fixtures[0].PoolAddress).SupportStatus,
            "A timed-out pool was not marked temporarily unavailable.");
        AssertEqual(
            OnChainSupportStatus.Supported,
            result.Pools.Single(pool => pool.PoolKey.PoolId == fixtures[1].PoolAddress).SupportStatus,
            "A timeout in one pool prevented the next pool from being validated.");
    }
    static void VerifyCurveCatalogParsing()
    {
        const string selected = "0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2";
        const string pool = "0x1111111111111111111111111111111111111111";
        var parsed = CurvePoolCatalogClient.Parse(
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                success = true,
                data = new
                {
                    poolData = new object[]
                    {
                        new
                        {
                            address = pool,
                            coinsAddresses = new[]
                            {
                                selected,
                                "0x2222222222222222222222222222222222222222",
                                EthereumDeploymentRegistry.Usdc
                            },
                            coins = new object[]
                            {
                                new { address = selected, symbol = "WETH", name = "Wrapped Ether" },
                                new { address = "0x2222222222222222222222222222222222222222", symbol = "OTHER", name = "Other" },
                                new { address = EthereumDeploymentRegistry.Usdc, symbol = "USDC", name = "USD Coin" }
                            },
                            registryId = "factory-twocrypto",
                            assetTypeName = "crypto",
                            usdTotal = 1234.5,
                            creationTs = 1_700_000_000,
                            isBroken = false
                        },
                        new
                        {
                            address = "0x3333333333333333333333333333333333333333",
                            coinsAddresses = new[] { selected, EthereumDeploymentRegistry.Usdc },
                            coins = Array.Empty<object>(),
                            isBroken = true
                        }
                    }
                }
            }),
            selected,
            EthereumDeploymentRegistry.MainnetQuoteAssets);
        AssertEqual(1, parsed.Pools.Length, "Curve catalog parsing did not exclude broken pools.");
        AssertEqual(OnChainProtocolIds.Curve, parsed.Pools[0].ProtocolId,
            "Curve catalog parsing returned the wrong protocol.");
        AssertEqual(pool, parsed.Pools[0].PoolAddress,
            "Curve catalog parsing returned the wrong pool.");
        AssertEqual((uint?)0, parsed.Pools[0].BaseTokenIndex,
            "Curve catalog parsing returned the wrong base index.");
        AssertEqual((uint?)2, parsed.Pools[0].QuoteTokenIndex,
            "Curve catalog parsing did not prefer a configured quote coin.");
        AssertEqual(EthereumDeploymentRegistry.Usdc, parsed.Pools[0].QuoteAsset.Address,
            "Curve catalog parsing returned the wrong quote coin.");
    }
    static async Task VerifyCurveDiscoveryAsync()
    {
        const string selected = EthereumDeploymentRegistry.WrappedEther;
        const string quote = EthereumDeploymentRegistry.Usdc;
        const string poolAddress = "0x1111111111111111111111111111111111111111";
        using var handler = new CurveRpcFixtureHandler(selected, quote, poolAddress);
        using var httpClient = new HttpClient(handler);
        var configuration = CreateProviderConfiguration(
            OnChainProviderTypes.PublicNodeEthereum,
            "wss://ethereum-rpc.publicnode.com",
            "https://ethereum-rpc.publicnode.com");
        var result = await new EthereumPoolDiscoveryService(
                new EvmJsonRpcClient(httpClient, configuration, null),
                httpClient)
            .DiscoverAsync(selected,
            [
                new PoolCatalogEntry
                {
                    SourceId = "curve-api",
                    ChainId = "ethereum",
                    ProtocolId = OnChainProtocolIds.Curve,
                    PoolAddress = poolAddress,
                    BaseAsset = new PoolCatalogAsset { Address = selected },
                    QuoteAsset = new PoolCatalogAsset { Address = quote },
                    BaseTokenIndex = 0,
                    QuoteTokenIndex = 2
                }
            ]);
        var curve = result.Pools.Single(pool => pool.PoolKey.ProtocolId == OnChainProtocolIds.Curve);
        AssertEqual(OnChainSupportStatus.Supported, curve.SupportStatus,
            "The pinned Curve fixture did not validate.");
        AssertEqual(EthereumDeploymentRegistry.CurveAddressProvider,
            curve.PoolKey.DeploymentKey.ContractAddress,
            "Curve discovery returned the wrong canonical deployment identity.");
        AssertEqual((uint?)0, curve.ProtocolAccounts.Single(account => account.Role == "baseCoin").Index,
            "Curve discovery returned the wrong base coin index.");
        AssertEqual((uint?)2, curve.ProtocolAccounts.Single(account => account.Role == "quoteCoin").Index,
            "Curve discovery returned the wrong quote coin index.");
        var fermi = result.Pools.Single(pool => pool.PoolKey.ProtocolId == OnChainProtocolIds.FermiSwap);
        AssertEqual(OnChainSupportStatus.Supported, fermi.SupportStatus,
            "The recent FermiSwap execution fixture did not produce a supported pair.");
        AssertEqual(EthereumDeploymentRegistry.FermiCurrentSwapper,
            fermi.PoolKey.DeploymentKey.ContractAddress,
            "FermiSwap discovery returned the wrong active swapper contract.");
        AssertEqual("0xc954555cf554e837f0314ed6a6c4d6372db48b21978d33695c3548b8d1fb6f59",
            fermi.PoolKey.PoolId,
            "FermiSwap discovery returned a non-canonical pair identity.");
        var filters = JsonSerializer.Serialize(
            EvmWebSocketStreamSource.CreateRpcLogFilters(
            [
                new OnChainWatchedPoolSelection { SelectedMint = selected, Descriptor = curve },
                new OnChainWatchedPoolSelection { SelectedMint = selected, Descriptor = fermi }
            ],
            1,
            2));
        Assert(filters.Contains(EvmEventTopics.CurveTokenExchangeSignedTopic, StringComparison.Ordinal)
               && filters.Contains(EvmEventTopics.CurveTokenExchangeUnsignedTopic, StringComparison.Ordinal)
               && filters.Contains(EvmEventTopics.CurveTokenExchangeExtendedTopic, StringComparison.Ordinal),
            "Curve replay filters omitted an official TokenExchange event layout.");
        Assert(filters.Contains(EvmEventTopics.FermiSwapTopic, StringComparison.Ordinal)
               && filters.Contains(EvmEventTopics.FermiSwappedTopic, StringComparison.Ordinal),
            "FermiSwap replay filters omitted a supported execution event.");
    }
    static async Task VerifyPoolDiscoveryAsync(OnChainEngineClient engine)
    {
        const string apiKey = "discovery-secret";
        const string tokenProgram = "TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA";
        const string token2022Program = "TokenzQdBNbLqP5VEhdkAS6EPFLC1PHnBqCXEpPxuEb";
        const string pumpProgram = "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P";
        const string pumpSwapProgram = "pAMMBay6oceH9fJKBRHGP5D4bD4sWpmSwMn52FMfXEA";
        const string raydiumAmmV4Program = "675kPX9MHTjS2zt1qfr1NYHuzeLXfQM9H24wFSUt1Mp8";
        const string meteoraDammV1Program = "Eo7WjKq67rjJQSZxS6z3YkapzY3eMj6Xy8X5EQVn5UaB";
        const string meteoraDammV2Program = "cpamdpZCGKUy5JxQXB4dcpGPiikHawvSWAd6mEn1sGG";
        const string meteoraVaultProgram = "24Uqj9JCLxUeoC3hGfh5W3s9FM9uCHDS2SG3LYwBpyTi";
        const string meteoraDlmmProgram = "LBUZKhRxPF3XUpBCjp4YzTKgLccjZhTSDM9YuVaPwxo";
        const string manifestProgram = "MNFSTqtC93rEfYHB6hF82sKdZpUDFWkViLByLd1k1Ms";
        const string wrappedSol = "So11111111111111111111111111111111111111112";
        const string curveAddress = "BqZdLs4LGfrtY7fmwARY2LojpNfLm4LkeCV7cVmGBzGq";

        var mintBytes = Enumerable.Repeat((byte)3, 32).ToArray();
        var mint = EncodeBase58(mintBytes);
        var quoteBytes = DecodeBase58(wrappedSol);
        var poolAddress = EncodeBase58(Enumerable.Repeat((byte)8, 32).ToArray());
        var decoyAddress = EncodeBase58(Enumerable.Repeat((byte)9, 32).ToArray());
        var dlmmAddress = EncodeBase58(Enumerable.Repeat((byte)10, 32).ToArray());
        var reserveX = Enumerable.Repeat((byte)11, 32).ToArray();
        var reserveY = Enumerable.Repeat((byte)12, 32).ToArray();
        var ammV4Address = EncodeBase58(Enumerable.Repeat((byte)13, 32).ToArray());
        var ammCoinVault = Enumerable.Repeat((byte)14, 32).ToArray();
        var ammPcVault = Enumerable.Repeat((byte)15, 32).ToArray();
        var dammV2Address = EncodeBase58(Enumerable.Repeat((byte)16, 32).ToArray());
        var dammV2VaultA = Enumerable.Repeat((byte)17, 32).ToArray();
        var dammV2VaultB = Enumerable.Repeat((byte)18, 32).ToArray();
        var dammV1Address = EncodeBase58(Enumerable.Repeat((byte)19, 32).ToArray());
        var dammV1VaultStateA = Enumerable.Repeat((byte)20, 32).ToArray();
        var dammV1VaultStateB = Enumerable.Repeat((byte)21, 32).ToArray();
        var dammV1VaultLpA = Enumerable.Repeat((byte)22, 32).ToArray();
        var dammV1VaultLpB = Enumerable.Repeat((byte)23, 32).ToArray();
        var dammV1VaultLpMintA = Enumerable.Repeat((byte)24, 32).ToArray();
        var dammV1VaultLpMintB = Enumerable.Repeat((byte)25, 32).ToArray();
        var dammV1TokenVaultA = Enumerable.Repeat((byte)26, 32).ToArray();
        var dammV1TokenVaultB = Enumerable.Repeat((byte)27, 32).ToArray();
        var manifestAddress = EncodeBase58(Enumerable.Repeat((byte)28, 32).ToArray());
        var manifestBaseVault = Enumerable.Repeat((byte)29, 32).ToArray();
        var manifestQuoteVault = Enumerable.Repeat((byte)30, 32).ToArray();
        var accounts = new Dictionary<string, RpcFixtureAccount>(StringComparer.Ordinal)
        {
            [mint] = new(tokenProgram, CreateMintAccount(6)),
            [wrappedSol] = new(tokenProgram, CreateMintAccount(9)),
            [curveAddress] = new(pumpProgram, CreatePumpCurveAccount(quoteBytes)),
            [poolAddress] = new(pumpSwapProgram, CreatePumpSwapAccount(mintBytes, quoteBytes)),
            [decoyAddress] = new(pumpSwapProgram, new byte[301]),
            [ammV4Address] = new(
                raydiumAmmV4Program,
                CreateRaydiumAmmV4Account(mintBytes, quoteBytes, ammCoinVault, ammPcVault)),
            [dammV2Address] = new(
                meteoraDammV2Program,
                CreateMeteoraDammV2Account(quoteBytes, mintBytes, dammV2VaultA, dammV2VaultB)),
            [dammV1Address] = new(
                meteoraDammV1Program,
                CreateMeteoraDammV1Account(
                    quoteBytes,
                    mintBytes,
                    dammV1VaultStateA,
                    dammV1VaultStateB,
                    dammV1VaultLpA,
                    dammV1VaultLpB)),
            [EncodeBase58(dammV1VaultStateA)] = new(
                meteoraVaultProgram,
                CreateMeteoraDynamicVaultAccount(quoteBytes, dammV1TokenVaultA, dammV1VaultLpMintA)),
            [EncodeBase58(dammV1VaultStateB)] = new(
                meteoraVaultProgram,
                CreateMeteoraDynamicVaultAccount(mintBytes, dammV1TokenVaultB, dammV1VaultLpMintB)),
            [dlmmAddress] = new(
                meteoraDlmmProgram,
                CreateMeteoraDlmmAccount(quoteBytes, mintBytes, reserveX, reserveY)),
            [manifestAddress] = new(
                manifestProgram,
                CreateManifestMarketAccount(
                    quoteBytes,
                    mintBytes,
                    manifestQuoteVault,
                    manifestBaseVault))
        };
        using var handler = new SolanaRpcFixtureHandler(apiKey, mint, poolAddress, decoyAddress, accounts);
        using var httpClient = new HttpClient(handler);
        var rpc = new SolanaRpcClient(
            httpClient,
            CreateProviderConfiguration(
                OnChainProviderTypes.HeliusLaserStream,
                "https://stream.unit-test.helius-rpc.com",
                "https://unit-test.helius-rpc.com"),
            apiKey);
        var discovery = new OnChainPoolDiscoveryService(rpc, engine);

        var result = await discovery.DiscoverAsync(
            mint,
            OnChainCommitment.Confirmed,
            [ammV4Address, dammV1Address, dammV2Address, dlmmAddress, manifestAddress]);
        AssertEqual(7, result.Pools.Length,
            "Discovery did not return the validated Pump, PumpSwap, Raydium AMM v4, DAMM v1/v2, DLMM, and Manifest markets.");
        Assert(result.Pools.All(static pool => pool.SupportStatus == OnChainSupportStatus.Supported),
            "Validated legacy-token fixtures should be supported.");
        Assert(result.Pools.Any(pool => pool.PoolKey.ProtocolId == OnChainProtocolIds.PumpBondingCurve
                                        && pool.PoolKey.PoolAddress == curveAddress),
            "The derived Pump curve was not returned.");
        var pumpSwap = result.Pools.Single(pool => pool.PoolKey.ProtocolId == OnChainProtocolIds.PumpSwap);
        AssertEqual(poolAddress, pumpSwap.PoolKey.PoolAddress, "PumpSwap candidate identity changed.");
        AssertEqual((byte)6, pumpSwap.BaseDecimals, "Base mint decimals were not decoded on-chain.");
        AssertEqual((byte)9, pumpSwap.QuoteDecimals, "Quote mint decimals were not decoded on-chain.");
        AssertEqual("selectedAsBase", pumpSwap.PairOrientation, "Pool orientation was not preserved.");
        var dlmm = result.Pools.Single(pool => pool.PoolKey.ProtocolId == OnChainProtocolIds.MeteoraDlmm);
        AssertEqual(mint, dlmm.BaseMint, "Reverse-oriented DLMM discovery did not normalize the selected mint as base.");
        AssertEqual(wrappedSol, dlmm.QuoteMint, "Reverse-oriented DLMM discovery did not normalize the quote mint.");
        AssertEqual(EncodeBase58(reserveY), dlmm.BaseVault, "Reverse-oriented DLMM discovery chose the wrong base vault.");
        AssertEqual(EncodeBase58(reserveX), dlmm.QuoteVault, "Reverse-oriented DLMM discovery chose the wrong quote vault.");
        AssertEqual("selectedAsQuote", dlmm.PairOrientation, "Canonical DLMM orientation was not preserved for diagnostics.");
        AssertEqual(OnChainSupportStatus.Supported, dlmm.SupportStatus,
            "A valid reverse-oriented DLMM pool was not selectable.");
        var ammV4 = result.Pools.Single(pool => pool.PoolKey.ProtocolId == OnChainProtocolIds.RaydiumAmmV4);
        AssertEqual(ammV4Address, ammV4.PoolKey.PoolAddress,
            "Raydium AMM v4 candidate identity changed.");
        AssertEqual(EncodeBase58(ammCoinVault), ammV4.BaseVault,
            "Raydium AMM v4 discovery chose the wrong coin vault.");
        AssertEqual(EncodeBase58(ammPcVault), ammV4.QuoteVault,
            "Raydium AMM v4 discovery chose the wrong pc vault.");
        var dammV2 = result.Pools.Single(pool => pool.PoolKey.ProtocolId == OnChainProtocolIds.MeteoraDammV2);
        AssertEqual(mint, dammV2.BaseMint,
            "Reverse-oriented DAMM v2 discovery did not normalize the selected mint as base.");
        AssertEqual(EncodeBase58(dammV2VaultB), dammV2.BaseVault,
            "Reverse-oriented DAMM v2 discovery chose the wrong base vault.");
        AssertEqual(EncodeBase58(dammV2VaultA), dammV2.QuoteVault,
            "Reverse-oriented DAMM v2 discovery chose the wrong quote vault.");
        var dammV1 = result.Pools.Single(pool => pool.PoolKey.ProtocolId == OnChainProtocolIds.MeteoraDammV1);
        AssertEqual(mint, dammV1.BaseMint,
            "Reverse-oriented DAMM v1 discovery did not normalize the selected mint as base.");
        AssertEqual(EncodeBase58(dammV1TokenVaultB), dammV1.BaseVault,
            "DAMM v1 discovery did not resolve the selected token's underlying Dynamic Vault account.");
        AssertEqual(EncodeBase58(dammV1TokenVaultA), dammV1.QuoteVault,
            "DAMM v1 discovery did not resolve the quote token's underlying Dynamic Vault account.");
        AssertEqual(6, dammV1.ProtocolAccounts.Length,
            "DAMM v1 discovery did not preserve all state, share, and supply dependencies.");
        AssertEqual(EncodeBase58(dammV1VaultLpMintB),
            dammV1.ProtocolAccounts.Single(account => account.Role == "baseVaultLpMint").Address,
            "DAMM v1 reverse orientation selected the wrong base vault LP mint.");
        var manifest = result.Pools.Single(pool =>
            pool.PoolKey.ProtocolId == OnChainProtocolIds.ManifestOrderbook);
        AssertEqual(mint, manifest.BaseMint,
            "Reverse-oriented Manifest discovery did not normalize the selected mint as base.");
        AssertEqual(EncodeBase58(manifestBaseVault), manifest.BaseVault,
            "Manifest discovery chose the wrong selected-token vault.");
        AssertEqual(EncodeBase58(manifestQuoteVault), manifest.QuoteVault,
            "Manifest discovery chose the wrong quote-token vault.");
        AssertEqual("centralLimitOrderBook", manifest.PoolType,
            "Manifest was mislabeled as an AMM pool.");

        var meteoraCatalog = MeteoraDammV2PoolCatalogClient.Parse(
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                data = new object[]
                {
                    new
                    {
                        address = dammV2Address,
                        token_x = new { address = wrappedSol, name = "Wrapped SOL", symbol = "SOL" },
                        token_y = new { address = mint, name = "Test Token", symbol = "TEST" },
                        tvl = 1234.5,
                        volume = new Dictionary<string, double> { ["24h"] = 987.25 },
                        created_at = 1_754_985_927_000L,
                        pool_config = new { concentrated_liquidity = true }
                    },
                    new
                    {
                        address = decoyAddress,
                        token_x = new { address = wrappedSol, name = "Wrapped SOL", symbol = "SOL" },
                        token_y = new { address = poolAddress, name = "Other", symbol = "OTHER" },
                        tvl = 9999.0
                    }
                }
            }),
            mint);
        AssertEqual(1, meteoraCatalog.Pools.Length,
            "Meteora DAMM v2 catalog parsing did not require an exact mint match.");
        AssertEqual(OnChainProtocolIds.MeteoraDammV2, meteoraCatalog.Pools[0].ProtocolId,
            "Meteora DAMM v2 catalog parsing lost the internal protocol identity.");
        AssertEqual("Concentrated", meteoraCatalog.Pools[0].Labels.Single(),
            "Meteora DAMM v2 catalog parsing lost the liquidity mode.");

        var meteoraV1Catalog = MeteoraDammV1PoolCatalogClient.Parse(
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                data = new object[]
                {
                    new
                    {
                        pool_address = dammV1Address,
                        pool_token_mints = new[] { wrappedSol, mint },
                        pool_name = "SOL-TEST",
                        pool_type = "volatile",
                        pool_tvl = "4321.5",
                        trading_volume = 765.25,
                        created_at = 1_738_820_726L
                    }
                }
            }),
            mint);
        AssertEqual(1, meteoraV1Catalog.Pools.Length,
            "Meteora DAMM v1 catalog parsing did not retain the exact mint pool.");
        AssertEqual(OnChainProtocolIds.MeteoraDammV1, meteoraV1Catalog.Pools[0].ProtocolId,
            "Meteora DAMM v1 catalog parsing lost the internal protocol identity.");
        AssertEqual(4_321.5, meteoraV1Catalog.Pools[0].LiquidityUsd,
            "Meteora DAMM v1 catalog parsing did not use invariant-culture TVL.");
        var manifestCatalog = ManifestPoolCatalogClient.Parse(
            JsonSerializer.SerializeToUtf8Bytes(new object[]
            {
                new
                {
                    ticker_id = manifestAddress,
                    base_currency = wrappedSol,
                    target_currency = mint,
                    pool_id = manifestAddress,
                    liquidity_in_usd = "123.5"
                },
                new
                {
                    ticker_id = decoyAddress,
                    base_currency = wrappedSol,
                    target_currency = poolAddress,
                    pool_id = decoyAddress,
                    liquidity_in_usd = 999.0
                }
            }),
            mint);
        AssertEqual(1, manifestCatalog.Pools.Length,
            "Manifest catalog parsing did not require an exact mint match.");
        AssertEqual(OnChainProtocolIds.ManifestOrderbook, manifestCatalog.Pools[0].ProtocolId,
            "Manifest catalog parsing lost the internal order-book identity.");
        AssertEqual(123.5, manifestCatalog.Pools[0].LiquidityUsd,
            "Manifest catalog parsing did not use invariant-culture liquidity metadata.");
        Assert(result.Warnings.Any(static warning => warning.Contains("Ignored 1 candidate", StringComparison.Ordinal)),
            "The invalid candidate was not reported as rejected.");
        Assert(handler.SawApiKey && handler.SawZeroLengthDataSlice,
            "Discovery did not authenticate transiently or use the bounded address-only scan.");

        var token2022Accounts = accounts.ToDictionary(static entry => entry.Key, static entry => entry.Value,
            StringComparer.Ordinal);
        token2022Accounts[mint] = new RpcFixtureAccount(
            token2022Program,
            CreateToken2022MintAccount(6, (18, 64), (19, 4)));
        using var token2022Handler = new SolanaRpcFixtureHandler(
            apiKey,
            mint,
            poolAddress,
            decoyAddress,
            token2022Accounts);
        using var token2022HttpClient = new HttpClient(token2022Handler);
        var token2022Discovery = new OnChainPoolDiscoveryService(
            new SolanaRpcClient(
                token2022HttpClient,
                CreateProviderConfiguration(
                    OnChainProviderTypes.HeliusLaserStream,
                    "https://stream.unit-test.helius-rpc.com",
                    "https://unit-test.helius-rpc.com"),
                apiKey),
            engine);
        var token2022Result = await token2022Discovery.DiscoverAsync(mint, OnChainCommitment.Confirmed);
        AssertEqual(2, token2022Result.Pools.Length,
            "Token-2022 safety classification discarded discoverable pool identities.");
        Assert(token2022Result.Pools.All(static pool =>
                pool.SupportStatus == OnChainSupportStatus.Supported),
            "Metadata-only Token-2022 pools were not selectable.");

        var transferFeeAccounts = accounts.ToDictionary(static entry => entry.Key, static entry => entry.Value,
            StringComparer.Ordinal);
        transferFeeAccounts[mint] = new RpcFixtureAccount(
            token2022Program,
            CreateToken2022MintAccount(6, (1, 1)));
        using var transferFeeHandler = new SolanaRpcFixtureHandler(
            apiKey,
            mint,
            poolAddress,
            decoyAddress,
            transferFeeAccounts);
        using var transferFeeHttpClient = new HttpClient(transferFeeHandler);
        var transferFeeDiscovery = new OnChainPoolDiscoveryService(
            new SolanaRpcClient(
                transferFeeHttpClient,
                CreateProviderConfiguration(
                    OnChainProviderTypes.HeliusLaserStream,
                    "https://stream.unit-test.helius-rpc.com",
                    "https://unit-test.helius-rpc.com"),
                apiKey),
            engine);
        var transferFeeResult = await transferFeeDiscovery.DiscoverAsync(mint, OnChainCommitment.Confirmed);
        AssertEqual(2, transferFeeResult.Pools.Length,
            "Token-2022 transfer-fee classification discarded discoverable pool identities.");
        Assert(transferFeeResult.Pools.All(static pool =>
                pool.SupportStatus == OnChainSupportStatus.DiscoveredUnsupported
                && pool.SupportReason?.Contains("non-metadata", StringComparison.Ordinal) == true),
            "A Token-2022 transfer-fee mint bypassed the fail-closed pricing boundary.");

        using var rateLimitedHandler = new SolanaRpcFixtureHandler(
            apiKey,
            mint,
            poolAddress,
            decoyAddress,
            accounts,
            rateLimitProgramAccounts: true);
        using var rateLimitedHttpClient = new HttpClient(rateLimitedHandler);
        var rateLimitedDiscovery = new OnChainPoolDiscoveryService(
            new SolanaRpcClient(
                rateLimitedHttpClient,
                CreateProviderConfiguration(
                    OnChainProviderTypes.AlchemyWebSocket,
                    "wss://solana-mainnet.streaming.alchemy.com/v2",
                    "https://solana-mainnet.g.alchemy.com/v2"),
                apiKey),
            engine);

        var partialResult = await rateLimitedDiscovery.DiscoverAsync(mint, OnChainCommitment.Confirmed);
        AssertEqual(1, partialResult.Pools.Length,
            "A PumpSwap discovery rate limit discarded the independently validated Pump curve.");
        AssertEqual(OnChainProtocolIds.PumpBondingCurve, partialResult.Pools[0].PoolKey.ProtocolId,
            "The rate-limited discovery result did not preserve the Pump curve.");
        Assert(partialResult.Warnings.Any(static warning => warning.Contains("rate-limited", StringComparison.Ordinal)),
            "The incomplete PumpSwap discovery result did not explain the provider rate limit.");
        AssertEqual(1, rateLimitedHandler.ProgramAccountRequestCount,
            "Discovery continued issuing expensive PumpSwap scans after the provider rate limit.");

        using var publicRpcHandler = new SolanaRpcFixtureHandler(
            string.Empty,
            mint,
            poolAddress,
            decoyAddress,
            accounts);
        using var publicRpcHttpClient = new HttpClient(publicRpcHandler);
        var publicDiscovery = new OnChainPoolDiscoveryService(
            new SolanaRpcClient(publicRpcHttpClient, OnChainPoolDiscoveryService.PublicMainnetRpcEndpoint),
            engine);

        var publicResult = await publicDiscovery.DiscoverAsync(mint, OnChainCommitment.Confirmed);
        AssertEqual(2, publicResult.Pools.Length,
            "Provider-independent public discovery did not return both strictly validated pools.");
        AssertEqual(8, publicRpcHandler.ProgramAccountRequestCount,
            "Public discovery did not run the eight bounded PumpSwap orientation/layout scans.");
        Assert(publicRpcHandler.SawCredentialFreeRequest && publicRpcHandler.SawZeroLengthDataSlice,
            "Public discovery was not credential-free or did not use bounded address-only scans.");
    }
}

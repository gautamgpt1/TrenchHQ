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
    static async Task VerifyLiveRobinhoodDiscoveryAsync()
    {
        const string usde = "0x5d3a1ff2b6bab83b63cd9ad0787074081a52ef34";
        const string longAsset = "0x2e8c31162b855a2ffa90f6f8634643ad6f111e18";
        const string ponsV1Delta = "0xe8ffd7e24187f72afb08d75b1bb13088a989a791";
        const string expectedV4PoolId =
            "0xa5f23cae4e5c3388c5a8a6b08a83f53e56df8f1a63757e606b362994b68a2361";
        using var catalogHttp = new HttpClient
        {
            BaseAddress = new Uri("https://api.dexscreener.com/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        using var rpcHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var alchemyApiKey = Environment.GetEnvironmentVariable("TRENCHHQ_ALCHEMY_API_KEY");
        var robinhoodPreset = OnChainProviderCatalog.Get(OnChainProviderTypes.AlchemyRobinhood);
        var configuration = string.IsNullOrWhiteSpace(alchemyApiKey)
            ? CreateProviderConfiguration(
                OnChainProviderTypes.CustomRobinhood,
                "wss://unused.invalid",
                RobinhoodDeploymentRegistry.PublicRpcEndpoint)
            : CreateProviderConfiguration(
                robinhoodPreset.ProviderType,
                robinhoodPreset.DefaultStreamEndpoint,
                robinhoodPreset.DefaultRpcEndpoint);
        var discovery = new EthereumPoolDiscoveryService(
            new EvmJsonRpcClient(rpcHttp, configuration, alchemyApiKey),
            EvmChainDefinitions.RobinhoodMainnet,
            RobinhoodDeploymentRegistry.Catalog,
            rpcHttp);
        var catalogClient = new DexScreenerPoolCatalogClient(catalogHttp);
        var catalogToken = Environment.GetEnvironmentVariable("TRENCHHQ_LIVE_ROBINHOOD_TOKEN");
        if (!string.IsNullOrWhiteSpace(catalogToken))
        {
            Console.WriteLine("LIVE ROBINHOOD | validating requested token's three largest V4 pools");
            using var catalogTokenTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var tokenCatalog = await catalogClient.SearchAsync(
                RobinhoodDeploymentRegistry.Catalog.CatalogChainId,
                catalogToken,
                catalogTokenTimeout.Token);
            var largestV4CatalogPools = tokenCatalog.Pools
                .Where(pool => pool.ProtocolId.Equals("uniswap", StringComparison.OrdinalIgnoreCase)
                               && pool.Labels.Contains("v4", StringComparer.OrdinalIgnoreCase))
                .OrderByDescending(static pool => pool.LiquidityUsd ?? double.MinValue)
                .Take(3)
                .ToArray();
            AssertEqual(3, largestV4CatalogPools.Length,
                "The requested Robinhood Chain token did not return three V4 catalog pools.");
            if (string.Equals(
                    Environment.GetEnvironmentVariable("TRENCHHQ_LIVE_ROBINHOOD_COLD_CATALOG"),
                    "1",
                    StringComparison.Ordinal))
            {
                var coldTimer = System.Diagnostics.Stopwatch.StartNew();
                var coldResult = await discovery.DiscoverCatalogPoolsSharedAsync(
                    catalogToken,
                    tokenCatalog.Pools,
                    catalogTokenTimeout.Token);
                coldTimer.Stop();
                Assert(largestV4CatalogPools.All(catalogPool => coldResult.Pools.Any(pool =>
                        pool.PoolKey.PoolId.Equals(catalogPool.PoolAddress, StringComparison.OrdinalIgnoreCase)
                        && pool.SupportStatus == OnChainSupportStatus.Supported)),
                    "The cold catalog path lost a supported top-liquidity Robinhood pool.");
                Console.WriteLine(
                    $"LIVE ROBINHOOD COLD CATALOG | catalogPools={tokenCatalog.Pools.Length} | supported={coldResult.Pools.Count(pool => pool.SupportStatus == OnChainSupportStatus.Supported)} | elapsedMs={coldTimer.ElapsedMilliseconds}");
                return;
            }
            foreach (var catalogPool in largestV4CatalogPools)
            {
                var poolTimer = System.Diagnostics.Stopwatch.StartNew();
                var tokenResult = await discovery.DiscoverCatalogPoolsAsync(
                    catalogToken,
                    [catalogPool],
                    catalogTokenTimeout.Token);
                poolTimer.Stop();
                var descriptor = tokenResult.Pools.Single(pool => pool.PoolKey.PoolId.Equals(
                    catalogPool.PoolAddress,
                    StringComparison.OrdinalIgnoreCase));
                Console.WriteLine(
                    $"LIVE ROBINHOOD TOKEN | {descriptor.PoolKey.PoolId} | liquidityUsd={catalogPool.LiquidityUsd} | {descriptor.SupportStatus} | elapsedMs={poolTimer.ElapsedMilliseconds} | {descriptor.SupportReason ?? "validated"}");
                AssertEqual(OnChainSupportStatus.Supported, descriptor.SupportStatus,
                    "A requested top-liquidity Robinhood Chain V4 pool failed on-chain validation.");
            }
            if (string.Equals(
                    Environment.GetEnvironmentVariable("TRENCHHQ_LIVE_ROBINHOOD_FULL_SEARCH"),
                    "1",
                    StringComparison.Ordinal))
            {
                var catalogTimer = System.Diagnostics.Stopwatch.StartNew();
                var concurrentCatalogResult = await discovery.DiscoverCatalogPoolsSharedAsync(
                    catalogToken,
                    tokenCatalog.Pools,
                    catalogTokenTimeout.Token);
                catalogTimer.Stop();
                Console.WriteLine(
                    $"LIVE ROBINHOOD CONCURRENT CATALOG | catalogPools={tokenCatalog.Pools.Length} | supported={concurrentCatalogResult.Pools.Count(pool => pool.SupportStatus == OnChainSupportStatus.Supported)} | elapsedMs={catalogTimer.ElapsedMilliseconds}");
                var searchTimer = System.Diagnostics.Stopwatch.StartNew();
                var fullResult = await discovery.DiscoverAsync(
                    catalogToken,
                    tokenCatalog.Pools,
                    catalogTokenTimeout.Token);
                searchTimer.Stop();
                Assert(largestV4CatalogPools.All(catalogPool => fullResult.Pools.Any(pool =>
                        pool.PoolKey.PoolId.Equals(catalogPool.PoolAddress, StringComparison.OrdinalIgnoreCase)
                        && pool.SupportStatus == OnChainSupportStatus.Supported)),
                    "The full Robinhood search path lost a supported top-liquidity pool.");
                Console.WriteLine(
                    $"LIVE ROBINHOOD FULL SEARCH | catalogPools={tokenCatalog.Pools.Length} | supported={fullResult.Pools.Count(pool => pool.SupportStatus == OnChainSupportStatus.Supported)} | elapsedMs={searchTimer.ElapsedMilliseconds}");
            }
            return;
        }

        Console.WriteLine("LIVE ROBINHOOD | validating WETH reference discovery");
        using var wethTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var wethCatalog = await catalogClient.SearchAsync(
            RobinhoodDeploymentRegistry.Catalog.CatalogChainId,
            RobinhoodDeploymentRegistry.WrappedEther,
            wethTimeout.Token);
        var referenceCatalog = wethCatalog.Pools.Where(pool => pool.PoolAddress.Equals(
            RobinhoodDeploymentRegistry.WrappedEtherUsdgReferencePool,
            StringComparison.OrdinalIgnoreCase)).ToArray();
        AssertEqual(1, referenceCatalog.Length,
            "The live catalog did not return the canonical Robinhood Chain WETH/USDG reference pool.");
        var wethResult = await discovery.DiscoverAsync(
            RobinhoodDeploymentRegistry.WrappedEther,
            referenceCatalog,
            wethTimeout.Token);
        var referencePool = wethResult.Pools.Single(pool =>
            pool.PoolKey.PoolId.Equals(
                RobinhoodDeploymentRegistry.WrappedEtherUsdgReferencePool,
                StringComparison.OrdinalIgnoreCase));
        AssertEqual(OnChainProtocolIds.UniswapV3, referencePool.PoolKey.ProtocolId,
            "Live Robinhood Chain discovery returned the wrong reference protocol.");
        AssertEqual(OnChainSupportStatus.Supported, referencePool.SupportStatus,
            "The canonical Robinhood Chain WETH/USDG reference pool failed validation.");

        Console.WriteLine("LIVE ROBINHOOD | validating USDe V4 discovery");
        using var usdeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var usdeCatalog = await catalogClient.SearchAsync(
            RobinhoodDeploymentRegistry.Catalog.CatalogChainId,
            usde,
            usdeTimeout.Token);
        var v4Catalog = usdeCatalog.Pools.Where(pool => pool.PoolAddress.Equals(
            expectedV4PoolId,
            StringComparison.OrdinalIgnoreCase)).ToArray();
        AssertEqual(1, v4Catalog.Length,
            "The live catalog did not return the canonical Robinhood Chain USDe V4 pool.");
        var usdeResult = await discovery.DiscoverAsync(usde, v4Catalog, usdeTimeout.Token);
        var v4Pool = usdeResult.Pools.Single(pool =>
            pool.PoolKey.PoolId.Equals(expectedV4PoolId, StringComparison.OrdinalIgnoreCase));
        AssertEqual(OnChainProtocolIds.UniswapV4, v4Pool.PoolKey.ProtocolId,
            "Live Robinhood Chain discovery returned the wrong V4 protocol.");
        AssertEqual(OnChainSupportStatus.Supported, v4Pool.SupportStatus,
            "The live Robinhood Chain zero-hook V4 pool failed validation.");
        AssertEqual(RobinhoodDeploymentRegistry.UniswapV4PoolManager, v4Pool.ProgramId,
            "Live Robinhood Chain V4 discovery returned the wrong PoolManager.");
        AssertEqual(RobinhoodDeploymentRegistry.NativeEther, v4Pool.HookAddress,
            "The recorded Robinhood Chain V4 pool no longer has a zero hook.");

        Console.WriteLine("LIVE ROBINHOOD | validating long.xyz launchpad discovery");
        using var longTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var longResult = await discovery.DiscoverAsync(longAsset, longTimeout.Token);
        foreach (var warning in longResult.Warnings)
        {
            Console.WriteLine($"LIVE ROBINHOOD WARNING | {warning}");
        }
        var longPool = longResult.Pools.Single(pool =>
            pool.DiscoverySource.StartsWith("canonical:long.xyz", StringComparison.Ordinal));
        AssertEqual(OnChainProtocolIds.UniswapV4, longPool.PoolKey.ProtocolId,
            "Live long.xyz discovery returned the wrong protocol.");
        AssertEqual(OnChainSupportStatus.Supported, longPool.SupportStatus,
            "The canonical long.xyz pool failed validation.");
        AssertEqual(RobinhoodDeploymentRegistry.DopplerHookInitializer, longPool.HookAddress,
            "Live long.xyz discovery returned the wrong Doppler hook.");
        AssertEqual("spotOnly", longPool.PricingMode,
            "The returned-delta long.xyz pool was not constrained to spot-only pricing.");

        Console.WriteLine("LIVE ROBINHOOD | validating pons v1 launchpad discovery");
        using var ponsTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ponsV1Result = await discovery.DiscoverAsync(ponsV1Delta, ponsTimeout.Token);
        var ponsV1Pool = ponsV1Result.Pools.Single(pool =>
            pool.DiscoverySource.StartsWith("canonical:pons-v1", StringComparison.Ordinal));
        AssertEqual(OnChainProtocolIds.UniswapV3, ponsV1Pool.PoolKey.ProtocolId,
            "Live pons v1 discovery returned the wrong protocol.");
        AssertEqual(OnChainSupportStatus.Supported, ponsV1Pool.SupportStatus,
            "The canonical pons v1 pool failed validation.");
        Console.WriteLine(
            $"LIVE ROBINHOOD | WETH pools={wethResult.Pools.Length} | USDe pools={usdeResult.Pools.Length} | V4={v4Pool.PoolKey.PoolId} | long.xyz={longPool.PoolKey.PoolId} | pons-v1={ponsV1Pool.PoolKey.PoolId}");

    }
}

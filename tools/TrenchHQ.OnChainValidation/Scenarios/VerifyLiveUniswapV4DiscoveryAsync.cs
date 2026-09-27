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
    static async Task VerifyLiveUniswapV4DiscoveryAsync()
    {
        const string shib = "0x95aD61b0a150d79219dCF64E1E6Cc01f0B64C4cE";
        using var catalogHttp = new HttpClient
        {
            BaseAddress = new Uri("https://api.dexscreener.com/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        using var rpcHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var catalog = await new DexScreenerPoolCatalogClient(catalogHttp)
            .SearchAsync("ethereum", shib);
        var configuration = CreateProviderConfiguration(
            OnChainProviderTypes.CustomEthereum,
            "wss://eth-mainnet.public.blastapi.io",
            "https://eth-mainnet.public.blastapi.io");
        var v4CatalogPools = catalog.Pools
            .Where(pool => pool.ProtocolId.Equals("uniswap", StringComparison.OrdinalIgnoreCase)
                           && pool.Labels.Contains("v4", StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var result = await new EthereumPoolDiscoveryService(
                new EvmJsonRpcClient(rpcHttp, configuration, null),
                rpcHttp)
            .DiscoverAsync(shib, v4CatalogPools);
        var v4Pools = v4CatalogPools
            .Select(pool => result.Pools.FirstOrDefault(descriptor =>
                               descriptor.PoolKey.PoolId.Equals(
                                   pool.PoolAddress,
                                   StringComparison.OrdinalIgnoreCase))
                           ?? new OnChainPoolDescriptor
                           {
                               PoolKey = new OnChainPoolKey { PoolId = pool.PoolAddress },
                               SupportStatus = OnChainSupportStatus.DiscoveredUnsupported,
                               SupportReason = "No descriptor was returned."
                           })
            .ToArray();
        Console.WriteLine(
            $"LIVE EVM | descriptors={result.Pools.Length} | supported={result.Pools.Count(pool => pool.SupportStatus == OnChainSupportStatus.Supported)}");
        foreach (var pool in v4Pools)
        {
            Console.WriteLine(
                $"LIVE V4 | {pool.PoolKey.PoolId} | {pool.SupportStatus} | hook={pool.HookAddress ?? "unknown"} | {pool.SupportReason ?? "validated"}");
        }
        Assert(v4Pools.Length > 0, "The live SHIB catalog did not contain a V4 pool.");
        Assert(v4Pools.All(pool => pool.SupportReason != "No descriptor was returned."),
            "At least one live SHIB V4 candidate was not classified by discovery.");
        Assert(v4Pools.Any(pool => pool.SupportStatus == OnChainSupportStatus.Supported),
            "The live SHIB catalog did not contain a selectable Uniswap V4 pool.");
    }
}

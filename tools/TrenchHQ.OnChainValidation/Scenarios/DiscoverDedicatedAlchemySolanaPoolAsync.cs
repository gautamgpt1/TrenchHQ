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
    static async Task<OnChainWatchedPoolSelection> DiscoverDedicatedAlchemySolanaPoolAsync(
        HttpClient rpcHttp, OnChainProviderConfiguration configuration, string credential,
        OnChainEngineClient client)
    {
        const string wrappedSol = "So11111111111111111111111111111111111111112";
        const string solanaPoolId = "58oQChx4yWmvKdwLLZzBi4ChoCc2fqCUWBkwMihLYQo2";
        var discovered = await new OnChainPoolDiscoveryService(
                new SolanaRpcClient(rpcHttp, configuration, credential), client)
            .DiscoverAsync(wrappedSol, OnChainCommitment.Confirmed, [solanaPoolId],
                includeDerivedDiscovery: false);
        return new OnChainWatchedPoolSelection
        {
            SelectedMint = wrappedSol,
            Descriptor = discovered.Pools.Single(pool =>
                pool.PoolKey.PoolAddress == solanaPoolId
                && pool.SupportStatus == OnChainSupportStatus.Supported)
        };
    }
}

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
    internal static async Task VerifyPublicBatchAsync()
    {
        foreach (var chain in EvmChainDefinitions.Supported)
        {
            var preset = OnChainProviderCatalog.GetPublicEvaluationPreset(chain.ChainNamespace, chain.ChainId)!;
            var profile = OnChainProviderConfigurationStore.CreateConfiguration(preset);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var rpc = new EvmJsonRpcClient(http, profile, null);
            var block = await rpc.GetBlockAsync("latest", default);
            var pool = chain.CreateNativeUsdReferenceSelection!();
            var calls = Enumerable.Range(0, 256).Select(index => (pool.Descriptor.PoolKey.PoolId,
                index % 2 == 0 ? EthereumAbi.Slot0Selector : EthereumAbi.LiquiditySelector)).ToArray();
            var result = await rpc.CallAsync(EvmMulticall3.Address, EvmMulticall3.EncodeCalls(calls), block, default);
            if (!EvmMulticall3.TryDecodeResults(result.GetString(), calls.Length, out var values)
                || !EthereumAbi.TryDecodeUniswapV3Slot0(values[0], out _, out _))
                throw new Exception($"{chain.DisplayName}: public large aggregate failed.");
            Console.WriteLine($"PUBLIC AGGREGATE | {chain.DisplayName} | block={block.Number} | subcalls=256 | eth_call=1 | bytes={(result.GetString()!.Length - 2) / 2}");
        }
    }

}

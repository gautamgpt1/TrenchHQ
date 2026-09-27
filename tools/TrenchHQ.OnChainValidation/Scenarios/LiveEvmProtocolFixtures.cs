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
    static LiveEvmProtocolFixture[] LiveEvmProtocolFixtures(string chainId)
    {
        return chainId switch
        {
            EvmChainDefinitions.EthereumMainnetChainId =>
            [
                new("Uniswap V2", LiveEvmProtocolKind.V2,
                    "0xb4e16d0168e52d35cacd2c6185b44281ec28c9dc"),
                new("Uniswap V3", LiveEvmProtocolKind.V3,
                    "0x88e6a0c2ddd26feeb64f039a2c41296fcb3f5640"),
                new("Uniswap V4", LiveEvmProtocolKind.V4,
                    EthereumDeploymentRegistry.UniswapV4StateView,
                    "0x284220e1aba6ce712aa7cd7f9d3eb1a9c25d082cceeb796beba70a3ff45f7379"),
                new("ShibaSwap V1", LiveEvmProtocolKind.V2,
                    "0xcf6daab95c476106eca715d48de4b13287ffdeaa"),
                new("ShibaSwap V2", LiveEvmProtocolKind.V3,
                    "0xab21798d88ee0854f99f1d148c1e59f238e561fe")
            ],
            EvmChainDefinitions.BaseMainnetChainId =>
            [
                new("Uniswap V2", LiveEvmProtocolKind.V2,
                    "0x88a43bbdf9d098eec7bceda4e2494615dfd9bb9c"),
                new("Uniswap V3", LiveEvmProtocolKind.V3,
                    BaseDeploymentRegistry.WrappedEtherUsdcReferencePool),
                new("Uniswap V4", LiveEvmProtocolKind.V4,
                    BaseDeploymentRegistry.UniswapV4StateView,
                    "0x1d8c55f347727c0fb4f5e1b65cdb93639e0c7102580a7d345e1144cd5a718f54"),
                new("PancakeSwap V2", LiveEvmProtocolKind.V2,
                    "0x79474223aedd0339780bacce75abda0be84dcbf9"),
                new("PancakeSwap V3", LiveEvmProtocolKind.V3,
                    "0x72ab388e2e2f6facef59e3c3fa2c4e29011c2d38"),
                new("PancakeSwap Infinity CL", LiveEvmProtocolKind.InfinityCl,
                    BaseDeploymentRegistry.PancakeInfinityClPoolManager,
                    "0x9ed2b133457a9debb64997f932b15a0a81f61718e8a267a4b36fab7b960d788a"),
                new("PancakeSwap Infinity Bin", LiveEvmProtocolKind.InfinityBin,
                    BaseDeploymentRegistry.PancakeInfinityBinPoolManager,
                    "0x34e19068fd32b59dc649757324d827675a736f2be33ec2bef4647bf876c0f6e7"),
                new("Aerodrome Classic", LiveEvmProtocolKind.AerodromeClassic,
                    "0xcdac0d6c6c59727a65f871236188350531885c43"),
                new("Aerodrome Slipstream Initial", LiveEvmProtocolKind.AerodromeSlipstream,
                    "0xb2cc224c1c9fee385f8ad6a55b4d94e92359dc59"),
                new("Aerodrome Slipstream Gauge Caps", LiveEvmProtocolKind.AerodromeSlipstream,
                    "0xc758d81b9b81a6fcdad075bd471874a2c46b54e0"),
                new("Aerodrome Slipstream Gauges V3", LiveEvmProtocolKind.AerodromeSlipstream,
                    "0x3fe04a59ebd38cf06080a6f60a98d124eb59392a")
            ],
            EvmChainDefinitions.BnbMainnetChainId =>
            [
                new("Uniswap V2", LiveEvmProtocolKind.V2,
                    "0x8a1ed8e124fdfbd534bf48baf732e26db9cc0cf4"),
                new("Uniswap V3", LiveEvmProtocolKind.V3,
                    "0x6fe9e9de56356f7edbfcbb29fab7cd69471a4869"),
                new("Uniswap V4", LiveEvmProtocolKind.V4,
                    BnbDeploymentRegistry.UniswapV4StateView,
                    "0x29d0870548bda44f40d9935fb2b1eda917f16310a02cba355a655c8365aa70c8"),
                new("PancakeSwap V2", LiveEvmProtocolKind.V2,
                    "0x16b9a82891338f9ba80e2d6970fdda79d1eb0dae"),
                new("PancakeSwap V3", LiveEvmProtocolKind.V3,
                    BnbDeploymentRegistry.WrappedBnbUsdtReferencePool),
                new("PancakeSwap Infinity CL", LiveEvmProtocolKind.InfinityCl,
                    BnbDeploymentRegistry.PancakeInfinityClPoolManager,
                    "0x32a805e65a3219a79707ab3e75443d75160d798de46613fc960d39cd4a96bb22"),
                new("PancakeSwap Infinity Bin", LiveEvmProtocolKind.InfinityBin,
                    BnbDeploymentRegistry.PancakeInfinityBinPoolManager,
                    "0xa4cfc9e29aa6a115f125701cc63ae060635d8546a09b8c216e716ca85fad9dff")
            ],
            EvmChainDefinitions.RobinhoodMainnetChainId =>
            [
                new("Uniswap V2", LiveEvmProtocolKind.V2,
                    "0x8803c117ccae7b5146297876c2a25df135141c4d"),
                new("Uniswap V3", LiveEvmProtocolKind.V3,
                    RobinhoodDeploymentRegistry.WrappedEtherUsdgReferencePool),
                new("Uniswap V4", LiveEvmProtocolKind.V4,
                    RobinhoodDeploymentRegistry.UniswapV4StateView,
                    "0xa5f23cae4e5c3388c5a8a6b08a83f53e56df8f1a63757e606b362994b68a2361")
            ],
            _ => throw new InvalidOperationException($"No live protocol fixtures exist for chain {chainId}.")
        };
    }
}

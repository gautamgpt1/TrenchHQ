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
    static async Task VerifyLiveEvmProtocolAsync(
        EvmJsonRpcClient rpc,
        LiveEvmProtocolFixture protocol,
        object blockReference,
        string providerLabel,
        CancellationToken cancellationToken)
    {
        var callData = protocol.Kind switch
        {
            LiveEvmProtocolKind.V2 or LiveEvmProtocolKind.AerodromeClassic =>
                EthereumAbi.GetReservesSelector,
            LiveEvmProtocolKind.V3 or LiveEvmProtocolKind.AerodromeSlipstream =>
                EthereumAbi.Slot0Selector,
            LiveEvmProtocolKind.V4 => EthereumAbi.EncodeBytes32Call(
                EthereumAbi.UniswapV4StateViewSlot0Selector,
                protocol.PoolId!),
            LiveEvmProtocolKind.InfinityCl or LiveEvmProtocolKind.InfinityBin =>
                EthereumAbi.EncodeBytes32Call(EthereumAbi.InfinitySlot0Selector, protocol.PoolId!),
            _ => throw new InvalidOperationException($"Unsupported live probe kind {protocol.Kind}.")
        };
        var state = await rpc.CallAsync(
            protocol.TargetAddress,
            callData,
            blockReference,
            cancellationToken);
        var stateData = state.ValueKind == JsonValueKind.String ? state.GetString() : null;
        var stateValid = protocol.Kind switch
        {
            LiveEvmProtocolKind.V2 =>
                EthereumAbi.TryDecodeUniswapV2Reserves(stateData, out _, out _),
            LiveEvmProtocolKind.AerodromeClassic =>
                EthereumAbi.TryDecodeAerodromeClassicReserves(stateData, out _, out _),
            LiveEvmProtocolKind.V3 =>
                EthereumAbi.TryDecodeUniswapV3Slot0(stateData, out _, out _),
            LiveEvmProtocolKind.AerodromeSlipstream =>
                EthereumAbi.TryDecodeAerodromeSlipstreamSlot0(stateData, out _, out _),
            LiveEvmProtocolKind.V4 or LiveEvmProtocolKind.InfinityCl =>
                EthereumAbi.TryDecodeUniswapV4Slot0(stateData, out _, out _),
            LiveEvmProtocolKind.InfinityBin =>
                EthereumAbi.TryDecodeInfinityBinSlot0(stateData, out _),
            _ => false
        };
        Assert(stateValid, $"{providerLabel} returned malformed {protocol.Name} state.");

        if (protocol.Kind is not (LiveEvmProtocolKind.V3
            or LiveEvmProtocolKind.AerodromeSlipstream
            or LiveEvmProtocolKind.V4
            or LiveEvmProtocolKind.InfinityCl))
        {
            return;
        }
        var liquidityCall = protocol.Kind switch
        {
            LiveEvmProtocolKind.V3 or LiveEvmProtocolKind.AerodromeSlipstream =>
                EthereumAbi.LiquiditySelector,
            LiveEvmProtocolKind.V4 => EthereumAbi.EncodeBytes32Call(
                EthereumAbi.UniswapV4StateViewLiquiditySelector,
                protocol.PoolId!),
            LiveEvmProtocolKind.InfinityCl => EthereumAbi.EncodeBytes32Call(
                EthereumAbi.InfinityLiquiditySelector,
                protocol.PoolId!),
            _ => throw new InvalidOperationException($"Unsupported liquidity probe kind {protocol.Kind}.")
        };
        var liquidity = await rpc.CallAsync(
            protocol.TargetAddress,
            liquidityCall,
            blockReference,
            cancellationToken);
        Assert(
            liquidity.ValueKind == JsonValueKind.String
            && EthereumAbi.TryDecodeSingleUnsigned(liquidity.GetString(), 128, out _),
            $"{providerLabel} returned malformed {protocol.Name} liquidity.");
    }
}

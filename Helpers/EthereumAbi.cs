using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;

namespace TrenchHQ.Helpers
{
    internal static class EthereumAbi
    {
        internal const string GetPairSelector = "0xe6a43905";
        internal const string FactorySelector = "0xc45a0155";
        internal const string Token0Selector = "0x0dfe1681";
        internal const string Token1Selector = "0xd21220a7";
        internal const string GetReservesSelector = "0x0902f1ac";
        internal const string StableSelector = "0x22be3de1";
        internal const string IsPoolSelector = "0x5b16ebb7";
        internal const string AerodromeClassicGetPoolSelector = "0x79bc57d5";
        internal const string AerodromeSlipstreamGetPoolSelector = "0x28af8d0b";
        internal const string SymbolSelector = "0x95d89b41";
        internal const string DecimalsSelector = "0x313ce567";
        internal const string GetPoolSelector = "0x1698ee82";
        internal const string FeeSelector = "0xddca3f43";
        internal const string TickSpacingSelector = "0xd0c93a7c";
        internal const string Slot0Selector = "0x3850c7bd";
        internal const string LiquiditySelector = "0x1a686502";
        internal const string UniswapV4StateViewSlot0Selector = "0xc815641c";
        internal const string UniswapV4StateViewLiquiditySelector = "0xfa6793d5";
        internal const string InfinityPoolKeySelector = "0x0e2d484a";
        internal const string InfinitySlot0Selector = "0xc815641c";
        internal const string InfinityLiquiditySelector = "0xfa6793d5";
        internal const string CurveCoinsSelector = "0xc6610657";
        internal const string GetLaunchedTokenSelector = "0x3cf28b5a";
        internal const string LiquidityPoolSelector = "0x665a11ca";

        internal static bool UniswapV4HookReturnsSwapDelta(string hookAddress)
        {
            return !EvmAddress.TryNormalize(hookAddress, out var normalized)
                   || !byte.TryParse(
                       normalized.AsSpan(^2),
                       NumberStyles.AllowHexSpecifier,
                       CultureInfo.InvariantCulture,
                       out var flags)
                   || (flags & 0x0c) != 0;
        }

        internal static string EncodeAddressPairCall(string selector, string first, string second)
        {
            if (!IsSelector(selector)
                || !EvmAddress.TryNormalize(first, out var normalizedFirst)
                || !EvmAddress.TryNormalize(second, out var normalizedSecond))
            {
                throw new ArgumentException("The ABI call contains an invalid selector or address.");
            }
            return selector.ToLowerInvariant()
                   + normalizedFirst[2..].PadLeft(64, '0')
                   + normalizedSecond[2..].PadLeft(64, '0');
        }

        internal static string EncodeAddressCall(string selector, string address)
        {
            if (!IsSelector(selector) || !EvmAddress.TryNormalize(address, out var normalized))
            {
                throw new ArgumentException("The ABI call contains an invalid selector or address.");
            }
            return selector.ToLowerInvariant() + normalized[2..].PadLeft(64, '0');
        }

        internal static string EncodeAddressPairFeeCall(
            string selector,
            string first,
            string second,
            uint fee)
        {
            if (fee >= 1 << 24)
            {
                throw new ArgumentOutOfRangeException(nameof(fee));
            }
            return EncodeAddressPairCall(selector, first, second)
                   + fee.ToString("x64", CultureInfo.InvariantCulture);
        }

        internal static string EncodeAddressPairBooleanCall(
            string selector,
            string first,
            string second,
            bool value)
        {
            return EncodeAddressPairCall(selector, first, second)
                   + (value ? "1" : "0").PadLeft(64, '0');
        }

        internal static string EncodeAddressPairSignedCall(
            string selector,
            string first,
            string second,
            int value,
            int valueBits)
        {
            if (valueBits is <= 0 or > 32)
            {
                throw new ArgumentOutOfRangeException(nameof(valueBits));
            }
            var limit = BigInteger.One << (valueBits - 1);
            var integer = new BigInteger(value);
            if (integer < -limit || integer >= limit)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            var encoded = integer.Sign < 0 ? (BigInteger.One << 256) + integer : integer;
            return EncodeAddressPairCall(selector, first, second)
                   + encoded.ToString("x64", CultureInfo.InvariantCulture);
        }

        internal static string EncodeBytes32Call(string selector, string value)
        {
            if (!IsSelector(selector) || !EvmAddress.IsHash(value))
            {
                throw new ArgumentException("The ABI call contains an invalid selector or bytes32 value.");
            }
            return selector.ToLowerInvariant() + value[2..].ToLowerInvariant();
        }

        internal static string EncodeUnsignedCall(string selector, uint value)
        {
            if (!IsSelector(selector))
            {
                throw new ArgumentException("An EVM function selector is required.", nameof(selector));
            }
            return selector + value.ToString("x64", CultureInfo.InvariantCulture);
        }

        internal static bool TryDecodeAddress(string? data, out string address)
        {
            return TryDecodeAddress(data, 0, out address);
        }

        internal static bool TryDecodeAddress(string? data, int index, out string address)
        {
            address = string.Empty;
            if (!TryGetWord(data, index, out var word)
                || !word[..24].Equals(new string('0', 24), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return EvmAddress.TryNormalize("0x" + word[24..], out address);
        }

        internal static bool TryDecodeByte(string? data, out byte value)
        {
            value = 0;
            return TryGetWord(data, 0, out var word)
                   && BigInteger.TryParse(
                       word,
                       NumberStyles.AllowHexSpecifier,
                       CultureInfo.InvariantCulture,
                       out var integer)
                   && integer >= byte.MinValue
                   && integer <= byte.MaxValue
                   && byte.TryParse(integer.ToString(CultureInfo.InvariantCulture), out value);
        }

        internal static bool TryDecodeString(string? data, out string value)
        {
            value = string.Empty;
            if (!EvmAddress.IsData(data, 4096) || data!.Length < 66)
            {
                return false;
            }
            string encoded;
            if (data.Length == 66)
            {
                encoded = data[2..].TrimEnd('0');
                if (encoded.Length % 2 != 0)
                {
                    encoded += "0";
                }
            }
            else if (TryDecodeUnsigned(data, 0, 32, out var offset)
                     && offset == 32
                     && TryDecodeUnsigned(data, 1, 32, out var length)
                     && length > BigInteger.Zero
                     && length <= 64
                     && data.Length >= 2 + 128 + (int)length * 2)
            {
                encoded = data.Substring(2 + 128, (int)length * 2);
            }
            else
            {
                return false;
            }
            try
            {
                value = new UTF8Encoding(false, true).GetString(Convert.FromHexString(encoded)).Trim();
                return value.Length is > 0 and <= 32
                       && value.All(static character =>
                           !char.IsControl(character)
                           && char.GetUnicodeCategory(character) != UnicodeCategory.OtherNotAssigned);
            }
            catch (FormatException)
            {
                value = string.Empty;
                return false;
            }
            catch (DecoderFallbackException)
            {
                value = string.Empty;
                return false;
            }
        }

        internal static bool TryDecodeBoolean(string? data, out bool value)
        {
            value = false;
            if (!HasExactWordCount(data, 1)
                || !TryGetUnsignedWord(data, 0, out var integer)
                || integer < BigInteger.Zero
                || integer > BigInteger.One)
            {
                return false;
            }
            value = integer == BigInteger.One;
            return true;
        }

        internal static bool TryDecodeUniswapV2Reserves(
            string? data,
            out BigInteger reserve0,
            out BigInteger reserve1)
        {
            reserve0 = BigInteger.Zero;
            reserve1 = BigInteger.Zero;
            return TryGetUnsignedWord(data, 0, out reserve0)
                   && TryGetUnsignedWord(data, 1, out reserve1)
                   && reserve0 < (BigInteger.One << 112)
                   && reserve1 < (BigInteger.One << 112)
                   && TryGetUnsignedWord(data, 2, out var timestamp)
                   && timestamp < (BigInteger.One << 32);
        }

        internal static bool TryDecodeAerodromeClassicReserves(
            string? data,
            out BigInteger reserve0,
            out BigInteger reserve1)
        {
            reserve0 = BigInteger.Zero;
            reserve1 = BigInteger.Zero;
            return HasExactWordCount(data, 3)
                   && TryGetUnsignedWord(data, 0, out reserve0)
                   && TryGetUnsignedWord(data, 1, out reserve1)
                   && TryGetUnsignedWord(data, 2, out _);
        }

        internal static bool TryDecodePonsV2Reserves(
            string? data,
            out BigInteger quoteReserve,
            out BigInteger tokenReserve)
        {
            quoteReserve = BigInteger.Zero;
            tokenReserve = BigInteger.Zero;
            return HasExactWordCount(data, 2)
                   && TryGetUnsignedWord(data, 0, out quoteReserve)
                   && TryGetUnsignedWord(data, 1, out tokenReserve)
                   && quoteReserve > BigInteger.Zero
                   && tokenReserve > BigInteger.Zero;
        }

        internal static bool TryDecodeUnsigned(
            string? data,
            int index,
            int maximumBits,
            out BigInteger value)
        {
            value = BigInteger.Zero;
            return maximumBits is > 0 and <= 256
                   && TryGetUnsignedWord(data, index, out value)
                   && value < (BigInteger.One << maximumBits);
        }

        internal static bool TryDecodeSingleUnsigned(
            string? data,
            int maximumBits,
            out BigInteger value)
        {
            value = BigInteger.Zero;
            return HasExactWordCount(data, 1)
                   && TryDecodeUnsigned(data, 0, maximumBits, out value);
        }

        internal static bool TryDecodeSigned(
            string? data,
            int index,
            int valueBits,
            out BigInteger value)
        {
            value = BigInteger.Zero;
            if (valueBits is <= 0 or > 256
                || !TryGetUnsignedWord(data, index, out var unsigned))
            {
                return false;
            }
            value = unsigned >= (BigInteger.One << 255)
                ? unsigned - (BigInteger.One << 256)
                : unsigned;
            var limit = BigInteger.One << (valueBits - 1);
            return value >= -limit && value < limit;
        }

        internal static bool TryDecodeUniswapV3Slot0(
            string? data,
            out BigInteger sqrtPriceX96,
            out int tick)
        {
            sqrtPriceX96 = BigInteger.Zero;
            tick = 0;
            return TryDecodeUnsigned(data, 0, 160, out sqrtPriceX96)
                   && sqrtPriceX96 > 0
                   && TryDecodeSigned(data, 1, 24, out var decodedTick)
                   && int.TryParse(
                       decodedTick.ToString(CultureInfo.InvariantCulture),
                       CultureInfo.InvariantCulture,
                       out tick)
                   && TryGetWord(data, 6, out _);
        }

        internal static bool TryDecodeAerodromeSlipstreamSlot0(
            string? data,
            out BigInteger sqrtPriceX96,
            out int tick)
        {
            sqrtPriceX96 = BigInteger.Zero;
            tick = 0;
            return HasExactWordCount(data, 6)
                   && TryDecodeUnsigned(data, 0, 160, out sqrtPriceX96)
                   && sqrtPriceX96 > 0
                   && TryDecodeSigned(data, 1, 24, out var decodedTick)
                   && decodedTick >= -887272
                   && decodedTick <= 887272
                   && int.TryParse(
                       decodedTick.ToString(CultureInfo.InvariantCulture),
                       CultureInfo.InvariantCulture,
                       out tick);
        }

        internal static bool TryDecodeUniswapV4Initialize(
            string[]? topics,
            string? data,
            out EthereumV4Initialize initialize)
        {
            initialize = new EthereumV4Initialize();
            if (topics is not { Length: 4 }
                || !topics[0].Equals(
                    EvmWebSocketStreamSource.UniswapV4InitializeTopic,
                    StringComparison.OrdinalIgnoreCase)
                || !EvmAddress.IsHash(topics[1])
                || !TryDecodeTopicAddress(topics[2], out var currency0)
                || !TryDecodeTopicAddress(topics[3], out var currency1)
                || !TryDecodeUnsigned(data, 0, 24, out var fee)
                || fee > uint.MaxValue
                || !TryDecodeSigned(data, 1, 24, out var tickSpacing)
                || tickSpacing <= 0
                || tickSpacing > int.MaxValue
                || !TryGetWord(data, 2, out var hookWord)
                || !TryDecodeAddress("0x" + hookWord, out var hooks)
                || !TryDecodeUnsigned(data, 3, 160, out var sqrtPriceX96)
                || sqrtPriceX96 <= 0
                || !TryDecodeSigned(data, 4, 24, out var tick)
                || tick < -887272
                || tick > 887272)
            {
                return false;
            }
            initialize = new EthereumV4Initialize
            {
                PoolId = topics[1].ToLowerInvariant(),
                Currency0 = currency0,
                Currency1 = currency1,
                Fee = (uint)fee,
                TickSpacing = (int)tickSpacing,
                HookAddress = hooks,
                SqrtPriceX96 = sqrtPriceX96,
                Tick = (int)tick
            };
            return true;
        }

        internal static bool TryDecodeUniswapV4Slot0(
            string? data,
            out BigInteger sqrtPriceX96,
            out int tick)
        {
            sqrtPriceX96 = BigInteger.Zero;
            tick = 0;
            return TryDecodeUnsigned(data, 0, 160, out sqrtPriceX96)
                   && sqrtPriceX96 > 0
                   && TryDecodeSigned(data, 1, 24, out var decodedTick)
                   && decodedTick >= -887272
                   && decodedTick <= 887272
                   && int.TryParse(
                       decodedTick.ToString(CultureInfo.InvariantCulture),
                       CultureInfo.InvariantCulture,
                       out tick)
                   && TryDecodeUnsigned(data, 2, 24, out _)
                   && TryDecodeUnsigned(data, 3, 24, out _);
        }

        internal static bool TryDecodeInfinityPoolKey(
            string? data,
            out PancakeInfinityPoolKey poolKey)
        {
            poolKey = new PancakeInfinityPoolKey();
            if (!HasExactWordCount(data, 6)
                || !TryGetWord(data, 0, out var currency0Word)
                || !TryDecodeAddress("0x" + currency0Word, out var currency0)
                || !TryGetWord(data, 1, out var currency1Word)
                || !TryDecodeAddress("0x" + currency1Word, out var currency1)
                || !TryGetWord(data, 2, out var hookWord)
                || !TryDecodeAddress("0x" + hookWord, out var hookAddress)
                || !TryGetWord(data, 3, out var managerWord)
                || !TryDecodeAddress("0x" + managerWord, out var poolManager)
                || !TryDecodeUnsigned(data, 4, 24, out var fee)
                || fee > uint.MaxValue
                || !TryGetUnsignedWord(data, 5, out var parameters))
            {
                return false;
            }
            poolKey = new PancakeInfinityPoolKey
            {
                Currency0 = currency0,
                Currency1 = currency1,
                HookAddress = hookAddress,
                PoolManager = poolManager,
                Fee = (uint)fee,
                Parameters = parameters
            };
            return true;
        }

        internal static bool TryDecodeInfinityClParameters(BigInteger parameters, out int tickSpacing)
        {
            tickSpacing = 0;
            var encoded = (parameters >> 16) & ((BigInteger.One << 24) - 1);
            if (encoded >= BigInteger.One << 23)
            {
                encoded -= BigInteger.One << 24;
            }
            return encoded > 0
                   && encoded <= short.MaxValue
                   && int.TryParse(
                       encoded.ToString(CultureInfo.InvariantCulture),
                       CultureInfo.InvariantCulture,
                       out tickSpacing);
        }

        internal static bool TryDecodeInfinityBinParameters(BigInteger parameters, out ushort binStep)
        {
            binStep = 0;
            var encoded = (parameters >> 16) & ushort.MaxValue;
            return encoded > 0
                   && ushort.TryParse(
                       encoded.ToString(CultureInfo.InvariantCulture),
                       CultureInfo.InvariantCulture,
                       out binStep);
        }

        internal static bool TryDecodeInfinityBinSlot0(string? data, out uint activeId)
        {
            activeId = 0;
            return HasExactWordCount(data, 3)
                   && TryDecodeUnsigned(data, 0, 24, out var decodedActiveId)
                   && uint.TryParse(
                       decodedActiveId.ToString(CultureInfo.InvariantCulture),
                       CultureInfo.InvariantCulture,
                       out activeId)
                   && TryDecodeUnsigned(data, 1, 24, out _)
                   && TryDecodeUnsigned(data, 2, 24, out _);
        }

        private static bool TryDecodeTopicAddress(string? topic, out string address)
        {
            address = string.Empty;
            return EvmAddress.IsHash(topic)
                   && topic![2..26].Equals(new string('0', 24), StringComparison.OrdinalIgnoreCase)
                   && EvmAddress.TryNormalize("0x" + topic[26..], out address);
        }

        private static bool TryGetUnsignedWord(string? data, int index, out BigInteger value)
        {
            value = BigInteger.Zero;
            return TryGetWord(data, index, out var word)
                   && BigInteger.TryParse(
                       "0" + word,
                       NumberStyles.AllowHexSpecifier,
                       CultureInfo.InvariantCulture,
                       out value);
        }

        private static bool TryGetWord(string? data, int index, out string word)
        {
            word = string.Empty;
            var offset = 2 + index * 64;
            if (!EvmAddress.IsData(data, 4096) || data!.Length < offset + 64)
            {
                return false;
            }
            word = data.Substring(offset, 64);
            return true;
        }

        private static bool HasExactWordCount(string? data, int count)
        {
            return data != null
                   && data.Length == 2 + count * 64
                   && EvmAddress.IsData(data, 4096);
        }

        private static bool IsSelector(string? value)
        {
            if (value == null || value.Length != 10 || !value.StartsWith("0x", StringComparison.Ordinal))
            {
                return false;
            }
            foreach (var character in value.AsSpan(2))
            {
                if (!Uri.IsHexDigit(character))
                {
                    return false;
                }
            }
            return true;
        }
    }

    internal sealed class EthereumV4Initialize
    {
        internal string PoolId { get; init; } = string.Empty;
        internal string Currency0 { get; init; } = string.Empty;
        internal string Currency1 { get; init; } = string.Empty;
        internal uint Fee { get; init; }
        internal int TickSpacing { get; init; }
        internal string HookAddress { get; init; } = string.Empty;
        internal BigInteger SqrtPriceX96 { get; init; }
        internal int Tick { get; init; }
    }

    internal sealed class PancakeInfinityPoolKey
    {
        internal string Currency0 { get; init; } = string.Empty;
        internal string Currency1 { get; init; } = string.Empty;
        internal string HookAddress { get; init; } = string.Empty;
        internal string PoolManager { get; init; } = string.Empty;
        internal uint Fee { get; init; }
        internal BigInteger Parameters { get; init; }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace TrenchHQ.Core.OnChain.Evm
{
    internal static class EvmMulticall3
    {
        internal const string Address = "0xca11bde05977b3631167028862be2a173976ca11";
        internal const int MaximumCalls = 256;
        private const string TryAggregateSelector = "0xbce38bd7";

        internal static string EncodePair(string target, string firstCall, string secondCall)
        {
            return EncodeCalls([(target, firstCall), (target, secondCall)]);
        }

        internal static string EncodeCalls(IReadOnlyList<(string Target, string Data)> calls)
        {
            if (calls.Count is < 1 or > MaximumCalls)
            {
                throw new ArgumentException("The multicall request count is invalid.", nameof(calls));
            }
            var encoded = new string[calls.Count];
            for (var index = 0; index < calls.Count; index++)
            {
                var (target, data) = calls[index];
                if (!EvmAddress.TryNormalize(target, out var address)
                    || !EvmAddress.IsData(data, 256)
                    || data.Length < 10)
                {
                    throw new ArgumentException("The multicall target or call data is invalid.", nameof(calls));
                }
                encoded[index] = EncodeCall(address, data);
            }
            var result = new StringBuilder(TryAggregateSelector + Word(1) + Word(64) + Word(calls.Count));
            var offset = calls.Count * 32;
            foreach (var call in encoded)
            {
                result.Append(Word(offset));
                offset += call.Length / 2;
            }
            foreach (var call in encoded)
            {
                result.Append(call);
            }
            return result.ToString();
        }

        internal static bool TryDecodePair(string? result, out string first, out string second)
        {
            first = string.Empty;
            second = string.Empty;
            if (!TryDecodeResults(result, 2, out var results))
            {
                return false;
            }
            first = results[0];
            second = results[1];
            return true;
        }

        internal static bool TryDecodeResults(string? result, int expectedCount, out string[] results)
        {
            results = [];
            if (expectedCount is < 1 or > MaximumCalls
                || !EvmAddress.IsData(result, 128 * 1024)
                || result!.Length % 64 != 2
                || !TryReadWord(result, 0, out var arrayOffset)
                || arrayOffset != 32
                || !TryReadWord(result, 32, out var count)
                || count != expectedCount)
            {
                return false;
            }
            var decoded = new string[count];
            var nextOffset = 64 + count * 32;
            for (var index = 0; index < count; index++)
            {
                if (!TryReadWord(result, 64 + index * 32, out var offset)
                    || offset != nextOffset - 64
                    || !TryReadResult(result, nextOffset, out decoded[index], out nextOffset))
                {
                    return false;
                }
            }
            if (nextOffset != (result.Length - 2) / 2)
            {
                return false;
            }
            results = decoded;
            return true;
        }

        private static string EncodeCall(string address, string data)
        {
            var bytes = (data.Length - 2) / 2;
            return address[2..].PadLeft(64, '0')
                   + Word(64) + Word(bytes)
                   + data[2..].ToLowerInvariant().PadRight(((bytes + 31) / 32) * 64, '0');
        }

        private static bool TryReadResult(
            string result,
            int tupleOffset,
            out string data,
            out int nextOffset)
        {
            data = string.Empty;
            nextOffset = 0;
            if (!TryReadWord(result, tupleOffset, out var success)
                || success != 1
                || !TryReadWord(result, tupleOffset + 32, out var dataOffset)
                || dataOffset != 64
                || !TryReadWord(result, tupleOffset + 64, out var length)
                || length == 0
                || length > 4096)
            {
                return false;
            }

            var start = (long)tupleOffset + 96;
            var end = start + ((length + 31) / 32) * 32;
            if (end > (result.Length - 2) / 2)
            {
                return false;
            }
            data = "0x" + result.Substring(2 + checked((int)start) * 2, length * 2);
            nextOffset = checked((int)end);
            return true;
        }

        private static bool TryReadWord(string data, int byteOffset, out int value)
        {
            value = 0;
            if (byteOffset < 0 || (long)byteOffset + 32 > (data.Length - 2) / 2
                || !BigInteger.TryParse(
                    "0" + data.Substring(2 + byteOffset * 2, 64),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out var decoded)
                || decoded > int.MaxValue)
            {
                return false;
            }
            value = (int)decoded;
            return true;
        }

        private static string Word(int value) => value.ToString("x64", CultureInfo.InvariantCulture);
    }
}

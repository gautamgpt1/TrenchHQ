using System;

namespace TrenchHQ.Core.OnChain.Solana
{
    internal static class SolanaBase58
    {
        private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

        internal static string Encode(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty)
            {
                return string.Empty;
            }

            var zeroCount = 0;
            while (zeroCount < bytes.Length && bytes[zeroCount] == 0)
            {
                zeroCount++;
            }

            var digits = new byte[bytes.Length * 138 / 100 + 1];
            var digitCount = 0;
            for (var index = zeroCount; index < bytes.Length; index++)
            {
                var carry = (int)bytes[index];
                var position = 0;
                for (; position < digitCount; position++)
                {
                    carry += digits[position] << 8;
                    digits[position] = (byte)(carry % 58);
                    carry /= 58;
                }
                while (carry > 0)
                {
                    digits[digitCount++] = (byte)(carry % 58);
                    carry /= 58;
                }
            }

            var chars = new char[zeroCount + digitCount];
            Array.Fill(chars, '1', 0, zeroCount);
            for (var index = 0; index < digitCount; index++)
            {
                chars[zeroCount + index] = Alphabet[digits[digitCount - index - 1]];
            }
            return new string(chars);
        }

        internal static byte[] Decode(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length == 0)
            {
                return [];
            }

            var zeroCount = 0;
            while (zeroCount < value.Length && value[zeroCount] == '1')
            {
                zeroCount++;
            }

            var bytes = new byte[value.Length * 733 / 1000 + 1];
            var byteCount = 0;
            for (var index = zeroCount; index < value.Length; index++)
            {
                var digit = Alphabet.IndexOf(value[index]);
                if (digit < 0)
                {
                    throw new FormatException("The value is not valid base58.");
                }

                var carry = digit;
                var position = 0;
                for (; position < byteCount; position++)
                {
                    carry += bytes[position] * 58;
                    bytes[position] = (byte)(carry & 0xff);
                    carry >>= 8;
                }
                while (carry > 0)
                {
                    bytes[byteCount++] = (byte)(carry & 0xff);
                    carry >>= 8;
                }
            }

            var result = new byte[zeroCount + byteCount];
            for (var index = 0; index < byteCount; index++)
            {
                result[zeroCount + index] = bytes[byteCount - index - 1];
            }
            return result;
        }
    }
}

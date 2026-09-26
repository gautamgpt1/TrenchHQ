using System;
using System.Globalization;

namespace TrenchHQ.Helpers
{
    internal static class EvmAddress
    {
        internal static bool TryNormalize(string? value, out string normalized)
        {
            normalized = string.Empty;
            if (!IsFixedHex(value, 20))
            {
                return false;
            }
            normalized = "0x" + value![2..].ToLowerInvariant();
            return true;
        }

        internal static bool IsHash(string? value)
        {
            return IsFixedHex(value, 32);
        }

        internal static bool IsData(string? value, int maximumBytes)
        {
            if (value == null
                || value.Length < 2
                || !value.StartsWith("0x", StringComparison.Ordinal)
                || (value.Length - 2) % 2 != 0
                || (value.Length - 2) / 2 > maximumBytes)
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

        internal static bool TryParseQuantity(string? value, out ulong quantity)
        {
            quantity = 0;
            if (string.IsNullOrWhiteSpace(value)
                || !value.StartsWith("0x", StringComparison.Ordinal)
                || value.Length <= 2
                || value.Length > 18)
            {
                return false;
            }
            return ulong.TryParse(
                value.AsSpan(2),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out quantity);
        }

        private static bool IsFixedHex(string? value, int byteCount)
        {
            if (value == null || value.Length != 2 + byteCount * 2
                || !value.StartsWith("0x", StringComparison.Ordinal))
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
}

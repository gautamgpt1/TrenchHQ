using System;
using System.Globalization;
using System.Numerics;

namespace TrenchHQ.Helpers
{
    internal static class PriceFormattingRules
    {
        internal const int MaximumDisplayLength = 18;

        internal static string Format(double value)
        {
            if (!double.IsFinite(value))
            {
                return "$--";
            }

            var magnitude = Math.Abs(value);
            if (magnitude >= 1_000_000_000d || magnitude is > 0d and < 0.00000001d)
            {
                return "$" + value.ToString("0.######E+0", CultureInfo.InvariantCulture);
            }

            if (magnitude >= 1000d) return "$" + value.ToString("#,0.00", CultureInfo.InvariantCulture);
            if (magnitude >= 1d) return "$" + value.ToString("0.00", CultureInfo.InvariantCulture);
            if (magnitude >= 0.01d) return "$" + value.ToString("0.0000", CultureInfo.InvariantCulture);
            if (magnitude >= 0.0001d) return "$" + value.ToString("0.000000", CultureInfo.InvariantCulture);
            return "$" + value.ToString("0.00000000", CultureInfo.InvariantCulture);
        }

        internal static string FormatExact(
            string coefficient,
            uint scale,
            string prefix = "",
            string suffix = "")
        {
            if (!BigInteger.TryParse(coefficient, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return "$--";
            }
            if (parsed.IsZero)
            {
                return prefix + "0.00" + suffix;
            }

            var scientific = $"{coefficient}E-{scale}";
            if (!double.TryParse(
                    scientific,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value)
                || !double.IsFinite(value)
                || value == 0d)
            {
                return prefix + FormatScientific(parsed, scale) + suffix;
            }

            var magnitude = Math.Abs(value);
            var formatted = magnitude switch
            {
                >= 1_000_000_000d => value.ToString("0.######E+0", CultureInfo.InvariantCulture),
                > 0d and < 0.00000001d => value.ToString("0.######E+0", CultureInfo.InvariantCulture),
                >= 1000d => value.ToString("#,0.00", CultureInfo.InvariantCulture),
                >= 1d => value.ToString("0.00", CultureInfo.InvariantCulture),
                >= 0.01d => value.ToString("0.0000", CultureInfo.InvariantCulture),
                >= 0.0001d => value.ToString("0.000000", CultureInfo.InvariantCulture),
                _ => value.ToString("0.00000000", CultureInfo.InvariantCulture)
            };
            return prefix + formatted + suffix;
        }

        private static string FormatScientific(BigInteger coefficient, uint scale)
        {
            var sign = coefficient.Sign < 0 ? "-" : string.Empty;
            var digits = BigInteger.Abs(coefficient).ToString(CultureInfo.InvariantCulture);
            var exponent = (long)digits.Length - 1L - scale;
            var significant = digits.Length == 1
                ? digits
                : digits[0] + "." + digits[1..Math.Min(digits.Length, 7)].TrimEnd('0');
            return $"{sign}{significant.TrimEnd('.')}E{(exponent >= 0 ? "+" : string.Empty)}{exponent}";
        }
    }
}

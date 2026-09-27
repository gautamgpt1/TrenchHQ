using System;

namespace TrenchHQ.Core.Panels
{
    internal static class PanelContentSizes
    {
        internal const string Small = "small";
        internal const string Standard = "standard";
        internal const string Large = "large";

        internal static readonly string[] All = [Small, Standard, Large];
    }

    internal static class PanelContentSizeRules
    {
        internal static string Normalize(string? value)
        {
            foreach (var candidate in PanelContentSizes.All)
            {
                if (string.Equals(candidate, value?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            return PanelContentSizes.Standard;
        }

        internal static double GetScale(string? value) => Normalize(value) switch
        {
            PanelContentSizes.Small => 0.85d,
            PanelContentSizes.Large => 1.25d,
            _ => 1d
        };

        internal static double NormalizeScale(double value) =>
            double.IsFinite(value) ? Math.Clamp(value, 0.75d, 1.5d) : 1d;
    }
}

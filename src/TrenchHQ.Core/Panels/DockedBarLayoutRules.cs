using System;

namespace TrenchHQ.Core.Panels
{
    internal static class DockedBarLayoutRules
    {
        internal static readonly string[] Edges = ["Top", "Bottom", "Left", "Right"];
        internal const int MaximumThickness = 1200;
        internal const double PriceTickerHorizontalItemWidthDip = 210;
        internal const double PriceTickerHorizontalItemHeightDip = 35;
        internal const double PriceTickerHorizontalControlsInsetDip = 108;
        internal const double PriceTickerVerticalItemWidthDip = 150;
        internal const double PriceTickerVerticalItemHeightDip = 52;

        // Opposite bars together must leave at least 40% of this dimension free.
        internal static int GetMaximumThickness(int monitorSpan) =>
            (int)Math.Clamp(Math.Floor(monitorSpan * 0.3d), 1, MaximumThickness);

        internal static int GetMinimumThickness(string? edge) => IsHorizontal(edge) ? 36 : 96;

        // Fixed usable window area, wide enough for the pin actions, plus 44-DIP controls.
        internal static (int Width, int Height) MinimumApplicationWindowSize(double dpiScale)
        {
            var dpi = double.IsFinite(dpiScale) ? Math.Max(1, dpiScale) : 1;
            return ((int)Math.Ceiling(360 * dpi) + 2, (int)Math.Ceiling(244 * dpi) + 2);
        }

        internal static bool IsHorizontal(string? edge)
        {
            var normalized = NormalizeEdge(edge);
            return normalized is "Top" or "Bottom";
        }

        internal static string NormalizeEdge(string? edge)
        {
            foreach (var candidate in Edges)
            {
                if (string.Equals(candidate, edge, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            return "Top";
        }

        internal static int ClampThickness(string? edge, int thickness)
        {
            return Math.Clamp(thickness, GetMinimumThickness(edge), MaximumThickness);
        }

        internal static int GetDefaultThickness(string? edge)
        {
            return IsHorizontal(edge) ? 50 : 180;
        }

        internal static int GetRuntimeThickness(string? edge, int configuredThickness, double textScaleFactor,
            int monitorSpan = int.MaxValue, double rasterizationScale = 1, double contentScale = 1)
        {
            var panelScale = PanelContentSizeRules.NormalizeScale(contentScale);
            var configured = (int)Math.Ceiling(ClampThickness(edge, configuredThickness) * panelScale);
            var scale = (double.IsFinite(textScaleFactor) ? Math.Max(1d, textScaleFactor) : 1d) * panelScale;
            var dpiScale = double.IsFinite(rasterizationScale) ? Math.Max(1d, rasterizationScale) : 1d;
            var accessibleMinimum = (int)Math.Min(MaximumThickness,
                Math.Ceiling(GetMinimumThickness(edge) * scale * dpiScale));
            var maximum = GetMaximumThickness(monitorSpan);
            return Math.Min(maximum, Math.Max(configured, accessibleMinimum));
        }

        internal static int GetAutomaticPriceTickerThickness(
            string? edge,
            int instrumentCount,
            double availablePrimarySpanDip,
            double textScaleFactor,
            int monitorSpan,
            double rasterizationScale,
            double measuredCrossSpanDip = double.NaN,
            double contentScale = 1)
        {
            var horizontal = IsHorizontal(edge);
            var textScale = double.IsFinite(textScaleFactor) ? Math.Max(1d, textScaleFactor) : 1d;
            var panelScale = PanelContentSizeRules.NormalizeScale(contentScale);
            var dpiScale = double.IsFinite(rasterizationScale) ? Math.Max(1d, rasterizationScale) : 1d;
            var primarySpan = double.IsFinite(availablePrimarySpanDip)
                ? Math.Max(1d, availablePrimarySpanDip)
                : 1d;
            var widthScale = 1d + ((textScale - 1d) / 2d);
            var primaryItemSize = horizontal
                ? PriceTickerHorizontalItemWidthDip * panelScale * widthScale
                : PriceTickerVerticalItemHeightDip * panelScale * textScale;
            var itemsPerBand = Math.Max(1, (int)Math.Floor(primarySpan / primaryItemSize));
            var itemCount = Math.Max(0, instrumentCount);
            var bandCount = Math.Max(1, (itemCount + itemsPerBand - 1) / itemsPerBand);
            if (horizontal && itemCount > 0)
            {
                var firstBandCapacity = Math.Max(1, (int)Math.Floor(
                    Math.Max(1d, primarySpan - (PriceTickerHorizontalControlsInsetDip * panelScale)) / primaryItemSize));
                bandCount = 1 + Math.Max(0,
                    (itemCount - firstBandCapacity + itemsPerBand - 1) / itemsPerBand);
            }
            var crossItemSize = horizontal
                ? PriceTickerHorizontalItemHeightDip * panelScale * textScale
                : PriceTickerVerticalItemWidthDip * panelScale * widthScale;
            var maximum = GetMaximumThickness(monitorSpan);
            var measuredCrossSpan = double.IsFinite(measuredCrossSpanDip) && measuredCrossSpanDip > 0
                ? measuredCrossSpanDip
                : 0;
            var desired = itemCount == 0
                ? 0
                : (int)Math.Min(maximum, Math.Ceiling(
                    (measuredCrossSpan > 0 ? measuredCrossSpan : bandCount * crossItemSize) * dpiScale));
            var minimum = itemCount > 0
                ? (int)Math.Min(maximum, Math.Ceiling(
                    (horizontal ? PriceTickerHorizontalItemHeightDip : PriceTickerVerticalItemWidthDip)
                    * panelScale * dpiScale))
                : GetRuntimeThickness(
                    edge,
                    GetMinimumThickness(edge),
                    textScale,
                    monitorSpan,
                    dpiScale,
                    panelScale);
            return Math.Min(maximum, Math.Max(minimum, desired));
        }
    }
}

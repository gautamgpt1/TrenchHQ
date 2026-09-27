using TrenchHQ.Core.Widgets;
using System;

namespace TrenchHQ.Core.Panels
{
    internal static class OverlayLayoutRules
    {
        internal const double DefaultWidth = 350;
        internal const double MinimumWidth = PanelWidgetHostRules.PriceTickerOverlayMinimumWidth;
        internal const double MaximumWidth = 600;

        internal static bool HasAdjustableHeight(bool useApplicationWindow, string? widgetType) =>
            useApplicationWindow || widgetType is PanelWidgetTypes.XTimeline or PanelWidgetTypes.Website or PanelWidgetTypes.WalletActivity;

        internal static double MinimumWidthFor(string? widgetType) => widgetType == PanelWidgetTypes.Website ? 320 : MinimumWidth;

        internal static double ClampHeight(double height) =>
            double.IsFinite(height) ? Math.Clamp(height, 240, 900) : 480;

        internal static (int Width, int Height) ClampPixelSize(int width, int height, int workWidth, int workHeight) =>
            (Math.Clamp(width, 1, Math.Max(1, workWidth - 16)), Math.Clamp(height, 1, Math.Max(1, workHeight - 16)));

        internal static double ClampWidth(double width, string? widgetType = null)
        {
            return double.IsFinite(width)
                ? Math.Clamp(width, MinimumWidthFor(widgetType), MaximumWidth)
                : DefaultWidth;
        }
    }
}

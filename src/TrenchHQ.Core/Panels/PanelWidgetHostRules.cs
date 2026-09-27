using TrenchHQ.Core.Widgets;

namespace TrenchHQ.Core.Panels
{
    internal enum PanelWidgetHostType
    {
        Overlay,
        DockedBar
    }

    internal enum PanelWidgetOrientation
    {
        Vertical,
        Horizontal
    }

    internal enum PanelWidgetDensity
    {
        Comfortable,
        Compact
    }

    internal readonly record struct PanelWidgetHostContext(
        PanelWidgetHostType HostType,
        PanelWidgetOrientation Orientation,
        PanelWidgetDensity Density);

    internal readonly record struct PanelWidgetMinimumSize(double? Width, double? Height);

    internal static class PanelWidgetHostRules
    {
        internal const double PriceTickerOverlayMinimumWidth = 250;
        internal const double PriceTickerHorizontalBarMinimumHeight = 40;
        internal const double PriceTickerVerticalBarMinimumWidth = 140;

        internal static PanelWidgetHostContext ForOverlay()
        {
            return new PanelWidgetHostContext(
                PanelWidgetHostType.Overlay,
                PanelWidgetOrientation.Vertical,
                PanelWidgetDensity.Comfortable);
        }

        internal static PanelWidgetHostContext ForDockedBar(string? edge)
        {
            return new PanelWidgetHostContext(
                PanelWidgetHostType.DockedBar,
                DockedBarLayoutRules.IsHorizontal(edge)
                    ? PanelWidgetOrientation.Horizontal
                    : PanelWidgetOrientation.Vertical,
                PanelWidgetDensity.Compact);
        }

        internal static PanelWidgetMinimumSize GetPriceTickerMinimumSize(PanelWidgetHostContext context)
        {
            return context.HostType switch
            {
                PanelWidgetHostType.Overlay => new PanelWidgetMinimumSize(PriceTickerOverlayMinimumWidth, null),
                PanelWidgetHostType.DockedBar when context.Orientation == PanelWidgetOrientation.Horizontal
                    => new PanelWidgetMinimumSize(null, PriceTickerHorizontalBarMinimumHeight),
                _ => new PanelWidgetMinimumSize(PriceTickerVerticalBarMinimumWidth, null)
            };
        }

        internal static bool SupportsWidget(string? widgetType, PanelWidgetHostContext context)
        {
            return widgetType switch
            {
                PanelWidgetTypes.PriceTicker or PanelWidgetTypes.WalletActivity => true,
                PanelWidgetTypes.XTimeline => true,
                PanelWidgetTypes.Website => true,
                _ => false
            };
        }
    }
}

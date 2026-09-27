using TrenchHQ.Core.Panels;
using TrenchHQ.Core.Widgets;
using TrenchHQ.Infrastructure.Widgets;
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;

namespace TrenchHQ.Infrastructure.Panels
{
    internal static class DesktopSetupService
    {
        internal static async Task<DesktopSetup> LoadOrCreateAsync(SavedWidgetCatalogService catalog)
        {
            var folderPath = ApplicationData.Current.LocalFolder.Path;
            var settings = await DesktopSetupStore.LoadAsync(folderPath).ConfigureAwait(false);
            if (settings != null)
            {
                if (DesktopSetupRules.ReconcileWidgetReferences(settings, catalog.GetWidgets()))
                    await SaveAsync(settings).ConfigureAwait(false);
                return settings;
            }

            var defaultWidget = await EnsureFallbackWidgetAsync(catalog);
            var created = new DesktopSetup
            {
                Overlays = [CreateOverlay("Overlay 1", defaultWidget.Id)],
                DockedBars = [CreateDockedBar("Panel Top", defaultWidget.Id)]
            };
            await SaveAsync(created).ConfigureAwait(false);
            return created;
        }

        internal static async Task SaveAsync(DesktopSetup settings)
        {
            await DesktopSetupStore.SaveAsync(ApplicationData.Current.LocalFolder.Path, settings).ConfigureAwait(false);
        }

        internal static OverlayDefinition CreateOverlay(string name, string? widgetId)
        {
            return new OverlayDefinition
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                Enabled = !string.IsNullOrWhiteSpace(widgetId),
                WidgetIds = CreateWidgetReferences(widgetId)
            };
        }

        internal static OverlayDefinition Clone(OverlayDefinition source, bool createNewId = false)
        {
            return new OverlayDefinition
            {
                UseApplicationWindow = source.UseApplicationWindow,
                HeightDip = source.HeightDip,
                Id = createNewId ? Guid.NewGuid().ToString("N") : source.Id,
                Name = source.Name,
                Enabled = source.Enabled,
                WidgetIds = [.. (source.WidgetIds ?? [])],
                WidthDip = source.WidthDip,
                BackgroundColor = source.BackgroundColor,
                TextColor = source.TextColor,
                ContentSize = source.ContentSize,
                LuminosityOpacity = source.LuminosityOpacity,
                TintOpacity = source.TintOpacity,
                PriceFlashOnChange = source.PriceFlashOnChange,
                Shortcut = source.Shortcut,
                PositionX = source.PositionX,
                PositionY = source.PositionY,
                MonitorDeviceName = source.MonitorDeviceName
            };
        }

        internal static DockedBarDefinition CreateDockedBar(
            string name,
            string? widgetId,
            string monitorDeviceName = "",
            string edge = "Top")
        {
            var normalizedEdge = DockedBarLayoutRules.NormalizeEdge(edge);
            return new DockedBarDefinition
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                Enabled = !string.IsNullOrWhiteSpace(widgetId),
                WidgetIds = CreateWidgetReferences(widgetId),
                MonitorDeviceName = monitorDeviceName ?? string.Empty,
                Edge = normalizedEdge,
                ThicknessPx = DockedBarLayoutRules.GetDefaultThickness(normalizedEdge)
            };
        }

        internal static DockedBarDefinition Clone(DockedBarDefinition source, bool createNewId = false)
        {
            return new DockedBarDefinition
            {
                UseApplicationWindow = source.UseApplicationWindow,
                Id = createNewId ? Guid.NewGuid().ToString("N") : source.Id,
                Name = source.Name,
                Enabled = source.Enabled,
                WidgetIds = [.. (source.WidgetIds ?? [])],
                MonitorDeviceName = source.MonitorDeviceName,
                Edge = source.Edge,
                ThicknessPx = source.ThicknessPx,
                BackgroundColor = source.BackgroundColor,
                TextColor = source.TextColor,
                ContentSize = source.ContentSize,
                PriceFlashOnChange = source.PriceFlashOnChange,
                Shortcut = source.Shortcut
            };
        }

        private static async Task<SavedWidgetDefinition> EnsureFallbackWidgetAsync(SavedWidgetCatalogService catalog)
        {
            return catalog.GetWidgets().FirstOrDefault()
                   ?? await catalog.EnsurePriceTickerAsync(
                       SavedWidgetCatalogRules.CreateCentralizedInstruments(DefaultPriceTickerMarkets));
        }

        private static readonly string[] DefaultPriceTickerMarkets =
        [
            "BTC/USDT - Binance",
            "ETH/USDT - Binance",
            "SOL/USDT - Binance"
        ];

        private static string[] CreateWidgetReferences(string? widgetId)
        {
            return string.IsNullOrWhiteSpace(widgetId) ? [] : [widgetId];
        }
    }
}

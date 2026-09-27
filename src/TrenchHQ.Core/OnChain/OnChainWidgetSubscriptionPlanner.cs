using TrenchHQ.Core.Panels;
using TrenchHQ.Core.Widgets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TrenchHQ.Core.OnChain
{
    internal static class OnChainWidgetSubscriptionPlanner
    {
        internal static OnChainWatchedPoolSelection[] Build(
            IEnumerable<OverlayDefinition> overlays,
            IEnumerable<DockedBarDefinition> dockedBars,
            IEnumerable<SavedWidgetDefinition> savedWidgets) =>
            BuildWithModes(overlays, dockedBars, savedWidgets)
                .Select(static plan => plan.Selection)
                .ToArray();

        internal static OnChainPoolSubscription[] BuildWithModes(
            IEnumerable<OverlayDefinition> overlays,
            IEnumerable<DockedBarDefinition> dockedBars,
            IEnumerable<SavedWidgetDefinition> savedWidgets)
        {
            var referencedWidgetIds = overlays.SelectMany(static definition => definition.WidgetIds ?? [])
                .Concat(dockedBars.SelectMany(static definition => definition.WidgetIds ?? []))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unique = new Dictionary<string, OnChainPoolSubscription>(StringComparer.Ordinal);
            foreach (var widget in savedWidgets
                         .Where(widget => referencedWidgetIds.Contains(widget.Id)
                                          && widget.Type == PanelWidgetTypes.PriceTicker))
            foreach (var instrument in (widget.Instruments ?? [])
                         .Where(instrument => instrument.Kind == TickerInstrumentTypes.OnChainPool
                                              && !string.IsNullOrWhiteSpace(instrument.SelectedMint)
                                              && instrument.Pool?.SupportStatus == OnChainSupportStatus.Supported))
            {
                var descriptor = instrument.Pool!;
                var key = SavedWidgetCatalogRules.GetInstrumentKey(instrument);
                var mode = OnChainPriceModes.Normalize(widget.OnChainPriceMode);
                if (unique.TryGetValue(key, out var existing)
                    && existing.Mode == OnChainPriceModes.WebSocket)
                {
                    mode = OnChainPriceModes.WebSocket;
                }
                unique[key] = new OnChainPoolSubscription(new OnChainWatchedPoolSelection
                {
                    Descriptor = descriptor,
                    SelectedMint = instrument.SelectedMint!
                }, mode);
            }
            return [.. unique.Values];
        }
    }

    internal sealed record OnChainPoolSubscription(OnChainWatchedPoolSelection Selection, string Mode);
}

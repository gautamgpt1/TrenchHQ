using System;
using System.Collections.Generic;
using System.Linq;
using TrenchHQ.Models;

namespace TrenchHQ.Helpers
{
    internal readonly record struct WidgetSubscription(string VenueId, string Symbol);

    internal static class WidgetSubscriptionPlanner
    {
        internal static WidgetSubscription[] Build(
            IEnumerable<OverlayDefinition> overlays,
            IEnumerable<DockedBarDefinition> dockedBars,
            IEnumerable<SavedWidgetDefinition> savedWidgets)
        {
            var referencedWidgetIds = overlays.SelectMany(static definition => definition.WidgetIds ?? [])
                .Concat(dockedBars.SelectMany(static definition => definition.WidgetIds ?? []))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unique = new Dictionary<string, WidgetSubscription>(StringComparer.OrdinalIgnoreCase);
            foreach (var widget in savedWidgets.Where(widget =>
                         referencedWidgetIds.Contains(widget.Id)
                         && string.Equals(widget.Type, PanelWidgetTypes.PriceTicker, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var instrument in (widget.Instruments ?? []).Where(static instrument =>
                             instrument.Kind == TickerInstrumentTypes.CentralizedMarket))
                {
                    if (string.IsNullOrWhiteSpace(instrument.Symbol)
                        || string.IsNullOrWhiteSpace(instrument.VenueId))
                    {
                        continue;
                    }

                    unique[$"{instrument.VenueId}|{instrument.Symbol}"] = new WidgetSubscription(
                        instrument.VenueId,
                        instrument.Symbol);
                }
            }

            return [.. unique.Values];
        }
    }
}

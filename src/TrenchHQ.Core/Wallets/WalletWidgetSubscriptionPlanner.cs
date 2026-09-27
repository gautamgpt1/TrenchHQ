using TrenchHQ.Core.Panels;
using TrenchHQ.Core.Widgets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TrenchHQ.Core.Wallets
{
    internal static class WalletWidgetSubscriptionPlanner
    {
        internal static SavedTrackedWallet[] Build(
            IEnumerable<OverlayDefinition> overlays,
            IEnumerable<DockedBarDefinition> dockedBars,
            IEnumerable<SavedWidgetDefinition> savedWidgets)
        {
            var referencedWidgetIds = overlays.SelectMany(static definition => definition.WidgetIds ?? [])
                .Concat(dockedBars.SelectMany(static definition => definition.WidgetIds ?? []))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unique = new Dictionary<string, SavedTrackedWallet>(StringComparer.OrdinalIgnoreCase);
            foreach (var wallet in savedWidgets
                         .Where(widget => referencedWidgetIds.Contains(widget.Id)
                                          && string.Equals(
                                              widget.Type,
                                              PanelWidgetTypes.WalletActivity,
                                              StringComparison.OrdinalIgnoreCase))
                         .SelectMany(static widget => widget.Wallets ?? []))
            {
                var normalized = SavedWidgetCatalogRules.NormalizeWallet(new SavedTrackedWallet
                {
                    ChainNamespace = wallet.ChainNamespace,
                    ChainId = wallet.ChainId,
                    Address = wallet.Address,
                    Label = wallet.Label
                });
                if (normalized != null)
                {
                    unique[SavedWidgetCatalogRules.GetWalletKey(normalized)] = normalized;
                }
            }
            return [.. unique.Values];
        }
    }
}

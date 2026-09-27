using System;

namespace TrenchHQ.Core.Markets
{
    internal static class SelectedPairParser
    {
        internal static bool TryParse(
            string rawPair,
            out string symbol,
            out string exchangeId,
            out string display)
        {
            symbol = string.Empty;
            exchangeId = string.Empty;
            display = string.Empty;

            if (string.IsNullOrWhiteSpace(rawPair))
            {
                return false;
            }

            var trimmed = rawPair.Trim();
            var separatorIndex = trimmed.IndexOf(" - ", StringComparison.Ordinal);
            if (separatorIndex <= 0 || separatorIndex >= trimmed.Length - 3)
            {
                return false;
            }

            symbol = trimmed[..separatorIndex].Trim();
            var exchangeName = trimmed[(separatorIndex + 3)..].Trim();
            exchangeId = CertifiedExchangeCatalog.GetExchangeId(exchangeName) ?? string.Empty;
            display = symbol;
            return !string.IsNullOrWhiteSpace(symbol) && !string.IsNullOrWhiteSpace(exchangeId);
        }
    }
}

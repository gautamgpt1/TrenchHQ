using System;
using System.Globalization;
using System.Text.Json;
using TrenchHQ.Models;

namespace TrenchHQ.Helpers
{
    internal static class MarketFeedProtocolParser
    {
        internal static bool HasCurrentVersion(JsonElement root)
        {
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("protocolVersion", out var versionElement)
                   && versionElement.TryGetInt32(out var version)
                   && version == MarketFeedProtocol.CurrentVersion;
        }

        internal static bool TryParseMarket(JsonElement element, out FeedMarketIdentity? market)
        {
            market = null;
            if (element.ValueKind != JsonValueKind.Object
                || !TryGetRequiredText(element, "id", out var id)
                || !TryGetRequiredText(element, "symbol", out var symbol)
                || !TryGetRequiredText(element, "kind", out var kind)
                || !element.TryGetProperty("baseAsset", out var baseElement)
                || !TryParseAsset(baseElement, out var baseAsset)
                || !element.TryGetProperty("quoteAsset", out var quoteElement)
                || !TryParseAsset(quoteElement, out var quoteAsset)
                || !element.TryGetProperty("venue", out var venueElement)
                || !TryParseVenue(venueElement, out var venue))
            {
                return false;
            }

            market = new FeedMarketIdentity(id, symbol, kind, baseAsset!, quoteAsset!, venue!);
            return true;
        }

        internal static bool TryParsePriceUpdate(JsonElement root, out FeedPriceUpdate? update)
        {
            update = null;
            if (!HasCurrentVersion(root)
                || !root.TryGetProperty("market", out var marketElement)
                || !TryParseMarket(marketElement, out var market)
                || !root.TryGetProperty("source", out var sourceElement)
                || !TryParseSource(sourceElement, out var source)
                || !string.Equals(source!.VenueId, market!.Venue.Id, StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("price", out var priceElement)
                || !priceElement.TryGetProperty("value", out var valueElement)
                || !valueElement.TryGetDouble(out var value)
                || !double.IsFinite(value)
                || value <= 0
                || !root.TryGetProperty("timing", out var timingElement)
                || !TryGetTimestamp(timingElement, "receivedAt", required: true, out var receivedAt)
                || !TryGetTimestamp(timingElement, "sourceAt", required: false, out var sourceAt))
            {
                return false;
            }

            update = new FeedPriceUpdate(market, source, value, sourceAt, receivedAt!.Value);
            return true;
        }

        private static bool TryParseAsset(JsonElement element, out FeedAssetIdentity? asset)
        {
            asset = null;
            if (element.ValueKind != JsonValueKind.Object
                || !TryGetRequiredText(element, "id", out var id)
                || !TryGetRequiredText(element, "symbol", out var symbol)
                || !TryGetRequiredText(element, "kind", out var kind))
            {
                return false;
            }

            asset = new FeedAssetIdentity(
                id,
                symbol,
                kind,
                GetOptionalText(element, "chainId"),
                GetOptionalText(element, "address"));
            return true;
        }

        private static bool TryParseVenue(JsonElement element, out FeedVenueIdentity? venue)
        {
            venue = null;
            if (element.ValueKind != JsonValueKind.Object
                || !TryGetRequiredText(element, "id", out var id)
                || !TryGetRequiredText(element, "kind", out var kind))
            {
                return false;
            }

            venue = new FeedVenueIdentity(id, kind);
            return true;
        }

        private static bool TryParseSource(JsonElement element, out FeedSourceIdentity? source)
        {
            source = null;
            if (element.ValueKind != JsonValueKind.Object
                || !TryGetRequiredText(element, "id", out var id)
                || !TryGetRequiredText(element, "providerId", out var providerId)
                || !TryGetRequiredText(element, "venueId", out var venueId)
                || !TryGetRequiredText(element, "transport", out var transport))
            {
                return false;
            }

            source = new FeedSourceIdentity(id, providerId, venueId, transport);
            return true;
        }

        private static bool TryGetTimestamp(
            JsonElement element,
            string propertyName,
            bool required,
            out DateTimeOffset? value)
        {
            value = null;
            if (!element.TryGetProperty(propertyName, out var timestampElement)
                || timestampElement.ValueKind == JsonValueKind.Null)
            {
                return !required;
            }

            if (timestampElement.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(
                    timestampElement.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var timestamp))
            {
                return false;
            }

            value = timestamp;
            return true;
        }

        private static bool TryGetRequiredText(JsonElement element, string propertyName, out string value)
        {
            value = GetOptionalText(element, propertyName) ?? string.Empty;
            return value.Length > 0;
        }

        private static string? GetOptionalText(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var valueElement)
                || valueElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var value = valueElement.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }
}

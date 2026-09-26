using System;

namespace TrenchHQ.Models
{
    internal static class MarketFeedProtocol
    {
        internal const int CurrentVersion = 1;
        internal const string CentralizedVenueKind = "centralizedExchange";
        internal const string VenueAssetKind = "venueAsset";
        internal const string SpotMarketKind = "spot";
    }

    internal sealed record FeedAssetIdentity(
        string Id,
        string Symbol,
        string Kind,
        string? ChainId,
        string? Address);

    internal sealed record FeedVenueIdentity(
        string Id,
        string Kind);

    internal sealed record FeedMarketIdentity(
        string Id,
        string Symbol,
        string Kind,
        FeedAssetIdentity BaseAsset,
        FeedAssetIdentity QuoteAsset,
        FeedVenueIdentity Venue);

    internal sealed record FeedSourceIdentity(
        string Id,
        string ProviderId,
        string VenueId,
        string Transport);

    internal sealed record FeedPriceUpdate(
        FeedMarketIdentity Market,
        FeedSourceIdentity Source,
        double Value,
        DateTimeOffset? SourceTimestampUtc,
        DateTimeOffset ReceivedAtUtc);
}

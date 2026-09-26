using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using TrenchHQ.Models;
using Windows.Storage;

namespace TrenchHQ.Helpers
{
    internal sealed class MarketCacheEntry
    {
        public int Version { get; set; } = MarketFeedProtocol.CurrentVersion;
        public string VenueId { get; set; } = string.Empty;
        public DateTimeOffset FetchedAtUtc { get; set; } = DateTimeOffset.UtcNow;
        public FeedMarketIdentity[] Markets { get; set; } = [];
    }

    internal static partial class MarketCacheService
    {
        private static readonly ConcurrentDictionary<string, string> LastErrors = new(StringComparer.OrdinalIgnoreCase);
        private const string CachePrefix = "normalized_markets_v1_";
        private static readonly TimeSpan DefaultCacheDuration = TimeSpan.FromHours(24);
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        public static CertifiedExchange[] GetCertifiedExchanges() => CertifiedExchangeCatalog.GetAll();

        public static string GetDisplayName(string exchangeId)
        {
            return CertifiedExchangeCatalog.GetDisplayName(exchangeId);
        }

        public static string? GetExchangeId(string exchangeNameOrId)
        {
            return CertifiedExchangeCatalog.GetExchangeId(exchangeNameOrId);
        }

        public static async Task<MarketCacheEntry?> EnsureExchangeCacheAsync(
            string exchangeId,
            bool forceRefresh = false,
            TimeSpan? maxAge = null)
        {
            var cached = await ReadCacheAsync(exchangeId).ConfigureAwait(false);
            var ttl = maxAge ?? DefaultCacheDuration;

            if (!forceRefresh && cached != null && DateTimeOffset.UtcNow - cached.FetchedAtUtc <= ttl)
            {
                return cached;
            }

            var fresh = await FetchExchangeMarketsAsync(exchangeId).ConfigureAwait(false);
            if (fresh != null)
            {
                await WriteCacheAsync(fresh).ConfigureAwait(false);
                LastErrors.TryRemove(exchangeId, out _);
            }

            return fresh ?? cached;
        }

        public static string[] GetLastErrorExchangeIds()
        {
            if (LastErrors.IsEmpty)
            {
                return [];
            }

            return [.. LastErrors.Keys];
        }

        public static async Task<FeedMarketIdentity[]> GetCachedMarketsAsync(string venueId)
        {
            var cached = await ReadCacheAsync(venueId).ConfigureAwait(false);
            return cached?.Markets ?? [];
        }

        private static async Task<MarketCacheEntry?> FetchExchangeMarketsAsync(string exchangeId)
        {
            try
            {
                var markets = await MarketSidecarClient.Current.GetMarketsAsync(exchangeId).ConfigureAwait(false);
                return new MarketCacheEntry
                {
                    VenueId = exchangeId,
                    FetchedAtUtc = DateTimeOffset.UtcNow,
                    Markets = markets
                };
            }
            catch (Exception ex)
            {
                LastErrors[exchangeId] = ex.Message;
                return null;
            }
        }

        private static async Task<MarketCacheEntry?> ReadCacheAsync(string exchangeId)
        {
            var folder = ApplicationData.Current.LocalCacheFolder;
            var fileName = $"{CachePrefix}{exchangeId}.json";

            try
            {
                var file = await folder.GetFileAsync(fileName).AsTask().ConfigureAwait(false);
                var json = await FileIO.ReadTextAsync(file).AsTask().ConfigureAwait(false);
                var entry = JsonSerializer.Deserialize<MarketCacheEntry>(json);
                return entry != null
                       && entry.Version == MarketFeedProtocol.CurrentVersion
                       && string.Equals(entry.VenueId, exchangeId, StringComparison.OrdinalIgnoreCase)
                       && entry.Markets != null
                    ? entry
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static async Task WriteCacheAsync(MarketCacheEntry entry)
        {
            var folder = ApplicationData.Current.LocalCacheFolder;
            var fileName = $"{CachePrefix}{entry.VenueId}.json";

            var file = await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting)
                .AsTask().ConfigureAwait(false);
            var json = JsonSerializer.Serialize(entry, JsonOptions);
            await FileIO.WriteTextAsync(file, json).AsTask().ConfigureAwait(false);
        }
    }
}

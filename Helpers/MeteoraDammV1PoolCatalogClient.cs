using TrenchHQ.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrenchHQ.Helpers
{
    internal sealed class MeteoraDammV1PoolCatalogClient
    {
        private const int MaximumResponseBytes = 2 * 1024 * 1024;
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
        private static readonly HttpClient SharedHttpClient = new()
        {
            BaseAddress = new Uri("https://damm-api.meteora.ag/"),
            Timeout = TimeSpan.FromSeconds(15)
        };
        private readonly HttpClient _httpClient;
        private readonly BoundedAsyncCache<string, PoolCatalogSearchResult> _cache =
            new(128, StringComparer.Ordinal);

        internal static MeteoraDammV1PoolCatalogClient Current { get; } = new(SharedHttpClient);

        internal MeteoraDammV1PoolCatalogClient(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        internal async Task<PoolCatalogSearchResult> SearchAsync(
            string mint,
            CancellationToken cancellationToken = default)
        {
            mint = RequireMint(mint);
            return await _cache.GetOrCreateAsync(
                    mint,
                    CacheLifetime,
                    () => FetchAsync(mint),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<PoolCatalogSearchResult> FetchAsync(string mint)
        {
            var requestUri = $"pools/search?page=0&size=100&include_token_mints={Uri.EscapeDataString(mint)}";
            using var response = await _httpClient.GetAsync(
                    requestUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Meteora DAMM v1 pool catalog returned HTTP {(int)response.StatusCode}.");
            }
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            {
                throw new InvalidOperationException("Meteora DAMM v1 pool catalog response was too large.");
            }
            var payload = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (payload.Length > MaximumResponseBytes)
            {
                throw new InvalidOperationException("Meteora DAMM v1 pool catalog response was too large.");
            }
            return Parse(payload, mint);
        }

        internal static PoolCatalogSearchResult Parse(ReadOnlyMemory<byte> payload, string mint)
        {
            mint = RequireMint(mint);
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("data", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("Meteora DAMM v1 pool catalog returned an invalid response.");
            }

            var pools = new List<PoolCatalogEntry>();
            foreach (var item in items.EnumerateArray())
            {
                var address = ReadString(item, "pool_address");
                var mints = ReadStringArray(item, "pool_token_mints");
                if (address.Length == 0 || mints.Length != 2 || !mints.Contains(mint, StringComparer.Ordinal))
                {
                    continue;
                }
                var symbols = ReadString(item, "pool_name")
                    .Split('-', 2, StringSplitOptions.TrimEntries);
                pools.Add(new PoolCatalogEntry
                {
                    SourceId = "meteora-damm-v1",
                    ChainId = "solana",
                    ProtocolId = OnChainProtocolIds.MeteoraDammV1,
                    PoolAddress = address,
                    Labels = [ReadString(item, "pool_type")],
                    BaseAsset = new PoolCatalogAsset
                    {
                        Address = mints[0],
                        Symbol = symbols.ElementAtOrDefault(0) ?? string.Empty
                    },
                    QuoteAsset = new PoolCatalogAsset
                    {
                        Address = mints[1],
                        Symbol = symbols.ElementAtOrDefault(1) ?? string.Empty
                    },
                    LiquidityUsd = ReadNonNegativeDouble(item, "pool_tvl"),
                    Volume24hUsd = ReadNonNegativeDouble(item, "trading_volume"),
                    CreatedAtUnixMs = ReadCreatedAtUnixMs(item)
                });
            }

            var sorted = pools
                .GroupBy(static pool => pool.PoolAddress, StringComparer.Ordinal)
                .Select(static group => group.First())
                .OrderByDescending(static pool => pool.LiquidityUsd ?? double.MinValue)
                .ThenByDescending(static pool => pool.Volume24hUsd ?? double.MinValue)
                .Take(30)
                .ToArray();
            var selectedAsset = sorted
                .Select(pool => string.Equals(pool.BaseAsset.Address, mint, StringComparison.Ordinal)
                    ? pool.BaseAsset
                    : pool.QuoteAsset)
                .FirstOrDefault();
            return new PoolCatalogSearchResult
            {
                SourceId = "meteora-damm-v1",
                ChainId = "solana",
                AssetAddress = mint,
                AssetSymbol = selectedAsset?.Symbol ?? string.Empty,
                Pools = sorted
            };
        }

        private static string[] ReadStringArray(JsonElement parent, string propertyName)
        {
            if (!parent.TryGetProperty(propertyName, out var values)
                || values.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            return values.EnumerateArray()
                .Where(static value => value.ValueKind == JsonValueKind.String)
                .Select(static value => value.GetString()?.Trim() ?? string.Empty)
                .ToArray();
        }

        private static string ReadString(JsonElement parent, string propertyName)
        {
            if (!parent.TryGetProperty(propertyName, out var value))
            {
                return string.Empty;
            }
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString()?.Trim() ?? string.Empty,
                JsonValueKind.Number => value.GetRawText(),
                _ => string.Empty
            };
        }

        private static double? ReadNonNegativeDouble(JsonElement parent, string propertyName)
        {
            var text = ReadString(parent, propertyName);
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                   && double.IsFinite(number)
                   && number >= 0
                ? number
                : null;
        }

        private static long? ReadCreatedAtUnixMs(JsonElement item)
        {
            return item.TryGetProperty("created_at", out var value)
                   && value.ValueKind == JsonValueKind.Number
                   && value.TryGetInt64(out var seconds)
                   && seconds > 0
                ? checked(seconds * 1000)
                : null;
        }

        private static string RequireMint(string mint)
        {
            if (string.IsNullOrWhiteSpace(mint))
            {
                throw new ArgumentException("A mint is required.", nameof(mint));
            }
            return mint.Trim();
        }

    }
}

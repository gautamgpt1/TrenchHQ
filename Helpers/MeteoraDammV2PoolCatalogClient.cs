using TrenchHQ.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrenchHQ.Helpers
{
    internal sealed class MeteoraDammV2PoolCatalogClient
    {
        private const int MaximumResponseBytes = 2 * 1024 * 1024;
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
        private static readonly HttpClient SharedHttpClient = new()
        {
            BaseAddress = new Uri("https://damm-v2.datapi.meteora.ag/"),
            Timeout = TimeSpan.FromSeconds(15)
        };
        private readonly HttpClient _httpClient;
        private readonly BoundedAsyncCache<string, PoolCatalogSearchResult> _cache =
            new(128, StringComparer.Ordinal);

        internal static MeteoraDammV2PoolCatalogClient Current { get; } = new(SharedHttpClient);

        internal MeteoraDammV2PoolCatalogClient(HttpClient httpClient)
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
            var requestUri = $"pools?page=1&page_size=100&query={Uri.EscapeDataString(mint)}&sort_by=volume_24h%3Adesc&filter_by=is_blacklisted%3Dfalse";
            using var response = await _httpClient.GetAsync(
                    requestUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Meteora DAMM v2 pool catalog returned HTTP {(int)response.StatusCode}.");
            }
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            {
                throw new InvalidOperationException("Meteora DAMM v2 pool catalog response was too large.");
            }

            var payload = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (payload.Length > MaximumResponseBytes)
            {
                throw new InvalidOperationException("Meteora DAMM v2 pool catalog response was too large.");
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
                throw new InvalidOperationException("Meteora DAMM v2 pool catalog returned an invalid response.");
            }

            var pools = new List<PoolCatalogEntry>();
            foreach (var item in items.EnumerateArray())
            {
                var address = ReadString(item, "address");
                var tokenX = ReadAsset(item, "token_x");
                var tokenY = ReadAsset(item, "token_y");
                if (address.Length == 0
                    || (!string.Equals(tokenX.Address, mint, StringComparison.Ordinal)
                        && !string.Equals(tokenY.Address, mint, StringComparison.Ordinal)))
                {
                    continue;
                }

                var labels = ReadLabels(item);
                pools.Add(new PoolCatalogEntry
                {
                    SourceId = "meteora-damm-v2",
                    ChainId = "solana",
                    ProtocolId = OnChainProtocolIds.MeteoraDammV2,
                    PoolAddress = address,
                    Labels = labels,
                    BaseAsset = tokenX,
                    QuoteAsset = tokenY,
                    LiquidityUsd = ReadNonNegativeDouble(item, "tvl"),
                    Volume24hUsd = ReadNestedNonNegativeDouble(item, "volume", "24h"),
                    CreatedAtUnixMs = ReadPositiveInt64(item, "created_at")
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
                SourceId = "meteora-damm-v2",
                ChainId = "solana",
                AssetAddress = mint,
                AssetName = selectedAsset?.Name ?? string.Empty,
                AssetSymbol = selectedAsset?.Symbol ?? string.Empty,
                Pools = sorted
            };
        }

        private static PoolCatalogAsset ReadAsset(JsonElement parent, string propertyName)
        {
            if (!parent.TryGetProperty(propertyName, out var value)
                || value.ValueKind != JsonValueKind.Object)
            {
                return new PoolCatalogAsset();
            }
            return new PoolCatalogAsset
            {
                Address = ReadString(value, "address"),
                Name = ReadString(value, "name"),
                Symbol = ReadString(value, "symbol")
            };
        }

        private static string[] ReadLabels(JsonElement item)
        {
            if (!item.TryGetProperty("pool_config", out var config)
                || config.ValueKind != JsonValueKind.Object
                || !config.TryGetProperty("concentrated_liquidity", out var concentrated)
                || concentrated.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return [];
            }
            return [concentrated.GetBoolean() ? "Concentrated" : "Compounding"];
        }

        private static string ReadString(JsonElement parent, string propertyName)
        {
            return parent.TryGetProperty(propertyName, out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim() ?? string.Empty
                : string.Empty;
        }

        private static double? ReadNonNegativeDouble(JsonElement parent, string propertyName)
        {
            if (!parent.TryGetProperty(propertyName, out var value)
                || value.ValueKind != JsonValueKind.Number
                || !value.TryGetDouble(out var number)
                || !double.IsFinite(number)
                || number < 0)
            {
                return null;
            }
            return number;
        }

        private static double? ReadNestedNonNegativeDouble(
            JsonElement parent,
            string objectName,
            string propertyName)
        {
            return parent.TryGetProperty(objectName, out var nested)
                   && nested.ValueKind == JsonValueKind.Object
                ? ReadNonNegativeDouble(nested, propertyName)
                : null;
        }

        private static long? ReadPositiveInt64(JsonElement parent, string propertyName)
        {
            return parent.TryGetProperty(propertyName, out var value)
                   && value.ValueKind == JsonValueKind.Number
                   && value.TryGetInt64(out var number)
                   && number > 0
                ? number
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

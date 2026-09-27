using TrenchHQ.Core.OnChain;
using TrenchHQ.Infrastructure.OnChain;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrenchHQ.Infrastructure.OnChain.Solana
{
    internal sealed class ManifestPoolCatalogClient
    {
        private const int MaximumResponseBytes = 2 * 1024 * 1024;
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
        private static readonly HttpClient SharedHttpClient = new()
        {
            BaseAddress = new Uri("https://mfx-stats-mainnet.fly.dev/"),
            Timeout = TimeSpan.FromSeconds(15)
        };
        private readonly HttpClient _httpClient;
        private readonly BoundedAsyncCache<string, byte[]> _catalog =
            new(1, StringComparer.Ordinal);

        internal static ManifestPoolCatalogClient Current { get; } = new(SharedHttpClient);

        internal ManifestPoolCatalogClient(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        internal async Task<PoolCatalogSearchResult> SearchAsync(
            string mint,
            CancellationToken cancellationToken = default)
        {
            mint = RequireMint(mint);
            var payload = await _catalog.GetOrCreateAsync(
                    "tickers",
                    CacheLifetime,
                    FetchCatalogAsync,
                    cancellationToken)
                .ConfigureAwait(false);
            return Parse(payload, mint);
        }

        private async Task<byte[]> FetchCatalogAsync()
        {
            using var response = await _httpClient.GetAsync(
                    "tickers",
                    HttpCompletionOption.ResponseHeadersRead,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Manifest market catalog returned HTTP {(int)response.StatusCode}.");
            }
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            {
                throw new InvalidOperationException("Manifest market catalog response was too large.");
            }
            var payload = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (payload.Length > MaximumResponseBytes)
            {
                throw new InvalidOperationException("Manifest market catalog response was too large.");
            }
            return payload;
        }

        internal static PoolCatalogSearchResult Parse(ReadOnlyMemory<byte> payload, string mint)
        {
            mint = RequireMint(mint);
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("Manifest market catalog returned an invalid response.");
            }

            var pools = new List<PoolCatalogEntry>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var address = ReadString(item, "ticker_id");
                var poolId = ReadString(item, "pool_id");
                var baseMint = ReadString(item, "base_currency");
                var quoteMint = ReadString(item, "target_currency");
                if (address.Length == 0
                    || poolId != address
                    || baseMint.Length == 0
                    || quoteMint.Length == 0
                    || (!string.Equals(baseMint, mint, StringComparison.Ordinal)
                        && !string.Equals(quoteMint, mint, StringComparison.Ordinal)))
                {
                    continue;
                }
                pools.Add(new PoolCatalogEntry
                {
                    SourceId = "manifest",
                    ChainId = "solana",
                    ProtocolId = OnChainProtocolIds.ManifestOrderbook,
                    PoolAddress = address,
                    Labels = ["Order book"],
                    BaseAsset = new PoolCatalogAsset { Address = baseMint },
                    QuoteAsset = new PoolCatalogAsset { Address = quoteMint },
                    LiquidityUsd = ReadPositiveDouble(item, "liquidity_in_usd")
                });
            }

            return new PoolCatalogSearchResult
            {
                SourceId = "manifest",
                ChainId = "solana",
                AssetAddress = mint,
                Pools = pools
                    .GroupBy(static pool => pool.PoolAddress, StringComparer.Ordinal)
                    .Select(static group => group.First())
                    .OrderByDescending(static pool => pool.LiquidityUsd ?? double.MinValue)
                    .ThenBy(static pool => pool.PoolAddress, StringComparer.Ordinal)
                    .Take(30)
                    .ToArray()
            };
        }

        private static string ReadString(JsonElement item, string propertyName)
        {
            return item.TryGetProperty(propertyName, out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim() ?? string.Empty
                : string.Empty;
        }

        private static double? ReadPositiveDouble(JsonElement item, string propertyName)
        {
            if (!item.TryGetProperty(propertyName, out var value))
            {
                return null;
            }
            var text = value.ValueKind switch
            {
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.String => value.GetString(),
                _ => null
            };
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                   && double.IsFinite(number)
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

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
    internal sealed class CurvePoolCatalogClient
    {
        private const int MaximumResponseBytes = 8 * 1024 * 1024;
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
        private static readonly HttpClient SharedHttpClient = new()
        {
            BaseAddress = new Uri("https://api.curve.finance/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        private readonly HttpClient _httpClient;
        private readonly BoundedAsyncCache<string, byte[]> _catalog =
            new(1, StringComparer.Ordinal);

        internal static CurvePoolCatalogClient Current { get; } = new(SharedHttpClient);

        internal CurvePoolCatalogClient(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        internal async Task<PoolCatalogSearchResult> SearchAsync(
            string assetAddress,
            CancellationToken cancellationToken = default)
        {
            if (!EvmAddress.TryNormalize(assetAddress, out var normalizedAsset))
            {
                throw new ArgumentException("Enter a valid Ethereum token contract address.", nameof(assetAddress));
            }
            var payload = await _catalog.GetOrCreateAsync(
                    "ethereum",
                    CacheLifetime,
                    FetchCatalogAsync,
                    cancellationToken)
                .ConfigureAwait(false);
            return Parse(payload, normalizedAsset, EthereumDeploymentRegistry.MainnetQuoteAssets);
        }

        private async Task<byte[]> FetchCatalogAsync()
        {
            using var response = await _httpClient.GetAsync(
                    "v1/getPools/all/ethereum",
                    HttpCompletionOption.ResponseHeadersRead,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Curve pool catalog returned HTTP {(int)response.StatusCode}.");
            }
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            {
                throw new InvalidOperationException("Curve pool catalog response was too large.");
            }
            var payload = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (payload.Length > MaximumResponseBytes)
            {
                throw new InvalidOperationException("Curve pool catalog response was too large.");
            }
            return payload;
        }

        internal static PoolCatalogSearchResult Parse(
            ReadOnlyMemory<byte> payload,
            string assetAddress,
            IReadOnlyList<EvmQuoteAsset>? preferredQuotes = null)
        {
            if (!EvmAddress.TryNormalize(assetAddress, out var normalizedAsset))
            {
                throw new ArgumentException("Enter a valid Ethereum token contract address.", nameof(assetAddress));
            }
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("success", out var success)
                || success.ValueKind != JsonValueKind.True
                || !document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("poolData", out var poolData)
                || poolData.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("Curve pool catalog returned an invalid response.");
            }

            var pools = new List<PoolCatalogEntry>();
            foreach (var pool in poolData.EnumerateArray())
            {
                if (pool.ValueKind != JsonValueKind.Object
                    || ReadBoolean(pool, "isBroken")
                    || !EvmAddress.TryNormalize(ReadString(pool, "address"), out var poolAddress)
                    || !pool.TryGetProperty("coinsAddresses", out var coinAddresses)
                    || coinAddresses.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var coins = new List<(uint Index, string Address)>();
                uint index = 0;
                foreach (var coin in coinAddresses.EnumerateArray())
                {
                    if (coin.ValueKind == JsonValueKind.String
                        && TryNormalizeCurveCoin(coin.GetString(), out var coinAddress))
                    {
                        coins.Add((index, coinAddress));
                    }
                    index++;
                }
                var selected = coins.FirstOrDefault(coin =>
                    coin.Address.Equals(normalizedAsset, StringComparison.OrdinalIgnoreCase));
                if (selected.Address == null)
                {
                    continue;
                }
                var counterpart = coins
                    .Where(coin => coin.Index != selected.Index)
                    .OrderBy(coin => PreferredQuoteRank(coin.Address, preferredQuotes))
                    .ThenBy(static coin => coin.Index)
                    .FirstOrDefault();
                if (counterpart.Address == null)
                {
                    continue;
                }

                pools.Add(new PoolCatalogEntry
                {
                    SourceId = "curve-api",
                    ChainId = "ethereum",
                    ProtocolId = OnChainProtocolIds.Curve,
                    PoolAddress = poolAddress,
                    Labels = BuildLabels(pool),
                    BaseAsset = ReadAsset(pool, selected.Index, normalizedAsset),
                    QuoteAsset = ReadAsset(pool, counterpart.Index, counterpart.Address),
                    LiquidityUsd = ReadNonNegativeDouble(pool, "usdTotal"),
                    CreatedAtUnixMs = ReadUnixMilliseconds(pool, "creationTs"),
                    BaseTokenIndex = selected.Index,
                    QuoteTokenIndex = counterpart.Index
                });
            }

            var ordered = pools
                .GroupBy(static pool => pool.PoolAddress, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .OrderByDescending(static pool => pool.LiquidityUsd ?? double.MinValue)
                .ThenBy(static pool => pool.PoolAddress, StringComparer.Ordinal)
                .Take(30)
                .ToArray();
            var selectedMetadata = ordered.FirstOrDefault()?.BaseAsset;
            return new PoolCatalogSearchResult
            {
                SourceId = "curve-api",
                ChainId = "ethereum",
                AssetAddress = normalizedAsset,
                AssetName = selectedMetadata?.Name ?? string.Empty,
                AssetSymbol = selectedMetadata?.Symbol ?? string.Empty,
                Pools = ordered
            };
        }

        private static PoolCatalogAsset ReadAsset(JsonElement pool, uint index, string address)
        {
            var asset = new PoolCatalogAsset { Address = address };
            if (!pool.TryGetProperty("coins", out var coins)
                || coins.ValueKind != JsonValueKind.Array
                || index >= coins.GetArrayLength())
            {
                return asset;
            }
            var coin = coins[(int)index];
            if (coin.ValueKind == JsonValueKind.Object)
            {
                asset.Name = ReadString(coin, "name");
                asset.Symbol = ReadString(coin, "symbol");
            }
            return asset;
        }

        private static string[] BuildLabels(JsonElement pool)
        {
            var labels = new List<string> { "Event-priced" };
            var registry = ReadString(pool, "registryId");
            var assetType = ReadString(pool, "assetTypeName");
            if (registry.Length > 0)
            {
                labels.Add(registry);
            }
            if (assetType.Length > 0)
            {
                labels.Add(assetType);
            }
            return labels.ToArray();
        }

        private static int PreferredQuoteRank(
            string address,
            IReadOnlyList<EvmQuoteAsset>? preferredQuotes)
        {
            if (preferredQuotes == null)
            {
                return int.MaxValue;
            }
            for (var index = 0; index < preferredQuotes.Count; index++)
            {
                if (preferredQuotes[index].Address.Equals(address, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }
            return int.MaxValue;
        }

        private static bool TryNormalizeCurveCoin(string? address, out string normalized)
        {
            normalized = string.Empty;
            if (string.Equals(address, EthereumDeploymentRegistry.CurveNativeEther, StringComparison.OrdinalIgnoreCase))
            {
                normalized = EthereumDeploymentRegistry.NativeEther;
                return true;
            }
            return EvmAddress.TryNormalize(address, out normalized)
                   && !normalized.Equals(EthereumDeploymentRegistry.NativeEther, StringComparison.Ordinal);
        }

        private static string ReadString(JsonElement parent, string propertyName)
        {
            return parent.TryGetProperty(propertyName, out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim() ?? string.Empty
                : string.Empty;
        }

        private static bool ReadBoolean(JsonElement parent, string propertyName)
        {
            return parent.TryGetProperty(propertyName, out var value)
                   && value.ValueKind == JsonValueKind.True;
        }

        private static double? ReadNonNegativeDouble(JsonElement parent, string propertyName)
        {
            if (!parent.TryGetProperty(propertyName, out var value))
            {
                return null;
            }
            var text = value.ValueKind switch
            {
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.String => value.GetString(),
                _ => null
            };
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                   && double.IsFinite(parsed)
                   && parsed >= 0
                ? parsed
                : null;
        }

        private static long? ReadUnixMilliseconds(JsonElement parent, string propertyName)
        {
            if (!parent.TryGetProperty(propertyName, out var value))
            {
                return null;
            }
            var parsed = value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt64(out var number) => number,
                JsonValueKind.String when long.TryParse(
                    value.GetString(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var number) => number,
                _ => 0
            };
            return parsed > 0 && parsed <= long.MaxValue / 1000 ? parsed * 1000 : null;
        }

    }
}

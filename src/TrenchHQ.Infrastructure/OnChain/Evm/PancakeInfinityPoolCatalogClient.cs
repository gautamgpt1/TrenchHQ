using TrenchHQ.Core.OnChain;
using TrenchHQ.Core.OnChain.Evm;
using TrenchHQ.Infrastructure.OnChain;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrenchHQ.Infrastructure.OnChain.Evm
{
    internal sealed class PancakeInfinityPoolCatalogClient
    {
        private const int MaximumResponseBytes = 2 * 1024 * 1024;
        private const int MaximumPagesPerProtocol = 4;
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
        private static readonly HttpClient SharedHttpClient = new()
        {
            BaseAddress = new Uri("https://explorer.pancakeswap.com/"),
            Timeout = TimeSpan.FromSeconds(15)
        };
        private readonly HttpClient _httpClient;
        private readonly BoundedAsyncCache<string, PoolCatalogSearchResult> _cache =
            new(128, StringComparer.OrdinalIgnoreCase);

        internal static PancakeInfinityPoolCatalogClient Current { get; } = new(SharedHttpClient);

        internal PancakeInfinityPoolCatalogClient(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        internal async Task<PoolCatalogSearchResult> SearchAsync(
            string assetAddress,
            CancellationToken cancellationToken = default)
        {
            return await SearchAsync(
                    BnbDeploymentRegistry.Catalog,
                    assetAddress,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        internal async Task<PoolCatalogSearchResult> SearchAsync(
            EvmDeploymentCatalog deployments,
            string assetAddress,
            CancellationToken cancellationToken = default)
        {
            if (deployments.ChainId is not (EvmChainDefinitions.BnbMainnetChainId
                or EvmChainDefinitions.BaseMainnetChainId))
            {
                throw new ArgumentException("PancakeSwap Infinity is not enabled for this chain.", nameof(deployments));
            }
            if (!EvmAddress.TryNormalize(assetAddress, out var normalizedAsset))
            {
                throw new ArgumentException(
                    $"Enter a valid {deployments.DisplayName} token contract address.",
                    nameof(assetAddress));
            }
            var cacheKey = $"{deployments.ChainId}:{normalizedAsset}";
            return await _cache.GetOrCreateAsync(
                    cacheKey,
                    CacheLifetime,
                    () => FetchResultAsync(deployments, normalizedAsset),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<PoolCatalogSearchResult> FetchResultAsync(
            EvmDeploymentCatalog deployments,
            string normalizedAsset)
        {
            var clTask = FetchAsync(deployments, "infinityCl", normalizedAsset);
            var binTask = FetchAsync(deployments, "infinityBin", normalizedAsset);
            await Task.WhenAll(clTask, binTask).ConfigureAwait(false);
            var pools = (await clTask.ConfigureAwait(false))
                .Concat(await binTask.ConfigureAwait(false))
                .GroupBy(static pool => pool.PoolAddress, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .OrderByDescending(static pool => pool.LiquidityUsd ?? double.MinValue)
                .ThenByDescending(static pool => pool.Volume24hUsd ?? double.MinValue)
                .Take(100)
                .ToArray();
            var metadataPool = pools.FirstOrDefault();
            var asset = metadataPool == null
                ? new PoolCatalogAsset { Address = normalizedAsset }
                : metadataPool.BaseAsset.Address.Equals(normalizedAsset, StringComparison.OrdinalIgnoreCase)
                    ? metadataPool.BaseAsset
                    : metadataPool.QuoteAsset;
            return new PoolCatalogSearchResult
            {
                SourceId = "pancake-explorer",
                ChainId = deployments.CatalogChainId,
                AssetAddress = normalizedAsset,
                AssetName = asset.Name,
                AssetSymbol = asset.Symbol,
                Pools = pools
            };
        }

        private async Task<PoolCatalogEntry[]> FetchAsync(
            EvmDeploymentCatalog deployments,
            string protocol,
            string assetAddress)
        {
            var pools = new List<PoolCatalogEntry>();
            string? after = null;
            for (var pageNumber = 0; pageNumber < MaximumPagesPerProtocol; pageNumber++)
            {
                var requestUri =
                    $"api/cached/pools/list?orderBy=volumeUSD24h&protocols={protocol}"
                    + $"&chains={Uri.EscapeDataString(deployments.CatalogChainId)}&limit=100"
                    + (after == null ? string.Empty : $"&after={Uri.EscapeDataString(after)}");
                using var response = await _httpClient.GetAsync(
                        requestUri,
                        HttpCompletionOption.ResponseHeadersRead,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        $"PancakeSwap Explorer pool catalog returned HTTP {(int)response.StatusCode}.");
                }
                if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                {
                    throw new InvalidOperationException("PancakeSwap Explorer pool catalog response was too large.");
                }
                var payload = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (payload.Length > MaximumResponseBytes)
                {
                    throw new InvalidOperationException("PancakeSwap Explorer pool catalog response was too large.");
                }
                var page = ParsePage(payload, protocol, assetAddress, deployments);
                pools.AddRange(page.Pools);
                if (page.Pools.Length > 0
                    || !page.HasNextPage
                    || string.IsNullOrWhiteSpace(page.EndCursor)
                    || string.Equals(after, page.EndCursor, StringComparison.Ordinal))
                {
                    break;
                }
                after = page.EndCursor;
            }
            return pools.ToArray();
        }

        internal static PoolCatalogEntry[] Parse(
            ReadOnlyMemory<byte> payload,
            string protocol,
            string assetAddress)
        {
            return Parse(payload, protocol, assetAddress, BnbDeploymentRegistry.Catalog);
        }

        internal static PoolCatalogEntry[] Parse(
            ReadOnlyMemory<byte> payload,
            string protocol,
            string assetAddress,
            EvmDeploymentCatalog deployments)
        {
            return ParsePage(payload, protocol, assetAddress, deployments).Pools;
        }

        private static CatalogPage ParsePage(
            ReadOnlyMemory<byte> payload,
            string protocol,
            string assetAddress,
            EvmDeploymentCatalog deployments)
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("rows", out var rows)
                || rows.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("PancakeSwap Explorer pool catalog returned an invalid response.");
            }
            var expectedProtocol = protocol switch
            {
                "infinityCl" => OnChainProtocolIds.PancakeInfinityCl,
                "infinityBin" => OnChainProtocolIds.PancakeInfinityBin,
                _ => throw new ArgumentException("The PancakeSwap Infinity protocol is unsupported.", nameof(protocol))
            };
            var pools = new List<PoolCatalogEntry>();
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object
                    || !row.TryGetProperty("chainId", out var chainId)
                    || !chainId.TryGetInt32(out var numericChainId)
                    || numericChainId != int.Parse(deployments.ChainId, CultureInfo.InvariantCulture)
                    || !string.Equals(ReadString(row, "protocol"), protocol, StringComparison.Ordinal)
                    || !EvmAddress.IsHash(ReadString(row, "id")))
                {
                    continue;
                }
                var token0 = ReadAsset(row, "token0");
                var token1 = ReadAsset(row, "token1");
                if ((!EvmAddress.TryNormalize(token0.Address, out var normalized0)
                     && !token0.Address.Equals(deployments.NativeAsset, StringComparison.OrdinalIgnoreCase))
                    || (!EvmAddress.TryNormalize(token1.Address, out var normalized1)
                        && !token1.Address.Equals(deployments.NativeAsset, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                token0.Address = normalized0 ?? deployments.NativeAsset;
                token1.Address = normalized1 ?? deployments.NativeAsset;
                if (!token0.Address.Equals(assetAddress, StringComparison.OrdinalIgnoreCase)
                    && !token1.Address.Equals(assetAddress, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                pools.Add(new PoolCatalogEntry
                {
                    SourceId = "pancake-explorer",
                    ChainId = deployments.CatalogChainId,
                    ProtocolId = expectedProtocol,
                    PoolAddress = ReadString(row, "id").ToLowerInvariant(),
                    Labels = [protocol],
                    BaseAsset = token0,
                    QuoteAsset = token1,
                    LiquidityUsd = ReadDouble(row, "tvlUSD"),
                    Volume24hUsd = ReadDouble(row, "volumeUSD24h")
                });
            }
            var hasNextPage = document.RootElement.TryGetProperty("hasNextPage", out var hasNext)
                              && hasNext.ValueKind == JsonValueKind.True;
            return new CatalogPage(
                pools.ToArray(),
                hasNextPage,
                ReadString(document.RootElement, "endCursor"));
        }

        private static PoolCatalogAsset ReadAsset(JsonElement parent, string propertyName)
        {
            if (!parent.TryGetProperty(propertyName, out var asset)
                || asset.ValueKind != JsonValueKind.Object)
            {
                return new PoolCatalogAsset();
            }
            return new PoolCatalogAsset
            {
                Address = ReadString(asset, "id"),
                Name = ReadString(asset, "name"),
                Symbol = ReadString(asset, "symbol")
            };
        }

        private static string ReadString(JsonElement parent, string propertyName)
        {
            return parent.TryGetProperty(propertyName, out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim() ?? string.Empty
                : string.Empty;
        }

        private static double? ReadDouble(JsonElement parent, string propertyName)
        {
            if (!parent.TryGetProperty(propertyName, out var value))
            {
                return null;
            }
            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            };
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                   && double.IsFinite(number)
                   && number >= 0
                ? number
                : null;
        }

        private sealed record CatalogPage(
            PoolCatalogEntry[] Pools,
            bool HasNextPage,
            string EndCursor);
    }
}

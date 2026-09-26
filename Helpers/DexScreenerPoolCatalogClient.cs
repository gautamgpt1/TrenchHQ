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
    internal sealed class DexScreenerPoolCatalogClient
    {
        private const int MaximumResponseBytes = 2 * 1024 * 1024;
        private const int MaximumCacheEntries = 128;
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
        private static readonly HttpClient SharedHttpClient = new()
        {
            BaseAddress = new Uri("https://api.dexscreener.com/"),
            Timeout = TimeSpan.FromSeconds(15)
        };
        private readonly HttpClient _httpClient;
        private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TokenSearchCacheEntry> _tokenSearchCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Task<PoolCatalogSearchResult>> _poolSearches = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Task<TokenCatalogSearchResult>> _tokenSearches = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _cacheLock = new();
        private static readonly HashSet<string> SupportedChainIds = new(StringComparer.OrdinalIgnoreCase)
        {
            "solana",
            "ethereum",
            "base",
            "bsc",
            "robinhood"
        };

        internal static DexScreenerPoolCatalogClient Current { get; } = new(SharedHttpClient);

        internal DexScreenerPoolCatalogClient(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        internal async Task<PoolCatalogSearchResult> SearchAsync(
            string chainId,
            string assetAddress,
            CancellationToken cancellationToken = default)
        {
            chainId = RequireValue(chainId, nameof(chainId));
            assetAddress = RequireValue(assetAddress, nameof(assetAddress));
            var cacheKey = $"{chainId}|{assetAddress}";
            Task<PoolCatalogSearchResult> search;
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(cacheKey, out var cached)
                    && cached.ExpiresAt > DateTimeOffset.UtcNow)
                {
                    return cached.Result;
                }

                _cache.Remove(cacheKey);
                if (!_poolSearches.TryGetValue(cacheKey, out var existingSearch)
                    || existingSearch == null)
                {
                    search = FetchPoolsAsync(chainId, assetAddress, cacheKey);
                    _poolSearches[cacheKey] = search;
                }
                else
                {
                    search = existingSearch;
                }
            }
            return await search.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<PoolCatalogSearchResult> FetchPoolsAsync(
            string chainId,
            string assetAddress,
            string cacheKey)
        {
            await Task.Yield();
            try
            {
                var requestUri = $"token-pairs/v1/{Uri.EscapeDataString(chainId)}/{Uri.EscapeDataString(assetAddress)}";
                using var response = await _httpClient.GetAsync(
                    requestUri,
                    HttpCompletionOption.ResponseHeadersRead)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        $"DEX Screener pool catalog returned HTTP {(int)response.StatusCode}.");
                }
                if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                {
                    throw new InvalidOperationException("DEX Screener pool catalog response was too large.");
                }

                var payload = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (payload.Length > MaximumResponseBytes)
                {
                    throw new InvalidOperationException("DEX Screener pool catalog response was too large.");
                }
                var result = Parse(payload, chainId, assetAddress);
                lock (_cacheLock)
                {
                    TrimCache(_cache);
                    _cache[cacheKey] = new CacheEntry(DateTimeOffset.UtcNow + CacheLifetime, result);
                }
                return result;
            }
            finally
            {
                lock (_cacheLock)
                {
                    _poolSearches.Remove(cacheKey);
                }
            }
        }

        internal async Task<TokenCatalogSearchResult> SearchTokensAsync(
            string query,
            CancellationToken cancellationToken = default)
        {
            query = RequireValue(query, nameof(query));
            Task<TokenCatalogSearchResult> search;
            lock (_cacheLock)
            {
                if (_tokenSearchCache.TryGetValue(query, out var cached)
                    && cached.ExpiresAt > DateTimeOffset.UtcNow)
                {
                    return cached.Result;
                }

                _tokenSearchCache.Remove(query);
                if (!_tokenSearches.TryGetValue(query, out var existingSearch)
                    || existingSearch == null)
                {
                    search = FetchTokensAsync(query);
                    _tokenSearches[query] = search;
                }
                else
                {
                    search = existingSearch;
                }
            }
            return await search.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<TokenCatalogSearchResult> FetchTokensAsync(string query)
        {
            await Task.Yield();
            try
            {
                var requestUri = $"latest/dex/search?q={Uri.EscapeDataString(query)}";
                using var response = await _httpClient.GetAsync(
                    requestUri,
                    HttpCompletionOption.ResponseHeadersRead)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        $"DEX Screener token search returned HTTP {(int)response.StatusCode}.");
                }
                if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                {
                    throw new InvalidOperationException("DEX Screener token search response was too large.");
                }

                var payload = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (payload.Length > MaximumResponseBytes)
                {
                    throw new InvalidOperationException("DEX Screener token search response was too large.");
                }
                var result = ParseTokenSearch(payload, query);
                lock (_cacheLock)
                {
                    TrimCache(_tokenSearchCache);
                    _tokenSearchCache[query] = new TokenSearchCacheEntry(
                        DateTimeOffset.UtcNow + CacheLifetime,
                        result);
                }
                return result;
            }
            finally
            {
                lock (_cacheLock)
                {
                    _tokenSearches.Remove(query);
                }
            }
        }

        private static void TrimCache<T>(Dictionary<string, T> cache) where T : ICacheEntry
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var key in cache.Where(entry => entry.Value.ExpiresAt <= now).Select(entry => entry.Key).ToArray())
            {
                cache.Remove(key);
            }

            while (cache.Count >= MaximumCacheEntries)
            {
                cache.Remove(cache.MinBy(entry => entry.Value.ExpiresAt).Key);
            }
        }

        internal static PoolCatalogSearchResult Parse(
            ReadOnlyMemory<byte> payload,
            string chainId,
            string assetAddress)
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("DEX Screener pool catalog returned an invalid response.");
            }

            var pools = new List<PoolCatalogEntry>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !EqualsText(ReadString(item, "chainId"), chainId))
                {
                    continue;
                }
                var poolAddress = ReadString(item, "pairAddress");
                var protocolId = ReadString(item, "dexId");
                var baseAsset = ReadAsset(item, "baseToken");
                var quoteAsset = ReadAsset(item, "quoteToken");
                if (string.IsNullOrWhiteSpace(poolAddress)
                    || string.IsNullOrWhiteSpace(protocolId)
                    || (!EqualsText(baseAsset.Address, assetAddress)
                        && !EqualsText(quoteAsset.Address, assetAddress)))
                {
                    continue;
                }

                pools.Add(new PoolCatalogEntry
                {
                    SourceId = "dexscreener",
                    ChainId = chainId,
                    ProtocolId = protocolId,
                    PoolAddress = poolAddress,
                    Labels = ReadStringArray(item, "labels"),
                    BaseAsset = baseAsset,
                    QuoteAsset = quoteAsset,
                    LiquidityUsd = ReadNestedDouble(item, "liquidity", "usd"),
                    Volume24hUsd = ReadNestedDouble(item, "volume", "h24"),
                    MarketCapUsd = ReadDouble(item, "marketCap"),
                    FullyDilutedValuationUsd = ReadDouble(item, "fdv"),
                    CreatedAtUnixMs = ReadInt64(item, "pairCreatedAt"),
                    IconUri = ReadHttpsUri(item, "info", "imageUrl")
                });
            }

            var sorted = pools
                .GroupBy(static pool => pool.PoolAddress, StringComparer.Ordinal)
                .Select(static group => group
                    .OrderByDescending(static pool => pool.LiquidityUsd ?? double.MinValue)
                    .First())
                .OrderByDescending(static pool => pool.LiquidityUsd ?? double.MinValue)
                .ThenBy(static pool => pool.ProtocolId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static pool => pool.PoolAddress, StringComparer.Ordinal)
                .Take(30)
                .ToArray();
            var metadataPool = sorted.FirstOrDefault(pool =>
                                   EqualsText(pool.BaseAsset.Address, assetAddress)
                                   || EqualsText(pool.QuoteAsset.Address, assetAddress));
            var asset = metadataPool == null
                ? new PoolCatalogAsset { Address = assetAddress }
                : EqualsText(metadataPool.BaseAsset.Address, assetAddress)
                    ? metadataPool.BaseAsset
                    : metadataPool.QuoteAsset;

            return new PoolCatalogSearchResult
            {
                SourceId = "dexscreener",
                ChainId = chainId,
                AssetAddress = assetAddress,
                AssetName = asset.Name,
                AssetSymbol = asset.Symbol,
                IconUri = sorted.Select(static pool => pool.IconUri).FirstOrDefault(static uri => uri != null),
                Pools = sorted
            };
        }

        internal static TokenCatalogSearchResult ParseTokenSearch(
            ReadOnlyMemory<byte> payload,
            string query)
        {
            query = RequireValue(query, nameof(query));
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("pairs", out var pairs)
                || pairs.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("DEX Screener token search returned an invalid response.");
            }

            var candidates = new List<TokenSearchCandidate>();
            foreach (var pair in pairs.EnumerateArray())
            {
                if (pair.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var chainId = ReadString(pair, "chainId");
                if (!SupportedChainIds.Contains(chainId))
                {
                    continue;
                }

                var liquidityUsd = ReadNestedDouble(pair, "liquidity", "usd");
                var marketCapUsd = ReadDouble(pair, "marketCap") ?? ReadDouble(pair, "fdv");
                var iconUri = ReadHttpsUri(pair, "info", "imageUrl");
                var pool = ReadPool(pair, chainId);
                if (pool == null)
                {
                    continue;
                }
                AddTokenCandidate(
                    candidates,
                    chainId,
                    ReadAsset(pair, "baseToken"),
                    query,
                    iconUri,
                    liquidityUsd,
                    marketCapUsd,
                    pool);
                AddTokenCandidate(
                    candidates,
                    chainId,
                    ReadAsset(pair, "quoteToken"),
                    query,
                    null,
                    liquidityUsd,
                    marketCapUsd,
                    pool);
            }

            var tokens = candidates
                .GroupBy(
                    static candidate => $"{candidate.Token.ChainId}|{candidate.Token.Address}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(static group =>
                {
                    var best = group
                        .OrderBy(static candidate => candidate.MatchRank)
                        .ThenByDescending(static candidate => candidate.Token.LiquidityUsd ?? double.MinValue)
                        .First();
                    return new TokenSearchCandidate(
                        new TokenCatalogSearchEntry
                        {
                            ChainId = best.Token.ChainId,
                            Address = best.Token.Address,
                            Name = group.Select(static candidate => candidate.Token.Name)
                                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty,
                            Symbol = group.Select(static candidate => candidate.Token.Symbol)
                                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty,
                            IconUri = group.Select(static candidate => candidate.Token.IconUri)
                                .FirstOrDefault(static value => value != null),
                            LiquidityUsd = group.Max(static candidate => candidate.Token.LiquidityUsd),
                            MarketCapUsd = group.Select(static candidate => candidate.Token.MarketCapUsd)
                                .FirstOrDefault(static value => value != null),
                            Pools = group.SelectMany(static candidate => candidate.Token.Pools)
                                .GroupBy(static pool => pool.PoolAddress, StringComparer.OrdinalIgnoreCase)
                                .Select(static poolGroup => poolGroup.First())
                                .OrderByDescending(static pool => pool.LiquidityUsd ?? double.MinValue)
                                .ToArray()
                        },
                        best.MatchRank);
                })
                .OrderBy(static candidate => candidate.MatchRank)
                .ThenByDescending(static candidate => candidate.Token.LiquidityUsd ?? double.MinValue)
                .ThenBy(static candidate => candidate.Token.Symbol, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static candidate => candidate.Token.Address, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .Select(static candidate => candidate.Token)
                .ToArray();

            return new TokenCatalogSearchResult
            {
                Query = query,
                Tokens = tokens
            };
        }

        private static void AddTokenCandidate(
            ICollection<TokenSearchCandidate> candidates,
            string chainId,
            PoolCatalogAsset asset,
            string query,
            string? iconUri,
            double? liquidityUsd,
            double? marketCapUsd,
            PoolCatalogEntry pool)
        {
            if (string.IsNullOrWhiteSpace(asset.Address))
            {
                return;
            }
            var matchRank = GetMatchRank(asset, query);
            if (matchRank == int.MaxValue)
            {
                return;
            }
            candidates.Add(new TokenSearchCandidate(
                new TokenCatalogSearchEntry
                {
                    ChainId = chainId,
                    Address = asset.Address,
                    Name = asset.Name,
                    Symbol = asset.Symbol,
                    IconUri = iconUri,
                    LiquidityUsd = liquidityUsd,
                    MarketCapUsd = marketCapUsd,
                    Pools = [pool]
                },
                matchRank));
        }

        private static PoolCatalogEntry? ReadPool(JsonElement pair, string chainId)
        {
            var poolAddress = ReadString(pair, "pairAddress");
            var protocolId = ReadString(pair, "dexId");
            if (string.IsNullOrWhiteSpace(poolAddress) || string.IsNullOrWhiteSpace(protocolId))
            {
                return null;
            }
            return new PoolCatalogEntry
            {
                SourceId = "dexscreener",
                ChainId = chainId,
                ProtocolId = protocolId,
                PoolAddress = poolAddress,
                Labels = ReadStringArray(pair, "labels"),
                BaseAsset = ReadAsset(pair, "baseToken"),
                QuoteAsset = ReadAsset(pair, "quoteToken"),
                LiquidityUsd = ReadNestedDouble(pair, "liquidity", "usd"),
                Volume24hUsd = ReadNestedDouble(pair, "volume", "h24"),
                MarketCapUsd = ReadDouble(pair, "marketCap"),
                FullyDilutedValuationUsd = ReadDouble(pair, "fdv"),
                CreatedAtUnixMs = ReadInt64(pair, "pairCreatedAt"),
                IconUri = ReadHttpsUri(pair, "info", "imageUrl")
            };
        }

        private static int GetMatchRank(PoolCatalogAsset asset, string query)
        {
            if (EqualsText(asset.Address, query))
            {
                return 0;
            }
            if (EqualsText(asset.Symbol, query))
            {
                return 1;
            }
            if (EqualsText(asset.Name, query))
            {
                return 2;
            }
            if (asset.Symbol.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                return 3;
            }
            if (asset.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                return 4;
            }
            return asset.Symbol.Contains(query, StringComparison.OrdinalIgnoreCase)
                   || asset.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                ? 5
                : int.MaxValue;
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
                Address = ReadString(asset, "address"),
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
                .Where(static value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();
        }

        private static double? ReadDouble(JsonElement parent, string propertyName)
        {
            if (!parent.TryGetProperty(propertyName, out var value))
            {
                return null;
            }
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            {
                return double.IsFinite(number) && number >= 0 ? number : null;
            }
            return null;
        }

        private static double? ReadNestedDouble(JsonElement parent, string objectName, string propertyName)
        {
            return parent.TryGetProperty(objectName, out var nested)
                   && nested.ValueKind == JsonValueKind.Object
                ? ReadDouble(nested, propertyName)
                : null;
        }

        private static long? ReadInt64(JsonElement parent, string propertyName)
        {
            return parent.TryGetProperty(propertyName, out var value)
                   && value.ValueKind == JsonValueKind.Number
                   && value.TryGetInt64(out var number)
                   && number > 0
                ? number
                : null;
        }

        private static string? ReadHttpsUri(JsonElement parent, string objectName, string propertyName)
        {
            if (!parent.TryGetProperty(objectName, out var nested)
                || nested.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var value = ReadString(nested, propertyName);
            return Uri.TryCreate(value, UriKind.Absolute, out var uri)
                   && uri.Scheme == Uri.UriSchemeHttps
                ? uri.AbsoluteUri
                : null;
        }

        private static bool EqualsText(string? left, string? right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static string RequireValue(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("A value is required.", parameterName);
            }
            return value.Trim();
        }

        private interface ICacheEntry
        {
            DateTimeOffset ExpiresAt { get; }
        }

        private sealed record CacheEntry(DateTimeOffset ExpiresAt, PoolCatalogSearchResult Result) : ICacheEntry;
        private sealed record TokenSearchCacheEntry(DateTimeOffset ExpiresAt, TokenCatalogSearchResult Result) : ICacheEntry;
        private sealed record TokenSearchCandidate(TokenCatalogSearchEntry Token, int MatchRank);
    }
}

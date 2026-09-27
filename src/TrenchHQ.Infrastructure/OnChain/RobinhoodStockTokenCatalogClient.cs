using TrenchHQ.Core.OnChain;
using TrenchHQ.Core.OnChain.Evm;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrenchHQ.Infrastructure.OnChain
{
    internal sealed record RobinhoodStockTokenAsset(
        string Address,
        string Symbol,
        string Name,
        string CurrentMultiplier,
        string? LogoUri);

    internal sealed record RobinhoodStockTokenReference(
        string Address,
        string Symbol,
        OnChainDecimalValue Value,
        string SourceId,
        long ObservedAtUnixMs);

    internal sealed class RobinhoodStockTokenCatalogClient
    {
        private const string ApiRoot = "https://api.robinhood.com/rhj/";
        private const int MaximumResponseBytes = 2 * 1024 * 1024;
        private const int MaximumMidpointSpreadBasisPoints = 200;
        private const int BasisPointDenominator = 10_000;
        private static readonly TimeSpan AssetCacheDuration = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan PriceCacheDuration = TimeSpan.FromSeconds(15);

        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _refreshLock = new(1, 1);
        private readonly object _cacheSync = new();
        private Dictionary<string, RobinhoodStockTokenAsset> _assetsByAddress =
            new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, RobinhoodStockTokenReferenceCacheEntry> _pricesByAddress =
            new(StringComparer.OrdinalIgnoreCase);
        private DateTimeOffset _assetsExpiresAt;

        internal static RobinhoodStockTokenCatalogClient Current { get; } = new(new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        });

        internal RobinhoodStockTokenCatalogClient(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        internal bool TryGetCachedByAddress(string address, out RobinhoodStockTokenAsset asset)
        {
            lock (_cacheSync)
            {
                return _assetsByAddress.TryGetValue(address, out asset!);
            }
        }

        internal async Task<RobinhoodStockTokenAsset?> FindByAddressAsync(
            string address,
            CancellationToken cancellationToken = default)
        {
            if (!EvmAddress.TryNormalize(address, out var normalized))
            {
                return null;
            }
            var assets = await GetAssetsAsync(cancellationToken).ConfigureAwait(false);
            return assets.TryGetValue(normalized, out var asset) ? asset : null;
        }

        internal async Task<RobinhoodStockTokenAsset?> ResolveAsync(
            string query,
            CancellationToken cancellationToken = default)
        {
            var value = query.Trim();
            if (value.Length == 0)
            {
                return null;
            }
            if (EvmAddress.TryNormalize(value, out var address))
            {
                return await FindByAddressAsync(address, cancellationToken).ConfigureAwait(false);
            }
            var assets = (await GetAssetsAsync(cancellationToken).ConfigureAwait(false)).Values;
            return assets.FirstOrDefault(asset => asset.Symbol.Equals(value, StringComparison.OrdinalIgnoreCase))
                   ?? assets.FirstOrDefault(asset => asset.Name.Equals(value, StringComparison.OrdinalIgnoreCase));
        }

        internal async Task<RobinhoodStockTokenReference?> GetUsdReferenceAsync(
            string address,
            CancellationToken cancellationToken = default)
        {
            var asset = await FindByAddressAsync(address, cancellationToken).ConfigureAwait(false);
            if (asset == null)
            {
                return null;
            }
            lock (_cacheSync)
            {
                if (_pricesByAddress.TryGetValue(asset.Address, out var cached)
                    && cached.ExpiresAt > DateTimeOffset.UtcNow)
                {
                    return cached.Reference;
                }
            }

            await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_cacheSync)
                {
                    if (_pricesByAddress.TryGetValue(asset.Address, out var cached)
                        && cached.ExpiresAt > DateTimeOffset.UtcNow)
                    {
                        return cached.Reference;
                    }
                }

                using var document = await GetJsonAsync(
                    $"prices/{Uri.EscapeDataString(asset.Symbol)}",
                    cancellationToken).ConfigureAwait(false);
                if (!document.RootElement.TryGetProperty("quotes", out var quotes)
                    || quotes.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException("Robinhood Stock Token prices returned an invalid response.");
                }
                foreach (var quote in quotes.EnumerateArray())
                {
                    if (!TryReadString(quote, "tokenSymbol", out var quoteSymbol)
                        || !quoteSymbol.Equals(asset.Symbol, StringComparison.OrdinalIgnoreCase)
                        || !HasDeployment(quote, asset.Address)
                        || !quote.TryGetProperty("isTradingHalt", out var tradingHalt)
                        || tradingHalt.ValueKind != JsonValueKind.False
                        || !TryReadString(quote, "bid", out var bid)
                        || !TryReadString(quote, "ask", out var ask)
                        || !TryReadString(quote, "currency", out var currency)
                        || !currency.Equals("USD", StringComparison.OrdinalIgnoreCase)
                        || !TryReferencePrice(quote, bid, ask, out var referencePrice)
                        || !TryMultiply(referencePrice, asset.CurrentMultiplier, out var value))
                    {
                        continue;
                    }
                    var observedAt = DateTimeOffset.UtcNow;
                    if (TryReadString(quote, "generatedAt", out var generatedAt)
                        && DateTimeOffset.TryParse(
                            generatedAt,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal,
                            out var parsed))
                    {
                        observedAt = parsed;
                    }
                    var reference = new RobinhoodStockTokenReference(
                        asset.Address,
                        asset.Symbol,
                        value,
                        $"robinhood-stock-token-api:{asset.Symbol}",
                        observedAt.ToUnixTimeMilliseconds());
                    lock (_cacheSync)
                    {
                        _pricesByAddress[asset.Address] = new RobinhoodStockTokenReferenceCacheEntry(
                            reference,
                            DateTimeOffset.UtcNow + PriceCacheDuration);
                    }
                    return reference;
                }
                throw new InvalidOperationException("Robinhood Stock Token prices did not include a usable USD quote.");
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        private async Task<Dictionary<string, RobinhoodStockTokenAsset>> GetAssetsAsync(
            CancellationToken cancellationToken)
        {
            lock (_cacheSync)
            {
                if (_assetsExpiresAt > DateTimeOffset.UtcNow)
                {
                    return _assetsByAddress;
                }
            }
            await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_cacheSync)
                {
                    if (_assetsExpiresAt > DateTimeOffset.UtcNow)
                    {
                        return _assetsByAddress;
                    }
                }
                using var document = await GetJsonAsync("assets", cancellationToken).ConfigureAwait(false);
                if (!document.RootElement.TryGetProperty("assets", out var assets)
                    || assets.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException("Robinhood Stock Token assets returned an invalid response.");
                }
                var result = new Dictionary<string, RobinhoodStockTokenAsset>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in assets.EnumerateArray())
                {
                    if (!TryReadString(item, "status", out var status)
                        || !status.Equals("ASSET_STATUS_ACTIVE", StringComparison.Ordinal)
                        || !TryReadString(item, "tokenSymbol", out var symbol)
                        || !TryReadString(item, "tokenName", out var name)
                        || !TryReadString(item, "currentMultiplier", out var multiplier)
                        || !TryReadPositiveDecimal(multiplier, out _, out _)
                        || !item.TryGetProperty("deployments", out var deployments)
                        || deployments.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }
                    var logo = TryReadString(item, "logoUrl", out var logoUri) ? logoUri : null;
                    foreach (var deployment in deployments.EnumerateArray())
                    {
                        if (!deployment.TryGetProperty("chainId", out var chainId)
                            || !chainId.TryGetInt32(out var numericChainId)
                            || numericChainId != 4663
                            || !TryReadString(deployment, "contractAddress", out var contract)
                            || !EvmAddress.TryNormalize(contract, out var normalized))
                        {
                            continue;
                        }
                        result[normalized] = new RobinhoodStockTokenAsset(
                            normalized,
                            symbol.Trim(),
                            name.Trim(),
                            multiplier,
                            Uri.TryCreate(logo, UriKind.Absolute, out var parsedLogo)
                            && parsedLogo.Scheme == Uri.UriSchemeHttps
                                ? parsedLogo.AbsoluteUri
                                : null);
                    }
                }
                lock (_cacheSync)
                {
                    _assetsByAddress = result;
                    _assetsExpiresAt = DateTimeOffset.UtcNow + AssetCacheDuration;
                    return _assetsByAddress;
                }
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
        {
            using var response = await _httpClient.GetAsync(
                new Uri(new Uri(ApiRoot), path),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Robinhood Stock Token API returned HTTP {(int)response.StatusCode}.");
            }
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            {
                throw new InvalidOperationException("Robinhood Stock Token API response was too large.");
            }
            var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (payload.Length > MaximumResponseBytes)
            {
                throw new InvalidOperationException("Robinhood Stock Token API response was too large.");
            }
            try
            {
                return JsonDocument.Parse(payload);
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException("Robinhood Stock Token API returned invalid JSON.", exception);
            }
        }

        private static bool TryReadString(JsonElement item, string propertyName, out string value)
        {
            value = string.Empty;
            if (!item.TryGetProperty(propertyName, out var property)
                || property.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(property.GetString()))
            {
                return false;
            }
            value = property.GetString()!;
            return true;
        }

        private static bool HasDeployment(JsonElement item, string expectedAddress)
        {
            return item.TryGetProperty("deployments", out var deployments)
                   && deployments.ValueKind == JsonValueKind.Array
                   && deployments.EnumerateArray().Any(deployment =>
                       deployment.TryGetProperty("chainId", out var chainId)
                       && chainId.TryGetInt32(out var numericChainId)
                       && numericChainId == 4663
                       && TryReadString(deployment, "contractAddress", out var contract)
                       && EvmAddress.TryNormalize(contract, out var normalized)
                       && normalized.Equals(expectedAddress, StringComparison.OrdinalIgnoreCase));
        }

        private static bool TryMidpoint(string bid, string ask, out OnChainDecimalValue midpoint)
        {
            midpoint = new OnChainDecimalValue();
            if (!TryReadPositiveDecimal(bid, out var bidCoefficient, out var bidScale)
                || !TryReadPositiveDecimal(ask, out var askCoefficient, out var askScale))
            {
                return false;
            }
            var scale = Math.Max(bidScale, askScale);
            var sum = bidCoefficient * BigInteger.Pow(10, checked((int)(scale - bidScale)))
                      + askCoefficient * BigInteger.Pow(10, checked((int)(scale - askScale)));
            if (sum.IsEven)
            {
                sum /= 2;
            }
            else
            {
                sum *= 5;
                scale++;
            }
            midpoint = Normalize(sum, scale);
            return true;
        }

        private static bool TryReferencePrice(
            JsonElement quote,
            string bid,
            string ask,
            out OnChainDecimalValue referencePrice)
        {
            referencePrice = new OnChainDecimalValue();
            if (!TryReadPositiveDecimal(bid, out var bidCoefficient, out var bidScale)
                || !TryReadPositiveDecimal(ask, out var askCoefficient, out var askScale))
            {
                return false;
            }
            var quoteScale = Math.Max(bidScale, askScale);
            var scaledBid = ScaleTo(bidCoefficient, bidScale, quoteScale);
            var scaledAsk = ScaleTo(askCoefficient, askScale, quoteScale);
            if (scaledAsk < scaledBid)
            {
                return false;
            }
            if ((scaledAsk - scaledBid) * BasisPointDenominator
                <= scaledBid * MaximumMidpointSpreadBasisPoints)
            {
                return TryMidpoint(bid, ask, out referencePrice);
            }

            if (!TryReadString(quote, "dailyLow", out var dailyLow)
                || !TryReadPositiveDecimal(dailyLow, out var lowCoefficient, out var lowScale)
                || !TryReadString(quote, "dailyHigh", out var dailyHigh)
                || !TryReadPositiveDecimal(dailyHigh, out var highCoefficient, out var highScale))
            {
                return false;
            }
            var rangeScale = Math.Max(quoteScale, Math.Max(lowScale, highScale));
            scaledBid = ScaleTo(bidCoefficient, bidScale, rangeScale);
            scaledAsk = ScaleTo(askCoefficient, askScale, rangeScale);
            var scaledLow = ScaleTo(lowCoefficient, lowScale, rangeScale);
            var scaledHigh = ScaleTo(highCoefficient, highScale, rangeScale);
            if (scaledLow > scaledHigh)
            {
                return false;
            }
            var bidIsInRange = scaledBid >= scaledLow && scaledBid <= scaledHigh;
            var askIsInRange = scaledAsk >= scaledLow && scaledAsk <= scaledHigh;
            if (bidIsInRange == askIsInRange)
            {
                return false;
            }
            referencePrice = bidIsInRange
                ? Normalize(bidCoefficient, bidScale)
                : Normalize(askCoefficient, askScale);
            return true;
        }

        private static BigInteger ScaleTo(BigInteger coefficient, uint scale, uint targetScale)
        {
            return coefficient * BigInteger.Pow(10, checked((int)(targetScale - scale)));
        }

        private static bool TryMultiply(
            OnChainDecimalValue left,
            string right,
            out OnChainDecimalValue product)
        {
            product = new OnChainDecimalValue();
            if (!BigInteger.TryParse(left.Coefficient, NumberStyles.None, CultureInfo.InvariantCulture, out var leftValue)
                || !TryReadPositiveDecimal(right, out var rightValue, out var rightScale))
            {
                return false;
            }
            product = Normalize(leftValue * rightValue, checked(left.Scale + rightScale));
            return true;
        }

        private static bool TryReadPositiveDecimal(
            string text,
            out BigInteger coefficient,
            out uint scale)
        {
            coefficient = BigInteger.Zero;
            scale = 0;
            var value = text.Trim();
            var separator = value.IndexOf('.');
            var digits = separator < 0 ? value : value.Remove(separator, 1);
            scale = separator < 0 ? 0u : checked((uint)(value.Length - separator - 1));
            return digits.Length > 0
                   && digits.All(char.IsAsciiDigit)
                   && BigInteger.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out coefficient)
                   && coefficient > BigInteger.Zero;
        }

        private static OnChainDecimalValue Normalize(BigInteger coefficient, uint scale)
        {
            while (scale > 0 && coefficient % 10 == 0)
            {
                coefficient /= 10;
                scale--;
            }
            return new OnChainDecimalValue
            {
                Coefficient = coefficient.ToString(CultureInfo.InvariantCulture),
                Scale = scale
            };
        }

        private sealed record RobinhoodStockTokenReferenceCacheEntry(
            RobinhoodStockTokenReference Reference,
            DateTimeOffset ExpiresAt);
    }
}

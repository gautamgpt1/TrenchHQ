using TrenchHQ.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace TrenchHQ.Helpers
{
    internal sealed record SavedWidgetCatalogNormalizationResult(SavedWidgetCatalog Catalog, bool WasChanged);

    internal static class SavedWidgetCatalogRules
    {
        internal const int CurrentVersion = 7;
        internal const int MaximumWalletsPerWidget = 20;

        private static readonly JsonSerializerOptions ComparisonJsonOptions = new();
        private static readonly HashSet<string> XReservedPaths = new(StringComparer.OrdinalIgnoreCase)
        {
            "compose", "explore", "home", "i", "intent", "messages", "notifications", "search", "settings"
        };

        internal static SavedWidgetCatalogNormalizationResult Normalize(SavedWidgetCatalog catalog)
        {
            var before = JsonSerializer.Serialize(catalog, ComparisonJsonOptions);
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            catalog.Version = CurrentVersion;
            catalog.Widgets ??= [];
            var normalizedWidgets = new List<SavedWidgetDefinition>();
            var priceTickerIndex = 0;
            var walletActivityIndex = 0;
            var xTimelineIndex = 0;
            var websiteIndex = 0;
            foreach (var widget in catalog.Widgets.Where(static widget => widget != null))
            {
                if (string.Equals(widget.Type, PanelWidgetTypes.PriceTicker, StringComparison.OrdinalIgnoreCase))
                {
                    normalizedWidgets.Add(NormalizePriceTicker(widget, priceTickerIndex++, usedIds));
                }
                else if (string.Equals(widget.Type, PanelWidgetTypes.WalletActivity, StringComparison.OrdinalIgnoreCase))
                {
                    normalizedWidgets.Add(NormalizeWalletActivity(widget, walletActivityIndex++, usedIds));
                }
                else if (string.Equals(widget.Type, PanelWidgetTypes.XTimeline, StringComparison.OrdinalIgnoreCase))
                {
                    normalizedWidgets.Add(NormalizeXTimeline(widget, xTimelineIndex++, usedIds));
                }
                else if (string.Equals(widget.Type, PanelWidgetTypes.Website, StringComparison.OrdinalIgnoreCase))
                {
                    widget.Id = NormalizeId(widget.Id, usedIds);
                    widget.Name = $"Website {++websiteIndex}";
                    widget.Type = PanelWidgetTypes.Website;
                    widget.Instruments = [];
                    widget.Wallets = [];
                    widget.XHandles = [];
                    widget.XExternalContentConsent = false;
                    widget.WebsiteUrl = WebsiteWidgetRules.TryUrl(widget.WebsiteUrl, out var url) ? url : "";
                    widget.WebsiteEnabled &= widget.WebsiteUrl.Length > 0;
                    widget.WebsiteZoom = WebsiteWidgetRules.Zoom(widget.WebsiteZoom);
                    normalizedWidgets.Add(widget);
                }
            }
            catalog.Widgets = normalizedWidgets.ToArray();

            var after = JsonSerializer.Serialize(catalog, ComparisonJsonOptions);
            return new SavedWidgetCatalogNormalizationResult(
                catalog,
                !string.Equals(before, after, StringComparison.Ordinal));
        }

        internal static string GetPriceTickerName(int index) => $"Price Ticker {index + 1}";

        internal static string GetWalletActivityName(int index) => $"Wallet Watcher {index + 1}";

        internal static string GetXTimelineName(int index) => $"X Tracker {index + 1}";

        private static SavedWidgetDefinition NormalizePriceTicker(
            SavedWidgetDefinition widget,
            int index,
            HashSet<string> usedIds)
        {
            widget.Id = NormalizeId(widget.Id, usedIds);
            widget.Name = GetPriceTickerName(index);
            widget.Type = PanelWidgetTypes.PriceTicker;
            widget.OnChainPriceMode = OnChainPriceModes.Normalize(widget.OnChainPriceMode);
            widget.Wallets = [];
            widget.XTimelineUrl = null;
            widget.XExternalContentConsent = false;
            widget.Instruments = (widget.Instruments ?? [])
                .Select(NormalizeInstrument)
                .OfType<SavedTickerInstrument>()
                .GroupBy(GetInstrumentKey, StringComparer.Ordinal)
                .Select(static group => group.First())
                .ToArray();
            if (widget.OnChainPriceMode == OnChainPriceModes.OneSecondPolling
                && widget.Instruments.Any(static instrument =>
                    instrument.Kind == TickerInstrumentTypes.OnChainPool
                    && instrument.Pool != null
                    && !OnChainPriceModes.SupportsPolling(instrument.Pool)))
            {
                widget.OnChainPriceMode = OnChainPriceModes.WebSocket;
            }
            return widget;
        }

        private static SavedWidgetDefinition NormalizeWalletActivity(
            SavedWidgetDefinition widget,
            int index,
            HashSet<string> usedIds)
        {
            widget.Id = NormalizeId(widget.Id, usedIds);
            widget.Name = GetWalletActivityName(index);
            widget.Type = PanelWidgetTypes.WalletActivity;
            widget.Instruments = [];
            widget.XTimelineUrl = null;
            widget.XExternalContentConsent = false;
            widget.Wallets = (widget.Wallets ?? [])
                .Select(NormalizeWallet)
                .OfType<SavedTrackedWallet>()
                .GroupBy(GetWalletKey, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .Take(MaximumWalletsPerWidget)
                .ToArray();
            return widget;
        }

        private static SavedWidgetDefinition NormalizeXTimeline(
            SavedWidgetDefinition widget,
            int index,
            HashSet<string> usedIds)
        {
            widget.Id = NormalizeId(widget.Id, usedIds);
            widget.Name = GetXTimelineName(index);
            widget.Type = PanelWidgetTypes.XTimeline;
            widget.Instruments = [];
            widget.Wallets = [];
            var source = string.Join(',', widget.XHandles ?? []);
            if (string.IsNullOrWhiteSpace(source)) source = widget.XTimelineUrl ?? string.Empty;
            widget.XHandles = SocialFeedRules.TryHandles(source, out var handles, out _) ? handles : [];
            widget.XTextFilter = (widget.XTextFilter ?? string.Empty).Trim();
            widget.XTimelineUrl = TryNormalizeXTimelineUrl(widget.XTimelineUrl, out var normalizedUrl)
                ? normalizedUrl
                : null;
            return widget;
        }

        internal static bool TryNormalizeXTimelineUrl(string? value, out string normalized)
        {
            normalized = string.Empty;
            var candidate = value?.Trim() ?? string.Empty;
            if (candidate.StartsWith('@'))
            {
                candidate = candidate[1..];
            }
            if (IsXUsername(candidate))
            {
                normalized = $"https://x.com/{candidate}";
                return true;
            }

            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                || !IsXHost(uri.Host)
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment))
            {
                return false;
            }

            var segments = uri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 1 && IsXUsername(segments[0]))
            {
                normalized = $"https://x.com/{segments[0]}";
                return true;
            }
            if (segments.Length == 3
                && string.Equals(segments[0], "i", StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[1], "lists", StringComparison.OrdinalIgnoreCase)
                && ulong.TryParse(segments[2], out _))
            {
                normalized = $"https://x.com/i/lists/{segments[2]}";
                return true;
            }
            if (segments.Length == 3
                && IsXUsername(segments[0])
                && string.Equals(segments[1], "lists", StringComparison.OrdinalIgnoreCase)
                && IsXListSlug(segments[2]))
            {
                normalized = $"https://x.com/{segments[0]}/lists/{segments[2]}";
                return true;
            }
            return false;
        }

        private static bool IsXHost(string host)
        {
            return host.Equals("x.com", StringComparison.OrdinalIgnoreCase)
                   || host.Equals("www.x.com", StringComparison.OrdinalIgnoreCase)
                   || host.Equals("twitter.com", StringComparison.OrdinalIgnoreCase)
                   || host.Equals("www.twitter.com", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsXUsername(string value)
        {
            return value.Length is >= 1 and <= 15
                   && !XReservedPaths.Contains(value)
                   && value.All(static character => char.IsAsciiLetterOrDigit(character) || character == '_');
        }

        private static bool IsXListSlug(string value)
        {
            return value.Length is >= 1 and <= 100
                   && value.All(static character => char.IsAsciiLetterOrDigit(character)
                                                    || character is '_' or '-');
        }

        internal static SavedTrackedWallet? NormalizeWallet(SavedTrackedWallet? wallet)
        {
            if (wallet == null)
            {
                return null;
            }

            var chainNamespace = wallet.ChainNamespace?.Trim().ToLowerInvariant();
            var chainId = wallet.ChainId?.Trim();
            string normalizedAddress;
            if (chainNamespace == ChainNamespaces.Eip155
                && EvmChainDefinitions.Supported.Any(chain => chain.ChainId == chainId)
                && EvmAddress.TryNormalize(wallet.Address, out normalizedAddress))
            {
            }
            else if (chainNamespace == ChainNamespaces.Solana
                     && chainId == "mainnet-beta"
                     && TryNormalizeSolanaAddress(wallet.Address, out normalizedAddress))
            {
            }
            else
            {
                return null;
            }

            wallet.ChainNamespace = chainNamespace;
            wallet.ChainId = chainId!;
            wallet.Address = normalizedAddress;
            wallet.Label = string.IsNullOrWhiteSpace(wallet.Label)
                ? Shorten(normalizedAddress)
                : wallet.Label.Trim();
            return wallet;
        }

        internal static string GetWalletKey(SavedTrackedWallet wallet)
        {
            var address = wallet.ChainNamespace == ChainNamespaces.Eip155
                ? wallet.Address.ToLowerInvariant()
                : wallet.Address;
            return $"{wallet.ChainNamespace.ToLowerInvariant()}|{wallet.ChainId}|{address}";
        }

        internal static string GetWalletChainDisplayName(SavedTrackedWallet wallet)
        {
            if (wallet.ChainNamespace == ChainNamespaces.Solana)
            {
                return "Solana";
            }
            return EvmChainDefinitions.Supported.FirstOrDefault(chain => chain.ChainId == wallet.ChainId)?.DisplayName
                   ?? "Unsupported chain";
        }

        private static bool TryNormalizeSolanaAddress(string? value, out string normalized)
        {
            normalized = string.Empty;
            try
            {
                var decoded = SolanaBase58.Decode(value?.Trim() ?? string.Empty);
                if (decoded.Length != 32)
                {
                    return false;
                }
                normalized = SolanaBase58.Encode(decoded);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        internal static SavedTickerInstrument[] CreateCentralizedInstruments(IEnumerable<string>? labels)
        {
            return (labels ?? [])
                .Select(CreateCentralizedInstrument)
                .OfType<SavedTickerInstrument>()
                .GroupBy(GetInstrumentKey, StringComparer.Ordinal)
                .Select(static group => group.First())
                .ToArray();
        }

        internal static string GetInstrumentKey(SavedTickerInstrument instrument)
        {
            if (instrument.Kind == TickerInstrumentTypes.OnChainPool)
            {
                var poolKey = instrument.Pool?.PoolKey;
                var isEvm = string.Equals(
                    poolKey?.ChainNamespace,
                    ChainNamespaces.Eip155,
                    StringComparison.OrdinalIgnoreCase);
                var protocolId = isEvm ? poolKey?.ProtocolId.ToLowerInvariant() : poolKey?.ProtocolId;
                var poolId = isEvm ? poolKey?.PoolAddress.ToLowerInvariant() : poolKey?.PoolAddress;
                return $"onchain|{poolKey?.ChainNamespace.ToLowerInvariant()}|{poolKey?.ChainId}|{protocolId}|{poolId}";
            }
            return $"cex|{instrument.VenueId?.ToLowerInvariant()}|{instrument.Symbol?.ToUpperInvariant()}";
        }

        private static SavedTickerInstrument? CreateCentralizedInstrument(string label)
        {
            if (!SelectedPairParser.TryParse(label, out var symbol, out var venueId, out var display))
            {
                return null;
            }
            return new SavedTickerInstrument
            {
                Kind = TickerInstrumentTypes.CentralizedMarket,
                DisplayLabel = display,
                VenueId = venueId,
                Symbol = symbol
            };
        }

        private static SavedTickerInstrument? NormalizeInstrument(SavedTickerInstrument? instrument)
        {
            if (instrument == null)
            {
                return null;
            }
            if (string.Equals(
                    instrument.Kind,
                    TickerInstrumentTypes.CentralizedMarket,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(instrument.VenueId)
                    || string.IsNullOrWhiteSpace(instrument.Symbol))
                {
                    return null;
                }
                instrument.Kind = TickerInstrumentTypes.CentralizedMarket;
                instrument.VenueId = instrument.VenueId.Trim().ToLowerInvariant();
                instrument.Symbol = instrument.Symbol.Trim().ToUpperInvariant();
                instrument.DisplayLabel = string.IsNullOrWhiteSpace(instrument.DisplayLabel)
                    ? instrument.Symbol
                    : instrument.DisplayLabel.Trim();
                instrument.SelectedMint = null;
                instrument.AssetName = null;
                instrument.AssetSymbol = null;
                instrument.QuoteSymbol = null;
                instrument.IconUri = null;
                instrument.Pool = null;
                return instrument;
            }
            if (string.Equals(
                    instrument.Kind,
                    TickerInstrumentTypes.OnChainPool,
                    StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(instrument.SelectedMint)
                && !string.IsNullOrWhiteSpace(instrument.Pool?.PoolKey?.ChainNamespace)
                && !string.IsNullOrWhiteSpace(instrument.Pool.PoolKey.ChainId)
                && !string.IsNullOrWhiteSpace(instrument.Pool?.PoolKey?.ProtocolId)
                && !string.IsNullOrWhiteSpace(instrument.Pool.PoolKey.PoolAddress))
            {
                instrument.Kind = TickerInstrumentTypes.OnChainPool;
                instrument.SelectedMint = instrument.SelectedMint.Trim();
                instrument.AssetName = NormalizeOptional(instrument.AssetName);
                instrument.AssetSymbol = NormalizeOptional(instrument.AssetSymbol);
                instrument.QuoteSymbol = NormalizeOptional(instrument.QuoteSymbol);
                instrument.IconUri = NormalizeHttpsUri(instrument.IconUri);
                instrument.DisplayLabel = string.IsNullOrWhiteSpace(instrument.DisplayLabel)
                    ? BuildOnChainDisplayLabel(instrument)
                    : instrument.DisplayLabel.Trim();
                instrument.VenueId = null;
                instrument.Symbol = null;
                return instrument;
            }
            return null;
        }

        private static string Shorten(string value)
        {
            return value.Length <= 12 ? value : $"{value[..6]}…{value[^4..]}";
        }

        private static string BuildOnChainDisplayLabel(SavedTickerInstrument instrument)
        {
            var asset = instrument.AssetSymbol ?? Shorten(instrument.SelectedMint!);
            return instrument.QuoteSymbol == null ? asset : $"{asset} / {instrument.QuoteSymbol}";
        }

        private static string? NormalizeOptional(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string? NormalizeHttpsUri(string? value)
        {
            value = NormalizeOptional(value);
            return Uri.TryCreate(value, UriKind.Absolute, out var uri)
                   && uri.Scheme == Uri.UriSchemeHttps
                ? uri.AbsoluteUri
                : null;
        }

        private static string NormalizeId(string? value, HashSet<string> usedIds)
        {
            if (Guid.TryParseExact(value?.Trim(), "N", out var parsed))
            {
                var normalized = parsed.ToString("N");
                if (usedIds.Add(normalized))
                {
                    return normalized;
                }
            }

            string generated;
            do
            {
                generated = Guid.NewGuid().ToString("N");
            }
            while (!usedIds.Add(generated));

            return generated;
        }
    }
}

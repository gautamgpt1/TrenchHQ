using TrenchHQ.Helpers;
using TrenchHQ.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Windows.UI;

namespace TrenchHQ
{
    public sealed class WalletActivityRow : INotifyPropertyChanged
    {
        private readonly WalletActivityUpdate[] _updates;
        private readonly long _observedAtUnixMs;
        private string _flowText;
        private string _marketCapText = string.Empty;
        private ImageSource? _venueIconSource;
        private ImageSource? _tokenIconSource;
        private double _contentScale = 1d;

        internal WalletActivityRow(IReadOnlyCollection<WalletActivityUpdate> updates)
        {
            ArgumentNullException.ThrowIfNull(updates);
            _updates = updates.ToArray();
            var update = _updates.First();
            var presentation = WalletActivityPresentationRules.Build(_updates);
            WalletLabel = update.WalletLabel;
            Summary = presentation.Summary;
            Detail = presentation.Detail;
            _flowText = GetFlowText(presentation);
            ActionBrush = new SolidColorBrush(GetActionColor(presentation.Kind));
            ChainLabel = GetChainLabel(update);
            ChainIconSource = CreateSvgImageSource(GetChainIconUri(update));
            MarketAssetAddress = presentation.MarketAssetAddress;
            DexScreenerChainId = WalletActivityMetadataRules.ToDexScreenerChainId(update.ChainId);
            var venueId = _updates
                .Select(static item => item.VenueId)
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
            VenueLabel = GetVenueLabel(venueId);
            _venueIconSource = CreateVenueIconSource(venueId);
            _observedAtUnixMs = _updates.Max(static item => item.ObservedAtUnixMs);
            Age = WalletActivityAgeRules.Format(_observedAtUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            ToolTip = BuildToolTip(update, presentation, _updates.Length);
        }

        public string WalletLabel { get; }
        public string Summary { get; private set; }
        public string Detail { get; private set; }
        public string FlowText => _flowText;
        public SolidColorBrush ActionBrush { get; }
        public string ChainLabel { get; }
        public string Age { get; private set; }
        public ImageSource ChainIconSource { get; }
        public ImageSource CoinIconSource => _tokenIconSource ?? ChainIconSource;
        public ImageSource? VenueIconSource => _venueIconSource;
        public Visibility VenueIconVisibility => _venueIconSource == null ? Visibility.Collapsed : Visibility.Visible;
        public string VenueLabel { get; private set; }
        public string MarketCapText => _marketCapText;
        public string MarketCapDisplayText => string.IsNullOrWhiteSpace(_marketCapText)
            ? string.Empty
            : $"MC {_marketCapText}";
        public Visibility MarketCapVisibility => string.IsNullOrWhiteSpace(_marketCapText)
            ? Visibility.Collapsed
            : Visibility.Visible;
        public string ToolTip { get; }
        public Thickness OverlayPadding => new(8 * _contentScale, 7 * _contentScale, 8 * _contentScale, 7 * _contentScale);
        public double OverlayRowSpacing => 2 * _contentScale;
        public double OverlayColumnSpacing => 4 * _contentScale;
        public double OverlayFlowColumnSpacing => 8 * _contentScale;
        public double OverlayInlineSpacing => 5 * _contentScale;
        public double OverlayFontSize => 13 * _contentScale;
        public double OverlaySecondaryFontSize => 11 * _contentScale;
        public double OverlayIconSize => 16 * _contentScale;
        public Thickness OverlayIconMargin => new(2 * _contentScale, 2 * _contentScale, 2 * _contentScale, 2 * _contentScale);
        public double HorizontalWidth => 350 * _contentScale;
        public Thickness HorizontalPadding => new(8 * _contentScale, 2 * _contentScale, 8 * _contentScale, 2 * _contentScale);
        public double HorizontalRowSpacing => 1 * _contentScale;
        public double CompactColumnSpacing => 3 * _contentScale;
        public double CompactFlowColumnSpacing => 4 * _contentScale;
        public double CompactInlineSpacing => 4 * _contentScale;
        public double CompactFontSize => 11 * _contentScale;
        public double CompactSecondaryFontSize => 10 * _contentScale;
        public double CompactIconSize => 14 * _contentScale;
        public Thickness CompactIconMargin => new(2 * _contentScale, 2 * _contentScale, 2 * _contentScale, 2 * _contentScale);
        public Thickness VerticalPadding => new(8 * _contentScale, 6 * _contentScale, 8 * _contentScale, 6 * _contentScale);
        public double VerticalRowSpacing => 2 * _contentScale;

        internal string? MarketAssetAddress { get; }
        internal string? DexScreenerChainId { get; }
        internal string? MetadataKey => string.IsNullOrWhiteSpace(DexScreenerChainId)
                                        || string.IsNullOrWhiteSpace(MarketAssetAddress)
            ? null
            : $"{DexScreenerChainId}|{MarketAssetAddress}";

        public event PropertyChangedEventHandler? PropertyChanged;

        internal void RefreshAge(long nowUnixMs)
        {
            var age = WalletActivityAgeRules.Format(_observedAtUnixMs, nowUnixMs);
            if (string.Equals(Age, age, StringComparison.Ordinal))
            {
                return;
            }
            Age = age;
            NotifyChanged(nameof(Age));
        }

        internal void SetContentScale(double scale)
        {
            var normalized = PanelContentSizeRules.NormalizeScale(scale);
            if (Math.Abs(_contentScale - normalized) < 0.001)
            {
                return;
            }

            _contentScale = normalized;
            foreach (var propertyName in new[]
                     {
                         nameof(OverlayPadding), nameof(OverlayRowSpacing), nameof(OverlayColumnSpacing),
                         nameof(OverlayFlowColumnSpacing), nameof(OverlayInlineSpacing), nameof(OverlayFontSize),
                         nameof(OverlaySecondaryFontSize), nameof(OverlayIconSize), nameof(OverlayIconMargin),
                         nameof(HorizontalWidth), nameof(HorizontalPadding), nameof(HorizontalRowSpacing),
                         nameof(CompactColumnSpacing), nameof(CompactFlowColumnSpacing), nameof(CompactInlineSpacing),
                         nameof(CompactFontSize), nameof(CompactSecondaryFontSize), nameof(CompactIconSize),
                         nameof(CompactIconMargin), nameof(VerticalPadding), nameof(VerticalRowSpacing)
                     })
            {
                NotifyChanged(propertyName);
            }
        }

        internal void ApplyCatalog(PoolCatalogSearchResult catalog)
        {
            if (string.IsNullOrWhiteSpace(MarketAssetAddress))
            {
                return;
            }
            if (!string.IsNullOrWhiteSpace(catalog.AssetSymbol))
            {
                foreach (var update in _updates.Where(update => string.Equals(
                             update.AssetAddress,
                             MarketAssetAddress,
                             StringComparison.OrdinalIgnoreCase)))
                {
                    update.AssetSymbol = catalog.AssetSymbol;
                }
                var presentation = WalletActivityPresentationRules.Build(_updates);
                Summary = presentation.Summary;
                Detail = presentation.Detail;
                _flowText = GetFlowText(presentation);
                NotifyChanged(nameof(Summary));
                NotifyChanged(nameof(Detail));
                NotifyChanged(nameof(FlowText));
            }

            var metadata = WalletActivityMetadataRules.Resolve(_updates, MarketAssetAddress, catalog);
            if (!string.IsNullOrWhiteSpace(metadata.VenueId))
            {
                VenueLabel = GetVenueLabel(metadata.VenueId);
                _venueIconSource = CreateVenueIconSource(metadata.VenueId);
                NotifyChanged(nameof(VenueLabel));
                NotifyChanged(nameof(VenueIconSource));
                NotifyChanged(nameof(VenueIconVisibility));
            }
            if (!string.IsNullOrWhiteSpace(metadata.TokenIconUri))
            {
                _tokenIconSource = new BitmapImage(new Uri(metadata.TokenIconUri));
                NotifyChanged(nameof(CoinIconSource));
            }
            _marketCapText = metadata.MarketCapUsd is double marketCap
                ? WalletActivityMetadataRules.FormatUsd(marketCap)
                : string.Empty;
            NotifyChanged(nameof(MarketCapText));
            NotifyChanged(nameof(MarketCapDisplayText));
            NotifyChanged(nameof(MarketCapVisibility));
        }

        private static string GetChainLabel(WalletActivityUpdate update)
        {
            return update.ChainNamespace == ChainNamespaces.Solana
                ? "Solana"
                : EvmChainDefinitions.Supported.FirstOrDefault(chain => chain.ChainId == update.ChainId)?.DisplayName
                  ?? update.ChainId;
        }

        private static string GetChainIconUri(WalletActivityUpdate update)
        {
            if (update.ChainNamespace == ChainNamespaces.Solana)
            {
                return "ms-appx:///Assets/Chains/solana.svg";
            }
            return update.ChainId switch
            {
                EvmChainDefinitions.EthereumMainnetChainId => "ms-appx:///Assets/Chains/ethereum.svg",
                EvmChainDefinitions.BaseMainnetChainId => "ms-appx:///Assets/Chains/base.svg",
                EvmChainDefinitions.BnbMainnetChainId => "ms-appx:///Assets/Chains/bnb-chain.svg",
                EvmChainDefinitions.RobinhoodMainnetChainId => "ms-appx:///Assets/Chains/robinhood-chain.svg",
                _ => "ms-appx:///Assets/Chains/ethereum.svg"
            };
        }

        private static SvgImageSource CreateSvgImageSource(string uri) =>
            new() { UriSource = new Uri(uri) };

        private static ImageSource? CreateVenueIconSource(string? venueId)
        {
            if (string.IsNullOrWhiteSpace(venueId))
            {
                return null;
            }
            var normalized = venueId.ToLowerInvariant();
            var localPath = normalized switch
            {
                "pumpswap" or "pumpbondingcurve" => "ms-appx:///Assets/Protocols/pump.svg",
                "meteora" or "meteoradammv1" or "meteoradammv2" or "meteoradlmm" =>
                    "ms-appx:///Assets/Protocols/meteora.svg",
                _ => null
            };
            if (localPath != null)
            {
                return CreateSvgImageSource(localPath);
            }
            var domain = normalized switch
            {
                "raydium" or "raydiumammv4" or "raydiumcpmm" or "raydiumclmm" => "raydium.io",
                "orca" or "orcawhirlpool" => "orca.so",
                "manifest" or "manifestorderbook" => "manifest.trade",
                "uniswap" or "uniswap-v2" or "uniswap-v3" or "uniswap-v4" => "app.uniswap.org",
                "aerodrome" or "aerodrome-classic" or "aerodrome-slipstream" => "aerodrome.finance",
                "pancakeswap" or "pancake-v2" or "pancake-v3" or "pancake-infinity-cl" or
                    "pancake-infinity-bin" => "pancakeswap.finance",
                "curve" => "curve.fi",
                "shibaswap" => "shibaswap.com",
                "pons-v2-curve" => "ponslaunchpad.com",
                _ => null
            };
            return domain == null
                ? null
                : new BitmapImage(new Uri($"https://www.google.com/s2/favicons?domain_url=https://{domain}&sz=64"));
        }

        private static string GetVenueLabel(string? venueId)
        {
            if (string.IsNullOrWhiteSpace(venueId))
            {
                return string.Empty;
            }
            return venueId.ToLowerInvariant() switch
            {
                "pumpswap" => "PumpSwap",
                "pumpbondingcurve" => "Pump bonding curve",
                "raydium" or "raydiumammv4" or "raydiumcpmm" or "raydiumclmm" => "Raydium",
                "meteora" or "meteoradammv1" or "meteoradammv2" or "meteoradlmm" => "Meteora",
                "orca" or "orcawhirlpool" => "Orca",
                "manifest" or "manifestorderbook" => "Manifest",
                "uniswap" or "uniswap-v2" or "uniswap-v3" or "uniswap-v4" => "Uniswap",
                "aerodrome" or "aerodrome-classic" or "aerodrome-slipstream" => "Aerodrome",
                "pancakeswap" or "pancake-v2" or "pancake-v3" or "pancake-infinity-cl" or
                    "pancake-infinity-bin" => "PancakeSwap",
                "curve" => "Curve",
                "shibaswap" => "ShibaSwap",
                "pons-v2-curve" => "Pons",
                _ => venueId
            };
        }

        private static string GetFlowText(WalletActivityPresentation presentation) => presentation.Kind switch
        {
            WalletActivityDisplayKind.Buy or WalletActivityDisplayKind.Sell or WalletActivityDisplayKind.Swap =>
                presentation.Detail,
            WalletActivityDisplayKind.TransferIn => $"Received {presentation.Detail}",
            WalletActivityDisplayKind.TransferOut => $"Sent {presentation.Detail}",
            _ => presentation.Summary
        };

        private static Color GetActionColor(WalletActivityDisplayKind kind) => kind switch
        {
            WalletActivityDisplayKind.Buy or WalletActivityDisplayKind.TransferIn => UiPalette.Success,
            WalletActivityDisplayKind.Sell or WalletActivityDisplayKind.TransferOut or WalletActivityDisplayKind.Failed => UiPalette.Error,
            WalletActivityDisplayKind.Swap => Color.FromArgb(255, 83, 179, 255),
            _ => UiPalette.Muted
        };

        private void NotifyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        private static string BuildToolTip(
            WalletActivityUpdate update,
            WalletActivityPresentation presentation,
            int eventCount)
        {
            return $"{presentation.Summary} · {update.WalletLabel} · {GetChainLabel(update)}"
                   + $"\nFlow: {presentation.Detail}"
                   + $"\nWallet: {update.WalletAddress}"
                   + $"\nTransaction: {update.TransactionId}"
                   + $"\nPosition: {update.ChainPosition}"
                   + $"\nConfirmation: {update.Confirmation}"
                   + $"\nGrouped events: {eventCount}"
                   + $"\nSource: {update.SourceId}";
        }
    }
}

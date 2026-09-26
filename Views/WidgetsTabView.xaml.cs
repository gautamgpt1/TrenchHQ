using TrenchHQ.Helpers;
using TrenchHQ.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Windows.System;

namespace TrenchHQ.Views
{
    internal sealed class SavedWidgetListItem(string id, string name)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
    }

    internal sealed class TrackedWalletEditorRow
    {
        internal TrackedWalletEditorRow(SavedTrackedWallet wallet)
        {
            Wallet = wallet;
            Icon = new SvgImageSource { UriSource = new Uri(wallet.ChainNamespace == ChainNamespaces.Solana
                ? "ms-appx:///Assets/Chains/solana.svg"
                : wallet.ChainId switch
                {
                    EvmChainDefinitions.EthereumMainnetChainId => "ms-appx:///Assets/Chains/ethereum.svg",
                    EvmChainDefinitions.BaseMainnetChainId => "ms-appx:///Assets/Chains/base.svg",
                    EvmChainDefinitions.BnbMainnetChainId => "ms-appx:///Assets/Chains/bnb-chain.svg",
                    EvmChainDefinitions.RobinhoodMainnetChainId => "ms-appx:///Assets/Chains/robinhood-chain.svg",
                    _ => "ms-appx:///Assets/Chains/ethereum.svg"
                }) };
        }

        internal SavedTrackedWallet Wallet { get; }
        public string Key => SavedWidgetCatalogRules.GetWalletKey(Wallet);
        public string Label => Wallet.Label;
        public string ChainLabel => SavedWidgetCatalogRules.GetWalletChainDisplayName(Wallet);
        public string Address => Wallet.Address;
        public SvgImageSource Icon { get; }
    }

    internal sealed record EvmChainPoolSearchResult(
        EvmChainDefinition Chain,
        PoolCatalogSearchResult? Catalog,
        OnChainPoolDiscoveryResult? Discovery,
        Exception? CatalogError,
        Exception? DiscoveryError);

    internal sealed record NamedTokenPoolValidation(
        TokenCatalogSearchEntry Token,
        string ChainNamespace,
        string ChainId,
        OnChainPoolDiscoveryResult? Discovery);

    internal sealed class OnChainPoolResultRow : INotifyPropertyChanged
    {
        private bool _isSelected;

        internal OnChainPoolResultRow(
            OnChainPoolDescriptor descriptor,
            string selectedMint,
            PoolCatalogEntry? catalogPool = null,
            PoolCatalogSearchResult? catalogResult = null)
        {
            Descriptor = descriptor;
            SelectedMint = selectedMint;
            CatalogPool = catalogPool;
            var selectedAsset = GetSelectedAsset(catalogPool, selectedMint);
            AssetName = !string.IsNullOrWhiteSpace(selectedAsset?.Name)
                ? selectedAsset.Name
                : catalogResult?.AssetName ?? string.Empty;
            AssetSymbol = !string.IsNullOrWhiteSpace(selectedAsset?.Symbol)
                ? selectedAsset.Symbol
                : !string.IsNullOrWhiteSpace(catalogResult?.AssetSymbol)
                    ? catalogResult.AssetSymbol
                : Short(selectedMint);
            IconUri = catalogPool?.IconUri ?? catalogResult?.IconUri;
            Icon = Uri.TryCreate(IconUri, UriKind.Absolute, out var iconUri)
                   && iconUri.Scheme == Uri.UriSchemeHttps
                ? new BitmapImage(iconUri)
                : null;
            QuoteSymbol = GetQuoteSymbol(descriptor, selectedMint, catalogPool);
            ChainIcon = CreateChainIcon(descriptor.PoolKey.ChainId);
            VenueIcon = null;
        }

        internal OnChainPoolDescriptor Descriptor { get; }
        internal string SelectedMint { get; }
        internal PoolCatalogEntry? CatalogPool { get; }
        internal string Key => SavedWidgetCatalogRules.GetInstrumentKey(new SavedTickerInstrument
        {
            Kind = TickerInstrumentTypes.OnChainPool,
            Pool = Descriptor
        });
        internal double? LiquidityUsd => CatalogPool?.LiquidityUsd;
        public string AssetName { get; }
        public string AssetSymbol { get; }
        public string AssetDisplayName => string.IsNullOrWhiteSpace(AssetName) ? AssetSymbol : AssetName;
        public string QuoteSymbol { get; }
        public string? IconUri { get; }
        public BitmapImage? Icon { get; }
        public SvgImageSource ChainIcon { get; }
        public ImageSource? VenueIcon { get; }
        public string SelectionDisplayLabel => $"{AssetSymbol} / {QuoteSymbol}";
        public string Title => ProtocolLabel(CatalogPool, Descriptor);
        public string TokenAddressText => Short(SelectedMint);
        public string MarketCap => FormatUsd(
            CatalogPool?.MarketCapUsd ?? CatalogPool?.FullyDilutedValuationUsd,
            "--");
        public string Liquidity => FormatUsd(CatalogPool?.LiquidityUsd, "--");
        public string Volume24h => FormatUsd(CatalogPool?.Volume24hUsd, "--");
        public string Age => FormatAge(CatalogPool?.CreatedAtUnixMs);
        public string Status => Descriptor.SupportStatus switch
        {
            OnChainSupportStatus.Supported => string.Empty,
            OnChainSupportStatus.TemporarilyUnavailable => Descriptor.SupportReason ?? string.Empty,
            _ => Descriptor.SupportReason ?? $"This {Title} pool is not supported for on-chain pricing yet."
        };
        public Visibility StatusVisibility => string.IsNullOrWhiteSpace(Status)
            ? Visibility.Collapsed
            : Visibility.Visible;
        public bool IsSelectable => Descriptor.SupportStatus == OnChainSupportStatus.Supported;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private static string QuoteLabel(string mint, string chainId)
        {
            if (chainId == EvmChainDefinitions.RobinhoodMainnetChainId
                && RobinhoodStockTokenCatalogClient.Current.TryGetCachedByAddress(mint, out var stockToken))
            {
                return stockToken.Symbol;
            }
            return mint.ToLowerInvariant() switch
            {
                "so11111111111111111111111111111111111111112" => "SOL",
                "0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2" => "ETH",
                "0x4200000000000000000000000000000000000006" => "ETH",
                "0x0bd7d308f8e1639fab988df18a8011f41eacad73" => "WETH",
                "0x0000000000000000000000000000000000000000" =>
                    chainId == EvmChainDefinitions.BnbMainnetChainId ? "BNB" : "ETH",
                "0xbb4cdb9cbd36b01bd1cbaebf2de08d9173bc095c" => "WBNB",
                "0xa0b86991c6218b36c1d19d4a2e9eb0ce3606eb48" => "USDC",
                "0x833589fcd6edb6e08f4c7c32d4f71b54bda02913" => "USDC",
                "0x8ac76a51cc950d9822d68b83fe1ad97b32cd580d" => "USDC",
                "0xdac17f958d2ee523a2206206994597c13d831ec7" => "USDT",
                "0x55d398326f99059ff775485246999027b3197955" => "USDT",
                "0x5fc5360d0400a0fd4f2af552add042d716f1d168" => "USDG",
                "0x6b175474e89094c44da98b954eedeac495271d0f" => "DAI",
                _ => Short(mint)
            };
        }

        private static string GetQuoteSymbol(
            OnChainPoolDescriptor descriptor,
            string selectedMint,
            PoolCatalogEntry? catalogPool)
        {
            if (catalogPool != null)
            {
                if (string.Equals(catalogPool.BaseAsset.Address, selectedMint, StringComparison.OrdinalIgnoreCase))
                {
                    return AssetLabel(catalogPool.QuoteAsset);
                }
                if (string.Equals(catalogPool.QuoteAsset.Address, selectedMint, StringComparison.OrdinalIgnoreCase))
                {
                    return AssetLabel(catalogPool.BaseAsset);
                }
            }
            var otherMint = string.Equals(descriptor.BaseMint, selectedMint, StringComparison.OrdinalIgnoreCase)
                ? descriptor.QuoteMint
                : descriptor.BaseMint;
            return QuoteLabel(otherMint, descriptor.PoolKey.ChainId);
        }

        private static string AssetLabel(PoolCatalogAsset asset)
        {
            return !string.IsNullOrWhiteSpace(asset.Symbol) ? asset.Symbol : Short(asset.Address);
        }

        private static PoolCatalogAsset? GetSelectedAsset(PoolCatalogEntry? pool, string selectedMint)
        {
            if (pool == null)
            {
                return null;
            }
            if (string.Equals(pool.BaseAsset.Address, selectedMint, StringComparison.OrdinalIgnoreCase))
            {
                return pool.BaseAsset;
            }
            return string.Equals(pool.QuoteAsset.Address, selectedMint, StringComparison.OrdinalIgnoreCase)
                ? pool.QuoteAsset
                : null;
        }

        private static SvgImageSource CreateChainIcon(string chainId)
        {
            var path = chainId switch
            {
                EvmChainDefinitions.BaseMainnetChainId => "ms-appx:///Assets/Chains/base.svg",
                EvmChainDefinitions.BnbMainnetChainId => "ms-appx:///Assets/Chains/bnb-chain.svg",
                EvmChainDefinitions.RobinhoodMainnetChainId => "ms-appx:///Assets/Chains/robinhood-chain.svg",
                "mainnet-beta" => "ms-appx:///Assets/Chains/solana.svg",
                _ => "ms-appx:///Assets/Chains/ethereum.svg"
            };
            return new SvgImageSource { UriSource = new Uri(path) };
        }

        internal static string ProtocolLabel(string protocolId)
        {
            return protocolId.ToLowerInvariant() switch
            {
                "pumpswap" => "PumpSwap",
                "pumpbondingcurve" => "Pump bonding curve",
                "raydium" => "Raydium",
                "raydiumammv4" => "Raydium AMM v4",
                "raydiumcpmm" => "Raydium CPMM",
                "raydiumclmm" => "Raydium CLMM",
                "meteora" => "Meteora",
                "meteoradammv1" => "Meteora DAMM v1",
                "meteoradammv2" => "Meteora DAMM v2",
                "meteoradlmm" => "Meteora DLMM",
                "orca" => "Orca",
                "orcawhirlpool" => "Orca Whirlpool",
                "manifest" => "Manifest",
                "manifestorderbook" => "Manifest order book",
                "uniswap-v2" => "Uniswap v2",
                "pancake-v2" => "PancakeSwap v2",
                "uniswap-v3" => "Uniswap v3",
                "uniswap-v4" => "Uniswap v4",
                "uniswap" => "Uniswap",
                "aerodrome-classic" => "Aerodrome classic",
                "aerodrome-slipstream" => "Aerodrome Slipstream",
                "aerodrome" => "Aerodrome",
                "pancake-v3" => "PancakeSwap v3",
                "pancake-infinity-cl" => "PancakeSwap Infinity CL",
                "pancake-infinity-bin" => "PancakeSwap Infinity bin",
                "pancakeswap-infinity-cl" => "PancakeSwap Infinity CL",
                "pancakeswap-infinity-bin" => "PancakeSwap Infinity bin",
                "pancakeswap" => "PancakeSwap",
                "curve" => "Curve",
                "fermi-swap" => "FermiSwap",
                "shibaswap" => "ShibaSwap v1",
                EthereumDeploymentRegistry.ShibaSwapV2Factory => "ShibaSwap v2",
                _ => protocolId
            };
        }

        private static string ProtocolLabel(
            PoolCatalogEntry? catalogPool,
            OnChainPoolDescriptor descriptor)
        {
            if (descriptor.DiscoverySource.StartsWith("canonical:long.xyz", StringComparison.Ordinal))
            {
                return "long.xyz (Uniswap v4)";
            }
            if (descriptor.DiscoverySource.StartsWith("canonical:pons-v1", StringComparison.Ordinal))
            {
                return "pons v1 (Uniswap v3)";
            }
            if (descriptor.DiscoverySource.StartsWith("canonical:pons-v2", StringComparison.Ordinal))
            {
                return descriptor.PoolKey.ProtocolId == OnChainProtocolIds.PonsV2Curve
                    ? "pons v2 bonding curve"
                    : "pons v2 (Uniswap v4)";
            }
            if (descriptor.PoolKey.ProtocolId == OnChainProtocolIds.AerodromeClassic)
            {
                return descriptor.PoolType == "stable"
                    ? "Aerodrome classic stable"
                    : "Aerodrome classic volatile";
            }
            if (descriptor.PoolKey.ProtocolId == OnChainProtocolIds.AerodromeSlipstream)
            {
                var generation = descriptor.ProgramId.ToLowerInvariant() switch
                {
                    BaseDeploymentRegistry.AerodromeSlipstreamInitialFactory => "Initial",
                    BaseDeploymentRegistry.AerodromeSlipstreamGaugeCapsFactory => "Gauge Caps",
                    BaseDeploymentRegistry.AerodromeSlipstreamGaugesV3Factory => "Gauges V3",
                    _ => "Unknown"
                };
                return $"Aerodrome Slipstream {generation}";
            }
            if (string.Equals(
                    descriptor.ProgramId,
                    EthereumDeploymentRegistry.ShibaSwapV1Factory,
                    StringComparison.OrdinalIgnoreCase))
            {
                return "ShibaSwap v1";
            }
            if (string.Equals(
                    descriptor.ProgramId,
                    EthereumDeploymentRegistry.ShibaSwapV2Factory,
                    StringComparison.OrdinalIgnoreCase))
            {
                return "ShibaSwap v2";
            }
            if (catalogPool == null || descriptor.SupportStatus == OnChainSupportStatus.Supported)
            {
                return ProtocolLabel(descriptor.PoolKey.ProtocolId);
            }
            if (string.Equals(catalogPool.ProtocolId, "uniswap", StringComparison.OrdinalIgnoreCase))
            {
                var version = catalogPool.Labels.FirstOrDefault(static label =>
                    label.Equals("v2", StringComparison.OrdinalIgnoreCase)
                    || label.Equals("v3", StringComparison.OrdinalIgnoreCase)
                    || label.Equals("v4", StringComparison.OrdinalIgnoreCase));
                return version == null ? "Uniswap" : $"Uniswap {version.ToLowerInvariant()}";
            }
            if (string.Equals(catalogPool.ProtocolId, "pancakeswap", StringComparison.OrdinalIgnoreCase))
            {
                var version = catalogPool.Labels.FirstOrDefault(static label =>
                    label.Equals("v2", StringComparison.OrdinalIgnoreCase)
                    || label.Equals("v3", StringComparison.OrdinalIgnoreCase));
                return version == null ? "PancakeSwap" : $"PancakeSwap {version.ToLowerInvariant()}";
            }
            return ProtocolLabel(catalogPool.ProtocolId);
        }

        private static string FormatUsd(double? value, string unavailable)
        {
            return value is double number ? FormatUsd(number) : unavailable;
        }

        private static string FormatUsd(double value)
        {
            return value switch
            {
                >= 1_000_000_000 => $"${value / 1_000_000_000:0.##}B",
                >= 1_000_000 => $"${value / 1_000_000:0.##}M",
                >= 1_000 => $"${value / 1_000:0.##}K",
                _ => $"${value:0.##}"
            };
        }

        private static string FormatAge(long? createdAtUnixMs)
        {
            if (createdAtUnixMs is not long value)
            {
                return "--";
            }
            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(value);
            if (age < TimeSpan.Zero)
            {
                return "<1m";
            }
            return age.TotalDays >= 1
                ? $"{(int)age.TotalDays}d"
                : age.TotalHours >= 1
                    ? $"{(int)age.TotalHours}h"
                    : $"{Math.Max(1, (int)age.TotalMinutes)}m";
        }

        internal static string Short(string value)
        {
            return value.Length <= 12 ? value : $"{value[..6]}…{value[^4..]}";
        }
    }

    public sealed partial class WidgetsTabView : UserControl
    {
        private SavedWidgetCatalogService? _catalog;
        private readonly ObservableCollection<SavedWidgetListItem> _widgetItems = [];
        private readonly List<MarketRow> _allMarketRows = [];
        private readonly ObservableCollection<MarketRow> _filteredMarketRows = [];
        private readonly ObservableCollection<OnChainPoolResultRow> _poolRows = [];
        private readonly ObservableCollection<TrackedWalletEditorRow> _walletRows = [];
        private SavedWidgetDefinition? _selectedWidget;
        private SavedWidgetListItem? _selectedListItem;
        private bool _initialized;
        private bool _isApplying;
        private bool _suppressDirtyEvents;
        private bool _isDirty;
        private bool _isShowingOnChainResults;
        private int _tokenSearchVersion;
        private string _currentFilter = string.Empty;
        private string _displayedOnChainMint = string.Empty;

        public WidgetsTabView()
        {
            InitializeComponent();
            WidgetList.ItemsSource = _widgetItems;
            MarketsListView.ItemsSource = _filteredMarketRows;
            OnChainPoolsListView.ItemsSource = _poolRows;
            TrackedWalletsListView.ItemsSource = _walletRows;
            SetActionButtonsDirty(false);
        }

        private SavedWidgetCatalogService Catalog => _catalog
                                                     ?? throw new InvalidOperationException("Widget catalog is unavailable.");

        internal async Task ActivateAsync()
        {
            if (_initialized)
            {
                HideProviderRequiredMessageWhenConfigured();
                return;
            }

            if (Application.Current is not App app)
            {
                return;
            }

            _initialized = true;
            _catalog = app.WidgetCatalog;
            await app.EnsureInitializedAsync();
            await Catalog.InitializeAsync();
            RefreshWidgets();
            await LoadMarketRowsAsync();
        }

        internal async Task OpenWidgetAsync(string widgetId)
        {
            await ActivateAsync();
            var target = _widgetItems.FirstOrDefault(item =>
                string.Equals(item.Id, widgetId, StringComparison.OrdinalIgnoreCase));
            if (target == null || ReferenceEquals(target, _selectedListItem))
            {
                return;
            }

            if (!await ConfirmDiscardChangesAsync())
            {
                return;
            }

            SetSelectedListItem(target);
            ApplyWidget(target);
        }

        private async void OnAddPriceTickerClick(object sender, RoutedEventArgs e)
        {
            if (!await ConfirmDiscardChangesAsync())
            {
                return;
            }

            try
            {
                var created = await Catalog.AddPriceTickerAsync();
                RefreshWidgets(created.Id);
            }
            catch (Exception exception)
            {
                await ShowMessageAsync("Widget could not be created", exception.Message);
            }
        }

        private async void OnAddWalletActivityClick(object sender, RoutedEventArgs e)
        {
            if (!await ConfirmDiscardChangesAsync())
            {
                return;
            }

            try
            {
                var created = await Catalog.AddWalletActivityAsync();
                RefreshWidgets(created.Id);
            }
            catch (Exception exception)
            {
                await ShowMessageAsync("Widget could not be created", exception.Message);
            }
        }

        private async void OnAddXTimelineClick(object sender, RoutedEventArgs e)
        {
            if (!await ConfirmDiscardChangesAsync())
            {
                return;
            }

            try
            {
                var created = await Catalog.AddXTimelineAsync();
                RefreshWidgets(created.Id);
            }
            catch (Exception exception)
            {
                await ShowMessageAsync("Widget could not be created", exception.Message);
            }
        }

        private async void OnAddWebsiteClick(object sender, RoutedEventArgs e)
        {
            if (!await ConfirmDiscardChangesAsync()) return;
            try { var created = await Catalog.AddWebsiteAsync(); RefreshWidgets(created.Id); }
            catch (Exception exception) { await ShowMessageAsync("Widget could not be created", exception.Message); }
        }

        private void OnWebsiteEdited(object sender, TextChangedEventArgs e) => WebsiteEdited();
        private void OnWebsiteNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => WebsiteEdited();
        private void WebsiteEdited()
        {
            if (_isApplying || _selectedWidget?.Type != PanelWidgetTypes.Website) return;
            WebsiteStatusText.Text = "";
            SetDirty(true);
        }

        private async void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            if (_selectedWidget == null)
            {
                return;
            }

            if (Application.Current is App app && app.PanelManager.GetWidgetUsageCount(_selectedWidget.Id) > 0)
            {
                await ShowMessageAsync(
                    "Widget is used by Desktop Setup",
                    "Assign a different widget to every panel using this widget before deleting it.");
                return;
            }

            if (await ShowConfirmationAsync(
                    "Delete this widget?",
                    $"Delete {_selectedWidget.Name}? This cannot be undone.",
                    "Delete",
                    "Cancel") != ContentDialogResult.Primary)
            {
                return;
            }

            try
            {
                await Catalog.DeleteAsync(_selectedWidget.Id);
                RefreshWidgets();
            }
            catch (Exception exception)
            {
                await ShowMessageAsync("Widget could not be deleted", exception.Message);
            }
        }

        private async void OnWidgetSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isApplying || WidgetList.SelectedItem is not SavedWidgetListItem target)
            {
                return;
            }

            if (_selectedListItem != null
                && !string.Equals(_selectedListItem.Id, target.Id, StringComparison.OrdinalIgnoreCase)
                && !await ConfirmDiscardChangesAsync())
            {
                SetSelectedListItem(_selectedListItem);
                return;
            }

            ApplyWidget(target);
        }

        private async void OnSaveClick(object sender, RoutedEventArgs e)
        {
            if (_selectedWidget == null || !_isDirty)
            {
                return;
            }

            if (_selectedWidget.Type == PanelWidgetTypes.XTimeline)
            {
                if (!SocialFeedRules.TryHandles(XTimelineUrlBox.Text, out var handles, out var error))
                {
                    XTimelineEditorStatusText.Text = error;
                    return;
                }
                _selectedWidget.XTimelineUrl = null;
                _selectedWidget.XHandles = handles;
                _selectedWidget.XTextFilter = XTextFilterBox.Text.Trim();
                _selectedWidget.XIncludePosts = XPostsCheck.IsChecked == true;
                _selectedWidget.XIncludeReplies = XRepliesCheck.IsChecked == true;
                _selectedWidget.XIncludeReposts = XRepostsCheck.IsChecked == true;
                _selectedWidget.XIncludeQuotes = XQuotesCheck.IsChecked == true;
                _selectedWidget.XExternalContentConsent = XExternalContentConsentToggle.IsOn;
                _selectedWidget.Instruments = [];
                _selectedWidget.Wallets = [];
            }
            else if (_selectedWidget.Type == PanelWidgetTypes.Website)
            {
                if (!WebsiteWidgetRules.TrySaveUrl(WebsiteUrlBox.Text, out var url, out var error))
                {
                    WebsiteStatusText.Text = error;
                    SaveStatusText.Text = error;
                    WebsiteUrlBox.Focus(FocusState.Programmatic);
                    return;
                }
                _selectedWidget.WebsiteUrl = url;
                _selectedWidget.WebsiteEnabled = url.Length > 0;
                _selectedWidget.WebsiteZoom = WebsiteWidgetRules.Zoom(WebsiteZoomBox.Value / 100);
            }
            else if (_selectedWidget.Type == PanelWidgetTypes.WalletActivity)
            {
                _selectedWidget.Wallets = GetTrackedWallets();
                _selectedWidget.Instruments = [];
            }
            else
            {
                var mode = (OnChainPriceModeBox.SelectedItem as ComboBoxItem)?.Tag as string;
                if (mode == OnChainPriceModes.OneSecondPolling
                    && GetSelectedInstruments().Any(instrument =>
                        instrument.Kind == TickerInstrumentTypes.OnChainPool
                        && instrument.Pool != null
                        && !OnChainPriceModes.SupportsPolling(instrument.Pool)))
                {
                    SaveStatusText.Text = "This pool has execution prices only. Choose WebSocket to watch it.";
                    return;
                }
                _selectedWidget.OnChainPriceMode = OnChainPriceModes.Normalize(mode);
                _selectedWidget.Instruments = GetSelectedInstruments();
                _selectedWidget.Wallets = [];
            }
            try
            {
                if (!await Catalog.SaveAsync(_selectedWidget))
                {
                    await ShowMessageAsync("Widget could not be saved", "The saved widget no longer exists.");
                    RefreshWidgets();
                    return;
                }

                var selectedId = _selectedWidget.Id;
                RefreshWidgets(selectedId);
                SaveStatusText.Text = "Saved";
                if (_selectedWidget?.Type == PanelWidgetTypes.Website && _selectedWidget.WebsiteUrl.Length == 0)
                    SaveStatusText.Text = "Saved. Add a website address when you're ready.";
            }
            catch (Exception exception)
            {
                await ShowMessageAsync("Widget could not be saved", exception.Message);
            }
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            if (_selectedWidget == null)
            {
                return;
            }

            var stored = Catalog.Find(_selectedWidget.Id);
            if (stored == null)
            {
                RefreshWidgets();
                return;
            }

            _selectedWidget = stored;
            _suppressDirtyEvents = true;
            ApplySelectedWidgetData(stored);
            SetDirty(false);
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    SetDirty(false);
                    _suppressDirtyEvents = false;
                });
        }

        private void OnWalletAddressKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter)
            {
                return;
            }
            e.Handled = true;
            AddTrackedWallet();
        }

        private void OnAddTrackedWalletClick(object sender, RoutedEventArgs e)
        {
            AddTrackedWallet();
        }

        private void OnXTimelineTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isApplying || _selectedWidget?.Type != PanelWidgetTypes.XTimeline)
            {
                return;
            }
            XTimelineEditorStatusText.Text = string.Empty;
            SetDirty(true);
        }

        private void OnXExternalContentConsentToggled(object sender, RoutedEventArgs e)
        {
            if (_isApplying || _selectedWidget?.Type != PanelWidgetTypes.XTimeline)
            {
                return;
            }
            SetDirty(true);
        }

        private async void OnOpenXPolicyClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string value }
                && Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                await Launcher.LaunchUriAsync(uri);
            }
        }

        private void AddTrackedWallet()
        {
            if (_selectedWidget?.Type != PanelWidgetTypes.WalletActivity
                || WalletChainBox.SelectedItem is not ComboBoxItem chainItem
                || chainItem.Tag is not string chainKey)
            {
                return;
            }
            if (_walletRows.Count >= SavedWidgetCatalogRules.MaximumWalletsPerWidget)
            {
                WalletEditorStatusText.Text = $"A Wallet Watcher can track up to {SavedWidgetCatalogRules.MaximumWalletsPerWidget} addresses.";
                return;
            }

            var parts = chainKey.Split('|', 2);
            var normalized = parts.Length == 2
                ? SavedWidgetCatalogRules.NormalizeWallet(new SavedTrackedWallet
                {
                    ChainNamespace = parts[0],
                    ChainId = parts[1],
                    Address = WalletAddressBox.Text,
                    Label = WalletLabelBox.Text
                })
                : null;
            if (normalized == null)
            {
                WalletEditorStatusText.Text = "Enter a valid public address for the selected chain.";
                return;
            }
            var key = SavedWidgetCatalogRules.GetWalletKey(normalized);
            if (_walletRows.Any(row => string.Equals(row.Key, key, StringComparison.OrdinalIgnoreCase)))
            {
                WalletEditorStatusText.Text = "That address is already tracked on this chain.";
                return;
            }

            _walletRows.Add(new TrackedWalletEditorRow(normalized));
            WalletAddressBox.Text = string.Empty;
            WalletLabelBox.Text = string.Empty;
            WalletEditorStatusText.Text = "Wallet added. Save the widget to start watching it.";
            SetDirty(true);
        }

        private void OnRemoveTrackedWalletClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string key })
            {
                return;
            }
            var row = _walletRows.FirstOrDefault(item =>
                string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
            if (row == null)
            {
                return;
            }
            _walletRows.Remove(row);
            WalletEditorStatusText.Text = _walletRows.Count == 0
                ? "Add at least one public address to receive wallet activity."
                : string.Empty;
            SetDirty(true);
        }

        private void OnMarketSelectionClick(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox checkBox
                || checkBox.Tag is not string pair
                || _selectedWidget == null)
            {
                return;
            }

            var row = _allMarketRows.FirstOrDefault(item =>
                string.Equals(item.TagValue, pair, StringComparison.OrdinalIgnoreCase));
            if (row == null)
            {
                return;
            }
            SetInstrumentSelected(
                new SavedTickerInstrument
                {
                    Kind = TickerInstrumentTypes.CentralizedMarket,
                    DisplayLabel = row.Symbol,
                    VenueId = row.VenueId,
                    Symbol = row.Symbol
                },
                checkBox.IsChecked == true);
        }

        private void OnRemoveInstrumentClick(object sender, RoutedEventArgs e)
        {
            var instrument = (sender as FrameworkElement)?.Tag as SavedTickerInstrument;
            if (_selectedWidget == null || instrument == null)
            {
                return;
            }

            SetInstrumentSelected(instrument, false);
        }

        private async void OnInstrumentSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            var nextFilter = InstrumentSearchBox?.Text?.Trim() ?? string.Empty;
            if (!string.Equals(nextFilter, _currentFilter, StringComparison.OrdinalIgnoreCase))
            {
                _tokenSearchVersion++;
                if (!InstrumentSearchButton.IsEnabled)
                {
                    InstrumentSearchButton.IsEnabled = true;
                    PoolSearchProgress.IsActive = false;
                    PoolSearchProgress.Visibility = Visibility.Collapsed;
                }
            }
            _currentFilter = nextFilter;
            if (EvmAddress.TryNormalize(_currentFilter, out var ethereumAddress))
            {
                if (InstrumentSearchButton.IsEnabled
                    && !string.Equals(
                        ethereumAddress,
                        _displayedOnChainMint,
                        StringComparison.OrdinalIgnoreCase))
                {
                    await SearchEvmPoolsAsync(ethereumAddress);
                }
                return;
            }
            if (!string.Equals(
                    _currentFilter,
                    _displayedOnChainMint,
                    StringComparison.OrdinalIgnoreCase))
            {
                ShowExchangeResults();
            }
            ApplyMarketFilter();
        }

        private async void OnInstrumentSearchKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter)
            {
                return;
            }
            e.Handled = true;
            await SearchCurrentQueryAsync();
        }

        private async void OnInstrumentSearchClick(object sender, RoutedEventArgs e)
        {
            await SearchCurrentQueryAsync();
        }

        private void OnManageProvidersClick(object sender, RoutedEventArgs e)
        {
            MainWindow.Current?.OpenProviders();
        }

        private async Task SearchCurrentQueryAsync()
        {
            if (!InstrumentSearchButton.IsEnabled)
            {
                return;
            }
            var query = InstrumentSearchBox.Text.Trim();
            _currentFilter = query;
            if (IsSolanaAddress(query))
            {
                await SearchSolanaPoolsAsync(query);
                return;
            }
            if (EvmAddress.TryNormalize(query, out var ethereumAddress))
            {
                await SearchEvmPoolsAsync(ethereumAddress);
                return;
            }
            if (query.Length == 0)
            {
                ShowExchangeResults();
                ApplyMarketFilter();
                return;
            }

            await SearchTokenPoolsAsync(query);
        }

        private async Task SearchTokenPoolsAsync(string query)
        {
            var searchVersion = ++_tokenSearchVersion;
            _displayedOnChainMint = query;
            ShowOnChainResults();
            InstrumentSearchButton.IsEnabled = false;
            PoolSearchProgress.IsActive = true;
            PoolSearchProgress.Visibility = Visibility.Visible;
            SetPoolSearchStatus(null);
            _poolRows.Clear();
            UpdateOnChainEmptyState();

            try
            {
                var result = await DexScreenerPoolCatalogClient.Current.SearchTokensAsync(query);
                if (searchVersion != _tokenSearchVersion
                    || !string.Equals(InstrumentSearchBox.Text.Trim(), query, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                if (Application.Current is not App app)
                {
                    throw new InvalidOperationException("The application provider service is unavailable.");
                }
                if (result.Tokens.Length == 0)
                {
                    SetPoolSearchStatus("No pools found.");
                    return;
                }

                using var validationGate = new System.Threading.SemaphoreSlim(3);
                var pendingValidations = result.Tokens.Select(async token =>
                {
                    await validationGate.WaitAsync();
                    try
                    {
                        return await ValidateNamedTokenAsync(token, app);
                    }
                    finally
                    {
                        validationGate.Release();
                    }
                }).ToList();
                while (pendingValidations.Count > 0)
                {
                    var completed = await Task.WhenAny(pendingValidations);
                    pendingValidations.Remove(completed);
                    var validation = await completed;
                    if (searchVersion != _tokenSearchVersion)
                    {
                        return;
                    }

                    PopulateValidatedTokenPoolRows(validation);
                    UpdateOnChainEmptyState();
                }
                SetPoolSearchStatus(_poolRows.Count == 0 ? "No supported pools found." : null);
            }
            catch
            {
                if (searchVersion == _tokenSearchVersion)
                {
                    SetPoolSearchStatus("Pool search is unavailable. Try again.");
                }
            }
            finally
            {
                if (searchVersion == _tokenSearchVersion)
                {
                    InstrumentSearchButton.IsEnabled = true;
                    PoolSearchProgress.IsActive = false;
                    PoolSearchProgress.Visibility = Visibility.Collapsed;
                    UpdateOnChainEmptyState();
                }
            }
        }

        private async Task SearchSolanaPoolsAsync(string mint)
        {
            _displayedOnChainMint = mint;
            ShowOnChainResults();
            ProviderRequiredPanel.Visibility = Visibility.Collapsed;
            InstrumentSearchButton.IsEnabled = false;
            PoolSearchProgress.IsActive = true;
            PoolSearchProgress.Visibility = Visibility.Visible;
            SetPoolSearchStatus(null);
            _poolRows.Clear();
            UpdateOnChainEmptyState();
            PoolCatalogSearchResult? catalogResult = null;
            OnChainPoolDiscoveryResult? discoveryResult = null;
            Exception? catalogError = null;
            Exception? discoveryError = null;
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var discovery = new OnChainPoolDiscoveryService(
                new SolanaRpcClient(httpClient, OnChainPoolDiscoveryService.PublicMainnetRpcEndpoint),
                OnChainEngineClient.Current);
            var catalogTask = DexScreenerPoolCatalogClient.Current.SearchAsync("solana", mint);
            var meteoraV1CatalogTask = MeteoraDammV1PoolCatalogClient.Current.SearchAsync(mint);
            var meteoraCatalogTask = MeteoraDammV2PoolCatalogClient.Current.SearchAsync(mint);
            var manifestCatalogTask = ManifestPoolCatalogClient.Current.SearchAsync(mint);
            try
            {
                catalogResult = await catalogTask;
                PopulatePoolRows(
                    ChainNamespaces.Solana,
                    "mainnet-beta",
                    mint,
                    catalogResult,
                    null,
                    "On-chain validation is still in progress.");
                SetPoolSearchStatus(null);
                UpdateOnChainEmptyState();
            }
            catch (Exception exception)
            {
                catalogError = exception;
            }
            try
            {
                var meteoraV1Catalog = await meteoraV1CatalogTask;
                catalogResult = MergeCatalogResults(catalogResult, meteoraV1Catalog, mint);
            }
            catch (Exception exception)
            {
                catalogError = catalogError == null
                    ? exception
                    : new AggregateException(catalogError, exception);
            }
            try
            {
                var meteoraCatalog = await meteoraCatalogTask;
                catalogResult = MergeCatalogResults(catalogResult, meteoraCatalog, mint);
            }
            catch (Exception exception)
            {
                catalogError = catalogError == null
                    ? exception
                    : new AggregateException(catalogError, exception);
            }
            try
            {
                var manifestCatalog = await manifestCatalogTask;
                catalogResult = MergeCatalogResults(catalogResult, manifestCatalog, mint);
            }
            catch (Exception exception)
            {
                catalogError = catalogError == null
                    ? exception
                    : new AggregateException(catalogError, exception);
            }
            try
            {
                discoveryResult = await discovery.DiscoverAsync(
                    mint,
                    OnChainCommitment.Confirmed,
                    catalogResult?.Pools.Select(static pool => pool.PoolAddress).ToArray());
            }
            catch (Exception exception)
            {
                discoveryError = exception;
            }
            try
            {
                PopulatePoolRows(
                    ChainNamespaces.Solana,
                    "mainnet-beta",
                    mint,
                    catalogResult,
                    discoveryResult,
                    discoveryResult == null
                        ? discoveryError?.Message ?? "On-chain validation is unavailable."
                        : null);
                SetPoolSearchStatus(BuildPoolSearchStatus(
                    catalogResult,
                    discoveryResult,
                    catalogError,
                    discoveryError));
            }
            catch (Exception)
            {
                SetPoolSearchStatus("Pool results could not be displayed. Try again.");
            }
            finally
            {
                InstrumentSearchButton.IsEnabled = true;
                PoolSearchProgress.IsActive = false;
                PoolSearchProgress.Visibility = Visibility.Collapsed;
                UpdateOnChainEmptyState();
            }
        }

        private static async Task<NamedTokenPoolValidation> ValidateNamedTokenAsync(
            TokenCatalogSearchEntry token,
            App app)
        {
            var chainNamespace = ChainNamespaceForCatalog(token.ChainId);
            var chainId = ChainIdForCatalog(token.ChainId);
            using var validationTimeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(12));
            try
            {
                if (string.Equals(token.ChainId, "solana", StringComparison.OrdinalIgnoreCase))
                {
                    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
                    var discovery = await new OnChainPoolDiscoveryService(
                            new SolanaRpcClient(httpClient, OnChainPoolDiscoveryService.PublicMainnetRpcEndpoint),
                            OnChainEngineClient.Current)
                        .DiscoverAsync(
                            token.Address,
                            OnChainCommitment.Confirmed,
                            token.Pools.Select(static pool => pool.PoolAddress).ToArray(),
                            validationTimeout.Token,
                            includeDerivedDiscovery: false);
                    return new NamedTokenPoolValidation(token, chainNamespace, chainId, discovery);
                }

                var chain = EvmChainDefinitions.Supported.Single(definition =>
                    string.Equals(definition.CatalogChainId, token.ChainId, StringComparison.OrdinalIgnoreCase));
                var deployments = chain.ChainId switch
                {
                    EvmChainDefinitions.EthereumMainnetChainId => EthereumDeploymentRegistry.Catalog,
                    EvmChainDefinitions.BaseMainnetChainId => BaseDeploymentRegistry.Catalog,
                    EvmChainDefinitions.BnbMainnetChainId => BnbDeploymentRegistry.Catalog,
                    EvmChainDefinitions.RobinhoodMainnetChainId => RobinhoodDeploymentRegistry.Catalog,
                    _ => throw new InvalidOperationException("The chain is not supported.")
                };
                var configuration = app.OnChainProviders.GetSelectedConfiguration(
                    chain.ChainNamespace,
                    chain.ChainId);
                string credential;
                if (configuration == null)
                {
                    var fallbackType = chain.ChainId switch
                    {
                        EvmChainDefinitions.BaseMainnetChainId => OnChainProviderTypes.BasePublic,
                        EvmChainDefinitions.BnbMainnetChainId => OnChainProviderTypes.PublicNodeBnb,
                        EvmChainDefinitions.RobinhoodMainnetChainId => OnChainProviderTypes.CustomRobinhood,
                        _ => OnChainProviderTypes.PublicNodeEthereum
                    };
                    configuration = OnChainProviderConfigurationStore.CreateConfiguration(
                        OnChainProviderCatalog.Get(fallbackType));
                    if (chain.ChainId == EvmChainDefinitions.RobinhoodMainnetChainId)
                    {
                        configuration.RpcEndpoint = RobinhoodDeploymentRegistry.PublicRpcEndpoint;
                    }
                    credential = string.Empty;
                }
                else
                {
                    credential = await app.OnChainProviders.GetCredentialAsync(configuration);
                }

                using var evmHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
                var evmDiscovery = await new EthereumPoolDiscoveryService(
                        new EvmJsonRpcClient(evmHttpClient, configuration, credential),
                        chain,
                        deployments,
                        evmHttpClient)
                    .DiscoverCatalogPoolsSharedAsync(token.Address, token.Pools, validationTimeout.Token);
                return new NamedTokenPoolValidation(token, chainNamespace, chainId, evmDiscovery);
            }
            catch
            {
                return new NamedTokenPoolValidation(token, chainNamespace, chainId, null);
            }
        }

        private static PoolCatalogSearchResult CreateCatalogResult(TokenCatalogSearchEntry token)
        {
            return new PoolCatalogSearchResult
            {
                SourceId = "dexscreener",
                ChainId = token.ChainId,
                AssetAddress = token.Address,
                AssetName = token.Name,
                AssetSymbol = token.Symbol,
                IconUri = token.IconUri,
                Pools = token.Pools
            };
        }

        private void PopulateValidatedTokenPoolRows(NamedTokenPoolValidation validation)
        {
            var supportedPools = validation.Discovery?.Pools
                .Where(static pool => pool.SupportStatus == OnChainSupportStatus.Supported)
                .ToArray() ?? [];
            if (supportedPools.Length == 0)
            {
                return;
            }

            var comparer = string.Equals(validation.ChainNamespace, ChainNamespaces.Eip155, StringComparison.Ordinal)
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            var supportedAddresses = supportedPools
                .Select(static pool => pool.PoolKey.PoolAddress)
                .ToHashSet(comparer);
            var catalog = CreateCatalogResult(validation.Token);
            catalog.Pools = catalog.Pools
                .Where(pool => supportedAddresses.Contains(pool.PoolAddress))
                .ToArray();
            var discovery = new OnChainPoolDiscoveryResult
            {
                Mint = validation.Discovery!.Mint,
                Pools = supportedPools,
                Truncated = validation.Discovery.Truncated,
                Warnings = validation.Discovery.Warnings
            };
            PopulatePoolRows(
                validation.ChainNamespace,
                validation.ChainId,
                validation.Token.Address,
                catalog,
                discovery,
                null,
                append: true);
        }

        private static string ChainNamespaceForCatalog(string catalogChainId)
        {
            return string.Equals(catalogChainId, "solana", StringComparison.OrdinalIgnoreCase)
                ? ChainNamespaces.Solana
                : ChainNamespaces.Eip155;
        }

        private static string ChainIdForCatalog(string catalogChainId)
        {
            if (string.Equals(catalogChainId, "solana", StringComparison.OrdinalIgnoreCase))
            {
                return "mainnet-beta";
            }
            return EvmChainDefinitions.Supported.Single(definition =>
                    string.Equals(definition.CatalogChainId, catalogChainId, StringComparison.OrdinalIgnoreCase))
                .ChainId;
        }

        private async Task SearchEvmPoolsAsync(string tokenAddress)
        {
            _displayedOnChainMint = tokenAddress;
            ShowOnChainResults();
            ProviderRequiredPanel.Visibility = Visibility.Collapsed;
            InstrumentSearchButton.IsEnabled = false;
            PoolSearchProgress.IsActive = true;
            PoolSearchProgress.Visibility = Visibility.Visible;
            SetPoolSearchStatus(null);
            _poolRows.Clear();
            UpdateOnChainEmptyState();
            try
            {
                if (Application.Current is not App app)
                {
                    throw new InvalidOperationException("The application provider service is unavailable.");
                }
                var results = await Task.WhenAll(
                    SearchEvmChainAsync(
                        tokenAddress,
                        EvmChainDefinitions.EthereumMainnet,
                        EthereumDeploymentRegistry.Catalog,
                        app),
                    SearchEvmChainAsync(
                        tokenAddress,
                        EvmChainDefinitions.BaseMainnet,
                        BaseDeploymentRegistry.Catalog,
                        app),
                    SearchEvmChainAsync(
                        tokenAddress,
                        EvmChainDefinitions.BnbMainnet,
                        BnbDeploymentRegistry.Catalog,
                        app),
                    SearchEvmChainAsync(
                        tokenAddress,
                        EvmChainDefinitions.RobinhoodMainnet,
                        RobinhoodDeploymentRegistry.Catalog,
                        app));
                foreach (var result in results)
                {
                    PopulatePoolRows(
                        result.Chain.ChainNamespace,
                        result.Chain.ChainId,
                        tokenAddress,
                        result.Catalog,
                        result.Discovery,
                        result.Discovery == null
                            ? result.DiscoveryError?.Message ?? "On-chain validation is unavailable."
                            : null,
                        append: true);
                }
                SetPoolSearchStatus(_poolRows.Count == 0 ? "No supported pools found." : null);
            }
            catch (Exception)
            {
                SetPoolSearchStatus("Pool results could not be displayed. Try again.");
            }
            finally
            {
                InstrumentSearchButton.IsEnabled = true;
                PoolSearchProgress.IsActive = false;
                PoolSearchProgress.Visibility = Visibility.Collapsed;
                UpdateOnChainEmptyState();
            }
        }

        private static async Task<EvmChainPoolSearchResult> SearchEvmChainAsync(
            string tokenAddress,
            EvmChainDefinition chain,
            EvmDeploymentCatalog deployments,
            App app)
        {
            PoolCatalogSearchResult? catalog = null;
            OnChainPoolDiscoveryResult? discovery = null;
            Exception? catalogError = null;
            Exception? discoveryError = null;
            var catalogTask = DexScreenerPoolCatalogClient.Current.SearchAsync(
                chain.CatalogChainId,
                tokenAddress);
            try
            {
                catalog = await catalogTask;
            }
            catch (Exception exception)
            {
                catalogError = exception;
            }
            if (chain.ChainId is EvmChainDefinitions.BnbMainnetChainId
                or EvmChainDefinitions.BaseMainnetChainId)
            {
                try
                {
                    var infinity = await PancakeInfinityPoolCatalogClient.Current.SearchAsync(
                        deployments,
                        tokenAddress);
                    catalog = MergeCatalogResults(catalog, infinity, tokenAddress);
                }
                catch (Exception exception)
                {
                    catalogError = catalogError == null
                        ? exception
                        : new AggregateException(catalogError, exception);
                }
            }
            if (chain.ChainId == EvmChainDefinitions.EthereumMainnetChainId)
            {
                try
                {
                    var curve = await CurvePoolCatalogClient.Current.SearchAsync(tokenAddress);
                    catalog = MergeCatalogResults(catalog, curve, tokenAddress);
                }
                catch (Exception exception)
                {
                    catalogError = catalogError == null
                        ? exception
                        : new AggregateException(catalogError, exception);
                }
            }
            if (chain.ChainId == EvmChainDefinitions.RobinhoodMainnetChainId)
            {
                try
                {
                    var stockToken = await RobinhoodStockTokenCatalogClient.Current.FindByAddressAsync(tokenAddress);
                    if (stockToken != null)
                    {
                        catalog ??= new PoolCatalogSearchResult
                        {
                            ChainId = chain.CatalogChainId,
                            AssetAddress = tokenAddress,
                            Pools = []
                        };
                        catalog.SourceId = string.IsNullOrWhiteSpace(catalog.SourceId)
                            ? "robinhood-stock-token-api"
                            : $"{catalog.SourceId}+robinhood-stock-token-api";
                        catalog.AssetName = stockToken.Name;
                        catalog.AssetSymbol = stockToken.Symbol;
                        catalog.IconUri = stockToken.LogoUri;
                    }
                }
                catch
                {
                    // Exact-address pool discovery remains available if metadata is rate limited.
                }
            }

            if (catalog is not { Pools.Length: > 0 })
            {
                return new EvmChainPoolSearchResult(
                    chain,
                    catalog,
                    new OnChainPoolDiscoveryResult { Mint = tokenAddress },
                    catalogError,
                    null);
            }

            try
            {
                var configuration = app.OnChainProviders.GetSelectedConfiguration(
                    chain.ChainNamespace,
                    chain.ChainId);
                string credential;
                if (configuration == null)
                {
                    var fallbackType = chain.ChainId switch
                    {
                        EvmChainDefinitions.BaseMainnetChainId => OnChainProviderTypes.BasePublic,
                        EvmChainDefinitions.BnbMainnetChainId => OnChainProviderTypes.PublicNodeBnb,
                        EvmChainDefinitions.RobinhoodMainnetChainId => OnChainProviderTypes.CustomRobinhood,
                        _ => OnChainProviderTypes.PublicNodeEthereum
                    };
                    configuration = OnChainProviderConfigurationStore.CreateConfiguration(
                        OnChainProviderCatalog.Get(fallbackType));
                    if (chain.ChainId == EvmChainDefinitions.RobinhoodMainnetChainId)
                    {
                        configuration.RpcEndpoint = RobinhoodDeploymentRegistry.PublicRpcEndpoint;
                    }
                    credential = string.Empty;
                }
                else
                {
                    credential = await app.OnChainProviders.GetCredentialAsync(configuration);
                }
                using var validationTimeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                var poolDiscovery = new EthereumPoolDiscoveryService(
                        new EvmJsonRpcClient(httpClient, configuration, credential),
                        chain,
                        deployments,
                        httpClient);
                discovery = await poolDiscovery.DiscoverCatalogPoolsSharedAsync(
                    tokenAddress,
                    catalog.Pools,
                    validationTimeout.Token);
            }
            catch (Exception exception)
            {
                discoveryError = exception;
            }

            return new EvmChainPoolSearchResult(
                chain,
                catalog,
                discovery,
                catalogError,
                discoveryError);
        }

        private static PoolCatalogSearchResult MergeCatalogResults(
            PoolCatalogSearchResult? primary,
            PoolCatalogSearchResult secondary,
            string assetAddress)
        {
            var addressComparer = string.Equals(secondary.ChainId, "solana", StringComparison.OrdinalIgnoreCase)
                ? StringComparer.Ordinal
                : StringComparer.OrdinalIgnoreCase;
            var pools = secondary.Pools
                .Concat(primary?.Pools ?? [])
                .GroupBy(static pool => pool.PoolAddress, addressComparer)
                .Select(static group => group.First())
                .OrderByDescending(static pool => pool.LiquidityUsd ?? double.MinValue)
                .ThenByDescending(static pool => pool.Volume24hUsd ?? double.MinValue)
                .ToArray();
            return new PoolCatalogSearchResult
            {
                SourceId = primary == null ? secondary.SourceId : $"{primary.SourceId}+{secondary.SourceId}",
                ChainId = secondary.ChainId,
                AssetAddress = assetAddress,
                AssetName = !string.IsNullOrWhiteSpace(primary?.AssetName)
                    ? primary.AssetName
                    : secondary.AssetName,
                AssetSymbol = !string.IsNullOrWhiteSpace(primary?.AssetSymbol)
                    ? primary.AssetSymbol
                    : secondary.AssetSymbol,
                IconUri = primary?.IconUri ?? secondary.IconUri,
                Pools = pools
            };
        }

        private void OnPoolSelectionClick(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox checkBox
                || checkBox.Tag is not OnChainPoolResultRow row
                || !row.IsSelectable)
            {
                return;
            }
            if (checkBox.IsChecked != true)
            {
                SetInstrumentSelected(new SavedTickerInstrument
                {
                    Kind = TickerInstrumentTypes.OnChainPool,
                    Pool = row.Descriptor
                }, false);
                return;
            }
            if (Application.Current is not App app
                || app.OnChainProviders.GetSelectedConfiguration(
                    row.Descriptor.PoolKey.ChainNamespace,
                    row.Descriptor.PoolKey.ChainId) == null)
            {
                row.IsSelected = false;
                checkBox.IsChecked = false;
                ProviderRequiredPanel.Visibility = Visibility.Visible;
                return;
            }
            ProviderRequiredPanel.Visibility = Visibility.Collapsed;
            SetInstrumentSelected(new SavedTickerInstrument
            {
                Kind = TickerInstrumentTypes.OnChainPool,
                DisplayLabel = row.SelectionDisplayLabel,
                SelectedMint = row.SelectedMint,
                AssetName = row.AssetName,
                AssetSymbol = row.AssetSymbol,
                QuoteSymbol = row.QuoteSymbol,
                IconUri = row.IconUri,
                Pool = row.Descriptor
            }, checkBox.IsChecked == true);
        }

        private void PopulatePoolRows(
            string chainNamespace,
            string chainId,
            string mint,
            PoolCatalogSearchResult? catalogResult,
            OnChainPoolDiscoveryResult? discoveryResult,
            string? unvalidatedReason,
            bool append = false)
        {
            if (!append)
            {
                _poolRows.Clear();
            }
            var comparer = string.Equals(chainNamespace, ChainNamespaces.Eip155, StringComparison.Ordinal)
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            var directPools = (discoveryResult?.Pools ?? [])
                .GroupBy(static pool => pool.PoolKey.PoolAddress, comparer)
                .ToDictionary(static group => group.Key, static group => group.First(), comparer);
            var matched = new HashSet<string>(comparer);
            var rows = new List<OnChainPoolResultRow>();
            foreach (var catalogPool in catalogResult?.Pools ?? [])
            {
                var descriptor = directPools.TryGetValue(catalogPool.PoolAddress, out var direct)
                    ? direct
                    : CreateCatalogDescriptor(
                        chainNamespace,
                        chainId,
                        mint,
                        catalogPool,
                        unvalidatedReason);
                matched.Add(catalogPool.PoolAddress);
                rows.Add(CreatePoolRow(descriptor, mint, catalogPool, catalogResult));
            }
            foreach (var descriptor in directPools.Values.Where(pool => !matched.Contains(pool.PoolKey.PoolAddress)))
            {
                rows.Add(CreatePoolRow(descriptor, mint, null, catalogResult));
            }

            rows = rows
                .Where(static row => row.IsSelectable)
                .OrderByDescending(static row => row.IsSelectable)
                .ThenByDescending(static row => row.LiquidityUsd ?? double.MinValue)
                .ThenBy(static row => row.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static row => row.Descriptor.PoolKey.PoolAddress, comparer)
                .ToList();
            foreach (var row in rows)
            {
                _poolRows.Add(row);
            }
            var metadataChanged = false;
            foreach (var row in rows)
            {
                metadataChanged |= EnrichSelectedInstrumentMetadata(row);
            }
            if (metadataChanged)
            {
                UpdateSelectedInstrumentPresentation();
                SetDirty(true);
            }
        }

        private bool EnrichSelectedInstrumentMetadata(OnChainPoolResultRow row)
        {
            if (!row.IsSelected || row.CatalogPool == null)
            {
                return false;
            }
            for (var index = 0; index < SelectedInstrumentsChips.Items.Count; index++)
            {
                if (SelectedInstrumentsChips.Items[index] is not SavedTickerInstrument instrument
                    || !string.Equals(
                        SavedWidgetCatalogRules.GetInstrumentKey(instrument),
                        row.Key,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var changed = !string.Equals(instrument.DisplayLabel, row.SelectionDisplayLabel, StringComparison.Ordinal)
                              || !string.Equals(instrument.AssetName, row.AssetName, StringComparison.Ordinal)
                              || !string.Equals(instrument.AssetSymbol, row.AssetSymbol, StringComparison.Ordinal)
                              || !string.Equals(instrument.QuoteSymbol, row.QuoteSymbol, StringComparison.Ordinal)
                              || !string.Equals(instrument.IconUri, row.IconUri, StringComparison.Ordinal);
                if (!changed)
                {
                    return false;
                }
                instrument.DisplayLabel = row.SelectionDisplayLabel;
                instrument.AssetName = row.AssetName;
                instrument.AssetSymbol = row.AssetSymbol;
                instrument.QuoteSymbol = row.QuoteSymbol;
                instrument.IconUri = row.IconUri;
                SelectedInstrumentsChips.Items.RemoveAt(index);
                SelectedInstrumentsChips.Items.Insert(index, instrument);
                return true;
            }
            return false;
        }

        private OnChainPoolResultRow CreatePoolRow(
            OnChainPoolDescriptor descriptor,
            string mint,
            PoolCatalogEntry? catalogPool,
            PoolCatalogSearchResult? catalogResult)
        {
            return new OnChainPoolResultRow(descriptor, mint, catalogPool, catalogResult)
            {
                IsSelected = ContainsInstrumentKey(SavedWidgetCatalogRules.GetInstrumentKey(
                    new SavedTickerInstrument
                    {
                        Kind = TickerInstrumentTypes.OnChainPool,
                        Pool = descriptor
                    }))
            };
        }

        private static OnChainPoolDescriptor CreateCatalogDescriptor(
            string chainNamespace,
            string chainId,
            string mint,
            PoolCatalogEntry pool,
            string? unvalidatedReason)
        {
            return new OnChainPoolDescriptor
            {
                PoolKey = new OnChainPoolKey
                {
                    DeploymentKey = new OnChainDeploymentKey
                    {
                        ChainNamespace = chainNamespace,
                        ChainId = chainId,
                        ProtocolId = pool.ProtocolId
                    },
                    PoolId = pool.PoolAddress
                },
                PoolType = pool.ProtocolId,
                BaseMint = pool.BaseAsset.Address,
                QuoteMint = pool.QuoteAsset.Address,
                PairOrientation = string.Equals(pool.BaseAsset.Address, mint, StringComparison.OrdinalIgnoreCase)
                    ? "selectedAsBase"
                    : "selectedAsQuote",
                DiscoverySource = pool.SourceId,
                SupportStatus = unvalidatedReason == null
                    ? OnChainSupportStatus.DiscoveredUnsupported
                    : OnChainSupportStatus.TemporarilyUnavailable,
                SupportReason = null
            };
        }

        private static string BuildPoolSearchStatus(
            PoolCatalogSearchResult? catalog,
            OnChainPoolDiscoveryResult? discovery,
            Exception? catalogError,
            Exception? discoveryError)
        {
            if ((catalog?.Pools.Length ?? 0) > 0 || (discovery?.Pools.Length ?? 0) > 0)
            {
                return string.Empty;
            }
            return catalogError != null || discoveryError != null
                ? "Pool search is temporarily unavailable. Try again."
                : "No pools were found.";
        }

        private void SetPoolSearchStatus(string? message)
        {
            var hasMessage = !string.IsNullOrWhiteSpace(message);
            PoolSearchStatusText.Text = hasMessage ? message : string.Empty;
            PoolSearchStatusText.Visibility = hasMessage ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string FormatMetric(double? value)
        {
            if (value is not double number)
            {
                return "--";
            }
            return number switch
            {
                >= 1_000_000_000 => $"${number / 1_000_000_000:0.##}B",
                >= 1_000_000 => $"${number / 1_000_000:0.##}M",
                >= 1_000 => $"${number / 1_000:0.##}K",
                >= 0 => $"${number:0.##}",
                _ => "--"
            };
        }

        private static string FormatAge(long createdAtUnixMs)
        {
            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(createdAtUnixMs);
            return age.TotalDays >= 1
                ? $"{Math.Max(0, (int)age.TotalDays)}d"
                : age.TotalHours >= 1
                    ? $"{Math.Max(0, (int)age.TotalHours)}h"
                    : $"{Math.Max(1, (int)age.TotalMinutes)}m";
        }

        private void RefreshWidgets(string? preferredId = null)
        {
            var widgets = Catalog.GetWidgets();
            _widgetItems.Clear();
            foreach (var widget in widgets)
            {
                _widgetItems.Add(new SavedWidgetListItem(
                    widget.Id,
                    widget.Name));
            }

            UpdateSidebarVisibility(_widgetItems.Count > 0);
            WidgetHeadingText.Text = $"Your widgets ({_widgetItems.Count})";
            var selected = !string.IsNullOrWhiteSpace(preferredId)
                ? _widgetItems.FirstOrDefault(item => string.Equals(item.Id, preferredId, StringComparison.OrdinalIgnoreCase))
                : null;
            selected ??= _widgetItems.FirstOrDefault();
            SetSelectedListItem(selected);
            if (selected == null)
            {
                ClearEditor();
            }
            else
            {
                ApplyWidget(selected);
            }
        }

        private void ApplyWidget(SavedWidgetListItem item)
        {
            var widget = Catalog.Find(item.Id);
            if (widget == null)
            {
                RefreshWidgets();
                return;
            }

            _selectedListItem = item;
            _selectedWidget = widget;
            WidgetEmptyState.Visibility = Visibility.Collapsed;
            WidgetEditor.Visibility = Visibility.Visible;
            WidgetNameText.Text = widget.Name;
            ApplySelectedWidgetData(widget);
            SetDirty(false);
        }

        private void ApplySelectedWidgetData(SavedWidgetDefinition widget)
        {
            var isWallet = widget.Type == PanelWidgetTypes.WalletActivity;
            var isXTimeline = widget.Type == PanelWidgetTypes.XTimeline;
            var isWebsite = widget.Type == PanelWidgetTypes.Website;
            WebsiteEditor.Visibility = isWebsite ? Visibility.Visible : Visibility.Collapsed;
            PriceTickerEditor.Visibility = isWallet || isXTimeline || isWebsite ? Visibility.Collapsed : Visibility.Visible;
            WalletActivityEditor.Visibility = isWallet ? Visibility.Visible : Visibility.Collapsed;
            XTimelineEditor.Visibility = isXTimeline ? Visibility.Visible : Visibility.Collapsed;
            if (isXTimeline)
            {
                _isApplying = true;
                try
                {
                    XTimelineUrlBox.Text = string.Join(Environment.NewLine, widget.XHandles);
                    XTextFilterBox.Text = widget.XTextFilter;
                    XPostsCheck.IsChecked = widget.XIncludePosts;
                    XRepliesCheck.IsChecked = widget.XIncludeReplies;
                    XRepostsCheck.IsChecked = widget.XIncludeReposts;
                    XQuotesCheck.IsChecked = widget.XIncludeQuotes;
                    XExternalContentConsentToggle.IsOn = widget.XExternalContentConsent;
                    XTimelineEditorStatusText.Text = widget.XHandles.Length == 0
                        ? "Add accounts and save. Configure and test the official X API under API settings."
                        : string.Empty;
                }
                finally
                {
                    _isApplying = false;
                }
            }
            else if (isWebsite)
            {
                _isApplying = true;
                try
                {
                    WebsiteUrlBox.Text = widget.WebsiteUrl;
                    WebsiteZoomBox.Value = widget.WebsiteZoom * 100;
                    WebsiteStatusText.Text = "";
                }
                finally { _isApplying = false; }
            }
            else if (isWallet)
            {
                ApplyTrackedWallets(widget.Wallets);
                WalletEditorStatusText.Text = _walletRows.Count == 0
                    ? "Add at least one public address to receive wallet activity."
                    : string.Empty;
            }
            else
            {
                _isApplying = true;
                try
                {
                    OnChainPriceModeBox.SelectedItem = OnChainPriceModeBox.Items
                        .OfType<ComboBoxItem>()
                        .First(item => string.Equals(item.Tag as string,
                            OnChainPriceModes.Normalize(widget.OnChainPriceMode),
                            StringComparison.Ordinal));
                }
                finally { _isApplying = false; }
                ApplySelectedInstruments(widget.Instruments);
                UpdateSelectedInstrumentPresentation();
            }
        }

        private void OnOnChainPriceModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isApplying && _selectedWidget?.Type == PanelWidgetTypes.PriceTicker)
            {
                SetDirty(true);
            }
        }

        private void UpdateSidebarVisibility(bool hasWidgets)
        {
            WidgetSidebarColumn.Width = hasWidgets ? new GridLength(240) : new GridLength(0);
            WidgetSidebar.Visibility = hasWidgets ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ClearEditor()
        {
            _selectedListItem = null;
            _selectedWidget = null;
            SelectedInstrumentsChips.Items.Clear();
            SetMarketRowsSelected([]);
            _poolRows.Clear();
            _walletRows.Clear();
            WalletAddressBox.Text = string.Empty;
            WalletLabelBox.Text = string.Empty;
            WalletEditorStatusText.Text = string.Empty;
            XTimelineUrlBox.Text = string.Empty;
            XExternalContentConsentToggle.IsOn = false;
            XTimelineEditorStatusText.Text = string.Empty;
            SelectedInstrumentsScroller.Visibility = Visibility.Collapsed;
            WidgetEmptyState.Visibility = Visibility.Visible;
            WidgetEditor.Visibility = Visibility.Collapsed;
            SetDirty(false);
        }

        private void SetSelectedListItem(SavedWidgetListItem? item)
        {
            _isApplying = true;
            try
            {
                WidgetList.SelectedItem = item;
            }
            finally
            {
                _isApplying = false;
            }
        }

        private async Task<bool> ConfirmDiscardChangesAsync()
        {
            if (!_isDirty)
            {
                return true;
            }

            return await ShowConfirmationAsync(
                       "Discard widget changes?",
                       "Your unsaved instrument changes will be lost.",
                       "Discard changes",
                       "Stay") == ContentDialogResult.Primary;
        }

        private void SetDirty(bool isDirty)
        {
            if (isDirty && _suppressDirtyEvents)
            {
                return;
            }

            _isDirty = isDirty;
            SaveStatusText.Text = isDirty ? "Unsaved changes" : string.Empty;
            SetActionButtonsDirty(isDirty);
        }

        private void SetActionButtonsDirty(bool isDirty)
        {
            WidgetCancelButton.IsEnabled = isDirty;
            WidgetSaveButton.IsEnabled = isDirty;
        }

        private void UpdateSelectedInstrumentPresentation()
        {
            SelectedInstrumentsScroller.Visibility = GetSelectedInstruments().Length == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void ApplySelectedInstruments(IEnumerable<SavedTickerInstrument> instruments)
        {
            SelectedInstrumentsChips.Items.Clear();
            var selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var instrument in instruments ?? [])
            {
                if (selectedKeys.Add(SavedWidgetCatalogRules.GetInstrumentKey(instrument)))
                {
                    SelectedInstrumentsChips.Items.Add(instrument);
                }
            }

            SetMarketRowsSelected(selectedKeys);
            SetPoolRowsSelected(selectedKeys);
        }

        private void SetInstrumentSelected(SavedTickerInstrument instrument, bool isSelected)
        {
            var key = SavedWidgetCatalogRules.GetInstrumentKey(instrument);
            var changed = false;
            if (isSelected)
            {
                if (!ContainsInstrumentKey(key))
                {
                    SelectedInstrumentsChips.Items.Add(instrument);
                    changed = true;
                }
            }
            else
            {
                for (var index = 0; index < SelectedInstrumentsChips.Items.Count; index++)
                {
                    if (SelectedInstrumentsChips.Items[index] is SavedTickerInstrument existing
                        && string.Equals(
                            SavedWidgetCatalogRules.GetInstrumentKey(existing),
                            key,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        SelectedInstrumentsChips.Items.RemoveAt(index);
                        changed = true;
                        break;
                    }
                }
            }

            SetMarketRowsSelected(GetSelectedInstrumentKeys());
            SetPoolRowsSelected(GetSelectedInstrumentKeys());

            if (changed)
            {
                UpdateSelectedInstrumentPresentation();
                SetDirty(true);
            }
        }

        private void SetMarketRowsSelected(IEnumerable<string> instrumentKeys)
        {
            var selectedKeys = new HashSet<string>(instrumentKeys, StringComparer.OrdinalIgnoreCase);
            foreach (var row in _allMarketRows)
            {
                row.IsSelected = selectedKeys.Contains(CentralizedKey(row.VenueId, row.Symbol));
            }
        }

        private void SetPoolRowsSelected(IEnumerable<string> instrumentKeys)
        {
            var selectedKeys = new HashSet<string>(instrumentKeys, StringComparer.OrdinalIgnoreCase);
            foreach (var row in _poolRows)
            {
                row.IsSelected = selectedKeys.Contains(row.Key);
            }
        }

        private SavedTickerInstrument[] GetSelectedInstruments()
        {
            return SelectedInstrumentsChips.Items.OfType<SavedTickerInstrument>().ToArray();
        }

        private void ApplyTrackedWallets(IEnumerable<SavedTrackedWallet> wallets)
        {
            _walletRows.Clear();
            foreach (var wallet in wallets ?? [])
            {
                var normalized = SavedWidgetCatalogRules.NormalizeWallet(new SavedTrackedWallet
                {
                    ChainNamespace = wallet.ChainNamespace,
                    ChainId = wallet.ChainId,
                    Address = wallet.Address,
                    Label = wallet.Label
                });
                if (normalized != null)
                {
                    _walletRows.Add(new TrackedWalletEditorRow(normalized));
                }
            }
        }

        private SavedTrackedWallet[] GetTrackedWallets()
        {
            return _walletRows.Select(row => new SavedTrackedWallet
            {
                ChainNamespace = row.Wallet.ChainNamespace,
                ChainId = row.Wallet.ChainId,
                Address = row.Wallet.Address,
                Label = row.Wallet.Label
            }).ToArray();
        }

        private string[] GetSelectedInstrumentKeys()
        {
            return GetSelectedInstruments().Select(SavedWidgetCatalogRules.GetInstrumentKey).ToArray();
        }

        private bool ContainsInstrumentKey(string key)
        {
            return GetSelectedInstruments().Any(instrument => string.Equals(
                SavedWidgetCatalogRules.GetInstrumentKey(instrument),
                key,
                StringComparison.OrdinalIgnoreCase));
        }

        private async Task LoadMarketRowsAsync()
        {
            SetLoadingState(true);
            _allMarketRows.Clear();
            _filteredMarketRows.Clear();
            await Task.Yield();

            try
            {
                foreach (var exchange in MarketCacheService.GetCertifiedExchanges())
                {
                    var entry = await Task.Run(async () => await MarketCacheService.EnsureExchangeCacheAsync(exchange.Id));
                    if (entry == null)
                    {
                        continue;
                    }

                    foreach (var market in entry.Markets)
                    {
                        var row = new MarketRow(market.Symbol, market.Venue.Id, exchange.Name);
                        row.IsSelected = ContainsInstrumentKey(CentralizedKey(row.VenueId, row.Symbol));
                        _allMarketRows.Add(row);
                        if (MatchesFilter(row, _currentFilter))
                        {
                            _filteredMarketRows.Add(row);
                        }
                    }

                    if (_filteredMarketRows.Count > 0)
                    {
                        SetLoadingState(false);
                    }
                }
            }
            finally
            {
                SetLoadingState(false);
            }
        }

        private void ApplyMarketFilter()
        {
            _filteredMarketRows.Clear();
            foreach (var row in _allMarketRows)
            {
                if (MatchesFilter(row, _currentFilter))
                {
                    _filteredMarketRows.Add(row);
                }
            }

            SetEmptyStateVisibility();
        }

        private void SetLoadingState(bool isLoading)
        {
            MarketsLoadingOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
            MarketsListView.IsEnabled = !isLoading;
            SetEmptyStateVisibility();
        }

        private void SetEmptyStateVisibility()
        {
            if (_isShowingOnChainResults)
            {
                MarketsEmptyState.Visibility = Visibility.Collapsed;
                return;
            }
            var isLoading = MarketsLoadingOverlay.Visibility == Visibility.Visible;
            MarketsEmptyState.Visibility = !isLoading && _filteredMarketRows.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (MarketsEmptyState.Visibility == Visibility.Visible)
            {
                UpdateEmptyStateWithErrors();
            }
        }

        private void ShowExchangeResults()
        {
            _isShowingOnChainResults = false;
            MarketsListView.Visibility = Visibility.Visible;
            OnChainResultsGrid.Visibility = Visibility.Collapsed;
            SetPoolSearchStatus(null);
            PoolSearchProgress.IsActive = false;
            PoolSearchProgress.Visibility = Visibility.Collapsed;
            ProviderRequiredPanel.Visibility = Visibility.Collapsed;
            SetEmptyStateVisibility();
        }

        private void ShowOnChainResults()
        {
            _isShowingOnChainResults = true;
            MarketsListView.Visibility = Visibility.Collapsed;
            MarketsLoadingOverlay.Visibility = Visibility.Collapsed;
            MarketsEmptyState.Visibility = Visibility.Collapsed;
            OnChainResultsGrid.Visibility = Visibility.Visible;
            UpdateOnChainEmptyState();
        }

        private void HideProviderRequiredMessageWhenConfigured()
        {
            if (Application.Current is App app
                && (app.OnChainProviders.GetSelectedConfiguration(
                        ChainNamespaces.Solana,
                        "mainnet-beta") != null
                    || EvmChainDefinitions.Supported.Any(chain =>
                        app.OnChainProviders.GetSelectedConfiguration(
                            chain.ChainNamespace,
                            chain.ChainId) != null)))
            {
                ProviderRequiredPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateOnChainEmptyState()
        {
            if (OnChainPoolsEmptyState != null)
            {
                OnChainPoolsEmptyState.Visibility = _poolRows.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            if (OnChainPoolsEmptyPrompt != null)
            {
                OnChainPoolsEmptyPrompt.Visibility = PoolSearchProgress.IsActive
                                                     || !string.IsNullOrWhiteSpace(PoolSearchStatusText.Text)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
        }

        private static string CentralizedKey(string venueId, string symbol)
        {
            return $"cex|{venueId}|{symbol}";
        }

        private void UpdateEmptyStateWithErrors()
        {
            if (!string.IsNullOrWhiteSpace(_currentFilter))
            {
                MarketsEmptyTitle.Text = "No matching markets.";
                MarketsEmptySubtitle.Text = "Try another symbol, exchange, or contract address.";
                return;
            }
            var errors = MarketCacheService.GetLastErrorExchangeIds();
            if (errors.Length == 0)
            {
                MarketsEmptyTitle.Text = "No matching markets.";
                MarketsEmptySubtitle.Text = "Try another symbol or exchange.";
                return;
            }

            MarketsEmptyTitle.Text = "Some markets could not be loaded.";
            MarketsEmptySubtitle.Text = $"Missing: {string.Join(", ", errors)}";
        }

        private static bool MatchesFilter(MarketRow row, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
            {
                return true;
            }

            var normalized = NormalizeToken(filter);
            return NormalizeToken(row.Symbol).Contains(normalized, StringComparison.OrdinalIgnoreCase)
                   || NormalizeToken(row.VenueName).Contains(normalized, StringComparison.OrdinalIgnoreCase)
                   || NormalizeToken(row.VenueId).Contains(normalized, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSolanaAddress(string value)
        {
            if (value.Length is < 32 or > 44)
            {
                return false;
            }
            try
            {
                return SolanaBase58.Decode(value).Length == 32;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static string NormalizeToken(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Replace(" ", string.Empty)
                    .Replace("/", string.Empty)
                    .Replace("-", string.Empty)
                    .Replace("_", string.Empty);
        }

        private async Task ShowMessageAsync(string title, string message)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = title,
                Content = string.IsNullOrWhiteSpace(message) ? "Try again." : message,
                CloseButtonText = "OK",
                DefaultButton = ContentDialogButton.Close
            };
            await dialog.ShowAsync();
        }

        private async Task<ContentDialogResult> ShowConfirmationAsync(
            string title,
            string message,
            string primaryButtonText,
            string closeButtonText)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = title,
                Content = message,
                PrimaryButtonText = primaryButtonText,
                CloseButtonText = closeButtonText,
                DefaultButton = ContentDialogButton.Close
            };
            return await dialog.ShowAsync();
        }
    }
}

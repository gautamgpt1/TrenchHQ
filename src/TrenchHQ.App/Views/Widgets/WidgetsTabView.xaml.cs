using TrenchHQ.ViewModels;
using TrenchHQ.Core.OnChain;
using TrenchHQ.Core.OnChain.Evm;
using TrenchHQ.Core.OnChain.Solana;
using TrenchHQ.Core.Providers;
using TrenchHQ.Core.Social;
using TrenchHQ.Core.Widgets;
using TrenchHQ.Infrastructure.Markets;
using TrenchHQ.Infrastructure.OnChain;
using TrenchHQ.Infrastructure.OnChain.Evm;
using TrenchHQ.Infrastructure.OnChain.Solana;
using TrenchHQ.Infrastructure.Providers;
using TrenchHQ.Infrastructure.Widgets;
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
            SaveStatusText.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) =>
                SaveStatusText.Visibility = string.IsNullOrWhiteSpace(SaveStatusText.Text)
                    ? Visibility.Collapsed : Visibility.Visible);
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
                    SaveStatusText.Text = "This pool requires Live streaming. Choose it to watch this pool.";
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
            // TextChanged is deferred; loading normalized saved text must stay clean.
            _suppressDirtyEvents = true;
            ApplySelectedWidgetData(widget);
            SetDirty(false);
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => _suppressDirtyEvents = false);
        }

        private void OnWidgetEditorHeaderSizeChanged(object sender, SizeChangedEventArgs e)
        {
            var compact = e.NewSize.Width < 620;
            Grid.SetRow(WidgetEditorActions, compact ? 1 : 0);
            Grid.SetColumn(WidgetEditorActions, compact ? 0 : 1);
            Grid.SetColumnSpan(WidgetEditorActions, compact ? 2 : 1);
            Grid.SetColumnSpan(WidgetNameText, compact ? 2 : 1);
            WidgetEditorActions.Margin = new Thickness(0, compact ? 8 : 0, 0, 0);
        }

        private void ApplySelectedWidgetData(SavedWidgetDefinition widget)
        {
            var isWallet = widget.Type == PanelWidgetTypes.WalletActivity;
            var isXTimeline = widget.Type == PanelWidgetTypes.XTimeline;
            var isWebsite = widget.Type == PanelWidgetTypes.Website;
            WebsiteEditor.Visibility = isWebsite ? Visibility.Visible : Visibility.Collapsed;
            PriceTickerEditor.Visibility = isWallet || isXTimeline || isWebsite ? Visibility.Collapsed : Visibility.Visible;
            OnChainPriceModeBox.Visibility = PriceTickerEditor.Visibility;
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

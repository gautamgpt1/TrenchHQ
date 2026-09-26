using TrenchHQ.Helpers;
using TrenchHQ.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI;

namespace TrenchHQ.Controls
{
    public sealed partial class WalletActivityWidget : UserControl, IDisposable
    {
        private WalletActivityService? _activityService;
        private SavedTrackedWallet[] _wallets = [];
        private PanelWidgetHostContext _hostContext = PanelWidgetHostRules.ForOverlay();
        private readonly DispatcherTimer _ageTimer;
        private readonly Dictionary<string, MetadataCacheEntry> _metadataCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTimeOffset> _metadataRetryAfter = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _metadataRequests = new(StringComparer.OrdinalIgnoreCase);
        private WidgetContentStatus _contentStatus = new(WidgetContentState.Loading, "Connecting");
        private bool _disposed;
        private string? _configurationError;
        private double _contentScale = 1d;

        public ObservableCollection<WalletActivityRow> Rows { get; } = [];
        internal WidgetContentStatus ContentStatus => _contentStatus;
        internal event EventHandler? ContentStatusChanged;

        public WalletActivityWidget()
        {
            InitializeComponent();
            _ageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _ageTimer.Tick += OnAgeTimerTick;
            _ageTimer.Start();
            if (Application.Current is App app)
            {
                _activityService = app.WalletActivity;
                _activityService.ActivityChanged += OnActivityChanged;
                _activityService.StateChanged += OnStateChanged;
            }
        }

        internal void Configure(
            SavedWidgetDefinition? definition,
            PanelWidgetHostContext hostContext,
            Color textColor,
            double contentScale)
        {
            _configurationError = null;
            _hostContext = hostContext;
            _contentScale = PanelContentSizeRules.NormalizeScale(contentScale);
            ApplyHostContext();
            ApplyTextColor(textColor);
            if (definition == null
                || !string.Equals(definition.Type, PanelWidgetTypes.WalletActivity, StringComparison.OrdinalIgnoreCase))
            {
                _wallets = [];
                _configurationError = "Wallet Watcher configuration is unavailable.";
            }
            else
            {
                _wallets = definition.Wallets ?? [];
            }
            RefreshRows();
            UpdateStatus();
        }

        internal void InvalidateContentMeasure()
        {
            OverlayRowsControl?.InvalidateMeasure();
            WidgetRoot?.InvalidateMeasure();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _ageTimer.Stop();
            _ageTimer.Tick -= OnAgeTimerTick;
            if (_activityService != null)
            {
                _activityService.ActivityChanged -= OnActivityChanged;
                _activityService.StateChanged -= OnStateChanged;
                _activityService = null;
            }
        }

        private void OnAgeTimerTick(object? sender, object e)
        {
            var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var row in Rows)
            {
                row.RefreshAge(nowUnixMs);
            }
        }

        private void OnActivityChanged(object? sender, WalletActivityChangedEventArgs e)
        {
            if (_disposed || !ContainsWallet(e.Update))
            {
                return;
            }
            DispatcherQueue.TryEnqueue(() =>
            {
                RefreshRows();
                UpdateStatus();
            });
        }

        private void OnStateChanged(object? sender, WalletStreamStateChangedEventArgs e)
        {
            if (_disposed || !_wallets.Any(wallet =>
                    wallet.ChainNamespace == e.ChainNamespace && wallet.ChainId == e.ChainId))
            {
                return;
            }
            DispatcherQueue.TryEnqueue(UpdateStatus);
        }

        private bool ContainsWallet(WalletActivityUpdate update)
        {
            return _wallets.Any(wallet =>
                wallet.ChainNamespace == update.ChainNamespace
                && wallet.ChainId == update.ChainId
                && string.Equals(
                    wallet.Address,
                    update.WalletAddress,
                    wallet.ChainNamespace == ChainNamespaces.Eip155
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal));
        }

        private void RefreshRows()
        {
            if (_activityService == null)
            {
                return;
            }
            const int maximum = 20;
            Rows.Clear();
            var transactions = _activityService.GetRecent(_wallets, maximum * 12)
                .GroupBy(static update => new
                {
                    update.ChainNamespace,
                    update.ChainId,
                    update.WalletAddress,
                    update.TransactionId
                })
                .Take(maximum);
            foreach (var transaction in transactions)
            {
                var row = new WalletActivityRow(transaction.ToArray());
                row.SetContentScale(_contentScale);
                ApplyOrLoadMetadata(row);
                Rows.Add(row);
            }
        }

        private void ApplyOrLoadMetadata(WalletActivityRow row)
        {
            if (row.MetadataKey is not { } key
                || row.DexScreenerChainId is not { } chainId
                || row.MarketAssetAddress is not { } assetAddress)
            {
                return;
            }
            if (_metadataCache.TryGetValue(key, out var cached))
            {
                if (cached.ExpiresAt > DateTimeOffset.UtcNow)
                {
                    row.ApplyCatalog(cached.Result);
                    return;
                }
                _metadataCache.Remove(key);
            }
            if (_metadataRetryAfter.TryGetValue(key, out var retryAfter)
                && retryAfter > DateTimeOffset.UtcNow)
            {
                return;
            }
            if (_metadataRequests.Add(key))
            {
                _ = LoadMetadataAsync(key, chainId, assetAddress);
            }
        }

        private async Task LoadMetadataAsync(string key, string chainId, string assetAddress)
        {
            try
            {
                var result = await DexScreenerPoolCatalogClient.Current
                    .SearchAsync(chainId, assetAddress)
                    .ConfigureAwait(false);
                DispatcherQueue.TryEnqueue(() => CompleteMetadata(key, result));
            }
            catch
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    _metadataRequests.Remove(key);
                    _metadataRetryAfter[key] = DateTimeOffset.UtcNow.AddMinutes(1);
                });
            }
        }

        private void CompleteMetadata(string key, PoolCatalogSearchResult result)
        {
            _metadataRequests.Remove(key);
            if (_disposed)
            {
                return;
            }
            _metadataRetryAfter.Remove(key);
            _metadataCache[key] = new MetadataCacheEntry(result, DateTimeOffset.UtcNow.AddMinutes(5));
            foreach (var row in Rows.Where(row => string.Equals(
                         row.MetadataKey,
                         key,
                         StringComparison.OrdinalIgnoreCase)))
            {
                row.ApplyCatalog(result);
            }
        }

        private void UpdateStatus()
        {
            var status = GetStatus();
            if (status == _contentStatus)
            {
                return;
            }
            _contentStatus = status;
            ContentStatusChanged?.Invoke(this, EventArgs.Empty);
        }

        private WidgetContentStatus GetStatus()
        {
            if (!string.IsNullOrWhiteSpace(_configurationError))
            {
                return new WidgetContentStatus(WidgetContentState.Error, "Error");
            }
            if (_wallets.Length == 0)
            {
                return new WidgetContentStatus(WidgetContentState.Empty, "Not configured");
            }
            if (_activityService == null)
            {
                return new WidgetContentStatus(WidgetContentState.Unavailable, "Unavailable");
            }
            return _activityService.GetStateFor(_wallets) switch
            {
                WalletStreamState.Live => new WidgetContentStatus(WidgetContentState.Live, "Live"),
                WalletStreamState.Reconnecting => new WidgetContentStatus(WidgetContentState.Loading, "Reconnecting"),
                WalletStreamState.Unavailable => new WidgetContentStatus(WidgetContentState.Unavailable, "Unavailable"),
                WalletStreamState.Disconnected => new WidgetContentStatus(WidgetContentState.Empty, "Stopped"),
                _ => new WidgetContentStatus(WidgetContentState.Loading, "Connecting")
            };
        }

        private void ApplyHostContext()
        {
            OverlayLayout.Visibility = _hostContext.HostType == PanelWidgetHostType.Overlay
                ? Visibility.Visible
                : Visibility.Collapsed;
            HorizontalBarLayout.Visibility = _hostContext.HostType == PanelWidgetHostType.DockedBar
                                             && _hostContext.Orientation == PanelWidgetOrientation.Horizontal
                ? Visibility.Visible
                : Visibility.Collapsed;
            VerticalBarLayout.Visibility = _hostContext.HostType == PanelWidgetHostType.DockedBar
                                           && _hostContext.Orientation == PanelWidgetOrientation.Vertical
                ? Visibility.Visible
                : Visibility.Collapsed;
            HorizontalBarLayout.Margin = new Thickness(0, 0, 120 * _contentScale, 0);
        }

        private void ApplyTextColor(Color color)
        {
            if (WidgetRoot.Resources["WidgetTextBrush"] is SolidColorBrush textBrush)
            {
                textBrush.Color = color;
            }
        }

        private sealed record MetadataCacheEntry(
            PoolCatalogSearchResult Result,
            DateTimeOffset ExpiresAt);
    }
}

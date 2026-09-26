using TrenchHQ.Helpers;
using TrenchHQ.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Windows.UI;

namespace TrenchHQ.Controls
{
    public sealed partial class PriceTickerWidget : UserControl, IDisposable
    {
        private readonly ConcurrentDictionary<string, PriceTickerRow> _rowsByStreamKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, PriceTickerRow> _rowsByPoolKey = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, OnChainPriceUpdate> _pendingOnChainUpdates = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, (ulong Epoch, ulong Sequence)> _renderedOnChainSequences = new(StringComparer.Ordinal);
        private readonly DispatcherQueueTimer _stateTimer;
        private readonly DispatcherQueueTimer _onChainRenderTimer;
        private PanelWidgetHostContext _hostContext = PanelWidgetHostRules.ForOverlay();
        private string _currentPairSignature = string.Empty;
        private bool _priceFlashOnChange = true;
        private bool _hasReceivedPrice;
        private bool _hasStreamError;
        private string _streamErrorMessage = string.Empty;
        private string _widgetErrorMessage = string.Empty;
        private DateTimeOffset _waitingSince = DateTimeOffset.UtcNow;
        private DateTimeOffset _lastPriceAt;
        private bool _disposed;
        private bool _hasCentralizedRows;
        private bool _hasOnChainRows;
        private bool _hasSolanaRows;
        private double _contentScale = 1d;
        private readonly HashSet<string> _evmChainIds = new(StringComparer.Ordinal);
        private OnChainStreamCoordinator? _onChainStreams;
        private EvmStreamCoordinatorCollection? _evmStreams;
        private WidgetContentStatus _contentStatus = new(WidgetContentState.Loading, "Connecting");

        public ObservableCollection<PriceTickerRow> Rows { get; } = [];
        internal event EventHandler? AutomaticThicknessChanged;
        internal event EventHandler? ContentStatusChanged;
        internal WidgetContentStatus ContentStatus => _contentStatus;

        public PriceTickerWidget()
        {
            InitializeComponent();
            MarketSidecarClient.Current.PriceUpdated += OnPriceUpdated;
            MarketSidecarClient.Current.StateChanged += OnSidecarStateChanged;
            MarketSidecarClient.Current.StreamError += OnStreamError;
            OnChainEngineClient.Current.PriceUpdated += OnOnChainPriceUpdated;
            OnChainEngineClient.Current.StateChanged += OnOnChainEngineStateChanged;
            if (Application.Current is App app)
            {
                _onChainStreams = app.OnChainStreams;
                _onChainStreams.StateChanged += OnOnChainRecoveryStateChanged;
                _onChainStreams.StreamError += OnOnChainStreamError;
                _evmStreams = app.EvmStreams;
                _evmStreams.StateChanged += OnOnChainRecoveryStateChanged;
                _evmStreams.StreamError += OnOnChainStreamError;
            }
            _stateTimer = DispatcherQueue.CreateTimer();
            _stateTimer.Interval = TimeSpan.FromSeconds(1);
            _stateTimer.Tick += OnStateTimerTick;
            _stateTimer.Start();
            _onChainRenderTimer = DispatcherQueue.CreateTimer();
            _onChainRenderTimer.Interval = TimeSpan.FromSeconds(1d / 15d);
            _onChainRenderTimer.Tick += OnOnChainRenderTimerTick;
            _onChainRenderTimer.Start();
        }

        internal void Configure(
            SavedWidgetDefinition? definition,
            PanelWidgetHostContext hostContext,
            Color textColor,
            bool priceFlashOnChange,
            double contentScale)
        {
            RunSafely(() =>
            {
                _widgetErrorMessage = string.Empty;
                _hostContext = hostContext;
                _priceFlashOnChange = priceFlashOnChange;
                _contentScale = PanelContentSizeRules.NormalizeScale(contentScale);
                ApplyHostContext();
                ApplyTextColor(textColor);

                if (definition == null
                    || !string.Equals(definition.Type, PanelWidgetTypes.PriceTicker, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Price Ticker configuration is unavailable.");
                }

                EnsureRows(definition.Instruments);
                ApplyRowAppearance(textColor);
                ApplyContentScale();
                UpdateStatus();
                AutomaticThicknessChanged?.Invoke(this, EventArgs.Empty);
            });
        }

        internal void InvalidateContentMeasure()
        {
            OverlayRowsControl?.InvalidateMeasure();
            HorizontalRowsControl?.InvalidateMeasure();
            VerticalRowsControl?.InvalidateMeasure();
            WidgetRoot?.InvalidateMeasure();
        }

        internal int GetAutomaticDockedThickness(
            int monitorSpan,
            double textScaleFactor,
            double rasterizationScale)
        {
            var availablePrimarySpan = _hostContext.Orientation == PanelWidgetOrientation.Horizontal
                ? HorizontalRowsScroller.ActualWidth
                : VerticalRowsScroller.ActualHeight;
            return DockedBarLayoutRules.GetAutomaticPriceTickerThickness(
                _hostContext.Orientation == PanelWidgetOrientation.Horizontal ? "Top" : "Left",
                availablePrimarySpan > 0 ? Rows.Count : 0,
                availablePrimarySpan,
                textScaleFactor,
                monitorSpan,
                rasterizationScale,
                double.NaN,
                _contentScale);
        }

        private void OnDockedViewportSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_hostContext.HostType == PanelWidgetHostType.DockedBar)
            {
                AutomaticThicknessChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            MarketSidecarClient.Current.PriceUpdated -= OnPriceUpdated;
            MarketSidecarClient.Current.StateChanged -= OnSidecarStateChanged;
            MarketSidecarClient.Current.StreamError -= OnStreamError;
            OnChainEngineClient.Current.PriceUpdated -= OnOnChainPriceUpdated;
            OnChainEngineClient.Current.StateChanged -= OnOnChainEngineStateChanged;
            if (_onChainStreams != null)
            {
                _onChainStreams.StateChanged -= OnOnChainRecoveryStateChanged;
                _onChainStreams.StreamError -= OnOnChainStreamError;
                _onChainStreams = null;
            }
            if (_evmStreams != null)
            {
                _evmStreams.StateChanged -= OnOnChainRecoveryStateChanged;
                _evmStreams.StreamError -= OnOnChainStreamError;
                _evmStreams = null;
            }
            _stateTimer.Stop();
            _stateTimer.Tick -= OnStateTimerTick;
            _onChainRenderTimer.Stop();
            _onChainRenderTimer.Tick -= OnOnChainRenderTimerTick;
            _pendingOnChainUpdates.Clear();
            _renderedOnChainSequences.Clear();
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
        }

        private void ApplyTextColor(Color color)
        {
            if (WidgetRoot.Resources["WidgetTextBrush"] is SolidColorBrush textBrush)
            {
                textBrush.Color = color;
            }
        }

        private void ApplyRowAppearance(Color color)
        {
            foreach (var row in Rows)
            {
                row.SetDefaultPriceColor(color);
                row.SetPriceFlashEnabled(_priceFlashOnChange);
            }
        }

        private void ApplyContentScale()
        {
            foreach (var row in Rows)
            {
                row.SetContentScale(_contentScale);
            }
            OverlayRowsControl.Margin = new Thickness(0, 4 * _contentScale, 0, 4 * _contentScale);
            if (HorizontalRowsControl.ItemsPanelRoot is WrapPanel panel)
            {
                panel.FirstLineEndInset = DockedBarLayoutRules.PriceTickerHorizontalControlsInsetDip * _contentScale;
            }
        }

        private void OnHorizontalRowsLoaded(object sender, RoutedEventArgs e) => ApplyContentScale();

        private void EnsureRows(SavedTickerInstrument[]? instruments)
        {
            var pairSignature = BuildInstrumentSignature(instruments);
            if (string.Equals(pairSignature, _currentPairSignature, StringComparison.Ordinal))
            {
                return;
            }

            _currentPairSignature = pairSignature;
            Rows.Clear();
            _rowsByStreamKey.Clear();
            _rowsByPoolKey.Clear();
            _pendingOnChainUpdates.Clear();
            _renderedOnChainSequences.Clear();
            _hasCentralizedRows = false;
            _hasOnChainRows = false;
            _hasSolanaRows = false;
            _evmChainIds.Clear();
            _hasReceivedPrice = false;
            _hasStreamError = false;
            _streamErrorMessage = string.Empty;
            _waitingSince = DateTimeOffset.UtcNow;
            _lastPriceAt = default;

            foreach (var instrument in instruments ?? [])
            {
                if (instrument.Kind == TickerInstrumentTypes.CentralizedMarket
                    && !string.IsNullOrWhiteSpace(instrument.Symbol)
                    && !string.IsNullOrWhiteSpace(instrument.VenueId))
                {
                    var row = new PriceTickerRow(
                        instrument.DisplayLabel,
                        "$--",
                        instrument.Symbol,
                        instrument.VenueId,
                        CentralizedAssetIconCatalog.GetIconUri(instrument.Symbol));
                    row.SetPriceFlashEnabled(_priceFlashOnChange);
                    row.SetContentScale(_contentScale);
                    Rows.Add(row);
                    _rowsByStreamKey[BuildStreamKey(instrument.VenueId, instrument.Symbol)] = row;
                    _hasCentralizedRows = true;
                    continue;
                }
                if (instrument.Kind == TickerInstrumentTypes.OnChainPool
                    && instrument.Pool != null)
                {
                    var row = new PriceTickerRow(
                        instrument.DisplayLabel,
                        "--",
                        instrument.SelectedMint ?? string.Empty,
                        instrument.Pool.PoolKey.ChainId,
                        instrument.IconUri);
                    row.SetPriceFlashEnabled(_priceFlashOnChange);
                    row.SetContentScale(_contentScale);
                    Rows.Add(row);
                    _rowsByPoolKey[BuildPoolKey(instrument.Pool.PoolKey)] = row;
                    _hasOnChainRows = true;
                    _hasSolanaRows |= instrument.Pool.PoolKey.ChainNamespace == ChainNamespaces.Solana;
                    if (instrument.Pool.PoolKey.ChainNamespace == ChainNamespaces.Eip155)
                    {
                        _evmChainIds.Add(instrument.Pool.PoolKey.ChainId);
                    }
                }
            }
        }

        private void OnPriceUpdated(object? sender, MarketPriceUpdatedEventArgs e)
        {
            var update = e.Update;
            if (_disposed
                || !_rowsByStreamKey.TryGetValue(
                    BuildStreamKey(update.Market.Venue.Id, update.Market.Symbol),
                    out var row))
            {
                return;
            }

            var formatted = PriceFormattingRules.Format(update.Value);
            DispatcherQueue.TryEnqueue(() => RunSafely(() =>
            {
                row.UpdatePrice(update.Value, formatted);
                row.PriceToolTip = $"{formatted}\nSource: {update.Source.Id}\nReceived: {update.ReceivedAtUtc:O}";
                _hasReceivedPrice = true;
                _hasStreamError = false;
                _streamErrorMessage = string.Empty;
                _lastPriceAt = update.ReceivedAtUtc;
                UpdateStatus();
            }));
        }

        private void OnSidecarStateChanged(object? sender, MarketSidecarStateChangedEventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            DispatcherQueue.TryEnqueue(() => RunSafely(() =>
            {
                if (e.State == MarketSidecarState.Reconnecting)
                {
                    _hasReceivedPrice = false;
                    _hasStreamError = false;
                    _streamErrorMessage = string.Empty;
                    _waitingSince = DateTimeOffset.UtcNow;
                    _lastPriceAt = default;
                }

                UpdateStatus();
            }));
        }

        private void OnOnChainPriceUpdated(object? sender, OnChainPriceUpdatedEventArgs e)
        {
            var update = e.Update;
            var key = BuildPoolKey(update.PoolKey);
            if (_disposed || !_rowsByPoolKey.ContainsKey(key))
            {
                return;
            }
            if (_renderedOnChainSequences.TryGetValue(key, out var rendered)
                && !IsNewer(update, rendered))
            {
                return;
            }
            var coalesced = false;
            _pendingOnChainUpdates.AddOrUpdate(
                key,
                update,
                (_, current) =>
                {
                    coalesced = true;
                    return IsNewer(update, (current.StreamEpoch, current.Sequence))
                        ? update
                        : current;
                });
            if (coalesced)
            {
                OnChainPipelineDiagnostics.RecordUiCoalesced();
            }
        }

        private void OnOnChainRenderTimerTick(DispatcherQueueTimer sender, object args)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var key in _pendingOnChainUpdates.Keys)
            {
                if (_pendingOnChainUpdates.TryRemove(key, out var update)
                    && _rowsByPoolKey.TryGetValue(key, out var row))
                {
                    ApplyOnChainPriceUpdate(key, row, update);
                }
            }
        }

        private void ApplyOnChainPriceUpdate(
            string key,
            PriceTickerRow row,
            OnChainPriceUpdate update)
        {
            var value = update.PriceUsd ?? update.PriceNative ?? update.PriceSol
                        ?? update.LastTradePriceQuote ?? update.SpotPriceQuote;
            if (value == null)
            {
                return;
            }
            var prefix = update.PriceUsd != null ? "$" : string.Empty;
            var formatted = PriceFormattingRules.FormatExact(
                value.Coefficient,
                value.Scale,
                prefix,
                string.Empty);
            var displayedKind = update.LastTradePriceQuote != null ? "last trade" : "pool spot";
            var referenceDetails = update.ReferenceSourceId == null
                ? string.Empty
                : $"\nUSD reference: {update.ReferenceSourceId}"
                  + (update.ReferenceObservedAtUnixMs.HasValue
                      ? $" at {DateTimeOffset.FromUnixTimeMilliseconds(update.ReferenceObservedAtUnixMs.Value):O}"
                      : string.Empty);
            var positionDetails = update.ChainPosition?.Kind == OnChainPositionKind.Evm
                                  && update.ChainPosition.BlockNumber.HasValue
                ? $"\nBlock: {update.ChainPosition.BlockNumber.Value} ({update.Finality?.Status ?? EvmFinality.Head})"
                : $"\nSlot: {update.Slot} ({update.Commitment})";
            var tooltip = $"{formatted} ({displayedKind})"
                          + $"\nPool: {update.PoolKey.PoolAddress}"
                          + positionDetails
                          + $"\nChain source: {update.SourceId}"
                          + $"\nStream: {update.RecoveryState}"
                          + (update.Stale ? " (stale)" : string.Empty)
                          + referenceDetails;
            row.UpdateExactPrice(value.Coefficient, value.Scale, formatted);
            row.PriceToolTip = tooltip;
            _renderedOnChainSequences[key] = (update.StreamEpoch, update.Sequence);
            OnChainPipelineDiagnostics.RecordUiRender(update.ObservedAtUnixMs);
            _hasReceivedPrice = true;
            _hasStreamError = false;
            _streamErrorMessage = string.Empty;
            _lastPriceAt = DateTimeOffset.FromUnixTimeMilliseconds(update.ObservedAtUnixMs);
            UpdateStatus();
        }

        private static bool IsNewer(
            OnChainPriceUpdate update,
            (ulong Epoch, ulong Sequence) current)
        {
            return update.StreamEpoch > current.Epoch
                   || (update.StreamEpoch == current.Epoch && update.Sequence > current.Sequence);
        }

        private void OnOnChainEngineStateChanged(object? sender, OnChainEngineStateChangedEventArgs e)
        {
            if (!_disposed && _hasOnChainRows)
            {
                DispatcherQueue.TryEnqueue(() => RunSafely(UpdateStatus));
            }
        }

        private void OnOnChainRecoveryStateChanged(object? sender, OnChainRecoveryStateChangedEventArgs e)
        {
            if (!_disposed && _hasOnChainRows)
            {
                DispatcherQueue.TryEnqueue(() => RunSafely(UpdateStatus));
            }
        }

        private void OnOnChainStreamError(object? sender, string message)
        {
            if (_disposed || !_hasOnChainRows)
            {
                return;
            }
            DispatcherQueue.TryEnqueue(() => RunSafely(() =>
            {
                _hasStreamError = true;
                _streamErrorMessage = message;
                UpdateStatus();
            }));
        }

        private void OnStreamError(object? sender, MarketStreamErrorEventArgs e)
        {
            if (_disposed
                || (!string.IsNullOrWhiteSpace(e.VenueId)
                    && !Rows.Any(row => string.Equals(row.VenueId, e.VenueId, StringComparison.OrdinalIgnoreCase))))
            {
                return;
            }

            DispatcherQueue.TryEnqueue(() => RunSafely(() =>
            {
                _hasStreamError = true;
                _streamErrorMessage = e.Message;
                UpdateStatus();
            }));
        }

        private void OnStateTimerTick(DispatcherQueueTimer sender, object args)
        {
            RunSafely(UpdateStatus);
        }

        private void UpdateStatus()
        {
            var status = string.IsNullOrWhiteSpace(_widgetErrorMessage)
                ? WidgetContentStateRules.EvaluatePriceTicker(
                    GetAggregateFeedState(),
                    Rows.Count > 0,
                    _hasReceivedPrice,
                    _hasStreamError,
                    _waitingSince,
                    _lastPriceAt,
                    DateTimeOffset.UtcNow)
                : new WidgetContentStatus(WidgetContentState.Error, "Error");
            if (status == _contentStatus)
            {
                return;
            }
            _contentStatus = status;
            ContentStatusChanged?.Invoke(this, EventArgs.Empty);
        }

        private MarketSidecarState GetAggregateFeedState()
        {
            if (_hasCentralizedRows && MarketSidecarClient.Current.State == MarketSidecarState.Connected)
            {
                return MarketSidecarState.Connected;
            }
            if ((_hasSolanaRows && _onChainStreams?.State == OnChainRecoveryState.Live)
                || (_evmChainIds.Count > 0
                    && _evmStreams?.GetStateFor(_evmChainIds) == OnChainRecoveryState.Live))
            {
                return MarketSidecarState.Connected;
            }
            if ((_hasCentralizedRows && MarketSidecarClient.Current.State == MarketSidecarState.Reconnecting)
                || (_hasSolanaRows && IsRecovering(_onChainStreams?.State))
                || (_evmChainIds.Count > 0 && IsRecovering(_evmStreams?.GetStateFor(_evmChainIds))))
            {
                return MarketSidecarState.Reconnecting;
            }
            if ((_hasCentralizedRows && MarketSidecarClient.Current.State == MarketSidecarState.Connecting)
                || (_hasSolanaRows && _onChainStreams?.State == OnChainRecoveryState.Connecting)
                || (_evmChainIds.Count > 0
                    && _evmStreams?.GetStateFor(_evmChainIds) == OnChainRecoveryState.Connecting))
            {
                return MarketSidecarState.Connecting;
            }
            return MarketSidecarState.Unavailable;
        }

        private static bool IsRecovering(OnChainRecoveryState? state)
        {
            return state is OnChainRecoveryState.Reconnecting
                or OnChainRecoveryState.Replaying
                or OnChainRecoveryState.Reconciling;
        }

        private void RunSafely(Action action)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                action();
            }
            catch (Exception exception)
            {
                _widgetErrorMessage = string.IsNullOrWhiteSpace(exception.Message)
                    ? "Price Ticker could not be displayed."
                    : exception.Message;
                try
                {
                    UpdateStatus();
                }
                catch
                {
                }
            }
        }

        private static string BuildInstrumentSignature(SavedTickerInstrument[]? instruments)
        {
            return instruments == null || instruments.Length == 0
                ? string.Empty
                : string.Join("\n", instruments.Select(instrument =>
                    $"{SavedWidgetCatalogRules.GetInstrumentKey(instrument)}|{instrument.DisplayLabel}|{instrument.IconUri}"));
        }

        private static string BuildStreamKey(string venueId, string symbol)
        {
            return $"{venueId}|{symbol}".ToUpperInvariant();
        }

        private static string BuildPoolKey(OnChainPoolKey poolKey)
        {
            return $"{poolKey.ChainNamespace}|{poolKey.ChainId}|{poolKey.ProtocolId}|{poolKey.PoolAddress}";
        }

    }
}

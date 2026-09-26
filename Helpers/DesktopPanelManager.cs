using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using TrenchHQ.Models;

namespace TrenchHQ.Helpers
{
    internal sealed record PanelShortcutAssignment(string Id, bool IsDocked, string Shortcut);

    internal sealed class DesktopPanelManager
    {
        private readonly Dictionary<string, Overlay> _overlayWindows = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DockedBarWindow> _dockedBarWindows = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _closedOverlays = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _closedDockedBars = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _saveLock = new(1, 1);
        private readonly SemaphoreSlim _subscriptionLock = new(1, 1);
        private readonly SemaphoreSlim _appBarRecoveryLock = new(1, 1);
        private readonly SavedWidgetCatalogService _widgetCatalog;
        private readonly OnChainProviderConfigurationService _onChainProviders;
        private readonly OnChainStreamCoordinator _onChainStreams;
        private readonly EvmStreamCoordinatorCollection _evmStreams;
        private readonly WalletActivityService _walletActivity;
        private DesktopSetup _settings = new();
        private int _activeSolanaPoolCount;
        private string[] _activeEvmChainIds = [];

        internal DesktopPanelManager(
            SavedWidgetCatalogService widgetCatalog,
            OnChainProviderConfigurationService onChainProviders,
            OnChainStreamCoordinator onChainStreams,
            EvmStreamCoordinatorCollection evmStreams,
            WalletActivityService walletActivity)
        {
            _widgetCatalog = widgetCatalog;
            _onChainProviders = onChainProviders;
            _onChainStreams = onChainStreams;
            _evmStreams = evmStreams;
            _walletActivity = walletActivity;
            _widgetCatalog.Changed += OnWidgetCatalogChanged;
            _onChainProviders.Changed += OnProviderConfigurationsChanged;
            _onChainStreams.ProviderFailed += OnProviderFailed;
            _evmStreams.ProviderFailed += OnProviderFailed;
            MarketSidecarClient.Current.PriceUpdated += OnReferencePriceUpdated;
        }

        internal event EventHandler? StateChanged;
        internal event EventHandler? DefinitionsChanged;
        internal event EventHandler? DisplayEnvironmentChanged;

        internal bool IsAnyRunning => _overlayWindows.Count > 0 || _dockedBarWindows.Count > 0;
        internal int ActiveOverlayCount => _overlayWindows.Count;
        internal int ActiveDockedBarCount => _dockedBarWindows.Count;
        internal int ActiveSubscriptionCount { get; private set; }
        internal bool HasEnabledPanels => _settings.Overlays.Any(item => item.Enabled && HasDisplayContent(item))
                                            || _settings.DockedBars.Any(item => item.Enabled && HasDisplayContent(item));

        internal async Task InitializeAsync()
        {
            _settings = await DesktopSetupService.LoadOrCreateAsync(_widgetCatalog);
            DefinitionsChanged?.Invoke(this, EventArgs.Empty);
        }

        internal void NotifyDisplayEnvironmentChanged()
        {
            DisplayEnvironmentChanged?.Invoke(this, EventArgs.Empty);
        }

        internal OverlayDefinition[] GetDefinitions()
        {
            return _settings.Overlays.Select(static item => DesktopSetupService.Clone(item)).ToArray();
        }

        internal DockedBarDefinition[] GetDockedBarDefinitions()
        {
            var monitors = WindowsAppBarNative.GetMonitors();
            return _settings.DockedBars.Select(item =>
            {
                var copy = DesktopSetupService.Clone(item);
                copy.Name = WindowsAppBarNative.GetPanelName(copy.Edge, copy.MonitorDeviceName, monitors);
                return copy;
            }).ToArray();
        }

        internal bool IsRunning(string id) => _overlayWindows.ContainsKey(id);

        internal bool IsOverlayMinimized(string id) =>
            _overlayWindows.TryGetValue(id, out var window) && window.IsMinimized;

        internal bool IsDockedBarRunning(string id) => _dockedBarWindows.ContainsKey(id);

        internal bool IsDockedBarMinimized(string id) =>
            _dockedBarWindows.TryGetValue(id, out var window) && window.IsMinimized;

        internal int GetWidgetUsageCount(string widgetId)
        {
            return _settings.Overlays.Count(item => item.WidgetIds.Contains(widgetId, StringComparer.OrdinalIgnoreCase))
                   + _settings.DockedBars.Count(item => item.WidgetIds.Contains(widgetId, StringComparer.OrdinalIgnoreCase));
        }

        internal bool HasDisplayContent(OverlayDefinition definition) =>
            DesktopSetupRules.HasDisplayContent(definition.UseApplicationWindow, definition.WidgetIds, _widgetCatalog.GetWidgets());

        internal bool HasDisplayContent(DockedBarDefinition definition) =>
            DesktopSetupRules.HasDisplayContent(definition.UseApplicationWindow, definition.WidgetIds, _widgetCatalog.GetWidgets());

        internal string GetPanelContentName(bool useApplicationWindow, IEnumerable<string>? widgetIds)
        {
            return useApplicationWindow
                ? "Application window"
                : _widgetCatalog.FindFirst(widgetIds)?.Name ?? "Choose content in Desktop Setup";
        }

        internal PanelShortcutAssignment[] GetShortcutAssignments()
        {
            return
            [
                .. _settings.Overlays
                    .Where(item => HasDisplayContent(item) && !string.IsNullOrWhiteSpace(item.Shortcut))
                    .Select(static item => new PanelShortcutAssignment(item.Id, false, item.Shortcut)),
                .. _settings.DockedBars
                    .Where(item => HasDisplayContent(item) && !string.IsNullOrWhiteSpace(item.Shortcut))
                    .Select(static item => new PanelShortcutAssignment(item.Id, true, item.Shortcut))
            ];
        }

        internal string GetShortcut(string id, bool isDocked)
        {
            return isDocked ? FindDockedBar(id)?.Shortcut ?? string.Empty : FindOverlay(id)?.Shortcut ?? string.Empty;
        }

        internal bool IsShortcutAvailable(string shortcut, string panelId, bool isDocked)
        {
            var normalized = PanelShortcutRules.Normalize(shortcut);
            return normalized.Length > 0
                   && !GetShortcutAssignments().Any(item =>
                       !(item.IsDocked == isDocked && string.Equals(item.Id, panelId, StringComparison.OrdinalIgnoreCase))
                       && string.Equals(item.Shortcut, normalized, StringComparison.OrdinalIgnoreCase));
        }

        internal async Task ClearShortcutAsync(string id, bool isDocked)
        {
            await SetShortcutAsync(id, isDocked, string.Empty);
        }

        internal async Task SetShortcutAsync(string id, bool isDocked, string shortcut)
        {
            if (isDocked)
            {
                var definition = FindDockedBar(id);
                if (definition == null || definition.Shortcut == shortcut)
                {
                    return;
                }

                definition.Shortcut = shortcut;
            }
            else
            {
                var definition = FindOverlay(id);
                if (definition == null || definition.Shortcut == shortcut)
                {
                    return;
                }

                definition.Shortcut = shortcut;
            }

            await SaveAndNotifyAsync();
        }

        internal async Task<OverlayDefinition> AddAsync()
        {
            var definition = DesktopSetupService.CreateOverlay(
                DesktopSetupRules.GetOverlayName(_settings.Overlays.Length),
                null);
            _settings.Overlays = [.. _settings.Overlays, definition];
            await SaveAndNotifyAsync();
            return DesktopSetupService.Clone(definition);
        }

        internal async Task SaveDefinitionAsync(OverlayDefinition updated)
        {
            var existing = FindOverlay(updated.Id);
            if (existing == null)
            {
                return;
            }

            var normalized = DesktopSetupService.Clone(updated);
            var index = Array.FindIndex(_settings.Overlays, item => string.Equals(item.Id, updated.Id, StringComparison.OrdinalIgnoreCase));
            _settings.Overlays[index] = normalized;
            if (_overlayWindows.TryGetValue(updated.Id, out var window))
            {
                window.ApplyDefinition(normalized, ResolveWidget(normalized.WidgetIds));
            }

            await SaveAndNotifyAsync();
            await RefreshSubscriptionsAsync();
        }

        internal async Task DeleteAsync(string id)
        {
            Close(id);
            _closedOverlays.Remove(id);
            _settings.Overlays = _settings.Overlays
                .Where(item => !string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            await SaveAndNotifyAsync();
        }

        internal async Task<DockedBarDefinition?> AddDockedBarAsync()
        {
            var placement = FindAvailableDockedBarPlacement();
            if (!placement.HasValue)
            {
                return null;
            }

            var definition = DesktopSetupService.CreateDockedBar(
                DesktopSetupRules.GetDockedBarName(placement.Value.Edge),
                null,
                placement.Value.MonitorDeviceName,
                placement.Value.Edge);
            _settings.DockedBars = [.. _settings.DockedBars, definition];
            await SaveAndNotifyAsync();
            return DesktopSetupService.Clone(definition);
        }

        internal async Task<bool> SaveDockedBarDefinitionAsync(DockedBarDefinition updated)
        {
            var existing = FindDockedBar(updated.Id);
            if (existing == null)
            {
                return false;
            }

            var normalized = DesktopSetupService.Clone(updated);
            normalized.MonitorDeviceName = WindowsAppBarNative.ResolveMonitorDeviceName(normalized.MonitorDeviceName);
            normalized.Edge = DockedBarLayoutRules.NormalizeEdge(normalized.Edge);
            normalized.ThicknessPx = DockedBarLayoutRules.ClampThickness(normalized.Edge, normalized.ThicknessPx);
            if (HasDockedBarPlacementConflict(normalized.Id, normalized.MonitorDeviceName, normalized.Edge))
            {
                return false;
            }

            var index = Array.FindIndex(_settings.DockedBars, item => string.Equals(item.Id, updated.Id, StringComparison.OrdinalIgnoreCase));
            _settings.DockedBars[index] = normalized;
            if (_dockedBarWindows.TryGetValue(updated.Id, out var window))
            {
                window.ApplyDefinition(normalized, ResolveWidget(normalized.WidgetIds));
            }

            await SaveAndNotifyAsync();
            await RefreshSubscriptionsAsync();
            return true;
        }

        internal async Task DeleteDockedBarAsync(string id)
        {
            CloseDockedBar(id);
            _closedDockedBars.Remove(id);
            _settings.DockedBars = _settings.DockedBars
                .Where(item => !string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            await SaveAndNotifyAsync();
        }

        internal async Task SetOverlayEnabledAsync(string id, bool enabled)
        {
            var definition = FindOverlay(id);
            var resolved = enabled && definition != null && HasDisplayContent(definition);
            if (definition == null || definition.Enabled == resolved)
            {
                return;
            }

            definition.Enabled = resolved;
            await SaveAndNotifyAsync();
        }

        internal async Task SetDockedBarEnabledAsync(string id, bool enabled)
        {
            var definition = FindDockedBar(id);
            var resolved = enabled && definition != null && HasDisplayContent(definition);
            if (definition == null || definition.Enabled == resolved)
            {
                return;
            }

            definition.Enabled = resolved;
            await SaveAndNotifyAsync();
        }

        internal async Task StartEnabledAsync()
        {
            foreach (var definition in _settings.Overlays.Where(item => item.Enabled && HasDisplayContent(item)))
            {
                try
                {
                    Show(definition.Id, activate: false);
                }
                catch
                {
                    Close(definition.Id);
                }
            }

            await RefreshSubscriptionsAsync();
        }

        internal async Task StartEnabledDockedBarsAsync()
        {
            foreach (var definition in _settings.DockedBars.Where(item => item.Enabled && HasDisplayContent(item)))
            {
                await ShowDockedBarAsync(definition.Id, activate: false);
            }

            await RefreshSubscriptionsAsync();
        }

        internal async Task TogglePanelAsync(string id, bool isDocked)
        {
            if (isDocked)
            {
                if (_closedDockedBars.Contains(id))
                {
                    return;
                }

                if (_dockedBarWindows.TryGetValue(id, out var window) && !window.IsMinimized)
                {
                    MinimizeDockedBar(id);
                }
                else
                {
                    await ShowDockedBarAsync(id, activate: false);
                }

                return;
            }

            if (_closedOverlays.Contains(id))
            {
                return;
            }

            if (_overlayWindows.TryGetValue(id, out var overlay) && !overlay.IsMinimized)
            {
                MinimizeOverlay(id);
            }
            else
            {
                Show(id, activate: false);
            }
        }

        internal void MinimizeOverlay(string id)
        {
            if (_overlayWindows.TryGetValue(id, out var window) && !window.IsMinimized)
            {
                window.Minimize();
                NotifyStateChanged();
            }
        }

        internal void MinimizeDockedBar(string id)
        {
            if (_dockedBarWindows.TryGetValue(id, out var window) && !window.IsMinimized)
            {
                window.Minimize();
                NotifyStateChanged();
            }
        }

        internal void Show(string id, bool activate = true)
        {
            var definition = FindOverlay(id);
            if (definition == null || !HasDisplayContent(definition))
            {
                return;
            }

            _closedOverlays.Remove(id);

            if (!_overlayWindows.TryGetValue(id, out var window))
            {
                var runtimeDefinition = DesktopSetupService.Clone(definition);
                window = new Overlay(runtimeDefinition, ResolveWidget(runtimeDefinition.WidgetIds), this);
                _overlayWindows[id] = window;
            }

            window.Show(activate);
            NotifyStateChanged();
        }

        internal async Task<bool> ShowDockedBarAsync(string id, bool activate = true)
        {
            try
            {
                var definition = await RecoverDockedBarPlacementAsync(id);
                if (definition == null || !HasDisplayContent(definition))
                {
                    return false;
                }

                _closedDockedBars.Remove(id);

                var monitor = WindowsAppBarNative.ResolveMonitorDeviceName(definition.MonitorDeviceName);
                if (HasRunningDockedBarPlacementConflict(id, monitor, definition.Edge))
                {
                    return false;
                }

                if (!_dockedBarWindows.TryGetValue(id, out var window))
                {
                    var runtimeDefinition = DesktopSetupService.Clone(definition);
                    runtimeDefinition.MonitorDeviceName = monitor;
                    window = new DockedBarWindow(runtimeDefinition, ResolveWidget(runtimeDefinition.WidgetIds), this);
                    _dockedBarWindows[id] = window;
                }

                window.Show(activate);
                NotifyStateChanged();
                return true;
            }
            catch
            {
                try
                {
                    CloseDockedBar(id);
                }
                catch
                {
                }

                return false;
            }
        }

        internal void Close(string id)
        {
            if (_overlayWindows.TryGetValue(id, out var window))
            {
                _closedOverlays.Add(id);
                window.Close();
            }
        }

        internal void CloseDockedBar(string id)
        {
            if (_dockedBarWindows.TryGetValue(id, out var window))
            {
                _closedDockedBars.Add(id);
                window.Close();
            }
        }

        internal void CloseAll()
        {
            _closedOverlays.UnionWith(_overlayWindows.Keys);
            _closedDockedBars.UnionWith(_dockedBarWindows.Keys);
            foreach (var window in _overlayWindows.Values.ToArray())
            {
                try
                {
                    window.Close();
                }
                catch
                {
                }
            }

            foreach (var window in _dockedBarWindows.Values.ToArray())
            {
                try
                {
                    window.Close();
                }
                catch
                {
                }
            }
        }

        internal async Task CloseAllAsync()
        {
            CloseAll();
            await RefreshSubscriptionsAsync();
        }

        internal void NotifyClosed(string id, Overlay window)
        {
            if (_overlayWindows.TryGetValue(id, out var current) && ReferenceEquals(current, window))
            {
                _closedOverlays.Add(id);
                _overlayWindows.Remove(id);
                NotifyStateChanged();
            }
        }

        internal void NotifyDockedBarClosed(string id, DockedBarWindow window)
        {
            if (_dockedBarWindows.TryGetValue(id, out var current) && ReferenceEquals(current, window))
            {
                _closedDockedBars.Add(id);
                _dockedBarWindows.Remove(id);
                NotifyStateChanged();
            }
        }

        internal async Task UpdatePositionAsync(string id, int x, int y, string monitorDeviceName)
        {
            var definition = FindOverlay(id);
            if (definition == null)
            {
                return;
            }

            definition.PositionX = x;
            definition.PositionY = y;
            definition.MonitorDeviceName = monitorDeviceName ?? string.Empty;
            await PersistAsync();
        }

        internal async Task<DockedBarDefinition?> RecoverDockedBarPlacementAsync(string id)
        {
            await _appBarRecoveryLock.WaitAsync();
            try
            {
                var definition = FindDockedBar(id);
                if (definition == null)
                {
                    return null;
                }

                var monitors = WindowsAppBarNative.GetMonitors();
                if (monitors.Length == 0)
                {
                    return null;
                }

                var currentMonitor = monitors.FirstOrDefault(item =>
                    string.Equals(item.DeviceName, definition.MonitorDeviceName, StringComparison.OrdinalIgnoreCase));
                if (currentMonitor != null)
                {
                    return DesktopSetupService.Clone(definition);
                }

                var resolvedMonitor = ResolveAvailableMonitor(definition.MonitorDeviceName, monitors);
                var occupied = _settings.DockedBars
                    .Select(item =>
                    {
                        var monitor = ResolveConfiguredMonitor(item.MonitorDeviceName, monitors);
                        return monitor == null
                            ? (DockedBarOccupiedPlacement?)null
                            : new DockedBarOccupiedPlacement(item.Id, monitor, item.Edge);
                    })
                    .OfType<DockedBarOccupiedPlacement>();
                var placement = DockedBarPlacementRules.FindAvailable(
                    monitors.Select(static item => new DockedBarMonitorCandidate(item.DeviceName, item.IsPrimary)),
                    occupied,
                    excludedDefinitionId: definition.Id,
                    preferredMonitor: resolvedMonitor,
                    preferredEdge: definition.Edge);
                if (!placement.HasValue)
                {
                    return null;
                }

                var recovered = DesktopSetupService.Clone(definition);
                recovered.MonitorDeviceName = placement.Value.MonitorDeviceName;
                recovered.Edge = placement.Value.Edge;
                recovered.ThicknessPx = DockedBarLayoutRules.ClampThickness(recovered.Edge, recovered.ThicknessPx);
                var changed = !string.Equals(definition.MonitorDeviceName, recovered.MonitorDeviceName, StringComparison.OrdinalIgnoreCase)
                              || !string.Equals(definition.Edge, recovered.Edge, StringComparison.OrdinalIgnoreCase)
                              || definition.ThicknessPx != recovered.ThicknessPx;
                if (changed)
                {
                    var index = Array.FindIndex(_settings.DockedBars, item =>
                        string.Equals(item.Id, definition.Id, StringComparison.OrdinalIgnoreCase));
                    _settings.DockedBars[index] = recovered;
                    await SaveAndNotifyAsync();
                }

                return DesktopSetupService.Clone(recovered);
            }
            finally
            {
                _appBarRecoveryLock.Release();
            }
        }

        private OverlayDefinition? FindOverlay(string id)
        {
            return _settings.Overlays.FirstOrDefault(item =>
                string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        private DockedBarDefinition? FindDockedBar(string id)
        {
            return _settings.DockedBars.FirstOrDefault(item =>
                string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        private (string MonitorDeviceName, string Edge)? FindAvailableDockedBarPlacement(string? preferredMonitor = null)
        {
            var monitors = WindowsAppBarNative.GetMonitors();
            var occupied = _settings.DockedBars
                .Select(item =>
                {
                    var monitor = ResolveConfiguredMonitor(item.MonitorDeviceName, monitors);
                    return monitor == null
                        ? (DockedBarOccupiedPlacement?)null
                        : new DockedBarOccupiedPlacement(item.Id, monitor, item.Edge);
                })
                .OfType<DockedBarOccupiedPlacement>();
            return DockedBarPlacementRules.FindAvailable(
                monitors.Select(static item => new DockedBarMonitorCandidate(item.DeviceName, item.IsPrimary)),
                occupied,
                preferredMonitor: preferredMonitor);
        }

        private static string ResolveAvailableMonitor(
            string? requestedDeviceName,
            WindowsAppBarNative.DisplayMonitorInfo[] monitors)
        {
            return monitors.FirstOrDefault(item =>
                       string.Equals(item.DeviceName, requestedDeviceName, StringComparison.OrdinalIgnoreCase))?.DeviceName
                   ?? monitors.FirstOrDefault(static item => item.IsPrimary)?.DeviceName
                   ?? monitors[0].DeviceName;
        }

        private static string? ResolveConfiguredMonitor(
            string? requestedDeviceName,
            WindowsAppBarNative.DisplayMonitorInfo[] monitors)
        {
            if (string.IsNullOrWhiteSpace(requestedDeviceName))
            {
                return monitors.FirstOrDefault(static item => item.IsPrimary)?.DeviceName
                       ?? monitors.FirstOrDefault()?.DeviceName;
            }

            return monitors.FirstOrDefault(item =>
                string.Equals(item.DeviceName, requestedDeviceName, StringComparison.OrdinalIgnoreCase))?.DeviceName;
        }

        private bool HasDockedBarPlacementConflict(string id, string monitorDeviceName, string edge)
        {
            var normalizedEdge = DockedBarLayoutRules.NormalizeEdge(edge);
            return _settings.DockedBars.Any(item =>
                !string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    WindowsAppBarNative.ResolveMonitorDeviceName(item.MonitorDeviceName),
                    monitorDeviceName,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    DockedBarLayoutRules.NormalizeEdge(item.Edge),
                    normalizedEdge,
                    StringComparison.OrdinalIgnoreCase));
        }

        private bool HasRunningDockedBarPlacementConflict(string id, string monitorDeviceName, string edge)
        {
            var normalizedEdge = DockedBarLayoutRules.NormalizeEdge(edge);
            return _dockedBarWindows.Keys.Any(runningId =>
            {
                if (string.Equals(runningId, id, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var running = FindDockedBar(runningId);
                return running != null
                       && string.Equals(
                           WindowsAppBarNative.ResolveMonitorDeviceName(running.MonitorDeviceName),
                           monitorDeviceName,
                           StringComparison.OrdinalIgnoreCase)
                       && string.Equals(
                           DockedBarLayoutRules.NormalizeEdge(running.Edge),
                           normalizedEdge,
                           StringComparison.OrdinalIgnoreCase);
            });
        }

        private async Task SaveAndNotifyAsync()
        {
            ApplyGeneratedNames();
            await PersistAsync();
            DefinitionsChanged?.Invoke(this, EventArgs.Empty);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ApplyGeneratedNames()
        {
            for (var index = 0; index < _settings.Overlays.Length; index++)
            {
                var definition = _settings.Overlays[index];
                var name = DesktopSetupRules.GetOverlayName(index);
                if (string.Equals(definition.Name, name, StringComparison.Ordinal))
                {
                    continue;
                }

                definition.Name = name;
                if (_overlayWindows.TryGetValue(definition.Id, out var window))
                {
                    var runtimeDefinition = DesktopSetupService.Clone(definition);
                    window.ApplyDefinition(runtimeDefinition, ResolveWidget(runtimeDefinition.WidgetIds));
                }
            }

            for (var index = 0; index < _settings.DockedBars.Length; index++)
            {
                var definition = _settings.DockedBars[index];
                var name = DesktopSetupRules.GetDockedBarName(definition.Edge);
                if (string.Equals(definition.Name, name, StringComparison.Ordinal))
                {
                    continue;
                }

                definition.Name = name;
                if (_dockedBarWindows.TryGetValue(definition.Id, out var window))
                {
                    var runtimeDefinition = DesktopSetupService.Clone(definition);
                    window.ApplyDefinition(runtimeDefinition, ResolveWidget(runtimeDefinition.WidgetIds));
                }
            }
        }

        private async Task PersistAsync()
        {
            await _saveLock.WaitAsync();
            try
            {
                await DesktopSetupService.SaveAsync(_settings);
            }
            finally
            {
                _saveLock.Release();
            }
        }

        private void NotifyStateChanged()
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            _ = RefreshSubscriptionsAsync();
        }

        private async Task RefreshSubscriptionsAsync()
        {
            await _subscriptionLock.WaitAsync();
            try
            {
                var overlayDefinitions = _overlayWindows
                    .Where(static item => !item.Value.IsMinimized)
                    .Select(item => FindOverlay(item.Key))
                    .OfType<OverlayDefinition>();
                var dockedBarDefinitions = _dockedBarWindows
                    .Where(static item => !item.Value.IsMinimized)
                    .Select(item => FindDockedBar(item.Key))
                    .OfType<DockedBarDefinition>();
                var widgets = _widgetCatalog.GetWidgets();
                var subscriptions = WidgetSubscriptionPlanner.Build(
                    overlayDefinitions,
                    dockedBarDefinitions,
                    widgets);
                var onChainPlans = OnChainWidgetSubscriptionPlanner.BuildWithModes(
                    overlayDefinitions,
                    dockedBarDefinitions,
                    widgets);
                var onChainPools = onChainPlans.Select(static plan => plan.Selection).ToArray();
                var trackedWallets = WalletWidgetSubscriptionPlanner.Build(
                    overlayDefinitions,
                    dockedBarDefinitions,
                    widgets);
                var solanaPools = onChainPools.Where(static pool =>
                    pool.Descriptor.PoolKey.ChainNamespace == ChainNamespaces.Solana
                    && pool.Descriptor.PoolKey.ChainId == "mainnet-beta").ToArray();
                var solanaWebSocketPools = onChainPlans
                    .Where(static plan => plan.Mode == OnChainPriceModes.WebSocket
                                          && plan.Selection.Descriptor.PoolKey.ChainNamespace == ChainNamespaces.Solana)
                    .Select(static plan => plan.Selection.Descriptor.PoolKey.PoolAddress)
                    .ToHashSet(StringComparer.Ordinal);
                var evmPoolsByChain = onChainPools
                    .Where(static pool =>
                        pool.Descriptor.PoolKey.ChainNamespace == ChainNamespaces.Eip155)
                    .GroupBy(static pool => pool.Descriptor.PoolKey.ChainId, StringComparer.Ordinal)
                    .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
                _onChainProviders.SetDemand(ChainNamespaces.Solana, "mainnet-beta",
                    solanaWebSocketPools.Count > 0,
                    trackedWallets.Any(wallet => wallet.ChainNamespace == ChainNamespaces.Solana));
                foreach (var chain in _evmStreams.Coordinators.Select(coordinator => coordinator.ChainDefinition))
                    _onChainProviders.SetDemand(chain.ChainNamespace, chain.ChainId,
                        onChainPlans.Any(plan => plan.Mode == OnChainPriceModes.WebSocket
                            && plan.Selection.Descriptor.PoolKey.ChainNamespace == chain.ChainNamespace
                            && plan.Selection.Descriptor.PoolKey.ChainId == chain.ChainId),
                        trackedWallets.Any(wallet => wallet.ChainNamespace == chain.ChainNamespace && wallet.ChainId == chain.ChainId));
                var solanaConfiguration = _onChainProviders.GetSelectedConfiguration(
                    ChainNamespaces.Solana,
                    "mainnet-beta");
                ActiveSubscriptionCount = subscriptions.Length + onChainPools.Length + trackedWallets.Length;
                Volatile.Write(ref _activeSolanaPoolCount, solanaPools.Length);
                _activeEvmChainIds = _evmStreams.Coordinators
                    .Select(static coordinator => coordinator.ChainDefinition.ChainId)
                    .Where(evmPoolsByChain.ContainsKey)
                    .ToArray();

                var centralizedSubscriptions = subscriptions
                    .Select(static item => new MarketSidecarSubscription
                    {
                        VenueId = item.VenueId,
                        Symbol = item.Symbol
                    })
                    .ToList();
                if (solanaPools.Length > 0
                    && !centralizedSubscriptions.Any(static item =>
                        string.Equals(item.VenueId, "binance", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(item.Symbol, "SOL/USDT", StringComparison.OrdinalIgnoreCase)))
                {
                    centralizedSubscriptions.Add(new MarketSidecarSubscription
                    {
                        VenueId = "binance",
                        Symbol = "SOL/USDT"
                    });
                }
                var evmReferenceMarkets = _evmStreams.Coordinators
                    .Where(coordinator => evmPoolsByChain.ContainsKey(
                        coordinator.ChainDefinition.ChainId))
                    .Select(static coordinator => coordinator.ChainDefinition.CentralizedUsdMarket)
                    .OfType<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var evmReferenceMarket in evmReferenceMarkets)
                {
                    if (!centralizedSubscriptions.Any(item =>
                            string.Equals(item.VenueId, "binance", StringComparison.OrdinalIgnoreCase)
                            && string.Equals(
                                item.Symbol,
                                evmReferenceMarket,
                                StringComparison.OrdinalIgnoreCase)))
                    {
                        centralizedSubscriptions.Add(new MarketSidecarSubscription
                        {
                            VenueId = "binance",
                            Symbol = evmReferenceMarket
                        });
                    }
                }

                try
                {
                    await MarketSidecarClient.Current.SetSubscriptionsAsync(centralizedSubscriptions);
                }
                catch
                {
                }

                if (solanaConfiguration == null || solanaPools.Length == 0)
                {
                    await _onChainStreams.StopAsync();
                }
                else
                {
                    try
                    {
                        var apiKey = await _onChainProviders.GetCredentialAsync(solanaConfiguration);
                        await _onChainStreams.StartAsync(solanaConfiguration, apiKey, solanaPools,
                            webSocketPoolAddresses: solanaWebSocketPools);
                    }
                    catch
                    {
                        await _onChainStreams.StopAsync();
                        await _onChainProviders.TryFailoverAsync(
                            solanaConfiguration.Id,
                            OnChainProviderFailureKind.Authentication);
                    }
                }

                foreach (var coordinator in _evmStreams.Coordinators)
                {
                    var chain = coordinator.ChainDefinition;
                    evmPoolsByChain.TryGetValue(chain.ChainId, out var chainPools);
                    var configuration = _onChainProviders.GetSelectedConfiguration(
                        chain.ChainNamespace,
                        chain.ChainId);
                    try
                    {
                        if (configuration == null || chainPools == null || chainPools.Length == 0)
                        {
                            await _evmStreams.StopAsync(chain.ChainId);
                        }
                        else
                        {
                            var apiKey = await _onChainProviders.GetCredentialAsync(configuration);
                            await _evmStreams.StartAsync(
                                chain.ChainId,
                                configuration,
                                apiKey,
                                chainPools,
                                webSocketPoolIds: onChainPlans
                                    .Where(plan => plan.Mode == OnChainPriceModes.WebSocket
                                                   && plan.Selection.Descriptor.PoolKey.ChainId == chain.ChainId)
                                    .Select(static plan => plan.Selection.Descriptor.PoolKey.PoolId)
                                    .ToHashSet(StringComparer.OrdinalIgnoreCase));
                        }
                    }
                    catch
                    {
                        await _evmStreams.StopAsync(chain.ChainId);
                        if (configuration != null)
                        {
                            await _onChainProviders.TryFailoverAsync(
                                configuration.Id,
                                OnChainProviderFailureKind.Authentication);
                        }
                    }
                }
                await _walletActivity.RefreshAsync(trackedWallets);
            }
            catch
            {
            }
            finally
            {
                _subscriptionLock.Release();
            }
        }

        private SavedWidgetDefinition? ResolveWidget(IEnumerable<string>? widgetIds)
        {
            return _widgetCatalog.FindFirst(widgetIds);
        }

        private void OnWidgetCatalogChanged(object? sender, EventArgs e)
        {
            foreach (var (id, window) in _overlayWindows.ToArray())
            {
                var definition = FindOverlay(id);
                if (definition != null)
                {
                    var runtimeDefinition = DesktopSetupService.Clone(definition);
                    window.ApplyDefinition(runtimeDefinition, ResolveWidget(runtimeDefinition.WidgetIds));
                }
            }

            foreach (var (id, window) in _dockedBarWindows.ToArray())
            {
                var definition = FindDockedBar(id);
                if (definition != null)
                {
                    var runtimeDefinition = DesktopSetupService.Clone(definition);
                    window.ApplyDefinition(runtimeDefinition, ResolveWidget(runtimeDefinition.WidgetIds));
                }
            }

            DefinitionsChanged?.Invoke(this, EventArgs.Empty);
            _ = RefreshSubscriptionsAsync();
        }

        private void OnProviderConfigurationsChanged(object? sender, EventArgs e)
        {
            _ = RefreshSubscriptionsAsync();
        }

        private void OnProviderFailed(object? sender, OnChainProviderFailureEventArgs e)
        {
            _ = _onChainProviders.TryFailoverAsync(e.ConfigurationId, e.Kind);
        }

        private void OnReferencePriceUpdated(object? sender, MarketPriceUpdatedEventArgs e)
        {
            var update = e.Update;
            if (OnChainEngineClient.Current.State != OnChainEngineState.Ready
                || !string.Equals(update.Market.Venue.Id, "binance", StringComparison.OrdinalIgnoreCase)
                || !double.IsFinite(update.Value)
                || update.Value <= 0
                || !TryCreateDecimalValue(update.Value, out var value))
            {
                return;
            }

            var isSolanaReference = string.Equals(
                update.Market.Symbol,
                "SOL/USDT",
                StringComparison.OrdinalIgnoreCase)
                && Volatile.Read(ref _activeSolanaPoolCount) > 0;
            var sourceId = string.IsNullOrWhiteSpace(update.Source.Id)
                ? $"ccxt:binance:{update.Market.Symbol}"
                : update.Source.Id;
            if (isSolanaReference)
            {
                _ = PublishReferencePriceAsync(
                    "solUsd",
                    value,
                    sourceId,
                    update.ReceivedAtUtc.ToUnixTimeMilliseconds());
                return;
            }

            foreach (var chainId in _activeEvmChainIds)
            {
                var chain = _evmStreams.Get(chainId).ChainDefinition;
                if (chain.ReferencePriceId != null
                    && chain.CentralizedUsdMarket != null
                    && string.Equals(
                        update.Market.Symbol,
                        chain.CentralizedUsdMarket,
                        StringComparison.OrdinalIgnoreCase))
                {
                    _ = PublishEvmReferencePriceAsync(
                        chain.ChainId,
                        chain.ReferencePriceId,
                        value,
                        sourceId,
                        update.ReceivedAtUtc.ToUnixTimeMilliseconds());
                }
            }
        }

        private static async Task PublishReferencePriceAsync(
            string referenceId,
            OnChainDecimalValue value,
            string sourceId,
            long observedAtUnixMs)
        {
            try
            {
                await OnChainEngineClient.Current.PublishReferencePriceAsync(
                    referenceId,
                    value,
                    sourceId,
                    observedAtUnixMs);
            }
            catch
            {
            }
        }

        private static async Task PublishEvmReferencePriceAsync(
            string chainId,
            string referenceId,
            OnChainDecimalValue value,
            string sourceId,
            long observedAtUnixMs)
        {
            try
            {
                await OnChainEngineClient.Current.PublishEvmReferencePriceAsync(
                    chainId,
                    referenceId,
                    value,
                    sourceId,
                    observedAtUnixMs);
            }
            catch
            {
            }
        }

        private static bool TryCreateDecimalValue(double value, out OnChainDecimalValue result)
        {
            try
            {
                var exact = (decimal)value;
                var bits = decimal.GetBits(exact);
                var coefficient = new BigInteger((uint)bits[0])
                                  | (new BigInteger((uint)bits[1]) << 32)
                                  | (new BigInteger((uint)bits[2]) << 64);
                result = new OnChainDecimalValue
                {
                    Coefficient = coefficient.ToString(CultureInfo.InvariantCulture),
                    Scale = (uint)((bits[3] >> 16) & 0x7F)
                };
                return coefficient > BigInteger.Zero;
            }
            catch (OverflowException)
            {
                result = new OnChainDecimalValue();
                return false;
            }
        }
    }
}

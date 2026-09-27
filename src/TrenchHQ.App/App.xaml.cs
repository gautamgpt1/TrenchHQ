using TrenchHQ.Infrastructure.Markets;
using TrenchHQ.Infrastructure.OnChain;
using TrenchHQ.Infrastructure.OnChain.Evm;
using TrenchHQ.Infrastructure.Providers;
using TrenchHQ.Infrastructure.Social;
using TrenchHQ.Infrastructure.Wallets;
using TrenchHQ.Infrastructure.Widgets;
using TrenchHQ.Infrastructure.Windows;
using TrenchHQ.Panels;
using TrenchHQ.Presentation;
using TrenchHQ.Interop;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Windows.AppLifecycle;
using Microsoft.UI.Xaml;
using Windows.Storage;
using WinRT.Interop;

namespace TrenchHQ
{
    public partial class App : Application
    {
        private MainWindow? _mainWindow;
        private AppInstance? _primaryInstance;
        private NotificationAreaIcon? _notificationAreaIcon;
        private PanelShortcutController? _panelShortcutController;
        private Task _initializationTask = Task.CompletedTask;

        internal SavedWidgetCatalogService WidgetCatalog { get; } = new();
        internal OnChainProviderConfigurationService OnChainProviders { get; } = new();
        internal OnChainStreamCoordinator OnChainStreams { get; }
        internal EvmStreamCoordinatorCollection EvmStreams { get; }
        internal WalletActivityService WalletActivity { get; }
        internal SocialFeedService SocialFeed { get; }
        internal DesktopPanelManager PanelManager { get; }
        internal PanelShortcutController? PanelShortcuts => _panelShortcutController;
        internal bool IsQuitting { get; private set; }
        internal bool IsDesktopDisplayRunning => PanelManager.IsAnyRunning;

        public App()
        {
            InitializeComponent();
            OnChainStreams = new OnChainStreamCoordinator(
                OnChainEngineClient.Current,
                ApplicationData.Current.LocalFolder.Path);
            EvmStreams = new EvmStreamCoordinatorCollection(
                OnChainEngineClient.Current,
                ApplicationData.Current.LocalFolder.Path);
            WalletActivity = new WalletActivityService(OnChainProviders);
            SocialFeed = new SocialFeedService(ApplicationData.Current.LocalFolder.Path);
            PanelManager = new DesktopPanelManager(
                WidgetCatalog,
                OnChainProviders,
                OnChainStreams,
                EvmStreams,
                WalletActivity);
            RequestedTheme = ApplicationTheme.Dark;
            PanelManager.StateChanged += OnPanelManagerStateChanged;
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            var currentInstance = AppInstance.GetCurrent();
            var activation = currentInstance.GetActivatedEventArgs();
            _primaryInstance = AppInstance.FindOrRegisterForKey("TrenchHQ.Primary");
            if (!_primaryInstance.IsCurrent)
            {
                await _primaryInstance.RedirectActivationToAsync(activation);
                Exit();
                return;
            }

            _primaryInstance.Activated -= OnAppInstanceActivated;
            _primaryInstance.Activated += OnAppInstanceActivated;
            var isStartupActivation = activation?.Kind == ExtendedActivationKind.StartupTask;
            _initializationTask = InitializeServicesAsync();
            if (isStartupActivation)
            {
                EnsureDashboardHost();
                _ = StartDesktopDisplayAsync();
                return;
            }

            ShowDashboard();
        }

        private void OnAppInstanceActivated(object? sender, AppActivationArguments args)
        {
            if (args.Kind == ExtendedActivationKind.StartupTask)
            {
                return;
            }

            _mainWindow?.DispatcherQueue.TryEnqueue(ShowDashboard);
        }

        internal void ShowDashboard()
        {
            if (IsQuitting)
            {
                return;
            }

            EnsureDashboardHost();
            _mainWindow?.ShowAndActivate();

            UpdateDesktopDisplayState();
        }

        private void EnsureDashboardHost()
        {
            if (_mainWindow != null)
            {
                return;
            }

            _mainWindow = new MainWindow();
            _mainWindow.Closed += OnMainWindowClosed;
            CreateNotificationAreaIcon();
            CreatePanelShortcutController();
        }

        internal void HideDashboard()
        {
            _mainWindow?.Hide();
        }

        internal async Task StartDesktopDisplayAsync()
        {
            if (IsQuitting)
            {
                return;
            }

            await _initializationTask;
            await PanelManager.StartEnabledAsync();
            await PanelManager.StartEnabledDockedBarsAsync();

            UpdateDesktopDisplayState();
        }

        internal void StopDesktopDisplay()
        {
            PanelManager.CloseAll();
            UpdateDesktopDisplayState();
        }

        internal async Task ToggleDesktopDisplayAsync()
        {
            if (IsDesktopDisplayRunning)
            {
                StopDesktopDisplay();
            }
            else
            {
                await StartDesktopDisplayAsync();
            }
        }

        internal async Task QuitAsync()
        {
            if (IsQuitting)
            {
                return;
            }

            IsQuitting = true;
            _panelShortcutController?.Dispose();
            _panelShortcutController = null;
            _notificationAreaIcon?.Dispose();
            _notificationAreaIcon = null;

            await PanelManager.CloseAllAsync();
            await OnChainStreams.StopAsync();
            await EvmStreams.StopAsync();
            await WalletActivity.StopAsync();
            await SocialFeed.StopAsync();
            await MarketSidecarClient.Current.StopAsync();
            OnChainProviders.Usage.Dispose();

            if (_mainWindow != null)
            {
                _mainWindow.Close();
            }

            Exit();
        }

        private async Task InitializeServicesAsync()
        {
            await SettingsService.InitializeAsync();
            await WidgetCatalog.InitializeAsync();
            await OnChainProviders.InitializeAsync();
            var startupStatus = await StartupRegistrationService.GetStatusAsync();
            SettingsService.SaveLaunchOnStartupSnapshot(startupStatus.IsEnabled);
            await PanelManager.InitializeAsync();
        }

        internal Task EnsureInitializedAsync()
        {
            return _initializationTask;
        }

        private void CreateNotificationAreaIcon()
        {
            if (_mainWindow == null || _notificationAreaIcon != null)
            {
                return;
            }

            var hwnd = WindowNative.GetWindowHandle(_mainWindow);
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            _notificationAreaIcon = new NotificationAreaIcon(
                hwnd,
                ShowDashboard,
                () => _ = ToggleDesktopDisplayAsync(),
                () => _ = QuitAsync(),
                () => IsDesktopDisplayRunning,
                GetNotificationAreaPanelActions,
                TogglePanelFromNotificationArea);
        }

        private NotificationAreaPanelAction[] GetNotificationAreaPanelActions()
        {
            var actions = new List<NotificationAreaPanelAction>();
            foreach (var definition in PanelManager.GetDefinitions())
            {
                if (PanelManager.IsRunning(definition.Id))
                {
                    actions.Add(new NotificationAreaPanelAction(
                        definition.Id,
                        definition.Name,
                        false,
                        PanelManager.IsOverlayMinimized(definition.Id)));
                }
            }

            foreach (var definition in PanelManager.GetDockedBarDefinitions())
            {
                if (PanelManager.IsDockedBarRunning(definition.Id))
                {
                    actions.Add(new NotificationAreaPanelAction(
                        definition.Id,
                        definition.Name,
                        true,
                        PanelManager.IsDockedBarMinimized(definition.Id)));
                }
            }

            return actions.ToArray();
        }

        private async void TogglePanelFromNotificationArea(NotificationAreaPanelAction action)
        {
            var isRunning = action.IsDocked
                ? PanelManager.IsDockedBarRunning(action.Id)
                : PanelManager.IsRunning(action.Id);
            if (!isRunning)
            {
                return;
            }

            var isMinimized = action.IsDocked
                ? PanelManager.IsDockedBarMinimized(action.Id)
                : PanelManager.IsOverlayMinimized(action.Id);
            if (!isMinimized)
            {
                if (action.IsDocked)
                {
                    PanelManager.MinimizeDockedBar(action.Id);
                }
                else
                {
                    PanelManager.MinimizeOverlay(action.Id);
                }

                return;
            }

            if (action.IsDocked)
            {
                await PanelManager.ShowDockedBarAsync(action.Id, activate: false);
            }
            else
            {
                PanelManager.Show(action.Id, activate: false);
            }
        }

        private void CreatePanelShortcutController()
        {
            if (_mainWindow == null || _panelShortcutController != null)
            {
                return;
            }

            var hwnd = WindowNative.GetWindowHandle(_mainWindow);
            if (hwnd != IntPtr.Zero)
            {
                _panelShortcutController = new PanelShortcutController(hwnd, PanelManager);
            }
        }

        private void OnMainWindowClosed(object sender, WindowEventArgs args)
        {
            if (!ReferenceEquals(sender, _mainWindow))
            {
                return;
            }

            _mainWindow.Closed -= OnMainWindowClosed;
            _mainWindow = null;
        }

        private void UpdateDesktopDisplayState()
        {
            _mainWindow?.UpdateDesktopDisplayState(IsDesktopDisplayRunning);
        }

        private void OnPanelManagerStateChanged(object? sender, EventArgs e)
        {
            UpdateDesktopDisplayState();
        }
    }
}

using System;
using System.Globalization;
using TrenchHQ.Helpers;
using TrenchHQ.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace TrenchHQ
{
    public sealed partial class DockedBarWindow : Window
    {
        private readonly DesktopPanelManager _panelManager;
        private readonly UISettings _uiSettings = new();
        private DockedBarDefinition _definition;
        private SavedWidgetDefinition? _savedWidget;
        private AppWindow? _appWindow;
        private bool _appWindowConfigured;
        private bool _isRegistered;
        private IntPtr _hwnd;
        private IntPtr _oldWndProc;
        private uint _callbackMessageId;
        private uint _taskbarCreatedMessageId;
        private WindowsAppBarNative.WindowProc? _wndProc;
        private bool _isRecoveringPlacement;
        private readonly ApplicationWindowPin _applicationPin = new();
        private readonly DispatcherTimer _applicationWatch = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _fullscreenExitWatch = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private bool _fittingApplication;
        private bool _closed;
        private bool _isMinimized;
        private bool _isFullscreenSuppressed;
        private bool _automaticThicknessUpdateQueued;
        private double _contentScale = 1d;
        private WidgetContentStatus _applicationContentStatus = new(WidgetContentState.Empty, "Not configured");

        internal string DefinitionId => _definition.Id;
        internal bool IsMinimized => _isMinimized;

        internal DockedBarWindow(
            DockedBarDefinition definition,
            SavedWidgetDefinition? savedWidget,
            DesktopPanelManager panelManager)
        {
            _definition = DesktopSetupService.Clone(definition);
            _savedWidget = savedWidget;
            _panelManager = panelManager;
            InitializeComponent();
            PriceTicker.AutomaticThicknessChanged += OnPriceTickerAutomaticThicknessChanged;
            PriceTicker.ContentStatusChanged += OnContentStatusChanged;
            WalletActivity.ContentStatusChanged += OnContentStatusChanged;
            XTimeline.ContentStatusChanged += OnContentStatusChanged;
            Website.ContentStatusChanged += OnContentStatusChanged;
            Website.SetHostWindow(this);
            _applicationWatch.Tick += OnApplicationWatch;
            _fullscreenExitWatch.Tick += OnFullscreenExitWatch;
            _uiSettings.TextScaleFactorChanged += OnTextScaleFactorChanged;
            Activated += OnDockedBarActivated;
            Closed += OnDockedBarClosed;
            if (Content is FrameworkElement rootElement)
            {
                rootElement.Loaded += OnDockedBarLoaded;
            }

            ApplyDefinition(_definition, _savedWidget);
        }

        internal void ApplyDefinition(DockedBarDefinition definition, SavedWidgetDefinition? savedWidget)
        {
            var placementChanged = !string.Equals(_definition.MonitorDeviceName, definition.MonitorDeviceName, StringComparison.OrdinalIgnoreCase)
                                   || !string.Equals(_definition.Edge, definition.Edge, StringComparison.OrdinalIgnoreCase)
                                   || _definition.ThicknessPx != definition.ThicknessPx
                                   || !string.Equals(_definition.ContentSize, definition.ContentSize, StringComparison.OrdinalIgnoreCase);
            if (placementChanged)
            {
                UnregisterAppBar();
            }

            _definition = DesktopSetupService.Clone(definition);
            _savedWidget = definition.UseApplicationWindow ? null : savedWidget;
            _definition.Edge = DockedBarLayoutRules.NormalizeEdge(_definition.Edge);
            _definition.ThicknessPx = DockedBarLayoutRules.ClampThickness(_definition.Edge, _definition.ThicknessPx);
            var isWebsite = _savedWidget?.Type == PanelWidgetTypes.Website;
            _contentScale = !_definition.UseApplicationWindow && !isWebsite
                ? PanelContentSizeRules.GetScale(_definition.ContentSize)
                : 1d;
            Title = WindowsAppBarNative.GetPanelName(_definition.Edge, _definition.MonitorDeviceName, WindowsAppBarNative.GetMonitors());
            var textColor = ApplyAppearance();
            ApplyOrientation();
            ApplyPanelChromeScale();
            if (!_definition.UseApplicationWindow) ReleaseApplication();
            ApplicationWindowArea.Visibility = _definition.UseApplicationWindow ? Visibility.Visible : Visibility.Collapsed;
            RefreshApplicationButtons();
            var hostContext = PanelWidgetHostRules.ForDockedBar(_definition.Edge);
            var isWalletActivity = _savedWidget?.Type == PanelWidgetTypes.WalletActivity;
            var isXTimeline = _savedWidget?.Type == PanelWidgetTypes.XTimeline;
            Website.Visibility = isWebsite ? Visibility.Visible : Visibility.Collapsed;
            Website.Configure(isWebsite ? _savedWidget : null, hostContext);
            WebsiteActions.Visibility = isWebsite ? Visibility.Visible : Visibility.Collapsed;
            RefreshWebsiteLock();
            PriceTicker.Visibility = _definition.UseApplicationWindow || isWalletActivity || isXTimeline || isWebsite ? Visibility.Collapsed : Visibility.Visible;
            WalletActivity.Visibility = isWalletActivity ? Visibility.Visible : Visibility.Collapsed;
            XTimeline.Visibility = isXTimeline ? Visibility.Visible : Visibility.Collapsed;
            XTimeline.Configure(isXTimeline ? _savedWidget : null, hostContext, _contentScale);
            if (isWalletActivity)
            {
                WalletActivity.Configure(_savedWidget, hostContext, textColor, _contentScale);
            }
            else if (!isXTimeline && !isWebsite && !_definition.UseApplicationWindow)
            {
                PriceTicker.Configure(
                    _savedWidget,
                    hostContext,
                    textColor,
                    _definition.PriceFlashOnChange,
                    _contentScale);
            }
            RefreshContentStatus();

            if (placementChanged && _hwnd != IntPtr.Zero && !_isMinimized)
            {
                EnsureRegistered();
                ApplyDockedBarPosition();
            }
        }

        internal void Show(bool activate)
        {
            _isMinimized = false;
            if (activate)
            {
                EnsureRegistered();
                ApplyDockedBarPosition();
                Activate();
                return;
            }

            EnsureRegistered();
            ConfigureAppWindow();
            ApplyDockedBarPosition();
            if (TryGetAppWindow(out var appWindow))
            {
                appWindow.Show(false);
            }
        }

        private void OnDockedBarLoaded(object sender, RoutedEventArgs e)
        {
            if (_isMinimized) return;
            ApplyPanelChromeScale();
            EnsureRegistered();
            ConfigureAppWindow();
            ApplyDockedBarPosition();
        }

        private void OnDockedBarActivated(object sender, WindowActivatedEventArgs args)
        {
            if (_isMinimized) return;
            EnsureRegistered();
            ConfigureAppWindow();
            ApplyDockedBarPosition();
        }

        private void OnWebsiteHomeClick(object sender, RoutedEventArgs e) => Website.GoHome();
        private async void OnWebsiteReloadClick(object sender, RoutedEventArgs e) => await Website.ReloadAsync();
        private async void OnWebsiteOpenClick(object sender, RoutedEventArgs e) => await Website.OpenInBrowserAsync();
        private void OnWebsiteLockClick(object sender, RoutedEventArgs e)
        {
            Website.ToggleInteractionLock();
            RefreshWebsiteLock();
        }

        private void RefreshWebsiteLock()
        {
            var label = Website.IsInteractionLocked ? "Unlock page interaction" : "Lock page interaction";
            WebsiteLockIcon.Glyph = Website.IsInteractionLocked ? "\uE72E" : "\uE785";
            ToolTipService.SetToolTip(WebsiteLockButton, label);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(WebsiteLockButton, label);
        }

        private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
        {
            if (Application.Current is App app)
            {
                app.ShowDashboard();
            }
        }

        private void OnMinimizeClick(object sender, RoutedEventArgs e) =>
            _panelManager.MinimizeDockedBar(_definition.Id);

        private void OnCloseClick(object sender, RoutedEventArgs e) =>
            _panelManager.CloseDockedBar(_definition.Id);

        internal void Minimize()
        {
            if (_closed || _isMinimized)
            {
                return;
            }

            ReleaseApplication();
            _isMinimized = true;
            UnregisterAppBar();
            if (TryGetAppWindow(out var appWindow))
            {
                appWindow.Hide();
            }
        }

        private void OnDockedBarClosed(object sender, WindowEventArgs args)
        {
            _closed = true;
            ReleaseApplication();
            _applicationWatch.Tick -= OnApplicationWatch;
            _fullscreenExitWatch.Tick -= OnFullscreenExitWatch;
            if (Content is FrameworkElement rootElement)
            {
                rootElement.Loaded -= OnDockedBarLoaded;
            }

            Activated -= OnDockedBarActivated;
            Closed -= OnDockedBarClosed;
            _uiSettings.TextScaleFactorChanged -= OnTextScaleFactorChanged;
            PriceTicker.AutomaticThicknessChanged -= OnPriceTickerAutomaticThicknessChanged;
            PriceTicker.ContentStatusChanged -= OnContentStatusChanged;
            WalletActivity.ContentStatusChanged -= OnContentStatusChanged;
            XTimeline.ContentStatusChanged -= OnContentStatusChanged;
            Website.ContentStatusChanged -= OnContentStatusChanged;
            PriceTicker.Dispose();
            WalletActivity.Dispose();
            XTimeline.Dispose();
            Website.Dispose();
            UnregisterAppBar();
            _panelManager.NotifyDockedBarClosed(_definition.Id, this);
        }

        private void OnContentStatusChanged(object? sender, EventArgs e) => RefreshContentStatus();

        private void SetApplicationContentStatus(WidgetContentStatus status)
        {
            _applicationContentStatus = status;
            RefreshContentStatus();
        }

        private void RefreshContentStatus()
        {
            if (_closed) return;
            var status = _definition.UseApplicationWindow
                ? _applicationContentStatus
                : _savedWidget?.Type switch
                {
                    PanelWidgetTypes.WalletActivity => WalletActivity.ContentStatus,
                    PanelWidgetTypes.XTimeline => XTimeline.ContentStatus,
                    PanelWidgetTypes.Website => Website.ContentStatus,
                    _ => PriceTicker.ContentStatus
                };
            ContentStatusDot.Fill = new SolidColorBrush(GetContentStatusColor(status.State));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                ContentStatusDot,
                "Content status: " + status.Label);
        }

        private static Color GetContentStatusColor(WidgetContentState state) => state switch
        {
            WidgetContentState.Live => UiPalette.Success,
            WidgetContentState.Loading or WidgetContentState.Stale => UiPalette.Warning,
            WidgetContentState.Error or WidgetContentState.Unavailable => UiPalette.Error,
            _ => UiPalette.Muted
        };

        private void ConfigureAppWindow()
        {
            if (_appWindowConfigured || !TryGetAppWindow(out var appWindow))
            {
                return;
            }

            appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
                presenter.IsResizable = false;
                presenter.IsAlwaysOnTop = true;
            }

            ApplyWindowChromeSettings();
            _appWindowConfigured = true;
        }

        private void ApplyWindowChromeSettings()
        {
            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            var style = WindowsAppBarNative.GetWindowLongPtr(_hwnd, WindowsAppBarNative.GwlStyle).ToInt64();
            style &= ~(WindowsAppBarNative.WsCaption |
                       WindowsAppBarNative.WsThickFrame |
                       WindowsAppBarNative.WsBorder |
                       WindowsAppBarNative.WsDlgFrame |
                       WindowsAppBarNative.WsSysMenu |
                       WindowsAppBarNative.WsMinimizeBox |
                       WindowsAppBarNative.WsMaximizeBox);
            style |= WindowsAppBarNative.WsPopup;
            WindowsAppBarNative.SetWindowLongPtr(_hwnd, WindowsAppBarNative.GwlStyle, new IntPtr(style));

            var exStyle = WindowsAppBarNative.GetWindowLongPtr(_hwnd, WindowsAppBarNative.GwlExStyle).ToInt64();
            exStyle &= ~WindowsAppBarNative.WsExAppWindow;
            exStyle |= WindowsAppBarNative.WsExToolWindow;
            WindowsAppBarNative.SetWindowLongPtr(_hwnd, WindowsAppBarNative.GwlExStyle, new IntPtr(exStyle));

            var cornerPreference = WindowsAppBarNative.DwmwcpDoNotRound;
            WindowsAppBarNative.DwmSetWindowAttribute(
                _hwnd,
                WindowsAppBarNative.DwmaWindowCornerPreference,
                ref cornerPreference,
                sizeof(uint));

            var borderColor = WindowsAppBarNative.DwmColorNone;
            WindowsAppBarNative.DwmSetWindowAttribute(
                _hwnd,
                WindowsAppBarNative.DwmaBorderColor,
                ref borderColor,
                sizeof(uint));

            WindowsAppBarNative.SetWindowPos(
                _hwnd,
                _isFullscreenSuppressed ? WindowsAppBarNative.HwndBottom : WindowsAppBarNative.HwndTopMost,
                0,
                0,
                0,
                0,
                WindowsAppBarNative.SwpNoMove |
                WindowsAppBarNative.SwpNoSize |
                WindowsAppBarNative.SwpNoActivate |
                WindowsAppBarNative.SwpFrameChanged);
        }

        private void EnsureRegistered()
        {
            if (_isMinimized)
            {
                return;
            }

            _hwnd = WindowNative.GetWindowHandle(this);
            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            if (_callbackMessageId == 0)
            {
                _callbackMessageId = WindowsAppBarNative.RegisterWindowMessage($"TrenchHQ.DockedBarCallback.{_definition.Id}");
                _taskbarCreatedMessageId = WindowsAppBarNative.RegisterWindowMessage("TaskbarCreated");
            }

            if (_oldWndProc == IntPtr.Zero)
            {
                _wndProc = AppBarWndProc;
                _oldWndProc = WindowsAppBarNative.SetWindowLongPtr(_hwnd, WindowsAppBarNative.GwlpWndProc, _wndProc);
            }

            if (_isRegistered)
            {
                return;
            }

            var appBarData = WindowsAppBarNative.CreateAppBarData(_hwnd, _callbackMessageId);
            _isRegistered = WindowsAppBarNative.SHAppBarMessage(WindowsAppBarNative.AbmNew, ref appBarData) != UIntPtr.Zero;
            _isFullscreenSuppressed = false;
            if (_isRegistered
                && WindowsAppBarNative.HasFullscreenWindowOnMonitor(
                    _definition.MonitorDeviceName,
                    (uint)Environment.ProcessId))
            {
                SetFullscreenSuppressed(true);
            }
        }

        private void UnregisterAppBar()
        {
            _fullscreenExitWatch.Stop();
            _isFullscreenSuppressed = false;
            if (_isRegistered && _hwnd != IntPtr.Zero)
            {
                var appBarData = WindowsAppBarNative.CreateAppBarData(_hwnd);
                WindowsAppBarNative.SHAppBarMessage(WindowsAppBarNative.AbmRemove, ref appBarData);
                _isRegistered = false;
            }

            if (_hwnd != IntPtr.Zero && _oldWndProc != IntPtr.Zero)
            {
                WindowsAppBarNative.SetWindowLongPtr(_hwnd, WindowsAppBarNative.GwlpWndProc, _oldWndProc);
                _oldWndProc = IntPtr.Zero;
                _wndProc = null;
            }
        }

        private void ApplyDockedBarPosition()
        {
            if (_isMinimized || !_isRegistered || _hwnd == IntPtr.Zero)
            {
                return;
            }

            var edge = GetNativeEdge(_definition.Edge);
            var monitorRect = WindowsAppBarNative.GetMonitorRect(_definition.MonitorDeviceName, _hwnd);
            var monitorSpan = DockedBarLayoutRules.IsHorizontal(_definition.Edge)
                ? monitorRect.bottom - monitorRect.top : monitorRect.right - monitorRect.left;
            var windowDpi = WindowsAppBarNative.GetDpiForWindow(_hwnd);
            var dpi = windowDpi > 0 ? windowDpi / 96d : 1d;
            var thickness = _savedWidget?.Type == PanelWidgetTypes.PriceTicker
                ? PriceTicker.GetAutomaticDockedThickness(
                    monitorSpan,
                    _uiSettings.TextScaleFactor,
                    dpi)
                : DockedBarLayoutRules.GetRuntimeThickness(
                    _definition.Edge,
                    _definition.ThicknessPx,
                    _uiSettings.TextScaleFactor,
                    monitorSpan,
                    dpi,
                    _contentScale);
            if (_definition.UseApplicationWindow || _savedWidget?.Type == PanelWidgetTypes.Website)
            {
                var required = _definition.UseApplicationWindow
                    ? DockedBarLayoutRules.MinimumApplicationWindowSize(dpi)
                    : WebsiteWidgetRules.MinimumDockedSize(dpi);
                thickness = Math.Min(DockedBarLayoutRules.GetMaximumThickness(monitorSpan),
                    Math.Max(thickness, DockedBarLayoutRules.IsHorizontal(_definition.Edge) ? required.Height : required.Width));
            }
            var appBarData = WindowsAppBarNative.CreateAppBarData(_hwnd, _callbackMessageId);
            appBarData.uEdge = edge;
            // Query the full monitor so reserved space is not subtracted from our thickness.
            appBarData.rc = monitorRect;

            WindowsAppBarNative.SHAppBarMessage(WindowsAppBarNative.AbmQueryPos, ref appBarData);
            // Respect space Windows has already reserved for other desktop bars.
            var availableSpan = DockedBarLayoutRules.IsHorizontal(_definition.Edge)
                ? appBarData.rc.bottom - appBarData.rc.top : appBarData.rc.right - appBarData.rc.left;
            thickness = Math.Min(thickness, Math.Max(1, availableSpan));
            ApplyRequestedThickness(ref appBarData.rc, edge, thickness);
            WindowsAppBarNative.SHAppBarMessage(WindowsAppBarNative.AbmSetPos, ref appBarData);

            var width = appBarData.rc.right - appBarData.rc.left;
            var height = appBarData.rc.bottom - appBarData.rc.top;
            WindowsAppBarNative.SetWindowPos(
                _hwnd,
                _isFullscreenSuppressed ? WindowsAppBarNative.HwndBottom : WindowsAppBarNative.HwndTopMost,
                appBarData.rc.left,
                appBarData.rc.top,
                width,
                height,
                WindowsAppBarNative.SwpNoActivate);
            var positionedDpi = WindowsAppBarNative.GetDpiForWindow(_hwnd);
            if (positionedDpi > 0 && positionedDpi != windowDpi)
            {
                DispatcherQueue.TryEnqueue(ApplyDockedBarPosition);
                return;
            }
            if (_applicationPin.IsPinned) _ = FitApplicationAsync();
        }

        private void OnPriceTickerAutomaticThicknessChanged(object? sender, EventArgs e)
        {
            if (_automaticThicknessUpdateQueued
                || _isMinimized
                || _savedWidget?.Type != PanelWidgetTypes.PriceTicker)
            {
                return;
            }

            _automaticThicknessUpdateQueued = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                _automaticThicknessUpdateQueued = false;
                if (!_isMinimized && _savedWidget?.Type == PanelWidgetTypes.PriceTicker)
                {
                    ApplyDockedBarPosition();
                }
            });
        }

        private WindowPinBounds GetApplicationBounds()
        {
            if (!TryGetAppWindow(out var window)) return default;
            var point = ApplicationWindowArea.TransformToVisual(DockedBarRoot)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            var scale = DockedBarRoot.XamlRoot?.RasterizationScale ?? 1;
            return new WindowPinBounds(window.Position.X + (int)Math.Ceiling(point.X * scale),
                window.Position.Y + (int)Math.Ceiling(point.Y * scale),
                (int)Math.Floor(ApplicationWindowArea.ActualWidth * scale),
                (int)Math.Floor(ApplicationWindowArea.ActualHeight * scale));
        }

        private async void OnChooseApplicationClick(object sender, RoutedEventArgs e)
        {
            if (_closed) return;
            if (_applicationPin.IsPinned) { ReleaseApplication(); return; }
            if (!GetApplicationBounds().Fits(0, 0))
            {
                ApplicationWindowStatus.Text = "Increase bar thickness in Desktop Setup before choosing an application.";
                SetApplicationContentStatus(new WidgetContentStatus(WidgetContentState.Error, "Error"));
                return;
            }
            if (Application.Current is not App app) return;
            app.ShowDashboard();
            await System.Threading.Tasks.Task.Yield();
            var dialogRoot = (MainWindow.Current?.Content as FrameworkElement)?.XamlRoot;
            if (dialogRoot == null || _closed) return;
            ApplicationWindowSelection? choice;
            ChooseApplicationButton.IsEnabled = false;
            try { choice = await ApplicationWindowPicker.ShowAsync(dialogRoot); }
            catch (Exception)
            {
                ApplicationWindowStatus.Text = "Close any other dashboard dialog, then choose a window again.";
                SetApplicationContentStatus(new WidgetContentStatus(WidgetContentState.Error, "Error"));
                return;
            }
            finally { RefreshApplicationButtons(); }
            if (choice == null || _closed || !_definition.UseApplicationWindow) return;
            _fittingApplication = true;
            ChooseApplicationButton.IsEnabled = false;
            SetApplicationContentStatus(new WidgetContentStatus(WidgetContentState.Loading, "Loading"));
            try
            {
                await _applicationPin.PinAsync(choice.Window, GetApplicationBounds(), _hwnd, choice.Mode);
                if (!_closed && _applicationPin.IsPinned)
                {
                    ApplicationWindowStatus.Text = "Pinned: " + choice.Window.Label;
                    SetApplicationContentStatus(new WidgetContentStatus(WidgetContentState.Live, "Live"));
                    _applicationWatch.Start();
                }
            }
            catch (Exception error)
            {
                ApplicationWindowStatus.Text = error.Message;
                SetApplicationContentStatus(new WidgetContentStatus(WidgetContentState.Error, "Error"));
            }
            finally { _fittingApplication = false; RefreshApplicationButtons(); }
        }

        private void RefreshApplicationButtons()
        {
            ChooseApplicationButton.Visibility = _definition.UseApplicationWindow ? Visibility.Visible : Visibility.Collapsed;
            ChooseApplicationButton.IsEnabled = !_fittingApplication;
            ApplicationPinIcon.Glyph = _applicationPin.IsPinned ? "\uE77A" : "\uE718";
            ToolTipService.SetToolTip(ChooseApplicationButton, _applicationPin.IsPinned ? "Unpin window" : "Pin window");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ChooseApplicationButton,
                _applicationPin.IsPinned ? "Unpin application window" : "Pin application window");
        }

        private void ReleaseApplication()
        {
            _applicationWatch.Stop();
            _applicationPin.Unpin();
            ApplicationWindowStatus.Text = "Select Pin window to pin an application here.";
            SetApplicationContentStatus(new WidgetContentStatus(WidgetContentState.Empty, "Not configured"));
            RefreshApplicationButtons();
        }

        private async void OnApplicationAreaSizeChanged(object sender, SizeChangedEventArgs args) => await FitApplicationAsync();

        private async System.Threading.Tasks.Task FitApplicationAsync()
        {
            if (_closed || _fittingApplication || !_applicationPin.IsPinned) return;
            _fittingApplication = true;
            try
            {
                var bounds = GetApplicationBounds();
                await _applicationPin.FitAsync(bounds);
                // Layout can finish while the cross-process positioning request is in flight.
                if (_applicationPin.IsPinned && bounds != GetApplicationBounds())
                    await _applicationPin.FitAsync(GetApplicationBounds());
            }
            catch (Exception error)
            {
                ReleaseApplication();
                ApplicationWindowStatus.Text = error.Message;
                SetApplicationContentStatus(new WidgetContentStatus(WidgetContentState.Error, "Error"));
            }
            finally { _fittingApplication = false; RefreshApplicationButtons(); }
        }

        private void OnApplicationWatch(object? sender, object e)
        {
            if (!_applicationPin.IsAlive)
            {
                ReleaseApplication();
                ApplicationWindowStatus.Text = "The selected window closed. Choose another window when ready.";
            }
        }

        private static void ApplyRequestedThickness(ref WindowsAppBarNative.Rect rect, uint edge, int thickness)
        {
            if (edge == WindowsAppBarNative.AbeLeft)
            {
                rect.right = rect.left + thickness;
            }
            else if (edge == WindowsAppBarNative.AbeRight)
            {
                rect.left = rect.right - thickness;
            }
            else if (edge == WindowsAppBarNative.AbeBottom)
            {
                rect.top = rect.bottom - thickness;
            }
            else
            {
                rect.bottom = rect.top + thickness;
            }
        }

        private static uint GetNativeEdge(string edge)
        {
            return DockedBarLayoutRules.NormalizeEdge(edge) switch
            {
                "Left" => WindowsAppBarNative.AbeLeft,
                "Right" => WindowsAppBarNative.AbeRight,
                "Bottom" => WindowsAppBarNative.AbeBottom,
                _ => WindowsAppBarNative.AbeTop
            };
        }

        private IntPtr AppBarWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == _callbackMessageId)
            {
                var notification = unchecked((uint)wParam.ToInt64());
                if (notification == WindowsAppBarNative.AbnPosChanged)
                {
                    DispatcherQueue.TryEnqueue(ApplyDockedBarPosition);
                    return IntPtr.Zero;
                }

                if (notification == WindowsAppBarNative.AbnFullscreenApp)
                {
                    var isOpening = lParam != IntPtr.Zero;
                    if (isOpening && WindowsAppBarNative.IsForegroundWindowOnMonitor(_definition.MonitorDeviceName))
                    {
                        DispatcherQueue.TryEnqueue(() => SetFullscreenSuppressed(true));
                    }
                    else if (!isOpening)
                    {
                        DispatcherQueue.TryEnqueue(BeginFullscreenExitRestore);
                    }
                    return IntPtr.Zero;
                }
            }

            if (msg == _taskbarCreatedMessageId)
            {
                _isRegistered = false;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_isMinimized) return;
                    EnsureRegistered();
                    ApplyDockedBarPosition();
                });
            }

            var result = _oldWndProc != IntPtr.Zero
                ? WindowsAppBarNative.CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam)
                : IntPtr.Zero;

            if (_isRegistered && msg == WindowsAppBarNative.WmWindowPosChanged)
            {
                var appBarData = WindowsAppBarNative.CreateAppBarData(_hwnd, _callbackMessageId);
                WindowsAppBarNative.SHAppBarMessage(WindowsAppBarNative.AbmWindowPosChanged, ref appBarData);
            }
            else if (_isRegistered && msg == WindowsAppBarNative.WmActivate)
            {
                var appBarData = WindowsAppBarNative.CreateAppBarData(_hwnd, _callbackMessageId);
                appBarData.lParam = (wParam.ToInt64() & 0xFFFF) == 0 ? IntPtr.Zero : new IntPtr(1);
                WindowsAppBarNative.SHAppBarMessage(WindowsAppBarNative.AbmActivate, ref appBarData);
            }
            else if (msg == WindowsAppBarNative.WmDisplayChange
                     || msg == WindowsAppBarNative.WmDpiChanged
                     || msg == WindowsAppBarNative.WmSettingChange
                     || msg == WindowsAppBarNative.WmDeviceChange)
            {
                DispatcherQueue.TryEnqueue(HandleDisplayEnvironmentChangedAsync);
            }

            return result;
        }

        private void SetFullscreenSuppressed(bool suppressed)
        {
            if (_isFullscreenSuppressed == suppressed
                || !_isRegistered
                || _isMinimized
                || _hwnd == IntPtr.Zero)
            {
                return;
            }

            if (suppressed)
            {
                _isFullscreenSuppressed = true;
                _fullscreenExitWatch.Start();
                WindowsAppBarNative.SetWindowPos(
                    _hwnd,
                    WindowsAppBarNative.HwndBottom,
                    0,
                    0,
                    0,
                    0,
                    WindowsAppBarNative.SwpNoMove
                    | WindowsAppBarNative.SwpNoSize
                    | WindowsAppBarNative.SwpNoActivate);
                return;
            }

            if (!WindowsAppBarNative.SetWindowPos(
                    _hwnd,
                    WindowsAppBarNative.HwndTopMost,
                    0,
                    0,
                    0,
                    0,
                    WindowsAppBarNative.SwpNoMove
                    | WindowsAppBarNative.SwpNoSize
                    | WindowsAppBarNative.SwpNoActivate))
            {
                return;
            }

            _isFullscreenSuppressed = false;
            _fullscreenExitWatch.Stop();
        }

        private void OnFullscreenExitWatch(object? sender, object e)
        {
            RestoreAfterFullscreenExit();
        }

        private void BeginFullscreenExitRestore()
        {
            if (!_isRegistered || _isMinimized || _hwnd == IntPtr.Zero)
            {
                return;
            }

            _isFullscreenSuppressed = true;
            _fullscreenExitWatch.Start();
            RestoreAfterFullscreenExit();
        }

        private void RestoreAfterFullscreenExit()
        {
            if (_isFullscreenSuppressed
                && WindowsAppBarNative.HasFullscreenWindowOnMonitor(
                    _definition.MonitorDeviceName,
                    (uint)Environment.ProcessId))
            {
                WindowsAppBarNative.SetWindowPos(
                    _hwnd,
                    WindowsAppBarNative.HwndBottom,
                    0,
                    0,
                    0,
                    0,
                    WindowsAppBarNative.SwpNoMove
                    | WindowsAppBarNative.SwpNoSize
                    | WindowsAppBarNative.SwpNoActivate);
            }
            else if (_isFullscreenSuppressed)
            {
                SetFullscreenSuppressed(false);
            }
        }

        private void OnTextScaleFactorChanged(UISettings sender, object args)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                PriceTicker.InvalidateContentMeasure();
                WalletActivity.InvalidateContentMeasure();
                if (_isMinimized) return;
                ApplyDockedBarPosition();
            });
        }

        private async void HandleDisplayEnvironmentChangedAsync()
        {
            if (_isMinimized || _isRecoveringPlacement)
            {
                return;
            }

            _isRecoveringPlacement = true;
            try
            {
                var recovered = await _panelManager.RecoverDockedBarPlacementAsync(_definition.Id);
                if (recovered == null)
                {
                    Close();
                    return;
                }

                ApplyDefinition(recovered, _savedWidget);
                ApplyDockedBarPosition();
            }
            catch
            {
                Close();
            }
            finally
            {
                _isRecoveringPlacement = false;
            }
        }

        private Color ApplyAppearance()
        {
            var background = ParseColor(_definition.BackgroundColor, Color.FromArgb(255, 0, 0, 0));
            var foreground = ParseColor(_definition.TextColor, Color.FromArgb(255, 245, 245, 245));
            DockedBarRoot.Background = new SolidColorBrush(background);
            if (DockedBarRoot.Resources["DockedBarTextBrush"] is SolidColorBrush textBrush)
            {
                textBrush.Color = foreground;
            }

            return foreground;
        }

        private void ApplyOrientation()
        {
            // One top-right toolbar; thin horizontal feeds share its row to preserve content height.
            var horizontal = DockedBarLayoutRules.IsHorizontal(_definition.Edge)
                             && _savedWidget?.Type != PanelWidgetTypes.Website && !_definition.UseApplicationWindow;
            ControlsRow.Height = horizontal ? new GridLength(0) : GridLength.Auto;
            Grid.SetRow(PanelControls, horizontal ? 1 : 0);
            Grid.SetColumnSpan(PanelContent, 2);
            PanelControls.Margin = horizontal
                ? new Thickness(8 * _contentScale, 0, 8 * _contentScale, 0)
                : new Thickness(8 * _contentScale, 4 * _contentScale, 8 * _contentScale, 4 * _contentScale);
        }

        private void ApplyPanelChromeScale()
        {
            PanelControls.Spacing = 4 * _contentScale;
            WebsiteActions.Spacing = 2 * _contentScale;
            ContentStatusDot.Width = 8 * _contentScale;
            ContentStatusDot.Height = 8 * _contentScale;
            ContentStatusDot.Margin = new Thickness(2 * _contentScale, 0, 0, 0);
            ScalePanelChromeChildren(PanelControls, _contentScale);
        }

        private static void ScalePanelChromeChildren(DependencyObject root, double scale)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                var child = VisualTreeHelper.GetChild(root, index);
                if (child is Button button)
                {
                    button.Width = 28 * scale;
                    button.Height = 28 * scale;
                    button.MinWidth = 28 * scale;
                    button.MinHeight = 28 * scale;
                    button.CornerRadius = new CornerRadius(14 * scale);
                }
                else if (child is FontIcon icon)
                {
                    icon.FontSize = 14 * scale;
                }
                ScalePanelChromeChildren(child, scale);
            }
        }

        private static Color ParseColor(string? hex, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            var value = hex.Trim().TrimStart('#');
            return value.Length == 6
                   && byte.TryParse(value.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var red)
                   && byte.TryParse(value.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var green)
                   && byte.TryParse(value.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var blue)
                ? Color.FromArgb(255, red, green, blue)
                : fallback;
        }

        private bool TryGetAppWindow(out AppWindow appWindow)
        {
            if (_appWindow != null)
            {
                appWindow = _appWindow;
                return true;
            }

            var hwnd = WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero)
            {
                appWindow = null!;
                return false;
            }

            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            appWindow = _appWindow;
            return true;
        }
    }
}

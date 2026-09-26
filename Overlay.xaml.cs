using TrenchHQ.Helpers;
using TrenchHQ.Models;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinRT.Interop;
using WinRT;

namespace TrenchHQ
{
    public sealed partial class Overlay : Window
    {
        private DesktopAcrylicController? _backdropController;
        private SystemBackdropConfiguration? _backdropConfiguration;
        private AppWindow? _appWindow;
        private bool _appWindowConfigured;
        private bool _isLoaded;
        private bool _isMinimized;
        private bool _isRestoringPosition;
        private double _lastRasterizationScale = 1d;
        private OverlayDefinition _definition;
        private SavedWidgetDefinition? _savedWidget;
        private readonly DesktopPanelManager _panelManager;
        private readonly UISettings _uiSettings = new();
        private double _overlayWidthDip = OverlayLayoutRules.DefaultWidth;
        private double _contentScale = 1d;
        private WidgetContentStatus _applicationContentStatus = new(WidgetContentState.Empty, "Not configured");

        internal string DefinitionId => _definition.Id;
        internal bool IsMinimized => _isMinimized;

        internal Overlay(
            OverlayDefinition definition,
            SavedWidgetDefinition? savedWidget,
            DesktopPanelManager panelManager)
        {
            _definition = DesktopSetupService.Clone(definition);
            _savedWidget = savedWidget;
            _panelManager = panelManager;
            InitializeComponent();
            PriceTicker.ContentStatusChanged += OnContentStatusChanged;
            WalletActivity.ContentStatusChanged += OnContentStatusChanged;
            XTimeline.ContentStatusChanged += OnContentStatusChanged;
            Website.ContentStatusChanged += OnContentStatusChanged;
            _applicationWatch.Tick += OnApplicationWatch;
            Website.SetHostWindow(this);
            _uiSettings.TextScaleFactorChanged += OnTextScaleFactorChanged;
            Activated += OnOverlayActivated;
            Closed += OnOverlayClosed;
            TrySetSystemBackdrop();
            ConfigureTitleBar();
            if (Content is FrameworkElement rootElement)
            {
                rootElement.Loaded += OnOverlayLoaded;
            }

            ApplyDefinition(_definition, _savedWidget);
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

        private void OnOpenSettingsClick(object _, RoutedEventArgs __)
        {
            if (Application.Current is App app)
            {
                app.ShowDashboard();
            }
        }

        private void OnMinimizeClick(object _, RoutedEventArgs __) =>
            _panelManager.MinimizeOverlay(_definition.Id);

        private void OnCloseClick(object _, RoutedEventArgs __)
        {
            _panelManager.Close(_definition.Id);
        }

        private void TrySetSystemBackdrop()
        {
            if (!DesktopAcrylicController.IsSupported())
            {
                return;
            }

            _backdropConfiguration = new SystemBackdropConfiguration
            {
                IsInputActive = true
            };
            SetBackdropTheme();

            _backdropController = new DesktopAcrylicController
            {
                FallbackColor = Color.FromArgb(10, 10, 20, 10),
                TintColor = Color.FromArgb(255, 110, 110, 110),
                TintOpacity = 0.1f,
                LuminosityOpacity = 0.01f
            };

            _backdropController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
            _backdropController.SetSystemBackdropConfiguration(_backdropConfiguration);

            if (Content is FrameworkElement rootElement)
            {
                rootElement.ActualThemeChanged += OnOverlayThemeChanged;
            }
        }

        private void OnOverlayActivated(object _, WindowActivatedEventArgs args)
        {
            ConfigureAppWindow();
            if (args.WindowActivationState != WindowActivationState.Deactivated && _applicationPin.IsPinned)
                _ = FitApplicationAsync();
            WindowSubclasser.PreventMaximizeOnTitleBarDoubleClick(this);

            if (_backdropConfiguration == null || _backdropController == null)
            {
                return;
            }

            _backdropConfiguration.IsInputActive = true;
            _backdropController.SetSystemBackdropConfiguration(_backdropConfiguration);
        }

        private void OnOverlayClosed(object _, WindowEventArgs __)
        {
            _closed = true;
            ReleaseApplication();
            _applicationWatch.Tick -= OnApplicationWatch;
            if (Content is FrameworkElement rootElement)
            {
                rootElement.ActualThemeChanged -= OnOverlayThemeChanged;
                rootElement.Loaded -= OnOverlayLoaded;
                if (rootElement.XamlRoot != null)
                {
                    rootElement.XamlRoot.Changed -= OnXamlRootChanged;
                }
            }

            Activated -= OnOverlayActivated;
            Closed -= OnOverlayClosed;
            PriceTicker.ContentStatusChanged -= OnContentStatusChanged;
            WalletActivity.ContentStatusChanged -= OnContentStatusChanged;
            XTimeline.ContentStatusChanged -= OnContentStatusChanged;
            Website.ContentStatusChanged -= OnContentStatusChanged;
            PriceTicker.Dispose();
            WalletActivity.Dispose();
            XTimeline.Dispose();
            Website.Dispose();
            _uiSettings.TextScaleFactorChanged -= OnTextScaleFactorChanged;

            if (_appWindow != null)
            {
                _appWindow.Changed -= OnAppWindowChanged;
            }

            _backdropController?.Dispose();
            _backdropController = null;
            _backdropConfiguration = null;

            _panelManager.NotifyClosed(_definition.Id, this);
        }

        private void OnOverlayThemeChanged(FrameworkElement _, object __)
        {
            SetBackdropTheme();
        }

        private void SetBackdropTheme()
        {
            if (_backdropConfiguration == null)
            {
                return;
            }

            var theme = (Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default;
            _backdropConfiguration.Theme = theme switch
            {
                ElementTheme.Light => SystemBackdropTheme.Light,
                ElementTheme.Dark => SystemBackdropTheme.Dark,
                _ => SystemBackdropTheme.Default
            };
            _backdropController?.SetSystemBackdropConfiguration(_backdropConfiguration);
        }

        private void ConfigureTitleBar()
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleBarDragRegion);
            ConfigureAppWindow();
        }

        private void ConfigureAppWindow()
        {
            if (_appWindowConfigured || !TryGetAppWindow(out var appWindow))
            {
                return;
            }

            appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);

            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(true, false);
                presenter.IsMinimizable = true;
                presenter.IsMaximizable = false;
                presenter.IsResizable = false;
                presenter.IsAlwaysOnTop = true;
            }

            ResizeOverlayWindow(appWindow, _overlayWidthDip, GetDesiredOverlayHeight(_overlayWidthDip));

            appWindow.Changed += OnAppWindowChanged;
            RestorePosition(appWindow);

            _appWindowConfigured = true;
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

        private void OnOverlayLoaded(object sender, RoutedEventArgs e)
        {
            if (_isLoaded)
            {
                return;
            }

            _isLoaded = true;
            if (Content is FrameworkElement rootElement && rootElement.XamlRoot != null)
            {
                _lastRasterizationScale = GetRasterizationScale();
                rootElement.XamlRoot.Changed += OnXamlRootChanged;
            }

            ApplyDefinition(_definition, _savedWidget);
        }

        internal void ApplyDefinition(OverlayDefinition definition, SavedWidgetDefinition? savedWidget)
        {
            _definition = DesktopSetupService.Clone(definition);
            _savedWidget = definition.UseApplicationWindow ? null : savedWidget;
            var isWalletActivity = _savedWidget?.Type == PanelWidgetTypes.WalletActivity;
            var isXTimeline = _savedWidget?.Type == PanelWidgetTypes.XTimeline;
            var isWebsite = _savedWidget?.Type == PanelWidgetTypes.Website;
            _contentScale = !_definition.UseApplicationWindow && !isWebsite
                ? PanelContentSizeRules.GetScale(_definition.ContentSize)
                : 1d;
            if (!definition.UseApplicationWindow) ReleaseApplication();
            ApplicationWindowArea.Visibility = definition.UseApplicationWindow ? Visibility.Visible : Visibility.Collapsed;
            OverlayContentArea.Height = HasAdjustableHeight
                ? OverlayLayoutRules.ClampHeight(definition.HeightDip) * _contentScale
                : double.NaN;
            RefreshApplicationButtons();
            _overlayWidthDip = OverlayLayoutRules.ClampWidth(_definition.WidthDip, _savedWidget?.Type);
            Title = _definition.Name;
            if (OverlayTitleText != null)
            {
                OverlayTitleText.Text = _definition.Name;
            }

            ApplyBackdropSettings(_definition);
            var textColor = ApplyTextColor(_definition);
            ApplyPanelChromeScale(isWebsite);
            Website.Visibility = isWebsite ? Visibility.Visible : Visibility.Collapsed;
            Website.Configure(isWebsite ? _savedWidget : null, PanelWidgetHostRules.ForOverlay());
            WebsiteActions.Visibility = isWebsite ? Visibility.Visible : Visibility.Collapsed;
            RefreshWebsiteLock();
            PriceTicker.Visibility = _definition.UseApplicationWindow || isWalletActivity || isXTimeline || isWebsite ? Visibility.Collapsed : Visibility.Visible;
            WalletActivity.Visibility = isWalletActivity ? Visibility.Visible : Visibility.Collapsed;
            XTimeline.Visibility = isXTimeline ? Visibility.Visible : Visibility.Collapsed;
            if (!isXTimeline) XTimeline.Configure(null, null, _contentScale);
            if (isXTimeline)
            {
                XTimeline.Configure(_savedWidget, null, _contentScale);
            }
            else if (isWalletActivity)
            {
                WalletActivity.Configure(_savedWidget, PanelWidgetHostRules.ForOverlay(), textColor, _contentScale);
            }
            else if (!isWebsite && !_definition.UseApplicationWindow)
            {
                PriceTicker.Configure(
                    _savedWidget,
                    PanelWidgetHostRules.ForOverlay(),
                    textColor,
                    _definition.PriceFlashOnChange,
                    _contentScale);
            }
            RefreshContentStatus();
            QueueSizeUpdate();
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

        internal void Show(bool activate)
        {
            ConfigureAppWindow();
            if (TryGetAppWindow(out var currentWindow)
                && currentWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } minimizedPresenter)
            {
                _isMinimized = false;
                minimizedPresenter.Restore(activate);
                return;
            }

            _isMinimized = false;
            if (activate)
            {
                Activate();
                return;
            }

            if (TryGetAppWindow(out var appWindow))
            {
                appWindow.Show(false);
            }
        }

        internal void Minimize()
        {
            if (_closed || _isMinimized)
            {
                return;
            }

            ReleaseApplication();
            _isMinimized = true;
            if (TryGetAppWindow(out var appWindow) && appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Minimize(false);
            }
        }

        private void ApplyBackdropSettings(OverlayDefinition definition)
        {
            if (_backdropController == null)
            {
                return;
            }

            var baseColor = ParseColor(definition.BackgroundColor, Color.FromArgb(255, 0, 0, 0));
            _backdropController.FallbackColor = baseColor;
            _backdropController.TintColor = baseColor;
            _backdropController.TintOpacity = ClampOpacity(definition.TintOpacity);
            _backdropController.LuminosityOpacity = ClampOpacity(definition.LuminosityOpacity);
        }

        private Color ApplyTextColor(OverlayDefinition definition)
        {
            var color = Color.FromArgb(255, 245, 245, 245);
            if (OverlayRoot?.Resources["OverlayTextBrush"] is SolidColorBrush brush)
            {
                color = ParseColor(definition.TextColor, brush.Color);
                brush.Color = color;
            }

            return color;
        }

        private static float ClampOpacity(double value)
        {
            if (value <= 0)
            {
                return 0f;
            }

            if (value >= 100)
            {
                return 1f;
            }

            return (float)(value / 100d);
        }

        private static Color ParseColor(string? hex, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(hex))
            {
                return fallback;
            }

            var value = hex.Trim();
            if (value.StartsWith("#", StringComparison.Ordinal))
            {
                value = value[1..];
            }

            if (value.Length == 6 &&
                byte.TryParse(value.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) &&
                byte.TryParse(value.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) &&
                byte.TryParse(value.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                return Color.FromArgb(255, r, g, b);
            }

            if (value.Length == 8 &&
                byte.TryParse(value.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var a) &&
                byte.TryParse(value.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rr) &&
                byte.TryParse(value.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var gg) &&
                byte.TryParse(value.AsSpan(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bb))
            {
                return Color.FromArgb(a, rr, gg, bb);
            }

            return fallback;
        }

        private void QueueSizeUpdate()
        {
            if (DispatcherQueue == null)
            {
                UpdateOverlaySize();
                return;
            }

            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, UpdateOverlaySize);
        }

        private void OnTextScaleFactorChanged(UISettings sender, object args)
        {
            QueueSizeUpdate();
        }

        private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
        {
            var scale = GetRasterizationScale();
            if (Math.Abs(scale - _lastRasterizationScale) < 0.001d)
            {
                return;
            }

            _lastRasterizationScale = scale;
            QueueSizeUpdate();
        }

        private void UpdateOverlaySize()
        {
            if (_closed || OverlayRoot == null || !TryGetAppWindow(out var appWindow))
            {
                return;
            }

            PriceTicker.InvalidateContentMeasure();
            WalletActivity.InvalidateContentMeasure();
            XTimeline.InvalidateContentMeasure();
            Website.InvalidateContentMeasure();
            OverlayRoot.InvalidateMeasure();
            var width = OverlayLayoutRules.ClampWidth(_overlayWidthDip);
            var height = GetDesiredOverlayHeight(width);
            ResizeOverlayWindow(appWindow, width, height);
        }

        private void ResizeOverlayWindow(AppWindow appWindow, double widthDip, double heightDip)
        {
            var widthPx = DipToPixels(widthDip);
            var heightPx = DipToPixels(heightDip);
            if (HasAdjustableHeight)
            {
                // AppWindow.Size includes the native frame; XAML is laid out in ClientSize.
                heightPx += Math.Max(0, appWindow.Size.Height - appWindow.ClientSize.Height);
                var workArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
                (widthPx, heightPx) = OverlayLayoutRules.ClampPixelSize(widthPx, heightPx, workArea.Width, workArea.Height);
            }
            appWindow.Resize(new SizeInt32(widthPx, heightPx));
            if (HasAdjustableHeight)
            {
                OverlayContentArea.Height = Math.Max(0, appWindow.ClientSize.Height / GetRasterizationScale() - Math.Max(32 * _contentScale, OverlayTitleBar.ActualHeight));
                KeepOverlayOnScreen(appWindow);
            }
        }

        private void RestorePosition(AppWindow appWindow)
        {
            if (!_definition.PositionX.HasValue || !_definition.PositionY.HasValue)
            {
                return;
            }

            var placement = WindowPlacementNative.GetSafePlacement(
                _definition.PositionX,
                _definition.PositionY,
                appWindow.Size.Width,
                appWindow.Size.Height,
                _definition.MonitorDeviceName);

            _isRestoringPosition = true;
            try
            {
                appWindow.Move(new PointInt32(placement.X, placement.Y));
            }
            finally
            {
                _isRestoringPosition = false;
            }

            if (placement.X != _definition.PositionX.Value
                || placement.Y != _definition.PositionY.Value
                || !string.Equals(placement.MonitorDeviceName, _definition.MonitorDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                _ = _panelManager.UpdatePositionAsync(
                    _definition.Id,
                    placement.X,
                    placement.Y,
                    placement.MonitorDeviceName);
            }
        }

        private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (_closed) return;
            if (HasAdjustableHeight && !_isRestoringPosition)
            {
                if (!sender.IsVisible || sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
                {
                    if (_definition.UseApplicationWindow) ReleaseApplication();
                }
                else if (args.DidPositionChange || args.DidSizeChange)
                {
                    KeepOverlayOnScreen(sender);
                    if (_definition.UseApplicationWindow) _ = FitApplicationAsync();
                }
            }
            if (_isRestoringPosition || !args.DidPositionChange)
            {
                return;
            }

            var hwnd = WindowNative.GetWindowHandle(this);
            _ = _panelManager.UpdatePositionAsync(
                _definition.Id,
                sender.Position.X,
                sender.Position.Y,
                WindowPlacementNative.GetMonitorDeviceName(hwnd));
        }

        private bool HasAdjustableHeight => OverlayLayoutRules.HasAdjustableHeight(_definition.UseApplicationWindow, _savedWidget?.Type);

        private double GetDesiredOverlayHeight(double widthDip)
        {
            if (OverlayRoot == null)
            {
                return 0;
            }

            if (HasAdjustableHeight)
                return (OverlayLayoutRules.ClampHeight(_definition.HeightDip) * _contentScale)
                       + Math.Max(32 * _contentScale, OverlayTitleBar.ActualHeight);

            OverlayRoot.Measure(new Windows.Foundation.Size(widthDip, double.PositiveInfinity));
            return Math.Ceiling(OverlayRoot.DesiredSize.Height);
        }

        private void ApplyPanelChromeScale(bool isWebsite)
        {
            OverlayTitleBar.MinHeight = 32 * _contentScale;
            OverlayTitleText.FontSize = 18 * _contentScale;
            OverlayTitleText.Margin = new Thickness(12 * _contentScale, 0, 8 * _contentScale, 0);
            TitleBarButtons.Spacing = (isWebsite ? 2 : 6) * _contentScale;
            TitleBarButtons.Margin = new Thickness(0, 0, 8 * _contentScale, 0);
            ApplicationActions.Spacing = 2 * _contentScale;
            WebsiteActions.Spacing = 2 * _contentScale;
            ContentStatusDot.Width = 8 * _contentScale;
            ContentStatusDot.Height = 8 * _contentScale;
            ContentStatusDot.Margin = new Thickness(2 * _contentScale, 0, 0, 0);
            ScalePanelChromeChildren(TitleBarButtons, _contentScale);
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

        private int DipToPixels(double dip)
        {
            return (int)Math.Ceiling(dip * GetRasterizationScale());
        }

        private double GetRasterizationScale()
        {
            var scale = OverlayRoot?.XamlRoot?.RasterizationScale ?? 1.0;
            return scale <= 0 ? 1.0 : scale;
        }

    }
}

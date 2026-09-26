using TrenchHQ.Helpers;
using TrenchHQ.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;

namespace TrenchHQ.Controls
{
    public sealed partial class WebsiteWidget : UserControl, IDisposable
    {
        private SavedWidgetDefinition? _definition;
        private WebView2? _browser;
        private bool _disposed;
        private bool _compatible = true;
        private bool _isDocked;
        private Window? _hostWindow;
        private AppWindow? _hostAppWindow;
        private XamlRoot? _observedRoot;
        private CoreWebView2MemoryUsageTargetLevel? _memoryLevel;
        private WidgetContentStatus _contentStatus = new(WidgetContentState.Empty, "Not configured");
        internal bool IsInteractionLocked { get; private set; }
        internal WidgetContentStatus ContentStatus => _contentStatus;
        internal event EventHandler? ContentStatusChanged;

        public WebsiteWidget()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += (_, _) => { ObserveRoot(null); CloseBrowser(); };
            BrowserHost.SizeChanged += async (_, _) => await EnsureBrowserAsync();
            RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => UpdateMemoryPolicy());
        }

        internal void SetHostWindow(Window window)
        {
            _hostWindow = window;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            _hostAppWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
            _hostAppWindow.Changed += OnHostWindowChanged;
            window.Activated += OnHostActivated;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_disposed) return;
            ObserveRoot(XamlRoot);
            await EnsureBrowserAsync();
        }

        private void ObserveRoot(XamlRoot? root)
        {
            if (_observedRoot == root) return;
            if (_observedRoot != null) _observedRoot.Changed -= OnRootChanged;
            _observedRoot = root;
            if (root != null) root.Changed += OnRootChanged;
        }

        private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateMemoryPolicy();
        private void OnHostWindowChanged(AppWindow sender, AppWindowChangedEventArgs args) => UpdateMemoryPolicy();
        private void OnHostActivated(object sender, WindowActivatedEventArgs args) =>
            DispatcherQueue.TryEnqueue(UpdateMemoryPolicy);

        private bool WantsLowMemory => WebsiteWidgetRules.UseLowMemory(
            XamlRoot?.IsHostVisible == true && _hostAppWindow?.IsVisible != false,
            _hostAppWindow?.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized },
            Visibility == Visibility.Visible);

        private void UpdateMemoryPolicy()
        {
            if (_disposed || !IsLoaded) return;
            var core = _browser?.CoreWebView2;
            if (core == null)
            {
                if (!WantsLowMemory) _ = EnsureBrowserAsync();
                return;
            }
            var level = WantsLowMemory ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal;
            if (_memoryLevel == level) return;
            try
            {
                // Keep live scripts/connections intact; do not mix this with TrySuspend/Resume.
                core.MemoryUsageTargetLevel = level;
                _memoryLevel = level;
            }
            catch (Exception error) when (error is System.Runtime.InteropServices.COMException or NotImplementedException)
            {
                // Older runtimes can reject this optional hint. Never close a live page for it.
                System.Diagnostics.Debug.WriteLine("WebView2 memory hint unavailable: " + error.GetType().Name);
            }
        }

        internal void Configure(SavedWidgetDefinition? definition, PanelWidgetHostContext? context = null)
        {
            var host = context ?? PanelWidgetHostRules.ForOverlay();
            var next = definition?.Type == PanelWidgetTypes.Website ? definition : null;
            var reset = next?.Id != _definition?.Id || next?.WebsiteUrl != _definition?.WebsiteUrl;
            _definition = next;
            _compatible = PanelWidgetHostRules.SupportsWidget(PanelWidgetTypes.Website, host);
            _isDocked = host.HostType == PanelWidgetHostType.DockedBar;
            if (reset) IsInteractionLocked = false;
            ApplyLock();
            if (reset || next?.WebsiteEnabled != true || !_compatible) CloseBrowser();
            ApplyZoom();
            if (!_compatible)
                SetContentStatus(new WidgetContentStatus(WidgetContentState.Unavailable, "Unavailable"));
            else if (next?.WebsiteEnabled != true)
                SetContentStatus(new WidgetContentStatus(WidgetContentState.Empty, "Not configured"));
            else if (!WebsiteWidgetRules.TryUrl(next.WebsiteUrl, out _))
                SetContentStatus(new WidgetContentStatus(WidgetContentState.Error, "Error"));
            else if (reset || _browser == null)
                SetContentStatus(new WidgetContentStatus(WidgetContentState.Loading, "Loading"));
            if (IsLoaded) _ = EnsureBrowserAsync();
        }

        private async Task EnsureBrowserAsync()
        {
            if (_disposed || !IsLoaded) return;
            var definition = _definition;
            if (!_compatible)
            {
                SetContentStatus(new WidgetContentStatus(WidgetContentState.Unavailable, "Unavailable"));
                return;
            }
            if (definition?.WebsiteEnabled != true)
            {
                SetContentStatus(new WidgetContentStatus(WidgetContentState.Empty, "Not configured"));
                return;
            }
            if (!Guid.TryParseExact(definition.Id, "N", out var widgetId)
                || !WebsiteWidgetRules.TryUrl(definition.WebsiteUrl, out var url))
            {
                SetContentStatus(new WidgetContentStatus(WidgetContentState.Error, "Error"));
                return;
            }
            if (_isDocked && !WebsiteWidgetRules.CanLoadInViewport(BrowserHost.ActualWidth, BrowserHost.ActualHeight))
            {
                if (_browser != null) CloseBrowser();
                SetContentStatus(new WidgetContentStatus(WidgetContentState.Error, "Error"));
                return;
            }
            if (_browser != null) return;
            if (WantsLowMemory) return; // Do not start a browser for a panel that is not visible yet.

            var browser = new WebView2();
            _browser = browser;
            BrowserHost.Children.Add(browser);
            ApplyLock();
            SetContentStatus(new WidgetContentStatus(WidgetContentState.Loading, "Loading"));
            try
            {
                // Keep old logins in place; new widgets share runtime processes, not cookies.
                var localFolder = ApplicationData.Current.LocalFolder.Path;
                var legacyFolder = Path.Combine(localFolder, "WebsiteProfiles", widgetId.ToString("N"));
                var storage = WebsiteWidgetRules.BrowserStorage(localFolder, widgetId, Directory.Exists(legacyFolder));
                var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, storage.Folder, null);
                if (_browser != browser || _disposed) return;
                if (storage.ProfileName == null)
                    await browser.EnsureCoreWebView2Async(environment);
                else
                {
                    var options = environment.CreateCoreWebView2ControllerOptions();
                    options.ProfileName = storage.ProfileName;
                    await browser.EnsureCoreWebView2Async(environment, options);
                }
                if (_browser != browser || _disposed) return;
                var core = browser.CoreWebView2;
                core.Settings.IsWebMessageEnabled = false;
                core.Settings.AreHostObjectsAllowed = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsPasswordAutosaveEnabled = false;
                core.Settings.IsGeneralAutofillEnabled = false;
                core.NavigationStarting += (sender, args) =>
                {
                    if (!WebsiteWidgetRules.TryUrl(args.Uri, out _))
                    {
                        args.Cancel = true;
                        SetContentStatus(new WidgetContentStatus(WidgetContentState.Error, "Error"));
                    }
                    else SetContentStatus(new WidgetContentStatus(WidgetContentState.Loading, "Loading"));
                };
                core.NavigationCompleted += (_, args) =>
                {
                    if (_browser != browser) return;
                    SetContentStatus(args.IsSuccess
                        ? new WidgetContentStatus(WidgetContentState.Live, "Live")
                        : new WidgetContentStatus(WidgetContentState.Error, "Error"));
                    if (args.IsSuccess) ApplyZoom();
                };
                core.NewWindowRequested += (_, args) =>
                {
                    args.Handled = true;
                };
                core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
                core.LaunchingExternalUriScheme += (_, args) => args.Cancel = true;
                core.DownloadStarting += (_, args) =>
                { args.Cancel = true; };
                core.ProcessFailed += (_, _) =>
                {
                    if (_browser != browser) return;
                    CloseBrowser();
                    SetContentStatus(new WidgetContentStatus(WidgetContentState.Error, "Error"));
                };
                ApplyZoom();
                core.Navigate(url);
                UpdateMemoryPolicy();
            }
            catch
            {
                if (_browser != browser) return;
                CloseBrowser();
                SetContentStatus(new WidgetContentStatus(WidgetContentState.Error, "Error"));
            }
        }

        private async void ApplyZoom()
        {
            var core = _browser?.CoreWebView2;
            if (core == null) return;
            // WinUI does not expose Controller.ZoomFactor. Only adjust presentation,
            // using a bounded invariant number; never read page content or credentials.
            var zoom = WebsiteWidgetRules.Zoom(_definition?.WebsiteZoom ?? 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            try { await core.ExecuteScriptAsync("document.documentElement.style.zoom = " + zoom + ";"); }
            catch { /* Navigation or teardown may replace the document. */ }
        }

        private void ApplyLock()
        {
            var locked = IsInteractionLocked;
            InteractionShield.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
            if (_browser != null)
            {
                _browser.IsHitTestVisible = !locked;
                _browser.IsTabStop = !locked;
            }
        }

        internal void ToggleInteractionLock()
        {
            IsInteractionLocked = !IsInteractionLocked;
            ApplyLock();
        }

        internal async Task ReloadAsync()
        {
            if (_browser?.CoreWebView2 != null) _browser.CoreWebView2.Reload();
            else await EnsureBrowserAsync();
        }
        internal void GoHome()
        {
            if (_browser?.CoreWebView2 != null && WebsiteWidgetRules.TryUrl(_definition?.WebsiteUrl, out var url))
                _browser.CoreWebView2.Navigate(url);
        }
        internal async Task OpenInBrowserAsync()
        {
            var url = _browser?.CoreWebView2?.Source ?? _definition?.WebsiteUrl;
            if (!WebsiteWidgetRules.TryUrl(url, out var safeUrl)) return;
            try { await Launcher.LaunchUriAsync(new Uri(safeUrl)); }
            catch { }
        }

        private void SetContentStatus(WidgetContentStatus status)
        {
            if (status == _contentStatus) return;
            _contentStatus = status;
            ContentStatusChanged?.Invoke(this, EventArgs.Empty);
        }

        private void CloseBrowser()
        {
            var browser = _browser;
            _browser = null;
            _memoryLevel = null;
            BrowserHost.Children.Clear();
            browser?.Close();
        }

        internal void InvalidateContentMeasure() => WidgetRoot.InvalidateMeasure();
        public void Dispose()
        {
            _disposed = true;
            ObserveRoot(null);
            if (_hostAppWindow != null) _hostAppWindow.Changed -= OnHostWindowChanged;
            if (_hostWindow != null) _hostWindow.Activated -= OnHostActivated;
            _hostAppWindow = null;
            _hostWindow = null;
            CloseBrowser();
        }
    }
}

using TrenchHQ.Core.Panels;
using TrenchHQ.Presentation;
using System;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;
using Windows.UI;

namespace TrenchHQ
{
    public sealed partial class MainWindow : Window
    {
        private AppWindow? _appWindow;
        public static new MainWindow? Current { get; private set; }

        public MainWindow()
        {
            InitializeComponent();
            Current = this;
            RootNav.IsPaneOpen = SettingsService.ReadMainNavigationOpen();
            UpdateMainNavigationWidth();
            ConfigureTitleBar();
            RootNav.SelectedItem = NavGeneral;
            SetActiveView("General");
            Closed += OnClosed;
        }

        private void OnNavSelectionChanged(NavigationView _, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is NavigationViewItem item)
            {
                var tag = item.Tag?.ToString() ?? "General";
                SetActiveView(tag);
            }
        }

        private void OnMainNavigationPaneOpened(NavigationView sender, object args)
        {
            if (!ReferenceEquals(sender, RootNav))
            {
                return;
            }

            SettingsService.SaveMainNavigationOpen(true);
            UpdateMainNavigationWidth();
        }

        private void OnMainNavigationPaneClosed(NavigationView sender, object args)
        {
            if (!ReferenceEquals(sender, RootNav))
            {
                return;
            }

            SettingsService.SaveMainNavigationOpen(false);
            UpdateMainNavigationWidth();
        }

        private void UpdateMainNavigationWidth()
        {
            NavigationPaneColumn.Width = new GridLength(RootNav.IsPaneOpen
                ? RootNav.OpenPaneLength
                : RootNav.CompactPaneLength);
        }

        private void SetActiveView(string tag)
        {
            DashboardView.Visibility = tag == "General" ? Visibility.Visible : Visibility.Collapsed;
            WidgetsView.Visibility = tag == "Widgets" ? Visibility.Visible : Visibility.Collapsed;
            ProvidersView.Visibility = tag == "Providers" ? Visibility.Visible : Visibility.Collapsed;
            DesktopSetupView.Visibility = tag == "DesktopSetup" ? Visibility.Visible : Visibility.Collapsed;
            HelpView.Visibility = tag == "Help" ? Visibility.Visible : Visibility.Collapsed;
            if (tag == "Widgets")
            {
                _ = WidgetsTab.ActivateAsync();
            }
            else if (tag == "Providers")
            {
                _ = ProvidersTab.ActivateAsync();
            }
        }

        internal async Task OpenWidgetAsync(string widgetId)
        {
            RootNav.SelectedItem = NavWidgets;
            SetActiveView("Widgets");
            await WidgetsTab.OpenWidgetAsync(widgetId);
        }

        internal void OpenProviders()
        {
            RootNav.SelectedItem = NavProviders;
            SetActiveView("Providers");
        }


        private void ConfigureTitleBar()
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleBarDragRegion);

            var hwnd = WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            appWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "TrenchHQ.ico"));
            _appWindow = appWindow;
            appWindow.Closing += OnAppWindowClosing;

            if (!AppWindowTitleBar.IsCustomizationSupported())
            {
                return;
            }

            var titleBar = appWindow.TitleBar;

            titleBar.ButtonBackgroundColor = Color.FromArgb(255, 0, 0, 0);
            titleBar.ButtonForegroundColor = Color.FromArgb(255, 245, 245, 245);
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 28, 28, 28);
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(255, 38, 38, 38);
            titleBar.ButtonInactiveBackgroundColor = Color.FromArgb(255, 0, 0, 0);
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 120, 120, 120);
        }

        internal void ShowAndActivate()
        {
            _appWindow?.Show();
            Activate();
        }

        internal void Hide()
        {
            _appWindow?.Hide();
        }

        internal void UpdateDesktopDisplayState(bool isRunning)
        {
            DashboardTab?.SetDesktopDisplayState(isRunning);
            DesktopSetupTab?.RefreshRuntimeState();
        }

        private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (Application.Current is App app && !app.IsQuitting)
            {
                args.Cancel = true;
                app.HideDashboard();
            }
        }

        private void OnClosed(object sender, WindowEventArgs args)
        {
            Closed -= OnClosed;
            if (_appWindow != null)
            {
                _appWindow.Closing -= OnAppWindowClosing;
            }

            if (ReferenceEquals(Current, this))
            {
                Current = null;
            }
        }

    }
}

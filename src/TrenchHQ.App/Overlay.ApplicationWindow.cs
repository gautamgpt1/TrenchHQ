using TrenchHQ.Core.Widgets;
using TrenchHQ.Infrastructure.Windows;
using TrenchHQ.Interop;
using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace TrenchHQ
{
    public sealed partial class Overlay
    {
        private readonly ApplicationWindowPin _applicationPin = new();
        private readonly DispatcherTimer _applicationWatch = new() { Interval = TimeSpan.FromSeconds(1) };
        private bool _fittingApplication;
        private bool _closed;

        private void KeepOverlayOnScreen(AppWindow window)
        {
            var work = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Nearest).WorkArea;
            if (window.Size.Width > Math.Max(1, work.Width - 16) || window.Size.Height > Math.Max(1, work.Height - 16))
                QueueSizeUpdate();
            var x = Math.Clamp(window.Position.X, work.X, Math.Max(work.X, work.X + work.Width - window.Size.Width));
            var y = Math.Clamp(window.Position.Y, work.Y, Math.Max(work.Y, work.Y + work.Height - window.Size.Height));
            if (x == window.Position.X && y == window.Position.Y) return;
            _isRestoringPosition = true;
            try { window.Move(new PointInt32(x, y)); }
            finally { _isRestoringPosition = false; }
        }

        private WindowPinBounds GetApplicationBounds()
        {
            if (!TryGetAppWindow(out var window) || !window.IsVisible
                || window.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }) return default;
            var point = ApplicationWindowArea.TransformToVisual(OverlayRoot)
                .TransformPoint(new global::Windows.Foundation.Point(0, 0));
            var scale = GetRasterizationScale();
            var origin = new ApplicationClientPoint();
            if (!ClientToScreen(WinRT.Interop.WindowNative.GetWindowHandle(this), ref origin)) return default;
            return new WindowPinBounds(origin.X + (int)Math.Ceiling(point.X * scale),
                origin.Y + (int)Math.Ceiling(point.Y * scale),
                (int)Math.Floor(ApplicationWindowArea.ActualWidth * scale),
                (int)Math.Floor(ApplicationWindowArea.ActualHeight * scale));
        }

        [StructLayout(LayoutKind.Sequential)] private struct ApplicationClientPoint { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref ApplicationClientPoint point);

        private async void OnChooseApplicationClick(object sender, RoutedEventArgs e)
        {
            if (_closed) return;
            if (_applicationPin.IsPinned) { ReleaseApplication(); return; }
            if (!GetApplicationBounds().Fits(0, 0))
            {
                ApplicationWindowStatus.Text = "Increase overlay width or application height in Desktop Setup before choosing an application.";
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
                await _applicationPin.PinAsync(choice.Window, GetApplicationBounds(), WinRT.Interop.WindowNative.GetWindowHandle(this), choice.Mode);
                if (_applicationPin.IsPinned) await _applicationPin.FitAsync(GetApplicationBounds());
                if (!_closed && _applicationPin.IsPinned)
                {
                    ApplicationWindowStatus.Text = "Pinned: " + choice.Window.Label;
                    SetApplicationContentStatus(new WidgetContentStatus(WidgetContentState.Live, "Live"));
                    _applicationWatch.Start();
                }
            }
            catch (Exception error)
            {
                ReleaseApplication();
                ApplicationWindowStatus.Text = error.Message;
                SetApplicationContentStatus(new WidgetContentStatus(WidgetContentState.Error, "Error"));
            }
            finally { _fittingApplication = false; RefreshApplicationButtons(); }
        }

        private void RefreshApplicationButtons()
        {
            ApplicationActions.Visibility = _definition.UseApplicationWindow ? Visibility.Visible : Visibility.Collapsed;
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
            ApplicationWindowStatus.Text = "Use Pin window in the title bar to pin an application here.";
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
                WindowPinBounds bounds;
                do
                {
                    bounds = GetApplicationBounds();
                    await _applicationPin.FitAsync(bounds);
                }
                while (!_closed && _applicationPin.IsPinned && bounds != GetApplicationBounds());
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
    }
}

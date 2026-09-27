using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace TrenchHQ.Infrastructure.Windows
{
    internal readonly record struct WindowPinBounds(int X, int Y, int Width, int Height)
    {
        internal bool Fits(int minimumWidth, int minimumHeight) =>
            Width >= Math.Max(200, minimumWidth) && Height >= Math.Max(120, minimumHeight);

        internal bool Contains(WindowPinBounds window) => window.Width > 0 && window.Height > 0
            && window.X >= X && window.Y >= Y
            && (long)window.X + window.Width <= (long)X + Width
            && (long)window.Y + window.Height <= (long)Y + Height;
    }

    internal sealed record ApplicationWindowChoice(IntPtr Handle, uint ProcessId, long Started, string Label);

    // Session-only pin ownership. The native helper restores the target even if this process exits.
    internal sealed class ApplicationWindowPin : IDisposable
    {
        private static readonly HashSet<IntPtr> Claimed = [];
        private ApplicationWindowChoice? _window;
        private IntPtr _owner;
        private ApplicationWindowPinMode _mode;
        private int _generation;
        internal bool IsPinned => _window != null;
        internal bool IsAlive => _window != null && SameWindow(_window) && ApplicationWindowNative.IsAttached(_window.Handle);
        internal string Label => _window?.Label ?? string.Empty;

        internal static ApplicationWindowChoice[] GetWindows()
        {
            var result = new List<ApplicationWindowChoice>();
            EnumWindows((hwnd, _) =>
            {
                if (TryChoice(hwnd, out var choice) && !Claimed.Contains(hwnd) && !ApplicationWindowNative.IsAttached(hwnd)) result.Add(choice!);
                return true;
            }, IntPtr.Zero);
            return result.OrderBy(item => item.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }

        internal async Task PinAsync(ApplicationWindowChoice choice, WindowPinBounds bounds, IntPtr owner,
            ApplicationWindowPinMode mode = ApplicationWindowPinMode.NativeLock)
        {
            if (_window != null) throw new InvalidOperationException("Unpin the current window first.");
            if (!SameWindow(choice) || !TryChoice(choice.Handle, out _))
                throw new InvalidOperationException("That window is no longer available. Refresh the list and choose it again.");
            if (mode == ApplicationWindowPinMode.EmbeddedLock && !SupportsEmbedding(choice.Handle))
                throw new InvalidOperationException("This Windows-hosted app uses Native lock. Select Native lock and try again.");
            if (!bounds.Fits(0, 0)) throw new InvalidOperationException("Increase the panel's content area before pinning a window.");
            if (!Claimed.Add(choice.Handle)) throw new InvalidOperationException("That window is already pinned to another panel.");
            _window = choice;
            _owner = owner;
            _mode = mode;
            try
            {
                ApplicationWindowNative.Attach(choice.Handle, owner, mode);
                await FitAsync(bounds);
            }
            catch { Unpin(); throw; }
        }

        internal async Task FitAsync(WindowPinBounds bounds)
        {
            var window = _window ?? throw new InvalidOperationException("Choose a window first.");
            var generation = ++_generation;
            if (!bounds.Fits(0, 0)) throw new InvalidOperationException("Increase the panel's content area before pinning a window.");
            if (!SameWindow(window) || !IsWindowVisible(window.Handle) || !IsUncloaked(window.Handle) || IsHungAppWindow(window.Handle))
                throw new InvalidOperationException("The app window is no longer available for pinning.");
            if (!ApplicationWindowNative.IsAttached(window.Handle))
                throw new InvalidOperationException("The window pin was released. Choose the app again.");
            var targetDpi = GetDpiForWindow(window.Handle);
            if (!ApplicationWindowNative.Position(window.Handle, _owner, bounds))
                throw new InvalidOperationException("Windows refused to position this window.");
            // Check visible containment, not exact outer bounds (which include invisible resize borders).
            WindowPinBounds? observed = null;
            var containedSamples = 0;
            for (var attempt = 0; attempt < 15; attempt++)
            {
                await Task.Delay(100);
                if (generation != _generation || _window != window) return;
                if (!SameWindow(window)) throw new InvalidOperationException("The selected window closed.");
                if (!IsWindowVisible(window.Handle) || !IsUncloaked(window.Handle))
                    throw new InvalidOperationException("The app window became hidden. Open it again before pinning.");
                var currentDpi = GetDpiForWindow(window.Handle);
                if (currentDpi != 0 && currentDpi != targetDpi)
                {
                    // A cross-monitor WM_DPICHANGED handler can rescale the first requested size.
                    // Apply our physical bounds again only after an actual DPI transition.
                    targetDpi = currentDpi;
                    containedSamples = 0;
                    if (!ApplicationWindowNative.Position(window.Handle, _owner, bounds))
                        throw new InvalidOperationException("Windows refused to position the window after its display scale changed.");
                    continue;
                }
                var surface = _mode == ApplicationWindowPinMode.NativeLock ? window.Handle : GetParent(window.Handle);
                GetWindowThreadProcessId(surface, out var surfacePid);
                if (surface != IntPtr.Zero && surfacePid == window.ProcessId && !IsIconic(surface) && !IsZoomed(surface)
                    && TryVisibleBounds(surface, out var actual))
                {
                    observed = actual;
                    if (bounds.Contains(actual) && IsBehindOwner(surface))
                    {
                        // Panel layout/activation may raise the host while this fit is awaiting samples.
                        containedSamples = 0;
                        if (!ApplicationWindowNative.Position(window.Handle, _owner, bounds))
                            throw new InvalidOperationException("Windows refused to bring the app above its panel.");
                        continue;
                    }
                    containedSamples = bounds.Contains(actual) ? containedSamples + 1 : 0;
                    if (containedSamples >= 2)
                    {
                        return;
                    }
                }
                else containedSamples = 0;
            }
            var detail = observed is { } measured
                ? $" Window: {measured.Width} × {measured.Height} at ({measured.X}, {measured.Y}); panel: {bounds.Width} × {bounds.Height} at ({bounds.X}, {bounds.Y})."
                : " Windows did not provide visible window bounds.";
            throw new InvalidOperationException("The app did not stay inside the panel after resizing. Its previous position will be restored." + detail);
        }

        private bool IsBehindOwner(IntPtr surface)
        {
            var seen = new HashSet<IntPtr>();
            for (var above = GetWindow(surface, 3); above != IntPtr.Zero && seen.Add(above); above = GetWindow(above, 3))
                if (above == _owner) return true;
            return false;
        }

        private static bool TryVisibleBounds(IntPtr hwnd, out WindowPinBounds bounds)
        {
            using var dpi = new PhysicalPixelScope();
            if (DwmGetWindowAttribute(hwnd, 9, out Rect frame, Marshal.SizeOf<Rect>()) == 0
                && frame.Right > frame.Left && frame.Bottom > frame.Top)
            {
                bounds = frame.Bounds;
                return true;
            }
            var success = GetWindowRect(hwnd, out var outer);
            bounds = outer.Bounds;
            return success;
        }

        internal void Unpin()
        {
            ++_generation;
            var window = _window;
            _window = null;
            if (window == null) return;
            Claimed.Remove(window.Handle);
            if (SameWindow(window) && ApplicationWindowNative.IsAttached(window.Handle))
                ApplicationWindowNative.Detach(window.Handle, _owner);
        }

        public void Dispose() => Unpin();

        private static bool SameWindow(ApplicationWindowChoice choice)
        {
            if (!IsWindow(choice.Handle)) return false;
            GetWindowThreadProcessId(choice.Handle, out var pid);
            if (pid != choice.ProcessId) return false;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                return process.StartTime.ToUniversalTime().Ticks == choice.Started;
            }
            catch { return false; }
        }

        private static bool TryChoice(IntPtr hwnd, out ApplicationWindowChoice? choice)
        {
            choice = null;
            if (!IsWindowVisible(hwnd) || !IsUncloaked(hwnd) || GetWindow(hwnd, 4) != IntPtr.Zero || IsHungAppWindow(hwnd)
                || hwnd == GetShellWindow() || hwnd == GetDesktopWindow()) return false;
            var style = GetWindowLongPtr(hwnd, -16).ToInt64();
            var exStyle = GetWindowLongPtr(hwnd, -20).ToInt64();
            if ((style & 0x40000) == 0 || (style & 0x40000000) != 0 || (exStyle & 0x80) != 0) return false;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == Environment.ProcessId || !IsNonElevated(pid)) return false;
            var title = new StringBuilder(512);
            GetWindowText(hwnd, title, title.Capacity);
            if (title.Length == 0) return false;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                choice = new(hwnd, pid, process.StartTime.ToUniversalTime().Ticks, $"{process.ProcessName} — {title}");
                return true;
            }
            catch { return false; }
        }

        // WS_VISIBLE can remain set on shell-hidden UWP frames and other cloaked windows.
        // Minimized windows are still eligible; native attachment restores them before fitting.
        private static bool IsUncloaked(IntPtr hwnd) =>
            DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked == 0;

        internal static bool SupportsEmbedding(IntPtr hwnd)
        {
            var windowClass = new StringBuilder(256);
            // Windows-hosted UWP frames reject SetParent; keep the supported top-level lock.
            return GetClassName(hwnd, windowClass, windowClass.Capacity) != 0
                && !string.Equals(windowClass.ToString(), "ApplicationFrameWindow", StringComparison.Ordinal);
        }

        private static bool IsNonElevated(uint pid)
        {
            var process = OpenProcess(0x1000, false, pid);
            if (process == IntPtr.Zero) return false;
            try
            {
                if (!ApplicationWindowNative.SupportsProcess(process) || !OpenProcessToken(process, 8, out var token)) return false;
                try { return GetTokenInformation(token, 20, out var elevated, sizeof(int), out _) && elevated == 0; }
                finally { CloseHandle(token); }
            }
            finally { CloseHandle(process); }
        }

        // Host callbacks/async continuations can inherit a different DPI context.
        // All supplied bounds are physical pixels. Never keep a thread DPI override across await.
        private readonly struct PhysicalPixelScope : IDisposable
        {
            private readonly IntPtr _previous;
            public PhysicalPixelScope()
            {
                _previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
                if (_previous == IntPtr.Zero) throw new InvalidOperationException("Windows could not establish physical-pixel window positioning.");
            }
            public void Dispose() => SetThreadDpiAwarenessContext(_previous);
        }

        [StructLayout(LayoutKind.Sequential)] private struct Rect
        {
            public int Left, Top, Right, Bottom;
            internal WindowPinBounds Bounds => new(Left, Top, Right - Left, Bottom - Top);
        }
        private delegate bool EnumWindowProc(IntPtr hwnd, IntPtr data);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr data);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out Rect value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out int value, int size);
        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll")] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll")] private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int information, int size, out int length);
    }
}

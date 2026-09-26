using TrenchHQ.Helpers;
using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class Program
{
    private static ApplicationWindowPinMode mode;
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--cache-only")) { CheckCache(); return 0; }
        mode = args.Contains("--embedded-lock") ? ApplicationWindowPinMode.EmbeddedLock : ApplicationWindowPinMode.NativeLock;
        Application.SetHighDpiMode(args.Contains("--dpi-unaware") ? HighDpiMode.DpiUnaware
            : args.Contains("--dpi-system") ? HighDpiMode.SystemAware : HighDpiMode.PerMonitorV2);
        if (args.Contains("--test-window"))
        {
            // Windows normalizes WINDOWPLACEMENT to the available work area on restore.
            // Start inside it even when another app owns top/left appbar space.
            var workArea = Screen.PrimaryScreen!.WorkingArea;
            using var form = new TestForm
            {
                Text = "TrenchHQ temporary window-pin test", AutoScaleMode = AutoScaleMode.Dpi,
                StartPosition = FormStartPosition.Manual, Location = new Point(workArea.Left + 40, workArea.Top + 40),
                Size = new Size(360, 240), MinimumSize = new Size(300, 180)
            };
            form.Shown += (_, _) =>
            {
                ShowWindow(form.Handle, 4); // Show only the explicitly created test form, without activation.
                Console.WriteLine(form.Handle.ToInt64());
                Console.Out.Flush();
            };
            Application.Run(form);
            return 0;
        }
        using var owner = new Form { Text = "TrenchHQ window-pin test owner", ShowInTaskbar = false, Size = new Size(200, 100) };
        var result = 1;
        owner.Shown += async (_, _) =>
        {
            var existingIndex = Array.IndexOf(args, "--existing-window");
            result = existingIndex >= 0 ? await CheckExistingAsync(new IntPtr(long.Parse(args[existingIndex + 1])), owner.Handle)
                : await CheckAsync(args.Contains("--target-unaware") ? " --dpi-unaware"
                : args.Contains("--target-system") ? " --dpi-system" : "", owner.Handle);
            owner.Close();
        };
        Application.Run(owner);
        return result;
    }

    private static void CheckCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "TrenchHQ-WindowPin-cache-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "TrenchHQ.WindowPin.test.dll");
            var cache = Path.Combine(root, "cache");
            File.WriteAllBytes(source, new byte[] { 1, 2, 3 });
            var results = new string[20];
            Parallel.For(0, results.Length, index => results[index] = ApplicationWindowNative.StageLibrary(source, cache));
            Check(results.Distinct().Count() == 1 && File.ReadAllBytes(results[0]).SequenceEqual(File.ReadAllBytes(source)), "concurrent cache publication reuses one complete verified binary");
            Check(!Directory.EnumerateFiles(cache, "*.tmp", SearchOption.AllDirectories).Any(), "cache publication removes its temporary files");
            // A concurrent Windows rename can retain DELETE access briefly after publication.
            using (var publisher = CreateFile(results[0], 0x10000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                Check(!publisher.IsInvalid, "test holds the published file with rename access");
                Check(ApplicationWindowNative.StageLibrary(source, cache) == results[0],
                    "cache verification tolerates a concurrent publisher's rename handle");
            }
            File.WriteAllBytes(source, new byte[] { 4, 5, 6 });
            var changed = ApplicationWindowNative.StageLibrary(source, cache);
            Check(changed != results[0] && File.ReadAllBytes(results[0]).SequenceEqual(new byte[] { 1, 2, 3 }), "changed build bytes get a new path without overwriting the previous version");
            File.WriteAllBytes(changed, new byte[] { 9 });
            try { ApplicationWindowNative.StageLibrary(source, cache); throw new Exception("Damaged cache accepted"); }
            catch (InvalidOperationException) { Check(true, "damaged cached binaries are rejected before loading"); }
        }
        finally { Directory.Delete(root, recursive: true); } // Only this test's fresh GUID directory; never loaded as code.
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    private static async Task<int> CheckExistingAsync(IntPtr handle, IntPtr owner)
    {
        var choice = ApplicationWindowPin.GetWindows().Single(item => item.Handle == handle);
        using var process = Process.GetProcessById((int)choice.ProcessId);
        if (process.ProcessName.Equals("brave", StringComparison.OrdinalIgnoreCase) || process.ProcessName == "TrenchHQ")
            throw new InvalidOperationException("Excluded application");
        var before = Bounds(handle);
        var beforeStyle = GetWindowLongPtr(handle, -16);
        using var pin = new ApplicationWindowPin();
        try
        {
            var destination = new WindowPinBounds(40, 70, 420, 600);
            await pin.PinAsync(choice, destination, owner, mode);
            Check(destination.Contains(VisibleBounds(GetParent(handle) == IntPtr.Zero ? handle : GetParent(handle))), "existing app is contained");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        finally
        {
            pin.Unpin();
            Check(Bounds(handle) == before && GetWindowLongPtr(handle, -16) == beforeStyle && !ApplicationWindowNative.IsAttached(handle),
                "existing application is restored without closing it");
        }
    }

    private static async Task<int> CheckAsync(string childArguments, IntPtr ownerHandle)
    {
        using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--test-window" + childArguments)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            WindowStyle = ProcessWindowStyle.Hidden
        })!;
        using var pin = new ApplicationWindowPin();
        try
        {
            var handle = new IntPtr(long.Parse((await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!));
            await Task.Delay(250);
            var choice = ApplicationWindowPin.GetWindows().Single(item => item.Handle == handle);
            var original = Bounds(handle);
            var originalControls = GetWindowLongPtr(handle, -16).ToInt64() & 0xCF0000;
            var destination = new WindowPinBounds(original.X + 20, original.Y + 20, original.Width + 24, original.Height + 24);
            try
            {
                Check(SendMessage(handle, 0x8002, new IntPtr(1), IntPtr.Zero) == IntPtr.Zero, "test app cloaks its own window");
                Check(IsWindowVisible(handle) && DwmGetWindowAttribute(handle, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0,
                    "cloaked window reproduces the misleading visible flag");
                Check(!ApplicationWindowPin.GetWindows().Any(item => item.Handle == handle), "cloaked window is excluded from the picker");
                try { await pin.PinAsync(choice, destination, ownerHandle, mode); throw new Exception("Cloaked stale selection accepted"); }
                catch (InvalidOperationException)
                {
                    Check(!pin.IsPinned && !ApplicationWindowNative.IsAttached(handle) && Bounds(handle) == original,
                        "window cloaked after selection is rejected without attaching or moving it");
                }
            }
            finally { SendMessage(handle, 0x8002, IntPtr.Zero, IntPtr.Zero); }
            Check(ApplicationWindowPin.GetWindows().Any(item => item.Handle == handle), "uncloaked app becomes selectable again");
            var hiddenDuringPin = pin.PinAsync(choice, destination, ownerHandle, mode);
            try
            {
                Check(SendMessage(handle, 0x8002, new IntPtr(1), IntPtr.Zero) == IntPtr.Zero, "test app cloaks while pin verification is pending");
                try { await hiddenDuringPin; throw new Exception("Hidden pin reported success"); }
                catch (InvalidOperationException error)
                {
                    Check(error.Message.Contains("hidden") && !pin.IsPinned && !ApplicationWindowNative.IsAttached(handle),
                        "window hidden during pin verification cannot report success");
                }
            }
            finally { SendMessage(handle, 0x8002, IntPtr.Zero, IntPtr.Zero); pin.Unpin(); }
            Check(Bounds(handle) == original && GetParent(handle) == IntPtr.Zero,
                $"hidden-during-pin failure restores the original window (expected {original}, actual {Bounds(handle)}, parent {GetParent(handle)})");
            try
            {
                var coveredPin = pin.PinAsync(choice, destination, ownerHandle, mode);
                SetWindowPos(ownerHandle, new IntPtr(-1), 0, 0, 0, 0, 0x13);
                await coveredPin;
                var surface = mode == ApplicationWindowPinMode.NativeLock ? handle : GetParent(handle);
                var seen = new HashSet<IntPtr>();
                var covered = false;
                for (var above = GetWindow(surface, 3); above != IntPtr.Zero && seen.Add(above); above = GetWindow(above, 3))
                    covered |= above == ownerHandle;
                Check(!covered, "pin verification puts the app above a panel raised while fitting");
            }
            finally { pin.Unpin(); SetWindowPos(ownerHandle, new IntPtr(-2), 0, 0, 0, 0, 0x13); }
            SendMessage(handle, 0x8003, new IntPtr(1), IntPtr.Zero);
            try
            {
                await pin.PinAsync(choice, destination, ownerHandle, mode);
                Check((GetWindowLongPtr(handle, -16).ToInt64() & 0xCF0000) != 0,
                    "app-managed window controls may remain while pinned");
                var beforeMove = Bounds(handle);
                SetWindowPos(handle, IntPtr.Zero, beforeMove.X + 80, beforeMove.Y + 60, beforeMove.Width + 50, beforeMove.Height + 50, 0x14);
                Check(Bounds(handle) == beforeMove, "app with retained controls still cannot move or resize while pinned");
                foreach (var command in new[] { 0xF020, 0xF030, 0xF120 })
                    SendMessage(handle, 0x112, new IntPtr(command), IntPtr.Zero);
                Check(!IsIconic(handle) && !IsZoomed(handle) && Bounds(handle) == beforeMove,
                    "retained minimize maximize and restore controls cannot release containment");
            }
            finally { pin.Unpin(); SendMessage(handle, 0x8003, IntPtr.Zero, IntPtr.Zero); }
            Check(Bounds(handle) == original && (GetWindowLongPtr(handle, -16).ToInt64() & 0xCF0000) == originalControls,
                "app-managed controls and original bounds restore on unpin");
            Check(Enum.GetValues<ApplicationWindowPinMode>().Length == 2, "only the two locked methods are available");
            try { await pin.PinAsync(choice, destination, ownerHandle, (ApplicationWindowPinMode)2); throw new Exception("Unknown method accepted"); }
            catch (InvalidOperationException) { Check(!pin.IsPinned && Bounds(handle) == original, "unknown native method is rejected without changing the app"); }
            await pin.PinAsync(choice, destination, ownerHandle, mode);
            var packagedHelper = Path.Combine(AppContext.BaseDirectory, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TrenchHQ.WindowPin.version")).Trim());
            var loadedHelper = child.Modules.Cast<ProcessModule>().Single(module => module.ModuleName.StartsWith("TrenchHQ.WindowPin.", StringComparison.Ordinal));
            Check(!loadedHelper.FileName.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase), "target loads helper outside the deployment folder");
            Check(File.ReadAllBytes(loadedHelper.FileName).SequenceEqual(File.ReadAllBytes(packagedHelper)), "cached helper matches the packaged binary");
            using (File.Open(packagedHelper, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(true, "packaged helper permits exclusive access while target is pinned");
            Check(Bounds(handle) == destination, "cross-process pin reaches exact bounds");
            Check((GetWindowLongPtr(handle, -16).ToInt64() & 0xCF0000) == 0, "pinned app has no title bar, sizing frame or system controls");
            var hitPoint = new IntPtr(((destination.Y + 2) << 16) | ((destination.X + 100) & 0xFFFF));
            var hit = SendMessage(handle, 0x84, IntPtr.Zero, hitPoint).ToInt32();
            Check(hit != 2 && (hit < 10 || hit > 17), "standard pinned frame no longer exposes caption or resize hit targets");
            Check(!ApplicationWindowPin.GetWindows().Any(item => item.Handle == handle), "claimed window is excluded from another picker");
            using (var duplicate = new ApplicationWindowPin())
            {
                try { await duplicate.PinAsync(choice, destination, ownerHandle, mode); throw new Exception("Duplicate pin succeeded"); }
                catch (InvalidOperationException) { Console.WriteLine("PASS duplicate pin rejected"); }
            }
            pin.Unpin();
            await Task.Delay(200);
            Check(Bounds(handle) == original, "unpin restores original bounds");
            Check((GetWindowLongPtr(handle, -16).ToInt64() & 0xCF0000) == originalControls, "unpin restores original window controls");
            Check((GetWindowLongPtr(handle, -20).ToInt64() & 8) == 0, "unpin restores non-topmost state");
            SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x4013);
            await Task.Delay(100);
            await pin.PinAsync(choice, destination, ownerHandle, mode);
            pin.Unpin();
            await Task.Delay(200);
            Check((GetWindowLongPtr(handle, -20).ToInt64() & 8) != 0, "original topmost state is preserved");
            SetWindowPos(handle, new IntPtr(-2), 0, 0, 0, 0, 0x4013);
            await Task.Delay(100);
            var pending = pin.PinAsync(choice, destination, ownerHandle, mode);
            pin.Unpin();
            await pending;
            await Task.Delay(200);
            Check(Bounds(handle) == original && !pin.IsPinned, "unpin during pending placement cannot repin the window");
            var small = new WindowPinBounds(40, 40, 210, 130);
            await pin.PinAsync(choice, small, ownerHandle, mode);
            Check(small.Contains(VisibleBounds(handle)), "tiling pin succeeds below the reported 300 by 180 minimum");
            pin.Unpin();
            await Task.Delay(200);
            Check(Bounds(handle) == original && !pin.IsPinned, "small pin restores the original window intact");
            SendMessage(handle, 0x8001, new IntPtr(1), new IntPtr(destination.Width));
            if (mode == ApplicationWindowPinMode.NativeLock)
            {
                try { await pin.PinAsync(choice, destination, ownerHandle, mode); throw new Exception("Oversized window accepted"); }
                catch (InvalidOperationException error)
                {
                    Check(error.Message.Contains("Window:") && error.Message.Contains("panel:"), "oversize rejection includes measured bounds");
                }
                await Task.Delay(250);
                Check(Bounds(handle) == original && !pin.IsPinned, "app that overrides tiling size is rejected and restored");
            }
            else
            {
                await pin.PinAsync(choice, destination, ownerHandle, mode);
                Check(Bounds(GetParent(handle)) == destination, "oversized embedded app is clipped by exact container bounds");
                pin.Unpin();
                await Task.Delay(250);
                Check(Bounds(handle) == original && !pin.IsPinned, "oversized embedded app restores on unpin");
            }
            Check((GetWindowLongPtr(handle, -16).ToInt64() & 0xCF0000) == originalControls, "failed pin restores window controls");
            SendMessage(handle, 0x8001, new IntPtr(2), new IntPtr(destination.Width));
            await pin.PinAsync(choice, destination, ownerHandle, mode);
            Check(Bounds(handle) != destination && destination.Contains(VisibleBounds(handle)), "smaller contained window is accepted without exact-size equality");
            pin.Unpin();
            await Task.Delay(200);
            await pin.PinAsync(choice, destination, ownerHandle, mode);
            var resized = destination with { Width = destination.Width + 20 };
            await pin.FitAsync(resized);
            Check(Bounds(handle) == resized, "fit follows a new reserved size");
            pin.Dispose();
            await Task.Delay(200);
            Check(Bounds(handle) == original, "dispose restores the application without closing it");
            var previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
            Rectangle[] monitorBounds;
            try { monitorBounds = Screen.AllScreens.Select(screen => screen.Bounds).ToArray(); }
            finally { SetThreadDpiAwarenessContext(previousDpi); }
            foreach (var monitor in monitorBounds)
            {
                var originalDpi = GetDpiForWindow(handle);
                var acrossMonitors = new WindowPinBounds(monitor.X + 40, monitor.Y + 40, 420, 600);
                await pin.PinAsync(choice, acrossMonitors, ownerHandle, mode);
                Check(acrossMonitors.Contains(VisibleBounds(handle)), $"DPI-scaling test app fits monitor at {monitor.X},{monitor.Y}");
                await Task.Delay(500);
                Check(acrossMonitors.Contains(VisibleBounds(handle)), "monitor transition remains contained after settling");
                if (GetDpiForWindow(handle) != originalDpi) SendMessage(handle, 0x8004, IntPtr.Zero, IntPtr.Zero);
                pin.Unpin();
                await Task.Delay(300);
                Check(Bounds(handle) == original, "unpin restores exact bounds even when the app rescales during a DPI transition");
            }
            foreach (var state in new[] { 3, 2 })
            {
                ShowWindow(handle, state);
                await Task.Delay(200);
                var before = Placement(handle);
                choice = ApplicationWindowPin.GetWindows().Single(item => item.Handle == handle);
                Check(choice.Handle == handle, $"state {state} is selectable without manual restoration");
                await pin.PinAsync(choice, destination, ownerHandle, mode);
                Check(!IsIconic(handle) && !IsZoomed(handle) && Bounds(handle) == destination,
                    $"state {state} automatically restores and fits");
                pin.Unpin();
                await Task.Delay(250);
                var after = Placement(handle);
                Check(after.ShowCommand == before.ShowCommand && after.Normal.Equals(before.Normal),
                    $"unpin restores state {state} and original normal bounds");
                try { await pin.PinAsync(choice, new WindowPinBounds(40, 40, 100, 80), ownerHandle, mode); throw new Exception("Undersized panel pin succeeded"); }
                catch (InvalidOperationException) { }
                await Task.Delay(250);
                Check(Placement(handle).ShowCommand == before.ShowCommand && !pin.IsPinned,
                    $"failed pin restores original state {state}");
                ShowWindow(handle, 9);
                await Task.Delay(150);
            }
            ShowWindow(handle, 3);
            ShowWindow(handle, 2);
            await Task.Delay(150);
            await pin.PinAsync(choice, destination, ownerHandle, mode);
            pin.Unpin();
            await Task.Delay(250);
            Check(IsIconic(handle), "original minimized-from-maximized window is minimized on unpin");
            ShowWindow(handle, 9);
            await Task.Delay(150);
            Check(IsZoomed(handle), "restoring the unpinned minimized window retains its maximized state");
            ShowWindow(handle, 9);
            await Task.Delay(150);
            ShowWindow(handle, 2);
            await Task.Delay(100);
            pending = pin.PinAsync(choice, destination, ownerHandle, mode);
            pin.Unpin();
            await pending;
            await Task.Delay(200);
            Check(IsIconic(handle) && !pin.IsPinned, "cancel during automatic restore returns to minimized state");
            ShowWindow(handle, 9);
            await Task.Delay(100);
            var originalStyle = GetWindowLongPtr(handle, -16);
            try
            {
                SetWindowLongPtr(handle, -16, new IntPtr(originalStyle.ToInt64() & ~0x40000L));
                Check(!ApplicationWindowPin.GetWindows().Any(item => item.Handle == handle), "non-resizable window cannot be pinned");
            }
            finally { SetWindowLongPtr(handle, -16, originalStyle); }
            await pin.PinAsync(choice, destination, ownerHandle, mode);
            Check(PostMessage(handle, 0x10, IntPtr.Zero, IntPtr.Zero), "close is sent to the selected application HWND");
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(!pin.IsAlive, "closed application is detected");
            pin.Unpin();
            Console.WriteLine("All native window-pin checks passed; only the temporary test window was moved.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            pin.Unpin();
            if (!child.HasExited)
            {
                child.CloseMainWindow();
                if (!child.WaitForExit(2000)) child.Kill(); // Only this test's own child process.
            }
        }
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        Console.WriteLine("PASS " + label);
    }
    private sealed class TestForm : Form
    {
        private int _resizeMode;
        private int _triggerWidth;
        private bool _keepWindowControls;
        private uint _rescaleRestoreDpi;
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x8001) { _resizeMode = message.WParam.ToInt32(); _triggerWidth = message.LParam.ToInt32(); return; }
            if (message.Msg == 0x8003) { _keepWindowControls = message.WParam != IntPtr.Zero; return; }
            if (message.Msg == 0x8004) { _rescaleRestoreDpi = GetDpiForWindow(Handle); return; }
            if (message.Msg == 0x2E0 && _rescaleRestoreDpi != 0)
            {
                var previousDpi = _rescaleRestoreDpi;
                _rescaleRestoreDpi = 0;
                base.WndProc(ref message);
                var rect = Bounds(Handle);
                var scale = (message.WParam.ToInt64() & 0xffff) / (double)previousDpi;
                SetWindowPos(Handle, IntPtr.Zero, rect.X, rect.Y, (int)Math.Round(rect.Width * scale),
                    (int)Math.Round(rect.Height * scale), 0x14);
                return;
            }
            if (message.Msg == 0x7C && message.WParam.ToInt32() == -16 && _keepWindowControls)
            {
                // Reproduce Visual Studio retaining its own controls during an external style change.
                Marshal.WriteInt32(message.LParam, sizeof(int), Marshal.ReadInt32(message.LParam, sizeof(int)) | 0xCF0000);
            }
            if (message.Msg == 0x8002)
            {
                var cloak = message.WParam.ToInt32();
                var surface = GetParent(Handle);
                message.Result = new IntPtr(DwmSetWindowAttribute(surface == IntPtr.Zero ? Handle : surface, 13, ref cloak, sizeof(int)));
                return;
            }
            base.WndProc(ref message);
        }
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (Bounds(Handle).Width != _triggerWidth) return;
            var mode = _resizeMode;
            _resizeMode = 0;
            if (mode != 0) Size = mode == 1 ? new Size(800, 600) : new Size(320, 200);
        }
    }
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
    private static WindowPinBounds Bounds(IntPtr handle)
    {
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            if (!GetWindowRect(handle, out var rect)) throw new Exception("Cannot read test window bounds");
            return new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        }
        finally { SetThreadDpiAwarenessContext(previous); }
    }
    private static WindowPinBounds VisibleBounds(IntPtr handle)
    {
        if (GetParent(handle) != IntPtr.Zero) return Bounds(handle);
        if (DwmGetWindowAttribute(handle, 9, out Rect rect, Marshal.SizeOf<Rect>()) != 0) return Bounds(handle);
        return new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out Rect value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref int value, int size);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct PlacementData
    {
        public uint Length, Flags, ShowCommand;
        public Point Minimum, Maximum;
        public Rect Normal;
    }
    private static PlacementData Placement(IntPtr handle)
    {
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            var placement = new PlacementData { Length = (uint)Marshal.SizeOf<PlacementData>() };
            if (!GetWindowPlacement(handle, ref placement)) throw new Exception("Cannot read test placement");
            return placement;
        }
        finally { SetThreadDpiAwarenessContext(previous); }
    }
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hwnd, ref PlacementData placement);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint relation);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
}

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using TrenchHQ.Helpers;

internal static partial class Program
{
    private static readonly List<object> Results = [];
    private static int failures;
    private static int inputSequence;
    private static string scenario = "";
    private static string targetDpi = "";
    private static bool snapKeyboard;
    private static bool integrated;
    private static bool inputEnabled;

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(args.Contains("--unaware") ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
        if (args.Contains("--target"))
        {
            using var target = new TargetForm(args.Contains("--custom"));
            target.Shown += (_, _) => { Native.ShowWindow(target.Handle, 4); Console.WriteLine(target.Handle.ToInt64()); Console.Out.Flush(); };
            Application.Run(target);
            return 0;
        }
        targetDpi = args.Contains("--target-unaware") ? " --unaware" : "";
        snapKeyboard = args.Contains("--snap-keyboard");
        integrated = args.Contains("--integrated");
        inputEnabled = !args.Contains("--no-input");
        if (args.Contains("--owner")) return RunOwner(args);
        using var host = new Form { Text = "TrenchHQ disposable containment test", StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(680, 120, 540, 410), TopMost = true };
        using var area = new Panel { Bounds = new Rectangle(12, 45, 480, 290), BackColor = Color.DarkSlateBlue };
        host.Controls.Add(area);
        host.Controls.Add(new Label { Text = "Disposable windows only. Testing native lock and embedding.", AutoSize = true, Location = new Point(12, 12) });
        host.Shown += async (_, _) =>
        {
            try
            {
                if (!args.Contains("--crash-only"))
                    foreach (string target in args.Contains("--chrome") ? new[] { "chrome" } : new[] { "standard-frame", "custom-caption" })
                        foreach (string mode in integrated ? Enum.GetNames<ApplicationWindowPinMode>() : args.Contains("--guarded-embed") ? new[] { "guarded-embed-lock" }
                            : args.Contains("--lock-only") ? new[] { "lock" } : new[] { "lock", "embed", "embed-lock" })
                            await RunCase(host, area, target, mode);
                if (args.Contains("--crash-only"))
                    foreach (string mode in integrated ? Enum.GetNames<ApplicationWindowPinMode>() : args.Contains("--guarded-embed") ? new[] { "guarded-embed-lock" } : new[] { "lock", "embed", "embed-lock" }) await CrashCase(mode);
            }
            catch (Exception error) { Record("harness error", false, error.ToString()); }
            finally
            {
                string output = Path.Combine(AppContext.BaseDirectory, $"results-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
                File.WriteAllText(output, JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("Results: " + output);
                host.Close();
            }
        };
        Application.Run(host);
        return failures == 0 ? 0 : 1;
    }

    private static async Task RunCase(Form host, Panel area, string target, string mode)
    {
        scenario = target + "/" + mode + (targetDpi.Length > 0 ? "/unaware-target" : "");
        bool chrome = target == "chrome";
        string marker = "TrenchHQContainmentProbe-" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(Environment.ProcessPath!, "--target" + (target == "custom-caption" ? " --custom" : "") + targetDpi)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (chrome)
        {
            string profile = Path.Combine(Path.GetTempPath(), marker);
            Directory.CreateDirectory(profile);
            Record("disposable Chrome profile", null, profile);
            start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"))
                { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
            string html = "<title>" + marker + "</title><body style='background:#eee'><input aria-label='Probe input' style='position:absolute;left:25px;top:70px' oninput=\"document.title='TrenchHQProbe typed:'+this.value\"><button style='position:absolute;left:25px;top:115px' onclick=\"this.dataset.count=1+Number(this.dataset.count||0);this.innerText='Clicked '+this.dataset.count;document.title=this.innerText\">Test click</button></body>";
            foreach (string argument in new[] { "--user-data-dir=" + profile, "--no-first-run", "--no-default-browser-check", "--disable-background-networking", "--disable-sync", "--force-renderer-accessibility", "--window-position=100,100", "--window-size=460,340", "--app=data:text/html," + Uri.EscapeDataString(html) }) start.ArgumentList.Add(argument);
        }
        using var child = Process.Start(start)!;
        nint hwnd = 0, hook = 0;
        long originalStyle = 0;
        Native.Rect original = default;
        bool embedded = false;
        try
        {
            if (chrome)
            {
                for (int i = 0; i < 100 && hwnd == 0; i++)
                {
                    Native.EnumWindows((candidate, _) =>
                    {
                        Native.GetWindowThreadProcessId(candidate, out uint candidatePid);
                        if (candidatePid == child.Id && Native.Title(candidate).Contains(marker)) hwnd = candidate;
                        return true;
                    }, 0);
                    if (hwnd == 0) await Task.Delay(100);
                }
                Require(hwnd != 0, "isolated Chrome window found in owned process");
                Native.ShowWindow(hwnd, 4);
                Native.SetWindowPos(hwnd, -1, 100, 100, 580, 420, 0x430);
            }
            else hwnd = new nint(long.Parse((await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!));
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != child.Id) throw new Exception("Safety check: target is not the process created by this harness.");
            await Task.Delay(200);
            original = Native.Bounds(hwnd);
            originalStyle = Native.GetWindowLongPtr(hwnd, -16).ToInt64();
            if (integrated && !inputEnabled)
            {
                await CheckIntegratedPin(host, area, hwnd, mode, original, originalStyle, default, default);
                return;
            }
            var caption = FindHit(hwnd, 2);
            var edge = FindHit(hwnd, 11);
            Require(caption != null && edge != null, "unlocked move and resize hit targets exist");
            Require(await Drag(hwnd, caption!.Value, new Point(65, 45)), "unlocked physical drag positive control moves");
            Native.Place(hwnd, original);
            await Task.Delay(200);
            Require(await Drag(hwnd, edge!.Value, new Point(55, 0)), "unlocked physical resize positive control resizes");
            Native.Place(hwnd, original);
            await Task.Delay(200);
            await InputCheck(hwnd);
            Record("unlocked input positive control", true, "Typing and clicking checked before modifying parent or hooking.");
            if (snapKeyboard && mode == "lock")
            {
                Require(Native.GetForegroundWindow() == hwnd, "only the disposable target receives unlocked Win+Right");
                Native.Chord(0x5B, 0x27);
                await Task.Delay(400);
                Require(Native.Bounds(hwnd) != original, "unlocked Win+Right positive control changes bounds");
                Native.Escape();
                Native.ShowWindow(hwnd, 4);
                Native.Place(hwnd, original);
                await Task.Delay(250);
            }

            if (integrated)
            {
                await CheckIntegratedPin(host, area, hwnd, mode, original, originalStyle, caption!.Value, edge!.Value);
                return;
            }

            if (mode.StartsWith("embed"))
            {
                Native.SetWindowLongPtr(hwnd, -16, new nint((originalStyle & ~0x80CF0000L) | 0x40000000L));
                Marshal.SetLastPInvokeError(0);
                Native.SetParent(hwnd, area.Handle);
                embedded = Native.GetParent(hwnd) == area.Handle;
                Require(embedded, "SetParent changes actual parent");
                Native.Place(hwnd, new Native.Rect(0, 0, area.ClientSize.Width, area.ClientSize.Height));
                await Task.Delay(400);
                Record("DPI after embedding", null, $"host={Native.GetDpiForWindow(area.Handle)} target={Native.GetDpiForWindow(hwnd)} context={Native.GetWindowDpiAwarenessContext(hwnd)}");
                Record("initial embedded rectangle", null, Native.Bounds(hwnd).ToString());
            }

            // Cache genuine unlocked hit positions before interception changes them to HTCLIENT.
            caption = FindHit(hwnd, 2);
            edge = FindHit(hwnd, 11);
            if (mode != "embed")
            {
                if (mode == "guarded-embed-lock") Native.Place(hwnd, Native.Bounds(area.Handle));
                hook = Native.AttachLock(hwnd, host.Handle, mode == "guarded-embed-lock");
                Require(hook != 0, "native hook attaches on selected target thread");
                if (mode == "guarded-embed-lock")
                {
                    nint parent = Native.GetParent(hwnd);
                    Native.GetWindowThreadProcessId(parent, out uint parentPid);
                    Require(parent != 0 && parentPid == child.Id, "embedded container is owned by target, not host process");
                    // Capture post-style-change hit points in this container's coordinate space.
                    caption = FindHit(hwnd, 2, raw: true);
                    edge = FindHit(hwnd, 11, raw: true);
                }
            }
            var pinned = Native.Bounds(hwnd);
            if (caption != null)
            {
                bool moved = await Drag(hwnd, caption.Value, new Point(60, 35));
                Record("physical caption drag changes bounds", mode == "embed" ? null : !moved, moved.ToString());
                if (moved) ResetPinned(hwnd, pinned, embedded, area);
            }
            else Record("caption drag", null, "No caption hit region after standard controls removed; not a drag-lock test.");
            if (edge != null)
            {
                bool resized = await Drag(hwnd, edge.Value, new Point(45, 0));
                Record("physical edge drag changes bounds", mode == "embed" ? null : !resized, resized.ToString());
                if (resized) ResetPinned(hwnd, pinned, embedded, area);
            }
            else Record("edge drag", null, "No resize hit region after standard controls removed.");

            await InputCheck(hwnd);
            if (snapKeyboard && mode == "lock")
            {
                Require(Native.GetForegroundWindow() == hwnd, "only the disposable target receives Win+Right");
                Native.Chord(0x5B, 0x27);
                await Task.Delay(400);
                Record("Win+Right snap changes locked bounds", null, $"before={pinned} after={Native.Bounds(hwnd)}");
                Native.Escape(); // Dismiss any snap-assist UI created by this test.
                ResetPinned(hwnd, pinned, embedded, area);
            }
            var before = Native.Bounds(hwnd);
            // Target's own WndProc executes SetWindowPos, including a deliberate notification bypass.
            if (chrome) Native.SetWindowPos(hwnd, 0, 80, 60, 650, 450, 0x14);
            else Native.Send(hwnd, 0x8001, 0, 0);
            await Task.Delay(200);
            Record(chrome ? "ordinary external reposition blocked" : "ordinary self-reposition blocked", mode == "embed" ? null : Native.Bounds(hwnd) == before,
                $"before={before} after={Native.Bounds(hwnd)}");
            if (embedded && mode == "embed")
            {
                var parentBounds = Native.Bounds(area.Handle);
                var outside = new Point(parentBounds.Right + 10, parentBounds.Top + 100);
                nint hit = Native.WindowFromPoint(outside);
                Require(hit != hwnd && !Native.IsChild(hwnd, hit), "embedded overflow is clipped outside parent");
            }
            ResetPinned(hwnd, pinned, embedded, area);
            if (chrome) Native.SetWindowPos(hwnd, 0, 80, 60, 650, 450, 0x414);
            else Native.Send(hwnd, 0x8001, 1, 0);
            await Task.Delay(200);
            Record("NOSENDCHANGING bypass moves target", null, (Native.Bounds(hwnd) != pinned).ToString());
            ResetPinned(hwnd, pinned, embedded, area);
            Native.Send(hwnd, 0x112, 0xF030, 0);
            await Task.Delay(200);
            Record("maximize command blocked", mode == "embed" ? null : !Native.IsZoomed(hwnd), Native.IsZoomed(hwnd).ToString());
            if (hook != 0)
            {
                Native.Send(hwnd, 0x112, 0xF020, 0);
                await Task.Delay(100);
                Require(!Native.IsIconic(hwnd), "minimize system command is blocked");
                Native.ShowWindow(hwnd, 2);
                await Task.Delay(100);
                Record("direct cross-process ShowWindow minimize bypass", null, Native.IsIconic(hwnd).ToString());
                Native.ShowWindow(hwnd, 4);
                ResetPinned(hwnd, pinned, embedded, area);
                await Task.Delay(200);
                await InputCheck(hwnd);
            }

            if (hook != 0) { Require(Native.DetachLock(hwnd, hook), "native hook detaches"); hook = 0; }
            if (embedded) { Native.SetParent(hwnd, 0); embedded = false; }
            Native.SetWindowLongPtr(hwnd, -16, new nint(originalStyle));
            Native.ShowWindow(hwnd, 4);
            Native.Place(hwnd, original);
            await Task.Delay(300);
            Require(Native.Bounds(hwnd) == original && Native.GetParent(hwnd) == 0
                && Native.GetWindowLongPtr(hwnd, -16).ToInt64() == originalStyle, "unpin restores parent, exact bounds and styles");
            caption = FindHit(hwnd, 2);
            Require(caption != null && await Drag(hwnd, caption.Value, new Point(60, 30)), "physical dragging works again after unpin");
        }
        catch (Exception error) { Record("case stopped", false, error.Message); }
        finally
        {
            if (hwnd != 0 && Native.IsWindow(hwnd))
            {
                if (hook != 0) Native.DetachLock(hwnd, hook);
                if (embedded) Native.SetParent(hwnd, 0);
                if (originalStyle != 0) Native.SetWindowLongPtr(hwnd, -16, new nint(originalStyle));
                Native.PostMessage(hwnd, 0x10, 0, 0);
            }
            if (!child.HasExited)
            {
                try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (TimeoutException) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            }
            Record("disposable target exited", child.HasExited, "Only the process created by this case was closed.");
        }
    }

    private static int RunOwner(string[] args)
    {
        nint target = new(long.Parse(args[Array.IndexOf(args, "--owner") + 1]));
        string mode = args[^1];
        Native.GetWindowThreadProcessId(target, out uint targetPid);
        using var targetProcess = Process.GetProcessById((int)targetPid);
        if (targetProcess.MainModule?.FileName != Environment.ProcessPath) return 2;
        using var owner = new Form { Text = "TrenchHQ disposable crash owner", Bounds = new Rectangle(680, 120, 520, 400), StartPosition = FormStartPosition.Manual, TopMost = true };
        using var pin = new ApplicationWindowPin();
        owner.Shown += async (_, _) =>
        {
            Native.ShowWindow(owner.Handle, 4);
            if (integrated)
            {
                try
                {
                    var choice = ApplicationWindowPin.GetWindows().Single(item => item.Handle == target);
                    await pin.PinAsync(choice, new WindowPinBounds(680, 180, 480, 290), owner.Handle, Enum.Parse<ApplicationWindowPinMode>(mode));
                    Console.WriteLine("READY");
                }
                catch (Exception error) { Console.WriteLine("FAILED " + error.Message); }
                Console.Out.Flush();
                return;
            }
            if (mode.StartsWith("embed"))
            {
                Native.SetWindowLongPtr(target, -16, new nint((Native.GetWindowLongPtr(target, -16).ToInt64() & ~0x80CF0000L) | 0x40000000L));
                Native.SetParent(target, owner.Handle);
                Native.Place(target, new Native.Rect(0, 0, 480, 290));
            }
            nint hook = mode == "embed" ? 0 : Native.AttachLock(target, owner.Handle, mode == "guarded-embed-lock");
            Console.WriteLine(mode == "embed" || hook != 0 ? "READY" : "FAILED");
            Console.Out.Flush();
        };
        Application.Run(owner);
        return 0;
    }

    private static async Task CrashCase(string mode)
    {
        scenario = "owner-crash/" + mode;
        using var target = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--target --custom")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, WindowStyle = ProcessWindowStyle.Hidden })!;
        nint hwnd = new(long.Parse((await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!));
        long style = Native.GetWindowLongPtr(hwnd, -16).ToInt64();
        var originalBounds = Native.Bounds(hwnd);
        using var owner = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"{(integrated ? "--integrated " : "")}--owner {hwnd.ToInt64()} {mode}")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, WindowStyle = ProcessWindowStyle.Hidden })!;
        try
        {
            Require(await owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "READY", "disposable lock/embed owner ready");
            owner.Kill(); // Deliberate crash of ONLY this test's own owner, not the target or TrenchHQ.
            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(1000);
            bool alive = Native.IsWindow(hwnd);
            bool guarded = integrated || mode == "lock" || mode == "guarded-embed-lock";
            Record("target HWND survives owner crash", guarded ? alive : null, $"window={alive} processExited={target.HasExited}");
            if (alive)
            {
                Record("parent after owner crash", null, Native.GetParent(hwnd).ToString());
                Require(Native.GetProp(hwnd, integrated ? ApplicationWindowNative.StateProperty : "TrenchHQ.ContainmentProbe.State.v1") == 0, "owner-death timer releases native lock");
                if (integrated) Require(Native.Bounds(hwnd) == originalBounds, "owner crash restores original bounds without managed cleanup");
                var caption = FindHit(hwnd, 2);
                if (inputEnabled) Record("target movable after owner crash", guarded ? caption != null && await Drag(hwnd, caption.Value, new Point(55, 30)) : null, "");
                else { Require(caption != null, "owner crash restores caption hit target"); Record("physical drag skipped", null, "--no-input; interactive verification not performed"); }
                if (integrated || mode == "guarded-embed-lock") Require(Native.GetParent(hwnd) == 0
                    && Native.GetWindowLongPtr(hwnd, -16).ToInt64() == style, "owner death detaches guarded child and restores styles");
            }
        }
        catch (Exception error) { Record("crash case stopped", false, error.Message); }
        finally
        {
            if (!owner.HasExited) { owner.Kill(); await owner.WaitForExitAsync(); }
            if (Native.IsWindow(hwnd))
            {
                Native.SetParent(hwnd, 0);
                Native.SetWindowLongPtr(hwnd, -16, new nint(style));
                Native.PostMessage(hwnd, 0x10, 0, 0);
            }
            if (!target.HasExited)
            {
                try { await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (TimeoutException) { target.Kill(); await target.WaitForExitAsync(); }
            }
        }
    }

    private static void ResetPinned(nint hwnd, Native.Rect bounds, bool embedded, Panel area)
    {
        if (Native.GetParent(hwnd) != 0) Native.Place(hwnd, new Native.Rect(0, 0, area.ClientSize.Width, area.ClientSize.Height));
        else Native.Place(hwnd, bounds);
    }

    private static Point? FindHit(nint hwnd, int wanted, bool raw = false)
    {
        var bounds = Native.Bounds(hwnd);
        IEnumerable<Point> points = wanted == 2
            ? Enumerable.Range(3, 12).Select(y => new Point(Math.Min(160, bounds.Width / 2), y * 3))
            : Enumerable.Range(1, 10).Select(x => new Point(bounds.Width - x, bounds.Height / 2));
        foreach (var p in points)
            if (Native.Send(hwnd, raw ? Native.RegisterWindowMessage("TrenchHQ.ContainmentProbe.RawHit.v1") : 0x84,
                0, Native.Packed(new Point(bounds.Left + p.X, bounds.Top + p.Y))) == wanted) return p;
        return null;
    }

    private static async Task<bool> Drag(nint hwnd, Point offset, Point delta)
    {
        var before = Native.Bounds(hwnd);
        // Activate through a verified blank point in the disposable target before dragging.
        await Click(hwnd, new Point(before.Left + before.Width / 2, before.Bottom - 55));
        var start = new Point(before.Left + offset.X, before.Top + offset.Y);
        Native.SetCursorPos(start.X, start.Y);
        await Task.Delay(100);
        CheckPoint(hwnd, start);
        Native.Mouse(2);
        try
        {
            await Task.Delay(100);
            for (int i = 1; i <= 10; i++)
            {
                Native.SetCursorPos(start.X + delta.X * i / 10, start.Y + delta.Y * i / 10);
                await Task.Delay(25);
            }
        }
        finally { Native.Mouse(4); }
        await Task.Delay(200);
        return Native.Bounds(hwnd) != before;
    }

    private static async Task InputCheck(nint hwnd)
    {
        if (!inputEnabled) { Record("keyboard and mouse input skipped", null, "--no-input; interactive verification not performed"); return; }
        var root = AutomationElement.FromHandle(hwnd);
        var input = FindControl(root, "Probe input");
        Record("input target geometry", null, $"window={Native.Bounds(hwnd)} input={input.Current.BoundingRectangle}");
        await Click(hwnd, ControlCenter(input));
        Record("input focus after click", null, $"focused={input.Current.HasKeyboardFocus} focusedElement={AutomationElement.FocusedElement?.Current.Name}");
        if (Native.GetParent(hwnd) != 0)
        {
            Native.SetFocus(hwnd);
            await Task.Delay(100);
            await Click(hwnd, ControlCenter(input));
            Record("embedded focus handoff", null, $"focusedElement={AutomationElement.FocusedElement?.Current.Name}");
        }
        Require(Native.GetForegroundWindow() == Native.GetAncestor(hwnd, 2), "input target is foreground before typing");
        string expected = "probe" + ++inputSequence;
        Native.SelectAll();
        Native.Type(expected);
        await Task.Delay(300);
        string value = ((ValuePattern)input.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
        Record("input observation", null, $"value={value} title={Native.Title(hwnd)}");
        if (value != expected)
        {
            var r = Native.Bounds(hwnd);
            using var bitmap = new Bitmap(r.Width, r.Height);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(r.Width, r.Height));
            string image = Path.Combine(AppContext.BaseDirectory, $"input-failure-{scenario.Replace('/', '-')}.png");
            bitmap.Save(image);
            Record("input failure screenshot", null, image);
        }
        Require(value == expected, "real keyboard input reaches embedded or locked app");
        var button = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
            .Cast<AutomationElement>().Single(element => element.Current.Name == "Test click" || element.Current.Name.StartsWith("Clicked "));
        string beforeName = button.Current.Name;
        await Click(hwnd, ControlCenter(button));
        await Task.Delay(300);
        Require(button.Current.Name.StartsWith("Clicked ") && button.Current.Name != beforeName, "real mouse input reaches app button");
    }

    private static AutomationElement FindControl(AutomationElement root, string name) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name))
            ?? throw new Exception("Test control not found: " + name);

    private static Point ControlCenter(AutomationElement control)
    {
        var bounds = control.Current.BoundingRectangle;
        if (bounds.IsEmpty) throw new Exception("Test control has no visible bounds: " + control.Current.Name);
        return new Point((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
    }

    private static async Task Click(nint hwnd, Point p)
    {
        Native.SetCursorPos(p.X, p.Y);
        await Task.Delay(50);
        CheckPoint(hwnd, p);
        Native.Mouse(2);
        Native.Mouse(4);
        await Task.Delay(150);
    }

    private static void CheckPoint(nint hwnd, Point p)
    {
        nint hit = Native.WindowFromPoint(p);
        if (hit != hwnd && !Native.IsChild(hwnd, hit))
            throw new Exception($"Input safety check: point {p} is covered by another window; no click was sent.");
    }

    private static void Require(bool passed, string name)
    {
        Record(name, passed, "");
        if (!passed) throw new Exception(name);
    }
    private static void Record(string name, bool? passed, string detail)
    {
        if (passed == false) failures++;
        Results.Add(new { scenario, name, passed, detail });
        Console.WriteLine($"{(passed == null ? "OBSERVE" : passed.Value ? "PASS" : "FAIL")} [{scenario}] {name}: {detail}");
    }

    private sealed class TargetForm : Form
    {
        private readonly bool custom;
        public TargetForm(bool customCaption)
        {
            custom = customCaption;
            Text = "TrenchHQ disposable containment target";
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Bounds = new Rectangle(100, 100, 460, 340);
            TopMost = true;
            var input = new TextBox { Bounds = new Rectangle(25, 70, 240, 30), AccessibleName = "Probe input" };
            input.TextChanged += (_, _) => Text = "TrenchHQProbe typed:" + input.Text;
            var button = new Button { Bounds = new Rectangle(25, 115, 180, 35), Text = "Test click" };
            int clicks = 0;
            button.Click += (_, _) => { Text = "TrenchHQProbe clicked:" + ++clicks; button.Text = "Clicked " + clicks; };
            Controls.Add(input);
            Controls.Add(button);
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x8001)
            {
                Native.SetWindowPos(Handle, 0, 80, 60, 650, 450, 0x14u | (m.WParam != 0 ? 0x400u : 0));
                return;
            }
            if (custom && m.Msg == 0x84)
            {
                var rect = Native.Bounds(Handle);
                int x = (short)(m.LParam.ToInt64() & 0xffff) - rect.Left;
                int y = (short)((m.LParam.ToInt64() >> 16) & 0xffff) - rect.Top;
                // Deliberately retains custom drag targets even when WS_CAPTION/THICKFRAME are removed.
                if (x >= rect.Width - 5) { m.Result = 11; return; }
                if (y >= 5 && y < 35) { m.Result = 2; return; }
            }
            base.WndProc(ref m);
        }
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal readonly record struct Rect(int Left, int Top, int Right, int Bottom)
    {
        internal int Width => Right - Left;
        internal int Height => Bottom - Top;
        public override string ToString() => $"({Left},{Top}) {Width}x{Height}";
    }
    internal static Rect Bounds(nint hwnd)
    {
        var previous = SetThreadDpiAwarenessContext(-4);
        try { if (!GetWindowRect(hwnd, out var bounds)) throw new Exception("GetWindowRect failed"); return bounds; }
        finally { SetThreadDpiAwarenessContext(previous); }
    }
    internal static void Place(nint hwnd, Rect bounds)
    {
        var previous = SetThreadDpiAwarenessContext(-4);
        try { if (!SetWindowPos(hwnd, 0, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x434)) throw new Exception("SetWindowPos failed"); }
        finally { SetThreadDpiAwarenessContext(previous); }
    }
    internal static nint Packed(Point p) => (nint)((p.Y << 16) | (p.X & 0xffff));
    internal static nint Send(nint hwnd, uint msg, nint wparam, nint lparam)
    {
        if (SendMessageTimeout(hwnd, msg, wparam, lparam, 2, 2500, out nint result) == 0)
            throw new Exception($"Timed out sending {msg:X} to disposable target");
        return result;
    }
    internal static string Title(nint hwnd) { var text = new StringBuilder(256); GetWindowText(hwnd, text, text.Capacity); return text.ToString(); }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort VirtualKey;
        [FieldOffset(10)] public ushort Scan;
        [FieldOffset(12)] public uint KeyFlags;
        [FieldOffset(20)] public uint MouseFlags;
    }
    internal static void Mouse(uint flags) => Inject(new Input { Type = 0, MouseFlags = flags });
    internal static void Type(string text)
    {
        foreach (char c in text) { Inject(new Input { Type = 1, Scan = c, KeyFlags = 4 }); Inject(new Input { Type = 1, Scan = c, KeyFlags = 6 }); }
    }
    internal static void SelectAll()
        => Chord(0x11, 0x41);
    internal static void Chord(ushort modifier, ushort key)
    {
        Inject(new Input { Type = 1, VirtualKey = modifier });
        Inject(new Input { Type = 1, VirtualKey = key });
        Inject(new Input { Type = 1, VirtualKey = key, KeyFlags = 2 });
        Inject(new Input { Type = 1, VirtualKey = modifier, KeyFlags = 2 });
    }
    internal static void Escape()
    {
        Inject(new Input { Type = 1, VirtualKey = 0x1B });
        Inject(new Input { Type = 1, VirtualKey = 0x1B, KeyFlags = 2 });
    }
    private static void Inject(Input input) { if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1) throw new Exception("SendInput failed: " + Marshal.GetLastWin32Error()); }
    [DllImport("WindowLockProbe.dll")] internal static extern nint AttachLock(nint hwnd, nint owner, [MarshalAs(UnmanagedType.Bool)] bool embed = false);
    [DllImport("WindowLockProbe.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DetachLock(nint hwnd, nint hook);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point p);
    [DllImport("user32.dll")] internal static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    internal delegate bool EnumWindowProc(nint hwnd, nint data);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowProc callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetProp(nint hwnd, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern nint SetFocus(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] internal static extern bool ClientToScreen(nint hwnd, ref Point p);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] internal static extern nint GetWindowDpiAwarenessContext(nint hwnd);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetParent(nint hwnd, nint parent);
    [DllImport("user32.dll")] internal static extern nint GetParent(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] internal static extern bool IsZoomed(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool PostMessage(nint hwnd, uint msg, nint wparam, nint lparam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeout(nint hwnd, uint msg, nint wparam, nint lparam, uint flags, uint timeout, out nint result);
}

using TrenchHQ.Helpers;

internal static partial class Program
{
    private static async Task CheckIntegratedPin(Form host, Panel area, nint hwnd, string method,
        Native.Rect original, long originalStyle, Point caption, Point edge)
    {
        var mode = Enum.Parse<ApplicationWindowPinMode>(method);
        var choice = ApplicationWindowPin.GetWindows().Single(item => item.Handle == hwnd);
        var rect = Native.Bounds(area.Handle);
        var bounds = new WindowPinBounds(rect.Left, rect.Top, rect.Width, rect.Height);
        using var pin = new ApplicationWindowPin();
        await pin.PinAsync(choice, bounds, host.Handle, mode);
        Require(pin.IsAlive, "integrated helper attaches to selected target");
        if (mode == ApplicationWindowPinMode.NativeLock) Require(Native.GetParent(hwnd) == 0, "native lock remains top-level");
        else
        {
            var container = Native.GetParent(hwnd);
            Native.GetWindowThreadProcessId(container, out var pid);
            Require(container != 0 && pid == choice.ProcessId, "embedded container belongs to target process");
            Require(Native.Title(container).Length == 0, "embedded container does not display helper text");
            Require((Native.GetWindowLongPtr(container, -16).ToInt64() & 0x1f) == 4, "embedded container uses a dark rectangle instead of a text background");
        }
        if (inputEnabled)
        {
            bool moved = await Drag(hwnd, caption, new Point(50, 25));
            Record("integrated caption drag changes bounds", !moved, moved.ToString());
            await pin.FitAsync(bounds);
            edge = new Point(Native.Bounds(hwnd).Width - 3, Native.Bounds(hwnd).Height / 2);
            bool resized = await Drag(hwnd, edge, new Point(40, 0));
            Record("integrated edge drag changes bounds", !resized, resized.ToString());
        }
        else Record("physical dragging skipped", null, "--no-input; interactive verification not performed");
        await pin.FitAsync(bounds);
        await InputCheck(hwnd);
        var before = Native.Bounds(hwnd);
        Native.SetWindowPos(hwnd, 0, 80, 60, 650, 450, 0x14);
        await Task.Delay(200);
        Record("integrated ordinary reposition blocked", Native.Bounds(hwnd) == before,
            $"before={before} after={Native.Bounds(hwnd)}");
        if (mode != ApplicationWindowPinMode.NativeLock)
        {
            var parent = Native.Bounds(Native.GetParent(hwnd));
            var hit = Native.WindowFromPoint(new Point(parent.Right + 10, parent.Top + 100));
            Require(hit != hwnd && !Native.IsChild(hwnd, hit), "embedded overflow cannot draw or receive clicks outside container");
        }
        await pin.FitAsync(bounds);
        await pin.FitAsync(bounds with { X = bounds.X + 20, Y = bounds.Y + 10, Width = bounds.Width + 20, Height = bounds.Height + 10 });
        await InputCheck(hwnd);
        Require(pin.IsAlive, "authorized panel move and resize retain pin");
        pin.Unpin();
        await Task.Delay(250);
        Require(Native.GetParent(hwnd) == 0 && Native.Bounds(hwnd) == original
            && Native.GetWindowLongPtr(hwnd, -16).ToInt64() == originalStyle, "integrated Unpin restores original parent, bounds and styles");
        await InputCheck(hwnd);
        var releasedCaption = FindHit(hwnd, 2);
        if (inputEnabled) Require(releasedCaption != null && await Drag(hwnd, releasedCaption.Value, new Point(55, 25)), "integrated Unpin restores physical dragging");
        else Require(releasedCaption != null, "integrated Unpin restores caption hit target");
        // Repin and destroy only this case's owner window. A live TrenchHQ process is not required for recovery.
        using var temporaryOwner = new Form { ShowInTaskbar = false };
        await pin.PinAsync(choice, bounds, temporaryOwner.Handle, mode);
        temporaryOwner.Dispose();
        await Task.Delay(750);
        Require(!ApplicationWindowNative.IsAttached(hwnd) && Native.IsWindow(hwnd) && Native.GetParent(hwnd) == 0,
            "owner HWND destruction releases target while owner process remains alive");
        pin.Unpin();
    }
}

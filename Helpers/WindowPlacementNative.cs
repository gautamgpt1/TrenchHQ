using System;
using System.Runtime.InteropServices;

namespace TrenchHQ.Helpers
{
    internal readonly record struct SafeWindowPlacement(int X, int Y, string MonitorDeviceName);

    internal static class WindowPlacementNative
    {
        private const uint MonitorDefaultToNull = 0;
        private const uint MonitorDefaultToNearest = 2;
        private const uint MonitorInfoPrimary = 1;
        private const int DefaultMargin = 32;

        internal static SafeWindowPlacement GetSafePlacement(
            int? requestedX,
            int? requestedY,
            int width,
            int height,
            string? preferredMonitorDeviceName = null)
        {
            var monitor = IntPtr.Zero;
            var useRequestedPosition = requestedX.HasValue && requestedY.HasValue;

            if (!string.IsNullOrWhiteSpace(preferredMonitorDeviceName))
            {
                monitor = FindMonitor(preferredMonitorDeviceName, primaryOnly: false);
                if (monitor == IntPtr.Zero)
                {
                    useRequestedPosition = false;
                }
            }

            if (monitor == IntPtr.Zero && useRequestedPosition)
            {
                var requestedRect = new WindowsAppBarNative.Rect
                {
                    left = requestedX!.Value,
                    top = requestedY!.Value,
                    right = requestedX.Value + Math.Max(1, width),
                    bottom = requestedY.Value + Math.Max(1, height)
                };
                monitor = MonitorFromRect(ref requestedRect, MonitorDefaultToNull);
            }

            if (monitor == IntPtr.Zero)
            {
                monitor = FindMonitor(null, primaryOnly: true);
                useRequestedPosition = false;
            }

            if (monitor == IntPtr.Zero)
            {
                monitor = MonitorFromWindow(IntPtr.Zero, MonitorDefaultToNearest);
            }

            if (!TryGetMonitorInfo(monitor, out var info))
            {
                return new SafeWindowPlacement(requestedX ?? DefaultMargin, requestedY ?? DefaultMargin, string.Empty);
            }

            var workArea = info.WorkArea;
            var desiredX = useRequestedPosition ? requestedX!.Value : workArea.left + DefaultMargin;
            var desiredY = useRequestedPosition ? requestedY!.Value : workArea.top + DefaultMargin;
            var maximumX = Math.Max(workArea.left, workArea.right - Math.Max(1, width));
            var maximumY = Math.Max(workArea.top, workArea.bottom - Math.Max(1, height));
            return new SafeWindowPlacement(
                Math.Clamp(desiredX, workArea.left, maximumX),
                Math.Clamp(desiredY, workArea.top, maximumY),
                info.DeviceName);
        }

        internal static string GetMonitorDeviceName(IntPtr hwnd)
        {
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            return TryGetMonitorInfo(monitor, out var info) ? info.DeviceName : string.Empty;
        }

        private static IntPtr FindMonitor(string? deviceName, bool primaryOnly)
        {
            var result = IntPtr.Zero;
            MonitorEnumProc callback = (monitor, _, _, _) =>
            {
                if (!TryGetMonitorInfo(monitor, out var info))
                {
                    return true;
                }

                var isMatch = primaryOnly
                    ? (info.Flags & MonitorInfoPrimary) != 0
                    : string.Equals(info.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase);
                if (!isMatch)
                {
                    return true;
                }

                result = monitor;
                return false;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            return result;
        }

        private static bool TryGetMonitorInfo(IntPtr monitor, out MonitorInfoEx info)
        {
            info = new MonitorInfoEx
            {
                Size = (uint)Marshal.SizeOf<MonitorInfoEx>(),
                DeviceName = string.Empty
            };
            return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info);
        }

        private delegate bool MonitorEnumProc(
            IntPtr monitor,
            IntPtr deviceContext,
            IntPtr monitorRect,
            IntPtr data);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfoEx
        {
            public uint Size;
            public WindowsAppBarNative.Rect Monitor;
            public WindowsAppBarNative.Rect WorkArea;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayMonitors(
            IntPtr deviceContext,
            IntPtr clipRect,
            MonitorEnumProc callback,
            IntPtr data);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromRect(
            ref WindowsAppBarNative.Rect rect,
            uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
    }
}

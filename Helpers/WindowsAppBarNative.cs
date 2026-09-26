using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace TrenchHQ.Helpers
{
    internal static class WindowsAppBarNative
    {
        internal const uint AbmNew = 0x00000000;
        internal const uint AbmRemove = 0x00000001;
        internal const uint AbmQueryPos = 0x00000002;
        internal const uint AbmSetPos = 0x00000003;
        internal const uint AbmActivate = 0x00000006;
        internal const uint AbmWindowPosChanged = 0x00000009;

        internal const uint AbeTop = 1;
        internal const uint AbeLeft = 0;
        internal const uint AbeRight = 2;
        internal const uint AbeBottom = 3;
        internal const uint AbnPosChanged = 1;
        internal const uint AbnFullscreenApp = 2;

        internal const int GwlStyle = -16;
        internal const int GwlExStyle = -20;
        internal const int GwlpWndProc = -4;
        internal const uint WmActivate = 0x0006;
        internal const uint WmSettingChange = 0x001A;
        internal const uint WmWindowPosChanged = 0x0047;
        internal const uint WmDisplayChange = 0x007E;
        internal const uint WmDeviceChange = 0x0219;
        internal const uint WmDpiChanged = 0x02E0;
        internal const uint MonitorDefaultToPrimary = 1;
        private const uint MonitorInfoPrimary = 1;
        internal const int SmCxScreen = 0;
        internal const int SmCyScreen = 1;
        internal const uint DwmaWindowCornerPreference = 33;
        internal const uint DwmaBorderColor = 34;
        private const uint DwmaExtendedFrameBounds = 9;
        private const uint DwmaCloaked = 14;
        internal const uint DwmwcpDoNotRound = 1;
        internal const uint DwmColorNone = 0xFFFFFFFE;
        internal const long WsBorder = 0x00800000L;
        internal const long WsDlgFrame = 0x00400000L;
        internal const long WsCaption = 0x00C00000L;
        internal const long WsSysMenu = 0x00080000L;
        internal const long WsThickFrame = 0x00040000L;
        internal const long WsMinimizeBox = 0x00020000L;
        internal const long WsMaximizeBox = 0x00010000L;
        internal const long WsPopup = unchecked((long)0x80000000u);
        internal const long WsExAppWindow = 0x00040000L;
        internal const long WsExToolWindow = 0x00000080L;
        internal static readonly IntPtr HwndTopMost = new(-1);
        internal static readonly IntPtr HwndBottom = new(1);
        internal const uint SwpNoSize = 0x0001;
        internal const uint SwpNoMove = 0x0002;
        internal const uint SwpNoActivate = 0x0010;
        internal const uint SwpFrameChanged = 0x0020;

        [StructLayout(LayoutKind.Sequential)]
        internal struct AppBarData
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public Rect rc;
            public IntPtr lParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        internal struct MonitorInfo
        {
            public uint cbSize;
            public Rect rcMonitor;
            public Rect rcWork;
            public uint dwFlags;
        }

        internal delegate IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr deviceContext, ref Rect monitorRect, IntPtr data);
        private delegate bool WindowEnumProc(IntPtr hwnd, IntPtr data);

        internal sealed class DisplayMonitorInfo
        {
            public string DeviceName { get; init; } = string.Empty;
            public bool IsPrimary { get; init; }
            public int Number { get; init; }
            public string Label => IsPrimary ? $"{DeviceName} (Primary)" : DeviceName;
        }

        internal static AppBarData CreateAppBarData(IntPtr hwnd, uint callbackMessage = 0)
        {
            return new AppBarData
            {
                cbSize = (uint)Marshal.SizeOf<AppBarData>(),
                hWnd = hwnd,
                uCallbackMessage = callbackMessage
            };
        }

        internal static Rect GetMonitorRect(IntPtr hwnd)
        {
            return GetMonitorRect(string.Empty, hwnd);
        }

        internal static Rect GetMonitorRect(string? deviceName, IntPtr fallbackHwnd)
        {
            var monitor = FindMonitor(deviceName);
            if (monitor == IntPtr.Zero)
            {
                monitor = MonitorFromWindow(fallbackHwnd, MonitorDefaultToPrimary);
            }

            if (monitor != IntPtr.Zero)
            {
                var info = new MonitorInfo
                {
                    cbSize = (uint)Marshal.SizeOf<MonitorInfo>()
                };
                if (GetMonitorInfo(monitor, ref info))
                {
                    return info.rcMonitor;
                }
            }

            return new Rect
            {
                left = 0,
                top = 0,
                right = GetSystemMetrics(SmCxScreen),
                bottom = GetSystemMetrics(SmCyScreen)
            };
        }

        internal static DisplayMonitorInfo[] GetMonitors()
        {
            var monitors = new List<DisplayMonitorInfo>();
            MonitorEnumProc callback = (IntPtr monitor, IntPtr _, ref Rect __, IntPtr ___) =>
            {
                if (TryGetMonitorInfo(monitor, out var info))
                {
                    monitors.Add(new DisplayMonitorInfo
                    {
                        DeviceName = info.szDevice,
                        Number = DockedBarPlacementRules.GetMonitorNumber(info.szDevice, monitors.Count + 1),
                        IsPrimary = (info.dwFlags & MonitorInfoPrimary) != 0
                    });
                }

                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            return [.. monitors];
        }

        internal static double GetMonitorScale(string? deviceName)
        {
            var monitor = FindMonitor(deviceName);
            return monitor != IntPtr.Zero && GetScaleFactorForMonitor(monitor, out var scale) == 0
                ? scale / 100d : 1d;
        }

        internal static bool IsForegroundWindowOnMonitor(string? deviceName)
        {
            var foreground = GetForegroundWindow();
            var monitor = FindMonitor(deviceName);
            return foreground != IntPtr.Zero
                && monitor != IntPtr.Zero
                && MonitorFromWindow(foreground, MonitorDefaultToPrimary) == monitor;
        }

        internal static bool HasFullscreenWindowOnMonitor(string? deviceName, uint currentProcessId)
        {
            var monitor = FindMonitor(deviceName);
            if (!TryGetMonitorInfo(monitor, out var info))
            {
                return false;
            }

            GetWindowThreadProcessId(GetShellWindow(), out var shellProcessId);
            var found = false;
            WindowEnumProc callback = (IntPtr hwnd, IntPtr _) =>
            {
                GetWindowThreadProcessId(hwnd, out var processId);
                if (processId == 0
                    || processId == currentProcessId
                    || processId == shellProcessId
                    || !IsWindowVisible(hwnd)
                    || MonitorFromWindow(hwnd, MonitorDefaultToPrimary) != monitor
                    || DwmGetWindowAttribute(hwnd, DwmaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                {
                    return true;
                }

                var hasFrame = DwmGetWindowAttribute(
                    hwnd,
                    DwmaExtendedFrameBounds,
                    out Rect frame,
                    Marshal.SizeOf<Rect>()) == 0;
                if (!hasFrame && !GetWindowRect(hwnd, out frame))
                {
                    return true;
                }

                found = frame.left <= info.rcMonitor.left + 2
                    && frame.top <= info.rcMonitor.top + 2
                    && frame.right >= info.rcMonitor.right - 2
                    && frame.bottom >= info.rcMonitor.bottom - 2;
                return !found;
            };
            EnumWindows(callback, IntPtr.Zero);
            return found;
        }

        [DllImport("shcore.dll")] private static extern int GetScaleFactorForMonitor(IntPtr monitor, out int scale);

        internal static string GetPanelName(string edge, string deviceName, DisplayMonitorInfo[] monitors)
        {
            var monitor = monitors.FirstOrDefault(item => string.Equals(item.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                ?? monitors.FirstOrDefault(item => item.IsPrimary);
            return DesktopSetupRules.GetDockedBarName(edge, monitors.Length > 1 ? monitor?.Number : null);
        }

        internal static string ResolveMonitorDeviceName(string? requestedDeviceName)
        {
            var requested = FindMonitor(requestedDeviceName);
            if (TryGetMonitorInfo(requested, out var requestedInfo))
            {
                return requestedInfo.szDevice;
            }

            foreach (var monitor in GetMonitors())
            {
                if (monitor.IsPrimary)
                {
                    return monitor.DeviceName;
                }
            }

            var available = GetMonitors();
            return available.Length > 0 ? available[0].DeviceName : string.Empty;
        }

        private static IntPtr FindMonitor(string? deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                return IntPtr.Zero;
            }

            var result = IntPtr.Zero;
            MonitorEnumProc callback = (IntPtr monitor, IntPtr _, ref Rect __, IntPtr ___) =>
            {
                if (TryGetMonitorInfo(monitor, out var info)
                    && string.Equals(info.szDevice, deviceName, StringComparison.OrdinalIgnoreCase))
                {
                    result = monitor;
                    return false;
                }

                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            return result;
        }

        private static bool TryGetMonitorInfo(IntPtr monitor, out MonitorInfoEx info)
        {
            info = new MonitorInfoEx
            {
                cbSize = (uint)Marshal.SizeOf<MonitorInfoEx>(),
                szDevice = string.Empty
            };
            return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfoEx
        {
            public uint cbSize;
            public Rect rcMonitor;
            public Rect rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [DllImport("shell32.dll")]
        internal static extern UIntPtr SHAppBarMessage(uint dwMessage, ref AppBarData pData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, WindowProc newProc);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr newProc);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
        internal static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, [MarshalAs(UnmanagedType.Bool)] bool bRepaint);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(WindowEnumProc callback, IntPtr data);

        [DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr clipRect, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        internal static extern int DwmSetWindowAttribute(IntPtr hwnd, uint dwAttribute, ref uint pvAttribute, uint cbAttribute);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint dwAttribute, out int value, int valueSize);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint dwAttribute, out Rect value, int valueSize);

    }
}

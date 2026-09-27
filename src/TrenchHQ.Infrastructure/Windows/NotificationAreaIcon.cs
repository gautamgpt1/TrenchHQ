using System;
using System.IO;
using System.Runtime.InteropServices;

namespace TrenchHQ.Infrastructure.Windows
{
    internal sealed record NotificationAreaPanelAction(
        string Id,
        string Name,
        bool IsDocked,
        bool IsMinimized);

    internal sealed class NotificationAreaIcon : IDisposable
    {
        private const uint IconId = 1;
        private const uint CallbackMessage = 0x8001;
        private const uint NimAdd = 0x00000000;
        private const uint NimDelete = 0x00000002;
        private const uint NifMessage = 0x00000001;
        private const uint NifIcon = 0x00000002;
        private const uint NifTip = 0x00000004;
        private const uint WmLButtonDoubleClick = 0x0203;
        private const uint WmRButtonUp = 0x0205;
        private const uint WmContextMenu = 0x007B;
        private const uint MfString = 0x00000000;
        private const uint MfSeparator = 0x00000800;
        private const uint TpmReturnCommand = 0x0100;
        private const uint TpmRightButton = 0x0002;
        private const uint WmNull = 0x0000;
        private const uint OpenDashboardCommand = 1;
        private const uint ToggleDisplayCommand = 2;
        private const uint QuitCommand = 3;
        private const uint FirstPanelCommand = 100;
        private const uint ImageIcon = 1;
        private const uint LoadFromFile = 0x0010;
        private const int SmCxSmallIcon = 49;
        private const int SmCySmallIcon = 50;

        private readonly IntPtr _hwnd;
        private readonly Action _showDashboard;
        private readonly Action _toggleDesktopDisplay;
        private readonly Action _quit;
        private readonly Func<bool> _isDesktopDisplayRunning;
        private readonly Func<NotificationAreaPanelAction[]> _getPanelActions;
        private readonly Action<NotificationAreaPanelAction> _togglePanel;
        private readonly WindowsAppBarNative.WindowProc _wndProc;
        private readonly IntPtr _icon;
        private IntPtr _oldWndProc;
        private bool _isAdded;
        private bool _isDisposed;

        internal NotificationAreaIcon(
            IntPtr hwnd,
            Action showDashboard,
            Action toggleDesktopDisplay,
            Action quit,
            Func<bool> isDesktopDisplayRunning,
            Func<NotificationAreaPanelAction[]> getPanelActions,
            Action<NotificationAreaPanelAction> togglePanel)
        {
            _hwnd = hwnd;
            _showDashboard = showDashboard;
            _toggleDesktopDisplay = toggleDesktopDisplay;
            _quit = quit;
            _isDesktopDisplayRunning = isDesktopDisplayRunning;
            _getPanelActions = getPanelActions;
            _togglePanel = togglePanel;
            _wndProc = WindowProc;

            _icon = LoadImage(
                IntPtr.Zero,
                Path.Combine(AppContext.BaseDirectory, "Assets", "TrenchHQ.ico"),
                ImageIcon,
                GetSystemMetrics(SmCxSmallIcon),
                GetSystemMetrics(SmCySmallIcon),
                LoadFromFile);
            if (_icon == IntPtr.Zero)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            _oldWndProc = WindowsAppBarNative.SetWindowLongPtr(
                _hwnd,
                WindowsAppBarNative.GwlpWndProc,
                _wndProc);

            var data = CreateIconData();
            _isAdded = Shell_NotifyIcon(NimAdd, ref data);
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            if (_isAdded)
            {
                var data = CreateIconData();
                Shell_NotifyIcon(NimDelete, ref data);
                _isAdded = false;
            }

            if (_oldWndProc != IntPtr.Zero)
            {
                WindowsAppBarNative.SetWindowLongPtr(
                    _hwnd,
                    WindowsAppBarNative.GwlpWndProc,
                    _oldWndProc);
                _oldWndProc = IntPtr.Zero;
            }

            DestroyIcon(_icon);
        }

        private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            var oldWndProc = _oldWndProc;
            if (message == CallbackMessage)
            {
                var notification = unchecked((uint)lParam.ToInt64());
                if (notification == WmLButtonDoubleClick)
                {
                    _showDashboard();
                    return IntPtr.Zero;
                }

                if (notification == WmRButtonUp || notification == WmContextMenu)
                {
                    ShowContextMenu();
                    return IntPtr.Zero;
                }
            }

            return oldWndProc != IntPtr.Zero
                ? WindowsAppBarNative.CallWindowProc(oldWndProc, hwnd, message, wParam, lParam)
                : IntPtr.Zero;
        }

        private void ShowContextMenu()
        {
            var menu = CreatePopupMenu();
            if (menu == IntPtr.Zero)
            {
                return;
            }

            try
            {
                AppendMenu(menu, MfString, new UIntPtr(OpenDashboardCommand), "Open TrenchHQ");
                var displayCommand = _isDesktopDisplayRunning()
                    ? "Stop Desktop Display"
                    : "Start Desktop Display";
                AppendMenu(menu, MfString, new UIntPtr(ToggleDisplayCommand), displayCommand);
                var panelActions = _getPanelActions();
                if (panelActions.Length > 0)
                {
                    AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
                    for (var index = 0; index < panelActions.Length; index++)
                    {
                        var action = panelActions[index];
                        var label = action.IsMinimized ? "Restore" : "Minimize";
                        AppendMenu(
                            menu,
                            MfString,
                            new UIntPtr(FirstPanelCommand + (uint)index),
                            $"{label} {action.Name}");
                    }
                }

                AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
                AppendMenu(menu, MfString, new UIntPtr(QuitCommand), "Quit TrenchHQ");

                GetCursorPos(out var cursor);
                SetForegroundWindow(_hwnd);
                var command = TrackPopupMenu(
                    menu,
                    TpmReturnCommand | TpmRightButton,
                    cursor.X,
                    cursor.Y,
                    0,
                    _hwnd,
                    IntPtr.Zero);

                switch (command)
                {
                    case OpenDashboardCommand:
                        _showDashboard();
                        break;
                    case ToggleDisplayCommand:
                        _toggleDesktopDisplay();
                        break;
                    case QuitCommand:
                        _quit();
                        break;
                    default:
                        if (command >= FirstPanelCommand)
                        {
                            var panelIndex = (int)(command - FirstPanelCommand);
                            if (panelIndex < panelActions.Length)
                            {
                                _togglePanel(panelActions[panelIndex]);
                            }
                        }
                        break;
                }

                PostMessage(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);
            }
            finally
            {
                DestroyMenu(menu);
            }
        }

        private NotifyIconData CreateIconData()
        {
            return new NotifyIconData
            {
                cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
                hWnd = _hwnd,
                uID = IconId,
                uFlags = NifMessage | NifIcon | NifTip,
                uCallbackMessage = CallbackMessage,
                hIcon = _icon,
                szTip = "TrenchHQ",
                szInfo = string.Empty,
                szInfoTitle = string.Empty
            };
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NotifyIconData
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X;
            public int Y;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr icon);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr itemId, string? text);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern uint TrackPopupMenu(
            IntPtr menu,
            uint flags,
            int x,
            int y,
            int reserved,
            IntPtr hwnd,
            IntPtr rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyMenu(IntPtr menu);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    }
}

using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace TrenchHQ.Interop
{
    internal static class WindowSubclasser
    {
        private const int WmNclbuttondblclk = 0x00A3;
        private const int GwlpWndproc = -4;

        private static IntPtr _hwnd;
        private static SubclassDelegate? _newWndProc;
        private static IntPtr _oldWndProc;
        private static bool _isApplied;

        public static void PreventMaximizeOnTitleBarDoubleClick(Window window)
        {
            if (_isApplied)
            {
                return;
            }

            _hwnd = WindowNative.GetWindowHandle(window);
            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            _newWndProc = WndProc;
            _oldWndProc = SetWindowLongPtr(_hwnd, GwlpWndproc, _newWndProc);
            _isApplied = _oldWndProc != IntPtr.Zero;
        }

        private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WmNclbuttondblclk)
            {
                return IntPtr.Zero;
            }

            return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
        }

        private delegate IntPtr SubclassDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, SubclassDelegate newProc);

        [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}

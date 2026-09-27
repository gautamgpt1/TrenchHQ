using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TrenchHQ.Infrastructure.Windows
{
    // Installed only for the editor dialog; never captures another application's input.
    internal sealed class PanelShortcutCaptureHook : IDisposable
    {
        private readonly Func<bool> _isCaptureFocused;
        private readonly Action<uint, bool> _onKey;
        private readonly IntPtr _window;
        private readonly HookProc _callback;
        private readonly HashSet<uint> _swallowed = [];
        private IntPtr _hook;

        internal PanelShortcutCaptureHook(IntPtr window, Func<bool> isCaptureFocused, Action<uint, bool> onKey)
        {
            _window = window;
            _isCaptureFocused = isCaptureFocused;
            _onKey = onKey;
            _callback = OnKeyboard;
            _hook = SetWindowsHookEx(13, _callback, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        private IntPtr OnKeyboard(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0)
            {
                var key = (uint)Marshal.ReadInt32(data);
                var down = message.ToInt64() is 0x100 or 0x104;
                var up = message.ToInt64() is 0x101 or 0x105;
                // Tab belongs to the dialog's normal focus navigation, not shortcut capture.
                if (key == 0x09 && GetForegroundWindow() == _window && _isCaptureFocused())
                {
                    return CallNextHookEx(_hook, code, message, data);
                }
                // Release swallowed keys even after focus moves, without swallowing unrelated input.
                if (up && _swallowed.Remove(key))
                {
                    _onKey(key, false);
                    return new IntPtr(1);
                }
                if (down && GetForegroundWindow() == _window && _isCaptureFocused())
                {
                    _swallowed.Add(key);
                    _onKey(key, true);
                    return new IntPtr(1);
                }
                if (up && GetForegroundWindow() == _window && _isCaptureFocused()) _onKey(key, false);
            }
            return CallNextHookEx(_hook, code, message, data);
        }

        public void Dispose()
        {
            if (_hook == IntPtr.Zero) return;
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            _swallowed.Clear();
        }

        private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    }
}

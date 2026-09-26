using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace TrenchHQ.Helpers
{
    internal enum PanelShortcutAssignmentResult
    {
        Available,
        Invalid,
        Unsafe,
        Reserved,
        Duplicate,
        Unavailable
    }

    internal sealed class PanelShortcutController : IDisposable
    {
        private const uint WmHotkey = 0x0312;
        private const uint NoRepeatModifier = 0x4000;
        private const int FirstRegistrationId = 0x5000;
        private const int ProbeRegistrationId = 0x5FFF;

        private readonly IntPtr _hwnd;
        private readonly DesktopPanelManager _manager;
        private readonly WindowsAppBarNative.WindowProc _windowProc;
        private readonly Dictionary<int, (string Id, bool IsDocked)> _targets = [];
        private IntPtr _oldWindowProc;
        private bool _captureSuspended;
        private bool _isDisposed;

        internal PanelShortcutController(IntPtr hwnd, DesktopPanelManager manager)
        {
            _hwnd = hwnd;
            _manager = manager;
            _windowProc = WindowProc;
            _oldWindowProc = WindowsAppBarNative.SetWindowLongPtr(
                _hwnd,
                WindowsAppBarNative.GwlpWndProc,
                _windowProc);
            _manager.DefinitionsChanged += OnDefinitionsChanged;
            RefreshRegistrations();
        }

        internal PanelShortcutAssignmentResult CanAssign(string shortcut, string panelId, bool isDocked)
        {
            if (string.IsNullOrWhiteSpace(shortcut))
            {
                return PanelShortcutAssignmentResult.Available;
            }

            if (!PanelShortcutRules.TryParse(shortcut, out var gesture, out var failure))
            {
                return failure switch
                {
                    PanelShortcutValidationFailure.UnsafeModifiers => PanelShortcutAssignmentResult.Unsafe,
                    PanelShortcutValidationFailure.WindowsReserved or PanelShortcutValidationFailure.DebuggerReserved
                        => PanelShortcutAssignmentResult.Reserved,
                    _ => PanelShortcutAssignmentResult.Invalid
                };
            }

            if (!_manager.IsShortcutAvailable(gesture.DisplayText, panelId, isDocked))
            {
                return PanelShortcutAssignmentResult.Duplicate;
            }

            var current = _manager.GetShortcut(panelId, isDocked);
            if (string.Equals(current, gesture.DisplayText, StringComparison.OrdinalIgnoreCase)
                && _targets.Values.Any(target => target.Id == panelId && target.IsDocked == isDocked))
            {
                return PanelShortcutAssignmentResult.Available;
            }

            var registered = RegisterHotKey(
                _hwnd,
                ProbeRegistrationId,
                gesture.Modifiers | NoRepeatModifier,
                gesture.VirtualKey);
            if (registered)
            {
                UnregisterHotKey(_hwnd, ProbeRegistrationId);
            }

            return registered
                ? PanelShortcutAssignmentResult.Available
                : PanelShortcutAssignmentResult.Unavailable;
        }

        internal void BeginCapture()
        {
            if (_isDisposed || _captureSuspended)
            {
                return;
            }

            _captureSuspended = true;
            ClearRegistrations();
        }

        internal void EndCapture()
        {
            if (_isDisposed || !_captureSuspended)
            {
                return;
            }

            _captureSuspended = false;
            RefreshRegistrations();
        }

        internal bool IsRegistered(string panelId, bool isDocked, string? shortcut)
        {
            if (string.IsNullOrWhiteSpace(shortcut))
            {
                return true;
            }
            var normalized = PanelShortcutRules.Normalize(shortcut);
            return !_captureSuspended
                   && normalized.Length > 0
                   && string.Equals(
                       _manager.GetShortcut(panelId, isDocked),
                       normalized,
                       StringComparison.OrdinalIgnoreCase)
                   && _targets.Values.Any(target =>
                       target.IsDocked == isDocked
                       && string.Equals(target.Id, panelId, StringComparison.OrdinalIgnoreCase));
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _manager.DefinitionsChanged -= OnDefinitionsChanged;
            ClearRegistrations();
            if (_oldWindowProc != IntPtr.Zero)
            {
                WindowsAppBarNative.SetWindowLongPtr(
                    _hwnd,
                    WindowsAppBarNative.GwlpWndProc,
                    _oldWindowProc);
                _oldWindowProc = IntPtr.Zero;
            }
        }

        private void OnDefinitionsChanged(object? sender, EventArgs e)
        {
            RefreshRegistrations();
        }

        private void RefreshRegistrations()
        {
            ClearRegistrations();
            if (_captureSuspended)
            {
                return;
            }
            var registrations = _manager.GetShortcutAssignments();
            var registrationId = FirstRegistrationId;
            foreach (var assignment in registrations)
            {
                if (!PanelShortcutRules.TryParse(assignment.Shortcut, out var gesture))
                {
                    continue;
                }

                if (RegisterHotKey(
                        _hwnd,
                        registrationId,
                        gesture.Modifiers | NoRepeatModifier,
                        gesture.VirtualKey))
                {
                    _targets[registrationId] = (assignment.Id, assignment.IsDocked);
                    registrationId++;
                }
            }
        }

        private void ClearRegistrations()
        {
            foreach (var registrationId in _targets.Keys)
            {
                UnregisterHotKey(_hwnd, registrationId);
            }

            _targets.Clear();
        }

        private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            var oldWindowProc = _oldWindowProc;
            if (message == WmHotkey
                && _targets.TryGetValue(unchecked((int)wParam.ToInt64()), out var target))
            {
                _ = _manager.TogglePanelAsync(target.Id, target.IsDocked);
                return IntPtr.Zero;
            }

            var result = oldWindowProc != IntPtr.Zero
                ? WindowsAppBarNative.CallWindowProc(oldWindowProc, hwnd, message, wParam, lParam)
                : IntPtr.Zero;
            if (message == WindowsAppBarNative.WmDisplayChange
                || message == WindowsAppBarNative.WmDpiChanged
                || message == WindowsAppBarNative.WmSettingChange
                || message == WindowsAppBarNative.WmDeviceChange)
            {
                _manager.NotifyDisplayEnvironmentChanged();
            }

            return result;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}

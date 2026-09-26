using System;
using System.Collections.Generic;
using System.Linq;

namespace TrenchHQ.Helpers
{
    internal sealed class PanelShortcutCapture
    {
        private readonly HashSet<uint> _keys = [];
        internal string Preview { get; private set; } = string.Empty;
        internal string? Shortcut { get; private set; }
        internal bool IsShiftDown => _keys.Any(k => k is 0x10 or 0xA0 or 0xA1);
        internal void ResetHeldKeys() => _keys.Clear();

        internal void Set(string shortcut)
        {
            _keys.Clear();
            Preview = shortcut;
            Shortcut = shortcut;
        }

        internal void Release(uint key) => _keys.Remove(key);

        internal void Press(uint key)
        {
            _keys.Add(key);
            uint modifiers = 0;
            if (_keys.Any(k => k is 0x11 or 0xA2 or 0xA3)) modifiers |= PanelShortcutRules.ControlModifier;
            if (_keys.Any(k => k is 0x12 or 0xA4 or 0xA5)) modifiers |= PanelShortcutRules.AltModifier;
            if (_keys.Any(k => k is 0x10 or 0xA0 or 0xA1)) modifiers |= PanelShortcutRules.ShiftModifier;
            if (_keys.Any(k => k is 0x5B or 0x5C)) modifiers |= PanelShortcutRules.WindowsModifier;
            var names = new List<string>();
            if ((modifiers & PanelShortcutRules.ControlModifier) != 0) names.Add("Ctrl");
            if ((modifiers & PanelShortcutRules.AltModifier) != 0) names.Add("Alt");
            if ((modifiers & PanelShortcutRules.ShiftModifier) != 0) names.Add("Shift");
            if ((modifiers & PanelShortcutRules.WindowsModifier) != 0) names.Add("Win");
            var mainKeys = _keys.Where(k => !IsModifier(k)).ToArray();
            names.AddRange(mainKeys.Select(KeyName));
            Preview = string.Join('+', names);
            Shortcut = mainKeys.Length == 1 && PanelShortcutRules.TryCreate(modifiers, mainKeys[0], out var gesture, out _)
                ? gesture.DisplayText : null;
        }

        internal static bool IsModifier(uint key) => key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5;

        private static string KeyName(uint key) => key switch
        {
            >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39 => ((char)key).ToString(),
            >= 0x70 and <= 0x87 => $"F{key - 0x70 + 1}",
            0x08 => "Backspace", 0x0D => "Enter", 0x20 => "Space", 0x2E => "Delete",
            _ => $"Key {key}"
        };
    }
}

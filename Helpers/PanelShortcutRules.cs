using System;
using System.Collections.Generic;

namespace TrenchHQ.Helpers
{
    internal readonly record struct PanelShortcutGesture(string DisplayText, uint Modifiers, uint VirtualKey);

    internal enum PanelShortcutValidationFailure
    {
        None,
        InvalidFormat,
        UnsafeModifiers,
        WindowsReserved,
        DebuggerReserved
    }

    internal static class PanelShortcutRules
    {
        internal const uint AltModifier = 0x0001;
        internal const uint ControlModifier = 0x0002;
        internal const uint ShiftModifier = 0x0004;
        internal const uint WindowsModifier = 0x0008;
        private const uint AllModifiers = AltModifier | ControlModifier | ShiftModifier | WindowsModifier;

        internal static bool TryParse(string? value, out PanelShortcutGesture gesture)
        {
            return TryParse(value, out gesture, out _);
        }

        internal static bool TryParse(
            string? value,
            out PanelShortcutGesture gesture,
            out PanelShortcutValidationFailure failure)
        {
            gesture = default;
            failure = PanelShortcutValidationFailure.InvalidFormat;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var parts = value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                return false;
            }

            uint modifiers = 0;
            for (var index = 0; index < parts.Length - 1; index++)
            {
                var modifier = parts[index].ToUpperInvariant() switch
                {
                    "CTRL" or "CONTROL" => ControlModifier,
                    "ALT" => AltModifier,
                    "SHIFT" => ShiftModifier,
                    "WIN" or "WINDOWS" => WindowsModifier,
                    _ => 0u
                };
                if (modifier == 0 || (modifiers & modifier) != 0)
                {
                    return false;
                }

                modifiers |= modifier;
            }

            if (!TryParseKey(parts[^1], out var virtualKey, out var keyText))
            {
                return false;
            }

            return TryCreate(modifiers, virtualKey, keyText, out gesture, out failure);
        }

        internal static bool TryCreate(
            uint modifiers,
            uint virtualKey,
            out PanelShortcutGesture gesture,
            out PanelShortcutValidationFailure failure)
        {
            gesture = default;
            failure = PanelShortcutValidationFailure.InvalidFormat;
            if (!TryGetKeyText(virtualKey, out var keyText))
            {
                return false;
            }

            return TryCreate(modifiers, virtualKey, keyText, out gesture, out failure);
        }

        private static bool TryCreate(
            uint modifiers,
            uint virtualKey,
            string keyText,
            out PanelShortcutGesture gesture,
            out PanelShortcutValidationFailure failure)
        {
            gesture = default;
            if ((modifiers & ~AllModifiers) != 0)
            {
                failure = PanelShortcutValidationFailure.InvalidFormat;
                return false;
            }
            if ((modifiers & WindowsModifier) != 0)
            {
                failure = PanelShortcutValidationFailure.WindowsReserved;
                return false;
            }
            if (virtualKey == 0x7B)
            {
                failure = PanelShortcutValidationFailure.DebuggerReserved;
                return false;
            }
            if ((modifiers & ControlModifier) == 0
                || (modifiers & (AltModifier | ShiftModifier)) == 0)
            {
                failure = PanelShortcutValidationFailure.UnsafeModifiers;
                return false;
            }

            var names = new List<string>(5);
            if ((modifiers & ControlModifier) != 0) names.Add("Ctrl");
            if ((modifiers & AltModifier) != 0) names.Add("Alt");
            if ((modifiers & ShiftModifier) != 0) names.Add("Shift");
            if ((modifiers & WindowsModifier) != 0) names.Add("Win");
            names.Add(keyText);
            gesture = new PanelShortcutGesture(string.Join('+', names), modifiers, virtualKey);
            failure = PanelShortcutValidationFailure.None;
            return true;
        }

        internal static string Normalize(string? value)
        {
            return TryParse(value, out var gesture) ? gesture.DisplayText : string.Empty;
        }

        private static bool TryParseKey(string value, out uint virtualKey, out string keyText)
        {
            var normalized = value.Trim().ToUpperInvariant();
            if (normalized.Length == 1 && ((normalized[0] >= 'A' && normalized[0] <= 'Z')
                                           || (normalized[0] >= '0' && normalized[0] <= '9')))
            {
                virtualKey = normalized[0];
                keyText = normalized;
                return true;
            }

            if (normalized.Length is 2 or 3
                && normalized[0] == 'F'
                && int.TryParse(normalized.AsSpan(1), out var functionKey)
                && functionKey is >= 1 and <= 12)
            {
                virtualKey = (uint)(0x70 + functionKey - 1);
                keyText = $"F{functionKey}";
                return true;
            }

            virtualKey = 0;
            keyText = string.Empty;
            return false;
        }

        private static bool TryGetKeyText(uint virtualKey, out string keyText)
        {
            if (virtualKey is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                keyText = ((char)virtualKey).ToString();
                return true;
            }
            if (virtualKey is >= 0x70 and <= 0x7B)
            {
                keyText = $"F{virtualKey - 0x70 + 1}";
                return true;
            }

            keyText = string.Empty;
            return false;
        }
    }
}

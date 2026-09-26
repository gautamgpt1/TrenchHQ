using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TrenchHQ.Models;

namespace TrenchHQ.Helpers
{
    internal static class DesktopSetupRules
    {
        internal const int CurrentVersion = 7;

        internal static DesktopSetup Normalize(DesktopSetup settings)
        {
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var usedShortcuts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            settings.Version = CurrentVersion;
            settings.Overlays ??= [];
            settings.Overlays = settings.Overlays
                .Where(static definition => definition != null)
                .Select((definition, index) => NormalizeOverlay(definition, usedIds, usedShortcuts, index))
                .ToArray();
            settings.DockedBars ??= [];
            settings.DockedBars = settings.DockedBars
                .Where(static definition => definition != null)
                .Select(definition => NormalizeDockedBar(definition, usedIds, usedShortcuts))
                .ToArray();

            return settings;
        }

        internal static string NormalizeColor(string? value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            var trimmed = value.Trim();
            return trimmed.Length == 7
                   && trimmed[0] == '#'
                   && uint.TryParse(trimmed.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)
                ? trimmed.ToUpperInvariant()
                : fallback;
        }

        internal static string GetOverlayName(int index) => $"Overlay {index + 1}";

        internal static string GetDockedBarName(string edge, int? monitorNumber = null) =>
            $"Panel {DockedBarLayoutRules.NormalizeEdge(edge)}" + (monitorNumber.HasValue ? $" {monitorNumber}" : string.Empty);

        internal static bool HasDisplayContent(
            bool useApplicationWindow,
            IEnumerable<string>? widgetIds,
            IEnumerable<SavedWidgetDefinition> widgets)
        {
            if (useApplicationWindow)
            {
                return true;
            }

            var availableIds = widgets
                .Select(static widget => widget.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return (widgetIds ?? []).Any(availableIds.Contains);
        }

        internal static bool ReconcileWidgetReferences(
            DesktopSetup settings,
            SavedWidgetDefinition[] widgets)
        {
            var availableIds = widgets.Select(static widget => widget.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var changed = false;
            foreach (var overlay in settings.Overlays)
            {
                if (overlay.UseApplicationWindow)
                {
                    changed |= overlay.WidgetIds.Length > 0;
                    overlay.WidgetIds = [];
                    continue;
                }
                changed |= ReconcileWidgetReferences(
                    overlay.WidgetIds,
                    availableIds,
                    out var repaired);
                overlay.WidgetIds = repaired;
                if (repaired.Length == 0 && overlay.Enabled)
                {
                    overlay.Enabled = false;
                    changed = true;
                }
            }

            foreach (var dockedBar in settings.DockedBars)
            {
                if (dockedBar.UseApplicationWindow)
                {
                    changed |= dockedBar.WidgetIds.Length > 0;
                    dockedBar.WidgetIds = [];
                    continue;
                }
                changed |= ReconcileWidgetReferences(
                    dockedBar.WidgetIds,
                    availableIds,
                    out var repaired);
                dockedBar.WidgetIds = repaired;
                if (repaired.Length == 0 && dockedBar.Enabled)
                {
                    dockedBar.Enabled = false;
                    changed = true;
                }
            }

            return changed;
        }

        private static OverlayDefinition NormalizeOverlay(
            OverlayDefinition definition,
            HashSet<string> usedIds,
            HashSet<string> usedShortcuts,
            int index)
        {
            definition.Id = NormalizeId(definition.Id, usedIds);
            definition.Name = GetOverlayName(index);
            definition.WidgetIds = definition.UseApplicationWindow ? [] : NormalizeWidgetIds(definition.WidgetIds);
            definition.HeightDip = OverlayLayoutRules.ClampHeight(definition.HeightDip);
            definition.WidthDip = OverlayLayoutRules.ClampWidth(definition.WidthDip);
            definition.BackgroundColor = NormalizeColor(definition.BackgroundColor, "#000000");
            definition.TextColor = NormalizeColor(definition.TextColor, "#F5F5F5");
            definition.ContentSize = PanelContentSizeRules.Normalize(definition.ContentSize);
            definition.LuminosityOpacity = NormalizeOpacity(definition.LuminosityOpacity, 85);
            definition.TintOpacity = NormalizeOpacity(definition.TintOpacity, 40);
            definition.Shortcut = NormalizeShortcut(definition.Shortcut, usedShortcuts);
            definition.MonitorDeviceName = definition.MonitorDeviceName?.Trim() ?? string.Empty;
            return definition;
        }

        private static DockedBarDefinition NormalizeDockedBar(
            DockedBarDefinition definition,
            HashSet<string> usedIds,
            HashSet<string> usedShortcuts)
        {
            definition.Id = NormalizeId(definition.Id, usedIds);
            definition.Name = GetDockedBarName(definition.Edge);
            definition.WidgetIds = definition.UseApplicationWindow ? [] : NormalizeWidgetIds(definition.WidgetIds);
            definition.MonitorDeviceName = definition.MonitorDeviceName?.Trim() ?? string.Empty;
            definition.Edge = DockedBarLayoutRules.NormalizeEdge(definition.Edge);
            definition.ThicknessPx = DockedBarLayoutRules.ClampThickness(definition.Edge, definition.ThicknessPx);
            definition.BackgroundColor = NormalizeColor(definition.BackgroundColor, "#000000");
            definition.TextColor = NormalizeColor(definition.TextColor, "#F5F5F5");
            definition.ContentSize = PanelContentSizeRules.Normalize(definition.ContentSize);
            definition.Shortcut = NormalizeShortcut(definition.Shortcut, usedShortcuts);
            return definition;
        }

        private static string NormalizeId(string? value, HashSet<string> usedIds)
        {
            if (Guid.TryParseExact(value?.Trim(), "N", out var parsed))
            {
                var normalized = parsed.ToString("N");
                if (usedIds.Add(normalized))
                {
                    return normalized;
                }
            }

            string generated;
            do
            {
                generated = Guid.NewGuid().ToString("N");
            }
            while (!usedIds.Add(generated));

            return generated;
        }

        private static string[] NormalizeWidgetIds(string[]? widgetIds)
        {
            return (widgetIds ?? [])
                .Where(static id => Guid.TryParseExact(id?.Trim(), "N", out _))
                .Select(static id => Guid.ParseExact(id.Trim(), "N").ToString("N"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static bool ReconcileWidgetReferences(
            string[]? widgetIds,
            HashSet<string> availableIds,
            out string[] repaired)
        {
            repaired = (widgetIds ?? [])
                .Where(availableIds.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return !(widgetIds ?? []).SequenceEqual(repaired, StringComparer.OrdinalIgnoreCase);
        }

        private static double NormalizeOpacity(double value, double fallback)
        {
            return double.IsFinite(value) ? Math.Clamp(value, 0, 100) : fallback;
        }

        private static string NormalizeShortcut(string? value, HashSet<string> usedShortcuts)
        {
            var normalized = PanelShortcutRules.Normalize(value);
            return normalized.Length > 0 && usedShortcuts.Add(normalized)
                ? normalized
                : string.Empty;
        }
    }
}

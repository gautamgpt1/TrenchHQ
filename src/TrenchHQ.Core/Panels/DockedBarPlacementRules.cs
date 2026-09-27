using System;
using System.Collections.Generic;
using System.Linq;

namespace TrenchHQ.Core.Panels
{
    internal readonly record struct DockedBarMonitorCandidate(string DeviceName, bool IsPrimary);

    internal readonly record struct DockedBarOccupiedPlacement(string DefinitionId, string MonitorDeviceName, string Edge);

    internal static class DockedBarPlacementRules
    {
        internal static int GetMonitorNumber(string deviceName, int fallback)
        {
            var marker = deviceName.LastIndexOf("DISPLAY", StringComparison.OrdinalIgnoreCase);
            return marker >= 0 && int.TryParse(deviceName.AsSpan(marker + 7), out var number) && number > 0
                ? number : fallback;
        }

        internal static bool IsOccupied(
            IEnumerable<DockedBarOccupiedPlacement> placements, string excludedId, string monitor, string edge) =>
            placements.Any(item => !string.Equals(item.DefinitionId, excludedId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.MonitorDeviceName, monitor, StringComparison.OrdinalIgnoreCase)
                && string.Equals(DockedBarLayoutRules.NormalizeEdge(item.Edge),
                    DockedBarLayoutRules.NormalizeEdge(edge), StringComparison.OrdinalIgnoreCase));

        internal static (string MonitorDeviceName, string Edge)? FindAvailable(
            IEnumerable<DockedBarMonitorCandidate> monitors,
            IEnumerable<DockedBarOccupiedPlacement> occupiedPlacements,
            string? excludedDefinitionId = null,
            string? preferredMonitor = null,
            string? preferredEdge = null)
        {
            var occupied = occupiedPlacements
                .Where(item => !string.Equals(item.DefinitionId, excludedDefinitionId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var orderedMonitors = monitors
                .Where(static item => !string.IsNullOrWhiteSpace(item.DeviceName))
                .OrderByDescending(item => string.Equals(item.DeviceName, preferredMonitor, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(static item => item.IsPrimary);
            var normalizedPreferredEdge = DockedBarLayoutRules.NormalizeEdge(preferredEdge ?? string.Empty);
            var orderedEdges = new[] { normalizedPreferredEdge }
                .Concat(DockedBarLayoutRules.Edges)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var monitor in orderedMonitors)
            {
                foreach (var edge in orderedEdges)
                {
                    if (!occupied.Any(item =>
                            string.Equals(item.MonitorDeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(
                                DockedBarLayoutRules.NormalizeEdge(item.Edge),
                                edge,
                                StringComparison.OrdinalIgnoreCase)))
                    {
                        return (monitor.DeviceName, edge);
                    }
                }
            }

            return null;
        }
    }
}

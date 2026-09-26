using TrenchHQ.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TrenchHQ.Helpers
{
    internal static class SolanaProgramDataLogParser
    {
        private const string ProgramPrefix = "Program ";
        private const string InvokeMarker = " invoke [";
        private const string DataPrefix = "Program data: ";

        internal static OnChainRawProgramData[] Parse(IEnumerable<string> logMessages)
        {
            var activePrograms = new SortedDictionary<int, string>();
            var programData = new List<OnChainRawProgramData>();
            var logIndex = 0U;

            foreach (var message in logMessages)
            {
                if (TryReadInvocation(message, out var programId, out var depth))
                {
                    foreach (var staleDepth in activePrograms.Keys.Where(key => key >= depth).ToArray())
                    {
                        activePrograms.Remove(staleDepth);
                    }
                    activePrograms[depth] = programId;
                }
                else if (message.StartsWith(DataPrefix, StringComparison.Ordinal)
                         && activePrograms.Count > 0)
                {
                    programData.Add(new OnChainRawProgramData
                    {
                        ProgramId = activePrograms.Last().Value,
                        DataBase64 = message[DataPrefix.Length..],
                        LogIndex = logIndex
                    });
                }
                else if (TryReadCompletion(message, out programId))
                {
                    var completedDepth = activePrograms
                        .Where(entry => string.Equals(entry.Value, programId, StringComparison.Ordinal))
                        .Select(static entry => (int?)entry.Key)
                        .LastOrDefault();
                    if (completedDepth.HasValue)
                    {
                        foreach (var staleDepth in activePrograms.Keys
                                     .Where(key => key >= completedDepth.Value)
                                     .ToArray())
                        {
                            activePrograms.Remove(staleDepth);
                        }
                    }
                }

                logIndex++;
            }

            return programData.ToArray();
        }

        private static bool TryReadInvocation(string message, out string programId, out int depth)
        {
            programId = string.Empty;
            depth = 0;
            if (!message.StartsWith(ProgramPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            var markerIndex = message.IndexOf(InvokeMarker, ProgramPrefix.Length, StringComparison.Ordinal);
            if (markerIndex < 0 || !message.EndsWith(']'))
            {
                return false;
            }

            programId = message[ProgramPrefix.Length..markerIndex];
            return int.TryParse(
                message.AsSpan(markerIndex + InvokeMarker.Length, message.Length - markerIndex - InvokeMarker.Length - 1),
                out depth)
                   && depth > 0;
        }

        private static bool TryReadCompletion(string message, out string programId)
        {
            programId = string.Empty;
            if (!message.StartsWith(ProgramPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            var remainder = message.AsSpan(ProgramPrefix.Length);
            var separator = remainder.IndexOf(' ');
            if (separator <= 0)
            {
                return false;
            }

            var status = remainder[(separator + 1)..];
            if (!status.Equals("success", StringComparison.Ordinal)
                && !status.StartsWith("failed:", StringComparison.Ordinal))
            {
                return false;
            }

            programId = remainder[..separator].ToString();
            return true;
        }
    }
}

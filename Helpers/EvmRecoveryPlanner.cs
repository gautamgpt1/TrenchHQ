using System;
using System.Collections.Generic;

namespace TrenchHQ.Helpers
{
    internal sealed record EvmBlockRange(ulong From, ulong To);

    internal static class EvmRecoveryPlanner
    {
        internal const ulong DefaultOverlapBlocks = 6;
        internal const ulong DefaultPageSize = 100;
        internal const ulong MaximumRecoveryBlocks = 4096;

        internal static ulong GetRecoveryStart(ulong? checkpoint, ulong latest)
        {
            if (!checkpoint.HasValue)
            {
                return latest;
            }
            return checkpoint.Value > DefaultOverlapBlocks
                ? checkpoint.Value - DefaultOverlapBlocks
                : 0;
        }

        internal static ulong ApplyReplayDepthLimit(
            ulong requestedStart,
            ulong latest,
            ulong maximumBlocks)
        {
            if (maximumBlocks == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumBlocks));
            }
            return latest >= requestedStart && latest - requestedStart >= maximumBlocks
                ? latest
                : requestedStart;
        }

        internal static EvmBlockRange[] BuildRanges(
            ulong from,
            ulong to,
            ulong pageSize = DefaultPageSize)
        {
            if (from > to)
            {
                return [];
            }
            if (pageSize == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pageSize));
            }
            if (to - from >= MaximumRecoveryBlocks)
            {
                throw new InvalidOperationException("The EVM recovery gap exceeds the bounded replay window.");
            }

            var ranges = new List<EvmBlockRange>();
            var current = from;
            while (current <= to)
            {
                var end = Math.Min(to, current + pageSize - 1);
                ranges.Add(new EvmBlockRange(current, end));
                if (end == ulong.MaxValue)
                {
                    break;
                }
                current = end + 1;
            }
            return [.. ranges];
        }
    }
}

using System;
using System.Threading;

namespace TrenchHQ.Helpers
{
    internal sealed record OnChainPipelineDiagnosticsSnapshot(
        long SourceUpdates,
        long EnginePriceUpdates,
        long UiRenders,
        long UiUpdatesCoalesced,
        long Reconnects,
        long Replays,
        long SnapshotRecoveries,
        double EngineLatencyP50Ms,
        double EngineLatencyP95Ms,
        double RenderLatencyP50Ms,
        double RenderLatencyP95Ms);

    internal static class OnChainPipelineDiagnostics
    {
        private static readonly long[] LatencyBoundsMs = [1, 2, 5, 10, 20, 50, 100, 250, 500, 1000];
        private static long _sourceUpdates;
        private static long _enginePriceUpdates;
        private static long _uiRenders;
        private static long _uiUpdatesCoalesced;
        private static long _reconnects;
        private static long _replays;
        private static long _snapshotRecoveries;
        private static long[] _engineLatencyBuckets = new long[LatencyBoundsMs.Length + 1];
        private static long[] _renderLatencyBuckets = new long[LatencyBoundsMs.Length + 1];

        internal static void RecordSourceUpdate() => Interlocked.Increment(ref _sourceUpdates);

        internal static void RecordEnginePrice(long observedAtUnixMs)
        {
            Interlocked.Increment(ref _enginePriceUpdates);
            RecordLatency(_engineLatencyBuckets, observedAtUnixMs);
        }

        internal static void RecordUiRender(long observedAtUnixMs)
        {
            Interlocked.Increment(ref _uiRenders);
            RecordLatency(_renderLatencyBuckets, observedAtUnixMs);
        }

        internal static void RecordUiCoalesced() => Interlocked.Increment(ref _uiUpdatesCoalesced);

        internal static void RecordReconnect() => Interlocked.Increment(ref _reconnects);

        internal static void RecordReplay() => Interlocked.Increment(ref _replays);

        internal static void RecordSnapshotRecovery() => Interlocked.Increment(ref _snapshotRecoveries);

        internal static OnChainPipelineDiagnosticsSnapshot Snapshot()
        {
            var engineBuckets = SnapshotBuckets(_engineLatencyBuckets);
            var renderBuckets = SnapshotBuckets(_renderLatencyBuckets);
            return new OnChainPipelineDiagnosticsSnapshot(
                Volatile.Read(ref _sourceUpdates),
                Volatile.Read(ref _enginePriceUpdates),
                Volatile.Read(ref _uiRenders),
                Volatile.Read(ref _uiUpdatesCoalesced),
                Volatile.Read(ref _reconnects),
                Volatile.Read(ref _replays),
                Volatile.Read(ref _snapshotRecoveries),
                Percentile(engineBuckets, 0.50),
                Percentile(engineBuckets, 0.95),
                Percentile(renderBuckets, 0.50),
                Percentile(renderBuckets, 0.95));
        }

        internal static void Reset()
        {
            Interlocked.Exchange(ref _sourceUpdates, 0);
            Interlocked.Exchange(ref _enginePriceUpdates, 0);
            Interlocked.Exchange(ref _uiRenders, 0);
            Interlocked.Exchange(ref _uiUpdatesCoalesced, 0);
            Interlocked.Exchange(ref _reconnects, 0);
            Interlocked.Exchange(ref _replays, 0);
            Interlocked.Exchange(ref _snapshotRecoveries, 0);
            Interlocked.Exchange(ref _engineLatencyBuckets, new long[LatencyBoundsMs.Length + 1]);
            Interlocked.Exchange(ref _renderLatencyBuckets, new long[LatencyBoundsMs.Length + 1]);
        }

        private static void RecordLatency(long[] buckets, long observedAtUnixMs)
        {
            var elapsed = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - observedAtUnixMs);
            var bucket = Array.FindIndex(LatencyBoundsMs, bound => elapsed <= bound);
            Interlocked.Increment(ref buckets[bucket < 0 ? buckets.Length - 1 : bucket]);
        }

        private static long[] SnapshotBuckets(long[] source)
        {
            var snapshot = new long[source.Length];
            for (var index = 0; index < source.Length; index++)
            {
                snapshot[index] = Volatile.Read(ref source[index]);
            }
            return snapshot;
        }

        private static double Percentile(long[] buckets, double percentile)
        {
            var total = 0L;
            foreach (var count in buckets)
            {
                total += count;
            }
            if (total == 0)
            {
                return 0;
            }

            var target = (long)Math.Ceiling(total * percentile);
            var cumulative = 0L;
            for (var index = 0; index < buckets.Length; index++)
            {
                cumulative += buckets[index];
                if (cumulative >= target)
                {
                    return index < LatencyBoundsMs.Length ? LatencyBoundsMs[index] : double.PositiveInfinity;
                }
            }
            return double.PositiveInfinity;
        }
    }
}

using TrenchHQ.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TrenchHQ.Helpers
{
    internal enum EvmLogAcceptance
    {
        Accepted,
        Duplicate,
        Removed,
        UnknownRemoval
    }

    internal sealed class EvmLogDeduplicator
    {
        internal const int DefaultCapacity = 8192;

        private readonly int _capacity;
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private readonly Queue<QueuedEntry> _order = new();
        private long _generation;

        internal EvmLogDeduplicator(int capacity = DefaultCapacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }
            _capacity = capacity;
        }

        internal EvmLogAcceptance Accept(EvmLogUpdate update)
        {
            ArgumentNullException.ThrowIfNull(update);
            var key = CreateEventId(update);
            if (update.Removed)
            {
                return _entries.Remove(key)
                    ? EvmLogAcceptance.Removed
                    : EvmLogAcceptance.UnknownRemoval;
            }
            if (_entries.ContainsKey(key))
            {
                return EvmLogAcceptance.Duplicate;
            }

            var generation = ++_generation;
            _entries.Add(key, new Entry(update.BlockNumber, generation));
            _order.Enqueue(new QueuedEntry(key, generation));
            Trim();
            return EvmLogAcceptance.Accepted;
        }

        internal void RollbackAfter(ulong blockNumber)
        {
            foreach (var key in _entries
                         .Where(pair => pair.Value.BlockNumber > blockNumber)
                         .Select(static pair => pair.Key)
                         .ToArray())
            {
                _entries.Remove(key);
            }
        }

        internal static string CreateEventId(EvmLogUpdate update)
        {
            return string.Join(':',
                update.ChainId,
                update.BlockHash.ToLowerInvariant(),
                update.TransactionHash.ToLowerInvariant(),
                update.LogIndex);
        }

        private void Trim()
        {
            while (_entries.Count > _capacity && _order.TryDequeue(out var oldest))
            {
                if (_entries.TryGetValue(oldest.Key, out var entry)
                    && entry.Generation == oldest.Generation)
                {
                    _entries.Remove(oldest.Key);
                }
            }
        }

        private sealed record Entry(ulong BlockNumber, long Generation);
        private sealed record QueuedEntry(string Key, long Generation);
    }
}

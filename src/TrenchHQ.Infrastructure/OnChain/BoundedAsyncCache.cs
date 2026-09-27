using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TrenchHQ.Infrastructure.OnChain
{
    internal sealed class BoundedAsyncCache<TKey, TValue> where TKey : notnull
    {
        private readonly int _maximumEntries;
        private readonly Dictionary<TKey, CacheEntry> _entries;
        private readonly Dictionary<TKey, Task<TValue>> _inFlight;
        private readonly object _sync = new();

        internal BoundedAsyncCache(int maximumEntries, IEqualityComparer<TKey>? comparer = null)
        {
            if (maximumEntries <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumEntries));
            }

            _maximumEntries = maximumEntries;
            _entries = new Dictionary<TKey, CacheEntry>(comparer);
            _inFlight = new Dictionary<TKey, Task<TValue>>(comparer);
        }

        internal int Count
        {
            get
            {
                lock (_sync)
                {
                    RemoveExpired(DateTimeOffset.UtcNow);
                    return _entries.Count;
                }
            }
        }

        internal async Task<TValue> GetOrCreateAsync(
            TKey key,
            TimeSpan lifetime,
            Func<Task<TValue>> factory,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(factory);
            cancellationToken.ThrowIfCancellationRequested();
            if (lifetime <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(lifetime));
            }

            TaskCompletionSource<TValue>? producer = null;
            Task<TValue> task;
            lock (_sync)
            {
                var now = DateTimeOffset.UtcNow;
                if (_entries.TryGetValue(key, out var cached))
                {
                    if (cached.ExpiresAt > now)
                    {
                        return cached.Value;
                    }

                    _entries.Remove(key);
                }

                if (!_inFlight.TryGetValue(key, out task!))
                {
                    producer = new TaskCompletionSource<TValue>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    task = producer.Task;
                    _inFlight[key] = task;
                }
            }

            if (producer != null)
            {
                _ = PopulateAsync(key, lifetime, factory, producer);
            }

            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task PopulateAsync(
            TKey key,
            TimeSpan lifetime,
            Func<Task<TValue>> factory,
            TaskCompletionSource<TValue> completion)
        {
            try
            {
                var value = await factory().ConfigureAwait(false);
                lock (_sync)
                {
                    var now = DateTimeOffset.UtcNow;
                    RemoveExpired(now);
                    _entries[key] = new CacheEntry(now + lifetime, value);
                    while (_entries.Count > _maximumEntries)
                    {
                        var oldest = _entries.MinBy(static pair => pair.Value.ExpiresAt).Key;
                        _entries.Remove(oldest);
                    }
                }

                completion.TrySetResult(value);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                lock (_sync)
                {
                    if (_inFlight.TryGetValue(key, out var task)
                        && ReferenceEquals(task, completion.Task))
                    {
                        _inFlight.Remove(key);
                    }
                }
            }
        }

        private void RemoveExpired(DateTimeOffset now)
        {
            foreach (var key in _entries
                         .Where(pair => pair.Value.ExpiresAt <= now)
                         .Select(static pair => pair.Key)
                         .ToArray())
            {
                _entries.Remove(key);
            }
        }

        private sealed record CacheEntry(DateTimeOffset ExpiresAt, TValue Value);
    }
}

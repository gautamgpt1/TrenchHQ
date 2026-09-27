using System;
using System.Collections.Generic;
using System.Linq;

namespace TrenchHQ.Core.OnChain.Evm
{
    internal sealed class EvmWalletTraceBatch
    {
        private readonly int _maximumBlockSpan;
        private readonly SortedDictionary<ulong, long> _observedAtByBlock = [];

        internal EvmWalletTraceBatch(int maximumBlockSpan)
        {
            if (maximumBlockSpan <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumBlockSpan));
            }

            _maximumBlockSpan = maximumBlockSpan;
        }

        internal int Count => _observedAtByBlock.Count;
        internal ulong FromBlock => _observedAtByBlock.First().Key;
        internal ulong ToBlock => _observedAtByBlock.Last().Key;
        internal IReadOnlyDictionary<ulong, long> ObservedAtByBlock => _observedAtByBlock;

        internal bool CanAdd(ulong fromBlock, ulong toBlock)
        {
            if (toBlock < fromBlock)
            {
                return false;
            }

            var combinedFrom = Count == 0 ? fromBlock : Math.Min(FromBlock, fromBlock);
            var combinedTo = Count == 0 ? toBlock : Math.Max(ToBlock, toBlock);
            return combinedTo - combinedFrom < (ulong)_maximumBlockSpan;
        }

        internal void Add(ulong blockNumber, long observedAtUnixMs)
        {
            if (!CanAdd(blockNumber, blockNumber))
            {
                throw new InvalidOperationException("The EVM wallet trace batch exceeded its block bound.");
            }

            _observedAtByBlock[blockNumber] = observedAtUnixMs;
        }

        internal void RemoveAfter(ulong blockNumber)
        {
            foreach (var key in _observedAtByBlock.Keys
                         .Where(key => key > blockNumber)
                         .ToArray())
            {
                _observedAtByBlock.Remove(key);
            }
        }

        internal void Clear() => _observedAtByBlock.Clear();
    }
}

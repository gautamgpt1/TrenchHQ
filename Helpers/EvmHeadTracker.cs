using TrenchHQ.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TrenchHQ.Helpers
{
    internal enum EvmHeadTransitionKind
    {
        First,
        Extended,
        Duplicate,
        Gap,
        Reorg,
        SnapshotRequired
    }

    internal sealed record EvmHeadTransition(
        EvmHeadTransitionKind Kind,
        EvmHeadUpdate Head,
        ulong? CommonAncestorNumber = null,
        string? CommonAncestorHash = null,
        ulong? MissingFrom = null,
        ulong? MissingTo = null);

    internal sealed class EvmHeadTracker
    {
        internal const int Capacity = 128;

        private readonly List<EvmHeadUpdate> _heads = [];
        private EvmBlockReference? _safe;
        private EvmBlockReference? _finalized;

        internal EvmHeadUpdate? Tip => _heads.Count == 0 ? null : _heads[^1];
        internal EvmBlockReference? Safe => _safe;
        internal EvmBlockReference? Finalized => _finalized;

        internal bool Contains(ulong number, string hash)
        {
            return _heads.Any(head => head.Number == number
                                      && string.Equals(head.Hash, hash, StringComparison.Ordinal));
        }

        internal EvmHeadTransition Apply(EvmHeadUpdate head)
        {
            ArgumentNullException.ThrowIfNull(head);
            if (!EvmAddress.IsHash(head.Hash) || !EvmAddress.IsHash(head.ParentHash))
            {
                throw new ArgumentException("The EVM head contains an invalid hash.", nameof(head));
            }
            var existing = _heads.FirstOrDefault(candidate => string.Equals(
                candidate.Hash,
                head.Hash,
                StringComparison.Ordinal));
            if (existing != null)
            {
                return new EvmHeadTransition(EvmHeadTransitionKind.Duplicate, head);
            }
            if (_heads.Count == 0)
            {
                Add(head);
                return new EvmHeadTransition(EvmHeadTransitionKind.First, head);
            }

            var tip = _heads[^1];
            if (head.Number > tip.Number + 1)
            {
                return new EvmHeadTransition(
                    EvmHeadTransitionKind.Gap,
                    head,
                    MissingFrom: tip.Number + 1,
                    MissingTo: head.Number - 1);
            }
            if (head.Number == tip.Number + 1
                && string.Equals(head.ParentHash, tip.Hash, StringComparison.Ordinal))
            {
                Add(head);
                return new EvmHeadTransition(EvmHeadTransitionKind.Extended, head);
            }

            var ancestorIndex = _heads.FindLastIndex(candidate => string.Equals(
                candidate.Hash,
                head.ParentHash,
                StringComparison.Ordinal));
            if (ancestorIndex < 0 || _heads[ancestorIndex].Number + 1 != head.Number)
            {
                return new EvmHeadTransition(EvmHeadTransitionKind.SnapshotRequired, head);
            }

            var ancestor = _heads[ancestorIndex];
            _heads.RemoveRange(ancestorIndex + 1, _heads.Count - ancestorIndex - 1);
            Add(head);
            return new EvmHeadTransition(
                EvmHeadTransitionKind.Reorg,
                head,
                CommonAncestorNumber: ancestor.Number,
                CommonAncestorHash: ancestor.Hash);
        }

        internal void Reset(EvmHeadUpdate head)
        {
            _heads.Clear();
            _safe = null;
            _finalized = null;
            Add(head);
        }

        internal void SetSampledTip(EvmHeadUpdate head)
        {
            ArgumentNullException.ThrowIfNull(head);
            if (!EvmAddress.IsHash(head.Hash) || !EvmAddress.IsHash(head.ParentHash))
            {
                throw new ArgumentException("The EVM head contains an invalid hash.", nameof(head));
            }
            _heads.Clear();
            Add(head);
        }

        internal bool ApplyFinality(EvmFinalityUpdate update)
        {
            ArgumentNullException.ThrowIfNull(update);
            if (!IsValid(update.Head)
                || update.Safe != null && !IsValid(update.Safe)
                || update.Finalized != null && !IsValid(update.Finalized)
                || update.Safe != null && update.Safe.Number > update.Head.Number
                || update.Finalized != null
                   && update.Finalized.Number > (update.Safe?.Number ?? update.Head.Number)
                || _safe != null && update.Safe != null && update.Safe.Number < _safe.Number
                || _finalized != null
                   && update.Finalized != null
                   && update.Finalized.Number < _finalized.Number)
            {
                return false;
            }
            _safe = update.Safe;
            _finalized = update.Finalized;
            return true;
        }

        private static bool IsValid(EvmBlockReference block)
        {
            return EvmAddress.IsHash(block.Hash);
        }

        private void Add(EvmHeadUpdate head)
        {
            _heads.Add(head);
            if (_heads.Count > Capacity)
            {
                _heads.RemoveRange(0, _heads.Count - Capacity);
            }
        }
    }
}

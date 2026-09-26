using TrenchHQ.Models;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TrenchHQ.Helpers
{
    internal enum OnChainSlotStatus
    {
        Processed,
        Confirmed,
        Finalized,
        FirstShredReceived,
        Completed,
        CreatedBank,
        Dead
    }

    internal sealed class OnChainStreamSubscription
    {
        internal OnChainWatchedPoolSelection[] Pools { get; init; } = [];
        internal OnChainCommitment Commitment { get; init; }
        internal ulong? FromSlot { get; init; }
        internal ulong ConnectionEpoch { get; init; }
    }

    internal abstract record OnChainSourceUpdate;

    internal sealed record OnChainSourceAccountUpdate(OnChainRawAccountUpdate Update)
        : OnChainSourceUpdate;

    internal sealed record OnChainSourceAccountSnapshot(OnChainRawAccountUpdate[] Updates)
        : OnChainSourceUpdate
    {
        internal TaskCompletionSource Processed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed record OnChainSourceTransactionUpdate(OnChainRawTransactionUpdate Update)
        : OnChainSourceUpdate;

    internal sealed record OnChainSourceSlotUpdate(
        ulong Slot,
        ulong? Parent,
        OnChainSlotStatus Status,
        long ObservedAtUnixMs)
        : OnChainSourceUpdate;

    internal sealed record OnChainSourceEvmHeadUpdate(EvmHeadUpdate Update)
        : OnChainSourceUpdate
    {
        internal TaskCompletionSource? Processed { get; init; }
    }

    internal sealed record OnChainSourceEvmLogUpdate(EvmLogUpdate Update)
        : OnChainSourceUpdate;

    internal interface IOnChainStreamSource
    {
        Task RunAsync(
            OnChainStreamSubscription subscription,
            ChannelWriter<OnChainSourceUpdate> output,
            CancellationToken cancellationToken);
    }
}

using TrenchHQ.Core.OnChain;
using TrenchHQ.Core.OnChain.Evm;
using TrenchHQ.Core.OnChain.Solana;
using TrenchHQ.Core.Providers;
using TrenchHQ.Core.Wallets;
using TrenchHQ.Core.Widgets;
using TrenchHQ.Infrastructure.Diagnostics;
using TrenchHQ.Infrastructure.OnChain;
using TrenchHQ.Infrastructure.OnChain.Evm;
using TrenchHQ.Infrastructure.OnChain.Solana;
using TrenchHQ.Infrastructure.Providers;
using TrenchHQ.Yellowstone;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using static TrenchHQ.TestSupport.OnChainFixtures;

namespace TrenchHQ.TestSupport;

internal sealed class ObservingEvmStreamSource(
    IOnChainStreamSource inner,
    ConcurrentQueue<EvmLogUpdate> logs,
    Action onRun) : IOnChainStreamSource
{
    public Task RunAsync(
        OnChainStreamSubscription subscription,
        ChannelWriter<OnChainSourceUpdate> output,
        CancellationToken cancellationToken)
    {
        onRun();
        return inner.RunAsync(
            subscription,
            new ObservingChannelWriter(output, logs),
            cancellationToken);
    }

    private sealed class ObservingChannelWriter(
        ChannelWriter<OnChainSourceUpdate> innerWriter,
        ConcurrentQueue<EvmLogUpdate> observedLogs) : ChannelWriter<OnChainSourceUpdate>
    {
        public override bool TryComplete(Exception? error = null)
        {
            return innerWriter.TryComplete(error);
        }

        public override bool TryWrite(OnChainSourceUpdate item)
        {
            if (item is OnChainSourceEvmLogUpdate log)
            {
                observedLogs.Enqueue(log.Update);
            }
            return innerWriter.TryWrite(item);
        }

        public override ValueTask<bool> WaitToWriteAsync(
            CancellationToken cancellationToken = default)
        {
            return innerWriter.WaitToWriteAsync(cancellationToken);
        }
    }
}

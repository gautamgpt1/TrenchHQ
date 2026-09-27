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

internal sealed class ScriptedOnChainSource(byte[] mintBytes) : IOnChainStreamSource
{
    private int _runCount;

    internal OnChainStreamSubscription? ObservedSubscription { get; private set; }
    internal int RunCount => Volatile.Read(ref _runCount);

    public async Task RunAsync(
        OnChainStreamSubscription subscription,
        System.Threading.Channels.ChannelWriter<OnChainSourceUpdate> output,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _runCount);
        ObservedSubscription = subscription;
        await output.WriteAsync(
            new OnChainSourceTransactionUpdate(CreateTrade()),
            cancellationToken);
        await output.WriteAsync(
            new OnChainSourceSlotUpdate(
                42,
                41,
                OnChainSlotStatus.Confirmed,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            cancellationToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private OnChainRawTransactionUpdate CreateTrade()
    {
        using var stream = new MemoryStream();
        stream.Write([189, 219, 127, 211, 78, 230, 97, 238]);
        stream.Write(mintBytes);
        stream.Write(BitConverter.GetBytes(2_000_000_000UL));
        stream.Write(BitConverter.GetBytes(1_000_000UL));
        stream.WriteByte(1);
        return new OnChainRawTransactionUpdate
        {
            Signature = "coordinator-signature",
            Slot = 42,
            Commitment = OnChainCommitment.Processed,
            SourceId = "scripted",
            ProgramData =
            [
                new OnChainRawProgramData
                {
                    ProgramId = "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P",
                    DataBase64 = Convert.ToBase64String(stream.ToArray()),
                    LogIndex = 9
                }
            ]
        };
    }
}

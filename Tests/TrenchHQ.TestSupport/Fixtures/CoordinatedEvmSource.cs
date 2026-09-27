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

internal sealed class CoordinatedEvmSource(
    string poolAddress,
    string chainId = EvmChainDefinitions.EthereumMainnetChainId,
    bool emitHead = true) : IOnChainStreamSource
{
    private int _runCount;

    internal int RunCount => Volatile.Read(ref _runCount);
    internal OnChainStreamSubscription? ObservedSubscription { get; private set; }

    public async Task RunAsync(
        OnChainStreamSubscription subscription,
        System.Threading.Channels.ChannelWriter<OnChainSourceUpdate> output,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _runCount);
        ObservedSubscription = subscription;
        var observedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (emitHead)
        {
            await output.WriteAsync(new OnChainSourceEvmHeadUpdate(new EvmHeadUpdate
            {
                ChainId = chainId,
                ConnectionEpoch = subscription.ConnectionEpoch,
                Number = 17,
                Hash = HashValue(17),
                ParentHash = HashValue(16),
                Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ObservedAtUnixMs = observedAt
            }), cancellationToken);
        }
        await output.WriteAsync(new OnChainSourceEvmLogUpdate(new EvmLogUpdate
        {
            ChainId = chainId,
            ConnectionEpoch = subscription.ConnectionEpoch,
            Address = poolAddress,
            Topics =
            [
                EvmEventTopics.UniswapV2SwapTopic,
                HashValue(7000),
                HashValue(7001)
            ],
            Data = Words(
                2_000_000_000_000_000_000UL,
                0,
                0,
                4_000_000UL),
            BlockNumber = 17,
            BlockHash = HashValue(17),
            TransactionHash = HashValue(7002),
            TransactionIndex = 1,
            LogIndex = 2,
            ObservedAtUnixMs = observedAt
        }), cancellationToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static string HashValue(ulong value) => $"0x{value:x64}";

    private static string Words(params ulong[] values)
    {
        return "0x" + string.Concat(values.Select(static value => value.ToString("x64")));
    }
}

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

internal sealed class ScriptedEvmSource(EvmHeadUpdate head, EvmLogUpdate log) : IOnChainStreamSource
{
    public async Task RunAsync(
        OnChainStreamSubscription subscription,
        System.Threading.Channels.ChannelWriter<OnChainSourceUpdate> output,
        CancellationToken cancellationToken)
    {
        await output.WriteAsync(new OnChainSourceEvmHeadUpdate(head), cancellationToken);
        await output.WriteAsync(new OnChainSourceEvmLogUpdate(log), cancellationToken);
    }
}

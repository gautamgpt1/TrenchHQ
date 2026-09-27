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

internal sealed class WalletCompletionEvmRpcFixtureHandler : HttpMessageHandler
{
    internal bool SawFullTransactions { get; private set; }
    internal string TraceFromBlock { get; private set; } = string.Empty;
    internal string TraceToBlock { get; private set; } = string.Empty;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(
            await request.Content!.ReadAsByteArrayAsync(cancellationToken));
        var root = document.RootElement;
        var id = root.GetProperty("id").GetInt64();
        var method = root.GetProperty("method").GetString();
        object result;
        if (method == "eth_getBlockByNumber")
        {
            SawFullTransactions = root.GetProperty("params")[1].GetBoolean();
            result = new
            {
                number = "0xc8",
                hash = "0x" + new string('a', 64),
                parentHash = "0x" + new string('b', 64),
                timestamp = "0x6553f100",
                transactions = new object[]
                {
                    new
                    {
                        hash = "0x" + new string('c', 64),
                        from = "0x1111111111111111111111111111111111111111",
                        to = "0x2222222222222222222222222222222222222222",
                        value = "0x64"
                    },
                    new
                    {
                        hash = "0x" + new string('d', 64),
                        from = "0x3333333333333333333333333333333333333333",
                        to = "0x1111111111111111111111111111111111111111",
                        value = "0x0"
                    }
                }
            };
        }
        else if (method == "trace_filter")
        {
            var filter = root.GetProperty("params")[0];
            TraceFromBlock = filter.GetProperty("fromBlock").GetString() ?? string.Empty;
            TraceToBlock = filter.GetProperty("toBlock").GetString() ?? string.Empty;
            result = new object[]
            {
                new
                {
                    blockNumber = 200,
                    transactionHash = "0x" + new string('e', 64),
                    traceAddress = new[] { 0, 1 },
                    type = "call",
                    action = new
                    {
                        from = "0x1111111111111111111111111111111111111111",
                        to = "0x4444444444444444444444444444444444444444",
                        value = "0x37"
                    }
                }
            };
        }
        else
        {
            throw new InvalidOperationException("Unexpected EVM wallet RPC method: " + method);
        }
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id, result })
        };
    }
}

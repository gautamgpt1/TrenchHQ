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

internal sealed class WalletSignatureRpcFixtureHandler : HttpMessageHandler
{
    internal string RequestBody { get; private set; } = string.Empty;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "jsonrpc": "2.0",
                  "id": 1,
                  "result": [
                    {
                      "signature": "new-signature",
                      "slot": 101,
                      "err": null,
                      "blockTime": 1700000001,
                      "confirmationStatus": "confirmed"
                    },
                    {
                      "signature": "failed-signature",
                      "slot": 100,
                      "err": { "InstructionError": [0, "Custom"] },
                      "blockTime": 1700000000,
                      "confirmationStatus": "confirmed"
                    }
                  ]
                }
                """)
        };
    }
}

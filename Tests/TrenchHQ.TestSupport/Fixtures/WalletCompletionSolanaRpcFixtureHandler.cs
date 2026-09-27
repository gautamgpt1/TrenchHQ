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

internal sealed class WalletCompletionSolanaRpcFixtureHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(
            await request.Content!.ReadAsByteArrayAsync(cancellationToken));
        var root = document.RootElement;
        var id = root.GetProperty("id").GetInt32();
        var method = root.GetProperty("method").GetString();
        object result = method switch
        {
            "getTokenAccountsByOwner" => (object)new
            {
                context = new { slot = 500UL },
                value = new object[]
                {
                    new { pubkey = "TokenAccount1111111111111111111111111111111", account = new { } },
                    new { pubkey = "TokenAccount2222222222222222222222222222222", account = new { } }
                }
            },
            "getSignatureStatuses" => (object)new
            {
                context = new { slot = 505UL },
                value = new object?[]
                {
                    new { slot = 503UL, confirmations = 1, err = (object?)null, confirmationStatus = "confirmed" },
                    new { slot = 500UL, confirmations = (int?)null, err = (object?)null, confirmationStatus = "finalized" }
                }
            },
            _ => throw new InvalidOperationException("Unexpected Solana wallet RPC method: " + method)
        };
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id, result })
        };
    }
}

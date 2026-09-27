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

internal sealed class SolanaSampleRpcFixtureHandler : HttpMessageHandler
{
    internal int RequestCount { get; private set; }
    internal string[] LastAddresses { get; private set; } = [];
    internal string? MissingAddress { get; init; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
        var root = document.RootElement;
        if (root.GetProperty("method").GetString() != "getMultipleAccounts")
        {
            throw new InvalidOperationException("The Solana price sample used an unbatched RPC method.");
        }
        LastAddresses = root.GetProperty("params")[0].EnumerateArray()
            .Select(static address => address.GetString()!).ToArray();
        RequestCount++;
        var response = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id = root.GetProperty("id").GetInt32(),
            result = new
            {
                context = new { slot = 123UL },
                value = LastAddresses.Select(address => address == MissingAddress
                    ? null
                    : (object)new
                    {
                        data = new[] { "AQ==", "base64" },
                        owner = "sample-program",
                        lamports = 1UL,
                        executable = false,
                        rentEpoch = 0UL,
                        space = 1
                    }).ToArray()
            }
        });
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(response)
        };
    }
}

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

internal sealed class InfuraBlockReferenceFallbackFixtureHandler : HttpMessageHandler
{
    internal const string BlockHash =
        "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ParentHash =
        "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    internal bool UsedVerifiedBlockNumberCall { get; private set; }
    internal int BlockByHashRequestCount { get; private set; }
    internal int BlockByNumberRequestCount { get; private set; }
    internal bool ReplaceBlockAfterCall { get; init; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(
            await request.Content!.ReadAsByteArrayAsync(cancellationToken));
        var rpcRequest = document.RootElement;
        var method = rpcRequest.GetProperty("method").GetString();
        object result = method switch
        {
            "eth_getBlockByHash" => GetBlockByHash(rpcRequest.GetProperty("params")),
            "eth_getBlockByNumber" => GetBlockByNumber(),
            "eth_call" => GetCall(rpcRequest.GetProperty("params")),
            _ => throw new InvalidOperationException($"Unexpected Infura fallback method {method}.")
        };
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0",
                id = rpcRequest.GetProperty("id").GetInt64(),
                result
            }))
        };
    }

    private object GetBlockByHash(JsonElement parameters)
    {
        if (parameters[0].GetString() != BlockHash)
        {
            throw new InvalidOperationException("The Infura fallback resolved the wrong block hash.");
        }
        BlockByHashRequestCount++;
        return GetBlock();
    }

    private object GetCall(JsonElement parameters)
    {
        UsedVerifiedBlockNumberCall = parameters[1].ValueKind == JsonValueKind.String
                                      && parameters[1].GetString() == "0x10";
        return "0x0000000000000000000000000000000000000000000000000000000000000012";
    }

    private object GetBlockByNumber()
    {
        BlockByNumberRequestCount++;
        return GetBlock();
    }

    private object GetBlock() => new
    {
        number = "0x10",
        hash = ReplaceBlockAfterCall && UsedVerifiedBlockNumberCall ? ParentHash : BlockHash,
        parentHash = ParentHash,
        timestamp = "0x20"
    };
}

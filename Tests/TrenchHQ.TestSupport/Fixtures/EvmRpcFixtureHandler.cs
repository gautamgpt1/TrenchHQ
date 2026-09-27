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

internal sealed class EvmRpcFixtureHandler(
    bool rateLimitFirstChainId = false,
    bool malformedMulticall = false) : HttpMessageHandler
{
    private const string BlockHash =
        "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ParentHash =
        "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private int _remainingRateLimitedChainIdRequests = rateLimitFirstChainId ? 1 : 0;

    internal int RateLimitedChainIdRequestCount { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(
            await request.Content!.ReadAsByteArrayAsync(cancellationToken));
        if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.GetProperty("method").GetString() == "eth_chainId"
            && Interlocked.Exchange(ref _remainingRateLimitedChainIdRequests, 0) == 1)
        {
            RateLimitedChainIdRequestCount++;
            return new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        }
        object response = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().Select(CreateResponse).ToArray()
            : CreateResponse(document.RootElement);
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(response))
        };
    }

    private object CreateResponse(JsonElement request)
    {
        var method = request.GetProperty("method").GetString();
        object result = method switch
        {
            "eth_chainId" => "0x1",
            "eth_blockNumber" => "0x10",
            "eth_getBlockByNumber" => new
            {
                number = "0x10",
                hash = BlockHash,
                parentHash = ParentHash,
                timestamp = "0x20"
            },
            "eth_call" => GetCall(request),
            "eth_getLogs" => Array.Empty<object>(),
            _ => throw new InvalidOperationException($"Unexpected EVM fixture method {method}.")
        };
        return new
        {
            jsonrpc = "2.0",
            id = request.GetProperty("id").GetInt64(),
            result
        };
    }

    private string GetCall(JsonElement request)
    {
        if (malformedMulticall) { return "0x"; }
        var call = request.GetProperty("params")[0];
        if (!string.Equals(
                call.GetProperty("to").GetString(),
                EvmMulticall3.Address,
                StringComparison.OrdinalIgnoreCase)
            || call.GetProperty("data").GetString() != EvmMulticall3.EncodePair(
                EvmChainDefinitions.EthereumMainnet.WrappedNativeAssetAddress,
                EthereumAbi.DecimalsSelector,
                EthereumAbi.DecimalsSelector))
        {
            throw new InvalidOperationException("The capability probe did not test paired pinned reads.");
        }
        const string decimals = "0x0000000000000000000000000000000000000000000000000000000000000012";
        return EvmCoordinatorRpcFixtureHandler.EncodePairResult(decimals, decimals);
    }
}

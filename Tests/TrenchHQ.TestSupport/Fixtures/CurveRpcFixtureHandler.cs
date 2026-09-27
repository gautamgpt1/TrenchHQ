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

internal sealed class CurveRpcFixtureHandler(
    string selected,
    string quote,
    string poolAddress) : HttpMessageHandler
{
    private const string BlockHash =
        "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ParentHash =
        "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

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
            "eth_chainId" => "0x1",
            "eth_getBlockByNumber" => new
            {
                number = "0x10",
                hash = BlockHash,
                parentHash = ParentHash,
                timestamp = "0x20"
            },
            "eth_getCode" => "0x6000",
            "eth_call" => GetCall(rpcRequest.GetProperty("params")),
            "eth_getLogs" => GetLogs(rpcRequest.GetProperty("params")),
            _ => throw new InvalidOperationException($"Unexpected Curve fixture method {method}.")
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

    private string GetCall(JsonElement parameters)
    {
        var call = parameters[0];
        var to = call.GetProperty("to").GetString()!;
        var data = call.GetProperty("data").GetString()!;
        if (data == EthereumAbi.DecimalsSelector)
        {
            return Word(to.Equals(quote, StringComparison.OrdinalIgnoreCase) ? 6UL : 18UL);
        }
        if (data.StartsWith(EthereumAbi.GetPairSelector, StringComparison.Ordinal)
            || data.StartsWith(EthereumAbi.GetPoolSelector, StringComparison.Ordinal))
        {
            return Address("0x0000000000000000000000000000000000000000");
        }
        if (to.Equals(poolAddress, StringComparison.OrdinalIgnoreCase)
            && data.StartsWith(EthereumAbi.CurveCoinsSelector, StringComparison.Ordinal))
        {
            var index = Convert.ToUInt32(data[^64..], 16);
            return index switch
            {
                0 => Address(selected),
                2 => Address(quote),
                _ => throw new InvalidOperationException("The Curve fixture requested an unexpected coin index.")
            };
        }
        throw new InvalidOperationException($"Unexpected Curve fixture call {to} {data}.");
    }

    private object[] GetLogs(JsonElement parameters)
    {
        var filter = parameters[0];
        var address = filter.GetProperty("address").GetString();
        if (!string.Equals(address, EthereumDeploymentRegistry.FermiCurrentSwapper, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }
        return
        [
            new
            {
                address = EthereumDeploymentRegistry.FermiCurrentSwapper,
                topics = new[]
                {
                    EvmEventTopics.FermiSwappedTopic,
                    Address("0x2222222222222222222222222222222222222222"),
                    Address(quote),
                    Address(selected)
                },
                data = "0x"
                       + Word(4_000_000_000)[2..]
                       + Word(2_000_000_000_000_000_000)[2..]
                       + Address("0x3333333333333333333333333333333333333333")[2..],
                blockNumber = "0x10"
            }
        ];
    }

    private static string Word(ulong value) => $"0x{value:x64}";

    private static string Address(string value) => "0x" + value[2..].PadLeft(64, '0');
}

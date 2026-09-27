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

internal sealed class UniswapDiscoveryFixtureHandler(
    string chainId,
    EvmDeploymentCatalog deployments,
    string selectedToken,
    string pair,
    string v3Pool,
    bool aerodromeRateLimited = false) : HttpMessageHandler
{
    private const string BlockHash =
        "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ParentHash =
        "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    public int CallCount { get; private set; }
    public bool SawUnpinnedStateRead { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(
            await request.Content!.ReadAsByteArrayAsync(cancellationToken));
        var rpcRequest = document.RootElement;
        var method = rpcRequest.GetProperty("method").GetString();
        if (aerodromeRateLimited
            && method == "eth_call"
            && IsAerodromeFactoryCall(rpcRequest))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        }
        object result = method switch
        {
            "eth_chainId" => "0x" + ulong.Parse(chainId).ToString("x"),
            "eth_getBlockByNumber" => new
            {
                number = "0x10",
                hash = BlockHash,
                parentHash = ParentHash,
                timestamp = "0x20"
            },
            "eth_getCode" => GetCode(rpcRequest),
            "eth_call" => GetCallResult(rpcRequest),
            _ => throw new InvalidOperationException($"Unexpected discovery RPC method {method}.")
        };
        var response = new
        {
            jsonrpc = "2.0",
            id = rpcRequest.GetProperty("id").GetInt64(),
            result
        };
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(response))
        };
    }

    private static bool IsAerodromeFactoryCall(JsonElement request)
    {
        var data = request.GetProperty("params")[0].GetProperty("data").GetString();
        return data?.StartsWith(EthereumAbi.AerodromeClassicGetPoolSelector, StringComparison.Ordinal) == true
               || data?.StartsWith(EthereumAbi.AerodromeSlipstreamGetPoolSelector, StringComparison.Ordinal) == true;
    }

    private string GetCode(JsonElement request)
    {
        CallCount++;
        ValidatePinned(request.GetProperty("params")[1]);
        return "0x6000";
    }

    private string GetCallResult(JsonElement request)
    {
        CallCount++;
        var parameters = request.GetProperty("params");
        ValidatePinned(parameters[1]);
        var call = parameters[0];
        var to = call.GetProperty("to").GetString()!.ToLowerInvariant();
        var data = call.GetProperty("data").GetString()!.ToLowerInvariant();
        if (data == EthereumAbi.DecimalsSelector)
        {
            return Word(to == BaseDeploymentRegistry.Usdc ? 6UL : 18UL);
        }
        if (to == pair && data == EthereumAbi.FactorySelector)
        {
            return AddressWord(deployments.UniswapV2Factory);
        }
        if (to == pair && data == EthereumAbi.Token0Selector)
        {
            return AddressWord(selectedToken);
        }
        if (to == pair && data == EthereumAbi.Token1Selector)
        {
            return AddressWord(deployments.QuoteAssets[0].Address);
        }
        if (to == pair && data == EthereumAbi.GetReservesSelector)
        {
            return Words(10, 20, 123);
        }
        if (to == v3Pool && data == EthereumAbi.FactorySelector)
        {
            return AddressWord(deployments.UniswapV3Factory);
        }
        if (to == v3Pool && data == EthereumAbi.Token0Selector)
        {
            return AddressWord(selectedToken);
        }
        if (to == v3Pool && data == EthereumAbi.Token1Selector)
        {
            return AddressWord(deployments.QuoteAssets[0].Address);
        }
        if (to == v3Pool && data == EthereumAbi.FeeSelector)
        {
            return Word(500);
        }
        if (to == v3Pool && data == EthereumAbi.TickSpacingSelector)
        {
            return Word(10);
        }
        if (to == v3Pool && data == EthereumAbi.Slot0Selector)
        {
            return BigWords(System.Numerics.BigInteger.One << 96, 0, 0, 0, 0, 0, 1);
        }
        if (to == v3Pool && data == EthereumAbi.LiquiditySelector)
        {
            return Word(100);
        }
        if (to == deployments.UniswapV2Factory
            && data.StartsWith(EthereumAbi.GetPairSelector, StringComparison.Ordinal))
        {
            var weth = deployments.QuoteAssets[0].Address[2..];
            return data.Contains(weth, StringComparison.Ordinal)
                ? AddressWord(pair)
                : AddressWord("0x0000000000000000000000000000000000000000");
        }
        if (to == deployments.UniswapV3Factory
            && data.StartsWith(EthereumAbi.GetPoolSelector, StringComparison.Ordinal))
        {
            var weth = deployments.QuoteAssets[0].Address[2..];
            var fee = data[^64..];
            return data.Contains(weth, StringComparison.Ordinal)
                   && fee.EndsWith("01f4", StringComparison.Ordinal)
                ? AddressWord(v3Pool)
                : AddressWord("0x0000000000000000000000000000000000000000");
        }
        if (data.StartsWith(EthereumAbi.AerodromeClassicGetPoolSelector, StringComparison.Ordinal)
            || data.StartsWith(EthereumAbi.AerodromeSlipstreamGetPoolSelector, StringComparison.Ordinal))
        {
            return AddressWord("0x0000000000000000000000000000000000000000");
        }
        throw new InvalidOperationException($"Unexpected discovery eth_call to {to} with {data}.");
    }

    private void ValidatePinned(JsonElement blockReference)
    {
        if (blockReference.ValueKind != JsonValueKind.Object
            || blockReference.GetProperty("blockHash").GetString() != BlockHash
            || !blockReference.GetProperty("requireCanonical").GetBoolean())
        {
            SawUnpinnedStateRead = true;
        }
    }

    private static string AddressWord(string address)
    {
        return "0x" + address[2..].PadLeft(64, '0');
    }

    private static string Word(ulong value) => $"0x{value:x64}";

    private static string Words(params ulong[] values)
    {
        return "0x" + string.Concat(values.Select(static value => $"{value:x64}"));
    }

    private static string BigWords(params System.Numerics.BigInteger[] values)
    {
        return "0x" + string.Concat(values.Select(static value =>
            value.ToString("x").PadLeft(64, '0')));
    }
}

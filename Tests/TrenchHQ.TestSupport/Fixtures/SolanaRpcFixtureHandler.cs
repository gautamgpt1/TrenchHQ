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

internal sealed class SolanaRpcFixtureHandler(
    string expectedApiKey,
    string selectedMint,
    string poolAddress,
    string decoyAddress,
    IReadOnlyDictionary<string, RpcFixtureAccount> accounts,
    bool rateLimitProgramAccounts = false) : HttpMessageHandler
{
    internal ConcurrentQueue<string> Methods { get; } = new();
    internal bool SawApiKey { get; private set; }
    internal bool SawCredentialFreeRequest { get; private set; }
    internal bool SawZeroLengthDataSlice { get; private set; }
    internal int ProgramAccountRequestCount { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        SawApiKey |= request.RequestUri?.Query == "?api-key=" + expectedApiKey;
        SawCredentialFreeRequest |= string.IsNullOrEmpty(request.RequestUri?.Query);
        using var document = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
        var root = document.RootElement;
        var method = root.GetProperty("method").GetString();
        Methods.Enqueue(method!);
        var parameters = root.GetProperty("params");
        if (method == "getProgramAccounts")
        {
            ProgramAccountRequestCount++;
            if (rateLimitProgramAccounts)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("{\"error\":{\"code\":429,\"message\":\"rate limited\"}}")
                };
            }
        }
        object result = method switch
        {
            "getAccountInfo" => Context(AccountValue(parameters[0].GetString()!)),
            "getMultipleAccounts" => Context(parameters[0].EnumerateArray()
                .Select(address => AccountValue(address.GetString()!)).ToArray()),
            "getProgramAccounts" => ProgramAccounts(parameters),
            _ => throw new InvalidOperationException($"Unexpected fixture RPC method {method}.")
        };
        var response = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id = root.GetProperty("id").GetInt32(),
            result
        });
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(response)
        };
    }

    private object ProgramAccounts(JsonElement parameters)
    {
        var config = parameters[1];
        SawZeroLengthDataSlice |= config.GetProperty("dataSlice").GetProperty("length").GetInt32() == 0;
        var filters = config.GetProperty("filters");
        var dataSize = filters[0].GetProperty("dataSize").GetInt32();
        var memcmp = filters[1].GetProperty("memcmp");
        var offset = memcmp.GetProperty("offset").GetInt32();
        var bytes = memcmp.GetProperty("bytes").GetString();
        var addresses = dataSize == 301 && offset == 43 && bytes == selectedMint
            ? new[] { poolAddress, decoyAddress }
            : [];
        return Context(addresses.Select(address => new
        {
            pubkey = address,
            account = AccountValue(address)
        }).ToArray());
    }

    private object? AccountValue(string address)
    {
        return accounts.TryGetValue(address, out var account)
            ? new
            {
                data = new[] { Convert.ToBase64String(account.Data), "base64" },
                executable = false,
                lamports = 1UL,
                owner = account.Owner,
                rentEpoch = 0UL,
                space = account.Data.Length
            }
            : null;
    }

    private static object Context(object? value)
    {
        return new
        {
            context = new { apiVersion = "2.3.0", slot = 123UL },
            value
        };
    }
}

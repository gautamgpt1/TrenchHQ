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

internal sealed class WalletTransactionRpcFixtureHandler(string walletAddress) : HttpMessageHandler
{
    internal string RequestBody { get; private set; } = string.Empty;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
        var response = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id = 1,
            result = new
            {
                slot = 102UL,
                transaction = new
                {
                    signatures = new[] { "wallet-fixture-signature" },
                    message = new
                    {
                        accountKeys = new[] { walletAddress, "usdc-account", "token-account" },
                        instructions = Array.Empty<object>()
                    }
                },
                meta = new
                {
                    err = (object?)null,
                    fee = 5000UL,
                    preBalances = new[] { 10_000_000_000UL, 2_039_280UL, 0UL },
                    postBalances = new[] { 9_997_955_720UL, 2_039_280UL, 2_039_280UL },
                    loadedAddresses = new
                    {
                        writable = Array.Empty<string>(),
                        @readonly = Array.Empty<string>()
                    },
                    preTokenBalances = new object[]
                    {
                        new
                        {
                            accountIndex = 1,
                            mint = WalletActivityPresentationRules.SolanaUsdcMint,
                            owner = walletAddress,
                            uiTokenAmount = new { amount = "12500000", decimals = 6 }
                        }
                    },
                    postTokenBalances = new object[]
                    {
                        new
                        {
                            accountIndex = 1,
                            mint = WalletActivityPresentationRules.SolanaUsdcMint,
                            owner = walletAddress,
                            uiTokenAmount = new { amount = "0", decimals = 6 }
                        },
                        new
                        {
                            accountIndex = 2,
                            mint = "TokenMint111111111111111111111111111111111",
                            owner = walletAddress,
                            uiTokenAmount = new { amount = "2000000000", decimals = 9 }
                        }
                    }
                }
            }
        });
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(response)
        };
    }
}

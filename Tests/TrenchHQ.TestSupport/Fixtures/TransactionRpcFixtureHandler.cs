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

internal sealed class TransactionRpcFixtureHandler(byte[] outerData, byte[] innerData) : HttpMessageHandler
{
    internal Uri? RequestUri { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestUri = request.RequestUri;
        var response = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id = 1,
            result = new
            {
                slot = 99UL,
                transaction = new
                {
                    signatures = new[] { "fixture-signature" },
                    message = new
                    {
                        accountKeys = new[] { "base-vault", "outer-program" },
                        instructions = new[]
                        {
                            new
                            {
                                programIdIndex = 1,
                                data = SolanaBase58.Encode(outerData),
                                accounts = new[] { 0, 3 }
                            }
                        }
                    }
                },
                meta = new
                {
                    err = (object?)null,
                    loadedAddresses = new
                    {
                        writable = new[] { "loaded-program" },
                        @readonly = new[] { "quote-vault" }
                    },
                    innerInstructions = new[]
                    {
                        new
                        {
                            index = 0,
                            instructions = new[]
                            {
                                new
                                {
                                    programIdIndex = 2,
                                    data = SolanaBase58.Encode(innerData),
                                    stackHeight = 2U,
                                    accounts = new[] { 0, 3 }
                                }
                            }
                        }
                    },
                    logMessages = new[]
                    {
                        "Program outer-program invoke [1]",
                        "Program data: AQID",
                        "Program loaded-program invoke [2]",
                        "Program data: BAUG",
                        "Program loaded-program success",
                        "Program data: BwgJ",
                        "Program outer-program success"
                    },
                    preTokenBalances = new object[]
                    {
                        new
                        {
                            accountIndex = 0,
                            mint = "base-mint",
                            uiTokenAmount = new { amount = "1000" }
                        },
                        new
                        {
                            accountIndex = 3,
                            mint = "quote-mint",
                            uiTokenAmount = new { amount = "2000" }
                        }
                    },
                    postTokenBalances = new object[]
                    {
                        new
                        {
                            accountIndex = 0,
                            mint = "base-mint",
                            uiTokenAmount = new { amount = "900" }
                        },
                        new
                        {
                            accountIndex = 3,
                            mint = "quote-mint",
                            uiTokenAmount = new { amount = "2200" }
                        }
                    }
                }
            }
        });
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(response)
        });
    }
}

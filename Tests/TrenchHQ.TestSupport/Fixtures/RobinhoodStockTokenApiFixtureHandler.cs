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

internal sealed class RobinhoodStockTokenApiFixtureHandler(string nvdaAddress, string gldAddress) : HttpMessageHandler
{
    internal int AssetRequests { get; private set; }
    internal int PriceRequests { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        byte[] payload;
        if (request.RequestUri?.AbsolutePath.EndsWith("/assets", StringComparison.Ordinal) == true)
        {
            AssetRequests++;
            payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                assets = new object[]
                {
                    new
                    {
                        status = "ASSET_STATUS_ACTIVE",
                        tokenSymbol = "NVDA",
                        tokenName = "NVIDIA Stock Token",
                        currentMultiplier = "0.5",
                        logoUrl = "https://cdn.robinhood.com/nvda.png",
                        deployments = new[] { new { chainId = 4663, contractAddress = nvdaAddress } }
                    },
                    new
                    {
                        status = "ASSET_STATUS_ACTIVE",
                        tokenSymbol = "GLD",
                        tokenName = "SPDR Gold Shares Stock Token",
                        currentMultiplier = "1",
                        logoUrl = "https://cdn.robinhood.com/gld.png",
                        deployments = new[] { new { chainId = 4663, contractAddress = gldAddress } }
                    },
                    new
                    {
                        status = "ASSET_STATUS_ACTIVE",
                        tokenSymbol = "OTHER",
                        tokenName = "Other Chain Token",
                        currentMultiplier = "1",
                        logoUrl = "http://insecure.invalid/logo.png",
                        deployments = new[]
                        {
                            new { chainId = 1, contractAddress = "0x2222222222222222222222222222222222222222" }
                        }
                    }
                }
            });
        }
        else if (request.RequestUri?.AbsolutePath.EndsWith("/prices/NVDA", StringComparison.Ordinal) == true)
        {
            PriceRequests++;
            payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                quotes = new[]
                {
                    new
                    {
                        tokenSymbol = "NVDA",
                        deployments = new[] { new { chainId = 4663, contractAddress = nvdaAddress } },
                        bid = "100.10",
                        ask = "100.30",
                        currency = "USD",
                        isTradingHalt = false,
                        generatedAt = DateTimeOffset.UtcNow.ToString("O")
                    }
                }
            });
        }
        else if (request.RequestUri?.AbsolutePath.EndsWith("/prices/GLD", StringComparison.Ordinal) == true)
        {
            PriceRequests++;
            payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                quotes = new[]
                {
                    new
                    {
                        tokenSymbol = "GLD",
                        deployments = new[] { new { chainId = 4663, contractAddress = gldAddress } },
                        bid = "401.22",
                        ask = "497",
                        dailyLow = "397.4",
                        dailyHigh = "403.33",
                        currency = "USD",
                        isTradingHalt = false,
                        generatedAt = DateTimeOffset.UtcNow.ToString("O")
                    }
                }
            });
        }
        else
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        });
    }
}

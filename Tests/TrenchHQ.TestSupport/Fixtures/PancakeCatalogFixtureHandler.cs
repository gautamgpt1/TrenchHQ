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

internal sealed class PancakeCatalogFixtureHandler(
    string selectedToken,
    EvmDeploymentCatalog? deployments = null) : HttpMessageHandler
{
    private int _requestCount;
    private EvmDeploymentCatalog Deployments => deployments ?? BnbDeploymentRegistry.Catalog;

    public int RequestCount => _requestCount;
    public bool SawExpectedChainQuery { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        Interlocked.Increment(ref _requestCount);
        var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
        SawExpectedChainQuery |= uri.Contains(
            $"chains={Deployments.CatalogChainId}",
            StringComparison.Ordinal);
        var protocol = uri.Contains("protocols=infinityBin", StringComparison.Ordinal)
            ? "infinityBin"
            : "infinityCl";
        var isSecondPage = uri.Contains("after=", StringComparison.Ordinal);
        object[] rows = isSecondPage
            ?
            [
                new
                {
                    chainId = int.Parse(
                        Deployments.ChainId,
                        System.Globalization.CultureInfo.InvariantCulture),
                    protocol,
                    id = protocol == "infinityBin"
                        ? "0x5555555555555555555555555555555555555555555555555555555555555555"
                        : "0x4444444444444444444444444444444444444444444444444444444444444444",
                    token0 = new { id = selectedToken, name = "Token", symbol = "TOKEN" },
                    token1 = new
                    {
                        id = Deployments.QuoteAssets[0].Address,
                        name = Deployments.QuoteAssets[0].Symbol,
                        symbol = Deployments.QuoteAssets[0].Symbol
                    },
                    tvlUSD = "1000",
                    volumeUSD24h = "100"
                }
            ]
            : [];
        var payload = new
        {
            hasNextPage = !isSecondPage,
            hasPrevPage = isSecondPage,
            startCursor = isSecondPage ? "second" : "first",
            endCursor = isSecondPage ? "done" : $"cursor-{protocol}",
            rows
        };
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload))
        });
    }
}

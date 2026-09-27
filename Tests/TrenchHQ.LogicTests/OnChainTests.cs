using TrenchHQ.Core.Markets;
using TrenchHQ.Core.OnChain;
using TrenchHQ.Core.OnChain.Evm;
using TrenchHQ.Core.OnChain.Solana;
using TrenchHQ.Core.Panels;
using TrenchHQ.Core.Providers;
using TrenchHQ.Core.Wallets;
using TrenchHQ.Core.Widgets;
using TrenchHQ.Core.Windows;
using TrenchHQ.Infrastructure.Diagnostics;
using TrenchHQ.Infrastructure.OnChain;
using TrenchHQ.Infrastructure.Panels;
using TrenchHQ.Infrastructure.Widgets;
using TrenchHQ.Infrastructure.Windows;
using System.Text.Json;
using Xunit;

namespace TrenchHQ.LogicTests;

public partial class LogicTests
{
    [Fact(DisplayName = "checksummed SHIB contract routes as an Ethereum address")]
    public void ChecksummedShibContractRoutesAsAnEthereumAddress()
    {
        Assert(EvmAddress.TryNormalize(
            "0x95aD61b0a150d79219dCF64E1E6Cc01f0B64C4cE",
            out var normalized));
        AssertEqual("0x95ad61b0a150d79219dcf64e1e6cc01f0b64c4ce", normalized);
    }

    [Fact(DisplayName = "Solana base58 round-trips boundary lengths and rejects ambiguous characters")]
    public void SolanaBase58RoundTripsBoundaryLengthsAndRejectsAmbiguousCharacters()
    {
        var random = new Random(20260919);
        foreach (var length in new[] { 0, 1, 31, 32, 64, 256 })
        {
            var bytes = new byte[length];
            random.NextBytes(bytes);
            if (bytes.Length > 1)
            {
                bytes[0] = 0;
                bytes[1] = 0;
            }

            Assert(bytes.SequenceEqual(SolanaBase58.Decode(SolanaBase58.Encode(bytes))));
        }

        foreach (var invalid in new[] { "0", "O", "I", "l", "+", "/", "abc\0" })
        {
            AssertThrows<FormatException>(() => SolanaBase58.Decode(invalid));
        }
    }

    [Fact(DisplayName = "DEX pool catalog normalizes metadata and ranks pools by liquidity")]
    public void DexPoolCatalogNormalizesMetadataAndRanksPoolsByLiquidity()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes("""
            [
              {
                "chainId": "solana",
                "dexId": "small-dex",
                "pairAddress": "small-pool",
                "baseToken": { "address": "selected-mint", "name": "Token Name", "symbol": "TOK" },
                "quoteToken": { "address": "quote-mint", "name": "USD Coin", "symbol": "USDC" },
                "liquidity": { "usd": 100 },
                "volume": { "h24": 25 },
                "marketCap": 1000,
                "pairCreatedAt": 1700000000000,
                "info": { "imageUrl": "http://unsafe.example/token.png" }
              },
              {
                "chainId": "solana",
                "dexId": "large-dex",
                "pairAddress": "large-pool",
                "labels": ["v3", "v3"],
                "baseToken": { "address": "sol-mint", "name": "Wrapped SOL", "symbol": "SOL" },
                "quoteToken": { "address": "selected-mint", "name": "Token Name", "symbol": "TOK" },
                "liquidity": { "usd": 5000 },
                "volume": { "h24": 900 },
                "fdv": 2000,
                "info": { "imageUrl": "https://cdn.example/token.png" }
              },
              {
                "chainId": "ethereum",
                "dexId": "wrong-chain",
                "pairAddress": "ignored-pool",
                "baseToken": { "address": "selected-mint", "name": "Wrong", "symbol": "BAD" },
                "quoteToken": { "address": "quote", "name": "Quote", "symbol": "Q" },
                "liquidity": { "usd": 999999 }
              }
            ]
            """);

        var result = DexScreenerPoolCatalogClient.Parse(payload, "solana", "selected-mint");
        AssertEqual("Token Name", result.AssetName);
        AssertEqual("TOK", result.AssetSymbol);
        AssertEqual("https://cdn.example/token.png", result.IconUri);
        AssertEqual(2, result.Pools.Length);
        AssertEqual("large-pool", result.Pools[0].PoolAddress);
        AssertEqual(5000d, result.Pools[0].LiquidityUsd);
        AssertEqual("v3", result.Pools[0].Labels.Single());
        AssertEqual("small-pool", result.Pools[1].PoolAddress);
    }

    [Fact(DisplayName = "DEX pool catalog caps each chain at thirty candidates")]
    public void DexPoolCatalogCapsEachChainAtThirtyCandidates()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(Enumerable.Range(0, 35).Select(index => new
        {
            chainId = "base",
            pairAddress = $"0x{index + 1:x40}",
            dexId = "uniswap",
            labels = new[] { "v3" },
            baseToken = new { address = "0x1111111111111111111111111111111111111111", name = "Token", symbol = "TOK" },
            quoteToken = new { address = BaseDeploymentRegistry.Usdc, name = "USD Coin", symbol = "USDC" },
            liquidity = new { usd = 35 - index }
        }));

        var result = DexScreenerPoolCatalogClient.Parse(
            payload,
            "base",
            "0x1111111111111111111111111111111111111111");

        AssertEqual(30, result.Pools.Length);
        AssertEqual("0x0000000000000000000000000000000000000001", result.Pools[0].PoolAddress);
    }

    [Fact(DisplayName = "DEX pool catalog caches repeated contract searches")]
    public async Task DexPoolCatalogCachesRepeatedContractSearches()
    {
        var handler = new CountingHttpMessageHandler("""
            [
              {
                "chainId": "solana",
                "dexId": "pumpswap",
                "pairAddress": "pool-address",
                "baseToken": { "address": "selected-mint", "name": "Token", "symbol": "TOK" },
                "quoteToken": { "address": "quote-mint", "name": "Wrapped SOL", "symbol": "SOL" },
                "liquidity": { "usd": 1000 }
              }
            ]
            """);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.dexscreener.com/")
        };
        var client = new DexScreenerPoolCatalogClient(httpClient);

        var first = await client.SearchAsync("solana", "selected-mint");
        var second = await client.SearchAsync("solana", "selected-mint");

        AssertEqual(1, handler.RequestCount);
        Assert(ReferenceEquals(first, second));
    }

    [Fact(DisplayName = "DEX pool catalog coalesces concurrent identical searches")]
    public async Task DexPoolCatalogCoalescesConcurrentIdenticalSearches()
    {
        var handler = new DelayedCountingHttpMessageHandler("[]");
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.dexscreener.com/")
        };
        var client = new DexScreenerPoolCatalogClient(httpClient);

        var searches = Enumerable.Range(0, 16)
            .Select(_ => client.SearchAsync("solana", "selected-mint"))
            .ToArray();
        await handler.FirstRequestStarted;
        handler.Release();
        var results = await Task.WhenAll(searches);

        AssertEqual(1, handler.RequestCount);
        Assert(results.All(result => ReferenceEquals(results[0], result)));
    }

    [Fact(DisplayName = "DEX pool catalog isolates caller cancellation from a shared search")]
    public async Task DexPoolCatalogIsolatesCallerCancellationFromASharedSearch()
    {
        var handler = new DelayedCountingHttpMessageHandler("[]");
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.dexscreener.com/")
        };
        var client = new DexScreenerPoolCatalogClient(httpClient);
        using var cancellation = new CancellationTokenSource();

        var cancelledSearch = client.SearchAsync("solana", "selected-mint", cancellation.Token);
        await handler.FirstRequestStarted;
        var survivingSearch = client.SearchAsync("solana", "selected-mint");
        cancellation.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => cancelledSearch);
        handler.Release();

        await survivingSearch;
        AssertEqual(1, handler.RequestCount);
    }

    [Fact(DisplayName = "DEX pool catalog retries after a failed shared search")]
    public async Task DexPoolCatalogRetriesAfterAFailedSharedSearch()
    {
        var handler = new SequenceHttpMessageHandler(
            System.Net.HttpStatusCode.InternalServerError,
            System.Net.HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.dexscreener.com/")
        };
        var client = new DexScreenerPoolCatalogClient(httpClient);

        await AssertThrowsAsync<InvalidOperationException>(() => client.SearchAsync("solana", "selected-mint"));
        await client.SearchAsync("solana", "selected-mint");

        AssertEqual(2, handler.RequestCount);
    }

    [Fact(DisplayName = "DEX pool catalog bounds unique-query cache growth")]
    public async Task DexPoolCatalogBoundsUniqueQueryCacheGrowth()
    {
        const int maximumExpectedCacheEntries = 128;
        var handler = new CountingHttpMessageHandler("[]");
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.dexscreener.com/")
        };
        var client = new DexScreenerPoolCatalogClient(httpClient);

        for (var index = 0; index <= maximumExpectedCacheEntries; index++)
        {
            await client.SearchAsync("solana", $"mint-{index}");
        }
        await client.SearchAsync("solana", "mint-0");

        AssertEqual(maximumExpectedCacheEntries + 2, handler.RequestCount);
    }

    [Fact(DisplayName = "DEX token search filters supported chains, deduplicates tokens, and ranks exact matches")]
    public void DexTokenSearchFiltersSupportedChainsDeduplicatesTokensAndRanksExactMatches()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes("""
            {
              "pairs": [
                {
                  "chainId": "solana",
                  "dexId": "pumpswap",
                  "pairAddress": "twine-sol-pool-1",
                  "baseToken": { "address": "twine-sol", "name": "TWINE", "symbol": "TWINE" },
                  "quoteToken": { "address": "sol", "name": "Wrapped SOL", "symbol": "SOL" },
                  "liquidity": { "usd": 1000 },
                  "marketCap": 5000,
                  "info": { "imageUrl": "https://cdn.example/twine.png" }
                },
                {
                  "chainId": "solana",
                  "dexId": "meteora",
                  "pairAddress": "twine-sol-pool-2",
                  "baseToken": { "address": "twine-sol", "name": "TWINE", "symbol": "TWINE" },
                  "quoteToken": { "address": "usdc", "name": "USD Coin", "symbol": "USDC" },
                  "liquidity": { "usd": 5000 }
                },
                {
                  "chainId": "base",
                  "dexId": "uniswap",
                  "pairAddress": "0x3333333333333333333333333333333333333333",
                  "baseToken": { "address": "0x1111111111111111111111111111111111111111", "name": "Twine Finance", "symbol": "TWIN" },
                  "quoteToken": { "address": "0x2222222222222222222222222222222222222222", "name": "USD Coin", "symbol": "USDC" },
                  "liquidity": { "usd": 9000 }
                },
                {
                  "chainId": "arbitrum",
                  "dexId": "uniswap",
                  "pairAddress": "ignored-pool",
                  "baseToken": { "address": "ignored", "name": "TWINE", "symbol": "TWINE" },
                  "quoteToken": { "address": "quote", "name": "Quote", "symbol": "Q" },
                  "liquidity": { "usd": 999999 }
                }
              ]
            }
            """);

        var result = DexScreenerPoolCatalogClient.ParseTokenSearch(payload, "twine");

        AssertEqual(2, result.Tokens.Length);
        AssertEqual("twine-sol", result.Tokens[0].Address);
        AssertEqual(5000d, result.Tokens[0].LiquidityUsd);
        AssertEqual("https://cdn.example/twine.png", result.Tokens[0].IconUri);
        AssertEqual(2, result.Tokens[0].Pools.Length);
        AssertEqual("twine-sol-pool-2", result.Tokens[0].Pools[0].PoolAddress);
        AssertEqual("base", result.Tokens[1].ChainId);
    }

    [Fact(DisplayName = "DEX token search caps ambiguous names at eight distinct tokens")]
    public void DexTokenSearchCapsAmbiguousNamesAtEightDistinctTokens()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            pairs = Enumerable.Range(0, 12).Select(index => new
            {
                chainId = "solana",
                dexId = "pumpswap",
                pairAddress = $"pool-{index}",
                baseToken = new { address = $"token-{index}", name = "Same Name", symbol = "SAME" },
                quoteToken = new { address = "sol", name = "Wrapped SOL", symbol = "SOL" },
                liquidity = new { usd = 12 - index }
            })
        });

        var result = DexScreenerPoolCatalogClient.ParseTokenSearch(payload, "same");

        AssertEqual(8, result.Tokens.Length);
        AssertEqual("token-0", result.Tokens[0].Address);
        AssertEqual("token-7", result.Tokens[7].Address);
    }

    [Fact(DisplayName = "DEX token search caches repeated name queries")]
    public async Task DexTokenSearchCachesRepeatedNameQueries()
    {
        var handler = new CountingHttpMessageHandler("""{ "pairs": [] }""");
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.dexscreener.com/")
        };
        var client = new DexScreenerPoolCatalogClient(httpClient);

        var first = await client.SearchTokensAsync("TWINE");
        var second = await client.SearchTokensAsync("twine");

        AssertEqual(1, handler.RequestCount);
        Assert(ReferenceEquals(first, second));
    }

    [Fact(DisplayName = "DEX token search coalesces concurrent identical queries")]
    public async Task DexTokenSearchCoalescesConcurrentIdenticalQueries()
    {
        var handler = new DelayedCountingHttpMessageHandler("""{ "pairs": [] }""");
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.dexscreener.com/")
        };
        var client = new DexScreenerPoolCatalogClient(httpClient);

        var searches = Enumerable.Range(0, 16)
            .Select(_ => client.SearchTokensAsync("twine"))
            .ToArray();
        await handler.FirstRequestStarted;
        handler.Release();
        await Task.WhenAll(searches);

        AssertEqual(1, handler.RequestCount);
    }

    [Fact(DisplayName = "on-chain widget metadata is normalized without changing pool identity")]
    public void OnChainWidgetMetadataIsNormalizedWithoutChangingPoolIdentity()
    {
        var catalog = new SavedWidgetCatalog
        {
            Widgets =
            [
                new SavedWidgetDefinition
                {
                    Instruments =
                    [
                        new SavedTickerInstrument
                        {
                            Kind = TickerInstrumentTypes.OnChainPool,
                            SelectedMint = " selected-mint ",
                            AssetName = " Token Name ",
                            AssetSymbol = " TOK ",
                            QuoteSymbol = " SOL ",
                            IconUri = "http://unsafe.example/token.png",
                            Pool = new OnChainPoolDescriptor
                            {
                                PoolKey = new OnChainPoolKey
                                {
                                    ProtocolId = OnChainProtocolIds.PumpSwap,
                                    PoolAddress = "pool-address"
                                }
                            }
                        }
                    ]
                }
            ]
        };

        var instrument = SavedWidgetCatalogRules.Normalize(catalog).Catalog.Widgets[0].Instruments[0];
        AssertEqual("TOK / SOL", instrument.DisplayLabel);
        AssertEqual("Token Name", instrument.AssetName);
        AssertEqual("TOK", instrument.AssetSymbol);
        AssertEqual("SOL", instrument.QuoteSymbol);
        AssertEqual<string?>(null, instrument.IconUri);
        AssertEqual("onchain|solana|mainnet-beta|pumpSwap|pool-address", SavedWidgetCatalogRules.GetInstrumentKey(instrument));
    }

    [Fact(DisplayName = "on-chain pool identity writes v2 and reads existing Solana keys")]
    public void OnChainPoolIdentityWritesV2AndReadsExistingSolanaKeys()
    {
        var key = new OnChainPoolKey
        {
            DeploymentKey = new OnChainDeploymentKey
            {
                ChainNamespace = ChainNamespaces.Eip155,
                ChainId = "1",
                ProtocolId = OnChainProtocolIds.UniswapV3,
                ContractAddress = "0x1F98431c8aD98523631AE4a59f267346ea31F984"
            },
            PoolId = "0x88e6A0c2dDD26FEEb64F039a2c41296FcB3f5640"
        };

        var json = JsonSerializer.Serialize(key);
        Assert(json.Contains("\"deploymentKey\"", StringComparison.Ordinal));
        Assert(json.Contains("\"poolId\"", StringComparison.Ordinal));
        Assert(!json.Contains("\"poolAddress\"", StringComparison.Ordinal));

        var legacy = JsonSerializer.Deserialize<OnChainPoolKey>("""
            {
              "chainNamespace": "solana",
              "chainId": "mainnet-beta",
              "protocolId": "pumpSwap",
              "poolAddress": "legacy-pool"
            }
            """);
        AssertEqual(ChainNamespaces.Solana, legacy!.DeploymentKey.ChainNamespace);
        AssertEqual(OnChainProtocolIds.PumpSwap, legacy.DeploymentKey.ProtocolId);
        AssertEqual("legacy-pool", legacy.PoolId);
    }

    [Fact(DisplayName = "EVM widget identity is chain-qualified and case-insensitive")]
    public void EvmWidgetIdentityIsChainQualifiedAndCaseInsensitive()
    {
        var widgetId = Guid.NewGuid().ToString("N");
        SavedTickerInstrument CreateInstrument(string selectedMint, string poolAddress) => new()
        {
            Kind = TickerInstrumentTypes.OnChainPool,
            SelectedMint = selectedMint,
            Pool = new OnChainPoolDescriptor
            {
                PoolKey = new OnChainPoolKey
                {
                    DeploymentKey = new OnChainDeploymentKey
                    {
                        ChainNamespace = ChainNamespaces.Eip155,
                        ChainId = "1",
                        ProtocolId = OnChainProtocolIds.UniswapV3,
                        ContractAddress = "0x1f98431c8ad98523631ae4a59f267346ea31f984"
                    },
                    PoolId = poolAddress
                },
                SupportStatus = OnChainSupportStatus.Supported
            }
        };
        var mixedCase = CreateInstrument(
            "0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2",
            "0x88e6A0c2dDD26FEEb64F039a2c41296FcB3f5640");
        var lowerCase = CreateInstrument(
            "0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2",
            "0x88e6a0c2ddd26feeb64f039a2c41296fcb3f5640");

        AssertEqual(
            "onchain|eip155|1|uniswap-v3|0x88e6a0c2ddd26feeb64f039a2c41296fcb3f5640",
            SavedWidgetCatalogRules.GetInstrumentKey(mixedCase));
        var planned = OnChainWidgetSubscriptionPlanner.Build(
            [new OverlayDefinition { WidgetIds = [widgetId] }],
            [],
            [new SavedWidgetDefinition { Id = widgetId, Instruments = [mixedCase, lowerCase] }]);
        AssertEqual(1, planned.Length);
        AssertEqual(ChainNamespaces.Eip155, planned[0].Descriptor.PoolKey.ChainNamespace);
    }

    [Fact(DisplayName = "EVM IPC payloads reject unknown fields")]
    public void EvmIpcPayloadsRejectUnknownFields()
    {
        try
        {
            JsonSerializer.Deserialize<EvmHeadUpdate>("""
                {
                  "ChainId": "1",
                  "ConnectionEpoch": 1,
                  "Number": 2,
                  "Hash": "0x02",
                  "ParentHash": "0x01",
                  "Timestamp": 3,
                  "ObservedAtUnixMs": 4,
                  "Unexpected": true
                }
                """);
            throw new InvalidOperationException("Unknown EVM IPC fields were accepted.");
        }
        catch (JsonException)
        {
        }
    }

    [Fact(DisplayName = "price ticker host contract defines orientation, density, and minimum dimensions")]
    public void PriceTickerHostContractDefinesOrientationDensityAndMinimumDimensions()
    {
        var overlay = PanelWidgetHostRules.ForOverlay();
        AssertEqual(PanelWidgetHostType.Overlay, overlay.HostType);
        AssertEqual(PanelWidgetOrientation.Vertical, overlay.Orientation);
        AssertEqual(PanelWidgetDensity.Comfortable, overlay.Density);
        AssertEqual(
            new PanelWidgetMinimumSize(PanelWidgetHostRules.PriceTickerOverlayMinimumWidth, null),
            PanelWidgetHostRules.GetPriceTickerMinimumSize(overlay));

        var horizontalBar = PanelWidgetHostRules.ForDockedBar("Top");
        AssertEqual(PanelWidgetOrientation.Horizontal, horizontalBar.Orientation);
        AssertEqual(PanelWidgetDensity.Compact, horizontalBar.Density);
        AssertEqual(
            new PanelWidgetMinimumSize(null, PanelWidgetHostRules.PriceTickerHorizontalBarMinimumHeight),
            PanelWidgetHostRules.GetPriceTickerMinimumSize(horizontalBar));

        var verticalBar = PanelWidgetHostRules.ForDockedBar("Left");
        AssertEqual(PanelWidgetOrientation.Vertical, verticalBar.Orientation);
        AssertEqual(
            new PanelWidgetMinimumSize(PanelWidgetHostRules.PriceTickerVerticalBarMinimumWidth, null),
            PanelWidgetHostRules.GetPriceTickerMinimumSize(verticalBar));
        Assert(PanelWidgetHostRules.SupportsWidget(PanelWidgetTypes.XTimeline, overlay));
        Assert(PanelWidgetHostRules.SupportsWidget(PanelWidgetTypes.XTimeline, horizontalBar));
        Assert(PanelWidgetHostRules.SupportsWidget(PanelWidgetTypes.XTimeline, verticalBar));
    }
}

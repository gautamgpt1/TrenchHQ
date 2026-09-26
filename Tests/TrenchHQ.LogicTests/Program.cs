using TrenchHQ.Helpers;
using TrenchHQ.Models;
using System.Text.Json;

var failures = new List<string>();
var passed = 0;

Run("diagnostic export has an explicit non-identifying schema and no settings or raw errors", () =>
{
    var output = ReleaseDiagnostics.Create(new Version(1, 0, 1, 0), OnChainPipelineDiagnostics.Snapshot());
    using var json = JsonDocument.Parse(output);
    var expected = new[] { "SchemaVersion", "Product", "Version", "CreatedUtc", "WindowsVersion", "Architecture",
        "DotnetVersion", "WorkingSetBytes", "ProcessorTimeMilliseconds", "Pipeline" };
    Assert(expected.SequenceEqual(json.RootElement.EnumerateObject().Select(p => p.Name)));
    AssertEqual("1.0.1.0", json.RootElement.GetProperty("Version").GetString());
    Assert(json.RootElement.GetProperty("Pipeline").EnumerateObject().All(p => p.Value.ValueKind == JsonValueKind.Number));
    Assert(!output.Contains(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase));
});

Run("provider failure thresholds survive offline rejection and reset after sustained health", () =>
{
    var failures = new OnChainProviderFailureCounter();
    Assert(!failures.Record(OnChainProviderFailureKind.Transport));
    Assert(!failures.Record(OnChainProviderFailureKind.Transport));
    Assert(failures.Record(OnChainProviderFailureKind.Transport));
    Assert(failures.Record(OnChainProviderFailureKind.Transport));
    Assert(!failures.Record(OnChainProviderFailureKind.RateLimited));
    Assert(failures.Record(OnChainProviderFailureKind.RateLimited));
    var now = DateTimeOffset.UtcNow;
    failures.RecordSuccess(now);
    failures.RecordSuccess(now.AddSeconds(29));
    AssertEqual(2, failures.Count);
    failures.RecordSuccess(now.AddSeconds(30));
    AssertEqual(0, failures.Count);
    Assert(!failures.Record(OnChainProviderFailureKind.Transport));
    Assert(failures.Record(OnChainProviderFailureKind.Authentication));
});

Run("pair parser accepts certified display names", () =>
{
    Assert(SelectedPairParser.TryParse(" BTC/USDT - Binance ", out var symbol, out var exchangeId, out var display));
    AssertEqual("BTC/USDT", symbol);
    AssertEqual("binance", exchangeId);
    AssertEqual("BTC/USDT", display);
});

Run("pair parser rejects malformed and unsupported pairs", () =>
{
    Assert(!SelectedPairParser.TryParse("BTC/USDT", out _, out _, out _));
    Assert(!SelectedPairParser.TryParse("BTC/USDT - Unknown Exchange", out _, out _, out _));
});

Run("centralized pair icons resolve exact packaged base assets", () =>
{
    var folder = CreateTemporaryFolder();
    try
    {
        var iconFolder = Path.Combine(folder, "Assets", "Crypto");
        Directory.CreateDirectory(iconFolder);
        File.WriteAllBytes(Path.Combine(iconFolder, "btc.png"), [0]);

        AssertEqual("btc", CentralizedAssetIconCatalog.GetBaseAssetSymbol("BTC/USDT"));
        AssertEqual("btc", CentralizedAssetIconCatalog.GetBaseAssetSymbol(" XBT/USD:USD "));
        AssertEqual("doge", CentralizedAssetIconCatalog.GetBaseAssetSymbol("XDG/USDT"));
        AssertEqual<string?>(null, CentralizedAssetIconCatalog.GetBaseAssetSymbol("BTCUSDT"));
        AssertEqual(
            "ms-appx:///Assets/Crypto/btc.png",
            CentralizedAssetIconCatalog.GetIconUri("BTC/USDT", folder));
        AssertEqual<string?>(null, CentralizedAssetIconCatalog.GetIconUri("ETH/USDT", folder));
    }
    finally
    {
        Directory.Delete(folder, recursive: true);
    }
});

Run("checksummed SHIB contract routes as an Ethereum address", () =>
{
    Assert(EvmAddress.TryNormalize(
        "0x95aD61b0a150d79219dCF64E1E6Cc01f0B64C4cE",
        out var normalized));
    AssertEqual("0x95ad61b0a150d79219dcf64e1e6cc01f0b64c4ce", normalized);
});

Run("Solana base58 round-trips boundary lengths and rejects ambiguous characters", () =>
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
});

Run("DEX pool catalog normalizes metadata and ranks pools by liquidity", () =>
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
});

Run("DEX pool catalog caps each chain at thirty candidates", () =>
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
});

await RunAsync("DEX pool catalog caches repeated contract searches", async () =>
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
});

await RunAsync("DEX pool catalog coalesces concurrent identical searches", async () =>
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
});

await RunAsync("DEX pool catalog isolates caller cancellation from a shared search", async () =>
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
});

await RunAsync("DEX pool catalog retries after a failed shared search", async () =>
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
});

await RunAsync("DEX pool catalog bounds unique-query cache growth", async () =>
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
});

Run("DEX token search filters supported chains, deduplicates tokens, and ranks exact matches", () =>
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
});

Run("DEX token search caps ambiguous names at eight distinct tokens", () =>
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
});

await RunAsync("DEX token search caches repeated name queries", async () =>
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
});

await RunAsync("DEX token search coalesces concurrent identical queries", async () =>
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
});

Run("on-chain widget metadata is normalized without changing pool identity", () =>
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
});

Run("on-chain pool identity writes v2 and reads existing Solana keys", () =>
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
});

Run("EVM widget identity is chain-qualified and case-insensitive", () =>
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
});

Run("EVM IPC payloads reject unknown fields", () =>
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
});

Run("normalized feed protocol parses a versioned price update", () =>
{
    using var document = JsonDocument.Parse("""
        {
          "protocolVersion": 1,
          "type": "priceUpdate",
          "market": {
            "id": "cex:binance:spot:BTC/USDT",
            "symbol": "BTC/USDT",
            "kind": "spot",
            "baseAsset": {
              "id": "cex:binance:asset:BTC",
              "symbol": "BTC",
              "kind": "venueAsset",
              "chainId": null,
              "address": null
            },
            "quoteAsset": {
              "id": "cex:binance:asset:USDT",
              "symbol": "USDT",
              "kind": "venueAsset",
              "chainId": null,
              "address": null
            },
            "venue": { "id": "binance", "kind": "centralizedExchange" }
          },
          "source": {
            "id": "ccxt:binance",
            "providerId": "ccxt",
            "venueId": "binance",
            "transport": "stream"
          },
          "price": { "value": 123.45 },
          "timing": {
            "sourceAt": null,
            "receivedAt": "2026-08-15T08:00:00Z"
          }
        }
        """);

    Assert(MarketFeedProtocolParser.TryParsePriceUpdate(document.RootElement, out var update));
    AssertEqual("cex:binance:spot:BTC/USDT", update!.Market.Id);
    AssertEqual("BTC", update.Market.BaseAsset.Symbol);
    AssertEqual("binance", update.Source.VenueId);
    AssertEqual("stream", update.Source.Transport);
    AssertEqual(123.45, update.Value);
    AssertEqual<DateTimeOffset?>(null, update.SourceTimestampUtc);
    AssertEqual(DateTimeOffset.Parse("2026-08-15T08:00:00Z"), update.ReceivedAtUtc);
});

Run("normalized feed protocol rejects incompatible or malformed updates", () =>
{
    using var nonObject = JsonDocument.Parse("[]");
    Assert(!MarketFeedProtocolParser.HasCurrentVersion(nonObject.RootElement));
    Assert(!MarketFeedProtocolParser.TryParsePriceUpdate(nonObject.RootElement, out _));

    using var wrongVersion = JsonDocument.Parse("""
        { "protocolVersion": 2, "type": "priceUpdate" }
        """);
    Assert(!MarketFeedProtocolParser.HasCurrentVersion(wrongVersion.RootElement));
    Assert(!MarketFeedProtocolParser.TryParsePriceUpdate(wrongVersion.RootElement, out _));

    using var invalidPrice = JsonDocument.Parse("""
        {
          "protocolVersion": 1,
          "market": {
            "id": "cex:test:spot:BTC/USDT",
            "symbol": "BTC/USDT",
            "kind": "spot",
            "baseAsset": { "id": "cex:test:asset:BTC", "symbol": "BTC", "kind": "venueAsset" },
            "quoteAsset": { "id": "cex:test:asset:USDT", "symbol": "USDT", "kind": "venueAsset" },
            "venue": { "id": "test", "kind": "centralizedExchange" }
          },
          "source": { "id": "ccxt:test", "providerId": "ccxt", "venueId": "test", "transport": "stream" },
          "price": { "value": 0 },
          "timing": { "receivedAt": "2026-08-15T08:00:00Z" }
        }
        """);
    Assert(!MarketFeedProtocolParser.TryParsePriceUpdate(invalidPrice.RootElement, out _));
});

Run("shortcut capture previews modifiers and keeps the last chord after release", () =>
{
    var capture = new PanelShortcutCapture();
    capture.Set(string.Empty);
    capture.Press(0xA2);
    AssertEqual("Ctrl", capture.Preview);
    Assert(capture.Shortcut == null);
    capture.Press(0xA0);
    AssertEqual("Ctrl+Shift", capture.Preview);
    capture.Press(0x41);
    AssertEqual("Ctrl+Shift+A", capture.Shortcut!);
    capture.Release(0x41);
    capture.Release(0xA0);
    capture.Release(0xA2);
    AssertEqual("Ctrl+Shift+A", capture.Preview);
    AssertEqual("Ctrl+Shift+A", capture.Shortcut!);
    Assert(!capture.IsShiftDown);
});

Run("shortcut capture displays invalid, reserved and multiple-key input without accepting it", () =>
{
    var capture = new PanelShortcutCapture();
    capture.Press(0x41);
    AssertEqual("A", capture.Preview);
    Assert(capture.Shortcut == null);
    capture.Set(string.Empty);
    capture.Press(0x5B);
    capture.Press(0x41);
    AssertEqual("Win+A", capture.Preview);
    Assert(capture.Shortcut == null);
    capture.Set(string.Empty);
    capture.Press(0x11);
    capture.Press(0x10);
    capture.Press(0x7B);
    AssertEqual("Ctrl+Shift+F12", capture.Preview);
    Assert(capture.Shortcut == null);
    capture.Release(0x7B);
    capture.Press(0x41);
    capture.Press(0x42);
    Assert(capture.Shortcut == null);
    AssertEqual("Ctrl+Shift+A+B", capture.Preview);
});

Run("shortcut reset and clear affect only the capture candidate", () =>
{
    var current = "Ctrl+Alt+K";
    var capture = new PanelShortcutCapture();
    capture.Set(current);
    capture.Press(0x41);
    Assert(capture.Shortcut == null);
    capture.Set(current);
    AssertEqual(current, capture.Shortcut!);
    capture.Set(string.Empty);
    AssertEqual(string.Empty, capture.Shortcut!);
    AssertEqual("Ctrl+Alt+K", current);
    capture.Press(0xA1);
    Assert(capture.IsShiftDown);
    capture.ResetHeldKeys();
    Assert(!capture.IsShiftDown);
    capture.Press(0x11);
    AssertEqual("Ctrl", capture.Preview);
});

Run("overlay width is clamped to product bounds", () =>
{
    AssertEqual(OverlayLayoutRules.MinimumWidth, OverlayLayoutRules.ClampWidth(100));
    AssertEqual(420d, OverlayLayoutRules.ClampWidth(420));
    AssertEqual(OverlayLayoutRules.MaximumWidth, OverlayLayoutRules.ClampWidth(900));
    AssertEqual(OverlayLayoutRules.DefaultWidth, OverlayLayoutRules.ClampWidth(double.NaN));
    AssertEqual(OverlayLayoutRules.DefaultWidth, OverlayLayoutRules.ClampWidth(double.PositiveInfinity));
});

Run("new panel definitions use neutral appearance defaults", () =>
{
    var overlay = new OverlayDefinition();
    AssertEqual(350d, overlay.WidthDip);
    AssertEqual("#000000", overlay.BackgroundColor);
    AssertEqual("#F5F5F5", overlay.TextColor);
    AssertEqual(85d, overlay.LuminosityOpacity);
    AssertEqual(40d, overlay.TintOpacity);
    Assert(overlay.PriceFlashOnChange);
    AssertEqual(string.Empty, overlay.Shortcut);

    var dockedBar = new DockedBarDefinition();
    AssertEqual(50, dockedBar.ThicknessPx);
    AssertEqual("#000000", dockedBar.BackgroundColor);
    AssertEqual("#F5F5F5", dockedBar.TextColor);
    Assert(dockedBar.PriceFlashOnChange);
    AssertEqual(string.Empty, dockedBar.Shortcut);
});

Run("panel shortcuts normalize supported keys and reject unsafe input", () =>
{
    Assert(PanelShortcutRules.TryParse(" alt + ctrl + h ", out var letter));
    AssertEqual("Ctrl+Alt+H", letter.DisplayText);
    Assert(PanelShortcutRules.TryParse("ctrl+shift+f11", out var function));
    AssertEqual("Ctrl+Shift+F11", function.DisplayText);
    Assert(!PanelShortcutRules.TryParse("win+shift+f11", out _, out var windowsReserved));
    AssertEqual(PanelShortcutValidationFailure.WindowsReserved, windowsReserved);
    Assert(!PanelShortcutRules.TryParse("ctrl+alt+f12", out _, out var debuggerReserved));
    AssertEqual(PanelShortcutValidationFailure.DebuggerReserved, debuggerReserved);
    Assert(!PanelShortcutRules.TryParse("Shift+H", out _, out var unsafeModifiers));
    AssertEqual(PanelShortcutValidationFailure.UnsafeModifiers, unsafeModifiers);
    Assert(!PanelShortcutRules.TryParse("Ctrl+H", out _, out unsafeModifiers));
    AssertEqual(PanelShortcutValidationFailure.UnsafeModifiers, unsafeModifiers);
    Assert(PanelShortcutRules.TryCreate(
        PanelShortcutRules.ControlModifier | PanelShortcutRules.AltModifier,
        '7',
        out var captured,
        out var captureFailure));
    AssertEqual(PanelShortcutValidationFailure.None, captureFailure);
    AssertEqual("Ctrl+Alt+7", captured.DisplayText);
    Assert(!PanelShortcutRules.TryParse("H", out _));
    Assert(!PanelShortcutRules.TryParse("Ctrl+Alt+Delete", out _));
    Assert(!PanelShortcutRules.TryParse("Ctrl+Ctrl+H", out _));
});

Run("subscription planner deduplicates exchange and symbol case-insensitively", () =>
{
    var firstWidgetId = Guid.NewGuid().ToString("N");
    var secondWidgetId = Guid.NewGuid().ToString("N");
    var definitions = new[]
    {
        new OverlayDefinition
        {
            WidgetIds = [firstWidgetId]
        },
        new OverlayDefinition
        {
            WidgetIds = [secondWidgetId]
        }
    };
    var widgets = new[]
    {
        new SavedWidgetDefinition
        {
            Id = firstWidgetId,
            Instruments = SavedWidgetCatalogRules.CreateCentralizedInstruments(
                ["BTC/USDT - Binance", "ETH/USDT - Binance", "not a pair"])
        },
        new SavedWidgetDefinition
        {
            Id = secondWidgetId,
            Instruments = SavedWidgetCatalogRules.CreateCentralizedInstruments(
                ["btc/usdt - binance", "BTC/USDT - Bybit"])
        }
    };

    var subscriptions = WidgetSubscriptionPlanner.Build(definitions, [], widgets);
    AssertEqual(3, subscriptions.Length);
    Assert(subscriptions.Any(item => item.VenueId == "binance" && item.Symbol.Equals("BTC/USDT", StringComparison.OrdinalIgnoreCase)));
    Assert(subscriptions.Any(item => item.VenueId == "binance" && item.Symbol == "ETH/USDT"));
    Assert(subscriptions.Any(item => item.VenueId == "bybit" && item.Symbol == "BTC/USDT"));
});

Run("widget content state distinguishes empty, loading, live, stale, unavailable, and error", () =>
{
    var now = DateTimeOffset.UtcNow;
    AssertEqual(
        new WidgetContentStatus(WidgetContentState.Empty, "No instruments"),
        WidgetContentStateRules.EvaluatePriceTicker(MarketSidecarState.Connected, false, false, false, now, default, now));
    AssertEqual(
        new WidgetContentStatus(WidgetContentState.Loading, "Connecting"),
        WidgetContentStateRules.EvaluatePriceTicker(MarketSidecarState.Connected, true, false, false, now, default, now));
    AssertEqual(
        new WidgetContentStatus(WidgetContentState.Loading, "Reconnecting"),
        WidgetContentStateRules.EvaluatePriceTicker(MarketSidecarState.Reconnecting, true, true, false, now, now, now));
    AssertEqual(
        new WidgetContentStatus(WidgetContentState.Live, "Live"),
        WidgetContentStateRules.EvaluatePriceTicker(MarketSidecarState.Connected, true, true, false, now, now, now));
    AssertEqual(
        new WidgetContentStatus(WidgetContentState.Stale, "Stale"),
        WidgetContentStateRules.EvaluatePriceTicker(
            MarketSidecarState.Connected,
            true,
            true,
            false,
            now,
            now - WidgetContentStateRules.StaleTimeout,
            now));
    AssertEqual(
        new WidgetContentStatus(WidgetContentState.Unavailable, "Unavailable"),
        WidgetContentStateRules.EvaluatePriceTicker(MarketSidecarState.Unavailable, true, true, false, now, now, now));
    AssertEqual(
        new WidgetContentStatus(WidgetContentState.Error, "Error"),
        WidgetContentStateRules.EvaluatePriceTicker(MarketSidecarState.Connected, true, true, true, now, now, now));
});

Run("price ticker host contract defines orientation, density, and minimum dimensions", () =>
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
});

Run("saved widget catalog normalizes identifiers, labels, types, and instruments", () =>
{
    var duplicateId = Guid.NewGuid().ToString("N");
    var catalog = new SavedWidgetCatalog
    {
        Version = 99,
        Widgets =
        [
            new SavedWidgetDefinition
            {
                Id = duplicateId,
                Name = "Custom name",
                Type = "PRICETICKER",
                Instruments = SavedWidgetCatalogRules.CreateCentralizedInstruments(
                    [" BTC/USDT - Binance ", "btc/usdt - binance", ""])
            },
            new SavedWidgetDefinition
            {
                Id = duplicateId,
                Name = "Another name",
                Instruments = SavedWidgetCatalogRules.CreateCentralizedInstruments(["ETH/USDT - Binance"])
            },
            new SavedWidgetDefinition
            {
                Type = "unsupported",
                Instruments = SavedWidgetCatalogRules.CreateCentralizedInstruments(["SOL/USDT - Binance"])
            }
        ]
    };

    var result = SavedWidgetCatalogRules.Normalize(catalog);
    Assert(result.WasChanged);
    AssertEqual(SavedWidgetCatalogRules.CurrentVersion, catalog.Version);
    AssertEqual(2, catalog.Widgets.Length);
    AssertEqual("Price Ticker 1", catalog.Widgets[0].Name);
    AssertEqual("Price Ticker 2", catalog.Widgets[1].Name);
    AssertEqual(1, catalog.Widgets[0].Instruments.Length);
    Assert(catalog.Widgets[0].Id != catalog.Widgets[1].Id);
});

Run("X tracker accepts only public account and List timelines", () =>
{
    Assert(SavedWidgetCatalogRules.TryNormalizeXTimelineUrl("@TrenchHQ_Crypto", out var handle));
    AssertEqual("https://x.com/TrenchHQ_Crypto", handle);
    Assert(SavedWidgetCatalogRules.TryNormalizeXTimelineUrl(
        "https://twitter.com/TwitterDev/lists/national-parks/", out var legacyList));
    AssertEqual("https://x.com/TwitterDev/lists/national-parks", legacyList);
    Assert(SavedWidgetCatalogRules.TryNormalizeXTimelineUrl(
        "https://x.com/i/lists/123456", out var idList));
    AssertEqual("https://x.com/i/lists/123456", idList);
    Assert(!SavedWidgetCatalogRules.TryNormalizeXTimelineUrl(
        "https://x.com/TrenchHQ_Crypto/status/123", out _));
    Assert(!SavedWidgetCatalogRules.TryNormalizeXTimelineUrl("https://x.com/home", out _));
    Assert(!SavedWidgetCatalogRules.TryNormalizeXTimelineUrl("javascript:alert(1)", out _));

    var catalog = new SavedWidgetCatalog
    {
        Version = 4,
        Widgets =
        [
            new SavedWidgetDefinition
            {
                Type = "XTIMELINE",
                XTimelineUrl = "https://www.x.com/TrenchHQ_Crypto/",
                XExternalContentConsent = true,
                Instruments = SavedWidgetCatalogRules.CreateCentralizedInstruments(["BTC/USDT - Binance"]),
                Wallets = [new SavedTrackedWallet()]
            }
        ]
    };
    SavedWidgetCatalogRules.Normalize(catalog);
    AssertEqual(SavedWidgetCatalogRules.CurrentVersion, catalog.Version);
    AssertEqual("X Tracker 1", catalog.Widgets[0].Name);
    AssertEqual("https://x.com/TrenchHQ_Crypto", catalog.Widgets[0].XTimelineUrl);
    AssertEqual("trenchhq_crypto", catalog.Widgets[0].XHandles.Single());
    Assert(catalog.Widgets[0].XExternalContentConsent);
    AssertEqual(0, catalog.Widgets[0].Instruments.Length);
    AssertEqual(0, catalog.Widgets[0].Wallets.Length);
});

Run("Website memory policy keeps visible panels live and reduces hidden or minimized views", () =>
{
    foreach (var hostVisible in new[] { true, false })
    foreach (var minimized in new[] { true, false })
    foreach (var widgetVisible in new[] { true, false })
        AssertEqual(!(hostVisible && !minimized && widgetVisible),
            WebsiteWidgetRules.UseLowMemory(hostVisible, minimized, widgetVisible));
    // Restore immediately returns to normal; neither focus nor interaction lock is an input.
    Assert(WebsiteWidgetRules.UseLowMemory(true, true, true));
    Assert(!WebsiteWidgetRules.UseLowMemory(true, false, true));
});

Run("Website shared runtime profiles isolate widgets and preserve legacy browser storage", () =>
{
    var localFolder = Path.Combine(Path.GetTempPath(), "TrenchHQBrowserStorageRules");
    var firstId = Guid.NewGuid();
    var secondId = Guid.NewGuid();
    var first = WebsiteWidgetRules.BrowserStorage(localFolder, firstId, false);
    var same = WebsiteWidgetRules.BrowserStorage(localFolder, firstId, false);
    var second = WebsiteWidgetRules.BrowserStorage(localFolder, secondId, false);
    AssertEqual(Path.Combine(localFolder, "WebsiteBrowser"), first.Folder);
    AssertEqual(first, same);
    AssertEqual(first.Folder, second.Folder);
    Assert(first.ProfileName != second.ProfileName);
    AssertEqual("widget_" + firstId.ToString("N"), first.ProfileName);
    var legacy = WebsiteWidgetRules.BrowserStorage(localFolder, firstId, true);
    AssertEqual(Path.Combine(localFolder, "WebsiteProfiles", firstId.ToString("N")), legacy.Folder);
    Assert(legacy.ProfileName == null);
});

Run("Website URLs reject scripts, files, HTTP and embedded credentials", () =>
{
    Assert(WebsiteWidgetRules.TryUrl(" https://j7tracker.io ", out var url));
    AssertEqual("https://j7tracker.io/", url);
    foreach (var value in new[] { "http://example.com", "javascript:alert(1)", "file:///C:/test", "data:text/html,test", "https://user:pass@example.com", "", "not a URL" })
        Assert(!WebsiteWidgetRules.TryUrl(value, out _));
});

Run("Website editor accepts ordinary addresses and empty drafts without a loading switch", () =>
{
    Assert(WebsiteWidgetRules.TrySaveUrl(" example.com/page ", out var url, out var error));
    AssertEqual("https://example.com/page", url);
    AssertEqual("", error);
    Assert(WebsiteWidgetRules.TrySaveUrl("https://example.com", out url, out error));
    AssertEqual("https://example.com/", url);
    Assert(WebsiteWidgetRules.TrySaveUrl("", out url, out error));
    AssertEqual("", url);
    foreach (var invalid in new[] { "http://example.com", "javascript:alert(1)", "file:///C:/test", "https://user:pass@example.com", "not a URL" })
    {
        Assert(!WebsiteWidgetRules.TrySaveUrl(invalid, out _, out error));
        Assert(error.Length > 0);
    }
});

Run("Website catalog normalization bounds presentation and preserves existing X watches", () =>
{
    var catalog = new SavedWidgetCatalog { Version = 6, Widgets =
    [
        new() { Type = PanelWidgetTypes.Website, WebsiteUrl = "https://example.com", WebsiteEnabled = true, WebsiteZoom = 0.01 },
        new() { Type = PanelWidgetTypes.Website, WebsiteUrl = "javascript:bad", WebsiteEnabled = true },
        new() { Type = PanelWidgetTypes.XTimeline, XHandles = ["alice"], XExternalContentConsent = true }
    ] };
    SavedWidgetCatalogRules.Normalize(catalog);
    AssertEqual(7, catalog.Version);
    AssertEqual(3, catalog.Widgets.Length);
    AssertEqual("Website 1", catalog.Widgets[0].Name);
    AssertEqual(0.5d, catalog.Widgets[0].WebsiteZoom);
    Assert(catalog.Widgets[0].WebsiteEnabled);
    Assert(!catalog.Widgets[1].WebsiteEnabled);
    AssertEqual("alice", catalog.Widgets[2].XHandles.Single());
    Assert(catalog.Widgets[2].XExternalContentConsent);
    Assert(PanelWidgetHostRules.SupportsWidget(PanelWidgetTypes.Website, PanelWidgetHostRules.ForOverlay()));
    Assert(PanelWidgetHostRules.SupportsWidget(PanelWidgetTypes.Website, PanelWidgetHostRules.ForDockedBar("left")));
    foreach (var edge in DockedBarLayoutRules.Edges)
        Assert(PanelWidgetHostRules.SupportsWidget(PanelWidgetTypes.Website, PanelWidgetHostRules.ForDockedBar(edge)));
});

Run("docked websites require usable page space in both dimensions", () =>
{
    Assert(WebsiteWidgetRules.CanLoadInViewport(320, 200));
    Assert(WebsiteWidgetRules.CanLoadInViewport(1920, 400));
    Assert(WebsiteWidgetRules.CanLoadInViewport(400, 1000));
    Assert(!WebsiteWidgetRules.CanLoadInViewport(319.9, 1000));
    Assert(!WebsiteWidgetRules.CanLoadInViewport(1920, 199.9));
    Assert(!WebsiteWidgetRules.CanLoadInViewport(0, 0));
    Assert(!WebsiteWidgetRules.CanLoadInViewport(double.NaN, 400));
    Assert(!WebsiteWidgetRules.CanLoadInViewport(400, double.PositiveInfinity));
});

Run("wallet widgets normalize supported addresses without changing price tickers", () =>
{
    const string solanaAddress = "11111111111111111111111111111111";
    var catalog = new SavedWidgetCatalog
    {
        Version = 3,
        Widgets =
        [
            new SavedWidgetDefinition
            {
                Type = PanelWidgetTypes.PriceTicker,
                Instruments = SavedWidgetCatalogRules.CreateCentralizedInstruments(["BTC/USDT - Binance"])
            },
            new SavedWidgetDefinition
            {
                Type = "WALLETACTIVITY",
                Wallets =
                [
                    new SavedTrackedWallet
                    {
                        ChainNamespace = ChainNamespaces.Eip155,
                        ChainId = EvmChainDefinitions.EthereumMainnetChainId,
                        Address = "0x95aD61b0a150d79219dCF64E1E6Cc01f0B64C4cE",
                        Label = " Whale "
                    },
                    new SavedTrackedWallet
                    {
                        ChainNamespace = ChainNamespaces.Solana,
                        ChainId = "mainnet-beta",
                        Address = solanaAddress
                    },
                    new SavedTrackedWallet
                    {
                        ChainNamespace = ChainNamespaces.Eip155,
                        ChainId = "42161",
                        Address = "0x1111111111111111111111111111111111111111"
                    }
                ]
            }
        ]
    };

    SavedWidgetCatalogRules.Normalize(catalog);

    AssertEqual(2, catalog.Widgets.Length);
    AssertEqual("Price Ticker 1", catalog.Widgets[0].Name);
    AssertEqual("Wallet Watcher 1", catalog.Widgets[1].Name);
    AssertEqual(2, catalog.Widgets[1].Wallets.Length);
    AssertEqual("Whale", catalog.Widgets[1].Wallets[0].Label);
    AssertEqual("0x95ad61b0a150d79219dcf64e1e6cc01f0b64c4ce", catalog.Widgets[1].Wallets[0].Address);
    AssertEqual(solanaAddress, catalog.Widgets[1].Wallets[1].Address);
    AssertEqual(0, catalog.Widgets[1].Instruments.Length);
});

Run("wallet subscription planner deduplicates addresses across active panel hosts", () =>
{
    var firstId = Guid.NewGuid().ToString("N");
    var secondId = Guid.NewGuid().ToString("N");
    var address = "0x95ad61b0a150d79219dcf64e1e6cc01f0b64c4ce";
    var planned = WalletWidgetSubscriptionPlanner.Build(
        [new OverlayDefinition { WidgetIds = [firstId] }],
        [new DockedBarDefinition { WidgetIds = [secondId] }],
        [
            new SavedWidgetDefinition
            {
                Id = firstId,
                Type = PanelWidgetTypes.WalletActivity,
                Wallets =
                [
                    new SavedTrackedWallet
                    {
                        ChainNamespace = ChainNamespaces.Eip155,
                        ChainId = EvmChainDefinitions.EthereumMainnetChainId,
                        Address = address,
                        Label = "First"
                    }
                ]
            },
            new SavedWidgetDefinition
            {
                Id = secondId,
                Type = PanelWidgetTypes.WalletActivity,
                Wallets =
                [
                    new SavedTrackedWallet
                    {
                        ChainNamespace = ChainNamespaces.Eip155,
                        ChainId = EvmChainDefinitions.EthereumMainnetChainId,
                        Address = address.ToUpperInvariant().Replace("0X", "0x", StringComparison.Ordinal),
                        Label = "Second"
                    }
                ]
            }
        ]);

    AssertEqual(1, planned.Length);
    AssertEqual(address, planned[0].Address);
});

Run("wallet activity rules cover ERC-20, ERC-721, and ERC-1155 transfer directions", () =>
{
    var sender = new SavedTrackedWallet
    {
        ChainNamespace = ChainNamespaces.Eip155,
        ChainId = EvmChainDefinitions.EthereumMainnetChainId,
        Address = "0x1111111111111111111111111111111111111111",
        Label = "Sender"
    };
    var receiver = new SavedTrackedWallet
    {
        ChainNamespace = ChainNamespaces.Eip155,
        ChainId = EvmChainDefinitions.EthereumMainnetChainId,
        Address = "0x2222222222222222222222222222222222222222",
        Label = "Receiver"
    };
    var filters = JsonSerializer.Serialize(WalletActivityRules.CreateEvmLogFilters([sender, receiver]));
    Assert(filters.Contains(WalletActivityRules.TransferTopic, StringComparison.Ordinal));
    Assert(filters.Contains(WalletActivityRules.TransferSingleTopic, StringComparison.Ordinal));
    Assert(filters.Contains(WalletActivityRules.TransferBatchTopic, StringComparison.Ordinal));
    Assert(filters.Contains("0x0000000000000000000000001111111111111111111111111111111111111111", StringComparison.Ordinal));

    var updates = WalletActivityRules.ParseEvmTransfer(
        new EvmLogUpdate
        {
            ChainId = EvmChainDefinitions.EthereumMainnetChainId,
            Address = "0x3333333333333333333333333333333333333333",
            Topics =
            [
                WalletActivityRules.TransferTopic,
                "0x0000000000000000000000001111111111111111111111111111111111111111",
                "0x0000000000000000000000002222222222222222222222222222222222222222"
            ],
            Data = "0x" + new string('0', 63) + "a",
            BlockNumber = 123,
            TransactionHash = "0x" + new string('4', 64),
            LogIndex = 7,
            ObservedAtUnixMs = 1000
        },
        [sender, receiver],
        "fixture");

    AssertEqual(2, updates.Length);
    AssertEqual(WalletActivityDirection.Outgoing, updates.Single(update => update.WalletLabel == "Sender").Direction);
    AssertEqual(WalletActivityDirection.Incoming, updates.Single(update => update.WalletLabel == "Receiver").Direction);
    AssertEqual("10", updates[0].AmountRaw);

    var maximum = WalletActivityRules.ParseEvmTransfer(
        new EvmLogUpdate
        {
            ChainId = EvmChainDefinitions.EthereumMainnetChainId,
            Address = "0x3333333333333333333333333333333333333333",
            Topics =
            [
                WalletActivityRules.TransferTopic,
                "0x0000000000000000000000001111111111111111111111111111111111111111",
                "0x0000000000000000000000002222222222222222222222222222222222222222"
            ],
            Data = "0x" + new string('f', 64),
            BlockNumber = 124,
            TransactionHash = "0x" + new string('5', 64),
            LogIndex = 8,
            ObservedAtUnixMs = 1001
        },
        [sender],
        "fixture");
    AssertEqual("115792089237316195423570985008687907853269984665640564039457584007913129639935",
        maximum[0].AmountRaw);

    var erc1155 = WalletActivityRules.ParseEvmTransfer(
        new EvmLogUpdate
        {
            ChainId = EvmChainDefinitions.EthereumMainnetChainId,
            Address = "0x3333333333333333333333333333333333333333",
            Topics =
            [
                WalletActivityRules.TransferSingleTopic,
                "0x000000000000000000000000aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "0x0000000000000000000000001111111111111111111111111111111111111111",
                "0x0000000000000000000000002222222222222222222222222222222222222222"
            ],
            Data = "0x" + new string('0', 63) + "2" + new string('0', 63) + "a",
            BlockNumber = 125,
            TransactionHash = "0x" + new string('6', 64),
            LogIndex = 9,
            ObservedAtUnixMs = 1002
        },
        [sender, receiver],
        "fixture");
    AssertEqual(2, erc1155.Length);
    AssertEqual("2", erc1155[0].AssetId);
    AssertEqual("10", erc1155[0].AmountRaw);

    var erc1155Batch = WalletActivityRules.ParseEvmTransfer(
        new EvmLogUpdate
        {
            ChainId = EvmChainDefinitions.EthereumMainnetChainId,
            Address = "0x3333333333333333333333333333333333333333",
            Topics =
            [
                WalletActivityRules.TransferBatchTopic,
                "0x000000000000000000000000aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "0x0000000000000000000000001111111111111111111111111111111111111111",
                "0x0000000000000000000000002222222222222222222222222222222222222222"
            ],
            Data = "0x" + HexWord(64) + HexWord(160)
                         + HexWord(2) + HexWord(2) + HexWord(3)
                         + HexWord(2) + HexWord(10) + HexWord(20),
            BlockNumber = 126,
            TransactionHash = "0x" + new string('9', 64),
            LogIndex = 10,
            ObservedAtUnixMs = 1003
        },
        [sender, receiver],
        "fixture");
    AssertEqual("2,3", erc1155Batch[0].AssetId);
    AssertEqual("10,20", erc1155Batch[0].AmountRaw);
});

Run("wallet activity rules normalize EVM native and internal value movement", () =>
{
    var wallet = new SavedTrackedWallet
    {
        ChainNamespace = ChainNamespaces.Eip155,
        ChainId = EvmChainDefinitions.BaseMainnetChainId,
        Address = "0x1111111111111111111111111111111111111111",
        Label = "Treasury"
    };
    var transaction = WalletActivityRules.ParseEvmTransaction(
        new EvmWalletTransaction
        {
            Hash = "0x" + new string('7', 64),
            From = wallet.Address,
            To = "0x2222222222222222222222222222222222222222",
            ValueRaw = "100"
        },
        EvmChainDefinitions.BaseMainnetChainId,
        200,
        1700000000000,
        [wallet],
        "fixture");
    AssertEqual(1, transaction.Length);
    AssertEqual(WalletActivityKind.NativeTransfer, transaction[0].Kind);
    AssertEqual(WalletActivityDirection.Outgoing, transaction[0].Direction);
    AssertEqual("100", transaction[0].AmountRaw);

    var trace = WalletActivityRules.ParseEvmTrace(
        new EvmWalletTrace
        {
            TransactionHash = "0x" + new string('8', 64),
            TracePath = "0.1",
            From = "0x2222222222222222222222222222222222222222",
            To = wallet.Address,
            ValueRaw = "55",
            BlockNumber = 201
        },
        EvmChainDefinitions.BaseMainnetChainId,
        1700000001000,
        [wallet],
        "fixture");
    AssertEqual(1, trace.Length);
    AssertEqual(WalletActivityKind.InternalTransfer, trace[0].Kind);
    AssertEqual(WalletActivityDirection.Incoming, trace[0].Direction);
    AssertEqual("55", trace[0].AmountRaw);
    Assert(WalletActivityRules.IsConfirmationUpgrade("head", "confirmed"));
    Assert(WalletActivityRules.IsConfirmationUpgrade("confirmed", "finalized"));
    Assert(!WalletActivityRules.IsConfirmationUpgrade("finalized", "confirmed"));
});

Run("wallet transaction flows classify buy sell swap and transfers", () =>
{
    const string wallet = "0x1111111111111111111111111111111111111111";
    const string token = "0x0b57b7333c43fe900d17a81bddc67c88c825afd6";
    const string otherToken = "0x2222222222222222222222222222222222222222";

    WalletActivityUpdate Movement(
        string transaction,
        string address,
        string symbol,
        byte decimals,
        string amount,
        WalletActivityDirection direction) => new()
    {
        EventId = transaction + address + direction,
        ChainNamespace = ChainNamespaces.Eip155,
        ChainId = EvmChainDefinitions.EthereumMainnetChainId,
        WalletAddress = wallet,
        WalletLabel = "Trader",
        TransactionId = transaction,
        Kind = WalletActivityKind.TokenTransfer,
        Direction = direction,
        AssetAddress = address,
        AssetSymbol = symbol,
        AssetDecimals = decimals,
        AmountRaw = amount
    };

    var buy = WalletActivityPresentationRules.Build([
        Movement("buy", EthereumDeploymentRegistry.Usdc, "USDC", 6, "12500000", WalletActivityDirection.Outgoing),
        Movement("buy", token, "SDLR", 18, "2500000000000000000", WalletActivityDirection.Incoming)
    ]);
    AssertEqual(WalletActivityDisplayKind.Buy, buy.Kind);
    AssertEqual("Buy SDLR", buy.Summary);
    AssertEqual("12.5 USDC → 2.5 SDLR", buy.Detail);
    AssertEqual(token, buy.MarketAssetAddress);

    var sell = WalletActivityPresentationRules.Build([
        Movement("sell", token, "SDLR", 18, "3000000000000000000", WalletActivityDirection.Outgoing),
        Movement("sell", EthereumDeploymentRegistry.Usdc, "USDC", 6, "17000000", WalletActivityDirection.Incoming)
    ]);
    AssertEqual(WalletActivityDisplayKind.Sell, sell.Kind);
    AssertEqual("Sell SDLR", sell.Summary);
    AssertEqual(token, sell.MarketAssetAddress);

    var swap = WalletActivityPresentationRules.Build([
        Movement("swap", token, "SDLR", 18, "1000000000000000000", WalletActivityDirection.Outgoing),
        Movement("swap", otherToken, "OTHER", 9, "2000000000", WalletActivityDirection.Incoming)
    ]);
    AssertEqual(WalletActivityDisplayKind.Swap, swap.Kind);
    AssertEqual("Swap SDLR → OTHER", swap.Summary);
    AssertEqual<string?>(null, swap.MarketAssetAddress);

    var transferIn = WalletActivityPresentationRules.Build([
        Movement("in", token, "SDLR", 18, "1", WalletActivityDirection.Incoming)
    ]);
    var transferOut = WalletActivityPresentationRules.Build([
        Movement("out", token, "SDLR", 18, "1", WalletActivityDirection.Outgoing)
    ]);
    AssertEqual(WalletActivityDisplayKind.TransferIn, transferIn.Kind);
    AssertEqual("Transfer in SDLR", transferIn.Summary);
    AssertEqual(WalletActivityDisplayKind.TransferOut, transferOut.Kind);
    AssertEqual("Transfer out SDLR", transferOut.Summary);
    AssertEqual("<0.000001 SDLR", transferIn.Detail);

    var nativeTransfer = WalletActivityPresentationRules.Build([new WalletActivityUpdate
    {
        EventId = "native-in",
        ChainNamespace = ChainNamespaces.Eip155,
        ChainId = EvmChainDefinitions.EthereumMainnetChainId,
        WalletAddress = wallet,
        WalletLabel = "Trader",
        TransactionId = "native-in",
        Kind = WalletActivityKind.NativeTransfer,
        Direction = WalletActivityDirection.Incoming,
        AmountRaw = "10000000000000000"
    }]);
    AssertEqual("Transfer in ETH", nativeTransfer.Summary);
    AssertEqual("0.01 ETH", nativeTransfer.Detail);
    AssertEqual("46.857", WalletActivityPresentationRules.FormatAmount("46856961", 6));
    AssertEqual("5M", WalletActivityPresentationRules.FormatAmount("5000000000000000", 9));
});

Run("wallet metadata uses exact venue evidence and factual market cap", () =>
{
    const string token = "0x0b57b7333c43fe900d17a81bddc67c88c825afd6";
    var updates = new[]
    {
        new WalletActivityUpdate
        {
            Counterparty = "0x1111111111111111111111111111111111111111",
            AssetAddress = token
        }
    };
    var catalog = new PoolCatalogSearchResult
    {
        AssetAddress = token,
        AssetSymbol = "SDLR",
        Pools =
        [
            new PoolCatalogEntry
            {
                ProtocolId = "uniswap",
                PoolAddress = "0x1111111111111111111111111111111111111111",
                BaseAsset = new PoolCatalogAsset { Address = token },
                QuoteAsset = new PoolCatalogAsset { Address = EthereumDeploymentRegistry.Usdc },
                LiquidityUsd = 100_000,
                MarketCapUsd = 149_700,
                IconUri = "https://example.com/token.png"
            }
        ]
    };
    var metadata = WalletActivityMetadataRules.Resolve(updates, token, catalog);
    AssertEqual("uniswap", metadata.VenueId);
    AssertEqual("https://example.com/token.png", metadata.TokenIconUri);
    AssertEqual(149_700d, metadata.MarketCapUsd);
    AssertEqual("$149.7K", WalletActivityMetadataRules.FormatUsd(metadata.MarketCapUsd!.Value));

    updates[0].Counterparty = "0x2222222222222222222222222222222222222222";
    metadata = WalletActivityMetadataRules.Resolve(updates, token, catalog);
    AssertEqual<string?>(null, metadata.VenueId);
});

Run("wallet metadata recognizes every implemented Solana venue", () =>
{
    var venues = new Dictionary<string, string>
    {
        ["6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P"] = OnChainProtocolIds.PumpBondingCurve,
        ["pAMMBay6oceH9fJKBRHGP5D4bD4sWpmSwMn52FMfXEA"] = OnChainProtocolIds.PumpSwap,
        ["675kPX9MHTjS2zt1qfr1NYHuzeLXfQM9H24wFSUt1Mp8"] = OnChainProtocolIds.RaydiumAmmV4,
        ["CPMMoo8L3F4NbTegBCKVNunggL7H1ZpdTHKxQB5qKP1C"] = OnChainProtocolIds.RaydiumCpmm,
        ["CAMMCzo5YL8w4VFF8KVHrK22GGUsp5VTaW7grrKgrWqK"] = OnChainProtocolIds.RaydiumClmm,
        ["Eo7WjKq67rjJQSZxS6z3YkapzY3eMj6Xy8X5EQVn5UaB"] = OnChainProtocolIds.MeteoraDammV1,
        ["cpamdpZCGKUy5JxQXB4dcpGPiikHawvSWAd6mEn1sGG"] = OnChainProtocolIds.MeteoraDammV2,
        ["LBUZKhRxPF3XUpBCjp4YzTKgLccjZhTSDM9YuVaPwxo"] = OnChainProtocolIds.MeteoraDlmm,
        ["whirLbMiicVdio4qvUfM5KAg6Ct8VwpYzGff3uctyCc"] = OnChainProtocolIds.OrcaWhirlpool,
        ["iwhrLHdsgrvmnwU8GF2FSmyabSMjfHwFGJAX2ufJ3ZN"] = OnChainProtocolIds.OrcaWhirlpool,
        ["MNFSTqtC93rEfYHB6hF82sKdZpUDFWkViLByLd1k1Ms"] = OnChainProtocolIds.ManifestOrderbook
    };
    foreach (var venue in venues)
    {
        AssertEqual(venue.Value, WalletActivityRules.GetSolanaVenueId([venue.Key]));
    }
    AssertEqual<string?>(null, WalletActivityRules.GetSolanaVenueId(["unknown"]));
});

Run("wallet activity ages advance from seconds through days", () =>
{
    const long now = 2_000_000_000_000;
    AssertEqual("now", WalletActivityAgeRules.Format(now, now));
    AssertEqual("now", WalletActivityAgeRules.Format(now, now + 999));
    AssertEqual("1s", WalletActivityAgeRules.Format(now, now + 1_000));
    AssertEqual("59s", WalletActivityAgeRules.Format(now, now + 59_999));
    AssertEqual("1m", WalletActivityAgeRules.Format(now, now + 60_000));
    AssertEqual("1h", WalletActivityAgeRules.Format(now, now + 3_600_000));
    AssertEqual("1d", WalletActivityAgeRules.Format(now, now + 86_400_000));
    AssertEqual("now", WalletActivityAgeRules.Format(now + 1_000, now));
});

Run("wallet transaction flow classification covers every EVM chain", () =>
{
    const string wallet = "0x1111111111111111111111111111111111111111";
    const string token = "0x2222222222222222222222222222222222222222";
    var chains = new[]
    {
        (EvmChainDefinitions.EthereumMainnetChainId, EthereumDeploymentRegistry.MainnetQuoteAssets[0]),
        (EvmChainDefinitions.BaseMainnetChainId, BaseDeploymentRegistry.MainnetQuoteAssets[0]),
        (EvmChainDefinitions.BnbMainnetChainId, BnbDeploymentRegistry.MainnetQuoteAssets[0]),
        (EvmChainDefinitions.RobinhoodMainnetChainId, RobinhoodDeploymentRegistry.MainnetQuoteAssets[0])
    };
    foreach (var (chainId, quote) in chains)
    {
        var presentation = WalletActivityPresentationRules.Build([
            new WalletActivityUpdate
            {
                ChainNamespace = ChainNamespaces.Eip155,
                ChainId = chainId,
                WalletAddress = wallet,
                TransactionId = chainId,
                Kind = WalletActivityKind.TokenTransfer,
                Direction = WalletActivityDirection.Outgoing,
                AssetAddress = quote.Address,
                AssetSymbol = quote.Symbol,
                AssetDecimals = 18,
                AmountRaw = "1000000000000000000"
            },
            new WalletActivityUpdate
            {
                ChainNamespace = ChainNamespaces.Eip155,
                ChainId = chainId,
                WalletAddress = wallet,
                TransactionId = chainId,
                Kind = WalletActivityKind.TokenTransfer,
                Direction = WalletActivityDirection.Incoming,
                AssetAddress = token,
                AssetSymbol = "TOKEN",
                AssetDecimals = 18,
                AmountRaw = "2000000000000000000"
            }
        ]);
        AssertEqual(WalletActivityDisplayKind.Buy, presentation.Kind);
        AssertEqual("Buy TOKEN", presentation.Summary);
    }
});

Run("Solana wallet balance deltas classify protocol-independent actions", () =>
{
    const string walletAddress = "11111111111111111111111111111111";
    const string tokenMint = "TokenMint111111111111111111111111111111111";
    var wallet = new SavedTrackedWallet
    {
        ChainNamespace = ChainNamespaces.Solana,
        ChainId = "mainnet-beta",
        Address = walletAddress,
        Label = "Trader"
    };
    var transaction = new SolanaWalletTransaction
    {
        Signature = "swap-signature",
        Slot = 99,
        FeeRaw = 5000,
        AccountAddresses = [walletAddress, "usdc-account", "token-account"],
        PreBalancesRaw = [10_000_000_000, 2_039_280, 2_039_280],
        PostBalancesRaw = [9_999_995_000, 2_039_280, 2_039_280],
        TokenBalances =
        [
            new SolanaWalletTokenBalance
            {
                AccountIndex = 1,
                AccountAddress = "usdc-account",
                Mint = WalletActivityPresentationRules.SolanaUsdcMint,
                OwnerAddress = walletAddress,
                Decimals = 6,
                PreAmountRaw = 12_500_000,
                PostAmountRaw = 0
            },
            new SolanaWalletTokenBalance
            {
                AccountIndex = 2,
                AccountAddress = "token-account",
                Mint = tokenMint,
                OwnerAddress = walletAddress,
                Decimals = 9,
                PreAmountRaw = 0,
                PostAmountRaw = 2_000_000_000
            }
        ]
    };
    var protocols = new[]
    {
        OnChainProtocolIds.PumpBondingCurve,
        OnChainProtocolIds.PumpSwap,
        OnChainProtocolIds.RaydiumAmmV4,
        OnChainProtocolIds.RaydiumCpmm,
        OnChainProtocolIds.RaydiumClmm,
        OnChainProtocolIds.MeteoraDammV1,
        OnChainProtocolIds.MeteoraDammV2,
        OnChainProtocolIds.MeteoraDlmm,
        OnChainProtocolIds.OrcaWhirlpool,
        OnChainProtocolIds.ManifestOrderbook
    };
    foreach (var protocol in protocols)
    {
        var updates = WalletActivityRules.ParseSolanaTransaction(
            transaction,
            wallet,
            "confirmed",
            1000,
            "fixture:" + protocol);
        var presentation = WalletActivityPresentationRules.Build(updates);
        AssertEqual(WalletActivityDisplayKind.Buy, presentation.Kind);
        AssertEqual("Buy TokenM…1111", presentation.Summary);
        AssertEqual("12.5 USDC → 2 TokenM…1111", presentation.Detail);
        Assert(!updates.Any(static update => update.AssetAddress == null));
    }

    var createdAccount = new SolanaWalletTransaction
    {
        Signature = "incoming-signature",
        Slot = 100,
        FeeRaw = 5000,
        AccountAddresses = [walletAddress, "new-token-account"],
        PreBalancesRaw = [5_000_000_000, 0],
        PostBalancesRaw = [4_997_955_720, 2_039_280],
        TokenBalances =
        [
            new SolanaWalletTokenBalance
            {
                AccountIndex = 1,
                AccountAddress = "new-token-account",
                Mint = tokenMint,
                OwnerAddress = walletAddress,
                Decimals = 9,
                PostAmountRaw = 1_000_000_000
            }
        ]
    };
    var incoming = WalletActivityPresentationRules.Build(WalletActivityRules.ParseSolanaTransaction(
        createdAccount,
        wallet,
        "confirmed",
        1001,
        "fixture:transfer"));
    AssertEqual(WalletActivityDisplayKind.TransferIn, incoming.Kind);
    AssertEqual("Transfer in TokenM…1111", incoming.Summary);

    var solBuy = new SolanaWalletTransaction
    {
        Signature = "sol-buy-signature",
        Slot = 101,
        FeeRaw = 5000,
        AccountAddresses = [walletAddress, "token-account"],
        PreBalancesRaw = [5_000_000_000, 2_039_280],
        PostBalancesRaw = [2_999_995_000, 2_039_280],
        TokenBalances =
        [
            new SolanaWalletTokenBalance
            {
                AccountIndex = 1,
                AccountAddress = "token-account",
                Mint = tokenMint,
                OwnerAddress = walletAddress,
                Decimals = 9,
                PostAmountRaw = 1_000_000_000
            }
        ]
    };
    var buy = WalletActivityPresentationRules.Build(WalletActivityRules.ParseSolanaTransaction(
        solBuy,
        wallet,
        "confirmed",
        1002,
        "fixture:native-buy"));
    AssertEqual(WalletActivityDisplayKind.Buy, buy.Kind);
    AssertEqual("2 SOL → 1 TokenM…1111", buy.Detail);
});

Run("Solana wallet notifications become stable wallet-qualified activities", () =>
{
    var wallet = new SavedTrackedWallet
    {
        ChainNamespace = ChainNamespaces.Solana,
        ChainId = "mainnet-beta",
        Address = "11111111111111111111111111111111",
        Label = "Treasury"
    };
    var update = WalletActivityRules.CreateSolanaTransaction(
        wallet,
        "signature",
        42,
        false,
        "processed",
        1000,
        "fixture");
    AssertEqual("solana|mainnet-beta|signature|11111111111111111111111111111111", update.EventId);
    AssertEqual(WalletActivityKind.Transaction, update.Kind);
    AssertEqual(42UL, update.ChainPosition);
    AssertEqual("processed", update.Confirmation);
});

await RunAsync("saved widget catalog round-trips and preserves the previous valid file", async () =>
{
    var folder = CreateTemporaryFolder();
    try
    {
        var original = new SavedWidgetCatalog
        {
            Widgets =
            [
                new SavedWidgetDefinition
                {
                    Instruments = SavedWidgetCatalogRules.CreateCentralizedInstruments(["BTC/USDT - Binance"])
                }
            ]
        };
        await SavedWidgetCatalogStore.SaveAsync(folder, original);

        var updated = new SavedWidgetCatalog
        {
            Widgets =
            [
                new SavedWidgetDefinition
                {
                    Instruments = SavedWidgetCatalogRules.CreateCentralizedInstruments(["ETH/USDT - Binance"])
                }
            ]
        };
        await SavedWidgetCatalogStore.SaveAsync(folder, updated);

        var loaded = await SavedWidgetCatalogStore.LoadAsync(folder);
        Assert(!loaded.WasRejected);
        AssertEqual("ETH/USDT", loaded.Catalog!.Widgets[0].Instruments[0].Symbol);

        var previousJson = await File.ReadAllTextAsync(Path.Combine(folder, SavedWidgetCatalogStore.LastValidFileName));
        Assert(previousJson.Contains("BTC/USDT", StringComparison.Ordinal)
               && previousJson.Contains("binance", StringComparison.Ordinal));
    }
    finally
    {
        Directory.Delete(folder, true);
    }
});

await RunAsync("saved widget catalog serializes overlapping atomic saves", async () =>
{
    var folder = CreateTemporaryFolder();
    try
    {
        var saves = Enumerable.Range(0, 16).Select(index => SavedWidgetCatalogStore.SaveAsync(
            folder,
            new SavedWidgetCatalog
            {
                Widgets =
                [
                    new SavedWidgetDefinition
                    {
                        Type = PanelWidgetTypes.Website,
                        WebsiteUrl = $"https://example.com/{index}",
                        WebsiteEnabled = true
                    }
                ]
            }));

        await Task.WhenAll(saves);
        var loaded = await SavedWidgetCatalogStore.LoadAsync(folder);
        Assert(!loaded.WasRejected);
        AssertEqual(1, loaded.Catalog!.Widgets.Length);
        AssertEqual(0, Directory.GetFiles(folder, "*.tmp").Length);
    }
    finally
    {
        Directory.Delete(folder, true);
    }
});

await RunAsync("version one widget catalogs migrate to typed centralized instruments", async () =>
{
    var folder = CreateTemporaryFolder();
    try
    {
        var path = Path.Combine(folder, SavedWidgetCatalogStore.CatalogFileName);
        await File.WriteAllTextAsync(
            path,
            "{\"Version\":1,\"Widgets\":[{\"Id\":\"11111111111111111111111111111111\",\"Name\":\"Price Ticker 1\",\"Type\":\"priceTicker\",\"SelectedPairs\":[\"BTC/USDT - Binance\"]}]}");

        var loaded = await SavedWidgetCatalogStore.LoadAsync(folder);
        Assert(!loaded.WasRejected);
        Assert(loaded.RequiresSave);
        AssertEqual(1, loaded.SourceVersion);
        AssertEqual(SavedWidgetCatalogRules.CurrentVersion, loaded.Catalog!.Version);
        AssertEqual(TickerInstrumentTypes.CentralizedMarket, loaded.Catalog.Widgets[0].Instruments[0].Kind);
        AssertEqual("binance", loaded.Catalog.Widgets[0].Instruments[0].VenueId);
        AssertEqual("BTC/USDT", loaded.Catalog.Widgets[0].Instruments[0].Symbol);
    }
    finally
    {
        Directory.Delete(folder, true);
    }
});

await RunAsync("version three price ticker catalogs migrate without data loss", async () =>
{
    var folder = CreateTemporaryFolder();
    try
    {
        var path = Path.Combine(folder, SavedWidgetCatalogStore.CatalogFileName);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new SavedWidgetCatalog
        {
            Version = 3,
            Widgets =
            [
                new SavedWidgetDefinition
                {
                    Type = PanelWidgetTypes.PriceTicker,
                    Instruments = SavedWidgetCatalogRules.CreateCentralizedInstruments(["ETH/USDT - Binance"])
                }
            ]
        }));

        var loaded = await SavedWidgetCatalogStore.LoadAsync(folder);
        Assert(!loaded.WasRejected);
        Assert(loaded.RequiresSave);
        AssertEqual(3, loaded.SourceVersion);
        AssertEqual(SavedWidgetCatalogRules.CurrentVersion, loaded.Catalog!.Version);
        AssertEqual("ETH/USDT", loaded.Catalog.Widgets[0].Instruments[0].Symbol);
        AssertEqual(0, loaded.Catalog.Widgets[0].Wallets.Length);
    }
    finally
    {
        Directory.Delete(folder, true);
    }
});

await RunAsync("version six X watches migrate and Website settings round-trip independently", async () =>
{
    var folder = CreateTemporaryFolder();
    try
    {
        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(folder, SavedWidgetCatalogStore.CatalogFileName);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new SavedWidgetCatalog
        {
            Version = 6,
            Widgets = [new() { Id = id, Type = PanelWidgetTypes.XTimeline, XHandles = ["alice"], XExternalContentConsent = true }]
        }));
        var loaded = await SavedWidgetCatalogStore.LoadAsync(folder);
        Assert(!loaded.WasRejected && loaded.RequiresSave && loaded.SourceVersion == 6);
        var catalog = loaded.Catalog!;
        AssertEqual(id, catalog.Widgets[0].Id);
        AssertEqual("alice", catalog.Widgets[0].XHandles.Single());
        catalog.Widgets = [.. catalog.Widgets, new()
        {
            Type = PanelWidgetTypes.Website, WebsiteUrl = "https://j7tracker.io",
            WebsiteEnabled = true, WebsiteZoom = 0.75
        }];
        await SavedWidgetCatalogStore.SaveAsync(folder, catalog);
        var reloaded = (await SavedWidgetCatalogStore.LoadAsync(folder)).Catalog!;
        AssertEqual(7, reloaded.Version);
        AssertEqual(2, reloaded.Widgets.Length);
        AssertEqual(id, reloaded.Widgets[0].Id);
        Assert(reloaded.Widgets[0].XExternalContentConsent);
        var website = reloaded.Widgets[1];
        AssertEqual("https://j7tracker.io/", website.WebsiteUrl);
        AssertEqual(0.75d, website.WebsiteZoom);
        Assert(website.WebsiteEnabled);
        Assert(File.Exists(Path.Combine(folder, SavedWidgetCatalogStore.LastValidFileName)));
    }
    finally { Directory.Delete(folder, true); }
});

await RunAsync("unsupported or malformed saved widget catalogs preserve exact rejected bytes", async () =>
{
    foreach (var testCase in new[]
    {
        (Json: "{\"Version\":99,\"Widgets\":[]}", Version: 99),
        (Json: "{\"Version\":7,\"Widgets\":[", Version: 0)
    })
    {
        var folder = CreateTemporaryFolder();
        try
        {
            var path = Path.Combine(folder, SavedWidgetCatalogStore.CatalogFileName);
            await File.WriteAllTextAsync(path, testCase.Json);

            var loaded = await SavedWidgetCatalogStore.LoadAsync(folder);
            Assert(loaded.WasRejected);
            AssertEqual(testCase.Version, loaded.SourceVersion);
            Assert(loaded.Catalog == null);
            AssertEqual(SavedWidgetCatalogStore.RejectedFileName, loaded.BackupFileName);
            AssertEqual(
                testCase.Json,
                await File.ReadAllTextAsync(Path.Combine(folder, SavedWidgetCatalogStore.RejectedFileName)));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
});

Run("market reconnect backoff grows and caps at fifteen seconds", () =>
{
    AssertEqual(TimeSpan.FromSeconds(1), MarketReconnectRules.GetDelay(0));
    AssertEqual(TimeSpan.FromSeconds(2), MarketReconnectRules.GetDelay(1));
    AssertEqual(TimeSpan.FromSeconds(15), MarketReconnectRules.GetDelay(4));
    AssertEqual(TimeSpan.FromSeconds(15), MarketReconnectRules.GetDelay(20));
});

Run("price formatting is deterministic and bounded for extreme values", () =>
{
    AssertEqual("$1,234.50", PriceFormattingRules.Format(1234.5));
    AssertEqual("$12.35", PriceFormattingRules.Format(12.345));
    AssertEqual("$0.1235", PriceFormattingRules.Format(0.123456));
    AssertEqual("$0.001235", PriceFormattingRules.Format(0.00123456));
    AssertEqual("$0.00001235", PriceFormattingRules.Format(0.000012345));
    AssertEqual("$1.234E-9", PriceFormattingRules.Format(0.000000001234));
    AssertEqual("$1E+9", PriceFormattingRules.Format(1_000_000_000));
    AssertEqual("$--", PriceFormattingRules.Format(double.NaN));
    AssertEqual("$--", PriceFormattingRules.Format(double.PositiveInfinity));
    Assert(PriceFormattingRules.Format(double.MaxValue).Length <= PriceFormattingRules.MaximumDisplayLength);
    AssertEqual("2.00 SOL", PriceFormattingRules.FormatExact("2000000000000000000", 18, suffix: " SOL"));
    AssertEqual("1E-30", PriceFormattingRules.FormatExact("1", 30));
    AssertEqual("1E+400", PriceFormattingRules.FormatExact("1" + new string('0', 400), 0));
    AssertEqual("1E-4294967295", PriceFormattingRules.FormatExact("1", uint.MaxValue));
});

Run("price tick state handles first, up, down, equal, and disabled updates", () =>
{
    var state = PriceTickRules.GetNextState(true, false, 0, 100, PriceTickState.Down);
    AssertEqual(PriceTickState.Default, state);

    state = PriceTickRules.GetNextState(true, true, 100, 101, state);
    AssertEqual(PriceTickState.Up, state);

    state = PriceTickRules.GetNextState(true, true, 101, 101, state);
    AssertEqual(PriceTickState.Up, state);

    state = PriceTickRules.GetNextState(true, true, 101, 99, state);
    AssertEqual(PriceTickState.Down, state);

    state = PriceTickRules.GetNextState(false, true, 99, 100, state);
    AssertEqual(PriceTickState.Default, state);
});

Run("website panel minimum includes viewport and controls without reserving status text space", () =>
{
    AssertEqual((322, 246), WebsiteWidgetRules.MinimumDockedSize(1));
    AssertEqual((402, 307), WebsiteWidgetRules.MinimumDockedSize(1.25));
    AssertEqual((642, 490), WebsiteWidgetRules.MinimumDockedSize(2));
    var enlarged = WebsiteWidgetRules.MinimumDockedSize(1.5);
    AssertEqual(482, enlarged.Width);
    AssertEqual(368, enlarged.Height);
    Assert(WebsiteWidgetRules.CanLoadInViewport((enlarged.Width - 2) / 1.5, (enlarged.Height - 2) / 1.5 - 44));
    var maximum = DockedBarLayoutRules.GetMaximumThickness(1080);
    AssertEqual(324, maximum);
    Assert(WebsiteWidgetRules.MinimumDockedSize(1.25).Height < maximum);
});

Run("website minimum never authorizes exceeding the existing thirty-percent panel cap", () =>
{
    var required = WebsiteWidgetRules.MinimumDockedSize(1);
    var smallMaximum = DockedBarLayoutRules.GetMaximumThickness(768);
    Assert(required.Height > smallMaximum);
    var largeMaximum = DockedBarLayoutRules.GetMaximumThickness(1080);
    Assert(required.Height <= largeMaximum);
    AssertEqual(230, Math.Min(required.Height, smallMaximum));
    AssertEqual(246, Math.Min(required.Height, largeMaximum));
});

Run("application panel fixed minimum includes window area and controls at monitor scale", () =>
{
    AssertEqual((362, 246), DockedBarLayoutRules.MinimumApplicationWindowSize(1));
    AssertEqual((452, 307), DockedBarLayoutRules.MinimumApplicationWindowSize(1.25));
    AssertEqual((722, 490), DockedBarLayoutRules.MinimumApplicationWindowSize(2));
    AssertEqual((362, 246), DockedBarLayoutRules.MinimumApplicationWindowSize(double.NaN));
    AssertEqual((362, 246), DockedBarLayoutRules.MinimumApplicationWindowSize(0.5));
    var required = DockedBarLayoutRules.MinimumApplicationWindowSize(1.25);
    Assert(new WindowPinBounds(0, 0, required.Width - 2, required.Height - 2 - 55).Fits(0, 0));
    Assert(required.Height < DockedBarLayoutRules.GetMaximumThickness(1080));
    Assert(required.Width < DockedBarLayoutRules.GetMaximumThickness(1920));
    Assert(required.Height > DockedBarLayoutRules.GetMaximumThickness(768));
    AssertEqual(230, Math.Min(required.Height, DockedBarLayoutRules.GetMaximumThickness(768)));
});

Run("application pin containment accepts smaller visible frames but rejects spillover", () =>
{
    var panel = new WindowPinBounds(-1920, 100, 360, 240);
    Assert(panel.Contains(panel));
    Assert(panel.Contains(new WindowPinBounds(-1912, 100, 344, 232)));
    Assert(!panel.Contains(new WindowPinBounds(-1921, 100, 360, 240)));
    Assert(!panel.Contains(new WindowPinBounds(-1920, 99, 360, 240)));
    Assert(!panel.Contains(new WindowPinBounds(-1920, 100, 361, 240)));
    Assert(!panel.Contains(new WindowPinBounds(-1920, 100, 360, 241)));
    Assert(!panel.Contains(new WindowPinBounds(-1920, 100, 0, 240)));
});

Run("fixed-height overlays bound height and stay smaller than the available work area", () =>
{
    AssertEqual(240d, OverlayLayoutRules.ClampHeight(10));
    AssertEqual(900d, OverlayLayoutRules.ClampHeight(10000));
    AssertEqual(480d, OverlayLayoutRules.ClampHeight(double.NaN));
    AssertEqual(480d, OverlayLayoutRules.ClampHeight(double.PositiveInfinity));
    foreach (var scale in new[] { 1d, 1.5, 2, 3 })
    {
        var size = OverlayLayoutRules.ClampPixelSize((int)(600 * scale), (int)(934 * scale), 1280, 680);
        Assert(size.Width <= 1264 && size.Height <= 664);
        Assert(size.Width > 0 && size.Height > 0);
    }
    AssertEqual((1, 1), OverlayLayoutRules.ClampPixelSize(350, 480, 10, 10));
});

Run("overlay height is adjustable for feeds websites and apps but never the ticker", () =>
{
    foreach (var type in new[] { PanelWidgetTypes.XTimeline, PanelWidgetTypes.Website, PanelWidgetTypes.WalletActivity })
        Assert(OverlayLayoutRules.HasAdjustableHeight(false, type));
    Assert(OverlayLayoutRules.HasAdjustableHeight(true, null));
    Assert(!OverlayLayoutRules.HasAdjustableHeight(false, PanelWidgetTypes.PriceTicker));
    Assert(!OverlayLayoutRules.HasAdjustableHeight(false, null));
    Assert(!OverlayLayoutRules.HasAdjustableHeight(false, "unknown"));
    AssertEqual(320d, OverlayLayoutRules.ClampWidth(250, PanelWidgetTypes.Website));
    AssertEqual(250d, OverlayLayoutRules.ClampWidth(250, PanelWidgetTypes.XTimeline));
    AssertEqual(350d, OverlayLayoutRules.ClampWidth(double.NaN, PanelWidgetTypes.Website));
});

await RunAsync("height is saved independently per overlay without widget-owned sizing", async () =>
{
    var folder = CreateTemporaryFolder();
    try
    {
        var widget = new SavedWidgetDefinition { Type = PanelWidgetTypes.Website };
        var first = new OverlayDefinition { WidgetIds = [widget.Id], WidthDip = 350, HeightDip = 270 };
        var second = new OverlayDefinition { WidgetIds = [widget.Id], WidthDip = 540, HeightDip = 660 };
        await DesktopSetupStore.SaveAsync(folder, new DesktopSetup { Overlays = [first, second] });
        var loaded = (await DesktopSetupStore.LoadAsync(folder))!;
        AssertEqual(270d, loaded.Overlays[0].HeightDip);
        AssertEqual(660d, loaded.Overlays[1].HeightDip);
        AssertEqual(540d, loaded.Overlays[1].WidthDip);
        var changed = loaded.Overlays[0];
        changed.UseApplicationWindow = true;
        DesktopSetupRules.Normalize(new DesktopSetup { Overlays = [changed] });
        AssertEqual(270d, changed.HeightDip);
        changed.UseApplicationWindow = false;
        changed.WidgetIds = ["ticker"];
        AssertEqual(270d, changed.HeightDip);
        Assert(!JsonSerializer.Serialize(widget).Contains("Height"));
        Assert(!JsonSerializer.Serialize(first).Contains("ApplicationWindowHeight"));
    }
    finally { Directory.Delete(folder, recursive: true); }
});

Run("application overlays exclude widget subscriptions and cannot gain a fallback widget", () =>
{
    var widget = new SavedWidgetDefinition();
    var settings = new DesktopSetup
    {
        Overlays = [new OverlayDefinition { UseApplicationWindow = true, WidgetIds = [widget.Id], HeightDip = 1200 }]
    };
    DesktopSetupRules.Normalize(settings);
    AssertEqual(900d, settings.Overlays[0].HeightDip);
    AssertEqual(0, settings.Overlays[0].WidgetIds.Length);
    DesktopSetupRules.ReconcileWidgetReferences(settings, [widget]);
    AssertEqual(0, settings.Overlays[0].WidgetIds.Length);
    AssertEqual(0, WidgetSubscriptionPlanner.Build(settings.Overlays, [], [widget]).Length);
    AssertEqual(0, OnChainWidgetSubscriptionPlanner.Build(settings.Overlays, [], [widget]).Length);
    AssertEqual(0, WalletWidgetSubscriptionPlanner.Build(settings.Overlays, [], [widget]).Length);
});

Run("application window content fits both dimensions and excludes widget subscriptions", () =>
{
    Assert(new WindowPinBounds(0, 0, 320, 240).Fits(320, 240));
    Assert(!new WindowPinBounds(0, 0, 319, 240).Fits(320, 240));
    Assert(!new WindowPinBounds(0, 0, 320, 239).Fits(320, 240));
    Assert(!new WindowPinBounds(0, 0, 100, 80).Fits(0, 0));
    var widget = new SavedWidgetDefinition();
    var settings = new DesktopSetup
    {
        DockedBars = [new DockedBarDefinition { UseApplicationWindow = true, WidgetIds = [widget.Id] }]
    };
    DesktopSetupRules.Normalize(settings);
    Assert(settings.DockedBars[0].UseApplicationWindow);
    AssertEqual(0, settings.DockedBars[0].WidgetIds.Length);
    DesktopSetupRules.ReconcileWidgetReferences(settings, [widget]);
    AssertEqual(0, settings.DockedBars[0].WidgetIds.Length);
    AssertEqual(0, WidgetSubscriptionPlanner.Build([], settings.DockedBars, [widget]).Length);
});

await RunAsync("application window panel mode round-trips without persisting foreign window handles", async () =>
{
    var folder = CreateTemporaryFolder();
    try
    {
        await DesktopSetupStore.SaveAsync(folder, new DesktopSetup
        {
            DockedBars = [new DockedBarDefinition { UseApplicationWindow = true }],
            Overlays = [new OverlayDefinition { UseApplicationWindow = true, HeightDip = 520 }]
        });
        var loaded = await DesktopSetupStore.LoadAsync(folder);
        Assert(loaded!.DockedBars[0].UseApplicationWindow);
        AssertEqual(0, loaded.DockedBars[0].WidgetIds.Length);
        Assert(loaded.Overlays[0].UseApplicationWindow);
        AssertEqual(520d, loaded.Overlays[0].HeightDip);
        AssertEqual(0, loaded.Overlays[0].WidgetIds.Length);
        var json = await File.ReadAllTextAsync(Path.Combine(folder, DesktopSetupStore.SettingsFileName));
        Assert(!json.Contains("Handle") && !json.Contains("ProcessId"));
        Assert(!new DockedBarDefinition().UseApplicationWindow);
    }
    finally { Directory.Delete(folder, recursive: true); }
});

Run("docked-bar layout rules enforce orientation-specific thickness", () =>
{
    AssertEqual(36, DockedBarLayoutRules.ClampThickness("Top", 10));
    AssertEqual(500, DockedBarLayoutRules.ClampThickness("Bottom", 500));
    AssertEqual(96, DockedBarLayoutRules.ClampThickness("Left", 50));
    AssertEqual(900, DockedBarLayoutRules.ClampThickness("Right", 900));
    foreach (var edge in DockedBarLayoutRules.Edges)
        AssertEqual(1200, DockedBarLayoutRules.ClampThickness(edge, int.MaxValue));
    AssertEqual("Top", DockedBarLayoutRules.NormalizeEdge("unsupported"));
    AssertEqual(40, DockedBarLayoutRules.GetRuntimeThickness("Top", 40, 1));
    AssertEqual(36, DockedBarLayoutRules.GetRuntimeThickness("Top", 36, 1));
    AssertEqual(96, DockedBarLayoutRules.GetRuntimeThickness("Left", 96, 1));
    AssertEqual(45, DockedBarLayoutRules.GetRuntimeThickness("Top", 40, 1.25));
    AssertEqual(54, DockedBarLayoutRules.GetRuntimeThickness("Top", 40, 1.5));
    AssertEqual(72, DockedBarLayoutRules.GetRuntimeThickness("Top", 40, 2));
    AssertEqual(81, DockedBarLayoutRules.GetRuntimeThickness("Top", 40, 2.25));
    AssertEqual(192, DockedBarLayoutRules.GetRuntimeThickness("Left", 180, 2));
    AssertEqual(360, DockedBarLayoutRules.GetRuntimeThickness("Top", 40, 10));
    AssertEqual(180, DockedBarLayoutRules.GetRuntimeThickness("Left", 180, double.NaN));
});

Run("docked-bar runtime sizing respects monitor caps and display scaling", () =>
{
    AssertEqual(324, DockedBarLayoutRules.GetRuntimeThickness("Top", 1200, 1, 1080));
    AssertEqual(576, DockedBarLayoutRules.GetRuntimeThickness("Right", 1200, 1, 1920));
    AssertEqual(648, DockedBarLayoutRules.GetRuntimeThickness("Bottom", 1200, 1, 2160));
    AssertEqual(72, DockedBarLayoutRules.GetRuntimeThickness("Top", 36, 1, 1080, 2));
    AssertEqual(216, DockedBarLayoutRules.GetRuntimeThickness("Left", 96, 1.5, 1920, 1.5));
    AssertEqual(30, DockedBarLayoutRules.GetRuntimeThickness("Left", 96, 2, 100, 2));
    AssertEqual(36, DockedBarLayoutRules.GetRuntimeThickness("Top", 36, double.NaN, 1080, double.NaN));
    AssertEqual(324, DockedBarLayoutRules.GetRuntimeThickness("Top", 36, double.MaxValue, 1080, 2));
    AssertEqual(43, DockedBarLayoutRules.GetRuntimeThickness("Top", 50, 1, 1080, 1, 0.85));
    AssertEqual(63, DockedBarLayoutRules.GetRuntimeThickness("Top", 50, 1, 1080, 1, 1.25));
});

Run("price ticker docked bars wrap into automatic rows and columns", () =>
{
    AssertEqual(35, DockedBarLayoutRules.GetAutomaticPriceTickerThickness("Top", 3, 840, 1, 1080, 1));
    AssertEqual(105, DockedBarLayoutRules.GetAutomaticPriceTickerThickness("Bottom", 8, 840, 1, 1080, 1));
    AssertEqual(158, DockedBarLayoutRules.GetAutomaticPriceTickerThickness("Top", 8, 840, 1.5, 1080, 1));
    AssertEqual(150, DockedBarLayoutRules.GetAutomaticPriceTickerThickness("Left", 20, 1040, 1, 1920, 1));
    AssertEqual(300, DockedBarLayoutRules.GetAutomaticPriceTickerThickness("Right", 21, 1040, 1, 1920, 1));
    AssertEqual(56, DockedBarLayoutRules.GetAutomaticPriceTickerThickness("Top", 16, 1536, 2.25, 1080, 1, 56));
    AssertEqual(70, DockedBarLayoutRules.GetAutomaticPriceTickerThickness("Top", 16, 1536, 2.25, 1080, 1.25, 56));
    AssertEqual(35, DockedBarLayoutRules.GetAutomaticPriceTickerThickness("Top", 3, 840, 1, 1080, 1, 35));
    AssertEqual(44, DockedBarLayoutRules.GetAutomaticPriceTickerThickness("Top", 3, 840, 1, 1080, 1.25, 35));
    AssertEqual(30, DockedBarLayoutRules.GetAutomaticPriceTickerThickness("Top", 3, 840, 1, 1080, 1, double.NaN, 0.85));
    AssertEqual(88, DockedBarLayoutRules.GetAutomaticPriceTickerThickness("Top", 3, 840, 1, 1080, 1, double.NaN, 1.25));
});

Run("panel content size normalizes and preserves per-panel scale", () =>
{
    AssertEqual(PanelContentSizes.Small, PanelContentSizeRules.Normalize(" SMALL "));
    AssertEqual(PanelContentSizes.Standard, PanelContentSizeRules.Normalize("unsupported"));
    AssertEqual(PanelContentSizes.Large, PanelContentSizeRules.Normalize("Large"));
    AssertEqual(0.85d, PanelContentSizeRules.GetScale(PanelContentSizes.Small));
    AssertEqual(1d, PanelContentSizeRules.GetScale(null));
    AssertEqual(1.25d, PanelContentSizeRules.GetScale(PanelContentSizes.Large));

    var settings = DesktopSetupRules.Normalize(new DesktopSetup
    {
        Overlays = [new OverlayDefinition { ContentSize = PanelContentSizes.Large }],
        DockedBars = [new DockedBarDefinition { ContentSize = PanelContentSizes.Small }]
    });
    var overlay = settings.Overlays[0];
    var docked = settings.DockedBars[0];
    AssertEqual(PanelContentSizes.Large, overlay.ContentSize);
    AssertEqual(PanelContentSizes.Small, docked.ContentSize);
});

Run("price ticker automatic thickness keeps the desktop safety cap", () =>
{
    AssertEqual(324, DockedBarLayoutRules.GetAutomaticPriceTickerThickness("Top", 100, 840, 1, 1080, 1));
    AssertEqual(576, DockedBarLayoutRules.GetAutomaticPriceTickerThickness("Left", 1000, 1040, 1, 1920, 1));
    AssertEqual(72, DockedBarLayoutRules.GetAutomaticPriceTickerThickness("Top", 0, 840, 1, 1080, 2));
    AssertEqual(192, DockedBarLayoutRules.GetAutomaticPriceTickerThickness("Right", 0, 1040, 2, 1920, 1));
});

Run("machine-wide outages do not exhaust the approved provider route", () =>
{
    AssertEqual(
        false,
        OnChainProviderFailoverRules.ShouldAdvanceRoute(OnChainProviderFailureKind.Transport, false));
    AssertEqual(
        true,
        OnChainProviderFailoverRules.ShouldAdvanceRoute(OnChainProviderFailureKind.Transport, true));
    AssertEqual(
        true,
        OnChainProviderFailoverRules.ShouldAdvanceRoute(OnChainProviderFailureKind.RateLimited, false));
    AssertEqual(
        true,
        OnChainProviderFailoverRules.ShouldAdvanceRoute(OnChainProviderFailureKind.Authentication, false));
});

Run("opposite maximum docked bars leave at least forty percent of each monitor dimension", () =>
{
    foreach (var span in new[] { 768, 1080, 1366, 1920, 2160, 3840 })
    foreach (var scale in new[] { 1d, 1.5d, 2d, 3d })
    foreach (var edges in new[] { new[] { "Left", "Right" }, new[] { "Top", "Bottom" } })
    {
        // Old saved oversized values and accessibility scaling must not bypass the safety cap.
        var first = DockedBarLayoutRules.GetRuntimeThickness(edges[0], int.MaxValue, scale, span, scale);
        var second = DockedBarLayoutRules.GetRuntimeThickness(edges[1], int.MaxValue, scale, span, scale);
        Assert(first <= Math.Floor(span * 0.3d));
        Assert(second <= Math.Floor(span * 0.3d));
        Assert(span - first - second >= Math.Ceiling(span * 0.4d));
    }
});

Run("subscription planner deduplicates pairs across overlays and docked bars", () =>
{
    var firstWidgetId = Guid.NewGuid().ToString("N");
    var secondWidgetId = Guid.NewGuid().ToString("N");
    var overlays = new[]
    {
        new OverlayDefinition { WidgetIds = [firstWidgetId] }
    };
    var dockedBars = new[]
    {
        new DockedBarDefinition { WidgetIds = [secondWidgetId] }
    };
    var widgets = new[]
    {
        new SavedWidgetDefinition
        {
            Id = firstWidgetId,
            Instruments = SavedWidgetCatalogRules.CreateCentralizedInstruments(["BTC/USDT - Binance"])
        },
        new SavedWidgetDefinition
        {
            Id = secondWidgetId,
            Instruments = SavedWidgetCatalogRules.CreateCentralizedInstruments(
                ["btc/usdt - binance", "ETH/USDT - Binance"])
        }
    };

    var subscriptions = WidgetSubscriptionPlanner.Build(overlays, dockedBars, widgets);
    AssertEqual(2, subscriptions.Length);
});

Run("three overlays retain independent widgets while sharing upstream subscriptions", () =>
{
    var firstWidgetId = Guid.NewGuid().ToString("N");
    var secondWidgetId = Guid.NewGuid().ToString("N");
    var thirdWidgetId = Guid.NewGuid().ToString("N");
    var overlays = new[]
    {
        new OverlayDefinition { WidgetIds = [firstWidgetId] },
        new OverlayDefinition { WidgetIds = [secondWidgetId] },
        new OverlayDefinition { WidgetIds = [thirdWidgetId] }
    };
    var dockedBars = new[]
    {
        new DockedBarDefinition { WidgetIds = [firstWidgetId] }
    };
    var widgets = new[]
    {
        new SavedWidgetDefinition
        {
            Id = firstWidgetId,
            Instruments = SavedWidgetCatalogRules.CreateCentralizedInstruments(
                ["BTC/USDT - Binance", "ETH/USDT - Binance"])
        },
        new SavedWidgetDefinition
        {
            Id = secondWidgetId,
            Instruments = SavedWidgetCatalogRules.CreateCentralizedInstruments(
                ["btc/usdt - binance", "SOL/USDT - Bybit"])
        },
        new SavedWidgetDefinition
        {
            Id = thirdWidgetId,
            Instruments = SavedWidgetCatalogRules.CreateCentralizedInstruments(["BTC/USDT - OKX"])
        }
    };

    var subscriptions = WidgetSubscriptionPlanner.Build(overlays, dockedBars, widgets);
    AssertEqual(4, subscriptions.Length);
    Assert(subscriptions.Any(item => item.VenueId == "binance" && item.Symbol.Equals("BTC/USDT", StringComparison.OrdinalIgnoreCase)));
    Assert(subscriptions.Any(item => item.VenueId == "binance" && item.Symbol == "ETH/USDT"));
    Assert(subscriptions.Any(item => item.VenueId == "bybit" && item.Symbol == "SOL/USDT"));
    Assert(subscriptions.Any(item => item.VenueId == "okx" && item.Symbol == "BTC/USDT"));
});

Run("on-chain planner preserves exact pool identity independently of provider choice", () =>
{
    var widgetId = Guid.NewGuid().ToString("N");
    var descriptor = new OnChainPoolDescriptor
    {
        PoolKey = new OnChainPoolKey
        {
            ProtocolId = OnChainProtocolIds.PumpSwap,
            PoolAddress = "pool-address"
        },
        BaseMint = "base-mint",
        QuoteMint = "quote-mint",
        SupportStatus = OnChainSupportStatus.Supported
    };
    var widget = new SavedWidgetDefinition
    {
        Id = widgetId,
        Instruments =
        [
            new SavedTickerInstrument
            {
                Kind = TickerInstrumentTypes.OnChainPool,
                SelectedMint = "base-mint",
                Pool = descriptor
            },
            new SavedTickerInstrument
            {
                Kind = TickerInstrumentTypes.OnChainPool,
                SelectedMint = "base-mint",
                Pool = descriptor
            }
        ]
    };

    var planned = OnChainWidgetSubscriptionPlanner.Build(
        [new OverlayDefinition { WidgetIds = [widgetId] }],
        [],
        [widget]);
    AssertEqual(1, planned.Length);
    AssertEqual("pool-address", planned[0].Descriptor.PoolKey.PoolAddress);
    AssertEqual("base-mint", planned[0].SelectedMint);
});

Run("price mode defaults to one-second polling and shared WebSocket demand wins", () =>
{
    var pool = new OnChainPoolDescriptor
    {
        PoolKey = new OnChainPoolKey
        {
            ProtocolId = OnChainProtocolIds.PumpSwap,
            PoolAddress = "shared-pool"
        },
        SupportStatus = OnChainSupportStatus.Supported
    };
    SavedWidgetDefinition Widget(string id, string mode) => new()
    {
        Id = id,
        OnChainPriceMode = mode,
        Instruments = [new SavedTickerInstrument
        {
            Kind = TickerInstrumentTypes.OnChainPool,
            SelectedMint = "selected-mint",
            Pool = pool
        }]
    };
    var polling = Widget("polling-widget", OnChainPriceModes.OneSecondPolling);
    var streamed = Widget("streamed-widget", OnChainPriceModes.WebSocket);
    var overlays = new[]
    {
        new OverlayDefinition { WidgetIds = [polling.Id] },
        new OverlayDefinition { WidgetIds = [streamed.Id] }
    };
    AssertEqual(OnChainPriceModes.OneSecondPolling,
        new SavedWidgetDefinition().OnChainPriceMode);
    AssertEqual(OnChainPriceModes.OneSecondPolling,
        OnChainPriceModes.Normalize("invalid"));
    AssertEqual(OnChainPriceModes.WebSocket,
        OnChainWidgetSubscriptionPlanner.BuildWithModes(overlays, [], [polling, streamed])
            .Single().Mode);
    AssertEqual(OnChainPriceModes.OneSecondPolling,
        OnChainWidgetSubscriptionPlanner.BuildWithModes([overlays[0]], [], [polling, streamed])
            .Single().Mode);
    Assert(!OnChainPriceModes.SupportsPolling(new OnChainPoolDescriptor
    {
        PoolKey = new OnChainPoolKey
        {
            DeploymentKey = new OnChainDeploymentKey
            {
                ChainNamespace = ChainNamespaces.Eip155,
                ChainId = EvmChainDefinitions.EthereumMainnetChainId,
                ProtocolId = OnChainProtocolIds.Curve
            },
            PoolId = "0x1111111111111111111111111111111111111111"
        }
    }));
    Assert(!OnChainPriceModes.SupportsPolling(new OnChainPoolDescriptor
    {
        PoolKey = new OnChainPoolKey
        {
            DeploymentKey = new OnChainDeploymentKey
            {
                ChainNamespace = ChainNamespaces.Solana,
                ProtocolId = OnChainProtocolIds.ManifestOrderbook
            }
        }
    }));
    var executionOnly = Widget("execution-only", OnChainPriceModes.OneSecondPolling);
    executionOnly.Instruments[0].Pool = new OnChainPoolDescriptor
    {
        PoolKey = new OnChainPoolKey
        {
            DeploymentKey = new OnChainDeploymentKey
            {
                ChainNamespace = ChainNamespaces.Eip155,
                ChainId = EvmChainDefinitions.EthereumMainnetChainId,
                ProtocolId = OnChainProtocolIds.Curve
            },
            PoolId = "0x1111111111111111111111111111111111111111"
        },
        SupportStatus = OnChainSupportStatus.Supported
    };
    var normalized = SavedWidgetCatalogRules.Normalize(new SavedWidgetCatalog
    {
        Widgets = [executionOnly]
    }).Catalog.Widgets.Single();
    AssertEqual(OnChainPriceModes.WebSocket, normalized.OnChainPriceMode);
});

Run("panel display content requires an application window or valid widget", () =>
{
    var widget = new SavedWidgetDefinition { Id = Guid.NewGuid().ToString("N") };
    Assert(!DesktopSetupRules.HasDisplayContent(false, [], [widget]));
    Assert(!DesktopSetupRules.HasDisplayContent(false, [Guid.NewGuid().ToString("N")], [widget]));
    Assert(DesktopSetupRules.HasDisplayContent(false, [widget.Id], [widget]));
    Assert(DesktopSetupRules.HasDisplayContent(true, [], []));
});

Run("widget reference reconciliation preserves unassigned panels without fallback", () =>
{
    var settings = new DesktopSetup
    {
        Overlays =
        [
            new OverlayDefinition { Enabled = true, WidgetIds = [Guid.NewGuid().ToString("N")] }
        ],
        DockedBars =
        [
            new DockedBarDefinition { Enabled = true, WidgetIds = [] },
            new DockedBarDefinition { Enabled = true, UseApplicationWindow = true, WidgetIds = [Guid.NewGuid().ToString("N")] }
        ]
    };

    Assert(DesktopSetupRules.ReconcileWidgetReferences(settings, []));
    AssertEqual(0, settings.Overlays[0].WidgetIds.Length);
    AssertEqual(0, settings.DockedBars[0].WidgetIds.Length);
    Assert(!settings.Overlays[0].Enabled);
    Assert(!settings.DockedBars[0].Enabled);
    Assert(settings.DockedBars[1].Enabled);
    AssertEqual(0, settings.DockedBars[1].WidgetIds.Length);
    Assert(!DesktopSetupRules.ReconcileWidgetReferences(settings, []));

    var widget = new SavedWidgetDefinition { Id = Guid.NewGuid().ToString("N") };
    Assert(!DesktopSetupRules.ReconcileWidgetReferences(settings, [widget]));
    AssertEqual(0, settings.Overlays[0].WidgetIds.Length);
    AssertEqual(0, settings.DockedBars[0].WidgetIds.Length);
});

Run("docked-bar recovery preserves an available edge on the preferred monitor", () =>
{
    var placement = DockedBarPlacementRules.FindAvailable(
        [new DockedBarMonitorCandidate("DISPLAY1", true)],
        [new DockedBarOccupiedPlacement("other", "DISPLAY1", "Bottom")],
        excludedDefinitionId: "recovering",
        preferredMonitor: "DISPLAY1",
        preferredEdge: "Top");

    Assert(placement.HasValue);
    var selected = placement.GetValueOrDefault();
    AssertEqual("DISPLAY1", selected.MonitorDeviceName);
    AssertEqual("Top", selected.Edge);
});

Run("docked-bar recovery selects another free edge and isolates exhaustion", () =>
{
    var occupied = new[]
    {
        new DockedBarOccupiedPlacement("top", "DISPLAY1", "Top"),
        new DockedBarOccupiedPlacement("bottom", "DISPLAY1", "Bottom"),
        new DockedBarOccupiedPlacement("left", "DISPLAY1", "Left")
    };
    var placement = DockedBarPlacementRules.FindAvailable(
        [new DockedBarMonitorCandidate("DISPLAY1", true)],
        occupied,
        preferredMonitor: "DISPLAY1",
        preferredEdge: "Top");

    Assert(placement.HasValue);
    AssertEqual("Right", placement.GetValueOrDefault().Edge);

    var exhausted = DockedBarPlacementRules.FindAvailable(
        [new DockedBarMonitorCandidate("DISPLAY1", true)],
        [.. occupied, new DockedBarOccupiedPlacement("right", "DISPLAY1", "Right")],
        preferredMonitor: "DISPLAY1",
        preferredEdge: "Top");
    Assert(!exhausted.HasValue);
});

Run("panel settings preserve valid data while repairing invalid fields and duplicate IDs", () =>
{
    var sharedId = Guid.NewGuid().ToString("N");
    var widgetId = Guid.NewGuid().ToString("N");
    var settings = new DesktopSetup
    {
        Version = 1,
        Overlays =
        [
            new OverlayDefinition
            {
                Id = sharedId,
                Name = "  Prices  ",
                WidgetIds = [widgetId, widgetId, "invalid"],
                WidthDip = 10,
                BackgroundColor = "invalid",
                TextColor = "#abcdef",
                LuminosityOpacity = -5,
                TintOpacity = 500,
                ContentSize = " enormous ",
                Shortcut = " alt + ctrl + h ",
                MonitorDeviceName = "  DISPLAY1  "
            },
            new OverlayDefinition { Name = "Custom second name" }
        ],
        DockedBars =
        [
            new DockedBarDefinition
            {
                Id = sharedId,
                Name = " ",
                Edge = "diagonal",
                ThicknessPx = 900,
                BackgroundColor = "#123456",
                TextColor = "bad",
                ContentSize = " LARGE ",
                Shortcut = "Ctrl+Alt+H"
            }
        ]
    };

    DesktopSetupRules.Normalize(settings);
    AssertEqual(DesktopSetupRules.CurrentVersion, settings.Version);
    AssertEqual(sharedId, settings.Overlays[0].Id);
    AssertEqual("Overlay 1", settings.Overlays[0].Name);
    AssertEqual("Overlay 2", settings.Overlays[1].Name);
    AssertEqual(1, settings.Overlays[0].WidgetIds.Length);
    AssertEqual(widgetId, settings.Overlays[0].WidgetIds[0]);
    AssertEqual(0, settings.Overlays[1].WidgetIds.Length);
    AssertEqual(OverlayLayoutRules.MinimumWidth, settings.Overlays[0].WidthDip);
    AssertEqual("#000000", settings.Overlays[0].BackgroundColor);
    AssertEqual("#ABCDEF", settings.Overlays[0].TextColor);
    AssertEqual(0d, settings.Overlays[0].LuminosityOpacity);
    AssertEqual(100d, settings.Overlays[0].TintOpacity);
    AssertEqual(PanelContentSizes.Standard, settings.Overlays[0].ContentSize);
    AssertEqual("Ctrl+Alt+H", settings.Overlays[0].Shortcut);
    AssertEqual("DISPLAY1", settings.Overlays[0].MonitorDeviceName);
    Assert(Guid.TryParseExact(settings.DockedBars[0].Id, "N", out _));
    Assert(settings.DockedBars[0].Id != sharedId);
    AssertEqual("Panel Top", settings.DockedBars[0].Name);
    AssertEqual("Top", settings.DockedBars[0].Edge);
    AssertEqual(900, settings.DockedBars[0].ThicknessPx);
    AssertEqual("#F5F5F5", settings.DockedBars[0].TextColor);
    AssertEqual(PanelContentSizes.Large, settings.DockedBars[0].ContentSize);
    AssertEqual(string.Empty, settings.DockedBars[0].Shortcut);
});

Run("screen-edge panel names follow position and add a monitor number only when requested", () =>
{
    foreach (var edge in DockedBarLayoutRules.Edges)
    {
        AssertEqual($"Panel {edge}", DesktopSetupRules.GetDockedBarName(edge));
        AssertEqual($"Panel {edge} 2", DesktopSetupRules.GetDockedBarName(edge, 2));
    }
    AssertEqual("Panel Left", DesktopSetupRules.GetDockedBarName("left"));
    AssertEqual("Panel Top", DesktopSetupRules.GetDockedBarName("invalid"));
});

Run("monitor labels extract display numbers without exposing device paths", () =>
{
    AssertEqual(2, DockedBarPlacementRules.GetMonitorNumber(@"\\.\DISPLAY2", 1));
    AssertEqual(12, DockedBarPlacementRules.GetMonitorNumber(@"\\.\display12", 1));
    AssertEqual(3, DockedBarPlacementRules.GetMonitorNumber("unknown", 3));
    AssertEqual(3, DockedBarPlacementRules.GetMonitorNumber("DISPLAY0", 3));
});

Run("position picker excludes other panels but allows its own edge and free monitor edges", () =>
{
    DockedBarOccupiedPlacement[] occupied =
    [
        new("editing", "DISPLAY1", "Top"),
        new("other", "DISPLAY1", "left"),
        new("second", "DISPLAY2", "Top")
    ];
    Assert(!DockedBarPlacementRules.IsOccupied(occupied, "editing", "DISPLAY1", "Top"));
    Assert(DockedBarPlacementRules.IsOccupied(occupied, "editing", "display1", "Left"));
    Assert(DockedBarPlacementRules.IsOccupied(occupied, "editing", "DISPLAY2", "Top"));
    Assert(!DockedBarPlacementRules.IsOccupied(occupied, "editing", "DISPLAY2", "Left"));
    Assert(!DockedBarPlacementRules.IsOccupied(occupied, "editing", "DISPLAY1", "Bottom"));
});

await RunAsync("unreadable or unsupported panel settings need no backup or migration", async () =>
{
    var folder = CreateTemporaryFolder();
    try
    {
        Assert(await DesktopSetupStore.LoadAsync(folder) == null);
        foreach (var input in new[] { "{ invalid", "null", "[]", "{\"Version\":\"invalid\"}", "{\"Version\":6}", "{\"Version\":999}" })
        {
            await File.WriteAllTextAsync(Path.Combine(folder, DesktopSetupStore.SettingsFileName), input);
            Assert(await DesktopSetupStore.LoadAsync(folder) == null);
            AssertEqual(1, Directory.GetFiles(folder).Length);
        }
        await DesktopSetupStore.SaveAsync(folder, new DesktopSetup());
        Assert(await DesktopSetupStore.LoadAsync(folder) != null);
        AssertEqual(1, Directory.GetFiles(folder).Length);
    }
    finally { Directory.Delete(folder, true); }
});

await RunAsync("panel saves keep only the latest setup and retain sizing validation", async () =>
{
    var folder = CreateTemporaryFolder();
    try
    {
        var settings = new DesktopSetup
        {
            Overlays = [new OverlayDefinition { WidthDip = 350, ContentSize = PanelContentSizes.Large }],
            DockedBars = [new DockedBarDefinition { Edge = "Left", ThicknessPx = 9000, ContentSize = PanelContentSizes.Small }]
        };
        await DesktopSetupStore.SaveAsync(folder, settings);
        settings.Overlays[0].WidthDip = 420;
        await DesktopSetupStore.SaveAsync(folder, settings);
        var current = await DesktopSetupStore.LoadAsync(folder)
            ?? throw new InvalidOperationException("Current settings were not readable.");
        AssertEqual(420d, current.Overlays[0].WidthDip);
        AssertEqual(PanelContentSizes.Large, current.Overlays[0].ContentSize);
        AssertEqual("Panel Left", current.DockedBars[0].Name);
        AssertEqual(DockedBarLayoutRules.ClampThickness("Left", 9000), current.DockedBars[0].ThicknessPx);
        AssertEqual(PanelContentSizes.Small, current.DockedBars[0].ContentSize);
        AssertEqual(1, Directory.GetFiles(folder).Length);
        AssertEqual(DesktopSetupStore.SettingsFileName, Path.GetFileName(Directory.GetFiles(folder).Single()));
    }
    finally { Directory.Delete(folder, true); }
});

if (failures.Count == 0)
{
    Console.WriteLine($"All {passed} TrenchHQ logic tests passed.");
    return 0;
}

foreach (var failure in failures)
{
    Console.Error.WriteLine(failure);
}

return 1;

void Run(string name, Action test)
{
    try
    {
        test();
        passed++;
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL: {name}: {exception.Message}");
    }
}

async Task RunAsync(string name, Func<Task> test)
{
    try
    {
        await test();
        passed++;
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL: {name}: {exception.Message}");
    }
}

static string CreateTemporaryFolder()
{
    var path = Path.Combine(Path.GetTempPath(), "TrenchHQ.LogicTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
}

static void Assert(bool condition)
{
    if (!condition)
    {
        throw new InvalidOperationException("Assertion failed.");
    }
}

static string HexWord(ulong value)
{
    return value.ToString("x").PadLeft(64, '0');
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}

static void AssertThrows<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static async Task AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

sealed class CountingHttpMessageHandler(string responseJson) : HttpMessageHandler
{
    private int _requestCount;
    public int RequestCount => Volatile.Read(ref _requestCount);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requestCount);
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
        });
    }
}

sealed class DelayedCountingHttpMessageHandler(string responseJson) : HttpMessageHandler
{
    private readonly TaskCompletionSource _firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _requestCount;

    public Task FirstRequestStarted => _firstRequestStarted.Task;
    public int RequestCount => Volatile.Read(ref _requestCount);
    public void Release() => _release.TrySetResult();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requestCount);
        _firstRequestStarted.TrySetResult();
        await _release.Task.WaitAsync(cancellationToken);
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
        };
    }
}

sealed class SequenceHttpMessageHandler(params System.Net.HttpStatusCode[] statuses) : HttpMessageHandler
{
    private int _requestCount;
    public int RequestCount => Volatile.Read(ref _requestCount);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var index = Interlocked.Increment(ref _requestCount) - 1;
        var status = index < statuses.Length ? statuses[index] : statuses[^1];
        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json")
        });
    }
}

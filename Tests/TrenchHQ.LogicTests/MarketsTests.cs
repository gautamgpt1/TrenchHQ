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
    [Fact(DisplayName = "diagnostic export has an explicit non-identifying schema and no settings or raw errors")]
    public void DiagnosticExportHasAnExplicitNonIdentifyingSchemaAndNoSettingsOrRawErrors()
    {
        var output = ReleaseDiagnostics.Create(new Version(1, 0, 1, 0), OnChainPipelineDiagnostics.Snapshot());
        using var json = JsonDocument.Parse(output);
        var expected = new[] { "SchemaVersion", "Product", "Version", "CreatedUtc", "WindowsVersion", "Architecture",
            "DotnetVersion", "WorkingSetBytes", "ProcessorTimeMilliseconds", "Pipeline" };
        Assert(expected.SequenceEqual(json.RootElement.EnumerateObject().Select(p => p.Name)));
        AssertEqual("1.0.1.0", json.RootElement.GetProperty("Version").GetString());
        Assert(json.RootElement.GetProperty("Pipeline").EnumerateObject().All(p => p.Value.ValueKind == JsonValueKind.Number));
        Assert(!output.Contains(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase));
    }

    [Fact(DisplayName = "pair parser accepts certified display names")]
    public void PairParserAcceptsCertifiedDisplayNames()
    {
        Assert(SelectedPairParser.TryParse(" BTC/USDT - Binance ", out var symbol, out var exchangeId, out var display));
        AssertEqual("BTC/USDT", symbol);
        AssertEqual("binance", exchangeId);
        AssertEqual("BTC/USDT", display);
    }

    [Fact(DisplayName = "pair parser rejects malformed and unsupported pairs")]
    public void PairParserRejectsMalformedAndUnsupportedPairs()
    {
        Assert(!SelectedPairParser.TryParse("BTC/USDT", out _, out _, out _));
        Assert(!SelectedPairParser.TryParse("BTC/USDT - Unknown Exchange", out _, out _, out _));
    }

    [Fact(DisplayName = "centralized pair icons resolve exact packaged base assets")]
    public void CentralizedPairIconsResolveExactPackagedBaseAssets()
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
    }

    [Fact(DisplayName = "normalized feed protocol parses a versioned price update")]
    public void NormalizedFeedProtocolParsesAVersionedPriceUpdate()
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
    }

    [Fact(DisplayName = "normalized feed protocol rejects incompatible or malformed updates")]
    public void NormalizedFeedProtocolRejectsIncompatibleOrMalformedUpdates()
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
    }

    [Fact(DisplayName = "subscription planner deduplicates exchange and symbol case-insensitively")]
    public void SubscriptionPlannerDeduplicatesExchangeAndSymbolCaseInsensitively()
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
    }

    [Fact(DisplayName = "market reconnect backoff grows and caps at fifteen seconds")]
    public void MarketReconnectBackoffGrowsAndCapsAtFifteenSeconds()
    {
        AssertEqual(TimeSpan.FromSeconds(1), MarketReconnectRules.GetDelay(0));
        AssertEqual(TimeSpan.FromSeconds(2), MarketReconnectRules.GetDelay(1));
        AssertEqual(TimeSpan.FromSeconds(15), MarketReconnectRules.GetDelay(4));
        AssertEqual(TimeSpan.FromSeconds(15), MarketReconnectRules.GetDelay(20));
    }

    [Fact(DisplayName = "price formatting is deterministic and bounded for extreme values")]
    public void PriceFormattingIsDeterministicAndBoundedForExtremeValues()
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
    }

    [Fact(DisplayName = "price tick state handles first, up, down, equal, and disabled updates")]
    public void PriceTickStateHandlesFirstUpDownEqualAndDisabledUpdates()
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
    }

    [Fact(DisplayName = "application pin containment accepts smaller visible frames but rejects spillover")]
    public void ApplicationPinContainmentAcceptsSmallerVisibleFramesButRejectsSpillover()
    {
        var panel = new WindowPinBounds(-1920, 100, 360, 240);
        Assert(panel.Contains(panel));
        Assert(panel.Contains(new WindowPinBounds(-1912, 100, 344, 232)));
        Assert(!panel.Contains(new WindowPinBounds(-1921, 100, 360, 240)));
        Assert(!panel.Contains(new WindowPinBounds(-1920, 99, 360, 240)));
        Assert(!panel.Contains(new WindowPinBounds(-1920, 100, 361, 240)));
        Assert(!panel.Contains(new WindowPinBounds(-1920, 100, 360, 241)));
        Assert(!panel.Contains(new WindowPinBounds(-1920, 100, 0, 240)));
    }

    [Fact(DisplayName = "price ticker automatic thickness keeps the desktop safety cap")]
    public void PriceTickerAutomaticThicknessKeepsTheDesktopSafetyCap()
    {
        AssertEqual(324, DockedBarLayoutRules.GetAutomaticPriceTickerThickness("Top", 100, 840, 1, 1080, 1));
        AssertEqual(576, DockedBarLayoutRules.GetAutomaticPriceTickerThickness("Left", 1000, 1040, 1, 1920, 1));
        AssertEqual(72, DockedBarLayoutRules.GetAutomaticPriceTickerThickness("Top", 0, 840, 1, 1080, 2));
        AssertEqual(192, DockedBarLayoutRules.GetAutomaticPriceTickerThickness("Right", 0, 1040, 2, 1920, 1));
    }

    [Fact(DisplayName = "price mode defaults to one-second polling and shared WebSocket demand wins")]
    public void PriceModeDefaultsToOneSecondPollingAndSharedWebsocketDemandWins()
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
    }
}

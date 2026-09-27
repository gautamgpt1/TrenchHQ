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
    [Fact(DisplayName = "widget content state distinguishes empty, loading, live, stale, unavailable, and error")]
    public void WidgetContentStateDistinguishesEmptyLoadingLiveStaleUnavailableAndError()
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
    }

    [Fact(DisplayName = "saved widget catalog normalizes identifiers, labels, types, and instruments")]
    public void SavedWidgetCatalogNormalizesIdentifiersLabelsTypesAndInstruments()
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
    }

    [Fact(DisplayName = "X tracker accepts only public account and List timelines")]
    public void XTrackerAcceptsOnlyPublicAccountAndListTimelines()
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
    }

    [Fact(DisplayName = "Website shared runtime profiles isolate widgets and preserve legacy browser storage")]
    public void WebsiteSharedRuntimeProfilesIsolateWidgetsAndPreserveLegacyBrowserStorage()
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
    }

    [Fact(DisplayName = "Website URLs reject scripts, files, HTTP and embedded credentials")]
    public void WebsiteUrlsRejectScriptsFilesHttpAndEmbeddedCredentials()
    {
        Assert(WebsiteWidgetRules.TryUrl(" https://j7tracker.io ", out var url));
        AssertEqual("https://j7tracker.io/", url);
        foreach (var value in new[] { "http://example.com", "javascript:alert(1)", "file:///C:/test", "data:text/html,test", "https://user:pass@example.com", "", "not a URL" })
            Assert(!WebsiteWidgetRules.TryUrl(value, out _));
    }

    [Fact(DisplayName = "Website editor accepts ordinary addresses and empty drafts without a loading switch")]
    public void WebsiteEditorAcceptsOrdinaryAddressesAndEmptyDraftsWithoutALoadingSwitch()
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
    }

    [Fact(DisplayName = "Website catalog normalization bounds presentation and preserves existing X watches")]
    public void WebsiteCatalogNormalizationBoundsPresentationAndPreservesExistingXWatches()
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
    }

    [Fact(DisplayName = "saved widget catalog round-trips and preserves the previous valid file")]
    public async Task SavedWidgetCatalogRoundTripsAndPreservesThePreviousValidFile()
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
    }

    [Fact(DisplayName = "saved widget catalog serializes overlapping atomic saves")]
    public async Task SavedWidgetCatalogSerializesOverlappingAtomicSaves()
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
    }

    [Fact(DisplayName = "version one widget catalogs migrate to typed centralized instruments")]
    public async Task VersionOneWidgetCatalogsMigrateToTypedCentralizedInstruments()
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
    }

    [Fact(DisplayName = "version three price ticker catalogs migrate without data loss")]
    public async Task VersionThreePriceTickerCatalogsMigrateWithoutDataLoss()
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
    }

    [Fact(DisplayName = "version six X watches migrate and Website settings round-trip independently")]
    public async Task VersionSixXWatchesMigrateAndWebsiteSettingsRoundTripIndependently()
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
    }

    [Fact(DisplayName = "unsupported or malformed saved widget catalogs preserve exact rejected bytes")]
    public async Task UnsupportedOrMalformedSavedWidgetCatalogsPreserveExactRejectedBytes()
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
    }

    [Fact(DisplayName = "application window content fits both dimensions and excludes widget subscriptions")]
    public void ApplicationWindowContentFitsBothDimensionsAndExcludesWidgetSubscriptions()
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
    }
}

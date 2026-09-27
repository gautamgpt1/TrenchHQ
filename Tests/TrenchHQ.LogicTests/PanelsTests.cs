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
    [Fact(DisplayName = "shortcut capture previews modifiers and keeps the last chord after release")]
    public void ShortcutCapturePreviewsModifiersAndKeepsTheLastChordAfterRelease()
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
    }

    [Fact(DisplayName = "shortcut capture displays invalid, reserved and multiple-key input without accepting it")]
    public void ShortcutCaptureDisplaysInvalidReservedAndMultipleKeyInputWithoutAcceptingIt()
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
    }

    [Fact(DisplayName = "shortcut reset and clear affect only the capture candidate")]
    public void ShortcutResetAndClearAffectOnlyTheCaptureCandidate()
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
    }

    [Fact(DisplayName = "overlay width is clamped to product bounds")]
    public void OverlayWidthIsClampedToProductBounds()
    {
        AssertEqual(OverlayLayoutRules.MinimumWidth, OverlayLayoutRules.ClampWidth(100));
        AssertEqual(420d, OverlayLayoutRules.ClampWidth(420));
        AssertEqual(OverlayLayoutRules.MaximumWidth, OverlayLayoutRules.ClampWidth(900));
        AssertEqual(OverlayLayoutRules.DefaultWidth, OverlayLayoutRules.ClampWidth(double.NaN));
        AssertEqual(OverlayLayoutRules.DefaultWidth, OverlayLayoutRules.ClampWidth(double.PositiveInfinity));
    }

    [Fact(DisplayName = "new panel definitions use neutral appearance defaults")]
    public void NewPanelDefinitionsUseNeutralAppearanceDefaults()
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
    }

    [Fact(DisplayName = "panel shortcuts normalize supported keys and reject unsafe input")]
    public void PanelShortcutsNormalizeSupportedKeysAndRejectUnsafeInput()
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
    }

    [Fact(DisplayName = "Website memory policy keeps visible panels live and reduces hidden or minimized views")]
    public void WebsiteMemoryPolicyKeepsVisiblePanelsLiveAndReducesHiddenOrMinimizedViews()
    {
        foreach (var hostVisible in new[] { true, false })
        foreach (var minimized in new[] { true, false })
        foreach (var widgetVisible in new[] { true, false })
            AssertEqual(!(hostVisible && !minimized && widgetVisible),
                WebsiteWidgetRules.UseLowMemory(hostVisible, minimized, widgetVisible));
        // Restore immediately returns to normal; neither focus nor interaction lock is an input.
        Assert(WebsiteWidgetRules.UseLowMemory(true, true, true));
        Assert(!WebsiteWidgetRules.UseLowMemory(true, false, true));
    }

    [Fact(DisplayName = "docked websites require usable page space in both dimensions")]
    public void DockedWebsitesRequireUsablePageSpaceInBothDimensions()
    {
        Assert(WebsiteWidgetRules.CanLoadInViewport(320, 200));
        Assert(WebsiteWidgetRules.CanLoadInViewport(1920, 400));
        Assert(WebsiteWidgetRules.CanLoadInViewport(400, 1000));
        Assert(!WebsiteWidgetRules.CanLoadInViewport(319.9, 1000));
        Assert(!WebsiteWidgetRules.CanLoadInViewport(1920, 199.9));
        Assert(!WebsiteWidgetRules.CanLoadInViewport(0, 0));
        Assert(!WebsiteWidgetRules.CanLoadInViewport(double.NaN, 400));
        Assert(!WebsiteWidgetRules.CanLoadInViewport(400, double.PositiveInfinity));
    }

    [Fact(DisplayName = "website panel minimum includes viewport and controls without reserving status text space")]
    public void WebsitePanelMinimumIncludesViewportAndControlsWithoutReservingStatusTextSpace()
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
    }

    [Fact(DisplayName = "website minimum never authorizes exceeding the existing thirty-percent panel cap")]
    public void WebsiteMinimumNeverAuthorizesExceedingTheExistingThirtyPercentPanelCap()
    {
        var required = WebsiteWidgetRules.MinimumDockedSize(1);
        var smallMaximum = DockedBarLayoutRules.GetMaximumThickness(768);
        Assert(required.Height > smallMaximum);
        var largeMaximum = DockedBarLayoutRules.GetMaximumThickness(1080);
        Assert(required.Height <= largeMaximum);
        AssertEqual(230, Math.Min(required.Height, smallMaximum));
        AssertEqual(246, Math.Min(required.Height, largeMaximum));
    }

    [Fact(DisplayName = "application panel fixed minimum includes window area and controls at monitor scale")]
    public void ApplicationPanelFixedMinimumIncludesWindowAreaAndControlsAtMonitorScale()
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
    }

    [Fact(DisplayName = "fixed-height overlays bound height and stay smaller than the available work area")]
    public void FixedHeightOverlaysBoundHeightAndStaySmallerThanTheAvailableWorkArea()
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
    }

    [Fact(DisplayName = "overlay height is adjustable for feeds websites and apps but never the ticker")]
    public void OverlayHeightIsAdjustableForFeedsWebsitesAndAppsButNeverTheTicker()
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
    }

    [Fact(DisplayName = "height is saved independently per overlay without widget-owned sizing")]
    public async Task HeightIsSavedIndependentlyPerOverlayWithoutWidgetOwnedSizing()
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
    }

    [Fact(DisplayName = "application overlays exclude widget subscriptions and cannot gain a fallback widget")]
    public void ApplicationOverlaysExcludeWidgetSubscriptionsAndCannotGainAFallbackWidget()
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
    }

    [Fact(DisplayName = "application window panel mode round-trips without persisting foreign window handles")]
    public async Task ApplicationWindowPanelModeRoundTripsWithoutPersistingForeignWindowHandles()
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
    }

    [Fact(DisplayName = "docked-bar layout rules enforce orientation-specific thickness")]
    public void DockedBarLayoutRulesEnforceOrientationSpecificThickness()
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
    }

    [Fact(DisplayName = "docked-bar runtime sizing respects monitor caps and display scaling")]
    public void DockedBarRuntimeSizingRespectsMonitorCapsAndDisplayScaling()
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
    }

    [Fact(DisplayName = "price ticker docked bars wrap into automatic rows and columns")]
    public void PriceTickerDockedBarsWrapIntoAutomaticRowsAndColumns()
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
    }

    [Fact(DisplayName = "panel content size normalizes and preserves per-panel scale")]
    public void PanelContentSizeNormalizesAndPreservesPerPanelScale()
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
    }

    [Fact(DisplayName = "opposite maximum docked bars leave at least forty percent of each monitor dimension")]
    public void OppositeMaximumDockedBarsLeaveAtLeastFortyPercentOfEachMonitorDimension()
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
    }

    [Fact(DisplayName = "subscription planner deduplicates pairs across overlays and docked bars")]
    public void SubscriptionPlannerDeduplicatesPairsAcrossOverlaysAndDockedBars()
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
    }

    [Fact(DisplayName = "three overlays retain independent widgets while sharing upstream subscriptions")]
    public void ThreeOverlaysRetainIndependentWidgetsWhileSharingUpstreamSubscriptions()
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
    }

    [Fact(DisplayName = "panel display content requires an application window or valid widget")]
    public void PanelDisplayContentRequiresAnApplicationWindowOrValidWidget()
    {
        var widget = new SavedWidgetDefinition { Id = Guid.NewGuid().ToString("N") };
        Assert(!DesktopSetupRules.HasDisplayContent(false, [], [widget]));
        Assert(!DesktopSetupRules.HasDisplayContent(false, [Guid.NewGuid().ToString("N")], [widget]));
        Assert(DesktopSetupRules.HasDisplayContent(false, [widget.Id], [widget]));
        Assert(DesktopSetupRules.HasDisplayContent(true, [], []));
    }

    [Fact(DisplayName = "widget reference reconciliation preserves unassigned panels without fallback")]
    public void WidgetReferenceReconciliationPreservesUnassignedPanelsWithoutFallback()
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
    }

    [Fact(DisplayName = "docked-bar recovery preserves an available edge on the preferred monitor")]
    public void DockedBarRecoveryPreservesAnAvailableEdgeOnThePreferredMonitor()
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
    }

    [Fact(DisplayName = "docked-bar recovery selects another free edge and isolates exhaustion")]
    public void DockedBarRecoverySelectsAnotherFreeEdgeAndIsolatesExhaustion()
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
    }

    [Fact(DisplayName = "panel settings preserve valid data while repairing invalid fields and duplicate IDs")]
    public void PanelSettingsPreserveValidDataWhileRepairingInvalidFieldsAndDuplicateIds()
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
    }

    [Fact(DisplayName = "screen-edge panel names follow position and add a monitor number only when requested")]
    public void ScreenEdgePanelNamesFollowPositionAndAddAMonitorNumberOnlyWhenRequested()
    {
        foreach (var edge in DockedBarLayoutRules.Edges)
        {
            AssertEqual($"Panel {edge}", DesktopSetupRules.GetDockedBarName(edge));
            AssertEqual($"Panel {edge} 2", DesktopSetupRules.GetDockedBarName(edge, 2));
        }
        AssertEqual("Panel Left", DesktopSetupRules.GetDockedBarName("left"));
        AssertEqual("Panel Top", DesktopSetupRules.GetDockedBarName("invalid"));
    }

    [Fact(DisplayName = "monitor labels extract display numbers without exposing device paths")]
    public void MonitorLabelsExtractDisplayNumbersWithoutExposingDevicePaths()
    {
        AssertEqual(2, DockedBarPlacementRules.GetMonitorNumber(@"\\.\DISPLAY2", 1));
        AssertEqual(12, DockedBarPlacementRules.GetMonitorNumber(@"\\.\display12", 1));
        AssertEqual(3, DockedBarPlacementRules.GetMonitorNumber("unknown", 3));
        AssertEqual(3, DockedBarPlacementRules.GetMonitorNumber("DISPLAY0", 3));
    }

    [Fact(DisplayName = "position picker excludes other panels but allows its own edge and free monitor edges")]
    public void PositionPickerExcludesOtherPanelsButAllowsItsOwnEdgeAndFreeMonitorEdges()
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
    }

    [Fact(DisplayName = "unreadable or unsupported panel settings need no backup or migration")]
    public async Task UnreadableOrUnsupportedPanelSettingsNeedNoBackupOrMigration()
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
    }

    [Fact(DisplayName = "panel saves keep only the latest setup and retain sizing validation")]
    public async Task PanelSavesKeepOnlyTheLatestSetupAndRetainSizingValidation()
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
    }
}

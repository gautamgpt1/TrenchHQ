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
    [Fact(DisplayName = "provider failure thresholds survive offline rejection and reset after sustained health")]
    public void ProviderFailureThresholdsSurviveOfflineRejectionAndResetAfterSustainedHealth()
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
    }

    [Fact(DisplayName = "machine-wide outages do not exhaust the approved provider route")]
    public void MachineWideOutagesDoNotExhaustTheApprovedProviderRoute()
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
    }

    [Fact(DisplayName = "on-chain planner preserves exact pool identity independently of provider choice")]
    public void OnChainPlannerPreservesExactPoolIdentityIndependentlyOfProviderChoice()
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
    }
}

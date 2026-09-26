using System;

namespace TrenchHQ.Helpers
{
    internal enum WidgetContentState
    {
        Loading,
        Empty,
        Live,
        Stale,
        Unavailable,
        Error
    }

    internal readonly record struct WidgetContentStatus(WidgetContentState State, string Label);

    internal static class WidgetContentStateRules
    {
        internal static readonly TimeSpan StaleTimeout = TimeSpan.FromSeconds(15);

        internal static WidgetContentStatus EvaluatePriceTicker(
            MarketSidecarState sidecarState,
            bool hasInstruments,
            bool hasReceivedPrice,
            bool hasStreamError,
            DateTimeOffset waitingSince,
            DateTimeOffset lastPriceAt,
            DateTimeOffset now)
        {
            if (!hasInstruments)
            {
                return new WidgetContentStatus(WidgetContentState.Empty, "No instruments");
            }

            if (hasStreamError)
            {
                return new WidgetContentStatus(WidgetContentState.Error, "Error");
            }

            if (sidecarState == MarketSidecarState.Reconnecting)
            {
                return new WidgetContentStatus(WidgetContentState.Loading, "Reconnecting");
            }

            if (sidecarState is MarketSidecarState.Stopped or MarketSidecarState.Unavailable)
            {
                return new WidgetContentStatus(WidgetContentState.Unavailable, "Unavailable");
            }

            if (sidecarState == MarketSidecarState.Connecting)
            {
                return new WidgetContentStatus(WidgetContentState.Loading, "Connecting");
            }

            if (!hasReceivedPrice)
            {
                return now - waitingSince >= StaleTimeout
                    ? new WidgetContentStatus(WidgetContentState.Unavailable, "Unavailable")
                    : new WidgetContentStatus(WidgetContentState.Loading, "Connecting");
            }

            return now - lastPriceAt >= StaleTimeout
                ? new WidgetContentStatus(WidgetContentState.Stale, "Stale")
                : new WidgetContentStatus(WidgetContentState.Live, "Live");
        }
    }
}

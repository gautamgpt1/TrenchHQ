using TrenchHQ.Core.OnChain;
using System;

namespace TrenchHQ.Core.Widgets
{
    internal static class PanelWidgetTypes
    {
        internal const string PriceTicker = "priceTicker";
        internal const string WalletActivity = "walletActivity";
        internal const string XTimeline = "xTimeline";
        internal const string Website = "website";
    }

    internal static class TickerInstrumentTypes
    {
        internal const string CentralizedMarket = "centralizedMarket";
        internal const string OnChainPool = "onChainPool";
    }

    internal static class OnChainPriceModes
    {
        internal const string OneSecondPolling = "oneSecondPolling";
        internal const string WebSocket = "webSocket";

        internal static string Normalize(string? mode) =>
            string.Equals(mode, WebSocket, StringComparison.Ordinal)
                ? WebSocket
                : OneSecondPolling;

        internal static bool SupportsPolling(OnChainPoolDescriptor pool) =>
            pool.PoolKey.ProtocolId is not (OnChainProtocolIds.ManifestOrderbook
                or OnChainProtocolIds.Curve or OnChainProtocolIds.FermiSwap);
    }

    internal sealed class SavedWidgetCatalog
    {
        public int Version { get; set; } = 7;
        public SavedWidgetDefinition[] Widgets { get; set; } = [];
    }

    internal sealed class SavedWidgetDefinition
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Price Ticker";
        public string Type { get; set; } = PanelWidgetTypes.PriceTicker;
        public SavedTickerInstrument[] Instruments { get; set; } = [];
        public string OnChainPriceMode { get; set; } = OnChainPriceModes.OneSecondPolling;
        public SavedTrackedWallet[] Wallets { get; set; } = [];
        public string WebsiteUrl { get; set; } = string.Empty;
        public bool WebsiteEnabled { get; set; }
        public double WebsiteZoom { get; set; } = 1;
        public string? XTimelineUrl { get; set; }
        public bool XExternalContentConsent { get; set; }
        public string[] XHandles { get; set; } = [];
        public string XTextFilter { get; set; } = string.Empty;
        public bool XIncludePosts { get; set; } = true;
        public bool XIncludeReplies { get; set; } = true;
        public bool XIncludeReposts { get; set; } = true;
        public bool XIncludeQuotes { get; set; } = true;
    }

    internal sealed class SavedTrackedWallet
    {
        public string ChainNamespace { get; set; } = ChainNamespaces.Solana;
        public string ChainId { get; set; } = "mainnet-beta";
        public string Address { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
    }

    internal sealed class SavedTickerInstrument
    {
        public string Kind { get; set; } = TickerInstrumentTypes.CentralizedMarket;
        public string DisplayLabel { get; set; } = string.Empty;
        public string? VenueId { get; set; }
        public string? Symbol { get; set; }
        public string? SelectedMint { get; set; }
        public string? AssetName { get; set; }
        public string? AssetSymbol { get; set; }
        public string? QuoteSymbol { get; set; }
        public string? IconUri { get; set; }
        public OnChainPoolDescriptor? Pool { get; set; }
    }
}

namespace TrenchHQ.Core.OnChain
{
    internal sealed class PoolCatalogAsset
    {
        public string Address { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
    }

    internal sealed class PoolCatalogEntry
    {
        public string SourceId { get; set; } = string.Empty;
        public string ChainId { get; set; } = string.Empty;
        public string ProtocolId { get; set; } = string.Empty;
        public string PoolAddress { get; set; } = string.Empty;
        public string[] Labels { get; set; } = [];
        public PoolCatalogAsset BaseAsset { get; set; } = new();
        public PoolCatalogAsset QuoteAsset { get; set; } = new();
        public double? LiquidityUsd { get; set; }
        public double? Volume24hUsd { get; set; }
        public double? MarketCapUsd { get; set; }
        public double? FullyDilutedValuationUsd { get; set; }
        public long? CreatedAtUnixMs { get; set; }
        public string? IconUri { get; set; }
        public uint? BaseTokenIndex { get; set; }
        public uint? QuoteTokenIndex { get; set; }
    }

    internal sealed class PoolCatalogSearchResult
    {
        public string SourceId { get; set; } = string.Empty;
        public string ChainId { get; set; } = string.Empty;
        public string AssetAddress { get; set; } = string.Empty;
        public string AssetName { get; set; } = string.Empty;
        public string AssetSymbol { get; set; } = string.Empty;
        public string? IconUri { get; set; }
        public PoolCatalogEntry[] Pools { get; set; } = [];
    }

    internal sealed class TokenCatalogSearchEntry
    {
        public string ChainId { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public string? IconUri { get; set; }
        public double? LiquidityUsd { get; set; }
        public double? MarketCapUsd { get; set; }
        public PoolCatalogEntry[] Pools { get; set; } = [];
    }

    internal sealed class TokenCatalogSearchResult
    {
        public string Query { get; set; } = string.Empty;
        public TokenCatalogSearchEntry[] Tokens { get; set; } = [];
    }
}

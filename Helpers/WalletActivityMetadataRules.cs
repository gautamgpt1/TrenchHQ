using TrenchHQ.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TrenchHQ.Helpers
{
    internal sealed record WalletActivityMarketMetadata(
        string? VenueId,
        string? TokenIconUri,
        double? MarketCapUsd);

    internal static class WalletActivityMetadataRules
    {
        internal static string? ToDexScreenerChainId(string chainId) => chainId switch
        {
            "mainnet-beta" => "solana",
            EvmChainDefinitions.EthereumMainnetChainId => "ethereum",
            EvmChainDefinitions.BaseMainnetChainId => "base",
            EvmChainDefinitions.BnbMainnetChainId => "bsc",
            EvmChainDefinitions.RobinhoodMainnetChainId => "robinhood",
            _ => null
        };

        internal static WalletActivityMarketMetadata Resolve(
            IReadOnlyCollection<WalletActivityUpdate> updates,
            string marketAssetAddress,
            PoolCatalogSearchResult catalog)
        {
            ArgumentNullException.ThrowIfNull(updates);
            ArgumentException.ThrowIfNullOrWhiteSpace(marketAssetAddress);
            ArgumentNullException.ThrowIfNull(catalog);

            var pools = catalog.Pools
                .Where(pool => string.Equals(
                                   pool.BaseAsset.Address,
                                   marketAssetAddress,
                                   StringComparison.OrdinalIgnoreCase)
                               || string.Equals(
                                   pool.QuoteAsset.Address,
                                   marketAssetAddress,
                                   StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var counterparties = updates
                .Select(static update => update.Counterparty)
                .Where(static address => !string.IsNullOrWhiteSpace(address))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var matchedPool = pools.FirstOrDefault(pool => counterparties.Contains(pool.PoolAddress));
            var venueId = updates
                              .Select(static update => update.VenueId)
                              .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
                          ?? matchedPool?.ProtocolId;
            var tokenIcon = pools
                .Where(pool => string.Equals(
                    pool.BaseAsset.Address,
                    marketAssetAddress,
                    StringComparison.OrdinalIgnoreCase))
                .Select(static pool => pool.IconUri)
                .FirstOrDefault(static uri => !string.IsNullOrWhiteSpace(uri));
            var marketCap = pools
                .Where(static pool => pool.MarketCapUsd.HasValue)
                .OrderByDescending(static pool => pool.LiquidityUsd ?? double.MinValue)
                .Select(static pool => pool.MarketCapUsd)
                .FirstOrDefault();
            return new WalletActivityMarketMetadata(venueId, tokenIcon, marketCap);
        }

        internal static string FormatUsd(double value) => value switch
        {
            >= 1_000_000_000 => $"${value / 1_000_000_000:0.##}B",
            >= 1_000_000 => $"${value / 1_000_000:0.##}M",
            >= 1_000 => $"${value / 1_000:0.##}K",
            _ => $"${value:0.##}"
        };
    }
}

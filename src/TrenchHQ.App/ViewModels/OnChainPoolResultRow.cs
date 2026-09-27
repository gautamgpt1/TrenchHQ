using TrenchHQ.Core.OnChain;
using TrenchHQ.Core.OnChain.Evm;
using TrenchHQ.Core.OnChain.Solana;
using TrenchHQ.Core.Providers;
using TrenchHQ.Core.Social;
using TrenchHQ.Core.Widgets;
using TrenchHQ.Infrastructure.Markets;
using TrenchHQ.Infrastructure.OnChain;
using TrenchHQ.Infrastructure.OnChain.Evm;
using TrenchHQ.Infrastructure.OnChain.Solana;
using TrenchHQ.Infrastructure.Providers;
using TrenchHQ.Infrastructure.Widgets;
using TrenchHQ.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Windows.System;

namespace TrenchHQ.ViewModels
{
    internal sealed class OnChainPoolResultRow : INotifyPropertyChanged
    {
        private bool _isSelected;

        internal OnChainPoolResultRow(
            OnChainPoolDescriptor descriptor,
            string selectedMint,
            PoolCatalogEntry? catalogPool = null,
            PoolCatalogSearchResult? catalogResult = null)
        {
            Descriptor = descriptor;
            SelectedMint = selectedMint;
            CatalogPool = catalogPool;
            var selectedAsset = GetSelectedAsset(catalogPool, selectedMint);
            AssetName = !string.IsNullOrWhiteSpace(selectedAsset?.Name)
                ? selectedAsset.Name
                : catalogResult?.AssetName ?? string.Empty;
            AssetSymbol = !string.IsNullOrWhiteSpace(selectedAsset?.Symbol)
                ? selectedAsset.Symbol
                : !string.IsNullOrWhiteSpace(catalogResult?.AssetSymbol)
                    ? catalogResult.AssetSymbol
                : Short(selectedMint);
            IconUri = catalogPool?.IconUri ?? catalogResult?.IconUri;
            Icon = Uri.TryCreate(IconUri, UriKind.Absolute, out var iconUri)
                   && iconUri.Scheme == Uri.UriSchemeHttps
                ? new BitmapImage(iconUri)
                : null;
            QuoteSymbol = GetQuoteSymbol(descriptor, selectedMint, catalogPool);
            ChainIcon = CreateChainIcon(descriptor.PoolKey.ChainId);
            VenueIcon = null;
        }

        internal OnChainPoolDescriptor Descriptor { get; }
        internal string SelectedMint { get; }
        internal PoolCatalogEntry? CatalogPool { get; }
        internal string Key => SavedWidgetCatalogRules.GetInstrumentKey(new SavedTickerInstrument
        {
            Kind = TickerInstrumentTypes.OnChainPool,
            Pool = Descriptor
        });
        internal double? LiquidityUsd => CatalogPool?.LiquidityUsd;
        public string AssetName { get; }
        public string AssetSymbol { get; }
        public string AssetDisplayName => string.IsNullOrWhiteSpace(AssetName) ? AssetSymbol : AssetName;
        public string QuoteSymbol { get; }
        public string? IconUri { get; }
        public BitmapImage? Icon { get; }
        public SvgImageSource ChainIcon { get; }
        public ImageSource? VenueIcon { get; }
        public string SelectionDisplayLabel => $"{AssetSymbol} / {QuoteSymbol}";
        public string Title => ProtocolLabel(CatalogPool, Descriptor);
        public string TokenAddressText => Short(SelectedMint);
        public string MarketCap => FormatUsd(
            CatalogPool?.MarketCapUsd ?? CatalogPool?.FullyDilutedValuationUsd,
            "--");
        public string Liquidity => FormatUsd(CatalogPool?.LiquidityUsd, "--");
        public string Volume24h => FormatUsd(CatalogPool?.Volume24hUsd, "--");
        public string Age => FormatAge(CatalogPool?.CreatedAtUnixMs);
        public string Status => Descriptor.SupportStatus switch
        {
            OnChainSupportStatus.Supported => string.Empty,
            OnChainSupportStatus.TemporarilyUnavailable => Descriptor.SupportReason ?? string.Empty,
            _ => Descriptor.SupportReason ?? $"This {Title} pool is not supported for on-chain pricing yet."
        };
        public Visibility StatusVisibility => string.IsNullOrWhiteSpace(Status)
            ? Visibility.Collapsed
            : Visibility.Visible;
        public bool IsSelectable => Descriptor.SupportStatus == OnChainSupportStatus.Supported;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private static string QuoteLabel(string mint, string chainId)
        {
            if (chainId == EvmChainDefinitions.RobinhoodMainnetChainId
                && RobinhoodStockTokenCatalogClient.Current.TryGetCachedByAddress(mint, out var stockToken))
            {
                return stockToken.Symbol;
            }
            return mint.ToLowerInvariant() switch
            {
                "so11111111111111111111111111111111111111112" => "SOL",
                "0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2" => "ETH",
                "0x4200000000000000000000000000000000000006" => "ETH",
                "0x0bd7d308f8e1639fab988df18a8011f41eacad73" => "WETH",
                "0x0000000000000000000000000000000000000000" =>
                    chainId == EvmChainDefinitions.BnbMainnetChainId ? "BNB" : "ETH",
                "0xbb4cdb9cbd36b01bd1cbaebf2de08d9173bc095c" => "WBNB",
                "0xa0b86991c6218b36c1d19d4a2e9eb0ce3606eb48" => "USDC",
                "0x833589fcd6edb6e08f4c7c32d4f71b54bda02913" => "USDC",
                "0x8ac76a51cc950d9822d68b83fe1ad97b32cd580d" => "USDC",
                "0xdac17f958d2ee523a2206206994597c13d831ec7" => "USDT",
                "0x55d398326f99059ff775485246999027b3197955" => "USDT",
                "0x5fc5360d0400a0fd4f2af552add042d716f1d168" => "USDG",
                "0x6b175474e89094c44da98b954eedeac495271d0f" => "DAI",
                _ => Short(mint)
            };
        }

        private static string GetQuoteSymbol(
            OnChainPoolDescriptor descriptor,
            string selectedMint,
            PoolCatalogEntry? catalogPool)
        {
            if (catalogPool != null)
            {
                if (string.Equals(catalogPool.BaseAsset.Address, selectedMint, StringComparison.OrdinalIgnoreCase))
                {
                    return AssetLabel(catalogPool.QuoteAsset);
                }
                if (string.Equals(catalogPool.QuoteAsset.Address, selectedMint, StringComparison.OrdinalIgnoreCase))
                {
                    return AssetLabel(catalogPool.BaseAsset);
                }
            }
            var otherMint = string.Equals(descriptor.BaseMint, selectedMint, StringComparison.OrdinalIgnoreCase)
                ? descriptor.QuoteMint
                : descriptor.BaseMint;
            return QuoteLabel(otherMint, descriptor.PoolKey.ChainId);
        }

        private static string AssetLabel(PoolCatalogAsset asset)
        {
            return !string.IsNullOrWhiteSpace(asset.Symbol) ? asset.Symbol : Short(asset.Address);
        }

        private static PoolCatalogAsset? GetSelectedAsset(PoolCatalogEntry? pool, string selectedMint)
        {
            if (pool == null)
            {
                return null;
            }
            if (string.Equals(pool.BaseAsset.Address, selectedMint, StringComparison.OrdinalIgnoreCase))
            {
                return pool.BaseAsset;
            }
            return string.Equals(pool.QuoteAsset.Address, selectedMint, StringComparison.OrdinalIgnoreCase)
                ? pool.QuoteAsset
                : null;
        }

        private static SvgImageSource CreateChainIcon(string chainId)
        {
            var path = chainId switch
            {
                EvmChainDefinitions.BaseMainnetChainId => "ms-appx:///Assets/Chains/base.svg",
                EvmChainDefinitions.BnbMainnetChainId => "ms-appx:///Assets/Chains/bnb-chain.svg",
                EvmChainDefinitions.RobinhoodMainnetChainId => "ms-appx:///Assets/Chains/robinhood-chain.svg",
                "mainnet-beta" => "ms-appx:///Assets/Chains/solana.svg",
                _ => "ms-appx:///Assets/Chains/ethereum.svg"
            };
            return new SvgImageSource { UriSource = new Uri(path) };
        }

        internal static string ProtocolLabel(string protocolId)
        {
            return protocolId.ToLowerInvariant() switch
            {
                "pumpswap" => "PumpSwap",
                "pumpbondingcurve" => "Pump bonding curve",
                "raydium" => "Raydium",
                "raydiumammv4" => "Raydium AMM v4",
                "raydiumcpmm" => "Raydium CPMM",
                "raydiumclmm" => "Raydium CLMM",
                "meteora" => "Meteora",
                "meteoradammv1" => "Meteora DAMM v1",
                "meteoradammv2" => "Meteora DAMM v2",
                "meteoradlmm" => "Meteora DLMM",
                "orca" => "Orca",
                "orcawhirlpool" => "Orca Whirlpool",
                "manifest" => "Manifest",
                "manifestorderbook" => "Manifest order book",
                "uniswap-v2" => "Uniswap v2",
                "pancake-v2" => "PancakeSwap v2",
                "uniswap-v3" => "Uniswap v3",
                "uniswap-v4" => "Uniswap v4",
                "uniswap" => "Uniswap",
                "aerodrome-classic" => "Aerodrome classic",
                "aerodrome-slipstream" => "Aerodrome Slipstream",
                "aerodrome" => "Aerodrome",
                "pancake-v3" => "PancakeSwap v3",
                "pancake-infinity-cl" => "PancakeSwap Infinity CL",
                "pancake-infinity-bin" => "PancakeSwap Infinity bin",
                "pancakeswap-infinity-cl" => "PancakeSwap Infinity CL",
                "pancakeswap-infinity-bin" => "PancakeSwap Infinity bin",
                "pancakeswap" => "PancakeSwap",
                "curve" => "Curve",
                "fermi-swap" => "FermiSwap",
                "shibaswap" => "ShibaSwap v1",
                EthereumDeploymentRegistry.ShibaSwapV2Factory => "ShibaSwap v2",
                _ => protocolId
            };
        }

        private static string ProtocolLabel(
            PoolCatalogEntry? catalogPool,
            OnChainPoolDescriptor descriptor)
        {
            if (descriptor.DiscoverySource.StartsWith("canonical:long.xyz", StringComparison.Ordinal))
            {
                return "long.xyz (Uniswap v4)";
            }
            if (descriptor.DiscoverySource.StartsWith("canonical:pons-v1", StringComparison.Ordinal))
            {
                return "pons v1 (Uniswap v3)";
            }
            if (descriptor.DiscoverySource.StartsWith("canonical:pons-v2", StringComparison.Ordinal))
            {
                return descriptor.PoolKey.ProtocolId == OnChainProtocolIds.PonsV2Curve
                    ? "pons v2 bonding curve"
                    : "pons v2 (Uniswap v4)";
            }
            if (descriptor.PoolKey.ProtocolId == OnChainProtocolIds.AerodromeClassic)
            {
                return descriptor.PoolType == "stable"
                    ? "Aerodrome classic stable"
                    : "Aerodrome classic volatile";
            }
            if (descriptor.PoolKey.ProtocolId == OnChainProtocolIds.AerodromeSlipstream)
            {
                var generation = descriptor.ProgramId.ToLowerInvariant() switch
                {
                    BaseDeploymentRegistry.AerodromeSlipstreamInitialFactory => "Initial",
                    BaseDeploymentRegistry.AerodromeSlipstreamGaugeCapsFactory => "Gauge Caps",
                    BaseDeploymentRegistry.AerodromeSlipstreamGaugesV3Factory => "Gauges V3",
                    _ => "Unknown"
                };
                return $"Aerodrome Slipstream {generation}";
            }
            if (string.Equals(
                    descriptor.ProgramId,
                    EthereumDeploymentRegistry.ShibaSwapV1Factory,
                    StringComparison.OrdinalIgnoreCase))
            {
                return "ShibaSwap v1";
            }
            if (string.Equals(
                    descriptor.ProgramId,
                    EthereumDeploymentRegistry.ShibaSwapV2Factory,
                    StringComparison.OrdinalIgnoreCase))
            {
                return "ShibaSwap v2";
            }
            if (catalogPool == null || descriptor.SupportStatus == OnChainSupportStatus.Supported)
            {
                return ProtocolLabel(descriptor.PoolKey.ProtocolId);
            }
            if (string.Equals(catalogPool.ProtocolId, "uniswap", StringComparison.OrdinalIgnoreCase))
            {
                var version = catalogPool.Labels.FirstOrDefault(static label =>
                    label.Equals("v2", StringComparison.OrdinalIgnoreCase)
                    || label.Equals("v3", StringComparison.OrdinalIgnoreCase)
                    || label.Equals("v4", StringComparison.OrdinalIgnoreCase));
                return version == null ? "Uniswap" : $"Uniswap {version.ToLowerInvariant()}";
            }
            if (string.Equals(catalogPool.ProtocolId, "pancakeswap", StringComparison.OrdinalIgnoreCase))
            {
                var version = catalogPool.Labels.FirstOrDefault(static label =>
                    label.Equals("v2", StringComparison.OrdinalIgnoreCase)
                    || label.Equals("v3", StringComparison.OrdinalIgnoreCase));
                return version == null ? "PancakeSwap" : $"PancakeSwap {version.ToLowerInvariant()}";
            }
            return ProtocolLabel(catalogPool.ProtocolId);
        }

        private static string FormatUsd(double? value, string unavailable)
        {
            return value is double number ? FormatUsd(number) : unavailable;
        }

        private static string FormatUsd(double value)
        {
            return value switch
            {
                >= 1_000_000_000 => $"${value / 1_000_000_000:0.##}B",
                >= 1_000_000 => $"${value / 1_000_000:0.##}M",
                >= 1_000 => $"${value / 1_000:0.##}K",
                _ => $"${value:0.##}"
            };
        }

        private static string FormatAge(long? createdAtUnixMs)
        {
            if (createdAtUnixMs is not long value)
            {
                return "--";
            }
            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(value);
            if (age < TimeSpan.Zero)
            {
                return "<1m";
            }
            return age.TotalDays >= 1
                ? $"{(int)age.TotalDays}d"
                : age.TotalHours >= 1
                    ? $"{(int)age.TotalHours}h"
                    : $"{Math.Max(1, (int)age.TotalMinutes)}m";
        }

        internal static string Short(string value)
        {
            return value.Length <= 12 ? value : $"{value[..6]}…{value[^4..]}";
        }
    }

}

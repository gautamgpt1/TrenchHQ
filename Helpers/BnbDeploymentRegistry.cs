using System;
using System.Linq;
using TrenchHQ.Models;

namespace TrenchHQ.Helpers
{
    internal static class BnbDeploymentRegistry
    {
        internal const string NativeBnb = "0x0000000000000000000000000000000000000000";
        internal const string WrappedBnb = "0xbb4cdb9cbd36b01bd1cbaebf2de08d9173bc095c";
        internal const string Usdt = "0x55d398326f99059ff775485246999027b3197955";
        internal const string Usdc = "0x8ac76a51cc950d9822d68b83fe1ad97b32cd580d";
        internal const string PancakeV2Factory = "0xca143ce32fe78f1f7019d7d551a6402fc5350c73";
        internal const string PancakeV3Factory = "0x0bfbcf9fa4f9c56b0f40a671ad40e0805a091865";
        internal const string PancakeInfinityClPoolManager = "0xa0ffb9c1ce1fe56963b0321b32e7a0302114058b";
        internal const string PancakeInfinityBinPoolManager = "0xc697d2898e0d09264376196696c51d7abbbaa4a9";
        internal const string UniswapV2Factory = "0x8909dc15e40173ff4699343b6eb8132c65e18ec6";
        internal const string UniswapV3Factory = "0xdb1d10011ad0ff90774d0c6bb92e5c5c8b4461f7";
        internal const string UniswapV4PoolManager = "0x28e2ea090877bf75740558f6bfb36a5ffee9e9df";
        internal const string UniswapV4StateView = "0xd13dd3d6e93f276fafc9db9e6bb47c1180aee0c4";
        internal const string WrappedBnbUsdtReferencePool = "0x172fcd41e0913e95784454622d1c3724f546f849";

        internal static readonly uint[] PancakeV3FeeTiers = [100, 500, 2500, 10000];

        internal static readonly EvmQuoteAsset[] MainnetQuoteAssets =
        [
            new("WBNB", WrappedBnb),
            new("USDT", Usdt),
            new("USDC", Usdc)
        ];

        private static readonly EvmPoolFamily PancakeV2Family = new(
            OnChainProtocolIds.PancakeV2,
            PancakeV2Factory,
            "bscRpc:pancakeV2Catalog");
        private static readonly EvmPoolFamily PancakeV3Family = new(
            OnChainProtocolIds.PancakeV3,
            PancakeV3Factory,
            "bscRpc:pancakeV3Catalog");
        private static readonly EvmPoolFamily PancakeInfinityClFamily = new(
            OnChainProtocolIds.PancakeInfinityCl,
            PancakeInfinityClPoolManager,
            "pancakeExplorer+bscRpc:infinityClCatalog");
        private static readonly EvmPoolFamily PancakeInfinityBinFamily = new(
            OnChainProtocolIds.PancakeInfinityBin,
            PancakeInfinityBinPoolManager,
            "pancakeExplorer+bscRpc:infinityBinCatalog");
        private static readonly EvmPoolFamily UniswapV2Family = new(
            OnChainProtocolIds.UniswapV2,
            UniswapV2Factory,
            "bscRpc:uniswapV2Catalog",
            "Uniswap");
        private static readonly EvmPoolFamily UniswapV3Family = new(
            OnChainProtocolIds.UniswapV3,
            UniswapV3Factory,
            "bscRpc:uniswapV3Catalog",
            "Uniswap",
            FeeTiers: EthereumDeploymentRegistry.UniswapV3FeeTiers);
        private static readonly EvmPoolFamily UniswapV4Family = new(
            OnChainProtocolIds.UniswapV4,
            UniswapV4PoolManager,
            "bscScan+bscRpc:uniswapV4Catalog",
            "Uniswap");

        internal static EvmPoolFamily[] GetCatalogPoolFamilies(PoolCatalogEntry pool)
        {
            var protocolId = pool.ProtocolId.Trim().ToLowerInvariant();
            if (protocolId is "pancakeswap-infinity-cl" or OnChainProtocolIds.PancakeInfinityCl)
            {
                return [PancakeInfinityClFamily];
            }
            if (protocolId is "pancakeswap-infinity-bin" or OnChainProtocolIds.PancakeInfinityBin)
            {
                return [PancakeInfinityBinFamily];
            }
            if (protocolId is not ("pancakeswap" or "pancakeswap-v2" or "pancakeswap-v3"))
            {
                if (protocolId != "uniswap")
                {
                    return [];
                }
                if (pool.Labels.Contains("v2", StringComparer.OrdinalIgnoreCase))
                {
                    return [UniswapV2Family];
                }
                if (pool.Labels.Contains("v3", StringComparer.OrdinalIgnoreCase))
                {
                    return [UniswapV3Family];
                }
                return pool.Labels.Contains("v4", StringComparer.OrdinalIgnoreCase)
                    ? [UniswapV4Family]
                    : [UniswapV2Family, UniswapV3Family];
            }
            if (protocolId == "pancakeswap-v2"
                || pool.Labels.Contains("v2", StringComparer.OrdinalIgnoreCase))
            {
                return [PancakeV2Family];
            }
            if (protocolId == "pancakeswap-v3"
                || pool.Labels.Contains("v3", StringComparer.OrdinalIgnoreCase))
            {
                return [PancakeV3Family];
            }
            return [PancakeV2Family, PancakeV3Family];
        }

        internal static EvmDeploymentCatalog Catalog { get; } = new(
            EvmChainDefinitions.BnbMainnetChainId,
            "BNB Smart Chain mainnet",
            "bsc",
            NativeBnb,
            OnChainProtocolIds.PancakeV2,
            OnChainProtocolIds.PancakeV3,
            "PancakeSwap",
            PancakeV2Factory,
            PancakeV3Factory,
            UniswapV4PoolManager,
            UniswapV4StateView,
            null,
            [],
            MainnetQuoteAssets,
            PancakeV3FeeTiers,
            [UniswapV2Family],
            [UniswapV3Family],
            GetCatalogPoolFamilies);

        internal static OnChainWatchedPoolSelection CreateBnbUsdReferenceSelection()
        {
            return new OnChainWatchedPoolSelection
            {
                SelectedMint = WrappedBnb,
                Descriptor = new OnChainPoolDescriptor
                {
                    PoolKey = new OnChainPoolKey
                    {
                        DeploymentKey = Catalog.Deployment(
                            OnChainProtocolIds.PancakeV3,
                            PancakeV3Factory),
                        PoolId = WrappedBnbUsdtReferencePool
                    },
                    PoolType = "concentratedLiquidity",
                    ProgramId = PancakeV3Factory,
                    BaseMint = WrappedBnb,
                    QuoteMint = Usdt,
                    BaseDecimals = 18,
                    QuoteDecimals = 18,
                    PairOrientation = "selectedAsToken1",
                    DiscoverySource = "canonical:pancakeV3:WBNB/USDT:100",
                    SupportStatus = OnChainSupportStatus.Supported,
                    Asset0 = Asset(Usdt),
                    Asset1 = Asset(WrappedBnb),
                    FeeTier = 100,
                    TickSpacing = 1
                }
            };
        }

        private static OnChainAssetKey Asset(string address)
        {
            return new OnChainAssetKey
            {
                ChainNamespace = ChainNamespaces.Eip155,
                ChainId = EvmChainDefinitions.BnbMainnetChainId,
                Address = address
            };
        }
    }
}

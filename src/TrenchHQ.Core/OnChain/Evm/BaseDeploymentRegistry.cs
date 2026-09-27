using TrenchHQ.Core.OnChain;
using System;
using System.Linq;

namespace TrenchHQ.Core.OnChain.Evm
{
    internal static class BaseDeploymentRegistry
    {
        internal const string NativeEther = "0x0000000000000000000000000000000000000000";
        internal const string WrappedEther = "0x4200000000000000000000000000000000000006";
        internal const string Usdc = "0x833589fcd6edb6e08f4c7c32d4f71b54bda02913";
        internal const string UniswapV2Factory = "0x8909dc15e40173ff4699343b6eb8132c65e18ec6";
        internal const string UniswapV3Factory = "0x33128a8fc17869897dce68ed026d694621f6fdfd";
        internal const string UniswapV4PoolManager = "0x498581ff718922c3f8e6a244956af099b2652b2b";
        internal const string UniswapV4StateView = "0xa3c0c9b65bad0b08107aa264b0f3db444b867a71";
        internal const string PancakeV2Factory = "0x02a84c1b3bbd7401a5f7fa98a384ebc70bb5749e";
        internal const string PancakeV3Factory = "0x0bfbcf9fa4f9c56b0f40a671ad40e0805a091865";
        internal const string PancakeInfinityClPoolManager = "0xa0ffb9c1ce1fe56963b0321b32e7a0302114058b";
        internal const string PancakeInfinityBinPoolManager = "0xc697d2898e0d09264376196696c51d7abbbaa4a9";
        internal const string AerodromeFactoryRegistry = "0x5c3f18f06cc09ca1910767a34a20f771039e37c0";
        internal const string AerodromeClassicFactory = "0x420dd381b31aef6683db6b902084cb0ffece40da";
        internal const string AerodromeSlipstreamInitialFactory = "0x5e7bb104d84c7cb9b682aac2f3d509f5f406809a";
        internal const string AerodromeSlipstreamGaugeCapsFactory = "0xade65c38cd4849adba595a4323a8c7ddfe89716a";
        internal const string AerodromeSlipstreamGaugesV3Factory = "0xf8f2eb4940cfe7d13603dddd87f123820fc061ef";
        internal const string WrappedEtherUsdcReferencePool = "0x6c561b446416e1a00e8e93e221854d6ea4171372";

        internal static readonly uint[] UniswapV3FeeTiers = [100, 500, 3000, 10000];
        internal static readonly uint[] PancakeV3FeeTiers = [100, 500, 2500, 10000];
        internal static readonly int[] AerodromeSlipstreamInitialTickSpacings = [1, 10, 50, 100, 200, 2000];
        internal static readonly int[] AerodromeSlipstreamGaugeCapsTickSpacings = [1, 10, 50, 100, 200, 500, 2000];
        internal static readonly int[] AerodromeSlipstreamGaugesV3TickSpacings = [1, 10, 50, 100, 200, 500, 2000];

        internal static readonly EvmQuoteAsset[] MainnetQuoteAssets =
        [
            new("WETH", WrappedEther),
            new("USDC", Usdc)
        ];

        private static readonly EvmPoolFamily UniswapV2Family = new(
            OnChainProtocolIds.UniswapV2,
            UniswapV2Factory,
            "baseRpc:uniswapV2Catalog");
        private static readonly EvmPoolFamily UniswapV3Family = new(
            OnChainProtocolIds.UniswapV3,
            UniswapV3Factory,
            "baseRpc:uniswapV3Catalog");
        private static readonly EvmPoolFamily UniswapV4Family = new(
            OnChainProtocolIds.UniswapV4,
            UniswapV4PoolManager,
            "blockscout+baseRpc:uniswapV4Catalog");
        private static readonly EvmPoolFamily PancakeV2Family = new(
            OnChainProtocolIds.PancakeV2,
            PancakeV2Factory,
            "baseRpc:pancakeV2Catalog",
            "PancakeSwap");
        private static readonly EvmPoolFamily PancakeV3Family = new(
            OnChainProtocolIds.PancakeV3,
            PancakeV3Factory,
            "baseRpc:pancakeV3Catalog",
            "PancakeSwap",
            FeeTiers: PancakeV3FeeTiers);
        private static readonly EvmPoolFamily PancakeInfinityClFamily = new(
            OnChainProtocolIds.PancakeInfinityCl,
            PancakeInfinityClPoolManager,
            "pancakeExplorer+baseRpc:infinityClCatalog");
        private static readonly EvmPoolFamily PancakeInfinityBinFamily = new(
            OnChainProtocolIds.PancakeInfinityBin,
            PancakeInfinityBinPoolManager,
            "pancakeExplorer+baseRpc:infinityBinCatalog");
        private static readonly EvmPoolFamily AerodromeClassicFamily = new(
            OnChainProtocolIds.AerodromeClassic,
            AerodromeClassicFactory,
            "baseRpc:aerodromeClassicCatalog");
        private static readonly EvmPoolFamily AerodromeSlipstreamInitialFamily = new(
            OnChainProtocolIds.AerodromeSlipstream,
            AerodromeSlipstreamInitialFactory,
            "baseRpc:aerodromeSlipstreamInitialCatalog",
            "Initial",
            AerodromeSlipstreamInitialTickSpacings);
        private static readonly EvmPoolFamily AerodromeSlipstreamGaugeCapsFamily = new(
            OnChainProtocolIds.AerodromeSlipstream,
            AerodromeSlipstreamGaugeCapsFactory,
            "baseRpc:aerodromeSlipstreamGaugeCapsCatalog",
            "Gauge Caps",
            AerodromeSlipstreamGaugeCapsTickSpacings);
        private static readonly EvmPoolFamily AerodromeSlipstreamGaugesV3Family = new(
            OnChainProtocolIds.AerodromeSlipstream,
            AerodromeSlipstreamGaugesV3Factory,
            "baseRpc:aerodromeSlipstreamGaugesV3Catalog",
            "Gauges V3",
            AerodromeSlipstreamGaugesV3TickSpacings);

        internal static readonly EvmPoolFamily[] AerodromeSlipstreamFamilies =
        [
            AerodromeSlipstreamInitialFamily,
            AerodromeSlipstreamGaugeCapsFamily,
            AerodromeSlipstreamGaugesV3Family
        ];

        internal static EvmDeploymentCatalog Catalog { get; } = new(
            EvmChainDefinitions.BaseMainnetChainId,
            "Base mainnet",
            "base",
            NativeEther,
            OnChainProtocolIds.UniswapV2,
            OnChainProtocolIds.UniswapV3,
            "Uniswap",
            UniswapV2Factory,
            UniswapV3Factory,
            UniswapV4PoolManager,
            UniswapV4StateView,
            AerodromeClassicFactory,
            AerodromeSlipstreamFamilies,
            MainnetQuoteAssets,
            UniswapV3FeeTiers,
            [PancakeV2Family],
            [PancakeV3Family],
            GetCatalogPoolFamilies);

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
            if (protocolId is "pancakeswap" or "pancakeswap-v2" or "pancakeswap-v3")
            {
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
            if (protocolId == "aerodrome")
            {
                return [AerodromeClassicFamily, .. AerodromeSlipstreamFamilies];
            }
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

        internal static OnChainWatchedPoolSelection CreateEthUsdReferenceSelection()
        {
            return new OnChainWatchedPoolSelection
            {
                SelectedMint = WrappedEther,
                Descriptor = new OnChainPoolDescriptor
                {
                    PoolKey = new OnChainPoolKey
                    {
                        DeploymentKey = Catalog.Deployment(
                            OnChainProtocolIds.UniswapV3,
                            UniswapV3Factory),
                        PoolId = WrappedEtherUsdcReferencePool
                    },
                    PoolType = "concentratedLiquidity",
                    ProgramId = UniswapV3Factory,
                    BaseMint = WrappedEther,
                    QuoteMint = Usdc,
                    BaseDecimals = 18,
                    QuoteDecimals = 6,
                    PairOrientation = "selectedAsToken0",
                    DiscoverySource = "canonical:uniswapV3:WETH/USDC:3000",
                    SupportStatus = OnChainSupportStatus.Supported,
                    Asset0 = Asset(WrappedEther),
                    Asset1 = Asset(Usdc),
                    FeeTier = 3000,
                    TickSpacing = 60
                }
            };
        }

        private static OnChainAssetKey Asset(string address)
        {
            return new OnChainAssetKey
            {
                ChainNamespace = ChainNamespaces.Eip155,
                ChainId = EvmChainDefinitions.BaseMainnetChainId,
                Address = address
            };
        }
    }
}

using System;
using System.Linq;
using TrenchHQ.Models;

namespace TrenchHQ.Helpers
{
    internal static class RobinhoodDeploymentRegistry
    {
        internal const string PublicRpcEndpoint = "https://rpc.mainnet.chain.robinhood.com";
        internal const string NativeEther = "0x0000000000000000000000000000000000000000";
        internal const string WrappedEther = "0x0bd7d308f8e1639fab988df18a8011f41eacad73";
        internal const string Usdg = "0x5fc5360d0400a0fd4f2af552add042d716f1d168";
        internal const string UniswapV2Factory = "0x8bceaa40b9acdfaedf85adf4ff01f5ad6517937f";
        internal const string UniswapV3Factory = "0x1f7d7550b1b028f7571e69a784071f0205fd2efa";
        internal const string UniswapV4PoolManager = "0x8366a39cc670b4001a1121b8f6a443a643e40951";
        internal const string UniswapV4StateView = "0xf3334192d15450cdd385c8b70e03f9a6bd9e673b";
        internal const string WrappedEtherUsdgReferencePool = "0x52e65b17fb6e5ba00ed806f37afcd2daa50271ca";
        internal const string DopplerAirlock = "0xeb7c034704ef8dcd2d32324c1545f62fb4ad0862";
        internal const string DopplerHookInitializer = "0x4e3468951d49f2eea976ed0d6e75ffcb44a9a544";
        internal const string PonsV1Factory = "0xa5aab3f0c6eeadf30ef1d3eb997108e976351feb";
        internal const string PonsV2Factory = "0x7ed598bcef8bd9edd8c97a195c6d13f40801ec7e";
        internal const string PonsV2MemeHook = "0xe5e702641ea86f4ae6cc3cdaed2b886f976be044";

        internal const string DopplerCreateTopic =
            "0xb224da6575b2c2ffd42454faedb236f7dbe5f92a0c96bb99c0273dbe98464c7e";
        internal const string PonsV2PoolGraduatedTopic =
            "0x0a44ef75df69c534f43cd6c1aa3ef8983065fe5fe79ef9e79f6494e6f258c259";

        internal static readonly uint[] UniswapV3FeeTiers = [100, 500, 3000, 10000];

        internal static readonly EvmQuoteAsset[] MainnetQuoteAssets =
        [
            new("WETH", WrappedEther),
            new("USDG", Usdg)
        ];

        private static readonly EvmPoolFamily UniswapV2Family = new(
            OnChainProtocolIds.UniswapV2,
            UniswapV2Factory,
            "robinhoodRpc:uniswapV2Catalog");
        private static readonly EvmPoolFamily UniswapV3Family = new(
            OnChainProtocolIds.UniswapV3,
            UniswapV3Factory,
            "robinhoodRpc:uniswapV3Catalog");
        private static readonly EvmPoolFamily UniswapV4Family = new(
            OnChainProtocolIds.UniswapV4,
            UniswapV4PoolManager,
            "blockscout+robinhoodRpc:uniswapV4Catalog");

        internal static EvmPoolFamily[] GetCatalogPoolFamilies(PoolCatalogEntry pool)
        {
            if (!pool.ProtocolId.Trim().Equals("uniswap", StringComparison.OrdinalIgnoreCase))
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

        internal static EvmDeploymentCatalog Catalog { get; } = new(
            EvmChainDefinitions.RobinhoodMainnetChainId,
            "Robinhood Chain mainnet",
            "robinhood",
            NativeEther,
            OnChainProtocolIds.UniswapV2,
            OnChainProtocolIds.UniswapV3,
            "Uniswap",
            UniswapV2Factory,
            UniswapV3Factory,
            UniswapV4PoolManager,
            UniswapV4StateView,
            null,
            [],
            MainnetQuoteAssets,
            UniswapV3FeeTiers,
            [],
            [],
            GetCatalogPoolFamilies);

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
                        PoolId = WrappedEtherUsdgReferencePool
                    },
                    PoolType = "concentratedLiquidity",
                    ProgramId = UniswapV3Factory,
                    BaseMint = WrappedEther,
                    QuoteMint = Usdg,
                    BaseDecimals = 18,
                    QuoteDecimals = 6,
                    PairOrientation = "selectedAsToken0",
                    DiscoverySource = "canonical:uniswapV3:WETH/USDG:100",
                    SupportStatus = OnChainSupportStatus.Supported,
                    Asset0 = Asset(WrappedEther),
                    Asset1 = Asset(Usdg),
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
                ChainId = EvmChainDefinitions.RobinhoodMainnetChainId,
                Address = address
            };
        }
    }
}

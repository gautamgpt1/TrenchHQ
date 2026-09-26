using System;
using System.Linq;
using TrenchHQ.Models;

namespace TrenchHQ.Helpers
{
    internal sealed record EvmQuoteAsset(string Symbol, string Address);
    internal sealed record EvmPoolFamily(
        string ProtocolId,
        string FactoryAddress,
        string DiscoverySource,
        string? Generation = null,
        int[]? TickSpacings = null,
        uint[]? FeeTiers = null);

    internal sealed record EvmDeploymentCatalog(
        string ChainId,
        string DisplayName,
        string CatalogChainId,
        string NativeAsset,
        string PrimaryV2ProtocolId,
        string PrimaryV3ProtocolId,
        string PrimaryDexDisplayName,
        string UniswapV2Factory,
        string UniswapV3Factory,
        string UniswapV4PoolManager,
        string UniswapV4StateView,
        string? AerodromeClassicFactory,
        EvmPoolFamily[] AerodromeSlipstreamFamilies,
        EvmQuoteAsset[] QuoteAssets,
        uint[] UniswapV3FeeTiers,
        EvmPoolFamily[] AdditionalV2Families,
        EvmPoolFamily[] AdditionalV3Families,
        Func<PoolCatalogEntry, EvmPoolFamily[]> GetCatalogPoolFamilies)
    {
        internal OnChainDeploymentKey Deployment(string protocolId, string contractAddress)
        {
            return new OnChainDeploymentKey
            {
                ChainNamespace = ChainNamespaces.Eip155,
                ChainId = ChainId,
                ProtocolId = protocolId,
                ContractAddress = contractAddress
            };
        }
    }

    internal static class EthereumDeploymentRegistry
    {
        internal const string NativeEther = "0x0000000000000000000000000000000000000000";
        internal const string UniswapV2Factory = "0x5c69bee701ef814a2b6a3edd4b1652cb9cc5aa6f";
        internal const string UniswapV3Factory = "0x1f98431c8ad98523631ae4a59f267346ea31f984";
        internal const string UniswapV4PoolManager = "0x000000000004444c5dc75cb358380d2e3de08a90";
        internal const string UniswapV4StateView = "0x7ffe42c4a5deea5b0fec41c94c136cf115597227";
        internal const string CurveAddressProvider = "0x0000000022d53366457f9d5e68ec105046fc4383";
        internal const string CurveNativeEther = "0xeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        internal const string FermiLegacySwapper = "0xb1076fe3ab5e28005c7c323bac5ac06a680d452e";
        internal const string FermiCurrentSwapper = "0x5979458912f80b96d30d4220af8e2e4925a33320";
        internal const string ShibaSwapV1Factory = "0x115934131916c8b277dd010ee02de363c09d037c";
        internal const string ShibaSwapV2Factory = "0xd9ce49caf7299daf18fffcb2b84a44fd33412509";
        internal const string WrappedEther = "0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2";
        internal const string Usdc = "0xa0b86991c6218b36c1d19d4a2e9eb0ce3606eb48";
        internal const string Usdt = "0xdac17f958d2ee523a2206206994597c13d831ec7";
        internal const string Dai = "0x6b175474e89094c44da98b954eedeac495271d0f";
        internal const string WrappedEtherUsdcReferencePool = "0x88e6a0c2ddd26feeb64f039a2c41296fcb3f5640";

        internal static readonly uint[] UniswapV3FeeTiers = [100, 500, 3000, 10000];

        internal static readonly EvmQuoteAsset[] MainnetQuoteAssets =
        [
            new("WETH", WrappedEther),
            new("USDC", Usdc),
            new("USDT", Usdt),
            new("DAI", Dai)
        ];

        private static readonly EvmPoolFamily UniswapV2Family = new(
            OnChainProtocolIds.UniswapV2,
            UniswapV2Factory,
            "ethereumRpc:uniswapV2Catalog");
        private static readonly EvmPoolFamily UniswapV3Family = new(
            OnChainProtocolIds.UniswapV3,
            UniswapV3Factory,
            "ethereumRpc:uniswapV3Catalog");
        private static readonly EvmPoolFamily UniswapV4Family = new(
            OnChainProtocolIds.UniswapV4,
            UniswapV4PoolManager,
            "blockscout+ethereumRpc:uniswapV4Catalog");
        private static readonly EvmPoolFamily ShibaSwapV1Family = new(
            OnChainProtocolIds.UniswapV2,
            ShibaSwapV1Factory,
            "ethereumRpc:shibaSwapV1Catalog");
        private static readonly EvmPoolFamily ShibaSwapV2Family = new(
            OnChainProtocolIds.UniswapV3,
            ShibaSwapV2Factory,
            "ethereumRpc:shibaSwapV2Catalog");

        internal static EvmPoolFamily[] GetCatalogPoolFamilies(PoolCatalogEntry pool)
        {
            var protocolId = pool.ProtocolId.Trim().ToLowerInvariant();
            if (protocolId == "uniswap")
            {
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
            return protocolId switch
            {
                "shibaswap" => [ShibaSwapV1Family],
                ShibaSwapV2Factory => [ShibaSwapV2Family],
                _ => []
            };
        }

        internal static EvmDeploymentCatalog Catalog { get; } = new(
            EvmChainDefinitions.EthereumMainnetChainId,
            "Ethereum mainnet",
            "ethereum",
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
                        DeploymentKey = UniswapV3Deployment(),
                        PoolId = WrappedEtherUsdcReferencePool
                    },
                    PoolType = "concentratedLiquidity",
                    ProgramId = UniswapV3Factory,
                    BaseMint = WrappedEther,
                    QuoteMint = Usdc,
                    BaseDecimals = 18,
                    QuoteDecimals = 6,
                    PairOrientation = "selectedAsToken1",
                    DiscoverySource = "canonical:uniswapV3:WETH/USDC:500",
                    SupportStatus = OnChainSupportStatus.Supported,
                    Asset0 = Asset(Usdc),
                    Asset1 = Asset(WrappedEther),
                    FeeTier = 500,
                    TickSpacing = 10
                }
            };
        }

        internal static OnChainDeploymentKey UniswapV2Deployment(string factoryAddress = UniswapV2Factory)
        {
            return new OnChainDeploymentKey
            {
                ChainNamespace = ChainNamespaces.Eip155,
                ChainId = EvmChainDefinitions.EthereumMainnetChainId,
                ProtocolId = OnChainProtocolIds.UniswapV2,
                ContractAddress = factoryAddress
            };
        }

        internal static OnChainDeploymentKey UniswapV3Deployment(string factoryAddress = UniswapV3Factory)
        {
            return new OnChainDeploymentKey
            {
                ChainNamespace = ChainNamespaces.Eip155,
                ChainId = EvmChainDefinitions.EthereumMainnetChainId,
                ProtocolId = OnChainProtocolIds.UniswapV3,
                ContractAddress = factoryAddress
            };
        }

        internal static OnChainDeploymentKey UniswapV4Deployment()
        {
            return new OnChainDeploymentKey
            {
                ChainNamespace = ChainNamespaces.Eip155,
                ChainId = EvmChainDefinitions.EthereumMainnetChainId,
                ProtocolId = OnChainProtocolIds.UniswapV4,
                ContractAddress = UniswapV4PoolManager
            };
        }

        private static OnChainAssetKey Asset(string address)
        {
            return new OnChainAssetKey
            {
                ChainNamespace = ChainNamespaces.Eip155,
                ChainId = EvmChainDefinitions.EthereumMainnetChainId,
                Address = address
            };
        }
    }
}

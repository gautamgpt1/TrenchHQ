using TrenchHQ.Models;
using System;

namespace TrenchHQ.Helpers
{
    internal sealed record EvmChainDefinition(
        string ChainNamespace,
        string ChainId,
        string DisplayName,
        string CatalogChainId,
        string BlockscoutApiRoot,
        string NativeAssetAddress,
        string WrappedNativeAssetAddress,
        string? NativeUsdReferencePoolId,
        string? UniswapV4StateViewAddress,
        string? CentralizedUsdMarket,
        string? ReferencePriceId,
        Func<OnChainWatchedPoolSelection>? CreateNativeUsdReferenceSelection)
    {
        internal string EnginePoolOwner => $"{ChainNamespace}:{ChainId}";
    }

    internal static class EvmChainDefinitions
    {
        internal const string EthereumMainnetChainId = "1";
        internal const string BaseMainnetChainId = "8453";
        internal const string BnbMainnetChainId = "56";
        internal const string RobinhoodMainnetChainId = "4663";

        internal static EvmChainDefinition EthereumMainnet { get; } = new(
            ChainNamespaces.Eip155,
            EthereumMainnetChainId,
            "Ethereum mainnet",
            "ethereum",
            "https://eth.blockscout.com/api",
            EthereumDeploymentRegistry.NativeEther,
            EthereumDeploymentRegistry.WrappedEther,
            EthereumDeploymentRegistry.WrappedEtherUsdcReferencePool,
            EthereumDeploymentRegistry.UniswapV4StateView,
            "ETH/USDT",
            "ethUsd",
            EthereumDeploymentRegistry.CreateEthUsdReferenceSelection);

        internal static EvmChainDefinition BaseMainnet { get; } = new(
            ChainNamespaces.Eip155,
            BaseMainnetChainId,
            "Base mainnet",
            "base",
            "https://base.blockscout.com/api",
            BaseDeploymentRegistry.NativeEther,
            BaseDeploymentRegistry.WrappedEther,
            BaseDeploymentRegistry.WrappedEtherUsdcReferencePool,
            BaseDeploymentRegistry.UniswapV4StateView,
            "ETH/USDT",
            "baseEthUsd",
            BaseDeploymentRegistry.CreateEthUsdReferenceSelection);

        internal static EvmChainDefinition BnbMainnet { get; } = new(
            ChainNamespaces.Eip155,
            BnbMainnetChainId,
            "BNB Smart Chain mainnet",
            "bsc",
            "https://api.bscscan.com/api",
            BnbDeploymentRegistry.NativeBnb,
            BnbDeploymentRegistry.WrappedBnb,
            BnbDeploymentRegistry.WrappedBnbUsdtReferencePool,
            BnbDeploymentRegistry.UniswapV4StateView,
            "BNB/USDT",
            "bnbUsd",
            BnbDeploymentRegistry.CreateBnbUsdReferenceSelection);

        internal static EvmChainDefinition RobinhoodMainnet { get; } = new(
            ChainNamespaces.Eip155,
            RobinhoodMainnetChainId,
            "Robinhood Chain mainnet",
            "robinhood",
            "https://robinhoodchain.blockscout.com/api",
            RobinhoodDeploymentRegistry.NativeEther,
            RobinhoodDeploymentRegistry.WrappedEther,
            RobinhoodDeploymentRegistry.WrappedEtherUsdgReferencePool,
            RobinhoodDeploymentRegistry.UniswapV4StateView,
            "ETH/USDT",
            "robinhoodEthUsd",
            RobinhoodDeploymentRegistry.CreateEthUsdReferenceSelection);

        internal static readonly EvmChainDefinition[] Supported =
        [
            EthereumMainnet,
            BaseMainnet,
            BnbMainnet,
            RobinhoodMainnet
        ];
    }
}

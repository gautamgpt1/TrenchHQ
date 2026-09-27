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
using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace TrenchHQ.Infrastructure.OnChain
{
    internal static class TokenPoolSearchService
    {
        internal static async Task<NamedTokenPoolValidation> ValidateNamedTokenAsync(
            TokenCatalogSearchEntry token,
            OnChainProviderConfigurationService providers)
        {
            var chainNamespace = ChainNamespaceForCatalog(token.ChainId);
            var chainId = ChainIdForCatalog(token.ChainId);
            using var validationTimeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(12));
            try
            {
                if (string.Equals(token.ChainId, "solana", StringComparison.OrdinalIgnoreCase))
                {
                    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
                    var discovery = await new OnChainPoolDiscoveryService(
                            new SolanaRpcClient(httpClient, OnChainPoolDiscoveryService.PublicMainnetRpcEndpoint),
                            OnChainEngineClient.Current)
                        .DiscoverAsync(
                            token.Address,
                            OnChainCommitment.Confirmed,
                            token.Pools.Select(static pool => pool.PoolAddress).ToArray(),
                            validationTimeout.Token,
                            includeDerivedDiscovery: false);
                    return new NamedTokenPoolValidation(token, chainNamespace, chainId, discovery);
                }

                var chain = EvmChainDefinitions.Supported.Single(definition =>
                    string.Equals(definition.CatalogChainId, token.ChainId, StringComparison.OrdinalIgnoreCase));
                var deployments = chain.ChainId switch
                {
                    EvmChainDefinitions.EthereumMainnetChainId => EthereumDeploymentRegistry.Catalog,
                    EvmChainDefinitions.BaseMainnetChainId => BaseDeploymentRegistry.Catalog,
                    EvmChainDefinitions.BnbMainnetChainId => BnbDeploymentRegistry.Catalog,
                    EvmChainDefinitions.RobinhoodMainnetChainId => RobinhoodDeploymentRegistry.Catalog,
                    _ => throw new InvalidOperationException("The chain is not supported.")
                };
                var configuration = providers.GetSelectedConfiguration(
                    chain.ChainNamespace,
                    chain.ChainId);
                string credential;
                if (configuration == null)
                {
                    var fallbackType = chain.ChainId switch
                    {
                        EvmChainDefinitions.BaseMainnetChainId => OnChainProviderTypes.BasePublic,
                        EvmChainDefinitions.BnbMainnetChainId => OnChainProviderTypes.PublicNodeBnb,
                        EvmChainDefinitions.RobinhoodMainnetChainId => OnChainProviderTypes.CustomRobinhood,
                        _ => OnChainProviderTypes.PublicNodeEthereum
                    };
                    configuration = OnChainProviderConfigurationStore.CreateConfiguration(
                        OnChainProviderCatalog.Get(fallbackType));
                    if (chain.ChainId == EvmChainDefinitions.RobinhoodMainnetChainId)
                    {
                        configuration.RpcEndpoint = RobinhoodDeploymentRegistry.PublicRpcEndpoint;
                    }
                    credential = string.Empty;
                }
                else
                {
                    credential = await providers.GetCredentialAsync(configuration);
                }

                using var evmHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
                var evmDiscovery = await new EthereumPoolDiscoveryService(
                        new EvmJsonRpcClient(evmHttpClient, configuration, credential),
                        chain,
                        deployments,
                        evmHttpClient)
                    .DiscoverCatalogPoolsSharedAsync(token.Address, token.Pools, validationTimeout.Token);
                return new NamedTokenPoolValidation(token, chainNamespace, chainId, evmDiscovery);
            }
            catch
            {
                return new NamedTokenPoolValidation(token, chainNamespace, chainId, null);
            }
        }

        private static string ChainNamespaceForCatalog(string catalogChainId)
        {
            return string.Equals(catalogChainId, "solana", StringComparison.OrdinalIgnoreCase)
                ? ChainNamespaces.Solana
                : ChainNamespaces.Eip155;
        }

        private static string ChainIdForCatalog(string catalogChainId)
        {
            if (string.Equals(catalogChainId, "solana", StringComparison.OrdinalIgnoreCase))
            {
                return "mainnet-beta";
            }
            return EvmChainDefinitions.Supported.Single(definition =>
                    string.Equals(definition.CatalogChainId, catalogChainId, StringComparison.OrdinalIgnoreCase))
                .ChainId;
        }

        internal static async Task<EvmChainPoolSearchResult> SearchEvmChainAsync(
            string tokenAddress,
            EvmChainDefinition chain,
            EvmDeploymentCatalog deployments,
            OnChainProviderConfigurationService providers)
        {
            PoolCatalogSearchResult? catalog = null;
            OnChainPoolDiscoveryResult? discovery = null;
            Exception? catalogError = null;
            Exception? discoveryError = null;
            var catalogTask = DexScreenerPoolCatalogClient.Current.SearchAsync(
                chain.CatalogChainId,
                tokenAddress);
            try
            {
                catalog = await catalogTask;
            }
            catch (Exception exception)
            {
                catalogError = exception;
            }
            if (chain.ChainId is EvmChainDefinitions.BnbMainnetChainId
                or EvmChainDefinitions.BaseMainnetChainId)
            {
                try
                {
                    var infinity = await PancakeInfinityPoolCatalogClient.Current.SearchAsync(
                        deployments,
                        tokenAddress);
                    catalog = MergeCatalogResults(catalog, infinity, tokenAddress);
                }
                catch (Exception exception)
                {
                    catalogError = catalogError == null
                        ? exception
                        : new AggregateException(catalogError, exception);
                }
            }
            if (chain.ChainId == EvmChainDefinitions.EthereumMainnetChainId)
            {
                try
                {
                    var curve = await CurvePoolCatalogClient.Current.SearchAsync(tokenAddress);
                    catalog = MergeCatalogResults(catalog, curve, tokenAddress);
                }
                catch (Exception exception)
                {
                    catalogError = catalogError == null
                        ? exception
                        : new AggregateException(catalogError, exception);
                }
            }
            if (chain.ChainId == EvmChainDefinitions.RobinhoodMainnetChainId)
            {
                try
                {
                    var stockToken = await RobinhoodStockTokenCatalogClient.Current.FindByAddressAsync(tokenAddress);
                    if (stockToken != null)
                    {
                        catalog ??= new PoolCatalogSearchResult
                        {
                            ChainId = chain.CatalogChainId,
                            AssetAddress = tokenAddress,
                            Pools = []
                        };
                        catalog.SourceId = string.IsNullOrWhiteSpace(catalog.SourceId)
                            ? "robinhood-stock-token-api"
                            : $"{catalog.SourceId}+robinhood-stock-token-api";
                        catalog.AssetName = stockToken.Name;
                        catalog.AssetSymbol = stockToken.Symbol;
                        catalog.IconUri = stockToken.LogoUri;
                    }
                }
                catch
                {
                    // Exact-address pool discovery remains available if metadata is rate limited.
                }
            }

            if (catalog is not { Pools.Length: > 0 })
            {
                return new EvmChainPoolSearchResult(
                    chain,
                    catalog,
                    new OnChainPoolDiscoveryResult { Mint = tokenAddress },
                    catalogError,
                    null);
            }

            try
            {
                var configuration = providers.GetSelectedConfiguration(
                    chain.ChainNamespace,
                    chain.ChainId);
                string credential;
                if (configuration == null)
                {
                    var fallbackType = chain.ChainId switch
                    {
                        EvmChainDefinitions.BaseMainnetChainId => OnChainProviderTypes.BasePublic,
                        EvmChainDefinitions.BnbMainnetChainId => OnChainProviderTypes.PublicNodeBnb,
                        EvmChainDefinitions.RobinhoodMainnetChainId => OnChainProviderTypes.CustomRobinhood,
                        _ => OnChainProviderTypes.PublicNodeEthereum
                    };
                    configuration = OnChainProviderConfigurationStore.CreateConfiguration(
                        OnChainProviderCatalog.Get(fallbackType));
                    if (chain.ChainId == EvmChainDefinitions.RobinhoodMainnetChainId)
                    {
                        configuration.RpcEndpoint = RobinhoodDeploymentRegistry.PublicRpcEndpoint;
                    }
                    credential = string.Empty;
                }
                else
                {
                    credential = await providers.GetCredentialAsync(configuration);
                }
                using var validationTimeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                var poolDiscovery = new EthereumPoolDiscoveryService(
                        new EvmJsonRpcClient(httpClient, configuration, credential),
                        chain,
                        deployments,
                        httpClient);
                discovery = await poolDiscovery.DiscoverCatalogPoolsSharedAsync(
                    tokenAddress,
                    catalog.Pools,
                    validationTimeout.Token);
            }
            catch (Exception exception)
            {
                discoveryError = exception;
            }

            return new EvmChainPoolSearchResult(
                chain,
                catalog,
                discovery,
                catalogError,
                discoveryError);
        }

        internal static PoolCatalogSearchResult MergeCatalogResults(
            PoolCatalogSearchResult? primary,
            PoolCatalogSearchResult secondary,
            string assetAddress)
        {
            var addressComparer = string.Equals(secondary.ChainId, "solana", StringComparison.OrdinalIgnoreCase)
                ? StringComparer.Ordinal
                : StringComparer.OrdinalIgnoreCase;
            var pools = secondary.Pools
                .Concat(primary?.Pools ?? [])
                .GroupBy(static pool => pool.PoolAddress, addressComparer)
                .Select(static group => group.First())
                .OrderByDescending(static pool => pool.LiquidityUsd ?? double.MinValue)
                .ThenByDescending(static pool => pool.Volume24hUsd ?? double.MinValue)
                .ToArray();
            return new PoolCatalogSearchResult
            {
                SourceId = primary == null ? secondary.SourceId : $"{primary.SourceId}+{secondary.SourceId}",
                ChainId = secondary.ChainId,
                AssetAddress = assetAddress,
                AssetName = !string.IsNullOrWhiteSpace(primary?.AssetName)
                    ? primary.AssetName
                    : secondary.AssetName,
                AssetSymbol = !string.IsNullOrWhiteSpace(primary?.AssetSymbol)
                    ? primary.AssetSymbol
                    : secondary.AssetSymbol,
                IconUri = primary?.IconUri ?? secondary.IconUri,
                Pools = pools
            };
        }

    }
}

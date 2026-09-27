using System;
using TrenchHQ.Core.OnChain;
using TrenchHQ.Core.OnChain.Evm;

namespace TrenchHQ.Infrastructure.OnChain
{
    internal sealed record EvmChainPoolSearchResult(
        EvmChainDefinition Chain,
        PoolCatalogSearchResult? Catalog,
        OnChainPoolDiscoveryResult? Discovery,
        Exception? CatalogError,
        Exception? DiscoveryError);

    internal sealed record NamedTokenPoolValidation(
        TokenCatalogSearchEntry Token,
        string ChainNamespace,
        string ChainId,
        OnChainPoolDiscoveryResult? Discovery);

}

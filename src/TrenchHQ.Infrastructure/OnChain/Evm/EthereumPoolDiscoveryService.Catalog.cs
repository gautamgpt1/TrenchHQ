using TrenchHQ.Core.OnChain;
using TrenchHQ.Core.OnChain.Evm;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrenchHQ.Infrastructure.OnChain.Evm
{
    internal sealed partial class EthereumPoolDiscoveryService
    {
        internal async Task<OnChainPoolDiscoveryResult> DiscoverCatalogPoolsAsync(
            string tokenAddress,
            IReadOnlyCollection<PoolCatalogEntry> catalogPools,
            CancellationToken cancellationToken)
        {
            if (!EvmAddress.TryNormalize(tokenAddress, out var selectedToken))
            {
                throw new ArgumentException($"Enter a valid {_chain.DisplayName} token contract address.", nameof(tokenAddress));
            }
            if (await _rpc.GetChainIdAsync(cancellationToken).ConfigureAwait(false)
                != ulong.Parse(_chain.ChainId, System.Globalization.CultureInfo.InvariantCulture))
            {
                throw new InvalidOperationException($"The selected provider is not connected to {_chain.DisplayName}.");
            }

            var block = await _rpc.GetBlockAsync("latest", cancellationToken).ConfigureAwait(false);
            var blockReference = new { blockHash = block.Hash, requireCanonical = true };
            if (!HasCode(await _rpc.GetCodeAsync(
                    selectedToken,
                    blockReference,
                    cancellationToken).ConfigureAwait(false)))
            {
                throw new InvalidOperationException($"The {_chain.DisplayName} address has no contract code at the validation block.");
            }
            var decimals = new Dictionary<string, byte?>(StringComparer.OrdinalIgnoreCase)
            {
                [selectedToken] = await TryReadDecimalsAsync(
                    selectedToken,
                    blockReference,
                    cancellationToken).ConfigureAwait(false)
            };
            var candidates = catalogPools
                .GroupBy(static pool => pool.PoolAddress, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .ToArray();
            var pools = new OnChainPoolDescriptor?[candidates.Length];
            var warnings = new string?[candidates.Length];
            var truncated = false;
            try
            {
                await Parallel.ForEachAsync(
                Enumerable.Range(0, candidates.Length),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = 4,
                    CancellationToken = cancellationToken
                },
                async (index, token) =>
            {
                var catalogPool = candidates[index];
                var poolDecimals = new Dictionary<string, byte?>(decimals, StringComparer.OrdinalIgnoreCase);
                if (catalogPool.ProtocolId.Equals(OnChainProtocolIds.Curve, StringComparison.OrdinalIgnoreCase))
                {
                    if (_chain.ChainId != EvmChainDefinitions.EthereumMainnetChainId)
                    {
                        return;
                    }
                    try
                    {
                        pools[index] = await ValidateCurvePoolAsync(
                            selectedToken,
                            catalogPool,
                            block,
                            blockReference,
                            poolDecimals,
                            token).ConfigureAwait(false);
                    }
                    catch (EvmJsonRpcException exception) when (exception.Kind == EvmRpcFailureKind.RpcError)
                    {
                        pools[index] = CreateCurveValidationFailure(selectedToken, catalogPool, exception.Message, false);
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
                    {
                        pools[index] = CreateCurveValidationFailure(selectedToken, catalogPool, exception.Message, false);
                    }
                    catch (OperationCanceledException exception) when (!token.IsCancellationRequested)
                    {
                        pools[index] = CreateCurveValidationFailure(selectedToken, catalogPool, exception.Message, true);
                    }
                    return;
                }
                var families = _deployments.GetCatalogPoolFamilies(catalogPool);
                var isSingleton = families.Any(static family =>
                    family.ProtocolId is OnChainProtocolIds.UniswapV4
                        or OnChainProtocolIds.PancakeInfinityCl
                        or OnChainProtocolIds.PancakeInfinityBin);
                string poolAddress;
                if (isSingleton)
                {
                    if (!EvmAddress.IsHash(catalogPool.PoolAddress))
                    {
                        return;
                    }
                    poolAddress = catalogPool.PoolAddress.ToLowerInvariant();
                }
                else if (!EvmAddress.TryNormalize(catalogPool.PoolAddress, out poolAddress))
                {
                    return;
                }
                var otherAddress = string.Equals(
                    catalogPool.BaseAsset.Address,
                    selectedToken,
                    StringComparison.OrdinalIgnoreCase)
                    ? catalogPool.QuoteAsset.Address
                    : string.Equals(
                        catalogPool.QuoteAsset.Address,
                        selectedToken,
                        StringComparison.OrdinalIgnoreCase)
                        ? catalogPool.BaseAsset.Address
                        : string.Empty;
                if (!EvmAddress.TryNormalize(otherAddress, out var quoteAddress)
                    || string.Equals(selectedToken, quoteAddress, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                Exception? validationFailure = null;
                foreach (var family in families)
                {
                    try
                    {
                        var descriptor = family.ProtocolId switch
                        {
                            OnChainProtocolIds.UniswapV2 or OnChainProtocolIds.PancakeV2 => await ValidateV2PairAsync(
                                selectedToken,
                                quoteAddress,
                                poolAddress,
                                family.FactoryAddress,
                                family.ProtocolId,
                                family.DiscoverySource,
                                block,
                                blockReference,
                                poolDecimals,
                                token).ConfigureAwait(false),
                            OnChainProtocolIds.UniswapV3 or OnChainProtocolIds.PancakeV3 => await ValidateV3PoolAsync(
                                selectedToken,
                                quoteAddress,
                                null,
                                poolAddress,
                                family.FactoryAddress,
                                family.ProtocolId,
                                family.DiscoverySource,
                                block,
                                blockReference,
                                poolDecimals,
                                token).ConfigureAwait(false),
                            OnChainProtocolIds.PancakeInfinityCl
                                or OnChainProtocolIds.PancakeInfinityBin => await ValidateInfinityPoolAsync(
                                selectedToken,
                                quoteAddress,
                                poolAddress,
                                family,
                                block,
                                blockReference,
                                poolDecimals,
                                token).ConfigureAwait(false),
                            OnChainProtocolIds.UniswapV4 => await ValidateV4PoolAsync(
                                selectedToken,
                                quoteAddress,
                                poolAddress,
                                catalogPool.CreatedAtUnixMs,
                                family.DiscoverySource,
                                block,
                                blockReference,
                                poolDecimals,
                                token).ConfigureAwait(false),
                            OnChainProtocolIds.AerodromeClassic => await ValidateAerodromeClassicPoolAsync(
                                selectedToken,
                                quoteAddress,
                                null,
                                poolAddress,
                                family.FactoryAddress,
                                family.DiscoverySource,
                                block,
                                blockReference,
                                poolDecimals,
                                token).ConfigureAwait(false),
                            OnChainProtocolIds.AerodromeSlipstream => await ValidateAerodromeSlipstreamPoolAsync(
                                selectedToken,
                                quoteAddress,
                                null,
                                poolAddress,
                                family,
                                block,
                                blockReference,
                                poolDecimals,
                                token).ConfigureAwait(false),
                            _ => throw new InvalidOperationException("The catalog pool family is unsupported.")
                        };
                        pools[index] = descriptor;
                        validationFailure = null;
                        break;
                    }
                    catch (EvmJsonRpcException exception) when (exception.Kind == EvmRpcFailureKind.RpcError)
                    {
                        validationFailure = exception;
                    }
                    catch (EvmJsonRpcException exception) when (exception.Kind == EvmRpcFailureKind.RateLimited)
                    {
                        validationFailure = exception;
                        warnings[index] = $"Some {_chain.DisplayName} catalog pools could not be validated because the provider rate limit was reached.";
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
                    {
                        validationFailure = exception;
                    }
                    catch (OperationCanceledException exception) when (!token.IsCancellationRequested)
                    {
                        validationFailure = exception;
                    }
                }
                if (families.Length > 0 && validationFailure != null)
                {
                    pools[index] = CreateCatalogValidationFailure(
                        selectedToken,
                        quoteAddress,
                        poolAddress,
                        families[0],
                        validationFailure.Message,
                        validationFailure is OperationCanceledException
                        || validationFailure is EvmJsonRpcException { Kind: EvmRpcFailureKind.RateLimited });
                }
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested
                                                    && pools.Any(static pool => pool != null))
            {
                truncated = true;
            }
            return new OnChainPoolDiscoveryResult
            {
                Mint = selectedToken,
                Pools = pools.OfType<OnChainPoolDescriptor>().ToArray(),
                Truncated = truncated,
                Warnings = warnings.OfType<string>().Distinct(StringComparer.Ordinal).ToArray()
            };
        }

        internal Task<OnChainPoolDiscoveryResult> DiscoverCatalogPoolsSharedAsync(
            string tokenAddress,
            IReadOnlyCollection<PoolCatalogEntry> catalogPools,
            CancellationToken cancellationToken)
        {
            return DiscoverCatalogPoolsAsync(tokenAddress, catalogPools, cancellationToken);
        }

        private async Task<OnChainPoolDescriptor> ValidateCurvePoolAsync(
            string selectedToken,
            PoolCatalogEntry catalogPool,
            EvmRpcBlockHeader validationBlock,
            object blockReference,
            Dictionary<string, byte?> decimals,
            CancellationToken cancellationToken)
        {
            if (!EvmAddress.TryNormalize(catalogPool.PoolAddress, out var poolAddress)
                || !catalogPool.BaseAsset.Address.Equals(selectedToken, StringComparison.OrdinalIgnoreCase)
                || catalogPool.BaseTokenIndex is not uint baseIndex
                || catalogPool.QuoteTokenIndex is not uint quoteIndex
                || baseIndex == quoteIndex
                || baseIndex > 7
                || quoteIndex > 7)
            {
                throw new InvalidOperationException("The Curve catalog entry has invalid pool or coin-index metadata.");
            }
            var quoteAddress = catalogPool.QuoteAsset.Address;
            if (!quoteAddress.Equals(_deployments.NativeAsset, StringComparison.OrdinalIgnoreCase)
                && !EvmAddress.TryNormalize(quoteAddress, out quoteAddress))
            {
                throw new InvalidOperationException("The Curve catalog entry has an invalid quote coin.");
            }
            if (selectedToken.Equals(quoteAddress, StringComparison.OrdinalIgnoreCase)
                || !HasCode(await _rpc.GetCodeAsync(poolAddress, blockReference, cancellationToken)
                    .ConfigureAwait(false)))
            {
                throw new InvalidOperationException("The Curve pool has no usable contract code at the validation block.");
            }

            var onChainBase = await ReadCurveCoinAsync(
                poolAddress,
                baseIndex,
                blockReference,
                cancellationToken).ConfigureAwait(false);
            var onChainQuote = await ReadCurveCoinAsync(
                poolAddress,
                quoteIndex,
                blockReference,
                cancellationToken).ConfigureAwait(false);
            if (!onChainBase.Equals(selectedToken, StringComparison.OrdinalIgnoreCase)
                || !onChainQuote.Equals(quoteAddress, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The Curve pool coin indices do not match the official catalog.");
            }

            var baseDecimals = await GetDecimalsAsync(
                selectedToken,
                blockReference,
                decimals,
                cancellationToken).ConfigureAwait(false);
            var quoteDecimals = await GetDecimalsAsync(
                quoteAddress,
                blockReference,
                decimals,
                cancellationToken).ConfigureAwait(false);
            var supported = baseDecimals != null && quoteDecimals != null;
            return new OnChainPoolDescriptor
            {
                PoolKey = new OnChainPoolKey
                {
                    DeploymentKey = _deployments.Deployment(
                        OnChainProtocolIds.Curve,
                        EthereumDeploymentRegistry.CurveAddressProvider),
                    PoolId = poolAddress
                },
                PoolType = "stableOrCryptoExecutedTrades",
                ProgramId = EthereumDeploymentRegistry.CurveAddressProvider,
                BaseMint = selectedToken,
                QuoteMint = quoteAddress,
                BaseDecimals = baseDecimals ?? 0,
                QuoteDecimals = quoteDecimals ?? 0,
                ProtocolAccounts =
                [
                    new OnChainProtocolAccount
                    {
                        Role = "baseCoin",
                        Address = selectedToken,
                        Index = baseIndex
                    },
                    new OnChainProtocolAccount
                    {
                        Role = "quoteCoin",
                        Address = quoteAddress,
                        Index = quoteIndex
                    }
                ],
                PairOrientation = "selectedAsCurveBaseCoin",
                DiscoverySource = "curve-api+ethereumRpc:coins",
                SupportStatus = supported
                    ? OnChainSupportStatus.Supported
                    : OnChainSupportStatus.DiscoveredUnsupported,
                SupportReason = supported ? null : "ERC-20 decimals could not be read at the validation block.",
                Asset0 = Asset(selectedToken),
                Asset1 = Asset(quoteAddress),
                ValidationBlock = validationBlock.Number,
                ValidationBlockHash = validationBlock.Hash
            };
        }

        private async Task<string> ReadCurveCoinAsync(
            string poolAddress,
            uint index,
            object blockReference,
            CancellationToken cancellationToken)
        {
            var data = await ReadCallDataAsync(
                poolAddress,
                EthereumAbi.EncodeUnsignedCall(EthereumAbi.CurveCoinsSelector, index),
                blockReference,
                cancellationToken).ConfigureAwait(false);
            if (!EthereumAbi.TryDecodeAddress(data, out var address))
            {
                throw new InvalidOperationException("A Curve pool returned a malformed coin address.");
            }
            return address.Equals(EthereumDeploymentRegistry.CurveNativeEther, StringComparison.OrdinalIgnoreCase)
                ? _deployments.NativeAsset
                : address;
        }

        private OnChainPoolDescriptor CreateCurveValidationFailure(
            string selectedToken,
            PoolCatalogEntry catalogPool,
            string reason,
            bool temporarilyUnavailable)
        {
            var quoteAddress = catalogPool.QuoteAsset.Address;
            return new OnChainPoolDescriptor
            {
                PoolKey = new OnChainPoolKey
                {
                    DeploymentKey = _deployments.Deployment(
                        OnChainProtocolIds.Curve,
                        EthereumDeploymentRegistry.CurveAddressProvider),
                    PoolId = catalogPool.PoolAddress.ToLowerInvariant()
                },
                PoolType = "stableOrCryptoExecutedTrades",
                ProgramId = EthereumDeploymentRegistry.CurveAddressProvider,
                BaseMint = selectedToken,
                QuoteMint = quoteAddress,
                DiscoverySource = "curve-api+ethereumRpc:coins",
                SupportStatus = temporarilyUnavailable
                    ? OnChainSupportStatus.TemporarilyUnavailable
                    : OnChainSupportStatus.DiscoveredUnsupported,
                SupportReason = $"On-chain validation failed for this Curve pool: {reason}"
            };
        }

        private async Task<OnChainPoolDescriptor> ValidateInfinityPoolAsync(
            string selectedToken,
            string quoteAddress,
            string poolId,
            EvmPoolFamily family,
            EvmRpcBlockHeader validationBlock,
            object blockReference,
            Dictionary<string, byte?> decimals,
            CancellationToken cancellationToken)
        {
            if (!EvmAddress.IsHash(poolId)
                || family.ProtocolId is not (OnChainProtocolIds.PancakeInfinityCl
                    or OnChainProtocolIds.PancakeInfinityBin))
            {
                throw new InvalidOperationException("The catalog did not provide a valid Infinity PoolId.");
            }
            if (!HasCode(await _rpc.GetCodeAsync(
                    family.FactoryAddress,
                    blockReference,
                    cancellationToken).ConfigureAwait(false)))
            {
                throw new InvalidOperationException("The allowlisted Infinity PoolManager has no contract code.");
            }
            var poolKeyData = await ReadCallDataAsync(
                family.FactoryAddress,
                EthereumAbi.EncodeBytes32Call(EthereumAbi.InfinityPoolKeySelector, poolId),
                blockReference,
                cancellationToken).ConfigureAwait(false);
            if (!EthereumAbi.TryDecodeInfinityPoolKey(poolKeyData, out var poolKey)
                || !poolKey.PoolManager.Equals(family.FactoryAddress, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The Infinity PoolManager did not resolve the expected pool key.");
            }
            var expectedCurrencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                selectedToken,
                quoteAddress
            };
            if (expectedCurrencies.Count != 2
                || !expectedCurrencies.SetEquals([poolKey.Currency0, poolKey.Currency1]))
            {
                throw new InvalidOperationException("The Infinity pool currencies do not match the search.");
            }

            int? tickSpacing = null;
            ushort? binStep = null;
            var slot0Data = await ReadCallDataAsync(
                family.FactoryAddress,
                EthereumAbi.EncodeBytes32Call(EthereumAbi.InfinitySlot0Selector, poolId),
                blockReference,
                cancellationToken).ConfigureAwait(false);
            if (family.ProtocolId == OnChainProtocolIds.PancakeInfinityCl)
            {
                if (!EthereumAbi.TryDecodeInfinityClParameters(poolKey.Parameters, out var decodedTickSpacing)
                    || !EthereumAbi.TryDecodeUniswapV4Slot0(slot0Data, out _, out _))
                {
                    throw new InvalidOperationException("The Infinity CL pool returned malformed parameters or slot0 state.");
                }
                var liquidityData = await ReadCallDataAsync(
                    family.FactoryAddress,
                    EthereumAbi.EncodeBytes32Call(EthereumAbi.InfinityLiquiditySelector, poolId),
                    blockReference,
                    cancellationToken).ConfigureAwait(false);
                if (!EthereumAbi.TryDecodeSingleUnsigned(liquidityData, 128, out _))
                {
                    throw new InvalidOperationException("The Infinity CL pool returned malformed active liquidity.");
                }
                tickSpacing = decodedTickSpacing;
            }
            else
            {
                if (!EthereumAbi.TryDecodeInfinityBinParameters(poolKey.Parameters, out var decodedBinStep)
                    || !EthereumAbi.TryDecodeInfinityBinSlot0(slot0Data, out _))
                {
                    throw new InvalidOperationException("The Infinity bin pool returned malformed parameters or slot0 state.");
                }
                binStep = decodedBinStep;
            }

            var currency0Decimals = await GetDecimalsAsync(
                poolKey.Currency0,
                blockReference,
                decimals,
                cancellationToken).ConfigureAwait(false);
            var currency1Decimals = await GetDecimalsAsync(
                poolKey.Currency1,
                blockReference,
                decimals,
                cancellationToken).ConfigureAwait(false);
            var selectedIsCurrency0 = selectedToken.Equals(
                poolKey.Currency0,
                StringComparison.OrdinalIgnoreCase);
            var baseDecimals = selectedIsCurrency0 ? currency0Decimals : currency1Decimals;
            var quoteDecimals = selectedIsCurrency0 ? currency1Decimals : currency0Decimals;
            var supported = baseDecimals.HasValue && quoteDecimals.HasValue;
            return new OnChainPoolDescriptor
            {
                PoolKey = new OnChainPoolKey
                {
                    DeploymentKey = _deployments.Deployment(
                        family.ProtocolId,
                        family.FactoryAddress),
                    PoolId = poolId.ToLowerInvariant()
                },
                PoolType = family.ProtocolId == OnChainProtocolIds.PancakeInfinityCl
                    ? "concentratedLiquidity"
                    : "binLiquidity",
                ProgramId = family.FactoryAddress,
                BaseMint = selectedToken,
                QuoteMint = quoteAddress,
                BaseDecimals = baseDecimals ?? 0,
                QuoteDecimals = quoteDecimals ?? 0,
                PairOrientation = selectedIsCurrency0 ? "selectedAsToken0" : "selectedAsToken1",
                DiscoverySource = family.DiscoverySource,
                SupportStatus = supported
                    ? OnChainSupportStatus.Supported
                    : OnChainSupportStatus.DiscoveredUnsupported,
                SupportReason = supported ? null : "ERC-20 decimals could not be read at the validation block.",
                Asset0 = Asset(poolKey.Currency0),
                Asset1 = Asset(poolKey.Currency1),
                ValidationBlock = validationBlock.Number,
                ValidationBlockHash = validationBlock.Hash,
                FeeTier = poolKey.Fee,
                TickSpacing = tickSpacing,
                BinStep = binStep,
                HookAddress = poolKey.HookAddress
            };
        }

        private OnChainPoolDescriptor CreateCatalogValidationFailure(
            string selectedToken,
            string quoteAddress,
            string poolAddress,
            EvmPoolFamily family,
            string reason,
            bool temporarilyUnavailable)
        {
            var isV2 = family.ProtocolId is OnChainProtocolIds.UniswapV2 or OnChainProtocolIds.PancakeV2;
            var isV4 = family.ProtocolId == OnChainProtocolIds.UniswapV4;
            var isInfinityCl = family.ProtocolId == OnChainProtocolIds.PancakeInfinityCl;
            var isInfinityBin = family.ProtocolId == OnChainProtocolIds.PancakeInfinityBin;
            var isAerodromeClassic = family.ProtocolId == OnChainProtocolIds.AerodromeClassic;
            return new OnChainPoolDescriptor
            {
                PoolKey = new OnChainPoolKey
                {
                    DeploymentKey = _deployments.Deployment(
                        family.ProtocolId,
                        isV4 ? _deployments.UniswapV4PoolManager : family.FactoryAddress),
                    PoolId = poolAddress
                },
                PoolType = isV4
                    ? "singletonConcentratedLiquidity"
                    : isInfinityCl ? "concentratedLiquidity"
                    : isInfinityBin ? "binLiquidity"
                    : isV2 ? "constantProduct"
                    : isAerodromeClassic ? "unknownCurve"
                    : "concentratedLiquidity",
                ProgramId = family.FactoryAddress,
                BaseMint = selectedToken,
                QuoteMint = quoteAddress,
                DiscoverySource = family.DiscoverySource,
                SupportStatus = temporarilyUnavailable
                    ? OnChainSupportStatus.TemporarilyUnavailable
                    : OnChainSupportStatus.DiscoveredUnsupported,
                SupportReason = $"On-chain validation failed for this pool: {reason}"
            };
        }

    }
}

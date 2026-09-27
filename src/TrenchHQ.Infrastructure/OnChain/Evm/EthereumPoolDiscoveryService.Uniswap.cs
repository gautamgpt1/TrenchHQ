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
        internal async Task<OnChainPoolDiscoveryResult> DiscoverUniswapV2Async(
            string tokenAddress,
            CancellationToken cancellationToken = default)
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

            var decimals = new Dictionary<string, byte?>(StringComparer.OrdinalIgnoreCase);
            decimals[selectedToken] = await TryReadDecimalsAsync(
                selectedToken,
                blockReference,
                cancellationToken).ConfigureAwait(false);

            var pools = new List<OnChainPoolDescriptor>();
            var warnings = new List<string>();
            var families = new[]
            {
                new EvmPoolFamily(
                    _deployments.PrimaryV2ProtocolId,
                    _deployments.UniswapV2Factory,
                    $"{_deployments.CatalogChainId}Rpc:{_deployments.PrimaryDexDisplayName}V2Factory",
                    _deployments.PrimaryDexDisplayName)
            }.Concat(_deployments.AdditionalV2Families);
            foreach (var quote in _deployments.QuoteAssets)
            {
                if (string.Equals(selectedToken, quote.Address, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var family in families)
                {
                    try
                    {
                        var pairData = await ReadCallDataAsync(
                            family.FactoryAddress,
                            EthereumAbi.EncodeAddressPairCall(
                                EthereumAbi.GetPairSelector,
                                selectedToken,
                                quote.Address),
                            blockReference,
                            cancellationToken).ConfigureAwait(false);
                        if (!EthereumAbi.TryDecodeAddress(pairData, out var pair)
                            || string.Equals(pair, ZeroAddress, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var descriptor = await ValidateV2PairAsync(
                            selectedToken,
                            quote.Address,
                            pair,
                            family.FactoryAddress,
                            family.ProtocolId,
                            family.DiscoverySource,
                            block,
                            blockReference,
                            decimals,
                            cancellationToken).ConfigureAwait(false);
                        pools.Add(descriptor);
                    }
                    catch (EvmJsonRpcException)
                    {
                        throw;
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
                    {
                        warnings.Add(
                            $"The {family.Generation ?? _deployments.PrimaryDexDisplayName} v2 {quote.Symbol} candidate failed validation: {exception.Message}");
                    }
                }
            }

            return new OnChainPoolDiscoveryResult
            {
                Mint = selectedToken,
                Pools = pools
                    .GroupBy(static pool => pool.PoolKey.PoolId, StringComparer.OrdinalIgnoreCase)
                    .Select(static group => group.First())
                    .ToArray(),
                Warnings = warnings.ToArray()
            };
        }

        internal async Task<OnChainPoolDiscoveryResult> DiscoverUniswapV3Async(
            string tokenAddress,
            CancellationToken cancellationToken = default)
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

            var decimals = new Dictionary<string, byte?>(StringComparer.OrdinalIgnoreCase);
            decimals[selectedToken] = await TryReadDecimalsAsync(
                selectedToken,
                blockReference,
                cancellationToken).ConfigureAwait(false);
            var pools = new List<OnChainPoolDescriptor>();
            var warnings = new List<string>();
            var families = new[]
            {
                new EvmPoolFamily(
                    _deployments.PrimaryV3ProtocolId,
                    _deployments.UniswapV3Factory,
                    $"{_deployments.CatalogChainId}Rpc:{_deployments.PrimaryDexDisplayName}V3Factory",
                    _deployments.PrimaryDexDisplayName)
            }.Concat(_deployments.AdditionalV3Families);
            foreach (var quote in _deployments.QuoteAssets)
            {
                if (string.Equals(selectedToken, quote.Address, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                foreach (var family in families)
                {
                    foreach (var feeTier in family.FeeTiers ?? _deployments.UniswapV3FeeTiers)
                    {
                        try
                        {
                            var poolData = await ReadCallDataAsync(
                                family.FactoryAddress,
                                EthereumAbi.EncodeAddressPairFeeCall(
                                    EthereumAbi.GetPoolSelector,
                                    selectedToken,
                                    quote.Address,
                                    feeTier),
                                blockReference,
                                cancellationToken).ConfigureAwait(false);
                            if (!EthereumAbi.TryDecodeAddress(poolData, out var pool)
                                || string.Equals(pool, ZeroAddress, StringComparison.Ordinal))
                            {
                                continue;
                            }

                            pools.Add(await ValidateV3PoolAsync(
                                selectedToken,
                                quote.Address,
                                feeTier,
                                pool,
                                family.FactoryAddress,
                                family.ProtocolId,
                                family.DiscoverySource,
                                block,
                                blockReference,
                                decimals,
                                cancellationToken).ConfigureAwait(false));
                        }
                        catch (EvmJsonRpcException)
                        {
                            throw;
                        }
                        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
                        {
                            warnings.Add(
                                $"The {family.Generation ?? _deployments.PrimaryDexDisplayName} v3 {quote.Symbol} {feeTier} candidate failed validation: {exception.Message}");
                        }
                    }
                }
            }

            return new OnChainPoolDiscoveryResult
            {
                Mint = selectedToken,
                Pools = pools
                    .GroupBy(static pool => pool.PoolKey.PoolId, StringComparer.OrdinalIgnoreCase)
                    .Select(static group => group.First())
                    .ToArray(),
                Warnings = warnings.ToArray()
            };
        }

        private async Task<OnChainPoolDescriptor> ValidateV2PairAsync(
            string selectedToken,
            string quoteAddress,
            string pair,
            string expectedFactory,
            string protocolId,
            string discoverySource,
            EvmRpcBlockHeader block,
            object blockReference,
            Dictionary<string, byte?> decimals,
            CancellationToken cancellationToken)
        {
            if (!HasCode(await _rpc.GetCodeAsync(pair, blockReference, cancellationToken)
                    .ConfigureAwait(false)))
            {
                throw new InvalidOperationException("The factory-returned pair has no contract code.");
            }

            var factoryData = await ReadCallDataAsync(
                pair,
                EthereumAbi.FactorySelector,
                blockReference,
                cancellationToken).ConfigureAwait(false);
            if (!EthereumAbi.TryDecodeAddress(factoryData, out var factory)
                || !string.Equals(
                    factory,
                    expectedFactory,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The pair does not identify the expected v2 factory.");
            }

            var token0 = await ReadAddressCallAsync(
                pair,
                EthereumAbi.Token0Selector,
                blockReference,
                cancellationToken).ConfigureAwait(false);
            var token1 = await ReadAddressCallAsync(
                pair,
                EthereumAbi.Token1Selector,
                blockReference,
                cancellationToken).ConfigureAwait(false);
            var expectedTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                selectedToken,
                quoteAddress
            };
            if (expectedTokens.Count != 2 || !expectedTokens.SetEquals(new[] { token0, token1 }))
            {
                throw new InvalidOperationException("The pair token identities do not match the search.");
            }

            var confirmedPairData = await ReadCallDataAsync(
                expectedFactory,
                EthereumAbi.EncodeAddressPairCall(EthereumAbi.GetPairSelector, token0, token1),
                blockReference,
                cancellationToken).ConfigureAwait(false);
            if (!EthereumAbi.TryDecodeAddress(confirmedPairData, out var confirmedPair)
                || !string.Equals(pair, confirmedPair, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The factory mapping does not confirm this pair.");
            }

            var reservesData = await ReadCallDataAsync(
                pair,
                EthereumAbi.GetReservesSelector,
                blockReference,
                cancellationToken).ConfigureAwait(false);
            if (!EthereumAbi.TryDecodeUniswapV2Reserves(reservesData, out _, out _))
            {
                throw new InvalidOperationException("The pair returned malformed reserves.");
            }

            var token0Decimals = await GetDecimalsAsync(
                token0,
                blockReference,
                decimals,
                cancellationToken).ConfigureAwait(false);
            var token1Decimals = await GetDecimalsAsync(
                token1,
                blockReference,
                decimals,
                cancellationToken).ConfigureAwait(false);
            var selectedIsToken0 = string.Equals(selectedToken, token0, StringComparison.OrdinalIgnoreCase);
            var baseDecimals = selectedIsToken0 ? token0Decimals : token1Decimals;
            var quoteDecimals = selectedIsToken0 ? token1Decimals : token0Decimals;
            var supported = baseDecimals.HasValue && quoteDecimals.HasValue;

            return new OnChainPoolDescriptor
            {
                PoolKey = new OnChainPoolKey
                {
                    DeploymentKey = _deployments.Deployment(protocolId, expectedFactory),
                    PoolId = pair
                },
                PoolType = "constantProduct",
                ProgramId = expectedFactory,
                BaseMint = selectedToken,
                QuoteMint = quoteAddress,
                BaseDecimals = baseDecimals ?? 0,
                QuoteDecimals = quoteDecimals ?? 0,
                PairOrientation = selectedIsToken0 ? "selectedAsToken0" : "selectedAsToken1",
                DiscoverySource = discoverySource,
                SupportStatus = supported
                    ? OnChainSupportStatus.Supported
                    : OnChainSupportStatus.DiscoveredUnsupported,
                SupportReason = supported ? null : "ERC-20 decimals could not be read at the validation block.",
                Asset0 = Asset(token0),
                Asset1 = Asset(token1),
                ValidationBlock = block.Number,
                ValidationBlockHash = block.Hash
            };
        }

        private async Task<OnChainPoolDescriptor> ValidateV3PoolAsync(
            string selectedToken,
            string quoteAddress,
            uint? requestedFeeTier,
            string pool,
            string expectedFactory,
            string protocolId,
            string discoverySource,
            EvmRpcBlockHeader block,
            object blockReference,
            Dictionary<string, byte?> decimals,
            CancellationToken cancellationToken)
        {
            if (!HasCode(await _rpc.GetCodeAsync(pool, blockReference, cancellationToken)
                    .ConfigureAwait(false)))
            {
                throw new InvalidOperationException("The factory-returned pool has no contract code.");
            }
            var factory = await ReadAddressCallAsync(
                pool,
                EthereumAbi.FactorySelector,
                blockReference,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(factory, expectedFactory,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The pool does not identify the expected v3 factory.");
            }
            var token0 = await ReadAddressCallAsync(
                pool,
                EthereumAbi.Token0Selector,
                blockReference,
                cancellationToken).ConfigureAwait(false);
            var token1 = await ReadAddressCallAsync(
                pool,
                EthereumAbi.Token1Selector,
                blockReference,
                cancellationToken).ConfigureAwait(false);
            var expectedTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                selectedToken,
                quoteAddress
            };
            if (expectedTokens.Count != 2 || !expectedTokens.SetEquals(new[] { token0, token1 }))
            {
                throw new InvalidOperationException("The pool token identities do not match the search.");
            }

            var feeData = await ReadCallDataAsync(
                pool,
                EthereumAbi.FeeSelector,
                blockReference,
                cancellationToken).ConfigureAwait(false);
            if (!EthereumAbi.TryDecodeUnsigned(feeData, 0, 24, out var decodedFee)
                || decodedFee > uint.MaxValue
                || (requestedFeeTier is uint expectedFeeTier && decodedFee != expectedFeeTier))
            {
                throw new InvalidOperationException("The pool fee tier does not match the factory query.");
            }
            var feeTier = (uint)decodedFee;
            var tickSpacingData = await ReadCallDataAsync(
                pool,
                EthereumAbi.TickSpacingSelector,
                blockReference,
                cancellationToken).ConfigureAwait(false);
            if (!EthereumAbi.TryDecodeSigned(tickSpacingData, 0, 24, out var decodedTickSpacing)
                || decodedTickSpacing <= 0
                || decodedTickSpacing > int.MaxValue)
            {
                throw new InvalidOperationException("The pool tick spacing is invalid.");
            }

            var confirmedPoolData = await ReadCallDataAsync(
                expectedFactory,
                EthereumAbi.EncodeAddressPairFeeCall(
                    EthereumAbi.GetPoolSelector,
                    token0,
                    token1,
                    feeTier),
                blockReference,
                cancellationToken).ConfigureAwait(false);
            if (!EthereumAbi.TryDecodeAddress(confirmedPoolData, out var confirmedPool)
                || !string.Equals(pool, confirmedPool, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The factory mapping does not confirm this pool.");
            }

            var slot0Data = await ReadCallDataAsync(
                pool,
                EthereumAbi.Slot0Selector,
                blockReference,
                cancellationToken).ConfigureAwait(false);
            if (!EthereumAbi.TryDecodeUniswapV3Slot0(slot0Data, out _, out _))
            {
                throw new InvalidOperationException("The pool returned malformed slot0 state.");
            }
            var liquidityData = await ReadCallDataAsync(
                pool,
                EthereumAbi.LiquiditySelector,
                blockReference,
                cancellationToken).ConfigureAwait(false);
            if (!EthereumAbi.TryDecodeUnsigned(liquidityData, 0, 128, out _))
            {
                throw new InvalidOperationException("The pool returned malformed active liquidity.");
            }

            var token0Decimals = await GetDecimalsAsync(
                token0,
                blockReference,
                decimals,
                cancellationToken).ConfigureAwait(false);
            var token1Decimals = await GetDecimalsAsync(
                token1,
                blockReference,
                decimals,
                cancellationToken).ConfigureAwait(false);
            var selectedIsToken0 = string.Equals(selectedToken, token0, StringComparison.OrdinalIgnoreCase);
            var baseDecimals = selectedIsToken0 ? token0Decimals : token1Decimals;
            var quoteDecimals = selectedIsToken0 ? token1Decimals : token0Decimals;
            var supported = baseDecimals.HasValue && quoteDecimals.HasValue;

            return new OnChainPoolDescriptor
            {
                PoolKey = new OnChainPoolKey
                {
                    DeploymentKey = _deployments.Deployment(protocolId, expectedFactory),
                    PoolId = pool
                },
                PoolType = "concentratedLiquidity",
                ProgramId = expectedFactory,
                BaseMint = selectedToken,
                QuoteMint = quoteAddress,
                BaseDecimals = baseDecimals ?? 0,
                QuoteDecimals = quoteDecimals ?? 0,
                PairOrientation = selectedIsToken0 ? "selectedAsToken0" : "selectedAsToken1",
                DiscoverySource = discoverySource,
                SupportStatus = supported
                    ? OnChainSupportStatus.Supported
                    : OnChainSupportStatus.DiscoveredUnsupported,
                SupportReason = supported ? null : "ERC-20 decimals could not be read at the validation block.",
                Asset0 = Asset(token0),
                Asset1 = Asset(token1),
                ValidationBlock = block.Number,
                ValidationBlockHash = block.Hash,
                FeeTier = feeTier,
                TickSpacing = (int)decodedTickSpacing
            };
        }

        private async Task<OnChainPoolDescriptor> ValidateV4PoolAsync(
            string selectedToken,
            string quoteAddress,
            string poolId,
            long? createdAtUnixMs,
            string discoverySource,
            EvmRpcBlockHeader validationBlock,
            object blockReference,
            Dictionary<string, byte?> decimals,
            CancellationToken cancellationToken,
            string? knownInitializeTransactionHash = null)
        {
            if (!EvmAddress.IsHash(poolId) || createdAtUnixMs is not > 0)
            {
                throw new InvalidOperationException("The catalog did not provide a valid V4 PoolId and creation time.");
            }
            var transactionHash = knownInitializeTransactionHash
                                  ?? await _v4Lookup.FindInitializeTransactionAsync(
                                      poolId,
                                      createdAtUnixMs.Value,
                                      cancellationToken).ConfigureAwait(false);
            var receipt = await _rpc.GetTransactionReceiptAsync(transactionHash, cancellationToken)
                .ConfigureAwait(false);
            var initialize = ReadV4InitializeFromReceipt(
                receipt,
                poolId,
                out var initializedAtBlock,
                out var initializedAtBlockHash);
            if (initializedAtBlock > validationBlock.Number)
            {
                throw new InvalidOperationException("The V4 initialization is newer than the validation block.");
            }
            var canonicalBlock = await _rpc.GetBlockAsync(
                $"0x{initializedAtBlock:x}",
                cancellationToken).ConfigureAwait(false);
            if (!canonicalBlock.Hash.Equals(initializedAtBlockHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The V4 initialization is not in the canonical chain.");
            }

            var expectedCurrencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                selectedToken,
                quoteAddress
            };
            if (expectedCurrencies.Count != 2
                || !expectedCurrencies.SetEquals([initialize.Currency0, initialize.Currency1]))
            {
                throw new InvalidOperationException("The V4 pool currencies do not match the search.");
            }

            var slot0Data = await ReadCallDataAsync(
                _deployments.UniswapV4StateView,
                EthereumAbi.EncodeBytes32Call(
                    EthereumAbi.UniswapV4StateViewSlot0Selector,
                    poolId),
                blockReference,
                cancellationToken).ConfigureAwait(false);
            var liquidityData = await ReadCallDataAsync(
                _deployments.UniswapV4StateView,
                EthereumAbi.EncodeBytes32Call(
                    EthereumAbi.UniswapV4StateViewLiquiditySelector,
                    poolId),
                blockReference,
                cancellationToken).ConfigureAwait(false);
            if (!EthereumAbi.TryDecodeUniswapV4Slot0(slot0Data, out _, out _)
                || !EthereumAbi.TryDecodeUnsigned(liquidityData, 0, 128, out _))
            {
                throw new InvalidOperationException("The V4 StateView returned malformed pool state.");
            }

            var currency0Decimals = await GetDecimalsAsync(
                initialize.Currency0,
                blockReference,
                decimals,
                cancellationToken).ConfigureAwait(false);
            var currency1Decimals = await GetDecimalsAsync(
                initialize.Currency1,
                blockReference,
                decimals,
                cancellationToken).ConfigureAwait(false);
            var selectedIsCurrency0 = selectedToken.Equals(
                initialize.Currency0,
                StringComparison.OrdinalIgnoreCase);
            var baseDecimals = selectedIsCurrency0 ? currency0Decimals : currency1Decimals;
            var quoteDecimals = selectedIsCurrency0 ? currency1Decimals : currency0Decimals;
            var hookReturnsSwapDelta = EthereumAbi.UniswapV4HookReturnsSwapDelta(
                initialize.HookAddress);
            var requiresZeroHook = _chain.ChainId == EvmChainDefinitions.BaseMainnetChainId;
            var usesVerifiedRobinhoodSpotPricing = _chain.ChainId == EvmChainDefinitions.RobinhoodMainnetChainId
                                                   && (initialize.HookAddress.Equals(
                                                           RobinhoodDeploymentRegistry.DopplerHookInitializer,
                                                           StringComparison.OrdinalIgnoreCase)
                                                       || initialize.HookAddress.Equals(
                                                           RobinhoodDeploymentRegistry.PonsV2MemeHook,
                                                           StringComparison.OrdinalIgnoreCase));
            var hookUnsupported = requiresZeroHook
                ? !initialize.HookAddress.Equals(ZeroAddress, StringComparison.OrdinalIgnoreCase)
                : hookReturnsSwapDelta && !usesVerifiedRobinhoodSpotPricing;
            var supported = baseDecimals.HasValue
                            && quoteDecimals.HasValue
                            && !hookUnsupported;

            return new OnChainPoolDescriptor
            {
                PoolKey = new OnChainPoolKey
                {
                    DeploymentKey = _deployments.Deployment(
                        OnChainProtocolIds.UniswapV4,
                        _deployments.UniswapV4PoolManager),
                    PoolId = poolId
                },
                PoolType = "singletonConcentratedLiquidity",
                ProgramId = _deployments.UniswapV4PoolManager,
                BaseMint = selectedToken,
                QuoteMint = quoteAddress,
                BaseDecimals = baseDecimals ?? 0,
                QuoteDecimals = quoteDecimals ?? 0,
                PairOrientation = selectedIsCurrency0 ? "selectedAsToken0" : "selectedAsToken1",
                DiscoveredAtSlot = initializedAtBlock,
                DiscoverySource = discoverySource,
                SupportStatus = supported
                    ? OnChainSupportStatus.Supported
                    : OnChainSupportStatus.DiscoveredUnsupported,
                SupportReason = supported
                    ? null
                    : requiresZeroHook && hookUnsupported
                        ? "Base V4 support is restricted to the zero-hook address."
                        : hookReturnsSwapDelta
                        ? "This V4 hook can alter swap deltas, so exact last-trade pricing is not supported yet."
                        : "ERC-20 decimals could not be read at the validation block.",
                Asset0 = Asset(initialize.Currency0),
                Asset1 = Asset(initialize.Currency1),
                ValidationBlock = validationBlock.Number,
                ValidationBlockHash = validationBlock.Hash,
                FeeTier = initialize.Fee,
                TickSpacing = initialize.TickSpacing,
                HookAddress = initialize.HookAddress,
                PricingMode = usesVerifiedRobinhoodSpotPricing ? "spotOnly" : null
            };
        }

    }
}

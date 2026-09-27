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
        private async Task<OnChainPoolDiscoveryResult> DiscoverAerodromeBestEffortAsync(
            string tokenAddress,
            CancellationToken cancellationToken)
        {
            try
            {
                return await DiscoverAerodromeAsync(tokenAddress, cancellationToken).ConfigureAwait(false);
            }
            catch (EvmJsonRpcException exception)
            {
                return new OnChainPoolDiscoveryResult
                {
                    Mint = tokenAddress,
                    Warnings = [$"Aerodrome discovery was incomplete: {exception.Message}"]
                };
            }
        }

        internal async Task<OnChainPoolDiscoveryResult> DiscoverAerodromeAsync(
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
            var decimals = new Dictionary<string, byte?>(StringComparer.OrdinalIgnoreCase)
            {
                [selectedToken] = await TryReadDecimalsAsync(
                    selectedToken,
                    blockReference,
                    cancellationToken).ConfigureAwait(false)
            };
            var pools = new List<OnChainPoolDescriptor>();
            var warnings = new List<string>();

            if (_deployments.AerodromeClassicFactory is string classicFactory)
            {
                foreach (var quote in _deployments.QuoteAssets)
                {
                    if (selectedToken.Equals(quote.Address, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    foreach (var stable in new[] { false, true })
                    {
                        try
                        {
                            var pairData = await ReadCallDataAsync(
                                classicFactory,
                                EthereumAbi.EncodeAddressPairBooleanCall(
                                    EthereumAbi.AerodromeClassicGetPoolSelector,
                                    selectedToken,
                                    quote.Address,
                                    stable),
                                blockReference,
                                cancellationToken).ConfigureAwait(false);
                            if (!EthereumAbi.TryDecodeAddress(pairData, out var pair)
                                || pair.Equals(ZeroAddress, StringComparison.Ordinal))
                            {
                                continue;
                            }
                            pools.Add(await ValidateAerodromeClassicPoolAsync(
                                selectedToken,
                                quote.Address,
                                stable,
                                pair,
                                classicFactory,
                                $"{_deployments.CatalogChainId}Rpc:aerodromeClassicFactory",
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
                                $"The Aerodrome classic {quote.Symbol} {(stable ? "stable" : "volatile")} candidate failed validation: {exception.Message}");
                        }
                    }
                }
            }

            foreach (var family in _deployments.AerodromeSlipstreamFamilies)
            {
                foreach (var quote in _deployments.QuoteAssets)
                {
                    if (selectedToken.Equals(quote.Address, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    foreach (var tickSpacing in family.TickSpacings ?? [])
                    {
                        try
                        {
                            var poolData = await ReadCallDataAsync(
                                family.FactoryAddress,
                                EthereumAbi.EncodeAddressPairSignedCall(
                                    EthereumAbi.AerodromeSlipstreamGetPoolSelector,
                                    selectedToken,
                                    quote.Address,
                                    tickSpacing,
                                    24),
                                blockReference,
                                cancellationToken).ConfigureAwait(false);
                            if (!EthereumAbi.TryDecodeAddress(poolData, out var pool)
                                || pool.Equals(ZeroAddress, StringComparison.Ordinal))
                            {
                                continue;
                            }
                            pools.Add(await ValidateAerodromeSlipstreamPoolAsync(
                                selectedToken,
                                quote.Address,
                                tickSpacing,
                                pool,
                                family,
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
                                $"The Aerodrome Slipstream {family.Generation} {quote.Symbol} tick {tickSpacing} candidate failed validation: {exception.Message}");
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

        private async Task<OnChainPoolDescriptor> ValidateAerodromeClassicPoolAsync(
            string selectedToken,
            string quoteAddress,
            bool? requestedStable,
            string pool,
            string expectedFactory,
            string discoverySource,
            EvmRpcBlockHeader block,
            object blockReference,
            Dictionary<string, byte?> decimals,
            CancellationToken cancellationToken)
        {
            if (!HasCode(await _rpc.GetCodeAsync(pool, blockReference, cancellationToken)
                    .ConfigureAwait(false)))
            {
                throw new InvalidOperationException("The factory-returned Aerodrome pair has no contract code.");
            }
            var factory = await ReadAddressCallAsync(
                pool,
                EthereumAbi.FactorySelector,
                blockReference,
                cancellationToken).ConfigureAwait(false);
            if (!factory.Equals(expectedFactory, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The pair does not identify the expected Aerodrome classic factory.");
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
                throw new InvalidOperationException("The Aerodrome pair token identities do not match the search.");
            }
            var stableData = await ReadCallDataAsync(
                pool,
                EthereumAbi.StableSelector,
                blockReference,
                cancellationToken).ConfigureAwait(false);
            if (!EthereumAbi.TryDecodeBoolean(stableData, out var stable)
                || requestedStable.HasValue && stable != requestedStable.Value)
            {
                throw new InvalidOperationException("The Aerodrome pair curve does not match the factory query.");
            }
            var isPoolData = await ReadCallDataAsync(
                expectedFactory,
                EthereumAbi.EncodeAddressCall(EthereumAbi.IsPoolSelector, pool),
                blockReference,
                cancellationToken).ConfigureAwait(false);
            if (!EthereumAbi.TryDecodeBoolean(isPoolData, out var isPool) || !isPool)
            {
                throw new InvalidOperationException("The Aerodrome classic factory does not recognize this pair.");
            }
            var confirmedPoolData = await ReadCallDataAsync(
                expectedFactory,
                EthereumAbi.EncodeAddressPairBooleanCall(
                    EthereumAbi.AerodromeClassicGetPoolSelector,
                    token0,
                    token1,
                    stable),
                blockReference,
                cancellationToken).ConfigureAwait(false);
            if (!EthereumAbi.TryDecodeAddress(confirmedPoolData, out var confirmedPool)
                || !pool.Equals(confirmedPool, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The Aerodrome classic factory mapping does not confirm this pair.");
            }
            var reservesData = await ReadCallDataAsync(
                pool,
                EthereumAbi.GetReservesSelector,
                blockReference,
                cancellationToken).ConfigureAwait(false);
            if (!EthereumAbi.TryDecodeAerodromeClassicReserves(reservesData, out _, out _))
            {
                throw new InvalidOperationException("The Aerodrome pair returned malformed reserves.");
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
            var selectedIsToken0 = selectedToken.Equals(token0, StringComparison.OrdinalIgnoreCase);
            var baseDecimals = selectedIsToken0 ? token0Decimals : token1Decimals;
            var quoteDecimals = selectedIsToken0 ? token1Decimals : token0Decimals;
            var supported = baseDecimals.HasValue && quoteDecimals.HasValue;
            return new OnChainPoolDescriptor
            {
                PoolKey = new OnChainPoolKey
                {
                    DeploymentKey = _deployments.Deployment(OnChainProtocolIds.AerodromeClassic, expectedFactory),
                    PoolId = pool
                },
                PoolType = stable ? "stable" : "volatile",
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

        private async Task<OnChainPoolDescriptor> ValidateAerodromeSlipstreamPoolAsync(
            string selectedToken,
            string quoteAddress,
            int? requestedTickSpacing,
            string pool,
            EvmPoolFamily family,
            EvmRpcBlockHeader block,
            object blockReference,
            Dictionary<string, byte?> decimals,
            CancellationToken cancellationToken)
        {
            if (!HasCode(await _rpc.GetCodeAsync(pool, blockReference, cancellationToken)
                    .ConfigureAwait(false)))
            {
                throw new InvalidOperationException("The factory-returned Slipstream pool has no contract code.");
            }
            var factory = await ReadAddressCallAsync(
                pool,
                EthereumAbi.FactorySelector,
                blockReference,
                cancellationToken).ConfigureAwait(false);
            if (!factory.Equals(family.FactoryAddress, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The pool does not identify the expected Slipstream factory.");
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
                throw new InvalidOperationException("The Slipstream pool token identities do not match the search.");
            }
            var tickSpacingData = await ReadCallDataAsync(
                pool,
                EthereumAbi.TickSpacingSelector,
                blockReference,
                cancellationToken).ConfigureAwait(false);
            if (!EthereumAbi.TryDecodeSigned(tickSpacingData, 0, 24, out var decodedTickSpacing)
                || decodedTickSpacing <= 0
                || decodedTickSpacing > int.MaxValue)
            {
                throw new InvalidOperationException("The Slipstream pool tick spacing is invalid.");
            }
            var tickSpacing = (int)decodedTickSpacing;
            if (requestedTickSpacing.HasValue && tickSpacing != requestedTickSpacing.Value
                || family.TickSpacings == null
                || !family.TickSpacings.Contains(tickSpacing))
            {
                throw new InvalidOperationException("The Slipstream pool tick spacing is not allowlisted for this factory.");
            }
            var isPoolData = await ReadCallDataAsync(
                family.FactoryAddress,
                EthereumAbi.EncodeAddressCall(EthereumAbi.IsPoolSelector, pool),
                blockReference,
                cancellationToken).ConfigureAwait(false);
            if (!EthereumAbi.TryDecodeBoolean(isPoolData, out var isPool) || !isPool)
            {
                throw new InvalidOperationException("The Slipstream factory does not recognize this pool.");
            }
            var confirmedPoolData = await ReadCallDataAsync(
                family.FactoryAddress,
                EthereumAbi.EncodeAddressPairSignedCall(
                    EthereumAbi.AerodromeSlipstreamGetPoolSelector,
                    token0,
                    token1,
                    tickSpacing,
                    24),
                blockReference,
                cancellationToken).ConfigureAwait(false);
            if (!EthereumAbi.TryDecodeAddress(confirmedPoolData, out var confirmedPool)
                || !pool.Equals(confirmedPool, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The Slipstream factory mapping does not confirm this pool.");
            }
            var slot0Data = await ReadCallDataAsync(
                pool,
                EthereumAbi.Slot0Selector,
                blockReference,
                cancellationToken).ConfigureAwait(false);
            var liquidityData = await ReadCallDataAsync(
                pool,
                EthereumAbi.LiquiditySelector,
                blockReference,
                cancellationToken).ConfigureAwait(false);
            var dynamicFeeData = await ReadCallDataAsync(
                pool,
                EthereumAbi.FeeSelector,
                blockReference,
                cancellationToken).ConfigureAwait(false);
            if (!EthereumAbi.TryDecodeAerodromeSlipstreamSlot0(slot0Data, out _, out _)
                || !EthereumAbi.TryDecodeSingleUnsigned(liquidityData, 128, out _)
                || !EthereumAbi.TryDecodeSingleUnsigned(dynamicFeeData, 24, out _))
            {
                throw new InvalidOperationException("The Slipstream pool returned malformed state.");
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
            var selectedIsToken0 = selectedToken.Equals(token0, StringComparison.OrdinalIgnoreCase);
            var baseDecimals = selectedIsToken0 ? token0Decimals : token1Decimals;
            var quoteDecimals = selectedIsToken0 ? token1Decimals : token0Decimals;
            var supported = baseDecimals.HasValue && quoteDecimals.HasValue;
            return new OnChainPoolDescriptor
            {
                PoolKey = new OnChainPoolKey
                {
                    DeploymentKey = _deployments.Deployment(
                        OnChainProtocolIds.AerodromeSlipstream,
                        family.FactoryAddress),
                    PoolId = pool
                },
                PoolType = "concentratedLiquidity",
                ProgramId = family.FactoryAddress,
                BaseMint = selectedToken,
                QuoteMint = quoteAddress,
                BaseDecimals = baseDecimals ?? 0,
                QuoteDecimals = quoteDecimals ?? 0,
                PairOrientation = selectedIsToken0 ? "selectedAsToken0" : "selectedAsToken1",
                DiscoverySource = family.DiscoverySource,
                SupportStatus = supported
                    ? OnChainSupportStatus.Supported
                    : OnChainSupportStatus.DiscoveredUnsupported,
                SupportReason = supported ? null : "ERC-20 decimals could not be read at the validation block.",
                Asset0 = Asset(token0),
                Asset1 = Asset(token1),
                ValidationBlock = block.Number,
                ValidationBlockHash = block.Hash,
                TickSpacing = tickSpacing
            };
        }

    }
}

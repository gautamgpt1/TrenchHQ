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
        private async Task<OnChainPoolDiscoveryResult> DiscoverRobinhoodLaunchpadsBestEffortAsync(
            string tokenAddress,
            CancellationToken cancellationToken)
        {
            if (!EvmAddress.TryNormalize(tokenAddress, out var selectedToken))
            {
                throw new ArgumentException("Robinhood launchpad discovery requires a token contract address.", nameof(tokenAddress));
            }
            var pools = new List<OnChainPoolDescriptor>();
            var warnings = new List<string>();
            EvmRpcBlockHeader validationBlock;
            object blockReference;
            try
            {
                if (await _rpc.GetChainIdAsync(cancellationToken).ConfigureAwait(false) != 4663)
                {
                    throw new InvalidOperationException("The selected provider is not connected to Robinhood Chain mainnet.");
                }
                validationBlock = await _rpc.GetBlockAsync("latest", cancellationToken).ConfigureAwait(false);
                blockReference = new { blockHash = validationBlock.Hash, requireCanonical = true };
                if (!HasCode(await _rpc.GetCodeAsync(selectedToken, blockReference, cancellationToken)
                        .ConfigureAwait(false)))
                {
                    return new OnChainPoolDiscoveryResult { Mint = selectedToken };
                }
            }
            catch (Exception exception) when (exception is EvmJsonRpcException or InvalidOperationException)
            {
                return new OnChainPoolDiscoveryResult
                {
                    Mint = selectedToken,
                    Warnings = [$"Robinhood launchpad discovery was unavailable: {exception.Message}"]
                };
            }

            var decimals = new Dictionary<string, byte?>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var doppler = await TryDiscoverDopplerAsync(
                    selectedToken,
                    validationBlock,
                    blockReference,
                    decimals,
                    cancellationToken).ConfigureAwait(false);
                if (doppler != null)
                {
                    pools.Add(doppler);
                }
            }
            catch (Exception exception) when (exception is EvmJsonRpcException or InvalidOperationException or ArgumentException)
            {
                warnings.Add($"long.xyz discovery was incomplete: {exception.Message}");
            }
            try
            {
                var ponsV1 = await TryDiscoverPonsV1Async(
                    selectedToken,
                    validationBlock,
                    blockReference,
                    decimals,
                    cancellationToken).ConfigureAwait(false);
                if (ponsV1 != null)
                {
                    pools.Add(ponsV1);
                }
            }
            catch (Exception exception) when (exception is EvmJsonRpcException or InvalidOperationException or ArgumentException)
            {
                warnings.Add($"pons v1 discovery was incomplete: {exception.Message}");
            }
            try
            {
                var ponsV2 = await TryDiscoverPonsV2Async(
                    selectedToken,
                    validationBlock,
                    blockReference,
                    decimals,
                    cancellationToken).ConfigureAwait(false);
                if (ponsV2 != null)
                {
                    pools.Add(ponsV2);
                }
            }
            catch (Exception exception) when (exception is EvmJsonRpcException or InvalidOperationException or ArgumentException)
            {
                warnings.Add($"pons v2 discovery was incomplete: {exception.Message}");
            }
            return new OnChainPoolDiscoveryResult
            {
                Mint = selectedToken,
                Pools = pools.ToArray(),
                Warnings = warnings.ToArray()
            };
        }

        private async Task<OnChainPoolDescriptor?> TryDiscoverDopplerAsync(
            string selectedToken,
            EvmRpcBlockHeader validationBlock,
            object blockReference,
            Dictionary<string, byte?> decimals,
            CancellationToken cancellationToken)
        {
            var assetData = await ReadCallDataAsync(
                RobinhoodDeploymentRegistry.DopplerAirlock,
                EthereumAbi.EncodeAddressCall("0x1652e7b7", selectedToken),
                blockReference,
                cancellationToken).ConfigureAwait(false);
            if (!EthereumAbi.TryDecodeAddress(assetData, 0, out var numeraire)
                || !EthereumAbi.TryDecodeAddress(assetData, 4, out var initializer)
                || !EthereumAbi.TryDecodeAddress(assetData, 5, out var pool)
                || !initializer.Equals(RobinhoodDeploymentRegistry.DopplerHookInitializer, StringComparison.OrdinalIgnoreCase)
                || !pool.Equals(selectedToken, StringComparison.OrdinalIgnoreCase)
                || numeraire.Equals(selectedToken, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            var expectedCreateTopics = new[]
            {
                RobinhoodDeploymentRegistry.DopplerCreateTopic,
                TopicAddress(RobinhoodDeploymentRegistry.UniswapV4PoolManager),
                TopicAddress(selectedToken),
                TopicAddress(numeraire)
            };
            string? transactionHash;
            try
            {
                transactionHash = await _v4Lookup.FindContractCreationTransactionAsync(
                    selectedToken,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or HttpRequestException
                    or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                transactionHash = null;
            }
            if (transactionHash == null)
            {
                transactionHash = (await FindLaunchLogAsync(
                    RobinhoodDeploymentRegistry.DopplerHookInitializer,
                    expectedCreateTopics,
                    cancellationToken).ConfigureAwait(false))?.TransactionHash;
            }
            if (transactionHash == null)
            {
                throw new InvalidOperationException("The canonical Doppler creation event was not found.");
            }
            var receipt = await _rpc.GetTransactionReceiptAsync(transactionHash, cancellationToken)
                .ConfigureAwait(false);
            if (!ReceiptContainsExactLog(
                    receipt,
                    RobinhoodDeploymentRegistry.DopplerHookInitializer,
                    expectedCreateTopics))
            {
                throw new InvalidOperationException("The token creation transaction did not contain the canonical Doppler event.");
            }
            var initialize = ReadV4InitializeFromReceipt(
                receipt,
                selectedToken,
                numeraire,
                RobinhoodDeploymentRegistry.DopplerHookInitializer,
                out var initializedAtBlock,
                out var initializedAtBlockHash);
            var createdAt = await ValidateLaunchBlockAsync(
                initializedAtBlock,
                initializedAtBlockHash,
                cancellationToken).ConfigureAwait(false);
            return await ValidateV4PoolAsync(
                selectedToken,
                numeraire,
                initialize.PoolId,
                createdAt,
                "canonical:long.xyz:doppler-airlock",
                validationBlock,
                blockReference,
                decimals,
                cancellationToken,
                transactionHash).ConfigureAwait(false);
        }

        private async Task<OnChainPoolDescriptor?> TryDiscoverPonsV1Async(
            string selectedToken,
            EvmRpcBlockHeader validationBlock,
            object blockReference,
            Dictionary<string, byte?> decimals,
            CancellationToken cancellationToken)
        {
            var launch = await ReadCallDataAsync(
                RobinhoodDeploymentRegistry.PonsV1Factory,
                EthereumAbi.EncodeAddressCall(EthereumAbi.GetLaunchedTokenSelector, selectedToken),
                blockReference,
                cancellationToken).ConfigureAwait(false);
            if (!EthereumAbi.TryDecodeAddress(launch, 0, out var token)
                || !token.Equals(selectedToken, StringComparison.OrdinalIgnoreCase)
                || !EthereumAbi.TryDecodeAddress(launch, 2, out var pairedToken)
                || !EthereumAbi.TryDecodeUnsigned(launch, 10, 24, out var feeValue)
                || !EthereumAbi.TryDecodeUnsigned(launch, 11, 1, out var exists)
                || exists.IsZero
                || !uint.TryParse(feeValue.ToString(), out var feeTier))
            {
                return null;
            }
            var pool = await ReadAddressCallAsync(
                selectedToken,
                EthereumAbi.LiquidityPoolSelector,
                blockReference,
                cancellationToken).ConfigureAwait(false);
            return await ValidateV3PoolAsync(
                selectedToken,
                pairedToken,
                feeTier,
                pool,
                RobinhoodDeploymentRegistry.UniswapV3Factory,
                OnChainProtocolIds.UniswapV3,
                "canonical:pons-v1:factory",
                validationBlock,
                blockReference,
                decimals,
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<OnChainPoolDescriptor?> TryDiscoverPonsV2Async(
            string selectedToken,
            EvmRpcBlockHeader validationBlock,
            object blockReference,
            Dictionary<string, byte?> decimals,
            CancellationToken cancellationToken)
        {
            var launch = await ReadCallDataAsync(
                RobinhoodDeploymentRegistry.PonsV2Factory,
                EthereumAbi.EncodeAddressCall(EthereumAbi.GetLaunchedTokenSelector, selectedToken),
                blockReference,
                cancellationToken).ConfigureAwait(false);
            if (!EthereumAbi.TryDecodeAddress(launch, 0, out var token)
                || !token.Equals(selectedToken, StringComparison.OrdinalIgnoreCase)
                || !EthereumAbi.TryDecodeAddress(launch, 1, out var curve)
                || !EthereumAbi.TryDecodeAddress(launch, 4, out var pairToken)
                || !EthereumAbi.TryDecodeUnsigned(launch, 10, 8, out var phaseValue)
                || !EthereumAbi.TryDecodeUnsigned(launch, 14, 1, out var exists)
                || exists.IsZero
                || !byte.TryParse(phaseValue.ToString(), out var phase))
            {
                return null;
            }
            if (phase == 0)
            {
                if (!HasCode(await _rpc.GetCodeAsync(curve, blockReference, cancellationToken)
                        .ConfigureAwait(false)))
                {
                    throw new InvalidOperationException("The pons v2 curve has no code at the validation block.");
                }
                var reserves = await ReadCallDataAsync(
                    curve,
                    EthereumAbi.GetReservesSelector,
                    blockReference,
                    cancellationToken).ConfigureAwait(false);
                if (!EthereumAbi.TryDecodeUnsigned(reserves, 0, 256, out var quoteReserve)
                    || !EthereumAbi.TryDecodeUnsigned(reserves, 1, 256, out var tokenReserve)
                    || quoteReserve.IsZero
                    || tokenReserve.IsZero)
                {
                    throw new InvalidOperationException("The pons v2 curve returned malformed reserves.");
                }
                var baseDecimals = await GetDecimalsAsync(
                    selectedToken,
                    blockReference,
                    decimals,
                    cancellationToken).ConfigureAwait(false);
                var quoteDecimals = await GetDecimalsAsync(
                    pairToken,
                    blockReference,
                    decimals,
                    cancellationToken).ConfigureAwait(false);
                var supported = baseDecimals.HasValue && quoteDecimals.HasValue;
                return new OnChainPoolDescriptor
                {
                    PoolKey = new OnChainPoolKey
                    {
                        DeploymentKey = _deployments.Deployment(
                            OnChainProtocolIds.PonsV2Curve,
                            RobinhoodDeploymentRegistry.PonsV2Factory),
                        PoolId = curve
                    },
                    PoolType = "constantProductBondingCurve",
                    ProgramId = RobinhoodDeploymentRegistry.PonsV2Factory,
                    BaseMint = selectedToken,
                    QuoteMint = pairToken,
                    BaseDecimals = baseDecimals ?? 0,
                    QuoteDecimals = quoteDecimals ?? 0,
                    PairOrientation = "selectedAsBase",
                    DiscoverySource = "canonical:pons-v2:factory:phase-0",
                    SupportStatus = supported
                        ? OnChainSupportStatus.Supported
                        : OnChainSupportStatus.DiscoveredUnsupported,
                    SupportReason = supported ? null : "ERC-20 decimals could not be read at the validation block.",
                    Asset0 = Asset(selectedToken),
                    Asset1 = Asset(pairToken),
                    ValidationBlock = validationBlock.Number,
                    ValidationBlockHash = validationBlock.Hash
                };
            }
            if (phase == 1)
            {
                return CreatePonsV2PhaseDescriptor(
                    selectedToken,
                    pairToken,
                    curve,
                    phase,
                    validationBlock,
                    "Graduation reserves are swept; trading resumes after the permissionless V4 pool seed succeeds.");
            }
            if (phase == 3)
            {
                return CreatePonsV2PhaseDescriptor(
                    selectedToken,
                    pairToken,
                    curve,
                    phase,
                    validationBlock,
                    "This launch was rescued and has no canonical pons trading venue.");
            }
            if (phase != 2)
            {
                throw new InvalidOperationException("The pons v2 factory returned an unknown graduation phase.");
            }

            var graduation = await FindLaunchLogAsync(
                RobinhoodDeploymentRegistry.PonsV2Factory,
                [RobinhoodDeploymentRegistry.PonsV2PoolGraduatedTopic, TopicAddress(selectedToken)],
                cancellationToken).ConfigureAwait(false);
            if (graduation == null)
            {
                return CreatePonsV2PhaseDescriptor(
                    selectedToken,
                    pairToken,
                    curve,
                    phase,
                    validationBlock,
                    "The factory reports a graduated pool, but its canonical graduation event is temporarily unavailable.",
                    temporarilyUnavailable: true);
            }
            var receipt = await _rpc.GetTransactionReceiptAsync(graduation.TransactionHash, cancellationToken)
                .ConfigureAwait(false);
            var initialize = ReadV4InitializeFromReceipt(
                receipt,
                selectedToken,
                pairToken,
                RobinhoodDeploymentRegistry.PonsV2MemeHook,
                out var initializedAtBlock,
                out var initializedAtBlockHash);
            var createdAt = await ValidateLaunchBlockAsync(
                initializedAtBlock,
                initializedAtBlockHash,
                cancellationToken).ConfigureAwait(false);
            return await ValidateV4PoolAsync(
                selectedToken,
                pairToken,
                initialize.PoolId,
                createdAt,
                "canonical:pons-v2:factory:phase-2",
                validationBlock,
                blockReference,
                decimals,
                cancellationToken,
                graduation.TransactionHash).ConfigureAwait(false);
        }

        private OnChainPoolDescriptor CreatePonsV2PhaseDescriptor(
            string selectedToken,
            string pairToken,
            string curve,
            byte phase,
            EvmRpcBlockHeader validationBlock,
            string reason,
            bool temporarilyUnavailable = false)
        {
            return new OnChainPoolDescriptor
            {
                PoolKey = new OnChainPoolKey
                {
                    DeploymentKey = _deployments.Deployment(
                        OnChainProtocolIds.PonsV2Curve,
                        RobinhoodDeploymentRegistry.PonsV2Factory),
                    PoolId = curve
                },
                PoolType = "ponsV2Lifecycle",
                ProgramId = RobinhoodDeploymentRegistry.PonsV2Factory,
                BaseMint = selectedToken,
                QuoteMint = pairToken,
                PairOrientation = "selectedAsBase",
                DiscoverySource = $"canonical:pons-v2:factory:phase-{phase}",
                SupportStatus = temporarilyUnavailable
                    ? OnChainSupportStatus.TemporarilyUnavailable
                    : OnChainSupportStatus.DiscoveredUnsupported,
                SupportReason = reason,
                Asset0 = Asset(selectedToken),
                Asset1 = Asset(pairToken),
                ValidationBlock = validationBlock.Number,
                ValidationBlockHash = validationBlock.Hash
            };
        }

        private async Task<ObservedLaunchLog?> FindLaunchLogAsync(
            string contract,
            string[] topics,
            CancellationToken cancellationToken)
        {
            try
            {
                var explorerLog = await _v4Lookup.FindAddressLogAsync(
                    contract,
                    topics[^1],
                    topics,
                    cancellationToken).ConfigureAwait(false);
                if (explorerLog != null)
                {
                    return new ObservedLaunchLog(
                        explorerLog.TransactionHash,
                        explorerLog.BlockNumber,
                        explorerLog.BlockHash);
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or HttpRequestException
                    or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
            }

            var logs = await _rpc.GetLogsAsync(new
            {
                address = contract,
                fromBlock = "0x0",
                toBlock = "latest",
                topics
            }, cancellationToken).ConfigureAwait(false);
            if (logs.ValueKind != JsonValueKind.Array || logs.GetArrayLength() > 16)
            {
                throw new InvalidOperationException("A canonical launch event query returned a malformed response.");
            }
            foreach (var log in logs.EnumerateArray().Reverse())
            {
                if (TryReadHash(log, "transactionHash", out var transactionHash)
                    && TryReadQuantity(log, "blockNumber", out var blockNumber)
                    && TryReadHash(log, "blockHash", out var blockHash))
                {
                    return new ObservedLaunchLog(transactionHash, blockNumber, blockHash);
                }
            }
            return null;
        }

        private async Task<long> ValidateLaunchBlockAsync(
            ulong blockNumber,
            string expectedHash,
            CancellationToken cancellationToken)
        {
            var block = await _rpc.GetBlockAsync($"0x{blockNumber:x}", cancellationToken)
                .ConfigureAwait(false);
            if (!block.Hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The launch event is not in the canonical chain.");
            }
            return checked((long)block.Timestamp * 1000);
        }

        private static string TopicAddress(string address)
        {
            if (!EvmAddress.TryNormalize(address, out var normalized))
            {
                throw new ArgumentException("An indexed launch address is invalid.", nameof(address));
            }
            return "0x" + normalized[2..].PadLeft(64, '0');
        }

        private static bool ReceiptContainsExactLog(
            JsonElement receipt,
            string contract,
            IReadOnlyList<string> expectedTopics)
        {
            if (receipt.ValueKind != JsonValueKind.Object
                || !receipt.TryGetProperty("logs", out var logs)
                || logs.ValueKind != JsonValueKind.Array)
            {
                return false;
            }
            return logs.EnumerateArray().Any(log =>
                log.ValueKind == JsonValueKind.Object
                && log.TryGetProperty("address", out var address)
                && address.ValueKind == JsonValueKind.String
                && address.GetString()?.Equals(contract, StringComparison.OrdinalIgnoreCase) == true
                && log.TryGetProperty("topics", out var topics)
                && topics.ValueKind == JsonValueKind.Array
                && topics.GetArrayLength() == expectedTopics.Count
                && topics.EnumerateArray().Select(static topic => topic.GetString())
                    .SequenceEqual(expectedTopics, StringComparer.OrdinalIgnoreCase));
        }

    }
}

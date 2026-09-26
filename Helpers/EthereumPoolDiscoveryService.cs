using TrenchHQ.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrenchHQ.Helpers
{
    internal sealed class EthereumPoolDiscoveryService
    {
        private const string ZeroAddress = "0x0000000000000000000000000000000000000000";
        private static readonly HttpClient SharedHttpClient = new();

        private readonly EvmJsonRpcClient _rpc;
        private readonly EvmChainDefinition _chain;
        private readonly EvmDeploymentCatalog _deployments;
        private readonly EthereumV4PoolLookupClient _v4Lookup;

        internal EthereumPoolDiscoveryService(EvmJsonRpcClient rpc, HttpClient? httpClient = null)
            : this(
                rpc,
                EvmChainDefinitions.EthereumMainnet,
                EthereumDeploymentRegistry.Catalog,
                httpClient)
        {
        }

        internal EthereumPoolDiscoveryService(
            EvmJsonRpcClient rpc,
            EvmChainDefinition chain,
            EvmDeploymentCatalog deployments,
            HttpClient? httpClient = null)
        {
            _rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));
            _chain = chain ?? throw new ArgumentNullException(nameof(chain));
            _deployments = deployments ?? throw new ArgumentNullException(nameof(deployments));
            if (_chain.ChainId != _deployments.ChainId)
            {
                throw new ArgumentException("The chain and deployment catalog do not match.", nameof(deployments));
            }
            _v4Lookup = new EthereumV4PoolLookupClient(
                httpClient ?? SharedHttpClient,
                _chain.BlockscoutApiRoot,
                _deployments.UniswapV4PoolManager,
                _chain.DisplayName,
                _rpc,
                _chain.ChainId == EvmChainDefinitions.RobinhoodMainnetChainId ? 10m : null);
        }

        internal async Task<OnChainPoolDiscoveryResult> DiscoverAsync(
            string tokenAddress,
            CancellationToken cancellationToken = default)
        {
            return await DiscoverAsync(tokenAddress, null, cancellationToken).ConfigureAwait(false);
        }

        internal async Task<OnChainPoolDiscoveryResult> DiscoverAsync(
            string tokenAddress,
            IReadOnlyCollection<PoolCatalogEntry>? catalogPools,
            CancellationToken cancellationToken = default)
        {
            var v2Task = DiscoverUniswapV2Async(tokenAddress, cancellationToken);
            var v3Task = DiscoverUniswapV3Async(tokenAddress, cancellationToken);
            var aerodromeTask = _deployments.AerodromeClassicFactory != null
                                || _deployments.AerodromeSlipstreamFamilies.Length > 0
                ? DiscoverAerodromeBestEffortAsync(tokenAddress, cancellationToken)
                : Task.FromResult(new OnChainPoolDiscoveryResult { Mint = tokenAddress });
            var catalogTask = catalogPools is { Count: > 0 }
                ? DiscoverCatalogPoolsSharedAsync(tokenAddress, catalogPools, cancellationToken)
                : Task.FromResult(new OnChainPoolDiscoveryResult { Mint = tokenAddress });
            var fermiTask = _chain.ChainId == EvmChainDefinitions.EthereumMainnetChainId
                ? DiscoverFermiBestEffortAsync(tokenAddress, cancellationToken)
                : Task.FromResult(new OnChainPoolDiscoveryResult { Mint = tokenAddress });
            var robinhoodLaunchpadTask = _chain.ChainId == EvmChainDefinitions.RobinhoodMainnetChainId
                ? DiscoverRobinhoodLaunchpadsBestEffortAsync(tokenAddress, cancellationToken)
                : Task.FromResult(new OnChainPoolDiscoveryResult { Mint = tokenAddress });
            await Task.WhenAll(v2Task, v3Task, aerodromeTask, catalogTask, fermiTask, robinhoodLaunchpadTask)
                .ConfigureAwait(false);
            var v2 = await v2Task.ConfigureAwait(false);
            var v3 = await v3Task.ConfigureAwait(false);
            var aerodrome = await aerodromeTask.ConfigureAwait(false);
            var catalog = await catalogTask.ConfigureAwait(false);
            var fermi = await fermiTask.ConfigureAwait(false);
            var robinhoodLaunchpads = await robinhoodLaunchpadTask.ConfigureAwait(false);
            return new OnChainPoolDiscoveryResult
            {
                Mint = v2.Mint,
                Pools = robinhoodLaunchpads.Pools.Concat(v2.Pools).Concat(v3.Pools)
                    .Concat(aerodrome.Pools).Concat(catalog.Pools)
                    .Concat(fermi.Pools)
                    .GroupBy(static pool => pool.PoolKey.PoolId, StringComparer.OrdinalIgnoreCase)
                    .Select(static group => group.First())
                    .ToArray(),
                Warnings = v2.Warnings.Concat(v3.Warnings).Concat(aerodrome.Warnings)
                    .Concat(catalog.Warnings).Concat(fermi.Warnings)
                    .Concat(robinhoodLaunchpads.Warnings).ToArray()
            };
        }

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

        private async Task<OnChainPoolDiscoveryResult> DiscoverFermiBestEffortAsync(
            string tokenAddress,
            CancellationToken cancellationToken)
        {
            try
            {
                return await DiscoverFermiAsync(tokenAddress, cancellationToken).ConfigureAwait(false);
            }
            catch (EvmJsonRpcException exception)
            {
                return new OnChainPoolDiscoveryResult
                {
                    Mint = tokenAddress,
                    Warnings = [$"FermiSwap discovery was incomplete: {exception.Message}"]
                };
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                return new OnChainPoolDiscoveryResult
                {
                    Mint = tokenAddress,
                    Warnings = [$"FermiSwap discovery timed out: {exception.Message}"]
                };
            }
            catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException)
            {
                return new OnChainPoolDiscoveryResult
                {
                    Mint = tokenAddress,
                    Warnings = [$"FermiSwap discovery was unavailable: {exception.Message}"]
                };
            }
        }

        internal async Task<OnChainPoolDiscoveryResult> DiscoverFermiAsync(
            string tokenAddress,
            CancellationToken cancellationToken = default)
        {
            if (_chain.ChainId != EvmChainDefinitions.EthereumMainnetChainId
                || !EvmAddress.TryNormalize(tokenAddress, out var selectedToken))
            {
                throw new ArgumentException("FermiSwap discovery requires an Ethereum token contract address.", nameof(tokenAddress));
            }
            if (await _rpc.GetChainIdAsync(cancellationToken).ConfigureAwait(false)
                != ulong.Parse(_chain.ChainId, System.Globalization.CultureInfo.InvariantCulture))
            {
                throw new InvalidOperationException("The selected provider is not connected to Ethereum mainnet.");
            }

            var block = await _rpc.GetBlockAsync("latest", cancellationToken).ConfigureAwait(false);
            var blockReference = new { blockHash = block.Hash, requireCanonical = true };
            if (!HasCode(await _rpc.GetCodeAsync(
                    selectedToken,
                    blockReference,
                    cancellationToken).ConfigureAwait(false)))
            {
                throw new InvalidOperationException("The Ethereum address has no contract code at the validation block.");
            }
            var decimals = new Dictionary<string, byte?>(StringComparer.OrdinalIgnoreCase)
            {
                [selectedToken] = await TryReadDecimalsAsync(
                    selectedToken,
                    blockReference,
                    cancellationToken).ConfigureAwait(false)
            };
            var warnings = new List<string>();
            var observed = new List<FermiObservedPair>();
            var fromBlock = block.Number >= 99 ? block.Number - 99 : 0;
            foreach (var deployment in FermiDeployments)
            {
                try
                {
                    if (!HasCode(await _rpc.GetCodeAsync(
                            deployment.Address,
                            blockReference,
                            cancellationToken).ConfigureAwait(false)))
                    {
                        warnings.Add($"FermiSwap contract {deployment.Address} has no code at the validation block.");
                        continue;
                    }
                    var result = await _rpc.GetLogsAsync(new
                    {
                        fromBlock = $"0x{fromBlock:x}",
                        toBlock = $"0x{block.Number:x}",
                        address = deployment.Address,
                        topics = new[] { deployment.Topic }
                    }, cancellationToken).ConfigureAwait(false);
                    if (result.ValueKind != JsonValueKind.Array)
                    {
                        throw new InvalidOperationException("FermiSwap log discovery returned a malformed response.");
                    }
                    foreach (var log in result.EnumerateArray())
                    {
                        if (TryReadFermiPair(log, deployment, selectedToken, out var pair))
                        {
                            observed.Add(pair);
                        }
                    }
                }
                catch (EvmJsonRpcException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
                {
                    warnings.Add($"FermiSwap contract {deployment.Address} could not be inspected: {exception.Message}");
                }
            }

            var pools = new List<OnChainPoolDescriptor>();
            foreach (var pair in observed
                         .GroupBy(static pair => $"{pair.Contract}:{pair.Counterpart}", StringComparer.OrdinalIgnoreCase)
                         .Select(static group => group.OrderByDescending(pair => pair.BlockNumber).First())
                         .OrderBy(pair => FermiQuoteRank(pair.Counterpart))
                         .ThenByDescending(static pair => pair.BlockNumber)
                         .Take(30))
            {
                if (!HasCode(await _rpc.GetCodeAsync(
                        pair.Counterpart,
                        blockReference,
                        cancellationToken).ConfigureAwait(false)))
                {
                    continue;
                }
                var quoteDecimals = await GetDecimalsAsync(
                    pair.Counterpart,
                    blockReference,
                    decimals,
                    cancellationToken).ConfigureAwait(false);
                var baseDecimals = decimals[selectedToken];
                var supported = baseDecimals != null && quoteDecimals != null;
                pools.Add(new OnChainPoolDescriptor
                {
                    PoolKey = new OnChainPoolKey
                    {
                        DeploymentKey = _deployments.Deployment(OnChainProtocolIds.FermiSwap, pair.Contract),
                        PoolId = CreateFermiPairId(pair.Contract, selectedToken, pair.Counterpart)
                    },
                    PoolType = "proprietaryInventoryExecutedTrades",
                    ProgramId = pair.Contract,
                    BaseMint = selectedToken,
                    QuoteMint = pair.Counterpart,
                    BaseDecimals = baseDecimals ?? 0,
                    QuoteDecimals = quoteDecimals ?? 0,
                    PairOrientation = "selectedAsBase",
                    DiscoverySource = "ethereumRpc:recentFermiSwapExecutions",
                    SupportStatus = supported
                        ? OnChainSupportStatus.Supported
                        : OnChainSupportStatus.DiscoveredUnsupported,
                    SupportReason = supported ? null : "ERC-20 decimals could not be read at the validation block.",
                    Asset0 = Asset(selectedToken),
                    Asset1 = Asset(pair.Counterpart),
                    ValidationBlock = block.Number,
                    ValidationBlockHash = block.Hash
                });
            }
            return new OnChainPoolDiscoveryResult
            {
                Mint = selectedToken,
                Pools = pools.ToArray(),
                Warnings = warnings.ToArray()
            };
        }

        private static bool TryReadFermiPair(
            JsonElement log,
            FermiDeployment deployment,
            string selectedToken,
            out FermiObservedPair pair)
        {
            pair = default!;
            if (log.ValueKind != JsonValueKind.Object
                || !log.TryGetProperty("address", out var addressElement)
                || addressElement.ValueKind != JsonValueKind.String
                || !EvmAddress.TryNormalize(addressElement.GetString(), out var address)
                || !address.Equals(deployment.Address, StringComparison.OrdinalIgnoreCase)
                || !log.TryGetProperty("topics", out var topics)
                || topics.ValueKind != JsonValueKind.Array
                || topics.GetArrayLength() != 4
                || topics[0].ValueKind != JsonValueKind.String
                || topics[2].ValueKind != JsonValueKind.String
                || topics[3].ValueKind != JsonValueKind.String
                || !string.Equals(topics[0].GetString(), deployment.Topic, StringComparison.OrdinalIgnoreCase)
                || !EthereumAbi.TryDecodeAddress(topics[2].GetString(), out var tokenIn)
                || !EthereumAbi.TryDecodeAddress(topics[3].GetString(), out var tokenOut)
                || tokenIn.Equals(tokenOut, StringComparison.OrdinalIgnoreCase)
                || !log.TryGetProperty("data", out var dataElement)
                || dataElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            var data = dataElement.GetString();
            if (data?.Length != 2 + deployment.DataWords * 64
                || !EthereumAbi.TryDecodeUnsigned(data, 0, 256, out var amountIn)
                || !EthereumAbi.TryDecodeUnsigned(data, 1, 256, out var amountOut)
                || amountIn.IsZero
                || amountOut.IsZero
                || deployment.DataWords == 3
                && !EthereumAbi.TryDecodeAddress("0x" + data.Substring(2 + 128, 64), out _)
                || !TryReadQuantity(log, "blockNumber", out var blockNumber))
            {
                return false;
            }
            var counterpart = tokenIn.Equals(selectedToken, StringComparison.OrdinalIgnoreCase)
                ? tokenOut
                : tokenOut.Equals(selectedToken, StringComparison.OrdinalIgnoreCase)
                    ? tokenIn
                    : string.Empty;
            if (counterpart.Length == 0)
            {
                return false;
            }
            pair = new FermiObservedPair(deployment.Address, counterpart, blockNumber);
            return true;
        }

        private static string CreateFermiPairId(string contract, string baseToken, string quoteToken)
        {
            var tokens = new[] { baseToken.ToLowerInvariant(), quoteToken.ToLowerInvariant() };
            Array.Sort(tokens, StringComparer.Ordinal);
            var identity = $"{contract.ToLowerInvariant()}:{tokens[0]}:{tokens[1]}";
            return "0x" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
                .ToLowerInvariant();
        }

        private static int FermiQuoteRank(string address)
        {
            var rank = Array.FindIndex(EthereumDeploymentRegistry.MainnetQuoteAssets, quote =>
                quote.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
            return rank < 0 ? int.MaxValue : rank;
        }

        private static readonly FermiDeployment[] FermiDeployments =
        [
            new(
                EthereumDeploymentRegistry.FermiLegacySwapper,
                EvmWebSocketStreamSource.FermiSwapTopic,
                2),
            new(
                EthereumDeploymentRegistry.FermiCurrentSwapper,
                EvmWebSocketStreamSource.FermiSwappedTopic,
                3)
        ];

        private sealed record FermiDeployment(string Address, string Topic, int DataWords);
        private sealed record FermiObservedPair(string Contract, string Counterpart, ulong BlockNumber);

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

        private async Task<byte?> GetDecimalsAsync(
            string token,
            object blockReference,
            Dictionary<string, byte?> cache,
            CancellationToken cancellationToken)
        {
            if (token.Equals(_deployments.NativeAsset, StringComparison.OrdinalIgnoreCase))
            {
                return 18;
            }
            if (!cache.TryGetValue(token, out var decimals))
            {
                decimals = await TryReadDecimalsAsync(token, blockReference, cancellationToken)
                    .ConfigureAwait(false);
                cache[token] = decimals;
            }
            return decimals;
        }

        private async Task<byte?> TryReadDecimalsAsync(
            string token,
            object blockReference,
            CancellationToken cancellationToken)
        {
            try
            {
                var data = await ReadCallDataAsync(
                    token,
                    EthereumAbi.DecimalsSelector,
                    blockReference,
                    cancellationToken).ConfigureAwait(false);
                return EthereumAbi.TryDecodeByte(data, out var decimals) ? decimals : null;
            }
            catch (EvmJsonRpcException exception) when (exception.Kind == EvmRpcFailureKind.RpcError)
            {
                return null;
            }
        }

        private async Task<string> ReadAddressCallAsync(
            string contract,
            string selector,
            object blockReference,
            CancellationToken cancellationToken)
        {
            var data = await ReadCallDataAsync(contract, selector, blockReference, cancellationToken)
                .ConfigureAwait(false);
            if (!EthereumAbi.TryDecodeAddress(data, out var address))
            {
                throw new InvalidOperationException("An Ethereum contract returned a malformed address.");
            }
            return address;
        }

        private async Task<string> ReadCallDataAsync(
            string contract,
            string callData,
            object blockReference,
            CancellationToken cancellationToken)
        {
            var result = await _rpc.CallAsync(contract, callData, blockReference, cancellationToken)
                .ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.String || !EvmAddress.IsData(result.GetString(), 4096))
            {
                throw new InvalidOperationException("An Ethereum contract call returned malformed data.");
            }
            return result.GetString()!.ToLowerInvariant();
        }

        private static bool HasCode(string code)
        {
            return code.Length > 2 && code.AsSpan(2).ContainsAnyExcept('0');
        }

        private OnChainAssetKey Asset(string address)
        {
            return new OnChainAssetKey
            {
                ChainNamespace = ChainNamespaces.Eip155,
                ChainId = _chain.ChainId,
                Address = address
            };
        }

        private EthereumV4Initialize ReadV4InitializeFromReceipt(
            JsonElement receipt,
            string firstCurrency,
            string secondCurrency,
            string hookAddress,
            out ulong blockNumber,
            out string blockHash)
        {
            blockNumber = 0;
            blockHash = string.Empty;
            if (receipt.ValueKind != JsonValueKind.Object
                || !TryReadQuantity(receipt, "status", out var status)
                || status != 1
                || !TryReadQuantity(receipt, "blockNumber", out blockNumber)
                || !TryReadHash(receipt, "blockHash", out blockHash)
                || !receipt.TryGetProperty("logs", out var logs)
                || logs.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("The V4 initialization transaction receipt is malformed.");
            }
            var expectedCurrencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                firstCurrency,
                secondCurrency
            };
            foreach (var log in logs.EnumerateArray())
            {
                if (log.ValueKind != JsonValueKind.Object
                    || !log.TryGetProperty("address", out var addressElement)
                    || addressElement.ValueKind != JsonValueKind.String
                    || !EvmAddress.TryNormalize(addressElement.GetString(), out var address)
                    || !address.Equals(_deployments.UniswapV4PoolManager, StringComparison.OrdinalIgnoreCase)
                    || !log.TryGetProperty("topics", out var topicsElement)
                    || topicsElement.ValueKind != JsonValueKind.Array
                    || !log.TryGetProperty("data", out var dataElement)
                    || dataElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                var topics = topicsElement.EnumerateArray()
                    .Where(static topic => topic.ValueKind == JsonValueKind.String)
                    .Select(static topic => topic.GetString() ?? string.Empty)
                    .ToArray();
                if (EthereumAbi.TryDecodeUniswapV4Initialize(topics, dataElement.GetString(), out var initialize)
                    && expectedCurrencies.SetEquals([initialize.Currency0, initialize.Currency1])
                    && initialize.HookAddress.Equals(hookAddress, StringComparison.OrdinalIgnoreCase))
                {
                    return initialize;
                }
            }
            throw new InvalidOperationException("The transaction did not contain the expected canonical V4 initialization event.");
        }

        private EthereumV4Initialize ReadV4InitializeFromReceipt(
            JsonElement receipt,
            string poolId,
            out ulong blockNumber,
            out string blockHash)
        {
            blockNumber = 0;
            blockHash = string.Empty;
            if (receipt.ValueKind != JsonValueKind.Object
                || !TryReadQuantity(receipt, "status", out var status)
                || status != 1
                || !TryReadQuantity(receipt, "blockNumber", out blockNumber)
                || !TryReadHash(receipt, "blockHash", out blockHash)
                || !receipt.TryGetProperty("logs", out var logs)
                || logs.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("The V4 initialization transaction receipt is malformed.");
            }
            foreach (var log in logs.EnumerateArray())
            {
                if (log.ValueKind != JsonValueKind.Object
                    || !log.TryGetProperty("address", out var addressElement)
                    || addressElement.ValueKind != JsonValueKind.String
                    || !EvmAddress.TryNormalize(addressElement.GetString(), out var address)
                    || !address.Equals(
                        _deployments.UniswapV4PoolManager,
                        StringComparison.OrdinalIgnoreCase)
                    || !log.TryGetProperty("topics", out var topicsElement)
                    || topicsElement.ValueKind != JsonValueKind.Array
                    || !log.TryGetProperty("data", out var dataElement)
                    || dataElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                var topics = topicsElement.EnumerateArray()
                    .Where(static topic => topic.ValueKind == JsonValueKind.String)
                    .Select(static topic => topic.GetString() ?? string.Empty)
                    .ToArray();
                if (EthereumAbi.TryDecodeUniswapV4Initialize(
                        topics,
                        dataElement.GetString(),
                        out var initialize)
                    && initialize.PoolId.Equals(poolId, StringComparison.OrdinalIgnoreCase))
                {
                    return initialize;
                }
            }
            throw new InvalidOperationException("The transaction did not contain the expected V4 initialization event.");
        }

        private static bool TryReadQuantity(JsonElement parent, string propertyName, out ulong value)
        {
            value = 0;
            return parent.TryGetProperty(propertyName, out var element)
                   && element.ValueKind == JsonValueKind.String
                   && EvmAddress.TryParseQuantity(element.GetString(), out value);
        }

        private static bool TryReadHash(JsonElement parent, string propertyName, out string value)
        {
            value = string.Empty;
            if (!parent.TryGetProperty(propertyName, out var element)
                || element.ValueKind != JsonValueKind.String
                || !EvmAddress.IsHash(element.GetString()))
            {
                return false;
            }
            value = element.GetString()!.ToLowerInvariant();
            return true;
        }

        private sealed record ObservedLaunchLog(
            string TransactionHash,
            ulong BlockNumber,
            string BlockHash);
    }
}

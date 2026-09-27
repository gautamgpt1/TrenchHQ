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
                EvmEventTopics.FermiSwapTopic,
                2),
            new(
                EthereumDeploymentRegistry.FermiCurrentSwapper,
                EvmEventTopics.FermiSwappedTopic,
                3)
        ];

        private sealed record FermiDeployment(string Address, string Topic, int DataWords);
        private sealed record FermiObservedPair(string Contract, string Counterpart, ulong BlockNumber);

    }
}

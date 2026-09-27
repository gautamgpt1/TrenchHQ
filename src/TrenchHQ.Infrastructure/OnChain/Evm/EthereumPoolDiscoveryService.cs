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

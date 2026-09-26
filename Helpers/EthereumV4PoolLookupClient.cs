using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrenchHQ.Helpers
{
    internal sealed class EthereumV4PoolLookupClient
    {
        private const int MaximumResponseBytes = 512 * 1024;
        private const int MaximumBlockExplorerAttempts = 2;
        private const ulong MaximumRpcLogBlockCount = 10;
        private static readonly TimeSpan MinimumBlockExplorerRequestInterval = TimeSpan.FromMilliseconds(300);
        private readonly HttpClient _httpClient;
        private readonly string _apiRoot;
        private readonly string _poolManager;
        private readonly string _chainDisplayName;
        private readonly EvmJsonRpcClient? _rpc;
        private readonly decimal? _estimatedBlocksPerSecond;
        private readonly SemaphoreSlim _blockExplorerGate = new(1, 1);
        private DateTimeOffset _nextBlockExplorerRequestAt;
        private DateTimeOffset _blockExplorerV1UnavailableUntil;

        internal EthereumV4PoolLookupClient(HttpClient httpClient)
            : this(
                httpClient,
                EvmChainDefinitions.EthereumMainnet.BlockscoutApiRoot,
                EthereumDeploymentRegistry.UniswapV4PoolManager,
                EvmChainDefinitions.EthereumMainnet.DisplayName,
                null,
                null)
        {
        }

        internal EthereumV4PoolLookupClient(
            HttpClient httpClient,
            string apiRoot,
            string poolManager,
            string chainDisplayName,
            EvmJsonRpcClient? rpc,
            decimal? estimatedBlocksPerSecond = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _apiRoot = apiRoot ?? throw new ArgumentNullException(nameof(apiRoot));
            _poolManager = poolManager ?? throw new ArgumentNullException(nameof(poolManager));
            _chainDisplayName = chainDisplayName ?? throw new ArgumentNullException(nameof(chainDisplayName));
            _rpc = rpc;
            _estimatedBlocksPerSecond = estimatedBlocksPerSecond;
        }

        internal async Task<string> FindInitializeTransactionAsync(
            string poolId,
            long createdAtUnixMs,
            CancellationToken cancellationToken)
        {
            if (!EvmAddress.IsHash(poolId) || createdAtUnixMs <= 0)
            {
                throw new ArgumentException("The Uniswap v4 catalog identity is incomplete.");
            }

            try
            {
                return await FindWithBlockExplorerAsync(
                    poolId,
                    createdAtUnixMs / 1000,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (BlockExplorerLogLookupException exception) when (_rpc != null)
            {
                return await FindWithRpcAsync(
                    poolId,
                    (ulong)(createdAtUnixMs / 1000),
                    cancellationToken,
                    exception.BlockNumber).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                _rpc != null
                && (exception is InvalidOperationException
                    || exception is HttpRequestException
                    || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                return await FindWithRpcAsync(
                    poolId,
                    (ulong)(createdAtUnixMs / 1000),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        internal async Task<string?> FindContractCreationTransactionAsync(
            string contract,
            CancellationToken cancellationToken)
        {
            if (!EvmAddress.TryNormalize(contract, out var normalizedContract))
            {
                throw new ArgumentException("The contract address is invalid.", nameof(contract));
            }

            using var document = await GetAsync(
                $"{_apiRoot.TrimEnd('/')}/v2/addresses/{normalizedContract}",
                cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    $"The {_chainDisplayName} contract lookup returned malformed results.");
            }
            if (document.RootElement.TryGetProperty("creation_transaction_hash", out var transactionHash)
                && transactionHash.ValueKind == JsonValueKind.String
                && EvmAddress.IsHash(transactionHash.GetString()))
            {
                return transactionHash.GetString()!.ToLowerInvariant();
            }
            return null;
        }

        internal async Task<EvmIndexedLog?> FindAddressLogAsync(
            string contract,
            string indexedTopic,
            IReadOnlyList<string> expectedTopics,
            CancellationToken cancellationToken)
        {
            if (!EvmAddress.TryNormalize(contract, out var normalizedContract)
                || !EvmAddress.IsHash(indexedTopic)
                || expectedTopics.Count is < 1 or > 4
                || expectedTopics.Any(topic => !EvmAddress.IsHash(topic)))
            {
                throw new ArgumentException("The indexed event lookup is incomplete.");
            }

            using var document = await GetAsync(
                $"{_apiRoot.TrimEnd('/')}/v2/addresses/{normalizedContract}/logs"
                + $"?topic={indexedTopic.ToLowerInvariant()}",
                cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("items", out var logs)
                || logs.ValueKind != JsonValueKind.Array
                || logs.GetArrayLength() > 50)
            {
                throw new InvalidOperationException(
                    $"The {_chainDisplayName} contract-log lookup returned malformed results.");
            }
            foreach (var log in logs.EnumerateArray())
            {
                if (log.ValueKind != JsonValueKind.Object
                    || !log.TryGetProperty("topics", out var topics)
                    || topics.ValueKind != JsonValueKind.Array
                    || topics.GetArrayLength() != expectedTopics.Count
                    || !topics.EnumerateArray().Select(static topic => topic.GetString())
                        .SequenceEqual(expectedTopics, StringComparer.OrdinalIgnoreCase)
                    || !log.TryGetProperty("transaction_hash", out var transactionHash)
                    || transactionHash.ValueKind != JsonValueKind.String
                    || !EvmAddress.IsHash(transactionHash.GetString())
                    || !TryReadV2BlockNumber(log, out var blockNumber)
                    || !log.TryGetProperty("block_hash", out var blockHash)
                    || blockHash.ValueKind != JsonValueKind.String
                    || !EvmAddress.IsHash(blockHash.GetString()))
                {
                    continue;
                }
                return new EvmIndexedLog(
                    transactionHash.GetString()!.ToLowerInvariant(),
                    blockNumber,
                    blockHash.GetString()!.ToLowerInvariant());
            }
            return null;
        }

        private async Task<string> FindWithBlockExplorerAsync(
            string poolId,
            long timestamp,
            CancellationToken cancellationToken)
        {
            using var blockDocument = await GetAsync(
                $"{_apiRoot}?module=block&action=getblocknobytime&timestamp={timestamp}&closest=before",
                cancellationToken).ConfigureAwait(false);
            if (!TryReadBlockNumber(blockDocument.RootElement, out var blockNumber))
            {
                throw new InvalidOperationException($"The {_chainDisplayName} block lookup returned no creation block.");
            }

            var fromBlock = blockNumber > 8 ? blockNumber - 8 : 0;
            var toBlock = blockNumber + 8;
            var logUri = $"{_apiRoot}?module=logs&action=getLogs"
                         + $"&address={_poolManager}"
                         + $"&fromBlock={fromBlock}&toBlock={toBlock}"
                         + $"&topic0={EvmWebSocketStreamSource.UniswapV4InitializeTopic}"
                         + $"&topic1={poolId.ToLowerInvariant()}&topic0_1_opr=and";
            try
            {
                using var logDocument = await GetAsync(logUri, cancellationToken).ConfigureAwait(false);
                if (logDocument.RootElement.TryGetProperty("result", out var logs)
                    && logs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var log in logs.EnumerateArray().Take(16))
                    {
                        if (log.ValueKind == JsonValueKind.Object
                            && log.TryGetProperty("transactionHash", out var transactionHash)
                            && transactionHash.ValueKind == JsonValueKind.String
                            && EvmAddress.IsHash(transactionHash.GetString()))
                        {
                            return transactionHash.GetString()!.ToLowerInvariant();
                        }
                    }
                }
                throw new BlockExplorerLogLookupException(blockNumber);
            }
            catch (BlockExplorerLogLookupException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or HttpRequestException
                    or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new BlockExplorerLogLookupException(blockNumber, exception);
            }
        }

        private async Task<string> FindWithRpcAsync(
            string poolId,
            ulong timestamp,
            CancellationToken cancellationToken,
            ulong? approximateBlockNumber = null)
        {
            var latest = await _rpc!.GetBlockAsync("latest", cancellationToken).ConfigureAwait(false);
            var closest = approximateBlockNumber.HasValue
                ? Math.Min(approximateBlockNumber.Value, latest.Number)
                : latest.Number;
            if (!approximateBlockNumber.HasValue
                && _estimatedBlocksPerSecond is > 0
                && timestamp < latest.Timestamp)
            {
                var estimatedDistance = (ulong)Math.Ceiling(
                    (latest.Timestamp - timestamp) * _estimatedBlocksPerSecond.Value);
                if (estimatedDistance < latest.Number)
                {
                    closest = latest.Number - estimatedDistance;
                    for (var attempt = 0; attempt < 8; attempt++)
                    {
                        var candidate = await _rpc.GetBlockAsync(
                            $"0x{closest:x}",
                            cancellationToken).ConfigureAwait(false);
                        if (candidate.Timestamp == timestamp)
                        {
                            break;
                        }
                        var correction = (long)Math.Round(
                            ((decimal)timestamp - candidate.Timestamp) * _estimatedBlocksPerSecond.Value);
                        var corrected = Math.Clamp((decimal)closest + correction, 0, latest.Number);
                        var next = (ulong)corrected;
                        if (next == closest)
                        {
                            next = candidate.Timestamp < timestamp
                                ? Math.Min(latest.Number, closest + 1)
                                : closest > 0 ? closest - 1 : 0;
                        }
                        closest = next;
                    }
                }
            }
            else if (!approximateBlockNumber.HasValue)
            {
                var first = await _rpc.GetBlockAsync("0x0", cancellationToken).ConfigureAwait(false);
                if (timestamp < first.Timestamp)
                {
                    throw new InvalidOperationException($"The {_chainDisplayName} RPC has no block at the catalog creation time.");
                }
                if (timestamp < latest.Timestamp)
                {
                    var lower = first.Number;
                    var upper = latest.Number;
                    while (lower + 1 < upper)
                    {
                        var middle = lower + (upper - lower) / 2;
                        var block = await _rpc.GetBlockAsync(
                            $"0x{middle:x}",
                            cancellationToken).ConfigureAwait(false);
                        if (block.Timestamp <= timestamp)
                        {
                            lower = middle;
                        }
                        else
                        {
                            upper = middle;
                        }
                        if (block.Timestamp == timestamp)
                        {
                            lower = middle;
                            break;
                        }
                    }
                    closest = lower;
                }
            }

            var fromBlock = closest > 16 ? closest - 16 : 0;
            var toBlock = Math.Min(latest.Number, closest + 16);
            for (var rangeStart = fromBlock; rangeStart <= toBlock;)
            {
                var rangeEnd = Math.Min(toBlock, rangeStart + MaximumRpcLogBlockCount - 1);
                var logs = await _rpc.GetLogsAsync(
                    new
                    {
                        address = _poolManager,
                        fromBlock = $"0x{rangeStart:x}",
                        toBlock = $"0x{rangeEnd:x}",
                        topics = new[]
                        {
                            EvmWebSocketStreamSource.UniswapV4InitializeTopic,
                            poolId.ToLowerInvariant()
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
                if (logs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var log in logs.EnumerateArray().Take(16))
                    {
                        if (log.ValueKind == JsonValueKind.Object
                            && log.TryGetProperty("transactionHash", out var transactionHash)
                            && transactionHash.ValueKind == JsonValueKind.String
                            && EvmAddress.IsHash(transactionHash.GetString()))
                        {
                            return transactionHash.GetString()!.ToLowerInvariant();
                        }
                    }
                }
                if (rangeEnd == ulong.MaxValue)
                {
                    break;
                }
                rangeStart = rangeEnd + 1;
            }
            throw new InvalidOperationException($"The {_chainDisplayName} RPC returned no V4 initialization event.");
        }

        private async Task<JsonDocument> GetAsync(
            string requestUri,
            CancellationToken cancellationToken)
        {
            var isV1Request = requestUri.Contains("?module=", StringComparison.Ordinal);
            for (var attempt = 0; attempt < MaximumBlockExplorerAttempts; attempt++)
            {
                if (isV1Request && DateTimeOffset.UtcNow < _blockExplorerV1UnavailableUntil)
                {
                    throw new InvalidOperationException($"The {_chainDisplayName} block lookup is temporarily rate limited.");
                }
                TimeSpan? retryDelay = null;
                await _blockExplorerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (isV1Request && DateTimeOffset.UtcNow < _blockExplorerV1UnavailableUntil)
                    {
                        throw new InvalidOperationException($"The {_chainDisplayName} block lookup is temporarily rate limited.");
                    }
                    var throttleDelay = _nextBlockExplorerRequestAt - DateTimeOffset.UtcNow;
                    if (throttleDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(throttleDelay, cancellationToken).ConfigureAwait(false);
                    }
                    using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                    request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; TrenchHQ/1.0)");
                    using var response = await _httpClient.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            cancellationToken)
                        .ConfigureAwait(false);
                    _nextBlockExplorerRequestAt = DateTimeOffset.UtcNow + MinimumBlockExplorerRequestInterval;
                    if ((int)response.StatusCode == 429 && attempt + 1 < MaximumBlockExplorerAttempts)
                    {
                        retryDelay = response.Headers.RetryAfter?.Delta
                                     ?? TimeSpan.FromMilliseconds(350 * (attempt + 1));
                        if (retryDelay < TimeSpan.Zero || retryDelay > TimeSpan.FromSeconds(2))
                        {
                            retryDelay = TimeSpan.FromSeconds(2);
                        }
                    }
                    else
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            if (isV1Request)
                            {
                                _blockExplorerV1UnavailableUntil = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
                            }
                            throw new InvalidOperationException(
                                $"The {_chainDisplayName} block lookup returned HTTP {(int)response.StatusCode}.");
                        }
                        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                        {
                            throw new InvalidOperationException($"The {_chainDisplayName} block lookup response was too large.");
                        }

                        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
                            .ConfigureAwait(false);
                        using var output = new MemoryStream();
                        var buffer = new byte[8192];
                        while (true)
                        {
                            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                            if (read == 0)
                            {
                                break;
                            }
                            if (output.Length + read > MaximumResponseBytes)
                            {
                                throw new InvalidOperationException($"The {_chainDisplayName} block lookup response was too large.");
                            }
                            output.Write(buffer, 0, read);
                        }
                        try
                        {
                            return JsonDocument.Parse(output.ToArray());
                        }
                        catch (JsonException)
                        {
                            throw new InvalidOperationException($"The {_chainDisplayName} block lookup returned invalid JSON.");
                        }
                    }
                }
                finally
                {
                    _blockExplorerGate.Release();
                }
                if (retryDelay.HasValue)
                {
                    await Task.Delay(retryDelay.Value, cancellationToken).ConfigureAwait(false);
                }
            }
            if (isV1Request)
            {
                _blockExplorerV1UnavailableUntil = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            }
            throw new InvalidOperationException($"The {_chainDisplayName} block lookup retry limit was reached.");
        }

        private static bool TryReadBlockNumber(JsonElement root, out ulong blockNumber)
        {
            blockNumber = 0;
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("result", out var result)
                   && result.ValueKind == JsonValueKind.Object
                   && result.TryGetProperty("blockNumber", out var value)
                   && ((value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out blockNumber))
                       || (value.ValueKind == JsonValueKind.String
                           && ulong.TryParse(value.GetString(), out blockNumber)));
        }

        private static bool TryReadV2BlockNumber(JsonElement log, out ulong blockNumber)
        {
            blockNumber = 0;
            return log.TryGetProperty("block_number", out var value)
                   && ((value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out blockNumber))
                       || value.ValueKind == JsonValueKind.String
                       && EvmAddress.TryParseQuantity(value.GetString(), out blockNumber));
        }

        private sealed class BlockExplorerLogLookupException : InvalidOperationException
        {
            internal BlockExplorerLogLookupException(ulong blockNumber, Exception? innerException = null)
                : base("The block explorer returned no usable V4 initialization event.", innerException)
            {
                BlockNumber = blockNumber;
            }

            internal ulong BlockNumber { get; }
        }
    }

    internal sealed record EvmIndexedLog(
        string TransactionHash,
        ulong BlockNumber,
        string BlockHash);

}

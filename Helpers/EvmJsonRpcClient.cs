using TrenchHQ.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrenchHQ.Helpers
{
    internal enum EvmRpcFailureKind
    {
        InvalidResponse,
        AuthenticationRejected,
        RateLimited,
        RequestLimitExceeded,
        RpcError
    }

    internal sealed class EvmJsonRpcException(
        EvmRpcFailureKind kind,
        string message,
        int? rpcCode = null) : InvalidOperationException(message)
    {
        internal EvmRpcFailureKind Kind { get; } = kind;
        internal int? RpcCode { get; } = rpcCode;
    }

    internal sealed class EvmRpcBlockHeader
    {
        internal ulong Number { get; init; }
        internal string Hash { get; init; } = string.Empty;
        internal string ParentHash { get; init; } = string.Empty;
        internal ulong Timestamp { get; init; }
    }

    internal sealed class EvmJsonRpcClient
    {
        private const int MaximumResponseBytes = 16 * 1024 * 1024;
        internal const ulong MaximumTraceFilterBlocks = 250;

        private readonly HttpClient _httpClient;
        private readonly OnChainUsageScope? _usage;
        private readonly HashSet<string> _recentStateReferences = new(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> _recentStateReferenceOrder = new();
        private readonly Uri _endpoint;
        private readonly bool _requiresCredential;
        private readonly bool _requiresVerifiedBlockNumberReferences;
        private long _requestId;

        internal string ProviderType { get; }

        internal EvmJsonRpcClient(
            HttpClient httpClient,
            OnChainProviderConfiguration configuration,
            string? apiKey)
        {
            ArgumentNullException.ThrowIfNull(httpClient);
            ArgumentNullException.ThrowIfNull(configuration);
            var preset = OnChainProviderCatalog.Get(configuration.ProviderType);
            if (preset.StreamTransport != OnChainStreamTransport.EvmWebSocket
                || !string.Equals(configuration.ChainNamespace, ChainNamespaces.Eip155, StringComparison.Ordinal)
                || !string.Equals(configuration.ChainId, preset.ChainId, StringComparison.Ordinal))
            {
                throw new ArgumentException("The provider is not an EVM JSON-RPC profile.", nameof(configuration));
            }
            if (!OnChainProviderConfigurationStore.IsSecureEndpoint(
                    configuration.RpcEndpoint,
                    preset.RpcAuthenticationMode))
            {
                throw new ArgumentException("The EVM RPC endpoint must be a credential-free HTTPS URL.", nameof(configuration));
            }
            if (preset.RequiresCredential && string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ArgumentException("This provider requires an API key.", nameof(apiKey));
            }

            _httpClient = httpClient;
            _usage = configuration.Usage;
            ProviderType = configuration.ProviderType;
            _endpoint = OnChainProviderEndpointBuilder.Build(
                configuration.RpcEndpoint,
                apiKey,
                preset.RpcAuthenticationMode);
            _requiresCredential = preset.RequiresCredential;
            _requiresVerifiedBlockNumberReferences = string.Equals(
                configuration.ProviderType,
                OnChainProviderTypes.InfuraEthereum,
                StringComparison.Ordinal);
        }

        internal async Task<ulong> GetChainIdAsync(CancellationToken cancellationToken)
        {
            var result = await SendAsync("eth_chainId", Array.Empty<object>(), cancellationToken)
                .ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.String
                || !EvmAddress.TryParseQuantity(result.GetString(), out var chainId))
            {
                throw InvalidResponse("eth_chainId returned an invalid quantity.");
            }
            return chainId;
        }

        internal async Task<EvmRpcBlockHeader> GetBlockAsync(
            string blockTag,
            CancellationToken cancellationToken)
        {
            var result = await SendAsync(
                "eth_getBlockByNumber",
                new object[] { blockTag, false },
                cancellationToken).ConfigureAwait(false);
            if (result.ValueKind == JsonValueKind.Null)
            {
                throw InvalidResponse($"The {blockTag} block is unavailable.");
            }
            if (result.ValueKind != JsonValueKind.Object
                || !TryReadQuantity(result, "number", out var number)
                || !TryReadHash(result, "hash", out var hash)
                || !TryReadHash(result, "parentHash", out var parentHash)
                || !TryReadQuantity(result, "timestamp", out var timestamp))
            {
                throw InvalidResponse($"The {blockTag} block header is invalid.");
            }
            if (blockTag == "latest")
            {
                lock (_recentStateReferences)
                {
                    foreach (var reference in new[] { hash, $"0x{number:x}" })
                    {
                        if (_recentStateReferences.Add(reference)) _recentStateReferenceOrder.Enqueue(reference);
                    }
                    while (_recentStateReferenceOrder.Count > 2) _recentStateReferences.Remove(_recentStateReferenceOrder.Dequeue());
                }
            }
            return new EvmRpcBlockHeader
            {
                Number = number,
                Hash = hash,
                ParentHash = parentHash,
                Timestamp = timestamp
            };
        }

        internal async Task<ulong> GetBlockNumberAsync(CancellationToken cancellationToken)
        {
            var result = await SendAsync("eth_blockNumber", Array.Empty<object>(), cancellationToken)
                .ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.String
                || !EvmAddress.TryParseQuantity(result.GetString(), out var blockNumber))
            {
                throw InvalidResponse("eth_blockNumber returned an invalid quantity.");
            }
            return blockNumber;
        }

        internal async Task<EvmWalletBlock> GetWalletBlockAsync(
            string blockTag,
            CancellationToken cancellationToken)
        {
            var result = await SendAsync(
                "eth_getBlockByNumber",
                new object[] { blockTag, true },
                cancellationToken).ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.Object
                || !TryReadQuantity(result, "number", out var number)
                || !TryReadHash(result, "hash", out var hash)
                || !TryReadHash(result, "parentHash", out var parentHash)
                || !TryReadQuantity(result, "timestamp", out var timestamp)
                || !result.TryGetProperty("transactions", out var transactions)
                || transactions.ValueKind != JsonValueKind.Array)
            {
                throw InvalidResponse($"The {blockTag} wallet block is invalid.");
            }
            var parsed = new List<EvmWalletTransaction>();
            foreach (var item in transactions.EnumerateArray())
            {
                if (!TryReadHash(item, "hash", out var transactionHash)
                    || !item.TryGetProperty("from", out var fromElement)
                    || fromElement.ValueKind != JsonValueKind.String
                    || !EvmAddress.TryNormalize(fromElement.GetString(), out var from)
                    || !item.TryGetProperty("value", out var valueElement)
                    || valueElement.ValueKind != JsonValueKind.String
                    || !TryReadUnsignedQuantity(valueElement.GetString(), out var value))
                {
                    throw InvalidResponse("An EVM wallet block transaction is invalid.");
                }
                string? to = null;
                if (item.TryGetProperty("to", out var toElement)
                    && toElement.ValueKind != JsonValueKind.Null)
                {
                    if (toElement.ValueKind != JsonValueKind.String
                        || !EvmAddress.TryNormalize(toElement.GetString(), out to))
                    {
                        throw InvalidResponse("An EVM wallet transaction recipient is invalid.");
                    }
                }
                parsed.Add(new EvmWalletTransaction
                {
                    Hash = transactionHash,
                    From = from,
                    To = to,
                    ValueRaw = value.ToString(CultureInfo.InvariantCulture)
                });
            }
            return new EvmWalletBlock
            {
                Number = number,
                Hash = hash,
                ParentHash = parentHash,
                Timestamp = timestamp,
                Transactions = parsed.ToArray()
            };
        }

        internal async Task<IReadOnlyList<EvmWalletTrace>> GetAddressTracesAsync(
            ulong blockNumber,
            IReadOnlyCollection<string> addresses,
            bool outgoing,
            CancellationToken cancellationToken)
        {
            return await GetAddressTracesAsync(
                    blockNumber,
                    blockNumber,
                    addresses,
                    outgoing,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        internal async Task<IReadOnlyList<EvmWalletTrace>> GetAddressTracesAsync(
            ulong fromBlock,
            ulong toBlock,
            IReadOnlyCollection<string> addresses,
            bool outgoing,
            CancellationToken cancellationToken)
        {
            if (toBlock < fromBlock
                || toBlock - fromBlock >= MaximumTraceFilterBlocks)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(toBlock),
                    $"An EVM trace range must contain at most {MaximumTraceFilterBlocks} blocks.");
            }
            var normalized = addresses
                .Select(address => EvmAddress.TryNormalize(address, out var value)
                    ? value
                    : throw new ArgumentException("An EVM trace address is invalid.", nameof(addresses)))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (normalized.Length == 0)
            {
                return [];
            }
            var filter = new Dictionary<string, object>
            {
                ["fromBlock"] = $"0x{fromBlock:x}",
                ["toBlock"] = $"0x{toBlock:x}",
                [outgoing ? "fromAddress" : "toAddress"] = normalized
            };
            var result = await SendAsync("trace_filter", new object[] { filter }, cancellationToken)
                .ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.Array)
            {
                throw InvalidResponse("trace_filter returned an invalid result.");
            }
            var traces = new List<EvmWalletTrace>();
            foreach (var item in result.EnumerateArray())
            {
                if (!item.TryGetProperty("traceAddress", out var traceAddress)
                    || traceAddress.ValueKind != JsonValueKind.Array
                    || traceAddress.GetArrayLength() == 0)
                {
                    continue;
                }
                if (!TryReadHash(item, "transactionHash", out var transactionHash)
                    || !TryReadTraceBlockNumber(item, out var traceBlockNumber)
                    || !TryReadTraceValueMovement(item, out var from, out var to, out var value))
                {
                    continue;
                }
                var path = string.Join(".", traceAddress.EnumerateArray().Select(static part => part.GetInt32()));
                traces.Add(new EvmWalletTrace
                {
                    TransactionHash = transactionHash,
                    TracePath = path,
                    From = from,
                    To = to,
                    ValueRaw = value.ToString(CultureInfo.InvariantCulture),
                    BlockNumber = traceBlockNumber
                });
            }
            return traces;
        }

        internal async Task<JsonElement> CallAsync(
            string contractAddress,
            string data,
            object blockReference,
            CancellationToken cancellationToken)
        {
            if (!EvmAddress.TryNormalize(contractAddress, out var address))
            {
                throw new ArgumentException("Contract address is invalid.", nameof(contractAddress));
            }
            return await SendBlockReferencedAsync(
                    "eth_call",
                    new { to = address, data },
                    blockReference,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        internal Task<JsonElement> GetLogsAsync(
            object filter,
            CancellationToken cancellationToken)
        {
            return SendAsync("eth_getLogs", new[] { filter }, cancellationToken);
        }

        internal Task<JsonElement> GetTransactionReceiptAsync(
            string transactionHash,
            CancellationToken cancellationToken)
        {
            if (!EvmAddress.IsHash(transactionHash))
            {
                throw new ArgumentException("Transaction hash is invalid.", nameof(transactionHash));
            }
            return SendAsync(
                "eth_getTransactionReceipt",
                new object[] { transactionHash.ToLowerInvariant() },
                cancellationToken);
        }

        internal Task<JsonElement> GetBlockReceiptsAsync(
            ulong blockNumber,
            CancellationToken cancellationToken)
        {
            return SendAsync(
                "eth_getBlockReceipts",
                new object[] { $"0x{blockNumber:x}" },
                cancellationToken);
        }

        internal async Task<string> GetCodeAsync(
            string contractAddress,
            object blockReference,
            CancellationToken cancellationToken)
        {
            if (!EvmAddress.TryNormalize(contractAddress, out var address))
            {
                throw new ArgumentException("Contract address is invalid.", nameof(contractAddress));
            }
            var result = await SendBlockReferencedAsync(
                    "eth_getCode",
                    address,
                    blockReference,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.String
                || !EvmAddress.IsData(result.GetString(), MaximumResponseBytes / 2))
            {
                throw InvalidResponse("eth_getCode returned invalid bytecode.");
            }
            return result.GetString()!.ToLowerInvariant();
        }

        private async Task<JsonElement> SendBlockReferencedAsync(
            string method,
            object firstParameter,
            object blockReference,
            CancellationToken cancellationToken)
        {
            var knownHeader = blockReference as EvmRpcBlockHeader;
            if (knownHeader != null)
            {
                blockReference = new { blockHash = knownHeader.Hash, requireCanonical = true };
            }
            if (!_requiresVerifiedBlockNumberReferences
                || !TryGetBlockHashReference(blockReference, out var expectedHash))
            {
                return await SendAsync(
                        method,
                        new[] { firstParameter, blockReference },
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var referencedBlock = knownHeader
                ?? await GetBlockByHashAsync(expectedHash, cancellationToken).ConfigureAwait(false);
            var blockNumber = $"0x{referencedBlock.Number:x}";
            var result = await SendAsync(
                    method,
                    new object[] { firstParameter, blockNumber },
                    cancellationToken)
                .ConfigureAwait(false);
            var canonicalBlock = await GetBlockAsync(blockNumber, cancellationToken)
                .ConfigureAwait(false);
            if (!canonicalBlock.Hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw InvalidResponse(
                    "The EVM provider changed the block while resolving a pinned state read.");
            }
            return result;
        }

        private async Task<EvmRpcBlockHeader> GetBlockByHashAsync(
            string blockHash,
            CancellationToken cancellationToken)
        {
            var result = await SendAsync(
                    "eth_getBlockByHash",
                    new object[] { blockHash, false },
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.ValueKind == JsonValueKind.Null)
            {
                throw InvalidResponse("The referenced block is unavailable.");
            }
            if (result.ValueKind != JsonValueKind.Object
                || !TryReadQuantity(result, "number", out var number)
                || !TryReadHash(result, "hash", out var hash)
                || !TryReadHash(result, "parentHash", out var parentHash)
                || !TryReadQuantity(result, "timestamp", out var timestamp)
                || !hash.Equals(blockHash, StringComparison.OrdinalIgnoreCase))
            {
                throw InvalidResponse("The referenced block header is invalid.");
            }
            return new EvmRpcBlockHeader
            {
                Number = number,
                Hash = hash,
                ParentHash = parentHash,
                Timestamp = timestamp
            };
        }

        private static bool TryGetBlockHashReference(object blockReference, out string blockHash)
        {
            blockHash = string.Empty;
            var element = JsonSerializer.SerializeToElement(blockReference);
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty("blockHash", out var hashElement)
                || hashElement.ValueKind != JsonValueKind.String
                || !EvmAddress.IsHash(hashElement.GetString()))
            {
                return false;
            }
            blockHash = hashElement.GetString()!.ToLowerInvariant();
            return true;
        }

        internal async Task ProbeBatchAsync(CancellationToken cancellationToken)
        {
            var firstId = Interlocked.Increment(ref _requestId);
            var secondId = Interlocked.Increment(ref _requestId);
            var request = new object[]
            {
                new { jsonrpc = "2.0", id = firstId, method = "eth_chainId", @params = Array.Empty<object>() },
                new { jsonrpc = "2.0", id = secondId, method = "eth_blockNumber", @params = Array.Empty<object>() }
            };
            var root = await SendDocumentAsync(request, "JSON-RPC batch", cancellationToken).ConfigureAwait(false);
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 2)
            {
                throw InvalidResponse("The JSON-RPC batch response is invalid.");
            }
            var ids = root.EnumerateArray()
                .Select(static item => item.TryGetProperty("id", out var id) && id.TryGetInt64(out var value)
                    ? value
                    : -1)
                .ToHashSet();
            if (!ids.SetEquals(new[] { firstId, secondId }))
            {
                throw InvalidResponse("The JSON-RPC batch response IDs do not match the request.");
            }
            foreach (var item in root.EnumerateArray())
            {
                ThrowIfRpcError(item);
                if (!item.TryGetProperty("result", out _))
                {
                    throw InvalidResponse("A JSON-RPC batch item has no result.");
                }
            }
        }

        private async Task<JsonElement> SendAsync(
            string method,
            object parameters,
            CancellationToken cancellationToken)
        {
            var request = new
            {
                jsonrpc = "2.0",
                id = Interlocked.Increment(ref _requestId),
                method,
                @params = parameters
            };
            var root = await SendDocumentAsync(request, method, cancellationToken).ConfigureAwait(false);
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw InvalidResponse("The JSON-RPC response is not an object.");
            }
            ThrowIfRpcError(root);
            if (!root.TryGetProperty("result", out var result))
            {
                throw InvalidResponse("The JSON-RPC response has no result.");
            }
            return result.Clone();
        }

        private async Task<JsonElement> SendDocumentAsync(
            object payload,
            string operation,
            CancellationToken cancellationToken)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new ByteArrayContent(bytes)
            };
            request.Content.Headers.ContentType = new("application/json");
            cancellationToken.ThrowIfCancellationRequested();
            if (_usage != null)
            {
                var document = JsonSerializer.SerializeToElement(payload);
                foreach (var item in document.ValueKind == JsonValueKind.Array ? document.EnumerateArray().ToArray() : new[] { document })
                {
                    var method = item.GetProperty("method").GetString()!;
                    var parameters = item.GetProperty("params");
                    var archive = false;
                    JsonElement? blockReference = method switch
                    {
                        "eth_call" or "eth_getCode" or "eth_getBalance" => parameters[1],
                        "eth_getBlockByNumber" => parameters[0],
                        "eth_getLogs" when parameters[0].TryGetProperty("fromBlock", out var from) => from,
                        _ => null
                    };
                    if (blockReference.HasValue)
                    {
                        var block = blockReference.Value;
                        var reference = block.ValueKind == JsonValueKind.String ? block.GetString()
                            : block.TryGetProperty("blockHash", out var hash) ? hash.GetString() : null;
                        lock (_recentStateReferences)
                            archive = reference is not ("latest" or "pending")
                                && (reference == null || !_recentStateReferences.Contains(reference));
                    }
                    _usage.Rpc(method, archive);
                }
            }
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                throw new EvmJsonRpcException(
                    EvmRpcFailureKind.RpcError,
                    "The EVM provider HTTP connection failed.");
            }
            using (response)
            {
                if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
                {
                    throw new EvmJsonRpcException(EvmRpcFailureKind.RequestLimitExceeded,
                        "The EVM provider rejected the request size.");
                }
                if (response.StatusCode == HttpStatusCode.PaymentRequired)
                {
                    _usage?.QuotaExceeded(response.Headers.RetryAfter?.Delta
                        ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow));
                    throw new EvmJsonRpcException(EvmRpcFailureKind.RateLimited, "The provider account quota is exhausted.");
                }
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                using var output = new MemoryStream();
                var buffer = new byte[16 * 1024];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }
                    if (output.Length + read > MaximumResponseBytes)
                    {
                        throw InvalidResponse("The JSON-RPC response exceeded the size limit.");
                    }
                    output.Write(buffer, 0, read);
                }
                JsonElement root;
                try
                {
                    using var document = JsonDocument.Parse(output.ToArray());
                    root = document.RootElement.Clone();
                }
                catch (JsonException) when (!response.IsSuccessStatusCode)
                {
                    root = default; // HTTP status still classifies plain-text or empty error bodies.
                }
                catch (JsonException)
                {
                    throw new EvmJsonRpcException(EvmRpcFailureKind.InvalidResponse, "The JSON-RPC response is invalid JSON.");
                }
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error)
                    && OnChainProviderUsage.IsQuotaError(error))
                    _usage?.QuotaExceeded(response.Headers.RetryAfter?.Delta
                        ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow));
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new EvmJsonRpcException(
                        _requiresCredential ? EvmRpcFailureKind.AuthenticationRejected : EvmRpcFailureKind.RpcError,
                        _requiresCredential ? "The EVM provider rejected the credential."
                            : "The EVM provider denied access to the public endpoint.");
                if ((int)response.StatusCode == 429)
                    throw new EvmJsonRpcException(EvmRpcFailureKind.RateLimited, "The EVM provider rate limit was reached.");
                if (!response.IsSuccessStatusCode)
                    throw new EvmJsonRpcException(EvmRpcFailureKind.RpcError,
                        $"The EVM provider returned HTTP {(int)response.StatusCode} for {operation}.");
                return root;
            }
        }

        private void ThrowIfRpcError(JsonElement response)
        {
            if (!response.TryGetProperty("error", out var error))
            {
                return;
            }
            var code = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsed)
                ? parsed
                : (int?)null;
            var message = error.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : null;
            if (OnChainProviderUsage.IsQuotaError(error)) _usage?.QuotaExceeded();
            var kind = code is 402 or 429 || OnChainProviderUsage.IsQuotaError(error) || message?.Contains("rate", StringComparison.OrdinalIgnoreCase) == true
                ? EvmRpcFailureKind.RateLimited
                : message != null && new[] { "out of gas", "gas required exceeds", "gas cap",
                    "request too large", "response too large", "response size", "request body too large" }
                    .Any(marker => message.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    ? EvmRpcFailureKind.RequestLimitExceeded
                : _requiresCredential
                  && (message?.Contains("auth", StringComparison.OrdinalIgnoreCase) == true
                      || message?.Contains("key", StringComparison.OrdinalIgnoreCase) == true)
                    ? EvmRpcFailureKind.AuthenticationRejected
                    : EvmRpcFailureKind.RpcError;
            throw new EvmJsonRpcException(kind, "The EVM provider rejected an RPC request.", code);
        }

        private static bool TryReadQuantity(JsonElement parent, string name, out ulong value)
        {
            value = 0;
            return parent.TryGetProperty(name, out var element)
                   && element.ValueKind == JsonValueKind.String
                   && EvmAddress.TryParseQuantity(element.GetString(), out value);
        }

        private static bool TryReadHash(JsonElement parent, string name, out string value)
        {
            value = string.Empty;
            if (!parent.TryGetProperty(name, out var element)
                || element.ValueKind != JsonValueKind.String
                || !EvmAddress.IsHash(element.GetString()))
            {
                return false;
            }
            value = element.GetString()!.ToLowerInvariant();
            return true;
        }

        private static bool TryReadTraceBlockNumber(JsonElement item, out ulong blockNumber)
        {
            blockNumber = 0;
            if (!item.TryGetProperty("blockNumber", out var element))
            {
                return false;
            }
            return element.ValueKind == JsonValueKind.Number
                ? element.TryGetUInt64(out blockNumber)
                : element.ValueKind == JsonValueKind.String
                  && EvmAddress.TryParseQuantity(element.GetString(), out blockNumber);
        }

        private static bool TryReadTraceValueMovement(
            JsonElement item,
            out string from,
            out string to,
            out BigInteger value)
        {
            from = string.Empty;
            to = string.Empty;
            value = BigInteger.Zero;
            if (!item.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("action", out var action)
                || action.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            string? fromValue;
            string? toValue;
            string? amountValue;
            switch (typeElement.GetString())
            {
                case "call":
                    fromValue = ReadString(action, "from");
                    toValue = ReadString(action, "to");
                    amountValue = ReadString(action, "value");
                    break;
                case "create":
                    fromValue = ReadString(action, "from");
                    toValue = item.TryGetProperty("result", out var result)
                        ? ReadString(result, "address")
                        : null;
                    amountValue = ReadString(action, "value");
                    break;
                case "suicide":
                    fromValue = ReadString(action, "address");
                    toValue = ReadString(action, "refundAddress");
                    amountValue = ReadString(action, "balance");
                    break;
                default:
                    return false;
            }
            return EvmAddress.TryNormalize(fromValue, out from)
                   && EvmAddress.TryNormalize(toValue, out to)
                   && TryReadUnsignedQuantity(amountValue, out value)
                   && value > BigInteger.Zero;
        }

        private static string? ReadString(JsonElement parent, string name)
        {
            return parent.ValueKind == JsonValueKind.Object
                   && parent.TryGetProperty(name, out var element)
                   && element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;
        }

        private static bool TryReadUnsignedQuantity(string? raw, out BigInteger value)
        {
            value = BigInteger.Zero;
            return !string.IsNullOrWhiteSpace(raw)
                   && raw.StartsWith("0x", StringComparison.Ordinal)
                   && raw.Length > 2
                   && raw.AsSpan(2).ToArray().All(Uri.IsHexDigit)
                   && BigInteger.TryParse(
                       "0" + raw[2..],
                       NumberStyles.AllowHexSpecifier,
                       CultureInfo.InvariantCulture,
                       out value)
                   && value >= BigInteger.Zero;
        }

        private static EvmJsonRpcException InvalidResponse(string message)
        {
            return new EvmJsonRpcException(EvmRpcFailureKind.InvalidResponse, message);
        }
    }
}

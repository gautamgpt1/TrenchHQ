using TrenchHQ.Core.OnChain;
using TrenchHQ.Core.OnChain.Evm;
using TrenchHQ.Core.Providers;
using TrenchHQ.Infrastructure.OnChain.Solana;
using TrenchHQ.Infrastructure.Providers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TrenchHQ.Infrastructure.OnChain.Evm
{
    internal sealed class EvmWebSocketStreamSource : IOnChainStreamSource
    {

        private const int MaximumMessageBytes = 2 * 1024 * 1024;
        private const int MaximumLogDataBytes = 128 * 1024;

        private readonly OnChainProviderConfiguration _configuration;
        private readonly Uri _endpoint;

        internal EvmWebSocketStreamSource(
            OnChainProviderConfiguration configuration,
            string? apiKey)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            var preset = OnChainProviderCatalog.Get(configuration.ProviderType);
            if (preset.StreamTransport != OnChainStreamTransport.EvmWebSocket
                || !string.Equals(configuration.ChainNamespace, ChainNamespaces.Eip155, StringComparison.Ordinal))
            {
                throw new ArgumentException("The provider is not an EVM WebSocket profile.", nameof(configuration));
            }
            if (!OnChainProviderConfigurationStore.IsSecureStreamEndpoint(
                    configuration.StreamEndpoint,
                    preset.StreamTransport,
                    preset.StreamAuthenticationMode))
            {
                throw new ArgumentException("The EVM stream endpoint must be a credential-free WSS URL.", nameof(configuration));
            }
            if (preset.RequiresCredential && string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ArgumentException("This provider requires an API key.", nameof(apiKey));
            }

            _configuration = configuration;
            _endpoint = OnChainProviderEndpointBuilder.Build(
                configuration.StreamEndpoint,
                apiKey,
                preset.StreamAuthenticationMode);
        }

        public async Task RunAsync(
            OnChainStreamSubscription subscription,
            ChannelWriter<OnChainSourceUpdate> output,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(subscription);
            ArgumentNullException.ThrowIfNull(output);

            var poolFilters = BuildPoolFilters(subscription.Pools);
            using var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            socket.Options.CollectHttpResponseDetails = true;
            try
            {
                await socket.ConnectAsync(_endpoint, cancellationToken).ConfigureAwait(false);
                var pending = new Dictionary<long, SubscriptionKind>();
                var active = new Dictionary<string, SubscriptionKind>(StringComparer.Ordinal);
                var requestId = 1L;
                if (ShouldSubscribeToHeads(_configuration.ChainId))
                {
                    await SendSubscriptionAsync(
                        socket,
                        requestId++,
                        new object[] { "newHeads" },
                        SubscriptionKind.Heads,
                        pending,
                        cancellationToken).ConfigureAwait(false);
                }
                foreach (var filter in poolFilters)
                {
                    await SendSubscriptionAsync(
                        socket,
                        requestId++,
                        new object[] { "logs", filter.CreateRpcFilter() },
                        SubscriptionKind.Logs,
                        pending,
                        cancellationToken).ConfigureAwait(false);
                }

                while (socket.State == WebSocketState.Open)
                {
                    var message = await ReceiveMessageAsync(socket, cancellationToken).ConfigureAwait(false);
                    if (message == null)
                    {
                        throw new IOException("The EVM WebSocket closed unexpectedly.");
                    }
                    using var document = JsonDocument.Parse(message);
                    var root = document.RootElement;
                    if (root.TryGetProperty("id", out var id))
                    {
                        ProcessSubscriptionResponse(root, id, pending, active);
                        continue;
                    }
                    if (!root.TryGetProperty("method", out var method)
                        || method.GetString() != "eth_subscription"
                        || !root.TryGetProperty("params", out var parameters)
                        || !parameters.TryGetProperty("subscription", out var subscriptionId)
                        || subscriptionId.ValueKind != JsonValueKind.String
                        || !active.TryGetValue(subscriptionId.GetString()!, out var kind)
                        || !parameters.TryGetProperty("result", out var result))
                    {
                        continue;
                    }

                    var observedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    switch (kind)
                    {
                        case SubscriptionKind.Heads:
                            await output.WriteAsync(
                                new OnChainSourceEvmHeadUpdate(ParseHead(
                                    result,
                                    _configuration.ChainId,
                                    subscription.ConnectionEpoch,
                                    observedAt)),
                                cancellationToken).ConfigureAwait(false);
                            break;
                        case SubscriptionKind.Logs:
                            var update = ParseLog(
                                result,
                                _configuration.ChainId,
                                subscription.ConnectionEpoch,
                                observedAt);
                            if (poolFilters.Any(filter => filter.Matches(update)))
                            {
                                await output.WriteAsync(
                                    new OnChainSourceEvmLogUpdate(update),
                                    cancellationToken).ConfigureAwait(false);
                            }
                            break;
                    }
                }
                throw new IOException("The EVM WebSocket ended unexpectedly.");
            }
            catch (WebSocketException)
            {
                if ((int)socket.HttpStatusCode == 402) _configuration.Usage?.QuotaExceeded();
                if ((int)socket.HttpStatusCode is 401 or 403)
                    throw new EvmJsonRpcException(EvmRpcFailureKind.AuthenticationRejected, "The provider rejected WebSocket access.");
                if ((int)socket.HttpStatusCode is 402 or 429)
                    throw new EvmJsonRpcException(EvmRpcFailureKind.RateLimited, "The provider limited WebSocket access.");
                throw new IOException("The EVM provider WebSocket connection failed.");
            }
        }

        internal static bool ShouldSubscribeToHeads(string chainId) =>
            !EvmStreamCoordinator.UsesSampledHeads(chainId);

        internal static async Task<(bool Heads, bool Logs)> ProbeAsync(
            OnChainProviderConfiguration configuration,
            string? apiKey,
            string poolAddress,
            string topic,
            CancellationToken cancellationToken)
        {
            var source = new EvmWebSocketStreamSource(configuration, apiKey);
            using var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            socket.Options.CollectHttpResponseDetails = true;
            try
            {
                await socket.ConnectAsync(source._endpoint, cancellationToken).ConfigureAwait(false);
                var pending = new Dictionary<long, SubscriptionKind>();
                var active = new Dictionary<string, SubscriptionKind>(StringComparer.Ordinal);
                if (ShouldSubscribeToHeads(configuration.ChainId))
                {
                    await source.SendSubscriptionAsync(
                        socket,
                        1,
                        new object[] { "newHeads" },
                        SubscriptionKind.Heads,
                        pending,
                        cancellationToken).ConfigureAwait(false);
                }
                await source.SendSubscriptionAsync(
                    socket,
                    2,
                    new object[]
                    {
                        "logs",
                        new { address = poolAddress, topics = new[] { topic } }
                    },
                    SubscriptionKind.Logs,
                    pending,
                    cancellationToken).ConfigureAwait(false);

                while (pending.Count > 0)
                {
                    var message = await source.ReceiveMessageAsync(socket, cancellationToken).ConfigureAwait(false)
                                  ?? throw new IOException("The EVM WebSocket closed during its capability probe.");
                    using var document = JsonDocument.Parse(message);
                    var root = document.RootElement;
                    if (root.TryGetProperty("id", out var id))
                    {
                        ProcessSubscriptionResponse(root, id, pending, active);
                    }
                }
                return (
                    active.ContainsValue(SubscriptionKind.Heads),
                    active.ContainsValue(SubscriptionKind.Logs));
            }
            catch (WebSocketException)
            {
                throw new IOException("The EVM provider WebSocket capability probe failed.");
            }
        }

        internal static EvmHeadUpdate ParseHead(
            JsonElement result,
            string chainId,
            ulong connectionEpoch,
            long observedAtUnixMs)
        {
            if (result.ValueKind != JsonValueKind.Object
                || !TryReadQuantity(result, "number", out var number)
                || !TryReadHash(result, "hash", out var hash)
                || !TryReadHash(result, "parentHash", out var parentHash)
                || !TryReadQuantity(result, "timestamp", out var timestamp))
            {
                throw new InvalidDataException("The EVM head notification is invalid.");
            }
            return new EvmHeadUpdate
            {
                ChainId = chainId,
                ConnectionEpoch = connectionEpoch,
                Number = number,
                Hash = hash,
                ParentHash = parentHash,
                Timestamp = timestamp,
                ObservedAtUnixMs = observedAtUnixMs
            };
        }

        internal static EvmLogUpdate ParseLog(
            JsonElement result,
            string chainId,
            ulong connectionEpoch,
            long observedAtUnixMs)
        {
            if (result.ValueKind != JsonValueKind.Object
                || !TryReadAddress(result, "address", out var address)
                || !TryReadData(result, "data", out var data)
                || !TryReadQuantity(result, "blockNumber", out var blockNumber)
                || !TryReadHash(result, "blockHash", out var blockHash)
                || !TryReadHash(result, "transactionHash", out var transactionHash)
                || !TryReadQuantity(result, "transactionIndex", out var transactionIndex)
                || !TryReadQuantity(result, "logIndex", out var logIndex)
                || !result.TryGetProperty("topics", out var topicsElement)
                || topicsElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("The EVM log notification is invalid.");
            }
            var topics = topicsElement.EnumerateArray()
                .Select(static topic => topic.ValueKind == JsonValueKind.String
                                        && EvmAddress.IsHash(topic.GetString())
                    ? topic.GetString()!.ToLowerInvariant()
                    : throw new InvalidDataException("An EVM log topic is invalid."))
                .ToArray();
            if (topics.Length is < 1 or > 4)
            {
                throw new InvalidDataException("The EVM log topic count is invalid.");
            }
            return new EvmLogUpdate
            {
                ChainId = chainId,
                ConnectionEpoch = connectionEpoch,
                Address = address,
                Topics = topics,
                Data = data,
                BlockNumber = blockNumber,
                BlockHash = blockHash,
                TransactionHash = transactionHash,
                TransactionIndex = transactionIndex,
                LogIndex = logIndex,
                Removed = result.TryGetProperty("removed", out var removed)
                          && removed.ValueKind == JsonValueKind.True,
                ObservedAtUnixMs = observedAtUnixMs
            };
        }

        private static PoolLogFilter[] BuildPoolFilters(OnChainWatchedPoolSelection[] pools)
        {
            var selections = (pools ?? [])
                .Where(static pool => string.Equals(
                    pool.Descriptor.PoolKey.ChainNamespace,
                    ChainNamespaces.Eip155,
                    StringComparison.Ordinal))
                .ToArray();
            var classic = selections
                .Where(static pool => pool.Descriptor.PoolKey.ProtocolId
                    is OnChainProtocolIds.UniswapV2
                        or OnChainProtocolIds.PancakeV2
                    or OnChainProtocolIds.UniswapV3
                    or OnChainProtocolIds.PancakeV3
                    or OnChainProtocolIds.AerodromeClassic
                    or OnChainProtocolIds.AerodromeSlipstream
                    or OnChainProtocolIds.Curve
                    or OnChainProtocolIds.FermiSwap
                    or OnChainProtocolIds.PonsV2Curve)
                .SelectMany(static pool => (pool.Descriptor.PoolKey.ProtocolId switch
                    {
                        OnChainProtocolIds.UniswapV2 or OnChainProtocolIds.PancakeV2 => new[]
                        {
                            EvmEventTopics.UniswapV2SwapTopic,
                            EvmEventTopics.UniswapV2SyncTopic
                        },
                        OnChainProtocolIds.UniswapV3 => new[] { EvmEventTopics.UniswapV3SwapTopic },
                        OnChainProtocolIds.PancakeV3 => new[] { EvmEventTopics.PancakeV3SwapTopic },
                        OnChainProtocolIds.AerodromeClassic => new[]
                        {
                            EvmEventTopics.AerodromeClassicSwapTopic,
                            EvmEventTopics.AerodromeClassicSyncTopic
                        },
                        OnChainProtocolIds.AerodromeSlipstream => new[] { EvmEventTopics.UniswapV3SwapTopic },
                        OnChainProtocolIds.Curve => new[]
                        {
                            EvmEventTopics.CurveTokenExchangeSignedTopic,
                            EvmEventTopics.CurveTokenExchangeUnsignedTopic,
                            EvmEventTopics.CurveTokenExchangeExtendedTopic
                        },
                        OnChainProtocolIds.FermiSwap => new[]
                        {
                            EvmEventTopics.FermiSwapTopic,
                            EvmEventTopics.FermiSwappedTopic
                        },
                        OnChainProtocolIds.PonsV2Curve => new[]
                        {
                            EvmEventTopics.PonsV2CurveBuyTopic,
                            EvmEventTopics.PonsV2CurveSellTopic
                        },
                        _ => throw new ArgumentException("The EVM pool protocol is unsupported.")
                    })
                    .Select(topic => new
                {
                    Address = EvmAddress.TryNormalize(
                        pool.Descriptor.PoolKey.ProtocolId == OnChainProtocolIds.FermiSwap
                            ? pool.Descriptor.PoolKey.DeploymentKey.ContractAddress
                            : pool.Descriptor.PoolKey.PoolId,
                        out var address)
                        ? address
                        : throw new ArgumentException("An EVM pool address is invalid."),
                    Topic = topic
                }))
                .Distinct()
                .ToArray();
            var filters = new List<PoolLogFilter>();
            if (classic.Length > 0)
            {
                filters.Add(new PoolLogFilter(
                    classic.Select(static filter => filter.Address)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                    classic.Select(static filter => filter.Topic)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                    []));
            }
            foreach (var manager in selections
                         .Where(static pool =>
                             pool.Descriptor.PoolKey.ProtocolId
                                 is OnChainProtocolIds.UniswapV4
                                 or OnChainProtocolIds.PancakeInfinityCl
                                 or OnChainProtocolIds.PancakeInfinityBin)
                         .GroupBy(static pool => new
                         {
                             pool.Descriptor.PoolKey.ProtocolId,
                             Address = EvmAddress.TryNormalize(
                                 pool.Descriptor.PoolKey.DeploymentKey.ContractAddress,
                                 out var address)
                             ? address
                             : throw new ArgumentException("An EVM singleton PoolManager address is invalid.")
                         }))
            {
                var poolIds = manager
                    .Select(static pool => EvmAddress.IsHash(pool.Descriptor.PoolKey.PoolId)
                        ? pool.Descriptor.PoolKey.PoolId.ToLowerInvariant()
                        : throw new ArgumentException("An EVM singleton PoolId is invalid."))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var topic = manager.Key.ProtocolId switch
                {
                    OnChainProtocolIds.UniswapV4 => EvmEventTopics.UniswapV4SwapTopic,
                    OnChainProtocolIds.PancakeInfinityCl => EvmEventTopics.PancakeInfinityClSwapTopic,
                    OnChainProtocolIds.PancakeInfinityBin => EvmEventTopics.PancakeInfinityBinSwapTopic,
                    _ => throw new ArgumentException("The EVM singleton protocol is unsupported.")
                };
                filters.Add(new PoolLogFilter(
                    [manager.Key.Address],
                    [topic],
                    poolIds));
            }
            return filters.ToArray();
        }

        internal static object[] CreateRpcLogFilters(
            OnChainWatchedPoolSelection[] pools,
            ulong fromBlock,
            ulong toBlock)
        {
            return BuildPoolFilters(pools)
                .Select(filter =>
                {
                    var rpcFilter = filter.CreateRpcFilter();
                    rpcFilter["fromBlock"] = $"0x{fromBlock:x}";
                    rpcFilter["toBlock"] = $"0x{toBlock:x}";
                    return (object)rpcFilter;
                })
                .ToArray();
        }

        private async Task SendSubscriptionAsync(
            ClientWebSocket socket,
            long requestId,
            object[] parameters,
            SubscriptionKind kind,
            Dictionary<long, SubscriptionKind> pending,
            CancellationToken cancellationToken)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0",
                id = requestId,
                method = "eth_subscribe",
                @params = parameters
            });
            pending.Add(requestId, kind);
            _configuration.Usage?.Rpc("eth_subscribe");
            await socket.SendAsync(
                payload,
                WebSocketMessageType.Text,
                true,
                cancellationToken).ConfigureAwait(false);
        }

        private static void ProcessSubscriptionResponse(
            JsonElement root,
            JsonElement idElement,
            Dictionary<long, SubscriptionKind> pending,
            Dictionary<string, SubscriptionKind> active)
        {
            if (!idElement.TryGetInt64(out var requestId)
                || !pending.Remove(requestId, out var kind))
            {
                return;
            }
            if (root.TryGetProperty("error", out _)
                || !root.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(result.GetString()))
            {
                throw new InvalidDataException("The EVM provider rejected a WebSocket subscription.");
            }
            active[result.GetString()!] = kind;
        }

        private async Task<byte[]?> ReceiveMessageAsync(
            ClientWebSocket socket,
            CancellationToken cancellationToken)
        {
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new InvalidDataException("The EVM provider sent a non-text WebSocket message.");
                }
                if (output.Length + result.Count > MaximumMessageBytes)
                {
                    throw new InvalidDataException("The EVM WebSocket message exceeded the size limit.");
                }
                output.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    var message = output.ToArray();
                    _configuration.Usage?.WebSocket(message);
                    return message;
                }
            }
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

        private static bool TryReadAddress(JsonElement parent, string name, out string value)
        {
            value = string.Empty;
            return parent.TryGetProperty(name, out var element)
                   && element.ValueKind == JsonValueKind.String
                   && EvmAddress.TryNormalize(element.GetString(), out value);
        }

        private static bool TryReadData(JsonElement parent, string name, out string value)
        {
            value = string.Empty;
            if (!parent.TryGetProperty(name, out var element)
                || element.ValueKind != JsonValueKind.String
                || !EvmAddress.IsData(element.GetString(), MaximumLogDataBytes))
            {
                return false;
            }
            value = element.GetString()!.ToLowerInvariant();
            return true;
        }

        private enum SubscriptionKind
        {
            Heads,
            Logs
        }

        private sealed class PoolLogFilter
        {
            private readonly string[] _addresses;
            private readonly string[] _eventTopics;
            private readonly string[] _poolIds;
            private readonly HashSet<string> _addressSet;
            private readonly HashSet<string> _eventTopicSet;
            private readonly HashSet<string> _poolIdSet;

            internal PoolLogFilter(string[] addresses, string[] eventTopics, string[] poolIds)
            {
                _addresses = addresses;
                _eventTopics = eventTopics;
                _poolIds = poolIds;
                _addressSet = addresses.ToHashSet(StringComparer.Ordinal);
                _eventTopicSet = eventTopics.ToHashSet(StringComparer.Ordinal);
                _poolIdSet = poolIds.ToHashSet(StringComparer.Ordinal);
            }

            internal Dictionary<string, object> CreateRpcFilter()
            {
                object addresses = _addresses.Length == 1 ? _addresses[0] : _addresses;
                object topics = _eventTopics.Length == 1 ? _eventTopics[0] : _eventTopics;
                var topicFilter = _poolIds.Length == 0
                    ? new object[] { topics }
                    : [topics, _poolIds.Length == 1 ? _poolIds[0] : _poolIds];
                return new Dictionary<string, object>
                {
                    ["address"] = addresses,
                    ["topics"] = topicFilter
                };
            }

            internal bool Matches(EvmLogUpdate update)
            {
                return _addressSet.Contains(update.Address)
                       && update.Topics.Length > 0
                       && _eventTopicSet.Contains(update.Topics[0])
                       && (_poolIdSet.Count == 0
                           || update.Topics.Length > 1 && _poolIdSet.Contains(update.Topics[1]));
            }
        }
    }
}

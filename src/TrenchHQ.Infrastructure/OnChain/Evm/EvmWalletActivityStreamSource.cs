using TrenchHQ.Core.OnChain.Evm;
using TrenchHQ.Core.OnChain;
using TrenchHQ.Core.Providers;
using TrenchHQ.Core.Wallets;
using TrenchHQ.Core.Widgets;
using TrenchHQ.Infrastructure.OnChain.Solana;
using TrenchHQ.Infrastructure.Providers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrenchHQ.Infrastructure.OnChain.Evm
{
    internal sealed class EvmWalletActivityStreamSource
    {
        private const int MaximumMessageBytes = 2 * 1024 * 1024;
        internal const int MaximumTokenMetadataEntries = 512;
        internal const int MaximumTraceBatchBlocks = 20;
        internal static readonly TimeSpan FinalityRefreshInterval = TimeSpan.FromMinutes(1);
        internal static readonly TimeSpan TraceBatchInterval = TimeSpan.FromSeconds(5);

        private readonly OnChainProviderConfiguration _configuration;
        private readonly string? _apiKey;
        private readonly Uri _endpoint;
        private readonly string _sourceId;

        internal EvmWalletActivityStreamSource(
            OnChainProviderConfiguration configuration,
            string? apiKey)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            var preset = OnChainProviderCatalog.Get(configuration.ProviderType);
            if (preset.StreamTransport != OnChainStreamTransport.EvmWebSocket
                || configuration.ChainNamespace != ChainNamespaces.Eip155)
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
            _apiKey = apiKey;
            _endpoint = OnChainProviderEndpointBuilder.Build(
                configuration.StreamEndpoint,
                apiKey,
                preset.StreamAuthenticationMode);
            _sourceId = $"{configuration.ProviderType}:{configuration.Id}:wallet-websocket";
        }

        internal async Task RunAsync(
            SavedTrackedWallet[] wallets,
            Action<WalletActivityUpdate> onActivity,
            Action<bool> onLive,
            CancellationToken cancellationToken)
        {
            if (wallets.Length == 0)
            {
                return;
            }

            using var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            socket.Options.CollectHttpResponseDetails = true;
            try { await socket.ConnectAsync(_endpoint, cancellationToken).ConfigureAwait(false); }
            catch (WebSocketException) when ((int)socket.HttpStatusCode is 401 or 402 or 403 or 429)
            {
                if ((int)socket.HttpStatusCode == 402) _configuration.Usage?.QuotaExceeded();
                throw new IOException($"The provider rejected WebSocket access: HTTP {(int)socket.HttpStatusCode}.");
            }

            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancellationToken = lifetime.Token;
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var rpc = new EvmJsonRpcClient(httpClient, _configuration, _apiKey);
            await RunConnectedAsync(socket, rpc, wallets, onActivity, onLive, cancellationToken).ConfigureAwait(false);
        }

        internal async Task RunConnectedAsync(WebSocket socket, EvmJsonRpcClient rpc,
            SavedTrackedWallet[] wallets, Action<WalletActivityUpdate> onActivity,
            Action<bool> onLive, CancellationToken cancellationToken)
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancellationToken = lifetime.Token;
            var pending = new HashSet<long>();
            var active = new Dictionary<string, SubscriptionKind>(StringComparer.Ordinal);
            var buffered = new EvmWalletRecoveryBuffer();
            var requestId = 0L;
            await SendSubscriptionAsync(socket, ++requestId, new object[] { "newHeads" }, pending, cancellationToken)
                .ConfigureAwait(false);
            foreach (var filter in WalletActivityRules.CreateEvmLogFilters(wallets))
            {
                await SendSubscriptionAsync(
                        socket,
                        ++requestId,
                        new object[] { "logs", filter },
                        pending,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            while (pending.Count > 0)
            {
                var message = await ReceiveMessageAsync(socket, cancellationToken).ConfigureAwait(false)
                              ?? throw new IOException("The EVM wallet WebSocket closed during setup.");
                using var document = JsonDocument.Parse(message);
                if (document.RootElement.TryGetProperty("id", out var id))
                {
                    ProcessSubscriptionResponse(document.RootElement, id, pending, active);
                }
                else
                {
                    buffered.Enqueue(message);
                }
            }

            var tracked = new Dictionary<string, WalletActivityUpdate>(StringComparer.Ordinal);
            var tokenMetadata = new TokenMetadataCache();
            var heads = new EvmHeadTracker();
            var receiveTask = ReceiveMessageAsync(socket, cancellationToken);
            var supportsTraceFilter = true;
            ulong? safeBlock = null;
            ulong? finalizedBlock = null;
            var lastFinalityRefresh = DateTimeOffset.UtcNow;
            var traceBatch = new EvmWalletTraceBatch(MaximumTraceBatchBlocks);
            var work = new Queue<Func<Task<Action>>>();
            Task<Action>? detailTask = null;
            long revision = 0;
            void Enqueue(Func<Task<Action>> job)
            {
                if (work.Count >= 1024)
                    throw new IOException("The provider cannot keep up with this wallet activity. Configure another provider or watch fewer wallets.");
                work.Enqueue(job);
            }
            void QueueTraces()
            {
                if (!supportsTraceFilter || traceBatch.Count == 0) return;
                var batch = new EvmWalletTraceBatch(MaximumTraceBatchBlocks);
                foreach (var item in traceBatch.ObservedAtByBlock) batch.Add(item.Key, item.Value);
                traceBatch.Clear();
                var expectedRevision = revision;
                Enqueue(async () =>
                {
                    var updates = new List<WalletActivityUpdate>();
                    var supported = await FlushTraceBatchAsync(rpc, wallets, batch,
                        new Dictionary<string, WalletActivityUpdate>(), true, updates.Add, cancellationToken).ConfigureAwait(false);
                    return () =>
                    {
                        if (!supported && supportsTraceFilter) { supportsTraceFilter = false; onLive(false); }
                        if (revision == expectedRevision)
                            foreach (var update in updates) PublishAndTrack(update, tracked, onActivity);
                    };
                });
            }
            void ProcessMessage(byte[] message)
            {
                using var document = JsonDocument.Parse(message);
                var root = document.RootElement;
                if (!root.TryGetProperty("params", out var parameters)
                    || !parameters.TryGetProperty("subscription", out var subscription)
                    || subscription.ValueKind != JsonValueKind.String
                    || !active.TryGetValue(subscription.GetString()!, out var kind)
                    || !parameters.TryGetProperty("result", out var result)) return;
                var expectedRevision = revision;
                if (kind == SubscriptionKind.Logs)
                {
                    var log = EvmWebSocketStreamSource.ParseLog(result, _configuration.ChainId, 0,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    var updates = WalletActivityRules.ParseEvmTransfer(log, wallets, _sourceId).ToArray();
                    foreach (var update in updates)
                    {
                        if (update.Removed) { tracked.Remove(update.EventId); onActivity(update); continue; }
                        if (tracked.ContainsKey(update.EventId)) continue;
                        // Show the transfer immediately; token metadata is optional enrichment.
                        PublishAndTrack(update, tracked, onActivity);
                        if (update.AssetId != null || update.AssetAddress == null) continue;
                        Enqueue(async () =>
                        {
                            var metadata = await GetTokenMetadataAsync(rpc, update.AssetAddress,
                                tokenMetadata, cancellationToken).ConfigureAwait(false);
                            return () =>
                            {
                                if (revision != expectedRevision || !tracked.TryGetValue(update.EventId, out var current)) return;
                                var enriched = WalletActivityRules.WithConfirmation(current, current.Confirmation);
                                enriched.AssetSymbol = metadata.Symbol;
                                enriched.AssetDecimals = metadata.Decimals;
                                tracked[enriched.EventId] = enriched;
                                onActivity(enriched);
                            };
                        });
                    }
                    return;
                }
                var head = EvmWebSocketStreamSource.ParseHead(result, _configuration.ChainId, 0,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                var transition = heads.Apply(head);
                if (transition.Kind == EvmHeadTransitionKind.Duplicate) return;
                if (transition.Kind == EvmHeadTransitionKind.Reorg)
                {
                    revision++;
                    RemoveActivitiesAfter(tracked, transition.CommonAncestorNumber!.Value, onActivity);
                    traceBatch.RemoveAfter(transition.CommonAncestorNumber.Value);
                }
                else if (transition.Kind is EvmHeadTransitionKind.Gap or EvmHeadTransitionKind.SnapshotRequired)
                {
                    if (transition.Kind == EvmHeadTransitionKind.SnapshotRequired)
                    {
                        revision++;
                        RemoveUnfinalizedActivities(tracked, onActivity);
                        traceBatch.Clear();
                    }
                    heads.Reset(head);
                }
                expectedRevision = revision;
                // Scan only the head actually received, never fill a reconnect/gap with old blocks.
                Enqueue(async () =>
                {
                    if (revision != expectedRevision) return () => { };
                    var updates = new List<WalletActivityUpdate>();
                    var observedAt = await ScanBlockTransactionsAsync(rpc, wallets, head.Number, head.Hash,
                        new Dictionary<string, WalletActivityUpdate>(), updates.Add, cancellationToken).ConfigureAwait(false);
                    return () =>
                    {
                        if (revision != expectedRevision) return;
                        foreach (var update in updates) PublishAndTrack(update, tracked, onActivity);
                        if (supportsTraceFilter)
                        {
                            if (!traceBatch.CanAdd(head.Number, head.Number)) QueueTraces();
                            traceBatch.Add(head.Number, observedAt);
                        }
                        PromoteActivities(tracked, heads.Tip?.Number ?? head.Number, safeBlock, finalizedBlock, onActivity);
                    };
                });
            }
            onLive(supportsTraceFilter);
            var maintenanceTask = Task.Delay(TraceBatchInterval, cancellationToken);
            var idleTask = Task.Delay(Timeout.Infinite, cancellationToken);
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (detailTask == null && work.TryDequeue(out var job)) detailTask = job();
                    await Task.WhenAny(receiveTask, maintenanceTask, (Task?)detailTask ?? idleTask,
                        buffered.Count > 0 ? Task.CompletedTask : idleTask).ConfigureAwait(false);
                    if (detailTask?.IsCompleted == true)
                    {
                        (await detailTask.ConfigureAwait(false))();
                        detailTask = null;
                    }
                    if (buffered.Count > 0) { buffered.TryDequeue(out var message); ProcessMessage(message); }
                    else if (receiveTask.IsCompleted)
                    {
                        var message = await receiveTask.ConfigureAwait(false)
                            ?? throw new IOException("The EVM wallet WebSocket closed unexpectedly.");
                        receiveTask = ReceiveMessageAsync(socket, cancellationToken);
                        ProcessMessage(message);
                    }
                    if (maintenanceTask.IsCompleted)
                    {
                        QueueTraces();
                        if (DateTimeOffset.UtcNow - lastFinalityRefresh >= FinalityRefreshInterval)
                        {
                            lastFinalityRefresh = DateTimeOffset.UtcNow;
                            Enqueue(async () =>
                            {
                                var safe = await TryGetFinalityBlockAsync(rpc, "safe", cancellationToken).ConfigureAwait(false);
                                var finalized = await TryGetFinalityBlockAsync(rpc, "finalized", cancellationToken).ConfigureAwait(false);
                                return () =>
                                {
                                    safeBlock = safe ?? safeBlock;
                                    finalizedBlock = finalized ?? finalizedBlock;
                                    PromoteActivities(tracked, heads.Tip?.Number ?? 0, safeBlock, finalizedBlock, onActivity);
                                };
                            });
                        }
                        maintenanceTask = Task.Delay(TraceBatchInterval, cancellationToken);
                    }
                }
                throw new IOException("The EVM wallet WebSocket ended unexpectedly.");
            }
            finally
            {
                lifetime.Cancel();
                try { if (detailTask != null) await detailTask.ConfigureAwait(false); } catch { }
                try { await receiveTask.ConfigureAwait(false); } catch { }
            }
        }

        private async Task<bool> FlushTraceBatchAsync(
            EvmJsonRpcClient rpc,
            SavedTrackedWallet[] wallets,
            EvmWalletTraceBatch traceBatch,
            IDictionary<string, WalletActivityUpdate> tracked,
            bool supportsTraceFilter,
            Action<WalletActivityUpdate> onActivity,
            CancellationToken cancellationToken)
        {
            if (!supportsTraceFilter || traceBatch.Count == 0)
            {
                return supportsTraceFilter;
            }

            try
            {
                return await TryPublishTracesAsync(
                        rpc,
                        wallets,
                        traceBatch.FromBlock,
                        traceBatch.ToBlock,
                        traceBatch.ObservedAtByBlock,
                        tracked,
                        onActivity,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                traceBatch.Clear();
            }
        }

        private async Task<long> ScanBlockTransactionsAsync(
            EvmJsonRpcClient rpc,
            SavedTrackedWallet[] wallets,
            ulong blockNumber,
            string expectedHash,
            IDictionary<string, WalletActivityUpdate> tracked,
            Action<WalletActivityUpdate> onActivity,
            CancellationToken cancellationToken)
        {
            var block = await OnChainRequestRetry.RunAsync(() => rpc.GetWalletBlockAsync($"0x{blockNumber:x}", cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            if (block.Number != blockNumber || !string.Equals(block.Hash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The EVM provider returned the wrong wallet block.");
            }
            var observedAt = checked((long)block.Timestamp * 1000);
            foreach (var transaction in block.Transactions)
            {
                foreach (var update in WalletActivityRules.ParseEvmTransaction(
                             transaction,
                             _configuration.ChainId,
                             block.Number,
                             observedAt,
                             wallets,
                             _sourceId + ":block"))
                {
                    PublishAndTrack(update, tracked, onActivity);
                }
            }
            return observedAt;
        }

        private async Task<bool> TryPublishTracesAsync(
            EvmJsonRpcClient rpc,
            SavedTrackedWallet[] wallets,
            ulong fromBlock,
            ulong toBlock,
            IReadOnlyDictionary<ulong, long> observedAtByBlock,
            IDictionary<string, WalletActivityUpdate> tracked,
            Action<WalletActivityUpdate> onActivity,
            CancellationToken cancellationToken)
        {
            try
            {
                var addresses = wallets.Select(static wallet => wallet.Address).ToArray();
                foreach (var outgoing in new[] { true, false })
                {
                    var traces = await OnChainRequestRetry.RunAsync(() => rpc.GetAddressTracesAsync(
                            fromBlock,
                            toBlock,
                            addresses,
                            outgoing,
                            cancellationToken), cancellationToken)
                        .ConfigureAwait(false);
                    foreach (var trace in traces)
                    {
                        if (!observedAtByBlock.TryGetValue(trace.BlockNumber, out var observedAtUnixMs))
                        {
                            continue;
                        }
                        foreach (var update in WalletActivityRules.ParseEvmTrace(
                                     trace,
                                     _configuration.ChainId,
                                     observedAtUnixMs,
                                     wallets,
                                     _sourceId + ":trace"))
                        {
                            PublishAndTrack(update, tracked, onActivity);
                        }
                    }
                }
                return true;
            }
            catch (EvmJsonRpcException exception) when (exception.Kind == EvmRpcFailureKind.RpcError)
            {
                return false;
            }
        }

        private async Task<TokenMetadata> GetTokenMetadataAsync(
            EvmJsonRpcClient rpc,
            string address,
            TokenMetadataCache cache,
            CancellationToken cancellationToken)
        {
            if (cache.TryGetValue(address, out var cached))
            {
                return cached;
            }
            var symbol = WalletActivityPresentationRules.GetKnownTokenSymbol(_configuration.ChainId, address);
            byte? decimals = null;
            try
            {
                var result = await OnChainRequestRetry.RunAsync(() => rpc.CallAsync(address, EthereumAbi.SymbolSelector, "latest", cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
                if (result.ValueKind == JsonValueKind.String
                    && EthereumAbi.TryDecodeString(result.GetString(), out var decoded))
                {
                    symbol = decoded;
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is not OnChainUsageBudgetException
                && exception is not EvmJsonRpcException { Kind: EvmRpcFailureKind.RateLimited or EvmRpcFailureKind.AuthenticationRejected })
            {
            }
            try
            {
                var result = await OnChainRequestRetry.RunAsync(() => rpc.CallAsync(address, EthereumAbi.DecimalsSelector, "latest", cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
                if (result.ValueKind == JsonValueKind.String
                    && EthereumAbi.TryDecodeByte(result.GetString(), out var decoded))
                {
                    decimals = decoded;
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is not OnChainUsageBudgetException
                && exception is not EvmJsonRpcException { Kind: EvmRpcFailureKind.RateLimited or EvmRpcFailureKind.AuthenticationRejected })
            {
            }
            var metadata = new TokenMetadata(symbol, decimals);
            cache.Set(address, metadata);
            return metadata;
        }

        private static void PublishAndTrack(
            WalletActivityUpdate update,
            IDictionary<string, WalletActivityUpdate> tracked,
            Action<WalletActivityUpdate> onActivity)
        {
            if (tracked.TryGetValue(update.EventId, out var existing)
                && !WalletActivityRules.IsConfirmationUpgrade(existing.Confirmation, update.Confirmation))
            {
                return;
            }
            tracked[update.EventId] = update;
            if (tracked.Count > 2048) tracked.Remove(tracked.Keys.First());
            onActivity(update);
        }

        private static void PromoteActivities(
            IDictionary<string, WalletActivityUpdate> tracked,
            ulong headBlock,
            ulong? safeBlock,
            ulong? finalizedBlock,
            Action<WalletActivityUpdate> onActivity)
        {
            foreach (var activity in tracked.Values.ToArray())
            {
                var confirmation = finalizedBlock.HasValue && activity.ChainPosition <= finalizedBlock.Value
                    ? "finalized"
                    : safeBlock.HasValue && activity.ChainPosition <= safeBlock.Value
                        ? "safe"
                        : activity.ChainPosition < headBlock ? "confirmed" : "head";
                if (!WalletActivityRules.IsConfirmationUpgrade(activity.Confirmation, confirmation))
                {
                    continue;
                }
                var promoted = WalletActivityRules.WithConfirmation(activity, confirmation);
                tracked[activity.EventId] = promoted;
                onActivity(promoted);
                if (confirmation == "finalized")
                {
                    tracked.Remove(activity.EventId);
                }
            }
        }

        private static void RemoveActivitiesAfter(
            IDictionary<string, WalletActivityUpdate> tracked,
            ulong blockNumber,
            Action<WalletActivityUpdate> onActivity)
        {
            foreach (var activity in tracked.Values
                         .Where(activity => activity.ChainPosition > blockNumber)
                         .ToArray())
            {
                var removed = WalletActivityRules.WithConfirmation(activity, activity.Confirmation);
                removed.Removed = true;
                tracked.Remove(activity.EventId);
                onActivity(removed);
            }
        }

        private static void RemoveUnfinalizedActivities(
            IDictionary<string, WalletActivityUpdate> tracked,
            Action<WalletActivityUpdate> onActivity)
        {
            foreach (var activity in tracked.Values.ToArray())
            {
                var removed = WalletActivityRules.WithConfirmation(activity, activity.Confirmation);
                removed.Removed = true;
                tracked.Remove(activity.EventId);
                onActivity(removed);
            }
        }

        private static async Task<ulong?> TryGetFinalityBlockAsync(
            EvmJsonRpcClient rpc,
            string blockTag,
            CancellationToken cancellationToken)
        {
            try
            {
                return (await OnChainRequestRetry.RunAsync(() => rpc.GetBlockAsync(blockTag, cancellationToken), cancellationToken).ConfigureAwait(false)).Number;
            }
            catch (EvmJsonRpcException exception) when (exception.Kind == EvmRpcFailureKind.RpcError)
            {
                return null;
            }
        }

        private async Task SendSubscriptionAsync(
            WebSocket socket,
            long requestId,
            object[] parameters,
            ISet<long> pending,
            CancellationToken cancellationToken)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0",
                id = requestId,
                method = "eth_subscribe",
                @params = parameters
            });
            pending.Add(requestId);
            _configuration.Usage?.Rpc("eth_subscribe");
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken)
                .ConfigureAwait(false);
        }

        private static void ProcessSubscriptionResponse(
            JsonElement root,
            JsonElement id,
            ISet<long> pending,
            IDictionary<string, SubscriptionKind> active)
        {
            var requestId = id.GetInt64();
            if (!pending.Remove(requestId))
            {
                return;
            }
            if (root.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var value)
                    ? value.GetString()
                    : "Unknown subscription error";
                throw new InvalidOperationException("EVM wallet subscription failed: " + message);
            }
            var subscriptionId = root.GetProperty("result").GetString()
                                 ?? throw new InvalidDataException("The EVM subscription ID is invalid.");
            active[subscriptionId] = requestId == 1 ? SubscriptionKind.Heads : SubscriptionKind.Logs;
        }

        private async Task<byte[]?> ReceiveMessageAsync(
            WebSocket socket,
            CancellationToken cancellationToken)
        {
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new InvalidDataException("The EVM provider returned a non-text wallet message.");
                }
                if (output.Length + result.Count > MaximumMessageBytes)
                {
                    throw new InvalidDataException("The EVM wallet message exceeded the local safety limit.");
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

        private enum SubscriptionKind
        {
            Heads,
            Logs
        }

        private sealed record TokenMetadata(string? Symbol, byte? Decimals);

        private sealed class TokenMetadataCache
        {
            private readonly Dictionary<string, TokenMetadata> _entries =
                new(StringComparer.OrdinalIgnoreCase);
            private readonly Queue<string> _order = new();

            internal bool TryGetValue(string address, out TokenMetadata metadata) =>
                _entries.TryGetValue(address, out metadata!);

            internal void Set(string address, TokenMetadata metadata)
            {
                if (_entries.ContainsKey(address))
                {
                    _entries[address] = metadata;
                    return;
                }

                _entries[address] = metadata;
                _order.Enqueue(address);
                while (_order.Count > MaximumTokenMetadataEntries)
                {
                    _entries.Remove(_order.Dequeue());
                }
            }
        }
    }

    internal sealed class EvmWalletRecoveryBuffer
    {
        internal const int MaximumMessages = 256;
        internal const int MaximumBytes = 8 * 1024 * 1024;

        private readonly Queue<byte[]> _messages = new();
        private int _bytes;

        internal int Count => _messages.Count;
        internal int Bytes => _bytes;

        internal void Enqueue(byte[] message)
        {
            ArgumentNullException.ThrowIfNull(message);
            if (_messages.Count >= MaximumMessages
                || message.Length > MaximumBytes - _bytes)
            {
                throw new InvalidDataException("EVM wallet recovery notifications exceeded the local safety limit.");
            }

            _messages.Enqueue(message);
            _bytes += message.Length;
        }

        internal bool TryDequeue(out byte[] message)
        {
            if (!_messages.TryDequeue(out message!))
            {
                return false;
            }

            _bytes -= message.Length;
            return true;
        }
    }
}

using TrenchHQ.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrenchHQ.Helpers
{
    internal sealed class EvmWalletActivityStreamSource
    {
        private const int MaximumMessageBytes = 2 * 1024 * 1024;
        private const ulong InitialHistoryBlocks = 20;
        private const ulong MaximumRecoveryBlocks = 250;
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
            ulong? lastObservedBlock,
            Action<WalletActivityUpdate> onActivity,
            Action<ulong> onBlockObserved,
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

            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var rpc = new EvmJsonRpcClient(httpClient, _configuration, _apiKey);
            var tracked = new Dictionary<string, WalletActivityUpdate>(StringComparer.Ordinal);
            var tokenMetadata = new TokenMetadataCache();
            var heads = new EvmHeadTracker();
            var receiveTask = ReceiveMessageAsync(socket, cancellationToken);
            RecoveryResult recovery;
            using (var recoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var recoveryTask = RecoverAsync(
                    rpc,
                    wallets,
                    lastObservedBlock,
                    tracked,
                    tokenMetadata,
                    onActivity,
                    onBlockObserved,
                    recoveryCancellation.Token);
                try
                {
                    while (!recoveryTask.IsCompleted)
                    {
                        var completed = await Task.WhenAny(recoveryTask, receiveTask).ConfigureAwait(false);
                        if (completed == recoveryTask)
                        {
                            break;
                        }

                        var message = await receiveTask.ConfigureAwait(false)
                                      ?? throw new IOException("The EVM wallet WebSocket closed during recovery.");
                        buffered.Enqueue(message);
                        receiveTask = ReceiveMessageAsync(socket, cancellationToken);
                    }
                    recovery = await recoveryTask.ConfigureAwait(false);
                    while (receiveTask.IsCompleted)
                    {
                        var message = await receiveTask.ConfigureAwait(false)
                                      ?? throw new IOException("The EVM wallet WebSocket closed during recovery.");
                        buffered.Enqueue(message);
                        receiveTask = ReceiveMessageAsync(socket, cancellationToken);
                    }
                    if (socket.State != WebSocketState.Open)
                    {
                        throw new IOException("The EVM wallet WebSocket closed during recovery.");
                    }
                }
                catch
                {
                    recoveryCancellation.Cancel();
                    try
                    {
                        await recoveryTask.ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                    throw;
                }
            }
            heads.Reset(ToHead(recovery.Latest));
            var supportsTraceFilter = recovery.SupportsTraceFilter;
            var safeBlock = recovery.SafeBlock;
            var finalizedBlock = recovery.FinalizedBlock;
            var lastFinalityRefresh = DateTimeOffset.UtcNow;
            var traceBatch = new EvmWalletTraceBatch(MaximumTraceBatchBlocks);
            onLive(supportsTraceFilter);

            while (buffered.TryDequeue(out var bufferedMessage))
            {
                var previouslySupported = supportsTraceFilter;
                supportsTraceFilter = await ProcessNotificationAsync(
                        bufferedMessage,
                        rpc,
                        wallets,
                        active,
                        heads,
                        tracked,
                        tokenMetadata,
                        traceBatch,
                        supportsTraceFilter,
                        safeBlock,
                        finalizedBlock,
                        onActivity,
                        onBlockObserved,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (previouslySupported && !supportsTraceFilter)
                {
                    onLive(false);
                }
            }

            var maintenanceTask = Task.Delay(TraceBatchInterval, cancellationToken);
            while (socket.State == WebSocketState.Open)
            {
                var completed = await Task.WhenAny(receiveTask, maintenanceTask).ConfigureAwait(false);
                if (completed == maintenanceTask)
                {
                    var supportedBeforeMaintenance = supportsTraceFilter;
                    supportsTraceFilter = await FlushTraceBatchAsync(
                            rpc,
                            wallets,
                            traceBatch,
                            tracked,
                            supportsTraceFilter,
                            onActivity,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (supportedBeforeMaintenance && !supportsTraceFilter)
                    {
                        onLive(false);
                    }
                    if (DateTimeOffset.UtcNow - lastFinalityRefresh >= FinalityRefreshInterval)
                    {
                        safeBlock = await TryGetFinalityBlockAsync(rpc, "safe", cancellationToken)
                            .ConfigureAwait(false) ?? safeBlock;
                        finalizedBlock = await TryGetFinalityBlockAsync(rpc, "finalized", cancellationToken)
                            .ConfigureAwait(false) ?? finalizedBlock;
                        PromoteActivities(tracked, heads.Tip?.Number ?? 0, safeBlock, finalizedBlock, onActivity);
                        lastFinalityRefresh = DateTimeOffset.UtcNow;
                    }
                    maintenanceTask = Task.Delay(TraceBatchInterval, cancellationToken);
                    continue;
                }

                var message = await receiveTask.ConfigureAwait(false);
                if (message == null)
                {
                    throw new IOException("The EVM wallet WebSocket closed unexpectedly.");
                }
                receiveTask = ReceiveMessageAsync(socket, cancellationToken);
                var previouslySupported = supportsTraceFilter;
                supportsTraceFilter = await ProcessNotificationAsync(
                        message,
                        rpc,
                        wallets,
                        active,
                        heads,
                        tracked,
                        tokenMetadata,
                        traceBatch,
                        supportsTraceFilter,
                        safeBlock,
                        finalizedBlock,
                        onActivity,
                        onBlockObserved,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (previouslySupported && !supportsTraceFilter)
                {
                    onLive(false);
                }
                if (supportsTraceFilter && traceBatch.Count >= MaximumTraceBatchBlocks)
                {
                    supportsTraceFilter = await FlushTraceBatchAsync(
                            rpc,
                            wallets,
                            traceBatch,
                            tracked,
                            supportsTraceFilter,
                            onActivity,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!supportsTraceFilter)
                    {
                        onLive(false);
                    }
                }
            }
            throw new IOException("The EVM wallet WebSocket ended unexpectedly.");
        }

        private async Task<RecoveryResult> RecoverAsync(
            EvmJsonRpcClient rpc,
            SavedTrackedWallet[] wallets,
            ulong? lastObservedBlock,
            IDictionary<string, WalletActivityUpdate> tracked,
            TokenMetadataCache tokenMetadata,
            Action<WalletActivityUpdate> onActivity,
            Action<ulong> onBlockObserved,
            CancellationToken cancellationToken)
        {
            var latest = await rpc.GetBlockAsync("latest", cancellationToken).ConfigureAwait(false);
            var first = lastObservedBlock.HasValue
                ? Math.Min(
                    lastObservedBlock.Value >= InitialHistoryBlocks - 1
                        ? lastObservedBlock.Value - InitialHistoryBlocks + 1
                        : 0,
                    latest.Number)
                : latest.Number >= InitialHistoryBlocks
                    ? latest.Number - InitialHistoryBlocks + 1
                    : 0;
            if (latest.Number - first + 1 > MaximumRecoveryBlocks)
            {
                first = latest.Number - MaximumRecoveryBlocks + 1;
            }

            var recoveredTokenLogs = false;
            if (first <= latest.Number)
            {
                try
                {
                    foreach (var filter in WalletActivityRules.CreateEvmLogFilters(
                                 wallets,
                                 $"0x{first:x}",
                                 $"0x{latest.Number:x}"))
                    {
                        var result = await rpc.GetLogsAsync(filter, cancellationToken).ConfigureAwait(false);
                        if (result.ValueKind != JsonValueKind.Array)
                        {
                            throw new InvalidDataException("The EVM wallet log replay response is invalid.");
                        }
                        foreach (var item in result.EnumerateArray())
                        {
                            await PublishLogAsync(
                                    item,
                                    rpc,
                                    wallets,
                                    tracked,
                                    tokenMetadata,
                                    onActivity,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                    recoveredTokenLogs = true;
                }
                catch (Exception exception) when (CanContinueWithoutTokenLogReplay(exception))
                {
                    // Some key-free endpoints require an address in eth_getLogs. Live topic
                    // subscriptions and full-block native activity remain available.
                }
            }

            var supportsTraceFilter = true;
            if (first <= latest.Number)
            {
                if (!recoveredTokenLogs)
                {
                    for (var blockNumber = first; blockNumber <= latest.Number; blockNumber++)
                    {
                        if (!await TryRecoverBlockReceiptLogsAsync(
                                rpc,
                                wallets,
                                blockNumber,
                                tracked,
                                tokenMetadata,
                                onActivity,
                                cancellationToken).ConfigureAwait(false))
                        {
                            break;
                        }
                        if (blockNumber == latest.Number)
                        {
                            break;
                        }
                    }
                }
                var observedAtByBlock = new Dictionary<ulong, long>();
                for (var blockNumber = first; blockNumber <= latest.Number; blockNumber++)
                {
                    observedAtByBlock[blockNumber] = await ScanBlockTransactionsAsync(
                            rpc,
                            wallets,
                            blockNumber,
                            tracked,
                            onActivity,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (blockNumber == latest.Number)
                    {
                        break;
                    }
                }
                supportsTraceFilter = await TryPublishTracesAsync(
                        rpc,
                        wallets,
                        first,
                        latest.Number,
                        observedAtByBlock,
                        tracked,
                        onActivity,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            var safeBlock = await TryGetFinalityBlockAsync(rpc, "safe", cancellationToken).ConfigureAwait(false);
            var finalizedBlock = await TryGetFinalityBlockAsync(rpc, "finalized", cancellationToken)
                .ConfigureAwait(false);
            PromoteActivities(tracked, latest.Number, safeBlock, finalizedBlock, onActivity);
            onBlockObserved(latest.Number);
            return new RecoveryResult(latest, supportsTraceFilter, safeBlock, finalizedBlock);
        }

        internal static bool CanContinueWithoutTokenLogReplay(Exception exception) =>
            exception is EvmJsonRpcException { Kind: EvmRpcFailureKind.RpcError };

        private async Task<bool> ProcessNotificationAsync(
            byte[] message,
            EvmJsonRpcClient rpc,
            SavedTrackedWallet[] wallets,
            IReadOnlyDictionary<string, SubscriptionKind> active,
            EvmHeadTracker heads,
            IDictionary<string, WalletActivityUpdate> tracked,
            TokenMetadataCache tokenMetadata,
            EvmWalletTraceBatch traceBatch,
            bool supportsTraceFilter,
            ulong? safeBlock,
            ulong? finalizedBlock,
            Action<WalletActivityUpdate> onActivity,
            Action<ulong> onBlockObserved,
            CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (!root.TryGetProperty("method", out var method)
                || method.GetString() != "eth_subscription"
                || !root.TryGetProperty("params", out var parameters)
                || !parameters.TryGetProperty("subscription", out var subscription)
                || subscription.ValueKind != JsonValueKind.String
                || !active.TryGetValue(subscription.GetString()!, out var kind)
                || !parameters.TryGetProperty("result", out var result))
            {
                return supportsTraceFilter;
            }

            if (kind == SubscriptionKind.Logs)
            {
                await PublishLogAsync(
                        result,
                        rpc,
                        wallets,
                        tracked,
                        tokenMetadata,
                        onActivity,
                        cancellationToken)
                    .ConfigureAwait(false);
                return supportsTraceFilter;
            }

            var head = EvmWebSocketStreamSource.ParseHead(
                result,
                _configuration.ChainId,
                0,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (heads.Tip != null && head.Number <= heads.Tip.Number && !heads.Contains(head.Number, head.Hash))
            {
                return supportsTraceFilter;
            }
            var transition = heads.Apply(head);
            ulong scanFrom;
            switch (transition.Kind)
            {
                case EvmHeadTransitionKind.Duplicate:
                    return supportsTraceFilter;
                case EvmHeadTransitionKind.Reorg:
                    scanFrom = transition.CommonAncestorNumber!.Value + 1;
                    RemoveActivitiesAfter(tracked, transition.CommonAncestorNumber.Value, onActivity);
                    traceBatch.RemoveAfter(transition.CommonAncestorNumber.Value);
                    break;
                case EvmHeadTransitionKind.Gap:
                    scanFrom = transition.MissingFrom!.Value;
                    heads.Reset(head);
                    break;
                case EvmHeadTransitionKind.SnapshotRequired:
                    RemoveUnfinalizedActivities(tracked, onActivity);
                    traceBatch.Clear();
                    scanFrom = head.Number >= MaximumRecoveryBlocks
                        ? head.Number - MaximumRecoveryBlocks + 1
                        : 0;
                    heads.Reset(head);
                    break;
                default:
                    scanFrom = head.Number;
                    break;
            }

            if (supportsTraceFilter
                && traceBatch.Count > 0
                && !traceBatch.CanAdd(scanFrom, head.Number))
            {
                supportsTraceFilter = await FlushTraceBatchAsync(
                        rpc,
                        wallets,
                        traceBatch,
                        tracked,
                        supportsTraceFilter,
                        onActivity,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            for (var blockNumber = scanFrom; blockNumber <= head.Number; blockNumber++)
            {
                var observedAt = await ScanBlockTransactionsAsync(
                        rpc,
                        wallets,
                        blockNumber,
                        tracked,
                        onActivity,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (supportsTraceFilter)
                {
                    if (!traceBatch.CanAdd(blockNumber, blockNumber))
                    {
                        supportsTraceFilter = await FlushTraceBatchAsync(
                                rpc,
                                wallets,
                                traceBatch,
                                tracked,
                                supportsTraceFilter,
                                onActivity,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                if (supportsTraceFilter)
                {
                    traceBatch.Add(blockNumber, observedAt);
                }
                onBlockObserved(blockNumber);
                if (blockNumber == head.Number)
                {
                    break;
                }
            }
            PromoteActivities(tracked, head.Number, safeBlock, finalizedBlock, onActivity);
            return supportsTraceFilter;
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
            IDictionary<string, WalletActivityUpdate> tracked,
            Action<WalletActivityUpdate> onActivity,
            CancellationToken cancellationToken)
        {
            var block = await rpc.GetWalletBlockAsync($"0x{blockNumber:x}", cancellationToken)
                .ConfigureAwait(false);
            if (block.Number != blockNumber)
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
                    var traces = await rpc.GetAddressTracesAsync(
                            fromBlock,
                            toBlock,
                            addresses,
                            outgoing,
                            cancellationToken)
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
            catch (EvmJsonRpcException)
            {
                return false;
            }
        }

        private async Task PublishLogAsync(
            JsonElement result,
            EvmJsonRpcClient rpc,
            SavedTrackedWallet[] wallets,
            IDictionary<string, WalletActivityUpdate> tracked,
            TokenMetadataCache tokenMetadata,
            Action<WalletActivityUpdate> onActivity,
            CancellationToken cancellationToken)
        {
            var log = EvmWebSocketStreamSource.ParseLog(
                result,
                _configuration.ChainId,
                0,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            foreach (var update in WalletActivityRules.ParseEvmTransfer(log, wallets, _sourceId))
            {
                if (update.AssetId == null && update.AssetAddress != null)
                {
                    var metadata = await GetTokenMetadataAsync(
                            rpc,
                            update.AssetAddress,
                            tokenMetadata,
                            cancellationToken)
                        .ConfigureAwait(false);
                    update.AssetSymbol = metadata.Symbol;
                    update.AssetDecimals = metadata.Decimals;
                }
                if (update.Removed)
                {
                    tracked.Remove(update.EventId);
                    onActivity(update);
                }
                else
                {
                    PublishAndTrack(update, tracked, onActivity);
                }
            }
        }

        private async Task<bool> TryRecoverBlockReceiptLogsAsync(
            EvmJsonRpcClient rpc,
            SavedTrackedWallet[] wallets,
            ulong blockNumber,
            IDictionary<string, WalletActivityUpdate> tracked,
            TokenMetadataCache tokenMetadata,
            Action<WalletActivityUpdate> onActivity,
            CancellationToken cancellationToken)
        {
            JsonElement receipts;
            try
            {
                receipts = await rpc.GetBlockReceiptsAsync(blockNumber, cancellationToken).ConfigureAwait(false);
            }
            catch (EvmJsonRpcException exception) when (exception.Kind == EvmRpcFailureKind.RpcError)
            {
                return false;
            }
            if (receipts.ValueKind != JsonValueKind.Array)
            {
                return false;
            }
            foreach (var receipt in receipts.EnumerateArray())
            {
                if (!receipt.TryGetProperty("logs", out var logs) || logs.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                foreach (var log in logs.EnumerateArray())
                {
                    if (!IsTransferLog(log))
                    {
                        continue;
                    }
                    await PublishLogAsync(
                            log,
                            rpc,
                            wallets,
                            tracked,
                            tokenMetadata,
                            onActivity,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            return true;
        }

        internal static bool IsTransferLog(JsonElement log)
        {
            if (log.ValueKind != JsonValueKind.Object
                || !log.TryGetProperty("topics", out var topics)
                || topics.ValueKind != JsonValueKind.Array
                || topics.GetArrayLength() == 0)
            {
                return false;
            }
            var topic = topics[0];
            if (topic.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            return topic.GetString() is { } value
                   && (value.Equals(WalletActivityRules.TransferTopic, StringComparison.OrdinalIgnoreCase)
                       || value.Equals(WalletActivityRules.TransferSingleTopic, StringComparison.OrdinalIgnoreCase)
                       || value.Equals(WalletActivityRules.TransferBatchTopic, StringComparison.OrdinalIgnoreCase));
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
                var result = await rpc.CallAsync(address, EthereumAbi.SymbolSelector, "latest", cancellationToken)
                    .ConfigureAwait(false);
                if (result.ValueKind == JsonValueKind.String
                    && EthereumAbi.TryDecodeString(result.GetString(), out var decoded))
                {
                    symbol = decoded;
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is not OnChainUsageBudgetException)
            {
            }
            try
            {
                var result = await rpc.CallAsync(address, EthereumAbi.DecimalsSelector, "latest", cancellationToken)
                    .ConfigureAwait(false);
                if (result.ValueKind == JsonValueKind.String
                    && EthereumAbi.TryDecodeByte(result.GetString(), out var decoded))
                {
                    decimals = decoded;
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is not OnChainUsageBudgetException)
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
                return (await rpc.GetBlockAsync(blockTag, cancellationToken).ConfigureAwait(false)).Number;
            }
            catch (EvmJsonRpcException)
            {
                return null;
            }
        }

        private static EvmHeadUpdate ToHead(EvmRpcBlockHeader block)
        {
            return new EvmHeadUpdate
            {
                ChainId = string.Empty,
                Number = block.Number,
                Hash = block.Hash,
                ParentHash = block.ParentHash,
                Timestamp = block.Timestamp
            };
        }

        private async Task SendSubscriptionAsync(
            ClientWebSocket socket,
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
            ClientWebSocket socket,
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

        private sealed record RecoveryResult(
            EvmRpcBlockHeader Latest,
            bool SupportsTraceFilter,
            ulong? SafeBlock,
            ulong? FinalizedBlock);

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

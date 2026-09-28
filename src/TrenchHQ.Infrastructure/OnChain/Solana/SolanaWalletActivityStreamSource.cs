using TrenchHQ.Core.OnChain;
using TrenchHQ.Core.Providers;
using TrenchHQ.Core.Wallets;
using TrenchHQ.Core.Widgets;
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

namespace TrenchHQ.Infrastructure.OnChain.Solana
{
    internal sealed class SolanaWalletActivityStreamSource
    {
        private const int MaximumMessageBytes = 2 * 1024 * 1024;
        private const int MaximumDirectTokenAccountSubscriptions = 200;
        private const int MaximumPendingSubscriptions = 16;
        private const string TokenProgram = "TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA";
        private const string Token2022Program = "TokenzQdBNbLqP5VEhdkAS6EPFLC1PHnBqCXEpPxuEb";
        internal static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(30);

        private readonly OnChainProviderConfiguration _configuration;
        private readonly string _apiKey;
        private readonly Uri _endpoint;
        private readonly string _sourceId;

        internal SolanaWalletActivityStreamSource(
            OnChainProviderConfiguration configuration,
            string apiKey)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            var preset = OnChainProviderCatalog.Get(configuration.ProviderType);
            if (preset.StreamTransport != OnChainStreamTransport.SolanaWebSocket)
            {
                throw new ArgumentException("The provider is not a standard Solana WebSocket provider.", nameof(configuration));
            }
            if (preset.RequiresCredential && string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ArgumentException("A Solana provider API key is required.", nameof(apiKey));
            }
            if (!OnChainProviderConfigurationStore.IsSecureStreamEndpoint(
                    configuration.StreamEndpoint,
                    preset.StreamTransport,
                    preset.StreamAuthenticationMode))
            {
                throw new ArgumentException("The Solana stream endpoint must be a credential-free WSS URL.", nameof(configuration));
            }

            _configuration = configuration;
            _apiKey = apiKey.Trim();
            _endpoint = OnChainProviderEndpointBuilder.Build(
                configuration.StreamEndpoint,
                _apiKey,
                preset.StreamAuthenticationMode);
            _sourceId = $"{configuration.ProviderType}:{configuration.Id}:wallet-websocket";
        }

        internal async Task RunAsync(
            SavedTrackedWallet[] wallets,
            Action<WalletActivityUpdate> onActivity,
            Action onLive,
            CancellationToken cancellationToken)
        {
            if (wallets.Length == 0)
            {
                return;
            }

            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            var rpc = new SolanaRpcClient(httpClient, _configuration, _apiKey);

            using var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            socket.Options.CollectHttpResponseDetails = true;
            try { await socket.ConnectAsync(_endpoint, cancellationToken).ConfigureAwait(false); }
            catch (WebSocketException) when ((int)socket.HttpStatusCode is 401 or 402 or 403 or 429)
            {
                if ((int)socket.HttpStatusCode == 402) _configuration.Usage?.QuotaExceeded();
                throw new IOException($"The provider rejected WebSocket access: HTTP {(int)socket.HttpStatusCode}.");
            }

            await RunConnectedAsync(socket, rpc, wallets, onActivity, onLive, cancellationToken)
                .ConfigureAwait(false);
        }

        internal async Task RunConnectedAsync(
            WebSocket socket, SolanaRpcClient rpc, SavedTrackedWallet[] wallets,
            Action<WalletActivityUpdate> onActivity, Action onLive, CancellationToken cancellationToken)
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancellationToken = lifetime.Token;
            var state = new SubscriptionState();
            var tracked = new Dictionary<string, WalletActivityUpdate>(StringComparer.Ordinal);
            var work = new Queue<Func<Task<Action>>>();
            var pendingTransactions = new HashSet<string>(StringComparer.Ordinal);
            var transactions = new Dictionary<string, SolanaWalletTransaction>(StringComparer.Ordinal);
            var pendingAccounts = new HashSet<string>(StringComparer.Ordinal);
            var buffered = new Queue<byte[]>();
            var accountQueues = new List<Queue<string>>();
            foreach (var wallet in wallets)
            {
                var accountQueue = new Queue<string>();
                accountQueues.Add(accountQueue);
                foreach (var program in new[] { TokenProgram, Token2022Program })
                {
                    var accounts = await rpc.GetTokenAccountAddressesByOwnerAsync(
                            wallet.Address,
                            program,
                            OnChainCommitment.Confirmed,
                            cancellationToken)
                        .ConfigureAwait(false);
                    foreach (var account in accounts)
                    {
                        accountQueue.Enqueue(account);
                    }

                }
            }

            foreach (var wallet in wallets)
            {
                await AddLogSubscriptionAsync(socket, state, wallet, wallet.Address, true, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var program in new[] { TokenProgram, Token2022Program })
                    await AddProgramSubscriptionAsync(socket, state, wallet, program, cancellationToken)
                        .ConfigureAwait(false);
                if (state.Pending.Count >= MaximumPendingSubscriptions)
                    await DrainSubscriptionResponsesAsync(socket, state, buffered, cancellationToken).ConfigureAwait(false);
            }
            while (accountQueues.Any(static queue => queue.Count > 0)
                   && state.OptionalLogCount < MaximumDirectTokenAccountSubscriptions)
            {
                for (var index = 0; index < wallets.Length; index++)
                {
                    if (accountQueues[index].Count == 0) continue;
                    await AddLogSubscriptionAsync(socket, state, wallets[index],
                            accountQueues[index].Dequeue(), false, cancellationToken)
                        .ConfigureAwait(false);
                    if (state.Pending.Count >= MaximumPendingSubscriptions)
                    {
                        await DrainSubscriptionResponsesAsync(socket, state, buffered, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
            await DrainSubscriptionResponsesAsync(socket, state, buffered, cancellationToken)
                .ConfigureAwait(false);

            onLive();

            // Only the event loop mutates subscription/tracking state. HTTP work returns an action
            // applied here, so a slow response or Retry-After never blocks WebSocket reads.
            void Enqueue(Func<Task<Action>> job)
            {
                if (work.Count >= 1024)
                    throw new IOException("The provider cannot keep up with this wallet activity. Configure another provider or watch fewer wallets.");
                work.Enqueue(job);
            }

            void PublishDetails(SolanaWalletTransaction transaction, SavedTrackedWallet wallet, long observedAt)
            {
                if (state.IsTransactionResolved(wallet.Address, transaction.Signature)) return;
                state.MarkTransactionResolved(wallet.Address, transaction.Signature);
                foreach (var update in WalletActivityRules.ParseSolanaTransaction(
                             transaction, wallet, "confirmed", observedAt, _sourceId + ":transaction"))
                    PublishAndTrack(update, tracked, onActivity);
            }

            void Observe(SavedTrackedWallet wallet, string signature, ulong slot, bool failed, long observedAt)
            {
                if (state.IsTransactionResolved(wallet.Address, signature)) return;
                PublishAndTrack(WalletActivityRules.CreateSolanaTransaction(
                    wallet, signature, slot, failed, "confirmed", observedAt, _sourceId), tracked, onActivity);
                if (failed)
                {
                    state.MarkTransactionResolved(wallet.Address, signature);
                    return;
                }
                if (transactions.TryGetValue(signature, out var cached))
                {
                    PublishDetails(cached, wallet, observedAt);
                    return;
                }
                if (!pendingTransactions.Add(signature)) return;
                Enqueue(async () =>
                {
                    SolanaWalletTransaction? transaction = null;
                    for (var attempt = 0; attempt < 3 && transaction == null; attempt++)
                    {
                        if (attempt > 0) await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), cancellationToken).ConfigureAwait(false);
                        transaction = await OnChainRequestRetry.RunAsync(
                            () => rpc.GetWalletTransactionAsync(signature, cancellationToken), cancellationToken).ConfigureAwait(false);
                    }
                    return () =>
                    {
                        pendingTransactions.Remove(signature);
                        if (transaction == null) return; // Keep the genuine notification even if details are unavailable.
                        transactions[signature] = transaction;
                        if (transactions.Count > 512) transactions.Remove(transactions.Keys.First());
                        var matches = tracked.Values.Where(item => item.TransactionId == signature)
                            .GroupBy(item => item.WalletAddress).Select(group => group.First()).ToArray();
                        foreach (var match in matches)
                            PublishDetails(transaction, wallets.First(item => item.Address == match.WalletAddress), match.ObservedAtUnixMs);
                    };
                });
            }

            async Task ProcessMessageAsync(byte[] message)
            {
                using var document = JsonDocument.Parse(message);
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var id))
                {
                    ProcessSubscriptionResponse(root, id, state);
                    return;
                }
                if (!root.TryGetProperty("params", out var parameters)
                    || !parameters.TryGetProperty("subscription", out var subscription)
                    || !subscription.TryGetUInt64(out var subscriptionId)
                    || !state.Active.TryGetValue(subscriptionId, out var target)
                    || !parameters.TryGetProperty("result", out var result)
                    || !result.TryGetProperty("context", out var context)
                    || !context.TryGetProperty("slot", out var slot) || !slot.TryGetUInt64(out var slotValue)
                    || !result.TryGetProperty("value", out var value)) return;
                var observedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (target.Kind == SolanaSubscriptionKind.Logs)
                {
                    if (!value.TryGetProperty("signature", out var signature) || signature.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(signature.GetString())) return;
                    Observe(target.Wallet, signature.GetString()!, slotValue,
                        value.TryGetProperty("err", out var error) && error.ValueKind != JsonValueKind.Null, observedAt);
                    return;
                }
                if (!value.TryGetProperty("pubkey", out var pubkey) || pubkey.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(pubkey.GetString())) return;
                var address = pubkey.GetString()!;
                var key = TargetKey(target.Wallet, address);
                // Active direct logs already supply signatures without an extra HTTP lookup.
                if (state.Active.Values.Any(item => item.Kind == SolanaSubscriptionKind.Logs
                    && TargetKey(item.Wallet, item.Address) == key)) return;
                if (state.Pending.Count < MaximumPendingSubscriptions)
                    await AddLogSubscriptionAsync(socket, state, target.Wallet, address, false, cancellationToken).ConfigureAwait(false);
                var accountSlot = key + "|" + slotValue;
                if (!pendingAccounts.Add(accountSlot)) return;
                Enqueue(async () =>
                {
                    var signatures = await FindLiveSignaturesAsync(rpc, address, slotValue, cancellationToken).ConfigureAwait(false);
                    return () =>
                    {
                        pendingAccounts.Remove(accountSlot);
                        foreach (var item in signatures)
                            Observe(target.Wallet, item.Signature, item.Slot, item.Failed, observedAt);
                    };
                });
            }

            var receiveTask = ReceiveMessageAsync(socket, cancellationToken);
            var maintenanceTask = Task.Delay(MaintenanceInterval, cancellationToken);
            Task<Action>? detailTask = null;
            var idleTask = Task.Delay(Timeout.Infinite, cancellationToken);
            var maintenancePending = false;
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
                    if (receiveTask.IsCompleted || buffered.Count > 0)
                    {
                        // Drain setup notifications in arrival order.
                        var fromBuffer = buffered.Count > 0;
                        var message = fromBuffer ? buffered.Dequeue() : await receiveTask.ConfigureAwait(false);
                        if (!fromBuffer)
                            receiveTask = ReceiveMessageAsync(socket, cancellationToken);
                        if (message == null) throw new IOException("The Solana wallet WebSocket closed unexpectedly.");
                        await ProcessMessageAsync(message).ConfigureAwait(false);
                    }
                    if (maintenanceTask.IsCompleted)
                    {
                        if (!maintenancePending)
                        {
                            var signatures = tracked.Values.Where(item => item.Confirmation != "finalized")
                                .Select(item => item.TransactionId).Distinct(StringComparer.Ordinal).Take(256).ToArray();
                            if (signatures.Length > 0)
                            {
                                maintenancePending = true;
                                Enqueue(async () =>
                                {
                                    var statuses = await OnChainRequestRetry.RunAsync(
                                        () => rpc.GetSignatureStatusesAsync(signatures, cancellationToken), cancellationToken).ConfigureAwait(false);
                                    return () =>
                                    {
                                        maintenancePending = false;
                                        for (var index = 0; index < signatures.Length; index++)
                                        {
                                            var status = statuses[index];
                                            if (status == null) continue;
                                            foreach (var item in tracked.Values.Where(item => item.TransactionId == signatures[index]).ToArray())
                                                if (WalletActivityRules.IsConfirmationUpgrade(item.Confirmation, status.Confirmation))
                                                    PublishAndTrack(WalletActivityRules.WithConfirmation(item, status.Confirmation, status.Failed), tracked, onActivity);
                                        }
                                    };
                                });
                            }
                        }
                        maintenanceTask = Task.Delay(MaintenanceInterval, cancellationToken);
                    }
                }
                throw new IOException("The Solana wallet WebSocket ended unexpectedly.");
            }
            finally
            {
                lifetime.Cancel();
                try { if (detailTask != null) await detailTask.ConfigureAwait(false); } catch { }
                try { await receiveTask.ConfigureAwait(false); } catch { }
            }
        }

        // programSubscribe supplies a slot, not a signature. Resolve only that live slot;
        // never replay earlier history, including on a newly discovered token account.
        internal static async Task<SolanaWalletSignature[]> FindLiveSignaturesAsync(
            SolanaRpcClient rpc, string address, ulong slot, CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0) await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), cancellationToken).ConfigureAwait(false);
                var recent = await OnChainRequestRetry.RunAsync(
                    () => rpc.GetSignaturesForAddressAsync(address, null, 100, cancellationToken), cancellationToken).ConfigureAwait(false);
                var matches = recent.Where(item => item.Slot == slot).ToArray();
                if (matches.Length > 0) return matches;
            }
            return [];
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
            if (update.Confirmation == "finalized")
            {
                tracked.Remove(update.EventId);
            }
        }

        private async Task<bool> AddLogSubscriptionAsync(
            WebSocket socket,
            SubscriptionState state,
            SavedTrackedWallet wallet,
            string address,
            bool required,
            CancellationToken cancellationToken)
        {
            var key = TargetKey(wallet, address);
            if (state.LogTargets.ContainsKey(key))
            {
                return false;
            }
            if (!required && state.OptionalLogCount >= MaximumDirectTokenAccountSubscriptions)
            {
                return false;
            }
            var target = new SolanaSubscriptionTarget(
                wallet,
                address,
                SolanaSubscriptionKind.Logs,
                required);
            state.LogTargets[key] = target;
            if (!required)
            {
                state.OptionalLogCount++;
            }
            await SendSubscriptionAsync(
                    socket,
                    state,
                    target,
                    "logsSubscribe",
                    new object[]
                    {
                        new { mentions = new[] { address } },
                        new { commitment = "confirmed" }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        private async Task AddProgramSubscriptionAsync(
            WebSocket socket,
            SubscriptionState state,
            SavedTrackedWallet wallet,
            string program,
            CancellationToken cancellationToken)
        {
            var key = TargetKey(wallet, program);
            if (!state.ProgramTargets.Add(key))
            {
                return;
            }
            var target = new SolanaSubscriptionTarget(
                wallet,
                program,
                SolanaSubscriptionKind.ProgramAccounts,
                false);
            await SendSubscriptionAsync(
                    socket,
                    state,
                    target,
                    "programSubscribe",
                    new object[]
                    {
                        program,
                        new
                        {
                            commitment = "confirmed",
                            encoding = "base64",
                            dataSlice = new { offset = 0, length = 0 },
                            filters = new object[]
                            {
                                new { memcmp = new { offset = 32, bytes = wallet.Address } }
                            }
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task SendSubscriptionAsync(
            WebSocket socket,
            SubscriptionState state,
            SolanaSubscriptionTarget target,
            string method,
            object[] parameters,
            CancellationToken cancellationToken)
        {
            var requestId = ++state.RequestId;
            state.Pending.Add(requestId, target);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0",
                id = requestId,
                method,
                @params = parameters
            });
            _configuration.Usage?.Rpc(method);
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken)
                .ConfigureAwait(false);
        }

        private static void ProcessSubscriptionResponse(
            JsonElement root,
            JsonElement id,
            SubscriptionState state)
        {
            var requestId = id.GetInt64();
            if (!state.Pending.Remove(requestId, out var target))
            {
                return;
            }
            if (root.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var value)
                    ? value.GetString()
                    : "Unknown subscription error";
                throw new InvalidOperationException("Solana wallet subscription failed: " + message);
            }
            state.Active[root.GetProperty("result").GetUInt64()] = target;
        }

        private async Task DrainSubscriptionResponsesAsync(
            WebSocket socket,
            SubscriptionState state,
            Queue<byte[]> buffered,
            CancellationToken cancellationToken)
        {
            while (state.Pending.Count > 0)
            {
                var message = await ReceiveMessageAsync(socket, cancellationToken).ConfigureAwait(false)
                              ?? throw new IOException("The Solana wallet WebSocket closed during setup.");
                using var document = JsonDocument.Parse(message);
                if (document.RootElement.TryGetProperty("id", out var id))
                    ProcessSubscriptionResponse(document.RootElement, id, state);
                else
                {
                    if (buffered.Count >= 256 || buffered.Sum(item => item.Length) + message.Length > 8 * 1024 * 1024)
                        throw new IOException("The Solana wallet setup exceeded its notification buffer.");
                    buffered.Enqueue(message);
                }
            }
        }

        private static string TargetKey(SavedTrackedWallet wallet, string address)
        {
            return wallet.Address + "|" + address;
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
                    throw new InvalidDataException("The Solana provider returned a non-text wallet message.");
                }
                if (output.Length + result.Count > MaximumMessageBytes)
                {
                    throw new InvalidDataException("The Solana wallet message exceeded the local safety limit.");
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

        private enum SolanaSubscriptionKind
        {
            Logs,
            ProgramAccounts
        }

        private sealed record SolanaSubscriptionTarget(
            SavedTrackedWallet Wallet,
            string Address,
            SolanaSubscriptionKind Kind,
            bool Required);

        private sealed class SubscriptionState
        {
            private const int MaximumResolvedTransactions = 4096;

            internal long RequestId { get; set; }
            internal int OptionalLogCount { get; set; }
            internal Dictionary<long, SolanaSubscriptionTarget> Pending { get; } = [];
            internal Dictionary<ulong, SolanaSubscriptionTarget> Active { get; } = [];
            internal Dictionary<string, SolanaSubscriptionTarget> LogTargets { get; } = new(StringComparer.Ordinal);
            internal HashSet<string> ProgramTargets { get; } = new(StringComparer.Ordinal);
            private HashSet<string> ResolvedTransactions { get; } = new(StringComparer.Ordinal);
            private Queue<string> ResolvedTransactionOrder { get; } = new();

            internal bool IsTransactionResolved(string walletAddress, string signature) =>
                ResolvedTransactions.Contains(walletAddress + "|" + signature);

            internal void MarkTransactionResolved(string walletAddress, string signature)
            {
                var key = walletAddress + "|" + signature;
                if (!ResolvedTransactions.Add(key))
                {
                    return;
                }
                ResolvedTransactionOrder.Enqueue(key);
                while (ResolvedTransactionOrder.Count > MaximumResolvedTransactions)
                {
                    ResolvedTransactions.Remove(ResolvedTransactionOrder.Dequeue());
                }
            }
        }
    }
}

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
    internal sealed class SolanaWalletActivityStreamSource
    {
        private const int MaximumMessageBytes = 2 * 1024 * 1024;
        private const int InitialHistoryCount = 10;
        private const int MaximumRecoveryCount = 100;
        private const int MaximumDirectTokenAccountSubscriptions = 200;
        private const string TokenProgram = "TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA";
        private const string Token2022Program = "TokenzQdBNbLqP5VEhdkAS6EPFLC1PHnBqCXEpPxuEb";
        internal static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(5);
        internal static readonly TimeSpan TokenAccountRefreshInterval = TimeSpan.FromMinutes(5);

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
            if (string.IsNullOrWhiteSpace(apiKey))
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
            IReadOnlyDictionary<string, string> lastSignatures,
            Action<WalletActivityUpdate> onActivity,
            Action<string, string> onSignatureObserved,
            Action onLive,
            CancellationToken cancellationToken)
        {
            if (wallets.Length == 0)
            {
                return;
            }

            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            var rpc = new SolanaRpcClient(httpClient, _configuration, _apiKey);
            var state = new SubscriptionState();
            var cursors = new Dictionary<string, string>(lastSignatures, StringComparer.Ordinal);
            var tracked = new Dictionary<string, WalletActivityUpdate>(StringComparer.Ordinal);

            using var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            socket.Options.CollectHttpResponseDetails = true;
            try { await socket.ConnectAsync(_endpoint, cancellationToken).ConfigureAwait(false); }
            catch (WebSocketException) when ((int)socket.HttpStatusCode is 401 or 402 or 403 or 429)
            {
                if ((int)socket.HttpStatusCode == 402) _configuration.Usage?.QuotaExceeded();
                throw new IOException($"The provider rejected WebSocket access: HTTP {(int)socket.HttpStatusCode}.");
            }

            foreach (var wallet in wallets)
            {
                await AddLogSubscriptionAsync(socket, state, wallet, wallet.Address, true, cancellationToken)
                    .ConfigureAwait(false);
            }
            foreach (var wallet in wallets)
            {
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
                        await AddLogSubscriptionAsync(socket, state, wallet, account, false, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    await AddProgramSubscriptionAsync(socket, state, wallet, program, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            var buffered = new Queue<byte[]>();
            while (state.Pending.Count > 0)
            {
                var message = await ReceiveMessageAsync(socket, cancellationToken).ConfigureAwait(false)
                              ?? throw new IOException("The Solana wallet WebSocket closed during setup.");
                using var document = JsonDocument.Parse(message);
                if (document.RootElement.TryGetProperty("id", out var id))
                {
                    ProcessSubscriptionResponse(document.RootElement, id, state);
                }
                else
                {
                    buffered.Enqueue(message);
                }
            }

            foreach (var target in state.LogTargets.Values)
            {
                await RecoverTargetAsync(
                        rpc,
                        state,
                        target,
                        cursors,
                        tracked,
                        onActivity,
                        onSignatureObserved,
                        cancellationToken)
                    .ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(125), cancellationToken).ConfigureAwait(false);
            }
            onLive();

            while (buffered.Count > 0)
            {
                await ProcessMessageAsync(
                        buffered.Dequeue(),
                        socket,
                        rpc,
                        state,
                        cursors,
                        tracked,
                        onActivity,
                        onSignatureObserved,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var lastTokenAccountRefresh = DateTimeOffset.UtcNow;
            var receiveTask = ReceiveMessageAsync(socket, cancellationToken);
            var maintenanceTask = Task.Delay(MaintenanceInterval, cancellationToken);
            while (socket.State == WebSocketState.Open)
            {
                var completed = await Task.WhenAny(receiveTask, maintenanceTask).ConfigureAwait(false);
                if (completed == maintenanceTask)
                {
                    await RecoverPendingTargetsAsync(
                            rpc,
                            state,
                            cursors,
                            tracked,
                            onActivity,
                            onSignatureObserved,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await PromoteActivitiesAsync(rpc, state, tracked, onActivity, cancellationToken)
                        .ConfigureAwait(false);
                    if (DateTimeOffset.UtcNow - lastTokenAccountRefresh >= TokenAccountRefreshInterval)
                    {
                        await RefreshTokenAccountsAsync(
                                socket,
                                rpc,
                                wallets,
                                state,
                                cursors,
                                tracked,
                                onActivity,
                                onSignatureObserved,
                                cancellationToken)
                            .ConfigureAwait(false);
                        lastTokenAccountRefresh = DateTimeOffset.UtcNow;
                    }
                    maintenanceTask = Task.Delay(MaintenanceInterval, cancellationToken);
                    continue;
                }

                var message = await receiveTask.ConfigureAwait(false);
                if (message == null)
                {
                    throw new IOException("The Solana wallet WebSocket closed unexpectedly.");
                }
                receiveTask = ReceiveMessageAsync(socket, cancellationToken);
                await ProcessMessageAsync(
                        message,
                        socket,
                        rpc,
                        state,
                        cursors,
                        tracked,
                        onActivity,
                        onSignatureObserved,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            throw new IOException("The Solana wallet WebSocket ended unexpectedly.");
        }

        private async Task ProcessMessageAsync(
            byte[] message,
            ClientWebSocket socket,
            SolanaRpcClient rpc,
            SubscriptionState state,
            IDictionary<string, string> cursors,
            IDictionary<string, WalletActivityUpdate> tracked,
            Action<WalletActivityUpdate> onActivity,
            Action<string, string> onSignatureObserved,
            CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (root.TryGetProperty("id", out var responseId))
            {
                ProcessSubscriptionResponse(root, responseId, state);
                return;
            }
            if (!root.TryGetProperty("params", out var parameters)
                || !parameters.TryGetProperty("subscription", out var subscription)
                || !subscription.TryGetUInt64(out var subscriptionId)
                || !state.Active.TryGetValue(subscriptionId, out var target)
                || !parameters.TryGetProperty("result", out var result))
            {
                return;
            }

            if (target.Kind == SolanaSubscriptionKind.ProgramAccounts)
            {
                if (!result.TryGetProperty("value", out var value)
                    || !value.TryGetProperty("pubkey", out var pubkey)
                    || pubkey.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(pubkey.GetString()))
                {
                    return;
                }
                var accountAddress = pubkey.GetString()!;
                var key = TargetKey(target.Wallet, accountAddress);
                var added = await AddLogSubscriptionAsync(
                        socket,
                        state,
                        target.Wallet,
                        accountAddress,
                        false,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!added && state.LogTargets.ContainsKey(key))
                {
                    return;
                }
                var accountTarget = added
                    ? state.LogTargets[key]
                    : new SolanaSubscriptionTarget(
                        target.Wallet,
                        accountAddress,
                        SolanaSubscriptionKind.Logs,
                        false);
                if (!await RecoverTargetAsync(
                        rpc,
                        state,
                        accountTarget,
                        cursors,
                        tracked,
                        onActivity,
                        onSignatureObserved,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    state.PendingRecovery[key] = accountTarget;
                }
                return;
            }

            if (!result.TryGetProperty("context", out var context)
                || !context.TryGetProperty("slot", out var slot)
                || !slot.TryGetUInt64(out var slotValue)
                || !result.TryGetProperty("value", out var logValue)
                || !logValue.TryGetProperty("signature", out var signature)
                || signature.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(signature.GetString()))
            {
                return;
            }
            var signatureValue = signature.GetString()!;
            var failed = logValue.TryGetProperty("err", out var error)
                         && error.ValueKind != JsonValueKind.Null;
            PublishAndTrack(
                WalletActivityRules.CreateSolanaTransaction(
                    target.Wallet,
                    signatureValue,
                    slotValue,
                    failed,
                    "processed",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    _sourceId),
                tracked,
                onActivity);
            if (failed)
            {
                state.MarkTransactionResolved(target.Wallet.Address, signatureValue);
            }
            else
            {
                await TryPublishTransactionAsync(
                        rpc,
                        state,
                        target.Wallet,
                        signatureValue,
                        "confirmed",
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        tracked,
                        onActivity,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private async Task RefreshTokenAccountsAsync(
            ClientWebSocket socket,
            SolanaRpcClient rpc,
            IEnumerable<SavedTrackedWallet> wallets,
            SubscriptionState state,
            IDictionary<string, string> cursors,
            IDictionary<string, WalletActivityUpdate> tracked,
            Action<WalletActivityUpdate> onActivity,
            Action<string, string> onSignatureObserved,
            CancellationToken cancellationToken)
        {
            foreach (var wallet in wallets)
            {
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
                        var key = TargetKey(wallet, account);
                        var added = await AddLogSubscriptionAsync(
                                socket,
                                state,
                                wallet,
                                account,
                                false,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (!added && !state.PollTargets.Contains(key))
                        {
                            continue;
                        }
                        if (!await RecoverTargetAsync(
                                rpc,
                                state,
                                state.LogTargets[key],
                                cursors,
                                tracked,
                                onActivity,
                                onSignatureObserved,
                                cancellationToken)
                            .ConfigureAwait(false) && added)
                        {
                            state.PendingRecovery[key] = state.LogTargets[key];
                        }
                    }
                }
            }
        }

        private async Task<bool> RecoverTargetAsync(
            SolanaRpcClient rpc,
            SubscriptionState state,
            SolanaSubscriptionTarget target,
            IDictionary<string, string> cursors,
            IDictionary<string, WalletActivityUpdate> tracked,
            Action<WalletActivityUpdate> onActivity,
            Action<string, string> onSignatureObserved,
            CancellationToken cancellationToken)
        {
            cursors.TryGetValue(target.Address, out var until);
            if (until != null)
            {
                var cursorStatus = (await rpc.GetSignatureStatusesAsync([until], cancellationToken)
                        .ConfigureAwait(false))[0];
                if (cursorStatus != null)
                {
                    PublishAndTrack(
                        WalletActivityRules.CreateSolanaTransaction(
                            target.Wallet,
                            until,
                            cursorStatus.Slot,
                            cursorStatus.Failed,
                            cursorStatus.Confirmation,
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            _sourceId + ":status"),
                        tracked,
                        onActivity);
                    if (cursorStatus.Failed)
                    {
                        state.MarkTransactionResolved(target.Wallet.Address, until);
                    }
                    else
                    {
                        await TryPublishTransactionAsync(
                                rpc,
                                state,
                                target.Wallet,
                                until,
                                cursorStatus.Confirmation,
                                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                tracked,
                                onActivity,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
            var history = await rpc.GetSignaturesForAddressAsync(
                    target.Address,
                    until,
                    until == null ? InitialHistoryCount : MaximumRecoveryCount,
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var signature in history.Reverse())
            {
                PublishAndTrack(
                    WalletActivityRules.CreateSolanaTransaction(
                        target.Wallet,
                        signature.Signature,
                        signature.Slot,
                        signature.Failed,
                        signature.Confirmation,
                        signature.BlockTimeUnixSeconds.HasValue
                            ? DateTimeOffset.FromUnixTimeSeconds(signature.BlockTimeUnixSeconds.Value)
                                .ToUnixTimeMilliseconds()
                            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        _sourceId + ":rpc"),
                    tracked,
                    onActivity);
                if (signature.Failed)
                {
                    state.MarkTransactionResolved(target.Wallet.Address, signature.Signature);
                }
                else
                {
                    await TryPublishTransactionAsync(
                            rpc,
                            state,
                            target.Wallet,
                            signature.Signature,
                            signature.Confirmation,
                            signature.BlockTimeUnixSeconds.HasValue
                                ? DateTimeOffset.FromUnixTimeSeconds(signature.BlockTimeUnixSeconds.Value)
                                    .ToUnixTimeMilliseconds()
                                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            tracked,
                            onActivity,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                cursors[target.Address] = signature.Signature;
                onSignatureObserved(target.Address, signature.Signature);
            }
            return history.Count > 0;
        }

        private async Task RecoverPendingTargetsAsync(
            SolanaRpcClient rpc,
            SubscriptionState state,
            IDictionary<string, string> cursors,
            IDictionary<string, WalletActivityUpdate> tracked,
            Action<WalletActivityUpdate> onActivity,
            Action<string, string> onSignatureObserved,
            CancellationToken cancellationToken)
        {
            foreach (var item in state.PendingRecovery.ToArray())
            {
                if (await RecoverTargetAsync(
                            rpc,
                            state,
                            item.Value,
                            cursors,
                            tracked,
                            onActivity,
                            onSignatureObserved,
                            cancellationToken)
                        .ConfigureAwait(false))
                {
                    state.PendingRecovery.Remove(item.Key);
                }
            }
        }

        private async Task PromoteActivitiesAsync(
            SolanaRpcClient rpc,
            SubscriptionState state,
            IDictionary<string, WalletActivityUpdate> tracked,
            Action<WalletActivityUpdate> onActivity,
            CancellationToken cancellationToken)
        {
            var signatures = tracked.Values
                .Where(static activity => activity.Confirmation != "finalized")
                .OrderByDescending(static activity => activity.ObservedAtUnixMs)
                .Select(static activity => activity.TransactionId)
                .Distinct(StringComparer.Ordinal)
                .Take(256)
                .ToArray();
            if (signatures.Length == 0)
            {
                return;
            }
            var statuses = await rpc.GetSignatureStatusesAsync(signatures, cancellationToken)
                .ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            for (var index = 0; index < signatures.Length; index++)
            {
                var matching = tracked.Values
                    .Where(activity => activity.TransactionId == signatures[index])
                    .ToArray();
                var status = statuses[index];
                foreach (var activity in matching)
                {
                    if (status != null
                        && WalletActivityRules.IsConfirmationUpgrade(activity.Confirmation, status.Confirmation))
                    {
                        var promoted = WalletActivityRules.WithConfirmation(
                            activity,
                            status.Confirmation,
                            status.Failed);
                        tracked[activity.EventId] = promoted;
                        onActivity(promoted);
                        if (status.Confirmation == "finalized")
                        {
                            tracked.Remove(activity.EventId);
                        }
                    }
                    else if (status == null
                             && activity.Confirmation == "processed"
                             && now - activity.ObservedAtUnixMs >= TimeSpan.FromMinutes(2).TotalMilliseconds)
                    {
                        var removed = WalletActivityRules.WithConfirmation(activity, activity.Confirmation);
                        removed.Removed = true;
                        tracked.Remove(activity.EventId);
                        onActivity(removed);
                    }
                }
                if (status != null && !status.Failed)
                {
                    foreach (var walletGroup in matching.GroupBy(
                                 static activity => activity.WalletAddress,
                                 StringComparer.Ordinal))
                    {
                        var sample = walletGroup.First();
                        await TryPublishTransactionAsync(
                                rpc,
                                state,
                                new SavedTrackedWallet
                                {
                                    ChainNamespace = ChainNamespaces.Solana,
                                    ChainId = "mainnet-beta",
                                    Address = sample.WalletAddress,
                                    Label = sample.WalletLabel
                                },
                                signatures[index],
                                status.Confirmation,
                                sample.ObservedAtUnixMs,
                                tracked,
                                onActivity,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
        }

        private async Task TryPublishTransactionAsync(
            SolanaRpcClient rpc,
            SubscriptionState state,
            SavedTrackedWallet wallet,
            string signature,
            string confirmation,
            long observedAtUnixMs,
            IDictionary<string, WalletActivityUpdate> tracked,
            Action<WalletActivityUpdate> onActivity,
            CancellationToken cancellationToken)
        {
            if (state.IsTransactionResolved(wallet.Address, signature))
            {
                return;
            }
            var transaction = await rpc.GetWalletTransactionAsync(signature, cancellationToken)
                .ConfigureAwait(false);
            if (transaction == null)
            {
                return;
            }
            state.MarkTransactionResolved(wallet.Address, signature);
            foreach (var update in WalletActivityRules.ParseSolanaTransaction(
                         transaction,
                         wallet,
                         confirmation,
                         observedAtUnixMs,
                         _sourceId + ":transaction"))
            {
                PublishAndTrack(update, tracked, onActivity);
            }
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
            if (update.Confirmation == "finalized")
            {
                tracked.Remove(update.EventId);
            }
        }

        private async Task<bool> AddLogSubscriptionAsync(
            ClientWebSocket socket,
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
                        new { commitment = "processed" }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        private async Task AddProgramSubscriptionAsync(
            ClientWebSocket socket,
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
                            commitment = "processed",
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
            ClientWebSocket socket,
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
                if (!target.Required)
                {
                    if (target.Kind == SolanaSubscriptionKind.Logs)
                    {
                        state.PollTargets.Add(TargetKey(target.Wallet, target.Address));
                    }
                    return;
                }
                var message = error.TryGetProperty("message", out var value)
                    ? value.GetString()
                    : "Unknown subscription error";
                throw new InvalidOperationException("Solana wallet subscription failed: " + message);
            }
            state.Active[root.GetProperty("result").GetUInt64()] = target;
        }

        private static string TargetKey(SavedTrackedWallet wallet, string address)
        {
            return wallet.Address + "|" + address;
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
            internal Dictionary<string, SolanaSubscriptionTarget> PendingRecovery { get; } = new(StringComparer.Ordinal);
            internal HashSet<string> PollTargets { get; } = new(StringComparer.Ordinal);
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

using TrenchHQ.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TrenchHQ.Helpers
{
    internal sealed class SolanaWebSocketStreamSource : IOnChainStreamSource
    {
        private const int MaximumMessageBytes = 8 * 1024 * 1024;
        private const int TransactionQueueCapacity = 256;
        private const int SignatureDeduplicationCapacity = 4096;

        private readonly OnChainProviderConfiguration _configuration;
        private readonly string _apiKey;
        private readonly OnChainProviderPreset _preset;
        private readonly Uri _endpoint;
        private readonly string _sourceId;

        internal SolanaWebSocketStreamSource(OnChainProviderConfiguration configuration, string apiKey)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            _preset = OnChainProviderCatalog.Get(configuration.ProviderType);
            if (_preset.StreamTransport != OnChainStreamTransport.SolanaWebSocket)
            {
                throw new ArgumentException("The provider is not a standard Solana WebSocket provider.", nameof(configuration));
            }
            if (!OnChainProviderConfigurationStore.IsSecureStreamEndpoint(
                    configuration.StreamEndpoint,
                    OnChainStreamTransport.SolanaWebSocket))
            {
                throw new ArgumentException("The Solana stream endpoint must be a credential-free WSS URL.", nameof(configuration));
            }
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ArgumentException("A provider API key is required.", nameof(apiKey));
            }

            _configuration = configuration;
            _apiKey = apiKey.Trim();
            _endpoint = OnChainProviderEndpointBuilder.Build(
                configuration.StreamEndpoint,
                _apiKey,
                _preset.StreamAuthenticationMode);
            _sourceId = $"{configuration.ProviderType}:{configuration.Id}:websocket";
        }

        public async Task RunAsync(
            OnChainStreamSubscription subscription,
            ChannelWriter<OnChainSourceUpdate> output,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(subscription);
            ArgumentNullException.ThrowIfNull(output);
            if (subscription.Pools.Length == 0)
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
                throw new SolanaRpcException("The provider rejected WebSocket access.", httpStatusCode: (int)socket.HttpStatusCode);
            }

            var pendingSubscriptions = new Dictionary<long, SubscriptionTarget>();
            var activeSubscriptions = new Dictionary<ulong, SubscriptionTarget>();
            var nextRequestId = 0L;
            var accountWriteVersions = new Dictionary<string, ulong>(StringComparer.Ordinal);
            var signatures = new HashSet<string>(StringComparer.Ordinal);
            var signatureOrder = new Queue<string>();
            var pendingTransactions = Channel.CreateBounded<PendingTransaction>(
                new BoundedChannelOptions(TransactionQueueCapacity)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = BoundedChannelFullMode.DropOldest
                });

            var accountAddresses = subscription.Pools
                .SelectMany(static pool => pool.Descriptor.EnumerateSolanaAccountAddresses())
                .Where(static address => !string.IsNullOrWhiteSpace(address))
                .Select(static address => address!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var poolAddresses = subscription.Pools
                .Select(static pool => pool.Descriptor.PoolKey.PoolAddress)
                .Where(static address => !string.IsNullOrWhiteSpace(address))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            foreach (var target in CreateRuntimeSubscriptionTargets(accountAddresses, poolAddresses))
            {
                var method = target.Kind == SubscriptionKind.Account
                    ? "accountSubscribe"
                    : "logsSubscribe";
                object[] parameters = target.Kind == SubscriptionKind.Account
                    ?
                    [
                        target.Address!,
                        new
                        {
                            encoding = "base64",
                            commitment = ToRpcCommitment(subscription.Commitment)
                        }
                    ]
                    :
                    [
                        new { mentions = new[] { target.Address! } },
                        new { commitment = ToRpcCommitment(subscription.Commitment) }
                    ];
                await SendSubscriptionAsync(
                    socket,
                    ++nextRequestId,
                    method,
                    parameters,
                    target,
                    pendingSubscriptions,
                    cancellationToken).ConfigureAwait(false);
            }

            using var workerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            async Task RunTransactionWorkerAsync()
            {
                try { await FetchTransactionsAsync(pendingTransactions.Reader, output, workerCts.Token).ConfigureAwait(false); }
                catch { workerCts.Cancel(); throw; }
            }
            var transactionWorker = RunTransactionWorkerAsync();
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var message = await ReceiveMessageAsync(socket, workerCts.Token).ConfigureAwait(false);
                    if (message == null)
                    {
                        throw new IOException("The Solana WebSocket closed unexpectedly.");
                    }
                    using var document = JsonDocument.Parse(message);
                    var root = document.RootElement;
                    if (root.TryGetProperty("id", out var idElement))
                    {
                        ProcessSubscriptionResponse(root, idElement, pendingSubscriptions, activeSubscriptions);
                        continue;
                    }
                    if (!root.TryGetProperty("method", out var methodElement)
                        || !root.TryGetProperty("params", out var parameters))
                    {
                        continue;
                    }

                    var method = methodElement.GetString();
                    var subscriptionId = parameters.GetProperty("subscription").GetUInt64();
                    if (!activeSubscriptions.TryGetValue(subscriptionId, out var target))
                    {
                        continue;
                    }
                    var observedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    switch (method, target.Kind)
                    {
                        case ("accountNotification", SubscriptionKind.Account):
                            await output.WriteAsync(
                                ParseAccountUpdate(
                                    parameters.GetProperty("result"),
                                    target.Address!,
                                    accountWriteVersions,
                                    observedAt),
                                cancellationToken).ConfigureAwait(false);
                            break;
                        case ("logsNotification", SubscriptionKind.Logs):
                            QueueTransaction(
                                parameters.GetProperty("result"),
                                observedAt,
                                signatures,
                                signatureOrder,
                                pendingTransactions.Writer);
                            break;
                    }
                }
                throw new IOException("The Solana WebSocket ended unexpectedly.");
            }
            finally
            {
                pendingTransactions.Writer.TryComplete();
                workerCts.Cancel();
                try
                {
                    await transactionWorker.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        private async Task FetchTransactionsAsync(
            ChannelReader<PendingTransaction> input,
            ChannelWriter<OnChainSourceUpdate> output,
            CancellationToken cancellationToken)
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var rpc = new SolanaRpcClient(httpClient, _configuration, _apiKey);
            await foreach (var pending in input.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var transaction = await FetchTransactionAsync(rpc, pending.Signature,
                    pending.ObservedAtUnixMs, cancellationToken).ConfigureAwait(false);
                if (transaction != null && !transaction.Failed)
                {
                    await output.WriteAsync(
                        new OnChainSourceTransactionUpdate(transaction),
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }

        internal async Task<OnChainRawTransactionUpdate?> FetchTransactionAsync(
            SolanaRpcClient rpc, string signature, long observedAtUnixMs, CancellationToken cancellationToken)
        {
            OnChainRawTransactionUpdate? transaction = null;
            foreach (var delay in new[] { 125, 350, 800 })
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                try
                {
                    transaction = await rpc.GetTransactionAsync(
                        signature,
                        _configuration.Commitment,
                        _sourceId + ":rpc",
                        observedAtUnixMs,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (SolanaRpcException exception) when (!exception.IsAccessRejected && !exception.IsRateLimited)
                {
                    transaction = null;
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested
                    && exception is not OnChainUsageBudgetException
                    && exception is not SolanaRpcException { IsAccessRejected: true }
                    && exception is not SolanaRpcException { IsRateLimited: true })
                {
                    transaction = null;
                    break;
                }
                if (transaction != null)
                {
                    break;
                }
            }
            return transaction;
        }

        private async Task SendSubscriptionAsync(
            ClientWebSocket socket,
            long requestId,
            string method,
            object[] parameters,
            SubscriptionTarget target,
            IDictionary<long, SubscriptionTarget> pendingSubscriptions,
            CancellationToken cancellationToken)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0",
                id = requestId,
                method,
                @params = parameters
            });
            pendingSubscriptions.Add(requestId, target);
            _configuration.Usage?.Rpc(method);
            await socket.SendAsync(
                payload,
                WebSocketMessageType.Text,
                true,
                cancellationToken).ConfigureAwait(false);
        }

        private static void ProcessSubscriptionResponse(
            JsonElement root,
            JsonElement idElement,
            IDictionary<long, SubscriptionTarget> pendingSubscriptions,
            IDictionary<ulong, SubscriptionTarget> activeSubscriptions)
        {
            var requestId = idElement.GetInt64();
            if (!pendingSubscriptions.Remove(requestId, out var target))
            {
                return;
            }
            if (root.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString()
                    : "Unknown subscription error";
                throw new InvalidOperationException("Solana WebSocket subscription failed: " + message);
            }
            activeSubscriptions[root.GetProperty("result").GetUInt64()] = target;
        }

        private OnChainSourceAccountUpdate ParseAccountUpdate(
            JsonElement result,
            string address,
            IDictionary<string, ulong> writeVersions,
            long observedAtUnixMs)
        {
            var value = result.GetProperty("value");
            var data = value.GetProperty("data");
            if (data.ValueKind != JsonValueKind.Array
                || data.GetArrayLength() < 1
                || string.IsNullOrWhiteSpace(data[0].GetString()))
            {
                throw new InvalidOperationException("The WebSocket account update contained invalid data.");
            }
            var nextWriteVersion = writeVersions.TryGetValue(address, out var previous)
                ? checked(previous + 1)
                : 1;
            writeVersions[address] = nextWriteVersion;
            return new OnChainSourceAccountUpdate(new OnChainRawAccountUpdate
            {
                Pubkey = address,
                OwnerProgram = value.GetProperty("owner").GetString()
                               ?? throw new InvalidOperationException("The account owner was missing."),
                DataBase64 = data[0].GetString()!,
                Slot = result.GetProperty("context").GetProperty("slot").GetUInt64(),
                WriteVersion = nextWriteVersion,
                Commitment = _configuration.Commitment,
                SourceId = _sourceId,
                ObservedAtUnixMs = observedAtUnixMs
            });
        }

        private static void QueueTransaction(
            JsonElement result,
            long observedAtUnixMs,
            ISet<string> signatures,
            Queue<string> signatureOrder,
            ChannelWriter<PendingTransaction> output)
        {
            var value = result.GetProperty("value");
            if (value.TryGetProperty("err", out var error) && error.ValueKind != JsonValueKind.Null)
            {
                return;
            }
            var signature = value.GetProperty("signature").GetString();
            if (string.IsNullOrWhiteSpace(signature) || !signatures.Add(signature))
            {
                return;
            }
            signatureOrder.Enqueue(signature);
            while (signatureOrder.Count > SignatureDeduplicationCapacity)
            {
                signatures.Remove(signatureOrder.Dequeue());
            }
            output.TryWrite(new PendingTransaction(signature, observedAtUnixMs));
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
                    throw new InvalidOperationException("The provider returned a non-text WebSocket message.");
                }
                if (output.Length + result.Count > MaximumMessageBytes)
                {
                    throw new InvalidOperationException("The provider WebSocket message exceeded the local safety limit.");
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

        private static string ToRpcCommitment(OnChainCommitment commitment)
        {
            return commitment switch
            {
                OnChainCommitment.Processed => "processed",
                OnChainCommitment.Confirmed => "confirmed",
                OnChainCommitment.Finalized => "finalized",
                _ => throw new ArgumentOutOfRangeException(nameof(commitment))
            };
        }

        internal static string[] GetRuntimeSubscriptionMethods(
            IEnumerable<string> accountAddresses,
            IEnumerable<string> poolAddresses) =>
            CreateRuntimeSubscriptionTargets(accountAddresses, poolAddresses)
                .Select(static target => target.Kind == SubscriptionKind.Account
                    ? "accountSubscribe"
                    : "logsSubscribe")
                .ToArray();

        private static SubscriptionTarget[] CreateRuntimeSubscriptionTargets(
            IEnumerable<string> accountAddresses,
            IEnumerable<string> poolAddresses) =>
            accountAddresses
                .Select(static address => new SubscriptionTarget(SubscriptionKind.Account, address))
                .Concat(poolAddresses.Select(static address =>
                    new SubscriptionTarget(SubscriptionKind.Logs, address)))
                .ToArray();

        private enum SubscriptionKind
        {
            Account,
            Logs
        }

        private sealed record SubscriptionTarget(SubscriptionKind Kind, string? Address);
        private sealed record PendingTransaction(string Signature, long ObservedAtUnixMs);
    }
}

using TrenchHQ.Models;
using Grpc.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TrenchHQ.Helpers
{
    internal sealed class OnChainRecoveryStateChangedEventArgs(OnChainRecoveryState state) : EventArgs
    {
        internal OnChainRecoveryState State { get; } = state;
    }

    internal sealed class OnChainStreamCoordinator
    {
        private const int ChannelCapacity = 1024;
        private const string EnginePoolOwner = "solana:mainnet-beta";

        private readonly OnChainEngineClient _engine;
        private readonly string _checkpointFolder;
        private readonly Func<OnChainProviderConfiguration, string, bool, IOnChainStreamSource> _sourceFactory;
        private readonly Func<
            OnChainProviderConfiguration,
            string,
            OnChainWatchedPoolSelection[],
            CancellationToken,
            Task> _reconcile;
        private readonly OnChainProviderFailureCounter _failures = new();
        private readonly SemaphoreSlim _sessionLock = new(1, 1);
        private CancellationTokenSource? _sessionCts;
        private CancellationTokenSource? _attemptCts;
        private Task _sessionTask = Task.CompletedTask;
        private string? _sessionSignature;
        private int _engineRestartRequested;
        private OnChainRecoveryState _state = OnChainRecoveryState.Connecting;

        internal OnChainStreamCoordinator(
            OnChainEngineClient engine,
            string checkpointFolder,
            Func<OnChainProviderConfiguration, string, bool, IOnChainStreamSource>? sourceFactory = null,
            Func<
                OnChainProviderConfiguration,
                string,
                OnChainWatchedPoolSelection[],
                CancellationToken,
                Task>? reconcile = null)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _checkpointFolder = checkpointFolder ?? throw new ArgumentNullException(nameof(checkpointFolder));
            _sourceFactory = sourceFactory
                             ?? CreateSource;
            _reconcile = reconcile ?? new OnChainSnapshotReconciler(engine).ReconcileAsync;
            _engine.StateChanged += OnEngineStateChanged;
        }

        internal event EventHandler<OnChainRecoveryStateChangedEventArgs>? StateChanged;
        internal event EventHandler<string>? StreamError;
        internal event EventHandler<OnChainProviderFailureEventArgs>? ProviderFailed;

        internal OnChainRecoveryState State => _state;

        internal async Task StartAsync(
            OnChainProviderConfiguration configuration,
            string apiKey,
            OnChainWatchedPoolSelection[] pools,
            CancellationToken cancellationToken = default,
            IReadOnlySet<string>? webSocketPoolAddresses = null)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            var supportedPools = (pools ?? [])
                .Where(static pool => pool.Descriptor.SupportStatus == OnChainSupportStatus.Supported)
                .ToArray();
            if (supportedPools.Length == 0)
            {
                await StopAsync().ConfigureAwait(false);
                return;
            }

            var streamedPools = supportedPools.Where(pool =>
                webSocketPoolAddresses?.Contains(pool.Descriptor.PoolKey.PoolAddress) == true).ToArray();
            var sampledPools = supportedPools.Except(streamedPools).ToArray();
            if (sampledPools.Any(pool => !OnChainPriceModes.SupportsPolling(pool.Descriptor)))
            {
                throw new InvalidOperationException("This pool requires event streaming for execution prices.");
            }
            var sessionSignature = BuildSessionSignature(
                configuration, apiKey, supportedPools, streamedPools);

            await _sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_sessionCts != null
                    && !_sessionTask.IsCompleted
                    && string.Equals(_sessionSignature, sessionSignature, StringComparison.Ordinal))
                {
                    return;
                }
                await StopSessionCoreAsync().ConfigureAwait(false);
                await _engine.ReplaceWatchedPoolsAsync(
                    EnginePoolOwner,
                    supportedPools,
                    cancellationToken).ConfigureAwait(false);
                var sessionCts = new CancellationTokenSource();
                _sessionCts = sessionCts;
                _sessionSignature = sessionSignature;
                _sessionTask = RunSessionAsync(
                    configuration,
                    apiKey,
                    supportedPools,
                    streamedPools,
                    sampledPools,
                    sessionCts.Token);
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        internal async Task StopAsync()
        {
            await _sessionLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopSessionCoreAsync().ConfigureAwait(false);
                await _engine.ReplaceWatchedPoolsAsync(EnginePoolOwner, []).ConfigureAwait(false);
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        private async Task StopSessionCoreAsync()
        {
            var cts = _sessionCts;
            var task = _sessionTask;
            _sessionCts = null;
            _sessionTask = Task.CompletedTask;
            _sessionSignature = null;
            Interlocked.Exchange(ref _engineRestartRequested, 0);
            if (cts == null)
            {
                return;
            }
            cts.Cancel();
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                cts.Dispose();
            }
        }

        private async Task RunSessionAsync(
            OnChainProviderConfiguration configuration,
            string apiKey,
            OnChainWatchedPoolSelection[] pools,
            OnChainWatchedPoolSelection[] streamedPools,
            OnChainWatchedPoolSelection[] sampledPools,
            CancellationToken cancellationToken)
        {
            var checkpoint = configuration.ReplayEnabled && streamedPools.Length > 0
                ? await OnChainCheckpointStore.LoadAsync(
                    _checkpointFolder,
                    configuration.Id,
                    cancellationToken).ConfigureAwait(false)
                : null;
            var snapshotRequired = true;
            _failures.Reset();

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (snapshotRequired)
                    {
                        OnChainPipelineDiagnostics.RecordSnapshotRecovery();
                        await SetStateAsync(OnChainRecoveryState.Reconciling, cancellationToken)
                            .ConfigureAwait(false);
                        await _engine.ResetForSnapshotAsync(cancellationToken).ConfigureAwait(false);
                        if (streamedPools.Length > 0)
                        {
                            await _reconcile(configuration, apiKey, streamedPools, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        snapshotRequired = false;
                    }

                    ulong? replayTarget = null;
                    if (checkpoint.HasValue)
                    {
                        OnChainPipelineDiagnostics.RecordReplay();
                        replayTarget = await GetCurrentSlotAsync(configuration, apiKey, cancellationToken)
                            .ConfigureAwait(false);
                        await SetStateAsync(OnChainRecoveryState.Replaying, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await SetStateAsync(OnChainRecoveryState.Live, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    var attemptCheckpoint = await RunAttemptAsync(
                        configuration,
                        apiKey,
                        streamedPools,
                        sampledPools,
                        checkpoint,
                        replayTarget,
                        cancellationToken).ConfigureAwait(false);
                    checkpoint = configuration.ReplayEnabled && streamedPools.Length > 0
                        ? attemptCheckpoint : null;
                    if (!configuration.ReplayEnabled || streamedPools.Length == 0)
                    {
                        snapshotRequired = true;
                    }
                    throw new IOException("The on-chain data stream ended unexpectedly.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (OnChainUsageBudgetException exception)
                {
                    await SetStateAsync(OnChainRecoveryState.Stale, cancellationToken).ConfigureAwait(false);
                    StreamError?.Invoke(this, exception.Message);
                    return;
                }
                catch (Exception) when (ConsumeEngineRestartRequest())
                {
                    snapshotRequired = true;
                    _failures.Reset();
                    await SetStateAsync(OnChainRecoveryState.SnapshotRequired, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (RpcException exception) when (
                    exception.StatusCode is StatusCode.Unauthenticated or StatusCode.PermissionDenied)
                {
                    await SetStateAsync(OnChainRecoveryState.Stale, cancellationToken)
                        .ConfigureAwait(false);
                    StreamError?.Invoke(this, "The streaming provider rejected the API key or account permissions.");
                    ProviderFailed?.Invoke(this, new OnChainProviderFailureEventArgs(
                        configuration.Id,
                        OnChainProviderFailureKind.Authentication));
                    return;
                }
                catch (RpcException exception) when (
                    checkpoint.HasValue
                    && exception.StatusCode is StatusCode.OutOfRange
                        or StatusCode.InvalidArgument
                        or StatusCode.FailedPrecondition)
                {
                    checkpoint = null;
                    snapshotRequired = true;
                    _failures.Reset();
                    await SetStateAsync(OnChainRecoveryState.SnapshotRequired, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OnChainForkDetectedException)
                {
                    checkpoint = null;
                    snapshotRequired = true;
                    _failures.Reset();
                    await SetStateAsync(OnChainRecoveryState.SnapshotRequired, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is SolanaRpcException { IsAccessRejected: true }
                    or OnChainProviderResponseException { Kind: OnChainProviderFailureKind.Authentication })
                {
                    await SetStateAsync(OnChainRecoveryState.Stale, cancellationToken)
                        .ConfigureAwait(false);
                    StreamError?.Invoke(this, "The Solana provider rejected the API key or account billing state.");
                    ProviderFailed?.Invoke(this, new OnChainProviderFailureEventArgs(
                        configuration.Id,
                        OnChainProviderFailureKind.Authentication));
                    return;
                }
                catch (RpcException exception) when (exception.StatusCode == StatusCode.ResourceExhausted)
                {
                    OnChainPipelineDiagnostics.RecordReconnect();
                    await SetStateAsync(OnChainRecoveryState.Reconnecting, cancellationToken)
                        .ConfigureAwait(false);
                    StreamError?.Invoke(this, Redact(exception.Message, apiKey));
                    if (_failures.Record(OnChainProviderFailureKind.RateLimited))
                    {
                        ProviderFailed?.Invoke(this, new OnChainProviderFailureEventArgs(
                            configuration.Id,
                            OnChainProviderFailureKind.RateLimited));
                    }
                    var seconds = Math.Min(30, Math.Pow(2, Math.Min(_failures.Count, 5)));
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(seconds * 1000 + Random.Shared.Next(0, 500)),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is SolanaRpcException { IsRateLimited: true }
                    or OnChainProviderResponseException { Kind: OnChainProviderFailureKind.RateLimited })
                {
                    OnChainPipelineDiagnostics.RecordReconnect();
                    await SetStateAsync(OnChainRecoveryState.Reconnecting, cancellationToken)
                        .ConfigureAwait(false);
                    StreamError?.Invoke(this, Redact(exception.Message, apiKey));
                    if (_failures.Record(OnChainProviderFailureKind.RateLimited))
                    {
                        ProviderFailed?.Invoke(this, new OnChainProviderFailureEventArgs(
                            configuration.Id,
                            OnChainProviderFailureKind.RateLimited));
                    }
                    var seconds = Math.Min(30, Math.Pow(2, Math.Min(_failures.Count, 5)));
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(seconds * 1000 + Random.Shared.Next(0, 500)),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    if (!checkpoint.HasValue)
                    {
                        snapshotRequired = true;
                    }
                    OnChainPipelineDiagnostics.RecordReconnect();
                    await SetStateAsync(OnChainRecoveryState.Reconnecting, cancellationToken)
                        .ConfigureAwait(false);
                    StreamError?.Invoke(this, Redact(exception.Message, apiKey));
                    if (_failures.Record(OnChainProviderFailureKind.Transport))
                    {
                        ProviderFailed?.Invoke(this, new OnChainProviderFailureEventArgs(
                            configuration.Id,
                            OnChainProviderFailureKind.Transport));
                    }
                    var exponentialSeconds = Math.Min(30, Math.Pow(2, Math.Min(_failures.Count, 5)));
                    var delay = TimeSpan.FromMilliseconds(
                        exponentialSeconds * 1000 + Random.Shared.Next(0, 500));
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task<ulong?> RunAttemptAsync(
            OnChainProviderConfiguration configuration,
            string apiKey,
            OnChainWatchedPoolSelection[] streamedPools,
            OnChainWatchedPoolSelection[] sampledPools,
            ulong? checkpoint,
            ulong? replayTarget,
            CancellationToken cancellationToken)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Interlocked.Exchange(ref _attemptCts, attemptCts);
            if (Volatile.Read(ref _engineRestartRequested) != 0)
            {
                attemptCts.Cancel();
            }
            var channel = Channel.CreateBounded<OnChainSourceUpdate>(new BoundedChannelOptions(ChannelCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
            var subscriptions = new List<(IOnChainStreamSource Source, OnChainStreamSubscription Subscription)>();
            if (streamedPools.Length > 0)
            {
                subscriptions.Add((_sourceFactory(configuration, apiKey, true),
                    new OnChainStreamSubscription
                    {
                        Pools = streamedPools,
                        Commitment = configuration.Commitment,
                        FromSlot = checkpoint
                    }));
            }
            if (sampledPools.Length > 0)
            {
                subscriptions.Add((_sourceFactory(configuration, apiKey, false),
                    new OnChainStreamSubscription
                    {
                        Pools = sampledPools,
                        Commitment = configuration.Commitment
                    }));
            }
            var producer = RunSourcesAsync(subscriptions, channel.Writer, attemptCts.Token);
            try
            {
                var nextCheckpoint = await ConsumeAsync(
                    channel.Reader,
                    configuration,
                    checkpoint,
                    replayTarget,
                    attemptCts.Token).ConfigureAwait(false);
                await producer.ConfigureAwait(false);
                return nextCheckpoint;
            }
            finally
            {
                Interlocked.CompareExchange(ref _attemptCts, null, attemptCts);
                attemptCts.Cancel();
                try
                {
                    await producer.ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }

        private async Task<ulong?> ConsumeAsync(
            ChannelReader<OnChainSourceUpdate> input,
            OnChainProviderConfiguration configuration,
            ulong? checkpoint,
            ulong? replayTarget,
            CancellationToken cancellationToken)
        {
            var confirmedSlot = checkpoint;
            var lastCheckpointWrite = DateTimeOffset.MinValue;
            await foreach (var update in input.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (update)
                {
                    case OnChainSourceAccountSnapshot snapshot:
                        await _engine.PublishAccountSnapshotAsync(snapshot.Updates, cancellationToken)
                            .ConfigureAwait(false);
                        _failures.RecordSuccess(DateTimeOffset.UtcNow);
                        snapshot.Processed.TrySetResult();
                        break;
                    case OnChainSourceAccountUpdate account:
                        await _engine.PublishAccountUpdateAsync(account.Update, cancellationToken)
                            .ConfigureAwait(false);
                        _failures.RecordSuccess(DateTimeOffset.UtcNow);
                        break;
                    case OnChainSourceTransactionUpdate transaction:
                        await _engine.PublishTransactionUpdateAsync(transaction.Update, cancellationToken)
                            .ConfigureAwait(false);
                        _failures.RecordSuccess(DateTimeOffset.UtcNow);
                        break;
                    case OnChainSourceSlotUpdate slot when slot.Status == OnChainSlotStatus.Dead:
                        throw new OnChainForkDetectedException(slot.Slot);
                    case OnChainSourceSlotUpdate slot when
                        slot.Status is OnChainSlotStatus.Confirmed or OnChainSlotStatus.Finalized:
                        confirmedSlot = !confirmedSlot.HasValue || slot.Slot > confirmedSlot.Value
                            ? slot.Slot
                            : confirmedSlot;
                        if (replayTarget.HasValue && slot.Slot >= replayTarget.Value)
                        {
                            replayTarget = null;
                            await SetStateAsync(OnChainRecoveryState.Live, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        if (confirmedSlot.HasValue
                            && DateTimeOffset.UtcNow - lastCheckpointWrite >= TimeSpan.FromSeconds(5))
                        {
                            await OnChainCheckpointStore.SaveAsync(
                                _checkpointFolder,
                                configuration.Id,
                                confirmedSlot.Value,
                                cancellationToken).ConfigureAwait(false);
                            lastCheckpointWrite = DateTimeOffset.UtcNow;
                        }
                        break;
                }
            }

            if (confirmedSlot.HasValue)
            {
                await OnChainCheckpointStore.SaveAsync(
                    _checkpointFolder,
                    configuration.Id,
                    confirmedSlot.Value,
                    cancellationToken).ConfigureAwait(false);
            }
            return confirmedSlot;
        }

        private async Task SetStateAsync(
            OnChainRecoveryState state,
            CancellationToken cancellationToken)
        {
            if (_state == state)
            {
                return;
            }
            _state = state;
            await _engine.SetRecoveryStateAsync(state, cancellationToken).ConfigureAwait(false);
            StateChanged?.Invoke(this, new OnChainRecoveryStateChangedEventArgs(state));
        }

        internal static async Task RunSourcesAsync(
            IEnumerable<(IOnChainStreamSource Source, OnChainStreamSubscription Subscription)> subscriptions,
            ChannelWriter<OnChainSourceUpdate> output,
            CancellationToken cancellationToken)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var tasks = subscriptions.Select(item =>
                item.Source.RunAsync(item.Subscription, output, linkedCts.Token)).ToArray();
            try
            {
                await await Task.WhenAny(tasks).ConfigureAwait(false);
                throw new IOException("An on-chain data source ended unexpectedly.");
            }
            catch (Exception exception)
            {
                output.TryComplete(exception);
                throw;
            }
            finally
            {
                linkedCts.Cancel();
                try
                {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }

        private static async Task<ulong> GetCurrentSlotAsync(
            OnChainProviderConfiguration configuration,
            string apiKey,
            CancellationToken cancellationToken)
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var rpc = new SolanaRpcClient(httpClient, configuration, apiKey);
            return await rpc.GetSlotAsync(configuration.Commitment, cancellationToken).ConfigureAwait(false);
        }

        private static string Redact(string? message, string apiKey)
        {
            var safe = string.IsNullOrWhiteSpace(message) ? "The on-chain stream failed." : message.Trim();
            safe = safe.Replace(apiKey, "[redacted]", StringComparison.Ordinal);
            return safe.Length <= 300 ? safe : safe[..300];
        }

        private void OnEngineStateChanged(object? sender, OnChainEngineStateChangedEventArgs e)
        {
            if (e.State != OnChainEngineState.Reconnecting || _sessionCts == null)
            {
                return;
            }
            Interlocked.Exchange(ref _engineRestartRequested, 1);
            try
            {
                Volatile.Read(ref _attemptCts)?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private bool ConsumeEngineRestartRequest()
        {
            return Interlocked.Exchange(ref _engineRestartRequested, 0) != 0;
        }

        private static string BuildSessionSignature(
            OnChainProviderConfiguration configuration,
            string apiKey,
            IEnumerable<OnChainWatchedPoolSelection> pools,
            IEnumerable<OnChainWatchedPoolSelection> streamedPools)
        {
            var credentialHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
            var poolSignature = string.Join("\n", pools
                .OrderBy(static pool => pool.Descriptor.PoolKey.ProtocolId, StringComparer.Ordinal)
                .ThenBy(static pool => pool.Descriptor.PoolKey.PoolAddress, StringComparer.Ordinal)
                .Select(static pool => string.Join('|',
                    pool.Descriptor.PoolKey.ProtocolId,
                    pool.Descriptor.PoolKey.PoolAddress,
                    pool.SelectedMint,
                    pool.Descriptor.BaseMint,
                    pool.Descriptor.QuoteMint,
                    pool.Descriptor.BaseDecimals,
                    pool.Descriptor.QuoteDecimals,
                    pool.Descriptor.BaseVault,
                    pool.Descriptor.QuoteVault,
                    string.Join(',', pool.Descriptor.ProtocolAccounts
                        .OrderBy(static account => account.Role, StringComparer.Ordinal)
                        .ThenBy(static account => account.Address, StringComparer.Ordinal)
                        .Select(static account => $"{account.Role}:{account.Address}")))));
            return string.Join('|',
                       configuration.Id,
                       configuration.ProviderType,
                       configuration.StreamEndpoint,
                       configuration.RpcEndpoint,
                       configuration.Region,
                       configuration.Commitment,
                       configuration.ReplayEnabled,
                       credentialHash)
                   + "\n"
                   + poolSignature
                   + "\nwebSocket="
                   + string.Join(',', streamedPools.Select(static pool => pool.Descriptor.PoolKey.PoolAddress)
                       .OrderBy(static address => address, StringComparer.Ordinal));
        }

        internal static IOnChainStreamSource CreateSource(
            OnChainProviderConfiguration configuration,
            string apiKey,
            bool webSocket = false)
        {
            if (!webSocket)
            {
                return new SolanaRpcSampleStreamSource(configuration, apiKey);
            }
            return OnChainProviderCatalog.Get(configuration.ProviderType).StreamTransport switch
            {
                OnChainStreamTransport.SolanaWebSocket
                    => new SolanaWebSocketStreamSource(configuration, apiKey),
                OnChainStreamTransport.YellowstoneGrpc
                    => new YellowstoneStreamSource(configuration, apiKey),
                _ => throw new ArgumentOutOfRangeException(nameof(configuration))
            };
        }

        private sealed class OnChainForkDetectedException(ulong slot)
            : Exception($"Slot {slot} was marked dead.");
    }
}

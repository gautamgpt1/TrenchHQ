using TrenchHQ.Core.OnChain;
using TrenchHQ.Core.OnChain.Evm;
using TrenchHQ.Core.Providers;
using TrenchHQ.Core.Widgets;
using TrenchHQ.Infrastructure.Diagnostics;
using TrenchHQ.Infrastructure.OnChain;
using TrenchHQ.Infrastructure.Providers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TrenchHQ.Infrastructure.OnChain.Evm
{
    internal sealed class EvmStreamCoordinator
    {
        private const int ChannelCapacity = 1024;
        private const int MaximumPendingLogs = 8192;
        private const ulong InitialLookbackBlocks = 100;
        private const ulong BnbPublicNodeReplayBlocks = 500;
        private const ulong RobinhoodLogPageSize = 10;
        private const ulong RobinhoodPublicNodeReplayBlocks = 40;
        private static readonly TimeSpan EvmHeadSampleInterval = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan EvmSnapshotInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan EvmReferenceSampleInterval = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan EvmPricePollInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan EvmSameHeightVerificationInterval = TimeSpan.FromSeconds(5);

        private readonly OnChainEngineClient _engine;
        private readonly EvmChainDefinition _chain;
        private readonly string _checkpointFolder;
        private readonly HttpClient _httpClient;
        private readonly RobinhoodStockTokenCatalogClient _stockTokens;
        private readonly Func<OnChainProviderConfiguration, string?, IOnChainStreamSource> _sourceFactory;
        private readonly TimeSpan _headSampleInterval;
        private readonly TimeSpan _sampledSnapshotInterval;
        private readonly TimeSpan _referenceSampleInterval;
        private readonly OnChainProviderFailureCounter _failures = new();
        private readonly SemaphoreSlim _sessionLock = new(1, 1);
        private readonly EvmHeadTracker _heads = new();
        private EvmLogDeduplicator _deduplicator = new();
        private CancellationTokenSource? _sessionCts;
        private CancellationTokenSource? _attemptCts;
        private Task _sessionTask = Task.CompletedTask;
        private string? _sessionSignature;
        private int _engineRestartRequested;
        private ulong _connectionEpoch;
        private OnChainRecoveryState _state = OnChainRecoveryState.Connecting;
        private DateTimeOffset _lastSampledSnapshot = DateTimeOffset.MinValue;
        private DateTimeOffset _lastReferenceSnapshot = DateTimeOffset.MinValue;
        private int _snapshotBatchLimit = EvmMulticall3.MaximumCalls;
        private string? _snapshotBatchEndpoint;

        internal EvmStreamCoordinator(
            OnChainEngineClient engine,
            string checkpointFolder,
            EvmChainDefinition chain,
            HttpClient? httpClient = null,
            Func<OnChainProviderConfiguration, string?, IOnChainStreamSource>? sourceFactory = null,
            TimeSpan? headSampleInterval = null,
            TimeSpan? sampledSnapshotInterval = null,
            TimeSpan? referenceSampleInterval = null)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _checkpointFolder = checkpointFolder ?? throw new ArgumentNullException(nameof(checkpointFolder));
            _chain = chain ?? throw new ArgumentNullException(nameof(chain));
            _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            _stockTokens = new RobinhoodStockTokenCatalogClient(_httpClient);
            _sourceFactory = sourceFactory ?? ((configuration, apiKey) =>
                new EvmWebSocketStreamSource(configuration, apiKey));
            _headSampleInterval = headSampleInterval ?? GetHeadSampleInterval(chain.ChainId);
            _sampledSnapshotInterval = sampledSnapshotInterval ?? GetSampledSnapshotInterval(chain.ChainId);
            _referenceSampleInterval = referenceSampleInterval ?? EvmReferenceSampleInterval;
            if (UsesSampledHeads(chain.ChainId)
                && (_headSampleInterval <= TimeSpan.Zero
                    || _sampledSnapshotInterval <= TimeSpan.Zero
                    || _referenceSampleInterval <= TimeSpan.Zero))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(headSampleInterval),
                    "Sampled EVM head and snapshot intervals must be positive.");
            }
            _engine.StateChanged += OnEngineStateChanged;
        }

        internal event EventHandler<OnChainRecoveryStateChangedEventArgs>? StateChanged;
        internal event EventHandler<string>? StreamError;
        internal event EventHandler<OnChainProviderFailureEventArgs>? ProviderFailed;

        internal OnChainRecoveryState State => _state;
        internal EvmChainDefinition ChainDefinition => _chain;

        internal async Task StartAsync(
            OnChainProviderConfiguration configuration,
            string? apiKey,
            OnChainWatchedPoolSelection[] pools,
            CancellationToken cancellationToken = default,
            IReadOnlySet<string>? webSocketPoolIds = null)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            var supportedPools = (pools ?? [])
                .Where(pool => pool.Descriptor.SupportStatus == OnChainSupportStatus.Supported
                               && pool.Descriptor.PoolKey.ChainNamespace == _chain.ChainNamespace
                               && pool.Descriptor.PoolKey.ChainId == _chain.ChainId)
                .ToArray();
            if (supportedPools.Length == 0)
            {
                await StopAsync().ConfigureAwait(false);
                return;
            }
            var (watchedPools, hiddenReference) = AddRequiredReferencePools(
                _chain, supportedPools);
            var subscribedPools = webSocketPoolIds == null
                ? supportedPools
                : supportedPools.Where(pool => webSocketPoolIds.Contains(
                    pool.Descriptor.PoolKey.PoolId)).ToArray();
            var sampledPools = supportedPools.Except(subscribedPools).ToArray();
            if (sampledPools.Any(pool => !OnChainPriceModes.SupportsPolling(pool.Descriptor)))
            {
                throw new ArgumentException(
                    "An execution-price-only pool requires WebSocket mode.", nameof(pools));
            }
            if (OnChainProviderCatalog.Get(configuration.ProviderType).StreamTransport
                    != OnChainStreamTransport.EvmWebSocket
                || configuration.ChainNamespace != _chain.ChainNamespace
                || configuration.ChainId != _chain.ChainId)
            {
                throw new ArgumentException(
                    $"The selected profile is not a {_chain.DisplayName} provider.",
                    nameof(configuration));
            }

            var signature = BuildSessionSignature(
                configuration, apiKey, watchedPools, subscribedPools, hiddenReference != null);
            await _sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_sessionCts != null
                    && !_sessionTask.IsCompleted
                    && string.Equals(_sessionSignature, signature, StringComparison.Ordinal))
                {
                    return;
                }
                await StopSessionCoreAsync().ConfigureAwait(false);

                var batchEndpoint = configuration.Id + ":" + configuration.RpcEndpoint;
                if (_snapshotBatchEndpoint != batchEndpoint)
                {
                    _snapshotBatchEndpoint = batchEndpoint;
                    _snapshotBatchLimit = EvmMulticall3.MaximumCalls;
                }
                await _engine.ReplaceWatchedPoolsAsync(
                    _chain.EnginePoolOwner,
                    watchedPools,
                    cancellationToken).ConfigureAwait(false);
                var sessionCts = new CancellationTokenSource();
                _sessionCts = sessionCts;
                _sessionSignature = signature;
                _sessionTask = RunSessionAsync(
                    configuration,
                    apiKey,
                    watchedPools,
                    subscribedPools,
                    sampledPools,
                    hiddenReference,
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
                await _engine.ReplaceWatchedPoolsAsync(_chain.EnginePoolOwner, []).ConfigureAwait(false);
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
            string? apiKey,
            OnChainWatchedPoolSelection[] pools,
            OnChainWatchedPoolSelection[] subscribedPools,
            OnChainWatchedPoolSelection[] sampledPools,
            OnChainWatchedPoolSelection? hiddenReference,
            CancellationToken cancellationToken)
        {
            _failures.Reset();
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await RunAttemptAsync(
                        configuration, apiKey, pools, subscribedPools, sampledPools,
                        hiddenReference, cancellationToken)
                        .ConfigureAwait(false);
                    throw new IOException("The EVM data stream ended unexpectedly.");
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
                    _failures.Reset();
                    await SetStateAsync(OnChainRecoveryState.SnapshotRequired, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is EvmJsonRpcException { Kind: EvmRpcFailureKind.AuthenticationRejected }
                    or OnChainProviderResponseException { Kind: OnChainProviderFailureKind.Authentication })
                {
                    await SetStateAsync(OnChainRecoveryState.Stale, cancellationToken)
                        .ConfigureAwait(false);
                    StreamError?.Invoke(this, "The EVM provider rejected the API key or account billing state.");
                    ProviderFailed?.Invoke(this, new OnChainProviderFailureEventArgs(
                        configuration.Id,
                        OnChainProviderFailureKind.Authentication));
                    return;
                }
                catch (Exception exception) when (exception is EvmJsonRpcException { Kind: EvmRpcFailureKind.RateLimited }
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
                            OnChainProviderFailureKind.RateLimited, OnChainRequestRetry.RetryAfter(exception)));
                    }
                    var seconds = Math.Min(30, Math.Pow(2, Math.Min(_failures.Count, 5)));
                    var delay = OnChainRequestRetry.RetryAfter(exception)
                        ?? TimeSpan.FromMilliseconds(seconds * 1000 + Random.Shared.Next(0, 500));
                    await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
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
                    var seconds = Math.Min(30, Math.Pow(2, Math.Min(_failures.Count, 5)));
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(seconds * 1000 + Random.Shared.Next(0, 500)),
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task RunAttemptAsync(
            OnChainProviderConfiguration configuration,
            string? apiKey,
            OnChainWatchedPoolSelection[] pools,
            OnChainWatchedPoolSelection[] subscribedPools,
            OnChainWatchedPoolSelection[] sampledPools,
            OnChainWatchedPoolSelection? hiddenReference,
            CancellationToken cancellationToken)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Interlocked.Exchange(ref _attemptCts, attemptCts);
            if (Volatile.Read(ref _engineRestartRequested) != 0)
            {
                attemptCts.Cancel();
            }
            var rpc = new EvmJsonRpcClient(_httpClient, configuration, apiKey);
            var channel = Channel.CreateBounded<OnChainSourceUpdate>(new BoundedChannelOptions(ChannelCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
            var subscription = new OnChainStreamSubscription
            {
                Pools = subscribedPools,
                ConnectionEpoch = ++_connectionEpoch
            };
            var producer = subscribedPools.Length == 0
                ? Task.CompletedTask
                : RunSourceAsync(_sourceFactory(configuration, apiKey), subscription,
                    channel.Writer, attemptCts.Token);
            var stockReferenceProducer = RunRobinhoodStockReferencesAsync(
                pools, attemptCts.Token);
            var sampledHeadProducer = Task.CompletedTask;
            try
            {
                await BootstrapAsync(
                    rpc, configuration, pools, subscribedPools, subscription.ConnectionEpoch, attemptCts.Token)
                    .ConfigureAwait(false);
                if (subscribedPools.Length == 0)
                {
                    // A state watcher has no event journal to recover. Finish each sample before
                    // scheduling the next one, including its pinned state reads.
                    await RunSampledHeadsAsync(rpc, channel.Writer, subscription.ConnectionEpoch,
                        EvmPricePollInterval, attemptCts.Token,
                        head => ProcessSampledHeadAsync(head, rpc, configuration, pools,
                            subscribedPools, sampledPools, hiddenReference,
                            subscription.ConnectionEpoch, attemptCts.Token)).ConfigureAwait(false);
                    return;
                }
                if (UsesSampledHeads(_chain.ChainId))
                {
                    sampledHeadProducer = RunSampledHeadsAsync(
                        rpc,
                        channel.Writer,
                        subscription.ConnectionEpoch,
                        sampledPools.Length > 0 ? EvmPricePollInterval
                            : hiddenReference == null ? _headSampleInterval : _referenceSampleInterval,
                        attemptCts.Token);
                }
                await ConsumeAsync(
                    channel.Reader,
                    rpc,
                    configuration,
                    pools,
                    subscribedPools,
                    sampledPools,
                    hiddenReference,
                    subscription.ConnectionEpoch,
                    attemptCts.Token).ConfigureAwait(false);
                await producer.ConfigureAwait(false);
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
                try
                {
                    await sampledHeadProducer.ConfigureAwait(false);
                }
                catch
                {
                }
                try
                {
                    await stockReferenceProducer.ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }

        private async Task RunRobinhoodStockReferencesAsync(
            OnChainWatchedPoolSelection[] pools,
            CancellationToken cancellationToken)
        {
            if (_chain.ChainId != EvmChainDefinitions.RobinhoodMainnetChainId)
            {
                return;
            }
            var quoteAddresses = pools
                .Select(static pool => pool.Descriptor.QuoteMint)
                .Where(static address => EvmAddress.TryNormalize(address, out _))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            while (!cancellationToken.IsCancellationRequested)
            {
                foreach (var address in quoteAddresses)
                {
                    try
                    {
                        var reference = await _stockTokens.GetUsdReferenceAsync(address, cancellationToken)
                            .ConfigureAwait(false);
                        if (reference != null)
                        {
                            await _engine.PublishEvmAssetReferencePriceAsync(
                                _chain.ChainId,
                                reference.Address,
                                reference.Value,
                                reference.SourceId,
                                reference.ObservedAtUnixMs,
                                cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch
                    {
                        // Stock references fail soft; the exact on-chain quote price remains available.
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task RunSampledHeadsAsync(
            EvmJsonRpcClient rpc,
            ChannelWriter<OnChainSourceUpdate> output,
            ulong connectionEpoch,
            TimeSpan interval,
            CancellationToken cancellationToken,
            Func<EvmHeadUpdate, Task>? consumeHead = null)
        {
            try
            {
                var previous = _heads.Tip == null ? null : ToRpcHeader(_heads.Tip);
                var lastFullHeaderAt = DateTimeOffset.UtcNow;
                var unchanged = false;
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                    if (unchanged && UsesDiscountedHeadProbe(rpc.ProviderType)
                        && DateTimeOffset.UtcNow - lastFullHeaderAt < EvmSameHeightVerificationInterval
                        && await rpc.GetBlockNumberAsync(cancellationToken).ConfigureAwait(false)
                           == previous!.Number)
                    {
                        if (consumeHead != null)
                        {
                            _failures.RecordSuccess(DateTimeOffset.UtcNow);
                        }
                        continue;
                    }
                    var latest = await rpc.GetBlockAsync("latest", cancellationToken)
                        .ConfigureAwait(false);
                    unchanged = previous != null
                                && latest.Number == previous.Number
                                && string.Equals(latest.Hash, previous.Hash, StringComparison.Ordinal);
                    previous = latest;
                    lastFullHeaderAt = DateTimeOffset.UtcNow;
                    var head = ToHead(latest, connectionEpoch);
                    if (consumeHead != null)
                    {
                        await consumeHead(head).ConfigureAwait(false);
                        _failures.RecordSuccess(DateTimeOffset.UtcNow);
                    }
                    else
                    {
                        var sampled = new OnChainSourceEvmHeadUpdate(head)
                        {
                            Processed = new(TaskCreationOptions.RunContinuationsAsynchronously)
                        };
                        await output.WriteAsync(sampled,
                            cancellationToken).ConfigureAwait(false);
                        await sampled.Processed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                output.TryComplete(exception);
                throw;
            }
        }

        internal static bool UsesDiscountedHeadProbe(string providerType) =>
            providerType is OnChainProviderTypes.AlchemyEthereum or OnChainProviderTypes.AlchemyBase
                or OnChainProviderTypes.AlchemyBnb or OnChainProviderTypes.AlchemyRobinhood;

        private async Task BootstrapAsync(
            EvmJsonRpcClient rpc,
            OnChainProviderConfiguration configuration,
            OnChainWatchedPoolSelection[] pools,
            OnChainWatchedPoolSelection[] subscribedPools,
            ulong connectionEpoch,
            CancellationToken cancellationToken)
        {
            OnChainPipelineDiagnostics.RecordSnapshotRecovery();
            await SetStateAsync(OnChainRecoveryState.Reconciling, cancellationToken)
                .ConfigureAwait(false);
            await _engine.SetEvmProviderContextAsync(
                _chain.ChainId,
                configuration.Id,
                $"{_chain.CatalogChainId}-json-rpc:{configuration.ProviderType}",
                cancellationToken).ConfigureAwait(false);
            await _engine.ResetEvmForSnapshotAsync(_chain.ChainId, cancellationToken)
                .ConfigureAwait(false);
            _deduplicator = new EvmLogDeduplicator();

            var latest = await rpc.GetBlockAsync("latest", cancellationToken).ConfigureAwait(false);
            if (subscribedPools.Length == 0)
            {
                var head = ToHead(latest, connectionEpoch);
                _heads.Reset(head);
                await _engine.PublishEvmHeadAsync(head, cancellationToken).ConfigureAwait(false);
                await SnapshotPoolsAsync(rpc, pools, latest, cancellationToken).ConfigureAwait(false);
                _lastSampledSnapshot = DateTimeOffset.UtcNow;
                _lastReferenceSnapshot = _lastSampledSnapshot;
                await SetStateAsync(OnChainRecoveryState.Live, cancellationToken).ConfigureAwait(false);
                return;
            }
            var checkpoint = await EvmCheckpointStore.LoadAsync(
                _checkpointFolder,
                configuration.Id,
                cancellationToken).ConfigureAwait(false);
            var fromBlock = latest.Number >= InitialLookbackBlocks - 1
                ? latest.Number - (InitialLookbackBlocks - 1)
                : 0;
            if (checkpoint != null && checkpoint.SafeBlockNumber <= latest.Number)
            {
                var checkpointBlock = await rpc.GetBlockAsync(
                    $"0x{checkpoint.SafeBlockNumber:x}",
                    cancellationToken).ConfigureAwait(false);
                if (checkpointBlock.Hash == checkpoint.SafeBlockHash
                    && latest.Number - checkpoint.SafeBlockNumber
                       < EvmRecoveryPlanner.MaximumRecoveryBlocks)
                {
                    fromBlock = EvmRecoveryPlanner.GetRecoveryStart(
                        checkpoint.SafeBlockNumber,
                        latest.Number);
                }
            }
            if (configuration.ProviderType == OnChainProviderTypes.PublicNodeBnb)
            {
                fromBlock = EvmRecoveryPlanner.ApplyReplayDepthLimit(
                    fromBlock,
                    latest.Number,
                    BnbPublicNodeReplayBlocks);
            }
            fromBlock = GetProviderReplayStart(
                configuration.ProviderType, fromBlock, latest.Number);

            await LoadHeadWindowAsync(rpc, latest, connectionEpoch, cancellationToken)
                .ConfigureAwait(false);
            await SetStateAsync(OnChainRecoveryState.Replaying, cancellationToken)
                .ConfigureAwait(false);
            OnChainPipelineDiagnostics.RecordReplay();
            await ReplayLogsAsync(
                    rpc, subscribedPools, fromBlock, latest.Number, connectionEpoch, cancellationToken)
                .ConfigureAwait(false);
            await SnapshotPoolsAsync(rpc, pools, latest, cancellationToken).ConfigureAwait(false);
            if (UsesSampledHeads(_chain.ChainId))
            {
                _lastSampledSnapshot = DateTimeOffset.UtcNow;
                _lastReferenceSnapshot = _lastSampledSnapshot;
            }
            await RefreshFinalityAsync(rpc, configuration, latest, cancellationToken)
                .ConfigureAwait(false);
            await SetStateAsync(OnChainRecoveryState.Live, cancellationToken).ConfigureAwait(false);
        }

        private async Task ConsumeAsync(
            ChannelReader<OnChainSourceUpdate> input,
            EvmJsonRpcClient rpc,
            OnChainProviderConfiguration configuration,
            OnChainWatchedPoolSelection[] pools,
            OnChainWatchedPoolSelection[] subscribedPools,
            OnChainWatchedPoolSelection[] sampledPools,
            OnChainWatchedPoolSelection? hiddenReference,
            ulong connectionEpoch,
            CancellationToken cancellationToken)
        {
            var pendingLogs = new List<EvmLogUpdate>();
            var lastFinalityRefresh = DateTimeOffset.UtcNow;
            var finalityRefreshInterval = _headSampleInterval;
            await foreach (var sourceUpdate in input.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (sourceUpdate)
                {
                    case OnChainSourceEvmLogUpdate log:
                        if (log.Update.ConnectionEpoch != connectionEpoch)
                        {
                            break;
                        }
                        if (UsesSampledHeads(_chain.ChainId)
                            || log.Update.Removed
                            || _heads.Contains(log.Update.BlockNumber, log.Update.BlockHash))
                        {
                            await ProcessLogAsync(log.Update, cancellationToken).ConfigureAwait(false);
                            _failures.RecordSuccess(DateTimeOffset.UtcNow);
                        }
                        else
                        {
                            if (pendingLogs.Count >= MaximumPendingLogs)
                            {
                                throw new EvmSnapshotRequiredException("The pending EVM log buffer is full.");
                            }
                            pendingLogs.Add(log.Update);
                        }
                        break;
                    case OnChainSourceEvmHeadUpdate head:
                        if (head.Update.ConnectionEpoch != connectionEpoch)
                        {
                            head.Processed?.TrySetResult();
                            break;
                        }
                        await ProcessHeadAsync(
                            head.Update,
                            rpc,
                            configuration,
                            pools,
                            subscribedPools,
                            sampledPools,
                            hiddenReference,
                            connectionEpoch,
                            cancellationToken).ConfigureAwait(false);
                        await FlushPendingLogsAsync(pendingLogs, cancellationToken).ConfigureAwait(false);
                        if (DateTimeOffset.UtcNow - lastFinalityRefresh >= finalityRefreshInterval)
                        {
                            await RefreshFinalityAsync(
                                rpc,
                                configuration,
                                ToRpcHeader(head.Update),
                                cancellationToken)
                                .ConfigureAwait(false);
                            lastFinalityRefresh = DateTimeOffset.UtcNow;
                        }
                        _failures.RecordSuccess(DateTimeOffset.UtcNow);
                        head.Processed?.TrySetResult();
                        break;
                }
            }
        }

        private async Task ProcessHeadAsync(
            EvmHeadUpdate head,
            EvmJsonRpcClient rpc,
            OnChainProviderConfiguration configuration,
            OnChainWatchedPoolSelection[] pools,
            OnChainWatchedPoolSelection[] subscribedPools,
            OnChainWatchedPoolSelection[] sampledPools,
            OnChainWatchedPoolSelection? hiddenReference,
            ulong connectionEpoch,
            CancellationToken cancellationToken)
        {
            if (UsesSampledHeads(_chain.ChainId))
            {
                await ProcessSampledHeadAsync(
                    head,
                    rpc,
                    configuration,
                    pools,
                    subscribedPools,
                    sampledPools,
                    hiddenReference,
                    connectionEpoch,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var transition = _heads.Apply(head);
            switch (transition.Kind)
            {
                case EvmHeadTransitionKind.First:
                case EvmHeadTransitionKind.Extended:
                    await _engine.PublishEvmHeadAsync(head, cancellationToken).ConfigureAwait(false);
                    return;
                case EvmHeadTransitionKind.Duplicate:
                    return;
                case EvmHeadTransitionKind.Gap:
                    await SetStateAsync(OnChainRecoveryState.Replaying, cancellationToken)
                        .ConfigureAwait(false);
                    if (configuration.ProviderType == OnChainProviderTypes.PublicNodeBnb
                        && head.Number - transition.MissingFrom!.Value >= BnbPublicNodeReplayBlocks)
                    {
                        await SetStateAsync(OnChainRecoveryState.SnapshotRequired, cancellationToken)
                            .ConfigureAwait(false);
                        await BootstrapAsync(
                            rpc, configuration, pools, subscribedPools, connectionEpoch, cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }
                    await RecoverGapAsync(
                        rpc,
                        subscribedPools,
                        transition.MissingFrom!.Value,
                        head.Number,
                        connectionEpoch,
                        cancellationToken).ConfigureAwait(false);
                    await SnapshotPoolsAsync(rpc, pools, ToRpcHeader(head), cancellationToken)
                        .ConfigureAwait(false);
                    await SetStateAsync(OnChainRecoveryState.Live, cancellationToken).ConfigureAwait(false);
                    return;
                case EvmHeadTransitionKind.Reorg:
                    await SetStateAsync(OnChainRecoveryState.Reconciling, cancellationToken)
                        .ConfigureAwait(false);
                    _deduplicator.RollbackAfter(transition.CommonAncestorNumber!.Value);
                    await _engine.PublishEvmRollbackAsync(new EvmRollback
                    {
                        ChainId = configuration.ChainId,
                        ToBlockNumber = transition.CommonAncestorNumber.Value,
                        ToBlockHash = transition.CommonAncestorHash!
                    }, cancellationToken).ConfigureAwait(false);
                    await _engine.PublishEvmHeadAsync(head, cancellationToken).ConfigureAwait(false);
                    await ReplayLogsAsync(
                        rpc,
                        subscribedPools,
                        head.Number,
                        head.Number,
                        connectionEpoch,
                        cancellationToken).ConfigureAwait(false);
                    await SnapshotPoolsAsync(rpc, pools, ToRpcHeader(head), cancellationToken)
                        .ConfigureAwait(false);
                    await SetStateAsync(OnChainRecoveryState.Live, cancellationToken).ConfigureAwait(false);
                    return;
                case EvmHeadTransitionKind.SnapshotRequired:
                    await SetStateAsync(OnChainRecoveryState.SnapshotRequired, cancellationToken)
                        .ConfigureAwait(false);
                    await BootstrapAsync(
                        rpc, configuration, pools, subscribedPools, connectionEpoch, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private async Task ProcessSampledHeadAsync(
            EvmHeadUpdate head,
            EvmJsonRpcClient rpc,
            OnChainProviderConfiguration configuration,
            OnChainWatchedPoolSelection[] pools,
            OnChainWatchedPoolSelection[] subscribedPools,
            OnChainWatchedPoolSelection[] sampledPools,
            OnChainWatchedPoolSelection? hiddenReference,
            ulong connectionEpoch,
            CancellationToken cancellationToken)
        {
            var tip = _heads.Tip;
            if (tip == null)
            {
                _heads.Reset(head);
                await _engine.PublishEvmHeadAsync(head, cancellationToken).ConfigureAwait(false);
                await SnapshotSampledPoolsAsync(rpc, sampledPools, hiddenReference, head,
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            if (head.Number == tip.Number
                && string.Equals(head.Hash, tip.Hash, StringComparison.Ordinal))
            {
                await SnapshotHiddenReferenceIfDueAsync(rpc, hiddenReference, head, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            if (head.Number <= tip.Number)
            {
                await BootstrapAsync(
                    rpc,
                    configuration,
                    pools,
                    subscribedPools,
                    connectionEpoch,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (subscribedPools.Length == 0)
            {
                // EIP-1898 requireCanonical pins every read to this sample's hash. Earlier
                // sampled blocks need no ancestry lookup: there are no intervening events.
                _heads.SetSampledTip(head);
                await _engine.PublishEvmHeadAsync(head, cancellationToken).ConfigureAwait(false);
                await SnapshotSampledPoolsAsync(rpc, sampledPools, hiddenReference, head,
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            if (head.Number - tip.Number == 1)
            {
                if (!string.Equals(head.ParentHash, tip.Hash, StringComparison.Ordinal))
                {
                    await BootstrapAsync(rpc, configuration, pools, subscribedPools,
                        connectionEpoch, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            else
            {
                var canonicalTip = await rpc.GetBlockAsync(
                    $"0x{tip.Number:x}",
                    cancellationToken).ConfigureAwait(false);
                if (!string.Equals(canonicalTip.Hash, tip.Hash, StringComparison.Ordinal))
                {
                    await BootstrapAsync(rpc, configuration, pools, subscribedPools,
                        connectionEpoch, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            _heads.SetSampledTip(head);
            await _engine.PublishEvmHeadAsync(head, cancellationToken).ConfigureAwait(false);
            if (DateTimeOffset.UtcNow - _lastSampledSnapshot >= _sampledSnapshotInterval)
            {
                await SnapshotPoolsAsync(rpc, pools, ToRpcHeader(head), cancellationToken)
                    .ConfigureAwait(false);
                _lastSampledSnapshot = DateTimeOffset.UtcNow;
                _lastReferenceSnapshot = _lastSampledSnapshot;
            }
            else
            {
                await SnapshotSampledPoolsAsync(rpc, sampledPools, hiddenReference, head,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task SnapshotSampledPoolsAsync(
            EvmJsonRpcClient rpc,
            OnChainWatchedPoolSelection[] sampledPools,
            OnChainWatchedPoolSelection? hiddenReference,
            EvmHeadUpdate head,
            CancellationToken cancellationToken)
        {
            var referenceDue = hiddenReference != null
                && DateTimeOffset.UtcNow - _lastReferenceSnapshot >= _referenceSampleInterval;
            await SnapshotPoolsAsync(rpc,
                referenceDue ? [.. sampledPools, hiddenReference!] : sampledPools,
                ToRpcHeader(head), cancellationToken).ConfigureAwait(false);
            if (referenceDue)
            {
                _lastReferenceSnapshot = DateTimeOffset.UtcNow;
            }
        }

        private async Task SnapshotHiddenReferenceIfDueAsync(
            EvmJsonRpcClient rpc,
            OnChainWatchedPoolSelection? hiddenReference,
            EvmHeadUpdate head,
            CancellationToken cancellationToken)
        {
            if (hiddenReference == null
                || DateTimeOffset.UtcNow - _lastReferenceSnapshot < _referenceSampleInterval)
            {
                return;
            }
            await SnapshotPoolsAsync(rpc, [hiddenReference], ToRpcHeader(head), cancellationToken)
                .ConfigureAwait(false);
            _lastReferenceSnapshot = DateTimeOffset.UtcNow;
        }

        private async Task RecoverGapAsync(
            EvmJsonRpcClient rpc,
            OnChainWatchedPoolSelection[] pools,
            ulong fromBlock,
            ulong toBlock,
            ulong connectionEpoch,
            CancellationToken cancellationToken)
        {
            _ = EvmRecoveryPlanner.BuildRanges(fromBlock, toBlock);
            for (var number = fromBlock; number <= toBlock; number++)
            {
                var block = await rpc.GetBlockAsync($"0x{number:x}", cancellationToken)
                    .ConfigureAwait(false);
                var normalized = ToHead(block, connectionEpoch);
                var transition = _heads.Apply(normalized);
                if (transition.Kind is not (EvmHeadTransitionKind.Extended or EvmHeadTransitionKind.Duplicate))
                {
                    throw new EvmSnapshotRequiredException("The recovered EVM block range is not canonical.");
                }
                if (transition.Kind == EvmHeadTransitionKind.Extended)
                {
                    await _engine.PublishEvmHeadAsync(normalized, cancellationToken).ConfigureAwait(false);
                }
                if (number == ulong.MaxValue)
                {
                    break;
                }
            }
            await ReplayLogsAsync(rpc, pools, fromBlock, toBlock, connectionEpoch, cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task LoadHeadWindowAsync(
            EvmJsonRpcClient rpc,
            EvmRpcBlockHeader latest,
            ulong connectionEpoch,
            CancellationToken cancellationToken)
        {
            var from = latest.Number > EvmRecoveryPlanner.DefaultOverlapBlocks
                ? latest.Number - EvmRecoveryPlanner.DefaultOverlapBlocks
                : 0;
            EvmHeadUpdate? previous = null;
            for (var number = from; number <= latest.Number; number++)
            {
                var block = number == latest.Number
                    ? latest
                    : await rpc.GetBlockAsync($"0x{number:x}", cancellationToken).ConfigureAwait(false);
                var head = ToHead(block, connectionEpoch);
                if (previous != null && head.ParentHash != previous.Hash)
                {
                    throw new EvmSnapshotRequiredException("The EVM head window changed during recovery.");
                }
                if (previous == null)
                {
                    _heads.Reset(head);
                }
                else
                {
                    _heads.Apply(head);
                }
                await _engine.PublishEvmHeadAsync(head, cancellationToken).ConfigureAwait(false);
                previous = head;
                if (number == ulong.MaxValue)
                {
                    break;
                }
            }
        }

        private async Task ReplayLogsAsync(
            EvmJsonRpcClient rpc,
            OnChainWatchedPoolSelection[] pools,
            ulong fromBlock,
            ulong toBlock,
            ulong connectionEpoch,
            CancellationToken cancellationToken)
        {
            foreach (var range in EvmRecoveryPlanner.BuildRanges(
                         fromBlock,
                         toBlock,
                         GetReplayPageSize(_chain.ChainId, rpc.ProviderType)))
            {
                var replayed = new List<EvmLogUpdate>();
                foreach (var filter in EvmWebSocketStreamSource.CreateRpcLogFilters(
                             pools,
                             range.From,
                             range.To))
                {
                    var result = await rpc.GetLogsAsync(filter, cancellationToken).ConfigureAwait(false);
                    if (result.ValueKind != JsonValueKind.Array
                        || replayed.Count + result.GetArrayLength() > MaximumPendingLogs)
                    {
                        throw new EvmSnapshotRequiredException(
                            "The EVM log replay response is invalid or too large.");
                    }
                    var observedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    replayed.AddRange(result.EnumerateArray().Select(log =>
                        EvmWebSocketStreamSource.ParseLog(
                            log,
                            _chain.ChainId,
                            connectionEpoch,
                            observedAt)));
                }
                var updates = replayed
                    .OrderBy(static log => log.BlockNumber)
                    .ThenBy(static log => log.TransactionIndex)
                    .ThenBy(static log => log.LogIndex)
                    .ToArray();
                foreach (var update in updates)
                {
                    await ProcessLogAsync(update, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task SnapshotPoolsAsync(
            EvmJsonRpcClient rpc,
            OnChainWatchedPoolSelection[] pools,
            EvmRpcBlockHeader block,
            CancellationToken cancellationToken)
        {
            var plans = pools.Select(pool => (Pool: pool, Requests: BuildSnapshotRequests(pool)))
                .Where(static plan => plan.Requests.Length > 0)
                .ToArray();
            var requests = plans.SelectMany(static plan => plan.Requests).ToArray();
            var results = new string[requests.Length];
            for (var offset = 0; offset < requests.Length;)
            {
                var batch = requests.Skip(offset).Take(_snapshotBatchLimit).ToArray();
                if (batch.Length == 1)
                {
                    var value = await rpc.CallAsync(batch[0].Target, batch[0].Data,
                        block, cancellationToken).ConfigureAwait(false);
                    if (value.ValueKind != JsonValueKind.String)
                    {
                        throw new EvmSnapshotRequiredException("An EVM pool state response was malformed.");
                    }
                    results[offset] = value.GetString()!;
                    offset++;
                    continue;
                }
                JsonElement aggregate;
                try
                {
                    aggregate = await rpc.CallAsync(EvmMulticall3.Address,
                        EvmMulticall3.EncodeCalls(batch), block, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (EvmJsonRpcException exception) when (
                    exception.Kind == EvmRpcFailureKind.RequestLimitExceeded)
                {
                    // Learn the endpoint's smaller bound once; keep every retry pinned to this block.
                    _snapshotBatchLimit = Math.Max(1, batch.Length / 2);
                    continue;
                }
                if (aggregate.ValueKind != JsonValueKind.String
                    || !EvmMulticall3.TryDecodeResults(aggregate.GetString(), batch.Length,
                        out var decoded))
                {
                    throw new EvmSnapshotRequiredException("A batched EVM pool state response was malformed.");
                }
                decoded.CopyTo(results, offset);
                offset += batch.Length;
            }
            var resultIndex = 0;
            foreach (var plan in plans)
            {
                var pool = plan.Pool;
                var first = results[resultIndex++];
                var second = plan.Requests.Length == 2 ? results[resultIndex++] : string.Empty;
                EvmSnapshotCall[] calls;
                switch (pool.Descriptor.PoolKey.ProtocolId)
                {
                    case OnChainProtocolIds.Curve:
                    case OnChainProtocolIds.FermiSwap:
                        continue;
                    case OnChainProtocolIds.PonsV2Curve:
                    {
                        if (!EthereumAbi.TryDecodePonsV2Reserves(first, out _, out _))
                        {
                            throw new EvmSnapshotRequiredException("A pons v2 curve reserve snapshot was malformed.");
                        }
                        calls =
                        [
                            new EvmSnapshotCall
                            {
                                Id = "getReserves",
                                Success = true,
                                ReturnData = first
                            }
                        ];
                        break;
                    }
                    case OnChainProtocolIds.UniswapV2:
                    case OnChainProtocolIds.PancakeV2:
                    {
                        if (!EthereumAbi.TryDecodeUniswapV2Reserves(first, out _, out _))
                        {
                            throw new EvmSnapshotRequiredException("A Uniswap v2 reserve snapshot was malformed.");
                        }
                        calls =
                        [
                            new EvmSnapshotCall
                            {
                                Id = "getReserves",
                                Success = true,
                                ReturnData = first
                            }
                        ];
                        break;
                    }
                    case OnChainProtocolIds.AerodromeClassic:
                    {
                        if (!EthereumAbi.TryDecodeAerodromeClassicReserves(
                                first,
                                out _,
                                out _))
                        {
                            throw new EvmSnapshotRequiredException(
                                "An Aerodrome classic reserve snapshot was malformed.");
                        }
                        calls =
                        [
                            new EvmSnapshotCall
                            {
                                Id = "getReserves",
                                Success = true,
                                ReturnData = first
                            }
                        ];
                        break;
                    }
                    case OnChainProtocolIds.UniswapV3:
                    case OnChainProtocolIds.PancakeV3:
                    {
                        if (!EthereumAbi.TryDecodeUniswapV3Slot0(first, out _, out _)
                            || !EthereumAbi.TryDecodeSingleUnsigned(second, 128, out _))
                        {
                            throw new EvmSnapshotRequiredException("A v3 state snapshot was malformed.");
                        }
                        calls =
                        [
                            new EvmSnapshotCall
                            {
                                Id = "slot0",
                                Success = true,
                                ReturnData = first
                            },
                            new EvmSnapshotCall
                            {
                                Id = "liquidity",
                                Success = true,
                                ReturnData = second
                            }
                        ];
                        break;
                    }
                    case OnChainProtocolIds.AerodromeSlipstream:
                    {
                        if (!EthereumAbi.TryDecodeAerodromeSlipstreamSlot0(
                                first,
                                out _,
                                out _)
                            || !EthereumAbi.TryDecodeUnsigned(second, 0, 128, out _))
                        {
                            throw new EvmSnapshotRequiredException(
                                "An Aerodrome Slipstream state snapshot was malformed.");
                        }
                        calls =
                        [
                            new EvmSnapshotCall
                            {
                                Id = "slot0",
                                Success = true,
                                ReturnData = first
                            },
                            new EvmSnapshotCall
                            {
                                Id = "liquidity",
                                Success = true,
                                ReturnData = second
                            }
                        ];
                        break;
                    }
                    case OnChainProtocolIds.UniswapV4:
                    {
                        if (!EthereumAbi.TryDecodeUniswapV4Slot0(first, out _, out _)
                            || !EthereumAbi.TryDecodeUnsigned(second, 0, 128, out _))
                        {
                            throw new EvmSnapshotRequiredException(
                                "A Uniswap v4 StateView snapshot was malformed.");
                        }
                        calls =
                        [
                            new EvmSnapshotCall
                            {
                                Id = "slot0",
                                Success = true,
                                ReturnData = first
                            },
                            new EvmSnapshotCall
                            {
                                Id = "liquidity",
                                Success = true,
                                ReturnData = second
                            }
                        ];
                        break;
                    }
                    case OnChainProtocolIds.PancakeInfinityCl:
                    {
                        if (!EthereumAbi.TryDecodeUniswapV4Slot0(first, out _, out _)
                            || !EthereumAbi.TryDecodeSingleUnsigned(second, 128, out _))
                        {
                            throw new EvmSnapshotRequiredException(
                                "A PancakeSwap Infinity CL snapshot was malformed.");
                        }
                        calls =
                        [
                            new EvmSnapshotCall
                            {
                                Id = "slot0",
                                Success = true,
                                ReturnData = first
                            },
                            new EvmSnapshotCall
                            {
                                Id = "liquidity",
                                Success = true,
                                ReturnData = second
                            }
                        ];
                        break;
                    }
                    case OnChainProtocolIds.PancakeInfinityBin:
                    {
                        if (!EthereumAbi.TryDecodeInfinityBinSlot0(first, out _))
                        {
                            throw new EvmSnapshotRequiredException(
                                "A PancakeSwap Infinity bin snapshot was malformed.");
                        }
                        calls =
                        [
                            new EvmSnapshotCall
                            {
                                Id = "slot0",
                                Success = true,
                                ReturnData = first
                            }
                        ];
                        break;
                    }
                    default:
                        throw new EvmSnapshotRequiredException(
                            "The selected EVM pool protocol has no snapshot implementation.");
                }
                await _engine.PublishEvmSnapshotAsync(new EvmSnapshotResponse
                {
                    RequestId = $"{pool.Descriptor.PoolKey.ProtocolId}:{block.Hash}:{pool.Descriptor.PoolKey.PoolId}",
                    PoolKey = pool.Descriptor.PoolKey,
                    BlockNumber = block.Number,
                    BlockHash = block.Hash,
                    Calls = calls
                }, cancellationToken).ConfigureAwait(false);
            }
        }

        private (string Target, string Data)[] BuildSnapshotRequests(OnChainWatchedPoolSelection pool)
        {
            var key = pool.Descriptor.PoolKey;
            switch (key.ProtocolId)
            {
                case OnChainProtocolIds.Curve:
                case OnChainProtocolIds.FermiSwap:
                    return [];
                case OnChainProtocolIds.PonsV2Curve:
                case OnChainProtocolIds.UniswapV2:
                case OnChainProtocolIds.PancakeV2:
                case OnChainProtocolIds.AerodromeClassic:
                    return [(key.PoolId, EthereumAbi.GetReservesSelector)];
                case OnChainProtocolIds.UniswapV3:
                case OnChainProtocolIds.PancakeV3:
                case OnChainProtocolIds.AerodromeSlipstream:
                    return
                    [
                        (key.PoolId, EthereumAbi.Slot0Selector),
                        (key.PoolId, EthereumAbi.LiquiditySelector)
                    ];
                case OnChainProtocolIds.UniswapV4:
                {
                    var stateView = _chain.UniswapV4StateViewAddress
                                    ?? throw new EvmSnapshotRequiredException(
                                        $"{_chain.DisplayName} has no Uniswap v4 StateView configured.");
                    return
                    [
                        (stateView, EthereumAbi.EncodeBytes32Call(
                            EthereumAbi.UniswapV4StateViewSlot0Selector, key.PoolId)),
                        (stateView, EthereumAbi.EncodeBytes32Call(
                            EthereumAbi.UniswapV4StateViewLiquiditySelector, key.PoolId))
                    ];
                }
                case OnChainProtocolIds.PancakeInfinityCl:
                    return
                    [
                        (key.DeploymentKey.ContractAddress, EthereumAbi.EncodeBytes32Call(
                            EthereumAbi.InfinitySlot0Selector, key.PoolId)),
                        (key.DeploymentKey.ContractAddress, EthereumAbi.EncodeBytes32Call(
                            EthereumAbi.InfinityLiquiditySelector, key.PoolId))
                    ];
                case OnChainProtocolIds.PancakeInfinityBin:
                    return
                    [
                        (key.DeploymentKey.ContractAddress, EthereumAbi.EncodeBytes32Call(
                            EthereumAbi.InfinitySlot0Selector, key.PoolId))
                    ];
                default:
                    throw new EvmSnapshotRequiredException(
                        "The selected EVM pool protocol has no snapshot implementation.");
            }
        }

        private async Task RefreshFinalityAsync(
            EvmJsonRpcClient rpc,
            OnChainProviderConfiguration configuration,
            EvmRpcBlockHeader head,
            CancellationToken cancellationToken)
        {
            var safe = await TryGetFinalityBlockAsync(
                rpc,
                "safe",
                configuration.CapabilitySnapshot?.SafeBlock
                    ?? OnChainProviderCapabilityState.Unknown,
                cancellationToken).ConfigureAwait(false);
            var finalized = await TryGetFinalityBlockAsync(
                rpc,
                "finalized",
                configuration.CapabilitySnapshot?.FinalizedBlock
                    ?? OnChainProviderCapabilityState.Unknown,
                cancellationToken).ConfigureAwait(false);
            var update = new EvmFinalityUpdate
            {
                ChainId = configuration.ChainId,
                Head = Reference(head),
                Safe = safe == null ? null : Reference(safe),
                Finalized = finalized == null ? null : Reference(finalized),
                ObservedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            if (!_heads.ApplyFinality(update))
            {
                return;
            }
            await _engine.PublishEvmFinalityAsync(update, cancellationToken).ConfigureAwait(false);
            if (safe != null)
            {
                await EvmCheckpointStore.SaveAsync(
                    _checkpointFolder,
                    configuration.Id,
                    new EvmCheckpoint
                    {
                        ChainId = configuration.ChainId,
                        SafeBlockNumber = safe.Number,
                        SafeBlockHash = safe.Hash,
                        ObservedAtUnixMs = update.ObservedAtUnixMs
                    },
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private static async Task<EvmRpcBlockHeader?> TryGetFinalityBlockAsync(
            EvmJsonRpcClient rpc,
            string tag,
            OnChainProviderCapabilityState capability,
            CancellationToken cancellationToken)
        {
            if (capability == OnChainProviderCapabilityState.Unsupported)
            {
                return null;
            }
            try
            {
                return await rpc.GetBlockAsync(tag, cancellationToken).ConfigureAwait(false);
            }
            catch (EvmJsonRpcException) when (capability == OnChainProviderCapabilityState.Unknown)
            {
                return null;
            }
        }

        private async Task ProcessLogAsync(
            EvmLogUpdate update,
            CancellationToken cancellationToken)
        {
            switch (_deduplicator.Accept(update))
            {
                case EvmLogAcceptance.Accepted:
                case EvmLogAcceptance.Removed:
                    await _engine.PublishEvmLogAsync(update, cancellationToken).ConfigureAwait(false);
                    break;
                case EvmLogAcceptance.Duplicate:
                    break;
                case EvmLogAcceptance.UnknownRemoval:
                    throw new EvmSnapshotRequiredException("A removed EVM log was outside the local journal.");
            }
        }

        private async Task FlushPendingLogsAsync(
            List<EvmLogUpdate> pending,
            CancellationToken cancellationToken)
        {
            foreach (var log in pending
                         .Where(log => _heads.Contains(log.BlockNumber, log.BlockHash))
                         .OrderBy(static log => log.BlockNumber)
                         .ThenBy(static log => log.TransactionIndex)
                         .ThenBy(static log => log.LogIndex)
                         .ToArray())
            {
                pending.Remove(log);
                await ProcessLogAsync(log, cancellationToken).ConfigureAwait(false);
            }
            var tip = _heads.Tip;
            if (tip != null)
            {
                pending.RemoveAll(log => log.BlockNumber <= tip.Number
                                         && !_heads.Contains(log.BlockNumber, log.BlockHash));
            }
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
            await _engine.SetEvmRecoveryStateAsync(_chain.ChainId, state, cancellationToken)
                .ConfigureAwait(false);
            StateChanged?.Invoke(this, new OnChainRecoveryStateChangedEventArgs(state));
        }

        private EvmHeadUpdate ToHead(EvmRpcBlockHeader block, ulong connectionEpoch)
        {
            return new EvmHeadUpdate
            {
                ChainId = _chain.ChainId,
                ConnectionEpoch = connectionEpoch,
                Number = block.Number,
                Hash = block.Hash,
                ParentHash = block.ParentHash,
                Timestamp = block.Timestamp,
                ObservedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }

        private static EvmRpcBlockHeader ToRpcHeader(EvmHeadUpdate block)
        {
            return new EvmRpcBlockHeader
            {
                Number = block.Number,
                Hash = block.Hash,
                ParentHash = block.ParentHash,
                Timestamp = block.Timestamp
            };
        }

        private static EvmBlockReference Reference(EvmRpcBlockHeader block)
        {
            return new EvmBlockReference { Number = block.Number, Hash = block.Hash };
        }

        private static async Task RunSourceAsync(
            IOnChainStreamSource source,
            OnChainStreamSubscription subscription,
            ChannelWriter<OnChainSourceUpdate> output,
            CancellationToken cancellationToken)
        {
            try
            {
                await source.RunAsync(subscription, output, cancellationToken).ConfigureAwait(false);
                output.TryComplete();
            }
            catch (Exception exception)
            {
                output.TryComplete(exception);
                throw;
            }
        }

        private static string BuildSessionSignature(
            OnChainProviderConfiguration configuration,
            string? apiKey,
            IEnumerable<OnChainWatchedPoolSelection> pools,
            IEnumerable<OnChainWatchedPoolSelection> subscribedPools,
            bool hasHiddenReference)
        {
            var credentialHash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(apiKey ?? string.Empty)));
            var poolSignature = string.Join('\n', pools
                .OrderBy(static pool => pool.Descriptor.PoolKey.ProtocolId, StringComparer.Ordinal)
                .ThenBy(static pool => pool.Descriptor.PoolKey.PoolId, StringComparer.Ordinal)
                .Select(static pool => string.Join('|',
                    pool.Descriptor.PoolKey.ProtocolId,
                    pool.Descriptor.PoolKey.PoolId,
                    pool.SelectedMint,
                    pool.Descriptor.BaseMint,
                    pool.Descriptor.QuoteMint,
                    pool.Descriptor.BaseDecimals,
                    pool.Descriptor.QuoteDecimals,
                    pool.Descriptor.PricingMode ?? string.Empty)));
            return string.Join('|',
                       configuration.Id,
                       configuration.ProviderType,
                       configuration.StreamEndpoint,
                       configuration.RpcEndpoint,
                       credentialHash)
                   + "\n"
                   + poolSignature
                   + "\nwebSocket="
                   + string.Join(',', subscribedPools
                       .Select(static pool => pool.Descriptor.PoolKey.PoolId)
                       .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase))
                   + (hasHiddenReference ? "\nreference:sampled" : "\nreference:live");
        }

        internal static ulong GetReplayPageSize(string chainId, string providerType) =>
            chainId == EvmChainDefinitions.RobinhoodMainnetChainId
                || OnChainProviderCatalog.Get(providerType).ProviderFamily == "alchemy"
                ? RobinhoodLogPageSize
                : EvmRecoveryPlanner.DefaultPageSize;

        internal static ulong GetProviderReplayStart(
            string providerType, ulong requestedStart, ulong latest) =>
            providerType == OnChainProviderTypes.PublicNodeRobinhood
                ? Math.Max(requestedStart,
                    latest >= RobinhoodPublicNodeReplayBlocks - 1
                        ? latest - (RobinhoodPublicNodeReplayBlocks - 1)
                        : 0)
                : requestedStart;

        internal static bool UsesSampledHeads(string chainId) =>
            EvmChainDefinitions.Supported.Any(chain =>
                string.Equals(chain.ChainId, chainId, StringComparison.Ordinal));

        internal static TimeSpan GetHeadSampleInterval(string chainId) =>
            UsesSampledHeads(chainId) ? EvmHeadSampleInterval : TimeSpan.Zero;

        internal static TimeSpan GetSampledSnapshotInterval(string chainId) =>
            UsesSampledHeads(chainId) ? EvmSnapshotInterval : TimeSpan.Zero;

        internal static (OnChainWatchedPoolSelection[] Pools, OnChainWatchedPoolSelection? HiddenReference)
            AddRequiredReferencePools(EvmChainDefinition chain, OnChainWatchedPoolSelection[] pools)
        {
            if (chain.NativeUsdReferencePoolId == null
                || chain.CreateNativeUsdReferenceSelection == null
                || !pools.Any(pool => string.Equals(
                    pool.Descriptor.QuoteMint,
                    chain.WrappedNativeAssetAddress,
                    StringComparison.OrdinalIgnoreCase))
                || pools.Any(pool => string.Equals(
                    pool.Descriptor.PoolKey.PoolId,
                    chain.NativeUsdReferencePoolId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return (pools, null);
            }

            var hiddenReference = chain.CreateNativeUsdReferenceSelection();
            return ([.. pools, hiddenReference], hiddenReference);
        }

        private static string Redact(string? message, string? apiKey)
        {
            var safe = string.IsNullOrWhiteSpace(message)
                ? "The EVM stream failed."
                : message.Trim();
            if (!string.IsNullOrEmpty(apiKey))
            {
                safe = safe.Replace(apiKey, "[redacted]", StringComparison.Ordinal);
            }
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

        private sealed class EvmSnapshotRequiredException(string message) : Exception(message);
    }
}

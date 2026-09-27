using TrenchHQ.Core.OnChain;
using TrenchHQ.Core.OnChain.Evm;
using TrenchHQ.Infrastructure.Diagnostics;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TrenchHQ.Infrastructure.OnChain
{
    internal enum OnChainEngineState
    {
        Stopped,
        Starting,
        Ready,
        Reconnecting,
        Unavailable
    }

    internal sealed class OnChainPriceUpdatedEventArgs(OnChainPriceUpdate update) : EventArgs
    {
        internal OnChainPriceUpdate Update { get; } = update;
    }

    internal sealed class OnChainEngineStateChangedEventArgs(OnChainEngineState state) : EventArgs
    {
        internal OnChainEngineState State { get; } = state;
    }

    internal sealed class OnChainEngineRequestException(
        string code,
        string message,
        bool retryable) : InvalidOperationException(message)
    {
        internal string Code { get; } = code;
        internal bool Retryable { get; } = retryable;
    }

    internal sealed class OnChainEngineClient
    {
        private const string ProtocolName = "trenchhq.onchain";
        private const int ProtocolVersion = 2;
        private const int MaximumFrameBytes = 4 * 1024 * 1024;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        public static OnChainEngineClient Current { get; } = new();

        private readonly string? _configuredEnginePath;
        private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly SemaphoreSlim _desiredUpdateLock = new(1, 1);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingRequests = new();
        private readonly object _desiredSync = new();
        private readonly Dictionary<string, OnChainWatchedPoolSelection[]> _desiredPoolsByOwner =
            new(StringComparer.Ordinal);
        private Process? _process;
        private Stream? _input;
        private CancellationTokenSource? _lifetimeCts;
        private nint _jobHandle;
        private int _nextRequestId;
        private int _state = (int)OnChainEngineState.Stopped;
        private int _intentionalStop;
        private int _recoveryScheduled;

        internal OnChainEngineClient(string? enginePath = null)
        {
            _configuredEnginePath = enginePath;
        }

        internal event EventHandler<OnChainPriceUpdatedEventArgs>? PriceUpdated;
        internal event EventHandler<OnChainEngineStateChangedEventArgs>? StateChanged;
        internal event EventHandler<string>? EngineError;

        internal OnChainEngineState State => (OnChainEngineState)Volatile.Read(ref _state);

        internal async Task<OnChainDecodedPoolAccount> DecodePoolAccountAsync(
            string protocolId,
            string poolAddress,
            string selectedMint,
            string ownerProgram,
            string dataBase64,
            CancellationToken cancellationToken = default)
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            var payload = await SendRequestCoreAsync(
                "decodePoolAccount",
                new
                {
                    protocolId,
                    poolAddress,
                    selectedMint,
                    ownerProgram,
                    dataBase64
                },
                cancellationToken).ConfigureAwait(false);
            return payload.Deserialize<OnChainDecodedPoolAccount>(JsonOptions)
                   ?? throw new InvalidDataException("The on-chain engine returned an empty pool account.");
        }

        internal async Task<OnChainDerivedAddress> DerivePumpBondingCurveAsync(
            string mint,
            CancellationToken cancellationToken = default)
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            var payload = await SendRequestCoreAsync(
                "derivePumpBondingCurve",
                new { mint },
                cancellationToken).ConfigureAwait(false);
            return payload.Deserialize<OnChainDerivedAddress>(JsonOptions)
                   ?? throw new InvalidDataException("The on-chain engine returned an empty derived address.");
        }

        internal async Task ReplaceWatchedPoolsAsync(
            OnChainWatchedPoolSelection[] pools,
            CancellationToken cancellationToken = default)
        {
            await ReplaceWatchedPoolsAsync("default", pools, cancellationToken).ConfigureAwait(false);
        }

        internal async Task ReplaceWatchedPoolsAsync(
            string owner,
            OnChainWatchedPoolSelection[] pools,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(owner))
            {
                throw new ArgumentException("A watched-pool owner is required.", nameof(owner));
            }
            var normalized = (pools ?? [])
                .Where(static pool => !string.IsNullOrWhiteSpace(pool.SelectedMint)
                                      && !string.IsNullOrWhiteSpace(pool.Descriptor?.PoolKey?.PoolAddress))
                .GroupBy(static pool =>
                    $"{pool.Descriptor.PoolKey.ChainNamespace}|{pool.Descriptor.PoolKey.ChainId}|{pool.Descriptor.PoolKey.ProtocolId}|{pool.Descriptor.PoolKey.PoolAddress}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .ToArray();
            await _desiredUpdateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                OnChainWatchedPoolSelection[] desired;
                lock (_desiredSync)
                {
                    if (normalized.Length == 0)
                    {
                        _desiredPoolsByOwner.Remove(owner);
                    }
                    else
                    {
                        _desiredPoolsByOwner[owner] = normalized;
                    }
                    desired = GetDesiredPoolsCore();
                }

                if (desired.Length == 0)
                {
                    if (IsRunning())
                    {
                        try
                        {
                            await SendRequestCoreAsync(
                                "replaceWatchedPools",
                                new { pools = desired },
                                cancellationToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            await StopProcessAsync().ConfigureAwait(false);
                        }
                    }
                    return;
                }

                await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
                await SendRequestCoreAsync(
                    "replaceWatchedPools",
                    new { pools = desired },
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _desiredUpdateLock.Release();
            }
        }

        internal async Task SetRecoveryStateAsync(
            OnChainRecoveryState state,
            CancellationToken cancellationToken = default)
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            await SendRequestCoreAsync("setRecoveryState", new { state }, cancellationToken)
                .ConfigureAwait(false);
        }

        internal async Task ResetForSnapshotAsync(CancellationToken cancellationToken = default)
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            await SendRequestCoreAsync("resetForSnapshot", new { }, cancellationToken)
                .ConfigureAwait(false);
        }

        internal async Task ResetEvmForSnapshotAsync(CancellationToken cancellationToken = default)
        {
            await ResetEvmForSnapshotAsync(
                EvmChainDefinitions.EthereumMainnet.ChainId,
                cancellationToken).ConfigureAwait(false);
        }

        internal async Task ResetEvmForSnapshotAsync(
            string chainId,
            CancellationToken cancellationToken = default)
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            await SendRequestCoreAsync("resetEvmForSnapshot", new { chainId }, cancellationToken)
                .ConfigureAwait(false);
        }

        internal async Task SetEvmRecoveryStateAsync(
            string chainId,
            OnChainRecoveryState state,
            CancellationToken cancellationToken = default)
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            await SendRequestCoreAsync(
                "setEvmRecoveryState",
                new { chainId, state },
                cancellationToken).ConfigureAwait(false);
        }

        internal async Task SetEvmProviderContextAsync(
            string providerProfileId,
            string sourceId,
            CancellationToken cancellationToken = default)
        {
            await SetEvmProviderContextAsync(
                EvmChainDefinitions.EthereumMainnet.ChainId,
                providerProfileId,
                sourceId,
                cancellationToken).ConfigureAwait(false);
        }

        internal async Task SetEvmProviderContextAsync(
            string chainId,
            string providerProfileId,
            string sourceId,
            CancellationToken cancellationToken = default)
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            await SendRequestCoreAsync(
                "setEvmProviderContext",
                new { chainId, providerProfileId, sourceId },
                cancellationToken).ConfigureAwait(false);
        }

        internal async Task PublishAccountUpdateAsync(
            OnChainRawAccountUpdate update,
            CancellationToken cancellationToken = default)
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            OnChainPipelineDiagnostics.RecordSourceUpdate();
            await SendEventAsync("rawAccountUpdate", update, cancellationToken).ConfigureAwait(false);
        }

        internal async Task PublishAccountSnapshotAsync(
            OnChainRawAccountUpdate[] updates,
            CancellationToken cancellationToken = default)
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            OnChainPipelineDiagnostics.RecordSourceUpdate();
            await SendRequestCoreAsync("rawAccountSnapshot", updates, cancellationToken).ConfigureAwait(false);
        }

        internal async Task PublishEvmHeadAsync(
            EvmHeadUpdate update,
            CancellationToken cancellationToken = default)
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            OnChainPipelineDiagnostics.RecordSourceUpdate();
            await SendRequestCoreAsync("evmHead", update, cancellationToken).ConfigureAwait(false);
        }

        internal async Task PublishEvmLogAsync(
            EvmLogUpdate update,
            CancellationToken cancellationToken = default)
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            OnChainPipelineDiagnostics.RecordSourceUpdate();
            await SendRequestCoreAsync("evmLog", update, cancellationToken).ConfigureAwait(false);
        }

        internal async Task PublishEvmSnapshotAsync(
            EvmSnapshotResponse snapshot,
            CancellationToken cancellationToken = default)
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            await SendRequestCoreAsync("evmSnapshot", snapshot, cancellationToken).ConfigureAwait(false);
        }

        internal async Task PublishEvmFinalityAsync(
            EvmFinalityUpdate update,
            CancellationToken cancellationToken = default)
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            await SendRequestCoreAsync("evmFinality", update, cancellationToken).ConfigureAwait(false);
        }

        internal async Task PublishEvmRollbackAsync(
            EvmRollback rollback,
            CancellationToken cancellationToken = default)
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            await SendRequestCoreAsync("evmRollback", rollback, cancellationToken).ConfigureAwait(false);
        }

        internal async Task PublishTransactionUpdateAsync(
            OnChainRawTransactionUpdate update,
            CancellationToken cancellationToken = default)
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            OnChainPipelineDiagnostics.RecordSourceUpdate();
            await SendEventAsync("rawTransactionUpdate", update, cancellationToken).ConfigureAwait(false);
        }

        internal async Task PublishReferencePriceAsync(
            string referenceId,
            OnChainDecimalValue value,
            string sourceId,
            long observedAtUnixMs,
            CancellationToken cancellationToken = default)
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            await SendRequestCoreAsync(
                "referencePriceUpdate",
                new
                {
                    referenceId,
                    value,
                    sourceId,
                    observedAtUnixMs
                },
                cancellationToken).ConfigureAwait(false);
        }

        internal async Task PublishEvmReferencePriceAsync(
            string chainId,
            string referenceId,
            OnChainDecimalValue value,
            string sourceId,
            long observedAtUnixMs,
            CancellationToken cancellationToken = default)
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            await SendRequestCoreAsync(
                "referencePriceUpdate",
                new
                {
                    referenceId,
                    chainId,
                    value,
                    sourceId,
                    observedAtUnixMs
                },
                cancellationToken).ConfigureAwait(false);
        }

        internal async Task PublishEvmAssetReferencePriceAsync(
            string chainId,
            string assetAddress,
            OnChainDecimalValue value,
            string sourceId,
            long observedAtUnixMs,
            CancellationToken cancellationToken = default)
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            await SendRequestCoreAsync(
                "referencePriceUpdate",
                new
                {
                    referenceId = "robinhoodStockUsd",
                    chainId,
                    assetAddress,
                    value,
                    sourceId,
                    observedAtUnixMs
                },
                cancellationToken).ConfigureAwait(false);
        }

        internal async Task StopAsync()
        {
            await _desiredUpdateLock.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_desiredSync)
                {
                    _desiredPoolsByOwner.Clear();
                }
                await StopProcessAsync().ConfigureAwait(false);
            }
            finally
            {
                _desiredUpdateLock.Release();
            }
        }

        private async Task EnsureStartedAsync(CancellationToken cancellationToken)
        {
            if (IsRunning() && State == OnChainEngineState.Ready)
            {
                return;
            }

            await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            Process? process = null;
            try
            {
                if (IsRunning() && State == OnChainEngineState.Ready)
                {
                    return;
                }

                SetState(State == OnChainEngineState.Reconnecting
                    ? OnChainEngineState.Reconnecting
                    : OnChainEngineState.Starting);
                var enginePath = ResolveEnginePath();
                if (!File.Exists(enginePath))
                {
                    SetState(OnChainEngineState.Unavailable);
                    throw new FileNotFoundException("The bundled on-chain engine was not found.", enginePath);
                }

                var lifetimeCts = new CancellationTokenSource();
                process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = enginePath,
                        WorkingDirectory = Path.GetDirectoryName(enginePath) ?? AppContext.BaseDirectory,
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    },
                    EnableRaisingEvents = true
                };
                process.Exited += OnProcessExited;
                if (!process.Start())
                {
                    throw new InvalidOperationException("The on-chain engine process did not start.");
                }
                _jobHandle = CreateKillOnCloseJob(process);

                _process = process;
                _input = process.StandardInput.BaseStream;
                _lifetimeCts = lifetimeCts;
                _ = RunReadLoopAsync(process, process.StandardOutput.BaseStream, lifetimeCts.Token);
                _ = RunErrorLoopAsync(process, process.StandardError, lifetimeCts.Token);

                var hello = await SendRequestCoreAsync(
                    "hello",
                    new { client = "TrenchHQ", expectedVersion = ProtocolVersion },
                    cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                if (!hello.TryGetProperty("oneSharedProcess", out var shared) || !shared.GetBoolean())
                {
                    throw new InvalidDataException("The on-chain engine handshake is incompatible.");
                }
                var desired = GetDesiredPools();
                if (desired.Length > 0)
                {
                    await SendRequestCoreAsync(
                        "replaceWatchedPools",
                        new { pools = desired },
                        cancellationToken).ConfigureAwait(false);
                }
                SetState(OnChainEngineState.Ready);
            }
            catch
            {
                StopProcess(process);
                ResetProcess(process);
                if (State != OnChainEngineState.Unavailable)
                {
                    SetState(OnChainEngineState.Reconnecting);
                }
                throw;
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        private async Task<JsonElement> SendRequestCoreAsync(
            string messageType,
            object payload,
            CancellationToken cancellationToken)
        {
            var requestId = Interlocked.Increment(ref _nextRequestId).ToString();
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pendingRequests.TryAdd(requestId, completion))
            {
                throw new InvalidOperationException("Could not allocate an on-chain request ID.");
            }

            try
            {
                await WriteEnvelopeAsync(messageType, requestId, payload, cancellationToken).ConfigureAwait(false);
                return await completion.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _pendingRequests.TryRemove(requestId, out _);
            }
        }

        private Task SendEventAsync(string messageType, object payload, CancellationToken cancellationToken)
        {
            return WriteEnvelopeAsync(messageType, null, payload, cancellationToken);
        }

        private async Task WriteEnvelopeAsync(
            string messageType,
            string? requestId,
            object payload,
            CancellationToken cancellationToken)
        {
            var input = _input ?? throw new InvalidOperationException("The on-chain engine input is unavailable.");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                protocol = ProtocolName,
                protocolVersion = ProtocolVersion,
                messageType,
                requestId,
                payload
            }, JsonOptions);
            if (bytes.Length == 0 || bytes.Length > MaximumFrameBytes)
            {
                throw new InvalidDataException("The on-chain engine frame size is invalid.");
            }

            var prefix = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)bytes.Length);
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await input.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
                    await input.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await input.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // A partial frame cannot be followed by another message on this pipe.
                    // EOF lets the worker exit and the existing lifecycle recovery restart it.
                    input.Dispose();
                    throw;
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task RunReadLoopAsync(Process process, Stream output, CancellationToken cancellationToken)
        {
            try
            {
                var prefix = new byte[4];
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!await ReadExactlyOrEndAsync(output, prefix, cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }
                    var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(prefix));
                    if (length <= 0 || length > MaximumFrameBytes)
                    {
                        throw new InvalidDataException("The on-chain engine returned an invalid frame length.");
                    }
                    var payload = new byte[length];
                    await output.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
                    using var document = JsonDocument.Parse(payload);
                    HandleEnvelope(document.RootElement);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                EngineError?.Invoke(this, exception.Message);
                if (ReferenceEquals(_process, process))
                {
                    StopProcess(process);
                }
            }
        }

        private void HandleEnvelope(JsonElement envelope)
        {
            if (!envelope.TryGetProperty("protocol", out var protocol)
                || !string.Equals(protocol.GetString(), ProtocolName, StringComparison.Ordinal)
                || !envelope.TryGetProperty("protocolVersion", out var version)
                || version.GetInt32() != ProtocolVersion
                || !envelope.TryGetProperty("messageType", out var messageTypeElement))
            {
                throw new InvalidDataException("The on-chain engine returned an incompatible envelope.");
            }

            var messageType = messageTypeElement.GetString() ?? string.Empty;
            var payload = envelope.TryGetProperty("payload", out var payloadElement)
                ? payloadElement.Clone()
                : default;
            if (envelope.TryGetProperty("requestId", out var requestIdElement)
                && requestIdElement.ValueKind == JsonValueKind.String
                && _pendingRequests.TryRemove(requestIdElement.GetString() ?? string.Empty, out var pending))
            {
                if (messageType is "requestFailed" or "fatalError")
                {
                    var code = payload.ValueKind == JsonValueKind.Object
                               && payload.TryGetProperty("code", out var codeElement)
                        ? codeElement.GetString()
                        : null;
                    var message = payload.ValueKind == JsonValueKind.Object
                                  && payload.TryGetProperty("message", out var messageElement)
                        ? messageElement.GetString()
                        : null;
                    var retryable = payload.ValueKind == JsonValueKind.Object
                                    && payload.TryGetProperty("retryable", out var retryableElement)
                                    && retryableElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                                    && retryableElement.GetBoolean();
                    pending.TrySetException(new OnChainEngineRequestException(
                        string.IsNullOrWhiteSpace(code) ? "engineRequestRejected" : code,
                        string.IsNullOrWhiteSpace(message)
                            ? $"The on-chain engine rejected the request ({code ?? "unknown"})."
                            : message,
                        retryable));
                }
                else
                {
                    pending.TrySetResult(payload);
                }
                return;
            }

            if (messageType == "priceDelta")
            {
                var update = payload.Deserialize<OnChainPriceUpdate>(JsonOptions);
                if (update != null)
                {
                    OnChainPipelineDiagnostics.RecordEnginePrice(update.ObservedAtUnixMs);
                    PriceUpdated?.Invoke(this, new OnChainPriceUpdatedEventArgs(update));
                }
            }
        }

        private static async Task<bool> ReadExactlyOrEndAsync(
            Stream stream,
            byte[] buffer,
            CancellationToken cancellationToken)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    return offset == 0
                        ? false
                        : throw new EndOfStreamException("The on-chain engine frame ended early.");
                }
                offset += count;
            }
            return true;
        }

        private async Task RunErrorLoopAsync(
            Process process,
            StreamReader error,
            CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await error.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line == null)
                    {
                        return;
                    }
                    if (ReferenceEquals(_process, process) && !string.IsNullOrWhiteSpace(line))
                    {
                        EngineError?.Invoke(this, line.Length <= 1000 ? line : line[..1000]);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private void OnProcessExited(object? sender, EventArgs e)
        {
            if (sender is Process process)
            {
                _ = HandleUnexpectedExitAsync(process);
            }
        }

        private async Task HandleUnexpectedExitAsync(Process process)
        {
            await _lifecycleLock.WaitAsync().ConfigureAwait(false);
            var shouldRecover = false;
            try
            {
                if (!ReferenceEquals(_process, process))
                {
                    return;
                }
                ResetProcess(process);
                FailPendingRequests(new IOException("The on-chain engine exited unexpectedly."));
                shouldRecover = Volatile.Read(ref _intentionalStop) == 0 && HasDesiredPools();
                SetState(shouldRecover ? OnChainEngineState.Reconnecting : OnChainEngineState.Stopped);
            }
            finally
            {
                _lifecycleLock.Release();
            }

            if (shouldRecover && Interlocked.CompareExchange(ref _recoveryScheduled, 1, 0) == 0)
            {
                try
                {
                    for (var attempt = 0; attempt < 8 && HasDesiredPools(); attempt++)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(5000, 250 * (1 << attempt))))
                            .ConfigureAwait(false);
                        await _desiredUpdateLock.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            if (!HasDesiredPools())
                            {
                                return;
                            }
                            await EnsureStartedAsync(CancellationToken.None).ConfigureAwait(false);
                            var desired = GetDesiredPools();
                            await SendRequestCoreAsync(
                                "replaceWatchedPools",
                                new { pools = desired },
                                CancellationToken.None).ConfigureAwait(false);
                            return;
                        }
                        catch (Exception exception)
                        {
                            EngineError?.Invoke(this, exception.Message);
                        }
                        finally
                        {
                            _desiredUpdateLock.Release();
                        }
                    }
                    if (HasDesiredPools())
                    {
                        SetState(OnChainEngineState.Unavailable);
                    }
                }
                finally
                {
                    Volatile.Write(ref _recoveryScheduled, 0);
                }
            }
        }

        private async Task StopProcessAsync()
        {
            Interlocked.Exchange(ref _intentionalStop, 1);
            await _lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var process = _process;
                if (process != null && IsRunning())
                {
                    try
                    {
                        await SendRequestCoreAsync("shutdown", new { }, CancellationToken.None)
                            .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    }
                    catch
                    {
                        StopProcess(process);
                    }
                }
                ResetProcess(process);
                FailPendingRequests(new OperationCanceledException("The on-chain engine stopped."));
                SetState(OnChainEngineState.Stopped);
            }
            finally
            {
                _lifecycleLock.Release();
                Volatile.Write(ref _intentionalStop, 0);
            }
        }

        private bool IsRunning()
        {
            try
            {
                return _process is { HasExited: false };
            }
            catch
            {
                return false;
            }
        }

        private bool HasDesiredPools()
        {
            lock (_desiredSync)
            {
                return _desiredPoolsByOwner.Values.Any(static pools => pools.Length > 0);
            }
        }

        private OnChainWatchedPoolSelection[] GetDesiredPools()
        {
            lock (_desiredSync)
            {
                return GetDesiredPoolsCore();
            }
        }

        private OnChainWatchedPoolSelection[] GetDesiredPoolsCore()
        {
            return _desiredPoolsByOwner.Values
                .SelectMany(static pools => pools)
                .GroupBy(static pool =>
                    $"{pool.Descriptor.PoolKey.ChainNamespace}|{pool.Descriptor.PoolKey.ChainId}|{pool.Descriptor.PoolKey.ProtocolId}|{pool.Descriptor.PoolKey.PoolAddress}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .ToArray();
        }

        private void ResetProcess(Process? process)
        {
            if (process != null && !ReferenceEquals(_process, process))
            {
                return;
            }
            _lifetimeCts?.Cancel();
            _lifetimeCts?.Dispose();
            _lifetimeCts = null;
            _input = null;
            var jobHandle = Interlocked.Exchange(ref _jobHandle, nint.Zero);
            if (jobHandle != nint.Zero)
            {
                CloseHandle(jobHandle);
            }
            if (_process != null)
            {
                _process.Exited -= OnProcessExited;
                _process.Dispose();
            }
            _process = null;
        }

        private static void StopProcess(Process? process)
        {
            if (process == null)
            {
                return;
            }
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    process.WaitForExit(2000);
                }
            }
            catch
            {
            }
        }

        private void FailPendingRequests(Exception exception)
        {
            foreach (var pending in _pendingRequests.ToArray())
            {
                if (_pendingRequests.TryRemove(pending.Key, out var completion))
                {
                    completion.TrySetException(exception);
                }
            }
        }

        private string ResolveEnginePath()
        {
            if (!string.IsNullOrWhiteSpace(_configuredEnginePath))
            {
                return Path.GetFullPath(_configuredEnginePath);
            }
            return Path.Combine(AppContext.BaseDirectory, "OnChainEngine", "trenchhq-onchain-engine.exe");
        }

        private void SetState(OnChainEngineState state)
        {
            var previous = (OnChainEngineState)Interlocked.Exchange(ref _state, (int)state);
            if (previous != state)
            {
                StateChanged?.Invoke(this, new OnChainEngineStateChangedEventArgs(state));
            }
        }

        private static nint CreateKillOnCloseJob(Process process)
        {
            var handle = CreateJobObject(nint.Zero, null);
            if (handle == nint.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the on-chain engine Job Object.");
            }

            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                }
            };
            if (!SetInformationJobObject(
                    handle,
                    JobObjectExtendedLimitInformationClass,
                    ref limits,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>())
                || !AssignProcessToJobObject(handle, process.Handle))
            {
                var error = Marshal.GetLastWin32Error();
                CloseHandle(handle);
                throw new Win32Exception(error, "Could not contain the on-chain engine process.");
            }
            return handle;
        }

        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const int JobObjectExtendedLimitInformationClass = 9;

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            internal long PerProcessUserTimeLimit;
            internal long PerJobUserTimeLimit;
            internal uint LimitFlags;
            internal nuint MinimumWorkingSetSize;
            internal nuint MaximumWorkingSetSize;
            internal uint ActiveProcessLimit;
            internal nuint Affinity;
            internal uint PriorityClass;
            internal uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            internal ulong ReadOperationCount;
            internal ulong WriteOperationCount;
            internal ulong OtherOperationCount;
            internal ulong ReadTransferCount;
            internal ulong WriteTransferCount;
            internal ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            internal JobObjectBasicLimitInformation BasicLimitInformation;
            internal IoCounters IoInfo;
            internal nuint ProcessMemoryLimit;
            internal nuint JobMemoryLimit;
            internal nuint PeakProcessMemoryUsed;
            internal nuint PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateJobObject(nint jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            nint job,
            int informationClass,
            ref JobObjectExtendedLimitInformation information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(nint job, nint process);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(nint handle);
    }
}

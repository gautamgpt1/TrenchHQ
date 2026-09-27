using TrenchHQ.Core.Markets;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TrenchHQ.Infrastructure.Markets
{
    internal sealed class MarketSidecarStateChangedEventArgs(MarketSidecarState state) : EventArgs
    {
        internal MarketSidecarState State { get; } = state;
    }

    internal sealed class MarketStreamErrorEventArgs(string venueId, string message) : EventArgs
    {
        internal string VenueId { get; } = venueId;
        internal string Message { get; } = message;
    }

    internal sealed class MarketSidecarSubscription
    {
        public string VenueId { get; init; } = string.Empty;
        public string Symbol { get; init; } = string.Empty;
        public string Kind { get; init; } = MarketFeedProtocol.SpotMarketKind;
    }

    internal sealed class MarketPriceUpdatedEventArgs(FeedPriceUpdate update) : EventArgs
    {
        internal FeedPriceUpdate Update { get; } = update;
    }

    internal sealed class MarketSidecarClient
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public static MarketSidecarClient Current { get; } = new();

        private readonly SemaphoreSlim _startLock = new(1, 1);
        private readonly SemaphoreSlim _subscriptionUpdateLock = new(1, 1);
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pendingRequests = new();
        private readonly ConcurrentDictionary<string, byte> _reconnectingStreams = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _subscriptionSync = new();
        private readonly object _reconnectSync = new();
        private Process? _process;
        private StreamWriter? _writer;
        private CancellationTokenSource? _lifetimeCts;
        private CancellationTokenSource? _reconnectCts;
        private Task? _reconnectTask;
        private TaskCompletionSource<bool>? _readyTcs;
        private MarketSidecarSubscription[] _desiredSubscriptions = [];
        private int _nextRequestId;
        private int _state = (int)MarketSidecarState.Stopped;
        private int _stopRequested;

        public event EventHandler<MarketPriceUpdatedEventArgs>? PriceUpdated;
        internal event EventHandler<MarketSidecarStateChangedEventArgs>? StateChanged;
        internal event EventHandler<MarketStreamErrorEventArgs>? StreamError;

        internal MarketSidecarState State => (MarketSidecarState)Volatile.Read(ref _state);

        public async Task StartAsync()
        {
            if (Volatile.Read(ref _stopRequested) != 0)
            {
                throw new OperationCanceledException("Market sidecar has been stopped.");
            }

            try
            {
                await StartCoreAsync(MarketSidecarState.Connecting, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                if (HasDesiredSubscriptions())
                {
                    ScheduleReconnect();
                }
                throw;
            }
        }

        private async Task StartCoreAsync(MarketSidecarState startingState, CancellationToken cancellationToken)
        {
            if (IsRunning())
            {
                return;
            }

            await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            Process? process = null;
            try
            {
                if (IsRunning())
                {
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();
                SetState(startingState);

                var nodePath = ResolveNodePath();
                var scriptPath = ResolveScriptPath();
                if (!File.Exists(nodePath))
                {
                    throw new FileNotFoundException("Bundled node runtime was not found.", nodePath);
                }

                if (!File.Exists(scriptPath))
                {
                    throw new FileNotFoundException("Bundled sidecar script was not found.", scriptPath);
                }

                var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var lifetimeCts = new CancellationTokenSource();
                _readyTcs = readyTcs;
                _lifetimeCts = lifetimeCts;

                process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = nodePath,
                        Arguments = $"\"{scriptPath}\"",
                        WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? AppContext.BaseDirectory,
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    },
                    EnableRaisingEvents = true
                };

                process.Exited += OnSidecarExited;
                if (!process.Start())
                {
                    throw new InvalidOperationException("Failed to start the market sidecar process.");
                }

                _process = process;
                _writer = process.StandardInput;
                _ = RunReadLoopAsync(process, process.StandardOutput, readyTcs, lifetimeCts.Token);
                _ = RunErrorLoopAsync(process.StandardError, lifetimeCts.Token);

                await readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                await RestoreSubscriptionsAsync(cancellationToken).ConfigureAwait(false);
                if (startingState != MarketSidecarState.Reconnecting || !HasDesiredSubscriptions())
                {
                    SetState(MarketSidecarState.Connected);
                }

            }
            catch
            {
                StopProcess(process);
                ResetProcessState(process);
                SetState(Volatile.Read(ref _stopRequested) != 0
                    ? MarketSidecarState.Stopped
                    : MarketSidecarState.Reconnecting);
                throw;
            }
            finally
            {
                _startLock.Release();
            }
        }

        public async Task<FeedMarketIdentity[]> GetMarketsAsync(string venueId, bool forceRefresh = false)
        {
            var response = await SendRequestAsync(new
            {
                type = "getMarkets",
                venueId,
                forceRefresh
            }).ConfigureAwait(false);

            if (!response.TryGetProperty("markets", out var marketsElement) || marketsElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var markets = new List<FeedMarketIdentity>();
            foreach (var item in marketsElement.EnumerateArray())
            {
                if (MarketFeedProtocolParser.TryParseMarket(item, out var market))
                {
                    markets.Add(market!);
                }
            }

            return [.. markets];
        }

        public async Task SetSubscriptionsAsync(IEnumerable<MarketSidecarSubscription> subscriptions)
        {
            await _subscriptionUpdateLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await SetSubscriptionsCoreAsync(subscriptions).ConfigureAwait(false);
            }
            finally
            {
                _subscriptionUpdateLock.Release();
            }
        }

        private async Task SetSubscriptionsCoreAsync(IEnumerable<MarketSidecarSubscription> subscriptions)
        {
            var normalizedSubscriptions = subscriptions
                .Where(static item => !string.IsNullOrWhiteSpace(item.VenueId)
                                      && !string.IsNullOrWhiteSpace(item.Symbol)
                                      && string.Equals(item.Kind, MarketFeedProtocol.SpotMarketKind, StringComparison.OrdinalIgnoreCase))
                .Select(static item => new MarketSidecarSubscription
                {
                    VenueId = item.VenueId.Trim().ToLowerInvariant(),
                    Symbol = item.Symbol.Trim(),
                    Kind = MarketFeedProtocol.SpotMarketKind
                })
                .GroupBy(static item => $"{item.VenueId}|{item.Kind}|{item.Symbol}", StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .OrderBy(static item => item.VenueId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.Kind, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.Symbol, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            bool unchanged;
            lock (_subscriptionSync)
            {
                unchanged = SubscriptionsEqual(_desiredSubscriptions, normalizedSubscriptions);
                _desiredSubscriptions = normalizedSubscriptions;
            }

            _reconnectingStreams.Clear();
            if (normalizedSubscriptions.Length == 0)
            {
                CancelReconnect();
                if (!IsRunning())
                {
                    SetState(MarketSidecarState.Stopped);
                    return;
                }
            }
            else if (unchanged && IsRunning())
            {
                return;
            }

            if (normalizedSubscriptions.Length > 0 && !IsRunning())
            {
                await StartAsync().ConfigureAwait(false);
                return;
            }

            await SendSubscriptionsAsync(normalizedSubscriptions).ConfigureAwait(false);
        }

        public async Task StopAsync()
        {
            Volatile.Write(ref _stopRequested, 1);
            CancelReconnect();

            await _startLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var process = _process;
                if (process != null)
                {
                    if (IsRunning())
                    {
                        try
                        {
                            await SendRequestCoreAsync(new
                            {
                                type = "shutdown"
                            }, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch
                        {
                        }
                    }

                    StopProcess(process);
                    ResetProcessState(process);
                }

                FailPendingRequests(new OperationCanceledException("Market sidecar stopped."));
                SetState(MarketSidecarState.Stopped);
            }
            finally
            {
                _startLock.Release();
            }
        }

        private async Task<JsonElement> SendRequestAsync(object payload)
        {
            await StartAsync().ConfigureAwait(false);
            return await SendRequestCoreAsync(payload, CancellationToken.None).ConfigureAwait(false);
        }

        private async Task<JsonElement> SendRequestCoreAsync(object payload, CancellationToken cancellationToken)
        {
            var writer = _writer ?? throw new InvalidOperationException("Market sidecar writer is not available.");
            var requestId = Interlocked.Increment(ref _nextRequestId);
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests[requestId] = tcs;

            var envelope = JsonSerializer.SerializeToNode(payload, JsonOptions)?.AsObject() ?? [];
            envelope["id"] = requestId;
            envelope["protocolVersion"] = MarketFeedProtocol.CurrentVersion;
            var message = envelope.ToJsonString(JsonOptions);

            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await writer.WriteLineAsync(message).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
            catch
            {
                _pendingRequests.TryRemove(requestId, out _);
                throw;
            }
            finally
            {
                _writeLock.Release();
            }

            try
            {
                return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _pendingRequests.TryRemove(requestId, out _);
                throw;
            }
        }

        private Task SendSubscriptionsAsync(MarketSidecarSubscription[] subscriptions)
        {
            var markets = subscriptions.Select(static item => new
            {
                venueId = item.VenueId,
                symbol = item.Symbol,
                kind = item.Kind
            }).ToArray();

            return SendRequestAsync(new
            {
                type = "setSubscriptions",
                markets
            });
        }

        private async Task RestoreSubscriptionsAsync(CancellationToken cancellationToken)
        {
            MarketSidecarSubscription[] subscriptions;
            lock (_subscriptionSync)
            {
                subscriptions = _desiredSubscriptions;
            }

            if (subscriptions.Length == 0)
            {
                return;
            }

            var markets = subscriptions.Select(static item => new
            {
                venueId = item.VenueId,
                symbol = item.Symbol,
                kind = item.Kind
            }).ToArray();
            await SendRequestCoreAsync(new
            {
                type = "setSubscriptions",
                markets
            }, cancellationToken).ConfigureAwait(false);
        }

        private bool HasDesiredSubscriptions()
        {
            lock (_subscriptionSync)
            {
                return _desiredSubscriptions.Length > 0;
            }
        }

        private static bool SubscriptionsEqual(
            IReadOnlyList<MarketSidecarSubscription> left,
            IReadOnlyList<MarketSidecarSubscription> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (var index = 0; index < left.Count; index++)
            {
                if (!string.Equals(left[index].VenueId, right[index].VenueId, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(left[index].Symbol, right[index].Symbol, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(left[index].Kind, right[index].Kind, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private async Task RunReadLoopAsync(
            Process process,
            StreamReader reader,
            TaskCompletionSource<bool> readyTcs,
            CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line == null)
                    {
                        break;
                    }

                    if (!ReferenceEquals(process, _process))
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;

                    if (!MarketFeedProtocolParser.HasCurrentVersion(root))
                    {
                        throw new InvalidDataException("Market sidecar protocol version is missing or unsupported.");
                    }

                    if (!root.TryGetProperty("type", out var typeElement))
                    {
                        continue;
                    }

                    var type = typeElement.GetString() ?? string.Empty;
                    if (string.Equals(type, "ready", StringComparison.OrdinalIgnoreCase))
                    {
                        readyTcs.TrySetResult(true);
                        continue;
                    }

                    if (string.Equals(type, "priceUpdate", StringComparison.OrdinalIgnoreCase))
                    {
                        if (MarketFeedProtocolParser.TryParsePriceUpdate(root, out var update))
                        {
                            _reconnectingStreams.TryRemove(
                                BuildStreamKey(update!.Market.Venue.Id, update.Market.Symbol),
                                out _);
                            _reconnectingStreams.TryRemove(update.Market.Venue.Id, out _);
                            if (_reconnectingStreams.IsEmpty && State == MarketSidecarState.Reconnecting && IsRunning())
                            {
                                SetState(MarketSidecarState.Connected);
                            }

                            PriceUpdated?.Invoke(this, new MarketPriceUpdatedEventArgs(update));
                        }

                        continue;
                    }

                    if (string.Equals(type, "reconnecting", StringComparison.OrdinalIgnoreCase))
                    {
                        var venueId = root.TryGetProperty("venueId", out var venueElement)
                            ? venueElement.GetString() ?? string.Empty
                            : string.Empty;
                        var symbol = root.TryGetProperty("symbol", out var symbolElement)
                            ? symbolElement.GetString() ?? string.Empty
                            : string.Empty;
                        if (!string.IsNullOrWhiteSpace(venueId))
                        {
                            _reconnectingStreams[BuildStreamKey(venueId, symbol)] = 0;
                            SetState(MarketSidecarState.Reconnecting);
                        }

                        continue;
                    }

                    if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase)
                        && !root.TryGetProperty("id", out _))
                    {
                        var venueId = root.TryGetProperty("venueId", out var venueElement)
                            ? venueElement.GetString() ?? string.Empty
                            : string.Empty;
                        var message = root.TryGetProperty("message", out var messageElement)
                            ? messageElement.GetString() ?? "Market stream unavailable."
                            : "Market stream unavailable.";
                        StreamError?.Invoke(this, new MarketStreamErrorEventArgs(venueId, message));
                        continue;
                    }

                    if (!root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var requestId))
                    {
                        continue;
                    }

                    if (!_pendingRequests.TryRemove(requestId, out var pending))
                    {
                        continue;
                    }

                    if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
                    {
                        var message = root.TryGetProperty("message", out var messageElement)
                            ? messageElement.GetString() ?? "Unknown sidecar error."
                            : "Unknown sidecar error.";
                        pending.TrySetException(new InvalidOperationException(message));
                        continue;
                    }

                    pending.TrySetResult(root.Clone());
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                readyTcs.TrySetException(ex);
                if (ReferenceEquals(process, _process))
                {
                    FailPendingRequests(ex);
                }
            }
        }

        private async Task RunErrorLoopAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line == null)
                    {
                        break;
                    }

                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void OnSidecarExited(object? sender, EventArgs e)
        {
            if (sender is not Process process || !ReferenceEquals(process, _process))
            {
                return;
            }

            var exitCode = GetExitCode(process);
            var exception = new InvalidOperationException($"Market sidecar exited unexpectedly with code {exitCode}.");
            _readyTcs?.TrySetException(exception);
            FailPendingRequests(exception);
            ResetProcessState(process);

            if (Volatile.Read(ref _stopRequested) != 0 || !HasDesiredSubscriptions())
            {
                SetState(MarketSidecarState.Stopped);
                return;
            }

            SetState(MarketSidecarState.Reconnecting);
            ScheduleReconnect();
        }

        private void ScheduleReconnect()
        {
            if (Volatile.Read(ref _stopRequested) != 0 || !HasDesiredSubscriptions())
            {
                return;
            }

            lock (_reconnectSync)
            {
                if (_reconnectTask is { IsCompleted: false })
                {
                    return;
                }

                _reconnectCts?.Dispose();
                _reconnectCts = new CancellationTokenSource();
                _reconnectTask = ReconnectLoopAsync(_reconnectCts.Token);
            }
        }

        private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
        {
            var failedAttempt = 0;
            while (!cancellationToken.IsCancellationRequested
                   && Volatile.Read(ref _stopRequested) == 0
                   && HasDesiredSubscriptions())
            {
                SetState(MarketSidecarState.Reconnecting);
                try
                {
                    await Task.Delay(MarketReconnectRules.GetDelay(failedAttempt), cancellationToken).ConfigureAwait(false);
                    await StartCoreAsync(MarketSidecarState.Reconnecting, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    failedAttempt++;
                }
            }
        }

        private void CancelReconnect()
        {
            lock (_reconnectSync)
            {
                _reconnectCts?.Cancel();
            }
        }

        private void FailPendingRequests(Exception exception)
        {
            foreach (var pair in _pendingRequests.ToArray())
            {
                if (_pendingRequests.TryRemove(pair.Key, out var pending))
                {
                    pending.TrySetException(exception);
                }
            }
        }

        private void ResetProcessState(Process? expectedProcess = null)
        {
            if (expectedProcess != null && !ReferenceEquals(expectedProcess, _process))
            {
                try
                {
                    expectedProcess.Exited -= OnSidecarExited;
                    expectedProcess.Dispose();
                }
                catch
                {
                }

                return;
            }

            _lifetimeCts?.Cancel();
            _reconnectingStreams.Clear();
            _lifetimeCts?.Dispose();
            _lifetimeCts = null;

            if (_process != null)
            {
                _process.Exited -= OnSidecarExited;
                _process.Dispose();
                _process = null;
            }

            _writer?.Dispose();
            _writer = null;
            _readyTcs = null;
        }

        private bool IsRunning()
        {
            try
            {
                return _process != null && !_process.HasExited && _writer != null;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
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
                }
            }
            catch
            {
            }
        }

        private static int GetExitCode(Process process)
        {
            try
            {
                return process.HasExited ? process.ExitCode : -1;
            }
            catch
            {
                return -1;
            }
        }

        private static string BuildStreamKey(string venueId, string symbol)
        {
            return string.IsNullOrWhiteSpace(symbol)
                ? venueId
                : $"{venueId}|{symbol}";
        }

        private void SetState(MarketSidecarState state)
        {
            var previous = Interlocked.Exchange(ref _state, (int)state);
            if (previous != (int)state)
            {
                StateChanged?.Invoke(this, new MarketSidecarStateChangedEventArgs(state));
            }
        }

        private static string ResolveNodePath()
        {
            return Path.Combine(AppContext.BaseDirectory, "SidecarRuntime", "node.exe");
        }

        private static string ResolveScriptPath()
        {
            return Path.Combine(AppContext.BaseDirectory, "SidecarApp", "dist", "sidecar.bundle.cjs");
        }
    }
}

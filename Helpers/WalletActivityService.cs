using TrenchHQ.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TrenchHQ.Helpers
{
    internal sealed class WalletActivityService
    {
        private const int MaximumCachedActivities = 500;

        private readonly OnChainProviderConfigurationService _providers;
        private readonly SemaphoreSlim _refreshLock = new(1, 1);
        private readonly object _stateLock = new();
        private readonly Dictionary<string, WalletStreamState> _states = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ulong> _evmCheckpoints = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _solanaCheckpoints = new(StringComparer.Ordinal);
        private readonly Dictionary<string, WalletActivityUpdate> _cache = new(StringComparer.Ordinal);
        private readonly LinkedList<string> _cacheOrder = [];
        private CancellationTokenSource? _runCts;
        private Task[] _workers = [];
        private string _activeSignature = string.Empty;

        internal WalletActivityService(OnChainProviderConfigurationService providers)
        {
            _providers = providers;
        }

        internal event EventHandler<WalletActivityChangedEventArgs>? ActivityChanged;
        internal event EventHandler<WalletStreamStateChangedEventArgs>? StateChanged;

        internal async Task RefreshAsync(
            IEnumerable<SavedTrackedWallet> requestedWallets,
            CancellationToken cancellationToken = default)
        {
            await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var wallets = requestedWallets
                    .Select(CloneAndNormalize)
                    .OfType<SavedTrackedWallet>()
                    .GroupBy(SavedWidgetCatalogRules.GetWalletKey, StringComparer.OrdinalIgnoreCase)
                    .Select(static group => group.First())
                    .ToArray();
                var plans = new List<NetworkPlan>();
                foreach (var group in wallets.GroupBy(static wallet =>
                             WalletActivityRules.GetNetworkKey(wallet.ChainNamespace, wallet.ChainId)))
                {
                    var first = group.First();
                    var configuration = _providers.GetSelectedConfiguration(first.ChainNamespace, first.ChainId);
                    if (configuration == null)
                    {
                        SetState(first.ChainNamespace, first.ChainId, WalletStreamState.Unavailable,
                            "No active API connection is configured for this chain.");
                        continue;
                    }
                    var preset = OnChainProviderCatalog.Get(configuration.ProviderType);
                    if (first.ChainNamespace == ChainNamespaces.Solana
                        && preset.StreamTransport != OnChainStreamTransport.SolanaWebSocket)
                    {
                        SetState(first.ChainNamespace, first.ChainId, WalletStreamState.Unavailable,
                            "Wallet Watcher V1 requires a standard Solana WebSocket API connection.");
                        continue;
                    }
                    try
                    {
                        var apiKey = await _providers.GetCredentialAsync(configuration).ConfigureAwait(false);
                        plans.Add(new NetworkPlan(configuration, apiKey, group.ToArray()));
                    }
                    catch (Exception exception)
                    {
                        SetState(first.ChainNamespace, first.ChainId, WalletStreamState.Unavailable, exception.Message);
                        await _providers.TryFailoverAsync(
                                configuration.Id,
                                OnChainProviderFailureKind.Authentication)
                            .ConfigureAwait(false);
                    }
                }

                var signature = BuildSignature(plans);
                if (string.Equals(signature, _activeSignature, StringComparison.Ordinal))
                {
                    return;
                }

                await StopWorkersAsync().ConfigureAwait(false);
                _activeSignature = signature;
                if (plans.Count == 0)
                {
                    return;
                }

                _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _workers = plans
                    .Select(plan => Task.Run(() => RunNetworkAsync(plan, _runCts.Token), _runCts.Token))
                    .ToArray();
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        internal async Task StopAsync()
        {
            await _refreshLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopWorkersAsync().ConfigureAwait(false);
                _activeSignature = string.Empty;
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        internal WalletActivityUpdate[] GetRecent(
            IEnumerable<SavedTrackedWallet> wallets,
            int maximumCount = 50)
        {
            var walletLabels = wallets
                .GroupBy(SavedWidgetCatalogRules.GetWalletKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.First().Label,
                    StringComparer.OrdinalIgnoreCase);
            lock (_stateLock)
            {
                return _cacheOrder
                    .Select(id => _cache[id])
                    .Select(update => new
                    {
                        Update = update,
                        Key = WalletActivityRules.GetNetworkKey(update.ChainNamespace, update.ChainId)
                              + "|" + NormalizeAddress(update)
                    })
                    .Where(item => walletLabels.ContainsKey(item.Key))
                    .Take(maximumCount)
                    .Select(item =>
                    {
                        var clone = Clone(item.Update);
                        clone.WalletLabel = walletLabels[item.Key];
                        return clone;
                    })
                    .ToArray();
            }
        }

        internal WalletStreamState GetStateFor(IEnumerable<SavedTrackedWallet> wallets)
        {
            var networkKeys = wallets
                .Select(wallet => WalletActivityRules.GetNetworkKey(wallet.ChainNamespace, wallet.ChainId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (networkKeys.Length == 0)
            {
                return WalletStreamState.Disconnected;
            }
            lock (_stateLock)
            {
                var states = networkKeys
                    .Select(key => _states.TryGetValue(key, out var state) ? state : WalletStreamState.Connecting)
                    .ToArray();
                if (states.Any(static state => state == WalletStreamState.Reconnecting))
                {
                    return WalletStreamState.Reconnecting;
                }
                if (states.Any(static state => state == WalletStreamState.Connecting))
                {
                    return WalletStreamState.Connecting;
                }
                if (states.Any(static state => state == WalletStreamState.Unavailable))
                {
                    return WalletStreamState.Unavailable;
                }
                return states.All(static state => state == WalletStreamState.Live)
                    ? WalletStreamState.Live
                    : WalletStreamState.Disconnected;
            }
        }

        internal string? GetErrorFor(IEnumerable<SavedTrackedWallet> wallets)
        {
            var networkKeys = wallets
                .Select(wallet => WalletActivityRules.GetNetworkKey(wallet.ChainNamespace, wallet.ChainId))
                .Distinct(StringComparer.Ordinal);
            lock (_stateLock)
            {
                return networkKeys.Select(key => _errors.TryGetValue(key, out var error) ? error : null)
                    .FirstOrDefault(static error => !string.IsNullOrWhiteSpace(error));
            }
        }

        private async Task RunNetworkAsync(NetworkPlan plan, CancellationToken cancellationToken)
        {
            var attempt = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                SetState(
                    plan.Configuration.ChainNamespace,
                    plan.Configuration.ChainId,
                    attempt == 0 ? WalletStreamState.Connecting : WalletStreamState.Reconnecting);
                try
                {
                    if (plan.Configuration.ChainNamespace == ChainNamespaces.Solana)
                    {
                        var checkpoints = GetSolanaCheckpoints();
                        var source = new SolanaWalletActivityStreamSource(plan.Configuration, plan.ApiKey);
                        await source.RunAsync(
                                plan.Wallets,
                                checkpoints,
                                Publish,
                                SetSolanaCheckpoint,
                                () => SetState(
                                    plan.Configuration.ChainNamespace,
                                    plan.Configuration.ChainId,
                                    WalletStreamState.Live,
                                    "Watching wallet, SPL Token, and Token-2022 activity with confirmation tracking."),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        var networkKey = WalletActivityRules.GetNetworkKey(
                            plan.Configuration.ChainNamespace,
                            plan.Configuration.ChainId);
                        ulong? checkpoint;
                        lock (_stateLock)
                        {
                            checkpoint = _evmCheckpoints.TryGetValue(networkKey, out var value) ? value : null;
                        }
                        var source = new EvmWalletActivityStreamSource(plan.Configuration, plan.ApiKey);
                        await source.RunAsync(
                                plan.Wallets,
                                checkpoint,
                                Publish,
                                block => SetEvmCheckpoint(networkKey, block),
                                internalTransfers => SetState(
                                    plan.Configuration.ChainNamespace,
                                    plan.Configuration.ChainId,
                                    WalletStreamState.Live,
                                    internalTransfers
                                        ? "Watching native, internal, ERC-20, ERC-721, and ERC-1155 activity."
                                        : "Watching native, ERC-20, ERC-721, and ERC-1155 activity. The active API does not expose internal traces."),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    attempt = 0;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (OnChainUsageBudgetException)
                {
                    break;
                }
                catch (Exception exception)
                {
                    attempt++;
                    SetState(
                        plan.Configuration.ChainNamespace,
                        plan.Configuration.ChainId,
                        WalletStreamState.Reconnecting,
                        SanitizeError(exception));
                    var failureKind = ClassifyProviderFailure(exception);
                    var shouldFailover = failureKind == OnChainProviderFailureKind.Authentication
                                         || (failureKind == OnChainProviderFailureKind.RateLimited && attempt >= 2)
                                         || attempt >= 3;
                    if (shouldFailover)
                    {
                        var outcome = await _providers.TryFailoverAsync(
                                plan.Configuration.Id,
                                failureKind)
                            .ConfigureAwait(false);
                        if (outcome != OnChainProviderFailoverOutcome.Ignored)
                        {
                            break;
                        }
                    }
                    var delay = MarketReconnectRules.GetDelay(attempt - 1);
                    try
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            SetState(plan.Configuration.ChainNamespace, plan.Configuration.ChainId, WalletStreamState.Disconnected);
        }

        private void Publish(WalletActivityUpdate update)
        {
            var changed = false;
            lock (_stateLock)
            {
                if (update.Removed)
                {
                    if (_cache.Remove(update.EventId))
                    {
                        _cacheOrder.Remove(update.EventId);
                        changed = true;
                    }
                }
                else if (_cache.TryGetValue(update.EventId, out var existing))
                {
                    if (WalletActivityRules.IsConfirmationUpgrade(existing.Confirmation, update.Confirmation))
                    {
                        var replacement = Clone(update);
                        replacement.ObservedAtUnixMs = Math.Min(
                            existing.ObservedAtUnixMs,
                            replacement.ObservedAtUnixMs);
                        _cache[update.EventId] = replacement;
                        changed = true;
                    }
                }
                else
                {
                    _cache[update.EventId] = Clone(update);
                    _cacheOrder.AddFirst(update.EventId);
                    while (_cacheOrder.Count > MaximumCachedActivities)
                    {
                        var last = _cacheOrder.Last!.Value;
                        _cacheOrder.RemoveLast();
                        _cache.Remove(last);
                    }
                    changed = true;
                }
            }
            if (changed)
            {
                ActivityChanged?.Invoke(this, new WalletActivityChangedEventArgs(Clone(update)));
            }
        }

        private void SetState(
            string chainNamespace,
            string chainId,
            WalletStreamState state,
            string? error = null)
        {
            var key = WalletActivityRules.GetNetworkKey(chainNamespace, chainId);
            var changed = false;
            lock (_stateLock)
            {
                changed = !_states.TryGetValue(key, out var previous) || previous != state;
                _states[key] = state;
                if (string.IsNullOrWhiteSpace(error))
                {
                    _errors.Remove(key);
                }
                else
                {
                    changed |= !_errors.TryGetValue(key, out var previousError)
                               || !string.Equals(previousError, error, StringComparison.Ordinal);
                    _errors[key] = error;
                }
            }
            if (changed)
            {
                StateChanged?.Invoke(this, new WalletStreamStateChangedEventArgs(
                    chainNamespace,
                    chainId,
                    state,
                    error));
            }
        }

        private void SetEvmCheckpoint(string networkKey, ulong block)
        {
            lock (_stateLock)
            {
                if (!_evmCheckpoints.TryGetValue(networkKey, out var current) || block > current)
                {
                    _evmCheckpoints[networkKey] = block;
                }
            }
        }

        private void SetSolanaCheckpoint(string address, string signature)
        {
            lock (_stateLock)
            {
                _solanaCheckpoints[address] = signature;
            }
        }

        private IReadOnlyDictionary<string, string> GetSolanaCheckpoints()
        {
            lock (_stateLock)
            {
                return new Dictionary<string, string>(_solanaCheckpoints, StringComparer.Ordinal);
            }
        }

        private async Task StopWorkersAsync()
        {
            if (_runCts == null)
            {
                return;
            }
            _runCts.Cancel();
            try
            {
                await Task.WhenAll(_workers).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
            _workers = [];
            _runCts.Dispose();
            _runCts = null;
        }

        private static string BuildSignature(IEnumerable<NetworkPlan> plans)
        {
            return string.Join("\n", plans
                .OrderBy(static plan => plan.Configuration.ChainNamespace, StringComparer.Ordinal)
                .ThenBy(static plan => plan.Configuration.ChainId, StringComparer.Ordinal)
                .Select(plan => $"{plan.Configuration.Id}|{plan.Configuration.StreamEndpoint}|{plan.Configuration.RpcEndpoint}|"
                                + string.Join(",", plan.Wallets
                                    .OrderBy(SavedWidgetCatalogRules.GetWalletKey, StringComparer.OrdinalIgnoreCase)
                                    .Select(SavedWidgetCatalogRules.GetWalletKey))));
        }

        private static SavedTrackedWallet? CloneAndNormalize(SavedTrackedWallet wallet)
        {
            return SavedWidgetCatalogRules.NormalizeWallet(new SavedTrackedWallet
            {
                ChainNamespace = wallet.ChainNamespace,
                ChainId = wallet.ChainId,
                Address = wallet.Address,
                Label = wallet.Label
            });
        }

        private static string NormalizeAddress(WalletActivityUpdate update)
        {
            return update.ChainNamespace == ChainNamespaces.Eip155
                ? update.WalletAddress.ToLowerInvariant()
                : update.WalletAddress;
        }

        private static string SanitizeError(Exception exception)
        {
            return exception is InvalidOperationException { Message: var message }
                   && message.StartsWith("EVM wallet subscription failed:", StringComparison.Ordinal)
                ? "The active EVM API rejected a wallet subscription. Retrying."
                : exception is InvalidOperationException { Message: var solanaMessage }
                  && solanaMessage.StartsWith("Solana wallet subscription failed:", StringComparison.Ordinal)
                    ? "The active Solana API rejected a wallet subscription. Retrying."
                    : "The wallet stream disconnected. Retrying.";
        }

        private static OnChainProviderFailureKind ClassifyProviderFailure(Exception exception)
        {
            if (exception is OnChainProviderResponseException response) return response.Kind;
            if (exception is EvmJsonRpcException { Kind: EvmRpcFailureKind.AuthenticationRejected }
                or SolanaRpcException { IsAccessRejected: true }) return OnChainProviderFailureKind.Authentication;
            if (exception is EvmJsonRpcException { Kind: EvmRpcFailureKind.RateLimited }
                or SolanaRpcException { IsRateLimited: true }) return OnChainProviderFailureKind.RateLimited;
            var message = exception.Message ?? string.Empty;
            if (message.Contains("401", StringComparison.OrdinalIgnoreCase)
                || message.Contains("403", StringComparison.OrdinalIgnoreCase)
                || message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
                || message.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
                || message.Contains("authentication", StringComparison.OrdinalIgnoreCase)
                || message.Contains("API key", StringComparison.OrdinalIgnoreCase))
            {
                return OnChainProviderFailureKind.Authentication;
            }
            if (message.Contains("402", StringComparison.OrdinalIgnoreCase)
                || message.Contains("429", StringComparison.OrdinalIgnoreCase)
                || message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                || message.Contains("resource exhausted", StringComparison.OrdinalIgnoreCase))
            {
                return OnChainProviderFailureKind.RateLimited;
            }
            return OnChainProviderFailureKind.Transport;
        }

        private static WalletActivityUpdate Clone(WalletActivityUpdate source)
        {
            return new WalletActivityUpdate
            {
                EventId = source.EventId,
                ChainNamespace = source.ChainNamespace,
                ChainId = source.ChainId,
                WalletAddress = source.WalletAddress,
                WalletLabel = source.WalletLabel,
                TransactionId = source.TransactionId,
                ChainPosition = source.ChainPosition,
                Kind = source.Kind,
                Direction = source.Direction,
                Counterparty = source.Counterparty,
                VenueId = source.VenueId,
                AssetAddress = source.AssetAddress,
                AssetId = source.AssetId,
                AmountRaw = source.AmountRaw,
                AssetSymbol = source.AssetSymbol,
                AssetDecimals = source.AssetDecimals,
                Failed = source.Failed,
                Removed = source.Removed,
                Confirmation = source.Confirmation,
                ObservedAtUnixMs = source.ObservedAtUnixMs,
                SourceId = source.SourceId
            };
        }

        private sealed record NetworkPlan(
            OnChainProviderConfiguration Configuration,
            string ApiKey,
            SavedTrackedWallet[] Wallets);
    }
}

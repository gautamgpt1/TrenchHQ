using TrenchHQ.Core.Markets;
using TrenchHQ.Core.OnChain;
using TrenchHQ.Core.Providers;
using TrenchHQ.Core.Wallets;
using TrenchHQ.Core.Widgets;
using TrenchHQ.Infrastructure.OnChain.Evm;
using TrenchHQ.Infrastructure.OnChain;
using TrenchHQ.Infrastructure.OnChain.Solana;
using TrenchHQ.Infrastructure.Providers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TrenchHQ.Infrastructure.Wallets
{
    internal sealed class WalletActivityService
    {
        private const int MaximumCachedActivities = 500;

        private readonly OnChainProviderConfigurationService _providers;
        private readonly SemaphoreSlim _refreshLock = new(1, 1);
        private readonly object _stateLock = new();
        private readonly Dictionary<string, WalletStreamState> _states = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);
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
                var unavailable = new List<(string ChainNamespace, string ChainId, string Reason)>();
                foreach (var group in wallets.GroupBy(static wallet =>
                             WalletActivityRules.GetNetworkKey(wallet.ChainNamespace, wallet.ChainId)))
                {
                    var first = group.First();
                    var configuration = _providers.GetSelectedConfiguration(first.ChainNamespace, first.ChainId);
                    if (configuration == null)
                    {
                        var reason = GetErrorFor(group)
                                     ?? "No compatible API connection is available. Check API Connections.";
                        SetState(first.ChainNamespace, first.ChainId, WalletStreamState.Unavailable,
                            reason);
                        unavailable.Add((first.ChainNamespace, first.ChainId, reason));
                        continue;
                    }
                    var preset = OnChainProviderCatalog.Get(configuration.ProviderType);
                    if (first.ChainNamespace == ChainNamespaces.Solana
                        && preset.StreamTransport != OnChainStreamTransport.SolanaWebSocket)
                    {
                        const string reason = "Wallet Watcher V1 requires a standard Solana WebSocket API connection.";
                        SetState(first.ChainNamespace, first.ChainId, WalletStreamState.Unavailable,
                            reason);
                        unavailable.Add((first.ChainNamespace, first.ChainId, reason));
                        continue;
                    }
                    try
                    {
                        var apiKey = await _providers.GetCredentialAsync(configuration).ConfigureAwait(false);
                        plans.Add(new NetworkPlan(configuration, apiKey, group.ToArray()));
                    }
                    catch (Exception)
                    {
                        const string reason = "The saved API credential could not be read. Update API Connections.";
                        SetState(first.ChainNamespace, first.ChainId, WalletStreamState.Unavailable,
                            reason);
                        unavailable.Add((first.ChainNamespace, first.ChainId, reason));
                        await _providers.TryFailoverAsync(
                                configuration.Id,
                                OnChainProviderFailureKind.Authentication)
                            .ConfigureAwait(false);
                    }
                }

                var signature = BuildSignature(plans);
                if (string.Equals(signature, _activeSignature, StringComparison.Ordinal)
                    && _workers.All(worker => !worker.IsCompleted))
                {
                    return;
                }

                await StopWorkersAsync().ConfigureAwait(false);
                foreach (var item in unavailable)
                    SetState(item.ChainNamespace, item.ChainId, WalletStreamState.Unavailable, item.Reason);
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
                    .OrderByDescending(item => item.Update.ObservedAtUnixMs)
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
            string? lastError = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                SetState(
                    plan.Configuration.ChainNamespace,
                    plan.Configuration.ChainId,
                    attempt == 0 ? WalletStreamState.Connecting : WalletStreamState.Reconnecting,
                    attempt == 0 ? null : lastError);
                try
                {
                    if (plan.Configuration.ChainNamespace == ChainNamespaces.Solana)
                    {
                        var source = new SolanaWalletActivityStreamSource(plan.Configuration, plan.ApiKey);
                        await source.RunAsync(
                                plan.Wallets,
                                Publish,
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
                        var source = new EvmWalletActivityStreamSource(plan.Configuration, plan.ApiKey);
                        await source.RunAsync(
                                plan.Wallets,
                                Publish,
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
                    lastError = SanitizeError(exception);
                    SetState(
                        plan.Configuration.ChainNamespace,
                        plan.Configuration.ChainId,
                        WalletStreamState.Reconnecting,
                        lastError);
                    var failureKind = ClassifyProviderFailure(exception);
                    var shouldFailover = failureKind == OnChainProviderFailureKind.Authentication
                                         || failureKind == OnChainProviderFailureKind.RateLimited
                                         || attempt >= 3;
                    if (shouldFailover)
                    {
                        var outcome = await _providers.TryFailoverAsync(
                                plan.Configuration.Id,
                                failureKind, OnChainRequestRetry.RetryAfter(exception))
                            .ConfigureAwait(false);
                        if (outcome != OnChainProviderFailoverOutcome.Ignored)
                        {
                            break;
                        }
                    }
                    var delay = OnChainRequestRetry.RetryAfter(exception) ?? MarketReconnectRules.GetDelay(attempt - 1);
                    if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
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
            SetState(plan.Configuration.ChainNamespace, plan.Configuration.ChainId,
                cancellationToken.IsCancellationRequested ? WalletStreamState.Disconnected : WalletStreamState.Unavailable,
                cancellationToken.IsCancellationRequested ? null : lastError);
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
                    if (WalletActivityRules.IsConfirmationUpgrade(existing.Confirmation, update.Confirmation)
                        || update.Confirmation == existing.Confirmation
                        && (existing.AssetDecimals != update.AssetDecimals || existing.AssetSymbol != update.AssetSymbol))
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
            var failureKind = ClassifyProviderFailure(exception);
            if (failureKind == OnChainProviderFailureKind.Authentication)
                return "The API connection rejected its credential. Update API Connections.";
            if (failureKind == OnChainProviderFailureKind.RateLimited)
                return "The API connection is rate limited. Retrying automatically.";
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

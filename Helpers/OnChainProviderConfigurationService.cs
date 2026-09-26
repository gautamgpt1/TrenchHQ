using TrenchHQ.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;
using Windows.Storage;

namespace TrenchHQ.Helpers
{
    internal sealed class OnChainProviderConfigurationService
    {
        private OnChainProviderConfigurationDocument _document = new();
        private readonly object _runtimeSync = new();
        private readonly Dictionary<string, string> _runtimeConfigurationIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _failedConfigurationIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _unavailableNetworkKeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _accessRejectedConfigurationIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CancellationTokenSource> _routeRetries = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _retryRounds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Func<bool> _hasInternetAccess;
        private readonly TimeSpan _routeRetryDelay;
        private bool _initialized;
        private readonly Dictionary<string, (bool Events, bool Wallet)> _demands = new(StringComparer.OrdinalIgnoreCase);
        internal OnChainProviderUsage Usage { get; private set; }

        internal OnChainProviderConfigurationService(
            OnChainProviderConfigurationDocument? document = null,
            Func<bool>? hasInternetAccess = null,
            TimeSpan? routeRetryDelay = null,
            OnChainProviderUsage? usage = null)
        {
            Usage = usage ?? new OnChainProviderUsage();
            Usage.AvailabilityChanged += OnUsageAvailabilityChanged;
            _hasInternetAccess = hasInternetAccess ?? HasInternetAccess;
            _routeRetryDelay = routeRetryDelay ?? TimeSpan.FromMinutes(1);
            if (document != null)
            {
                _document = document;
                ResetAllRuntimeSelectionsLocked();
                OnUsageAvailabilityChanged(this, EventArgs.Empty);
            }
        }

        internal event EventHandler? Changed;

        internal async Task InitializeAsync()
        {
            if (_initialized)
            {
                return;
            }
            var folderPath = ApplicationData.Current.LocalFolder.Path;
            Usage.AvailabilityChanged -= OnUsageAvailabilityChanged;
            Usage.Dispose();
            Usage = new OnChainProviderUsage(folderPath);
            Usage.AvailabilityChanged += OnUsageAvailabilityChanged;
            var isFirstRun = !File.Exists(Path.Combine(
                folderPath,
                OnChainProviderConfigurationStore.ConfigurationsFileName));
            _document = await OnChainProviderConfigurationStore.LoadAsync(folderPath)
                .ConfigureAwait(false);
            if (isFirstRun)
            {
                _document = OnChainProviderConfigurationStore.CreateFirstRunDocument();
                await OnChainProviderConfigurationStore.SaveAsync(folderPath, _document).ConfigureAwait(false);
            }
            else
            {
                var changed = false;
                var basePublicPreset = OnChainProviderCatalog.Get(OnChainProviderTypes.BasePublic);
                var existingBasePublic = _document.Configurations.FirstOrDefault(configuration =>
                    string.Equals(configuration.ProviderType, OnChainProviderTypes.BasePublic, StringComparison.Ordinal));
                if (existingBasePublic != null)
                {
                    if (string.Equals(existingBasePublic.StreamEndpoint, "wss://mainnet.base.org", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(existingBasePublic.StreamEndpoint, "wss://base.drpc.org", StringComparison.OrdinalIgnoreCase))
                    {
                        existingBasePublic.StreamEndpoint = basePublicPreset.DefaultStreamEndpoint;
                        changed = true;
                    }
                    if (string.Equals(existingBasePublic.RpcEndpoint, "https://mainnet.base.org", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(existingBasePublic.RpcEndpoint, "https://base.drpc.org", StringComparison.OrdinalIgnoreCase))
                    {
                        existingBasePublic.RpcEndpoint = basePublicPreset.DefaultRpcEndpoint;
                        changed = true;
                    }
                }
                foreach (var providerType in new[]
                         {
                             OnChainProviderTypes.PublicNodeEthereum,
                             OnChainProviderTypes.BasePublic,
                             OnChainProviderTypes.PublicNodeBnb,
                             OnChainProviderTypes.PublicNodeRobinhood
                         })
                {
                    var preset = OnChainProviderCatalog.Get(providerType);
                    if (_document.Configurations.Any(configuration =>
                            string.Equals(configuration.ProviderType, providerType, StringComparison.Ordinal)))
                    {
                        continue;
                    }
                    var configuration = OnChainProviderConfigurationStore.CreateConfiguration(preset);
                    _document.Configurations = [.. _document.Configurations, configuration];
                    var networkKey = OnChainProviderConfigurationStore.GetNetworkKey(
                        configuration.ChainNamespace,
                        configuration.ChainId);
                    _document.SelectedConfigurationIds.TryAdd(networkKey, configuration.Id);
                    changed = true;
                }
                if (changed)
                {
                    await OnChainProviderConfigurationStore.SaveAsync(folderPath, _document).ConfigureAwait(false);
                }
            }
            lock (_runtimeSync)
            {
                ResetAllRuntimeSelectionsLocked();
            }
            _initialized = true;
            OnUsageAvailabilityChanged(this, EventArgs.Empty);
        }

        internal OnChainProviderConfiguration[] GetConfigurations()
        {
            return _document.Configurations.Select(Clone).ToArray();
        }

        internal OnChainProviderConfiguration? GetSelectedConfiguration()
        {
            return GetSelectedConfiguration(ChainNamespaces.Solana, "mainnet-beta");
        }

        internal OnChainProviderConfiguration? GetSelectedConfiguration(
            string chainNamespace,
            string chainId)
        {
            var networkKey = OnChainProviderConfigurationStore.GetNetworkKey(chainNamespace, chainId);
            string? selectedConfigurationId;
            lock (_runtimeSync)
            {
                if (_unavailableNetworkKeys.Contains(networkKey))
                {
                    return null;
                }
                _runtimeConfigurationIds.TryGetValue(networkKey, out selectedConfigurationId);
            }
            var configuration = _document.Configurations.FirstOrDefault(item =>
                string.Equals(item.Id, selectedConfigurationId, StringComparison.OrdinalIgnoreCase));
            return configuration == null ? null : Clone(configuration);
        }

        // Changing demand only selects metadata. Consumers own all network I/O.
        internal void SetDemand(string chainNamespace, string chainId, bool events, bool wallet)
        {
            lock (_runtimeSync)
                _demands[OnChainProviderConfigurationStore.GetNetworkKey(chainNamespace, chainId)] = (events, wallet);
            ReconcileUsageBudgets(configurationChanged: false);
        }

        internal string GetRouteStatus(OnChainProviderConfiguration configuration)
        {
            lock (_runtimeSync)
            {
                if (_accessRejectedConfigurationIds.Contains(configuration.Id)) return "Credential rejected \u00b7 update key";
                if (Usage.IsBlocked(configuration)) return "Usage / quota paused";
                if (_failedConfigurationIds.Values.Any(ids => ids.Contains(configuration.Id))) return "Cooling down \u00b7 retries automatically";
                if (!IsCompatible(configuration)) return "Unavailable for current mode";
                return _runtimeConfigurationIds.Values.Contains(configuration.Id) ? "Active \u00b7 automatic" : "Ready \u00b7 automatic";
            }
        }

        internal async Task UpsertAsync(
            OnChainProviderConfiguration configuration,
            string? replacementCredential,
            string? reuseCredentialReference = null)
        {
            if (!OnChainProviderCatalog.IsSupported(configuration.ProviderType))
            {
                throw new ArgumentException("Provider type is not supported.");
            }
            var preset = OnChainProviderCatalog.Get(configuration.ProviderType);
            if (!string.Equals(configuration.ChainNamespace, preset.ChainNamespace, StringComparison.Ordinal)
                || !string.Equals(configuration.ChainId, preset.ChainId, StringComparison.Ordinal))
            {
                throw new ArgumentException("Provider chain/network does not match its preset.");
            }
            if (!OnChainProviderConfigurationStore.IsSecureStreamEndpoint(
                    configuration.StreamEndpoint,
                    preset.StreamTransport,
                    preset.StreamAuthenticationMode)
                || !OnChainProviderConfigurationStore.IsSecureEndpoint(
                    configuration.RpcEndpoint,
                    preset.RpcAuthenticationMode))
            {
                throw new ArgumentException(
                    "Use a credential-free WSS stream endpoint or HTTPS Yellowstone endpoint, plus a credential-free HTTPS RPC endpoint.");
            }
            if (!OnChainProviderConfigurationStore.HasCredentialFreePath(configuration))
            {
                throw new ArgumentException(
                    "Path-authenticated provider endpoint boxes must not contain the access token. Paste the token into the protected token field instead.");
            }
            configuration.ReplayEnabled = preset.ReplayEnabled;

            if (!string.IsNullOrWhiteSpace(replacementCredential)
                && !string.IsNullOrWhiteSpace(reuseCredentialReference))
            {
                throw new ArgumentException("Enter a replacement key or reuse a saved key, not both.");
            }
            OnChainProviderConfiguration? reuseSource = null;
            if (!string.IsNullOrWhiteSpace(reuseCredentialReference))
            {
                reuseSource = _document.Configurations.FirstOrDefault(item =>
                    string.Equals(
                        item.CredentialReference,
                        reuseCredentialReference,
                        StringComparison.OrdinalIgnoreCase));
                if (reuseSource == null
                    || !OnChainProviderConfigurationStore.CanReuseCredential(reuseSource, preset))
                {
                    throw new ArgumentException("A saved credential can only be reused by the same provider family.");
                }
                if (await OnChainProviderConfigurationStore.ReadCredentialAsync(
                        ApplicationData.Current.LocalFolder.Path,
                        reuseSource.CredentialReference).ConfigureAwait(false) == null)
                {
                    throw new ArgumentException("The saved provider API key is unavailable.");
                }
            }

            var index = Array.FindIndex(_document.Configurations, item =>
                string.Equals(item.ChainNamespace, configuration.ChainNamespace, StringComparison.Ordinal)
                && string.Equals(item.ChainId, configuration.ChainId, StringComparison.Ordinal)
                && string.Equals(item.ProviderType, configuration.ProviderType, StringComparison.Ordinal));
            var hasReplacementCredential = !string.IsNullOrWhiteSpace(replacementCredential);
            if (preset.RequiresCredential && index < 0 && !hasReplacementCredential && reuseSource == null)
            {
                throw new ArgumentException("Enter an API key to configure this provider.");
            }
            if (preset.RequiresCredential
                && index >= 0
                && !hasReplacementCredential
                && reuseSource == null
                && await OnChainProviderConfigurationStore.ReadCredentialAsync(
                    ApplicationData.Current.LocalFolder.Path,
                    _document.Configurations[index].CredentialReference).ConfigureAwait(false) == null)
            {
                throw new ArgumentException("The saved provider API key is unavailable. Enter it again.");
            }

            var previousCredentialReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sharedCredentialSource = OnChainProviderCatalog.SharesCredentialAcrossChains(preset)
                ? _document.Configurations.FirstOrDefault(item =>
                    OnChainProviderConfigurationStore.CanReuseCredential(item, preset))
                : null;
            var credentialReference = reuseSource?.CredentialReference
                                      ?? (index >= 0
                                          ? _document.Configurations[index].CredentialReference
                                          : sharedCredentialSource?.CredentialReference
                                            ?? Guid.NewGuid().ToString("N"));
            if (index >= 0)
            {
                previousCredentialReferences.Add(_document.Configurations[index].CredentialReference);
                configuration.Id = _document.Configurations[index].Id;
                configuration.CredentialReference = credentialReference;
                _document.Configurations[index] = Clone(configuration);
            }
            else
            {
                configuration.Id = Guid.NewGuid().ToString("N");
                configuration.CredentialReference = credentialReference;
                _document.Configurations = [.. _document.Configurations, Clone(configuration)];
            }

            if (hasReplacementCredential)
            {
                await OnChainProviderConfigurationStore.SaveCredentialAsync(
                    ApplicationData.Current.LocalFolder.Path,
                    configuration.CredentialReference,
                    replacementCredential!).ConfigureAwait(false);
            }

            if (OnChainProviderCatalog.SharesCredentialAcrossChains(preset))
            {
                _document.Configurations = OnChainProviderConfigurationStore.LinkSharedCredentialConfigurations(
                    _document.Configurations,
                    preset,
                    credentialReference,
                    previousCredentialReferences);
            }

            lock (_runtimeSync)
            {
                // Replacing a shared key also repairs its linked chains; unrelated rejected keys stay rejected.
                foreach (var profile in _document.Configurations.Where(item => item.Id == configuration.Id
                    || (hasReplacementCredential && item.CredentialReference == credentialReference)))
                {
                    _accessRejectedConfigurationIds.Remove(profile.Id);
                    foreach (var failed in _failedConfigurationIds.Values) failed.Remove(profile.Id);
                    if (_routeRetries.Remove(profile.Id, out var retry)) retry.Cancel();
                    _retryRounds.Remove(profile.Id);
                }
            }
            await SaveAsync().ConfigureAwait(false);
            foreach (var previousCredentialReference in previousCredentialReferences.Where(reference =>
                         !string.Equals(
                             reference,
                             configuration.CredentialReference,
                             StringComparison.OrdinalIgnoreCase)))
            {
                OnChainProviderConfigurationStore.DeleteCredentialIfUnreferenced(
                    ApplicationData.Current.LocalFolder.Path,
                    _document.Configurations,
                    previousCredentialReference);
            }
        }

        internal Task<OnChainProviderFailoverOutcome> TryFailoverAsync(
            string configurationId, OnChainProviderFailureKind kind)
        {
            if (!OnChainProviderFailoverRules.ShouldAdvanceRoute(kind, _hasInternetAccess()))
                return Task.FromResult(OnChainProviderFailoverOutcome.Ignored);
            var profile = _document.Configurations.FirstOrDefault(item => item.Id == configurationId);
            if (profile == null) return Task.FromResult(OnChainProviderFailoverOutcome.Ignored);
            var networkKey = OnChainProviderConfigurationStore.GetNetworkKey(profile.ChainNamespace, profile.ChainId);
            lock (_runtimeSync)
            {
                if (!_runtimeConfigurationIds.TryGetValue(networkKey, out var current) || current != configurationId)
                    return Task.FromResult(OnChainProviderFailoverOutcome.Ignored);
                if (!_failedConfigurationIds.TryGetValue(networkKey, out var failed))
                    _failedConfigurationIds[networkKey] = failed = new(StringComparer.OrdinalIgnoreCase);
                failed.Add(configurationId);
                if (kind == OnChainProviderFailureKind.Authentication)
                    _accessRejectedConfigurationIds.Add(configurationId);
                else
                {
                    if (_routeRetries.Remove(configurationId, out var previous)) previous.Cancel();
                    var retry = new CancellationTokenSource();
                    _routeRetries[configurationId] = retry;
                    _retryRounds.TryGetValue(configurationId, out var round);
                    _retryRounds[configurationId] = Math.Min(round + 1, 4);
                    _ = RetryRouteAsync(profile, networkKey, retry,
                        TimeSpan.FromTicks(_routeRetryDelay.Ticks * Math.Min(1 << round, 5)));
                }
            }
            ReconcileUsageBudgets(configurationChanged: false);
            return Task.FromResult(GetSelectedConfiguration(profile.ChainNamespace, profile.ChainId) == null
                ? OnChainProviderFailoverOutcome.Unavailable : OnChainProviderFailoverOutcome.Switched);
        }

        private bool IsCompatible(OnChainProviderConfiguration profile)
        {
            var preset = OnChainProviderCatalog.Get(profile.ProviderType);
            var demand = _demands.GetValueOrDefault(OnChainProviderConfigurationStore.GetNetworkKey(profile.ChainNamespace, profile.ChainId));
            if (string.IsNullOrWhiteSpace(profile.RpcEndpoint)) return false;
            if ((demand.Events || demand.Wallet) && string.IsNullOrWhiteSpace(profile.StreamEndpoint)) return false;
            if (demand.Wallet && preset.StreamTransport == OnChainStreamTransport.YellowstoneGrpc) return false;
            var caps = profile.CapabilitySnapshot;
            if (caps == null) return true; // Unknown capabilities are checked by the actual requested operation.
            if (caps.ChainId == OnChainProviderCapabilityState.Unsupported) return false;
            if ((demand.Events || demand.Wallet) && (caps.WebSocketLogs == OnChainProviderCapabilityState.Unsupported
                || (EvmWebSocketStreamSource.ShouldSubscribeToHeads(profile.ChainId)
                    && caps.WebSocketHeads == OnChainProviderCapabilityState.Unsupported))) return false;
            return preset.ProviderType == OnChainProviderTypes.InfuraEthereum
                || caps.BlockHashCall != OnChainProviderCapabilityState.Unsupported;
        }

        private void OnUsageAvailabilityChanged(object? sender, EventArgs args)
            => ReconcileUsageBudgets(configurationChanged: false);

        private void ReconcileUsageBudgets(bool configurationChanged)
        {
            var changed = false;
            lock (_runtimeSync)
            {
                var networks = _document.Configurations.GroupBy(profile =>
                    OnChainProviderConfigurationStore.GetNetworkKey(profile.ChainNamespace, profile.ChainId));
                foreach (var network in networks)
                {
                    _runtimeConfigurationIds.TryGetValue(network.Key, out var currentId);
                    _failedConfigurationIds.TryGetValue(network.Key, out var failed);
                    var eligible = network.Where(profile => !_accessRejectedConfigurationIds.Contains(profile.Id)
                        && failed?.Contains(profile.Id) != true && !Usage.IsBlocked(profile) && IsCompatible(profile))
                        .OrderBy(profile => OnChainProviderCatalog.IsPublicEvaluationProvider(profile.ProviderType))
                        .ThenByDescending(profile => Usage.Headroom(profile))
                        .ThenByDescending(profile => Usage.PollingCapacity(profile))
                        .ThenBy(profile => profile.ProviderType, StringComparer.Ordinal).ToArray();
                    var best = eligible.FirstOrDefault();
                    var current = eligible.FirstOrDefault(profile => profile.Id == currentId);
                    // Hysteresis: use a healthy private route until below 20%, and only move to
                    // at least twice its headroom. No background probes or balanced round-robin traffic.
                    if (!configurationChanged && current != null && best != null
                        && !OnChainProviderCatalog.IsPublicEvaluationProvider(current.ProviderType)
                        && (Usage.Headroom(current) > 0.2m || Usage.Headroom(best) < Usage.Headroom(current) * 2))
                        best = current;
                    if (best == null)
                    {
                        changed |= _runtimeConfigurationIds.Remove(network.Key);
                        _unavailableNetworkKeys.Add(network.Key);
                    }
                    else
                    {
                        _runtimeConfigurationIds[network.Key] = best.Id;
                        _unavailableNetworkKeys.Remove(network.Key);
                        changed |= best.Id != currentId;
                    }
                }
                foreach (var removed in _runtimeConfigurationIds.Keys.Where(key => !networks.Any(group => group.Key == key)).ToArray())
                    changed |= _runtimeConfigurationIds.Remove(removed);
            }
            if (changed) Changed?.Invoke(this, EventArgs.Empty);
        }

        private async Task RetryRouteAsync(OnChainProviderConfiguration profile,
            string networkKey, CancellationTokenSource retry, TimeSpan delay)
        {
            try
            {
                do { await Task.Delay(delay, retry.Token).ConfigureAwait(false); } while (!_hasInternetAccess());
                lock (_runtimeSync)
                {
                    if (!_routeRetries.TryGetValue(profile.Id, out var current) || current != retry || retry.IsCancellationRequested) return;
                    _routeRetries.Remove(profile.Id);
                    if (_failedConfigurationIds.TryGetValue(networkKey, out var failed)) failed.Remove(profile.Id);
                }
                ReconcileUsageBudgets(configurationChanged: false);
            }
            catch (OperationCanceledException) when (retry.IsCancellationRequested) { }
            finally { retry.Dispose(); }
        }

        private static bool HasInternetAccess()
        {
            try
            {
                return NetworkInformation.GetInternetConnectionProfile()?.GetNetworkConnectivityLevel()
                       == NetworkConnectivityLevel.InternetAccess;
            }
            catch
            {
                // If Windows cannot report connectivity, retain the established provider-failover behavior.
                return true;
            }
        }

        internal async Task<string> GetCredentialAsync(OnChainProviderConfiguration configuration)
        {
            var credential = await OnChainProviderConfigurationStore.ReadCredentialAsync(
                ApplicationData.Current.LocalFolder.Path,
                configuration.CredentialReference).ConfigureAwait(false);
            if (!OnChainProviderCatalog.Get(configuration.ProviderType).RequiresCredential)
            {
                return credential ?? string.Empty;
            }
            return string.IsNullOrWhiteSpace(credential)
                ? throw new InvalidOperationException("The provider API key is unavailable.")
                : credential;
        }

        internal async Task DeleteAsync(string configurationId)
        {
            var removed = OnChainProviderConfigurationStore.RemoveConfiguration(
                _document,
                configurationId);
            if (removed == null)
            {
                return;
            }
            OnChainProviderConfigurationStore.DeleteCredentialIfUnreferenced(
                ApplicationData.Current.LocalFolder.Path,
                _document.Configurations,
                removed.CredentialReference);
            lock (_runtimeSync)
            {
                foreach (var failed in _failedConfigurationIds.Values) failed.Remove(configurationId);
                _accessRejectedConfigurationIds.Remove(configurationId);
                if (_routeRetries.Remove(configurationId, out var retry)) retry.Cancel();
                _retryRounds.Remove(configurationId);
            }
            await SaveAsync().ConfigureAwait(false);
        }

        private void ResetAllRuntimeSelectionsLocked()
        {
            foreach (var retry in _routeRetries.Values) retry.Cancel();
            _routeRetries.Clear();
            _retryRounds.Clear();
            _accessRejectedConfigurationIds.Clear();
            _runtimeConfigurationIds.Clear();
            _failedConfigurationIds.Clear();
            _unavailableNetworkKeys.Clear();
        }

        private async Task SaveAsync()
        {
            await OnChainProviderConfigurationStore.SaveAsync(ApplicationData.Current.LocalFolder.Path, _document)
                .ConfigureAwait(false);
            ReconcileUsageBudgets(configurationChanged: true);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private OnChainProviderConfiguration Clone(OnChainProviderConfiguration source)
        {
            return new OnChainProviderConfiguration
            {
                Id = source.Id,
                Usage = Usage.CreateScope(source),
                ChainNamespace = source.ChainNamespace,
                ChainId = source.ChainId,
                ProviderType = source.ProviderType,
                FriendlyName = source.FriendlyName,
                StreamEndpoint = source.StreamEndpoint,
                RpcEndpoint = source.RpcEndpoint,
                Region = source.Region,
                CredentialReference = source.CredentialReference,
                Commitment = source.Commitment,
                ReplayEnabled = source.ReplayEnabled,
                CapabilitySnapshot = Clone(source.CapabilitySnapshot)
            };
        }

        private static OnChainProviderCapabilitySnapshot? Clone(OnChainProviderCapabilitySnapshot? source)
        {
            return source == null
                ? null
                : new OnChainProviderCapabilitySnapshot
                {
                    ObservedAtUnixMs = source.ObservedAtUnixMs,
                    ChainId = source.ChainId,
                    SafeBlock = source.SafeBlock,
                    FinalizedBlock = source.FinalizedBlock,
                    BlockHashCall = source.BlockHashCall,
                    BlockHashLogs = source.BlockHashLogs,
                    Batch = source.Batch,
                    WebSocketHeads = source.WebSocketHeads,
                    WebSocketLogs = source.WebSocketLogs
                };
        }
    }
}

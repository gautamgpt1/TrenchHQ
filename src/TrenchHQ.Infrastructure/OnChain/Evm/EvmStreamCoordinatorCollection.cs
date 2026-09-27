using TrenchHQ.Core.OnChain;
using TrenchHQ.Core.OnChain.Evm;
using TrenchHQ.Core.Providers;
using TrenchHQ.Infrastructure.OnChain;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TrenchHQ.Infrastructure.OnChain.Evm
{
    internal sealed class EvmStreamCoordinatorCollection
    {
        private readonly Dictionary<string, EvmStreamCoordinator> _coordinators;
        private readonly HashSet<string> _activeChainIds = new(StringComparer.Ordinal);
        private readonly object _activeLock = new();

        internal EvmStreamCoordinatorCollection(
            OnChainEngineClient engine,
            string checkpointFolder,
            Func<EvmChainDefinition, EvmStreamCoordinator>? coordinatorFactory = null)
        {
            ArgumentNullException.ThrowIfNull(engine);
            ArgumentNullException.ThrowIfNull(checkpointFolder);
            coordinatorFactory ??= chain => new EvmStreamCoordinator(engine, checkpointFolder, chain);
            _coordinators = EvmChainDefinitions.Supported.ToDictionary(
                static chain => chain.ChainId,
                chain => coordinatorFactory(chain),
                StringComparer.Ordinal);
            foreach (var coordinator in _coordinators.Values)
            {
                coordinator.StateChanged += OnStateChanged;
                coordinator.StreamError += OnStreamError;
                coordinator.ProviderFailed += OnProviderFailed;
            }
        }

        internal event EventHandler<OnChainRecoveryStateChangedEventArgs>? StateChanged;
        internal event EventHandler<string>? StreamError;
        internal event EventHandler<OnChainProviderFailureEventArgs>? ProviderFailed;

        internal IReadOnlyCollection<EvmStreamCoordinator> Coordinators => _coordinators.Values;

        internal EvmStreamCoordinator Get(string chainId)
        {
            return _coordinators.TryGetValue(chainId, out var coordinator)
                ? coordinator
                : throw new ArgumentException("The EVM chain is not supported.", nameof(chainId));
        }

        internal OnChainRecoveryState State => GetStateFor(GetActiveChainIds());

        internal OnChainRecoveryState GetStateFor(IEnumerable<string> chainIds)
        {
            var states = chainIds
                .Distinct(StringComparer.Ordinal)
                .Select(Get)
                .Select(static coordinator => coordinator.State)
                .ToArray();
            if (states.Length == 0 || states.Any(static state => state == OnChainRecoveryState.Connecting))
            {
                return OnChainRecoveryState.Connecting;
            }
            if (states.All(static state => state == OnChainRecoveryState.Live))
            {
                return OnChainRecoveryState.Live;
            }
            if (states.Any(static state => state == OnChainRecoveryState.Reconciling))
            {
                return OnChainRecoveryState.Reconciling;
            }
            if (states.Any(static state => state == OnChainRecoveryState.Replaying))
            {
                return OnChainRecoveryState.Replaying;
            }
            if (states.Any(static state => state == OnChainRecoveryState.Reconnecting))
            {
                return OnChainRecoveryState.Reconnecting;
            }
            return states[0];
        }

        internal async Task StartAsync(
            string chainId,
            OnChainProviderConfiguration configuration,
            string? apiKey,
            OnChainWatchedPoolSelection[] pools,
            CancellationToken cancellationToken = default,
            IReadOnlySet<string>? webSocketPoolIds = null)
        {
            var coordinator = Get(chainId);
            await coordinator.StartAsync(configuration, apiKey, pools, cancellationToken,
                    webSocketPoolIds)
                .ConfigureAwait(false);
            lock (_activeLock)
            {
                _activeChainIds.Add(chainId);
            }
        }

        internal async Task StopAsync(string chainId)
        {
            await Get(chainId).StopAsync().ConfigureAwait(false);
            lock (_activeLock)
            {
                _activeChainIds.Remove(chainId);
            }
        }

        internal async Task StopAsync()
        {
            foreach (var coordinator in _coordinators.Values)
            {
                await coordinator.StopAsync().ConfigureAwait(false);
            }
            lock (_activeLock)
            {
                _activeChainIds.Clear();
            }
        }

        private string[] GetActiveChainIds()
        {
            lock (_activeLock)
            {
                return [.. _activeChainIds];
            }
        }

        private void OnStateChanged(object? sender, OnChainRecoveryStateChangedEventArgs e)
        {
            StateChanged?.Invoke(sender ?? this, e);
        }

        private void OnStreamError(object? sender, string message)
        {
            StreamError?.Invoke(sender ?? this, message);
        }

        private void OnProviderFailed(object? sender, OnChainProviderFailureEventArgs e)
        {
            ProviderFailed?.Invoke(sender ?? this, e);
        }
    }
}

using TrenchHQ.Models;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrenchHQ.Helpers
{
    internal delegate Task<(bool Heads, bool Logs)> EvmWebSocketCapabilityProbe(
        OnChainProviderConfiguration configuration,
        string? apiKey,
        string poolAddress,
        string topic,
        CancellationToken cancellationToken);

    internal sealed class EvmProviderCapabilityProbe
    {
        private const string DecimalsSelector = "0x313ce567";

        private readonly HttpClient _httpClient;
        private readonly EvmWebSocketCapabilityProbe _webSocketProbe;

        internal EvmProviderCapabilityProbe(
            HttpClient httpClient,
            EvmWebSocketCapabilityProbe? webSocketProbe = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _webSocketProbe = webSocketProbe ?? EvmWebSocketStreamSource.ProbeAsync;
        }

        internal async Task<OnChainProviderCapabilitySnapshot> ProbeAsync(
            OnChainProviderConfiguration configuration,
            string? apiKey,
            CancellationToken cancellationToken = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var client = new EvmJsonRpcClient(_httpClient, configuration, apiKey);
            var expectedChainId = ulong.Parse(configuration.ChainId);
            var chain = EvmChainDefinitions.Supported.FirstOrDefault(definition =>
                definition.ChainId == configuration.ChainId)
                ?? throw new InvalidOperationException("The EVM provider chain is unsupported.");
            var actualChainId = await RetryTransientAsync(
                    () => client.GetChainIdAsync(timeout.Token),
                    timeout.Token)
                .ConfigureAwait(false);
            if (actualChainId != expectedChainId)
            {
                throw new InvalidOperationException(
                    $"The EVM endpoint returned chain ID {actualChainId}, expected {expectedChainId}.");
            }

            var snapshot = new OnChainProviderCapabilitySnapshot
            {
                ObservedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ChainId = OnChainProviderCapabilityState.Supported
            };
            var latest = await RetryTransientAsync(
                    () => client.GetBlockAsync("latest", timeout.Token),
                    timeout.Token)
                .ConfigureAwait(false);
            snapshot.BlockHashCall = await ProbeCapabilityAsync(async () =>
            {
                var result = await client.CallAsync(
                    EvmMulticall3.Address,
                    EvmMulticall3.EncodePair(
                        chain.WrappedNativeAssetAddress,
                        DecimalsSelector,
                        DecimalsSelector),
                    new { blockHash = latest.Hash, requireCanonical = true },
                    timeout.Token).ConfigureAwait(false);
                if (result.ValueKind != JsonValueKind.String
                    || !EvmMulticall3.TryDecodePair(result.GetString(), out var first, out var second)
                    || !EthereumAbi.TryDecodeSingleUnsigned(first, 8, out var firstDecimals)
                    || !EthereumAbi.TryDecodeSingleUnsigned(second, 8, out var secondDecimals)
                    || firstDecimals != secondDecimals)
                {
                    throw new InvalidDataException("The pinned Multicall3 result is invalid.");
                }
            }, timeout.Token, cancellationToken).ConfigureAwait(false);
            (snapshot.WebSocketHeads, snapshot.WebSocketLogs) = await ProbeWebSocketAsync(
                    configuration,
                    apiKey,
                    chain,
                    timeout.Token,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!EvmWebSocketStreamSource.ShouldSubscribeToHeads(chain.ChainId))
            {
                snapshot.WebSocketHeads = OnChainProviderCapabilityState.Unknown;
            }
            snapshot.SafeBlock = await ProbeCapabilityAsync(
                async () => { _ = await client.GetBlockAsync("safe", timeout.Token).ConfigureAwait(false); },
                timeout.Token,
                cancellationToken)
                .ConfigureAwait(false);
            snapshot.FinalizedBlock = await ProbeCapabilityAsync(
                async () => { _ = await client.GetBlockAsync("finalized", timeout.Token).ConfigureAwait(false); },
                timeout.Token,
                cancellationToken)
                .ConfigureAwait(false);
            snapshot.BlockHashLogs = await ProbeCapabilityAsync(async () =>
            {
                var result = await client.GetLogsAsync(
                    new
                    {
                        blockHash = latest.Hash,
                        address = chain.NativeUsdReferencePoolId,
                        topics = new[]
                        {
                            configuration.ChainId == EvmChainDefinitions.BnbMainnetChainId
                                ? EvmWebSocketStreamSource.PancakeV3SwapTopic
                                : EvmWebSocketStreamSource.UniswapV3SwapTopic
                        }
                    },
                    timeout.Token).ConfigureAwait(false);
                if (result.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException("The block-hash eth_getLogs result is invalid.");
                }
            }, timeout.Token, cancellationToken).ConfigureAwait(false);
            snapshot.Batch = await ProbeCapabilityAsync(
                    () => client.ProbeBatchAsync(timeout.Token),
                    timeout.Token,
                    cancellationToken)
                .ConfigureAwait(false);
            return snapshot;
        }

        private static async Task<OnChainProviderCapabilityState> ProbeCapabilityAsync(
            Func<Task> probe,
            CancellationToken probeCancellationToken,
            CancellationToken callerCancellationToken)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    await probe().ConfigureAwait(false);
                    return OnChainProviderCapabilityState.Supported;
                }
                catch (OperationCanceledException) when (!callerCancellationToken.IsCancellationRequested)
                {
                    return OnChainProviderCapabilityState.Degraded;
                }
                catch (Exception exception) when (exception is not OperationCanceledException and not OnChainUsageBudgetException)
                {
                    var state = Classify(exception);
                    if (attempt == 2 || !IsTransient(state))
                    {
                        return state;
                    }
                    var delay = state == OnChainProviderCapabilityState.RateLimited
                        ? TimeSpan.FromSeconds(2)
                        : TimeSpan.FromMilliseconds(500 * (attempt + 1));
                    try
                    {
                        await Task.Delay(delay, probeCancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!callerCancellationToken.IsCancellationRequested)
                    {
                        return OnChainProviderCapabilityState.Degraded;
                    }
                }
            }
            return OnChainProviderCapabilityState.Degraded;
        }

        private async Task<(OnChainProviderCapabilityState Heads, OnChainProviderCapabilityState Logs)>
            ProbeWebSocketAsync(
                OnChainProviderConfiguration configuration,
                string? apiKey,
                EvmChainDefinition chain,
                CancellationToken probeCancellationToken,
                CancellationToken callerCancellationToken)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var result = await _webSocketProbe(
                        configuration,
                        apiKey,
                        chain.NativeUsdReferencePoolId!,
                        configuration.ChainId == EvmChainDefinitions.BnbMainnetChainId
                            ? EvmWebSocketStreamSource.PancakeV3SwapTopic
                            : EvmWebSocketStreamSource.UniswapV3SwapTopic,
                        probeCancellationToken).ConfigureAwait(false);
                    return (
                        result.Heads
                            ? OnChainProviderCapabilityState.Supported
                            : OnChainProviderCapabilityState.Unsupported,
                        result.Logs
                            ? OnChainProviderCapabilityState.Supported
                            : OnChainProviderCapabilityState.Unsupported);
                }
                catch (OperationCanceledException) when (!callerCancellationToken.IsCancellationRequested)
                {
                    return (
                        OnChainProviderCapabilityState.Degraded,
                        OnChainProviderCapabilityState.Degraded);
                }
                catch (Exception exception) when (exception is not OperationCanceledException and not OnChainUsageBudgetException)
                {
                    var state = Classify(exception);
                    if (attempt == 2 || !IsTransient(state))
                    {
                        return (state, state);
                    }
                    var delay = state == OnChainProviderCapabilityState.RateLimited
                        ? TimeSpan.FromSeconds(2)
                        : TimeSpan.FromMilliseconds(500 * (attempt + 1));
                    try
                    {
                        await Task.Delay(delay, probeCancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!callerCancellationToken.IsCancellationRequested)
                    {
                        return (
                            OnChainProviderCapabilityState.Degraded,
                            OnChainProviderCapabilityState.Degraded);
                    }
                }
            }
            return (
                OnChainProviderCapabilityState.Degraded,
                OnChainProviderCapabilityState.Degraded);
        }

        private static async Task<T> RetryTransientAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await operation().ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException and not OnChainUsageBudgetException)
                {
                    var state = Classify(exception);
                    if (attempt >= 2 || !IsTransient(state))
                    {
                        throw;
                    }
                    var delay = state == OnChainProviderCapabilityState.RateLimited
                        ? TimeSpan.FromSeconds(2)
                        : TimeSpan.FromMilliseconds(500 * (attempt + 1));
                    await Task.Delay(delay, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        private static OnChainProviderCapabilityState Classify(Exception exception)
        {
            if (exception is EvmJsonRpcException rpc)
            {
                return rpc.Kind switch
                {
                    EvmRpcFailureKind.AuthenticationRejected =>
                        OnChainProviderCapabilityState.AuthenticationRejected,
                    EvmRpcFailureKind.RateLimited => OnChainProviderCapabilityState.RateLimited,
                    EvmRpcFailureKind.RpcError when rpc.RpcCode is not (-32601 or -32602) =>
                        OnChainProviderCapabilityState.Degraded,
                    _ => OnChainProviderCapabilityState.Unsupported
                };
            }
            return OnChainProviderCapabilityState.Degraded;
        }

        private static bool IsTransient(OnChainProviderCapabilityState state)
        {
            return state is OnChainProviderCapabilityState.Degraded
                or OnChainProviderCapabilityState.RateLimited;
        }
    }
}

using TrenchHQ.Models;
using TrenchHQ.Yellowstone;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using System;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrenchHQ.Helpers
{
    internal sealed class SolanaProviderCapabilityProbe
    {
        private readonly HttpClient _httpClient;
        private readonly Func<Uri, CancellationToken, Task>? _webSocketProbe;
        private readonly Func<Uri, string, CancellationToken, Task>? _yellowstoneProbe;

        internal SolanaProviderCapabilityProbe(
            HttpClient httpClient,
            Func<Uri, CancellationToken, Task>? webSocketProbe = null,
            Func<Uri, string, CancellationToken, Task>? yellowstoneProbe = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _webSocketProbe = webSocketProbe;
            _yellowstoneProbe = yellowstoneProbe;
        }

        internal async Task ProbeAsync(
            OnChainProviderConfiguration configuration,
            string credential,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            var preset = OnChainProviderCatalog.Get(configuration.ProviderType);
            if (preset.ChainNamespace != ChainNamespaces.Solana)
            {
                throw new ArgumentException("The provider is not a Solana configuration.", nameof(configuration));
            }

            await new SolanaRpcClient(_httpClient, configuration, credential)
                .GetSlotAsync(configuration.Commitment, cancellationToken)
                .ConfigureAwait(false);

            if (preset.StreamTransport == OnChainStreamTransport.SolanaWebSocket)
            {
                var endpoint = OnChainProviderEndpointBuilder.Build(
                    configuration.StreamEndpoint,
                    credential,
                    preset.StreamAuthenticationMode);
                await ProbeWebSocketWithRetryAsync(
                        endpoint,
                        configuration.ProviderType,
                        cancellationToken, configuration.Usage)
                    .ConfigureAwait(false);
                return;
            }

            await (_yellowstoneProbe?.Invoke(
                    new Uri(configuration.StreamEndpoint, UriKind.Absolute),
                    credential.Trim(),
                    cancellationToken)
                ?? ProbeYellowstoneAsync(new Uri(configuration.StreamEndpoint, UriKind.Absolute),
                    credential.Trim(), cancellationToken, configuration.Usage))
                .ConfigureAwait(false);
        }

        private async Task ProbeWebSocketWithRetryAsync(
            Uri endpoint,
            string providerType,
            CancellationToken cancellationToken, OnChainUsageScope? usage)
        {
            const int maximumAttempts = 3;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await (_webSocketProbe?.Invoke(endpoint, cancellationToken)
                        ?? ProbeWebSocketAsync(endpoint, cancellationToken, usage)).ConfigureAwait(false);
                    return;
                }
                catch (WebSocketException exception)
                {
                    if (attempt < maximumAttempts)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    if (providerType == OnChainProviderTypes.ShyftWebSocket
                        && exception.Message.Contains("429", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Shyft rejected WebSocket access (HTTP 429). The endpoint is rate-limited or unavailable for this account or current network. Retry later or use another provider; TrenchHQ will not enable Shyft until slotSubscribe succeeds.",
                            exception);
                    }
                    throw;
                }
            }
        }

        private static async Task ProbeWebSocketAsync(Uri endpoint, CancellationToken cancellationToken, OnChainUsageScope? usage)
        {
            using var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);

            var request = Encoding.UTF8.GetBytes(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"slotSubscribe\"}");
            usage?.Rpc("slotSubscribe");
            await socket.SendAsync(
                    request,
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken)
                .ConfigureAwait(false);

            var buffer = new byte[4096];
            using var response = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new InvalidOperationException(
                        "The Solana WebSocket closed before confirming slotSubscribe.");
                }
                if (result.MessageType != WebSocketMessageType.Text
                    || response.Length + result.Count > 64 * 1024)
                {
                    throw new InvalidOperationException(
                        "The Solana WebSocket returned an invalid slotSubscribe response.");
                }
                response.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            usage?.WebSocket(response.ToArray());
            ValidateWebSocketSubscriptionResponse(
                Encoding.UTF8.GetString(response.GetBuffer(), 0, checked((int)response.Length)));
        }

        internal static void ValidateWebSocketSubscriptionResponse(string response)
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.Number
                || id.GetInt32() != 1
                || !root.TryGetProperty("result", out var subscriptionId)
                || subscriptionId.ValueKind != JsonValueKind.Number)
            {
                throw new InvalidOperationException(
                    "The Solana WebSocket did not confirm slotSubscribe.");
            }
        }

        private static async Task ProbeYellowstoneAsync(
            Uri endpoint,
            string credential,
            CancellationToken cancellationToken, OnChainUsageScope? usage)
        {
            using var handler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1)
            };
            using var channel = GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions
            {
                HttpHandler = handler,
                DisposeHttpClient = true
            });
            var client = new Geyser.GeyserClient(channel);
            var headers = new Metadata { { "x-token", credential } };
            using var call = client.Subscribe(headers, cancellationToken: cancellationToken);
            var request = new SubscribeRequest
            {
                Commitment = CommitmentLevel.Confirmed
            };
            request.Slots.Add("capability-probe", new SubscribeRequestFilterSlots
            {
                FilterByCommitment = false,
                InterslotUpdates = false
            });
            await call.RequestStream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
            {
                usage?.Grpc(call.ResponseStream.Current.CalculateSize());
                if (call.ResponseStream.Current.UpdateOneofCase
                    == SubscribeUpdate.UpdateOneofOneofCase.Ping)
                {
                    await call.RequestStream.WriteAsync(
                        new SubscribeRequest { Ping = new SubscribeRequestPing { Id = 1 } },
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }
                return;
            }
            throw new InvalidOperationException("The Yellowstone subscription closed before returning an update.");
        }
    }
}

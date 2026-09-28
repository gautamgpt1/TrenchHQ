using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using TrenchHQ.Core.OnChain;
using TrenchHQ.Core.Providers;
using TrenchHQ.Core.Wallets;
using TrenchHQ.Core.Widgets;
using TrenchHQ.Infrastructure.OnChain.Solana;
using TrenchHQ.Infrastructure.OnChain;
using TrenchHQ.Infrastructure.OnChain.Evm;
using TrenchHQ.Infrastructure.Providers;
using Xunit;

internal static class LiveWalletTests
{
    internal static async Task EvmAsync()
    {
        const string wallet = "0x1111111111111111111111111111111111111111";
        foreach (var preset in OnChainProviderCatalog.AllPresets.Where(item => item.StreamTransport == OnChainStreamTransport.EvmWebSocket))
        {
            var profile = OnChainProviderConfigurationStore.CreateConfiguration(preset);
            profile.RpcEndpoint = "https://fixture.invalid";
            profile.StreamEndpoint = "wss://fixture.invalid";
            using var handler = new EvmHandler();
            using var http = new HttpClient(handler);
            using var socket = new Socket();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var updates = new ConcurrentQueue<WalletActivityUpdate>();
            var live = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var source = new EvmWalletActivityStreamSource(profile, "fixture-key");
            var run = source.RunConnectedAsync(socket, new EvmJsonRpcClient(http, profile, "fixture-key"),
                [new() { Address = wallet, ChainNamespace = ChainNamespaces.Eip155, ChainId = profile.ChainId }],
                updates.Enqueue, _ => live.TrySetResult(), stop.Token);
            try
            {
                await live.Task.WaitAsync(stop.Token);
                Assert.Empty(handler.Blocks);
                socket.Head(100);
                await handler.Started.Task.WaitAsync(stop.Token);
                socket.EvmLog(wallet);
                await UntilAsync(() => !updates.IsEmpty, stop.Token);
                Assert.False(handler.Release.Task.IsCompleted); // HTTP does not delay subscribed transfers.
                socket.Head(110);
                handler.Release.TrySetResult();
                await UntilAsync(() => handler.Blocks.Count == 2, stop.Token);
                Assert.Equal(new[] { "0x64", "0x6e" }, handler.Blocks.ToArray());
            }
            finally
            {
                stop.Cancel();
                handler.Release.TrySetResult();
                try { await run; } catch (OperationCanceledException) { }
            }
        }
    }

    internal static async Task RunAsync()
    {
        foreach (var preset in OnChainProviderCatalog.AllPresets.Where(item =>
                     item.StreamTransport == OnChainStreamTransport.SolanaWebSocket))
        {
            var profile = OnChainProviderConfigurationStore.CreateConfiguration(preset);
            profile.RpcEndpoint = "https://fixture.invalid";
            profile.StreamEndpoint = "wss://fixture.invalid";
            using var handler = new Handler();
            using var http = new HttpClient(handler);
            var rpc = new SolanaRpcClient(http, profile, "fixture-key");
            var source = new SolanaWalletActivityStreamSource(profile, "fixture-key");
            using var socket = new Socket();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var updates = new ConcurrentQueue<WalletActivityUpdate>();
            var live = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var run = source.RunConnectedAsync(socket, rpc,
                [new() { Address = "11111111111111111111111111111111" }, new() { Address = "So11111111111111111111111111111111111111112" }],
                updates.Enqueue, () => live.TrySetResult(), stop.Token);
            try
            {
                await live.Task.WaitAsync(stop.Token);
                Assert.Empty(updates);
                Assert.DoesNotContain(handler.Methods, method => method is "getTransaction" or "getSignaturesForAddress");
                socket.Log("11111111111111111111111111111111", "shared", 100);
                await handler.DetailStarted.Task.WaitAsync(stop.Token);
                socket.Log("11111111111111111111111111111111", "shared", 100);
                socket.Log("So11111111111111111111111111111111111111112", "shared", 100);
                socket.Log("11111111111111111111111111111111", "next", 101);
                await UntilAsync(() => updates.Any(item => item.TransactionId == "next"), stop.Token);
                Assert.False(handler.ReleaseDetails.Task.IsCompleted);
                Assert.Equal(3, updates.Count); // Live receipt is independent of slow HTTP; duplicates disappear.
                handler.ReleaseDetails.TrySetResult();
                await UntilAsync(() => handler.Transactions.GetValueOrDefault("next") > 0, stop.Token);
                Assert.Equal(1, handler.Transactions["shared"]); // Shared by both watched wallets.

                socket.Program("11111111111111111111111111111111", "TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA", 200);
                await UntilAsync(() => updates.Any(item => item.TransactionId == "slot-match"), stop.Token);
                Assert.DoesNotContain(updates, item => item.TransactionId is "old" or "later");
                Assert.Equal(1, handler.Methods.Count(method => method == "getSignaturesForAddress"));
            }
            finally
            {
                stop.Cancel();
                handler.ReleaseDetails.TrySetResult();
                try { await run; } catch (OperationCanceledException) { }
            }
            // Reconnection starts empty and does not replay any of the prior signatures.
            using var reconnect = new Socket();
            using var reconnectStop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var historyCalls = handler.Methods.Count(method => method == "getSignaturesForAddress");
            var rerun = source.RunConnectedAsync(reconnect, rpc, [new() { Address = "11111111111111111111111111111111" }],
                _ => throw new InvalidOperationException("Replayed a transaction on reconnect."),
                () => reconnected.TrySetResult(), reconnectStop.Token);
            await reconnected.Task.WaitAsync(reconnectStop.Token);
            reconnectStop.Cancel();
            try { await rerun; } catch (OperationCanceledException) { }
            Assert.Equal(historyCalls, handler.Methods.Count(method => method == "getSignaturesForAddress"));
        }

        var attempts = 0;
        var result = await OnChainRequestRetry.RunAsync(() =>
        {
            if (++attempts < 3) throw new SolanaRpcException("fixture throttle", httpStatusCode: 429, retryAfter: TimeSpan.Zero);
            return Task.FromResult(7);
        }, default);
        Assert.Equal(7, result);
        Assert.Equal(3, attempts);
        attempts = 0;
        await Assert.ThrowsAsync<SolanaRpcException>(() => OnChainRequestRetry.RunAsync<int>(() =>
        {
            attempts++;
            throw new SolanaRpcException("fixture rejected", httpStatusCode: 403);
        }, default));
        Assert.Equal(1, attempts);

        attempts = 0;
        await Assert.ThrowsAsync<EvmJsonRpcException>(() => OnChainRequestRetry.RunAsync<int>(() =>
        {
            attempts++;
            throw new EvmJsonRpcException(EvmRpcFailureKind.RateLimited, "long provider pause", 429, TimeSpan.FromMinutes(5));
        }, default));
        Assert.Equal(1, attempts); // Delegate a long pause to automatic routing, don't freeze enrichment for five minutes.

        using var usage = new OnChainProviderUsage();
        var first = OnChainProviderConfigurationStore.CreateConfiguration(OnChainProviderCatalog.Get(OnChainProviderTypes.AlchemyWebSocket));
        var backup = OnChainProviderConfigurationStore.CreateConfiguration(OnChainProviderCatalog.Get(OnChainProviderTypes.AlchemyWebSocket));
        var linked = OnChainProviderConfigurationStore.CreateConfiguration(OnChainProviderCatalog.Get(OnChainProviderTypes.AlchemyBase));
        linked.CredentialReference = first.CredentialReference;
        usage.PauseForQuota(first, TimeSpan.FromMinutes(2));
        Assert.True(usage.IsBlocked(first));
        Assert.True(usage.IsBlocked(linked));
        Assert.False(usage.IsBlocked(backup));
        var service = new OnChainProviderConfigurationService(new() { Configurations = [first, backup, linked] },
            () => true, usage: usage);
        Assert.Equal(backup.Id, service.GetSelectedConfiguration()!.Id);

        using var independentUsage = new OnChainProviderUsage();
        var retryService = new OnChainProviderConfigurationService(new() { Configurations = [backup] },
            () => true, TimeSpan.FromMilliseconds(10), independentUsage);
        await retryService.TryFailoverAsync(backup.Id, OnChainProviderFailureKind.RateLimited, TimeSpan.FromMilliseconds(300));
        await Task.Delay(60);
        Assert.Null(retryService.GetSelectedConfiguration());
        using var retryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await UntilAsync(() => retryService.GetSelectedConfiguration()?.Id == backup.Id, retryTimeout.Token);
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition()) await Task.Delay(10, cancellationToken);
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal readonly ConcurrentQueue<string> Methods = new();
        internal readonly ConcurrentDictionary<string, int> Transactions = new();
        internal readonly TaskCompletionSource DetailStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseDetails = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var method = body.RootElement.GetProperty("method").GetString()!;
            Methods.Enqueue(method);
            object result;
            switch (method)
            {
                case "getTokenAccountsByOwner":
                    result = new { context = new { slot = 99 }, value = Array.Empty<object>() };
                    break;
                case "getSignaturesForAddress":
                    result = new[] { new { signature = "later", slot = 201 }, new { signature = "slot-match", slot = 200 }, new { signature = "old", slot = 1 } }
                        .Select(item => new { item.signature, item.slot, err = (object?)null, confirmationStatus = "confirmed", blockTime = 1 });
                    break;
                case "getTransaction":
                    var signature = body.RootElement.GetProperty("params")[0].GetString()!;
                    Transactions.AddOrUpdate(signature, 1, (_, count) => count + 1);
                    DetailStarted.TrySetResult();
                    await ReleaseDetails.Task.WaitAsync(cancellationToken);
                    result = new
                    {
                        slot = 100,
                        transaction = new { signatures = new[] { signature }, message = new { accountKeys = new[] { "11111111111111111111111111111111", "So11111111111111111111111111111111111111112" } } },
                        meta = new { err = (object?)null, fee = 0, preBalances = new[] { 100, 0 }, postBalances = new[] { 90, 10 } }
                    };
                    break;
                default: throw new InvalidOperationException("Unexpected wallet request: " + method);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, result }))
            };
        }
    }

    private sealed class EvmHandler : HttpMessageHandler
    {
        internal readonly ConcurrentQueue<string> Blocks = new();
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var method = body.RootElement.GetProperty("method").GetString();
            object result;
            if (method == "eth_getBlockByNumber")
            {
                var block = body.RootElement.GetProperty("params")[0].GetString()!;
                Blocks.Enqueue(block);
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                var number = Convert.ToInt32(block[2..], 16);
                result = new { number = block, hash = Hash(number), parentHash = Hash(number - 1), timestamp = "0x65000000", transactions = Array.Empty<object>() };
            }
            else if (method == "eth_call") result = "0x" + 18.ToString("x64");
            else throw new InvalidOperationException("Unexpected EVM wallet call: " + method);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, result })) };
        }
    }

    private static string Hash(int value) => "0x" + value.ToString("x64");

    private sealed class Socket : WebSocket
    {
        private readonly Channel<byte[]> _messages = Channel.CreateUnbounded<byte[]>();
        private readonly ConcurrentDictionary<string, long> _logs = new();
        private readonly ConcurrentDictionary<string, long> _programs = new();
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            using var message = JsonDocument.Parse(buffer.AsMemory());
            var root = message.RootElement;
            var id = root.GetProperty("id").GetInt64();
            var parameters = root.GetProperty("params");
            if (root.GetProperty("method").GetString() == "eth_subscribe")
            {
                Push(new { jsonrpc = "2.0", id, result = id.ToString() });
                return Task.CompletedTask;
            }
            if (root.GetProperty("method").GetString() == "logsSubscribe")
                _logs[parameters[0].GetProperty("mentions")[0].GetString()!] = id;
            else
                _programs[parameters[1].GetProperty("filters")[0].GetProperty("memcmp").GetProperty("bytes").GetString()!] = id;
            Push(new { jsonrpc = "2.0", id, result = id });
            return Task.CompletedTask;
        }

        internal void Log(string address, string signature, int slot) => Push(new
        {
            method = "logsNotification",
            @params = new { subscription = _logs[address], result = new { context = new { slot }, value = new { signature, err = (object?)null } } }
        });
        internal void Program(string wallet, string pubkey, int slot) => Push(new
        {
            method = "programNotification",
            @params = new { subscription = _programs[wallet], result = new { context = new { slot }, value = new { pubkey } } }
        });
        internal void Head(int number) => Push(new
        {
            method = "eth_subscription",
            @params = new { subscription = "1", result = new { number = "0x" + number.ToString("x"), hash = Hash(number), parentHash = Hash(number - 1), timestamp = "0x65000000" } }
        });
        internal void EvmLog(string wallet) => Push(new
        {
            method = "eth_subscription",
            @params = new
            {
                subscription = "2", result = new
                {
                    address = "0x2222222222222222222222222222222222222222", blockNumber = "0x64", blockHash = Hash(100),
                    transactionHash = Hash(999), transactionIndex = "0x0", logIndex = "0x0", removed = false,
                    topics = new[] { WalletActivityRules.TransferTopic, "0x" + wallet[2..].PadLeft(64, '0'), Hash(333) }, data = Hash(5)
                }
            }
        });
        private void Push(object message) => _messages.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(message));
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var message = await _messages.Reader.ReadAsync(cancellationToken);
            message.AsSpan().CopyTo(buffer.AsSpan());
            return new WebSocketReceiveResult(message.Length, WebSocketMessageType.Text, true);
        }
    }
}

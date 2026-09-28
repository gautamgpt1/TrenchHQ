using TrenchHQ.Core.Providers;
using TrenchHQ.Infrastructure.OnChain.Evm;
using TrenchHQ.Infrastructure.OnChain.Solana;
using TrenchHQ.Infrastructure.Providers;
using System.Text;
using System.Text.Json;

internal static class UsageBudgetTests
{
    internal static async Task RunAsync()
    {
        var now = new DateTimeOffset(2026, 1, 31, 12, 0, 0, TimeSpan.Zero);
        var folder = Path.Combine(Path.GetTempPath(), "TrenchHQUsageFixture", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var ethereum = Profile(OnChainProviderTypes.AlchemyEthereum);
        var sibling = Profile(OnChainProviderTypes.AlchemyBase);
        var backup = Profile(OnChainProviderTypes.QuickNodeEthereum);
        var publicNode = Profile(OnChainProviderTypes.PublicNodeEthereum);
        var network = OnChainProviderConfigurationStore.GetNetworkKey(ethereum.ChainNamespace, ethereum.ChainId);
        var document = new OnChainProviderConfigurationDocument
        {
            Configurations = [ethereum, sibling, backup, publicNode],
            SelectedConfigurationIds = new() { [network] = ethereum.Id },
            FallbackConfigurationIds = new() { [network] = [backup.Id] }
        };
        using (var usage = new OnChainProviderUsage(folder, () => now))
        {
            var service = new OnChainProviderConfigurationService(document, () => true, usage: usage);
            var key = usage.Register(ethereum).Single();
            Check(key == usage.Register(sibling).Single(), "Provider chains did not share an allowance.");
            usage.Configure(key, true, 40, false, now, 0);
            service.GetSelectedConfiguration(ethereum.ChainNamespace, ethereum.ChainId)!.Usage!.Rpc("eth_getBlockByNumber");
            usage.CreateScope(sibling).Rpc("eth_getBlockByNumber");
            Check(service.GetSelectedConfiguration(ethereum.ChainNamespace, ethereum.ChainId)?.Id == backup.Id,
                "A shared budget did not select the configured backup.");
            var backupKey = usage.Register(backup).Single();
            usage.Configure(backupKey, true, 20, false, now, 20);
            Check(service.GetSelectedConfiguration(ethereum.ChainNamespace, ethereum.ChainId)?.Id == publicNode.Id,
                "Exhausted backups did not advance to PublicNode.");
            using var handler = new CountingHandler();
            using var http = new HttpClient(handler);
            ethereum.Usage = usage.CreateScope(ethereum);
            try { await new EvmJsonRpcClient(http, ethereum, "fixture-key").GetChainIdAsync(default); throw new Exception("Budget allowed a request."); }
            catch (OnChainUsageBudgetException) { }
            Check(handler.Count == 0, "A blocked request reached HTTP.");
            now = new DateTimeOffset(2026, 2, 28, 12, 0, 0, TimeSpan.Zero);
            Check(usage.Snapshot(key).Used == 0, "Monthly reset failed at the end of February.");
            usage.Tick();
            Check(!usage.IsBlocked(ethereum), "Budget did not reset.");
            Check(service.GetSelectedConfiguration(ethereum.ChainNamespace, ethereum.ChainId)?.Id == ethereum.Id,
                "Budget reset did not restore an eligible private provider.");
            usage.CreateScope(ethereum).Rpc("eth_call");
            now = new DateTimeOffset(2026, 3, 30, 12, 0, 0, TimeSpan.Zero);
            usage.Tick();
            Check(usage.Snapshot(key).Used == 26, "Monthly anchor drifted to February's shorter day.");
            now = now.AddDays(1);
            usage.Tick();
            Check(usage.Snapshot(key).Used == 0, "March monthly reset failed.");
            usage.CreateScope(ethereum).Rpc("eth_call");
            try { usage.CreateScope(ethereum).Rpc("eth_call"); throw new Exception("Oversized reservation allowed."); }
            catch (OnChainUsageBudgetException) { }
            Check(usage.Snapshot(key).Used == 26, "An unsent request was charged.");
            Check(usage.IsBlocked(sibling), "Blocked reservation did not pause the shared group.");
        }
        using (var restored = new OnChainProviderUsage(folder, () => now))
        {
            Check(restored.IsBlocked(ethereum), "Restart lost the budget pause.");
            Check(restored.Snapshot(OnChainProviderUsage.GroupKey(ethereum)).Used == 26, "Restart lost usage.");
            var json = File.ReadAllText(Path.Combine(folder, OnChainProviderUsage.FileName));
            Check(!json.Contains("fixture-key") && !json.Contains("https:") && !json.Contains(ethereum.CredentialReference),
                "Usage ledger persisted connection secrets or identifiers.");
        }

        using (var usage = new OnChainProviderUsage(now: () => now))
        {
            var solana = Profile(OnChainProviderTypes.HeliusWebSocket);
            var solNetwork = OnChainProviderConfigurationStore.GetNetworkKey(solana.ChainNamespace, solana.ChainId);
            var service = new OnChainProviderConfigurationService(new OnChainProviderConfigurationDocument
            {
                Configurations = [solana], SelectedConfigurationIds = new() { [solNetwork] = solana.Id }
            }, () => true, usage: usage);
            var key = usage.Register(solana).Single();
            usage.Configure(key, true, 1, true, now, 0);
            usage.CreateScope(solana).Rpc("getAccountInfo");
            Check(service.GetSelectedConfiguration() == null, "Exhausted Solana continued without a backup.");
            now = now.AddDays(1);
            // Snapshot can observe a reset before the timer; availability must still be delivered.
            usage.Snapshot(key);
            usage.Tick();
            Check(service.GetSelectedConfiguration()?.Id == solana.Id, "Daily reset did not resume a paused route.");
            usage.CreateScope(solana).WebSocket(Encoding.UTF8.GetBytes("{\"method\":\"accountNotification\"}"));
            Check(usage.Snapshot(key).Used > 0, "Solana stream bytes were not counted.");
        }

        using (var usage = new OnChainProviderUsage())
        {
            var infura = Profile(OnChainProviderTypes.InfuraEthereum);
            var scope = usage.CreateScope(infura);
            byte[] Message(string field, string subscription) => JsonSerializer.SerializeToUtf8Bytes(new
            {
                method = "eth_subscription", @params = new { subscription, result = new Dictionary<string, string> { [field] = "0x10" } }
            });
            scope.Rpc("eth_subscribe");
            scope.WebSocket(Message("blockNumber", "logs"));
            scope.WebSocket(Message("blockNumber", "logs"));
            scope.WebSocket(Message("number", "heads"));
            Check(usage.Snapshot(OnChainProviderUsage.GroupKey(infura)).Used == 355, "Infura charged each log instead of each block.");
            foreach (var type in new[] { OnChainProviderTypes.AlchemyWebSocket, OnChainProviderTypes.HeliusWebSocket,
                         OnChainProviderTypes.QuickNodeWebSocket, OnChainProviderTypes.ChainstackWebSocket, OnChainProviderTypes.DrpcWebSocket })
            {
                var profile = Profile(type);
                var key = usage.Register(profile).Single();
                var preset = OnChainProviderCatalog.Get(type);
                var expected = preset.ProviderFamily switch { "alchemy" => 10m, "helius" or "chainstack" => 1m, "quicknode" => 30m, _ => 20m };
                usage.CreateScope(profile).Rpc("getAccountInfo");
                Check(usage.Snapshot(key).Used == expected, $"Wrong single-account cost: {type}.");
            }
            var alchemy = Profile(OnChainProviderTypes.AlchemyEthereum);
            var alchemyKey = usage.Register(alchemy).Single();
            usage.Configure(alchemyKey, true, 100, false, DateTimeOffset.UtcNow.AddSeconds(-1), 0);
            try { usage.CreateScope(alchemy).Rpc("unpriced_method"); throw new Exception("Unknown method bypassed budget."); }
            catch (OnChainUsageBudgetException) { }
        }
        foreach (var type in new[] { OnChainProviderTypes.AlchemyWebSocket, OnChainProviderTypes.HeliusWebSocket,
                     OnChainProviderTypes.QuickNodeWebSocket, OnChainProviderTypes.AlchemyEthereum })
        {
            using var usage = new OnChainProviderUsage();
            var profile = Profile(type);
            usage.Record(profile, null, 100_000, notification: true);
            var expected = type switch
            {
                OnChainProviderTypes.AlchemyWebSocket => 20m,
                OnChainProviderTypes.HeliusWebSocket => 2m,
                OnChainProviderTypes.QuickNodeWebSocket => 15m,
                _ => 4000m
            };
            Check(usage.Snapshot(OnChainProviderUsage.GroupKey(profile)).Used == expected, "Incorrect uncompressed stream-byte rate.");
        }
        {
            var migrationNow = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
            var migrationFolder = Path.Combine(Path.GetTempPath(), "TrenchHQUsageFixture", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(migrationFolder);
            var alchemy = Profile(OnChainProviderTypes.AlchemyWebSocket);
            var key = OnChainProviderUsage.GroupKey(alchemy);
            var oldGroup = new OnChainUsageGroup
            {
                Name = "alchemy", Enabled = true, Automatic = true, Daily = true,
                Limit = 100_000, Used = 20, StreamBytes = 100_000,
                AnchorUtc = migrationNow, PeriodStartUtc = migrationNow,
                Methods = new() { ["logsSubscribe"] = 1 }
            };
            File.WriteAllText(Path.Combine(migrationFolder, OnChainProviderUsage.FileName),
                JsonSerializer.Serialize(new Dictionary<string, OnChainUsageGroup> { [key] = oldGroup }));
            using var migrated = new OnChainProviderUsage(migrationFolder, () => migrationNow);
            Check(!migrated.IsBlocked(alchemy), "An app-imposed budget survived migration.");
            Check(!migrated.Snapshot(key).Enabled && migrated.Snapshot(key).Used == 20, "Migration lost counters or kept the automatic cap.");
            migrationNow = migrationNow.AddDays(1);
            Check(!migrated.IsBlocked(alchemy), "Automatic cap returned on the next day.");
            Check(migrated.Snapshot(key).Used == 20, "Unrestricted usage was reset on an invented daily cycle.");
        }
        using (var usage = new OnChainProviderUsage())
        {
            var profile = Profile(OnChainProviderTypes.AlchemyYellowstone);
            usage.Register(profile);
            usage.Configure(OnChainProviderUsage.GroupKey(profile, true), true, 1, false, DateTimeOffset.UtcNow.AddSeconds(-1), 1);
            try { usage.CreateScope(profile).Rpc("getSlot"); throw new Exception("Exhausted stream allowance allowed companion RPC traffic."); }
            catch (OnChainUsageBudgetException) { }
            Check(usage.Snapshot(OnChainProviderUsage.GroupKey(profile)).RpcRequests == 0,
                "Blocked companion RPC was counted as sent.");
        }
        foreach (var status in new[] { 401, 402, 403, 429, 200 })
        {
            var profile = Profile(OnChainProviderTypes.AlchemyWebSocket);
            using var handler = new TransactionHandler(status);
            using var http = new HttpClient(handler);
            var rpc = new SolanaRpcClient(http, profile, "fixture-key");
            var source = new SolanaWebSocketStreamSource(profile, "fixture-key");
            try
            {
                var result = await source.FetchTransactionAsync(rpc, "fixture-signature", 0, default);
                Check(status == 200 && result == null, "Rejected transaction read was swallowed.");
            }
            catch (SolanaRpcException exception)
            {
                Check(status != 200 && (exception.IsAccessRejected || exception.IsRateLimited), "Transaction failure was misclassified.");
            }
            Check(handler.Count == (status == 200 ? 3 : 1), "Rejected request retried, or publication-delay retries were lost.");
        }
        using (var usage = new OnChainProviderUsage())
        {
            var profile = Profile(OnChainProviderTypes.AlchemyWebSocket);
            var key = usage.Register(profile).Single();
            profile.Usage = usage.CreateScope(profile);
            usage.Configure(key, true, 1, false, DateTimeOffset.UtcNow.AddSeconds(-1), 0);
            using var handler = new TransactionHandler(200);
            using var http = new HttpClient(handler);
            try
            {
                await new SolanaWebSocketStreamSource(profile, "fixture-key").FetchTransactionAsync(
                    new SolanaRpcClient(http, profile, "fixture-key"), "fixture-signature", 0, default);
                throw new Exception("Transaction budget failure was swallowed.");
            }
            catch (OnChainUsageBudgetException) { }
            Check(handler.Count == 0, "Over-budget transaction reached HTTP.");
        }
        Console.WriteLine("PASS: usage accounting, shared budgets, pre-send guard, automatic fallback, reset, private recovery, restart, privacy and unknown-cost pause.");
        Console.WriteLine("PASS: stream byte rates and Solana transaction rejection propagation without repeated paid retries.");
    }

    private static OnChainProviderConfiguration Profile(string type)
    {
        var profile = OnChainProviderConfigurationStore.CreateConfiguration(OnChainProviderCatalog.Get(type));
        if (string.IsNullOrEmpty(profile.RpcEndpoint)) profile.RpcEndpoint = "https://fixture.invalid";
        if (string.IsNullOrEmpty(profile.StreamEndpoint)) profile.StreamEndpoint = "wss://fixture.invalid";
        return profile;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        internal int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            throw new InvalidOperationException("No fixture network request was expected.");
        }
    }

    private sealed class TransactionHandler(int status) : HttpMessageHandler
    {
        internal int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)status)
                { Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":null}") });
        }
    }
}

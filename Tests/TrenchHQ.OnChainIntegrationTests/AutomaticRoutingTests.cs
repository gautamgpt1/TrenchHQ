using TrenchHQ.Core.OnChain;
using TrenchHQ.Core.Providers;
using TrenchHQ.Infrastructure.OnChain.Evm;
using TrenchHQ.Infrastructure.OnChain.Solana;
using TrenchHQ.Infrastructure.Providers;
using System.Net;
using System.Text;

internal static class AutomaticRoutingTests
{
    internal static async Task RunAsync()
    {
        foreach (var preset in OnChainProviderCatalog.AllPresets)
        {
            var profile = Profile(preset.ProviderType);
            using var usage = new OnChainProviderUsage();
            var service = Service(usage, profile);
            Check(Selected(service, profile)?.Id == profile.Id, $"Key-only selection failed: {preset.ProviderType}");
            Check(usage.Register(profile).All(key => !usage.Snapshot(key).Enabled && usage.Snapshot(key).Limit == 0),
                $"An automatic usage cap was imposed: {preset.ProviderType}");
            foreach (var status in new[] { 402, 401, 403, 429 })
            {
                using var isolatedUsage = new OnChainProviderUsage();
                profile.Usage = isolatedUsage.CreateScope(profile);
                using var http = new HttpClient(new ResponseHandler(status));
                try
                {
                    if (preset.ChainNamespace == ChainNamespaces.Eip155)
                        await new EvmJsonRpcClient(http, profile, "fixture-key").GetChainIdAsync(default);
                    else
                        await new SolanaRpcClient(http, profile, "fixture-key").GetSlotAsync(OnChainCommitment.Confirmed, default);
                    throw new InvalidOperationException("Rejected request succeeded.");
                }
                catch (OnChainUsageBudgetException)
                {
                    Check(status == 402 && isolatedUsage.IsBlocked(profile), "Quota response did not pause the provider.");
                    Check(isolatedUsage.QuotaPauseUntil(profile).HasValue,
                        "Quota was treated as a permanently rejected credential.");
                }
                catch (EvmJsonRpcException) { Check(status != 402, "Quota bypassed the shared usage ledger."); }
                catch (SolanaRpcException) { Check(status != 402, "Quota bypassed the shared usage ledger."); }
            }
        }

        foreach (var type in new[] { OnChainProviderTypes.AlchemyEthereum, OnChainProviderTypes.HeliusWebSocket })
        foreach (var code in new[] { 401, 403, 429 })
        {
            using var usage = new OnChainProviderUsage();
            var profile = Profile(type);
            try
            {
                usage.CreateScope(profile).WebSocket(Encoding.UTF8.GetBytes($"{{\"error\":{{\"code\":{code},\"message\":\"rejected\"}}}}"));
                throw new InvalidOperationException("WebSocket error response was ignored.");
            }
            catch (OnChainProviderResponseException exception)
            {
                Check(exception.Kind == (code == 429 ? OnChainProviderFailureKind.RateLimited : OnChainProviderFailureKind.Authentication),
                    "WebSocket authentication/rate classification was wrong.");
            }
        }
        foreach (var type in new[] { OnChainProviderTypes.AlchemyEthereum, OnChainProviderTypes.HeliusWebSocket })
        foreach (var status in new[] { 200, 403, 429 })
        {
            using var usage = new OnChainProviderUsage();
            var profile = Profile(type);
            profile.Usage = usage.CreateScope(profile);
            using var http = new HttpClient(new ResponseHandler(status,
                "{\"error\":{\"code\":-32000,\"message\":\"Monthly capacity limit exceeded\"}}"));
            try
            {
                if (profile.ChainNamespace == ChainNamespaces.Eip155)
                    await new EvmJsonRpcClient(http, profile, "fixture-key").GetChainIdAsync(default);
                else
                    await new SolanaRpcClient(http, profile, "fixture-key").GetSlotAsync(OnChainCommitment.Confirmed, default);
                throw new InvalidOperationException("RPC quota error was ignored.");
            }
            catch (OnChainUsageBudgetException) { }
            Check(usage.IsBlocked(profile), "JSON quota response did not pause the provider.");
        }
        using (var usage = new OnChainProviderUsage())
        {
            var source = Profile(OnChainProviderTypes.AlchemyEthereum);
            var linked = OnChainProviderConfigurationStore.LinkSharedCredentialConfigurations([source],
                OnChainProviderCatalog.Get(source.ProviderType), Guid.NewGuid().ToString("N"), new HashSet<string>());
            var service = Service(usage, linked);
            Check(linked.Length == 5 && linked.All(profile => Selected(service, profile)?.Id == profile.Id),
                "Saving a shared key did not make all five linked chains eligible.");
            var publicEth = Profile(OnChainProviderTypes.PublicNodeEthereum);
            var publicBase = Profile(OnChainProviderTypes.BasePublic);
            usage.CreateScope(publicEth).QuotaExceededForTest();
            Check(!usage.IsBlocked(publicBase), "A public endpoint rejection paused an unrelated chain.");
        }

        var now = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        using (var usage = new OnChainProviderUsage(now: () => now))
        {
            var alchemy = Profile(OnChainProviderTypes.AlchemyEthereum);
            var drpc = Profile(OnChainProviderTypes.DrpcEthereum);
            var publicNode = Profile(OnChainProviderTypes.PublicNodeEthereum);
            var otherChain = Profile(OnChainProviderTypes.AlchemyBase);
            // Stale manually selected primary/order must not govern the new automatic policy.
            var document = new OnChainProviderConfigurationDocument
            {
                Configurations = [publicNode, alchemy, drpc, otherChain],
                SelectedConfigurationIds = new() { ["eip155:1"] = publicNode.Id },
                FallbackConfigurationIds = new() { ["eip155:1"] = [alchemy.Id] }
            };
            var service = new OnChainProviderConfigurationService(document, () => true, usage: usage);
            Check(Selected(service, alchemy)?.Id == alchemy.Id, "Automatic selection used a saved manual route.");
            var key = usage.Register(drpc).Single();
            var auto = usage.Snapshot(key);
            Check(auto.Automatic == false && !auto.Enabled && auto.Limit == 0,
                "An app-imposed guard was enabled.");
            // Explicit limits can influence selection; unknown account balances cannot.
            usage.Configure(usage.Register(alchemy).Single(), true, 100, true, now, 85);
            usage.Configure(key, true, 100, true, now, 0);
            usage.Configure(usage.Register(alchemy).Single(), true, 100, true, now, 0);
            usage.Configure(key, true, 100, true, now, 79);
            Check(Selected(service, alchemy)?.Id == drpc.Id, "A healthy route flapped before low headroom.");
            usage.CreateScope(drpc).Rpc("eth_getBlockByNumber");
            Check(Selected(service, alchemy)?.Id == alchemy.Id, "Low headroom did not select a configured alternative.");
            var alchemyKey = usage.Register(alchemy).Single();
            Check(alchemyKey == usage.Register(otherChain).Single(), "Linked chains received separate free allowances.");
            usage.Configure(alchemyKey, true, 26, true, now, 26);
            Check(Selected(service, alchemy)?.Id == drpc.Id, "A budget change did not re-evaluate all configured providers.");
            usage.CreateScope(drpc).QuotaExceededForTest();
            Check(Selected(service, alchemy)?.Id == publicNode.Id, "Exhausted private routes did not use PublicNode.");
            Check(Selected(service, otherChain) == null, "Shared quota/usage failed to pause the other chain.");
            now = now.AddDays(1);
            usage.Tick();
            Check(Selected(service, alchemy)?.Id != publicNode.Id, "Reset did not restore private eligibility.");
            Check(Selected(service, otherChain)?.Id == otherChain.Id, "Daily guard did not resume the linked chain.");
        }

        using (var usage = new OnChainProviderUsage())
        {
            var httpOnly = Profile(OnChainProviderTypes.AlchemyEthereum);
            var events = Profile(OnChainProviderTypes.QuickNodeEthereum);
            httpOnly.CapabilitySnapshot = new() { WebSocketLogs = OnChainProviderCapabilityState.Unsupported };
            var service = Service(usage, httpOnly, events);
            Check(Selected(service, httpOnly)?.Id == httpOnly.Id, "Polling required a WebSocket capability.");
            service.SetDemand(httpOnly.ChainNamespace, httpOnly.ChainId, events: true, wallet: false);
            Check(Selected(service, httpOnly)?.Id == events.Id, "Event demand used a known unsupported provider.");
            var grpc = Profile(OnChainProviderTypes.CustomYellowstone);
            var solana = Profile(OnChainProviderTypes.HeliusWebSocket);
            var walletService = Service(usage, grpc, solana);
            walletService.SetDemand(solana.ChainNamespace, solana.ChainId, events: true, wallet: true);
            Check(walletService.GetSelectedConfiguration()?.Id == solana.Id, "Wallet demand selected incompatible gRPC.");
            var unknown = usage.Snapshot(usage.Register(events).Single());
            Check(!unknown.Enabled && unknown.Limit == 0, "A trial/custom account balance was invented.");
        }

        using (var usage = new OnChainProviderUsage())
        {
            var privateProfile = Profile(OnChainProviderTypes.AlchemyBnb);
            var publicNode = Profile(OnChainProviderTypes.PublicNodeBnb);
            var online = true;
            var service = new OnChainProviderConfigurationService(new() { Configurations = [privateProfile, publicNode] },
                () => online, TimeSpan.FromMilliseconds(40), usage);
            online = false;
            Check(await service.TryFailoverAsync(privateProfile.Id, OnChainProviderFailureKind.Transport)
                == OnChainProviderFailoverOutcome.Ignored, "Offline routing consumed a provider failure.");
            online = true;
            await service.TryFailoverAsync(privateProfile.Id, OnChainProviderFailureKind.Transport);
            Check(Selected(service, privateProfile)?.Id == publicNode.Id, "Transient failure did not use PublicNode.");
            await Task.Delay(180);
            Check(Selected(service, privateProfile)?.Id == privateProfile.Id, "A recovered private route required manual activation.");
            await service.TryFailoverAsync(privateProfile.Id, OnChainProviderFailureKind.Authentication);
            await Task.Delay(180);
            Check(Selected(service, privateProfile)?.Id == publicNode.Id, "Rejected credentials were retried without correction.");
        }

        using (var usage = new OnChainProviderUsage(now: () => now))
        {
            var profile = Profile(OnChainProviderTypes.HeliusWebSocket);
            try { usage.CreateScope(profile).WebSocket(Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"credits exhausted\"}}")); }
            catch (OnChainUsageBudgetException) { }
            Check(usage.IsBlocked(profile), "WebSocket quota errors were ignored.");
            now = now.AddHours(1);
            usage.Tick();
            Check(!usage.IsBlocked(profile), "Unknown billing quota pause never expired.");
        }
        using (var usage = new OnChainProviderUsage(now: () => now))
        {
            var infura = Profile(OnChainProviderTypes.InfuraEthereum);
            usage.CreateScope(infura).QuotaExceededForTest();
            var group = usage.Snapshot(usage.Register(infura).Single());
            Check(usage.QuotaPauseUntil(infura) == new DateTimeOffset(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero),
                "Infura did not use its documented UTC reset.");
            var used = group.Used;
            usage.CreateScope(infura, validation: true).Rpc("eth_chainId");
            Check(usage.Snapshot(usage.Register(infura).Single()).Used == used + 5,
                "Explicit Save validation was not metered.");
            usage.CredentialValidated(infura);
            Check(!usage.IsBlocked(infura), "A successfully corrected credential kept its server quota pause.");
        }
        Console.WriteLine("PASS: 34 automatic presets, opt-in guards, shared headroom, quota/auth separation, mode compatibility, reset and private recovery.");
    }

    private static void QuotaExceededForTest(this OnChainUsageScope scope)
    {
        try { scope.QuotaExceeded(); }
        catch (OnChainUsageBudgetException) { }
    }

    private static OnChainProviderConfigurationService Service(OnChainProviderUsage usage, params OnChainProviderConfiguration[] profiles)
        => new(new() { Configurations = profiles }, () => true, usage: usage);
    private static OnChainProviderConfiguration? Selected(OnChainProviderConfigurationService service, OnChainProviderConfiguration profile)
        => service.GetSelectedConfiguration(profile.ChainNamespace, profile.ChainId);
    private static OnChainProviderConfiguration Profile(string type)
    {
        var preset = OnChainProviderCatalog.Get(type);
        var profile = OnChainProviderConfigurationStore.CreateConfiguration(preset);
        profile.RpcEndpoint = "https://fixture.invalid";
        profile.StreamEndpoint = preset.StreamTransport == OnChainStreamTransport.YellowstoneGrpc ? "https://fixture.invalid" : "wss://fixture.invalid";
        return profile;
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private sealed class ResponseHandler(int status, string? body = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent(body ?? "{\"error\":{\"code\":-32000,\"message\":\"fixture rejection\"}}") });
    }
}

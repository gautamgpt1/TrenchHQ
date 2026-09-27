using TrenchHQ.Core.OnChain;
using TrenchHQ.Core.OnChain.Evm;
using TrenchHQ.Core.OnChain.Solana;
using TrenchHQ.Core.Providers;
using TrenchHQ.Core.Wallets;
using TrenchHQ.Core.Widgets;
using TrenchHQ.Infrastructure.Diagnostics;
using TrenchHQ.Infrastructure.OnChain;
using TrenchHQ.Infrastructure.OnChain.Evm;
using TrenchHQ.Infrastructure.OnChain.Solana;
using TrenchHQ.Infrastructure.Providers;
using TrenchHQ.Yellowstone;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using TrenchHQ.TestSupport;
using static TrenchHQ.TestSupport.OnChainFixtures;

namespace TrenchHQ.OnChainValidation;

internal static partial class Validation
{
    static async Task VerifyLiveProviderCoverageAsync(
        OnChainEngineClient engine,
        string providerLabel,
        string? credentialEnvironmentVariable,
        string? solanaProviderType,
        (string ProviderType, EvmChainDefinition Chain)[] evmProviders,
        string? yellowstoneProviderType,
        string? streamEndpointEnvironmentVariable = null,
        string? rpcEndpointEnvironmentVariable = null,
        string? suppliedApiKey = null)
    {
        var apiKey = suppliedApiKey ?? (credentialEnvironmentVariable == null
            ? null
            : Environment.GetEnvironmentVariable(credentialEnvironmentVariable));
        if (credentialEnvironmentVariable != null && string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"Live {providerLabel} validation requires {credentialEnvironmentVariable} in the current process.");
        }

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var requestedChainId = Environment.GetEnvironmentVariable("TRENCHHQ_LIVE_EVM_CHAIN_ID");
        var providerPhase = Environment.GetEnvironmentVariable("TRENCHHQ_LIVE_PROVIDER_PHASE") ?? "all";
        var streamEndpointOverride = streamEndpointEnvironmentVariable == null
            ? null
            : Environment.GetEnvironmentVariable(streamEndpointEnvironmentVariable);
        var rpcEndpointOverride = rpcEndpointEnvironmentVariable == null
            ? null
            : Environment.GetEnvironmentVariable(rpcEndpointEnvironmentVariable);
        if (providerPhase is not ("all" or "protocols"))
        {
            throw new InvalidOperationException(
                "TRENCHHQ_LIVE_PROVIDER_PHASE must be either 'all' or 'protocols'.");
        }
        if (string.IsNullOrWhiteSpace(requestedChainId)
            && solanaProviderType != null)
        {
        var requiredApiKey = apiKey!;
        var solanaPreset = OnChainProviderCatalog.Get(solanaProviderType);
        var solanaConfiguration = CreateProviderConfiguration(
            solanaPreset.ProviderType,
            solanaPreset.DefaultStreamEndpoint,
            solanaPreset.DefaultRpcEndpoint);
        var solanaAvailable = true;
        if (providerPhase == "all")
        {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await new SolanaProviderCapabilityProbe(httpClient)
                .ProbeAsync(solanaConfiguration, requiredApiKey, timeout.Token);
        }
        catch (SolanaRpcException exception) when (exception.RpcCode == 35)
        {
            solanaAvailable = false;
            Console.WriteLine(
                $"LIVE {providerLabel} | Solana | unavailable for this account plan | RPC {exception.RpcCode}");
        }
        }
        if (solanaAvailable)
        {
            Console.WriteLine(providerPhase == "all"
                ? $"LIVE {providerLabel} | Solana standard RPC and slotSubscribe PASS"
                : $"LIVE {providerLabel} | Solana | protocol-only phase");
            if (providerPhase == "all" && yellowstoneProviderType != null)
            {
                var yellowstonePreset = OnChainProviderCatalog.Get(yellowstoneProviderType);
                var yellowstoneConfiguration = CreateProviderConfiguration(
                    yellowstonePreset.ProviderType,
                    yellowstonePreset.DefaultStreamEndpoint,
                    yellowstonePreset.DefaultRpcEndpoint);
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await new SolanaProviderCapabilityProbe(httpClient)
                        .ProbeAsync(yellowstoneConfiguration, requiredApiKey, timeout.Token);
                    Console.WriteLine(
                        $"LIVE {providerLabel} | Solana Yellowstone | RPC and gRPC subscription PASS");
                }
                catch (Grpc.Core.RpcException exception) when (
                    exception.StatusCode is Grpc.Core.StatusCode.Unauthenticated
                        or Grpc.Core.StatusCode.PermissionDenied)
                {
                    Console.WriteLine(
                        $"LIVE {providerLabel} | Solana Yellowstone | unavailable for this app entitlement | {exception.StatusCode}");
                }
                catch (Grpc.Core.RpcException exception) when (
                    exception.StatusCode == Grpc.Core.StatusCode.Unimplemented)
                {
                    Console.WriteLine(
                        $"LIVE {providerLabel} | Solana Yellowstone | unavailable for this app or endpoint routing | {exception.StatusCode}");
                }
            }

            var solanaFixtures = new[]
            {
            new LiveSolanaPoolFixture(
                OnChainProtocolIds.PumpBondingCurve,
                "7K1Y3AiXT5Pf16mhRV9FJU4WQcCFWJ8cdjAyqaATRKQN",
                "Coyj3LtKn1BNSgWc9HsGK5SKoGfEoDaymig4wrN6pump"),
            new LiveSolanaPoolFixture(
                OnChainProtocolIds.PumpSwap,
                "H739cDUEhbW2ckYhjetYSwxTVT5vd6GrTW5GdfaTQJgB",
                "Coyj3LtKn1BNSgWc9HsGK5SKoGfEoDaymig4wrN6pump"),
            new LiveSolanaPoolFixture(
                OnChainProtocolIds.RaydiumAmmV4,
                "58oQChx4yWmvKdwLLZzBi4ChoCc2fqCUWBkwMihLYQo2",
                "So11111111111111111111111111111111111111112"),
            new LiveSolanaPoolFixture(
                OnChainProtocolIds.RaydiumCpmm,
                "7JuwJuNU88gurFnyWeiyGKbFmExMWcmRZntn9imEzdny",
                "So11111111111111111111111111111111111111112"),
            new LiveSolanaPoolFixture(
                OnChainProtocolIds.RaydiumClmm,
                "BZtgQEyS6eXUXicYPHecYQ7PybqodXQMvkjUbP4R8mUU",
                "So11111111111111111111111111111111111111112"),
            new LiveSolanaPoolFixture(
                OnChainProtocolIds.MeteoraDammV1,
                "4SBYWY5UuxybWuj8FwHdFXUN6mbtACrqbJwiZ9mXworP",
                "So11111111111111111111111111111111111111112"),
            new LiveSolanaPoolFixture(
                OnChainProtocolIds.MeteoraDammV2,
                "8Pm2kZpnxD3hoMmt4bjStX2Pw2Z9abpbHzZxMPqxPmie",
                "So11111111111111111111111111111111111111112"),
            new LiveSolanaPoolFixture(
                OnChainProtocolIds.MeteoraDlmm,
                "5rCf1DM8LjKTw4YqhnoLcngyZYeNnQqztScTogYHAS6",
                "So11111111111111111111111111111111111111112"),
            new LiveSolanaPoolFixture(
                OnChainProtocolIds.OrcaWhirlpool,
                "2pnrqjuJ7H1xgAMo1PRSBHyHJykGHd6byVoMTdDXDAau",
                "So11111111111111111111111111111111111111112"),
            new LiveSolanaPoolFixture(
                OnChainProtocolIds.ManifestOrderbook,
                "CZFtRhXpXiAfkfNQ7yJURPyhXHWkZgonwbXWQREPTQbX",
                "E6ifp2mJy8cYQehUGUtFvrXriRKxRuonLmrvTFypump")
            };
            var solanaRpc = new SolanaRpcClient(httpClient, solanaConfiguration, apiKey);
            var accounts = await solanaRpc.GetMultipleAccountsAsync(
                solanaFixtures.Select(static fixture => fixture.Address).ToArray(),
                OnChainCommitment.Confirmed,
                CancellationToken.None);
            for (var index = 0; index < solanaFixtures.Length; index++)
            {
                var fixture = solanaFixtures[index];
                var account = accounts[index]
                              ?? throw new InvalidOperationException(
                                  $"{providerLabel} did not return the live {fixture.ProtocolId} account.");
                var decoded = await engine.DecodePoolAccountAsync(
                    fixture.ProtocolId,
                    fixture.Address,
                    fixture.SelectedMint,
                    account.OwnerProgram,
                    account.DataBase64);
                AssertEqual(fixture.ProtocolId, decoded.ProtocolId,
                    $"{providerLabel} live account decoding returned the wrong {fixture.ProtocolId} protocol.");
                Console.WriteLine(
                    $"LIVE {providerLabel} | Solana | {fixture.ProtocolId} | account and decoder PASS");
            }
        }
        }

        if (evmProviders.Length == 0)
        {
            return;
        }

        var selectedEvmProviders = string.IsNullOrWhiteSpace(requestedChainId)
            ? evmProviders
            : evmProviders.Where(provider => provider.Chain.ChainId == requestedChainId.Trim()).ToArray();
        if (selectedEvmProviders.Length == 0)
        {
            throw new InvalidOperationException(
                $"No {providerLabel} live provider is configured for chain {requestedChainId}.");
        }
        foreach (var (providerType, chain) in selectedEvmProviders)
        {
            var preset = OnChainProviderCatalog.Get(providerType);
            var configuration = CreateProviderConfiguration(
                preset.ProviderType,
                string.IsNullOrWhiteSpace(streamEndpointOverride)
                    ? preset.DefaultStreamEndpoint
                    : streamEndpointOverride.Trim(),
                string.IsNullOrWhiteSpace(rpcEndpointOverride)
                    ? preset.DefaultRpcEndpoint
                    : rpcEndpointOverride.Trim());
            var rpc = new EvmJsonRpcClient(httpClient, configuration, apiKey);
            if (providerPhase == "all")
            {
            var snapshot = await RetryLiveTransientResultAsync(
                () => new EvmProviderCapabilityProbe(httpClient).ProbeAsync(configuration, apiKey),
                $"{providerLabel} {chain.DisplayName} capability probe");
            Console.WriteLine(
                $"LIVE {providerLabel} | {chain.DisplayName} | chain={snapshot.ChainId} | safe={snapshot.SafeBlock} | finalized={snapshot.FinalizedBlock} | blockCall={snapshot.BlockHashCall} | blockLogs={snapshot.BlockHashLogs} | batch={snapshot.Batch} | heads={snapshot.WebSocketHeads} | logs={snapshot.WebSocketLogs}");
            AssertEqual(OnChainProviderCapabilityState.Supported, snapshot.ChainId,
                $"{providerLabel} {chain.DisplayName} returned the wrong chain.");
            AssertEqual(OnChainProviderCapabilityState.Supported, snapshot.BlockHashCall,
                $"{providerLabel} {chain.DisplayName} rejected block-pinned calls.");
            AssertEqual(OnChainProviderCapabilityState.Unknown, snapshot.WebSocketHeads,
                $"{providerLabel} {chain.DisplayName} unexpectedly probed new-head subscriptions.");
            AssertEqual(OnChainProviderCapabilityState.Supported, snapshot.WebSocketLogs,
                $"{providerLabel} {chain.DisplayName} rejected log subscriptions.");
            if (new[]
                {
                    snapshot.SafeBlock,
                    snapshot.FinalizedBlock,
                    snapshot.BlockHashLogs,
                    snapshot.Batch
                }.Contains(OnChainProviderCapabilityState.RateLimited))
            {
                Console.WriteLine(
                    $"LIVE COOLDOWN | {providerLabel} {chain.DisplayName} | optional capability was rate limited");
                await Task.Delay(TimeSpan.FromSeconds(20));
            }
            }
            else
            {
                Console.WriteLine(
                    $"LIVE {providerLabel} | {chain.DisplayName} | protocol-only phase");
            }
            var block = await RetryLiveTransientResultAsync(
                () => rpc.GetBlockAsync("latest", CancellationToken.None),
                $"{providerLabel} {chain.DisplayName} latest block");
            var blockReference = new { blockHash = block.Hash, requireCanonical = true };
            await Task.Delay(TimeSpan.FromSeconds(3));
            var requestedProtocol = Environment.GetEnvironmentVariable("TRENCHHQ_LIVE_PROTOCOL_FILTER");
            var protocolFixtures = LiveEvmProtocolFixtures(chain.ChainId);
            var selectedProtocols = string.IsNullOrWhiteSpace(requestedProtocol)
                ? protocolFixtures
                : protocolFixtures.Where(protocol => protocol.Name.Contains(
                    requestedProtocol.Trim(),
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            if (selectedProtocols.Length == 0)
            {
                throw new InvalidOperationException(
                    $"No live protocol fixture matched '{requestedProtocol}' on {chain.DisplayName}.");
            }
            foreach (var protocol in selectedProtocols)
            {
                await RetryLiveTransientAsync(
                    () => VerifyLiveEvmProtocolAsync(
                        rpc,
                        protocol,
                        blockReference,
                        providerLabel,
                        CancellationToken.None),
                    $"{providerLabel} {chain.DisplayName} {protocol.Name}");
                Console.WriteLine(
                    $"LIVE {providerLabel} | {chain.DisplayName} | {protocol.Name} | pinned state PASS");
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }
}

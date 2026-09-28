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
using Xunit;
using TrenchHQ.TestSupport;
using static TrenchHQ.TestSupport.OnChainFixtures;

namespace TrenchHQ.OnChainIntegrationTests;

public partial class OnChainTests
{
    private static string EnginePath => Path.Combine(AppContext.BaseDirectory, "OnChainEngine", "trenchhq-onchain-engine.exe");

    [Fact]
    public void YellowstoneNormalization()
    {
        VerifyYellowstoneNormalization();
    }

    [Fact]
    public async Task ProviderRouteRecoveryAsync()
    {
        await VerifyProviderRouteRecoveryAsync();
    }

    [Fact]
    public async Task ProviderCachePoliciesAsync()
    {
        await VerifyProviderCachePoliciesAsync();
    }

    [Fact]
    public async Task CredentialProtectionAsync()
    {
        await VerifyCredentialProtectionAsync();
    }

    [Fact]
    public async Task ProviderConfigurationsAndRpcAuthenticationAsync()
    {
        await VerifyProviderConfigurationsAndRpcAuthenticationAsync();
    }

    [Fact]
    public async Task EvmProviderAndTransportContractsAsync()
    {
        await VerifyEvmProviderAndTransportContractsAsync();
    }

    [Fact]
    public async Task EvmRecoveryPrimitivesAsync()
    {
        await VerifyEvmRecoveryPrimitivesAsync();
    }

    [Fact]
    public void EvmMulticall3Codec()
    {
        VerifyEvmMulticall3Codec();
    }

    [Fact]
    public async Task UniswapV2DiscoveryAndEngineAsync()
    {
        await VerifyUniswapV2DiscoveryAndEngineAsync(EnginePath);
    }

    [Fact]
    public async Task BaseUniswapDiscoveryAsync()
    {
        await VerifyBaseUniswapDiscoveryAsync();
    }

    [Fact]
    public async Task BaseUniswapV4DiscoveryAsync()
    {
        await VerifyBaseUniswapV4DiscoveryAsync();
    }

    [Fact]
    public async Task BaseAerodromeDiscoveryAndEngineAsync()
    {
        await VerifyBaseAerodromeDiscoveryAndEngineAsync(EnginePath);
    }

    [Fact]
    public async Task BasePancakeDiscoveryAndEngineAsync()
    {
        await VerifyBasePancakeDiscoveryAndEngineAsync(EnginePath);
    }

    [Fact]
    public async Task BnbPancakeDiscoveryAndEngineAsync()
    {
        await VerifyBnbPancakeDiscoveryAndEngineAsync(EnginePath);
    }

    [Fact]
    public async Task RobinhoodStockTokenCatalogAsync()
    {
        await VerifyRobinhoodStockTokenCatalogAsync();
    }

    [Fact]
    public async Task RobinhoodUniswapDiscoveryAndEngineAsync()
    {
        await VerifyRobinhoodUniswapDiscoveryAndEngineAsync(EnginePath);
    }

    [Fact]
    public async Task EvmCoordinatorCollectionAsync()
    {
        await VerifyEvmCoordinatorCollectionAsync(EnginePath);
    }

    [Fact]
    public async Task MixedChainWatchSetMatrixAsync()
    {
        await VerifyMixedChainWatchSetMatrixAsync(EnginePath);
    }

    [Fact]
    public async Task CatalogProtocolDiscoveryAndEngineAsync()
    {
        await VerifyCatalogProtocolDiscoveryAndEngineAsync(EnginePath);
    }

    [Fact]
    public void CurveCatalogParsing()
    {
        VerifyCurveCatalogParsing();
    }

    [Fact]
    public async Task CurveDiscoveryAsync()
    {
        await VerifyCurveDiscoveryAsync();
    }

    [Fact]
    public async Task CatalogTimeoutIsolationAsync()
    {
        await VerifyCatalogTimeoutIsolationAsync();
    }

    [Fact]
    public async Task EthereumReferencePricingAsync()
    {
        await VerifyEthereumReferencePricingAsync(EnginePath);
    }

    [Fact]
    public async Task StandardWebSocketTransactionFetchAsync()
    {
        await VerifyStandardWebSocketTransactionFetchAsync();
    }

    [Fact]
    public async Task SolanaWalletTransactionFetchAsync()
    {
        await VerifySolanaWalletTransactionFetchAsync();
    }

    [Fact]
    public async Task WalletSignatureHistoryAsync()
    {
        await VerifyWalletSignatureHistoryAsync();
    }

    [Fact]
    public async Task WalletCompletionRpcMethodsAsync()
    {
        await VerifyWalletCompletionRpcMethodsAsync();
    }

    [Fact]
    public async Task PoolDiscoveryAsync()
    {
        var client = new OnChainEngineClient(EnginePath);
        try { await VerifyPoolDiscoveryAsync(client); }
        finally { await client.StopAsync(); }
    }

    [Fact]
    public async Task SolanaPriceSamplingAsync()
    {
        await VerifySolanaPriceSamplingAsync(EnginePath);
    }

    [Fact]
    public async Task InfuraKnownHeaderAsync()
    {
        await VerifyInfuraKnownHeaderAsync();
    }

    [Fact]
    public async Task TickerProviderMatrixAsync()
    {
        await VerifyTickerProviderMatrixAsync(EnginePath);
    }

    [Fact]
    public async Task MixedSourceFailureAsync()
    {
        await VerifyMixedSourceFailureAsync();
    }

    [Fact]
    public async Task StreamCoordinatorAsync()
    {
        await VerifyStreamCoordinatorAsync(EnginePath);
    }

    [Fact]
    public async Task EvmStreamCoordinatorAsync()
    {
        await VerifyEvmStreamCoordinatorAsync(EnginePath);
    }

    [Fact]
    public async Task HiddenEvmReferenceSamplingAsync()
    {
        await VerifyHiddenEvmReferenceSamplingAsync(EnginePath);
    }

    [Fact]
    public async Task RobinhoodSampledHeadCoordinatorAsync()
    {
        await VerifyRobinhoodSampledHeadCoordinatorAsync(EnginePath);
    }

    [Fact]
    public async Task PipelineBenchmarksAsync()
    {
        await VerifyPipelineBenchmarksAsync(EnginePath);
    }

    [Fact]
    public Task UsageBudgets() => UsageBudgetTests.RunAsync();

    [Fact]
    public void AlchemyWalletReceiptsKeepSharedSolanaRouteAvailable()
    {
        var ethereum = CreateProviderConfiguration(OnChainProviderTypes.AlchemyEthereum,
            "wss://fixture.invalid", "https://fixture.invalid");
        var solana = CreateProviderConfiguration(OnChainProviderTypes.AlchemyWebSocket,
            "wss://fixture.invalid", "https://fixture.invalid");
        using var usage = new OnChainProviderUsage();
        usage.Record(ethereum, "eth_getBlockReceipts");

        var snapshot = usage.Snapshot(OnChainProviderUsage.GroupKey(ethereum));
        AssertEqual(20m, snapshot.Used, "Alchemy block receipts cost changed.");
        Assert(!snapshot.UnknownCost, "Wallet receipts were treated as an unknown Alchemy method.");
        Assert(!usage.IsBlocked(solana), "Wallet receipts paused the shared Solana route.");

        usage.Record(solana, "getTokenAccountsByOwner");
        AssertEqual(30m, usage.Snapshot(OnChainProviderUsage.GroupKey(solana)).Used,
            "Solana wallet account discovery used the wrong Alchemy CU estimate.");

        usage.Configure(OnChainProviderUsage.GroupKey(ethereum), true, 100, false, DateTimeOffset.UtcNow, 30);
        Xunit.Assert.Throws<OnChainUsageBudgetException>(() => usage.Record(ethereum, "eth_unknownMethod"));
        Assert(usage.IsBlocked(solana), "A genuinely unknown method no longer pauses the shared route.");
    }

    [Fact]
    public Task LiveSolanaWallets() => LiveWalletTests.RunAsync();

    [Fact]
    public Task LiveEvmWallets() => LiveWalletTests.EvmAsync();

    [Fact]
    public Task AutomaticRoutes() => AutomaticRoutingTests.RunAsync();

    [Fact]
    public Task BatchedTickerSnapshots() => TickerBatchTests.RunAsync(EnginePath);

    [Fact]
    public void DiscountedHeadProbesRequireDocumentedProviderSavings()
    {
        foreach (var preset in OnChainProviderCatalog.Presets.Where(preset =>
                     preset.StreamTransport == OnChainStreamTransport.EvmWebSocket))
        {
            AssertEqual(preset.ProviderFamily == "alchemy",
                EvmStreamCoordinator.UsesDiscountedHeadProbe(preset.ProviderType),
                "Block-number probes must have a documented cost advantage.");
        }
    }
}

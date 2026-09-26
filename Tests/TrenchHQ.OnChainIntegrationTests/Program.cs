using TrenchHQ.Helpers;
using TrenchHQ.Models;
using TrenchHQ.Yellowstone;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

if (args is ["--ticker-public-batch"])
{
    await TickerBatchTests.VerifyPublicAsync();
    return 0;
}

if (args is ["--ticker-usage"])
{
    await UsageBudgetTests.RunAsync();
    await AutomaticRoutingTests.RunAsync();
    await TickerBatchTests.RunAsync(Path.Combine(AppContext.BaseDirectory, "OnChainEngine", "trenchhq-onchain-engine.exe"));
    return 0;
}

if (args is ["--ticker-provider-matrix"])
{
    await UsageBudgetTests.RunAsync();
    await AutomaticRoutingTests.RunAsync();
    await VerifyInfuraKnownHeaderAsync();
    await VerifyProviderRouteRecoveryAsync();
    await VerifyTickerProviderMatrixAsync(Path.Combine(
        AppContext.BaseDirectory, "OnChainEngine", "trenchhq-onchain-engine.exe"));
    return 0;
}

if (args is ["--ticker-public-poll"])
{
    var path = Path.Combine(AppContext.BaseDirectory, "OnChainEngine", "trenchhq-onchain-engine.exe");
    await VerifyPublicTickerPollingAsync(path);
    return 0;
}

if (args is ["--store-dedicated-alchemy-key"])
{
    Console.WriteLine("Enter the dedicated Alchemy app key (input hidden), then press Enter.");
    var credentialCharacters = new List<char>();
    while (true)
    {
        var input = Console.ReadKey(intercept: true);
        if (input.Key == ConsoleKey.Enter)
        {
            break;
        }
        if (input.Key == ConsoleKey.Backspace)
        {
            if (credentialCharacters.Count > 0)
            {
                credentialCharacters.RemoveAt(credentialCharacters.Count - 1);
            }
        }
        else if (!char.IsControl(input.KeyChar))
        {
            credentialCharacters.Add(input.KeyChar);
        }
    }
    var credential = new string([.. credentialCharacters]);
    credentialCharacters.Clear();
    if (string.IsNullOrWhiteSpace(credential))
    {
        throw new InvalidOperationException("A dedicated Alchemy app key is required on standard input.");
    }
    var root = GetDedicatedAlchemyTestRoot();
    if (Directory.Exists(root))
    {
        throw new InvalidOperationException("The isolated Alchemy test configuration already exists.");
    }
    var configuration = OnChainProviderConfigurationStore.CreateConfiguration(
        OnChainProviderCatalog.Get(OnChainProviderTypes.AlchemyWebSocket));
    await OnChainProviderConfigurationStore.SaveCredentialAsync(
        root, configuration.CredentialReference, credential);
    await OnChainProviderConfigurationStore.SaveAsync(root,
        new OnChainProviderConfigurationDocument { Configurations = [configuration] });
    Console.WriteLine("Dedicated Alchemy test key saved in an isolated TrenchHQ DPAPI configuration.");
    return 0;
}

if (args is ["--live-dedicated-alchemy-coverage"])
{
    var root = GetDedicatedAlchemyTestRoot();
    var configuration = (await OnChainProviderConfigurationStore.LoadAsync(root))
        .Configurations.Single(item => item.ProviderType == OnChainProviderTypes.AlchemyWebSocket);
    var credential = await OnChainProviderConfigurationStore.ReadCredentialAsync(
        root, configuration.CredentialReference)
        ?? throw new InvalidOperationException("The dedicated Alchemy test credential is missing.");
    var coverageEnginePath = Path.Combine(AppContext.BaseDirectory, "OnChainEngine", "trenchhq-onchain-engine.exe");
    var engine = new OnChainEngineClient(coverageEnginePath);
    try
    {
        await VerifyLiveProviderCoverageAsync(engine, "Alchemy", null,
            OnChainProviderTypes.AlchemyWebSocket,
            [
                (OnChainProviderTypes.AlchemyEthereum, EvmChainDefinitions.EthereumMainnet),
                (OnChainProviderTypes.AlchemyBase, EvmChainDefinitions.BaseMainnet),
                (OnChainProviderTypes.AlchemyBnb, EvmChainDefinitions.BnbMainnet),
                (OnChainProviderTypes.AlchemyRobinhood, EvmChainDefinitions.RobinhoodMainnet)
            ],
            OnChainProviderTypes.AlchemyYellowstone,
            suppliedApiKey: credential);
        Console.WriteLine("PASS: dedicated Alchemy live provider coverage.");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception.ToString().Replace(
            credential, "[redacted]", StringComparison.Ordinal));
        return 1;
    }
    finally
    {
        await engine.StopAsync();
    }
}

if (args.Length is 3 or 4 && args[0] == "--live-dedicated-alchemy-matrix")
{
    var matrixScenario = args[1];
    var durationText = args[2];
    var webSocket = args.Length == 4 && args[3] == "websocket";
    if (matrixScenario is not ("solana" or "robinhood" or "bnb" or "ethereum" or "base"
            or "2" or "3" or "5" or "5-stock")
        || !int.TryParse(durationText, out var durationSeconds)
        || durationSeconds is < 30 or > 300
        || args.Length == 4 && !webSocket && args[3] != "poll")
    {
        throw new ArgumentException("Use one chain, 2, 3, 5, or 5-stock, a 30-300 second duration, and poll or websocket.");
    }
    var root = GetDedicatedAlchemyTestRoot();
    var configuration = (await OnChainProviderConfigurationStore.LoadAsync(root))
        .Configurations.Single(item => item.ProviderType == OnChainProviderTypes.AlchemyWebSocket);
    var credential = await OnChainProviderConfigurationStore.ReadCredentialAsync(
        root, configuration.CredentialReference)
        ?? throw new InvalidOperationException("The dedicated Alchemy test credential is missing.");
    var matrixEnginePath = Path.Combine(AppContext.BaseDirectory, "OnChainEngine", "trenchhq-onchain-engine.exe");
    try
    {
        await VerifyDedicatedAlchemyMixedMatrixAsync(
            matrixEnginePath, credential, matrixScenario, TimeSpan.FromSeconds(durationSeconds), webSocket);
        Console.WriteLine("PASS: dedicated Alchemy live mixed-chain matrix.");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception.ToString().Replace(
            credential, "[redacted]", StringComparison.Ordinal));
        return 1;
    }
}

if (args is ["--live-dedicated-alchemy-solana-snapshot"]
    or ["--live-dedicated-alchemy-solana-snapshot", _])
{
    var sampleSeconds = args.Length == 2 && int.TryParse(args[1], out var seconds)
        ? seconds : 0;
    var sampleIntervalSeconds = (int)SolanaRpcSampleStreamSource.SampleInterval.TotalSeconds;
    if (args.Length == 2 && (sampleSeconds is < 30 or > 300
                             || sampleSeconds % sampleIntervalSeconds != 0))
    {
        throw new ArgumentException("Use a 30-300 second duration divisible by the sample interval.");
    }
    var root = GetDedicatedAlchemyTestRoot();
    var configuration = (await OnChainProviderConfigurationStore.LoadAsync(root))
        .Configurations.Single(item => item.ProviderType == OnChainProviderTypes.AlchemyWebSocket);
    var credential = await OnChainProviderConfigurationStore.ReadCredentialAsync(
        root, configuration.CredentialReference)
        ?? throw new InvalidOperationException("The dedicated Alchemy test credential is missing.");
    var snapshotEnginePath = Path.Combine(AppContext.BaseDirectory, "OnChainEngine", "trenchhq-onchain-engine.exe");
    try
    {
        await VerifyDedicatedAlchemySolanaSnapshotAsync(
            snapshotEnginePath, credential, sampleSeconds);
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception.ToString().Replace(
            credential, "[redacted]", StringComparison.Ordinal));
        return 1;
    }
}

if (string.Equals(
        Environment.GetEnvironmentVariable("TRENCHHQ_ONLY_LIVE_ROBINHOOD_POOL_TESTS"),
        "1",
        StringComparison.Ordinal))
{
    await VerifyLiveRobinhoodDiscoveryAsync();
    Console.WriteLine("PASS: focused live Robinhood Chain pool validation.");
    return 0;
}

var enginePath = Path.Combine(AppContext.BaseDirectory, "OnChainEngine", "trenchhq-onchain-engine.exe");
var client = new OnChainEngineClient(enginePath);
var states = new ConcurrentQueue<OnChainEngineState>();
var prices = new ConcurrentQueue<OnChainPriceUpdate>();

client.StateChanged += (_, args) => states.Enqueue(args.State);
client.PriceUpdated += (_, args) => prices.Enqueue(args.Update);

try
{
    await UsageBudgetTests.RunAsync();
    await AutomaticRoutingTests.RunAsync();
    await TickerBatchTests.RunAsync(enginePath);
    VerifyYellowstoneNormalization();
    foreach (var preset in OnChainProviderCatalog.Presets.Where(preset =>
                 preset.StreamTransport == OnChainStreamTransport.EvmWebSocket))
    {
        AssertEqual(preset.ProviderFamily == "alchemy",
            EvmStreamCoordinator.UsesDiscountedHeadProbe(preset.ProviderType),
            "Block-number probes must have a documented cost advantage.");
    }
    await VerifyProviderRouteRecoveryAsync();
    await VerifyProviderCachePoliciesAsync();
    await VerifyCredentialProtectionAsync();
    await VerifyProviderConfigurationsAndRpcAuthenticationAsync();
    await VerifyEvmProviderAndTransportContractsAsync();
    await VerifyEvmRecoveryPrimitivesAsync();
    VerifyEvmMulticall3Codec();
    await VerifyUniswapV2DiscoveryAndEngineAsync(enginePath);
    await VerifyBaseUniswapDiscoveryAsync();
    await VerifyBaseUniswapV4DiscoveryAsync();
    await VerifyBaseAerodromeDiscoveryAndEngineAsync(enginePath);
    await VerifyBasePancakeDiscoveryAndEngineAsync(enginePath);
    await VerifyBnbPancakeDiscoveryAndEngineAsync(enginePath);
    await VerifyRobinhoodStockTokenCatalogAsync();
    await VerifyRobinhoodUniswapDiscoveryAndEngineAsync(enginePath);
    await VerifyEvmCoordinatorCollectionAsync(enginePath);
    await VerifyMixedChainWatchSetMatrixAsync(enginePath);
    await VerifyCatalogProtocolDiscoveryAndEngineAsync(enginePath);
    VerifyCurveCatalogParsing();
    await VerifyCurveDiscoveryAsync();
    await VerifyCatalogTimeoutIsolationAsync();
    var runLiveEvmDiscovery = string.Equals(
        Environment.GetEnvironmentVariable("TRENCHHQ_RUN_LIVE_EVM_POOL_TESTS"),
        "1",
        StringComparison.Ordinal);
    var runLiveEvmStreams = string.Equals(
        Environment.GetEnvironmentVariable("TRENCHHQ_RUN_LIVE_EVM_STREAM_TESTS"),
        "1",
        StringComparison.Ordinal);
    var runLiveBaseStreams = string.Equals(
        Environment.GetEnvironmentVariable("TRENCHHQ_RUN_LIVE_BASE_STREAM_TESTS"),
        "1",
        StringComparison.Ordinal);
    var runLiveMixedMatrix = string.Equals(
        Environment.GetEnvironmentVariable("TRENCHHQ_RUN_LIVE_MIXED_MATRIX_TESTS"),
        "1",
        StringComparison.Ordinal);
    var runLiveRobinhoodDiscovery = string.Equals(
        Environment.GetEnvironmentVariable("TRENCHHQ_RUN_LIVE_ROBINHOOD_POOL_TESTS"),
        "1",
        StringComparison.Ordinal);
    var runLivePublicEvmValidation = string.Equals(
        Environment.GetEnvironmentVariable("TRENCHHQ_RUN_LIVE_PUBLIC_EVM_TESTS"),
        "1",
        StringComparison.Ordinal);
    var runLiveWalletValidation = string.Equals(
        Environment.GetEnvironmentVariable("TRENCHHQ_RUN_LIVE_WALLET_TESTS"),
        "1",
        StringComparison.Ordinal);
    var runLiveSolanaWalletValidation = string.Equals(
        Environment.GetEnvironmentVariable("TRENCHHQ_RUN_LIVE_SOLANA_WALLET_TESTS"),
        "1",
        StringComparison.Ordinal);
    var runLiveAlchemyValidation = string.Equals(
        Environment.GetEnvironmentVariable("TRENCHHQ_RUN_LIVE_ALCHEMY_TESTS"),
        "1",
        StringComparison.Ordinal);
    var runLiveDrpcValidation = string.Equals(
        Environment.GetEnvironmentVariable("TRENCHHQ_RUN_LIVE_DRPC_TESTS"),
        "1",
        StringComparison.Ordinal);
    var runLiveChainstackValidation = string.Equals(
        Environment.GetEnvironmentVariable("TRENCHHQ_RUN_LIVE_CHAINSTACK_TESTS"),
        "1",
        StringComparison.Ordinal);
    var runLiveQuickNodeEthereumValidation = string.Equals(
        Environment.GetEnvironmentVariable("TRENCHHQ_RUN_LIVE_QUICKNODE_ETHEREUM_TESTS"),
        "1",
        StringComparison.Ordinal);
    var runLiveHeliusValidation = string.Equals(
        Environment.GetEnvironmentVariable("TRENCHHQ_RUN_LIVE_HELIUS_TESTS"),
        "1",
        StringComparison.Ordinal);
    var runLiveShyftValidation = string.Equals(
        Environment.GetEnvironmentVariable("TRENCHHQ_RUN_LIVE_SHYFT_TESTS"),
        "1",
        StringComparison.Ordinal);
    var runLiveInfuraValidation = string.Equals(
        Environment.GetEnvironmentVariable("TRENCHHQ_RUN_LIVE_INFURA_TESTS"),
        "1",
        StringComparison.Ordinal);
    await VerifyEthereumReferencePricingAsync(enginePath);
    await VerifyStandardWebSocketTransactionFetchAsync();
    await VerifySolanaWalletTransactionFetchAsync();
    await VerifyWalletSignatureHistoryAsync();
    await VerifyWalletCompletionRpcMethodsAsync();
    if (runLiveSolanaWalletValidation)
    {
        await VerifyLivePublicSolanaWalletTransactionAsync();
    }
    if (runLiveWalletValidation)
    {
        await VerifyLivePublicWalletRecoveryAsync();
    }
    var existingProcessIds = GetEngineProcessIds();
    await VerifyPoolDiscoveryAsync(client);
    if (runLiveAlchemyValidation)
    {
        await VerifyLiveProviderCoverageAsync(
            client,
            "Alchemy",
            "TRENCHHQ_ALCHEMY_API_KEY",
            OnChainProviderTypes.AlchemyWebSocket,
            [
                (OnChainProviderTypes.AlchemyEthereum, EvmChainDefinitions.EthereumMainnet),
                (OnChainProviderTypes.AlchemyBase, EvmChainDefinitions.BaseMainnet),
                (OnChainProviderTypes.AlchemyBnb, EvmChainDefinitions.BnbMainnet),
                (OnChainProviderTypes.AlchemyRobinhood, EvmChainDefinitions.RobinhoodMainnet)
            ],
            OnChainProviderTypes.AlchemyYellowstone);
    }
    if (runLiveDrpcValidation)
    {
        await VerifyLiveProviderCoverageAsync(
            client,
            "dRPC",
            "TRENCHHQ_DRPC_API_KEY",
            OnChainProviderTypes.DrpcWebSocket,
            [
                (OnChainProviderTypes.DrpcEthereum, EvmChainDefinitions.EthereumMainnet),
                (OnChainProviderTypes.DrpcBase, EvmChainDefinitions.BaseMainnet),
                (OnChainProviderTypes.DrpcBnb, EvmChainDefinitions.BnbMainnet)
            ],
            null);
    }
    if (runLiveChainstackValidation)
    {
        await VerifyLiveProviderCoverageAsync(
            client,
            "Chainstack",
            "TRENCHHQ_CHAINSTACK_API_KEY",
            null,
            [
                (OnChainProviderTypes.ChainstackEthereum, EvmChainDefinitions.EthereumMainnet)
            ],
            null,
            "TRENCHHQ_CHAINSTACK_STREAM_ENDPOINT",
            "TRENCHHQ_CHAINSTACK_RPC_ENDPOINT");
    }
    if (runLiveQuickNodeEthereumValidation)
    {
        await VerifyLiveProviderCoverageAsync(
            client,
            "QuickNode",
            "TRENCHHQ_QUICKNODE_ETHEREUM_API_KEY",
            null,
            [
                (OnChainProviderTypes.QuickNodeEthereum, EvmChainDefinitions.EthereumMainnet)
            ],
            null,
            "TRENCHHQ_QUICKNODE_ETHEREUM_STREAM_ENDPOINT",
            "TRENCHHQ_QUICKNODE_ETHEREUM_RPC_ENDPOINT");
    }
    if (runLiveHeliusValidation)
    {
        await VerifyLiveProviderCoverageAsync(
            client,
            "Helius",
            "TRENCHHQ_HELIUS_API_KEY",
            OnChainProviderTypes.HeliusWebSocket,
            [],
            null);
    }
    if (runLiveShyftValidation)
    {
        await VerifyLiveProviderCoverageAsync(
            client,
            "Shyft",
            "TRENCHHQ_SHYFT_API_KEY",
            OnChainProviderTypes.ShyftWebSocket,
            [],
            null);
    }
    if (runLiveInfuraValidation)
    {
        await VerifyLiveProviderCoverageAsync(
            client,
            "Infura",
            "TRENCHHQ_INFURA_API_KEY",
            null,
            [
                (OnChainProviderTypes.InfuraEthereum, EvmChainDefinitions.EthereumMainnet),
                (OnChainProviderTypes.InfuraBase, EvmChainDefinitions.BaseMainnet),
                (OnChainProviderTypes.InfuraBnb, EvmChainDefinitions.BnbMainnet)
            ],
            null);
    }
    if (runLivePublicEvmValidation)
    {
        await VerifyLiveProviderCoverageAsync(
            client,
            "key-free public RPC",
            null,
            null,
            [
                (OnChainProviderTypes.PublicNodeEthereum, EvmChainDefinitions.EthereumMainnet),
                (OnChainProviderTypes.BasePublic, EvmChainDefinitions.BaseMainnet),
                (OnChainProviderTypes.PublicNodeBnb, EvmChainDefinitions.BnbMainnet),
                (OnChainProviderTypes.PublicNodeRobinhood, EvmChainDefinitions.RobinhoodMainnet)
            ],
            null,
            "TRENCHHQ_PUBLIC_EVM_STREAM_ENDPOINT",
            "TRENCHHQ_PUBLIC_EVM_RPC_ENDPOINT");
    }
    if (string.Equals(
            Environment.GetEnvironmentVariable("TRENCHHQ_RUN_LIVE_POOL_TESTS"),
            "1",
            StringComparison.Ordinal))
    {
        await VerifyLiveProtocolAccountsAsync(client);
    }
    var mintBytes = Enumerable.Repeat((byte)3, 32).ToArray();
    var mint = EncodeBase58(mintBytes);
    var descriptor = new OnChainPoolDescriptor
    {
        PoolKey = new OnChainPoolKey
        {
            ProtocolId = "pumpBondingCurve",
            PoolAddress = "curve"
        },
        PoolType = "bondingCurve",
        ProgramId = "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P",
        BaseMint = mint,
        QuoteMint = "So11111111111111111111111111111111111111112",
        BaseDecimals = 6,
        QuoteDecimals = 9,
        SupportStatus = OnChainSupportStatus.Supported
    };
    await client.ReplaceWatchedPoolsAsync(
    [
        new OnChainWatchedPoolSelection
        {
            Descriptor = descriptor,
            SelectedMint = mint
        }
    ]);
    AssertEqual(OnChainEngineState.Ready, client.State, "Engine did not become ready.");
    var firstProcessId = GetSingleNewEngineProcessId(existingProcessIds);

    await client.PublishTransactionUpdateAsync(CreatePumpTrade("signature-1", mintBytes));
    await WaitUntilAsync(() => prices.Count == 1, TimeSpan.FromSeconds(5), "first price");
    AssertEqual("2000000000000000000", prices.Last().LastTradePriceQuote?.Coefficient,
        "Engine returned the wrong exact price.");

    var referenceObservedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    await client.PublishReferencePriceAsync(
        "solUsd",
        new OnChainDecimalValue { Coefficient = "15025", Scale = 2 },
        "ccxt:binance:SOL/USDT",
        referenceObservedAt);
    await WaitUntilAsync(() => prices.Count == 2, TimeSpan.FromSeconds(5), "USD reference price");
    var referencedPrice = prices.Last();
    AssertEqual("2000000000000000000", referencedPrice.PriceSol?.Coefficient,
        "Wrapped-SOL quote price was not exposed as SOL.");
    AssertEqual("30050000000000000000000", referencedPrice.PriceUsd?.Coefficient,
        "SOL/USD reference conversion was not exact.");
    AssertEqual("ccxt:binance:SOL/USDT", referencedPrice.ReferenceSourceId,
        "Reference-price provenance was lost.");
    AssertEqual<long?>(referenceObservedAt, referencedPrice.ReferenceObservedAtUnixMs,
        "Reference-price timestamp was lost.");

    try
    {
        await client.PublishReferencePriceAsync(
            "solUsd",
            new OnChainDecimalValue { Coefficient = "0", Scale = 0 },
            "invalid",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        throw new InvalidOperationException("A zero reference price was accepted.");
    }
    catch (OnChainEngineRequestException exception)
    {
        AssertEqual("invalidRequest", exception.Code, "Engine request error code changed.");
        Assert(!exception.Retryable, "Invalid reference input was marked retryable.");
    }

    await client.PublishTransactionUpdateAsync(CreatePumpTrade("signature-1", mintBytes));
    await Task.Delay(300);
    AssertEqual(2, prices.Count, "A replay duplicate produced another price.");

    using (var process = Process.GetProcessById(firstProcessId))
    {
        process.Kill(true);
        process.WaitForExit(2000);
    }
    await WaitUntilAsync(
        () => states.Contains(OnChainEngineState.Reconnecting),
        TimeSpan.FromSeconds(5),
        "reconnecting state");
    await WaitUntilAsync(
        () => client.State == OnChainEngineState.Ready,
        TimeSpan.FromSeconds(10),
        "replacement engine");
    var secondProcessId = GetSingleNewEngineProcessId(existingProcessIds);
    Assert(firstProcessId != secondProcessId, "Recovery reused the terminated process.");

    await client.PublishTransactionUpdateAsync(CreatePumpTrade("signature-2", mintBytes));
    await WaitUntilAsync(() => prices.Count == 3, TimeSpan.FromSeconds(5), "post-recovery price");

    await client.ReplaceWatchedPoolsAsync([]);
    await WaitUntilAsync(
        () => !GetEngineProcessIds().Except(existingProcessIds).Any(),
        TimeSpan.FromSeconds(5),
        "lazy engine stop");
    AssertEqual(OnChainEngineState.Stopped, client.State, "Clearing watched pools did not stop the engine.");
    AssertEqual(3, prices.Count, "Recovery produced duplicate or missing prices.");
    await VerifySolanaPriceSamplingAsync(enginePath);
    await VerifyInfuraKnownHeaderAsync();
    await VerifyTickerProviderMatrixAsync(enginePath);
    await VerifyMixedSourceFailureAsync();
    await VerifyStreamCoordinatorAsync(enginePath);
    await VerifyEvmStreamCoordinatorAsync(enginePath);
    await VerifyHiddenEvmReferenceSamplingAsync(enginePath);
    await VerifyRobinhoodSampledHeadCoordinatorAsync(enginePath);
    await VerifyPipelineBenchmarksAsync(enginePath);
    if (runLiveEvmDiscovery)
    {
        await VerifyLiveUniswapV4DiscoveryAsync();
    }
    if (runLiveRobinhoodDiscovery)
    {
        await VerifyLiveRobinhoodDiscoveryAsync();
    }
    if (runLiveEvmStreams)
    {
        await VerifyLiveEvmStreamsAsync(enginePath);
    }
    if (runLiveBaseStreams)
    {
        await VerifyLiveBaseStreamAsync(enginePath);
    }
    if (runLiveMixedMatrix)
    {
        await VerifyLiveMixedMatrixAsync(enginePath);
    }

    Console.WriteLine("PASS: provider configurations, DPAPI, transaction normalization, multi-protocol discovery/decoding, exact reference conversion, one shared engine, bounded streaming, checkpoints, replay deduplication, crash recovery, pool restoration, lazy shutdown, and 1/10/100-pool pipeline benchmarks.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FAIL: on-chain engine integration: {exception}");
    return 1;
}
finally
{
    try
    {
        await client.StopAsync();
    }
    catch
    {
    }
}

static OnChainRawTransactionUpdate CreatePumpTrade(string signature, byte[] mintBytes)
{
    using var stream = new MemoryStream();
    stream.Write([189, 219, 127, 211, 78, 230, 97, 238]);
    stream.Write(mintBytes);
    stream.Write(BitConverter.GetBytes(2_000_000_000UL));
    stream.Write(BitConverter.GetBytes(1_000_000UL));
    stream.WriteByte(1);
    return new OnChainRawTransactionUpdate
    {
        Signature = signature,
        Slot = 20,
        Commitment = OnChainCommitment.Processed,
        SourceId = "integration-fixture",
        ProgramData =
        [
            new OnChainRawProgramData
            {
                ProgramId = "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P",
                DataBase64 = Convert.ToBase64String(stream.ToArray()),
                LogIndex = 9
            }
        ]
    };
}

static async Task VerifyPipelineBenchmarksAsync(string enginePath)
{
    foreach (var poolCount in new[] { 1, 10, 100 })
    {
        var updatesPerPool = poolCount == 100 ? 20 : 100;
        var expectedUpdates = poolCount * updatesPerPool;
        OnChainPipelineDiagnostics.Reset();
        var existingProcessIds = GetEngineProcessIds();
        var client = new OnChainEngineClient(enginePath);
        var delivered = 0;
        client.PriceUpdated += (_, args) =>
        {
            OnChainPipelineDiagnostics.RecordUiRender(args.Update.ObservedAtUnixMs);
            Interlocked.Increment(ref delivered);
        };
        try
        {
            var selections = new OnChainWatchedPoolSelection[poolCount];
            var mints = new byte[poolCount][];
            for (var index = 0; index < poolCount; index++)
            {
                var mintBytes = new byte[32];
                BitConverter.TryWriteBytes(mintBytes.AsSpan(0, sizeof(int)), index + 1000);
                mintBytes[31] = 7;
                mints[index] = mintBytes;
                var mint = EncodeBase58(mintBytes);
                selections[index] = new OnChainWatchedPoolSelection
                {
                    SelectedMint = mint,
                    Descriptor = new OnChainPoolDescriptor
                    {
                        PoolKey = new OnChainPoolKey
                        {
                            ProtocolId = OnChainProtocolIds.PumpBondingCurve,
                            PoolAddress = $"benchmark-curve-{index}"
                        },
                        PoolType = "bondingCurve",
                        ProgramId = "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P",
                        BaseMint = mint,
                        QuoteMint = "So11111111111111111111111111111111111111112",
                        BaseDecimals = 6,
                        QuoteDecimals = 9,
                        SupportStatus = OnChainSupportStatus.Supported
                    }
                };
            }

            await client.ReplaceWatchedPoolsAsync(selections);
            var engineProcess = Process.GetProcessById(GetSingleNewEngineProcessId(existingProcessIds));
            using (engineProcess)
            using (var appProcess = Process.GetCurrentProcess())
            {
                appProcess.Refresh();
                engineProcess.Refresh();
                var cpuBefore = appProcess.TotalProcessorTime + engineProcess.TotalProcessorTime;
                var stopwatch = Stopwatch.StartNew();
                await Task.WhenAll(mints.SelectMany((mint, poolIndex) =>
                    Enumerable.Range(0, updatesPerPool).Select(updateIndex =>
                        client.PublishTransactionUpdateAsync(CreatePumpTrade(
                            $"benchmark-{poolCount}-{poolIndex}-{updateIndex}",
                            mint)))));
                await WaitUntilAsync(
                    () => Volatile.Read(ref delivered) == expectedUpdates,
                    TimeSpan.FromSeconds(20),
                    $"{poolCount}-pool benchmark delivery");
                stopwatch.Stop();
                appProcess.Refresh();
                engineProcess.Refresh();
                var cpuUsed = appProcess.TotalProcessorTime + engineProcess.TotalProcessorTime - cpuBefore;
                var workingSetMb = (appProcess.WorkingSet64 + engineProcess.WorkingSet64) / 1024d / 1024d;
                var diagnostics = OnChainPipelineDiagnostics.Snapshot();
                AssertEqual((long)expectedUpdates, diagnostics.SourceUpdates,
                    $"The {poolCount}-pool benchmark lost source updates.");
                AssertEqual((long)expectedUpdates, diagnostics.EnginePriceUpdates,
                    $"The {poolCount}-pool benchmark lost engine prices.");
                AssertEqual((long)expectedUpdates, diagnostics.UiRenders,
                    $"The {poolCount}-pool benchmark lost delivery-boundary updates.");
                Assert(!double.IsInfinity(diagnostics.EngineLatencyP95Ms)
                       && diagnostics.EngineLatencyP95Ms <= 500,
                    $"The {poolCount}-pool engine p95 exceeded 500 ms.");
                Console.WriteLine(
                    $"BENCH | pools={poolCount} | updates={expectedUpdates}"
                    + $" | elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F1}"
                    + $" | updatesPerSecond={expectedUpdates / stopwatch.Elapsed.TotalSeconds:F0}"
                    + $" | engineP50Ms={diagnostics.EngineLatencyP50Ms:F0}"
                    + $" | engineP95Ms={diagnostics.EngineLatencyP95Ms:F0}"
                    + $" | deliveryP95Ms={diagnostics.RenderLatencyP95Ms:F0}"
                    + $" | cpuMs={cpuUsed.TotalMilliseconds:F1} | combinedWorkingSetMb={workingSetMb:F1}");
            }
        }
        finally
        {
            await client.StopAsync();
        }
    }
}

static async Task VerifyCredentialProtectionAsync()
{
    var temporaryRoot = Path.Combine(Path.GetTempPath(), "TrenchHQOnChainIntegration", Guid.NewGuid().ToString("N"));
    var credentialReference = Guid.NewGuid().ToString("N");
    const string credential = "integration-secret-that-must-not-be-plaintext";
    try
    {
        // A synthetic v1 payload proves existing protected credentials survive the rename.
        var v1CredentialPath = Path.Combine(temporaryRoot, "onchain-secrets", credentialReference + ".bin");
        Directory.CreateDirectory(Path.GetDirectoryName(v1CredentialPath)!);
        await File.WriteAllBytesAsync(v1CredentialPath, System.Security.Cryptography.ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(credential),
            Convert.FromHexString("4E657875732E4F6E436861696E2E50726F766964657243726564656E7469616C2E7631"),
            System.Security.Cryptography.DataProtectionScope.CurrentUser));
        AssertEqual(credential, await OnChainProviderConfigurationStore.ReadCredentialAsync(temporaryRoot, credentialReference),
            "The persisted v1 DPAPI format changed during the product rename.");
        await OnChainProviderConfigurationStore.SaveCredentialAsync(temporaryRoot, credentialReference, credential);
        var restored = await OnChainProviderConfigurationStore.ReadCredentialAsync(temporaryRoot, credentialReference);
        AssertEqual(credential, restored, "DPAPI credential did not round-trip for the current Windows user.");
        var storedBytes = await File.ReadAllBytesAsync(
            Path.Combine(temporaryRoot, "onchain-secrets", credentialReference + ".bin"));
        Assert(!System.Text.Encoding.UTF8.GetString(storedBytes).Contains(credential, StringComparison.Ordinal),
            "Credential was written in plaintext.");
        Assert(!OnChainProviderConfigurationStore.IsSecureEndpoint("https://example.test/?api-key=secret"),
            "Credential-bearing endpoint queries must be rejected.");
        Assert(!OnChainProviderConfigurationStore.IsSecureEndpoint("https://example.test/#secret"),
            "Credential-bearing endpoint fragments must be rejected.");
        Assert(OnChainProviderConfigurationStore.IsSecureEndpoint("https://example.test"),
            "A credential-free HTTPS endpoint should be accepted.");
        Assert(OnChainProviderConfigurationStore.IsSecureStreamEndpoint(
                "wss://example.test",
                OnChainStreamTransport.SolanaWebSocket),
            "A credential-free WSS endpoint should be accepted.");
        Assert(!OnChainProviderConfigurationStore.IsSecureStreamEndpoint(
                "wss://example.test/?api_key=secret",
                OnChainStreamTransport.SolanaWebSocket),
            "A credential-bearing WSS endpoint query must be rejected.");
        Assert(!OnChainProviderConfigurationStore.HasCredentialFreePath(CreateProviderConfiguration(
                OnChainProviderTypes.QuickNodeWebSocket,
                "wss://sample.solana-mainnet.quiknode.pro/plaintext-token",
                "https://sample.solana-mainnet.quiknode.pro/plaintext-token")),
            "A QuickNode token embedded in public configuration paths was accepted.");
        Assert(!OnChainProviderConfigurationStore.HasCredentialFreePath(CreateProviderConfiguration(
                OnChainProviderTypes.QuickNodeEthereum,
                "wss://sample.ethereum-mainnet.quiknode.pro/plaintext-token",
                "https://sample.ethereum-mainnet.quiknode.pro/plaintext-token")),
            "A QuickNode Ethereum token embedded in public configuration paths was accepted.");
        var ethereumAlchemy = CreateProviderConfiguration(
            OnChainProviderTypes.AlchemyEthereum,
            "wss://eth-mainnet.g.alchemy.com/v2",
            "https://eth-mainnet.g.alchemy.com/v2");
        var baseAlchemy = CreateProviderConfiguration(
            OnChainProviderTypes.AlchemyBase,
            "wss://base-mainnet.g.alchemy.com/v2",
            "https://base-mainnet.g.alchemy.com/v2");
        ethereumAlchemy.CredentialReference = credentialReference;
        baseAlchemy.CredentialReference = credentialReference;
        Assert(OnChainProviderConfigurationStore.CanReuseCredential(
                ethereumAlchemy,
                OnChainProviderCatalog.Get(baseAlchemy.ProviderType)),
            "A Base row could not explicitly reuse its same-family Ethereum credential.");
        var solanaAlchemy = CreateProviderConfiguration(
            OnChainProviderTypes.AlchemyWebSocket,
            "wss://solana-mainnet.streaming.alchemy.com/v2",
            "https://solana-mainnet.g.alchemy.com/v2");
        Assert(OnChainProviderConfigurationStore.CanReuseCredential(
                solanaAlchemy,
                OnChainProviderCatalog.Get(OnChainProviderTypes.AlchemyRobinhood)),
            "One Alchemy credential could not cross the Solana/EVM namespace boundary.");
        var solanaDrpc = CreateProviderConfiguration(
            OnChainProviderTypes.DrpcWebSocket,
            "wss://lb.drpc.live/solana",
            "https://lb.drpc.live/solana");
        Assert(OnChainProviderConfigurationStore.CanReuseCredential(
                solanaDrpc,
                OnChainProviderCatalog.Get(OnChainProviderTypes.DrpcBase)),
            "One dRPC credential could not cross the Solana/EVM namespace boundary.");
        Assert(!OnChainProviderConfigurationStore.CanReuseCredential(
                ethereumAlchemy,
                OnChainProviderCatalog.Get(OnChainProviderTypes.DrpcBase)),
            "A Base row accepted a cross-family credential reference.");
        var chainstackEthereum = CreateProviderConfiguration(
            OnChainProviderTypes.ChainstackEthereum,
            "wss://ethereum-mainnet.core.chainstack.com",
            "https://ethereum-mainnet.core.chainstack.com");
        Assert(!OnChainProviderConfigurationStore.CanReuseCredential(
                chainstackEthereum,
                OnChainProviderCatalog.Get(OnChainProviderTypes.ChainstackBase)),
            "A node-specific Chainstack credential was treated as reusable across chains.");
        Assert(!OnChainProviderConfigurationStore.CanReuseCredential(
                CreateProviderConfiguration(
                    OnChainProviderTypes.QuickNodeWebSocket,
                    "wss://sample.solana-mainnet.quiknode.pro",
                    "https://sample.solana-mainnet.quiknode.pro"),
                OnChainProviderCatalog.Get(OnChainProviderTypes.QuickNodeRobinhood)),
            "A QuickNode endpoint token was treated as reusable across endpoints.");
        AssertEqual(5,
            OnChainProviderCatalog.GetAutomaticSharedCredentialTargets(
                OnChainProviderCatalog.Get(OnChainProviderTypes.AlchemyWebSocket)).Length,
            "Alchemy did not expose every standard TrenchHQ chain as an automatic shared-key target.");
        AssertEqual(4,
            OnChainProviderCatalog.GetAutomaticSharedCredentialTargets(
                OnChainProviderCatalog.Get(OnChainProviderTypes.DrpcWebSocket)).Length,
            "dRPC did not expose every standard TrenchHQ chain as an automatic shared-key target.");
        var infuraEthereum = CreateProviderConfiguration(
            OnChainProviderTypes.InfuraEthereum,
            "wss://mainnet.infura.io/ws/v3",
            "https://mainnet.infura.io/v3");
        Assert(OnChainProviderConfigurationStore.CanReuseCredential(
                infuraEthereum,
                OnChainProviderCatalog.Get(OnChainProviderTypes.InfuraBase))
               && OnChainProviderConfigurationStore.CanReuseCredential(
                   infuraEthereum,
                   OnChainProviderCatalog.Get(OnChainProviderTypes.InfuraBnb)),
            "One Infura credential could not be reused across the supported TrenchHQ EVM chains.");
        AssertEqual(3,
            OnChainProviderCatalog.GetAutomaticSharedCredentialTargets(
                OnChainProviderCatalog.Get(OnChainProviderTypes.InfuraEthereum)).Length,
            "Infura did not expose Ethereum, Base, and BNB as automatic shared-key targets.");
        infuraEthereum.CredentialReference = credentialReference;
        var linkedInfuraConfigurations = OnChainProviderConfigurationStore.LinkSharedCredentialConfigurations(
            [infuraEthereum],
            OnChainProviderCatalog.Get(infuraEthereum.ProviderType),
            credentialReference,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        AssertEqual(3, linkedInfuraConfigurations.Length,
            "Saving one Infura key did not create Ethereum, Base, and BNB configurations.");
        AssertEqual(
            "Supported in TrenchHQ: Ethereum, Base, BNB Smart Chain",
            OnChainProviderCatalog.GetSupportedChainsText(
                OnChainProviderCatalog.Get(OnChainProviderTypes.InfuraEthereum)),
            "The Infura support label does not list its implemented TrenchHQ chains.");
        solanaAlchemy.CredentialReference = credentialReference;
        var replacedReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var linkedAlchemyConfigurations = OnChainProviderConfigurationStore.LinkSharedCredentialConfigurations(
            [solanaAlchemy],
            OnChainProviderCatalog.Get(solanaAlchemy.ProviderType),
            credentialReference,
            replacedReferences);
        AssertEqual(5, linkedAlchemyConfigurations.Length,
            "Saving one Alchemy key did not create every standard TrenchHQ chain configuration.");
        Assert(linkedAlchemyConfigurations.All(configuration =>
                string.Equals(
                    configuration.CredentialReference,
                    credentialReference,
                    StringComparison.OrdinalIgnoreCase)),
            "Alchemy configurations did not retain one protected credential reference.");
        AssertEqual(0, replacedReferences.Count,
            "Linking a new Alchemy family reported a credential replacement that did not occur.");
        AssertEqual(
            "Supported in TrenchHQ: Solana, Ethereum, Base, BNB Smart Chain, Robinhood Chain",
            OnChainProviderCatalog.GetSupportedChainsText(
                OnChainProviderCatalog.Get(OnChainProviderTypes.AlchemyEthereum)),
            "The Alchemy support label does not distinguish TrenchHQ-supported chains.");
        var credentialPath = Path.Combine(
            temporaryRoot,
            "onchain-secrets",
            credentialReference + ".bin");
        OnChainProviderConfigurationStore.DeleteCredentialIfUnreferenced(
            temporaryRoot,
            [baseAlchemy],
            credentialReference);
        Assert(File.Exists(credentialPath),
            "Deleting one same-family configuration removed a still-referenced DPAPI blob.");
        OnChainProviderConfigurationStore.DeleteCredentialIfUnreferenced(
            temporaryRoot,
            [],
            credentialReference);
        Assert(!File.Exists(credentialPath),
            "The last removed credential reference left an orphaned DPAPI blob.");
    }
    finally
    {
        if (Directory.Exists(temporaryRoot))
        {
            Directory.Delete(temporaryRoot, true);
        }
    }
}

static async Task VerifyProviderConfigurationsAndRpcAuthenticationAsync()
{
    AssertEqual(OnChainProviderTypes.AlchemyWebSocket, new OnChainProviderConfiguration().ProviderType,
        "Alchemy free WebSocket is not the default on-chain provider.");
    AssertEqual(10, OnChainProviderCatalog.Presets.Length,
        "The on-chain provider catalog does not contain all supported presets.");
    Assert(OnChainProviderCatalog.Presets.All(preset =>
            !string.IsNullOrWhiteSpace(preset.SetupInstructions)
            && !string.IsNullOrWhiteSpace(preset.CredentialLabel)),
        "A provider preset is missing user-facing credential instructions.");
    var configurablePresets = OnChainProviderCatalog.AllPresets
        .Where(preset => !OnChainProviderCatalog.IsPublicEvaluationProvider(preset.ProviderType))
        .ToArray();
    Assert(configurablePresets.All(preset => preset.Badges is { Count: > 0 }),
        "A configurable provider preset is missing its factual UI badges.");
    var redundantBadges = new[] { "Free tier", "Solana only", "Bring your own" };
    Assert(configurablePresets
            .SelectMany(preset => preset.Badges!)
            .All(badge => !redundantBadges.Contains(badge, StringComparer.OrdinalIgnoreCase)
                          && !badge.Contains("TrenchHQ chains", StringComparison.OrdinalIgnoreCase)),
        "A provider badge repeats information already supplied by its access group or selected chain.");
    AssertEqual(5, OnChainProviderCatalog.Presets.Count(preset =>
            preset.AccessCategory == OnChainProviderAccessCategory.FreeTierAvailable),
        "The Solana free-tier provider group changed unexpectedly.");
    AssertEqual(4, OnChainProviderCatalog.Presets.Count(preset =>
            preset.AccessCategory == OnChainProviderAccessCategory.TrialOrPaid),
        "The Solana trial-or-paid provider group changed unexpectedly.");
    AssertEqual(1, OnChainProviderCatalog.Presets.Count(preset =>
            preset.AccessCategory == OnChainProviderAccessCategory.CustomEndpoint),
        "The Solana custom-endpoint provider group changed unexpectedly.");
    AssertEqual(4, OnChainProviderCatalog.EthereumPresets.Count(preset =>
            !OnChainProviderCatalog.IsPublicEvaluationProvider(preset.ProviderType)
            && preset.AccessCategory == OnChainProviderAccessCategory.FreeTierAvailable),
        "The Ethereum free-tier provider group changed unexpectedly.");
    AssertEqual(1, OnChainProviderCatalog.EthereumPresets.Count(preset =>
            preset.AccessCategory == OnChainProviderAccessCategory.TrialOrPaid),
        "The Ethereum trial-or-paid provider group changed unexpectedly.");
    AssertEqual(1, OnChainProviderCatalog.EthereumPresets.Count(preset =>
            preset.AccessCategory == OnChainProviderAccessCategory.CustomEndpoint),
        "The Ethereum custom-endpoint provider group changed unexpectedly.");
    AssertEqual(4, OnChainProviderCatalog.BasePresets.Count(preset =>
            !OnChainProviderCatalog.IsPublicEvaluationProvider(preset.ProviderType)
            && preset.AccessCategory == OnChainProviderAccessCategory.FreeTierAvailable),
        "The Base free-tier provider group changed unexpectedly.");
    AssertEqual(0, OnChainProviderCatalog.BasePresets.Count(preset =>
            preset.AccessCategory == OnChainProviderAccessCategory.TrialOrPaid),
        "Base unexpectedly exposes an empty trial-or-paid provider group.");
    AssertEqual(1, OnChainProviderCatalog.BasePresets.Count(preset =>
            preset.AccessCategory == OnChainProviderAccessCategory.CustomEndpoint),
        "The Base custom-endpoint provider group changed unexpectedly.");
    AssertEqual(4, OnChainProviderCatalog.BnbPresets.Count(preset =>
            !OnChainProviderCatalog.IsPublicEvaluationProvider(preset.ProviderType)
            && preset.AccessCategory == OnChainProviderAccessCategory.FreeTierAvailable),
        "The BNB free-tier provider group changed unexpectedly.");
    AssertEqual(0, OnChainProviderCatalog.BnbPresets.Count(preset =>
            preset.AccessCategory == OnChainProviderAccessCategory.TrialOrPaid),
        "BNB unexpectedly exposes an empty trial-or-paid provider group.");
    AssertEqual(1, OnChainProviderCatalog.BnbPresets.Count(preset =>
            preset.AccessCategory == OnChainProviderAccessCategory.CustomEndpoint),
        "The BNB custom-endpoint provider group changed unexpectedly.");
    AssertEqual(2, OnChainProviderCatalog.RobinhoodPresets.Count(preset =>
            !OnChainProviderCatalog.IsPublicEvaluationProvider(preset.ProviderType)
            && preset.AccessCategory == OnChainProviderAccessCategory.FreeTierAvailable),
        "The Robinhood free-tier provider group changed unexpectedly.");
    AssertEqual(1, OnChainProviderCatalog.RobinhoodPresets.Count(preset =>
            preset.AccessCategory == OnChainProviderAccessCategory.TrialOrPaid),
        "The Robinhood trial-or-paid provider group changed unexpectedly.");
    AssertEqual(1, OnChainProviderCatalog.RobinhoodPresets.Count(preset =>
            preset.AccessCategory == OnChainProviderAccessCategory.CustomEndpoint),
        "The Robinhood custom-endpoint provider group changed unexpectedly.");
    AssertEqual(OnChainProviderAccessCategory.TrialOrPaid,
        OnChainProviderCatalog.Get(OnChainProviderTypes.QuickNodeRobinhood).AccessCategory,
        "QuickNode Robinhood was incorrectly presented as an ongoing free tier.");
    Assert(configurablePresets
            .Where(preset => preset.ProviderFamily == "chainstack")
            .All(preset => preset.AccessCategory == OnChainProviderAccessCategory.FreeTierAvailable
                           && preset.Badges!.Contains("1 free node total")),
        "Chainstack rows do not consistently disclose the account-wide one-node free allowance.");
    Assert(configurablePresets
            .Where(preset => preset.ProviderFamily == "quicknode")
            .All(preset => preset.AccessCategory == OnChainProviderAccessCategory.TrialOrPaid
                           && preset.Badges!.Contains("30-day trial")),
        "QuickNode rows do not consistently disclose the time-limited trial.");
    Assert(configurablePresets
            .Where(preset => preset.ProviderFamily == "custom")
            .All(preset => preset.AccessCategory == OnChainProviderAccessCategory.CustomEndpoint),
        "A custom endpoint is classified as a provider plan.");
    Assert(configurablePresets
            .Where(preset => preset.ProviderFamily == "custom"
                             && preset.ChainNamespace == ChainNamespaces.Eip155)
            .All(preset => preset.DisplayName == "Custom keyless RPC endpoint"
                           && preset.Badges!.Contains("Keyless only")),
        "An EVM custom endpoint does not disclose its keyless-only authentication scope.");
    Assert(configurablePresets
            .Where(preset => preset.ChainNamespace == ChainNamespaces.Eip155
                             && preset.ProviderFamily == "alchemy")
            .All(preset => preset.DisplayName == "Alchemy")
           && configurablePresets
               .Where(preset => preset.ChainNamespace == ChainNamespaces.Eip155
                                && preset.ProviderFamily == "drpc")
               .All(preset => preset.DisplayName == "dRPC")
           && configurablePresets
               .Where(preset => preset.ChainNamespace == ChainNamespaces.Eip155
                                && preset.ProviderFamily == "infura")
               .All(preset => preset.DisplayName == "Infura")
           && configurablePresets
               .Where(preset => preset.ChainNamespace == ChainNamespaces.Eip155
                                && preset.ProviderFamily == "quicknode")
               .All(preset => preset.DisplayName == "QuickNode")
           && configurablePresets
               .Where(preset => preset.ChainNamespace == ChainNamespaces.Eip155
                                && preset.ProviderFamily == "chainstack")
               .All(preset => preset.DisplayName == "Chainstack"),
        "An EVM provider name redundantly repeats its selected chain.");
    AssertEqual(
        "https://dashboard.alchemy.com/apps",
        OnChainProviderCatalog.Get(OnChainProviderTypes.AlchemyWebSocket).SetupUrl,
        "The Alchemy API-key portal changed unexpectedly.");
    AssertEqual(
        "https://dashboard.helius.dev/",
        OnChainProviderCatalog.Get(OnChainProviderTypes.HeliusLaserStream).SetupUrl,
        "The Helius API-key portal changed unexpectedly.");
    AssertEqual(
        "https://customers.triton.one/onboarding",
        OnChainProviderCatalog.Get(OnChainProviderTypes.TritonYellowstone).SetupUrl,
        "The Triton onboarding portal changed unexpectedly.");
    AssertEqual(
        string.Empty,
        OnChainProviderCatalog.Get(OnChainProviderTypes.CustomYellowstone).SetupUrl,
        "Custom Yellowstone must not imply that a universal API-key portal exists.");
    AssertEqual(
        "wss://solana-mainnet.streaming.alchemy.com/v2",
        OnChainProviderCatalog.Get(OnChainProviderTypes.AlchemyWebSocket).DefaultStreamEndpoint,
        "The Alchemy Solana WebSocket endpoint changed unexpectedly.");
    AssertEqual(
        "https://solana-mainnet.streaming.alchemy.com",
        OnChainProviderCatalog.Get(OnChainProviderTypes.AlchemyYellowstone).DefaultStreamEndpoint,
        "The Alchemy Yellowstone mainnet endpoint changed unexpectedly.");
    AssertEqual(6, OnChainProviderCatalog.Presets.Count(preset =>
            preset.StreamTransport == OnChainStreamTransport.SolanaWebSocket),
        "The standard-WebSocket provider set changed unexpectedly.");
    Assert(OnChainProviderCatalog.Presets
            .Where(preset => preset.StreamTransport == OnChainStreamTransport.SolanaWebSocket)
            .All(preset => !preset.ReplayEnabled),
        "A standard Solana WebSocket preset incorrectly claims replay support.");
    var shyftPreset = OnChainProviderCatalog.Get(OnChainProviderTypes.ShyftWebSocket);
    Assert(shyftPreset.SetupInstructions.Contains("unlimited RPC credits", StringComparison.Ordinal)
           && shyftPreset.SetupInstructions.Contains("10 requests/second", StringComparison.Ordinal)
           && shyftPreset.SetupInstructions.Contains("gRPC is not included", StringComparison.Ordinal)
           && shyftPreset.SetupInstructions.Contains("slotSubscribe", StringComparison.Ordinal),
        "The Shyft setup guidance does not match its current free RPC and WebSocket documentation.");
    var tritonPreset = OnChainProviderCatalog.Get(OnChainProviderTypes.TritonYellowstone);
    Assert(tritonPreset.SetupInstructions.Contains("$125 minimum prepaid deposit", StringComparison.Ordinal)
           && tritonPreset.SetupInstructions.Contains("valid for 12 months", StringComparison.Ordinal),
        "The Triton setup guidance does not disclose its current minimum prepaid commitment.");
    var heliusLaserStreamPreset = OnChainProviderCatalog.Get(OnChainProviderTypes.HeliusLaserStream);
    Assert(heliusLaserStreamPreset.Badges!.Contains("Business+ mainnet")
           && heliusLaserStreamPreset.Badges!.Contains("2-day trial by approval"),
        "The Helius LaserStream badges do not distinguish ongoing Mainnet access from its reviewed trial.");

    var temporaryRoot = Path.Combine(Path.GetTempPath(), "TrenchHQProviderConfigurations", Guid.NewGuid().ToString("N"));
    var helius = CreateProviderConfiguration(
        OnChainProviderTypes.HeliusLaserStream,
        "https://laserstream-mainnet-sgp.helius-rpc.com",
        "https://mainnet.helius-rpc.com");
    var alchemy = CreateProviderConfiguration(
        OnChainProviderTypes.AlchemyYellowstone,
        "https://solana-mainnet.streaming.alchemy.com",
        "https://solana-mainnet.g.alchemy.com/v2");
    try
    {
        Directory.CreateDirectory(temporaryRoot);
        var document = new OnChainProviderConfigurationDocument
        {
            SelectedConfigurationId = helius.Id,
            FallbackConfigurationIds = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                [OnChainProviderConfigurationStore.GetNetworkKey(
                    ChainNamespaces.Solana,
                    "mainnet-beta")] = [alchemy.Id]
            },
            Configurations = [helius, alchemy]
        };
        await OnChainProviderConfigurationStore.SaveAsync(temporaryRoot, document);
        var loaded = await OnChainProviderConfigurationStore.LoadAsync(temporaryRoot);
        AssertEqual(2, loaded.Configurations.Length,
            "Current provider configurations did not round-trip.");
        Assert(loaded.Configurations.Any(configuration => configuration.ProviderType == OnChainProviderTypes.HeliusLaserStream),
            "The Helius configuration was not preserved.");
        AssertEqual(
            helius.Id,
            loaded.SelectedConfigurationIds[OnChainProviderConfigurationStore.GetNetworkKey(
                ChainNamespaces.Solana,
                "mainnet-beta")],
            "The selected configuration changed during persistence.");
        AssertEqual(
            alchemy.Id,
            loaded.FallbackConfigurationIds[OnChainProviderConfigurationStore.GetNetworkKey(
                ChainNamespaces.Solana,
                "mainnet-beta")].Single(),
            "The ordered provider fallback did not round-trip.");
        OnChainProviderConfigurationStore.RemoveConfiguration(loaded, alchemy.Id);
        Assert(!loaded.FallbackConfigurationIds.ContainsKey(
                OnChainProviderConfigurationStore.GetNetworkKey(
                    ChainNamespaces.Solana,
                    "mainnet-beta")),
            "Removing a provider left it in the automatic fallback route.");
        var firstRun = OnChainProviderConfigurationStore.CreateFirstRunDocument();
        AssertEqual(4, firstRun.Configurations.Length,
            "First-run provider setup did not preserve the four verified no-key streaming profiles.");
        var firstRunEthereum = firstRun.Configurations.Single(configuration =>
            configuration.ChainId == EvmChainDefinitions.EthereumMainnetChainId);
        var firstRunBase = firstRun.Configurations.Single(configuration =>
            configuration.ChainId == EvmChainDefinitions.BaseMainnetChainId);
        var firstRunBnb = firstRun.Configurations.Single(configuration =>
            configuration.ChainId == EvmChainDefinitions.BnbMainnetChainId);
        var firstRunRobinhood = firstRun.Configurations.Single(configuration =>
            configuration.ChainId == EvmChainDefinitions.RobinhoodMainnetChainId);
        AssertEqual(OnChainProviderTypes.PublicNodeEthereum, firstRunEthereum.ProviderType,
            "First-run provider setup did not use the no-key Ethereum profile.");
        AssertEqual(OnChainProviderTypes.BasePublic, firstRunBase.ProviderType,
            "First-run provider setup did not use the no-key Base profile.");
        AssertEqual("wss://base-rpc.publicnode.com", firstRunBase.StreamEndpoint,
            "First-run Base setup did not use the live-validated key-free WebSocket endpoint.");
        AssertEqual("https://base-rpc.publicnode.com", firstRunBase.RpcEndpoint,
            "First-run Base setup did not use the live-validated key-free RPC endpoint.");
        AssertEqual(OnChainProviderTypes.PublicNodeBnb, firstRunBnb.ProviderType,
            "First-run provider setup did not use the no-key BNB profile.");
        AssertEqual(OnChainProviderTypes.PublicNodeRobinhood, firstRunRobinhood.ProviderType,
            "First-run provider setup did not use the no-key Robinhood profile.");
        AssertEqual("wss://robinhood-rpc.publicnode.com", firstRunRobinhood.StreamEndpoint,
            "First-run Robinhood setup did not use the validated key-free WebSocket endpoint.");
        AssertEqual("https://robinhood-rpc.publicnode.com", firstRunRobinhood.RpcEndpoint,
            "First-run Robinhood setup did not use the validated key-free RPC endpoint.");
        AssertEqual(
            firstRunEthereum.Id,
            firstRun.SelectedConfigurationIds[OnChainProviderConfigurationStore.GetNetworkKey(
                ChainNamespaces.Eip155,
                EvmChainDefinitions.EthereumMainnet.ChainId)],
            "The first-run Ethereum provider was not selected.");
        AssertEqual(
            firstRunBase.Id,
            firstRun.SelectedConfigurationIds[OnChainProviderConfigurationStore.GetNetworkKey(
                ChainNamespaces.Eip155,
                EvmChainDefinitions.BaseMainnet.ChainId)],
            "The first-run Base provider was not selected.");
        AssertEqual(
            firstRunBnb.Id,
            firstRun.SelectedConfigurationIds[OnChainProviderConfigurationStore.GetNetworkKey(
                ChainNamespaces.Eip155,
                EvmChainDefinitions.BnbMainnet.ChainId)],
            "The first-run BNB provider was not selected.");
        AssertEqual(
            firstRunRobinhood.Id,
            firstRun.SelectedConfigurationIds[OnChainProviderConfigurationStore.GetNetworkKey(
                ChainNamespaces.Eip155,
                EvmChainDefinitions.RobinhoodMainnet.ChainId)],
            "The first-run Robinhood provider was not selected.");
        AssertEqual(4, OnChainProviderCatalog.AllPresets.Count(preset =>
                OnChainProviderCatalog.IsPublicEvaluationProvider(preset.ProviderType)),
            "The internal public-evaluation provider set changed unexpectedly.");
        Assert(OnChainProviderCatalog.IsPublicEvaluationProvider(OnChainProviderTypes.PublicNodeEthereum)
               && OnChainProviderCatalog.IsPublicEvaluationProvider(OnChainProviderTypes.BasePublic)
               && OnChainProviderCatalog.IsPublicEvaluationProvider(OnChainProviderTypes.PublicNodeBnb)
               && OnChainProviderCatalog.IsPublicEvaluationProvider(OnChainProviderTypes.PublicNodeRobinhood)
               && !OnChainProviderCatalog.IsPublicEvaluationProvider(OnChainProviderTypes.QuickNodeEthereum),
            "Public evaluation was not kept distinct from configurable provider rows.");
        AssertEqual(
            OnChainProviderTypes.BasePublic,
            OnChainProviderCatalog.GetPublicEvaluationPreset(
                ChainNamespaces.Eip155,
                EvmChainDefinitions.BaseMainnetChainId)?.ProviderType,
            "Base public evaluation did not resolve to its internal preset.");

        var routeDocument = OnChainProviderConfigurationStore.CreateFirstRunDocument();
        foreach (var privateProviderType in new[]
                 {
                     OnChainProviderTypes.AlchemyEthereum,
                     OnChainProviderTypes.AlchemyBase,
                     OnChainProviderTypes.AlchemyBnb,
                     OnChainProviderTypes.AlchemyRobinhood
                 })
        {
            var primary = OnChainProviderConfigurationStore.CreateConfiguration(
                OnChainProviderCatalog.Get(privateProviderType));
            var networkKey = OnChainProviderConfigurationStore.GetNetworkKey(
                primary.ChainNamespace, primary.ChainId);
            var publicId = routeDocument.SelectedConfigurationIds[networkKey];
            routeDocument.Configurations = [.. routeDocument.Configurations, primary];
            routeDocument.SelectedConfigurationIds[networkKey] = primary.Id;
            var automatic = new OnChainProviderConfigurationService(routeDocument, () => true);
            AssertEqual(primary.Id, automatic.GetSelectedConfiguration(primary.ChainNamespace, primary.ChainId)?.Id,
                "A saved private provider required manual activation.");
            await automatic.TryFailoverAsync(primary.Id, OnChainProviderFailureKind.Authentication);
            AssertEqual(publicId, automatic.GetSelectedConfiguration(primary.ChainNamespace, primary.ChainId)?.Id,
                "A private provider did not get its automatic public last resort.");
            AssertEqual(primary.Id, routeDocument.SelectedConfigurationIds[networkKey],
                "Computing the runtime fallback rewrote the saved primary.");
            Assert(!routeDocument.FallbackConfigurationIds.ContainsKey(networkKey),
                "Computing the runtime fallback rewrote the saved route.");
        }
        var solanaPrimary = OnChainProviderConfigurationStore.CreateConfiguration(
            OnChainProviderCatalog.Get(OnChainProviderTypes.AlchemyWebSocket));
        routeDocument.Configurations = [.. routeDocument.Configurations, solanaPrimary];
        routeDocument.SelectedConfigurationIds[
            OnChainProviderConfigurationStore.GetNetworkKey(
                solanaPrimary.ChainNamespace, solanaPrimary.ChainId)] = solanaPrimary.Id;
        var solanaAutomatic = new OnChainProviderConfigurationService(routeDocument, () => true);
        await solanaAutomatic.TryFailoverAsync(solanaPrimary.Id, OnChainProviderFailureKind.Authentication);
        Assert(solanaAutomatic.GetSelectedConfiguration() == null,
            "Solana gained an unsupported public live-provider fallback.");

        var privateEthereum = CreateProviderConfiguration(
            OnChainProviderTypes.AlchemyEthereum,
            "wss://eth-mainnet.g.alchemy.com/v2",
            "https://eth-mainnet.g.alchemy.com/v2");
        firstRun.Configurations = [.. firstRun.Configurations, privateEthereum];
        var ethereumNetworkKey = OnChainProviderConfigurationStore.GetNetworkKey(
            ChainNamespaces.Eip155,
            EvmChainDefinitions.EthereumMainnetChainId);
        firstRun.SelectedConfigurationIds[ethereumNetworkKey] = privateEthereum.Id;
        var removedPrivateEthereum = OnChainProviderConfigurationStore.RemoveConfiguration(
            firstRun,
            privateEthereum.Id);
        AssertEqual(privateEthereum.Id, removedPrivateEthereum?.Id,
            "The selected private Ethereum configuration was not removed.");
        Assert(!firstRun.SelectedConfigurationIds.ContainsKey(ethereumNetworkKey),
            "Removing an active private API silently selected a remaining public provider.");
        Assert(firstRun.Configurations.Any(configuration =>
                configuration.ProviderType == OnChainProviderTypes.PublicNodeEthereum),
            "Removing a private API also removed the internal Ethereum public-evaluation profile.");
        var persistedJson = await File.ReadAllTextAsync(
            Path.Combine(temporaryRoot, OnChainProviderConfigurationStore.ConfigurationsFileName));
        Assert(persistedJson.Contains("\"Configurations\"", StringComparison.Ordinal),
            "The provider-configuration document used the wrong schema.");

        var duplicateAlchemy = CreateProviderConfiguration(
            OnChainProviderTypes.AlchemyWebSocket,
            "wss://solana-mainnet.streaming.alchemy.com/v2",
            "https://solana-mainnet.g.alchemy.com/v2");
        var secondAlchemy = CreateProviderConfiguration(
            OnChainProviderTypes.AlchemyWebSocket,
            "wss://solana-mainnet.streaming.alchemy.com/v2",
            "https://solana-mainnet.g.alchemy.com/v2");
        await OnChainProviderConfigurationStore.SaveAsync(temporaryRoot, new OnChainProviderConfigurationDocument
        {
            SelectedConfigurationId = secondAlchemy.Id,
            Configurations = [duplicateAlchemy, secondAlchemy]
        });
        var rejectedDuplicate = await OnChainProviderConfigurationStore.LoadAsync(temporaryRoot);
        AssertEqual(0, rejectedDuplicate.Configurations.Length,
            "An invalid duplicate provider configuration document was accepted.");

        await VerifyRpcAuthenticationAsync(alchemy, "alchemy-secret", "/v2/alchemy-secret", string.Empty);
        await VerifyRpcAuthenticationAsync(helius, "helius-secret", "/", "?api-key=helius-secret");
        await VerifyRpcAuthenticationAsync(
            CreateProviderConfiguration(
                OnChainProviderTypes.ShyftWebSocket,
                "wss://rpc.shyft.to",
                "https://rpc.shyft.to"),
            "shyft-secret",
            "/",
            "?api_key=shyft-secret");
        await VerifyRpcAuthenticationAsync(
            CreateProviderConfiguration(
                OnChainProviderTypes.QuickNodeWebSocket,
                "wss://sample.solana-mainnet.quiknode.pro",
                "https://sample.solana-mainnet.quiknode.pro"),
            "quicknode-secret",
            "/quicknode-secret",
            string.Empty);
        await VerifyRpcAuthenticationAsync(
            CreateProviderConfiguration(
                OnChainProviderTypes.ChainstackWebSocket,
                "wss://solana-mainnet.core.chainstack.com",
                "https://solana-mainnet.core.chainstack.com"),
            "chainstack-secret",
            "/chainstack-secret",
            string.Empty);
        await VerifyRpcAuthenticationAsync(
            CreateProviderConfiguration(
                OnChainProviderTypes.DrpcWebSocket,
                "wss://lb.drpc.live/solana",
                "https://lb.drpc.live/solana"),
            "drpc-secret",
            "/solana/drpc-secret",
            string.Empty);
        var probeConfiguration = CreateProviderConfiguration(
            OnChainProviderTypes.AlchemyWebSocket,
            "wss://solana-mainnet.streaming.alchemy.com/v2",
            "https://solana-mainnet.g.alchemy.com/v2");
        using (var probeHandler = new RpcAuthenticationFixtureHandler())
        using (var probeClient = new HttpClient(probeHandler))
        {
            Uri? observedWebSocketEndpoint = null;
            await new SolanaProviderCapabilityProbe(
                    probeClient,
                    (endpoint, _) =>
                    {
                        observedWebSocketEndpoint = endpoint;
                        return Task.CompletedTask;
                    })
                .ProbeAsync(probeConfiguration, "alchemy-secret");
            AssertEqual("/v2/alchemy-secret", probeHandler.RequestUri?.AbsolutePath,
                "The Solana activation probe did not authenticate its RPC request.");
            AssertEqual("/v2/alchemy-secret", observedWebSocketEndpoint?.AbsolutePath,
                "The Solana activation probe did not authenticate its WebSocket request.");
        }
        using (var retryHandler = new RpcAuthenticationFixtureHandler())
        using (var retryClient = new HttpClient(retryHandler))
        {
            var webSocketAttempts = 0;
            await new SolanaProviderCapabilityProbe(
                    retryClient,
                    (_, _) => ++webSocketAttempts == 1
                        ? Task.FromException(new WebSocketException("HTTP 429"))
                        : Task.CompletedTask)
                .ProbeAsync(probeConfiguration, "alchemy-secret");
            AssertEqual(2, webSocketAttempts,
                "The Solana activation probe did not retry a transient WebSocket handshake failure.");
        }
        using (var shyftLimitHandler = new RpcAuthenticationFixtureHandler())
        using (var shyftLimitClient = new HttpClient(shyftLimitHandler))
        {
            var shyftConfiguration = CreateProviderConfiguration(
                OnChainProviderTypes.ShyftWebSocket,
                "wss://rpc.shyft.to",
                "https://rpc.shyft.to");
            try
            {
                await new SolanaProviderCapabilityProbe(
                        shyftLimitClient,
                        (_, _) => Task.FromException(new WebSocketException(
                            "The server returned status code '429' when status code '101' was expected.")))
                    .ProbeAsync(shyftConfiguration, "shyft-secret");
                throw new InvalidOperationException(
                    "The Shyft activation probe accepted a rate-limited WebSocket endpoint.");
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("rate-limited or unavailable", StringComparison.Ordinal))
            {
            }
        }
        SolanaProviderCapabilityProbe.ValidateWebSocketSubscriptionResponse(
            "{\"jsonrpc\":\"2.0\",\"result\":42,\"id\":1}");
        try
        {
            SolanaProviderCapabilityProbe.ValidateWebSocketSubscriptionResponse(
                "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32601},\"id\":1}");
            throw new InvalidOperationException(
                "The Solana activation probe accepted a rejected slotSubscribe response.");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("did not confirm slotSubscribe", StringComparison.Ordinal))
        {
        }
        using (var planHandler = new SolanaPlanRestrictionFixtureHandler())
        using (var planClient = new HttpClient(planHandler))
        {
            try
            {
                await new SolanaRpcClient(planClient, probeConfiguration, "drpc-secret")
                    .GetSlotAsync(OnChainCommitment.Confirmed, CancellationToken.None);
                throw new InvalidOperationException(
                    "The Solana client accepted a provider plan restriction as a slot result.");
            }
            catch (SolanaRpcException exception) when (exception.RpcCode == 35)
            {
                AssertEqual(400, exception.HttpStatusCode,
                    "The Solana client lost the HTTP status accompanying an RPC plan restriction.");
                Assert(!exception.Message.Contains("drpc-secret", StringComparison.Ordinal),
                    "The Solana client exposed a credential from an RPC error message.");
                Assert(exception.Message.Contains("[redacted]", StringComparison.Ordinal),
                    "The Solana client did not redact a credential from an RPC error message.");
            }
        }
        await VerifyRpcAuthenticationAsync(
            CreateProviderConfiguration(
                OnChainProviderTypes.TritonYellowstone,
                "https://grpc.triton.test",
                "https://rpc.triton.test/customer"),
            "triton-secret",
            "/customer",
            string.Empty);
    }
    finally
    {
        if (Directory.Exists(temporaryRoot))
        {
            Directory.Delete(temporaryRoot, true);
        }
    }
}

static OnChainProviderConfiguration CreateProviderConfiguration(
    string providerType,
    string streamEndpoint,
    string rpcEndpoint)
{
    var preset = OnChainProviderCatalog.Get(providerType);
    return new OnChainProviderConfiguration
    {
        Id = Guid.NewGuid().ToString("N"),
        CredentialReference = Guid.NewGuid().ToString("N"),
        ChainNamespace = preset.ChainNamespace,
        ChainId = preset.ChainId,
        ProviderType = providerType,
        StreamEndpoint = streamEndpoint,
        RpcEndpoint = rpcEndpoint
    };
}

static async Task VerifyLivePublicWalletRecoveryAsync()
{
    var configuration = CreateProviderConfiguration(
        OnChainProviderTypes.PublicNodeEthereum,
        "wss://ethereum-rpc.publicnode.com",
        "https://ethereum-rpc.publicnode.com");
    var wallets = new[]
    {
        new SavedTrackedWallet
        {
            ChainNamespace = ChainNamespaces.Eip155,
            ChainId = EvmChainDefinitions.EthereumMainnetChainId,
            Address = "0x28c6c06298d514db089934071355e5743bf21d60",
            Label = "Binance 14"
        },
        new SavedTrackedWallet
        {
            ChainNamespace = ChainNamespaces.Eip155,
            ChainId = EvmChainDefinitions.EthereumMainnetChainId,
            Address = "0x1d1d6117d6699b502c087be2e7a8b39a8de12c8b",
            Label = "SDLR trader"
        }
    };
    var updates = new ConcurrentQueue<WalletActivityUpdate>();
    var reachedLive = false;
    ulong? lastObservedBlock = null;
    for (var attempt = 1; attempt <= 3 && !reachedLive; attempt++)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var source = new EvmWalletActivityStreamSource(configuration, null);
        try
        {
            await source.RunAsync(
                wallets,
                lastObservedBlock,
                updates.Enqueue,
                block => lastObservedBlock = block,
                _ =>
                {
                    reachedLive = true;
                    cancellation.Cancel();
                },
                cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested && reachedLive)
        {
        }
        catch (Exception exception) when (
            reachedLive && exception is IOException or WebSocketException)
        {
        }
        catch (Exception exception) when (
            attempt < 3 && exception is IOException or WebSocketException)
        {
            Console.WriteLine(
                $"LIVE WALLET RETRY | public endpoint transport closed | attempt={attempt}");
            await Task.Delay(TimeSpan.FromSeconds(attempt));
        }
    }
    Assert(reachedLive, "The live PublicNode wallet recovery did not reach Live.");
    var presentations = updates
        .GroupBy(static update => new
        {
            update.ChainId,
            update.WalletAddress,
            update.TransactionId
        })
        .Select(static group => WalletActivityPresentationRules.Build(group.ToArray()))
        .ToArray();
    Console.WriteLine(
        $"LIVE WALLET | events={updates.Count} | rows={presentations.Length} | actions="
        + (presentations.Length == 0
            ? "none in the bounded recovery window"
            : string.Join(", ", presentations.Select(static item => item.Summary).Distinct())));
}

static async Task VerifyLivePublicSolanaWalletTransactionAsync()
{
    const string signature = "43ufneL1Wmx8Nm89rzRTcCQrQSJyXJT3Cxhg2C7DLAdgUUg6q6gJVMKDtDogb4VfFxG4PfqTQBJj4XMZA2Cdzrs2";
    const string walletAddress = "zAVEtutVm6hPGGNNTSPxXvPoyKAW7k3mrDWExVUAeyv";
    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var rpc = new SolanaRpcClient(httpClient, OnChainPoolDiscoveryService.PublicMainnetRpcEndpoint);
    var transaction = await rpc.GetWalletTransactionAsync(signature, CancellationToken.None);
    Assert(transaction != null, "The public Solana RPC did not return the recorded PumpSwap transaction.");
    var updates = WalletActivityRules.ParseSolanaTransaction(
        transaction!,
        new SavedTrackedWallet
        {
            ChainNamespace = ChainNamespaces.Solana,
            ChainId = "mainnet-beta",
            Address = walletAddress,
            Label = "Recorded PumpSwap trader"
        },
        "finalized",
        1700000000000,
        "solana-public-live");
    var presentation = WalletActivityPresentationRules.Build(updates);
    AssertEqual(WalletActivityDisplayKind.Buy, presentation.Kind,
        "The recorded PumpSwap wallet deltas did not classify as a buy.");
    Assert(updates.Any(update => update.AssetAddress == WalletActivityPresentationRules.SolanaUsdcMint
                                 && update.Direction == WalletActivityDirection.Outgoing),
        "The recorded PumpSwap wallet deltas did not include outgoing USDC.");
    Assert(updates.Any(update => update.AssetAddress == "6NwarBvDkXhByqVp2Qkq5i9XbtA2B3Bwe8SWGu9vpump"
                                 && update.Direction == WalletActivityDirection.Incoming),
        "The recorded PumpSwap wallet deltas did not include the purchased token.");
    Console.WriteLine($"LIVE SOLANA WALLET | slot={transaction!.Slot} | action={presentation.Summary} | flow={presentation.Detail}");
}

static async Task VerifyEvmProviderAndTransportContractsAsync()
{
    AssertEqual(7, OnChainProviderCatalog.EthereumPresets.Length,
        "The Ethereum provider catalog is incomplete.");
    Assert(OnChainProviderCatalog.EthereumPresets.All(preset =>
            preset.StreamTransport == OnChainStreamTransport.EvmWebSocket
            && preset.ChainNamespace == ChainNamespaces.Eip155
            && preset.ChainId == "1"),
        "An Ethereum provider preset has the wrong chain identity or transport.");
    AssertEqual(6, OnChainProviderCatalog.BasePresets.Length,
        "The Base provider catalog is incomplete.");
    Assert(OnChainProviderCatalog.BasePresets.All(preset =>
            preset.StreamTransport == OnChainStreamTransport.EvmWebSocket
            && preset.ChainNamespace == ChainNamespaces.Eip155
            && preset.ChainId == EvmChainDefinitions.BaseMainnetChainId),
        "A Base provider preset has the wrong chain identity or transport.");
    AssertEqual(6, OnChainProviderCatalog.BnbPresets.Length,
        "The BNB provider catalog is incomplete.");
    Assert(OnChainProviderCatalog.BnbPresets.All(preset =>
            preset.StreamTransport == OnChainStreamTransport.EvmWebSocket
            && preset.ChainNamespace == ChainNamespaces.Eip155
            && preset.ChainId == EvmChainDefinitions.BnbMainnetChainId),
        "A BNB provider preset has the wrong chain identity or transport.");
    AssertEqual(5, OnChainProviderCatalog.RobinhoodPresets.Length,
        "The Robinhood Chain provider catalog is incomplete.");
    Assert(OnChainProviderCatalog.RobinhoodPresets.All(preset =>
            preset.StreamTransport == OnChainStreamTransport.EvmWebSocket
            && preset.ChainNamespace == ChainNamespaces.Eip155
            && preset.ChainId == EvmChainDefinitions.RobinhoodMainnetChainId),
        "A Robinhood Chain provider preset has the wrong chain identity or transport.");
    Assert(EvmAddress.IsHash(EvmWebSocketStreamSource.UniswapV2SwapTopic)
           && EvmAddress.IsHash(EvmWebSocketStreamSource.UniswapV3SwapTopic)
           && EvmAddress.IsHash(EvmWebSocketStreamSource.PancakeV3SwapTopic)
           && EvmAddress.IsHash(EvmWebSocketStreamSource.PancakeInfinityClSwapTopic)
           && EvmAddress.IsHash(EvmWebSocketStreamSource.PancakeInfinityBinSwapTopic),
        "An EVM swap topic is not exactly 32 bytes.");

    AssertEqual(EvmChainDefinitions.BnbMainnetChainId, EvmChainDefinitions.BnbMainnet.ChainId,
        "The BNB mainnet definition has the wrong chain ID.");
    AssertEqual(OnChainProtocolIds.PancakeV2, BnbDeploymentRegistry.Catalog.PrimaryV2ProtocolId,
        "BNB V2 discovery does not retain PancakeSwap protocol identity.");
    AssertEqual(OnChainProtocolIds.PancakeV3, BnbDeploymentRegistry.Catalog.PrimaryV3ProtocolId,
        "BNB V3 discovery does not retain PancakeSwap protocol identity.");
    AssertEqual(BnbDeploymentRegistry.WrappedBnbUsdtReferencePool,
        EvmChainDefinitions.BnbMainnet.NativeUsdReferencePoolId,
        "The BNB reference pool is not canonical.");
    AssertEqual(BnbDeploymentRegistry.UniswapV4StateView,
        EvmChainDefinitions.BnbMainnet.UniswapV4StateViewAddress,
        "The BNB mainnet definition lost the canonical Uniswap V4 StateView.");
    AssertEqual(OnChainProtocolIds.UniswapV4,
        BnbDeploymentRegistry.GetCatalogPoolFamilies(new PoolCatalogEntry
        {
            ProtocolId = "uniswap",
            Labels = ["v4"]
        }).Single().ProtocolId,
        "BNB V4 catalog routing lost Uniswap identity.");
    AssertEqual(EvmChainDefinitions.RobinhoodMainnetChainId,
        EvmChainDefinitions.RobinhoodMainnet.ChainId,
        "The Robinhood Chain mainnet definition has the wrong chain ID.");
    AssertEqual(RobinhoodDeploymentRegistry.WrappedEtherUsdgReferencePool,
        EvmChainDefinitions.RobinhoodMainnet.NativeUsdReferencePoolId,
        "The Robinhood Chain reference pool is not canonical.");
    AssertEqual(OnChainProtocolIds.UniswapV4,
        RobinhoodDeploymentRegistry.GetCatalogPoolFamilies(new PoolCatalogEntry
        {
            ProtocolId = "uniswap",
            Labels = ["v4"]
        }).Single().ProtocolId,
        "Robinhood Chain V4 catalog routing lost Uniswap identity.");

    var alchemy = CreateProviderConfiguration(
        OnChainProviderTypes.AlchemyEthereum,
        "wss://eth-mainnet.g.alchemy.com/v2",
        "https://eth-mainnet.g.alchemy.com/v2");
    var alchemyHttp = OnChainProviderEndpointBuilder.Build(
        alchemy.RpcEndpoint,
        "protected-key",
        OnChainProviderCatalog.Get(alchemy.ProviderType).RpcAuthenticationMode);
    AssertEqual("/v2/protected-key", alchemyHttp.AbsolutePath,
        "Alchemy Ethereum authentication used the wrong URL path.");

    var drpc = CreateProviderConfiguration(
        OnChainProviderTypes.DrpcEthereum,
        "wss://lb.drpc.live/ethereum",
        "https://lb.drpc.live/ethereum");
    var drpcHttp = OnChainProviderEndpointBuilder.Build(
        drpc.RpcEndpoint,
        "protected-key",
        OnChainProviderCatalog.Get(drpc.ProviderType).RpcAuthenticationMode);
    AssertEqual("/ethereum/protected-key", drpcHttp.AbsolutePath,
        "dRPC Ethereum authentication used the wrong URL path.");

    var publicNode = CreateProviderConfiguration(
        OnChainProviderTypes.PublicNodeEthereum,
        "wss://ethereum-rpc.publicnode.com",
        "https://ethereum-rpc.publicnode.com");
    Assert(!OnChainProviderCatalog.Get(publicNode.ProviderType).RequiresCredential,
        "The zero-key PublicNode profile incorrectly requires a credential.");
    using (var forbiddenClient = new HttpClient(
               new FixedHttpStatusHandler(System.Net.HttpStatusCode.Forbidden)))
    {
        try
        {
            _ = await new EvmJsonRpcClient(forbiddenClient, publicNode, null)
                .GetChainIdAsync(CancellationToken.None);
            throw new InvalidOperationException("The public provider accepted HTTP 403.");
        }
        catch (EvmJsonRpcException exception) when (
            exception.Kind == EvmRpcFailureKind.RpcError)
        {
            Assert(exception.Message.Contains("public endpoint", StringComparison.Ordinal)
                   && !exception.Message.Contains("credential", StringComparison.Ordinal),
                "A key-free provider HTTP 403 was misleadingly reported as a rejected credential.");
        }
    }
    using (var keyErrorClient = new HttpClient(new FixedEvmRpcErrorHandler("state key not found")))
    {
        try
        {
            _ = await new EvmJsonRpcClient(keyErrorClient, publicNode, null)
                .GetChainIdAsync(CancellationToken.None);
            throw new InvalidOperationException("The public provider accepted an RPC state-key error.");
        }
        catch (EvmJsonRpcException exception) when (exception.Kind == EvmRpcFailureKind.RpcError)
        {
            AssertEqual(-32000, exception.RpcCode ?? 0,
                "A public RPC state-key error was misclassified as an API-key failure.");
        }
    }
    using (var exhaustedClient = new HttpClient(
               new FixedHttpStatusHandler(System.Net.HttpStatusCode.PaymentRequired)))
    {
        try
        {
            _ = await new EvmJsonRpcClient(exhaustedClient, alchemy, "protected-key")
                .GetChainIdAsync(CancellationToken.None);
            throw new InvalidOperationException("The exhausted private provider accepted HTTP 402.");
        }
        catch (EvmJsonRpcException exception) when (
            exception.Kind == EvmRpcFailureKind.RateLimited)
        {
            Assert(exception.Message.Contains("quota", StringComparison.OrdinalIgnoreCase),
                "HTTP 402 did not expose an retryable account-quota failure for automatic failover.");
        }
    }
    Assert(
        EvmWalletActivityStreamSource.CanContinueWithoutTokenLogReplay(
            new EvmJsonRpcException(EvmRpcFailureKind.RpcError, "Address-scoped logs required.")),
        "A provider-specific eth_getLogs restriction still disabled native and live wallet activity.");
    Assert(
        !EvmWalletActivityStreamSource.CanContinueWithoutTokenLogReplay(
            new EvmJsonRpcException(EvmRpcFailureKind.RateLimited, "Rate limited."))
        && !EvmWalletActivityStreamSource.CanContinueWithoutTokenLogReplay(
            new EvmJsonRpcException(EvmRpcFailureKind.AuthenticationRejected, "Rejected.")),
        "Wallet recovery incorrectly swallowed rate-limit or authentication failures.");
    var recoveryBuffer = new EvmWalletRecoveryBuffer();
    for (var index = 0; index < EvmWalletRecoveryBuffer.MaximumMessages; index++)
    {
        recoveryBuffer.Enqueue([(byte)index]);
    }
    AssertEqual(EvmWalletRecoveryBuffer.MaximumMessages, recoveryBuffer.Count,
        "EVM wallet recovery buffering did not retain the bounded notification set.");
    try
    {
        recoveryBuffer.Enqueue([0]);
        throw new InvalidOperationException("EVM wallet recovery buffering accepted too many notifications.");
    }
    catch (InvalidDataException)
    {
    }
    for (var index = 0; index < EvmWalletRecoveryBuffer.MaximumMessages; index++)
    {
        Assert(recoveryBuffer.TryDequeue(out var message) && message[0] == (byte)index,
            "EVM wallet recovery buffering did not preserve notification order.");
    }
    AssertEqual(0, recoveryBuffer.Bytes,
        "EVM wallet recovery buffering did not release its byte budget after draining.");
    var recoveryByteBuffer = new EvmWalletRecoveryBuffer();
    recoveryByteBuffer.Enqueue(new byte[EvmWalletRecoveryBuffer.MaximumBytes]);
    try
    {
        recoveryByteBuffer.Enqueue([0]);
        throw new InvalidOperationException("EVM wallet recovery buffering exceeded its byte budget.");
    }
    catch (InvalidDataException)
    {
    }
    var symbolHex = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes("SDLR")).ToLowerInvariant();
    var dynamicSymbolValue = "0x"
                             + "20".PadLeft(64, '0')
                             + "4".PadLeft(64, '0')
                             + symbolHex.PadRight(64, '0');
    Assert(EthereumAbi.TryDecodeString(dynamicSymbolValue, out var dynamicSymbol)
           && dynamicSymbol == "SDLR",
        "A standards-compliant dynamic ERC-20 symbol did not decode.");
    Assert(EthereumAbi.TryDecodeString("0x" + symbolHex.PadRight(64, '0'), out var bytes32Symbol)
           && bytes32Symbol == "SDLR",
        "A legacy bytes32 ERC-20 symbol did not decode.");
    Assert(!EthereumAbi.TryDecodeString(
            "0x" + "1000".PadLeft(64, '0') + "4".PadLeft(64, '0'),
            out _),
        "A malformed ERC-20 symbol offset was accepted.");
    var misleadingSymbolBytes = System.Text.Encoding.UTF8.GetBytes("USD\uFFF0T");
    var misleadingSymbolValue = "0x"
                                 + "20".PadLeft(64, '0')
                                 + misleadingSymbolBytes.Length.ToString("x").PadLeft(64, '0')
                                 + Convert.ToHexString(misleadingSymbolBytes).ToLowerInvariant().PadRight(64, '0');
    Assert(!EthereumAbi.TryDecodeString(misleadingSymbolValue, out _),
        "An ERC-20 symbol containing an unassigned Unicode character was accepted.");
    using (var transferLog = JsonDocument.Parse(
               $$"""{"topics":["{{WalletActivityRules.TransferTopic}}"]}"""))
    using (var unrelatedLog = JsonDocument.Parse("""{"topics":[]}"""))
    {
        Assert(EvmWalletActivityStreamSource.IsTransferLog(transferLog.RootElement),
            "Receipt recovery rejected an ERC transfer topic.");
        Assert(!EvmWalletActivityStreamSource.IsTransferLog(unrelatedLog.RootElement),
            "Receipt recovery accepted an unrelated anonymous event.");
    }

    var baseAlchemy = CreateProviderConfiguration(
        OnChainProviderTypes.AlchemyBase,
        "wss://base-mainnet.g.alchemy.com/v2",
        "https://base-mainnet.g.alchemy.com/v2");
    var baseAlchemyHttp = OnChainProviderEndpointBuilder.Build(
        baseAlchemy.RpcEndpoint,
        "protected-key",
        OnChainProviderCatalog.Get(baseAlchemy.ProviderType).RpcAuthenticationMode);
    AssertEqual("/v2/protected-key", baseAlchemyHttp.AbsolutePath,
        "Alchemy Base authentication used the wrong URL path.");
    AssertEqual(
        OnChainProviderCatalog.Get(OnChainProviderTypes.AlchemyEthereum).ProviderFamily,
        OnChainProviderCatalog.Get(OnChainProviderTypes.AlchemyBase).ProviderFamily,
        "Same-family Alchemy presets cannot reuse one protected credential reference.");
    AssertEqual(
        OnChainProviderCatalog.Get(OnChainProviderTypes.AlchemyEthereum).ProviderFamily,
        OnChainProviderCatalog.Get(OnChainProviderTypes.AlchemyBnb).ProviderFamily,
        "Same-family Alchemy BNB presets cannot reuse one protected credential reference.");
    AssertEqual(
        OnChainProviderCatalog.Get(OnChainProviderTypes.AlchemyEthereum).ProviderFamily,
        OnChainProviderCatalog.Get(OnChainProviderTypes.AlchemyRobinhood).ProviderFamily,
        "Same-family Alchemy Robinhood Chain presets cannot reuse one protected credential reference.");
    AssertEqual(
        OnChainProviderCatalog.Get(OnChainProviderTypes.InfuraEthereum).ProviderFamily,
        OnChainProviderCatalog.Get(OnChainProviderTypes.InfuraBase).ProviderFamily,
        "Same-family Infura Base presets cannot reuse one protected credential reference.");
    AssertEqual(
        OnChainProviderCatalog.Get(OnChainProviderTypes.InfuraEthereum).ProviderFamily,
        OnChainProviderCatalog.Get(OnChainProviderTypes.InfuraBnb).ProviderFamily,
        "Same-family Infura BNB presets cannot reuse one protected credential reference.");

    var baseInfura = CreateProviderConfiguration(
        OnChainProviderTypes.InfuraBase,
        "wss://base-mainnet.infura.io/ws/v3",
        "https://base-mainnet.infura.io/v3");
    var baseInfuraHttp = OnChainProviderEndpointBuilder.Build(
        baseInfura.RpcEndpoint,
        "protected-key",
        OnChainProviderCatalog.Get(baseInfura.ProviderType).RpcAuthenticationMode);
    AssertEqual("/v3/protected-key", baseInfuraHttp.AbsolutePath,
        "Infura Base authentication used the wrong URL path.");

    var bnbInfura = CreateProviderConfiguration(
        OnChainProviderTypes.InfuraBnb,
        "wss://bsc-mainnet.infura.io/ws/v3",
        "https://bsc-mainnet.infura.io/v3");
    var bnbInfuraHttp = OnChainProviderEndpointBuilder.Build(
        bnbInfura.RpcEndpoint,
        "protected-key",
        OnChainProviderCatalog.Get(bnbInfura.ProviderType).RpcAuthenticationMode);
    AssertEqual("/v3/protected-key", bnbInfuraHttp.AbsolutePath,
        "Infura BNB authentication used the wrong URL path.");

    var robinhoodAlchemy = CreateProviderConfiguration(
        OnChainProviderTypes.AlchemyRobinhood,
        "wss://robinhood-mainnet.g.alchemy.com/v2",
        "https://robinhood-mainnet.g.alchemy.com/v2");
    var robinhoodAlchemyHttp = OnChainProviderEndpointBuilder.Build(
        robinhoodAlchemy.RpcEndpoint,
        "protected-key",
        OnChainProviderCatalog.Get(robinhoodAlchemy.ProviderType).RpcAuthenticationMode);
    AssertEqual("/v2/protected-key", robinhoodAlchemyHttp.AbsolutePath,
        "Alchemy Robinhood Chain authentication used the wrong URL path.");
    Assert(!OnChainProviderCatalog.Get(OnChainProviderTypes.CustomRobinhood).RequiresCredential,
        "The custom Robinhood Chain profile unexpectedly requires a credential.");

    var bnbPublicNode = CreateProviderConfiguration(
        OnChainProviderTypes.PublicNodeBnb,
        "wss://bsc-rpc.publicnode.com",
        "https://bsc-rpc.publicnode.com");
    Assert(!OnChainProviderCatalog.Get(bnbPublicNode.ProviderType).RequiresCredential,
        "The zero-key BNB PublicNode profile incorrectly requires a credential.");

    using var handler = new EvmRpcFixtureHandler();
    using var httpClient = new HttpClient(handler);
    var probe = new EvmProviderCapabilityProbe(
        httpClient,
        (_, _, _, _, _) => Task.FromResult((false, true)));
    var snapshot = await probe.ProbeAsync(publicNode, null);
    AssertEqual(OnChainProviderCapabilityState.Supported, snapshot.ChainId,
        "The Ethereum chain-ID probe failed.");
    AssertEqual(OnChainProviderCapabilityState.Supported, snapshot.SafeBlock,
        "The Ethereum safe-block probe failed.");
    AssertEqual(OnChainProviderCapabilityState.Supported, snapshot.FinalizedBlock,
        "The Ethereum finalized-block probe failed.");
    AssertEqual(OnChainProviderCapabilityState.Supported, snapshot.BlockHashCall,
        "The pinned Multicall3 capability probe failed.");
    AssertEqual(OnChainProviderCapabilityState.Supported, snapshot.BlockHashLogs,
        "The block-hash log probe failed.");
    AssertEqual(OnChainProviderCapabilityState.Supported, snapshot.Batch,
        "The Ethereum batch probe failed.");
    AssertEqual(OnChainProviderCapabilityState.Unknown, snapshot.WebSocketHeads,
        "The sampled-head Ethereum profile still requires a WebSocket head subscription.");
    AssertEqual(OnChainProviderCapabilityState.Supported, snapshot.WebSocketLogs,
        "The Ethereum log-subscription probe failed.");

    using var malformedMulticallHandler = new EvmRpcFixtureHandler(malformedMulticall: true);
    using var malformedMulticallClient = new HttpClient(malformedMulticallHandler);
    var malformedMulticallSnapshot = await new EvmProviderCapabilityProbe(
            malformedMulticallClient,
            (_, _, _, _, _) => Task.FromResult((true, true)))
        .ProbeAsync(publicNode, null);
    AssertEqual(OnChainProviderCapabilityState.Degraded, malformedMulticallSnapshot.BlockHashCall,
        "A provider returning malformed paired state was marked Multicall3-capable.");

    using var transientHandler = new EvmRpcFixtureHandler(rateLimitFirstChainId: true);
    using var transientHttpClient = new HttpClient(transientHandler);
    var recoveredSnapshot = await new EvmProviderCapabilityProbe(
            transientHttpClient,
            (_, _, _, _, _) => Task.FromResult((true, true)))
        .ProbeAsync(publicNode, null);
    AssertEqual(OnChainProviderCapabilityState.Supported, recoveredSnapshot.SafeBlock,
        "The EVM capability probe did not recover from a transient provider rate limit.");
    AssertEqual(1, transientHandler.RateLimitedChainIdRequestCount,
        "The EVM capability retry fixture did not exercise exactly one rate limit.");

    var infura = CreateProviderConfiguration(
        OnChainProviderTypes.InfuraEthereum,
        "wss://mainnet.infura.io/ws/v3",
        "https://mainnet.infura.io/v3");
    using var infuraFallbackHandler = new InfuraBlockReferenceFallbackFixtureHandler();
    using var infuraFallbackClient = new HttpClient(infuraFallbackHandler);
    var infuraCall = await new EvmJsonRpcClient(infuraFallbackClient, infura, "infura-secret")
        .CallAsync(
            EvmChainDefinitions.EthereumMainnet.WrappedNativeAssetAddress,
            "0x313ce567",
            new
            {
                blockHash = InfuraBlockReferenceFallbackFixtureHandler.BlockHash,
                requireCanonical = true
            },
            CancellationToken.None);
    AssertEqual(JsonValueKind.String, infuraCall.ValueKind,
        "The Infura verified block-number fallback returned invalid state.");
    Assert(infuraFallbackHandler.UsedVerifiedBlockNumberCall,
        "The Infura provider did not use its verified block-number fallback.");
    AssertEqual(1, infuraFallbackHandler.BlockByHashRequestCount,
        "The Infura fallback did not resolve the requested block hash exactly once.");

    using var headDocument = JsonDocument.Parse("""
        {
          "number": "0x10",
          "hash": "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "parentHash": "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
          "timestamp": "0x20"
        }
        """);
    var head = EvmWebSocketStreamSource.ParseHead(headDocument.RootElement, "1", 7, 1000);
    AssertEqual(16UL, head.Number, "The EVM head block number was decoded incorrectly.");
    AssertEqual(7UL, head.ConnectionEpoch, "The EVM source lost its connection epoch.");

    using var logDocument = JsonDocument.Parse($$"""
        {
          "address": "0x88e6a0c2ddd26feeb64f039a2c41296fcb3f5640",
          "topics": ["{{EvmWebSocketStreamSource.UniswapV3SwapTopic}}"],
          "data": "0x00",
          "blockNumber": "0x10",
          "blockHash": "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "transactionHash": "0xcccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
          "transactionIndex": "0x2",
          "logIndex": "0x3",
          "removed": false
        }
        """);
    var log = EvmWebSocketStreamSource.ParseLog(logDocument.RootElement, "1", 7, 1001);
    AssertEqual(3UL, log.LogIndex, "The EVM log index was decoded incorrectly.");
    AssertEqual(EvmWebSocketStreamSource.UniswapV3SwapTopic, log.Topics[0],
        "The EVM log topic changed during normalization.");

    var channel = System.Threading.Channels.Channel.CreateBounded<OnChainSourceUpdate>(4);
    var fake = new ScriptedEvmSource(head, log);
    await fake.RunAsync(new OnChainStreamSubscription(), channel.Writer, CancellationToken.None);
    channel.Writer.TryComplete();
    var updates = new List<OnChainSourceUpdate>();
    await foreach (var update in channel.Reader.ReadAllAsync())
    {
        updates.Add(update);
    }
    Assert(updates[0] is OnChainSourceEvmHeadUpdate && updates[1] is OnChainSourceEvmLogUpdate,
        "The deterministic EVM source did not preserve head/log ordering.");
}

static async Task VerifyEvmRecoveryPrimitivesAsync()
{
    var tracker = new EvmHeadTracker();
    var head10 = CreateEvmHead(10, Hash(10), Hash(9));
    var head11 = CreateEvmHead(11, Hash(11), Hash(10));
    var head12 = CreateEvmHead(12, Hash(12), Hash(11));
    var head13 = CreateEvmHead(13, Hash(13), Hash(12));
    AssertEqual(EvmHeadTransitionKind.First, tracker.Apply(head10).Kind,
        "The first EVM head was not accepted.");
    AssertEqual(EvmHeadTransitionKind.Extended, tracker.Apply(head11).Kind,
        "A canonical EVM head extension was not accepted.");
    var gap = tracker.Apply(head13);
    AssertEqual(EvmHeadTransitionKind.Gap, gap.Kind,
        "A missing EVM block was not detected.");
    AssertEqual<ulong?>(12, gap.MissingFrom, "The EVM gap start is wrong.");
    AssertEqual<ulong?>(12, gap.MissingTo, "The EVM gap end is wrong.");
    AssertEqual(11UL, tracker.Tip!.Number, "A gapped head mutated canonical state.");
    tracker.Apply(head12);
    tracker.Apply(head13);

    var replacement12 = CreateEvmHead(12, Hash(112), Hash(11));
    var reorg = tracker.Apply(replacement12);
    AssertEqual(EvmHeadTransitionKind.Reorg, reorg.Kind,
        "A shallow EVM reorg was not detected.");
    AssertEqual<ulong?>(11, reorg.CommonAncestorNumber,
        "The EVM reorg common ancestor is wrong.");
    var unknownParent = CreateEvmHead(13, Hash(113), Hash(99));
    AssertEqual(EvmHeadTransitionKind.SnapshotRequired, tracker.Apply(unknownParent).Kind,
        "An EVM fork outside the canonical ring did not require a snapshot.");

    tracker.Reset(CreateEvmHead(1, Hash(1), Hash(0)));
    for (ulong number = 2; number <= EvmHeadTracker.Capacity + 2UL; number++)
    {
        tracker.Apply(CreateEvmHead(number, Hash(number), Hash(number - 1)));
    }
    AssertEqual(EvmHeadTransitionKind.SnapshotRequired,
        tracker.Apply(CreateEvmHead(3, Hash(1003), Hash(2))).Kind,
        "A fork deeper than the canonical ring was treated as reversible.");

    var finality = new EvmFinalityUpdate
    {
        ChainId = "1",
        Head = new EvmBlockReference { Number = 130, Hash = Hash(130) },
        Safe = new EvmBlockReference { Number = 125, Hash = Hash(125) },
        Finalized = new EvmBlockReference { Number = 120, Hash = Hash(120) }
    };
    Assert(tracker.ApplyFinality(finality), "Valid EVM finality was rejected.");
    Assert(!tracker.ApplyFinality(new EvmFinalityUpdate
        {
            ChainId = "1",
            Head = finality.Head,
            Safe = new EvmBlockReference { Number = 124, Hash = Hash(124) },
            Finalized = finality.Finalized
        }),
        "Regressing EVM safe finality was accepted.");
    tracker.SetSampledTip(CreateEvmHead(200, Hash(200), Hash(199)));
    AssertEqual(200UL, tracker.Tip!.Number,
        "A sampled EVM head did not replace the sparse canonical head window.");
    AssertEqual<ulong?>(125, tracker.Safe?.Number,
        "Advancing a sampled EVM head discarded safe-finality monotonicity.");
    AssertEqual<ulong?>(120, tracker.Finalized?.Number,
        "Advancing a sampled EVM head discarded finalized monotonicity.");

    var deduplicator = new EvmLogDeduplicator(2);
    var first = CreateEvmLog(10, Hash(10), Hash(100), 1);
    AssertEqual(EvmLogAcceptance.Accepted, deduplicator.Accept(first),
        "The first EVM log was rejected.");
    AssertEqual(EvmLogAcceptance.Duplicate, deduplicator.Accept(first),
        "A duplicate EVM log was accepted.");
    first.Removed = true;
    AssertEqual(EvmLogAcceptance.Removed, deduplicator.Accept(first),
        "A known removed EVM log was not reversed.");
    AssertEqual(EvmLogAcceptance.UnknownRemoval, deduplicator.Accept(first),
        "A duplicate removal was treated as a canonical event.");
    first.Removed = false;
    AssertEqual(EvmLogAcceptance.Accepted, deduplicator.Accept(first),
        "A reintroduced canonical EVM log was blocked by stale dedup state.");
    deduplicator.Accept(CreateEvmLog(11, Hash(11), Hash(101), 1));
    deduplicator.RollbackAfter(10);
    AssertEqual(EvmLogAcceptance.Accepted,
        deduplicator.Accept(CreateEvmLog(11, Hash(11), Hash(101), 1)),
        "Rollback did not release orphaned EVM log IDs.");

    var ranges = EvmRecoveryPlanner.BuildRanges(10, 260);
    AssertEqual(3, ranges.Length, "EVM recovery paging produced the wrong number of ranges.");
    AssertEqual(new EvmBlockRange(10, 109), ranges[0], "The first EVM recovery range is wrong.");
    AssertEqual(new EvmBlockRange(210, 260), ranges[2], "The last EVM recovery range is wrong.");
    AssertEqual(10UL,
        EvmStreamCoordinator.GetReplayPageSize(
            EvmChainDefinitions.RobinhoodMainnetChainId,
            OnChainProviderTypes.PublicNodeRobinhood),
        "Robinhood Chain recovery no longer respects its ten-block eth_getLogs limit.");
    AssertEqual(EvmRecoveryPlanner.DefaultPageSize,
        EvmStreamCoordinator.GetReplayPageSize(
            EvmChainDefinitions.EthereumMainnetChainId,
            OnChainProviderTypes.PublicNodeEthereum),
        "The Robinhood Chain range limit leaked into other EVM chains.");
    foreach (var (chainId, providerType) in new[]
             {
                 (EvmChainDefinitions.EthereumMainnetChainId, OnChainProviderTypes.AlchemyEthereum),
                 (EvmChainDefinitions.BaseMainnetChainId, OnChainProviderTypes.AlchemyBase),
                 (EvmChainDefinitions.BnbMainnetChainId, OnChainProviderTypes.AlchemyBnb),
                 (EvmChainDefinitions.RobinhoodMainnetChainId, OnChainProviderTypes.AlchemyRobinhood)
             })
    {
        var pageSize = EvmStreamCoordinator.GetReplayPageSize(chainId, providerType);
        AssertEqual(10UL, pageSize,
            $"Alchemy {chainId} replay exceeded its Free-tier eth_getLogs range.");
        var alchemyRanges = EvmRecoveryPlanner.BuildRanges(10, 109, pageSize);
        AssertEqual(10, alchemyRanges.Length,
            $"Alchemy {chainId} replay discarded history instead of paging it.");
        AssertEqual(new EvmBlockRange(100, 109), alchemyRanges[^1],
            $"Alchemy {chainId} replay lost its final ten-block page.");
    }
    foreach (var chain in EvmChainDefinitions.Supported)
    {
        Assert(EvmStreamCoordinator.UsesSampledHeads(chain.ChainId),
            $"{chain.DisplayName} returned to its quota-exhausting per-block head stream.");
        Assert(!EvmWebSocketStreamSource.ShouldSubscribeToHeads(chain.ChainId),
            $"{chain.DisplayName} still requests the metered chain-wide newHeads stream.");
        AssertEqual(TimeSpan.FromMinutes(1),
            EvmStreamCoordinator.GetHeadSampleInterval(chain.ChainId),
            $"{chain.DisplayName} canonical-head sampling is no longer quota bounded.");
        AssertEqual(TimeSpan.FromMinutes(5),
            EvmStreamCoordinator.GetSampledSnapshotInterval(chain.ChainId),
            $"{chain.DisplayName} state reconciliation is no longer quota bounded.");
    }
    Assert(!EvmStreamCoordinator.UsesSampledHeads("999999"),
        "Sampled-head behavior was enabled for an unsupported EVM chain.");
    Assert(EvmWebSocketStreamSource.ShouldSubscribeToHeads("999999"),
        "An unsupported/custom EVM chain unexpectedly lost its head subscription.");
    AssertEqual(TimeSpan.FromMinutes(1), EvmWalletActivityStreamSource.FinalityRefreshInterval,
        "EVM Wallet Watcher finality polling returned to its quota-heavy cadence.");
    AssertEqual(TimeSpan.FromSeconds(5), SolanaWalletActivityStreamSource.MaintenanceInterval,
        "Solana Wallet Watcher pending-signature maintenance returned to its quota-heavy cadence.");
    AssertEqual(TimeSpan.FromMinutes(5), SolanaWalletActivityStreamSource.TokenAccountRefreshInterval,
        "Solana Wallet Watcher token-account repair returned to its quota-heavy cadence.");
    AssertEqual(512, EvmWalletActivityStreamSource.MaximumTokenMetadataEntries,
        "The EVM Wallet Watcher token metadata cache lost its resource bound.");
    AssertEqual(TimeSpan.FromSeconds(5), EvmWalletActivityStreamSource.TraceBatchInterval,
        "EVM Wallet Watcher live trace batching returned to a per-block cadence.");
    AssertEqual(20, EvmWalletActivityStreamSource.MaximumTraceBatchBlocks,
        "EVM Wallet Watcher live trace batching lost its reconnect-safe block bound.");
    Assert(SolanaWebSocketStreamSource.GetRuntimeSubscriptionMethods(
            ["account-a", "account-b"],
            ["pool-a"])
        .SequenceEqual(["accountSubscribe", "accountSubscribe", "logsSubscribe"]),
        "The retained exact Solana WebSocket adapter reintroduced the unused slot firehose.");
    AssertEqual(TimeSpan.FromSeconds(1), SolanaRpcSampleStreamSource.SampleInterval,
        "Solana price-only sampling lost its bounded cadence.");
    var solanaPreset = OnChainProviderConfigurationStore.CreateConfiguration(
        OnChainProviderCatalog.Get(OnChainProviderTypes.AlchemyWebSocket));
    Assert(OnChainStreamCoordinator.CreateSource(solanaPreset, "fixture-key", true)
           is SolanaWebSocketStreamSource,
        "The explicit Solana WebSocket mode did not select the exact stream source.");
    var traceBatch = new EvmWalletTraceBatch(
        EvmWalletActivityStreamSource.MaximumTraceBatchBlocks);
    for (ulong block = 100; block < 120; block++)
    {
        traceBatch.Add(block, checked((long)block * 1000));
    }
    AssertEqual(20, traceBatch.Count, "EVM wallet traces were not batched to the configured bound.");
    Assert(!traceBatch.CanAdd(120, 120),
        "EVM wallet trace batching accepted a range beyond its reconnect-safe bound.");
    traceBatch.RemoveAfter(109);
    AssertEqual(10, traceBatch.Count, "EVM wallet trace reorg rollback retained orphaned blocks.");
    Assert(traceBatch.CanAdd(110, 119),
        "EVM wallet trace reorg rollback did not release the canonical replacement range.");
    traceBatch.Clear();
    AssertEqual(0, traceBatch.Count, "EVM wallet trace batch did not clear after publication.");
    var monthlyHeadAndFinalityComputeUnits =
        TimeSpan.FromDays(30).TotalMinutes * 4 * 20;
    Assert(monthlyHeadAndFinalityComputeUnits < 4_000_000,
        "The per-chain sampled-head policy exceeds its bounded monthly RPC budget.");
    AssertEqual(94UL, EvmRecoveryPlanner.GetRecoveryStart(100, 110),
        "The EVM checkpoint overlap is wrong.");
    AssertEqual(100UL, EvmRecoveryPlanner.ApplyReplayDepthLimit(100, 599, 500),
        "A replay range inside the provider depth was discarded.");
    AssertEqual(600UL, EvmRecoveryPlanner.ApplyReplayDepthLimit(100, 600, 500),
        "An unavailable deep replay range did not fall back to a current snapshot.");
    AssertEqual(61UL, EvmStreamCoordinator.GetProviderReplayStart(
            OnChainProviderTypes.PublicNodeRobinhood, 0, 100),
        "Robinhood PublicNode startup replay exceeded its verified recent-log window.");
    AssertEqual(95UL, EvmStreamCoordinator.GetProviderReplayStart(
            OnChainProviderTypes.PublicNodeRobinhood, 95, 100),
        "A recent Robinhood PublicNode checkpoint was widened unnecessarily.");
    AssertEqual(0UL, EvmStreamCoordinator.GetProviderReplayStart(
            OnChainProviderTypes.PublicNodeRobinhood, 0, 20),
        "Robinhood PublicNode replay underflowed near genesis.");
    AssertEqual(0UL, EvmStreamCoordinator.GetProviderReplayStart(
            OnChainProviderTypes.AlchemyRobinhood, 0, 100),
        "The public-endpoint replay limit changed the saved Alchemy route.");

    var temporaryRoot = Path.Combine(
        Path.GetTempPath(),
        "TrenchHQEvmCheckpoints",
        Guid.NewGuid().ToString("N"));
    try
    {
        var checkpoint = new EvmCheckpoint
        {
            ChainId = "1",
            SafeBlockNumber = 120,
            SafeBlockHash = Hash(120),
            ObservedAtUnixMs = 1000
        };
        await EvmCheckpointStore.SaveAsync(temporaryRoot, "profile-1", checkpoint);
        var loaded = await EvmCheckpointStore.LoadAsync(temporaryRoot, "profile-1");
        AssertEqual(120UL, loaded!.SafeBlockNumber, "The EVM checkpoint block changed.");
        AssertEqual(Hash(120), loaded.SafeBlockHash, "The EVM checkpoint hash changed.");
    }
    finally
    {
        if (Directory.Exists(temporaryRoot))
        {
            Directory.Delete(temporaryRoot, true);
        }
    }
}

static async Task VerifyUniswapV2DiscoveryAndEngineAsync(string enginePath)
{
    const string selectedToken = "0x1111111111111111111111111111111111111111";
    const string pair = "0x2222222222222222222222222222222222222222";
    const string v3Pool = "0x3333333333333333333333333333333333333333";
    using var handler = new UniswapDiscoveryFixtureHandler(
        EvmChainDefinitions.EthereumMainnetChainId,
        EthereumDeploymentRegistry.Catalog,
        selectedToken,
        pair,
        v3Pool);
    using var httpClient = new HttpClient(handler);
    var configuration = CreateProviderConfiguration(
        OnChainProviderTypes.PublicNodeEthereum,
        "wss://ethereum-rpc.publicnode.com",
        "https://ethereum-rpc.publicnode.com");
    var rpc = new EvmJsonRpcClient(httpClient, configuration, null);
    var discovery = new EthereumPoolDiscoveryService(rpc);
    var result = await discovery.DiscoverAsync(selectedToken);
    AssertEqual(2, result.Pools.Length,
        "Bounded Uniswap factory discovery returned the wrong number of pools.");
    var descriptor = result.Pools.Single(pool =>
        pool.PoolKey.ProtocolId == OnChainProtocolIds.UniswapV2);
    AssertEqual(OnChainProtocolIds.UniswapV2, descriptor.PoolKey.ProtocolId,
        "The discovered Uniswap v2 protocol identity is wrong.");
    AssertEqual(pair, descriptor.PoolKey.PoolId,
        "The discovered Uniswap v2 pair identity is wrong.");
    AssertEqual(OnChainSupportStatus.Supported, descriptor.SupportStatus,
        "A fully validated Uniswap v2 pair was not marked supported.");
    var v3Descriptor = result.Pools.Single(pool =>
        pool.PoolKey.ProtocolId == OnChainProtocolIds.UniswapV3);
    AssertEqual(v3Pool, v3Descriptor.PoolKey.PoolId,
        "The discovered Uniswap v3 pool identity is wrong.");
    AssertEqual(500U, v3Descriptor.FeeTier,
        "The discovered Uniswap v3 fee tier is wrong.");
    AssertEqual(10, v3Descriptor.TickSpacing,
        "The discovered Uniswap v3 tick spacing is wrong.");
    Assert(handler.CallCount <= 50,
        "Uniswap discovery exceeded its bounded factory-call budget.");
    Assert(!handler.SawUnpinnedStateRead,
        "Uniswap v2 discovery performed a mutable-latest state read.");

    var client = new OnChainEngineClient(enginePath);
    var prices = new ConcurrentQueue<OnChainPriceUpdate>();
    client.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
    try
    {
        await client.ReplaceWatchedPoolsAsync(
        [
            new OnChainWatchedPoolSelection
            {
                Descriptor = descriptor,
                SelectedMint = selectedToken
            }
        ]);
        var blockHash = Hash(16);
        await client.PublishEvmHeadAsync(CreateEvmHead(16, blockHash, Hash(15)));
        await client.PublishEvmSnapshotAsync(new EvmSnapshotResponse
        {
            RequestId = "v2-snapshot-16",
            PoolKey = descriptor.PoolKey,
            BlockNumber = 16,
            BlockHash = blockHash,
            Calls =
            [
                new EvmSnapshotCall
                {
                    Id = "getReserves",
                    Success = true,
                    ReturnData = AbiWords(
                        8_000_000_000_000_000_000UL,
                        16_000_000_000_000_000_000UL,
                        123)
                }
            ]
        });
        await WaitUntilAsync(() => prices.Count == 1, TimeSpan.FromSeconds(5),
            "Uniswap v2 reserve snapshot price");
        AssertEqual("2000000000000000000", prices.Last().SpotPriceQuote?.Coefficient,
            "The Uniswap v2 reserve spot price is wrong.");

        var swap = new EvmLogUpdate
        {
            ChainId = "1",
            ConnectionEpoch = 7,
            Address = pair,
            Topics =
            [
                EvmWebSocketStreamSource.UniswapV2SwapTopic,
                Hash(1000),
                Hash(1001)
            ],
            Data = AbiWords(
                2_000_000_000_000_000_000UL,
                0,
                0,
                4_000_000_000_000_000_000UL),
            BlockNumber = 16,
            BlockHash = blockHash,
            TransactionHash = Hash(2000),
            TransactionIndex = 3,
            LogIndex = 4,
            ObservedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        await client.PublishEvmLogAsync(swap);
        await WaitUntilAsync(() => prices.Count == 2, TimeSpan.FromSeconds(5),
            "Uniswap v2 executed trade price");
        var trade = prices.Last();
        AssertEqual("2000000000000000000", trade.LastTradePriceQuote?.Coefficient,
            "The Uniswap v2 executed price is wrong.");
        AssertEqual(EvmFinality.Head, trade.Finality?.Status,
            "A new EVM trade was not marked as head/tentative.");
        AssertEqual(16UL, trade.ChainPosition?.BlockNumber,
            "The EVM block position was lost at the engine boundary.");

        await client.PublishEvmLogAsync(swap);
        await Task.Delay(150);
        AssertEqual(2, prices.Count,
            "A duplicate Uniswap v2 log produced another price update.");

        await client.PublishEvmFinalityAsync(new EvmFinalityUpdate
        {
            ChainId = "1",
            Head = new EvmBlockReference { Number = 16, Hash = blockHash },
            Safe = new EvmBlockReference { Number = 16, Hash = blockHash },
            ObservedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
        await WaitUntilAsync(() => prices.Count == 3, TimeSpan.FromSeconds(5),
            "Uniswap v2 safe finality promotion");
        AssertEqual(EvmFinality.Safe, prices.Last().Finality?.Status,
            "The Uniswap v2 price did not advance to safe finality.");

        swap.Removed = true;
        await client.PublishEvmLogAsync(swap);
        await WaitUntilAsync(() => prices.Count == 4, TimeSpan.FromSeconds(5),
            "Uniswap v2 removed-log rollback");
        Assert(prices.Last().LastTradePriceQuote == null
               && prices.Last().SpotPriceQuote != null,
            "A removed Uniswap v2 trade did not restore the prior reserve snapshot.");

        while (prices.TryDequeue(out _))
        {
        }
        await client.ReplaceWatchedPoolsAsync(
        [
            new OnChainWatchedPoolSelection
            {
                Descriptor = v3Descriptor,
                SelectedMint = selectedToken
            }
        ]);
        var q96 = System.Numerics.BigInteger.One << 96;
        await client.PublishEvmSnapshotAsync(new EvmSnapshotResponse
        {
            RequestId = "v3-snapshot-16",
            PoolKey = v3Descriptor.PoolKey,
            BlockNumber = 16,
            BlockHash = blockHash,
            Calls =
            [
                new EvmSnapshotCall
                {
                    Id = "slot0",
                    Success = true,
                    ReturnData = AbiBigWords(q96, 0, 0, 0, 0, 0, 1)
                },
                new EvmSnapshotCall
                {
                    Id = "liquidity",
                    Success = true,
                    ReturnData = AbiBigWords(100)
                }
            ]
        });
        await WaitUntilAsync(() => prices.Count == 1, TimeSpan.FromSeconds(5),
            "Uniswap v3 slot0 snapshot price");
        AssertEqual("1000000000000000000", prices.Last().SpotPriceQuote?.Coefficient,
            "The Uniswap v3 Q64.96 snapshot price is wrong.");

        await client.PublishEvmLogAsync(new EvmLogUpdate
        {
            ChainId = "1",
            ConnectionEpoch = 8,
            Address = v3Pool,
            Topics =
            [
                EvmWebSocketStreamSource.UniswapV3SwapTopic,
                Hash(3000),
                Hash(3001)
            ],
            Data = AbiBigWords(
                2_000_000_000_000_000_000,
                -4_000_000_000_000_000_000,
                q96,
                100,
                0),
            BlockNumber = 17,
            BlockHash = Hash(17),
            TransactionHash = Hash(3002),
            TransactionIndex = 1,
            LogIndex = 2,
            ObservedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
        await WaitUntilAsync(() => prices.Count == 2, TimeSpan.FromSeconds(5),
            "Uniswap v3 swap price");
        AssertEqual("2000000000000000000", prices.Last().LastTradePriceQuote?.Coefficient,
            "The Uniswap v3 signed-delta execution price is wrong.");
        AssertEqual("1000000000000000000", prices.Last().SpotPriceQuote?.Coefficient,
            "The Uniswap v3 post-swap sqrt price is wrong.");
    }
    finally
    {
        await client.StopAsync();
    }
}

static async Task VerifyBaseUniswapDiscoveryAsync()
{
    const string selectedToken = "0x1111111111111111111111111111111111111111";
    const string pair = "0x2222222222222222222222222222222222222222";
    const string v3Pool = "0x3333333333333333333333333333333333333333";
    using var handler = new UniswapDiscoveryFixtureHandler(
        EvmChainDefinitions.BaseMainnetChainId,
        BaseDeploymentRegistry.Catalog,
        selectedToken,
        pair,
        v3Pool);
    using var httpClient = new HttpClient(handler);
    var configuration = CreateProviderConfiguration(
        OnChainProviderTypes.BasePublic,
        "wss://mainnet.base.org",
        "https://mainnet.base.org");
    var discovery = new EthereumPoolDiscoveryService(
        new EvmJsonRpcClient(httpClient, configuration, null),
        EvmChainDefinitions.BaseMainnet,
        BaseDeploymentRegistry.Catalog,
        httpClient);
    var v2Result = await discovery.DiscoverUniswapV2Async(selectedToken);
    var v3Result = await discovery.DiscoverUniswapV3Async(selectedToken);
    var discoveredPools = v2Result.Pools.Concat(v3Result.Pools).ToArray();

    AssertEqual(2, discoveredPools.Length,
        "Bounded Base Uniswap discovery returned the wrong number of pools.");
    Assert(discoveredPools.All(pool => pool.PoolKey.ChainId == EvmChainDefinitions.BaseMainnetChainId),
        "Base discovery emitted a pool with the wrong chain identity.");
    AssertEqual(pair, discoveredPools.Single(pool =>
            pool.PoolKey.ProtocolId == OnChainProtocolIds.UniswapV2).PoolKey.PoolId,
        "Base V2 factory discovery returned the wrong pair.");
    var v3 = discoveredPools.Single(pool => pool.PoolKey.ProtocolId == OnChainProtocolIds.UniswapV3);
    AssertEqual(v3Pool, v3.PoolKey.PoolId,
        "Base V3 factory discovery returned the wrong pool.");
    AssertEqual(500U, v3.FeeTier,
        "Base V3 discovery returned the wrong fee tier.");
    Assert(handler.CallCount <= 50,
        "Base Uniswap discovery exceeded its bounded factory-call budget.");
    Assert(!handler.SawUnpinnedStateRead,
        "Base discovery performed a mutable-latest state read.");

    using var rateLimitedHandler = new UniswapDiscoveryFixtureHandler(
        EvmChainDefinitions.BaseMainnetChainId,
        BaseDeploymentRegistry.Catalog,
        selectedToken,
        pair,
        v3Pool,
        aerodromeRateLimited: true);
    using var rateLimitedHttpClient = new HttpClient(rateLimitedHandler);
    var partialResult = await new EthereumPoolDiscoveryService(
            new EvmJsonRpcClient(rateLimitedHttpClient, configuration, null),
            EvmChainDefinitions.BaseMainnet,
            BaseDeploymentRegistry.Catalog,
            rateLimitedHttpClient)
        .DiscoverAsync(selectedToken);
    AssertEqual(2, partialResult.Pools.Length,
        "An Aerodrome rate limit discarded independently validated Base Uniswap pools.");
    Assert(partialResult.Warnings.Any(static warning =>
            warning.Contains("Aerodrome discovery was incomplete", StringComparison.Ordinal)
            && warning.Contains("rate limit", StringComparison.OrdinalIgnoreCase)),
        "A partial Base discovery result did not explain the Aerodrome rate limit.");
}

static async Task VerifyEvmCoordinatorCollectionAsync(string enginePath)
{
    var client = new OnChainEngineClient(enginePath);
    var coordinators = new EvmStreamCoordinatorCollection(
        client,
        Path.GetTempPath());
    AssertEqual(4, coordinators.Coordinators.Count,
        "The fixed EVM coordinator collection did not register all supported chains.");
    AssertEqual(EvmChainDefinitions.EthereumMainnetChainId,
        coordinators.Get(EvmChainDefinitions.EthereumMainnetChainId).ChainDefinition.ChainId,
        "The coordinator collection returned the wrong Ethereum owner.");
    AssertEqual(EvmChainDefinitions.BaseMainnetChainId,
        coordinators.Get(EvmChainDefinitions.BaseMainnetChainId).ChainDefinition.ChainId,
        "The coordinator collection returned the wrong Base owner.");
    AssertEqual(EvmChainDefinitions.BnbMainnetChainId,
        coordinators.Get(EvmChainDefinitions.BnbMainnetChainId).ChainDefinition.ChainId,
        "The coordinator collection returned the wrong BNB owner.");
    AssertEqual(EvmChainDefinitions.RobinhoodMainnetChainId,
        coordinators.Get(EvmChainDefinitions.RobinhoodMainnetChainId).ChainDefinition.ChainId,
        "The coordinator collection returned the wrong Robinhood Chain owner.");
    await coordinators.StopAsync();
    await client.StopAsync();
}

static async Task VerifyMixedChainWatchSetMatrixAsync(string enginePath)
{
    var solanaMintBytes = Enumerable.Repeat((byte)37, 32).ToArray();
    var solana = CreateMixedMatrixSolanaSelection(solanaMintBytes);
    const string stockQuote = "0x3333333333333333333333333333333333333333";
    var pools = new Dictionary<string, (EvmChainDefinition Chain, OnChainWatchedPoolSelection Selection, int QuoteDecimals)>
    {
        ["ETH/USDC"] = (EvmChainDefinitions.EthereumMainnet, CreatePool(
            EvmChainDefinitions.EthereumMainnet, OnChainProtocolIds.UniswapV2,
            EthereumDeploymentRegistry.UniswapV2Factory, EthereumDeploymentRegistry.Usdc,
            "0x0000000000000000000000000000000000000101"), 6),
        ["BASE/USDC"] = (EvmChainDefinitions.BaseMainnet, CreatePool(
            EvmChainDefinitions.BaseMainnet, OnChainProtocolIds.UniswapV2,
            BaseDeploymentRegistry.UniswapV2Factory, BaseDeploymentRegistry.Usdc,
            "0x0000000000000000000000000000000000000102"), 6),
        ["BNB/USDT"] = (EvmChainDefinitions.BnbMainnet, CreatePool(
            EvmChainDefinitions.BnbMainnet, OnChainProtocolIds.PancakeV2,
            BnbDeploymentRegistry.PancakeV2Factory, BnbDeploymentRegistry.Usdt,
            "0x0000000000000000000000000000000000000103"), 18),
        ["RH-STOCK/USDG"] = (EvmChainDefinitions.RobinhoodMainnet, CreatePool(
            EvmChainDefinitions.RobinhoodMainnet, OnChainProtocolIds.UniswapV2,
            RobinhoodDeploymentRegistry.UniswapV2Factory, RobinhoodDeploymentRegistry.Usdg,
            "0x0000000000000000000000000000000000000104"), 18),
        ["RH-STOCK/STOCK"] = (EvmChainDefinitions.RobinhoodMainnet, CreatePool(
            EvmChainDefinitions.RobinhoodMainnet, OnChainProtocolIds.UniswapV2,
            RobinhoodDeploymentRegistry.UniswapV2Factory, stockQuote,
            "0x0000000000000000000000000000000000000105"), 18)
    };
    var stages = new (string Name, string[] Names)[]
    {
        ("2", ["SOL", "RH-STOCK/USDG"]),
        ("3", ["SOL", "ETH/USDC", "BNB/USDT"]),
        ("5-usd", ["SOL", "ETH/USDC", "BASE/USDC", "BNB/USDT", "RH-STOCK/USDG"]),
        ("5-stock", ["SOL", "ETH/USDC", "BASE/USDC", "RH-STOCK/USDG", "RH-STOCK/STOCK"])
    };
    var baselineProcessIds = GetEngineProcessIds();
    var client = new OnChainEngineClient(enginePath);
    var prices = new ConcurrentQueue<OnChainPriceUpdate>();
    client.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
    try
    {
        int? sharedProcessId = null;
        for (var stageIndex = 0; stageIndex < stages.Length; stageIndex++)
        {
            var (stageName, names) = stages[stageIndex];
            foreach (var chain in EvmChainDefinitions.Supported)
            {
                var selectedOnChain = names.Where(pools.ContainsKey)
                    .Select(name => pools[name])
                    .Where(pool => pool.Chain.ChainId == chain.ChainId)
                    .Select(pool => pool.Selection).ToArray();
                var filters = JsonSerializer.Serialize(
                    EvmWebSocketStreamSource.CreateRpcLogFilters(selectedOnChain, 1, 2));
                Assert(pools.Values.Where(pool => pool.Chain.ChainId == chain.ChainId)
                        .All(pool => filters.Contains(pool.Selection.Descriptor.PoolKey.PoolId,
                                         StringComparison.OrdinalIgnoreCase)
                                     == selectedOnChain.Contains(pool.Selection)),
                    $"The {stageName}-coin {chain.DisplayName} subscription includes a deselected pool or omits a selected pool.");
                await client.ReplaceWatchedPoolsAsync(chain.EnginePoolOwner, selectedOnChain);
                if (selectedOnChain.Length > 0)
                {
                    await client.SetEvmProviderContextAsync(chain.ChainId,
                        $"matrix-{chain.ChainId}", $"fixture:{chain.ChainId}");
                }
            }
            await client.ReplaceWatchedPoolsAsync("solana:mainnet-beta", [solana]);
            var processId = GetSingleNewEngineProcessId(baselineProcessIds);
            sharedProcessId ??= processId;
            AssertEqual(sharedProcessId.Value, processId,
                $"The {stageName}-coin change restarted the shared engine.");

            var before = prices.Count;
            await client.PublishTransactionUpdateAsync(CreatePumpTrade($"matrix-{stageName}", solanaMintBytes));
            foreach (var (name, pool) in pools)
            {
                if (!names.Contains(name, StringComparer.Ordinal))
                {
                    continue;
                }
                await client.PublishEvmSnapshotAsync(new EvmSnapshotResponse
                {
                    RequestId = $"matrix-{stageName}-{name}",
                    PoolKey = pool.Selection.Descriptor.PoolKey,
                    BlockNumber = (ulong)(20 + stageIndex),
                    BlockHash = Hash((ulong)(20 + stageIndex)),
                    Calls =
                    [
                        new EvmSnapshotCall
                        {
                            Id = "getReserves",
                            Success = true,
                            ReturnData = AbiBigWords(
                                System.Numerics.BigInteger.Pow(10, 18),
                                2 * System.Numerics.BigInteger.Pow(10, pool.QuoteDecimals),
                                123)
                        }
                    ]
                });
            }
            await WaitUntilAsync(() => prices.Count >= before + names.Length,
                TimeSpan.FromSeconds(5), $"{stageName}-coin mixed-chain prices");
            await Task.Delay(100);
            var stagePrices = prices.Skip(before).ToArray();
            AssertEqual(names.Length, stagePrices.Length,
                $"The {stageName}-coin watch set emitted missing or deselected prices.");
            foreach (var name in names.Where(name => name != "SOL"))
            {
                var pool = pools[name];
                var update = stagePrices.Single(price =>
                    price.PoolKey.ChainId == pool.Chain.ChainId
                    && price.PoolKey.PoolId == pool.Selection.Descriptor.PoolKey.PoolId);
                AssertDecimalValue(update.SpotPriceQuote, 2,
                    $"The {stageName}-coin {name} quote price was wrong.");
                AssertEqual($"matrix-{pool.Chain.ChainId}", update.ProviderProfileId,
                    $"The {stageName}-coin {name} price used another chain's provider.");
                AssertEqual($"fixture:{pool.Chain.ChainId}", update.SourceId,
                    $"The {stageName}-coin {name} price lost its chain source.");
                if (name == "RH-STOCK/STOCK")
                {
                    Assert(update.PriceUsd == null,
                        "A stock-token quote was incorrectly treated as USD.");
                }
                else
                {
                    AssertDecimalValue(update.PriceUsd, 2,
                        $"The {stageName}-coin {name} stable-quoted USD price was wrong.");
                }
            }
            Assert(stagePrices.Count(price => price.PoolKey.PoolAddress == solana.Descriptor.PoolKey.PoolAddress) == 1,
                $"The {stageName}-coin Solana selection was lost or duplicated.");
            Console.WriteLine($"MIXED MATRIX | stage={stageName} | prices={stagePrices.Length} | chains={string.Join(',', names)} | engine={processId}");
        }
        foreach (var chain in EvmChainDefinitions.Supported)
        {
            await client.ReplaceWatchedPoolsAsync(chain.EnginePoolOwner, []);
        }
        await client.ReplaceWatchedPoolsAsync("solana:mainnet-beta", []);
        await WaitUntilAsync(() => !GetEngineProcessIds().Except(baselineProcessIds).Any(),
            TimeSpan.FromSeconds(5), "mixed-chain lazy engine stop");
    }
    finally
    {
        await client.StopAsync();
    }

    static OnChainWatchedPoolSelection CreatePool(
        EvmChainDefinition chain, string protocol, string factory, string quote, string address)
    {
        var token = "0x1111111111111111111111111111111111111111";
        var catalog = chain.ChainId switch
        {
            EvmChainDefinitions.EthereumMainnetChainId => EthereumDeploymentRegistry.Catalog,
            EvmChainDefinitions.BaseMainnetChainId => BaseDeploymentRegistry.Catalog,
            EvmChainDefinitions.BnbMainnetChainId => BnbDeploymentRegistry.Catalog,
            _ => RobinhoodDeploymentRegistry.Catalog
        };
        return new OnChainWatchedPoolSelection
        {
            SelectedMint = token,
            Descriptor = new OnChainPoolDescriptor
            {
                PoolKey = new OnChainPoolKey
                {
                    DeploymentKey = catalog.Deployment(protocol, factory),
                    PoolId = address
                },
                PoolType = "constantProduct",
                ProgramId = factory,
                BaseMint = token,
                QuoteMint = quote,
                BaseDecimals = 18,
                QuoteDecimals = (byte)(quote == EthereumDeploymentRegistry.Usdc
                                       || quote == BaseDeploymentRegistry.Usdc ? 6 : 18),
                PairOrientation = "selectedAsToken0",
                SupportStatus = OnChainSupportStatus.Supported,
                Asset0 = new OnChainAssetKey
                {
                    ChainNamespace = ChainNamespaces.Eip155,
                    ChainId = chain.ChainId,
                    Address = token
                },
                Asset1 = new OnChainAssetKey
                {
                    ChainNamespace = ChainNamespaces.Eip155,
                    ChainId = chain.ChainId,
                    Address = quote
                }
            }
        };
    }
}

static OnChainWatchedPoolSelection CreateMixedMatrixSolanaSelection(byte[] mintBytes)
{
    var mint = EncodeBase58(mintBytes);
    return new OnChainWatchedPoolSelection
    {
        SelectedMint = mint,
        Descriptor = new OnChainPoolDescriptor
        {
            PoolKey = new OnChainPoolKey
            {
                ProtocolId = OnChainProtocolIds.PumpBondingCurve,
                PoolAddress = "mixed-chain-solana-curve"
            },
            PoolType = "bondingCurve",
            ProgramId = "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P",
            BaseMint = mint,
            QuoteMint = "So11111111111111111111111111111111111111112",
            BaseDecimals = 6,
            QuoteDecimals = 9,
            SupportStatus = OnChainSupportStatus.Supported
        }
    };
}

static async Task VerifyRobinhoodStockTokenCatalogAsync()
{
    const string nvda = "0x1111111111111111111111111111111111111111";
    const string gld = "0x3333333333333333333333333333333333333333";
    using var handler = new RobinhoodStockTokenApiFixtureHandler(nvda, gld);
    using var httpClient = new HttpClient(handler);
    var catalog = new RobinhoodStockTokenCatalogClient(httpClient);

    var bySymbol = await catalog.ResolveAsync("nvda")
                   ?? throw new InvalidOperationException(
                       "The official Robinhood Stock Token symbol did not resolve.");
    AssertEqual(nvda, bySymbol.Address, "Stock Token search returned the wrong deployment address.");
    AssertEqual("NVDA", bySymbol.Symbol, "Stock Token search changed the official symbol.");
    AssertEqual("NVIDIA Stock Token", bySymbol.Name, "Stock Token search changed the official name.");
    var byName = await catalog.ResolveAsync("NVIDIA Stock Token");
    AssertEqual(nvda, byName?.Address, "Exact Stock Token name search did not reuse the catalog.");
    Assert(catalog.TryGetCachedByAddress(nvda, out var cached) && cached.Symbol == "NVDA",
        "The Stock Token catalog did not expose the validated address cache.");

    var reference = await catalog.GetUsdReferenceAsync(nvda)
                    ?? throw new InvalidOperationException(
                        "The official Stock Token price did not resolve.");
    AssertEqual("501", reference.Value.Coefficient,
        "The bid/ask midpoint and currentMultiplier were not applied exactly.");
    AssertEqual<uint>(1, reference.Value.Scale,
        "The Stock Token reference decimal was not normalized exactly.");
    AssertEqual("robinhood-stock-token-api:NVDA", reference.SourceId,
        "The Stock Token price lost its source identity.");
    var cachedReference = await catalog.GetUsdReferenceAsync(nvda);
    AssertEqual(reference, cachedReference, "The 15-second Stock Token price cache changed the value.");
    AssertEqual(1, handler.AssetRequests, "The five-minute Stock Token asset cache was bypassed.");
    AssertEqual(1, handler.PriceRequests, "The 15-second Stock Token price cache was bypassed.");

    var wideSpreadReference = await catalog.GetUsdReferenceAsync(gld)
                              ?? throw new InvalidOperationException(
                                  "The corroborated side of a wide Stock Token spread did not resolve.");
    AssertEqual("40122", wideSpreadReference.Value.Coefficient,
        "A wide bid/ask spread used the distorted midpoint instead of the side inside the daily range.");
    AssertEqual<uint>(2, wideSpreadReference.Value.Scale,
        "The corroborated wide-spread Stock Token reference was not normalized exactly.");
    AssertEqual(2, handler.PriceRequests, "The second Stock Token price was not requested exactly once.");

    var ponsCurve = new OnChainPoolDescriptor
    {
        PoolKey = new OnChainPoolKey
        {
            DeploymentKey = RobinhoodDeploymentRegistry.Catalog.Deployment(
                OnChainProtocolIds.PonsV2Curve,
                RobinhoodDeploymentRegistry.PonsV2Factory),
            PoolId = "0x2222222222222222222222222222222222222222"
        },
        PoolType = "constantProductBondingCurve",
        ProgramId = RobinhoodDeploymentRegistry.PonsV2Factory,
        BaseMint = nvda,
        QuoteMint = RobinhoodDeploymentRegistry.WrappedEther,
        BaseDecimals = 18,
        QuoteDecimals = 18,
        SupportStatus = OnChainSupportStatus.Supported
    };
    var filters = JsonSerializer.Serialize(EvmWebSocketStreamSource.CreateRpcLogFilters(
    [
        new OnChainWatchedPoolSelection { SelectedMint = nvda, Descriptor = ponsCurve }
    ],
    1,
    2));
    Assert(filters.Contains(ponsCurve.PoolKey.PoolId, StringComparison.OrdinalIgnoreCase),
        "Pons v2 curve replay did not target the exact launched curve.");
    Assert(filters.Contains(EvmWebSocketStreamSource.PonsV2CurveBuyTopic, StringComparison.Ordinal)
           && filters.Contains(EvmWebSocketStreamSource.PonsV2CurveSellTopic, StringComparison.Ordinal),
        "Pons v2 curve replay omitted an official trade event.");
    Assert(RobinhoodDeploymentRegistry.PonsV2Factory.Equals(
            "0x7eD598BcEf8bd9Edd8C97A195C6d13f40801EC7e",
            StringComparison.OrdinalIgnoreCase),
        "The allowlisted pons v2 factory changed.");
    Assert(RobinhoodDeploymentRegistry.DopplerAirlock.Equals(
            "0xeb7c034704ef8dcd2d32324c1545f62fb4ad0862",
            StringComparison.OrdinalIgnoreCase),
        "The allowlisted Doppler Airlock changed.");
}

static async Task VerifyRobinhoodUniswapDiscoveryAndEngineAsync(string enginePath)
{
    const string selectedToken = "0x1111111111111111111111111111111111111111";
    const string v2Pair = "0x2222222222222222222222222222222222222222";
    const string v3Pool = "0x3333333333333333333333333333333333333333";
    const string v4PoolId =
        "0x4444444444444444444444444444444444444444444444444444444444444444";
    var fixtures = new[]
    {
        new EvmCatalogPoolFixture(
            v2Pair,
            RobinhoodDeploymentRegistry.WrappedEther,
            OnChainProtocolIds.UniswapV2,
            RobinhoodDeploymentRegistry.UniswapV2Factory),
        new EvmCatalogPoolFixture(
            v3Pool,
            RobinhoodDeploymentRegistry.Usdg,
            OnChainProtocolIds.UniswapV3,
            RobinhoodDeploymentRegistry.UniswapV3Factory,
            FeeTier: 100,
            TickSpacing: 1),
        new EvmCatalogPoolFixture(
            v4PoolId,
            RobinhoodDeploymentRegistry.Usdg,
            OnChainProtocolIds.UniswapV4,
            RobinhoodDeploymentRegistry.UniswapV4PoolManager,
            FeeTier: 100,
            TickSpacing: 1,
            CreatedAtUnixMs: 32000)
    };
    var v4Catalog = new PoolCatalogEntry
    {
        SourceId = "dexscreener",
        ChainId = "robinhood",
        ProtocolId = "uniswap",
        PoolAddress = v4PoolId,
        Labels = ["v4"],
        BaseAsset = new PoolCatalogAsset { Address = selectedToken },
        QuoteAsset = new PoolCatalogAsset { Address = RobinhoodDeploymentRegistry.Usdg },
        CreatedAtUnixMs = 32000
    };

    using var handler = new CatalogProtocolDiscoveryFixtureHandler(
        selectedToken,
        fixtures,
        chain: EvmChainDefinitions.RobinhoodMainnet,
        deployments: RobinhoodDeploymentRegistry.Catalog);
    using var httpClient = new HttpClient(handler);
    var configuration = CreateProviderConfiguration(
        OnChainProviderTypes.CustomRobinhood,
        "wss://robinhood.example",
        RobinhoodDeploymentRegistry.PublicRpcEndpoint);
    var result = await new EthereumPoolDiscoveryService(
            new EvmJsonRpcClient(httpClient, configuration, null),
            EvmChainDefinitions.RobinhoodMainnet,
            RobinhoodDeploymentRegistry.Catalog,
            httpClient)
        .DiscoverAsync(selectedToken, [v4Catalog]);

    AssertEqual(3, result.Pools.Length,
        "Robinhood Chain discovery did not retain Uniswap V2, V3, and V4.");
    Assert(result.Pools.All(pool =>
            pool.PoolKey.ChainId == EvmChainDefinitions.RobinhoodMainnetChainId),
        "Robinhood Chain discovery emitted a pool with the wrong chain identity.");
    AssertEqual(v2Pair, result.Pools.Single(pool =>
            pool.PoolKey.ProtocolId == OnChainProtocolIds.UniswapV2).PoolKey.PoolId,
        "Robinhood Chain V2 factory discovery returned the wrong pair.");
    AssertEqual(v3Pool, result.Pools.Single(pool =>
            pool.PoolKey.ProtocolId == OnChainProtocolIds.UniswapV3).PoolKey.PoolId,
        "Robinhood Chain V3 factory discovery returned the wrong pool.");
    AssertEqual(v4PoolId, result.Pools.Single(pool =>
            pool.PoolKey.ProtocolId == OnChainProtocolIds.UniswapV4).PoolKey.PoolId,
        "Robinhood Chain V4 discovery returned the wrong PoolId.");
    Assert(!handler.SawUnpinnedStateRead,
        "Robinhood Chain discovery performed a mutable-latest state read.");
    Assert(handler.SawExpectedBlockscoutApiRoot,
        "Robinhood Chain V4 discovery did not use the official Blockscout API root.");

    using var fallbackHandler = new CatalogProtocolDiscoveryFixtureHandler(
        selectedToken,
        fixtures,
        chain: EvmChainDefinitions.RobinhoodMainnet,
        deployments: RobinhoodDeploymentRegistry.Catalog,
        blockExplorerUnavailable: true);
    using var fallbackHttpClient = new HttpClient(fallbackHandler);
    var fallbackResult = await new EthereumPoolDiscoveryService(
            new EvmJsonRpcClient(fallbackHttpClient, configuration, null),
            EvmChainDefinitions.RobinhoodMainnet,
            RobinhoodDeploymentRegistry.Catalog,
            fallbackHttpClient)
        .DiscoverAsync(selectedToken, [v4Catalog]);
    AssertEqual(OnChainSupportStatus.Supported, fallbackResult.Pools.Single(pool =>
            pool.PoolKey.PoolId == v4PoolId).SupportStatus,
        "Robinhood Chain V4 discovery did not recover from an unavailable explorer.");
    Assert(fallbackHandler.SawRpcV4Lookup,
        "Robinhood Chain V4 discovery did not fall back to a bounded RPC lookup.");

    using var rangeLimitedHandler = new CatalogProtocolDiscoveryFixtureHandler(
        selectedToken,
        fixtures,
        chain: EvmChainDefinitions.RobinhoodMainnet,
        deployments: RobinhoodDeploymentRegistry.Catalog,
        blockExplorerUnavailable: true,
        maximumRpcLogBlockCount: 10);
    using var rangeLimitedHttpClient = new HttpClient(rangeLimitedHandler);
    var rangeLimitedResult = await new EthereumPoolDiscoveryService(
            new EvmJsonRpcClient(rangeLimitedHttpClient, configuration, null),
            EvmChainDefinitions.RobinhoodMainnet,
            RobinhoodDeploymentRegistry.Catalog,
            rangeLimitedHttpClient)
        .DiscoverAsync(selectedToken, [v4Catalog]);
    AssertEqual(OnChainSupportStatus.Supported, rangeLimitedResult.Pools.Single(pool =>
            pool.PoolKey.PoolId == v4PoolId).SupportStatus,
        "Robinhood Chain V4 discovery did not respect a provider's ten-block eth_getLogs limit.");
    Assert(rangeLimitedHandler.MaximumObservedRpcLogBlockCount <= 10,
        "Robinhood Chain V4 discovery requested more than ten blocks from eth_getLogs.");

    using var rateLimitedExplorerHandler = new CatalogProtocolDiscoveryFixtureHandler(
        selectedToken,
        fixtures,
        chain: EvmChainDefinitions.RobinhoodMainnet,
        deployments: RobinhoodDeploymentRegistry.Catalog,
        blockExplorerRateLimitedOnce: true);
    using var rateLimitedExplorerHttpClient = new HttpClient(rateLimitedExplorerHandler);
    var rateLimitedExplorerResult = await new EthereumPoolDiscoveryService(
            new EvmJsonRpcClient(rateLimitedExplorerHttpClient, configuration, null),
            EvmChainDefinitions.RobinhoodMainnet,
            RobinhoodDeploymentRegistry.Catalog,
            rateLimitedExplorerHttpClient)
        .DiscoverAsync(selectedToken, [v4Catalog]);
    AssertEqual(OnChainSupportStatus.Supported, rateLimitedExplorerResult.Pools.Single(pool =>
            pool.PoolKey.PoolId == v4PoolId).SupportStatus,
        "Robinhood Chain V4 discovery did not recover from a transient Blockscout rate limit.");
    Assert(!rateLimitedExplorerHandler.SawRpcV4Lookup,
        "Robinhood Chain V4 discovery used the slow RPC timestamp scan after a transient explorer rate limit.");

    using var logUnavailableHandler = new CatalogProtocolDiscoveryFixtureHandler(
        selectedToken,
        fixtures,
        chain: EvmChainDefinitions.RobinhoodMainnet,
        deployments: RobinhoodDeploymentRegistry.Catalog,
        blockExplorerLogsUnavailable: true);
    using var logUnavailableHttpClient = new HttpClient(logUnavailableHandler);
    var logUnavailableResult = await new EthereumPoolDiscoveryService(
            new EvmJsonRpcClient(logUnavailableHttpClient, configuration, null),
            EvmChainDefinitions.RobinhoodMainnet,
            RobinhoodDeploymentRegistry.Catalog,
            logUnavailableHttpClient)
        .DiscoverCatalogPoolsAsync(selectedToken, [v4Catalog], CancellationToken.None);
    AssertEqual(OnChainSupportStatus.Supported, logUnavailableResult.Pools.Single(pool =>
            pool.PoolKey.PoolId == v4PoolId).SupportStatus,
        "Robinhood Chain V4 discovery did not reuse the explorer block after its log endpoint failed.");
    AssertEqual(3, logUnavailableHandler.RpcBlockLookupCount,
        "Robinhood Chain V4 discovery repeated the slow RPC timestamp search despite an explorer block hint.");

    var creationTransaction = await new EthereumV4PoolLookupClient(
            rateLimitedExplorerHttpClient,
            EvmChainDefinitions.RobinhoodMainnet.BlockscoutApiRoot,
            RobinhoodDeploymentRegistry.UniswapV4PoolManager,
            EvmChainDefinitions.RobinhoodMainnet.DisplayName,
            null)
        .FindContractCreationTransactionAsync(
            selectedToken,
            CancellationToken.None);
    AssertEqual(v4PoolId, creationTransaction,
        "The Blockscout contract lookup returned the wrong creation transaction.");
    var indexedLog = await new EthereumV4PoolLookupClient(
            rateLimitedExplorerHttpClient,
            EvmChainDefinitions.RobinhoodMainnet.BlockscoutApiRoot,
            RobinhoodDeploymentRegistry.UniswapV4PoolManager,
            EvmChainDefinitions.RobinhoodMainnet.DisplayName,
            null)
        .FindAddressLogAsync(
            RobinhoodDeploymentRegistry.PonsV2Factory,
            v4PoolId,
            [EvmWebSocketStreamSource.UniswapV4InitializeTopic, v4PoolId],
            CancellationToken.None);
    Assert(indexedLog != null
           && indexedLog.TransactionHash == v4PoolId
           && indexedLog.BlockNumber == 16
           && indexedLog.BlockHash == CatalogProtocolDiscoveryFixtureHandler.BlockHash,
        "The Blockscout indexed-log lookup lost transaction or canonical-block identity.");

    var filters = EvmWebSocketStreamSource.CreateRpcLogFilters(
        result.Pools.Select(pool => new OnChainWatchedPoolSelection
        {
            Descriptor = pool,
            SelectedMint = selectedToken
        }).ToArray(),
        1,
        2);
    var filterJson = JsonSerializer.Serialize(filters);
    AssertEqual(2, filters.Length,
        "Robinhood Chain stream filters did not separate address pools from V4 PoolIds.");
    Assert(filterJson.Contains(EvmWebSocketStreamSource.UniswapV2SwapTopic, StringComparison.Ordinal)
           && filterJson.Contains(EvmWebSocketStreamSource.UniswapV3SwapTopic, StringComparison.Ordinal)
           && filterJson.Contains(EvmWebSocketStreamSource.UniswapV4SwapTopic, StringComparison.Ordinal)
           && filterJson.Contains(v4PoolId, StringComparison.Ordinal),
        "Robinhood Chain stream filters lost a canonical Uniswap event family.");

    var client = new OnChainEngineClient(enginePath);
    try
    {
        await client.ReplaceWatchedPoolsAsync(result.Pools.Select(pool =>
            new OnChainWatchedPoolSelection
            {
                Descriptor = pool,
                SelectedMint = selectedToken
            }).ToArray());
        AssertEqual(OnChainEngineState.Ready, client.State,
            "The shared engine rejected validated Robinhood Chain Uniswap deployments.");
    }
    finally
    {
        await client.StopAsync();
    }
}

static async Task VerifyBasePancakeDiscoveryAndEngineAsync(string enginePath)
{
    const string selectedToken = "0x1111111111111111111111111111111111111111";
    const string v2Pair = "0x2222222222222222222222222222222222222222";
    const string v3Pool = "0x3333333333333333333333333333333333333333";
    const string clPoolId =
        "0x4444444444444444444444444444444444444444444444444444444444444444";
    const string binPoolId =
        "0x5555555555555555555555555555555555555555555555555555555555555555";
    var fixtures = new[]
    {
        new EvmCatalogPoolFixture(
            v2Pair,
            BaseDeploymentRegistry.WrappedEther,
            OnChainProtocolIds.PancakeV2,
            BaseDeploymentRegistry.PancakeV2Factory),
        new EvmCatalogPoolFixture(
            v3Pool,
            BaseDeploymentRegistry.Usdc,
            OnChainProtocolIds.PancakeV3,
            BaseDeploymentRegistry.PancakeV3Factory,
            FeeTier: 2500,
            TickSpacing: 50),
        new EvmCatalogPoolFixture(
            clPoolId,
            BaseDeploymentRegistry.Usdc,
            OnChainProtocolIds.PancakeInfinityCl,
            BaseDeploymentRegistry.PancakeInfinityClPoolManager,
            FeeTier: 1,
            TickSpacing: 1),
        new EvmCatalogPoolFixture(
            binPoolId,
            BaseDeploymentRegistry.Usdc,
            OnChainProtocolIds.PancakeInfinityBin,
            BaseDeploymentRegistry.PancakeInfinityBinPoolManager,
            FeeTier: 4,
            TickSpacing: 1)
    };
    var catalogPools = fixtures
        .Where(static fixture => fixture.ProtocolId is OnChainProtocolIds.PancakeInfinityCl
            or OnChainProtocolIds.PancakeInfinityBin)
        .Select(fixture => new PoolCatalogEntry
        {
            SourceId = "pancake-explorer",
            ChainId = "base",
            ProtocolId = fixture.ProtocolId,
            PoolAddress = fixture.PoolAddress,
            Labels = [fixture.ProtocolId],
            BaseAsset = new PoolCatalogAsset
            {
                Address = selectedToken,
                Symbol = "TOKEN"
            },
            QuoteAsset = new PoolCatalogAsset
            {
                Address = fixture.QuoteAddress,
                Symbol = "USDC"
            },
            LiquidityUsd = 1000
        })
        .ToArray();

    using var handler = new CatalogProtocolDiscoveryFixtureHandler(
        selectedToken,
        fixtures,
        chain: EvmChainDefinitions.BaseMainnet,
        deployments: BaseDeploymentRegistry.Catalog);
    using var httpClient = new HttpClient(handler);
    var configuration = CreateProviderConfiguration(
        OnChainProviderTypes.BasePublic,
        "wss://mainnet.base.org",
        "https://mainnet.base.org");
    var result = await new EthereumPoolDiscoveryService(
            new EvmJsonRpcClient(httpClient, configuration, null),
            EvmChainDefinitions.BaseMainnet,
            BaseDeploymentRegistry.Catalog,
            httpClient)
        .DiscoverAsync(selectedToken, catalogPools);

    AssertEqual(4, result.Pools.Length,
        "Base discovery did not retain PancakeSwap V2, V3, Infinity CL, and Infinity bin.");
    AssertEqual(v2Pair, result.Pools.Single(pool =>
            pool.PoolKey.ProtocolId == OnChainProtocolIds.PancakeV2).PoolKey.PoolId,
        "PancakeSwap V2 factory discovery returned the wrong Base pair.");
    var v3 = result.Pools.Single(pool => pool.PoolKey.ProtocolId == OnChainProtocolIds.PancakeV3);
    AssertEqual(v3Pool, v3.PoolKey.PoolId,
        "PancakeSwap V3 factory discovery returned the wrong Base pool.");
    AssertEqual(2500U, v3.FeeTier,
        "Base PancakeSwap V3 discovery did not use the protocol-specific 0.25% fee tier.");
    AssertEqual(clPoolId, result.Pools.Single(pool =>
            pool.PoolKey.ProtocolId == OnChainProtocolIds.PancakeInfinityCl).PoolKey.PoolId,
        "PancakeSwap Infinity CL discovery returned the wrong Base PoolId.");
    AssertEqual(binPoolId, result.Pools.Single(pool =>
            pool.PoolKey.ProtocolId == OnChainProtocolIds.PancakeInfinityBin).PoolKey.PoolId,
        "PancakeSwap Infinity bin discovery returned the wrong Base PoolId.");
    Assert(!handler.SawUnpinnedStateRead,
        "Base PancakeSwap discovery performed a mutable-latest state read.");
    Assert(result.Pools.All(pool => pool.SupportStatus == OnChainSupportStatus.Supported),
        "Base PancakeSwap discovery returned an unsupported fixture.");

    var filters = EvmWebSocketStreamSource.CreateRpcLogFilters(
        result.Pools.Select(pool => new OnChainWatchedPoolSelection
        {
            Descriptor = pool,
            SelectedMint = selectedToken
        }).ToArray(),
        1,
        2);
    var filterJson = JsonSerializer.Serialize(filters);
    AssertEqual(3, filters.Length,
        "Base PancakeSwap filters did not split address pools from both singleton managers.");
    Assert(filterJson.Contains(EvmWebSocketStreamSource.UniswapV2SwapTopic, StringComparison.Ordinal)
           && filterJson.Contains(EvmWebSocketStreamSource.PancakeV3SwapTopic, StringComparison.Ordinal)
           && filterJson.Contains(EvmWebSocketStreamSource.PancakeInfinityClSwapTopic, StringComparison.Ordinal)
           && filterJson.Contains(EvmWebSocketStreamSource.PancakeInfinityBinSwapTopic, StringComparison.Ordinal)
           && filterJson.Contains(clPoolId, StringComparison.Ordinal)
           && filterJson.Contains(binPoolId, StringComparison.Ordinal),
        "Base stream filters lost a PancakeSwap event family or indexed PoolId.");

    var catalogPayload = System.Text.Encoding.UTF8.GetBytes($$"""
        {
          "rows": [
            {
              "chainId": 8453,
              "protocol": "infinityCl",
              "id": "{{clPoolId}}",
              "token0": { "id": "{{selectedToken}}", "name": "Token", "symbol": "TOKEN" },
              "token1": { "id": "{{BaseDeploymentRegistry.Usdc}}", "name": "USD Coin", "symbol": "USDC" },
              "tvlUSD": "1000.5",
              "volumeUSD24h": "42"
            },
            {
              "chainId": 56,
              "protocol": "infinityCl",
              "id": "{{binPoolId}}",
              "token0": { "id": "{{selectedToken}}" },
              "token1": { "id": "{{BaseDeploymentRegistry.Usdc}}" }
            }
          ]
        }
        """);
    var parsedCatalog = PancakeInfinityPoolCatalogClient.Parse(
        catalogPayload,
        "infinityCl",
        selectedToken,
        BaseDeploymentRegistry.Catalog);
    AssertEqual(1, parsedCatalog.Length,
        "PancakeSwap Explorer parsing did not constrain Base chain, protocol, and token.");
    AssertEqual("base", parsedCatalog[0].ChainId,
        "PancakeSwap Explorer parsing returned the wrong Base catalog identity.");
    AssertEqual(1, BaseDeploymentRegistry.GetCatalogPoolFamilies(parsedCatalog[0]).Length,
        "Parsed Base Infinity entries did not route to an allowlisted manager.");

    using (var pancakeHandler = new PancakeCatalogFixtureHandler(
               selectedToken,
               BaseDeploymentRegistry.Catalog))
    using (var pancakeHttpClient = new HttpClient(pancakeHandler)
           {
               BaseAddress = new Uri("https://explorer.pancakeswap.com/")
           })
    {
        var pancakeClient = new PancakeInfinityPoolCatalogClient(pancakeHttpClient);
        var pagedCatalog = await pancakeClient.SearchAsync(
            BaseDeploymentRegistry.Catalog,
            selectedToken);
        AssertEqual(2, pagedCatalog.Pools.Length,
            "Bounded PancakeSwap Explorer pagination did not find both Base Infinity families.");
        Assert(pancakeHandler.SawExpectedChainQuery,
            "PancakeSwap Explorer search did not request the Base catalog.");
    }

    var client = new OnChainEngineClient(enginePath);
    try
    {
        await client.ReplaceWatchedPoolsAsync(result.Pools.Select(pool =>
            new OnChainWatchedPoolSelection
            {
                Descriptor = pool,
                SelectedMint = selectedToken
            }).ToArray());
        AssertEqual(OnChainEngineState.Ready, client.State,
            "The shared engine rejected validated Base PancakeSwap deployments.");
    }
    finally
    {
        await client.StopAsync();
    }
}

static async Task VerifyBnbPancakeDiscoveryAndEngineAsync(string enginePath)
{
    const string selectedToken = "0x1111111111111111111111111111111111111111";
    const string v2Pair = "0x2222222222222222222222222222222222222222";
    const string v3Pool = "0x3333333333333333333333333333333333333333";
    const string clPoolId =
        "0x4444444444444444444444444444444444444444444444444444444444444444";
    const string binPoolId =
        "0x5555555555555555555555555555555555555555555555555555555555555555";
    const string uniswapV2Pair = "0x6666666666666666666666666666666666666666";
    const string uniswapV3Pool = "0x7777777777777777777777777777777777777777";
    const string uniswapV4PoolId =
        "0x8888888888888888888888888888888888888888888888888888888888888888";
    var fixtures = new[]
    {
        new EvmCatalogPoolFixture(
            v2Pair,
            BnbDeploymentRegistry.WrappedBnb,
            OnChainProtocolIds.PancakeV2,
            BnbDeploymentRegistry.PancakeV2Factory),
        new EvmCatalogPoolFixture(
            v3Pool,
            BnbDeploymentRegistry.WrappedBnb,
            OnChainProtocolIds.PancakeV3,
            BnbDeploymentRegistry.PancakeV3Factory,
            FeeTier: 500,
            TickSpacing: 10),
        new EvmCatalogPoolFixture(
            clPoolId,
            BnbDeploymentRegistry.Usdt,
            OnChainProtocolIds.PancakeInfinityCl,
            BnbDeploymentRegistry.PancakeInfinityClPoolManager,
            FeeTier: 1,
            TickSpacing: 1),
        new EvmCatalogPoolFixture(
            binPoolId,
            BnbDeploymentRegistry.Usdt,
            OnChainProtocolIds.PancakeInfinityBin,
            BnbDeploymentRegistry.PancakeInfinityBinPoolManager,
            FeeTier: 4,
            TickSpacing: 1),
        new EvmCatalogPoolFixture(
            uniswapV2Pair,
            BnbDeploymentRegistry.WrappedBnb,
            OnChainProtocolIds.UniswapV2,
            BnbDeploymentRegistry.UniswapV2Factory),
        new EvmCatalogPoolFixture(
            uniswapV3Pool,
            BnbDeploymentRegistry.WrappedBnb,
            OnChainProtocolIds.UniswapV3,
            BnbDeploymentRegistry.UniswapV3Factory,
            FeeTier: 3000,
            TickSpacing: 60),
        new EvmCatalogPoolFixture(
            uniswapV4PoolId,
            BnbDeploymentRegistry.Usdt,
            OnChainProtocolIds.UniswapV4,
            BnbDeploymentRegistry.UniswapV4PoolManager,
            FeeTier: 500,
            TickSpacing: 10,
            CreatedAtUnixMs: 1000)
    };
    var catalogPools = fixtures
        .Where(static fixture => fixture.ProtocolId is OnChainProtocolIds.PancakeInfinityCl
            or OnChainProtocolIds.PancakeInfinityBin
            or OnChainProtocolIds.UniswapV4)
        .Select(fixture => new PoolCatalogEntry
        {
            SourceId = "pancake-explorer",
            ChainId = "bsc",
            ProtocolId = fixture.ProtocolId == OnChainProtocolIds.UniswapV4
                ? "uniswap"
                : fixture.ProtocolId,
            PoolAddress = fixture.PoolAddress,
            Labels = fixture.ProtocolId == OnChainProtocolIds.UniswapV4
                ? ["v4"]
                : [fixture.ProtocolId],
            BaseAsset = new PoolCatalogAsset
            {
                Address = selectedToken,
                Symbol = "TOKEN"
            },
            QuoteAsset = new PoolCatalogAsset
            {
                Address = fixture.QuoteAddress,
                Symbol = "USDT"
            },
            LiquidityUsd = 1000,
            CreatedAtUnixMs = fixture.CreatedAtUnixMs > 0 ? fixture.CreatedAtUnixMs : null
        })
        .ToArray();

    using var handler = new CatalogProtocolDiscoveryFixtureHandler(
        selectedToken,
        fixtures,
        chain: EvmChainDefinitions.BnbMainnet,
        deployments: BnbDeploymentRegistry.Catalog);
    using var httpClient = new HttpClient(handler);
    var configuration = CreateProviderConfiguration(
        OnChainProviderTypes.PublicNodeBnb,
        "wss://bsc-rpc.publicnode.com",
        "https://bsc-rpc.publicnode.com");
    var result = await new EthereumPoolDiscoveryService(
            new EvmJsonRpcClient(httpClient, configuration, null),
            EvmChainDefinitions.BnbMainnet,
            BnbDeploymentRegistry.Catalog,
            httpClient)
        .DiscoverAsync(selectedToken, catalogPools);

    AssertEqual(7, result.Pools.Length,
        "BNB discovery did not retain PancakeSwap V2, V3, Infinity CL/bin and Uniswap V2-V4.");
    Assert(result.Pools.All(pool => pool.PoolKey.ChainId == EvmChainDefinitions.BnbMainnetChainId),
        "BNB discovery emitted a pool with the wrong chain identity.");
    AssertEqual(v2Pair, result.Pools.Single(pool =>
            pool.PoolKey.ProtocolId == OnChainProtocolIds.PancakeV2).PoolKey.PoolId,
        "PancakeSwap V2 factory discovery returned the wrong pair.");
    AssertEqual(v3Pool, result.Pools.Single(pool =>
            pool.PoolKey.ProtocolId == OnChainProtocolIds.PancakeV3).PoolKey.PoolId,
        "PancakeSwap V3 factory discovery returned the wrong pool.");
    var cl = result.Pools.Single(pool =>
        pool.PoolKey.ProtocolId == OnChainProtocolIds.PancakeInfinityCl);
    AssertEqual(1, cl.TickSpacing,
        "PancakeSwap Infinity CL pool-key tick spacing was not preserved.");
    var bin = result.Pools.Single(pool =>
        pool.PoolKey.ProtocolId == OnChainProtocolIds.PancakeInfinityBin);
    AssertEqual<ushort?>(1, bin.BinStep,
        "PancakeSwap Infinity bin pool-key step was not preserved.");
    AssertEqual(uniswapV2Pair, result.Pools.Single(pool =>
            pool.PoolKey.ProtocolId == OnChainProtocolIds.UniswapV2).PoolKey.PoolId,
        "Uniswap V2 factory discovery returned the wrong BNB pair.");
    AssertEqual(uniswapV3Pool, result.Pools.Single(pool =>
            pool.PoolKey.ProtocolId == OnChainProtocolIds.UniswapV3).PoolKey.PoolId,
        "Uniswap V3 factory discovery returned the wrong BNB pool.");
    AssertEqual(uniswapV4PoolId, result.Pools.Single(pool =>
            pool.PoolKey.ProtocolId == OnChainProtocolIds.UniswapV4).PoolKey.PoolId,
        "Uniswap V4 catalog discovery returned the wrong BNB PoolId.");
    Assert(!handler.SawUnpinnedStateRead,
        "BNB PancakeSwap discovery performed a mutable-latest state read.");
    Assert(result.Pools.All(pool => pool.SupportStatus == OnChainSupportStatus.Supported),
        "BNB discovery returned an unsupported fixture: " + string.Join(" | ", result.Pools
            .Where(pool => pool.SupportStatus != OnChainSupportStatus.Supported)
            .Select(pool => $"{pool.PoolKey.ProtocolId}: {pool.SupportReason}")));

    var filters = EvmWebSocketStreamSource.CreateRpcLogFilters(
        result.Pools.Select(pool => new OnChainWatchedPoolSelection
        {
            Descriptor = pool,
            SelectedMint = selectedToken
        }).ToArray(),
        1,
        2);
    var filterJson = JsonSerializer.Serialize(filters);
    AssertEqual(4, filters.Length,
        "BNB stream filters did not split address pools from all three singleton managers.");
    Assert(filterJson.Contains(EvmWebSocketStreamSource.UniswapV2SwapTopic, StringComparison.Ordinal)
           && filterJson.Contains(EvmWebSocketStreamSource.UniswapV3SwapTopic, StringComparison.Ordinal)
           && filterJson.Contains(EvmWebSocketStreamSource.UniswapV4SwapTopic, StringComparison.Ordinal)
           && filterJson.Contains(EvmWebSocketStreamSource.PancakeV3SwapTopic, StringComparison.Ordinal)
           && filterJson.Contains(EvmWebSocketStreamSource.PancakeInfinityClSwapTopic, StringComparison.Ordinal)
           && filterJson.Contains(EvmWebSocketStreamSource.PancakeInfinityBinSwapTopic, StringComparison.Ordinal)
           && filterJson.Contains(clPoolId, StringComparison.Ordinal)
           && filterJson.Contains(binPoolId, StringComparison.Ordinal)
           && filterJson.Contains(uniswapV4PoolId, StringComparison.Ordinal),
        "BNB stream filters lost a PancakeSwap or Uniswap event family or indexed PoolId.");

    var catalogPayload = System.Text.Encoding.UTF8.GetBytes($$"""
        {
          "rows": [
            {
              "chainId": 56,
              "protocol": "infinityCl",
              "id": "{{clPoolId}}",
              "token0": { "id": "{{selectedToken}}", "name": "Token", "symbol": "TOKEN" },
              "token1": { "id": "{{BnbDeploymentRegistry.Usdt}}", "name": "Tether USD", "symbol": "USDT" },
              "tvlUSD": "1000.5",
              "volumeUSD24h": "42"
            },
            {
              "chainId": 1,
              "protocol": "infinityCl",
              "id": "{{binPoolId}}",
              "token0": { "id": "{{selectedToken}}" },
              "token1": { "id": "{{BnbDeploymentRegistry.Usdt}}" }
            }
          ]
        }
        """);
    var parsedCatalog = PancakeInfinityPoolCatalogClient.Parse(
        catalogPayload,
        "infinityCl",
        selectedToken);
    AssertEqual(1, parsedCatalog.Length,
        "PancakeSwap Explorer parsing did not constrain chain, protocol, and selected token.");
    AssertEqual(OnChainProtocolIds.PancakeInfinityCl, parsedCatalog[0].ProtocolId,
        "PancakeSwap Explorer parsing returned the wrong internal protocol identity.");
    AssertEqual(1000.5, parsedCatalog[0].LiquidityUsd,
        "PancakeSwap Explorer parsing lost invariant-culture TVL metadata.");
    AssertEqual(1, BnbDeploymentRegistry.GetCatalogPoolFamilies(parsedCatalog[0]).Length,
        "Parsed PancakeSwap Infinity entries did not route to an allowlisted manager.");

    using (var pancakeHandler = new PancakeCatalogFixtureHandler(selectedToken))
    using (var pancakeHttpClient = new HttpClient(pancakeHandler)
           {
               BaseAddress = new Uri("https://explorer.pancakeswap.com/")
           })
    {
        var pancakeClient = new PancakeInfinityPoolCatalogClient(pancakeHttpClient);
        var concurrentCatalogs = await Task.WhenAll(
            pancakeClient.SearchAsync(selectedToken),
            pancakeClient.SearchAsync(selectedToken));
        var pagedCatalog = concurrentCatalogs[0];
        AssertEqual(2, pagedCatalog.Pools.Length,
            "Bounded PancakeSwap Explorer pagination did not find both Infinity families.");
        AssertEqual(4, pancakeHandler.RequestCount,
            "Concurrent PancakeSwap Explorer searches were not coalesced or bounded to matching pages.");
        _ = await pancakeClient.SearchAsync(selectedToken);
        AssertEqual(4, pancakeHandler.RequestCount,
            "PancakeSwap Explorer search did not reuse its bounded cache.");
    }

    var client = new OnChainEngineClient(enginePath);
    try
    {
        await client.ReplaceWatchedPoolsAsync(result.Pools.Select(pool =>
            new OnChainWatchedPoolSelection
            {
                Descriptor = pool,
                SelectedMint = selectedToken
            }).ToArray());
        AssertEqual(OnChainEngineState.Ready, client.State,
            "The shared engine rejected a validated BNB PancakeSwap deployment.");
    }
    finally
    {
        await client.StopAsync();
    }
}

static async Task VerifyBaseAerodromeDiscoveryAndEngineAsync(string enginePath)
{
    var fixtures = new[]
    {
        new EvmCatalogPoolFixture(
            "0xcdac0d6c6c59727a65f871236188350531885c43",
            BaseDeploymentRegistry.Usdc,
            OnChainProtocolIds.AerodromeClassic,
            BaseDeploymentRegistry.AerodromeClassicFactory,
            Stable: false),
        new EvmCatalogPoolFixture(
            "0x3548029694fbb241d45fb24ba0cd9c9d4e745f16",
            BaseDeploymentRegistry.Usdc,
            OnChainProtocolIds.AerodromeClassic,
            BaseDeploymentRegistry.AerodromeClassicFactory,
            Stable: true),
        new EvmCatalogPoolFixture(
            "0xb2cc224c1c9fee385f8ad6a55b4d94e92359dc59",
            BaseDeploymentRegistry.Usdc,
            OnChainProtocolIds.AerodromeSlipstream,
            BaseDeploymentRegistry.AerodromeSlipstreamInitialFactory,
            TickSpacing: 100),
        new EvmCatalogPoolFixture(
            "0xc758d81b9b81a6fcdad075bd471874a2c46b54e0",
            BaseDeploymentRegistry.Usdc,
            OnChainProtocolIds.AerodromeSlipstream,
            BaseDeploymentRegistry.AerodromeSlipstreamGaugeCapsFactory,
            TickSpacing: 50),
        new EvmCatalogPoolFixture(
            "0x3fe04a59ebd38cf06080a6f60a98d124eb59392a",
            BaseDeploymentRegistry.Usdc,
            OnChainProtocolIds.AerodromeSlipstream,
            BaseDeploymentRegistry.AerodromeSlipstreamGaugesV3Factory,
            TickSpacing: 50)
    };
    using var handler = new CatalogProtocolDiscoveryFixtureHandler(
        BaseDeploymentRegistry.WrappedEther,
        fixtures,
        chain: EvmChainDefinitions.BaseMainnet,
        deployments: BaseDeploymentRegistry.Catalog);
    using var httpClient = new HttpClient(handler);
    var configuration = CreateProviderConfiguration(
        OnChainProviderTypes.BasePublic,
        "wss://mainnet.base.org",
        "https://mainnet.base.org");
    var result = await new EthereumPoolDiscoveryService(
            new EvmJsonRpcClient(httpClient, configuration, null),
            EvmChainDefinitions.BaseMainnet,
            BaseDeploymentRegistry.Catalog,
            httpClient)
        .DiscoverAerodromeAsync(BaseDeploymentRegistry.WrappedEther);

    AssertEqual(5, result.Pools.Length,
        "Base Aerodrome discovery did not return both classic curves and all Slipstream generations.");
    var classic = result.Pools
        .Where(pool => pool.PoolKey.ProtocolId == OnChainProtocolIds.AerodromeClassic)
        .ToArray();
    AssertEqual(2, classic.Length, "Aerodrome classic discovery returned the wrong pool count.");
    Assert(classic.Select(pool => pool.PoolType).ToHashSet(StringComparer.Ordinal)
            .SetEquals(["volatile", "stable"]),
        "Aerodrome classic descriptors did not retain exact curve identity.");
    var slipstream = result.Pools
        .Where(pool => pool.PoolKey.ProtocolId == OnChainProtocolIds.AerodromeSlipstream)
        .ToArray();
    AssertEqual(3, slipstream.Length, "Slipstream discovery returned the wrong generation count.");
    Assert(slipstream.All(pool => pool.FeeTier == null && pool.TickSpacing > 0),
        "Slipstream dynamic fee leaked into descriptor identity or tick spacing was lost.");
    Assert(slipstream.Select(pool => pool.ProgramId).ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(BaseDeploymentRegistry.AerodromeSlipstreamFamilies.Select(family => family.FactoryAddress)),
        "Slipstream discovery did not retain every fixed factory generation.");
    Assert(!handler.SawUnpinnedStateRead,
        "Aerodrome discovery performed a mutable-latest state read.");
    var filters = EvmWebSocketStreamSource.CreateRpcLogFilters(
        result.Pools.Select(pool => new OnChainWatchedPoolSelection
        {
            Descriptor = pool,
            SelectedMint = BaseDeploymentRegistry.WrappedEther
        }).ToArray(),
        1,
        2);
    var filterJson = JsonSerializer.Serialize(filters);
    Assert(filters.Length == 1
           && filterJson.Contains(EvmWebSocketStreamSource.AerodromeClassicSwapTopic, StringComparison.Ordinal)
           && filterJson.Contains(EvmWebSocketStreamSource.AerodromeClassicSyncTopic, StringComparison.Ordinal)
           && filterJson.Contains(EvmWebSocketStreamSource.UniswapV3SwapTopic, StringComparison.Ordinal),
        "Aerodrome stream filters did not retain the classic and Slipstream event families.");

    var sixWordSlot0 = "0x" + string.Concat(new[]
    {
        (System.Numerics.BigInteger.One << 96).ToString("x64"),
        0.ToString("x64"),
        0.ToString("x64"),
        0.ToString("x64"),
        0.ToString("x64"),
        1.ToString("x64")
    });
    Assert(EthereumAbi.TryDecodeAerodromeSlipstreamSlot0(sixWordSlot0, out _, out _),
        "The exact six-word Slipstream slot0 shape was rejected.");
    Assert(!EthereumAbi.TryDecodeAerodromeSlipstreamSlot0(
            sixWordSlot0 + 0.ToString("x64"), out _, out _),
        "The Slipstream slot0 decoder accepted a seventh word.");

    var client = new OnChainEngineClient(enginePath);
    var prices = new ConcurrentQueue<OnChainPriceUpdate>();
    client.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
    try
    {
        await client.ReplaceWatchedPoolsAsync(result.Pools.Select(pool =>
            new OnChainWatchedPoolSelection
            {
                Descriptor = pool,
                SelectedMint = BaseDeploymentRegistry.WrappedEther
            }).ToArray());
        AssertEqual(OnChainEngineState.Ready, client.State,
            "The engine rejected a mixed five-pool Aerodrome watch set.");

        var stable = classic.Single(pool => pool.PoolType == "stable");
        await client.PublishEvmSnapshotAsync(new EvmSnapshotResponse
        {
            RequestId = "aerodrome-stable-snapshot",
            PoolKey = stable.PoolKey,
            BlockNumber = 20,
            BlockHash = Hash(20),
            Calls =
            [
                new EvmSnapshotCall
                {
                    Id = "getReserves",
                    Success = true,
                    ReturnData = AbiBigWords(
                        System.Numerics.BigInteger.Pow(10, 18),
                        System.Numerics.BigInteger.Pow(10, 6),
                        1)
                }
            ]
        });
        await WaitUntilAsync(() => prices.Count == 1, TimeSpan.FromSeconds(5),
            "Aerodrome stable snapshot price");
        AssertEqual("1000000000000000000", prices.Last().SpotPriceQuote?.Coefficient,
            "Aerodrome stable marginal spot price is wrong at peg.");

        await client.PublishEvmLogAsync(new EvmLogUpdate
        {
            ChainId = EvmChainDefinitions.BaseMainnetChainId,
            ConnectionEpoch = 10,
            Address = stable.PoolKey.PoolId,
            Topics =
            [
                EvmWebSocketStreamSource.AerodromeClassicSwapTopic,
                Hash(5000),
                Hash(5001)
            ],
            Data = AbiBigWords(
                System.Numerics.BigInteger.Pow(10, 18),
                0,
                0,
                2 * System.Numerics.BigInteger.Pow(10, 6)),
            BlockNumber = 21,
            BlockHash = Hash(21),
            TransactionHash = Hash(5002),
            TransactionIndex = 1,
            LogIndex = 2,
            ObservedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
        await WaitUntilAsync(() => prices.Count == 2, TimeSpan.FromSeconds(5),
            "Aerodrome classic swap price");
        AssertEqual("2000000000000000000", prices.Last().LastTradePriceQuote?.Coefficient,
            "Aerodrome classic executed price is wrong.");

        var initial = slipstream.Single(pool =>
            pool.ProgramId.Equals(
                BaseDeploymentRegistry.AerodromeSlipstreamInitialFactory,
                StringComparison.OrdinalIgnoreCase));
        var q96 = System.Numerics.BigInteger.One << 96;
        await client.PublishEvmSnapshotAsync(new EvmSnapshotResponse
        {
            RequestId = "slipstream-snapshot",
            PoolKey = initial.PoolKey,
            BlockNumber = 22,
            BlockHash = Hash(22),
            Calls =
            [
                new EvmSnapshotCall
                {
                    Id = "slot0",
                    Success = true,
                    ReturnData = AbiBigWords(q96, 0, 0, 0, 0, 1)
                },
                new EvmSnapshotCall
                {
                    Id = "liquidity",
                    Success = true,
                    ReturnData = AbiBigWords(100)
                }
            ]
        });
        await WaitUntilAsync(() => prices.Count == 3, TimeSpan.FromSeconds(5),
            "Slipstream snapshot price");
        AssertEqual("1000000000000000000000000000000", prices.Last().SpotPriceQuote?.Coefficient,
            "Slipstream six-word slot0 spot price is wrong.");

        await client.PublishEvmLogAsync(new EvmLogUpdate
        {
            ChainId = EvmChainDefinitions.BaseMainnetChainId,
            ConnectionEpoch = 10,
            Address = initial.PoolKey.PoolId,
            Topics = [EvmWebSocketStreamSource.UniswapV3SwapTopic, Hash(5010), Hash(5011)],
            Data = AbiBigWords(
                System.Numerics.BigInteger.Pow(10, 18),
                -2 * System.Numerics.BigInteger.Pow(10, 6),
                q96,
                100,
                0),
            BlockNumber = 23,
            BlockHash = Hash(23),
            TransactionHash = Hash(5012),
            TransactionIndex = 1,
            LogIndex = 2,
            ObservedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
        await WaitUntilAsync(() => prices.Count == 4, TimeSpan.FromSeconds(5),
            "Slipstream swap price");
        AssertEqual("2000000000000000000", prices.Last().LastTradePriceQuote?.Coefficient,
            "Slipstream executed price is wrong.");
    }
    finally
    {
        await client.StopAsync();
    }
}

static async Task VerifyBaseUniswapV4DiscoveryAsync()
{
    const string selectedToken = "0x1111111111111111111111111111111111111111";
    const string poolId = "0xeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    var fixture = new EvmCatalogPoolFixture(
        poolId,
        BaseDeploymentRegistry.NativeEther,
        OnChainProtocolIds.UniswapV4,
        BaseDeploymentRegistry.UniswapV4PoolManager,
        3000,
        60,
        32000);
    var catalog = new PoolCatalogEntry
    {
        SourceId = "dexscreener",
        ChainId = "base",
        ProtocolId = "uniswap",
        PoolAddress = poolId,
        Labels = ["v4"],
        BaseAsset = new PoolCatalogAsset { Address = selectedToken },
        QuoteAsset = new PoolCatalogAsset { Address = BaseDeploymentRegistry.NativeEther },
        CreatedAtUnixMs = fixture.CreatedAtUnixMs
    };
    using var handler = new CatalogProtocolDiscoveryFixtureHandler(
        selectedToken,
        [fixture],
        chain: EvmChainDefinitions.BaseMainnet,
        deployments: BaseDeploymentRegistry.Catalog);
    using var httpClient = new HttpClient(handler);
    var configuration = CreateProviderConfiguration(
        OnChainProviderTypes.BasePublic,
        "wss://mainnet.base.org",
        "https://mainnet.base.org");
    var result = await new EthereumPoolDiscoveryService(
            new EvmJsonRpcClient(httpClient, configuration, null),
            EvmChainDefinitions.BaseMainnet,
            BaseDeploymentRegistry.Catalog,
            httpClient)
        .DiscoverAsync(selectedToken, [catalog]);
    var descriptor = result.Pools.Single(pool =>
        pool.PoolKey.ProtocolId == OnChainProtocolIds.UniswapV4);
    AssertEqual(EvmChainDefinitions.BaseMainnetChainId, descriptor.PoolKey.ChainId,
        "Base V4 discovery emitted the wrong chain identity.");
    AssertEqual(BaseDeploymentRegistry.UniswapV4PoolManager, descriptor.ProgramId,
        "Base V4 discovery emitted the wrong PoolManager.");
    AssertEqual(OnChainSupportStatus.Supported, descriptor.SupportStatus,
        "A zero-hook Base V4 pool was not selectable.");
    AssertEqual(BaseDeploymentRegistry.NativeEther, descriptor.HookAddress,
        "The Base V4 zero-hook identity was not preserved.");
    Assert(handler.SawExpectedBlockscoutApiRoot,
        "Base V4 discovery did not use the documented Base Blockscout API root.");

    var hookedFixture = fixture with
    {
        HookAddress = "0x0000000000000000000000000000000000000001"
    };
    using var hookedHandler = new CatalogProtocolDiscoveryFixtureHandler(
        selectedToken,
        [hookedFixture],
        chain: EvmChainDefinitions.BaseMainnet,
        deployments: BaseDeploymentRegistry.Catalog);
    using var hookedHttpClient = new HttpClient(hookedHandler);
    var hookedResult = await new EthereumPoolDiscoveryService(
            new EvmJsonRpcClient(hookedHttpClient, configuration, null),
            EvmChainDefinitions.BaseMainnet,
            BaseDeploymentRegistry.Catalog,
            hookedHttpClient)
        .DiscoverAsync(selectedToken, [catalog]);
    var hookedDescriptor = hookedResult.Pools.Single(pool =>
        pool.PoolKey.ProtocolId == OnChainProtocolIds.UniswapV4);
    AssertEqual(OnChainSupportStatus.DiscoveredUnsupported, hookedDescriptor.SupportStatus,
        "A nonzero-hook Base V4 pool was not rejected.");
    Assert(hookedDescriptor.SupportReason?.Contains("zero-hook", StringComparison.Ordinal) == true,
        "A nonzero-hook Base V4 pool did not report the strict Base rule.");
}

static async Task VerifyCatalogProtocolDiscoveryAndEngineAsync(string enginePath)
{
    const string selectedToken = "0x1111111111111111111111111111111111111111";
    var fixtures = new[]
    {
        new EvmCatalogPoolFixture(
            "0x2222222222222222222222222222222222222222",
            "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            OnChainProtocolIds.UniswapV2,
            EthereumDeploymentRegistry.UniswapV2Factory),
        new EvmCatalogPoolFixture(
            "0x3333333333333333333333333333333333333333",
            "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            OnChainProtocolIds.UniswapV3,
            EthereumDeploymentRegistry.UniswapV3Factory,
            500,
            10),
        new EvmCatalogPoolFixture(
            "0x4444444444444444444444444444444444444444",
            "0xcccccccccccccccccccccccccccccccccccccccc",
            OnChainProtocolIds.UniswapV2,
            EthereumDeploymentRegistry.ShibaSwapV1Factory),
        new EvmCatalogPoolFixture(
            "0x5555555555555555555555555555555555555555",
            "0xdddddddddddddddddddddddddddddddddddddddd",
            OnChainProtocolIds.UniswapV3,
            EthereumDeploymentRegistry.ShibaSwapV2Factory,
            3000,
            60),
        new EvmCatalogPoolFixture(
            "0xeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            EthereumDeploymentRegistry.NativeEther,
            OnChainProtocolIds.UniswapV4,
            EthereumDeploymentRegistry.UniswapV4PoolManager,
            10000,
            200,
            32000)
    };
    using var handler = new CatalogProtocolDiscoveryFixtureHandler(
        selectedToken,
        fixtures,
        poolCodeDelay: TimeSpan.FromMilliseconds(30));
    using var httpClient = new HttpClient(handler);
    var configuration = CreateProviderConfiguration(
        OnChainProviderTypes.PublicNodeEthereum,
        "wss://ethereum-rpc.publicnode.com",
        "https://ethereum-rpc.publicnode.com");
    var discovery = new EthereumPoolDiscoveryService(
        new EvmJsonRpcClient(httpClient, configuration, null),
        httpClient);
    var catalog = new[]
    {
        CatalogPool("uniswap", "v2", fixtures[0]),
        CatalogPool("uniswap", "v3", fixtures[1]),
        CatalogPool("shibaswap", null, fixtures[2]),
        CatalogPool(EthereumDeploymentRegistry.ShibaSwapV2Factory, null, fixtures[3]),
        CatalogPool("uniswap", "v4", fixtures[4])
    };

    var result = await discovery.DiscoverCatalogPoolsSharedAsync(
        selectedToken,
        catalog,
        CancellationToken.None);
    AssertEqual(1, handler.GetRpcMethodCount("eth_chainId"),
        "Catalog validation repeated the chain preflight for individual pools.");
    AssertEqual(2, handler.RpcBlockLookupCount,
        "Catalog validation repeated the latest-block preflight for individual pools.");
    Assert(handler.MaximumConcurrentPoolCodeRequests is >= 2 and <= 4,
        "Catalog validation did not overlap pool checks within the four-request bound.");
    AssertEqual(5, result.Pools.Length,
        "Catalog validation did not return every supported Uniswap deployment.");
    foreach (var fixture in fixtures)
    {
        var descriptor = result.Pools.Single(pool =>
            pool.PoolKey.PoolId.Equals(fixture.PoolAddress, StringComparison.OrdinalIgnoreCase));
        AssertEqual(fixture.ProtocolId, descriptor.PoolKey.ProtocolId,
            "Catalog validation selected the wrong pricing family.");
        AssertEqual(fixture.FactoryAddress, descriptor.PoolKey.DeploymentKey.ContractAddress,
            "Catalog validation lost the exact factory identity.");
        AssertEqual(OnChainSupportStatus.Supported, descriptor.SupportStatus,
            "A validated catalog pool was not selectable.");
    }
    var v4Descriptor = result.Pools.Single(pool =>
        pool.PoolKey.ProtocolId == OnChainProtocolIds.UniswapV4);
    AssertEqual("selectedAsToken1", v4Descriptor.PairOrientation,
        "Native ETH and selected-token orientation was lost for V4.");
    AssertEqual(EthereumDeploymentRegistry.NativeEther, v4Descriptor.Asset0?.Address,
        "The V4 native currency was not preserved as currency0.");
    AssertEqual(10000U, v4Descriptor.FeeTier,
        "The V4 initialization fee was not preserved.");
    AssertEqual(200, v4Descriptor.TickSpacing,
        "The V4 initialization tick spacing was not preserved.");
    Assert(!EthereumAbi.UniswapV4HookReturnsSwapDelta(v4Descriptor.HookAddress!),
        "A no-hook V4 pool was incorrectly classified as delta-modifying.");
    Assert(EthereumAbi.UniswapV4HookReturnsSwapDelta(
            "0x000000000000000000000000000000000000000c"),
        "V4 swap-delta hook permissions were not detected.");
    Assert(!handler.SawUnpinnedStateRead,
        "Catalog protocol discovery performed a mutable-latest state read.");

    using var partialHandler = new CatalogProtocolDiscoveryFixtureHandler(
        selectedToken,
        fixtures,
        poolCodeDelay: TimeSpan.FromSeconds(5),
        undelayedPoolAddress: fixtures[0].PoolAddress);
    using var partialHttpClient = new HttpClient(partialHandler);
    var partialDiscovery = new EthereumPoolDiscoveryService(
        new EvmJsonRpcClient(partialHttpClient, configuration, null),
        partialHttpClient);
    using var partialTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
    var partialResult = await partialDiscovery.DiscoverCatalogPoolsSharedAsync(
        selectedToken,
        catalog,
        partialTimeout.Token);
    Assert(partialResult.Pools.Length is >= 1 and < 5,
        "The catalog deadline discarded completed pools or returned the entire delayed catalog.");
    Assert(partialResult.Truncated,
        "The incomplete catalog validation was not marked as truncated.");
    Assert(partialResult.Pools.Any(pool => pool.PoolKey.PoolId == fixtures[0].PoolAddress),
        "The catalog deadline did not preserve the completed highest-priority pool.");
    Assert(partialResult.Pools.All(pool => fixtures.Any(fixture => fixture.PoolAddress == pool.PoolKey.PoolId)),
        "The catalog deadline fabricated a pool absent from the catalog.");

    var filters = EvmWebSocketStreamSource.CreateRpcLogFilters(
        result.Pools.Select(pool => new OnChainWatchedPoolSelection
        {
            Descriptor = pool,
            SelectedMint = selectedToken
        }).ToArray(),
        1,
        2);
    using (var filterDocument = JsonDocument.Parse(JsonSerializer.Serialize(filters)))
    {
        var v4Filter = filterDocument.RootElement.EnumerateArray().Single(filter =>
            filter.GetProperty("address").ValueKind == JsonValueKind.String
            && filter.GetProperty("address").GetString()
            == EthereumDeploymentRegistry.UniswapV4PoolManager);
        AssertEqual(EvmWebSocketStreamSource.UniswapV4SwapTopic,
            v4Filter.GetProperty("topics")[0].GetString(),
            "The V4 stream filter used the wrong event topic.");
        AssertEqual(v4Descriptor.PoolKey.PoolId,
            v4Filter.GetProperty("topics")[1].GetString(),
            "The V4 stream filter did not constrain the indexed PoolId.");
    }

    var client = new OnChainEngineClient(enginePath);
    var prices = new ConcurrentQueue<OnChainPriceUpdate>();
    client.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
    try
    {
        await client.ReplaceWatchedPoolsAsync(result.Pools.Select(pool =>
            new OnChainWatchedPoolSelection
            {
                Descriptor = pool,
                SelectedMint = selectedToken
            }).ToArray());
        AssertEqual(OnChainEngineState.Ready, client.State,
            "The shared engine rejected a validated V4 deployment.");
        var q96 = System.Numerics.BigInteger.One << 96;
        await client.PublishEvmSnapshotAsync(new EvmSnapshotResponse
        {
            RequestId = "v4-snapshot-16",
            PoolKey = v4Descriptor.PoolKey,
            BlockNumber = 16,
            BlockHash = Hash(16),
            Calls =
            [
                new EvmSnapshotCall
                {
                    Id = "slot0",
                    Success = true,
                    ReturnData = AbiBigWords(q96, 0, 0, 10000)
                },
                new EvmSnapshotCall
                {
                    Id = "liquidity",
                    Success = true,
                    ReturnData = AbiBigWords(100)
                }
            ]
        });
        await WaitUntilAsync(() => prices.Count == 1, TimeSpan.FromSeconds(5),
            "Uniswap v4 StateView snapshot price");
        AssertDecimalValue(prices.Last().SpotPriceQuote, 1,
            "The V4 StateView spot price is wrong.");

        await client.PublishEvmLogAsync(new EvmLogUpdate
        {
            ChainId = EvmChainDefinitions.EthereumMainnet.ChainId,
            ConnectionEpoch = 9,
            Address = EthereumDeploymentRegistry.UniswapV4PoolManager,
            Topics =
            [
                EvmWebSocketStreamSource.UniswapV4SwapTopic,
                v4Descriptor.PoolKey.PoolId,
                Hash(7000)
            ],
            Data = AbiBigWords(
                -4_000_000_000_000_000_000,
                2_000_000_000_000_000_000,
                q96,
                100,
                0,
                10000),
            BlockNumber = 17,
            BlockHash = Hash(17),
            TransactionHash = Hash(7001),
            TransactionIndex = 1,
            LogIndex = 2,
            ObservedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
        await WaitUntilAsync(() => prices.Count == 2, TimeSpan.FromSeconds(5),
            "Uniswap v4 PoolManager swap price");
        AssertDecimalValue(prices.Last().LastTradePriceQuote, 2,
            "The V4 signed pool-delta execution price is wrong.");
        AssertDecimalValue(prices.Last().PriceNative, 2,
            "A native-ETH V4 quote was not exposed in ETH units.");
    }
    finally
    {
        await client.StopAsync();
    }

    static PoolCatalogEntry CatalogPool(
        string protocolId,
        string? label,
        EvmCatalogPoolFixture fixture)
    {
        return new PoolCatalogEntry
        {
            SourceId = "dexscreener",
            ChainId = "ethereum",
            ProtocolId = protocolId,
            PoolAddress = fixture.PoolAddress,
            Labels = label == null ? [] : [label],
            BaseAsset = new PoolCatalogAsset { Address = selectedToken },
            QuoteAsset = new PoolCatalogAsset { Address = fixture.QuoteAddress },
            CreatedAtUnixMs = fixture.CreatedAtUnixMs > 0 ? fixture.CreatedAtUnixMs : null
        };
    }
}

static async Task VerifyCatalogTimeoutIsolationAsync()
{
    const string selectedToken = "0x1111111111111111111111111111111111111111";
    var fixtures = new[]
    {
        new EvmCatalogPoolFixture(
            "0x2222222222222222222222222222222222222222",
            "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            OnChainProtocolIds.UniswapV2,
            EthereumDeploymentRegistry.UniswapV2Factory),
        new EvmCatalogPoolFixture(
            "0x3333333333333333333333333333333333333333",
            "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            OnChainProtocolIds.UniswapV2,
            EthereumDeploymentRegistry.UniswapV2Factory)
    };
    using var handler = new CatalogProtocolDiscoveryFixtureHandler(
        selectedToken,
        fixtures,
        fixtures[0].PoolAddress);
    using var httpClient = new HttpClient(handler);
    var configuration = CreateProviderConfiguration(
        OnChainProviderTypes.PublicNodeEthereum,
        "wss://ethereum-rpc.publicnode.com",
        "https://ethereum-rpc.publicnode.com");
    var catalog = fixtures.Select(fixture => new PoolCatalogEntry
    {
        SourceId = "dexscreener",
        ChainId = "ethereum",
        ProtocolId = "uniswap",
        PoolAddress = fixture.PoolAddress,
        Labels = ["v2"],
        BaseAsset = new PoolCatalogAsset { Address = selectedToken },
        QuoteAsset = new PoolCatalogAsset { Address = fixture.QuoteAddress }
    }).ToArray();

    var result = await new EthereumPoolDiscoveryService(
            new EvmJsonRpcClient(httpClient, configuration, null),
            httpClient)
        .DiscoverAsync(selectedToken, catalog);
    AssertEqual(2, result.Pools.Length,
        "One timed-out pool discarded other catalog validation results.");
    AssertEqual(
        OnChainSupportStatus.TemporarilyUnavailable,
        result.Pools.Single(pool => pool.PoolKey.PoolId == fixtures[0].PoolAddress).SupportStatus,
        "A timed-out pool was not marked temporarily unavailable.");
    AssertEqual(
        OnChainSupportStatus.Supported,
        result.Pools.Single(pool => pool.PoolKey.PoolId == fixtures[1].PoolAddress).SupportStatus,
        "A timeout in one pool prevented the next pool from being validated.");
}

static async Task VerifyEthereumReferencePricingAsync(string enginePath)
{
    const string selectedToken = "0x1111111111111111111111111111111111111111";
    const string selectedPool = "0x4444444444444444444444444444444444444444";
    var selected = new OnChainWatchedPoolSelection
    {
        SelectedMint = selectedToken,
        Descriptor = new OnChainPoolDescriptor
        {
            PoolKey = new OnChainPoolKey
            {
                DeploymentKey = EthereumDeploymentRegistry.UniswapV3Deployment(),
                PoolId = selectedPool
            },
            PoolType = "concentratedLiquidity",
            ProgramId = EthereumDeploymentRegistry.UniswapV3Factory,
            BaseMint = selectedToken,
            QuoteMint = EthereumDeploymentRegistry.WrappedEther,
            BaseDecimals = 18,
            QuoteDecimals = 18,
            PairOrientation = "selectedAsToken0",
            SupportStatus = OnChainSupportStatus.Supported,
            Asset0 = new OnChainAssetKey
            {
                ChainNamespace = ChainNamespaces.Eip155,
                ChainId = EvmChainDefinitions.EthereumMainnet.ChainId,
                Address = selectedToken
            },
            Asset1 = new OnChainAssetKey
            {
                ChainNamespace = ChainNamespaces.Eip155,
                ChainId = EvmChainDefinitions.EthereumMainnet.ChainId,
                Address = EthereumDeploymentRegistry.WrappedEther
            },
            FeeTier = 3000,
            TickSpacing = 60
        }
    };
    var reference = EthereumDeploymentRegistry.CreateEthUsdReferenceSelection();
    var prices = new ConcurrentQueue<OnChainPriceUpdate>();
    var client = new OnChainEngineClient(enginePath);
    client.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
    try
    {
        await client.ReplaceWatchedPoolsAsync([selected, reference]);
        await client.SetEvmProviderContextAsync("ethereum-profile", "ethereum-json-rpc:test");
        await client.PublishEvmHeadAsync(CreateEvmHead(20, Hash(20), Hash(19)));
        var q96 = System.Numerics.BigInteger.One << 96;
        var observedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await client.PublishEvmLogAsync(new EvmLogUpdate
        {
            ChainId = EvmChainDefinitions.EthereumMainnet.ChainId,
            ConnectionEpoch = 11,
            Address = selectedPool,
            Topics = [EvmWebSocketStreamSource.UniswapV3SwapTopic, Hash(4000), Hash(4001)],
            Data = AbiBigWords(
                2_000_000_000_000_000_000,
                -4_000_000_000_000_000_000,
                q96,
                100,
                0),
            BlockNumber = 20,
            BlockHash = Hash(20),
            TransactionHash = Hash(4002),
            TransactionIndex = 1,
            LogIndex = 1,
            ObservedAtUnixMs = observedAt
        });
        await WaitUntilAsync(
            () => prices.Any(update => update.PoolKey.PoolId == selectedPool),
            TimeSpan.FromSeconds(5),
            "ETH-quoted target price");
        var native = prices.Last(update => update.PoolKey.PoolId == selectedPool);
        AssertDecimalValue(native.PriceNative, 2,
            "An ETH-quoted Uniswap price was not exposed in native ETH units.");
        Assert(native.PriceUsd == null,
            "An ETH-quoted price was labeled USD before a reference price existed.");

        await client.PublishEvmLogAsync(new EvmLogUpdate
        {
            ChainId = EvmChainDefinitions.EthereumMainnet.ChainId,
            ConnectionEpoch = 11,
            Address = EthereumDeploymentRegistry.WrappedEtherUsdcReferencePool,
            Topics = [EvmWebSocketStreamSource.UniswapV3SwapTopic, Hash(4010), Hash(4011)],
            Data = AbiBigWords(
                2_000_000_000,
                -1_000_000_000_000_000_000,
                q96,
                100,
                0),
            BlockNumber = 20,
            BlockHash = Hash(20),
            TransactionHash = Hash(4012),
            TransactionIndex = 2,
            LogIndex = 2,
            ObservedAtUnixMs = observedAt
        });
        await WaitUntilAsync(
            () => prices.Any(update => update.PoolKey.PoolId == selectedPool
                                       && update.PriceUsd != null),
            TimeSpan.FromSeconds(5),
            "on-chain ETH/USD conversion");
        var onChainUsd = prices.Last(update => update.PoolKey.PoolId == selectedPool
                                                && update.PriceUsd != null);
        AssertDecimalValue(onChainUsd.PriceUsd, 4000,
            "The on-chain WETH/USDC reference conversion was wrong.");
        Assert(onChainUsd.ReferenceSourceId?.StartsWith(
                   "ethereum:uniswap-v3:WETH/USDC:500:",
                   StringComparison.Ordinal) == true,
            "The on-chain ETH/USD reference provenance was lost.");
        AssertEqual("ethereum-profile", onChainUsd.ProviderProfileId,
            "The Ethereum provider profile identity was lost.");
        AssertEqual("ethereum-json-rpc:test", onChainUsd.SourceId,
            "The Ethereum source identity was lost.");

        await client.ResetEvmForSnapshotAsync(EvmChainDefinitions.EthereumMainnet.ChainId);
        await client.PublishEvmReferencePriceAsync(
            EvmChainDefinitions.EthereumMainnet.ChainId,
            "ethUsd",
            new OnChainDecimalValue { Coefficient = "2100", Scale = 0 },
            "ccxt:binance:ETH/USDT",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await client.PublishEvmHeadAsync(CreateEvmHead(21, Hash(21), Hash(20)));
        await client.PublishEvmLogAsync(new EvmLogUpdate
        {
            ChainId = EvmChainDefinitions.EthereumMainnet.ChainId,
            ConnectionEpoch = 12,
            Address = selectedPool,
            Topics = [EvmWebSocketStreamSource.UniswapV3SwapTopic, Hash(4020), Hash(4021)],
            Data = AbiBigWords(
                2_000_000_000_000_000_000,
                -4_000_000_000_000_000_000,
                q96,
                100,
                0),
            BlockNumber = 21,
            BlockHash = Hash(21),
            TransactionHash = Hash(4022),
            TransactionIndex = 1,
            LogIndex = 1,
            ObservedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
        await WaitUntilAsync(
            () => prices.Any(update => update.PoolKey.PoolId == selectedPool
                                       && update.ReferenceSourceId == "ccxt:binance:ETH/USDT"),
            TimeSpan.FromSeconds(5),
            "ETH/USD fallback conversion");
        var fallbackUsd = prices.Last(update => update.PoolKey.PoolId == selectedPool
                                               && update.ReferenceSourceId == "ccxt:binance:ETH/USDT");
        AssertDecimalValue(fallbackUsd.PriceUsd, 4200,
            "The ETH/USDT fallback conversion was wrong.");
    }
    finally
    {
        await client.StopAsync();
    }
}

static async Task VerifyLiveUniswapV4DiscoveryAsync()
{
    const string shib = "0x95aD61b0a150d79219dCF64E1E6Cc01f0B64C4cE";
    using var catalogHttp = new HttpClient
    {
        BaseAddress = new Uri("https://api.dexscreener.com/"),
        Timeout = TimeSpan.FromSeconds(30)
    };
    using var rpcHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var catalog = await new DexScreenerPoolCatalogClient(catalogHttp)
        .SearchAsync("ethereum", shib);
    var configuration = CreateProviderConfiguration(
        OnChainProviderTypes.CustomEthereum,
        "wss://eth-mainnet.public.blastapi.io",
        "https://eth-mainnet.public.blastapi.io");
    var v4CatalogPools = catalog.Pools
        .Where(pool => pool.ProtocolId.Equals("uniswap", StringComparison.OrdinalIgnoreCase)
                       && pool.Labels.Contains("v4", StringComparer.OrdinalIgnoreCase))
        .ToArray();
    var result = await new EthereumPoolDiscoveryService(
            new EvmJsonRpcClient(rpcHttp, configuration, null),
            rpcHttp)
        .DiscoverAsync(shib, v4CatalogPools);
    var v4Pools = v4CatalogPools
        .Select(pool => result.Pools.FirstOrDefault(descriptor =>
                           descriptor.PoolKey.PoolId.Equals(
                               pool.PoolAddress,
                               StringComparison.OrdinalIgnoreCase))
                       ?? new OnChainPoolDescriptor
                       {
                           PoolKey = new OnChainPoolKey { PoolId = pool.PoolAddress },
                           SupportStatus = OnChainSupportStatus.DiscoveredUnsupported,
                           SupportReason = "No descriptor was returned."
                       })
        .ToArray();
    Console.WriteLine(
        $"LIVE EVM | descriptors={result.Pools.Length} | supported={result.Pools.Count(pool => pool.SupportStatus == OnChainSupportStatus.Supported)}");
    foreach (var pool in v4Pools)
    {
        Console.WriteLine(
            $"LIVE V4 | {pool.PoolKey.PoolId} | {pool.SupportStatus} | hook={pool.HookAddress ?? "unknown"} | {pool.SupportReason ?? "validated"}");
    }
    Assert(v4Pools.Length > 0, "The live SHIB catalog did not contain a V4 pool.");
    Assert(v4Pools.All(pool => pool.SupportReason != "No descriptor was returned."),
        "At least one live SHIB V4 candidate was not classified by discovery.");
    Assert(v4Pools.Any(pool => pool.SupportStatus == OnChainSupportStatus.Supported),
        "The live SHIB catalog did not contain a selectable Uniswap V4 pool.");
}

static string GetDedicatedAlchemyTestRoot()
{
    return Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TrenchHQTest", "DedicatedAlchemy");
}

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

static async Task<T> RetryLiveTransientResultAsync<T>(Func<Task<T>> operation, string operationName)
{
    for (var attempt = 0; ; attempt++)
    {
        try
        {
            return await operation();
        }
        catch (EvmJsonRpcException exception) when (
            (exception.Kind == EvmRpcFailureKind.RateLimited
             || exception.Kind == EvmRpcFailureKind.RpcError && exception.RpcCode == null)
            && attempt < 4)
        {
            Console.WriteLine(
                $"LIVE RETRY | {operationName} | transient provider failure | attempt={attempt + 1}");
            var delay = exception.Kind == EvmRpcFailureKind.RateLimited
                ? TimeSpan.FromSeconds(2)
                : TimeSpan.FromSeconds(1 << attempt);
            await Task.Delay(delay);
        }
    }
}

static async Task RetryLiveTransientAsync(Func<Task> operation, string operationName)
{
    for (var attempt = 0; ; attempt++)
    {
        try
        {
            await operation();
            return;
        }
        catch (EvmJsonRpcException exception) when (
            (exception.Kind == EvmRpcFailureKind.RateLimited
             || exception.Kind == EvmRpcFailureKind.RpcError && exception.RpcCode == null)
            && attempt < 4)
        {
            Console.WriteLine(
                $"LIVE RETRY | {operationName} | transient provider failure | attempt={attempt + 1}");
            var delay = exception.Kind == EvmRpcFailureKind.RateLimited
                ? TimeSpan.FromSeconds(2)
                : TimeSpan.FromSeconds(1 << attempt);
            await Task.Delay(delay);
        }
    }
}

static LiveEvmProtocolFixture[] LiveEvmProtocolFixtures(string chainId)
{
    return chainId switch
    {
        EvmChainDefinitions.EthereumMainnetChainId =>
        [
            new("Uniswap V2", LiveEvmProtocolKind.V2,
                "0xb4e16d0168e52d35cacd2c6185b44281ec28c9dc"),
            new("Uniswap V3", LiveEvmProtocolKind.V3,
                "0x88e6a0c2ddd26feeb64f039a2c41296fcb3f5640"),
            new("Uniswap V4", LiveEvmProtocolKind.V4,
                EthereumDeploymentRegistry.UniswapV4StateView,
                "0x284220e1aba6ce712aa7cd7f9d3eb1a9c25d082cceeb796beba70a3ff45f7379"),
            new("ShibaSwap V1", LiveEvmProtocolKind.V2,
                "0xcf6daab95c476106eca715d48de4b13287ffdeaa"),
            new("ShibaSwap V2", LiveEvmProtocolKind.V3,
                "0xab21798d88ee0854f99f1d148c1e59f238e561fe")
        ],
        EvmChainDefinitions.BaseMainnetChainId =>
        [
            new("Uniswap V2", LiveEvmProtocolKind.V2,
                "0x88a43bbdf9d098eec7bceda4e2494615dfd9bb9c"),
            new("Uniswap V3", LiveEvmProtocolKind.V3,
                BaseDeploymentRegistry.WrappedEtherUsdcReferencePool),
            new("Uniswap V4", LiveEvmProtocolKind.V4,
                BaseDeploymentRegistry.UniswapV4StateView,
                "0x1d8c55f347727c0fb4f5e1b65cdb93639e0c7102580a7d345e1144cd5a718f54"),
            new("PancakeSwap V2", LiveEvmProtocolKind.V2,
                "0x79474223aedd0339780bacce75abda0be84dcbf9"),
            new("PancakeSwap V3", LiveEvmProtocolKind.V3,
                "0x72ab388e2e2f6facef59e3c3fa2c4e29011c2d38"),
            new("PancakeSwap Infinity CL", LiveEvmProtocolKind.InfinityCl,
                BaseDeploymentRegistry.PancakeInfinityClPoolManager,
                "0x9ed2b133457a9debb64997f932b15a0a81f61718e8a267a4b36fab7b960d788a"),
            new("PancakeSwap Infinity Bin", LiveEvmProtocolKind.InfinityBin,
                BaseDeploymentRegistry.PancakeInfinityBinPoolManager,
                "0x34e19068fd32b59dc649757324d827675a736f2be33ec2bef4647bf876c0f6e7"),
            new("Aerodrome Classic", LiveEvmProtocolKind.AerodromeClassic,
                "0xcdac0d6c6c59727a65f871236188350531885c43"),
            new("Aerodrome Slipstream Initial", LiveEvmProtocolKind.AerodromeSlipstream,
                "0xb2cc224c1c9fee385f8ad6a55b4d94e92359dc59"),
            new("Aerodrome Slipstream Gauge Caps", LiveEvmProtocolKind.AerodromeSlipstream,
                "0xc758d81b9b81a6fcdad075bd471874a2c46b54e0"),
            new("Aerodrome Slipstream Gauges V3", LiveEvmProtocolKind.AerodromeSlipstream,
                "0x3fe04a59ebd38cf06080a6f60a98d124eb59392a")
        ],
        EvmChainDefinitions.BnbMainnetChainId =>
        [
            new("Uniswap V2", LiveEvmProtocolKind.V2,
                "0x8a1ed8e124fdfbd534bf48baf732e26db9cc0cf4"),
            new("Uniswap V3", LiveEvmProtocolKind.V3,
                "0x6fe9e9de56356f7edbfcbb29fab7cd69471a4869"),
            new("Uniswap V4", LiveEvmProtocolKind.V4,
                BnbDeploymentRegistry.UniswapV4StateView,
                "0x29d0870548bda44f40d9935fb2b1eda917f16310a02cba355a655c8365aa70c8"),
            new("PancakeSwap V2", LiveEvmProtocolKind.V2,
                "0x16b9a82891338f9ba80e2d6970fdda79d1eb0dae"),
            new("PancakeSwap V3", LiveEvmProtocolKind.V3,
                BnbDeploymentRegistry.WrappedBnbUsdtReferencePool),
            new("PancakeSwap Infinity CL", LiveEvmProtocolKind.InfinityCl,
                BnbDeploymentRegistry.PancakeInfinityClPoolManager,
                "0x32a805e65a3219a79707ab3e75443d75160d798de46613fc960d39cd4a96bb22"),
            new("PancakeSwap Infinity Bin", LiveEvmProtocolKind.InfinityBin,
                BnbDeploymentRegistry.PancakeInfinityBinPoolManager,
                "0xa4cfc9e29aa6a115f125701cc63ae060635d8546a09b8c216e716ca85fad9dff")
        ],
        EvmChainDefinitions.RobinhoodMainnetChainId =>
        [
            new("Uniswap V2", LiveEvmProtocolKind.V2,
                "0x8803c117ccae7b5146297876c2a25df135141c4d"),
            new("Uniswap V3", LiveEvmProtocolKind.V3,
                RobinhoodDeploymentRegistry.WrappedEtherUsdgReferencePool),
            new("Uniswap V4", LiveEvmProtocolKind.V4,
                RobinhoodDeploymentRegistry.UniswapV4StateView,
                "0xa5f23cae4e5c3388c5a8a6b08a83f53e56df8f1a63757e606b362994b68a2361")
        ],
        _ => throw new InvalidOperationException($"No live protocol fixtures exist for chain {chainId}.")
    };
}

static async Task VerifyLiveEvmProtocolAsync(
    EvmJsonRpcClient rpc,
    LiveEvmProtocolFixture protocol,
    object blockReference,
    string providerLabel,
    CancellationToken cancellationToken)
{
    var callData = protocol.Kind switch
    {
        LiveEvmProtocolKind.V2 or LiveEvmProtocolKind.AerodromeClassic =>
            EthereumAbi.GetReservesSelector,
        LiveEvmProtocolKind.V3 or LiveEvmProtocolKind.AerodromeSlipstream =>
            EthereumAbi.Slot0Selector,
        LiveEvmProtocolKind.V4 => EthereumAbi.EncodeBytes32Call(
            EthereumAbi.UniswapV4StateViewSlot0Selector,
            protocol.PoolId!),
        LiveEvmProtocolKind.InfinityCl or LiveEvmProtocolKind.InfinityBin =>
            EthereumAbi.EncodeBytes32Call(EthereumAbi.InfinitySlot0Selector, protocol.PoolId!),
        _ => throw new InvalidOperationException($"Unsupported live probe kind {protocol.Kind}.")
    };
    var state = await rpc.CallAsync(
        protocol.TargetAddress,
        callData,
        blockReference,
        cancellationToken);
    var stateData = state.ValueKind == JsonValueKind.String ? state.GetString() : null;
    var stateValid = protocol.Kind switch
    {
        LiveEvmProtocolKind.V2 =>
            EthereumAbi.TryDecodeUniswapV2Reserves(stateData, out _, out _),
        LiveEvmProtocolKind.AerodromeClassic =>
            EthereumAbi.TryDecodeAerodromeClassicReserves(stateData, out _, out _),
        LiveEvmProtocolKind.V3 =>
            EthereumAbi.TryDecodeUniswapV3Slot0(stateData, out _, out _),
        LiveEvmProtocolKind.AerodromeSlipstream =>
            EthereumAbi.TryDecodeAerodromeSlipstreamSlot0(stateData, out _, out _),
        LiveEvmProtocolKind.V4 or LiveEvmProtocolKind.InfinityCl =>
            EthereumAbi.TryDecodeUniswapV4Slot0(stateData, out _, out _),
        LiveEvmProtocolKind.InfinityBin =>
            EthereumAbi.TryDecodeInfinityBinSlot0(stateData, out _),
        _ => false
    };
    Assert(stateValid, $"{providerLabel} returned malformed {protocol.Name} state.");

    if (protocol.Kind is not (LiveEvmProtocolKind.V3
        or LiveEvmProtocolKind.AerodromeSlipstream
        or LiveEvmProtocolKind.V4
        or LiveEvmProtocolKind.InfinityCl))
    {
        return;
    }
    var liquidityCall = protocol.Kind switch
    {
        LiveEvmProtocolKind.V3 or LiveEvmProtocolKind.AerodromeSlipstream =>
            EthereumAbi.LiquiditySelector,
        LiveEvmProtocolKind.V4 => EthereumAbi.EncodeBytes32Call(
            EthereumAbi.UniswapV4StateViewLiquiditySelector,
            protocol.PoolId!),
        LiveEvmProtocolKind.InfinityCl => EthereumAbi.EncodeBytes32Call(
            EthereumAbi.InfinityLiquiditySelector,
            protocol.PoolId!),
        _ => throw new InvalidOperationException($"Unsupported liquidity probe kind {protocol.Kind}.")
    };
    var liquidity = await rpc.CallAsync(
        protocol.TargetAddress,
        liquidityCall,
        blockReference,
        cancellationToken);
    Assert(
        liquidity.ValueKind == JsonValueKind.String
        && EthereumAbi.TryDecodeSingleUnsigned(liquidity.GetString(), 128, out _),
        $"{providerLabel} returned malformed {protocol.Name} liquidity.");
}

static void VerifyCurveCatalogParsing()
{
    const string selected = "0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2";
    const string pool = "0x1111111111111111111111111111111111111111";
    var parsed = CurvePoolCatalogClient.Parse(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            success = true,
            data = new
            {
                poolData = new object[]
                {
                    new
                    {
                        address = pool,
                        coinsAddresses = new[]
                        {
                            selected,
                            "0x2222222222222222222222222222222222222222",
                            EthereumDeploymentRegistry.Usdc
                        },
                        coins = new object[]
                        {
                            new { address = selected, symbol = "WETH", name = "Wrapped Ether" },
                            new { address = "0x2222222222222222222222222222222222222222", symbol = "OTHER", name = "Other" },
                            new { address = EthereumDeploymentRegistry.Usdc, symbol = "USDC", name = "USD Coin" }
                        },
                        registryId = "factory-twocrypto",
                        assetTypeName = "crypto",
                        usdTotal = 1234.5,
                        creationTs = 1_700_000_000,
                        isBroken = false
                    },
                    new
                    {
                        address = "0x3333333333333333333333333333333333333333",
                        coinsAddresses = new[] { selected, EthereumDeploymentRegistry.Usdc },
                        coins = Array.Empty<object>(),
                        isBroken = true
                    }
                }
            }
        }),
        selected,
        EthereumDeploymentRegistry.MainnetQuoteAssets);
    AssertEqual(1, parsed.Pools.Length, "Curve catalog parsing did not exclude broken pools.");
    AssertEqual(OnChainProtocolIds.Curve, parsed.Pools[0].ProtocolId,
        "Curve catalog parsing returned the wrong protocol.");
    AssertEqual(pool, parsed.Pools[0].PoolAddress,
        "Curve catalog parsing returned the wrong pool.");
    AssertEqual((uint?)0, parsed.Pools[0].BaseTokenIndex,
        "Curve catalog parsing returned the wrong base index.");
    AssertEqual((uint?)2, parsed.Pools[0].QuoteTokenIndex,
        "Curve catalog parsing did not prefer a configured quote coin.");
    AssertEqual(EthereumDeploymentRegistry.Usdc, parsed.Pools[0].QuoteAsset.Address,
        "Curve catalog parsing returned the wrong quote coin.");
}

static async Task VerifyProviderCachePoliciesAsync()
{
    var cache = new BoundedAsyncCache<string, int>(2, StringComparer.Ordinal);
    var factoryCalls = 0;
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var concurrent = Enumerable.Range(0, 16)
        .Select(_ => cache.GetOrCreateAsync(
            "same",
            TimeSpan.FromMinutes(1),
            async () =>
            {
                Interlocked.Increment(ref factoryCalls);
                await gate.Task;
                return 42;
            }))
        .ToArray();
    await WaitUntilAsync(
        () => Volatile.Read(ref factoryCalls) == 1,
        TimeSpan.FromSeconds(2),
        "one coalesced catalog request");
    gate.SetResult();
    Assert((await Task.WhenAll(concurrent)).All(static value => value == 42),
        "Coalesced cache callers did not receive the shared result.");
    AssertEqual(1, factoryCalls, "Concurrent identical catalog lookups were not coalesced.");

    _ = await cache.GetOrCreateAsync("second", TimeSpan.FromMinutes(1), () => Task.FromResult(2));
    _ = await cache.GetOrCreateAsync("third", TimeSpan.FromMinutes(1), () => Task.FromResult(3));
    AssertEqual(2, cache.Count, "The provider cache exceeded its configured resource bound.");

    var failureCalls = 0;
    try
    {
        _ = await cache.GetOrCreateAsync(
            "failure",
            TimeSpan.FromMinutes(1),
            () =>
            {
                Interlocked.Increment(ref failureCalls);
                return Task.FromException<int>(new InvalidOperationException("fixture failure"));
            });
        throw new InvalidOperationException("A failed provider request was accepted as a cache value.");
    }
    catch (InvalidOperationException exception) when (exception.Message == "fixture failure")
    {
    }
    AssertEqual(7, await cache.GetOrCreateAsync(
        "failure",
        TimeSpan.FromMinutes(1),
        () =>
        {
            Interlocked.Increment(ref failureCalls);
            return Task.FromResult(7);
        }), "A failed provider request poisoned its cache key.");
    AssertEqual(2, failureCalls, "A failed provider request was cached instead of retried.");

    var cancellationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var cancellationFactoryCalls = 0;
    using var waiterCancellation = new CancellationTokenSource();
    var canceledWaiter = cache.GetOrCreateAsync(
        "cancelled-waiter",
        TimeSpan.FromMinutes(1),
        async () =>
        {
            Interlocked.Increment(ref cancellationFactoryCalls);
            await cancellationGate.Task;
            return 9;
        },
        waiterCancellation.Token);
    await WaitUntilAsync(
        () => Volatile.Read(ref cancellationFactoryCalls) == 1,
        TimeSpan.FromSeconds(2),
        "cancelable provider cache producer");
    var survivingWaiter = cache.GetOrCreateAsync(
        "cancelled-waiter",
        TimeSpan.FromMinutes(1),
        () => Task.FromResult(99));
    waiterCancellation.Cancel();
    try
    {
        _ = await canceledWaiter;
        throw new InvalidOperationException("A canceled cache waiter completed successfully.");
    }
    catch (OperationCanceledException)
    {
    }
    cancellationGate.SetResult();
    AssertEqual(9, await survivingWaiter,
        "Canceling one waiter canceled the shared provider request.");
    AssertEqual(1, cancellationFactoryCalls,
        "Canceling one waiter started a duplicate provider request.");

    const string manifestMintA = "mint-a";
    const string manifestMintB = "mint-b";
    var manifestPayload = JsonSerializer.SerializeToUtf8Bytes(new object[]
    {
        new
        {
            ticker_id = "manifest-a",
            pool_id = "manifest-a",
            base_currency = manifestMintA,
            target_currency = manifestMintB,
            liquidity_in_usd = "100"
        }
    });
    using var manifestHandler = new StaticJsonFixtureHandler(manifestPayload);
    using var manifestHttp = new HttpClient(manifestHandler)
    {
        BaseAddress = new Uri("https://mfx-stats-mainnet.fly.dev/")
    };
    var manifest = new ManifestPoolCatalogClient(manifestHttp);
    var manifestResults = await Task.WhenAll(
        manifest.SearchAsync(manifestMintA),
        manifest.SearchAsync(manifestMintB));
    AssertEqual(1, manifestHandler.RequestCount,
        "Manifest downloaded its identical global catalog once per mint.");
    Assert(manifestResults.All(static result => result.Pools.Length == 1),
        "Manifest global catalog reuse changed per-mint filtering.");

    const string curveAssetA = "0x1111111111111111111111111111111111111111";
    const string curveAssetB = "0x2222222222222222222222222222222222222222";
    var curvePayload = JsonSerializer.SerializeToUtf8Bytes(new
    {
        success = true,
        data = new
        {
            poolData = new object[]
            {
                new
                {
                    address = "0x3333333333333333333333333333333333333333",
                    coinsAddresses = new[] { curveAssetA, curveAssetB },
                    coins = new object[]
                    {
                        new { symbol = "A", name = "Asset A" },
                        new { symbol = "B", name = "Asset B" }
                    },
                    usdTotal = 1000,
                    isBroken = false
                }
            }
        }
    });
    using var curveHandler = new StaticJsonFixtureHandler(curvePayload);
    using var curveHttp = new HttpClient(curveHandler)
    {
        BaseAddress = new Uri("https://api.curve.finance/")
    };
    var curve = new CurvePoolCatalogClient(curveHttp);
    var curveResults = await Task.WhenAll(
        curve.SearchAsync(curveAssetA),
        curve.SearchAsync(curveAssetB));
    AssertEqual(1, curveHandler.RequestCount,
        "Curve downloaded its identical Ethereum catalog once per token.");
    Assert(curveResults.All(static result => result.Pools.Length == 1),
        "Curve global catalog reuse changed per-token filtering.");
}

static async Task VerifyCurveDiscoveryAsync()
{
    const string selected = EthereumDeploymentRegistry.WrappedEther;
    const string quote = EthereumDeploymentRegistry.Usdc;
    const string poolAddress = "0x1111111111111111111111111111111111111111";
    using var handler = new CurveRpcFixtureHandler(selected, quote, poolAddress);
    using var httpClient = new HttpClient(handler);
    var configuration = CreateProviderConfiguration(
        OnChainProviderTypes.PublicNodeEthereum,
        "wss://ethereum-rpc.publicnode.com",
        "https://ethereum-rpc.publicnode.com");
    var result = await new EthereumPoolDiscoveryService(
            new EvmJsonRpcClient(httpClient, configuration, null),
            httpClient)
        .DiscoverAsync(selected,
        [
            new PoolCatalogEntry
            {
                SourceId = "curve-api",
                ChainId = "ethereum",
                ProtocolId = OnChainProtocolIds.Curve,
                PoolAddress = poolAddress,
                BaseAsset = new PoolCatalogAsset { Address = selected },
                QuoteAsset = new PoolCatalogAsset { Address = quote },
                BaseTokenIndex = 0,
                QuoteTokenIndex = 2
            }
        ]);
    var curve = result.Pools.Single(pool => pool.PoolKey.ProtocolId == OnChainProtocolIds.Curve);
    AssertEqual(OnChainSupportStatus.Supported, curve.SupportStatus,
        "The pinned Curve fixture did not validate.");
    AssertEqual(EthereumDeploymentRegistry.CurveAddressProvider,
        curve.PoolKey.DeploymentKey.ContractAddress,
        "Curve discovery returned the wrong canonical deployment identity.");
    AssertEqual((uint?)0, curve.ProtocolAccounts.Single(account => account.Role == "baseCoin").Index,
        "Curve discovery returned the wrong base coin index.");
    AssertEqual((uint?)2, curve.ProtocolAccounts.Single(account => account.Role == "quoteCoin").Index,
        "Curve discovery returned the wrong quote coin index.");
    var fermi = result.Pools.Single(pool => pool.PoolKey.ProtocolId == OnChainProtocolIds.FermiSwap);
    AssertEqual(OnChainSupportStatus.Supported, fermi.SupportStatus,
        "The recent FermiSwap execution fixture did not produce a supported pair.");
    AssertEqual(EthereumDeploymentRegistry.FermiCurrentSwapper,
        fermi.PoolKey.DeploymentKey.ContractAddress,
        "FermiSwap discovery returned the wrong active swapper contract.");
    AssertEqual("0xc954555cf554e837f0314ed6a6c4d6372db48b21978d33695c3548b8d1fb6f59",
        fermi.PoolKey.PoolId,
        "FermiSwap discovery returned a non-canonical pair identity.");
    var filters = JsonSerializer.Serialize(
        EvmWebSocketStreamSource.CreateRpcLogFilters(
        [
            new OnChainWatchedPoolSelection { SelectedMint = selected, Descriptor = curve },
            new OnChainWatchedPoolSelection { SelectedMint = selected, Descriptor = fermi }
        ],
        1,
        2));
    Assert(filters.Contains(EvmWebSocketStreamSource.CurveTokenExchangeSignedTopic, StringComparison.Ordinal)
           && filters.Contains(EvmWebSocketStreamSource.CurveTokenExchangeUnsignedTopic, StringComparison.Ordinal)
           && filters.Contains(EvmWebSocketStreamSource.CurveTokenExchangeExtendedTopic, StringComparison.Ordinal),
        "Curve replay filters omitted an official TokenExchange event layout.");
    Assert(filters.Contains(EvmWebSocketStreamSource.FermiSwapTopic, StringComparison.Ordinal)
           && filters.Contains(EvmWebSocketStreamSource.FermiSwappedTopic, StringComparison.Ordinal),
        "FermiSwap replay filters omitted a supported execution event.");
}

static async Task VerifyLiveRobinhoodDiscoveryAsync()
{
    const string usde = "0x5d3a1ff2b6bab83b63cd9ad0787074081a52ef34";
    const string longAsset = "0x2e8c31162b855a2ffa90f6f8634643ad6f111e18";
    const string ponsV1Delta = "0xe8ffd7e24187f72afb08d75b1bb13088a989a791";
    const string expectedV4PoolId =
        "0xa5f23cae4e5c3388c5a8a6b08a83f53e56df8f1a63757e606b362994b68a2361";
    using var catalogHttp = new HttpClient
    {
        BaseAddress = new Uri("https://api.dexscreener.com/"),
        Timeout = TimeSpan.FromSeconds(30)
    };
    using var rpcHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var alchemyApiKey = Environment.GetEnvironmentVariable("TRENCHHQ_ALCHEMY_API_KEY");
    var robinhoodPreset = OnChainProviderCatalog.Get(OnChainProviderTypes.AlchemyRobinhood);
    var configuration = string.IsNullOrWhiteSpace(alchemyApiKey)
        ? CreateProviderConfiguration(
            OnChainProviderTypes.CustomRobinhood,
            "wss://unused.invalid",
            RobinhoodDeploymentRegistry.PublicRpcEndpoint)
        : CreateProviderConfiguration(
            robinhoodPreset.ProviderType,
            robinhoodPreset.DefaultStreamEndpoint,
            robinhoodPreset.DefaultRpcEndpoint);
    var discovery = new EthereumPoolDiscoveryService(
        new EvmJsonRpcClient(rpcHttp, configuration, alchemyApiKey),
        EvmChainDefinitions.RobinhoodMainnet,
        RobinhoodDeploymentRegistry.Catalog,
        rpcHttp);
    var catalogClient = new DexScreenerPoolCatalogClient(catalogHttp);
    var catalogToken = Environment.GetEnvironmentVariable("TRENCHHQ_LIVE_ROBINHOOD_TOKEN");
    if (!string.IsNullOrWhiteSpace(catalogToken))
    {
        Console.WriteLine("LIVE ROBINHOOD | validating requested token's three largest V4 pools");
        using var catalogTokenTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var tokenCatalog = await catalogClient.SearchAsync(
            RobinhoodDeploymentRegistry.Catalog.CatalogChainId,
            catalogToken,
            catalogTokenTimeout.Token);
        var largestV4CatalogPools = tokenCatalog.Pools
            .Where(pool => pool.ProtocolId.Equals("uniswap", StringComparison.OrdinalIgnoreCase)
                           && pool.Labels.Contains("v4", StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(static pool => pool.LiquidityUsd ?? double.MinValue)
            .Take(3)
            .ToArray();
        AssertEqual(3, largestV4CatalogPools.Length,
            "The requested Robinhood Chain token did not return three V4 catalog pools.");
        if (string.Equals(
                Environment.GetEnvironmentVariable("TRENCHHQ_LIVE_ROBINHOOD_COLD_CATALOG"),
                "1",
                StringComparison.Ordinal))
        {
            var coldTimer = System.Diagnostics.Stopwatch.StartNew();
            var coldResult = await discovery.DiscoverCatalogPoolsSharedAsync(
                catalogToken,
                tokenCatalog.Pools,
                catalogTokenTimeout.Token);
            coldTimer.Stop();
            Assert(largestV4CatalogPools.All(catalogPool => coldResult.Pools.Any(pool =>
                    pool.PoolKey.PoolId.Equals(catalogPool.PoolAddress, StringComparison.OrdinalIgnoreCase)
                    && pool.SupportStatus == OnChainSupportStatus.Supported)),
                "The cold catalog path lost a supported top-liquidity Robinhood pool.");
            Console.WriteLine(
                $"LIVE ROBINHOOD COLD CATALOG | catalogPools={tokenCatalog.Pools.Length} | supported={coldResult.Pools.Count(pool => pool.SupportStatus == OnChainSupportStatus.Supported)} | elapsedMs={coldTimer.ElapsedMilliseconds}");
            return;
        }
        foreach (var catalogPool in largestV4CatalogPools)
        {
            var poolTimer = System.Diagnostics.Stopwatch.StartNew();
            var tokenResult = await discovery.DiscoverCatalogPoolsAsync(
                catalogToken,
                [catalogPool],
                catalogTokenTimeout.Token);
            poolTimer.Stop();
            var descriptor = tokenResult.Pools.Single(pool => pool.PoolKey.PoolId.Equals(
                catalogPool.PoolAddress,
                StringComparison.OrdinalIgnoreCase));
            Console.WriteLine(
                $"LIVE ROBINHOOD TOKEN | {descriptor.PoolKey.PoolId} | liquidityUsd={catalogPool.LiquidityUsd} | {descriptor.SupportStatus} | elapsedMs={poolTimer.ElapsedMilliseconds} | {descriptor.SupportReason ?? "validated"}");
            AssertEqual(OnChainSupportStatus.Supported, descriptor.SupportStatus,
                "A requested top-liquidity Robinhood Chain V4 pool failed on-chain validation.");
        }
        if (string.Equals(
                Environment.GetEnvironmentVariable("TRENCHHQ_LIVE_ROBINHOOD_FULL_SEARCH"),
                "1",
                StringComparison.Ordinal))
        {
            var catalogTimer = System.Diagnostics.Stopwatch.StartNew();
            var concurrentCatalogResult = await discovery.DiscoverCatalogPoolsSharedAsync(
                catalogToken,
                tokenCatalog.Pools,
                catalogTokenTimeout.Token);
            catalogTimer.Stop();
            Console.WriteLine(
                $"LIVE ROBINHOOD CONCURRENT CATALOG | catalogPools={tokenCatalog.Pools.Length} | supported={concurrentCatalogResult.Pools.Count(pool => pool.SupportStatus == OnChainSupportStatus.Supported)} | elapsedMs={catalogTimer.ElapsedMilliseconds}");
            var searchTimer = System.Diagnostics.Stopwatch.StartNew();
            var fullResult = await discovery.DiscoverAsync(
                catalogToken,
                tokenCatalog.Pools,
                catalogTokenTimeout.Token);
            searchTimer.Stop();
            Assert(largestV4CatalogPools.All(catalogPool => fullResult.Pools.Any(pool =>
                    pool.PoolKey.PoolId.Equals(catalogPool.PoolAddress, StringComparison.OrdinalIgnoreCase)
                    && pool.SupportStatus == OnChainSupportStatus.Supported)),
                "The full Robinhood search path lost a supported top-liquidity pool.");
            Console.WriteLine(
                $"LIVE ROBINHOOD FULL SEARCH | catalogPools={tokenCatalog.Pools.Length} | supported={fullResult.Pools.Count(pool => pool.SupportStatus == OnChainSupportStatus.Supported)} | elapsedMs={searchTimer.ElapsedMilliseconds}");
        }
        return;
    }

    Console.WriteLine("LIVE ROBINHOOD | validating WETH reference discovery");
    using var wethTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    var wethCatalog = await catalogClient.SearchAsync(
        RobinhoodDeploymentRegistry.Catalog.CatalogChainId,
        RobinhoodDeploymentRegistry.WrappedEther,
        wethTimeout.Token);
    var referenceCatalog = wethCatalog.Pools.Where(pool => pool.PoolAddress.Equals(
        RobinhoodDeploymentRegistry.WrappedEtherUsdgReferencePool,
        StringComparison.OrdinalIgnoreCase)).ToArray();
    AssertEqual(1, referenceCatalog.Length,
        "The live catalog did not return the canonical Robinhood Chain WETH/USDG reference pool.");
    var wethResult = await discovery.DiscoverAsync(
        RobinhoodDeploymentRegistry.WrappedEther,
        referenceCatalog,
        wethTimeout.Token);
    var referencePool = wethResult.Pools.Single(pool =>
        pool.PoolKey.PoolId.Equals(
            RobinhoodDeploymentRegistry.WrappedEtherUsdgReferencePool,
            StringComparison.OrdinalIgnoreCase));
    AssertEqual(OnChainProtocolIds.UniswapV3, referencePool.PoolKey.ProtocolId,
        "Live Robinhood Chain discovery returned the wrong reference protocol.");
    AssertEqual(OnChainSupportStatus.Supported, referencePool.SupportStatus,
        "The canonical Robinhood Chain WETH/USDG reference pool failed validation.");

    Console.WriteLine("LIVE ROBINHOOD | validating USDe V4 discovery");
    using var usdeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    var usdeCatalog = await catalogClient.SearchAsync(
        RobinhoodDeploymentRegistry.Catalog.CatalogChainId,
        usde,
        usdeTimeout.Token);
    var v4Catalog = usdeCatalog.Pools.Where(pool => pool.PoolAddress.Equals(
        expectedV4PoolId,
        StringComparison.OrdinalIgnoreCase)).ToArray();
    AssertEqual(1, v4Catalog.Length,
        "The live catalog did not return the canonical Robinhood Chain USDe V4 pool.");
    var usdeResult = await discovery.DiscoverAsync(usde, v4Catalog, usdeTimeout.Token);
    var v4Pool = usdeResult.Pools.Single(pool =>
        pool.PoolKey.PoolId.Equals(expectedV4PoolId, StringComparison.OrdinalIgnoreCase));
    AssertEqual(OnChainProtocolIds.UniswapV4, v4Pool.PoolKey.ProtocolId,
        "Live Robinhood Chain discovery returned the wrong V4 protocol.");
    AssertEqual(OnChainSupportStatus.Supported, v4Pool.SupportStatus,
        "The live Robinhood Chain zero-hook V4 pool failed validation.");
    AssertEqual(RobinhoodDeploymentRegistry.UniswapV4PoolManager, v4Pool.ProgramId,
        "Live Robinhood Chain V4 discovery returned the wrong PoolManager.");
    AssertEqual(RobinhoodDeploymentRegistry.NativeEther, v4Pool.HookAddress,
        "The recorded Robinhood Chain V4 pool no longer has a zero hook.");

    Console.WriteLine("LIVE ROBINHOOD | validating long.xyz launchpad discovery");
    using var longTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    var longResult = await discovery.DiscoverAsync(longAsset, longTimeout.Token);
    foreach (var warning in longResult.Warnings)
    {
        Console.WriteLine($"LIVE ROBINHOOD WARNING | {warning}");
    }
    var longPool = longResult.Pools.Single(pool =>
        pool.DiscoverySource.StartsWith("canonical:long.xyz", StringComparison.Ordinal));
    AssertEqual(OnChainProtocolIds.UniswapV4, longPool.PoolKey.ProtocolId,
        "Live long.xyz discovery returned the wrong protocol.");
    AssertEqual(OnChainSupportStatus.Supported, longPool.SupportStatus,
        "The canonical long.xyz pool failed validation.");
    AssertEqual(RobinhoodDeploymentRegistry.DopplerHookInitializer, longPool.HookAddress,
        "Live long.xyz discovery returned the wrong Doppler hook.");
    AssertEqual("spotOnly", longPool.PricingMode,
        "The returned-delta long.xyz pool was not constrained to spot-only pricing.");

    Console.WriteLine("LIVE ROBINHOOD | validating pons v1 launchpad discovery");
    using var ponsTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    var ponsV1Result = await discovery.DiscoverAsync(ponsV1Delta, ponsTimeout.Token);
    var ponsV1Pool = ponsV1Result.Pools.Single(pool =>
        pool.DiscoverySource.StartsWith("canonical:pons-v1", StringComparison.Ordinal));
    AssertEqual(OnChainProtocolIds.UniswapV3, ponsV1Pool.PoolKey.ProtocolId,
        "Live pons v1 discovery returned the wrong protocol.");
    AssertEqual(OnChainSupportStatus.Supported, ponsV1Pool.SupportStatus,
        "The canonical pons v1 pool failed validation.");
    Console.WriteLine(
        $"LIVE ROBINHOOD | WETH pools={wethResult.Pools.Length} | USDe pools={usdeResult.Pools.Length} | V4={v4Pool.PoolKey.PoolId} | long.xyz={longPool.PoolKey.PoolId} | pons-v1={ponsV1Pool.PoolKey.PoolId}");

}

static async Task<OnChainWatchedPoolSelection> DiscoverDedicatedAlchemySolanaPoolAsync(
    HttpClient rpcHttp, OnChainProviderConfiguration configuration, string credential,
    OnChainEngineClient client)
{
    const string wrappedSol = "So11111111111111111111111111111111111111112";
    const string solanaPoolId = "58oQChx4yWmvKdwLLZzBi4ChoCc2fqCUWBkwMihLYQo2";
    var discovered = await new OnChainPoolDiscoveryService(
            new SolanaRpcClient(rpcHttp, configuration, credential), client)
        .DiscoverAsync(wrappedSol, OnChainCommitment.Confirmed, [solanaPoolId],
            includeDerivedDiscovery: false);
    return new OnChainWatchedPoolSelection
    {
        SelectedMint = wrappedSol,
        Descriptor = discovered.Pools.Single(pool =>
            pool.PoolKey.PoolAddress == solanaPoolId
            && pool.SupportStatus == OnChainSupportStatus.Supported)
    };
}

static async Task VerifyDedicatedAlchemySolanaSnapshotAsync(
    string enginePath, string credential, int sampleSeconds)
{
    var sampleIntervalSeconds = (int)SolanaRpcSampleStreamSource.SampleInterval.TotalSeconds;
    var baselineProcessIds = GetEngineProcessIds();
    var client = new OnChainEngineClient(enginePath);
    using var rpcHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var configuration = OnChainProviderConfigurationStore.CreateConfiguration(
        OnChainProviderCatalog.Get(OnChainProviderTypes.AlchemyWebSocket));
    var prices = new ConcurrentQueue<OnChainPriceUpdate>();
    client.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
    try
    {
        var selected = await DiscoverDedicatedAlchemySolanaPoolAsync(
            rpcHttp, configuration, credential, client);
        Console.WriteLine($"ALCHEMY SOLANA POOL | quote={selected.Descriptor.QuoteMint} | orientation={selected.Descriptor.PairOrientation} | accounts={selected.Descriptor.EnumerateSolanaAccountAddresses().Count()}");
        await client.ReplaceWatchedPoolsAsync("solana:mainnet-beta", [selected]);
        await new OnChainSnapshotReconciler(client).ReconcileAsync(
            configuration, credential, [selected], CancellationToken.None);
        await WaitUntilAsync(() => prices.Count > 0, TimeSpan.FromSeconds(10),
            "Alchemy Solana snapshot price");
        var price = prices.Last();
        Console.WriteLine($"ALCHEMY SOLANA SNAPSHOT | spot={price.SpotPriceQuote != null} | usd={price.PriceUsd != null} | source={price.SourceId} | updates={prices.Count}");
        Assert(price.PriceUsd != null,
            "The live Alchemy Solana SOL/USDC snapshot did not produce a USD price.");
        if (sampleSeconds > 0)
        {
            var addresses = selected.Descriptor.EnumerateSolanaAccountAddresses()
                .Where(static address => !string.IsNullOrWhiteSpace(address))
                .Select(static address => address!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var rpc = new SolanaRpcClient(rpcHttp, configuration, credential);
            var started = DateTimeOffset.UtcNow;
            for (var sample = 0; sample < sampleSeconds / sampleIntervalSeconds; sample++)
            {
                var accounts = await rpc.GetMultipleAccountsAsync(
                    addresses, configuration.Commitment, CancellationToken.None);
                var observedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                foreach (var account in accounts.Where(static account => account != null))
                {
                    await client.PublishAccountUpdateAsync(new OnChainRawAccountUpdate
                    {
                        Pubkey = account!.Address,
                        OwnerProgram = account.OwnerProgram,
                        DataBase64 = account.DataBase64,
                        Slot = account.Slot,
                        WriteVersion = 0,
                        Commitment = configuration.Commitment,
                        SourceId = $"rpcSample:{configuration.Id}",
                        ObservedAtUnixMs = observedAt
                    });
                }
                var nextSampleAt = started + SolanaRpcSampleStreamSource.SampleInterval * (sample + 1);
                await Task.Delay(nextSampleAt - DateTimeOffset.UtcNow > TimeSpan.Zero
                    ? nextSampleAt - DateTimeOffset.UtcNow
                    : TimeSpan.Zero);
            }
            var sampledPrices = prices.Count(update => update.SourceId.StartsWith(
                "rpcSample:", StringComparison.Ordinal) && update.PriceUsd != null);
            Console.WriteLine($"ALCHEMY SOLANA SAMPLE | utcStart={started:O} | utcEnd={DateTimeOffset.UtcNow:O} | intervalSeconds={SolanaRpcSampleStreamSource.SampleInterval.TotalSeconds} | samples={sampleSeconds / sampleIntervalSeconds} | usdPrices={sampledPrices} | engine={GetSingleNewEngineProcessId(baselineProcessIds)}");
            Assert(sampledPrices >= sampleSeconds / sampleIntervalSeconds,
                "A Solana pool-state sample failed to produce a USD price update.");
        }
    }
    finally
    {
        await client.StopAsync();
    }
    await WaitUntilAsync(() => !GetEngineProcessIds().Except(baselineProcessIds).Any(),
        TimeSpan.FromSeconds(10), "Alchemy Solana snapshot engine stop");
}

static async Task VerifyDedicatedAlchemyMixedMatrixAsync(
    string enginePath, string credential, string scenario, TimeSpan observationWindow,
    bool webSocket)
{
    const string solanaPoolId = "58oQChx4yWmvKdwLLZzBi4ChoCc2fqCUWBkwMihLYQo2";
    var checkpointRoot = Path.Combine(Path.GetTempPath(), "TrenchHQAlchemyMixedCheckpoint",
        Guid.NewGuid().ToString("N"));
    var baselineProcessIds = GetEngineProcessIds();
    var client = new OnChainEngineClient(enginePath);
    using var rpcHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var prices = new ConcurrentQueue<OnChainPriceUpdate>();
    var logs = new ConcurrentQueue<EvmLogUpdate>();
    var errors = new ConcurrentQueue<string>();
    client.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
    var solanaConfiguration = OnChainProviderConfigurationStore.CreateConfiguration(
        OnChainProviderCatalog.Get(OnChainProviderTypes.AlchemyWebSocket));
    var solana = new OnChainStreamCoordinator(client, checkpointRoot);
    solana.StreamError += (_, message) => errors.Enqueue(
        "solana:" + message.Replace(credential, "[redacted]", StringComparison.Ordinal));
    var evm = new EvmStreamCoordinatorCollection(client, checkpointRoot,
        chain => new EvmStreamCoordinator(client, checkpointRoot, chain, rpcHttp,
            sourceFactory: (profile, key) => new ObservingEvmStreamSource(
                new EvmWebSocketStreamSource(profile, key), logs, static () => { })));
    evm.StreamError += (sender, message) => errors.Enqueue(
        $"{(sender as EvmStreamCoordinator)?.ChainDefinition.CatalogChainId}:"
        + message.Replace(credential, "[redacted]", StringComparison.Ordinal));
    try
    {
        var includeSolana = scenario is "solana" or "2" or "3" or "5" or "5-stock";
        var solanaPool = includeSolana
            ? await DiscoverDedicatedAlchemySolanaPoolAsync(
                rpcHttp, solanaConfiguration, credential, client)
            : null;
        var pools = new List<(EvmChainDefinition Chain, string ProviderType,
            OnChainWatchedPoolSelection[] Selections)>();
        if (scenario is "robinhood" or "2" or "3" or "5" or "5-stock")
        {
            const string schiffy = "0x42aFA2124ca5a2B83898E46B2dA9a190995b1E18";
            const string usdgPool =
                "0xf66ccb1c5e6b579f94311131724a8daf0d96b0ebdbe53ab9fa27b98a8abaeb8b";
            const string gldPool =
                "0xc749412e31087a6e6f9210af575bc9100159fbc9f45da3ca8b26b31f3bf5777e";
            using var catalogHttp = new HttpClient
            {
                BaseAddress = new Uri("https://api.dexscreener.com/"),
                Timeout = TimeSpan.FromSeconds(30)
            };
            var catalog = await new DexScreenerPoolCatalogClient(catalogHttp)
                .SearchAsync("robinhood", schiffy);
            var poolIds = scenario == "5-stock"
                ? new[] { usdgPool, gldPool }
                : [usdgPool];
            var candidates = catalog.Pools.Where(pool => poolIds.Contains(
                pool.PoolAddress, StringComparer.OrdinalIgnoreCase)).ToArray();
            AssertEqual(poolIds.Length, candidates.Length,
                "The Alchemy stock test lost a selected SCHIFFY catalog pool.");
            var robinhoodPreset = OnChainProviderCatalog.Get(OnChainProviderTypes.AlchemyRobinhood);
            var robinhoodRpc = new EvmJsonRpcClient(rpcHttp,
                CreateProviderConfiguration(robinhoodPreset.ProviderType,
                    robinhoodPreset.DefaultStreamEndpoint, robinhoodPreset.DefaultRpcEndpoint),
                credential);
            var discovered = await new EthereumPoolDiscoveryService(
                    robinhoodRpc, EvmChainDefinitions.RobinhoodMainnet,
                    RobinhoodDeploymentRegistry.Catalog, rpcHttp)
                .DiscoverCatalogPoolsSharedAsync(schiffy, candidates, CancellationToken.None);
            pools.Add((EvmChainDefinitions.RobinhoodMainnet,
                OnChainProviderTypes.AlchemyRobinhood,
                poolIds.Select(poolId => new OnChainWatchedPoolSelection
                {
                    SelectedMint = schiffy,
                    Descriptor = discovered.Pools.Single(pool =>
                        pool.PoolKey.PoolId.Equals(poolId, StringComparison.OrdinalIgnoreCase)
                        && pool.SupportStatus == OnChainSupportStatus.Supported)
                }).ToArray()));
        }
        if (scenario is "bnb" or "3" or "5")
        {
            pools.Add((EvmChainDefinitions.BnbMainnet, OnChainProviderTypes.AlchemyBnb,
                [BnbDeploymentRegistry.CreateBnbUsdReferenceSelection()]));
        }
        if (scenario is "ethereum" or "5" or "5-stock")
        {
            pools.Add((EvmChainDefinitions.EthereumMainnet, OnChainProviderTypes.AlchemyEthereum,
                [EthereumDeploymentRegistry.CreateEthUsdReferenceSelection()]));
        }
        if (scenario is "base" or "5" or "5-stock")
        {
            pools.Add((EvmChainDefinitions.BaseMainnet, OnChainProviderTypes.AlchemyBase,
                [BaseDeploymentRegistry.CreateEthUsdReferenceSelection()]));
        }
        var expectedCount = scenario switch
        {
            "solana" or "robinhood" or "bnb" or "ethereum" or "base" => 1,
            "5-stock" => 5,
            _ => int.Parse(scenario)
        };
        AssertEqual(expectedCount,
            (includeSolana ? 1 : 0) + pools.Sum(item => item.Selections.Length),
            "The Alchemy live scenario selected the wrong number of pools.");
        if (solanaPool != null)
        {
            await solana.StartAsync(solanaConfiguration, credential, [solanaPool],
                webSocketPoolAddresses: webSocket
                    ? new HashSet<string>(StringComparer.Ordinal) { solanaPoolId }
                    : new HashSet<string>(StringComparer.Ordinal));
        }
        foreach (var (chain, providerType, selections) in pools)
        {
            var preset = OnChainProviderCatalog.Get(providerType);
            var profile = CreateProviderConfiguration(providerType,
                preset.DefaultStreamEndpoint, preset.DefaultRpcEndpoint);
            profile.CapabilitySnapshot = new OnChainProviderCapabilitySnapshot
            {
                ObservedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ChainId = OnChainProviderCapabilityState.Supported,
                SafeBlock = OnChainProviderCapabilityState.Supported,
                FinalizedBlock = OnChainProviderCapabilityState.Supported,
                BlockHashCall = OnChainProviderCapabilityState.Supported,
                BlockHashLogs = OnChainProviderCapabilityState.Supported,
                WebSocketLogs = OnChainProviderCapabilityState.Supported
            };
            await evm.StartAsync(chain.ChainId, profile, credential, selections,
                webSocketPoolIds: webSocket
                    ? selections.Select(static item => item.Descriptor.PoolKey.PoolId)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
        var selected = pools.SelectMany(item => item.Selections).ToArray();
        try
        {
            await WaitUntilAsync(
            () => (!includeSolana || solana.State == OnChainRecoveryState.Live)
                  && (pools.Count == 0 || evm.GetStateFor(pools.Select(item => item.Chain.ChainId))
                      == OnChainRecoveryState.Live)
                  && (!includeSolana || prices.Any(price => price.PoolKey.PoolAddress == solanaPoolId
                      && price.PriceUsd != null)
                      )
                  && selected.All(selection => prices.Any(price =>
                      price.PoolKey.ChainId == selection.Descriptor.PoolKey.ChainId
                      && price.PoolKey.PoolId == selection.Descriptor.PoolKey.PoolId
                      && price.PriceUsd != null)),
            TimeSpan.FromSeconds(30), $"Alchemy {scenario} live prices");
        }
        catch (TimeoutException)
        {
            Console.WriteLine($"ALCHEMY MATRIX TIMEOUT | scenario={scenario} | solanaState={solana.State} | evmStates={string.Join(',', pools.Select(item => $"{item.Chain.CatalogChainId}:{evm.Get(item.Chain.ChainId).State}"))} | prices={prices.Count} | solanaUsd={prices.Count(price => price.PoolKey.PoolAddress == solanaPoolId && price.PriceUsd != null)} | evmUsd={prices.Count(price => price.PoolKey.ChainNamespace == ChainNamespaces.Eip155 && price.PriceUsd != null)} | logs={logs.Count} | errors={string.Join(" | ", errors.Take(4))}");
            throw;
        }
        var engineId = GetSingleNewEngineProcessId(baselineProcessIds);
        Console.WriteLine($"ALCHEMY MATRIX START | scenario={scenario} | mode={(webSocket ? "websocket" : "poll-1s")} | utc={DateTimeOffset.UtcNow:O} | engine={engineId} | prices={prices.Count} | logs={logs.Count}");
        await Task.Delay(observationWindow);
        var solanaSampledPrices = prices.Count(price =>
            price.PoolKey.PoolAddress == solanaPoolId
            && price.SourceId.StartsWith("rpcSample:", StringComparison.Ordinal));
        var stockUsdPrices = prices.Count(price =>
            price.ReferenceSourceId?.StartsWith(
                "robinhood-stock-token-api:GLD", StringComparison.Ordinal) == true);
        var logsByChain = string.Join(',', logs.GroupBy(static log => log.ChainId)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(static group => $"{group.Key}:{group.Count()}"));
        Console.WriteLine($"ALCHEMY MATRIX END | scenario={scenario} | mode={(webSocket ? "websocket" : "poll-1s")} | utc={DateTimeOffset.UtcNow:O} | prices={prices.Count} | logs={logs.Count} | logsByChain={logsByChain} | solanaSampledPrices={solanaSampledPrices} | stockUsdPrices={stockUsdPrices} | errors={errors.Count}");
        AssertEqual(engineId, GetSingleNewEngineProcessId(baselineProcessIds),
            "The Alchemy live selection restarted the shared engine.");
        if (includeSolana && !webSocket)
        {
            Assert(solanaSampledPrices > 0,
                "The live Solana selection never delivered a sampled USD price.");
        }
        if (scenario == "5-stock")
        {
            Assert(stockUsdPrices > 0,
                "The live Alchemy stock-quoted pool never received official GLD USD conversion.");
        }
        AssertEqual(0, errors.Count,
            "A live Alchemy stream reported a provider error during the observation window.");
    }
    finally
    {
        await evm.StopAsync();
        await solana.StopAsync();
        await client.StopAsync();
        if (Directory.Exists(checkpointRoot))
        {
            Directory.Delete(checkpointRoot, true);
        }
    }
    await WaitUntilAsync(() => !GetEngineProcessIds().Except(baselineProcessIds).Any(),
        TimeSpan.FromSeconds(10), "Alchemy mixed-chain engine stop");
}

static async Task VerifyLiveMixedMatrixAsync(string enginePath)
{
    const string schiffy = "0x42aFA2124ca5a2B83898E46B2dA9a190995b1E18";
    const string stockUsdPoolId =
        "0xf66ccb1c5e6b579f94311131724a8daf0d96b0ebdbe53ab9fa27b98a8abaeb8b";
    const string stockGldPoolId =
        "0xc749412e31087a6e6f9210af575bc9100159fbc9f45da3ca8b26b31f3bf5777e";
    using var discoveryHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    using var catalogHttp = new HttpClient
    {
        BaseAddress = new Uri("https://api.dexscreener.com/"),
        Timeout = TimeSpan.FromSeconds(30)
    };
    var discoveryConfiguration = CreateProviderConfiguration(
        OnChainProviderTypes.CustomRobinhood, "wss://unused.invalid",
        RobinhoodDeploymentRegistry.PublicRpcEndpoint);
    var catalog = await new DexScreenerPoolCatalogClient(catalogHttp)
        .SearchAsync("robinhood", schiffy);
    var selectedCatalog = catalog.Pools.Where(pool =>
        pool.PoolAddress.Equals(stockUsdPoolId, StringComparison.OrdinalIgnoreCase)
        || pool.PoolAddress.Equals(stockGldPoolId, StringComparison.OrdinalIgnoreCase)).ToArray();
    AssertEqual(2, selectedCatalog.Length,
        "The live SCHIFFY catalog no longer contains its USDG and GLD quote pools.");
    var discovered = await new EthereumPoolDiscoveryService(
            new EvmJsonRpcClient(discoveryHttp, discoveryConfiguration, null),
            EvmChainDefinitions.RobinhoodMainnet,
            RobinhoodDeploymentRegistry.Catalog,
            discoveryHttp)
        .DiscoverCatalogPoolsSharedAsync(schiffy, selectedCatalog, CancellationToken.None);
    var stockUsd = new OnChainWatchedPoolSelection
    {
        SelectedMint = schiffy,
        Descriptor = discovered.Pools.Single(pool => pool.PoolKey.PoolId.Equals(
            stockUsdPoolId, StringComparison.OrdinalIgnoreCase))
    };
    var stockGld = new OnChainWatchedPoolSelection
    {
        SelectedMint = schiffy,
        Descriptor = discovered.Pools.Single(pool => pool.PoolKey.PoolId.Equals(
            stockGldPoolId, StringComparison.OrdinalIgnoreCase))
    };
    Assert(stockUsd.Descriptor.SupportStatus == OnChainSupportStatus.Supported
           && stockGld.Descriptor.SupportStatus == OnChainSupportStatus.Supported
           && stockUsd.Descriptor.QuoteMint.Equals(
               RobinhoodDeploymentRegistry.Usdg, StringComparison.OrdinalIgnoreCase)
           && stockGld.Descriptor.QuoteMint.Equals(
               "0xC9a981FEE1F9DEc688bb123ccDeCc63D0deBFC4e",
               StringComparison.OrdinalIgnoreCase),
        "The live Robinhood stock/USDG and stock/GLD selections lost validated quote identity.");
    var gldReference = await new RobinhoodStockTokenCatalogClient(discoveryHttp)
        .GetUsdReferenceAsync(stockGld.Descriptor.QuoteMint);
    Assert(gldReference?.SourceId == "robinhood-stock-token-api:GLD",
        "The official GLD Stock Token reference was unavailable before the live test.");
    var livePools = new (EvmChainDefinition Chain, string ProviderType, OnChainWatchedPoolSelection Pool)[]
    {
        (EvmChainDefinitions.RobinhoodMainnet, OnChainProviderTypes.PublicNodeRobinhood,
            stockUsd),
        (EvmChainDefinitions.BnbMainnet, OnChainProviderTypes.PublicNodeBnb,
            BnbDeploymentRegistry.CreateBnbUsdReferenceSelection()),
        (EvmChainDefinitions.EthereumMainnet, OnChainProviderTypes.PublicNodeEthereum,
            EthereumDeploymentRegistry.CreateEthUsdReferenceSelection()),
        (EvmChainDefinitions.BaseMainnet, OnChainProviderTypes.BasePublic,
            BaseDeploymentRegistry.CreateEthUsdReferenceSelection())
    };
    var stages = new[] { 1, 2, 4 };
    var solanaMintBytes = Enumerable.Repeat((byte)37, 32).ToArray();
    var solana = CreateMixedMatrixSolanaSelection(solanaMintBytes);
    var checkpointRoot = Path.Combine(Path.GetTempPath(), "TrenchHQLiveMixedCheckpoint",
        Guid.NewGuid().ToString("N"));
    var baselineProcessIds = GetEngineProcessIds();
    var client = new OnChainEngineClient(enginePath);
    var prices = new ConcurrentQueue<OnChainPriceUpdate>();
    var logs = new ConcurrentQueue<EvmLogUpdate>();
    var streamErrors = new ConcurrentQueue<string>();
    using var rpcHttp = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(30)
    };
    client.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
    var coordinators = new EvmStreamCoordinatorCollection(client, checkpointRoot,
        chain => new EvmStreamCoordinator(client, checkpointRoot, chain, rpcHttp,
            sourceFactory: (profile, key) => new ObservingEvmStreamSource(
                new EvmWebSocketStreamSource(profile, key), logs, static () => { })));
    coordinators.StreamError += (sender, message) => streamErrors.Enqueue(
        $"{(sender as EvmStreamCoordinator)?.ChainDefinition.CatalogChainId}: {message}");
    try
    {
        await client.ReplaceWatchedPoolsAsync("solana:mainnet-beta", [solana]);
        var sharedProcessId = GetSingleNewEngineProcessId(baselineProcessIds);
        for (var stageIndex = 0; stageIndex < stages.Length; stageIndex++)
        {
            var active = livePools.Take(stages[stageIndex]).ToArray();
            foreach (var (chain, providerType, pool) in active)
            {
                await coordinators.StartAsync(chain.ChainId,
                    CreateLiveConfiguration(chain, providerType), null, [pool]);
            }
            await client.PublishTransactionUpdateAsync(CreatePumpTrade(
                $"live-matrix-{stageIndex}", solanaMintBytes));
            try
            {
                await WaitUntilAsync(
                    () => coordinators.GetStateFor(active.Select(item => item.Chain.ChainId))
                              == OnChainRecoveryState.Live
                          && prices.Count(update => update.PoolKey.PoolAddress
                              == solana.Descriptor.PoolKey.PoolAddress) >= stageIndex + 1
                          && active.All(item => prices.Any(update =>
                              update.PoolKey.ChainId == item.Chain.ChainId
                              && update.PoolKey.PoolId == item.Pool.Descriptor.PoolKey.PoolId
                              && update.SpotPriceQuote != null && update.PriceUsd != null)),
                    TimeSpan.FromSeconds(90),
                    $"live {stages[stageIndex] + 1}-coin mixed-chain prices");
            }
            catch (TimeoutException)
            {
                Console.WriteLine(
                    $"LIVE MIXED TIMEOUT | stage={stages[stageIndex] + 1} | state={coordinators.State} | prices={prices.Count} | logs={logs.Count} | errors={string.Join(" | ", streamErrors)}");
                throw;
            }
            AssertEqual(sharedProcessId, GetSingleNewEngineProcessId(baselineProcessIds),
                "The live mixed-chain stage restarted the shared engine.");
            Console.WriteLine(
                $"LIVE MIXED MATRIX | coins={stages[stageIndex] + 1} | evmChains={string.Join(',', active.Select(item => item.Chain.CatalogChainId))} | prices={prices.Count} | logs={logs.Count} | engine={sharedProcessId}");
        }
        await coordinators.StopAsync(EvmChainDefinitions.BnbMainnetChainId);
        var stockFilters = JsonSerializer.Serialize(EvmWebSocketStreamSource.CreateRpcLogFilters(
            [stockUsd, stockGld], 1, 2));
        Assert(stockFilters.Contains(stockUsdPoolId, StringComparison.OrdinalIgnoreCase)
               && stockFilters.Contains(stockGldPoolId, StringComparison.OrdinalIgnoreCase)
               && !stockFilters.Contains(
                   RobinhoodDeploymentRegistry.WrappedEtherUsdgReferencePool,
                   StringComparison.OrdinalIgnoreCase),
            "The stock-pair subscription added the unselected WETH/USDG reference.");
        await coordinators.StartAsync(EvmChainDefinitions.RobinhoodMainnetChainId,
            CreateLiveConfiguration(EvmChainDefinitions.RobinhoodMainnet,
                OnChainProviderTypes.PublicNodeRobinhood), null, [stockUsd, stockGld]);
        await client.PublishTransactionUpdateAsync(CreatePumpTrade(
            "live-matrix-5-stock", solanaMintBytes));
        try
        {
            await WaitUntilAsync(
                () => coordinators.GetStateFor(
                          [EvmChainDefinitions.RobinhoodMainnetChainId,
                              EvmChainDefinitions.EthereumMainnetChainId,
                              EvmChainDefinitions.BaseMainnetChainId]) == OnChainRecoveryState.Live
                      && prices.Any(update => update.PoolKey.PoolId.Equals(
                          stockGldPoolId, StringComparison.OrdinalIgnoreCase)
                          && update.PriceUsd != null
                          && update.ReferenceSourceId?.StartsWith(
                              "robinhood-stock-token-api:GLD", StringComparison.Ordinal) == true),
                TimeSpan.FromSeconds(90), "live five-coin stock-quoted USD price");
        }
        catch (TimeoutException)
        {
            var stockUpdates = prices.Where(update => update.PoolKey.PoolId.Equals(
                stockGldPoolId, StringComparison.OrdinalIgnoreCase)).ToArray();
            Console.WriteLine(
                $"LIVE STOCK TIMEOUT | state={coordinators.Get(EvmChainDefinitions.RobinhoodMainnetChainId).State} | stockUpdates={stockUpdates.Length} | latestQuote={stockUpdates.LastOrDefault()?.SpotPriceQuote != null} | latestUsd={stockUpdates.LastOrDefault()?.PriceUsd != null} | latestReference={stockUpdates.LastOrDefault()?.ReferenceSourceId ?? "none"} | errors={string.Join(" | ", streamErrors)}");
            throw;
        }
        AssertEqual(sharedProcessId, GetSingleNewEngineProcessId(baselineProcessIds),
            "The live stock-pair switch restarted the shared engine.");
        Console.WriteLine(
            $"LIVE MIXED MATRIX | coins=5-stock | evmChains=robinhood,ethereum,base | stockQuote=GLD | prices={prices.Count} | logs={logs.Count} | engine={sharedProcessId}");
    }
    finally
    {
        await coordinators.StopAsync();
        await client.ReplaceWatchedPoolsAsync("solana:mainnet-beta", []);
        await client.StopAsync();
        if (Directory.Exists(checkpointRoot))
        {
            Directory.Delete(checkpointRoot, true);
        }
    }
    await WaitUntilAsync(() => !GetEngineProcessIds().Except(baselineProcessIds).Any(),
        TimeSpan.FromSeconds(10), "live mixed-chain engine stop");

    static OnChainProviderConfiguration CreateLiveConfiguration(
        EvmChainDefinition chain, string providerType)
    {
        var preset = OnChainProviderCatalog.Get(providerType);
        var configuration = CreateProviderConfiguration(providerType,
            preset.DefaultStreamEndpoint, preset.DefaultRpcEndpoint);
        configuration.Id = $"live-matrix-{chain.ChainId}";
        configuration.CapabilitySnapshot = new OnChainProviderCapabilitySnapshot
        {
            ObservedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ChainId = OnChainProviderCapabilityState.Supported,
            SafeBlock = OnChainProviderCapabilityState.Supported,
            FinalizedBlock = OnChainProviderCapabilityState.Supported,
            BlockHashCall = OnChainProviderCapabilityState.Supported,
            BlockHashLogs = OnChainProviderCapabilityState.Supported,
            WebSocketLogs = OnChainProviderCapabilityState.Supported
        };
        return configuration;
    }
}

static async Task VerifyLiveEvmStreamsAsync(string enginePath)
{
    const string selectedMint = "0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2";
    var expectedPools = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [OnChainProtocolIds.UniswapV2] = "0xb4e16d0168e52d35cacd2c6185b44281ec28c9dc",
        [OnChainProtocolIds.UniswapV3] = "0x88e6a0c2ddd26feeb64f039a2c41296fcb3f5640"
    };
    using var catalogHttp = new HttpClient
    {
        BaseAddress = new Uri("https://api.dexscreener.com/"),
        Timeout = TimeSpan.FromSeconds(30)
    };
    using var rpcHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var discoveryConfiguration = CreateProviderConfiguration(
        OnChainProviderTypes.CustomEthereum,
        "wss://eth-mainnet.public.blastapi.io",
        "https://eth-mainnet.public.blastapi.io");
    var discoveryRpc = new EvmJsonRpcClient(rpcHttp, discoveryConfiguration, null);
    var catalog = await new DexScreenerPoolCatalogClient(catalogHttp)
        .SearchAsync("ethereum", selectedMint);
    var selectedCatalog = catalog.Pools
        .Where(pool => expectedPools.Values.Contains(
                           pool.PoolAddress,
                           StringComparer.OrdinalIgnoreCase)
                       || pool.ProtocolId.Equals("uniswap", StringComparison.OrdinalIgnoreCase)
                       && pool.Labels.Contains("v4", StringComparer.OrdinalIgnoreCase))
        .ToArray();
    Assert(expectedPools.Values.All(poolId => selectedCatalog.Any(pool =>
            pool.PoolAddress.Equals(poolId, StringComparison.OrdinalIgnoreCase))),
        "The live catalog did not return both canonical V2/V3 smoke pools.");
    var discoveryService = new EthereumPoolDiscoveryService(discoveryRpc, rpcHttp);
    var discovery = await RetryLiveTransientResultAsync(
        async () =>
        {
            var candidate = await discoveryService.DiscoverCatalogPoolsSharedAsync(
                selectedMint,
                selectedCatalog,
                CancellationToken.None);
            if (candidate.Pools.Any(pool =>
                    pool.SupportReason?.Contains("HTTP 5", StringComparison.Ordinal) == true))
            {
                throw new EvmJsonRpcException(
                    EvmRpcFailureKind.RpcError,
                    "The key-free Ethereum endpoint returned a transient server failure.");
            }
            return candidate;
        },
        "key-free Ethereum live pool discovery");
    foreach (var catalogPool in selectedCatalog.Where(pool =>
                 pool.Labels.Contains("v4", StringComparer.OrdinalIgnoreCase)))
    {
        var descriptor = discovery.Pools.FirstOrDefault(pool =>
            pool.PoolKey.PoolId.Equals(catalogPool.PoolAddress, StringComparison.OrdinalIgnoreCase));
        Console.WriteLine(
            $"LIVE V4 CANDIDATE | {catalogPool.PoolAddress} | liquidityUsd={catalogPool.LiquidityUsd} | {descriptor?.SupportStatus.ToString() ?? "missing"} | hook={descriptor?.HookAddress ?? "unknown"} | {descriptor?.SupportReason ?? "validated"}");
    }
    var supportedV4Pool = selectedCatalog
        .Where(pool => pool.Labels.Contains("v4", StringComparer.OrdinalIgnoreCase))
        .OrderByDescending(static pool => pool.LiquidityUsd ?? double.MinValue)
        .Select(catalogPool => discovery.Pools.FirstOrDefault(pool =>
            pool.PoolKey.ProtocolId == OnChainProtocolIds.UniswapV4
            && pool.PoolKey.PoolId.Equals(
                catalogPool.PoolAddress,
                StringComparison.OrdinalIgnoreCase)))
        .FirstOrDefault(static pool => pool?.SupportStatus == OnChainSupportStatus.Supported);
    Assert(supportedV4Pool != null,
        "The live WETH catalog did not contain a supported Uniswap V4 pool.");
    expectedPools[OnChainProtocolIds.UniswapV4] = supportedV4Pool!.PoolKey.PoolId;
    var selections = expectedPools
        .Select(expected => new OnChainWatchedPoolSelection
        {
            SelectedMint = selectedMint,
            Descriptor = discovery.Pools.Single(pool =>
                pool.PoolKey.ProtocolId == expected.Key
                && pool.PoolKey.PoolId.Equals(expected.Value, StringComparison.OrdinalIgnoreCase))
        })
        .ToArray();
    foreach (var selection in selections)
    {
        Console.WriteLine(
            $"LIVE SELECT | {selection.Descriptor.PoolKey.ProtocolId} | {selection.Descriptor.PoolKey.PoolId} | {selection.Descriptor.SupportStatus} | hook={selection.Descriptor.HookAddress ?? "n/a"} | {selection.Descriptor.SupportReason ?? "validated"}");
    }
    Assert(selections.All(selection =>
            selection.Descriptor.SupportStatus == OnChainSupportStatus.Supported),
        "At least one live V2/V3/V4 smoke pool failed on-chain validation.");

    var configuration = CreateProviderConfiguration(
        OnChainProviderTypes.PublicNodeEthereum,
        "wss://ethereum-rpc.publicnode.com",
        "https://ethereum-rpc.publicnode.com");
    configuration.Id = "live-publicnode-ethereum";
    configuration.CapabilitySnapshot = new OnChainProviderCapabilitySnapshot
    {
        ObservedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        ChainId = OnChainProviderCapabilityState.Supported,
        SafeBlock = OnChainProviderCapabilityState.Supported,
        FinalizedBlock = OnChainProviderCapabilityState.Supported,
        BlockHashCall = OnChainProviderCapabilityState.Supported,
        BlockHashLogs = OnChainProviderCapabilityState.Supported,
        WebSocketHeads = OnChainProviderCapabilityState.Supported,
        WebSocketLogs = OnChainProviderCapabilityState.Supported
    };
    var rpc = new EvmJsonRpcClient(rpcHttp, configuration, null);
    var activeStreamProtocols = new HashSet<string>(StringComparer.Ordinal)
    {
        OnChainProtocolIds.UniswapV2,
        OnChainProtocolIds.UniswapV3
    };

    var checkpointRoot = Path.Combine(
        Path.GetTempPath(),
        "TrenchHQLiveEvmCheckpoint",
        Guid.NewGuid().ToString("N"));
    var baselineProcessIds = GetEngineProcessIds();
    var prices = new ConcurrentQueue<OnChainPriceUpdate>();
    var sourceLogs = new ConcurrentQueue<EvmLogUpdate>();
    var sourceRunCount = 0;
    var client = new OnChainEngineClient(enginePath);
    client.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
    var coordinator = new EvmStreamCoordinator(
        client,
        checkpointRoot,
        EvmChainDefinitions.EthereumMainnet,
        rpcHttp,
        (profile, apiKey) => new ObservingEvmStreamSource(
            new EvmWebSocketStreamSource(profile, apiKey),
            sourceLogs,
            () => Interlocked.Increment(ref sourceRunCount)));
    try
    {
        var streamStartedAfter = (await rpc.GetBlockAsync("latest", CancellationToken.None)).Number;
        await coordinator.StartAsync(configuration, null, selections);
        await WaitUntilAsync(
            () => coordinator.State == OnChainRecoveryState.Live
                  && expectedPools.Where(expected => activeStreamProtocols.Contains(expected.Key)).All(expected =>
                      sourceLogs.Any(log => LiveLogMatchesPool(log, expected.Key, expected.Value)
                                            && log.BlockNumber > streamStartedAfter)
                      && prices.Any(update =>
                          update.PoolKey.ProtocolId == expected.Key
                          && update.PoolKey.PoolId.Equals(
                              expected.Value,
                              StringComparison.OrdinalIgnoreCase)
                          && update.ChainPosition?.BlockNumber > streamStartedAfter
                          && update.LastTradeEventId != null)),
            TimeSpan.FromMinutes(4),
            "natural PublicNode V2/V3 WebSocket prices while V4 remains subscribed");
        await WaitUntilAsync(
            () => GetEngineProcessIds().Except(baselineProcessIds).Count() == 1,
            TimeSpan.FromSeconds(5),
            "one shared engine during the live EVM smoke");
        var firstEngineProcessId = GetSingleNewEngineProcessId(baselineProcessIds);
        foreach (var expected in expectedPools.Where(expected => activeStreamProtocols.Contains(expected.Key)))
        {
            var update = prices.Last(item =>
                item.PoolKey.ProtocolId == expected.Key
                && item.PoolKey.PoolId.Equals(expected.Value, StringComparison.OrdinalIgnoreCase)
                && item.ChainPosition?.BlockNumber > streamStartedAfter
                && item.LastTradeEventId != null);
            Console.WriteLine(
                $"LIVE STREAM | {expected.Key} | block={update.ChainPosition?.BlockNumber} | event={update.LastTradeEventId} | source={update.SourceId}");
        }
        var checkpoint = await EvmCheckpointStore.LoadAsync(checkpointRoot, configuration.Id);
        Assert(checkpoint?.SafeBlockNumber > 0,
            "The live EVM coordinator did not persist a safe checkpoint.");

        await coordinator.StopAsync();
        await WaitUntilAsync(
            () => !GetEngineProcessIds().Except(baselineProcessIds).Any(),
            TimeSpan.FromSeconds(10),
            "live EVM engine shutdown before restart");
        var restartStartedAfter = (await rpc.GetBlockAsync("latest", CancellationToken.None)).Number;
        await coordinator.StartAsync(configuration, null, selections);
        await WaitUntilAsync(
            () => coordinator.State == OnChainRecoveryState.Live
                  && Volatile.Read(ref sourceRunCount) >= 2
                  && prices.Any(update =>
                      update.ChainPosition?.BlockNumber > restartStartedAfter
                      && update.LastTradeEventId != null),
            TimeSpan.FromMinutes(3),
            "live EVM checkpoint restart and post-restart WebSocket price");
        var secondEngineProcessId = GetSingleNewEngineProcessId(baselineProcessIds);
        Assert(firstEngineProcessId != secondEngineProcessId,
            "The stopped EVM engine process was reused during restart validation.");
        Console.WriteLine(
            $"LIVE RESTART | safe={checkpoint!.SafeBlockNumber} | engine={firstEngineProcessId}->{secondEngineProcessId} | sourceRuns={sourceRunCount}");
    }
    finally
    {
        await coordinator.StopAsync();
        await client.StopAsync();
        if (Directory.Exists(checkpointRoot))
        {
            Directory.Delete(checkpointRoot, true);
        }
    }
    await WaitUntilAsync(
        () => !GetEngineProcessIds().Except(baselineProcessIds).Any(),
        TimeSpan.FromSeconds(10),
        "live EVM engine cleanup");
}

static async Task VerifyLiveBaseStreamAsync(string enginePath)
{
    const string poolId = "0xb2cc224c1c9fee385f8ad6a55b4d94e92359dc59";
    var selection = new OnChainWatchedPoolSelection
    {
        SelectedMint = BaseDeploymentRegistry.WrappedEther,
        Descriptor = new OnChainPoolDescriptor
        {
            PoolKey = new OnChainPoolKey
            {
                DeploymentKey = BaseDeploymentRegistry.Catalog.Deployment(
                    OnChainProtocolIds.AerodromeSlipstream,
                    BaseDeploymentRegistry.AerodromeSlipstreamInitialFactory),
                PoolId = poolId
            },
            PoolType = "concentratedLiquidity",
            ProgramId = BaseDeploymentRegistry.AerodromeSlipstreamInitialFactory,
            BaseMint = BaseDeploymentRegistry.WrappedEther,
            QuoteMint = BaseDeploymentRegistry.Usdc,
            BaseDecimals = 18,
            QuoteDecimals = 6,
            PairOrientation = "selectedAsToken0",
            DiscoverySource = "liveSmoke:fixedBaseSlipstreamInitial",
            SupportStatus = OnChainSupportStatus.Supported,
            TickSpacing = 100,
            Asset0 = new OnChainAssetKey
            {
                ChainNamespace = ChainNamespaces.Eip155,
                ChainId = EvmChainDefinitions.BaseMainnetChainId,
                Address = BaseDeploymentRegistry.WrappedEther
            },
            Asset1 = new OnChainAssetKey
            {
                ChainNamespace = ChainNamespaces.Eip155,
                ChainId = EvmChainDefinitions.BaseMainnetChainId,
                Address = BaseDeploymentRegistry.Usdc
            }
        }
    };
    var configuration = CreateProviderConfiguration(
        OnChainProviderTypes.BasePublic,
        "wss://base-rpc.publicnode.com",
        "https://base-rpc.publicnode.com");
    configuration.Id = "live-publicnode-base";
    configuration.CapabilitySnapshot = new OnChainProviderCapabilitySnapshot
    {
        ObservedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        ChainId = OnChainProviderCapabilityState.Supported,
        SafeBlock = OnChainProviderCapabilityState.Supported,
        FinalizedBlock = OnChainProviderCapabilityState.Supported,
        BlockHashCall = OnChainProviderCapabilityState.Supported,
        BlockHashLogs = OnChainProviderCapabilityState.Supported,
        WebSocketHeads = OnChainProviderCapabilityState.Supported,
        WebSocketLogs = OnChainProviderCapabilityState.Supported
    };

    using var rpcHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var rpc = new EvmJsonRpcClient(rpcHttp, configuration, null);
    var checkpointRoot = Path.Combine(
        Path.GetTempPath(),
        "TrenchHQLiveBaseCheckpoint",
        Guid.NewGuid().ToString("N"));
    var baselineProcessIds = GetEngineProcessIds();
    var prices = new ConcurrentQueue<OnChainPriceUpdate>();
    var sourceLogs = new ConcurrentQueue<EvmLogUpdate>();
    var streamErrors = new ConcurrentQueue<string>();
    var client = new OnChainEngineClient(enginePath);
    client.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
    var coordinator = new EvmStreamCoordinator(
        client,
        checkpointRoot,
        EvmChainDefinitions.BaseMainnet,
        rpcHttp,
        (profile, apiKey) => new ObservingEvmStreamSource(
            new EvmWebSocketStreamSource(profile, apiKey),
            sourceLogs,
            static () => { }));
    coordinator.StreamError += (_, message) => streamErrors.Enqueue(message);
    try
    {
        var streamStartedAfter = (await rpc.GetBlockAsync("latest", CancellationToken.None)).Number;
        await coordinator.StartAsync(configuration, null, [selection]);
        try
        {
            await WaitUntilAsync(
                () => coordinator.State == OnChainRecoveryState.Live
                      && sourceLogs.Any(log =>
                          log.Address.Equals(poolId, StringComparison.OrdinalIgnoreCase)
                          && log.BlockNumber > streamStartedAfter)
                      && prices.Any(update =>
                          update.PoolKey.ChainId == EvmChainDefinitions.BaseMainnetChainId
                          && update.PoolKey.PoolId.Equals(poolId, StringComparison.OrdinalIgnoreCase)
                          && update.ChainPosition?.BlockNumber > streamStartedAfter
                          && update.LastTradeEventId != null),
                TimeSpan.FromMinutes(2),
                "natural Base Slipstream WebSocket price");
        }
        catch (TimeoutException)
        {
            Console.WriteLine(
                $"LIVE BASE TIMEOUT | state={coordinator.State} | logs={sourceLogs.Count} | prices={prices.Count} | errors={string.Join(" | ", streamErrors)}");
            throw;
        }
        var live = prices.Last(update =>
            update.PoolKey.PoolId.Equals(poolId, StringComparison.OrdinalIgnoreCase)
            && update.ChainPosition?.BlockNumber > streamStartedAfter
            && update.LastTradeEventId != null);
        Assert(live.LastTradePriceQuote != null && live.SpotPriceQuote != null,
            "The live Base Slipstream update omitted trade or spot price.");
        Assert(live.SourceId.Contains("base-json-rpc:basePublic", StringComparison.Ordinal),
            "The live Base update lost chain/provider attribution.");
        AssertEqual(1, GetEngineProcessIds().Except(baselineProcessIds).Count(),
            "The live Base smoke did not retain exactly one engine process.");
        var checkpoint = await EvmCheckpointStore.LoadAsync(checkpointRoot, configuration.Id);
        Assert(checkpoint?.ChainId == EvmChainDefinitions.BaseMainnetChainId
               && checkpoint.SafeBlockNumber > 0,
            "The live Base coordinator did not persist a Base safe checkpoint.");
        Console.WriteLine(
            $"LIVE BASE | {selection.Descriptor.PoolKey.ProtocolId} | block={live.ChainPosition?.BlockNumber} | event={live.LastTradeEventId} | source={live.SourceId} | errors={streamErrors.Count}"
            + (streamErrors.IsEmpty ? string.Empty : $" | {string.Join(" | ", streamErrors)}"));
    }
    finally
    {
        await coordinator.StopAsync();
        await client.StopAsync();
        if (Directory.Exists(checkpointRoot))
        {
            Directory.Delete(checkpointRoot, true);
        }
    }
    await WaitUntilAsync(
        () => !GetEngineProcessIds().Except(baselineProcessIds).Any(),
        TimeSpan.FromSeconds(10),
        "live Base engine cleanup");
}

static bool LiveLogMatchesPool(EvmLogUpdate log, string protocolId, string poolId)
{
    return protocolId == OnChainProtocolIds.UniswapV4
        ? log.Address.Equals(
              EthereumDeploymentRegistry.UniswapV4PoolManager,
              StringComparison.OrdinalIgnoreCase)
          && log.Topics.Length > 1
          && log.Topics[1].Equals(poolId, StringComparison.OrdinalIgnoreCase)
        : log.Address.Equals(poolId, StringComparison.OrdinalIgnoreCase);
}

static void AssertDecimalValue(OnChainDecimalValue? value, int expected, string message)
{
    if (value == null
        || !System.Numerics.BigInteger.TryParse(value.Coefficient, out var coefficient)
        || coefficient != new System.Numerics.BigInteger(expected)
                          * System.Numerics.BigInteger.Pow(10, checked((int)value.Scale)))
    {
        throw new InvalidOperationException(
            $"{message} Actual: {value?.Coefficient ?? "null"}e-{value?.Scale.ToString() ?? "null"}.");
    }
}

static string AbiWords(params ulong[] values)
{
    return "0x" + string.Concat(values.Select(static value => $"{value:x64}"));
}

static string AbiBigWords(params System.Numerics.BigInteger[] values)
{
    var modulus = System.Numerics.BigInteger.One << 256;
    return "0x" + string.Concat(values.Select(value =>
    {
        var encoded = value.Sign < 0 ? modulus + value : value;
        return encoded.ToString("x").TrimStart('0').PadLeft(64, '0');
    }));
}

static EvmHeadUpdate CreateEvmHead(ulong number, string hash, string parentHash)
{
    return new EvmHeadUpdate
    {
        ChainId = "1",
        ConnectionEpoch = 1,
        Number = number,
        Hash = hash,
        ParentHash = parentHash,
        Timestamp = number,
        ObservedAtUnixMs = (long)number
    };
}

static EvmLogUpdate CreateEvmLog(
    ulong blockNumber,
    string blockHash,
    string transactionHash,
    ulong logIndex)
{
    return new EvmLogUpdate
    {
        ChainId = "1",
        ConnectionEpoch = 1,
        Address = "0x88e6a0c2ddd26feeb64f039a2c41296fcb3f5640",
        Topics = [EvmWebSocketStreamSource.UniswapV3SwapTopic],
        Data = "0x00",
        BlockNumber = blockNumber,
        BlockHash = blockHash,
        TransactionHash = transactionHash,
        TransactionIndex = 0,
        LogIndex = logIndex,
        ObservedAtUnixMs = (long)blockNumber
    };
}

static string Hash(ulong value) => $"0x{value:x64}";

static async Task VerifyRpcAuthenticationAsync(
    OnChainProviderConfiguration configuration,
    string apiKey,
    string expectedPath,
    string expectedQuery)
{
    using var handler = new RpcAuthenticationFixtureHandler();
    using var httpClient = new HttpClient(handler);
    var rpc = new SolanaRpcClient(httpClient, configuration, apiKey);
    AssertEqual(42UL, await rpc.GetSlotAsync(OnChainCommitment.Confirmed, CancellationToken.None),
        $"{configuration.ProviderType} RPC fixture returned the wrong slot.");
    AssertEqual(expectedPath, handler.RequestUri?.AbsolutePath,
        $"{configuration.ProviderType} RPC authentication used the wrong URL path.");
    AssertEqual(expectedQuery, handler.RequestUri?.Query,
        $"{configuration.ProviderType} RPC authentication used the wrong query string.");
}

static async Task VerifyStandardWebSocketTransactionFetchAsync()
{
    var outerData = new byte[] { 1, 2, 3, 4 };
    var innerData = new byte[] { 5, 6, 7, 8 };
    AssertEqual(Convert.ToHexString(outerData), Convert.ToHexString(SolanaBase58.Decode(EncodeBase58(outerData))),
        "The production base58 decoder did not round-trip instruction bytes.");
    try
    {
        SolanaBase58.Decode("0");
        throw new InvalidOperationException("Invalid base58 input was accepted.");
    }
    catch (FormatException)
    {
    }

    using var handler = new TransactionRpcFixtureHandler(outerData, innerData);
    using var httpClient = new HttpClient(handler);
    var configuration = CreateProviderConfiguration(
        OnChainProviderTypes.AlchemyWebSocket,
        "wss://solana-mainnet.streaming.alchemy.com/v2",
        "https://solana-mainnet.g.alchemy.com/v2");
    var rpc = new SolanaRpcClient(httpClient, configuration, "free-tier-key");
    var transaction = await rpc.GetTransactionAsync(
        "fixture-signature",
        OnChainCommitment.Processed,
        "websocket-fixture",
        1234,
        CancellationToken.None);
    Assert(transaction != null, "The standard-WebSocket transaction lookup returned no transaction.");
    AssertEqual(OnChainCommitment.Confirmed, transaction!.Commitment,
        "A processed WebSocket log was not safely fetched at confirmed commitment.");
    AssertEqual(2, transaction.Instructions.Length,
        "The standard RPC transaction path did not preserve outer and inner instructions.");
    AssertEqual(Convert.ToBase64String(outerData), transaction.Instructions[0].DataBase64,
        "Outer instruction data was not converted from Solana base58 correctly.");
    AssertEqual(Convert.ToBase64String(innerData), transaction.Instructions[1].DataBase64,
        "Inner instruction data was not converted from Solana base58 correctly.");
    AssertEqual("loaded-program", transaction.Instructions[1].ProgramId,
        "A loaded-address program index was not resolved.");
    AssertEqual((ushort)0, transaction.Instructions[1].OuterInstructionIndex,
        "The inner instruction parent index changed.");
    AssertEqual((ushort)0, transaction.Instructions[1].InnerInstructionIndex,
        "The inner instruction index changed.");
    AssertEqual(2U, transaction.Instructions[1].StackHeight,
        "The standard RPC path lost CPI stack height.");
    AssertEqual("base-vault", transaction.Instructions[0].AccountAddresses[0],
        "The standard RPC path did not resolve an instruction account index.");
    AssertEqual("quote-vault", transaction.Instructions[0].AccountAddresses[1],
        "The standard RPC path did not resolve a loaded instruction account index.");
    AssertEqual(2, transaction.TokenBalances.Length,
        "The standard RPC path did not normalize changed vault balances.");
    AssertEqual(3, transaction.ProgramData.Length,
        "The standard RPC path did not preserve program-attributed log data.");
    AssertEqual("outer-program", transaction.ProgramData[0].ProgramId,
        "The outer program-data payload was attributed incorrectly.");
    AssertEqual("loaded-program", transaction.ProgramData[1].ProgramId,
        "The nested program-data payload was attributed incorrectly.");
    AssertEqual(3U, transaction.ProgramData[1].LogIndex,
        "The nested program-data log index changed.");
    AssertEqual("outer-program", transaction.ProgramData[2].ProgramId,
        "The outer program was not restored after its CPI completed.");
    AssertEqual(900UL,
        transaction.TokenBalances.Single(balance => balance.AccountAddress == "base-vault").PostAmountRaw,
        "The standard RPC path returned the wrong post-swap base-vault amount.");
    AssertEqual(2200UL,
        transaction.TokenBalances.Single(balance => balance.AccountAddress == "quote-vault").PostAmountRaw,
        "The standard RPC path returned the wrong post-swap quote-vault amount.");
    AssertEqual("/v2/free-tier-key", handler.RequestUri?.AbsolutePath,
        "The WebSocket provider's HTTP transaction lookup did not authenticate transiently.");

    _ = new SolanaWebSocketStreamSource(configuration, "free-tier-key");
    try
    {
        _ = new SolanaWebSocketStreamSource(
            CreateProviderConfiguration(
                OnChainProviderTypes.AlchemyYellowstone,
                "https://solana-mainnet.streaming.alchemy.com",
                "https://solana-mainnet.g.alchemy.com/v2"),
            "key");
        throw new InvalidOperationException("A Yellowstone configuration was accepted by the WebSocket source.");
    }
    catch (ArgumentException)
    {
    }
}

static async Task VerifySolanaWalletTransactionFetchAsync()
{
    const string walletAddress = "11111111111111111111111111111111";
    using var handler = new WalletTransactionRpcFixtureHandler(walletAddress);
    using var httpClient = new HttpClient(handler);
    var configuration = CreateProviderConfiguration(
        OnChainProviderTypes.AlchemyWebSocket,
        "wss://solana-mainnet.streaming.alchemy.com/v2",
        "https://solana-mainnet.g.alchemy.com/v2");
    var rpc = new SolanaRpcClient(httpClient, configuration, "free-tier-key");
    var transaction = await rpc.GetWalletTransactionAsync(
        "wallet-fixture-signature",
        CancellationToken.None);
    Assert(transaction != null, "The Solana wallet transaction lookup returned no transaction.");
    AssertEqual(3, transaction!.AccountAddresses.Length,
        "The Solana wallet transaction lost account balance positions.");
    AssertEqual(2, transaction.TokenBalances.Length,
        "The Solana wallet transaction did not retain created and existing token accounts.");
    AssertEqual(9, transaction.TokenBalances.Single(balance =>
        balance.Mint == "TokenMint111111111111111111111111111111111").Decimals,
        "The Solana wallet transaction lost token decimals.");

    var updates = WalletActivityRules.ParseSolanaTransaction(
        transaction,
        new SavedTrackedWallet
        {
            ChainNamespace = ChainNamespaces.Solana,
            ChainId = "mainnet-beta",
            Address = walletAddress,
            Label = "Trader"
        },
        "confirmed",
        1700000000000,
        "wallet-fixture");
    var presentation = WalletActivityPresentationRules.Build(updates);
    AssertEqual(WalletActivityDisplayKind.Buy, presentation.Kind,
        "The parsed Solana wallet deltas did not classify the fixture trade as a buy.");
    AssertEqual("12.5 USDC → 2 TokenM…1111", presentation.Detail,
        "The parsed Solana wallet deltas produced the wrong trade flow.");
    Assert(!updates.Any(static update => update.AssetAddress == null),
        "The Solana fee and new-account rent were exposed as a false SOL movement.");
    Assert(handler.RequestBody.Contains("getTransaction", StringComparison.Ordinal)
           && handler.RequestBody.Contains("maxSupportedTransactionVersion", StringComparison.Ordinal),
        "The wallet lookup did not request a bounded versioned transaction response.");
}

static async Task VerifyWalletSignatureHistoryAsync()
{
    using var handler = new WalletSignatureRpcFixtureHandler();
    using var httpClient = new HttpClient(handler);
    var configuration = CreateProviderConfiguration(
        OnChainProviderTypes.AlchemyWebSocket,
        "wss://solana-mainnet.streaming.alchemy.com/v2",
        "https://solana-mainnet.g.alchemy.com/v2");
    var rpc = new SolanaRpcClient(httpClient, configuration, "free-tier-key");

    var signatures = await rpc.GetSignaturesForAddressAsync(
        "11111111111111111111111111111111",
        "previous-signature",
        10,
        CancellationToken.None);

    AssertEqual(2, signatures.Count, "Wallet signature recovery returned the wrong number of entries.");
    AssertEqual("new-signature", signatures[0].Signature,
        "Wallet signature recovery changed newest-first RPC ordering.");
    Assert(!signatures[0].Failed, "A successful wallet transaction was marked failed.");
    Assert(signatures[1].Failed, "A failed wallet transaction was not preserved.");
    Assert(handler.RequestBody.Contains("getSignaturesForAddress", StringComparison.Ordinal)
           && handler.RequestBody.Contains("previous-signature", StringComparison.Ordinal),
        "Wallet signature recovery did not send the expected bounded until cursor.");
}

static async Task VerifyWalletCompletionRpcMethodsAsync()
{
    using var solanaHandler = new WalletCompletionSolanaRpcFixtureHandler();
    using var solanaHttpClient = new HttpClient(solanaHandler);
    var solanaConfiguration = CreateProviderConfiguration(
        OnChainProviderTypes.AlchemyWebSocket,
        "wss://solana-mainnet.streaming.alchemy.com/v2",
        "https://solana-mainnet.g.alchemy.com/v2");
    var solanaRpc = new SolanaRpcClient(solanaHttpClient, solanaConfiguration, "free-tier-key");
    var tokenAccounts = await solanaRpc.GetTokenAccountAddressesByOwnerAsync(
        "11111111111111111111111111111111",
        "TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA",
        OnChainCommitment.Confirmed,
        CancellationToken.None);
    AssertEqual(2, tokenAccounts.Count,
        "Solana wallet token-account discovery returned the wrong count.");
    var statuses = await solanaRpc.GetSignatureStatusesAsync(
        ["processed-signature", "finalized-signature"],
        CancellationToken.None);
    AssertEqual("confirmed", statuses[0]?.Confirmation,
        "Solana signature status did not preserve confirmation progression.");
    AssertEqual("finalized", statuses[1]?.Confirmation,
        "Solana signature status did not preserve finality.");

    using var evmHandler = new WalletCompletionEvmRpcFixtureHandler();
    using var evmHttpClient = new HttpClient(evmHandler);
    var evmConfiguration = CreateProviderConfiguration(
        OnChainProviderTypes.AlchemyBase,
        "wss://base-mainnet.g.alchemy.com/v2",
        "https://base-mainnet.g.alchemy.com/v2");
    var evmRpc = new EvmJsonRpcClient(evmHttpClient, evmConfiguration, "free-tier-key");
    var block = await evmRpc.GetWalletBlockAsync("0xc8", CancellationToken.None);
    AssertEqual(200UL, block.Number, "EVM wallet block parsing changed its block number.");
    AssertEqual(2, block.Transactions.Length,
        "EVM wallet block parsing did not return full transactions.");
    var traces = await evmRpc.GetAddressTracesAsync(
        199,
        200,
        ["0x1111111111111111111111111111111111111111"],
        true,
        CancellationToken.None);
    AssertEqual(1, traces.Count, "EVM internal trace parsing returned the wrong count.");
    AssertEqual("0.1", traces[0].TracePath, "EVM internal trace identity was not stable.");
    AssertEqual("0xc7", evmHandler.TraceFromBlock,
        "EVM trace recovery did not preserve the requested range start.");
    AssertEqual("0xc8", evmHandler.TraceToBlock,
        "EVM trace recovery did not preserve the requested range end.");
    Assert(evmHandler.SawFullTransactions,
        "EVM wallet block lookup did not request full transaction objects.");
}

static async Task VerifyPoolDiscoveryAsync(OnChainEngineClient engine)
{
    const string apiKey = "discovery-secret";
    const string tokenProgram = "TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA";
    const string token2022Program = "TokenzQdBNbLqP5VEhdkAS6EPFLC1PHnBqCXEpPxuEb";
    const string pumpProgram = "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P";
    const string pumpSwapProgram = "pAMMBay6oceH9fJKBRHGP5D4bD4sWpmSwMn52FMfXEA";
    const string raydiumAmmV4Program = "675kPX9MHTjS2zt1qfr1NYHuzeLXfQM9H24wFSUt1Mp8";
    const string meteoraDammV1Program = "Eo7WjKq67rjJQSZxS6z3YkapzY3eMj6Xy8X5EQVn5UaB";
    const string meteoraDammV2Program = "cpamdpZCGKUy5JxQXB4dcpGPiikHawvSWAd6mEn1sGG";
    const string meteoraVaultProgram = "24Uqj9JCLxUeoC3hGfh5W3s9FM9uCHDS2SG3LYwBpyTi";
    const string meteoraDlmmProgram = "LBUZKhRxPF3XUpBCjp4YzTKgLccjZhTSDM9YuVaPwxo";
    const string manifestProgram = "MNFSTqtC93rEfYHB6hF82sKdZpUDFWkViLByLd1k1Ms";
    const string wrappedSol = "So11111111111111111111111111111111111111112";
    const string curveAddress = "BqZdLs4LGfrtY7fmwARY2LojpNfLm4LkeCV7cVmGBzGq";

    var mintBytes = Enumerable.Repeat((byte)3, 32).ToArray();
    var mint = EncodeBase58(mintBytes);
    var quoteBytes = DecodeBase58(wrappedSol);
    var poolAddress = EncodeBase58(Enumerable.Repeat((byte)8, 32).ToArray());
    var decoyAddress = EncodeBase58(Enumerable.Repeat((byte)9, 32).ToArray());
    var dlmmAddress = EncodeBase58(Enumerable.Repeat((byte)10, 32).ToArray());
    var reserveX = Enumerable.Repeat((byte)11, 32).ToArray();
    var reserveY = Enumerable.Repeat((byte)12, 32).ToArray();
    var ammV4Address = EncodeBase58(Enumerable.Repeat((byte)13, 32).ToArray());
    var ammCoinVault = Enumerable.Repeat((byte)14, 32).ToArray();
    var ammPcVault = Enumerable.Repeat((byte)15, 32).ToArray();
    var dammV2Address = EncodeBase58(Enumerable.Repeat((byte)16, 32).ToArray());
    var dammV2VaultA = Enumerable.Repeat((byte)17, 32).ToArray();
    var dammV2VaultB = Enumerable.Repeat((byte)18, 32).ToArray();
    var dammV1Address = EncodeBase58(Enumerable.Repeat((byte)19, 32).ToArray());
    var dammV1VaultStateA = Enumerable.Repeat((byte)20, 32).ToArray();
    var dammV1VaultStateB = Enumerable.Repeat((byte)21, 32).ToArray();
    var dammV1VaultLpA = Enumerable.Repeat((byte)22, 32).ToArray();
    var dammV1VaultLpB = Enumerable.Repeat((byte)23, 32).ToArray();
    var dammV1VaultLpMintA = Enumerable.Repeat((byte)24, 32).ToArray();
    var dammV1VaultLpMintB = Enumerable.Repeat((byte)25, 32).ToArray();
    var dammV1TokenVaultA = Enumerable.Repeat((byte)26, 32).ToArray();
    var dammV1TokenVaultB = Enumerable.Repeat((byte)27, 32).ToArray();
    var manifestAddress = EncodeBase58(Enumerable.Repeat((byte)28, 32).ToArray());
    var manifestBaseVault = Enumerable.Repeat((byte)29, 32).ToArray();
    var manifestQuoteVault = Enumerable.Repeat((byte)30, 32).ToArray();
    var accounts = new Dictionary<string, RpcFixtureAccount>(StringComparer.Ordinal)
    {
        [mint] = new(tokenProgram, CreateMintAccount(6)),
        [wrappedSol] = new(tokenProgram, CreateMintAccount(9)),
        [curveAddress] = new(pumpProgram, CreatePumpCurveAccount(quoteBytes)),
        [poolAddress] = new(pumpSwapProgram, CreatePumpSwapAccount(mintBytes, quoteBytes)),
        [decoyAddress] = new(pumpSwapProgram, new byte[301]),
        [ammV4Address] = new(
            raydiumAmmV4Program,
            CreateRaydiumAmmV4Account(mintBytes, quoteBytes, ammCoinVault, ammPcVault)),
        [dammV2Address] = new(
            meteoraDammV2Program,
            CreateMeteoraDammV2Account(quoteBytes, mintBytes, dammV2VaultA, dammV2VaultB)),
        [dammV1Address] = new(
            meteoraDammV1Program,
            CreateMeteoraDammV1Account(
                quoteBytes,
                mintBytes,
                dammV1VaultStateA,
                dammV1VaultStateB,
                dammV1VaultLpA,
                dammV1VaultLpB)),
        [EncodeBase58(dammV1VaultStateA)] = new(
            meteoraVaultProgram,
            CreateMeteoraDynamicVaultAccount(quoteBytes, dammV1TokenVaultA, dammV1VaultLpMintA)),
        [EncodeBase58(dammV1VaultStateB)] = new(
            meteoraVaultProgram,
            CreateMeteoraDynamicVaultAccount(mintBytes, dammV1TokenVaultB, dammV1VaultLpMintB)),
        [dlmmAddress] = new(
            meteoraDlmmProgram,
            CreateMeteoraDlmmAccount(quoteBytes, mintBytes, reserveX, reserveY)),
        [manifestAddress] = new(
            manifestProgram,
            CreateManifestMarketAccount(
                quoteBytes,
                mintBytes,
                manifestQuoteVault,
                manifestBaseVault))
    };
    using var handler = new SolanaRpcFixtureHandler(apiKey, mint, poolAddress, decoyAddress, accounts);
    using var httpClient = new HttpClient(handler);
    var rpc = new SolanaRpcClient(
        httpClient,
        CreateProviderConfiguration(
            OnChainProviderTypes.HeliusLaserStream,
            "https://stream.unit-test.helius-rpc.com",
            "https://unit-test.helius-rpc.com"),
        apiKey);
    var discovery = new OnChainPoolDiscoveryService(rpc, engine);

    var result = await discovery.DiscoverAsync(
        mint,
        OnChainCommitment.Confirmed,
        [ammV4Address, dammV1Address, dammV2Address, dlmmAddress, manifestAddress]);
    AssertEqual(7, result.Pools.Length,
        "Discovery did not return the validated Pump, PumpSwap, Raydium AMM v4, DAMM v1/v2, DLMM, and Manifest markets.");
    Assert(result.Pools.All(static pool => pool.SupportStatus == OnChainSupportStatus.Supported),
        "Validated legacy-token fixtures should be supported.");
    Assert(result.Pools.Any(pool => pool.PoolKey.ProtocolId == OnChainProtocolIds.PumpBondingCurve
                                    && pool.PoolKey.PoolAddress == curveAddress),
        "The derived Pump curve was not returned.");
    var pumpSwap = result.Pools.Single(pool => pool.PoolKey.ProtocolId == OnChainProtocolIds.PumpSwap);
    AssertEqual(poolAddress, pumpSwap.PoolKey.PoolAddress, "PumpSwap candidate identity changed.");
    AssertEqual((byte)6, pumpSwap.BaseDecimals, "Base mint decimals were not decoded on-chain.");
    AssertEqual((byte)9, pumpSwap.QuoteDecimals, "Quote mint decimals were not decoded on-chain.");
    AssertEqual("selectedAsBase", pumpSwap.PairOrientation, "Pool orientation was not preserved.");
    var dlmm = result.Pools.Single(pool => pool.PoolKey.ProtocolId == OnChainProtocolIds.MeteoraDlmm);
    AssertEqual(mint, dlmm.BaseMint, "Reverse-oriented DLMM discovery did not normalize the selected mint as base.");
    AssertEqual(wrappedSol, dlmm.QuoteMint, "Reverse-oriented DLMM discovery did not normalize the quote mint.");
    AssertEqual(EncodeBase58(reserveY), dlmm.BaseVault, "Reverse-oriented DLMM discovery chose the wrong base vault.");
    AssertEqual(EncodeBase58(reserveX), dlmm.QuoteVault, "Reverse-oriented DLMM discovery chose the wrong quote vault.");
    AssertEqual("selectedAsQuote", dlmm.PairOrientation, "Canonical DLMM orientation was not preserved for diagnostics.");
    AssertEqual(OnChainSupportStatus.Supported, dlmm.SupportStatus,
        "A valid reverse-oriented DLMM pool was not selectable.");
    var ammV4 = result.Pools.Single(pool => pool.PoolKey.ProtocolId == OnChainProtocolIds.RaydiumAmmV4);
    AssertEqual(ammV4Address, ammV4.PoolKey.PoolAddress,
        "Raydium AMM v4 candidate identity changed.");
    AssertEqual(EncodeBase58(ammCoinVault), ammV4.BaseVault,
        "Raydium AMM v4 discovery chose the wrong coin vault.");
    AssertEqual(EncodeBase58(ammPcVault), ammV4.QuoteVault,
        "Raydium AMM v4 discovery chose the wrong pc vault.");
    var dammV2 = result.Pools.Single(pool => pool.PoolKey.ProtocolId == OnChainProtocolIds.MeteoraDammV2);
    AssertEqual(mint, dammV2.BaseMint,
        "Reverse-oriented DAMM v2 discovery did not normalize the selected mint as base.");
    AssertEqual(EncodeBase58(dammV2VaultB), dammV2.BaseVault,
        "Reverse-oriented DAMM v2 discovery chose the wrong base vault.");
    AssertEqual(EncodeBase58(dammV2VaultA), dammV2.QuoteVault,
        "Reverse-oriented DAMM v2 discovery chose the wrong quote vault.");
    var dammV1 = result.Pools.Single(pool => pool.PoolKey.ProtocolId == OnChainProtocolIds.MeteoraDammV1);
    AssertEqual(mint, dammV1.BaseMint,
        "Reverse-oriented DAMM v1 discovery did not normalize the selected mint as base.");
    AssertEqual(EncodeBase58(dammV1TokenVaultB), dammV1.BaseVault,
        "DAMM v1 discovery did not resolve the selected token's underlying Dynamic Vault account.");
    AssertEqual(EncodeBase58(dammV1TokenVaultA), dammV1.QuoteVault,
        "DAMM v1 discovery did not resolve the quote token's underlying Dynamic Vault account.");
    AssertEqual(6, dammV1.ProtocolAccounts.Length,
        "DAMM v1 discovery did not preserve all state, share, and supply dependencies.");
    AssertEqual(EncodeBase58(dammV1VaultLpMintB),
        dammV1.ProtocolAccounts.Single(account => account.Role == "baseVaultLpMint").Address,
        "DAMM v1 reverse orientation selected the wrong base vault LP mint.");
    var manifest = result.Pools.Single(pool =>
        pool.PoolKey.ProtocolId == OnChainProtocolIds.ManifestOrderbook);
    AssertEqual(mint, manifest.BaseMint,
        "Reverse-oriented Manifest discovery did not normalize the selected mint as base.");
    AssertEqual(EncodeBase58(manifestBaseVault), manifest.BaseVault,
        "Manifest discovery chose the wrong selected-token vault.");
    AssertEqual(EncodeBase58(manifestQuoteVault), manifest.QuoteVault,
        "Manifest discovery chose the wrong quote-token vault.");
    AssertEqual("centralLimitOrderBook", manifest.PoolType,
        "Manifest was mislabeled as an AMM pool.");

    var meteoraCatalog = MeteoraDammV2PoolCatalogClient.Parse(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            data = new object[]
            {
                new
                {
                    address = dammV2Address,
                    token_x = new { address = wrappedSol, name = "Wrapped SOL", symbol = "SOL" },
                    token_y = new { address = mint, name = "Test Token", symbol = "TEST" },
                    tvl = 1234.5,
                    volume = new Dictionary<string, double> { ["24h"] = 987.25 },
                    created_at = 1_754_985_927_000L,
                    pool_config = new { concentrated_liquidity = true }
                },
                new
                {
                    address = decoyAddress,
                    token_x = new { address = wrappedSol, name = "Wrapped SOL", symbol = "SOL" },
                    token_y = new { address = poolAddress, name = "Other", symbol = "OTHER" },
                    tvl = 9999.0
                }
            }
        }),
        mint);
    AssertEqual(1, meteoraCatalog.Pools.Length,
        "Meteora DAMM v2 catalog parsing did not require an exact mint match.");
    AssertEqual(OnChainProtocolIds.MeteoraDammV2, meteoraCatalog.Pools[0].ProtocolId,
        "Meteora DAMM v2 catalog parsing lost the internal protocol identity.");
    AssertEqual("Concentrated", meteoraCatalog.Pools[0].Labels.Single(),
        "Meteora DAMM v2 catalog parsing lost the liquidity mode.");

    var meteoraV1Catalog = MeteoraDammV1PoolCatalogClient.Parse(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            data = new object[]
            {
                new
                {
                    pool_address = dammV1Address,
                    pool_token_mints = new[] { wrappedSol, mint },
                    pool_name = "SOL-TEST",
                    pool_type = "volatile",
                    pool_tvl = "4321.5",
                    trading_volume = 765.25,
                    created_at = 1_738_820_726L
                }
            }
        }),
        mint);
    AssertEqual(1, meteoraV1Catalog.Pools.Length,
        "Meteora DAMM v1 catalog parsing did not retain the exact mint pool.");
    AssertEqual(OnChainProtocolIds.MeteoraDammV1, meteoraV1Catalog.Pools[0].ProtocolId,
        "Meteora DAMM v1 catalog parsing lost the internal protocol identity.");
    AssertEqual(4_321.5, meteoraV1Catalog.Pools[0].LiquidityUsd,
        "Meteora DAMM v1 catalog parsing did not use invariant-culture TVL.");
    var manifestCatalog = ManifestPoolCatalogClient.Parse(
        JsonSerializer.SerializeToUtf8Bytes(new object[]
        {
            new
            {
                ticker_id = manifestAddress,
                base_currency = wrappedSol,
                target_currency = mint,
                pool_id = manifestAddress,
                liquidity_in_usd = "123.5"
            },
            new
            {
                ticker_id = decoyAddress,
                base_currency = wrappedSol,
                target_currency = poolAddress,
                pool_id = decoyAddress,
                liquidity_in_usd = 999.0
            }
        }),
        mint);
    AssertEqual(1, manifestCatalog.Pools.Length,
        "Manifest catalog parsing did not require an exact mint match.");
    AssertEqual(OnChainProtocolIds.ManifestOrderbook, manifestCatalog.Pools[0].ProtocolId,
        "Manifest catalog parsing lost the internal order-book identity.");
    AssertEqual(123.5, manifestCatalog.Pools[0].LiquidityUsd,
        "Manifest catalog parsing did not use invariant-culture liquidity metadata.");
    Assert(result.Warnings.Any(static warning => warning.Contains("Ignored 1 candidate", StringComparison.Ordinal)),
        "The invalid candidate was not reported as rejected.");
    Assert(handler.SawApiKey && handler.SawZeroLengthDataSlice,
        "Discovery did not authenticate transiently or use the bounded address-only scan.");

    var token2022Accounts = accounts.ToDictionary(static entry => entry.Key, static entry => entry.Value,
        StringComparer.Ordinal);
    token2022Accounts[mint] = new RpcFixtureAccount(
        token2022Program,
        CreateToken2022MintAccount(6, (18, 64), (19, 4)));
    using var token2022Handler = new SolanaRpcFixtureHandler(
        apiKey,
        mint,
        poolAddress,
        decoyAddress,
        token2022Accounts);
    using var token2022HttpClient = new HttpClient(token2022Handler);
    var token2022Discovery = new OnChainPoolDiscoveryService(
        new SolanaRpcClient(
            token2022HttpClient,
            CreateProviderConfiguration(
                OnChainProviderTypes.HeliusLaserStream,
                "https://stream.unit-test.helius-rpc.com",
                "https://unit-test.helius-rpc.com"),
            apiKey),
        engine);
    var token2022Result = await token2022Discovery.DiscoverAsync(mint, OnChainCommitment.Confirmed);
    AssertEqual(2, token2022Result.Pools.Length,
        "Token-2022 safety classification discarded discoverable pool identities.");
    Assert(token2022Result.Pools.All(static pool =>
            pool.SupportStatus == OnChainSupportStatus.Supported),
        "Metadata-only Token-2022 pools were not selectable.");

    var transferFeeAccounts = accounts.ToDictionary(static entry => entry.Key, static entry => entry.Value,
        StringComparer.Ordinal);
    transferFeeAccounts[mint] = new RpcFixtureAccount(
        token2022Program,
        CreateToken2022MintAccount(6, (1, 1)));
    using var transferFeeHandler = new SolanaRpcFixtureHandler(
        apiKey,
        mint,
        poolAddress,
        decoyAddress,
        transferFeeAccounts);
    using var transferFeeHttpClient = new HttpClient(transferFeeHandler);
    var transferFeeDiscovery = new OnChainPoolDiscoveryService(
        new SolanaRpcClient(
            transferFeeHttpClient,
            CreateProviderConfiguration(
                OnChainProviderTypes.HeliusLaserStream,
                "https://stream.unit-test.helius-rpc.com",
                "https://unit-test.helius-rpc.com"),
            apiKey),
        engine);
    var transferFeeResult = await transferFeeDiscovery.DiscoverAsync(mint, OnChainCommitment.Confirmed);
    AssertEqual(2, transferFeeResult.Pools.Length,
        "Token-2022 transfer-fee classification discarded discoverable pool identities.");
    Assert(transferFeeResult.Pools.All(static pool =>
            pool.SupportStatus == OnChainSupportStatus.DiscoveredUnsupported
            && pool.SupportReason?.Contains("non-metadata", StringComparison.Ordinal) == true),
        "A Token-2022 transfer-fee mint bypassed the fail-closed pricing boundary.");

    using var rateLimitedHandler = new SolanaRpcFixtureHandler(
        apiKey,
        mint,
        poolAddress,
        decoyAddress,
        accounts,
        rateLimitProgramAccounts: true);
    using var rateLimitedHttpClient = new HttpClient(rateLimitedHandler);
    var rateLimitedDiscovery = new OnChainPoolDiscoveryService(
        new SolanaRpcClient(
            rateLimitedHttpClient,
            CreateProviderConfiguration(
                OnChainProviderTypes.AlchemyWebSocket,
                "wss://solana-mainnet.streaming.alchemy.com/v2",
                "https://solana-mainnet.g.alchemy.com/v2"),
            apiKey),
        engine);

    var partialResult = await rateLimitedDiscovery.DiscoverAsync(mint, OnChainCommitment.Confirmed);
    AssertEqual(1, partialResult.Pools.Length,
        "A PumpSwap discovery rate limit discarded the independently validated Pump curve.");
    AssertEqual(OnChainProtocolIds.PumpBondingCurve, partialResult.Pools[0].PoolKey.ProtocolId,
        "The rate-limited discovery result did not preserve the Pump curve.");
    Assert(partialResult.Warnings.Any(static warning => warning.Contains("rate-limited", StringComparison.Ordinal)),
        "The incomplete PumpSwap discovery result did not explain the provider rate limit.");
    AssertEqual(1, rateLimitedHandler.ProgramAccountRequestCount,
        "Discovery continued issuing expensive PumpSwap scans after the provider rate limit.");

    using var publicRpcHandler = new SolanaRpcFixtureHandler(
        string.Empty,
        mint,
        poolAddress,
        decoyAddress,
        accounts);
    using var publicRpcHttpClient = new HttpClient(publicRpcHandler);
    var publicDiscovery = new OnChainPoolDiscoveryService(
        new SolanaRpcClient(publicRpcHttpClient, OnChainPoolDiscoveryService.PublicMainnetRpcEndpoint),
        engine);

    var publicResult = await publicDiscovery.DiscoverAsync(mint, OnChainCommitment.Confirmed);
    AssertEqual(2, publicResult.Pools.Length,
        "Provider-independent public discovery did not return both strictly validated pools.");
    AssertEqual(8, publicRpcHandler.ProgramAccountRequestCount,
        "Public discovery did not run the eight bounded PumpSwap orientation/layout scans.");
    Assert(publicRpcHandler.SawCredentialFreeRequest && publicRpcHandler.SawZeroLengthDataSlice,
        "Public discovery was not credential-free or did not use bounded address-only scans.");
}

static async Task VerifyLiveProtocolAccountsAsync(OnChainEngineClient engine)
{
    const string sampleMint = "6NwarBvDkXhByqVp2Qkq5i9XbtA2B3Bwe8SWGu9vpump";
    const string metadataOnlyToken2022Mint = "CNWxmoBSQZo2Sgp5KSAK5m9FwSqDbXQRP4CNMuoe78Gm";
    const string metadataOnlyPumpSwapPool = "5x74XfDESP5j2vfr7nW76mqgQkKMUJUhGWXog4FaJs9X";
    var fixtures = new[]
    {
        new LivePoolFixture(
            OnChainProtocolIds.RaydiumAmmV4,
            "58oQChx4yWmvKdwLLZzBi4ChoCc2fqCUWBkwMihLYQo2"),
        new LivePoolFixture(
            OnChainProtocolIds.RaydiumCpmm,
            "7JuwJuNU88gurFnyWeiyGKbFmExMWcmRZntn9imEzdny"),
        new LivePoolFixture(
            OnChainProtocolIds.RaydiumClmm,
            "BZtgQEyS6eXUXicYPHecYQ7PybqodXQMvkjUbP4R8mUU"),
        new LivePoolFixture(
            OnChainProtocolIds.MeteoraDammV1,
            "4SBYWY5UuxybWuj8FwHdFXUN6mbtACrqbJwiZ9mXworP"),
        new LivePoolFixture(
            OnChainProtocolIds.MeteoraDammV2,
            "8Pm2kZpnxD3hoMmt4bjStX2Pw2Z9abpbHzZxMPqxPmie"),
        new LivePoolFixture(
            OnChainProtocolIds.MeteoraDlmm,
            "3S86WtfvZroac8tGH3h1bKZmPK7uaZWNCg2U6kZH9vvd"),
        new LivePoolFixture(
            OnChainProtocolIds.OrcaWhirlpool,
            "AjuZMJRkcP9QsJuuBg35eAKU7wM9A8a11qy5D9nvp7xy")
    };
    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var rpc = new SolanaRpcClient(httpClient, OnChainPoolDiscoveryService.PublicMainnetRpcEndpoint);
    var accounts = await rpc.GetMultipleAccountsAsync(
        fixtures.Select(static fixture => fixture.Address).ToArray(),
        OnChainCommitment.Confirmed,
        CancellationToken.None);
    for (var index = 0; index < fixtures.Length; index++)
    {
        var fixture = fixtures[index];
        var account = accounts[index]
                      ?? throw new InvalidOperationException($"Live pool {fixture.Address} was unavailable.");
        var decoded = await engine.DecodePoolAccountAsync(
            fixture.ProtocolId,
            fixture.Address,
            "So11111111111111111111111111111111111111112",
            account.OwnerProgram,
            account.DataBase64);
        AssertEqual(fixture.ProtocolId, decoded.ProtocolId,
            $"Live pool {fixture.Address} decoded as the wrong protocol.");
        Assert(!string.IsNullOrWhiteSpace(decoded.BaseMint)
               && !string.IsNullOrWhiteSpace(decoded.QuoteMint)
               && (fixture.ProtocolId == OnChainProtocolIds.MeteoraDammV1
                   ? decoded.ProtocolAccounts.Length == 4
                   : !string.IsNullOrWhiteSpace(decoded.BaseVault)
                     && !string.IsNullOrWhiteSpace(decoded.QuoteVault)),
            $"Live pool {fixture.Address} did not expose its complete pair identity.");
    }

    var catalog = await DexScreenerPoolCatalogClient.Current.SearchAsync("solana", sampleMint);
    var discovery = new OnChainPoolDiscoveryService(rpc, engine);
    var discovered = await discovery.DiscoverAsync(
        sampleMint,
        OnChainCommitment.Confirmed,
        catalog.Pools.Select(static pool => pool.PoolAddress).ToArray());
    foreach (var protocol in new[]
             {
                 OnChainProtocolIds.PumpSwap,
                 OnChainProtocolIds.MeteoraDlmm,
                 OnChainProtocolIds.OrcaWhirlpool
             })
    {
        Assert(discovered.Pools.Any(pool => pool.PoolKey.ProtocolId == protocol
                                            && pool.SupportStatus == OnChainSupportStatus.Supported),
            $"The live sample mint did not return a selectable {protocol} pool.");
    }

    var token2022Catalog = await DexScreenerPoolCatalogClient.Current.SearchAsync(
        "solana",
        metadataOnlyToken2022Mint);
    var token2022Discovered = await discovery.DiscoverAsync(
        metadataOnlyToken2022Mint,
        OnChainCommitment.Confirmed,
        token2022Catalog.Pools.Select(static pool => pool.PoolAddress).ToArray());
    Assert(token2022Discovered.Pools.Any(pool =>
            pool.PoolKey.PoolAddress == metadataOnlyPumpSwapPool
            && pool.PoolKey.ProtocolId == OnChainProtocolIds.PumpSwap
            && pool.SupportStatus == OnChainSupportStatus.Supported),
        "The live metadata-only Token-2022 mint did not return its selectable PumpSwap pool.");
}

static void VerifyYellowstoneNormalization()
{
    var pool = EncodeBase58(Enumerable.Repeat((byte)8, 32).ToArray());
    var baseVault = EncodeBase58(Enumerable.Repeat((byte)4, 32).ToArray());
    var quoteVault = EncodeBase58(Enumerable.Repeat((byte)5, 32).ToArray());
    var subscription = new OnChainStreamSubscription
    {
        Commitment = OnChainCommitment.Confirmed,
        FromSlot = 77,
        Pools =
        [
            new OnChainWatchedPoolSelection
            {
                SelectedMint = EncodeBase58(Enumerable.Repeat((byte)3, 32).ToArray()),
                Descriptor = new OnChainPoolDescriptor
                {
                    PoolKey = new OnChainPoolKey
                    {
                        ProtocolId = OnChainProtocolIds.PumpSwap,
                        PoolAddress = pool
                    },
                    BaseVault = baseVault,
                    QuoteVault = quoteVault
                }
            }
        ]
    };
    var request = YellowstoneStreamSource.BuildRequest(subscription);
    AssertEqual(CommitmentLevel.Confirmed, request.Commitment,
        "Yellowstone commitment was not mapped.");
    Assert(request.HasFromSlot && request.FromSlot == 77,
        "Yellowstone replay slot was not included.");
    AssertEqual(3, request.Accounts["watched-pool-state"].Account.Count,
        "Yellowstone did not subscribe to the pool and both vaults.");
    AssertEqual(pool, request.Transactions["watched-pool-transactions"].AccountInclude.Single(),
        "Yellowstone transaction filter did not preserve exact pool identity.");

    var pumpProgramBytes = DecodeBase58("6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P");
    var pumpSwapProgramBytes = DecodeBase58("pAMMBay6oceH9fJKBRHGP5D4bD4sWpmSwMn52FMfXEA");
    var transaction = new TrenchHQ.Yellowstone.SolanaStorage.Transaction
    {
        Message = new TrenchHQ.Yellowstone.SolanaStorage.Message()
    };
    transaction.Signatures.Add(ByteString.CopyFrom(Enumerable.Repeat((byte)10, 64).ToArray()));
    transaction.Message.AccountKeys.Add(ByteString.CopyFrom(DecodeBase58(baseVault)));
    transaction.Message.AccountKeys.Add(ByteString.CopyFrom(pumpProgramBytes));
    transaction.Message.Instructions.Add(new TrenchHQ.Yellowstone.SolanaStorage.CompiledInstruction
    {
        ProgramIdIndex = 1,
        Data = ByteString.CopyFrom([1, 2, 3]),
        Accounts = ByteString.CopyFrom([0, 3])
    });
    var meta = new TrenchHQ.Yellowstone.SolanaStorage.TransactionStatusMeta();
    meta.LoadedWritableAddresses.Add(ByteString.CopyFrom(pumpSwapProgramBytes));
    meta.LoadedReadonlyAddresses.Add(ByteString.CopyFrom(DecodeBase58(quoteVault)));
    meta.PreTokenBalances.Add(CreateYellowstoneTokenBalance(0, "base-mint", 1000));
    meta.PostTokenBalances.Add(CreateYellowstoneTokenBalance(0, "base-mint", 900));
    meta.PreTokenBalances.Add(CreateYellowstoneTokenBalance(3, "quote-mint", 2000));
    meta.PostTokenBalances.Add(CreateYellowstoneTokenBalance(3, "quote-mint", 2200));
    var inner = new TrenchHQ.Yellowstone.SolanaStorage.InnerInstructions { Index = 0 };
    inner.Instructions.Add(new TrenchHQ.Yellowstone.SolanaStorage.InnerInstruction
    {
        ProgramIdIndex = 2,
        Data = ByteString.CopyFrom([4, 5, 6]),
        StackHeight = 2,
        Accounts = ByteString.CopyFrom([0, 3])
    });
    meta.InnerInstructions.Add(inner);
    meta.LogMessages.Add("Program 6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P invoke [1]");
    meta.LogMessages.Add("Program data: AQID");
    meta.LogMessages.Add("Program pAMMBay6oceH9fJKBRHGP5D4bD4sWpmSwMn52FMfXEA invoke [2]");
    meta.LogMessages.Add("Program data: BAUG");
    meta.LogMessages.Add("Program pAMMBay6oceH9fJKBRHGP5D4bD4sWpmSwMn52FMfXEA success");
    meta.LogMessages.Add("Program data: BwgJ");
    meta.LogMessages.Add("Program 6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P success");
    var update = new SubscribeUpdate
    {
        CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(500)),
        Transaction = new SubscribeUpdateTransaction
        {
            Slot = 88,
            Transaction = new SubscribeUpdateTransactionInfo
            {
                Signature = ByteString.CopyFrom(Enumerable.Repeat((byte)10, 64).ToArray()),
                Transaction = transaction,
                Meta = meta
            }
        }
    };

    var normalized = YellowstoneStreamSource.Normalize(update, OnChainCommitment.Confirmed, "fixture")
                     as OnChainSourceTransactionUpdate
                     ?? throw new InvalidOperationException("Yellowstone transaction was not normalized.");
    AssertEqual(88UL, normalized.Update.Slot, "Normalized transaction slot changed.");
    AssertEqual(2, normalized.Update.Instructions.Length,
        "Outer and inner instructions were not both preserved.");
    AssertEqual("6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P",
        normalized.Update.Instructions[0].ProgramId,
        "Static program index was resolved incorrectly.");
    AssertEqual("pAMMBay6oceH9fJKBRHGP5D4bD4sWpmSwMn52FMfXEA",
        normalized.Update.Instructions[1].ProgramId,
        "Loaded program index was resolved incorrectly.");
    AssertEqual((ushort)0, normalized.Update.Instructions[1].OuterInstructionIndex,
        "Inner instruction parent index changed.");
    AssertEqual((ushort)0, normalized.Update.Instructions[1].InnerInstructionIndex,
        "Inner instruction index changed.");
    AssertEqual(2U, normalized.Update.Instructions[1].StackHeight,
        "Inner stack height was lost.");
    AssertEqual(baseVault, normalized.Update.Instructions[1].AccountAddresses[0],
        "Yellowstone did not resolve a static instruction account.");
    AssertEqual(quoteVault, normalized.Update.Instructions[1].AccountAddresses[1],
        "Yellowstone did not resolve a loaded instruction account.");
    AssertEqual(2, normalized.Update.TokenBalances.Length,
        "Yellowstone did not normalize changed transaction token balances.");
    AssertEqual(3, normalized.Update.ProgramData.Length,
        "Yellowstone did not preserve program-attributed log data.");
    AssertEqual("pAMMBay6oceH9fJKBRHGP5D4bD4sWpmSwMn52FMfXEA",
        normalized.Update.ProgramData[1].ProgramId,
        "Yellowstone attributed nested program data to the wrong program.");
    AssertEqual(3U, normalized.Update.ProgramData[1].LogIndex,
        "Yellowstone changed the program-data log index.");
    AssertEqual(2200UL,
        normalized.Update.TokenBalances.Single(balance => balance.AccountAddress == quoteVault).PostAmountRaw,
        "Yellowstone returned the wrong post-swap quote-vault amount.");
    AssertEqual(500L, normalized.Update.ObservedAtUnixMs,
        "Provider observation time was not preserved.");
}

static TrenchHQ.Yellowstone.SolanaStorage.TokenBalance CreateYellowstoneTokenBalance(
    uint accountIndex,
    string mint,
    ulong amount)
{
    return new TrenchHQ.Yellowstone.SolanaStorage.TokenBalance
    {
        AccountIndex = accountIndex,
        Mint = mint,
        UiTokenAmount = new TrenchHQ.Yellowstone.SolanaStorage.UiTokenAmount
        {
            Amount = amount.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }
    };
}

static async Task VerifySolanaPriceSamplingAsync(string enginePath)
{
    var standardPresets = OnChainProviderCatalog.Presets
        .Where(static preset => preset.StreamTransport == OnChainStreamTransport.SolanaWebSocket)
        .ToArray();
    AssertEqual(6, standardPresets.Length,
        "The standard Solana provider coverage changed without a sampling regression.");
    foreach (var preset in standardPresets)
    {
        var configuration = OnChainProviderConfigurationStore.CreateConfiguration(
            preset);
        Assert(OnChainStreamCoordinator.CreateSource(configuration, "fixture-key")
               is SolanaRpcSampleStreamSource,
            "A Solana price-only route subscribed to every transaction event.");
    }

    var profile = OnChainProviderConfigurationStore.CreateConfiguration(
        OnChainProviderCatalog.Get(OnChainProviderTypes.AlchemyWebSocket));
    using var handler = new SolanaSampleRpcFixtureHandler();
    var source = new SolanaRpcSampleStreamSource(
        profile, "fixture-key", handler, TimeSpan.FromMilliseconds(100));
    var channel = Channel.CreateUnbounded<OnChainSourceUpdate>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var selection = new OnChainWatchedPoolSelection
    {
        Descriptor = new OnChainPoolDescriptor
        {
            PoolKey = new OnChainPoolKey { PoolAddress = "sample-pool" },
            BaseVault = "shared-vault",
            QuoteVault = "quote-vault",
            ProtocolAccounts = [new() { Role = "duplicate", Address = "shared-vault" }]
        }
    };
    var producer = source.RunAsync(new OnChainStreamSubscription
    {
        Pools = [selection],
        Commitment = OnChainCommitment.Confirmed
    }, channel.Writer, cts.Token);
    var update = await channel.Reader.ReadAsync(cts.Token);
    Assert(update is OnChainSourceAccountSnapshot,
        "Solana sampling did not publish one complete account snapshot.");
    var snapshot = (OnChainSourceAccountSnapshot)update;
    var updates = snapshot.Updates;
    await Task.Delay(250);
    AssertEqual(1, handler.RequestCount, "Solana sampling ran ahead of the engine consumer.");
    snapshot.Processed.TrySetResult();
    cts.Cancel();
    try
    {
        await producer;
    }
    catch (OperationCanceledException)
    {
    }
    AssertEqual(1, handler.RequestCount,
        "One Solana sampling tick made more than one batched RPC request.");
    AssertEqual(3, handler.LastAddresses.Length,
        "A shared Solana account was fetched more than once per sample.");
    Assert(updates.All(update => update.Slot == 123
                                 && update.WriteVersion == 0
                                 && update.Commitment == OnChainCommitment.Confirmed
                                 && update.SourceId.StartsWith("rpcSample:", StringComparison.Ordinal)),
        "Solana sampled account provenance or slot was lost.");

    var many = Enumerable.Range(0, 40).Select(index => new OnChainWatchedPoolSelection
    {
        Descriptor = new OnChainPoolDescriptor
        {
            PoolKey = new OnChainPoolKey { PoolAddress = $"pool-{index}" },
            BaseVault = $"base-{index}",
            QuoteVault = $"quote-{index}"
        }
    }).ToArray();
    var batches = SolanaRpcSampleStreamSource.BuildAccountBatches(many);
    AssertEqual(2, batches.Length, "Solana batches did not respect the 100-account limit.");
    Assert(batches.All(batch => batch.Length <= 100)
           && many.All(pool => batches.Any(batch =>
               pool.Descriptor.EnumerateSolanaAccountAddresses().All(address => batch.Contains(address)))),
        "A pool was split between different Solana snapshot slots.");

    using var missingHandler = new SolanaSampleRpcFixtureHandler
    {
        MissingAddress = "quote-vault"
    };
    var missingSource = new SolanaRpcSampleStreamSource(
        profile, "fixture-key", missingHandler, TimeSpan.FromMilliseconds(20));
    var missingChannel = Channel.CreateUnbounded<OnChainSourceUpdate>();
    using var missingCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    try
    {
        await missingSource.RunAsync(new OnChainStreamSubscription
        {
            Pools = [selection],
            Commitment = OnChainCommitment.Confirmed
        }, missingChannel.Writer, missingCts.Token);
        throw new InvalidOperationException("A missing selected pool account was accepted.");
    }
    catch (SolanaRpcException)
    {
    }
    AssertEqual(0, missingChannel.Reader.Count,
        "A partial Solana pool sample was published before the missing account failed.");

    var engine = new OnChainEngineClient(enginePath);
    var prices = new ConcurrentQueue<OnChainPriceUpdate>();
    engine.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
    var mint = EncodeBase58(Enumerable.Repeat((byte)3, 32).ToArray());
    var quote = EncodeBase58(Enumerable.Repeat((byte)9, 32).ToArray());
    var pool = new OnChainWatchedPoolSelection
    {
        SelectedMint = mint,
        Descriptor = new OnChainPoolDescriptor
        {
            PoolKey = new OnChainPoolKey
            {
                ProtocolId = OnChainProtocolIds.PumpBondingCurve,
                PoolAddress = "atomic-sample-curve"
            },
            PoolType = "bondingCurve",
            ProgramId = "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P",
            BaseMint = mint,
            QuoteMint = quote,
            BaseDecimals = 6,
            QuoteDecimals = 9,
            SupportStatus = OnChainSupportStatus.Supported
        }
    };
    try
    {
        await engine.ReplaceWatchedPoolsAsync("atomic-sample", [pool]);
        await engine.PublishAccountSnapshotAsync([new OnChainRawAccountUpdate
        {
            Pubkey = pool.Descriptor.PoolKey.PoolAddress,
            OwnerProgram = pool.Descriptor.ProgramId,
            DataBase64 = Convert.ToBase64String(CreatePumpCurveAccount(
                Enumerable.Repeat((byte)9, 32).ToArray())),
            Slot = 123,
            SourceId = "rpcSample:fixture",
            Commitment = OnChainCommitment.Confirmed
        }]);
        await WaitUntilAsync(() => prices.Any(price => price.SpotPriceQuote != null
                                                      && price.Slot == 123),
            TimeSpan.FromSeconds(5), "atomic Solana sample over real engine IPC");
    }
    finally
    {
        await engine.StopAsync();
    }
}

static async Task VerifyStreamCoordinatorAsync(string enginePath)
{
    var checkpointRoot = Path.Combine(Path.GetTempPath(), "TrenchHQOnChainCheckpoint", Guid.NewGuid().ToString("N"));
    var baselineProcessIds = GetEngineProcessIds();
    var client = new OnChainEngineClient(enginePath);
    var prices = new ConcurrentQueue<OnChainPriceUpdate>();
    client.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
    var mintBytes = Enumerable.Repeat((byte)3, 32).ToArray();
    var mint = EncodeBase58(mintBytes);
    var pool = new OnChainWatchedPoolSelection
    {
        SelectedMint = mint,
        Descriptor = new OnChainPoolDescriptor
        {
            PoolKey = new OnChainPoolKey
            {
                ProtocolId = OnChainProtocolIds.PumpBondingCurve,
                PoolAddress = "coordinator-curve"
            },
            PoolType = "bondingCurve",
            ProgramId = "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P",
            BaseMint = mint,
            QuoteMint = "So11111111111111111111111111111111111111112",
            BaseDecimals = 6,
            QuoteDecimals = 9,
            SupportStatus = OnChainSupportStatus.Supported
        }
    };
    var configuration = new OnChainProviderConfiguration
    {
        Id = "coordinator-configuration",
        StreamEndpoint = "wss://stream.test",
        RpcEndpoint = "https://rpc.test",
        Commitment = OnChainCommitment.Processed,
        ReplayEnabled = false
    };
    var source = new ScriptedOnChainSource(mintBytes);
    var coordinator = new OnChainStreamCoordinator(
        client,
        checkpointRoot,
        (_, _, _) => source,
        (_, _, _, _) => Task.CompletedTask);
    try
    {
        await coordinator.StartAsync(configuration, "checkpoint-secret", [pool]);
        await WaitUntilAsync(() => prices.Count == 1, TimeSpan.FromSeconds(5), "coordinated price");
        await coordinator.StartAsync(configuration, "checkpoint-secret", [pool]);
        await Task.Delay(100);
        AssertEqual(1, source.RunCount,
            "An unchanged panel refresh restarted the on-chain stream.");
        var firstProcessId = GetSingleNewEngineProcessId(baselineProcessIds);
        using (var process = Process.GetProcessById(firstProcessId))
        {
            process.Kill(true);
            process.WaitForExit(2000);
        }
        await WaitUntilAsync(() => source.RunCount >= 2, TimeSpan.FromSeconds(10),
            "coordinator stream restart after engine crash");
        await WaitUntilAsync(() => prices.Count >= 2, TimeSpan.FromSeconds(10),
            "post-crash snapshot epoch price");
        await WaitUntilAsync(() => coordinator.State == OnChainRecoveryState.Live,
            TimeSpan.FromSeconds(10), "coordinator live recovery after engine crash");
        var secondProcessId = GetSingleNewEngineProcessId(baselineProcessIds);
        Assert(firstProcessId != secondProcessId,
            "Coordinator recovery reused the terminated engine process.");
        await WaitUntilAsync(
            () => OnChainCheckpointStore.LoadAsync(checkpointRoot, configuration.Id).GetAwaiter().GetResult() == 42UL,
            TimeSpan.FromSeconds(5),
            "confirmed checkpoint");
        AssertEqual(1, source.ObservedSubscription?.Pools.Length ?? 0,
            "Coordinator did not preserve the selected pool subscription.");
        await coordinator.StopAsync();
        Assert(!File.ReadAllText(Path.Combine(checkpointRoot, "onchain-checkpoints-v1.json"))
                .Contains("checkpoint-secret", StringComparison.Ordinal),
            "Checkpoint persistence exposed the provider API key.");
    }
    finally
    {
        await coordinator.StopAsync();
        await client.StopAsync();
        if (Directory.Exists(checkpointRoot))
        {
            Directory.Delete(checkpointRoot, true);
        }
    }
    await WaitUntilAsync(
        () => !GetEngineProcessIds().Except(baselineProcessIds).Any(),
        TimeSpan.FromSeconds(5),
        "coordinator lazy engine stop");
}

static async Task VerifyMixedSourceFailureAsync()
{
    var waitingSource = new WaitingOnChainSource();
    var failedSource = new FailedOnChainSource();
    var channel = Channel.CreateUnbounded<OnChainSourceUpdate>();
    var subscription = new OnChainStreamSubscription();
    try
    {
        await OnChainStreamCoordinator.RunSourcesAsync(
                [(waitingSource, subscription), (failedSource, subscription)],
                channel.Writer,
                CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));
        throw new InvalidOperationException("A failed mixed-mode source was ignored.");
    }
    catch (IOException exception) when (exception.Message == "simulated source failure")
    {
    }
    Assert(waitingSource.Cancelled,
        "The healthy mixed-mode source was not cancelled after its peer failed.");
    Assert(channel.Reader.Completion.IsFaulted,
        "The mixed-mode consumer was not notified of its source failure.");
}

static async Task VerifyEvmStreamCoordinatorAsync(string enginePath)
{
    const string selectedToken = "0x1111111111111111111111111111111111111111";
    const string poolAddress = "0x2222222222222222222222222222222222222222";
    var checkpointRoot = Path.Combine(
        Path.GetTempPath(),
        "TrenchHQEvmCheckpoint",
        Guid.NewGuid().ToString("N"));
    var baselineProcessIds = GetEngineProcessIds();
    var client = new OnChainEngineClient(enginePath);
    var prices = new ConcurrentQueue<OnChainPriceUpdate>();
    client.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
    var evmPool = new OnChainWatchedPoolSelection
    {
        SelectedMint = selectedToken,
        Descriptor = new OnChainPoolDescriptor
        {
            PoolKey = new OnChainPoolKey
            {
                DeploymentKey = EthereumDeploymentRegistry.UniswapV2Deployment(),
                PoolId = poolAddress
            },
            PoolType = "constantProduct",
            ProgramId = EthereumDeploymentRegistry.UniswapV2Factory,
            BaseMint = selectedToken,
            QuoteMint = EthereumDeploymentRegistry.Usdc,
            BaseDecimals = 18,
            QuoteDecimals = 6,
            PairOrientation = "selectedAsToken0",
            SupportStatus = OnChainSupportStatus.Supported,
            Asset0 = new OnChainAssetKey
            {
                ChainNamespace = ChainNamespaces.Eip155,
                ChainId = EvmChainDefinitions.EthereumMainnet.ChainId,
                Address = selectedToken
            },
            Asset1 = new OnChainAssetKey
            {
                ChainNamespace = ChainNamespaces.Eip155,
                ChainId = EvmChainDefinitions.EthereumMainnet.ChainId,
                Address = EthereumDeploymentRegistry.Usdc
            }
        }
    };
    var mintBytes = Enumerable.Repeat((byte)3, 32).ToArray();
    var solanaMint = EncodeBase58(mintBytes);
    var solanaPool = new OnChainWatchedPoolSelection
    {
        SelectedMint = solanaMint,
        Descriptor = new OnChainPoolDescriptor
        {
            PoolKey = new OnChainPoolKey
            {
                ProtocolId = OnChainProtocolIds.PumpBondingCurve,
                PoolAddress = "shared-engine-curve"
            },
            PoolType = "bondingCurve",
            ProgramId = "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P",
            BaseMint = solanaMint,
            QuoteMint = "So11111111111111111111111111111111111111112",
            BaseDecimals = 6,
            QuoteDecimals = 9,
            SupportStatus = OnChainSupportStatus.Supported
        }
    };
    var configuration = CreateProviderConfiguration(
        OnChainProviderTypes.AlchemyEthereum,
        "wss://fixture.invalid",
        "https://fixture.invalid");
    configuration.Id = "evm-coordinator-profile";
    configuration.CapabilitySnapshot = new OnChainProviderCapabilitySnapshot
    {
        SafeBlock = OnChainProviderCapabilityState.Supported,
        FinalizedBlock = OnChainProviderCapabilityState.Supported
    };
    using var handler = new EvmCoordinatorRpcFixtureHandler(poolAddress);
    using var httpClient = new HttpClient(handler);
    var source = new CoordinatedEvmSource(poolAddress);
    var coordinator = new EvmStreamCoordinator(
        client,
        checkpointRoot,
        EvmChainDefinitions.EthereumMainnet,
        httpClient,
        (_, _) => source);
    try
    {
        await client.ReplaceWatchedPoolsAsync("solana:test", [solanaPool]);
        await coordinator.StartAsync(configuration, "fixture-key", [evmPool]);
        await WaitUntilAsync(
            () => coordinator.State == OnChainRecoveryState.Live
                  && prices.Any(update => update.PoolKey.PoolId == poolAddress
                                          && update.ChainPosition?.BlockNumber == 17),
            TimeSpan.FromSeconds(10),
            "Ethereum coordinator live delivery");
        await coordinator.StartAsync(configuration, "fixture-key", [evmPool]);
        await Task.Delay(100);
        AssertEqual(1, source.RunCount,
            "An unchanged Ethereum panel refresh restarted the WebSocket source.");
        AssertEqual(1, source.ObservedSubscription?.Pools.Length ?? 0,
            "The Ethereum coordinator subscribed beyond the selected non-ETH-quoted pool.");
        Assert(handler.GetLogsCount > 0,
            "The Ethereum coordinator did not run its bounded replay query.");
        var websocketRuns = source.RunCount;
        var replayCalls = handler.GetLogsCount;
        var historyReads = handler.NumericBlockRequestCount;
        var finalityReads = handler.FinalizedBlockRequestCount;
        await coordinator.StartAsync(configuration, "fixture-key", [evmPool],
            webSocketPoolIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        await WaitUntilAsync(() => coordinator.State == OnChainRecoveryState.Live,
            TimeSpan.FromSeconds(5), "Ethereum polling coordinator live state");
        var latestReads = handler.LatestBlockRequestCount;
        await WaitUntilAsync(() => handler.LatestBlockRequestCount > latestReads,
            TimeSpan.FromSeconds(3), "Ethereum one-second head poll");
        AssertEqual(websocketRuns, source.RunCount,
            "Polling opened the selected-pool WebSocket source.");
        AssertEqual(replayCalls, handler.GetLogsCount,
            "Polling replayed selected-pool swap logs.");
        AssertEqual(historyReads, handler.NumericBlockRequestCount,
            "Polling bootstrap fetched an event-recovery header window.");
        AssertEqual(finalityReads, handler.FinalizedBlockRequestCount,
            "Polling fetched finality for current-state prices.");
        await WaitUntilAsync(() => handler.BlockNumberRequestCount > 0,
            TimeSpan.FromSeconds(4), "unchanged-head number probe");
        var fullHeaderReads = handler.LatestBlockRequestCount;
        var numberProbes = handler.BlockNumberRequestCount;
        await WaitUntilAsync(() => handler.BlockNumberRequestCount > numberProbes,
            TimeSpan.FromSeconds(3), "second unchanged-head number probe");
        AssertEqual(fullHeaderReads, handler.LatestBlockRequestCount,
            "An unchanged EVM block still spent a full header request each second.");
        await WaitUntilAsync(() => handler.LatestBlockRequestCount > fullHeaderReads,
            TimeSpan.FromSeconds(6), "five-second same-height reorg check");
        var canonicalReads = handler.NumericBlockRequestCount;
        var headersBeforeActivity = handler.LatestBlockRequestCount;
        handler.EnableContiguousLatest();
        await WaitUntilAsync(() => handler.LatestBlockRequestCount >= headersBeforeActivity + 3,
            TimeSpan.FromSeconds(5), "contiguous Ethereum head polls");
        var activeHeadReads = handler.LatestBlockRequestCount;
        var activeNumberProbes = handler.BlockNumberRequestCount;
        await WaitUntilAsync(() => handler.LatestBlockRequestCount > activeHeadReads,
            TimeSpan.FromSeconds(3), "active-chain full header poll");
        AssertEqual(activeNumberProbes, handler.BlockNumberRequestCount,
            "An active chain kept making a redundant block-number probe.");
        AssertEqual(canonicalReads, handler.NumericBlockRequestCount,
            "A directly linked polled head redundantly rechecked its parent by RPC.");
        Assert(handler.SawPinnedCall,
            "The Ethereum coordinator snapshot was not pinned to a canonical block hash.");
        var evmPrice = prices.Last(update => update.PoolKey.PoolId == poolAddress);
        AssertEqual("evm-coordinator-profile", evmPrice.ProviderProfileId,
            "The coordinator did not attach its provider profile to EVM prices.");
        AssertEqual("ethereum-json-rpc:alchemyEthereum", evmPrice.SourceId,
            "The coordinator did not attach its provider source to EVM prices.");
        var checkpoint = await EvmCheckpointStore.LoadAsync(checkpointRoot, configuration.Id);
        AssertEqual(15UL, checkpoint?.SafeBlockNumber ?? 0,
            "The Ethereum coordinator did not persist its safe checkpoint.");

        await client.PublishTransactionUpdateAsync(CreatePumpTrade("shared-solana-1", mintBytes));
        await WaitUntilAsync(
            () => prices.Any(update => update.PoolKey.PoolId == "shared-engine-curve"),
            TimeSpan.FromSeconds(5),
            "simultaneous Solana price");
        await WaitUntilAsync(
            () => GetEngineProcessIds().Except(baselineProcessIds).Count() == 1,
            TimeSpan.FromSeconds(5),
            "single shared Solana/Ethereum engine process");
        var sharedProcessId = GetSingleNewEngineProcessId(baselineProcessIds);
        await coordinator.StopAsync();
        Assert(Process.GetProcessById(sharedProcessId).HasExited == false,
            "Stopping Ethereum terminated the shared engine while Solana was still active.");
        await client.PublishTransactionUpdateAsync(CreatePumpTrade("shared-solana-2", mintBytes));
        await WaitUntilAsync(
            () => prices.Count(update => update.PoolKey.PoolId == "shared-engine-curve") >= 2,
            TimeSpan.FromSeconds(5),
            "Solana delivery after Ethereum stop");
        await client.ReplaceWatchedPoolsAsync("solana:test", []);
    }
    finally
    {
        await coordinator.StopAsync();
        await client.StopAsync();
        if (Directory.Exists(checkpointRoot))
        {
            Directory.Delete(checkpointRoot, true);
        }
    }
    await WaitUntilAsync(
        () => !GetEngineProcessIds().Except(baselineProcessIds).Any(),
        TimeSpan.FromSeconds(5),
        "shared Solana/Ethereum engine stop");
}

static async Task VerifyHiddenEvmReferenceSamplingAsync(string enginePath)
{
    const string selectedToken = "0x1111111111111111111111111111111111111111";
    const string selectedPool = "0x2222222222222222222222222222222222222222";
    foreach (var chain in EvmChainDefinitions.Supported)
    {
        var selected = new OnChainWatchedPoolSelection
        {
            SelectedMint = selectedToken,
            Descriptor = new OnChainPoolDescriptor
            {
                PoolKey = new OnChainPoolKey
                {
                    DeploymentKey = new OnChainDeploymentKey
                    {
                        ChainNamespace = chain.ChainNamespace,
                        ChainId = chain.ChainId,
                        ProtocolId = OnChainProtocolIds.UniswapV2,
                        ContractAddress = "0x3333333333333333333333333333333333333333"
                    },
                    PoolId = selectedPool
                },
                BaseMint = selectedToken,
                QuoteMint = chain.WrappedNativeAssetAddress,
                SupportStatus = OnChainSupportStatus.Supported
            }
        };
        var (watched, hidden) = EvmStreamCoordinator.AddRequiredReferencePools(chain, [selected]);
        AssertEqual(2, watched.Length,
            $"{chain.DisplayName} did not retain the hidden USD reference for snapshots.");
        AssertEqual(chain.NativeUsdReferencePoolId, hidden?.Descriptor.PoolKey.PoolId,
            $"{chain.DisplayName} selected the wrong hidden USD reference.");
        var explicitReference = chain.CreateNativeUsdReferenceSelection!();
        var (explicitWatched, explicitHidden) = EvmStreamCoordinator.AddRequiredReferencePools(
            chain, [selected, explicitReference]);
        AssertEqual(2, explicitWatched.Length,
            $"{chain.DisplayName} duplicated an explicitly selected reference pool.");
        Assert(explicitHidden == null,
            $"{chain.DisplayName} would suppress a user-selected reference stream.");
        selected.Descriptor.QuoteMint = "0x4444444444444444444444444444444444444444";
        var (stableWatched, stableHidden) = EvmStreamCoordinator.AddRequiredReferencePools(
            chain, [selected]);
        AssertEqual(1, stableWatched.Length,
            $"{chain.DisplayName} added a reference without a native-quoted pool.");
        Assert(stableHidden == null,
            $"{chain.DisplayName} would sample an unused USD reference.");
    }

    var root = Path.Combine(Path.GetTempPath(), "TrenchHQHiddenReference", Guid.NewGuid().ToString("N"));
    var baselineProcessIds = GetEngineProcessIds();
    var client = new OnChainEngineClient(enginePath);
    var prices = new ConcurrentQueue<OnChainPriceUpdate>();
    client.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
    var pool = new OnChainWatchedPoolSelection
    {
        SelectedMint = selectedToken,
        Descriptor = new OnChainPoolDescriptor
        {
            PoolKey = new OnChainPoolKey
            {
                DeploymentKey = EthereumDeploymentRegistry.UniswapV2Deployment(),
                PoolId = selectedPool
            },
            PoolType = "constantProduct",
            ProgramId = EthereumDeploymentRegistry.UniswapV2Factory,
            BaseMint = selectedToken,
            QuoteMint = EthereumDeploymentRegistry.WrappedEther,
            BaseDecimals = 18,
            QuoteDecimals = 18,
            PairOrientation = "selectedAsToken0",
            SupportStatus = OnChainSupportStatus.Supported,
            Asset0 = new OnChainAssetKey
            {
                ChainNamespace = ChainNamespaces.Eip155,
                ChainId = EvmChainDefinitions.EthereumMainnetChainId,
                Address = selectedToken
            },
            Asset1 = new OnChainAssetKey
            {
                ChainNamespace = ChainNamespaces.Eip155,
                ChainId = EvmChainDefinitions.EthereumMainnetChainId,
                Address = EthereumDeploymentRegistry.WrappedEther
            }
        }
    };
    var configuration = CreateProviderConfiguration(
        OnChainProviderTypes.PublicNodeEthereum,
        "wss://fixture.invalid",
        "https://fixture.invalid");
    configuration.Id = "hidden-reference-sampling-profile";
    configuration.CapabilitySnapshot = new OnChainProviderCapabilitySnapshot
    {
        SafeBlock = OnChainProviderCapabilityState.Supported,
        FinalizedBlock = OnChainProviderCapabilityState.Supported
    };
    using var handler = new EvmCoordinatorRpcFixtureHandler(
        selectedPool, advanceLatest: true,
        referenceAddress: EthereumDeploymentRegistry.WrappedEtherUsdcReferencePool);
    using var httpClient = new HttpClient(handler);
    var source = new CoordinatedEvmSource(
        selectedPool, EvmChainDefinitions.EthereumMainnetChainId, emitHead: false);
    var coordinator = new EvmStreamCoordinator(
        client, root, EvmChainDefinitions.EthereumMainnet,
        httpClient, (_, _) => source,
        headSampleInterval: TimeSpan.FromMilliseconds(50),
        sampledSnapshotInterval: TimeSpan.FromHours(1),
        referenceSampleInterval: TimeSpan.FromMilliseconds(100));
    try
    {
        await coordinator.StartAsync(configuration, null, [pool]);
        await WaitUntilAsync(
            () => coordinator.State == OnChainRecoveryState.Live
                  && prices.Any(update => update.PoolKey.PoolId == selectedPool
                                          && update.PriceUsd != null),
            TimeSpan.FromSeconds(10),
            "hidden reference bootstrap conversion");
        AssertEqual(1, source.ObservedSubscription?.Pools.Length ?? 0,
            "The hidden reference was included in the paid WebSocket subscription.");
        AssertEqual(selectedPool, source.ObservedSubscription?.Pools[0].Descriptor.PoolKey.PoolId,
            "The selected pool was removed from the live WebSocket subscription.");
        Assert(handler.GetLogsCount > 0,
            "The hidden-reference test did not exercise RPC log replay.");
        Assert(handler.LogFilters.All(filter => !filter.Contains(
                   EthereumDeploymentRegistry.WrappedEtherUsdcReferencePool,
                   StringComparison.OrdinalIgnoreCase)),
            "The hidden reference was included in RPC log replay.");
        var bootstrapReferenceCalls = handler.ReferenceCallCount;
        await WaitUntilAsync(
            () => handler.ReferenceCallCount >= bootstrapReferenceCalls + 2,
            TimeSpan.FromSeconds(5),
            "periodic pinned hidden-reference snapshot");
        Assert(handler.SawPinnedCall,
            "The hidden reference snapshot was not pinned to a canonical block.");
        Assert(handler.MulticallRequestCount >= 2 && handler.DirectReferenceCallCount == 0,
            "The hidden reference still spends two separate RPC calls per snapshot.");
        AssertEqual(0, handler.DirectSelectedCallCount,
            "The selected pool was fetched separately from its hidden reference.");
        Assert(prices.Any(update => update.PoolKey.PoolId == selectedPool
                                    && update.ReferenceSourceId?.StartsWith(
                                        "ethereum:uniswap-v3:WETH/USDC:500:",
                                        StringComparison.Ordinal) == true),
            "A sampled hidden reference lost on-chain price provenance.");
        await coordinator.StartAsync(configuration, null,
            [pool, EthereumDeploymentRegistry.CreateEthUsdReferenceSelection()]);
        await WaitUntilAsync(
            () => source.RunCount == 2 && coordinator.State == OnChainRecoveryState.Live,
            TimeSpan.FromSeconds(10),
            "explicit reference restored live subscription");
        AssertEqual(2, source.ObservedSubscription?.Pools.Length ?? 0,
            "Changing from hidden to explicitly selected reference did not restart the stream.");
        Assert(source.ObservedSubscription?.Pools.Any(selection => string.Equals(
                   selection.Descriptor.PoolKey.PoolId,
                   EthereumDeploymentRegistry.WrappedEtherUsdcReferencePool,
                   StringComparison.OrdinalIgnoreCase)) == true,
            "An explicitly selected reference pool was not included in the live stream.");
        var numericReads = handler.NumericBlockRequestCount;
        var finalityReads = handler.FinalizedBlockRequestCount;
        var replayReads = handler.GetLogsCount;
        await coordinator.StartAsync(configuration, null, [pool],
            webSocketPoolIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        await WaitUntilAsync(() => coordinator.State == OnChainRecoveryState.Live,
            TimeSpan.FromSeconds(5), "fast-chain polling bootstrap");
        var selectedReads = handler.DirectSelectedCallCount;
        var batchReads = handler.MulticallRequestCount;
        await WaitUntilAsync(() => handler.MulticallRequestCount >= batchReads + 2,
            TimeSpan.FromSeconds(5), "fast-chain selected and reference polling batches");
        AssertEqual(selectedReads, handler.DirectSelectedCallCount,
            "A due reference used a separate RPC from selected state.");
        AssertEqual(numericReads, handler.NumericBlockRequestCount,
            "Polling skipped blocks triggered historical canonical-tip reads.");
        AssertEqual(finalityReads, handler.FinalizedBlockRequestCount,
            "Polling used event finality requests.");
        AssertEqual(replayReads, handler.GetLogsCount, "Polling performed event replay.");
        Console.WriteLine("POLL REQUESTS | fast chain | each due selected+reference sample=1 latest header + 1 Multicall | history=0 | finality=0 | replay=0");
    }
    finally
    {
        await coordinator.StopAsync();
        await client.StopAsync();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
    await WaitUntilAsync(
        () => !GetEngineProcessIds().Except(baselineProcessIds).Any(),
        TimeSpan.FromSeconds(5),
        "hidden-reference engine stop");
}

static void VerifyEvmMulticall3Codec()
{
    const string target = "0x1111111111111111111111111111111111111111";
    var call = EvmMulticall3.EncodePair(
        target, EthereumAbi.Slot0Selector, EthereumAbi.LiquiditySelector);
    Assert(call.StartsWith("0xbce38bd7", StringComparison.Ordinal)
           && call.Split(target[2..], StringSplitOptions.None).Length == 3,
        "The paired state call did not encode both calls to the selected contract.");
    var v4First = EthereumAbi.UniswapV4StateViewSlot0Selector + new string('a', 64);
    var v4Second = EthereumAbi.UniswapV4StateViewLiquiditySelector + new string('a', 64);
    var v4Call = EvmMulticall3.EncodePair(target, v4First, v4Second);
    Assert(v4Call.Contains(v4First[2..], StringComparison.Ordinal)
           && v4Call.Contains(v4Second[2..], StringComparison.Ordinal)
           && v4Call.Substring(10 + 64 * 4, 64).EndsWith("e0", StringComparison.Ordinal),
        "The paired V4 state call lost its bytes32 pool ID or padded call offset.");

    var first = "0x" + new string('a', 448);
    var second = "0x" + new string('b', 64);
    var response = EvmCoordinatorRpcFixtureHandler.EncodePairResult(first, second);
    Assert(EvmMulticall3.TryDecodePair(response, out var decodedFirst, out var decodedSecond)
           && decodedFirst == first && decodedSecond == second,
        "A valid paired state response lost a result or its order.");
    Assert(!EvmMulticall3.TryDecodePair(ReplaceWord(response, 128, 0), out _, out _),
        "A failed first subcall was accepted as a snapshot.");
    Assert(!EvmMulticall3.TryDecodePair(ReplaceWord(response, 128 + 96 + 224, 0), out _, out _),
        "A failed second subcall was accepted as a snapshot.");
    Assert(!EvmMulticall3.TryDecodePair(ReplaceWord(response, 32, 3), out _, out _),
        "A malformed paired result count was accepted.");
    Assert(!EvmMulticall3.TryDecodePair(ReplaceWord(response, 64, 4096), out _, out _),
        "An out-of-range paired result offset was accepted.");
    Assert(!EvmMulticall3.TryDecodePair(ReplaceWord(response, 96, 64), out _, out _),
        "Overlapping paired results were accepted.");
    Assert(!EvmMulticall3.TryDecodePair(response[..^64], out _, out _),
        "A truncated paired state response was accepted.");
    Assert(!EvmMulticall3.TryDecodePair("0xnot-hex", out _, out _),
        "A non-hex paired state response was accepted.");

    var threeCalls = new (string Target, string Data)[]
    {
        (target, EthereumAbi.GetReservesSelector),
        (target, EthereumAbi.Slot0Selector),
        (target, EthereumAbi.LiquiditySelector)
    };
    var grouped = EvmMulticall3.EncodeCalls(threeCalls);
    Assert(grouped.StartsWith("0xbce38bd7", StringComparison.Ordinal)
           && grouped.Split(target[2..], StringSplitOptions.None).Length == 4,
        "A three-call aggregate omitted or reordered a pool read.");
    var threeResults = EvmCoordinatorRpcFixtureHandler.EncodeResults(first, second, first);
    Assert(EvmMulticall3.TryDecodeResults(threeResults, 3, out var decoded)
           && decoded.SequenceEqual(new[] { first, second, first }),
        "A mixed three-call aggregate lost state or ordering.");
    Assert(!EvmMulticall3.TryDecodeResults(threeResults, 2, out _)
           && !EvmMulticall3.TryDecodeResults(ReplaceWord(threeResults, 64, 32), 3, out _)
           && !EvmMulticall3.TryDecodeResults(ReplaceWord(threeResults, 160, 0), 3, out _)
           && !EvmMulticall3.TryDecodeResults(threeResults[..^64], 3, out _),
        "A count mismatch, overlapping offset, failed subcall, or truncated batch was accepted.");
    var oversizedRejected = false;
    try
    {
        EvmMulticall3.EncodeCalls(Enumerable.Repeat(threeCalls[0], EvmMulticall3.MaximumCalls + 1).ToArray());
    }
    catch (ArgumentException)
    {
        oversizedRejected = true;
    }
    Assert(oversizedRejected, "An unbounded EVM aggregate was accepted.");

    static string ReplaceWord(string data, int byteOffset, int value) =>
        data[..(2 + byteOffset * 2)]
        + value.ToString("x64")
        + data[(2 + (byteOffset + 32) * 2)..];
}

static async Task VerifyTickerProviderMatrixAsync(string enginePath)
{
    var presets = typeof(OnChainProviderTypes)
        .GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(field => OnChainProviderCatalog.Get((string)field.GetRawConstantValue()!)).ToArray();
    AssertEqual(34, presets.Length, "Update the ticker coverage matrix for the changed provider catalog.");
    foreach (var preset in presets)
    {
        var configuration = CreateProviderConfiguration(preset.ProviderType,
            preset.StreamTransport == OnChainStreamTransport.YellowstoneGrpc
                ? "https://fixture.invalid" : "wss://fixture.invalid", "https://fixture.invalid");
        using var usage = new OnChainProviderUsage();
        configuration.Usage = usage.CreateScope(configuration);
        var engine = new OnChainEngineClient(enginePath);
        var prices = new ConcurrentQueue<OnChainPriceUpdate>();
        engine.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
        var checkpoint = Path.Combine(Path.GetTempPath(), "TrenchHQTickerMatrix", Guid.NewGuid().ToString("N"));
        if (preset.StreamTransport == OnChainStreamTransport.EvmWebSocket)
        {
            var chain = EvmChainDefinitions.Supported.Single(chain => chain.ChainId == preset.ChainId);
            var pool = chain.CreateNativeUsdReferenceSelection!();
            using var handler = new EvmCoordinatorRpcFixtureHandler(pool.Descriptor.PoolKey.PoolId,
                advanceLatest: true, referenceAddress: pool.Descriptor.PoolKey.PoolId);
            using var http = new HttpClient(handler);
            var coordinator = new EvmStreamCoordinator(engine, checkpoint, chain, http,
                (_, _) => throw new InvalidOperationException("Polling opened an EVM socket."));
            try
            {
                await coordinator.StartAsync(configuration, "fixture-key", [pool],
                    webSocketPoolIds: new HashSet<string>());
                await WaitUntilAsync(() => prices.Count >= 2, TimeSpan.FromSeconds(5),
                    $"{preset.ProviderType} advancing polling samples");
            }
            finally
            {
                await coordinator.StopAsync();
                await engine.StopAsync();
            }
            AssertEqual(2, handler.LatestBlockRequestCount, $"{preset.ProviderType} redundant latest header.");
            AssertEqual(2, handler.MulticallRequestCount, $"{preset.ProviderType} unbatched pool state.");
            var infuraCompatibility = preset.ProviderType == OnChainProviderTypes.InfuraEthereum;
            AssertEqual(infuraCompatibility ? 2 : 0, handler.NumericBlockRequestCount,
                $"{preset.ProviderType} unexpected historical/verification header work.");
            AssertEqual(infuraCompatibility ? 0 : 2, handler.PinnedCallCount,
                $"{preset.ProviderType} lost canonical hash pinning.");
            AssertEqual(infuraCompatibility ? 2 : 0, handler.NumberedCallCount,
                $"{preset.ProviderType} unexpected numbered state calls.");
            AssertEqual(0, handler.GetLogsCount, $"{preset.ProviderType} replayed polling history.");
            AssertEqual(0, handler.FinalizedBlockRequestCount, $"{preset.ProviderType} fetched polling finality.");
            AssertEqual(0, handler.BlockNumberRequestCount, $"{preset.ProviderType} probed advancing heads twice.");
            Console.WriteLine($"PROVIDER POLL | {preset.ProviderType} | prices={prices.Count} | latest=2 | aggregate=2 | canonicalCheck={handler.NumericBlockRequestCount}");
        }
        else
        {
            Assert(OnChainStreamCoordinator.CreateSource(configuration, "fixture-key")
                   is SolanaRpcSampleStreamSource, $"{preset.ProviderType} did not select RPC polling.");
            var eventSource = OnChainStreamCoordinator.CreateSource(configuration, "fixture-key", true);
            Assert(preset.StreamTransport == OnChainStreamTransport.YellowstoneGrpc
                    ? eventSource is YellowstoneStreamSource : eventSource is SolanaWebSocketStreamSource,
                $"{preset.ProviderType} lost its explicit event transport.");
            var quoteBytes = Enumerable.Repeat((byte)9, 32).ToArray();
            var pool = new OnChainWatchedPoolSelection
            {
                SelectedMint = EncodeBase58(Enumerable.Repeat((byte)3, 32).ToArray()),
                Descriptor = new OnChainPoolDescriptor
                {
                    PoolKey = new OnChainPoolKey
                    {
                        ProtocolId = OnChainProtocolIds.PumpBondingCurve,
                        PoolAddress = "provider-matrix-curve"
                    },
                    PoolType = "bondingCurve",
                    ProgramId = "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P",
                    BaseMint = EncodeBase58(Enumerable.Repeat((byte)3, 32).ToArray()),
                    QuoteMint = EncodeBase58(quoteBytes),
                    BaseDecimals = 6,
                    QuoteDecimals = 9,
                    SupportStatus = OnChainSupportStatus.Supported
                }
            };
            using var handler = new SolanaRpcFixtureHandler("fixture-key", pool.SelectedMint,
                pool.Descriptor.PoolKey.PoolAddress, "unused", new Dictionary<string, RpcFixtureAccount>
                {
                    [pool.Descriptor.PoolKey.PoolAddress] = new(pool.Descriptor.ProgramId,
                        CreatePumpCurveAccount(quoteBytes))
                });
            var coordinator = new OnChainStreamCoordinator(engine, checkpoint,
                (profile, key, events) => events
                    ? throw new InvalidOperationException("Polling opened a Solana stream.")
                    : new SolanaRpcSampleStreamSource(profile, key, handler),
                (_, _, _, _) => throw new InvalidOperationException("Polling repeated startup reconciliation."));
            try
            {
                await coordinator.StartAsync(configuration, "fixture-key", [pool]);
                await WaitUntilAsync(() => prices.Any(price => price.SpotPriceQuote != null && price.Slot == 123),
                    TimeSpan.FromSeconds(5), $"{preset.ProviderType} atomic polling price");
            }
            finally
            {
                await coordinator.StopAsync();
                await engine.StopAsync();
            }
            AssertEqual(1, handler.Methods.Count, $"{preset.ProviderType} duplicated startup acquisition.");
            AssertEqual("getAccountInfo", handler.Methods.Single(),
                $"{preset.ProviderType} used the multi-account method for one account.");
            Console.WriteLine($"PROVIDER POLL | {preset.ProviderType} | prices={prices.Count} | getAccountInfo=1 | stream=0");
        }
        var measured = usage.Snapshot(OnChainProviderUsage.GroupKey(configuration));
        Assert(!measured.UnknownCost, $"{preset.ProviderType} has an unpriced polling method.");
        AssertEqual(preset.StreamTransport == OnChainStreamTransport.EvmWebSocket
                ? preset.ProviderType == OnChainProviderTypes.InfuraEthereum ? 6L : 4L : 1L,
            measured.RpcRequests, $"{preset.ProviderType} usage meter missed or duplicated an RPC.");
        Console.WriteLine($"PROVIDER USAGE | {preset.ProviderType} | units={measured.Used} {measured.Unit}");
        foreach (var status in new[] { 401, 402, 403, 429 })
        {
            using var caseUsage = new OnChainProviderUsage();
            configuration.Usage = caseUsage.CreateScope(configuration);
            using var http = new HttpClient(new FixedHttpStatusHandler((System.Net.HttpStatusCode)status));
            try
            {
                if (preset.StreamTransport == OnChainStreamTransport.EvmWebSocket)
                {
                    await new EvmJsonRpcClient(http, configuration, "fixture-key")
                        .GetBlockAsync("latest", CancellationToken.None);
                }
                else
                {
                    await new SolanaRpcClient(http, configuration, "fixture-key")
                        .GetAccountInfoAsync("fixture-account", OnChainCommitment.Confirmed, CancellationToken.None);
                }
                throw new InvalidOperationException($"{preset.ProviderType} accepted HTTP {status}.");
            }
            catch (OnChainUsageBudgetException)
            {
                AssertEqual(402, status, $"{preset.ProviderType} treated a non-quota failure as quota exhaustion.");
            }
            catch (EvmJsonRpcException exception)
            {
                AssertEqual(status is 402 or 429 ? EvmRpcFailureKind.RateLimited
                    : preset.RequiresCredential ? EvmRpcFailureKind.AuthenticationRejected : EvmRpcFailureKind.RpcError,
                    exception.Kind, $"{preset.ProviderType} misclassified HTTP {status}.");
            }
            catch (SolanaRpcException exception)
            {
                AssertEqual(status is 402 or 429, exception.IsRateLimited, $"{preset.ProviderType} rate-limit classification.");
                AssertEqual(status is 401 or 403, exception.IsAccessRejected, $"{preset.ProviderType} access classification.");
            }
        }
        var backupPreset = presets.First(candidate => candidate.ChainNamespace == preset.ChainNamespace
            && candidate.ChainId == preset.ChainId && candidate.ProviderType != preset.ProviderType
            && candidate.RequiresCredential);
        var backup = CreateProviderConfiguration(backupPreset.ProviderType,
            backupPreset.StreamTransport == OnChainStreamTransport.YellowstoneGrpc ? "https://fixture.invalid" : "wss://fixture.invalid",
            "https://fixture.invalid");
        var network = OnChainProviderConfigurationStore.GetNetworkKey(preset.ChainNamespace, preset.ChainId);
        var document = new OnChainProviderConfigurationDocument
        {
            Configurations = [configuration, backup],
            SelectedConfigurationIds = new() { [network] = configuration.Id },
            FallbackConfigurationIds = new() { [network] = [backup.Id] }
        };
        var saved = JsonSerializer.Serialize(document);
        var service = new OnChainProviderConfigurationService(document, () => true);
        var active = service.GetSelectedConfiguration(preset.ChainNamespace, preset.ChainId)!;
        var remaining = active.Id == configuration.Id ? backup : configuration;
        await service.TryFailoverAsync(active.Id, OnChainProviderFailureKind.Authentication);
        AssertEqual(remaining.Id, service.GetSelectedConfiguration(preset.ChainNamespace, preset.ChainId)?.Id,
            $"{preset.ProviderType} did not select another saved provider automatically.");
        AssertEqual(saved, JsonSerializer.Serialize(document), $"{preset.ProviderType} rewrote the saved route.");
    }
    Console.WriteLine("PASS: all 34 presets, polling request budgets, HTTP failure classification and automatic provider selection.");
}

static async Task VerifyInfuraKnownHeaderAsync()
{
    var configuration = CreateProviderConfiguration(OnChainProviderTypes.InfuraEthereum,
        "wss://fixture.invalid", "https://fixture.invalid");
    foreach (var replaced in new[] { false, true })
    {
        using var handler = new InfuraBlockReferenceFallbackFixtureHandler
        {
            ReplaceBlockAfterCall = replaced
        };
        using var http = new HttpClient(handler);
        try
        {
            await new EvmJsonRpcClient(http, configuration, "fixture-key").CallAsync(
                EvmChainDefinitions.EthereumMainnet.WrappedNativeAssetAddress,
                EthereumAbi.DecimalsSelector,
                new EvmRpcBlockHeader
                {
                    Number = 16,
                    Hash = InfuraBlockReferenceFallbackFixtureHandler.BlockHash
                }, CancellationToken.None);
            Assert(!replaced, "Infura published state after the numbered block was replaced.");
        }
        catch (EvmJsonRpcException exception) when (replaced
            && exception.Kind == EvmRpcFailureKind.InvalidResponse)
        {
        }
        Assert(handler.UsedVerifiedBlockNumberCall, "Infura did not reuse the known header's number.");
        AssertEqual(0, handler.BlockByHashRequestCount, "Infura fetched an already known block header.");
        AssertEqual(1, handler.BlockByNumberRequestCount, "Infura omitted the post-read canonical check.");
    }
    Console.WriteLine("PASS: Infura known-header reuse and post-read replacement rejection.");
}

static async Task VerifyPublicTickerPollingAsync(string enginePath)
{
    foreach (var chain in EvmChainDefinitions.Supported)
    {
        var preset = OnChainProviderCatalog.Get(chain.ChainId switch
        {
            EvmChainDefinitions.EthereumMainnetChainId => OnChainProviderTypes.PublicNodeEthereum,
            EvmChainDefinitions.BaseMainnetChainId => OnChainProviderTypes.BasePublic,
            EvmChainDefinitions.BnbMainnetChainId => OnChainProviderTypes.PublicNodeBnb,
            EvmChainDefinitions.RobinhoodMainnetChainId => OnChainProviderTypes.PublicNodeRobinhood,
            _ => throw new InvalidOperationException("No public ticker test route.")
        });
        var configuration = OnChainProviderConfigurationStore.CreateConfiguration(preset);
        var engine = new OnChainEngineClient(enginePath);
        var prices = new ConcurrentQueue<OnChainPriceUpdate>();
        engine.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
        using var handler = new TickerRequestCountingHandler();
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var coordinator = new EvmStreamCoordinator(engine,
            Path.Combine(Path.GetTempPath(), "TrenchHQTickerPublic", Guid.NewGuid().ToString("N")),
            chain, http, (_, _) => throw new InvalidOperationException("Polling opened a socket."));
        try
        {
            await coordinator.StartAsync(configuration, null, [chain.CreateNativeUsdReferenceSelection!()],
                webSocketPoolIds: new HashSet<string>());
            await WaitUntilAsync(() => prices.Any(price => price.SpotPriceQuote != null),
                TimeSpan.FromSeconds(20), $"{chain.CatalogChainId} public polling price");
            await Task.Delay(TimeSpan.FromSeconds(4));
            AssertEqual(0, handler.Counts.GetValueOrDefault("eth_getLogs"), "Public polling replayed logs.");
            Console.WriteLine($"PUBLIC POLL | {chain.CatalogChainId} | prices={prices.Count} | block={prices.Last().ChainPosition?.BlockNumber} | methods={JsonSerializer.Serialize(handler.Counts)}");
        }
        finally
        {
            await coordinator.StopAsync();
            await engine.StopAsync();
        }
    }
}

static async Task VerifyProviderRouteRecoveryAsync()
{
    await VerifyEvmProviderRouteRecoveryAsync(OnChainProviderTypes.AlchemyEthereum,
        OnChainProviderTypes.DrpcEthereum, OnChainProviderTypes.PublicNodeEthereum);
    await VerifyEvmProviderRouteRecoveryAsync(OnChainProviderTypes.InfuraBase,
        OnChainProviderTypes.ChainstackBase, OnChainProviderTypes.BasePublic);
    await VerifyEvmProviderRouteRecoveryAsync(OnChainProviderTypes.DrpcBnb,
        OnChainProviderTypes.InfuraBnb, OnChainProviderTypes.PublicNodeBnb);
    await VerifyEvmProviderRouteRecoveryAsync(OnChainProviderTypes.QuickNodeRobinhood,
        OnChainProviderTypes.ChainstackRobinhood, OnChainProviderTypes.PublicNodeRobinhood);

    var primary = OnChainProviderConfigurationStore.CreateConfiguration(
        OnChainProviderCatalog.Get(OnChainProviderTypes.HeliusWebSocket));
    var backup = OnChainProviderConfigurationStore.CreateConfiguration(
        OnChainProviderCatalog.Get(OnChainProviderTypes.AlchemyWebSocket));
    var network = OnChainProviderConfigurationStore.GetNetworkKey(primary.ChainNamespace, primary.ChainId);
    var document = new OnChainProviderConfigurationDocument
    {
        Configurations = [primary, backup],
        SelectedConfigurationIds = new() { [network] = primary.Id },
        FallbackConfigurationIds = new() { [network] = [backup.Id] }
    };
    var service = new OnChainProviderConfigurationService(document, () => true,
        TimeSpan.FromMilliseconds(50));
    if (service.GetSelectedConfiguration()?.Id != primary.Id) (primary, backup) = (backup, primary);
    await service.TryFailoverAsync(primary.Id, OnChainProviderFailureKind.Authentication);
    await service.TryFailoverAsync(backup.Id, OnChainProviderFailureKind.Transport);
    Assert(service.GetSelectedConfiguration() == null, "Solana invented a public fallback.");
    await WaitUntilAsync(() => service.GetSelectedConfiguration()?.Id == backup.Id,
        TimeSpan.FromSeconds(2), "Solana exhausted-route retry without rejected primary");
    await service.TryFailoverAsync(backup.Id, OnChainProviderFailureKind.Authentication);
    await Task.Delay(150);
    Assert(service.GetSelectedConfiguration() == null, "Solana retried access-rejected routes.");
    Console.WriteLine("PASS: Solana configured-backup exhaustion and transient recovery.");
}

static async Task VerifyEvmProviderRouteRecoveryAsync(string primaryType, string backupType, string publicType)
{
    var primary = OnChainProviderConfigurationStore.CreateConfiguration(
        OnChainProviderCatalog.Get(primaryType));
    var backup = OnChainProviderConfigurationStore.CreateConfiguration(
        OnChainProviderCatalog.Get(backupType));
    foreach (var profile in new[] { primary, backup })
    {
        if (string.IsNullOrEmpty(profile.RpcEndpoint)) profile.RpcEndpoint = "https://fixture.invalid";
        if (string.IsNullOrEmpty(profile.StreamEndpoint)) profile.StreamEndpoint = "wss://fixture.invalid";
    }
    var publicNode = OnChainProviderConfigurationStore.CreateConfiguration(
        OnChainProviderCatalog.Get(publicType));
    var network = OnChainProviderConfigurationStore.GetNetworkKey(primary.ChainNamespace, primary.ChainId);
    var document = new OnChainProviderConfigurationDocument
    {
        Configurations = [primary, backup, publicNode],
        SelectedConfigurationIds = new() { [network] = primary.Id },
        FallbackConfigurationIds = new() { [network] = [backup.Id] }
    };
    var saved = JsonSerializer.Serialize(document);
    var online = false;
    var service = new OnChainProviderConfigurationService(document, () => online,
        TimeSpan.FromMilliseconds(50));
    if (service.GetSelectedConfiguration(primary.ChainNamespace, primary.ChainId)?.Id != primary.Id)
        (primary, backup) = (backup, primary);
    string? Selected() => service.GetSelectedConfiguration(primary.ChainNamespace, primary.ChainId)?.Id;
    AssertEqual(OnChainProviderFailoverOutcome.Ignored,
        await service.TryFailoverAsync(primary.Id, OnChainProviderFailureKind.Transport),
        "Offline transport failure exhausted the route.");
    AssertEqual(primary.Id, Selected(), "Offline failure changed providers.");
    online = true;
    AssertEqual(OnChainProviderFailoverOutcome.Switched,
        await service.TryFailoverAsync(primary.Id, OnChainProviderFailureKind.Authentication),
        "Rejected primary did not switch.");
    AssertEqual(backup.Id, Selected(), "Private backup did not precede PublicNode.");
    AssertEqual(OnChainProviderFailoverOutcome.Ignored,
        await service.TryFailoverAsync(primary.Id, OnChainProviderFailureKind.Transport),
        "An old session advanced the new route.");
    await service.TryFailoverAsync(backup.Id, OnChainProviderFailureKind.RateLimited);
    AssertEqual(publicNode.Id, Selected(), "Automatic PublicNode fallback was omitted.");
    await service.TryFailoverAsync(publicNode.Id, OnChainProviderFailureKind.Transport);
    Assert(Selected() == null, "Exhaustion did not show unavailable during cooldown.");
    online = false;
    await Task.Delay(150);
    Assert(Selected() == null, "Cooldown retried while offline.");
    online = true;
    await WaitUntilAsync(() => Selected() == backup.Id, TimeSpan.FromSeconds(2),
        "transient route retry without reusing rejected credentials");
    await service.TryFailoverAsync(backup.Id, OnChainProviderFailureKind.Authentication);
    await service.TryFailoverAsync(publicNode.Id, OnChainProviderFailureKind.Authentication);
    await Task.Delay(200);
    Assert(Selected() == null, "Access-rejected routes were retried automatically.");
    AssertEqual(saved, JsonSerializer.Serialize(document), "Runtime failover rewrote the saved route.");
    Console.WriteLine($"PASS: {primary.ChainId} offline recovery, private/PublicNode fallback, cooldown, and access rejection.");
}

static async Task VerifyRobinhoodSampledHeadCoordinatorAsync(string enginePath)
{
    const string selectedToken = "0x1111111111111111111111111111111111111111";
    const string poolAddress = "0x2222222222222222222222222222222222222222";
    var checkpointRoot = Path.Combine(
        Path.GetTempPath(),
        "TrenchHQRobinhoodSampledHeadCheckpoint",
        Guid.NewGuid().ToString("N"));
    var baselineProcessIds = GetEngineProcessIds();
    var client = new OnChainEngineClient(enginePath);
    var prices = new ConcurrentQueue<OnChainPriceUpdate>();
    client.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
    var pool = new OnChainWatchedPoolSelection
    {
        SelectedMint = selectedToken,
        Descriptor = new OnChainPoolDescriptor
        {
            PoolKey = new OnChainPoolKey
            {
                DeploymentKey = RobinhoodDeploymentRegistry.Catalog.Deployment(
                    OnChainProtocolIds.UniswapV2,
                    RobinhoodDeploymentRegistry.UniswapV2Factory),
                PoolId = poolAddress
            },
            PoolType = "constantProduct",
            ProgramId = RobinhoodDeploymentRegistry.UniswapV2Factory,
            BaseMint = selectedToken,
            QuoteMint = RobinhoodDeploymentRegistry.Usdg,
            BaseDecimals = 18,
            QuoteDecimals = 18,
            PairOrientation = "selectedAsToken0",
            SupportStatus = OnChainSupportStatus.Supported,
            Asset0 = new OnChainAssetKey
            {
                ChainNamespace = ChainNamespaces.Eip155,
                ChainId = EvmChainDefinitions.RobinhoodMainnetChainId,
                Address = selectedToken
            },
            Asset1 = new OnChainAssetKey
            {
                ChainNamespace = ChainNamespaces.Eip155,
                ChainId = EvmChainDefinitions.RobinhoodMainnetChainId,
                Address = RobinhoodDeploymentRegistry.Usdg
            }
        }
    };
    var configuration = CreateProviderConfiguration(
        OnChainProviderTypes.CustomRobinhood,
        "wss://fixture.invalid",
        "https://fixture.invalid");
    configuration.Id = "robinhood-sampled-head-profile";
    configuration.CapabilitySnapshot = new OnChainProviderCapabilitySnapshot
    {
        SafeBlock = OnChainProviderCapabilityState.Supported,
        FinalizedBlock = OnChainProviderCapabilityState.Supported
    };
    using var handler = new EvmCoordinatorRpcFixtureHandler(
        poolAddress, advanceLatest: true, inconsistentFinality: true);
    using var httpClient = new HttpClient(handler);
    var source = new CoordinatedEvmSource(
        poolAddress,
        EvmChainDefinitions.RobinhoodMainnetChainId,
        emitHead: false);
    var coordinator = new EvmStreamCoordinator(
        client,
        checkpointRoot,
        EvmChainDefinitions.RobinhoodMainnet,
        httpClient,
        (_, _) => source,
        headSampleInterval: TimeSpan.FromMilliseconds(50),
        sampledSnapshotInterval: TimeSpan.FromMilliseconds(150));
    try
    {
        await coordinator.StartAsync(configuration, null, [pool]);
        await WaitUntilAsync(
            () => coordinator.State == OnChainRecoveryState.Live
                  && prices.Any(update => update.PoolKey.PoolId == poolAddress
                                          && update.ChainPosition?.BlockNumber == 17),
            TimeSpan.FromSeconds(10),
            "Robinhood log-only live delivery");
        var bootstrapLogRequests = handler.GetLogsCount;
        var bootstrapPinnedCalls = handler.PinnedCallCount;
        await WaitUntilAsync(
            () => handler.LatestBlockRequestCount >= 4
                  && handler.PinnedCallCount > bootstrapPinnedCalls,
            TimeSpan.FromSeconds(5),
            "Robinhood sampled canonical heads and periodic pinned snapshot");
        AssertEqual(1, source.RunCount,
            "Robinhood sampled heads restarted the log-only WebSocket source.");
        AssertEqual(bootstrapLogRequests, handler.GetLogsCount,
            "A sampled Robinhood head replayed every skipped high-frequency block.");
        Assert(handler.SawPinnedCall,
            "Robinhood sampled-head processing did not retain canonical pinned snapshots.");
        Assert(handler.FinalizedBlockRequestCount > 0
               && coordinator.State == OnChainRecoveryState.Live,
            "Inconsistent public finality restarted an otherwise live price stream.");
        Assert(await EvmCheckpointStore.LoadAsync(checkpointRoot, configuration.Id) == null,
            "An inconsistent provider finality sample became a safe checkpoint.");
    }
    finally
    {
        await coordinator.StopAsync();
        await client.StopAsync();
        if (Directory.Exists(checkpointRoot))
        {
            Directory.Delete(checkpointRoot, true);
        }
    }
    await WaitUntilAsync(
        () => !GetEngineProcessIds().Except(baselineProcessIds).Any(),
        TimeSpan.FromSeconds(5),
        "Robinhood sampled-head engine stop");
}

static byte[] CreateMintAccount(byte decimals)
{
    var data = new byte[82];
    data[44] = decimals;
    data[45] = 1;
    return data;
}

static byte[] CreateToken2022MintAccount(
    byte decimals,
    params (ushort ExtensionType, ushort ExtensionLength)[] extensions)
{
    var data = new byte[166 + extensions.Sum(static extension => 4 + extension.ExtensionLength)];
    data[44] = decimals;
    data[45] = 1;
    data[165] = 1;
    var offset = 166;
    foreach (var extension in extensions)
    {
        BitConverter.GetBytes(extension.ExtensionType).CopyTo(data, offset);
        BitConverter.GetBytes(extension.ExtensionLength).CopyTo(data, offset + 2);
        offset += 4 + extension.ExtensionLength;
    }
    return data;
}

static byte[] CreatePumpCurveAccount(byte[] quoteMint)
{
    using var stream = new MemoryStream();
    stream.Write([23, 183, 248, 55, 96, 216, 172, 96]);
    foreach (var value in new[] { 1_000_000UL, 2_000_000_000UL, 500_000UL, 1_000_000_000UL, 1_000_000UL })
    {
        stream.Write(BitConverter.GetBytes(value));
    }
    stream.WriteByte(0);
    stream.Write(new byte[32]);
    stream.WriteByte(0);
    stream.WriteByte(0);
    stream.Write(quoteMint);
    return stream.ToArray();
}

static byte[] CreatePumpSwapAccount(byte[] baseMint, byte[] quoteMint)
{
    using var stream = new MemoryStream();
    stream.Write([241, 154, 109, 4, 17, 177, 109, 188]);
    stream.WriteByte(254);
    stream.Write(BitConverter.GetBytes((ushort)1));
    stream.Write(Enumerable.Repeat((byte)1, 32).ToArray());
    stream.Write(baseMint);
    stream.Write(quoteMint);
    stream.Write(Enumerable.Repeat((byte)6, 32).ToArray());
    stream.Write(Enumerable.Repeat((byte)4, 32).ToArray());
    stream.Write(Enumerable.Repeat((byte)5, 32).ToArray());
    stream.Write(BitConverter.GetBytes(1_000_000UL));
    stream.Write(Enumerable.Repeat((byte)7, 32).ToArray());
    stream.WriteByte(0);
    stream.WriteByte(0);
    stream.Write(new byte[16]);
    stream.Write(new byte[40]);
    return stream.ToArray();
}

static byte[] CreateMeteoraDlmmAccount(
    byte[] tokenXMint,
    byte[] tokenYMint,
    byte[] reserveX,
    byte[] reserveY)
{
    var data = new byte[904];
    new byte[] { 33, 11, 49, 98, 181, 101, 177, 13 }.CopyTo(data, 0);
    BitConverter.GetBytes(-12).CopyTo(data, 76);
    BitConverter.GetBytes((ushort)25).CopyTo(data, 80);
    tokenXMint.CopyTo(data, 88);
    tokenYMint.CopyTo(data, 120);
    reserveX.CopyTo(data, 152);
    reserveY.CopyTo(data, 184);
    return data;
}

static byte[] CreateMeteoraDammV2Account(
    byte[] tokenAMint,
    byte[] tokenBMint,
    byte[] tokenAVault,
    byte[] tokenBVault)
{
    var data = new byte[1112];
    new byte[] { 241, 154, 109, 4, 17, 177, 109, 188 }.CopyTo(data, 0);
    tokenAMint.CopyTo(data, 168);
    tokenBMint.CopyTo(data, 200);
    tokenAVault.CopyTo(data, 232);
    tokenBVault.CopyTo(data, 264);
    BitConverter.GetBytes(1UL).CopyTo(data, 464);
    data[696] = 1;
    return data;
}

static byte[] CreateMeteoraDammV1Account(
    byte[] tokenAMint,
    byte[] tokenBMint,
    byte[] vaultStateA,
    byte[] vaultStateB,
    byte[] vaultLpA,
    byte[] vaultLpB)
{
    var data = new byte[952];
    new byte[] { 241, 154, 109, 4, 17, 177, 109, 188 }.CopyTo(data, 0);
    tokenAMint.CopyTo(data, 40);
    tokenBMint.CopyTo(data, 72);
    vaultStateA.CopyTo(data, 104);
    vaultStateB.CopyTo(data, 136);
    vaultLpA.CopyTo(data, 168);
    vaultLpB.CopyTo(data, 200);
    data[233] = 1;
    return data;
}

static byte[] CreateMeteoraDynamicVaultAccount(
    byte[] tokenMint,
    byte[] tokenVault,
    byte[] lpMint)
{
    var data = new byte[1232];
    new byte[] { 211, 8, 232, 43, 2, 152, 117, 119 }.CopyTo(data, 0);
    data[8] = 1;
    BitConverter.GetBytes(1_000_000UL).CopyTo(data, 11);
    tokenVault.CopyTo(data, 19);
    tokenMint.CopyTo(data, 83);
    lpMint.CopyTo(data, 115);
    return data;
}

static byte[] CreateManifestMarketAccount(
    byte[] baseMint,
    byte[] quoteMint,
    byte[] baseVault,
    byte[] quoteVault)
{
    var data = new byte[336];
    BitConverter.GetBytes(4_859_840_929_024_028_656UL).CopyTo(data, 0);
    baseMint.CopyTo(data, 16);
    quoteMint.CopyTo(data, 48);
    baseVault.CopyTo(data, 80);
    quoteVault.CopyTo(data, 112);
    BitConverter.GetBytes(80U).CopyTo(data, 152);
    return data;
}

static byte[] CreateRaydiumAmmV4Account(
    byte[] coinMint,
    byte[] pcMint,
    byte[] coinVault,
    byte[] pcVault)
{
    var data = new byte[752];
    BitConverter.GetBytes(6UL).CopyTo(data, 0);
    BitConverter.GetBytes(6UL).CopyTo(data, 32);
    BitConverter.GetBytes(9UL).CopyTo(data, 40);
    coinVault.CopyTo(data, 336);
    pcVault.CopyTo(data, 368);
    coinMint.CopyTo(data, 400);
    pcMint.CopyTo(data, 432);
    return data;
}

static int[] GetEngineProcessIds()
{
    return Process.GetProcessesByName("trenchhq-onchain-engine")
        .Select(static process =>
        {
            try
            {
                return process.Id;
            }
            finally
            {
                process.Dispose();
            }
        })
        .ToArray();
}

static int GetSingleNewEngineProcessId(int[] existingIds)
{
    var ids = GetEngineProcessIds().Except(existingIds).ToArray();
    AssertEqual(1, ids.Length, "Expected exactly one shared on-chain engine process.");
    return ids[0];
}

static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string description)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        if (condition())
        {
            return;
        }
        await Task.Delay(50);
    }
    throw new TimeoutException($"Timed out waiting for {description}.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected {expected}; actual {actual}.");
    }
}

static string EncodeBase58(byte[] bytes)
{
    const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
    var value = new System.Numerics.BigInteger(bytes, isUnsigned: true, isBigEndian: true);
    var result = string.Empty;
    while (value > 0)
    {
        value = System.Numerics.BigInteger.DivRem(value, 58, out var remainder);
        result = alphabet[(int)remainder] + result;
    }
    var zeroCount = bytes.TakeWhile(static value => value == 0).Count();
    return new string('1', zeroCount) + result;
}

static byte[] DecodeBase58(string value)
{
    const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
    var decoded = System.Numerics.BigInteger.Zero;
    foreach (var character in value)
    {
        var digit = alphabet.IndexOf(character);
        if (digit < 0)
        {
            throw new InvalidOperationException("Invalid base58 fixture.");
        }
        decoded = decoded * 58 + digit;
    }
    var bytes = decoded.ToByteArray(isUnsigned: true, isBigEndian: true);
    var leadingZeros = value.TakeWhile(static character => character == '1').Count();
    return Enumerable.Repeat((byte)0, leadingZeros).Concat(bytes).ToArray();
}

internal sealed record RpcFixtureAccount(string Owner, byte[] Data);

internal sealed record LivePoolFixture(string ProtocolId, string Address);

internal sealed record LiveSolanaPoolFixture(
    string ProtocolId,
    string Address,
    string SelectedMint);

internal enum LiveEvmProtocolKind
{
    V2,
    V3,
    V4,
    AerodromeClassic,
    AerodromeSlipstream,
    InfinityCl,
    InfinityBin
}

internal sealed record LiveEvmProtocolFixture(
    string Name,
    LiveEvmProtocolKind Kind,
    string TargetAddress,
    string? PoolId = null);

internal sealed class TickerRequestCountingHandler() : DelegatingHandler(new HttpClientHandler())
{
    internal ConcurrentDictionary<string, int> Counts { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
        var method = document.RootElement.GetProperty("method").GetString()!;
        Counts.AddOrUpdate(method, 1, static (_, count) => count + 1);
        return await base.SendAsync(request, cancellationToken);
    }
}

internal sealed class RpcAuthenticationFixtureHandler : HttpMessageHandler
{
    internal Uri? RequestUri { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestUri = request.RequestUri;
        var response = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id = 1,
            result = 42
        });
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(response)
        });
    }
}

internal sealed class RobinhoodStockTokenApiFixtureHandler(string nvdaAddress, string gldAddress) : HttpMessageHandler
{
    internal int AssetRequests { get; private set; }
    internal int PriceRequests { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        byte[] payload;
        if (request.RequestUri?.AbsolutePath.EndsWith("/assets", StringComparison.Ordinal) == true)
        {
            AssetRequests++;
            payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                assets = new object[]
                {
                    new
                    {
                        status = "ASSET_STATUS_ACTIVE",
                        tokenSymbol = "NVDA",
                        tokenName = "NVIDIA Stock Token",
                        currentMultiplier = "0.5",
                        logoUrl = "https://cdn.robinhood.com/nvda.png",
                        deployments = new[] { new { chainId = 4663, contractAddress = nvdaAddress } }
                    },
                    new
                    {
                        status = "ASSET_STATUS_ACTIVE",
                        tokenSymbol = "GLD",
                        tokenName = "SPDR Gold Shares Stock Token",
                        currentMultiplier = "1",
                        logoUrl = "https://cdn.robinhood.com/gld.png",
                        deployments = new[] { new { chainId = 4663, contractAddress = gldAddress } }
                    },
                    new
                    {
                        status = "ASSET_STATUS_ACTIVE",
                        tokenSymbol = "OTHER",
                        tokenName = "Other Chain Token",
                        currentMultiplier = "1",
                        logoUrl = "http://insecure.invalid/logo.png",
                        deployments = new[]
                        {
                            new { chainId = 1, contractAddress = "0x2222222222222222222222222222222222222222" }
                        }
                    }
                }
            });
        }
        else if (request.RequestUri?.AbsolutePath.EndsWith("/prices/NVDA", StringComparison.Ordinal) == true)
        {
            PriceRequests++;
            payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                quotes = new[]
                {
                    new
                    {
                        tokenSymbol = "NVDA",
                        deployments = new[] { new { chainId = 4663, contractAddress = nvdaAddress } },
                        bid = "100.10",
                        ask = "100.30",
                        currency = "USD",
                        isTradingHalt = false,
                        generatedAt = DateTimeOffset.UtcNow.ToString("O")
                    }
                }
            });
        }
        else if (request.RequestUri?.AbsolutePath.EndsWith("/prices/GLD", StringComparison.Ordinal) == true)
        {
            PriceRequests++;
            payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                quotes = new[]
                {
                    new
                    {
                        tokenSymbol = "GLD",
                        deployments = new[] { new { chainId = 4663, contractAddress = gldAddress } },
                        bid = "401.22",
                        ask = "497",
                        dailyLow = "397.4",
                        dailyHigh = "403.33",
                        currency = "USD",
                        isTradingHalt = false,
                        generatedAt = DateTimeOffset.UtcNow.ToString("O")
                    }
                }
            });
        }
        else
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        });
    }
}

internal sealed class SolanaPlanRestrictionFixtureHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":35,\"message\":\"drpc-secret is not enabled for this chain\"}}")
        });
    }
}

internal sealed class TransactionRpcFixtureHandler(byte[] outerData, byte[] innerData) : HttpMessageHandler
{
    internal Uri? RequestUri { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestUri = request.RequestUri;
        var response = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id = 1,
            result = new
            {
                slot = 99UL,
                transaction = new
                {
                    signatures = new[] { "fixture-signature" },
                    message = new
                    {
                        accountKeys = new[] { "base-vault", "outer-program" },
                        instructions = new[]
                        {
                            new
                            {
                                programIdIndex = 1,
                                data = SolanaBase58.Encode(outerData),
                                accounts = new[] { 0, 3 }
                            }
                        }
                    }
                },
                meta = new
                {
                    err = (object?)null,
                    loadedAddresses = new
                    {
                        writable = new[] { "loaded-program" },
                        @readonly = new[] { "quote-vault" }
                    },
                    innerInstructions = new[]
                    {
                        new
                        {
                            index = 0,
                            instructions = new[]
                            {
                                new
                                {
                                    programIdIndex = 2,
                                    data = SolanaBase58.Encode(innerData),
                                    stackHeight = 2U,
                                    accounts = new[] { 0, 3 }
                                }
                            }
                        }
                    },
                    logMessages = new[]
                    {
                        "Program outer-program invoke [1]",
                        "Program data: AQID",
                        "Program loaded-program invoke [2]",
                        "Program data: BAUG",
                        "Program loaded-program success",
                        "Program data: BwgJ",
                        "Program outer-program success"
                    },
                    preTokenBalances = new object[]
                    {
                        new
                        {
                            accountIndex = 0,
                            mint = "base-mint",
                            uiTokenAmount = new { amount = "1000" }
                        },
                        new
                        {
                            accountIndex = 3,
                            mint = "quote-mint",
                            uiTokenAmount = new { amount = "2000" }
                        }
                    },
                    postTokenBalances = new object[]
                    {
                        new
                        {
                            accountIndex = 0,
                            mint = "base-mint",
                            uiTokenAmount = new { amount = "900" }
                        },
                        new
                        {
                            accountIndex = 3,
                            mint = "quote-mint",
                            uiTokenAmount = new { amount = "2200" }
                        }
                    }
                }
            }
        });
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(response)
        });
    }
}

internal sealed class WalletTransactionRpcFixtureHandler(string walletAddress) : HttpMessageHandler
{
    internal string RequestBody { get; private set; } = string.Empty;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
        var response = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id = 1,
            result = new
            {
                slot = 102UL,
                transaction = new
                {
                    signatures = new[] { "wallet-fixture-signature" },
                    message = new
                    {
                        accountKeys = new[] { walletAddress, "usdc-account", "token-account" },
                        instructions = Array.Empty<object>()
                    }
                },
                meta = new
                {
                    err = (object?)null,
                    fee = 5000UL,
                    preBalances = new[] { 10_000_000_000UL, 2_039_280UL, 0UL },
                    postBalances = new[] { 9_997_955_720UL, 2_039_280UL, 2_039_280UL },
                    loadedAddresses = new
                    {
                        writable = Array.Empty<string>(),
                        @readonly = Array.Empty<string>()
                    },
                    preTokenBalances = new object[]
                    {
                        new
                        {
                            accountIndex = 1,
                            mint = WalletActivityPresentationRules.SolanaUsdcMint,
                            owner = walletAddress,
                            uiTokenAmount = new { amount = "12500000", decimals = 6 }
                        }
                    },
                    postTokenBalances = new object[]
                    {
                        new
                        {
                            accountIndex = 1,
                            mint = WalletActivityPresentationRules.SolanaUsdcMint,
                            owner = walletAddress,
                            uiTokenAmount = new { amount = "0", decimals = 6 }
                        },
                        new
                        {
                            accountIndex = 2,
                            mint = "TokenMint111111111111111111111111111111111",
                            owner = walletAddress,
                            uiTokenAmount = new { amount = "2000000000", decimals = 9 }
                        }
                    }
                }
            }
        });
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(response)
        };
    }
}

internal sealed class WalletSignatureRpcFixtureHandler : HttpMessageHandler
{
    internal string RequestBody { get; private set; } = string.Empty;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "jsonrpc": "2.0",
                  "id": 1,
                  "result": [
                    {
                      "signature": "new-signature",
                      "slot": 101,
                      "err": null,
                      "blockTime": 1700000001,
                      "confirmationStatus": "confirmed"
                    },
                    {
                      "signature": "failed-signature",
                      "slot": 100,
                      "err": { "InstructionError": [0, "Custom"] },
                      "blockTime": 1700000000,
                      "confirmationStatus": "confirmed"
                    }
                  ]
                }
                """)
        };
    }
}

internal sealed class WalletCompletionSolanaRpcFixtureHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(
            await request.Content!.ReadAsByteArrayAsync(cancellationToken));
        var root = document.RootElement;
        var id = root.GetProperty("id").GetInt32();
        var method = root.GetProperty("method").GetString();
        object result = method switch
        {
            "getTokenAccountsByOwner" => (object)new
            {
                context = new { slot = 500UL },
                value = new object[]
                {
                    new { pubkey = "TokenAccount1111111111111111111111111111111", account = new { } },
                    new { pubkey = "TokenAccount2222222222222222222222222222222", account = new { } }
                }
            },
            "getSignatureStatuses" => (object)new
            {
                context = new { slot = 505UL },
                value = new object?[]
                {
                    new { slot = 503UL, confirmations = 1, err = (object?)null, confirmationStatus = "confirmed" },
                    new { slot = 500UL, confirmations = (int?)null, err = (object?)null, confirmationStatus = "finalized" }
                }
            },
            _ => throw new InvalidOperationException("Unexpected Solana wallet RPC method: " + method)
        };
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id, result })
        };
    }
}

internal sealed class WalletCompletionEvmRpcFixtureHandler : HttpMessageHandler
{
    internal bool SawFullTransactions { get; private set; }
    internal string TraceFromBlock { get; private set; } = string.Empty;
    internal string TraceToBlock { get; private set; } = string.Empty;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(
            await request.Content!.ReadAsByteArrayAsync(cancellationToken));
        var root = document.RootElement;
        var id = root.GetProperty("id").GetInt64();
        var method = root.GetProperty("method").GetString();
        object result;
        if (method == "eth_getBlockByNumber")
        {
            SawFullTransactions = root.GetProperty("params")[1].GetBoolean();
            result = new
            {
                number = "0xc8",
                hash = "0x" + new string('a', 64),
                parentHash = "0x" + new string('b', 64),
                timestamp = "0x6553f100",
                transactions = new object[]
                {
                    new
                    {
                        hash = "0x" + new string('c', 64),
                        from = "0x1111111111111111111111111111111111111111",
                        to = "0x2222222222222222222222222222222222222222",
                        value = "0x64"
                    },
                    new
                    {
                        hash = "0x" + new string('d', 64),
                        from = "0x3333333333333333333333333333333333333333",
                        to = "0x1111111111111111111111111111111111111111",
                        value = "0x0"
                    }
                }
            };
        }
        else if (method == "trace_filter")
        {
            var filter = root.GetProperty("params")[0];
            TraceFromBlock = filter.GetProperty("fromBlock").GetString() ?? string.Empty;
            TraceToBlock = filter.GetProperty("toBlock").GetString() ?? string.Empty;
            result = new object[]
            {
                new
                {
                    blockNumber = 200,
                    transactionHash = "0x" + new string('e', 64),
                    traceAddress = new[] { 0, 1 },
                    type = "call",
                    action = new
                    {
                        from = "0x1111111111111111111111111111111111111111",
                        to = "0x4444444444444444444444444444444444444444",
                        value = "0x37"
                    }
                }
            };
        }
        else
        {
            throw new InvalidOperationException("Unexpected EVM wallet RPC method: " + method);
        }
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id, result })
        };
    }
}

internal sealed class SolanaSampleRpcFixtureHandler : HttpMessageHandler
{
    internal int RequestCount { get; private set; }
    internal string[] LastAddresses { get; private set; } = [];
    internal string? MissingAddress { get; init; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
        var root = document.RootElement;
        if (root.GetProperty("method").GetString() != "getMultipleAccounts")
        {
            throw new InvalidOperationException("The Solana price sample used an unbatched RPC method.");
        }
        LastAddresses = root.GetProperty("params")[0].EnumerateArray()
            .Select(static address => address.GetString()!).ToArray();
        RequestCount++;
        var response = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id = root.GetProperty("id").GetInt32(),
            result = new
            {
                context = new { slot = 123UL },
                value = LastAddresses.Select(address => address == MissingAddress
                    ? null
                    : (object)new
                    {
                        data = new[] { "AQ==", "base64" },
                        owner = "sample-program",
                        lamports = 1UL,
                        executable = false,
                        rentEpoch = 0UL,
                        space = 1
                    }).ToArray()
            }
        });
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(response)
        };
    }
}

internal sealed class StaticJsonFixtureHandler(byte[] payload) : HttpMessageHandler
{
    private int _requestCount;

    internal int RequestCount => Volatile.Read(ref _requestCount);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        _ = request;
        Interlocked.Increment(ref _requestCount);
        await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        };
    }
}

internal sealed class SolanaRpcFixtureHandler(
    string expectedApiKey,
    string selectedMint,
    string poolAddress,
    string decoyAddress,
    IReadOnlyDictionary<string, RpcFixtureAccount> accounts,
    bool rateLimitProgramAccounts = false) : HttpMessageHandler
{
    internal ConcurrentQueue<string> Methods { get; } = new();
    internal bool SawApiKey { get; private set; }
    internal bool SawCredentialFreeRequest { get; private set; }
    internal bool SawZeroLengthDataSlice { get; private set; }
    internal int ProgramAccountRequestCount { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        SawApiKey |= request.RequestUri?.Query == "?api-key=" + expectedApiKey;
        SawCredentialFreeRequest |= string.IsNullOrEmpty(request.RequestUri?.Query);
        using var document = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
        var root = document.RootElement;
        var method = root.GetProperty("method").GetString();
        Methods.Enqueue(method!);
        var parameters = root.GetProperty("params");
        if (method == "getProgramAccounts")
        {
            ProgramAccountRequestCount++;
            if (rateLimitProgramAccounts)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("{\"error\":{\"code\":429,\"message\":\"rate limited\"}}")
                };
            }
        }
        object result = method switch
        {
            "getAccountInfo" => Context(AccountValue(parameters[0].GetString()!)),
            "getMultipleAccounts" => Context(parameters[0].EnumerateArray()
                .Select(address => AccountValue(address.GetString()!)).ToArray()),
            "getProgramAccounts" => ProgramAccounts(parameters),
            _ => throw new InvalidOperationException($"Unexpected fixture RPC method {method}.")
        };
        var response = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id = root.GetProperty("id").GetInt32(),
            result
        });
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(response)
        };
    }

    private object ProgramAccounts(JsonElement parameters)
    {
        var config = parameters[1];
        SawZeroLengthDataSlice |= config.GetProperty("dataSlice").GetProperty("length").GetInt32() == 0;
        var filters = config.GetProperty("filters");
        var dataSize = filters[0].GetProperty("dataSize").GetInt32();
        var memcmp = filters[1].GetProperty("memcmp");
        var offset = memcmp.GetProperty("offset").GetInt32();
        var bytes = memcmp.GetProperty("bytes").GetString();
        var addresses = dataSize == 301 && offset == 43 && bytes == selectedMint
            ? new[] { poolAddress, decoyAddress }
            : [];
        return Context(addresses.Select(address => new
        {
            pubkey = address,
            account = AccountValue(address)
        }).ToArray());
    }

    private object? AccountValue(string address)
    {
        return accounts.TryGetValue(address, out var account)
            ? new
            {
                data = new[] { Convert.ToBase64String(account.Data), "base64" },
                executable = false,
                lamports = 1UL,
                owner = account.Owner,
                rentEpoch = 0UL,
                space = account.Data.Length
            }
            : null;
    }

    private static object Context(object? value)
    {
        return new
        {
            context = new { apiVersion = "2.3.0", slot = 123UL },
            value
        };
    }
}

internal sealed class FixedHttpStatusHandler(System.Net.HttpStatusCode statusCode) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new HttpResponseMessage(statusCode));
    }
}

internal sealed class FixedEvmRpcErrorHandler(string message) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = 1,
                error = new { code = -32000, message }
            })
        });
    }
}

internal sealed class WaitingOnChainSource : IOnChainStreamSource
{
    internal bool Cancelled { get; private set; }

    public async Task RunAsync(OnChainStreamSubscription subscription,
        ChannelWriter<OnChainSourceUpdate> output, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Cancelled = true;
        }
    }
}

internal sealed class FailedOnChainSource : IOnChainStreamSource
{
    public async Task RunAsync(OnChainStreamSubscription subscription,
        ChannelWriter<OnChainSourceUpdate> output, CancellationToken cancellationToken)
    {
        await Task.Yield();
        throw new IOException("simulated source failure");
    }
}

internal sealed class ScriptedOnChainSource(byte[] mintBytes) : IOnChainStreamSource
{
    private int _runCount;

    internal OnChainStreamSubscription? ObservedSubscription { get; private set; }
    internal int RunCount => Volatile.Read(ref _runCount);

    public async Task RunAsync(
        OnChainStreamSubscription subscription,
        System.Threading.Channels.ChannelWriter<OnChainSourceUpdate> output,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _runCount);
        ObservedSubscription = subscription;
        await output.WriteAsync(
            new OnChainSourceTransactionUpdate(CreateTrade()),
            cancellationToken);
        await output.WriteAsync(
            new OnChainSourceSlotUpdate(
                42,
                41,
                OnChainSlotStatus.Confirmed,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            cancellationToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private OnChainRawTransactionUpdate CreateTrade()
    {
        using var stream = new MemoryStream();
        stream.Write([189, 219, 127, 211, 78, 230, 97, 238]);
        stream.Write(mintBytes);
        stream.Write(BitConverter.GetBytes(2_000_000_000UL));
        stream.Write(BitConverter.GetBytes(1_000_000UL));
        stream.WriteByte(1);
        return new OnChainRawTransactionUpdate
        {
            Signature = "coordinator-signature",
            Slot = 42,
            Commitment = OnChainCommitment.Processed,
            SourceId = "scripted",
            ProgramData =
            [
                new OnChainRawProgramData
                {
                    ProgramId = "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P",
                    DataBase64 = Convert.ToBase64String(stream.ToArray()),
                    LogIndex = 9
                }
            ]
        };
    }
}

internal sealed class ScriptedEvmSource(EvmHeadUpdate head, EvmLogUpdate log) : IOnChainStreamSource
{
    public async Task RunAsync(
        OnChainStreamSubscription subscription,
        System.Threading.Channels.ChannelWriter<OnChainSourceUpdate> output,
        CancellationToken cancellationToken)
    {
        await output.WriteAsync(new OnChainSourceEvmHeadUpdate(head), cancellationToken);
        await output.WriteAsync(new OnChainSourceEvmLogUpdate(log), cancellationToken);
    }
}

internal sealed class CoordinatedEvmSource(
    string poolAddress,
    string chainId = EvmChainDefinitions.EthereumMainnetChainId,
    bool emitHead = true) : IOnChainStreamSource
{
    private int _runCount;

    internal int RunCount => Volatile.Read(ref _runCount);
    internal OnChainStreamSubscription? ObservedSubscription { get; private set; }

    public async Task RunAsync(
        OnChainStreamSubscription subscription,
        System.Threading.Channels.ChannelWriter<OnChainSourceUpdate> output,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _runCount);
        ObservedSubscription = subscription;
        var observedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (emitHead)
        {
            await output.WriteAsync(new OnChainSourceEvmHeadUpdate(new EvmHeadUpdate
            {
                ChainId = chainId,
                ConnectionEpoch = subscription.ConnectionEpoch,
                Number = 17,
                Hash = HashValue(17),
                ParentHash = HashValue(16),
                Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ObservedAtUnixMs = observedAt
            }), cancellationToken);
        }
        await output.WriteAsync(new OnChainSourceEvmLogUpdate(new EvmLogUpdate
        {
            ChainId = chainId,
            ConnectionEpoch = subscription.ConnectionEpoch,
            Address = poolAddress,
            Topics =
            [
                EvmWebSocketStreamSource.UniswapV2SwapTopic,
                HashValue(7000),
                HashValue(7001)
            ],
            Data = Words(
                2_000_000_000_000_000_000UL,
                0,
                0,
                4_000_000UL),
            BlockNumber = 17,
            BlockHash = HashValue(17),
            TransactionHash = HashValue(7002),
            TransactionIndex = 1,
            LogIndex = 2,
            ObservedAtUnixMs = observedAt
        }), cancellationToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static string HashValue(ulong value) => $"0x{value:x64}";

    private static string Words(params ulong[] values)
    {
        return "0x" + string.Concat(values.Select(static value => value.ToString("x64")));
    }
}

internal sealed class EvmCoordinatorRpcFixtureHandler(
    string poolAddress,
    bool advanceLatest = false,
    string? referenceAddress = null,
    bool inconsistentFinality = false) : HttpMessageHandler
{
    private int _latestBlockRequestCount;
    private int _blockNumberRequestCount;
    private int _finalizedBlockRequestCount;
    private int _pinnedCallCount;
    private int _numberedCallCount;
    private int _referenceCallCount;
    private int _directReferenceCallCount;
    private int _directSelectedCallCount;
    private int _multicallRequestCount;
    private int _numericBlockRequestCount;
    private int _contiguousAdvanceStart = -1;

    internal int GetLogsCount { get; private set; }
    internal ConcurrentQueue<string> LogFilters { get; } = new();
    internal int LatestBlockRequestCount => Volatile.Read(ref _latestBlockRequestCount);
    internal int BlockNumberRequestCount => Volatile.Read(ref _blockNumberRequestCount);
    internal int FinalizedBlockRequestCount => Volatile.Read(ref _finalizedBlockRequestCount);
    internal int PinnedCallCount => Volatile.Read(ref _pinnedCallCount);
    internal int NumberedCallCount => Volatile.Read(ref _numberedCallCount);
    internal int ReferenceCallCount => Volatile.Read(ref _referenceCallCount);
    internal int DirectReferenceCallCount => Volatile.Read(ref _directReferenceCallCount);
    internal int DirectSelectedCallCount => Volatile.Read(ref _directSelectedCallCount);
    internal int MulticallRequestCount => Volatile.Read(ref _multicallRequestCount);
    internal int NumericBlockRequestCount => Volatile.Read(ref _numericBlockRequestCount);
    internal bool SawPinnedCall { get; private set; }

    internal void EnableContiguousLatest() =>
        Volatile.Write(ref _contiguousAdvanceStart, LatestBlockRequestCount);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(
            await request.Content!.ReadAsByteArrayAsync(cancellationToken));
        var rpcRequest = document.RootElement;
        var method = rpcRequest.GetProperty("method").GetString();
        object result = method switch
        {
            "eth_getBlockByNumber" => GetBlock(rpcRequest.GetProperty("params")[0].GetString()!),
            "eth_blockNumber" => GetBlockNumber(),
            "eth_getLogs" => GetLogs(rpcRequest.GetProperty("params")),
            "eth_call" => GetCall(rpcRequest.GetProperty("params")),
            _ => throw new InvalidOperationException($"Unexpected coordinator RPC method {method}.")
        };
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0",
                id = rpcRequest.GetProperty("id").GetInt64(),
                result
            }))
        };
    }

    private object GetBlock(string tag)
    {
        if (tag.StartsWith("0x", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _numericBlockRequestCount);
        }
        if (tag == "finalized")
        {
            Interlocked.Increment(ref _finalizedBlockRequestCount);
        }
        var number = tag switch
        {
            "latest" => GetLatestBlockNumber(),
            "safe" => 15UL,
            "finalized" => inconsistentFinality ? 16UL : 14UL,
            _ => Convert.ToUInt64(tag[2..], 16)
        };
        var timestamp = (ulong)Math.Max(
            1,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() - checked((long)(17 - Math.Min(number, 17))) * 12);
        return new
        {
            number = $"0x{number:x}",
            hash = HashValue(number),
            parentHash = HashValue(number == 0 ? 0 : number - 1),
            timestamp = $"0x{timestamp:x}"
        };
    }

    private ulong GetLatestBlockNumber()
    {
        var request = Interlocked.Increment(ref _latestBlockRequestCount);
        return LatestBlockNumberForRequest(request);
    }

    private string GetBlockNumber()
    {
        Interlocked.Increment(ref _blockNumberRequestCount);
        return $"0x{LatestBlockNumberForRequest(LatestBlockRequestCount + 1):x}";
    }

    private ulong LatestBlockNumberForRequest(int request)
    {
        var contiguousStart = Volatile.Read(ref _contiguousAdvanceStart);
        return contiguousStart >= 0
            ? 16UL + checked((ulong)(request - contiguousStart))
            : advanceLatest
                ? 16UL + checked((ulong)(request - 1) * 100UL)
                : 16UL;
    }

    private object[] GetLogs(JsonElement parameters)
    {
        GetLogsCount++;
        LogFilters.Enqueue(parameters[0].GetRawText());
        if (Convert.ToUInt64(parameters[0].GetProperty("fromBlock").GetString()![2..], 16) > 16)
        {
            return [];
        }
        return
        [
            new
            {
                address = poolAddress,
                topics = new[] { EvmWebSocketStreamSource.UniswapV2SyncTopic },
                data = Words(8_000_000_000_000_000_000UL, 16_000_000UL),
                blockNumber = "0x10",
                blockHash = HashValue(16),
                transactionHash = HashValue(6000),
                transactionIndex = "0x1",
                logIndex = "0x1",
                removed = false
            }
        ];
    }

    private string GetCall(JsonElement parameters)
    {
        var call = parameters[0];
        var to = call.GetProperty("to").GetString();
        var data = call.GetProperty("data").GetString();
        var blockReference = parameters[1];
        if (blockReference.ValueKind == JsonValueKind.String
            && blockReference.GetString()!.StartsWith("0x", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _numberedCallCount);
        }
        var isPinnedCall = blockReference.ValueKind == JsonValueKind.Object
                           && blockReference.GetProperty("requireCanonical").GetBoolean();
        if (isPinnedCall)
        {
            Interlocked.Increment(ref _pinnedCallCount);
        }
        SawPinnedCall |= isPinnedCall
                         && blockReference.GetProperty("blockHash").GetString() == HashValue(16);
        if (string.Equals(to, EvmMulticall3.Address, StringComparison.OrdinalIgnoreCase))
        {
            if (referenceAddress == null)
            {
                throw new InvalidOperationException("Unexpected paired reference call.");
            }
            Interlocked.Increment(ref _multicallRequestCount);
            Interlocked.Add(ref _referenceCallCount, 2);
            if (data == EvmMulticall3.EncodeCalls(
                    [(poolAddress, EthereumAbi.GetReservesSelector),
                     (referenceAddress, EthereumAbi.Slot0Selector),
                     (referenceAddress, EthereumAbi.LiquiditySelector)]))
            {
                return EncodeResults(
                    Words(8_000_000_000_000_000_000UL, 16_000_000UL, 123),
                    UniswapV3Slot0(), Words(100));
            }
            if (data != EvmMulticall3.EncodePair(
                    referenceAddress, EthereumAbi.Slot0Selector,
                    EthereumAbi.LiquiditySelector))
            {
                throw new InvalidOperationException("Unexpected batched reference call.");
            }
            return EncodePairResult(UniswapV3Slot0(), Words(100));
        }
        if (string.Equals(to, referenceAddress, StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _directReferenceCallCount);
            Interlocked.Increment(ref _referenceCallCount);
            if (data == EthereumAbi.Slot0Selector)
            {
                return UniswapV3Slot0();
            }
            if (data == EthereumAbi.LiquiditySelector)
            {
                return Words(100);
            }
            throw new InvalidOperationException("Unexpected reference pool call.");
        }
        if (!string.Equals(to, poolAddress, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unexpected direct selected-pool call.");
        }
        Interlocked.Increment(ref _directSelectedCallCount);
        return Words(8_000_000_000_000_000_000UL, 16_000_000UL, 123);
    }

    private static string HashValue(ulong value) => $"0x{value:x64}";

    private static string UniswapV3Slot0() => "0x" + string.Concat(
        new System.Numerics.BigInteger[]
        {
            System.Numerics.BigInteger.One << 96, 0, 0, 0, 0, 0, 1
        }.Select(static value => value.ToString("x").PadLeft(64, '0')));

    internal static string EncodePairResult(string first, string second) =>
        EncodeResults(first, second);

    internal static string EncodeResults(params string[] results)
    {
        static string Word(int value) => value.ToString("x64");
        static string Item(string data)
        {
            var bytes = (data.Length - 2) / 2;
            return Word(1) + Word(64) + Word(bytes)
                   + data[2..].PadRight(((bytes + 31) / 32) * 64, '0');
        }
        var items = results.Select(Item).ToArray();
        var offsets = new string[items.Length];
        var offset = items.Length * 32;
        for (var index = 0; index < items.Length; index++)
        {
            offsets[index] = Word(offset);
            offset += items[index].Length / 2;
        }
        return "0x" + Word(32) + Word(items.Length)
               + string.Concat(offsets) + string.Concat(items);
    }

    private static string Words(params ulong[] values)
    {
        return "0x" + string.Concat(values.Select(static value => value.ToString("x64")));
    }
}

internal sealed class CurveRpcFixtureHandler(
    string selected,
    string quote,
    string poolAddress) : HttpMessageHandler
{
    private const string BlockHash =
        "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ParentHash =
        "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(
            await request.Content!.ReadAsByteArrayAsync(cancellationToken));
        var rpcRequest = document.RootElement;
        var method = rpcRequest.GetProperty("method").GetString();
        object result = method switch
        {
            "eth_chainId" => "0x1",
            "eth_getBlockByNumber" => new
            {
                number = "0x10",
                hash = BlockHash,
                parentHash = ParentHash,
                timestamp = "0x20"
            },
            "eth_getCode" => "0x6000",
            "eth_call" => GetCall(rpcRequest.GetProperty("params")),
            "eth_getLogs" => GetLogs(rpcRequest.GetProperty("params")),
            _ => throw new InvalidOperationException($"Unexpected Curve fixture method {method}.")
        };
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0",
                id = rpcRequest.GetProperty("id").GetInt64(),
                result
            }))
        };
    }

    private string GetCall(JsonElement parameters)
    {
        var call = parameters[0];
        var to = call.GetProperty("to").GetString()!;
        var data = call.GetProperty("data").GetString()!;
        if (data == EthereumAbi.DecimalsSelector)
        {
            return Word(to.Equals(quote, StringComparison.OrdinalIgnoreCase) ? 6UL : 18UL);
        }
        if (data.StartsWith(EthereumAbi.GetPairSelector, StringComparison.Ordinal)
            || data.StartsWith(EthereumAbi.GetPoolSelector, StringComparison.Ordinal))
        {
            return Address("0x0000000000000000000000000000000000000000");
        }
        if (to.Equals(poolAddress, StringComparison.OrdinalIgnoreCase)
            && data.StartsWith(EthereumAbi.CurveCoinsSelector, StringComparison.Ordinal))
        {
            var index = Convert.ToUInt32(data[^64..], 16);
            return index switch
            {
                0 => Address(selected),
                2 => Address(quote),
                _ => throw new InvalidOperationException("The Curve fixture requested an unexpected coin index.")
            };
        }
        throw new InvalidOperationException($"Unexpected Curve fixture call {to} {data}.");
    }

    private object[] GetLogs(JsonElement parameters)
    {
        var filter = parameters[0];
        var address = filter.GetProperty("address").GetString();
        if (!string.Equals(address, EthereumDeploymentRegistry.FermiCurrentSwapper, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }
        return
        [
            new
            {
                address = EthereumDeploymentRegistry.FermiCurrentSwapper,
                topics = new[]
                {
                    EvmWebSocketStreamSource.FermiSwappedTopic,
                    Address("0x2222222222222222222222222222222222222222"),
                    Address(quote),
                    Address(selected)
                },
                data = "0x"
                       + Word(4_000_000_000)[2..]
                       + Word(2_000_000_000_000_000_000)[2..]
                       + Address("0x3333333333333333333333333333333333333333")[2..],
                blockNumber = "0x10"
            }
        ];
    }

    private static string Word(ulong value) => $"0x{value:x64}";

    private static string Address(string value) => "0x" + value[2..].PadLeft(64, '0');
}

internal sealed class EvmRpcFixtureHandler(
    bool rateLimitFirstChainId = false,
    bool malformedMulticall = false) : HttpMessageHandler
{
    private const string BlockHash =
        "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ParentHash =
        "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private int _remainingRateLimitedChainIdRequests = rateLimitFirstChainId ? 1 : 0;

    internal int RateLimitedChainIdRequestCount { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(
            await request.Content!.ReadAsByteArrayAsync(cancellationToken));
        if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.GetProperty("method").GetString() == "eth_chainId"
            && Interlocked.Exchange(ref _remainingRateLimitedChainIdRequests, 0) == 1)
        {
            RateLimitedChainIdRequestCount++;
            return new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        }
        object response = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().Select(CreateResponse).ToArray()
            : CreateResponse(document.RootElement);
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(response))
        };
    }

    private object CreateResponse(JsonElement request)
    {
        var method = request.GetProperty("method").GetString();
        object result = method switch
        {
            "eth_chainId" => "0x1",
            "eth_blockNumber" => "0x10",
            "eth_getBlockByNumber" => new
            {
                number = "0x10",
                hash = BlockHash,
                parentHash = ParentHash,
                timestamp = "0x20"
            },
            "eth_call" => GetCall(request),
            "eth_getLogs" => Array.Empty<object>(),
            _ => throw new InvalidOperationException($"Unexpected EVM fixture method {method}.")
        };
        return new
        {
            jsonrpc = "2.0",
            id = request.GetProperty("id").GetInt64(),
            result
        };
    }

    private string GetCall(JsonElement request)
    {
        if (malformedMulticall) { return "0x"; }
        var call = request.GetProperty("params")[0];
        if (!string.Equals(
                call.GetProperty("to").GetString(),
                EvmMulticall3.Address,
                StringComparison.OrdinalIgnoreCase)
            || call.GetProperty("data").GetString() != EvmMulticall3.EncodePair(
                EvmChainDefinitions.EthereumMainnet.WrappedNativeAssetAddress,
                EthereumAbi.DecimalsSelector,
                EthereumAbi.DecimalsSelector))
        {
            throw new InvalidOperationException("The capability probe did not test paired pinned reads.");
        }
        const string decimals = "0x0000000000000000000000000000000000000000000000000000000000000012";
        return EvmCoordinatorRpcFixtureHandler.EncodePairResult(decimals, decimals);
    }
}

internal sealed class InfuraBlockReferenceFallbackFixtureHandler : HttpMessageHandler
{
    internal const string BlockHash =
        "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ParentHash =
        "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    internal bool UsedVerifiedBlockNumberCall { get; private set; }
    internal int BlockByHashRequestCount { get; private set; }
    internal int BlockByNumberRequestCount { get; private set; }
    internal bool ReplaceBlockAfterCall { get; init; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(
            await request.Content!.ReadAsByteArrayAsync(cancellationToken));
        var rpcRequest = document.RootElement;
        var method = rpcRequest.GetProperty("method").GetString();
        object result = method switch
        {
            "eth_getBlockByHash" => GetBlockByHash(rpcRequest.GetProperty("params")),
            "eth_getBlockByNumber" => GetBlockByNumber(),
            "eth_call" => GetCall(rpcRequest.GetProperty("params")),
            _ => throw new InvalidOperationException($"Unexpected Infura fallback method {method}.")
        };
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0",
                id = rpcRequest.GetProperty("id").GetInt64(),
                result
            }))
        };
    }

    private object GetBlockByHash(JsonElement parameters)
    {
        if (parameters[0].GetString() != BlockHash)
        {
            throw new InvalidOperationException("The Infura fallback resolved the wrong block hash.");
        }
        BlockByHashRequestCount++;
        return GetBlock();
    }

    private object GetCall(JsonElement parameters)
    {
        UsedVerifiedBlockNumberCall = parameters[1].ValueKind == JsonValueKind.String
                                      && parameters[1].GetString() == "0x10";
        return "0x0000000000000000000000000000000000000000000000000000000000000012";
    }

    private object GetBlockByNumber()
    {
        BlockByNumberRequestCount++;
        return GetBlock();
    }

    private object GetBlock() => new
    {
        number = "0x10",
        hash = ReplaceBlockAfterCall && UsedVerifiedBlockNumberCall ? ParentHash : BlockHash,
        parentHash = ParentHash,
        timestamp = "0x20"
    };
}

internal sealed class UniswapDiscoveryFixtureHandler(
    string chainId,
    EvmDeploymentCatalog deployments,
    string selectedToken,
    string pair,
    string v3Pool,
    bool aerodromeRateLimited = false) : HttpMessageHandler
{
    private const string BlockHash =
        "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ParentHash =
        "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    public int CallCount { get; private set; }
    public bool SawUnpinnedStateRead { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(
            await request.Content!.ReadAsByteArrayAsync(cancellationToken));
        var rpcRequest = document.RootElement;
        var method = rpcRequest.GetProperty("method").GetString();
        if (aerodromeRateLimited
            && method == "eth_call"
            && IsAerodromeFactoryCall(rpcRequest))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        }
        object result = method switch
        {
            "eth_chainId" => "0x" + ulong.Parse(chainId).ToString("x"),
            "eth_getBlockByNumber" => new
            {
                number = "0x10",
                hash = BlockHash,
                parentHash = ParentHash,
                timestamp = "0x20"
            },
            "eth_getCode" => GetCode(rpcRequest),
            "eth_call" => GetCallResult(rpcRequest),
            _ => throw new InvalidOperationException($"Unexpected discovery RPC method {method}.")
        };
        var response = new
        {
            jsonrpc = "2.0",
            id = rpcRequest.GetProperty("id").GetInt64(),
            result
        };
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(response))
        };
    }

    private static bool IsAerodromeFactoryCall(JsonElement request)
    {
        var data = request.GetProperty("params")[0].GetProperty("data").GetString();
        return data?.StartsWith(EthereumAbi.AerodromeClassicGetPoolSelector, StringComparison.Ordinal) == true
               || data?.StartsWith(EthereumAbi.AerodromeSlipstreamGetPoolSelector, StringComparison.Ordinal) == true;
    }

    private string GetCode(JsonElement request)
    {
        CallCount++;
        ValidatePinned(request.GetProperty("params")[1]);
        return "0x6000";
    }

    private string GetCallResult(JsonElement request)
    {
        CallCount++;
        var parameters = request.GetProperty("params");
        ValidatePinned(parameters[1]);
        var call = parameters[0];
        var to = call.GetProperty("to").GetString()!.ToLowerInvariant();
        var data = call.GetProperty("data").GetString()!.ToLowerInvariant();
        if (data == EthereumAbi.DecimalsSelector)
        {
            return Word(to == BaseDeploymentRegistry.Usdc ? 6UL : 18UL);
        }
        if (to == pair && data == EthereumAbi.FactorySelector)
        {
            return AddressWord(deployments.UniswapV2Factory);
        }
        if (to == pair && data == EthereumAbi.Token0Selector)
        {
            return AddressWord(selectedToken);
        }
        if (to == pair && data == EthereumAbi.Token1Selector)
        {
            return AddressWord(deployments.QuoteAssets[0].Address);
        }
        if (to == pair && data == EthereumAbi.GetReservesSelector)
        {
            return Words(10, 20, 123);
        }
        if (to == v3Pool && data == EthereumAbi.FactorySelector)
        {
            return AddressWord(deployments.UniswapV3Factory);
        }
        if (to == v3Pool && data == EthereumAbi.Token0Selector)
        {
            return AddressWord(selectedToken);
        }
        if (to == v3Pool && data == EthereumAbi.Token1Selector)
        {
            return AddressWord(deployments.QuoteAssets[0].Address);
        }
        if (to == v3Pool && data == EthereumAbi.FeeSelector)
        {
            return Word(500);
        }
        if (to == v3Pool && data == EthereumAbi.TickSpacingSelector)
        {
            return Word(10);
        }
        if (to == v3Pool && data == EthereumAbi.Slot0Selector)
        {
            return BigWords(System.Numerics.BigInteger.One << 96, 0, 0, 0, 0, 0, 1);
        }
        if (to == v3Pool && data == EthereumAbi.LiquiditySelector)
        {
            return Word(100);
        }
        if (to == deployments.UniswapV2Factory
            && data.StartsWith(EthereumAbi.GetPairSelector, StringComparison.Ordinal))
        {
            var weth = deployments.QuoteAssets[0].Address[2..];
            return data.Contains(weth, StringComparison.Ordinal)
                ? AddressWord(pair)
                : AddressWord("0x0000000000000000000000000000000000000000");
        }
        if (to == deployments.UniswapV3Factory
            && data.StartsWith(EthereumAbi.GetPoolSelector, StringComparison.Ordinal))
        {
            var weth = deployments.QuoteAssets[0].Address[2..];
            var fee = data[^64..];
            return data.Contains(weth, StringComparison.Ordinal)
                   && fee.EndsWith("01f4", StringComparison.Ordinal)
                ? AddressWord(v3Pool)
                : AddressWord("0x0000000000000000000000000000000000000000");
        }
        if (data.StartsWith(EthereumAbi.AerodromeClassicGetPoolSelector, StringComparison.Ordinal)
            || data.StartsWith(EthereumAbi.AerodromeSlipstreamGetPoolSelector, StringComparison.Ordinal))
        {
            return AddressWord("0x0000000000000000000000000000000000000000");
        }
        throw new InvalidOperationException($"Unexpected discovery eth_call to {to} with {data}.");
    }

    private void ValidatePinned(JsonElement blockReference)
    {
        if (blockReference.ValueKind != JsonValueKind.Object
            || blockReference.GetProperty("blockHash").GetString() != BlockHash
            || !blockReference.GetProperty("requireCanonical").GetBoolean())
        {
            SawUnpinnedStateRead = true;
        }
    }

    private static string AddressWord(string address)
    {
        return "0x" + address[2..].PadLeft(64, '0');
    }

    private static string Word(ulong value) => $"0x{value:x64}";

    private static string Words(params ulong[] values)
    {
        return "0x" + string.Concat(values.Select(static value => $"{value:x64}"));
    }

    private static string BigWords(params System.Numerics.BigInteger[] values)
    {
        return "0x" + string.Concat(values.Select(static value =>
            value.ToString("x").PadLeft(64, '0')));
    }
}

internal sealed record EvmCatalogPoolFixture(
    string PoolAddress,
    string QuoteAddress,
    string ProtocolId,
    string FactoryAddress,
    uint FeeTier = 0,
    int TickSpacing = 0,
    long CreatedAtUnixMs = 0,
    string HookAddress = "0x0000000000000000000000000000000000000000",
    bool? Stable = null);

internal sealed class PancakeCatalogFixtureHandler(
    string selectedToken,
    EvmDeploymentCatalog? deployments = null) : HttpMessageHandler
{
    private int _requestCount;
    private EvmDeploymentCatalog Deployments => deployments ?? BnbDeploymentRegistry.Catalog;

    public int RequestCount => _requestCount;
    public bool SawExpectedChainQuery { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        Interlocked.Increment(ref _requestCount);
        var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
        SawExpectedChainQuery |= uri.Contains(
            $"chains={Deployments.CatalogChainId}",
            StringComparison.Ordinal);
        var protocol = uri.Contains("protocols=infinityBin", StringComparison.Ordinal)
            ? "infinityBin"
            : "infinityCl";
        var isSecondPage = uri.Contains("after=", StringComparison.Ordinal);
        object[] rows = isSecondPage
            ?
            [
                new
                {
                    chainId = int.Parse(
                        Deployments.ChainId,
                        System.Globalization.CultureInfo.InvariantCulture),
                    protocol,
                    id = protocol == "infinityBin"
                        ? "0x5555555555555555555555555555555555555555555555555555555555555555"
                        : "0x4444444444444444444444444444444444444444444444444444444444444444",
                    token0 = new { id = selectedToken, name = "Token", symbol = "TOKEN" },
                    token1 = new
                    {
                        id = Deployments.QuoteAssets[0].Address,
                        name = Deployments.QuoteAssets[0].Symbol,
                        symbol = Deployments.QuoteAssets[0].Symbol
                    },
                    tvlUSD = "1000",
                    volumeUSD24h = "100"
                }
            ]
            : [];
        var payload = new
        {
            hasNextPage = !isSecondPage,
            hasPrevPage = isSecondPage,
            startCursor = isSecondPage ? "second" : "first",
            endCursor = isSecondPage ? "done" : $"cursor-{protocol}",
            rows
        };
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload))
        });
    }
}

internal sealed class CatalogProtocolDiscoveryFixtureHandler(
    string selectedToken,
    IReadOnlyCollection<EvmCatalogPoolFixture> fixtures,
    string? timedOutPool = null,
    EvmChainDefinition? chain = null,
    EvmDeploymentCatalog? deployments = null,
    bool blockExplorerUnavailable = false,
    int? maximumRpcLogBlockCount = null,
    bool blockExplorerRateLimitedOnce = false,
    bool blockExplorerLogsUnavailable = false,
    TimeSpan? poolCodeDelay = null,
    string? undelayedPoolAddress = null) : HttpMessageHandler
{
    internal const string BlockHash =
        "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ParentHash =
        "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    public bool SawUnpinnedStateRead { get; private set; }
    public bool SawExpectedBlockscoutApiRoot { get; private set; }
    public bool SawRpcV4Lookup { get; private set; }
    public ulong MaximumObservedRpcLogBlockCount { get; private set; }
    public int RpcBlockLookupCount { get; private set; }
    public int MaximumConcurrentPoolCodeRequests { get; private set; }
    private readonly ConcurrentDictionary<string, int> _rpcMethodCounts = new(StringComparer.Ordinal);
    private readonly object _poolCodeRequestLock = new();
    private int _activePoolCodeRequests;
    private bool _timedOut;
    private bool _blockExplorerRateLimited;
    private EvmChainDefinition Chain => chain ?? EvmChainDefinitions.EthereumMainnet;
    private EvmDeploymentCatalog Deployments => deployments ?? EthereumDeploymentRegistry.Catalog;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get)
        {
            return GetBlockExplorerResponse(request);
        }
        using var document = JsonDocument.Parse(
            await request.Content!.ReadAsByteArrayAsync(cancellationToken));
        var rpcRequest = document.RootElement;
        var method = rpcRequest.GetProperty("method").GetString()
                     ?? throw new InvalidOperationException("Catalog discovery RPC method is missing.");
        _rpcMethodCounts.AddOrUpdate(method, 1, static (_, count) => count + 1);
        if (method == "eth_getBlockByNumber")
        {
            RpcBlockLookupCount++;
        }
        if (poolCodeDelay is { } delay
            && method == "eth_getCode"
            && !rpcRequest.GetProperty("params")[0].GetString()!.Equals(
                selectedToken,
                StringComparison.OrdinalIgnoreCase)
            && !rpcRequest.GetProperty("params")[0].GetString()!.Equals(
                undelayedPoolAddress,
                StringComparison.OrdinalIgnoreCase))
        {
            lock (_poolCodeRequestLock)
            {
                _activePoolCodeRequests++;
                MaximumConcurrentPoolCodeRequests = Math.Max(
                    MaximumConcurrentPoolCodeRequests,
                    _activePoolCodeRequests);
            }
            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            finally
            {
                lock (_poolCodeRequestLock)
                {
                    _activePoolCodeRequests--;
                }
            }
        }
        object result = method switch
        {
            "eth_chainId" => "0x" + ulong.Parse(Chain.ChainId).ToString("x"),
            "eth_getBlockByNumber" => new
            {
                number = "0x10",
                hash = BlockHash,
                parentHash = ParentHash,
                timestamp = "0x20"
            },
            "eth_getCode" => GetCode(rpcRequest),
            "eth_call" => GetCallResult(rpcRequest),
            "eth_getLogs" => GetV4InitializeLogs(rpcRequest),
            "eth_getTransactionReceipt" => GetTransactionReceipt(rpcRequest),
            _ => throw new InvalidOperationException($"Unexpected catalog discovery RPC method {method}.")
        };
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0",
                id = rpcRequest.GetProperty("id").GetInt64(),
                result
            }))
        };
    }

    internal int GetRpcMethodCount(string method) =>
        _rpcMethodCounts.TryGetValue(method, out var count) ? count : 0;

    private HttpResponseMessage GetBlockExplorerResponse(HttpRequestMessage request)
    {
        var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
        SawExpectedBlockscoutApiRoot |= uri.StartsWith(
            Chain.BlockscoutApiRoot,
            StringComparison.Ordinal);
        if (blockExplorerUnavailable)
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
        }
        if (blockExplorerRateLimitedOnce
            && !_blockExplorerRateLimited
            && uri.Contains("action=getLogs", StringComparison.Ordinal))
        {
            _blockExplorerRateLimited = true;
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                TimeSpan.Zero);
            return response;
        }
        if (blockExplorerLogsUnavailable
            && uri.Contains("action=getLogs", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
        }
        object payload;
        if (uri.Contains("/api/v2/addresses/", StringComparison.Ordinal))
        {
            var fixture = fixtures.Single(item => item.ProtocolId == OnChainProtocolIds.UniswapV4);
            payload = uri.Contains("/logs?", StringComparison.Ordinal)
                ? new
                {
                    items = new[]
                    {
                        new
                        {
                            transaction_hash = fixture.PoolAddress,
                            block_number = 16,
                            block_hash = BlockHash,
                            topics = new[]
                            {
                                EvmWebSocketStreamSource.UniswapV4InitializeTopic,
                                fixture.PoolAddress
                            }
                        }
                    }
                }
                : new { creation_transaction_hash = fixture.PoolAddress };
        }
        else if (uri.Contains("action=getblocknobytime", StringComparison.Ordinal))
        {
            payload = new { status = "1", message = "OK", result = new { blockNumber = 16 } };
        }
        else if (uri.Contains("action=getLogs", StringComparison.Ordinal))
        {
            var fixture = fixtures.Single(item => item.ProtocolId == OnChainProtocolIds.UniswapV4);
            payload = new
            {
                status = "1",
                message = "OK",
                result = new[]
                {
                    new
                    {
                        transactionHash = fixture.PoolAddress,
                        blockNumber = "0x10",
                        blockHash = BlockHash
                    }
                }
            };
        }
        else
        {
            throw new InvalidOperationException($"Unexpected block explorer request {uri}.");
        }
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload))
        };
    }

    private object GetV4InitializeLogs(JsonElement request)
    {
        SawRpcV4Lookup = true;
        var fixture = fixtures.Single(item => item.ProtocolId == OnChainProtocolIds.UniswapV4);
        var filter = request.GetProperty("params")[0];
        if (!EvmAddress.TryParseQuantity(filter.GetProperty("fromBlock").GetString(), out var fromBlock)
            || !EvmAddress.TryParseQuantity(filter.GetProperty("toBlock").GetString(), out var toBlock)
            || toBlock < fromBlock)
        {
            throw new InvalidOperationException("The V4 RPC lookup used an invalid block range.");
        }
        var blockCount = toBlock - fromBlock + 1;
        MaximumObservedRpcLogBlockCount = Math.Max(MaximumObservedRpcLogBlockCount, blockCount);
        if (maximumRpcLogBlockCount.HasValue && blockCount > (ulong)maximumRpcLogBlockCount.Value)
        {
            throw new InvalidOperationException(
                $"The fixture provider rejected an eth_getLogs request for {blockCount} blocks.");
        }
        if (maximumRpcLogBlockCount.HasValue && (16 < fromBlock || 16 > toBlock))
        {
            return Array.Empty<object>();
        }
        return new[] { new { transactionHash = fixture.PoolAddress } };
    }

    private object GetTransactionReceipt(JsonElement request)
    {
        var transactionHash = request.GetProperty("params")[0].GetString();
        var fixture = fixtures.Single(item =>
            item.ProtocolId == OnChainProtocolIds.UniswapV4
            && item.PoolAddress.Equals(transactionHash, StringComparison.OrdinalIgnoreCase));
        var currencies = new[] { selectedToken, fixture.QuoteAddress }
            .OrderBy(static address => address, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new
        {
            status = "0x1",
            blockNumber = "0x10",
            blockHash = BlockHash,
            logs = new[]
            {
                new
                {
                    address = Deployments.UniswapV4PoolManager,
                    topics = new[]
                    {
                        EvmWebSocketStreamSource.UniswapV4InitializeTopic,
                        fixture.PoolAddress,
                        AddressTopic(currencies[0]),
                        AddressTopic(currencies[1])
                    },
                    data = BigWords(
                        fixture.FeeTier,
                        fixture.TickSpacing,
                        ParseAddressWord(fixture.HookAddress),
                        System.Numerics.BigInteger.One << 96,
                        0)
                }
            }
        };
    }

    private string GetCode(JsonElement request)
    {
        ValidatePinned(request.GetProperty("params")[1]);
        return "0x6000";
    }

    private string GetCallResult(JsonElement request)
    {
        var parameters = request.GetProperty("params");
        ValidatePinned(parameters[1]);
        var call = parameters[0];
        var to = call.GetProperty("to").GetString()!.ToLowerInvariant();
        var data = call.GetProperty("data").GetString()!.ToLowerInvariant();
        if (!_timedOut && to.Equals(timedOutPool, StringComparison.OrdinalIgnoreCase))
        {
            _timedOut = true;
            throw new TaskCanceledException("Fixture RPC timeout.");
        }
        if (data == EthereumAbi.DecimalsSelector)
        {
            return Word(Chain.ChainId == EvmChainDefinitions.BaseMainnetChainId
                        && to == BaseDeploymentRegistry.Usdc
                        || Chain.ChainId == EvmChainDefinitions.RobinhoodMainnetChainId
                        && to == RobinhoodDeploymentRegistry.Usdg
                ? 6UL
                : 18UL);
        }

        if (to == Deployments.UniswapV4StateView)
        {
            if (data.StartsWith(EthereumAbi.UniswapV4StateViewSlot0Selector, StringComparison.Ordinal))
            {
                return BigWords(System.Numerics.BigInteger.One << 96, 0, 0, 10000);
            }
            if (data.StartsWith(EthereumAbi.UniswapV4StateViewLiquiditySelector, StringComparison.Ordinal))
            {
                return Word(100);
            }
        }

        var pool = fixtures.FirstOrDefault(fixture =>
            fixture.PoolAddress.Equals(to, StringComparison.OrdinalIgnoreCase));
        if (pool != null)
        {
            if (data == EthereumAbi.FactorySelector)
            {
                return AddressWord(pool.FactoryAddress);
            }
            if (data == EthereumAbi.Token0Selector)
            {
                return AddressWord(selectedToken);
            }
            if (data == EthereumAbi.Token1Selector)
            {
                return AddressWord(pool.QuoteAddress);
            }
            if (pool.ProtocolId is OnChainProtocolIds.UniswapV2 or OnChainProtocolIds.PancakeV2
                && data == EthereumAbi.GetReservesSelector)
            {
                return Words(10, 20, 123);
            }
            if (pool.ProtocolId is OnChainProtocolIds.UniswapV3 or OnChainProtocolIds.PancakeV3)
            {
                if (data == EthereumAbi.FeeSelector)
                {
                    return Word(pool.FeeTier);
                }
                if (data == EthereumAbi.TickSpacingSelector)
                {
                    return Word((ulong)pool.TickSpacing);
                }
                if (data == EthereumAbi.Slot0Selector)
                {
                    return BigWords(System.Numerics.BigInteger.One << 96, 0, 0, 0, 0, 0, 1);
                }
                if (data == EthereumAbi.LiquiditySelector)
                {
                    return Word(100);
                }
            }
            if (pool.ProtocolId == OnChainProtocolIds.AerodromeClassic)
            {
                if (data == EthereumAbi.StableSelector)
                {
                    return Word(pool.Stable == true ? 1UL : 0UL);
                }
                if (data == EthereumAbi.GetReservesSelector)
                {
                    return Words(10, 20, 123);
                }
            }
            if (pool.ProtocolId == OnChainProtocolIds.AerodromeSlipstream)
            {
                if (data == EthereumAbi.TickSpacingSelector)
                {
                    return Word((ulong)pool.TickSpacing);
                }
                if (data == EthereumAbi.Slot0Selector)
                {
                    return BigWords(System.Numerics.BigInteger.One << 96, 0, 0, 0, 0, 1);
                }
                if (data == EthereumAbi.LiquiditySelector)
                {
                    return Word(100);
                }
                if (data == EthereumAbi.FeeSelector)
                {
                    return Word(700);
                }
            }
        }

        var infinityPool = fixtures.FirstOrDefault(fixture =>
            fixture.FactoryAddress.Equals(to, StringComparison.OrdinalIgnoreCase)
            && fixture.ProtocolId is OnChainProtocolIds.PancakeInfinityCl
                or OnChainProtocolIds.PancakeInfinityBin
            && data.EndsWith(fixture.PoolAddress[2..], StringComparison.OrdinalIgnoreCase));
        if (infinityPool != null)
        {
            if (data.StartsWith(EthereumAbi.InfinityPoolKeySelector, StringComparison.Ordinal))
            {
                var encodedParameters = (ulong)infinityPool.TickSpacing << 16;
                return BigWords(
                    ParseAddressWord(selectedToken),
                    ParseAddressWord(infinityPool.QuoteAddress),
                    0,
                    ParseAddressWord(infinityPool.FactoryAddress),
                    infinityPool.FeeTier,
                    encodedParameters);
            }
            if (data.StartsWith(EthereumAbi.InfinitySlot0Selector, StringComparison.Ordinal))
            {
                return infinityPool.ProtocolId == OnChainProtocolIds.PancakeInfinityCl
                    ? BigWords(System.Numerics.BigInteger.One << 96, 0, 4097, infinityPool.FeeTier)
                    : BigWords((System.Numerics.BigInteger.One << 23) + 1, 4097, infinityPool.FeeTier);
            }
            if (data.StartsWith(EthereumAbi.InfinityLiquiditySelector, StringComparison.Ordinal))
            {
                return Word(100);
            }
        }

        if (data.StartsWith(EthereumAbi.IsPoolSelector, StringComparison.Ordinal))
        {
            var requestedPool = AddressFromWord(data[^64..]);
            return Word(fixtures.Any(fixture =>
                fixture.FactoryAddress.Equals(to, StringComparison.OrdinalIgnoreCase)
                && fixture.PoolAddress.Equals(requestedPool, StringComparison.OrdinalIgnoreCase))
                ? 1UL
                : 0UL);
        }

        var factoryPool = fixtures.FirstOrDefault(fixture =>
            fixture.FactoryAddress.Equals(to, StringComparison.OrdinalIgnoreCase)
            && data.Contains(fixture.QuoteAddress[2..], StringComparison.OrdinalIgnoreCase)
            && fixture.ProtocolId switch
            {
                OnChainProtocolIds.UniswapV2 or OnChainProtocolIds.PancakeV2 =>
                    data.StartsWith(EthereumAbi.GetPairSelector, StringComparison.Ordinal),
                OnChainProtocolIds.UniswapV3 or OnChainProtocolIds.PancakeV3 =>
                    data.StartsWith(EthereumAbi.GetPoolSelector, StringComparison.Ordinal)
                    && data[^64..].EndsWith($"{fixture.FeeTier:x6}", StringComparison.Ordinal),
                OnChainProtocolIds.AerodromeClassic =>
                    data.StartsWith(EthereumAbi.AerodromeClassicGetPoolSelector, StringComparison.Ordinal)
                    && data[^64..] == (fixture.Stable == true ? "1" : "0").PadLeft(64, '0'),
                OnChainProtocolIds.AerodromeSlipstream =>
                    data.StartsWith(EthereumAbi.AerodromeSlipstreamGetPoolSelector, StringComparison.Ordinal)
                    && data[^64..].EndsWith($"{fixture.TickSpacing:x6}", StringComparison.Ordinal),
                _ => false
            });
        if (factoryPool != null)
        {
            return AddressWord(factoryPool.PoolAddress);
        }
        if (data.StartsWith(EthereumAbi.GetPairSelector, StringComparison.Ordinal)
            || data.StartsWith(EthereumAbi.GetPoolSelector, StringComparison.Ordinal)
            || data.StartsWith(EthereumAbi.AerodromeClassicGetPoolSelector, StringComparison.Ordinal)
            || data.StartsWith(EthereumAbi.AerodromeSlipstreamGetPoolSelector, StringComparison.Ordinal))
        {
            return AddressWord("0x0000000000000000000000000000000000000000");
        }
        throw new InvalidOperationException($"Unexpected catalog discovery eth_call to {to} with {data}.");
    }

    private void ValidatePinned(JsonElement blockReference)
    {
        if (blockReference.ValueKind != JsonValueKind.Object
            || blockReference.GetProperty("blockHash").GetString() != BlockHash
            || !blockReference.GetProperty("requireCanonical").GetBoolean())
        {
            SawUnpinnedStateRead = true;
        }
    }

    private static string AddressWord(string address)
    {
        return "0x" + address[2..].PadLeft(64, '0');
    }

    private static string AddressFromWord(string word)
    {
        return "0x" + word[^40..];
    }

    private static string AddressTopic(string address)
    {
        return "0x" + address[2..].PadLeft(64, '0');
    }

    private static string Word(ulong value) => $"0x{value:x64}";

    private static System.Numerics.BigInteger ParseAddressWord(string address)
    {
        return System.Numerics.BigInteger.Parse(
            "0" + address[2..],
            System.Globalization.NumberStyles.AllowHexSpecifier,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Words(params ulong[] values)
    {
        return "0x" + string.Concat(values.Select(static value => $"{value:x64}"));
    }

    private static string BigWords(params System.Numerics.BigInteger[] values)
    {
        var modulus = System.Numerics.BigInteger.One << 256;
        return "0x" + string.Concat(values.Select(value =>
        {
            var encoded = value.Sign < 0 ? modulus + value : value;
            return encoded.ToString("x").PadLeft(64, '0');
        }));
    }
}

internal sealed class ObservingEvmStreamSource(
    IOnChainStreamSource inner,
    ConcurrentQueue<EvmLogUpdate> logs,
    Action onRun) : IOnChainStreamSource
{
    public Task RunAsync(
        OnChainStreamSubscription subscription,
        ChannelWriter<OnChainSourceUpdate> output,
        CancellationToken cancellationToken)
    {
        onRun();
        return inner.RunAsync(
            subscription,
            new ObservingChannelWriter(output, logs),
            cancellationToken);
    }

    private sealed class ObservingChannelWriter(
        ChannelWriter<OnChainSourceUpdate> innerWriter,
        ConcurrentQueue<EvmLogUpdate> observedLogs) : ChannelWriter<OnChainSourceUpdate>
    {
        public override bool TryComplete(Exception? error = null)
        {
            return innerWriter.TryComplete(error);
        }

        public override bool TryWrite(OnChainSourceUpdate item)
        {
            if (item is OnChainSourceEvmLogUpdate log)
            {
                observedLogs.Enqueue(log.Update);
            }
            return innerWriter.TryWrite(item);
        }

        public override ValueTask<bool> WaitToWriteAsync(
            CancellationToken cancellationToken = default)
        {
            return innerWriter.WaitToWriteAsync(cancellationToken);
        }
    }
}

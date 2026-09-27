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
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--ticker-public-batch"])
        {
            await VerifyPublicBatchAsync();
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

        if (args is ["--environment"])
        {
            var enginePath = Path.Combine(AppContext.BaseDirectory, "OnChainEngine", "trenchhq-onchain-engine.exe");
            var client = new OnChainEngineClient(enginePath);
            try
            {
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
                if (runLiveSolanaWalletValidation)
                {
                    await VerifyLivePublicSolanaWalletTransactionAsync();
                }
                if (runLiveWalletValidation)
                {
                    await VerifyLivePublicWalletRecoveryAsync();
                }
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

                return 0;
            }
            finally { await client.StopAsync(); }
        }
        Console.WriteLine("Live validation only. Commands: --ticker-public-batch, --ticker-public-poll, --store-dedicated-alchemy-key, --live-dedicated-alchemy-coverage, --live-dedicated-alchemy-matrix, --live-dedicated-alchemy-solana-snapshot, --environment. See docs/BUILD.md before using credentials.");
        return args.Length == 0 ? 0 : 2;
    }
}

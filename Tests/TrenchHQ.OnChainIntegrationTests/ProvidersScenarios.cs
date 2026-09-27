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
        Assert(EvmAddress.IsHash(EvmEventTopics.UniswapV2SwapTopic)
               && EvmAddress.IsHash(EvmEventTopics.UniswapV3SwapTopic)
               && EvmAddress.IsHash(EvmEventTopics.PancakeV3SwapTopic)
               && EvmAddress.IsHash(EvmEventTopics.PancakeInfinityClSwapTopic)
               && EvmAddress.IsHash(EvmEventTopics.PancakeInfinityBinSwapTopic),
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
              "topics": ["{{EvmEventTopics.UniswapV3SwapTopic}}"],
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
        AssertEqual(EvmEventTopics.UniswapV3SwapTopic, log.Topics[0],
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
}

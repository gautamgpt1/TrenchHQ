using TrenchHQ.Core.OnChain;
using TrenchHQ.Core.Providers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace TrenchHQ.Infrastructure.Providers
{
    internal static class OnChainProviderConfigurationStore
    {
        internal const string ConfigurationsFileName = "onchain_provider_configurations.json";
        private const string SecretsDirectoryName = "onchain-secrets";
        // Persisted v1 encryption-format bytes must remain stable across product renames.
        private static readonly byte[] OptionalEntropy = Convert.FromHexString("4E657875732E4F6E436861696E2E50726F766964657243726564656E7469616C2E7631");
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        internal static async Task<OnChainProviderConfigurationDocument> LoadAsync(string folderPath)
        {
            var path = Path.Combine(folderPath, ConfigurationsFileName);
            if (!File.Exists(path))
            {
                return new OnChainProviderConfigurationDocument();
            }

            try
            {
                var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                var document = JsonSerializer.Deserialize<OnChainProviderConfigurationDocument>(json, JsonOptions);
                if (document == null || document.Configurations == null)
                {
                    return new OnChainProviderConfigurationDocument();
                }
                if (document.Version == 1)
                {
                    foreach (var configuration in document.Configurations)
                    {
                        configuration.ChainNamespace = ChainNamespaces.Solana;
                        configuration.ChainId = "mainnet-beta";
                    }
                    document.SelectedConfigurationIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (!string.IsNullOrWhiteSpace(document.SelectedConfigurationId))
                    {
                        document.SelectedConfigurationIds[GetNetworkKey(ChainNamespaces.Solana, "mainnet-beta")] =
                            document.SelectedConfigurationId;
                    }
                    document.SelectedConfigurationId = null;
                    document.Version = 2;
                }
                if (document.Version != 2
                    || document.Configurations.Any(configuration => !IsValidPublicConfiguration(configuration))
                    || document.Configurations.GroupBy(static configuration => configuration.Id, StringComparer.OrdinalIgnoreCase).Any(static group => group.Count() != 1)
                    || document.Configurations.GroupBy(static configuration =>
                        $"{configuration.ChainNamespace}|{configuration.ChainId}|{configuration.ProviderType}",
                        StringComparer.Ordinal).Any(static group => group.Count() != 1))
                {
                    return new OnChainProviderConfigurationDocument();
                }
                document.SelectedConfigurationIds ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                document.FallbackConfigurationIds ??= new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                if (document.SelectedConfigurationIds.Any(selection =>
                        !document.Configurations.Any(configuration =>
                            string.Equals(configuration.Id, selection.Value, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(
                                GetNetworkKey(configuration.ChainNamespace, configuration.ChainId),
                                selection.Key,
                                StringComparison.OrdinalIgnoreCase))))
                {
                    return new OnChainProviderConfigurationDocument();
                }
                if (document.FallbackConfigurationIds.Any(route =>
                        route.Value == null
                        || route.Value.Distinct(StringComparer.OrdinalIgnoreCase).Count() != route.Value.Length
                        || route.Value.Any(configurationId =>
                            !document.Configurations.Any(configuration =>
                                string.Equals(configuration.Id, configurationId, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(
                                    GetNetworkKey(configuration.ChainNamespace, configuration.ChainId),
                                    route.Key,
                                    StringComparison.OrdinalIgnoreCase)))
                        || route.Value.Take(Math.Max(0, route.Value.Length - 1)).Any(configurationId =>
                            document.Configurations.Any(configuration =>
                                string.Equals(configuration.Id, configurationId, StringComparison.OrdinalIgnoreCase)
                                && OnChainProviderCatalog.IsPublicEvaluationProvider(configuration.ProviderType)))
                        || (document.SelectedConfigurationIds.TryGetValue(route.Key, out var selectedId)
                            && route.Value.Contains(selectedId, StringComparer.OrdinalIgnoreCase))))
                {
                    return new OnChainProviderConfigurationDocument();
                }
                foreach (var configuration in document.Configurations)
                {
                    configuration.ReplayEnabled = OnChainProviderCatalog.Get(configuration.ProviderType).ReplayEnabled;
                }
                return document;
            }
            catch
            {
                return new OnChainProviderConfigurationDocument();
            }
        }

        internal static OnChainProviderConfigurationDocument CreateFirstRunDocument()
        {
            var ethereum = CreateConfiguration(OnChainProviderCatalog.Get(
                OnChainProviderTypes.PublicNodeEthereum));
            var @base = CreateConfiguration(OnChainProviderCatalog.Get(
                OnChainProviderTypes.BasePublic));
            var bnb = CreateConfiguration(OnChainProviderCatalog.Get(
                OnChainProviderTypes.PublicNodeBnb));
            var robinhood = CreateConfiguration(OnChainProviderCatalog.Get(
                OnChainProviderTypes.PublicNodeRobinhood));
            return new OnChainProviderConfigurationDocument
            {
                Version = 2,
                Configurations = [ethereum, @base, bnb, robinhood],
                SelectedConfigurationIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [GetNetworkKey(ethereum.ChainNamespace, ethereum.ChainId)] = ethereum.Id,
                    [GetNetworkKey(@base.ChainNamespace, @base.ChainId)] = @base.Id,
                    [GetNetworkKey(bnb.ChainNamespace, bnb.ChainId)] = bnb.Id,
                    [GetNetworkKey(robinhood.ChainNamespace, robinhood.ChainId)] = robinhood.Id
                }
            };
        }

        internal static OnChainProviderConfiguration CreateConfiguration(OnChainProviderPreset preset)
        {
            return new OnChainProviderConfiguration
            {
                Id = Guid.NewGuid().ToString("N"),
                ChainNamespace = preset.ChainNamespace,
                ChainId = preset.ChainId,
                ProviderType = preset.ProviderType,
                FriendlyName = preset.DisplayName,
                StreamEndpoint = preset.DefaultStreamEndpoint,
                RpcEndpoint = preset.DefaultRpcEndpoint,
                CredentialReference = Guid.NewGuid().ToString("N"),
                Commitment = OnChainCommitment.Processed,
                ReplayEnabled = preset.ReplayEnabled
            };
        }

        internal static async Task SaveAsync(string folderPath, OnChainProviderConfigurationDocument document)
        {
            Directory.CreateDirectory(folderPath);
            document.Version = 2;
            document.Configurations ??= [];
            document.SelectedConfigurationIds ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            document.FallbackConfigurationIds ??= new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            if (document.SelectedConfigurationIds.Count == 0
                && !string.IsNullOrWhiteSpace(document.SelectedConfigurationId))
            {
                var selected = document.Configurations.FirstOrDefault(configuration => string.Equals(
                    configuration.Id,
                    document.SelectedConfigurationId,
                    StringComparison.OrdinalIgnoreCase));
                if (selected != null)
                {
                    document.SelectedConfigurationIds[GetNetworkKey(
                        selected.ChainNamespace,
                        selected.ChainId)] = selected.Id;
                }
            }
            document.SelectedConfigurationId = null;
            var json = JsonSerializer.Serialize(document, JsonOptions);
            var path = Path.Combine(folderPath, ConfigurationsFileName);
            var temporaryPath = path + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, json).ConfigureAwait(false);
            File.Move(temporaryPath, path, true);
        }

        internal static async Task SaveCredentialAsync(
            string folderPath,
            string credentialReference,
            string credential)
        {
            ValidateCredentialReference(credentialReference);
            if (string.IsNullOrWhiteSpace(credential))
            {
                throw new ArgumentException("API key cannot be empty.", nameof(credential));
            }

            var plaintext = Encoding.UTF8.GetBytes(credential.Trim());
            try
            {
                var protectedBytes = ProtectedData.Protect(plaintext, OptionalEntropy, DataProtectionScope.CurrentUser);
                var secretsPath = Path.Combine(folderPath, SecretsDirectoryName);
                Directory.CreateDirectory(secretsPath);
                var path = Path.Combine(secretsPath, credentialReference + ".bin");
                var temporaryPath = path + ".tmp";
                await File.WriteAllBytesAsync(temporaryPath, protectedBytes).ConfigureAwait(false);
                File.Move(temporaryPath, path, true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        internal static async Task<string?> ReadCredentialAsync(string folderPath, string credentialReference)
        {
            ValidateCredentialReference(credentialReference);
            var path = Path.Combine(folderPath, SecretsDirectoryName, credentialReference + ".bin");
            if (!File.Exists(path))
            {
                return null;
            }

            var protectedBytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            var plaintext = ProtectedData.Unprotect(protectedBytes, OptionalEntropy, DataProtectionScope.CurrentUser);
            try
            {
                return Encoding.UTF8.GetString(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        internal static void DeleteCredential(string folderPath, string credentialReference)
        {
            ValidateCredentialReference(credentialReference);
            var path = Path.Combine(folderPath, SecretsDirectoryName, credentialReference + ".bin");
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        internal static bool CanReuseCredential(
            OnChainProviderConfiguration source,
            OnChainProviderPreset target)
        {
            var sourcePreset = OnChainProviderCatalog.Get(source.ProviderType);
            return OnChainProviderCatalog.SharesCredentialAcrossChains(sourcePreset)
                   && OnChainProviderCatalog.SharesCredentialAcrossChains(target)
                   && string.Equals(
                       sourcePreset.ProviderFamily,
                       target.ProviderFamily,
                       StringComparison.Ordinal);
        }

        internal static OnChainProviderConfiguration[] LinkSharedCredentialConfigurations(
            IEnumerable<OnChainProviderConfiguration> configurations,
            OnChainProviderPreset source,
            string credentialReference,
            ISet<string> replacedCredentialReferences)
        {
            ValidateCredentialReference(credentialReference);
            ArgumentNullException.ThrowIfNull(configurations);
            ArgumentNullException.ThrowIfNull(replacedCredentialReferences);
            var linkedConfigurations = configurations.ToList();
            if (!OnChainProviderCatalog.SharesCredentialAcrossChains(source))
            {
                return linkedConfigurations.ToArray();
            }

            foreach (var linked in linkedConfigurations.Where(item =>
                         CanReuseCredential(item, source)))
            {
                if (string.Equals(
                        linked.CredentialReference,
                        credentialReference,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                replacedCredentialReferences.Add(linked.CredentialReference);
                linked.CredentialReference = credentialReference;
            }

            foreach (var target in OnChainProviderCatalog.GetAutomaticSharedCredentialTargets(source))
            {
                if (linkedConfigurations.Any(item => string.Equals(
                        item.ProviderType,
                        target.ProviderType,
                        StringComparison.Ordinal)))
                {
                    continue;
                }
                var linked = CreateConfiguration(target);
                linked.CredentialReference = credentialReference;
                linkedConfigurations.Add(linked);
            }
            return linkedConfigurations.ToArray();
        }

        internal static void DeleteCredentialIfUnreferenced(
            string folderPath,
            IEnumerable<OnChainProviderConfiguration> configurations,
            string credentialReference)
        {
            if (!configurations.Any(configuration => string.Equals(
                    configuration.CredentialReference,
                    credentialReference,
                    StringComparison.OrdinalIgnoreCase)))
            {
                DeleteCredential(folderPath, credentialReference);
            }
        }

        internal static OnChainProviderConfiguration? RemoveConfiguration(
            OnChainProviderConfigurationDocument document,
            string configurationId)
        {
            ArgumentNullException.ThrowIfNull(document);
            var removed = document.Configurations.FirstOrDefault(configuration =>
                string.Equals(configuration.Id, configurationId, StringComparison.OrdinalIgnoreCase));
            if (removed == null)
            {
                return null;
            }

            document.Configurations = document.Configurations
                .Where(configuration => !string.Equals(
                    configuration.Id,
                    configurationId,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            document.SelectedConfigurationIds ??=
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var networkKey = GetNetworkKey(removed.ChainNamespace, removed.ChainId);
            if (document.SelectedConfigurationIds.TryGetValue(networkKey, out var selected)
                && string.Equals(selected, removed.Id, StringComparison.OrdinalIgnoreCase))
            {
                document.SelectedConfigurationIds.Remove(networkKey);
            }
            document.FallbackConfigurationIds ??=
                new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var route in document.FallbackConfigurationIds.ToArray())
            {
                var remaining = route.Value
                    .Where(id => !string.Equals(id, removed.Id, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (remaining.Length == 0)
                {
                    document.FallbackConfigurationIds.Remove(route.Key);
                }
                else
                {
                    document.FallbackConfigurationIds[route.Key] = remaining;
                }
            }
            return removed;
        }

        private static bool IsValidPublicConfiguration(OnChainProviderConfiguration? configuration)
        {
            return configuration != null
                   && Guid.TryParseExact(configuration.Id, "N", out _)
                   && Guid.TryParseExact(configuration.CredentialReference, "N", out _)
                   && OnChainProviderCatalog.IsSupported(configuration.ProviderType)
                   && string.Equals(
                       configuration.ChainNamespace,
                       OnChainProviderCatalog.Get(configuration.ProviderType).ChainNamespace,
                       StringComparison.Ordinal)
                   && string.Equals(
                       configuration.ChainId,
                       OnChainProviderCatalog.Get(configuration.ProviderType).ChainId,
                       StringComparison.Ordinal)
                   && IsSecureStreamEndpoint(
                       configuration.StreamEndpoint,
                       OnChainProviderCatalog.Get(configuration.ProviderType).StreamTransport,
                       OnChainProviderCatalog.Get(configuration.ProviderType).StreamAuthenticationMode)
                   && IsSecureEndpoint(
                       configuration.RpcEndpoint,
                       OnChainProviderCatalog.Get(configuration.ProviderType).RpcAuthenticationMode)
                   && HasCredentialFreePath(configuration);
        }

        internal static bool IsSecureEndpoint(string? endpoint)
        {
            return IsSecureEndpoint(endpoint, OnChainEndpointAuthenticationMode.None);
        }

        internal static bool IsSecureEndpoint(
            string? endpoint,
            OnChainEndpointAuthenticationMode authenticationMode)
        {
            return Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri)
                   && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                   && string.IsNullOrEmpty(uri.UserInfo)
                   && string.IsNullOrEmpty(uri.Fragment)
                   && HasAllowedPublicQuery(uri, authenticationMode);
        }

        internal static bool IsSecureStreamEndpoint(
            string? endpoint,
            OnChainStreamTransport transport,
            OnChainEndpointAuthenticationMode authenticationMode = OnChainEndpointAuthenticationMode.None)
        {
            if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri)
                || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Fragment)
                || !HasAllowedPublicQuery(uri, authenticationMode))
            {
                return false;
            }
            return transport is OnChainStreamTransport.SolanaWebSocket or OnChainStreamTransport.EvmWebSocket
                ? string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase)
                : string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool HasCredentialFreePath(OnChainProviderConfiguration configuration)
        {
            var expectedPaths = configuration.ProviderType switch
            {
                OnChainProviderTypes.QuickNodeWebSocket => (Stream: string.Empty, Rpc: string.Empty),
                OnChainProviderTypes.ChainstackWebSocket => (Stream: string.Empty, Rpc: string.Empty),
                OnChainProviderTypes.DrpcWebSocket => (Stream: "solana", Rpc: "solana"),
                OnChainProviderTypes.AlchemyEthereum => (Stream: "v2", Rpc: "v2"),
                OnChainProviderTypes.DrpcEthereum => (Stream: "ethereum", Rpc: "ethereum"),
                OnChainProviderTypes.InfuraEthereum => (Stream: "ws/v3", Rpc: "v3"),
                OnChainProviderTypes.QuickNodeEthereum => (Stream: string.Empty, Rpc: string.Empty),
                OnChainProviderTypes.ChainstackEthereum => (Stream: string.Empty, Rpc: string.Empty),
                OnChainProviderTypes.AlchemyBase => (Stream: "v2", Rpc: "v2"),
                OnChainProviderTypes.DrpcBase => (Stream: "base", Rpc: "base"),
                OnChainProviderTypes.ChainstackBase => (Stream: string.Empty, Rpc: string.Empty),
                OnChainProviderTypes.AlchemyRobinhood => (Stream: "v2", Rpc: "v2"),
                OnChainProviderTypes.QuickNodeRobinhood => (Stream: string.Empty, Rpc: string.Empty),
                OnChainProviderTypes.ChainstackRobinhood => (Stream: string.Empty, Rpc: string.Empty),
                _ => ((string Stream, string Rpc)?)null
            };
            return expectedPaths == null
                   || HasExpectedPath(configuration.StreamEndpoint, expectedPaths.Value.Stream)
                   && HasExpectedPath(configuration.RpcEndpoint, expectedPaths.Value.Rpc);
        }

        internal static string GetNetworkKey(string chainNamespace, string chainId)
        {
            return $"{chainNamespace.Trim()}:{chainId.Trim()}";
        }

        private static bool HasAllowedPublicQuery(
            Uri uri,
            OnChainEndpointAuthenticationMode authenticationMode)
        {
            if (string.IsNullOrEmpty(uri.Query))
            {
                return true;
            }
            return false;
        }

        private static bool HasExpectedPath(string endpoint, string expectedPath)
        {
            return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                   && string.Equals(
                       uri.AbsolutePath.Trim('/'),
                       expectedPath,
                       StringComparison.Ordinal);
        }

        private static void ValidateCredentialReference(string credentialReference)
        {
            if (!Guid.TryParseExact(credentialReference, "N", out _))
            {
                throw new ArgumentException("Credential reference is invalid.", nameof(credentialReference));
            }
        }
    }
}

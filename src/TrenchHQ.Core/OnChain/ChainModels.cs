using TrenchHQ.Core.OnChain;
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrenchHQ.Core.OnChain
{
    internal static class ChainNamespaces
    {
        internal const string Solana = "solana";
        internal const string Eip155 = "eip155";
    }

    internal sealed class OnChainAssetKey
    {
        public string ChainNamespace { get; set; } = ChainNamespaces.Solana;
        public string ChainId { get; set; } = "mainnet-beta";
        public string Address { get; set; } = string.Empty;
    }

    internal sealed class OnChainDeploymentKey
    {
        public string ChainNamespace { get; set; } = ChainNamespaces.Solana;
        public string ChainId { get; set; } = "mainnet-beta";
        public string ProtocolId { get; set; } = string.Empty;
        public string ContractAddress { get; set; } = string.Empty;
    }

    [JsonConverter(typeof(OnChainPoolKeyJsonConverter))]
    internal sealed class OnChainPoolKey
    {
        public OnChainDeploymentKey DeploymentKey { get; set; } = new();
        public string PoolId { get; set; } = string.Empty;

        [JsonIgnore]
        public string ChainNamespace
        {
            get => DeploymentKey.ChainNamespace;
            set => DeploymentKey.ChainNamespace = value;
        }

        [JsonIgnore]
        public string ChainId
        {
            get => DeploymentKey.ChainId;
            set => DeploymentKey.ChainId = value;
        }

        [JsonIgnore]
        public string ProtocolId
        {
            get => DeploymentKey.ProtocolId;
            set => DeploymentKey.ProtocolId = value;
        }

        [JsonIgnore]
        public string PoolAddress
        {
            get => PoolId;
            set => PoolId = value;
        }
    }

    internal enum OnChainPositionKind
    {
        Solana,
        Evm
    }

    internal sealed class OnChainPosition
    {
        public OnChainPositionKind Kind { get; set; }
        public ulong? Slot { get; set; }
        public ulong? BlockNumber { get; set; }
        public string? BlockHash { get; set; }
        public string? ParentHash { get; set; }
        public ulong? TransactionIndex { get; set; }
        public ulong? LogIndex { get; set; }
    }

    internal enum EvmFinality
    {
        Head,
        Safe,
        Finalized
    }

    internal enum OnChainFinalityKind
    {
        Solana,
        Evm
    }

    internal sealed class OnChainFinality
    {
        public OnChainFinalityKind Kind { get; set; }
        public OnChainCommitment? Commitment { get; set; }
        public EvmFinality? Status { get; set; }
    }

    internal sealed class OnChainPoolKeyJsonConverter : JsonConverter<OnChainPoolKey>
    {
        public override OnChainPoolKey Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Pool key must be an object.");
            }

            var key = new OnChainPoolKey();
            if (TryGetProperty(root, "deploymentKey", out var deployment))
            {
                if (deployment.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("Pool deployment key must be an object.");
                }
                key.DeploymentKey.ChainNamespace = ReadString(
                    deployment,
                    "chainNamespace",
                    key.DeploymentKey.ChainNamespace);
                key.DeploymentKey.ChainId = ReadString(
                    deployment,
                    "chainId",
                    key.DeploymentKey.ChainId);
                key.DeploymentKey.ProtocolId = ReadString(deployment, "protocolId", string.Empty);
                key.DeploymentKey.ContractAddress = ReadString(
                    deployment,
                    "contractAddress",
                    string.Empty);
                key.PoolId = ReadString(root, "poolId", string.Empty);
                return key;
            }

            // Read-only compatibility for existing pre-Ethereum Widget files.
            key.DeploymentKey.ChainNamespace = ReadString(
                root,
                "chainNamespace",
                key.DeploymentKey.ChainNamespace);
            key.DeploymentKey.ChainId = ReadString(root, "chainId", key.DeploymentKey.ChainId);
            key.DeploymentKey.ProtocolId = ReadString(root, "protocolId", string.Empty);
            key.PoolId = ReadString(root, "poolAddress", string.Empty);
            return key;
        }

        public override void Write(
            Utf8JsonWriter writer,
            OnChainPoolKey value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("deploymentKey");
            writer.WriteStartObject();
            writer.WriteString("chainNamespace", value.DeploymentKey.ChainNamespace);
            writer.WriteString("chainId", value.DeploymentKey.ChainId);
            writer.WriteString("protocolId", value.DeploymentKey.ProtocolId);
            writer.WriteString("contractAddress", value.DeploymentKey.ContractAddress);
            writer.WriteEndObject();
            writer.WriteString("poolId", value.PoolId);
            writer.WriteEndObject();
        }

        private static string ReadString(JsonElement parent, string name, string fallback)
        {
            return TryGetProperty(parent, name, out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? fallback
                : fallback;
        }

        private static bool TryGetProperty(
            JsonElement parent,
            string name,
            out JsonElement value)
        {
            foreach (var property in parent.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
            value = default;
            return false;
        }
    }
}

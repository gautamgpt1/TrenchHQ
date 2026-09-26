using System.Text.Json.Serialization;

namespace TrenchHQ.Models
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class EvmBlockReference
    {
        public ulong Number { get; set; }
        public string Hash { get; set; } = string.Empty;
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class EvmHeadUpdate
    {
        public string ChainId { get; set; } = string.Empty;
        public ulong ConnectionEpoch { get; set; }
        public ulong Number { get; set; }
        public string Hash { get; set; } = string.Empty;
        public string ParentHash { get; set; } = string.Empty;
        public ulong Timestamp { get; set; }
        public long ObservedAtUnixMs { get; set; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class EvmLogUpdate
    {
        public string ChainId { get; set; } = string.Empty;
        public ulong ConnectionEpoch { get; set; }
        public string Address { get; set; } = string.Empty;
        public string[] Topics { get; set; } = [];
        public string Data { get; set; } = string.Empty;
        public ulong BlockNumber { get; set; }
        public string BlockHash { get; set; } = string.Empty;
        public string TransactionHash { get; set; } = string.Empty;
        public ulong TransactionIndex { get; set; }
        public ulong LogIndex { get; set; }
        public bool Removed { get; set; }
        public long ObservedAtUnixMs { get; set; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class EvmSnapshotCall
    {
        public string Id { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string ReturnData { get; set; } = string.Empty;
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class EvmSnapshotResponse
    {
        public string RequestId { get; set; } = string.Empty;
        public OnChainPoolKey PoolKey { get; set; } = new();
        public ulong BlockNumber { get; set; }
        public string BlockHash { get; set; } = string.Empty;
        public EvmSnapshotCall[] Calls { get; set; } = [];
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class EvmFinalityUpdate
    {
        public string ChainId { get; set; } = string.Empty;
        public EvmBlockReference Head { get; set; } = new();
        public EvmBlockReference? Safe { get; set; }
        public EvmBlockReference? Finalized { get; set; }
        public long ObservedAtUnixMs { get; set; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class EvmRollback
    {
        public string ChainId { get; set; } = string.Empty;
        public ulong ToBlockNumber { get; set; }
        public string ToBlockHash { get; set; } = string.Empty;
    }
}

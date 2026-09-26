using System;

namespace TrenchHQ.Models
{
    internal enum WalletActivityKind
    {
        Transaction,
        TokenTransfer,
        NativeTransfer,
        InternalTransfer
    }

    internal enum WalletActivityDirection
    {
        Interaction,
        Incoming,
        Outgoing,
        Self
    }

    internal enum WalletStreamState
    {
        Disconnected,
        Connecting,
        Live,
        Reconnecting,
        Unavailable
    }

    internal sealed class WalletActivityUpdate
    {
        public string EventId { get; set; } = string.Empty;
        public string ChainNamespace { get; set; } = string.Empty;
        public string ChainId { get; set; } = string.Empty;
        public string WalletAddress { get; set; } = string.Empty;
        public string WalletLabel { get; set; } = string.Empty;
        public string TransactionId { get; set; } = string.Empty;
        public ulong ChainPosition { get; set; }
        public WalletActivityKind Kind { get; set; }
        public WalletActivityDirection Direction { get; set; }
        public string? Counterparty { get; set; }
        public string? VenueId { get; set; }
        public string? AssetAddress { get; set; }
        public string? AssetId { get; set; }
        public string? AmountRaw { get; set; }
        public string? AssetSymbol { get; set; }
        public byte? AssetDecimals { get; set; }
        public bool Failed { get; set; }
        public bool Removed { get; set; }
        public string Confirmation { get; set; } = string.Empty;
        public long ObservedAtUnixMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public string SourceId { get; set; } = string.Empty;
    }

    internal sealed class WalletActivityChangedEventArgs(WalletActivityUpdate update) : EventArgs
    {
        internal WalletActivityUpdate Update { get; } = update;
    }

    internal sealed class WalletStreamStateChangedEventArgs(
        string chainNamespace,
        string chainId,
        WalletStreamState state,
        string? error = null) : EventArgs
    {
        internal string ChainNamespace { get; } = chainNamespace;
        internal string ChainId { get; } = chainId;
        internal WalletStreamState State { get; } = state;
        internal string? Error { get; } = error;
    }

    internal sealed class SolanaWalletSignature
    {
        internal string Signature { get; init; } = string.Empty;
        internal ulong Slot { get; init; }
        internal bool Failed { get; init; }
        internal long? BlockTimeUnixSeconds { get; init; }
        internal string Confirmation { get; init; } = "confirmed";
    }

    internal sealed class SolanaWalletSignatureStatus
    {
        internal string Signature { get; init; } = string.Empty;
        internal ulong Slot { get; init; }
        internal bool Failed { get; init; }
        internal string Confirmation { get; init; } = "confirmed";
    }

    internal sealed class SolanaWalletTransaction
    {
        internal string Signature { get; init; } = string.Empty;
        internal ulong Slot { get; init; }
        internal bool Failed { get; init; }
        internal ulong FeeRaw { get; init; }
        internal string[] AccountAddresses { get; init; } = [];
        internal ulong[] PreBalancesRaw { get; init; } = [];
        internal ulong[] PostBalancesRaw { get; init; } = [];
        internal SolanaWalletTokenBalance[] TokenBalances { get; init; } = [];
    }

    internal sealed class SolanaWalletTokenBalance
    {
        internal int AccountIndex { get; init; }
        internal string AccountAddress { get; init; } = string.Empty;
        internal string Mint { get; init; } = string.Empty;
        internal string OwnerAddress { get; init; } = string.Empty;
        internal byte Decimals { get; init; }
        internal ulong PreAmountRaw { get; init; }
        internal ulong PostAmountRaw { get; init; }
    }

    internal sealed class EvmWalletBlock
    {
        internal ulong Number { get; init; }
        internal string Hash { get; init; } = string.Empty;
        internal string ParentHash { get; init; } = string.Empty;
        internal ulong Timestamp { get; init; }
        internal EvmWalletTransaction[] Transactions { get; init; } = [];
    }

    internal sealed class EvmWalletTransaction
    {
        internal string Hash { get; init; } = string.Empty;
        internal string From { get; init; } = string.Empty;
        internal string? To { get; init; }
        internal string ValueRaw { get; init; } = "0";
    }

    internal sealed class EvmWalletTrace
    {
        internal string TransactionHash { get; init; } = string.Empty;
        internal string TracePath { get; init; } = string.Empty;
        internal string From { get; init; } = string.Empty;
        internal string? To { get; init; }
        internal string ValueRaw { get; init; } = "0";
        internal ulong BlockNumber { get; init; }
    }
}

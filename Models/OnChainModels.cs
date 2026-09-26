using System;
using System.Collections.Generic;

namespace TrenchHQ.Models
{
    internal static class OnChainProtocolIds
    {
        internal const string PumpBondingCurve = "pumpBondingCurve";
        internal const string PumpSwap = "pumpSwap";
        internal const string RaydiumAmmV4 = "raydiumAmmV4";
        internal const string RaydiumCpmm = "raydiumCpmm";
        internal const string RaydiumClmm = "raydiumClmm";
        internal const string MeteoraDammV1 = "meteoraDammV1";
        internal const string MeteoraDammV2 = "meteoraDammV2";
        internal const string MeteoraDlmm = "meteoraDlmm";
        internal const string OrcaWhirlpool = "orcaWhirlpool";
        internal const string ManifestOrderbook = "manifestOrderbook";
        internal const string UniswapV2 = "uniswap-v2";
        internal const string UniswapV3 = "uniswap-v3";
        internal const string UniswapV4 = "uniswap-v4";
        internal const string AerodromeClassic = "aerodrome-classic";
        internal const string AerodromeSlipstream = "aerodrome-slipstream";
        internal const string PancakeV2 = "pancake-v2";
        internal const string PancakeV3 = "pancake-v3";
        internal const string PancakeInfinityCl = "pancake-infinity-cl";
        internal const string PancakeInfinityBin = "pancake-infinity-bin";
        internal const string Curve = "curve";
        internal const string FermiSwap = "fermi-swap";
        internal const string PonsV2Curve = "pons-v2-curve";
    }

    internal enum OnChainSupportStatus
    {
        Supported,
        DiscoveredUnsupported,
        InvalidCandidate,
        TemporarilyUnavailable
    }

    internal enum OnChainCommitment
    {
        Processed,
        Confirmed,
        Finalized
    }

    internal enum OnChainRecoveryState
    {
        Connecting,
        Live,
        Reconnecting,
        Replaying,
        Reconciling,
        SnapshotRequired,
        Stale
    }

    internal sealed class OnChainPoolDescriptor
    {
        public OnChainPoolKey PoolKey { get; set; } = new();
        public string PoolType { get; set; } = string.Empty;
        public string ProgramId { get; set; } = string.Empty;
        public string BaseMint { get; set; } = string.Empty;
        public string QuoteMint { get; set; } = string.Empty;
        public byte BaseDecimals { get; set; }
        public byte QuoteDecimals { get; set; }
        public string? BaseVault { get; set; }
        public string? QuoteVault { get; set; }
        public OnChainProtocolAccount[] ProtocolAccounts { get; set; } = [];
        public string PairOrientation { get; set; } = "selectedAsBase";
        public ulong? DiscoveredAtSlot { get; set; }
        public string DiscoverySource { get; set; } = string.Empty;
        public OnChainSupportStatus SupportStatus { get; set; } = OnChainSupportStatus.Supported;
        public string? SupportReason { get; set; }
        public OnChainAssetKey? Asset0 { get; set; }
        public OnChainAssetKey? Asset1 { get; set; }
        public ulong? ValidationBlock { get; set; }
        public string? ValidationBlockHash { get; set; }
        public uint? FeeTier { get; set; }
        public int? TickSpacing { get; set; }
        public ushort? BinStep { get; set; }
        public string? HookAddress { get; set; }
        public string? PricingMode { get; set; }

        internal IEnumerable<string?> EnumerateSolanaAccountAddresses()
        {
            yield return PoolKey.PoolAddress;
            yield return BaseVault;
            yield return QuoteVault;
            foreach (var account in ProtocolAccounts)
            {
                yield return account.Address;
            }
        }
    }

    internal sealed class OnChainProtocolAccount
    {
        public string Role { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public uint? Index { get; set; }
    }

    internal sealed class OnChainPoolDiscoveryResult
    {
        public string Mint { get; set; } = string.Empty;
        public OnChainPoolDescriptor[] Pools { get; set; } = [];
        public bool Truncated { get; set; }
        public string[] Warnings { get; set; } = [];
    }

    internal sealed class OnChainWatchedPoolSelection
    {
        public OnChainPoolDescriptor Descriptor { get; set; } = new();
        public string SelectedMint { get; set; } = string.Empty;
    }

    internal sealed class OnChainDecimalValue
    {
        public string Coefficient { get; set; } = "0";
        public uint Scale { get; set; }
    }

    internal sealed class OnChainPriceUpdate
    {
        public OnChainPoolKey PoolKey { get; set; } = new();
        public ulong StreamEpoch { get; set; }
        public ulong Sequence { get; set; }
        public OnChainDecimalValue? LastTradePriceQuote { get; set; }
        public string? LastTradeEventId { get; set; }
        public OnChainDecimalValue? SpotPriceQuote { get; set; }
        public OnChainDecimalValue? PriceSol { get; set; }
        public OnChainDecimalValue? PriceUsd { get; set; }
        public OnChainDecimalValue? PriceNative { get; set; }
        public string QuoteMint { get; set; } = string.Empty;
        public OnChainAssetKey? QuoteAsset { get; set; }
        public ulong Slot { get; set; }
        public OnChainCommitment Commitment { get; set; }
        public OnChainPosition? ChainPosition { get; set; }
        public OnChainFinality? Finality { get; set; }
        public string SourceId { get; set; } = string.Empty;
        public string? ProviderProfileId { get; set; }
        public string? ReferenceSourceId { get; set; }
        public long? ReferenceObservedAtUnixMs { get; set; }
        public long ObservedAtUnixMs { get; set; }
        public ulong? ChainAgeMs { get; set; }
        public bool Stale { get; set; }
        public bool Replaying { get; set; }
        public OnChainRecoveryState RecoveryState { get; set; }
    }

    internal sealed class OnChainDecodedPoolAccount
    {
        public string ProtocolId { get; set; } = string.Empty;
        public string PoolType { get; set; } = string.Empty;
        public string PoolAddress { get; set; } = string.Empty;
        public string ProgramId { get; set; } = string.Empty;
        public string BaseMint { get; set; } = string.Empty;
        public string QuoteMint { get; set; } = string.Empty;
        public string? BaseVault { get; set; }
        public string? QuoteVault { get; set; }
        public OnChainProtocolAccount[] ProtocolAccounts { get; set; } = [];
        public bool? Complete { get; set; }
        public bool? Enabled { get; set; }
        public string? Layout { get; set; }
        public string? VirtualQuoteReserves { get; set; }
    }

    internal sealed class OnChainDerivedAddress
    {
        public string Address { get; set; } = string.Empty;
        public byte Bump { get; set; }
    }

    internal sealed class OnChainRawAccountUpdate
    {
        public string Pubkey { get; set; } = string.Empty;
        public string OwnerProgram { get; set; } = string.Empty;
        public string DataBase64 { get; set; } = string.Empty;
        public ulong Slot { get; set; }
        public ulong WriteVersion { get; set; }
        public OnChainCommitment Commitment { get; set; }
        public string SourceId { get; set; } = string.Empty;
        public long ObservedAtUnixMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    internal sealed class OnChainRawTransactionUpdate
    {
        public string Signature { get; set; } = string.Empty;
        public ulong Slot { get; set; }
        public bool Failed { get; set; }
        public OnChainCommitment Commitment { get; set; }
        public string SourceId { get; set; } = string.Empty;
        public long ObservedAtUnixMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public OnChainRawInstruction[] Instructions { get; set; } = [];
        public OnChainRawProgramData[] ProgramData { get; set; } = [];
        public OnChainRawTokenBalance[] TokenBalances { get; set; } = [];
    }

    internal sealed class OnChainRawProgramData
    {
        public string ProgramId { get; set; } = string.Empty;
        public string DataBase64 { get; set; } = string.Empty;
        public uint LogIndex { get; set; }
    }

    internal sealed class OnChainRawInstruction
    {
        public string ProgramId { get; set; } = string.Empty;
        public string DataBase64 { get; set; } = string.Empty;
        public ushort OuterInstructionIndex { get; set; }
        public ushort? InnerInstructionIndex { get; set; }
        public uint? StackHeight { get; set; }
        public string[] AccountAddresses { get; set; } = [];
    }

    internal sealed class OnChainRawTokenBalance
    {
        public string AccountAddress { get; set; } = string.Empty;
        public string Mint { get; set; } = string.Empty;
        public ulong PreAmountRaw { get; set; }
        public ulong PostAmountRaw { get; set; }
    }
}

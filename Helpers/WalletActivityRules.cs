using TrenchHQ.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace TrenchHQ.Helpers
{
    internal static class WalletActivityRules
    {
        private static readonly IReadOnlyDictionary<string, string> SolanaVenues =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P"] = OnChainProtocolIds.PumpBondingCurve,
                ["pAMMBay6oceH9fJKBRHGP5D4bD4sWpmSwMn52FMfXEA"] = OnChainProtocolIds.PumpSwap,
                ["675kPX9MHTjS2zt1qfr1NYHuzeLXfQM9H24wFSUt1Mp8"] = OnChainProtocolIds.RaydiumAmmV4,
                ["CPMMoo8L3F4NbTegBCKVNunggL7H1ZpdTHKxQB5qKP1C"] = OnChainProtocolIds.RaydiumCpmm,
                ["CAMMCzo5YL8w4VFF8KVHrK22GGUsp5VTaW7grrKgrWqK"] = OnChainProtocolIds.RaydiumClmm,
                ["Eo7WjKq67rjJQSZxS6z3YkapzY3eMj6Xy8X5EQVn5UaB"] = OnChainProtocolIds.MeteoraDammV1,
                ["cpamdpZCGKUy5JxQXB4dcpGPiikHawvSWAd6mEn1sGG"] = OnChainProtocolIds.MeteoraDammV2,
                ["LBUZKhRxPF3XUpBCjp4YzTKgLccjZhTSDM9YuVaPwxo"] = OnChainProtocolIds.MeteoraDlmm,
                ["whirLbMiicVdio4qvUfM5KAg6Ct8VwpYzGff3uctyCc"] = OnChainProtocolIds.OrcaWhirlpool,
                ["iwhrLHdsgrvmnwU8GF2FSmyabSMjfHwFGJAX2ufJ3ZN"] = OnChainProtocolIds.OrcaWhirlpool,
                ["MNFSTqtC93rEfYHB6hF82sKdZpUDFWkViLByLd1k1Ms"] = OnChainProtocolIds.ManifestOrderbook
            };

        internal const string TransferTopic =
            "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef";
        internal const string TransferSingleTopic =
            "0xc3d58168c5ae7397731d063d5bbf3d657854427343f4c083240f7aacaa2d0f62";
        internal const string TransferBatchTopic =
            "0x4a39dc06d4c0dbc64b70af90fd698a233a518aa5d07e595d983b8c0526c8f7fb";

        internal static object[] CreateEvmLogFilters(
            IEnumerable<SavedTrackedWallet> wallets,
            string? fromBlock = null,
            string? toBlock = null)
        {
            var topics = wallets
                .Select(static wallet => ToAddressTopic(wallet.Address))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (topics.Length == 0)
            {
                return [];
            }

            object Create(object?[] topicFilter)
            {
                return fromBlock == null || toBlock == null
                    ? new { topics = topicFilter }
                    : new { fromBlock, toBlock, topics = topicFilter };
            }

            return
            [
                Create([TransferTopic, topics]),
                Create([TransferTopic, null, topics]),
                Create([TransferSingleTopic, null, topics]),
                Create([TransferSingleTopic, null, null, topics]),
                Create([TransferBatchTopic, null, topics]),
                Create([TransferBatchTopic, null, null, topics])
            ];
        }

        internal static WalletActivityUpdate[] ParseEvmTransfer(
            EvmLogUpdate log,
            IReadOnlyCollection<SavedTrackedWallet> wallets,
            string sourceId)
        {
            if (log.Topics.Length == 0)
            {
                return [];
            }

            string from;
            string to;
            string? assetId = null;
            string? amount;
            if (string.Equals(log.Topics[0], TransferTopic, StringComparison.OrdinalIgnoreCase))
            {
                if (log.Topics.Length < 3
                    || !TryReadTopicAddress(log.Topics[1], out from)
                    || !TryReadTopicAddress(log.Topics[2], out to))
                {
                    return [];
                }
                amount = log.Topics.Length >= 4
                    ? TryReadUnsigned(log.Topics[3])
                    : TryReadUnsigned(log.Data);
            }
            else if (string.Equals(log.Topics[0], TransferSingleTopic, StringComparison.OrdinalIgnoreCase))
            {
                if (log.Topics.Length < 4
                    || !TryReadTopicAddress(log.Topics[2], out from)
                    || !TryReadTopicAddress(log.Topics[3], out to)
                    || !TryReadWord(log.Data, 0, out assetId)
                    || !TryReadWord(log.Data, 32, out amount))
                {
                    return [];
                }
            }
            else if (string.Equals(log.Topics[0], TransferBatchTopic, StringComparison.OrdinalIgnoreCase))
            {
                if (log.Topics.Length < 4
                    || !TryReadTopicAddress(log.Topics[2], out from)
                    || !TryReadTopicAddress(log.Topics[3], out to)
                    || !TryReadErc1155Batch(log.Data, out assetId, out amount))
                {
                    return [];
                }
            }
            else
            {
                return [];
            }
            var updates = new List<WalletActivityUpdate>();
            foreach (var wallet in wallets)
            {
                var isFrom = string.Equals(wallet.Address, from, StringComparison.OrdinalIgnoreCase);
                var isTo = string.Equals(wallet.Address, to, StringComparison.OrdinalIgnoreCase);
                if (!isFrom && !isTo)
                {
                    continue;
                }
                var direction = isFrom && isTo
                    ? WalletActivityDirection.Self
                    : isFrom
                        ? WalletActivityDirection.Outgoing
                        : WalletActivityDirection.Incoming;
                updates.Add(new WalletActivityUpdate
                {
                    EventId = $"{ChainNamespaces.Eip155}|{log.ChainId}|{log.TransactionHash}|{log.LogIndex}|{wallet.Address}",
                    ChainNamespace = ChainNamespaces.Eip155,
                    ChainId = log.ChainId,
                    WalletAddress = wallet.Address,
                    WalletLabel = wallet.Label,
                    TransactionId = log.TransactionHash,
                    ChainPosition = log.BlockNumber,
                    Kind = WalletActivityKind.TokenTransfer,
                    Direction = direction,
                    Counterparty = direction switch
                    {
                        WalletActivityDirection.Incoming => from,
                        WalletActivityDirection.Outgoing => to,
                        _ => wallet.Address
                    },
                    AssetAddress = log.Address,
                    AssetId = assetId,
                    AmountRaw = amount,
                    Removed = log.Removed,
                    Confirmation = "head",
                    ObservedAtUnixMs = log.ObservedAtUnixMs,
                    SourceId = sourceId
                });
            }
            return updates.ToArray();
        }

        internal static WalletActivityUpdate[] ParseEvmTransaction(
            EvmWalletTransaction transaction,
            string chainId,
            ulong blockNumber,
            long observedAtUnixMs,
            IReadOnlyCollection<SavedTrackedWallet> wallets,
            string sourceId)
        {
            if (!EvmAddress.IsHash(transaction.Hash)
                || !EvmAddress.TryNormalize(transaction.From, out var from)
                || transaction.To != null && !EvmAddress.TryNormalize(transaction.To, out _)
                || !BigInteger.TryParse(transaction.ValueRaw, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                || value < BigInteger.Zero)
            {
                return [];
            }
            var to = transaction.To == null
                ? null
                : EvmAddress.TryNormalize(transaction.To, out var normalizedTo) ? normalizedTo : null;
            return CreateEvmValueUpdates(
                transaction.Hash,
                "transaction",
                from,
                to,
                value,
                chainId,
                blockNumber,
                observedAtUnixMs,
                wallets,
                sourceId,
                value > BigInteger.Zero ? WalletActivityKind.NativeTransfer : WalletActivityKind.Transaction);
        }

        internal static WalletActivityUpdate[] ParseEvmTrace(
            EvmWalletTrace trace,
            string chainId,
            long observedAtUnixMs,
            IReadOnlyCollection<SavedTrackedWallet> wallets,
            string sourceId)
        {
            if (!EvmAddress.IsHash(trace.TransactionHash)
                || string.IsNullOrWhiteSpace(trace.TracePath)
                || !EvmAddress.TryNormalize(trace.From, out var from)
                || trace.To == null
                || !EvmAddress.TryNormalize(trace.To, out var to)
                || !BigInteger.TryParse(trace.ValueRaw, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                || value <= BigInteger.Zero)
            {
                return [];
            }
            return CreateEvmValueUpdates(
                trace.TransactionHash,
                "trace:" + trace.TracePath,
                from,
                to,
                value,
                chainId,
                trace.BlockNumber,
                observedAtUnixMs,
                wallets,
                sourceId,
                WalletActivityKind.InternalTransfer);
        }

        internal static bool IsConfirmationUpgrade(string current, string candidate)
        {
            return ConfirmationRank(candidate) > ConfirmationRank(current);
        }

        internal static WalletActivityUpdate WithConfirmation(
            WalletActivityUpdate source,
            string confirmation,
            bool? failed = null)
        {
            return new WalletActivityUpdate
            {
                EventId = source.EventId,
                ChainNamespace = source.ChainNamespace,
                ChainId = source.ChainId,
                WalletAddress = source.WalletAddress,
                WalletLabel = source.WalletLabel,
                TransactionId = source.TransactionId,
                ChainPosition = source.ChainPosition,
                Kind = source.Kind,
                Direction = source.Direction,
                Counterparty = source.Counterparty,
                VenueId = source.VenueId,
                AssetAddress = source.AssetAddress,
                AssetId = source.AssetId,
                AmountRaw = source.AmountRaw,
                AssetSymbol = source.AssetSymbol,
                AssetDecimals = source.AssetDecimals,
                Failed = failed ?? source.Failed,
                Removed = source.Removed,
                Confirmation = confirmation,
                ObservedAtUnixMs = source.ObservedAtUnixMs,
                SourceId = source.SourceId
            };
        }

        internal static WalletActivityUpdate CreateSolanaTransaction(
            SavedTrackedWallet wallet,
            string signature,
            ulong slot,
            bool failed,
            string confirmation,
            long observedAtUnixMs,
            string sourceId,
            string? venueId = null)
        {
            return new WalletActivityUpdate
            {
                EventId = $"{ChainNamespaces.Solana}|mainnet-beta|{signature}|{wallet.Address}",
                ChainNamespace = ChainNamespaces.Solana,
                ChainId = "mainnet-beta",
                WalletAddress = wallet.Address,
                WalletLabel = wallet.Label,
                TransactionId = signature,
                ChainPosition = slot,
                Kind = WalletActivityKind.Transaction,
                Direction = WalletActivityDirection.Interaction,
                VenueId = venueId,
                Failed = failed,
                Confirmation = confirmation,
                ObservedAtUnixMs = observedAtUnixMs,
                SourceId = sourceId
            };
        }

        internal static WalletActivityUpdate[] ParseSolanaTransaction(
            SolanaWalletTransaction transaction,
            SavedTrackedWallet wallet,
            string confirmation,
            long observedAtUnixMs,
            string sourceId)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            ArgumentNullException.ThrowIfNull(wallet);
            if (transaction.AccountAddresses.Length == 0
                || transaction.AccountAddresses.Length != transaction.PreBalancesRaw.Length
                || transaction.AccountAddresses.Length != transaction.PostBalancesRaw.Length)
            {
                return [];
            }
            var venueId = GetSolanaVenueId(transaction.AccountAddresses);
            if (transaction.Failed)
            {
                return
                [
                    CreateSolanaTransaction(
                        wallet,
                        transaction.Signature,
                        transaction.Slot,
                        true,
                        confirmation,
                        observedAtUnixMs,
                        sourceId,
                        venueId)
                ];
            }

            var updates = new List<WalletActivityUpdate>();
            var ownedTokenBalances = transaction.TokenBalances
                .Where(balance => string.Equals(balance.OwnerAddress, wallet.Address, StringComparison.Ordinal))
                .ToArray();
            var nativeAccountIndexes = transaction.AccountAddresses
                .Select((address, index) => (address, index))
                .Where(item => string.Equals(item.address, wallet.Address, StringComparison.Ordinal))
                .Select(static item => item.index)
                .Concat(ownedTokenBalances
                    .Where(balance => !string.Equals(
                        balance.Mint,
                        WalletActivityPresentationRules.WrappedSolMint,
                        StringComparison.Ordinal))
                    .Select(static balance => balance.AccountIndex))
                .Distinct()
                .ToArray();
            var nativePre = nativeAccountIndexes.Aggregate(
                BigInteger.Zero,
                (total, index) => total + transaction.PreBalancesRaw[index]);
            var nativePost = nativeAccountIndexes.Aggregate(
                BigInteger.Zero,
                (total, index) => total + transaction.PostBalancesRaw[index]);
            if (string.Equals(transaction.AccountAddresses[0], wallet.Address, StringComparison.Ordinal))
            {
                nativePost += transaction.FeeRaw;
            }
            AddSolanaMovement(
                updates,
                transaction,
                wallet,
                null,
                "SOL",
                9,
                nativePre,
                nativePost,
                confirmation,
                observedAtUnixMs,
                sourceId,
                venueId);

            foreach (var mintGroup in ownedTokenBalances.GroupBy(static balance => balance.Mint, StringComparer.Ordinal))
            {
                var decimals = mintGroup.Select(static balance => balance.Decimals).Distinct().ToArray();
                if (decimals.Length != 1)
                {
                    continue;
                }
                var pre = mintGroup.Aggregate(
                    BigInteger.Zero,
                    static (total, balance) => total + balance.PreAmountRaw);
                var post = mintGroup.Aggregate(
                    BigInteger.Zero,
                    static (total, balance) => total + balance.PostAmountRaw);
                AddSolanaMovement(
                    updates,
                    transaction,
                    wallet,
                    mintGroup.Key,
                    WalletActivityPresentationRules.GetKnownTokenSymbol("mainnet-beta", mintGroup.Key),
                    decimals[0],
                    pre,
                    post,
                    confirmation,
                    observedAtUnixMs,
                    sourceId,
                    venueId);
            }

            return updates.Count == 0
                ?
                [
                    CreateSolanaTransaction(
                        wallet,
                        transaction.Signature,
                        transaction.Slot,
                        false,
                        confirmation,
                        observedAtUnixMs,
                        sourceId,
                        venueId)
                ]
                : updates.ToArray();
        }

        internal static string GetNetworkKey(string chainNamespace, string chainId)
        {
            return $"{chainNamespace.ToLowerInvariant()}|{chainId}";
        }

        internal static string? GetSolanaVenueId(IEnumerable<string> accountAddresses)
        {
            ArgumentNullException.ThrowIfNull(accountAddresses);
            foreach (var address in accountAddresses)
            {
                if (SolanaVenues.TryGetValue(address, out var venueId))
                {
                    return venueId;
                }
            }
            return null;
        }

        internal static string Shorten(string value)
        {
            return value.Length <= 14 ? value : $"{value[..6]}…{value[^4..]}";
        }

        private static string ToAddressTopic(string address)
        {
            return "0x" + new string('0', 24) + address[2..].ToLowerInvariant();
        }

        private static bool TryReadTopicAddress(string value, out string address)
        {
            address = string.Empty;
            return EvmAddress.IsHash(value)
                   && EvmAddress.TryNormalize("0x" + value[^40..], out address);
        }

        private static string? TryReadUnsigned(string value)
        {
            if (!EvmAddress.IsData(value, 32) || value.Length <= 2)
            {
                return null;
            }
            return BigInteger.TryParse(
                "0" + value[2..],
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out var amount)
                ? amount.ToString(CultureInfo.InvariantCulture)
                : null;
        }

        private static WalletActivityUpdate[] CreateEvmValueUpdates(
            string transactionHash,
            string eventSuffix,
            string from,
            string? to,
            BigInteger value,
            string chainId,
            ulong blockNumber,
            long observedAtUnixMs,
            IReadOnlyCollection<SavedTrackedWallet> wallets,
            string sourceId,
            WalletActivityKind kind)
        {
            var updates = new List<WalletActivityUpdate>();
            foreach (var wallet in wallets)
            {
                var isFrom = string.Equals(wallet.Address, from, StringComparison.OrdinalIgnoreCase);
                var isTo = to != null && string.Equals(wallet.Address, to, StringComparison.OrdinalIgnoreCase);
                if (!isFrom && !isTo)
                {
                    continue;
                }
                var direction = isFrom && isTo
                    ? WalletActivityDirection.Self
                    : isFrom ? WalletActivityDirection.Outgoing : WalletActivityDirection.Incoming;
                updates.Add(new WalletActivityUpdate
                {
                    EventId = $"{ChainNamespaces.Eip155}|{chainId}|{transactionHash}|{eventSuffix}|{wallet.Address}",
                    ChainNamespace = ChainNamespaces.Eip155,
                    ChainId = chainId,
                    WalletAddress = wallet.Address,
                    WalletLabel = wallet.Label,
                    TransactionId = transactionHash,
                    ChainPosition = blockNumber,
                    Kind = kind,
                    Direction = direction,
                    Counterparty = direction switch
                    {
                        WalletActivityDirection.Incoming => from,
                        WalletActivityDirection.Outgoing => to,
                        _ => wallet.Address
                    },
                    AmountRaw = value.ToString(CultureInfo.InvariantCulture),
                    Confirmation = "head",
                    ObservedAtUnixMs = observedAtUnixMs,
                    SourceId = sourceId
                });
            }
            return updates.ToArray();
        }

        private static void AddSolanaMovement(
            ICollection<WalletActivityUpdate> updates,
            SolanaWalletTransaction transaction,
            SavedTrackedWallet wallet,
            string? assetAddress,
            string? assetSymbol,
            byte decimals,
            BigInteger preAmount,
            BigInteger postAmount,
            string confirmation,
            long observedAtUnixMs,
            string sourceId,
            string? venueId)
        {
            if (preAmount == postAmount)
            {
                return;
            }
            var incoming = postAmount > preAmount;
            updates.Add(new WalletActivityUpdate
            {
                EventId = $"{ChainNamespaces.Solana}|mainnet-beta|{transaction.Signature}|{wallet.Address}|asset:{assetAddress ?? "native"}",
                ChainNamespace = ChainNamespaces.Solana,
                ChainId = "mainnet-beta",
                WalletAddress = wallet.Address,
                WalletLabel = wallet.Label,
                TransactionId = transaction.Signature,
                ChainPosition = transaction.Slot,
                Kind = assetAddress == null
                    ? WalletActivityKind.NativeTransfer
                    : WalletActivityKind.TokenTransfer,
                Direction = incoming
                    ? WalletActivityDirection.Incoming
                    : WalletActivityDirection.Outgoing,
                VenueId = venueId,
                AssetAddress = assetAddress,
                AssetSymbol = assetSymbol,
                AssetDecimals = decimals,
                AmountRaw = BigInteger.Abs(postAmount - preAmount).ToString(CultureInfo.InvariantCulture),
                Confirmation = confirmation,
                ObservedAtUnixMs = observedAtUnixMs,
                SourceId = sourceId
            });
        }

        private static bool TryReadErc1155Batch(
            string data,
            out string? assetIds,
            out string? amounts)
        {
            assetIds = null;
            amounts = null;
            if (!TryReadWordAsInt(data, 0, out var idsOffset)
                || !TryReadWordAsInt(data, 32, out var valuesOffset)
                || !TryReadWordAsInt(data, idsOffset, out var count)
                || count is < 1 or > 256
                || !TryReadWordAsInt(data, valuesOffset, out var valueCount)
                || valueCount != count)
            {
                return false;
            }
            var ids = new string[count];
            var values = new string[count];
            for (var index = 0; index < count; index++)
            {
                if (!TryReadWord(data, idsOffset + 32 + index * 32, out ids[index])
                    || !TryReadWord(data, valuesOffset + 32 + index * 32, out values[index]))
                {
                    return false;
                }
            }
            assetIds = string.Join(",", ids);
            amounts = string.Join(",", values);
            return true;
        }

        private static bool TryReadWordAsInt(string data, int byteOffset, out int value)
        {
            value = 0;
            return TryReadWord(data, byteOffset, out var parsed)
                   && BigInteger.TryParse(parsed, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                   && number >= BigInteger.Zero
                   && number <= int.MaxValue
                   && (value = (int)number) >= 0;
        }

        private static bool TryReadWord(string data, int byteOffset, out string value)
        {
            value = string.Empty;
            if (!EvmAddress.IsData(data, 2 * 1024 * 1024)
                || byteOffset < 0
                || byteOffset > (data.Length - 2) / 2 - 32)
            {
                return false;
            }
            var word = data.Substring(2 + byteOffset * 2, 64);
            if (!BigInteger.TryParse(
                    "0" + word,
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out var number))
            {
                return false;
            }
            value = number.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static int ConfirmationRank(string confirmation)
        {
            return confirmation.ToLowerInvariant() switch
            {
                "head" or "processed" => 0,
                "confirmed" => 1,
                "safe" => 2,
                "finalized" => 3,
                _ => -1
            };
        }
    }
}

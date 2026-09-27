using TrenchHQ.Core.OnChain.Evm;
using TrenchHQ.Core.OnChain;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace TrenchHQ.Core.Wallets
{
    internal enum WalletActivityDisplayKind
    {
        Buy,
        Sell,
        Swap,
        TransferIn,
        TransferOut,
        SelfTransfer,
        Interaction,
        Failed
    }

    internal sealed record WalletActivityPresentation(
        WalletActivityDisplayKind Kind,
        string Summary,
        string Detail,
        string? MarketAssetAddress);

    internal static class WalletActivityPresentationRules
    {
        internal const string WrappedSolMint = "So11111111111111111111111111111111111111112";
        internal const string SolanaUsdcMint = "EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v";
        internal const string SolanaUsdtMint = "Es9vMFrzaCERmJfrF4H2FYD4KCoNkY11McCe8BenwNYB";

        private static readonly EvmQuoteAsset[] SolanaMainnetQuoteAssets =
        [
            new("SOL", WrappedSolMint),
            new("USDC", SolanaUsdcMint),
            new("USDT", SolanaUsdtMint)
        ];

        internal static WalletActivityPresentation Build(
            IReadOnlyCollection<WalletActivityUpdate> transactionUpdates)
        {
            ArgumentNullException.ThrowIfNull(transactionUpdates);
            if (transactionUpdates.Count == 0)
            {
                throw new ArgumentException("At least one wallet activity is required.", nameof(transactionUpdates));
            }

            var representative = transactionUpdates.First();
            if (transactionUpdates.Any(static update => update.Failed))
            {
                return new(WalletActivityDisplayKind.Failed, "Failed transaction", "No asset change", null);
            }

            var movements = BuildNetMovements(transactionUpdates);
            var incoming = movements.Where(static movement => movement.Incoming).ToArray();
            var outgoing = movements.Where(static movement => !movement.Incoming).ToArray();
            var marketAssets = movements
                .Where(movement => !IsQuoteAsset(representative.ChainId, movement))
                .Select(static movement => movement.AssetAddress)
                .Where(static address => !string.IsNullOrWhiteSpace(address))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var marketAssetAddress = marketAssets.Length == 1 ? marketAssets[0] : null;
            if (incoming.Length > 0 && outgoing.Length > 0)
            {
                var incomingNonQuote = incoming.Where(movement => !IsQuoteAsset(representative.ChainId, movement)).ToArray();
                var outgoingNonQuote = outgoing.Where(movement => !IsQuoteAsset(representative.ChainId, movement)).ToArray();
                if (incomingNonQuote.Length == 1
                    && outgoingNonQuote.Length == 0
                    && outgoing.All(movement => IsQuoteAsset(representative.ChainId, movement)))
                {
                    return new(
                        WalletActivityDisplayKind.Buy,
                        $"Buy {incomingNonQuote[0].Symbol}",
                        BuildFlow(outgoing, incoming),
                        marketAssetAddress);
                }
                if (outgoingNonQuote.Length == 1
                    && incomingNonQuote.Length == 0
                    && incoming.All(movement => IsQuoteAsset(representative.ChainId, movement)))
                {
                    return new(
                        WalletActivityDisplayKind.Sell,
                        $"Sell {outgoingNonQuote[0].Symbol}",
                        BuildFlow(outgoing, incoming),
                        marketAssetAddress);
                }
                return new(
                    WalletActivityDisplayKind.Swap,
                    $"Swap {string.Join(" + ", outgoing.Select(static movement => movement.Symbol))} → {string.Join(" + ", incoming.Select(static movement => movement.Symbol))}",
                    BuildFlow(outgoing, incoming),
                    marketAssetAddress);
            }
            if (incoming.Length > 0)
            {
                return new(
                    WalletActivityDisplayKind.TransferIn,
                    $"Transfer in {string.Join(" + ", incoming.Select(static movement => movement.Symbol))}",
                    string.Join(" + ", incoming.Select(FormatMovement)),
                    marketAssetAddress);
            }
            if (outgoing.Length > 0)
            {
                return new(
                    WalletActivityDisplayKind.TransferOut,
                    $"Transfer out {string.Join(" + ", outgoing.Select(static movement => movement.Symbol))}",
                    string.Join(" + ", outgoing.Select(FormatMovement)),
                    marketAssetAddress);
            }
            if (transactionUpdates.Any(static update => update.Direction == WalletActivityDirection.Self))
            {
                return new(WalletActivityDisplayKind.SelfTransfer, "Self transfer", "Same wallet", null);
            }
            return new(WalletActivityDisplayKind.Interaction, "Contract interaction", "No wallet asset change detected", null);
        }

        internal static string? GetKnownTokenSymbol(string chainId, string? address)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return GetNativeSymbol(chainId);
            }
            return GetQuoteAssets(chainId)
                .FirstOrDefault(asset => string.Equals(asset.Address, address, StringComparison.OrdinalIgnoreCase))
                ?.Symbol;
        }

        internal static string FormatAmount(string amountRaw, byte? decimals)
        {
            if (!BigInteger.TryParse(amountRaw, NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
                || amount < BigInteger.Zero)
            {
                return amountRaw;
            }
            var scale = decimals.GetValueOrDefault();
            if (scale == 0)
            {
                return amount.ToString("N0", CultureInfo.InvariantCulture);
            }
            var digits = amount.ToString(CultureInfo.InvariantCulture).PadLeft(scale + 1, '0');
            var whole = digits[..^scale];
            var fractional = digits[^scale..];
            var shown = fractional[..Math.Min(6, fractional.Length)].TrimEnd('0');
            if (shown.Length == 0
                && amount > BigInteger.Zero
                && whole == "0"
                && fractional.Any(static digit => digit != '0'))
            {
                return "<0.000001";
            }
            return CompactAmount(shown.Length == 0 ? whole : $"{whole}.{shown}");
        }

        private static string CompactAmount(string value)
        {
            if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
            {
                return value;
            }
            return number switch
            {
                >= 1_000_000_000m => $"{number / 1_000_000_000m:0.##}B",
                >= 1_000_000m => $"{number / 1_000_000m:0.##}M",
                >= 1_000m => $"{number / 1_000m:0.##}K",
                >= 1m => number.ToString("0.###", CultureInfo.InvariantCulture),
                >= 0.01m => number.ToString("0.####", CultureInfo.InvariantCulture),
                _ => number.ToString("0.######", CultureInfo.InvariantCulture)
            };
        }

        private static Movement[] BuildNetMovements(IEnumerable<WalletActivityUpdate> updates)
        {
            var movements = new List<Movement>();
            foreach (var group in updates
                         .Where(static update => update.Kind is WalletActivityKind.TokenTransfer
                             or WalletActivityKind.NativeTransfer
                             or WalletActivityKind.InternalTransfer)
                         .Where(static update => update.Direction is WalletActivityDirection.Incoming
                             or WalletActivityDirection.Outgoing)
                         .Where(static update => update.AssetId == null)
                         .GroupBy(GetAssetKey, StringComparer.OrdinalIgnoreCase))
            {
                var sample = group.First();
                var incoming = Sum(group.Where(static update => update.Direction == WalletActivityDirection.Incoming));
                var outgoing = Sum(group.Where(static update => update.Direction == WalletActivityDirection.Outgoing));
                if (incoming == outgoing)
                {
                    continue;
                }
                var isIncoming = incoming > outgoing;
                movements.Add(new Movement(
                    sample.AssetAddress,
                    GetSymbol(sample),
                    sample.AssetAddress == null
                        && sample.ChainNamespace == ChainNamespaces.Eip155
                            ? (byte)18
                            : sample.AssetDecimals,
                    BigInteger.Abs(incoming - outgoing),
                    isIncoming));
            }
            return movements.ToArray();
        }

        private static BigInteger Sum(IEnumerable<WalletActivityUpdate> updates)
        {
            var result = BigInteger.Zero;
            foreach (var update in updates)
            {
                if (BigInteger.TryParse(
                        update.AmountRaw,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var amount)
                    && amount >= BigInteger.Zero)
                {
                    result += amount;
                }
            }
            return result;
        }

        private static string GetAssetKey(WalletActivityUpdate update) =>
            update.AssetAddress?.ToLowerInvariant() ?? "native";

        private static string GetSymbol(WalletActivityUpdate update) =>
            !string.IsNullOrWhiteSpace(update.AssetSymbol)
                ? update.AssetSymbol!
                : GetKnownTokenSymbol(update.ChainId, update.AssetAddress)
                  ?? WalletActivityRules.Shorten(update.AssetAddress ?? "Token");

        private static bool IsQuoteAsset(string chainId, Movement movement) =>
            movement.AssetAddress == null
            || GetQuoteAssets(chainId).Any(asset =>
                string.Equals(asset.Address, movement.AssetAddress, StringComparison.OrdinalIgnoreCase));

        private static EvmQuoteAsset[] GetQuoteAssets(string chainId) => chainId switch
        {
            "mainnet-beta" => SolanaMainnetQuoteAssets,
            EvmChainDefinitions.EthereumMainnetChainId => EthereumDeploymentRegistry.MainnetQuoteAssets,
            EvmChainDefinitions.BaseMainnetChainId => BaseDeploymentRegistry.MainnetQuoteAssets,
            EvmChainDefinitions.BnbMainnetChainId => BnbDeploymentRegistry.MainnetQuoteAssets,
            EvmChainDefinitions.RobinhoodMainnetChainId => RobinhoodDeploymentRegistry.MainnetQuoteAssets,
            _ => []
        };

        private static string GetNativeSymbol(string chainId) =>
            chainId switch
            {
                "mainnet-beta" => "SOL",
                EvmChainDefinitions.BnbMainnetChainId => "BNB",
                _ => "ETH"
            };

        private static string BuildFlow(IEnumerable<Movement> outgoing, IEnumerable<Movement> incoming) =>
            $"{string.Join(" + ", outgoing.Select(FormatMovement))} → {string.Join(" + ", incoming.Select(FormatMovement))}";

        private static string FormatMovement(Movement movement) =>
            $"{FormatAmount(movement.Amount.ToString(CultureInfo.InvariantCulture), movement.Decimals)} {movement.Symbol}";

        private sealed record Movement(
            string? AssetAddress,
            string Symbol,
            byte? Decimals,
            BigInteger Amount,
            bool Incoming);
    }
}

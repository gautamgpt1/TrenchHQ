using TrenchHQ.Core.Markets;
using TrenchHQ.Core.OnChain;
using TrenchHQ.Core.OnChain.Evm;
using TrenchHQ.Core.OnChain.Solana;
using TrenchHQ.Core.Panels;
using TrenchHQ.Core.Providers;
using TrenchHQ.Core.Wallets;
using TrenchHQ.Core.Widgets;
using TrenchHQ.Core.Windows;
using TrenchHQ.Infrastructure.Diagnostics;
using TrenchHQ.Infrastructure.OnChain;
using TrenchHQ.Infrastructure.Panels;
using TrenchHQ.Infrastructure.Widgets;
using TrenchHQ.Infrastructure.Windows;
using System.Text.Json;
using Xunit;

namespace TrenchHQ.LogicTests;

public partial class LogicTests
{
    [Fact(DisplayName = "wallet widgets normalize supported addresses without changing price tickers")]
    public void WalletWidgetsNormalizeSupportedAddressesWithoutChangingPriceTickers()
    {
        const string solanaAddress = "11111111111111111111111111111111";
        var catalog = new SavedWidgetCatalog
        {
            Version = 3,
            Widgets =
            [
                new SavedWidgetDefinition
                {
                    Type = PanelWidgetTypes.PriceTicker,
                    Instruments = SavedWidgetCatalogRules.CreateCentralizedInstruments(["BTC/USDT - Binance"])
                },
                new SavedWidgetDefinition
                {
                    Type = "WALLETACTIVITY",
                    Wallets =
                    [
                        new SavedTrackedWallet
                        {
                            ChainNamespace = ChainNamespaces.Eip155,
                            ChainId = EvmChainDefinitions.EthereumMainnetChainId,
                            Address = "0x95aD61b0a150d79219dCF64E1E6Cc01f0B64C4cE",
                            Label = " Whale "
                        },
                        new SavedTrackedWallet
                        {
                            ChainNamespace = ChainNamespaces.Solana,
                            ChainId = "mainnet-beta",
                            Address = solanaAddress
                        },
                        new SavedTrackedWallet
                        {
                            ChainNamespace = ChainNamespaces.Eip155,
                            ChainId = "42161",
                            Address = "0x1111111111111111111111111111111111111111"
                        }
                    ]
                }
            ]
        };

        SavedWidgetCatalogRules.Normalize(catalog);

        AssertEqual(2, catalog.Widgets.Length);
        AssertEqual("Price Ticker 1", catalog.Widgets[0].Name);
        AssertEqual("Wallet Watcher 1", catalog.Widgets[1].Name);
        AssertEqual(2, catalog.Widgets[1].Wallets.Length);
        AssertEqual("Whale", catalog.Widgets[1].Wallets[0].Label);
        AssertEqual("0x95ad61b0a150d79219dcf64e1e6cc01f0b64c4ce", catalog.Widgets[1].Wallets[0].Address);
        AssertEqual(solanaAddress, catalog.Widgets[1].Wallets[1].Address);
        AssertEqual(0, catalog.Widgets[1].Instruments.Length);
    }

    [Fact(DisplayName = "wallet subscription planner deduplicates addresses across active panel hosts")]
    public void WalletSubscriptionPlannerDeduplicatesAddressesAcrossActivePanelHosts()
    {
        var firstId = Guid.NewGuid().ToString("N");
        var secondId = Guid.NewGuid().ToString("N");
        var address = "0x95ad61b0a150d79219dcf64e1e6cc01f0b64c4ce";
        var planned = WalletWidgetSubscriptionPlanner.Build(
            [new OverlayDefinition { WidgetIds = [firstId] }],
            [new DockedBarDefinition { WidgetIds = [secondId] }],
            [
                new SavedWidgetDefinition
                {
                    Id = firstId,
                    Type = PanelWidgetTypes.WalletActivity,
                    Wallets =
                    [
                        new SavedTrackedWallet
                        {
                            ChainNamespace = ChainNamespaces.Eip155,
                            ChainId = EvmChainDefinitions.EthereumMainnetChainId,
                            Address = address,
                            Label = "First"
                        }
                    ]
                },
                new SavedWidgetDefinition
                {
                    Id = secondId,
                    Type = PanelWidgetTypes.WalletActivity,
                    Wallets =
                    [
                        new SavedTrackedWallet
                        {
                            ChainNamespace = ChainNamespaces.Eip155,
                            ChainId = EvmChainDefinitions.EthereumMainnetChainId,
                            Address = address.ToUpperInvariant().Replace("0X", "0x", StringComparison.Ordinal),
                            Label = "Second"
                        }
                    ]
                }
            ]);

        AssertEqual(1, planned.Length);
        AssertEqual(address, planned[0].Address);
    }

    [Fact(DisplayName = "wallet activity rules cover ERC-20, ERC-721, and ERC-1155 transfer directions")]
    public void WalletActivityRulesCoverErc20Erc721AndErc1155TransferDirections()
    {
        var sender = new SavedTrackedWallet
        {
            ChainNamespace = ChainNamespaces.Eip155,
            ChainId = EvmChainDefinitions.EthereumMainnetChainId,
            Address = "0x1111111111111111111111111111111111111111",
            Label = "Sender"
        };
        var receiver = new SavedTrackedWallet
        {
            ChainNamespace = ChainNamespaces.Eip155,
            ChainId = EvmChainDefinitions.EthereumMainnetChainId,
            Address = "0x2222222222222222222222222222222222222222",
            Label = "Receiver"
        };
        var filters = JsonSerializer.Serialize(WalletActivityRules.CreateEvmLogFilters([sender, receiver]));
        Assert(filters.Contains(WalletActivityRules.TransferTopic, StringComparison.Ordinal));
        Assert(filters.Contains(WalletActivityRules.TransferSingleTopic, StringComparison.Ordinal));
        Assert(filters.Contains(WalletActivityRules.TransferBatchTopic, StringComparison.Ordinal));
        Assert(filters.Contains("0x0000000000000000000000001111111111111111111111111111111111111111", StringComparison.Ordinal));

        var updates = WalletActivityRules.ParseEvmTransfer(
            new EvmLogUpdate
            {
                ChainId = EvmChainDefinitions.EthereumMainnetChainId,
                Address = "0x3333333333333333333333333333333333333333",
                Topics =
                [
                    WalletActivityRules.TransferTopic,
                    "0x0000000000000000000000001111111111111111111111111111111111111111",
                    "0x0000000000000000000000002222222222222222222222222222222222222222"
                ],
                Data = "0x" + new string('0', 63) + "a",
                BlockNumber = 123,
                TransactionHash = "0x" + new string('4', 64),
                LogIndex = 7,
                ObservedAtUnixMs = 1000
            },
            [sender, receiver],
            "fixture");

        AssertEqual(2, updates.Length);
        AssertEqual(WalletActivityDirection.Outgoing, updates.Single(update => update.WalletLabel == "Sender").Direction);
        AssertEqual(WalletActivityDirection.Incoming, updates.Single(update => update.WalletLabel == "Receiver").Direction);
        AssertEqual("10", updates[0].AmountRaw);

        var maximum = WalletActivityRules.ParseEvmTransfer(
            new EvmLogUpdate
            {
                ChainId = EvmChainDefinitions.EthereumMainnetChainId,
                Address = "0x3333333333333333333333333333333333333333",
                Topics =
                [
                    WalletActivityRules.TransferTopic,
                    "0x0000000000000000000000001111111111111111111111111111111111111111",
                    "0x0000000000000000000000002222222222222222222222222222222222222222"
                ],
                Data = "0x" + new string('f', 64),
                BlockNumber = 124,
                TransactionHash = "0x" + new string('5', 64),
                LogIndex = 8,
                ObservedAtUnixMs = 1001
            },
            [sender],
            "fixture");
        AssertEqual("115792089237316195423570985008687907853269984665640564039457584007913129639935",
            maximum[0].AmountRaw);

        var erc1155 = WalletActivityRules.ParseEvmTransfer(
            new EvmLogUpdate
            {
                ChainId = EvmChainDefinitions.EthereumMainnetChainId,
                Address = "0x3333333333333333333333333333333333333333",
                Topics =
                [
                    WalletActivityRules.TransferSingleTopic,
                    "0x000000000000000000000000aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    "0x0000000000000000000000001111111111111111111111111111111111111111",
                    "0x0000000000000000000000002222222222222222222222222222222222222222"
                ],
                Data = "0x" + new string('0', 63) + "2" + new string('0', 63) + "a",
                BlockNumber = 125,
                TransactionHash = "0x" + new string('6', 64),
                LogIndex = 9,
                ObservedAtUnixMs = 1002
            },
            [sender, receiver],
            "fixture");
        AssertEqual(2, erc1155.Length);
        AssertEqual("2", erc1155[0].AssetId);
        AssertEqual("10", erc1155[0].AmountRaw);

        var erc1155Batch = WalletActivityRules.ParseEvmTransfer(
            new EvmLogUpdate
            {
                ChainId = EvmChainDefinitions.EthereumMainnetChainId,
                Address = "0x3333333333333333333333333333333333333333",
                Topics =
                [
                    WalletActivityRules.TransferBatchTopic,
                    "0x000000000000000000000000aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    "0x0000000000000000000000001111111111111111111111111111111111111111",
                    "0x0000000000000000000000002222222222222222222222222222222222222222"
                ],
                Data = "0x" + HexWord(64) + HexWord(160)
                             + HexWord(2) + HexWord(2) + HexWord(3)
                             + HexWord(2) + HexWord(10) + HexWord(20),
                BlockNumber = 126,
                TransactionHash = "0x" + new string('9', 64),
                LogIndex = 10,
                ObservedAtUnixMs = 1003
            },
            [sender, receiver],
            "fixture");
        AssertEqual("2,3", erc1155Batch[0].AssetId);
        AssertEqual("10,20", erc1155Batch[0].AmountRaw);
    }

    [Fact(DisplayName = "wallet activity rules normalize EVM native and internal value movement")]
    public void WalletActivityRulesNormalizeEvmNativeAndInternalValueMovement()
    {
        var wallet = new SavedTrackedWallet
        {
            ChainNamespace = ChainNamespaces.Eip155,
            ChainId = EvmChainDefinitions.BaseMainnetChainId,
            Address = "0x1111111111111111111111111111111111111111",
            Label = "Treasury"
        };
        var transaction = WalletActivityRules.ParseEvmTransaction(
            new EvmWalletTransaction
            {
                Hash = "0x" + new string('7', 64),
                From = wallet.Address,
                To = "0x2222222222222222222222222222222222222222",
                ValueRaw = "100"
            },
            EvmChainDefinitions.BaseMainnetChainId,
            200,
            1700000000000,
            [wallet],
            "fixture");
        AssertEqual(1, transaction.Length);
        AssertEqual(WalletActivityKind.NativeTransfer, transaction[0].Kind);
        AssertEqual(WalletActivityDirection.Outgoing, transaction[0].Direction);
        AssertEqual("100", transaction[0].AmountRaw);

        var trace = WalletActivityRules.ParseEvmTrace(
            new EvmWalletTrace
            {
                TransactionHash = "0x" + new string('8', 64),
                TracePath = "0.1",
                From = "0x2222222222222222222222222222222222222222",
                To = wallet.Address,
                ValueRaw = "55",
                BlockNumber = 201
            },
            EvmChainDefinitions.BaseMainnetChainId,
            1700000001000,
            [wallet],
            "fixture");
        AssertEqual(1, trace.Length);
        AssertEqual(WalletActivityKind.InternalTransfer, trace[0].Kind);
        AssertEqual(WalletActivityDirection.Incoming, trace[0].Direction);
        AssertEqual("55", trace[0].AmountRaw);
        Assert(WalletActivityRules.IsConfirmationUpgrade("head", "confirmed"));
        Assert(WalletActivityRules.IsConfirmationUpgrade("confirmed", "finalized"));
        Assert(!WalletActivityRules.IsConfirmationUpgrade("finalized", "confirmed"));
    }

    [Fact(DisplayName = "wallet transaction flows classify buy sell swap and transfers")]
    public void WalletTransactionFlowsClassifyBuySellSwapAndTransfers()
    {
        const string wallet = "0x1111111111111111111111111111111111111111";
        const string token = "0x0b57b7333c43fe900d17a81bddc67c88c825afd6";
        const string otherToken = "0x2222222222222222222222222222222222222222";

        WalletActivityUpdate Movement(
            string transaction,
            string address,
            string symbol,
            byte decimals,
            string amount,
            WalletActivityDirection direction) => new()
        {
            EventId = transaction + address + direction,
            ChainNamespace = ChainNamespaces.Eip155,
            ChainId = EvmChainDefinitions.EthereumMainnetChainId,
            WalletAddress = wallet,
            WalletLabel = "Trader",
            TransactionId = transaction,
            Kind = WalletActivityKind.TokenTransfer,
            Direction = direction,
            AssetAddress = address,
            AssetSymbol = symbol,
            AssetDecimals = decimals,
            AmountRaw = amount
        };

        var buy = WalletActivityPresentationRules.Build([
            Movement("buy", EthereumDeploymentRegistry.Usdc, "USDC", 6, "12500000", WalletActivityDirection.Outgoing),
            Movement("buy", token, "SDLR", 18, "2500000000000000000", WalletActivityDirection.Incoming)
        ]);
        AssertEqual(WalletActivityDisplayKind.Buy, buy.Kind);
        AssertEqual("Buy SDLR", buy.Summary);
        AssertEqual("12.5 USDC → 2.5 SDLR", buy.Detail);
        AssertEqual(token, buy.MarketAssetAddress);

        var sell = WalletActivityPresentationRules.Build([
            Movement("sell", token, "SDLR", 18, "3000000000000000000", WalletActivityDirection.Outgoing),
            Movement("sell", EthereumDeploymentRegistry.Usdc, "USDC", 6, "17000000", WalletActivityDirection.Incoming)
        ]);
        AssertEqual(WalletActivityDisplayKind.Sell, sell.Kind);
        AssertEqual("Sell SDLR", sell.Summary);
        AssertEqual(token, sell.MarketAssetAddress);

        var swap = WalletActivityPresentationRules.Build([
            Movement("swap", token, "SDLR", 18, "1000000000000000000", WalletActivityDirection.Outgoing),
            Movement("swap", otherToken, "OTHER", 9, "2000000000", WalletActivityDirection.Incoming)
        ]);
        AssertEqual(WalletActivityDisplayKind.Swap, swap.Kind);
        AssertEqual("Swap SDLR → OTHER", swap.Summary);
        AssertEqual<string?>(null, swap.MarketAssetAddress);

        var transferIn = WalletActivityPresentationRules.Build([
            Movement("in", token, "SDLR", 18, "1", WalletActivityDirection.Incoming)
        ]);
        var transferOut = WalletActivityPresentationRules.Build([
            Movement("out", token, "SDLR", 18, "1", WalletActivityDirection.Outgoing)
        ]);
        AssertEqual(WalletActivityDisplayKind.TransferIn, transferIn.Kind);
        AssertEqual("Transfer in SDLR", transferIn.Summary);
        AssertEqual(WalletActivityDisplayKind.TransferOut, transferOut.Kind);
        AssertEqual("Transfer out SDLR", transferOut.Summary);
        AssertEqual("<0.000001 SDLR", transferIn.Detail);

        var nativeTransfer = WalletActivityPresentationRules.Build([new WalletActivityUpdate
        {
            EventId = "native-in",
            ChainNamespace = ChainNamespaces.Eip155,
            ChainId = EvmChainDefinitions.EthereumMainnetChainId,
            WalletAddress = wallet,
            WalletLabel = "Trader",
            TransactionId = "native-in",
            Kind = WalletActivityKind.NativeTransfer,
            Direction = WalletActivityDirection.Incoming,
            AmountRaw = "10000000000000000"
        }]);
        AssertEqual("Transfer in ETH", nativeTransfer.Summary);
        AssertEqual("0.01 ETH", nativeTransfer.Detail);
        AssertEqual("46.857", WalletActivityPresentationRules.FormatAmount("46856961", 6));
        AssertEqual("5M", WalletActivityPresentationRules.FormatAmount("5000000000000000", 9));
    }

    [Fact(DisplayName = "wallet metadata uses exact venue evidence and factual market cap")]
    public void WalletMetadataUsesExactVenueEvidenceAndFactualMarketCap()
    {
        const string token = "0x0b57b7333c43fe900d17a81bddc67c88c825afd6";
        var updates = new[]
        {
            new WalletActivityUpdate
            {
                Counterparty = "0x1111111111111111111111111111111111111111",
                AssetAddress = token
            }
        };
        var catalog = new PoolCatalogSearchResult
        {
            AssetAddress = token,
            AssetSymbol = "SDLR",
            Pools =
            [
                new PoolCatalogEntry
                {
                    ProtocolId = "uniswap",
                    PoolAddress = "0x1111111111111111111111111111111111111111",
                    BaseAsset = new PoolCatalogAsset { Address = token },
                    QuoteAsset = new PoolCatalogAsset { Address = EthereumDeploymentRegistry.Usdc },
                    LiquidityUsd = 100_000,
                    MarketCapUsd = 149_700,
                    IconUri = "https://example.com/token.png"
                }
            ]
        };
        var metadata = WalletActivityMetadataRules.Resolve(updates, token, catalog);
        AssertEqual("uniswap", metadata.VenueId);
        AssertEqual("https://example.com/token.png", metadata.TokenIconUri);
        AssertEqual(149_700d, metadata.MarketCapUsd);
        AssertEqual("$149.7K", WalletActivityMetadataRules.FormatUsd(metadata.MarketCapUsd!.Value));

        updates[0].Counterparty = "0x2222222222222222222222222222222222222222";
        metadata = WalletActivityMetadataRules.Resolve(updates, token, catalog);
        AssertEqual<string?>(null, metadata.VenueId);
    }

    [Fact(DisplayName = "wallet metadata recognizes every implemented Solana venue")]
    public void WalletMetadataRecognizesEveryImplementedSolanaVenue()
    {
        var venues = new Dictionary<string, string>
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
        foreach (var venue in venues)
        {
            AssertEqual(venue.Value, WalletActivityRules.GetSolanaVenueId([venue.Key]));
        }
        AssertEqual<string?>(null, WalletActivityRules.GetSolanaVenueId(["unknown"]));
    }

    [Fact(DisplayName = "wallet activity ages advance from seconds through days")]
    public void WalletActivityAgesAdvanceFromSecondsThroughDays()
    {
        const long now = 2_000_000_000_000;
        AssertEqual("now", WalletActivityAgeRules.Format(now, now));
        AssertEqual("now", WalletActivityAgeRules.Format(now, now + 999));
        AssertEqual("1s", WalletActivityAgeRules.Format(now, now + 1_000));
        AssertEqual("59s", WalletActivityAgeRules.Format(now, now + 59_999));
        AssertEqual("1m", WalletActivityAgeRules.Format(now, now + 60_000));
        AssertEqual("1h", WalletActivityAgeRules.Format(now, now + 3_600_000));
        AssertEqual("1d", WalletActivityAgeRules.Format(now, now + 86_400_000));
        AssertEqual("now", WalletActivityAgeRules.Format(now + 1_000, now));
    }

    [Fact(DisplayName = "wallet transaction flow classification covers every EVM chain")]
    public void WalletTransactionFlowClassificationCoversEveryEvmChain()
    {
        const string wallet = "0x1111111111111111111111111111111111111111";
        const string token = "0x2222222222222222222222222222222222222222";
        var chains = new[]
        {
            (EvmChainDefinitions.EthereumMainnetChainId, EthereumDeploymentRegistry.MainnetQuoteAssets[0]),
            (EvmChainDefinitions.BaseMainnetChainId, BaseDeploymentRegistry.MainnetQuoteAssets[0]),
            (EvmChainDefinitions.BnbMainnetChainId, BnbDeploymentRegistry.MainnetQuoteAssets[0]),
            (EvmChainDefinitions.RobinhoodMainnetChainId, RobinhoodDeploymentRegistry.MainnetQuoteAssets[0])
        };
        foreach (var (chainId, quote) in chains)
        {
            var presentation = WalletActivityPresentationRules.Build([
                new WalletActivityUpdate
                {
                    ChainNamespace = ChainNamespaces.Eip155,
                    ChainId = chainId,
                    WalletAddress = wallet,
                    TransactionId = chainId,
                    Kind = WalletActivityKind.TokenTransfer,
                    Direction = WalletActivityDirection.Outgoing,
                    AssetAddress = quote.Address,
                    AssetSymbol = quote.Symbol,
                    AssetDecimals = 18,
                    AmountRaw = "1000000000000000000"
                },
                new WalletActivityUpdate
                {
                    ChainNamespace = ChainNamespaces.Eip155,
                    ChainId = chainId,
                    WalletAddress = wallet,
                    TransactionId = chainId,
                    Kind = WalletActivityKind.TokenTransfer,
                    Direction = WalletActivityDirection.Incoming,
                    AssetAddress = token,
                    AssetSymbol = "TOKEN",
                    AssetDecimals = 18,
                    AmountRaw = "2000000000000000000"
                }
            ]);
            AssertEqual(WalletActivityDisplayKind.Buy, presentation.Kind);
            AssertEqual("Buy TOKEN", presentation.Summary);
        }
    }

    [Fact(DisplayName = "Solana wallet balance deltas classify protocol-independent actions")]
    public void SolanaWalletBalanceDeltasClassifyProtocolIndependentActions()
    {
        const string walletAddress = "11111111111111111111111111111111";
        const string tokenMint = "TokenMint111111111111111111111111111111111";
        var wallet = new SavedTrackedWallet
        {
            ChainNamespace = ChainNamespaces.Solana,
            ChainId = "mainnet-beta",
            Address = walletAddress,
            Label = "Trader"
        };
        var transaction = new SolanaWalletTransaction
        {
            Signature = "swap-signature",
            Slot = 99,
            FeeRaw = 5000,
            AccountAddresses = [walletAddress, "usdc-account", "token-account"],
            PreBalancesRaw = [10_000_000_000, 2_039_280, 2_039_280],
            PostBalancesRaw = [9_999_995_000, 2_039_280, 2_039_280],
            TokenBalances =
            [
                new SolanaWalletTokenBalance
                {
                    AccountIndex = 1,
                    AccountAddress = "usdc-account",
                    Mint = WalletActivityPresentationRules.SolanaUsdcMint,
                    OwnerAddress = walletAddress,
                    Decimals = 6,
                    PreAmountRaw = 12_500_000,
                    PostAmountRaw = 0
                },
                new SolanaWalletTokenBalance
                {
                    AccountIndex = 2,
                    AccountAddress = "token-account",
                    Mint = tokenMint,
                    OwnerAddress = walletAddress,
                    Decimals = 9,
                    PreAmountRaw = 0,
                    PostAmountRaw = 2_000_000_000
                }
            ]
        };
        var protocols = new[]
        {
            OnChainProtocolIds.PumpBondingCurve,
            OnChainProtocolIds.PumpSwap,
            OnChainProtocolIds.RaydiumAmmV4,
            OnChainProtocolIds.RaydiumCpmm,
            OnChainProtocolIds.RaydiumClmm,
            OnChainProtocolIds.MeteoraDammV1,
            OnChainProtocolIds.MeteoraDammV2,
            OnChainProtocolIds.MeteoraDlmm,
            OnChainProtocolIds.OrcaWhirlpool,
            OnChainProtocolIds.ManifestOrderbook
        };
        foreach (var protocol in protocols)
        {
            var updates = WalletActivityRules.ParseSolanaTransaction(
                transaction,
                wallet,
                "confirmed",
                1000,
                "fixture:" + protocol);
            var presentation = WalletActivityPresentationRules.Build(updates);
            AssertEqual(WalletActivityDisplayKind.Buy, presentation.Kind);
            AssertEqual("Buy TokenM…1111", presentation.Summary);
            AssertEqual("12.5 USDC → 2 TokenM…1111", presentation.Detail);
            Assert(!updates.Any(static update => update.AssetAddress == null));
        }

        var createdAccount = new SolanaWalletTransaction
        {
            Signature = "incoming-signature",
            Slot = 100,
            FeeRaw = 5000,
            AccountAddresses = [walletAddress, "new-token-account"],
            PreBalancesRaw = [5_000_000_000, 0],
            PostBalancesRaw = [4_997_955_720, 2_039_280],
            TokenBalances =
            [
                new SolanaWalletTokenBalance
                {
                    AccountIndex = 1,
                    AccountAddress = "new-token-account",
                    Mint = tokenMint,
                    OwnerAddress = walletAddress,
                    Decimals = 9,
                    PostAmountRaw = 1_000_000_000
                }
            ]
        };
        var incoming = WalletActivityPresentationRules.Build(WalletActivityRules.ParseSolanaTransaction(
            createdAccount,
            wallet,
            "confirmed",
            1001,
            "fixture:transfer"));
        AssertEqual(WalletActivityDisplayKind.TransferIn, incoming.Kind);
        AssertEqual("Transfer in TokenM…1111", incoming.Summary);

        var solBuy = new SolanaWalletTransaction
        {
            Signature = "sol-buy-signature",
            Slot = 101,
            FeeRaw = 5000,
            AccountAddresses = [walletAddress, "token-account"],
            PreBalancesRaw = [5_000_000_000, 2_039_280],
            PostBalancesRaw = [2_999_995_000, 2_039_280],
            TokenBalances =
            [
                new SolanaWalletTokenBalance
                {
                    AccountIndex = 1,
                    AccountAddress = "token-account",
                    Mint = tokenMint,
                    OwnerAddress = walletAddress,
                    Decimals = 9,
                    PostAmountRaw = 1_000_000_000
                }
            ]
        };
        var buy = WalletActivityPresentationRules.Build(WalletActivityRules.ParseSolanaTransaction(
            solBuy,
            wallet,
            "confirmed",
            1002,
            "fixture:native-buy"));
        AssertEqual(WalletActivityDisplayKind.Buy, buy.Kind);
        AssertEqual("2 SOL → 1 TokenM…1111", buy.Detail);
    }

    [Fact(DisplayName = "Solana wallet notifications become stable wallet-qualified activities")]
    public void SolanaWalletNotificationsBecomeStableWalletQualifiedActivities()
    {
        var wallet = new SavedTrackedWallet
        {
            ChainNamespace = ChainNamespaces.Solana,
            ChainId = "mainnet-beta",
            Address = "11111111111111111111111111111111",
            Label = "Treasury"
        };
        var update = WalletActivityRules.CreateSolanaTransaction(
            wallet,
            "signature",
            42,
            false,
            "processed",
            1000,
            "fixture");
        AssertEqual("solana|mainnet-beta|signature|11111111111111111111111111111111", update.EventId);
        AssertEqual(WalletActivityKind.Transaction, update.Kind);
        AssertEqual(42UL, update.ChainPosition);
        AssertEqual("processed", update.Confirmation);
    }
}

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
    static async Task VerifyLivePublicSolanaWalletTransactionAsync()
    {
        const string signature = "43ufneL1Wmx8Nm89rzRTcCQrQSJyXJT3Cxhg2C7DLAdgUUg6q6gJVMKDtDogb4VfFxG4PfqTQBJj4XMZA2Cdzrs2";
        const string walletAddress = "zAVEtutVm6hPGGNNTSPxXvPoyKAW7k3mrDWExVUAeyv";
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var rpc = new SolanaRpcClient(httpClient, OnChainPoolDiscoveryService.PublicMainnetRpcEndpoint);
        var transaction = await rpc.GetWalletTransactionAsync(signature, CancellationToken.None);
        Assert(transaction != null, "The public Solana RPC did not return the recorded PumpSwap transaction.");
        var updates = WalletActivityRules.ParseSolanaTransaction(
            transaction!,
            new SavedTrackedWallet
            {
                ChainNamespace = ChainNamespaces.Solana,
                ChainId = "mainnet-beta",
                Address = walletAddress,
                Label = "Recorded PumpSwap trader"
            },
            "finalized",
            1700000000000,
            "solana-public-live");
        var presentation = WalletActivityPresentationRules.Build(updates);
        AssertEqual(WalletActivityDisplayKind.Buy, presentation.Kind,
            "The recorded PumpSwap wallet deltas did not classify as a buy.");
        Assert(updates.Any(update => update.AssetAddress == WalletActivityPresentationRules.SolanaUsdcMint
                                     && update.Direction == WalletActivityDirection.Outgoing),
            "The recorded PumpSwap wallet deltas did not include outgoing USDC.");
        Assert(updates.Any(update => update.AssetAddress == "6NwarBvDkXhByqVp2Qkq5i9XbtA2B3Bwe8SWGu9vpump"
                                     && update.Direction == WalletActivityDirection.Incoming),
            "The recorded PumpSwap wallet deltas did not include the purchased token.");
        Console.WriteLine($"LIVE SOLANA WALLET | slot={transaction!.Slot} | action={presentation.Summary} | flow={presentation.Detail}");
    }
}

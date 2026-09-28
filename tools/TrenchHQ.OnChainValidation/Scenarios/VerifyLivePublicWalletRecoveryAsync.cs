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
    static async Task VerifyLivePublicWalletRecoveryAsync()
    {
        var configuration = CreateProviderConfiguration(
            OnChainProviderTypes.PublicNodeEthereum,
            "wss://ethereum-rpc.publicnode.com",
            "https://ethereum-rpc.publicnode.com");
        var wallets = new[]
        {
            new SavedTrackedWallet
            {
                ChainNamespace = ChainNamespaces.Eip155,
                ChainId = EvmChainDefinitions.EthereumMainnetChainId,
                Address = "0x28c6c06298d514db089934071355e5743bf21d60",
                Label = "Binance 14"
            },
            new SavedTrackedWallet
            {
                ChainNamespace = ChainNamespaces.Eip155,
                ChainId = EvmChainDefinitions.EthereumMainnetChainId,
                Address = "0x1d1d6117d6699b502c087be2e7a8b39a8de12c8b",
                Label = "SDLR trader"
            }
        };
        var updates = new ConcurrentQueue<WalletActivityUpdate>();
        var reachedLive = false;
        for (var attempt = 1; attempt <= 3 && !reachedLive; attempt++)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var source = new EvmWalletActivityStreamSource(configuration, null);
            try
            {
                await source.RunAsync(
                    wallets,
                    updates.Enqueue,
                    _ =>
                    {
                        reachedLive = true;
                        cancellation.CancelAfter(TimeSpan.FromSeconds(30));
                    },
                    cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested && reachedLive)
            {
            }
            catch (Exception exception) when (
                reachedLive && exception is IOException or WebSocketException)
            {
            }
            catch (Exception exception) when (
                attempt < 3 && exception is IOException or WebSocketException)
            {
                Console.WriteLine(
                    $"LIVE WALLET RETRY | public endpoint transport closed | attempt={attempt}");
                await Task.Delay(TimeSpan.FromSeconds(attempt));
            }
        }
        Assert(reachedLive, "The PublicNode wallet connection did not reach Live.");
        var presentations = updates
            .GroupBy(static update => new
            {
                update.ChainId,
                update.WalletAddress,
                update.TransactionId
            })
            .Select(static group => WalletActivityPresentationRules.Build(group.ToArray()))
            .ToArray();
        Console.WriteLine(
            $"LIVE WALLET | events={updates.Count} | rows={presentations.Length} | actions="
            + (presentations.Length == 0
                ? "none in the live observation window"
                : string.Join(", ", presentations.Select(static item => item.Summary).Distinct())));
    }
}

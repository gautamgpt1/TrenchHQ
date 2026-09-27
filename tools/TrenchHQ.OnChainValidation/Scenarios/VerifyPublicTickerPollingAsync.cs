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
    static async Task VerifyPublicTickerPollingAsync(string enginePath)
    {
        foreach (var chain in EvmChainDefinitions.Supported)
        {
            var preset = OnChainProviderCatalog.Get(chain.ChainId switch
            {
                EvmChainDefinitions.EthereumMainnetChainId => OnChainProviderTypes.PublicNodeEthereum,
                EvmChainDefinitions.BaseMainnetChainId => OnChainProviderTypes.BasePublic,
                EvmChainDefinitions.BnbMainnetChainId => OnChainProviderTypes.PublicNodeBnb,
                EvmChainDefinitions.RobinhoodMainnetChainId => OnChainProviderTypes.PublicNodeRobinhood,
                _ => throw new InvalidOperationException("No public ticker test route.")
            });
            var configuration = OnChainProviderConfigurationStore.CreateConfiguration(preset);
            var engine = new OnChainEngineClient(enginePath);
            var prices = new ConcurrentQueue<OnChainPriceUpdate>();
            engine.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
            using var handler = new TickerRequestCountingHandler();
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var coordinator = new EvmStreamCoordinator(engine,
                Path.Combine(Path.GetTempPath(), "TrenchHQTickerPublic", Guid.NewGuid().ToString("N")),
                chain, http, (_, _) => throw new InvalidOperationException("Polling opened a socket."));
            try
            {
                await coordinator.StartAsync(configuration, null, [chain.CreateNativeUsdReferenceSelection!()],
                    webSocketPoolIds: new HashSet<string>());
                await WaitUntilAsync(() => prices.Any(price => price.SpotPriceQuote != null),
                    TimeSpan.FromSeconds(20), $"{chain.CatalogChainId} public polling price");
                await Task.Delay(TimeSpan.FromSeconds(4));
                AssertEqual(0, handler.Counts.GetValueOrDefault("eth_getLogs"), "Public polling replayed logs.");
                Console.WriteLine($"PUBLIC POLL | {chain.CatalogChainId} | prices={prices.Count} | block={prices.Last().ChainPosition?.BlockNumber} | methods={JsonSerializer.Serialize(handler.Counts)}");
            }
            finally
            {
                await coordinator.StopAsync();
                await engine.StopAsync();
            }
        }
    }
}

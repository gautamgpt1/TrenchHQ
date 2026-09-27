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
    static async Task VerifyDedicatedAlchemySolanaSnapshotAsync(
        string enginePath, string credential, int sampleSeconds)
    {
        var sampleIntervalSeconds = (int)SolanaRpcSampleStreamSource.SampleInterval.TotalSeconds;
        var baselineProcessIds = GetEngineProcessIds();
        var client = new OnChainEngineClient(enginePath);
        using var rpcHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var configuration = OnChainProviderConfigurationStore.CreateConfiguration(
            OnChainProviderCatalog.Get(OnChainProviderTypes.AlchemyWebSocket));
        var prices = new ConcurrentQueue<OnChainPriceUpdate>();
        client.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
        try
        {
            var selected = await DiscoverDedicatedAlchemySolanaPoolAsync(
                rpcHttp, configuration, credential, client);
            Console.WriteLine($"ALCHEMY SOLANA POOL | quote={selected.Descriptor.QuoteMint} | orientation={selected.Descriptor.PairOrientation} | accounts={selected.Descriptor.EnumerateSolanaAccountAddresses().Count()}");
            await client.ReplaceWatchedPoolsAsync("solana:mainnet-beta", [selected]);
            await new OnChainSnapshotReconciler(client).ReconcileAsync(
                configuration, credential, [selected], CancellationToken.None);
            await WaitUntilAsync(() => prices.Count > 0, TimeSpan.FromSeconds(10),
                "Alchemy Solana snapshot price");
            var price = prices.Last();
            Console.WriteLine($"ALCHEMY SOLANA SNAPSHOT | spot={price.SpotPriceQuote != null} | usd={price.PriceUsd != null} | source={price.SourceId} | updates={prices.Count}");
            Assert(price.PriceUsd != null,
                "The live Alchemy Solana SOL/USDC snapshot did not produce a USD price.");
            if (sampleSeconds > 0)
            {
                var addresses = selected.Descriptor.EnumerateSolanaAccountAddresses()
                    .Where(static address => !string.IsNullOrWhiteSpace(address))
                    .Select(static address => address!)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var rpc = new SolanaRpcClient(rpcHttp, configuration, credential);
                var started = DateTimeOffset.UtcNow;
                for (var sample = 0; sample < sampleSeconds / sampleIntervalSeconds; sample++)
                {
                    var accounts = await rpc.GetMultipleAccountsAsync(
                        addresses, configuration.Commitment, CancellationToken.None);
                    var observedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    foreach (var account in accounts.Where(static account => account != null))
                    {
                        await client.PublishAccountUpdateAsync(new OnChainRawAccountUpdate
                        {
                            Pubkey = account!.Address,
                            OwnerProgram = account.OwnerProgram,
                            DataBase64 = account.DataBase64,
                            Slot = account.Slot,
                            WriteVersion = 0,
                            Commitment = configuration.Commitment,
                            SourceId = $"rpcSample:{configuration.Id}",
                            ObservedAtUnixMs = observedAt
                        });
                    }
                    var nextSampleAt = started + SolanaRpcSampleStreamSource.SampleInterval * (sample + 1);
                    await Task.Delay(nextSampleAt - DateTimeOffset.UtcNow > TimeSpan.Zero
                        ? nextSampleAt - DateTimeOffset.UtcNow
                        : TimeSpan.Zero);
                }
                var sampledPrices = prices.Count(update => update.SourceId.StartsWith(
                    "rpcSample:", StringComparison.Ordinal) && update.PriceUsd != null);
                Console.WriteLine($"ALCHEMY SOLANA SAMPLE | utcStart={started:O} | utcEnd={DateTimeOffset.UtcNow:O} | intervalSeconds={SolanaRpcSampleStreamSource.SampleInterval.TotalSeconds} | samples={sampleSeconds / sampleIntervalSeconds} | usdPrices={sampledPrices} | engine={GetSingleNewEngineProcessId(baselineProcessIds)}");
                Assert(sampledPrices >= sampleSeconds / sampleIntervalSeconds,
                    "A Solana pool-state sample failed to produce a USD price update.");
            }
        }
        finally
        {
            await client.StopAsync();
        }
        await WaitUntilAsync(() => !GetEngineProcessIds().Except(baselineProcessIds).Any(),
            TimeSpan.FromSeconds(10), "Alchemy Solana snapshot engine stop");
    }
}

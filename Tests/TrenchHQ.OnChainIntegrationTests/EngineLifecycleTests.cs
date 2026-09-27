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
using Xunit;
using TrenchHQ.TestSupport;
using static TrenchHQ.TestSupport.OnChainFixtures;

namespace TrenchHQ.OnChainIntegrationTests;

public partial class OnChainTests
{
    [Fact]
    public async Task EngineCrashRestoresExactPricesAndStopsWhenUnused()
    {
        var client = new OnChainEngineClient(EnginePath);
        var states = new ConcurrentQueue<OnChainEngineState>();
        var prices = new ConcurrentQueue<OnChainPriceUpdate>();

        client.StateChanged += (_, args) => states.Enqueue(args.State);
        client.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
        var existingProcessIds = GetEngineProcessIds();
        try
        {
            var mintBytes = Enumerable.Repeat((byte)3, 32).ToArray();
            var mint = EncodeBase58(mintBytes);
            var descriptor = new OnChainPoolDescriptor
            {
                PoolKey = new OnChainPoolKey
                {
                    ProtocolId = "pumpBondingCurve",
                    PoolAddress = "curve"
                },
                PoolType = "bondingCurve",
                ProgramId = "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P",
                BaseMint = mint,
                QuoteMint = "So11111111111111111111111111111111111111112",
                BaseDecimals = 6,
                QuoteDecimals = 9,
                SupportStatus = OnChainSupportStatus.Supported
            };
            await client.ReplaceWatchedPoolsAsync(
            [
                new OnChainWatchedPoolSelection
                {
                    Descriptor = descriptor,
                    SelectedMint = mint
                }
            ]);
            AssertEqual(OnChainEngineState.Ready, client.State, "Engine did not become ready.");
            var firstProcessId = GetSingleNewEngineProcessId(existingProcessIds);

            await client.PublishTransactionUpdateAsync(CreatePumpTrade("signature-1", mintBytes));
            await WaitUntilAsync(() => prices.Count == 1, TimeSpan.FromSeconds(5), "first price");
            AssertEqual("2000000000000000000", prices.Last().LastTradePriceQuote?.Coefficient,
                "Engine returned the wrong exact price.");

            var referenceObservedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await client.PublishReferencePriceAsync(
                "solUsd",
                new OnChainDecimalValue { Coefficient = "15025", Scale = 2 },
                "ccxt:binance:SOL/USDT",
                referenceObservedAt);
            await WaitUntilAsync(() => prices.Count == 2, TimeSpan.FromSeconds(5), "USD reference price");
            var referencedPrice = prices.Last();
            AssertEqual("2000000000000000000", referencedPrice.PriceSol?.Coefficient,
                "Wrapped-SOL quote price was not exposed as SOL.");
            AssertEqual("30050000000000000000000", referencedPrice.PriceUsd?.Coefficient,
                "SOL/USD reference conversion was not exact.");
            AssertEqual("ccxt:binance:SOL/USDT", referencedPrice.ReferenceSourceId,
                "Reference-price provenance was lost.");
            AssertEqual<long?>(referenceObservedAt, referencedPrice.ReferenceObservedAtUnixMs,
                "Reference-price timestamp was lost.");

            try
            {
                await client.PublishReferencePriceAsync(
                    "solUsd",
                    new OnChainDecimalValue { Coefficient = "0", Scale = 0 },
                    "invalid",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                throw new InvalidOperationException("A zero reference price was accepted.");
            }
            catch (OnChainEngineRequestException exception)
            {
                AssertEqual("invalidRequest", exception.Code, "Engine request error code changed.");
                Assert(!exception.Retryable, "Invalid reference input was marked retryable.");
            }

            await client.PublishTransactionUpdateAsync(CreatePumpTrade("signature-1", mintBytes));
            await Task.Delay(300);
            AssertEqual(2, prices.Count, "A replay duplicate produced another price.");

            using (var process = Process.GetProcessById(firstProcessId))
            {
                process.Kill(true);
                process.WaitForExit(2000);
            }
            await WaitUntilAsync(
                () => states.Contains(OnChainEngineState.Reconnecting),
                TimeSpan.FromSeconds(5),
                "reconnecting state");
            await WaitUntilAsync(
                () => client.State == OnChainEngineState.Ready,
                TimeSpan.FromSeconds(10),
                "replacement engine");
            var secondProcessId = GetSingleNewEngineProcessId(existingProcessIds);
            Assert(firstProcessId != secondProcessId, "Recovery reused the terminated process.");

            await client.PublishTransactionUpdateAsync(CreatePumpTrade("signature-2", mintBytes));
            await WaitUntilAsync(() => prices.Count == 3, TimeSpan.FromSeconds(5), "post-recovery price");

            await client.ReplaceWatchedPoolsAsync([]);
            await WaitUntilAsync(
                () => !GetEngineProcessIds().Except(existingProcessIds).Any(),
                TimeSpan.FromSeconds(5),
                "lazy engine stop");
            AssertEqual(OnChainEngineState.Stopped, client.State, "Clearing watched pools did not stop the engine.");
            AssertEqual(3, prices.Count, "Recovery produced duplicate or missing prices.");
        }
        finally { await client.StopAsync(); }
    }
}

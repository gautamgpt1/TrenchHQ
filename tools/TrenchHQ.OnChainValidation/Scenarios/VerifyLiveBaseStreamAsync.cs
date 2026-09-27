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
    static async Task VerifyLiveBaseStreamAsync(string enginePath)
    {
        const string poolId = "0xb2cc224c1c9fee385f8ad6a55b4d94e92359dc59";
        var selection = new OnChainWatchedPoolSelection
        {
            SelectedMint = BaseDeploymentRegistry.WrappedEther,
            Descriptor = new OnChainPoolDescriptor
            {
                PoolKey = new OnChainPoolKey
                {
                    DeploymentKey = BaseDeploymentRegistry.Catalog.Deployment(
                        OnChainProtocolIds.AerodromeSlipstream,
                        BaseDeploymentRegistry.AerodromeSlipstreamInitialFactory),
                    PoolId = poolId
                },
                PoolType = "concentratedLiquidity",
                ProgramId = BaseDeploymentRegistry.AerodromeSlipstreamInitialFactory,
                BaseMint = BaseDeploymentRegistry.WrappedEther,
                QuoteMint = BaseDeploymentRegistry.Usdc,
                BaseDecimals = 18,
                QuoteDecimals = 6,
                PairOrientation = "selectedAsToken0",
                DiscoverySource = "liveSmoke:fixedBaseSlipstreamInitial",
                SupportStatus = OnChainSupportStatus.Supported,
                TickSpacing = 100,
                Asset0 = new OnChainAssetKey
                {
                    ChainNamespace = ChainNamespaces.Eip155,
                    ChainId = EvmChainDefinitions.BaseMainnetChainId,
                    Address = BaseDeploymentRegistry.WrappedEther
                },
                Asset1 = new OnChainAssetKey
                {
                    ChainNamespace = ChainNamespaces.Eip155,
                    ChainId = EvmChainDefinitions.BaseMainnetChainId,
                    Address = BaseDeploymentRegistry.Usdc
                }
            }
        };
        var configuration = CreateProviderConfiguration(
            OnChainProviderTypes.BasePublic,
            "wss://base-rpc.publicnode.com",
            "https://base-rpc.publicnode.com");
        configuration.Id = "live-publicnode-base";
        configuration.CapabilitySnapshot = new OnChainProviderCapabilitySnapshot
        {
            ObservedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ChainId = OnChainProviderCapabilityState.Supported,
            SafeBlock = OnChainProviderCapabilityState.Supported,
            FinalizedBlock = OnChainProviderCapabilityState.Supported,
            BlockHashCall = OnChainProviderCapabilityState.Supported,
            BlockHashLogs = OnChainProviderCapabilityState.Supported,
            WebSocketHeads = OnChainProviderCapabilityState.Supported,
            WebSocketLogs = OnChainProviderCapabilityState.Supported
        };

        using var rpcHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var rpc = new EvmJsonRpcClient(rpcHttp, configuration, null);
        var checkpointRoot = Path.Combine(
            Path.GetTempPath(),
            "TrenchHQLiveBaseCheckpoint",
            Guid.NewGuid().ToString("N"));
        var baselineProcessIds = GetEngineProcessIds();
        var prices = new ConcurrentQueue<OnChainPriceUpdate>();
        var sourceLogs = new ConcurrentQueue<EvmLogUpdate>();
        var streamErrors = new ConcurrentQueue<string>();
        var client = new OnChainEngineClient(enginePath);
        client.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
        var coordinator = new EvmStreamCoordinator(
            client,
            checkpointRoot,
            EvmChainDefinitions.BaseMainnet,
            rpcHttp,
            (profile, apiKey) => new ObservingEvmStreamSource(
                new EvmWebSocketStreamSource(profile, apiKey),
                sourceLogs,
                static () => { }));
        coordinator.StreamError += (_, message) => streamErrors.Enqueue(message);
        try
        {
            var streamStartedAfter = (await rpc.GetBlockAsync("latest", CancellationToken.None)).Number;
            await coordinator.StartAsync(configuration, null, [selection]);
            try
            {
                await WaitUntilAsync(
                    () => coordinator.State == OnChainRecoveryState.Live
                          && sourceLogs.Any(log =>
                              log.Address.Equals(poolId, StringComparison.OrdinalIgnoreCase)
                              && log.BlockNumber > streamStartedAfter)
                          && prices.Any(update =>
                              update.PoolKey.ChainId == EvmChainDefinitions.BaseMainnetChainId
                              && update.PoolKey.PoolId.Equals(poolId, StringComparison.OrdinalIgnoreCase)
                              && update.ChainPosition?.BlockNumber > streamStartedAfter
                              && update.LastTradeEventId != null),
                    TimeSpan.FromMinutes(2),
                    "natural Base Slipstream WebSocket price");
            }
            catch (TimeoutException)
            {
                Console.WriteLine(
                    $"LIVE BASE TIMEOUT | state={coordinator.State} | logs={sourceLogs.Count} | prices={prices.Count} | errors={string.Join(" | ", streamErrors)}");
                throw;
            }
            var live = prices.Last(update =>
                update.PoolKey.PoolId.Equals(poolId, StringComparison.OrdinalIgnoreCase)
                && update.ChainPosition?.BlockNumber > streamStartedAfter
                && update.LastTradeEventId != null);
            Assert(live.LastTradePriceQuote != null && live.SpotPriceQuote != null,
                "The live Base Slipstream update omitted trade or spot price.");
            Assert(live.SourceId.Contains("base-json-rpc:basePublic", StringComparison.Ordinal),
                "The live Base update lost chain/provider attribution.");
            AssertEqual(1, GetEngineProcessIds().Except(baselineProcessIds).Count(),
                "The live Base smoke did not retain exactly one engine process.");
            var checkpoint = await EvmCheckpointStore.LoadAsync(checkpointRoot, configuration.Id);
            Assert(checkpoint?.ChainId == EvmChainDefinitions.BaseMainnetChainId
                   && checkpoint.SafeBlockNumber > 0,
                "The live Base coordinator did not persist a Base safe checkpoint.");
            Console.WriteLine(
                $"LIVE BASE | {selection.Descriptor.PoolKey.ProtocolId} | block={live.ChainPosition?.BlockNumber} | event={live.LastTradeEventId} | source={live.SourceId} | errors={streamErrors.Count}"
                + (streamErrors.IsEmpty ? string.Empty : $" | {string.Join(" | ", streamErrors)}"));
        }
        finally
        {
            await coordinator.StopAsync();
            await client.StopAsync();
            if (Directory.Exists(checkpointRoot))
            {
                Directory.Delete(checkpointRoot, true);
            }
        }
        await WaitUntilAsync(
            () => !GetEngineProcessIds().Except(baselineProcessIds).Any(),
            TimeSpan.FromSeconds(10),
            "live Base engine cleanup");
    }
}

using TrenchHQ.TestSupport;
using TrenchHQ.Core.OnChain;
using TrenchHQ.Core.OnChain.Evm;
using TrenchHQ.Core.Providers;
using TrenchHQ.Infrastructure.OnChain;
using TrenchHQ.Infrastructure.OnChain.Evm;
using TrenchHQ.Infrastructure.Providers;
using System.Collections.Concurrent;
using System.Text.Json;

internal static class TickerBatchTests
{
    internal static async Task RunAsync(string enginePath)
    {
        foreach (var limit in new[] { 256, 16 })
        {
            var chain = EvmChainDefinitions.Supported.Single(item => item.ChainId == "1");
            var template = chain.CreateNativeUsdReferenceSelection!();
            var pools = Enumerable.Range(1, 128).Select(index =>
            {
                var pool = JsonSerializer.Deserialize<OnChainWatchedPoolSelection>(JsonSerializer.Serialize(template))!;
                pool.Descriptor.PoolKey.PoolId = $"0x{index:x40}";
                return pool;
            }).ToArray();
            var engine = new OnChainEngineClient(enginePath);
            var prices = new ConcurrentQueue<OnChainPriceUpdate>();
            engine.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
            using var handler = new BatchHandler(limit);
            using var http = new HttpClient(handler);
            var coordinator = new EvmStreamCoordinator(engine,
                Path.Combine(Path.GetTempPath(), "TrenchHQBatchFixture", Guid.NewGuid().ToString("N")), chain, http,
                (_, _) => throw new Exception("Polling opened a socket."));
            try
            {
                var profile = OnChainProviderConfigurationStore.CreateConfiguration(OnChainProviderCatalog.Get(OnChainProviderTypes.PublicNodeEthereum));
                await coordinator.StartAsync(profile, null, pools, webSocketPoolIds: new HashSet<string>());
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (prices.Count < 256 && DateTime.UtcNow < deadline) await Task.Delay(20);
                if (prices.Count != 256) throw new Exception($"Large snapshot produced {prices.Count} prices instead of 256.");
            }
            finally { await coordinator.StopAsync(); await engine.StopAsync(); }
            var expected = limit == 256 ? 2 : 36; // 4 size rejections, then 16 batches per snapshot.
            if (handler.CallCount != expected || handler.LatestCount != 2)
                throw new Exception($"Unexpected learned batch counts: {handler.CallCount} calls, {handler.LatestCount} heads.");
            if (handler.UnpinnedCount != 0) throw new Exception("A split snapshot lost hash pinning.");
            Console.WriteLine($"PASS: 128 V3 pools, limit={limit}, prices=256, eth_call={handler.CallCount}, rejected={handler.Rejections}, pinned throughout.");
        }
        // Rate and authorization errors must not be mistaken for a size limit and multiplied into retries.
        foreach (var status in new[] { 401, 402, 403, 429, 413 })
        {
            using var http = new HttpClient(new StatusHandler(status));
            var profile = OnChainProviderConfigurationStore.CreateConfiguration(OnChainProviderCatalog.Get(OnChainProviderTypes.AlchemyEthereum));
            try { await new EvmJsonRpcClient(http, profile, "fixture-key").GetBlockAsync("latest", default); throw new Exception("HTTP failure accepted."); }
            catch (EvmJsonRpcException exception)
            {
                if ((exception.Kind == EvmRpcFailureKind.RequestLimitExceeded) != (status == 413))
                    throw new Exception("HTTP failure could trigger an incorrect batch split.");
            }
        }
    }

    private sealed class BatchHandler(int limit) : HttpMessageHandler
    {
        internal int CallCount, LatestCount, Rejections, UnpinnedCount;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            var root = document.RootElement;
            var method = root.GetProperty("method").GetString();
            object result;
            if (method == "eth_getBlockByNumber")
            {
                LatestCount++;
                result = new { number = $"0x{LatestCount:x}", hash = $"0x{LatestCount:x64}", parentHash = $"0x{LatestCount - 1:x64}",
                    timestamp = $"0x{DateTimeOffset.UtcNow.ToUnixTimeSeconds():x}" };
            }
            else if (method == "eth_call")
            {
                CallCount++;
                var parameters = root.GetProperty("params");
                if (parameters[1].GetProperty("blockHash").GetString() != $"0x{LatestCount:x64}"
                    || !parameters[1].GetProperty("requireCanonical").GetBoolean()) UnpinnedCount++;
                var data = parameters[0].GetProperty("data").GetString()!;
                var count = Convert.ToInt32(data.Substring(138, 64), 16);
                if (count > limit)
                {
                    Rejections++;
                    return Reply(new { jsonrpc = "2.0", id = root.GetProperty("id").GetInt64(), error = new { code = -32000, message = "gas required exceeds allowance (fixture)" } });
                }
                var values = new List<string>();
                for (var index = 0; index < count; index++)
                {
                    var offset = Convert.ToInt32(data.Substring(202 + index * 64, 64), 16);
                    var selector = data.Substring(202 + offset * 2 + 192, 8);
                    values.Add(selector == EthereumAbi.Slot0Selector[2..]
                        ? "0x" + (System.Numerics.BigInteger.One << 96).ToString("x").PadLeft(64, '0') + string.Concat(Enumerable.Repeat(new string('0', 64), 5)) + 1.ToString("x64")
                        : selector == EthereumAbi.LiquiditySelector[2..] ? "0x" + 100.ToString("x64")
                        : throw new Exception("Unexpected aggregate selector."));
                }
                result = EvmCoordinatorRpcFixtureHandler.EncodeResults(values.ToArray());
            }
            else throw new Exception($"Unexpected large-snapshot method: {method}");
            return Reply(new { jsonrpc = "2.0", id = root.GetProperty("id").GetInt64(), result });
        }
        private static HttpResponseMessage Reply(object payload) => new(System.Net.HttpStatusCode.OK)
            { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload)) };
    }

    private sealed class StatusHandler(int status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = new StringContent("{}") });
    }
}

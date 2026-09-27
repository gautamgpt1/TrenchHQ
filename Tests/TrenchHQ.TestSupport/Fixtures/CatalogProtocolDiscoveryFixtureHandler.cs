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
using static TrenchHQ.TestSupport.OnChainFixtures;

namespace TrenchHQ.TestSupport;

internal sealed class CatalogProtocolDiscoveryFixtureHandler(
    string selectedToken,
    IReadOnlyCollection<EvmCatalogPoolFixture> fixtures,
    string? timedOutPool = null,
    EvmChainDefinition? chain = null,
    EvmDeploymentCatalog? deployments = null,
    bool blockExplorerUnavailable = false,
    int? maximumRpcLogBlockCount = null,
    bool blockExplorerRateLimitedOnce = false,
    bool blockExplorerLogsUnavailable = false,
    TimeSpan? poolCodeDelay = null,
    string? undelayedPoolAddress = null) : HttpMessageHandler
{
    internal const string BlockHash =
        "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ParentHash =
        "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    public bool SawUnpinnedStateRead { get; private set; }
    public bool SawExpectedBlockscoutApiRoot { get; private set; }
    public bool SawRpcV4Lookup { get; private set; }
    public ulong MaximumObservedRpcLogBlockCount { get; private set; }
    public int RpcBlockLookupCount { get; private set; }
    public int MaximumConcurrentPoolCodeRequests { get; private set; }
    private readonly ConcurrentDictionary<string, int> _rpcMethodCounts = new(StringComparer.Ordinal);
    private readonly object _poolCodeRequestLock = new();
    private int _activePoolCodeRequests;
    private bool _timedOut;
    private bool _blockExplorerRateLimited;
    private EvmChainDefinition Chain => chain ?? EvmChainDefinitions.EthereumMainnet;
    private EvmDeploymentCatalog Deployments => deployments ?? EthereumDeploymentRegistry.Catalog;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get)
        {
            return GetBlockExplorerResponse(request);
        }
        using var document = JsonDocument.Parse(
            await request.Content!.ReadAsByteArrayAsync(cancellationToken));
        var rpcRequest = document.RootElement;
        var method = rpcRequest.GetProperty("method").GetString()
                     ?? throw new InvalidOperationException("Catalog discovery RPC method is missing.");
        _rpcMethodCounts.AddOrUpdate(method, 1, static (_, count) => count + 1);
        if (method == "eth_getBlockByNumber")
        {
            RpcBlockLookupCount++;
        }
        if (poolCodeDelay is { } delay
            && method == "eth_getCode"
            && !rpcRequest.GetProperty("params")[0].GetString()!.Equals(
                selectedToken,
                StringComparison.OrdinalIgnoreCase)
            && !rpcRequest.GetProperty("params")[0].GetString()!.Equals(
                undelayedPoolAddress,
                StringComparison.OrdinalIgnoreCase))
        {
            lock (_poolCodeRequestLock)
            {
                _activePoolCodeRequests++;
                MaximumConcurrentPoolCodeRequests = Math.Max(
                    MaximumConcurrentPoolCodeRequests,
                    _activePoolCodeRequests);
            }
            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            finally
            {
                lock (_poolCodeRequestLock)
                {
                    _activePoolCodeRequests--;
                }
            }
        }
        object result = method switch
        {
            "eth_chainId" => "0x" + ulong.Parse(Chain.ChainId).ToString("x"),
            "eth_getBlockByNumber" => new
            {
                number = "0x10",
                hash = BlockHash,
                parentHash = ParentHash,
                timestamp = "0x20"
            },
            "eth_getCode" => GetCode(rpcRequest),
            "eth_call" => GetCallResult(rpcRequest),
            "eth_getLogs" => GetV4InitializeLogs(rpcRequest),
            "eth_getTransactionReceipt" => GetTransactionReceipt(rpcRequest),
            _ => throw new InvalidOperationException($"Unexpected catalog discovery RPC method {method}.")
        };
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0",
                id = rpcRequest.GetProperty("id").GetInt64(),
                result
            }))
        };
    }

    internal int GetRpcMethodCount(string method) =>
        _rpcMethodCounts.TryGetValue(method, out var count) ? count : 0;

    private HttpResponseMessage GetBlockExplorerResponse(HttpRequestMessage request)
    {
        var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
        SawExpectedBlockscoutApiRoot |= uri.StartsWith(
            Chain.BlockscoutApiRoot,
            StringComparison.Ordinal);
        if (blockExplorerUnavailable)
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
        }
        if (blockExplorerRateLimitedOnce
            && !_blockExplorerRateLimited
            && uri.Contains("action=getLogs", StringComparison.Ordinal))
        {
            _blockExplorerRateLimited = true;
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                TimeSpan.Zero);
            return response;
        }
        if (blockExplorerLogsUnavailable
            && uri.Contains("action=getLogs", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
        }
        object payload;
        if (uri.Contains("/api/v2/addresses/", StringComparison.Ordinal))
        {
            var fixture = fixtures.Single(item => item.ProtocolId == OnChainProtocolIds.UniswapV4);
            payload = uri.Contains("/logs?", StringComparison.Ordinal)
                ? new
                {
                    items = new[]
                    {
                        new
                        {
                            transaction_hash = fixture.PoolAddress,
                            block_number = 16,
                            block_hash = BlockHash,
                            topics = new[]
                            {
                                EvmEventTopics.UniswapV4InitializeTopic,
                                fixture.PoolAddress
                            }
                        }
                    }
                }
                : new { creation_transaction_hash = fixture.PoolAddress };
        }
        else if (uri.Contains("action=getblocknobytime", StringComparison.Ordinal))
        {
            payload = new { status = "1", message = "OK", result = new { blockNumber = 16 } };
        }
        else if (uri.Contains("action=getLogs", StringComparison.Ordinal))
        {
            var fixture = fixtures.Single(item => item.ProtocolId == OnChainProtocolIds.UniswapV4);
            payload = new
            {
                status = "1",
                message = "OK",
                result = new[]
                {
                    new
                    {
                        transactionHash = fixture.PoolAddress,
                        blockNumber = "0x10",
                        blockHash = BlockHash
                    }
                }
            };
        }
        else
        {
            throw new InvalidOperationException($"Unexpected block explorer request {uri}.");
        }
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload))
        };
    }

    private object GetV4InitializeLogs(JsonElement request)
    {
        SawRpcV4Lookup = true;
        var fixture = fixtures.Single(item => item.ProtocolId == OnChainProtocolIds.UniswapV4);
        var filter = request.GetProperty("params")[0];
        if (!EvmAddress.TryParseQuantity(filter.GetProperty("fromBlock").GetString(), out var fromBlock)
            || !EvmAddress.TryParseQuantity(filter.GetProperty("toBlock").GetString(), out var toBlock)
            || toBlock < fromBlock)
        {
            throw new InvalidOperationException("The V4 RPC lookup used an invalid block range.");
        }
        var blockCount = toBlock - fromBlock + 1;
        MaximumObservedRpcLogBlockCount = Math.Max(MaximumObservedRpcLogBlockCount, blockCount);
        if (maximumRpcLogBlockCount.HasValue && blockCount > (ulong)maximumRpcLogBlockCount.Value)
        {
            throw new InvalidOperationException(
                $"The fixture provider rejected an eth_getLogs request for {blockCount} blocks.");
        }
        if (maximumRpcLogBlockCount.HasValue && (16 < fromBlock || 16 > toBlock))
        {
            return Array.Empty<object>();
        }
        return new[] { new { transactionHash = fixture.PoolAddress } };
    }

    private object GetTransactionReceipt(JsonElement request)
    {
        var transactionHash = request.GetProperty("params")[0].GetString();
        var fixture = fixtures.Single(item =>
            item.ProtocolId == OnChainProtocolIds.UniswapV4
            && item.PoolAddress.Equals(transactionHash, StringComparison.OrdinalIgnoreCase));
        var currencies = new[] { selectedToken, fixture.QuoteAddress }
            .OrderBy(static address => address, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new
        {
            status = "0x1",
            blockNumber = "0x10",
            blockHash = BlockHash,
            logs = new[]
            {
                new
                {
                    address = Deployments.UniswapV4PoolManager,
                    topics = new[]
                    {
                        EvmEventTopics.UniswapV4InitializeTopic,
                        fixture.PoolAddress,
                        AddressTopic(currencies[0]),
                        AddressTopic(currencies[1])
                    },
                    data = BigWords(
                        fixture.FeeTier,
                        fixture.TickSpacing,
                        ParseAddressWord(fixture.HookAddress),
                        System.Numerics.BigInteger.One << 96,
                        0)
                }
            }
        };
    }

    private string GetCode(JsonElement request)
    {
        ValidatePinned(request.GetProperty("params")[1]);
        return "0x6000";
    }

    private string GetCallResult(JsonElement request)
    {
        var parameters = request.GetProperty("params");
        ValidatePinned(parameters[1]);
        var call = parameters[0];
        var to = call.GetProperty("to").GetString()!.ToLowerInvariant();
        var data = call.GetProperty("data").GetString()!.ToLowerInvariant();
        if (!_timedOut && to.Equals(timedOutPool, StringComparison.OrdinalIgnoreCase))
        {
            _timedOut = true;
            throw new TaskCanceledException("Fixture RPC timeout.");
        }
        if (data == EthereumAbi.DecimalsSelector)
        {
            return Word(Chain.ChainId == EvmChainDefinitions.BaseMainnetChainId
                        && to == BaseDeploymentRegistry.Usdc
                        || Chain.ChainId == EvmChainDefinitions.RobinhoodMainnetChainId
                        && to == RobinhoodDeploymentRegistry.Usdg
                ? 6UL
                : 18UL);
        }

        if (to == Deployments.UniswapV4StateView)
        {
            if (data.StartsWith(EthereumAbi.UniswapV4StateViewSlot0Selector, StringComparison.Ordinal))
            {
                return BigWords(System.Numerics.BigInteger.One << 96, 0, 0, 10000);
            }
            if (data.StartsWith(EthereumAbi.UniswapV4StateViewLiquiditySelector, StringComparison.Ordinal))
            {
                return Word(100);
            }
        }

        var pool = fixtures.FirstOrDefault(fixture =>
            fixture.PoolAddress.Equals(to, StringComparison.OrdinalIgnoreCase));
        if (pool != null)
        {
            if (data == EthereumAbi.FactorySelector)
            {
                return AddressWord(pool.FactoryAddress);
            }
            if (data == EthereumAbi.Token0Selector)
            {
                return AddressWord(selectedToken);
            }
            if (data == EthereumAbi.Token1Selector)
            {
                return AddressWord(pool.QuoteAddress);
            }
            if (pool.ProtocolId is OnChainProtocolIds.UniswapV2 or OnChainProtocolIds.PancakeV2
                && data == EthereumAbi.GetReservesSelector)
            {
                return Words(10, 20, 123);
            }
            if (pool.ProtocolId is OnChainProtocolIds.UniswapV3 or OnChainProtocolIds.PancakeV3)
            {
                if (data == EthereumAbi.FeeSelector)
                {
                    return Word(pool.FeeTier);
                }
                if (data == EthereumAbi.TickSpacingSelector)
                {
                    return Word((ulong)pool.TickSpacing);
                }
                if (data == EthereumAbi.Slot0Selector)
                {
                    return BigWords(System.Numerics.BigInteger.One << 96, 0, 0, 0, 0, 0, 1);
                }
                if (data == EthereumAbi.LiquiditySelector)
                {
                    return Word(100);
                }
            }
            if (pool.ProtocolId == OnChainProtocolIds.AerodromeClassic)
            {
                if (data == EthereumAbi.StableSelector)
                {
                    return Word(pool.Stable == true ? 1UL : 0UL);
                }
                if (data == EthereumAbi.GetReservesSelector)
                {
                    return Words(10, 20, 123);
                }
            }
            if (pool.ProtocolId == OnChainProtocolIds.AerodromeSlipstream)
            {
                if (data == EthereumAbi.TickSpacingSelector)
                {
                    return Word((ulong)pool.TickSpacing);
                }
                if (data == EthereumAbi.Slot0Selector)
                {
                    return BigWords(System.Numerics.BigInteger.One << 96, 0, 0, 0, 0, 1);
                }
                if (data == EthereumAbi.LiquiditySelector)
                {
                    return Word(100);
                }
                if (data == EthereumAbi.FeeSelector)
                {
                    return Word(700);
                }
            }
        }

        var infinityPool = fixtures.FirstOrDefault(fixture =>
            fixture.FactoryAddress.Equals(to, StringComparison.OrdinalIgnoreCase)
            && fixture.ProtocolId is OnChainProtocolIds.PancakeInfinityCl
                or OnChainProtocolIds.PancakeInfinityBin
            && data.EndsWith(fixture.PoolAddress[2..], StringComparison.OrdinalIgnoreCase));
        if (infinityPool != null)
        {
            if (data.StartsWith(EthereumAbi.InfinityPoolKeySelector, StringComparison.Ordinal))
            {
                var encodedParameters = (ulong)infinityPool.TickSpacing << 16;
                return BigWords(
                    ParseAddressWord(selectedToken),
                    ParseAddressWord(infinityPool.QuoteAddress),
                    0,
                    ParseAddressWord(infinityPool.FactoryAddress),
                    infinityPool.FeeTier,
                    encodedParameters);
            }
            if (data.StartsWith(EthereumAbi.InfinitySlot0Selector, StringComparison.Ordinal))
            {
                return infinityPool.ProtocolId == OnChainProtocolIds.PancakeInfinityCl
                    ? BigWords(System.Numerics.BigInteger.One << 96, 0, 4097, infinityPool.FeeTier)
                    : BigWords((System.Numerics.BigInteger.One << 23) + 1, 4097, infinityPool.FeeTier);
            }
            if (data.StartsWith(EthereumAbi.InfinityLiquiditySelector, StringComparison.Ordinal))
            {
                return Word(100);
            }
        }

        if (data.StartsWith(EthereumAbi.IsPoolSelector, StringComparison.Ordinal))
        {
            var requestedPool = AddressFromWord(data[^64..]);
            return Word(fixtures.Any(fixture =>
                fixture.FactoryAddress.Equals(to, StringComparison.OrdinalIgnoreCase)
                && fixture.PoolAddress.Equals(requestedPool, StringComparison.OrdinalIgnoreCase))
                ? 1UL
                : 0UL);
        }

        var factoryPool = fixtures.FirstOrDefault(fixture =>
            fixture.FactoryAddress.Equals(to, StringComparison.OrdinalIgnoreCase)
            && data.Contains(fixture.QuoteAddress[2..], StringComparison.OrdinalIgnoreCase)
            && fixture.ProtocolId switch
            {
                OnChainProtocolIds.UniswapV2 or OnChainProtocolIds.PancakeV2 =>
                    data.StartsWith(EthereumAbi.GetPairSelector, StringComparison.Ordinal),
                OnChainProtocolIds.UniswapV3 or OnChainProtocolIds.PancakeV3 =>
                    data.StartsWith(EthereumAbi.GetPoolSelector, StringComparison.Ordinal)
                    && data[^64..].EndsWith($"{fixture.FeeTier:x6}", StringComparison.Ordinal),
                OnChainProtocolIds.AerodromeClassic =>
                    data.StartsWith(EthereumAbi.AerodromeClassicGetPoolSelector, StringComparison.Ordinal)
                    && data[^64..] == (fixture.Stable == true ? "1" : "0").PadLeft(64, '0'),
                OnChainProtocolIds.AerodromeSlipstream =>
                    data.StartsWith(EthereumAbi.AerodromeSlipstreamGetPoolSelector, StringComparison.Ordinal)
                    && data[^64..].EndsWith($"{fixture.TickSpacing:x6}", StringComparison.Ordinal),
                _ => false
            });
        if (factoryPool != null)
        {
            return AddressWord(factoryPool.PoolAddress);
        }
        if (data.StartsWith(EthereumAbi.GetPairSelector, StringComparison.Ordinal)
            || data.StartsWith(EthereumAbi.GetPoolSelector, StringComparison.Ordinal)
            || data.StartsWith(EthereumAbi.AerodromeClassicGetPoolSelector, StringComparison.Ordinal)
            || data.StartsWith(EthereumAbi.AerodromeSlipstreamGetPoolSelector, StringComparison.Ordinal))
        {
            return AddressWord("0x0000000000000000000000000000000000000000");
        }
        throw new InvalidOperationException($"Unexpected catalog discovery eth_call to {to} with {data}.");
    }

    private void ValidatePinned(JsonElement blockReference)
    {
        if (blockReference.ValueKind != JsonValueKind.Object
            || blockReference.GetProperty("blockHash").GetString() != BlockHash
            || !blockReference.GetProperty("requireCanonical").GetBoolean())
        {
            SawUnpinnedStateRead = true;
        }
    }

    private static string AddressWord(string address)
    {
        return "0x" + address[2..].PadLeft(64, '0');
    }

    private static string AddressFromWord(string word)
    {
        return "0x" + word[^40..];
    }

    private static string AddressTopic(string address)
    {
        return "0x" + address[2..].PadLeft(64, '0');
    }

    private static string Word(ulong value) => $"0x{value:x64}";

    private static System.Numerics.BigInteger ParseAddressWord(string address)
    {
        return System.Numerics.BigInteger.Parse(
            "0" + address[2..],
            System.Globalization.NumberStyles.AllowHexSpecifier,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Words(params ulong[] values)
    {
        return "0x" + string.Concat(values.Select(static value => $"{value:x64}"));
    }

    private static string BigWords(params System.Numerics.BigInteger[] values)
    {
        var modulus = System.Numerics.BigInteger.One << 256;
        return "0x" + string.Concat(values.Select(value =>
        {
            var encoded = value.Sign < 0 ? modulus + value : value;
            return encoded.ToString("x").PadLeft(64, '0');
        }));
    }
}

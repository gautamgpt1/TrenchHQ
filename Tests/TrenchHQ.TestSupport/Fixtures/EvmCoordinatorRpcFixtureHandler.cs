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

internal sealed class EvmCoordinatorRpcFixtureHandler(
    string poolAddress,
    bool advanceLatest = false,
    string? referenceAddress = null,
    bool inconsistentFinality = false) : HttpMessageHandler
{
    private int _latestBlockRequestCount;
    private int _blockNumberRequestCount;
    private int _finalizedBlockRequestCount;
    private int _pinnedCallCount;
    private int _numberedCallCount;
    private int _referenceCallCount;
    private int _directReferenceCallCount;
    private int _directSelectedCallCount;
    private int _multicallRequestCount;
    private int _numericBlockRequestCount;
    private int _contiguousAdvanceStart = -1;

    internal int GetLogsCount { get; private set; }
    internal ConcurrentQueue<string> LogFilters { get; } = new();
    internal int LatestBlockRequestCount => Volatile.Read(ref _latestBlockRequestCount);
    internal int BlockNumberRequestCount => Volatile.Read(ref _blockNumberRequestCount);
    internal int FinalizedBlockRequestCount => Volatile.Read(ref _finalizedBlockRequestCount);
    internal int PinnedCallCount => Volatile.Read(ref _pinnedCallCount);
    internal int NumberedCallCount => Volatile.Read(ref _numberedCallCount);
    internal int ReferenceCallCount => Volatile.Read(ref _referenceCallCount);
    internal int DirectReferenceCallCount => Volatile.Read(ref _directReferenceCallCount);
    internal int DirectSelectedCallCount => Volatile.Read(ref _directSelectedCallCount);
    internal int MulticallRequestCount => Volatile.Read(ref _multicallRequestCount);
    internal int NumericBlockRequestCount => Volatile.Read(ref _numericBlockRequestCount);
    internal bool SawPinnedCall { get; private set; }

    internal void EnableContiguousLatest() =>
        Volatile.Write(ref _contiguousAdvanceStart, LatestBlockRequestCount);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(
            await request.Content!.ReadAsByteArrayAsync(cancellationToken));
        var rpcRequest = document.RootElement;
        var method = rpcRequest.GetProperty("method").GetString();
        object result = method switch
        {
            "eth_getBlockByNumber" => GetBlock(rpcRequest.GetProperty("params")[0].GetString()!),
            "eth_blockNumber" => GetBlockNumber(),
            "eth_getLogs" => GetLogs(rpcRequest.GetProperty("params")),
            "eth_call" => GetCall(rpcRequest.GetProperty("params")),
            _ => throw new InvalidOperationException($"Unexpected coordinator RPC method {method}.")
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

    private object GetBlock(string tag)
    {
        if (tag.StartsWith("0x", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _numericBlockRequestCount);
        }
        if (tag == "finalized")
        {
            Interlocked.Increment(ref _finalizedBlockRequestCount);
        }
        var number = tag switch
        {
            "latest" => GetLatestBlockNumber(),
            "safe" => 15UL,
            "finalized" => inconsistentFinality ? 16UL : 14UL,
            _ => Convert.ToUInt64(tag[2..], 16)
        };
        var timestamp = (ulong)Math.Max(
            1,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() - checked((long)(17 - Math.Min(number, 17))) * 12);
        return new
        {
            number = $"0x{number:x}",
            hash = HashValue(number),
            parentHash = HashValue(number == 0 ? 0 : number - 1),
            timestamp = $"0x{timestamp:x}"
        };
    }

    private ulong GetLatestBlockNumber()
    {
        var request = Interlocked.Increment(ref _latestBlockRequestCount);
        return LatestBlockNumberForRequest(request);
    }

    private string GetBlockNumber()
    {
        Interlocked.Increment(ref _blockNumberRequestCount);
        return $"0x{LatestBlockNumberForRequest(LatestBlockRequestCount + 1):x}";
    }

    private ulong LatestBlockNumberForRequest(int request)
    {
        var contiguousStart = Volatile.Read(ref _contiguousAdvanceStart);
        return contiguousStart >= 0
            ? 16UL + checked((ulong)(request - contiguousStart))
            : advanceLatest
                ? 16UL + checked((ulong)(request - 1) * 100UL)
                : 16UL;
    }

    private object[] GetLogs(JsonElement parameters)
    {
        GetLogsCount++;
        LogFilters.Enqueue(parameters[0].GetRawText());
        if (Convert.ToUInt64(parameters[0].GetProperty("fromBlock").GetString()![2..], 16) > 16)
        {
            return [];
        }
        return
        [
            new
            {
                address = poolAddress,
                topics = new[] { EvmEventTopics.UniswapV2SyncTopic },
                data = Words(8_000_000_000_000_000_000UL, 16_000_000UL),
                blockNumber = "0x10",
                blockHash = HashValue(16),
                transactionHash = HashValue(6000),
                transactionIndex = "0x1",
                logIndex = "0x1",
                removed = false
            }
        ];
    }

    private string GetCall(JsonElement parameters)
    {
        var call = parameters[0];
        var to = call.GetProperty("to").GetString();
        var data = call.GetProperty("data").GetString();
        var blockReference = parameters[1];
        if (blockReference.ValueKind == JsonValueKind.String
            && blockReference.GetString()!.StartsWith("0x", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _numberedCallCount);
        }
        var isPinnedCall = blockReference.ValueKind == JsonValueKind.Object
                           && blockReference.GetProperty("requireCanonical").GetBoolean();
        if (isPinnedCall)
        {
            Interlocked.Increment(ref _pinnedCallCount);
        }
        SawPinnedCall |= isPinnedCall
                         && blockReference.GetProperty("blockHash").GetString() == HashValue(16);
        if (string.Equals(to, EvmMulticall3.Address, StringComparison.OrdinalIgnoreCase))
        {
            if (referenceAddress == null)
            {
                throw new InvalidOperationException("Unexpected paired reference call.");
            }
            Interlocked.Increment(ref _multicallRequestCount);
            Interlocked.Add(ref _referenceCallCount, 2);
            if (data == EvmMulticall3.EncodeCalls(
                    [(poolAddress, EthereumAbi.GetReservesSelector),
                     (referenceAddress, EthereumAbi.Slot0Selector),
                     (referenceAddress, EthereumAbi.LiquiditySelector)]))
            {
                return EncodeResults(
                    Words(8_000_000_000_000_000_000UL, 16_000_000UL, 123),
                    UniswapV3Slot0(), Words(100));
            }
            if (data != EvmMulticall3.EncodePair(
                    referenceAddress, EthereumAbi.Slot0Selector,
                    EthereumAbi.LiquiditySelector))
            {
                throw new InvalidOperationException("Unexpected batched reference call.");
            }
            return EncodePairResult(UniswapV3Slot0(), Words(100));
        }
        if (string.Equals(to, referenceAddress, StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _directReferenceCallCount);
            Interlocked.Increment(ref _referenceCallCount);
            if (data == EthereumAbi.Slot0Selector)
            {
                return UniswapV3Slot0();
            }
            if (data == EthereumAbi.LiquiditySelector)
            {
                return Words(100);
            }
            throw new InvalidOperationException("Unexpected reference pool call.");
        }
        if (!string.Equals(to, poolAddress, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unexpected direct selected-pool call.");
        }
        Interlocked.Increment(ref _directSelectedCallCount);
        return Words(8_000_000_000_000_000_000UL, 16_000_000UL, 123);
    }

    private static string HashValue(ulong value) => $"0x{value:x64}";

    private static string UniswapV3Slot0() => "0x" + string.Concat(
        new System.Numerics.BigInteger[]
        {
            System.Numerics.BigInteger.One << 96, 0, 0, 0, 0, 0, 1
        }.Select(static value => value.ToString("x").PadLeft(64, '0')));

    internal static string EncodePairResult(string first, string second) =>
        EncodeResults(first, second);

    internal static string EncodeResults(params string[] results)
    {
        static string Word(int value) => value.ToString("x64");
        static string Item(string data)
        {
            var bytes = (data.Length - 2) / 2;
            return Word(1) + Word(64) + Word(bytes)
                   + data[2..].PadRight(((bytes + 31) / 32) * 64, '0');
        }
        var items = results.Select(Item).ToArray();
        var offsets = new string[items.Length];
        var offset = items.Length * 32;
        for (var index = 0; index < items.Length; index++)
        {
            offsets[index] = Word(offset);
            offset += items[index].Length / 2;
        }
        return "0x" + Word(32) + Word(items.Length)
               + string.Concat(offsets) + string.Concat(items);
    }

    private static string Words(params ulong[] values)
    {
        return "0x" + string.Concat(values.Select(static value => value.ToString("x64")));
    }
}

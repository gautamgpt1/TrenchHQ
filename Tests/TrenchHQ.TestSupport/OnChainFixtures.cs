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

internal static class OnChainFixtures
{
    internal static OnChainRawTransactionUpdate CreatePumpTrade(string signature, byte[] mintBytes)
    {
        using var stream = new MemoryStream();
        stream.Write([189, 219, 127, 211, 78, 230, 97, 238]);
        stream.Write(mintBytes);
        stream.Write(BitConverter.GetBytes(2_000_000_000UL));
        stream.Write(BitConverter.GetBytes(1_000_000UL));
        stream.WriteByte(1);
        return new OnChainRawTransactionUpdate
        {
            Signature = signature,
            Slot = 20,
            Commitment = OnChainCommitment.Processed,
            SourceId = "integration-fixture",
            ProgramData =
            [
                new OnChainRawProgramData
                {
                    ProgramId = "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P",
                    DataBase64 = Convert.ToBase64String(stream.ToArray()),
                    LogIndex = 9
                }
            ]
        };
    }
    internal static OnChainProviderConfiguration CreateProviderConfiguration(
        string providerType,
        string streamEndpoint,
        string rpcEndpoint)
    {
        var preset = OnChainProviderCatalog.Get(providerType);
        return new OnChainProviderConfiguration
        {
            Id = Guid.NewGuid().ToString("N"),
            CredentialReference = Guid.NewGuid().ToString("N"),
            ChainNamespace = preset.ChainNamespace,
            ChainId = preset.ChainId,
            ProviderType = providerType,
            StreamEndpoint = streamEndpoint,
            RpcEndpoint = rpcEndpoint
        };
    }
    internal static OnChainWatchedPoolSelection CreateMixedMatrixSolanaSelection(byte[] mintBytes)
    {
        var mint = EncodeBase58(mintBytes);
        return new OnChainWatchedPoolSelection
        {
            SelectedMint = mint,
            Descriptor = new OnChainPoolDescriptor
            {
                PoolKey = new OnChainPoolKey
                {
                    ProtocolId = OnChainProtocolIds.PumpBondingCurve,
                    PoolAddress = "mixed-chain-solana-curve"
                },
                PoolType = "bondingCurve",
                ProgramId = "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P",
                BaseMint = mint,
                QuoteMint = "So11111111111111111111111111111111111111112",
                BaseDecimals = 6,
                QuoteDecimals = 9,
                SupportStatus = OnChainSupportStatus.Supported
            }
        };
    }
    internal static void AssertDecimalValue(OnChainDecimalValue? value, int expected, string message)
    {
        if (value == null
            || !System.Numerics.BigInteger.TryParse(value.Coefficient, out var coefficient)
            || coefficient != new System.Numerics.BigInteger(expected)
                              * System.Numerics.BigInteger.Pow(10, checked((int)value.Scale)))
        {
            throw new InvalidOperationException(
                $"{message} Actual: {value?.Coefficient ?? "null"}e-{value?.Scale.ToString() ?? "null"}.");
        }
    }
    internal static string AbiWords(params ulong[] values)
    {
        return "0x" + string.Concat(values.Select(static value => $"{value:x64}"));
    }
    internal static string AbiBigWords(params System.Numerics.BigInteger[] values)
    {
        var modulus = System.Numerics.BigInteger.One << 256;
        return "0x" + string.Concat(values.Select(value =>
        {
            var encoded = value.Sign < 0 ? modulus + value : value;
            return encoded.ToString("x").TrimStart('0').PadLeft(64, '0');
        }));
    }
    internal static EvmHeadUpdate CreateEvmHead(ulong number, string hash, string parentHash)
    {
        return new EvmHeadUpdate
        {
            ChainId = "1",
            ConnectionEpoch = 1,
            Number = number,
            Hash = hash,
            ParentHash = parentHash,
            Timestamp = number,
            ObservedAtUnixMs = (long)number
        };
    }
    internal static EvmLogUpdate CreateEvmLog(
        ulong blockNumber,
        string blockHash,
        string transactionHash,
        ulong logIndex)
    {
        return new EvmLogUpdate
        {
            ChainId = "1",
            ConnectionEpoch = 1,
            Address = "0x88e6a0c2ddd26feeb64f039a2c41296fcb3f5640",
            Topics = [EvmEventTopics.UniswapV3SwapTopic],
            Data = "0x00",
            BlockNumber = blockNumber,
            BlockHash = blockHash,
            TransactionHash = transactionHash,
            TransactionIndex = 0,
            LogIndex = logIndex,
            ObservedAtUnixMs = (long)blockNumber
        };
    }
    internal static string Hash(ulong value) => $"0x{value:x64}";
    internal static TrenchHQ.Yellowstone.SolanaStorage.TokenBalance CreateYellowstoneTokenBalance(
        uint accountIndex,
        string mint,
        ulong amount)
    {
        return new TrenchHQ.Yellowstone.SolanaStorage.TokenBalance
        {
            AccountIndex = accountIndex,
            Mint = mint,
            UiTokenAmount = new TrenchHQ.Yellowstone.SolanaStorage.UiTokenAmount
            {
                Amount = amount.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        };
    }
    internal static byte[] CreateMintAccount(byte decimals)
    {
        var data = new byte[82];
        data[44] = decimals;
        data[45] = 1;
        return data;
    }
    internal static byte[] CreateToken2022MintAccount(
        byte decimals,
        params (ushort ExtensionType, ushort ExtensionLength)[] extensions)
    {
        var data = new byte[166 + extensions.Sum(static extension => 4 + extension.ExtensionLength)];
        data[44] = decimals;
        data[45] = 1;
        data[165] = 1;
        var offset = 166;
        foreach (var extension in extensions)
        {
            BitConverter.GetBytes(extension.ExtensionType).CopyTo(data, offset);
            BitConverter.GetBytes(extension.ExtensionLength).CopyTo(data, offset + 2);
            offset += 4 + extension.ExtensionLength;
        }
        return data;
    }
    internal static byte[] CreatePumpCurveAccount(byte[] quoteMint)
    {
        using var stream = new MemoryStream();
        stream.Write([23, 183, 248, 55, 96, 216, 172, 96]);
        foreach (var value in new[] { 1_000_000UL, 2_000_000_000UL, 500_000UL, 1_000_000_000UL, 1_000_000UL })
        {
            stream.Write(BitConverter.GetBytes(value));
        }
        stream.WriteByte(0);
        stream.Write(new byte[32]);
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.Write(quoteMint);
        return stream.ToArray();
    }
    internal static byte[] CreatePumpSwapAccount(byte[] baseMint, byte[] quoteMint)
    {
        using var stream = new MemoryStream();
        stream.Write([241, 154, 109, 4, 17, 177, 109, 188]);
        stream.WriteByte(254);
        stream.Write(BitConverter.GetBytes((ushort)1));
        stream.Write(Enumerable.Repeat((byte)1, 32).ToArray());
        stream.Write(baseMint);
        stream.Write(quoteMint);
        stream.Write(Enumerable.Repeat((byte)6, 32).ToArray());
        stream.Write(Enumerable.Repeat((byte)4, 32).ToArray());
        stream.Write(Enumerable.Repeat((byte)5, 32).ToArray());
        stream.Write(BitConverter.GetBytes(1_000_000UL));
        stream.Write(Enumerable.Repeat((byte)7, 32).ToArray());
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.Write(new byte[16]);
        stream.Write(new byte[40]);
        return stream.ToArray();
    }
    internal static byte[] CreateMeteoraDlmmAccount(
        byte[] tokenXMint,
        byte[] tokenYMint,
        byte[] reserveX,
        byte[] reserveY)
    {
        var data = new byte[904];
        new byte[] { 33, 11, 49, 98, 181, 101, 177, 13 }.CopyTo(data, 0);
        BitConverter.GetBytes(-12).CopyTo(data, 76);
        BitConverter.GetBytes((ushort)25).CopyTo(data, 80);
        tokenXMint.CopyTo(data, 88);
        tokenYMint.CopyTo(data, 120);
        reserveX.CopyTo(data, 152);
        reserveY.CopyTo(data, 184);
        return data;
    }
    internal static byte[] CreateMeteoraDammV2Account(
        byte[] tokenAMint,
        byte[] tokenBMint,
        byte[] tokenAVault,
        byte[] tokenBVault)
    {
        var data = new byte[1112];
        new byte[] { 241, 154, 109, 4, 17, 177, 109, 188 }.CopyTo(data, 0);
        tokenAMint.CopyTo(data, 168);
        tokenBMint.CopyTo(data, 200);
        tokenAVault.CopyTo(data, 232);
        tokenBVault.CopyTo(data, 264);
        BitConverter.GetBytes(1UL).CopyTo(data, 464);
        data[696] = 1;
        return data;
    }
    internal static byte[] CreateMeteoraDammV1Account(
        byte[] tokenAMint,
        byte[] tokenBMint,
        byte[] vaultStateA,
        byte[] vaultStateB,
        byte[] vaultLpA,
        byte[] vaultLpB)
    {
        var data = new byte[952];
        new byte[] { 241, 154, 109, 4, 17, 177, 109, 188 }.CopyTo(data, 0);
        tokenAMint.CopyTo(data, 40);
        tokenBMint.CopyTo(data, 72);
        vaultStateA.CopyTo(data, 104);
        vaultStateB.CopyTo(data, 136);
        vaultLpA.CopyTo(data, 168);
        vaultLpB.CopyTo(data, 200);
        data[233] = 1;
        return data;
    }
    internal static byte[] CreateMeteoraDynamicVaultAccount(
        byte[] tokenMint,
        byte[] tokenVault,
        byte[] lpMint)
    {
        var data = new byte[1232];
        new byte[] { 211, 8, 232, 43, 2, 152, 117, 119 }.CopyTo(data, 0);
        data[8] = 1;
        BitConverter.GetBytes(1_000_000UL).CopyTo(data, 11);
        tokenVault.CopyTo(data, 19);
        tokenMint.CopyTo(data, 83);
        lpMint.CopyTo(data, 115);
        return data;
    }
    internal static byte[] CreateManifestMarketAccount(
        byte[] baseMint,
        byte[] quoteMint,
        byte[] baseVault,
        byte[] quoteVault)
    {
        var data = new byte[336];
        BitConverter.GetBytes(4_859_840_929_024_028_656UL).CopyTo(data, 0);
        baseMint.CopyTo(data, 16);
        quoteMint.CopyTo(data, 48);
        baseVault.CopyTo(data, 80);
        quoteVault.CopyTo(data, 112);
        BitConverter.GetBytes(80U).CopyTo(data, 152);
        return data;
    }
    internal static byte[] CreateRaydiumAmmV4Account(
        byte[] coinMint,
        byte[] pcMint,
        byte[] coinVault,
        byte[] pcVault)
    {
        var data = new byte[752];
        BitConverter.GetBytes(6UL).CopyTo(data, 0);
        BitConverter.GetBytes(6UL).CopyTo(data, 32);
        BitConverter.GetBytes(9UL).CopyTo(data, 40);
        coinVault.CopyTo(data, 336);
        pcVault.CopyTo(data, 368);
        coinMint.CopyTo(data, 400);
        pcMint.CopyTo(data, 432);
        return data;
    }
    internal static int[] GetEngineProcessIds()
    {
        return Process.GetProcessesByName("trenchhq-onchain-engine")
            .Select(static process =>
            {
                try
                {
                    return process.Id;
                }
                finally
                {
                    process.Dispose();
                }
            })
            .ToArray();
    }
    internal static int GetSingleNewEngineProcessId(int[] existingIds)
    {
        var ids = GetEngineProcessIds().Except(existingIds).ToArray();
        AssertEqual(1, ids.Length, "Expected exactly one shared on-chain engine process.");
        return ids[0];
    }
    internal static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string description)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"Timed out waiting for {description}.");
    }
    internal static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
    internal static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected {expected}; actual {actual}.");
        }
    }
    internal static string EncodeBase58(byte[] bytes)
    {
        const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        var value = new System.Numerics.BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        var result = string.Empty;
        while (value > 0)
        {
            value = System.Numerics.BigInteger.DivRem(value, 58, out var remainder);
            result = alphabet[(int)remainder] + result;
        }
        var zeroCount = bytes.TakeWhile(static value => value == 0).Count();
        return new string('1', zeroCount) + result;
    }
    internal static byte[] DecodeBase58(string value)
    {
        const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        var decoded = System.Numerics.BigInteger.Zero;
        foreach (var character in value)
        {
            var digit = alphabet.IndexOf(character);
            if (digit < 0)
            {
                throw new InvalidOperationException("Invalid base58 fixture.");
            }
            decoded = decoded * 58 + digit;
        }
        var bytes = decoded.ToByteArray(isUnsigned: true, isBigEndian: true);
        var leadingZeros = value.TakeWhile(static character => character == '1').Count();
        return Enumerable.Repeat((byte)0, leadingZeros).Concat(bytes).ToArray();
    }
}

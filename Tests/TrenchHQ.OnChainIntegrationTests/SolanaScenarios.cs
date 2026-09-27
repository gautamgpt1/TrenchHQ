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
    static async Task VerifyStandardWebSocketTransactionFetchAsync()
    {
        var outerData = new byte[] { 1, 2, 3, 4 };
        var innerData = new byte[] { 5, 6, 7, 8 };
        AssertEqual(Convert.ToHexString(outerData), Convert.ToHexString(SolanaBase58.Decode(EncodeBase58(outerData))),
            "The production base58 decoder did not round-trip instruction bytes.");
        try
        {
            SolanaBase58.Decode("0");
            throw new InvalidOperationException("Invalid base58 input was accepted.");
        }
        catch (FormatException)
        {
        }

        using var handler = new TransactionRpcFixtureHandler(outerData, innerData);
        using var httpClient = new HttpClient(handler);
        var configuration = CreateProviderConfiguration(
            OnChainProviderTypes.AlchemyWebSocket,
            "wss://solana-mainnet.streaming.alchemy.com/v2",
            "https://solana-mainnet.g.alchemy.com/v2");
        var rpc = new SolanaRpcClient(httpClient, configuration, "free-tier-key");
        var transaction = await rpc.GetTransactionAsync(
            "fixture-signature",
            OnChainCommitment.Processed,
            "websocket-fixture",
            1234,
            CancellationToken.None);
        Assert(transaction != null, "The standard-WebSocket transaction lookup returned no transaction.");
        AssertEqual(OnChainCommitment.Confirmed, transaction!.Commitment,
            "A processed WebSocket log was not safely fetched at confirmed commitment.");
        AssertEqual(2, transaction.Instructions.Length,
            "The standard RPC transaction path did not preserve outer and inner instructions.");
        AssertEqual(Convert.ToBase64String(outerData), transaction.Instructions[0].DataBase64,
            "Outer instruction data was not converted from Solana base58 correctly.");
        AssertEqual(Convert.ToBase64String(innerData), transaction.Instructions[1].DataBase64,
            "Inner instruction data was not converted from Solana base58 correctly.");
        AssertEqual("loaded-program", transaction.Instructions[1].ProgramId,
            "A loaded-address program index was not resolved.");
        AssertEqual((ushort)0, transaction.Instructions[1].OuterInstructionIndex,
            "The inner instruction parent index changed.");
        AssertEqual((ushort)0, transaction.Instructions[1].InnerInstructionIndex,
            "The inner instruction index changed.");
        AssertEqual(2U, transaction.Instructions[1].StackHeight,
            "The standard RPC path lost CPI stack height.");
        AssertEqual("base-vault", transaction.Instructions[0].AccountAddresses[0],
            "The standard RPC path did not resolve an instruction account index.");
        AssertEqual("quote-vault", transaction.Instructions[0].AccountAddresses[1],
            "The standard RPC path did not resolve a loaded instruction account index.");
        AssertEqual(2, transaction.TokenBalances.Length,
            "The standard RPC path did not normalize changed vault balances.");
        AssertEqual(3, transaction.ProgramData.Length,
            "The standard RPC path did not preserve program-attributed log data.");
        AssertEqual("outer-program", transaction.ProgramData[0].ProgramId,
            "The outer program-data payload was attributed incorrectly.");
        AssertEqual("loaded-program", transaction.ProgramData[1].ProgramId,
            "The nested program-data payload was attributed incorrectly.");
        AssertEqual(3U, transaction.ProgramData[1].LogIndex,
            "The nested program-data log index changed.");
        AssertEqual("outer-program", transaction.ProgramData[2].ProgramId,
            "The outer program was not restored after its CPI completed.");
        AssertEqual(900UL,
            transaction.TokenBalances.Single(balance => balance.AccountAddress == "base-vault").PostAmountRaw,
            "The standard RPC path returned the wrong post-swap base-vault amount.");
        AssertEqual(2200UL,
            transaction.TokenBalances.Single(balance => balance.AccountAddress == "quote-vault").PostAmountRaw,
            "The standard RPC path returned the wrong post-swap quote-vault amount.");
        AssertEqual("/v2/free-tier-key", handler.RequestUri?.AbsolutePath,
            "The WebSocket provider's HTTP transaction lookup did not authenticate transiently.");

        _ = new SolanaWebSocketStreamSource(configuration, "free-tier-key");
        try
        {
            _ = new SolanaWebSocketStreamSource(
                CreateProviderConfiguration(
                    OnChainProviderTypes.AlchemyYellowstone,
                    "https://solana-mainnet.streaming.alchemy.com",
                    "https://solana-mainnet.g.alchemy.com/v2"),
                "key");
            throw new InvalidOperationException("A Yellowstone configuration was accepted by the WebSocket source.");
        }
        catch (ArgumentException)
        {
        }
    }
    static async Task VerifySolanaWalletTransactionFetchAsync()
    {
        const string walletAddress = "11111111111111111111111111111111";
        using var handler = new WalletTransactionRpcFixtureHandler(walletAddress);
        using var httpClient = new HttpClient(handler);
        var configuration = CreateProviderConfiguration(
            OnChainProviderTypes.AlchemyWebSocket,
            "wss://solana-mainnet.streaming.alchemy.com/v2",
            "https://solana-mainnet.g.alchemy.com/v2");
        var rpc = new SolanaRpcClient(httpClient, configuration, "free-tier-key");
        var transaction = await rpc.GetWalletTransactionAsync(
            "wallet-fixture-signature",
            CancellationToken.None);
        Assert(transaction != null, "The Solana wallet transaction lookup returned no transaction.");
        AssertEqual(3, transaction!.AccountAddresses.Length,
            "The Solana wallet transaction lost account balance positions.");
        AssertEqual(2, transaction.TokenBalances.Length,
            "The Solana wallet transaction did not retain created and existing token accounts.");
        AssertEqual(9, transaction.TokenBalances.Single(balance =>
            balance.Mint == "TokenMint111111111111111111111111111111111").Decimals,
            "The Solana wallet transaction lost token decimals.");

        var updates = WalletActivityRules.ParseSolanaTransaction(
            transaction,
            new SavedTrackedWallet
            {
                ChainNamespace = ChainNamespaces.Solana,
                ChainId = "mainnet-beta",
                Address = walletAddress,
                Label = "Trader"
            },
            "confirmed",
            1700000000000,
            "wallet-fixture");
        var presentation = WalletActivityPresentationRules.Build(updates);
        AssertEqual(WalletActivityDisplayKind.Buy, presentation.Kind,
            "The parsed Solana wallet deltas did not classify the fixture trade as a buy.");
        AssertEqual("12.5 USDC → 2 TokenM…1111", presentation.Detail,
            "The parsed Solana wallet deltas produced the wrong trade flow.");
        Assert(!updates.Any(static update => update.AssetAddress == null),
            "The Solana fee and new-account rent were exposed as a false SOL movement.");
        Assert(handler.RequestBody.Contains("getTransaction", StringComparison.Ordinal)
               && handler.RequestBody.Contains("maxSupportedTransactionVersion", StringComparison.Ordinal),
            "The wallet lookup did not request a bounded versioned transaction response.");
    }
    static async Task VerifyWalletSignatureHistoryAsync()
    {
        using var handler = new WalletSignatureRpcFixtureHandler();
        using var httpClient = new HttpClient(handler);
        var configuration = CreateProviderConfiguration(
            OnChainProviderTypes.AlchemyWebSocket,
            "wss://solana-mainnet.streaming.alchemy.com/v2",
            "https://solana-mainnet.g.alchemy.com/v2");
        var rpc = new SolanaRpcClient(httpClient, configuration, "free-tier-key");

        var signatures = await rpc.GetSignaturesForAddressAsync(
            "11111111111111111111111111111111",
            "previous-signature",
            10,
            CancellationToken.None);

        AssertEqual(2, signatures.Count, "Wallet signature recovery returned the wrong number of entries.");
        AssertEqual("new-signature", signatures[0].Signature,
            "Wallet signature recovery changed newest-first RPC ordering.");
        Assert(!signatures[0].Failed, "A successful wallet transaction was marked failed.");
        Assert(signatures[1].Failed, "A failed wallet transaction was not preserved.");
        Assert(handler.RequestBody.Contains("getSignaturesForAddress", StringComparison.Ordinal)
               && handler.RequestBody.Contains("previous-signature", StringComparison.Ordinal),
            "Wallet signature recovery did not send the expected bounded until cursor.");
    }
    static async Task VerifyWalletCompletionRpcMethodsAsync()
    {
        using var solanaHandler = new WalletCompletionSolanaRpcFixtureHandler();
        using var solanaHttpClient = new HttpClient(solanaHandler);
        var solanaConfiguration = CreateProviderConfiguration(
            OnChainProviderTypes.AlchemyWebSocket,
            "wss://solana-mainnet.streaming.alchemy.com/v2",
            "https://solana-mainnet.g.alchemy.com/v2");
        var solanaRpc = new SolanaRpcClient(solanaHttpClient, solanaConfiguration, "free-tier-key");
        var tokenAccounts = await solanaRpc.GetTokenAccountAddressesByOwnerAsync(
            "11111111111111111111111111111111",
            "TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA",
            OnChainCommitment.Confirmed,
            CancellationToken.None);
        AssertEqual(2, tokenAccounts.Count,
            "Solana wallet token-account discovery returned the wrong count.");
        var statuses = await solanaRpc.GetSignatureStatusesAsync(
            ["processed-signature", "finalized-signature"],
            CancellationToken.None);
        AssertEqual("confirmed", statuses[0]?.Confirmation,
            "Solana signature status did not preserve confirmation progression.");
        AssertEqual("finalized", statuses[1]?.Confirmation,
            "Solana signature status did not preserve finality.");

        using var evmHandler = new WalletCompletionEvmRpcFixtureHandler();
        using var evmHttpClient = new HttpClient(evmHandler);
        var evmConfiguration = CreateProviderConfiguration(
            OnChainProviderTypes.AlchemyBase,
            "wss://base-mainnet.g.alchemy.com/v2",
            "https://base-mainnet.g.alchemy.com/v2");
        var evmRpc = new EvmJsonRpcClient(evmHttpClient, evmConfiguration, "free-tier-key");
        var block = await evmRpc.GetWalletBlockAsync("0xc8", CancellationToken.None);
        AssertEqual(200UL, block.Number, "EVM wallet block parsing changed its block number.");
        AssertEqual(2, block.Transactions.Length,
            "EVM wallet block parsing did not return full transactions.");
        var traces = await evmRpc.GetAddressTracesAsync(
            199,
            200,
            ["0x1111111111111111111111111111111111111111"],
            true,
            CancellationToken.None);
        AssertEqual(1, traces.Count, "EVM internal trace parsing returned the wrong count.");
        AssertEqual("0.1", traces[0].TracePath, "EVM internal trace identity was not stable.");
        AssertEqual("0xc7", evmHandler.TraceFromBlock,
            "EVM trace recovery did not preserve the requested range start.");
        AssertEqual("0xc8", evmHandler.TraceToBlock,
            "EVM trace recovery did not preserve the requested range end.");
        Assert(evmHandler.SawFullTransactions,
            "EVM wallet block lookup did not request full transaction objects.");
    }
    static void VerifyYellowstoneNormalization()
    {
        var pool = EncodeBase58(Enumerable.Repeat((byte)8, 32).ToArray());
        var baseVault = EncodeBase58(Enumerable.Repeat((byte)4, 32).ToArray());
        var quoteVault = EncodeBase58(Enumerable.Repeat((byte)5, 32).ToArray());
        var subscription = new OnChainStreamSubscription
        {
            Commitment = OnChainCommitment.Confirmed,
            FromSlot = 77,
            Pools =
            [
                new OnChainWatchedPoolSelection
                {
                    SelectedMint = EncodeBase58(Enumerable.Repeat((byte)3, 32).ToArray()),
                    Descriptor = new OnChainPoolDescriptor
                    {
                        PoolKey = new OnChainPoolKey
                        {
                            ProtocolId = OnChainProtocolIds.PumpSwap,
                            PoolAddress = pool
                        },
                        BaseVault = baseVault,
                        QuoteVault = quoteVault
                    }
                }
            ]
        };
        var request = YellowstoneStreamSource.BuildRequest(subscription);
        AssertEqual(CommitmentLevel.Confirmed, request.Commitment,
            "Yellowstone commitment was not mapped.");
        Assert(request.HasFromSlot && request.FromSlot == 77,
            "Yellowstone replay slot was not included.");
        AssertEqual(3, request.Accounts["watched-pool-state"].Account.Count,
            "Yellowstone did not subscribe to the pool and both vaults.");
        AssertEqual(pool, request.Transactions["watched-pool-transactions"].AccountInclude.Single(),
            "Yellowstone transaction filter did not preserve exact pool identity.");

        var pumpProgramBytes = DecodeBase58("6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P");
        var pumpSwapProgramBytes = DecodeBase58("pAMMBay6oceH9fJKBRHGP5D4bD4sWpmSwMn52FMfXEA");
        var transaction = new TrenchHQ.Yellowstone.SolanaStorage.Transaction
        {
            Message = new TrenchHQ.Yellowstone.SolanaStorage.Message()
        };
        transaction.Signatures.Add(ByteString.CopyFrom(Enumerable.Repeat((byte)10, 64).ToArray()));
        transaction.Message.AccountKeys.Add(ByteString.CopyFrom(DecodeBase58(baseVault)));
        transaction.Message.AccountKeys.Add(ByteString.CopyFrom(pumpProgramBytes));
        transaction.Message.Instructions.Add(new TrenchHQ.Yellowstone.SolanaStorage.CompiledInstruction
        {
            ProgramIdIndex = 1,
            Data = ByteString.CopyFrom([1, 2, 3]),
            Accounts = ByteString.CopyFrom([0, 3])
        });
        var meta = new TrenchHQ.Yellowstone.SolanaStorage.TransactionStatusMeta();
        meta.LoadedWritableAddresses.Add(ByteString.CopyFrom(pumpSwapProgramBytes));
        meta.LoadedReadonlyAddresses.Add(ByteString.CopyFrom(DecodeBase58(quoteVault)));
        meta.PreTokenBalances.Add(CreateYellowstoneTokenBalance(0, "base-mint", 1000));
        meta.PostTokenBalances.Add(CreateYellowstoneTokenBalance(0, "base-mint", 900));
        meta.PreTokenBalances.Add(CreateYellowstoneTokenBalance(3, "quote-mint", 2000));
        meta.PostTokenBalances.Add(CreateYellowstoneTokenBalance(3, "quote-mint", 2200));
        var inner = new TrenchHQ.Yellowstone.SolanaStorage.InnerInstructions { Index = 0 };
        inner.Instructions.Add(new TrenchHQ.Yellowstone.SolanaStorage.InnerInstruction
        {
            ProgramIdIndex = 2,
            Data = ByteString.CopyFrom([4, 5, 6]),
            StackHeight = 2,
            Accounts = ByteString.CopyFrom([0, 3])
        });
        meta.InnerInstructions.Add(inner);
        meta.LogMessages.Add("Program 6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P invoke [1]");
        meta.LogMessages.Add("Program data: AQID");
        meta.LogMessages.Add("Program pAMMBay6oceH9fJKBRHGP5D4bD4sWpmSwMn52FMfXEA invoke [2]");
        meta.LogMessages.Add("Program data: BAUG");
        meta.LogMessages.Add("Program pAMMBay6oceH9fJKBRHGP5D4bD4sWpmSwMn52FMfXEA success");
        meta.LogMessages.Add("Program data: BwgJ");
        meta.LogMessages.Add("Program 6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P success");
        var update = new SubscribeUpdate
        {
            CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(500)),
            Transaction = new SubscribeUpdateTransaction
            {
                Slot = 88,
                Transaction = new SubscribeUpdateTransactionInfo
                {
                    Signature = ByteString.CopyFrom(Enumerable.Repeat((byte)10, 64).ToArray()),
                    Transaction = transaction,
                    Meta = meta
                }
            }
        };

        var normalized = YellowstoneStreamSource.Normalize(update, OnChainCommitment.Confirmed, "fixture")
                         as OnChainSourceTransactionUpdate
                         ?? throw new InvalidOperationException("Yellowstone transaction was not normalized.");
        AssertEqual(88UL, normalized.Update.Slot, "Normalized transaction slot changed.");
        AssertEqual(2, normalized.Update.Instructions.Length,
            "Outer and inner instructions were not both preserved.");
        AssertEqual("6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P",
            normalized.Update.Instructions[0].ProgramId,
            "Static program index was resolved incorrectly.");
        AssertEqual("pAMMBay6oceH9fJKBRHGP5D4bD4sWpmSwMn52FMfXEA",
            normalized.Update.Instructions[1].ProgramId,
            "Loaded program index was resolved incorrectly.");
        AssertEqual((ushort)0, normalized.Update.Instructions[1].OuterInstructionIndex,
            "Inner instruction parent index changed.");
        AssertEqual((ushort)0, normalized.Update.Instructions[1].InnerInstructionIndex,
            "Inner instruction index changed.");
        AssertEqual(2U, normalized.Update.Instructions[1].StackHeight,
            "Inner stack height was lost.");
        AssertEqual(baseVault, normalized.Update.Instructions[1].AccountAddresses[0],
            "Yellowstone did not resolve a static instruction account.");
        AssertEqual(quoteVault, normalized.Update.Instructions[1].AccountAddresses[1],
            "Yellowstone did not resolve a loaded instruction account.");
        AssertEqual(2, normalized.Update.TokenBalances.Length,
            "Yellowstone did not normalize changed transaction token balances.");
        AssertEqual(3, normalized.Update.ProgramData.Length,
            "Yellowstone did not preserve program-attributed log data.");
        AssertEqual("pAMMBay6oceH9fJKBRHGP5D4bD4sWpmSwMn52FMfXEA",
            normalized.Update.ProgramData[1].ProgramId,
            "Yellowstone attributed nested program data to the wrong program.");
        AssertEqual(3U, normalized.Update.ProgramData[1].LogIndex,
            "Yellowstone changed the program-data log index.");
        AssertEqual(2200UL,
            normalized.Update.TokenBalances.Single(balance => balance.AccountAddress == quoteVault).PostAmountRaw,
            "Yellowstone returned the wrong post-swap quote-vault amount.");
        AssertEqual(500L, normalized.Update.ObservedAtUnixMs,
            "Provider observation time was not preserved.");
    }
    static async Task VerifySolanaPriceSamplingAsync(string enginePath)
    {
        var standardPresets = OnChainProviderCatalog.Presets
            .Where(static preset => preset.StreamTransport == OnChainStreamTransport.SolanaWebSocket)
            .ToArray();
        AssertEqual(6, standardPresets.Length,
            "The standard Solana provider coverage changed without a sampling regression.");
        foreach (var preset in standardPresets)
        {
            var configuration = OnChainProviderConfigurationStore.CreateConfiguration(
                preset);
            Assert(OnChainStreamCoordinator.CreateSource(configuration, "fixture-key")
                   is SolanaRpcSampleStreamSource,
                "A Solana price-only route subscribed to every transaction event.");
        }

        var profile = OnChainProviderConfigurationStore.CreateConfiguration(
            OnChainProviderCatalog.Get(OnChainProviderTypes.AlchemyWebSocket));
        using var handler = new SolanaSampleRpcFixtureHandler();
        var source = new SolanaRpcSampleStreamSource(
            profile, "fixture-key", handler, TimeSpan.FromMilliseconds(100));
        var channel = Channel.CreateUnbounded<OnChainSourceUpdate>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var selection = new OnChainWatchedPoolSelection
        {
            Descriptor = new OnChainPoolDescriptor
            {
                PoolKey = new OnChainPoolKey { PoolAddress = "sample-pool" },
                BaseVault = "shared-vault",
                QuoteVault = "quote-vault",
                ProtocolAccounts = [new() { Role = "duplicate", Address = "shared-vault" }]
            }
        };
        var producer = source.RunAsync(new OnChainStreamSubscription
        {
            Pools = [selection],
            Commitment = OnChainCommitment.Confirmed
        }, channel.Writer, cts.Token);
        var update = await channel.Reader.ReadAsync(cts.Token);
        Assert(update is OnChainSourceAccountSnapshot,
            "Solana sampling did not publish one complete account snapshot.");
        var snapshot = (OnChainSourceAccountSnapshot)update;
        var updates = snapshot.Updates;
        await Task.Delay(250);
        AssertEqual(1, handler.RequestCount, "Solana sampling ran ahead of the engine consumer.");
        snapshot.Processed.TrySetResult();
        cts.Cancel();
        try
        {
            await producer;
        }
        catch (OperationCanceledException)
        {
        }
        AssertEqual(1, handler.RequestCount,
            "One Solana sampling tick made more than one batched RPC request.");
        AssertEqual(3, handler.LastAddresses.Length,
            "A shared Solana account was fetched more than once per sample.");
        Assert(updates.All(update => update.Slot == 123
                                     && update.WriteVersion == 0
                                     && update.Commitment == OnChainCommitment.Confirmed
                                     && update.SourceId.StartsWith("rpcSample:", StringComparison.Ordinal)),
            "Solana sampled account provenance or slot was lost.");

        var many = Enumerable.Range(0, 40).Select(index => new OnChainWatchedPoolSelection
        {
            Descriptor = new OnChainPoolDescriptor
            {
                PoolKey = new OnChainPoolKey { PoolAddress = $"pool-{index}" },
                BaseVault = $"base-{index}",
                QuoteVault = $"quote-{index}"
            }
        }).ToArray();
        var batches = SolanaRpcSampleStreamSource.BuildAccountBatches(many);
        AssertEqual(2, batches.Length, "Solana batches did not respect the 100-account limit.");
        Assert(batches.All(batch => batch.Length <= 100)
               && many.All(pool => batches.Any(batch =>
                   pool.Descriptor.EnumerateSolanaAccountAddresses().All(address => batch.Contains(address)))),
            "A pool was split between different Solana snapshot slots.");

        using var missingHandler = new SolanaSampleRpcFixtureHandler
        {
            MissingAddress = "quote-vault"
        };
        var missingSource = new SolanaRpcSampleStreamSource(
            profile, "fixture-key", missingHandler, TimeSpan.FromMilliseconds(20));
        var missingChannel = Channel.CreateUnbounded<OnChainSourceUpdate>();
        using var missingCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await missingSource.RunAsync(new OnChainStreamSubscription
            {
                Pools = [selection],
                Commitment = OnChainCommitment.Confirmed
            }, missingChannel.Writer, missingCts.Token);
            throw new InvalidOperationException("A missing selected pool account was accepted.");
        }
        catch (SolanaRpcException)
        {
        }
        AssertEqual(0, missingChannel.Reader.Count,
            "A partial Solana pool sample was published before the missing account failed.");

        var engine = new OnChainEngineClient(enginePath);
        var prices = new ConcurrentQueue<OnChainPriceUpdate>();
        engine.PriceUpdated += (_, args) => prices.Enqueue(args.Update);
        var mint = EncodeBase58(Enumerable.Repeat((byte)3, 32).ToArray());
        var quote = EncodeBase58(Enumerable.Repeat((byte)9, 32).ToArray());
        var pool = new OnChainWatchedPoolSelection
        {
            SelectedMint = mint,
            Descriptor = new OnChainPoolDescriptor
            {
                PoolKey = new OnChainPoolKey
                {
                    ProtocolId = OnChainProtocolIds.PumpBondingCurve,
                    PoolAddress = "atomic-sample-curve"
                },
                PoolType = "bondingCurve",
                ProgramId = "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P",
                BaseMint = mint,
                QuoteMint = quote,
                BaseDecimals = 6,
                QuoteDecimals = 9,
                SupportStatus = OnChainSupportStatus.Supported
            }
        };
        try
        {
            await engine.ReplaceWatchedPoolsAsync("atomic-sample", [pool]);
            await engine.PublishAccountSnapshotAsync([new OnChainRawAccountUpdate
            {
                Pubkey = pool.Descriptor.PoolKey.PoolAddress,
                OwnerProgram = pool.Descriptor.ProgramId,
                DataBase64 = Convert.ToBase64String(CreatePumpCurveAccount(
                    Enumerable.Repeat((byte)9, 32).ToArray())),
                Slot = 123,
                SourceId = "rpcSample:fixture",
                Commitment = OnChainCommitment.Confirmed
            }]);
            await WaitUntilAsync(() => prices.Any(price => price.SpotPriceQuote != null
                                                          && price.Slot == 123),
                TimeSpan.FromSeconds(5), "atomic Solana sample over real engine IPC");
        }
        finally
        {
            await engine.StopAsync();
        }
    }
}

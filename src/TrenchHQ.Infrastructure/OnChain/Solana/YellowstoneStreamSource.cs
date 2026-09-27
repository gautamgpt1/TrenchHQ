using TrenchHQ.Core.OnChain;
using TrenchHQ.Core.OnChain.Solana;
using TrenchHQ.Core.Providers;
using TrenchHQ.Infrastructure.Providers;
using TrenchHQ.Yellowstone;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TrenchHQ.Infrastructure.OnChain.Solana
{
    internal sealed class YellowstoneStreamSource : IOnChainStreamSource
    {
        private const int MaximumMessageBytes = 16 * 1024 * 1024;

        private readonly Uri _endpoint;
        private readonly IOnChainUsageScope? _usage;
        private readonly string _apiKey;
        private readonly string _sourceId;

        internal YellowstoneStreamSource(OnChainProviderConfiguration configuration, string apiKey)
        {
            if (!OnChainProviderConfigurationStore.IsSecureStreamEndpoint(
                    configuration.StreamEndpoint,
                    OnChainStreamTransport.YellowstoneGrpc))
            {
                throw new ArgumentException("The Yellowstone endpoint is not a credential-free HTTPS URL.", nameof(configuration));
            }
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ArgumentException("A provider API key is required.", nameof(apiKey));
            }

            _endpoint = new Uri(configuration.StreamEndpoint, UriKind.Absolute);
            _usage = configuration.Usage;
            _apiKey = apiKey.Trim();
            _sourceId = $"{configuration.ProviderType}:{configuration.Id}";
        }

        public async Task RunAsync(
            OnChainStreamSubscription subscription,
            ChannelWriter<OnChainSourceUpdate> output,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(subscription);
            ArgumentNullException.ThrowIfNull(output);
            if (subscription.Pools.Length == 0)
            {
                return;
            }

            using var handler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                KeepAlivePingDelay = TimeSpan.FromSeconds(20),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
            };
            using var channel = GrpcChannel.ForAddress(_endpoint, new GrpcChannelOptions
            {
                HttpHandler = handler,
                MaxReceiveMessageSize = MaximumMessageBytes,
                MaxSendMessageSize = MaximumMessageBytes,
                DisposeHttpClient = true
            });
            var client = new Geyser.GeyserClient(channel);
            var headers = new Metadata { { "x-token", _apiKey } };
            _usage?.EnsureAvailable();
            using var call = client.Subscribe(headers, cancellationToken: cancellationToken);
            try
            {
                await call.RequestStream.WriteAsync(BuildRequest(subscription), cancellationToken)
                    .ConfigureAwait(false);

                while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
                {
                    var update = call.ResponseStream.Current;
                    _usage?.Grpc(update.CalculateSize());
                    if (update.UpdateOneofCase == SubscribeUpdate.UpdateOneofOneofCase.Ping)
                    {
                        await call.RequestStream.WriteAsync(
                            new SubscribeRequest { Ping = new SubscribeRequestPing { Id = 1 } },
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var normalized = Normalize(update, subscription.Commitment, _sourceId);
                    if (normalized != null)
                    {
                        await output.WriteAsync(normalized, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (RpcException exception) when (OnChainProviderUsage.IsQuotaMessage(exception.Status.Detail))
            {
                _usage?.QuotaExceeded();
                throw;
            }
        }

        internal static SubscribeRequest BuildRequest(OnChainStreamSubscription subscription)
        {
            var accountAddresses = subscription.Pools
                .SelectMany(static pool => pool.Descriptor.EnumerateSolanaAccountAddresses())
                .Where(static address => !string.IsNullOrWhiteSpace(address))
                .Select(static address => address!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var poolAddresses = subscription.Pools
                .Select(static pool => pool.Descriptor.PoolKey.PoolAddress)
                .Where(static address => !string.IsNullOrWhiteSpace(address))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var request = new SubscribeRequest
            {
                Commitment = ToYellowstoneCommitment(subscription.Commitment)
            };
            request.Accounts.Add("watched-pool-state", new SubscribeRequestFilterAccounts
            {
                Account = { accountAddresses }
            });
            request.Transactions.Add("watched-pool-transactions", new SubscribeRequestFilterTransactions
            {
                Vote = false,
                Failed = false,
                AccountInclude = { poolAddresses }
            });
            request.Slots.Add("slot-status", new SubscribeRequestFilterSlots
            {
                FilterByCommitment = false,
                // Upstream also gates SLOT_DEAD behind this flag; fork recovery needs it.
                InterslotUpdates = true
            });
            if (subscription.FromSlot.HasValue)
            {
                request.FromSlot = subscription.FromSlot.Value;
            }
            return request;
        }

        internal static OnChainSourceUpdate? Normalize(
            SubscribeUpdate update,
            OnChainCommitment commitment,
            string sourceId)
        {
            var observedAt = update.CreatedAt == null
                ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                : update.CreatedAt.ToDateTimeOffset().ToUnixTimeMilliseconds();
            return update.UpdateOneofCase switch
            {
                SubscribeUpdate.UpdateOneofOneofCase.Account => NormalizeAccount(
                    update.Account,
                    commitment,
                    sourceId,
                    observedAt),
                SubscribeUpdate.UpdateOneofOneofCase.Transaction => NormalizeTransaction(
                    update.Transaction,
                    commitment,
                    sourceId,
                    observedAt),
                SubscribeUpdate.UpdateOneofOneofCase.Slot => new OnChainSourceSlotUpdate(
                    update.Slot.Slot,
                    update.Slot.HasParent ? update.Slot.Parent : null,
                    ToSlotStatus(update.Slot.Status),
                    observedAt),
                _ => null
            };
        }

        private static OnChainSourceAccountUpdate NormalizeAccount(
            SubscribeUpdateAccount update,
            OnChainCommitment commitment,
            string sourceId,
            long observedAt)
        {
            if (update.Account == null)
            {
                throw new InvalidOperationException("Yellowstone account update omitted account data.");
            }
            return new OnChainSourceAccountUpdate(new OnChainRawAccountUpdate
            {
                Pubkey = EncodeAddress(update.Account.Pubkey, "account public key"),
                OwnerProgram = EncodeAddress(update.Account.Owner, "account owner"),
                DataBase64 = Convert.ToBase64String(update.Account.Data.Span),
                Slot = update.Slot,
                WriteVersion = update.Account.WriteVersion,
                Commitment = commitment,
                SourceId = sourceId,
                ObservedAtUnixMs = observedAt
            });
        }

        private static OnChainSourceTransactionUpdate NormalizeTransaction(
            SubscribeUpdateTransaction update,
            OnChainCommitment commitment,
            string sourceId,
            long observedAt)
        {
            var info = update.Transaction
                       ?? throw new InvalidOperationException("Yellowstone transaction update omitted transaction data.");
            var transaction = info.Transaction
                              ?? throw new InvalidOperationException("Yellowstone transaction payload is missing.");
            var message = transaction.Message
                          ?? throw new InvalidOperationException("Yellowstone transaction message is missing.");
            var accountKeys = message.AccountKeys
                .Concat(info.Meta?.LoadedWritableAddresses ?? [])
                .Concat(info.Meta?.LoadedReadonlyAddresses ?? [])
                .Select(static bytes => EncodeAddress(bytes, "transaction account key"))
                .ToArray();
            var instructions = new List<OnChainRawInstruction>();
            for (var index = 0; index < message.Instructions.Count; index++)
            {
                var instruction = message.Instructions[index];
                instructions.Add(new OnChainRawInstruction
                {
                    ProgramId = ResolveProgram(accountKeys, instruction.ProgramIdIndex),
                    DataBase64 = Convert.ToBase64String(instruction.Data.Span),
                    OuterInstructionIndex = checked((ushort)index),
                    AccountAddresses = ResolveAccounts(accountKeys, instruction.Accounts.Span)
                });
            }
            if (info.Meta != null)
            {
                foreach (var innerGroup in info.Meta.InnerInstructions)
                {
                    for (var index = 0; index < innerGroup.Instructions.Count; index++)
                    {
                        var instruction = innerGroup.Instructions[index];
                        instructions.Add(new OnChainRawInstruction
                        {
                            ProgramId = ResolveProgram(accountKeys, instruction.ProgramIdIndex),
                            DataBase64 = Convert.ToBase64String(instruction.Data.Span),
                            OuterInstructionIndex = checked((ushort)innerGroup.Index),
                            InnerInstructionIndex = checked((ushort)index),
                            StackHeight = instruction.HasStackHeight ? instruction.StackHeight : null,
                            AccountAddresses = ResolveAccounts(accountKeys, instruction.Accounts.Span)
                        });
                    }
                }
            }

            return new OnChainSourceTransactionUpdate(new OnChainRawTransactionUpdate
            {
                Signature = EncodeSignature(info.Signature, transaction.Signatures.FirstOrDefault()),
                Slot = update.Slot,
                Failed = info.Meta?.Err != null,
                Commitment = commitment,
                SourceId = sourceId,
                ObservedAtUnixMs = observedAt,
                Instructions = instructions.ToArray(),
                ProgramData = SolanaProgramDataLogParser.Parse(info.Meta?.LogMessages ?? []),
                TokenBalances = ReadTokenBalances(info.Meta, accountKeys)
            });
        }

        private static string EncodeSignature(ByteString primary, ByteString? fallback)
        {
            var bytes = primary.Length > 0 ? primary : fallback;
            if (bytes == null || bytes.Length != 64)
            {
                throw new InvalidOperationException("Yellowstone transaction signature is not 64 bytes.");
            }
            return SolanaBase58.Encode(bytes.Span);
        }

        private static string EncodeAddress(ByteString bytes, string field)
        {
            if (bytes.Length != 32)
            {
                throw new InvalidOperationException($"Yellowstone {field} is not 32 bytes.");
            }
            return SolanaBase58.Encode(bytes.Span);
        }

        private static string ResolveProgram(IReadOnlyList<string> accountKeys, uint index)
        {
            return index < accountKeys.Count
                ? accountKeys[(int)index]
                : throw new InvalidOperationException("Yellowstone instruction program index is out of range.");
        }

        private static string[] ResolveAccounts(IReadOnlyList<string> accountKeys, ReadOnlySpan<byte> indexes)
        {
            var addresses = new string[indexes.Length];
            for (var index = 0; index < indexes.Length; index++)
            {
                var accountIndex = indexes[index];
                addresses[index] = accountIndex < accountKeys.Count
                    ? accountKeys[accountIndex]
                    : throw new InvalidOperationException("Yellowstone instruction account index is invalid.");
            }
            return addresses;
        }

        private static OnChainRawTokenBalance[] ReadTokenBalances(
            TrenchHQ.Yellowstone.SolanaStorage.TransactionStatusMeta? meta,
            IReadOnlyList<string> accountKeys)
        {
            if (meta == null)
            {
                return [];
            }
            var pre = meta.PreTokenBalances.ToDictionary(
                static balance => balance.AccountIndex,
                static balance => balance);
            return meta.PostTokenBalances
                .Where(balance => pre.ContainsKey(balance.AccountIndex))
                .Select(balance =>
                {
                    var before = pre[balance.AccountIndex];
                    if (balance.AccountIndex >= accountKeys.Count
                        || !string.Equals(before.Mint, balance.Mint, StringComparison.Ordinal)
                        || !ulong.TryParse(before.UiTokenAmount?.Amount, out var preAmount)
                        || !ulong.TryParse(balance.UiTokenAmount?.Amount, out var postAmount))
                    {
                        throw new InvalidOperationException("Yellowstone token balance is invalid.");
                    }
                    return new OnChainRawTokenBalance
                    {
                        AccountAddress = accountKeys[checked((int)balance.AccountIndex)],
                        Mint = balance.Mint,
                        PreAmountRaw = preAmount,
                        PostAmountRaw = postAmount
                    };
                })
                .Where(static balance => balance.PreAmountRaw != balance.PostAmountRaw)
                .ToArray();
        }

        private static CommitmentLevel ToYellowstoneCommitment(OnChainCommitment commitment)
        {
            return commitment switch
            {
                OnChainCommitment.Processed => CommitmentLevel.Processed,
                OnChainCommitment.Confirmed => CommitmentLevel.Confirmed,
                OnChainCommitment.Finalized => CommitmentLevel.Finalized,
                _ => throw new ArgumentOutOfRangeException(nameof(commitment))
            };
        }

        private static OnChainSlotStatus ToSlotStatus(SlotStatus status)
        {
            return status switch
            {
                SlotStatus.SlotProcessed => OnChainSlotStatus.Processed,
                SlotStatus.SlotConfirmed => OnChainSlotStatus.Confirmed,
                SlotStatus.SlotFinalized => OnChainSlotStatus.Finalized,
                SlotStatus.SlotFirstShredReceived => OnChainSlotStatus.FirstShredReceived,
                SlotStatus.SlotCompleted => OnChainSlotStatus.Completed,
                SlotStatus.SlotCreatedBank => OnChainSlotStatus.CreatedBank,
                SlotStatus.SlotDead => OnChainSlotStatus.Dead,
                _ => throw new ArgumentOutOfRangeException(nameof(status))
            };
        }
    }
}

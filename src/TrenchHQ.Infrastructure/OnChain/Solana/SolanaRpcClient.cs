using TrenchHQ.Core.OnChain;
using TrenchHQ.Core.OnChain.Solana;
using TrenchHQ.Core.Providers;
using TrenchHQ.Core.Wallets;
using TrenchHQ.Infrastructure.Providers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TrenchHQ.Infrastructure.OnChain.Solana
{
    internal sealed class SolanaRpcException(
        string message,
        int? rpcCode = null,
        int? httpStatusCode = null,
        TimeSpan? retryAfter = null) : Exception(message)
    {
        internal TimeSpan? RetryAfter { get; } = retryAfter;
        internal int? RpcCode { get; } = rpcCode;
        internal int? HttpStatusCode { get; } = httpStatusCode;
        internal bool IsRateLimited => RpcCode is 402 or 429 || HttpStatusCode is 402 or 429;
        internal bool IsAccessRejected => HttpStatusCode is 401 or 403;
    }

    internal sealed class SolanaAccountSnapshot
    {
        internal string Address { get; init; } = string.Empty;
        internal string OwnerProgram { get; init; } = string.Empty;
        internal string DataBase64 { get; init; } = string.Empty;
        internal ulong Lamports { get; init; }
        internal ulong Slot { get; init; }
    }

    internal sealed class SolanaRpcClient
    {
        private const int MaximumResponseBytes = 8 * 1024 * 1024;
        private const int MaximumMultipleAccounts = 100;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly HttpClient _httpClient;
        private readonly IOnChainUsageScope? _usage;
        private readonly Uri _endpoint;
        private readonly string? _apiKey;
        private readonly OnChainEndpointAuthenticationMode _authenticationMode;
        private int _nextRequestId;

        internal SolanaRpcClient(
            HttpClient httpClient,
            OnChainProviderConfiguration configuration,
            string? apiKey)
        {
            ArgumentNullException.ThrowIfNull(httpClient);
            ArgumentNullException.ThrowIfNull(configuration);
            if (!Uri.TryCreate(configuration.RpcEndpoint, UriKind.Absolute, out var endpointUri)
                || endpointUri.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(endpointUri.UserInfo)
                || !string.IsNullOrEmpty(endpointUri.Query)
                || !string.IsNullOrEmpty(endpointUri.Fragment))
            {
                throw new ArgumentException(
                    "The Solana RPC endpoint must be an HTTPS URL without credentials, query, or fragment.",
                    nameof(configuration));
            }

            _httpClient = httpClient;
            _usage = configuration.Usage;
            _endpoint = endpointUri;
            _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
            _authenticationMode = OnChainProviderCatalog.Get(configuration.ProviderType).RpcAuthenticationMode;
            if (_authenticationMode != OnChainEndpointAuthenticationMode.None && _apiKey == null)
            {
                throw new ArgumentException("This provider requires an API key.", nameof(apiKey));
            }
        }

        internal SolanaRpcClient(HttpClient httpClient, string endpoint)
        {
            ArgumentNullException.ThrowIfNull(httpClient);
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
                || endpointUri.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(endpointUri.UserInfo)
                || !string.IsNullOrEmpty(endpointUri.Query)
                || !string.IsNullOrEmpty(endpointUri.Fragment))
            {
                throw new ArgumentException(
                    "The Solana RPC endpoint must be an HTTPS URL without credentials, query, or fragment.",
                    nameof(endpoint));
            }

            _httpClient = httpClient;
            _endpoint = endpointUri;
            _apiKey = null;
            _authenticationMode = OnChainEndpointAuthenticationMode.None;
        }

        internal async Task<SolanaAccountSnapshot?> GetAccountInfoAsync(
            string address,
            OnChainCommitment commitment,
            CancellationToken cancellationToken)
        {
            var result = await SendAsync(
                "getAccountInfo",
                new object[]
                {
                    address,
                    new
                    {
                        encoding = "base64",
                        commitment = ToRpcCommitment(commitment)
                    }
                },
                cancellationToken).ConfigureAwait(false);
            var slot = ReadContextSlot(result);
            var value = result.GetProperty("value");
            return value.ValueKind == JsonValueKind.Null
                ? null
                : ParseAccount(address, value, slot);
        }

        internal async Task<IReadOnlyList<string>> GetProgramAccountAddressesAsync(
            string programId,
            int dataSize,
            int memcmpOffset,
            string memcmpBytes,
            OnChainCommitment commitment,
            CancellationToken cancellationToken)
        {
            var result = await SendAsync(
                "getProgramAccounts",
                new object[]
                {
                    programId,
                    new
                    {
                        encoding = "base64",
                        commitment = ToRpcCommitment(commitment),
                        withContext = true,
                        dataSlice = new { offset = 0, length = 0 },
                        filters = new object[]
                        {
                            new { dataSize },
                            new { memcmp = new { offset = memcmpOffset, bytes = memcmpBytes } }
                        }
                    }
                },
                cancellationToken).ConfigureAwait(false);

            var addresses = new List<string>();
            foreach (var item in result.GetProperty("value").EnumerateArray())
            {
                var address = item.GetProperty("pubkey").GetString();
                if (!string.IsNullOrWhiteSpace(address))
                {
                    addresses.Add(address);
                }
            }
            return addresses;
        }

        internal async Task<IReadOnlyList<SolanaAccountSnapshot?>> GetMultipleAccountsAsync(
            IReadOnlyList<string> addresses,
            OnChainCommitment commitment,
            CancellationToken cancellationToken)
        {
            if (addresses.Count == 0)
            {
                return [];
            }
            if (addresses.Count > MaximumMultipleAccounts)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(addresses),
                    $"At most {MaximumMultipleAccounts} accounts can be requested at once.");
            }

            var result = await SendAsync(
                "getMultipleAccounts",
                new object[]
                {
                    addresses,
                    new
                    {
                        encoding = "base64",
                        commitment = ToRpcCommitment(commitment)
                    }
                },
                cancellationToken).ConfigureAwait(false);
            var slot = ReadContextSlot(result);
            var values = result.GetProperty("value").EnumerateArray().ToArray();
            if (values.Length != addresses.Count)
            {
                throw new SolanaRpcException("The RPC returned a mismatched multiple-account result.");
            }

            var accounts = new SolanaAccountSnapshot?[values.Length];
            for (var index = 0; index < values.Length; index++)
            {
                accounts[index] = values[index].ValueKind == JsonValueKind.Null
                    ? null
                    : ParseAccount(addresses[index], values[index], slot);
            }
            return accounts;
        }

        internal async Task<ulong> GetSlotAsync(
            OnChainCommitment commitment,
            CancellationToken cancellationToken)
        {
            var result = await SendAsync(
                "getSlot",
                new object[] { new { commitment = ToRpcCommitment(commitment) } },
                cancellationToken).ConfigureAwait(false);
            return result.GetUInt64();
        }

        internal async Task<IReadOnlyList<string>> GetTokenAccountAddressesByOwnerAsync(
            string ownerAddress,
            string tokenProgramId,
            OnChainCommitment commitment,
            CancellationToken cancellationToken)
        {
            if (SolanaBase58.Decode(ownerAddress).Length != 32)
            {
                throw new ArgumentException("The Solana wallet address is invalid.", nameof(ownerAddress));
            }
            if (SolanaBase58.Decode(tokenProgramId).Length != 32)
            {
                throw new ArgumentException("The Solana token program address is invalid.", nameof(tokenProgramId));
            }
            var result = await SendAsync(
                "getTokenAccountsByOwner",
                new object[]
                {
                    ownerAddress,
                    new { programId = tokenProgramId },
                    new
                    {
                        commitment = ToRpcCommitment(commitment),
                        encoding = "base64",
                        dataSlice = new { offset = 0, length = 0 }
                    }
                },
                cancellationToken).ConfigureAwait(false);
            if (!result.TryGetProperty("value", out var value)
                || value.ValueKind != JsonValueKind.Array)
            {
                throw new SolanaRpcException("getTokenAccountsByOwner returned an invalid result.");
            }
            var addresses = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                if (!item.TryGetProperty("pubkey", out var address)
                    || address.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(address.GetString()))
                {
                    throw new SolanaRpcException("getTokenAccountsByOwner returned an invalid entry.");
                }
                addresses.Add(address.GetString()!);
            }
            return addresses;
        }

        internal async Task<IReadOnlyList<SolanaWalletSignature>> GetSignaturesForAddressAsync(
            string address,
            string? until,
            int limit,
            CancellationToken cancellationToken)
        {
            if (SolanaBase58.Decode(address).Length != 32)
            {
                throw new ArgumentException("The Solana wallet address is invalid.", nameof(address));
            }
            if (limit is < 1 or > 1000)
            {
                throw new ArgumentOutOfRangeException(nameof(limit));
            }

            var result = await SendAsync(
                "getSignaturesForAddress",
                new object[]
                {
                    address,
                    new
                    {
                        commitment = "confirmed",
                        until,
                        limit
                    }
                },
                cancellationToken).ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.Array)
            {
                throw new SolanaRpcException("getSignaturesForAddress returned an invalid result.");
            }

            var signatures = new List<SolanaWalletSignature>();
            foreach (var item in result.EnumerateArray())
            {
                if (!item.TryGetProperty("signature", out var signature)
                    || signature.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(signature.GetString())
                    || !item.TryGetProperty("slot", out var slot)
                    || !slot.TryGetUInt64(out var slotValue))
                {
                    throw new SolanaRpcException("getSignaturesForAddress returned an invalid entry.");
                }
                signatures.Add(new SolanaWalletSignature
                {
                    Signature = signature.GetString()!,
                    Slot = slotValue,
                    Failed = item.TryGetProperty("err", out var error)
                             && error.ValueKind != JsonValueKind.Null,
                    BlockTimeUnixSeconds = item.TryGetProperty("blockTime", out var blockTime)
                                           && blockTime.ValueKind == JsonValueKind.Number
                                           && blockTime.TryGetInt64(out var timestamp)
                        ? timestamp
                        : null,
                    Confirmation = ReadConfirmation(item, "confirmed")
                });
            }
            return signatures;
        }

        internal async Task<IReadOnlyList<SolanaWalletSignatureStatus?>> GetSignatureStatusesAsync(
            IReadOnlyList<string> signatures,
            CancellationToken cancellationToken)
        {
            if (signatures.Count == 0)
            {
                return [];
            }
            if (signatures.Count > 256 || signatures.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentOutOfRangeException(nameof(signatures));
            }
            var result = await SendAsync(
                "getSignatureStatuses",
                new object[]
                {
                    signatures,
                    new { searchTransactionHistory = true }
                },
                cancellationToken).ConfigureAwait(false);
            if (!result.TryGetProperty("value", out var value)
                || value.ValueKind != JsonValueKind.Array)
            {
                throw new SolanaRpcException("getSignatureStatuses returned an invalid result.");
            }
            var entries = value.EnumerateArray().ToArray();
            if (entries.Length != signatures.Count)
            {
                throw new SolanaRpcException("getSignatureStatuses returned a mismatched result.");
            }
            var statuses = new SolanaWalletSignatureStatus?[entries.Length];
            for (var index = 0; index < entries.Length; index++)
            {
                var item = entries[index];
                if (item.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }
                if (!item.TryGetProperty("slot", out var slot)
                    || !slot.TryGetUInt64(out var slotValue))
                {
                    throw new SolanaRpcException("getSignatureStatuses returned an invalid entry.");
                }
                statuses[index] = new SolanaWalletSignatureStatus
                {
                    Signature = signatures[index],
                    Slot = slotValue,
                    Failed = item.TryGetProperty("err", out var error)
                             && error.ValueKind != JsonValueKind.Null,
                    Confirmation = ReadConfirmation(item, "confirmed")
                };
            }
            return statuses;
        }

        internal async Task<SolanaWalletTransaction?> GetWalletTransactionAsync(
            string signature,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(signature))
            {
                throw new ArgumentException("The Solana transaction signature is required.", nameof(signature));
            }

            var result = await SendAsync(
                "getTransaction",
                new object[]
                {
                    signature,
                    new
                    {
                        encoding = "json",
                        commitment = "confirmed",
                        maxSupportedTransactionVersion = 0
                    }
                },
                cancellationToken).ConfigureAwait(false);
            if (result.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            var transaction = result.GetProperty("transaction");
            var message = transaction.GetProperty("message");
            var meta = result.GetProperty("meta");
            var accountAddresses = ReadAccountKeys(message, meta);
            var preBalances = ReadLamportBalances(meta, "preBalances", accountAddresses.Length);
            var postBalances = ReadLamportBalances(meta, "postBalances", accountAddresses.Length);
            var actualSignature = transaction.GetProperty("signatures")[0].GetString();
            if (string.IsNullOrWhiteSpace(actualSignature))
            {
                throw new SolanaRpcException("The wallet transaction signature was missing.");
            }

            return new SolanaWalletTransaction
            {
                Signature = actualSignature,
                Slot = result.GetProperty("slot").GetUInt64(),
                Failed = meta.TryGetProperty("err", out var error)
                         && error.ValueKind != JsonValueKind.Null,
                FeeRaw = meta.GetProperty("fee").GetUInt64(),
                AccountAddresses = accountAddresses,
                PreBalancesRaw = preBalances,
                PostBalancesRaw = postBalances,
                TokenBalances = ReadWalletTokenBalances(meta, accountAddresses)
            };
        }

        internal async Task<OnChainRawTransactionUpdate?> GetTransactionAsync(
            string signature,
            OnChainCommitment commitment,
            string sourceId,
            long observedAtUnixMs,
            CancellationToken cancellationToken)
        {
            var effectiveCommitment = commitment == OnChainCommitment.Processed
                ? OnChainCommitment.Confirmed
                : commitment;
            var result = await SendAsync(
                "getTransaction",
                new object[]
                {
                    signature,
                    new
                    {
                        encoding = "json",
                        commitment = ToRpcCommitment(effectiveCommitment),
                        maxSupportedTransactionVersion = 0
                    }
                },
                cancellationToken).ConfigureAwait(false);
            if (result.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            var transaction = result.GetProperty("transaction");
            var message = transaction.GetProperty("message");
            var meta = result.GetProperty("meta");
            var accountKeys = ReadAccountKeys(message, meta);
            var instructions = new List<OnChainRawInstruction>();
            var outerInstructions = message.GetProperty("instructions").EnumerateArray().ToArray();
            for (var index = 0; index < outerInstructions.Length; index++)
            {
                instructions.Add(ParseInstruction(
                    outerInstructions[index],
                    accountKeys,
                    checked((ushort)index),
                    null));
            }
            if (meta.TryGetProperty("innerInstructions", out var innerGroups)
                && innerGroups.ValueKind == JsonValueKind.Array)
            {
                foreach (var group in innerGroups.EnumerateArray())
                {
                    var outerIndex = checked((ushort)group.GetProperty("index").GetInt32());
                    var innerInstructions = group.GetProperty("instructions").EnumerateArray().ToArray();
                    for (var index = 0; index < innerInstructions.Length; index++)
                    {
                        instructions.Add(ParseInstruction(
                            innerInstructions[index],
                            accountKeys,
                            outerIndex,
                            checked((ushort)index)));
                    }
                }
            }
            var tokenBalances = ReadTokenBalances(meta, accountKeys);
            var programData = meta.TryGetProperty("logMessages", out var logMessages)
                              && logMessages.ValueKind == JsonValueKind.Array
                ? SolanaProgramDataLogParser.Parse(logMessages.EnumerateArray()
                    .Select(static message => message.GetString() ?? string.Empty))
                : [];

            return new OnChainRawTransactionUpdate
            {
                Signature = transaction.GetProperty("signatures")[0].GetString()
                            ?? throw new SolanaRpcException("The transaction signature was missing."),
                Slot = result.GetProperty("slot").GetUInt64(),
                Failed = meta.TryGetProperty("err", out var error)
                         && error.ValueKind != JsonValueKind.Null,
                Commitment = effectiveCommitment,
                SourceId = sourceId,
                ObservedAtUnixMs = observedAtUnixMs,
                Instructions = instructions.ToArray(),
                ProgramData = programData,
                TokenBalances = tokenBalances
            };
        }

        private async Task<JsonElement> SendAsync(
            string method,
            object[] parameters,
            CancellationToken cancellationToken)
        {
            var requestId = Interlocked.Increment(ref _nextRequestId);
            var requestBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0",
                id = requestId,
                method,
                @params = parameters
            }, JsonOptions);

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildRequestUri())
            {
                Content = new ByteArrayContent(requestBytes)
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            cancellationToken.ThrowIfCancellationRequested();
            _usage?.Rpc(method);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            var retryAfter = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
            if ((int)response.StatusCode == 402)
            {
                _usage?.QuotaExceeded(response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow));
                throw new SolanaRpcException("The provider account quota is exhausted.", httpStatusCode: 402);
            }
            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            var responseBytes = await ReadBoundedAsync(responseStream, cancellationToken).ConfigureAwait(false);
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(responseBytes);
            }
            catch (JsonException) when (!response.IsSuccessStatusCode)
            {
                throw new SolanaRpcException(
                    $"The Solana RPC returned HTTP {(int)response.StatusCode}.",
                    httpStatusCode: (int)response.StatusCode, retryAfter: retryAfter);
            }
            using (document)
            {
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var codeElement)
                    ? codeElement.GetInt32()
                    : (int?)null;
                var message = error.TryGetProperty("message", out var messageElement)
                    ? Redact(messageElement.GetString())
                    : "Unknown RPC error";
                if (OnChainProviderUsage.IsQuotaError(error))
                    _usage?.QuotaExceeded(response.Headers.RetryAfter?.Delta
                        ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow));
                throw new SolanaRpcException(
                    $"Solana RPC error {code}: {message}",
                    code,
                    response.IsSuccessStatusCode ? null : (int)response.StatusCode, retryAfter);
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new SolanaRpcException(
                    $"The Solana RPC returned HTTP {(int)response.StatusCode}.",
                    httpStatusCode: (int)response.StatusCode, retryAfter: retryAfter);
            }
            if (!root.TryGetProperty("result", out var result))
            {
                throw new SolanaRpcException("The Solana RPC response did not contain a result.");
            }
            return result.Clone();
            }
        }

        private Uri BuildRequestUri()
        {
            return OnChainProviderEndpointBuilder.Build(_endpoint.AbsoluteUri, _apiKey, _authenticationMode);
        }

        private string Redact(string? message)
        {
            var bounded = string.IsNullOrWhiteSpace(message) ? "Unknown RPC error" : message.Trim();
            if (_apiKey != null)
            {
                bounded = bounded.Replace(_apiKey, "[redacted]", StringComparison.Ordinal);
            }
            return bounded.Length <= 300 ? bounded : bounded[..300];
        }

        private static SolanaAccountSnapshot ParseAccount(string address, JsonElement value, ulong slot)
        {
            var owner = value.GetProperty("owner").GetString();
            var data = value.GetProperty("data");
            if (string.IsNullOrWhiteSpace(owner)
                || data.ValueKind != JsonValueKind.Array
                || data.GetArrayLength() < 1
                || string.IsNullOrWhiteSpace(data[0].GetString()))
            {
                throw new SolanaRpcException("The Solana RPC returned an invalid account payload.");
            }

            return new SolanaAccountSnapshot
            {
                Address = address,
                OwnerProgram = owner,
                DataBase64 = data[0].GetString()!,
                Lamports = value.TryGetProperty("lamports", out var lamports)
                    ? lamports.GetUInt64()
                    : 0,
                Slot = slot
            };
        }

        private static string[] ReadAccountKeys(JsonElement message, JsonElement meta)
        {
            var keys = new List<string>();
            foreach (var key in message.GetProperty("accountKeys").EnumerateArray())
            {
                var value = key.ValueKind == JsonValueKind.String
                    ? key.GetString()
                    : key.TryGetProperty("pubkey", out var pubkey) ? pubkey.GetString() : null;
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new SolanaRpcException("The transaction contained an invalid account key.");
                }
                keys.Add(value);
            }
            if (meta.TryGetProperty("loadedAddresses", out var loaded)
                && loaded.ValueKind == JsonValueKind.Object)
            {
                AddLoadedAddresses(keys, loaded, "writable");
                AddLoadedAddresses(keys, loaded, "readonly");
            }
            return keys.ToArray();
        }

        private static void AddLoadedAddresses(List<string> keys, JsonElement loaded, string propertyName)
        {
            if (!loaded.TryGetProperty(propertyName, out var addresses)
                || addresses.ValueKind != JsonValueKind.Array)
            {
                return;
            }
            foreach (var address in addresses.EnumerateArray())
            {
                var value = address.GetString();
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new SolanaRpcException("The transaction contained an invalid loaded address.");
                }
                keys.Add(value);
            }
        }

        private static OnChainRawInstruction ParseInstruction(
            JsonElement instruction,
            IReadOnlyList<string> accountKeys,
            ushort outerInstructionIndex,
            ushort? innerInstructionIndex)
        {
            string programId;
            if (instruction.TryGetProperty("programIdIndex", out var programIndex))
            {
                var index = programIndex.GetInt32();
                programId = index >= 0 && index < accountKeys.Count
                    ? accountKeys[index]
                    : throw new SolanaRpcException("The transaction instruction program index was invalid.");
            }
            else if (instruction.TryGetProperty("programId", out var program))
            {
                programId = program.GetString()
                            ?? throw new SolanaRpcException("The transaction instruction program was missing.");
            }
            else
            {
                throw new SolanaRpcException("The transaction instruction did not identify a program.");
            }

            var encodedData = instruction.TryGetProperty("data", out var data)
                ? data.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(encodedData))
            {
                throw new SolanaRpcException("The transaction instruction data was missing.");
            }
            var decodedData = SolanaBase58.Decode(encodedData);
            return new OnChainRawInstruction
            {
                ProgramId = programId,
                DataBase64 = Convert.ToBase64String(decodedData),
                OuterInstructionIndex = outerInstructionIndex,
                InnerInstructionIndex = innerInstructionIndex,
                StackHeight = instruction.TryGetProperty("stackHeight", out var stackHeight)
                              && stackHeight.ValueKind == JsonValueKind.Number
                    ? stackHeight.GetUInt32()
                    : null,
                AccountAddresses = ReadInstructionAccounts(instruction, accountKeys)
            };
        }

        private static string[] ReadInstructionAccounts(
            JsonElement instruction,
            IReadOnlyList<string> accountKeys)
        {
            if (!instruction.TryGetProperty("accounts", out var accounts)
                || accounts.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            var resolved = new List<string>();
            foreach (var account in accounts.EnumerateArray())
            {
                if (account.ValueKind == JsonValueKind.String)
                {
                    var address = account.GetString();
                    if (!string.IsNullOrWhiteSpace(address))
                    {
                        resolved.Add(address);
                    }
                    continue;
                }
                if (account.ValueKind != JsonValueKind.Number)
                {
                    throw new SolanaRpcException("The transaction instruction contained an invalid account index.");
                }
                var index = account.GetInt32();
                if (index < 0 || index >= accountKeys.Count)
                {
                    throw new SolanaRpcException("The transaction instruction account index was invalid.");
                }
                resolved.Add(accountKeys[index]);
            }
            return resolved.ToArray();
        }

        private static OnChainRawTokenBalance[] ReadTokenBalances(
            JsonElement meta,
            IReadOnlyList<string> accountKeys)
        {
            var pre = ReadTokenBalanceSide(meta, "preTokenBalances", accountKeys);
            var post = ReadTokenBalanceSide(meta, "postTokenBalances", accountKeys);
            return pre.Keys
                .Intersect(post.Keys)
                .Select(index => new OnChainRawTokenBalance
                {
                    AccountAddress = accountKeys[index],
                    Mint = string.Equals(pre[index].Mint, post[index].Mint, StringComparison.Ordinal)
                        ? post[index].Mint
                        : throw new SolanaRpcException("A transaction token balance changed mint unexpectedly."),
                    PreAmountRaw = pre[index].Amount,
                    PostAmountRaw = post[index].Amount
                })
                .Where(static balance => balance.PreAmountRaw != balance.PostAmountRaw)
                .ToArray();
        }

        private static ulong[] ReadLamportBalances(
            JsonElement meta,
            string propertyName,
            int expectedCount)
        {
            if (!meta.TryGetProperty(propertyName, out var balances)
                || balances.ValueKind != JsonValueKind.Array)
            {
                throw new SolanaRpcException($"The wallet transaction did not include {propertyName}.");
            }
            var values = balances.EnumerateArray().Select(static balance => balance.GetUInt64()).ToArray();
            if (values.Length != expectedCount)
            {
                throw new SolanaRpcException($"The wallet transaction returned a mismatched {propertyName} array.");
            }
            return values;
        }

        private static SolanaWalletTokenBalance[] ReadWalletTokenBalances(
            JsonElement meta,
            IReadOnlyList<string> accountKeys)
        {
            var pre = ReadWalletTokenBalanceSide(meta, "preTokenBalances", accountKeys);
            var post = ReadWalletTokenBalanceSide(meta, "postTokenBalances", accountKeys);
            return pre.Keys
                .Union(post.Keys)
                .OrderBy(static index => index)
                .Select(index =>
                {
                    pre.TryGetValue(index, out var before);
                    post.TryGetValue(index, out var after);
                    if (before != null && after != null
                        && (!string.Equals(before.Mint, after.Mint, StringComparison.Ordinal)
                            || before.Decimals != after.Decimals
                            || !string.IsNullOrWhiteSpace(before.OwnerAddress)
                            && !string.IsNullOrWhiteSpace(after.OwnerAddress)
                            && !string.Equals(before.OwnerAddress, after.OwnerAddress, StringComparison.Ordinal)))
                    {
                        throw new SolanaRpcException("A wallet token balance changed identity unexpectedly.");
                    }
                    var identity = after ?? before!;
                    var owner = !string.IsNullOrWhiteSpace(after?.OwnerAddress)
                        ? after!.OwnerAddress
                        : before?.OwnerAddress ?? string.Empty;
                    return new SolanaWalletTokenBalance
                    {
                        AccountIndex = index,
                        AccountAddress = accountKeys[index],
                        Mint = identity.Mint,
                        OwnerAddress = owner,
                        Decimals = identity.Decimals,
                        PreAmountRaw = before?.AmountRaw ?? 0,
                        PostAmountRaw = after?.AmountRaw ?? 0
                    };
                })
                .ToArray();
        }

        private static Dictionary<int, SolanaWalletTokenBalanceSide> ReadWalletTokenBalanceSide(
            JsonElement meta,
            string propertyName,
            IReadOnlyList<string> accountKeys)
        {
            var balances = new Dictionary<int, SolanaWalletTokenBalanceSide>();
            if (!meta.TryGetProperty(propertyName, out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return balances;
            }
            foreach (var item in items.EnumerateArray())
            {
                var index = item.GetProperty("accountIndex").GetInt32();
                if (index < 0 || index >= accountKeys.Count)
                {
                    throw new SolanaRpcException("A wallet token balance account index was invalid.");
                }
                var mint = item.GetProperty("mint").GetString();
                var tokenAmount = item.GetProperty("uiTokenAmount");
                var amount = tokenAmount.GetProperty("amount").GetString();
                if (string.IsNullOrWhiteSpace(mint)
                    || !ulong.TryParse(amount, out var rawAmount)
                    || !tokenAmount.TryGetProperty("decimals", out var decimals)
                    || !decimals.TryGetByte(out var decimalValue))
                {
                    throw new SolanaRpcException("A wallet token balance was invalid.");
                }
                var owner = item.TryGetProperty("owner", out var ownerElement)
                            && ownerElement.ValueKind == JsonValueKind.String
                    ? ownerElement.GetString() ?? string.Empty
                    : string.Empty;
                balances[index] = new SolanaWalletTokenBalanceSide(mint, owner, decimalValue, rawAmount);
            }
            return balances;
        }

        private static Dictionary<int, (string Mint, ulong Amount)> ReadTokenBalanceSide(
            JsonElement meta,
            string propertyName,
            IReadOnlyList<string> accountKeys)
        {
            var balances = new Dictionary<int, (string Mint, ulong Amount)>();
            if (!meta.TryGetProperty(propertyName, out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return balances;
            }
            foreach (var item in items.EnumerateArray())
            {
                var index = item.GetProperty("accountIndex").GetInt32();
                if (index < 0 || index >= accountKeys.Count)
                {
                    throw new SolanaRpcException("A transaction token balance account index was invalid.");
                }
                var mint = item.GetProperty("mint").GetString();
                var amount = item.GetProperty("uiTokenAmount").GetProperty("amount").GetString();
                if (string.IsNullOrWhiteSpace(mint) || !ulong.TryParse(amount, out var rawAmount))
                {
                    throw new SolanaRpcException("A transaction token balance was invalid.");
                }
                balances[index] = (mint, rawAmount);
            }
            return balances;
        }

        private sealed record SolanaWalletTokenBalanceSide(
            string Mint,
            string OwnerAddress,
            byte Decimals,
            ulong AmountRaw);

        private static ulong ReadContextSlot(JsonElement result)
        {
            if (!result.TryGetProperty("context", out var context)
                || !context.TryGetProperty("slot", out var slot))
            {
                throw new SolanaRpcException("The Solana RPC response did not include a context slot.");
            }
            return slot.GetUInt64();
        }

        private static string ReadConfirmation(JsonElement item, string fallback)
        {
            if (!item.TryGetProperty("confirmationStatus", out var status)
                || status.ValueKind != JsonValueKind.String)
            {
                return fallback;
            }
            return status.GetString() switch
            {
                "processed" => "processed",
                "confirmed" => "confirmed",
                "finalized" => "finalized",
                _ => fallback
            };
        }

        private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
        {
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    return output.ToArray();
                }
                if (output.Length + count > MaximumResponseBytes)
                {
                    throw new SolanaRpcException("The Solana RPC response exceeded the local safety limit.");
                }
                output.Write(buffer, 0, count);
            }
        }

        private static string ToRpcCommitment(OnChainCommitment commitment)
        {
            return commitment switch
            {
                OnChainCommitment.Processed => "processed",
                OnChainCommitment.Confirmed => "confirmed",
                OnChainCommitment.Finalized => "finalized",
                _ => throw new ArgumentOutOfRangeException(nameof(commitment))
            };
        }
    }
}

using TrenchHQ.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TrenchHQ.Helpers
{
    internal sealed class OnChainPoolDiscoveryService
    {
        internal const string PublicMainnetRpcEndpoint = "https://api.mainnet.solana.com";

        private const string PumpProgramId = "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P";
        private const string PumpSwapProgramId = "pAMMBay6oceH9fJKBRHGP5D4bD4sWpmSwMn52FMfXEA";
        private const string RaydiumAmmV4ProgramId = "675kPX9MHTjS2zt1qfr1NYHuzeLXfQM9H24wFSUt1Mp8";
        private const string RaydiumCpmmProgramId = "CPMMoo8L3F4NbTegBCKVNunggL7H1ZpdTHKxQB5qKP1C";
        private const string RaydiumClmmProgramId = "CAMMCzo5YL8w4VFF8KVHrK22GGUsp5VTaW7grrKgrWqK";
        private const string MeteoraDammV1ProgramId = "Eo7WjKq67rjJQSZxS6z3YkapzY3eMj6Xy8X5EQVn5UaB";
        private const string MeteoraDammV2ProgramId = "cpamdpZCGKUy5JxQXB4dcpGPiikHawvSWAd6mEn1sGG";
        private const string MeteoraDynamicVaultProgramId = "24Uqj9JCLxUeoC3hGfh5W3s9FM9uCHDS2SG3LYwBpyTi";
        private const string MeteoraDlmmProgramId = "LBUZKhRxPF3XUpBCjp4YzTKgLccjZhTSDM9YuVaPwxo";
        private const string OrcaWhirlpoolProgramId = "whirLbMiicVdio4qvUfM5KAg6Ct8VwpYzGff3uctyCc";
        private const string OrcaImmutableWhirlpoolProgramId = "iwhrLHdsgrvmnwU8GF2FSmyabSMjfHwFGJAX2ufJ3ZN";
        private const string ManifestProgramId = "MNFSTqtC93rEfYHB6hF82sKdZpUDFWkViLByLd1k1Ms";
        private const string TokenProgramId = "TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA";
        private const string Token2022ProgramId = "TokenzQdBNbLqP5VEhdkAS6EPFLC1PHnBqCXEpPxuEb";
        private const int Token2022MintAccountTypeOffset = 165;
        private const int Token2022ExtensionDataOffset = 166;
        private const ushort Token2022MetadataPointerExtension = 18;
        private const ushort Token2022TokenMetadataExtension = 19;
        private const string WrappedSolMint = "So11111111111111111111111111111111111111112";
        private const int PumpSwapLegacySize = 245;
        private const int PumpSwapCurrentSize = 261;
        private const int PumpSwapPaddedCurrentSize = 300;
        private const int PumpSwapExpandedPaddedCurrentSize = 301;
        private const int PumpSwapBaseMintOffset = 43;
        private const int PumpSwapQuoteMintOffset = 75;
        private const int MaximumPoolCandidates = 128;

        private readonly SolanaRpcClient _rpc;
        private readonly OnChainEngineClient _engine;

        internal OnChainPoolDiscoveryService(SolanaRpcClient rpc, OnChainEngineClient engine)
        {
            _rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        internal async Task<OnChainPoolDiscoveryResult> DiscoverAsync(
            string mint,
            OnChainCommitment commitment,
            IReadOnlyList<string>? candidatePoolAddresses = null,
            CancellationToken cancellationToken = default,
            bool includeDerivedDiscovery = true)
        {
            mint = mint?.Trim() ?? string.Empty;
            var selectedMintAccount = await _rpc.GetAccountInfoAsync(mint, commitment, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("The mint account does not exist.");
            var selectedMint = DecodeMint(selectedMintAccount);

            var warnings = new List<string>();
            var decoded = new List<DecodedCandidate>();
            if (includeDerivedDiscovery)
            {
                var derivedCurve = await _engine.DerivePumpBondingCurveAsync(mint, cancellationToken)
                    .ConfigureAwait(false);
                var curveAccount = await _rpc.GetAccountInfoAsync(
                    derivedCurve.Address,
                    commitment,
                    cancellationToken).ConfigureAwait(false);
                if (curveAccount != null)
                {
                    var curve = await TryDecodeAsync(
                        OnChainProtocolIds.PumpBondingCurve,
                        mint,
                        curveAccount,
                        commitment,
                        cancellationToken).ConfigureAwait(false);
                    if (curve == null)
                    {
                        warnings.Add("A derived Pump account existed but failed strict owner or layout validation.");
                    }
                    else
                    {
                        decoded.Add(curve);
                    }
                }
            }

            var candidateAddressSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (var address in candidatePoolAddresses ?? [])
            {
                if (!string.IsNullOrWhiteSpace(address))
                {
                    candidateAddressSet.Add(address.Trim());
                }
            }
            var pumpSwapRateLimited = false;
            var scanRpc = _rpc;
            if (includeDerivedDiscovery)
            foreach (var dataSize in new[]
                     {
                         PumpSwapExpandedPaddedCurrentSize,
                         PumpSwapPaddedCurrentSize,
                         PumpSwapCurrentSize,
                         PumpSwapLegacySize
                     })
            {
                foreach (var mintOffset in new[] { PumpSwapBaseMintOffset, PumpSwapQuoteMintOffset })
                {
                    if (mintOffset == PumpSwapQuoteMintOffset
                        && string.Equals(mint, WrappedSolMint, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    try
                    {
                        var addresses = await scanRpc.GetProgramAccountAddressesAsync(
                            PumpSwapProgramId,
                            dataSize,
                            mintOffset,
                            mint,
                            commitment,
                            cancellationToken).ConfigureAwait(false);
                        foreach (var address in addresses)
                        {
                            candidateAddressSet.Add(address);
                        }
                    }
                    catch (SolanaRpcException exception) when (exception.IsRateLimited)
                    {
                        pumpSwapRateLimited = true;
                        break;
                    }
                }
                if (pumpSwapRateLimited)
                {
                    break;
                }
            }
            if (includeDerivedDiscovery && string.Equals(mint, WrappedSolMint, StringComparison.Ordinal))
            {
                warnings.Add("Quote-side PumpSwap discovery is omitted for wrapped SOL to avoid downloading the global pool set.");
            }
            if (pumpSwapRateLimited)
            {
                warnings.Add("PumpSwap discovery was rate-limited by the public Solana RPC endpoint, so these results may be incomplete. Wait and retry.");
            }

            var candidateAddresses = candidateAddressSet
                .OrderBy(static address => address, StringComparer.Ordinal)
                .ToArray();
            var truncated = candidateAddresses.Length > MaximumPoolCandidates;
            if (truncated)
            {
                warnings.Add($"Pool discovery returned more than {MaximumPoolCandidates} candidates; refine the mint or use a hosted registry.");
                candidateAddresses = candidateAddresses[..MaximumPoolCandidates];
            }

            var candidateAccounts = await GetAccountsAsync(
                    scanRpc,
                    candidateAddresses,
                    commitment,
                    cancellationToken)
                .ConfigureAwait(false);
            var invalidCandidateCount = 0;
            foreach (var account in candidateAccounts.Where(static account => account != null))
            {
                var protocolId = ProtocolIdForOwner(account!.OwnerProgram);
                if (protocolId == null)
                {
                    continue;
                }
                var candidate = await TryDecodeAsync(
                    protocolId,
                    mint,
                    account,
                    commitment,
                    cancellationToken).ConfigureAwait(false);
                if (candidate == null
                    || (!string.Equals(candidate.Pool.BaseMint, mint, StringComparison.Ordinal)
                        && !string.Equals(candidate.Pool.QuoteMint, mint, StringComparison.Ordinal)))
                {
                    invalidCandidateCount++;
                    continue;
                }
                decoded.Add(candidate);
            }
            if (invalidCandidateCount > 0)
            {
                warnings.Add($"Ignored {invalidCandidateCount} candidate account(s) that failed strict protocol validation.");
            }

            var mintAddresses = decoded
                .SelectMany(static candidate => new[] { candidate.Pool.BaseMint, candidate.Pool.QuoteMint })
                .Append(mint)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var mintAccounts = await GetAccountsAsync(
                    scanRpc,
                    mintAddresses,
                    commitment,
                    cancellationToken)
                .ConfigureAwait(false);
            var mintDetails = new Dictionary<string, MintDetails>(StringComparer.Ordinal)
            {
                [mint] = selectedMint
            };
            for (var index = 0; index < mintAddresses.Length; index++)
            {
                if (mintAccounts[index] != null)
                {
                    mintDetails[mintAddresses[index]] = DecodeMint(mintAccounts[index]!);
                }
            }

            var pools = new List<OnChainPoolDescriptor>();
            foreach (var candidate in decoded)
            {
                if (!mintDetails.TryGetValue(candidate.Pool.BaseMint, out var baseMint)
                    || !mintDetails.TryGetValue(candidate.Pool.QuoteMint, out var quoteMint))
                {
                    warnings.Add($"Ignored pool {candidate.Pool.PoolAddress} because a mint account was unavailable.");
                    continue;
                }

                var selectedAsBase = string.Equals(candidate.Pool.BaseMint, mint, StringComparison.Ordinal);
                var supportsEitherOrientation = candidate.Pool.ProtocolId is
                    OnChainProtocolIds.RaydiumAmmV4 or
                    OnChainProtocolIds.RaydiumCpmm or
                    OnChainProtocolIds.RaydiumClmm or
                    OnChainProtocolIds.MeteoraDammV1 or
                    OnChainProtocolIds.MeteoraDammV2 or
                    OnChainProtocolIds.MeteoraDlmm or
                    OnChainProtocolIds.OrcaWhirlpool or
                    OnChainProtocolIds.ManifestOrderbook;
                var supportStatus = OnChainSupportStatus.Supported;
                string? supportReason = null;
                if (!selectedAsBase && !supportsEitherOrientation)
                {
                    supportStatus = OnChainSupportStatus.DiscoveredUnsupported;
                    supportReason = "Pool discovered, but quote-side display orientation is not supported in the first slice.";
                }
                else if (candidate.Pool.ProtocolId == OnChainProtocolIds.MeteoraDammV1
                         && candidate.Pool.Enabled == false)
                {
                    supportStatus = OnChainSupportStatus.DiscoveredUnsupported;
                    supportReason = "Meteora DAMM v1 pool is disabled on-chain.";
                }
                else if (candidate.Pool.ProtocolId == OnChainProtocolIds.MeteoraDammV1
                         && string.Equals(candidate.Pool.Layout, "stable", StringComparison.Ordinal))
                {
                    supportStatus = OnChainSupportStatus.DiscoveredUnsupported;
                    supportReason = "Meteora DAMM v1 stable-curve marginal pricing is not supported yet.";
                }
                else if (!baseMint.SupportsPricing || !quoteMint.SupportsPricing)
                {
                    supportStatus = OnChainSupportStatus.DiscoveredUnsupported;
                    supportReason = "Pool discovered, but this Token-2022 mint has non-metadata extensions that are not supported for pricing yet.";
                }
                else if (candidate.Pool.Complete == true)
                {
                    supportStatus = OnChainSupportStatus.DiscoveredUnsupported;
                    supportReason = "Pump bonding curve completed; select its migrated PumpSwap pool when available.";
                }

                var normalizedBaseMint = selectedAsBase ? candidate.Pool.BaseMint : candidate.Pool.QuoteMint;
                var normalizedQuoteMint = selectedAsBase ? candidate.Pool.QuoteMint : candidate.Pool.BaseMint;
                var normalizedBaseVault = selectedAsBase ? candidate.Pool.BaseVault : candidate.Pool.QuoteVault;
                var normalizedQuoteVault = selectedAsBase ? candidate.Pool.QuoteVault : candidate.Pool.BaseVault;
                var normalizedBaseDetails = selectedAsBase ? baseMint : quoteMint;
                var normalizedQuoteDetails = selectedAsBase ? quoteMint : baseMint;

                pools.Add(new OnChainPoolDescriptor
                {
                    PoolKey = new OnChainPoolKey
                    {
                        ProtocolId = candidate.Pool.ProtocolId,
                        PoolAddress = candidate.Pool.PoolAddress
                    },
                    PoolType = candidate.Pool.PoolType,
                    ProgramId = candidate.Pool.ProgramId,
                    BaseMint = normalizedBaseMint,
                    QuoteMint = normalizedQuoteMint,
                    BaseDecimals = normalizedBaseDetails.Decimals,
                    QuoteDecimals = normalizedQuoteDetails.Decimals,
                    BaseVault = normalizedBaseVault,
                    QuoteVault = normalizedQuoteVault,
                    ProtocolAccounts = NormalizeProtocolAccounts(
                        candidate.Pool.ProtocolAccounts,
                        selectedAsBase),
                    PairOrientation = selectedAsBase ? "selectedAsBase" : "selectedAsQuote",
                    DiscoveredAtSlot = candidate.Account.Slot,
                    DiscoverySource = "solanaRpcValidated",
                    SupportStatus = supportStatus,
                    SupportReason = supportReason
                });
            }

            return new OnChainPoolDiscoveryResult
            {
                Mint = mint,
                Pools = pools
                    .OrderBy(static pool => pool.SupportStatus == OnChainSupportStatus.Supported ? 0 : 1)
                    .ThenBy(static pool => pool.PoolKey.ProtocolId, StringComparer.Ordinal)
                    .ThenBy(static pool => pool.PoolKey.PoolAddress, StringComparer.Ordinal)
                    .ToArray(),
                Truncated = truncated,
                Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray()
            };
        }

        private async Task<DecodedCandidate?> TryDecodeAsync(
            string protocolId,
            string selectedMint,
            SolanaAccountSnapshot account,
            OnChainCommitment commitment,
            CancellationToken cancellationToken)
        {
            try
            {
                var decoded = await _engine.DecodePoolAccountAsync(
                    protocolId,
                    account.Address,
                    selectedMint,
                    account.OwnerProgram,
                    account.DataBase64,
                    cancellationToken).ConfigureAwait(false);
                if (!string.Equals(decoded.PoolAddress, account.Address, StringComparison.Ordinal)
                    || !string.Equals(decoded.ProgramId, account.OwnerProgram, StringComparison.Ordinal)
                    || (protocolId == OnChainProtocolIds.PumpBondingCurve
                        && !string.Equals(account.OwnerProgram, PumpProgramId, StringComparison.Ordinal))
                    || (protocolId == OnChainProtocolIds.PumpSwap
                        && !string.Equals(account.OwnerProgram, PumpSwapProgramId, StringComparison.Ordinal))
                    || (protocolId == OnChainProtocolIds.RaydiumAmmV4
                        && !string.Equals(account.OwnerProgram, RaydiumAmmV4ProgramId, StringComparison.Ordinal))
                    || (protocolId == OnChainProtocolIds.RaydiumCpmm
                        && !string.Equals(account.OwnerProgram, RaydiumCpmmProgramId, StringComparison.Ordinal))
                    || (protocolId == OnChainProtocolIds.RaydiumClmm
                        && !string.Equals(account.OwnerProgram, RaydiumClmmProgramId, StringComparison.Ordinal))
                    || (protocolId == OnChainProtocolIds.MeteoraDammV1
                        && !string.Equals(account.OwnerProgram, MeteoraDammV1ProgramId, StringComparison.Ordinal))
                    || (protocolId == OnChainProtocolIds.MeteoraDammV2
                        && !string.Equals(account.OwnerProgram, MeteoraDammV2ProgramId, StringComparison.Ordinal))
                    || (protocolId == OnChainProtocolIds.MeteoraDlmm
                        && !string.Equals(account.OwnerProgram, MeteoraDlmmProgramId, StringComparison.Ordinal))
                    || (protocolId == OnChainProtocolIds.OrcaWhirlpool
                        && !string.Equals(account.OwnerProgram, OrcaWhirlpoolProgramId, StringComparison.Ordinal)
                        && !string.Equals(account.OwnerProgram, OrcaImmutableWhirlpoolProgramId, StringComparison.Ordinal))
                    || (protocolId == OnChainProtocolIds.ManifestOrderbook
                        && !string.Equals(account.OwnerProgram, ManifestProgramId, StringComparison.Ordinal)))
                {
                    return null;
                }
                if (protocolId == OnChainProtocolIds.MeteoraDammV1
                    && decoded.Enabled == true
                    && string.Equals(decoded.Layout, "constantProduct", StringComparison.Ordinal))
                {
                    await HydrateMeteoraDammV1Async(decoded, commitment, cancellationToken)
                        .ConfigureAwait(false);
                }
                return new DecodedCandidate(decoded, account);
            }
            catch (OnChainEngineRequestException)
            {
                return null;
            }
        }

        private static string? ProtocolIdForOwner(string ownerProgram)
        {
            return ownerProgram switch
            {
                PumpSwapProgramId => OnChainProtocolIds.PumpSwap,
                RaydiumAmmV4ProgramId => OnChainProtocolIds.RaydiumAmmV4,
                RaydiumCpmmProgramId => OnChainProtocolIds.RaydiumCpmm,
                RaydiumClmmProgramId => OnChainProtocolIds.RaydiumClmm,
                MeteoraDammV1ProgramId => OnChainProtocolIds.MeteoraDammV1,
                MeteoraDammV2ProgramId => OnChainProtocolIds.MeteoraDammV2,
                MeteoraDlmmProgramId => OnChainProtocolIds.MeteoraDlmm,
                OrcaWhirlpoolProgramId => OnChainProtocolIds.OrcaWhirlpool,
                OrcaImmutableWhirlpoolProgramId => OnChainProtocolIds.OrcaWhirlpool,
                ManifestProgramId => OnChainProtocolIds.ManifestOrderbook,
                _ => null
            };
        }

        private async Task HydrateMeteoraDammV1Async(
            OnChainDecodedPoolAccount pool,
            OnChainCommitment commitment,
            CancellationToken cancellationToken)
        {
            var tokenAVaultState = RequireProtocolAccount(pool, "tokenAVaultState");
            var tokenBVaultState = RequireProtocolAccount(pool, "tokenBVaultState");
            var accounts = await GetAccountsAsync(
                    _rpc,
                    [tokenAVaultState, tokenBVaultState],
                    commitment,
                    cancellationToken)
                .ConfigureAwait(false);
            if (accounts[0] == null || accounts[1] == null)
            {
                throw InvalidMeteora("Meteora DAMM v1 Dynamic Vault state was unavailable.");
            }
            var vaultA = DecodeMeteoraDynamicVault(accounts[0]!, pool.BaseMint);
            var vaultB = DecodeMeteoraDynamicVault(accounts[1]!, pool.QuoteMint);
            pool.BaseVault = vaultA.TokenVault;
            pool.QuoteVault = vaultB.TokenVault;
            pool.ProtocolAccounts = pool.ProtocolAccounts
                .Concat([
                    new OnChainProtocolAccount { Role = "tokenAVaultLpMint", Address = vaultA.LpMint },
                    new OnChainProtocolAccount { Role = "tokenBVaultLpMint", Address = vaultB.LpMint }
                ])
                .ToArray();
        }

        private static string RequireProtocolAccount(OnChainDecodedPoolAccount pool, string role)
        {
            return pool.ProtocolAccounts.SingleOrDefault(account => account.Role == role)?.Address
                   ?? throw InvalidMeteora($"Meteora DAMM v1 pool omitted the required {role} account.");
        }

        private static MeteoraDynamicVaultDetails DecodeMeteoraDynamicVault(
            SolanaAccountSnapshot account,
            string expectedMint)
        {
            byte[] data;
            try
            {
                data = Convert.FromBase64String(account.DataBase64);
            }
            catch (FormatException)
            {
                throw InvalidMeteora("Meteora Dynamic Vault account data was not valid base64.");
            }
            if (!string.Equals(account.OwnerProgram, MeteoraDynamicVaultProgramId, StringComparison.Ordinal)
                || data.Length is < 1227 or > 10240
                || !data.AsSpan(0, 8).SequenceEqual(new byte[] { 211, 8, 232, 43, 2, 152, 117, 119 })
                || data[8] != 1)
            {
                throw InvalidMeteora("Meteora Dynamic Vault owner or layout was invalid.");
            }
            var tokenMint = SolanaBase58.Encode(data.AsSpan(83, 32));
            if (!string.Equals(tokenMint, expectedMint, StringComparison.Ordinal))
            {
                throw InvalidMeteora("Meteora Dynamic Vault mint did not match its pool.");
            }
            return new MeteoraDynamicVaultDetails(
                SolanaBase58.Encode(data.AsSpan(19, 32)),
                SolanaBase58.Encode(data.AsSpan(115, 32)));
        }

        private static OnChainProtocolAccount[] NormalizeProtocolAccounts(
            IReadOnlyList<OnChainProtocolAccount> accounts,
            bool selectedAsTokenA)
        {
            return accounts.Select(account => new OnChainProtocolAccount
            {
                Role = account.Role switch
                {
                    "tokenAVaultState" => selectedAsTokenA ? "baseVaultState" : "quoteVaultState",
                    "tokenBVaultState" => selectedAsTokenA ? "quoteVaultState" : "baseVaultState",
                    "tokenAVaultLp" => selectedAsTokenA ? "baseVaultLp" : "quoteVaultLp",
                    "tokenBVaultLp" => selectedAsTokenA ? "quoteVaultLp" : "baseVaultLp",
                    "tokenAVaultLpMint" => selectedAsTokenA ? "baseVaultLpMint" : "quoteVaultLpMint",
                    "tokenBVaultLpMint" => selectedAsTokenA ? "quoteVaultLpMint" : "baseVaultLpMint",
                    _ => account.Role
                },
                Address = account.Address
            }).ToArray();
        }

        private static OnChainEngineRequestException InvalidMeteora(string message)
        {
            return new OnChainEngineRequestException("invalidCandidate", message, false);
        }

        private async Task<SolanaAccountSnapshot?[]> GetAccountsAsync(
            SolanaRpcClient rpc,
            IReadOnlyList<string> addresses,
            OnChainCommitment commitment,
            CancellationToken cancellationToken)
        {
            var results = new SolanaAccountSnapshot?[addresses.Count];
            for (var offset = 0; offset < addresses.Count; offset += 100)
            {
                var count = Math.Min(100, addresses.Count - offset);
                var batch = addresses.Skip(offset).Take(count).ToArray();
                var accounts = await rpc.GetMultipleAccountsAsync(batch, commitment, cancellationToken)
                    .ConfigureAwait(false);
                for (var index = 0; index < count; index++)
                {
                    results[offset + index] = accounts[index];
                }
            }
            return results;
        }

        private static MintDetails DecodeMint(SolanaAccountSnapshot account)
        {
            if (account.OwnerProgram != TokenProgramId && account.OwnerProgram != Token2022ProgramId)
            {
                throw new InvalidOperationException("The selected address is not an SPL mint account.");
            }
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(account.DataBase64);
            }
            catch (FormatException exception)
            {
                throw new InvalidOperationException("The mint account data is not valid base64.", exception);
            }
            if (bytes.Length < 82)
            {
                throw new InvalidOperationException("The mint account is shorter than the SPL mint layout.");
            }
            var token2022 = account.OwnerProgram == Token2022ProgramId;
            return new MintDetails(
                bytes[44],
                !token2022 || HasOnlyMetadataToken2022Extensions(bytes));
        }

        private static bool HasOnlyMetadataToken2022Extensions(byte[] bytes)
        {
            if (bytes.Length == 82)
            {
                return true;
            }
            if (bytes.Length < Token2022ExtensionDataOffset
                || bytes[Token2022MintAccountTypeOffset] != 1)
            {
                return false;
            }

            var offset = Token2022ExtensionDataOffset;
            while (offset < bytes.Length)
            {
                if (bytes.Length - offset < 4)
                {
                    return false;
                }
                var extensionType = (ushort)(bytes[offset] | bytes[offset + 1] << 8);
                var extensionLength = (ushort)(bytes[offset + 2] | bytes[offset + 3] << 8);
                if (extensionType == 0 && extensionLength == 0)
                {
                    return bytes.Skip(offset).All(static value => value == 0);
                }
                if (extensionType is not (Token2022MetadataPointerExtension or Token2022TokenMetadataExtension)
                    || extensionLength > bytes.Length - offset - 4)
                {
                    return false;
                }
                offset += 4 + extensionLength;
            }
            return true;
        }

        private sealed record DecodedCandidate(
            OnChainDecodedPoolAccount Pool,
            SolanaAccountSnapshot Account);

        private sealed record MintDetails(byte Decimals, bool SupportsPricing);

        private sealed record MeteoraDynamicVaultDetails(string TokenVault, string LpMint);
    }
}

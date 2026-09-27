using TrenchHQ.Core.OnChain;
using TrenchHQ.Core.Providers;
using TrenchHQ.Infrastructure.OnChain.Solana;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TrenchHQ.Infrastructure.OnChain
{
    internal sealed class OnChainSnapshotReconciler
    {
        private readonly OnChainEngineClient _engine;

        internal OnChainSnapshotReconciler(OnChainEngineClient engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        internal async Task ReconcileAsync(
            OnChainProviderConfiguration configuration,
            string apiKey,
            IReadOnlyList<OnChainWatchedPoolSelection> pools,
            CancellationToken cancellationToken)
        {
            var addresses = pools
                .SelectMany(static pool => pool.Descriptor.EnumerateSolanaAccountAddresses())
                .Where(static address => !string.IsNullOrWhiteSpace(address))
                .Select(static address => address!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            var rpc = new SolanaRpcClient(httpClient, configuration, apiKey);
            for (var offset = 0; offset < addresses.Length; offset += 100)
            {
                var batch = addresses.Skip(offset).Take(100).ToArray();
                var accounts = await rpc.GetMultipleAccountsAsync(
                    batch,
                    configuration.Commitment,
                    cancellationToken).ConfigureAwait(false);
                foreach (var account in accounts.Where(static account => account != null))
                {
                    await _engine.PublishAccountUpdateAsync(new OnChainRawAccountUpdate
                    {
                        Pubkey = account!.Address,
                        OwnerProgram = account.OwnerProgram,
                        DataBase64 = account.DataBase64,
                        Slot = account.Slot,
                        WriteVersion = 0,
                        Commitment = configuration.Commitment,
                        SourceId = $"rpcSnapshot:{configuration.Id}",
                        ObservedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    }, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }
}

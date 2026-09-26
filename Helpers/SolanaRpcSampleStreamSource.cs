using TrenchHQ.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TrenchHQ.Helpers
{
    // Price tickers need current pool state, not every intervening swap event.
    internal sealed class SolanaRpcSampleStreamSource : IOnChainStreamSource
    {
        internal static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

        private readonly OnChainProviderConfiguration _configuration;
        private readonly string _apiKey;
        private readonly HttpMessageHandler? _handler;
        private readonly TimeSpan _interval;

        internal SolanaRpcSampleStreamSource(
            OnChainProviderConfiguration configuration,
            string apiKey,
            HttpMessageHandler? handler = null,
            TimeSpan? interval = null)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
            _handler = handler;
            _interval = interval ?? SampleInterval;
            if (_interval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(interval));
            }
        }

        public async Task RunAsync(
            OnChainStreamSubscription subscription,
            ChannelWriter<OnChainSourceUpdate> output,
            CancellationToken cancellationToken)
        {
            var batches = BuildAccountBatches(subscription.Pools);
            if (batches.Length == 0)
            {
                return;
            }

            using var http = _handler == null
                ? new HttpClient { Timeout = TimeSpan.FromSeconds(20) }
                : new HttpClient(_handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
            var rpc = new SolanaRpcClient(http, _configuration, _apiKey);
            while (!cancellationToken.IsCancellationRequested)
            {
                foreach (var batch in batches)
                {
                    IReadOnlyList<SolanaAccountSnapshot?> accounts = batch.Length == 1
                        ? [await rpc.GetAccountInfoAsync(batch[0], subscription.Commitment,
                            cancellationToken).ConfigureAwait(false)]
                        : await rpc.GetMultipleAccountsAsync(
                            batch, subscription.Commitment, cancellationToken).ConfigureAwait(false);
                    if (accounts.Any(static account => account == null))
                    {
                        throw new SolanaRpcException("A selected Solana pool account is unavailable.");
                    }
                    var observedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var snapshot = new OnChainSourceAccountSnapshot(accounts.Select(account =>
                        new OnChainRawAccountUpdate
                        {
                            Pubkey = account!.Address,
                            OwnerProgram = account.OwnerProgram,
                            DataBase64 = account.DataBase64,
                            Slot = account.Slot,
                            WriteVersion = 0,
                            Commitment = subscription.Commitment,
                            SourceId = $"rpcSample:{_configuration.Id}",
                            ObservedAtUnixMs = observedAt
                        }).ToArray());
                    await output.WriteAsync(snapshot, cancellationToken).ConfigureAwait(false);
                    await snapshot.Processed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
            }
        }

        internal static string[][] BuildAccountBatches(OnChainWatchedPoolSelection[] pools)
        {
            var batches = new List<string[]>();
            var current = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pool in pools)
            {
                var addresses = pool.Descriptor.EnumerateSolanaAccountAddresses()
                    .Where(static address => !string.IsNullOrWhiteSpace(address))
                    .Select(static address => address!)
                    .Distinct(StringComparer.Ordinal).ToArray();
                if (addresses.Length > 100)
                {
                    throw new InvalidOperationException("A pool exceeds the atomic RPC account limit.");
                }
                if (current.Union(addresses, StringComparer.Ordinal).Count() > 100)
                {
                    batches.Add(current.ToArray());
                    current.Clear();
                }
                current.UnionWith(addresses);
            }
            if (current.Count > 0)
            {
                batches.Add(current.ToArray());
            }
            return batches.ToArray();
        }
    }
}

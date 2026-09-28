using System;
using System.Threading;
using System.Threading.Tasks;
using TrenchHQ.Infrastructure.OnChain.Evm;
using TrenchHQ.Infrastructure.OnChain.Solana;

namespace TrenchHQ.Infrastructure.OnChain
{
    internal static class OnChainRequestRetry
    {
        internal static async Task<T> RunAsync<T>(Func<Task<T>> request, CancellationToken cancellationToken)
        {
            for (var attempt = 0; ; attempt++)
            {
                try { return await request().ConfigureAwait(false); }
                catch (Exception exception) when (attempt < 3 && IsThroughputFailure(exception))
                {
                    // A long provider pause should select an available backup promptly.
                    if (RetryAfter(exception) > TimeSpan.FromSeconds(30)) throw;
                    var delay = RetryAfter(exception) ?? TimeSpan.FromMilliseconds((1 << attempt) * 1000 + Random.Shared.Next(250));
                    await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        internal static TimeSpan? RetryAfter(Exception exception) => exception switch
        {
            SolanaRpcException solana => solana.RetryAfter,
            EvmJsonRpcException evm => evm.RetryAfter,
            _ => null
        };

        private static bool IsThroughputFailure(Exception exception) => exception is
            SolanaRpcException { HttpStatusCode: 429 } or SolanaRpcException { RpcCode: 429 }
            or EvmJsonRpcException { Kind: EvmRpcFailureKind.RateLimited, RpcCode: not 402 };
    }
}

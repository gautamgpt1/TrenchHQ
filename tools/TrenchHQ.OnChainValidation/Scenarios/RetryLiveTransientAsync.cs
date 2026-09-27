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
using TrenchHQ.TestSupport;
using static TrenchHQ.TestSupport.OnChainFixtures;

namespace TrenchHQ.OnChainValidation;

internal static partial class Validation
{
    static async Task RetryLiveTransientAsync(Func<Task> operation, string operationName)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await operation();
                return;
            }
            catch (EvmJsonRpcException exception) when (
                (exception.Kind == EvmRpcFailureKind.RateLimited
                 || exception.Kind == EvmRpcFailureKind.RpcError && exception.RpcCode == null)
                && attempt < 4)
            {
                Console.WriteLine(
                    $"LIVE RETRY | {operationName} | transient provider failure | attempt={attempt + 1}");
                var delay = exception.Kind == EvmRpcFailureKind.RateLimited
                    ? TimeSpan.FromSeconds(2)
                    : TimeSpan.FromSeconds(1 << attempt);
                await Task.Delay(delay);
            }
        }
    }
}

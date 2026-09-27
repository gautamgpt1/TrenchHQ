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
    static bool LiveLogMatchesPool(EvmLogUpdate log, string protocolId, string poolId)
    {
        return protocolId == OnChainProtocolIds.UniswapV4
            ? log.Address.Equals(
                  EthereumDeploymentRegistry.UniswapV4PoolManager,
                  StringComparison.OrdinalIgnoreCase)
              && log.Topics.Length > 1
              && log.Topics[1].Equals(poolId, StringComparison.OrdinalIgnoreCase)
            : log.Address.Equals(poolId, StringComparison.OrdinalIgnoreCase);
    }
}

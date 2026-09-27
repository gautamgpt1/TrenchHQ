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
    static async Task VerifyLiveProtocolAccountsAsync(OnChainEngineClient engine)
    {
        const string sampleMint = "6NwarBvDkXhByqVp2Qkq5i9XbtA2B3Bwe8SWGu9vpump";
        const string metadataOnlyToken2022Mint = "CNWxmoBSQZo2Sgp5KSAK5m9FwSqDbXQRP4CNMuoe78Gm";
        const string metadataOnlyPumpSwapPool = "5x74XfDESP5j2vfr7nW76mqgQkKMUJUhGWXog4FaJs9X";
        var fixtures = new[]
        {
            new LivePoolFixture(
                OnChainProtocolIds.RaydiumAmmV4,
                "58oQChx4yWmvKdwLLZzBi4ChoCc2fqCUWBkwMihLYQo2"),
            new LivePoolFixture(
                OnChainProtocolIds.RaydiumCpmm,
                "7JuwJuNU88gurFnyWeiyGKbFmExMWcmRZntn9imEzdny"),
            new LivePoolFixture(
                OnChainProtocolIds.RaydiumClmm,
                "BZtgQEyS6eXUXicYPHecYQ7PybqodXQMvkjUbP4R8mUU"),
            new LivePoolFixture(
                OnChainProtocolIds.MeteoraDammV1,
                "4SBYWY5UuxybWuj8FwHdFXUN6mbtACrqbJwiZ9mXworP"),
            new LivePoolFixture(
                OnChainProtocolIds.MeteoraDammV2,
                "8Pm2kZpnxD3hoMmt4bjStX2Pw2Z9abpbHzZxMPqxPmie"),
            new LivePoolFixture(
                OnChainProtocolIds.MeteoraDlmm,
                "3S86WtfvZroac8tGH3h1bKZmPK7uaZWNCg2U6kZH9vvd"),
            new LivePoolFixture(
                OnChainProtocolIds.OrcaWhirlpool,
                "AjuZMJRkcP9QsJuuBg35eAKU7wM9A8a11qy5D9nvp7xy")
        };
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var rpc = new SolanaRpcClient(httpClient, OnChainPoolDiscoveryService.PublicMainnetRpcEndpoint);
        var accounts = await rpc.GetMultipleAccountsAsync(
            fixtures.Select(static fixture => fixture.Address).ToArray(),
            OnChainCommitment.Confirmed,
            CancellationToken.None);
        for (var index = 0; index < fixtures.Length; index++)
        {
            var fixture = fixtures[index];
            var account = accounts[index]
                          ?? throw new InvalidOperationException($"Live pool {fixture.Address} was unavailable.");
            var decoded = await engine.DecodePoolAccountAsync(
                fixture.ProtocolId,
                fixture.Address,
                "So11111111111111111111111111111111111111112",
                account.OwnerProgram,
                account.DataBase64);
            AssertEqual(fixture.ProtocolId, decoded.ProtocolId,
                $"Live pool {fixture.Address} decoded as the wrong protocol.");
            Assert(!string.IsNullOrWhiteSpace(decoded.BaseMint)
                   && !string.IsNullOrWhiteSpace(decoded.QuoteMint)
                   && (fixture.ProtocolId == OnChainProtocolIds.MeteoraDammV1
                       ? decoded.ProtocolAccounts.Length == 4
                       : !string.IsNullOrWhiteSpace(decoded.BaseVault)
                         && !string.IsNullOrWhiteSpace(decoded.QuoteVault)),
                $"Live pool {fixture.Address} did not expose its complete pair identity.");
        }

        var catalog = await DexScreenerPoolCatalogClient.Current.SearchAsync("solana", sampleMint);
        var discovery = new OnChainPoolDiscoveryService(rpc, engine);
        var discovered = await discovery.DiscoverAsync(
            sampleMint,
            OnChainCommitment.Confirmed,
            catalog.Pools.Select(static pool => pool.PoolAddress).ToArray());
        foreach (var protocol in new[]
                 {
                     OnChainProtocolIds.PumpSwap,
                     OnChainProtocolIds.MeteoraDlmm,
                     OnChainProtocolIds.OrcaWhirlpool
                 })
        {
            Assert(discovered.Pools.Any(pool => pool.PoolKey.ProtocolId == protocol
                                                && pool.SupportStatus == OnChainSupportStatus.Supported),
                $"The live sample mint did not return a selectable {protocol} pool.");
        }

        var token2022Catalog = await DexScreenerPoolCatalogClient.Current.SearchAsync(
            "solana",
            metadataOnlyToken2022Mint);
        var token2022Discovered = await discovery.DiscoverAsync(
            metadataOnlyToken2022Mint,
            OnChainCommitment.Confirmed,
            token2022Catalog.Pools.Select(static pool => pool.PoolAddress).ToArray());
        Assert(token2022Discovered.Pools.Any(pool =>
                pool.PoolKey.PoolAddress == metadataOnlyPumpSwapPool
                && pool.PoolKey.ProtocolId == OnChainProtocolIds.PumpSwap
                && pool.SupportStatus == OnChainSupportStatus.Supported),
            "The live metadata-only Token-2022 mint did not return its selectable PumpSwap pool.");
    }
}

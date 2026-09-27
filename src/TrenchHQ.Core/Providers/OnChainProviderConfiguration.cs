using TrenchHQ.Core.OnChain;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TrenchHQ.Core.Providers
{
    internal enum OnChainStreamTransport
    {
        SolanaWebSocket,
        YellowstoneGrpc,
        EvmWebSocket
    }

    internal enum OnChainEndpointAuthenticationMode
    {
        None,
        ApiKeyQuery,
        ApiKeyQueryUnderscore,
        ApiKeyPathSegment
    }

    internal enum OnChainProviderAccessCategory
    {
        FreeTierAvailable,
        TrialOrPaid,
        CustomEndpoint
    }

    internal static class OnChainProviderTypes
    {
        internal const string AlchemyWebSocket = "alchemyWebSocket";
        internal const string HeliusWebSocket = "heliusWebSocket";
        internal const string QuickNodeWebSocket = "quickNodeWebSocket";
        internal const string ShyftWebSocket = "shyftWebSocket";
        internal const string ChainstackWebSocket = "chainstackWebSocket";
        internal const string DrpcWebSocket = "drpcWebSocket";
        internal const string AlchemyYellowstone = "alchemyYellowstone";
        internal const string HeliusLaserStream = "heliusLaserStream";
        internal const string TritonYellowstone = "tritonYellowstone";
        internal const string CustomYellowstone = "customYellowstone";
        internal const string AlchemyEthereum = "alchemyEthereum";
        internal const string DrpcEthereum = "drpcEthereum";
        internal const string InfuraEthereum = "infuraEthereum";
        internal const string QuickNodeEthereum = "quickNodeEthereum";
        internal const string ChainstackEthereum = "chainstackEthereum";
        internal const string PublicNodeEthereum = "publicNodeEthereum";
        internal const string CustomEthereum = "customEthereum";
        internal const string AlchemyBase = "alchemyBase";
        internal const string DrpcBase = "drpcBase";
        internal const string InfuraBase = "infuraBase";
        internal const string BasePublic = "basePublic";
        internal const string ChainstackBase = "chainstackBase";
        internal const string CustomBase = "customBase";
        internal const string AlchemyBnb = "alchemyBnb";
        internal const string DrpcBnb = "drpcBnb";
        internal const string InfuraBnb = "infuraBnb";
        internal const string PublicNodeBnb = "publicNodeBnb";
        internal const string ChainstackBnb = "chainstackBnb";
        internal const string CustomBnb = "customBnb";
        internal const string AlchemyRobinhood = "alchemyRobinhood";
        internal const string PublicNodeRobinhood = "publicNodeRobinhood";
        internal const string QuickNodeRobinhood = "quickNodeRobinhood";
        internal const string ChainstackRobinhood = "chainstackRobinhood";
        internal const string CustomRobinhood = "customRobinhood";
    }

    internal sealed record OnChainProviderPreset(
        string ProviderType,
        string DisplayName,
        OnChainStreamTransport StreamTransport,
        string DefaultStreamEndpoint,
        string DefaultRpcEndpoint,
        string SetupInstructions,
        string CredentialLabel,
        string SetupLinkLabel,
        string SetupUrl,
        OnChainEndpointAuthenticationMode StreamAuthenticationMode,
        OnChainEndpointAuthenticationMode RpcAuthenticationMode,
        bool ReplayEnabled,
        string ChainNamespace = ChainNamespaces.Solana,
        string ChainId = "mainnet-beta",
        bool RequiresCredential = true,
        string ProviderFamily = "",
        OnChainProviderAccessCategory AccessCategory = OnChainProviderAccessCategory.FreeTierAvailable,
        IReadOnlyList<string>? Badges = null);

    internal static class OnChainProviderCatalog
    {
        internal static readonly OnChainProviderPreset[] Presets =
        [
            new(
                OnChainProviderTypes.AlchemyWebSocket,
                "Alchemy — Standard WebSocket",
                OnChainStreamTransport.SolanaWebSocket,
                "wss://solana-mainnet.streaming.alchemy.com/v2",
                "https://solana-mainnet.g.alchemy.com/v2",
                "Free plan: 30M compute units/month and standard Solana WebSockets.\n1. Open Alchemy Apps and create or select an app.\n2. Enable Solana Mainnet for that app.\n3. Copy the app API key (not an Admin Access Key).\n4. Paste only that key below.\nPrice tickers default to one-second current-state polling; live event streaming is a separate widget choice. Polling batches shared accounts and reconnects with a fresh snapshot. Streaming usage depends on delivered data.",
                "Alchemy app API key",
                "Open Alchemy Apps",
                "https://dashboard.alchemy.com/apps",
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                false,
                ProviderFamily: "alchemy",
                Badges: ["Recommended", "1 key · 5 chains"]),
            new(
                OnChainProviderTypes.HeliusWebSocket,
                "Helius — Standard WebSocket",
                OnChainStreamTransport.SolanaWebSocket,
                "wss://mainnet.helius-rpc.com",
                "https://mainnet.helius-rpc.com",
                "Free plan: 1M credits/month, 10 requests/second, and up to 5 standard WebSocket connections.\n1. Open the Helius Dashboard.\n2. Create or select a project.\n3. Open API Keys and copy the project API key.\n4. Paste only that key below.\nStandard WebSocket data is metered by uncompressed bytes. Helius extensions require a paid plan; Mainnet LaserStream gRPC requires Business or Professional.",
                "Helius project API key",
                "Open Helius Dashboard",
                "https://dashboard.helius.dev/",
                OnChainEndpointAuthenticationMode.ApiKeyQuery,
                OnChainEndpointAuthenticationMode.ApiKeyQuery,
                false,
                ProviderFamily: "helius",
                Badges: ["1M credits/month"]),
            new(
                OnChainProviderTypes.QuickNodeWebSocket,
                "QuickNode — Standard WebSocket",
                OnChainStreamTransport.SolanaWebSocket,
                string.Empty,
                string.Empty,
                "QuickNode offers a one-month no-card trial, not a permanent free plan. Its current multichain endpoint can expose multiple networks through one endpoint token, but TrenchHQ has not credential-live-validated that mode.\n1. Open QuickNode and create or enable a Solana Mainnet endpoint.\n2. Copy its generated HTTPS and WSS URLs.\n3. Enter only the credential-free hosts below (for example, wss://NAME.solana-mainnet.quiknode.pro and https://NAME.solana-mainnet.quiknode.pro).\n4. Paste the final token/path segment into the protected token field.\nDo not paste the token into either endpoint box.",
                "QuickNode endpoint token",
                "Open QuickNode Dashboard",
                "https://dashboard.quicknode.com/",
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                false,
                ProviderFamily: "quicknode",
                AccessCategory: OnChainProviderAccessCategory.TrialOrPaid,
                Badges: ["30-day trial", "Paid afterward", "Endpoint-specific"]),
            new(
                OnChainProviderTypes.ShyftWebSocket,
                "Shyft — Standard WebSocket",
                OnChainStreamTransport.SolanaWebSocket,
                "wss://rpc.shyft.to",
                "https://rpc.shyft.to",
                "Free plan: unlimited RPC credits at up to 10 requests/second; gRPC is not included. Shyft documents standard Solana WebSocket methods including slotSubscribe.\n1. Open the Shyft Dashboard.\n2. Create or copy an API key.\n3. Paste only that key below.\nTrenchHQ supplies the standard mainnet endpoints. Save checks HTTP access; streaming is used only when an active widget or wallet needs it.",
                "Shyft API key",
                "Open Shyft Dashboard",
                "https://dashboard.shyft.to/",
                OnChainEndpointAuthenticationMode.ApiKeyQueryUnderscore,
                OnChainEndpointAuthenticationMode.ApiKeyQueryUnderscore,
                false,
                ProviderFamily: "shyft",
                Badges: ["10 req/s"]),
            new(
                OnChainProviderTypes.ChainstackWebSocket,
                "Chainstack — Standard WebSocket",
                OnChainStreamTransport.SolanaWebSocket,
                "wss://solana-mainnet.core.chainstack.com",
                "https://solana-mainnet.core.chainstack.com",
                "Free Developer plan: 3M request units/month, 25 requests/second, and one node total across the account, with no trial time limit. Choosing Solana uses that free node allowance.\n1. Open Chainstack and create a Solana Mainnet Global Node.\n2. Copy its endpoint.\n3. Paste only the final access-token path segment into the protected token field.\n4. Keep both endpoint boxes credential-free.",
                "Chainstack node access token",
                "Open Chainstack Console",
                "https://console.chainstack.com/",
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                false,
                ProviderFamily: "chainstack",
                Badges: ["1 free node total", "Node-specific"]),
            new(
                OnChainProviderTypes.DrpcWebSocket,
                "dRPC — Standard WebSocket",
                OnChainStreamTransport.SolanaWebSocket,
                "wss://lb.drpc.live/solana",
                "https://lb.drpc.live/solana",
                "Free tier: 210M compute units per 30 days over public nodes; availability and rate limits can vary by network.\n1. Open dRPC and create a key with Solana access.\n2. Copy that dRPC key.\n3. Paste only the key below.\nTrenchHQ appends it to the credential-free endpoint bases and tests Solana access before enabling it.",
                "dRPC key",
                "Open dRPC",
                "https://drpc.org/login",
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                false,
                ProviderFamily: "drpc",
                Badges: ["1 key · 4 chains"]),
            new(
                OnChainProviderTypes.AlchemyYellowstone,
                "Alchemy — Yellowstone gRPC",
                OnChainStreamTransport.YellowstoneGrpc,
                "https://solana-mainnet.streaming.alchemy.com",
                "https://solana-mainnet.g.alchemy.com/v2",
                "Paid mode.\n1. Enable Pay As You Go for your Alchemy team.\n2. Create or select a Solana Mainnet app.\n3. Copy its app API key.\n4. Paste only that key below.\nYellowstone supports filtered account/transaction streaming and provider replay; it is not included in Alchemy's free tier.",
                "Alchemy app API key",
                "Open Alchemy Apps",
                "https://dashboard.alchemy.com/apps",
                OnChainEndpointAuthenticationMode.None,
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                true,
                ProviderFamily: "alchemy",
                AccessCategory: OnChainProviderAccessCategory.TrialOrPaid,
                Badges: ["PAYG required", "Replay"]),
            new(
                OnChainProviderTypes.HeliusLaserStream,
                "Helius — LaserStream gRPC",
                OnChainStreamTransport.YellowstoneGrpc,
                "https://laserstream-mainnet-sgp.helius-rpc.com",
                "https://mainnet.helius-rpc.com",
                "Trial or paid mode; ongoing Mainnet LaserStream access requires Business or Professional, and Helius offers a reviewed 2-day trial.\n1. Open Helius and create or select a project.\n2. Open API Keys.\n3. Copy the project API key.\n4. Paste only that key below.",
                "Helius project API key",
                "Open Helius Dashboard",
                "https://dashboard.helius.dev/",
                OnChainEndpointAuthenticationMode.None,
                OnChainEndpointAuthenticationMode.ApiKeyQuery,
                true,
                ProviderFamily: "helius",
                AccessCategory: OnChainProviderAccessCategory.TrialOrPaid,
                Badges: ["Business+ mainnet", "2-day trial by approval", "Replay"]),
            new(
                OnChainProviderTypes.TritonYellowstone,
                "Triton One — Yellowstone gRPC",
                OnChainStreamTransport.YellowstoneGrpc,
                string.Empty,
                string.Empty,
                "Paid mode with a $125 minimum prepaid deposit that is non-refundable and valid for 12 months.\n1. Complete Triton onboarding and fund the prepaid balance.\n2. Copy the Yellowstone endpoint, Solana RPC endpoint, and X-Token from the dashboard.\n3. Enter both credential-free endpoints below.\n4. Paste the X-Token into the protected token field.",
                "Triton x-token",
                "Open Triton onboarding",
                "https://customers.triton.one/onboarding",
                OnChainEndpointAuthenticationMode.None,
                OnChainEndpointAuthenticationMode.None,
                true,
                ProviderFamily: "triton",
                AccessCategory: OnChainProviderAccessCategory.TrialOrPaid,
                Badges: ["$125 prepaid", "Replay"]),
            new(
                OnChainProviderTypes.CustomYellowstone,
                "Custom Yellowstone gRPC",
                OnChainStreamTransport.YellowstoneGrpc,
                string.Empty,
                string.Empty,
                "Use a Yellowstone endpoint supplied by another provider, your organization, or a self-hosted node. Cost and reliability depend on the endpoint owner.\n1. Ask the operator for a credential-free HTTPS Yellowstone gRPC endpoint.\n2. Obtain its X-Token.\n3. Obtain a credential-free HTTPS Solana RPC endpoint.\n4. Enter those values below.\nReplay is disabled because custom-provider semantics cannot be assumed.",
                "Yellowstone x-token",
                string.Empty,
                string.Empty,
                OnChainEndpointAuthenticationMode.None,
                OnChainEndpointAuthenticationMode.None,
                false,
                ProviderFamily: "custom",
                AccessCategory: OnChainProviderAccessCategory.CustomEndpoint,
                Badges: ["X-Token"])
        ];

        internal static readonly OnChainProviderPreset[] EthereumPresets =
        [
            new(
                OnChainProviderTypes.AlchemyEthereum,
                "Alchemy",
                OnChainStreamTransport.EvmWebSocket,
                "wss://eth-mainnet.g.alchemy.com/v2",
                "https://eth-mainnet.g.alchemy.com/v2",
                "Free tier: 30M compute units/month. Create an Ethereum Mainnet app in Alchemy, copy its app API key, and paste only the key below. The same saved key can configure all five TrenchHQ chains, which are tested independently before activation.",
                "Alchemy app API key",
                "Open Alchemy Apps",
                "https://dashboard.alchemy.com/apps",
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                false,
                ChainNamespaces.Eip155,
                "1",
                true,
                "alchemy",
                Badges: ["Recommended", "1 key · 5 chains"]),
            new(
                OnChainProviderTypes.DrpcEthereum,
                "dRPC",
                OnChainStreamTransport.EvmWebSocket,
                "wss://lb.drpc.live/ethereum",
                "https://lb.drpc.live/ethereum",
                "Free tier: 210M compute units per 30 days over public nodes. Create a dRPC key and paste only that key below. TrenchHQ appends it to the credential-free Ethereum endpoint base; the same saved key can configure TrenchHQ Solana, Base, and BNB rows.",
                "dRPC key",
                "Open dRPC",
                "https://drpc.org/login",
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                false,
                ChainNamespaces.Eip155,
                "1",
                true,
                "drpc",
                Badges: ["1 key · 4 chains"]),
            new(
                OnChainProviderTypes.InfuraEthereum,
                "Infura",
                OnChainStreamTransport.EvmWebSocket,
                "wss://mainnet.infura.io/ws/v3",
                "https://mainnet.infura.io/v3",
                "Core free plan: 3M credits/day, 500 credits/second, and one API key. Enable Ethereum Mainnet and paste only that key below. The same saved Infura key can also configure TrenchHQ Base and BNB rows, which are tested independently before activation.",
                "Infura API key",
                "Open Infura",
                "https://app.infura.io/",
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                false,
                ChainNamespaces.Eip155,
                "1",
                true,
                "infura",
                Badges: ["1 key · 3 chains"]),
            new(
                OnChainProviderTypes.QuickNodeEthereum,
                "QuickNode",
                OnChainStreamTransport.EvmWebSocket,
                string.Empty,
                string.Empty,
                "QuickNode currently offers a one-month trial rather than a permanent free Ethereum endpoint. Create or enable an Ethereum Mainnet endpoint, enter its credential-free HTTPS and WSS hosts, and paste the final endpoint token separately below. TrenchHQ has not credential-live-validated QuickNode's newer multichain endpoint mode.",
                "QuickNode endpoint token",
                "Open QuickNode Dashboard",
                "https://dashboard.quicknode.com/",
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                false,
                ChainNamespaces.Eip155,
                "1",
                true,
                "quicknode",
                OnChainProviderAccessCategory.TrialOrPaid,
                ["30-day trial", "Paid afterward", "Endpoint-specific"]),
            new(
                OnChainProviderTypes.ChainstackEthereum,
                "Chainstack",
                OnChainStreamTransport.EvmWebSocket,
                string.Empty,
                string.Empty,
                "Chainstack's free Developer plan permits one node total across the account. Create an Ethereum Mainnet node, enter its credential-free HTTPS and WSS endpoint bases, and paste the node-specific access token separately below.",
                "Chainstack node access token",
                "Open Chainstack Console",
                "https://console.chainstack.com/",
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                false,
                ChainNamespaces.Eip155,
                "1",
                true,
                "chainstack",
                Badges: ["1 free node total", "Node-specific"]),
            new(
                OnChainProviderTypes.PublicNodeEthereum,
                "PublicNode (best effort)",
                OnChainStreamTransport.EvmWebSocket,
                "wss://ethereum-rpc.publicnode.com",
                "https://ethereum-rpc.publicnode.com",
                "No key is required. This is a best-effort trial endpoint without a product SLA; configure a private provider for dependable use.",
                "No API key required",
                "Open PublicNode status",
                "https://ethereum-rpc.publicnode.com/",
                OnChainEndpointAuthenticationMode.None,
                OnChainEndpointAuthenticationMode.None,
                false,
                ChainNamespaces.Eip155,
                "1",
                false,
                "publicnode"),
            new(
                OnChainProviderTypes.CustomEthereum,
                "Custom keyless RPC endpoint",
                OnChainStreamTransport.EvmWebSocket,
                string.Empty,
                string.Empty,
                "Enter keyless HTTPS and WSS Ethereum Mainnet endpoints supplied by another provider, your organization, or a self-hosted node. Cost and reliability depend on the endpoint owner. This profile does not support a URL key, token, or authentication header.",
                "No API key required",
                string.Empty,
                string.Empty,
                OnChainEndpointAuthenticationMode.None,
                OnChainEndpointAuthenticationMode.None,
                false,
                ChainNamespaces.Eip155,
                "1",
                false,
                "custom",
                OnChainProviderAccessCategory.CustomEndpoint,
                ["Keyless only"])
        ];

        internal static readonly OnChainProviderPreset[] BasePresets =
        [
            new(
                OnChainProviderTypes.AlchemyBase,
                "Alchemy",
                OnChainStreamTransport.EvmWebSocket,
                "wss://base-mainnet.g.alchemy.com/v2",
                "https://base-mainnet.g.alchemy.com/v2",
                "Free tier: 30M compute units/month. Enable Base Mainnet for your Alchemy app, copy its app API key, and paste only the key below. The same saved key can configure all five TrenchHQ chains, which are tested independently before activation.",
                "Alchemy app API key",
                "Open Alchemy Apps",
                "https://dashboard.alchemy.com/apps",
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                false,
                ChainNamespaces.Eip155,
                "8453",
                true,
                "alchemy",
                Badges: ["Recommended", "1 key · 5 chains"]),
            new(
                OnChainProviderTypes.DrpcBase,
                "dRPC",
                OnChainStreamTransport.EvmWebSocket,
                "wss://lb.drpc.live/base",
                "https://lb.drpc.live/base",
                "Free tier: 210M compute units per 30 days over public nodes. Create a dRPC key and paste only that key below. TrenchHQ appends it to the credential-free Base endpoint base; the same saved key can configure TrenchHQ Solana, Ethereum, and BNB rows.",
                "dRPC key",
                "Open dRPC",
                "https://drpc.org/login",
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                false,
                ChainNamespaces.Eip155,
                "8453",
                true,
                "drpc",
                Badges: ["1 key · 4 chains"]),
            new(
                OnChainProviderTypes.InfuraBase,
                "Infura",
                OnChainStreamTransport.EvmWebSocket,
                "wss://base-mainnet.infura.io/ws/v3",
                "https://base-mainnet.infura.io/v3",
                "Core free plan: 3M credits/day, 500 credits/second, and one API key. Enable Base Mainnet and paste only that key below. The same saved Infura key can be reused from the Ethereum or BNB row; TrenchHQ tests Base independently before activation.",
                "Infura API key",
                "Open Infura",
                "https://app.infura.io/",
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                false,
                ChainNamespaces.Eip155,
                "8453",
                true,
                "infura",
                Badges: ["1 key · 3 chains"]),
            new(
                OnChainProviderTypes.BasePublic,
                "Base public (best effort)",
                OnChainStreamTransport.EvmWebSocket,
                "wss://base-rpc.publicnode.com",
                "https://base-rpc.publicnode.com",
                "No key is required. PublicNode provides these Base endpoints without a product SLA; use them for bounded search validation or best-effort trial only.",
                "No API key required",
                "Open PublicNode Base endpoint",
                "https://base.publicnode.com/",
                OnChainEndpointAuthenticationMode.None,
                OnChainEndpointAuthenticationMode.None,
                false,
                ChainNamespaces.Eip155,
                "8453",
                false,
                "base-public"),
            new(
                OnChainProviderTypes.ChainstackBase,
                "Chainstack",
                OnChainStreamTransport.EvmWebSocket,
                string.Empty,
                string.Empty,
                "Chainstack's free Developer plan permits one node total across the account. Create a Base Mainnet node, enter its credential-free HTTPS and WSS endpoint bases, and paste the node-specific access token separately below. TrenchHQ splits replay into at most 100-block ranges for this preset.",
                "Chainstack node access token",
                "Open Chainstack Console",
                "https://console.chainstack.com/",
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                false,
                ChainNamespaces.Eip155,
                "8453",
                true,
                "chainstack",
                Badges: ["1 free node total", "Node-specific"]),
            new(
                OnChainProviderTypes.CustomBase,
                "Custom keyless RPC endpoint",
                OnChainStreamTransport.EvmWebSocket,
                string.Empty,
                string.Empty,
                "Enter keyless HTTPS and WSS Base Mainnet endpoints supplied by another provider, your organization, or a self-hosted node. Cost and reliability depend on the endpoint owner. This profile does not support a URL key, token, or authentication header.",
                "No API key required",
                string.Empty,
                string.Empty,
                OnChainEndpointAuthenticationMode.None,
                OnChainEndpointAuthenticationMode.None,
                false,
                ChainNamespaces.Eip155,
                "8453",
                false,
                "custom",
                OnChainProviderAccessCategory.CustomEndpoint,
                ["Keyless only"])
        ];

        internal static readonly OnChainProviderPreset[] BnbPresets =
        [
            new(
                OnChainProviderTypes.AlchemyBnb,
                "Alchemy",
                OnChainStreamTransport.EvmWebSocket,
                "wss://bnb-mainnet.g.alchemy.com/v2",
                "https://bnb-mainnet.g.alchemy.com/v2",
                "Free tier: 30M compute units/month. Enable BNB Smart Chain Mainnet for your Alchemy app, copy its app API key, and paste only the key below. The same saved key can configure all five TrenchHQ chains, which are tested independently before activation.",
                "Alchemy app API key",
                "Open Alchemy Apps",
                "https://dashboard.alchemy.com/apps",
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                false,
                ChainNamespaces.Eip155,
                "56",
                true,
                "alchemy",
                Badges: ["1 key · 5 chains"]),
            new(
                OnChainProviderTypes.DrpcBnb,
                "dRPC",
                OnChainStreamTransport.EvmWebSocket,
                "wss://lb.drpc.live/bsc",
                "https://lb.drpc.live/bsc",
                "Free tier: 210M compute units per 30 days over public nodes. Create a dRPC key and paste only that key below. TrenchHQ appends it to the credential-free BNB endpoint base; the same saved key can configure TrenchHQ Solana, Ethereum, and Base rows.",
                "dRPC key",
                "Open dRPC",
                "https://drpc.org/login",
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                false,
                ChainNamespaces.Eip155,
                "56",
                true,
                "drpc",
                Badges: ["1 key · 4 chains"]),
            new(
                OnChainProviderTypes.InfuraBnb,
                "Infura",
                OnChainStreamTransport.EvmWebSocket,
                "wss://bsc-mainnet.infura.io/ws/v3",
                "https://bsc-mainnet.infura.io/v3",
                "Core free plan: 3M credits/day, 500 credits/second, and one API key. Enable BNB Smart Chain Mainnet and paste only that key below. The same saved Infura key can be reused from the Ethereum or Base row; TrenchHQ tests BNB independently before activation.",
                "Infura API key",
                "Open Infura",
                "https://app.infura.io/",
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                false,
                ChainNamespaces.Eip155,
                "56",
                true,
                "infura",
                Badges: ["1 key · 3 chains"]),
            new(
                OnChainProviderTypes.PublicNodeBnb,
                "PublicNode BNB (best effort)",
                OnChainStreamTransport.EvmWebSocket,
                "wss://bsc-rpc.publicnode.com",
                "https://bsc-rpc.publicnode.com",
                "No key is required. This is a best-effort trial endpoint; configure a private provider for dependable streaming and deeper recovery ranges.",
                "No API key required",
                "Open PublicNode endpoint",
                "https://bsc-rpc.publicnode.com/",
                OnChainEndpointAuthenticationMode.None,
                OnChainEndpointAuthenticationMode.None,
                false,
                ChainNamespaces.Eip155,
                "56",
                false,
                "publicnode"),
            new(
                OnChainProviderTypes.ChainstackBnb,
                "Chainstack",
                OnChainStreamTransport.EvmWebSocket,
                string.Empty,
                string.Empty,
                "Chainstack's free Developer plan permits one node total across the account. Create a BNB Smart Chain Mainnet node, enter its credential-free HTTPS and WSS endpoint bases, and paste the node-specific access token separately below.",
                "Chainstack node access token",
                "Open Chainstack Console",
                "https://console.chainstack.com/",
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                false,
                ChainNamespaces.Eip155,
                "56",
                true,
                "chainstack",
                Badges: ["1 free node total", "Node-specific"]),
            new(
                OnChainProviderTypes.CustomBnb,
                "Custom keyless RPC endpoint",
                OnChainStreamTransport.EvmWebSocket,
                string.Empty,
                string.Empty,
                "Enter keyless HTTPS and WSS BNB Smart Chain Mainnet endpoints supplied by another provider, your organization, or a self-hosted node. Cost and reliability depend on the endpoint owner. This profile does not support a URL key, token, or authentication header.",
                "No API key required",
                string.Empty,
                string.Empty,
                OnChainEndpointAuthenticationMode.None,
                OnChainEndpointAuthenticationMode.None,
                false,
                ChainNamespaces.Eip155,
                "56",
                false,
                "custom",
                OnChainProviderAccessCategory.CustomEndpoint,
                ["Keyless only"])
        ];

        internal static readonly OnChainProviderPreset[] RobinhoodPresets =
        [
            new(
                OnChainProviderTypes.PublicNodeRobinhood,
                "PublicNode Robinhood (best effort)",
                OnChainStreamTransport.EvmWebSocket,
                "wss://robinhood-rpc.publicnode.com",
                "https://robinhood-rpc.publicnode.com",
                "No key is required. This public endpoint provides live WebSocket pool updates without an account allowance, but has no product SLA; configure a private provider for dependable access.",
                "No API key required",
                "Open PublicNode endpoint",
                "https://robinhood.publicnode.com/",
                OnChainEndpointAuthenticationMode.None,
                OnChainEndpointAuthenticationMode.None,
                false,
                ChainNamespaces.Eip155,
                "4663",
                false,
                "publicnode"),
            new(
                OnChainProviderTypes.AlchemyRobinhood,
                "Alchemy",
                OnChainStreamTransport.EvmWebSocket,
                "wss://robinhood-mainnet.g.alchemy.com/v2",
                "https://robinhood-mainnet.g.alchemy.com/v2",
                "Free tier: 30M compute units/month. Enable Robinhood Chain Mainnet for your Alchemy app, copy its app API key, and paste only the key below. The same saved key can configure all five TrenchHQ chains, which are tested independently before activation.",
                "Alchemy app API key",
                "Open Alchemy Apps",
                "https://dashboard.alchemy.com/apps",
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                false,
                ChainNamespaces.Eip155,
                "4663",
                true,
                "alchemy",
                Badges: ["Recommended", "1 key · 5 chains"]),
            new(
                OnChainProviderTypes.QuickNodeRobinhood,
                "QuickNode",
                OnChainStreamTransport.EvmWebSocket,
                string.Empty,
                string.Empty,
                "QuickNode offers a one-month trial rather than a permanent free plan. Create or enable a Robinhood Chain Mainnet endpoint, enter its credential-free HTTPS and WSS endpoint hosts, and paste the final endpoint token separately below. TrenchHQ has not credential-live-validated QuickNode's newer multichain endpoint mode.",
                "QuickNode endpoint token",
                "Open QuickNode",
                "https://www.quicknode.com/chains/robinhood",
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                false,
                ChainNamespaces.Eip155,
                "4663",
                true,
                "quicknode",
                OnChainProviderAccessCategory.TrialOrPaid,
                ["30-day trial", "Paid afterward", "Endpoint-specific"]),
            new(
                OnChainProviderTypes.ChainstackRobinhood,
                "Chainstack",
                OnChainStreamTransport.EvmWebSocket,
                string.Empty,
                string.Empty,
                "Chainstack's free Developer plan permits one node total across the account. Create a Robinhood Chain Mainnet node, enter its credential-free HTTPS and WSS endpoint bases, and paste the node-specific access token separately below.",
                "Chainstack node access token",
                "Open Chainstack Console",
                "https://console.chainstack.com/",
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                OnChainEndpointAuthenticationMode.ApiKeyPathSegment,
                false,
                ChainNamespaces.Eip155,
                "4663",
                true,
                "chainstack",
                Badges: ["1 free node total", "Node-specific"]),
            new(
                OnChainProviderTypes.CustomRobinhood,
                "Custom keyless RPC endpoint",
                OnChainStreamTransport.EvmWebSocket,
                string.Empty,
                string.Empty,
                "Enter keyless HTTPS and WSS Robinhood Chain Mainnet endpoints supplied by another provider, your organization, or a self-hosted node. Cost and reliability depend on the endpoint owner. This profile does not support a URL key, token, or authentication header.",
                "No API key required",
                string.Empty,
                string.Empty,
                OnChainEndpointAuthenticationMode.None,
                OnChainEndpointAuthenticationMode.None,
                false,
                ChainNamespaces.Eip155,
                "4663",
                false,
                "custom",
                OnChainProviderAccessCategory.CustomEndpoint,
                ["Keyless only"])
        ];

        internal static readonly OnChainProviderPreset[] AllPresets =
            [.. Presets, .. EthereumPresets, .. BasePresets, .. BnbPresets, .. RobinhoodPresets];

        internal static bool IsSupported(string? providerType)
        {
            return Find(providerType) != null;
        }

        internal static OnChainProviderPreset Get(string providerType)
        {
            return Find(providerType)
                   ?? throw new ArgumentException("Provider type is not supported.", nameof(providerType));
        }

        internal static bool IsPublicEvaluationProvider(string? providerType)
        {
            return providerType is OnChainProviderTypes.PublicNodeEthereum
                or OnChainProviderTypes.BasePublic
                or OnChainProviderTypes.PublicNodeBnb
                or OnChainProviderTypes.PublicNodeRobinhood;
        }

        internal static OnChainProviderPreset? GetPublicEvaluationPreset(
            string chainNamespace,
            string chainId)
        {
            return AllPresets.FirstOrDefault(preset =>
                IsPublicEvaluationProvider(preset.ProviderType)
                && string.Equals(preset.ChainNamespace, chainNamespace, StringComparison.Ordinal)
                && string.Equals(preset.ChainId, chainId, StringComparison.Ordinal));
        }

        internal static bool SharesCredentialAcrossChains(OnChainProviderPreset preset)
        {
            return preset.RequiresCredential
                   && preset.ProviderFamily is "alchemy" or "drpc" or "infura";
        }

        internal static bool IsAutomaticSharedCredentialTarget(OnChainProviderPreset preset)
        {
            return SharesCredentialAcrossChains(preset)
                   && preset.StreamTransport != OnChainStreamTransport.YellowstoneGrpc;
        }

        internal static OnChainProviderPreset[] GetAutomaticSharedCredentialTargets(
            OnChainProviderPreset source)
        {
            if (!SharesCredentialAcrossChains(source))
            {
                return [];
            }
            return AllPresets.Where(candidate =>
                    IsAutomaticSharedCredentialTarget(candidate)
                    && string.Equals(
                        candidate.ProviderFamily,
                        source.ProviderFamily,
                        StringComparison.Ordinal))
                .ToArray();
        }

        internal static string GetSupportedChainsText(OnChainProviderPreset preset)
        {
            var chainNames = AllPresets
                .Where(candidate => string.Equals(
                    candidate.ProviderFamily,
                    preset.ProviderFamily,
                    StringComparison.Ordinal))
                .Select(GetChainDisplayName)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return $"Supported in TrenchHQ: {string.Join(", ", chainNames)}";
        }

        internal static string GetChainDisplayName(OnChainProviderPreset preset)
        {
            if (preset.ChainNamespace != ChainNamespaces.Eip155)
            {
                return "Solana";
            }
            return preset.ChainId switch
            {
                "8453" => "Base",
                "56" => "BNB Smart Chain",
                "4663" => "Robinhood Chain",
                _ => "Ethereum"
            };
        }

        private static OnChainProviderPreset? Find(string? providerType)
        {
            return Array.Find(AllPresets, preset =>
                string.Equals(preset.ProviderType, providerType, StringComparison.Ordinal));
        }
    }

    internal enum OnChainProviderCapabilityState
    {
        Unknown,
        Supported,
        Unsupported,
        Degraded,
        AuthenticationRejected,
        RateLimited
    }

    internal sealed class OnChainProviderCapabilitySnapshot
    {
        public long ObservedAtUnixMs { get; set; }
        public OnChainProviderCapabilityState ChainId { get; set; }
        public OnChainProviderCapabilityState SafeBlock { get; set; }
        public OnChainProviderCapabilityState FinalizedBlock { get; set; }
        public OnChainProviderCapabilityState BlockHashCall { get; set; }
        public OnChainProviderCapabilityState BlockHashLogs { get; set; }
        public OnChainProviderCapabilityState Batch { get; set; }
        public OnChainProviderCapabilityState WebSocketHeads { get; set; }
        public OnChainProviderCapabilityState WebSocketLogs { get; set; }
    }

    internal sealed class OnChainProviderConfiguration
    {
        internal IOnChainUsageScope? Usage { get; set; }
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string ChainNamespace { get; set; } = ChainNamespaces.Solana;
        public string ChainId { get; set; } = "mainnet-beta";
        public string ProviderType { get; set; } = OnChainProviderTypes.AlchemyWebSocket;
        public string? FriendlyName { get; set; }
        public string StreamEndpoint { get; set; } = string.Empty;
        public string RpcEndpoint { get; set; } = string.Empty;
        public string? Region { get; set; }
        public string CredentialReference { get; set; } = Guid.NewGuid().ToString("N");
        public OnChainCommitment Commitment { get; set; } = OnChainCommitment.Processed;
        public bool ReplayEnabled { get; set; }
        public OnChainProviderCapabilitySnapshot? CapabilitySnapshot { get; set; }
    }

    internal sealed class OnChainProviderConfigurationDocument
    {
        public int Version { get; set; } = 2;
        public Dictionary<string, string> SelectedConfigurationIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string[]> FallbackConfigurationIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string? SelectedConfigurationId { get; set; }
        public OnChainProviderConfiguration[] Configurations { get; set; } = [];
    }
}

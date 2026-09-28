# Services and integrations

TrenchHQ brings exchange prices, on-chain markets, wallet activity and X feeds
onto the desktop. Requests go directly from the user's device to the services
needed by their chosen widgets.

## What each feature needs

| Feature | Connection |
| --- | --- |
| Exchange prices | Public spot feeds; no exchange account or API key |
| Ethereum, Base, BNB Smart Chain and Robinhood Chain | A compatible provider, with PublicNode as the key-free fallback |
| Solana prices and wallets | A configured provider with the required RPC/stream access |
| X Tracker | The user's paid official X API access |
| Website | The site's normal connection and login, if required |

See [setup and help](public/support.md) for supported markets and everyday use.
A provider account may require endpoint URLs as well as a key, and some features
need a particular plan.

## Connections and usage

Add provider details in **API Connections**. TrenchHQ chooses compatible connections
and available backups automatically, keeping the selected polling or streaming
mode. Supported providers share a saved key across their linked chains.

**Usage** shows local estimates. There is no app-imposed allowance or reset cycle;
you can enable your own limit if you want one. Actual provider quotas and rate
limits still apply, and TrenchHQ cannot see usage from your other applications.
If no suitable connection is available, the affected feed pauses visibly.

Price tickers offer **Every second (polling)** or **Live streaming** where supported.
Polling reads the current price; streaming follows events and its usage depends
on market activity. Wallet Watcher follows new activity while connected, without
loading old transactions at startup or replaying an outage.

## RPC and streaming providers

These integrations use the user's own access. TrenchHQ does not supply paid
subscriptions or resell a shared data feed. Endpoint capabilities and account
terms determine what is available.

| Service | Exposed use | Official terms and release status |
| --- | --- | --- |
| Alchemy | User-key HTTP/WebSocket RPC | [Terms](https://www.alchemy.com/terms-conditions/terms); user's own plan and limits apply. |
| Helius | Solana HTTP/WebSocket and configured stream services | [Terms](https://www.helius.dev/terms); account/endpoint entitlements must be certified with fresh credentials. |
| QuickNode | User-configured RPC/stream endpoints | [Terms](https://www.quicknode.com/terms); no bundled subscription or shared account. |
| Chainstack | User-configured HTTP/WebSocket/gRPC where supported | [Terms](https://chainstack.com/tos/); endpoint and plan restrictions apply. |
| dRPC | Shared user key across compatible chains | [Terms](https://drpc.org/terms-of-use); account terms review remains open. |
| Infura | User-key supported EVM RPC | [Consensys terms](https://legal.consensys.io/plain/terms-of-use/); project and service-specific terms apply. |
| Shyft | Solana RPC/gRPC configuration | [Terms](https://shyft.to/terms); website text alone did not establish a specific redistribution grant. Confirm account terms before claiming certification. |
| Triton | Solana user endpoint/Yellowstone | [Policies](https://triton.one/policies); contracted endpoint entitlements apply. |
| PublicNode | Key-free fallback for Ethereum, Base, BNB and Robinhood Chain | [Terms](https://www.publicnode.com/terms) permit revocable RPC access; no availability promise or bypass of blocking. |
| Solana public RPC | Bounded discovery only | [Official limits](https://solana.com/docs/references/clusters); no key-free live ticker/wallet fallback is promised. |
| Custom endpoints | User-specified compatible RPC or stream | Operator terms unknown; user must have access rights. No certification implied by saving a URL. |

## Centralized exchanges

The sidecar exposes public market discovery and ticker delivery through locked
CCXT, with no exchange credentials. Public APIs are used directly; no scraping,
order submission, balance access or trading-account login is exposed. The sources
below identify each integration. Exchange-specific data/branding/territory terms
must be reviewed for the release markets; this table is not a blanket legal grant.

| Exchange | Official API source | Application scope / terms gate |
| --- | --- | --- |
| Binance | [Spot API](https://developers.binance.com/en/docs/products/spot/CHANGELOG) | Public spot markets/tickers; API terms and regional access apply. |
| Bybit | [V5](https://bybit-exchange.github.io/docs/v5/intro) | Public spot markets/tickers; service terms and regional access apply. |
| OKX | [V5](https://www.okx.com/docs-v5/en/) | Public spot markets/tickers; data-use/territory review remains open. |
| Gate | [V4](https://www.gate.com/docs/developers/apiv4/en/) | Public spot markets/tickers; API agreement applies. |
| KuCoin | [Documentation](https://www.kucoin.com/docs-new) | Public spot markets/tickers; data-use/territory review remains open. |
| Bitget | [Documentation](https://www.bitget.com/api-doc/common/intro), [API terms](https://www.bitget.com/support/articles/12560603797947) | Public spot markets/tickers; restrictions on competing clients, repackaging and benchmarking need a release-scope decision. Review remains open. |
| BingX | [Documentation](https://bingx-api.github.io/docs/) | Public spot markets/tickers; data-use/territory review remains open. |
| HTX | [Spot documentation](https://huobiapi.github.io/docs/spot/v1/en/) | Public spot markets/tickers; data-use/territory review remains open. |
| MEXC | [Spot V3](https://www.mexc.com/api-docs/spot-v3/introduction) | Public spot markets/tickers; data-use/territory review remains open. |
| Crypto.com | [Exchange API](https://exchange-docs.crypto.com/exchange/v1/rest-ws/index.html) | Public spot markets/tickers; data-use/territory review remains open. |
| CoinEx | [V2](https://docs.coinex.com/api/v2) | Public spot markets/tickers; data-use/territory review remains open. |
| HashKey Global | [Documentation](https://hashkeyglobal-apidoc.readme.io/) | Public spot markets/tickers; data-use/territory review remains open. |
| WOO X | [Documentation](https://developer.woox.io/) | Public spot markets/tickers; data-use/territory review remains open. |

CCXT's [MIT license](https://docs.ccxt.com/about-us) covers its implementation,
not exchange data rights. The local live gate records current technical reachability
separately from permission. No exchange logo permission is inferred from CCXT.

## Discovery, protocols and content

| Service/content | Actual use | Release decision |
| --- | --- | --- |
| DEX Screener | Token/pool discovery metadata, subsequently validated on-chain | **Open release blocker:** its [API terms](https://docs.dexscreener.com/api/api-terms-and-conditions) restrict directly competing products. Obtain a scope decision/permission or replace this discovery dependency before an end-user launch; no permission has been requested or obtained. |
| Meteora DAMM catalogs | Public pool catalog hints; on-chain validation | `damm-api.meteora.ag`, `damm-v2.datapi.meteora.ag`; service/data-use review remains open. No Meteora logo is published. |
| Manifest stats | Public pool catalog hints; on-chain validation | `mfx-stats-mainnet.fly.dev`; service/data-use review remains open. |
| Curve API | Public pool discovery; on-chain validation | `api.curve.finance`; service/data-use review remains open. |
| Pancake Infinity explorer | Public pool discovery; on-chain validation | `explorer.pancakeswap.com`; service/data-use review remains open. |
| Robinhood Stock Token API | Public token metadata; RPC validates actual pool state | [Official documentation](https://docs.robinhood.com/); reference metadata does not provide brokerage access or rights to token trading. Service/data-use review remains open. |
| On-chain protocols | Decode public state/events via configured RPC | Raydium, Orca, Pump, Meteora, Manifest, Uniswap, Aerodrome, PancakeSwap, Curve, ShibaSwap, Doppler/long.xyz and pons interoperability; no contract transactions are submitted. Third-party contract implementations are not bundled. |
| X | Official paid Filtered Stream under user's own developer access | [Agreement](https://developer.x.com/developer-terms/agreement), [policy](https://developer.x.com/developer-terms/policy), [display requirements](https://developer.x.com/developer-terms/display-requirements); app display/caching and fresh paid access need release certification. No unofficial X adapter. |
| Website panels | User-chosen website in local WebView2 profile | Each site's terms, logins, cookies and content permissions apply. No paywall bypass or data redistribution service is provided. |
| Chain marks / coin icons | Local identifying assets | See [third-party notices](../THIRD_PARTY_NOTICES.md). Protocol support uses text names; Pump and Meteora logos are not included. |

## Before distribution

The publisher must resolve service-use and branding questions for the launch
territories and account plans. API access and an SDK license do not establish
permission to redistribute data. Keep this review separate from technical tests.

- **DEX Screener:** resolve the competing-product restriction for pool discovery,
  obtain permission if needed, or replace the dependency.
- **Bitget:** resolve the API restrictions on competing clients, repackaging and
  benchmarking for public price display and release testing.
- **Other exchanges, catalogs and providers:** complete the data-display,
  territory and account-specific reviews identified above.
- **X:** check the official display, caching and account requirements.
- **Brand assets:** follow the [third-party notices](../THIRD_PARTY_NOTICES.md).
  Adding Pump or Meteora logos requires permission or applicable brand terms first.

These reviews remain open; no third-party approval is claimed. See the
[release plan](RELEASE.md) for distribution gates and the [privacy policy](public/privacy.txt)
for service recipients, local storage and deletion.

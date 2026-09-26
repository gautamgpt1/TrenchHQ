# Services and constraints

Review date: 2026-09-26. This records the actual read-only integration boundary
and official review sources. Public reachability, an open-source SDK license and
a successful test do not grant redistribution rights. The app makes requests
from the user's device; it does not resell a shared provider account or data feed.
Regional restrictions, account terms, attribution and rate limits still apply.

## Implemented scope

Networks: Solana, Ethereum, Base, BNB Smart Chain and Robinhood Chain. Production
fixtures cover 34 on-chain presets. Account entitlements and custom endpoints
still need actual certification. Public reachability does not prove a paid plan.

See [user support](public/support.md) for exchange/protocol coverage and recovery,
[architecture](ARCHITECTURE.md) for acquisition/failover, and [release status](RELEASE.md)
for launch gates. This is an integration inventory, not legal clearance.

## RPC and streaming providers

All configured chains use the same automatic health/local-budget selection policy.
Only compatible profiles are eligible. Account-specific URLs may be required in
addition to a key. Account balances are not inferred from local usage estimates.
Failover must never be represented as permission to evade an account suspension.

| Service | Exposed use | Official terms and release status |
| --- | --- | --- |
| Alchemy | User-key HTTP/WebSocket RPC | [Terms](https://www.alchemy.com/terms-conditions/terms); user's own plan and limits apply. |
| Helius | Solana HTTP/WebSocket and configured stream services | [Terms](https://www.helius.dev/terms); account/endpoint entitlements must be certified with fresh credentials. |
| QuickNode | User-configured RPC/stream endpoints | [Terms](https://www.quicknode.com/terms); no bundled subscription or shared account. |
| Chainstack | User-configured HTTP/WebSocket/gRPC where supported | [Terms](https://chainstack.com/tos/); endpoint and plan restrictions apply. |
| dRPC | Shared user key across compatible chains | [Terms](https://drpc.org/terms-of-use); page is script-rendered and full text was not retrievable in this review. Manual terms review remains open. |
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
| OKX | [V5](https://www.okx.com/docs-v5/en/) | Public spot markets/tickers; documentation fetch failed during this review, manual review remains open. |
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
| Chain marks / coin icons | Local identifying assets | See [third-party notices](../THIRD_PARTY_NOTICES.md). Protocol favicon downloads are removed. |

Before distribution, the publisher must resolve the entries marked open for the
actual launch scope. Technical certification, license collection and this inventory
do not replace that decision. No permission request or third-party approval is implied by this review.

---
layout: default
title: TrenchHQ setup and help
---
# Setup and help

TrenchHQ puts the information you follow on your Windows desktop. A **widget**
is what you watch; a **panel** is where you see it. You can place the same saved
widget in several panels, each with its own size and appearance.

The first signed Windows download is being prepared. To try the current app,
follow [Build from source](https://github.com/gautamgpt1/TrenchHQ/blob/main/docs/BUILD.md)
on Windows x64.

## Set up your first ticker

1. In **Widgets**, select **+** and choose **Price Ticker**.
2. Search for a coin or pair, select a market from an exchange, then **Save**.
3. In **Desktop Setup**, add an **Overlay** or a screen-edge panel.
4. Choose your ticker under **Panel content**, then **Save** and **Show**.

Public exchange prices need no API key or exchange login. To open several panels
together, select them on **Dashboard** and choose **Start Desktop Display**.

## Choose where it appears

Use an **Overlay** for a floating window, or a screen-edge panel to reserve space
along the top, bottom, left or right of a monitor. In **Desktop Setup**, choose
the monitor, position, size and appearance.

Use **Show** to open a panel. Minimize or its shortcut hides the running panel;
**Close** ends that instance, so use Show or Start to reopen it.
**Stop Desktop Display** closes the panels and releases their reserved screen space.
You can reopen the dashboard from the notification-area icon. Use **Quit TrenchHQ**
there to exit the app completely.

## Add the information you want

### Watch an on-chain price

In a Price Ticker, search by token name, symbol or contract address. Choose the
chain and supported pool you want. Check the contract and quote currency:
similarly named tokens and different pools can have different prices.

The toolbar offers **Every second (polling)** and **Live streaming** for on-chain
prices. Polling checks the current state, waits for that request to finish, then
waits one second before checking again. Streaming follows incoming events.
Some pools provide trade-based prices and require streaming. Costs depend on
your provider and market activity; neither mode is always cheaper. Exchange
markets use their own feed.

### Follow a wallet

Create a **Wallet Watcher**, choose the chain and enter a public wallet address.
Add an optional label, save it and assign the widget to a panel. Provider
requirements apply to wallet activity too.

Wallet Watcher shows supported activity, not a complete account statement or
portfolio valuation. Updates can arrive after the original transaction.
TrenchHQ does not need a seed phrase, private key or wallet-signing permission.

### Follow accounts on X

Configure your official X developer bearer token in the app's API settings.
Create an **X Tracker**, add accounts, choose any post-type/text filters, enable
**Load posts through the official X API**, then save and assign it to a panel.

This needs your own paid X API access. Filters affect what you see; they do not
reduce X API charges. To show the x.com website instead, use a Website widget.

### Keep a website open

Create a **Website** widget with an HTTPS address. Save it, assign it to a panel
and sign in on the page if needed. The site keeps its own account and subscription
requirements. Each website widget has a separate browser profile; panels sharing
that widget share its login.

**Reload** refreshes the page. **Open in browser** opens it in your normal browser.
The website's **Lock** blocks clicks and typing while the page keeps running.

### Pin an application

In **Desktop Setup**, choose **Application Window** as the panel content and
select an eligible running app. You choose the target each session; it is not
automatically repinned next time.

This feature is experimental and works with compatible resizable, non-admin
x64 apps. Unpinning, minimizing or closing the panel restores the app window.
TrenchHQ attaches a native helper to the selected app to keep it positioned.
Some custom windows are incompatible; a frozen target must respond again before
restoration can finish. Pinning is not a security boundary. See the
[technical details](https://github.com/gautamgpt1/TrenchHQ/blob/main/docs/ARCHITECTURE.md#application-window)
if you need them.

## Connect data providers

Public exchange prices work without keys. On Ethereum, Base, BNB Smart Chain and
Robinhood Chain, PublicNode can provide on-chain access without a key, subject
to its availability and limits. Solana tickers and wallet activity need a
configured provider.

Add provider details in **API Connections**. Some services need a project key;
others also need the endpoint URL from your provider dashboard. Save compatible
providers and TrenchHQ chooses which to use for each chain, including backups
when needed. It keeps your selected polling/streaming mode during a switch.

**Usage** shows local estimates and configured limits. These are not the account's
actual remaining balance, especially if other apps share the same key. Use the
provider dashboard for billing and account-wide usage. See
[Services and integrations](https://github.com/gautamgpt1/TrenchHQ/blob/main/docs/SERVICES.md)
for provider coverage and detailed behavior.

## Supported markets

### Exchanges

Public spot prices: Binance, Bybit, OKX, Gate, KuCoin, Bitget, BingX, HTX, MEXC,
Crypto.com, CoinEx, HashKey Global and WOO X. Listings and availability depend
on the exchange, your region and service outages.

### Chains and pools

Solana, Ethereum mainnet, Base, BNB Smart Chain and Robinhood Chain are integrated.

| Chain | Supported protocol families |
| --- | --- |
| Solana | Pump/PumpSwap, Raydium, Meteora, Orca and Manifest |
| Ethereum | Uniswap, ShibaSwap, Curve and FermiSwap |
| Base | Uniswap, Aerodrome and PancakeSwap |
| BNB Smart Chain | PancakeSwap and Uniswap |
| Robinhood Chain | Uniswap, long.xyz/Doppler and pons |

Support applies to validated pool types and deployments, not every pool or hook
from a named protocol. A token appearing in search does not mean all its pools
are supported. Streaming cannot guarantee delivery of every event during an outage.

### Windows

The app currently targets Windows x64; ARM64 and 32-bit Windows are not supported.
The tested Windows versions for the first public installer will be listed when
it is released. The package's technical minimum alone is not a compatibility
promise.

## When something goes wrong

| Problem | Try this |
| --- | --- |
| Dashboard disappeared | Open TrenchHQ from its notification-area icon. |
| A closed panel does not return with its shortcut | Use Show or Start Desktop Display to create it again. |
| A price/feed stopped updating | Check your network and API Connections for rejected credentials or usage limits. Recovery is automatic when a compatible route is available; stale data is not a current price. |
| Website will not load or sign in | Check the HTTPS address, Reload, or use Open in browser. Some sites block embedded sign-in. |
| App asks for WebView2 | Install or repair [Microsoft's Evergreen WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/), then retry. |
| Pinned app does not restore immediately | Let a frozen target recover; then unpin or close the panel. |
| Controls are cut off in a very narrow window | Widen the dashboard. Some widget-editor controls currently need more horizontal room. |

## Get help or report a bug

Ask in [Q&A](https://github.com/gautamgpt1/TrenchHQ/discussions/categories/q-a), or
[report a bug](https://github.com/gautamgpt1/TrenchHQ/issues/new/choose). Include
the app version/source commit, Windows version, steps and expected/actual result.

**Help and FAQ → Save diagnostics** creates an optional local JSON export.
Review it before sharing. Do not attach settings folders, raw logs, API keys,
wallet secrets, browser data or screenshots containing private information.

The [privacy policy](privacy.txt) explains storage and deletion. Report security
vulnerabilities through the
[private reporting form](https://github.com/gautamgpt1/TrenchHQ/security/advisories/new).

## Downloads and updates

Use only the official installation links when a signed release is announced.
Store and direct-download installations may use different identities; do not
assume that switching channels transfers settings or updates an existing install.
Current distribution status is in the
[release plan](https://github.com/gautamgpt1/TrenchHQ/blob/main/docs/RELEASE.md).

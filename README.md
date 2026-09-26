<img src="Assets/TrenchHQ.svg" width="64" height="64" alt="TrenchHQ">

# TrenchHQ

Read-only crypto market panels for your Windows desktop.

Keep prices, public wallet activity, websites and an optional X feed visible in
floating overlays or panels docked to any screen edge. Configure saved widgets
once and use them across multiple panels.

**Pre-release source · Windows x64 · version 1.0.1.0**

No public binary is available yet. Microsoft Store is the planned primary binary
channel; a trusted signed direct download is conditional on signing approval.
There is no wallet custody, transaction signing, exchange-account access or trading.

## What you can use

| Content | Purpose | Access |
| --- | --- | --- |
| Price Ticker | Public exchange prices and supported on-chain pools | Public exchange prices need no key; on-chain requirements vary |
| Wallet Watcher | Read-only activity for public wallet addresses | Compatible chain provider |
| X Tracker | Native feed using the official X Filtered Stream | Your own authorized paid X API access |
| Website | An HTTPS site in its own local WebView2 profile | The site's own login, subscription and terms |
| Application Window | Pin a selected app into a panel | Experimental; compatible resizable, non-admin x64 apps |

Application Window loads a native helper into the selected application. Read the
[implementation and limitations](docs/ARCHITECTURE.md#application-window) before
using or changing it.

## Start without an API key

After building and launching the app:

1. Open **Widgets** and save a **Price Ticker** with a public exchange and spot pair.
2. In **Desktop Setup**, assign the widget to an overlay or screen-edge panel.
3. Select **Show**, or start the selected panels from **Dashboard**.
4. Use **Help and FAQ** for setup, recovery, version, privacy and diagnostics.

The integrated chains are **Solana, Ethereum, Base, BNB Smart Chain and Robinhood
Chain**. PublicNode is a best-effort EVM fallback. Solana live data needs a configured
provider. Regional restrictions, quotas and outages can affect any public feed.
See [supported feeds and recovery](docs/public/support.md).

## Provider selection and usage

Enter your provider key and save it. TrenchHQ chooses among compatible configured
providers for each chain and recovers from failures or exhausted local allowances.
Some providers also need an account-specific endpoint URL.

Price Ticker defaults to one-second current-state polling; live events are an
explicit option. Automatic provider changes preserve that choice. Usage guards
are local estimates, **not your account's remaining balance**. They cannot account
for other applications sharing your subscription or guarantee a free allowance
will last all month. See [services and constraints](docs/SERVICES.md).

Credentials stay in the app's protected Windows configuration. Never put API keys,
wallet secrets, browser profiles or personal configuration in the repository.

## Build and contribute

This is a WinUI 3 / .NET application with a Rust engine and a Node/CCXT sidecar.
Start with [build and verification instructions](docs/BUILD.md), including the
locked toolchain and dependency bootstrap. Open `TrenchHQ.slnx` in Visual Studio.

- [Contributing](CONTRIBUTING.md)
- [Architecture and behavior](docs/ARCHITECTURE.md)
- [Implementation status and release plan](docs/RELEASE.md)
- [Security reporting](SECURITY.md)
- [Privacy policy](docs/public/privacy.txt)

Use this repository's Issues tab for reproducible bugs and feature requests.
Review diagnostics before attaching them. Security-sensitive reports follow
`SECURITY.md`; do not post secrets or exploit details in public issues.

## License and attribution

TrenchHQ source and original artwork use the [MIT license](LICENSE), copyright
2026 Gautam Gupta. Dependencies and identifying marks retain their own terms:
[third-party notices](THIRD_PARTY_NOTICES.md) and
[dependency inventory](ThirdParty/DEPENDENCIES.json).

Software licenses do not grant rights to exchange data, provider accounts or
third-party branding. Open service-use questions are recorded in
[services and constraints](docs/SERVICES.md); source availability does not imply
provider endorsement or certification.

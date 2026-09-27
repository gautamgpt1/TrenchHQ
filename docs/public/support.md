---
layout: default
title: TrenchHQ support
---
# TrenchHQ support and limitations

TrenchHQ 1.0.1.0 is a release candidate for **x64 Windows**, for read-only monitoring. It cannot sign transactions, hold wallet keys, log in to exchange accounts or trade. A build is not a public release until its signing and clean-machine gates pass.

## First run without a key

1. Open **Widgets**, create a **Price Ticker**, and choose an exchange and public spot pair.
2. Save, open **Desktop Setup**, create a panel or overlay, and assign that widget.
3. Select **Show**, or select panels on Dashboard and **Start Desktop Display**.
4. Use **Help** for privacy, version information, recovery and local diagnostic export.

PublicNode provides best-effort on-chain access on Ethereum, Base, BNB Smart Chain and Robinhood Chain. Solana live tickers/wallets require a configured RPC provider. X Tracker requires the user's official paid X API access. Website displays an HTTPS site with that site's permissions and subscription requirements. A key is never required for basic public exchange prices.

## Supported scope

Public spot exchanges: Binance, Bybit, OKX, Gate, KuCoin, Bitget, BingX, HTX, MEXC, Crypto.com, CoinEx, HashKey Global and WOO X. Current validation results accompany each candidate; regional restrictions, delistings, API changes and outages can affect any exchange or pair. These names identify interoperability, not endorsement.

Chains: Solana, Ethereum mainnet, Base, BNB Smart Chain and Robinhood Chain. Arbitrum, other networks, x86 and ARM64 are not supported. The installer has a technical Windows build floor of 17763; this does not certify every old Windows edition. Public support is limited to Microsoft-serviced x64 Windows editions actually tested for the released candidate. The oldest supported edition remains unannounced until that gate passes.

Protocol scope includes validated Pump/PumpSwap, Raydium, Meteora, Orca and Manifest pools on Solana; allowlisted Uniswap, ShibaSwap, Curve and FermiSwap on Ethereum; Uniswap, Aerodrome and PancakeSwap on Base; PancakeSwap and Uniswap on BNB; and Uniswap, long.xyz/Doppler and pons on Robinhood. Not every pool, hook or deployment is supported. Unsupported candidates must not display guessed prices.

Polling samples current state with a delay of at least one second after each completed sample. It does not show every trade. Live events may cost more and do not imply lossless delivery across outages. Execution-only pools require events. Provider usage guards are local estimates, not account balances; other applications may share an account allowance. Paid access, rate limits and provider outages remain the provider's responsibility.

## Recovery

Use the notification-area icon to reopen the dashboard. Stop Desktop Display releases panels and appbar space. Minimize is reversible; Close requires Show or Start to create a new instance. Network recovery is automatic where a compatible route remains; unavailable/stale data must not be treated as a current price. Check the API page for rejected credentials or exhausted allowances.

For Website failures, reload, check the HTTPS address or use Open in browser. Some sites do not support embedded sign-in. WebView2 Runtime must be available; install or repair Microsoft's Evergreen WebView2 Runtime from Microsoft's official download page when instructed. Do not download runtime DLLs from unofficial sites.

Application Window is experimental for compatible resizable, non-admin x64 applications. Pinning is explicit and session-only. Unpin or close/minimize the panel to restore the application. Native lock and Embedded + lock are not security boundaries and can be incompatible with custom window behavior. A hung target delays recovery. The candidate's native-window restoration gate must pass before advertising full compatibility.

## Help and data

[Privacy policy](privacy.txt) explains credentials, website sessions, external services and deletion. Select **Save diagnostics** in Help and FAQ, review the saved JSON and attach it to [GitHub Issues](https://github.com/gautamgpt1/TrenchHQ/issues). Include version, Windows version, reproduction steps and expected/actual behavior. Do not attach settings folders, raw logs, screenshots with private data, provider tokens or wallet secrets.

Do not install an unsigned or self-signed package from a public download. Store and direct-signed package identities can differ; cross-channel updates and data transfer are not guaranteed. Rollback is a newly signed higher-version replacement, not forcing an older version over saved data.

The [code signing policy](https://github.com/gautamgpt1/TrenchHQ/blob/main/docs/RELEASE.md#code-signing-policy) records the maintainer's approval process and current signing status. No SignPath approval or public signed binary is claimed yet.

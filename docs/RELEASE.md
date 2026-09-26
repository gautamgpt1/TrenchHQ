# Implementation status and release plan

Current source version: **1.0.1.0**, Windows x64, pre-release. Source publication,
signed binaries and Store certification have separate acceptance gates. This
document is the maintained plan; update it as evidence changes.

## Implemented

- Shared saved widgets, overlays and panels docked to all screen edges.
- Public CEX tickers, five-chain on-chain tickers, public Wallet Watcher activity,
  official X Tracker, Website and explicit Application Window pinning.
- Automatic compatible-provider selection, local usage guards and recovery while
  preserving polling/event mode.
- Startup control, tray recovery, panel shortcuts, sizing and fullscreen behavior.
- DPAPI credentials, bundled privacy/help/version and aggregate diagnostics export.
- Locked dependencies, original application artwork, dependency/license inventory,
  source/candidate tooling, verification workflow and manual Pages workflow.

Application-code checks recorded on 26 September 2026: zero-warning/error x64
Release build; 92 logic and 16 social checks; sidecar protocol/lifecycle; full
on-chain integration including 34 provider presets; Rust format/Clippy/136 tests;
both native pin modes and six cache checks. Public live validation passed 13/13
exchanges and all four EVM PublicNode chains at that time. This is dated evidence,
not a future availability guarantee or private-account certification.

Fresh local development registration also exercised no-key tickers, Help/privacy,
diagnostic export, panel/quit cleanup and unregistration. It does not establish
signed clean-machine installation. No official binary, Store acceptance, SignPath
approval or hosted CI pass is currently claimed.

Source-publication checks on 27 September 2026: a clean 940-file export restored
locked dependencies and built x64 Release with zero warnings/errors in a separate
validation directory. Application source matched the tested source above. All 150
recorded dependency-notice hashes passed; Git filters preserved all 158 retained
notice files with automatic line-ending conversion enabled and disabled. Relative
documentation links and the source allowlist passed. Gitleaks 8.30.1 returned 20
reviewed public token/mint fixture matches and no credential findings. Historical
Git metadata, private captures and build outputs were excluded. The exact initial
commit still needs its own review after repository initialization.

## 1. Publish the source repository

- [x] Keep current source, tests, necessary assets and exact dependency locks.
- [x] Consolidate documentation; retain privacy, security, contribution guidance,
  dependency licenses and attribution.
- [x] Exclude old Git metadata, session reports, local configuration, private
  captures, logs, temporary evidence and generated output.
- [x] Confirm the destination repository and initial commit author/email:
  `gautamgpt1/TrenchHQ`, Gautam Gupta, GitHub's account-linked no-reply email.
- [ ] Inspect and scan the exact initial commit before pushing. Ignore rules alone
  do not remove already tracked secrets. Do not import old development history.
- [x] Enable Issues and private vulnerability reporting.
- [ ] Publish as pre-release source and observe the first verification workflow run.
- [x] Confirm app/site support links match the final repository URL.
- [ ] Set Pages source to GitHub Actions, run the manual Pages workflow and verify
  the privacy/support pages publicly. Review deployment permissions first.

GitHub documents [Pages configuration](https://docs.github.com/en/pages/getting-started-with-github-pages/configuring-a-publishing-source-for-your-github-pages-site).
Source publication does not grant permission to use third-party services outside
their terms. Retain and resolve the integration questions in [SERVICES](SERVICES.md).

## 2. Prepare a signed binary release

- [ ] Resolve launch-scope API/data/brand questions, particularly DEX Screener and
  Bitget. Obtain permission or replace affected integrations where required.
- [ ] Independently validate the reserved Store identity and suitable account type
  in Partner Center. Explain optional RPC project credentials accurately.
- [ ] Record the exact commit, dependencies, version and artifact hashes. Inspect
  identity, native architectures, licenses, payload and secret-scan findings.
- [ ] Obtain Store certification/signing or an approved trusted direct signer.
  SignPath Foundation is conditional; eligibility and acceptance are not assumed.
- [ ] Inspect the signed artifact and signature chain; repeat Defender scanning.
- [ ] Complete authorized real-provider/X certification with newly supplied keys
  through protected configuration. Keep inaccessible plans visibly unclaimed.
- [ ] Run WACK and the signed clean-Windows matrix below; record actual failures.
- [ ] Observe a small test ring before broad binary distribution.

Microsoft Store is the primary binary channel. Direct distribution needs its own
trusted identity and update path. Different package families/signers cannot be
assumed to share data or cross-update. Never publish unsigned/self-signed binaries.

## Certification notes

The product is read-only: no wallet signing/custody, exchange-account access,
orders, withdrawals, swaps or mining. It needs `runFullTrust` for desktop windows,
appbars, tray/shortcuts, local Node/Rust workers and user-selected native window
integration. It runs at the user's integrity level without requesting elevation,
installing a driver/service or exposing a local network server.

Disclose Application Window's `SetWindowsHookEx` injection into the chosen app.
Eligibility checks inspect metadata; do not claim no process inspection. There is
no input/screen capture, foreign-memory reading, credential extraction or network
transmission by the helper. Detach/recovery is session-based, but the DLL cache and
mapped helper can remain. See [ARCHITECTURE](ARCHITECTURE.md#application-window).
If Store review rejects it, evaluate a separately tested Store variant with the
feature and helper excluded; that variant does not exist yet.

RPC credentials are optional data-provider project keys; X uses a developer bearer
token. Neither is a wallet private key or exchange-account secret. Basic public
exchange monitoring must work without payment or keys. Reviewer path: create a
Price Ticker in Widgets, choose an exchange/pair, assign it to an overlay in
Desktop Setup, then Show. Help supplies privacy, recovery, version and diagnostics.

Disclose that bundled CCXT contains unused private/trading/cryptographic code.
The public sidecar exposes only `getMarkets`, `setSubscriptions`, `ping` and
`shutdown`, with no arbitrary method invocation. Production-bundle tests reject
private methods and inherited properties. Reassess bundle size/scope if review
raises objections; do not describe all of CCXT as public-method-only code.

Submit exact source/artifact records, dependency inventory, signature/Store
validation, Defender/WACK results, provider results and clean-machine evidence.
Privacy/support URLs must be live. Sources: [Store policies](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies),
[capabilities](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations),
[SignPath conditions](https://signpath.org/terms).

## Signed clean-Windows matrix

Use disposable, properly licensed Windows installations with the exact signed
artifact: a current Windows 11 x64 edition and the oldest serviced Windows SKU
the release will advertise. The manifest's technical 17763 floor is not a promise
to support every old Windows edition. Record OS/runtime versions, timestamps,
artifact hashes and sanitized observations. Do not mark unavailable tests passed.

| Check | Required evidence |
| --- | --- |
| Install and first run | Trusted install without Developer Mode/test certificate; no-key ticker, Help/privacy/version/export work |
| WebView2 | Runtime present/absent; clear recovery and official runtime installation; Website load |
| Startup | Toggle both ways, respect DisabledByUser/Policy, reboot into selected panels only when enabled |
| Update | Same-family lower signed test version, synthetic settings/credentials, upgrade preserves state |
| Interrupted update | VM snapshots and controlled interruption; one complete version stays launchable |
| Rollback replacement | Last-known-good source with a new higher version and same approved identity; preserve state |
| Identity/channel | Store/direct coexistence and update tested; no assumed DPAPI migration |
| Quit/uninstall | No orphan processes, appbar reservation, startup registration or unusable pinned target; data/cache behavior matches privacy |
| Explorer restart | Tray/appbar recovery and eventual cleanup, inside a VM |
| Sleep/network | Resume and offline/network-switch recovery; no busy retry or stale quote presented as current |
| Display/DPI | Monitor/scaling/resolution changes; reachable panels, fullscreen rules and cleanup |
| Application Window | Both methods; supported disposable apps, crash/hang recovery, original bounds/styles/state restored |
| Soak | At least 24 hours; resource trends, provider usage, correctness and failure/restart observations |

WACK needs an appropriate administrator test session; preserve its report and
failure details. A local development registration is not this matrix.

## Ring and rollback

After install/data-safety gates pass, start with the publisher and 2–5 consenting
testers for at least 72 hours, including the 24-hour soak. Use one known signed
version, redacted reports and no automatic diagnostic uploads. Expand only after
reviewing correctness, stability, cleanup and provider-budget behavior.

Stop distribution for data loss, secret disclosure, incorrect/stale prices shown
as current, appbar leaks, failed updates or sustained crash/resource regressions.
Restore the last-known-good source, assign a new higher version, build/sign/test
again and publish through the same channel. Never force a downgrade over user
data or reuse a published version for different bits.

Trading, wallet custody/signing, hosted execution, additional chains and other
architectures require separate product, security and publisher review. They are
not implied commitments for this first release.

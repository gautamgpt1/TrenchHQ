# Release plan

## Current status

The [source repository](https://github.com/gautamgpt1/TrenchHQ),
[help site](https://gautamgpt1.github.io/TrenchHQ/) and
[privacy policy](https://gautamgpt1.github.io/TrenchHQ/privacy.txt) are public.
There is no signed public installer yet.

The first Microsoft Store submission is a draft, with Free pricing selected.
Its uploaded package predates the current source changes and must be replaced.
Screenshots, final candidate checks and certification are still pending.
The reserved identity is in `release/store-identity.json`; the application
version is maintained in `Directory.Build.props` and the package manifest.

Source publication, website deployment, signing and binary release are separate
actions. Keep test logs, artifact hashes and review evidence with the candidate
or CI run; this document tracks release decisions and outstanding requirements.

## Distribution

| Channel | Purpose | Signing and updates |
| --- | --- | --- |
| Microsoft Store | Primary Windows installation | Microsoft signs after acceptance and handles updates |
| GitHub Releases / direct download | Independent secondary installation | Requires an approved trusted signer and a tested update path |
| GitHub repository | Source, documentation, issues and contributions | Build instructions and locked dependencies |

SignPath Foundation is the preferred direct-signing option, subject to acceptance.
It has not accepted TrenchHQ. Its [conditions](https://signpath.org/terms) require
maintained, released open-source software and verifiable builds. Confirm
eligibility, including the native helper and bundled components, before relying
on it. A Store rejection does not guarantee immediate SignPath availability.

Application Window remains part of the product. If Store review rejects that
feature, pursue approved trusted direct distribution while addressing genuine
security findings; do not remove the feature solely to obtain Store acceptance.

Build both channels from the same approved version tag. Their publisher
identities may differ, so do not promise cross-updates or shared settings.
Obtain the direct certificate's exact publisher before configuring that package;
keep the reserved Store identity intact. Never publish unsigned or self-signed
binaries as end-user downloads.

## Before the first binary release

- [ ] Finish screenshots and listing assets using the actual app and cleared content.
- [ ] Resolve the open API/data/brand decisions in [Services](SERVICES.md#before-distribution).
- [ ] Review and tag the intended source, version and locked dependencies.
- [ ] Run the [candidate checks](BUILD.md#verify) against that exact source,
  including current public exchange checks and newly authorized provider/X tests.
  Record untested plans and features honestly.
- [ ] Scan source and payload for secrets, unexpected files and security findings;
  review dependency licenses and native binaries.
- [ ] Build and inspect the channel package. Record source fingerprint, identity,
  toolchain and artifact hashes outside the public checkout.
- [ ] Verify live privacy/support URLs and that the served privacy text matches
  the policy bundled with the candidate.
- [ ] Complete submission review, trusted signing, signature inspection, Defender
  scanning and WACK. Repeat affected checks when the candidate changes.
- [ ] Complete the signed clean-Windows matrix below.
- [ ] Observe the small test ring before expanding distribution.

Fresh account tests, clean Windows machines and final signed-artifact evidence
are still required. Synthetic tests and local developer installations do not
replace them. Packaging commands are in [Build](BUILD.md#package-privately).

## Code signing policy

Maintainer and signing approver: [Gautam Gupta (@gautamgpt1)](https://github.com/gautamgpt1).
Signing requires explicit approval of the verified artifact. Use MFA for signing
and repository accounts; never expose signing credentials to pull-request builds.

Archive the approved tag, source fingerprint, dependency inputs and each channel's
artifact records. These identify reproducible source inputs, not a guarantee of
byte-identical signed packages. A direct `.appinstaller` feed needs its final
HTTPS location and tested install/update/rollback behavior before publication.
Add any signer attribution only after approval.

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
Disclose the same behavior to either signer and address genuine security findings.

RPC credentials are optional data-provider project keys; X uses a developer bearer
token. Neither is a wallet private key or exchange-account secret. Basic public
exchange monitoring must work without payment or keys. Reviewer path: create a
Price Ticker in Widgets, choose an exchange/pair, assign it to an overlay in
Desktop Setup, then Show. Help supplies privacy, recovery, version and diagnostics.

Disclose that bundled CCXT contains unused private/trading/cryptographic code.
The public sidecar uses newline-delimited JSON protocol v1 over local standard
input/output. It exposes only these requests:

| Request | Exposed operation |
| --- | --- |
| `getMarkets` | Public spot-market discovery through `loadMarkets` |
| `setSubscriptions` | Public ticker subscriptions/seeding using `watchTicker`, `watchTickers`, `fetchTicker` and `fetchTickers` |
| `ping` | Local acknowledgement; no exchange operation |
| `shutdown` | Close local subscriptions/workers |

No arbitrary CCXT method dispatch or exchange-account credential input is exposed.
Include the public-protocol check and locked CCXT version in submission evidence.
Reassess bundle scope if a scanner objects; do not describe all of CCXT as
public-method-only code.

Submit exact source/artifact records, dependency inventory, signature/Store
validation, Defender/WACK results, provider results and clean-machine evidence.
Privacy/support URLs must be live. The Store's desktop privacy requirement is
section 10.5.1; intellectual property is section 11.2. Confirm the Individual
account is suitable for the declared functionality and optional provider
credentials during Partner Center review.
Sources: [Store policies](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies),
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

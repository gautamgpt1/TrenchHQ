# Implementation status and release plan

Current source version: **1.0.1.0**, Windows x64, pre-release. Source publication,
signed binaries and Store certification have separate acceptance gates. This
document is the maintained plan; update it as evidence changes.

## Current gate

The architecture, documentation and logo-source cleanup are complete and locally
verified. The owner authorized publishing these reviewed source changes on
27 September 2026. Binary launch, final version tagging, signing and certification
remain deferred. There is no final binary release candidate yet; a source review
snapshot or earlier passing build must not be described as that candidate.

The [public help site](https://gautamgpt1.github.io/TrenchHQ/) and
[privacy policy](https://gautamgpt1.github.io/TrenchHQ/privacy.txt) are live. The
served privacy text must continue to match the file bundled with each candidate.
New provider credentials, a trusted signing route and clean Windows machines will
be supplied in the later release phase.

## Implemented and verification scope

The app includes shared saved widgets, floating overlays and panels docked to all
screen edges; public exchange and five-chain tickers; public-wallet monitoring;
official X, websites and explicit Application Window pinning. Automatic provider
selection preserves polling/event mode. Startup, tray recovery, panel shortcuts,
fullscreen handling, DPAPI credentials, bundled help/privacy/version and local
aggregate diagnostics are implemented.

The source was published with a fresh history starting at `f6f77a1`; the existing
published head reviewed before restructuring was `1d2314c`. No private development
history was imported. The maintained documents, public pages, exact dependency
locks, fixtures, owner-supplied logo and upstream notices form the source export.
Generated output, personal settings, logs, screenshots and session transcripts are
excluded. Local source changes still require review and authorization to publish.

The earlier [hosted workflow](https://github.com/gautamgpt1/TrenchHQ/actions/runs/36266982119)
passed at `0bbb4b1`, including an unsigned Store-upload payload inspection. The
documented build also passed in a fresh network clone of `1d2314c`. These identify
the source tested at those times; neither run verifies later edits.

Local checks before the structural refactor covered both native pin modes, cache
publication, no-key tickers, privacy/diagnostics, panel cleanup and the final merged
title bar. Wide/narrow UI checks used 125% display scaling. The original 1254-pixel
logo remains the byte-identical master; Windows resource variants and their
unchanged-file generation behavior were verified. Public live exchange/PublicNode
results from 26 September are historical reachability evidence only.

The current restructuring separates the WinUI host, Core, Infrastructure and
Yellowstone bridge; moves Rust/Node/native production components under `src/`;
replaces production-source links with project references; and moves live probes
into `tools/`. Synthetic checks use discoverable xUnit cases. The package inspector
now requires version metadata for seven owned PE files, including both new class
libraries. See [BUILD](BUILD.md) for the exact commands and verification boundaries.

Local structural verification on 27 September 2026 passed Debug and Release
builds, including locked bootstrap/build from a fresh allowlisted source export.
The managed suites passed 92 logic, 16 social, one sidecar-lifecycle and 42 on-chain
cases. Rust format/Clippy and all 136 Rust tests passed, as did the production
sidecar protocol check, six native cache checks and both interactive pin modes.
The private unsigned Store-upload package passed identity, native architecture,
seven owned binary versions, 150 dependency-notice hashes, policy/brand files
and exclusion of test/tool assemblies. This is local source/payload evidence.

An isolated package identity verified page navigation, ticker mode save/cancel,
panel show/close, provider rows, Usage and Help. The logo master/assets and the
existing merged title bar were preserved. The unchanged widget layout still clips
some controls at very narrow widths; see the limitation in [BUILD](BUILD.md).
Protected configuration and the existing deployed application remained unchanged.

The subsequent logo cleanup retains one original PNG in source and generates
20 Windows resources under ignored `obj/BrandAssets`. Unused lock-screen images
and the PNG-in-SVG wrapper were removed. Debug built with zero warnings/errors;
a fresh source export produced an unsigned Store upload whose inspection verified
all 24 policy/brand files against the source master and generator. All 20 generated
resources retain their previous bytes, and the Windows resource index resolves
their packaged paths. Repeating generation preserves their bytes and timestamps.

The restructured source and logo pipeline were published as `1dd56f8`.
[Hosted verification](https://github.com/gautamgpt1/TrenchHQ/actions/runs/36336568044)
passed the Release build, managed/Node/Rust checks, native cache checks and private
unsigned Store-upload inspection. The two interactive pin tests remain local gates.

The public presentation pass replaces the technical README opening with product
examples and setup instructions, and adds issue forms, a PR template, support and
brand guidance, and Contributor Covenant 3.0 with GitHub's private abuse-reporting
route. The existing MIT permissions for code and original artwork are unchanged.
Screenshots, demo video and a social preview remain owner-supplied follow-ups.

GitHub Discussions, Dependabot alerts/security updates and CodeQL default setup
are enabled. Private vulnerability reporting, secret scanning and push protection
were already enabled and remain on. Projects and Wiki are disabled. CodeQL's
[initial run](https://github.com/gautamgpt1/TrenchHQ/actions/runs/36338588132)
completed for Actions, C/C++, C#, JavaScript/TypeScript, Python and Rust with both
remote and local input sources. Its 132 findings were subsequently reviewed as
described below. Dependabot and secret scanning returned no open alerts at the
time of this setup check. Scan execution success is not a clean security audit.
The active main-branch ruleset blocks deletion and force pushes, including for the
owner. Normal reviewed source pushes remain available. The public site uses the
manual Pages workflow; publishing documentation does not publish an app binary.

No final tag, trusted signature, WACK result, clean-Windows certification or new
private-provider certification is claimed. Signing, credentials and test machines
remain deferred by the owner. Resolve the service-use questions in
[SERVICES](SERVICES.md), including DEX Screener and Bitget, before end-user launch.

### CodeQL review

The 132 findings on baseline `b7db1115` were reviewed using their complete SARIF
source-to-sink traces and production callers. Each was a false positive under the
existing caller boundaries; individual explanations are retained on
[the dismissed alerts](https://github.com/gautamgpt1/TrenchHQ/security/code-scanning?query=is%3Adismissed).
No query, directory or local-input analysis was disabled to remove findings.

| Findings | Reviewed boundary |
| --- | --- |
| 60 C# test paths | Fresh GUID fixtures, plus one test reading its own build-generated helper-version file |
| 47 C# shared storage/helper paths | Traces originate in fixtures or owner-run validation; production uses app-local storage, fixed names, GUID credential references, validated icon names or the packaged native helper |
| 10 C# validation-tool paths | Owned temporary roots or a fixed subdirectory of the invoking user's LocalApplicationData |
| 13 Python release-tool paths | Explicit operator-selected package, report and candidate paths; candidate hashes are compared to the owner-generated record |
| 2 process-launch findings | Cargo's output path passed to a fixed PowerShell `-File` script, and the optional diagnostic's fixed installed Chrome path with separate arguments |

The exact Rust build script was also compiled and exercised with spaces, `&`,
`;` and `$(literal)` in its output path. It created the expected version-resource
files at that literal path. The six native-cache checks passed, including damaged
cache rejection. These checks used synthetic data, without provider credentials.

Verification exposed a separate worker-pipe cancellation defect: an interrupted
length-prefixed write could leave the worker waiting for the missing message body.
The new `CancelledPartialFrameClosesWorkerPipeAndAllowsRestart` regression failed
before the fix and passed after the client began closing interrupted pipes. The
full on-chain suite then passed all 43 tests. The Release build completed with
zero warnings and errors. This review is scoped to these findings and that defect;
the final signed-candidate gates below still apply.

## 1. Publish the source repository

- [x] Keep current source, tests, necessary assets and exact dependency locks.
- [x] Consolidate documentation; retain privacy, security, contribution guidance,
  dependency licenses and attribution.
- [x] Exclude old Git metadata, session reports, local configuration, private
  captures, logs, temporary evidence and generated output.
- [x] Confirm the destination repository and initial commit author/email:
  `gautamgpt1/TrenchHQ`, Gautam Gupta, GitHub's account-linked no-reply email.
- [x] Inspect and scan the exact initial commit before pushing. Ignore rules alone
  do not remove already tracked secrets. Do not import old development history.
- [x] Enable Issues and private vulnerability reporting.
- [x] Publish as pre-release source: initial commit
  [`f6f77a1`](https://github.com/gautamgpt1/TrenchHQ/commit/f6f77a10ed22470c85d89cbf8988d6d95f4dfa3a).
- [x] Observe a successful hosted verification workflow run, with the two
  interactive pin exclusions recorded above.
- [x] Confirm app/site support links match the final repository URL.
- [x] Add product-focused README/help, contribution/security guidance and issue forms.
- [x] Set repository description/topics and enable Discussions and security tooling.
- [ ] Reduce Discussions to Announcements, Q&A, Ideas and Show and tell. These four
  are available; the empty General/Polls defaults still need removal in GitHub's
  category settings.
- [x] Configure Pages for GitHub Actions and deploy the reviewed documentation.
  [Deployment](https://github.com/gautamgpt1/TrenchHQ/actions/runs/36349009424)
  on 28 September 2026 served the home, support and privacy URLs with HTTP 200.
  Privacy text uses pinned LF line endings so Pages and Windows checkouts have
  identical bytes; verify that equality again for the signed candidate.

GitHub documents [Pages configuration](https://docs.github.com/en/pages/getting-started-with-github-pages/configuring-a-publishing-source-for-your-github-pages-site).
Source publication does not grant permission to use third-party services outside
their terms. Retain and resolve the integration questions in [SERVICES](SERVICES.md).

## 2. Prepare a signed binary release

- [x] Triage the first CodeQL findings and retain per-alert dismissal evidence;
  fix the worker-pipe cancellation defect found during verification.
- [ ] Rerun security scanning against the final immutable release source and
  review new findings before signing.
- [ ] Resolve launch-scope API/data/brand questions, particularly DEX Screener and
  Bitget. Obtain permission or replace affected integrations where required.
- [ ] Independently validate the reserved Store identity and suitable account type
  in Partner Center. Explain optional RPC project credentials accurately.
- [ ] Record the exact commit, dependencies, version and artifact hashes. Inspect
  identity, native architectures, licenses, payload and secret-scan findings.
- [ ] Build both channels from the same approved immutable version tag, using
  the channel process below. Rerun all gates after any source change.
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

### Channels and source identity

| Channel | Signing and updates | Status |
| --- | --- | --- |
| Microsoft Store | Microsoft signs an accepted submission; Store manages updates | Reserved identity recorded; submission and acceptance pending |
| GitHub Releases / direct download | Approved trusted signing, preferably SignPath Foundation; separately validated `.appinstaller` updates | No signer, direct identity, update feed or public binary yet |
| GitHub repository | Public source, issues, notices and build instructions | Published as pre-release source; binary distribution remains a separate gate |

After final edits, review the complete diff, scan the source and dependency notices,
commit the intended files and tag that exact commit. Record its SHA, tag, version,
toolchain, locks and `Prepare-Candidate.py` source fingerprint. Build each channel
in a separate checkout of that same tag. Record the approved direct identity as a
reviewed build input; its publisher must match the eventual certificate exactly.
Do not invent a SignPath publisher or modify the Store identity in the main tree.
Channel identity, signing and package bytes may differ; application features and
source provenance must agree. Archive each artifact hash and verification report.
This identifies reproducible source inputs, not a promise of byte-identical signed
packages. An `.appinstaller` feed needs the final HTTPS hosting path and signed
identity, plus install/update/rollback tests before it can be advertised.

### Code signing policy

Maintainer, reviewer and future signing approver: [Gautam Gupta (@gautamgpt1)](https://github.com/gautamgpt1).
Contributor changes require review; signing requires explicit maintainer approval
of the verified artifact. Signing and repository accounts must use MFA before
signing is enabled. Never expose signing credentials to pull-request builds.

SignPath Foundation has not accepted TrenchHQ. Its [conditions](https://signpath.org/terms)
require maintained, released open-source software, verifiable builds and an
approved artifact configuration. Disclose the helper, bundled runtimes and vendor
components for eligibility review; do not assume all qualify as system libraries.
Add SignPath's required attribution only after approval. Its certificate identifies
the Foundation; obtain the exact subject and validate direct-package identity then.
There is no guarantee that a Store rejection can immediately be followed by a
SignPath-signed download. Confirm the prior-release condition with SignPath;
Microsoft Store publication is not stated as its sole qualifying route.

The [privacy policy](public/privacy.txt) documents requested network features,
automatic RPC fallback, third-party recipients and local deletion. Recheck any
installer disclosure/opt-out obligations for the actual direct distribution.

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
Application Window remains part of the product. If Store review rejects this
feature, retain it and pursue approved trusted direct distribution, including
SignPath if accepted. Do not create a reduced Store variant solely to remove it.
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
Unknown/private requests and inherited handler names are rejected by
`Tests/SidecarProtocol.test.cjs` against the production bundle. Include this result
and the locked CCXT version in submission evidence. Reassess bundle scope if a
scanner objects; do not describe all of CCXT as public-method-only code.

Submit exact source/artifact records, dependency inventory, signature/Store
validation, Defender/WACK results, provider results and clean-machine evidence.
Privacy/support URLs must be live. The Store's desktop privacy requirement is
section 10.5.1; intellectual property is section 11.2. The current optional
provider-key/account-type question still needs Partner Center review under 10.8.3;
read-only functionality is not an advance acceptance decision.
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

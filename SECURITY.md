# Security policy

## Report a vulnerability privately

Use [GitHub's private vulnerability reporting form](https://github.com/gautamgpt1/TrenchHQ/security/advisories/new).
Do not open a public issue with security-sensitive details.

Include the affected version or commit, the impact and minimal reproduction
steps using test data. A suggested fix is welcome but not required. Never send
real API keys, wallet secrets, browser data or unredacted settings/logs.

Reports are reviewed by the maintainer. Please keep details private while the
issue is investigated and a fix or disclosure is coordinated. Response times
depend on maintainer availability; there is no guaranteed response window.

## Supported versions

Report against the current `main` branch and identify the commit you tested.
There is no stable public binary or long-term support branch yet. This policy
will identify supported releases when signed downloads become available.

## What to report

Examples include credential exposure, unsafe handling of feed or website data,
unexpected access across local process boundaries, or failures to validate
downloaded/native components. Ordinary setup problems and feature requests belong
in [support](SUPPORT.md).

Relevant boundaries for researchers:

- Provider/X credentials use per-user Windows DPAPI. This does not defend against
  malicious code already running as that Windows user.
- Wallet addresses, labels, URLs and ordinary settings are local configuration,
  not an encrypted database.
- Embedded websites have WebView2 profiles and the sites' own login/session behavior.
- Application Window deliberately attaches a native helper to a selected supported
  app. Pinning is not an isolation or security boundary.
- Node and Rust workers use bounded local protocols. The exchange sidecar exposes
  public market methods only, with no account or trading credentials.
- Diagnostic export is local and is never uploaded automatically.

See the [privacy policy](docs/public/privacy.txt) and
[architecture](docs/ARCHITECTURE.md) for the full storage and process boundaries.

## Reviewing security findings

Review findings against the actual trust boundary, including production callers.
Keep scanning enabled and record any dismissal rationale on the alert. Revisit
it when the input source, caller or privilege level changes. Remote content must
not select storage paths or executable commands.

## If a credential has been exposed

Revoke or rotate it with the provider immediately. Deleting a file or comment does
not invalidate copies that were already published. Notify the maintainer privately
with the location of the exposure, not another copy of the secret.

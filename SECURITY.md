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

## Reviewing local path and process findings

Trace each finding to its caller before deciding whether it crosses a trust
boundary. Production stores use Windows app-local storage with fixed filenames;
credential filenames require a GUID. Feed content, wallet addresses, widget
labels and URLs must never choose a storage root. Tests pass isolated temporary
roots to the same stores, which can produce findings in production source files.

Build and validation tools run with the invoking user's permissions. Their
explicit input/output paths, Cargo build directories and OS known folders are
local tooling inputs. They are not interfaces for accepting remote file paths or
commands. Native launches must keep executable selection separate from arguments;
PowerShell build helpers use `-File`, not interpolated command text.

Keep CodeQL's local and remote input analysis enabled. A test location alone is
not grounds for dismissal: inspect every reported source, path component and
operation, and record the rationale on the alert. Revisit that decision if a
caller, privilege level or input source changes. The initial review and its
verification are recorded in [RELEASE](docs/RELEASE.md#codeql-review).

## If a credential has been exposed

Revoke or rotate it with the provider immediately. Deleting a file or comment does
not invalidate copies that were already published. Notify the maintainer privately
with the location of the exposure, not another copy of the secret.

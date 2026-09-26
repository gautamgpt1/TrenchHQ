# Security policy

## Supported versions

TrenchHQ is pre-release. Report issues against the current default branch and
identify the affected commit or application version. There is no stable public
binary or long-term support branch yet.

## Reporting a vulnerability

When this repository's **Security → Advisories → Report a vulnerability** option
is available, use it for a private report. Include affected versions, impact,
minimal reproduction steps and a proposed fix if you have one.

If private reporting is unavailable, open an issue asking the maintainer for a
private reporting channel **without disclosing the vulnerability or sensitive
details**. Do not post exploit code, keys, wallet identifiers, browser data or
unredacted logs publicly. No response-time guarantee is currently offered.

Maintainers must enable private vulnerability reporting when setting up the
public repository. GitHub documents the
[repository setting](https://docs.github.com/en/code-security/how-tos/report-and-fix-vulnerabilities/configure-vulnerability-reporting/configure-for-a-repository).

## Relevant boundaries

- No wallet signing/custody, trading or exchange-account credentials are exposed.
- Optional provider/X credentials use per-user Windows DPAPI. It does not protect
  against malicious code already running as the same Windows user.
- Public wallet addresses, labels, website URLs and ordinary settings are local
  configuration, not an encrypted database.
- WebView2 websites and their logins are governed by the website and browser runtime.
- Application Window deliberately loads a native helper into an explicitly chosen
  supported app. It is not an isolation/security boundary.
- Node and Rust workers communicate locally through bounded protocols. The CCXT
  sidecar rejects arbitrary/private method calls.
- Help's diagnostics export contains a fixed aggregate schema and is saved locally;
  it is never uploaded automatically. Review it before sharing.

Read the [privacy policy](docs/public/privacy.txt) and
[architecture](docs/ARCHITECTURE.md) for storage, native-helper and cleanup details.

## Accidental disclosure

Revoke or rotate an exposed credential at its issuer. Removing a file from a later
commit does not invalidate copies already published. Inform the maintainer privately
so affected history/artifacts and notices can be handled. Never copy a real secret
into a test intended to demonstrate the problem.

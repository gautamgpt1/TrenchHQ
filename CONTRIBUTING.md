# Contributing to TrenchHQ

TrenchHQ is pre-release software for read-only Windows desktop monitoring.
For a substantial feature, open an issue describing the problem and proposed
scope before implementation. Small, focused fixes can be submitted directly.
Security reports follow [SECURITY.md](SECURITY.md).

## Local development

1. Follow [BUILD](docs/BUILD.md) to restore the pinned dependencies.
2. Read [ARCHITECTURE](docs/ARCHITECTURE.md) and the relevant source/tests.
3. Make a focused change and run the checks affected by it.
4. Include the reason, user-visible effect, verification and limitations in the PR.

Keep unrelated formatting, dependency upgrades and generated files out of a PR.
Tests should exercise observable behavior and cover a distinct regression.
Place shared rules/models in Core, acquisition/storage in Infrastructure, and
WinUI presentation/window work in the app. Add project references for tests; do
not link production source files into test projects. Live probes belong under
`tools/`, outside default test execution.

Native window changes also need an interactive Windows check for restoration and
cleanup. Provider changes must preserve the selected polling/event mode and
distinguish synthetic fixtures from actual account certification.

## Preserve user data and editor work

Follow [AGENTS.md](AGENTS.md) when Visual Studio and external tools share a checkout.
Do not overwrite unsaved buffers or reset unrelated work. Never edit `bin`, `obj`,
`AppPackages`, Rust `target` or generated sidecar output as source.

Use synthetic credentials and disposable settings in tests. Live provider tests
are opt-in and require your own authorized credentials through the protected
configuration path. Do not use someone else's saved settings, wallets or tokens.

## What belongs in a contribution

- Source, focused tests, necessary assets and dependency lock files.
- Updates to the relevant maintained document when behavior changes.
- License/provenance information for new dependencies or copied material.
- Sanitized reproduction steps and screenshots containing only test data.

Do not commit API keys, `.env` files, signing certificates/private keys, browser
profiles, real wallet labels/addresses, raw logs, dumps, local evidence archives,
IDE state or machine-specific paths. Public contract/mint fixtures already in
tests are intentional protocol data; they are not private wallet records.

The root [MIT license](LICENSE) applies to project contributions unless a file
has an identified third-party license. Submit only material you have the right
to contribute and preserve upstream notices. New integrations also need a
service-terms review; an open-source client library does not license its data feed.

## Documentation

Maintain the README, community policies and four development references:

- `docs/BUILD.md`: toolchain, commands and verification.
- `docs/ARCHITECTURE.md`: implementation map and important behavior.
- `docs/SERVICES.md`: integrations, constraints and terms review.
- `docs/RELEASE.md`: current status, release checks and rollout.

User-facing support/privacy live under `docs/public/`; attribution lives in
`THIRD_PARTY_NOTICES.md` and `ThirdParty/`. Summarize durable decisions in these
files instead of adding session transcripts or dated handoff documents.

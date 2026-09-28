# Contributing to TrenchHQ

Thanks for helping improve TrenchHQ. You can contribute without writing code:
report a reproducible bug, improve a confusing instruction, suggest a better
workflow or share a useful desktop setup.

Please follow our [Code of Conduct](CODE_OF_CONDUCT.md). For help using the app,
see [SUPPORT.md](SUPPORT.md); report vulnerabilities through [SECURITY.md](SECURITY.md).

## Before you start

Search existing [issues](https://github.com/gautamgpt1/TrenchHQ/issues) and
[discussions](https://github.com/gautamgpt1/TrenchHQ/discussions) first. Use
Discussions to explore an idea and the issue forms for bugs, concrete features
or integrations. For a substantial change, agree on the problem and scope in an
issue before implementing it. Small fixes can go straight to a pull request.

## Set up for development

Follow [Build and test](docs/BUILD.md) for the Windows toolchain, dependency
bootstrap and Visual Studio setup. The [architecture guide](docs/ARCHITECTURE.md)
maps the projects and explains where different kinds of changes belong.

Keep each change focused. Preserve unrelated edits and unsaved editor buffers;
see [editor coordination](AGENTS.md#editor-coordination) if Visual Studio is open
while another tool edits files. Generated build output is not source code.

## Verify your change

Run the checks affected by the change and record what you actually ran. Add a
regression test when fixing a behavior that could break again. Documentation-only
changes need link and content checks, not a new app test suite.

- UI changes: check the affected flow, including display scaling and narrow widths.
- Window/panel changes: check restoration and cleanup on an interactive Windows desktop.
- Feed/provider changes: use synthetic fixtures first; preserve the user's selected
  polling or streaming mode and test relevant failure/recovery behavior.

Commands and the distinction between automated, interactive and live checks are
in [BUILD](docs/BUILD.md#verify). A passing fixture does not certify a real provider
account or a signed installation.

## Open a pull request

Explain the user problem, your change and how you verified it. Link the issue if
there is one, and note any remaining limitations. Use screenshots only when they
help explain a visible change, with private information removed.

Keep unrelated formatting, dependency upgrades and generated files out of the PR.
Update the relevant existing document when behavior changes. Maintainers review
changes before merging; there is no automatic merge of dependency updates.

## Providers and integrations

Use the integration request form to identify the service, user need and official
documentation. Check [SERVICES](docs/SERVICES.md) for current coverage and constraints.
New dependencies, copied code and assets need license/provenance information;
new data feeds also need a service-terms review. A library's license does not
grant permission to use or redistribute its data provider's service.

## Test data and privacy

Use synthetic credentials, public protocol fixtures and disposable settings.
Live tests are opt-in and require your own authorized credentials through the
app's protected configuration. Never use someone else's saved keys or settings.

Do not commit API keys, wallet secrets, browser profiles, signing certificates,
personal wallet labels, raw logs, settings folders, IDE state or private machine
paths. Review diagnostic exports and screenshots before sharing. Existing public
contract/mint fixtures are intentional protocol data, not personal wallet records.

## Documentation and licensing

Keep the README focused on using the product. Setup and troubleshooting belong
in `docs/public/support.md`. Keep BUILD, ARCHITECTURE, SERVICES and RELEASE
focused on reproducible instructions, product behavior and durable decisions.
Record test results in the pull request or CI run, not a running development diary.
Update existing documents instead of adding session handoffs or vendor billing tables.

Contribute only material you have the right to share. Project contributions use
the [MIT license](LICENSE) unless an identified third-party license applies.
Preserve upstream notices and the [brand guidelines](TRADEMARKS.md). The Code of
Conduct has its own attribution and license.

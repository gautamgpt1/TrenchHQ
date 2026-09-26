# Build and verify

TrenchHQ is a packaged WinUI 3 application for Windows x64. Open `TrenchHQ.slnx`.
Version 1.0.1.0 and the public Store identity are recorded in the manifest and
`release/store-identity.json`; those identifiers are not signing credentials.

## Toolchain

- Windows with Visual Studio, WinUI/MSIX tooling and the x64 C++ build tools.
  The verified local toolchain used Visual Studio 2026.
- Windows SDK and SDK BuildTools 10.0.26100.7175.
- .NET SDK 10.0.401, pinned by `global.json`.
- Rust 1.88.0 with rustfmt and Clippy.
- Python 3.11+, Git, ripgrep, curl and npm on PATH; verified local npm: 11.7.0.
- An interactive Windows desktop for native window tests.

`release/dependencies.json` pins the official Node 22.23.3 runtime hash. Downloaded
runtimes, restored dependencies, compiled helpers and the CCXT bundle are generated
locally from locked inputs. Do not copy them from an old installation.

## Restore and build

Run in PowerShell at the repository root:

```powershell
rustup toolchain install 1.88.0 --profile minimal --component rustfmt --component clippy
powershell -NoProfile -File Scripts/Initialize-BuildDependencies.ps1
dotnet restore TrenchHQ.slnx --locked-mode -p:Platform=x64
dotnet build TrenchHQ.slnx -c Release -p:Platform=x64 -p:ContinuousIntegrationBuild=true --no-restore
```

Bootstrap verifies Node, runs `npm ci --ignore-scripts` with that runtime and
invokes esbuild explicitly. MSBuild builds the locked Rust engine and native
WindowPin helper. NuGet/Cargo/npm lock files belong in source control.

Use Visual Studio's packaged launch for interactive development. Deployment can
register the manifest's family and use its local data. Preserve installations and
settings you need; use a separate Windows test user or disposable machine for
configuration and lifecycle work.

## Verify

Use a new evidence directory outside the checkout:

```powershell
powershell -NoProfile -File Scripts/Test-Candidate.ps1 -EvidencePath C:/TrenchHQ-QA
```

This runs locked restore, Release build, logic/social/sidecar/on-chain integration,
production sidecar protocol checks, Rust format/Clippy/tests and native/embedded/
cache checks. Inherited provider-test opt-in environment variables are removed.
Default provider checks use synthetic fixtures.

Add `-LiveExchanges` to request public live spot discovery/tickers. Regional
restrictions can affect results; a pass does not certify paid accounts or all pairs.
For focused edits, run affected commands from `Scripts/Test-Candidate.ps1`.
Real provider certification requires fresh authorized keys through protected
configuration. Never reuse another developer's saved credentials.

Hosted CI still needs its first observed run; local results do not prove that
GitHub's Windows runner supports every interactive window test.

## Package privately

```powershell
powershell -NoProfile -File Scripts/Build-ReleasePackage.ps1 -OutputPath C:/TrenchHQ-Package -StoreUpload
```

Output must be new or empty. The default is an **unsigned private validation
package**, with symbols and an upload container for `-StoreUpload`. Inspect the
MSIX with `Scripts/Inspect-Package.py <package.msix> <report.json>`.
Direct signing additionally requires `-CertificateThumbprint` for a valid code
signing certificate with private-key access and a matching publisher. The script
verifies the final signature. Never publish unsigned or self-signed binaries.

## Source and dependency integrity

After Git initialization, run `python Scripts/Prepare-Candidate.py --output
C:/TrenchHQ-Candidates/candidate-name` as one command to copy reviewed source into
a new external directory. Its `CANDIDATE.json` records hashes and Git provenance.
Keep the generated publication map and evidence outside the public checkout.
Run `python Scripts/Verify-Candidate.py` inside that frozen candidate before and
after builds. Editable clones do not need a committed candidate record.

A public commit and lock files identify source inputs. Compiler versions,
timestamps and signatures can still affect package bytes. After intentional
dependency updates, regenerate locks and run `Scripts/Collect-DependencyNotices.py`
after restore; review the resulting inventory and notices. `.gitattributes`
preserves upstream notice bytes so line-ending conversion cannot invalidate hashes.

See [RELEASE](RELEASE.md) for publication, Pages, signing and lifecycle gates.

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

Start from a fresh clone when checking that published source is sufficient:
Use a short checkout/export path, such as `C:/src/TrenchHQ`. The Windows build
tools can still hit the 260-character path limit when copying deeply nested
package notices. Enabling long paths alone is not a substitute for checking the
actual toolchain; a deeply nested local QA export hit this limit during verification.

```powershell
git clone https://github.com/gautamgpt1/TrenchHQ.git
cd TrenchHQ
```

There are no source submodules, sibling-project dependencies or private research
downloads to recover. All project references and committed test fixtures live
inside this checkout. The installed toolchain and network access to dependency
registries are still required; NuGet, Cargo and npm restore their locked inputs.
Node is downloaded and hash-checked by the bootstrap below. Do not copy build
outputs or provider settings from an older installation.

Run in PowerShell at the repository root:

```powershell
rustup toolchain install 1.88.0 --profile minimal --component rustfmt --component clippy
powershell -NoProfile -File Scripts/Initialize-BuildDependencies.ps1
dotnet restore TrenchHQ.slnx --locked-mode -p:Platform=x64 -p:Configuration=Release
dotnet build TrenchHQ.slnx -c Release -p:Platform=x64 -p:ContinuousIntegrationBuild=true --no-restore
```

Bootstrap verifies Node, runs `npm ci --ignore-scripts` with that runtime and
invokes esbuild explicitly. MSBuild builds the locked Rust engine and native
WindowPin helper. NuGet/Cargo/npm lock files belong in source control.
The app, on-chain tests and live on-chain tool each import
`src/OnChainEngine/OnChainEngine.targets`. That target builds the worker before
including it in the consumer's output; parallel solution builds do not depend
on a previous app build or an already-existing executable.

`Directory.Build.props` supplies product/version metadata for all managed production assemblies. Keep its version aligned with `src/TrenchHQ.App/Package.appxmanifest`. The Rust build script
and WindowPin build use `Scripts/Build-VersionResource.ps1` and the manifest version
to embed Windows version resources with the SDK's `rc.exe`; no extra Cargo package
is needed. WindowPin's immutable filename also covers the resource script and
manifest inputs, so a version change cannot overwrite a helper loaded in a target.

Restore and build must use the same configuration. Release enables ReadyToRun;
restoring with Debug defaults can omit its compiler package and fail with
`NETSDK1094` on a fresh machine.

Use Visual Studio's packaged launch for interactive development. Deployment can
register the manifest's family and use its local data. Preserve installations and
settings you need; use a separate Windows test user or disposable machine for
configuration and lifecycle work.

## Run in Visual Studio

After the dependency bootstrap above, open `TrenchHQ.slnx` from the checkout you
are editing. A solution with the same name in another folder still builds that
other copy; Run does not fetch changes from GitHub.

1. Set `TrenchHQ` as the startup project in Solution Explorer.
2. Select **Debug**, **x64**, and the **TrenchHQ (Package)** launch target.
3. If an older TrenchHQ instance is running, use **Quit TrenchHQ** from its
   notification-area menu. Closing the dashboard alone leaves it running.
4. Use **Ctrl+F5 / Start Without Debugging** for normal use, or **F5 / Start
   Debugging** to inspect code with the debugger. Both use the selected project
   and launch target. Allow the build/deployment to finish; fix build failures
   instead of accepting a prompt to run the previous successful build.

The startup-project/profile selector configures what runs; it is not an app
version selector. Use the packaged target because this app uses package identity
and package-local settings.

## Brand assets

`src/TrenchHQ.App/Assets/TrenchHQ.png` is the unchanged 1254 x 1254 owner-supplied logo master.
It is the only app-logo source file. Normal builds run
`Scripts/Build-BrandAssets.ps1` automatically and place the Windows tile/splash
PNGs and executable/window/tray ICO in `src/TrenchHQ.App/obj/BrandAssets/`.
Those generated files are ignored by Git and packaged under `Assets/`.
To export them separately for inspection or Store listing preparation, run:

```powershell
powershell -NoProfile -File Scripts/Build-BrandAssets.ps1 -OutputPath C:/TrenchHQ-BrandAssets
```

Resizing preserves the black background, proportions and original padding; wide assets center the logo
on black. The ICO contains 16, 20, 24, 32, 40, 48, 64, 128 and 256 pixel frames.
Windows package icons/tiles/splash include 100%, 200% and 400% variants; shell
target-size variants include 256 pixels. These are generated directly from the
master, never enlarged from a small icon. Windows chooses the matching size or
downscales a larger one. The ICO's smaller frames serve native tray/caption sizes.

`StoreLogo.png` is the manifest's 50-pixel package-logo resource, with 100- and
200-pixel qualified variants. It is not the master or the Store listing upload.
For Partner Center's **1:1 App tile icon**, use
the generated `Square150x150Logo.scale-200.png` (300 x 300, made from the master).
Microsoft specifies this listing size; uploading a larger image does not replace
that requirement. See [Windows icon sizes](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-construction)
and [Store listing images](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/screenshots-and-images).

The generator uses Windows PowerShell/WPF already available on the build machine;
no separate image editor is needed. The unused lock-screen images and the SVG
that merely wrapped the PNG are removed. Rebuild and deploy the packaged app to
see master-logo changes; an already-running instance retains its loaded icons.
The generator writes only changed bytes. Package inspection independently
regenerates these assets and checks their packaged bytes against the master.

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

Hosted Windows runners [run as administrators with UAC disabled](https://docs.github.com/en/actions/reference/runners/github-hosted-runners#administrative-privileges).
TrenchHQ excludes elevated pin targets. CI explicitly uses
`-SkipInteractiveWindowTests`, records `pin-native` and `pin-embedded` as skipped,
and still runs the native cache checks. Run the default command above on an
interactive, non-elevated desktop for both pin modes before binary release.
A hosted CI pass does not certify these two interactive gates.

### Managed tests and live tools

Managed tests use xUnit v3 and the .NET 10 Microsoft Testing Platform, selected
in `global.json`. Build the solution before running engine/sidecar tests; the
bootstrap supplies Node and the solution build supplies the Rust worker.

```powershell
dotnet test --project Tests/TrenchHQ.LogicTests/TrenchHQ.LogicTests.csproj -c Release
dotnet test --project Tests/TrenchHQ.SocialTests/TrenchHQ.SocialTests.csproj -c Release
dotnet test --project Tests/TrenchHQ.IntegrationTests/TrenchHQ.IntegrationTests.csproj -c Release
dotnet test --project Tests/TrenchHQ.OnChainIntegrationTests/TrenchHQ.OnChainIntegrationTests.csproj -c Release
```

Visual Studio's **Test Explorer** discovers the same cases. Fixture assemblies
run sequentially because their scenarios observe shared coordinators and worker
lifetimes. The solution includes every maintained managed project. Production
code is compiled once per project and referenced by tests; `Tests/TrenchHQ.TestSupport`
holds reusable synthetic RPC fixtures shared with the explicit validation tool.

| Location | Purpose |
| --- | --- |
| `Tests/TrenchHQ.LogicTests` | 92 model, rule, formatting, layout and storage cases |
| `Tests/TrenchHQ.SocialTests` | 16 synthetic X parsing, stream, ownership and DPAPI cases |
| `Tests/TrenchHQ.IntegrationTests` | One complete sidecar crash/reconnect/lazy-stop scenario |
| `Tests/TrenchHQ.OnChainIntegrationTests` | 42 synthetic provider, protocol, engine and recovery scenarios |
| `Tests/TrenchHQ.WindowPinTests` | Interactive native/embedded scenarios and isolated cache checks |
| `tools/TrenchHQ.ProviderValidation` | Explicit public exchange validation |
| `tools/TrenchHQ.OnChainValidation` | Explicit live RPC/provider/polling validation |
| `tools/TrenchHQ.SocialValidation` | Explicit authorized X validation |
| `tools/TrenchHQ.WindowContainmentProbe` | Optional interactive comparison diagnostic |

Default managed tests contain no live-validation entrypoint or environment-variable
opt-in. Running either live validation tool with no arguments prints usage and
does no network or credential work. On-chain public probes use `--ticker-public-poll`
or `--ticker-public-batch`; the legacy environment-selected live scenarios require
the additional explicit `--environment` argument. Dedicated-provider commands
require newly authorized credentials in their isolated DPAPI configuration.
X validation accepts `--live --settings <authorized-dpapi-file>`.
Never point a validation tool at someone else's configuration or use old test
credentials without fresh authorization. These tools are excluded from the MSIX.

### Focused editor UI checks

Known layout limitation: the current widget editor can clip controls in very
narrow windows (observed at 920 physical pixels and 125% scaling, especially with
the navigation pane expanded). The structural refactor retained that layout;
the local navigation/save/cancel smoke check is not a full responsive-layout pass.
Use a wider window until this is addressed in a focused UI change.

Use isolated package data for UI work. Check wide and narrow windows at fractional
display scaling, with the navigation pane open and collapsed. Verify that page
actions clear the title bar, editor titles align with their action row, status
messages stay below it, and controls remain reachable after wrapping or scrolling.
Check keyboard focus, mouse actions, enabled/disabled states and dialog footers in
a short window. Exercise slider labels, all widget editors and API sections.

Save a Website URL without a scheme and X handles with commas, then let deferred
UI events finish: both editors must still show Saved with Save/Cancel disabled.
A subsequent edit must enable them; Cancel must restore the saved values. Use
synthetic content with no active feeds. Help must retain six inline sections and
31 expandable questions, with working scrolling and bundled privacy.

### Optional window-containment diagnostic

`tools/TrenchHQ.WindowContainmentProbe` is a standalone comparison harness, not
part of the app package or the standard candidate gate. Use the production
`WindowPinTests` gates above first. For deeper investigation:

```powershell
powershell -NoProfile -File tools/TrenchHQ.WindowContainmentProbe/Build.ps1
& ./tools/TrenchHQ.WindowContainmentProbe/bin/x64/Debug/net8.0-windows10.0.19041.0/TrenchHQ.WindowContainmentProbe.exe --integrated
```

This uses the actual helper implementation with disposable target windows. It
moves the pointer and types during interactive checks; do not run it alongside
other desktop interaction. `--no-input` explicitly skips those input checks.
`--chrome` uses fresh temporary profiles; it does not use the normal browser's
login. Results go under the harness's `bin` directory. Temporary profiles remain
for inspection; remove only a run's identified profiles after its processes exit.
Historical comparison modes and their `OBSERVE` results are not production
features or evidence that all applications can be pinned.

## Package privately

```powershell
powershell -NoProfile -File Scripts/Build-ReleasePackage.ps1 -OutputPath C:/TrenchHQ-Package -StoreUpload
```

Output must be new or empty. The default is an **unsigned private validation
package**, with symbols and an upload container for `-StoreUpload`. Inspect the
MSIX with `Scripts/Inspect-Package.py <package.msix> <report.json>`.
Run this inspection on Windows. It verifies all seven TrenchHQ-owned PE files have
the expected product name and file/product version, alongside native architecture,
identity, required assets and license hashes. Upstream binaries retain their own
publisher/version metadata. Signature presence is reported separately from trust;
an unsigned validation package is not a release.
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

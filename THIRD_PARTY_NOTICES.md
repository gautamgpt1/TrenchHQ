# Third-Party Notices

TrenchHQ is licensed under the repository's MIT license. The dependencies below
remain under their own licenses and vendor redistribution terms. Those terms do
not change the license of TrenchHQ source code or grant rights to service data.

## Exact dependency inventory

`ThirdParty/DEPENDENCIES.json` records 98 locked npm, Cargo and NuGet dependencies,
their license metadata and SHA-256 hashes of collected notices. Notices are bundled
under `ThirdParty/Licenses/Dependencies/`. Build-only SDK tools are identified in
that inventory. Node 22.23.3 is pinned in `release/dependencies.json`; its complete
license and embedded-library notices are in `ThirdParty/Licenses/Node-LICENSE.txt`.
CCXT and its bundled dependencies retain their upstream notices. CCXT's MIT license
does not license exchange data. The public-only IPC surface is documented in
`docs/ARCHITECTURE.md`.

Microsoft Windows App SDK, WebView2 and Windows SDK components retain their vendor
terms. The Windows SDK BuildTools package is a build dependency, not an application
payload. Win2D's upstream MIT text is retained as `ThirdParty/Licenses/Win2D-LICENSE.txt`.
All inventory entries have license metadata. Build-only
`Microsoft.Windows.SDK.BuildTools` has a vendor license URL rather than a copied
notice. Win2D 1.3.2's package license URL redirects; its retained MIT text comes
from the [official upstream license](https://github.com/microsoft/Win2D/blob/0fd4f810be5bf0cb9c981432a6f4ab3954fda08f/LICENSE.txt).
See `docs/SERVICES.md` for outstanding service/brand review.

## Development test dependencies

The managed tests restore xUnit v3 4.0.1 and its Visual Studio adapter 4.0.0
(Apache-2.0), plus Microsoft.NET.Test.Sdk 18.3.0 (MIT). Each test project's
`packages.lock.json` records its complete resolved dependency graph. These packages
are development tools and are not included in the application payload. Their
upstream license metadata is retained in the restored NuGet packages:
[xUnit](https://www.nuget.org/packages/xunit.v3/4.0.1),
[adapter](https://www.nuget.org/packages/xunit.runner.visualstudio/4.0.0), and
[test SDK](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/18.3.0).

## Cryptocurrency icons

The packaged centralized-market base-asset icons under `src/TrenchHQ.App/Assets/Crypto/` are
from `cryptocurrency-icons` 0.18.1 by Christopher Downer and contributors.
They are distributed under CC0-1.0; the exact license text is retained at
`ThirdParty/Licenses/CryptocurrencyIcons-CC0-1.0.md`. TrenchHQ resolves icons by
the normalized CCXT base symbol and shows no icon when the pack has no exact
match.

## X feed and Website widget

TrenchHQ uses an original official X Filtered Stream client and the WebView2
component supplied through the existing Windows App SDK dependency. The X API
requires the user's own authorized paid access. A Website widget displays a
third-party site; it does not redistribute that site's implementation or grant
a subscription. Website terms and content rights remain with their owners.

No unofficial X adapter or third-party X server implementation is bundled.

## On-chain .NET dependency inventory

| Component | Version/source | License | Notice |
| --- | --- | --- | --- |
| Google.Protobuf | 3.36.0, repository commit `8c891823af6d8308cdd36e1930e5d9db04d5289c` | BSD-3-Clause | `ThirdParty/Licenses/GoogleProtobuf-BSD-3-Clause.txt` |
| Grpc.Net.Client, Grpc.Net.Common, and Grpc.Core.Api | 2.83.0 | Apache-2.0 | `ThirdParty/Yellowstone/LICENSE_APACHE2` |
| Grpc.Tools (build only) | 2.83.0 | Apache-2.0 | `ThirdParty/Yellowstone/LICENSE_APACHE2` |
| System.Security.Cryptography.ProtectedData | 8.0.0 | MIT | NuGet package metadata |
| Microsoft.Extensions.Logging.Abstractions and DependencyInjection.Abstractions | 8.0.0, transitive through gRPC | MIT | NuGet package metadata |

## Yellowstone protobuf contract

The reduced `ThirdParty/Yellowstone/*.proto` declarations are derived from the
Apache-2.0 `yellowstone-grpc-proto` component at commit
`5e76c224f693ddb71ee442aa55372a9c4a5f9cee`. The separately licensed AGPL
Yellowstone server/plugin workspace is not copied, linked, or shipped.

## Rust on-chain engine

`src/OnChainEngine/Cargo.lock` is the authoritative exact version inventory.
The locked dependency graph was reviewed on 2026-08-29. The chosen licensing
paths are:

- MIT: `autocfg`, `base64`, `block-buffer`, `bs58`, `cfg-if`, `cpufeatures`,
  `crypto-common`, `curve25519-dalek-derive`, `digest`, `fiat-crypto`,
  `generic-array`, `itoa`, `libc`, `memchr`, `num-bigint`, `num-integer`,
  `num-traits`, `proc-macro2`, `quote`, `rustc_version`, `semver`, `serde`,
  `serde_core`, `serde_derive`, `serde_json`, `sha2`, `syn`, `thiserror`,
  `thiserror-impl`, `tinyvec`, `tinyvec_macros`, `typenum`, and
  `version_check`.
- Apache-2.0: `ryu` (the Apache option is selected from its alternative
  license expression).
- BSD-3-Clause: `curve25519-dalek` and `subtle`; exact notices are in
  `ThirdParty/Licenses/`.
- MIT plus Unicode-3.0: `unicode-ident`; the Unicode notice is in
  `ThirdParty/Licenses/unicode-ident-Unicode-License.txt`.

No GPL, AGPL, SSPL, source-available-only, non-commercial, or custom-restricted
component is linked into or distributed with the on-chain implementation.
Repositories with those terms were used only as architectural or behavioral
research references; their source and archives are not included.

## Protocol interoperability references

Robinhood Stock Token API documentation, Doppler/long.xyz contracts, and pons
contracts were inspected to implement public API and on-chain ABI
interoperability. No source, package, generated artifact, or contract bytecode
from those projects is linked into or distributed with TrenchHQ. The inspected
Doppler repository is Business Source License 1.1; it remained reference-only.
The pons first-party contracts used for ABI interpretation carry SPDX MIT
headers; the repository also contains GPL-licensed and third-party vendor files
that were not copied or used as TrenchHQ source. Upstream research checkouts are
not build inputs or runtime dependencies. The source, fixtures and notices needed
to build and test TrenchHQ are retained in this repository.

## Chain identification marks

The chain marks under `src/TrenchHQ.App/Assets/Chains/` are unmodified official assets used only
to identify the corresponding network in TrenchHQ. Their trademarks and brand
terms remain with their respective owners:

- Solana logomark: https://solana.com/branding
- Ethereum diamond: https://ethereum.org/assets/
- Base Square: https://brand.base.org/
- BNB Chain symbol: https://www.bnbchain.org/en/brand-guidelines
- Robinhood Chain feather symbol: https://docs.robinhood.com/chain/brand-guidelines/

Pump and Meteora assets are excluded from the package and publication snapshot
until reuse permission is established. Protocols use text labels; remote protocol
favicon downloads were removed. No protocol affiliation or endorsement is claimed.

## TrenchHQ artwork

`src/TrenchHQ.App/Assets/TrenchHQ.png` is the logo supplied by the project owner on 27 September
2026, replacing the earlier placeholder. It and its derived app, tile, splash and
tray assets use the root MIT license. `Scripts/Build-BrandAssets.ps1` rebuilds the
Windows PNG sizes and multi-resolution ICO from this unmodified PNG into ignored
build output; those derivatives are packaged, not maintained as source images.
The unused SVG wrapper and lock-screen assets were removed. The supplied black
background, white mark and proportions are preserved.

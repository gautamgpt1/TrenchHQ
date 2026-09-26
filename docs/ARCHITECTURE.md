# Architecture and behavior

TrenchHQ is a read-only desktop monitor. Saved widgets describe content; panel
definitions describe placement/presentation. Application Window is panel content,
not a saved market widget. Shared content shares acquisition/cache state; each
visible panel owns its rendering and lifecycle.

## Implementation map

| Area | Main source |
| --- | --- |
| App lifecycle, tray, startup | `App.xaml.cs`, `MainWindow.xaml.cs`, `Helpers/StartupRegistrationService.cs` |
| Editors and definitions | `Views/`, `Models/` |
| Saved widgets and panels | `Helpers/SavedWidgetCatalogService.cs`, `Helpers/DesktopPanelManager.cs` |
| Floating and docked windows | `Overlay.xaml.cs`, `DockedBarWindow.xaml.cs`, `Helpers/WindowsAppBarNative.cs` |
| Public exchange acquisition | `Helpers/MarketSidecarClient.cs`, `Helpers/MarketCacheService.cs`, `SidecarApp/main.cjs` |
| Providers and local usage | `Helpers/OnChainProviderConfigurationService.cs`, `Helpers/OnChainProviderUsage.cs` |
| On-chain coordination | `Helpers/OnChainStreamCoordinator.cs`, `Helpers/EvmStreamCoordinator.cs`, `Helpers/WalletActivityService.cs` |
| Protocol decoding/calculation | `OnChainEngine/src/`, `OnChainEngine/tests/` |
| Yellowstone transport | `OnChainTransport/`, `ThirdParty/Yellowstone/` |
| Window helper | `Helpers/ApplicationWindowNative.cs`, `Native/WindowPin/` |
| Diagnostics | `Helpers/ReleaseDiagnostics.cs`, `Helpers/OnChainPipelineDiagnostics.cs` |
| Behavioral checks | `Tests/`, `Scripts/Test-Candidate.ps1` |

## Process boundaries

The WinUI host owns user configuration and network/provider coordination. One
shared Rust engine processes bounded messages, protocol state and price updates.
The Node/CCXT sidecar serves public exchange catalogs/tickers over local pipes.
Workers start on demand, recover with bounded retry and stop when unused. Preserve
IPC size/schema validation, backpressure and equivalent-subscription idempotence.

The sidecar accepts only `getMarkets`, `setSubscriptions`, `ping` and `shutdown`.
It constructs exchanges without account credentials. Malformed frames, inherited
handler names and arbitrary/private methods are rejected. CCXT includes unused
trading/cryptographic code; the production-bundle regression verifies that it is
not exposed through IPC.

## Price Ticker

- Current-state polling defaults to one second on all five chains. Each sample
  completes before the next delay; no overlapping backlog.
- Solana keeps a pool's accounts together and publishes complete snapshots
  atomically. One account uses `getAccountInfo`; larger batches use
  `getMultipleAccounts`. Standard pricing does not also request the discarded
  `slotSubscribe` feed.
- EVM polling batches due pool/reference reads through Multicall3, up to 256
  subcalls. Smaller limits are learned only after explicit size/gas rejection.
  Pure polling does not request event history, replay or wallet-finality work.
- Alchemy uses discounted number probes and a full header on the first tick
  at/after five seconds. Other providers use one full header per due tick.
  This optimization must not change displayed-price semantics.
- Live events are an explicit choice with their own replay/fork recovery rules.
  Execution-price-only pools require events. Polling does not include every trade.
- UI updates are coalesced, up to 15 per second. That display bound alone cannot
  limit provider billing; acquisition needs its own controls.
- Public CEX streaming uses CCXT; its polling fallback is five seconds.
- Prices require validated protocol state, decimals and references. Unsupported
  pools/hooks must fail clearly rather than display guessed prices.

## Providers and budgets

Every saved compatible profile enters automatic per-chain selection. Supported
families share protected credentials while endpoint capabilities stay specific
to each chain. Healthy routes use hysteresis. Transient failures retry after
1/2/4/5-minute cooldowns while online. Rejected credentials wait for correction;
quota pauses recover on reset/cooldown. Idle chains generate no health probes.

PublicNode is the final EVM route and may yield to a recovered private provider.
Solana has no equivalent key-free live fallback. Exhaustion pauses an unavailable
chain without changing polling/event mode.

Known free allowances seed conservative local guards: 90% of a monthly allowance
divided by 31, or 90% of a daily allowance. Explicit overrides remain. Unknown
plans use health/quota-response handling without inventing a balance. Preserve
shared counters, persistence, pre-send checks and reset boundaries. Local estimates
cannot measure other applications' account usage.

Wallet Watcher has a separate exact-activity contract. Never apply ticker sampling
to wallet transfers/full-block requirements. Preserve bounded trace ranges,
one-minute EVM wallet finality and five-minute Solana token-account repair.

## Panels and fullscreen

Minimize and the panel shortcut toggle the same running instance. Close disposes
it; Show or Start recreates it. Stop closes panels and blocks their shortcuts.
Hidden/minimized docked panels release reserved work area.

Screen-edge thickness is capped at 30% of its monitor. Website/Application Window
enforce viable minimums. X, Website, Wallet and Application overlays use saved
content width/height; Price Ticker derives height from rows. Layout uses DIP and
must stay reachable after monitor/DPI changes.

Normal/maximized windows respect appbars. During genuine fullscreen, appbars stay
registered and panels lower behind the fullscreen app. Restore only after DWM
confirms fullscreen ended. Never resize or exit the external application to expose
a panel.

## Application Window

Native lock and experimental Embedded + lock use `SetWindowsHookEx` to load the
versioned x64 helper into the explicitly selected app's UI thread. Eligibility
checks inspect window/process metadata, architecture, responsiveness, resize
support and integrity. Admin/system and unsupported targets are excluded.

The helper does not capture input/screens, read foreign memory, extract credentials
or transmit data. Temporary attachment hooks are removed; subclass/CBT callbacks
and timers maintain bounds and recover on lease/owner loss. Hung targets must
resume message processing for recovery to complete.

Unpin, minimize, close and Quit detach and restore original window state. No target
is saved or automatically repinned. The module can stay mapped until target exit;
its verified DLL cache remains outside package data. Do not claim complete module
unload or absence of all retained files.

Cache publication is atomic. Verification shares read/delete access while denying
writes, tolerating a publisher's rename handle. Changed bytes get a new path;
damaged cached binaries are rejected before loading. Preserve concurrent-publication
and held-rename-handle regressions.

## X, websites and local data

X Tracker uses only the official paid Filtered Stream. Website embeds the site's
UI with a separate WebView2 profile per widget; shared-widget panels share its
login. Hidden views request lower memory use without claiming script suspension.
Close releases the WebView; widget deletion does not erase its browser profile.

Provider/X tokens use per-user DPAPI. Ordinary settings and public wallet labels/
addresses are not encrypted as a whole. Read [privacy](public/privacy.txt) for
network requests, cached helpers, diagnostics and deletion behavior.

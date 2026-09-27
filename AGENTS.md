# Working on TrenchHQ

Read `README.md`, `docs/ARCHITECTURE.md` and the relevant sections of
`docs/BUILD.md`, `docs/SERVICES.md` and `docs/RELEASE.md`. These maintained
documents replace historical development/session reports.

- Treat this checkout as self-contained. Build and development instructions must
  not depend on private research folders, old checkouts or prior chat history.
- Keep changes focused; preserve unrelated work and unsaved editor buffers.
- State assumptions, verify affected behavior and report actual results.
- Use synthetic fixtures by default. Never recover or reuse saved credentials
  without the owner's explicit authorization for the current test.
- Keep secrets, local configuration, screenshots with private data, logs and
  generated outputs out of commits. Follow `CONTRIBUTING.md` and `SECURITY.md`.
- Update an existing maintained document instead of creating session handoffs.
- Publishing source, deploying Pages, signing and releasing binaries are separate
  actions; obtain the owner's authorization for the action being performed.

## Editor coordination

Visual Studio may be open on this repository while external tools edit files.

- Before changing a file that is open in Visual Studio, inspect the active DTE
  document state when available. Never externally overwrite a document whose
  `Saved` property is `false`; preserve or reconcile its buffer first.
- After external edits, verify that open clean documents reloaded from disk
  before invoking Build, Run, Save All, or closing Visual Studio.
- Do not disable Visual Studio's external-change detection or its conflict
  warning. The warning protects genuine unsaved user work.
- Do not edit generated `bin`, `obj`, `AppPackages`, or Rust `target` output as
  source files.

# Release history — DeepSeek Harness panel

This is the English release history. It is kept from version 1.21.0 onwards; the history of earlier
versions exists only in `CHANGELOG.md` (Russian), which stays the source of truth. The same is true of
the Chinese file `CHANGELOG.zh.md`.

## 1.21.0 — 2026-09-21

### Added

- **Release notes in the language of the interface.** The panel downloads the release body from GitHub
  and used to show it as is — in Russian always, because the body was cut from the Russian
  `CHANGELOG.md`. The release body now carries three blocks with machine markers (`en`, `ru`, `zh`),
  and the panel picks the block matching the interface language: the requested language first, English
  when that block is missing, and the whole body untouched for releases made before 1.21.0, which have
  no markers at all. On the release page the English text comes first, and Russian and Chinese follow
  as separate sections below it — with no HTML at all, so a panel older than 1.21.0 shows readable
  text rather than tag soup.
- **`CHANGELOG.en.md` and `CHANGELOG.zh.md`** — English and Chinese release history with the same
  `## <version>` sections as `CHANGELOG.md`, kept from 1.21.0 onwards. The release body is assembled
  from all three by `tools\release-notes.ps1`, and all three files ship next to the panel.
- **`tools\check-notes.ps1`** — checks the assembled release body: all three blocks present, the
  version equal to `<Version>` in `DshTray.csproj`, the English block not empty, the marker order
  right, and a second run producing exactly the same file.
- **A winget package** (`winget\Danerus23.DSHPanel*.yaml` and `tools\winget-manifest.ps1`): the panel
  will be installable with `winget install Danerus23.DSHPanel` once the package is accepted into the
  winget repository. The manifest passes `winget validate`, and the script takes the version and the
  SHA-256 **from the release**, refusing to substitute a local sum unless it is explicitly allowed.
- **A window collage for the README** (`docs\demo.png`) — four real frames (panel, settings, backups,
  tray menu).
- **The About tab says what donations pay for.** Under the support link there is now a short note in
  all three languages: the panel is free, there is no paid version, and donations pay for development
  time — code, builds and checks on clean Windows.

### Changed

- **A silent install can no longer stall on an invisible window.** Two things were in the way: the
  Inno startup prompt “This will install… Do you want to continue?”, which `/SILENT` and `/VERYSILENT`
  do not disable (now removed by `DisableStartupPrompt=yes`, and the winget manifest also passes
  `/SP-`), and the message boxes in `[Code]` about .NET or Node, which `/SUPPRESSMSGBOXES` does not
  suppress either — in an unattended run they now go to the installation log instead of popping up.
- **The updates window tells where the full history is.** A hint line under the notes field says that
  the notes follow the interface language and that the full history is `CHANGELOG.md` next to the
  panel.
- **`--update-check` prints the Russian block without the machine markers**: the console report is read
  by the owner, and service markers only got in the way there.
- **Window screenshots re-shot in all three languages** — the README still showed 1.20.0.

### Fixed

- **The panel version in the list of installed programs no longer lags behind.** The panel updates
  itself, while that list entry is written by the installer — once, at install time — so it kept the
  version from then. At startup the panel now checks and corrects `DisplayVersion` in its own entry
  (`PanelRegistration.RefreshVersion`), and nothing else: the entry is never created, someone else's
  entries are never touched, and a check run with overridden folders touches nothing at all. For
  winget this matters more than it looks: it compares its version with the recorded one, and without
  this it could offer an “upgrade” to a release older than the running panel.

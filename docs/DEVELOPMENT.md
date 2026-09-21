# Development

Everything here is about building the panel itself. Users only need `DshTray.exe` and the
requirements from [README.md](../README.md).

## Build

Needs the **.NET 8 SDK** and **Node.js**: Node runs the translation check and the local GitHub stub that both update checks rely on.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

Publishes into `.\app`. Options:

| Option | What it does |
| --- | --- |
| `-OutDir <path>` | publish somewhere else |
| `-SelfContained` | put .NET itself into the folder (~160 MB, works without the runtime) |
| `-SingleFile` | one `.exe` (also needs the internet: the ILLink package is downloaded) |
| `-NoPublish` | only build into `bin\Release`, without publishing |

The publish folder is cleared first: otherwise a file deleted from the project (or a `settings.json`
left next to the `.exe`) silently travels into both the archive and the installer.

The self-check at the end (`--status`) runs in its **own temporary profile**: `DSH_PANEL_DATA`,
`DSH_PANEL_STATE`, `DSH_PANEL_SSH_DIR`, `DSH_TRAY_BACKUP`, `DSH_HOME` point into `%TEMP%`, and
`DSH_PANEL_NO_MIGRATE=1` stops the one-time migration from `%APPDATA%\DeepSeekHarness`. Without that,
a build on a developer's machine reads their real settings and writes into their real profile.

The release carries two builds: `DshPanel.zip` (framework-dependent, small, needs the .NET Desktop
Runtime 8) and `DshPanel-selfcontained.zip` (one big `.exe` with the runtime inside). Both can be
built by the same script:

```powershell
.\build.ps1 -OutDir dist\panel
.\build.ps1 -OutDir dist\panel-selfcontained -SelfContained -SingleFile
```

`-SelfContained` and `-SingleFile` add `--source https://api.nuget.org/v3/index.json` by themselves:
`NuGet.config` clears the package sources on purpose, and without an explicit source the runtime pack
and the ILLink package cannot be resolved (the failure is `NU1100`, which does not hint at the cause).

`install.ps1` creates the desktop and Start menu shortcuts (`DSH Panel`, the same name the installer
uses): `-AppDir <path>`, `-NoDesktop`, `-NoStartMenu`. The Start menu shortcut goes into the same group
folder as the installer's (`Programs\DSH Panel\DSH Panel.lnk`, because the installer sets
`DisableProgramGroupPage=yes` with `DefaultGroupName=DSH Panel`), and the script also removes a shortcut
of the same name left in the root of `Programs` by an older generation of itself — but only when it
points at this very copy, so somebody else's shortcut is never touched. Ordinary users get the shortcuts
from the installer; this script is for someone who built the panel themselves.

The project has **no external packages**: `NuGet.config` clears the package sources on purpose, so
the build uses only the SDK.

**A running panel cannot be rebuilt**: `.exe` and `.dll` are locked. Close the tray, run
`build.ps1`, start it again.

## Installer

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\build-installer.ps1
```

It builds the panel, takes the version from `DshTray.csproj`, writes `installer\appversion.iss`
(generated, not committed) and compiles `dist\dsh-panel-setup.exe` with Inno Setup. Options:
`-SkipBuild` (use the existing `app` folder), `-AppDir <path>` (publish taken from elsewhere — handy
when the running panel locks `app\`), `-Iscc <path>`.

**Inno Setup 7 is required.** What actually decides it: the wizard includes
`Languages\ChineseSimplified.isl`, and that file only exists in version 7 (6.7.3 stops with
`Couldn't open include file`, which says nothing about the real cause). The script does **not** ask the
compiler for its version — `ISCC` prints it outside the standard output, and parsing that banner would
only add brittleness. Instead it probes every candidate compiler it found and the criterion is
`Languages\ChineseSimplified.isl` next to `ISCC.exe`, then reports each candidate and whether it is
usable:

```powershell
winget install --id JRSoftware.InnoSetup.7 -e -s winget
```

The script installs **per user** into `%LOCALAPPDATA%\Programs\DSH Panel`: no administrator rights,
nothing into `Program Files`, an uninstall entry in HKCU. The payload excludes `status.txt`, `logs\*`,
`settings.json`, `*.log`, `*.pdb` and `build.txt` — those are reports, logs and machine-specific data
that must never be shipped. After installation the wizard is offered (`DshTray.exe --onboard`); the
desktop shortcut and autostart are optional tasks, off by default. The `[Run]` entry uses
`runasoriginaluser`, so a panel started from an elevated setup still runs as the ordinary user.

The uninstaller stops a running panel (`taskkill`), removes the autostart value even if the panel
created it itself, and deletes only what can be recreated (`…\DshPanel\update`, `…\DshPanel\node`) —
settings and backups in `%APPDATA%\DshPanel` are left alone.

It is our own `taskkill` that closes a running panel: it runs at the start of file copying
(`CurStepChanged`, step `ssInstall`). `CloseApplications=yes` is the second half — it lets Inno see the
running panel through the Restart Manager (verified by a probe: the panel shows up there as a holder)
and, in an ordinary install, list it among the applications to close. `RestartApplications=no`, so the
panel is not started twice — the last page starts it, a silent run does not. `AppMutex` was **removed**
in 1.21.0
on purpose: Inno checks a mutex at startup and shows an **undismissable** “the panel is already
running” box *before* `[Code]` — that is, before our own `taskkill` — and a `winget upgrade` would
stall on a window nobody closes (a hang in an automatic run, code 5 “cancelled by the user” on
“Cancel”). The `[UninstallRun]` `taskkill` is still there. **Honest note: installing over a running
panel has not been checked on a live machine** — that reading comes from the Inno documentation and
the script, so it deserves one run in a VM.

A silent install must show **no window at all**, and two things were in the way. Inno's “This will
install…” prompt ignores `/SILENT` and `/VERYSILENT`: it is switched off by `DisableStartupPrompt=yes`
and, belt-and-braces, by `/SP-` in the silent switches of the winget manifest (Inno's own help
contradicts itself on which of the two matters, so both are set). A `MsgBox` from `[Code]` is not
suppressed by `/SUPPRESSMSGBOXES` either, so every message in `[Code]` goes through the `SayOrLog`
helper: a window for the person in front of the screen, a line in the install log (`/LOG`, and in
winget's report) for a non-interactive run. A new message goes through it too. `UsePreviousTasks=no`
keeps Inno from restoring a previous task choice, which a silent install would otherwise use to bring
back an autostart the person had already switched off.

The `.iss` and the `.ps1` are UTF-8 **with BOM** (Cyrillic text, Inno Setup and PowerShell 5.1);
editing tools may drop the BOM — `tools\check-encoding.ps1` verifies and repairs this.

The .NET Desktop Runtime 8 is **bundled inside the installer** (~58 MB total instead of 2 MB):
`build-installer.ps1` downloads `windowsdesktop-runtime-8.0.x-win-x64.exe` into `installer\redist`
(not committed — it is in `.gitignore`), **verifies its Authenticode signature** (must be valid and
signed by Microsoft) and passes `/DRuntimeFile` + `/DRuntimeName` to ISCC. `-SkipRuntime` builds a
light installer for a machine that already has the runtime.

Why bundled instead of downloaded at install time: the download used to happen in `InitializeWizard`,
before the wizard window is shown, so the user saw nothing but the UAC prompt and concluded the setup
had broken (that is exactly what happened during acceptance testing). Now the runtime is installed
from the bundle at the end of the installation (`InstallBundledDotNet` from `CurStepChanged`,
`ssPostInstall`), where the setup window is visible, and the installer waits for the runtime to
actually appear before deciding whether to install Node.

The runtime check (`HasDotNet8Desktop`) looks at the **installation folders**
(`{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\8.*`, then the x86 folder) and only then at
the registry: the registry key is not always written, and on a machine with the runtime installed the
old registry-only check reported “no runtime” forever. `{commonpf}` is Program Files (x86) in Inno —
the 64-bit folder is `{commonpf64}`. The panel reports the same fact in `--env-check`.

## Panel registration

The installer writes the version into the “Programs and features” entry once, at install time, while
the panel updates itself — so that list drifts, and **winget** compares its own version with the
recorded one and may offer an “upgrade” to a release older than the running panel (the panel would
then offer to update right back). At every start the panel therefore corrects one value in its own
entry: `DisplayVersion` under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\{7F3C6A21-9D4E-4B8A-9C1F-2E5D8B7A4C11}_is1`
(`PanelRegistration.RefreshVersion`, called from `TrayHost`; the same GUID `installer\dsh-panel.iss`
uses as `AppId` and `tools\check-installed.ps1` looks for). The value written is the short version
number, without the `+<commit>` build suffix (`AppVersion.Short`).

The guards, all verified against `PanelRegistration.cs`: the key must exist (a portable build has no
entry), the entry must introduce itself as exactly `DSH Panel`, and **nothing but `DisplayVersion` is
touched and the entry is never created** — a foreign row in that list is never edited. A **check
run** (the panel started with an overridden `DSH_PANEL_DATA`, `DSH_PANEL_STATE` or
`DSH_PANEL_INSTANCE`) returns immediately and leaves the machine's entry alone: the acceptance suite
and the wizard check raise a real tray panel, and without this guard they would edit the owner's list.
That check-run marker is the same one `Autostart` uses. A key locked by policy or missing rights is
not an error either: the old version simply stays, and the panel works as usual.

## Acceptance

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\acceptance.ps1 -Exe .\app\DshTray.exe
```

Runs the panel **as a stranger**: its own settings/state folders, its own `DSH_HOME`, its own port
(3097 by default) and `preview\stub-dsh.js` instead of the real engine — so the running panel and
the owner's server are never touched. Nineteen checks: the wizard is still pending; node and the
`dsh` package are found; the dictionaries and the layout are fine in all three languages; the
self-test (version comparison, checksum parsing) passes; the wizard's answers (port, working folder)
take effect; the server starts and stops; a backup without keys holds neither `keys/` nor
`workspace/`; a backup with keys holds them; the archive passes its own verification; a restore
brings damaged data and the keys back and leaves a safety copy behind; the restore window stays
unclipped once an archive exists; no foreign paths leak into it.

The key folder for the run is `DSH_PANEL_SSH_DIR` (a temporary folder): a restore tightens the ACLs of
the folders it restores keys into, and a check must never touch the real `~/.ssh`.

Options: `-Exe <path>`, `-Port <port>`, `-Keep` (keep the temporary profile for inspection).
Exit code is 0 only when every check passed.

## Checks

```powershell
# dictionaries: key sets, empty values, placeholders
node tools\check-lang.mjs

# the same labels in all three languages; compares the embedded dictionaries in full
.\app\DshTray.exe --lang-check --out lang-check.txt

# what the panel found in the system: node, the dsh package, the key storage, the port
.\app\DshTray.exe --env-check

# state, self-check and pictures of every window
.\app\DshTray.exe --status --out status.txt
.\app\DshTray.exe --selftest --out selftest.txt
.\app\DshTray.exe --shot .\shots\panel.png

# scripts and installer: UTF-8 with BOM and CRLF (PowerShell 5.1 fails on a BOM-less file)
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\check-encoding.ps1

# restore: safety copy, key ACLs, zip-slip, links outside, keys from another machine
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\check-restore.ps1 -Dll .\dist\panel\DshTray.dll

# autostart: the entry is checked against this copy and repaired; the registry is taken to a
# throwaway branch, and the check asserts the real HKCU\...\Run was not touched
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\check-autostart.ps1 -Exe .\app\DshTray.exe

# the same by hand, on the real registry
.\app\DshTray.exe --autostart-fix --out autostart.txt

# migration from the previous generation's settings folder: the panel's own file must never be
# overwritten (the previous folder is substituted with DSH_PANEL_LEGACY_DATA, so the real
# %APPDATA%\DeepSeekHarness is not even read)
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\check-migrate.ps1 -Exe .\app\DshTray.exe

# pack a publish folder for release (also called by the CI)
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\pack-panel.ps1 `
  -Source dist\panel -Destination dist\DshPanel.zip

# assemble the release body from the three histories (CHANGELOG.md, .en.md, .zh.md) into three
# machine-marked language blocks — the same script the pipeline and the CI call
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\release-notes.ps1 -Out dist\release-notes.md

# the assembled body itself: three blocks, the right marker order, the version, no HTML, and a
# second run producing the same bytes (touches nothing and goes nowhere)
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\check-notes.ps1

# winget manifests: version from <Version>, SHA-256 from the PUBLISHED release (`-Validate` also
# runs `winget validate`); refuses a local sum and a tag that disagrees with the version
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\winget-manifest.ps1 -ReleaseTag vX.Y.Z -Validate

# rebuild the README collage docs\demo.png from docs\screenshots\en (four frames)
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\make-demo-collage.ps1

# where the panel would download Node from, and whether those addresses answer (name patterns differ:
# the MSI is node-v24.21.0-x64.msi, the portable archive is node-v24.21.0-win-x64.zip — guessing this
# once shipped a 404 that broke Node installation for everyone)
.\app\DshTray.exe --node-check --out node-check.txt

# checksums of the release assets (the panel refuses to update itself without them)
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\write-checksums.ps1 -Dist dist

# the update path end to end: a local GitHub API stub, real downloads, real hash checks
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\check-update.ps1

# the first-run wizard opens exactly ONCE (counts the windows of its own panel process)
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\check-onboarding.ps1
```

`check-onboarding.ps1` starts the panel with `--onboard` in its own profile and counts the wizard
windows through `EnumWindows`; it closes only its own process. If another panel is already running it
reports «ПРОПУЩЕНО» and exits with code 2 unless `DSH_PANEL_INSTANCE` gives it a separate instance
name (which the script sets itself, so it can run next to a working panel). It needs an interactive
desktop, so it is not part of the CI.

`--shot` writes `panel.png`, `settings.png`, `settings-big.png`, `prices.png`, `backups.png`,
`restore.png`, `onboarding.png`, `onboarding-env.png` and `menu.png` next to the given file. Take them with
`--lang ru|en|zh`: pictures must differ between languages wherever text is translated and must match
only for windows that are not translated yet. For the public screenshots take them with an isolated
profile **and a free port** (`--port 3901` plus `serverPort` in that profile's `settings.json`):
otherwise the picture shows somebody's running server, its PID and that the port is busy. The recipe
must also point `DSH_PANEL_RUN_KEY` at a throwaway branch: the “Start at Windows sign-in” checkbox is
drawn from the **real** `HKCU\...\Run` (`MainForm.ApplyAutostart` ← `Autostart.IsEnabled`), so without
that override the picture carries the state of the machine it was taken on, not of the demo profile.

The README showcase is the collage `docs/demo.png` — four frames (panel, settings, backups, tray
menu) — and `tools\make-demo-collage.ps1` builds it from `docs\screenshots\en`: **rebuild it after
re-shooting the screenshots**, otherwise the README keeps showing windows of the previous version.
The demo backup used for the restore frame has to be anonymised **by hand**: the panel always writes
`Environment.MachineName` and the user name into the backup manifest, and there is no CLI switch to
anonymise it.

`tools\check-restore.ps1` runs in a sandbox folder (`-Work`, default `_build\restore-test`), refuses
port 3080 and cleans up after itself even when a check fails. It covers, among others, the case of an
archive made on **another machine**: keys recorded outside the current profile must not be restored to
that path, while a `.ssh` group is returned into the current user's own `.ssh`.

The server that serves the web interface **must not be stopped** by the checks: it holds the running
session.

## Server ownership

The panel may stop only a server it can prove is its own. `ServerController.IsOurs` confirms ownership by
the process itself, and every condition has to hold at once:

- the number recorded in `server.pid` is exactly this process (`server.pid` alone proves nothing: the file
  outlives a reboot, and process numbers are reused);
- the process is alive;
- it is a `node.exe` image;
- our port appears in its command line;
- it is not the panel's own process.

The sign-in link file (`web-url.txt`) is **not** evidence of ownership at all: it holds an address with a
token, and a single mention of the port in it used to be enough to call somebody else's process “ours” —
and to stop it. The path to our `bin.js` is deliberately **not** checked either: it legitimately changes
(a portable Node, a restore, a reinstall of the engine), and a false “foreign” is more expensive — the
panel would lose the ability to stop **its own** server and would show a permanent “the port is taken by
another process”. When the process details cannot be read at all (no rights, another account, a protected
process), the server is counted as foreign: warning is safer than stopping somebody else's process. The
verdict goes into the application log once per change, and a stale `server.pid` is removed.

With a foreign owner the panel does **nothing**: it never kills it. `Start` refuses with “Port *N* is
taken by *name* (PID *pid*). Stop it manually.” (`error.portBusy`) and `Stop` cancels in the
same words, writing the refusal to the log. Ownership is re-checked right before a kill, and `KillTree`
also compares the process creation time, so a recycled process number cannot make the panel kill somebody
else's process.

## Translations

- Dictionaries: `lang\ru.json`, `lang\en.json`, `lang\zh.json`. They are embedded into the `.exe`
  (`EmbeddedResource` in `DshTray.csproj`), so the panel stays a single file with no language
  folders next to it.
- Use `Loc.T("key")` and `Loc.T("key", arg1, arg2)`; the wording lives only in the JSON files.
- **No user-visible string in code.** Everything a person can see — window titles, tray balloons,
  exception messages, the `README.txt` written into a backup archive — goes through `Loc`. Log lines
  and CLI diagnostics stay Russian: they are read by the owner and by bug reports.
- Fallback order: the chosen language → English → the key itself (so a missing string is visible
  right in the window and is reported by `--lang-check`, which now compares the whole dictionaries).
- Add a new key to **all three** dictionaries: `check-lang.mjs` fails on a missing or extra key, an
  empty value, or `{0}`-style placeholders that disagree with the English template.
- Chinese needs a font with CJK glyphs: `Loc.UiFont(...)` picks `Microsoft YaHei UI` for `zh`
  (Segoe UI has no hieroglyphs and would show squares).
- The language is stored in `settings.json` (`language`: `auto`, `ru`, `en`, `zh`); `--lang` and
  `DSH_PANEL_LANG` override it for one run. When comparing screenshots, make sure `DSH_PANEL_LANG`
  is not left over in the environment — it wins over `--lang`.

Overrides meant for checks (all optional). `DSH_TRAY_NODE` and `DSH_TRAY_BIN` are also useful to
ordinary users, so the README names them — as it in fact names most of the list below; this table is
where each one is explained:

| Variable | What it does |
| --- | --- |
| `DSH_PANEL_LANG` | interface language for one run (`ru`, `en`, `zh`); wins over `--lang` |
| `DSH_PANEL_DATA`, `DSH_PANEL_STATE` | where settings and state live — this is how a second copy is run for tests |
| `DSH_PANEL_NO_MIGRATE` | `1` disables the one-time migration from `%APPDATA%\DeepSeekHarness`; used by the build self-check and the CI so they never read the builder's own settings |
| `DSH_PANEL_SSH_DIR` | your own key folder for one run: a restore closes key folders to the owner, so checks must not point at the real `~/.ssh` |
| `DSH_HOME` | the DSH data folder for one run (`.dsh` by default): the panel and its checks read this one instead of `%USERPROFILE%\.dsh`, so a check never touches the real data |
| `DSH_TRAY_TZ_OFFSET` | forces the time-zone offset in hours (`7`, `5.5`, `-8`) used to show peak hours — for checking another time zone |
| `DSH_TRAY_PRICING`, `DSH_TRAY_PRICING_SOURCE` | your own pricing file or pricing page address |
| `DSH_TRAY_BACKUP` | your own backup folder |
| `DSH_PANEL_REPO` | the release repository the update check asks (`owner/name`), so the check can be tested against any public repository |
| `DSH_PANEL_API` | the GitHub API base address; a check points it at a local stub to walk the whole update path offline |
| `DSH_PANEL_INSTANCE` | the mutex/event name of the “single instance” logic; a check overrides it to run its own panel next to a working one (the wizard check does this) |
| `DSH_PANEL_RUN_KEY` | the HKCU branch that holds the autostart entry (`Software\Microsoft\Windows\CurrentVersion\Run` by default); `tools\check-autostart.ps1` points it at a throwaway branch, so a check never touches the real `Run` |
| `DSH_PANEL_LEGACY_DATA` | the previous generation's settings folder (`%APPDATA%\DeepSeekHarness` by default); `tools\check-migrate.ps1` points it at a throwaway folder, so the migration can be checked without reading the builder's own settings |
| `DSH_PANEL_VARIANT` | `framework` or `selfcontained`, forcing the build variant the update check looks for; for checks only — in normal work the variant is detected from the panel folder |
| `DSH_PANEL_DONATE` | the link shown in the About tab, overriding the shipped one (`AppLinks.DonateUrl` = `https://app.lava.top/3686297587`, so the row is shown; an empty link keeps the row hidden) |
| `DSH_TRAY_BALANCE_SCRIPT` | your own fallback script for the balance request |

An overridden `DSH_PANEL_DATA`, `DSH_PANEL_STATE` or `DSH_PANEL_INSTANCE` also marks the run as **a
check** in the panel itself: such a run never repairs the real `HKCU\...\Run` and never corrects the
version in “Programs and features” (`PanelRegistration.IsCheckRun`; the same marker `Autostart` uses).
Without that, the acceptance suite — which really does raise a tray panel — would edit the owner's
registry. `DSH_PANEL_RUN_KEY` is the narrower override for the same purpose.

## Version and changelog

- The version lives in one place: `<Version>` in `DshTray.csproj` (SemVer). `1.0.1` is a fix,
  `1.1.0` a new capability, `2.0.0` a breaking change.
- History lives in **three** files: `CHANGELOG.md` (`## x.y.z`, sections “Добавлено / Изменено /
  Исправлено”) is the source of truth and the only file with the full history; `CHANGELOG.en.md` and
  `CHANGELOG.zh.md` are kept **from 1.21.0 onwards**, and for older versions they have no section at
  all. “Version history” in the tray menu opens the file for the interface language and falls back to
  `CHANGELOG.md` only when that file is missing (`AppVersion.HistoryFor`) — so on a translated
  interface it shows the translated history, which simply does not reach back before 1.21.0, and the
  hint above the update notes says the full history is `CHANGELOG.md` next to the panel
  (`update.notesHint`). All three ship next to the panel, because the menu opens them from there: they
  are `CopyToOutputDirectory` items in `DshTray.csproj`, so they travel into both archives and into
  the installer. `build.ps1` warns when the section for the current version is missing from
  `CHANGELOG.md`.
- The release body is assembled from all three by `tools\release-notes.ps1`: three blocks —
  `## English`, `## Русский`, `## 中文` — each wrapped in machine markers
  (`<!-- dsh-notes:en -->` … `<!-- /dsh-notes:en -->`, then `ru`, then `zh`). There is deliberately
  **no HTML** in the body: a panel older than 1.21.0 stores and shows the raw body, so `<details>` or
  `<summary>` would reach the person as tag soup. A version whose translation has no section gets the
  Russian one, and the script says so instead of shipping an empty block.
- The update window shows **one** block, chosen by the interface language at display time
  (`UpdateService.PickNotes`). `settings.json` keeps the raw body on purpose, so switching the
  language re-picks the block instead of waiting for the next update check. No block for that
  language → English; no markers at all (a release made before 1.21.0) → the whole body, as before.
  `tools\check-notes.ps1` checks the assembled body: each of the three marker pairs exactly once and
  in order, the right section inside each block (for 1.21.0 and newer a missing translation is a
  release error; for older versions the check asserts that the Russian section was substituted
  honestly), the version equal to `<Version>`, no HTML tags, and a second run producing exactly the
  same bytes.
- The exact build time goes into the `build.txt` stamp next to the panel: the number says what it
  is, the stamp says when. `tools\check-versions.ps1` cross-checks the version in the project, the
  panel, the stamp, `appversion.iss`, the setup and the archive.
- Version comparison knows about pre-releases: `1.20.0` is newer than `1.20.0-rc.1`, and
  `rc.10` is newer than `rc.2` (`UpdateService.IsNewer`; the cases are asserted by `--selftest`).
  Without that, someone on a release candidate would never be offered the final release.

## Updates and integrity

The panel updates itself from the GitHub release: it downloads its **own build variant** — `DshPanel.zip`
for the ordinary build, `DshPanel-selfcontained.zip` for the single-file build with the runtime inside,
decided by the panel folder itself (`UpdateService.SelfContainedBuild`) — plus `dsh-panel-setup.exe`,
unpacks it into `%LOCALAPPDATA%\DshPanel\update\<version>` and replaces its own files after closing.
If the release carries no archive for this variant, the update stops with a straight explanation instead
of substituting a “similar” build: the framework-dependent one needs an installed runtime, the
self-contained one must not have it. What is checked:

1. `SHA256SUMS.txt` from the release is **required**: the downloaded archive and installer are
   compared against the published hashes, and a missing sums file stops the update with an
   explanation. There is no code-signing certificate, so this is the only integrity check available.
2. The version inside the unpacked `DshTray.exe` must equal the release tag.
3. The previous panel is copied to `update\backup-<version>` first, so an update can be reverted by
   hand, and the replacement script rolls back by itself when `robocopy` fails.

The outcome of a replacement is **not** guessed from the script's exit code. At the next start the panel
looks at the version in the name of the `update\<version>` folder: a version strictly newer than its own
means the replacement did not happen (the panel did not exit, the mutex stayed busy, the rollback put the
previous version back). The person is told about it **once**, with a tray balloon naming the reason, and
the folder is renamed to `failed-<version>`: the traces stay for inspection and manual repair, but the
message is not repeated — not at the next start, not after a reboot. Exactly one such folder is kept. A
version that is not newer is taken as “the replacement happened”: the panel cleans up after itself (the
folder, the downloaded archive and the sums file; `backup-<version>` is left alone) and honestly writes
into its log that, if the same version was rebuilt and released again, the files may never have been
copied at all.

`tools\write-checksums.ps1` produces the file (run by `make-release.ps1` and by the CI on a tag), and
the release must carry all four assets: `dsh-panel-setup.exe`, `DshPanel.zip`,
`DshPanel-selfcontained.zip`, `SHA256SUMS.txt`.

The whole path can be walked without publishing anything:

```powershell
# a local GitHub API stub answers "releases/latest", the panel then really downloads the archive,
# verifies the published hashes and unpacks it (the panel is NOT closed, the script is NOT started)
.\tools\check-update.ps1

# the same by hand, against any release or stub
.\app\DshTray.exe --update-prepare [--force] --out update.txt
```

`--force` only allows preparing a release of the *same* version (that is how the check exercises the
path before the first release exists); it does not skip a single integrity check.
`tools\check-update.ps1` runs three cases: correct hashes (prepared), a tampered hash (refused and
cleaned up), and a release without the sums file (refused).

## Release

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\make-release.ps1
```

One command does everything that can be automated, and **publishes nothing**: it checks the script
encoding, refuses to continue if private keys or owner data are found in the text files that would go
to the repository — `tools\check-personal.ps1` reads the tracked files **and the new, not yet added
ones** (`git ls-files --cached --others --exclude-standard`), but it does not look inside binary files
such as PNG, so screenshots stay outside its verdict — checks the
dictionaries, publishes the panel into `dist\panel` (not into `app\`, so a running panel does not get
in the way), packs both archives with `tools\pack-panel.ps1` (which removes `status.txt`,
`settings.json`, `web-url.txt` — the sign-in link, which holds a token —, `*.pdb`, `*.log` and the
builder's path from `build.txt`, then **verifies** the result), checks the Node
download addresses (`--node-check`), runs `tools\acceptance.ps1` and `tools\check-restore.ps1` against
that build, then `tools\check-autostart.ps1` and `tools\check-migrate.ps1`, walks the whole update path
(`tools\check-update.ps1` and `tools\check-update-apply.ps1`), compiles `dist\dsh-panel-setup.exe`,
cross-checks the versions, writes `SHA256SUMS.txt`, assembles `dist\release-notes.md` from the three
histories (`tools\release-notes.ps1`) and checks it (`tools\check-notes.ps1`). Options:
`-SkipAcceptance`, `-SkipInstaller`, `-SkipSelfContained` and `-SkipUpdate`
(leaves out both update checks). The exit code is 1 if any step failed.

Publishing is the owner's call and is done with a tag:

1. bump `<Version>` in `DshTray.csproj`, add the section to each of the three histories
   (`CHANGELOG.md`, `CHANGELOG.en.md`, `CHANGELOG.zh.md`), commit;
2. `git tag vX.Y.Z` and `git push origin main --tags`;
3. after the release is published, regenerate the winget manifest (see the next section).

The `build` workflow (`.github/workflows/build.yml`) builds the panel on every push and on pull
requests with the **same scripts** as a local build (`build.ps1`, `tools\pack-panel.ps1`,
`tools\check-lang.mjs`, `tools\check-encoding.ps1`, `tools\check-personal.ps1`). Compiling the installer
(`installer\build-installer.ps1`), cross-checking the versions
(`tools\check-versions.ps1`), writing `SHA256SUMS.txt` (`tools\write-checksums.ps1`) and assembling and
checking the release body (`tools\release-notes.ps1`, `tools\check-notes.ps1`) are gated by
`if: startsWith(github.ref, 'refs/tags/v')`, so they happen **only on a `v*` tag**. On a `v*`
tag, accordingly, it also installs Inno Setup 7, compiles the installer, verifies that the tag matches `<Version>`
(a tag newer than the project version makes `UpdateService` fail for everyone with
`update.versionMismatch`), and creates or updates the release with four assets —
`dsh-panel-setup.exe`, `DshPanel.zip`, `DshPanel-selfcontained.zip`, `SHA256SUMS.txt` — taking the
release body assembled from the three histories (`tools\release-notes.ps1`, checked by
`tools\check-notes.ps1`). Writing permissions are granted only to the release
job; the rest of the workflow runs read-only.

Actions are pinned **by commit SHA**, not by a moving major tag (`@v4`): the owner of an action can
retag it at any time, and that would change the code running in this workflow. Comments next to the
pins carry the readable version. To update a pin deliberately:

```powershell
gh api repos/actions/checkout/git/ref/tags/v4 --jq .object.sha
# annotated tags need one more step: git/tags/<sha> → .object.sha
```

If the release was created by hand, upload the files into it instead of creating a second one:

```powershell
gh release upload vX.Y.Z dist\dsh-panel-setup.exe dist\DshPanel.zip dist\DshPanel-selfcontained.zip --clobber
```

## Winget package

`winget\` holds three manifests of schema **1.12.0** — `Danerus23.DSHPanel.yaml`,
`Danerus23.DSHPanel.installer.yaml` and `Danerus23.DSHPanel.locale.en-US.yaml`: an Inno installer
(`InstallerType: inno`), `Scope: user`, **no dependencies** (the .NET Desktop Runtime 8 is already
inside the setup, and declaring `Microsoft.DotNet.DesktopRuntime.8` would add a system-wide UAC
prompt in front of this user-scoped package), and `RequireExplicitUpgrade: true` — otherwise
`winget upgrade --all` would pull the panel, whose own updater runs far more often than this
manifest, while an explicit `winget upgrade Danerus23.DSHPanel` still works.

`tools\winget-manifest.ps1` fills the manifests in and checks them. The version comes from
`<Version>`; the SHA-256 comes **from the published release** (`-ReleaseTag v<version>`, i.e. the
`SHA256SUMS.txt` asset), because the installer is built by CI and its bytes differ from a local build
in `dist\`. A local sum is refused unless `-AllowLocalSum` is passed (for debugging the script, never
for a release), and a tag that disagrees with `<Version>` is an error: the sum would be taken from one
release while the link pointed at another. `-Validate` also runs `winget validate` over the folder.

The manifest in the repository deliberately describes the **last published** release, so it lags one
version behind `<Version>` and that is normal. Order of work: publish the release, then re-run the
script (`-ReleaseTag v<version> -Validate`) and send the result to `microsoft/winget-pkgs` as a pull
request — the first PR needs the CLA signed in a browser. No code signing is needed for winget. What
makes this work in practice is `PanelRegistration` above: without the corrected `DisplayVersion`,
winget would compare its own version with a stale one and offer a downgrade.

## Checking an installed panel

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\check-installed.ps1
```

Run this on a machine where the panel was installed by the setup (a clean VM, for instance): it
checks that the panel and its uninstaller are in place, that the entry in “Programs and features”
exists, runs the dictionary, environment and layout checks against the **installed** copy and then
the whole stranger-style acceptance. It finds the entry by the `AppId` GUID and asserts its
`DisplayName`; the `DisplayVersion` inside it is kept current by the panel itself at startup
(`PanelRegistration`, above), not by this check. Default folder is `%LOCALAPPDATA%\Programs\DSH Panel`,
override with `-AppDir <path>`.

## Test doubles and fixtures

- `preview\stub-dsh.js` — a stand-in for the real server. Run it exactly like DSH
  (`node preview\stub-dsh.js web --no-open --port 3099`) and point the panel at it with
  `DSH_TRAY_BIN`; it answers 200 only for its own token and 401 for anything else, so the panel's
  “start the server, catch the sign-in link, kill the process tree” logic can be checked without
  touching a real DSH. `STUB_DELAY_MS` delays the listener, which is how the “do not open the
  browser before the page answers” behaviour is tested.
- `preview\pricing.html`, `preview\pricing-zh.html` — saved copies of the official pricing page
  (English and Chinese) used by `--pricing-check --from <file>`; keep them when the parser changes.
- `preview\fetch-pricing.mjs` — fetches a fresh copy of the pricing page into a file.

## Running external programs

Everything that spawns a process goes through `ProcessRunner`, which reads stdout and stderr
concurrently (a child that fills the 4 KB stderr buffer while nobody reads it never exits) and now
also:

- **puts the panel's own Node directory on the child's `PATH`** (`NodeLocator.AddToPath`). A portable
  Node lives in `%LOCALAPPDATA%\DshPanel\node` and is not on `PATH`, so `npm` itself starts (it is
  called by full path) but package scripts that call `node ./cnoke.cjs` fail with “node is not
  recognized” — that is how installing the DSH engine used to break on a clean machine. Reproduced
  and fixed on purpose: with the directory in `PATH` the same `npm install -g @deepseek-ai/dsh`
  finishes with 518 packages instead of failing in `koffi`.
- **decodes both streams as UTF-8**: node and npm print UTF-8, while .NET reads pipes in the console
  code page (866 on Russian Windows), which turned npm's report into mojibake and hid the reason.
  The server process (`ServerController`) already augments `PATH` the same way.

## Where the panel looks for Node and the engine

`NodeLocator` searches, in order: `DSH_TRAY_NODE` / the saved `nodePath`, every `PATH` entry, the npm
global directory, `%ProgramFiles%\nodejs` and — importantly — **the panel's own portable Node**
(`%LOCALAPPDATA%\DshPanel\node\node.exe`) and the engine installed inside it
(`…\node\node_modules\@deepseek-ai\dsh\lib\bin.js`). The last two were missing once, and right after a
clean install the panel reported “Node not found”, “package dsh not found”, showed the tray balloon
about a missing engine and could not start the server — even though it had just installed both. The
same list is used by the acceptance suite, which is why those checks failed in the VM as well.

After installing Node or the engine the wizard records the resolved paths (`nodePath`, `dshBinPath`)
in `settings.json` and fills the path fields, so the panel does not have to guess later.

## Windows traps worth remembering

- `.ps1` files with Cyrillic need a **UTF-8 BOM**: PowerShell 5.1 reads a BOM-less file as ANSI and
  fails on smart quotes (the error points inside a string and says nothing about the encoding).
  `tools\check-encoding.ps1` checks and repairs this; it touches **only** `.ps1` and `.iss`. JSON
  configs are the opposite — they must have **no BOM**, otherwise Node refuses to parse them.
- `Get-ChildItem -Include` together with `-LiteralPath` **ignores the filter** (in Windows PowerShell
  5.1 and in PowerShell 7) and returns every file: filter with `Where-Object`. This is not academic —
  a careless "fix the encoding of .ps1 files" run rewrote every file in the project, including PNG
  screenshots and the icon. `New-Item` has no `-LiteralPath` parameter at all.
- Edit text files with an editor, not `Get-Content`/`Set-Content`: PowerShell 5.1 mangles Cyrillic.
- `DshTray.exe` is a GUI application: PowerShell does not wait for it. Use `Start-Process -PassThru`
  plus `WaitForExit`, or write the report with `--out <file>`. `Start-Process -ArgumentList` joins its
  elements with spaces and does not quote them: a path with a space must be quoted by the caller.
- `Environment.GetFolderPath` does not honour the `%APPDATA%` / `%LOCALAPPDATA%` environment
  variables; use `DSH_PANEL_DATA` and `DSH_PANEL_STATE` to run a second copy for tests.
- The application log (`%LOCALAPPDATA%\DshPanel\dsh-tray.log`) rotates at 2 MB and keeps two older
  files (`.1`, `.2`): installing the engine writes the whole npm output into it, and the panel runs
  for months. The server log (`dsh-web.log`) is capped separately (5 MB, dropped when the server
  starts).

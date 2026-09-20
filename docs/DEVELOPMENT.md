# Development

Everything here is about building the panel itself. Users only need `DshTray.exe` and the
requirements from [README.md](../README.md).

## Build

Needs the **.NET 8 SDK**; Node.js is needed only for the translation check.

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
uses): `-AppDir <path>`, `-NoDesktop`, `-NoStartMenu`. Ordinary users get the shortcuts from the
installer; this script is for someone who built the panel themselves.

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
`Couldn't open include file`, which says nothing about the real cause). The script asks the compiler
for its version before building and reports what it found:

```powershell
winget install --id JRSoftware.InnoSetup.7 -e -s winget
```

The script installs **per user** into `%LOCALAPPDATA%\Programs\DSH Panel`: no administrator rights,
nothing into `Program Files`, an uninstall entry in HKCU. The payload excludes `status.txt`, `logs\*`,
`settings.json`, `*.pdb` and `build.txt` — those are reports, logs and machine-specific data that must
never be shipped. After installation the wizard is offered (`DshTray.exe --onboard`); the desktop
shortcut and autostart are optional tasks, off by default. The `[Run]` entry uses
`runasoriginaluser`, so a panel started from an elevated setup still runs as the ordinary user.

The uninstaller stops a running panel (`taskkill`), removes the autostart value even if the panel
created it itself, and deletes only what can be recreated (`…\DshPanel\update`, `…\DshPanel\node`) —
settings and backups in `%APPDATA%\DshPanel` are left alone.

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

# pack a publish folder for release (also called by the CI)
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\pack-panel.ps1 `
  -Source dist\panel -Destination dist\DshPanel.zip

# cut the current version's section out of CHANGELOG.md (the release notes the update window shows)
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\release-notes.ps1 -Out dist\release-notes.md

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
otherwise the picture shows somebody's running server, its PID and that the port is busy.

`tools\check-restore.ps1` runs in a sandbox folder (`-Work`, default `_build\restore-test`), refuses
port 3080 and cleans up after itself even when a check fails. It covers, among others, the case of an
archive made on **another machine**: keys recorded outside the current profile must not be restored to
that path, while a `.ssh` group is returned into the current user's own `.ssh`.

The server that serves the web interface **must not be stopped** by the checks: it holds the running
session.

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

Overrides meant for checks (all optional; `DSH_TRAY_NODE` and `DSH_TRAY_BIN` are listed in the
README as well, because users need those two):

| Variable | What it does |
| --- | --- |
| `DSH_PANEL_LANG` | interface language for one run (`ru`, `en`, `zh`); wins over `--lang` |
| `DSH_PANEL_DATA`, `DSH_PANEL_STATE` | where settings and state live — this is how a second copy is run for tests |
| `DSH_PANEL_NO_MIGRATE` | `1` disables the one-time migration from `%APPDATA%\DeepSeekHarness`; used by the build self-check and the CI so they never read the builder's own settings |
| `DSH_PANEL_SSH_DIR` | your own key folder for one run: a restore closes key folders to the owner, so checks must not point at the real `~/.ssh` |
| `DSH_TRAY_TZ_OFFSET` | forces the time-zone offset in hours (`7`, `5.5`, `-8`) used to show peak hours — for checking another time zone |
| `DSH_TRAY_PRICING`, `DSH_TRAY_PRICING_SOURCE` | your own pricing file or pricing page address |
| `DSH_TRAY_BACKUP` | your own backup folder |
| `DSH_PANEL_REPO` | the release repository the update check asks (`owner/name`), so the check can be tested against any public repository |
| `DSH_PANEL_API` | the GitHub API base address; a check points it at a local stub to walk the whole update path offline |
| `DSH_PANEL_INSTANCE` | the mutex/event name of the “single instance” logic; a check overrides it to run its own panel next to a working one (the wizard check does this) |
| `DSH_PANEL_DONATE` | the donation link shown in the About tab (the shipped constant is empty, so the row stays hidden) |
| `DSH_TRAY_BALANCE_SCRIPT` | your own fallback script for the balance request |

## Version and changelog

- The version lives in one place: `<Version>` in `DshTray.csproj` (SemVer). `1.0.1` is a fix,
  `1.1.0` a new capability, `2.0.0` a breaking change.
- History lives in `CHANGELOG.md` (`## x.y.z`), sections “Добавлено / Изменено / Исправлено”.
  `build.ps1` warns when the section for the current version is missing, and the release notes are cut
  from that section (`tools\release-notes.ps1`) — the update window in the panel shows them, so a
  release without the section has no changelog to display.
- The exact build time goes into the `build.txt` stamp next to the panel: the number says what it
  is, the stamp says when. `tools\check-versions.ps1` cross-checks the version in the project, the
  panel, the stamp, `appversion.iss`, the setup and the archive.
- Version comparison knows about pre-releases: `1.20.0` is newer than `1.20.0-rc.1`, and
  `rc.10` is newer than `rc.2` (`UpdateService.IsNewer`; the cases are asserted by `--selftest`).
  Without that, someone on a release candidate would never be offered the final release.

## Updates and integrity

The panel updates itself from the GitHub release: it downloads `DshPanel.zip` (and
`dsh-panel-setup.exe`), unpacks it into `%LOCALAPPDATA%\DshPanel\update\<version>` and replaces its
own files after closing. What is checked:

1. `SHA256SUMS.txt` from the release is **required**: the downloaded archive and installer are
   compared against the published hashes, and a missing sums file stops the update with an
   explanation. There is no code-signing certificate, so this is the only integrity check available.
2. The version inside the unpacked `DshTray.exe` must equal the release tag.
3. The previous panel is copied to `update\backup-<version>` first, so an update can be reverted by
   hand, and the replacement script rolls back by itself when `robocopy` fails.

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
encoding, refuses to continue if private keys or owner data are found in the project, checks the
dictionaries, publishes the panel into `dist\panel` (not into `app\`, so a running panel does not get
in the way), packs both archives with `tools\pack-panel.ps1` (which removes `status.txt`, `*.pdb`,
`settings.json` and the builder's path from `build.txt`, then **verifies** the result), runs
`tools\acceptance.ps1` and `tools\check-restore.ps1` against that build, compiles
`dist\dsh-panel-setup.exe`, cross-checks the versions and cuts `dist\release-notes.md` out of
`CHANGELOG.md`. Options: `-SkipAcceptance`, `-SkipInstaller`, `-SkipSelfContained`. The exit code is 1
if any step failed.

Publishing is the owner's call and is done with a tag:

1. bump `<Version>` in `DshTray.csproj`, add the `CHANGELOG.md` section, commit;
2. `git tag vX.Y.Z` and `git push origin main --tags`.

The `build` workflow (`.github/workflows/build.yml`) builds the panel on every push and on pull
requests with the **same scripts** as a local build (`build.ps1`, `tools\pack-panel.ps1`,
`installer\build-installer.ps1`, `tools\check-versions.ps1`, `tools\write-checksums.ps1`). On a `v*`
tag it also installs Inno Setup 7, compiles the installer, verifies that the tag matches `<Version>`
(a tag newer than the project version makes `UpdateService` fail for everyone with
`update.versionMismatch`), and creates or updates the release with four assets —
`dsh-panel-setup.exe`, `DshPanel.zip`, `DshPanel-selfcontained.zip`, `SHA256SUMS.txt` — taking the
release body from the `CHANGELOG.md` section. Writing permissions are granted only to the release
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

## Checking an installed panel

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\check-installed.ps1
```

Run this on a machine where the panel was installed by the setup (a clean VM, for instance): it
checks that the panel and its uninstaller are in place, that the entry in “Programs and features”
exists, runs the dictionary, environment and layout checks against the **installed** copy and then
the whole stranger-style acceptance. Default folder is `%LOCALAPPDATA%\Programs\DSH Panel`,
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

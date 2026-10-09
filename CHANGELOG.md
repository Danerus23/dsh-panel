# Version history

The history of **DSH Panel 2.0**. The history starts with the first release — **2.0.0**.

## 2.0.3 - 2026-10-10

**The window no longer opens full-screen, and the update answer moved into Settings.**

* **A very wide window after a full restart - fixed.** The panel no longer remembers the placement of
  an INVISIBLE window: a window hidden in the tray reported someone else's geometry, and after a
  restart the panel opened full-screen. A hidden window is not asked for its placement at all now.
* **The update has no separate window any more.** The answer to "what do I have and what is out"
  lives in Settings, in the "Update" section, and the tray menu item opens Settings on that section.
  The section is built as **three cards** (state · actions · "What's new in this version"), and
  **the verdict comes first**: "You have the latest version 2.0.3 - no need to update".
* **"Skip this version" is available only when the release REALLY is newer**; when it is not, the
  button names the reason in its tooltip. "Download and prepare" on your own release says
  "nothing to update to" - it used to be available and did nothing.
* **The "last checked" line no longer repeats the verdict:** in the unchecked state the same sentence
  stood twice in a row and read like a failure.
* **Backups: the words about keys stopped scaring people.** The journal now says "layout `keys/…`:
  the panel writes the archive itself" instead of "group name `keys/1-ssh` is nested - 7-Zip cannot
  do that". The behaviour did not change; the words did, and the old line read like a broken backup.

**Under the hood (no visible change):** the test run no longer fails "every other time" - the panel
reaches the UI thread only through its own door, and source-level gates guard that; console output is
decoded with the SYSTEM code page rather than the culture one (on English Windows with a Russian
culture a failure reason arrived as mojibake); self-update has been verified on a live installation
in a virtual machine - file replacement, reading the outcome and the rollback branch. The test count
is **1271**.

## 2.0.2 - 2026-10-09

**The panel no longer confuses "the money ran out" with "the key was rejected".**

* On an exhausted account the panel said "Account unavailable: the key was rejected" — while the key
  was accepted and the balance was simply used up. Now it names the real reason and **shows the
  amount**: "Account unavailable: the balance is used up (-0.01 CNY)".
* A genuinely rejected key (401/403) is described in terms of the key, and a connection failure
  speaks for itself ("Balance not received"), with the reason on the details line. All three cases
  used to look the same.
* A rejected key or a failed request is no longer reported as "the balance has not been requested yet".

**Under the hood (no visible change):** the panel's window placement is now written to the journal —
rectangle, window state, display scale and screen areas. Those numbers are used to pin down a rare
case where the window becomes very wide after a full computer restart. The test count is **1264**.

## 2.0.1 - 2026-09-30

**Accuracy and language.** A small release about the panel not confusing agents, speaking the panel's
own language, and getting a problem report all the way to GitHub.

* **The balance no longer confuses agents.** If you switched the active agent while a balance request
  was in flight, the panel could show the **previous** agent's balance under the new agent's name.
  Now an answer is applied only to the agent it was asked for, and the new agent is asked again right
  away.
* **The problem report is translated.** Its text was Russian whatever the panel language - now it
  follows the panel language, and the report says that the panel log is kept in Russian as well.
* **The update window.** Failure reasons are no longer substituted as Russian words into English and
  Chinese sentences.
* **The "Open on GitHub" link in the problem report works.** GitHub used to refuse it because the
  whole report did not fit into the address - now the panel shortens it itself and says how many log
  lines went in.
* **The installer carries the panel icon again** (it had the Inno icon).
* **README and logo updated.**

## 2.0.0 - 2026-09-29

**The first 2.0 release.** The panel was rewritten from scratch: C# / Avalonia, .NET 10, **one exe**,
with the .NET runtime inside. The installer no longer installs Node or the DSH engine - the panel
does not ship them.

### What it does

* **Server.** Start, restart and stop the local DSH server; real state (running / stopped / port taken
  by another program); the sign-in link opens in your browser **only when you click it**.
* **Adopting a server that is already running.** The panel finds a server it did not start, shows it
  to you, and takes it over with your consent - **and remembers your answer**. It will not start its
  own server next to a found one.
* **Tray.** The icon shows the state by colour, the menu carries three status lines and a peak badge.
  The cross and "minimise" put the window away in the tray; the server keeps running.
* **Balance and peak hours.** Balance from your model key, peak windows in your local time,
  notifications about an approaching peak and a low balance. A balloon "the panel is running - I am
  in the tray": starting used to be silent, so there was no way to tell whether the panel was up.
* **Prices and price history.** The "Cost" table from the pricing page plus **a history of changes**:
  an entry appears only when prices or peak windows really changed, the comparison is against the
  previous entry, the newest one is marked "current", a rise is red and up, a fall is green and down,
  and the balloon names **exactly what** changed.
* **Backups and restore.** A backup of your DSH data as an archive, a list of backups, restore with
  a plan and a safety copy, scheduled automatic backups with rotation and three size modes.
  **Private keys** outside `~/.dsh` are included only with a separate permission. A **shareable copy**
  is a one-off action, not a setting: the checkbox in the copies window clears itself after the copy,
  a copy without a window has its own `--shareable` switch, and scheduled copies are never shareable -
  the access-keys file is left out of it entirely. Private keys and sharing together are refused,
  with the reason in words.
* **Updating itself.** A release check once a day, release notes **in the panel's language**, "skip
  this version", download with SHA-256 verification, unpacking with a version check, file replacement
  by a script **with rollback**, and a plain-words outcome on the next start.
* **Report a problem.** In the "About" window: the panel collects a technical report, **shows you
  exactly the text that will be sent**, and sends it only when you press a button. The model key,
  the balance and the sign-in link never go into it.
* **Three languages:** Russian, English, Chinese.
* **Settings:** autostart, theme, your server's port, the server working folder, and a "Server"
  section with an environment report - what was looked for, what was found and exactly where, with
  the versions of DSH, Node, npm and pnpm.

### Installation

* The installer **installs over the 1.x panel**: the same package identity, one panel in the tray,
  the previous installation does not remain as a second copy.
* It installs **into your user profile** and **does not require administrator rights**.
* The .NET runtime is not needed - it is inside.

### What 2.0 does not have

* **No DSH engine and no Node in the distribution:** you need your own DSH installed.
* **No code signing at all** - SmartScreen will warn on the first run.
* **No DSH updates:** the panel updates only itself.
* The peak table does not know Chinese holidays: the schedule is weekly.

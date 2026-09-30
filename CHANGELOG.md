# Version history

The history of **DSH Panel 2.0**. The history starts with the first release — **2.0.0**.

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

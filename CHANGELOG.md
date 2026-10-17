# Version history

The history of **DSH Panel 2.0**. The history starts with the first release — **2.0.0**.

## 2.1.0 - 2026-10-17

**A phone on your own network answers the agent's question, and the panel now installs, updates
and rolls back the DSH server itself — from a verified set of versions.** The version is in
"About": it says **2.1.0**.

### Phone and the agent's question

* **You can answer the agent from your phone.** Settings has a new "Phone" section: the "Allow
  phone" switch, the port, the "Show code" button — **six digits** with a lifetime, the list of
  connected devices with "Unpair" next to each, and the address the panel waits on for the phone.
  Next to it is a **QR code** with the address and port: a camera opens the phone page straight
  away, no need to type the code by hand. The tray menu carries a "Phone: connected" or "Phone:
  waiting for a connection" line; while the phone is not allowed, there is no line at all.
* **The phone page is the agent's question in words, with answer choices.** You can answer by
  choosing or in your own words if none of the choices fits; the answer goes **once**, and the
  page does not send it a second time. A separate "Cancel the agent's run" button says plainly
  that the work of that run will not continue. No question — the page says so and refreshes
  itself. Nothing external and not a single script from the network: the page works offline.
* **The firewall rule is placed only when you click.** The "Phone" section has a "Firewall" card:
  the state of the rule (read without administrator rights), words BEFORE about what exactly will
  change on the machine and what Windows will ask, **one** button and one line after it. The rule
  is narrow: private profile only, the panel's port only, this program only and your own subnet
  only. Until Windows gives its confirmation nothing changes, and that is said in words; without
  a person's click the panel places no rule — neither when the phone is enabled nor at start-up.
* **The phone no longer promises a question that will not come.** If the DSH server was raised by
  something other than the panel, the panel cannot sign in to it — and the agent's question will
  not reach the phone. The phone used to say "the panel is in touch and waiting for a question
  from the agent" in that position; now it says the truth: there is no connection to the DSH
  server, the question cannot be shown, look at the computer. The "Phone" section on the computer
  says the same — that is the place where it can be fixed. The **false line of the first seconds**
  is gone too: the panel wrote "there is no connection to the DSH server" before it had read the
  server state; now it stays silent while it does not know, and asks for the subscription the
  moment the state becomes known.

* **Phone.** Works on your own network: sign in by QR and answer the agent's question; setting tasks and managing agents are in development; it makes no external connections.

### Backups

* **The clean-up behind the panel is finished.** Temporary update and backup folders could stay
  on disk while the panel counted them as removed: deletion broke on a folder holding a link
  (junction) or a read-only file, and the error was swallowed silently. Now the link is removed
  by itself (the panel does not go inside it), a copied file no longer locks the clean-up, and
  **the result of the clean-up is checked** — "I will remove it later" is no longer passed off
  as "removed".
* **A backup row can be selected from the keyboard.** The selection used to live on a mouse click
  only: arrow keys did nothing and there was no visible focus at all. Now the arrows and Home/End
  move the row, the focus is visible as a ring and stays in place after the table is rebuilt.
* **The numbers in the backup table stand in a column.** "Size" and "Files" — both the header and
  the cells — are right-aligned, as in the restore plan table.

### DSH server: install, update, roll back

* **The panel installs the DSH server itself.** The install goes **from a verified set of versions**
  (exact versions and sums of every package), and afterwards the tree is checked against it path by
  path. The "Update" section has a "DSH server" block: the first line is **your own** version, then
  the **recommended** (verified) one and the **fresh** one (from the npm registry), with words about
  how they differ. The main button installs the verified version only; the fresh one is a separate
  action with a confirmation; when there is no server at all, the button is called "Install".
* **Before replacing a version the panel always asks:** "will stop the DSH server, take a copy,
  install {version}; agent work will be interrupted". It takes a safety copy of the profile
  (a failed copy refuses the update — "there is no such thing as half an archive"), stops **only
  its own** server, installs the verified version and **checks what was installed live**: raises it
  on a free port in its own temporary home and runs the probe — **84 of 84 names and 8 of 8
  skills**. Passed — the server comes back up on the new version; failed or broken — the
  **previous** one comes back. Words before the deed, one line after it. If the system Node does
  not fit by version (the panel takes it from the set's data), the panel downloads **its own** —
  from the official source and with the sum checked.
* **Rollback is a quiet line under the buttons.** The "Return to version {X}…" line is visible only
  when there is somewhere to return to. The panel always asks and speaks about sessions **plainly**:
  their journal is newer and the old server will not open them. The return goes only to what the
  panel itself installed and what is on disk, and the previous version is checked live first: did
  not come up — the panel changes nothing and says so in words.
* **The panel says what it can do with this server.** The "Server" section has a table of skills:
  the sign-in link, the session list, setting a task, cancelling, answering the agent's question,
  the balance by the model key, the profile layout for copies, the server version. Each skill is
  put in words as **checked · does not work · not checked**, and, when something is missing, **what
  exactly** it gets in the way of. The table lives in the same environment report — no new windows.
* **A feature without the skill it needs refuses in words instead of staying silent.** The panel
  checks the skills at the moment a person asks for a feature (for example, the balance with no
  model key file) and explains what is missing; the rest keeps working.
* **Hints about server versions.** The panel reads a short file from the public repository: which
  versions are verified, what is worth knowing about them and which troubles are known — in the
  panel's language. The file is optional: could not reach it — the panel says "checked on: no
  versions" and works as usual. The card shows the decision and the buttons, and the version
  summary — notes and **known troubles** — sits behind the "Show all versions" link; the "DSH
  server releases on GitHub" link opens only on a click. No notes — the panel says so in words
  instead of showing an empty field.
* **The panel shows ALL the servers it found, not the first one.** If more than one DSH runs on the
  machine, the panel shows a row for each, marked "the panel's server (version)" or "a found
  server", and each has its own "Take over — port N" button: the person sees which server exactly
  is being taken over. The usual case of one server has not changed.

### Settings and windows

* **The "Server" section shows the truth by itself.** "What was found on this machine" and "What
  the panel can do with this DSH": states are dense tags with the word inside, no column headers;
  the path is shown as a mask (`~\.dsh`), and the folder has the row action "Open folder". The live
  skill probe no longer waits for a press of "Check the environment": its facts arrive in the
  background by themselves, and the port line changes by itself when the server is raised or
  stopped.
* **The "Update" section reads in order.** First everything about the panel — state and version,
  buttons, "What's new", the file replacement card — and only then, in one piece, the "DSH server"
  block (its versions, its buttons, its notes and "Show all versions"). The block used to stand
  between the panel's cards, and the section read out of order; nothing was removed or added — only
  the order changed.
* **The first acquaintance.** The first-run window is shown **once** and speaks to the point: what
  is missing (DSH server · model key) and what to do. It has "Check again" and a button "Open the
  Update section"; the panel is already running and living in the tray at that point.

### Updating the panel

* **An unfinished download no longer takes the panel's place.** Release files are downloaded in
  chunks (8 MiB, a single stream — measured), with retries and a pause; **the overall SHA-256 is
  checked before the file takes its working place**, and an unfinished file does not take the
  working place at all.
* **A failed check no longer looks like "never checked".** A network refusal (including the GitHub
  request limit) used to wipe everything from the section: the found version, the notes and the
  date of the check — and on the next start the panel knocked at the already drained limit again.
  Now the panel keeps **two different facts**: the last successful answer (version, notes and the
  date of the check) and the last attempt — as a quiet line "last attempt: {when} — {why}; the next
  attempt no earlier than {when}". After the limit the panel stays silent for **several hours**
  (6 hours after the limit, an hour after any other failure) and remembers both facts on the next
  start.
* **If an update folder is left behind, the panel says so in words.** A failed clean-up used to be
  silent: a folder held by another process stayed on disk and the person knew nothing about it.
  Now the refusal of the clean-up is named both in the journal and in the outcome of the
  replacement: "could not clean up after the update: the update folder is left and must be removed
  by hand".

### Three languages

* **The new words exist in all three languages:** the phone section and page, the skills table, the
  "DSH server" block, the first-run window. The dictionaries hold **1298 keys × 3 languages**.
* **A duplicate key in a dictionary cannot hide any more.** The same record (`PhonePageWaiting`)
  stood twice in the dictionaries — in each of the three languages: JSON parsing merges identical
  keys into one and stays silent, so neither the key check nor the dictionary guard saw the
  trouble, and one of the two phrases simply disappeared. Now the guard **counts records in the
  file separately from the parse** and names the repeated key. The duplicate itself is gone.

### What 2.1 does not have

* **Phone traffic goes over the network unencrypted** — the sign-in is protected by the six-digit
  code and the device pass. The phone's limits are in the "Phone and the agent's question" section
  above.
* **There is still no code signing** — SmartScreen will warn on the first run.
* **The panel installs and updates only ITS OWN DSH server.** It never touches a foreign
  installation: it shows the version and what it can do with it. A fresh version from the registry
  is shown but not installed until a verified set has been taken for it.

**Under the hood (no visible change):** tree deletion lives in one place, and source-level gates
prevent a recursive delete from being written around it; the panel writes a list of what it put on
the machine (`state\installed.json`) — a future uninstall will need it, and it holds no personal
data; the server surface is checked against a pinned list of **84 names** and one real call, so that
"the server has become different" is caught before people are; dead server locks are removed while
a live owner is never touched. The test count is **1972**.

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

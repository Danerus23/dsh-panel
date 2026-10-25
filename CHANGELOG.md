# Version history

The history of **DSH Panel 2.0**. The history starts with the first release — **2.0.0**.

## 2.4.2 - 2026-10-25

**Four live update and window defects — no new capabilities.** The panel no longer loses an
update preparation when the settings window is closed; there is one preparation per process; the
update downloads one file instead of two and reports the total size, speed and time left; a
minimized panel hides its satellite windows and brings the same windows back when restored. The
version is in "About": it says **2.4.2**.

* **Closed the settings window during preparation — the progress is not lost:** the state lives
  in the panel, not in the window; reopen it and the same progress and prepared build are there.
* **A second start over a running preparation is impossible** and is explained in words; the
  refusal names no exception type and no owner path.
* **The update downloads one file** — the panel archive that actually replaces the files; progress
  reads "downloaded X of Y · Z/s · about N left", 16 MiB chunks, resume after a break.
* **A minimized panel hides its satellites** and restores the same windows; one taskbar button.
* Internal: the `Avalonia.Controls.DataGrid` package is removed as deprecated; moving to
  `TableView` is a separate probe after the release.

## 2.4.1 - 2026-10-24

**Eight accumulated defects — no new capabilities.** What changes is what the panel reports and how
the screen reads: a server crash no longer goes silent, focus after an update step is no longer lost,
the standard screenshot door can show the "prepared" state, window documents now say what the code
does, date and currency follow the panel language, terminology is one name per concept, empty states
call onward, and every field has an accessible name. The version is in "About": it says **2.4.1**.

* **A crash of your own server is reported even when the panel adopted it by record** (after a panel
  restart): a balloon, a journal line with the exit code, and the record cleared. If the code is not
  available, it says "unknown" honestly instead of inventing a zero.
* **Focus after an update step is not lost:** after preparing it sits on the replace card, after a
  refusal on the repeatable action; the reason is said in words and announced to screen readers.
* **The "prepared" frame** is taken by the standard door (`--shot … settings-update-prepared`), and
  update frames no longer say "no update needed" above a ready build.
* **Window documents match the code:** windows open above the panel, and the panel is not closed;
  the live `--windows-selftest` probe checks this on a real desktop.
* **Date and currency follow the panel language**, not the Windows culture: `22.10.2026` ·
  `Oct 22, 2026` · `2026年10月22日`; the currency code (USD/CNY) is named next to the price, with no
  conversion.
* **Terminology: one name per concept** in all three dictionaries ("Backup", "folder", two distinct
  unlinking actions — server and phone).
* **Empty states call onward, dead-end errors name a way out** — by naming the button.
* **All 19 fields have an accessible name** equal to their label.
## 2.4.0 - 2026-10-24

**A batch of accumulated corrections: the update runs in the background and does not touch
the working server, windows behave as one whole, and the screen tells the truth about what the panel
is doing.** There are no new capabilities — what changes is what a person sees and reads, and how
the doors that already exist behave. The version is in "About": it says **2.4.0**.

### The update runs in the background and leaves the working server alone

Preparing an update no longer holds the window and no longer breaks on a panel restart:

* **The preparation state lives in a file** next to the download chunks (address, sum, length, size
  of the future file), so after a restart the panel continues where it stopped instead of starting
  over.
* **The download resumes by `Range`** — a retry asks for the remainder, not the whole file. Before,
  a retry could jump back to the beginning and throw away what was already downloaded; that is where
  "8 MiB in 3–5 minutes" came from.
* **Measured on a real release:** an 8 MiB chunk (8 388 608 B) took **7.5 s** against a 30 s limit;
  the whole release file (44 572 116 B) — **19.4 s**, in 8 MiB chunks, the SHA-256 sum matched. Every
  attempt is recorded in the journal as a number: `metric=8MiB seconds=… limit=30 slow=no`.
* **Background preparation does not stop the shared server on 3080.** Installing the update is still
  **by button only**; there is no auto-restart.

The notification bubble now shows the preparation's progress and outcome, and a click on it leads
where the bubble points: the "Update" section, or the settings section when the matter is autostart.
There is no percentage or bar in the bubble — numbers and the bar stay in the "Update" section
(one event — one message). Network errors are named in words: "what happened and what to
do"; the technical line goes to the journal only. The prepare button no longer stays clickable
"in flight".

**If the panel's own server has ended by itself**, the panel says so through three channels:
a bubble "The DSH server ended by itself" with the code and time, a journal line ("by itself", exit
code, tail of the output masked) and an event for subscribers. **There is no raise button and no
auto-raise** — that is a separate decision by the person.

### The panel start does not scare with a false window

At PC start the first-run window "The DSH server did not start" no longer appears while a start
attempt is in progress: "starting" is not "did not start". A real failure of that start is still
shown — after the attempt has ended.

### Windows behave as one whole

Satellites (backups, update, "Peaks") hide and close together with the panel, and the panel has
**one taskbar button**. A satellite is shown above the panel, and a window closed by the person does
not pop back on its own. **"About" has become the seventh settings section** — the separate window
is gone; it holds the version, "what it does", links, "Report a problem…", the MIT licence and ©.

### Small fixes you can see and hear

* **Esc closes four panel dialogs** and does not silently discard an unsaved edit.
* **The selected row is visible as selected** — a backplate and a bold label, not colour alone;
  in the peaks table "today" is named in words, not only by a green colour.
* **The main window at 640×640**: the bottom row of buttons is fully inside the window, margins are
  at least 8 points.
* **The backup window asks when closed mid-copy**: interrupt the copy or leave it in the background;
  the safe answer does not break the work.
* **The stop-server-before-backup dialog**: the title is a question ("Stop the server before the
  backup?"), the answers name different things ("Stop it and back up" / "Back up on the fly"), and
  the caption "Close without saving" says what will happen.
* **The report preview is exactly the text that will be sent**: no service HTML comment and no lone
  dot; an empty field stays an empty section.
* **Buttons with a picture name themselves by their label** — the screen reader says "Refresh
  balance", not the content type.
* **Six small look-and-wording fixes from the frames**: the port explanation sits under its own
  label, a refusal names the available checkbox, "the server does not declare the call" is named
  "not supported by the server", a dangling ";" is gone, the price-change cell uses an arrow without
  a sign, and the phone address is not duplicated.

### Prices are read again

The price page address now ends with a slash — without it the site answers 200 and serves the root
page "Your First API Call", which has no table. The parser understands the new table: two models and
off-peak/peak price pairs. The model comes from the agent profile (`deepseek-flash`,
`deepseek-v4-pro`), foreign prices are not substituted; the currency follows the panel language
(`$` or `¥`). If the site ever serves the shell instead of the real page again, the window will say
honestly that the price table was not found.

### Words

The program speaks about itself **impersonally and in the third person** in all three languages:
**40 first-person lines** were removed (ru 27, en 11, zh 2) and **16 journal lines**. The journal
jargon was left as it was.

### Under the hood

Checks are now **2245** (was 2190), dictionaries — **1343 keys × 3 languages** (was 1333). The engine
and its set are untouched: the verified default version is still `0.2.1-rc.2`. Build — 0 warnings /
0 errors; the personal-data, encoding and dictionary guards — 0.

### What this release does not include

* **The phone has no new capabilities** — the gate surface did not grow: no new addresses, names or
  calls; HTTPS and SSE are not included.
* **Installing the update is by button only**; there is no auto-restart: background preparation
  prepares the file but does not install it itself.

## 2.3.2 - 2026-10-22

**A security patch for the phone gate: a foreign door and a foreign page are refused, a question
with no one to answer it no longer hangs, and the gate's responses are not cached or embedded.**
There are no new capabilities — only what concerns access to the phone and what a person sees on its
page. The version is in "About": it says **2.3.2**.

### The gate checks that the request came to it

Before, the gate looked neither at the door's name nor at the page the request came from: a request
to a different address was served as its own. Now `Host` and `Origin` are checked. If the door's name
is foreign or the request came from another site's page — **403 with words, before any action**: the
question is not shown, the answer is not sent, the device is not remembered. `Origin: null` (how a
sandbox, `file://` and `data:` name themselves) is refused separately — before, it was
indistinguishable from "no header". The gate's own name is the one a person sees in the "Phone"
section; when the panel is opened on the same machine, `localhost` is accepted too. Older phone
browsers that send no `Origin` at all work as before: the pairing code and the device pass remain
the protection.

### A question with no answerer no longer hangs forever

Two things change in the phone's behaviour:

* **A second question no longer silently overwrites the first.** If the agent has a new question,
  the previous turn is **cancelled**, and only then does the phone show the new one; an answer to the
  old question does not go into a foreign turn. Before, the previous turn kept waiting for an answer
  that would never come.
* **No answer for 10 minutes — the question is dropped.** The phone was turned off, the page was
  closed, the person walked away: after **10 minutes** the question is dropped, the agent's turn is
  **cancelled**, and the page says so in words ("the question was dropped: there was no answer for
  10 minutes — the agent's turn was cancelled") instead of staying silent.

### The gate's responses are not cached and not embedded

Every gate response — an ordinary page, JSON, and any error — now carries three prohibitions:
**do not cache** (`Cache-Control: no-store`), **do not guess the content type**
(`X-Content-Type-Options: nosniff`) and **do not embed the page in a frame**
(`Content-Security-Policy: frame-ancestors 'none'`). Before, the phone or a proxy could cache the
page with the agent's question and the session list, the browser could render JSON as HTML, and the
page could be embedded in a frame on a foreign site. The headers live in one place through which all
responses pass, so an error response gets them too.

**Under the hood (no visible effect):** checks are now **2190** (was 2164); dictionaries —
**1333 keys × 3 languages** (was 1328). The engine and its set are untouched: the verified default
version is still `0.2.1-rc.2`. Build — 0 warnings / 0 errors; the personal-data, encoding and
dictionary guards — 0. Internal checks were strengthened: the personal-data guard now looks at both
the index and the git history and knows more secret patterns (tokens, private keys, home paths).

### What this patch does not include

* **No channel encryption (HTTPS):** the gate still works only in its own network and over HTTP.
* **No new addresses, names or calls in the gate** — the surface did not grow; the patch only closes
  holes and makes behaviour stricter.

## 2.3.1 - 2026-10-20

**A small patch that follows a live update from 2.2.1 to 2.3.0: an update no longer looks broken,
and the screen tells the truth about what it is doing.** There are no new capabilities — only what
a person sees and reads. The version is in "About": it says **2.3.1**.

### After preparation the action is in plain sight

Before, when "Download and prepare" finished, the button went grey while "Update and restart the
panel" sat **below** the "What's new" card — past the edge of the window. All the person saw was a
disabled button, so an update read as broken: exactly how the live 2.2.1 → 2.3.0 update looked.

Now the replacement card sits **right under the row of buttons, above "What's new"**, and after a
successful preparation the panel **scrolls to it** and puts **focus** on it — the focus ring is
visible. Focus is on the card, not on the replacement button: that button cannot be undone (the
panel closes and is replaced), so it must not sit under Enter right after the step. Measured on a
1080×900 window with a 706-point viewport: the card occupies **435–540** — fully visible, with 166
points to spare; "What's new" starts at 602. Before the fix: top **702** against a **706**-point
viewport — four points of the frame showed, neither a line nor a button.

### The progress line is cleared, the progress bar hidden

After "Download and prepare" the progress line kept the last stage ("Panel archive: 100 %"), so a
frozen stage read as "work is still going". Now, when the attempt ends — **either way** (success,
refusal, or an exception in the background task) — the line is **empty** and the bar is **hidden**;
while work is in progress nothing changes. No new words: just an empty line.

### "Stage: …" where there are no percentages

"The file size is unknown" was also said where there is no size at all — checksum verification,
unpacking, the safety copy and the replacement script: a lie by meaning. Those steps now say
"**stage: …**" ("stage: Checksum verification", "stage: Unpacking the archive", "stage: Safety copy
of the current panel", "stage: Replacement script"), and the panel mentions the file size **only**
while downloading — the checksum file, the panel archive and the release installer — where the size
matters.

### "Peak hours: checked" — two dates, each labelled

The balance card showed **one** date — from the DSH server profile — and the person read it as the
date of **their own** check, even though the panel parses prices by itself every day. Now there are
**two** lines: the panel's own — "Peak hours: checked 12.10.2026" (when the panel parsed prices; if
it has not, "Peak hours: not checked yet", and the profile date is **not** substituted for it) —
and the profile one on its own line with the source named: "date from the DSH server profile:
24.09.2026".

**Under the hood (no visible effect):** checks are now **2164** (was 2153); dictionaries —
**1328 keys × 3 languages** (was 1325). The engine and its set are untouched: the verified default
version is still `0.2.1-rc.2`. Build — 0 warnings / 0 errors; the personal-data, encoding and
dictionary guards — 0.

## 2.3.0 - 2026-10-20

**The default DSH server version is now the 0.2.x line: `0.2.1-rc.2`.** The panel installs it with the
main button — from its own set and with a probe check — while the previous verified `0.1.5-rc.2` stays
on disk for a rollback. The version is in "About": it says **2.3.0**.

### Why 0.2.1-rc.2 is the default, and what "verified" means here

* **The set is 683 packages, and the file's sum is pinned in the build.** The install is checked by a
  real `npm ci`: code 0, **63 639 files / 692.9 MB**, the version in the installed tree is
  `0.2.1-rc.2`.
* **The installed tree passes the panel's full probe:** "144 declared; 77 needed — 77 of 77
  recognised; 7 retired — 7 of 7; 67 known new — 67 of 67; 8 skills — 8 of 8; SERVER SUCCESS". The
  live poll — the same **77 of 77**.
* **The surface adds up, and it is named rather than swallowed:** 144 declared names = **77** the
  panel needs + **67** new in **16** families; seven of our former names are no longer declared by
  this version, and the panel never calls them. **An unknown name at the engine still fails the
  probe** — the next version must be noticed before people meet it.
* **The phone on this pairing is checked in isolation:** the phone self-test — code 0, **74 of 74**,
  no failures; the `ready` frame of the subscription arrives **13 ms** after the channel opens. The
  allow-list is still **13 calls** — not one new.
* **And a live agent question from a real phone went through** (19.10.2026, panel 2.2.1 with this
  engine): the question was shown to the phone, the answer went to the DSH server under the same
  event, and the agent's run continued after the answer. One question, one answer, one device. This
  is the check the 0.2.x line had been missing.

### The main button installs 0.2.1 from its own lock

There are **two** verified versions now, and the hints recommend the new one — so the install road had
to be chosen differently. The main road (checked against the primary lock, the very one the surface
snapshot was taken on) is now open **only for the primary lock's version**; any other version,
including the verified `0.2.1-rc.2`, goes its own road, and the installer checks **its** lock. The old
rule would have produced exactly the "two truths about one tree" lie: the card says "recommended
0.2.1-rc.2" while the button installs `0.1.5-rc.2` through the primary lock.

### How to roll back to 0.1.5-rc.2

* **It stays the previous verified version;** its **588-package** set is pinned in the build too, and
  returning does not mean "installing again": both versions live on disk.
* **What to read before a rollback:** the session journal of the 0.2.x line is **v4**. Sessions opened
  under `0.2.1` are **not seen by `0.1.5-rc.2` and it does not say so** — they vanish from the list
  silently. The panel warns in numbers before a rollback (since 2.2.0) but does not undo the
  consequence: the history is intact and lies next to it, and visibility comes back with one manual
  step — remove the new journal file (`session.v4.jsonl.zstd`) from the session folder. The panel does
  not do this itself.

### What "rc" means and what it does not promise

* **`rc` is a pre-release, and that is said plainly.** Every version DSH publishes is rc or alpha —
  upstream releases nothing without a pre-release suffix: it expresses "stability" by moving a version
  to the `latest` tag. On 19.10.2026 both `latest` and `next` pointed at `0.2.1-rc.2` — that is the
  sign by which the version became the default.
* **The number does not promise that the set under it stays the same:** upstream may re-release or
  replace the version. "Verified" means "the panel measured this version and this set", not "upstream
  will not change anything any more". The set's sum is pinned in the build: a swapped set will not be
  installed.

### Balance: the amounts now carry their currency

The balance line no longer makes you guess the currency: `USD: topped up … · bonus …` and
`CNY: topped up … · bonus …` — it says which is which (in the panel's line they are separated by `|`).

### What 2.3 does not have

* **Subagent sessions do not open from the phone** — they need the parent address. The phone does not
  show them, and the number of hidden ones goes to the panel log.
* **The phone works only in its own network:** the page opens over a local-network address, the panel
  does not go outside, and there is no HTTPS.
* **Question delivery in `timed` mode is not supported.** If upstream switches to it, the old answer
  door is not enough: `userQuestions/attachWait` and `userQuestions/answer` will be needed — that is
  an extension of the phone surface (the 13-call allow-list), a separate decision. Today the panel
  honestly calls such an answer a refusal.
* **There is no code signature** — SmartScreen will warn on the first run.

**Under the hood (no visible effect):** the version hints (`engine-hints.json`) now name **two**
verified entries, the current one **first**; `0.2.0-rc.2` stays unverified — there is no set for it,
and the 0.2.x line is `0.2.1-rc.2`. The 84-name snapshot is still taken on `0.1.5-rc.2`
(`MeasuredOnVersion` is the version measured on, not "the default version"; the default version is set
by **one** place — the order of verified entries in the hints file). Checks are now **2153** (was
2095): "the main button installs 0.2.1 from its own lock" and "five caveats about 0.2.1 exist in all
three languages" were added. Build — 0 warnings / 0 errors; the personal-data, encoding and dictionary
guards — 0.

## 2.2.1 - 2026-10-19

**A background notification can no longer take the panel down, and the panel's own log no longer
goes into a copy.** The version in "About": **2.2.1**.

### Tray notification: a refusal does not kill the panel

Before 2.2.1 the automatic notification was shown without protection: if the tray icon was not
installed, or the shell refused to show the message, the exception went up and **the panel closed**.
That is how it died on the startup backup when the backup failed — caught by a run on a clean Windows.
Now the show goes through one door: any exception is caught, **the reason is written to the panel log**
("notification not shown: <what> — <exception type>: <text>"), and the panel keeps running. The line
"notification shown" is written only after a successful show — before, it appeared even when the show
threw.

### Copy: the panel log is not archived

The panel's own log (`state\panel.log`) is no longer put into a copy: the panel keeps it open, and
a copy reading that file sometimes failed ("copy not taken") — a person could be left without the
nightly copy. **What this means for restoring:** the panel log is **not** brought back by a restore —
it is not the person's data but the history of what the panel did; after a restore it starts over.
Settings, state, sessions and working folders are restored as before.
## 2.2.0 - 2026-10-18

**The panel keeps TWO versions of the DSH server side by side, installs each from its own verified
set, and before switching versions counts the sessions on disk — so that it can name in numbers what
will become invisible instead of staying silent.** The version is in "About": it says **2.2.0**.

### DSH server: two versions side by side, each from its own set

* **A second verified set — for the 0.2.x line.** `0.2.1-rc.2`: **683 packages**, every record with
  its own sum and address, the set file is **437 323 B** and its sum is pinned in the build. The set
  is checked by a real `npm ci`: code 0, **20.7 s**, **63 639 files / 692.8 MB**, and the tree
  installed from it passes the **panel's full probe**: "77 needed — 77 of 77 recognised; 67 known new
  — 67 of 67; 7 retired — 7 of 7; 8 skills — 8 of 8; SERVER SUCCESS".
* **Each version is installed from its own set:** the version's folder gets its own files, and a
  foreign set is never applied to it. The disk now holds **two** versions — the current and the
  previous one (**906.4 MB** together); the previous one stays after a rollback too, so "return" is
  not "install again". The rollback was checked live on two versions (its self-test takes
  **41.2 s**): "the new folder stayed — we keep the current and the previous one".
* **A fresh version that has its own set can be installed — but only by a separate button with a
  confirmation.** The line about it says honestly: "fresh {version} — newer, but the panel has not
  verified it". **Verified remains `0.1.5-rc.2`:** "it has a set" and "it is verified" are different
  things, and the panel does not mix them.
* **The Node threshold is taken no weaker than the strictest of the sets:** one Node serves both
  versions, and a rollback will not raise the previous version on an unsuitable Node.

### Session journal: a number before a version switch, not silence after

* **The panel counts the session journal format itself — from the disk, by file names.** It reads
  not one byte of content: the names `session.vN.jsonl*` inside session folders are what is counted.
  Five numbers: sessions in total, with the new journal, with the old one only, with no journal at
  all, and a separate count of the "flat" layout. An unfinished record (`.tmp`) does not count as a
  format; a missing folder is an honest zero, while an unparsable argument is **"could not find
  out"** — a separate answer, not a zero.
* **The question before a rollback names the number** — for example: "DSH server 0.1.5-rc.2 will not
  see **5 of 197** sessions: their journal is already in the new format, and it will not say so —
  they will disappear from the list silently. The history is intact and lies next to it in the file
  of the old format; visibility can be returned in one step — remove the new journal file
  (session.v4) from the session folder; the panel does not do this itself."
* **After a successful rollback the panel repeats the number itself** — in the past tense. That is
  not politeness: the engine never speaks about the sessions that disappeared (measured: the `0.1.5`
  list became **197 → 196**, with neither a field nor a word in the answer, stderr **0 bytes**), so
  only the panel can say it.
* **The question before an update says how many sessions will move** on the first opening under the
  new version: "Right now **192 of 197** sessions are in the old format". This works for the separate
  button of an unverified version too: the window used to compose the question text itself and lost
  everything the door added — that is, the journal warning would not have reached the person exactly
  where an unverified version is installed. The defect was found and fixed by this release.
* **The migration is lazy and the history is not lost.** Measured on a copy of the profile (197
  sessions: 179 subagent and 18 root): the old journal file is not rewritten (its SHA-256 matched),
  a new file appears next to it. Records **261 → 261** and **1043 → 1044** (the migration added one
  service record), all record kinds matched, the three token counters are the same, and the new file
  is **26.7 %** and **35.8 %** smaller than the compressed old one.
* **A falsehood about the mechanism is fixed** in three dictionaries: it used to say "sessions
  **created** by version {0} will not be opened by the old DSH server" — by measurement, the session
  that was merely **opened** under the new version is hidden too. Now it says "opened under".

### Phone

* **The phone shows only the conversations that can be opened.** Measured: **179 of 197** sessions
  are subagent ones, and they cannot be opened without the parent address — the rows were in the list
  while there was no way to open them. Now only root sessions are given to the phone, and the hidden
  ones are **not hidden silently**: a line goes to the panel's journal — "subagent sessions not shown
  to the phone: 179 of 197" — and it repeats only when the number changes. No new call, path or
  address was added: the allow-list is still **13 calls**.
* **An answer the server did not accept is no longer passed off as sent.** If the server answers "not
  accepted" (`200 {ok:true, value:false}` — its answer when the profile is `mode: legacy`), the phone
  gets a refusal: "The DSH server did not accept the answer — the question is still open. Answer
  again or cancel the run", not "the answer has been sent".
* **The gate does not come up on a wildcard address.** `0.0.0.0` and `::` are refused with a reason:
  that would open it to the whole network rather than to one machine. Previously this was a promise
  in a comment, while the check only asked "does the string parse as an address".
* **A profile copy with the new journal is checked by a real 7-Zip:** both the archive and the
  restore return all five journal files byte for byte.

### Backups

* **The copies window no longer gets stuck.** The answer of a background job could fail to reach the
  window (measured: 1 failure in 5 full runs under load), and the window stayed busy forever. Now the
  window takes its own thread and answers through it — someone else's forgetfulness does not disarm
  it. The product's behaviour has not changed: the copy still runs in the background, the window is
  busy while it works and unblocks when it ends.
* **A backup archive appears in one appearance instead of flickering.** The final name used to appear
  and disappear (measured before: in 6 runs out of 6 — three appearances and two removals of the
  final name per copy; after — a single event). Now the archive is written under `<name>.part`,
  checked by `7z t` **before** publication and published by a single move; a stray `.part` does not
  count as a copy. The same was done in the update archive.

### What 2.2 does not have

* **The 0.2.x line is not declared verified.** Verified remains `0.1.5-rc.2`; `0.2.1-rc.2` has its own
  set and can be installed by a separate button with a confirmation, but the words "the panel has
  verified it" do not appear because of that.
* **The panel does not remove `session.v4` itself** and does not "fix" the journal layout: that is a
  person's data, and the step is named in words rather than done silently.
* **Subagent sessions do not open from the phone** — they need the parent address. The phone does not
  show them, and the number of hidden ones goes to the panel's journal.
* **There is still no code signing** — SmartScreen will warn on the first run.

**Under the hood (no visible change):** the server surface catalogue knows the second line: on
`0.2.1-rc.2` there are **144** names declared — **77** of ours are alive, **7** are retired, **67**
are new in **16** families (on `0.2.0-rc.2` — 140, 63 and 13; the lines are not subsets of each
other). The probe calls only the 77 needed live, names the absence of the retired ones in a line, and
**an unknown name at the engine still fails the probe**: the next version must be noticed before
people are. The version registry reads **both** marks — `latest` and `next` — and takes the senior
one: the old "fresh = latest" hid a fresher version entirely. The build in CI is pinned to SDK
`10.0.402` (`10.0.401` failed `restore --locked-mode` with `NU1004` —
`Microsoft.NET.ILLink.Tasks`; the sets were taken on `10.0.402`), and a test run cleans up after
itself in `%TEMP%\dsh-panel-tests` (was 66 folders / 28.54 MB — now 0). The test count is **2084**.

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


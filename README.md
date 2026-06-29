# FileDrift

File comparison and verification tool for Windows. Compares source and destination directory trees — checking names, sizes, timestamps, hashes, and ACLs — with direct MFT enumeration on local NTFS volumes and parallel SMB enumeration for remote shares.

It can also copy source→destination to reconcile differences it finds, which is handy for small fixes and migration cleanup. That copy capability is a convenience, **not** a general-purpose bulk-copy engine — for large or performance-critical transfers, reach for `robocopy` or another dedicated tool. FileDrift's focus is verification and migration sign-off: knowing, and being able to prove, that two trees match.

![FileDrift wordmark](FileDrift-wordmark.png)

> **A note on authorship:** the code in this project is generated in its entirety by [Claude Code](https://claude.com/claude-code) (Anthropic's AI coding agent), directed and reviewed by the maintainer. This is stated up front in the interest of transparency – some people are rightly particular about AI-generated code, and you should weigh that for your own environment.

> **No support:** this tool is provided as-is, with no warranty and no support. It is shared for transparency and reference. Issues and pull requests may not be monitored or actioned, and there is no commitment to fixes, updates, or assistance of any kind.

---

## Use cases

- **Pre-migration verification** — confirm a file server copy landed correctly before cutting over
- **Post-migration sign-off** — produce a signed report showing zero drift between source and destination
- **Ongoing drift detection** — scheduled or ad-hoc comparison of two shares to catch unexpected changes

## Requirements

- Windows 10/11 or Windows Server 2016+
- .NET 9 runtime (bundled in self-contained release builds — no separate install)
- Administrator privileges for MFT enumeration on local volumes; standard read access is sufficient for SMB paths

## Architecture

```
FileDrift.sln
├── src/FileDrift.Core/      — engine: enumeration, comparison, hashing, ACL checks, run history
├── src/FileDrift.App/       — WPF GUI (FileDrift.exe)
├── src/FileDrift.Cli/       — console CLI (FileDrift-CLI.exe)
└── tests/FileDrift.Core.Tests/ — xUnit tests for the engine
```

**FileDrift.Core** is a pure C# class library with no UI dependency. Both the GUI and CLI call it directly. Key interfaces:

| Interface | Purpose |
|---|---|
| `IFileEnumerator` | Enumerate a directory tree (MFT or SMB implementation) |
| `IRunRepository` | Persist and query run history (SQLite implementation) |
| `ICredentialStore` | Store and retrieve credentials (Windows Credential Manager implementation) |

**FileDrift.App** is the WPF GUI (`FileDrift.exe`). **FileDrift.Cli** is a separate console executable (`FileDrift-CLI.exe`) for headless and scriptable use — a real console-subsystem program, so the shell waits for it, output is synchronous, and exit codes are reliable in scripts and scheduled tasks.

## CLI usage

```
FileDrift-CLI preflight --src \\server\share --dst \\server2\share2
FileDrift-CLI verify    --src \\server\share --dst \\server2\share2 [--depth full] [--acl] [--threads 8]
FileDrift-CLI reconcile --src \\server\share --dst \\server2\share2 [--acl] [--yes]
FileDrift-CLI report    --id <run-id>
FileDrift-CLI history   [--last 10] [--src \\server\share]
```

`reconcile` runs a verify, then copies source→destination to fix what differs. It is **non-destructive** (never deletes destination-only files; permissions are only added) and **previews by default** — it writes nothing unless you pass `--yes`. This makes it safe to schedule: run without `--yes` to see the plan, add `--yes` to apply.

### Credentials

The CLI never takes a password as an argument. Credentials are stored in **Windows Credential Manager** (under the `FileDrift:` convention) and entered through a masked prompt. Manage them with the `credential` command:

```
FileDrift-CLI credential add --share \\server\share     # prompts for username + password (hidden)
FileDrift-CLI credential add --default                  # fallback used for any share with no entry of its own
FileDrift-CLI credential list                           # show what's saved (passwords are never shown)
FileDrift-CLI credential remove --share \\server\share
```

Once saved, `verify`, `reconcile`, and `preflight` **resolve the credential automatically** from the path's share root (falling back to the default), so the common case needs no credential flag:

```
FileDrift-CLI reconcile --src \\server\share --dst D:\target --yes
```

To force a specific saved entry, pass `--cred-source` / `--cred-dest` with its target name (e.g. `"FileDrift:\\server\share"`). Entries are also visible and editable in the app's **Credentials** page and under Control Panel → Credential Manager. (Windows' built-in `cmdkey /generic:"FileDrift:\\server\share" /user:"DOMAIN\user"` works too.)

CLI output is a **human-readable table** in an interactive console (handy for quick lookups) and **JSON** when the output is piped or redirected — so scripts always get JSON. Force either with `--table` or `--json`. Pipe the JSON to `jq` or PowerShell's `ConvertFrom-Json` for scripting.

## Enumeration strategy

FileDrift automatically selects the enumeration method based on the target path:

- **Local NTFS volume** (`C:\`, `D:\`, etc.) — USN Journal via `FSCTL_ENUM_USN_DATA`. Enumerates millions of files in seconds without touching file data.
- **UNC or mapped drive** (`\\server\share`) — parallel SMB enumeration using `Parallel.ForEach` over directory entries.

Running FileDrift on the file server itself unlocks MFT enumeration for large local jobs. Remote share mode still benefits significantly from parallelism versus sequential `Get-ChildItem`.

## Where to run FileDrift

For a reconcile (copy) job, the machine you run FileDrift from affects both throughput and reliability:

- **On the destination server (recommended).** Writes and ACL changes are local, and only the source is read over the network. This is the best choice for many-small-files jobs, where per-file round-trips dominate — keeping the writes local eliminates roughly half of them. It also avoids the Kerberos double-hop problem and runs ACL changes locally rather than against a remote target.
- **On the source server (second choice).** Use this when you cannot run on the destination (for example, the destination is a NAS or appliance). Source reads are local; only the writes cross the network.
- **On a third machine or your laptop (avoid for large jobs).** Copy data flows source → this machine → destination, which is two network hops and roughly double the wire traffic. Copying between two remote servers from a third box can also trigger the Kerberos double-hop "access denied" problem. Acceptable for small, ad-hoc jobs; not for a migration.

Hashing (Full depth) and ACL processing happen wherever FileDrift runs, so use a capable host — not an underpowered laptop — for large verifies. Note also that MFT enumeration only works on a local NTFS volume, so running on the source or destination unlocks it for that side.

## Verify depth

| Flag | Checks |
|---|---|
| `--depth quick` | Name and size |
| `--depth standard` (default) | Name, size, and last-write timestamp |
| `--depth full` | Name, size, timestamp, and SHA-256 content hash |

Add `--acl` to any depth to also compare security descriptors.

## Run history and sign-off

Every run is stored in a local SQLite database (`%APPDATA%\FileDrift\history.db`). After reviewing results you can attach a sign-off note, which is included in the exported report.

## Building from source

```
git clone https://github.com/FileDrift/FileDrift.git
cd FileDrift
dotnet build
dotnet run --project src/FileDrift.App
```

Self-contained single-file publish:

```
dotnet publish src/FileDrift.App -c Release --self-contained -p:PublishSingleFile=true -o publish/
```

## Changelog

Versioning follows `major.minor.bugfix`. The `0.x` series is pre-release; `1.0` is reserved for the first released build.

### 0.9.3 (2026-06-29)

- **Human-readable CLI output.** CLI commands now print an aligned table in an interactive console (handy for quick lookups like `FileDrift-CLI credential list` or `FileDrift-CLI history`) and JSON when the output is piped or redirected, so scripts are unaffected. Force either with `--table` or `--json`.

### 0.9.2 (2026-06-26)

- **CLI credential management.** New `FileDrift-CLI credential` command: `add` saves a credential to Windows Credential Manager through a **masked password prompt** (never echoed or passed as an argument), `list` shows what's saved (passwords are never shown), and `remove` deletes one. No more needing `cmdkey` or Credential Manager directly.
- **Automatic credential resolution.** `verify`, `reconcile`, and `preflight` now resolve the saved credential for a path's share root automatically (falling back to the default), so the common case needs no `--cred-source` / `--cred-dest` flag.
- **Clearer scope up front.** The README now states plainly that FileDrift can copy/reconcile differences but is a verification and migration-sign-off tool, not a bulk-copy replacement for `robocopy`.
- **Fixed a misleading post-cancel message.** After a *stopped* reconcile, the activity log no longer claims "re-run Verify to confirm the destination now matches" — it now says the reconcile was stopped and to re-run Verify to see what still differs.

### 0.9.1 (2026-06-26)

- **The CLI is now a dedicated console executable, `FileDrift-CLI.exe`.** Previously the GUI `FileDrift.exe` doubled as the CLI by attaching to the parent console — which made `cmd.exe` return the prompt *before* the output printed (it looked like it hung waiting for a keypress) and made batch sequencing and exit codes unreliable. The CLI is now a proper console-subsystem program: the shell waits for it, output is synchronous, and exit codes are reliable for scripts and scheduled tasks. **Breaking:** CLI commands now use `FileDrift-CLI` (for example `FileDrift-CLI verify …`); the GUI `FileDrift.exe` no longer accepts command-line arguments.

### 0.9.0 (2026-06-26)

- **Automated test suite.** Added `FileDrift.Core.Tests` (xUnit, 27 tests) covering ACL→readable translation, glob matching, comparison classification + timestamp tolerance, the full reconcile path (live byte progress, hard/soft cancel, partial cleanup, last-file reporting, metadata/attribute preservation, read-only overwrite), reparse-point skipping, inaccessible-path tracking, and the history-database round-trip + v1→v2 schema migration. The correctness fixes from 0.8.x are now permanent regression tests.
- **`reconcile` is now available from the CLI.** `FileDrift-CLI reconcile --src … --dst …` previews the plan and writes nothing; add `--yes` to apply. Supports the same options as `verify` (depth, `--acl`, credentials, date range, exclude, strict) and emits JSON with the plan and result. This makes reconcile schedulable for headless/automated use.

### 0.8.2 (2026-06-26)

- **Dark-mode contrast.** The verify summary tiles, the activity-log pane, and the Credentials cards now use the app's standard card surface and border brushes (`CardBackgroundFillColorDefaultBrush` / `CardStrokeColorDefaultBrush`) instead of a near-transparent fill that disappeared against the dark Mica backdrop. They read as defined panels in dark mode now; light mode is unchanged.

### 0.8.1 (2026-06-26)

- **Read-permission failures are now prominent.** When a verify can't read one or more paths, a banner appears with the count. If the **source or destination root itself** was unreadable — so the results aren't a valid comparison at all — the banner is red and says so explicitly, rather than the result reading like a clean "0 differences". Previously this was only an appended note on the status line.
- **Inaccessible count is persisted to history.** Each run now records how many paths were skipped as unreadable, shown in a new "Inaccessible" column on the History page. The history database migrates automatically; existing runs show 0.

### 0.8.0 (2026-06-26) — correctness hardening

- **Inaccessible paths are no longer silent.** Files and folders that can't be read during a verify (access denied or I/O error) are now counted, flagged in the summary ("*N path(s) could not be read and were skipped – the comparison is incomplete*"), and listed in the run log. Previously they were skipped silently, so a "zero drift" result could quietly omit files nobody could read.
- **Reparse points are skipped on SMB scans.** The SMB enumerator no longer follows directory junctions, symlinks, or mount points (matching the MFT scanner), avoiding infinite loops and double-counting on file servers that use them.
- **Reconcile preserves more metadata.** Copies now also set the source's creation time and user-settable attributes (read-only, hidden, system, archive). A read-only destination is cleared first so an overwrite doesn't fail.
- **Long-path (>260) handling confirmed.** Verified that enumeration and reconcile already handle paths longer than 260 characters on .NET 9 — no change required.

### 0.7.0 (2026-06-25)
- **Live transfer rate** — during a reconcile copy, a write-throughput readout appears next to the status line (e.g. `112 MB/s`). It samples every second and shows a rolling average (~10s window) so it's not bursty, reads "–" when nothing is moving (between files, ACL-only phase), and is labelled as FileDrift's write throughput (which can briefly run ahead of the wire over buffered SMB). No ETA — by design.
- **Preview summary banner** — running Preview now shows an inline, dismissible banner with the plan headline (`Copy 5 · Overwrite 0 · 425 ACL · 92.8 GB to write`), amber when any overwrite would replace a newer destination file. It auto-hides when a run starts; full per-file detail still goes to the preview log file.
- **Graceful close while busy** — closing the app during an operation now asks first. A verify/preflight gets a simple confirm; a **reconcile reuses the three-way stop prompt** (Stop now / Finish current / Continue) and the app waits for the copy to actually stop — removing the in-progress partial (Stop now) or finishing the current file (Finish current) — before exiting.
- **Synced live readouts** — the transfer rate and the activity-log rollup now refresh on the same tick, in phase, instead of drifting.
- The activity-log refresh control is now labelled **"Live refresh"** (Verify page and Settings), since it paces both the activity log and the transfer-rate readout.

### 0.6.0 (2026-06-25)
- **Themed dialogs** — every confirmation now uses the app's own Fluent style (WPF-UI) instead of the OS message box, so they match the window's theme, accent, and corners. Covers the Reconcile confirmation, the three-way Stop prompt, and the Settings preset prompts. The Stop prompt's buttons read **Stop now** (red) / **Finish current** / **Continue file copy**.
- **Activity-log rollup** — during a reconcile of many small files, the throttled on-screen log now shows how many files and bytes were copied since the previous line (e.g. `+312 files, 4.2 GB · Copy …`), so it reads as a periodic summary rather than appearing to skip files. The per-run log file still records every file.
- **Log-refresh slider on the Verify page** — the activity-log refresh interval can now be adjusted right on the Verify page (next to the log), in addition to Settings; the two stay in sync and either persists.
- **Persistent "stopping" status** — after choosing **Finish current** on a cancel, the status line now keeps showing "stopping after this file" while the current (possibly large) file finishes, instead of reverting to normal progress text.
- **Cleanup is logged** — choosing **Stop now** now records which partially-written file was deleted (`Cleanup – deleted partial copy: …`) in the activity log. When a reconcile stops, it also logs the file that was actually copied last (`Stopped – last file copied: …`), independent of the activity-log sampling.
- En dashes throughout the UI copy.

### 0.5.0 (2026-06-25)
- **Byte-level Reconcile progress** — the progress bar and status now advance *within* a file as it copies (bytes copied / total bytes), not just once per completed file. A job of a few very large files no longer looks frozen for minutes at a time.
- **Cancel refinement** — the Cancel button is now red (it appears only during a run). Cancelling a Reconcile mid-copy prompts for how to stop: **Stop now** (abort the current file and delete its partial copy), **Finish current** (let the file in progress complete, then stop before the next), or **Continue file copy**. Verify and Preflight are read-only and still stop immediately. The summary reports how many files copied and whether a partial was removed.
- **Human-readable ACLs** — Reconcile Preview and the report now translate raw SDDL into plain language (for example `A;;FA;;;BA` becomes "Allow Administrators: Full control"), resolving well-known and domain accounts, combined rights, and hex access masks, with a fallback to the raw SID when a name can't be resolved.
- **Activity-log refresh slider** — the on-screen activity log's refresh interval is now configurable in Settings (default 3s, floor 0.5s) and takes effect **live during a run**. The per-run log *file* still records every line.
- **Presets** — added a **Default** entry that resets appearance to the system theme and stock colors, the ability to **save the current theme and colors as a named preset** (stored in `presets.json`), and a **custom accent hex input** with a live preview swatch.
- **Remember window size and position** — the main window's size, position, and maximized state are restored on the next launch, clamped to the visible screen if a monitor has gone away.
- **Off-thread plan build** — Reconcile's plan is now built off the UI thread, removing the brief pause on Preview/Reconcile for large difference sets.

### 0.4.7 (2026-06-24)
- **Reconcile Preview is now detailed and logged.** Each action shows the explicit ACEs and owner it would apply (not just "Set ACL"), the complete preview is written to its own `preview-<timestamp>.log` (openable via the Open log file button), and the on-screen list is capped at 50 with the rest in the file. Faster too — no longer dumps every action to the screen.

### 0.4.6 (2026-06-24)
- Larger default window (1280×860, was 940×640) — gives the results grid much more height for reviewing differences. Still resizable; minimum unchanged.

### 0.4.5 (2026-06-24)
- **Results freeze fixed properly** (0.4.4's viewport binding lagged at 0 on the first layout pass, so it didn't hold — confirmed by repro showing 28s initial / freeze on nav-back). The grid now gets a stable height cap tied to the window size (set in code on load + resize), which reliably virtualizes it. Repro: 20k rows go from ~28s to ~0ms on both initial display and navigation back.

### 0.4.4 (2026-06-24)
- Attempted results-freeze fix by binding page height to the viewport (superseded by 0.4.5 — the binding lagged and didn't hold across navigation).

### 0.4.3 (2026-06-24)
- Added an **Open log file** button next to Copy activity log — opens the current run's complete log (including the full difference list) in the default editor.

### 0.4.2 (2026-06-24)
- Fix permanent freeze when displaying results of a run with many differences. The results grid wasn't virtualizing, so it tried to realize all rows (tens of thousands) at once during layout. The grid now shows the first 5,000 differences (it's for spot-checking — Reconcile still acts on all of them), the **complete difference list is written to the run's log file**, and row virtualization is enabled.

### 0.4.1 (2026-06-24)
- Enumeration counts now say "entries" instead of "files" — they include folders when ACL mode is on.
- Start/End date fields show the full date (short format), no longer truncated.

### 0.4.0 (2026-06-24)
- **Per-run activity log files** — each verify/preflight/reconcile writes a complete, unthrottled log to `%APPDATA%\FileDrift\logs\<verb>-<timestamp>.log`. The on-screen log stays calm (throttled); the file captures every line, and survives a crash. The saved path is shown when a run finishes.
- **Folders-only ACL scope** — with Compare ACLs on, a scope selector chooses **Files + folders** (default, complete) or **Folders only** (fast — reads only folder permissions, skipping the bulk of per-file ACL reads over SMB). Folders-only misses a permission set directly on an individual file, so the report/summary is clearly labeled "ACL scope: folders only" and accuracy stays the default. CLI: `--acl-folders-only`.

### 0.3.2 (2026-06-24)
- Fix freeze when displaying results of a large ACL verify. The page kept all comparisons (1M+), each carrying a full SDDL with ACL mode on — multiple GB pinned after the run. Now only the differences are retained (all the grid and Reconcile need); the matched records are released.

### 0.3.1 (2026-06-24)
- Activity log is less chatty during scans: scan/enrich lines are throttled to one every 2s (was ~5/sec). The progress bar and status line still update on every report, so the live view is unchanged.

### 0.3.0 (2026-06-23)
- **Explicit-permission ACL comparison** — Compare ACLs now compares only **explicit (non-inherited)** permissions on both files and directories, ignoring inherited ACEs (which differ structurally between two server roots). This turns a cross-server compare from "everything differs" into a short list of deliberate permission drift. The report shows both directions: source permissions **missing on the destination** and destination explicit permissions that **differ from the source**.
- **Directory enumeration under ACL mode** — folders are enumerated and compared when Compare ACLs is on (explicit permissions usually live on folders and inherit down).
- **Additive ACL reconcile** — Reconcile adds the source's missing explicit ACEs to the destination, **preserving** the destination's own permissions and inheritance (never strips them — the destination may be a live target). Missing folders are created.
- **Enforce ownership** (optional, off by default) — also require/copy the owner. Strict mode forces it on. CLI: `--enforce-ownership`.

### 0.2.0 (2026-06-23)
- **Date range filter** — replaced the single "As of" cutoff with **Start** and **End** dates (on last-modified). Start is symmetric (ignores files older than it on both sides — for consolidating into a destination with existing older content); End is asymmetric (excludes only newer destination-only files, so a migrated file edited later still compares as different rather than going missing). CLI: `--start` / `--end`.
- **ACL reconciliation** — Reconcile now also copies permissions (owner/group/DACL) when the verify compared ACLs; an ACL-only difference is fixed without rewriting the file.
- **System theme** — theme now defaults to **System** (follows OS light/dark, live), alongside Light/Dark. Color presets trimmed to Trans Rights.
- **Navigation no longer orphans a running verify** — the Verify page is cached, so visiting Settings/History mid-run and returning keeps the live activity, progress, and inputs.
- **Preflight parity** — preflight shows per-directory scanning detail and reports source/dest file counts and sizes in clearly-labeled tiles.

### 0.1.1 (2026-06-23)
- Fix crash when typing a UNC path into the source/destination box (partial-path parsing threw on the second backslash).
- Fix the app becoming unresponsive when verifying very large trees (1M+ files): the results grid now lists **differences only** (matched files are summarised by the count tile), projected off the UI thread and bound in one pass, capped at 100,000 rows.
- Throttle the activity log on large shares so it can't flood the UI thread.
- Treat an existing Kerberos/domain session as success on authenticated shares (no more false auth failures on domain machines).
- Move the Copy button below the activity log and make it accent-colored.

### 0.1.0
- Initial C# / WPF / .NET 9 rewrite. Preflight, verify (MFT + SMB), as-of cutoff, Reconcile (source→destination), run history, Windows Credential Manager, named color presets.

## License

[GNU General Public License v3.0 or later](LICENSE) (`GPL-3.0-or-later`).

FileDrift was previously published under the PolyForm Noncommercial License 1.0.0. As the sole copyright holder, the maintainer has relicensed the project — including all earlier versions — under GPL-3.0-or-later. You may use, modify, and redistribute it under those terms; derivative works and distributions must remain open under the same license.

Third-party dependencies keep their own licenses (WPF-UI, System.CommandLine, and Microsoft.Data.Sqlite are MIT-licensed and GPL-compatible).

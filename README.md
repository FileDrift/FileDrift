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

## Roadmap

Current version: **1.0.0-rc20** — feature complete for local-filesystem and SMB verify/reconcile; in release-candidate testing.

**Shipped toward 1.0:** verify (MFT + SMB enumeration, quick/standard/full depth, ACL comparison), non-destructive reconcile with preview, run history with age/sign-off filtering, run sign-off (GUI + CLI, with a protected operating-account audit trail), tamper-evident HTML certificates of verification, a Compliance tab for single/batch certificate checks, history clear/import/export (signed-off runs are never deletable or overwritable), Windows Credential Manager integration (GUI + CLI, including clear-all), a dedicated console CLI (`FileDrift-CLI.exe`) with human-readable table output, and local Authenticode code signing. Relicensed to GPL-3.0-or-later ahead of any public release.

**Remaining before 1.0:** security review complete (rc10); cutting an actual public release remains (signing via an internal CA now, SignPath Foundation pending approval for public-trust signing). When setting up SignPath, pin the actions in `.github/workflows/release.yml` (`SignPath/github-action-submit-signing-request`, `actions/checkout`, `actions/upload-artifact`, `actions/setup-dotnet`) to full commit SHAs instead of mutable tags **before** adding the `SIGNPATH_API_TOKEN` repository secret — a hijacked action tag is the classic path to CI secret exfiltration.

**Post-1.0 (deliberately deferred):** certificate PDF export and Authenticode-signing the certificate file itself (today's SHA-256 fingerprint detects tampering but isn't a cryptographic signature); object storage and SharePoint/OneDrive targets (a real feature requiring a write abstraction, not a small addition).

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
FileDrift-CLI signoff   --id <run-id> [--by "Approver Name"] [--note "..."] [--force]
FileDrift-CLI certificate --id <run-id> [--out file|dir]   # generate an HTML certificate
FileDrift-CLI certificate --verify <file>                  # re-check a certificate's integrity
FileDrift-CLI history   [--last 10] [--src \\server\share] [--since 30d] [--signed-off true|false]
FileDrift-CLI history export --out history.json [--since 90d] [--src ...] [--dst ...]
FileDrift-CLI history import --in history.json [--overwrite]
FileDrift-CLI history prune [--older-than 90d] [--yes]     # dry-run without --yes
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

Every run is stored in a local SQLite database (`%APPDATA%\FileDrift\history.db`) and listed on the **History** page (and via `FileDrift-CLI history`).

Once you've reviewed a run's results, you can **sign it off** to record that a named party accepts them:

- **In the app** — open History, select the run, and click **Sign off**. The dialog pre-fills the accountable party with the Windows account you're running under; you can change it (e.g. to sign on behalf of a named approver) and add an optional note. The History page then shows the sign-off time and party. Once signed off, you can view the certificate immediately — no separate export needed.
- **At the CLI** — `FileDrift-CLI signoff --id <run-id> [--by "Approver Name"] [--note "..."]`. Without `--by`, the current Windows account is recorded as the approver. Re-signing an already-signed run requires `--force`.

The Windows account that actually performed the sign-off is **always captured and stored separately** from the editable "signed off by" name, so overriding the approver never erases who operated the tool. When the two differ, the run is flagged as a delegated sign-off. `FileDrift-CLI report --id <run-id>` prints the full sign-off block (time, approver, operating account, delegated flag, note).

### Certificate of verification

You can export a self-contained **HTML certificate** for any completed run — from the main **Verify** page (the *Sign off* and *Export certificate* buttons on the right of the live-refresh bar act on the run that just completed), from the **History** page (*Export certificate* for any past run), or via `FileDrift-CLI certificate --id <run-id>`. The **Compliance** tab is the home for checking certificates: *Verify certificate…* checks a single file, and *Verify folder…* re-checks every `.html` certificate under a folder (recursively) and lists the results altered-first — handy for auditing an archive of filed certificates. (The History page has the single-file *Verify certificate…* too, for convenience.) Both are the GUI equivalent of `certificate --verify`. It records the run's result verdict (MATCH / DIFFERENCES FOUND / INCOMPLETE), the options it ran with, the file counts, the sign-off block, and — if the run's differences were reconciled — a Reconcile section with the total data copied and file counts, and is styled to print cleanly (use the browser's *Print → Save as PDF*).

### Filtering, clearing, and moving history

The **History** page has a **Show** filter (All / Last 7 / 30 / 90 days) that scopes both the grid and the *Export history…* button; the CLI equivalent is `FileDrift-CLI history --since 30d`. Filter by sign-off state at the CLI with `--signed-off true|false`.

**Clearing history is restricted to unsigned runs, by design.** A signed-off run is a record that someone attested to — deleting it would also orphan any certificate that references it — so neither the GUI's *Clear unsigned…* button nor `FileDrift-CLI history prune` can ever remove one. *Clear unsigned…* lets you pick a scope (all unsigned, or older than 7/30/90 days), shows how many runs match before you commit, and requires confirmation. `history prune [--older-than 90d]` without `--yes` only reports what *would* be deleted; add `--yes` to actually delete.

**Export and import move history between machines or into an archive.** *Export history…* (History page) or `FileDrift-CLI history export --out file.json` writes every field of the matching runs — including sign-off — to a JSON file. *Import history…* or `FileDrift-CLI history import --in file.json [--overwrite]` reads it back in: without `--overwrite`, a run whose ID already exists locally is left untouched; with `--overwrite`, existing runs are updated, **except a locally signed-off run is never overwritten** by an import, protecting its audit trail the same way pruning does. This is a portability convenience, not a security control — the underlying SQLite file isn't itself tamper-proof, so treat an imported history file with the same trust you'd give any other admin export.

Each certificate carries a **SHA-256 integrity fingerprint that covers the entire document** (the fingerprint is blanked to a placeholder while hashing, then substituted in). `FileDrift-CLI certificate --verify <file>` reverses that and re-hashes the whole file, so **any** later edit — a visible number, the watermark, or the embedded facts — flips the result to *altered*. When the run still exists in the local history database, verify additionally reports whether the certificate's embedded facts still match that system of record (`matchesDatabase`). This makes tampering *detectable*; it is an integrity check, **not** a cryptographic signature (Authenticode file signing is on the post-1.0 backlog). A run that has not been signed off is stamped with a diagonal **"NOT SIGNED OFF" watermark laid over the certificate body** — not just the page margin — so an unattested certificate can't be mistaken for an approved one. The layout is sized to print on a single Letter or A4 page.

> Cryptographic (Authenticode) signing of certificates and a native PDF export are on the post-1.0 backlog.

## Building from source

```
git clone https://github.com/FileDrift/FileDrift.git
cd FileDrift
dotnet build
dotnet run --project src/FileDrift.App
```

## Releases & signing

Produce self-contained, single-file release binaries (no .NET install needed on the target) with the helper script:

```
./publish.ps1            # -> publish/FileDrift.exe and publish/FileDrift-CLI.exe (win-x64)
```

**Code signing.** The signing path depends on how you're distributing:

- **Internal / your own builds** — sign with a code-signing certificate you control, by thumbprint. A certificate from an **internal/Enterprise CA** is ideal: it's already trusted on every domain-joined machine, so SmartScreen/AppLocker/WDAC are satisfied with no per-machine setup. Signatures are RFC-3161 timestamped (they stay valid after the cert expires):

  ```
  ./sign.ps1 -Thumbprint <cert-thumbprint>     # signs both exes in publish/
  ./sign.ps1 -SelfSigned                        # generates a dev cert just to test the pipeline
  ```

  A **self-signed** certificate also works but isn't trusted by other machines until you deploy its public key (Group Policy → Computer Config → Windows Settings → Security Settings → Public Key Policies → Trusted Publishers, and Trusted Root). That's a free, standard approach for internal line-of-business apps.

- **Public releases** — publicly-trusted signing happens in CI. The [`.github/workflows/release.yml`](.github/workflows/release.yml) workflow builds the binaries and submits them to SignPath for signing (the private key lives in SignPath's HSM, never in this repo). It needs a SignPath organization/project plus a `SIGNPATH_API_TOKEN` repository secret. The cheapest paid public-trust alternative is Microsoft's Azure Trusted Signing.

### Code signing attribution

Public Windows release binaries of FileDrift are code-signed by [SignPath.io](https://about.signpath.io/), with a free code-signing certificate granted by the [SignPath Foundation](https://signpath.io/).

## Changelog

Versioning follows `major.minor.bugfix`. The `0.x` series is pre-release; `1.0` is reserved for the first released build.

### 1.0.0-rc20 (2026-07-02)

- **Sign off now offers to view the certificate immediately.** Previously the only way to see what a run's certificate looks like was to export it (choosing a save location) and open it separately — there was no quick "just show me" path. After a successful sign-off, choosing *View* now renders the certificate to a temp file and opens it right away, with no save dialog and no separate verify step. *Export certificate* is still the way to keep a permanent copy.

### 1.0.0-rc19 (2026-07-02)

- **Fixed: Sign off and Export certificate went dark on the Verify page immediately after a reconcile**, even though the exact same actions remained available for that same run on the History page. Preview and Reconcile correctly require a fresh verify after a reconcile (the destination changed, so the old diffs are stale) — but Sign off and Export certificate don't act on the diffs, they document a historical fact about the run and its reconcile, which is still perfectly valid the moment the reconcile finishes. The two are now tracked independently, so Sign off and Export certificate stay available on the Verify page for the run you just reconciled, right when the review is freshest, while Preview/Reconcile still correctly demand a fresh verify.

### 1.0.0-rc18 (2026-07-02)

- **Certificates now show total data copied for a reconciled run.** When a run's differences were later reconciled, its certificate gains a **Reconcile** section — reconciled time, data copied (formatted as B/KB/MB/GB/TB as appropriate), files copied, and files overwritten — right below the verification facts. If the reconcile was stopped before finishing, the certificate says so, since the totals then only reflect what completed. Unreconciled runs are unaffected; the section simply doesn't appear.
- This required persisting the reconcile outcome for the first time: the run history database gains four columns (reconciled time, bytes copied, files copied/overwritten, stopped flag), written by both the GUI and `FileDrift-CLI reconcile`. `FileDrift-CLI report` also gains a `reconcile` block reflecting the same data.

### 1.0.0-rc17 (2026-07-02)

- **ETA now recalculates for the final file during a "Finish current" stop, instead of disappearing.** Once a soft stop is requested, the whole-plan ETA correctly stops applying (the run won't reach the rest of the plan) — but the file already in flight is still genuinely copying to completion, and knowing how much longer *it* needs is still honest and useful. The rate readout now shows "~N sec left in this file" for the remainder of that copy, computed from the same smoothed rate. A hard stop (which aborts immediately) still shows no ETA, since there's nothing being timed to completion.

### 1.0.0-rc16 (2026-07-02)

- **Cancel now logs an acknowledgement immediately, not just at completion.** Choosing *Stop now* or *Finish current* during a reconcile updated the status line above the log, but the Activity Log itself stayed silent until the stop actually finished (the partial-file cleanup or "last file copied" line) — with a large in-flight file, that could be a long wait with no record that anything had been requested. A line is now appended to the Activity Log (and the run's log file) the moment the stop is requested, for both the in-app cancel and the equivalent close-while-reconciling prompt.

### 1.0.0-rc15 (2026-07-02)

- **Same fix as rc14, extended to credentials.** Preview and Reconcile also captured the source/destination *credential* selection from the last verify, not the current dropdown choice. Manually switching either credential dropdown after a verify — without re-verifying — left Preview/Reconcile enabled and would silently write using the old credential while the screen showed a different one selected. Picking a different saved credential now invalidates the result exactly like editing a path does.

### 1.0.0-rc14 (2026-07-02)

- **Fixed: Preview/Reconcile could act on a stale source or destination.** Preview and Reconcile always operate on the paths from the *last completed verify*, not on whatever the Source/Destination boxes currently show. If you edited either path after verifying — without re-running Verify — the buttons stayed enabled and would silently reconcile against the old, already-verified paths while the screen displayed the new, unverified ones. Now, editing either path after a verify immediately invalidates the result (Preview/Reconcile/Sign off/Export certificate all disable) and prompts for a fresh verify, exactly as already happens after a reconcile completes.
- **Settings now shows the full release-candidate version** (e.g. `1.0.0-rc14`) instead of just `1.0.0.0`, so build identity is unambiguous during RC testing.

### 1.0.0-rc13 (2026-07-01)

- **Double-buffered reconcile copy (Tier 3 of the pre-release optimization review).** While one chunk is being written to the destination, the next is already being read from the source — so source latency and destination latency overlap instead of adding, approaching the throughput of the slower side alone on network↔disk copies. The pump is a standalone stream-to-stream helper (`StreamPump`) with progress and cancellation on every read and write, deliberately shaped so the planned object-storage write target can reuse it unchanged.
- Byte progress during reconcile now reports the **actual** bytes copied per file rather than the plan's recorded size (they could drift if a file changed between verify and reconcile).
- Cancel semantics are unchanged by design (hard stop still aborts the in-flight file and removes the partial; soft stop still finishes the current file) and remain covered by the engine tests — but the 3-way cancel deserves a manual pass on a real copy job before 1.0.

### 1.0.0-rc12 (2026-07-01)

- **Compare-pipeline and hashing efficiency (Tier 2 of the pre-release optimization review).** No behavior changes — same records, same verdicts, verified by the full test suite and an end-to-end full-depth run:
  - **The verify pipeline no longer copies or rebuilds its million-entry structures.** Enumeration now fills plain dictionaries (hash/ACL enrichment computes in parallel but writes back single-threaded, so the heavier concurrent collections aren't needed), and the comparison consumes those dictionaries directly instead of receiving array copies and rebuilding its own destination index. On large runs this removes two full copies, one full dictionary rebuild, and a matched-paths set — a substantial transient-memory and CPU reduction.
  - **Both sides of a matched pair are now hashed concurrently.** Source and destination are usually different devices (share vs. local disk), so the slower side hides behind the faster one instead of adding to it — up to ~2× on full-depth verifies whose sides have unequal speeds.
  - **Hashing uses the one-shot `HashDataAsync` API** (no per-file `HashAlgorithm` allocation) and opens files with a sequential-scan hint, improving read-ahead and keeping large files from polluting the file cache.

### 1.0.0-rc11 (2026-07-01)

- **Enumeration fast-path (performance, from the pre-release optimization review).** No behavior changes — same records, same results, verified by the full test suite and an end-to-end run:
  - **SMB/remote scans now make one directory-listing pass per folder instead of paying 2–3 extra network round-trips per entry.** Previously each file's size/timestamps and each subfolder's reparse check were fetched with separate calls after the listing; they now come from the same find data the listing already returned. On many-small-files shares — the tool's most common heavy case — enumeration should be substantially faster.
  - **MFT scans memoize directory paths.** Phase 2 used to rebuild every file's full ancestor chain from scratch; each directory's path is now resolved once, so per-file work drops to a lookup and a concatenation on million-file volumes.
  - **Relative paths are computed by substring** instead of `Path.GetRelativePath`'s full re-normalization per record, in both enumerators.
- **Polish:** exclude-pattern matching uses compiled regexes (it runs once per record); the live-refresh slider's settings save is debounced so a drag no longer writes `settings.json` on every snap point; the app now shares one history-repository and credential-store instance across pages (the schema check runs once per launch).
- Documented the `RelativePath` separator convention on `IFileEnumerator` for future non-filesystem enumerators (object storage).

### 1.0.0-rc10 (2026-07-01)

- **Security review fixes** (from a full-project review ahead of the public release):
  - **Fixed: HTML injection into certificates via free-text fields.** The machine-readable facts block was embedded in the certificate unencoded, so a sign-off note containing `</script>` — typed at sign-off, or arriving via a history import file — could break out of the data block and inject live markup/script that ran when the certificate was opened. The block is now HTML-encoded on generation and decoded on verification, so hostile content is inert while fingerprinting and the database cross-check behave exactly as before.
  - **Certificates now carry a Content-Security-Policy** (`default-src 'none'`) as defense in depth: no script, network fetch, or external resource can execute or load from inside a certificate, even if markup ever slipped through.
  - **Credential hardening:** the plaintext password buffer passed to Windows Credential Manager is zeroed before its memory is released, instead of lingering in freed memory.

### 1.0.0-rc9 (2026-07-01)

- **History filter, clear, and import/export.**
  - **Filter by age.** History page gets a *Show* dropdown (All / 7 / 30 / 90 days), scoping the grid and *Export history…*. CLI: `history --since 30d` and `--signed-off true|false`.
  - **Clear unsigned runs.** *Clear unsigned…* (History page) and `FileDrift-CLI history prune [--older-than 90d] [--yes]` permanently delete unsigned run history. **Signed-off runs are never deletable this way** — a hard rule, not a default, since removing one would also orphan any certificate that references it. The GUI shows the matching count before you confirm; the CLI defaults to a dry run and requires `--yes` to actually delete.
  - **Export/import history.** *Export history…* / `history export --out file.json` writes every field (including sign-off) of the matching runs to JSON. *Import history…* / `history import --in file.json [--overwrite]` reads it back: by default a run that already exists locally is left alone; with `--overwrite`, existing runs are updated **except a locally signed-off run, which an import can never overwrite** — the same protection pruning has. This is a portability convenience, not a security boundary, since the underlying database isn't itself tamper-proof.

### 1.0.0-rc8 (2026-06-29)

- **Compliance tab.** A dedicated tab for certificate verification, separate from History. *Verify certificate…* checks one file; *Verify folder…* recursively re-checks every `.html` certificate under a folder and lists them altered-first (intact / altered / not-a-certificate, plus whether each still matches this machine's history database) — for auditing an archive of filed certificates at once. Per-run sign-off and certificate export remain on the Verify and History pages.

### 1.0.0-rc7 (2026-06-29)

- **Compliance actions on the main window.** *Sign off* and *Export certificate* now sit on the right of the Verify page's live-refresh bar and act on the run that just completed, so you don't have to switch to History for the common case. They enable once a verify finishes and disable during a run or after a reconcile (which invalidates the result until you re-verify).
- **Verify a certificate from the UI.** The History page gains a *Verify certificate…* button — the GUI equivalent of `certificate --verify` — that opens a certificate file, re-checks its whole-document fingerprint, and cross-checks it against the local history database.
- **Fixed: the "NOT SIGNED OFF" watermark didn't render.** The repeated phrase was joined with non-breaking spaces, so it was a single unbreakable line that never tiled and fell outside the clipped area. It now tiles diagonally across the certificate body, on screen and in print.
- Internal: shared `ComplianceActions` (sign off / export / verify) so the Verify and History pages behave identically.

### 1.0.0-rc6 (2026-06-29)

- **Certificate hardening, from rc5 testing:**
  - **Whole-document integrity.** The SHA-256 fingerprint now covers the entire certificate, not just the embedded facts block, so editing a *visible* value (e.g. changing a displayed count) is detected by `certificate --verify`, not only edits to the machine-readable data. Any byte change flips the result to altered.
  - **Watermark over the content.** The "NOT SIGNED OFF" watermark is now tiled diagonally *across the certificate body* instead of sitting in the page margin, so it can't be cropped away and stamps the actual data.
  - **Single-page print.** Tightened the layout so a certificate fits on one Letter or A4 page for filing a hard copy (12 mm print margins, flattened sheet styling, no forced paper size).

### 1.0.0-rc5 (2026-06-29)

- **Certificate of verification (HTML).** Export a self-contained certificate for any completed run — from the History page (*Export certificate*) or `FileDrift-CLI certificate --id <run-id>`. It states the verdict (MATCH / DIFFERENCES FOUND / INCOMPLETE), the run options and counts, and the sign-off block, and is styled to print to PDF.
  - **Tamper-evident.** Each certificate embeds a SHA-256 fingerprint over a canonical block of the run's facts. `FileDrift-CLI certificate --verify <file>` recomputes it (intact vs altered) and, when the run is still in the local history DB, confirms the certificate matches the system of record. It's an integrity check, not a cryptographic signature.
  - **Unsigned runs are watermarked.** A run with no sign-off renders a diagonal "NOT SIGNED OFF" watermark, so an unattested certificate can't pass as approved. Inaccessible (skipped) paths force an INCOMPLETE verdict rather than a clean one.
  - Authenticode signing of certificates and native PDF export are backlogged for post-1.0.

### 1.0.0-rc4 (2026-06-29)

- **Readable CLI tables for long paths.** The interactive table view now caps each column width and middle-ellipsises over-long values (e.g. UNC paths), so a run's ID and the distinguishing tail of its source/destination stay lined up and scannable instead of the table blowing out to hundreds of columns. JSON output (piped/redirected, or `--json`) is unchanged and still carries full values.

### 1.0.0-rc3 (2026-06-29)

- **Sign-off workflow is now actually wired up.** The data model had sign-off fields and the report could display them, but nothing could *set* them – there was no way to sign off a run. There now is, in both surfaces:
  - **History page** gains a *Sign off* button (and *Signed off* / *By* columns). Selecting a run and signing off records the time, an accountable party, and an optional note in the app's themed dialog.
  - **CLI** gains `signoff --id <run-id> [--by …] [--note …] [--force]`, for scripted or batch sign-off.
  - **Identity is captured, not just typed.** The accountable party defaults to the Windows account performing the sign-off and can be overridden (e.g. signing on behalf of a named approver). The operating account is always recorded separately, so an override never erases who actually operated the tool; when the two differ the run is flagged as a delegated sign-off. The history database migrates automatically (v2→v3, two new columns); existing runs are unaffected.

### 1.0.0-rc2 (2026-06-29)

- **Fixed: live-refresh interval reset to 0.5 s on every launch.** The Verify page's throttle slider has a 0.5 s minimum; when the page was built, that minimum coerced the slider's initial value and fired a change event *before* the saved setting was applied, silently overwriting the stored interval with the floor. The slider now ignores that construction-time event, so your chosen interval persists across restarts.
- **Clear all credentials now uses the app's themed dialog and lists what it will remove.** The confirmation matches the rest of the app's Fluent styling (instead of the OS message box) and itemizes each credential about to be deleted by label and username, so you can see exactly which accounts are affected before confirming.

### 1.0.0-rc1 (2026-06-29)

- **Clear all credentials.** The Credentials page gains a *Clear all* button that removes every entry FileDrift has stored — per-share and default — in one step, after a confirmation prompt. It is scoped to FileDrift's own targets in Windows Credential Manager and touches nothing else. This rounds out the credential management on the page, which already supported saving, per-share delete, and clearing the default.
- First release candidate for 1.0.

### 0.9.4 (2026-06-29)

- **Estimated time remaining on reconcile.** Next to the transfer rate, the readout now shows an approximate `~ N min left` while copying. It is derived from the *same* ~10-second smoothed rate (not an instantaneous spike), only appears once that rate has settled and bytes remain, blanks out when nothing is moving or once you've chosen to stop, and rounds up so it never under-promises. It is deliberately marked approximate (the `~` and the tooltip) and will drift if the transfer rate does.

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

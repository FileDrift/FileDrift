# FileDrift

File comparison and verification tool for Windows. Compares source and destination directory trees — checking names, sizes, timestamps, hashes, and ACLs — with direct MFT enumeration on local NTFS volumes and parallel SMB enumeration for remote shares.

![FileDrift wordmark](FileDrift-wordmark.png)

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
├── src/FileDrift.Core/     — engine: enumeration, comparison, hashing, ACL checks, run history
└── src/FileDrift.App/      — WPF UI + CLI entry point (single exe)
```

**FileDrift.Core** is a pure C# class library with no UI dependency. Both the GUI and CLI call it directly. Key interfaces:

| Interface | Purpose |
|---|---|
| `IFileEnumerator` | Enumerate a directory tree (MFT or SMB implementation) |
| `IRunRepository` | Persist and query run history (SQLite implementation) |
| `ICredentialStore` | Store and retrieve credentials (Windows Credential Manager implementation) |

**FileDrift.App** is a single `FileDrift.exe`. When launched with arguments it runs headless via `AttachConsole`; when launched without arguments it opens the WPF window.

## CLI usage

```
FileDrift preflight --src \\server\share --dst \\server2\share2
FileDrift verify    --src \\server\share --dst \\server2\share2 [--depth full] [--acl] [--threads 8]
FileDrift report    --id <run-id>
FileDrift history   [--last 10] [--src \\server\share]
```

All CLI output is JSON. Pipe to `jq` or PowerShell's `ConvertFrom-Json` for scripting.

## Enumeration strategy

FileDrift automatically selects the enumeration method based on the target path:

- **Local NTFS volume** (`C:\`, `D:\`, etc.) — USN Journal via `FSCTL_ENUM_USN_DATA`. Enumerates millions of files in seconds without touching file data.
- **UNC or mapped drive** (`\\server\share`) — parallel SMB enumeration using `Parallel.ForEach` over directory entries.

Running FileDrift on the file server itself unlocks MFT enumeration for large local jobs. Remote share mode still benefits significantly from parallelism versus sequential `Get-ChildItem`.

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

[MIT](LICENSE)

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

## License

[MIT](LICENSE)

// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using FileDrift.Core.Engine.Native;
using FileDrift.Core.Interfaces;
using FileDrift.Core.Models;
using Microsoft.Win32.SafeHandles;

namespace FileDrift.Core.Engine;

/// <summary>Enumerates local NTFS volumes via the USN Journal (FSCTL_ENUM_USN_DATA).
/// Requires administrator privileges or SeManageVolumePrivilege.
/// Falls back to <see cref="UnauthorizedAccessException"/> when privileges are insufficient.</summary>
public sealed class MftFileEnumerator : IFileEnumerator
{
    public EnumerationSource Source => EnumerationSource.Mft;

    // MFT access-denial is all-or-nothing (the volume handle fails → SmartFileEnumerator falls back to
    // SMB, which tracks inaccessible paths); per-file stat misses here are deletion races, not denials.
    public IReadOnlyCollection<string> InaccessiblePaths => Array.Empty<string>();

    public async IAsyncEnumerable<FileRecord> EnumerateAsync(
        string rootPath,
        VerifyOptions options,
        IProgress<EnumerationProgress>? progress = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<FileRecord>(new BoundedChannelOptions(2000)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var producerTask = Task.Run(async () =>
        {
            try   { await ScanAsync(rootPath, channel.Writer, progress, cancellationToken); }
            finally { channel.Writer.Complete(); }
        }, cancellationToken);

        await foreach (var record in channel.Reader.ReadAllAsync(cancellationToken))
            yield return record;

        await producerTask;
    }

    // ── Phase 1: build FRN map, Phase 2: reconstruct paths and emit records ──

    private static async Task ScanAsync(
        string rootPath,
        ChannelWriter<FileRecord> writer,
        IProgress<EnumerationProgress>? progress,
        CancellationToken ct)
    {
        var driveRoot   = Path.GetPathRoot(rootPath)
            ?? throw new ArgumentException($"Cannot determine drive root: {rootPath}");
        var volumePath  = $@"\\.\{driveRoot.TrimEnd(Path.DirectorySeparatorChar)}";

        using var volumeHandle = NtfsMethods.CreateFileW(
            volumePath,
            NtfsMethods.GenericRead,
            NtfsMethods.FileShareRead | NtfsMethods.FileShareWrite,
            IntPtr.Zero, NtfsMethods.OpenExisting, NtfsMethods.FileFlagBackupSemantics, IntPtr.Zero);

        if (volumeHandle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            if (err == NtfsMethods.ErrorAccessDenied)
                throw new UnauthorizedAccessException(
                    $"MFT enumeration requires administrator privileges (volume: {volumePath}).");
            throw new IOException($"Cannot open volume {volumePath} (Win32 error {err}).");
        }

        ulong rootFrn = GetRootFrn(driveRoot);

        // Phase 1 — read all MFT records into a FRN → entry map (synchronous P/Invoke)
        var frnMap = await Task.Run(() => BuildFrnMap(volumeHandle, progress, ct), ct);

        ct.ThrowIfCancellationRequested();

        // Phase 2 — reconstruct paths and emit FileRecord objects. Directory paths are memoized
        // (FRN → full path), so each file is one dictionary lookup + one concat instead of walking its
        // whole ancestor chain — the walk was O(depth) lookups plus a Stack/HashSet/array allocation
        // per file, which dominated phase 2 on million-file volumes.
        var normalizedRoot = rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var dirPaths = new Dictionary<ulong, string?> { [rootFrn] = driveRoot };
        long fileCount = 0, byteCount = 0;

        foreach (var (frn, entry) in frnMap)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.IsDirectory) continue;

            var parentPath = DirectoryPath(entry.ParentFrn, frnMap, dirPaths);
            if (parentPath is null) continue; // orphaned or cyclic ancestry — corrupt/stale MFT data

            var fullPath = Join(parentPath, entry.Name);

            if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)) continue;

            if (!NtfsMethods.GetFileAttributesExW(fullPath, 0, out var attrs)) continue; // deleted/inaccessible

            long sizeBytes = ((long)attrs.FileSizeHigh << 32) | attrs.FileSizeLow;

            await writer.WriteAsync(new FileRecord
            {
                // The StartsWith guard above just proved the prefix, so the relative path is a substring —
                // Path.GetRelativePath would re-normalize both full paths on every call.
                RelativePath     = fullPath[normalizedRoot.Length..],
                FullPath         = fullPath,
                SizeBytes        = sizeBytes,
                LastWriteTimeUtc = entry.LastWriteTimeUtc,
                CreatedTimeUtc   = DateTime.FromFileTimeUtc(attrs.CreationTime),
                IsDirectory      = false,
                Source           = EnumerationSource.Mft,
            }, ct);

            fileCount++;
            byteCount += sizeBytes;

            if (fileCount % 1000 == 0)
                progress?.Report(new EnumerationProgress { FilesFound = fileCount, BytesFound = byteCount });
        }

        progress?.Report(new EnumerationProgress { FilesFound = fileCount, BytesFound = byteCount });
    }

    private static Dictionary<ulong, FrnEntry> BuildFrnMap(
        SafeFileHandle volumeHandle,
        IProgress<EnumerationProgress>? progress,
        CancellationToken ct)
    {
        var map    = new Dictionary<ulong, FrnEntry>(capacity: 65536);
        var buffer = new byte[65536];
        var input  = new MftEnumDataV0 { StartFileReferenceNumber = 0, LowUsn = 0, HighUsn = long.MaxValue };
        int inputSize = Marshal.SizeOf<MftEnumDataV0>();
        long scanned = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (!NtfsMethods.DeviceIoControl(
                    volumeHandle, NtfsMethods.FsctlEnumUsnData,
                    ref input, inputSize,
                    buffer, buffer.Length,
                    out int bytesReturned, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                if (err == NtfsMethods.ErrorHandleEof) break;
                throw new IOException($"FSCTL_ENUM_USN_DATA failed (Win32 error {err}).");
            }

            // First 8 bytes of output: next StartFileReferenceNumber
            input.StartFileReferenceNumber = BitConverter.ToUInt64(buffer, 0);

            int offset = 8;
            int recordHeaderSize = Marshal.SizeOf<UsnRecordV2>();

            while (offset + recordHeaderSize <= bytesReturned)
            {
                var record = MemoryMarshal.Read<UsnRecordV2>(buffer.AsSpan(offset));
                if (record.RecordLength == 0) break;

                bool isDir = (record.FileAttributes & NtfsFileAttributes.Directory) != 0;
                // Skip reparse points (junctions, symlinks) to avoid infinite loops during reconstruction
                bool isReparse = (record.FileAttributes & NtfsFileAttributes.ReparsePoint) != 0;

                if (!isReparse)
                {
                    string name = Encoding.Unicode.GetString(buffer, offset + record.FileNameOffset, record.FileNameLength);
                    map[record.FileReferenceNumber] = new FrnEntry(
                        record.ParentFileReferenceNumber,
                        name,
                        isDir,
                        DateTime.FromFileTimeUtc(record.TimeStamp));
                }

                scanned++;
                if (scanned % 20000 == 0)
                    progress?.Report(new EnumerationProgress { FilesFound = scanned, BytesFound = 0 });

                offset += (int)record.RecordLength;
            }
        }

        return map;
    }

    private static ulong GetRootFrn(string driveRoot)
    {
        using var handle = NtfsMethods.CreateFileW(
            driveRoot,
            NtfsMethods.GenericRead,
            NtfsMethods.FileShareRead | NtfsMethods.FileShareWrite,
            IntPtr.Zero, NtfsMethods.OpenExisting, NtfsMethods.FileFlagBackupSemantics, IntPtr.Zero);

        if (handle.IsInvalid)
            throw new IOException($"Cannot open root directory {driveRoot} (Win32 error {Marshal.GetLastWin32Error()}).");

        if (!NtfsMethods.GetFileInformationByHandle(handle, out var info))
            throw new IOException($"GetFileInformationByHandle failed (Win32 error {Marshal.GetLastWin32Error()}).");

        return ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
    }

    /// <summary>Resolves a directory FRN to its full path, memoizing every level it touches in
    /// <paramref name="cache"/> so each directory's ancestry is walked at most once per scan.
    /// Returns null (also cached) for orphaned or cyclic chains — corrupt/stale MFT data.</summary>
    private static string? DirectoryPath(
        ulong frn,
        Dictionary<ulong, FrnEntry> frnMap,
        Dictionary<ulong, string?> cache)
    {
        if (cache.TryGetValue(frn, out var known)) return known;

        // Walk up until we hit a cached ancestor (the root is pre-seeded), an orphan, or a cycle.
        var chain = new List<ulong>();
        var onChain = new HashSet<ulong>();
        string? basePath;
        var current = frn;
        while (true)
        {
            if (cache.TryGetValue(current, out var hit)) { basePath = hit; break; }
            if (!onChain.Add(current) ||                                      // cycle — corrupt MFT
                !frnMap.TryGetValue(current, out var entry)) { basePath = null; break; } // orphaned
            chain.Add(current);
            current = entry.ParentFrn;
        }

        // Unwind, building and caching each level (a failure is cached too, so it's never re-walked).
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            var level = chain[i];
            basePath = basePath is null ? null : Join(basePath, frnMap[level].Name);
            cache[level] = basePath;
        }

        return basePath;
    }

    private static string Join(string parent, string name) =>
        parent.EndsWith(Path.DirectorySeparatorChar) ? parent + name : parent + Path.DirectorySeparatorChar + name;

    private readonly record struct FrnEntry(ulong ParentFrn, string Name, bool IsDirectory, DateTime LastWriteTimeUtc);
}

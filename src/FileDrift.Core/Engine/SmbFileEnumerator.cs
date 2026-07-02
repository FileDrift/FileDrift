// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FileDrift.Core.Interfaces;
using FileDrift.Core.Models;

namespace FileDrift.Core.Engine;

/// <summary>Enumerates a directory tree over SMB (or any path accessible via the filesystem API)
/// using parallel directory scans bounded by <see cref="VerifyOptions.Threads"/>.</summary>
public sealed class SmbFileEnumerator : IFileEnumerator
{
    public EnumerationSource Source => EnumerationSource.Smb;

    private ConcurrentBag<string> _inaccessible = new();
    public IReadOnlyCollection<string> InaccessiblePaths => _inaccessible;

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

        var inaccessible = _inaccessible = new(); // fresh per enumeration
        var normalizedRoot = Path.GetFullPath(rootPath); // children's FullName strings build on this exact text
        var producerTask = Task.Run(async () =>
        {
            try   { await ScanTreeAsync(normalizedRoot, options, channel.Writer, progress, inaccessible, cancellationToken); }
            finally { channel.Writer.Complete(); }
        }, cancellationToken);

        await foreach (var record in channel.Reader.ReadAllAsync(cancellationToken))
            yield return record;

        await producerTask;
    }

    private static async Task ScanTreeAsync(
        string rootPath,
        VerifyOptions options,
        ChannelWriter<FileRecord> writer,
        IProgress<EnumerationProgress>? progress,
        ConcurrentBag<string> inaccessible,
        CancellationToken ct)
    {
        var sem         = new SemaphoreSlim(options.Threads, options.Threads);
        var exceptions  = new ConcurrentBag<Exception>();
        var allDone     = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingLock = new object();
        int pending     = 1;
        long filesFound = 0, bytesFound = 0;

        // Fast relative paths: every enumerated FullName is the root text plus a suffix, so a substring
        // replaces Path.GetRelativePath's per-call full normalization (it re-runs GetFullPath on both
        // arguments — real CPU at millions of files). Fallback kept for safety (e.g. the root itself → ".").
        var rootPrefix = rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string RelativeTo(string fullPath) =>
            fullPath.Length > rootPrefix.Length && fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                ? fullPath[rootPrefix.Length..]
                : Path.GetRelativePath(rootPath, fullPath);

        void ScanDir(string dir)
        {
            _ = Task.Run(async () =>
            {
                List<string>? subdirList = null;
                await sem.WaitAsync(ct);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        // Folders are where explicit permissions usually live — emit them when comparing ACLs.
                        if (options.IncludeAcl)
                        {
                            var di = new DirectoryInfo(dir);
                            await writer.WriteAsync(new FileRecord
                            {
                                RelativePath      = RelativeTo(dir),
                                FullPath          = dir,
                                SizeBytes         = 0,
                                LastWriteTimeUtc  = di.LastWriteTimeUtc,
                                CreatedTimeUtc    = di.CreationTimeUtc,
                                IsDirectory       = true,
                                Source            = EnumerationSource.Smb,
                            }, ct);
                        }

                        // ONE FindFirstFileEx pass per directory: subdirectories and files together, with
                        // size/timestamps/attributes populated from the same find data. The previous shape
                        // (GetDirectories + GetAttributes per subdir + EnumerateFiles + new FileInfo per
                        // file) cost 2–3 extra SMB round-trips per entry.
                        subdirList = [];
                        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                        {
                            if (entry is DirectoryInfo sub)
                            {
                                // Don't recurse into reparse points (junctions, symlinks, mount points) —
                                // avoids infinite loops and double-counting. MFT already skips them.
                                if ((sub.Attributes & FileAttributes.ReparsePoint) == 0)
                                    subdirList.Add(sub.FullName);
                                continue;
                            }

                            var info = (FileInfo)entry;
                            await writer.WriteAsync(new FileRecord
                            {
                                RelativePath      = RelativeTo(info.FullName),
                                FullPath          = info.FullName,
                                SizeBytes         = info.Length,
                                LastWriteTimeUtc  = info.LastWriteTimeUtc,
                                CreatedTimeUtc    = info.CreationTimeUtc,
                                IsDirectory       = false,
                                Source            = EnumerationSource.Smb,
                            }, ct);

                            long fc = Interlocked.Increment(ref filesFound);
                            Interlocked.Add(ref bytesFound, info.Length);

                            if (fc % 500 == 0)
                                progress?.Report(new EnumerationProgress
                                {
                                    FilesFound       = fc,
                                    BytesFound       = Volatile.Read(ref bytesFound),
                                    CurrentDirectory = dir,
                                });
                        }
                    }
                    catch (UnauthorizedAccessException) { inaccessible.Add(dir); }
                    catch (IOException) { inaccessible.Add(dir); }
                }
                catch (OperationCanceledException ex) { exceptions.Add(ex); subdirList = null; }
                catch (Exception ex)                  { exceptions.Add(ex); subdirList = null; }
                finally { sem.Release(); }

                string[] subdirs = subdirList is null ? [] : [.. subdirList];

                // Atomically account for new subdirs and mark this dir done.
                // pendingCount can only reach 0 here when subdirs == [] (proven in design).
                lock (pendingLock)
                {
                    pending += subdirs.Length - 1;
                    if (pending == 0) allDone.TrySetResult();
                }

                foreach (var sub in subdirs)
                    ScanDir(sub);
            }, ct);
        }

        ScanDir(rootPath);
        await allDone.Task.WaitAsync(ct);

        progress?.Report(new EnumerationProgress
        {
            FilesFound = Volatile.Read(ref filesFound),
            BytesFound = Volatile.Read(ref bytesFound),
        });

        var fatal = exceptions.FirstOrDefault(e => e is not OperationCanceledException);
        if (fatal is not null) throw fatal;
        ct.ThrowIfCancellationRequested();
    }

}

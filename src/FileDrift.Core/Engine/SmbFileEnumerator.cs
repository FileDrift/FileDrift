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
        var producerTask = Task.Run(async () =>
        {
            try   { await ScanTreeAsync(rootPath, options, channel.Writer, progress, inaccessible, cancellationToken); }
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

        void ScanDir(string dir)
        {
            _ = Task.Run(async () =>
            {
                string[] subdirs = [];
                await sem.WaitAsync(ct);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        subdirs = Directory.GetDirectories(dir);

                        // Don't recurse into reparse points (junctions, symlinks, mount points) — avoids
                        // infinite loops and double-counting. MFT enumeration already skips them.
                        if (subdirs.Length > 0)
                            subdirs = Array.FindAll(subdirs, sub => !IsReparsePoint(sub));

                        // Folders are where explicit permissions usually live — emit them when comparing ACLs.
                        if (options.IncludeAcl)
                        {
                            var di = new DirectoryInfo(dir);
                            await writer.WriteAsync(new FileRecord
                            {
                                RelativePath      = Path.GetRelativePath(rootPath, dir),
                                FullPath          = dir,
                                SizeBytes         = 0,
                                LastWriteTimeUtc  = di.LastWriteTimeUtc,
                                CreatedTimeUtc    = di.CreationTimeUtc,
                                IsDirectory       = true,
                                Source            = EnumerationSource.Smb,
                            }, ct);
                        }

                        foreach (var file in Directory.EnumerateFiles(dir))
                        {
                            try
                            {
                                var info = new FileInfo(file);
                                await writer.WriteAsync(new FileRecord
                                {
                                    RelativePath      = Path.GetRelativePath(rootPath, file),
                                    FullPath          = file,
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
                            catch (UnauthorizedAccessException) { inaccessible.Add(file); }
                            catch (IOException) { inaccessible.Add(file); }
                        }
                    }
                    catch (UnauthorizedAccessException) { inaccessible.Add(dir); }
                    catch (IOException) { inaccessible.Add(dir); }
                }
                catch (OperationCanceledException ex) { exceptions.Add(ex); subdirs = []; }
                catch (Exception ex)                  { exceptions.Add(ex); subdirs = []; }
                finally { sem.Release(); }

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

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return false; } // unreadable attributes — let the normal scan handle/skip it
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
using FileDrift.Core.Interfaces;

namespace FileDrift.Core.Engine;

/// <summary>
/// Writes to a local or SMB directory tree — the destination FileDrift has always written to.
/// </summary>
/// <remarks>
/// This is a faithful extraction of the copy path that previously lived inside
/// <see cref="ReconcileEngine"/>, not a reimplementation: same read-only clearing, same double-buffered
/// <see cref="StreamPump"/>, same timestamp-then-attributes ordering. The behaviour is relied on by
/// real migrations, so the refactor deliberately changed nothing about it.
/// <para>Source paths arrive absolute (the reconcile plan already holds them) while the destination is
/// addressed relatively, so the same plan can be pointed at a cloud target without rewriting it.</para>
/// </remarks>
public sealed class LocalFileTarget : IFileTarget
{
    private const int BufferSize = 1 << 20; // 1 MiB, matching the previous in-engine copy

    private readonly string _root;

    public LocalFileTarget(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("A destination root is required.", nameof(rootPath));
        _root = rootPath;
    }

    public string Description => _root;

    /// <summary>False: Windows accepts every name FileDrift can enumerate from a Windows source, so
    /// there is nothing to transform and callers need not inspect the returned path.</summary>
    public bool MayTransformNames => false;

    public Task CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Resolve(relativePath));
        return Task.CompletedTask;
    }

    public async Task<FileWriteResult> WriteFileAsync(
        FileWriteRequest request, CancellationToken cancellationToken = default)
    {
        var dest = Resolve(request.RelativePath);

        var dir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // An existing read-only destination would block FileMode.Create; clear it so the overwrite proceeds.
        if (File.Exists(dest) && (File.GetAttributes(dest) & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(dest, File.GetAttributes(dest) & ~FileAttributes.ReadOnly);

        long fileBytes;
        await using (var src = await request.OpenSource(cancellationToken).ConfigureAwait(false))
        await using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None,
                         BufferSize, FileOptions.Asynchronous))
        {
            fileBytes = await StreamPump.PumpAsync(
                src, dst, request.OnBytesWritten, cancellationToken).ConfigureAwait(false);
        }

        // Preserve source timestamps so a follow-up verify reports the pair as matched.
        File.SetLastWriteTimeUtc(dest, request.LastWriteTimeUtc);

        // Creation time is best-effort; the content and last-write time are already correct.
        try { File.SetCreationTimeUtc(dest, request.CreatedTimeUtc); }
        catch { /* metadata only */ }

        return new FileWriteResult(fileBytes, request.RelativePath);
    }

    public Task<bool> TryDeletePartialAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var path = Resolve(relativePath);
            if (File.Exists(path)) { File.Delete(path); return Task.FromResult(true); }
        }
        catch { /* best-effort cleanup on the cancellation path */ }
        return Task.FromResult(false);
    }

    /// <summary>Copies the source's user-settable attributes to a file already written.
    /// <para>Not part of <see cref="IFileTarget"/>: file attributes are a Windows filesystem concept
    /// with no cloud equivalent, so this stays specific to this target rather than becoming a member
    /// every implementation must refuse. Applied LAST, because a copied read-only flag would block the
    /// timestamp writes above.</para></summary>
    public void TryCopyAttributes(string sourceFullPath, string relativePath)
    {
        try
        {
            const FileAttributes copyable = FileAttributes.ReadOnly | FileAttributes.Hidden
                | FileAttributes.System | FileAttributes.Archive;
            File.SetAttributes(Resolve(relativePath), File.GetAttributes(sourceFullPath) & copyable);
        }
        catch { /* metadata is best-effort */ }
    }

    private string Resolve(string relativePath) =>
        string.IsNullOrEmpty(relativePath) ? _root : Path.Combine(_root, relativePath);

    /// <summary>Opens a source file with the hints the copy loop wants: sequential, asynchronous, and
    /// tolerant of other readers.</summary>
    public static Func<CancellationToken, Task<Stream>> OpenLocalSource(string sourceFullPath) =>
        _ => Task.FromResult<Stream>(new FileStream(
            sourceFullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan));
}

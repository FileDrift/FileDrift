// SPDX-License-Identifier: GPL-3.0-or-later
namespace FileDrift.Core.Interfaces;

/// <summary>One file to write, addressed by its path relative to the target's root.</summary>
/// <param name="RelativePath">Backslash-separated path below the target root, per the
/// <see cref="IFileEnumerator"/> convention. The target translates it to whatever its own addressing
/// needs.</param>
/// <param name="OpenSource">Opens the source content. A factory rather than a <see cref="Stream"/>
/// so the target decides when to open it, and can reopen on a resumed or retried write without the
/// caller having to care.</param>
/// <param name="SizeBytes">Expected length, for progress and for choosing an upload strategy. The
/// actual bytes written are what the target returns.</param>
/// <param name="LastWriteTimeUtc">Source last-write time, to be preserved at the destination.</param>
/// <param name="CreatedTimeUtc">Source creation time, to be preserved where the target supports it.</param>
/// <param name="OnBytesWritten">Called with the running total for THIS file as it is written, for a
/// live rate readout. Invoked often, so it must stay cheap.</param>
public sealed record FileWriteRequest(
    string RelativePath,
    Func<CancellationToken, Task<Stream>> OpenSource,
    long SizeBytes,
    DateTime LastWriteTimeUtc,
    DateTime CreatedTimeUtc,
    Action<long>? OnBytesWritten = null);

/// <summary>The outcome of writing one file.</summary>
/// <param name="BytesWritten">Bytes actually written, which may differ from the planned size if the
/// source changed since the verify.</param>
/// <param name="FinalRelativePath">Where the file actually landed. Differs from the requested path
/// when the target had to transform the name to something it accepts — a SharePoint destination
/// rejects names Windows allows. Callers record the difference rather than assuming it away.</param>
public sealed record FileWriteResult(long BytesWritten, string FinalRelativePath);

/// <summary>
/// A destination FileDrift can write to: a local or SMB directory tree, or a cloud location.
/// </summary>
/// <remarks>
/// <para><b>The target pulls; it does not hand back a stream.</b> The obvious shape — "give me a
/// writable Stream for this path" — cannot express a Graph upload, where content is PUT as byte ranges
/// in multiples of 320 KiB with explicit range headers, and a session may need resuming. So the
/// request carries a way to OPEN THE SOURCE and the target owns the transfer loop. A filesystem target
/// implements that with a stream copy; a cloud target implements it with chunked uploads. Inverting
/// this would force every cloud implementation to fight the abstraction.</para>
/// <para>Permissions are deliberately absent. ACLs are a Windows filesystem concept with no cloud
/// equivalent, so they stay in the local reconcile path rather than becoming an interface member every
/// target must answer and most must refuse.</para>
/// </remarks>
public interface IFileTarget
{
    /// <summary>Human-readable description of where this target writes, for logs and reports.</summary>
    string Description { get; }

    /// <summary>True when the target may change a name to satisfy its own rules, so callers know to
    /// check <see cref="FileWriteResult.FinalRelativePath"/> and disclose any renames.</summary>
    bool MayTransformNames { get; }

    /// <summary>Ensures a directory exists at the relative path, creating parents as needed.</summary>
    Task CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default);

    /// <summary>Writes one file, replacing any existing content at that path.</summary>
    Task<FileWriteResult> WriteFileAsync(FileWriteRequest request, CancellationToken cancellationToken = default);

    /// <summary>Best-effort removal of a partially-written file after an aborted write. Returns true if
    /// something was removed. Must not throw — it runs on the cancellation path, where an exception
    /// would mask the original reason for stopping.</summary>
    Task<bool> TryDeletePartialAsync(string relativePath, CancellationToken cancellationToken = default);
}

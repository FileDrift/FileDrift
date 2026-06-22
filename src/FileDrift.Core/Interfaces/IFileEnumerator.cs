using FileDrift.Core.Models;

namespace FileDrift.Core.Interfaces;

/// <summary>
/// Enumerates a directory tree and yields file records.
/// Concrete implementations select MFT or SMB strategy based on the target path.
/// </summary>
public interface IFileEnumerator
{
    /// <summary>Which enumeration strategy this instance uses.</summary>
    EnumerationSource Source { get; }

    /// <summary>
    /// Streams file records from <paramref name="rootPath"/>.
    /// Hash and ACL fields on each record are populated only if requested via <paramref name="options"/>.
    /// </summary>
    IAsyncEnumerable<FileRecord> EnumerateAsync(
        string rootPath,
        VerifyOptions options,
        IProgress<EnumerationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

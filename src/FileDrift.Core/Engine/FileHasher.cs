// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using FileDrift.Core.Models;

namespace FileDrift.Core.Engine;

/// <summary>Computes content hashes by streaming files (constant memory regardless of file size).</summary>
public sealed class FileHasher
{
    private const int BufferSize = 1 << 20; // 1 MiB

    /// <summary>Returns the uppercase hex hash of the file, or null if it cannot be read.</summary>
    public async Task<string?> TryComputeHashAsync(
        string path,
        FileDriftHashAlgorithm algorithm,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // SequentialScan: hashing reads front-to-back once — the hint improves read-ahead and keeps
            // large files from polluting the cache. Static one-shot HashDataAsync avoids allocating a
            // HashAlgorithm instance per file (this runs once or twice per matched pair).
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] hash = algorithm switch
            {
                FileDriftHashAlgorithm.MD5    => await MD5.HashDataAsync(stream, cancellationToken),
                FileDriftHashAlgorithm.SHA1   => await SHA1.HashDataAsync(stream, cancellationToken),
                FileDriftHashAlgorithm.SHA256 => await SHA256.HashDataAsync(stream, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Unsupported hash algorithm."),
            };
            return Convert.ToHexString(hash);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

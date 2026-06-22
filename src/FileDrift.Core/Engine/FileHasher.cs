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
            using var algo = Create(algorithm);
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            var hash = await algo.ComputeHashAsync(stream, cancellationToken);
            return Convert.ToHexString(hash);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static HashAlgorithm Create(FileDriftHashAlgorithm algorithm) => algorithm switch
    {
        FileDriftHashAlgorithm.MD5    => MD5.Create(),
        FileDriftHashAlgorithm.SHA1   => SHA1.Create(),
        FileDriftHashAlgorithm.SHA256 => SHA256.Create(),
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Unsupported hash algorithm."),
    };
}

// SPDX-License-Identifier: GPL-3.0-or-later
namespace FileDrift.Core.Engine;

/// <summary>
/// Microsoft's <c>quickXorHash</c> — the only content hash the Graph API returns for files in
/// OneDrive and SharePoint Online. Implementing it locally is what lets a Full-depth verify compare
/// a cloud file's content against a local one; MD5/SHA-256 are not available cloud-side.
/// </summary>
/// <remarks>
/// This is a port of the algorithm published in Microsoft's OneDrive API documentation. It is NOT a
/// cryptographic hash — it is a 160-bit XOR-based checksum designed to be cheap, and it makes no
/// collision-resistance guarantees against deliberately crafted input. That is fine for drift
/// detection (its purpose here) and is exactly what the service itself uses, but it must never be
/// treated as a security primitive.
/// <para>Endianness: the length is folded in via <see cref="BitConverter"/>, which is
/// architecture-dependent. Correct on little-endian (x86/x64/ARM as shipped); would need explicit
/// little-endian writes to be portable to a big-endian platform.</para>
/// </remarks>
public sealed class QuickXorHash
{
    private const int WidthInBits = 160;
    private const int Shift = 11;
    private const int BitsInLastCell = 32;

    /// <summary>Length of the resulting hash in bytes (160 bits).</summary>
    public const int HashSizeInBytes = WidthInBits / 8;

    /// <summary>Chunk size for stream hashing. Deliberately a multiple of <see cref="WidthInBits"/>
    /// so every block except the final one lands on a shift-cycle boundary, which keeps the
    /// incremental shift bookkeeping identical to hashing the whole buffer at once.</summary>
    private const int StreamBufferSize = WidthInBits * 6_400; // 1,024,000 bytes

    private readonly ulong[] _data = new ulong[(WidthInBits / 64) + 1];
    private long _lengthSoFar;
    private int _shiftSoFar;

    /// <summary>Folds another span of data into the hash.</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        unchecked
        {
            int vectorArrayIndex = _shiftSoFar / 64;
            int vectorOffset = _shiftSoFar % 64;
            int iterations = Math.Min(data.Length, WidthInBits);

            for (int i = 0; i < iterations; i++)
            {
                bool isLastCell = vectorArrayIndex == _data.Length - 1;
                int bitsInVectorCell = isLastCell ? BitsInLastCell : 64;

                if (vectorOffset <= bitsInVectorCell - 8)
                {
                    // The byte fits entirely within the current cell.
                    for (int j = i; j < data.Length; j += WidthInBits)
                        _data[vectorArrayIndex] ^= (ulong)data[j] << vectorOffset;
                }
                else
                {
                    // The byte straddles two cells (wrapping to cell 0 at the end).
                    int index1 = vectorArrayIndex;
                    int index2 = isLastCell ? 0 : vectorArrayIndex + 1;
                    int low = bitsInVectorCell - vectorOffset;

                    byte xoredByte = 0;
                    for (int j = i; j < data.Length; j += WidthInBits)
                        xoredByte ^= data[j];

                    _data[index1] ^= (ulong)xoredByte << vectorOffset;
                    _data[index2] ^= (ulong)xoredByte >> low;
                }

                vectorOffset += Shift;
                while (vectorOffset >= bitsInVectorCell)
                {
                    vectorArrayIndex = isLastCell ? 0 : vectorArrayIndex + 1;
                    vectorOffset -= bitsInVectorCell;
                }
            }

            _shiftSoFar = (_shiftSoFar + Shift * (data.Length % WidthInBits)) % WidthInBits;
            _lengthSoFar += data.Length;
        }
    }

    /// <summary>Produces the final 20-byte hash. The instance should not be reused afterwards.</summary>
    public byte[] GetHash()
    {
        var result = new byte[HashSizeInBytes];

        for (int i = 0; i < _data.Length - 1; i++)
            BitConverter.TryWriteBytes(result.AsSpan(i * 8), _data[i]);

        // The final cell contributes only its low bytes (160 bits doesn't divide evenly into 64).
        int tailOffset = (_data.Length - 1) * 8;
        Span<byte> tail = stackalloc byte[8];
        BitConverter.TryWriteBytes(tail, _data[^1]);
        tail[..(result.Length - tailOffset)].CopyTo(result.AsSpan(tailOffset));

        // Fold the total length into the least-significant bytes, so two files with identical bytes
        // but different lengths (e.g. trailing zeros) don't collide.
        Span<byte> lengthBytes = stackalloc byte[8];
        BitConverter.TryWriteBytes(lengthBytes, _lengthSoFar);
        for (int i = 0; i < lengthBytes.Length; i++)
            result[HashSizeInBytes - lengthBytes.Length + i] ^= lengthBytes[i];

        return result;
    }

    /// <summary>Hashes a whole buffer in one call.</summary>
    public static byte[] Compute(ReadOnlySpan<byte> data)
    {
        var hash = new QuickXorHash();
        hash.Append(data);
        return hash.GetHash();
    }

    /// <summary>Hashes a stream, reading in shift-cycle-aligned chunks (constant memory).</summary>
    public static async Task<byte[]> ComputeAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var hash = new QuickXorHash();
        var buffer = new byte[StreamBufferSize];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            hash.Append(buffer.AsSpan(0, read));
        return hash.GetHash();
    }

    /// <summary>Base64 form — the representation Microsoft Graph reports in
    /// <c>file.hashes.quickXorHash</c>, so this is what a cloud-vs-local comparison keys on.</summary>
    public static string ToBase64(byte[] hash) => Convert.ToBase64String(hash);
}

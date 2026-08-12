// SPDX-License-Identifier: GPL-3.0-or-later
using FileDrift.Core.Engine;
using Xunit;

namespace FileDrift.Core.Tests;

/// <summary>
/// Property/self-consistency tests for the quickXorHash port.
/// <para>IMPORTANT: these prove the implementation is internally consistent (deterministic, order-
/// and length-sensitive, chunk-size independent) — they do NOT prove it matches Microsoft's
/// algorithm, because that can only be established by comparing against a real
/// <c>file.hashes.quickXorHash</c> returned by Graph for a known file. That cross-validation is a
/// required step before Full-depth cloud verification can be trusted. See the backlog.</para>
/// </summary>
public class QuickXorHashTests
{
    private static byte[] Bytes(int length, int seed)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    [Fact]
    public void Hash_is_160_bits()
    {
        Assert.Equal(20, QuickXorHash.HashSizeInBytes);
        Assert.Equal(20, QuickXorHash.Compute(Bytes(1000, 1)).Length);
        Assert.Equal(20, QuickXorHash.Compute([]).Length); // empty input still yields a full-width hash
    }

    [Fact]
    public void Is_deterministic()
    {
        var data = Bytes(5000, 2);
        Assert.Equal(QuickXorHash.Compute(data), QuickXorHash.Compute(data));
    }

    [Fact]
    public void Different_content_of_the_same_length_hashes_differently()
    {
        var a = Bytes(4096, 3);
        var b = Bytes(4096, 4);
        Assert.NotEqual(QuickXorHash.Compute(a), QuickXorHash.Compute(b));
    }

    [Fact]
    public void Byte_order_matters()
    {
        // A plain XOR checksum would collide here; the per-byte shifting is what prevents it.
        byte[] forward = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] reversed = [8, 7, 6, 5, 4, 3, 2, 1];
        Assert.NotEqual(QuickXorHash.Compute(forward), QuickXorHash.Compute(reversed));
    }

    [Fact]
    public void Length_is_folded_in_so_trailing_zeros_change_the_hash()
    {
        // Same leading bytes, different length: without the length fold these could collide, since
        // XOR-ing in a zero byte is a no-op.
        byte[] shorter = [9, 9, 9];
        byte[] longer = [9, 9, 9, 0, 0, 0];
        Assert.NotEqual(QuickXorHash.Compute(shorter), QuickXorHash.Compute(longer));
    }

    [Theory]
    // Chunk sizes deliberately spanning the 160-byte shift cycle: an exact multiple, an odd size
    // that never aligns, one byte at a time, and a size larger than the cycle.
    [InlineData(160)]
    [InlineData(37)]
    [InlineData(1)]
    [InlineData(1024)]
    public void Incremental_hashing_matches_one_shot(int chunkSize)
    {
        var data = Bytes(4000, 5);

        var incremental = new QuickXorHash();
        for (int offset = 0; offset < data.Length; offset += chunkSize)
            incremental.Append(data.AsSpan(offset, Math.Min(chunkSize, data.Length - offset)));

        Assert.Equal(QuickXorHash.Compute(data), incremental.GetHash());
    }

    [Fact]
    public async Task Stream_hashing_matches_one_shot_across_the_buffer_boundary()
    {
        // Larger than the 1,024,000-byte internal read buffer, so this exercises multi-block streaming.
        var data = Bytes(2_500_000, 6);
        using var stream = new MemoryStream(data);

        var streamed = await QuickXorHash.ComputeAsync(stream);

        Assert.Equal(QuickXorHash.Compute(data), streamed);
    }

    [Fact]
    public void Base64_form_is_the_28_character_graph_representation()
    {
        // Graph reports quickXorHash as base64; 20 bytes -> 28 base64 chars (with padding).
        var b64 = QuickXorHash.ToBase64(QuickXorHash.Compute(Bytes(100, 7)));
        Assert.Equal(28, b64.Length);
        Assert.Equal(20, Convert.FromBase64String(b64).Length);
    }
}

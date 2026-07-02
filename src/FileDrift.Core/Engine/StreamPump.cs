// SPDX-License-Identifier: GPL-3.0-or-later
namespace FileDrift.Core.Engine;

/// <summary>Double-buffered stream-to-stream copy: while one chunk is being written, the next is
/// already being read, so source latency and destination latency overlap instead of adding — on a
/// network→disk copy that approaches the throughput of the slower side alone. Deliberately
/// stream-shaped (not path-shaped) so a future non-filesystem write target (object-storage upload)
/// can reuse it unchanged.</summary>
public static class StreamPump
{
    public const int DefaultBufferSize = 1 << 20; // 1 MiB

    /// <summary>Copies <paramref name="source"/> to <paramref name="dest"/> until end of stream.
    /// Invokes <paramref name="onBytesWritten"/> with the cumulative WRITTEN byte count after each
    /// chunk's write completes (drives byte-level progress). Returns the total bytes copied.
    /// Cancellation is honored on every read and write.</summary>
    public static Task<long> PumpAsync(
        Stream source, Stream dest, Action<long>? onBytesWritten, CancellationToken cancellationToken) =>
        PumpAsync(source, dest, DefaultBufferSize, onBytesWritten, cancellationToken);

    public static async Task<long> PumpAsync(
        Stream source, Stream dest, int bufferSize, Action<long>? onBytesWritten,
        CancellationToken cancellationToken)
    {
        var current = new byte[bufferSize];
        var next    = new byte[bufferSize];

        long total = 0;
        int read = await source.ReadAsync(current, cancellationToken);
        while (read > 0)
        {
            // Overlap: start writing this chunk, and read the next one while the write is in flight.
            var write = dest.WriteAsync(current.AsMemory(0, read), cancellationToken);
            int nextRead;
            try
            {
                nextRead = await source.ReadAsync(next, cancellationToken);
            }
            catch
            {
                // Don't leave the write dangling — its buffer is about to be reused and the caller will
                // dispose the streams. Surface the read failure, not any secondary write failure.
                try { await write; } catch { /* the read error wins */ }
                throw;
            }
            await write;

            total += read;
            onBytesWritten?.Invoke(total);

            (current, next) = (next, current);
            read = nextRead;
        }

        return total;
    }
}

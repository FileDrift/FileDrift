// SPDX-License-Identifier: GPL-3.0-or-later
using FileDrift.Core.Engine;
using Xunit;

namespace FileDrift.Core.Tests;

public class StreamPumpTests
{
    [Fact]
    public async Task Round_trips_content_across_many_chunks_including_an_odd_tail()
    {
        // Small buffer so a modest payload spans many chunks and exercises the buffer swap; the +3
        // guarantees a partial final chunk.
        var payload = new byte[8 * 16 + 3];
        new Random(42).NextBytes(payload);
        using var source = new MemoryStream(payload);
        using var dest = new MemoryStream();

        long total = await StreamPump.PumpAsync(source, dest, bufferSize: 16, onBytesWritten: null, CancellationToken.None);

        Assert.Equal(payload.Length, total);
        Assert.Equal(payload, dest.ToArray());
    }

    [Fact]
    public async Task Progress_is_cumulative_monotonic_and_ends_at_the_total()
    {
        var payload = new byte[100];
        using var source = new MemoryStream(payload);
        using var dest = new MemoryStream();
        var reports = new List<long>();

        long total = await StreamPump.PumpAsync(source, dest, bufferSize: 32, reports.Add, CancellationToken.None);

        Assert.NotEmpty(reports);
        Assert.Equal(total, reports[^1]);
        for (int i = 1; i < reports.Count; i++)
            Assert.True(reports[i] > reports[i - 1], "progress must be strictly increasing");
    }

    [Fact]
    public async Task Zero_length_source_copies_nothing_and_reports_nothing()
    {
        using var source = new MemoryStream();
        using var dest = new MemoryStream();
        var reports = new List<long>();

        long total = await StreamPump.PumpAsync(source, dest, reports.Add, CancellationToken.None);

        Assert.Equal(0, total);
        Assert.Empty(reports);
        Assert.Equal(0, dest.Length);
    }

    [Fact]
    public async Task Cancellation_mid_pump_throws_promptly()
    {
        var payload = new byte[1024];
        using var source = new MemoryStream(payload);
        using var dest = new MemoryStream();
        using var cts = new CancellationTokenSource();

        // Cancel from the first progress callback — the next read/write must observe it.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            StreamPump.PumpAsync(source, dest, bufferSize: 64, _ => cts.Cancel(), cts.Token));
    }

    [Fact]
    public async Task Read_failure_surfaces_and_is_not_masked_by_the_in_flight_write()
    {
        using var source = new FailAfterFirstReadStream(new byte[256]);
        using var dest = new MemoryStream();

        var ex = await Assert.ThrowsAsync<IOException>(() =>
            StreamPump.PumpAsync(source, dest, bufferSize: 64, onBytesWritten: null, CancellationToken.None));
        Assert.Equal("simulated read failure", ex.Message);
    }

    /// <summary>Serves the first read normally, then throws — simulating a source that dies mid-copy
    /// while a destination write is in flight.</summary>
    private sealed class FailAfterFirstReadStream(byte[] data) : MemoryStream(data)
    {
        private int _reads;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _reads) > 1)
                throw new IOException("simulated read failure");
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}

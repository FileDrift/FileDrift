// SPDX-License-Identifier: GPL-3.0-or-later
using FileDrift.Core.Engine;
using FileDrift.Core.Interfaces;
using FileDrift.Core.Models;
using Xunit;

namespace FileDrift.Core.Tests;

/// <summary>
/// Preflight's write check. Reported permissions can differ from what a write actually does, so the
/// only answer worth having comes from attempting one — and because that writes, it is opt-in, and its
/// absence has to be reported rather than read as success.
/// </summary>
public class WriteProbeTests
{
    private sealed class EmptyEnumerator : IFileEnumerator
    {
        public EnumerationSource Source => EnumerationSource.Smb;
        public IReadOnlyCollection<string> InaccessiblePaths => [];
        public async IAsyncEnumerable<FileRecord> EnumerateAsync(
            string rootPath, VerifyOptions options,
            IProgress<EnumerationProgress>? progress = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    /// <summary>Records what the probe did, and can be told to fail either step.</summary>
    private sealed class SpyTarget(bool writeFails = false, bool deleteFails = false) : IFileTarget
    {
        public List<string> Written { get; } = [];
        public List<string> Deleted { get; } = [];

        public string Description => "spy";
        public bool MayTransformNames => false;

        public Task CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public async Task<FileWriteResult> WriteFileAsync(
            FileWriteRequest request, CancellationToken cancellationToken = default)
        {
            if (writeFails) throw new UnauthorizedAccessException("access denied by the spy");

            // Drain the source the way a real target would, so a broken request factory would surface.
            await using var stream = await request.OpenSource(cancellationToken);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);

            Written.Add(request.RelativePath);
            return new FileWriteResult(buffer.Length, request.RelativePath);
        }

        public Task<bool> TryDeletePartialAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            Deleted.Add(relativePath);
            return Task.FromResult(!deleteFails);
        }
    }

    /// <summary>Runs preflight with a real, existing source directory: a missing source would raise its
    /// own error and mask what these tests are actually about.</summary>
    private static Task<PreFlightResult> Run(SpyTarget? target, bool checkWrite, string dest, string source)
    {
        var engine = new PreflightEngine(new EmptyEnumerator());
        return engine.RunAsync(source, dest, new VerifyOptions(),
            progress: null, cancellationToken: default,
            destinationTarget: target, checkWrite: checkWrite);
    }

    /// <summary>A temp directory pair: an existing source, and a destination to probe.</summary>
    private static (string Source, string Dest) Paths(TempDir t)
    {
        var source = Path.Combine(t.Path, "src");
        var dest = Path.Combine(t.Path, "dst");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(dest);
        return (source, dest);
    }

    [Fact]
    public async Task Not_asking_for_the_check_leaves_it_untested_and_says_so()
    {
        using var t = new TempDir();
        var (source, dest) = Paths(t);
        var result = await Run(null, checkWrite: false, dest: dest, source: source);

        Assert.False(result.WriteProbe.Attempted);
        Assert.Null(result.WriteProbe.Succeeded);
        // Silence would read as "write access is fine". It must be stated.
        Assert.Contains(result.Issues, i => i.Contains("[WARN]") && i.Contains("NOT tested"));
        // A warning, not a blocker: an untested destination is not a known-bad one.
        Assert.True(result.IsReady);
    }

    [Fact]
    public async Task A_successful_probe_writes_then_removes_exactly_one_file()
    {
        using var t = new TempDir();
        var (source, dest) = Paths(t);
        var spy = new SpyTarget();
        var result = await Run(spy, checkWrite: true, dest: dest, source: source);

        Assert.True(result.WriteProbe.Attempted);
        Assert.True(result.WriteProbe.Succeeded);
        Assert.True(result.WriteProbe.CleanedUp);

        var written = Assert.Single(spy.Written);
        Assert.Equal(written, Assert.Single(spy.Deleted));
        // Named so a failed cleanup is identifiable as disposable.
        Assert.StartsWith("filedrift-write-probe-", written);
        Assert.EndsWith(".tmp", written);

        Assert.True(result.IsReady);
        Assert.DoesNotContain(result.Issues, i => i.Contains("NOT tested"));
    }

    [Fact]
    public async Task A_failed_write_blocks_the_run()
    {
        // The whole purpose of a preflight is learning this now rather than partway through a copy.
        using var t = new TempDir();
        var (source, dest) = Paths(t);
        var result = await Run(new SpyTarget(writeFails: true), checkWrite: true, dest: dest, source: source);

        Assert.True(result.WriteProbe.Attempted);
        Assert.False(result.WriteProbe.Succeeded);
        Assert.Contains("access denied by the spy", result.WriteProbe.Error);
        Assert.Contains(result.Issues, i => i.StartsWith("[ERROR]") && i.Contains("Cannot write"));
        Assert.False(result.IsReady);
    }

    [Fact]
    public async Task A_probe_that_cannot_be_cleaned_up_admits_it()
    {
        // A tool that litters quietly is worse than one that says where the litter is.
        using var t = new TempDir();
        var (source, dest) = Paths(t);
        var result = await Run(new SpyTarget(deleteFails: true), checkWrite: true, dest: dest, source: source);

        Assert.True(result.WriteProbe.Succeeded);
        Assert.False(result.WriteProbe.CleanedUp);
        Assert.Contains(result.Issues, i =>
            i.Contains("[WARN]") && i.Contains("could not be removed") && i.Contains(result.WriteProbe.ProbePath!));
        // Still writable, so not a blocker -- just a stray file to delete.
        Assert.True(result.IsReady);
    }

    [Fact]
    public async Task Each_probe_uses_a_distinct_name()
    {
        // Two runs against one destination must not collide.
        using var t = new TempDir();
        var (source, dest) = Paths(t);
        var a = new SpyTarget();
        var b = new SpyTarget();
        await Run(a, checkWrite: true, dest: dest, source: source);
        await Run(b, checkWrite: true, dest: dest, source: source);

        Assert.NotEqual(a.Written[0], b.Written[0]);
    }

    [Fact]
    public async Task Probing_a_real_directory_without_a_target_uses_the_local_filesystem()
    {
        // No target supplied means a local destination, which must still be genuinely tested.
        using var t = new TempDir();
        var (source, dest) = Paths(t);
        var result = await Run(null, checkWrite: true, dest: dest, source: source);

        Assert.True(result.WriteProbe.Succeeded);
        Assert.True(result.WriteProbe.CleanedUp);
        // Nothing left behind.
        Assert.Empty(Directory.GetFiles(dest));
    }
}

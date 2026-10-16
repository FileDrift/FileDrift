// SPDX-License-Identifier: GPL-3.0-or-later
using FileDrift.Core.Engine;
using FileDrift.Core.Interfaces;
using FileDrift.Core.Models;
using Xunit;

namespace FileDrift.Core.Tests;

/// <summary>
/// A rename has to be visible in the run log, not only counted in the certificate.
/// <para>The certificate states how many files were stored under a different name. If the log that
/// accompanies the run shows only source names, the operator has a number they cannot corroborate
/// against anything — which is the weaker half of a disclosure, and the failure mode this project
/// exists to avoid. It also has to survive progress throttling, since routine per-file reports are
/// sampled and a rename that got sampled away would be invisible precisely when it mattered.</para>
/// </summary>
public class RenameLogDisclosureTests
{
    /// <summary>A target that renames whatever it is given, the way a cloud destination does.</summary>
    private sealed class RenamingTarget(string suffix) : IFileTarget
    {
        public string Description => "renaming test target";
        public bool MayTransformNames => true;
        public List<string> Written { get; } = [];

        public Task CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public async Task<FileWriteResult> WriteFileAsync(
            FileWriteRequest request, CancellationToken cancellationToken = default)
        {
            await using var source = await request.OpenSource(cancellationToken);
            using var sink = new MemoryStream();
            await source.CopyToAsync(sink, cancellationToken);

            var final = request.RelativePath + suffix;
            Written.Add(final);
            return new FileWriteResult(sink.Length, final);
        }

        public Task<bool> TryDeletePartialAsync(string relativePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    /// <summary>A target that keeps every name, to prove the disclosure does not fire spuriously.</summary>
    private sealed class FaithfulTarget : IFileTarget
    {
        public string Description => "faithful test target";
        public bool MayTransformNames => true;

        public Task CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public async Task<FileWriteResult> WriteFileAsync(
            FileWriteRequest request, CancellationToken cancellationToken = default)
        {
            await using var source = await request.OpenSource(cancellationToken);
            using var sink = new MemoryStream();
            await source.CopyToAsync(sink, cancellationToken);
            return new FileWriteResult(sink.Length, request.RelativePath);
        }

        public Task<bool> TryDeletePartialAsync(string relativePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private static (ReconcilePlan Plan, string Source, string Dest, TempDir Temp) Plan(params string[] names)
    {
        var temp = new TempDir();
        var src = temp.Sub("src");
        var dst = temp.Sub("dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);

        var comparisons = new List<ComparisonResult>();
        foreach (var name in names)
        {
            var full = Path.Combine(src, name);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "content of " + name);
            comparisons.Add(new ComparisonResult
            {
                RelativePath = name,
                Status = ComparisonStatus.MissingAtDest,
                Source = Rec.File(name, new FileInfo(full).Length, fullPath: full),
            });
        }

        return (ReconcileEngine.BuildPlan(comparisons, dst), src, dst, temp);
    }

    [Fact]
    public async Task A_renamed_file_names_both_paths_in_the_log()
    {
        var (plan, src, dst, temp) = Plan("report.txt");
        using var _ = temp;
        var progress = new SyncProgress<ReconcileProgress>();

        var result = await new ReconcileEngine().ExecuteAsync(
            plan, src, dst, progress: progress, target: new RenamingTarget("_x"));

        Assert.Equal(1, result.Copied);
        Assert.Equal("report.txt_x", result.Renamed["report.txt"]);

        var line = Assert.Single(progress.Items, p => p.Message!.Contains("renamed at destination"));
        Assert.Contains("report.txt", line.Message);
        Assert.Contains("report.txt_x", line.Message);
    }

    [Fact]
    public async Task The_rename_line_is_important_so_throttling_cannot_drop_it()
    {
        var (plan, src, dst, temp) = Plan("report.txt");
        using var _ = temp;
        var progress = new SyncProgress<ReconcileProgress>();

        await new ReconcileEngine().ExecuteAsync(
            plan, src, dst, progress: progress, target: new RenamingTarget("_x"));

        Assert.Contains(progress.Items, p => p.Important && p.Message!.Contains("renamed at destination"));
    }

    [Fact]
    public async Task A_faithful_copy_says_nothing_about_renames()
    {
        // Claiming a rename that did not happen would overstate how much the migration altered, and
        // the count is disclosed in the certificate -- so a false positive here is a false assertion.
        var (plan, src, dst, temp) = Plan("report.txt");
        using var _ = temp;
        var progress = new SyncProgress<ReconcileProgress>();

        var result = await new ReconcileEngine().ExecuteAsync(
            plan, src, dst, progress: progress, target: new FaithfulTarget());

        Assert.Empty(result.Renamed);
        Assert.DoesNotContain(progress.Items, p => p.Message!.Contains("renamed at destination"));
    }

    [Fact]
    public async Task Every_renamed_file_is_disclosed_not_just_the_first()
    {
        var (plan, src, dst, temp) = Plan(@"Reports\a.txt", @"Reports\b.txt", @"Policies\c.txt");
        using var _ = temp;
        var progress = new SyncProgress<ReconcileProgress>();

        var result = await new ReconcileEngine().ExecuteAsync(
            plan, src, dst, progress: progress, target: new RenamingTarget("_x"));

        Assert.Equal(3, result.Renamed.Count);
        Assert.Equal(3, progress.Items.Count(p => p.Message!.Contains("renamed at destination")));
    }
}

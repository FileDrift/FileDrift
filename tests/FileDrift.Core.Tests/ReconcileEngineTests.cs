// SPDX-License-Identifier: GPL-3.0-or-later
using FileDrift.Core.Engine;
using FileDrift.Core.Models;
using Xunit;

namespace FileDrift.Core.Tests;

public class ReconcileEngineTests
{
    private const int MB = 1024 * 1024;

    private static ComparisonResult MissingAt(string rel, string srcFull, long size) => new()
    {
        RelativePath = rel,
        Status = ComparisonStatus.MissingAtDest,
        Source = Rec.File(rel, size, fullPath: srcFull),
    };

    [Fact]
    public void BuildPlan_missing_is_copy_different_is_overwrite()
    {
        var missing = MissingAt("a", @"S:\a", 10);
        var different = new ComparisonResult
        {
            RelativePath = "b", Status = ComparisonStatus.Different, Differences = FileDifference.Size,
            Source = Rec.File("b", 20, fullPath: @"S:\b"), Dest = Rec.File("b", 10, fullPath: @"D:\b"),
        };

        var plan = ReconcileEngine.BuildPlan(new[] { missing, different }, @"D:\dst");

        Assert.Equal(1, plan.CopyCount);
        Assert.Equal(1, plan.OverwriteCount);
        Assert.Equal(30, plan.TotalBytes);
    }

    [Fact]
    public async Task ExecuteAsync_copies_files_and_feeds_live_bytes()
    {
        using var t = new TempDir();
        var src = t.Sub("src"); var dst = t.Sub("dst");
        Directory.CreateDirectory(src); Directory.CreateDirectory(dst);
        var f0 = Path.Combine(src, "f0.bin"); var f1 = Path.Combine(src, "f1.bin");
        File.WriteAllBytes(f0, new byte[8 * MB]);
        File.WriteAllBytes(f1, new byte[4 * MB]);
        var plan = ReconcileEngine.BuildPlan(
            new[] { MissingAt("f0.bin", f0, 8 * MB), MissingAt("f1.bin", f1, 4 * MB) }, dst);

        int calls = 0; long max = 0, prev = -1; bool monotonic = true;
        Action<long> onLive = b => { calls++; if (b < prev) monotonic = false; prev = b; if (b > max) max = b; };

        var result = await new ReconcileEngine().ExecuteAsync(plan, src, dst, onLiveBytes: onLive);

        Assert.Equal(2, result.Copied);
        Assert.True(calls >= 10, $"expected many live-byte callbacks, got {calls}");
        Assert.True(monotonic);
        Assert.Equal(12L * MB, max);
        Assert.True(File.Exists(Path.Combine(dst, "f0.bin")) && File.Exists(Path.Combine(dst, "f1.bin")));
    }

    [Fact]
    public async Task ExecuteAsync_hard_cancel_removes_partial_and_reports_cleanup()
    {
        using var t = new TempDir();
        var src = t.Sub("src"); var dst = t.Sub("dst");
        Directory.CreateDirectory(src); Directory.CreateDirectory(dst);
        var f0 = Path.Combine(src, "f0.bin");
        File.WriteAllBytes(f0, new byte[8 * MB]);
        var plan = ReconcileEngine.BuildPlan(new[] { MissingAt("f0.bin", f0, 8 * MB) }, dst);

        var cts = new CancellationTokenSource();
        Action<long> onLive = b => { if (b >= 3 * MB) cts.Cancel(); }; // deterministic mid-file cancel
        var prog = new SyncProgress<ReconcileProgress>();

        var result = await new ReconcileEngine().ExecuteAsync(plan, src, dst, progress: prog, hardCancel: cts.Token, onLiveBytes: onLive);

        Assert.True(result.Stopped);
        Assert.Equal(1, result.PartialsRemoved);
        Assert.Empty(Directory.GetFiles(dst));
        Assert.Contains(prog.Items, p => p.Important && p.Message!.Contains("deleted partial"));
    }

    [Fact]
    public async Task ExecuteAsync_soft_stop_finishes_current_then_reports_last_file()
    {
        using var t = new TempDir();
        var src = t.Sub("src"); var dst = t.Sub("dst");
        Directory.CreateDirectory(src); Directory.CreateDirectory(dst);
        var f0 = Path.Combine(src, "f0.bin"); var f1 = Path.Combine(src, "f1.bin");
        File.WriteAllBytes(f0, new byte[4 * MB]);
        File.WriteAllBytes(f1, new byte[4 * MB]);
        var plan = ReconcileEngine.BuildPlan(
            new[] { MissingAt("f0.bin", f0, 4 * MB), MissingAt("f1.bin", f1, 4 * MB) }, dst);

        var soft = new CancellationTokenSource();
        Action<long> onLive = b => { if (b >= 3 * MB) soft.Cancel(); }; // during first file; it still finishes
        var prog = new SyncProgress<ReconcileProgress>();

        var result = await new ReconcileEngine().ExecuteAsync(plan, src, dst, progress: prog, softStop: soft.Token, onLiveBytes: onLive);

        Assert.True(result.Stopped);
        Assert.Equal(1, result.Copied);
        Assert.Equal(0, result.PartialsRemoved);
        Assert.Contains(prog.Items, p => p.Important && p.Message!.Contains("last file copied"));
    }

    [Fact]
    public async Task ExecuteAsync_preserves_metadata_and_overwrites_readonly_destination()
    {
        using var t = new TempDir();
        var src = t.Sub("src"); var dst = t.Sub("dst");
        Directory.CreateDirectory(src); Directory.CreateDirectory(dst);
        var srcFile = Path.Combine(src, "f.txt"); var dstFile = Path.Combine(dst, "f.txt");
        File.WriteAllText(srcFile, "v2-source");
        var created = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetCreationTimeUtc(srcFile, created);
        File.SetAttributes(srcFile, FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.Archive);
        File.WriteAllText(dstFile, "v1-old");
        File.SetAttributes(dstFile, FileAttributes.ReadOnly);

        var cmp = new ComparisonResult
        {
            RelativePath = "f.txt", Status = ComparisonStatus.Different, Differences = FileDifference.Size,
            Source = Rec.File("f.txt", 9, fullPath: srcFile), Dest = Rec.File("f.txt", 6, fullPath: dstFile),
        };
        var plan = ReconcileEngine.BuildPlan(new[] { cmp }, dst);

        var result = await new ReconcileEngine().ExecuteAsync(plan, src, dst);

        Assert.Equal(1, result.Overwritten);
        Assert.Equal("v2-source", File.ReadAllText(dstFile));
        Assert.Equal(created, File.GetCreationTimeUtc(dstFile));
        var attrs = File.GetAttributes(dstFile);
        Assert.True(attrs.HasFlag(FileAttributes.ReadOnly));
        Assert.True(attrs.HasFlag(FileAttributes.Hidden));
    }
}

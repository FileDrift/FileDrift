// SPDX-License-Identifier: GPL-3.0-or-later
using FileDrift.Core.Models;
using FileDrift.Core.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FileDrift.Core.Tests;

public class HistoryExportTests
{
    private static RunRecord Run(bool signedOff = false, long matched = 1) => new()
    {
        Id = Guid.NewGuid(),
        StartedAtUtc = DateTime.UtcNow,
        SourcePath = @"\\srv\share",
        DestPath = @"C:\Test1",
        Options = new VerifyOptions { IncludeAcl = true, StartUtc = DateTime.UtcNow.AddDays(-1) },
        Status = RunStatus.Completed,
        CompletedAtUtc = DateTime.UtcNow,
        MatchedCount = matched,
        SignedOffAtUtc = signedOff ? DateTime.UtcNow : null,
        SignedOffBy = signedOff ? "Approver" : null,
        SignedOffByAccount = signedOff ? @"DOM\op" : null,
    };

    [Fact]
    public async Task Export_then_import_round_trips_into_an_empty_repository()
    {
        using var t = new TempDir();
        var source = new SqliteRunRepository(t.Sub("source.db"));
        var run = Run(signedOff: true);
        await source.SaveAsync(run);

        var json = HistoryExport.Export(await source.ListAsync(), "1.0.0-test", DateTime.UtcNow);

        var dest = new SqliteRunRepository(t.Sub("dest.db"));
        var summary = await HistoryExport.ImportAsync(dest, json, overwrite: false);

        Assert.Equal(1, summary.Imported);
        Assert.Equal(0, summary.Updated + summary.SkippedExists + summary.SkippedProtected + summary.Errors);

        var loaded = await dest.GetAsync(run.Id);
        Assert.NotNull(loaded);
        Assert.Equal(run.SourcePath, loaded!.SourcePath);
        Assert.Equal(run.MatchedCount, loaded.MatchedCount);
        Assert.Equal(run.SignedOffBy, loaded.SignedOffBy);
        Assert.True(loaded.Options.IncludeAcl);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Import_without_overwrite_skips_existing_runs()
    {
        using var t = new TempDir();
        var repo = new SqliteRunRepository(t.Sub("s.db"));
        var run = Run(matched: 1);
        await repo.SaveAsync(run);

        // Simulate a re-import of the same run with different (stale) data — same Id, different counts.
        var sameIdIncoming = new RunRecord
        {
            Id = run.Id, StartedAtUtc = run.StartedAtUtc, SourcePath = run.SourcePath, DestPath = run.DestPath,
            Options = run.Options, MatchedCount = 999,
        };
        var json = HistoryExport.Export([sameIdIncoming], "1.0.0-test", DateTime.UtcNow);

        var summary = await HistoryExport.ImportAsync(repo, json, overwrite: false);

        Assert.Equal(0, summary.Imported);
        Assert.Equal(1, summary.SkippedExists);
        var stillLocal = await repo.GetAsync(run.Id);
        Assert.Equal(1, stillLocal!.MatchedCount); // untouched by the import
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Import_with_overwrite_updates_existing_unsigned_runs()
    {
        using var t = new TempDir();
        var repo = new SqliteRunRepository(t.Sub("s.db"));
        var run = Run(matched: 1);
        await repo.SaveAsync(run);

        var incoming = new RunRecord
        {
            Id = run.Id, StartedAtUtc = run.StartedAtUtc, SourcePath = run.SourcePath, DestPath = run.DestPath,
            Options = run.Options, MatchedCount = 999,
        };
        var json = HistoryExport.Export([incoming], "1.0.0-test", DateTime.UtcNow);

        var summary = await HistoryExport.ImportAsync(repo, json, overwrite: true);

        Assert.Equal(1, summary.Updated);
        var updated = await repo.GetAsync(run.Id);
        Assert.Equal(999, updated!.MatchedCount);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Import_with_overwrite_never_clobbers_a_locally_signed_off_run()
    {
        using var t = new TempDir();
        var repo = new SqliteRunRepository(t.Sub("s.db"));
        var run = Run(signedOff: true, matched: 1);
        await repo.SaveAsync(run);

        // An incoming record for the SAME run, unsigned — as if importing an older, pre-sign-off export.
        var incoming = new RunRecord
        {
            Id = run.Id, StartedAtUtc = run.StartedAtUtc, SourcePath = run.SourcePath, DestPath = run.DestPath,
            Options = run.Options, MatchedCount = 999,
        };
        var json = HistoryExport.Export([incoming], "1.0.0-test", DateTime.UtcNow);

        var summary = await HistoryExport.ImportAsync(repo, json, overwrite: true);

        Assert.Equal(0, summary.Updated);
        Assert.Equal(1, summary.SkippedProtected);
        var stillLocal = await repo.GetAsync(run.Id);
        Assert.Equal(1, stillLocal!.MatchedCount);          // sign-off protected it from the overwrite
        Assert.NotNull(stillLocal.SignedOffAtUtc);           // sign-off itself is intact
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task ImportAsync_rejects_a_document_without_the_format_marker()
    {
        using var t = new TempDir();
        var repo = new SqliteRunRepository(t.Sub("s.db"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            HistoryExport.ImportAsync(repo, """{"runs":[]}""", overwrite: false));
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task ImportAsync_rejects_malformed_json()
    {
        using var t = new TempDir();
        var repo = new SqliteRunRepository(t.Sub("s.db"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            HistoryExport.ImportAsync(repo, "not json at all", overwrite: false));
        SqliteConnection.ClearAllPools();
    }
}

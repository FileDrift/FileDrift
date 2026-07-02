// SPDX-License-Identifier: GPL-3.0-or-later
using FileDrift.Core.Models;
using FileDrift.Core.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FileDrift.Core.Tests;

public class SqliteRunRepositoryTests
{
    private static RunRecord NewRun(long inaccessible) => new()
    {
        Id = Guid.NewGuid(),
        StartedAtUtc = DateTime.UtcNow,
        SourcePath = @"\\srv\share",
        DestPath = @"C:\Test1",
        Options = new VerifyOptions(),
        Status = RunStatus.Completed,
        CompletedAtUtc = DateTime.UtcNow,
        MatchedCount = 10,
        InaccessibleCount = inaccessible,
    };

    [Fact]
    public async Task Roundtrips_inaccessible_count()
    {
        using var t = new TempDir();
        var repo = new SqliteRunRepository(t.Sub("h.db"));
        var run = NewRun(7);
        await repo.SaveAsync(run);

        var loaded = await repo.GetAsync(run.Id);

        Assert.NotNull(loaded);
        Assert.Equal(7, loaded!.InaccessibleCount);
        Assert.Equal(10, loaded.MatchedCount);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task MarkSignedOff_records_party_account_and_note()
    {
        using var t = new TempDir();
        var repo = new SqliteRunRepository(t.Sub("s.db"));
        var run = NewRun(0);
        await repo.SaveAsync(run);

        bool ok = await repo.MarkSignedOffAsync(run.Id, "Jane Approver", @"CONTOSO\jdoe", "reviewed Q2 migration");
        Assert.True(ok);

        var loaded = await repo.GetAsync(run.Id);
        Assert.NotNull(loaded);
        Assert.NotNull(loaded!.SignedOffAtUtc);
        Assert.Equal("Jane Approver", loaded.SignedOffBy);
        Assert.Equal(@"CONTOSO\jdoe", loaded.SignedOffByAccount);
        Assert.Equal("reviewed Q2 migration", loaded.SignOffNote);
        Assert.True(loaded.SignOffWasDelegated); // approver differs from operating account
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task MarkSignedOff_same_account_is_not_delegated()
    {
        using var t = new TempDir();
        var repo = new SqliteRunRepository(t.Sub("s.db"));
        var run = NewRun(0);
        await repo.SaveAsync(run);

        await repo.MarkSignedOffAsync(run.Id, @"DOM\op", @"DOM\op", null);

        var loaded = await repo.GetAsync(run.Id);
        Assert.False(loaded!.SignOffWasDelegated);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task MarkSignedOff_returns_false_for_unknown_run()
    {
        using var t = new TempDir();
        var repo = new SqliteRunRepository(t.Sub("s.db"));
        Assert.False(await repo.MarkSignedOffAsync(Guid.NewGuid(), "x", "y", null));
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Migrates_v2_database_adding_signoff_columns()
    {
        using var t = new TempDir();

        var seed = new SqliteRunRepository(t.Sub("seed.db"));
        await seed.SaveAsync(NewRun(0));
        string optionsJson;
        using (var c = new SqliteConnection($"Data Source={t.Sub("seed.db")}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT options_json FROM runs LIMIT 1;";
            optionsJson = (string)cmd.ExecuteScalar()!;
        }

        // Hand-build a v2 database: runs table has inaccessible_count but not the sign-off-by columns.
        var path = t.Sub("v2.db");
        var oldId = Guid.NewGuid();
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            void E(string sql) { using var c = conn.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
            E("CREATE TABLE schema_version (version INTEGER NOT NULL); INSERT INTO schema_version VALUES (2);");
            E("""
                CREATE TABLE runs (
                    id TEXT PRIMARY KEY, started_at_utc TEXT NOT NULL, source_path TEXT NOT NULL,
                    dest_path TEXT NOT NULL, options_json TEXT NOT NULL, completed_at_utc TEXT NULL,
                    status TEXT NOT NULL, total_source_files INTEGER NOT NULL, total_dest_files INTEGER NOT NULL,
                    matched_count INTEGER NOT NULL, different_count INTEGER NOT NULL,
                    missing_at_dest_count INTEGER NOT NULL, extra_at_dest_count INTEGER NOT NULL,
                    sign_off_note TEXT NULL, signed_off_at_utc TEXT NULL,
                    inaccessible_count INTEGER NOT NULL DEFAULT 0);
                """);
            using var ins = conn.CreateCommand();
            ins.CommandText = "INSERT INTO runs VALUES ($id,$s,$src,$dst,$o,$c,'Completed',0,0,4,0,0,0,NULL,NULL,2);";
            ins.Parameters.AddWithValue("$id", oldId.ToString());
            ins.Parameters.AddWithValue("$s", DateTime.UtcNow.ToString("O"));
            ins.Parameters.AddWithValue("$src", @"\\srv\old");
            ins.Parameters.AddWithValue("$dst", @"C:\Old");
            ins.Parameters.AddWithValue("$o", optionsJson);
            ins.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("O"));
            ins.ExecuteNonQuery();
        }

        // Opening with the current repo triggers the v2->v3 migration (two ADD COLUMNs).
        var repo = new SqliteRunRepository(path);
        var oldRun = await repo.GetAsync(oldId);
        Assert.NotNull(oldRun);
        Assert.Null(oldRun!.SignedOffBy);         // new columns default null on the existing row
        Assert.Null(oldRun.SignedOffByAccount);
        Assert.Equal(4, oldRun.MatchedCount);     // existing data preserved
        Assert.Equal(2, oldRun.InaccessibleCount);

        Assert.True(await repo.MarkSignedOffAsync(oldId, "Sam", @"DOM\sam", null)); // write path works post-migration
        var signed = await repo.GetAsync(oldId);
        Assert.Equal("Sam", signed!.SignedOffBy);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Migrates_v1_database_adding_inaccessible_column()
    {
        using var t = new TempDir();

        // A valid options_json from a real save (VerifyOptionsJson is internal).
        var seed = new SqliteRunRepository(t.Sub("seed.db"));
        await seed.SaveAsync(NewRun(0));
        string optionsJson;
        using (var c = new SqliteConnection($"Data Source={t.Sub("seed.db")}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT options_json FROM runs LIMIT 1;";
            optionsJson = (string)cmd.ExecuteScalar()!;
        }

        // Hand-build a v1 database (schema_version=1, runs table without inaccessible_count) + one row.
        var path = t.Sub("v1.db");
        var oldId = Guid.NewGuid();
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            void E(string sql) { using var c = conn.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
            E("CREATE TABLE schema_version (version INTEGER NOT NULL); INSERT INTO schema_version VALUES (1);");
            E("""
                CREATE TABLE runs (
                    id TEXT PRIMARY KEY, started_at_utc TEXT NOT NULL, source_path TEXT NOT NULL,
                    dest_path TEXT NOT NULL, options_json TEXT NOT NULL, completed_at_utc TEXT NULL,
                    status TEXT NOT NULL, total_source_files INTEGER NOT NULL, total_dest_files INTEGER NOT NULL,
                    matched_count INTEGER NOT NULL, different_count INTEGER NOT NULL,
                    missing_at_dest_count INTEGER NOT NULL, extra_at_dest_count INTEGER NOT NULL,
                    sign_off_note TEXT NULL, signed_off_at_utc TEXT NULL);
                """);
            using var ins = conn.CreateCommand();
            ins.CommandText = "INSERT INTO runs VALUES ($id,$s,$src,$dst,$o,$c,'Completed',0,0,3,0,0,0,NULL,NULL);";
            ins.Parameters.AddWithValue("$id", oldId.ToString());
            ins.Parameters.AddWithValue("$s", DateTime.UtcNow.ToString("O"));
            ins.Parameters.AddWithValue("$src", @"\\srv\old");
            ins.Parameters.AddWithValue("$dst", @"C:\Old");
            ins.Parameters.AddWithValue("$o", optionsJson);
            ins.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("O"));
            ins.ExecuteNonQuery();
        }

        // Opening with the current repo triggers the v1->v2 migration (ALTER TABLE ADD COLUMN).
        var repo = new SqliteRunRepository(path);
        var oldRun = await repo.GetAsync(oldId);
        var newRun = NewRun(5);
        await repo.SaveAsync(newRun);
        var newLoaded = await repo.GetAsync(newRun.Id);

        Assert.Equal(0, oldRun!.InaccessibleCount); // existing row defaulted by the migration
        Assert.Equal(3, oldRun.MatchedCount);       // existing data preserved
        Assert.Equal(5, newLoaded!.InaccessibleCount);
        SqliteConnection.ClearAllPools();
    }

    private static RunRecord NewRunAt(DateTime startedAtUtc, bool signedOff = false) => new()
    {
        Id = Guid.NewGuid(),
        StartedAtUtc = startedAtUtc,
        SourcePath = @"\\srv\share",
        DestPath = @"C:\Test1",
        Options = new VerifyOptions(),
        Status = RunStatus.Completed,
        CompletedAtUtc = startedAtUtc,
        MatchedCount = 1,
        SignedOffAtUtc = signedOff ? DateTime.UtcNow : null,
        SignedOffBy = signedOff ? "Approver" : null,
        SignedOffByAccount = signedOff ? @"DOM\op" : null,
    };

    [Fact]
    public async Task ListAsync_filters_by_signed_off_state()
    {
        using var t = new TempDir();
        var repo = new SqliteRunRepository(t.Sub("s.db"));
        var signed = NewRunAt(DateTime.UtcNow, signedOff: true);
        var unsigned = NewRunAt(DateTime.UtcNow, signedOff: false);
        await repo.SaveAsync(signed);
        await repo.SaveAsync(unsigned);

        var signedOnly = await repo.ListAsync(new RunQueryOptions { SignedOff = true });
        var unsignedOnly = await repo.ListAsync(new RunQueryOptions { SignedOff = false });
        var both = await repo.ListAsync(new RunQueryOptions { SignedOff = null });

        Assert.Single(signedOnly);
        Assert.Equal(signed.Id, signedOnly[0].Id);
        Assert.Single(unsignedOnly);
        Assert.Equal(unsigned.Id, unsignedOnly[0].Id);
        Assert.Equal(2, both.Count);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task DeleteUnsignedAsync_never_deletes_signed_off_runs()
    {
        using var t = new TempDir();
        var repo = new SqliteRunRepository(t.Sub("s.db"));
        var signed = NewRunAt(DateTime.UtcNow.AddDays(-200), signedOff: true);
        var unsigned = NewRunAt(DateTime.UtcNow.AddDays(-200), signedOff: false);
        await repo.SaveAsync(signed);
        await repo.SaveAsync(unsigned);

        int deleted = await repo.DeleteUnsignedAsync(olderThanUtc: null); // no age limit — would delete everything unsigned

        Assert.Equal(1, deleted);
        Assert.NotNull(await repo.GetAsync(signed.Id));  // protected regardless of age
        Assert.Null(await repo.GetAsync(unsigned.Id));   // removed
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task DeleteUnsignedAsync_honors_age_cutoff()
    {
        using var t = new TempDir();
        var repo = new SqliteRunRepository(t.Sub("s.db"));
        var old = NewRunAt(DateTime.UtcNow.AddDays(-100));
        var recent = NewRunAt(DateTime.UtcNow.AddDays(-1));
        await repo.SaveAsync(old);
        await repo.SaveAsync(recent);

        int deleted = await repo.DeleteUnsignedAsync(olderThanUtc: DateTime.UtcNow.AddDays(-30));

        Assert.Equal(1, deleted);
        Assert.Null(await repo.GetAsync(old.Id));      // older than cutoff — removed
        Assert.NotNull(await repo.GetAsync(recent.Id)); // within cutoff — kept
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Roundtrips_reconcile_summary()
    {
        using var t = new TempDir();
        var repo = new SqliteRunRepository(t.Sub("s.db"));
        var run = NewRun(0);
        await repo.SaveAsync(run);

        run.ReconciledAtUtc = DateTime.UtcNow;
        run.ReconcileBytesCopied = 123_456_789_012;
        run.ReconcileFilesCopied = 42;
        run.ReconcileFilesOverwritten = 7;
        run.ReconcileStopped = true;
        await repo.SaveAsync(run);

        var loaded = await repo.GetAsync(run.Id);
        Assert.NotNull(loaded);
        Assert.NotNull(loaded!.ReconciledAtUtc);
        Assert.Equal(123_456_789_012, loaded.ReconcileBytesCopied);
        Assert.Equal(42, loaded.ReconcileFilesCopied);
        Assert.Equal(7, loaded.ReconcileFilesOverwritten);
        Assert.True(loaded.ReconcileStopped);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Unreconciled_run_defaults_reconcile_fields_to_empty()
    {
        using var t = new TempDir();
        var repo = new SqliteRunRepository(t.Sub("s.db"));
        var run = NewRun(0);
        await repo.SaveAsync(run);

        var loaded = await repo.GetAsync(run.Id);
        Assert.Null(loaded!.ReconciledAtUtc);
        Assert.Equal(0, loaded.ReconcileBytesCopied);
        Assert.False(loaded.ReconcileStopped);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Migrates_v3_database_adding_reconcile_columns()
    {
        using var t = new TempDir();

        var seed = new SqliteRunRepository(t.Sub("seed.db"));
        await seed.SaveAsync(NewRun(0));
        string optionsJson;
        using (var c = new SqliteConnection($"Data Source={t.Sub("seed.db")}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT options_json FROM runs LIMIT 1;";
            optionsJson = (string)cmd.ExecuteScalar()!;
        }

        // Hand-build a v3 database: runs table has signed_off_by/account but no reconcile_* columns.
        var path = t.Sub("v3.db");
        var oldId = Guid.NewGuid();
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            void E(string sql) { using var c = conn.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
            E("CREATE TABLE schema_version (version INTEGER NOT NULL); INSERT INTO schema_version VALUES (3);");
            E("""
                CREATE TABLE runs (
                    id TEXT PRIMARY KEY, started_at_utc TEXT NOT NULL, source_path TEXT NOT NULL,
                    dest_path TEXT NOT NULL, options_json TEXT NOT NULL, completed_at_utc TEXT NULL,
                    status TEXT NOT NULL, total_source_files INTEGER NOT NULL, total_dest_files INTEGER NOT NULL,
                    matched_count INTEGER NOT NULL, different_count INTEGER NOT NULL,
                    missing_at_dest_count INTEGER NOT NULL, extra_at_dest_count INTEGER NOT NULL,
                    sign_off_note TEXT NULL, signed_off_at_utc TEXT NULL,
                    inaccessible_count INTEGER NOT NULL DEFAULT 0,
                    signed_off_by TEXT NULL, signed_off_by_account TEXT NULL);
                """);
            using var ins = conn.CreateCommand();
            ins.CommandText =
                "INSERT INTO runs VALUES ($id,$s,$src,$dst,$o,$c,'Completed',0,0,4,0,0,0,NULL,NULL,0,NULL,NULL);";
            ins.Parameters.AddWithValue("$id", oldId.ToString());
            ins.Parameters.AddWithValue("$s", DateTime.UtcNow.ToString("O"));
            ins.Parameters.AddWithValue("$src", @"\\srv\old");
            ins.Parameters.AddWithValue("$dst", @"C:\Old");
            ins.Parameters.AddWithValue("$o", optionsJson);
            ins.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("O"));
            ins.ExecuteNonQuery();
        }

        // Opening with the current repo triggers the v3->v4 migration (five ADD COLUMNs).
        var repo = new SqliteRunRepository(path);
        var oldRun = await repo.GetAsync(oldId);
        Assert.NotNull(oldRun);
        Assert.Null(oldRun!.ReconciledAtUtc);      // new columns default null/0 on the existing row
        Assert.Equal(0, oldRun.ReconcileBytesCopied);
        Assert.False(oldRun.ReconcileStopped);
        Assert.Equal(4, oldRun.MatchedCount);      // existing data preserved

        oldRun.ReconciledAtUtc = DateTime.UtcNow;
        oldRun.ReconcileBytesCopied = 999;
        await repo.SaveAsync(oldRun); // write path works post-migration
        var updated = await repo.GetAsync(oldId);
        Assert.Equal(999, updated!.ReconcileBytesCopied);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task DeleteUnsignedAsync_dry_run_count_matches_actual_delete()
    {
        // ListAsync(SignedOff:false, Before:cutoff) is how the CLI's dry-run counts what WOULD be deleted;
        // it must agree exactly with what DeleteUnsignedAsync actually removes for the same cutoff.
        using var t = new TempDir();
        var repo = new SqliteRunRepository(t.Sub("s.db"));
        var cutoff = DateTime.UtcNow.AddDays(-30);
        await repo.SaveAsync(NewRunAt(cutoff.AddDays(-1)));           // older — matches
        await repo.SaveAsync(NewRunAt(cutoff));                       // exactly at cutoff — matches (<=)
        await repo.SaveAsync(NewRunAt(cutoff.AddDays(1)));            // newer — doesn't match
        await repo.SaveAsync(NewRunAt(cutoff.AddDays(-1), signedOff: true)); // old but signed off — never matches

        var dryRun = await repo.ListAsync(new RunQueryOptions { SignedOff = false, Before = cutoff });
        int deleted = await repo.DeleteUnsignedAsync(cutoff);

        Assert.Equal(2, dryRun.Count);
        Assert.Equal(dryRun.Count, deleted);
        SqliteConnection.ClearAllPools();
    }
}

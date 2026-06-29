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
}

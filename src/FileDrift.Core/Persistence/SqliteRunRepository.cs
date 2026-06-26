using System.Globalization;
using FileDrift.Core.Interfaces;
using FileDrift.Core.Models;
using Microsoft.Data.Sqlite;

namespace FileDrift.Core.Persistence;

/// <summary>SQLite-backed run history. Creates and migrates its schema on construction.</summary>
public sealed class SqliteRunRepository : IRunRepository
{
    private const int TargetSchemaVersion = 2;

    private readonly string _connectionString;

    /// <summary>The on-disk database file backing this repository.</summary>
    public string DatabasePath { get; }

    /// <param name="databasePath">Path to the SQLite file. Defaults to %APPDATA%\FileDrift\history.db.</param>
    public SqliteRunRepository(string? databasePath = null)
    {
        DatabasePath = databasePath ?? AppPaths.HistoryDatabase;

        var dir = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        Initialize();
    }

    // ─────────────────────────── schema ───────────────────────────

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private void Initialize()
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        Execute(conn, tx, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);");

        int current = ReadSchemaVersion(conn, tx);
        if (current < 1)
            ApplyMigration1(conn, tx);
        if (current < 2)
            ApplyMigration2(conn, tx);

        if (current < TargetSchemaVersion)
        {
            Execute(conn, tx, "DELETE FROM schema_version;");
            using var set = conn.CreateCommand();
            set.Transaction = tx;
            set.CommandText = "INSERT INTO schema_version (version) VALUES ($v);";
            set.Parameters.AddWithValue("$v", TargetSchemaVersion);
            set.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static int ReadSchemaVersion(SqliteConnection conn, SqliteTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT version FROM schema_version LIMIT 1;";
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static void ApplyMigration1(SqliteConnection conn, SqliteTransaction tx)
    {
        Execute(conn, tx, """
            CREATE TABLE IF NOT EXISTS runs (
                id                    TEXT    PRIMARY KEY,
                started_at_utc        TEXT    NOT NULL,
                source_path           TEXT    NOT NULL,
                dest_path             TEXT    NOT NULL,
                options_json          TEXT    NOT NULL,
                completed_at_utc      TEXT    NULL,
                status                TEXT    NOT NULL,
                total_source_files    INTEGER NOT NULL,
                total_dest_files      INTEGER NOT NULL,
                matched_count         INTEGER NOT NULL,
                different_count       INTEGER NOT NULL,
                missing_at_dest_count INTEGER NOT NULL,
                extra_at_dest_count   INTEGER NOT NULL,
                sign_off_note         TEXT    NULL,
                signed_off_at_utc     TEXT    NULL
            );
            """);
        Execute(conn, tx, "CREATE INDEX IF NOT EXISTS idx_runs_started ON runs(started_at_utc);");
        Execute(conn, tx, "CREATE INDEX IF NOT EXISTS idx_runs_source  ON runs(source_path);");
        Execute(conn, tx, "CREATE INDEX IF NOT EXISTS idx_runs_status  ON runs(status);");
    }

    private static void ApplyMigration2(SqliteConnection conn, SqliteTransaction tx) =>
        Execute(conn, tx, "ALTER TABLE runs ADD COLUMN inaccessible_count INTEGER NOT NULL DEFAULT 0;");

    private static void Execute(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ─────────────────────────── IRunRepository ───────────────────────────

    public async Task SaveAsync(RunRecord run, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO runs (
                id, started_at_utc, source_path, dest_path, options_json,
                completed_at_utc, status, total_source_files, total_dest_files,
                matched_count, different_count, missing_at_dest_count, extra_at_dest_count,
                sign_off_note, signed_off_at_utc, inaccessible_count)
            VALUES (
                $id, $started, $src, $dst, $options,
                $completed, $status, $totalSrc, $totalDst,
                $matched, $different, $missing, $extra,
                $note, $signedOff, $inaccessible)
            ON CONFLICT(id) DO UPDATE SET
                started_at_utc        = excluded.started_at_utc,
                source_path           = excluded.source_path,
                dest_path             = excluded.dest_path,
                options_json          = excluded.options_json,
                completed_at_utc      = excluded.completed_at_utc,
                status                = excluded.status,
                total_source_files    = excluded.total_source_files,
                total_dest_files      = excluded.total_dest_files,
                matched_count         = excluded.matched_count,
                different_count       = excluded.different_count,
                missing_at_dest_count = excluded.missing_at_dest_count,
                extra_at_dest_count   = excluded.extra_at_dest_count,
                sign_off_note         = excluded.sign_off_note,
                signed_off_at_utc     = excluded.signed_off_at_utc,
                inaccessible_count    = excluded.inaccessible_count;
            """;

        AddParam(cmd, "$id",        run.Id.ToString());
        AddParam(cmd, "$started",   ToIso(run.StartedAtUtc));
        AddParam(cmd, "$src",       run.SourcePath);
        AddParam(cmd, "$dst",       run.DestPath);
        AddParam(cmd, "$options",   VerifyOptionsJson.Serialize(run.Options));
        AddParam(cmd, "$completed", ToIsoOrNull(run.CompletedAtUtc));
        AddParam(cmd, "$status",    run.Status.ToString());
        AddParam(cmd, "$totalSrc",  run.TotalSourceFiles);
        AddParam(cmd, "$totalDst",  run.TotalDestFiles);
        AddParam(cmd, "$matched",   run.MatchedCount);
        AddParam(cmd, "$different", run.DifferentCount);
        AddParam(cmd, "$missing",   run.MissingAtDestCount);
        AddParam(cmd, "$extra",     run.ExtraAtDestCount);
        AddParam(cmd, "$note",      run.SignOffNote);
        AddParam(cmd, "$signedOff", ToIsoOrNull(run.SignedOffAtUtc));
        AddParam(cmd, "$inaccessible", run.InaccessibleCount);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<RunRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM runs WHERE id = $id;";
        AddParam(cmd, "$id", id.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return Map(reader);
    }

    public async Task<IReadOnlyList<RunRecord>> ListAsync(
        RunQueryOptions? query = null,
        CancellationToken cancellationToken = default)
    {
        var conditions = new List<string>();
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();

        if (query is not null)
        {
            if (!string.IsNullOrWhiteSpace(query.SourcePath))
            {
                conditions.Add("source_path = $src");
                AddParam(cmd, "$src", query.SourcePath);
            }
            if (!string.IsNullOrWhiteSpace(query.DestPath))
            {
                conditions.Add("dest_path = $dst");
                AddParam(cmd, "$dst", query.DestPath);
            }
            if (query.Status is { } status)
            {
                conditions.Add("status = $status");
                AddParam(cmd, "$status", status.ToString());
            }
            if (query.After is { } after)
            {
                conditions.Add("started_at_utc >= $after");
                AddParam(cmd, "$after", ToIso(after));
            }
            if (query.Before is { } before)
            {
                conditions.Add("started_at_utc <= $before");
                AddParam(cmd, "$before", ToIso(before));
            }
        }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        var limit = query?.Limit is { } n && n > 0 ? "LIMIT $limit" : "";
        if (limit.Length > 0)
            AddParam(cmd, "$limit", query!.Limit!.Value);

        cmd.CommandText = $"SELECT {Columns} FROM runs {where} ORDER BY started_at_utc DESC {limit};";

        var results = new List<RunRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(Map(reader));
        return results;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM runs WHERE id = $id;";
        AddParam(cmd, "$id", id.ToString());
        int affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    // ─────────────────────────── mapping ───────────────────────────

    private const string Columns =
        "id, started_at_utc, source_path, dest_path, options_json, " +
        "completed_at_utc, status, total_source_files, total_dest_files, " +
        "matched_count, different_count, missing_at_dest_count, extra_at_dest_count, " +
        "sign_off_note, signed_off_at_utc, inaccessible_count";

    private static RunRecord Map(SqliteDataReader r)
    {
        return new RunRecord
        {
            Id           = Guid.Parse(r.GetString(0)),
            StartedAtUtc = FromIso(r.GetString(1)),
            SourcePath   = r.GetString(2),
            DestPath     = r.GetString(3),
            Options      = VerifyOptionsJson.Deserialize(r.GetString(4)),
            CompletedAtUtc     = r.IsDBNull(5) ? null : FromIso(r.GetString(5)),
            Status             = Enum.Parse<RunStatus>(r.GetString(6)),
            TotalSourceFiles   = r.GetInt64(7),
            TotalDestFiles     = r.GetInt64(8),
            MatchedCount       = r.GetInt64(9),
            DifferentCount     = r.GetInt64(10),
            MissingAtDestCount = r.GetInt64(11),
            ExtraAtDestCount   = r.GetInt64(12),
            SignOffNote        = r.IsDBNull(13) ? null : r.GetString(13),
            SignedOffAtUtc     = r.IsDBNull(14) ? null : FromIso(r.GetString(14)),
            InaccessibleCount  = r.GetInt64(15),
        };
    }

    private static void AddParam(SqliteCommand cmd, string name, object? value) =>
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string ToIso(DateTime utc) =>
        utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? ToIsoOrNull(DateTime? utc) =>
        utc is { } v ? ToIso(v) : null;

    private static DateTime FromIso(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

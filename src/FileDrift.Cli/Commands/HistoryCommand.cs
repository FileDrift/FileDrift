// SPDX-License-Identifier: GPL-3.0-or-later
using System.CommandLine;
using FileDrift.Core;
using FileDrift.Core.Models;
using FileDrift.Core.Persistence;

namespace FileDrift.Cli.Commands;

internal static class HistoryCommand
{
    internal static Command Build()
    {
        var last = new Option<int>("--last")   { Description = "Show the N most recent runs (default: 20)" };
        var src  = new Option<string>("--src") { Description = "Filter by source path" };
        var dst  = new Option<string>("--dst") { Description = "Filter by destination path" };
        var since = new Option<string>("--since")
            { Description = "Only show runs started within the last N days, e.g. \"7d\", \"30d\", \"90\"" };
        var signedOff = new Option<bool?>("--signed-off")
            { Description = "Filter by sign-off state: true = signed off only, false = unsigned only" };

        var cmd = new Command("history", "List past runs");
        cmd.Add(last);
        cmd.Add(src);
        cmd.Add(dst);
        cmd.Add(since);
        cmd.Add(signedOff);

        cmd.SetAction(async (parseResult, ct) =>
        {
            try
            {
                var sinceVal = parseResult.GetValue(since);
                if (sinceVal is not null && ParseDays(sinceVal) is null)
                    return CliOutput.Error("history", $"'{sinceVal}' is not a valid --since value (expected e.g. \"7d\", \"30d\", \"90\").");

                var query = new RunQueryOptions
                {
                    Limit = parseResult.GetValue(last) is int n && n > 0 ? n : 20,
                    SourcePath = parseResult.GetValue(src),
                    DestPath = parseResult.GetValue(dst),
                    After = sinceVal is null ? null : DateTime.UtcNow.AddDays(-ParseDays(sinceVal)!.Value),
                    SignedOff = parseResult.GetValue(signedOff),
                };

                var runs = await CliServices.Repository().ListAsync(query, ct);

                CliOutput.Write(new
                {
                    verb = "history",
                    count = runs.Count,
                    runs = runs.Select(r => new
                    {
                        runId = r.Id,
                        startedAtUtc = r.StartedAtUtc,
                        completedAtUtc = r.CompletedAtUtc,
                        status = r.Status,
                        source = r.SourcePath,
                        destination = r.DestPath,
                        matched = r.MatchedCount,
                        differences = r.TotalDifferences,
                        signedOff = r.SignedOffAtUtc is not null,
                    }),
                });

                return 0;
            }
            catch (Exception ex)
            {
                return CliOutput.Error("history", ex.Message, ex.GetType().Name);
            }
        });

        cmd.Add(BuildExport());
        cmd.Add(BuildImport());
        cmd.Add(BuildPrune());
        return cmd;
    }

    /// <summary>Parses a day count like "7d", "30d", or a bare "90" into an integer. Returns null if the
    /// value doesn't parse.</summary>
    private static int? ParseDays(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.EndsWith('d') || trimmed.EndsWith('D'))
            trimmed = trimmed[..^1];
        return int.TryParse(trimmed, out var days) && days > 0 ? days : null;
    }

    private static Command BuildExport()
    {
        var outOpt = new Option<string>("--out") { Description = "Output JSON file", Required = true };
        var src = new Option<string>("--src") { Description = "Filter by source path" };
        var dst = new Option<string>("--dst") { Description = "Filter by destination path" };
        var since = new Option<string>("--since") { Description = "Only export runs started within the last N days" };

        var cmd = new Command("export", "Export run history to a JSON file");
        cmd.Add(outOpt);
        cmd.Add(src);
        cmd.Add(dst);
        cmd.Add(since);

        cmd.SetAction(async (parseResult, ct) =>
        {
            try
            {
                var sinceVal = parseResult.GetValue(since);
                if (sinceVal is not null && ParseDays(sinceVal) is null)
                    return CliOutput.Error("history export", $"'{sinceVal}' is not a valid --since value.");

                var query = new RunQueryOptions
                {
                    SourcePath = parseResult.GetValue(src),
                    DestPath = parseResult.GetValue(dst),
                    After = sinceVal is null ? null : DateTime.UtcNow.AddDays(-ParseDays(sinceVal)!.Value),
                };

                var runs = await CliServices.Repository().ListAsync(query, ct);
                var json = HistoryExport.Export(runs, AppInfo.Version, DateTime.UtcNow);
                var path = Path.GetFullPath(parseResult.GetValue(outOpt)!);
                await File.WriteAllTextAsync(path, json, ct);

                CliOutput.Write(new { verb = "history export", file = path, count = runs.Count });
                return 0;
            }
            catch (Exception ex)
            {
                return CliOutput.Error("history export", ex.Message, ex.GetType().Name);
            }
        });
        return cmd;
    }

    private static Command BuildImport()
    {
        var inOpt = new Option<string>("--in") { Description = "Input JSON file", Required = true };
        var overwrite = new Option<bool>("--overwrite")
            { Description = "Overwrite runs that already exist locally (never overwrites a locally signed-off run)" };

        var cmd = new Command("import", "Import run history from a JSON file");
        cmd.Add(inOpt);
        cmd.Add(overwrite);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var path = parseResult.GetValue(inOpt)!;
            if (!File.Exists(path))
                return CliOutput.Error("history import", $"File not found: {path}");

            try
            {
                var json = await File.ReadAllTextAsync(path, ct);
                var summary = await HistoryExport.ImportAsync(
                    CliServices.Repository(), json, parseResult.GetValue(overwrite), ct);

                CliOutput.Write(new
                {
                    verb = "history import",
                    imported = summary.Imported,
                    updated = summary.Updated,
                    skippedExisting = summary.SkippedExists,
                    skippedProtected = summary.SkippedProtected,
                    errors = summary.Errors,
                });
                return summary.Errors > 0 ? 1 : 0;
            }
            catch (InvalidDataException ex)
            {
                return CliOutput.Error("history import", ex.Message);
            }
            catch (Exception ex)
            {
                return CliOutput.Error("history import", ex.Message, ex.GetType().Name);
            }
        });
        return cmd;
    }

    private static Command BuildPrune()
    {
        var olderThan = new Option<string>("--older-than")
            { Description = "Only delete unsigned runs started more than N days ago, e.g. \"90d\". Omit to delete all unsigned runs." };
        var yes = new Option<bool>("--yes") { Description = "Actually delete. Without this, only reports what would be deleted." };

        var cmd = new Command("prune", "Delete unsigned run history. Signed-off runs are never deleted by this command.");
        cmd.Add(olderThan);
        cmd.Add(yes);

        cmd.SetAction(async (parseResult, ct) =>
        {
            try
            {
                var olderThanVal = parseResult.GetValue(olderThan);
                if (olderThanVal is not null && ParseDays(olderThanVal) is null)
                    return CliOutput.Error("history prune", $"'{olderThanVal}' is not a valid --older-than value.");
                DateTime? cutoff = olderThanVal is null ? null : DateTime.UtcNow.AddDays(-ParseDays(olderThanVal)!.Value);

                var repo = CliServices.Repository();
                if (!parseResult.GetValue(yes))
                {
                    var matching = await repo.ListAsync(new RunQueryOptions { SignedOff = false, Before = cutoff }, ct);
                    CliOutput.Write(new
                    {
                        verb = "history prune",
                        dryRun = true,
                        wouldDelete = matching.Count,
                        note = "Signed-off runs are never included. Re-run with --yes to delete.",
                    });
                    return 0;
                }

                int deleted = await repo.DeleteUnsignedAsync(cutoff, ct);
                CliOutput.Write(new { verb = "history prune", deleted });
                return 0;
            }
            catch (Exception ex)
            {
                return CliOutput.Error("history prune", ex.Message, ex.GetType().Name);
            }
        });
        return cmd;
    }
}

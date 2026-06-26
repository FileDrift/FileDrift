using System.CommandLine;
using FileDrift.Core.Models;

namespace FileDrift.Cli.Commands;

internal static class HistoryCommand
{
    internal static Command Build()
    {
        var last = new Option<int>("--last")   { Description = "Show the N most recent runs (default: 20)" };
        var src  = new Option<string>("--src") { Description = "Filter by source path" };
        var dst  = new Option<string>("--dst") { Description = "Filter by destination path" };

        var cmd = new Command("history", "List past runs");
        cmd.Add(last);
        cmd.Add(src);
        cmd.Add(dst);

        cmd.SetAction(async (parseResult, ct) =>
        {
            try
            {
                var query = new RunQueryOptions
                {
                    Limit = parseResult.GetValue(last) is int n && n > 0 ? n : 20,
                    SourcePath = parseResult.GetValue(src),
                    DestPath = parseResult.GetValue(dst),
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

        return cmd;
    }
}

using System.CommandLine;

namespace FileDrift.App.Cli.Commands;

internal static class ReportCommand
{
    internal static Command Build()
    {
        var id = new Option<string>("--id") { Description = "Run ID to retrieve", Required = true };

        var cmd = new Command("report", "Print the stored summary report for a completed run");
        cmd.Add(id);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var idVal = parseResult.GetValue(id)!;

            if (!Guid.TryParse(idVal, out var runId))
                return CliOutput.Error("report", $"'{idVal}' is not a valid run ID (GUID).");

            try
            {
                var run = await CliServices.Repository().GetAsync(runId, ct);
                if (run is null)
                    return CliOutput.Error("report", $"No run found with ID {runId}.");

                CliOutput.Write(new
                {
                    verb = "report",
                    runId = run.Id,
                    status = run.Status,
                    startedAtUtc = run.StartedAtUtc,
                    completedAtUtc = run.CompletedAtUtc,
                    source = run.SourcePath,
                    destination = run.DestPath,
                    options = new
                    {
                        depth = run.Options.Depth,
                        hashAlgorithm = run.Options.HashAlgorithm,
                        includeAcl = run.Options.IncludeAcl,
                        threads = run.Options.Threads,
                    },
                    summary = new
                    {
                        sourceFiles = run.TotalSourceFiles,
                        destFiles = run.TotalDestFiles,
                        matched = run.MatchedCount,
                        different = run.DifferentCount,
                        missingAtDest = run.MissingAtDestCount,
                        extraAtDest = run.ExtraAtDestCount,
                    },
                    signOff = new
                    {
                        signedOffAtUtc = run.SignedOffAtUtc,
                        note = run.SignOffNote,
                    },
                });

                return 0;
            }
            catch (Exception ex)
            {
                return CliOutput.Error("report", ex.Message, ex.GetType().Name);
            }
        });

        return cmd;
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
using System.CommandLine;
using FileDrift.Core;

namespace FileDrift.Cli.Commands;

internal static class SignOffCommand
{
    internal static Command Build()
    {
        var id = new Option<string>("--id") { Description = "Run ID to sign off", Required = true };
        var by = new Option<string>("--by")
        {
            Description = "Accountable party to record. Defaults to the current Windows account. " +
                          "When different, both the entered name and the operating account are stored.",
        };
        var note = new Option<string>("--note") { Description = "Optional sign-off note" };
        var force = new Option<bool>("--force") { Description = "Re-sign a run that is already signed off" };

        var cmd = new Command("signoff", "Record sign-off for a completed run");
        cmd.Add(id);
        cmd.Add(by);
        cmd.Add(note);
        cmd.Add(force);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var idVal = parseResult.GetValue(id)!;
            if (!Guid.TryParse(idVal, out var runId))
                return CliOutput.Error("signoff", $"'{idVal}' is not a valid run ID (GUID).");

            try
            {
                var repo = CliServices.Repository();
                var run = await repo.GetAsync(runId, ct);
                if (run is null)
                    return CliOutput.Error("signoff", $"No run found with ID {runId}.");

                if (run.SignedOffAtUtc is not null && !parseResult.GetValue(force))
                    return CliOutput.Error("signoff",
                        $"Run {runId} was already signed off at {run.SignedOffAtUtc:u} by " +
                        $"'{run.SignedOffBy}'. Pass --force to re-sign.");

                var account = OperatorIdentity.Current;
                var enteredBy = parseResult.GetValue(by);
                var signedOffBy = string.IsNullOrWhiteSpace(enteredBy) ? account : enteredBy.Trim();
                var noteVal = parseResult.GetValue(note);

                bool ok = await repo.MarkSignedOffAsync(runId, signedOffBy, account, noteVal, ct);
                if (!ok)
                    return CliOutput.Error("signoff", $"No run found with ID {runId}.");

                var updated = await repo.GetAsync(runId, ct);
                CliOutput.Write(new
                {
                    verb = "signoff",
                    runId,
                    signedOffAtUtc = updated!.SignedOffAtUtc,
                    signedOffBy = updated.SignedOffBy,
                    signedOffByAccount = updated.SignedOffByAccount,
                    delegated = updated.SignOffWasDelegated,
                    note = updated.SignOffNote,
                });

                return 0;
            }
            catch (Exception ex)
            {
                return CliOutput.Error("signoff", ex.Message, ex.GetType().Name);
            }
        });

        return cmd;
    }
}

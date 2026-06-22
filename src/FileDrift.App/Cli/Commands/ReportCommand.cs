using System.CommandLine;

namespace FileDrift.App.Cli.Commands;

internal static class ReportCommand
{
    internal static Command Build()
    {
        var id = new Option<string>("--id") { Description = "Run ID to retrieve", Required = true };

        var cmd = new Command("report", "Print the full result report for a completed run");
        cmd.Add(id);

        cmd.SetAction(parseResult =>
        {
            var idVal = parseResult.GetValue(id)!;
            CliOutput.Write(new { verb = "report", status = "not_implemented", args = new { id = idVal } });
        });

        return cmd;
    }
}

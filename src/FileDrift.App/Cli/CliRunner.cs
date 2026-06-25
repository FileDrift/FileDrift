using System.CommandLine;
using FileDrift.App.Cli.Commands;

namespace FileDrift.App.Cli;

internal static class CliRunner
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var root = new RootCommand("FileDrift – file comparison and verification tool");
        root.Add(PreflightCommand.Build());
        root.Add(VerifyCommand.Build());
        root.Add(ReportCommand.Build());
        root.Add(HistoryCommand.Build());
        return await root.Parse(args).InvokeAsync(configuration: null, cancellationToken);
    }
}

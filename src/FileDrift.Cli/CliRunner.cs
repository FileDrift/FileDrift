using System.CommandLine;
using FileDrift.Cli.Commands;

namespace FileDrift.Cli;

internal static class CliRunner
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var root = new RootCommand("FileDrift – file comparison and verification tool");
        root.Add(PreflightCommand.Build());
        root.Add(VerifyCommand.Build());
        root.Add(ReconcileCommand.Build());
        root.Add(ReportCommand.Build());
        root.Add(HistoryCommand.Build());
        root.Add(CredentialCommand.Build());
        return await root.Parse(args).InvokeAsync(configuration: null, cancellationToken);
    }
}

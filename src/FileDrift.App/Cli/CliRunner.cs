using System.CommandLine;
using FileDrift.App.Cli.Commands;

namespace FileDrift.App.Cli;

internal static class CliRunner
{
    internal static int Run(string[] args)
    {
        var root = new RootCommand("FileDrift — file comparison and verification tool");
        root.Add(PreflightCommand.Build());
        root.Add(VerifyCommand.Build());
        root.Add(ReportCommand.Build());
        root.Add(HistoryCommand.Build());
        return root.Parse(args).Invoke();
    }
}

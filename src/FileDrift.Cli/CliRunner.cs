// SPDX-License-Identifier: GPL-3.0-or-later
using System.CommandLine;
using FileDrift.Cli.Commands;

namespace FileDrift.Cli;

internal static class CliRunner
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        // Output format: explicit --json/--table wins; otherwise a table in an interactive console and
        // JSON when redirected/piped (so scripts always get JSON). Strip the flags before parsing.
        CliOutput.Format =
            args.Contains("--json")  ? OutputFormat.Json :
            args.Contains("--table") ? OutputFormat.Table :
            Console.IsOutputRedirected ? OutputFormat.Json : OutputFormat.Table;
        args = args.Where(a => a is not ("--json" or "--table")).ToArray();

        var root = new RootCommand("FileDrift – file comparison and verification tool");
        root.Add(PreflightCommand.Build());
        root.Add(VerifyCommand.Build());
        root.Add(ReconcileCommand.Build());
        root.Add(ReportCommand.Build());
        root.Add(SignOffCommand.Build());
        root.Add(CertificateCommand.Build());
        root.Add(HistoryCommand.Build());
        root.Add(CredentialCommand.Build());
        return await root.Parse(args).InvokeAsync(configuration: null, cancellationToken);
    }
}

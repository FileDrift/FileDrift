using System.CommandLine;

namespace FileDrift.App.Cli.Commands;

internal static class PreflightCommand
{
    internal static Command Build()
    {
        var src = new Option<string>("--src") { Description = "Source path (local or UNC)", Required = true };
        var dst = new Option<string>("--dst") { Description = "Destination path (local or UNC)", Required = true };

        var cmd = new Command("preflight", "Check that source and destination are accessible before running a verify");
        cmd.Add(src);
        cmd.Add(dst);

        cmd.SetAction(parseResult =>
        {
            var srcVal = parseResult.GetValue(src)!;
            var dstVal = parseResult.GetValue(dst)!;
            CliOutput.Write(new { verb = "preflight", status = "not_implemented", args = new { src = srcVal, dst = dstVal } });
        });

        return cmd;
    }
}

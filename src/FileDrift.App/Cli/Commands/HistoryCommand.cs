using System.CommandLine;

namespace FileDrift.App.Cli.Commands;

internal static class HistoryCommand
{
    internal static Command Build()
    {
        var last = new Option<int>("--last")    { Description = "Show the N most recent runs" };
        var src  = new Option<string>("--src")  { Description = "Filter by source path" };
        var dst  = new Option<string>("--dst")  { Description = "Filter by destination path" };

        var cmd = new Command("history", "List past runs");
        cmd.Add(last);
        cmd.Add(src);
        cmd.Add(dst);

        cmd.SetAction(parseResult =>
        {
            var lastVal = parseResult.GetValue(last) is int n && n > 0 ? (int?)n : null;
            var srcVal  = parseResult.GetValue(src);
            var dstVal  = parseResult.GetValue(dst);
            CliOutput.Write(new
            {
                verb   = "history",
                status = "not_implemented",
                args   = new { last = lastVal, src = srcVal, dst = dstVal }
            });
        });

        return cmd;
    }
}

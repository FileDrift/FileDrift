using System.CommandLine;

namespace FileDrift.App.Cli.Commands;

internal static class VerifyCommand
{
    internal static Command Build()
    {
        var src     = new Option<string>("--src")   { Description = "Source path (local or UNC)", Required = true };
        var dst     = new Option<string>("--dst")   { Description = "Destination path (local or UNC)", Required = true };
        var depth   = new Option<string>("--depth") { Description = "Comparison depth: quick | standard | full (default: standard)" };
        var acl     = new Option<bool>("--acl")     { Description = "Include ACL comparison" };
        var threads = new Option<int>("--threads")  { Description = "Parallel threads for enumeration (default: 8)" };

        var cmd = new Command("verify", "Compare source and destination trees and report differences");
        cmd.Add(src);
        cmd.Add(dst);
        cmd.Add(depth);
        cmd.Add(acl);
        cmd.Add(threads);

        cmd.SetAction(parseResult =>
        {
            var srcVal     = parseResult.GetValue(src)!;
            var dstVal     = parseResult.GetValue(dst)!;
            var depthVal   = parseResult.GetValue(depth) ?? "standard";
            var aclVal     = parseResult.GetValue(acl);
            var threadsVal = parseResult.GetValue(threads) is int t && t > 0 ? t : 8;
            CliOutput.Write(new
            {
                verb   = "verify",
                status = "not_implemented",
                args   = new { src = srcVal, dst = dstVal, depth = depthVal, acl = aclVal, threads = threadsVal }
            });
        });

        return cmd;
    }
}

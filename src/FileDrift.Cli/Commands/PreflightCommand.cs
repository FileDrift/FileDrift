using System.CommandLine;
using FileDrift.Core.Models;

namespace FileDrift.Cli.Commands;

internal static class PreflightCommand
{
    internal static Command Build()
    {
        var src = new Option<string>("--src") { Description = "Source path (local or UNC)", Required = true };
        var dst = new Option<string>("--dst") { Description = "Destination path (local or UNC)", Required = true };
        var credSrc = new Option<string>("--cred-source") { Description = "Saved credential target name for the source share" };
        var credDst = new Option<string>("--cred-dest") { Description = "Saved credential target name for the destination share" };

        var cmd = new Command("preflight", "Check that source and destination are accessible and report file/byte counts");
        cmd.Add(src);
        cmd.Add(dst);
        cmd.Add(credSrc);
        cmd.Add(credDst);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var srcVal = parseResult.GetValue(src)!;
            var dstVal = parseResult.GetValue(dst)!;

            try
            {
                var sourceCred = CliServices.ResolveCredentialForPath(parseResult.GetValue(credSrc), srcVal);
                var destCred = CliServices.ResolveCredentialForPath(parseResult.GetValue(credDst), dstVal);

                var result = await CliServices.Preflight().RunAsync(
                    srcVal, dstVal, new VerifyOptions(), sourceCred, destCred, progress: null, ct);

                CliOutput.Write(new
                {
                    verb = "preflight",
                    status = result.IsReady ? "ready" : "blocked",
                    checkedAtUtc = result.CheckedAtUtc,
                    source = result.SourcePath,
                    destination = result.DestPath,
                    sourceAccessible = result.SourceAccessible,
                    destAccessible = result.DestAccessible,
                    sourceFiles = result.SourceFileCount,
                    destFiles = result.DestFileCount,
                    sourceBytes = result.SourceTotalBytes,
                    destBytes = result.DestTotalBytes,
                    issues = result.Issues,
                });
                return result.IsReady ? 0 : 1;
            }
            catch (Exception ex)
            {
                return CliOutput.Error("preflight", ex.Message, ex.GetType().Name);
            }
        });

        return cmd;
    }
}

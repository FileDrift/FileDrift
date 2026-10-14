// SPDX-License-Identifier: GPL-3.0-or-later
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
        var checkWrite = new Option<bool>("--check-write")
        {
            Description = "Test that the running account can actually WRITE to the destination, by " +
                          "creating a tiny temporary file and deleting it. Off by default because it " +
                          "writes; without it, write access is reported as untested rather than fine.",
        };
        var credDst = new Option<string>("--cred-dest") { Description = "Saved credential target name for the destination share" };

        var cmd = new Command("preflight", "Check that source and destination are accessible and report file/byte counts");
        cmd.Add(src);
        cmd.Add(dst);
        cmd.Add(credSrc);
        cmd.Add(credDst);
        cmd.Add(checkWrite);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var srcVal = parseResult.GetValue(src)!;
            var dstVal = parseResult.GetValue(dst)!;

            try
            {
                var sourceCred = CliServices.ResolveCredentialForPath(parseResult.GetValue(credSrc), srcVal);
                var destCred = CliServices.ResolveCredentialForPath(parseResult.GetValue(credDst), dstVal);

                var wantWrite = parseResult.GetValue(checkWrite);
                // Only built when the probe is actually wanted: constructing a cloud target signs in
                // and resolves the library, which a read-only preflight has no reason to do.
                var destTarget = wantWrite ? await CliServices.CloudTargetAsync(dstVal, ct) : null;

                var result = await CliServices.Preflight().RunAsync(
                    srcVal, dstVal, new VerifyOptions(), sourceCred, destCred, progress: null, ct,
                    destinationTarget: destTarget, checkWrite: wantWrite);

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
                    writeAccess = new
                    {
                        tested = result.WriteProbe.Attempted,
                        canWrite = result.WriteProbe.Succeeded,
                        probeRemoved = result.WriteProbe.CleanedUp,
                        probePath = result.WriteProbe.ProbePath,
                        error = result.WriteProbe.Error,
                    },
                    issues = result.Issues,
                    // Null-omitted for a local destination, so a normal preflight is unchanged.
                    migration = result.Migration is not { } m ? null : new
                    {
                        filesAssessed = m.FilesAssessed,
                        willBeRenamed = m.RenameCount,
                        cannotMigrate = m.BlockedCount,
                        cannotMigrateBytes = m.BlockedBytes,
                        nameCollisions = m.Collisions.Count,
                        renameSamples = m.RenameSamples
                            .Select(r => new { from = r.RelativePath, to = r.SanitizedPath }).ToList(),
                        blockedSamples = m.BlockedSamples
                            .Select(b => new { path = b.RelativePath, reasons = b.Blockers }).ToList(),
                        collisionSamples = m.Collisions
                            .Select(c => new { first = c.First, second = c.Second, becomes = c.Sanitized }).ToList(),
                    },
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

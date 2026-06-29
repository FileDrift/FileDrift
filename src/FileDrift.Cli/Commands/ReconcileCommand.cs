using System.CommandLine;
using FileDrift.Core.Engine;
using FileDrift.Core.Models;

namespace FileDrift.Cli.Commands;

internal static class ReconcileCommand
{
    internal static Command Build()
    {
        var src     = new Option<string>("--src") { Description = "Source path (local or UNC)", Required = true };
        var dst     = new Option<string>("--dst") { Description = "Destination path (local or UNC). The destination credential needs write access.", Required = true };
        var depth   = new Option<string>("--depth") { Description = "Verify depth used to decide what differs: quick | standard | full (default: standard)" };
        var hash    = new Option<string>("--hash")  { Description = "md5 | sha1 | sha256 (default: md5, full depth only)" };
        var acl     = new Option<bool>("--acl")     { Description = "Also reconcile explicit (non-inherited) permissions — additive; never strips destination perms" };
        var ownAcl  = new Option<bool>("--enforce-ownership") { Description = "With --acl, also set the source owner where it differs" };
        var aclDirs = new Option<bool>("--acl-folders-only") { Description = "With --acl, compare/apply folder permissions only (fast)" };
        var threads = new Option<int>("--threads")  { Description = "Parallel threads for the verify pass" };
        var credSrc = new Option<string>("--cred-source") { Description = "Saved credential target name for the source share" };
        var credDst = new Option<string>("--cred-dest")   { Description = "Saved credential target name for the destination share (needs write access)" };
        var exclude = new Option<string>("--exclude") { Description = "Comma-separated glob patterns to exclude (e.g. \"*.tmp,~$*\")" };
        var strict  = new Option<bool>("--strict")  { Description = "Exact match for the verify pass (forces full depth, SHA-256, ACLs, zero timestamp tolerance)" };
        var start   = new Option<string>("--start") { Description = "Lower bound on last-modified (yyyy-MM-dd)" };
        var end     = new Option<string>("--end")   { Description = "Upper bound on last-modified (yyyy-MM-dd)" };
        var yes     = new Option<bool>("--yes")     { Description = "Apply the changes. Without this flag, reconcile only PREVIEWS the plan and writes nothing." };

        var cmd = new Command("reconcile",
            "Copy source→destination to fix missing/different files (and optionally permissions). " +
            "Non-destructive: never deletes destination-only files; permissions are only added. " +
            "Previews the plan unless --yes is given.");
        foreach (var o in new Option[] { src, dst, depth, hash, acl, ownAcl, aclDirs, threads, credSrc, credDst, exclude, strict, start, end, yes })
            cmd.Add(o);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var srcVal = parseResult.GetValue(src)!;
            var dstVal = parseResult.GetValue(dst)!;

            try
            {
                var options = new VerifyOptions
                {
                    Depth = VerifyCommand.ParseDepth(parseResult.GetValue(depth)),
                    HashAlgorithm = VerifyCommand.ParseHash(parseResult.GetValue(hash)),
                    IncludeAcl = parseResult.GetValue(acl),
                    EnforceOwnership = parseResult.GetValue(ownAcl),
                    AclScope = parseResult.GetValue(aclDirs) ? AclScope.FoldersOnly : AclScope.FilesAndFolders,
                    Threads = parseResult.GetValue(threads) is int t && t > 0 ? t : VerifyOptions.DefaultThreads,
                    ExcludePatterns = (parseResult.GetValue(exclude) ?? "")
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    Strict = parseResult.GetValue(strict),
                    StartUtc = VerifyCommand.ParseDate(parseResult.GetValue(start), startOfDay: true),
                    EndUtc = VerifyCommand.ParseDate(parseResult.GetValue(end), startOfDay: false),
                };

                var sourceCred = CliServices.ResolveCredentialForPath(parseResult.GetValue(credSrc), srcVal);
                var destCred = CliServices.ResolveCredentialForPath(parseResult.GetValue(credDst), dstVal);

                // 1) Verify to find what differs (this also enriches ACLs if --acl).
                var verify = await CliServices.Verify().RunAsync(srcVal, dstVal, options, sourceCred, destCred, progress: null, ct);

                // 2) Build the plan from the comparison (effective options reflect any --strict overrides).
                var effective = verify.Run.Options;
                var plan = ReconcileEngine.BuildPlan(verify.Comparisons, dstVal, effective.IncludeAcl, effective.EnforceOwnership);

                var planSummary = new
                {
                    copy = plan.CopyCount,
                    overwrite = plan.OverwriteCount,
                    createFolders = plan.DirCreateCount,
                    aclChanges = plan.AclCount,
                    newerAtDest = plan.ClobberCount,
                    totalBytes = plan.TotalBytes,
                };

                if (plan.TotalActions == 0)
                {
                    CliOutput.Write(new { verb = "reconcile", status = "nothing-to-do", applied = false, source = srcVal, destination = dstVal, plan = planSummary });
                    return 0;
                }

                // 3) Default is a safe preview; only --yes writes anything.
                if (!parseResult.GetValue(yes))
                {
                    CliOutput.Write(new
                    {
                        verb = "reconcile", status = "preview", applied = false,
                        message = "Pass --yes to apply these changes.",
                        source = srcVal, destination = dstVal, plan = planSummary,
                    });
                    return 0;
                }

                // 4) Apply. A Ctrl-C (ct) hard-cancels: aborts the in-flight file and removes its partial.
                var result = await CliServices.Reconcile().ExecuteAsync(
                    plan, srcVal, dstVal, sourceCred, destCred, progress: null, hardCancel: ct);

                CliOutput.Write(new
                {
                    verb = "reconcile",
                    status = result.Stopped ? "stopped" : "done",
                    applied = true,
                    source = srcVal, destination = dstVal,
                    plan = planSummary,
                    result = new
                    {
                        copied = result.Copied,
                        overwritten = result.Overwritten,
                        bytesCopied = result.BytesCopied,
                        foldersCreated = result.DirectoriesCreated,
                        aclsApplied = result.AclsApplied,
                        partialsRemoved = result.PartialsRemoved,
                        failed = result.FailureCount,
                    },
                    failures = result.Failures.Select(f => new { path = f.RelativePath, error = f.Error }),
                });

                return result.FailureCount > 0 ? 1 : 0;
            }
            catch (Exception ex)
            {
                return CliOutput.Error("reconcile", ex.Message, ex.GetType().Name);
            }
        });

        return cmd;
    }
}

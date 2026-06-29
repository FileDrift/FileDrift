// SPDX-License-Identifier: GPL-3.0-or-later
using System.CommandLine;
using FileDrift.Core.Models;

namespace FileDrift.Cli.Commands;

internal static class VerifyCommand
{
    internal static Command Build()
    {
        var src     = new Option<string>("--src")   { Description = "Source path (local or UNC)", Required = true };
        var dst     = new Option<string>("--dst")   { Description = "Destination path (local or UNC)", Required = true };
        var depth   = new Option<string>("--depth") { Description = "quick | standard | full (default: standard)" };
        var hash    = new Option<string>("--hash")  { Description = "md5 | sha1 | sha256 (default: md5, full depth only)" };
        var acl     = new Option<bool>("--acl")     { Description = "Compare explicit (non-inherited) permissions on files and folders" };
        var ownAcl  = new Option<bool>("--enforce-ownership") { Description = "With --acl, also require the owner to match" };
        var aclDirs = new Option<bool>("--acl-folders-only") { Description = "With --acl, compare folder permissions only (fast; skips file ACLs, misses file-level perms)" };
        var threads = new Option<int>("--threads")  { Description = "Parallel threads (default: 8)" };
        var credSrc = new Option<string>("--cred-source") { Description = "Saved credential target name for the source share" };
        var credDst = new Option<string>("--cred-dest")   { Description = "Saved credential target name for the destination share" };
        var exclude = new Option<string>("--exclude") { Description = "Comma-separated glob patterns to exclude (e.g. \"*.tmp,~$*\")" };
        var strict  = new Option<bool>("--strict")  { Description = "Exact match: forces full depth, SHA-256, ACLs, and zero timestamp tolerance" };
        var start   = new Option<string>("--start") { Description = "Lower bound on last-modified (yyyy-MM-dd). Ignores files modified before this on BOTH sides" };
        var end     = new Option<string>("--end")   { Description = "Upper bound on last-modified (yyyy-MM-dd). Excludes destination-only files modified after this" };
        var all     = new Option<bool>("--all")     { Description = "Include matched files in the differences array (default: differences only)" };

        var cmd = new Command("verify", "Compare source and destination trees and report differences");
        foreach (var o in new Option[] { src, dst, depth, hash, acl, ownAcl, aclDirs, threads, credSrc, credDst, exclude, strict, start, end, all })
            cmd.Add(o);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var srcVal = parseResult.GetValue(src)!;
            var dstVal = parseResult.GetValue(dst)!;

            try
            {
                var options = new VerifyOptions
                {
                    Depth = ParseDepth(parseResult.GetValue(depth)),
                    HashAlgorithm = ParseHash(parseResult.GetValue(hash)),
                    IncludeAcl = parseResult.GetValue(acl),
                    EnforceOwnership = parseResult.GetValue(ownAcl),
                    AclScope = parseResult.GetValue(aclDirs) ? AclScope.FoldersOnly : AclScope.FilesAndFolders,
                    Threads = parseResult.GetValue(threads) is int t && t > 0 ? t : VerifyOptions.DefaultThreads,
                    ExcludePatterns = (parseResult.GetValue(exclude) ?? "")
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    Strict = parseResult.GetValue(strict),
                    StartUtc = ParseDate(parseResult.GetValue(start), startOfDay: true),
                    EndUtc = ParseDate(parseResult.GetValue(end), startOfDay: false),
                };

                var sourceCred = CliServices.ResolveCredentialForPath(parseResult.GetValue(credSrc), srcVal);
                var destCred = CliServices.ResolveCredentialForPath(parseResult.GetValue(credDst), dstVal);

                var result = await CliServices.Verify().RunAsync(
                    srcVal, dstVal, options, sourceCred, destCred, progress: null, ct);

                bool includeMatched = parseResult.GetValue(all);
                var rows = result.Comparisons
                    .Where(c => includeMatched || c.Status != ComparisonStatus.Matched)
                    .Select(c => new
                    {
                        path = c.RelativePath,
                        status = c.Status,
                        differences = c.Differences == FileDifference.None ? null : c.Differences.ToString(),
                        sizeBytes = (c.Source ?? c.Dest)?.SizeBytes,
                    });

                var run = result.Run;
                CliOutput.Write(new
                {
                    verb = "verify",
                    status = run.Status,
                    runId = run.Id,
                    startedAtUtc = run.StartedAtUtc,
                    completedAtUtc = run.CompletedAtUtc,
                    source = run.SourcePath,
                    destination = run.DestPath,
                    options = new
                    {
                        depth = run.Options.Depth,
                        hashAlgorithm = run.Options.HashAlgorithm,
                        includeAcl = run.Options.IncludeAcl,
                        enforceOwnership = run.Options.EnforceOwnership,
                        aclScope = run.Options.AclScope,
                        threads = run.Options.Threads,
                        strict = run.Options.Strict,
                        startUtc = run.Options.StartUtc,
                        endUtc = run.Options.EndUtc,
                    },
                    summary = new
                    {
                        sourceFiles = run.TotalSourceFiles,
                        destFiles = run.TotalDestFiles,
                        matched = run.MatchedCount,
                        different = run.DifferentCount,
                        missingAtDest = run.MissingAtDestCount,
                        extraAtDest = run.ExtraAtDestCount,
                        excludedNewer = result.ExcludedNewerCount,
                    },
                    differences = rows,
                });

                return 0;
            }
            catch (Exception ex)
            {
                return CliOutput.Error("verify", ex.Message, ex.GetType().Name);
            }
        });

        return cmd;
    }

    internal static VerifyDepth ParseDepth(string? value) => value?.ToLowerInvariant() switch
    {
        "quick" => VerifyDepth.Quick,
        "full" => VerifyDepth.Full,
        null or "" or "standard" => VerifyDepth.Standard,
        _ => throw new ArgumentException($"Unknown depth '{value}'. Use quick, standard, or full."),
    };

    internal static DateTime? ParseDate(string? value, bool startOfDay)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!DateTime.TryParse(value, System.Globalization.CultureInfo.CurrentCulture,
                System.Globalization.DateTimeStyles.None, out var date))
            throw new ArgumentException($"Unknown date '{value}'. Use a format like 2026-02-06.");
        return startOfDay ? VerifyOptions.StartOfLocalDayUtc(date) : VerifyOptions.EndOfLocalDayUtc(date);
    }

    internal static FileDriftHashAlgorithm ParseHash(string? value) => value?.ToLowerInvariant() switch
    {
        "sha1" => FileDriftHashAlgorithm.SHA1,
        "sha256" => FileDriftHashAlgorithm.SHA256,
        null or "" or "md5" => FileDriftHashAlgorithm.MD5,
        _ => throw new ArgumentException($"Unknown hash '{value}'. Use md5, sha1, or sha256."),
    };
}

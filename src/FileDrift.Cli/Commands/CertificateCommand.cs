// SPDX-License-Identifier: GPL-3.0-or-later
using System.CommandLine;
using FileDrift.Core;
using FileDrift.Core.Reporting;

namespace FileDrift.Cli.Commands;

internal static class CertificateCommand
{
    internal static Command Build()
    {
        var id = new Option<string>("--id") { Description = "Run ID to certify" };
        var verify = new Option<string>("--verify") { Description = "Path to a certificate file to re-check" };
        var outOpt = new Option<string>("--out") { Description = "Output file or directory (default: current directory)" };

        var cmd = new Command("certificate", "Produce or re-check an HTML certificate of verification for a run");
        cmd.Add(id);
        cmd.Add(verify);
        cmd.Add(outOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var verifyPath = parseResult.GetValue(verify);
            if (!string.IsNullOrWhiteSpace(verifyPath))
                return await VerifyAsync(verifyPath, ct);

            var idVal = parseResult.GetValue(id);
            if (string.IsNullOrWhiteSpace(idVal))
                return CliOutput.Error("certificate",
                    "Provide --id <run-id> to generate a certificate, or --verify <file> to re-check one.");
            if (!Guid.TryParse(idVal, out var runId))
                return CliOutput.Error("certificate", $"'{idVal}' is not a valid run ID (GUID).");

            try
            {
                var run = await CliServices.Repository().GetAsync(runId, ct);
                if (run is null)
                    return CliOutput.Error("certificate", $"No run found with ID {runId}.");

                var cert = CompletionCertificate.Generate(run, AppInfo.Version, DateTime.UtcNow);
                var path = Path.GetFullPath(ResolveOutPath(parseResult.GetValue(outOpt), runId));
                await File.WriteAllTextAsync(path, cert.Html, ct);

                CliOutput.Write(new
                {
                    verb = "certificate",
                    action = "generated",
                    runId,
                    file = path,
                    fingerprint = cert.Fingerprint,
                    signedOff = run.SignedOffAtUtc is not null,
                });
                return 0;
            }
            catch (Exception ex)
            {
                return CliOutput.Error("certificate", ex.Message, ex.GetType().Name);
            }
        });

        return cmd;
    }

    private static async Task<int> VerifyAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return CliOutput.Error("certificate", $"File not found: {path}");

        try
        {
            var html = await File.ReadAllTextAsync(path, ct);
            var result = CompletionCertificate.Verify(html);
            if (!result.Parsed)
                return CliOutput.Error("certificate",
                    "This file is not a FileDrift certificate (no embedded canonical block found).");

            // If the run still exists locally, also confirm the certificate matches the system of record.
            bool? matchesDatabase = null;
            if (result.RunId is Guid rid && result.Canonical is not null)
            {
                var run = await CliServices.Repository().GetAsync(rid, ct);
                if (run is not null)
                {
                    var appVer = CompletionCertificate.CanonicalField(result.Canonical, "appVersion") ?? AppInfo.Version;
                    matchesDatabase = string.Equals(
                        CompletionCertificate.BuildCanonical(run, appVer), result.Canonical, StringComparison.Ordinal);
                }
            }

            CliOutput.Write(new
            {
                verb = "certificate",
                action = "verify",
                intact = result.Intact,
                runId = result.RunId,
                embeddedFingerprint = result.EmbeddedFingerprint,
                recomputedFingerprint = result.RecomputedFingerprint,
                matchesDatabase,
            });
            return result.Intact ? 0 : 1;
        }
        catch (Exception ex)
        {
            return CliOutput.Error("certificate", ex.Message, ex.GetType().Name);
        }
    }

    /// <summary>Picks the output path: an explicit file, a file inside an explicit/existing directory, or a
    /// default <c>FileDrift-Certificate-{shortId}.html</c> in the current directory.</summary>
    private static string ResolveOutPath(string? outArg, Guid id)
    {
        var name = $"FileDrift-Certificate-{id.ToString()[..8]}.html";
        if (string.IsNullOrWhiteSpace(outArg)) return name;
        if (Directory.Exists(outArg)) return Path.Combine(outArg, name);
        return outArg;
    }
}

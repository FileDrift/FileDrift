// SPDX-License-Identifier: GPL-3.0-or-later
using System.CommandLine;
using FileDrift.Core;
using FileDrift.Core.Engine;
using FileDrift.Core.Reporting;

namespace FileDrift.Cli.Commands;

internal static class PermissionsCommand
{
    /// <summary>Printed in the output itself, not only in the docs. A permissions inventory sitting in
    /// a migration folder next to a signed certificate could easily be mistaken for evidence that
    /// permissions were verified; it is not, and the file has to say so on its face.</summary>
    private const string Disclaimer =
        "This is a REPORT of the source permissions at the time of the scan, to help recreate them " +
        "elsewhere. It is NOT an attestation: nothing about the destination was examined or compared. " +
        "FileDrift cannot compare permissions against a cloud destination, because cloud permission " +
        "models have no Windows ACL equivalent.";

    internal static Command Build()
    {
        var src = new Option<string>("--src") { Description = "Source path to inventory (local or UNC)." };
        var includeFiles = new Option<bool>("--include-files")
        {
            Description = "Also read permissions on files. Off by default: explicit permissions are " +
                          "normally set on folders, and reading every file's descriptor is far slower.",
        };
        var exclude = new Option<string>("--exclude") { Description = "Comma-separated glob patterns to skip." };

        var cmd = new Command("permissions",
            "Inventory the explicit (non-inherited) permissions on a source tree, grouped by distinct " +
            "permission set, so they can be recreated at a destination by hand. A report only -- " +
            "FileDrift never assigns permissions, and this is not an attestation.");
        foreach (var o in new Option[] { src, includeFiles, exclude }) cmd.Add(o);

        cmd.SetAction(async (parseResult, ct) =>
        {
            try
            {
                var srcVal = parseResult.GetValue(src);
                if (string.IsNullOrWhiteSpace(srcVal))
                    return CliOutput.Error("permissions", "--src is required.", "ArgumentException");

                // Windows ACLs do not exist on a cloud path; say so rather than returning an empty
                // report that looks like "no permissions are set".
                if (CloudPath.IsSharePoint(srcVal))
                    return CliOutput.Error("permissions",
                        "A SharePoint/OneDrive path has no Windows ACLs to inventory. Cloud permissions " +
                        "are a different model (role assignments and sharing links) and must be reviewed " +
                        "in SharePoint itself.", "NotSupportedException");

                var inventory = await PermissionsReport.BuildAsync(
                    CliServices.EnumeratorForPermissions(),
                    srcVal,
                    AppInfo.Version,
                    includeFiles: parseResult.GetValue(includeFiles),
                    cancellationToken: ct);

                CliOutput.Write(new
                {
                    verb = "permissions",
                    disclaimer = Disclaimer,
                    source = inventory.RootPath,
                    generatedAtUtc = inventory.GeneratedAtUtc,
                    appVersion = inventory.AppVersion,
                    includedFiles = inventory.IncludedFiles,
                    entriesScanned = inventory.EntriesScanned,
                    entriesWithExplicitPermissions = inventory.EntriesWithExplicitPermissions,
                    distinctPermissionSets = inventory.PermissionSets.Count,
                    unreadable = inventory.Unreadable,
                    permissionSets = inventory.PermissionSets.Select(p => new
                    {
                        p.Owner,
                        p.Permissions,
                        p.EntryCount,
                        p.SamplePaths,
                        p.Sddl,
                    }).ToList(),
                });

                return 0;
            }
            catch (Exception ex) { return CliOutput.Error("permissions", ex.Message, ex.GetType().Name); }
        });

        return cmd;
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using FileDrift.Core.Engine.Native;

namespace FileDrift.Core.Engine;

/// <summary>Applies permissions to a destination file/folder to match the source. Windows-only (advapi32).
/// ADDITIVE: explicit ACEs from the source are merged into the destination's existing explicit ACEs —
/// the destination's own permissions and inheritance are never removed (it is a live target).</summary>
public sealed class AclWriter
{
    private readonly AclReader _reader = new();

    /// <summary>Ensures every ACE in <paramref name="sourceExplicitAces"/> (ACE bodies like
    /// "A;;FR;;;WD") is present on <paramref name="path"/>, preserving the destination's existing
    /// explicit ACEs and its inheritance. Returns null on success / no-op, or an error message.</summary>
    public string? TryApplyExplicitAces(string path, IReadOnlyCollection<string> sourceExplicitAces)
    {
        if (sourceExplicitAces.Count == 0)
            return null;

        // Union with the destination's CURRENT explicit ACEs (re-read at apply time, not from stale verify data).
        var union = AclModel.ExplicitAces(_reader.TryGetSddl(path));
        bool added = false;
        foreach (var ace in sourceExplicitAces)
            if (union.Add(ace.ToUpperInvariant())) added = true;
        if (!added)
            return null; // every source ACE already present

        // Deny ACEs before allow (canonical-ish). Build an unprotected DACL so inherited ACEs still apply.
        var ordered = union.OrderBy(a => a.StartsWith("D;", StringComparison.OrdinalIgnoreCase) ? 0 : 1);
        var dacl = "D:" + string.Concat(ordered.Select(a => $"({a})"));

        if (!AclMethods.ConvertStringSecurityDescriptorToSecurityDescriptorW(
                dacl, AclMethods.SddlRevision1, out IntPtr pSd, out _))
            return $"parse DACL failed (win32 {Marshal.GetLastWin32Error()})";

        try
        {
            AclMethods.GetSecurityDescriptorDacl(pSd, out bool present, out IntPtr pDacl, out _);
            int err = AclMethods.SetNamedSecurityInfoW(
                path, AclMethods.SeFileObject,
                AclMethods.DaclSecurityInformation | AclMethods.UnprotectedDaclSecurityInformation,
                IntPtr.Zero, IntPtr.Zero, present ? pDacl : IntPtr.Zero, IntPtr.Zero);
            return err == AclMethods.ErrorSuccess ? null : $"set DACL failed (win32 {err})";
        }
        finally
        {
            AclMethods.LocalFree(pSd);
        }
    }

    /// <summary>Sets the owner of <paramref name="path"/> to <paramref name="ownerSid"/> (SDDL SID or alias).
    /// Needs SeRestorePrivilege. Returns null on success or an error message.</summary>
    public string? TrySetOwner(string path, string ownerSid)
    {
        if (string.IsNullOrEmpty(ownerSid))
            return null;

        if (!AclMethods.ConvertStringSecurityDescriptorToSecurityDescriptorW(
                "O:" + ownerSid, AclMethods.SddlRevision1, out IntPtr pSd, out _))
            return $"parse owner failed (win32 {Marshal.GetLastWin32Error()})";

        try
        {
            AclMethods.GetSecurityDescriptorOwner(pSd, out IntPtr pOwner, out _);
            int err = AclMethods.SetNamedSecurityInfoW(
                path, AclMethods.SeFileObject, AclMethods.OwnerSecurityInformation,
                pOwner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            return err == AclMethods.ErrorSuccess ? null : $"set owner failed (win32 {err})";
        }
        finally
        {
            AclMethods.LocalFree(pSd);
        }
    }
}

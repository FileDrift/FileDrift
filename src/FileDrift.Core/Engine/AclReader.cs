using System.Runtime.InteropServices;
using FileDrift.Core.Engine.Native;

namespace FileDrift.Core.Engine;

/// <summary>Reads a file's owner + DACL as an SDDL string for comparison.
/// Windows-only (uses advapi32); returns null on any failure.</summary>
public sealed class AclReader
{
    private const uint OwnerGroupDacl =
        AclMethods.OwnerSecurityInformation |
        AclMethods.GroupSecurityInformation |
        AclMethods.DaclSecurityInformation;

    /// <summary>Full SDDL (owner + group + DACL), or null if it cannot be read.</summary>
    public string? TryGetSddl(string path)
    {
        int err = AclMethods.GetNamedSecurityInfoW(
            path, AclMethods.SeFileObject, OwnerGroupDacl,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
            out IntPtr pSecurityDescriptor);

        if (err != AclMethods.ErrorSuccess || pSecurityDescriptor == IntPtr.Zero)
            return null;

        try
        {
            if (!AclMethods.ConvertSecurityDescriptorToStringSecurityDescriptorW(
                    pSecurityDescriptor, AclMethods.SddlRevision1, OwnerGroupDacl,
                    out IntPtr pString, out _))
                return null;

            try   { return Marshal.PtrToStringUni(pString); }
            finally { AclMethods.LocalFree(pString); }
        }
        finally
        {
            AclMethods.LocalFree(pSecurityDescriptor);
        }
    }

    /// <summary>Extracts the owner SID token (the "O:" component) from an SDDL string, or null.</summary>
    public static string? ExtractOwner(string? sddl)
    {
        if (string.IsNullOrEmpty(sddl)) return null;

        int oIndex = sddl.IndexOf("O:", StringComparison.Ordinal);
        if (oIndex < 0) return null;

        int start = oIndex + 2;
        int end = start;
        // Owner runs until the next section marker (G:/D:/S:).
        while (end < sddl.Length)
        {
            if (end + 1 < sddl.Length && sddl[end + 1] == ':' &&
                sddl[end] is 'G' or 'D' or 'S')
                break;
            end++;
        }

        var owner = sddl[start..end];
        return owner.Length > 0 ? owner : null;
    }
}

using System.Runtime.InteropServices;
using FileDrift.Core.Engine.Native;

namespace FileDrift.Core.Engine;

/// <summary>Applies an SDDL security descriptor (owner + group + DACL) to a file, to make a
/// destination's permissions match the source. Windows-only (advapi32). Owner/group are attempted
/// first and, if the process lacks the privilege, it falls back to setting just the DACL.</summary>
public sealed class AclWriter
{
    private const uint OwnerGroupDacl =
        AclMethods.OwnerSecurityInformation |
        AclMethods.GroupSecurityInformation |
        AclMethods.DaclSecurityInformation;

    /// <summary>Applies <paramref name="sddl"/> to <paramref name="path"/>. Returns null on success,
    /// or a short error message on failure.</summary>
    public string? TryApplySddl(string path, string sddl)
    {
        if (string.IsNullOrEmpty(sddl))
            return "no source SDDL";

        if (!AclMethods.ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl, AclMethods.SddlRevision1, out IntPtr pSd, out _))
            return $"parse SDDL failed (win32 {Marshal.GetLastWin32Error()})";

        try
        {
            AclMethods.GetSecurityDescriptorOwner(pSd, out IntPtr pOwner, out _);
            AclMethods.GetSecurityDescriptorGroup(pSd, out IntPtr pGroup, out _);
            AclMethods.GetSecurityDescriptorDacl(pSd, out bool daclPresent, out IntPtr pDacl, out _);
            IntPtr dacl = daclPresent ? pDacl : IntPtr.Zero;

            int err = AclMethods.SetNamedSecurityInfoW(
                path, AclMethods.SeFileObject, OwnerGroupDacl, pOwner, pGroup, dacl, IntPtr.Zero);

            // Setting owner/group needs SeRestorePrivilege; if denied, still copy the DACL.
            if (err == AclMethods.ErrorPrivilegeNotHeld)
                err = AclMethods.SetNamedSecurityInfoW(
                    path, AclMethods.SeFileObject, AclMethods.DaclSecurityInformation,
                    IntPtr.Zero, IntPtr.Zero, dacl, IntPtr.Zero);

            return err == AclMethods.ErrorSuccess ? null : $"set ACL failed (win32 {err})";
        }
        finally
        {
            AclMethods.LocalFree(pSd);
        }
    }
}

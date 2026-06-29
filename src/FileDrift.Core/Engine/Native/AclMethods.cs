// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace FileDrift.Core.Engine.Native;

/// <summary>P/Invoke for reading a file's security descriptor as an SDDL string.</summary>
internal static class AclMethods
{
    internal const int  SeFileObject = 1; // SE_OBJECT_TYPE.SE_FILE_OBJECT
    internal const int  SddlRevision1 = 1;

    // SECURITY_INFORMATION flags
    internal const uint OwnerSecurityInformation = 0x00000001;
    internal const uint GroupSecurityInformation = 0x00000002;
    internal const uint DaclSecurityInformation  = 0x00000004;
    internal const uint UnprotectedDaclSecurityInformation = 0x20000000; // allow inheritance to propagate

    internal const int  ErrorSuccess = 0;
    internal const int  ErrorPrivilegeNotHeld = 1314; // SetNamedSecurityInfo owner/group needs privilege

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    internal static extern int GetNamedSecurityInfoW(
        string pObjectName,
        int objectType,
        uint securityInfo,
        IntPtr ppsidOwner,
        IntPtr ppsidGroup,
        IntPtr ppDacl,
        IntPtr ppSacl,
        out IntPtr ppSecurityDescriptor);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(
        IntPtr securityDescriptor,
        int requestedStringSdRevision,
        uint securityInformation,
        out IntPtr stringSecurityDescriptor,
        out int stringSecurityDescriptorLen);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr LocalFree(IntPtr hMem);

    // ── writing ──

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string stringSecurityDescriptor,
        int stringSdRevision,
        out IntPtr securityDescriptor,
        out int securityDescriptorSize);

    [DllImport("advapi32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSecurityDescriptorOwner(
        IntPtr securityDescriptor, out IntPtr owner, [MarshalAs(UnmanagedType.Bool)] out bool ownerDefaulted);

    [DllImport("advapi32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSecurityDescriptorGroup(
        IntPtr securityDescriptor, out IntPtr group, [MarshalAs(UnmanagedType.Bool)] out bool groupDefaulted);

    [DllImport("advapi32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSecurityDescriptorDacl(
        IntPtr securityDescriptor, [MarshalAs(UnmanagedType.Bool)] out bool daclPresent,
        out IntPtr dacl, [MarshalAs(UnmanagedType.Bool)] out bool daclDefaulted);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ConvertStringSidToSidW(string stringSid, out IntPtr sid);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool LookupAccountSidW(
        string? systemName, IntPtr sid,
        System.Text.StringBuilder? name, ref uint cchName,
        System.Text.StringBuilder? referencedDomainName, ref uint cchReferencedDomainName,
        out int peUse);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    internal static extern int SetNamedSecurityInfoW(
        string pObjectName,
        int objectType,
        uint securityInfo,
        IntPtr psidOwner,
        IntPtr psidGroup,
        IntPtr pDacl,
        IntPtr pSacl);
}

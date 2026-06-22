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

    internal const int  ErrorSuccess = 0;

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
}

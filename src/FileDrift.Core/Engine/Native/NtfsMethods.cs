// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FileDrift.Core.Engine.Native;

internal static class NtfsMethods
{
    internal const uint GenericRead        = 0x80000000;
    internal const uint FileShareRead      = 0x00000001;
    internal const uint FileShareWrite     = 0x00000002;
    internal const uint OpenExisting       = 3;
    internal const uint FileFlagBackupSemantics = 0x02000000;

    internal const uint FsctlEnumUsnData   = 0x000900B3;
    internal const int  ErrorHandleEof     = 38;
    internal const int  ErrorAccessDenied  = 5;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref MftEnumDataV0 lpInBuffer,
        int nInBufferSize,
        byte[] lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool GetFileAttributesExW(
        string lpFileName,
        int fInfoLevelId,     // 0 = GetFileExInfoStandard
        out Win32FileAttributeData lpFileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);
}

using System.Runtime.InteropServices;

namespace FileDrift.Core.Engine.Native;

internal static class CredentialMethods
{
    internal const uint CredTypeGeneric        = 1;
    internal const uint CredPersistLocalMachine = 2;
    internal const int  ErrorNotFound          = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;          // LPWSTR
        public IntPtr Comment;             // LPWSTR
        public long LastWritten;           // FILETIME
        public uint CredentialBlobSize;    // bytes
        public IntPtr CredentialBlob;      // LPBYTE
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;         // LPWSTR
        public IntPtr UserName;            // LPWSTR
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CredRead(string target, uint type, uint flags, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CredEnumerate(string? filter, uint flags, out uint count, out IntPtr credentialsPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    internal static extern void CredFree(IntPtr buffer);
}

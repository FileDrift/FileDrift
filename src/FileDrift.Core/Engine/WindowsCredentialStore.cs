using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using FileDrift.Core.Engine.Native;
using FileDrift.Core.Interfaces;

namespace FileDrift.Core.Engine;

/// <summary>Stores credentials in Windows Credential Manager as generic credentials.
/// Targets are visible/editable under Control Panel → Credential Manager.</summary>
public sealed class WindowsCredentialStore : ICredentialStore
{
    public NetworkCredential? GetCredential(string targetName)
    {
        if (!CredentialMethods.CredRead(targetName, CredentialMethods.CredTypeGeneric, 0, out IntPtr ptr))
        {
            int err = Marshal.GetLastWin32Error();
            if (err == CredentialMethods.ErrorNotFound) return null;
            throw new Win32Exception(err, $"CredRead failed for '{targetName}'.");
        }

        try
        {
            var cred = Marshal.PtrToStructure<CredentialMethods.Credential>(ptr);
            string user = cred.UserName != IntPtr.Zero ? Marshal.PtrToStringUni(cred.UserName) ?? "" : "";
            string password = "";
            if (cred.CredentialBlob != IntPtr.Zero && cred.CredentialBlobSize > 0)
                password = Marshal.PtrToStringUni(cred.CredentialBlob, (int)(cred.CredentialBlobSize / 2));
            return new NetworkCredential(user, password);
        }
        finally
        {
            CredentialMethods.CredFree(ptr);
        }
    }

    public void SetCredential(string targetName, NetworkCredential credential)
    {
        string user = string.IsNullOrEmpty(credential.Domain)
            ? credential.UserName
            : $"{credential.Domain}\\{credential.UserName}";

        byte[] blob = Encoding.Unicode.GetBytes(credential.Password ?? "");

        IntPtr targetPtr = Marshal.StringToCoTaskMemUni(targetName);
        IntPtr userPtr = Marshal.StringToCoTaskMemUni(user);
        IntPtr blobPtr = blob.Length > 0 ? Marshal.AllocCoTaskMem(blob.Length) : IntPtr.Zero;
        if (blobPtr != IntPtr.Zero) Marshal.Copy(blob, 0, blobPtr, blob.Length);

        try
        {
            var cred = new CredentialMethods.Credential
            {
                Type = CredentialMethods.CredTypeGeneric,
                TargetName = targetPtr,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = CredentialMethods.CredPersistLocalMachine,
                UserName = userPtr,
            };

            if (!CredentialMethods.CredWrite(ref cred, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"CredWrite failed for '{targetName}'.");
        }
        finally
        {
            Marshal.FreeCoTaskMem(targetPtr);
            Marshal.FreeCoTaskMem(userPtr);
            if (blobPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(blobPtr);
        }
    }

    public bool DeleteCredential(string targetName)
    {
        if (CredentialMethods.CredDelete(targetName, CredentialMethods.CredTypeGeneric, 0))
            return true;

        int err = Marshal.GetLastWin32Error();
        if (err == CredentialMethods.ErrorNotFound) return false;
        throw new Win32Exception(err, $"CredDelete failed for '{targetName}'.");
    }

    public IEnumerable<string> ListTargets()
    {
        if (!CredentialMethods.CredEnumerate("FileDrift:*", 0, out uint count, out IntPtr arrayPtr))
        {
            int err = Marshal.GetLastWin32Error();
            if (err == CredentialMethods.ErrorNotFound) return [];
            throw new Win32Exception(err, "CredEnumerate failed.");
        }

        try
        {
            var targets = new List<string>((int)count);
            for (int i = 0; i < count; i++)
            {
                IntPtr credPtr = Marshal.ReadIntPtr(arrayPtr, i * IntPtr.Size);
                var cred = Marshal.PtrToStructure<CredentialMethods.Credential>(credPtr);
                if (cred.TargetName != IntPtr.Zero)
                {
                    var name = Marshal.PtrToStringUni(cred.TargetName);
                    if (!string.IsNullOrEmpty(name)) targets.Add(name);
                }
            }
            return targets;
        }
        finally
        {
            CredentialMethods.CredFree(arrayPtr);
        }
    }
}

using System.ComponentModel;
using System.Net;
using FileDrift.Core.Engine.Native;

namespace FileDrift.Core.Engine;

/// <summary>Establishes an authenticated SMB session to a UNC share root using the given credentials,
/// and tears it down on dispose. Lets enumeration of <c>\\server\share</c> use alternate credentials
/// without mapping a drive letter.</summary>
public sealed class NetworkConnection : IDisposable
{
    private readonly string _shareRoot;
    private bool _connected;

    public NetworkConnection(string shareRoot, NetworkCredential credential)
    {
        _shareRoot = shareRoot;

        var resource = new NetworkMethods.NetResource
        {
            Type = NetworkMethods.ResourceTypeDisk,
            RemoteName = shareRoot,
        };

        string user = string.IsNullOrEmpty(credential.Domain)
            ? credential.UserName
            : $"{credential.Domain}\\{credential.UserName}";

        int result = NetworkMethods.WNetAddConnection2(ref resource, credential.Password, user, 0);
        if (result == NetworkMethods.NoError)
        {
            _connected = true; // we own this session; tear it down on dispose
        }
        else if (result == NetworkMethods.ErrorSessionCredentialConflict)
        {
            // A Kerberos/domain session to this server already exists.  That session will
            // serve the enumeration — no need to establish a new one or cancel on dispose.
            _connected = false;
        }
        else
        {
            throw new Win32Exception(result, $"Could not connect to {shareRoot} as '{user}'.");
        }
    }

    public void Dispose()
    {
        if (!_connected) return;
        _connected = false;
        // Force teardown even if handles linger; ignore the result during cleanup.
        NetworkMethods.WNetCancelConnection2(_shareRoot, 0, true);
    }
}

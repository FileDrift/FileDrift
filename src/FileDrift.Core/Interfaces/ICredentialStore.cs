using System.Net;

namespace FileDrift.Core.Interfaces;

/// <summary>
/// Stores and retrieves network credentials keyed by target name.
/// Target name convention: "FileDrift:\\server\share"
/// </summary>
public interface ICredentialStore
{
    /// <summary>Returns the stored credential for <paramref name="targetName"/>, or null if none is stored.</summary>
    NetworkCredential? GetCredential(string targetName);

    /// <summary>Persists a credential. Overwrites any existing entry for the same target.</summary>
    void SetCredential(string targetName, NetworkCredential credential);

    /// <summary>Removes the stored credential. Returns true if one existed; false if there was nothing to delete.</summary>
    bool DeleteCredential(string targetName);

    /// <summary>Returns all target names currently stored by FileDrift.</summary>
    IEnumerable<string> ListTargets();
}

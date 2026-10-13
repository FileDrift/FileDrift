// SPDX-License-Identifier: GPL-3.0-or-later
namespace FileDrift.Core.Cloud;

/// <summary>Graph authentication failed in a way the user can act on. The message is written to be
/// shown directly — it names the likely cause and the fix rather than echoing an AADSTS code.</summary>
public class GraphAuthException : Exception
{
    public GraphAuthException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>No cached credential could be refreshed, so an interactive sign-in is required. Thrown
/// instead of silently opening a browser, so unattended runs fail fast and predictably.</summary>
public sealed class GraphSignInRequiredException : GraphAuthException
{
    public GraphSignInRequiredException(string? detail = null)
        : base("Not signed in to Microsoft Graph (no cached credential could be refreshed). " +
               "Run \"FileDrift-CLI graph login\" to sign in." +
               (string.IsNullOrWhiteSpace(detail) ? "" : $" Detail: {detail}")) { }
}

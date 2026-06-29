// SPDX-License-Identifier: GPL-3.0-or-later
namespace FileDrift.Core;

/// <summary>The Windows account running the tool, used to stamp who performed a sign-off.</summary>
public static class OperatorIdentity
{
    /// <summary>The current account as <c>DOMAIN\user</c> (or <c>MACHINE\user</c> on a workgroup box).
    /// Falls back to the bare user name if the domain/machine name is unavailable.</summary>
    public static string Current
    {
        get
        {
            var user = Environment.UserName;
            var domain = Environment.UserDomainName;
            return string.IsNullOrEmpty(domain) ? user : $@"{domain}\{user}";
        }
    }
}

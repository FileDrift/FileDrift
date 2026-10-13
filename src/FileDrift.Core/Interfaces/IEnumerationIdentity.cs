// SPDX-License-Identifier: GPL-3.0-or-later
namespace FileDrift.Core.Interfaces;

/// <summary>Implemented by an enumerator whose access depends on an authenticated identity, so a run
/// can record WHO it read as.
/// <para>A separate interface rather than a member on <see cref="IFileEnumerator"/>: a local or SMB
/// scan has no such identity beyond the process token, and adding a property every implementation
/// must answer would be noise. Callers pattern-match for it.</para></summary>
public interface IEnumerationIdentity
{
    /// <summary>The identity the most recent enumeration ran as (e.g. a Graph UPN), or null if none
    /// applies or nothing has been enumerated yet.</summary>
    string? EnumerationIdentity { get; }
}

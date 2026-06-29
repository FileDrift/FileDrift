// SPDX-License-Identifier: GPL-3.0-or-later
using FileDrift.Core.Models;

namespace FileDrift.Core.Interfaces;

/// <summary>Persists and queries run history.</summary>
public interface IRunRepository
{
    /// <summary>
    /// Insert or update a run record. Called once to create the record at run start,
    /// then again (or more) to update counts and status as the run progresses.
    /// </summary>
    Task SaveAsync(RunRecord run, CancellationToken cancellationToken = default);

    /// <summary>Returns the run with the given ID, or null if not found.</summary>
    Task<RunRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns runs matching the query, ordered by StartedAtUtc descending.</summary>
    Task<IReadOnlyList<RunRecord>> ListAsync(
        RunQueryOptions? query = null,
        CancellationToken cancellationToken = default);

    /// <summary>Stamps a run as signed off: records the sign-off time (UTC now), the accountable party,
    /// the Windows account that performed it, and an optional note. Only the sign-off fields are touched.
    /// Returns false if no run with that ID exists.</summary>
    Task<bool> MarkSignedOffAsync(
        Guid id, string signedOffBy, string signedOffByAccount, string? note,
        CancellationToken cancellationToken = default);

    /// <summary>Permanently deletes a run record. Returns true if it existed.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

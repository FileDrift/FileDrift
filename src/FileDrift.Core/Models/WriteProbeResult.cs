// SPDX-License-Identifier: GPL-3.0-or-later
namespace FileDrift.Core.Models;

/// <summary>
/// Outcome of testing whether the running account can actually write to the destination.
/// </summary>
/// <remarks>
/// <para><b>Why this writes.</b> Reported permissions can lie — an inherited deny, a retention or
/// hold policy, a library requiring check-out, a read-only mount, a quota already at its limit. None
/// of those show up in a permissions query, and all of them stop a migration partway through. The only
/// answer that means anything is to attempt a real write, so this creates a tiny uniquely-named file
/// and removes it again.</para>
/// <para><b>Why it is opt-in.</b> A step called "preflight" that writes to a destination unasked would
/// be a trap for whoever runs it next. When the probe is skipped, the result says so rather than
/// leaving a reader to assume write access was confirmed.</para>
/// </remarks>
public sealed class WriteProbeResult
{
    /// <summary>False when the probe was not requested — in which case nothing here says anything
    /// about whether the destination is writable.</summary>
    public required bool Attempted { get; init; }

    /// <summary>True only if a real file was written. Null when not attempted.</summary>
    public bool? Succeeded { get; init; }

    /// <summary>Whether the probe file was removed again. False means a stray file was LEFT BEHIND at
    /// <see cref="ProbePath"/> and someone has to delete it — reported rather than hidden, because a
    /// tool that litters quietly is worse than one that admits it.</summary>
    public bool? CleanedUp { get; init; }

    /// <summary>Relative path the probe used, so a failed cleanup can be found by hand.</summary>
    public string? ProbePath { get; init; }

    /// <summary>Why the write failed, when it did.</summary>
    public string? Error { get; init; }

    public static WriteProbeResult NotAttempted() => new() { Attempted = false };
}

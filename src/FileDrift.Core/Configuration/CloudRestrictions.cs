// SPDX-License-Identifier: GPL-3.0-or-later
namespace FileDrift.Core.Configuration;

/// <summary>A cloud target that requires an explicit opt-in, and the reason it does.</summary>
public sealed record CloudRestriction(string Name, string Reason);

/// <summary>
/// Provider-neutral policy for which cloud targets require a deliberate opt-in before FileDrift will
/// talk to them. Microsoft Graph clouds use it today; AWS regions and other S3-compatible endpoints
/// are expected to use the same mechanism when those targets land, which is why this lives here
/// rather than inside any one provider's configuration.
/// </summary>
/// <remarks>
/// <b>What this is, and is not.</b> It is a guardrail against reaching a foreign-operated cloud by
/// accident — a mistyped region, a copied-and-pasted endpoint — and nothing more. It is deliberately
/// NOT described as sanctions screening or export-control compliance, because it cannot be either:
/// <list type="bullet">
/// <item>Cloud providers do not operate regions in OFAC-sanctioned countries, so a "sanctions list"
/// would be a list that never matches, while implying screening FileDrift does not perform. In a
/// tool people rely on for attestation, that false assurance is worse than no check at all.</item>
/// <item>An endpoint says nothing about <i>data residency</i> — content in a commercial-cloud tenant
/// can live in any region the provider offers.</item>
/// <item>Explicit endpoint overrides bypass this entirely, by design. It stops accidents, not intent.</item>
/// </list>
/// The encoded rule is narrow and checkable: <i>a cloud operated by a non-Western entity under
/// non-US/EU jurisdiction requires a deliberate opt-in</i>. Reasons are stated as verifiable facts
/// about the operator, so entries stay factual rather than becoming political assertions this
/// project has no standing to make.
/// </remarks>
public static class CloudRestrictions
{
    /// <summary>Microsoft's China cloud, operated by 21Vianet.</summary>
    public const string MicrosoftChina = "China";

    /// <summary>Restriction on a Microsoft Graph national cloud, or null if unrestricted.</summary>
    public static CloudRestriction? ForMicrosoftCloud(string? cloud) =>
        Matches(cloud, MicrosoftChina)
            ? new CloudRestriction(MicrosoftChina,
                "it is operated by 21Vianet under PRC jurisdiction rather than by Microsoft, so " +
                "content and access are subject to that jurisdiction.")
            : null;

    // When S3/AWS support lands, its regions plug in here — the AWS China regions (cn-north-1,
    // operated by Sinnet, and cn-northwest-1, operated by NWCD) are the known cases, and they are
    // separate partitions requiring separate credentials, so they cannot be reached by accident from
    // a standard account. Left unimplemented rather than guessed at: adding a region list before the
    // provider exists would be speculative, and a stale list is worse than none.

    private static bool Matches(string? value, string name) =>
        string.Equals(value?.Trim(), name, StringComparison.OrdinalIgnoreCase);
}

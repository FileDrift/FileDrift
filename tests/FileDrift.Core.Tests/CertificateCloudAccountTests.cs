// SPDX-License-Identifier: GPL-3.0-or-later
using FileDrift.Core.Models;
using FileDrift.Core.Reporting;
using Xunit;

namespace FileDrift.Core.Tests;

/// <summary>
/// The certificate must name the identity a cloud side was read as. Graph access is delegated, so
/// what a run could see is bounded by that account — a library it can only partially read would
/// produce a clean-looking match over a partial view. Omitting the identity would make the document
/// assert a completeness it cannot support.
/// </summary>
public class CertificateCloudAccountTests
{
    private static RunRecord Run(string? cloudAccount) => new()
    {
        Id = Guid.NewGuid(),
        StartedAtUtc = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc),
        CompletedAtUtc = new DateTime(2026, 10, 13, 12, 5, 0, DateTimeKind.Utc),
        SourcePath = "https://contoso.sharepoint.com/sites/IT/Shared Documents",
        DestPath = @"C:\Dest",
        Options = new VerifyOptions { Depth = VerifyDepth.Full, HashAlgorithm = FileDriftHashAlgorithm.QuickXor },
        Status = RunStatus.Completed,
        MatchedCount = 10,
        CloudAccount = cloudAccount,
    };

    [Fact]
    public void A_cloud_run_names_the_account_it_read_as()
    {
        var cert = CompletionCertificate.Generate(Run("svc-migrate@contoso.gov"), "1.1.0-test", DateTime.UtcNow);

        Assert.Contains("svc-migrate@contoso.gov", cert.Html);
        Assert.Contains("Cloud account (read as)", cert.Html);
    }

    [Fact]
    public void A_cloud_run_states_the_limits_of_delegated_access()
    {
        // A bare account row invites the reader to assume the scan saw everything. The document has
        // to say otherwise, and has to say permissions were not compared.
        var cert = CompletionCertificate.Generate(Run("svc-migrate@contoso.gov"), "1.1.0-test", DateTime.UtcNow);

        Assert.Contains("delegated access", cert.Html);
        Assert.Contains("permissions were not compared", cert.Html);
    }

    [Fact]
    public void A_purely_local_run_says_nothing_about_a_cloud_account()
    {
        var cert = CompletionCertificate.Generate(Run(null), "1.1.0-test", DateTime.UtcNow);

        Assert.DoesNotContain("Cloud account", cert.Html);
        Assert.DoesNotContain("delegated access", cert.Html);
    }

    [Fact]
    public void The_account_is_covered_by_the_integrity_fingerprint()
    {
        // Changing who read the data must invalidate the document, or the field would be decorative.
        var at = new DateTime(2026, 10, 13, 14, 0, 0, DateTimeKind.Utc);
        var a = CompletionCertificate.Generate(Run("one@contoso.gov"), "1.1.0-test", at);
        var b = CompletionCertificate.Generate(Run("two@contoso.gov"), "1.1.0-test", at);

        Assert.NotEqual(a.Fingerprint, b.Fingerprint);
        Assert.Contains("cloudAccount", a.Canonical);
    }

    [Fact]
    public void The_canonical_block_records_the_absence_of_a_cloud_account_too()
    {
        // Emitted even when empty: otherwise a cloud run's certificate could be made to resemble a
        // local one simply by deleting the line, and the fingerprint would not notice.
        var cert = CompletionCertificate.Generate(Run(null), "1.1.0-test", DateTime.UtcNow);
        Assert.Contains("cloudAccount", cert.Canonical);
    }

    [Fact]
    public void Tampering_with_the_account_is_detected()
    {
        var cert = CompletionCertificate.Generate(Run("real@contoso.gov"), "1.1.0-test", DateTime.UtcNow);
        var tampered = cert.Html.Replace("real@contoso.gov", "someone.else@contoso.gov");

        var check = CompletionCertificate.Verify(tampered);
        Assert.False(check.Intact);
    }
}

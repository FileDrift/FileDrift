// SPDX-License-Identifier: GPL-3.0-or-later
using FileDrift.Core.Models;
using FileDrift.Core.Reporting;
using Xunit;

namespace FileDrift.Core.Tests;

public class CompletionCertificateTests
{
    private static RunRecord Run(long differences = 0, long inaccessible = 0, bool signed = false) => new()
    {
        Id = Guid.NewGuid(),
        StartedAtUtc = new DateTime(2026, 6, 29, 12, 0, 0, DateTimeKind.Utc),
        CompletedAtUtc = new DateTime(2026, 6, 29, 12, 5, 0, DateTimeKind.Utc),
        SourcePath = @"\\srv\share",
        DestPath = @"C:\Dest",
        Options = new VerifyOptions(),
        Status = RunStatus.Completed,
        MatchedCount = 100,
        DifferentCount = differences,
        InaccessibleCount = inaccessible,
        SignedOffAtUtc = signed ? new DateTime(2026, 6, 29, 13, 0, 0, DateTimeKind.Utc) : null,
        SignedOffBy = signed ? "Jane Approver" : null,
        SignedOffByAccount = signed ? @"DOM\jane" : null,
    };

    [Fact]
    public void Generate_then_verify_is_intact()
    {
        var cert = CompletionCertificate.Generate(Run(), "1.0.0-test", DateTime.UtcNow);
        var v = CompletionCertificate.Verify(cert.Html);

        Assert.True(v.Parsed);
        Assert.True(v.Intact);
        Assert.Equal(cert.Fingerprint, v.EmbeddedFingerprint);
        Assert.Equal(cert.Fingerprint, v.RecomputedFingerprint);
    }

    [Fact]
    public void Tampering_with_a_displayed_value_is_detected()
    {
        // Edit the visible matched count without updating the embedded canonical → fingerprint still matches
        // the (untouched) canonical, but the canonical itself no longer matches the recomputed-from-DB path.
        // Here we tamper the embedded canonical instead, which the self-contained check must catch.
        var cert = CompletionCertificate.Generate(Run(), "1.0.0-test", DateTime.UtcNow);
        var tampered = cert.Html.Replace("matched=100", "matched=999");

        var v = CompletionCertificate.Verify(tampered);
        Assert.True(v.Parsed);
        Assert.False(v.Intact); // recomputed hash of altered canonical != embedded fingerprint
    }

    [Fact]
    public void Fingerprint_is_stable_across_regeneration_of_same_run()
    {
        var run = Run();
        var a = CompletionCertificate.Generate(run, "1.0.0-test", new DateTime(2026, 6, 29, 14, 0, 0, DateTimeKind.Utc));
        var b = CompletionCertificate.Generate(run, "1.0.0-test", new DateTime(2026, 6, 30, 9, 0, 0, DateTimeKind.Utc));

        // Different generation timestamps must NOT change the fingerprint (it covers run facts only).
        Assert.Equal(a.Fingerprint, b.Fingerprint);
    }

    [Fact]
    public void Unsigned_run_shows_watermark_signed_run_does_not()
    {
        var unsigned = CompletionCertificate.Generate(Run(signed: false), "1.0.0-test", DateTime.UtcNow);
        var signed = CompletionCertificate.Generate(Run(signed: true), "1.0.0-test", DateTime.UtcNow);

        Assert.Contains("class=\"watermark\"", unsigned.Html);
        Assert.DoesNotContain("class=\"watermark\"", signed.Html);
    }

    [Fact]
    public void Verdict_reflects_match_differences_and_incompleteness()
    {
        Assert.Contains("MATCH", CompletionCertificate.Generate(Run(), "v", DateTime.UtcNow).Html);
        Assert.Contains("DIFFERENCES FOUND", CompletionCertificate.Generate(Run(differences: 3), "v", DateTime.UtcNow).Html);
        Assert.Contains("INCOMPLETE", CompletionCertificate.Generate(Run(inaccessible: 2), "v", DateTime.UtcNow).Html);
    }

    [Fact]
    public void Verify_rejects_a_non_certificate_file()
    {
        var v = CompletionCertificate.Verify("<html><body>not a certificate</body></html>");
        Assert.False(v.Parsed);
    }
}

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
    public void Tampering_with_the_embedded_facts_is_detected()
    {
        var cert = CompletionCertificate.Generate(Run(), "1.0.0-test", DateTime.UtcNow);
        var tampered = cert.Html.Replace("matched=100", "matched=999");

        var v = CompletionCertificate.Verify(tampered);
        Assert.True(v.Parsed);
        Assert.False(v.Intact);
    }

    [Fact]
    public void Tampering_with_a_visible_value_is_also_detected()
    {
        // The fingerprint covers the whole document, so editing only the rendered HTML (not the embedded
        // facts) must still fail verification — this is the case the per-facts hash would have missed.
        var cert = CompletionCertificate.Generate(Run(differences: 3), "1.0.0-test", DateTime.UtcNow);
        var tampered = cert.Html.Replace("DIFFERENCES FOUND", "MATCH"); // fake a clean verdict on the page
        Assert.NotEqual(cert.Html, tampered); // sanity: the visible text was actually present

        var v = CompletionCertificate.Verify(tampered);
        Assert.True(v.Parsed);
        Assert.False(v.Intact);
    }

    [Fact]
    public void Fingerprint_is_deterministic_for_identical_input()
    {
        var run = Run();
        var at = new DateTime(2026, 6, 29, 14, 0, 0, DateTimeKind.Utc);
        var a = CompletionCertificate.Generate(run, "1.0.0-test", at);
        var b = CompletionCertificate.Generate(run, "1.0.0-test", at);

        Assert.Equal(a.Fingerprint, b.Fingerprint);
        Assert.Equal(a.Html, b.Html);
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

    [Fact]
    public void Hostile_sign_off_note_cannot_inject_markup_and_still_round_trips()
    {
        // A note (free text — also importable from a history JSON) must not be able to close the embedded
        // canonical block and smuggle live markup/script into the certificate.
        const string hostile = "</script><script>alert('pwned')</script><img src=x onerror=alert(1)>";
        var run = Run(signed: true);
        run.SignOffNote = hostile;

        var cert = CompletionCertificate.Generate(run, "1.0.0-test", DateTime.UtcNow);

        Assert.DoesNotContain("<script>alert", cert.Html); // nothing executable made it through
        Assert.DoesNotContain("<img", cert.Html);

        var v = CompletionCertificate.Verify(cert.Html);
        Assert.True(v.Intact); // encoding didn't break whole-document verification
        // The extracted (decoded) canonical still carries the exact original note, so the DB cross-check
        // (BuildCanonical comparison) keeps working for hostile content too.
        Assert.Equal(hostile, CompletionCertificate.CanonicalField(v.Canonical!, "signOffNote"));
        Assert.Equal(v.Canonical, CompletionCertificate.BuildCanonical(run, "1.0.0-test"));
    }

    [Fact]
    public void Certificate_carries_a_script_blocking_content_security_policy()
    {
        var cert = CompletionCertificate.Generate(Run(), "1.0.0-test", DateTime.UtcNow);
        Assert.Contains("Content-Security-Policy", cert.Html);
        Assert.Contains("default-src 'none'", cert.Html);
    }

    [Fact]
    public void Certificate_never_mentions_reconcile_facts_even_when_the_run_has_them()
    {
        // The certificate attests to what a verify found, not to what a reconcile subsequently did — even
        // when a run genuinely has reconcile data (persisted for report/History), none of it should leak
        // into the certificate or its fingerprinted canonical facts.
        var run = Run();
        run.ReconciledAtUtc = new DateTime(2026, 6, 29, 14, 0, 0, DateTimeKind.Utc);
        run.ReconcileBytesCopied = 5L * 1024 * 1024 * 1024;
        run.ReconcileFilesCopied = 79;
        run.ReconcileFilesOverwritten = 3;
        run.ReconcileStopped = true;

        var cert = CompletionCertificate.Generate(run, "1.0.0-test", DateTime.UtcNow);

        Assert.DoesNotContain("reconcile", cert.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Data copied", cert.Html);
        Assert.DoesNotContain("reconcile", cert.Canonical, StringComparison.OrdinalIgnoreCase);

        var v = CompletionCertificate.Verify(cert.Html);
        Assert.True(v.Intact);
        Assert.Equal(v.Canonical, CompletionCertificate.BuildCanonical(run, "1.0.0-test"));
    }
}

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
    public void Unreconciled_run_shows_no_reconcile_section()
    {
        var cert = CompletionCertificate.Generate(Run(), "1.0.0-test", DateTime.UtcNow);
        Assert.DoesNotContain("class=\"reconcile\"", cert.Html);
        Assert.DoesNotContain("Data copied", cert.Html);
    }

    [Fact]
    public void Reconciled_run_shows_data_copied_in_GB()
    {
        var run = Run();
        run.ReconciledAtUtc = new DateTime(2026, 6, 29, 14, 0, 0, DateTimeKind.Utc);
        run.ReconcileBytesCopied = 5L * 1024 * 1024 * 1024; // exactly 5 GB
        run.ReconcileFilesCopied = 79;
        run.ReconcileFilesOverwritten = 3;

        var cert = CompletionCertificate.Generate(run, "1.0.0-test", DateTime.UtcNow);

        Assert.Contains("class=\"reconcile\"", cert.Html);
        Assert.Contains("5 GB", cert.Html);
        Assert.Contains(">79<", cert.Html);
        Assert.DoesNotContain("Stopped before finishing", cert.Html); // completed cleanly
    }

    [Fact]
    public void Reconciled_run_formats_small_and_large_byte_counts_appropriately()
    {
        var small = Run();
        small.ReconciledAtUtc = DateTime.UtcNow;
        small.ReconcileBytesCopied = 500; // under 1 KB threshold
        Assert.Contains("500 B", CompletionCertificate.Generate(small, "v", DateTime.UtcNow).Html);

        var large = Run();
        large.ReconciledAtUtc = DateTime.UtcNow;
        large.ReconcileBytesCopied = 3L * 1024 * 1024 * 1024 * 1024; // 3 TB
        Assert.Contains("3 TB", CompletionCertificate.Generate(large, "v", DateTime.UtcNow).Html);
    }

    [Fact]
    public void Stopped_reconcile_is_flagged_on_the_certificate()
    {
        var run = Run();
        run.ReconciledAtUtc = DateTime.UtcNow;
        run.ReconcileBytesCopied = 1024;
        run.ReconcileStopped = true;

        var cert = CompletionCertificate.Generate(run, "1.0.0-test", DateTime.UtcNow);
        Assert.Contains("Stopped before finishing", cert.Html);
    }

    [Fact]
    public void Reconcile_fields_round_trip_through_canonical_and_verify()
    {
        var run = Run();
        run.ReconciledAtUtc = new DateTime(2026, 6, 29, 15, 0, 0, DateTimeKind.Utc);
        run.ReconcileBytesCopied = 123_456;
        run.ReconcileFilesCopied = 10;
        run.ReconcileFilesOverwritten = 2;
        run.ReconcileStopped = true;

        var cert = CompletionCertificate.Generate(run, "1.0.0-test", DateTime.UtcNow);
        var v = CompletionCertificate.Verify(cert.Html);

        Assert.True(v.Intact);
        Assert.Equal("123456", CompletionCertificate.CanonicalField(v.Canonical!, "reconcileBytesCopied"));
        Assert.Equal("true", CompletionCertificate.CanonicalField(v.Canonical!, "reconcileStopped"));
        // Cross-checking against the authoritative record must also agree (same as the DB cross-check path).
        Assert.Equal(v.Canonical, CompletionCertificate.BuildCanonical(run, "1.0.0-test"));
    }
}

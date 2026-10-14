// SPDX-License-Identifier: GPL-3.0-or-later
using FileDrift.Core.Models;
using FileDrift.Core.Reporting;
using Xunit;

namespace FileDrift.Core.Tests;

/// <summary>
/// When a destination could not accept the source names, a clean match is a weaker equivalence than it
/// appears: the content agrees, the names do not. The certificate has to say so, or it overstates what
/// was verified.
/// </summary>
public class CertificateRenameDisclosureTests
{
    private static RunRecord Run(long renamed) => new()
    {
        Id = Guid.NewGuid(),
        StartedAtUtc = new DateTime(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc),
        CompletedAtUtc = new DateTime(2026, 10, 14, 9, 1, 0, DateTimeKind.Utc),
        SourcePath = @"\\fileserv\dept",
        DestPath = "https://contoso.sharepoint.com/sites/IT/Shared Documents",
        Options = new VerifyOptions { Depth = VerifyDepth.Full, HashAlgorithm = FileDriftHashAlgorithm.QuickXor },
        Status = RunStatus.Completed,
        MatchedCount = 5,
        CloudAccount = "migrator@contoso.gov",
        RenamedCount = renamed,
    };

    [Fact]
    public void Renames_are_counted_and_explained()
    {
        var cert = CompletionCertificate.Generate(Run(4), "1.1.0-test", DateTime.UtcNow);

        Assert.Contains("Renamed at destination", cert.Html);
        Assert.Contains("stored under a different name", cert.Html);
        // The crucial qualification: the match covers the data, not the names.
        Assert.Contains("does not", cert.Html);
        Assert.Contains("names are identical", cert.Html);
    }

    [Fact]
    public void A_run_with_no_renames_says_nothing_about_them()
    {
        var cert = CompletionCertificate.Generate(Run(0), "1.1.0-test", DateTime.UtcNow);

        Assert.DoesNotContain("Renamed at destination", cert.Html);
        Assert.DoesNotContain("stored under a different name", cert.Html);
    }

    [Fact]
    public void The_rename_count_is_covered_by_the_integrity_fingerprint()
    {
        // Otherwise the disclosure could be edited out without invalidating the document.
        var at = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        var none = CompletionCertificate.Generate(Run(0), "1.1.0-test", at);
        var some = CompletionCertificate.Generate(Run(4), "1.1.0-test", at);

        Assert.NotEqual(none.Fingerprint, some.Fingerprint);
        Assert.Contains("renamedCount", none.Canonical);   // emitted even when zero
        Assert.Contains("renamedCount", some.Canonical);
    }

    [Fact]
    public void Removing_the_disclosure_breaks_integrity()
    {
        var cert = CompletionCertificate.Generate(Run(4), "1.1.0-test", DateTime.UtcNow);
        var tampered = cert.Html.Replace("Renamed at destination", "Nothing to see");

        Assert.False(CompletionCertificate.Verify(tampered).Intact);
    }

    [Fact]
    public void A_renamed_run_can_still_report_a_match()
    {
        // The verdict is about the data. Renaming is disclosed alongside it, not treated as drift.
        var cert = CompletionCertificate.Generate(Run(4), "1.1.0-test", DateTime.UtcNow);
        Assert.Contains("MATCH", cert.Html);
    }
}

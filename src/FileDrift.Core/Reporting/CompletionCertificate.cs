// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using FileDrift.Core.Models;

namespace FileDrift.Core.Reporting;

/// <summary>Generates a self-contained HTML "certificate of verification" for a single run, plus a
/// SHA-256 fingerprint over a canonical serialization of the run's facts. The fingerprint and the exact
/// canonical text are embedded in the file so it can be re-checked later for tampering
/// (see <see cref="Verify"/>). The fingerprint makes alteration *detectable*; it is not a cryptographic
/// signature — Authenticode signing of the file is a separate, post-1.0 step.</summary>
public static class CompletionCertificate
{
    /// <summary>Marker so a parser can be sure it is looking at our canonical block, and so the format
    /// can be versioned independently of the app.</summary>
    public const string FormatId = "filedrift-certificate-v1";

    private const string CanonicalOpen = "<script id=\"filedrift-canonical\" type=\"text/plain\">";
    private const string CanonicalClose = "</script>";

    /// <summary>Stand-in for the fingerprint while the document is hashed (the fingerprint can't hash
    /// itself). Replaced with the real value after hashing; reversed back before re-hashing in Verify.</summary>
    private const string FingerprintToken = "{{FILEDRIFT-FINGERPRINT}}";

    public sealed record Certificate(string Html, string Fingerprint, string Canonical);

    /// <summary>The result of re-checking a certificate file: whether the embedded facts still hash to the
    /// embedded fingerprint, plus both fingerprints for display.</summary>
    public sealed record VerifyResult(bool Parsed, bool Intact, string? EmbeddedFingerprint,
        string? RecomputedFingerprint, Guid? RunId, string? Canonical);

    /// <summary>Reads a single <c>key=value</c> field out of a canonical block, or null if absent.</summary>
    public static string? CanonicalField(string canonical, string key)
    {
        foreach (var line in canonical.Split('\n'))
            if (line.StartsWith(key + "=", StringComparison.Ordinal))
                return line[(key.Length + 1)..];
        return null;
    }

    /// <summary>Builds the certificate for a run. The fingerprint covers the ENTIRE rendered document
    /// (with the fingerprint itself blanked to a placeholder while hashing), so any later edit — a visible
    /// value, the watermark, or the embedded facts — is detectable by <see cref="Verify"/>. Two
    /// certificates generated for the same run with the same <paramref name="generatedAtUtc"/> are
    /// byte-identical and share a fingerprint; a different generation time yields a different document and
    /// fingerprint, as each physical certificate is its own artifact.</summary>
    public static Certificate Generate(RunRecord run, string appVersion, DateTime generatedAtUtc)
    {
        var canonical = BuildCanonical(run, appVersion);
        var template = BuildHtml(run, appVersion, generatedAtUtc, canonical, FingerprintToken);
        var fingerprint = ComputeFingerprint(template);
        var html = template.Replace(FingerprintToken, fingerprint);
        return new Certificate(html, fingerprint, canonical);
    }

    /// <summary>SHA-256 (lowercase hex) of the supplied UTF-8 content.</summary>
    public static string ComputeFingerprint(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Re-checks a certificate's HTML by reversing the fingerprint substitution and re-hashing the
    /// WHOLE document, so any edit anywhere is caught. Also extracts the embedded canonical facts (and run
    /// ID) so a caller can additionally cross-check against the system of record.</summary>
    public static VerifyResult Verify(string html)
    {
        int fpAt = html.IndexOf("data-fingerprint=\"", StringComparison.Ordinal);
        if (fpAt < 0)
            return new VerifyResult(false, false, null, null, null, null);
        int fpStart = fpAt + "data-fingerprint=\"".Length;
        int fpEnd = html.IndexOf('"', fpStart);
        var embedded = fpEnd < 0 ? "" : html[fpStart..fpEnd];
        if (embedded.Length != 64) // not our fingerprint format
            return new VerifyResult(false, false, null, null, null, null);

        // Whole-document integrity: put the placeholder back where the fingerprint is and re-hash the file.
        var template = html.Replace(embedded, FingerprintToken);
        var recomputed = ComputeFingerprint(template);

        // The canonical facts block (if still present) is what the DB cross-check compares against. If it
        // was stripped, the document hash already won't match, so intact is false regardless.
        int open = html.IndexOf(CanonicalOpen, StringComparison.Ordinal);
        int close = open < 0 ? -1 : html.IndexOf(CanonicalClose, open, StringComparison.Ordinal);
        string? canonical = open >= 0 && close >= 0
            ? html[(open + CanonicalOpen.Length)..close].Trim('\r', '\n')
            : null;
        var runId = canonical is null ? null : ParseRunId(canonical);

        return new VerifyResult(true, string.Equals(recomputed, embedded, StringComparison.Ordinal),
            embedded, recomputed, runId, canonical);
    }

    /// <summary>Rebuilds the canonical facts for a run as it stands now (e.g. the authoritative DB record),
    /// so a caller can compare against a certificate's embedded canonical to confirm it matches the system
    /// of record.</summary>
    public static string BuildCanonical(RunRecord run, string appVersion)
    {
        // Order is fixed; values use invariant formatting so the hash is stable across machines/locales.
        var sb = new StringBuilder();
        void Line(string key, string? value) => sb.Append(key).Append('=').Append(value ?? "").Append('\n');

        sb.Append(FormatId).Append('\n');
        Line("appVersion", appVersion);
        Line("runId", run.Id.ToString());
        Line("status", run.Status.ToString());
        Line("startedAtUtc", Iso(run.StartedAtUtc));
        Line("completedAtUtc", IsoOrNull(run.CompletedAtUtc));
        Line("source", run.SourcePath);
        Line("destination", run.DestPath);
        Line("depth", run.Options.Depth.ToString());
        Line("hashAlgorithm", run.Options.HashAlgorithm.ToString());
        Line("includeAcl", run.Options.IncludeAcl ? "true" : "false");
        Line("enforceOwnership", run.Options.EnforceOwnership ? "true" : "false");
        Line("aclScope", run.Options.AclScope.ToString());
        Line("threads", run.Options.Threads.ToString(CultureInfo.InvariantCulture));
        Line("strict", run.Options.Strict ? "true" : "false");
        Line("startUtc", IsoOrNull(run.Options.StartUtc));
        Line("endUtc", IsoOrNull(run.Options.EndUtc));
        Line("totalSourceFiles", run.TotalSourceFiles.ToString(CultureInfo.InvariantCulture));
        Line("totalDestFiles", run.TotalDestFiles.ToString(CultureInfo.InvariantCulture));
        Line("matched", run.MatchedCount.ToString(CultureInfo.InvariantCulture));
        Line("different", run.DifferentCount.ToString(CultureInfo.InvariantCulture));
        Line("missingAtDest", run.MissingAtDestCount.ToString(CultureInfo.InvariantCulture));
        Line("extraAtDest", run.ExtraAtDestCount.ToString(CultureInfo.InvariantCulture));
        Line("inaccessible", run.InaccessibleCount.ToString(CultureInfo.InvariantCulture));
        Line("signedOff", run.SignedOffAtUtc is not null ? "true" : "false");
        Line("signedOffAtUtc", IsoOrNull(run.SignedOffAtUtc));
        Line("signedOffBy", run.SignedOffBy);
        Line("signedOffByAccount", run.SignedOffByAccount);
        Line("signOffNote", run.SignOffNote);
        return sb.ToString().TrimEnd('\n');
    }

    private static Guid? ParseRunId(string canonical)
    {
        foreach (var line in canonical.Split('\n'))
            if (line.StartsWith("runId=", StringComparison.Ordinal) &&
                Guid.TryParse(line["runId=".Length..], out var g))
                return g;
        return null;
    }

    // ─────────────────────────── verdict ───────────────────────────

    private enum Verdict { Match, Differences, Incomplete, NotCompleted }

    private static (Verdict Kind, string Headline, string Detail) Assess(RunRecord r)
    {
        if (r.Status != RunStatus.Completed)
            return (Verdict.NotCompleted, r.Status.ToString().ToUpperInvariant(),
                "This run did not complete, so it does not certify a comparison.");

        var diffs = r.TotalDifferences;
        var detail =
            $"{r.MatchedCount:N0} matched · {diffs:N0} difference(s) " +
            $"({r.DifferentCount:N0} different, {r.MissingAtDestCount:N0} missing at destination, " +
            $"{r.ExtraAtDestCount:N0} extra at destination)";

        if (r.InaccessibleCount > 0)
            return (Verdict.Incomplete, "INCOMPLETE",
                $"{r.InaccessibleCount:N0} path(s) could not be read and were skipped, so the comparison is " +
                $"incomplete. {detail}.");
        if (diffs == 0)
            return (Verdict.Match, "MATCH", $"Source and destination are identical. {detail}.");
        return (Verdict.Differences, "DIFFERENCES FOUND", $"{detail}.");
    }

    // ─────────────────────────── HTML ───────────────────────────

    private static string BuildHtml(RunRecord r, string appVersion, DateTime generatedAtUtc,
        string canonical, string fingerprint)
    {
        var (kind, headline, detail) = Assess(r);
        bool signed = r.SignedOffAtUtc is not null;
        string verdictClass = kind switch
        {
            Verdict.Match => "ok",
            Verdict.Differences => "warn",
            _ => "bad",
        };

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append($"<title>FileDrift Certificate — {E(r.Id.ToString())}</title>\n");
        sb.Append("<style>\n").Append(Css).Append("\n</style>\n</head>\n");
        sb.Append("<body class=\"").Append(signed ? "signed" : "unsigned").Append("\">\n");
        sb.Append("<main class=\"sheet\" data-fingerprint=\"").Append(fingerprint).Append("\">\n");

        // Watermark lives INSIDE the sheet and overlays the certificate body (on top, translucent), so it
        // is stamped across the actual content rather than sitting in the page margin where it could be
        // cropped away. (HTML remains editable — this is a visual deterrent; the fingerprint is the check.)
        if (!signed)
            sb.Append("<div class=\"watermark\" aria-hidden=\"true\"><div class=\"tile\">")
              .Append(WatermarkText()).Append("</div></div>\n");

        sb.Append("<header><div class=\"brand\">FileDrift</div>")
          .Append("<h1>Certificate of Verification</h1></header>\n");

        sb.Append($"<div class=\"verdict {verdictClass}\"><span class=\"headline\">{E(headline)}</span>")
          .Append($"<span class=\"detail\">{E(detail)}</span></div>\n");

        sb.Append("<table class=\"facts\">\n");
        Row(sb, "Run ID", r.Id.ToString());
        Row(sb, "Source", r.SourcePath);
        Row(sb, "Destination", r.DestPath);
        Row(sb, "Status", r.Status.ToString());
        Row(sb, "Started (UTC)", Iso(r.StartedAtUtc));
        Row(sb, "Completed (UTC)", IsoOrNull(r.CompletedAtUtc) is { Length: > 0 } c ? c : "—");
        Row(sb, "Depth", r.Options.Depth.ToString());
        Row(sb, "Hash algorithm", r.Options.HashAlgorithm.ToString());
        Row(sb, "ACL comparison", r.Options.IncludeAcl
            ? $"Yes ({r.Options.AclScope}{(r.Options.EnforceOwnership ? ", ownership enforced" : "")})"
            : "No");
        Row(sb, "Strict mode", r.Options.Strict ? "Yes" : "No");
        Row(sb, "Source files", r.TotalSourceFiles.ToString("N0", CultureInfo.InvariantCulture));
        Row(sb, "Destination files", r.TotalDestFiles.ToString("N0", CultureInfo.InvariantCulture));
        Row(sb, "Matched", r.MatchedCount.ToString("N0", CultureInfo.InvariantCulture));
        Row(sb, "Differences", r.TotalDifferences.ToString("N0", CultureInfo.InvariantCulture));
        Row(sb, "Inaccessible (skipped)", r.InaccessibleCount.ToString("N0", CultureInfo.InvariantCulture));
        sb.Append("</table>\n");

        sb.Append("<section class=\"signoff\">\n<h2>Sign-off</h2>\n");
        if (signed)
        {
            sb.Append("<table class=\"facts\">\n");
            Row(sb, "Signed off (UTC)", IsoOrNull(r.SignedOffAtUtc) ?? "");
            Row(sb, "Signed off by", r.SignedOffBy ?? "");
            Row(sb, "Operating account", r.SignedOffByAccount ?? "");
            if (r.SignOffWasDelegated)
                Row(sb, "Delegated", "Yes — recorded on behalf of the named party above");
            if (!string.IsNullOrWhiteSpace(r.SignOffNote))
                Row(sb, "Note", r.SignOffNote!);
            sb.Append("</table>\n");
        }
        else
        {
            sb.Append("<p class=\"unsigned-note\">This run has <strong>not been signed off</strong>. ")
              .Append("This certificate records the verification result only; it is not an attestation ")
              .Append("by a responsible party.</p>\n");
        }
        sb.Append("</section>\n");

        sb.Append("<footer>\n");
        sb.Append($"<p>Generated {E(Iso(generatedAtUtc))} UTC by FileDrift {E(appVersion)}.</p>\n");
        sb.Append("<p class=\"fp\">Integrity fingerprint (SHA-256):<br><code>")
          .Append(E(fingerprint)).Append("</code></p>\n");
        sb.Append("<p class=\"fp-note\">Re-check with <code>FileDrift-CLI certificate --verify &lt;file&gt;</code>. ")
          .Append("The fingerprint covers this entire document, so any edit to it is detectable; it is an ")
          .Append("integrity check, not a cryptographic signature.</p>\n");
        sb.Append("</footer>\n");

        // The exact bytes the fingerprint covers, embedded verbatim for re-checking.
        sb.Append(CanonicalOpen).Append('\n').Append(canonical).Append('\n').Append(CanonicalClose).Append('\n');
        sb.Append("</main>\n</body></html>\n");
        return sb.ToString();
    }

    private static void Row(StringBuilder sb, string label, string value) =>
        sb.Append("<tr><th>").Append(E(label)).Append("</th><td>").Append(E(value)).Append("</td></tr>\n");

    private static string WatermarkText()
    {
        // Repeat the phrase enough to tile the rotated overlay across a printed page.
        var one = "NOT&nbsp;SIGNED&nbsp;OFF";
        return string.Join("&nbsp;&nbsp;&nbsp;", Enumerable.Repeat(one, 220));
    }

    private static string E(string s) => WebUtility.HtmlEncode(s);

    private static string Iso(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string? IsoOrNull(DateTime? utc) => utc is { } v ? Iso(v) : "";

    // Sized to fit a single Letter or A4 page when printed: compact vertical rhythm, no forced @page size
    // (so the user's paper choice is honoured), 12mm margins, and the sheet styled flat for print.
    private const string Css = """
        :root { --ink:#1b1b1b; --muted:#5a5a5a; --line:#d9d9d9; --ok:#1a7f37; --warn:#9a6700; --bad:#cf222e; }
        * { box-sizing: border-box; }
        body { margin:0; padding:22px; font-family:'Segoe UI',system-ui,sans-serif; color:var(--ink);
               background:#f3f3f3; }
        .sheet { position:relative; overflow:hidden; max-width:760px; margin:0 auto; background:#fff;
                 border:1px solid var(--line); border-radius:8px; padding:28px 36px; box-shadow:0 1px 4px rgba(0,0,0,.08); }
        header { text-align:center; border-bottom:2px solid var(--ink); padding-bottom:8px; margin-bottom:14px; }
        .brand { font-weight:700; letter-spacing:.18em; text-transform:uppercase; color:var(--muted); font-size:12px; }
        h1 { margin:4px 0 0; font-size:22px; letter-spacing:.02em; }
        .verdict { display:flex; flex-direction:column; gap:3px; padding:10px 14px; border-radius:6px; margin-bottom:14px;
                   border:1px solid var(--line); }
        .verdict .headline { font-size:18px; font-weight:700; letter-spacing:.04em; }
        .verdict .detail { font-size:12.5px; color:var(--muted); }
        .verdict.ok   { border-color:var(--ok);  background:#eaf6ec; } .verdict.ok .headline   { color:var(--ok); }
        .verdict.warn { border-color:var(--warn); background:#fbf3e0; } .verdict.warn .headline { color:var(--warn); }
        .verdict.bad  { border-color:var(--bad); background:#fbeaec; } .verdict.bad .headline  { color:var(--bad); }
        table.facts { width:100%; border-collapse:collapse; margin:0; font-size:13px; position:relative; }
        table.facts th { text-align:left; width:190px; padding:4px 12px 4px 0; color:var(--muted); font-weight:600;
                         vertical-align:top; border-bottom:1px solid #eee; }
        table.facts td { padding:4px 0; vertical-align:top; word-break:break-all; border-bottom:1px solid #eee; }
        section.signoff { margin-top:14px; }
        h2 { font-size:15px; border-bottom:1px solid var(--line); padding-bottom:5px; margin:0 0 8px; }
        .unsigned-note { font-size:12.5px; color:var(--bad); background:#fbeaec; border:1px solid var(--bad);
                         border-radius:6px; padding:9px 13px; }
        footer { margin-top:16px; border-top:1px solid var(--line); padding-top:10px; font-size:11.5px; color:var(--muted); }
        footer p { margin:4px 0; }
        footer .fp code { font-size:11.5px; color:var(--ink); word-break:break-all; }
        footer .fp-note { font-size:11px; }
        /* Diagonal tiled watermark, laid OVER the certificate body (translucent, non-interactive). */
        .watermark { position:absolute; inset:0; overflow:hidden; pointer-events:none; z-index:5; }
        .watermark .tile { position:absolute; top:-50%; left:-50%; width:200%; height:200%; transform:rotate(-30deg);
                           font:700 33px 'Segoe UI',sans-serif; color:var(--bad); opacity:.13; line-height:2.9;
                           letter-spacing:.10em; word-spacing:.4em; user-select:none; }
        @media print {
            @page { margin:12mm; }
            body { background:#fff; padding:0; }
            .sheet { border:none; box-shadow:none; max-width:none; border-radius:0; padding:0; }
            .verdict, .unsigned-note, .watermark .tile {
                -webkit-print-color-adjust:exact; print-color-adjust:exact; }
            .watermark .tile { opacity:.16; }
        }
        """;
}

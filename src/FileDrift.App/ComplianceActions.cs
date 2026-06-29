// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FileDrift.Core;
using FileDrift.Core.Interfaces;
using FileDrift.Core.Models;
using FileDrift.Core.Reporting;

namespace FileDrift.App;

/// <summary>Shared sign-off / certificate actions so the Verify page and History page (and any future
/// compliance surface) behave identically. Each returns a status line to show, or null if cancelled.</summary>
internal static class ComplianceActions
{
    public static async Task<string?> SignOffAsync(IRunRepository repository, RunRecord run)
    {
        bool signed = run.SignedOffAtUtc is not null;
        if (signed && !await Dialogs.ConfirmAsync("Already signed off",
                $"This run was signed off on {run.SignedOffAtUtc:yyyy-MM-dd HH:mm} UTC by {run.SignedOffBy}. " +
                "Record a new sign-off over it?", confirmText: "Re-sign", danger: true))
            return null;

        var account = OperatorIdentity.Current;

        var byBox = new Wpf.Ui.Controls.TextBox { Text = account, MinWidth = 340 };
        var noteBox = new Wpf.Ui.Controls.TextBox
        {
            PlaceholderText = "Optional note (what was reviewed, ticket #, …)",
            MinWidth = 340, MinHeight = 64, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };

        var panel = new StackPanel { MaxWidth = 380 };
        panel.Children.Add(Muted($"{run.SourcePath}  →  {run.DestPath}", 0, 0, 0, 4));
        panel.Children.Add(Muted(
            $"{run.MatchedCount:N0} matched · {run.TotalDifferences:N0} difference(s) · " +
            $"{run.InaccessibleCount:N0} inaccessible", 0, 0, 0, 12));
        panel.Children.Add(new TextBlock { Text = "Signed off by", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(byBox);
        panel.Children.Add(Muted(
            $"Operating account ({account}) is recorded separately and cannot be changed.", 0, 2, 0, 10, 11));
        panel.Children.Add(new TextBlock { Text = "Note", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(noteBox);

        if (!await Dialogs.ConfirmAsync("Sign off run", panel, confirmText: "Sign off"))
            return null;

        var by = string.IsNullOrWhiteSpace(byBox.Text) ? account : byBox.Text.Trim();
        var note = string.IsNullOrWhiteSpace(noteBox.Text) ? null : noteBox.Text.Trim();

        try
        {
            bool ok = await repository.MarkSignedOffAsync(run.Id, by, account, note);
            if (!ok) return "That run no longer exists.";
            // Reflect the new state on the in-memory record so a follow-up export shows it as signed.
            run.SignedOffAtUtc = DateTime.UtcNow;
            run.SignedOffBy = by;
            run.SignedOffByAccount = account;
            run.SignOffNote = note;
            return string.Equals(by, account, StringComparison.OrdinalIgnoreCase)
                ? $"Signed off by {by}."
                : $"Signed off as {by} (operated by {account}).";
        }
        catch (Exception ex)
        {
            return $"Sign-off failed: {ex.Message}";
        }
    }

    public static async Task<string?> ExportCertificateAsync(RunRecord run)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save certificate of verification",
            Filter = "HTML certificate (*.html)|*.html",
            FileName = $"FileDrift-Certificate-{run.Id.ToString()[..8]}.html",
            AddExtension = true,
            DefaultExt = ".html",
        };
        if (dialog.ShowDialog() != true) return null;

        try
        {
            var cert = CompletionCertificate.Generate(run, AppInfo.Version, DateTime.UtcNow);
            await File.WriteAllTextAsync(dialog.FileName, cert.Html);
            var status = run.SignedOffAtUtc is null
                ? $"Certificate saved (unsigned run) – {dialog.FileName}"
                : $"Certificate saved – {dialog.FileName}";

            if (await Dialogs.ConfirmAsync("Certificate saved", "Open the certificate now?", confirmText: "Open"))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dialog.FileName)
                { UseShellExecute = true });
            return status;
        }
        catch (Exception ex)
        {
            return $"Certificate export failed: {ex.Message}";
        }
    }

    public static async Task<string?> VerifyCertificateAsync(IRunRepository repository)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Verify a certificate of verification",
            Filter = "HTML certificate (*.html)|*.html|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return null;

        try
        {
            var html = await File.ReadAllTextAsync(dialog.FileName);
            var result = CompletionCertificate.Verify(html);
            if (!result.Parsed)
            {
                await Dialogs.InfoAsync("Not a certificate",
                    "This file is not a FileDrift certificate (no integrity fingerprint found).");
                return "Not a FileDrift certificate.";
            }

            bool? matchesDb = null;
            if (result.RunId is Guid rid && result.Canonical is not null)
            {
                var run = await repository.GetAsync(rid);
                if (run is not null)
                {
                    var appVer = CompletionCertificate.CanonicalField(result.Canonical, "appVersion") ?? AppInfo.Version;
                    matchesDb = string.Equals(
                        CompletionCertificate.BuildCanonical(run, appVer), result.Canonical, StringComparison.Ordinal);
                }
            }

            var verdict = result.Intact ? "INTACT" : "ALTERED";
            var body = new StackPanel { MaxWidth = 420 };
            body.Children.Add(new TextBlock
            {
                Text = result.Intact
                    ? "The certificate is intact – it has not been modified since FileDrift produced it."
                    : "WARNING: this certificate has been ALTERED since it was produced. Do not trust its contents.",
                TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10),
            });
            body.Children.Add(Muted($"Run ID: {result.RunId}", 0, 0, 0, 2));
            body.Children.Add(Muted(matchesDb switch
            {
                true => "Matches this machine's history database.",
                false => "Does NOT match this machine's history database.",
                null => "The referenced run is not in this machine's history database (cross-check skipped).",
            }, 0, 0, 0, 2));
            body.Children.Add(Muted($"File: {dialog.FileName}", 0, 0, 0, 0, 11));

            await Dialogs.InfoAsync($"Certificate {verdict}", body);
            return result.Intact
                ? $"Certificate INTACT{(matchesDb == true ? " · matches database" : "")}."
                : "Certificate ALTERED – do not trust its contents.";
        }
        catch (Exception ex)
        {
            return $"Verification failed: {ex.Message}";
        }
    }

    private static TextBlock Muted(string text, double l, double t, double r, double b, double size = 12) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = size,
        Margin = new Thickness(l, t, r, b),
        Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextFillColorSecondaryBrush"),
    };
}

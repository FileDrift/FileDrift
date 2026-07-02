// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FileDrift.Core;
using FileDrift.Core.Interfaces;
using FileDrift.Core.Models;
using FileDrift.Core.Persistence;

namespace FileDrift.App.Pages;

public partial class HistoryPage : Page
{
    private readonly IRunRepository _repository;
    private readonly ObservableCollection<HistoryRow> _rows = new();
    private DateTime? _currentAfter; // the active "Show" filter cutoff, also the scope of Export history

    public HistoryPage()
    {
        InitializeComponent();
        _repository = AppServices.Repository;
        HistoryGrid.ItemsSource = _rows;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void OnShowFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        _currentAfter = CutoffFromComboIndex(ShowFilterBox.SelectedIndex);
        if (IsLoaded) await LoadAsync();
    }

    /// <summary>Maps the "Show" / clear-scope combo index (0=All, 1=7d, 2=30d, 3=90d) to a UTC cutoff.</summary>
    private static DateTime? CutoffFromComboIndex(int index) => index switch
    {
        1 => DateTime.UtcNow.AddDays(-7),
        2 => DateTime.UtcNow.AddDays(-30),
        3 => DateTime.UtcNow.AddDays(-90),
        _ => null,
    };

    private async Task LoadAsync()
    {
        _rows.Clear();
        var runs = await _repository.ListAsync(new RunQueryOptions { Limit = 200, After = _currentAfter });
        foreach (var run in runs)
            _rows.Add(HistoryRow.From(run));
        StatusText.Text = $"{_rows.Count} run(s) shown. Select a run and choose Sign off to record review.";
    }

    private async void OnSignOff(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not HistoryRow row)
        {
            StatusText.Text = "Select a run to sign off.";
            return;
        }

        var run = await _repository.GetAsync(row.Id);
        if (run is null) { StatusText.Text = "That run no longer exists."; return; }

        var status = await ComplianceActions.SignOffAsync(_repository, run);
        if (status is not null) StatusText.Text = status;
        await LoadAsync();
    }

    private async void OnExportCertificate(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not HistoryRow row)
        {
            StatusText.Text = "Select a run to export a certificate.";
            return;
        }

        var run = await _repository.GetAsync(row.Id);
        if (run is null) { StatusText.Text = "That run no longer exists."; return; }

        var status = await ComplianceActions.ExportCertificateAsync(run);
        if (status is not null) StatusText.Text = status;
    }

    private async void OnVerifyCertificate(object sender, RoutedEventArgs e)
    {
        var status = await ComplianceActions.VerifyCertificateAsync(_repository);
        if (status is not null) StatusText.Text = status;
    }

    private async void OnExportHistory(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export run history",
            Filter = "JSON (*.json)|*.json",
            FileName = "FileDrift-History-Export.json",
            AddExtension = true,
            DefaultExt = ".json",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var runs = await _repository.ListAsync(new RunQueryOptions { After = _currentAfter });
            var json = HistoryExport.Export(runs, AppInfo.Version, DateTime.UtcNow);
            await File.WriteAllTextAsync(dialog.FileName, json);
            StatusText.Text = $"Exported {runs.Count} run(s) (per the Show filter above) – {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Export failed: {ex.Message}";
        }
    }

    private async void OnImportHistory(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import run history",
            Filter = "JSON (*.json)|*.json|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        bool overwrite = await Dialogs.ConfirmAsync("Import history",
            "Overwrite runs that already exist locally with the imported data? A locally signed-off run " +
            "is never overwritten, no matter what you choose here.",
            confirmText: "Overwrite existing", cancelText: "Keep local (skip existing)");

        try
        {
            var json = await File.ReadAllTextAsync(dialog.FileName);
            var summary = await HistoryExport.ImportAsync(_repository, json, overwrite);
            StatusText.Text =
                $"Imported {summary.Imported}, updated {summary.Updated}, " +
                $"skipped {summary.SkippedExists} (existing), skipped {summary.SkippedProtected} (signed off)" +
                (summary.Errors > 0 ? $", {summary.Errors} error(s)." : ".");
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Import failed: {ex.Message}";
        }
    }

    private async void OnClearUnsigned(object sender, RoutedEventArgs e)
    {
        var scopeBox = new ComboBox { Width = 200, SelectedIndex = 3 }; // default: older than 90 days
        scopeBox.Items.Add(new ComboBoxItem { Content = "All unsigned runs" });
        scopeBox.Items.Add(new ComboBoxItem { Content = "Older than 7 days" });
        scopeBox.Items.Add(new ComboBoxItem { Content = "Older than 30 days" });
        scopeBox.Items.Add(new ComboBoxItem { Content = "Older than 90 days" });

        var countText = new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 0), FontSize = 12,
            Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush"),
        };

        async Task RefreshCountAsync()
        {
            var cutoff = CutoffFromComboIndex(scopeBox.SelectedIndex);
            var matching = await _repository.ListAsync(new RunQueryOptions { SignedOff = false, Before = cutoff });
            countText.Text = $"{matching.Count} unsigned run(s) match this scope and will be permanently deleted.";
        }

        scopeBox.SelectionChanged += async (_, _) => await RefreshCountAsync();

        var panel = new StackPanel { MaxWidth = 380 };
        panel.Children.Add(new TextBlock
        {
            Text = "Permanently delete unsigned run history. Signed-off runs are never affected, " +
                   "regardless of scope, and cannot be removed here.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });
        panel.Children.Add(new TextBlock { Text = "Scope", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(scopeBox);
        panel.Children.Add(countText);

        await RefreshCountAsync(); // reflect the default selection before the dialog is shown

        if (!await Dialogs.ConfirmAsync("Clear unsigned runs", panel, confirmText: "Delete", danger: true))
            return;

        try
        {
            var cutoff = CutoffFromComboIndex(scopeBox.SelectedIndex);
            int deleted = await _repository.DeleteUnsignedAsync(cutoff);
            StatusText.Text = $"Deleted {deleted} unsigned run(s).";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Clear failed: {ex.Message}";
        }
        await LoadAsync();
    }

    private sealed record HistoryRow(
        Guid Id, string Started, string Source, string Dest, string Status, string Matched,
        string Differences, string Inaccessible, bool IsSignedOff, string SignedOff, string SignedOffBy)
    {
        public static HistoryRow From(RunRecord r) => new(
            r.Id,
            r.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            r.SourcePath,
            r.DestPath,
            r.Status.ToString(),
            r.MatchedCount.ToString("N0"),
            r.TotalDifferences.ToString("N0"),
            r.InaccessibleCount.ToString("N0"),
            r.SignedOffAtUtc is not null,
            r.SignedOffAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "",
            r.SignOffWasDelegated ? $"{r.SignedOffBy} (as {r.SignedOffByAccount})" : r.SignedOffBy ?? "");
    }
}

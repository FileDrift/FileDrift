// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using FileDrift.Core.Interfaces;
using FileDrift.Core.Models;
using FileDrift.Core.Persistence;

namespace FileDrift.App.Pages;

public partial class HistoryPage : Page
{
    private readonly IRunRepository _repository;
    private readonly ObservableCollection<HistoryRow> _rows = new();

    public HistoryPage()
    {
        InitializeComponent();
        _repository = new SqliteRunRepository();
        HistoryGrid.ItemsSource = _rows;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        _rows.Clear();
        var runs = await _repository.ListAsync(new RunQueryOptions { Limit = 200 });
        foreach (var run in runs)
            _rows.Add(HistoryRow.From(run));
        StatusText.Text = $"{_rows.Count} run(s). Select a run and choose Sign off to record review.";
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

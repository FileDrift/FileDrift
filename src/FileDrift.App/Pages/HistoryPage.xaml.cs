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
    }

    private sealed record HistoryRow(
        string Started, string Source, string Dest, string Status, string Matched, string Differences,
        string Inaccessible)
    {
        public static HistoryRow From(RunRecord r) => new(
            r.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            r.SourcePath,
            r.DestPath,
            r.Status.ToString(),
            r.MatchedCount.ToString("N0"),
            r.TotalDifferences.ToString("N0"),
            r.InaccessibleCount.ToString("N0"));
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
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

        if (row.IsSignedOff &&
            !await Dialogs.ConfirmAsync("Already signed off",
                $"This run was signed off on {row.SignedOff} by {row.SignedOffBy}. Record a new sign-off over it?",
                confirmText: "Re-sign", danger: true))
            return;

        var account = OperatorIdentity.Current;

        // Editable approver (defaults to the operating account) and an optional note.
        var byBox = new Wpf.Ui.Controls.TextBox { Text = account, MinWidth = 340 };
        var noteBox = new Wpf.Ui.Controls.TextBox
        {
            PlaceholderText = "Optional note (what was reviewed, ticket #, …)",
            MinWidth = 340, MinHeight = 64, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };

        var panel = new StackPanel { MaxWidth = 380 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{row.Source}  →  {row.Dest}",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
            Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush"),
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{row.Matched} matched · {row.Differences} difference(s) · {row.Inaccessible} inaccessible",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
            Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush"),
        });
        panel.Children.Add(new TextBlock { Text = "Signed off by", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(byBox);
        panel.Children.Add(new TextBlock
        {
            Text = $"Operating account ({account}) is recorded separately and cannot be changed.",
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 2, 0, 10),
            Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush"),
        });
        panel.Children.Add(new TextBlock { Text = "Note", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(noteBox);

        if (!await Dialogs.ConfirmAsync("Sign off run", panel, confirmText: "Sign off"))
            return;

        var by = string.IsNullOrWhiteSpace(byBox.Text) ? account : byBox.Text.Trim();
        var note = string.IsNullOrWhiteSpace(noteBox.Text) ? null : noteBox.Text.Trim();

        try
        {
            bool ok = await _repository.MarkSignedOffAsync(row.Id, by, account, note);
            StatusText.Text = ok
                ? (string.Equals(by, account, StringComparison.OrdinalIgnoreCase)
                    ? $"Signed off by {by}."
                    : $"Signed off as {by} (operated by {account}).")
                : "That run no longer exists.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Sign-off failed: {ex.Message}";
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

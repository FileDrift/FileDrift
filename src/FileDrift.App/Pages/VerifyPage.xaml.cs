using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using FileDrift.Core.Engine;
using FileDrift.Core.Models;
using FileDrift.Core.Persistence;
using Microsoft.Win32;

namespace FileDrift.App.Pages;

public partial class VerifyPage : Page
{
    private readonly VerifyEngine _engine;
    private readonly ObservableCollection<ComparisonRow> _rows = new();
    private CancellationTokenSource? _cts;

    public VerifyPage()
    {
        InitializeComponent();
        _engine = new VerifyEngine(new SmartFileEnumerator(), new SqliteRunRepository());
        ResultsGrid.ItemsSource = _rows;
    }

    private void OnBrowseSource(object sender, RoutedEventArgs e) => BrowseInto(SourceBox);
    private void OnBrowseDest(object sender, RoutedEventArgs e) => BrowseInto(DestBox);

    private static void BrowseInto(System.Windows.Controls.TextBox target)
    {
        var dialog = new OpenFolderDialog { Title = "Select folder" };
        if (dialog.ShowDialog() == true)
            target.Text = dialog.FolderName;
    }

    private async void OnVerifyClick(object sender, RoutedEventArgs e)
    {
        var src = SourceBox.Text?.Trim();
        var dst = DestBox.Text?.Trim();

        if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(dst))
        {
            StatusText.Text = "Enter both a source and a destination path.";
            return;
        }

        var options = new VerifyOptions
        {
            Depth = (VerifyDepth)Math.Max(0, DepthBox.SelectedIndex),
            IncludeAcl = AclSwitch.IsChecked == true,
            Threads = (int)(ThreadsBox.Value ?? 8),
        };

        _cts = new CancellationTokenSource();
        SetRunning(true);
        _rows.Clear();
        ResetSummary();

        var progress = new Progress<VerifyProgress>(OnProgress);

        try
        {
            var result = await Task.Run(() => _engine.RunAsync(src, dst, options, progress, _cts.Token));
            ShowResult(result);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled.";
            ProgressBar.Value = 0;
        }
        catch (UnauthorizedAccessException ex)
        {
            StatusText.Text = $"Access denied: {ex.Message}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.GetType().Name} — {ex.Message}";
        }
        finally
        {
            SetRunning(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusText.Text = "Cancelling…";
    }

    private void OnProgress(VerifyProgress p)
    {
        StatusText.Text = p.Message ?? p.Phase.ToString();

        if (p.Total > 0)
        {
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = Math.Min(100, p.Processed * 100.0 / p.Total);
        }
        else
        {
            ProgressBar.IsIndeterminate = true;
        }
    }

    private void ShowResult(VerifyResult result)
    {
        var run = result.Run;
        ProgressBar.IsIndeterminate = false;
        ProgressBar.Value = 100;

        MatchedText.Text = run.MatchedCount.ToString("N0");
        DifferentText.Text = run.DifferentCount.ToString("N0");
        MissingText.Text = run.MissingAtDestCount.ToString("N0");
        ExtraText.Text = run.ExtraAtDestCount.ToString("N0");

        // Show differences first (most actionable), then the rest.
        foreach (var c in result.Comparisons
                     .OrderBy(c => c.Status == ComparisonStatus.Matched)
                     .ThenBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            _rows.Add(ComparisonRow.From(c));
        }

        StatusText.Text =
            $"Done — {run.MatchedCount:N0} matched, {run.TotalDifferences:N0} differences " +
            $"across {run.TotalSourceFiles:N0} source / {run.TotalDestFiles:N0} dest files.";
    }

    private void ResetSummary()
    {
        MatchedText.Text = DifferentText.Text = MissingText.Text = ExtraText.Text = "—";
        ProgressBar.Value = 0;
    }

    private void SetRunning(bool running)
    {
        VerifyButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        SourceBox.IsEnabled = DestBox.IsEnabled = DepthBox.IsEnabled =
            AclSwitch.IsEnabled = ThreadsBox.IsEnabled = !running;
    }

    private sealed record ComparisonRow(string Path, string Status, string Differences, string Size)
    {
        public static ComparisonRow From(ComparisonResult c)
        {
            var record = c.Source ?? c.Dest;
            return new ComparisonRow(
                c.RelativePath,
                FormatStatus(c.Status),
                c.Differences == FileDifference.None ? "" : c.Differences.ToString(),
                record is { IsDirectory: false } ? FormatSize(record.SizeBytes) : "");
        }

        private static string FormatStatus(ComparisonStatus status) => status switch
        {
            ComparisonStatus.Matched => "Matched",
            ComparisonStatus.Different => "Different",
            ComparisonStatus.MissingAtDest => "Missing at dest",
            ComparisonStatus.ExtraAtDest => "Extra at dest",
            _ => status.ToString(),
        };

        private static string FormatSize(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
            return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
        }
    }
}

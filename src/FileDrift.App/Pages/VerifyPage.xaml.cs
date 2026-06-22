using System.Collections.ObjectModel;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using FileDrift.Core.Engine;
using FileDrift.Core.Interfaces;
using FileDrift.Core.Models;
using FileDrift.Core.Persistence;
using Microsoft.Win32;

namespace FileDrift.App.Pages;

public partial class VerifyPage : Page
{
    private readonly SmartFileEnumerator _enumerator = new();
    private readonly VerifyEngine _engine;
    private readonly PreflightEngine _preflight;
    private readonly ICredentialStore _credentials = new WindowsCredentialStore();
    private readonly ObservableCollection<ComparisonRow> _rows = new();
    private CancellationTokenSource? _cts;
    private bool _hasDefaultCredential;

    public VerifyPage()
    {
        InitializeComponent();
        _engine = new VerifyEngine(_enumerator, new SqliteRunRepository());
        _preflight = new PreflightEngine(_enumerator);
        ResultsGrid.ItemsSource = _rows;
        Loaded += OnLoaded;
        UpdateModeReadout();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThreadsBox.Text = VerifyOptions.DefaultThreads.ToString(); // 50% of logical processors
        ThreadsHint.Text = $"of {Environment.ProcessorCount} detected";
        UpdateHashVisibility();
        RefreshCredentialCombos();
    }

    private void OnThreadsInput(object sender, System.Windows.Input.TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(char.IsDigit);

    private int ResolveThreads() =>
        int.TryParse(ThreadsBox.Text, out var n) ? Math.Clamp(n, 1, 64) : VerifyOptions.DefaultThreads;

    // ── credential combos ──

    private void RefreshCredentialCombos()
    {
        string[] targets;
        string? defaultLabel = null;
        try
        {
            var all = _credentials.ListTargets().ToArray();
            targets = all.Where(t => !CredentialTarget.IsDefault(t)).OrderBy(t => t).ToArray();
            if (all.Any(CredentialTarget.IsDefault))
            {
                var def = _credentials.GetCredential(CredentialTarget.DefaultTarget);
                if (def is not null) defaultLabel = $"Default ({def.UserName})";
            }
        }
        catch { targets = []; }

        _hasDefaultCredential = defaultLabel is not null;

        Populate(SourceCredBox, targets, defaultLabel);
        Populate(DestCredBox, targets, defaultLabel);

        AutoSelectCredential(SourceCredBox, SourceBox.Text);
        AutoSelectCredential(DestCredBox, DestBox.Text);
    }

    private static void Populate(ComboBox box, string[] targets, string? defaultLabel)
    {
        box.Items.Clear();
        box.Items.Add(new ComboBoxItem { Content = "Current user", Tag = null });
        if (defaultLabel is not null)
            box.Items.Add(new ComboBoxItem { Content = defaultLabel, Tag = CredentialTarget.DefaultTarget });
        foreach (var t in targets)
            box.Items.Add(new ComboBoxItem { Content = CredentialTarget.Display(t), Tag = t });
        box.SelectedIndex = 0;
    }

    private static string? SelectedTarget(ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Tag as string;

    /// <summary>Selects the share-specific credential if one exists, else the default credential
    /// (for UNC paths), else current user.</summary>
    private void AutoSelectCredential(ComboBox box, string? path)
    {
        box.SelectedIndex = 0; // current user
        if (string.IsNullOrWhiteSpace(path) || !NetworkPath.IsUnc(path)) return;

        var target = CredentialTarget.For(path);
        foreach (var item in box.Items)
            if (item is ComboBoxItem ci && (string?)ci.Tag == target) { box.SelectedItem = ci; return; }

        if (_hasDefaultCredential)
            foreach (var item in box.Items)
                if (item is ComboBoxItem ci && (string?)ci.Tag == CredentialTarget.DefaultTarget) { box.SelectedItem = ci; return; }
    }

    private NetworkCredential? ResolveCredential(ComboBox box)
    {
        var target = SelectedTarget(box);
        return target is null ? null : _credentials.GetCredential(target);
    }

    // ── input events ──

    private void OnSourceChanged(object sender, TextChangedEventArgs e)
    {
        AutoSelectCredential(SourceCredBox, SourceBox.Text);
        UpdateModeReadout();
    }

    private void OnDestChanged(object sender, TextChangedEventArgs e)
    {
        AutoSelectCredential(DestCredBox, DestBox.Text);
        UpdateModeReadout();
    }

    private void OnDepthChanged(object sender, SelectionChangedEventArgs e) => UpdateHashVisibility();

    /// <summary>The Hash selector only appears at Full depth, and is read-only under Strict Mode.</summary>
    private void UpdateHashVisibility()
    {
        if (HashPanel is null) return;
        bool full = DepthBox.SelectedIndex == 2;
        HashPanel.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        HashPanel.IsEnabled = full && StrictSwitch?.IsChecked != true;
    }

    private void OnStrictChanged(object sender, RoutedEventArgs e) => ApplyStrictState();

    /// <summary>When Strict is on, force Full/SHA-256/ACL and disable those controls so the UI
    /// reflects what will actually run.</summary>
    private void ApplyStrictState()
    {
        if (StrictSwitch is null) return;
        bool strict = StrictSwitch.IsChecked == true;

        if (strict)
        {
            DepthBox.SelectedIndex = 2;  // Full
            HashBox.SelectedIndex = 2;   // SHA256
            AclSwitch.IsChecked = true;
        }

        DepthBox.IsEnabled = !strict;
        AclSwitch.IsEnabled = !strict;
        UpdateHashVisibility();
    }

    private void UpdateModeReadout()
    {
        if (ModeText is null) return;
        string Mode(string? p) => string.IsNullOrWhiteSpace(p)
            ? "—"
            : SmartFileEnumerator.PredictSource(p!) == EnumerationSource.Mft ? "MFT" : "SMB";
        ModeText.Text = $"Source → {Mode(SourceBox.Text)}   ·   Dest → {Mode(DestBox.Text)}";
    }

    private void OnBrowseSource(object sender, RoutedEventArgs e) => BrowseInto(SourceBox);
    private void OnBrowseDest(object sender, RoutedEventArgs e) => BrowseInto(DestBox);

    private static void BrowseInto(System.Windows.Controls.TextBox target)
    {
        var dialog = new OpenFolderDialog { Title = "Select folder" };
        if (dialog.ShowDialog() == true)
            target.Text = dialog.FolderName;
    }

    private VerifyOptions BuildOptions() => new()
    {
        Depth = (VerifyDepth)Math.Max(0, DepthBox.SelectedIndex),
        HashAlgorithm = (FileDriftHashAlgorithm)Math.Max(0, HashBox.SelectedIndex),
        IncludeAcl = AclSwitch.IsChecked == true,
        Threads = ResolveThreads(),
        ExcludePatterns = (ExcludeBox.Text ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        Strict = StrictSwitch.IsChecked == true,
    };

    // ── actions ──

    private async void OnVerifyClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetPaths(out var src, out var dst)) return;

        var options = BuildOptions();
        NetworkCredential? srcCred, dstCred;
        try
        {
            srcCred = ResolveCredential(SourceCredBox);
            dstCred = ResolveCredential(DestCredBox);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Credential error: {ex.Message}";
            return;
        }

        _cts = new CancellationTokenSource();
        SetRunning(true);
        _rows.Clear();
        ResetSummary();

        var progress = new Progress<VerifyProgress>(OnProgress);
        try
        {
            var result = await Task.Run(() => _engine.RunAsync(src, dst, options, srcCred, dstCred, progress, _cts.Token));
            ShowResult(result);
        }
        catch (OperationCanceledException) { StatusText.Text = "Cancelled."; ProgressBar.Value = 0; }
        catch (Exception ex) { StatusText.Text = $"Error: {ex.GetType().Name} — {ex.Message}"; }
        finally { SetRunning(false); _cts?.Dispose(); _cts = null; }
    }

    private async void OnPreflightClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetPaths(out var src, out var dst)) return;

        var options = BuildOptions();
        NetworkCredential? srcCred, dstCred;
        try
        {
            srcCred = ResolveCredential(SourceCredBox);
            dstCred = ResolveCredential(DestCredBox);
        }
        catch (Exception ex) { StatusText.Text = $"Credential error: {ex.Message}"; return; }

        _cts = new CancellationTokenSource();
        SetRunning(true);
        ResetSummary();
        var progress = new Progress<VerifyProgress>(OnProgress);

        try
        {
            var result = await Task.Run(() => _preflight.RunAsync(src, dst, options, srcCred, dstCred, progress, _cts.Token));
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = 100;
            MatchedText.Text = result.SourceFileCount?.ToString("N0") ?? "—";
            DifferentText.Text = result.DestFileCount?.ToString("N0") ?? "—";
            var issues = result.Issues.Count == 0 ? "no issues" : string.Join("; ", result.Issues);
            StatusText.Text = result.IsReady
                ? $"Preflight OK — source {result.SourceFileCount:N0} files / {Bytes(result.SourceTotalBytes)}, " +
                  $"dest {result.DestFileCount:N0} files / {Bytes(result.DestTotalBytes)}."
                : $"Preflight blocked — {issues}";
        }
        catch (OperationCanceledException) { StatusText.Text = "Cancelled."; }
        catch (Exception ex) { StatusText.Text = $"Error: {ex.GetType().Name} — {ex.Message}"; }
        finally { SetRunning(false); _cts?.Dispose(); _cts = null; }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusText.Text = "Cancelling…";
    }

    private bool TryGetPaths(out string src, out string dst)
    {
        src = SourceBox.Text?.Trim() ?? "";
        dst = DestBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(dst))
        {
            StatusText.Text = "Enter both a source and a destination path.";
            return false;
        }
        return true;
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

        foreach (var c in result.Comparisons
                     .OrderBy(c => c.Status == ComparisonStatus.Matched)
                     .ThenBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            _rows.Add(ComparisonRow.From(c));
        }

        var mode = _enumerator.Source == EnumerationSource.Mft ? "MFT" : "SMB";
        StatusText.Text =
            $"Done ({mode}) — {run.MatchedCount:N0} matched, {run.TotalDifferences:N0} differences " +
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
        PreflightButton.IsEnabled = !running;
        SourceBox.IsEnabled = DestBox.IsEnabled = DepthBox.IsEnabled = HashBox.IsEnabled =
            AclSwitch.IsEnabled = ThreadsBox.IsEnabled = ExcludeBox.IsEnabled =
            StrictSwitch.IsEnabled = SourceCredBox.IsEnabled = DestCredBox.IsEnabled = !running;

        if (!running) ApplyStrictState(); // restore forced/disabled controls if Strict is on
    }

    private static string Bytes(long? bytes) => bytes is { } b ? FormatSize(b) : "?";

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
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
    }
}

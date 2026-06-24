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
    /// <summary>Cap on rows shown in the results grid. Differences only — matched files are summarised
    /// by the count tile, never listed (1M+ rows would freeze the grid).</summary>
    private const int MaxGridRows = 100_000;

    private readonly SmartFileEnumerator _enumerator = new();
    private readonly VerifyEngine _engine;
    private readonly PreflightEngine _preflight;
    private readonly ReconcileEngine _reconcile = new();
    private readonly ICredentialStore _credentials = new WindowsCredentialStore();
    private CancellationTokenSource? _cts;
    private bool _hasDefaultCredential;

    // Context of the last completed verify, so Reconcile/Preview can act on it.
    private VerifyResult? _lastResult;
    private string _lastSrc = "", _lastDst = "";
    private NetworkCredential? _lastSrcCred, _lastDstCred;

    public VerifyPage()
    {
        InitializeComponent();
        _engine = new VerifyEngine(_enumerator, new SqliteRunRepository());
        _preflight = new PreflightEngine(_enumerator);
        Loaded += OnLoaded;
        UpdateModeReadout();
    }

    private bool _loadedOnce;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // The page is cached and reused across navigation; run first-time setup only once so we don't
        // clobber the user's inputs (or a running verify) every time they return to this page.
        if (!_loadedOnce)
        {
            _loadedOnce = true;
            ThreadsBox.Text = VerifyOptions.DefaultThreads.ToString(); // 50% of logical processors
            ThreadsHint.Text = $"of {Environment.ProcessorCount} detected";
            UpdateHashVisibility();
        }

        // Pick up credentials added on the Credentials page since last visit (but not mid-run).
        if (!_running)
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

    // These fire on every keystroke with arbitrary partial path text; never let one crash the app.
    private void OnSourceChanged(object sender, TextChangedEventArgs e)
    {
        try
        {
            AutoSelectCredential(SourceCredBox, SourceBox.Text);
            UpdateModeReadout();
        }
        catch { /* partial/malformed path mid-type — ignore until it parses */ }
    }

    private void OnDestChanged(object sender, TextChangedEventArgs e)
    {
        try
        {
            AutoSelectCredential(DestCredBox, DestBox.Text);
            UpdateModeReadout();
        }
        catch { /* partial/malformed path mid-type — ignore until it parses */ }
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
            OwnerSwitch.IsChecked = true; // Strict enforces ownership too
        }

        DepthBox.IsEnabled = !strict;
        AclSwitch.IsEnabled = !strict;
        OwnerSwitch.IsEnabled = !strict;
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
        EnforceOwnership = OwnerSwitch.IsChecked == true,
        Threads = ResolveThreads(),
        ExcludePatterns = (ExcludeBox.Text ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        Strict = StrictSwitch.IsChecked == true,
        StartUtc = StartBox.SelectedDate is { } s ? VerifyOptions.StartOfLocalDayUtc(s) : null,
        EndUtc = EndBox.SelectedDate is { } en ? VerifyOptions.EndOfLocalDayUtc(en) : null,
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
        ResultsGrid.ItemsSource = null;
        ResetSummary();
        ClearLog();
        AppendLog($"Starting verify ({DescribeOptions(options)})");
        AppendLog($"Source: {src}");
        AppendLog($"Destination: {dst}");

        var progress = new Progress<VerifyProgress>(OnProgress);
        try
        {
            var result = await Task.Run(() => _engine.RunAsync(src, dst, options, srcCred, dstCred, progress, _cts.Token));
            // Project the grid rows off the UI thread: differences only, sorted, capped.
            var rows = await Task.Run(() => result.Comparisons
                .Where(c => c.Status != ComparisonStatus.Matched)
                .OrderBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Take(MaxGridRows)
                .Select(ComparisonRow.From)
                .ToList());
            ShowResult(result, rows);
            _lastResult = result;
            _lastSrc = src; _lastDst = dst;
            _lastSrcCred = srcCred; _lastDstCred = dstCred;
        }
        catch (OperationCanceledException) { StatusText.Text = "Cancelled."; ProgressBar.Value = 0; AppendLog("Cancelled."); }
        catch (Exception ex)
        {
            var m = $"Error: {ex.GetType().Name} — {ex.Message}";
            StatusText.Text = m;
            AppendLog(m);
        }
        finally { SetRunning(false); _cts?.Dispose(); _cts = null; }
    }

    private static string DescribeOptions(VerifyOptions o)
    {
        var parts = new List<string> { o.Depth.ToString().ToLowerInvariant() };
        if (o.Depth == VerifyDepth.Full) parts.Add(o.HashAlgorithm.ToString());
        if (o.IncludeAcl) parts.Add("ACLs");
        if (o.Strict) parts.Add("strict");
        parts.Add($"{o.Threads} threads");
        return string.Join(", ", parts);
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
        ClearLog();
        AppendLog("Starting preflight");
        AppendLog($"Source: {src}");
        AppendLog($"Destination: {dst}");
        var progress = new Progress<VerifyProgress>(OnProgress);

        try
        {
            var result = await Task.Run(() => _preflight.RunAsync(src, dst, options, srcCred, dstCred, progress, _cts.Token));
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = 100;

            // Repurpose the four tiles to show what preflight gathered.
            SetSummaryLabels("Source files", "Source size", "Dest files", "Dest size");
            MatchedText.Text   = result.SourceFileCount?.ToString("N0") ?? "—";
            DifferentText.Text = Bytes(result.SourceTotalBytes);
            MissingText.Text   = result.DestFileCount?.ToString("N0") ?? "—";
            ExtraText.Text     = Bytes(result.DestTotalBytes);

            foreach (var issue in result.Issues)
                AppendLog(issue);

            var summary = result.IsReady
                ? $"Preflight OK — source {result.SourceFileCount:N0} files / {Bytes(result.SourceTotalBytes)}, " +
                  $"dest {result.DestFileCount:N0} files / {Bytes(result.DestTotalBytes)}."
                : $"Preflight blocked — {(result.Issues.Count == 0 ? "see issues" : string.Join("; ", result.Issues))}";
            StatusText.Text = summary;
            AppendLog(summary);
        }
        catch (OperationCanceledException) { StatusText.Text = "Cancelled."; AppendLog("Cancelled."); }
        catch (Exception ex)
        {
            var m = $"Error: {ex.GetType().Name} — {ex.Message}";
            StatusText.Text = m;
            AppendLog(m);
        }
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
        var message = p.Message ?? p.Phase.ToString();
        StatusText.Text = message; // always current, even when the log line is throttled

        if (p.Total > 0)
        {
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = Math.Min(100, p.Processed * 100.0 / p.Total);
        }
        else
        {
            ProgressBar.IsIndeterminate = true;
        }

        // The progress bar + status line above carry the live view, so the log only needs to be a
        // periodic record: throttle scan/enrich lines to one every LogThrottle. Phase changes always log.
        bool phaseChanged = p.Phase != _lastProgressPhase;
        _lastProgressPhase = p.Phase;
        if (!phaseChanged && DateTime.UtcNow - _lastLogAppendUtc < LogThrottle)
            return;

        AppendLog(p.Phase switch
        {
            VerifyPhase.EnumeratingSource => $"Source · {message}",
            VerifyPhase.EnumeratingDestination => $"Destination · {message}",
            _ => message,
        });
    }

    // ── activity log ──

    private static readonly TimeSpan LogThrottle = TimeSpan.FromSeconds(2);
    private string? _lastLogged;
    private VerifyPhase _lastProgressPhase = (VerifyPhase)(-1);
    private DateTime _lastLogAppendUtc = DateTime.MinValue;

    private void AppendLog(string line)
    {
        if (line == _lastLogged) return; // collapse consecutive duplicates
        _lastLogged = line;
        _lastLogAppendUtc = DateTime.UtcNow;

        ActivityLog.Items.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        while (ActivityLog.Items.Count > 1000)
            ActivityLog.Items.RemoveAt(0);
        ActivityLog.ScrollIntoView(ActivityLog.Items[^1]);
    }

    private void ClearLog()
    {
        ActivityLog.Items.Clear();
        _lastLogged = null;
    }

    private void OnCopyLogClick(object sender, RoutedEventArgs e)
    {
        if (ActivityLog.Items.Count == 0) return;
        var text = string.Join(Environment.NewLine, ActivityLog.Items.Cast<string>());
        Clipboard.SetText(text);
    }

    // ── reconcile ──

    private ReconcilePlan? BuildPlanOrNull()
    {
        if (_lastResult is not { } r) return null;
        // ACL reconciliation is driven by whether the verify compared ACLs / enforced ownership.
        var plan = ReconcileEngine.BuildPlan(
            r.Comparisons, _lastDst, r.Run.Options.IncludeAcl, r.Run.Options.EnforceOwnership);
        return plan.TotalActions == 0 ? null : plan;
    }

    private void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        if (BuildPlanOrNull() is not { } plan)
        {
            StatusText.Text = "Nothing to reconcile — no missing or different files.";
            return;
        }

        AppendLog($"── Preview: {plan.CopyCount:N0} to copy, {plan.OverwriteCount:N0} to overwrite" +
                  $"{(plan.ClobberCount > 0 ? $", {plan.ClobberCount:N0} newer at dest" : "")}" +
                  $"{(plan.AclCount > 0 ? $", {plan.AclCount:N0} ACLs" : "")}, {FormatSize(plan.TotalBytes)} total ──");
        foreach (var a in plan.Actions)
        {
            var warn = a.ClobbersNewer ? "  ⚠ dest newer" : "";
            AppendLog($"[preview] {ReconcileEngine.ActionVerb(a),-13} {a.RelativePath}  ({FormatSize(a.SizeBytes)}){warn}");
        }
        StatusText.Text = $"Preview: would copy {plan.CopyCount:N0}, overwrite {plan.OverwriteCount:N0}" +
                          $"{(plan.AclCount > 0 ? $", set {plan.AclCount:N0} ACLs" : "")} " +
                          $"({FormatSize(plan.TotalBytes)}). No changes made.";
    }

    private async void OnReconcileClick(object sender, RoutedEventArgs e)
    {
        if (BuildPlanOrNull() is not { } plan)
        {
            StatusText.Text = "Nothing to reconcile — no missing or different files.";
            return;
        }

        var msg = $"Reconcile will copy source → destination:\n\n" +
                  $"    • Copy {plan.CopyCount:N0} missing file(s)\n" +
                  $"    • Overwrite {plan.OverwriteCount:N0} differing file(s)\n" +
                  (plan.DirCreateCount > 0 ? $"    • Create {plan.DirCreateCount:N0} missing folder(s)\n" : "") +
                  (plan.AclCount > 0 ? $"    • Add source permissions (ACLs) to {plan.AclCount:N0} item(s)\n" : "") +
                  $"\nTotal to write: {FormatSize(plan.TotalBytes)}.\n" +
                  $"The source is the source of truth — nothing on the destination is deleted or removed " +
                  $"(ACLs are added, never stripped).";
        if (plan.ClobberCount > 0)
            msg += $"\n\n⚠ WARNING: {plan.ClobberCount:N0} of the overwrites replace destination files that are " +
                   $"NEWER than the source. Their newer content will be lost.";
        msg += "\n\nProceed?";

        var choice = MessageBox.Show(msg, "Confirm Reconcile",
            MessageBoxButton.YesNo,
            plan.ClobberCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question,
            MessageBoxResult.No);
        if (choice != MessageBoxResult.Yes) return;

        _cts = new CancellationTokenSource();
        SetRunning(true);
        ClearLog();
        AppendLog($"Reconcile: copying {plan.CopyCount:N0}, overwriting {plan.OverwriteCount:N0} " +
                  $"({FormatSize(plan.TotalBytes)}) source → destination");

        var progress = new Progress<ReconcileProgress>(p =>
        {
            if (p.Total > 0)
            {
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = Math.Min(100, p.Processed * 100.0 / p.Total);
            }
            StatusText.Text = $"Reconciling {p.Processed:N0}/{p.Total:N0}…";
            if (p.Message is { } m) AppendLog(m);
        });

        try
        {
            var result = await Task.Run(() => _reconcile.ExecuteAsync(
                plan, _lastSrc, _lastDst, _lastSrcCred, _lastDstCred, progress, _cts.Token));

            ProgressBar.Value = 100;
            var summary = $"Reconcile done — copied {result.Copied:N0}, overwrote {result.Overwritten:N0}, " +
                          $"{FormatSize(result.BytesCopied)} written" +
                          (result.DirectoriesCreated > 0 ? $", {result.DirectoriesCreated:N0} folders created" : "") +
                          (result.AclsApplied > 0 ? $", {result.AclsApplied:N0} ACLs updated" : "") +
                          (result.FailureCount > 0 ? $", {result.FailureCount:N0} failed." : ".");
            StatusText.Text = summary;
            AppendLog(summary);
            foreach (var f in result.Failures)
                AppendLog($"[FAILED] {f.RelativePath} — {f.Error}");

            // Destination changed; require a fresh verify before reconciling again.
            _lastResult = null;
            AppendLog("Re-run Verify to confirm the destination now matches.");
        }
        catch (OperationCanceledException) { StatusText.Text = "Reconcile cancelled."; ProgressBar.Value = 0; AppendLog("Cancelled."); }
        catch (Exception ex)
        {
            var m = $"Reconcile error: {ex.GetType().Name} — {ex.Message}";
            StatusText.Text = m;
            AppendLog(m);
        }
        finally { SetRunning(false); _cts?.Dispose(); _cts = null; }
    }

    private void ShowResult(VerifyResult result, List<ComparisonRow> rows)
    {
        var run = result.Run;
        ProgressBar.IsIndeterminate = false;
        ProgressBar.Value = 100;

        MatchedText.Text = run.MatchedCount.ToString("N0");
        DifferentText.Text = run.DifferentCount.ToString("N0");
        MissingText.Text = run.MissingAtDestCount.ToString("N0");
        ExtraText.Text = run.ExtraAtDestCount.ToString("N0");

        // Differences only — matched files live in the count tile, never the grid.
        ResultsGrid.ItemsSource = rows;

        var mode = _enumerator.Source == EnumerationSource.Mft ? "MFT" : "SMB";
        var summary =
            $"Done ({mode}) — {run.MatchedCount:N0} matched, {run.TotalDifferences:N0} differences " +
            $"across {run.TotalSourceFiles:N0} source / {run.TotalDestFiles:N0} dest files.";
        if (result.ExcludedNewerCount > 0)
            summary += $"  ({result.ExcludedNewerCount:N0} newer dest files excluded by as-of filter.)";
        if (run.TotalDifferences > rows.Count)
            summary += $"  (Grid shows first {rows.Count:N0} of {run.TotalDifferences:N0} differences.)";
        StatusText.Text = summary;
        AppendLog(summary);
    }

    private void ResetSummary()
    {
        MatchedText.Text = DifferentText.Text = MissingText.Text = ExtraText.Text = "—";
        SetSummaryLabels("Matched", "Different", "Missing at dest", "Extra at dest");
        ProgressBar.Value = 0;
    }

    private void SetSummaryLabels(string a, string b, string c, string d)
    {
        MatchedLabel.Text = a;
        DifferentLabel.Text = b;
        MissingLabel.Text = c;
        ExtraLabel.Text = d;
    }

    private bool _running;

    private void SetRunning(bool running)
    {
        _running = running;
        VerifyButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        PreflightButton.IsEnabled = !running;
        SourceBox.IsEnabled = DestBox.IsEnabled = DepthBox.IsEnabled = HashBox.IsEnabled =
            AclSwitch.IsEnabled = ThreadsBox.IsEnabled = ExcludeBox.IsEnabled =
            StrictSwitch.IsEnabled = SourceCredBox.IsEnabled = DestCredBox.IsEnabled =
            OwnerSwitch.IsEnabled = StartBox.IsEnabled = EndBox.IsEnabled = !running;

        if (running)
        {
            PreviewButton.IsEnabled = ReconcileButton.IsEnabled = false;
        }
        else
        {
            ApplyStrictState();      // restore forced/disabled controls if Strict is on
            UpdateReconcileState();  // re-enable Reconcile/Preview if the last verify is actionable
        }
    }

    /// <summary>Enables Preview/Reconcile when the last completed verify has files to copy or overwrite.</summary>
    private void UpdateReconcileState()
    {
        bool actionable = _lastResult is { } r &&
            r.Run.MissingAtDestCount + r.Run.DifferentCount > 0;
        PreviewButton.IsEnabled = ReconcileButton.IsEnabled = actionable;
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
            var diff = c.Differences == FileDifference.None ? "" : c.Differences.ToString();
            if (c.AclDetail is { } detail) diff += $" ({detail})"; // e.g. "Acl (1 missing on dest, 1 extra on dest)"
            return new ComparisonRow(
                c.RelativePath,
                FormatStatus(c.Status),
                diff,
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

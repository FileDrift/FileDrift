using System.Net;
using System.Windows;
using System.Windows.Controls;
using FileDrift.App.Settings;
using FileDrift.Core.Engine;
using FileDrift.Core.Interfaces;
using FileDrift.Core.Models;
using FileDrift.Core.Persistence;
using Microsoft.Win32;

namespace FileDrift.App.Pages;

public partial class VerifyPage : Page
{
    /// <summary>Cap on rows shown in the results grid. The grid now virtualizes properly (its page has a
    /// bounded height), so large sets render instantly; this cap just bounds the projection/memory.
    /// Reconcile still acts on ALL differences, and the full list is always written to the run log.</summary>
    private const int MaxGridRows = 50_000;

    private readonly SmartFileEnumerator _enumerator = new();
    private readonly VerifyEngine _engine;
    private readonly PreflightEngine _preflight;
    private readonly ReconcileEngine _reconcile = new();
    private readonly ICredentialStore _credentials = new WindowsCredentialStore();
    private CancellationTokenSource? _cts;            // hard cancel (abort + rollback)
    private CancellationTokenSource? _softStop;       // soft stop (finish current, stop before next) — reconcile only
    private bool _reconcileRunning;                    // OnCancelClick prompts only during a reconcile
    private bool _hasDefaultCredential;

    // Context of the last completed verify, so Reconcile/Preview can act on it. We retain ONLY the
    // differences (not the matched comparisons) so a large ACL run — where every record carries a full
    // SDDL — doesn't pin multiple GB of matched data in memory after the run.
    private List<ComparisonResult>? _lastDiffs;
    private RunRecord? _lastRun;
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
            UpdateAclScopeEnabled();
        }

        // Pick up credentials added on the Credentials page since last visit (but not mid-run).
        if (!_running)
            RefreshCredentialCombos();

        // The page is hosted inside WPF-UI's content ScrollViewer, which gives it infinite height — so
        // the results DataGrid never virtualizes and arranges every row (a multi-second freeze on each
        // display). Give the grid a STABLE bounded height tied to the window so it always virtualizes.
        // (Binding to the ScrollViewer's ViewportHeight doesn't work — it lags at 0 on the first pass.)
        if (Window.GetWindow(this) is { } win)
        {
            win.SizeChanged -= OnHostSizeChanged;
            win.SizeChanged += OnHostSizeChanged;
        }
        UpdateResultsMaxHeight();
    }

    private void OnHostSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateResultsMaxHeight();

    /// <summary>Caps the results grid height to the visible area so it virtualizes. The offset leaves
    /// room for the controls above; if it is slightly off the page just scrolls — there is no freeze.</summary>
    private void UpdateResultsMaxHeight()
    {
        double host = Window.GetWindow(this)?.ActualHeight ?? 640;
        ResultsGrid.MaxHeight = Math.Max(120, host - 480);
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

    private void OnAclChanged(object sender, RoutedEventArgs e) => UpdateAclScopeEnabled();

    /// <summary>The ACL-scope dropdown only matters with Compare ACLs on, and Strict forces the full scope.</summary>
    private void UpdateAclScopeEnabled()
    {
        if (AclScopeBox is null || AclSwitch is null || StrictSwitch is null) return;
        AclScopeBox.IsEnabled = AclSwitch.IsChecked == true && StrictSwitch.IsChecked != true;
    }

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
            OwnerSwitch.IsChecked = true;     // Strict enforces ownership too
            AclScopeBox.SelectedIndex = 0;    // Strict is complete: files + folders
        }

        DepthBox.IsEnabled = !strict;
        AclSwitch.IsEnabled = !strict;
        OwnerSwitch.IsEnabled = !strict;
        UpdateHashVisibility();
        UpdateAclScopeEnabled();
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
        AclScope = AclScopeBox.SelectedIndex == 1 ? AclScope.FoldersOnly : AclScope.FilesAndFolders,
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
        StartRunLog("verify", src, dst);
        AppendLog($"Starting verify ({DescribeOptions(options)})");
        AppendLog($"Source: {src}");
        AppendLog($"Destination: {dst}");

        var progress = new Progress<VerifyProgress>(OnProgress);
        try
        {
            var result = await Task.Run(() => _engine.RunAsync(src, dst, options, srcCred, dstCred, progress, _cts.Token));
            // Off the UI thread: keep only the differences (all the grid and Reconcile need), and project
            // the grid rows. Dropping the matched comparisons lets their (SDDL-bearing) records be GC'd.
            var (diffs, rows) = await Task.Run(() =>
            {
                var d = result.Comparisons.Where(c => c.Status != ComparisonStatus.Matched).ToList();
                var r = d.OrderBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase)
                         .Take(MaxGridRows)
                         .Select(ComparisonRow.From)
                         .ToList();
                return (d, r);
            });
            ShowResult(result, rows);
            LogDifferences(diffs); // complete list to the log file (grid is capped)
            _lastDiffs = diffs;
            _lastRun = result.Run;
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
        finally { EndRunLog(); SetRunning(false); _cts?.Dispose(); _cts = null; }
    }

    private static string DescribeOptions(VerifyOptions o)
    {
        var parts = new List<string> { o.Depth.ToString().ToLowerInvariant() };
        if (o.Depth == VerifyDepth.Full) parts.Add(o.HashAlgorithm.ToString());
        if (o.IncludeAcl) parts.Add(o.AclScope == AclScope.FoldersOnly ? "ACLs (folders only)" : "ACLs");
        if (o.EnforceOwnership) parts.Add("owner");
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
        StartRunLog("preflight", src, dst);
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
        finally { EndRunLog(); SetRunning(false); _cts?.Dispose(); _cts = null; }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        // Reconcile writes files, so offer a choice; verify/preflight are read-only and just stop.
        if (_reconcileRunning)
        {
            var choice = MessageBox.Show(
                "A file is currently being copied. How do you want to stop?\n\n" +
                "Yes  —  Stop now: abort the current file and delete its partial copy.\n" +
                "No  —  Finish the current file, then stop.\n" +
                "Cancel  —  Keep going (don't stop).",
                "Stop Reconcile", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);

            if (choice == MessageBoxResult.Yes) { _cts?.Cancel(); StatusText.Text = "Stopping now…"; }
            else if (choice == MessageBoxResult.No) { _softStop?.Cancel(); StatusText.Text = "Finishing current file, then stopping…"; }
            // Cancel / dismissed → keep going
        }
        else
        {
            _cts?.Cancel();
            StatusText.Text = "Cancelling…";
        }
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

        var line = p.Phase switch
        {
            VerifyPhase.EnumeratingSource => $"Source · {message}",
            VerifyPhase.EnumeratingDestination => $"Destination · {message}",
            _ => message,
        };

        // The file log gets every report (complete record). The on-screen log only needs to be a
        // periodic record since the progress bar + status carry the live view: throttle to one every
        // LogThrottle, but always show the first line of a new phase.
        LogToFile(line);
        bool phaseChanged = p.Phase != _lastProgressPhase;
        _lastProgressPhase = p.Phase;
        if (phaseChanged || DateTime.UtcNow - _lastLogAppendUtc >= RuntimeOptions.LogThrottle)
            AppendScreen(line);
    }

    // ── activity log ──

    private string? _lastLogged;
    private VerifyPhase _lastProgressPhase = (VerifyPhase)(-1);
    private DateTime _lastLogAppendUtc = DateTime.MinValue;
    private RunLogger? _runLogger;
    private string? _lastLogPath;

    /// <summary>Logs an important line to both the on-screen log and the run's file log.</summary>
    private void AppendLog(string line)
    {
        LogToFile(line);
        AppendScreen(line);
    }

    /// <summary>Writes to the complete, unthrottled per-run file log (if a run is active).</summary>
    private void LogToFile(string line) => _runLogger?.Write(line);

    /// <summary>Appends to the throttled on-screen activity log.</summary>
    private void AppendScreen(string line)
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

    /// <summary>Writes the complete difference list to the run's file log (the grid is capped).</summary>
    private void LogDifferences(List<ComparisonResult> diffs)
    {
        if (_runLogger is null || diffs.Count == 0) return;
        var lines = new List<string>(diffs.Count + 2) { "", $"── All differences ({diffs.Count:N0}) ──" };
        foreach (var c in diffs.OrderBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var detail = c.AclDetail is { } d ? $" ({d})" : "";
            lines.Add($"  {c.Status,-14} {c.RelativePath}  [{c.Differences}{detail}]");
        }
        _runLogger.WriteMany(lines);
    }

    private void StartRunLog(string verb, string src, string dst)
    {
        _runLogger = RunLogger.Start(verb, src, dst);
        _lastLogPath = _runLogger.FilePath;
        OpenLogButton.IsEnabled = _lastLogPath is not null; // openable during and after the run
    }

    private void OnOpenLogClick(object sender, RoutedEventArgs e)
    {
        if (_lastLogPath is not { } path) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = $"Couldn't open log: {ex.Message}"; }
    }

    private void EndRunLog()
    {
        if (_runLogger is { } logger)
        {
            if (logger.FilePath is { } path) AppendScreen($"Activity log saved to {path}");
            logger.Dispose();
            _runLogger = null;
        }
    }

    private void OnCopyLogClick(object sender, RoutedEventArgs e)
    {
        if (ActivityLog.Items.Count == 0) return;
        var text = string.Join(Environment.NewLine, ActivityLog.Items.Cast<string>());
        Clipboard.SetText(text);
    }

    // ── reconcile ──

    // Built off the UI thread — over ~38k diffs (each parsing folder SDDL) it's a noticeable pause.
    private async Task<ReconcilePlan?> BuildPlanOrNullAsync()
    {
        if (_lastDiffs is not { } diffs || _lastRun is not { } run) return null;
        // ACL reconciliation is driven by whether the verify compared ACLs / enforced ownership.
        var plan = await Task.Run(() => ReconcileEngine.BuildPlan(
            diffs, _lastDst, run.Options.IncludeAcl, run.Options.EnforceOwnership));
        return plan.TotalActions == 0 ? null : plan;
    }

    /// <summary>One detailed preview line for an action, including the explicit ACEs / owner it would apply.</summary>
    private static string DescribeAction(ReconcileAction a)
    {
        var s = $"{ReconcileEngine.ActionVerb(a),-13} {a.RelativePath}";
        if (a.CopyContent) s += $"  ({FormatSize(a.SizeBytes)})";
        if (a.ClobbersNewer) s += "  ⚠ dest newer";
        if (a.AddExplicitAces is { Count: > 0 } aces) s += $"  +ACE: {string.Join("  |  ", aces.Select(AclReadable.Ace))}";
        if (a.SetOwnerSid is { } owner) s += $"  owner→{AclReadable.Trustee(owner)}";
        return s;
    }

    private async void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        if (await BuildPlanOrNullAsync() is not { } plan)
        {
            StatusText.Text = "Nothing to reconcile — no missing or different files.";
            return;
        }

        var summary = $"Preview: copy {plan.CopyCount:N0}, overwrite {plan.OverwriteCount:N0}" +
                      (plan.DirCreateCount > 0 ? $", create {plan.DirCreateCount:N0} folders" : "") +
                      (plan.ClobberCount > 0 ? $", {plan.ClobberCount:N0} newer at dest" : "") +
                      (plan.AclCount > 0 ? $", {plan.AclCount:N0} ACL change(s)" : "") +
                      $" — {FormatSize(plan.TotalBytes)} total. No changes made.";

        // Complete preview (every action + the ACEs/owner it applies) to a log file.
        StartRunLog("preview", _lastSrc, _lastDst);
        _runLogger?.Write(summary);
        if (_runLogger is not null)
        {
            var lines = new List<string>(plan.TotalActions + 1) { $"── Preview actions ({plan.TotalActions:N0}) ──" };
            lines.AddRange(plan.Actions.Select(a => "  " + DescribeAction(a)));
            _runLogger.WriteMany(lines);
        }

        // On screen: summary + first 50 actions; the rest is in the log file.
        const int screenCap = 50;
        AppendScreen($"── {summary} ──");
        foreach (var a in plan.Actions.Take(screenCap))
            AppendScreen("[preview] " + DescribeAction(a));
        if (plan.TotalActions > screenCap)
            AppendScreen($"… {plan.TotalActions - screenCap:N0} more — open the log file for the full preview.");

        EndRunLog(); // closes the file, shows its path, enables "Open log file"
        StatusText.Text = summary;
    }

    private async void OnReconcileClick(object sender, RoutedEventArgs e)
    {
        if (await BuildPlanOrNullAsync() is not { } plan)
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
                  $"Non-destructive: files that exist only on the destination are kept, and permissions are " +
                  $"only added — never removed. (Differing files are overwritten with the source.)";
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
        _softStop = new CancellationTokenSource();
        _reconcileRunning = true;
        SetRunning(true);
        ClearLog();
        StartRunLog("reconcile", _lastSrc, _lastDst);
        AppendLog($"Reconcile: copying {plan.CopyCount:N0}, overwriting {plan.OverwriteCount:N0} " +
                  $"({FormatSize(plan.TotalBytes)}) source → destination");

        var progress = new Progress<ReconcileProgress>(p =>
        {
            if (p.TotalBytes > 0)
            {
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = Math.Min(100, p.BytesCopied * 100.0 / p.TotalBytes);
            }
            StatusText.Text = $"Reconciling {p.Processed:N0}/{p.Total:N0} — {FormatSize(p.BytesCopied)} / {FormatSize(p.TotalBytes)}";
            if (p.Message is { } m)
            {
                LogToFile(m); // every action to the file log
                if (DateTime.UtcNow - _lastLogAppendUtc >= RuntimeOptions.LogThrottle) AppendScreen(m); // throttled on screen
            }
        });

        try
        {
            var result = await Task.Run(() => _reconcile.ExecuteAsync(
                plan, _lastSrc, _lastDst, _lastSrcCred, _lastDstCred, progress, _cts.Token, _softStop.Token));

            ProgressBar.Value = result.Stopped ? ProgressBar.Value : 100;
            var verb = result.Stopped ? "Reconcile stopped" : "Reconcile done";
            var summary = $"{verb} — copied {result.Copied:N0}, overwrote {result.Overwritten:N0}, " +
                          $"{FormatSize(result.BytesCopied)} written" +
                          (result.DirectoriesCreated > 0 ? $", {result.DirectoriesCreated:N0} folders created" : "") +
                          (result.AclsApplied > 0 ? $", {result.AclsApplied:N0} ACLs updated" : "") +
                          (result.PartialsRemoved > 0 ? $", {result.PartialsRemoved:N0} partial removed" : "") +
                          (result.FailureCount > 0 ? $", {result.FailureCount:N0} failed." : ".");
            StatusText.Text = summary;
            AppendLog(summary);
            foreach (var f in result.Failures)
                AppendLog($"[FAILED] {f.RelativePath} — {f.Error}");

            // Destination changed; require a fresh verify before reconciling again.
            _lastDiffs = null;
            _lastRun = null;
            AppendLog("Re-run Verify to confirm the destination now matches.");
        }
        catch (Exception ex)
        {
            var m = $"Reconcile error: {ex.GetType().Name} — {ex.Message}";
            StatusText.Text = m;
            AppendLog(m);
        }
        finally
        {
            _reconcileRunning = false;
            EndRunLog(); SetRunning(false);
            _cts?.Dispose(); _cts = null;
            _softStop?.Dispose(); _softStop = null;
        }
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
            $"across {run.TotalSourceFiles:N0} source / {run.TotalDestFiles:N0} dest entries.";
        if (result.ExcludedNewerCount > 0)
            summary += $"  ({result.ExcludedNewerCount:N0} newer dest files excluded by as-of filter.)";
        if (run.Options is { IncludeAcl: true, AclScope: AclScope.FoldersOnly })
            summary += "  (ACL scope: folders only — file permissions not checked.)";
        if (run.TotalDifferences > rows.Count)
            summary += $"  (Grid shows first {rows.Count:N0} of {run.TotalDifferences:N0} differences — full list in the log file.)";
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
            OwnerSwitch.IsEnabled = AclScopeBox.IsEnabled = StartBox.IsEnabled = EndBox.IsEnabled = !running;

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
        bool actionable = _lastRun is { } run &&
            run.MissingAtDestCount + run.DifferentCount > 0;
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

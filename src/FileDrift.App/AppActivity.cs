namespace FileDrift.App;

/// <summary>App-wide flags for whether a long operation is running, so the main window can warn before
/// closing mid-operation (set by the Verify page; read by the window's Closing handler).</summary>
public static class AppActivity
{
    /// <summary>True while a verify, preflight, or reconcile is running.</summary>
    public static bool OperationRunning { get; set; }

    /// <summary>True while a reconcile is running — it writes files, so closing warrants a stronger warning.</summary>
    public static bool ReconcileRunning { get; set; }

    /// <summary>Set by the Verify page: on app close during a reconcile, runs the same 3-way stop prompt
    /// and waits for the reconcile to actually stop (cleanup / finish current file). Returns true if the
    /// app may close, false if the user chose to keep running.</summary>
    public static Func<Task<bool>>? RequestReconcileStopForClose;
}

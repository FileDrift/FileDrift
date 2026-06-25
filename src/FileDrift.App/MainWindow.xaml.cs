using FileDrift.App.Settings;
using Wpf.Ui.Controls;

namespace FileDrift.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        WindowPlacement.Restore(this, SettingsStore.Load()); // size/position from last session
        // Cache the Verify page so a running verify survives navigation to Settings/History and back.
        RootNavigation.SetPageProviderService(new PageProvider());
        Loaded += (_, _) =>
        {
            RootNavigation.Navigate(typeof(Pages.VerifyPage));
            AppearanceApplier.ApplyWindowChrome(SettingsStore.Load()); // window now exists
        };
        Closing += OnWindowClosing;
    }

    private bool _forceClose;
    private bool _handlingClose; // guards against re-entrant close attempts while a prompt/stop is in flight

    private async void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Warn before closing mid-operation — a reconcile is writing files, so it's the worse one to interrupt.
        if (!_forceClose && AppActivity.OperationRunning)
        {
            e.Cancel = true; // must be set before the first await, or the close already proceeded
            if (_handlingClose) return;
            _handlingClose = true;
            try
            {
                bool mayClose;
                if (AppActivity.ReconcileRunning && AppActivity.RequestReconcileStopForClose is { } stopForClose)
                    // Reuse the reconcile 3-way stop workflow, and wait for it to actually stop before closing.
                    mayClose = await stopForClose();
                else
                    mayClose = await Dialogs.ConfirmAsync("Close FileDrift",
                        "An operation is still running. Closing now will stop it.",
                        confirmText: "Close anyway", danger: true, cancelText: "Keep running");

                if (mayClose)
                {
                    _forceClose = true;
                    Close(); // re-enters this handler; _forceClose lets it through to save + close
                }
            }
            finally { _handlingClose = false; }
            return;
        }

        var settings = SettingsStore.Load();
        WindowPlacement.Save(this, settings);
        SettingsStore.Save(settings);
    }
}

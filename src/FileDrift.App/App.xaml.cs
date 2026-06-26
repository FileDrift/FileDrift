using System.Windows;
using FileDrift.App.Settings;

namespace FileDrift.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // GUI only — the CLI lives in the separate console executable (FileDrift-CLI.exe).
        var settings = SettingsStore.Load();
        RuntimeOptions.SetLogThrottle(settings.LogThrottleSeconds);
        AppearanceApplier.Apply(settings);
        var window = new MainWindow();
        window.Show();
    }
}

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using FileDrift.App.Cli;

namespace FileDrift.App;

public partial class App : Application
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    private const int AttachParentProcess = -1;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length > 0)
        {
            // CLI mode: attach to the launching terminal and run headless.
            if (AttachConsole(AttachParentProcess))
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
                Console.WriteLine(); // separate output from the shell prompt
            }

            int exitCode = CliRunner.Run(e.Args);
            Shutdown(exitCode);
            return;
        }

        // GUI mode.
        var window = new MainWindow();
        window.Show();
    }
}

using System.IO;
using System.Runtime.InteropServices;

namespace FileDrift.App;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    // -1 = ATTACH_PARENT_PROCESS
    private const int AttachParentProcess = -1;

    [STAThread]
    internal static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            if (AttachConsole(AttachParentProcess))
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
                Console.WriteLine(); // newline after the shell prompt
            }
            return Cli.CliRunner.Run(args);
        }

        var app = new App();
        return app.Run(new MainWindow());
    }
}

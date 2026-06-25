using System.IO;
using FileDrift.Core.Persistence;

namespace FileDrift.App;

/// <summary>Writes a complete, unthrottled per-run activity log to %APPDATA%\FileDrift\logs.
/// Best-effort — any failure is swallowed so logging never breaks a run.</summary>
public sealed class RunLogger : IDisposable
{
    private readonly StreamWriter? _writer;

    public string? FilePath { get; }

    private RunLogger(StreamWriter? writer, string? path)
    {
        _writer = writer;
        FilePath = path;
    }

    public static RunLogger Start(string verb, string source, string dest)
    {
        try
        {
            var path = Path.Combine(AppPaths.LogsDirectory, $"{verb}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var writer = new StreamWriter(path, append: false) { AutoFlush = true };
            writer.WriteLine($"# FileDrift {verb} – {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            writer.WriteLine($"# Source:      {source}");
            writer.WriteLine($"# Destination: {dest}");
            writer.WriteLine();
            return new RunLogger(writer, path);
        }
        catch
        {
            return new RunLogger(null, null); // logging disabled, run continues
        }
    }

    public void Write(string line)
    {
        try { _writer?.WriteLine($"{DateTime.Now:HH:mm:ss}  {line}"); }
        catch { /* best-effort */ }
    }

    /// <summary>Writes many raw lines (no timestamp prefix) with a single flush — for bulk dumps
    /// like the full difference list, where per-line flushing would be slow.</summary>
    public void WriteMany(IEnumerable<string> lines)
    {
        if (_writer is null) return;
        try
        {
            foreach (var line in lines) _writer.WriteLine(line);
            _writer.Flush();
        }
        catch { /* best-effort */ }
    }

    public void Dispose()
    {
        try { _writer?.Dispose(); }
        catch { /* best-effort */ }
    }
}

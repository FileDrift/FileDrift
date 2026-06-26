using FileDrift.Core.Models;

namespace FileDrift.Core.Tests;

/// <summary>A temp directory that best-effort deletes itself (clearing read-only attrs) on Dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fdtest_{Guid.NewGuid():N}");

    public TempDir() => Directory.CreateDirectory(Path);

    public string Sub(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { /* ignore */ }
            Directory.Delete(Path, recursive: true);
        }
        catch { /* best-effort */ }
    }
}

/// <summary>Collects IProgress reports synchronously (the engine calls Report inline), so tests need no delay.</summary>
internal sealed class SyncProgress<T> : IProgress<T>
{
    public List<T> Items { get; } = new();
    public void Report(T value) { lock (Items) Items.Add(value); }
}

internal static class Rec
{
    public static FileRecord File(string rel, long size, DateTime? lastWrite = null, string? fullPath = null) => new()
    {
        RelativePath = rel,
        FullPath = fullPath ?? @"X:\" + rel,
        SizeBytes = size,
        LastWriteTimeUtc = lastWrite ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        CreatedTimeUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        IsDirectory = false,
        Source = EnumerationSource.Smb,
    };
}

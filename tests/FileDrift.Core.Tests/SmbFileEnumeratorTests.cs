// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using FileDrift.Core.Engine;
using FileDrift.Core.Models;
using Xunit;

namespace FileDrift.Core.Tests;

public class SmbFileEnumeratorTests
{
    private static void Shell(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        p.WaitForExit();
    }

    [Fact]
    public async Task Does_not_follow_directory_junctions()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t.Sub("real"));
        File.WriteAllText(t.Sub("real", "file.txt"), "x");
        Shell("cmd", $"/c mklink /J \"{t.Sub("link")}\" \"{t.Sub("real")}\"");
        try
        {
            var files = new List<string>();
            await foreach (var r in new SmbFileEnumerator().EnumerateAsync(t.Path, new VerifyOptions()))
                if (!r.IsDirectory) files.Add(r.RelativePath);

            Assert.Single(files);
            Assert.Equal(@"real\file.txt", files[0], ignoreCase: true);
            Assert.DoesNotContain(files, f => f.StartsWith("link", StringComparison.OrdinalIgnoreCase));
        }
        finally { Shell("cmd", $"/c rmdir \"{t.Sub("link")}\""); }
    }

    [Fact]
    public async Task Tracks_inaccessible_directories_instead_of_silently_skipping()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t.Sub("open"));
        Directory.CreateDirectory(t.Sub("denied"));
        File.WriteAllText(t.Sub("open", "a.txt"), "x");
        File.WriteAllText(t.Sub("denied", "secret.txt"), "x");
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        Shell("icacls", $"\"{t.Sub("denied")}\" /deny \"{user}:(RX)\"");
        try
        {
            var enumerator = new SmbFileEnumerator();
            var files = new List<string>();
            await foreach (var r in enumerator.EnumerateAsync(t.Path, new VerifyOptions()))
                if (!r.IsDirectory) files.Add(r.RelativePath);

            Assert.DoesNotContain(files, f => f.EndsWith("secret.txt"));
            Assert.Contains(enumerator.InaccessiblePaths, p => p.EndsWith("denied"));
        }
        finally { Shell("icacls", $"\"{t.Sub("denied")}\" /remove:d \"{user}\""); }
    }
}

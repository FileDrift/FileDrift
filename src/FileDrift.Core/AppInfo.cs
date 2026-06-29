// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;

namespace FileDrift.Core;

/// <summary>Single source of the product version string, so every surface (and the certificate) reports
/// the same value. Prefers the informational version (carries the "-rcN" suffix) over the numeric one.</summary>
public static class AppInfo
{
    public static string Version { get; } = Resolve();

    private static string Resolve()
    {
        var asm = typeof(AppInfo).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            int plus = info.IndexOf('+'); // strip the SDK-appended source-revision hash
            return plus >= 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString(3) ?? "unknown";
    }
}

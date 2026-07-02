// SPDX-License-Identifier: GPL-3.0-or-later
using FileDrift.Core.Engine;
using FileDrift.Core.Interfaces;
using FileDrift.Core.Persistence;

namespace FileDrift.App;

/// <summary>App-wide shared service instances. Both are stateless per call (the repository opens a
/// pooled connection per operation; the credential store wraps Win32 calls), so one instance serves
/// every page — and the repository's schema-migration check runs once per launch instead of once per
/// page construction.</summary>
internal static class AppServices
{
    public static IRunRepository Repository { get; } = new SqliteRunRepository();

    public static ICredentialStore Credentials { get; } = new WindowsCredentialStore();
}

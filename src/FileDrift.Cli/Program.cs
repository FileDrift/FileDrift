// SPDX-License-Identifier: GPL-3.0-or-later
using FileDrift.Cli;

// Console entry point for the FileDrift CLI (FileDrift-CLI.exe). A real console-subsystem executable,
// so the shell waits for it, output is synchronous, and exit codes are reliable for scripting.
return await CliRunner.RunAsync(args);

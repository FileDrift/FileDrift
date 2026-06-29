using System.CommandLine;
using System.Net;
using System.Text;
using FileDrift.Core.Engine;

namespace FileDrift.Cli.Commands;

internal static class CredentialCommand
{
    internal static Command Build()
    {
        var cmd = new Command("credential",
            "Manage the credentials FileDrift uses for SMB shares (stored in Windows Credential Manager).");
        cmd.Add(BuildList());
        cmd.Add(BuildAdd());
        cmd.Add(BuildRemove());
        return cmd;
    }

    private static Command BuildList()
    {
        var cmd = new Command("list", "List the credentials FileDrift has saved (passwords are never shown).");
        cmd.SetAction((parseResult, _) =>
        {
            try
            {
                var store = CliServices.Credentials();
                var entries = store.ListTargets()
                    .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                    .Select(t => new { target = t, share = CredentialTarget.Display(t), user = store.GetCredential(t)?.UserName })
                    .ToArray();
                CliOutput.Write(new { verb = "credential.list", count = entries.Length, entries });
                return Task.FromResult(0);
            }
            catch (Exception ex) { return Task.FromResult(CliOutput.Error("credential.list", ex.Message, ex.GetType().Name)); }
        });
        return cmd;
    }

    private static Command BuildAdd()
    {
        var share = new Option<string>("--share") { Description = @"Share path the credential applies to (e.g. \\server\share); keyed to the share root." };
        var asDefault = new Option<bool>("--default") { Description = "Save as the fallback credential used for any share without its own entry." };
        var user = new Option<string>("--user") { Description = @"Username (DOMAIN\user). Prompted if omitted." };

        var cmd = new Command("add", "Save a credential. Prompts for the password — it is never echoed or passed as an argument.");
        cmd.Add(share); cmd.Add(asDefault); cmd.Add(user);
        cmd.SetAction((parseResult, _) =>
        {
            try
            {
                bool def = parseResult.GetValue(asDefault);
                var sharePath = parseResult.GetValue(share);
                bool shareGiven = !string.IsNullOrWhiteSpace(sharePath);
                if (def == shareGiven)
                    return Task.FromResult(CliOutput.Error("credential.add", "Specify either --share <path> or --default (not both)."));

                var target = def ? CredentialTarget.DefaultTarget : CredentialTarget.For(sharePath!);

                var userName = parseResult.GetValue(user);
                if (string.IsNullOrWhiteSpace(userName))
                {
                    Console.Error.Write(@"Username (DOMAIN\user): ");
                    userName = Console.ReadLine();
                }
                if (string.IsNullOrWhiteSpace(userName))
                    return Task.FromResult(CliOutput.Error("credential.add", "A username is required."));

                if (Console.IsInputRedirected)
                    return Task.FromResult(CliOutput.Error("credential.add", "Password entry needs an interactive console (stdin is redirected)."));

                Console.Error.Write("Password (hidden): ");
                var password = ReadHidden();

                CliServices.Credentials().SetCredential(target, new NetworkCredential(userName, password));
                CliOutput.Write(new { verb = "credential.add", status = "saved", target, share = CredentialTarget.Display(target), user = userName });
                return Task.FromResult(0);
            }
            catch (Exception ex) { return Task.FromResult(CliOutput.Error("credential.add", ex.Message, ex.GetType().Name)); }
        });
        return cmd;
    }

    private static Command BuildRemove()
    {
        var share = new Option<string>("--share") { Description = @"Share path whose credential to remove (e.g. \\server\share)." };
        var asDefault = new Option<bool>("--default") { Description = "Remove the fallback (default) credential." };

        var cmd = new Command("remove", "Remove a saved FileDrift credential.");
        cmd.Add(share); cmd.Add(asDefault);
        cmd.SetAction((parseResult, _) =>
        {
            try
            {
                bool def = parseResult.GetValue(asDefault);
                var sharePath = parseResult.GetValue(share);
                bool shareGiven = !string.IsNullOrWhiteSpace(sharePath);
                if (def == shareGiven)
                    return Task.FromResult(CliOutput.Error("credential.remove", "Specify either --share <path> or --default (not both)."));

                var target = def ? CredentialTarget.DefaultTarget : CredentialTarget.For(sharePath!);
                bool removed = CliServices.Credentials().DeleteCredential(target);
                CliOutput.Write(new { verb = "credential.remove", status = removed ? "removed" : "not-found", target });
                return Task.FromResult(removed ? 0 : 1);
            }
            catch (Exception ex) { return Task.FromResult(CliOutput.Error("credential.remove", ex.Message, ex.GetType().Name)); }
        });
        return cmd;
    }

    /// <summary>Reads a line from the console without echoing it (for password entry).</summary>
    private static string ReadHidden()
    {
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.Error.WriteLine(); break; }
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
            else if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
        return sb.ToString();
    }
}

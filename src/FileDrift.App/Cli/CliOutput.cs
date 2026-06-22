using System.Text.Json;

namespace FileDrift.App.Cli;

internal static class CliOutput
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Write(object value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
}

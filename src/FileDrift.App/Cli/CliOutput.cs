using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileDrift.App.Cli;

internal static class CliOutput
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Write(object value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    /// <summary>Writes a structured error object to stdout and returns a non-zero exit code.</summary>
    public static int Error(string verb, string message, string? detail = null)
    {
        Write(new { verb, status = "error", error = message, detail });
        return 1;
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using FileDrift.Core.Models;

namespace FileDrift.Core.Persistence;

/// <summary>Serializes <see cref="VerifyOptions"/> to/from the JSON stored in the runs table.
/// Enums are written as strings so reordering enum members never corrupts stored history.</summary>
internal static class VerifyOptionsJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(VerifyOptions options) =>
        JsonSerializer.Serialize(options, Options);

    public static VerifyOptions Deserialize(string json) =>
        JsonSerializer.Deserialize<VerifyOptions>(json, Options)
        ?? throw new InvalidDataException("Stored VerifyOptions JSON deserialized to null.");
}

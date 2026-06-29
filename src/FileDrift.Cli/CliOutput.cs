// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileDrift.Cli;

internal enum OutputFormat { Json, Table }

internal static class CliOutput
{
    /// <summary>Display format. JSON is the canonical/scriptable output; Table is for interactive lookups.
    /// Set once at startup (see CliRunner): Table when the console is interactive, JSON when redirected.</summary>
    public static OutputFormat Format { get; set; } = OutputFormat.Json;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Write(object value)
    {
        if (Format == OutputFormat.Table)
            WriteHuman(value, 0);
        else
            Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
    }

    /// <summary>Writes a structured error and returns a non-zero exit code.</summary>
    public static int Error(string verb, string message, string? detail = null)
    {
        if (Format == OutputFormat.Table)
            Console.Error.WriteLine($"error ({verb}): {message}{(detail is null ? "" : $" [{detail}]")}");
        else
            Console.WriteLine(JsonSerializer.Serialize(new { verb, status = "error", error = message, detail }, JsonOptions));
        return 1;
    }

    // ── human-readable rendering (reflection over the same result objects) ──

    private static void WriteHuman(object value, int indent)
    {
        var pad = new string(' ', indent);
        foreach (var prop in value.GetType().GetProperties())
        {
            var v = prop.GetValue(value);
            if (v is null) continue;
            var name = Title(prop.Name);

            if (v is string or null || IsScalar(v))
            {
                Console.WriteLine($"{pad}{name}: {Cell(v)}");
            }
            else if (v is IEnumerable list)
            {
                var items = list.Cast<object?>().Where(x => x is not null).Select(x => x!).ToList();
                Console.WriteLine($"{pad}{name}: ({items.Count})");
                WriteCollection(items, indent + 2);
            }
            else
            {
                Console.WriteLine($"{pad}{name}:");
                WriteHuman(v, indent + 2);
            }
        }
    }

    private static void WriteCollection(List<object> items, int indent)
    {
        if (items.Count == 0) return;
        var pad = new string(' ', indent);

        // Scalar elements (e.g. a list of issue strings): one per line.
        if (IsScalar(items[0]))
        {
            foreach (var it in items)
                Console.WriteLine($"{pad}- {Cell(it)}");
            return;
        }

        // Objects: render as an aligned table using the first element's properties as columns.
        var props = items[0].GetType().GetProperties();
        var headers = props.Select(p => Title(p.Name)).ToArray();
        var rows = items.Select(it => props.Select(p => Cell(p.GetValue(it))).ToArray()).ToList();
        // Cap each column so a long value (e.g. a UNC path) can't blow the table out to an unreadable
        // width. Over-long cells are middle-ellipsised, which keeps both the server head and the leaf
        // (the distinguishing part) visible. GUIDs (36) and timestamps (20) fit under the cap untouched.
        var widths = headers
            .Select((h, i) => Math.Min(MaxColumnWidth, Math.Max(h.Length, rows.Max(r => r[i].Length))))
            .ToArray();

        Console.WriteLine(pad + Join(headers, widths));
        Console.WriteLine(pad + string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var r in rows)
            Console.WriteLine(pad + Join(r, widths));
    }

    private const int MaxColumnWidth = 44;

    private static string Join(string[] cells, int[] widths) =>
        string.Join("  ", cells.Select((c, i) => Ellipsize(c, widths[i]).PadRight(widths[i]))).TrimEnd();

    /// <summary>Shortens a string to <paramref name="width"/> by dropping the middle and inserting "…",
    /// preserving the head and tail. Used only for table display; JSON keeps full values.</summary>
    private static string Ellipsize(string s, int width)
    {
        if (s.Length <= width) return s;
        if (width <= 1) return s[..width];
        int head = (width - 1 + 1) / 2; // bias the head one longer on odd widths
        int tail = width - 1 - head;
        return s[..head] + "…" + s[^tail..];
    }

    private static bool IsScalar(object v) =>
        v is string || v is Enum || v.GetType().IsPrimitive || v is decimal || v is DateTime || v is Guid;

    private static string Cell(object? v) => v switch
    {
        null => "",
        DateTime d => d.ToString("u"),
        _ => v.ToString() ?? "",
    };

    private static string Title(string name) => name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];
}

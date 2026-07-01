// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using FileDrift.Core.Interfaces;
using FileDrift.Core.Models;

namespace FileDrift.Core.Persistence;

/// <summary>Exports/imports run history as JSON, for archiving or moving history between machines. This is
/// a portability convenience, not a security boundary — the SQLite file it reads from is not itself
/// tamper-proof, so an imported file should be trusted the same way you'd trust any other admin export.</summary>
public static class HistoryExport
{
    public const string FormatId = "filedrift-history-export-v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed record ExportFile(string FormatId, DateTime ExportedAtUtc, string AppVersion, List<RunRecord> Runs);

    /// <summary>Serializes the given runs (typically the result of an <see cref="IRunRepository.ListAsync"/>
    /// call) to a JSON document, including every field — sign-off state included.</summary>
    public static string Export(IReadOnlyList<RunRecord> runs, string appVersion, DateTime exportedAtUtc) =>
        JsonSerializer.Serialize(new ExportFile(FormatId, exportedAtUtc, appVersion, runs.ToList()), JsonOptions);

    public enum RowOutcome { Imported, Updated, SkippedExists, SkippedProtected, Error }

    public sealed record ImportRow(Guid Id, string Source, string Dest, RowOutcome Outcome, string? Detail = null);

    public sealed record ImportSummary(int Imported, int Updated, int SkippedExists, int SkippedProtected,
        int Errors, IReadOnlyList<ImportRow> Rows);

    /// <summary>Imports runs from a previously exported JSON document.
    /// <para>Default (<paramref name="overwrite"/> = false): a run whose ID already exists locally is left
    /// untouched (reported as SkippedExists).</para>
    /// <para><paramref name="overwrite"/> = true: an existing run is overwritten by the imported one,
    /// EXCEPT when the existing local run is already signed off — that run is always protected and
    /// reported as SkippedProtected, so an import can never silently erase a local sign-off.</para></summary>
    public static async Task<ImportSummary> ImportAsync(
        IRunRepository repository, string json, bool overwrite, CancellationToken cancellationToken = default)
    {
        ExportFile? file;
        try { file = JsonSerializer.Deserialize<ExportFile>(json, JsonOptions); }
        catch (JsonException ex) { throw new InvalidDataException($"Not a valid FileDrift history export: {ex.Message}"); }

        if (file is null || !string.Equals(file.FormatId, FormatId, StringComparison.Ordinal))
            throw new InvalidDataException("Not a FileDrift history export (missing or unrecognized format marker).");

        var rows = new List<ImportRow>();
        int imported = 0, updated = 0, skippedExists = 0, skippedProtected = 0, errors = 0;

        foreach (var run in file.Runs)
        {
            try
            {
                var existing = await repository.GetAsync(run.Id, cancellationToken);
                if (existing is null)
                {
                    await repository.SaveAsync(run, cancellationToken);
                    imported++;
                    rows.Add(new ImportRow(run.Id, run.SourcePath, run.DestPath, RowOutcome.Imported));
                }
                else if (!overwrite)
                {
                    skippedExists++;
                    rows.Add(new ImportRow(run.Id, run.SourcePath, run.DestPath, RowOutcome.SkippedExists));
                }
                else if (existing.SignedOffAtUtc is not null)
                {
                    skippedProtected++;
                    rows.Add(new ImportRow(run.Id, run.SourcePath, run.DestPath, RowOutcome.SkippedProtected,
                        "Locally signed off; import cannot overwrite a signed-off run."));
                }
                else
                {
                    await repository.SaveAsync(run, cancellationToken);
                    updated++;
                    rows.Add(new ImportRow(run.Id, run.SourcePath, run.DestPath, RowOutcome.Updated));
                }
            }
            catch (Exception ex)
            {
                errors++;
                rows.Add(new ImportRow(run.Id, run.SourcePath, run.DestPath, RowOutcome.Error, ex.Message));
            }
        }

        return new ImportSummary(imported, updated, skippedExists, skippedProtected, errors, rows);
    }
}

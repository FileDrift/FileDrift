// SPDX-License-Identifier: GPL-3.0-or-later
namespace FileDrift.Core.Models;

public sealed class EnumerationProgress
{
    public required long FilesFound { get; init; }
    public required long BytesFound { get; init; }

    /// <summary>The directory currently being scanned; null during MFT enumeration (no directory-by-directory traversal).</summary>
    public string? CurrentDirectory { get; init; }
}

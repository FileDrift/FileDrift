namespace FileDrift.Core.Models;

/// <summary>Represents a single enumerated file entry from either an MFT or SMB scan.</summary>
public sealed class FileRecord
{
    public required string RelativePath { get; init; }
    public required string FullPath { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTime LastWriteTimeUtc { get; init; }
    public required DateTime CreatedTimeUtc { get; init; }
    public required bool IsDirectory { get; init; }
    public required EnumerationSource Source { get; init; }

    /// <summary>Populated only when hash checking was requested.</summary>
    public string? Hash { get; init; }

    /// <summary>Populated only when ACL checking was requested.</summary>
    public string? Owner { get; init; }

    /// <summary>SDDL-format security descriptor; populated only when ACL checking was requested.</summary>
    public string? SecurityDescriptor { get; init; }
}

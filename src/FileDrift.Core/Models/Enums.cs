namespace FileDrift.Core.Models;

public enum EnumerationSource { Smb, Mft }

public enum RunStatus { InProgress, Completed, Failed, Cancelled }

public enum VerifyDepth
{
    /// <summary>Name and size only.</summary>
    Quick,
    /// <summary>Name, size, and last-write timestamp.</summary>
    Standard,
    /// <summary>Name, size, timestamp, and content hash.</summary>
    Full
}

public enum FileDriftHashAlgorithm { MD5, SHA1, SHA256 }

/// <summary>Top-level outcome of comparing one file entry.</summary>
public enum ComparisonStatus { Matched, Different, MissingAtDest, ExtraAtDest }

/// <summary>Specific differences present when Status == Different.</summary>
[Flags]
public enum FileDifference
{
    None      = 0,
    Size      = 1 << 0,
    Timestamp = 1 << 1,
    Hash      = 1 << 2,
    Acl       = 1 << 3,
}

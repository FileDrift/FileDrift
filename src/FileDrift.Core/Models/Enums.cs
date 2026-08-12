// SPDX-License-Identifier: GPL-3.0-or-later
namespace FileDrift.Core.Models;

/// <summary>Which strategy enumerated a tree. <see cref="Graph"/> covers SharePoint Online and
/// OneDrive for Business (Microsoft Graph API), which is not a filesystem.</summary>
public enum EnumerationSource { Smb, Mft, Graph }

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

/// <summary><see cref="QuickXor"/> is Microsoft's quickXorHash — the only content hash Graph
/// reports for OneDrive/SharePoint files, so it is the only algorithm that can compare cloud
/// content against local content without downloading every file.</summary>
public enum FileDriftHashAlgorithm { MD5, SHA1, SHA256, QuickXor }

/// <summary>Which entries get their ACLs compared when ACL comparison is on.</summary>
public enum AclScope
{
    /// <summary>Compare ACLs on both files and folders (complete; reads every file's security descriptor).</summary>
    FilesAndFolders,
    /// <summary>Compare ACLs on folders only (fast — skips file ACL reads; misses a permission set directly on a file).</summary>
    FoldersOnly,
}

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

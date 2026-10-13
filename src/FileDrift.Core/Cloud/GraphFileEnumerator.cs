// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using FileDrift.Core.Configuration;
using FileDrift.Core.Interfaces;
using FileDrift.Core.Models;

namespace FileDrift.Core.Cloud;

/// <summary>
/// Enumerates a SharePoint Online / OneDrive for Business document library through Microsoft Graph,
/// yielding <see cref="FileRecord"/>s the existing comparison pipeline can consume unchanged.
/// </summary>
/// <remarks>
/// <para><b>Files only, no directory records</b> — matching <see cref="Engine.MftFileEnumerator"/>.
/// The SMB scanner emits directories only to compare ACLs, and ACL comparison is permanently refused
/// against a cloud path (SharePoint permissions are a different model from Windows DACLs), so there is
/// nothing a directory record would contribute. Consequence, same as MFT mode: an empty folder is not
/// an entry, so it is neither reported nor recreated.</para>
/// <para><b>Hashes come free.</b> Graph returns <c>file.hashes.quickXorHash</c> inline in child
/// listings, so a Full-depth verify costs no extra request per file. The hash is attached here, and
/// <see cref="Engine.VerifyEngine"/> uses a pre-populated hash rather than trying to open
/// <see cref="FileRecord.FullPath"/> — which for a cloud record is a browser URL, not an openable
/// file.</para>
/// <para><b>Recursive children, not delta.</b> Delta is drive-root scoped, so enumerating a subfolder
/// would mean pulling the whole library and filtering, and it carries extra semantics (deleted items,
/// token management) that buy nothing for a one-shot verify. Children listing is scoped exactly to the
/// requested subtree and pages predictably. Delta is the right optimisation for repeat runs later.</para>
/// </remarks>
public sealed class GraphFileEnumerator : IFileEnumerator
{
    private readonly GraphClient _client;
    private readonly GraphSettings _settings;
    private ConcurrentBag<string> _inaccessible = new();

    public GraphFileEnumerator(GraphClient client, GraphSettings settings)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public EnumerationSource Source => EnumerationSource.Graph;

    public IReadOnlyCollection<string> InaccessiblePaths => _inaccessible;

    /// <summary>The target resolved by the most recent enumeration, for reporting which library was
    /// actually read.</summary>
    public GraphTarget? ResolvedTarget { get; private set; }

    public async IAsyncEnumerable<FileRecord> EnumerateAsync(
        string rootPath,
        VerifyOptions options,
        IProgress<EnumerationProgress>? progress = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var inaccessible = _inaccessible = new(); // fresh per enumeration, like the SMB scanner
        var target = await GraphTargetResolver.ResolveAsync(_client, rootPath, cancellationToken)
            .ConfigureAwait(false);
        ResolvedTarget = target;

        var wantHashes = options.Depth >= VerifyDepth.Full;
        long files = 0, bytes = 0;

        // Explicit stack rather than recursion: a deep library should not risk the call stack, and this
        // keeps the async iterator flat.
        var pending = new Stack<string>();
        pending.Push(target.FolderPath);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = pending.Pop();

            progress?.Report(new EnumerationProgress
            {
                FilesFound = files,
                BytesFound = bytes,
                CurrentDirectory = DisplayPath(target, folder),
            });

            // Collected per folder so a 403 on one subtree does not abort the scan — the run instead
            // reports it as inaccessible, which drives the certificate to INCOMPLETE rather than
            // letting a partial scan look like a clean match.
            List<JsonNode>? items;
            try
            {
                items = await ListAllAsync(target.DriveId, folder, cancellationToken).ConfigureAwait(false);
            }
            catch (GraphApiException ex)
            {
                inaccessible.Add($"{DisplayPath(target, folder)} ({(int)ex.StatusCode})");
                continue;
            }

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var name = DriveItemJson.Name(item);
                if (string.IsNullOrEmpty(name)) continue;

                var childPath = string.IsNullOrEmpty(folder) ? name : $"{folder}/{name}";

                if (DriveItemJson.IsFolder(item))
                {
                    pending.Push(childPath);
                    continue;
                }

                // Exclude patterns are deliberately NOT applied here: VerifyEngine and PreflightEngine
                // own that filtering for every source, and duplicating it would diverge from how the
                // MFT and SMB enumerators behave.
                var relative = RelativeTo(target.FolderPath, childPath);

                var size = DriveItemJson.Size(item);
                var modified = DriveItemJson.LastModifiedUtc(item) ?? default;
                var created = DriveItemJson.CreatedUtc(item) ?? modified;

                files++;
                bytes += size;

                yield return new FileRecord
                {
                    RelativePath = relative,
                    // A browser URL, NOT an openable path. Nothing may hand this to a File API; the
                    // pre-populated Hash below is what keeps VerifyEngine from trying.
                    FullPath = item["webUrl"]?.GetValue<string>() ?? DisplayPath(target, childPath),
                    SizeBytes = size,
                    LastWriteTimeUtc = modified,
                    CreatedTimeUtc = created,
                    IsDirectory = false,
                    Source = EnumerationSource.Graph,
                    Hash = wantHashes ? HashFor(item, options.HashAlgorithm) : null,
                    // SharePoint permissions have no SDDL equivalent; ACL comparison is refused upstream.
                    Owner = null,
                    SecurityDescriptor = null,
                };
            }
        }

        progress?.Report(new EnumerationProgress
        {
            FilesFound = files,
            BytesFound = bytes,
            CurrentDirectory = null,
        });
    }

    /// <summary>Returns the hash matching the requested algorithm, or null when the service did not
    /// report one. Null means "could not prove equality", which the comparison treats as a difference —
    /// the safe direction.</summary>
    private static string? HashFor(JsonNode item, FileDriftHashAlgorithm algorithm)
    {
        var (quickXor, sha1, sha256) = DriveItemJson.Hashes(item);
        return algorithm switch
        {
            FileDriftHashAlgorithm.QuickXor => quickXor,
            FileDriftHashAlgorithm.SHA1 => sha1,
            FileDriftHashAlgorithm.SHA256 => sha256,
            // Graph never reports MD5. Rather than silently compare nothing, leave it null so the
            // mismatch is visible; the CLI/GUI steer Full-depth cloud verification to quickxor.
            _ => null,
        };
    }

    /// <summary>Fetches every page of a folder's children.</summary>
    private async Task<List<JsonNode>> ListAllAsync(
        string driveId, string folderPath, CancellationToken cancellationToken)
    {
        var url = string.IsNullOrEmpty(folderPath)
            ? $"drives/{driveId}/root/children?$top={_settings.PageSize}"
            : $"drives/{driveId}/root:/{GraphSites.EncodePath(folderPath)}:/children?$top={_settings.PageSize}";

        var all = new List<JsonNode>();
        while (true)
        {
            var page = await _client.GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
            if (page["value"]?.AsArray() is { } items)
                foreach (var item in items)
                    if (item is not null) all.Add(item);

            if (page["@odata.nextLink"]?.GetValue<string>() is not { Length: > 0 } next) break;
            url = next;
        }

        return all;
    }

    /// <summary>Path of <paramref name="itemPath"/> relative to the enumeration root, using backslash
    /// separators per the <see cref="IFileEnumerator"/> convention — Graph speaks forward slashes, and
    /// comparison keys on exact string equality, so this normalisation is load-bearing.</summary>
    private static string RelativeTo(string rootPath, string itemPath)
    {
        var relative = string.IsNullOrEmpty(rootPath)
            ? itemPath
            : itemPath[Math.Min(rootPath.Length + 1, itemPath.Length)..];
        return relative.Replace('/', '\\');
    }

    private static string DisplayPath(GraphTarget target, string folderPath)
    {
        var library = target.LibraryName ?? "library";
        return string.IsNullOrEmpty(folderPath) ? library : $"{library}/{folderPath}";
    }
}

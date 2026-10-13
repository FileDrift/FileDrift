// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.CompilerServices;
using FileDrift.Core.Interfaces;
using FileDrift.Core.Models;

namespace FileDrift.Core.Engine;

/// <summary>
/// Selects MFT enumeration for local NTFS volumes and SMB enumeration for all other paths.
/// Falls back to SMB automatically if MFT is unavailable (insufficient privileges or non-NTFS).
/// </summary>
public sealed class SmartFileEnumerator : IFileEnumerator, IEnumerationIdentity
{
    private readonly SmbFileEnumerator _smb = new();
    private readonly MftFileEnumerator _mft = new();

    // A factory, not an instance: building the cloud enumerator means loading Graph configuration and
    // an authenticated client, and a purely local run must not pay for that -- or fail because Graph
    // is unconfigured. Created on first use and cached, so InaccessiblePaths survives the call.
    private readonly Func<IFileEnumerator>? _cloudFactory;
    private IFileEnumerator? _cloud;

    public SmartFileEnumerator(Func<IFileEnumerator>? cloudEnumeratorFactory = null) =>
        _cloudFactory = cloudEnumeratorFactory;

    // Reflects the strategy used in the most recent EnumerateAsync call.
    private EnumerationSource _lastSource = EnumerationSource.Smb;
    public EnumerationSource Source => _lastSource;

    /// <summary>Forwarded from the cloud enumerator when one was used; null for a purely local run.</summary>
    public string? EnumerationIdentity => (_cloud as IEnumerationIdentity)?.EnumerationIdentity;

    public IReadOnlyCollection<string> InaccessiblePaths => _lastSource switch
    {
        EnumerationSource.Mft => _mft.InaccessiblePaths,
        EnumerationSource.Graph => _cloud?.InaccessiblePaths ?? [],
        _ => _smb.InaccessiblePaths,
    };

    public async IAsyncEnumerable<FileRecord> EnumerateAsync(
        string rootPath,
        VerifyOptions options,
        IProgress<EnumerationProgress>? progress = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (CloudPath.IsSharePoint(rootPath))
        {
            _lastSource = EnumerationSource.Graph;
            var cloud = _cloud ??= _cloudFactory?.Invoke()
                ?? throw new NotSupportedException(
                    "This build cannot enumerate SharePoint/OneDrive paths: no cloud enumerator was " +
                    "supplied. (A host that supports cloud targets wires one in; this is a wiring " +
                    "error rather than something to fix in configuration.)");

            await foreach (var record in cloud.EnumerateAsync(rootPath, options, progress, cancellationToken))
                yield return record;
            yield break;
        }

        // MFT enumeration is files-only; ACL comparison needs directory records (and is I/O-bound on
        // ACL reads anyway), so fall back to the directory-aware SMB scanner when comparing ACLs.
        if (!options.IncludeAcl && IsLocalNtfs(rootPath))
        {
            _lastSource = EnumerationSource.Mft;
            IAsyncEnumerable<FileRecord>? mftResult = null;
            try
            {
                // Materialise the first element to catch access-denied early
                mftResult = _mft.EnumerateAsync(rootPath, options, progress, cancellationToken);
            }
            catch (UnauthorizedAccessException) { }

            if (mftResult is not null)
            {
                bool fell = false;
                await using var enumerator = mftResult.GetAsyncEnumerator(cancellationToken);
                while (true)
                {
                    bool moved;
                    try   { moved = await enumerator.MoveNextAsync(); }
                    catch (UnauthorizedAccessException) { fell = true; break; }

                    if (!moved) yield break;
                    yield return enumerator.Current;
                }

                if (!fell) yield break;
                // Access denied mid-stream — fall through to SMB
            }
        }

        _lastSource = EnumerationSource.Smb;
        await foreach (var record in _smb.EnumerateAsync(rootPath, options, progress, cancellationToken))
            yield return record;
    }

    private static bool IsLocalNtfs(string path) => PredictSource(path) == EnumerationSource.Mft;

    /// <summary>Predicts which strategy a path will use: Graph for SharePoint/OneDrive URLs, MFT for
    /// local fixed NTFS volumes, SMB otherwise. MFT may still fall back to SMB at runtime if
    /// privileges are insufficient.</summary>
    public static EnumerationSource PredictSource(string path)
    {
        if (CloudPath.IsSharePoint(path))
            return EnumerationSource.Graph;

        if (string.IsNullOrEmpty(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            return EnumerationSource.Smb; // UNC path

        var root = Path.GetPathRoot(path);
        if (root is null) return EnumerationSource.Smb;

        try
        {
            var drive = new DriveInfo(root);
            return drive.DriveType == DriveType.Fixed
                && drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase)
                ? EnumerationSource.Mft
                : EnumerationSource.Smb;
        }
        catch
        {
            return EnumerationSource.Smb;
        }
    }
}

using System.Runtime.CompilerServices;
using FileDrift.Core.Interfaces;
using FileDrift.Core.Models;

namespace FileDrift.Core.Engine;

/// <summary>
/// Selects MFT enumeration for local NTFS volumes and SMB enumeration for all other paths.
/// Falls back to SMB automatically if MFT is unavailable (insufficient privileges or non-NTFS).
/// </summary>
public sealed class SmartFileEnumerator : IFileEnumerator
{
    private readonly SmbFileEnumerator _smb = new();
    private readonly MftFileEnumerator _mft = new();

    // Reflects the strategy used in the most recent EnumerateAsync call.
    private EnumerationSource _lastSource = EnumerationSource.Smb;
    public EnumerationSource Source => _lastSource;

    public async IAsyncEnumerable<FileRecord> EnumerateAsync(
        string rootPath,
        VerifyOptions options,
        IProgress<EnumerationProgress>? progress = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (IsLocalNtfs(rootPath))
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

    private static bool IsLocalNtfs(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return false; // UNC path

        var root = Path.GetPathRoot(path);
        if (root is null) return false;

        try
        {
            var drive = new DriveInfo(root);
            return drive.DriveType == DriveType.Fixed
                && drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

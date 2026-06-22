using System.Collections.Concurrent;
using FileDrift.Core.Interfaces;
using FileDrift.Core.Models;

namespace FileDrift.Core.Engine;

/// <summary>Orchestrates a full verify run: enumerate both trees, optionally enrich matched pairs
/// with hashes and ACLs, compare, tally, and persist the run record.</summary>
public sealed class VerifyEngine
{
    private readonly IFileEnumerator _enumerator;
    private readonly IRunRepository _repository;
    private readonly ComparisonEngine _comparison;
    private readonly FileHasher _hasher;
    private readonly AclReader _aclReader;

    public VerifyEngine(
        IFileEnumerator enumerator,
        IRunRepository repository,
        ComparisonEngine? comparison = null,
        FileHasher? hasher = null,
        AclReader? aclReader = null)
    {
        _enumerator = enumerator;
        _repository = repository;
        _comparison = comparison ?? new ComparisonEngine();
        _hasher     = hasher ?? new FileHasher();
        _aclReader  = aclReader ?? new AclReader();
    }

    public async Task<VerifyResult> RunAsync(
        string sourcePath,
        string destPath,
        VerifyOptions options,
        IProgress<VerifyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var run = new RunRecord
        {
            Id = Guid.NewGuid(),
            StartedAtUtc = DateTime.UtcNow,
            SourcePath = sourcePath,
            DestPath = destPath,
            Options = options,
            Status = RunStatus.InProgress,
        };
        await _repository.SaveAsync(run, cancellationToken);

        try
        {
            // Phase 1 & 2 — enumerate both trees
            var source = await EnumerateAsync(sourcePath, options, VerifyPhase.EnumeratingSource, progress, cancellationToken);
            var dest   = await EnumerateAsync(destPath, options, VerifyPhase.EnumeratingDestination, progress, cancellationToken);

            run.TotalSourceFiles = source.Count;
            run.TotalDestFiles   = dest.Count;

            // Phase 3 — enrich matched pairs (hash and/or ACL) in parallel
            if (options.Depth >= VerifyDepth.Full || options.IncludeAcl)
                await EnrichMatchedPairsAsync(source, dest, options, progress, cancellationToken);

            // Phase 4 — compare
            progress?.Report(new VerifyProgress { Phase = VerifyPhase.Comparing, Message = "Comparing trees" });
            var comparisons = _comparison.Compare(source.Values.ToArray(), dest.Values.ToArray(), options);

            Tally(run, comparisons);

            // Phase 5 — persist completed run
            run.Status = RunStatus.Completed;
            run.CompletedAtUtc = DateTime.UtcNow;
            progress?.Report(new VerifyProgress { Phase = VerifyPhase.Persisting, Message = "Saving run to history" });
            await _repository.SaveAsync(run, cancellationToken);

            progress?.Report(new VerifyProgress
            {
                Phase = VerifyPhase.Done,
                Processed = run.MatchedCount + run.TotalDifferences,
                Total = run.MatchedCount + run.TotalDifferences,
                Message = $"Done — {run.MatchedCount} matched, {run.TotalDifferences} differences",
            });

            return new VerifyResult { Run = run, Comparisons = comparisons };
        }
        catch (OperationCanceledException)
        {
            run.Status = RunStatus.Cancelled;
            run.CompletedAtUtc = DateTime.UtcNow;
            await SaveQuietlyAsync(run);
            throw;
        }
        catch
        {
            run.Status = RunStatus.Failed;
            run.CompletedAtUtc = DateTime.UtcNow;
            await SaveQuietlyAsync(run);
            throw;
        }
    }

    private async Task<ConcurrentDictionary<string, FileRecord>> EnumerateAsync(
        string rootPath,
        VerifyOptions options,
        VerifyPhase phase,
        IProgress<VerifyProgress>? progress,
        CancellationToken ct)
    {
        var map = new ConcurrentDictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
        long count = 0;

        var enumProgress = new Progress<EnumerationProgress>(p =>
            progress?.Report(new VerifyProgress
            {
                Phase = phase,
                Processed = p.FilesFound,
                Message = p.CurrentDirectory is { } dir ? $"Scanning {dir}" : "Scanning",
            }));

        await foreach (var record in _enumerator.EnumerateAsync(rootPath, options, enumProgress, ct))
        {
            map[record.RelativePath] = record;
            count++;
        }

        progress?.Report(new VerifyProgress { Phase = phase, Processed = count, Total = count, Message = $"Found {count} files" });
        return map;
    }

    private async Task EnrichMatchedPairsAsync(
        ConcurrentDictionary<string, FileRecord> source,
        ConcurrentDictionary<string, FileRecord> dest,
        VerifyOptions options,
        IProgress<VerifyProgress>? progress,
        CancellationToken ct)
    {
        var matchedPaths = source.Keys.Where(dest.ContainsKey).ToArray();
        long processed = 0;
        bool doHash = options.Depth >= VerifyDepth.Full;
        bool doAcl  = options.IncludeAcl;

        var parallel = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, options.Threads),
            CancellationToken = ct,
        };

        await Parallel.ForEachAsync(matchedPaths, parallel, async (path, token) =>
        {
            var s = source[path];
            var d = dest[path];

            string? sHash = s.Hash, dHash = d.Hash;
            string? sSddl = s.SecurityDescriptor, dSddl = d.SecurityDescriptor;
            string? sOwner = s.Owner, dOwner = d.Owner;

            // Only hash when sizes match — a size mismatch is already a definitive difference.
            if (doHash && s.SizeBytes == d.SizeBytes)
            {
                sHash = await _hasher.TryComputeHashAsync(s.FullPath, options.HashAlgorithm, token);
                dHash = await _hasher.TryComputeHashAsync(d.FullPath, options.HashAlgorithm, token);
            }

            if (doAcl)
            {
                sSddl = _aclReader.TryGetSddl(s.FullPath);
                dSddl = _aclReader.TryGetSddl(d.FullPath);
                sOwner = AclReader.ExtractOwner(sSddl);
                dOwner = AclReader.ExtractOwner(dSddl);
            }

            source[path] = s with { Hash = sHash, SecurityDescriptor = sSddl, Owner = sOwner };
            dest[path]   = d with { Hash = dHash, SecurityDescriptor = dSddl, Owner = dOwner };

            long n = Interlocked.Increment(ref processed);
            if (n % 200 == 0)
                progress?.Report(new VerifyProgress
                {
                    Phase = VerifyPhase.Enriching,
                    Processed = n,
                    Total = matchedPaths.Length,
                    Message = $"{(doHash ? "Hashing" : "Reading ACLs on")} matched pairs",
                });
        });

        progress?.Report(new VerifyProgress
        {
            Phase = VerifyPhase.Enriching,
            Processed = matchedPaths.Length,
            Total = matchedPaths.Length,
            Message = "Enrichment complete",
        });
    }

    private static void Tally(RunRecord run, IReadOnlyList<ComparisonResult> comparisons)
    {
        long matched = 0, different = 0, missing = 0, extra = 0;
        foreach (var c in comparisons)
        {
            switch (c.Status)
            {
                case ComparisonStatus.Matched:      matched++;   break;
                case ComparisonStatus.Different:    different++; break;
                case ComparisonStatus.MissingAtDest: missing++;  break;
                case ComparisonStatus.ExtraAtDest:  extra++;     break;
            }
        }
        run.MatchedCount = matched;
        run.DifferentCount = different;
        run.MissingAtDestCount = missing;
        run.ExtraAtDestCount = extra;
    }

    private async Task SaveQuietlyAsync(RunRecord run)
    {
        try { await _repository.SaveAsync(run, CancellationToken.None); }
        catch { /* best-effort status update during failure unwinding */ }
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
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
        NetworkCredential? sourceCredential = null,
        NetworkCredential? destCredential = null,
        IProgress<VerifyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Strict mode forces Full depth, SHA-256, and ACLs; persist what actually ran.
        options = options.AsEffective();

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

        var connections = new List<IDisposable>();
        try
        {
            // Authenticate UNC shares with the supplied credentials, if any.
            if (sourceCredential is not null && NetworkPath.IsUnc(sourcePath))
                connections.Add(new NetworkConnection(NetworkPath.GetShareRoot(sourcePath), sourceCredential));
            if (destCredential is not null && NetworkPath.IsUnc(destPath))
                connections.Add(new NetworkConnection(NetworkPath.GetShareRoot(destPath), destCredential));

            // Phase 1 & 2 — enumerate both trees, capturing paths that couldn't be read on each side
            // (so a "zero drift" result can't quietly omit files that were never readable).
            var source = await EnumerateAsync(sourcePath, options, VerifyPhase.EnumeratingSource, progress, cancellationToken);
            var sourceInaccessible = _enumerator.InaccessiblePaths.ToArray();
            var dest   = await EnumerateAsync(destPath, options, VerifyPhase.EnumeratingDestination, progress, cancellationToken);
            var inaccessible = sourceInaccessible.Concat(_enumerator.InaccessiblePaths).ToList();

            run.TotalSourceFiles = source.Count;
            run.TotalDestFiles   = dest.Count;

            // Phase 3 — enrich matched pairs (hash and/or ACL) in parallel
            if (options.Depth >= VerifyDepth.Full || options.IncludeAcl)
                await EnrichMatchedPairsAsync(source, dest, options, progress, cancellationToken);

            // Phase 4 — compare. Strict mode uses zero timestamp tolerance (exact match). The maps are
            // passed straight through — no .Values.ToArray() copies, no rebuild inside Compare.
            progress?.Report(new VerifyProgress { Phase = VerifyPhase.Comparing, Message = "Comparing trees" });
            var comparer = options.Strict ? new ComparisonEngine(TimeSpan.Zero) : _comparison;
            var comparisons = comparer.Compare(source, dest, options);

            // End (upper bound), ASYMMETRIC: drop destination-only files newer than End (post-migration
            // noise). Files present on both sides are kept and compared regardless of age. The Start
            // (lower bound) is applied symmetrically during enumeration, above.
            long excludedNewer = 0;
            if (options.EndUtc is { } end)
            {
                var kept = new List<ComparisonResult>(comparisons.Count);
                foreach (var c in comparisons)
                {
                    if (c.Status == ComparisonStatus.ExtraAtDest && c.Dest is { } d && d.LastWriteTimeUtc > end)
                    {
                        excludedNewer++;
                        continue;
                    }
                    kept.Add(c);
                }
                comparisons = kept;
                progress?.Report(new VerifyProgress
                {
                    Phase = VerifyPhase.Comparing,
                    Message = $"Excluded {excludedNewer:N0} destination-only files modified after {end.ToLocalTime():yyyy-MM-dd}",
                });
            }

            Tally(run, comparisons);
            run.InaccessibleCount = inaccessible.Count;

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
                Message = $"Done – {run.MatchedCount} matched, {run.TotalDifferences} differences" +
                          (inaccessible.Count > 0 ? $", {inaccessible.Count} inaccessible (skipped)" : ""),
            });

            return new VerifyResult
            {
                Run = run,
                Comparisons = comparisons,
                ExcludedNewerCount = excludedNewer,
                InaccessiblePaths = inaccessible,
            };
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
        finally
        {
            foreach (var connection in connections)
                connection.Dispose();
        }
    }

    private async Task<Dictionary<string, FileRecord>> EnumerateAsync(
        string rootPath,
        VerifyOptions options,
        VerifyPhase phase,
        IProgress<VerifyProgress>? progress,
        CancellationToken ct)
    {
        // Plain Dictionary: this loop is the only writer, and enrichment computes in parallel but
        // writes back single-threaded — a ConcurrentDictionary cost an extra node allocation per entry
        // plus lock overhead, which is real memory/CPU at millions of records.
        var map = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
        var excludes = new GlobMatcher(options.ExcludePatterns);
        long count = 0, skippedOld = 0;

        var enumProgress = new Progress<EnumerationProgress>(p =>
            progress?.Report(new VerifyProgress
            {
                Phase = phase,
                Processed = p.FilesFound,
                Message = p.CurrentDirectory is { } dir
                    ? $"Scanning {dir} ({p.FilesFound:N0} files)"
                    : $"Scanning… ({p.FilesFound:N0} files)",
            }));

        await foreach (var record in _enumerator.EnumerateAsync(rootPath, options, enumProgress, ct))
        {
            if (excludes.IsExcluded(record.RelativePath)) continue;
            // Start (lower bound), SYMMETRIC: ignore files modified before Start on both sides.
            if (options.StartUtc is { } start && record.LastWriteTimeUtc < start) { skippedOld++; continue; }
            map[record.RelativePath] = record;
            count++;
        }

        var foundMsg = skippedOld > 0
            ? $"Found {count:N0} entries (skipped {skippedOld:N0} modified before start date)"
            : $"Found {count:N0} entries";
        progress?.Report(new VerifyProgress { Phase = phase, Processed = count, Total = count, Message = foundMsg });
        return map;
    }

    private async Task EnrichMatchedPairsAsync(
        Dictionary<string, FileRecord> source,
        Dictionary<string, FileRecord> dest,
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

        // The parallel phase only READS the dictionaries (safe with no concurrent writer) and deposits
        // each pair's enriched records into its own slot; the write-back below is single-threaded.
        var enriched = new (FileRecord Source, FileRecord Dest)[matchedPaths.Length];

        await Parallel.ForAsync(0, matchedPaths.Length, parallel, async (i, token) =>
        {
            var path = matchedPaths[i];
            var s = source[path];
            var d = dest[path];

            string? sHash = s.Hash, dHash = d.Hash;
            string? sSddl = s.SecurityDescriptor, dSddl = d.SecurityDescriptor;
            string? sOwner = s.Owner, dOwner = d.Owner;

            // Only hash files (not directories) whose sizes match — a size mismatch is already definitive.
            if (doHash && !s.IsDirectory && s.SizeBytes == d.SizeBytes)
            {
                // Hash the two sides concurrently: they're usually different devices (share vs local
                // disk), so the slower side hides behind the faster one instead of adding to it.
                var sTask = _hasher.TryComputeHashAsync(s.FullPath, options.HashAlgorithm, token);
                var dTask = _hasher.TryComputeHashAsync(d.FullPath, options.HashAlgorithm, token);
                sHash = await sTask;
                dHash = await dTask;
            }

            // Folders-only scope skips reading file ACLs (the bulk of the SMB round-trips).
            bool readAcl = doAcl && (options.AclScope == AclScope.FilesAndFolders || s.IsDirectory);
            if (readAcl)
            {
                sSddl = _aclReader.TryGetSddl(s.FullPath);
                dSddl = _aclReader.TryGetSddl(d.FullPath);
                sOwner = AclReader.ExtractOwner(sSddl);
                dOwner = AclReader.ExtractOwner(dSddl);
            }

            enriched[i] = (s with { Hash = sHash, SecurityDescriptor = sSddl, Owner = sOwner },
                           d with { Hash = dHash, SecurityDescriptor = dSddl, Owner = dOwner });

            long n = Interlocked.Increment(ref processed);
            if (n % 200 == 0)
                progress?.Report(new VerifyProgress
                {
                    Phase = VerifyPhase.Enriching,
                    Processed = n,
                    Total = matchedPaths.Length,
                    Message = $"{(doHash ? "Hashing" : "Reading ACLs on")} matched pairs ({n:N0}/{matchedPaths.Length:N0})",
                });
        });

        for (int i = 0; i < matchedPaths.Length; i++)
        {
            source[matchedPaths[i]] = enriched[i].Source;
            dest[matchedPaths[i]]   = enriched[i].Dest;
        }

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

using System.Security.Cryptography;
using System.Text;

namespace Massdex.SyncShark;

public sealed class SyncAuthorityInitialPlanner(SyncAuthorityDatabase authority,
    ISyncAuthorityContentComparison? contentComparison = null)
{
    // Existing call sites that omit the optional comparer keep the legacy single-algorithm compatibility
    // policy. Cloud mixed-provider production wiring supplies the explicit provider-aware instance.
    private readonly ISyncAuthorityContentComparison _contentComparison =
        contentComparison ?? SyncAuthorityLegacyContentComparison.Instance;

    public async Task<IReadOnlyList<SyncDecision>> AdoptAsync(string pairId,
        CancellationToken cancellationToken = default,
        Func<SyncComparisonProgress, Task>? onProgress = null)
        => await AdoptCoreAsync(pairId, null, cancellationToken, onProgress);

    // A later first-sync rescan can discover conflicts after initial adoption.
    // Retain only those classified conflict paths; never adopt a new baseline or
    // turn a one-sided change into a conflict while refreshing the review.
    internal Task<IReadOnlyList<SyncDecision>> RetainReviewConflictsAsync(string pairId,
        IReadOnlySet<string> paths, CancellationToken cancellationToken,
        Func<SyncComparisonProgress, Task>? onProgress = null) =>
        AdoptCoreAsync(pairId, paths, cancellationToken, onProgress);

    private async Task<IReadOnlyList<SyncDecision>> AdoptCoreAsync(string pairId,
        IReadOnlySet<string>? conflictOnlyPaths, CancellationToken cancellationToken,
        Func<SyncComparisonProgress, Task>? onProgress)
    {
        var started = DateTimeOffset.UtcNow;
        var phase = conflictOnlyPaths is null ? "Recording First Sync Baselines" : "Retaining First Sync Conflicts";
        if (onProgress is not null)
            await onProgress(new SyncComparisonProgress(phase, pairId, 0, 0, started, DateTimeOffset.UtcNow,
                "Reading the completed inventories to record matching files and unresolved differences. No file transfer or deletion is running.",
                "paths processed"));
        if (!await authority.HasCompleteObservationAsync(pairId, "local", cancellationToken) ||
            !await authority.HasCompleteObservationAsync(pairId, "dropbox", cancellationToken))
            throw new InvalidOperationException("Initial adoption requires one complete inventory on both sides.");
        var local = (await authority.LoadLatestCompleteObservationAsync(pairId, "local", cancellationToken))
            .ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        var dropbox = (await authority.LoadLatestCompleteObservationAsync(pairId, "dropbox", cancellationToken))
            .ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        var localUncertain = await authority.LoadLatestCompleteUncertainPathsAsync(pairId, "local",
            cancellationToken);
        var dropboxUncertain = await authority.LoadLatestCompleteUncertainPathsAsync(pairId, "dropbox",
            cancellationToken);
        var decisions = new List<SyncDecision>();
        var paths = local.Keys.Concat(dropbox.Keys).Concat(localUncertain).Concat(dropboxUncertain)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(path => conflictOnlyPaths is null || conflictOnlyPaths.Contains(path))
                     .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var processed = 0;
        // Keep every per-path event without waiting for a separate durable log flush per file.
        // Submission order is stable; the host serializes delivery and batches its retained log.
        var pendingProgress = new List<Task>(256);
        // Baselines and conflicts commit in one transaction per chunk of paths. A path's event is
        // reported only after its chunk committed, in the original path order.
        var pendingPoints = new List<SyncAuthorityPointRecord>();
        var pendingConflicts = new List<SyncAuthorityConflictRecord>();
        var pendingReports = new List<(string Path, string Detail)>(PersistChunkPaths);
        async Task FlushAsync()
        {
            await authority.AcceptSynchronizationPointsAsync(pendingPoints, cancellationToken);
            await authority.RecordConflictsAsync(pendingConflicts, cancellationToken);
            pendingPoints.Clear();
            pendingConflicts.Clear();
            foreach (var (path, detail) in pendingReports)
            {
                // As before batching: a cancelled run reports no further path.
                cancellationToken.ThrowIfCancellationRequested();
                processed++;
                if (onProgress is null) continue;
                pendingProgress.Add(onProgress(new SyncComparisonProgress(phase, path, processed, paths.Length,
                    started, DateTimeOffset.UtcNow, detail, "paths processed")));
                if (pendingProgress.Count < 256) continue;
                await Task.WhenAll(pendingProgress);
                pendingProgress.Clear();
            }
            pendingReports.Clear();
        }
        async Task ReportAsync(string path, string detail)
        {
            pendingReports.Add((path, detail));
            if (pendingReports.Count >= PersistChunkPaths) await FlushAsync();
        }
        try
        {
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                local.TryGetValue(path, out var localEntry);
                dropbox.TryGetValue(path, out var dropboxEntry);
                if (localUncertain.Contains(path) || dropboxUncertain.Contains(path))
                {
                    decisions.Add(new SyncDecision(path, SyncDecisionKind.Blocked,
                        "At least one stable discovery scan could not observe this path; uncertainty is not absence and no baseline was adopted."));
                    await ReportAsync(path, decisions[^1].Reason);
                    continue;
                }
                if (localEntry is not null && dropboxEntry is not null)
                {
                    if (!string.Equals(localEntry.ObjectId, dropboxEntry.ObjectId, StringComparison.Ordinal))
                    {
                        // A reviewed rename can leave a different file occupying the old
                        // path on one side. A shared name does not merge their identities.
                        // Preserve both and let the independently reviewed move/copy rows
                        // proceed; never abort the whole inventory or invent a baseline.
                        decisions.Add(new SyncDecision(path, SyncDecisionKind.Blocked,
                            "Different retained file identities occupy this path on the two sides. Review any proposed move before copying the replacement. No baseline or cross-object conflict was adopted; neither file was changed."));
                        await ReportAsync(path, decisions[^1].Reason);
                        continue;
                    }
                    var comparison = await _contentComparison.CompareAsync(localEntry, dropboxEntry,
                        cancellationToken);
                    if (comparison.Kind == SyncContentComparisonKind.Unknown)
                    {
                        decisions.Add(new SyncDecision(path, SyncDecisionKind.Blocked,
                            "Both complete inventories retained this path under one logical-object authority, but " +
                            "cross-side content equality is unknown (" + comparison.Reason + "); no baseline was " +
                            "adopted and neither side was mutated."));
                        await ReportAsync(path, decisions[^1].Reason);
                        continue;
                    }
                    if (comparison.Kind == SyncContentComparisonKind.Different)
                    {
                        var conflictId = "conflict-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                            string.Join("\n", pairId, localEntry.ObjectId, localEntry.ObjectVersionId,
                                dropboxEntry.ObjectVersionId)))).ToLowerInvariant()[..32];
                        pendingConflicts.Add(new(conflictId, pairId, localEntry.ObjectId,
                            "Two stable initial observations retain different versions.",
                            localEntry.ObjectVersionId, localEntry.MembershipId,
                            dropboxEntry.ObjectVersionId, dropboxEntry.MembershipId));
                        decisions.Add(new SyncDecision(path, SyncDecisionKind.Conflict,
                            "The complete initial inventories retain different object types or file versions; neither side was mutated."));
                        await ReportAsync(path, "Retained this file's conflict for an explicit decision; neither side was changed.");
                        continue;
                    }
                }
                if (conflictOnlyPaths is not null)
                {
                    await ReportAsync(path, "Rechecked this path; no new conflict or synchronization baseline was recorded.");
                    continue;
                }
                var objectId = localEntry?.ObjectId ?? dropboxEntry!.ObjectId;
                if (localEntry is null || dropboxEntry is null)
                {
                    decisions.Add(new SyncDecision(path, SyncDecisionKind.Baseline,
                        localEntry is not null
                            ? "Stable first-run discovery found this path locally only; it remains unadopted so the active merge pass can create it in Dropbox."
                            : "Stable first-run discovery found this path in Dropbox only; it remains unadopted so the active merge pass can create it locally."));
                    await ReportAsync(path, "Recorded this one-sided path for the first-sync review; no file was transferred or baseline adopted.");
                    continue;
                }
                var pointId = DeterministicId(pairId, objectId,
                    localEntry?.ObjectVersionId ?? "absent", dropboxEntry?.ObjectVersionId ?? "absent");
                pendingPoints.Add(new(pointId, pairId, objectId,
                    localEntry?.ObjectVersionId, dropboxEntry?.ObjectVersionId,
                    localEntry?.MembershipId, dropboxEntry?.MembershipId));
                decisions.Add(new SyncDecision(path, SyncDecisionKind.Baseline,
                    "Two unchanged complete observations were adopted without copying, overwriting or deleting either side."));
                await ReportAsync(path, "Saved the synchronization baseline for this matching path; no upload, download or deletion was needed.");
            }
            await FlushAsync();
        }
        finally
        {
            // Cancellation/failure must not discard already submitted, completed-path log events.
            await Task.WhenAll(pendingProgress);
        }
        if (onProgress is not null)
            await onProgress(new SyncComparisonProgress(phase, pairId, processed, paths.Length, started,
                DateTimeOffset.UtcNow, "Finished recording the initial matching paths and review decisions. No files were transferred or deleted.",
                "paths processed"));
        return decisions;
    }

    // Paths per commit: amortizes the durable commit while keeping each transaction short.
    private const int PersistChunkPaths = 2048;

    private static string DeterministicId(params string[] parts) => "syncpoint-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts))))
            .ToLowerInvariant()[..32];
}

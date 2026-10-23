using System.Collections.Concurrent;

namespace Massdex.SyncShark.V2;

/// <summary>Composition-owned, root-deduplicated background work. Never joined by planning.</summary>
internal sealed class LocalGapWorker(ILocalGapStore store, ILocalGapIo io) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, Task> _roots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _rootStops = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _runNumber = new(StringComparer.Ordinal);
    private bool _disposed;

    internal void Start(RootDef root)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _runNumber.AddOrUpdate(root.RootId, 1, (_, previous) => previous + 1);
            if (_roots.TryGetValue(root.RootId, out var prior) && !prior.IsCompleted) return;
            // Schedule independently: an all-metadata page can complete synchronously, so merely
            // passing two Loop calls to WhenAll could finish discovery before starting hashing.
            if (_rootStops.Remove(root.RootId, out var oldStop)) oldStop.Dispose();
            var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            _rootStops[root.RootId] = stop;
            _roots[root.RootId] = Task.Run(() => RunRoot(root, stop));
        }
    }

    private async Task RunRoot(RootDef root, CancellationTokenSource stop)
    {
        await Task.WhenAll(Task.Run(() => Loop(root, true, stop)), Task.Run(() => Loop(root, false, stop))).ConfigureAwait(false);
    }

    internal async Task StopAsync(string rootId)
    {
        Task? task;
        lock (_gate)
        {
            if (_rootStops.TryGetValue(rootId, out var stop)) stop.Cancel();
            _roots.TryGetValue(rootId, out task);
        }
        if (task is not null) await task.ConfigureAwait(false);
    }

    private async Task Loop(RootDef root, bool discovery, CancellationTokenSource stop)
    {
        var ct = stop.Token;
        using var admission = FairStoreGate.WithCancellation(ct);
        long pausedDiscoveryRun = -1;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!store.HasGapProtection(root.RootId)) return;
                if (store is IStateStore retry && !retry.IndexRetryDue(root.RootId))
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                    continue;
                }
                var run = _runNumber.GetValueOrDefault(root.RootId);
                if (discovery && pausedDiscoveryRun == run)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                    continue;
                }
                var worked = false;
                store.SetGapStatus(root.RootId, discovery, discovery ? "metadata discovery running" : io.PauseReason);
                if ((discovery || !io.Paused) && store.GapEpoch(root.RootId) is { } epoch)
                    worked = discovery ? Discover(root, epoch, ct) : await HashPage(root, epoch, ct).ConfigureAwait(false);
                if (discovery && !worked && store.GapEpoch(root.RootId) is { DiscoveryDone: false })
                {
                    // Retry discovery only after the next Start/run; do not cancel independent content work.
                    pausedDiscoveryRun = run;
                }
                // Give foreground store callers a scheduling opportunity after EVERY page.
                await Task.Delay(worked ? TimeSpan.FromMilliseconds(20) : TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (store is IStateStore sql) sql.RecordIndexFailure(root.RootId, error.Message);
            RunLog.Trace($"local gap worker failed ({root.RootId}): {error}");
            try { store.SetGapStatus(root.RootId, discovery, $"worker failed: {error.Message}; retry on next sync"); }
            catch (Exception noticeError) { RunLog.Trace($"local gap failure status unavailable: {noticeError.Message}"); }
            // Stop the sibling lane too, so Start can restart this root on the next run.
            stop.Cancel();
        }
    }

    internal static bool MetadataOnly(ItemState item) => item.Kind != ItemKind.File ||
        (item.Flags & (ItemFlags.Ignored | ItemFlags.Link | ItemFlags.Placeholder | (ItemFlags)128)) != 0;

    internal bool Discover(RootDef root, LocalGapEpoch epoch, CancellationToken ct)
    {
        if (epoch.DiscoveryDone) return false;
        var page = store.GapDiscoveryPage(root.RootId, epoch, 128);
        using var probes = io.OpenProbes(root);
        if (epoch.TrustFileUsn && probes.UnavailableReason is { } unavailable)
        {
            if (store is IStateStore failed) failed.RecordIndexFailure(root.RootId, unavailable);
            store.SetGapStatus(root.RootId, true, unavailable + "; metadata discovery resumes after retry deadline");
            return false;
        }
        var discovered = new List<(ItemState Item, bool RequiresHash)>();
        var failures = new List<(ItemState Item, string Reason)>();
        var reasons = new List<(string ObjectId, string Reason)>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        string? firstWin32 = null;
        void Record(ItemState item, string reason)
        {
            reasons.Add((item.ObjectId, reason));
            var category = reason.Split('(')[0];
            counts[category] = counts.GetValueOrDefault(category) + 1;
            if (firstWin32 is null && reason.Contains('(')) firstWin32 = reason;
        }
        foreach (var item in page)
        {
            // Preserve the completed prefix before honoring shutdown. The durable frontier
            // must not discard probes or skip the unvisited suffix of a partially read page.
            if (ct.IsCancellationRequested) break;
            try
            {
                if (MetadataOnly(item) || io.Excluded(root, item))
                {
                    discovered.Add((item, false));
                    Record(item, "metadata-only");
                    continue;
                }
                var proof = epoch.TrustFileUsn ? probes.Probe(item) : null;
                var reason = !epoch.TrustFileUsn ? "untrusted-history" :
                    proof is null ? probes.LastFailureReason ?? "proof-unavailable" :
                    !ProofFollows(proof.JournalCursor, epoch.JournalCursor) ? "journal-mismatch" :
                    VerificationBudget.RequiresHash(item, proof.File) ? "content-proof-required" :
                    !Matches(item, proof.File) ? "metadata-mismatch" :
                    proof.File.Usn >= epoch.Boundary ? "usn>=boundary" : "proved-unchanged";
                discovered.Add((item, reason != "proved-unchanged"));
                Record(item, reason);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                discovered.Add((item, true));
                failures.Add((item, error.Message));
                Record(item, "probe-exception");
                RunLog.Trace($"local gap item discovery retained: {item.Path}: {error}");
            }
        }
        // Once observed, a bounded completed prefix is durable even when stop races admission.
        using (FairStoreGate.WithCancellation(CancellationToken.None))
        {
            // Even a poison item advances discovery. Its protected, entity-owned work is retried separately.
            store.GapDiscoveredBatch(root.RootId, epoch, discovered);
            foreach (var failure in failures) store.FailGapItem(root.RootId, epoch, failure.Item, failure.Reason);
            // Diagnostics are best effort and must never skip poison-item accounting or
            // prevent discovery from completing after durable work has been recorded.
            // Genuine store corruption is still surfaced, never masked as a diagnostic outage.
            try
            {
                if (reasons.Count != 0) store.SetGapDiscoveryReasons(root.RootId, epoch, reasons);
            }
            catch (Exception error) when (error is not (StateDamagedException or OperationCanceledException or OutOfMemoryException))
            {
                RunLog.Trace($"local gap discovery reasons unavailable: {error}");
            }
        }
        if (page.Count > 0)
            RunLog.Trace($"local gap metadata page: root={root.RootId}; epoch={epoch.Epoch}; count={page.Count}; " +
                string.Join(", ", counts.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => $"{entry.Key}={entry.Value}")) +
                "; first-win32=" + (firstWin32 ?? "none"));
        if (page.Count < 128 && discovered.Count == page.Count && !ct.IsCancellationRequested)
            store.GapDiscoveryDone(root.RootId, epoch);
        ct.ThrowIfCancellationRequested();
        return page.Count > 0;
    }

    internal async Task<bool> HashPage(RootDef root, LocalGapEpoch epoch, CancellationToken ct)
    {
        var page = store.GapHashPage(root.RootId, epoch, 16);
        if (page.Count == 0) return false;
        using var availability = io.OpenProbes(root);
        if (availability.UnavailableReason is { } unavailable)
        {
            if (store is IStateStore failed) failed.RecordIndexFailure(root.RootId, unavailable);
            store.SetGapStatus(root.RootId, false, unavailable);
            return false;
        }
        var completed = false;
        foreach (var item in page)
        {
            ct.ThrowIfCancellationRequested();
            if (io.Paused) return false;
            try
            {
                if (MetadataOnly(item) || io.Excluded(root, item))
                {
                    store.RetireGapItem(root.RootId, epoch, item);
                    continue;
                }
                store.SetGapStatus(root.RootId, false, $"hashing {item.Path}; interrupted files restart from byte zero");
                RunLog.Trace($"local gap verification starting/restarting from byte zero: {item.Path}");
                var result = await io.HashAsync(root, item, ct).ConfigureAwait(false);
                if (result is { Outcome: LocalGapHashOutcome.Verified, Hash: { } hash, Proof: { } proof })
                {
                    store.StageGapHash(root.RootId, epoch, item, hash, proof);
                    completed = true;
                }
                else
                {
                    ct.ThrowIfCancellationRequested();
                    // The IO owns the observed cause. Never re-read live pause state to
                    // reinterpret an interrupted or disabled read as a verification failure.
                    if (result.Outcome == LocalGapHashOutcome.Unverifiable)
                        store.FailGapItem(root.RootId, epoch, item,
                            "file/journal proof unavailable or changed; make offline/cloud content available, then retry sync");
                    store.SetGapStatus(root.RootId, false, result.Outcome switch
                    {
                        LocalGapHashOutcome.Paused => "content verification paused during read; retry when activity/power allows",
                        LocalGapHashOutcome.Disabled => "content verification disabled by byte-rate setting",
                        _ => $"file/journal proof unavailable or changed for {item.Path}; retry pending"
                    });
                    RunLog.Trace($"local gap verification interrupted or changed; retained for restart: {item.Path}");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                RunLog.Trace($"local gap file interrupted; next attempt restarts from byte zero: {item.Path}");
                throw;
            }
            catch (Exception error)
            {
                store.FailGapItem(root.RootId, epoch, item, error.Message);
                store.SetGapStatus(root.RootId, false, $"cannot verify {item.Path}: {error.Message}; retry pending");
                RunLog.Trace($"local gap file retained for restart: {item.Path}: {error.Message}");
            }
        }
        return completed;
    }

    internal static bool Matches(ItemState item, NtfsFileUsn proof)
    {
        var identity = LocalItems.ParseObjectId(item.ObjectId);
        return identity.Volume == proof.Volume && identity.FileId == proof.FileId && item.Size == proof.Size &&
            item.Mtime.UtcDateTime.ToFileTimeUtc() == proof.MtimeFileTime;
    }

    internal static bool SameJournal(string a, string b) =>
        NtfsUsnCheckpoint.TryParseCursor(a, out var first, out _) &&
        NtfsUsnCheckpoint.TryParseCursor(b, out var second, out _) && first == second;

    private static bool ProofFollows(string current, string previous) =>
        NtfsUsnCheckpoint.TryParseCursor(current, out var id, out var next) &&
        NtfsUsnCheckpoint.TryParseCursor(previous, out var oldId, out var oldNext) && id == oldId && next >= oldNext;

    internal static void PromoteDiscovery(ILocalGapStore store, RootDef root, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { store.PromoteGapDiscovery(root.RootId); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            store.SetGapStatus(root.RootId, true, $"metadata promotion deferred: {error.Message}; retry next sync");
            RunLog.Trace($"local gap metadata promotion deferred: {root.RootId}: {error}");
            return;
        }
    }

    internal static void Promote(ILocalGapStore store, IStateStore known, ILocalGapIo io, RootDef root,
        long through, NtfsUsnCheckpoint? runStart, Func<string, bool> include, Action<string> touched, CancellationToken ct)
    {
        // At most four pages (512 probes) per planning pass; remaining work waits for the next pass.
        // Selection belongs only to planning touches, never entity verification.
        if (runStart is null) return;
        long after = 0;
        for (var pageNumber = 0; pageNumber < 4; pageNumber++)
        {
            ct.ThrowIfCancellationRequested();
            var results = store.GapResults(root.RootId, through, 128, after);
            if (results.Count == 0) return;
            using var probes = io.OpenProbes(root);
            foreach (var result in results)
            {
                ct.ThrowIfCancellationRequested();
                after = result.Sequence;
                try
                {
                    var currentItem = known.ByObject(root.RootId, result.Expected.ObjectId);
                    if (currentItem is null || MetadataOnly(currentItem) || io.Excluded(root, currentItem))
                    {
                        if (store.GapEpoch(root.RootId) is { } epoch)
                            store.RetireGapItem(root.RootId, epoch, currentItem ?? result.Expected);
                        continue;
                    }
                    if (!ProofFollows(runStart.Cursor, result.Proof.JournalCursor))
                    {
                        store.RejectGapResult(root.RootId, result);
                        continue;
                    }
                    var current = probes.Probe(currentItem);
                    if (current is null)
                    {
                        store.DeferGapResult(root.RootId, result, probes.UnavailableReason ?? "file identity proof temporarily unavailable");
                        continue;
                    }
                    if (!ProofFollows(current.JournalCursor, runStart.Cursor) ||
                        current.File != result.Proof.File || !store.AcceptGapResult(root.RootId, result))
                    {
                        store.RejectGapResult(root.RootId, result);
                        continue;
                    }
                    // AcceptGapResult retained the entity's durable publication obligation.
                    // The indexer's bounded planning handoff owns alias expansion; no direct touches here.
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    store.DeferGapResult(root.RootId, result, error.Message);
                    RunLog.Trace($"local gap result validation deferred: {result.Expected.Path}: {error}");
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _stop.Cancel(); tasks = _roots.Values.ToArray();
        }
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (Exception error) { RunLog.Trace($"local gap shutdown: {error}"); }
        finally
        {
            foreach (var stop in _rootStops.Values) stop.Dispose();
            _stop.Dispose();
        }
    }
}

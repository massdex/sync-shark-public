using System.Collections.Concurrent;

namespace Massdex.SyncShark.V2;

internal interface ILocalGapIo
{
    bool Paused { get; }
    LocalGapProof? Probe(RootDef root, ItemState item);
    ILocalGapProbe OpenProbes(RootDef root) => new DelegatingGapProbe(this, root);
    string PauseReason => Paused ? "content verification paused for activity or power" : "content verification running";
    bool Excluded(RootDef root, ItemState item) => false;
    Task<(string Hash, LocalGapProof Proof)?> HashAsync(RootDef root, ItemState item, CancellationToken ct);
}

internal interface ILocalGapProbe : IDisposable
{
    LocalGapProof? Probe(ItemState item);
    string? UnavailableReason => null;
}

internal sealed class DelegatingGapProbe(ILocalGapIo io, RootDef root) : ILocalGapProbe
{
    public LocalGapProof? Probe(ItemState item) => io.Probe(root, item);
    public void Dispose() { }
}

/// <summary>Composition-owned, root-deduplicated background work. Never joined by planning.</summary>
internal sealed class LocalGapWorker(ILocalGapStore store, ILocalGapIo io) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, Task> _roots = new(StringComparer.Ordinal);
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
            _roots[root.RootId] = Task.Run(() => RunRoot(root));
        }
    }

    private async Task RunRoot(RootDef root)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        await Task.WhenAll(Task.Run(() => Loop(root, true, stop)), Task.Run(() => Loop(root, false, stop))).ConfigureAwait(false);
    }

    private async Task Loop(RootDef root, bool discovery, CancellationTokenSource stop)
    {
        var ct = stop.Token;
        long pausedDiscoveryRun = -1;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!store.HasGapProtection(root.RootId)) return;
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
                if (!worked) await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
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
            store.SetGapStatus(root.RootId, true, unavailable + "; metadata discovery resumes next sync");
            return false;
        }
        var discovered = new List<(ItemState Item, bool RequiresHash)>();
        var failures = new List<(ItemState Item, string Reason)>();
        foreach (var item in page)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (MetadataOnly(item) || io.Excluded(root, item))
                {
                    store.RetireGapItem(root.RootId, epoch, item);
                    discovered.Add((item, false));
                    continue;
                }
                var proof = epoch.TrustFileUsn ? probes.Probe(item) : null;
                var unchanged = epoch.TrustFileUsn && proof is not null && ProofFollows(proof.JournalCursor, epoch.JournalCursor) &&
                    Matches(item, proof.File) && proof.File.Usn < epoch.Boundary;
                discovered.Add((item, !unchanged));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                discovered.Add((item, true));
                failures.Add((item, error.Message));
                RunLog.Trace($"local gap item discovery retained: {item.Path}: {error}");
            }
        }
        // Even a poison item advances discovery. Its protected, entity-owned work is retried separately.
        store.GapDiscoveredBatch(root.RootId, epoch, discovered);
        foreach (var failure in failures) store.FailGapItem(root.RootId, epoch, failure.Item, failure.Reason);
        if (page.Count < 128) store.GapDiscoveryDone(root.RootId, epoch);
        return page.Count > 0;
    }

    internal async Task<bool> HashPage(RootDef root, LocalGapEpoch epoch, CancellationToken ct)
    {
        var page = store.GapHashPage(root.RootId, epoch, 16);
        if (page.Count == 0) return false;
        using var availability = io.OpenProbes(root);
        if (availability.UnavailableReason is { } unavailable)
        {
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
                if (result is { } verified)
                {
                    store.StageGapHash(root.RootId, epoch, item, verified.Hash, verified.Proof);
                    completed = true;
                }
                else
                {
                    store.SetGapStatus(root.RootId, false, io.Paused ? io.PauseReason :
                        $"file/journal proof unavailable or changed for {item.Path}; retry from byte zero");
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
                    for (var offset = 0; ; offset += 128)
                    {
                        ct.ThrowIfCancellationRequested();
                        var aliases = known.ObjectPaths(root.RootId, result.Expected.ObjectId, offset, 128);
                        foreach (var path in aliases) if (include(path)) touched(path);
                        if (aliases.Count < 128) break;
                    }
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
        finally { _stop.Dispose(); }
    }
}

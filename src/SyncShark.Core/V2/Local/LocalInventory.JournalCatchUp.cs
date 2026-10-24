using System.Diagnostics;

namespace Massdex.SyncShark.V2;

public sealed partial class LocalInventory
{
    // One bounded summary line per journal catch-up; counters are per catch-up, never per item.
    private static LocalCatchUpCounters? CatchUp => LocalCatchUpCounters.Current;

    // The single counted store seam. Outside a catch-up (a direct RehashAsync caller) it still
    // serves the lookup, just without a summary to count it into.
    private ItemState? CatchUpByObject(string rootId, string objectId) =>
        CatchUp is { } counters ? counters.ByObject(rootId, objectId) : _known.ByObject(rootId, objectId);

    /// <inheritdoc/>
    public async Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include, CancellationToken ct)
    {
        var rootId = _root.RootId;
        if (_known is ILocalWalkStore sql && sql.HasLocalWalk(rootId))
            return Invalid(gen, rootId, cursor, "unfinished local full walk takes precedence");
        if (_pairId is not null && (WalkCheckpointForTest is null || CapacityQueryForTest is not null)) ObserveCapacity(cursor);
        if (!NtfsUsnCheckpoint.TryParseCursor(cursor, out var journalId, out var next)) return Invalid(gen, rootId, cursor, "saved cursor cannot be parsed");
        PilotDirectoryLease lease;
        try
        {
            lease = PilotDirectoryLease.Open(_root.RootPath, _rootIdentity);
        }
        catch (Exception error) when (IsRootUnavailable(error))
        {
            return Invalid(gen, rootId, cursor, "root lease failed: " + error.Message);
        }
        using (lease)
        {
            // One bounded summary per catch-up, emitted at the end and on failure; never per item.
            var counters = new LocalCatchUpCounters(_known);
            var previous = LocalCatchUpCounters.Slot.Value;
            LocalCatchUpCounters.Slot.Value = counters;
            var started = Stopwatch.GetTimestamp();
            try
            {
                return await DeltaAsync(lease, cursor, gen, rootId, journalId, next, include, ct).ConfigureAwait(false);
            }
            finally
            {
                LocalCatchUpCounters.Slot.Value = previous;
                counters.Emit(rootId, Stopwatch.GetElapsedTime(started));
            }
        }
    }

    private async Task<Delta> DeltaAsync(PilotDirectoryLease lease, string cursor, long gen, string rootId,
        ulong journalId, long next, Func<string, bool> include, CancellationToken ct)
    {
        var (through, journalDeleted) = ReadWalkCheckpointState();
        if (through is null)
        {
            if (!journalDeleted) return RetryJournal(gen, rootId, cursor, "journal checkpoint temporarily unavailable");
            (_known as ILocalGapStore)?.BeginGap(rootId, "", 0, false, "NTFS journal deleted or disabled");
            return Invalid(gen, rootId, cursor, "journal deleted");
        }
        if (through.RequiresFullReconciliation(cursor))
        {
            BeginJournalGap(rootId, journalId, next, through);
            return Invalid(gen, rootId, cursor,
                journalId != through.JournalId ? "journal replaced" : next > through.NextUsn ? "cursor ahead of journal" : "journal gap: cursor precedes retained range");
        }
        var window = new JournalReplayWindow();
        var budget = Math.Clamp(_settings.LocalReplayWindowsPerRun, 1, 64);
        for (var index = 1; ; index++)
        {
            ct.ThrowIfCancellationRequested();
            var end = window.Through(next, through);
            Delta delta;
            try { delta = await ReadWindowAsync(lease, cursor, gen, rootId, journalId, next, end, window, include, ct).ConfigureAwait(false); }
            catch (RetrySmallerJournalWindow) { index--; continue; }
            // Incomplete evidence uses the existing Run retry owner. Never advance past it here.
            if (delta.CursorInvalid || delta.IncompleteSubtrees.Count != 0 || delta.PendingDeletionEvidence.Count != 0 ||
                !NtfsUsnCheckpoint.TryParseCursor(delta.CursorAfter, out _, out var consumed) || consumed <= next)
                return delta;
            RunTrace.Emit($"journal catch-up window {index}: {consumed - next} bytes, {CatchUp?.Records ?? 0} records, {CatchUp?.Resolved ?? 0} paths");
            // Run owns deletion timestamps, directory descendant expansion and move pre-images.
            // End this call at such a window rather than discard that authority between windows.
            if (consumed >= through.NextUsn || index >= budget || !_known.SupportsJournalWindows ||
                delta.Changes.Any(change => change.Kind == ChangeKind.Delete || change.State?.Kind == ItemKind.Folder ||
                    _known.ByObject(rootId, change.ObjectId) is { } before && !PairPath.Same(before.Path, change.Path)))
                return delta;
            _known.CheckpointJournalWindow(delta with { IncludesPath = include });
            JournalWindowPersistedForTest?.Invoke(delta.CursorAfter);
            cursor = delta.CursorAfter;
            next = consumed;
            delta = null!; // Release this window's scan/evidence before the next journal read.
        }
    }

    private sealed class RetrySmallerJournalWindow : Exception { }
    internal Action<string>? JournalWindowPersistedForTest { get; set; }

    private async Task<Delta> ReadWindowAsync(PilotDirectoryLease lease, string cursor, long gen, string rootId,
        ulong journalId, long next, NtfsUsnCheckpoint through, JournalReplayWindow window,
        Func<string, bool> include, CancellationToken ct)
    {
        var from = new NtfsUsnCheckpoint(journalId, through.FirstUsn, next, through.LowestValidUsn);
        IReadOnlyList<NtfsUsnChange> changes;
        string? readFailure;
        NtfsUsnReadFailure readFailureKind;
        long consumed = through.NextUsn;
        var read = JournalReadForTest is { } fake
            ? fake(_root.RootPath, from, through, out changes, out readFailure, out readFailureKind, ct)
            : NtfsUsnJournal.TryReadChanges(_root.RootPath, from, through, out changes, out readFailure, out readFailureKind, out consumed, ct);
        if (!read)
        {
            if (readFailureKind == NtfsUsnReadFailure.OverLimit && window.Halve(through.NextUsn - next))
                throw new RetrySmallerJournalWindow();
            return JournalReadFailed(gen, rootId, cursor, through, next, readFailure ?? "journal read failed", readFailureKind);
        }
        if (consumed < next || consumed > through.NextUsn || (consumed == next && through.NextUsn > next))
            return RetryJournal(gen, rootId, cursor, "journal window did not advance at a record boundary");
        through = through with { NextUsn = consumed };
        CatchUp?.ObserveRecords(changes.Count);
        return await ResolveWindowAsync(lease, cursor, gen, rootId, next, through, window, changes, include, ct).ConfigureAwait(false);
    }

    private async Task<Delta> ResolveWindowAsync(PilotDirectoryLease lease, string cursor, long gen, string rootId,
        long next, NtfsUsnCheckpoint through, JournalReplayWindow window, IReadOnlyList<NtfsUsnChange> changes,
        Func<string, bool> include, CancellationToken ct)
    {
        Delta Overflow(string reason)
        {
            if (window.Halve(through.NextUsn - next)) throw new RetrySmallerJournalWindow();
            PreserveJournalWrites(changes, rootId);
            return JournalReadFailed(gen, rootId, cursor, through, next, reason, NtfsUsnReadFailure.OverLimit);
        }
        var names = new LocalJournalNames(_root, _known, changes, _settings.LocalDeltaMaxPaths, CatchUp);
        var scope = new JournalScope(this, names, rootId, ct);
        var fallbackReason = "resolved path limit exceeded";
        var paths = await ResolveJournalAsync(changes, names, reason => fallbackReason = names.LastFailure ?? reason,
            scope.Defer, () => scope.Overflow, ct).ConfigureAwait(false);
        CatchUp?.ObserveResolved(paths?.Count ?? 0);
        if (scope.Overflow || paths is null || paths.Count > _settings.LocalDeltaMaxPaths) return Overflow(fallbackReason);
        // Holds precede both refresh publication and any cursor checkpoint.
        scope.Hold();
        var delta = await RefreshWindowAsync(lease, cursor, gen, rootId, through.Cursor, paths, include, ct).ConfigureAwait(false);
        AfterRefreshScan?.Invoke();
        scope.Observe(delta);
        var protectedDelta = scope.Apply(delta);
        if (protectedDelta is null) return Overflow("ambiguous journal scope exceeds bounded path budget");
        var written = changes.Where(change => (change.Reason & 0x7) != 0)
            .Select(change => SyncLocalEndpointStore.ObjectId(_volume, change.FileId)).ToHashSet(StringComparer.Ordinal);
        return await RehashAsync(lease, protectedDelta, written, ct).ConfigureAwait(false);
    }

    private async Task<Delta> RefreshWindowAsync(PilotDirectoryLease lease, string cursor, long gen, string rootId,
        string through, IReadOnlyList<string> paths, Func<string, bool> include, CancellationToken ct)
    {
        var watched = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        paths = WithDefenderDeferredPaths(paths, rootId);
        var exact = paths.Where(path => !UnderAny(path, watched)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return paths.Count == 0
            ? new Delta(rootId, _side, gen, cursor, through, Array.Empty<Change>(), false, [])
            : await RefreshAsync(lease, gen, cursor, through, include, paths, ct, exact).ConfigureAwait(false);
    }

    // Journal data writes need full reconciliation before hashing; recorded as durable debt.
    private void PreserveJournalWrites(IReadOnlyList<NtfsUsnChange> changes, string rootId)
    {
        var writtenKnown = changes.Where(record => (record.Reason & 0x7) != 0).DistinctBy(record => record.FileId)
            .Select(record => CatchUpByObject(rootId, SyncLocalEndpointStore.ObjectId(_volume, record.FileId)))
            .Where(item => item is { Kind: ItemKind.File } && !item.Flags.HasFlag(ItemFlags.OwnedPointer)).Select(item => item!);
        _known.RequireRehashBatch(rootId, writtenKnown, "journal data write requires full reconciliation before hashing");
    }

    private async Task<Delta> RefreshAsync(PilotDirectoryLease lease, long gen, string cursor, string throughCursor,
        Func<string, bool> include, IReadOnlyList<string> paths, CancellationToken ct, IReadOnlySet<string>? exactPaths = null)
    {
        paths = paths.Where(path => include(path) && !LocalItems.IsExcluded(path, _productOwnedRelative)).ToArray();
        // The ancestors stay held until the listing completes, so none can be swapped after it was validated.
        using var guard = LocalItems.GuardRefreshPaths(_root.RootPath, _root.RootId, _known, paths);
        // The deletion time is the scan time it is first seen missing, so it is sampled before the scan.
        var scanTime = _clock.GetUtcNow();
        BeforeRefreshScan?.Invoke();
        PilotLocalScan scan;
        try
        {
            scan = await ScanAsync(lease, ct, include, guard.Safe, null, exactPaths: exactPaths).ConfigureAwait(false);
        }
        catch (Exception error) when (IsRootUnavailable(error))
        {
            return Invalid(gen, _root.RootId, cursor, "root unavailable during guarded refresh");
        }
        scan = scan with { Decisions = scan.Decisions.Concat(guard.Quarantine).ToArray() };
        var exactSafe = paths.Where(path => exactPaths?.Contains(path) == true &&
            !guard.Incomplete.Any(parent => PairPath.IsSelfOrUnder(path, parent))).ToArray();
        scan = await ApplyHistoryAsync(scan, include, ct,
            guard.Safe.Where(path => exactPaths?.Contains(path) != true).ToArray(), exactSafe).ConfigureAwait(false);
        CatchUp?.ObserveRefresh(scan.Entries.Count, scan.PathsInspected);
        var quarantined = scan.Decisions.Where(d => d.IsQuarantine).Select(d => d.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var incomplete = guard.Incomplete.Where(path => !quarantined.Contains(path)).ToArray();
        // Observing only a queued folder itself is not evidence that its unseen children disappeared.
        var deletionScopes = RefreshDeletionScopes(scan, paths, exactPaths);
        return BuildDelta(gen, _root.RootId, cursor, throughCursor, scan, deletionScopes, include, scanTime, incomplete);
    }
}

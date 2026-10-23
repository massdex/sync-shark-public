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
        var from = new NtfsUsnCheckpoint(journalId, through.FirstUsn, next, through.LowestValidUsn);
        if (through.RequiresFullReconciliation(cursor))
        {
            BeginJournalGap(rootId, journalId, next, through);
            return Invalid(gen, rootId, cursor,
                journalId != through.JournalId ? "journal replaced" : next > through.NextUsn ? "cursor ahead of journal" : "journal gap: cursor precedes retained range");
        }
        if (ReplayTooLarge(next, through.NextUsn))
            return JournalReadFailed(gen, rootId, cursor, through, next,
                "journal byte range exceeds replay budget", NtfsUsnReadFailure.OverLimit);
        if (!(JournalReadForTest ?? NtfsUsnJournal.TryReadChanges)(_root.RootPath, from, through, out var changes, out var readFailure, out var readFailureKind, ct))
        {
            return JournalReadFailed(gen, rootId, cursor, through, next, readFailure ?? "journal read failed", readFailureKind);
        }
        CatchUp?.ObserveRecords(changes.Count);
        var names = new LocalJournalNames(_root, _known, changes, _settings.LocalDeltaMaxPaths, CatchUp);
        var scopedParents = new HashSet<(ulong Object, ulong Parent)>();
        var ambiguous = new Dictionary<string, ItemState>(StringComparer.Ordinal);
        var scoped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var scopeOverflow = false;
        long scopeCharacters = 0;
        void Scope(string path, string reason)
        {
            if (scopeOverflow) return;
            if (!scoped.ContainsKey(path)) scopeCharacters += path.Length;
            scoped[path] = reason;
            scopeOverflow = scoped.Count > Math.Min(_settings.LocalDeltaMaxPaths, PathProtectionIndex.DefaultPathLimit) || scopeCharacters > PathProtectionIndex.DefaultCharacterLimit;
        }
        void Defer(NtfsUsnChange record, ItemState? known, string? scope, string reason)
        {
            scopeOverflow |= names.ScopeOverflow;
            if (scopeOverflow) return;
            var resolveNames = record.Name.Length == 0 && (record.Reason & 0x13100) != 0;
            if (scope is not null && !(resolveNames && known is null)) Scope(scope, reason);
            if (resolveNames && scopedParents.Add((record.FileId, record.ParentFileId)))
            {
                var parent = record.ParentFileId == _rootFileId ? "" :
                    CatchUpByObject(rootId, SyncLocalEndpointStore.ObjectId(_volume, record.ParentFileId)) is { Kind: ItemKind.Folder } folder
                        ? folder.Path : null;
                if (parent is not null)
                {
                    var aliases = names.FindAmbiguous(record, parent, ct);
                    if (aliases is null) Scope(parent, "ambiguous journal parent could not be read; scoped retry required");
                    else foreach (var alias in aliases) Scope(alias, reason);
                    scopeOverflow |= names.ScopeOverflow;
                }
            }
            if (scopeOverflow || known is null) return; // Unknown parent/object is outside the retained inventory.
            if (!ambiguous.TryAdd(known.ObjectId, known)) return;
            // Entity-owned rehash protects all aliases; named scopes also protect opposite-side
            // absence decisions and names not yet present in our inventory.
            for (var offset = 0; ; offset += 128)
            {
                var aliases = _known.ObjectPaths(rootId, known.ObjectId, offset, 128);
                foreach (var alias in aliases) Scope(alias, reason);
                if (aliases.Count < 128) break;
                if (scopeOverflow) break;
            }
            Scope(known.Path, reason);
        }
        var fallbackReason = "resolved path limit exceeded";
        var context = new ResolveContext(_rootFileId, _volume,
            id => CatchUpByObject(rootId, SyncLocalEndpointStore.ObjectId(_volume, id)),
            _settings.LocalDeltaMaxPaths, names.Find, names.Ambiguous, reason => fallbackReason = names.LastFailure ?? reason, Defer, () => scopeOverflow);
        var paths = await LocalDeltaResolver.ResolveAsync(changes, context, ct).ConfigureAwait(false);
        CatchUp?.ObserveResolved(paths?.Count ?? 0);
        if (scopeOverflow || paths is null || paths.Count > _settings.LocalDeltaMaxPaths)
        {
            // Unlike a gap, this batch contains authoritative write evidence. Keep it through fallback.
            PreserveJournalWrites(changes, rootId);
            return Invalid(gen, rootId, cursor, fallbackReason);
        }
        // Persist the hold before any cursor can advance. Do not hash or infer a delete
        // from a journal record whose name/identity is ambiguous.
        _known.RequireRehashBatch(rootId, ambiguous.Values.Where(item => item.Kind == ItemKind.File && !item.Flags.HasFlag(ItemFlags.OwnedPointer)),
            "ambiguous journal identity; scoped verification required");
        var watched = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        paths = WithDefenderDeferredPaths(paths, rootId);
        var exact = paths.Where(path => !UnderAny(path, watched)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var delta = paths.Count == 0
            ? new Delta(rootId, _side, gen, cursor, through.Cursor, Array.Empty<Change>(), false, [])
            : await RefreshAsync(lease, gen, cursor, through.Cursor, include, paths, ct, exact).ConfigureAwait(false);
        // A moved-in directory may expose files with no individual journal record in this interval.
        // Validate those refreshed identities too, before upserts can mistake aliases for old move positions.
        AfterRefreshScan?.Invoke();
        foreach (var change in delta.Changes)
        {
            if (scopeOverflow) break;
            if (change.State is not { Kind: ItemKind.File } state) continue;
            ct.ThrowIfCancellationRequested();
            if (!names.Ambiguous(LocalItems.ParseObjectId(state.ObjectId).FileId, state, requirePresent: true)) continue;
            RunTrace.Emit("local journal post-refresh retry: " + names.LastFailure);
            Defer(new(LocalItems.ParseObjectId(state.ObjectId).FileId, 0, 0, 0, FileAttributes.Normal, ""),
                CatchUpByObject(rootId, state.ObjectId), state.Path,
                names.LastFailure ?? "refreshed file has ambiguous hard-link identity");
        }
        CatchUp?.ObserveAmbiguous(ambiguous.Count);
        if (scopeOverflow) { PreserveJournalWrites(changes, rootId); return Invalid(gen, rootId, cursor, "ambiguous journal scope exceeds bounded path budget"); }
        if (scoped.Count > 0)
        {
            _known.RequireRehashBatch(rootId, ambiguous.Values.Where(item => item.Kind == ItemKind.File && !item.Flags.HasFlag(ItemFlags.OwnedPointer)),
                "ambiguous journal identity; scoped verification required");
            var scopeIndex = PathProtectionIndex.TryCreate(scoped.Keys);
            if (scopeIndex is null) { PreserveJournalWrites(changes, rootId); return Invalid(gen, rootId, cursor, "ambiguous journal scope exceeds bounded path budget"); }
            bool Protected(string path) => scoped.ContainsKey("") ||
                scopeIndex.Touches(path, false) || scopeIndex.Touches(path, true);
            var retained = new List<Change>();
            foreach (var change in delta.Changes)
                if (Protected(change.Path) || ambiguous.ContainsKey(change.ObjectId))
                    Scope(change.Path, scoped.GetValueOrDefault(change.Path) ?? "change overlaps ambiguous journal identity; reread required");
                else retained.Add(change);
            if (scopeOverflow) { PreserveJournalWrites(changes, rootId); return Invalid(gen, rootId, cursor, "ambiguous journal scope exceeds bounded path budget"); }
            delta = delta with
            {
                Changes = retained.ToArray(),
                IncompleteSubtrees = delta.IncompleteSubtrees.Concat(scoped.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                IncompleteReasons = delta.IncompleteReasons.Concat(scoped).GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase)
            };
            RunTrace.Emit($"local journal scoped retry ({rootId}): {scoped.Count} ambiguous path(s); remaining replay retained");
        }
        var written = changes.Where(change => (change.Reason & 0x7) != 0)
            .Select(change => SyncLocalEndpointStore.ObjectId(_volume, change.FileId)).ToHashSet(StringComparer.Ordinal);
        return await RehashAsync(lease, delta, written, ct).ConfigureAwait(false);
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

namespace Massdex.SyncShark.V2;

/// <summary>Simple, correct in-memory IStateStore for contract tests; not optimised.</summary>
/// <remarks>
/// Every public member takes one gate, exactly as <see cref="SqliteStateStore"/> serializes its operations, so the
/// double is safe for concurrent callers too (the scan reads both sides at once). Reads still return copies, so no
/// internal collection escapes under the lock.
/// </remarks>
internal sealed class InMemoryStateStore : IStateStore
{
    private readonly Dictionary<(string Pair, string Path), SyncRetry> _retries = new();
    public IEnumerable<SyncRetry> ReadSyncRetries(string pairId)
    { lock (_gate) return _retries.Where(row => row.Key.Pair == pairId).Select(row => row.Value).ToArray(); }
    public SyncRetry? SyncRetryAt(string pairId, string path)
    { lock (_gate) return _retries.GetValueOrDefault((pairId, path.ToUpperInvariant())); }
    public IReadOnlyList<SyncRetry> DueSyncRetries(string pairId, DateTimeOffset now, int limit) =>
        ReadSyncRetries(pairId).Where(item => item.RetryAfter <= now).OrderBy(item => item.RetryAfter).Take(Math.Clamp(limit, 1, 256)).ToArray();
    // An indefinite pair-wide decision hold (Kind decision at the root path) is not scheduling debt, but one still
    // waiting on a failed notice write (PendingNotice set) is.
    public int CountSyncRetries(string pairId) => ReadSyncRetries(pairId).Count(item =>
        item.RetryAfter < DateTimeOffset.MaxValue &&
        !(item.Path.Length == 0 && item.Kind == "decision" && item.PendingNotice is null));
    public bool HasActiveRestoredRetries(string pairId) =>
        ReadSyncRetries(pairId).Any(item => item.ReprobeEvidence && item.RetryAfter < DateTimeOffset.MaxValue);
    public bool RetryProtected(string pairId, string path, IReadOnlySet<string> active, bool descendants, bool restoredOnly = false) =>
        ReadSyncRetries(pairId).Any(item => (!restoredOnly || item.ReprobeEvidence) &&
            !(item.ReprobeEvidence && item.RetryAfter == DateTimeOffset.MaxValue) && !active.Contains(item.Path) && (descendants
            ? PairPath.IsSelfOrUnder(item.Path, path) : PairPath.IsSelfOrUnder(path, item.Path)));
    public void SaveSyncRetry(string pairId, SyncRetry retry)
    { lock (_gate) _retries[(pairId, retry.Path.ToUpperInvariant())] = retry; }
    public void ClearSyncRetry(string pairId, string path)
    { lock (_gate) _retries.Remove((pairId, path.ToUpperInvariant())); }

    public void SuspendHeldRetry(string pairId, SyncRetry retry)
    {
        lock (_gate)
        {
            SaveSyncRetry(pairId, new(retry.Path, "deletion-evidence", 0, retry.LastError,
                DateTimeOffset.MaxValue, Held: retry.Held, ReprobeEvidence: true));
            foreach (var notice in _notices.Where(n => n.PairId == pairId && PairPath.Same(n.Path, retry.Path) &&
                n.ReasonKey is "sync-retry" or "deletion-evidence-pending"))
                _closedNotices[notice.NoticeId!] = NoticeChoices.Exclude;
        }
    }

    private const long LiveToGen = long.MaxValue;

    private sealed record Generation(string RootId, string Kind);
    private sealed record Row(ItemState State, long ToGen, long FromGen);
    private sealed record IntentRecord(SyncAction Action, string State, long Updated, long RowId);

    private readonly object _gate = new();
    private readonly Dictionary<long, Generation> _generations = new();
    private readonly Dictionary<string, Dictionary<string, Row>> _live = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _objectPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _cursors = new(StringComparer.Ordinal);
    private readonly List<(string RootId, Row Row)> _closed = new();
    private readonly Dictionary<string, Dictionary<string, Baseline>> _baselines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, IntentRecord>> _intents = new(StringComparer.Ordinal);
    private readonly List<Notice> _notices = new();
    private readonly Dictionary<string, string?> _closedNotices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PairDef> _pairs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _firstSyncDone = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, RecoveryRecord>> _recoveries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, DeletionEvidenceRecord>> _deletionEvidence = new(StringComparer.Ordinal);
    private readonly Dictionary<RootDef, DeletionClassification> _classification = new();
    public DeletionClassification? ReadDeletionClassification(RootDef root)
    {
        lock (_gate) return _classification.GetValueOrDefault(root);
    }
    public void ReplaceDeletionClassification(RootDef root, string cursor, IReadOnlySet<string> exact, IReadOnlySet<string> ancestors)
    {
        lock (_gate) _classification[root] = new(cursor,
            exact.ToHashSet(StringComparer.OrdinalIgnoreCase), ancestors.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }
    public void ReplayDeletionClassification(RootDef root, string cursor, IEnumerable<(string Path, bool Deleted)> changes)
    {
        lock (_gate)
        {
            var old = _classification[root];
            var exact = old.Exact.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ancestors = old.Ancestors.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var row in changes)
                if (row.Deleted) { exact.Add(row.Path); ancestors.Add(row.Path); }
                else exact.Remove(row.Path);
            _classification[root] = new(cursor, exact, ancestors);
        }
    }
    private readonly Dictionary<(string Root, string Object), (long Attempt, string Reason)> _rehash = new();
    private long _rehashAttempt;

    public void RequireRehash(string rootId, ItemState item, string reason)
    {
        lock (_gate)
        {
            _rehash[(rootId, item.ObjectId)] = (++_rehashAttempt, reason);
            var live = LiveRows(rootId);
            foreach (var (path, row) in live.Where(p => p.Value.State.ObjectId == item.ObjectId).ToArray())
                live[path] = row with { State = row.State with { Hash = null, Flags = row.State.Flags | ItemFlags.NeedsRehash } };
        }
    }

    public IReadOnlyList<string> ObjectPaths(string rootId, string objectId, int offset, int limit) =>
        Live(rootId).Where(i => i.ObjectId == objectId).Select(i => i.Path).Skip(offset).Take(limit).ToArray();

    public long RehashWatermark(string rootId)
    {
        lock (_gate) return _rehash.Where(p => p.Key.Root == rootId).Select(p => p.Value.Attempt).DefaultIfEmpty().Max();
    }

    public void AcknowledgeRehash(string rootId, long through, Func<string, bool> settled, CancellationToken ct = default)
    {
        lock (_gate)
            foreach (var key in _rehash.Where(p => p.Key.Root == rootId && p.Value.Attempt <= through).Select(p => p.Key).ToArray())
            {
                ct.ThrowIfCancellationRequested();
                var aliases = LiveRows(rootId).Values.Where(r => r.State.ObjectId == key.Object).Select(r => r.State).ToArray();
                if (aliases.Length > 0 && aliases.All(i => i.Hash is not null && (i.Flags & ItemFlags.NeedsRehash) == 0 && settled(i.Path)))
                    _rehash.Remove(key);
            }
    }

    public IReadOnlyList<ItemState> RehashCandidates(string rootId, int limit) => RehashCandidates(rootId, limit, RehashWatermark(rootId));

    public IReadOnlyList<ItemState> RehashCandidates(string rootId, int limit, long through)
    {
        lock (_gate)
        {
            var rows = _rehash.Where(pair => pair.Key.Root == rootId && pair.Value.Attempt <= through).OrderBy(pair => pair.Value.Attempt)
                .Select(pair => LiveRows(rootId).Values.FirstOrDefault(row => row.State.ObjectId == pair.Key.Object)?.State)
                .Where(item => item is not null).Take(limit).Select(item => item!).ToArray();
            foreach (var row in rows) _rehash[(rootId, row.ObjectId)] = (++_rehashAttempt, _rehash[(rootId, row.ObjectId)].Reason);
            return rows;
        }
    }

    public bool CompleteRehash(string rootId, ItemState expected, string hash)
    {
        lock (_gate)
        {
            var live = LiveRows(rootId);
            if (!live.TryGetValue(expected.Path, out var row) || row.State.ObjectId != expected.ObjectId ||
                row.State.Size != expected.Size || row.State.Mtime != expected.Mtime) return false;
            var aliases = live.Where(pair => pair.Value.State.ObjectId == expected.ObjectId).ToArray();
            if (aliases.Any(pair => pair.Value.State.Size != expected.Size || pair.Value.State.Mtime != expected.Mtime)) return false;
            foreach (var (path, alias) in aliases)
                live[path] = alias with { State = alias.State with { Hash = hash, Flags = alias.State.Flags & ~ItemFlags.NeedsRehash } };
            return true;
        }
    }

    private long _nextGeneration = 1;
    private long _noticeSeq;
    // Mirrors SQLite's (updated, rowid) intent order: a monotonic update stamp and a stable row identity.
    private long _intentUpdated;
    private long _nextIntentRowId;

    public long BeginGeneration(string rootId, string kind)
    {
        lock (_gate)
        {
            var gen = _nextGeneration++;
            _generations[gen] = new Generation(rootId, kind);
            return gen;
        }
    }

    public void WriteSnapshot(Snapshot s)
    {
        lock (_gate)
        {
            s.PendingDeletionEvidence.Clear();
            var live = LiveRows(s.RootId);
            var incomplete = s.IncompleteSubtrees;
            // A snapshot is authoritative per exact spelling: case clashes it reports are all kept (RQ-039).
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in s.Items)
            {
                if (!(s.IncludesPath?.Invoke(item.Path) ?? true) || (incomplete.Count > 0 && IsInsideIncomplete(item.Path, incomplete))) continue;
                seen.Add(item.Path);
                Put(s.RootId, item, s.Gen);
                ForgetUnderLink(s.RootId, item);
            }
            // Close rows absent from the snapshot, but only outside incomplete subtrees.
            foreach (var path in live.Keys.ToList())
            {
                if (seen.Contains(path) || IsInsideIncomplete(path, incomplete) ||
                    !(s.IncludesPath?.Invoke(path) ?? true) || !s.CanCloseAbsent(live[path].State)) continue;
                Close(s.RootId, path, s.Gen);
            }
        }
    }

    public void WriteDelta(Delta d)
    {
        lock (_gate)
        {
            // Every Delete first, then every Upsert (storage.md 4.4); a folder Delete closes descendants (run.md R-S5).
            // Like Storage, move suppression applies after selection: a row whose object is upserted later in the delta
            // is left for that Upsert (B-128), so Delete(X) + Upsert(X) at one spelling never displaces a clash sibling.
            var upserted = d.Changes.Where(change => change.Kind == ChangeKind.Upsert &&
                (d.IncludesPath?.Invoke(change.Path) ?? true))
                .Select(change => change.ObjectId).ToHashSet(StringComparer.Ordinal);
            foreach (var change in d.Changes)
                if (change.Kind == ChangeKind.Delete)
                {
                    var live = LiveRows(d.RootId);
                    foreach (var path in DeleteTargets(live, change))
                        if ((d.IncludesPath?.Invoke(path) ?? true) &&
                            !upserted.Contains(live[path].State.ObjectId)) Close(d.RootId, path, d.Gen);
                }
            foreach (var change in d.Changes)
                if (change.Kind == ChangeKind.Upsert && change.State is not null &&
                    (d.IncludesPath?.Invoke(change.Path) ?? true))
                {
                    Upsert(d.RootId, change.State, d.Gen, d.IncludesPath);
                    ForgetUnderLink(d.RootId, change.State);
                }
        }
    }

    // As Storage (decision 59): writing a link forgets every baseline at or under it on that root's side, at once.
    private void ForgetUnderLink(string rootId, ItemState item)
    {
        if ((item.Flags & ItemFlags.Link) == 0) return;
        foreach (var pair in _pairs.Values)
        {
            var side = pair.A.RootId == rootId ? Side.A : pair.B.RootId == rootId ? Side.B : (Side?)null;
            if (side is null || !_baselines.TryGetValue(pair.PairId, out var map)) continue;
            foreach (var key in map.Where(entry => PairPath.IsSelfOrUnder(
                         (side == Side.A ? entry.Value.A : entry.Value.B).Path, item.Path)).Select(entry => entry.Key).ToArray())
                map.Remove(key);
        }
    }

    public void CompleteGeneration(long gen, string cursor, bool complete)
    {
        lock (_gate)
            // As Storage: only a complete generation commits its cursor.
            if (complete && _generations.TryGetValue(gen, out var generation)) _cursors[generation.RootId] = cursor;
    }

    public IReadOnlyList<ItemState> LiveFlaggedFolders(string rootId, ItemFlags flags)
    {
        lock (_gate)
            return !_live.TryGetValue(rootId, out var rows) ? Array.Empty<ItemState>() :
                rows.Values.Where(row => row.State.Kind == ItemKind.Folder && (row.State.Flags & flags) != 0)
                    .Select(row => row.State).ToArray();
    }

    public (long Count, IReadOnlyList<ItemState> Examples) LiveFlaggedSummary(string rootId, ItemFlags flags, int limit)
    {
        lock (_gate)
        {
            long count = 0;
            var examples = new List<ItemState>();
            if (_live.TryGetValue(rootId, out var rows))
                foreach (var row in rows.Values)
                {
                    if ((row.State.Flags & flags) == 0) continue;
                    count++;
                    if (examples.Count < limit) examples.Add(row.State);
                }
            return (count, examples);
        }
    }

    public string? Cursor(string rootId)
    {
        lock (_gate) return _cursors.TryGetValue(rootId, out var cursor) ? cursor : null;
    }

    public void ForgetCursor(string rootId)
    {
        lock (_gate) _cursors.Remove(rootId);
    }

    public bool FirstSyncDone(string pairId)
    {
        lock (_gate) return _firstSyncDone.Contains(pairId);
    }

    public void MarkFirstSyncDone(string pairId)
    {
        lock (_gate)
        {
            if (!_pairs.ContainsKey(pairId)) throw new InvalidOperationException("Unknown pair " + pairId);
            _firstSyncDone.Add(pairId);
        }
    }

    public IReadOnlyList<ItemState> LiveChildren(string rootId, string directory) =>
        Live(rootId).Where(i => PairPath.Same(RowCodec.Split(i.Path).Parent, directory)).ToArray();

    public IReadOnlyList<string> DirectorySpellings(string rootId, string path) =>
        Live(rootId).Select(i => RowCodec.Split(i.Path).Parent).Distinct(StringComparer.Ordinal)
            .Where(p => PairPath.Same(DropboxNameMapping.Map(p), DropboxNameMapping.Map(path))).ToArray();

    public IReadOnlyList<ItemState> Live(string rootId, IEnumerable<string>? paths = null)
    {
        lock (_gate)
        {
            if (!_live.TryGetValue(rootId, out var live)) return Array.Empty<ItemState>();
            IEnumerable<ItemState> items = live.Values.Select(row => row.State);
            if (paths is not null)
            {
                // Path lookups match ignoring case , one row per path: the exact spelling when held.
                items = paths.Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(path => live.TryGetValue(path, out var exact)
                        ? exact.State
                        : items.Where(state => PairPath.Same(state.Path, path))
                            .OrderBy(state => state.Path, StringComparer.Ordinal).FirstOrDefault())
                    .OfType<ItemState>().ToList();
            }
            return items.OrderBy(state => state.Path, StringComparer.Ordinal).ToArray();
        }
    }

    public IReadOnlyList<ItemState> LiveUnder(string rootId, IEnumerable<string> pathPrefixes)
    {
        lock (_gate)
        {
            if (!_live.TryGetValue(rootId, out var live)) return Array.Empty<ItemState>();
            var prefixes = pathPrefixes.ToArray();
            return live.Values.Select(row => row.State)
                .Where(state => prefixes.Any(prefix => IsSelfOrUnder(state.Path, prefix)))
                .OrderBy(state => state.Path, StringComparer.Ordinal).ToArray();
        }
    }

    public ItemState? ByObject(string rootId, string objectId)
    {
        lock (_gate)
        {
            if (_objectPaths.TryGetValue(rootId, out var map) && map.TryGetValue(objectId, out var path) &&
                _live.TryGetValue(rootId, out var live) && live.TryGetValue(path, out var row))
                return row.State;
            return null;
        }
    }

    public IReadOnlyList<PairDef> Pairs()
    {
        lock (_gate) return _pairs.Values.OrderBy(pair => pair.PairId, StringComparer.Ordinal).ToArray();
    }

    public PairDef? Pair(string pairId)
    {
        lock (_gate) return _pairs.TryGetValue(pairId, out var pair) ? pair : null;
    }

    public void SavePair(PairDef p)
    {
        lock (_gate)
        {
            _pairs.TryGetValue(p.PairId, out var previous);
            if (previous is null || previous.A != p.A || previous.B != p.B) _cursors.Clear();
            else if (previous.Excluded.Any(path => !p.Excluded.Any(parent => PairPath.IsSelfOrUnder(path, parent))) ||
                previous.IgnoreRules.Except(p.IgnoreRules, StringComparer.OrdinalIgnoreCase).Any())
            {
                _cursors.Remove(p.A.RootId);
                _cursors.Remove(p.B.RootId);
            }
            _pairs[p.PairId] = p;
        }
    }

    public int SavePairIncluded(PairDef previous, PairDef included)
    {
        lock (_gate)
        {
            if (_pairs.GetValueOrDefault(previous.PairId) != previous)
                throw new InvalidOperationException("The pair changed.");
            var paths = previous.Excluded.Where(path => !included.Excluded.Any(retained =>
                PairPath.IsSelfOrUnder(path, retained))).ToArray();
            if (_baselines.TryGetValue(previous.PairId, out var baselines))
                foreach (var key in baselines.Where(entry => paths.Any(path =>
                    PairPath.IsSelfOrUnder(entry.Value.A.Path, path) || PairPath.IsSelfOrUnder(entry.Value.B.Path, path)))
                    .Select(entry => entry.Key).ToArray()) baselines.Remove(key);
            foreach (var root in new[] { previous.A.RootId, previous.B.RootId })
                if (_deletionEvidence.TryGetValue(previous.PairId + "\n" + root, out var evidence))
                    foreach (var path in evidence.Keys.Where(item => paths.Any(prefix =>
                        PairPath.IsSelfOrUnder(item, prefix))).ToArray()) evidence.Remove(path);
            var heldRows = ReadSyncRetries(previous.PairId).Where(retry => retry.Held is not null)
                .ToDictionary(retry => retry.Path, StringComparer.OrdinalIgnoreCase);
            if (RebuildableSelection.Sides(included) is { } sides)
                foreach (var notice in _notices.Where(notice => notice.PairId == previous.PairId && notice.ReasonKey == "deletion-evidence-pending" &&
                    notice.NoticeId is not null && _closedNotices.TryGetValue(notice.NoticeId, out var choice) &&
                    choice is NoticeChoices.Ignore or NoticeChoices.Exclude))
                    heldRows.TryAdd(notice.Path, new(notice.Path, "deletion-evidence", 0, "", DateTimeOffset.MinValue,
                        Held: new HeldDeletion(notice.Path, sides.Local)));
            foreach (var retry in heldRows.Values.Where(retry => paths.Any(prefix => PairPath.IsSelfOrUnder(retry.Path, prefix)) &&
                !included.Excluded.Any(retained => PairPath.IsSelfOrUnder(retry.Path, retained))))
                SaveSyncRetry(previous.PairId, new(retry.Path, "deletion-evidence", 0, "", DateTimeOffset.MinValue,
                    Held: retry.Held, ReprobeEvidence: true));
            // Mirror SavePairIncluded: removing an exclusion reverses its Ignore, so a fresh problem at the path
            // may raise its notice again.
            foreach (var notice in _notices.Where(item => item.PairId == previous.PairId && item.NoticeId is not null &&
                         paths.Any(prefix => PairPath.IsSelfOrUnder(item.Path, prefix))).ToArray())
                if (_closedNotices.TryGetValue(notice.NoticeId!, out var choice) && choice is NoticeChoices.Ignore or NoticeChoices.Exclude)
                    _closedNotices[notice.NoticeId!] = null;
            var affected = UnresolvedIntents(previous.PairId).Where(action =>
                new[] { action.Path, action.FromPath, action.Source?.Path, action.ExpectedTarget?.Path }
                    .OfType<string>().Any(footprint => paths.Any(path =>
                        PairPath.IsSelfOrUnder(footprint, path) || PairPath.IsSelfOrUnder(path, footprint)))).ToArray();
            foreach (var action in affected)
            {
                var record = IntentsFor(previous.PairId)[action.ActionId];
                IntentsFor(previous.PairId)[action.ActionId] = record with { Action = action with { SuppressReconciledBaseline = true } };
            }
            SavePair(included);
            return affected.Length;
        }
    }

    public IReadOnlyList<Baseline> Baselines(string pairId, IEnumerable<string>? objectKeys = null)
    {
        lock (_gate)
        {
            if (!_baselines.TryGetValue(pairId, out var map)) return Array.Empty<Baseline>();
            IEnumerable<Baseline> values = map.Values;
            if (objectKeys is not null)
            {
                var wanted = new HashSet<string>(objectKeys, StringComparer.Ordinal);
                values = values.Where(baseline => wanted.Contains(baseline.ObjectKey));
            }
            return values.ToArray();
        }
    }

    public IReadOnlyList<Baseline> BaselinesAt(string pairId, Side side, IEnumerable<string> paths)
    {
        var exact = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Baselines(pairId).Where(b => exact.Contains(side == Side.A ? b.A.Path : b.B.Path)).ToArray();
    }

    public IReadOnlyList<Baseline> BaselinesTouching(string pairId, Side side, IEnumerable<string> objectIds,
        IEnumerable<string> pathPrefixes)
    {
        lock (_gate)
        {
            if (!_baselines.TryGetValue(pairId, out var map)) return Array.Empty<Baseline>();
            var ids = new HashSet<string>(objectIds, StringComparer.Ordinal);
            var prefixes = pathPrefixes.ToArray();
            return map.Values.Where(baseline =>
            {
                var state = side == Side.A ? baseline.A : baseline.B;
                return ids.Contains(state.ObjectId) || prefixes.Any(prefix => IsSelfOrUnder(state.Path, prefix));
            }).ToArray();
        }
    }

    public void CommitResult(string pairId, long gen, SyncAction a, ActionResult r)
    {
        lock (_gate)
        {
            if (IntentsFor(pairId).GetValueOrDefault(a.ActionId)?.Action.SuppressReconciledBaseline == true)
                a = a with { SuppressReconciledBaseline = true };
            // a resolved intent (Done, Skipped) is deleted with its result, like the real store.
            // Any other outcome overwrites held, so a committer never leaves a stale hold behind.
            var intents = IntentsFor(pairId);
            var intentRow = intents.TryGetValue(a.ActionId, out var knownIntent) ? knownIntent.RowId : ++_nextIntentRowId;
            if (r.Outcome is Outcome.Done or Outcome.Skipped)
            {
                intents.Remove(a.ActionId);
                // M1: a resolved or retired intent never leaves a parked verification retry behind.
                var parkedKey = (pairId, a.Path.ToUpperInvariant());
                if (_retries.GetValueOrDefault(parkedKey) is { Kind: "sync", Held: null } parked &&
                    parked.RetryAfter == DateTimeOffset.MaxValue) _retries.Remove(parkedKey);
            }
            else intents[a.ActionId] = new IntentRecord(a, r.Outcome.ToString().ToLowerInvariant(), ++_intentUpdated, intentRow);

            if (r.Recovery is { } recovery)
            {
                var now = DateTimeOffset.UtcNow;
                RecoveriesFor(pairId)[a.ActionId] = new RecoveryRecord("rc:" + pairId + ":" + a.ActionId, pairId,
                    a.ActionId, recovery.Side, a.Path, recovery.Location, recovery.VersionId, recovery.Hash,
                    recovery.Size, now, now + V2Settings.Default.RecoveryRetention);
            }

            if (r.ProviderNameRefused)
            {
                var pair = _pairs[pairId];
                var root = a.Target == Side.A ? pair.B.RootId : pair.A.RootId;
                var source = a.Source!;
                var rows = LiveRows(root);
                var row = rows[source.Path];
                rows[source.Path] = row with { State = row.State with { Flags = row.State.Flags | ItemFlags.ProviderNameRefused } };
                return;
            }
            if (SyncDirectDownloadCheckpoint.NoticeFor(pairId, a, r) is { } notice) AddNotices([notice]);
            if (r.Outcome == Outcome.Skipped && r.ForgetAbsentDownloadBaseline && a.ObjectKey is not null)
                BaselinesFor(pairId).Remove(a.ObjectKey);
            if (r.Outcome != Outcome.Done) return;
            if (a.Kind is ActionKind.CopyNew or ActionKind.Update or ActionKind.Move or ActionKind.MkDir or ActionKind.KeepBoth &&
                (a.Target == Side.A ? r.NewA : r.NewB) is { } written)
            {
                var pair = _pairs[pairId];
                var rows = LiveRows(a.Target == Side.A ? pair.A.RootId : pair.B.RootId);
                if (rows.TryGetValue(written.Path, out var row) && row.State.Path == written.Path &&
                    (row.State.ObjectId == written.ObjectId ||
                     (a.ExpectedTarget is { } expected && expected.Path == written.Path && row.State.ObjectId == expected.ObjectId)))
                    rows[written.Path] = row with { State = row.State with { Flags = row.State.Flags & ~ItemFlags.ProviderNameRefused } };
            }
            if (r.Outcome == Outcome.Done)
            {
                var winner = a.Target == Side.A ? r.NewA : r.NewB;
                if (winner is not null)
                {
                    var index = _notices.FindIndex(notice => notice.PairId == pairId &&
                        notice.RetainedVersion?.ActionId == a.ActionId && notice.RetainedVersion.Side == a.Target);
                    if (index >= 0 && !_closedNotices.ContainsKey(_notices[index].NoticeId!))
                        _notices[index] = _notices[index] with
                        { RetainedVersion = _notices[index].RetainedVersion! with { Winner = winner } };
                }
                if (a.ResolvedNoticeId is { } resolvedNotice && !_closedNotices.ContainsKey(resolvedNotice) &&
                    _notices.Any(notice => notice.PairId == pairId && notice.NoticeId == resolvedNotice))
                {
                    if (a.Kind == ActionKind.Update && r.Recovery is not null &&
                        a.ExpectedTarget is { } displaced && winner is { } restored)
                        AddNotices([new Notice(pairId, a.Rule, NoticeKind.AutoConflict, a.Path,
                            "The other version was restored. The displaced version is retained; use it again, save it as a copy, or dismiss this notice.",
                            "retained-version-restored")
                        { RetainedVersion = new RetainedNoticeVersion(a.ActionId, a.Target, displaced, restored) }]);
                    _closedNotices[resolvedNotice] = a.Kind == ActionKind.CopyNew ? "save-copy" : "use-other";
                }
            }
            if (r.Outcome != Outcome.Done || a.SuppressReconciledBaseline) return;


            var baselines = BaselinesFor(pairId);
            if (a.Kind is ActionKind.Delete or ActionKind.Forget)
            {
                if (a.ObjectKey is not null) baselines.Remove(a.ObjectKey);
                return;
            }
            if (a.ObjectKey is null) return;
            var existing = baselines.TryGetValue(a.ObjectKey, out var current) ? current : null;
            var other = a.Target == Side.A ? Side.B : Side.A;
            var target = (a.Target == Side.A ? r.NewA : r.NewB) ??
                (a.Kind == ActionKind.Adopt ? a.ExpectedTarget : null) ?? (a.Target == Side.A ? existing?.A : existing?.B);
            var otherState = (a.Target == Side.A ? r.NewB : r.NewA) ?? a.Source ??
                (other == Side.A ? existing?.A : existing?.B);
            if (target is null || otherState is null) return;
            baselines[a.ObjectKey] = a.Target == Side.A
                ? new Baseline(pairId, a.ObjectKey, target, otherState, gen)
                : new Baseline(pairId, a.ObjectKey, otherState, target, gen);
        }
    }

    public void RecordIntent(string pairId, SyncAction a)
    {
        lock (_gate)
        {
            // Upsert semantics: a repeated action id replaces the row, keeps its stable row identity and clears a hold.
            var intents = IntentsFor(pairId);
            var rowId = intents.TryGetValue(a.ActionId, out var existing) ? existing.RowId : ++_nextIntentRowId;
            intents[a.ActionId] = new IntentRecord(a, "started", ++_intentUpdated, rowId);
        }
    }

    public IReadOnlyList<SyncAction> UnresolvedIntents(string pairId)
    {
        lock (_gate)
            return _intents.TryGetValue(pairId, out var map)
                ? map.Values.Where(IsUnresolved).OrderBy(intent => intent.Updated)
                    .ThenBy(intent => intent.Action.ActionId, StringComparer.Ordinal)
                    .Select(intent => intent.Action).ToArray()
                : Array.Empty<SyncAction>();
    }

    public int CountUnresolvedIntents(string pairId)
    {
        lock (_gate) return _intents.TryGetValue(pairId, out var map) ? map.Values.Count(IsUnresolved) : 0;
    }

    public void SetIntentHeld(string pairId, string actionId, bool held)
    {
        lock (_gate)
        {
            RequireIntentOwner(pairId, actionId);
            ApplyHeld(pairId, actionId, held);
        }
    }

    // Ownership matches SQLite's primary-key rule: an id already owned by another pair is rejected.
    private void RequireIntentOwner(string pairId, string actionId)
    {
        if (_intents.Any(entry => !string.Equals(entry.Key, pairId, StringComparison.Ordinal) &&
                entry.Value.ContainsKey(actionId)))
            throw new InvalidOperationException($"Intent {actionId} belongs to another pair.");
    }

    // A missing id is a no-op, never a reinsert; an unchanged state is not rewritten.
    private void ApplyHeld(string pairId, string actionId, bool held)
    {
        if (!_intents.TryGetValue(pairId, out var map) || !map.TryGetValue(actionId, out var record)) return;
        if (held)
        {
            if (record.State != "held") map[actionId] = record with { State = "held" };
        }
        else if (record.State == "held")
        {
            // Rollback returns a hold to uncertain; attempts and the update stamp stay untouched.
            map[actionId] = record with { State = "uncertain" };
        }
    }

    public int CountHeldIntents(string pairId)
    {
        lock (_gate) return _intents.TryGetValue(pairId, out var map)
            ? map.Values.Count(intent => intent.State == "held") : 0;
    }

    public bool IsIntentHeld(string pairId, string actionId)
    {
        lock (_gate) return _intents.TryGetValue(pairId, out var map) &&
            map.TryGetValue(actionId, out var record) && record.State == "held";
    }

    public void SetIntentsHeld(string pairId, IReadOnlyList<string> actionIds, bool held)
    {
        ArgumentNullException.ThrowIfNull(actionIds);
        // Mirror SQLite exactly: one 256-id transaction per chunk. Validate every ownership first so a later
        // foreign id rolls the whole chunk back, while earlier committed chunks stay committed.
        for (var offset = 0; offset < actionIds.Count; offset += 256)
        {
            var end = Math.Min(offset + 256, actionIds.Count);
            lock (_gate)
            {
                for (var index = offset; index < end; index++) RequireIntentOwner(pairId, actionIds[index]);
                for (var index = offset; index < end; index++) ApplyHeld(pairId, actionIds[index], held);
            }
        }
    }

    // Mirrors SQLite: a fixed rowid horizon captured once, then bounded pages in rowid order. Rows whose state
    // becomes resolved after the horizon was captured simply drop out of the matching set; rowids never move.
    public IEnumerable<IntentRow> StreamUnresolvedIntentRows(string pairId)
    {
        long through;
        lock (_gate)
            through = _intents.TryGetValue(pairId, out var map) && map.Count > 0
                ? map.Values.Max(intent => intent.RowId) : 0;
        long after = 0;
        while (after < through)
        {
            List<(IntentRow Row, long RowId)> page;
            lock (_gate)
                page = _intents.TryGetValue(pairId, out var map)
                    ? map.Values.Where(intent => IsUnresolved(intent) && intent.RowId > after && intent.RowId <= through)
                        .OrderBy(intent => intent.RowId).Take(201)
                        .Select(intent => (new IntentRow(intent.Action, intent.State), intent.RowId)).ToList()
                    : new List<(IntentRow Row, long RowId)>();
            if (page.Count == 0) yield break;
            foreach (var row in page.Take(200)) yield return row.Row;
            if (page.Count <= 200) yield break;
            after = page[199].RowId;
        }
    }

    public IEnumerable<SyncAction> StreamUnresolvedIntents(string pairId) =>
        StreamUnresolvedIntentRows(pairId).Select(row => row.Action);

    public string? PairForIntent(string actionId)
    {
        lock (_gate)
            return _intents.FirstOrDefault(entry => entry.Value.ContainsKey(actionId)).Key;
    }

    public IntentPage HeldIntentsPage(string pairId, string? cursor, int limit)
    {
        if (limit is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "Intent pages are limited to 1..200.");
        var after = cursor is null ? ((long Updated, long RowId)?)null : IntentPageCursor.Parse(cursor);
        lock (_gate)
        {
            if (!_intents.TryGetValue(pairId, out var map)) return new IntentPage([], null);
            IEnumerable<IntentRecord> rows = map.Values.Where(intent => intent.State == "held")
                .OrderBy(intent => intent.Updated).ThenBy(intent => intent.RowId);
            if (after is { } from)
                rows = rows.Where(intent => intent.Updated > from.Updated ||
                    (intent.Updated == from.Updated && intent.RowId > from.RowId));
            var page = rows.Take(limit + 1).ToArray();
            var items = page.Take(limit).Select(intent => intent.Action).ToArray();
            var next = page.Length > limit ? IntentPageCursor.Encode(page[limit - 1].Updated, page[limit - 1].RowId) : null;
            return new IntentPage(items, next);
        }
    }

    private static bool IsUnresolved(IntentRecord intent) =>
        intent.State is "started" or "uncertain" or "failed" or "held";

    public void AddNotices(IEnumerable<Notice> n)
    {
        lock (_gate)
        {
            // Decision 26: one notice per open problem (pair, path, rule, reason). Raising it again while open adds
            // nothing; raising it after it was closed reopens it.
            foreach (var notice in n)
            {
                var same = _notices.LastOrDefault(known => known.PairId == notice.PairId && known.Rule == notice.Rule &&
                    known.ReasonKey == notice.ReasonKey && PairPath.Same(known.Path, notice.Path) &&
                    known.RetainedVersion?.ActionId == notice.RetainedVersion?.ActionId);
                if (same is not null)
                {
                    // Mirror NoticeTable.Add: an ignored retry notice stays closed; every other repeated notice
                    // reopens, and a retry notice refreshes its display text without changing problem identity.
                    if (NoticeChoices.IsRetryNotice(notice) && _closedNotices.GetValueOrDefault(same.NoticeId!) == "ignore") continue;
                    if (notice.RetainedVersion is null) _closedNotices.Remove(same.NoticeId!);
                    if (NoticeChoices.IsRetryNotice(notice) || notice.FreeSpace is not null ||
                        (notice.Kind == NoticeKind.UserDecision && notice.Detail.Contains(ProviderNameRefusedException.Detail, StringComparison.Ordinal)) ||
                        notice.ReasonKey == ProviderNameRefusedException.NoticeKey)
                        _notices[_notices.IndexOf(same)] = notice with { NoticeId = same.NoticeId };

                    continue;
                }
                _notices.Add(notice.NoticeId is null ? notice with { NoticeId = "n" + (++_noticeSeq) } : notice);
            }
        }
    }

    public IReadOnlyList<Notice> OpenNotices(string? pairId)
    {
        lock (_gate)
            return _notices.Where(notice => notice.NoticeId is not null && !_closedNotices.ContainsKey(notice.NoticeId) &&
                (pairId is null || string.Equals(notice.PairId, pairId, StringComparison.Ordinal))).ToArray();
    }

    public IReadOnlyList<RecoveryRecord> Recoveries(string pairId)
    {
        lock (_gate) return _recoveries.TryGetValue(pairId, out var map) ? map.Values.ToArray() : Array.Empty<RecoveryRecord>();
    }

    public RecoveryRecord? RecoveryForAction(string pairId, string actionId)
    {
        lock (_gate) return _recoveries.TryGetValue(pairId, out var map) ? map.GetValueOrDefault(actionId) : null;
    }

    public SyncAction? IntentById(string pairId, string actionId)
    {
        lock (_gate) return _intents.TryGetValue(pairId, out var map) ? map.GetValueOrDefault(actionId)?.Action : null;
    }

    public (Notice Notice, bool Closed)? NoticeById(string pairId, string noticeId)
    {
        lock (_gate)
        {
            var notice = _notices.FirstOrDefault(row => row.PairId == pairId && row.NoticeId == noticeId);
            return notice is null ? null : (notice, _closedNotices.ContainsKey(noticeId));
        }
    }

    public void RecordNoticeChoiceFailure(Notice original, string failure)
    {
        lock (_gate)
        {
            var index = _notices.FindIndex(n => n.PairId == original.PairId && n.NoticeId == original.NoticeId);
            if (index < 0) return;
            _notices[index] = _notices[index] with { Detail = original.Detail + "\n\n" + failure };
            _closedNotices.Remove(original.NoticeId!);
        }
    }

    public void CloseNoticeFamily(string pairId, string reasonPrefix, string? exceptReason = null)
    {
        lock (_gate)
            foreach (var n in _notices.Where(n => n.PairId == pairId && n.ReasonKey?.StartsWith(reasonPrefix, StringComparison.Ordinal) == true && n.ReasonKey != exceptReason))
                _closedNotices[n.NoticeId!] = "resolved";
    }

    public void CloseNotice(string noticeId, string? choice)
    {
        lock (_gate) _closedNotices[noticeId] = choice;
    }

    private readonly List<RunSummary> _runHistory = [];
    private long _runSequence;

    public Action<RunSummary>? BeforeRecordSummary { get; set; }

    public void RecordRunSummary(RunSummary summary)
    {
        lock (_gate)
        {
            BeforeRecordSummary?.Invoke(summary);
            if (_runHistory.Any(row => row.RunId == summary.RunId)) return;
            _runHistory.Add(summary with { Seq = ++_runSequence });
        }
    }

    public IReadOnlyList<RunSummary> ReadRunHistory(string? pairId = null, int limit = 50, long before = 0)
    {
        lock (_gate)
            return _runHistory
                .Where(row => pairId is null || row.PairId == pairId)
                .Where(row => before == 0 || row.Seq < before)
                .OrderByDescending(row => row.Seq).Take(limit).ToArray();
    }

    public IReadOnlyList<RunSummary> ReadRunHistoryAfter(long after, int limit = 50)
    {
        lock (_gate)
            return _runHistory.Where(row => row.Seq > after).OrderBy(row => row.Seq).Take(limit).ToArray();
    }

    public IReadOnlyList<RunSummary> ReadRunHistoryPage(string? pairId, int limit, int offset)
    {
        lock (_gate)
            return _runHistory
                .Where(row => pairId is null || row.PairId == pairId)
                .OrderByDescending(row => row.Seq).Skip(offset).Take(limit).ToArray();
    }

    public RunSummary? RunSummaryById(string runId)
    {
        lock (_gate) return _runHistory.FirstOrDefault(row => row.RunId == runId);
    }

    /// <summary>The choice recorded when a notice was closed, or null when it is still open; a test-only reader.</summary>
    internal string? ClosedChoice(string noticeId)
    {
        lock (_gate) return _closedNotices.TryGetValue(noticeId, out var choice) ? choice : null;
    }

    public IReadOnlyList<DeletionEvidenceRecord> DeletionEvidenceRows(string pairId, string rootId)
    {
        lock (_gate) return _deletionEvidence.TryGetValue(pairId + "\n" + rootId, out var map)
            ? map.Values.ToArray() : [];
    }

    public void RemoveDeletionEvidence(string pairId, string rootId, IReadOnlyList<string> paths)
    {
        lock (_gate)
            if (_deletionEvidence.TryGetValue(pairId + "\n" + rootId, out var map))
                foreach (var path in paths) map.Remove(path);
    }

    public void StoreDeletionEvidence(string pairId, string rootId, IReadOnlyList<DeletionEvidenceRecord> results)
    {
        lock (_gate)
        {
            var key = pairId + "\n" + rootId;
            if (!_deletionEvidence.TryGetValue(key, out var map))
                _deletionEvidence[key] = map = new Dictionary<string, DeletionEvidenceRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in results) map[record.Path] = record;
        }
    }

    public void Prune()
    {
        lock (_gate)
        {
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
        }
    }

    private Dictionary<string, Row> LiveRows(string rootId)
    {
        if (!_live.TryGetValue(rootId, out var live))
        {
            // Exact spellings: a case clash recorded by a full scan is two rows (RQ-039); lookups ignore case.
            live = new Dictionary<string, Row>(StringComparer.Ordinal);
            _live[rootId] = live;
        }
        return live;
    }

    private Dictionary<string, Baseline> BaselinesFor(string pairId)
    {
        if (!_baselines.TryGetValue(pairId, out var map))
        {
            map = new Dictionary<string, Baseline>(StringComparer.Ordinal);
            _baselines[pairId] = map;
        }
        return map;
    }

    private Dictionary<string, IntentRecord> IntentsFor(string pairId)
    {
        if (!_intents.TryGetValue(pairId, out var map))
        {
            map = new Dictionary<string, IntentRecord>(StringComparer.Ordinal);
            _intents[pairId] = map;
        }
        return map;
    }

    private Dictionary<string, RecoveryRecord> RecoveriesFor(string pairId)
    {
        if (!_recoveries.TryGetValue(pairId, out var map))
        {
            map = new Dictionary<string, RecoveryRecord>(StringComparer.Ordinal);
            _recoveries[pairId] = map;
        }
        return map;
    }

    // Like Storage: every Delete acts on its exact spelling only (B-126). Naming its object, it removes the row at
    // its exact path while that row carries the object, plus a folder's exact subtree; without an object id, the
    // exact spelling and its exact subtree.
    private static List<string> DeleteTargets(Dictionary<string, Row> live, Change change)
    {
        if (change.ObjectId.Length == 0) return live.Keys.Where(path => IsExactUnder(path, change.Path)).ToList();
        if (!live.TryGetValue(change.Path, out var row) ||
            !string.Equals(row.State.ObjectId, change.ObjectId, StringComparison.Ordinal))
            return [];
        return row.State.Kind == ItemKind.Folder
            ? live.Keys.Where(path => IsExactUnder(path, change.Path)).ToList()
            : [change.Path];
    }

    private static bool IsExactUnder(string path, string prefix) =>
        string.Equals(path, prefix, StringComparison.Ordinal) || path.StartsWith(prefix + "/", StringComparison.Ordinal);

    // Like Storage: the object's old row goes (a move or case-only rename); an exact-path row is updated in place;
    // without one, a single other-spelling row of another object is displaced, while a known clash is kept.
    private void Upsert(string rootId, ItemState item, long gen, Func<string, bool>? include)
    {
        var live = LiveRows(rootId);
        var atKey = live.Keys.Where(path => PairPath.Same(path, item.Path)).ToList();
        foreach (var other in live
                     .Where(pair => string.Equals(pair.Value.State.ObjectId, item.ObjectId, StringComparison.Ordinal) &&
                         !string.Equals(pair.Key, item.Path, StringComparison.Ordinal) &&
                         (include?.Invoke(pair.Key) ?? true))
                     .Select(pair => pair.Key).ToList())
            Close(rootId, other, gen);
        if (!live.ContainsKey(item.Path) && atKey.Count == 1 && live.TryGetValue(atKey[0], out var single) &&
            !string.Equals(single.State.ObjectId, item.ObjectId, StringComparison.Ordinal))
            Close(rootId, atKey[0], gen);
        Put(rootId, item, gen);
    }

    private void Put(string rootId, ItemState item, long gen)
    {
        var live = LiveRows(rootId);
        var debt = _rehash.ContainsKey((rootId, item.ObjectId));
        if (debt && live.TryGetValue(item.Path, out var prior) && prior.State.ObjectId == item.ObjectId &&
            (prior.State.Size != item.Size || prior.State.Mtime != item.Mtime))
            foreach (var alias in live.Where(p => p.Value.State.ObjectId == item.ObjectId).ToArray())
                live[alias.Key] = alias.Value with { State = alias.Value.State with { Hash = null, Flags = alias.Value.State.Flags & ~ItemFlags.NeedsRehash } };
        // Alias evidence is relevant only to rehash debt. Avoid a whole-store scan on every
        // ordinary snapshot insertion (the million-item prepare fixture otherwise becomes quadratic).
        var aliases = debt ? live.Values.Where(r => r.State.ObjectId == item.ObjectId).ToArray() : Array.Empty<Row>();
        var needs = debt && (aliases.Length == 0 || aliases.Any(r => (r.State.Flags & ItemFlags.NeedsRehash) != 0));
        if (needs) item = item with { Hash = null, Flags = item.Flags | ItemFlags.NeedsRehash };
        else if ((item.Flags & ItemFlags.NeedsRehash) != 0) item = item with { Hash = null, Flags = item.Flags & ~ItemFlags.NeedsRehash };
        if (live.TryGetValue(item.Path, out var existing))
        {
            item = RowCodec.CarryHash(item, existing.State);
            if (existing.State != item) _closed.Add((rootId, existing with { ToGen = gen }));
        }
        live[item.Path] = new Row(item, LiveToGen, gen);
        ObjectPathsFor(rootId)[item.ObjectId] = item.Path;
    }

    private void Close(string rootId, string path, long gen)
    {
        var live = LiveRows(rootId);
        if (!live.TryGetValue(path, out var row)) return;
        live.Remove(path);
        _closed.Add((rootId, row with { ToGen = gen }));
        if (_objectPaths.TryGetValue(rootId, out var map) && map.TryGetValue(row.State.ObjectId, out var mapped) &&
            string.Equals(mapped, path, StringComparison.Ordinal))
            map.Remove(row.State.ObjectId);
    }

    private Dictionary<string, string> ObjectPathsFor(string rootId)
    {
        if (!_objectPaths.TryGetValue(rootId, out var map))
        {
            map = new Dictionary<string, string>(StringComparer.Ordinal);
            _objectPaths[rootId] = map;
        }
        return map;
    }

    // path matching ignores case; the empty prefix means the whole root.
    private static bool IsSelfOrUnder(string path, string prefix) =>
        prefix.Length == 0 || PairPath.IsSelfOrUnder(path, prefix);

    private static bool IsInsideIncomplete(string path, IReadOnlyList<string> incomplete) =>
        incomplete.Any(prefix => IsSelfOrUnder(path, prefix));
}

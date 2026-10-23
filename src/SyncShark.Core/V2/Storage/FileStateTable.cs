using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>Writes the live rows of file_state and the local-deletion tombstones.</summary>
internal sealed class FileStateTable
{
    private readonly SqliteConnection _connection;
    private readonly DirTable _dir;
    private readonly FileStateReader _reader;
    private readonly V2Settings _settings;
    private readonly TimeProvider _clock;
    private readonly Func<long, string, SqliteTransaction, bool> _baselined;

    /// <summary>
    /// Decision 59: called inside the same transaction as each written link row, so the baselines under a link are
    /// forgotten atomically with its discovery (no window in which the link is stored but its baselines survive).
    /// </summary>
    internal Action<SqliteTransaction, long, string>? OnLinkWritten { get; set; }
    internal Action? AfterSnapshotCompared { get; set; }

    /// <param name="baselined">Whether an object of a root is a side of some baseline .</param>
    public FileStateTable(SqliteConnection connection, DirTable dir, FileStateReader reader, V2Settings settings,
        TimeProvider clock, Func<long, string, SqliteTransaction, bool> baselined)
    {
        _connection = connection;
        _dir = dir;
        _reader = reader;
        _settings = settings;
        _clock = clock;
        _baselined = baselined;
    }

    /// <summary>Replaces the live set with the snapshot's items, closing anything absent outside incomplete subtrees.</summary>
    public void WriteSnapshot(Snapshot snapshot, long rootNo, IEnumerable<ItemState>? input = null)
    {
        SnapshotTemporary.Sweep(_connection);
        using var verifier = snapshot.IncompleteSubtrees.Count == 0 ? null : snapshot.OpenAbsenceVerifier?.Invoke();
        if (verifier is not null) snapshot = snapshot with { VerifyAbsence = verifier.Check };
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { if (TryWriteSnapshot(snapshot, rootNo, input ?? snapshot.Items, locked: attempt == 2)) return; }
            finally { _dir.ClearCache(); }
        }
    }

    private bool TryWriteSnapshot(Snapshot snapshot, long rootNo, IEnumerable<ItemState> input, bool locked)
    {
        var profile = Environment.GetEnvironmentVariable("SYNCSHARK_SNAPSHOT_PROFILE") == "1";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        void phase(string name)
        {
            if (profile) System.Diagnostics.Trace.WriteLine($"snapshot-phase {name} ms={watch.ElapsedMilliseconds}");
            watch.Restart();
        }
        snapshot.PendingDeletionEvidence.Clear();
        var version = StoreSchema.Scalar(_connection, null, "PRAGMA data_version;");
        // A partially enumerated subtree can contain observations from before the failure. Do not
        // let even a replacement close a row there, or let an ignored observation suppress a move.
        var include = snapshot.IncludesPath;
        // Normally compare under a deferred read. After two concurrent changes, keep BEGIN IMMEDIATE
        // through comparison and publication so a busy external writer cannot starve this scan.
        using var lockedBatch = locked ? new StoreBatch(_connection, int.MaxValue) : null;
        using var readingScope = locked ? null : _connection.BeginTransaction(deferred: true);
        var reading = lockedBatch?.Transaction ?? readingScope!;
        // Already ordered, unchanged scanner arrays need neither a sort nor scratch writes. An
        // out-of-order or changed row falls back to the general disk-backed merge, without mutation.
        if (snapshot.IncompleteSubtrees.Count == 0 && input is ItemState[] or List<ItemState> &&
            UnchangedOrdered(input, include, rootNo, reading) && !HasOrphanDebt(rootNo, reading))
        {
            readingScope?.Commit();
            phase("unchanged-ordered");
            if (locked) { lockedBatch!.Flush(); return true; }
            AfterSnapshotCompared?.Invoke();
            return Equals(version, StoreSchema.Scalar(_connection, null, "PRAGMA data_version;"));
        }
        var hasQuarantine = snapshot.IncompleteSubtrees.Count > 0 && StoreSchema.Scalar(_connection, reading,
            "SELECT 1 FROM file_state INDEXED BY fs_quarantine WHERE root_no=$r AND (flags & 128)!=0 LIMIT 1;",
            ("$r", rootNo)) is not null;
        bool Observed(ItemState item) => !InsideIncomplete(item.Path, snapshot.IncompleteSubtrees) ||
            (hasQuarantine && StoreSchema.Scalar(_connection, reading,
                "SELECT 1 FROM file_state INDEXED BY fs_obj WHERE root_no=$r AND object_id=$o AND (flags & 128)!=0 LIMIT 1;",
                ("$r", rootNo), ("$o", item.ObjectId)) is not null);
        // Positively observed held identities keep their new aliases even inside an incomplete
        // scope. This never proves absence or authorizes replacement of another identity there.
        using var incoming = SnapshotInput.Create(_connection, _dir,
            snapshot.IncompleteSubtrees.Count == 0 ? input : input.Where(Observed), include, reading);
        if (snapshot.IncompleteSubtrees.Count > 0 || StoreSchema.Scalar(_connection, reading,
            "SELECT 1 FROM file_state INDEXED BY fs_quarantine WHERE root_no=$r AND (flags & 128)!=0 LIMIT 1;",
            ("$r", rootNo)) is not null) incoming.PrepareObjectLookup();
        phase("stage");
        var now = _clock.GetUtcNow();
        Func<string, bool> suppressed = incoming.ContainsObject;
        using var changes = new SnapshotChanges(_connection);
        using var heldMoves = new SnapshotChanges(_connection);
        using var left = incoming.Entries().GetEnumerator();
        using var right = _reader.SnapshotEntries(rootNo, () => reading).GetEnumerator();
        var hasLeft = left.MoveNext();
        var hasRight = right.MoveNext();
        var initialLoad = !hasRight && hasLeft;
        if (initialLoad) hasLeft = false;
        while (hasLeft || hasRight)
        {
            var order = !hasLeft ? 1 : !hasRight ? -1 : SnapshotInput.Compare(left.Current, right.Current);
            if (order == 0)
            {
                var merged = RowCodec.CarryHash(left.Current.Item, right.Current.Item);
                var movingHold = right.Current.Item.Flags.HasFlag(ItemFlags.Quarantined) &&
                    right.Current.Item.ObjectId != merged.ObjectId && suppressed(right.Current.Item.ObjectId);
                var protectedReplacement = InsideIncomplete(merged.Path, snapshot.IncompleteSubtrees) &&
                    merged.ObjectId != right.Current.Item.ObjectId;
                if (movingHold && !protectedReplacement) heldMoves.Add(right.Current, left.Current.Item, wasLive: true);
                if (!movingHold && !protectedReplacement && !RowCodec.Same(merged, right.Current.Item))
                    changes.Add(right.Current, merged, wasLive: true);
                hasLeft = left.MoveNext();
                hasRight = right.MoveNext();
            }
            else if (order < 0)
            {
                changes.Add(left.Current, left.Current.Item, wasLive: false);
                hasLeft = left.MoveNext();
            }
            else
            {
                var row = right.Current;
                if (row.NameKey != RowCodec.Key(row.Name) && incoming.At(row) is { } samePath)
                    changes.Add(row, RowCodec.CarryHash(samePath, row.Item), wasLive: true);
                else if (PairPath.Observed(row.Item.Path, snapshot.IncompleteSubtrees, snapshot.IncludesPath) &&
                    (row.Item.Flags.HasFlag(ItemFlags.Quarantined) || snapshot.IncompleteSubtrees.Count == 0 ||
                        suppressed(row.Item.ObjectId) || snapshot.CanCloseAbsent(row.Item)))
                {
                    if (row.Item.Flags.HasFlag(ItemFlags.Quarantined))
                    {
                        if (suppressed(row.Item.ObjectId)) heldMoves.Add(row, null, wasLive: true);
                    }
                    else changes.Add(row, null, wasLive: true);
                }
                hasRight = right.MoveNext();
            }
        }
        readingScope?.Commit();
        phase("compare");
        if (initialLoad)
        {
            left.Dispose();
            incoming.PreparePathOrder();
        }
        if (changes.HasChanges) incoming.PrepareObjectLookup();
        phase("replay-index");
        if (!locked) AfterSnapshotCompared?.Invoke();
        // Preserve cross-row effects of the old writer without retaining the old live root in memory.
        // End the read snapshot before opening the write transaction (no deferred upgrade/BUSY_SNAPSHOT).
        const string orphan = "FROM local_rehash WHERE root_no=$r AND NOT EXISTS " +
            "(SELECT 1 FROM file_state f WHERE f.root_no=local_rehash.root_no AND f.object_id=local_rehash.object_id)";
        bool hasOrphans(SqliteTransaction? tx) => snapshot.IncompleteSubtrees.Count == 0 &&
            (long)StoreSchema.Scalar(_connection, tx, "SELECT EXISTS(SELECT 1 " + orphan + ");", ("$r", rootNo))! != 0;
        if (initialLoad || changes.HasChanges || heldMoves.HasChanges || hasOrphans(lockedBatch?.Transaction))
        {
            // A snapshot is one publication: callback or disk failures must roll back every row.
            using var batch = lockedBatch ?? new StoreBatch(_connection, int.MaxValue);
            if (!locked && !Equals(version, StoreSchema.Scalar(_connection, batch.Transaction, "PRAGMA data_version;")))
                return false;
            // Under this write lock an insert-only publication cannot create deletion evidence or
            // rehash debt. Probe empty tables once rather than issuing two million empty statements.
            var initialHasDebt = !initialLoad || StoreSchema.Scalar(_connection, batch.Transaction,
                "SELECT 1 FROM local_rehash WHERE root_no=$r LIMIT 1;", ("$r", rootNo)) is not null;
            var initialHasTombstones = !initialLoad || StoreSchema.Scalar(_connection, batch.Transaction,
                "SELECT 1 FROM tombstone WHERE root_no=$r LIMIT 1;", ("$r", rootNo)) is not null;
            var replay = initialLoad
                ? incoming.PathEntries().Select(entry => new SnapshotChanges.Change(entry, entry.Item, false))
                : changes.Read();
            foreach (var change in replay)
            {
                if (!initialLoad) snapshot.ChangedObject?.Invoke(change.Entry.Item.ObjectId);
                if (!initialLoad && change.Incoming is { } changed && changed.ObjectId != change.Entry.Item.ObjectId)
                    snapshot.ChangedObject?.Invoke(changed.ObjectId);
                if (change.Incoming is null)
                {
                    DeleteRow(rootNo, change.Entry.DirNo, change.Entry.Name, batch, change.Entry.NameKey);
                    if (!suppressed(change.Entry.Item.ObjectId))
                        InsertTombstone(rootNo, change.Entry.DirNo, change.Entry.Name, change.Entry.Item.ObjectId, now, batch);
                }
                else if (change.WasLive)
                    Step(batch, Replace(change.Entry, change.Incoming, rootNo, suppressed, now, batch, snapshot.CanCloseAbsent));
                else
                    Step(batch, Insert(change.Entry, rootNo, batch,
                        debtReconciled: !initialHasDebt && (change.Entry.Item.Flags & ItemFlags.NeedsRehash) == 0,
                        clearTombstone: initialHasTombstones));
            }
            // Retire moved held aliases only after their new held identity exists. The deferred
            // replay is disk-backed, preserving quarantine semantics without a root-sized list.
            foreach (var change in heldMoves.Read())
            {
                var old = change.Entry;
                if (StoreSchema.Scalar(_connection, batch.Transaction, HeldMoveProbeSql,
                    ("$r", rootNo), ("$o", old.Item.ObjectId),
                    ("$d", old.DirNo), ("$n", old.Name)) is null ||
                    _reader.ReadObjectId(rootNo, old.DirNo, old.Name, batch.Transaction) != old.Item.ObjectId) continue;
                if (change.Incoming is null)
                    Step(batch, RetireHeldAlias(rootNo, old.DirNo, old.Name, old.Item.ObjectId, batch));
                else
                    Step(batch, Replace(old, RowCodec.CarryHash(change.Incoming,
                        old.Item with { Flags = old.Item.Flags & ~ItemFlags.Quarantined }), rootNo, suppressed, now, batch));
            }
            if (hasOrphans(batch.Transaction))
                StoreSchema.Execute(_connection, batch.Transaction, "DELETE " + orphan + ";", ("$r", rootNo));
            batch.Flush();
        }
        else if (locked) lockedBatch!.Flush();
        else if (!Equals(version, StoreSchema.Scalar(_connection, null, "PRAGMA data_version;"))) return false;
        if (initialLoad) snapshot.AllItemsChanged?.Invoke();
        phase("publish");
        return true;
    }

    private bool UnchangedOrdered(IEnumerable<ItemState> items, Func<string, bool>? include,
        long rootNo, SqliteTransaction reading)
    {
        using var live = _reader.SnapshotEntries(rootNo, () => reading)
            .Where(row => include?.Invoke(row.Item.Path) ?? true).GetEnumerator();
        foreach (var item in items)
        {
            if (!(include?.Invoke(item.Path) ?? true)) continue;
            if (!live.MoveNext()) return false;
            // Matching each exact pathname against the PK-ordered live stream proves the same set
            // without assuming any particular scanner sort order or constructing a second collection.
            var row = live.Current;
            if (item.Path != row.Item.Path || row.NameKey != RowCodec.Key(row.Name) ||
                !RowCodec.Same(RowCodec.CarryHash(item, row.Item), row.Item)) return false;
        }
        return !live.MoveNext();
    }

    private bool HasOrphanDebt(long rootNo, SqliteTransaction reading) =>
        StoreSchema.Scalar(_connection, reading,
            "SELECT 1 FROM local_rehash WHERE root_no=$r AND NOT EXISTS " +
            "(SELECT 1 FROM file_state f WHERE f.root_no=local_rehash.root_no AND f.object_id=local_rehash.object_id) LIMIT 1;",
            ("$r", rootNo)) is not null;

    /// <summary>Applies a delta's deletes then upserts, each in list order, tombstoning every real deletion.</summary>
    public void WriteDelta(Delta delta, long rootNo)
    {
        var evidence = delta.DisplacementEvidence;
        using var verifier = evidence is { IncompleteSubtrees.Count: > 0 } ? evidence.OpenAbsenceVerifier?.Invoke() : null;
        if (verifier is not null) evidence = evidence! with { VerifyAbsence = verifier.Check };
        Func<ItemState, bool>? confirmDisplacement = evidence is null ? null : evidence.CanCloseAbsent;
        var suppressed = delta.Changes.Where(change => change.Kind == ChangeKind.Upsert &&
                (delta.IncludesPath?.Invoke(change.Path) ?? true))
            .Select(change => change.ObjectId).ToHashSet(StringComparer.Ordinal);
        using var batch = new StoreBatch(_connection, _settings.StoreBatchRows);
        var retainedAliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in delta.Changes)
            if (change.Kind == ChangeKind.Delete) Delete(change, rootNo, suppressed, batch, delta.IncludesPath, delta.ChangedObject, retainedAliases);
        var now = _clock.GetUtcNow();
        foreach (var change in delta.Changes)
            if (change.Kind == ChangeKind.Upsert && (delta.IncludesPath?.Invoke(change.Path) ?? true))
                Step(batch, Upsert(change, rootNo, suppressed, now, batch, delta.IncludesPath, confirmDisplacement, retainedAliases));
        batch.Flush();
    }

    internal void HoldQuarantine(SqliteTransaction transaction, long rootNo, ItemState item)
    {
        var (parent, name) = RowCodec.Split(item.Path);
        var dir = _dir.Intern(parent, transaction);
        // Retain the observed row when antivirus removal raced inventory. A replacement already at this path
        // retains its own identity/content and is conservatively held, never overwritten by stale metadata.
        StoreSchema.Execute(_connection, transaction,
            "INSERT INTO file_state(root_no,dir_no,name_key,name,kind,object_id,version_id,hash,size,mtime,flags,identity_json) " +
            "VALUES($r,$d,$nk,$n,$k,$o,$v,$h,$s,$m,$f,$i) " +
            "ON CONFLICT(root_no,dir_no,name_key,name) DO UPDATE SET flags=file_state.flags | $hold;",
            ("$r",rootNo), ("$d",dir), ("$nk",RowCodec.Key(name)), ("$n",name), ("$k",RowCodec.KindCode(item.Kind)),
            ("$o",item.ObjectId), ("$v",item.VersionId), ("$h",RowCodec.HashBlob(item.Hash)), ("$s",item.Size),
            ("$m",RowCodec.MtimeTicks(item.Mtime)), ("$f",(long)(item.Flags | ItemFlags.Quarantined)),
            ("$hold",(long)ItemFlags.Quarantined), ("$i",item.IdentityJson));
        StoreSchema.Execute(_connection, transaction, "DELETE FROM tombstone WHERE root_no=$r AND object_id=$o;",
            ("$r",rootNo), ("$o",item.ObjectId));
    }

    // The refusal belongs to the existing item row, and commits atomically with retiring its intent.
    internal bool MarkProviderNameRefused(SqliteTransaction transaction, long rootNo, ItemState source,
        ItemFlags flag = ItemFlags.ProviderNameRefused)
    {
        var (parent, name) = RowCodec.Split(source.Path);
        var dirNo = _dir.Find(parent, transaction);
        if (dirNo is null) return false;
        using var command = StoreSchema.Command(_connection, transaction,
            "UPDATE file_state SET flags=flags | $f WHERE root_no=$r AND dir_no=$d AND name_key=$k AND name=$n AND object_id=$o;");
        command.Parameters.AddWithValue("$f", (long)flag);
        command.Parameters.AddWithValue("$r", rootNo);
        command.Parameters.AddWithValue("$d", dirNo.Value);
        command.Parameters.AddWithValue("$k", RowCodec.Key(name));
        command.Parameters.AddWithValue("$n", name);
        command.Parameters.AddWithValue("$o", source.ObjectId);
        return command.ExecuteNonQuery() == 1;
    }

    // Inventory still names the pre-write entity until refresh. Clear only the accepted destination,
    // bound to either that expected entity or the returned entity; never clear a moved-away source.
    internal void ClearProviderNameRefused(SqliteTransaction transaction, long rootNo,
        ItemState written, ItemState? expected)
    {
        var (parent, name) = RowCodec.Split(written.Path);
        var dirNo = _dir.Find(parent, transaction);
        if (dirNo is null) return;
        var oldId = expected is not null && string.Equals(expected.Path, written.Path, StringComparison.Ordinal)
            ? expected.ObjectId : written.ObjectId;
        StoreSchema.Execute(_connection, transaction,
            "UPDATE file_state SET flags=flags & ~$f WHERE root_no=$r AND dir_no=$d AND name_key=$k " +
            "AND name=$n AND object_id IN ($o,$old) AND (flags & $f) != 0;",
            ("$f", (long)ItemFlags.ProviderNameRefused), ("$r", rootNo), ("$d", dirNo.Value),
            ("$k", RowCodec.Key(name)), ("$n", name), ("$o", written.ObjectId), ("$old", oldId));
    }

    // A successful folder effect also carries ignored descendants, which have no individual actions.
    // Retire the exact identity-bound subtree in the result transaction, not by a full-root scan.
    internal void RetireDeletedFolder(SqliteTransaction transaction, long rootNo, ItemState expected)
    {
        var deletedAt = _clock.GetUtcNow();
        var change = new Change(ChangeKind.Delete, expected.ObjectId, expected.Path, null, deletedAt);
        foreach (var row in ExactRows(rootNo, change, transaction))
        {
            StoreSchema.Execute(_connection, transaction,
                "DELETE FROM file_state WHERE root_no=$r AND dir_no=$d AND name_key=$k AND name=$n;",
                ("$r", rootNo), ("$d", row.DirNo), ("$k", RowCodec.Key(row.Name)), ("$n", row.Name));
            InsertTombstone(rootNo, row.DirNo, row.Name, row.ObjectId, deletedAt, transaction);
        }
    }

    // A case clash a full scan recorded (Docs/a.txt and Docs/A.txt both live) is kept until evidence for each
    // removes it (RQ-039). Every Delete acts on its exact spelling only, so applying it twice equals applying it
    // once (a replay after a crash never reaches another spelling, B-126): a Delete naming its object removes the
    // row at its exact path only while that row still carries the object (a reused id elsewhere, or a new object
    // at the path, is another incarnation and is untouched), plus a folder's exactly spelled subtree; a Delete
    // without an object id removes the exact spelling and its exact subtree. Choosing among other spellings is
    // the inventory's job, on evidence for that object. Move suppression applies after selection: a row whose
    // object is upserted later in the delta is a move written as Delete(old) + Upsert(new) and is left for that
    // Upsert, which removes it in the same step as the insertion. One step per removed row.
    private void Delete(Change change, long rootNo, HashSet<string> suppressed, StoreBatch batch,
        Func<string, bool>? include, Action<string>? changedObject, HashSet<string> retainedAliases)
    {
        var deletedAt = change.DeletedUtc ?? _clock.GetUtcNow();
        foreach (var row in ExactRows(rootNo, change, batch.Transaction))
        {
            var path = RowCodec.Join(row.Path, row.Name);
            // Retire only the explicitly observed missing spelling. Another live local
            // alias suppresses the entity tombstone, not this path's inventory removal.
            var alias = row.ObjectId.StartsWith("id:local:", StringComparison.Ordinal) &&
                (retainedAliases.Contains(row.ObjectId) || StoreSchema.Scalar(_connection, batch.Transaction,
                    "SELECT 1 FROM file_state INDEXED BY fs_obj WHERE root_no=$r AND object_id=$o LIMIT 1 OFFSET 1;",
                    ("$r", rootNo), ("$o", row.ObjectId)) is not null) &&
                _reader.AtKey(rootNo, path, batch.Transaction).Any(entry => entry.Item.Kind == ItemKind.File &&
                    entry.Item.ObjectId == row.ObjectId && string.Equals(entry.Item.Path, path, StringComparison.Ordinal));
            if ((!suppressed.Contains(row.ObjectId) || alias && string.Equals(path, change.Path, StringComparison.Ordinal)) &&
                (include?.Invoke(path) ?? true))
            {
                if (alias && suppressed.Contains(row.ObjectId)) retainedAliases.Add(row.ObjectId);
                var removed = RemoveAt(rootNo, row.DirNo, row.Name, deletedAt,
                    id => suppressed.Contains(id) || alias, batch);
                if (removed) changedObject?.Invoke(row.ObjectId);
                Step(batch, removed);
            }
        }
    }

    private List<RowRef> ExactRows(long rootNo, Change change, SqliteTransaction transaction)
    {
        var at = _reader.AtKey(rootNo, change.Path, transaction).Where(entry =>
            string.Equals(entry.Item.Path, change.Path, StringComparison.Ordinal) &&
            (change.ObjectId.Length == 0 ||
                string.Equals(entry.Item.ObjectId, change.ObjectId, StringComparison.Ordinal))).ToList();
        // A Delete naming its object reaches under its path only when that object is the folder there. Descendants
        // come first and the folder row last (B-127): batches commit between rows, and a crash after a boundary
        // must leave the folder live so the replayed Delete still finds it and removes the rest.
        var rows = change.ObjectId.Length == 0 || at.Any(entry => entry.Item.Kind == ItemKind.Folder)
            ? Subtree(rootNo, change.Path, transaction).Where(row => IsExactUnder(row.Path, row.Name, change.Path)).ToList()
            : new List<RowRef>();
        rows.AddRange(at.Select(entry => new RowRef(entry.DirNo, entry.Parent, entry.Name, entry.Item.ObjectId)));
        return rows;
    }

    // Every live row in a folder at the path or under it, ignoring case (callers keep the exact spelling).
    private IEnumerable<RowRef> Subtree(long rootNo, string path, SqliteTransaction transaction) =>
        _dir.SelfOrUnder(path, transaction).SelectMany(dir => _reader.ReadObjectIds(rootNo, dir.DirNo, transaction)
            .Select(row => new RowRef(dir.DirNo, dir.Path, row.Name, row.ObjectId)));

    private readonly record struct RowRef(long DirNo, string Path, string Name, string ObjectId);

    // Whether a row (its folder path and name) is the path itself or under it, spelled exactly.
    private static bool IsExactUnder(string dirPath, string name, string path)
    {
        var rowPath = RowCodec.Join(dirPath, name);
        return string.Equals(rowPath, path, StringComparison.Ordinal) ||
            rowPath.StartsWith(path + "/", StringComparison.Ordinal);
    }

    // One step (one transaction) per upsert. The object's old row (a move, or a case-only rename) goes. An existing
    // row at the exact path is updated or replaced in place; other spellings are never touched then. Without one, a
    // single other-spelling row of another object is a replacement that changed case (Docs/file.txt by
    // docs/File.txt) and is displaced with its evidence; a known clash (two or more spellings) is kept (RQ-039).
    // Exact observation admission reuses upsert without inferring moves, replacement or absence.
    internal bool AdmitIndexObservation(long rootNo, ItemState item, StoreBatch batch) =>
        Upsert(new(ChangeKind.Upsert, item.ObjectId, item.Path, item, null), rootNo, [], _clock.GetUtcNow(), batch,
            _ => false, _ => false, new HashSet<string>(StringComparer.Ordinal) { item.ObjectId });

    private bool Upsert(Change change, long rootNo, HashSet<string> suppressed, DateTimeOffset now, StoreBatch batch,
        Func<string, bool>? include, Func<ItemState, bool>? confirmDisplacement, IReadOnlySet<string> retainedAliases)
    {
        var state = change.State
            ?? throw new ArgumentException("An upsert change must carry its state.", nameof(change));
        if (!string.Equals(state.Path, change.Path, StringComparison.Ordinal))
            throw new ArgumentException("An upsert change's state must be at its path.", nameof(change));
        state = ReconcileDebt(state, rootNo, batch.Transaction);
        var (parent, name) = RowCodec.Split(state.Path);
        var target = _dir.Find(parent, batch.Transaction);
        var atKey = _reader.AtKey(rootNo, state.Path, batch.Transaction);
        var exactAt = atKey.FindIndex(at => IsExact(at.DirNo, at.Name, target, name));
        FileStateReader.Entry? live = exactAt < 0 ? null : atKey[exactAt];
        var displaced = live ?? (atKey.Count == 1 ? atKey[0] : (FileStateReader.Entry?)null);
        if (displaced is { } old && old.Item.ObjectId != state.ObjectId &&
            !suppressed.Contains(old.Item.ObjectId) && !(confirmDisplacement?.Invoke(old.Item) ?? true))
            return false; // Retain the old row and retry the replacement through pending evidence.
        var wrote = false;
        var objectRows = _reader.ReadObject(rootNo, state.ObjectId, batch.Transaction);
        // Multiple local file names may be hard-link aliases, not successive positions of a move.
        // Provider objects and folders still use the existing move cleanup semantics.
        var aliases = (objectRows.Count > 1 || retainedAliases.Contains(state.ObjectId)) && state.Kind == ItemKind.File &&
            state.ObjectId.StartsWith("id:local:", StringComparison.Ordinal);
        foreach (var moved in objectRows)
            if (!aliases && !IsExact(moved.DirNo, moved.Name, target, name) && (include?.Invoke(moved.Path) ?? true))
            {
                DeleteRow(rootNo, moved.DirNo, moved.Name, batch);
                wrote = true;
            }
        if (live is null && atKey.Count == 1 && atKey[0].Item.ObjectId != state.ObjectId)
            wrote |= RemoveAt(rootNo, atKey[0].DirNo, atKey[0].Name, now, suppressed, batch);
        if (live is null) return Insert(FileStateReader.Entry.Of(parent, name, state), rootNo, batch, debtReconciled: true);
        var merged = RowCodec.CarryHash(state, live.Value.Item);
        if (RowCodec.Same(merged, live.Value.Item)) return wrote;
        return Replace(live.Value, merged, rootNo, suppressed, now, batch);
    }

    private static bool IsExact(long dirNo, string rowName, long? target, string name) =>
        target == dirNo && string.Equals(rowName, name, StringComparison.Ordinal);

    private static void Step(StoreBatch batch, bool wrote)
    {
        if (wrote) batch.Step();
    }

    private bool RemoveAt(long rootNo, long dirNo, string name, DateTimeOffset deletedAt, HashSet<string> suppressed,
        StoreBatch batch) => RemoveAt(rootNo, dirNo, name, deletedAt, suppressed.Contains, batch);

    private bool RemoveAt(long rootNo, long dirNo, string name, DateTimeOffset deletedAt, Func<string, bool> suppressed,
        StoreBatch batch)
    {
        if (StoreSchema.Scalar(_connection, batch.Transaction,
            "SELECT object_id FROM file_state WHERE root_no=$r AND dir_no=$d AND name_key=$k AND name=$n AND (flags & $f)=0;",
            ("$r", rootNo), ("$d", dirNo), ("$k", RowCodec.Key(name)), ("$n", name),
            ("$f", (long)ItemFlags.Quarantined)) is not string objectId) return false;
        DeleteRow(rootNo, dirNo, name, batch);
        if (!suppressed(objectId)) InsertTombstone(rootNo, dirNo, name, objectId, deletedAt, batch);
        return true;
    }

    // Only called after a held alias of this identity is durably present at its observed name.
    private bool RetireHeldAlias(long rootNo, long dirNo, string name, string objectId, StoreBatch batch)
    {
        StoreSchema.Execute(_connection, batch.Transaction,
            "DELETE FROM file_state WHERE root_no=$r AND dir_no=$d AND name=$n AND object_id=$o AND (flags & $f)!=0;",
            ("$r", rootNo), ("$d", dirNo), ("$n", name), ("$o", objectId), ("$f", (long)ItemFlags.Quarantined));
        return StoreSchema.Changes(_connection, batch.Transaction) != 0;
    }

    internal void RemoveHydrationRows(long rootNo, IEnumerable<(long Dir, string Name)> rows)
    {
        using var batch = new StoreBatch(_connection, _settings.StoreBatchRows);
        foreach (var (dirNo, name) in rows)
        {
            var objectId = _reader.ReadObjectId(rootNo, dirNo, name, batch.Transaction);
            if (objectId is not null && (long)StoreSchema.Scalar(_connection, batch.Transaction,
                "SELECT EXISTS(SELECT 1 FROM hydration_seen WHERE root_no=$r AND object_id=$o AND json IS NOT NULL);",
                ("$r", rootNo), ("$o", objectId))! == 0)
                Step(batch, RemoveAt(rootNo, dirNo, name, _clock.GetUtcNow(), [], batch));
        }
        batch.Flush();
    }

    // A different object displacing the live one is a deletion of the displaced object: keep its evidence.
    private bool Replace(FileStateReader.Entry live, ItemState item, long rootNo, HashSet<string> suppressed,
        DateTimeOffset deletedAt, StoreBatch batch) => Replace(live, item, rootNo, suppressed.Contains, deletedAt, batch);

    private bool Replace(FileStateReader.Entry live, ItemState item, long rootNo, Func<string, bool> suppressed,
        DateTimeOffset deletedAt, StoreBatch batch, Func<ItemState, bool>? confirmDisplacement = null)
    {
        if (live.Item.ObjectId != item.ObjectId && !suppressed(live.Item.ObjectId) &&
            !(confirmDisplacement?.Invoke(live.Item) ?? true)) return false;
        item = ReconcileDebt(item, rootNo, batch.Transaction);
        DeleteRow(rootNo, live.DirNo, live.Name, batch, live.NameKey);
        var displaced = live.Item.ObjectId;
        if (!string.Equals(displaced, item.ObjectId, StringComparison.Ordinal) && !suppressed(displaced))
            InsertTombstone(rootNo, live.DirNo, live.Name, displaced, deletedAt, batch);
        return Insert(FileStateReader.Entry.Of(live.Parent, live.Name, item), rootNo, batch, debtReconciled: true);
    }

    // Without the explicit object index SQLite can choose the root-only primary-key range,
    // turning N changed rows in a million-item root into N million-row scans.
    internal const string ReconcileProtectionSql =
        "SELECT EXISTS(SELECT 1 FROM local_rehash WHERE root_no=$r AND object_id=$o) | " +
        "(128 * EXISTS(SELECT 1 FROM file_state INDEXED BY fs_obj WHERE root_no=$r AND object_id=$o AND (flags & 128)!=0));";

    // The same root/object binding for the held-move probe and the rehash-debt write and probe.
    internal const string HeldMoveProbeSql =
        "SELECT 1 FROM file_state INDEXED BY fs_obj WHERE root_no=$r AND object_id=$o AND (flags & 128)!=0 " +
        "AND NOT(dir_no=$d AND name=$n) LIMIT 1;";
    internal const string ClearRehashDebtSql =
        "UPDATE file_state INDEXED BY fs_obj SET flags=flags & ~256,hash=NULL WHERE root_no=$r AND object_id=$o;";
    internal const string RehashDebtProbeSql =
        "SELECT 1 FROM file_state INDEXED BY fs_obj WHERE root_no=$r AND object_id=$o AND (flags & 256)!=0 LIMIT 1;";

    private ItemState ReconcileDebt(ItemState item, long rootNo, SqliteTransaction tx)
    {
        // Both protections belong to the root/object, not its old name. Read before move cleanup.
        var protection = Convert.ToInt64(StoreSchema.Scalar(_connection, tx, ReconcileProtectionSql,
            ("$r", rootNo), ("$o", item.ObjectId)));
        if ((protection & 128) != 0) item = item with { Flags = item.Flags | ItemFlags.Quarantined };
        var debt = (protection & 1) != 0;
        // Only current exact-path identity evidence can release metadata-preserving-write debt.
        // If a rename already removed that row, retain the debt for the after-write verification pass.
        if (debt && _reader.AtKey(rootNo, item.Path, tx).Any(row => row.Item.Path == item.Path &&
            row.Item.ObjectId == item.ObjectId && (row.Item.Size != item.Size || row.Item.Mtime != item.Mtime)))
        {
            StoreSchema.Execute(_connection, tx, ClearRehashDebtSql,
                ("$r", rootNo), ("$o", item.ObjectId));
            // Keep the queue obligation until the changed entity has been planned and settled.
            debt = false;
            item = item with { Hash = null };
        }
        if (debt && (StoreSchema.Scalar(_connection, tx,
            "SELECT 1 FROM file_state WHERE root_no=$r AND object_id=$o LIMIT 1;",
            ("$r", rootNo), ("$o", item.ObjectId)) is null || StoreSchema.Scalar(_connection, tx,
            RehashDebtProbeSql,
            ("$r", rootNo), ("$o", item.ObjectId)) is not null))
            return item with { Hash = null, Flags = item.Flags | ItemFlags.NeedsRehash };
        return (item.Flags & ItemFlags.NeedsRehash) == 0 ? item :
            item with { Hash = null, Flags = item.Flags & ~ItemFlags.NeedsRehash };
    }

    private bool Insert(FileStateReader.Entry entry, long rootNo, StoreBatch batch, bool debtReconciled = false,
        bool clearTombstone = true)
    {
        var dirNo = _dir.Intern(entry.Parent, batch.Transaction);
        if (!debtReconciled)
            entry = FileStateReader.Entry.Of(entry.Parent, entry.Name, ReconcileDebt(entry.Item, rootNo, batch.Transaction));
        batch.Execute(
            "INSERT INTO file_state(root_no, dir_no, name_key, name, kind, object_id, version_id, hash, size, mtime, flags, " +
            "identity_json) VALUES($r, $d, $nk, $n, $k, $o, $v, $h, $s, $m, $f, $i);",
            ("$r", rootNo), ("$d", dirNo), ("$nk", RowCodec.Key(entry.Name)), ("$n", entry.Name),
            ("$k", RowCodec.KindCode(entry.Item.Kind)),
            ("$o", entry.Item.ObjectId), ("$v", entry.Item.VersionId),
            ("$h", RowCodec.HashBlob(entry.Item.Hash)), ("$s", entry.Item.Size),
            ("$m", RowCodec.MtimeTicks(entry.Item.Mtime)), ("$f", (long)entry.Item.Flags),
            ("$i", entry.Item.IdentityJson));
        if (clearTombstone) ClearTombstone(rootNo, entry.Item.ObjectId, batch);
        if ((entry.Item.Flags & ItemFlags.Link) != 0)
            OnLinkWritten?.Invoke(batch.Transaction, rootNo, RowCodec.Join(entry.Parent, entry.Name));
        return true;
    }

    private void DeleteRow(long rootNo, long dirNo, string name, StoreBatch batch, string? storedKey = null) =>
        StoreSchema.Execute(_connection, batch.Transaction,
            "DELETE FROM file_state WHERE root_no=$r AND dir_no=$d AND name_key=$k AND name=$n;",
            ("$r", rootNo), ("$d", dirNo), ("$k", storedKey ?? RowCodec.Key(name)), ("$n", name));

    // Keyed by object: a later deletion at the same path never overwrites an unresolved tombstone (decision 19).
    // Written only for an object that has a baseline: the Planner's deletion evidence has no other reader (49).
    private void InsertTombstone(long rootNo, long dirNo, string name, string objectId, DateTimeOffset deletedAt,
        StoreBatch batch) => InsertTombstone(rootNo, dirNo, name, objectId, deletedAt, batch.Transaction);

    private void InsertTombstone(long rootNo, long dirNo, string name, string objectId, DateTimeOffset deletedAt,
        SqliteTransaction transaction)
    {
        if (!_baselined(rootNo, objectId, transaction)) return;
        StoreSchema.Execute(_connection, transaction,
            "INSERT INTO tombstone(root_no, object_id, dir_no, name, deleted_at) VALUES($r, $o, $d, $n, $t) " +
            "ON CONFLICT(root_no, object_id) DO UPDATE SET dir_no=excluded.dir_no, name=excluded.name, " +
            "deleted_at=excluded.deleted_at;",
            ("$r", rootNo), ("$d", dirNo), ("$n", name), ("$o", objectId), ("$t", deletedAt.ToUnixTimeMilliseconds()));
    }

    private void ClearTombstone(long rootNo, string objectId, StoreBatch batch) =>
        batch.Execute(
            "DELETE FROM tombstone WHERE root_no=$r AND object_id=$o;", ("$r", rootNo), ("$o", objectId));

    // "" is the whole root; otherwise the shared self-or-under rule (standards audit B2: no local copy of it).
    private static bool InsideIncomplete(string path, IReadOnlyList<string> subtrees) =>
        PairPath.CoveredBy(path, subtrees);
}

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    // Keyset pages release the store lock between batches and never depend on prunable generations.
    public IEnumerable<ItemState> QuarantineBaselineItems(string rootId, IReadOnlyList<string>? coveredPaths = null)
    {
        var roots = InStore(() => RootNo(rootId) is { } root
            ? RootSides(root, null).Select(x => (Root: root, Pair: x.Item1, Side: x.Item2)).ToArray()
            : []);
        if (coveredPaths is not null)
        {
            var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in coveredPaths.OrderBy(p => p.Length))
                if (!LocalInventory.UnderAny(path, scopes)) scopes.Add(path);
            coveredPaths = scopes.ToArray();
        }
        foreach (var binding in roots)
        {
            IEnumerable<ItemState> Pages(long? dir)
            {
                FileStateReader.Entry? after = null;
                while (true)
                {
                    var page = InStore(() => _reads.QuarantineBaselinePage(binding.Root, binding.Pair, binding.Side, after, dir));
                    if (page.Count == 0) yield break;
                    foreach (var row in page) yield return row.Item;
                    after = page[^1];
                }
            }
            if (coveredPaths is null)
            {
                foreach (var item in Pages(null)) yield return item;
                continue;
            }
            foreach (var scope in coveredPaths)
            {
                // Exact prefix item plus indexed descendant directories, paged without subtree materialization.
                var exact = InStore(() => _reads.Lookup(binding.Root, scope)?.Item);
                if (exact is not null && InStore(() => _points.HasBaseline(null, [(binding.Pair, binding.Side)], exact.ObjectId)))
                    yield return exact;
                var afterPath = "";
                long afterNo = -1;
                while (true)
                {
                    var dirs = InStore(() => _reads.QuarantineDirectories(scope, afterPath, afterNo));
                    if (dirs.Count == 0) break;
                    foreach (var directory in dirs)
                        foreach (var item in Pages(directory.No)) yield return item;
                    afterPath = RowCodec.Key(dirs[^1].Path);
                    afterNo = dirs[^1].No;
                }
            }
        }
    }

    public IReadOnlySet<string> DeferDefenderHistory(IReadOnlyList<string> objects, string run, DateTimeOffset now) => InStore(() =>
    {
        var expired = new HashSet<string>(StringComparer.Ordinal);
        if (objects.Count == 0) return expired;
        using var tx = _connection.BeginTransaction();
        foreach (var id in objects.Distinct(StringComparer.Ordinal))
        {
            StoreSchema.Execute(_connection, tx,
                "INSERT INTO defender_deferral(object_id,attempts,first_utc,last_run,updated) VALUES($id,1,$now,$run,$now) ON CONFLICT(object_id) DO UPDATE SET " +
                "attempts=attempts+CASE WHEN last_run=$run THEN 0 ELSE 1 END,last_run=$run,updated=MAX(updated,$now);",
                ("$id", id), ("$now", now.UtcTicks), ("$run", run));
            if (StoreSchema.Scalar(_connection, tx,
                "SELECT 1 FROM defender_deferral WHERE object_id=$id AND (attempts>=3 OR first_utc<=$cut);",
                ("$id", id), ("$cut", (now-TimeSpan.FromHours(24)).UtcTicks)) is not null) expired.Add(id);
        }
        tx.Commit();
        return expired;
    });

    public void ClearDefenderDeferrals(IReadOnlyList<string> objects) => InStore(() =>
    {
        if (objects.Count == 0) return;
        using var tx = _connection.BeginTransaction();
        StoreSchema.Execute(_connection, tx,
            "DELETE FROM defender_deferral WHERE object_id IN (SELECT value FROM json_each($ids));",
            ("$ids", System.Text.Json.JsonSerializer.Serialize(objects)));
        tx.Commit();
    });

    public void ReleaseDefenderDeferrals(IReadOnlyList<string> objects) => InStore(() =>
    {
        if (objects.Count == 0) return;
        using var tx = _connection.BeginTransaction();
        foreach (var id in objects.Distinct(StringComparer.Ordinal))
            StoreSchema.Execute(_connection, tx,
                "UPDATE defender_deferral SET consent=$released,batch=NULL WHERE object_id=$id AND consent=$pending;",
                ("$id", id), ("$released", (long)DefenderConsent.Released), ("$pending", (long)DefenderConsent.Pending));
        tx.Commit();
    });

    public IReadOnlyDictionary<string, DefenderDeferralState> DefenderDeferrals(IReadOnlyList<string> objects) => InStore(() =>
    {
        var result = new Dictionary<string, DefenderDeferralState>(StringComparer.Ordinal);
        foreach (var page in objects.Chunk(512))
        {
            using var cmd = StoreSchema.Command(_connection, null,
                "SELECT object_id,consent,updated,batch FROM defender_deferral WHERE object_id IN (SELECT value FROM json_each($ids));");
            cmd.Parameters.AddWithValue("$ids", System.Text.Json.JsonSerializer.Serialize(page));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result[reader.GetString(0)] = new((DefenderConsent)reader.GetInt64(1), new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero),
                    reader.IsDBNull(3) ? null : reader.GetString(3));
        }
        return (IReadOnlyDictionary<string, DefenderDeferralState>)result;
    });

    public IReadOnlyDictionary<string, DefenderConsent> DefenderDeferralConsent(IReadOnlyList<string> objects) =>
        DefenderDeferrals(objects).ToDictionary(row => row.Key, row => row.Value.Consent, StringComparer.Ordinal);

    public void AssignDefenderBatch(IReadOnlyList<string> objects, string batch) => InStore(() =>
    {
        using var tx = _connection.BeginTransaction();
        foreach (var id in objects.Distinct(StringComparer.Ordinal))
            StoreSchema.Execute(_connection, tx, "UPDATE defender_deferral SET batch=$batch WHERE object_id=$id AND consent=$pending AND batch IS NULL;",
                ("$batch", batch), ("$id", id), ("$pending", (long)DefenderConsent.Pending));
        tx.Commit();
    });

    public long DefenderBatchCount(string batch) => InStore(() => (long)StoreSchema.Scalar(_connection, null,
        "SELECT COUNT(*) FROM defender_deferral WHERE batch=$batch;", ("$batch", batch))!);

    // Proceed leaves the batch (its notice closes). Keep moves the rows to the root's standing held batch and
    // stamps the choice time, so a Keep-held row is not rechecked against Defender history for a week.
    public void DecideDefenderBatch(string batch, bool proceed, string rootId) => InStore(() =>
        StoreSchema.Execute(_connection, null,
            "UPDATE defender_deferral SET consent=$consent,batch=$next,updated=MAX(updated,$now) WHERE batch=$batch AND consent=$pending;",
            ("$consent", (long)(proceed ? DefenderConsent.Proceed : DefenderConsent.Keep)),
            ("$next", proceed ? null : DefenderReview.HeldBatch(rootId)), ("$now", _clock.GetUtcNow().UtcTicks),
            ("$batch", batch), ("$pending", (long)DefenderConsent.Pending)));

    public void ReopenDefenderBatch(string closedBatch, string? batch) => InStore(() =>
        StoreSchema.Execute(_connection, null, "UPDATE defender_deferral SET batch=$batch WHERE batch=$closed AND consent=$pending;",
            ("$batch", batch), ("$closed", closedBatch), ("$pending", (long)DefenderConsent.Pending)));

    // The batch index, then the root's object index: only this root's live Keep-held entities are counted or listed.
    private const string HeldFrom = "FROM defender_deferral q INDEXED BY defender_deferral_batch " +
        "CROSS JOIN file_state f INDEXED BY fs_obj ON f.root_no=$root AND f.object_id=q.object_id ";
    private const string HeldWhere = "WHERE q.batch=$held AND q.consent=$keep";

    public IReadOnlyList<Notice> DefenderNotices(string pairId, string rootId) => InStore(() =>
        PairNo(pairId) is { } pair ? _notices.Open(pair, rootId, unresolved: true) : (IReadOnlyList<Notice>)[]);

    public long DefenderHeldCount(string rootId, string? pairId = null) => InStore(() =>
    {
        if (RootNo(rootId) is not { } root) return 0L;
        var excluded = pairId is null ? [] : PairRecord(pairId)?.Excluded ?? [];
        if (excluded.Count == 0)
            return (long)StoreSchema.Scalar(_connection, null, "SELECT COUNT(*) " + HeldFrom + HeldWhere + ";",
                ("$root", root), ("$held", DefenderReview.HeldBatch(rootId)), ("$keep", (long)DefenderConsent.Keep))!;
        // Stream only actual held rows; never materialize the root inventory to count one binding.
        using var cmd = StoreSchema.Command(_connection, null,
            "SELECT d.path,f.name " + HeldFrom + "JOIN dir d ON d.dir_no=f.dir_no " + HeldWhere + ";");
        cmd.Parameters.AddWithValue("$root", root);
        cmd.Parameters.AddWithValue("$held", DefenderReview.HeldBatch(rootId));
        cmd.Parameters.AddWithValue("$keep", (long)DefenderConsent.Keep);
        using var reader = cmd.ExecuteReader();
        long count = 0;
        while (reader.Read())
            if (!excluded.Any(prefix => PairPath.IsSelfOrUnder(RowCodec.Join(reader.GetString(0), reader.GetString(1)), prefix))) count++;
        return count;
    });

    public IReadOnlyList<string> DefenderHeldPaths(string rootId, int limit, string? pairId = null) => InStore(() =>
    {
        var paths = new List<string>();
        var excluded = pairId is null ? [] : (PairRecord(pairId) ?? throw new InvalidOperationException($"Unknown pair {pairId}")).Excluded;
        if (limit <= 0 || RootNo(rootId) is not { } root) return (IReadOnlyList<string>)paths;
        using (var cmd = StoreSchema.Command(_connection, null,
            "SELECT d.path,f.name " + HeldFrom + "JOIN dir d ON d.dir_no=f.dir_no " + HeldWhere + " ORDER BY q.object_id;"))
        {
            cmd.Parameters.AddWithValue("$root", root);
            cmd.Parameters.AddWithValue("$held", DefenderReview.HeldBatch(rootId));
            cmd.Parameters.AddWithValue("$keep", (long)DefenderConsent.Keep);
            using var reader = cmd.ExecuteReader();
            while (paths.Count < limit && reader.Read())
            {
                var path = RowCodec.Join(reader.GetString(0), reader.GetString(1));
                if (!excluded.Any(prefix => PairPath.IsSelfOrUnder(path, prefix))) paths.Add(path);
            }
        }
        paths.Sort(StringComparer.OrdinalIgnoreCase);
        return (IReadOnlyList<string>)paths;
    });

    // Release is the user's own act: it becomes Proceed, so the next pass still rechecks positive evidence first.
    public int ReleaseDefenderHeld(string rootId, string? objectId) => InStore(() =>
    {
        StoreSchema.Execute(_connection, null,
            "UPDATE defender_deferral SET consent=$proceed,batch=NULL,updated=MAX(updated,$now) " +
            "WHERE batch=$held AND consent=$keep AND ($id IS NULL OR object_id=$id);",
            ("$proceed", (long)DefenderConsent.Proceed), ("$now", _clock.GetUtcNow().UtcTicks),
            ("$held", DefenderReview.HeldBatch(rootId)), ("$keep", (long)DefenderConsent.Keep), ("$id", objectId));
        return (int)StoreSchema.Changes(_connection, null);
    });

    public bool ForgetDefenderHeld(string rootId, string objectId) => InStore(() =>
    {
        if (RootNo(rootId) is { } root)
        {
            var item = _reads.ByObject(root, objectId);
            if (item is not null && Pairs().Any(pair => (pair.A.RootId == rootId || pair.B.RootId == rootId) &&
                !pair.Excluded.Any(prefix => PairPath.IsSelfOrUnder(item.Path, prefix)))) return false;
        }
        StoreSchema.Execute(_connection, null, "DELETE FROM defender_deferral WHERE object_id=$id AND batch=$held AND consent=$keep;",
            ("$id", objectId), ("$held", DefenderReview.HeldBatch(rootId)), ("$keep", (long)DefenderConsent.Keep));
        return StoreSchema.Changes(_connection, null) == 1;
    });

    // A Keep-held row returns to rotation (a history recheck) only once a week; the user's own act rechecks sooner.
    public IReadOnlyList<string> DefenderDeferredPaths(string rootId, int limit) => InStore(() =>
    {
        var paths = new List<string>();
        if (limit <= 0 || RootNo(rootId) is not { } root) return paths;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        using (var cmd = StoreSchema.Command(_connection, null,
            "SELECT d.path,f.name,q.object_id FROM defender_deferral q CROSS JOIN file_state f INDEXED BY fs_obj ON f.object_id=q.object_id " +
            "JOIN dir d ON d.dir_no=f.dir_no WHERE f.root_no=$root AND (q.consent<>$keep OR q.updated<=$due) " +
            "ORDER BY q.updated,q.object_id LIMIT $limit;"))
        {
            cmd.Parameters.AddWithValue("$root", root);
            cmd.Parameters.AddWithValue("$limit", limit);
            cmd.Parameters.AddWithValue("$keep", (long)DefenderConsent.Keep);
            cmd.Parameters.AddWithValue("$due", (_clock.GetUtcNow() - DefenderReview.KeepRecheck).UtcTicks);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                paths.Add(reader.GetString(0).Length == 0 ? reader.GetString(1) : reader.GetString(0)+"/"+reader.GetString(1));
                ids.Add(reader.GetString(2));
            }
        }
        // Rotate even paths later found unreadable/excluded. Selection is not a history attempt.
        using var tx = _connection.BeginTransaction();
        var now = Math.Max(_clock.GetUtcNow().UtcTicks, (long)StoreSchema.Scalar(_connection, tx, "SELECT COALESCE(MAX(updated),0)+1 FROM defender_deferral;")!);
        foreach (var id in ids)
            StoreSchema.Execute(_connection, tx, "UPDATE defender_deferral SET updated=$now WHERE object_id=$id;", ("$now", now), ("$id", id));
        tx.Commit();
        return paths;
    });

    // Same-identity inventory moves carry the hold and its review to the new name. Path-bound
    // Defender grants deliberately stay at their exact historical exclusion paths instead.
    private void FollowQuarantineNotices(string rootId)
    {
        var rootNo = RootNo(rootId);
        if (rootNo is null) return;
        foreach (var pair in Pairs().Where(p => p.A.RootId == rootId || p.B.RootId == rootId))
        {
            var side = pair.A.RootId == rootId ? Side.A : Side.B;
            var pairNo = RequirePair(pair.PairId);
            foreach (var notice in _notices.Open(pairNo, reasonKey: QuarantineFailure.NoticeKey, unresolved: true))
            {
                if (notice.Quarantine is not { ConflictingGrantId: null, GrantedExclusionPath: null } hold) continue;
                var expected = hold.Target == side ? hold.ExpectedTarget : hold.Source;
                if (expected is null || _reads.ByObject(rootNo.Value, expected.ObjectId) is not { } current ||
                    !current.Flags.HasFlag(ItemFlags.Quarantined) || current.Path == expected.Path) continue;
                // Preserve reviewed content evidence; a rename is not permission to accept new bytes.
                var moved = expected with { Path = current.Path };
                var updated = hold.Target == side ? hold with { ExpectedTarget = moved } : hold with { Source = moved };
                using var transaction = _connection.BeginTransaction();
                _notices.Add(transaction, pairNo, notice with { NoticeId = null, Path = current.Path, Quarantine = updated }, NowMs());
                if (!PairPath.Same(notice.Path, current.Path))
                    _notices.CloseResolved(transaction, pairNo, notice.NoticeId!, "quarantine-renamed");
                transaction.Commit();
            }
        }
    }

    private sealed record CapturedDefenderNotice(Notice Original, Notice Captured, PairDef? Pair,
        ItemState? SourceObservation, ItemState? TargetObservation, QuarantineNotice? Previous = null);

    private ItemState? NoticeObservation(ItemState? item, RootDef root, Microsoft.Data.Sqlite.SqliteTransaction? transaction = null) =>
        item is not null && RootNo(root.RootId, transaction) is { } number
            ? _reads.AtKey(number, item.Path, transaction).FirstOrDefault().Item : null;

    private Notice RetainedDefenderNotice(Notice notice, PairDef pair, Microsoft.Data.Sqlite.SqliteTransaction? transaction = null)
    {
        var hold = notice.Quarantine!;
        ItemState? Retained(ItemState? item, RootDef root) => item is null ? null :
            RootNo(root.RootId, transaction) is { } number ? _reads.AtKey(number, item.Path, transaction)
                .FirstOrDefault(row => row.Item?.ObjectId == item.ObjectId).Item ?? item : item;
        return notice with { Quarantine = hold with
        {
            Source = Retained(hold.Source, hold.Target == Side.A ? pair.B : pair.A),
            ExpectedTarget = Retained(hold.ExpectedTarget, hold.Target == Side.A ? pair.A : pair.B)
        } };
    }

    private CapturedDefenderNotice PrepareDefenderNotice(Notice notice, bool retainLive, CancellationToken? cancellationToken = null)
    {
        // Reentrant publication cannot release its caller's atomic transaction.
        // Grant observations instead stage a bounded page and capture after releasing the gate.
        if (notice.Quarantine is null || _gate.IsHeldByCurrentThread) return new(notice, notice, null, null, null);
        return CapturePreparedDefenderNotice(InStore(() => PrepareDefenderNoticeCore(notice, retainLive)),
            cancellationToken ?? FairStoreGate.CurrentCancellation);
    }

    private CapturedDefenderNotice PrepareDefenderNoticeCore(Notice notice, bool retainLive)
    {
        var pair = PairRecord(notice.PairId) ?? throw new InvalidOperationException($"Unknown pair {notice.PairId}");
        var original = retainLive ? RetainedDefenderNotice(notice, pair) : notice;
        var previous = _notices.ById(RequirePair(pair.PairId), NoticeTable.Id(original))?.Notice.Quarantine;
        var hold = original.Quarantine!;
        return new(original, original, pair,
            NoticeObservation(hold.Source, hold.Target == Side.A ? pair.B : pair.A),
            NoticeObservation(hold.ExpectedTarget, hold.Target == Side.A ? pair.A : pair.B), previous);
    }

    private CapturedDefenderNotice CapturePreparedDefenderNotice(CapturedDefenderNotice prepared, CancellationToken ct)
    {
        if (_gate.IsHeldByCurrentThread) throw new InvalidOperationException("Optional hashing must run outside the store gate.");
        return prepared with { Captured = CaptureDefenderNotice(prepared.Original, prepared.Pair!, prepared.Previous, ct) };
    }

    private Notice ValidateDefenderCapture(CapturedDefenderNotice capture, Notice current, Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        if (current.Quarantine is not { } hold || capture.Pair is not { } pair) return current;
        var currentPair = PairRecord(current.PairId, transaction);
        // Bind evidence to its own roots and exact observations, not unrelated writes by
        // other pairs or background gap pages. Absence/presence changes also invalidate it.
        return currentPair is not null && currentPair.A == pair.A && currentPair.B == pair.B &&
            capture.Original.Quarantine == hold &&
            capture.SourceObservation == NoticeObservation(hold.Source, hold.Target == Side.A ? pair.B : pair.A, transaction) &&
            capture.TargetObservation == NoticeObservation(hold.ExpectedTarget, hold.Target == Side.A ? pair.A : pair.B, transaction)
            ? capture.Captured : current;
    }

    internal Func<ItemState, ItemState>? DefenderCaptureForTest { get; set; }

    private Notice CaptureDefenderNotice(Notice notice, PairDef pair, QuarantineNotice? previous, CancellationToken ct)
    {
        if (notice.Quarantine is not { } hold) return notice;
        ItemState? Capture(ItemState? item, RootDef root)
        {
            if (root.Provider != SyncProviders.Local || item is not { Kind: ItemKind.File, Hash: null } ||
                (item.Flags & (ItemFlags.Unreadable | ItemFlags.Locked | ItemFlags.Link | ItemFlags.Placeholder)) != 0) return item;
            // A published review is evidence, not a request to read its held bytes again on every pass.
            var previousTarget = previous?.Target == Side.A ? pair.A : pair.B;
            var recorded = previousTarget.RootId == root.RootId ? previous?.ExpectedTarget : previous?.Source;
            if (recorded is not null && recorded.ObjectId == item.ObjectId && PairPath.Same(recorded.Path, item.Path) &&
                recorded.VersionId == item.VersionId && recorded.Size == item.Size && recorded.Mtime == item.Mtime)
                return item with { Hash = recorded.Hash };
            if (ct.IsCancellationRequested) return item;
            if (DefenderCaptureForTest is { } capture) return capture(item);
            return LocalItems.CaptureOptionalHash(root, item, ct);
        }
        return notice with { Quarantine = hold with
        {
            Source = Capture(hold.Source, hold.Target == Side.A ? pair.B : pair.A),
            ExpectedTarget = Capture(hold.ExpectedTarget, hold.Target == Side.A ? pair.A : pair.B)
        } };
    }

    public bool AcceptDefenderGrant(DefenderGrant expected, ItemState local, ItemState? cloud) => InStore(() =>
    {
        if (DefenderGrantById(expected.GrantId) != expected || local.Kind != ItemKind.File || string.IsNullOrEmpty(local.ObjectId)) return false;
        StoreSchema.Execute(_connection, null, "UPDATE defender_grant SET owner_object=$local,local_object=$local,source_object=$source WHERE grant_id=$id AND verified<>2;",
            ("$local", local.ObjectId), ("$source", cloud?.ObjectId ?? expected.SourceObject), ("$id", expected.GrantId));
        return StoreSchema.Changes(_connection, null) == 1;
    });

    public void StoreDefenderGrant(DefenderGrant grant) => InStore(() =>
        StoreSchema.Execute(_connection, null,
            "INSERT INTO defender_grant(grant_id,owner_object,source_object,local_object,path,path_key,stage_path,verified) " +
            "VALUES($id,$owner,$source,$local,$path,$key,$stage,$verified) ON CONFLICT(grant_id) DO UPDATE SET verified=MAX(verified,excluded.verified);",
            ("$id", grant.GrantId), ("$owner", grant.OwnerObject), ("$source", grant.SourceObject), ("$local", grant.LocalObject),
            ("$path", grant.Path), ("$key", RowCodec.Key(grant.Path)), ("$stage", grant.StagePath), ("$verified", grant.Verified ? 1 : 0)));

    public DefenderGrant? DefenderGrantConflict(string pairId, SyncAction action) => InStore(() =>
    {
        var pair = PairRecord(pairId);
        if (pair is null || action.Kind is not (ActionKind.CopyNew or ActionKind.Update or ActionKind.Move or ActionKind.MkDir or ActionKind.KeepBoth or ActionKind.Adopt)) return null;
        var targetRoot = action.Target == Side.A ? pair.A : pair.B;
        var sourceLocal = targetRoot.Provider != SyncProviders.Local;
        var root = sourceLocal ? (action.Target == Side.A ? pair.B : pair.A) : targetRoot;
        if (root.Provider != SyncProviders.Local || sourceLocal && action.Source is null) return null;
        var path = Path.GetFullPath(Path.Combine(root.RootPath, (sourceLocal ? action.Source!.Path : action.Path).Replace('/', Path.DirectorySeparatorChar)));
        var folder = action.Kind == ActionKind.MkDir || action.Source?.Kind == ItemKind.Folder;
        // Indexed exact ancestor checks protect descendants; a range check protects an enclosing folder move/create.
        for (var at = path; at is not null; at = Path.GetDirectoryName(at))
        {
            using var cmd = StoreSchema.Command(_connection, null,
                "SELECT grant_id,owner_object,source_object,local_object,path,stage_path,verified FROM defender_grant WHERE path_key=$key AND verified<>2;");
            cmd.Parameters.AddWithValue("$key", RowCodec.Key(at));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var source = reader.IsDBNull(2) ? null : reader.GetString(2);
                var local = reader.IsDBNull(3) ? null : reader.GetString(3);
                var explicitMissing = action.QuarantineStage && action.ExpectedTarget is null &&
                    string.Equals(reader.GetString(5), AttendedElevatedAction.DownloadStagePath(path), StringComparison.OrdinalIgnoreCase);
                var same = string.Equals(path, at, StringComparison.OrdinalIgnoreCase) && !folder &&
                    (sourceLocal || action.Kind == ActionKind.Move ? action.Source?.ObjectId == local && local is not null :
                        source is not null && action.Source?.ObjectId == source &&
                        (explicitMissing || local is not null && action.ExpectedTarget?.ObjectId == local));
                if (!same) return ReadGrant(reader);
            }
        }
        if (folder)
        {
            var prefix = RowCodec.Key(path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            if (StoreSchema.Scalar(_connection, null,
                "SELECT grant_id FROM defender_grant WHERE path_key >= $prefix AND path_key < $end AND verified<>2 LIMIT 1;",
                ("$prefix", prefix), ("$end", prefix + char.MaxValue)) is string child) return DefenderGrantById(child);
        }
        return null;
    });

    private static DefenderGrant ReadGrant(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetString(4), reader.GetString(5), reader.GetInt64(6) == 1);

    public DefenderGrant? DefenderGrantById(string id) => InStore(() =>
    {
        using var cmd = StoreSchema.Command(_connection, null,
            "SELECT grant_id,owner_object,source_object,local_object,path,stage_path,verified FROM defender_grant WHERE grant_id=$id AND verified<>2;");
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadGrant(reader) : null;
    });

    public IReadOnlyList<string> DefenderGrantHoldPaths(string rootId) => InStore(() => ObserveDefenderGrants(rootId, publish: false));

    private IReadOnlyList<string> ObserveDefenderGrants(string rootId, bool publish)
    {
        var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootNumber = InStore(() => RootNo(rootId));
        if (rootNumber is not { } rootNo) return [];
        var bindings = InStore(() => RootSides(rootNo, null).Select(binding =>
        {
            var json = (string)StoreSchema.Scalar(_connection, null, "SELECT def_json FROM pair WHERE pair_no=$p;", ("$p", binding.PairNo))!;
            return (Pair: Deserialize(json), binding.Side);
        }).ToArray());
        if (bindings.Length == 0) return [];
        var first = bindings[0];
        var root = first.Side == Side.A ? first.Pair.A : first.Pair.B;
        if (root.Provider != SyncProviders.Local) return [];
        var prefix = RowCodec.Key(Path.GetFullPath(root.RootPath).TrimEnd('\\', '/') + Path.DirectorySeparatorChar);
        var after = prefix;
        var afterId = "";
        while (true)
        {
            // Range/keyset over grant paths, never a file inventory scan or an all-grants x all-items join.
            var page = new List<DefenderGrant>();
            var prepared = new List<CapturedDefenderNotice>();
            InStore(() =>
            {
                using (var cmd = StoreSchema.Command(_connection, null,
                    "SELECT grant_id,owner_object,source_object,local_object,path,stage_path,verified FROM defender_grant " +
                    "WHERE path_key >= $prefix AND path_key < $end AND (path_key > $after OR (path_key=$after AND grant_id>$id)) " +
                    "AND verified<>2 ORDER BY path_key,grant_id LIMIT 512;"))
                {
                    cmd.Parameters.AddWithValue("$prefix", prefix); cmd.Parameters.AddWithValue("$end", prefix + char.MaxValue);
                    cmd.Parameters.AddWithValue("$after", after); cmd.Parameters.AddWithValue("$id", afterId);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read()) page.Add(ReadGrant(reader));
                }
                foreach (var grant in page)
                {
                    var relative = Path.GetRelativePath(root.RootPath, grant.Path).Replace('\\', '/');
                    var item = _reads.Lookup(rootNo, relative)?.Item;
                    if (item is null) continue; // Missing does not prohibit a separately authorized same-source download.
                    if (item.Kind == ItemKind.File && !string.IsNullOrEmpty(item.ObjectId) && item.ObjectId == grant.LocalObject &&
                        (item.Flags & (ItemFlags.Unreadable | ItemFlags.Locked | ItemFlags.Link)) == 0) continue;
                    held.Add(item.Path);
                    if (!publish) continue;
                    foreach (var binding in bindings)
                    {
                        // Do not attach a captured old-root observation to a newly rebound pair.
                        var currentPair = PairRecord(binding.Pair.PairId);
                        if (currentPair is null || currentPair.A != binding.Pair.A || currentPair.B != binding.Pair.B) continue;
                        prepared.Add(PrepareDefenderNoticeCore(new Notice(binding.Pair.PairId, 30, NoticeKind.UserDecision,
                            item.Path, QuarantineFailure.GrantWarning(grant.Path), QuarantineFailure.NoticeKey,
                            Quarantine: new(binding.Side, null, item, ScanOnly: true, GrantedExclusionPath: grant.Path,
                                GrantedStagePath: grant.StagePath, ConflictingGrantId: grant.GrantId)), retainLive: false));
                    }
                }
            });
            if (page.Count == 0) break;
            // Durable grants and inventory re-derive this page after interruption; no cursor or
            // grant is advanced by capture/publication. Notice ids make repeated publication idempotent.
            if (prepared.Count != 0)
                PublishPreparedNotices(prepared.Select(item => CapturePreparedDefenderNotice(item,
                    FairStoreGate.CurrentCancellation)).ToArray());
            after = RowCodec.Key(page[^1].Path);
            afterId = page[^1].GrantId;
        }
        return held.ToArray();
    }

    public bool RetireDefenderGrant(DefenderGrant expected) => InStore(() =>
    {
        if (DefenderGrantById(expected.GrantId) != expected) return false;
        StoreSchema.Execute(_connection, null, "UPDATE defender_grant SET verified=2 WHERE grant_id=$id;", ("$id", expected.GrantId));
        return true;
    });

    private void RebindDefenderGrant(Microsoft.Data.Sqlite.SqliteTransaction tx, PairDef pair, SyncAction action, ActionResult result)
    {
        var root = action.Target == Side.A ? pair.A : pair.B;
        var written = action.Target == Side.A ? result.NewA : result.NewB;
        if (root.Provider != SyncProviders.Local || action.Kind is not (ActionKind.CopyNew or ActionKind.Update) ||
            written is not { Kind: ItemKind.File } || action.Source is null) return;
        var path = Path.GetFullPath(Path.Combine(root.RootPath, action.Path.Replace('/', Path.DirectorySeparatorChar)));
        StoreSchema.Execute(_connection, tx,
            "UPDATE defender_grant SET local_object=$new WHERE path_key=$path AND source_object=$source AND verified<>2 " +
            "AND (local_object=$old OR ($old IS NULL AND $explicit=1));",
            ("$new", written.ObjectId), ("$path", RowCodec.Key(path)), ("$source", action.Source.ObjectId),
            ("$old", action.ExpectedTarget?.ObjectId), ("$explicit", action.QuarantineStage ? 1 : 0));
    }

    public bool QuarantineRecoveryRetained(string pairId, string noticeId) => InStore(() =>
        StoreSchema.Scalar(_connection, null,
            "SELECT 1 FROM notice n JOIN pair p ON p.pair_no=n.pair_no WHERE p.pair_id=$p AND n.notice_id=$id " +
            "AND n.reason_key='antivirus-quarantine' AND " + NoticeTable.ActionableSql("n.state") + " AND n.choice='quarantine-ignore';",
            ("$p", pairId), ("$id", noticeId)) is not null);

    public void HoldQuarantine(string rootId, ItemState item) => InStore(() =>
    {
        if (RootNo(rootId) is not { } root) return;
        using var transaction = _connection.BeginTransaction();
        _files.HoldQuarantine(transaction, root, item);
        // Native/positive evidence ends any prior UNKNOWN absence consent, including hash/write failures.
        StoreSchema.Execute(_connection, transaction, "DELETE FROM defender_deferral WHERE object_id=$id;", ("$id", item.ObjectId));
        transaction.Commit();
    });

    public bool ClearQuarantine(string rootId, ItemState item) => InStore(() =>
    {
        if (RootNo(rootId) is not { } rootNo) return false;
        using var transaction = _connection.BeginTransaction();
        using var command = StoreSchema.Command(_connection, transaction,
            "UPDATE file_state SET flags=flags & ~$f WHERE root_no=$r AND object_id=$o AND (flags & $f)!=0;");
        command.Parameters.AddWithValue("$f", (long)ItemFlags.Quarantined);
        command.Parameters.AddWithValue("$r", rootNo);
        command.Parameters.AddWithValue("$o", item.ObjectId);
        var changed = command.ExecuteNonQuery() > 0;
        transaction.Commit();
        return changed;
    });
}

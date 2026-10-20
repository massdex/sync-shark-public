using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>The SQLite state store; the only place that opens the database and owns SQL.</summary>
public sealed partial class SqliteStateStore : IStateStore
{
    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private readonly V2Settings _settings;
    private readonly TimeProvider _clock;
    private readonly DirTable _dir;
    private readonly FileStateReader _reads;
    private readonly FileStateTable _files;
    private readonly SyncPointTable _points;
    private readonly IntentTable _intents;
    private readonly RecoveryTable _recoveries;
    private readonly NoticeTable _notices;
    private readonly RunSummaryTable _runHistory;
    private readonly DeletionEvidenceTable _deletionEvidence;
    private readonly Dictionary<string, long> _rootNo = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _pairNo = new(StringComparer.Ordinal);
    private readonly Dictionary<long, (long PairNo, Side Side)[]> _rootSides = new();
    private bool _disposed;
    private bool _storageEnabled;
    private long _storageProbeTimestamp;

    private SqliteStateStore(SqliteConnection connection, V2Settings settings, TimeProvider clock)
    {
        _connection = connection;
        _connection.CreateFunction<string, string>("choice_key", PathKey.Of, isDeterministic: true);
        _settings = settings;
        _clock = clock;
        _dir = new DirTable(connection);
        _storageEnabled = FilesOnDemandSchema.IsEnabled(connection);
        _storageProbeTimestamp = _clock.GetTimestamp();
        _reads = new FileStateReader(connection, _dir) { StorageEnabled = _storageEnabled };
        _points = new SyncPointTable(connection, _dir);
        _files = new FileStateTable(connection, _dir, _reads, settings, clock,
            (rootNo, objectId, transaction) =>
                _points.HasBaseline(transaction, RootSides(rootNo, transaction), objectId));
        _files.OnLinkWritten = ForgetUnderLink;
        _intents = new IntentTable(connection);
        _recoveries = new RecoveryTable(connection);
        _notices = new NoticeTable(connection);
        _runHistory = new RunSummaryTable(connection);
        _deletionEvidence = new DeletionEvidenceTable(connection, settings.StoreBatchRows);
        Load();
    }

    /// <summary>The one database file path beneath <paramref name="dataRoot"/>; every caller reads it from here.</summary>
    public static string DatabasePath(string dataRoot) => Path.Combine(dataRoot, StoreSchema.DatabaseRelativePath);

    /// <summary>Opens (creating when fresh) the store beneath <paramref name="dataRoot"/>; a damaged DB throws on use.</summary>
    public static SqliteStateStore Open(string dataRoot, V2Settings settings, TimeProvider clock) =>
        OpenCore(dataRoot, settings, clock, sweepPrepare: true);

    // The pause control plane can coexist with a run publishing a prepare. It must not sweep that run's staging.
    internal static SqliteStateStore OpenPauseControl(string dataRoot) =>
        OpenCore(dataRoot, V2Settings.Default, TimeProvider.System, sweepPrepare: false);

    private static SqliteStateStore OpenCore(string dataRoot, V2Settings settings, TimeProvider clock, bool sweepPrepare)
    {
        Validate(settings);
        var dbPath = DatabasePath(dataRoot);
        if (!sweepPrepare && (!File.Exists(dbPath) || new FileInfo(dbPath).Length == 0))
            throw new InvalidOperationException("No state yet. Run or configure Sync Shark before pausing a pair.");
        if (sweepPrepare) Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var fresh = sweepPrepare && (!File.Exists(dbPath) || new FileInfo(dbPath).Length == 0);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = sweepPrepare ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            StoreSchema.Pragmas(connection, settings);
            if (fresh) StoreSchema.Create(connection);
            else StoreSchema.Verify(connection);
            StoreSchema.CreateTemporary(connection);
            SnapshotTemporary.Sweep(connection);
            var store = new SqliteStateStore(connection, settings, clock);
            if (sweepPrepare) store.TrySweepPrepareGenerations(propagateDamage: true);
            return store;
        }
        catch (SqliteException exception) when (StoreSchema.IsDamaged(exception))
        {
            connection.Dispose();
            throw new StateDamagedException($"v2 state is damaged: {exception.Message}", exception);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Closes the store's connection; idempotent.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _connection.Dispose();
        }
    }

    public long BeginGeneration(string rootId, string kind) => InStore(() =>
    {
        if (string.IsNullOrEmpty(kind)) throw new ArgumentException("A generation needs a kind.", nameof(kind));
        var now = NowMs();
        using var transaction = _connection.BeginTransaction();
        var rootNo = RootNoCreate(rootId, transaction);
        StoreSchema.Execute(_connection, transaction,
            "UPDATE generation SET finished=$now, complete=0 WHERE root_no=$r AND finished IS NULL;",
            ("$now", now), ("$r", rootNo));
        StoreSchema.Execute(_connection, transaction,
            "INSERT INTO generation(root_no, kind, started) VALUES($r, $k, $now);",
            ("$r", rootNo), ("$k", kind), ("$now", now));
        var gen = (long)StoreSchema.Scalar(_connection, transaction, "SELECT last_insert_rowid();")!;
        transaction.Commit();
        _rootNo[rootId] = rootNo;
        return gen;
    });

    public void WriteSnapshot(Snapshot snapshot) => InStore(() =>
    {
        var rootNo = RootNo(snapshot.RootId) ?? throw new InvalidOperationException($"Unknown root {snapshot.RootId}");
        RequireOpenGen(null, snapshot.Gen, rootNo);
        _files.WriteSnapshot(snapshot, rootNo);
        CleanupStorageItems();
        BindStoragePathIntents(snapshot.RootId);
        FollowQuarantineNotices(snapshot.RootId);
        ObserveDefenderGrants(snapshot.RootId, publish: true);
    });

    public void WriteDelta(Delta delta) => InStore(() =>
    {
        if (delta.CursorInvalid)
            throw new InvalidOperationException("A cursor-invalid delta must not be written; run a full scan.");
        var rootNo = RootNo(delta.RootId) ?? throw new InvalidOperationException($"Unknown root {delta.RootId}");
        RequireOpenGen(null, delta.Gen, rootNo);
        _files.WriteDelta(delta, rootNo);
        CleanupStorageItems();
        BindStoragePathIntents(delta.RootId);
        FollowQuarantineNotices(delta.RootId);
        ObserveDefenderGrants(delta.RootId, publish: true);
    });

    public void CompleteGeneration(long gen, string cursor, bool complete) => InStore(() =>
    {
        using var transaction = _connection.BeginTransaction();
        StoreSchema.Execute(_connection, transaction,
            "UPDATE generation SET finished=$now, complete=$c WHERE gen=$g AND finished IS NULL;",
            ("$now", NowMs()), ("$c", complete ? 1 : 0), ("$g", gen));
        if (StoreSchema.Changes(_connection, transaction) == 0)
            throw new InvalidOperationException($"Generation {gen} is unknown, finished or abandoned");
        if (complete)
        {
            var rootNo = (long)StoreSchema.Scalar(_connection, transaction,
                "SELECT root_no FROM generation WHERE gen=$g;", ("$g", gen))!;
            StoreSchema.Execute(_connection, transaction, "UPDATE root SET cursor=$cursor WHERE root_no=$r;",
                ("$cursor", cursor), ("$r", rootNo));
        }
        transaction.Commit();
    });

    public string? Cursor(string rootId) => InStore(() =>
        StoreSchema.Scalar(_connection, null, "SELECT cursor FROM root WHERE root_id=$id;", ("$id", rootId)) as string);

    public void ForgetCursor(string rootId) => InStore(() =>
        StoreSchema.Execute(_connection, null, "UPDATE root SET cursor=NULL WHERE root_id=$id;", ("$id", rootId)));

    public bool FirstSyncDone(string pairId) => InStore(() =>
        StoreSchema.Scalar(_connection, null, "SELECT first_sync_done FROM pair WHERE pair_id=$id;", ("$id", pairId))
            is long done && done == 1);

    public void MarkFirstSyncDone(string pairId) => InStore(() =>
    {
        RequirePair(pairId);
        StoreSchema.Execute(_connection, null, "UPDATE pair SET first_sync_done=1 WHERE pair_id=$id;", ("$id", pairId));
    });

    public IReadOnlyList<ItemState> Live(string rootId, IEnumerable<string>? paths = null) => InStore(() =>
    {
        var rootNo = RootNo(rootId);
        return rootNo is null ? Array.Empty<ItemState>() : _reads.Live(rootNo.Value, paths);
    });

    public IReadOnlyList<ItemState> LiveUnder(string rootId, IEnumerable<string> pathPrefixes) => InStore(() =>
    {
        var rootNo = RootNo(rootId);
        return rootNo is null ? Array.Empty<ItemState>() : _reads.LiveUnder(rootNo.Value, pathPrefixes.ToArray());
    });

    public IReadOnlyList<ItemState> LiveChildren(string rootId, string directory) => InStore(() =>
    {
        var rootNo = RootNo(rootId);
        return rootNo is null ? Array.Empty<ItemState>() : _reads.LiveChildren(rootNo.Value, directory);
    });

    public IReadOnlyList<string> DirectorySpellings(string rootId, string path) => InStore(() =>
    {
        var rootNo = RootNo(rootId);
        if (rootNo is null) return Array.Empty<string>();
        using var command = StoreSchema.Command(_connection, null,
            "SELECT path FROM dir WHERE dir_no IN (SELECT dir_no FROM dir WHERE path_key=$k UNION SELECT dir_no FROM dir WHERE mapped_key=$k) " +
            "AND EXISTS (SELECT 1 FROM file_state f WHERE f.root_no=$r AND f.dir_no=dir.dir_no);");
        command.Parameters.AddWithValue("$k", RowCodec.Key(DropboxNameMapping.Map(path)));
        command.Parameters.AddWithValue("$r", rootNo.Value);
        var result = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result.ToArray();
    });

    /// <summary>Every live spelling of one path through the stored keys (one indexed lookup, no subtree read).</summary>
    public IReadOnlyList<ItemState> LiveSpellings(string rootId, string path) => InStore(() =>
    {
        var rootNo = RootNo(rootId);
        return rootNo is null || path.Length == 0
            ? Array.Empty<ItemState>()
            : _reads.AtKey(rootNo.Value, path, null).Select(entry => entry.Item).ToArray();
    });

    public IReadOnlyList<ItemState> LiveFlagged(string rootId, ItemFlags flags) => InStore(() =>
    {
        var rootNo = RootNo(rootId);
        return rootNo is null ? Array.Empty<ItemState>() : _reads.LiveFlagged(rootNo.Value, flags);
    });

    public IReadOnlyList<ItemState> LiveFlaggedFolders(string rootId, ItemFlags flags) => InStore(() =>
    {
        var rootNo = RootNo(rootId);
        return rootNo is null ? Array.Empty<ItemState>() : _reads.LiveFlaggedFolders(rootNo.Value, flags);
    });

    public (long Count, IReadOnlyList<ItemState> Examples) LiveFlaggedSummary(string rootId, ItemFlags flags, int limit) =>
        InStore<(long Count, IReadOnlyList<ItemState> Examples)>(() =>
        {
            var rootNo = RootNo(rootId);
            return rootNo is null ? (0L, Array.Empty<ItemState>()) : _reads.LiveFlaggedSummary(rootNo.Value, flags, limit);
        });

    public bool HasMultipleNames(string rootId, string objectId) => InStore(() =>
    {
        var rootNo = RootNo(rootId);
        return rootNo is not null && StoreSchema.Scalar(_connection, null,
            "SELECT 1 FROM file_state WHERE root_no=$r AND object_id=$o LIMIT 1 OFFSET 1;",
            ("$r", rootNo.Value), ("$o", objectId)) is not null;
    });

    public ItemState? ByObject(string rootId, string objectId) => InStore(() =>
    {
        var rootNo = RootNo(rootId);
        return rootNo is null ? null : _reads.ByObject(rootNo.Value, objectId);
    });

    public IReadOnlyList<PairDef> Pairs() => InStore(() =>
    {
        using var command = StoreSchema.Command(_connection, null, "SELECT def_json FROM pair ORDER BY pair_id;");
        var pairs = new List<PairDef>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) pairs.Add(Deserialize(reader.GetString(0)));
        return pairs;
    });

    public PairDef? Pair(string pairId) => InStore(() => PairRecord(pairId));

    public void SavePair(PairDef pair) => InStore(() =>
    {
        var previous = PairRecord(pair.PairId);
        using var transaction = _connection.BeginTransaction();
        var rootNoA = RootNoCreate(pair.A.RootId, transaction);
        var rootNoB = RootNoCreate(pair.B.RootId, transaction);
        StoreSchema.Execute(_connection, transaction,
            "INSERT INTO pair(pair_id, def_json, first_sync_done) VALUES($id, $json, 0) " +
            "ON CONFLICT(pair_id) DO UPDATE SET def_json=excluded.def_json;",
            ("$id", pair.PairId), ("$json", JsonSerializer.Serialize(pair, RowCodec.Json)));
        var pairNo = (long)StoreSchema.Scalar(_connection, transaction,
            "SELECT pair_no FROM pair WHERE pair_id=$id;", ("$id", pair.PairId))!;
        // Scope changes invalidate coverage, not retained rows. A root change can also release an
        // auto-excluded nested area in another pair, so conservatively rescan all sides in that case.
        if (previous is null || previous.A != pair.A || previous.B != pair.B)
            StoreSchema.Execute(_connection, transaction, "UPDATE root SET cursor=NULL;");
        else if (previous.Excluded.Any(path => !pair.Excluded.Any(parent => PairPath.IsSelfOrUnder(path, parent))) ||
            previous.IgnoreRules.Except(pair.IgnoreRules, StringComparer.OrdinalIgnoreCase).Any())
            StoreSchema.Execute(_connection, transaction,
                "UPDATE root SET cursor=NULL WHERE root_no=$a OR root_no=$b;", ("$a", rootNoA), ("$b", rootNoB));
        transaction.Commit();
        _rootNo[pair.A.RootId] = rootNoA;
        _rootNo[pair.B.RootId] = rootNoB;
        _pairNo[pair.PairId] = pairNo;
        _rootSides.Clear();
    });

    public IReadOnlyList<Baseline> Baselines(string pairId, IEnumerable<string>? objectKeys = null) => InStore(() =>
    {
        var pairNo = PairNo(pairId);
        return pairNo is null ? Array.Empty<Baseline>() : _points.Read(pairId, pairNo.Value, objectKeys);
    });

    public IReadOnlyList<Baseline> BaselinesAt(string pairId, Side side, IEnumerable<string> paths) => InStore(() =>
    {
        var pairNo = PairNo(pairId);
        return pairNo is null ? Array.Empty<Baseline>() : _points.At(pairId, pairNo.Value, side, paths);
    });

    public IReadOnlyList<Baseline> BaselinesTouching(string pairId, Side side, IEnumerable<string> objectIds,
        IEnumerable<string> pathPrefixes) => InStore(() =>
    {
        var pairNo = PairNo(pairId);
        return pairNo is null
            ? Array.Empty<Baseline>()
            : _points.Touching(pairId, pairNo.Value, side, objectIds, pathPrefixes);
    });

    public void CommitResult(string pairId, long gen, SyncAction action, ActionResult result) =>
        CommitResults(pairId, gen, [(action, result)]);

    /// <summary>Commits the results in order in one transaction; any failure rolls back all of them.</summary>
    public void CommitResults(string pairId, long gen, IReadOnlyList<(SyncAction Action, ActionResult Result)> results) =>
        InStore(() =>
    {
        foreach (var (action, result) in results)
        {
            if (!string.Equals(result.ActionId, action.ActionId, StringComparison.Ordinal))
                throw new ArgumentException("A result must carry its action's id.", nameof(results));
            if (action.Kind == ActionKind.Hold) throw new InvalidOperationException("A Hold has no committed result.");
        }
        var pairNo = RequirePair(pairId);
        var pair = PairRecord(pairId) ?? throw new InvalidOperationException($"Unknown pair {pairId}");
        var rootNoA = RootNo(pair.A.RootId) ?? throw new InvalidOperationException($"Unknown root {pair.A.RootId}");
        var rootNoB = RootNo(pair.B.RootId) ?? throw new InvalidOperationException($"Unknown root {pair.B.RootId}");
        var captures = new Dictionary<string, Notice>(StringComparer.Ordinal);
        foreach (var (action, result) in results.Where(row => row.Result.Quarantined))
        {
            var heldNotice = QuarantineFailure.Notice(pairId, action, result.Reason!);
            ItemState? Retained(ItemState? item, long root) => item is null ? null :
                _reads.AtKey(root, item.Path, null).FirstOrDefault().Item ?? item;
            captures[action.ActionId] = CaptureDefenderNotice(heldNotice with { Quarantine = heldNotice.Quarantine! with
            {
                Source = Retained(action.Source, action.Target == Side.A ? rootNoB : rootNoA),
                ExpectedTarget = Retained(action.ExpectedTarget, action.Target == Side.A ? rootNoA : rootNoB)
            } }, pair);
        }
        var now = NowMs();
        using var transaction = _connection.BeginTransaction();
        foreach (var (action, result) in results)
        {
            // Read durable intent metadata before Commit removes a resolved row; a caller may hold a pre-include action.
            var suppressBaseline = action.SuppressReconciledBaseline || StoreSchema.Scalar(_connection, transaction,
                "SELECT COALESCE(json_extract(action_json,'$.SuppressReconciledBaseline'),0) " +
                "FROM pending_effect WHERE pair_no=$p AND action_id=$id;",
                ("$p", pairNo), ("$id", action.ActionId)) is long flag && flag != 0;
            var retainedAction = suppressBaseline ? action with { SuppressReconciledBaseline = true } : action;
            _intents.Commit(transaction, pairNo, retainedAction, result.Quarantined && action.ProviderEffect is not null ? Outcome.Uncertain : result.ProviderNameRefused ? Outcome.Skipped : result.Outcome, now);
            if (SyncDirectDownloadCheckpoint.NoticeFor(pairId, action, result) is { } notice)
                _notices.Add(transaction, pairNo, notice, now);
            if (result.Outcome == Outcome.Skipped && result.ForgetAbsentDownloadBaseline)
                _points.CommitDone(transaction, pairNo, pairId, gen, rootNoA, rootNoB,
                    action with { Kind = ActionKind.Forget }, result,
                    (rootNo, objectId) => _points.HasBaseline(transaction, RootSides(rootNo, transaction), objectId));

            if (result.Recovery is { } recovery)
                _recoveries.Insert(transaction, pairNo, pairId, action, recovery, now, _settings.RecoveryRetention);
            if (result.Quarantined)
            {
                foreach (var (item, root) in new[] { (action.Source, action.Target == Side.A ? rootNoB : rootNoA),
                    (action.ExpectedTarget, action.Target == Side.A ? rootNoA : rootNoB) })
                    if (item is not null) _files.HoldQuarantine(transaction, root, item);
                // Retain missing observed entities; a same-path replacement keeps its own metadata and is
                // conservatively held too. The notice protects the exact path through LinkExclusions.
                _notices.Add(transaction, pairNo, captures[action.ActionId], now);
            }
            else if (result.ProviderNameRefused)
            {
                var source = action.Source ?? throw new InvalidOperationException("A name refusal needs its source item.");
                if (!_files.MarkProviderNameRefused(transaction, action.Target == Side.A ? rootNoB : rootNoA, source))
                    _notices.Add(transaction, pairNo, new Notice(pairId, action.Rule, NoticeKind.Info, source.Path,
                        "The provider refused this name, but its source item is missing or changed. " +
                        "No item was marked ignored; the current item will be reconsidered on the next sync.",
                        "provider-name-refused-source-changed"), now);
            }
            else if (result.Outcome == Outcome.Done)
            {
                if (action.ExpectedTarget is { } oldStorageItem &&
                    (action.Kind == ActionKind.Delete ||
                     (action.Target == Side.A ? result.NewA : result.NewB) is { } newStorageItem && newStorageItem.ObjectId != oldStorageItem.ObjectId))
                    RemoveStorageEntity(transaction, (action.Target == Side.A ? pair.A : pair.B).RootId, oldStorageItem.ObjectId);
                QueueConfirmedStorageReceipt(transaction, pair, action, result);
                RebindDefenderGrant(transaction, pair, action, result);
                if (action.ConflictRestore && (action.Target == Side.A ? result.NewA : result.NewB) is { } restoredItem)
                    StoreSchema.Execute(_connection, transaction,
                        "INSERT INTO choice_restore_receipt(pair_no,action_id,item_json) VALUES($p,$id,$json) " +
                        "ON CONFLICT(pair_no,action_id) DO UPDATE SET item_json=excluded.item_json;",
                        ("$p", pairNo), ("$id", action.ActionId),
                        ("$json", System.Text.Json.JsonSerializer.Serialize(restoredItem, RowCodec.Json)));
                if (action.Kind == ActionKind.Update)
                    _notices.CommitWinner(transaction, pairNo, pairId, action,
                        action.Target == Side.A ? result.NewA : result.NewB);
                if (action.ResolvedNoticeId is { } resolvedNotice &&
                    _notices.ById(pairNo, resolvedNotice, transaction) is { Closed: false })
                {
                    if (action.Kind == ActionKind.Update && result.Recovery is not null &&
                        action.ExpectedTarget is { } displaced &&
                        (action.Target == Side.A ? result.NewA : result.NewB) is { } restored)
                        _notices.Add(transaction, pairNo, new Notice(pairId, action.Rule, NoticeKind.AutoConflict,
                            action.Path, "The other version was restored. The displaced version is retained; use it again, save it as a copy, or dismiss this notice.",
                            "retained-version-restored")
                        { RetainedVersion = new RetainedNoticeVersion(action.ActionId, action.Target, displaced, restored) }, now);
                    _notices.CloseResolved(transaction, pairNo, resolvedNotice,
                        action.Kind == ActionKind.CopyNew ? "save-copy" : "use-other");
                }
                if (action.Kind == ActionKind.Delete && action.ExpectedTarget is { Kind: ItemKind.Folder } folder)
                    _files.RetireDeletedFolder(transaction, action.Target == Side.A ? rootNoA : rootNoB, folder);
                if (!suppressBaseline)
                    _points.CommitDone(transaction, pairNo, pairId, gen, rootNoA, rootNoB, action, result,
                        (rootNo, objectId) => _points.HasBaseline(transaction, RootSides(rootNo, transaction), objectId));
                if (action.Kind is ActionKind.CopyNew or ActionKind.Update or ActionKind.Move or ActionKind.MkDir or ActionKind.KeepBoth &&
                    (action.Target == Side.A ? result.NewA : result.NewB) is { } written)
                    _files.ClearProviderNameRefused(transaction, action.Target == Side.A ? rootNoA : rootNoB,
                        written, action.ExpectedTarget);

            }
        }
        transaction.Commit();
        CleanupStorageItems();
    });

    public void RecordIntent(string pairId, SyncAction action) => RecordIntents(pairId, [action]);

    /// <summary>Records the intents as started in one transaction.</summary>
    public void RecordIntents(string pairId, IReadOnlyList<SyncAction> actions) => InStore(() =>
    {
        var pairNo = RequirePair(pairId);
        var now = NowMs();
        using var transaction = _connection.BeginTransaction();
        foreach (var action in actions) _intents.Record(transaction, pairNo, action, now);
        transaction.Commit();
    });

    public IReadOnlyList<SyncAction> UnresolvedIntents(string pairId) => InStore(() =>
    {
        var pairNo = PairNo(pairId);
        return pairNo is null ? new List<SyncAction>() : _intents.Unresolved(pairNo.Value);
    });

    public void AddNotices(IEnumerable<Notice> notices) => InStore(() =>
    {
        var captured = notices.Select(notice => notice.Quarantine is null ? notice :
            CaptureDefenderNotice(notice, PairRecord(notice.PairId) ?? throw new InvalidOperationException($"Unknown pair {notice.PairId}"))).ToArray();
        using var transaction = _connection.BeginTransaction();
        var now = NowMs();
        foreach (var notice in captured)
            _notices.Add(transaction, RequirePair(notice.PairId), notice, now);
        transaction.Commit();
    });

    public IReadOnlyList<Notice> OpenNotices(string? pairId) => InStore(() =>
    {
        long? pairNo = null;
        if (pairId is not null)
        {
            pairNo = PairNo(pairId);
            if (pairNo is null) return new List<Notice>();
        }
        return _notices.Open(pairNo);
    });

    public IReadOnlyList<RecoveryRecord> Recoveries(string pairId) => InStore(() =>
    {
        var pairNo = PairNo(pairId);
        return pairNo is null ? new List<RecoveryRecord>() : _recoveries.Read(pairNo.Value, pairId);
    });

    public void CloseNotice(string noticeId, string? choice) => InStore(() => _notices.Close(noticeId, choice));

    public void RecordNoticeChoiceFailure(Notice original, string failure) => InStore(() =>
    {
        using var transaction = _connection.BeginTransaction();
        _notices.RecordChoiceFailure(transaction, RequirePair(original.PairId), original, failure);
        transaction.Commit();
    });

    public void CloseNoticeFamily(string pairId, string reasonPrefix, string? exceptReason = null) => InStore(() =>
    {
        if (PairNo(pairId) is { } pairNo) _notices.CloseFamily(pairNo, reasonPrefix, exceptReason);
    });

    public void RecordRunSummary(RunSummary summary) => InStore(() =>
        _runHistory.Record(summary, NowMs(), _settings.HistoryRetention, _settings.RunSummaryMaximumRows));

    public IReadOnlyList<RunSummary> ReadRunHistory(string? pairId = null, int limit = 50, long before = 0) =>
        InStore(() => _runHistory.Read(pairId, limit, before));

    public IReadOnlyList<RunSummary> ReadRunHistoryAfter(long after, int limit = 50) =>
        InStore(() => _runHistory.ReadAfter(after, limit));

    public IReadOnlyList<RunSummary> ReadRunHistoryPage(string? pairId, int limit, int offset) =>
        InStore(() => _runHistory.Page(pairId, limit, offset));

    public RunSummary? RunSummaryById(string runId) => InStore(() => _runHistory.ById(runId));

    public void StoreDeletionEvidence(string pairId, string rootId, IReadOnlyList<DeletionEvidenceRecord> results) =>
        InStore(() =>
    {
        if (results.Count == 0) return;
        var rootNo = RootNo(rootId) ?? throw new InvalidOperationException($"Unknown root {rootId}");
        _deletionEvidence.Store(RequirePair(pairId), rootNo, results, NowMs());
    });

    public IReadOnlyList<DeletionEvidenceRecord> DeletionEvidenceRows(string pairId, string rootId) =>
        InStore(() => _deletionEvidence.ReadAll(RequirePair(pairId),
            RootNo(rootId) ?? throw new InvalidOperationException($"Unknown root {rootId}")));

    public void RemoveDeletionEvidence(string pairId, string rootId, IReadOnlyList<string> paths) =>
        InStore(() => _deletionEvidence.Remove(RequirePair(pairId),
            RootNo(rootId) ?? throw new InvalidOperationException($"Unknown root {rootId}"), paths));

    public void Prune() => InStore(() =>
    {
        var cutoff = NowMs() - (long)_settings.HistoryRetention.TotalMilliseconds;
        using var transaction = _connection.BeginTransaction();
        var pin = Pin(transaction);
        StoreSchema.Execute(_connection, transaction,
            "DELETE FROM generation WHERE finished IS NOT NULL AND finished < $cutoff AND gen < $pin;",
            ("$cutoff", cutoff), ("$pin", pin));
        StoreSchema.Execute(_connection, transaction,
            "DELETE FROM notice WHERE state <> 'open' AND NOT (reason_key='antivirus-quarantine' AND COALESCE(choice,'')='quarantine-ignore') AND created < $cutoff;", ("$cutoff", cutoff));
        // Only fully resolved deferrals age out: proceeded, released, or the entity is gone on both sides (no live row
        // in any root and no baseline). Pending and Keep-held rows of a live entity are never pruned, on any root type.
        StoreSchema.Execute(_connection, transaction,
            "DELETE FROM defender_deferral WHERE object_id IN (SELECT q.object_id FROM defender_deferral q " +
            "WHERE q.updated<$cutoff AND (q.consent IN ($proceed,$released) OR (" +
            "NOT EXISTS (SELECT 1 FROM root r CROSS JOIN file_state f INDEXED BY fs_obj ON f.root_no=r.root_no AND f.object_id=q.object_id) AND " +
            "NOT EXISTS (SELECT 1 FROM pair p CROSS JOIN sync_point s INDEXED BY sp_a_obj ON s.pair_no=p.pair_no AND s.a_object_id=q.object_id) AND " +
            "NOT EXISTS (SELECT 1 FROM pair p CROSS JOIN sync_point s INDEXED BY sp_b_obj ON s.pair_no=p.pair_no AND s.b_object_id=q.object_id))) " +
            "ORDER BY q.updated LIMIT 512);",
            ("$cutoff", (_clock.GetUtcNow() - _settings.HistoryRetention).UtcTicks),
            ("$proceed", (long)DefenderConsent.Proceed), ("$released", (long)DefenderConsent.Released));
        StoreSchema.Execute(_connection, transaction,
            "DELETE FROM choice_merge WHERE NOT EXISTS (SELECT 1 FROM notice n WHERE n.notice_id=choice_merge.notice_id AND n.state='open');");
        _runHistory.Prune(transaction, NowMs(), _settings.HistoryRetention, _settings.RunSummaryMaximumRows);
        StoreSchema.Execute(_connection, transaction, "DROP TABLE IF EXISTS used;");
        StoreSchema.Execute(_connection, transaction,
            "CREATE TEMP TABLE used AS SELECT DISTINCT dir_no FROM file_state " +
            "UNION SELECT a_dir FROM sync_point UNION SELECT b_dir FROM sync_point WHERE b_dir IS NOT NULL " +
            "UNION SELECT dir_no FROM tombstone;");
        StoreSchema.Execute(_connection, transaction, "DELETE FROM dir WHERE dir_no NOT IN (SELECT dir_no FROM used);");
        StoreSchema.Execute(_connection, transaction, "DROP TABLE used;");
        transaction.Commit();
        _dir.ClearCache();
    });

    /// <summary>Rows changed on this store's connection so far; a probe letting tests prove that a write did nothing.</summary>
    internal long ChangeCount() =>
        InStore(() => (long)StoreSchema.Scalar(_connection, null, "SELECT total_changes();")!);

    /// <summary>Bytes held by live (non-free) pages; a probe for the size projection test.</summary>
    internal long LiveBytes() => InStore(() =>
        ((long)StoreSchema.Scalar(_connection, null, "PRAGMA page_count;")! -
         (long)StoreSchema.Scalar(_connection, null, "PRAGMA freelist_count;")!) *
        (long)StoreSchema.Scalar(_connection, null, "PRAGMA page_size;")!);

    /// <summary>The connection's PRAGMA synchronous (1 NORMAL, 2 FULL); a probe proving the durability setting applies.</summary>
    internal long SynchronousMode() =>
        InStore(() => (long)StoreSchema.Scalar(_connection, null, "PRAGMA synchronous;")!);

    /// <summary>Intents stored for all pairs; a probe proving resolved intents are deleted .</summary>
    internal long IntentRows() => InStore(() => _intents.Count());

    /// <summary>The query plans of the hot case-insensitive lookups; a probe proving they are indexed.</summary>
    internal IReadOnlyList<string> HotQueryPlans() => InStore(() => _reads.Plans().Concat(_points.Plans()).ToArray());

    /// <summary>The query plans of run-history paging; a probe proving the sequence order and pair index are used.</summary>
    internal IReadOnlyList<string> RunHistoryQueryPlans() => InStore(() => _runHistory.Plans());

    /// <summary>The deletion tombstones recorded for a root; a probe, since no interface member reads them yet.</summary>
    internal IReadOnlyList<(string Path, string ObjectId, DateTimeOffset DeletedAt)> Tombstones(string rootId) => InStore(() =>
    {
        var rootNo = RootNo(rootId);
        return rootNo is null
            ? new List<(string, string, DateTimeOffset)>()
            : _reads.Tombstones(rootNo.Value);
    });

    private long Pin(SqliteTransaction transaction)
    {
        var unfinished = StoreSchema.Scalar(_connection, transaction,
            "SELECT MIN(gen) FROM generation WHERE finished IS NULL;");
        var newest = StoreSchema.Scalar(_connection, transaction,
            "SELECT MIN(newest) FROM (SELECT MAX(gen) AS newest FROM sync_point GROUP BY pair_no);");
        var candidates = new[] { unfinished, newest }.OfType<long>().ToArray();
        if (candidates.Length > 0) return candidates.Min();
        return (StoreSchema.Scalar(_connection, transaction, "SELECT MAX(gen) FROM generation;") is long highest
            ? highest
            : 0) + 1;
    }

    // Decision 59: a link row was written; every pair this root belongs to forgets its baselines at or under the
    // link on that side, in the same transaction.
    private void ForgetUnderLink(SqliteTransaction transaction, long rootNo, string path)
    {
        foreach (var (pairNo, side) in RootSides(rootNo, transaction))
        {
            var json = (string?)StoreSchema.Scalar(_connection, transaction,
                "SELECT def_json FROM pair WHERE pair_no=$p;", ("$p", pairNo));
            if (json is null) continue;
            var pair = Deserialize(json);
            if (!_rootNo.TryGetValue(pair.A.RootId, out var rootNoA) || !_rootNo.TryGetValue(pair.B.RootId, out var rootNoB))
                continue;
            _points.ForgetUnder(transaction, pairNo, side, path, rootNoA, rootNoB,
                (root, objectId) => _points.HasBaseline(transaction, RootSides(root, transaction), objectId));
        }
    }

    // The (pair, side) slots a root fills, from the pair definitions; cached until the next SavePair. SavePair
    // creates both roots, so every root a pair names is in _rootNo.
    private (long PairNo, Side Side)[] RootSides(long rootNo, SqliteTransaction? transaction)
    {
        if (_rootSides.TryGetValue(rootNo, out var cached)) return cached;
        var sides = new List<(long, Side)>();
        using (var command = StoreSchema.Command(_connection, transaction, "SELECT pair_no, def_json FROM pair;"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var pair = Deserialize(reader.GetString(1));
                if (_rootNo.TryGetValue(pair.A.RootId, out var a) && a == rootNo) sides.Add((reader.GetInt64(0), Side.A));
                if (_rootNo.TryGetValue(pair.B.RootId, out var b) && b == rootNo) sides.Add((reader.GetInt64(0), Side.B));
            }
        return _rootSides[rootNo] = sides.ToArray();
    }

    private void RequireOpenGen(SqliteTransaction? transaction, long gen, long rootNo)
    {
        using var command = StoreSchema.Command(_connection, transaction,
            "SELECT root_no, finished FROM generation WHERE gen=$g;");
        command.Parameters.AddWithValue("$g", gen);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException($"Generation {gen} is unknown");
        if (reader.GetInt64(0) != rootNo) throw new InvalidOperationException($"Generation {gen} belongs to another root");
        if (!reader.IsDBNull(1)) throw new InvalidOperationException($"Generation {gen} is already finished");
    }

    private long RootNoCreate(string rootId, SqliteTransaction transaction)
    {
        if (_rootNo.TryGetValue(rootId, out var cached)) return cached;
        StoreSchema.Execute(_connection, transaction, "INSERT INTO root(root_id) VALUES($id) ON CONFLICT(root_id) DO NOTHING;",
            ("$id", rootId));
        return (long)StoreSchema.Scalar(_connection, transaction, "SELECT root_no FROM root WHERE root_id=$id;",
            ("$id", rootId))!;
    }

    private long? RootNo(string rootId)
    {
        if (_rootNo.TryGetValue(rootId, out var cached)) return cached;
        if (StoreSchema.Scalar(_connection, null, "SELECT root_no FROM root WHERE root_id=$id;", ("$id", rootId))
            is not long number) return null;
        _rootNo[rootId] = number;
        return number;
    }

    private long? PairNo(string pairId)
    {
        if (_pairNo.TryGetValue(pairId, out var cached)) return cached;
        if (StoreSchema.Scalar(_connection, null, "SELECT pair_no FROM pair WHERE pair_id=$id;", ("$id", pairId))
            is not long number) return null;
        _pairNo[pairId] = number;
        return number;
    }

    private PairDef? PairRecord(string pairId) =>
        StoreSchema.Scalar(_connection, null, "SELECT def_json FROM pair WHERE pair_id=$id;", ("$id", pairId)) is string json
            ? Deserialize(json)
            : null;

    private long RequirePair(string pairId) =>
        PairNo(pairId) ?? throw new InvalidOperationException($"Unknown pair {pairId}");

    private void Load()
    {
        using (var command = StoreSchema.Command(_connection, null, "SELECT root_id, root_no FROM root;"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) _rootNo[reader.GetString(0)] = reader.GetInt64(1);
        using (var command = StoreSchema.Command(_connection, null, "SELECT pair_id, pair_no FROM pair;"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) _pairNo[reader.GetString(0)] = reader.GetInt64(1);
    }

    private void InStore(Action body, [System.Runtime.CompilerServices.CallerMemberName] string operationName = "") => InStore<object?>(() =>
    {
        body();
        return null;
    }, operationName);

    private T InStore<T>(Func<T> body, [System.Runtime.CompilerServices.CallerMemberName] string operationName = "")
    {
        // One tracked operation covers both the lock wait and the body (RQ-090): a store call that waits on the lock
        // is exactly as slow as one that runs, and both are named the same way.
        using var operation = RunOperations.Begin(RunOperationKind.Store, "store " + operationName);
        lock (_gate)
        {
            try
            {
                return body();
            }
            catch (SqliteException exception) when (StoreSchema.IsDamaged(exception))
            {
                _dir.ClearCache();
                throw new StateDamagedException($"v2 state is damaged: {exception.Message}", exception);
            }
            catch
            {
                _dir.ClearCache();
                throw;
            }
        }
    }

    private long NowMs() => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    private static PairDef Deserialize(string json) => JsonSerializer.Deserialize<PairDef>(json, RowCodec.Json)!;

    private static void Validate(V2Settings settings)
    {
        if (settings.StoreBatchRows <= 0)
            throw new ArgumentOutOfRangeException(nameof(settings), settings.StoreBatchRows, "StoreBatchRows must be positive.");
        if (settings.StoreCacheMb <= 0)
            throw new ArgumentOutOfRangeException(nameof(settings), settings.StoreCacheMb, "StoreCacheMb must be positive.");
        if (settings.StoreBusyTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(settings), settings.StoreBusyTimeout, "StoreBusyTimeout must be positive.");
        if (settings.HistoryRetention <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(settings), settings.HistoryRetention, "HistoryRetention must be positive.");
        if (settings.RecoveryRetention <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(settings), settings.RecoveryRetention, "RecoveryRetention must be positive.");
        if (settings.RunSummaryMaximumRows <= 0)
            throw new ArgumentOutOfRangeException(nameof(settings), settings.RunSummaryMaximumRows,
                "RunSummaryMaximumRows must be positive.");
    }
}

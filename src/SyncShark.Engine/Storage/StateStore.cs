using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>The engine database at &lt;ProductRoot&gt;/engine/state.db (sections 2.6, 3.1). One write connection under one
/// lock; ScanRows and ScanRenames use their own read-only connection.</summary>
public sealed class StateStore : IStateStore
{
    /// <inheritdoc />
    public (PairConfig Pair, long Generation)? ReadPairExecution(string pairId)
    {
        ArgumentNullException.ThrowIfNull(pairId);
        lock (_lock)
            return ReadOne<(PairConfig Pair, long Generation)?>(null,
                _legacy ? StateSchema.LegacyPairFence : StateSchema.PairFence,
                static reader => (StateEncoding.DecodePair(reader.GetString(StateSchema.PairFenceDefJson)),
                    reader.GetInt64(StateSchema.PairFenceExecutionGeneration)), (StateSchema.ParamPair, pairId));
    }

    /// <inheritdoc />
    public ItemRetryCandidate? ReadItemRetryForSubject(string pairId, long expectedGeneration,
        string path, IssueSubject subject)
    {
        ArgumentNullException.ThrowIfNull(pairId);
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(subject);
        var key = PathNames.KeyFor(Side.Local, path);
        var local = IssueSubject.DistinctItems(subject.Items.Where(item => item.Side == Side.Local));
        if (local.Length != 1 || !PathNames.Comparer.Equals(PathNames.KeyFor(Side.Local, local[0].Path), key))
            throw new ArgumentException(null, nameof(subject));
        lock (_lock)
        {
            if (_legacy) throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
            using var transaction = _connection.BeginTransaction(deferred: true);
            var pair = RequireGeneration(transaction, pairId, expectedGeneration);
            var ids = ReadPage(transaction, StateSchema.ItemEpisodeBySubject,
                static reader => reader.GetInt64(StateSchema.EpisodeBySubjectIssueId),
                (StateSchema.ParamPairRow, pair), (StateSchema.ParamSide, (int)Side.Local),
                (StateSchema.ParamPath, key), (StateSchema.ParamIdentity, ChangeTable.Identity(Side.Local, local[0].Id)))
                .Distinct().ToArray();
            if (ids.Length == 0) return null;
            if (ids.Length != 1) throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
            var candidate = IssueTable.ReadItemRetryState(_connection, transaction, pairId, pair, ids)[ids[0]];
            if (candidate is null || candidate.Token.PairGeneration != expectedGeneration ||
                !candidate.Token.Subject.HasSameItems(subject))
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
            return candidate;
        }
    }

    /// <inheritdoc />
    public long ReadWorkWatermark()
    {
        lock (_lock)
        {
            if (_legacy) throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
            return ReadOne(null, StateSchema.WorkWatermark,
                static reader => reader.GetInt64(StateSchema.FirstColumn));
        }
    }

    /// <inheritdoc />
    public void SaveImportedPair(PairConfig pair, bool legacyPaused, IEnumerable<SyncedEntry>? baselines = null) => Write(() =>
    {
        ArgumentNullException.ThrowIfNull(pair);
        if (pair.Roots is not null || legacyPaused && pair.Enabled)
            throw new ArgumentException(null, nameof(pair));
        using var transaction = _connection.BeginTransaction();
        RequireOne(Execute(_connection, transaction, StateSchema.InsertPair,
            (StateSchema.ParamPair, pair.PairId), (StateSchema.ParamDef, JsonSerializer.Serialize(pair, PairEncoding))));
        _changes.SeedPairSources(transaction, pair.PairId);
        if (baselines is not null)
            foreach (var entry in baselines)
                StateCommitter.Insert(_connection, transaction, pair.PairId, entry, null);
        if (legacyPaused)
            IssueTable.Raise(_connection, transaction, pair.PairId, IssueKind.Info, TextKey.InfoPairPausedInOldApp,
                string.Empty, new IssueSubject([], null), DateTimeOffset.UtcNow);
        transaction.Commit();
    });

    /// <inheritdoc />
    public void MergeDriveEvidence(string pairId, long expectedGeneration, DriveEvidence acquired) => Write(() =>
    {
        ArgumentNullException.ThrowIfNull(acquired);
        using var transaction = _connection.BeginTransaction();
        var current = ReadPairSnapshot(transaction, pairId, expectedGeneration);
        var merged = DriveEvidence.After(current.Pair.Drive, acquired);
        if (merged == current.Pair.Drive) return;
        RequireOne(Execute(_connection, transaction, StateSchema.UpdatePairDefinition,
            (StateSchema.ParamPairRow, current.Row), (StateSchema.ParamGeneration, expectedGeneration),
            (StateSchema.ParamDefinition, JsonSerializer.Serialize(current.Pair with { Drive = merged }, PairEncoding))));
        transaction.Commit();
    });

    /// <inheritdoc />
    public void ReconcileRootIdentity(string pairId, long expectedGeneration, RootBinding? expectedBinding,
        Issue? expectedIssue, StateChange issueOnlyChange) => Write(() =>
    {
        RequireIssueOnly(issueOnlyChange);
        if (issueOnlyChange.FirstRunAcknowledged.Count != 0 ||
            issueOnlyChange.Raise.Any(issue => issue.Kind != IssueKind.RootIdentity || issue.Path != string.Empty) ||
            issueOnlyChange.Close.Any(close => close.Path != string.Empty ||
                close.Kinds.Any(kind => kind != IssueKind.RootIdentity)) ||
            expectedIssue is not null && (expectedIssue.Kind != IssueKind.RootIdentity || expectedIssue.Path != string.Empty))
            throw new ArgumentException(null, nameof(issueOnlyChange));
        using var transaction = _connection.BeginTransaction();
        var current = ReadPairSnapshot(transaction, pairId, expectedGeneration);
        if (current.Pair.Roots != expectedBinding)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
        var stored = ReadOne(transaction, StateSchema.SelectIssueByPathKind, IssueTable.ReadIssue,
            (StateSchema.ParamPair, pairId), (StateSchema.ParamPath, string.Empty),
            (StateSchema.ParamKind, (int)IssueKind.RootIdentity));
        if (expectedIssue is null ? stored is not null : stored is null || !stored.Matches(expectedIssue))
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
        if (stored is null && issueOnlyChange.Raise.Count == 0) return;
        if (stored is not null && issueOnlyChange.Close.Any(close => close.Kinds.Contains(IssueKind.RootIdentity)))
        {
            if (!IssueTable.CloseExact(_connection, transaction, pairId, stored))
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
        }
        StateCommitter.Commit(_connection, transaction, _changes, pairId,
            issueOnlyChange with { Close = [] }, DateTimeOffset.UtcNow, narrowRecordUpdates: true, raised: null);
        transaction.Commit();
    });

    private (long Row, PairConfig Pair) ReadPairSnapshot(SqliteTransaction transaction, string pairId, long generation)
    {
        ArgumentNullException.ThrowIfNull(pairId);
        if (_legacy) throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
        using var command = StorageCommand.Create(_connection, transaction, StateSchema.PairFence,
            (StateSchema.ParamPair, pairId));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt64(StateSchema.PairFenceExecutionGeneration) != generation)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
        return (reader.GetInt64(StateSchema.PairFenceId),
            StateEncoding.DecodePair(reader.GetString(StateSchema.PairFenceDefJson)));
    }

    private static readonly JsonSerializerOptions PairEncoding = new()
    {
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                static type =>
                {
                    if (type.Type != typeof(PairConfig)) return;
                    foreach (var property in type.Properties)
                        if (property.Name == nameof(PairConfig.IgnoredFolderKeepRules))
                            property.ShouldSerialize = static (_, value) => value is IReadOnlyList<string> { Count: > 0 };
                }
            }
        }
    };
    private static readonly IReadOnlyList<IssueKind> ClosureKinds =
        Array.AsReadOnly(new[] { IssueKind.Failure, IssueKind.Tie, IssueKind.NameClash, IssueKind.DriveChange });
    private readonly SqliteConnection _connection;
    private readonly string _path;
    private readonly bool _legacy;
    private readonly object _lock = new();
    private readonly ChangeTable _changes;
    private readonly IssueFactsPublication _issueFacts;
    private Func<Guid, string, long, IssueSubject, ItemRetryToken?, bool>? _isRegisteredItemAttempt;
    private readonly Dictionary<object, (string PairId, long Generation, Issue Notice)> _pageWaits =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(string PairId, Guid ScanId), ScanPartition> _scans = [];
    private readonly HashSet<string> _plans = new(StringComparer.Ordinal);
    private string? _scanPair;
    private bool _scanOnDisk;
    private Dictionary<string, HashSet<(string Path, IssueKind Kind)>>? _closureKeys;
    // Bounded by Repair's outstanding row-task capacity: each submitter awaits its completion.
    private List<(string PairId, StateChange Change, TaskCompletionSource Completion, bool Narrow, Exception? Failure)>? _records;
    private bool _flushing;
    private bool _disposed;
    private readonly object _queueLock = new();

    private StateStore(SqliteConnection connection, string path, bool legacy, bool writable)
    {
        _connection = connection;
        _path = path;
        _legacy = legacy;
        _changes = new ChangeTable(connection, writable);
        _issueFacts = new IssueFactsPublication(connection, writable);
    }

    /// <summary>Creates the folder and durable schema if absent; WAL, synchronous=FULL, foreign_keys=ON. It composes
    /// no item-attempt registry, so item-attempt methods refuse; production opens the validator overload.</summary>
    internal static StateStore Open(string path) => OpenStore(path, writable: true)!;

    /// <summary>Composes item-attempt validation from the owning host.</summary>
    public static StateStore Open(string path,
        Func<Guid, string, long, IssueSubject, ItemRetryToken?, bool> isRegisteredItemAttempt,
        IProgress<RunEvent>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(isRegisteredItemAttempt);
        var store = OpenStore(path, writable: true, progress)!;
        store._isRegisteredItemAttempt = isRegisteredItemAttempt;
        return store;
    }

    /// <summary>Null if the file is absent; opens read-only without creating or attaching a scan.</summary>
    public static StateStore? OpenReadOnly(string path) => OpenStore(path, writable: false);

    /// <summary>The engine database path under the product root; opening and creation stay with the caller.</summary>
    public static string DatabasePath(string productRoot) =>
        Path.Combine(productRoot, ResourceNames.EngineFolder, ResourceNames.DatabaseFile);

    private static StateStore? OpenStore(string path, bool writable, IProgress<RunEvent>? progress = null)
    {
        var full = Path.GetFullPath(path);
        if (writable) Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        else if (!new FileInfo(full).Exists) return null;
        var connection = Connect(full, writable ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly);
        try
        {
            progress?.Report(RunEvent.Stage(TextKey.StageSchema));
            var legacy = InitializeSchema(connection, writable);
            progress?.Report(RunEvent.Stage(TextKey.StageSchema, ended: true));
            return new StateStore(connection, full, legacy, writable);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Returns every saved pair, in saved order.</summary>
    public IReadOnlyList<PairConfig> Pairs()
    {
        lock (_lock)
        {
            var pairs = new List<PairConfig>();
            using var command = StorageCommand.Create(_connection, null, StateSchema.SelectPairs);
            using var reader = command.ExecuteReader();
            while (reader.Read()) pairs.Add(StateEncoding.DecodePair(reader.GetString(StateSchema.FirstColumn)));
            return pairs;
        }
    }

    /// <summary>Returns one pair, or null when the id is unknown.</summary>
    public PairConfig? Pair(string pairId) =>
        ReadMetadata(StateSchema.SelectPair, static reader => StateEncoding.DecodePair(reader.GetString(StateSchema.FirstColumn)),
            (StateSchema.ParamPair, pairId));

    public void SavePair(PairConfig pair) => Write(pair, static (store, pair) =>
    {
        using var transaction = store._connection.BeginTransaction();
        var before = store.ReadOne(transaction, StateSchema.SelectPair,
            static reader => StateEncoding.DecodePair(reader.GetString(StateSchema.FirstColumn)),
            (StateSchema.ParamPair, pair.PairId));
        var replaceIdentity = before is not null && RootBinding.HistoryIdentityChanged(before, pair);
        if (replaceIdentity)
            Execute(store._connection, transaction, StateSchema.ReplaceDeletedHistoryIdentity, (StateSchema.ParamPair, pair.PairId));
        Execute(store._connection, transaction, StateSchema.UpsertPair,
            (StateSchema.ParamPair, pair.PairId), (StateSchema.ParamDef, JsonSerializer.Serialize(pair, PairEncoding)));
        store._changes.SeedPairSources(transaction, pair.PairId);
        transaction.Commit();
        if (replaceIdentity) store.ResetScan(pair.PairId);
    });

    /// <summary>Removes the pair, its scan and its rows (cascade), then reclaims pages.</summary>
    public void RemovePair(string pairId) => Write(pairId, static (store, pairId) =>
    {
        using (var transaction = store._connection.BeginTransaction())
        {
            var pair = store.RequirePairRow(transaction, pairId);
            IssueTable.DetachPairMembers(store._connection, transaction, pair);
            RequireOne(Execute(store._connection, transaction, StateSchema.DeletePair,
                (StateSchema.ParamPair, pairId)));
            transaction.Commit();
        }
        store._closureKeys?.Remove(pairId);
        store._issueFacts.Remove(pairId);
        store.InvalidateScans(pairId);
        store.Reclaim();
    });

    /// <summary>True once a completed first repair has run for the pair.</summary>
    public bool FirstRepairDone(string pairId) =>
        ReadMetadata(StateSchema.SelectFirstRepairDone, static reader => StateEncoding.ReadBool(reader, StateSchema.FirstColumn),
            (StateSchema.ParamPair, pairId));

    /// <summary>Returns the pair's last repair summary, or null when none ran.</summary>
    public RepairSummary? LastRepair(string pairId) =>
        ReadMetadata(StateSchema.SelectLastRepair, static reader => reader.IsDBNull(StateSchema.FirstColumn)
            ? null : StateEncoding.Decode<RepairSummary>(reader.GetString(StateSchema.FirstColumn)),
            (StateSchema.ParamPair, pairId));

    public void MarkRepairLogFailed(string pairId, string runId) => Write((pairId, runId), static (store, state) =>
        Execute(store._connection, null, StateSchema.MarkRepairLogFailed,
            (StateSchema.ParamPair, state.pairId), (StateSchema.ParamRunId, state.runId)));

    /// <summary>Saves progress without declaring authoritative completion.</summary>
    public void FinishRepair(string pairId, RepairSummary summary) => Write((pairId, summary), static (store, state) =>
    {
        using var transaction = store._connection.BeginTransaction();
        Execute(store._connection, transaction, StateSchema.FinishRepair,
            (StateSchema.ParamPair, state.pairId),
            (StateSchema.ParamSummary, JsonSerializer.Serialize(state.summary with { Completed = false })),
            (StateSchema.ParamCompleted, false));
        transaction.Commit();
    });

    public void ForgetAllSynced(string pairId) => Write(pairId, static (store, pairId) =>
    {
        using var transaction = store._connection.BeginTransaction();
        Execute(store._connection, transaction, StateSchema.ForgetAllSynced, (StateSchema.ParamPair, pairId));
        transaction.Commit();
    });

    /// <summary>Returns the saved admission level for the account, or null when none.</summary>
    public int? LimiterLevel(string connectionId) =>
        ReadMetadata<int?>(StateSchema.SelectLimiterLevel, static reader => (int)reader.GetInt64(StateSchema.FirstColumn),
            (StateSchema.ParamConnection, connectionId));

    public void SaveLimiterLevel(string connectionId, int level) => Write((connectionId, level), static (store, state) =>
        Execute(store._connection, null, StateSchema.UpsertLimiterLevel,
            (StateSchema.ParamConnection, state.connectionId), (StateSchema.ParamLevel, state.level)));

    public SyncedEntry? SyncedByKey(string pairId, Side side, string key)
    {
        lock (_lock)
            return ReadSynced(
                side == Side.Local ? StateSchema.SelectSyncedByLocalKey : StateSchema.SelectSyncedByDropboxKey,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamTargetKey, key));
    }

    public SyncedEntry? SyncedById(string pairId, Side side, string id)
    {
        lock (_lock)
            return ReadSynced(
                side == Side.Local ? StateSchema.SelectSyncedByLocalId : StateSchema.SelectSyncedByDropboxId,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamId, StateEncoding.IdFor(side, id)));
    }

    public IReadOnlyList<SyncedEntry> SyncedBindingsById(string pairId, Side side, string id)
    {
        lock (_lock)
        {
            using var command = StorageCommand.Create(_connection, null,
                side == Side.Local ? StateSchema.SelectSyncedByLocalId : StateSchema.SelectSyncedByDropboxId,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamId, StateEncoding.IdFor(side, id)));
            using var reader = command.ExecuteReader();
            var bindings = new List<SyncedEntry>();
            while (reader.Read()) bindings.Add(DecodeSynced(reader));
            return bindings;
        }
    }

    /// <summary>Applies one state change in a transaction, skipping when it changes nothing.</summary>
    public void Commit(string pairId, StateChange change, Action<Issue>? committedIssue = null) =>
        Commit(pairId, change, null, committedIssue);

    public void Commit(string pairId, StateChange change, WorkSelection? selection, Action<Issue>? committedIssue)
    {
        List<Issue>? raised = committedIssue is null ? null : [];
        Write((pairId, change, selection, raised), static (store, state) =>
        {
            ObjectDisposedException.ThrowIf(store._disposed, store);
            var (pairId, change, selection, raised) = state;
            if (change.Record.Count == 0 && change.StaleBindings.Count == 0 && change.ForgetBothSubtrees.Count == 0 && change.Renames.Count == 0 && change.FirstRunAcknowledged.Count == 0 &&
                change.ForgetSelf.Count == 0 && change.ForgetSubtrees.Count == 0 && change.Raise.Count == 0 &&
                !store.NeedsClosure(pairId, change.Close)) return;
            if (change.Renames.Count != 0) store.AttachScan(pairId, create: false);
            var scans = change.Renames.Count == 0 ? [] : store.ActiveScanKeys(pairId).ToArray();
            using var transaction = store._connection.BeginTransaction();
            if (selection is not null) store._changes.AdvanceBefore(transaction, pairId, selection, change);
            StateCommitter.Commit(store._connection, transaction, store._changes, pairId, change, DateTimeOffset.UtcNow,
                narrowRecordUpdates: true, raised, scans);
            transaction.Commit();
            store.UpdateClosures(pairId, change);
        });
        if (raised is not null)
            foreach (var issue in raised) committedIssue!(issue);
    }

    /// <summary>First-run Record entry: queueable file Records share the Record queue; raising changes and folder
    /// Records commit now so their committed issue reports exactly once.</summary>
    public Task CommitRecordAsync(string pairId, StateChange change, Action<Issue>? committedIssue)
    {
        var hasFolder = change.Record.Any(entry => entry.IsFolder);
        if (change.Raise.Count != 0 || hasFolder)
        {
            Commit(pairId, change, committedIssue);
            return Task.CompletedTask;
        }
        if (!QueueableRecord(change, hasFolder)) throw new ArgumentException(null, nameof(change));
        return QueueRecord(pairId, change, narrow: false);
    }

    private Task QueueRecord(string pairId, StateChange change, bool narrow)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool flush;
        lock (_queueLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            (_records ??= []).Add((pairId, change, completion, narrow, null));
            flush = !_flushing;
            if (flush) _flushing = true;
        }
        if (flush) ThreadPool.QueueUserWorkItem(static store => store.FlushRecords(), this, preferLocal: false);
        return completion.Task;
    }

    internal static void RequireQueueableRecord(StateChange change)
    {
        if (!QueueableRecord(change)) throw new ArgumentException(null, nameof(change));
    }

    private static bool QueueableAdoption(StateChange change) =>
        change.Record.Count == 0 && change.StaleBindings.Count == 0 && change.ForgetSelf.Count == 0 &&
        change.ForgetSubtrees.Count == 0 && change.ForgetBothSubtrees.Count == 0 && change.Raise.Count == 0 &&
        change.Renames is [{ Before.IsFolder: false, After.IsFolder: false, CounterpartSide: null,
            CounterpartOldPath: null, CounterpartNewPath: null }];

    private static bool QueueableRecord(StateChange change) =>
        QueueableRecord(change, change.Record.Any(entry => entry.IsFolder));

    // hasFolder is computed by the caller when it already enumerated the Records, so eligibility visits them once.
    private static bool QueueableRecord(StateChange change, bool hasFolder) =>
        change.Record.Count != 0 && !hasFolder &&
        change.Renames.Count == 0 && change.ForgetSelf.Count == 0 && change.ForgetSubtrees.Count == 0 && change.ForgetBothSubtrees.Count == 0 &&
        change.Raise.Count == 0 && !change.Close.Any(closure =>
            closure.Kinds.Any(kind => !IssueClosure.AfterHeldInSync.Contains(kind)));

    /// <inheritdoc />
    public PendingRead Pending(string pairId)
    {
        lock (_lock)
            return PairRow(null, pairId) is { } pair
                ? StateEncoding.DecodePending(PendingJson(null, pair)) : new NoPending();
    }

    private string? PendingJson(SqliteTransaction? transaction, long pair)
    {
        using var command = StorageCommand.Create(_connection, transaction, StateSchema.PendingRead,
            (StateSchema.ParamPairRow, pair));
        return command.ExecuteScalar() as string;
    }

    private string RequirePending(SqliteTransaction transaction, long pair, PendingEffect expected)
    {
        var raw = PendingJson(transaction, pair);
        if (StateEncoding.DecodePending(raw) is not CurrentPending current ||
            StateEncoding.EncodePending(current.Value) != StateEncoding.EncodePending(expected))
            throw new InvalidOperationException();
        return raw!;
    }

    private void RequirePendingSaved(SqliteTransaction transaction, string pairId, PendingEffect effect)
    {
        if (!IsCrossedLocalReplace(effect))
        {
            RequireSaved(transaction, pairId, effect.Losing.Expected, effect.Saved);
            return;
        }
        // The losing file was unbound; custody retains the distinct, still-current source binding.
        RequireSaved(transaction, pairId, effect.Losing.Expected, null);
        var saved = effect.Saved!;
        if (ReadOne(transaction, StateSchema.SelectSyncedByDropboxKey, DecodeSynced,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamTargetKey, saved.DropboxKey)) != saved ||
            ReadOne(transaction, StateSchema.SelectSyncedByDropboxId, DecodeSynced,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamId, saved.DropboxId)) != saved)
            throw new InvalidOperationException();
    }

    internal static bool IsCrossedLocalReplace(PendingEffect effect) =>
        effect.Kind == PendingKind.LocalReplace &&
        effect.Saved is { IsFolder: false } saved && saved.LocalKey != saved.DropboxKey &&
        effect.Losing.Expected is { IsFolder: false, Skip: null } local &&
        effect.Source is { IsFolder: false, Skip: null } source &&
        effect.Path == local.Path && PathNames.Same(local.Path, PathNames.LocalSpelling(source.Path)) &&
        PathNames.Same(source.Path, saved.DropboxKey) && source.Id == saved.DropboxId && local.Id != saved.LocalId;

    private void RequireSaved(SqliteTransaction transaction, string pairId, Entry expected, SyncedEntry? saved)
    {
        if (ReadBinding(StateSchema.SelectSyncedByLocalKey, StateSchema.ParamTargetKey,
                PathNames.Key(expected.Path)) != saved ||
            ReadBinding(StateSchema.SelectSyncedByLocalId, StateSchema.ParamId,
                StateEncoding.LocalId(expected.Id)) != saved)
            throw new InvalidOperationException();

        SyncedEntry? ReadBinding(string sql, string parameter, object? value)
        {
            using var command = StorageCommand.Create(_connection, transaction, sql,
                (StateSchema.ParamPair, pairId), (parameter, value));
            using var reader = command.ExecuteReader();
            return reader.Read() ? DecodeSynced(reader) : null;
        }
    }

    /// <summary>Records the pending replace; throws when one is already pending.</summary>
    public void ArmPending(string pairId, PendingEffect effect) => Write((pairId, effect), static (store, state) =>
    {
        var json = StateEncoding.EncodePending(state.effect);
        if (state.effect.Generation != 0 || state.effect.Phase != PendingPhase.Armed)
            throw new ArgumentException(null, nameof(effect));
        using var transaction = store._connection.BeginTransaction();
        var pair = store.RequirePairRow(transaction, state.pairId);
        store.RequirePendingSaved(transaction, state.pairId, state.effect);
        RequireOne(Execute(store._connection, transaction, StateSchema.PendingArm,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamNext, json)));
        transaction.Commit();
    });

    /// <summary>Returns the requested kinds, or every issue, dismissed included, in id order.</summary>
    public IReadOnlyList<Issue> Issues(string pairId, IReadOnlyList<IssueKind>? kinds = null)
    {
        lock (_lock) return IssueTable.List(_connection, pairId, kinds);
    }

    /// <inheritdoc />
    public IEnumerable<Issue> OpenIssuesUnder(string pairId, string scopeKey, IReadOnlyList<IssueKind>? kinds = null) =>
        ReadOrdered(ReadIssuesUnder(pairId, scopeKey, kinds));

    private IEnumerable<Issue> ReadIssuesUnder(string pairId, string scopeKey, IReadOnlyList<IssueKind>? kinds)
    {
        using var command = StorageCommand.Create(_connection, null,
            kinds is null ? StateSchema.SelectIssues : StateSchema.SelectIssuesByKinds,
            (StateSchema.ParamPair, pairId));
        if (kinds is not null)
            command.Parameters.AddWithValue(StateSchema.ParamKinds, JsonSerializer.Serialize(kinds));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var issue = IssueTable.ReadIssue(reader);
            if (issue.IsUnder(scopeKey)) yield return issue;
        }
    }

    /// <summary>Counts matched baseline items once through the pair index, without decoding entries.</summary>
    public long SyncedCount(string pairId)
    {
        lock (_lock)
        {
            using var command = StorageCommand.Create(_connection, null, StateSchema.CountSynced,
                (StateSchema.ParamPair, pairId));
            return (long)command.ExecuteScalar()!;
        }
    }

    /// <summary>Changes visibility of one exact notice without changing its responsibility or retry state.</summary>
    public bool SetIssueIgnored(string pairId, Issue expected, bool ignored)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            var changed = IssueTable.SetIgnored(_connection, transaction, pairId, expected, ignored);
            transaction.Commit();
            return changed;
        }
    }

    /// <summary>Counts without decoding subjects; null visibility includes ignored safety obligations.</summary>
    public IReadOnlyDictionary<IssueKind, int> IssueCounts(string pairId, bool? ignored = null)
    {
        lock (_lock) return IssueTable.Counts(_connection, pairId, ignored);
    }

    /// <summary>Reads one bounded page of open issues and the filtered total through a short-lived read-only
    /// connection, so a CLI page never queues behind or holds the host's writer connection.</summary>
    public (IReadOnlyList<(string PairId, Issue Issue)> Page, int Total) OpenIssuePage(string? pairId, string? account,
        string? search, IReadOnlyList<IssueKind>? kinds, int limit, int offset, bool ignored = false)
    {
        using var read = Connect(_path, SqliteOpenMode.ReadOnly);
        return IssueTable.OpenPage(read, pairId, account, search, kinds, limit, offset, ignored: ignored);
    }

    /// <summary>Reads every open issue of the requested kinds and its total through one short-lived read-only
    /// connection and one read transaction, so the whole traversal sees one snapshot and does not hold the host's
    /// writer connection.</summary>
    public (IReadOnlyList<(string PairId, Issue Issue)> Rows, int Total) OpenIssueSnapshot(
        IReadOnlyList<IssueKind>? kinds, int pageSize)
    {
        using var read = Connect(_path, SqliteOpenMode.ReadOnly);
        return IssueTable.PageSnapshot(read, kinds, pageSize);
    }

    /// <summary>Returns one issue only when its id belongs to the pair.</summary>
    public Issue? IssueById(string pairId, long id)
    {
        lock (_lock) return IssueTable.ById(_connection, pairId, id);
    }

    private void RequireExpectedIssue(string pairId, Issue? expected)
    {
        if (expected is not null && IssueTable.ById(_connection, pairId, expected.Id)?.Matches(expected) != true)
            throw StaleIssue(expected.Id);
    }

    private static EngineRequestException StaleIssue(long id) => new(TextKey.ResolveStale,
        (TextArg.Id, id.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>The stored holding kind for the row, excluding explicitly selected Failure retries.</summary>
    public IssueKind? HeldKind(string pairId, Row row, IReadOnlySet<long>? exceptIssues)
    {
        lock (_lock) return _issueFacts.Read(pairId).HeldKind(row, exceptIssues);
    }

    /// <summary>Whether a stored holding issue covers this key.</summary>
    public bool HeldCovers(string pairId, string key, IReadOnlySet<long>? exceptIssues)
    {
        lock (_lock) return _issueFacts.Read(pairId).Covering(key, exceptIssues) is not null;
    }

    /// <summary>Checks every covering hold except the exact notice selected for resolution.</summary>
    public bool HeldByOtherIssue(string pairId, Row row, long selectedIssueId)
    {
        lock (_lock) return _issueFacts.Read(pairId).HeldByOtherIssue(row, selectedIssueId);
    }

    /// <summary>The earliest stored holding issue covering any of the row's current or saved keys.</summary>
    public Issue? HoldingIssue(string pairId, Row row)
    {
        lock (_lock) return _issueFacts.Read(pairId).HoldingIssue(row);
    }

    /// <summary>An immutable view of the pair's recorded first-run subjects.</summary>
    public IReadOnlyDictionary<string, IssueSubject> RetainedFirstRun(string pairId)
    {
        lock (_lock) return _issueFacts.Read(pairId).Retained;
    }

    /// <summary>Replaces the pair's skipped-item issues atomically with the given set.</summary>
    public void ReplaceSkipped(string pairId, Guid scanToken,
        IEnumerable<(string Path, TextKey Cause, IssueSubject Subject)> skipped) =>
        Write(() =>
        {
            var scan = RequireScan(pairId, scanToken);
            if (scan.Record.Scope is not null) throw new InvalidOperationException();
            AttachScan(pairId, create: false);
            using var transaction = _connection.BeginTransaction();
            RequireGeneration(transaction, pairId, scan.Record.Generation);
            IssueTable.ReplaceSkipped(_connection, transaction, pairId, scan.PairRow, scan.Key, skipped, DateTimeOffset.UtcNow);
            transaction.Commit();
        });

    public void DismissIssue(string pairId, long id) => Write((pairId, id), static (store, state) =>
        IssueTable.Dismiss(store._connection, state.pairId, state.id));

    /// <summary>Creates one operation's partition without disturbing another active scan.</summary>
    public Guid BeginScan(string pairId, Guid scanId, long expectedGeneration, bool includeDeleted, string? scope = null) => Ordered(() =>
    {
        var pair = RequireGeneration(null, pairId, expectedGeneration);
        var local = ReadSource(pairId, Side.Local) ?? throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
        var dropbox = ReadSource(pairId, Side.Dropbox) ?? throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
        if (scanId == Guid.Empty || _scans.ContainsKey((pairId, scanId)) || includeDeleted && scope is not null ||
            scope is null && _scans.Any(item => item.Key.PairId == pairId && item.Value.Active && item.Value.Record.Scope is null))
            throw new InvalidOperationException();
        if (!_scans.Keys.Any(key => key.PairId == pairId)) ResetScan(pairId);
        AttachScan(pairId, create: true);
        var scan = new ScanPartition(new ScanRecord(scanId, expectedGeneration, includeDeleted, !FirstRepairDone(pairId),
            ReadWorkWatermark(), local.Epoch, local.Revision, dropbox.Epoch, dropbox.Revision,
            ReadLocalGap(pairId, local.Epoch)) { Scope = scope }, pair);
        using (var transaction = _connection.BeginTransaction())
        {
            Execute(_connection, transaction, StateSchema.CreateSkipTouched);
            Execute(_connection, transaction, StateSchema.CreateScanIssues);
            if (scope is null)
                Execute(_connection, transaction, StateSchema.CaptureScanIssues,
                    (StateSchema.ParamPairRow, pair), (StateSchema.ParamScan, scan.Key));
            if (includeDeleted)
            {
                Execute(_connection, transaction, StateSchema.CreateDeletedName);
                Execute(_connection, transaction, StateSchema.ClearDeletedNames, (StateSchema.ParamPairRow, pair));
            }
            transaction.Commit();
        }
        _scans.Add((pairId, scanId), scan);
        return scanId;
    });

    public ScanRecord ReadScan(string pairId, Guid scanToken)
    {
        lock (_lock) return RequireScan(pairId, scanToken).Record;
    }

    private ScanPartition RequireScan(string pairId, Guid scanId)
    {
        if (!_scans.TryGetValue((pairId, scanId), out var scan) || !scan.Active)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
        RequireGeneration(null, pairId, scan.Record.Generation);
        return scan;
    }

    /// <summary>Opens a token-fenced writer and tracks it until its save loop has exited.</summary>
    public IScanSink OpenScan(string pairId, Guid scanId, Side side, int batchRows)
    {
        lock (_lock)
        {
            var scan = RequireScan(pairId, scanId);
            var fresh = side == Side.Local && scan.FreshLocal;
            if (side == Side.Local) scan.FreshLocal = false;
            scan.Borrowers++;
            try
            {
                return new ScanWriter(_connection, _lock, pairId, side, () => scan.Active = false,
                    batchRows, fresh, () => { RequireScan(pairId, scanId); AttachScan(pairId, create: false); },
                    side == Side.Dropbox && scan.Record.Discovery ? scan.PairRow : null,
                    liveOverrides: side == Side.Dropbox && scan.ResumedHistory,
                    deletedDeltaPairRow: side == Side.Dropbox && scan.IncrementalHistory ? scan.PairRow : null,
                    scanId: scan.Key, release: () => scan.Borrowers--);
            }
            catch { scan.Borrowers--; throw; }
        }
    }

    // One scan ending: partition removal and paged row cleanup share the scan partition and its borrower count.
    public async Task EndScanAsync(string pairId, Guid scanId, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        ScanPartition? scan = null;
        var removed = Ordered(() =>
        {
            if (!_scans.TryGetValue((pairId, scanId), out scan)) return true;
            if (scan.Borrowers != 0) throw new InvalidOperationException();
            scan.Active = false;
            if (_scans.Keys.Any(key => key.PairId == pairId && key.ScanId != scanId)) return false;
            ResetScan(pairId);
            return true;
        });
        if (removed) return;
        Exception? failure = null;
        try
        {
            foreach (var sql in new[] { StateSchema.DeleteScanBatch, StateSchema.DeleteTouchBatch, StateSchema.DeleteScanIssueBatch })
            {
                while (Ordered(() =>
                {
                    AttachScan(pairId, create: false);
                    using var transaction = _connection.BeginTransaction();
                    var count = Execute(_connection, transaction, sql, (StateSchema.ParamPairRow, scan!.PairRow),
                        (StateSchema.ParamScan, scan.Key), (StateSchema.ParamTake, pageSize));
                    transaction.Commit();
                    return count;
                }) != 0) await Task.Yield();
            }
        }
        catch (Exception cleanup) { failure = cleanup; }
        try
        {
            Write(() =>
            {
                _scans.Remove((pairId, scanId));
                if (!_scans.Keys.Any(key => key.PairId == pairId)) ResetScan(pairId);
            });
        }
        catch (Exception cleanup)
        {
            if (failure is not null) throw new AggregateException(failure, cleanup);
            throw;
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <inheritdoc />
    public bool DeletedHistoryOwed(string pairId)
    {
        lock (_lock)
            return ReadOne(null, StateSchema.ReadDeletedHistoryOwed,
                static reader => StateEncoding.ReadBool(reader, StateSchema.FirstColumn), (StateSchema.ParamPair, pairId));
    }

    // One deleted-history restore: each block commits separately, while the cursor and count span the whole verified replay.
    /// <inheritdoc />
    public string? RestoreDeletedHistory(string pairId, Guid scanToken, Action<long>? restored = null)
    {
        var cursor = Ordered(() =>
        {
            // RequireScan already fences the scan's generation.
            return RequireScan(pairId, scanToken).Record.Discovery ? DeletedHistoryCursor(pairId) : null;
        });
        if (cursor is null) return null;
        string? after = null;
        long count = 0;
        // One block per lock hold, as CopyDeletedHistory does, so the quiet progress observer is never starved.
        while (true)
        {
            var step = Ordered<bool?>(() =>
            {
                var scan = RequireScan(pairId, scanToken);
                var pair = scan.PairRow;
                AttachScan(pairId, create: false);
                if (DeletedHistoryCursor(pairId) != cursor)
                {
                    // History withdrawn between blocks: the scan holds no restored names, as if it had gone first.
                    Execute(_connection, null, StateSchema.ClearScanDeletedNames, (StateSchema.ParamPair, pairId));
                    return null;
                }
                try
                {
                    using var transaction = _connection.BeginTransaction();
                    using var block = StorageCommand.Create(_connection, transaction, StateSchema.ReadDeletedBlock,
                        (StateSchema.ParamPairRow, pair), (StateSchema.ParamAfter, after));
                    using var reader = block.ExecuteReader();
                    if (!reader.Read())
                    {
                        using var total = StorageCommand.Create(_connection, transaction, StateSchema.DeletedHistoryCount,
                            (StateSchema.ParamPairRow, pair));
                        if ((long)total.ExecuteScalar()! != count) throw new InvalidDataException();
                        scan.ResumedHistory = true;
                        return false;
                    }
                    var last = reader.GetString(StateSchema.DeletedBlockLast);
                    using (var names = new SqliteBlob(_connection, StateSchema.DeletedBlockTable, StateSchema.DeletedBlockPayload,
                        reader.GetInt64(StateSchema.DeletedBlockRow), readOnly: true))
                        count += InsertDeletedNames(transaction, pair,
                            StateEncoding.DecodeDeletedNames(names, reader.GetInt32(StateSchema.DeletedBlockCount), last, after));
                    transaction.Commit();
                    after = last;
                    return true;
                }
                catch (InvalidDataException)
                {
                    InvalidateCorruptDeletedHistory(pairId, requireRepair: false);
                    return null;
                }
            });
            if (step is not true) return step is null ? null : cursor;
            restored?.Invoke(count);
        }
    }

    // Streams keys into DeletedNamesInsert arrays of about one maximum key, so a transfer never outgrows two decoded keys.
    private int InsertDeletedNames(SqliteTransaction transaction, long pair, IEnumerable<string> keys)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        // Keys are validated UTF-8 bound only into json_each, so relaxed escaping keeps them at their UTF-8 size.
        using var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        using var insert = StorageCommand.Create(_connection, transaction, StateSchema.DeletedNamesInsert,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamKeys, string.Empty));
        var inserted = 0;
        var pending = false;
        json.WriteStartArray();
        foreach (var key in keys)
        {
            json.WriteStringValue(key);
            pending = true;
            if (json.BytesPending + buffer.WrittenCount >= StateEncoding.MaximumDeletedKeyBytes) inserted += Flush();
        }
        if (pending) inserted += Flush();
        return inserted;

        int Flush()
        {
            json.WriteEndArray();
            json.Flush();
            insert.Parameters[StateSchema.ParamKeys].Value = System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
            buffer.ResetWrittenCount();
            json.Reset();
            json.WriteStartArray();
            pending = false;
            return insert.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    public string? ContinueDeletedHistory(string pairId, Guid scanToken) => Ordered(() =>
    {
        var scan = ReadScan(pairId, scanToken);
        RequireGeneration(null, pairId, scan.Generation);
        var cursor = DeletedHistoryCursor(pairId);
        if (!scan.Discovery) return null;
        if (cursor is not null) RequireScan(pairId, scanToken).IncrementalHistory = true;
        return cursor;
    });

    private void InvalidateCorruptDeletedHistory(string pairId, bool requireRepair = true) => Write(() =>
    {
        // The held store lock fences the generation read and the loss transition together.
        var generation = ReadPairExecution(pairId)!.Value.Generation;
        var clearScan = _scans.Any(item => item.Key.PairId == pairId && item.Value.Active && item.Value.Record.Discovery);
        if (clearScan) AttachScan(pairId, create: false);
        var change = StateChange.Empty with
        {
            Raise = [(IssueKind.Info, TextKey.DeletedHistoryCorrupt, string.Empty, new IssueSubject([], null))]
        };
        using (var transaction = _connection.BeginTransaction())
        {
            Execute(_connection, transaction, StateSchema.ClearDeletedHistory, (StateSchema.ParamPair, pairId));
            if (clearScan)
                Execute(_connection, transaction, StateSchema.ClearScanDeletedNames, (StateSchema.ParamPair, pairId));
            // Restore's current Repair reacquires history before executing rows; other readers need new debt.
            if (requireRepair) _changes.RequireRepairAfterLoss(transaction, pairId, generation);
            StateCommitter.Commit(_connection, transaction, _changes, pairId, change, DateTimeOffset.UtcNow,
                narrowRecordUpdates: true, raised: null);
            transaction.Commit();
        }
        foreach (var partition in _scans.Where(item => item.Key.PairId == pairId).Select(item => item.Value))
        {
            partition.ResumedHistory = false;
            partition.IncrementalHistory = false;
        }
        UpdateClosures(pairId, change);
    });

    private string? DeletedHistoryCursor(string pairId)
    {
        using var command = StorageCommand.Create(_connection, null, StateSchema.ReadDeletedHistoryCursor,
            (StateSchema.ParamPair, pairId));
        return command.ExecuteScalar() as string;
    }

    /// <inheritdoc />
    public void ResetDeletedHistory(string pairId, Guid scanToken, bool discardNames) => Write(() =>
    {
        var scan = RequireScan(pairId, scanToken);
        if (!scan.Record.Discovery) throw new InvalidOperationException();
        AttachScan(pairId, create: false);
        using var transaction = _connection.BeginTransaction();
        Execute(_connection, transaction, StateSchema.ClearScanDropbox, (StateSchema.ParamPair, pairId),
            (StateSchema.ParamScan, scanToken.ToByteArray()));
        if (discardNames)
        {
            scan.ResumedHistory = false;
            scan.IncrementalHistory = false;
            Execute(_connection, transaction, StateSchema.ClearScanDeletedNames, (StateSchema.ParamPair, pairId));
            Execute(_connection, transaction, StateSchema.ClearDeletedHistory, (StateSchema.ParamPair, pairId));
        }
        transaction.Commit();
    });

    /// <inheritdoc />
    public void SaveDeletedHistory(string pairId, Guid scanToken, string cursor)
    {
        ArgumentException.ThrowIfNullOrEmpty(cursor);
        var pair = Ordered(() =>
        {
            var scan = ReadScan(pairId, scanToken);
            var row = RequireGeneration(null, pairId, scan.Generation);
            if (RequireScan(pairId, scanToken).IncrementalHistory)
            {
                Execute(_connection, null, StateSchema.PublishDeletedHistory,
                    (StateSchema.ParamPair, pairId), (StateSchema.ParamCursor, cursor));
                return (long?)null;
            }
            if (!scan.Discovery) return (long?)null;
            Execute(_connection, null, StateSchema.ClearDeletedHistory, (StateSchema.ParamPair, pairId));
            return row;
        });
        if (pair is null) return;
        ReclaimDeletedHistory(pair.Value);
        CopyDeletedHistory(pairId, scanToken, cursor);
    }

    private void ReclaimDeletedHistory(long pair)
    {
        while (Ordered(() => Execute(_connection, null, StateSchema.ReclaimDeletedHistory,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamTake, StateSchema.DeletedHistoryBatchSize))) != 0) { }
    }

    private void CopyDeletedHistory(string pairId, Guid scanToken, string cursor)
    {
        FoldDeletedHistory(pairId, scanToken);
        string? after = null;
        while (Ordered(() =>
        {
            var scan = ReadScan(pairId, scanToken);
            var pair = RequireGeneration(null, pairId, scan.Generation);
            if (DeletedHistoryCursor(pairId) is not null)
                throw new InvalidOperationException();
            AttachScan(pairId, create: false);
            using var transaction = _connection.BeginTransaction();
            var keys = new List<string>();
            using (var command = StorageCommand.Create(_connection, transaction, StateSchema.SaveDeletedHistory,
                (StateSchema.ParamPairRow, pair), (StateSchema.ParamAfter, after),
                (StateSchema.ParamTake, StateSchema.DeletedHistoryBatchSize)))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) keys.Add(reader.GetString(0));
            if (keys.Count == 0) return false;
            WriteDeletedBlock(_connection, transaction, pair, keys);
            after = keys[^1];
            transaction.Commit();
            return true;
        })) { }
        Write(() =>
        {
            var scan = ReadScan(pairId, scanToken);
            var pair = RequireGeneration(null, pairId, scan.Generation);
            AttachScan(pairId, create: false);
            if (DeletedHistoryCursor(pairId) is not null)
                throw new InvalidOperationException();
            using var transaction = _connection.BeginTransaction();
            Execute(_connection, transaction, StateSchema.RetireFoldedDeletedNames, (StateSchema.ParamPairRow, pair));
            Execute(_connection, transaction, StateSchema.PublishDeletedHistory,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamCursor, cursor));
            transaction.Commit();
        });
    }

    private void FoldDeletedHistory(string pairId, Guid scanToken)
    {
        string? after = null;
        while (Ordered(() =>
        {
            var scan = ReadScan(pairId, scanToken);
            var pair = RequireGeneration(null, pairId, scan.Generation);
            AttachScan(pairId, create: false);
            using var transaction = _connection.BeginTransaction();
            var delta = new List<string>();
            using (var page = StorageCommand.Create(_connection, transaction, StateSchema.DeletedDeltaPage,
                (StateSchema.ParamPairRow, pair), (StateSchema.ParamAfter, after),
                (StateSchema.ParamTake, StateSchema.DeletedHistoryBatchSize)))
            using (var reader = page.ExecuteReader())
                while (reader.Read()) delta.Add(reader.GetString(0));
            if (delta.Count == 0) return false;
            InsertDeletedNames(transaction, pair, delta);
            after = delta[^1];
            transaction.Commit();
            return true;
        })) { }
    }

    public bool CloseScanIssue(string pairId, Guid scanId, Issue expected) => Ordered(() =>
    {
        var scan = RequireScan(pairId, scanId);
        if (scan.Record.Scope is not null) throw new InvalidOperationException();
        AttachScan(pairId, create: false);
        using var transaction = _connection.BeginTransaction();
        using var unchanged = StorageCommand.Create(_connection, transaction, StateSchema.ScanIssueUnchanged,
            (StateSchema.ParamScan, scan.Key), (StateSchema.ParamPairRow, scan.PairRow), (StateSchema.ParamIssue, expected.Id));
        if ((long)unchanged.ExecuteScalar()! == 0 ||
            !IssueTable.CloseExact(_connection, transaction, pairId, expected, acknowledgeFirstRun: true)) return false;
        transaction.Commit();
        _closureKeys?.Remove(pairId);
        return true;
    });

    /// <summary>Opens fresh disposable plan scratch, including for read-only exclusion preview.</summary>
    public FolderPlan OpenFolderPlan(string pairId, Row folder, Side deletingFrom)
    {
        lock (_lock)
        {
            var path = _path + StateSchema.PlanFileSuffix + ResourceNames.NewId() + StateSchema.DatabaseExtension;
            using (Stream scratch = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) { }
            _plans.Add(path);
            SqliteConnection? connection = null;
            try
            {
                connection = Connect(StateSchema.ReadRoot, SqliteOpenMode.ReadWriteCreate);
                Execute(connection, null, StateSchema.AttachState, (StateSchema.ParamPath, _path));
                Execute(connection, null, StateSchema.AttachScan, (StateSchema.ParamPath, path));
                Execute(connection, null, StateSchema.PlanPragmas);
                return new StoredFolderPlan(connection, path, pairId, folder, deletingFrom, this);
            }
            catch (Exception setup)
            {
                try
                {
                    connection?.Dispose();
                    if (_plans.Contains(path))
                    {
                        File.Delete(path);
                        _plans.Remove(path);
                    }
                }
                catch (Exception cleanup)
                {
                    throw new AggregateException(setup, cleanup);
                }
                throw;
            }
        }
    }

    /// <summary>Ends this store's registration of one plan scratch path.</summary>
    internal void ReleasePlan(string path)
    {
        lock (_lock) _plans.Remove(path);
    }

    /// <inheritdoc />
    public bool UnreadableTouches(string pairId, string localKey, bool isFolder)
    {
        lock (_lock) return IssueTable.UnreadableTouches(_connection, pairId, localKey, isFolder);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> UnreadableUnder(string pairId, string localKey)
    {
        lock (_lock) return IssueTable.UnreadableUnder(_connection, pairId, localKey);
    }

    /// <inheritdoc />
    public bool ReleaseUnreadable(string pairId, long expectedGeneration, string localKey, string acquiredId) => Ordered(() =>
    {
        var touched = _scans.Where(item => item.Key.PairId == pairId && item.Value.Active &&
            item.Value.Record.Generation == expectedGeneration).Select(item => item.Value).ToArray();
        if (touched.Length != 0) AttachScan(pairId, create: false);
        using var transaction = _connection.BeginTransaction();
        var pair = RequireGeneration(transaction, pairId, expectedGeneration);
        if (!IssueTable.ReleasableMember(_connection, transaction, pair, localKey, acquiredId)) return false;
        foreach (var scan in touched)
            if (scan.Record.Scope is null || PathNames.IsSelfOrUnderKey(localKey, PathNames.Key(scan.Record.Scope)))
                Execute(_connection, transaction, StateSchema.TouchSkip, (StateSchema.ParamPairRow, pair), (StateSchema.ParamScan, scan.Key),
                (StateSchema.ParamSide, (int)Side.Local), (StateSchema.ParamPath, localKey));
        IssueTable.RemoveLocalSkip(_connection, transaction, pairId, pair, localKey, DateTimeOffset.UtcNow);
        transaction.Commit();
        return true;
    });

    /// <summary>Tests the exact forget ranges against known-unreadable Local content and saved counterparts.</summary>
    public bool ForgetTouchesUnreadable(string pairId, IReadOnlyList<(Side Side, string Key)> roots, CancellationToken ct)
    {
        lock (_lock) return ForgetTouchesUnreadable(_connection, pairId, PathNames.DisjointRoots(roots), ct);
    }

    // One forget-root test: the local and Dropbox probes share the unreadable-touch query and cleared prefix set.
    internal static bool ForgetTouchesUnreadable(SqliteConnection connection, string pairId,
        IEnumerable<(Side Side, string Key)> roots, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var cleared = new PrefixSet();
        foreach (var (side, key) in roots)
        {
            ct.ThrowIfCancellationRequested();
            if (side == Side.Local)
            {
                if (IssueTable.UnreadableTouches(connection, pairId, key, isFolder: true)) return true;
                cleared.Add(key);
                continue;
            }
            if (side != Side.Dropbox) throw new ArgumentOutOfRangeException(nameof(roots));
            string? mapped = null;
            if (Probe(StateSchema.SelectSyncedByDropboxKey, key, cover: null) ||
                Probe(StateSchema.SelectSyncedUnderDropboxKey, key, cover: mapped)) return true;

            bool Probe(string sql, string target, string? cover)
            {
                using var command = StorageCommand.Create(connection, null, sql,
                    (StateSchema.ParamPair, pairId), (StateSchema.ParamTargetKey, target));
                using var reader = command.ExecuteReader();
                var proved = new PrefixSet();
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    // The reader holds one snapshot; a nested check sees what the per-row checks it replaces would see.
                    if (cover is not null)
                    {
                        if (!cleared.Covers(cover) && !IssueTable.UnreadableTouches(connection, pairId, cover, isFolder: true))
                            proved.Add(cover);
                        cover = null;
                    }
                    var localKey = reader.GetString(StateSchema.SyncedLocalKey);
                    var isFolder = reader.GetBoolean(StateSchema.SyncedIsFolder);
                    if (sql == StateSchema.SelectSyncedByDropboxKey && isFolder) mapped = localKey;
                    if (cleared.Covers(localKey) || proved.Covers(localKey)) continue;
                    if (IssueTable.UnreadableTouches(connection, pairId, localKey, isFolder)) return true;
                    if (isFolder) proved.Add(localKey);
                }
                return false;
            }
        }
        return false;
    }

    /// <summary>Reads token-isolated rows while keeping the pair file alive through iterator disposal.</summary>
    public IEnumerable<Row> ScanRows(string pairId, Guid scanId, Func<Row, RowContext> context)
    {
        ScanPartition scan;
        SqliteConnection read;
        lock (_lock)
        {
            scan = RequireScan(pairId, scanId);
            read = OpenScanRead(pairId);
            scan.Borrowers++;
        }
        try
        {
            using (read)
                foreach (var row in ScanReader.Rows(read, pairId, context, scan.Record.Scope, scan.Key)) yield return row;
        }
        finally { lock (_lock) scan.Borrowers--; }
    }

    public IReadOnlyList<RenameCandidate> ScanRenames(string pairId, Guid scanId)
    {
        ScanPartition scan;
        SqliteConnection read;
        lock (_lock)
        {
            scan = RequireScan(pairId, scanId);
            read = OpenScanRead(pairId);
            scan.Borrowers++;
        }
        try { using (read) return ScanReader.Renames(read, pairId, scan.Key); }
        finally { lock (_lock) scan.Borrowers--; }
    }

    public Entry? ScanEntry(string pairId, Guid scanId, Side side, string key)
    {
        lock (_lock)
        {
            var scan = RequireScan(pairId, scanId);
            AttachScan(pairId, create: false);
            return ScanReader.EntryByKey(_connection, pairId, side, key, scan.Key);
        }
    }

    public bool InScan(string pairId, Guid scanId, Side side, string key)
    {
        lock (_lock)
        {
            var scan = RequireScan(pairId, scanId);
            AttachScan(pairId, create: false);
            using var command = StorageCommand.Create(_connection, null, side == Side.Local ? StateSchema.InScanLocal : StateSchema.InScanDropbox,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamScan, scan.Key), (StateSchema.ParamTargetKey, key));
            return (long)command.ExecuteScalar()! != 0;
        }
    }

    // One pair decision: generation fence, supersessions and source advances are one transaction.
    /// <inheritdoc />
    public void SavePairDecision(PairConfig expected, long expectedGeneration, PairConfig next,
        Issue? expectedIssue, bool resetHistory, string? reincludedKey) => Write(() =>
    {
        if (expected.PairId != next.PairId || expected.LocalRoot != next.LocalRoot ||
            expected.DropboxRoot != next.DropboxRoot || expected.ConnectionId != next.ConnectionId)
            throw new ArgumentException(null, nameof(next));
        var local = _changes.ReadSource(expected.PairId, Side.Local) ?? throw new InvalidOperationException();
        var dropbox = _changes.ReadSource(expected.PairId, Side.Dropbox) ?? throw new InvalidOperationException();
        using var transaction = _connection.BeginTransaction();
        var pair = RequireGeneration(transaction, expected.PairId, expectedGeneration);
        var current = ReadOne(transaction, StateSchema.SelectPair,
            static reader => StateEncoding.DecodePair(reader.GetString(StateSchema.FirstColumn)),
            (StateSchema.ParamPair, expected.PairId)) ?? throw new InvalidOperationException();
        if (JsonSerializer.Serialize(current, PairEncoding) != JsonSerializer.Serialize(expected, PairEncoding))
            throw new InvalidOperationException();
        if (reincludedKey is not null)
        {
            if (resetHistory || expectedIssue is not null ||
                JsonSerializer.Serialize(next with { Exclusions = expected.Exclusions }, PairEncoding) !=
                    JsonSerializer.Serialize(expected, PairEncoding))
                throw new ArgumentException(null, nameof(reincludedKey));
        }
        // Configuration changes must not supersede pending or folder settlement custody.
        if ((resetHistory || reincludedKey is not null) && (PendingJson(transaction, pair) is not null ||
            ReadOne(transaction, StateSchema.ActiveFolders,
                static reader => StateEncoding.ReadFolderRemoval(reader, null),
                (StateSchema.ParamPairRow, pair), (StateSchema.ParamAfterOperation, string.Empty),
                (StateSchema.ParamTake, StateSchema.OneRow)) is not null))
            throw new InvalidOperationException();
        var retry = ReadRetry(transaction, pair);
        var notice = retry is null ? null : IssueTable.RetryNotice(_connection, transaction, expected.PairId) ??
            throw new InvalidOperationException();
        if (notice is not null)
            RequireOne(IssueTable.CloseSupersededRetryNotice(_connection, transaction, pair, notice.Id,
                expectedGeneration, local.Epoch, local.Revision, dropbox.Epoch, dropbox.Revision, retry!.Value));
        if (expectedIssue is not null && !IssueTable.CloseExact(_connection, transaction, expected.PairId, expectedIssue))
            throw StaleIssue(expectedIssue.Id);
        if (_changes.HasCurrentLocalWork(transaction, expected.PairId, expectedGeneration))
            _changes.TransferLocalEvidenceGap(transaction, expected.PairId, expectedGeneration, local.Epoch, local.Revision);
        if (RootBinding.HistoryIdentityChanged(current, next))
            Execute(_connection, transaction, StateSchema.ReplaceDeletedHistoryIdentity, (StateSchema.ParamPair, expected.PairId));
        var generation = checked(expectedGeneration + 1);
        RequireOne(Execute(_connection, transaction, StateSchema.AdvancePair,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamGeneration, expectedGeneration),
            (StateSchema.ParamNextGeneration, generation),
            (StateSchema.ParamDefinition, JsonSerializer.Serialize(next, PairEncoding)),
            (StateSchema.ParamGapReason, nameof(SourceObligation.LocalSkippedCoverage))));
        _changes.AdvanceSource(transaction, expected.PairId, Side.Local, generation, local.Epoch, local.Revision,
            local.Provenance, local.Cursor, checked(local.Epoch + 1), checked(local.Revision + 1),
            null, null, SourceObligations.Owed(local.Obligation, SourceObligation.Configuration));
        _changes.AdvanceSource(transaction, expected.PairId, Side.Dropbox, generation, dropbox.Epoch, dropbox.Revision,
            dropbox.Provenance, dropbox.Cursor, checked(dropbox.Epoch + 1), checked(dropbox.Revision + 1),
            null, null, SourceObligations.Owed(dropbox.Obligation, SourceObligation.Configuration));
        if (!resetHistory)
            _changes.TransferLocalDirtyEvidence(transaction, expected.PairId, next.Roots, expectedGeneration,
                local.Epoch, generation, checked(local.Epoch + 1));
        _changes.TransferItemEpisodes(transaction, expected.PairId, next.Roots, generation,
            checked(local.Epoch + 1), checked(dropbox.Epoch + 1));
        if (resetHistory) Execute(_connection, transaction, StateSchema.ForgetAllSynced,
            (StateSchema.ParamPair, expected.PairId));
        if (notice is not null)
            IssueTable.Raise(_connection, transaction, expected.PairId, notice.Kind, notice.Cause, notice.Path,
                notice.Subject, DateTimeOffset.UtcNow);
        transaction.Commit();
        _closureKeys?.Remove(expected.PairId);
        InvalidateScans(expected.PairId);
    });

    // One repair finish under the store write lock: the fence reads and checks precede the transaction that settles generation, obligations, the retry notice and covered work; a following write reclaims and checkpoints.
    /// <inheritdoc />
    public void FinishRepair(string pairId, RepairSummary summary, long expectedGeneration, Guid scanToken,
        LocalCatchUp local, DropboxCatchUp dropbox)
    {
        Write(() =>
        {
            var scan = ReadScan(pairId, scanToken);
            var localEpoch = scan.LocalEpoch;
            var dropboxEpoch = scan.DropboxEpoch;
            var localRevision = scan.LocalRevision;
            var dropboxRevision = scan.DropboxRevision;
            // Page acquisition preserves this fence; every other cursor transition invalidates it.
            if (scan.Scope is not null || !summary.Completed || scan.Generation != expectedGeneration || local.PairId != pairId || dropbox.PairId != pairId ||
                local.PairGeneration != expectedGeneration || dropbox.PairGeneration != expectedGeneration ||
                local.SourceEpoch != localEpoch || dropbox.SourceEpoch != dropboxEpoch ||
                scan.ThroughWorkId > local.ThroughWorkId || scan.ThroughWorkId > dropbox.ThroughWorkId ||
                _changes.ReadLocalGap(pairId, localEpoch) != scan.LocalGapRevision ||
                _changes.ReadSource(pairId, Side.Dropbox) is not { } remote ||
                remote.Epoch != dropboxEpoch || remote.Revision != dropboxRevision)
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
            var definition = Pair(pairId) ?? throw new InvalidOperationException();
            var domain = definition.Roots?.LocalVolumeKey()
                ?? throw new InvalidOperationException();
            var position = _changes.ReadJournal(domain);
            if (position is null || position.JournalId != local.Checkpoint.JournalId ||
                position.NextUsn < checked((ulong)local.Checkpoint.NextUsn))
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
            using (var transaction = _connection.BeginTransaction())
            {
                var pair = RequireGeneration(transaction, pairId, expectedGeneration);
                var retry = ReadRetry(transaction, pair);
                var notice = retry is null ? null : IssueTable.RetryNotice(_connection, transaction, pairId) ??
                    throw new InvalidOperationException();
                RequireOne(Execute(_connection, transaction, StateSchema.CompletePrerequisitePair,
                    (StateSchema.ParamPairRow, pair), (StateSchema.ParamGeneration, expectedGeneration),
                    (StateSchema.ParamLocalEpoch, localEpoch), (StateSchema.ParamDropboxEpoch, dropboxEpoch),
                    (StateSchema.ParamLocalRevision, localRevision), (StateSchema.ParamDropboxRevision, dropboxRevision),
                    (StateSchema.ParamExpectedAttempts, retry?.Attempts),
                    (StateSchema.ParamExpectedRetryAfter, retry?.RetryAfter.UtcTicks),
                    (StateSchema.ParamSummary, JsonSerializer.Serialize(summary)), (StateSchema.ParamCompleted, true)));
                _changes.FinishObligation(transaction, pairId, Side.Local, expectedGeneration, localEpoch, localRevision);
                _changes.FinishObligation(transaction, pairId, Side.Dropbox, expectedGeneration, dropboxEpoch, dropboxRevision);
                if (notice is not null) RequireOne(IssueTable.CloseRetryNotice(_connection, transaction, pair, notice.Id));
                _changes.RetireCoveredWork(transaction, pairId, pair, expectedGeneration,
                    localEpoch, dropboxEpoch, scan.ThroughWorkId);
                transaction.Commit();

            }
            _closureKeys?.Remove(pairId);
        });
        Write(() =>
        {
            Reclaim();
            using var checkpoint = StorageCommand.Create(_connection, null, StateSchema.Checkpoint);
            using var result = checkpoint.ExecuteReader();
            result.Read();
        });
    }

    /// <inheritdoc />
    public Task CommitAsync(string pairId, StateChange change)
    {
        if (QueueableRecord(change) || QueueableAdoption(change)) return QueueRecord(pairId, change, narrow: true);
        Commit(pairId, change);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void AdvancePending(string pairId, PendingEffect expected, PendingEffect next) => Write(() =>
    {
        if (next.OperationId != expected.OperationId || next.Generation != checked(expected.Generation + 1) ||
            next.Kind != expected.Kind || next.Path != expected.Path || next.Losing != expected.Losing ||
            next.Saved != expected.Saved || next.Source != expected.Source || next.Stage != expected.Stage ||
            next.PermanentAllowed != expected.PermanentAllowed)
            throw new ArgumentException(null, nameof(next));
        var json = StateEncoding.EncodePending(next);
        using var transaction = _connection.BeginTransaction();
        var pair = RequirePairRow(transaction, pairId);
        var raw = RequirePending(transaction, pair, expected);
        var issueIds = ReadPage(transaction, StateSchema.ItemEpisodeBySubject,
            static reader => reader.GetInt64(StateSchema.FirstColumn),
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamSide, (int)Side.Local),
            (StateSchema.ParamPath, PathNames.Key(expected.Path)),
            (StateSchema.ParamIdentity, StateEncoding.LocalId(expected.Losing.Expected.Id)));
        var retries = issueIds.Count == 0 ? null :
            IssueTable.ReadItemRetryState(_connection, transaction, pairId, pair, issueIds);
        RequireOne(Execute(_connection, transaction, StateSchema.PendingAdvance,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamExpected, raw), (StateSchema.ParamNext, json)));
        if (retries is not null)
            foreach (var candidate in retries.Values)
                if (candidate is { Dispatch: ItemRetryDispatch.LocalRecovery } &&
                    candidate.PendingOperationId == expected.OperationId && candidate.PendingGeneration == expected.Generation)
                    _changes.RefreshPendingRetryBinding(transaction, candidate, next.Generation, json);
        transaction.Commit();
    });

    /// <inheritdoc />
    public void SettlePending(string pairId, PendingEffect expected, StateChange change, Action<Issue>? committedIssue = null) =>
        SettlePending(pairId, expected, change, null, committedIssue);

    public void SettlePending(string pairId, PendingEffect expected, StateChange change, WorkSelection? selection, Action<Issue>? committedIssue)
    {
        List<Issue>? raised = committedIssue is null || change.Raise.Count == 0 ? null : [];
        Write(() =>
        {
            using var transaction = _connection.BeginTransaction();
            var pair = RequirePairRow(transaction, pairId);
            if (selection is not null) _changes.AdvanceBefore(transaction, pairId, selection, change);
            ApplyPendingSettlement(transaction, pair, pairId, expected, change, raised);
            transaction.Commit();
            UpdateClosures(pairId, change);
        });
        if (raised is not null)
            foreach (var issue in raised) committedIssue!(issue);
    }

    private void ApplyPendingSettlement(SqliteTransaction transaction, long pair, string pairId,
        PendingEffect expected, StateChange change, List<Issue>? raised = null)
    {
        if (change.Renames.Count != 0 || change.ForgetSubtrees.Count != 0 || change.ForgetBothSubtrees.Count != 0)
            throw new ArgumentException(null, nameof(change));
        if (change.FirstRunAcknowledged.Count != 0 &&
            (expected is not ({ Kind: PendingKind.LocalDelete, Phase: PendingPhase.Removed } or
                { Kind: PendingKind.LocalReplace, Phase: PendingPhase.Published }) ||
             change.FirstRunAcknowledged.Any(item => item.Side != Side.Local ||
                 item.Path != expected.Losing.Expected.Path || item.Id != expected.Losing.Expected.Id)))
            throw new ArgumentException(null, nameof(change));
        foreach (var (side, key) in change.ForgetSelf)
            if (expected.Phase != PendingPhase.Removed || expected.Kind != PendingKind.LocalDelete ||
                (expected.Saved is { } saved ? key != saved.KeyOn(side) :
                    side != Side.Local || key != PathNames.Key(expected.Losing.Expected.Path)))
                throw new ArgumentException(null, nameof(change));
        foreach (var entry in change.Record)
            if (expected.Phase != PendingPhase.Published || expected.Kind != PendingKind.LocalReplace ||
                expected.Stage is null || expected.Source is null || entry.LocalPath != expected.Path ||
                entry.LocalId != expected.Stage.FileId || entry.LocalSize != expected.Stage.Size ||
                entry.DropboxId != expected.Source.Id || entry.DropboxContentHash != expected.Stage.ContentHash)
                throw new ArgumentException(null, nameof(change));
        var raw = RequirePending(transaction, pair, expected);
        RequirePendingSaved(transaction, pairId, expected);
        StateCommitter.Commit(_connection, transaction, _changes, pairId, change, DateTimeOffset.UtcNow,
            narrowRecordUpdates: true, raised: raised);
        RequireOne(Execute(_connection, transaction, StateSchema.PendingSettle,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamExpected, raw)));
    }

    /// <inheritdoc />
    public void SettleLegacyPending(string pairId, string rawToken, StateChange issueOnlyChange) => Write(() =>
    {
        RequireIssueOnly(issueOnlyChange);
        using var transaction = _connection.BeginTransaction();
        var pair = RequirePairRow(transaction, pairId);
        var raw = PendingJson(transaction, pair);
        if (StateEncoding.DecodePending(raw) is not LegacyPending legacy || legacy.RawToken != rawToken)
            throw new InvalidOperationException();
        StateCommitter.Commit(_connection, transaction, _changes, pairId, issueOnlyChange, DateTimeOffset.UtcNow,
            narrowRecordUpdates: true, raised: null);
        RequireOne(Execute(_connection, transaction, StateSchema.PendingSettle,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamExpected, raw)));
        transaction.Commit();
        UpdateClosures(pairId, issueOnlyChange);
    });

    private static void RequireIssueOnly(StateChange change)
    {
        if (change.Record.Count != 0 || change.StaleBindings.Count != 0 || change.ForgetBothSubtrees.Count != 0 || change.Renames.Count != 0 || change.ForgetSelf.Count != 0 ||
            change.ForgetSubtrees.Count != 0) throw new ArgumentException(null, nameof(change));
    }

    /// <inheritdoc />
    public JournalPosition? ReadJournal(string volumeKey)
    {
        lock (_lock) return _legacy ? null : _changes.ReadJournal(volumeKey);
    }

    /// <inheritdoc />
    public long? AcquireJournalPage(string volumeKey, JournalPosition? expected, JournalPosition next,
        IReadOnlyList<(string PairId, long Generation, long Epoch, long Revision,
            bool LostInterval)> members,
        IEnumerable<(string PairId, ChangeNotice Notice)> notices) =>
        Ordered(() => _changes.AcquireJournalPage(volumeKey, expected, next, members, notices));

    /// <inheritdoc />
    public SourceObligation LostIntervalObligation(string pairId)
    {
        lock (_lock) return _changes.LostIntervalObligation(pairId);
    }

    /// <inheritdoc />
    public void RequireRepairAfterLoss(string pairId, long expectedGeneration) => Write(() =>
    {
        using var transaction = _connection.BeginTransaction();
        _changes.RequireRepairAfterLoss(transaction, pairId, expectedGeneration);
        transaction.Commit();
    });

    /// <inheritdoc />
    public void RequireRepairForOwedHistory(string pairId, long expectedGeneration) => Write(() =>
    {
        RequireGeneration(null, pairId, expectedGeneration);
        if (!DeletedHistoryOwed(pairId) || _changes.Obligated(pairId)) return;
        RequireRepairAfterLoss(pairId, expectedGeneration);
    });

    /// <inheritdoc />
    public (long Epoch, SourceProvenance? Provenance, string? Cursor, SourceObligation? Obligation, long Revision)?
        ReadSource(string pairId, Side side)
    {
        lock (_lock) return _legacy ? null : _changes.ReadSource(pairId, side);
    }

    /// <inheritdoc />
    public bool Obligated(string pairId)
    {
        lock (_lock) return !_legacy && _changes.Obligated(pairId);
    }

    /// <inheritdoc />
    public LiveRefusal ReadLiveRefusal(string pairId)
    {
        lock (_lock) return _legacy ? LiveRefusal.None : _changes.ReadLiveRefusal(pairId);
    }

    /// <inheritdoc />
    public long? ReadLocalGap(string pairId, long localEpoch)
    {
        lock (_lock) return _legacy ? null : _changes.ReadLocalGap(pairId, localEpoch);
    }

    /// <inheritdoc />
    public long AcquireDropboxPage(string pairId, long expectedGeneration, long expectedEpoch, long expectedRevision,
        SourceProvenance provenance, string? expectedCursor, IEnumerable<ChangeNotice> notices, string cursor) =>
        Ordered(() => _changes.AcquireDropboxPage(pairId, expectedGeneration, expectedEpoch, expectedRevision,
            provenance, expectedCursor, notices, cursor));

    /// <inheritdoc />
    public void AdvanceSource(string pairId, Side side, long expectedGeneration,
        long expectedEpoch, long expectedRevision, SourceProvenance? expectedProvenance, string? expectedCursor,
        long nextEpoch, long nextRevision, SourceProvenance? provenance, string? cursor,
        SourceObligation? obligation) => Write(() =>
        {
            var roots = nextEpoch != expectedEpoch ? Pair(pairId)!.Roots : null;
            using var transaction = _connection.BeginTransaction();
            _changes.AdvanceSource(transaction, pairId, side, expectedGeneration, expectedEpoch, expectedRevision,
                expectedProvenance, expectedCursor, nextEpoch, nextRevision, provenance, cursor, obligation);
            if (nextEpoch != expectedEpoch)
                _changes.TransferItemEpisodes(transaction, pairId, roots, expectedGeneration,
                    nextEpoch, nextEpoch, side);
            transaction.Commit();
        });

    /// <inheritdoc />
    public WorkSelection? SelectWork(string pairId, long expectedGeneration, PathScope scope) =>
        Ordered(() => _changes.SelectWork(pairId, expectedGeneration, scope));

    /// <inheritdoc />
    public bool EndWorkPass(string pairId)
    {
        lock (_lock) return _changes.EndWorkPass(pairId);
    }

    /// <inheritdoc />
    public IEnumerable<PendingChange> ReadWork(WorkSelection selection) =>
        ReadOrdered(_changes.ReadWork(selection));

    /// <inheritdoc />
    public IEnumerable<WorkSelection> PendingAncestors(string pairId, long expectedGeneration, Side side,
        IReadOnlyList<string> ancestorKeys, long? coveredThrough = null) =>
        ReadOrdered(_changes.PendingAncestors(pairId, expectedGeneration, side, ancestorKeys, coveredThrough));

    /// <inheritdoc />
    public AncestorAnswer LaterAncestorNews(string pairId, long expectedGeneration, Side side,
        IReadOnlyList<string> ancestorKeys, long coveredThrough)
    {
        lock (_lock) return _changes.LaterAncestorNews(pairId, expectedGeneration, side, ancestorKeys, coveredThrough);
    }

    /// <inheritdoc />
    public Func<Side, string, string?, bool> CreateSeparateWorkProbe(WorkSelection selection)
    {
        Func<Side, string, string?, bool> probe;
        lock (_lock) probe = _changes.CreateSeparateWorkProbe(selection);
        return (side, key, identity) => { lock (_lock) return probe(side, key, identity); };
    }

    /// <inheritdoc />
    public Func<SyncedEntry, long, bool> CreateLocalEchoProbe(WorkSelection selection)
    {
        Func<SyncedEntry, long, bool> probe;
        lock (_lock) probe = _changes.CreateLocalEchoProbe(selection);
        return (saved, through) => { lock (_lock) return probe(saved, through); };
    }

    public void RequireDiscovery(WorkSelection selection, long workId) =>
        Write(() => _changes.RequireDiscovery(selection, workId));

    public void BeginSelection(WorkSelection selection) =>
        Write(() => _changes.BeginSelection(selection));

    /// <inheritdoc />
    public void ClaimSelectionPeer(WorkSelection selection, SelectionPeer peer)
    {
        lock (_lock) _changes.ClaimSelectionPeer(selection, peer);
    }

    /// <inheritdoc />
    public void EndSelection(WorkSelection selection)
    {
        lock (_lock) _changes.EndSelection(selection);
    }

    /// <inheritdoc />
    public bool CompleteWork(WorkSelection selection, SelectionPeer? peer) => Ordered(() => _changes.CompleteWork(selection, peer));

    /// <inheritdoc />
    public bool AttachIssueWork(WorkSelection parent, Issue exactIssue,
        IEnumerable<(ChangeNotice Notice, bool IsFolder, SyncedEntry? Before)> unfinished) =>
        Ordered(() => _changes.AttachIssueWork(parent, exactIssue, unfinished));

    /// <inheritdoc />
    public (bool Dirty, JournalPosition? SourceBoundary, long EvidenceVersion) ReadLocalDirty(
        string pairId, long expectedGeneration, long localSourceEpoch, string nativeDomain,
        string? fullIdentity, string? currentPathKey, string? recordedPathKey) =>
        Ordered(() => _changes.ReadLocalDirty(pairId, expectedGeneration, localSourceEpoch, nativeDomain,
            fullIdentity, currentPathKey, recordedPathKey));

    /// <inheritdoc />
    public PendingRead ReadPendingForFolderRecovery(string pairId, FolderRemovalState operation) => Ordered(() =>
    {
        var pair = RequirePairRow(null, pairId);
        if (ReadFolder(null, pair, operation.OperationId) != operation || !operation.Active)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
        return StateEncoding.DecodePending(PendingJson(null, pair));
    });

    /// <inheritdoc />
    public void ValidatePendingForFolderRecovery(string pairId, FolderRemovalState operation,
        PendingRead expectedPending) => Write(() =>
    {
        var pair = RequirePairRow(null, pairId);
        if (ReadFolder(null, pair, operation.OperationId) != operation || !operation.Active ||
            StateEncoding.DecodePending(PendingJson(null, pair)) != expectedPending)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
    });

    /// <inheritdoc />
    public LiveCoverage CaptureLiveCoverage(WorkSelection selection, LocalCatchUp local, DropboxCatchUp dropbox) =>
        Ordered(() => _changes.CaptureLiveCoverage(selection, local, dropbox));

    /// <inheritdoc />
    public void ValidateLiveCoverage(WorkSelection selection, LiveCoverage coverage,
        IReadOnlyList<LiveEffectScope> footprint) =>
        Write(() => _changes.ValidateLiveCoverage(selection, coverage, footprint, _issueFacts.Read(selection.PairId).Overlaps));

    /// <inheritdoc />
    public Massdex.SyncShark.V2.FailureRetryOwner.State? ReadPrerequisiteRetry(string pairId, Issue? expected = null)
    {
        lock (_lock)
        {
            RequireExpectedIssue(pairId, expected);
            return !_legacy && PairRow(null, pairId) is { } pair ? ReadRetry(null, pair) : null;
        }
    }

    private Massdex.SyncShark.V2.FailureRetryOwner.State? ReadRetry(SqliteTransaction? transaction, long pair) =>
        ReadOne(transaction, StateSchema.ReadRetry,
            static reader => StateEncoding.ReadRetryState(reader, StateSchema.RetryAttempts),
            (StateSchema.ParamPairRow, pair));

    private long RequireGeneration(SqliteTransaction? transaction, string pairId, long expectedGeneration)
    {
        using var command = StorageCommand.Create(_connection, transaction,
            _legacy ? StateSchema.LegacyPairFence : StateSchema.PairFence,
            (StateSchema.ParamPair, pairId));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt64(StateSchema.PairFenceExecutionGeneration) != expectedGeneration)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
        return reader.GetInt64(StateSchema.PairFenceId);
    }

    /// <inheritdoc />
    public IEnumerable<(long PairRow, string PairId, long Generation, Massdex.SyncShark.V2.FailureRetryOwner.State State)>
        ReadPrerequisiteRetries(long? afterTicks, long? afterPairRow, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        var ticks = afterTicks ?? StateSchema.BeforeFirstTicks;
        var row = afterPairRow ?? StateSchema.BeforeFirstPairRow;
        lock (_lock)
            return _legacy ? [] : ReadPage(null, StateSchema.EarliestRetry, static reader =>
                (reader.GetInt64(StateSchema.EarliestRetryId), reader.GetString(StateSchema.EarliestRetryPairId),
                 reader.GetInt64(StateSchema.EarliestRetryExecutionGeneration),
                 StateEncoding.ReadRetryState(reader, StateSchema.EarliestRetryAttempts) ??
                    throw new InvalidDataException()),
                (StateSchema.ParamAfterTicks, ticks), (StateSchema.ParamAfterPair, row),
                (StateSchema.ParamTake, pageSize));
    }

    /// <inheritdoc />
    public void RecordPrerequisiteFailure(string pairId, long expectedGeneration,
        long localEpoch, long dropboxEpoch, long localRevision, long dropboxRevision,
        Massdex.SyncShark.V2.FailureRetryOwner.State? expected,
        Massdex.SyncShark.V2.FailureRetryOwner.State next, StateChange failureNotice) => Write(() =>
    {
        RequireRetryNotice(failureNotice);
        using var transaction = _connection.BeginTransaction();
        var pair = RequireGeneration(transaction, pairId, expectedGeneration);
        RequireOne(Execute(_connection, transaction, StateSchema.RecordPrerequisiteFailure,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamGeneration, expectedGeneration),
            (StateSchema.ParamLocalEpoch, localEpoch), (StateSchema.ParamDropboxEpoch, dropboxEpoch),
            (StateSchema.ParamLocalRevision, localRevision), (StateSchema.ParamDropboxRevision, dropboxRevision),
            (StateSchema.ParamExpectedAttempts, expected?.Attempts),
            (StateSchema.ParamExpectedRetryAfter, expected?.RetryAfter.UtcTicks),
            (StateSchema.ParamAttempts, next.Attempts), (StateSchema.ParamRetryAfter, next.RetryAfter.UtcTicks)));
        StateCommitter.Commit(_connection, transaction, _changes, pairId, failureNotice, DateTimeOffset.UtcNow,
            narrowRecordUpdates: true, raised: null);
        transaction.Commit();
        UpdateClosures(pairId, failureNotice);
    });

    /// <inheritdoc />
    public void CompleteRecomposition(string pairId, long expectedGeneration, long localEpoch, long dropboxEpoch,
        long localRevision, long dropboxRevision, Massdex.SyncShark.V2.FailureRetryOwner.State expected) => Write(() =>
    {
        using var transaction = _connection.BeginTransaction();
        var pair = RequireGeneration(transaction, pairId, expectedGeneration);
        var notice = IssueTable.RetryNotice(_connection, transaction, pairId) ?? throw new InvalidOperationException();
        RequireOne(Execute(_connection, transaction, StateSchema.CompleteRecomposition,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamGeneration, expectedGeneration),
            (StateSchema.ParamLocalEpoch, localEpoch), (StateSchema.ParamDropboxEpoch, dropboxEpoch),
            (StateSchema.ParamLocalRevision, localRevision), (StateSchema.ParamDropboxRevision, dropboxRevision),
            (StateSchema.ParamExpectedAttempts, expected.Attempts),
            (StateSchema.ParamExpectedRetryAfter, expected.RetryAfter.UtcTicks)));
        RequireOne(IssueTable.CloseRetryNotice(_connection, transaction, pair, notice.Id));
        transaction.Commit();
        _closureKeys?.Remove(pairId);
    });

    private static void RequireRetryNotice(StateChange change)
    {
        RequireIssueOnly(change);
        if (change.Raise.Count != 1 || change.Close.Count != 0 || change.FirstRunAcknowledged.Count != 0 ||
            change.Raise[0].Kind != IssueKind.Failure || change.Raise[0].Cause != TextKey.RequiredRepairRetry ||
            change.Raise[0].Path != string.Empty)
            throw new ArgumentException(null, nameof(change));
    }

    /// <inheritdoc />
    public Issue RecordPrerequisitePageWait(string pairId, long expectedGeneration, object operation,
        StateChange failureNotice) => Ordered(() =>
    {
        ArgumentNullException.ThrowIfNull(operation);
        RequireRetryNotice(failureNotice);
        using var transaction = _connection.BeginTransaction();
        RequireGeneration(transaction, pairId, expectedGeneration);
        StateCommitter.Commit(_connection, transaction, _changes, pairId, failureNotice, DateTimeOffset.UtcNow,
            narrowRecordUpdates: true, raised: null);
        var notice = IssueTable.RetryNotice(_connection, transaction, pairId) ?? throw new InvalidOperationException();
        transaction.Commit();
        _pageWaits[operation] = (pairId, expectedGeneration, notice);
        UpdateClosures(pairId, failureNotice);
        return notice;
    });

    /// <inheritdoc />
    public void CompletePrerequisitePageWait(string pairId, long expectedGeneration, object operation,
        Issue expectedNotice) => Write(() =>
    {
        if (!_pageWaits.TryGetValue(operation, out var wait) || wait.PairId != pairId ||
            wait.Generation != expectedGeneration || wait.Notice != expectedNotice)
            throw new InvalidOperationException();
        using var transaction = _connection.BeginTransaction();
        var pair = RequireGeneration(transaction, pairId, expectedGeneration);
        if (!_pageWaits.Any(other => !ReferenceEquals(other.Key, operation) &&
            other.Value.PairId == pairId && other.Value.Notice.Id == expectedNotice.Id))
            IssueTable.ClosePageWaitNotice(_connection, transaction, pairId, pair, expectedGeneration, expectedNotice.Id);
        transaction.Commit();
        _pageWaits.Remove(operation);
        _closureKeys?.Remove(pairId);
    });

    // One in-use failure: attempt claim, issue raise and episode and work creation are one transaction.
    /// <inheritdoc />
    public ItemRetryCandidate RecordInUseFailure(string pairId, long expectedGeneration, long sourceEpoch,
        string path, IssueSubject subject, TextKey cause, WorkSelection? selection,
        ItemRetryDispatch dispatch, PendingEffect? expectedPending, ItemRetryToken? expectedEpisode,
        Guid registeredAttemptId) => Ordered(() =>
    {
        RequireItemAttempt(registeredAttemptId, pairId, expectedGeneration, subject, expectedEpisode);
        if (!IssuePolicy.IsLocalAccessRetryCause(cause) || path.Length == 0)
            throw new ArgumentException(null, nameof(cause));
        using var transaction = _connection.BeginTransaction();
        var pair = RequireGeneration(transaction, pairId, expectedGeneration);
        var raw = PendingJson(transaction, pair);
        if (dispatch == ItemRetryDispatch.LocalRecovery &&
            (expectedPending is null || StateEncoding.DecodePending(raw) is not CurrentPending pending ||
             pending.Value != expectedPending || expectedPending.Phase is PendingPhase.Unknown or PendingPhase.Retained))
            throw new InvalidOperationException();
        if (expectedEpisode is not null)
        {
            var candidate = new ItemRetryCandidate(expectedEpisode, pairId, dispatch,
                expectedPending?.OperationId, expectedPending?.Generation);
            if (!IssueTable.TryClaimItemRetry(_connection, transaction, pair, candidate, raw))
                throw new InvalidOperationException();
            var oldCause = ReadOne(transaction, StateSchema.ReadItemEpisode,
                static reader => StateEncoding.ReadEnum<TextKey>(reader, StateSchema.ItemEpisodeCause),
                (StateSchema.ParamPairRow, pair), (StateSchema.ParamIssue, expectedEpisode.IssueId));
            if (oldCause != cause)
                RequireOne(IssueTable.ReclassifyItemRetry(_connection, transaction, pair, expectedEpisode,
                    oldCause, cause, DateTimeOffset.UtcNow));
        }
        var raised = new List<Issue>();
        IssueTable.Raise(_connection, transaction, pairId, IssueKind.Failure, cause, path, subject,
            DateTimeOffset.UtcNow, raised);
        var issue = raised.Single();
        if (issue.Cause != cause || !issue.Subject.HasSameItems(subject)) throw new InvalidOperationException();
        var existing = IssueTable.ReadItemRetryState(_connection, transaction, pairId, pair, [issue.Id])[issue.Id];
        if (expectedEpisode is null ? existing is not null : existing is null ||
            existing.Token with { Subject = expectedEpisode.Subject } != expectedEpisode ||
            !existing.Token.Subject.HasSameItems(expectedEpisode.Subject)) throw new InvalidOperationException();
        var work = existing?.Token.WorkId ?? _changes.EnsureIssueWork(transaction, pairId, expectedGeneration, sourceEpoch, issue.Id,
            path, issue.Subject, selection, dispatch, expectedPending);
        var result = existing ?? new ItemRetryCandidate(new ItemRetryToken(issue.Id, ResourceNames.NewId(),
            0, work, expectedGeneration, sourceEpoch, issue.Subject), pairId, dispatch,
            expectedPending?.OperationId, expectedPending?.Generation);
        if (existing is null) RequireOne(IssueTable.StartItemEpisode(_connection, transaction, pair, result, raw));
        else if (!IssueTable.TryClaimItemRetry(_connection, transaction, pair, result, raw))
            throw new InvalidOperationException();
        transaction.Commit();
        _closureKeys?.Remove(pairId);
        return result;
    });

    private void RequireItemAttempt(Guid attempt, string pairId, long generation,
        IssueSubject subject, ItemRetryToken? expected)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (_isRegisteredItemAttempt is not { } isRegisteredAttempt)
            throw new InvalidOperationException();
        if (attempt == Guid.Empty || !isRegisteredAttempt(attempt, pairId, generation, subject, expected))
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
    }

    /// <inheritdoc />
    public bool TryClaimItemRetry(ItemRetryCandidate expected, Guid registeredAttemptId)
    {
        RequireItemAttempt(registeredAttemptId, expected.PairId, expected.Token.PairGeneration,
            expected.Token.Subject, expected.Token);
        lock (_lock)
        {
            if (PairRow(null, expected.PairId) is not { } pair) return false;
            return IssueTable.TryClaimItemRetry(_connection, null, pair, expected, PendingJson(null, pair));
        }
    }

    // One item-attempt completion: claim, change commit and pending and work settlement are one transaction.
    /// <inheritdoc />
    public void CompleteItemAttempt(ItemRetryCandidate expected, Guid registeredAttemptId, StateChange change,
        PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null)
    {
        List<Issue>? raised = committedIssue is null || change.Raise.Count == 0 ? null : [];
        Write(() =>
        {
            RequireItemAttempt(registeredAttemptId, expected.PairId, expected.Token.PairGeneration,
                expected.Token.Subject, expected.Token);
            using var transaction = _connection.BeginTransaction();
            var pair = RequireGeneration(transaction, expected.PairId, expected.Token.PairGeneration);
            if (selection is not null) _changes.AdvanceBefore(transaction, expected.PairId, selection, change);
            var pending = PendingJson(transaction, pair);
            if (completedPending is not null)
            {
                if (change.Raise.Any(issue => issue.Kind != IssueKind.Info) ||
                    completedPending is not ({ Kind: PendingKind.LocalDelete, Phase: PendingPhase.Removed } or
                        { Kind: PendingKind.LocalReplace, Phase: PendingPhase.Published }) ||
                    !expected.Token.Subject.Items.Contains(new IssueItem(Side.Local,
                        completedPending.Losing.Expected.Path, completedPending.Losing.Expected.Id)))
                    throw new ArgumentException(null, nameof(completedPending));
                RequirePending(transaction, pair, completedPending);
                if (expected.Dispatch == ItemRetryDispatch.LocalRecovery)
                {
                    if (expected.PendingOperationId != completedPending.OperationId ||
                        expected.PendingGeneration is not { } claimedGeneration || claimedGeneration > completedPending.Generation)
                        throw new InvalidOperationException();
                    expected = expected with { PendingGeneration = completedPending.Generation };
                }
                else if (expected.Dispatch != ItemRetryDispatch.OrdinaryItem)
                    throw new ArgumentException(null, nameof(completedPending));
            }
            if (!IssueTable.TryClaimItemRetry(_connection, transaction, pair, expected, pending))
                throw new InvalidOperationException();
            var failure = change.Raise.Any(issue => issue.Kind == IssueKind.Failure &&
                IssuePolicy.IsLocalAccessRetryCause(issue.Cause));
            var retiredEpisode = false;
            if (failure)
            {
                RequireIssueOnly(change);
                var reported = change.Raise.Single(issue => issue.Kind == IssueKind.Failure &&
                    IssuePolicy.IsLocalAccessRetryCause(issue.Cause) && issue.Subject.HasSameItems(expected.Token.Subject));
                var oldCause = ReadOne(transaction, StateSchema.ReadItemEpisode,
                    static reader => StateEncoding.ReadEnum<TextKey>(reader, StateSchema.ItemEpisodeCause),
                    (StateSchema.ParamPairRow, pair), (StateSchema.ParamIssue, expected.Token.IssueId));
                if (reported.Cause != oldCause)
                    RequireOne(IssueTable.ReclassifyItemRetry(_connection, transaction, pair, expected.Token,
                        oldCause, reported.Cause, DateTimeOffset.UtcNow));
                RequireOne(IssueTable.CompleteFailedItemAttempt(_connection, transaction, pair, expected, pending));
                StateCommitter.Commit(_connection, transaction, _changes, expected.PairId, change, DateTimeOffset.UtcNow,
                    narrowRecordUpdates: true, raised: raised);
            }
            else
            {
                RequireOne(IssueTable.ClearItemEpisode(_connection, transaction, pair, expected.Token));
                if (completedPending is not null)
                    ApplyPendingSettlement(transaction, pair, expected.PairId, completedPending, change, raised);
                else
                {
                    if (expected.Dispatch == ItemRetryDispatch.LocalRecovery && change.Raise.All(issue => issue.Kind == IssueKind.Info))
                        throw new ArgumentException(null, nameof(completedPending));
                    StateCommitter.Commit(_connection, transaction, _changes, expected.PairId, change, DateTimeOffset.UtcNow,
                        narrowRecordUpdates: true, raised: raised);
                }
                retiredEpisode = ReadOne(transaction, StateSchema.WorkDispatch,
                    static reader => !reader.IsDBNull(StateSchema.DispatchIssueWorkKey),
                    (StateSchema.ParamPairRow, pair), (StateSchema.ParamWork, expected.Token.WorkId));
                if (retiredEpisode) _changes.SettleIssueWork(transaction, expected);
                else if (selection is null && change.Raise.All(issue => issue.Kind == IssueKind.Info) &&
                    change.Close.Any(closure => closure.Kinds.Contains(IssueKind.Failure) &&
                        expected.Token.Subject.Items.Any(item => item.Path == closure.Path)))
                    _changes.SettleAcquiredWork(transaction, expected);
            }
            transaction.Commit();
            if (retiredEpisode) _changes.RecordRetirement(expected.PairId);
            UpdateClosures(expected.PairId, change);
        });
        if (raised is not null)
            foreach (var issue in raised) committedIssue!(issue);
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<long, ItemRetryCandidate?> ReadItemRetryState(string pairId,
        IReadOnlyList<long> issueIds, Issue? expected = null)
    {
        lock (_lock)
        {
            RequireExpectedIssue(pairId, expected);
            return _legacy ? issueIds.Distinct().ToDictionary(static id => id, static _ => (ItemRetryCandidate?)null) :
                IssueTable.ReadItemRetryState(_connection, null, pairId, RequirePairRow(null, pairId), issueIds);
        }
    }

    /// <inheritdoc />
    public string BeginFolderRemoval(string pairId, FolderPlan plan, int pageSize, CancellationToken ct) => Ordered(() =>
    {
        var root = plan.Folder.EntryOn(plan.DeletingFrom) ?? throw new ArgumentException(null, nameof(plan));
        if (!root.IsFolder || root.Path.Length == 0) throw new ArgumentException(null, nameof(plan));
        var operation = ResourceNames.NewId();
        using var transaction = _connection.BeginTransaction();
        var pair = RequirePairRow(transaction, pairId);
        RequireOne(Execute(_connection, transaction, StateSchema.InsertFolder,
            (StateSchema.ParamPairId, pair), (StateSchema.ParamOperationId, operation),
            (StateSchema.ParamGeneration, 0),
            (StateSchema.ParamPhase, nameof(FolderRemovalPhase.Prepared)),
            (StateSchema.ParamDeletingSide, (int)plan.DeletingFrom),
            (StateSchema.ParamRootEntryJson, StateEncoding.Envelope(root)),
            (StateSchema.ParamSourceEvidenceJson, StateEncoding.Envelope<Entry?>(
                plan.Folder.EntryOn(PathNames.Other(plan.DeletingFrom)))),
            (StateSchema.ParamSavedRootJson, StateEncoding.OptionalEnvelope(plan.Folder.Synced)),
            (StateSchema.ParamRootContextJson, StateEncoding.Envelope(plan.Folder.Context)),
            (StateSchema.ParamCursor, null), (StateSchema.ParamActive, true), (StateSchema.ParamCause, null)));
        Add(0, plan.Folder.Key, root.Path, root, plan.Folder.Synced,
            plan.DeleteWhole ? FolderVerdict.Removed : FolderVerdict.Survives, false, plan.DeleteWhole);
        long previous = 0;
        foreach (var entry in plan.FrozenEvidence(pageSize, ct))
        {
            if (entry.Ordinal <= previous) throw new InvalidOperationException();
            previous = entry.Ordinal;
            Add(entry.Ordinal, entry.Key, entry.Entry?.Path ?? entry.Saved?.PathOn(plan.DeletingFrom) ??
                throw new InvalidDataException(), entry.Entry, entry.Saved, entry.Verdict, entry.FollowUp,
                !plan.DeleteWhole && entry.SelectedBranch);
        }
        if (plan.Folder.Synced is { } rootSaved) AddCounterpartSaved(rootSaved);
        transaction.Commit();
        return operation;

        // A saved binding under the counterpart root is inside the completion's other forget range even when its
        // deleting-side path is outside the plan; it joins as a saved-only member so the count guard agrees (7.6).
        void AddCounterpartSaved(SyncedEntry rootSaved)
        {
            var other = PathNames.Other(plan.DeletingFrom);
            var otherKey = rootSaved.KeyOn(other);
            if (otherKey.Length == 0) return;
            var rootKey = PathNames.KeyFor(plan.DeletingFrom, root.Path);
            var bounds = PathNames.DescendantBounds(otherKey);
            var take = Math.Max(StateSchema.OneRow, pageSize);
            Capture(other == Side.Dropbox ? StateSchema.SelectSyncedUnderInheritedKeyPaged :
                StateSchema.SelectSyncedUnderLocalKeyPaged);
            if (other == Side.Dropbox) Capture(StateSchema.SelectSyncedUnderDropboxKeyPaged);

            void Capture(string sql)
            {
                var after = bounds.Low;
                (string? Parent, SyncedEntry Saved, string Path) previousAnchor = (null, rootSaved, root.Path);
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var rows = ReadPage(transaction, sql, DecodeSynced,
                        (StateSchema.ParamPairRow, pair), (StateSchema.ParamHigh, bounds.High),
                        (StateSchema.ParamAfter, after), (StateSchema.ParamTake, take));
                    if (rows.Count == 0) break;
                    after = rows[^1].KeyOn(other);
                    foreach (var row in rows)
                    {
                        ct.ThrowIfCancellationRequested();
                        // Planned members already cover bindings under the deleting root.
                        if (PathNames.IsSelfOrUnderKey(row.KeyOn(plan.DeletingFrom), rootKey)) continue;
                        var anchor = rootSaved;
                        var anchorPath = root.Path;
                        var parent = plan.DeleteWhole ? null : PathNames.Parent(row.KeyOn(other));
                        if (!plan.DeleteWhole && parent == previousAnchor.Parent)
                        {
                            anchor = previousAnchor.Saved;
                            anchorPath = previousAnchor.Path;
                        }
                        else if (!plan.DeleteWhole)
                        {
                            foreach (var ancestor in PathNames.Ancestors(row.KeyOn(other)))
                            {
                                if (!PathNames.IsUnderKey(ancestor, otherKey)) break;
                                var saved = ReadOne(transaction, other == Side.Local ? StateSchema.SelectSyncedByLocalKey :
                                    StateSchema.SelectSyncedByDropboxKey, DecodeSynced,
                                    (StateSchema.ParamPair, pairId), (StateSchema.ParamTargetKey, ancestor));
                                if (saved is null) continue;
                                var branch = ReadOne(transaction, StateSchema.SelectedFolderBranch, StateEncoding.ReadFolderItem,
                                    (StateSchema.ParamPairRow, pair), (StateSchema.ParamOperation, operation),
                                    (StateSchema.ParamTargetKey, saved.KeyOn(plan.DeletingFrom)));
                                if (branch?.Saved != saved) continue;
                                anchor = saved;
                                anchorPath = branch.Path;
                                break;
                            }
                            previousAnchor = (parent, anchor, anchorPath);
                        }
                        var relative = other == Side.Dropbox
                            ? PathNames.RelativeUnderDropboxRoot(anchor.PathOn(other), row.PathOn(other))
                            : PathNames.RelativeUnderLocalRoot(anchor.PathOn(other), row.PathOn(other));
                        if (relative is null) continue;
                        var path = PathNames.Child(anchorPath, relative);
                        // Distinct saved-only identities need distinct in-scope paths, never physical effects.
                        while (ReadOne(transaction, StateSchema.FolderItemNative,
                            reader => StateEncoding.ReadBool(reader, StateSchema.FirstColumn),
                            (StateSchema.ParamPairRow, pair), (StateSchema.ParamOperation, operation),
                            (StateSchema.ParamTargetKey, PathNames.Key(path))))
                            path = PathNames.Child(path, row.LocalKey);
                        Add(++previous, PathNames.Key(path), path, null, row, FolderVerdict.Removed, false, false);
                    }
                }
            }
        }

        void Add(long ordinal, string key, string path, Entry? entry, SyncedEntry? saved,
            FolderVerdict verdict, bool followUp, bool selected)
        {
            var kind = !selected ? FolderEffectKind.None : entry is null ? FolderEffectKind.StateOnly :
                entry.IsFolder ? FolderEffectKind.RecursiveFolder : FolderEffectKind.ConditionalFile;
            RequireOne(Execute(_connection, transaction, StateSchema.InsertFolderItem,
                (StateSchema.ParamPairId, pair), (StateSchema.ParamOperationId, operation),
                (StateSchema.ParamOrdinal, ordinal), (StateSchema.ParamGeneration, 0),
                (StateSchema.ParamProvenance, nameof(FolderRemovalItem.ProvenanceKind.Planned)),
                (StateSchema.ParamPath, path), (StateSchema.ParamPolicyKey, key),
                (StateSchema.ParamExpectedJson, StateEncoding.OptionalEnvelope(entry)),
                (StateSchema.ParamSavedJson, StateEncoding.OptionalEnvelope(saved)),
                (StateSchema.ParamVerdict, verdict.ToString()), (StateSchema.ParamFollowUp, followUp),
                (StateSchema.ParamFollowUpState, (followUp ? FolderRemovalItem.FollowUpStatus.Pending :
                    FolderRemovalItem.FollowUpStatus.None).ToString()), (StateSchema.ParamSelectedBranch, selected),
                (StateSchema.ParamEffectKind, kind.ToString()),
                (StateSchema.ParamEffectState, (selected ? FolderRemovalItem.EffectStatus.Ready :
                    FolderRemovalItem.EffectStatus.None).ToString()),
                (StateSchema.ParamEffectGenerationColumn, 0),
                (StateSchema.ParamSubmissionId, null), (StateSchema.ParamSubmissionMember, null),
                (StateSchema.ParamEffectResultJson, null),
                (StateSchema.ParamPhase, nameof(FolderRemovalItem.RecoveryPhase.Planned)),
                (StateSchema.ParamSelectedJson, null), (StateSchema.ParamStageAllocationJson, null),
                (StateSchema.ParamStageJson, null), (StateSchema.ParamResultJson, null), (StateSchema.ParamCause, null)));
        }
    });

    // One folder-member insert: provenance and path checks, ordinal allocation and row insert are one transaction.
    /// <inheritdoc />
    public FolderRemovalItem InsertFolderMember(string pairId, string operationId, long expectedGeneration,
        FolderRemovalItem.ProvenanceKind provenance, string path, Entry? observed, Entry? selected) => Ordered(() =>
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (provenance is not (FolderRemovalItem.ProvenanceKind.Unexpected or FolderRemovalItem.ProvenanceKind.BlockingParent) ||
            observed is not null && observed.Path != path || selected is not null &&
            (selected.Path != path || selected.IsFolder || selected.Size < 0 || selected.Skip is not null ||
                string.IsNullOrEmpty(selected.Id) || string.IsNullOrEmpty(selected.Revision) ||
                string.IsNullOrEmpty(selected.ContentHash)))
            throw new ArgumentException(null, nameof(provenance));
        using var transaction = _connection.BeginTransaction();
        var pair = RequirePairRow(transaction, pairId);
        var operation = ReadFolder(transaction, pair, operationId);
        if (!operation.Active || operation.Generation != expectedGeneration)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
        var nativeKey = PathNames.Key(path);
        var root = PathNames.Key(operation.RootEntry.Path);
        if (!PathNames.IsSelfOrUnderKey(nativeKey, root) &&
            !(provenance == FolderRemovalItem.ProvenanceKind.BlockingParent && PathNames.IsUnderKey(root, nativeKey)))
            throw new ArgumentException(null, nameof(path));
        var existing = ReadOne(transaction, StateSchema.RecoveryMember, StateEncoding.ReadFolderItem,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamOperation, operationId),
            (StateSchema.ParamTargetKey, nativeKey), (StateSchema.ParamProvenance, provenance.ToString()));
        if (existing is not null)
        {
            if (existing.Path != path || existing.Expected != observed || selected is not null && existing.Selected != selected)
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
            return existing;
        }
        var ordinal = checked(ReadOne(transaction, StateSchema.LastFolderOrdinal,
            static reader => reader.GetInt64(StateSchema.FirstColumn),
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamOperation, operationId)) + 1);
        RequireOne(Execute(_connection, transaction, StateSchema.InsertFolderItem,
            (StateSchema.ParamPairId, pair), (StateSchema.ParamOperationId, operationId),
            (StateSchema.ParamOrdinal, ordinal), (StateSchema.ParamGeneration, 0),
            (StateSchema.ParamProvenance, provenance.ToString()), (StateSchema.ParamPath, path),
            (StateSchema.ParamPolicyKey, PathNames.KeyFor(operation.DeletingSide, path)),
            (StateSchema.ParamExpectedJson, StateEncoding.OptionalEnvelope(observed)), (StateSchema.ParamSavedJson, null),
            (StateSchema.ParamVerdict, nameof(FolderVerdict.Survives)), (StateSchema.ParamFollowUp, false),
            (StateSchema.ParamFollowUpState, nameof(FolderRemovalItem.FollowUpStatus.None)),
            (StateSchema.ParamSelectedBranch, false), (StateSchema.ParamEffectKind, nameof(FolderEffectKind.None)),
            (StateSchema.ParamEffectState, nameof(FolderRemovalItem.EffectStatus.None)),
            (StateSchema.ParamEffectGenerationColumn, 0), (StateSchema.ParamSubmissionId, null),
            (StateSchema.ParamSubmissionMember, null), (StateSchema.ParamEffectResultJson, null),
            (StateSchema.ParamPhase, (selected is null ? FolderRemovalItem.RecoveryPhase.Planned :
                FolderRemovalItem.RecoveryPhase.RecoverySelected).ToString()),
            (StateSchema.ParamSelectedJson, StateEncoding.OptionalEnvelope(selected)),
            (StateSchema.ParamStageAllocationJson, null), (StateSchema.ParamStageJson, null),
            (StateSchema.ParamResultJson, null), (StateSchema.ParamCause, null)));
        var result = ReadFolderItem(transaction, pair, operationId, ordinal);
        transaction.Commit();
        return result;
    });

    /// <inheritdoc />
    public FolderRemovalState FolderRemoval(string pairId, string operationId)
    {
        lock (_lock) return ReadFolder(null, RequirePairRow(null, pairId), operationId);
    }

    private FolderRemovalState ReadFolder(SqliteTransaction? transaction, long pair, string operationId)
    {
        var arguments = new (string Name, object? Value)[]
        {
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamOperation, operationId)
        };
        var disposition = ReadOne(transaction, StateSchema.ReadFolderDisposition,
            static reader => StateEncoding.ReadOptionalEnum<FolderRetirementDisposition>(reader,
                StateSchema.DispositionRetirementDisposition), arguments);
        return ReadOne(transaction, StateSchema.FolderRead,
            reader => StateEncoding.ReadFolderRemoval(reader, disposition), arguments) ??
            throw new InvalidOperationException();
    }

    private T? ReadOne<T>(SqliteTransaction? transaction, string sql, Func<SqliteDataReader, T> decode,
        params (string Name, object? Value)[] arguments)
    {
        using var command = StorageCommand.Create(_connection, transaction, sql, arguments);
        using var reader = command.ExecuteReader();
        return reader.Read() ? decode(reader) : default;
    }

    private FolderRemovalItem ReadFolderItem(SqliteTransaction? transaction, long pair,
        string operationId, long ordinal) =>
        ReadOne(transaction, StateSchema.FolderItem, StateEncoding.ReadFolderItem,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamOperation, operationId),
            (StateSchema.ParamOrdinal, ordinal)) ?? throw new InvalidOperationException();

    /// <inheritdoc />
    public IEnumerable<FolderRemovalState> ActiveFolderRemovals(string pairId, CancellationToken ct)
    {
        if (_legacy) yield break;
        long? pair;
        lock (_lock) pair = PairRow(null, pairId);
        if (pair is null) yield break;
        var after = string.Empty;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var next = ReadMetadata(StateSchema.ActiveFolders,
                static reader => StateEncoding.ReadFolderRemoval(reader, null),
                (StateSchema.ParamPairRow, pair.Value), (StateSchema.ParamAfterOperation, after),
                (StateSchema.ParamTake, StateSchema.OneRow));
            if (next is null) yield break;
            after = next.OperationId;
            yield return next;
        }
    }

    /// <inheritdoc />
    public IEnumerable<FolderRemovalEffect> ReadyFolderEffects(string pairId, string operationId,
        long afterOrdinal, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        lock (_lock)
        {
            var pair = RequirePairRow(null, pairId);
            var arguments = new List<(string Name, object? Value)>
            {
                (StateSchema.ParamPairRow, pair), (StateSchema.ParamOperation, operationId),
                (StateSchema.ParamTake, pageSize), (StateSchema.ParamAfter, afterOrdinal)
            };
            if (afterOrdinal >= 0)
            {
                using var command = StorageCommand.Create(_connection, null, StateSchema.ReadyBoundary,
                    arguments.ToArray());
                using var reader = command.ExecuteReader();
                if (!reader.Read()) throw new InvalidOperationException();
                arguments.Add((StateSchema.ParamNegativeDepth, reader.GetInt64(StateSchema.ReadyBoundaryNegativeDepth)));
                arguments.Add((StateSchema.ParamPolicy, reader.GetString(StateSchema.ReadyBoundaryPolicyKey)));
                arguments.Add((StateSchema.ParamNative, reader.GetString(StateSchema.ReadyBoundaryNativeKey)));
                arguments.Add((StateSchema.PlanParamPresent, reader.GetInt64(StateSchema.ReadyBoundaryPresent)));
            }
            return ReadPage(null, afterOrdinal < 0 ? StateSchema.ReadyFirst : StateSchema.ReadyNext,
                static reader => StateEncoding.ReadFolderEffect(reader, StateSchema.FirstColumn,
                    reader.GetInt64(StateSchema.ReadyDeletingSide) == (int)Side.Dropbox),
                arguments.ToArray());
        }
    }

    private IReadOnlyList<T> ReadPage<T>(SqliteTransaction? transaction, string sql,
        Func<SqliteDataReader, T> decode, params (string Name, object? Value)[] arguments)
    {
        var result = new List<T>();
        using var command = StorageCommand.Create(_connection, transaction, sql, arguments);
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(decode(reader));
        return result;
    }

    /// <inheritdoc />
    public IEnumerable<FolderRemovalItem> PendingFolderFollowUps(string pairId, string operationId,
        long afterOrdinal, int pageSize) => FolderItemPage(pairId, operationId, afterOrdinal, pageSize,
            StateSchema.PendingFollowUps);

    /// <inheritdoc />
    public void CompleteFolderFollowUp(string pairId, string operationId, long ordinal, long expectedGeneration) =>
        Write(() =>
        {
            using var transaction = _connection.BeginTransaction();
            var pair = RequirePairRow(transaction, pairId);
            if (!ReadFolder(transaction, pair, operationId).Active) throw new InvalidOperationException();
            RequireOne(Execute(_connection, transaction, StateSchema.CompleteFollowUp,
                (StateSchema.ParamPairRow, pair), (StateSchema.ParamOperation, operationId),
                (StateSchema.ParamOrdinal, ordinal), (StateSchema.ParamGeneration, expectedGeneration),
                (StateSchema.ParamNextGeneration, checked(expectedGeneration + 1))));
            transaction.Commit();
        });

    /// <inheritdoc />
    public IEnumerable<FolderRemovalItem> FolderRemovalItems(string pairId, string operationId,
        long afterOrdinal, int pageSize) => FolderItemPage(pairId, operationId, afterOrdinal, pageSize,
            StateSchema.FolderItems);

    /// <inheritdoc />
    public FolderRemovalItem? FolderRemovalItemByKey(string pairId, string operationId, string key)
    {
        lock (_lock)
            return ReadOne(null, StateSchema.FolderItemsInScope, StateEncoding.ReadFolderItem,
                (StateSchema.ParamPairRow, RequirePairRow(null, pairId)), (StateSchema.ParamOperation, operationId),
                (StateSchema.ParamTargetKey, key), (StateSchema.ParamLow, key), (StateSchema.ParamHigh, key),
                (StateSchema.ParamAfter, StateSchema.BeforeFirstOrdinal), (StateSchema.ParamTake, StateSchema.OneRow));
    }

    private IReadOnlyList<FolderRemovalItem> FolderItemPage(string pairId, string operationId,
        long afterOrdinal, int pageSize, string sql)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        lock (_lock)
            return ReadPage(null, sql, StateEncoding.ReadFolderItem,
                (StateSchema.ParamPairRow, RequirePairRow(null, pairId)),
                (StateSchema.ParamOperation, operationId), (StateSchema.ParamAfter, afterOrdinal),
                (StateSchema.ParamTake, pageSize));
    }

    /// <inheritdoc />
    public void AdvanceFolderRemoval(string pairId, string operationId, long expectedGeneration,
        FolderRemovalPhase expectedPhase, FolderRemovalState next) => Write(() =>
    {
        using var transaction = _connection.BeginTransaction();
        var pair = RequirePairRow(transaction, pairId);
        var current = ReadFolder(transaction, pair, operationId);
        if (!current.Active || current.Generation != expectedGeneration || current.Phase != expectedPhase ||
            next.Generation != checked(expectedGeneration + 1) || !next.Active ||
            next.Phase == FolderRemovalPhase.Complete ||
            next with { Generation = current.Generation, Phase = current.Phase, Cursor = current.Cursor,
                Cause = current.Cause } != current)
            throw new InvalidOperationException();
        RequireOne(Execute(_connection, transaction, StateSchema.AdvanceFolder,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamOperation, operationId),
            (StateSchema.ParamGeneration, expectedGeneration), (StateSchema.ParamPhase, expectedPhase.ToString()),
            (StateSchema.ParamNextGeneration, next.Generation), (StateSchema.ParamNextPhase, next.Phase.ToString()),
            (StateSchema.ParamCursor, next.Cursor), (StateSchema.ParamCause, next.Cause?.ToString())));
        transaction.Commit();
    });

    /// <inheritdoc />
    public void SettleFolderRemovalItem(string pairId, string operationId, long ordinal,
        long expectedGeneration, FolderRemovalItem next, StateChange verifiedChange)
    {
        if (next.Ordinal != ordinal) throw new InvalidOperationException();
        if (!SettleFolderRemovalItems(pairId, operationId, [(expectedGeneration, next, verifiedChange)])[0])
            throw new FolderStateRefusedException();
    }

    /// <inheritdoc />
    public IReadOnlyList<bool> SettleFolderRemovalItems(string pairId, string operationId,
        IReadOnlyList<(long ExpectedGeneration, FolderRemovalItem Next, StateChange Change)> items) => Ordered(() =>
    {
        if (items.Count == 0) return (IReadOnlyList<bool>)Array.Empty<bool>();
        using var transaction = _connection.BeginTransaction();
        var pair = RequirePairRow(transaction, pairId);
        var operation = ReadFolder(transaction, pair, operationId);
        var settled = new bool[items.Count];
        for (var index = 0; index < items.Count; index++)
        {
            var (generation, next, change) = items[index];
            var current = ReadFolderItem(transaction, pair, operationId, next.Ordinal);
            bool settling;
            try
            {
                settling = ValidateFolderSettlement(transaction, pair, pairId, operation, current, generation, next, change);
            }
            catch (FolderStateRefusedException)
            {
                continue;
            }
            ApplyFolderSettlement(transaction, pair, pairId, operationId, current, generation, next, change, settling);
            if (settling && operation.DeletingSide == Side.Local &&
                current.EffectState == FolderRemovalItem.EffectStatus.Verified &&
                current.EffectKind != FolderEffectKind.StateOnly)
            {
                var key = PathNames.DropboxKey(current.Path);
                if (!change.ForgetSelf.Any(item => item.Side == Side.Local && item.Key == key))
                    DeletedHistory.Spend(_connection, transaction, pairId, key);
            }
            settled[index] = true;
        }
        transaction.Commit();
        for (var index = 0; index < items.Count; index++)
            if (settled[index]) UpdateClosures(pairId, items[index].Change);
        return settled;
    });

    private bool ValidateFolderSettlement(SqliteTransaction transaction, long pair, string pairId,
        FolderRemovalState operation, FolderRemovalItem current, long expectedGeneration,
        FolderRemovalItem next, StateChange verifiedChange)
    {
        var settling = next.EffectState == FolderRemovalItem.EffectStatus.Settled &&
            current.EffectState != FolderRemovalItem.EffectStatus.Settled;
        if (!operation.Active || current.Generation != expectedGeneration) throw new FolderStateRefusedException();
        if (current.IsTerminal && !next.IsTerminal ||
            next.Generation != checked(expectedGeneration + 1) ||
            next with { Generation = current.Generation, EffectGeneration = current.EffectGeneration,
                EffectState = current.EffectState, Phase = current.Phase, Selected = current.Selected,
                StageAllocation = current.StageAllocation, Stage = current.Stage, Result = current.Result,
                Cause = current.Cause } != current ||
            next.EffectGeneration != (settling ? checked(current.EffectGeneration + 1) : current.EffectGeneration) ||
            !settling && next.EffectState != current.EffectState)
            throw new InvalidOperationException();
        if (settling && (next.StageAllocation is not null || next.Stage is not null ||
            next.Phase != FolderRemovalItem.RecoveryPhase.Settled ||
            current.EffectState is not (FolderRemovalItem.EffectStatus.Verified or FolderRemovalItem.EffectStatus.NoEffect) &&
                current.EffectKind != FolderEffectKind.StateOnly)) throw new InvalidOperationException();
        if (current.StageAllocation is not null && next.StageAllocation is null &&
            current.Phase is not (FolderRemovalItem.RecoveryPhase.DiscardArmed or FolderRemovalItem.RecoveryPhase.AllocateArmed))
            throw new InvalidOperationException();
        ValidateFolderChange(transaction, pair, pairId, operation, current.Path, current.Saved, verifiedChange);
        return settling;
    }

    private void ApplyFolderSettlement(SqliteTransaction transaction, long pair, string pairId, string operationId,
        FolderRemovalItem current, long expectedGeneration, FolderRemovalItem next, StateChange verifiedChange, bool settling)
    {
        var ordinal = current.Ordinal;
        // The effect update already writes phase and generation; recovery payloads only need writing when changed.
        if (!settling || next.Selected != current.Selected || next.StageAllocation != current.StageAllocation ||
            next.Stage != current.Stage || next.Result != current.Result)
            RequireOne(Execute(_connection, transaction, StateSchema.AdvanceRecovery,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamOperation, operationId),
            (StateSchema.ParamOrdinal, ordinal), (StateSchema.ParamGeneration, expectedGeneration),
            (StateSchema.ParamPhase, current.Phase.ToString()),
            (StateSchema.ParamNextGeneration, settling ? expectedGeneration : next.Generation),
            (StateSchema.ParamNextPhase, next.Phase.ToString()),
            (StateSchema.ParamSelected, StateEncoding.OptionalEnvelope(next.Selected)),
            (StateSchema.ParamAllocation, StateEncoding.OptionalEnvelope(next.StageAllocation)),
            (StateSchema.ParamStage, StateEncoding.OptionalEnvelope(next.Stage)),
            (StateSchema.ParamResult, StateEncoding.OptionalEnvelope(next.Result)),
            (StateSchema.ParamCause, next.Cause?.ToString())));
        if (settling)
            RequireOne(Execute(_connection, transaction, StateSchema.SettleEffect,
                (StateSchema.ParamPairRow, pair), (StateSchema.ParamOperation, operationId),
                (StateSchema.ParamOrdinal, ordinal), (StateSchema.ParamGeneration, expectedGeneration),
                (StateSchema.ParamEffectGeneration, current.EffectGeneration),
                (StateSchema.ParamState, current.EffectState.ToString()),
                (StateSchema.ParamNextGeneration, next.Generation),
                (StateSchema.ParamNextEffectGeneration, next.EffectGeneration),
                (StateSchema.ParamCause, next.Cause?.ToString())));
        StateCommitter.Commit(_connection, transaction, _changes, pairId, verifiedChange, DateTimeOffset.UtcNow,
            narrowRecordUpdates: true, raised: null);
    }

    /// <inheritdoc />
    public void CompleteFolderRemoval(string pairId, string operationId, long expectedGeneration,
        StateChange verifiedRootChange, WorkSelection? selection) => Write(() =>
    {
        using var transaction = _connection.BeginTransaction();
        var pair = RequirePairRow(transaction, pairId);
        var operation = ReadFolder(transaction, pair, operationId);
        if (!operation.Active || operation.Generation != expectedGeneration ||
            operation.Phase is not (FolderRemovalPhase.Checking or FolderRemovalPhase.Recovering) ||
            ReadOne(transaction, StateSchema.FolderHasUnsettled,
                static reader => StateEncoding.ReadBool(reader, StateSchema.FirstColumn),
                (StateSchema.ParamPairRow, pair), (StateSchema.ParamOperation, operationId)))
            throw new FolderStateRefusedException();
        foreach (var item in FolderMembers(transaction, pair, operationId))
            if (item.Verdict == FolderVerdict.Survives || item.Result is not null ||
                item.Phase is FolderRemovalItem.RecoveryPhase.Unknown or FolderRemovalItem.RecoveryPhase.Conflict)
                throw new FolderStateRefusedException();
        using (var submissions = StorageCommand.Create(_connection, transaction, StateSchema.OperationSubmissions,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamOperation, operationId)))
        using (var reader = submissions.ExecuteReader())
            while (reader.Read())
                if (ReadSubmission(transaction, reader.GetString(StateSchema.FirstColumn)).Submission.Phase !=
                    Massdex.SyncShark.Engine.FolderSubmission.SubmissionPhase.Terminal) throw new FolderStateRefusedException();
        ValidateFolderChange(transaction, pair, pairId, operation, operation.RootEntry.Path,
            operation.SavedRoot, verifiedRootChange);
        if (selection is not null) _changes.AdvanceBefore(transaction, pairId, selection, verifiedRootChange);
        StateCommitter.Commit(_connection, transaction, _changes, pairId, verifiedRootChange, DateTimeOffset.UtcNow,
            narrowRecordUpdates: true, raised: null);
        RequireOne(Execute(_connection, transaction, StateSchema.TerminalFolder,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamOperation, operationId),
            (StateSchema.ParamGeneration, expectedGeneration),
            (StateSchema.ParamNextGeneration, checked(expectedGeneration + 1)),
            (StateSchema.ParamNextPhase, nameof(FolderRemovalPhase.Complete)), (StateSchema.ParamCause, null)));
        transaction.Commit();
        UpdateClosures(pairId, verifiedRootChange);
    });

    private void ValidateFolderChange(SqliteTransaction transaction, long pair, string pairId,
        FolderRemovalState operation, string path, SyncedEntry? saved, StateChange change)
    {
        if (change.Renames.Count != 0 || change.ForgetBothSubtrees.Count != 0) throw new ArgumentException(null, nameof(change));
        foreach (var record in change.Record)
            if (record.PathOn(operation.DeletingSide) != path) throw new ArgumentException(null, nameof(change));
        foreach (var root in change.ForgetSelf.Concat(change.ForgetSubtrees))
            if (root.Key != (saved?.KeyOn(root.Side) ?? PathNames.KeyFor(root.Side, path)))
                throw new ArgumentException(null, nameof(change));
        if (change.Record.Count == 0 && change.ForgetSelf.Count == 0 && change.ForgetSubtrees.Count == 0) return;
        if (saved is null)
        {
            foreach (var record in change.Record)
                if (ReadOne(transaction, StateSchema.SelectSyncedByLocalKey, DecodeSynced,
                        (StateSchema.ParamPair, pairId), (StateSchema.ParamTargetKey, record.LocalKey)) is not null ||
                    ReadOne(transaction, StateSchema.SelectSyncedByDropboxKey, DecodeSynced,
                        (StateSchema.ParamPair, pairId), (StateSchema.ParamTargetKey, record.DropboxKey)) is not null)
                    throw new FolderStateRefusedException();
            foreach (var root in change.ForgetSelf)
                if (ReadOne(transaction, root.Side == Side.Local ? StateSchema.SelectSyncedByLocalKey :
                    StateSchema.SelectSyncedByDropboxKey, DecodeSynced,
                    (StateSchema.ParamPair, pairId), (StateSchema.ParamTargetKey, root.Key)) is not null)
                    throw new FolderStateRefusedException();
        }
        if (change.ForgetSubtrees.Count == 0)
        {
            if (saved is not null && !Verify(saved) && change.Record.Count != 0) throw new FolderStateRefusedException();
            return;
        }
        var key = PathNames.KeyFor(operation.DeletingSide, path);
        var bounds = PathNames.DescendantBounds(key);
        var counts = new long[change.ForgetSelf.Count + change.ForgetSubtrees.Count];
        using (var command = StorageCommand.Create(_connection, transaction, StateSchema.FolderItemsInScope,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamOperation, operation.OperationId),
            (StateSchema.ParamTargetKey, key), (StateSchema.ParamLow, bounds.Low), (StateSchema.ParamHigh, bounds.High),
            (StateSchema.ParamAfter, StateSchema.BeforeFirstOrdinal), (StateSchema.ParamTake, long.MaxValue)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var item = StateEncoding.ReadFolderItem(reader);
                var present = item.Saved is { } before &&
                    (change.ForgetSelf.Any(root => PathNames.Comparer.Equals(before.KeyOn(root.Side), root.Key)) ||
                     change.ForgetSubtrees.Any(root => PathNames.IsSelfOrUnderKey(before.KeyOn(root.Side), root.Key))) &&
                    Verify(before);
                var index = 0;
                foreach (var root in change.ForgetSelf)
                {
                    Check(root.Side, root.Key, false, item, index, present);
                    index++;
                }
                foreach (var root in change.ForgetSubtrees)
                {
                    Check(root.Side, root.Key, true, item, index, present);
                    index++;
                }
            }
        }
        var countIndex = 0;
        foreach (var root in change.ForgetSelf)
        {
            if (Count(root.Side, root.Key, false) != counts[countIndex]) throw new FolderStateRefusedException();
            countIndex++;
        }
        foreach (var root in change.ForgetSubtrees)
        {
            if (Count(root.Side, root.Key, true) != counts[countIndex]) throw new FolderStateRefusedException();
            countIndex++;
        }
        if (saved is not null && change.Record.Count != 0) Verify(saved);

        void Check(Side side, string root, bool subtree, FolderRemovalItem item, int index, bool present)
        {
            if (!present || item.Saved is not { } before || !(subtree
                ? PathNames.IsSelfOrUnderKey(before.KeyOn(side), root) : PathNames.Comparer.Equals(before.KeyOn(side), root)))
                return;
            if (item.Verdict != FolderVerdict.Removed || item.FollowUpState == FolderRemovalItem.FollowUpStatus.Pending ||
                item.StageAllocation is not null || item.Stage is not null)
                throw new InvalidOperationException();
            counts[index]++;
        }

        bool Verify(SyncedEntry before)
        {
            var local = ReadOne(transaction, StateSchema.SelectSyncedByLocalKey, DecodeSynced,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamTargetKey, before.LocalKey));
            var dropbox = ReadOne(transaction, StateSchema.SelectSyncedByDropboxKey, DecodeSynced,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamTargetKey, before.DropboxKey));
            if (local is null && dropbox is null) return false;
            if (local != before || dropbox != before) throw new FolderStateRefusedException();
            return true;
        }

        long Count(Side side, string root, bool subtree)
        {
            long total = 0;
            CountQuery(side == Side.Local ? StateSchema.SelectSyncedByLocalKey : StateSchema.SelectSyncedByDropboxKey);
            if (subtree) CountQuery(side == Side.Local ? StateSchema.SelectSyncedUnderLocalKey : StateSchema.SelectSyncedUnderDropboxKey);
            return total;

            void CountQuery(string sql)
            {
                using var query = StorageCommand.Create(_connection, transaction, sql,
                    (StateSchema.ParamPair, pairId), (StateSchema.ParamTargetKey, root));
                using var rows = query.ExecuteReader();
                while (rows.Read()) total++;
            }
        }
    }

    /// <inheritdoc />
    public void RetireFolderRemoval(string pairId, string operationId, long expectedGeneration,
        FolderRetirementDisposition disposition, StateChange issueOnlyAcknowledgement) => Write(() =>
    {
        RequireIssueOnly(issueOnlyAcknowledgement);
        using var transaction = _connection.BeginTransaction();
        var pair = RequirePairRow(transaction, pairId);
        var current = ReadFolder(transaction, pair, operationId);
        if (!current.Active || current.Generation != expectedGeneration) throw new InvalidOperationException();
        if (disposition != FolderRetirement.For(FolderMembers(transaction, pair, operationId)))
            throw new InvalidOperationException();
        RequireOne(Execute(_connection, transaction, StateSchema.RetireFolderDisposition,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamOperation, operationId),
            (StateSchema.ParamGeneration, expectedGeneration),
            (StateSchema.ParamNextGeneration, checked(expectedGeneration + 1)),
            (StateSchema.ParamNextPhase, (current.Phase == FolderRemovalPhase.Unknown ? FolderRemovalPhase.Unknown :
                FolderRemovalPhase.Retained).ToString()), (StateSchema.ParamDisposition, disposition.ToString()),
            (StateSchema.ParamCause, current.Cause?.ToString())));
        StateCommitter.Commit(_connection, transaction, _changes, pairId, issueOnlyAcknowledgement, DateTimeOffset.UtcNow,
            narrowRecordUpdates: true, raised: null);
        transaction.Commit();
        UpdateClosures(pairId, issueOnlyAcknowledgement);
    });

    private IEnumerable<FolderRemovalItem> FolderMembers(SqliteTransaction transaction, long pair, string operationId)
    {
        long after = StateSchema.BeforeFirstOrdinal;
        while (true)
        {
            var item = ReadOne(transaction, StateSchema.FolderItems, StateEncoding.ReadFolderItem,
                (StateSchema.ParamPairRow, pair), (StateSchema.ParamOperation, operationId),
                (StateSchema.ParamAfter, after), (StateSchema.ParamTake, StateSchema.OneRow));
            if (item is null) yield break;
            after = item.Ordinal;
            yield return item;
        }
    }

    // One effect batch: validation, key sort and member arms commit in one transaction.
    /// <inheritdoc />
    public string ArmFolderSubmission(string pairId, IReadOnlyList<FolderRemovalEffect> effects) => Ordered(() =>
    {
        if (effects.Count == 0 || effects.Count > DropboxApiLimits.BatchMaxEntries)
            throw new ArgumentOutOfRangeException(nameof(effects));
        using var transaction = _connection.BeginTransaction();
        var pair = RequirePairRow(transaction, pairId);
        var members = new List<FolderRemovalItem>(effects.Count);
        var keys = new List<string>(effects.Count);
        Side? side = null;
        foreach (var effect in effects)
        {
            var operation = ReadFolder(transaction, pair, effect.OperationId);
            var item = ReadFolderItem(transaction, pair, effect.OperationId, effect.ItemOrdinal);
            side ??= operation.DeletingSide;
            if (!operation.Active || operation.DeletingSide != side || item.IsTerminal || item.SubmissionId is not null ||
                item.EffectState != FolderRemovalItem.EffectStatus.Ready || item.EffectGeneration != effect.EffectGeneration ||
                item.Expected != effect.Expected || item.EffectKind != effect.Kind ||
                effect.Kind is not (FolderEffectKind.RecursiveFolder or FolderEffectKind.ConditionalFile) ||
                effect.Expected is null) throw new InvalidOperationException();
            members.Add(item);
            keys.Add(PathNames.KeyFor(operation.DeletingSide, effect.Expected.Path));
        }
        keys.Sort(StringComparer.Ordinal);
        for (var index = 0; index < keys.Count; index++)
            if (index > 0 && PathNames.IsSelfOrUnderKey(keys[index], keys[index - 1]))
                throw new InvalidOperationException();
        var submission = ResourceNames.NewId();
        RequireOne(Execute(_connection, transaction, StateSchema.InsertSubmission,
            (StateSchema.ParamSubmission, submission), (StateSchema.ParamPairRow, pair)));
        for (var index = 0; index < members.Count; index++)
        {
            var item = members[index];
            RequireOne(Execute(_connection, transaction, StateSchema.ArmEffect,
                (StateSchema.ParamPairRow, pair), (StateSchema.ParamOperation, item.OperationId),
                (StateSchema.ParamOrdinal, item.Ordinal), (StateSchema.ParamGeneration, item.Generation),
                (StateSchema.ParamNextGeneration, checked(item.Generation + 1)),
                (StateSchema.ParamEffectGeneration, item.EffectGeneration),
                (StateSchema.ParamNextEffectGeneration, checked(item.EffectGeneration + 1)),
                (StateSchema.ParamSubmission, submission), (StateSchema.ParamMember, index)));
        }
        transaction.Commit();
        return submission;
    });

    /// <inheritdoc />
    public FolderSubmission FolderSubmission(string submissionId)
    {
        lock (_lock) return ReadSubmission(null, submissionId).Submission;
    }

    private (long Pair, FolderSubmission Submission) ReadSubmission(SqliteTransaction? transaction, string submissionId)
    {
        long pair;
        FolderSubmission submission;
        using (var command = StorageCommand.Create(_connection, transaction, StateSchema.SubmissionRead,
            (StateSchema.ParamSubmission, submissionId)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidOperationException();
            pair = reader.GetInt64(StateSchema.SubmissionPairId);
            submission = StateEncoding.ReadFolderSubmission(reader);
        }
        var pairId = ReadOne(transaction, StateSchema.PairIdByRow,
            static reader => reader.GetString(StateSchema.FirstColumn), (StateSchema.ParamPairRow, pair)) ??
            throw new InvalidDataException();
        return (pair, submission with { PairId = pairId });
    }

    /// <inheritdoc />
    public IReadOnlyList<FolderRemovalEffect> FolderSubmissionEffects(string submissionId)
    {
        lock (_lock) return ReadSubmissionEffects(null, submissionId);
    }

    private IReadOnlyList<FolderRemovalEffect> ReadSubmissionEffects(SqliteTransaction? transaction, string submissionId)
    {
        var results = new List<FolderRemovalEffect>();
        using var command = StorageCommand.Create(_connection, transaction, StateSchema.SubmissionEffects,
            (StateSchema.ParamSubmission, submissionId));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetInt64(StateSchema.SubmissionEffectSubmissionMember) != results.Count ||
                results.Count >= DropboxApiLimits.BatchMaxEntries) throw new InvalidDataException();
            results.Add(StateEncoding.ReadFolderEffect(reader, StateSchema.FirstColumn,
                reader.GetInt64(StateSchema.SubmissionEffectDeletingSide) == (int)Side.Dropbox));
        }
        if (results.Count == 0) throw new InvalidDataException();
        return results;
    }

    /// <inheritdoc />
    public void AdvanceFolderSubmission(FolderSubmission expected, FolderSubmission next,
        IReadOnlyList<FolderMemberResult> results) => Write(() =>
    {
        using var transaction = _connection.BeginTransaction();
        var (pair, current) = ReadSubmission(transaction, expected.SubmissionId);
        if (current != expected) throw new InvalidOperationException();
        AdvanceSubmission(transaction, pair, expected, next, results);
        ReconcileSubmission(transaction, pair, expected, next);
        transaction.Commit();
        _closureKeys?.Remove(expected.PairId);
    });

    // One submission advance: generation and phase checks, member effect updates and the submission row update are one transaction.
    private void AdvanceSubmission(SqliteTransaction transaction, long pair, FolderSubmission expected,
        FolderSubmission next, IReadOnlyList<FolderMemberResult> results)
    {
        if (next.SubmissionId != expected.SubmissionId || next.PairId != expected.PairId ||
            next.Generation != checked(expected.Generation + 1) ||
            expected.Phase == Massdex.SyncShark.Engine.FolderSubmission.SubmissionPhase.Terminal ||
            expected.JobId is not null && next.JobId != expected.JobId ||
            next.Phase == Massdex.SyncShark.Engine.FolderSubmission.SubmissionPhase.Armed ||
            next.Phase == Massdex.SyncShark.Engine.FolderSubmission.SubmissionPhase.Accepted && string.IsNullOrEmpty(next.JobId) ||
            next.Phase != Massdex.SyncShark.Engine.FolderSubmission.SubmissionPhase.Terminal && results.Count != 0)
            throw new ArgumentException(null, nameof(next));
        if (next.Phase != expected.Phase)
        {
        var effects = ReadSubmissionEffects(transaction, expected.SubmissionId);
        var terminal = next.Phase == Massdex.SyncShark.Engine.FolderSubmission.SubmissionPhase.Terminal;
        var outcomes = terminal ? results.ToLookup(result => result.Ordinal) : null;
        for (var index = 0; index < effects.Count; index++)
        {
            var effect = effects[index];
            if (!ReadFolder(transaction, pair, effect.OperationId).Active) throw new InvalidOperationException();
            var item = ReadFolderItem(transaction, pair, effect.OperationId, effect.ItemOrdinal);
            // A terminal choice owns this member; a late receipt still advances its unsettled siblings.
            if (item.IsTerminal) continue;
            if (item.SubmissionId != expected.SubmissionId || item.SubmissionMember != index ||
                item.EffectState is not (FolderRemovalItem.EffectStatus.Armed or FolderRemovalItem.EffectStatus.Accepted or
                    FolderRemovalItem.EffectStatus.Unknown)) throw new InvalidOperationException();
            FolderMemberResult? result = null;
            if (terminal)
            {
                using var candidates = outcomes![index].GetEnumerator();
                if (results.Count == effects.Count && candidates.MoveNext()) result = candidates.Current;
                if (result is null || candidates.MoveNext()) result = new FolderMemberResult.Unknown(index, next.Cause);
            }
            var state = next.Phase == Massdex.SyncShark.Engine.FolderSubmission.SubmissionPhase.Accepted ? FolderRemovalItem.EffectStatus.Accepted :
                result is FolderMemberResult.Receipt ? FolderRemovalItem.EffectStatus.Verified :
                result is FolderMemberResult.KnownNoEffect ? FolderRemovalItem.EffectStatus.NoEffect :
                FolderRemovalItem.EffectStatus.Unknown;
            var phase = result is FolderMemberResult.Receipt ? FolderRemovalItem.RecoveryPhase.Deleted :
                state == FolderRemovalItem.EffectStatus.Unknown ? FolderRemovalItem.RecoveryPhase.Unknown : item.Phase;
            RequireOne(Execute(_connection, transaction, StateSchema.AdvanceEffect,
                (StateSchema.ParamPairRow, pair), (StateSchema.ParamOperation, item.OperationId),
                (StateSchema.ParamOrdinal, item.Ordinal), (StateSchema.ParamGeneration, item.Generation),
                (StateSchema.ParamNextGeneration, checked(item.Generation + 1)),
                (StateSchema.ParamEffectGeneration, item.EffectGeneration),
                (StateSchema.ParamNextEffectGeneration, checked(item.EffectGeneration + 1)),
                (StateSchema.ParamState, item.EffectState.ToString()), (StateSchema.ParamNextState, state.ToString()),
                (StateSchema.ParamNextPhase, phase.ToString()), (StateSchema.ParamReceipt, StateEncoding.OptionalEnvelope(result)),
                (StateSchema.ParamCause, next.Cause?.ToString()), (StateSchema.ParamSubmission, expected.SubmissionId),
                (StateSchema.ParamMember, index)));
        }
        }
        RequireOne(Execute(_connection, transaction, StateSchema.AdvanceSubmission,
            (StateSchema.ParamSubmission, expected.SubmissionId), (StateSchema.ParamPairRow, pair),
            (StateSchema.ParamGeneration, expected.Generation), (StateSchema.ParamPhase, expected.Phase.ToString()),
            (StateSchema.ParamNextGeneration, next.Generation), (StateSchema.ParamNextPhase, next.Phase.ToString()),
            (StateSchema.ParamJob, next.JobId), (StateSchema.ParamCause, next.Cause?.ToString())));
    }

    private void ReconcileSubmission(SqliteTransaction transaction, long pair, FolderSubmission before,
        FolderSubmission after)
    {
        if (!InvalidContribution(before) && !InvalidContribution(after)) return;
        var paths = ReadPage(transaction, StateSchema.FolderSubmissionRoots,
            static reader => reader.GetString(StateSchema.SubmissionRootPath),
            (StateSchema.ParamSubmission, before.SubmissionId));
        foreach (var path in paths)
            IssueTable.ReconcileInvalidFolderJob(_connection, transaction, before.PairId, pair, path);
    }

    private static bool InvalidContribution(FolderSubmission submission) =>
        submission.Phase == Massdex.SyncShark.Engine.FolderSubmission.SubmissionPhase.Accepted &&
        submission.Cause == TextKey.FailDropboxDeleteJobInvalid;

    /// <inheritdoc />
    public void RecordFolderJobObservation(FolderSubmission expected,
        FolderJobResult? observation, TextKey? failureCause) => Write(() =>
    {
        if ((observation is null) == (failureCause is null) || expected.Phase != Massdex.SyncShark.Engine.FolderSubmission.SubmissionPhase.Accepted)
            throw new ArgumentException(null, nameof(observation));
        using var transaction = _connection.BeginTransaction();
        var (pair, current) = ReadSubmission(transaction, expected.SubmissionId);
        if (current != expected) throw new FolderStateRefusedException();
        var next = expected;
        IReadOnlyList<FolderMemberResult> results = [];
        if (observation is FolderJobResult.Terminal terminal)
        {
            next = expected with { Phase = Massdex.SyncShark.Engine.FolderSubmission.SubmissionPhase.Terminal, Cause = null };
            results = terminal.Results;
        }
        else if (observation is FolderJobResult.InProgress)
        {
            // A successful observation supersedes any earlier failure, resuming the progress cadence.
            next = expected with { Cause = null };
        }
        else if (failureCause == TextKey.FailDropboxDeleteJobInvalid)
            next = expected with { Cause = failureCause };
        else if (observation is null && !InvalidContribution(expected))
            next = expected with { Cause = failureCause };
        if (next != expected)
        {
            next = next with { Generation = checked(expected.Generation + 1) };
            AdvanceSubmission(transaction, pair, expected, next, results);
        }
        ReconcileSubmission(transaction, pair, expected, next);
        transaction.Commit();
        if (InvalidContribution(expected) || InvalidContribution(next)) _closureKeys?.Remove(expected.PairId);
    });

    /// <inheritdoc />
    public bool HasInvalidFolderJob(string pairId, string path)
    {
        lock (_lock)
            return !_legacy && PairRow(null, pairId) is { } pair && ReadInvalidFolderJob(_connection, null, pair, path);
    }

    internal static bool ReadInvalidFolderJob(SqliteConnection connection, SqliteTransaction? transaction,
        long pair, string path)
    {
        using var command = StorageCommand.Create(connection, transaction, StateSchema.HasInvalidFolderJob,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamPath, path),
            (StateSchema.ParamCause, TextKey.FailDropboxDeleteJobInvalid.ToString()));
        return (long)command.ExecuteScalar()! != 0;
    }

    internal static FolderRemovalState? ReadActiveFolderByRoot(SqliteConnection connection,
        SqliteTransaction? transaction, long pair, string path)
    {
        using var command = StorageCommand.Create(connection, transaction, StateSchema.ActiveFolderByRoot,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamPath, path),
            (StateSchema.ParamTake, StateSchema.OneRow));
        using var reader = command.ExecuteReader();
        return reader.Read() ? StateEncoding.ReadFolderRemoval(reader, null) : null;
    }

    /// <inheritdoc />
    public IEnumerable<FolderSubmission> PendingFolderSubmissions(string pairId, CancellationToken ct, bool progressOnly = false)
    {
        if (_legacy) yield break;
        long? pair;
        lock (_lock) pair = PairRow(null, pairId);
        if (pair is null) yield break;
        var after = string.Empty;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var next = ReadMetadata(StateSchema.PendingFolderSubmissions, StateEncoding.ReadFolderSubmission,
                (StateSchema.ParamPairRow, pair.Value), (StateSchema.ParamAfter, after),
                (StateSchema.ParamProgressOnly, progressOnly), (StateSchema.ParamTake, StateSchema.OneRow));
            if (next is null) yield break;
            after = next.SubmissionId;
            yield return next;
        }
    }

    /// <inheritdoc />
    public bool HasFolderJobProgress(string pairId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (_legacy) return false;
            using var command = StorageCommand.Create(_connection, null, StateSchema.HasFolderJobProgress,
                StateSchema.ParamPair, pairId);
            return (long)command.ExecuteScalar()! != 0;
        }
    }

    private long? PairRow(SqliteTransaction? transaction, string pairId)
    {
        using var command = StorageCommand.Create(_connection, transaction,
            _legacy ? StateSchema.LegacyPairFence : StateSchema.PairFence,
            (StateSchema.ParamPair, pairId));
        return command.ExecuteScalar() as long?;
    }

    private long RequirePairRow(SqliteTransaction? transaction, string pairId) =>
        PairRow(transaction, pairId) ?? throw new InvalidOperationException();

    /// <inheritdoc />
    public void ObserveSkipped(string pairId, long expectedGeneration, Side side, string path,
        (TextKey Cause, IssueSubject Subject)? currentSkip) => Write(() =>
    {
        var touched = _scans.Where(item => item.Key.PairId == pairId && item.Value.Active &&
            item.Value.Record.Generation == expectedGeneration).Select(item => item.Value).ToArray();
        if (touched.Length != 0) AttachScan(pairId, create: false);
        using var transaction = _connection.BeginTransaction();
        var pair = RequireGeneration(transaction, pairId, expectedGeneration);
        // A touched side would stop ReplaceSkipped clearing it, so this returns before the touch.
        if (side == Side.Local && !IssuePolicy.ProtectsSubtree(currentSkip?.Cause.ToString()) &&
            IssueTable.ProtectingMember(_connection, transaction, pair, PathNames.Key(path))) return;
        foreach (var scan in touched)
            if (scan.Record.Scope is null || PathNames.IsSelfOrUnderKey(PathNames.KeyFor(side, path), PathNames.Key(scan.Record.Scope)))
                Execute(_connection, transaction, StateSchema.TouchSkip, (StateSchema.ParamPairRow, pair), (StateSchema.ParamScan, scan.Key),
                (StateSchema.ParamSide, (int)side), (StateSchema.ParamPath, PathNames.KeyFor(side, path)));
        IssueTable.ObserveSkipped(_connection, transaction, pairId, pair, side, path, currentSkip, DateTimeOffset.UtcNow);
        transaction.Commit();
    });

    /// <inheritdoc />
    public bool RetryNoticeProtected(string pairId, long issueId)
    {
        lock (_lock)
            return !_legacy && PairRow(null, pairId) is { } pair && IssueTable.ById(_connection, pairId, issueId) is { } issue &&
                IssueTable.RetainedByOwner(_connection, null, pair, issue);
    }

    /// <inheritdoc />
    public void KeepFolderAlways(string pairId, long expectedGeneration, Issue expected,
        IReadOnlyList<Massdex.SyncShark.SyncIgnoreRule> keepRules) => Write(() =>
    {
        if (expected.Kind != IssueKind.Info || expected.Cause != TextKey.InfoIgnoredFolderKept || keepRules.Count == 0)
            throw new ArgumentException(null, nameof(expected));
        var normalized = SyncIgnorePolicy.Normalize(keepRules);
        using var transaction = _connection.BeginTransaction();
        RequireGeneration(transaction, pairId, expectedGeneration);
        var current = ReadOne(transaction, StateSchema.SelectPair,
            static reader => StateEncoding.DecodePair(reader.GetString(StateSchema.FirstColumn)),
            (StateSchema.ParamPair, pairId)) ?? throw new InvalidOperationException();
        var union = SyncIgnorePolicy.Normalize((current.IgnoredFolderKeepRules ?? [])
            .SelectMany(SyncIgnorePolicy.Parse).Concat(normalized).ToArray())
            .Select(SyncIgnorePolicy.Format).ToArray();
        if (!IssueTable.CloseExact(_connection, transaction, pairId, expected))
        {
            if (ReadOne<long?>(transaction, StateSchema.SelectIssueById,
                    static reader => reader.GetInt64(StateSchema.IssueId),
                    (StateSchema.ParamPair, pairId), (StateSchema.ParamId, expected.Id)) is not null ||
                !(current.IgnoredFolderKeepRules ?? []).SequenceEqual(union))
                throw new InvalidOperationException();
        }
        else
            Execute(_connection, transaction, StateSchema.UpsertPair,
                (StateSchema.ParamPair, pairId),
                (StateSchema.ParamDef, JsonSerializer.Serialize(current with { IgnoredFolderKeepRules = union }, PairEncoding)));
        transaction.Commit();
    });

    /// <inheritdoc />
    public bool HasDeletedNames(string pairId, Guid scanId)
    {
        lock (_lock)
        {
            var scan = RequireScan(pairId, scanId);
            if (!scan.Record.Discovery) return false;
            AttachScan(pairId, create: false);
            return ScanReader.HasDeletedNames(_connection, scan.PairRow);
        }
    }

    /// <inheritdoc />
    public bool DeletedNameExact(string pairId, Guid scanId, string key)
    {
        lock (_lock)
        {
            var scan = RequireScan(pairId, scanId);
            return scan.Record.Discovery && ExactDeletedName(pairId, key, scan);
        }
    }

    /// <inheritdoc />
    public bool SavedDeletedNameExact(string pairId, string key)
    {
        lock (_lock) return !DeletedHistoryOwed(pairId) && ExactDeletedName(pairId, key, null);
    }

    private bool ExactDeletedName(string pairId, string key, ScanPartition? scan)
    {
        var pair = RequirePairRow(null, pairId);
        using var delta = StorageCommand.Create(_connection, null, StateSchema.DeletedKeyDelta,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamPath, key));
        if (delta.ExecuteScalar() is long spent) return spent == 0;
        if (scan is not null)
        {
            AttachScan(pairId, create: false);
            var foundInScan = ScanReader.DeletedNameExact(_connection, pair, key);
            if (foundInScan || scan.ResumedHistory) return foundInScan;
        }
        if (DeletedHistoryCursor(pairId) is null) return false;
        try
        {
            using var block = StorageCommand.Create(_connection, null, StateSchema.DeletedKeyBlock,
                (StateSchema.ParamPairRow, pair), (StateSchema.ParamPath, key));
            using var reader = block.ExecuteReader();
            if (!reader.Read()) return false;
            using var names = new SqliteBlob(_connection, StateSchema.DeletedBlockTable, StateSchema.DeletedBlockPayload,
                reader.GetInt64(StateSchema.DeletedBlockRow), readOnly: true);
            var found = false;
            foreach (var name in StateEncoding.DecodeDeletedNames(names, reader.GetInt32(StateSchema.DeletedBlockCount),
                reader.GetString(StateSchema.DeletedBlockLast), reader.IsDBNull(StateSchema.DeletedBlockPrevious)
                    ? null : reader.GetString(StateSchema.DeletedBlockPrevious)))
                found |= name == key;
            return found;
        }
        catch (InvalidDataException)
        {
            InvalidateCorruptDeletedHistory(pairId);
            throw;
        }
    }

    /// <inheritdoc />
    public bool DeletedNameSpent(string pairId, string key)
    {
        lock (_lock)
            return ReadOne(null, StateSchema.SpentDeletedKey,
                static reader => StateEncoding.ReadBool(reader, StateSchema.FirstColumn),
                (StateSchema.ParamPair, pairId), (StateSchema.ParamPath, key));
    }

    public void Dispose()
    {
        lock (_lock)
        {
            lock (_queueLock)
            {
                if (_disposed) return;
                _disposed = true;
            }
            Write(_connection, static (_, connection) => connection.Dispose());
        }
    }

    private void Write(Action action) => Write(action, static (_, action) => action());

    private T Ordered<T>(Func<T> action)
    {
        T result = default!;
        Write(() => result = action());
        return result;
    }

    private IEnumerable<T> ReadOrdered<T>(IEnumerable<T> source)
    {
        IEnumerator<T> iterator;
        lock (_lock) iterator = source.GetEnumerator();
        try
        {
            while (true)
            {
                T item;
                lock (_lock)
                {
                    if (!iterator.MoveNext()) yield break;
                    item = iterator.Current;
                }
                yield return item;
            }
        }
        finally
        {
            lock (_lock) iterator.Dispose();
        }
    }

    private void Write<T>(T state, Action<StateStore, T> write)
    {
        List<(string PairId, StateChange Change, TaskCompletionSource Completion, bool Narrow, Exception? Failure)>? completed = null;
        Exception? failure = null;
        try
        {
            lock (_lock)
            {
                completed = DrainRecords(out failure);
                try { write(this, state); }
                catch (Exception error)
                {
                    try { _issueFacts.Publish(); }
                    catch (Exception publication) { throw new AggregateException(error, publication); }
                    throw;
                }
                _issueFacts.Publish();
            }
        }
        finally
        {
            CompleteRecords(completed, failure);
        }
    }

    private void FlushRecords()
    {
        try
        {
            List<(string PairId, StateChange Change, TaskCompletionSource Completion, bool Narrow, Exception? Failure)>? completed;
            Exception? failure;
            lock (_lock) completed = DrainRecords(out failure);
            CompleteRecords(completed, failure);
        }
        finally
        {
            bool flush;
            lock (_queueLock)
            {
                flush = _records is not null;
                if (!flush) _flushing = false;
            }
            if (flush) ThreadPool.QueueUserWorkItem(static store => store.FlushRecords(), this, preferLocal: false);
        }
    }

    private List<(string PairId, StateChange Change, TaskCompletionSource Completion, bool Narrow, Exception? Failure)>? DrainRecords(out Exception? failure)
    {
        List<(string PairId, StateChange Change, TaskCompletionSource Completion, bool Narrow, Exception? Failure)>? records;
        lock (_queueLock)
        {
            records = _records;
            _records = null;
        }
        failure = null;
        if (records is null) return null;
        var group = Apply(0, records.Count);
        if (group.Error is not null && group.Isolatable && records.Count > 1)
        {
            for (var index = 0; index < records.Count; index++)
            {
                var single = Apply(index, index + 1);
                if (single.Error is null) continue;
                Fail(index, single.Error);
                if (single.Isolatable) continue;
                for (var remaining = index + 1; remaining < records.Count; remaining++) Fail(remaining, single.Error);
                break;
            }
        }
        else if (group.Error is not null) failure = group.Error;
        return records;

        void Fail(int index, Exception error)
        {
            var record = records[index];
            records[index] = (record.PairId, record.Change, record.Completion, record.Narrow, error);
        }

        (Exception? Error, bool Isolatable) Apply(int first, int end)
        {
            SqliteTransaction? transaction = null;
            var applying = false;
            var committed = false;
            var isolatable = false;
            Exception? failure = null;
            try
            {
                transaction = _connection.BeginTransaction();
                var now = DateTimeOffset.UtcNow;
                applying = true;
                for (var index = first; index < end; index++)
                {
                    var record = records[index];
                    if (QueueableAdoption(record.Change))
                    {
                        var expected = record.Change.Renames[0].Before!;
                        var current = ReadOne(transaction, StateSchema.SelectSyncedByLocalKey, DecodeSynced,
                            (StateSchema.ParamPair, record.PairId), (StateSchema.ParamTargetKey, expected.LocalKey));
                        if (current != expected) throw new ChangedUnderneathException(expected.LocalPath);
                    }
                    StateCommitter.Commit(_connection, transaction, _changes, record.PairId, record.Change, now,
                        record.Narrow, raised: null);
                }
                applying = false;
                transaction.Commit();
                committed = true;
            }
            catch (Exception error)
            {
                failure = error;
                isolatable = applying && error is ArgumentException or ChangedUnderneathException ||
                    error is SqliteException sqlite && sqlite.SqliteErrorCode == SQLitePCL.raw.SQLITE_CONSTRAINT;
                try
                {
                    if (transaction is null) isolatable = false;
                    else transaction.Rollback();
                }
                catch (Exception rollback)
                {
                    failure = new AggregateException(error, rollback);
                    isolatable = false;
                }
            }
            finally
            {
                try { transaction?.Dispose(); }
                catch (Exception disposal)
                {
                    failure = failure is null ? disposal : new AggregateException(failure, disposal);
                    isolatable = false;
                }
            }
            try { _issueFacts.Publish(); }
            catch (Exception publication)
            {
                failure = failure is null ? publication : new AggregateException(failure, publication);
                isolatable = false;
            }
            if (!committed) return (failure, isolatable);
            if (failure is not null)
                for (var index = first; index < end; index++) Fail(index, failure);
            for (var index = first; index < end; index++)
            {
                var record = records[index];
                try { UpdateClosures(record.PairId, record.Change); }
                catch (Exception error)
                {
                    _closureKeys?.Remove(record.PairId);
                    Fail(index, error);
                }
            }
            return (null, false);
        }
    }

    private static void CompleteRecords(
        List<(string PairId, StateChange Change, TaskCompletionSource Completion, bool Narrow, Exception? Failure)>? records, Exception? failure)
    {
        if (records is null) return;
        foreach (var record in records)
        {
            var error = record.Failure ?? failure;
            if (error is null) record.Completion.TrySetResult();
            else record.Completion.TrySetException(error);
        }
    }

    private bool NeedsClosure(string pairId, IReadOnlyList<(string Path, IReadOnlyList<IssueKind> Kinds)> closures)
    {
        if (closures.Count == 0) return false;
        _closureKeys ??= new(StringComparer.Ordinal);
        if (!_closureKeys.TryGetValue(pairId, out var keys))
        {
            keys = IssueTable.ClosureKeys(_connection, pairId, ClosureKinds);
            _closureKeys.Add(pairId, keys);
        }
        foreach (var (path, kinds) in closures)
            foreach (var kind in kinds)
                // Explicit closures outside this cache still reach the ordinary close transaction.
                if (!ClosureKinds.Contains(kind) || keys.Contains((path, kind))) return true;
        return false;
    }

    private void UpdateClosures(string pairId, StateChange change)
    {
        if (_closureKeys is null || !_closureKeys.TryGetValue(pairId, out var keys)) return;
        // Closure can retain protected responsibility or restore an invalid-job projection.
        foreach (var (path, kinds) in change.Close)
            foreach (var kind in kinds)
            {
                if (!ClosureKinds.Contains(kind)) continue;
                var present = ReadOne(null, StateSchema.SelectIssueByPathKind,
                    static reader => reader.GetInt64(StateSchema.IssueId),
                    (StateSchema.ParamPair, pairId), (StateSchema.ParamPath, path), (StateSchema.ParamKind, (int)kind));
                if (present != 0) keys.Add((path, kind));
                else keys.Remove((path, kind));
            }
        foreach (var item in change.FirstRunAcknowledged) keys.Add((item.Path, IssueKind.Failure));
        foreach (var issue in change.Raise)
            if (ClosureKinds.Contains(issue.Kind)) keys.Add((issue.Path, issue.Kind));
    }

    private IEnumerable<byte[]> ActiveScanKeys(string pairId)
    {
        var generation = ReadPairExecution(pairId)?.Generation;
        return _scans.Where(item => item.Key.PairId == pairId && item.Value.Active &&
            item.Value.Record.Generation == generation).Select(item => item.Value.Key);
    }

    private string ScanPath(string pairId) => _path + StateSchema.ScanFileSuffix +
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(pairId))) +
        StateSchema.DatabaseExtension;

    private void AttachScan(string pairId, bool create)
    {
        if (_scanPair == pairId && (!create || _scanOnDisk)) return;
        var path = ScanPath(pairId);
        var onDisk = create || File.Exists(path);
        if (_scanPair is not null)
        {
            Execute(_connection, null, StateSchema.DetachScan);
            _scanPair = null;
        }
        Execute(_connection, null, StateSchema.AttachScan,
            (StateSchema.ParamPath, onDisk ? path : StateSchema.EmptyScan));
        _scanPair = pairId;
        _scanOnDisk = onDisk;
        try
        {
            if (onDisk)
            {
                using var journal = StorageCommand.Create(_connection, null, StateSchema.ScanWal);
                if (journal.ExecuteScalar() as string != StateSchema.WalMode)
                    throw new InvalidDataException();
                Execute(_connection, null, StateSchema.ScanPragmas);
            }
            else Execute(_connection, null, StateSchema.EmptyScanPragmas);
            Execute(_connection, null, StateSchema.CreateScan);
        }
        catch (Exception setup)
        {
            try { Execute(_connection, null, StateSchema.DetachScan); _scanPair = null; }
            catch (Exception cleanup) { throw new AggregateException(setup, cleanup); }
            throw;
        }
    }

    private SqliteConnection OpenScanRead(string pairId)
    {
        // Enable URI handling in memory; disk attachments retain native paths, including UNC roots.
        var read = Connect(StateSchema.ReadRoot, SqliteOpenMode.ReadOnly);
        try
        {
            Execute(read, null, StateSchema.AttachState, (StateSchema.ParamPath, _path));
            var path = ScanPath(pairId);
            var exists = File.Exists(path);
            Execute(read, null, StateSchema.AttachScan,
                (StateSchema.ParamPath, exists ? path : StateSchema.EmptyScanRead));
            Execute(read, null, StateSchema.ScanPragmas);
            if (!exists) Execute(read, null, StateSchema.CreateScan);
            return read;
        }
        catch
        {
            read.Dispose();
            throw;
        }
    }

    private void InvalidateScans(string pairId)
    {
        foreach (var scan in _scans.Where(item => item.Key.PairId == pairId)) scan.Value.Active = false;
        if (!_scans.Keys.Any(key => key.PairId == pairId)) ResetScan(pairId);
    }

    private void ResetScan(string pairId)
    {
        var scans = _scans.Where(item => item.Key.PairId == pairId).ToArray();
        if (scans.Any(item => item.Value.Borrowers != 0)) throw new InvalidOperationException();
        if (_scanPair == pairId)
        {
            Execute(_connection, null, StateSchema.DetachScan);
            _scanPair = null;
        }
        foreach (var scan in scans) _scans.Remove(scan.Key);
        var path = ScanPath(pairId);
        File.Delete(path);
        File.Delete(path + StateSchema.WalSuffix);
        File.Delete(path + StateSchema.ShmSuffix);
    }

    private T? ReadMetadata<T>(string sql, Func<SqliteDataReader, T> decode,
        params (string Name, object? Value)[] arguments)
    {
        lock (_lock)
        {
            using var command = StorageCommand.Create(_connection, null, sql, arguments);
            using var reader = command.ExecuteReader();
            return reader.Read() ? decode(reader) : default;
        }
    }

    private static bool InitializeSchema(SqliteConnection connection, bool writable)
    {
        using var version = StorageCommand.Create(connection, null, StateSchema.ReadVersion);
        var actual = (long)version.ExecuteScalar()!;
        using var schema = StorageCommand.Create(connection, null, StateSchema.HasSchema);
        var empty = (long)schema.ExecuteScalar()! == 0;
        if (actual != StateSchema.Version && actual != StateSchema.DiscoveryRequiredUpgradeVersion &&
            actual != StateSchema.LocalPublishedUpgradeVersion &&
            actual != StateSchema.SpentHistoryUpgradeVersion &&
            actual != StateSchema.DeletedBlocksUpgradeVersion &&
            actual != StateSchema.IssueVisibilityUpgradeVersion &&
            actual != StateSchema.PhaseIndexVersion && actual != StateSchema.UpgradeFromVersion &&
            actual != StateSchema.DeletedHistoryUpgradeVersion && (!writable || !empty))
            throw new InvalidDataException(Text.Get(actual < StateSchema.Version
                ? TextKey.StateSchemaTooOld : TextKey.StateSchemaTooNew, (TextArg.Path, connection.DataSource)));
        using var reference = Connect(StateSchema.ReadRoot, SqliteOpenMode.ReadWriteCreate);
        Execute(reference, null, StateSchema.Create);
        if (!empty)
        {
            if (actual != StateSchema.UpgradeFromVersion) Execute(reference, null, StateSchema.SchemaAdditions);
            if (actual == StateSchema.PhaseIndexVersion) Execute(reference, null, StateSchema.CreateObsoletePhaseIndex);
            if (actual >= StateSchema.IssueVisibilityUpgradeVersion)
                Execute(reference, null, StateSchema.DeletedHistorySchema);
            if (actual >= StateSchema.DeletedBlocksUpgradeVersion) Execute(reference, null, StateSchema.RebuildIssues);
            if (actual >= StateSchema.SpentHistoryUpgradeVersion)
                Execute(reference, null, StateSchema.DeletedHistoryBlocks + StateSchema.DropDeletedHistoryNames);
            if (actual >= StateSchema.LocalPublishedUpgradeVersion)
                Execute(reference, null, StateSchema.SpentDeletedHistory);
            if (actual >= StateSchema.DiscoveryRequiredUpgradeVersion) Execute(reference, null, StateSchema.LocalPublishedColumns);
            if (actual == StateSchema.Version) Execute(reference, null, StateSchema.DiscoveryRequiredColumn);
            ValidateStructure(connection, null, reference);
        }
        if (!writable)
        {
            if (actual == StateSchema.UpgradeFromVersion) return true;
            if (actual != StateSchema.Version)
                throw new InvalidDataException(Text.Get(TextKey.StateSchemaUpgradeRequired));
            return false;
        }
        Execute(connection, null, StateSchema.Pragmas);
        if (!empty && actual == StateSchema.Version)
        {
            Reclaim(connection);
            Execute(connection, null, StateSchema.Checkpoint);
            return false;
        }
        // Disabling enforcement outside the transaction preserves member links while their parent is rebuilt.
        Execute(connection, null, StateSchema.ForeignKeysOff);
        try
        {
            using var transaction = connection.BeginTransaction();
            if (empty || actual == StateSchema.UpgradeFromVersion)
            {
                connection.CreateFunction(StateSchema.MappedSpellingFunction,
                    (string? path) => path is null ? null : PathNames.UpgradeMappedSpelling(path), isDeterministic: true);
                if (empty) Execute(connection, transaction, StateSchema.Create);
                Execute(connection, transaction, StateSchema.SchemaAdditions);
                Execute(connection, transaction, StateSchema.SeedSources,
                    (StateSchema.ParamBootstrap, nameof(SourceObligation.Bootstrap)),
                    (StateSchema.ParamNormal, nameof(SourceObligation.NormalRepair)));
                using (var collisions = StorageCommand.Create(connection, transaction, StateSchema.EffectiveDropboxKeyCollisions))
                    if ((long)collisions.ExecuteScalar()! != 0) throw new InvalidDataException();
                try { Execute(connection, transaction, StateSchema.ConvertMappedDropboxKeys); }
                catch (SqliteException error) when (error.SqliteErrorCode == SQLitePCL.raw.SQLITE_CONSTRAINT)
                { throw new InvalidDataException(null, error); }
                Execute(reference, null, StateSchema.SchemaAdditions);
                using var pairs = StorageCommand.Create(connection, transaction, StateSchema.SelectPairs);
                using var reader = pairs.ExecuteReader();
                while (reader.Read()) StateEncoding.DecodePair(reader.GetString(StateSchema.FirstColumn));
            }
            if (!empty && actual == StateSchema.PhaseIndexVersion)
            {
                Execute(connection, transaction, StateSchema.DropObsoletePhaseIndex);
                Execute(reference, null, StateSchema.DropObsoletePhaseIndex);
            }
            if (empty || actual < StateSchema.IssueVisibilityUpgradeVersion)
            {
                Execute(connection, transaction, StateSchema.DeletedHistorySchema);
                Execute(reference, null, StateSchema.DeletedHistorySchema);
            }
            if (empty || actual < StateSchema.DeletedBlocksUpgradeVersion)
            {
                Execute(connection, transaction, StateSchema.RebuildIssues);
                Execute(reference, null, StateSchema.RebuildIssues);
            }
            if (empty || actual < StateSchema.SpentHistoryUpgradeVersion)
            {
                Execute(connection, transaction, StateSchema.DeletedHistoryBlocks);
                MigrateDeletedHistory(connection, transaction);
                Execute(connection, transaction, StateSchema.DropDeletedHistoryNames);
                Execute(reference, null, StateSchema.DeletedHistoryBlocks + StateSchema.DropDeletedHistoryNames);
            }
            if (empty || actual < StateSchema.LocalPublishedUpgradeVersion)
            {
                Execute(connection, transaction, StateSchema.SpentDeletedHistory);
                Execute(reference, null, StateSchema.SpentDeletedHistory);
            }
            Execute(connection, transaction, StateSchema.LocalPublishedColumns);
            Execute(reference, null, StateSchema.LocalPublishedColumns);
            Execute(connection, transaction, StateSchema.DiscoveryRequiredColumn);
            Execute(reference, null, StateSchema.DiscoveryRequiredColumn);
            if (empty || actual == StateSchema.UpgradeFromVersion) IssueTable.BackfillSkipMembers(connection, transaction);
            ValidateStructure(connection, transaction, reference);
            using (var foreign = StorageCommand.Create(connection, transaction, StateSchema.ForeignKeyViolations))
            using (var reader = foreign.ExecuteReader())
                if (reader.Read()) throw new InvalidDataException();
            Execute(connection, transaction, StateSchema.PublishVersion);
            transaction.Commit();
        }
        finally
        {
            try
            {
                Execute(connection, null, StateSchema.ForeignKeysOn);
                using var enabled = StorageCommand.Create(connection, null, StateSchema.ReadForeignKeys);
                if ((long)enabled.ExecuteScalar()! != 1) throw new InvalidDataException();
            }
            catch { connection.Dispose(); throw; }
        }
        Reclaim(connection);
        Execute(connection, null, StateSchema.Checkpoint);
        return false;
    }

    private static void WriteDeletedBlock(SqliteConnection connection, SqliteTransaction transaction,
        long pair, IReadOnlyList<string> keys) => Execute(connection, transaction, StateSchema.SaveDeletedBlock,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamPath, keys[^1]),
            (StateSchema.ParamCount, keys.Count), (StateSchema.ParamNames, StateEncoding.EncodeDeletedNames(keys)));

    private static void MigrateDeletedHistory(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var total = StorageCommand.Create(connection, transaction, StateSchema.LegacyDeletedCount);
        var expected = (long)total.ExecuteScalar()!;
        using var command = StorageCommand.Create(connection, transaction, StateSchema.LegacyDeletedNames);
        using var reader = command.ExecuteReader();
        var keys = new List<string>(StateSchema.DeletedHistoryBatchSize);
        long pair = 0;
        long copied = 0;
        while (reader.Read())
        {
            var nextPair = reader.GetInt64(0);
            if (keys.Count != 0 && (nextPair != pair || keys.Count == StateSchema.DeletedHistoryBatchSize))
            {
                WriteDeletedBlock(connection, transaction, pair, keys);
                keys.Clear();
            }
            pair = nextPair;
            keys.Add(reader.GetString(StateSchema.LegacyDeletedKeyColumn));
            copied++;
        }
        if (keys.Count != 0) WriteDeletedBlock(connection, transaction, pair, keys);
        if (copied != expected) throw new InvalidDataException();
    }

    private static void ValidateStructure(SqliteConnection connection, SqliteTransaction? transaction,
        SqliteConnection reference)
    {
        var objects = Compare(StateSchema.SchemaObjects, []);
        foreach (var row in objects)
        {
            var name = (string)row[1];
            Compare(StateSchema.TableColumns, [(StateSchema.ParamTable, name)]);
            Compare(StateSchema.TableForeignKeys, [(StateSchema.ParamTable, name)]);
            Compare(StateSchema.TableIndexes, [(StateSchema.ParamTable, name)]);
            Compare(StateSchema.IndexColumns, [(StateSchema.ParamIndex, name)]);
        }

        IReadOnlyList<object[]> Compare(string sql, (string Name, object? Value)[] arguments)
        {
            var rows = new List<object[]>();
            using var expectedCommand = StorageCommand.Create(reference, null, sql, arguments);
            using var actualCommand = StorageCommand.Create(connection, transaction, sql, arguments);
            using var expected = expectedCommand.ExecuteReader();
            using var actual = actualCommand.ExecuteReader();
            while (expected.Read())
            {
                if (!actual.Read() || actual.FieldCount != expected.FieldCount) throw new InvalidDataException();
                var values = new object[expected.FieldCount];
                expected.GetValues(values);
                for (var index = 0; index < values.Length; index++)
                    if (!Equals(values[index], actual.GetValue(index))) throw new InvalidDataException();
                rows.Add(values);
            }
            if (actual.Read()) throw new InvalidDataException();
            return rows;
        }
    }

    internal static SqliteConnection Connect(string path, SqliteOpenMode mode)
    {
        // Pooling keeps the file locked after Dispose, so a caller's temporary folder cannot be removed.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
        }.ToString());
        try
        {
            OpenConnection(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal static void OpenConnection(SqliteConnection connection)
    {
        connection.Open();
        // Wait inside SQLite before the provider resets a busy statement; reuse its command timeout.
        SqliteException.ThrowExceptionForRC(SQLitePCL.raw.sqlite3_busy_timeout(connection.Handle,
            checked((int)TimeSpan.FromSeconds(connection.DefaultTimeout).TotalMilliseconds)), connection.Handle);
        connection.CreateFunction(StateSchema.HistoryKeyFunction, (string path) => PathNames.DropboxKey(path), isDeterministic: true);
        connection.CreateFunction(StateSchema.PathKeyFunction, (string? path) => path is null ? null : PathNames.Key(path), isDeterministic: true);
        connection.CreateFunction(StateSchema.ResponsibilityOverlapsFunction,
            (string localKey, string dropboxKey, bool folder, string? path) =>
            {
                if (path is null) return false;
                var key = PathNames.Key(path);
                return PathNames.ResponsibilityOverlaps(localKey, key, folder) ||
                    localKey != dropboxKey && PathNames.ResponsibilityOverlaps(dropboxKey, key, folder);
            }, isDeterministic: true);
        connection.CreateFunction(StateSchema.PlanDepthFunction, (string path) => PathNames.Depth(path), isDeterministic: true);
        connection.CreateFunction(StateSchema.LocalAccessRetryCauseFunction, (string? cause) =>
            Enum.TryParse<TextKey>(cause, out var key) && IssuePolicy.IsLocalAccessRetryCause(key), isDeterministic: true);
    }

    /// <summary>Returns free pages to the file; incremental_vacuum yields once per reclaimed page.</summary>
    private void Reclaim() => Reclaim(_connection);

    private static void Reclaim(SqliteConnection connection)
    {
        using var command = StorageCommand.Create(connection, null, StateSchema.Reclaim);
        using var pages = command.ExecuteReader();
        while (pages.Read()) { }
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] arguments)
    {
        using var command = StorageCommand.Create(connection, transaction, sql, arguments);
        return command.ExecuteNonQuery();
    }

    private static void RequireOne(int affected)
    {
        if (affected != 1) throw new InvalidOperationException();
    }

    private SyncedEntry? ReadSynced(string sql, params (string Name, object? Value)[] arguments)
    {
        using var command = StorageCommand.Create(_connection, null, sql, arguments);
        using var reader = command.ExecuteReader();
        return reader.Read() ? DecodeSynced(reader) : null;
    }

    private static SyncedEntry DecodeSynced(SqliteDataReader reader) =>
        StateEncoding.ReadSynced(reader, reader.GetString(StateSchema.SyncedLocalKey), StateSchema.SyncedLocalPath);
}

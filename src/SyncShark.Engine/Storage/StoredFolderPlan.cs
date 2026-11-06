using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Owns one disposable capture and its frozen deletion proof, never durable sync authority.</summary>
internal sealed class StoredFolderPlan : FolderPlan
{
    private readonly SqliteConnection _connection;
    private readonly string _path;
    private readonly string _pairId;
    private readonly string _rootKey;
    private readonly string _rootNativeKey;
    private StateStore? _registration;
    private SqliteTransaction? _transaction;
    private Phase _phase;
    private Candidate? _current;
    private bool _marked;
    private bool _hasSurvivors;
    private bool _deleteWhole;

    /// <summary>Takes ownership of exactly one scratch path; the opener's registration ends only after this plan's
    /// readers, connection and file are gone.</summary>
    internal StoredFolderPlan(SqliteConnection connection, string path, string pairId, Row folder, Side deletingFrom,
        StateStore? registration)
    {
        _connection = connection;
        _path = path;
        _pairId = pairId;
        _registration = registration;
        Folder = folder;
        DeletingFrom = deletingFrom;
        var rootPath = folder.PathOn(deletingFrom)!;
        _rootNativeKey = PathNames.Key(rootPath);
        _rootKey = PathNames.KeyFor(deletingFrom, rootPath, _rootNativeKey);
        connection.CreateFunction(StateSchema.PlanDepthFunction,
            (string key) => PathNames.Depth(key), isDeterministic: true);
        Execute(StateSchema.CreateScan);
        Execute(StateSchema.PlanCreateClassified);
    }

    public Row Folder { get; private set; }

    public void PrepareRemoval(Row observed)
    {
        Require(Phase.Captured);
        if (observed.EntryOn(DeletingFrom) is not { IsFolder: true } root ||
            PathNames.KeyFor(DeletingFrom, root.Path) != _rootKey || PathNames.Key(root.Path) != _rootNativeKey)
            throw new InvalidOperationException();
        Folder = observed;
    }
    public Side DeletingFrom { get; }
    public bool DeleteWhole
    {
        get
        {
            Require(Phase.Completed);
            return _deleteWhole;
        }
    }

    public async Task CaptureAsync(IAsyncEnumerable<Listed> source, CancellationToken ct)
    {
        Require(Phase.New);
        _phase = Phase.Capturing;
        IAsyncEnumerator<Listed>? iterator = null;
        ScanEntries? entries = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            _transaction = _connection.BeginTransaction(deferred: true);
            entries = new ScanEntries(_connection, _pairId, DeletingFrom, begunEmpty: true);
            iterator = source.GetAsyncEnumerator(ct);
            while (await iterator.MoveNextAsync().ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                var item = iterator.Current;
                if (IssuePolicy.ProtectsSubtree(item.Entry?.Skip))
                    throw new InventoryIncompleteException(Text.Get(Text.ReadFailedOn(DeletingFrom),
                        (TextArg.Path, item.Path), (TextArg.Message, "")), null);
                entries.Apply(_transaction, item);
            }
            // Successful enumeration alone is not proof: disposal can report a late listing failure.
            var completed = iterator;
            iterator = null;
            await completed.DisposeAsync().ConfigureAwait(false);
            entries.Dispose();
            entries = null;
            ct.ThrowIfCancellationRequested();
            _transaction.Commit();
            _transaction.Dispose();
            _transaction = null;
            _phase = Phase.Captured;
        }
        catch
        {
            // OFF journalling cannot roll back a failed capture into a reusable plan.
            if (iterator is not null)
            {
                try { await iterator.DisposeAsync().ConfigureAwait(false); }
                catch { /* Preserve the capture failure. */ }
            }
            try { entries?.Dispose(); }
            catch { /* Preserve the capture failure. */ }
            Discard();
            throw;
        }
    }

    public IEnumerable<Entry> Entries(CancellationToken ct)
    {
        Require(Phase.Captured);
        var after = "";
        while (true)
        {
            Require(Phase.Captured);
            ct.ThrowIfCancellationRequested();
            Entry? entry = null;
            try
            {
                using var command = Command(StateSchema.PlanEntries,
                    (StateSchema.ParamPair, _pairId), (StateSchema.ParamSide, (int)DeletingFrom),
                    (StateSchema.ParamAfter, after));
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    after = reader.GetString(StateSchema.PlanInventoryCursorColumn);
                    entry = StateEncoding.ReadEntry(reader, StateSchema.PlanInventoryEntryColumn, localProjection: false);
                }
            }
            catch
            {
                Discard();
                throw;
            }
            if (entry is null) yield break;
            yield return entry;
        }
    }

    public IEnumerable<(string Key, string NativeKey, Entry? Entry, SyncedEntry? Synced)> Children(CancellationToken ct)
    {
        Require(Phase.Captured);
        _phase = Phase.Classifying;
        var exhausted = false;
        try
        {
            _transaction = _connection.BeginTransaction(deferred: true);
            foreach (var sql in new[] { StateSchema.PlanPresent, StateSchema.PlanSaved })
            {
                var after = "";
                var savedOverride = false;
                while (true)
                {
                    Require(Phase.Classifying);
                    ct.ThrowIfCancellationRequested();
                    var candidate = ReadCandidate(sql, hasCursor: true, hasBranch: false,
                        sql == StateSchema.PlanPresent
                            ? [(StateSchema.ParamPair, _pairId), (StateSchema.ParamSide, (int)DeletingFrom),
                                (StateSchema.ParamOwnKey, _rootNativeKey), (StateSchema.ParamAfter, after)]
                            : [(StateSchema.ParamPair, _pairId), (StateSchema.ParamSide, (int)DeletingFrom),
                                (StateSchema.ParamTargetKey, _rootKey), (StateSchema.ParamAfter, after),
                                (StateSchema.PlanParamSavedOverride, savedOverride)]);
                    if (candidate is not { } current) break;
                    after = current.Cursor;
                    savedOverride = current.SavedOverride;
                    _current = current;
                    _marked = false;
                    yield return (current.Key, current.NativeKey, current.Entry, current.Synced);
                    if (!_marked) throw new InvalidOperationException();
                    _current = null;
                }
            }
            _phase = Phase.Classified;
            exhausted = true;
        }
        finally
        {
            _current = null;
            if (!exhausted && _phase != Phase.Disposed) _phase = Phase.Faulted;
        }
    }

    public void MarkClassified(string key, string nativeKey, FolderChild child, bool followUp, CancellationToken ct)
    {
        Require(Phase.Classifying);
        ct.ThrowIfCancellationRequested();
        if (_current is not { } current || _marked || key != current.Key || nativeKey != current.NativeKey ||
            child.OnDeletingSide != current.Entry || child.Synced != current.Synced)
            throw new InvalidOperationException();
        var entry = current.Entry;
        var saved = current.Synced;
        Execute(StateSchema.ClassifiedInsert,
            (StateSchema.ParamPolicyKey, key), (StateSchema.ParamNativeKey, nativeKey),
            (StateSchema.PlanParamPresent, entry is not null), (StateSchema.ParamEntryPath, entry?.Path),
            (StateSchema.ParamEntryFolder, entry?.IsFolder), (StateSchema.ParamEntrySize, entry?.Size),
            (StateSchema.ParamEntryMtime, entry?.Modified.UtcTicks),
            (StateSchema.ParamEntryId, StateEncoding.IdFor(DeletingFrom, entry?.Id)),
            (StateSchema.ParamEntryRev, entry?.Revision),
            (StateSchema.ParamEntryHash, StateEncoding.Hash(entry?.ContentHash)),
            (StateSchema.ParamEntrySkip, entry?.Skip), (StateSchema.ParamSavedLocalKey, saved?.LocalKey),
            (StateSchema.ParamSavedLocalPath, saved?.LocalPath),
            (StateSchema.ParamSavedDropboxDelta, Delta(saved?.DropboxKey, saved?.LocalKey)),
            (StateSchema.ParamSavedDropboxPathDelta, Delta(saved?.DropboxPath, saved?.LocalPath)),
            (StateSchema.ParamSavedFolder, saved?.IsFolder),
            (StateSchema.ParamSavedLocalId, StateEncoding.LocalId(saved?.LocalId)),
            (StateSchema.ParamSavedSize, saved?.LocalSize), (StateSchema.ParamSavedMtime, saved?.LocalModifiedTicks),
            (StateSchema.ParamSavedDropboxId, saved?.DropboxId),
            (StateSchema.ParamSavedHash, StateEncoding.Hash(saved?.DropboxContentHash)),
            (StateSchema.ParamVerdict, child.Verdict.ToString()), (StateSchema.ParamFollowUp, followUp));
        _marked = true;
        _hasSurvivors |= child.Verdict == FolderVerdict.Survives;
    }

    public void PrepareBranches(CancellationToken ct)
    {
        Require(Phase.Classified);
        ct.ThrowIfCancellationRequested();
        if (!_hasSurvivors) throw new InvalidOperationException();
        Execute(DeletingFrom == Side.Local
            ? StateSchema.PlanCreateClassifiedLocalRoots : StateSchema.PlanCreateClassifiedDropboxRoots);
        _phase = Phase.Prepared;
    }

    public IEnumerable<(long Id, string Key, FolderChild Child)> RemovedChildren(CancellationToken ct)
    {
        Require(Phase.Prepared);
        _phase = Phase.Selecting;
        var exhausted = false;
        try
        {
            var depth = 0L;
            while (true)
            {
                Require(Phase.Selecting);
                ct.ThrowIfCancellationRequested();
                var nextDepth = Scalar(StateSchema.ClassifiedNextDepth, (StateSchema.ParamNegativeDepth, depth));
                if (nextDepth is not long next) break;
                depth = next;
                Candidate? after = null;
                while (true)
                {
                    Require(Phase.Selecting);
                    ct.ThrowIfCancellationRequested();
                    var candidate = ReadAfter(StateSchema.ClassifiedRemoved, hasBranch: true, after,
                        (StateSchema.ParamNegativeDepth, depth));
                    if (candidate is not { } current) break;
                    after = current;
                    _current = current;
                    _marked = false;
                    yield return (current.Id, current.Key, new FolderChild(current.Entry, current.Synced, FolderVerdict.Removed));
                    _current = null;
                }
            }
            _phase = Phase.Selected;
            exhausted = true;
        }
        finally
        {
            _current = null;
            if (!exhausted && _phase != Phase.Disposed) _phase = Phase.Faulted;
        }
    }

    public bool HasSurvivorAtOrBelow(string key, CancellationToken ct)
    {
        Require(Phase.Selecting);
        ct.ThrowIfCancellationRequested();
        var (low, high) = PathNames.DescendantBounds(key);
        return Exists(StateSchema.ClassifiedHasSurvivor, (StateSchema.ParamJoinKey, key),
            (StateSchema.ParamLow, low), (StateSchema.ParamHigh, high));
    }

    public bool HasSelectedAncestor(string key, CancellationToken ct)
    {
        Require(Phase.Selecting);
        ct.ThrowIfCancellationRequested();
        while (PathNames.Parent(key) is var parent && PathNames.IsUnderKey(key, parent))
        {
            key = parent;
            if (key == _rootKey) break;
            if (Exists(StateSchema.ClassifiedHasSelected, (StateSchema.ParamJoinKey, key))) return true;
        }
        return false;
    }

    public void SelectBranch(long id, CancellationToken ct)
    {
        Require(Phase.Selecting);
        ct.ThrowIfCancellationRequested();
        if (_current is not { } current || current.Id != id || _marked) throw new InvalidOperationException();
        Execute(StateSchema.ClassifiedSelect, (StateSchema.ParamId, id));
        _marked = true;
    }

    public void Complete(bool deleteWhole, CancellationToken ct)
    {
        if (deleteWhole)
        {
            Require(Phase.Classified);
            if (_hasSurvivors) throw new InvalidOperationException();
        }
        else
        {
            Require(Phase.Selected);
        }
        try
        {
            ct.ThrowIfCancellationRequested();
            _transaction!.Commit();
            _transaction.Dispose();
            _transaction = null;
            _deleteWhole = deleteWhole;
            _phase = Phase.Completed;
        }
        catch
        {
            Discard();
            throw;
        }
    }

    public bool ForgetTouchesUnreadable(CancellationToken ct)
    {
        Require(Phase.Completed);
        try
        {
            return StateStore.ForgetTouchesUnreadable(_connection, _pairId, ForgetRoots(ct), ct);
        }
        catch
        {
            Discard();
            throw;
        }
    }

    public IEnumerable<FrozenFolderEntry> FrozenEvidence(int pageSize, CancellationToken ct)
    {
        Require(Phase.Completed);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        return FrozenRows(pageSize, ct);
    }

    private IEnumerable<FrozenFolderEntry> FrozenRows(int pageSize, CancellationToken ct)
    {
        Candidate? after = null;
        var ordinal = 0L;
        while (true)
        {
            Require(Phase.Completed);
            ct.ThrowIfCancellationRequested();
            var rows = new List<FrozenFolderEntry>(pageSize);
            try
            {
                using var command = Command(StateSchema.FrozenEvidence, Page(after, pageSize));
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    var facts = ReadFacts(reader, StateSchema.FrozenPolicyKey);
                    rows.Add(new FrozenFolderEntry(++ordinal, facts.Key, facts.NativeKey, facts.Entry, facts.Synced,
                        StateEncoding.ReadEnum<FolderVerdict>(reader, StateSchema.FrozenVerdict),
                        StateEncoding.ReadBool(reader, StateSchema.FrozenFollowUp),
                        StateEncoding.ReadBool(reader, StateSchema.FrozenSelected)));
                    after = facts;
                }
            }
            catch
            {
                Discard();
                throw;
            }
            if (rows.Count == 0) yield break;
            foreach (var row in rows)
            {
                Require(Phase.Completed);
                ct.ThrowIfCancellationRequested();
                yield return row;
            }
        }
    }

    public IEnumerable<FolderChild> Branches(CancellationToken ct)
    {
        Require(Phase.Completed);
        return _deleteWhole ? Array.Empty<FolderChild>() : FrozenChildren(branches: true, ct);
    }

    public IEnumerable<FolderChild> Survivors(CancellationToken ct)
    {
        Require(Phase.Completed);
        return _deleteWhole ? Array.Empty<FolderChild>() : FrozenChildren(branches: false, ct);
    }

    private IEnumerable<FolderChild> FrozenChildren(bool branches, CancellationToken ct)
    {
        Candidate? after = null;
        while (true)
        {
            Require(Phase.Completed);
            var candidate = branches
                ? NextSelected(after) : ReadAfter(StateSchema.ClassifiedSurvivors, hasBranch: false, after);
            if (candidate is not { } current) yield break;
            // An empty completed partial plan has no effect and does not manufacture cancellation work.
            ct.ThrowIfCancellationRequested();
            after = current;
            yield return new FolderChild(current.Entry, current.Synced,
                branches ? FolderVerdict.Removed : FolderVerdict.Survives);
        }
    }

    /// <summary>Streams the exact forget footprint, so each candidate comes from a selected root.</summary>
    private IEnumerable<(Side Side, string Key)> ForgetRoots(CancellationToken ct)
    {
        if (_deleteWhole)
        {
            foreach (var root in MappedRoots(_rootKey, Folder.Synced)) yield return root;
            yield break;
        }
        Candidate? after = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (NextSelected(after) is not { } current) yield break;
            after = current;
            foreach (var root in MappedRoots(current.Key, current.Synced)) yield return root;
        }
    }

    // The deleting side always has a root; the other side has one only through a saved mapping.
    private IEnumerable<(Side Side, string Key)> MappedRoots(string key, SyncedEntry? saved)
    {
        yield return (DeletingFrom, saved?.KeyOn(DeletingFrom) ?? key);
        if (saved is not null) yield return (PathNames.Other(DeletingFrom), saved.KeyOn(PathNames.Other(DeletingFrom)));
    }

    private Candidate? NextSelected(Candidate? after) =>
        after is not { } last
            ? ReadCandidate(StateSchema.ClassifiedSelectedFirst, hasCursor: false, hasBranch: true,
                (StateSchema.ParamTake, StateSchema.OneRow))
            : ReadAfter(StateSchema.ClassifiedSelected, hasBranch: true, after,
                (StateSchema.ParamNegativeDepth, -last.Depth));

    private Candidate? ReadAfter(string sql, bool hasBranch, Candidate? after,
        params (string Name, object? Value)[] arguments) =>
        ReadCandidate(sql, hasCursor: false, hasBranch, [.. arguments, .. Page(after)]);

    private Candidate? ReadCandidate(string sql, bool hasCursor, bool hasBranch,
        params (string Name, object? Value)[] arguments)
    {
        try
        {
            using var command = Command(sql, arguments);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            return ReadFacts(reader, StateSchema.PlanKeyColumn) with
            {
                Cursor = hasCursor ? reader.GetString(StateSchema.PlanCursorColumn) : "",
                Id = hasBranch ? reader.GetInt64(StateSchema.PlanIdColumn) : 0L,
                Depth = hasBranch ? reader.GetInt64(StateSchema.PlanDepthColumn) : 0L,
                SavedOverride = sql == StateSchema.PlanSaved &&
                    StateEncoding.ReadBool(reader, StateSchema.PlanSavedOverrideColumn)
            };
        }
        catch
        {
            Discard();
            throw;
        }
    }

    /// <summary>Decodes the shared 21-field payload starting at <paramref name="first"/>.</summary>
    private static Candidate ReadFacts(SqliteDataReader reader, int first)
    {
        var entry = StateEncoding.ReadBool(reader, first + StateSchema.PlanPresentColumn)
            ? StateEncoding.ReadEntry(reader, first + StateSchema.PlanEntryColumn, localProjection: false) : null;
        var saved = reader.IsDBNull(first + StateSchema.PlanSavedKeyColumn) ? null
            : StateEncoding.ReadSynced(reader, reader.GetString(first + StateSchema.PlanSavedKeyColumn),
                first + StateSchema.PlanSavedColumn);
        if (entry is null && saved is null) throw new InvalidDataException();
        return new Candidate(reader.GetString(first + StateSchema.PlanKeyColumn),
            reader.GetString(first + StateSchema.PlanNativeColumn), entry, saved, "", 0L, 0L, false);
    }

    private static (string Name, object? Value)[] Page(Candidate? after, int pageSize = StateSchema.OneRow) =>
    [
        (StateSchema.ParamPolicy, after?.Key ?? ""), (StateSchema.ParamNative, after?.NativeKey ?? ""),
        (StateSchema.PlanParamPresent, Presence(after)), (StateSchema.ParamTake, pageSize)
    ];

    private static int Presence(Candidate? candidate) =>
        candidate is null ? StateSchema.BeforeFirstPresence : Convert.ToInt32(candidate.Value.Entry is not null);

    // The schema stores a Dropbox value only where it differs from its Local value.
    private static string? Delta(string? value, string? local) => value == local ? null : value;

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] arguments) =>
        StorageCommand.Create(_connection, _transaction, sql, arguments);

    private object? Scalar(string sql, params (string Name, object? Value)[] arguments)
    {
        try
        {
            using var command = Command(sql, arguments);
            return command.ExecuteScalar();
        }
        catch
        {
            Discard();
            throw;
        }
    }

    private bool Exists(string sql, params (string Name, object? Value)[] arguments) =>
        Scalar(sql, arguments) is long value && value != 0;

    private void Execute(string sql, params (string Name, object? Value)[] arguments)
    {
        try
        {
            using var command = Command(sql, arguments);
            command.ExecuteNonQuery();
        }
        catch
        {
            Discard();
            throw;
        }
    }

    private void Require(Phase phase)
    {
        ObjectDisposedException.ThrowIf(_phase == Phase.Disposed, this);
        if (_phase != phase) throw new InvalidOperationException();
    }

    public void Dispose()
    {
        if (_registration is not { } registration) return;
        _phase = Phase.Disposed;
        _current = null;
        try
        {
            _transaction?.Dispose();
        }
        finally
        {
            _transaction = null;
            _connection.Dispose();
        }
        // The path stays owned when cleanup is refused; the next same-pair open retries and surfaces the failure.
        try { File.Delete(_path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _registration = null;
            return;
        }
        _registration = null;
        registration.ReleasePlan(_path);
    }

    private void Discard()
    {
        try { Dispose(); }
        catch { /* An existing capture, decode or commit exception keeps its original identity. */ }
    }

    private readonly record struct Candidate(string Key, string NativeKey, Entry? Entry, SyncedEntry? Synced,
        string Cursor, long Id, long Depth, bool SavedOverride);

    private enum Phase
    {
        New,
        Capturing,
        Captured,
        Classifying,
        Classified,
        Prepared,
        Selecting,
        Selected,
        Completed,
        Faulted,
        Disposed
    }
}

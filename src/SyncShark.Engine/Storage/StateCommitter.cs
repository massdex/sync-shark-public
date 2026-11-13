using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Applies one StateChange inside the caller's transaction, in the order of section 3.5.</summary>
public static class StateCommitter
{
    /// <summary>Applies the change; the caller states whether first-run Record writes keep the in-place update path,
    /// and raised collects each raised issue for the caller to report once its transaction has committed.</summary>
    // Each stage reads what the previous stage wrote within the caller's transaction.
    internal static void Commit(SqliteConnection connection, SqliteTransaction transaction, ChangeTable changes, string pairId,
        StateChange change, DateTimeOffset now, bool narrowRecordUpdates, ICollection<Issue>? raised, IReadOnlyList<ScanPartition> scans,
        ICollection<(Issue Issue, bool IsNew)>? settled = null)
    {
        ValidateChange(change, scans);
        foreach (var rename in change.Renames)
            ApplyRename(connection, transaction, pairId, rename, scans, change);

        HashSet<(Side Side, string Path, string Id)>? released = null;
        ForgetBindings(connection, transaction, pairId, change, ref released, scans);
        // M-B62 mutant: the pre-forget check runs after the forget.
        if (change.SettleRepair is { } settle) DuplicateRepairs.RequirePreForget(connection, transaction, pairId, settle);
        RetireStaleBindings(connection, transaction, pairId, change, ref released, scans);
        RecordBindings(connection, transaction, pairId, change, narrowRecordUpdates, ref released, scans);

        if (released is not null)
            foreach (var endpoint in released)
                changes.RegisterReleasedEndpoint(transaction, pairId, endpoint.Side, endpoint.Path, endpoint.Id);

        if (change.SettleRepair is { } settlement)
            DuplicateRepairs.Settle(connection, transaction, changes, pairId, settlement, now, raised);
        if (change.Release is { } release) DuplicateRepairs.Release(connection, transaction, pairId, release);
        // Acknowledged first: Close leaves a Failure in place while it still carries unfinished first-run work.
        foreach (var item in change.FirstRunAcknowledged)
            IssueTable.AcknowledgeFirstRun(connection, transaction, pairId, item);
        foreach (var (path, kinds) in change.Close)
            IssueTable.Close(connection, transaction, pairId, path, kinds, change.CloseScope);
        foreach (var (kind, cause, path, subject) in change.Raise)
            IssueTable.Raise(connection, transaction, pairId, kind, cause, path, subject, now, raised, settled);
        // The hold commit raises exactly its one Failure; the hold binds to whatever Failure that raise left there.
        if (change.HoldRepair is { } hold)
            DuplicateRepairs.Hold(connection, transaction, pairId, hold, change.Raise.Single().Path);
    }

    private static void ValidateChange(StateChange change, IReadOnlyList<ScanPartition> scans)
    {
        if (change.ListedScanId is { } token && !scans.Any(scan => scan.Record.Token == token && scan.Active))
            throw new ChangedUnderneathException(change.Record.FirstOrDefault()?.LocalPath ?? change.Renames.FirstOrDefault()?.After.LocalPath ?? string.Empty);
        foreach (var scan in scans) scan.Guard(change);
        if (change.FirstRunAcknowledged.Any(item => item.Side != Side.Local))
            throw new ArgumentException(null, nameof(change));
        foreach (var entry in change.Record) RequireLocalKey(entry);
        foreach (var rename in change.Renames)
        {
            if (rename.Before is { } before) RequireLocalKey(before);
            RequireLocalKey(rename.After);
        }
    }

    private static void ForgetBindings(SqliteConnection connection, SqliteTransaction transaction, string pairId,
        StateChange change, ref HashSet<(Side Side, string Path, string Id)>? released, IReadOnlyList<ScanPartition> scans)
    {
        foreach (var (side, key) in change.ForgetSelf)
        {
            var spent = RetireBindings(connection, transaction, pairId, change, ref released, scans, true,
                side == Side.Local ? StateSchema.DeleteSyncedLocalKey : StateSchema.DeleteSyncedDropboxKey,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamTargetKey, key));
            if (side == Side.Local && !spent) DeletedHistory.Spend(connection, transaction, pairId, PathNames.DropboxKey(key));
        }

        var retained = change.ForgetSubtrees.Count != 0 || change.ForgetBothSubtrees.Count != 0
            ? JsonSerializer.Serialize(change.Record.Select(entry => entry.LocalKey)
                .Concat(change.Renames.Select(rename => rename.After.LocalKey))) : null;

        foreach (var (side, key) in change.ForgetSubtrees)
        {
            foreach (var scan in scans) scan.Touch(side, key, true);
            Execute(connection, transaction,
                side == Side.Local ? StateSchema.DeleteSyncedSubtreeLocal : StateSchema.DeleteSyncedSubtreeDropbox,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamTargetKey, key), (StateSchema.ParamRetained, retained));
        }

        foreach (var (localKey, dropboxKey) in change.ForgetBothSubtrees)
        {
            foreach (var scan in scans)
            {
                scan.Touch(Side.Local, localKey, true);
                scan.Touch(Side.Dropbox, dropboxKey, true);
            }
            Execute(connection, transaction, StateSchema.DeleteSyncedBothSubtrees,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamLocalKey, localKey), (StateSchema.ParamDropboxKey, dropboxKey),
                (StateSchema.ParamRetained, retained));
        }

    }

    private static void RecordBindings(SqliteConnection connection, SqliteTransaction transaction, string pairId,
        StateChange change, bool narrowRecordUpdates, ref HashSet<(Side Side, string Path, string Id)>? released,
        IReadOnlyList<ScanPartition> scans)
    {
        foreach (var entry in change.Record)
        {
            foreach (var scan in scans) scan.Touch(entry, change);
            // A boundary belongs only to the row holding the identity it was written for; every other Record clears it.
            var published = change.LocalPublished is { } own && own.LocalId == entry.LocalId && !entry.IsFolder
                ? own.Position : null;
            if (narrowRecordUpdates && TryUpdateSameKeys(connection, transaction, pairId, entry, published)) continue;
            RetireBindings(connection, transaction, pairId, change, ref released, scans, true, StateSchema.DeleteSyncedEitherKey,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamLocalKey, entry.LocalKey),
                (StateSchema.ParamDropboxKey, entry.DropboxKey));
            Insert(connection, transaction, pairId, entry, published);
        }
    }

    internal static void ForgetAll(SqliteConnection connection, SqliteTransaction transaction, string pairId,
        IReadOnlyList<ScanPartition> scans)
    {
        foreach (var scan in scans)
        {
            scan.Touch(Side.Local, string.Empty, true);
            scan.Touch(Side.Dropbox, string.Empty, true);
        }
        Execute(connection, transaction, StateSchema.ForgetAllSynced, (StateSchema.ParamPair, pairId));
    }

    internal static Side? ReleasedSide(SyncedEntry removed, StateChange change)
    {
        if (removed.IsFolder || removed.LocalKey == removed.DropboxKey) return null;
        var local = change.Record.Any(entry => entry.LocalKey == removed.LocalKey);
        var dropbox = change.Record.Any(entry => entry.DropboxKey == removed.DropboxKey);
        return local == dropbox ? null : local ? Side.Dropbox : Side.Local;
    }

    private static void RetireStaleBindings(SqliteConnection connection, SqliteTransaction transaction,
        string pairId, StateChange change, ref HashSet<(Side Side, string Path, string Id)>? released, IReadOnlyList<ScanPartition> scans)
    {
        foreach (var before in change.StaleBindings)
        {
            RequireLocalKey(before);
            if (!change.Record.Any(entry => entry.IsRelocationOf(before)))
                throw new ArgumentException(null, nameof(change));
            using (var command = StorageCommand.Create(connection, transaction, StateSchema.SelectSyncedByLocalKey,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamTargetKey, before.LocalKey)))
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read() || StateEncoding.ReadSynced(reader,
                    reader.GetString(StateSchema.SyncedLocalKey), StateSchema.SyncedLocalPath) != before) continue;
            }
            RetireBindings(connection, transaction, pairId, change, ref released, scans, false, StateSchema.DeleteSyncedLocalKey,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamTargetKey, before.LocalKey));
        }
    }

    private static bool RetireBindings(SqliteConnection connection, SqliteTransaction transaction,
        string pairId, StateChange change, ref HashSet<(Side Side, string Path, string Id)>? released,
        IReadOnlyList<ScanPartition> scans, bool releaseEndpoints, string sql, params (string Name, object? Value)[] arguments)
    {
        // Exact-key deletes return at most the two endpoint collisions, never a whole subtree.
        var retired = new List<SyncedEntry>();
        using (var command = StorageCommand.Create(connection, transaction, sql, arguments))
        using (var reader = command.ExecuteReader())
        {
            do
            {
                while (reader.Read())
                    retired.Add(StateEncoding.ReadSynced(reader,
                        reader.GetString(StateSchema.SyncedLocalKey), StateSchema.SyncedLocalPath));
            } while (reader.NextResult());
        }
        var spent = false;
        foreach (var entry in retired)
        {
            foreach (var scan in scans) scan.Touch(entry, change, retiring: true);
            if (releaseEndpoints && ReleasedSide(entry, change) is { } side)
                (released ??= []).Add((side, entry.PathOn(side), entry.IdOn(side)));
            if (!change.Record.Any(record => record.LocalKey == entry.LocalKey) &&
                !change.Renames.Any(rename => rename.After.LocalKey == entry.LocalKey))
            {
                DeletedHistory.Spend(connection, transaction, pairId, PathNames.DropboxKey(entry.LocalPath));
                spent = true;
            }
        }
        return spent;
    }

    private static void RequireLocalKey(SyncedEntry entry)
    {
        if (entry.LocalPath is null || entry.LocalKey != PathNames.Key(entry.LocalPath))
            throw new ArgumentException(Text.Get(TextKey.StateLocalKeyInvalid,
                (TextArg.Path, entry.LocalKey)), nameof(entry));
    }

    private static void ApplyRename(SqliteConnection connection, SqliteTransaction transaction, string pairId,
        RenameApplied rename, IReadOnlyList<ScanPartition> scans, StateChange change)
    {
        var before = rename.Before;
        var after = rename.After;
        var hasCounterpart = Counterpart(rename, out var counterpartSide, out var counterpartOldPath,
            out var counterpartNewPath);
        RequireRenameDestination(connection, transaction, pairId, before, after);
        TouchRenamedScans(scans, change, before, after, hasCounterpart, counterpartSide, counterpartOldPath, counterpartNewPath);
        if (before is null)
        {
            InsertUnrecordedRename(connection, transaction, pairId, after, hasCounterpart,
                counterpartSide, counterpartOldPath, counterpartNewPath);
        }
        else
        {
            Execute(connection, transaction, StateSchema.RenameSyncedRow,
                (StateSchema.ParamPair, pairId),
                (StateSchema.ParamOldLocalKey, before.LocalKey),
                (StateSchema.ParamNewLocalPath, after.LocalPath),
                (StateSchema.ParamNewLocalKey, after.LocalKey),
                (StateSchema.ParamNewDropboxKey, after.DropboxKey),
                (StateSchema.ParamNewDropboxPath, after.DropboxPath),
                (StateSchema.ParamLocalId, StateEncoding.LocalId(after.LocalId)),
                (StateSchema.ParamDropboxId, after.DropboxId));
            if (before.IsFolder)
            {
                RebaseRenamedDescendants(connection, transaction, pairId, before, after);
            }
        }

        if (before is not null) RequireUniqueDropboxKey(connection, transaction, pairId, after);
        if (after.IsFolder)
            RequireNoClash(connection, transaction, StateSchema.ClashInDropboxSubtree, after.DropboxPath,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamTargetKey, after.DropboxKey));

        if (hasCounterpart)
            MoveCounterpartScans(connection, transaction, pairId, scans, counterpartSide,
                counterpartOldPath, counterpartNewPath);
    }

    private static void InsertUnrecordedRename(SqliteConnection connection, SqliteTransaction transaction,
        string pairId, SyncedEntry after, bool hasCounterpart, Side counterpartSide,
        string counterpartOldPath, string counterpartNewPath)
    {
        Insert(connection, transaction, pairId, after, null);
        // A folder move with no saved record still rebases the descendants already saved under it.
        if (after.IsFolder && hasCounterpart &&
            !string.Equals(counterpartOldPath, counterpartNewPath, StringComparison.Ordinal))
            Execute(connection, transaction,
                counterpartSide == Side.Local ? StateSchema.RebaseDescendantsLocal : StateSchema.RebaseDescendantsDropbox,
                (StateSchema.ParamPair, pairId),
                (StateSchema.ParamOldKey, PathNames.KeyFor(counterpartSide, counterpartOldPath)),
                (StateSchema.ParamNewKey, PathNames.KeyFor(counterpartSide, counterpartNewPath)),
                (StateSchema.ParamOldPath, counterpartOldPath), (StateSchema.ParamNewPath, counterpartNewPath));
    }

    private static void MoveCounterpartScans(SqliteConnection connection, SqliteTransaction transaction,
        string pairId, IReadOnlyList<ScanPartition> scans, Side counterpartSide,
        string counterpartOldPath, string counterpartNewPath)
    {
        var oldOwnKey = PathNames.Key(counterpartOldPath);
        var newOwnKey = PathNames.Key(counterpartNewPath);
        foreach (var scan in scans)
            Execute(connection, transaction, StateSchema.MoveScanSubtree,
            (StateSchema.ParamScan, scan.Key), (StateSchema.ParamPair, pairId), (StateSchema.ParamSide, (int)counterpartSide),
            (StateSchema.ParamOldOwnKey, oldOwnKey), (StateSchema.ParamNewOwnKey, newOwnKey),
            (StateSchema.ParamOldKey, PathNames.KeyFor(counterpartSide, counterpartOldPath, oldOwnKey)),
            (StateSchema.ParamNewKey, PathNames.KeyFor(counterpartSide, counterpartNewPath, newOwnKey)),
            (StateSchema.ParamOldPath, counterpartOldPath), (StateSchema.ParamNewPath, counterpartNewPath));
    }

    private static void TouchRenamedScans(IReadOnlyList<ScanPartition> scans, StateChange change,
        SyncedEntry? before, SyncedEntry after, bool hasCounterpart, Side counterpartSide,
        string counterpartOldPath, string counterpartNewPath)
    {
        foreach (var scan in scans)
        {
            // Same-scan listed adoption preserves that listing's mapping; external and fresh writes still invalidate it.
            if (change.ListedScanId == scan.Record.Token) continue;
            if (before is not null) scan.Touch(before, change, retiring: before.IsFolder);
            scan.Touch(after, change, retiring: after.IsFolder);
            if (after.IsFolder && hasCounterpart)
            {
                scan.Touch(counterpartSide, PathNames.KeyFor(counterpartSide, counterpartOldPath), true);
                scan.Touch(counterpartSide, PathNames.KeyFor(counterpartSide, counterpartNewPath), true);
            }
        }
    }

    private static void RebaseRenamedDescendants(SqliteConnection connection, SqliteTransaction transaction,
        string pairId, SyncedEntry before, SyncedEntry after)
    {
        var localChanged = !string.Equals(before.LocalPath, after.LocalPath, StringComparison.Ordinal);
        var dropboxChanged = !string.Equals(before.DropboxPath, after.DropboxPath, StringComparison.Ordinal);
        if (localChanged && dropboxChanged)
            Execute(connection, transaction, StateSchema.RebaseDescendantsBoth,
                (StateSchema.ParamPair, pairId),
                (StateSchema.ParamOldLocalKey, before.LocalKey),
                (StateSchema.ParamOldLocalPath, before.LocalPath),
                (StateSchema.ParamNewLocalKey, after.LocalKey),
                (StateSchema.ParamNewLocalPath, after.LocalPath),
                (StateSchema.ParamOldDropboxKey, before.DropboxKey),
                (StateSchema.ParamOldDropboxPath, before.DropboxPath),
                (StateSchema.ParamNewDropboxKey, after.DropboxKey),
                (StateSchema.ParamNewDropboxPath, after.DropboxPath));
        else if (localChanged)
            Execute(connection, transaction, StateSchema.RebaseDescendantsLocal,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamOldKey, before.LocalKey),
                (StateSchema.ParamNewKey, after.LocalKey), (StateSchema.ParamOldPath, before.LocalPath),
                (StateSchema.ParamNewPath, after.LocalPath));
        else if (dropboxChanged)
            Execute(connection, transaction, StateSchema.RebaseDescendantsDropbox,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamOldKey, before.DropboxKey),
                (StateSchema.ParamNewKey, after.DropboxKey), (StateSchema.ParamOldPath, before.DropboxPath),
                (StateSchema.ParamNewPath, after.DropboxPath));
    }

    private static void RequireRenameDestination(SqliteConnection connection, SqliteTransaction transaction,
        string pairId, SyncedEntry? before, SyncedEntry after)
    {
        if (RenameDestinations.FirstOther(connection, transaction, pairId, before, after) is { } side)
            throw new PathOccupiedException(after.PathOn(side), side);
    }

    /// <summary>Recognizes the three counterpart inputs once; false unless all three are present.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static bool Counterpart(RenameApplied rename, out Side side, out string oldPath, out string newPath)
    {
        side = rename.CounterpartSide ?? default;
        oldPath = rename.CounterpartOldPath!;
        newPath = rename.CounterpartNewPath!;
        return rename.CounterpartSide is not null && oldPath is not null && newPath is not null;
    }

    internal static void Insert(SqliteConnection connection, SqliteTransaction transaction, string pairId,
        SyncedEntry entry, JournalPosition? published)
    {
        RequireLocalKey(entry);
        Execute(connection, transaction, StateSchema.InsertSynced, RecordArguments(pairId, entry, published));
        RequireUniqueDropboxKey(connection, transaction, pairId, entry);
    }

    private static bool TryUpdateSameKeys(SqliteConnection connection, SqliteTransaction transaction, string pairId,
        SyncedEntry entry, JournalPosition? published) =>
        Execute(connection, transaction, StateSchema.UpdateSyncedSameKeys, RecordArguments(pairId, entry, published)) == 1;

    private static (string Name, object? Value)[] RecordArguments(string pairId, SyncedEntry entry,
        JournalPosition? published) =>
    [
        (StateSchema.ParamPair, pairId),
        (StateSchema.ParamLocalKey, entry.LocalKey),
        (StateSchema.ParamLocalPath, entry.LocalPath),
        (StateSchema.ParamDropboxKey, entry.DropboxKey), (StateSchema.ParamDropboxPath, entry.DropboxPath),
        (StateSchema.ParamIsFolder, entry.IsFolder), (StateSchema.ParamLocalId, StateEncoding.LocalId(entry.LocalId)),
        (StateSchema.ParamLocalSize, entry.LocalSize), (StateSchema.ParamLocalMtime, entry.LocalModifiedTicks),
        (StateSchema.ParamDropboxId, entry.DropboxId), (StateSchema.ParamDropboxHash, StateEncoding.Hash(entry.DropboxContentHash)),
        (StateSchema.ParamPublishedJournal, published is null ? null : ChangeTable.Signed(published.JournalId)),
        (StateSchema.ParamPublishedUsn, published is null ? null : ChangeTable.Signed(published.NextUsn)),
    ];

    private static void RequireUniqueDropboxKey(SqliteConnection connection, SqliteTransaction transaction,
        string pairId, SyncedEntry entry) =>
        RequireNoClash(connection, transaction,
            entry.DropboxKey == entry.LocalKey ? StateSchema.ClashWithOverride : StateSchema.ClashWithInherited,
            entry.DropboxPath, (StateSchema.ParamPair, pairId), (StateSchema.ParamLocalKey, entry.LocalKey),
            (StateSchema.ParamDropboxKey, entry.DropboxKey));

    private static void RequireNoClash(SqliteConnection connection, SqliteTransaction transaction,
        string sql, string path, params (string Name, object? Value)[] arguments)
    {
        using var command = StorageCommand.Create(connection, transaction, sql, arguments);
        if ((long)command.ExecuteScalar()! != 0)
            throw new InvalidOperationException(Text.Get(TextKey.ClashNameTaken, (TextArg.Path, path)));
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction transaction, string sql,
        params (string Name, object? Value)[] arguments)
    {
        using var command = StorageCommand.Create(connection, transaction, sql, arguments);
        return command.ExecuteNonQuery();
    }
}

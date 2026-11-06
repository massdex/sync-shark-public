using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Applies one StateChange inside the caller's transaction, in the order of section 3.5.</summary>
public static class StateCommitter
{
    /// <summary>Applies the change; the caller states whether first-run Record writes keep the in-place update path,
    /// and raised collects each raised issue for the caller to report once its transaction has committed.</summary>
    internal static void Commit(SqliteConnection connection, SqliteTransaction transaction, ChangeTable changes, string pairId,
        StateChange change, DateTimeOffset now, bool narrowRecordUpdates, ICollection<Issue>? raised, IEnumerable<byte[]>? scans = null)
    {
        if (change.FirstRunAcknowledged.Any(item => item.Side != Side.Local))
            throw new ArgumentException(null, nameof(change));
        foreach (var entry in change.Record) RequireLocalKey(entry);
        foreach (var rename in change.Renames)
        {
            if (rename.Before is { } before) RequireLocalKey(before);
            RequireLocalKey(rename.After);
        }
        foreach (var rename in change.Renames)
            ApplyRename(connection, transaction, pairId, rename, scans);

        HashSet<(Side Side, string Path, string Id)>? released = null;
        foreach (var (side, key) in change.ForgetSelf)
        {
            var spent = RetireBindings(connection, transaction, pairId, change, ref released, true,
                side == Side.Local ? StateSchema.DeleteSyncedLocalKey : StateSchema.DeleteSyncedDropboxKey,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamTargetKey, key));
            if (side == Side.Local && !spent) DeletedHistory.Spend(connection, transaction, pairId, PathNames.DropboxKey(key));
        }

        var retained = change.ForgetSubtrees.Count != 0 || change.ForgetBothSubtrees.Count != 0
            ? JsonSerializer.Serialize(change.Record.Select(entry => entry.LocalKey)
                .Concat(change.Renames.Select(rename => rename.After.LocalKey))) : null;

        foreach (var (side, key) in change.ForgetSubtrees)
            Execute(connection, transaction,
                side == Side.Local ? StateSchema.DeleteSyncedSubtreeLocal : StateSchema.DeleteSyncedSubtreeDropbox,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamTargetKey, key), (StateSchema.ParamRetained, retained));

        foreach (var (localKey, dropboxKey) in change.ForgetBothSubtrees)
            Execute(connection, transaction, StateSchema.DeleteSyncedBothSubtrees,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamLocalKey, localKey), (StateSchema.ParamDropboxKey, dropboxKey),
                (StateSchema.ParamRetained, retained));

        RetireStaleBindings(connection, transaction, pairId, change, ref released);
        foreach (var entry in change.Record)
        {
            if (narrowRecordUpdates && TryUpdateSameKeys(connection, transaction, pairId, entry)) continue;
            RetireBindings(connection, transaction, pairId, change, ref released, true, StateSchema.DeleteSyncedEitherKey,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamLocalKey, entry.LocalKey),
                (StateSchema.ParamDropboxKey, entry.DropboxKey));
            Insert(connection, transaction, pairId, entry);
        }

        if (released is not null)
            foreach (var endpoint in released)
                changes.RegisterReleasedEndpoint(transaction, pairId, endpoint.Side, endpoint.Path, endpoint.Id);

        // Acknowledged first: Close leaves a Failure in place while it still carries unfinished first-run work.
        foreach (var item in change.FirstRunAcknowledged)
            IssueTable.AcknowledgeFirstRun(connection, transaction, pairId, item);
        foreach (var (path, kinds) in change.Close) IssueTable.Close(connection, transaction, pairId, path, kinds);
        foreach (var (kind, cause, path, subject) in change.Raise)
            IssueTable.Raise(connection, transaction, pairId, kind, cause, path, subject, now, raised);
    }

    internal static Side? ReleasedSide(SyncedEntry removed, StateChange change)
    {
        if (removed.IsFolder || removed.LocalKey == removed.DropboxKey) return null;
        var local = change.Record.Any(entry => entry.LocalKey == removed.LocalKey);
        var dropbox = change.Record.Any(entry => entry.DropboxKey == removed.DropboxKey);
        // MUTANT: a retirement that neither side records still releases the local endpoint as work.
        return local == dropbox ? (local ? null : Side.Local) : local ? Side.Dropbox : Side.Local;
    }

    private static void RetireStaleBindings(SqliteConnection connection, SqliteTransaction transaction,
        string pairId, StateChange change, ref HashSet<(Side Side, string Path, string Id)>? released)
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
            RetireBindings(connection, transaction, pairId, change, ref released, false, StateSchema.DeleteSyncedLocalKey,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamTargetKey, before.LocalKey));
        }
    }

    private static bool RetireBindings(SqliteConnection connection, SqliteTransaction transaction,
        string pairId, StateChange change, ref HashSet<(Side Side, string Path, string Id)>? released,
        bool releaseEndpoints, string sql, params (string Name, object? Value)[] arguments)
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
        RenameApplied rename, IEnumerable<byte[]>? scans)
    {
        var before = rename.Before;
        var after = rename.After;
        var hasCounterpart = Counterpart(rename, out var counterpartSide, out var counterpartOldPath,
            out var counterpartNewPath);
        RequireRenameDestination(connection, transaction, pairId, before, after);
        if (before is null)
        {
            Insert(connection, transaction, pairId, after);
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
        }

        if (before is not null) RequireUniqueDropboxKey(connection, transaction, pairId, after);
        if (after.IsFolder)
            RequireNoClash(connection, transaction, StateSchema.ClashInDropboxSubtree, after.DropboxPath,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamTargetKey, after.DropboxKey));

        if (hasCounterpart)
        {
            var oldOwnKey = PathNames.Key(counterpartOldPath);
            var newOwnKey = PathNames.Key(counterpartNewPath);
            foreach (var scan in scans ?? [])
                Execute(connection, transaction, StateSchema.MoveScanSubtree,
                (StateSchema.ParamScan, scan), (StateSchema.ParamPair, pairId), (StateSchema.ParamSide, (int)counterpartSide),
                (StateSchema.ParamOldOwnKey, oldOwnKey), (StateSchema.ParamNewOwnKey, newOwnKey),
                (StateSchema.ParamOldKey, PathNames.KeyFor(counterpartSide, counterpartOldPath, oldOwnKey)),
                (StateSchema.ParamNewKey, PathNames.KeyFor(counterpartSide, counterpartNewPath, newOwnKey)),
                (StateSchema.ParamOldPath, counterpartOldPath), (StateSchema.ParamNewPath, counterpartNewPath));
        }
    }

    private static void RequireRenameDestination(SqliteConnection connection, SqliteTransaction transaction,
        string pairId, SyncedEntry? before, SyncedEntry after)
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            using var command = StorageCommand.Create(connection, transaction,
                side == Side.Local ? StateSchema.SelectSyncedByLocalKey : StateSchema.SelectSyncedByDropboxKey,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamTargetKey, after.KeyOn(side)));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var destination = StateEncoding.ReadSynced(reader,
                    reader.GetString(StateSchema.SyncedLocalKey), StateSchema.SyncedLocalPath);
                if (destination.LocalKey == before?.LocalKey) continue;
                // Matching identities do not prove the old endpoints absent; only prepared Record can retire them.
                throw new PathOccupiedException(after.PathOn(side), side);
            }
        }
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
        SyncedEntry entry)
    {
        RequireLocalKey(entry);
        Execute(connection, transaction, StateSchema.InsertSynced, RecordArguments(pairId, entry));
        RequireUniqueDropboxKey(connection, transaction, pairId, entry);
    }

    private static bool TryUpdateSameKeys(SqliteConnection connection, SqliteTransaction transaction, string pairId,
        SyncedEntry entry) =>
        Execute(connection, transaction, StateSchema.UpdateSyncedSameKeys, RecordArguments(pairId, entry)) == 1;

    private static (string Name, object? Value)[] RecordArguments(string pairId, SyncedEntry entry) =>
    [
        (StateSchema.ParamPair, pairId),
        (StateSchema.ParamLocalKey, entry.LocalKey),
        (StateSchema.ParamLocalPath, entry.LocalPath),
        (StateSchema.ParamDropboxKey, entry.DropboxKey), (StateSchema.ParamDropboxPath, entry.DropboxPath),
        (StateSchema.ParamIsFolder, entry.IsFolder), (StateSchema.ParamLocalId, StateEncoding.LocalId(entry.LocalId)),
        (StateSchema.ParamLocalSize, entry.LocalSize), (StateSchema.ParamLocalMtime, entry.LocalModifiedTicks),
        (StateSchema.ParamDropboxId, entry.DropboxId), (StateSchema.ParamDropboxHash, StateEncoding.Hash(entry.DropboxContentHash)),
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

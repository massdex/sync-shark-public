using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    // These lookups never materialize all notices, pending intents or Recovery history.
    public (Notice Notice, bool Closed)? NoticeById(string pairId, string noticeId) => InStore(() =>
        PairNo(pairId) is { } pairNo ? _notices.ById(pairNo, noticeId) : null);

    public void SaveConflictOperation(string pairId, string noticeId, ConflictNoticeOperation operation) => InStore(() =>
    {
        if (PairNo(pairId) is not { } pairNo) throw new InvalidOperationException("The pair is unavailable.");
        var pair = PairRecord(pairId) ?? throw new InvalidOperationException("The pair is unavailable.");
        using var transaction = _connection.BeginTransaction();
        var saved = _notices.ById(pairNo, noticeId, transaction);
        if (saved is null || saved.Value.Closed || saved.Value.Notice.ConflictVersions is null)
            throw new InvalidOperationException("The conflict notice is no longer open.");
        var prior = saved.Value.Notice.ConflictOperation;
        if (operation.MergeBytes is { } mergeBytes)
        {
            if (prior?.MergeBytes is { } legacyBytes && !legacyBytes.SequenceEqual(mergeBytes))
                throw new InvalidOperationException("The saved merge content cannot change.");
            if (prior?.MergeContentId is { } priorContent &&
                StoreSchema.Scalar(_connection, transaction, "SELECT content FROM choice_merge WHERE notice_id=$id;",
                    ("$id", priorContent)) is byte[] savedBytes && !savedBytes.SequenceEqual(mergeBytes))
                throw new InvalidOperationException("The saved merge content cannot change.");
            StoreSchema.Execute(_connection, transaction,
                "INSERT INTO choice_merge(notice_id,content) VALUES($id,$bytes) ON CONFLICT(notice_id) DO NOTHING;",
                ("$id", noticeId), ("$bytes", mergeBytes));
            operation = operation with { MergeBytes = null, MergeContentId = noticeId };
            if (prior?.MergeBytes is not null) prior = prior with { MergeBytes = null, MergeContentId = noticeId };
        }
        if (prior is not null && (prior.Choice != operation.Choice || prior.Destination != operation.Destination ||
            !prior.Actions.SequenceEqual(operation.Actions) ||
            prior.MergeContentId != operation.MergeContentId || prior.Restoring && !operation.Restoring))
            throw new InvalidOperationException("An earlier choice is still in progress. Finish or reconcile it first.");
        if (prior?.RestorePath is not null && prior.RestorePath != operation.RestorePath)
        {
            if (operation.RestorePath is null) throw new InvalidOperationException("A copy retry needs a new copy destination.");
            // A fresh copy must include everything created in the interrupted copy. Old items and
            // Recovery stay untouched; state reset and destination checkpoint are one transaction.
            foreach (var action in operation.Actions.Where(action => action.Kind == ActionKind.Delete))
                StoreSchema.Execute(_connection, transaction,
                    "UPDATE choice_victim SET state=$removed WHERE op_id=$op AND state=$copied AND depth>=0;",
                    ("$removed", (long)ChoiceVictimState.Removed), ("$op", action.ActionId),
                    ("$copied", (long)ChoiceVictimState.RestoredCopy));
        }
        // The first manifest and its victim/source snapshots commit together. No retry may freeze a later tree.
        if (prior is null)
        {
            foreach (var action in operation.Actions.Where(FolderExpansion.Expands))
                FreezeChoiceVictims(transaction, pairId, action.ActionId,
                    action.Target == Side.A ? pair.A.RootId : pair.B.RootId, action.Path);
            if (operation.Choice == NoticeChoices.ReplaceExisting &&
                saved.Value.Notice.ConflictVersions.Incoming is { } incoming)
            {
                var source = incoming.Target == Side.A ? saved.Value.Notice.ConflictVersions.B : saved.Value.Notice.ConflictVersions.A;
                if (source.Kind == ItemKind.Folder)
                    FreezeChoiceVictims(transaction, pairId, noticeId + ":arrival",
                        incoming.Target == Side.A ? pair.B.RootId : pair.A.RootId, source.Path);
            }
        }
        var updated = saved.Value.Notice with { ConflictOperation = operation };
        StoreSchema.Execute(_connection, transaction, "UPDATE notice SET detail=$detail WHERE notice_id=$id AND pair_no=$p;",
            ("$detail", NoticeDetailCodec.Encode(updated)), ("$id", noticeId), ("$p", pairNo));
        transaction.Commit();
    });

    public void ForgetRestoreReceipts(string pairId, IReadOnlyList<string> actionIds) => InStore(() =>
    {
        using var transaction = _connection.BeginTransaction();
        foreach (var id in actionIds)
            StoreSchema.Execute(_connection, transaction,
                "DELETE FROM choice_restore_receipt WHERE pair_no=$p AND action_id=$id;", ("$p", PairNo(pairId)), ("$id", id));
        transaction.Commit();
    });

    public byte[]? ConflictMergeBytes(string noticeId) => InStore(() =>
        StoreSchema.Scalar(_connection, null, "SELECT content FROM choice_merge WHERE notice_id=$id;", ("$id", noticeId)) as byte[]);

    public void ForgetConflictPayload(string noticeId) => InStore(() =>
        StoreSchema.Execute(_connection, null, "DELETE FROM choice_merge WHERE notice_id=$id;", ("$id", noticeId)));

    public bool ConflictChoiceHasPending(string pairId, string operationId) => InStore(() =>
        StoreSchema.Scalar(_connection, null,
            "SELECT 1 FROM pending_effect WHERE pair_no=$p AND (action_id=$op OR (action_id>$lo AND action_id<$hi)) LIMIT 1;",
            ("$p", PairNo(pairId)), ("$op", operationId), ("$lo", operationId + ":"), ("$hi", operationId + ";")) is not null);

    public IReadOnlyList<SyncAction> RestoreIntentsPage(string pairId, int limit) => InStore(() =>
    {
        using var command = StoreSchema.Command(_connection, null,
            "SELECT action_json FROM pending_effect WHERE pair_no=$p AND json_extract(action_json,'$.ConflictRestore')=1 LIMIT $limit;");
        command.Parameters.AddWithValue("$p", (object?)PairNo(pairId) ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 4096));
        using var reader = command.ExecuteReader();
        var page = new List<SyncAction>();
        while (reader.Read()) page.Add(JsonSerializer.Deserialize<SyncAction>(reader.GetString(0), RowCodec.Json)!);
        return (IReadOnlyList<SyncAction>)page;
    });

    public ItemState? ConflictRestoreReceipt(string pairId, string actionId) => InStore(() =>
    {
        var json = StoreSchema.Scalar(_connection, null,
            "SELECT item_json FROM choice_restore_receipt WHERE pair_no=$p AND action_id=$id;",
            ("$p", PairNo(pairId)), ("$id", actionId)) as string;
        return json is null ? null : JsonSerializer.Deserialize<ItemState>(json, RowCodec.Json);
    });

    public SyncAction? IntentById(string pairId, string actionId) => InStore(() =>
    {
        if (PairNo(pairId) is not { } pairNo) return null;
        var json = StoreSchema.Scalar(_connection, null,
            "SELECT action_json FROM pending_effect WHERE action_id=$id AND pair_no=$p;",
            ("$id", actionId), ("$p", pairNo)) as string;
        return json is null ? null : JsonSerializer.Deserialize<SyncAction>(json, RowCodec.Json);
    });

    public RecoveryRecord? RecoveryForVersion(string pairId, Side side, ItemState version) => InStore(() =>
    {
        if (PairNo(pairId) is not { } pairNo || version.Hash is null) return null;
        // Explicit per-notice operation only: SQLite walks the pair index and returns one row,
        // never a million-item Recovery list in application memory.
        var actionId = StoreSchema.Scalar(_connection, null,
            "SELECT action_id FROM recovery WHERE pair_no=$p AND side=$side AND path=$path AND hash=$hash AND size=$size " +
            "ORDER BY created DESC LIMIT 1;", ("$p", pairNo), ("$side", side.ToString()),
            ("$path", version.Path), ("$hash", version.Hash), ("$size", version.Size)) as string;
        return actionId is null ? null : RecoveryForAction(pairId, actionId);
    });

    public RecoveryRecord? RecoveryForAction(string pairId, string actionId) => InStore(() =>
    {
        if (PairNo(pairId) is not { } pairNo) return null;
        using var command = StoreSchema.Command(_connection, null,
            "SELECT recovery_id,action_id,side,path,location,version_id,hash,size,created,expires " +
            "FROM recovery WHERE recovery_id=$id AND pair_no=$p AND action_id=$action;");
        command.Parameters.AddWithValue("$id", ShortId.Make("rc", pairId, actionId));
        command.Parameters.AddWithValue("$p", pairNo);
        command.Parameters.AddWithValue("$action", actionId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new RecoveryRecord(reader.GetString(0), pairId, reader.GetString(1),
            Enum.Parse<Side>(reader.GetString(2)), reader.GetString(3), reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6), reader.GetInt64(7),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)));
    });
}

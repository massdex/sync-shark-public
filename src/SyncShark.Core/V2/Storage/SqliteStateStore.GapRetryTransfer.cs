namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    internal long GapItemCount(string rootId, string epoch) => InStore(() => Convert.ToInt64(
        StoreSchema.Scalar(_connection, null,
            "SELECT count(*) FROM local_gap_item WHERE root_no=(SELECT root_no FROM root WHERE root_id=$r) AND epoch=$e;",
            ("$r", rootId), ("$e", epoch))));

    internal long TransferGapItemsToRetries(string rootId, string epoch, IReadOnlyList<string> folders, CancellationToken ct)
    {
        if (folders.Count == 0) return 0;
        long transferred = 0;
        var after = string.Empty;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = InIndexingStore(() => TransferGapRetryPage(rootId, epoch, folders, after));
            transferred += page.Transferred;
            if (page.Last is null) return transferred;
            after = page.Last;
        }
    }

    private (string? Last, int Transferred) TransferGapRetryPage(string rootId, string epoch,
        IReadOnlyList<string> folders, string after)
    {
        var root = RootNo(rootId);
        if (root is null || ReadGap(root)?.Epoch != epoch) return (null, 0);
        using var tx = _connection.BeginTransaction();
        var ids = new List<string>();
        using (var command = StoreSchema.Command(_connection, tx,
            "SELECT object_id FROM local_gap_item WHERE root_no=$r AND epoch=$e AND object_id>$after ORDER BY object_id LIMIT $n;"))
        {
            command.Parameters.AddWithValue("$r", root.Value); command.Parameters.AddWithValue("$e", epoch);
            command.Parameters.AddWithValue("$after", after); command.Parameters.AddWithValue("$n", V2Settings.PlanningObservationBatchRows);
            using var reader = command.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetString(0));
        }
        var transferred = 0;
        foreach (var id in ids)
        {
            if (!AllGapAliasesUnderRetries(root.Value, id, folders, tx) || _reads.ByObject(root.Value, id, tx) is not { } item) continue;
            MarkRehash(root.Value, item, MessageCatalog.Default.Template("gap-unreadable-transfer"), tx);
            StoreSchema.Execute(_connection, tx,
                "DELETE FROM local_gap_item WHERE root_no=$r AND epoch=$e AND object_id=$o;",
                ("$r", root.Value), ("$e", epoch), ("$o", id));
            transferred++;
        }
        tx.Commit();
        return (ids.Count == 0 ? null : ids[^1], transferred);
    }

    private bool AllGapAliasesUnderRetries(long root, string id, IReadOnlyList<string> folders,
        Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        using var command = StoreSchema.Command(_connection, tx, RehashAcknowledgementAliasesSql);
        command.Parameters.AddWithValue("$r", root); command.Parameters.AddWithValue("$o", id);
        using var reader = command.ExecuteReader();
        var any = false;
        while (reader.Read())
        {
            any = true;
            var path = RowCodec.Join(reader.GetString(0), reader.GetString(1));
            if (!folders.Any(folder => folder.Length > 0 && PairPath.IsSelfOrUnder(path, folder))) return false;
        }
        return any;
    }
}

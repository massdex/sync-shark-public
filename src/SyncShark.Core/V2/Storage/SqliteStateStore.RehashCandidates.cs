namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    // Bound examined identities, not just matches: a sparse verification queue must also yield.

    public IReadOnlyList<ItemState> RehashCandidates(string rootId, int limit, long through, bool? needsVerification)
    {
        var result = new List<ItemState>();
        var after = (Order: long.MinValue, Id: string.Empty);
        var now = NowMs();
        while (result.Count < limit)
        {
            var pageSize = Math.Min(Math.Max(V2Settings.MinimumStoreRehashPageRows, _settings.StoreRehashPageRows), limit - result.Count);
            var page = InStore(() => ReadRehashCandidatePage(rootId, through, needsVerification, now, after, pageSize));
            if (page.Rows.Count == 0) break;
            result.AddRange(page.Items);
            after = (page.Rows[^1].Order, page.Rows[^1].Id);
        }
        return result.ToArray();
    }

    private (List<(string Id, long Order, bool Eligible)> Rows, List<ItemState> Items) ReadRehashCandidatePage(
        string rootId, long through, bool? needsVerification, long now, (long Order, string Id) after, int limit)
    {
        var rows = new List<(string Id, long Order, bool Eligible)>();
        var items = new List<ItemState>();
        var root = RootNo(rootId);
        if (root is null) return (rows, items);
        using var tx = _connection.BeginTransaction();
        using (var cmd = StoreSchema.Command(_connection, tx, RehashCandidatePageSql(needsVerification)))
        {
            cmd.Parameters.AddWithValue("$r", root.Value);
            cmd.Parameters.AddWithValue("$through", through);
            cmd.Parameters.AddWithValue("$after", after.Order);
            cmd.Parameters.AddWithValue("$id", after.Id);
            cmd.Parameters.AddWithValue("$limit", limit);
            if (needsVerification is true) cmd.Parameters.AddWithValue("$now", now);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetBoolean(2)));
        }
        foreach (var row in rows.Where(row => row.Eligible))
        {
            StoreSchema.Execute(_connection, tx,
                "UPDATE local_rehash SET last_attempt=$order WHERE root_no=$r AND object_id=$o;",
                ("$r", root.Value), ("$o", row.Id), ("$order", NextRehashOrder(root.Value, tx, through)));
            items.Add(_reads.ByObject(root.Value, row.Id, tx)!);
        }
        tx.Commit();
        return (rows, items);
    }

    internal static string RehashCandidatePageSql(bool? needsVerification) =>
        "SELECT q.object_id,q.last_attempt," +
        (needsVerification is true ? NotGapOwnedSql + " AND q.retry_after<=$now AND " : "") +
        "EXISTS(SELECT 1 FROM file_state f INDEXED BY fs_obj WHERE f.root_no=q.root_no AND f.object_id=q.object_id" +
        (needsVerification is true ? " AND " + VerificationNeededSql + " AND " + VerificationEligibleSql :
         needsVerification is false ? " AND NOT " + VerificationNeededSql : "") + ") " +
        "FROM local_rehash q INDEXED BY rehash_retry WHERE q.root_no=$r AND q.last_attempt<=$through " +
        "AND (q.last_attempt,q.object_id)>($after,$id) ORDER BY q.last_attempt,q.object_id LIMIT $limit;";
}

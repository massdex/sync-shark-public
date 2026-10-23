namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    string? IStateStore.ReportStorageNotice(string pairId, Notice notice, string familyPrefix) =>
        ReportStorageNotice(pairId, notice, familyPrefix);
    IReadOnlyList<StorageNotice> IStateStore.OpenStoragePairCauses(string pairId, string rootId, string remoteBinding) =>
        OpenStoragePairCauses(pairId, rootId, remoteBinding);

    /// <summary>Live pair-level causes of the two storage families, including an Ignore awaiting its episode's
    /// success. Callers evaluate every scope, so an unrelated success can never retire a binding/volume
    /// suppression. Only pair-level rows are read, never the per-item notice population.</summary>
    internal IReadOnlyList<StorageNotice> OpenStoragePairCauses(string pairId, string rootId, string remoteBinding) => InStore(() =>
    {
        var pair = RequirePair(pairId);
        var prefixes = new[]
        {
            StorageNotices.Prefix(new StorageNotice(rootId, "", remoteBinding, "availability")),
            StorageNotices.Prefix(new StorageNotice(rootId, "", remoteBinding, StorageNotices.HydrationPairOperation))
        };
        var result = new List<StorageNotice>(4);
        foreach (var prefix in prefixes)
        {
            // Each statement pins pair_no and its own reason range so the scan stays a true index bound even
            // when the pair has no pair-family row and the table holds a large item-notice population.
            using var command = StoreSchema.Command(_connection, null, $"""
                SELECT detail FROM notice INDEXED BY nt_pair_reason
                  WHERE pair_no=$p AND reason_key>=$lo AND reason_key<$hi AND {NoticeTable.ActionableSql("state")}
                  ORDER BY ({NoticeTable.IgnoredSql("choice")}), created;
                """);
            command.Parameters.AddWithValue("$p", pair);
            command.Parameters.AddWithValue("$lo", prefix);
            command.Parameters.AddWithValue("$hi", prefix + "￿");
            using var reader = command.ExecuteReader();
            while (reader.Read())
                if (NoticeDetailCodec.Decode(new Notice(pairId, 0, NoticeKind.Info, "", reader.GetString(0), "")).Storage is { } storage)
                    result.Add(storage);
        }
        return (IReadOnlyList<StorageNotice>)result;
    });

    /// <summary>Publishes through the shared notice owner; ignored causes retain suppression.</summary>
    internal string? ReportStorageNotice(string pairId, Notice notice, string familyPrefix) => InStore(() =>
    {
        var pair = RequirePair(pairId);
        using var tx = _connection.BeginTransaction();
        var id = ReportStorageNotice(tx, pair, notice, familyPrefix);
        tx.Commit();
        return id;
    });

    /// <summary>The transaction-scoped form used by aggregate writers, so one decision cannot race its own family.</summary>
    private string? ReportStorageNotice(Microsoft.Data.Sqlite.SqliteTransaction tx, long pair, Notice notice, string familyPrefix)
    {
        return _notices.AddStorage(tx, pair, notice, familyPrefix, NowMs());
    }
}

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    // v12 deletes resolved intents and has no general completed-transfer history. Never infer a write
    // from a baseline, generation, prepare, Recovery archive (the predecessor), or a pending intent.
    // The retained-version-restored notice is emitted in CommitResults only after a successful restore;
    // its Winner records the actual published identity/hash/mtime. Closed notices remain useful evidence.
    internal void PrepareDateWriteEvidence(string pairId) => InStore(() =>
    {
        StoreSchema.Execute(_connection, null,
            "CREATE TEMP TABLE IF NOT EXISTS date_write_evidence(side INTEGER,path TEXT,object_id TEXT,hash TEXT,size INTEGER,mtime INTEGER,written INTEGER," +
            "PRIMARY KEY(side,path,object_id,mtime)) WITHOUT ROWID; DELETE FROM date_write_evidence;");
        using var command = StoreSchema.Command(_connection, null,
            "SELECT path,detail,created FROM notice WHERE pair_no=$p AND reason_key='retained-version-restored';");
        command.Parameters.AddWithValue("$p", RequirePair(pairId));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var notice = NoticeDetailCodec.Decode(new Notice(pairId, 0, NoticeKind.Info, reader.GetString(0),
                reader.GetString(1), "retained-version-restored"));
            if (notice.RetainedVersion is not { Winner: { Kind: ItemKind.File, Hash: not null } winner } restored) continue;
            StoreSchema.Execute(_connection, null,
                "INSERT OR IGNORE INTO date_write_evidence VALUES($side,$path,$id,$hash,$size,$mtime,$written);",
                ("$side", (int)restored.Side), ("$path", RowCodec.Key(winner.Path)), ("$id", winner.ObjectId),
                ("$hash", winner.Hash), ("$size", winner.Size), ("$mtime", winner.Mtime.UtcTicks),
                ("$written", DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)).UtcTicks));
        }
    });

    internal string? DateWriteSkipReason(Side side, ItemState local, string? hash) => InStore(() =>
    {
        const string missing = "reliable Sync Shark local write history unavailable";
        if (StoreSchema.Scalar(_connection, null,
            "SELECT 1 FROM date_write_evidence WHERE side=$side AND path=$path AND object_id=$id LIMIT 1;",
            ("$side", (int)side), ("$path", RowCodec.Key(local.Path)), ("$id", local.ObjectId)) is null) return missing;
        return StoreSchema.Scalar(_connection, null,
            "SELECT 1 FROM date_write_evidence WHERE side=$side AND path=$path AND object_id=$id AND hash=$hash AND size=$size " +
            "AND abs(mtime-$mtime)<=$tolerance AND abs(written-$mtime)<=$tolerance LIMIT 1;",
            ("$side", (int)side), ("$path", RowCodec.Key(local.Path)), ("$id", local.ObjectId),
            ("$hash", (object?)hash ?? DBNull.Value), ("$size", local.Size), ("$mtime", local.Mtime.UtcTicks),
            ("$tolerance", TimeSpan.FromMinutes(5).Ticks)) is null ? "local date not written by Sync Shark" : null;
    });
}

using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    // These lookups never materialize all notices, pending intents or Recovery history.
    public (Notice Notice, bool Closed)? NoticeById(string pairId, string noticeId) => InStore(() =>
        PairNo(pairId) is { } pairNo ? _notices.ById(pairNo, noticeId) : null);

    public SyncAction? IntentById(string pairId, string actionId) => InStore(() =>
    {
        if (PairNo(pairId) is not { } pairNo) return null;
        var json = StoreSchema.Scalar(_connection, null,
            "SELECT action_json FROM pending_effect WHERE action_id=$id AND pair_no=$p;",
            ("$id", actionId), ("$p", pairNo)) as string;
        return json is null ? null : JsonSerializer.Deserialize<SyncAction>(json, RowCodec.Json);
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

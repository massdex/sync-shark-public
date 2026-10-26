using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    // Receipts come from the exact publication transaction, never a later/global watermark.
    // Do not expose receipts until WriteDelta has committed all its observation batches.
    internal IReadOnlyDictionary<string, long> WriteHashObservation(string pairId, Delta delta)
    {
        var consumer = InStore(() => PairNo(pairId));
        var versions = new Dictionary<string, long>(StringComparer.Ordinal);
        WriteDelta(delta, (tx, root, items) =>
            RetainPlanningObservations(tx, root, items, incremental: true, published: (transaction, pair, number, ids) =>
            {
                if (consumer != pair) return;
                using var command = StoreSchema.Command(_connection, transaction, """
                    SELECT d.object_id,d.version FROM json_each($ids) j CROSS JOIN planning_deferred d
                    WHERE d.pair_no=$p AND d.root_no=$r AND d.object_id=j.value;
                    """);
                command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids));
                command.Parameters.AddWithValue("$p", pair);
                command.Parameters.AddWithValue("$r", number);
                using var reader = command.ExecuteReader();
                while (reader.Read()) versions[reader.GetString(0)] = reader.GetInt64(1);
            }));
        return versions;
    }
}

using System.IO;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// Explicit predecessor input for UI projection/filter/paging tests. These rows have no run writer,
// command admission or effect authority. Current producer behavior is proved through the real Host
// and bounded-history writer tests; this helper does not masquerade as that producer.
internal static class LegacyHistoryFixture
{
    internal static Task SeedAsync(SyncAuthorityDatabase database, SyncActivityEvent item) =>
        SeedAsync(database, [item]);

    internal static async Task SeedAsync(SyncAuthorityDatabase database, IReadOnlyList<SyncActivityEvent> items)
    {
        await database.VerifyHealthAsync();
        await using var connection = new SqliteConnection("Data Source=" + database.DatabasePath + ";Pooling=False");
        await connection.OpenAsync();
        await SyncAuthorityDatabase.ConfigureAuthorityConnectionAsync(connection);
        await using var transaction = connection.BeginTransaction();
        foreach (var item in items)
        {
            var safe = SyncAuthorityDatabase.NormalizeActivityEvent(item);
            var payload = JsonSerializer.Serialize(safe);
            await using var existing = connection.CreateCommand();
            existing.Transaction = transaction;
            existing.CommandText = "SELECT detail_json FROM history_event WHERE history_event_id=$id;";
            existing.Parameters.AddWithValue("$id", safe.EventId);
            if (await existing.ExecuteScalarAsync() is string retained)
            {
                if (retained != payload) throw new InvalidDataException("A fixture identity has different predecessor bytes.");
                continue;
            }
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO history_event(rowid,history_event_id,event_kind,detail_json,occurred_utc,retention_class)
                VALUES((SELECT next_history_sequence FROM installation),$id,'presentation',$payload,$utc,'legacy');
                """;
            insert.Parameters.AddWithValue("$id", safe.EventId);
            insert.Parameters.AddWithValue("$payload", payload);
            insert.Parameters.AddWithValue("$utc", safe.OccurredUtc.ToString("O"));
            await insert.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }
}

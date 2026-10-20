using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>Owns the pending_effect table: recorded intents and their outcomes.</summary>
internal sealed class IntentTable
{
    private readonly SqliteConnection _connection;

    public IntentTable(SqliteConnection connection) => _connection = connection;

    /// <summary>Records an intent as started in the caller's transaction, rejecting an id owned by another pair.</summary>
    public void Record(SqliteTransaction transaction, long pairNo, SyncAction action, long now)
    {
        RequireOwner(transaction, action.ActionId, pairNo);
        StoreSchema.Execute(_connection, transaction,
            "INSERT INTO pending_effect(action_id, pair_no, action_json, state, attempts, updated) " +
            "VALUES($id, $p, $json, 'started', 0, $now) " +
            "ON CONFLICT(action_id) DO UPDATE SET state='started', action_json=excluded.action_json, updated=excluded.updated;",
            ("$id", action.ActionId), ("$p", pairNo), ("$json", JsonSerializer.Serialize(action, RowCodec.Json)),
            ("$now", now));
    }

    /// <summary>Deletes a resolved intent (Done, Skipped); keeps any other outcome and bumps its attempt count.</summary>
    /// <remarks>per-file history comes from the history rows, not from resolved intents.</remarks>
    public void Commit(SqliteTransaction transaction, long pairNo, SyncAction action, Outcome outcome, long now)
    {
        RequireOwner(transaction, action.ActionId, pairNo);
        if (outcome is Outcome.Done or Outcome.Skipped)
        {
            StoreSchema.Execute(_connection, transaction, "DELETE FROM pending_effect WHERE action_id=$id;",
                ("$id", action.ActionId));
            return;
        }
        var state = outcome.ToString().ToLowerInvariant();
        StoreSchema.Execute(_connection, transaction,
            "INSERT INTO pending_effect(action_id, pair_no, action_json, state, attempts, updated) " +
            "VALUES($id, $p, $json, $state, 1, $now) " +
            "ON CONFLICT(action_id) DO UPDATE SET state=excluded.state, action_json=excluded.action_json, " +
            "attempts=pending_effect.attempts + 1, updated=excluded.updated;",
            ("$id", action.ActionId), ("$p", pairNo), ("$json", JsonSerializer.Serialize(action, RowCodec.Json)),
            ("$state", state), ("$now", now));
    }

    /// <summary>The intents whose last outcome was started, uncertain or failed, ordered by update time.</summary>
    public IReadOnlyList<SyncAction> Unresolved(long pairNo)
    {
        using var command = StoreSchema.Command(_connection, null,
            "SELECT action_json FROM pending_effect WHERE pair_no=$p AND state IN ('started','uncertain','failed') " +
            "ORDER BY updated, action_id;");
        command.Parameters.AddWithValue("$p", pairNo);
        var intents = new List<SyncAction>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) intents.Add(JsonSerializer.Deserialize<SyncAction>(reader.GetString(0), RowCodec.Json)!);
        return intents;
    }

    /// <summary>How many intents are stored for any pair.</summary>
    public long Count() => (long)StoreSchema.Scalar(_connection, null, "SELECT COUNT(*) FROM pending_effect;")!;

    private void RequireOwner(SqliteTransaction? transaction, string actionId, long pairNo)
    {
        if (StoreSchema.Scalar(_connection, transaction, "SELECT pair_no FROM pending_effect WHERE action_id=$id;",
                ("$id", actionId)) is long other && other != pairNo)
            throw new InvalidOperationException($"Intent {actionId} belongs to another pair.");
    }
}

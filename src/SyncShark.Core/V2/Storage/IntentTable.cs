using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>Owns the pending_effect table: recorded intents and their outcomes.</summary>
internal sealed class IntentTable
{
    /// <summary>The states that are still in play; held is a temporary verification classification.</summary>
    public const string UnresolvedStateSql = "'started','uncertain','failed','held'";

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

    /// <summary>
    /// Temporarily holds one intent, or returns a held one to uncertain. No timestamp or attempt changes, and a
    /// missing id is never reinserted; an id owned by another pair is rejected.
    /// </summary>
    public void SetHeld(SqliteTransaction transaction, long pairNo, string actionId, bool held)
    {
        RequireOwner(transaction, actionId, pairNo);
        StoreSchema.Execute(_connection, transaction, held
                ? "UPDATE pending_effect SET state='held' WHERE pair_no=$p AND action_id=$id AND state<>'held';"
                : "UPDATE pending_effect SET state='uncertain' WHERE pair_no=$p AND action_id=$id AND state='held';",
            ("$p", pairNo), ("$id", actionId));
    }

    /// <summary>The intents whose last outcome was started, uncertain, failed or held, ordered by update time.</summary>
    public IReadOnlyList<SyncAction> Unresolved(long pairNo)
    {
        using var command = StoreSchema.Command(_connection, null,
            "SELECT action_json FROM pending_effect WHERE pair_no=$p AND state IN (" + UnresolvedStateSql + ") " +
            "ORDER BY updated, action_id;");
        command.Parameters.AddWithValue("$p", pairNo);
        var intents = new List<SyncAction>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) intents.Add(JsonSerializer.Deserialize<SyncAction>(reader.GetString(0), RowCodec.Json)!);
        return intents;
    }

    /// <summary>How many unresolved intents the pair has, counted through the (pair_no, state, updated) index.</summary>
    public long CountUnresolved(long pairNo) => (long)StoreSchema.Scalar(_connection, null,
        "SELECT COUNT(*) FROM pending_effect INDEXED BY pe_unresolved WHERE pair_no=$p AND state IN (" +
        UnresolvedStateSql + ");", ("$p", pairNo))!;

    /// <summary>How many of the pair's intents are temporarily held, counted through the unresolved index.</summary>
    public long CountHeld(long pairNo) => (long)StoreSchema.Scalar(_connection, null,
        "SELECT COUNT(*) FROM pending_effect INDEXED BY pe_unresolved WHERE pair_no=$p AND state='held';",
        ("$p", pairNo))!;

    /// <summary>Whether this pair's exact intent is held, through the action-id primary-key point lookup.</summary>
    public bool IsHeld(long pairNo, string actionId) => StoreSchema.Scalar(_connection, null,
        "SELECT 1 FROM pending_effect WHERE action_id=$id AND pair_no=$p AND state='held';",
        ("$id", actionId), ("$p", pairNo)) is not null;

    /// <summary>One bounded page of held intents in (updated, rowid) keyset order.</summary>
    public IntentPage HeldPage(long pairNo, long? afterUpdated, long? afterRowId, int limit) =>
        // The held summary only reads held rows: pe_unresolved pins pair+state, then updated,rowid is the index order.
        Page(pairNo, "pe_unresolved", "state='held'", afterUpdated, afterRowId, limit);

    /// <summary>The pair's largest existing rowid; a stream fixes this horizon once so later inserts stay out.</summary>
    public long UnresolvedHorizon(long pairNo) => (long)StoreSchema.Scalar(_connection, null,
        "SELECT COALESCE(MAX(rowid),0) FROM pending_effect INDEXED BY pe_intent_order WHERE pair_no=$p;",
        ("$p", pairNo))!;

    /// <summary>
    /// One bounded page of unresolved intents in rowid order within a captured horizon. Rowid never changes on
    /// re-record or state reclassification, so a mid-stream commit cannot move a row behind or ahead of the cursor.
    /// </summary>
    public IntentPage UnresolvedPage(long pairNo, long afterRowId, long throughRowId, int limit)
    {
        if (throughRowId <= afterRowId) return new IntentPage([], null);
        var sql = "SELECT action_json, rowid FROM pending_effect INDEXED BY pe_intent_order " +
            "WHERE pair_no=$p AND rowid>$after AND rowid<=$through AND state IN (" + UnresolvedStateSql + ") " +
            "ORDER BY rowid LIMIT $n;";
        using var command = StoreSchema.Command(_connection, null, sql);
        command.Parameters.AddWithValue("$p", pairNo);
        command.Parameters.AddWithValue("$after", afterRowId);
        command.Parameters.AddWithValue("$through", throughRowId);
        command.Parameters.AddWithValue("$n", limit + 1);
        var rows = new List<(SyncAction Action, long RowId)>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                rows.Add((JsonSerializer.Deserialize<SyncAction>(reader.GetString(0), RowCodec.Json)!,
                    reader.GetInt64(1)));
        var items = rows.Take(limit).Select(row => row.Action).ToArray();
        // The rowid-only cursor reuses the shared cursor shape; the updated component stays zero.
        var next = rows.Count > limit ? IntentPageCursor.Encode(0, rows[limit - 1].RowId) : null;
        return new IntentPage(items, next);
    }

    // pe_unresolved (pair_no, state, updated) serves the held page; its implicit rowid is the final key, so a
    // state change that leaves updated untouched cannot move a held row between pages.
    private IntentPage Page(long pairNo, string index, string statePredicate, long? afterUpdated, long? afterRowId,
        int limit)
    {
        var sql = "SELECT action_json, updated, rowid FROM pending_effect INDEXED BY " + index +
            " WHERE pair_no=$p AND " + statePredicate +
            (afterUpdated is null ? "" : " AND (updated, rowid) > ($u, $r)") +
            " ORDER BY updated, rowid LIMIT $n;";
        using var command = StoreSchema.Command(_connection, null, sql);
        command.Parameters.AddWithValue("$p", pairNo);
        if (afterUpdated is not null && afterRowId is not null)
        {
            command.Parameters.AddWithValue("$u", afterUpdated.Value);
            command.Parameters.AddWithValue("$r", afterRowId.Value);
        }
        command.Parameters.AddWithValue("$n", limit + 1);
        var rows = new List<(SyncAction Action, long Updated, long RowId)>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                rows.Add((JsonSerializer.Deserialize<SyncAction>(reader.GetString(0), RowCodec.Json)!,
                    reader.GetInt64(1), reader.GetInt64(2)));
        var items = rows.Take(limit).Select(row => row.Action).ToArray();
        var next = rows.Count > limit
            ? IntentPageCursor.Encode(rows[limit - 1].Updated, rows[limit - 1].RowId)
            : null;
        return new IntentPage(items, next);
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

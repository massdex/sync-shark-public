using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    public int SavePairIncluded(PairDef previous, PairDef included) => InIndexingStore(() =>
    {
        // Numeric identities are stable; warm them before opening the transaction (helpers can query).
        var pairNo = PairNo(previous.PairId) ?? throw new InvalidOperationException("The pair no longer exists.");
        var rootA = RootNo(previous.A.RootId)!.Value;
        var rootB = RootNo(previous.B.RootId)!.Value;
        using var transaction = _connection.BeginTransaction();
        var json = StoreSchema.Scalar(_connection, transaction,
            "SELECT def_json FROM pair WHERE pair_id=$id;", ("$id", previous.PairId)) as string;
        var current = json is null ? null : Deserialize(json);
        if (current is null || JsonSerializer.Serialize(current, RowCodec.Json) != JsonSerializer.Serialize(previous, RowCodec.Json))
            throw new InvalidOperationException("The pair changed. Retry including the folder; nothing was changed.");
        if (included.PairId != previous.PairId || included.A != previous.A || included.B != previous.B ||
            included.Excluded.Any(path => !previous.Excluded.Contains(path, StringComparer.OrdinalIgnoreCase)))
            throw new ArgumentException("Include must only remove existing exclusions.", nameof(included));
        var newlyIncluded = previous.Excluded.Where(path =>
            !included.Excluded.Any(retained => PairPath.IsSelfOrUnder(path, retained))).ToArray();
        var affectedIntents = 0;
        // started/attempts=0 is already write-ahead authority: a crash after submission can retain zero.
        // Every retained intent therefore remains for existing verify-only reconciliation, never blind replay.
        string? after = null;
        while (true)
        {
            var batch = new List<(string Id, string Json)>(256);
            using (var command = StoreSchema.Command(_connection, transaction,
                "SELECT action_id,action_json FROM pending_effect WHERE pair_no=$p " +
                "AND ($after IS NULL OR action_id>$after) ORDER BY action_id LIMIT 256;"))
            {
                command.Parameters.AddWithValue("$p", pairNo);
                command.Parameters.AddWithValue("$after", (object?)after ?? DBNull.Value);
                using var reader = command.ExecuteReader();
                while (reader.Read()) batch.Add((reader.GetString(0), reader.GetString(1)));
            }
            if (batch.Count == 0) break;
            after = batch[^1].Id;
            // Close the SELECT before modifying its table. Keyset paging retains bounded memory.
            foreach (var row in batch)
            {
                var action = JsonSerializer.Deserialize<SyncAction>(row.Json, RowCodec.Json)!;
                if (new[] { action.Path, action.FromPath, action.Source?.Path, action.ExpectedTarget?.Path }
                    .OfType<string>().Any(footprint => newlyIncluded.Any(path =>
                        PairPath.IsSelfOrUnder(footprint, path) || PairPath.IsSelfOrUnder(path, footprint))))
                {
                    affectedIntents++;
                    StoreSchema.Execute(_connection, transaction,
                        "UPDATE pending_effect SET action_json=$j WHERE pair_no=$p AND action_id=$id;",
                        ("$j", JsonSerializer.Serialize(action with { SuppressReconciledBaseline = true }, RowCodec.Json)),
                        ("$p", pairNo), ("$id", row.Id));
                }
            }
        }
        foreach (var path in newlyIncluded)
        {
            foreach (var side in new[] { Side.A, Side.B })
                _points.ForgetUnder(transaction, pairNo, side, path, rootA, rootB,
                    (root, objectId) => _points.HasBaseline(transaction, RootSides(root, transaction), objectId));
            // Evidence belongs to this pair/root/path. Never touch another pair's evidence or live inventory.
            var key = RowCodec.Key(path);
            StoreSchema.Execute(_connection, transaction,
                "DELETE FROM deletion_evidence WHERE pair_no=$p AND (root_no=$a OR root_no=$b) " +
                "AND (path_key=$k OR substr(path_key,1,length($k)+1)=$k||'/');",
                ("$p", pairNo), ("$a", rootA), ("$b", rootB), ("$k", key));
            // Re-inclusion starts matching afresh; retained prepare stamps must not revive an old deletion.
            StoreSchema.Execute(_connection, transaction,
                "DELETE FROM sync_retry WHERE pair_no=$p AND json_extract(json,'$.Held') IS NULL " +
                "AND (path_key=$k OR substr(path_key,1,length($k)+1)=$k||'/');",
                ("$p", pairNo), ("$k", key));
            // Keyset over affected retry keys; preserve stored side/rule, never old consent.
            string? afterKey = null;
            while (true)
            {
                var batch = new List<(string Key, SyncRetry Retry)>(256);
                using (var command = StoreSchema.Command(_connection, transaction,
                    "SELECT path_key,json FROM sync_retry WHERE pair_no=$p " +
                    "AND path_key >= $k AND path_key < $hi AND (path_key=$k OR path_key >= $lo) " +
                    "AND path_key" + (afterKey is null ? ">=" : ">") + "$after ORDER BY path_key LIMIT 256;"))
                {
                    command.Parameters.AddWithValue("$p", pairNo);
                    command.Parameters.AddWithValue("$k", key);
                    command.Parameters.AddWithValue("$lo", key.Length == 0 ? "" : key + "/");
                    command.Parameters.AddWithValue("$hi", key.Length == 0 ? "￿" : key + "0");
                    command.Parameters.AddWithValue("$after", afterKey ?? key);
                    using var reader = command.ExecuteReader();
                    while (reader.Read()) batch.Add((reader.GetString(0), JsonSerializer.Deserialize<SyncRetry>(reader.GetString(1), RowCodec.Json)!));
                }
                if (batch.Count == 0) break;
                afterKey = batch[^1].Key;
                foreach (var row in batch)
                    // N6: a decision row owns the user's open choice and proposal; re-inclusion must not
                    // overwrite it with generic restored-hold state.
                    if (row.Retry.Held is not null && row.Retry.Kind != "decision" &&
                        !included.Excluded.Any(retained => PairPath.IsSelfOrUnder(row.Retry.Path, retained)))
                        RestoreHold(new(row.Retry.Path, "deletion-evidence", 0, "", DateTimeOffset.MinValue,
                            Held: row.Retry.Held, ReprobeEvidence: true));
            }
            // Legacy notices are keyset-paged too, never a full UNION re-scan per page.
            if (RebuildableSelection.Sides(included) is { } sides)
            {
                string afterNotice = "";
                while (true)
                {
                    var batch = new List<(string Id, string Path)>(256);
                    using (var command = StoreSchema.Command(_connection, transaction,
                        "SELECT notice_id,path FROM notice WHERE notice_id>$after AND pair_no=$p " +
                        "AND state='closed' AND reason_key='deletion-evidence-pending' AND choice IN ('ignore','exclude') " +
                        "AND (path=$raw COLLATE NOCASE OR substr(path,1,length($raw)+1)=($raw||'/') COLLATE NOCASE) " +
                        "ORDER BY notice_id LIMIT 256;"))
                    {
                        command.Parameters.AddWithValue("$p", pairNo);
                        command.Parameters.AddWithValue("$raw", path);
                        command.Parameters.AddWithValue("$after", afterNotice);
                        using var reader = command.ExecuteReader();
                        while (reader.Read()) batch.Add((reader.GetString(0), reader.GetString(1)));
                    }
                    if (batch.Count == 0) break;
                    afterNotice = batch[^1].Id;
                    foreach (var row in batch)
                        if (!included.Excluded.Any(retained => PairPath.IsSelfOrUnder(row.Path, retained)) &&
                            StoreSchema.Scalar(_connection, transaction, "SELECT 1 FROM sync_retry WHERE pair_no=$p AND path_key=$k;",
                                ("$p", pairNo), ("$k", RowCodec.Key(row.Path))) is null)
                            RestoreHold(new(row.Path, "deletion-evidence", 0, "", DateTimeOffset.MinValue,
                                Held: new HeldDeletion(row.Path, sides.Local), ReprobeEvidence: true));
                }
            }
            void RestoreHold(SyncRetry retry) => StoreSchema.Execute(_connection, transaction,
                "INSERT INTO sync_retry(pair_no,path_key,json,retry_after) VALUES($p,$k,$j,$due) " +
                "ON CONFLICT(pair_no,path_key) DO UPDATE SET json=excluded.json,retry_after=excluded.retry_after;",
                ("$p", pairNo), ("$k", RowCodec.Key(retry.Path)),
                ("$j", JsonSerializer.Serialize(retry, RowCodec.Json)), ("$due", retry.RetryAfter.ToUnixTimeMilliseconds()));
            // An Ignore on this path was the request to stop syncing it. Removing the exclusion reverses that
            // request, so clear the dismissal: a fresh problem at the path (for example pending deletion evidence)
            // must be visible again instead of staying suppressed by the old ignore.
            // The pair-root quota and online-only summaries are never exclusion paths, so clearing their Ignore
            // would defeat a deliberate dismissal. A path that is still under another retained exclusion stays
            // excluded, so its Ignore is not reversed either.
            var clearIgnore = new StringBuilder(
                "UPDATE notice SET choice=NULL WHERE pair_no=$p AND ((state='closed' AND choice IN ($ignore,'exclude')) OR (" +
                NoticeTable.ActionableSql("state") + " AND " + NoticeTable.IgnoredSql("choice") + ")) " +
                "AND reason_key NOT IN ($quota,$stubs) " +
                "AND (path=$raw COLLATE NOCASE OR (substr(path,1,length($raw)+1)) = ($raw||'/') COLLATE NOCASE)");
            var clearParameters = new List<(string Name, object? Value)>
            {
                ("$p", pairNo), ("$raw", path), ("$ignore", NoticeChoices.Ignore),
                ("$quota", DropboxQuota.NoticeKey), ("$stubs", NoticeVocabulary.StubsLeftAlone)
            };
            for (var index = 0; index < included.Excluded.Count; index++)
            {
                var name = "$kept" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                clearIgnore.Append($" AND NOT (path={name} COLLATE NOCASE OR " +
                    $"substr(path,1,length({name})+1)=({name}||'/') COLLATE NOCASE)");
                clearParameters.Add((name, included.Excluded[index]));
            }
            StoreSchema.Execute(_connection, transaction, clearIgnore.ToString(), clearParameters.ToArray());
        }
        StoreSchema.Execute(_connection, transaction, "UPDATE pair SET def_json=" + PreservePlanningCursors("$j") + " WHERE pair_no=$p;",
            ("$j", JsonSerializer.Serialize(included, RowCodec.Json)), ("$p", pairNo));
        StoreSchema.Execute(_connection, transaction, "UPDATE root SET cursor=NULL WHERE root_no=$a OR root_no=$b;",
            ("$a", rootA), ("$b", rootB));
        transaction.Commit();
        _rootSides.Clear();
        return affectedIntents;
    });
}

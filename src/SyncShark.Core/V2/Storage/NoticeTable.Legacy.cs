using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal sealed partial class NoticeTable
{
    private const string AliasPrefix = "notice-alias:";

    /// <summary>Test-only: how many one-time legacy identity sweeps this store has run.</summary>
    internal int LegacyFoldPasses { get; private set; }

    // Legacy repair rows written before fa93b15f carry a path-bearing id. Once the item moves, the stored id no
    // longer matches the item's canonical identity. The refused-name fold therefore runs once per (pair, reason):
    // one pass decodes the open rows, rewrites those whose id differs, and records a durable marker. Later adds do
    // no scan, so the steady-state cost is O(1) rather than a JSON decode of every open same-reason notice.
    private void MigrateLegacyRepair(SqliteTransaction transaction, long pairNo, string canonicalId, Notice notice)
    {
        if (notice.Repair is not { } repair || notice.RetainedVersion is not null) return;
        // Only the pre-fa93b15f refused-name notices used the path-bearing legacy id; newer repair notices never did.
        if (notice.ReasonKey is "provider-name-refused" or "windows-name-invalid")
        {
            if (!LegacyFoldDone(transaction, pairNo, notice.ReasonKey))
            {
                FoldLegacyRepairRows(transaction, pairNo, notice.PairId, notice.Rule, notice.ReasonKey);
                MarkLegacyFoldDone(transaction, pairNo, notice.ReasonKey);
            }
            // The marker suppresses broad rescans, not the indexed exact legacy-id lookup.
            // A legacy writer can still publish this identity after the one-time sweep.

        }
        var legacyId = ShortId.Make("n", notice.PairId, notice.Rule.ToString(CultureInfo.InvariantCulture),
            RowCodec.Key(notice.Path), notice.ReasonKey, repair.Side.ToString(), repair.Item.ObjectId);
        var candidates = new List<(string Id, Notice Notice)>();
        var exact = ById(pairNo, legacyId, transaction);
        if (exact is { } exactRow && exactRow.Notice.NoticeId == legacyId) candidates.Add((legacyId, exactRow.Notice));
        if (candidates.Count == 0) return;
        FoldLegacyRepair(transaction, pairNo, canonicalId, notice, candidates);
    }

    // One pass over the open rows of a (pair, rule, reason): recompute each row's canonical identity from its own
    // stored repair evidence and fold the rows whose stored id differs. No row window, so a moved item is found
    // however many same-reason notices share the table. Plain-text or corrupt details decode with no Repair and
    // are skipped, never an error.
    private void FoldLegacyRepairRows(SqliteTransaction transaction, long pairNo, string pairId, int rule, string reason)
    {
        LegacyFoldPasses++;
        var stored = new List<(string Id, Notice Notice)>();
        using (var command = StoreSchema.Command(_connection, transaction,
            "SELECT notice_id,kind,path,detail FROM notice " +
            "WHERE pair_no=$p AND rule=$rule AND reason_key=$reason AND " + NoticeTable.ActionableSql("state") + " ORDER BY notice_id;"))
        {
            command.Parameters.AddWithValue("$p", pairNo);
            command.Parameters.AddWithValue("$rule", rule);
            command.Parameters.AddWithValue("$reason", reason);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var row = NoticeDetailCodec.Decode(new Notice(pairId, rule,
                    Enum.Parse<NoticeKind>(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reason, id));
                if (row.Repair is not null && row.RetainedVersion is null) stored.Add((id, row));
            }
        }
        foreach (var (id, row) in stored)
        {
            var canonical = CanonicalRepairId(pairId, rule, reason, row.Repair!);
            if (string.Equals(id, canonical, StringComparison.Ordinal)) continue;
            FoldLegacyRepair(transaction, pairNo, canonical, row, [(id, row)]);
        }
    }

    private static string CanonicalRepairId(string pairId, int rule, string reason, NoticeRepair repair) =>
        ShortId.Make("n", pairId, rule.ToString(CultureInfo.InvariantCulture), reason,
            repair.Side.ToString(), repair.Item.ObjectId);

    private void FoldLegacyRepair(SqliteTransaction transaction, long pairNo, string canonicalId, Notice notice,
        IReadOnlyList<(string Id, Notice Notice)> candidates)
    {
        foreach (var candidate in candidates)
            if (!IsSameProblem(transaction, candidate.Id, pairNo, notice))
                throw new InvalidDataException("Legacy notice identity collision.");
        var canonical = ById(pairNo, canonicalId, transaction);
        if (canonical is not null && !IsSameProblem(transaction, canonicalId, pairNo, notice))
            throw new InvalidDataException("Canonical notice identity collision.");
        // Preserve an already-written effect destination rather than losing its reconciliation obligation.
        var pending = candidates.Select(candidate => candidate.Notice.Repair)
            .Where(item => item?.Destination is not null).Select(item => item!).ToArray();
        var destinations = pending.Select(item => item.Destination!).Distinct(StringComparer.Ordinal).ToArray();
        if (destinations.Length > 1)
            throw new InvalidDataException("Two legacy repair destinations need reconciliation before notices can be combined.");
        if (pending.Length > 0 && canonical is not null)
        {
            var current = canonical.Value.Notice;
            if (current.Repair?.Destination is { } destination && destination != destinations[0])
                throw new InvalidDataException("Two legacy repair destinations need reconciliation before notices can be combined.");
        }
        var sourceId = pending.Length > 0
            ? candidates.First(candidate => string.Equals(candidate.Notice.Repair?.Destination, destinations[0], StringComparison.Ordinal)).Id
            : candidates[0].Id;
        StoreSchema.Execute(_connection, transaction,
            "INSERT INTO notice(notice_id,pair_no,rule,kind,path,path_key,detail,reason_key,state,choice,created,closed_at) " +
            "SELECT $new,pair_no,rule,kind,path,path_key,detail,reason_key,state,choice,created,closed_at FROM notice " +
            "WHERE notice_id=$old AND pair_no=$p ON CONFLICT(notice_id) DO NOTHING;",
            ("$new", canonicalId), ("$old", sourceId), ("$p", pairNo));
        // A freshly inserted row copies the legacy path and identity. The current observation owns the path,
        // while a destination already persisted on the legacy row must survive.
        if (StoreSchema.Changes(_connection, transaction) != 0 || pending.Length > 0)
            StoreSchema.Execute(_connection, transaction,
                "UPDATE notice SET path=$path,path_key=$path_key,kind=$kind,detail=$detail WHERE notice_id=$id AND pair_no=$p;",
                ("$path", notice.Path), ("$path_key", PathKey.Of(notice.Path)), ("$kind", notice.Kind.ToString()),
                ("$detail", NoticeDetailCodec.Encode(notice with
                {
                    Repair = pending.Length > 0 ? notice.Repair! with { Destination = destinations[0] } : notice.Repair
                })),
                ("$id", canonicalId), ("$p", pairNo));
        foreach (var candidate in candidates)
            CloseWhere(transaction, AliasPrefix + canonicalId, "notice_id=$old AND pair_no=$p;",
                ("$old", candidate.Id), ("$p", pairNo));
    }

    private bool LegacyFoldDone(SqliteTransaction transaction, long pairNo, string reason) =>
        StoreSchema.Scalar(_connection, transaction,
            "SELECT 1 FROM notice_legacy_fold WHERE pair_no=$p AND reason_key=$r;",
            ("$p", pairNo), ("$r", reason)) is not null;

    private void MarkLegacyFoldDone(SqliteTransaction transaction, long pairNo, string reason) =>
        StoreSchema.Execute(_connection, transaction,
            "INSERT INTO notice_legacy_fold(pair_no,reason_key) VALUES($p,$r) ON CONFLICT DO NOTHING;",
            ("$p", pairNo), ("$r", reason));

    private string ResolveLegacyAlias(long pairNo, string noticeId, SqliteTransaction? transaction)
    {
        using var command = StoreSchema.Command(_connection, transaction,
            "SELECT choice FROM notice WHERE pair_no=$p AND notice_id=$id AND state='closed';");
        command.Parameters.AddWithValue("$p", pairNo);
        command.Parameters.AddWithValue("$id", noticeId);
        // Exactly one hop: aliases cannot be chains or unbounded recursive reads.
        return command.ExecuteScalar() is string choice && choice.StartsWith(AliasPrefix, StringComparison.Ordinal)
            ? choice[AliasPrefix.Length..] : noticeId;
    }
}

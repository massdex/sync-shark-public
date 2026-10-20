using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal sealed partial class NoticeTable
{
    private const string AliasPrefix = "notice-alias:";
    // The identity probe reads open notices only, bounded: a notice table is item-sized, never inventory-sized.
    private const int IdentityProbeLimit = 64;

    // The exact primary key still wins. A legacy id embeds the path the item had when the notice was written, so an
    // item that has since moved is found by its stable object identity (side + object id, the stored object key)
    // instead. Every matching legacy row is folded into one canonical row, never duplicated.
    private void MigrateLegacyRepair(SqliteTransaction transaction, long pairNo, string canonicalId, Notice notice)
    {
        if (notice.Repair is not { } repair || notice.RetainedVersion is not null) return;
        var legacyId = ShortId.Make("n", notice.PairId, notice.Rule.ToString(CultureInfo.InvariantCulture),
            RowCodec.Key(notice.Path), notice.ReasonKey, repair.Side.ToString(), repair.Item.ObjectId);
        var candidates = new List<(string Id, Notice Notice)>();
        var exact = ById(pairNo, legacyId, transaction);
        if (exact is { } exactRow && exactRow.Notice.NoticeId == legacyId) candidates.Add((legacyId, exactRow.Notice));
        // Only the pre-fa93b15f refused-name notices used the path-bearing legacy id; newer repair notices never did.
        if (notice.ReasonKey is "provider-name-refused" or "windows-name-invalid")
            candidates.AddRange(FindLegacyByIdentity(transaction, pairNo, notice, repair, canonicalId, legacyId));
        if (candidates.Count == 0) return;
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
            "INSERT INTO notice(notice_id,pair_no,rule,kind,path,detail,reason_key,state,choice,created) " +
            "SELECT $new,pair_no,rule,kind,path,detail,reason_key,state,choice,created FROM notice " +
            "WHERE notice_id=$old AND pair_no=$p ON CONFLICT(notice_id) DO NOTHING;",
            ("$new", canonicalId), ("$old", sourceId), ("$p", pairNo));
        // A freshly inserted row copies the legacy path and identity. The current observation owns the path,
        // while a destination already persisted on the legacy row must survive.
        if (StoreSchema.Changes(_connection, transaction) != 0 || pending.Length > 0)
            StoreSchema.Execute(_connection, transaction,
                "UPDATE notice SET path=$path,kind=$kind,detail=$detail WHERE notice_id=$id AND pair_no=$p;",
                ("$path", notice.Path), ("$kind", notice.Kind.ToString()),
                ("$detail", NoticeDetailCodec.Encode(notice with
                {
                    Repair = pending.Length > 0 ? notice.Repair! with { Destination = destinations[0] } : notice.Repair
                })),
                ("$id", canonicalId), ("$p", pairNo));
        foreach (var candidate in candidates)
            StoreSchema.Execute(_connection, transaction,
                "UPDATE notice SET state='closed',choice=$alias WHERE notice_id=$old AND pair_no=$p;",
                ("$alias", AliasPrefix + canonicalId), ("$old", candidate.Id), ("$p", pairNo));
    }

    // Bounded probe of open notices for the same rule and reason whose stored repair carries this exact object
    // identity. The category-plus-prefix id never appears: only the stable (side, object id) does.
    private IReadOnlyList<(string Id, Notice Notice)> FindLegacyByIdentity(SqliteTransaction transaction, long pairNo,
        Notice notice, NoticeRepair repair, string canonicalId, string exactLegacyId)
    {
        var found = new List<(string Id, Notice Notice)>();
        using var command = StoreSchema.Command(_connection, transaction,
            "SELECT notice_id,rule,kind,path,detail,reason_key FROM notice " +
            "WHERE pair_no=$p AND rule=$rule AND reason_key=$reason AND state='open' " +
            "AND notice_id<>$canonical AND notice_id<>$exact ORDER BY notice_id LIMIT $limit;");
        command.Parameters.AddWithValue("$p", pairNo);
        command.Parameters.AddWithValue("$rule", notice.Rule);
        command.Parameters.AddWithValue("$reason", notice.ReasonKey);
        command.Parameters.AddWithValue("$canonical", canonicalId);
        command.Parameters.AddWithValue("$exact", exactLegacyId);
        command.Parameters.AddWithValue("$limit", IdentityProbeLimit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            var candidate = NoticeDetailCodec.Decode(new Notice(notice.PairId, (int)reader.GetInt64(1),
                Enum.Parse<NoticeKind>(reader.GetString(2)), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), id));
            if (candidate.Repair is { } stored && stored.Side == repair.Side &&
                string.Equals(stored.Item.ObjectId, repair.Item.ObjectId, StringComparison.Ordinal))
                found.Add((id, candidate));
        }
        return found;
    }

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

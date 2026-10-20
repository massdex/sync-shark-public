using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal sealed partial class NoticeTable
{
    private const string AliasPrefix = "notice-alias:";

    // Exact primary-key probes only; no schema change and no pair-sized compatibility scan.
    private void MigrateLegacyRepair(SqliteTransaction transaction, long pairNo, string canonicalId, Notice notice)
    {
        if (notice.Repair is not { } repair || notice.RetainedVersion is not null) return;
        var legacyId = ShortId.Make("n", notice.PairId, notice.Rule.ToString(CultureInfo.InvariantCulture),
            RowCodec.Key(notice.Path), notice.ReasonKey, repair.Side.ToString(), repair.Item.ObjectId);
        var legacy = ById(pairNo, legacyId, transaction);
        if (legacy is null || legacy.Value.Notice.NoticeId != legacyId) return;
        if (!IsSameProblem(transaction, legacyId, pairNo, notice))
            throw new InvalidDataException("Legacy notice identity collision.");
        var canonical = ById(pairNo, canonicalId, transaction);
        if (canonical is not null && !IsSameProblem(transaction, canonicalId, pairNo, notice))
            throw new InvalidDataException("Canonical notice identity collision.");
        // Preserve an already-written effect destination rather than losing its reconciliation obligation.
        if (legacy.Value.Notice.Repair is { Destination: not null } pending && canonical is not null)
        {
            var current = canonical.Value.Notice;
            if (current.Repair?.Destination is { } destination && destination != pending.Destination)
                throw new InvalidDataException("Two legacy repair destinations need reconciliation before notices can be combined.");
            StoreSchema.Execute(_connection, transaction, "UPDATE notice SET detail=$detail WHERE notice_id=$id;",
                ("$detail", NoticeDetailCodec.Encode(current with { Repair = pending })), ("$id", canonicalId));
        }
        StoreSchema.Execute(_connection, transaction,
            "INSERT INTO notice(notice_id,pair_no,rule,kind,path,detail,reason_key,state,choice,created) " +
            "SELECT $new,pair_no,rule,kind,path,detail,reason_key,state,choice,created FROM notice " +
            "WHERE notice_id=$old AND pair_no=$p ON CONFLICT(notice_id) DO NOTHING;",
            ("$new", canonicalId), ("$old", legacyId), ("$p", pairNo));
        StoreSchema.Execute(_connection, transaction,
            "UPDATE notice SET state='closed',choice=$alias WHERE notice_id=$old AND pair_no=$p;",
            ("$alias", AliasPrefix + canonicalId), ("$old", legacyId), ("$p", pairNo));
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

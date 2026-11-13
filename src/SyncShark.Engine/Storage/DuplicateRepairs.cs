using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Owns duplicate-folder obligations and their transactional validity.</summary>
internal static class DuplicateRepairs
{
    internal static void UpgradeExisting(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now)
    {
        using (var backfill = StorageCommand.Create(connection, transaction, StateSchema.BackfillVacateAuthority))
            backfill.ExecuteNonQuery();
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            long afterPair = 0;
            object after = 0L;
            while (true)
            {
                using (var command = StorageCommand.Create(connection, transaction,
                    side == Side.Local ? StateSchema.DuplicateLocalIds : StateSchema.DuplicateDropboxIds,
                    (StateSchema.ParamAfterPair, afterPair), (StateSchema.ParamAfter, after)))
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) break;
                    afterPair = reader.GetInt64(0);
                    after = reader.GetValue(1);
                }
                var bindings = Bindings(connection, transaction, afterPair, side, after, foldersOnly: true, take: 3);
                if (bindings.Count < 2) continue;
                RecordGroup(connection, transaction, afterPair, side, after, bindings, now);
            }
        }
    }

    private static IReadOnlyList<SyncedEntry> Bindings(SqliteConnection connection, SqliteTransaction transaction,
        long pair, Side side, object identity, bool foldersOnly, int take)
    {
        using var command = StorageCommand.Create(connection, transaction, StateSchema.SyncedBindingsByIdentity(side, foldersOnly),
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamId, identity), (StateSchema.ParamTake, take));
        using var reader = command.ExecuteReader();
        var rows = new List<SyncedEntry>();
        while (reader.Read()) rows.Add(StateEncoding.ReadSynced(reader, reader.GetString(0), 1));
        return rows.OrderBy(row => row.LocalKey, StringComparer.Ordinal).ToArray();
    }

    private static void RecordGroup(SqliteConnection connection, SqliteTransaction transaction, long pair,
        Side side, object identity, IReadOnlyList<SyncedEntry> bindings, DateTimeOffset now)
    {
        var other = PathNames.Other(side);
        var qualifies = bindings.Count == 2 && bindings[0].IdOn(other) != bindings[1].IdOn(other) &&
            bindings.All(binding => Bindings(connection, transaction, pair, other,
                StateEncoding.IdFor(other, binding.IdOn(other))!, foldersOnly: false, take: 2).Count == 1 &&
                !HasCustody(connection, transaction, pair, binding, null));
        using var pairQuery = StorageCommand.Create(connection, transaction, StateSchema.PairIdByRow,
            (StateSchema.ParamPairRow, pair));
        var pairId = (string)pairQuery.ExecuteScalar()!;
        if (!qualifies)
        {
            IssueTable.Raise(connection, transaction, pairId, IssueKind.Failure, TextKey.FailDuplicateFolderLinks,
                bindings[0].PathOn(side), LinksSubject(side, bindings), now);
            return;
        }
        using (var insert = StorageCommand.Create(connection, transaction, StateSchema.InsertDuplicateRepair,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamSide, (int)side), (StateSchema.ParamIdentity, identity),
            ("$first", StateEncoding.IdFor(other, bindings[0].IdOn(other))),
            ("$second", StateEncoding.IdFor(other, bindings[1].IdOn(other))))) insert.ExecuteNonQuery();
        ReleaseClashes(connection, transaction, pairId, side, bindings);
    }

    private static IssueSubject LinksSubject(Side side, IReadOnlyList<SyncedEntry> bindings)
    {
        var first = bindings[0];
        var other = PathNames.Other(side);
        return new IssueSubject([new IssueItem(side, first.PathOn(side), first.IdOn(side)),
            .. bindings.Take(2).Select(binding => new IssueItem(other, binding.PathOn(other), binding.IdOn(other)))], null);
    }

    private static void ReleaseClashes(SqliteConnection connection, SqliteTransaction transaction, string pairId,
        Side side, IReadOnlyList<SyncedEntry> bindings)
    {
        var subject = LinksSubject(side, bindings);
        foreach (var path in bindings.SelectMany(binding => new[] { binding.PathOn(side), binding.PathOn(PathNames.Other(side)) })
            .Distinct(PathNames.Comparer))
        {
            var issue = IssueTable.AtPath(connection, transaction, pairId, path, IssueKind.NameClash);
            if (issue is null || !subject.Items.All(expected => issue.Subject.Items.Any(item =>
                item.Side == expected.Side && item.Id == expected.Id))) continue;
            ChangeTable.ReleaseClashContinuations(connection, transaction, pairId, issue);
            IssueTable.CloseExact(connection, transaction, pairId, issue);
        }
    }

    /// <summary>The Release stage (-6 3.5.4): the clash's rename continuations, then any obligation this issue holds,
    /// so the Close that follows deletes it.</summary>
    internal static void Release(SqliteConnection connection, SqliteTransaction transaction, string pairId, Issue issue)
    {
        ChangeTable.ReleaseClashContinuations(connection, transaction, pairId, issue);
        using var command = StorageCommand.Create(connection, transaction, StateSchema.ReleaseRepairHold,
            (StateSchema.ParamPair, pairId), (StateSchema.ParamIssue, issue.Id));
        command.ExecuteNonQuery();
    }

    internal static bool HasCustody(SqliteConnection connection, SqliteTransaction transaction, long pair,
        SyncedEntry binding, string? operation)
    {
        using var command = StorageCommand.Create(connection, transaction, StateSchema.DuplicateRepairCustody,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamLocalKey, binding.LocalKey),
            (StateSchema.ParamDropboxKey, binding.DropboxKey), (StateSchema.ParamIsFolder, binding.IsFolder),
            (StateSchema.ParamOperation, operation));
        return (long)command.ExecuteScalar()! != 0;
    }
}

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
                    afterPair = reader.GetInt64(StateSchema.DuplicateIdPair);
                    after = reader.GetValue(StateSchema.DuplicateIdValue);
                }
                var bindings = Bindings(connection, transaction, afterPair, side, after, foldersOnly: true, take: StateSchema.DuplicateRepairProbe);
                if (bindings.Count < StateSchema.DuplicateRepairPartners) continue;
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
        while (reader.Read()) rows.Add(StateEncoding.ReadSynced(reader, reader.GetString(StateSchema.SyncedLocalKey), StateSchema.SyncedLocalPath));
        return rows.OrderBy(row => row.LocalKey, PathLocks.KeyComparer).ToArray();
    }

    private static void RecordGroup(SqliteConnection connection, SqliteTransaction transaction, long pair,
        Side side, object identity, IReadOnlyList<SyncedEntry> bindings, DateTimeOffset now)
    {
        var other = PathNames.Other(side);
        var qualifies = bindings.Count == StateSchema.DuplicateRepairPartners && bindings[0].IdOn(other) != bindings[1].IdOn(other) &&
            Validity(connection, transaction, pair, side, bindings, null) == DuplicateRepairProof.Valid;
        using var pairQuery = StorageCommand.Create(connection, transaction, StateSchema.PairIdByRow,
            (StateSchema.ParamPairRow, pair));
        var pairId = (string)pairQuery.ExecuteScalar()!;
        if (!qualifies)
        {
            LinksNotice(connection, transaction, pairId, side, bindings, now, null);
            return;
        }
        using (var insert = StorageCommand.Create(connection, transaction, StateSchema.InsertDuplicateRepair,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamSide, (int)side), (StateSchema.ParamIdentity, identity),
            (StateSchema.ParamFirst, StateEncoding.IdFor(other, bindings[0].IdOn(other))),
            (StateSchema.ParamSecond, StateEncoding.IdFor(other, bindings[1].IdOn(other))))) insert.ExecuteNonQuery();
        ReleaseClashes(connection, transaction, pairId, side, bindings);
    }

    /// <summary>The one FailDuplicateFolderLinks notice for a group that has no obligation (L-5, G1).</summary>
    private static void LinksNotice(SqliteConnection connection, SqliteTransaction transaction, string pairId, Side side,
        IReadOnlyList<SyncedEntry> bindings, DateTimeOffset now, ICollection<Issue>? raised) =>
        IssueTable.Raise(connection, transaction, pairId, IssueKind.Failure, TextKey.FailDuplicateFolderLinks,
            bindings[0].PathOn(side), IssueSubject.DuplicateFolderItems(side, bindings), now, raised);

    private static void ReleaseClashes(SqliteConnection connection, SqliteTransaction transaction, string pairId,
        Side side, IReadOnlyList<SyncedEntry> bindings)
    {
        var subject = IssueSubject.DuplicateFolderItems(side, bindings);
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

    /// <summary>The pre-forget invariants of each settle shape, checked once before any commit stage writes
    /// (-7 2): the operation path refuses as folder state, the state-only and obsolete settles as changed underneath.</summary>
    internal static void RequirePreForget(SqliteConnection connection, SqliteTransaction transaction, string pairId,
        (Side Side, string Identity, SyncedEntry? Victim, string? OperationId, (Side Side, string Path, string Id)? Handoff) settle)
    {
        var pair = IssueTable.PairFor(connection, transaction, pairId);
        var identity = StateEncoding.IdFor(settle.Side, settle.Identity)!;
        if (settle.Victim is not { } victim)
        {
            // R0 runs as one statement here, as the commit's other unbounded in-transaction reads do.
            if (Row(connection, transaction, pair, settle.Side, identity) is not { } row ||
                Classify(connection, transaction, pairId, pair, settle.Side, identity, row,
                    new(() => OwnOperations(connection, transaction, pair, long.MaxValue)), null) is not
                    { State: DuplicateRepairState.Obsolete })
                throw new ChangedUnderneathException(string.Empty);
            return;
        }
        var exact = BindingAt(connection, transaction, pairId, victim.LocalKey) == victim &&
            !HasSavedDescendants(connection, transaction, pair, victim);
        if (settle.OperationId is not null)
        {
            if (!exact || Row(connection, transaction, pair, settle.Side, identity) is null)
                throw new FolderStateRefusedException();
            return;
        }
        if (!exact || Validate(connection, transaction, pair, settle.Side, identity, null) != DuplicateRepairProof.Valid)
            throw new ChangedUnderneathException(victim.PathOn(settle.Side));
    }

    /// <summary>The SettleRepair stage (-6 3.5.5, -7 2): RequirePreForget proved the facts, so it only writes. The row
    /// goes first, so the recorded Failure is no longer protected when it closes.</summary>
    internal static void Settle(SqliteConnection connection, SqliteTransaction transaction, ChangeTable changes,
        string pairId, (Side Side, string Identity, SyncedEntry? Victim, string? OperationId,
            (Side Side, string Path, string Id)? Handoff) settle, DateTimeOffset now, ICollection<Issue>? raised)
    {
        var pair = IssueTable.PairFor(connection, transaction, pairId);
        var identity = StateEncoding.IdFor(settle.Side, settle.Identity)!;
        var row = Row(connection, transaction, pair, settle.Side, identity)!.Value;
        Execute(connection, transaction, StateSchema.DeleteDuplicateRepair, pair, settle.Side, identity);
        IReadOnlyList<SyncedEntry> current = [];
        if (settle.Victim is { } victim) ReleaseClashes(connection, transaction, pairId, settle.Side, [victim]);
        else
        {
            var other = PathNames.Other(settle.Side);
            current = Bindings(connection, transaction, pair, settle.Side, identity, foldersOnly: true, take: StateSchema.DuplicateRepairProbe);
            foreach (var binding in current.Where(binding => StateEncoding.IdFor(other, binding.IdOn(other)) is { } id &&
                (id.Equals(row.First) || id.Equals(row.Second))))
                ReleaseClashes(connection, transaction, pairId, settle.Side, [binding]);
        }
        if (row.Issue is { } recorded && IssueTable.ById(connection, pairId, recorded, transaction) is
            { Cause: TextKey.FailDuplicateFolderBinding } failure)
            IssueTable.CloseExact(connection, transaction, pairId, failure);
        if (settle.Handoff is { } handoff)
            changes.RegisterReleasedEndpoint(transaction, pairId, handoff.Side, handoff.Path, handoff.Id);
        if (current.Count >= StateSchema.DuplicateRepairPartners) LinksNotice(connection, transaction, pairId, settle.Side, current, now, raised);
    }

    /// <summary>The HoldRepair stage, after Raise (-6 3.5.4): binds the obligation to the Failure now at path, a CAS on
    /// the issue it recorded; a miss rolls the hold commit back.</summary>
    internal static void Hold(SqliteConnection connection, SqliteTransaction transaction, string pairId,
        (Side Side, string Identity, long? ExpectedIssue) hold, string path)
    {
        var pair = IssueTable.PairFor(connection, transaction, pairId);
        if (IssueTable.AtPath(connection, transaction, pairId, path, IssueKind.Failure) is not { } issue)
            throw new ChangedUnderneathException(path);
        using var command = StorageCommand.Create(connection, transaction, StateSchema.HoldDuplicateRepair,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamSide, (int)hold.Side),
            (StateSchema.ParamIdentity, StateEncoding.IdFor(hold.Side, hold.Identity)),
            (StateSchema.ParamIssue, issue.Id), (StateSchema.ParamExpected, hold.ExpectedIssue));
        if (command.ExecuteNonQuery() == 0) throw new ChangedUnderneathException(path);
    }

    /// <summary>V1-V4 (-6 3.5.2): the row exists and X's folder bindings are exactly the recorded two (else Changed),
    /// then <see cref="Validity"/> over those two.</summary>
    internal static DuplicateRepairProof Validate(SqliteConnection connection, SqliteTransaction transaction, long pair,
        Side side, object identity, string? operation) =>
        Row(connection, transaction, pair, side, identity) is { } row &&
        Partners(connection, transaction, pair, side, identity, row) is { } bindings
            ? Validity(connection, transaction, pair, side, bindings, operation)
            : DuplicateRepairProof.Changed;

    // V4 then V3, the classifier's order (rows 6 and 7): no unresolved custody but operation's own overlaps a binding,
    // then each other-side id is bound once.
    private static DuplicateRepairProof Validity(SqliteConnection connection, SqliteTransaction transaction, long pair,
        Side side, IReadOnlyList<SyncedEntry> bindings, string? operation)
    {
        if (bindings.Any(binding => HasCustody(connection, transaction, pair, binding, operation)))
            return DuplicateRepairProof.Busy;
        return bindings.All(binding => BoundOnce(connection, transaction, pair, side, binding))
            ? DuplicateRepairProof.Valid : DuplicateRepairProof.LinkedElsewhere;
    }

    /// <summary>One binding's Repair proofs (-7 1.4): V1-V4, excluding the operation's own custody when given, then P2.</summary>
    internal static DuplicateRepairProof Check(SqliteConnection connection, SqliteTransaction transaction, string pairId,
        DuplicateRepairKey key, SyncedEntry binding, string? operation)
    {
        var pair = IssueTable.PairFor(connection, transaction, pairId);
        var proof = Validate(connection, transaction, pair, key.Side, StateEncoding.IdFor(key.Side, key.Identity)!, operation);
        return proof == DuplicateRepairProof.Valid && HasSavedDescendants(connection, transaction, pair, binding)
            ? DuplicateRepairProof.History : proof;
    }

    /// <summary>One classified page (-6 3.5.2): the keyset read, R0 at most once, then each obligation's checks. X's
    /// bindings are its two current ones in key order, empty when V2 fails; Own is row 3's operation.</summary>
    internal static IReadOnlyList<(DuplicateRepair Obligation, DuplicateRepairState State,
        IReadOnlyList<SyncedEntry> Bindings, FolderRemovalState? Own)> Page(SqliteConnection connection,
        SqliteTransaction transaction, string pairId, PathScope scope, DuplicateRepairKey? after, int take)
    {
        var pair = IssueTable.PairFor(connection, transaction, pairId);
        var rows = new List<(DuplicateRepairKey Key, (object First, object Second, long? Issue) Row)>();
        using (var command = StorageCommand.Create(connection, transaction, StateSchema.DuplicateRepairPage,
            (StateSchema.ParamPairRow, pair),
            (StateSchema.ParamSide, after is { } start ? (int)start.Side : StateSchema.BeforeFirstRepairSide),
            (StateSchema.ParamIdentity, after is { } key ? StateEncoding.IdFor(key.Side, key.Identity) : null),
            (StateSchema.ParamTake, take)))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                rows.Add((new DuplicateRepairKey((Side)reader.GetInt32(StateSchema.RepairSide),
                    reader.GetString(StateSchema.RepairIdentity)), ReadRow(reader)));
        var own = new Lazy<Dictionary<(Side Side, object? Identity, object? Other), FolderRemovalState>>(() =>
            OwnOperations(connection, transaction, pair, take));
        return rows.Select(row =>
        {
            var (state, bindings, found) = Classify(connection, transaction, pairId, pair, row.Key.Side,
                StateEncoding.IdFor(row.Key.Side, row.Key.Identity)!, row.Row, own, scope)!.Value;
            return (new DuplicateRepair(row.Key, row.Row.Issue), state, bindings, found);
        }).ToArray();
    }

    /// <summary>P2: a saved descendant under the binding's Local, explicit Dropbox or inherited Dropbox key.</summary>
    internal static bool HasSavedDescendants(SqliteConnection connection, SqliteTransaction transaction, long pair,
        SyncedEntry binding)
    {
        var local = PathNames.DescendantBounds(binding.LocalKey);
        var dropbox = PathNames.DescendantBounds(binding.DropboxKey);
        return Any(StateSchema.SelectSyncedUnderLocalKeyPaged, local) ||
            Any(StateSchema.SelectSyncedUnderInheritedKeyPaged, dropbox) ||
            Any(StateSchema.SelectSyncedUnderDropboxKeyPaged, dropbox);

        bool Any(string sql, (string Low, string High) bounds)
        {
            using var command = StorageCommand.Create(connection, transaction, sql, (StateSchema.ParamPairRow, pair),
                (StateSchema.ParamAfter, bounds.Low), (StateSchema.ParamHigh, bounds.High),
                (StateSchema.ParamTake, StateSchema.OneRow));
            using var reader = command.ExecuteReader();
            return reader.Read();
        }
    }

    /// <summary>R0: the pair's unresolved Repair operations by (shared side, X, other-side id), authority compared
    /// before the row is decoded (-7 5.1). A Repair's shared side is the one it does not delete on.</summary>
    internal static Dictionary<(Side Side, object? Identity, object? Other), FolderRemovalState> OwnOperations(
        SqliteConnection connection, SqliteTransaction transaction, long pair, long take)
    {
        var own = new Dictionary<(Side, object?, object?), FolderRemovalState>();
        for (var after = string.Empty; ;)
        {
            using var command = StorageCommand.Create(connection, transaction, StateSchema.UnresolvedFolders,
                (StateSchema.ParamPairRow, pair), (StateSchema.ParamAfterOperation, after), (StateSchema.ParamTake, take));
            using var reader = command.ExecuteReader();
            long read = 0;
            while (reader.Read())
            {
                read++;
                after = reader.GetString(StateSchema.FolderOperationId);
                if (StateEncoding.ReadEnum<FolderRemovalAuthority>(reader, StateSchema.FolderAuthority) !=
                    FolderRemovalAuthority.Repair) continue;
                var operation = StateEncoding.ReadFolderRemoval(reader, null);
                if (operation.SavedRoot is not { } root) continue;
                var shared = PathNames.Other(operation.DeletingSide);
                own[(shared, StateEncoding.IdFor(shared, root.IdOn(shared)),
                    StateEncoding.IdFor(operation.DeletingSide, root.IdOn(operation.DeletingSide)))] = operation;
            }
            if (read < take) return own;
        }
    }

    // Classify (-6 3.5.2): the first check that decides. Rows 1-4 alone decide Obsolete, so the obsolete settle's
    // in-commit re-check passes no scope and stops there (null); R0 is read at most once per page or commit.
    private static (DuplicateRepairState State, IReadOnlyList<SyncedEntry> Bindings, FolderRemovalState? Own)? Classify(
        SqliteConnection connection, SqliteTransaction transaction, string pairId, long pair, Side side, object identity,
        (object First, object Second, long? Issue) row,
        Lazy<Dictionary<(Side Side, object? Identity, object? Other), FolderRemovalState>> own, PathScope? scope)
    {
        IReadOnlyList<SyncedEntry> none = [];
        var bindings = Partners(connection, transaction, pair, side, identity, row);
        if (row.Issue is { } issue && IssueTable.ById(connection, pairId, issue, transaction) is not null)
            return (bindings is null ? DuplicateRepairState.Obsolete : DuplicateRepairState.Held, bindings ?? none, null);
        if (own.Value.TryGetValue((side, identity, row.First), out var found) ||
            own.Value.TryGetValue((side, identity, row.Second), out found))
            return (DuplicateRepairState.Own, none, found);
        if (bindings is null) return (DuplicateRepairState.Obsolete, none, null);
        if (scope is null) return null;
        if (bindings.All(scope.ExcludesBinding)) return (DuplicateRepairState.Excluded, bindings, null);
        return (Validity(connection, transaction, pair, side, bindings, null) switch
        {
            DuplicateRepairProof.Busy => DuplicateRepairState.Waiting,
            DuplicateRepairProof.LinkedElsewhere => DuplicateRepairState.LinkedElsewhere,
            _ => DuplicateRepairState.Valid
        }, bindings, null);
    }

    // V1, the obligation's primary-key probe.
    private static (object First, object Second, long? Issue)? Row(SqliteConnection connection,
        SqliteTransaction transaction, long pair, Side side, object identity)
    {
        using var command = StorageCommand.Create(connection, transaction, StateSchema.SelectDuplicateRepair,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamSide, (int)side), (StateSchema.ParamIdentity, identity));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }

    private static (object First, object Second, long? Issue) ReadRow(SqliteDataReader reader) =>
        (reader.GetValue(StateSchema.RepairFirst), reader.GetValue(StateSchema.RepairSecond),
            reader.IsDBNull(StateSchema.RepairIssue) ? null : reader.GetInt64(StateSchema.RepairIssue));

    // V2: X's current folder bindings (LIMIT 3) are exactly two, with the recorded other-side ids.
    private static IReadOnlyList<SyncedEntry>? Partners(SqliteConnection connection, SqliteTransaction transaction,
        long pair, Side side, object identity, (object First, object Second, long? Issue) row)
    {
        var other = PathNames.Other(side);
        var bindings = Bindings(connection, transaction, pair, side, identity, foldersOnly: true, take: StateSchema.DuplicateRepairProbe);
        var ids = bindings.Select(binding => StateEncoding.IdFor(other, binding.IdOn(other))).ToArray();
        return bindings.Count == StateSchema.DuplicateRepairPartners && ids.Contains(row.First) && ids.Contains(row.Second) ? bindings : null;
    }

    // V3 for one binding: its other-side id is bound exactly once.
    private static bool BoundOnce(SqliteConnection connection, SqliteTransaction transaction, long pair, Side side,
        SyncedEntry binding)
    {
        var other = PathNames.Other(side);
        return Bindings(connection, transaction, pair, other, StateEncoding.IdFor(other, binding.IdOn(other))!,
            foldersOnly: false, take: StateSchema.OneRow + 1).Count == 1;
    }

    private static SyncedEntry? BindingAt(SqliteConnection connection, SqliteTransaction transaction, string pairId,
        string localKey)
    {
        using var command = StorageCommand.Create(connection, transaction, StateSchema.SelectSyncedByLocalKey,
            (StateSchema.ParamPair, pairId), (StateSchema.ParamTargetKey, localKey));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? StateEncoding.ReadSynced(reader, reader.GetString(StateSchema.SyncedLocalKey), StateSchema.SyncedLocalPath)
            : null;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, long pair,
        Side side, object identity)
    {
        using var command = StorageCommand.Create(connection, transaction, sql,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamSide, (int)side), (StateSchema.ParamIdentity, identity));
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

namespace Massdex.SyncShark.Engine.Storage;

public sealed partial class StateStore
{
    /// <inheritdoc />
    public void SetFolderMove(string pairId, long expectedGeneration, FolderMove? expected, FolderMove? next) => Write(() =>
    {
        var row = next ?? expected ?? throw new ArgumentException();
        if (expected is not null && next is not null &&
            (next.SourceId != expected.SourceId || next.RootId != expected.RootId || next.ParentId != expected.ParentId ||
             next.SourcePath != expected.SourcePath || next.DestinationPath != expected.DestinationPath ||
             next.Generation != checked(expected.Generation + 1))) throw new ArgumentException();
        using var transaction = _connection.BeginTransaction();
        var pair = RequireGeneration(transaction, pairId, expectedGeneration);
        var changed = Execute(_connection, transaction,
            expected is null ? StateSchema.FolderMoveInsert : next is null ? StateSchema.FolderMoveDelete : StateSchema.FolderMoveUpdate,
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamId, row.SourceId),
            (StateSchema.ParamMoveRoot, row.RootId), (StateSchema.ParamMoveParent, row.ParentId),
            (StateSchema.ParamMoveSourcePath, row.SourcePath), (StateSchema.ParamMoveDestinationPath, row.DestinationPath),
            (StateSchema.ParamMoveDestinationId, row.DestinationId), (StateSchema.ParamPhase, row.Phase.ToString()),
            (StateSchema.ParamMoveLaunch, StateEncoding.EncodeMoveLaunch(row.Launch)), (StateSchema.ParamJob, row.JobId),
            (StateSchema.ParamGeneration, row.Generation), (StateSchema.ParamExpected, expected?.Generation));
        if (changed != StateSchema.OneRow) throw new ChangedUnderneathException(row.SourcePath);
        transaction.Commit();
        if (expected is null && row.ParentId is null) _changes.RequestRepeat(pairId, expectedGeneration);
    });

    /// <inheritdoc />
    public IReadOnlyList<FolderMove> FolderMoves(string pairId, string rootId)
    {
        lock (_lock) return _legacy ? [] : ReadPage(null, StateSchema.FolderMovesRead, StateEncoding.ReadFolderMove,
            (StateSchema.ParamPair, pairId), (StateSchema.ParamMoveRoot, rootId));
    }

    /// <inheritdoc />
    public IEnumerable<FolderMove> FolderMoveRoots(string pairId, CancellationToken ct)
    {
        if (_legacy) yield break;
        var after = string.Empty;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            FolderMove? row;
            lock (_lock) row = ReadOne(null, StateSchema.FolderMoveRootRead, StateEncoding.ReadFolderMove,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamAfter, after));
            if (row is null) yield break;
            after = row.SourceId;
            yield return row;
        }
    }

    /// <inheritdoc />
    public Issue? MoveResponsibility(string pairId, string dropboxId)
    {
        lock (_lock) return _issueFacts.Read(pairId).HeldIdentity(dropboxId);
    }

    /// <inheritdoc />
    public void HoldFolderMoves(string pairId) => Write(() =>
    {
        if (_legacy) return;
        var facts = _issueFacts.Read(pairId);
        using var transaction = _connection.BeginTransaction();
        var rows = ReadPage(transaction, StateSchema.FolderMoveUnheldRoots, StateEncoding.ReadFolderMove,
            (StateSchema.ParamPairRow, facts.PairRow));
        foreach (var row in rows)
        {
            if (facts.HeldIdentity(row.SourceId) is not null) continue;
            var saved = ReadOne(transaction, StateSchema.SelectSyncedByDropboxId, DecodeSynced,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamId, row.SourceId));
            List<IssueItem> items = [new(Side.Dropbox, row.SourcePath, row.SourceId)];
            if (saved is not null) items.Add(new(Side.Local, saved.LocalPath, saved.LocalId));
            var subject = new IssueSubject(items, Side.Dropbox)
            {
                Move = new(row.SourceId, row.SourcePath, row.DestinationPath, null, true)
            };
            IssueTable.Raise(_connection, transaction, pairId, IssueKind.Failure, TextKey.FailMoveInParts,
                row.SourcePath, subject, DateTimeOffset.UtcNow);
        }
        transaction.Commit();
    });

    /// <inheritdoc />
    public bool HasPendingUnder(string pairId, string path)
    {
        lock (_lock) return !_legacy && _changes.HasPendingUnder(pairId, path);
    }
}

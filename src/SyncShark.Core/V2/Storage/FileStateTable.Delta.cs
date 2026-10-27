namespace Massdex.SyncShark.V2;

internal sealed partial class FileStateTable
{
    /// <summary>Applies a delta's deletes then upserts, each in list order, tombstoning every real deletion.</summary>
    public void WriteDelta(Delta delta, long rootNo,
        Action<Microsoft.Data.Sqlite.SqliteTransaction, long, IReadOnlyList<ItemState>>? observe = null)
    {
        var evidence = delta.DisplacementEvidence;
        using var verifier = evidence is { IncompleteSubtrees.Count: > 0 } ? evidence.OpenAbsenceVerifier?.Invoke() : null;
        if (verifier is not null) evidence = evidence! with { VerifyAbsence = verifier.Check };
        Func<ItemState, bool>? confirmDisplacement = evidence is null ? null : evidence.CanCloseAbsent;
        var suppressed = delta.Changes.Where(change => change.Kind == ChangeKind.Upsert &&
                (delta.IncludesPath?.Invoke(change.Path) ?? true))
            .Select(change => change.ObjectId).ToHashSet(StringComparer.Ordinal);
        using var batch = new StoreBatch(_connection, _settings.StoreBatchRows);
        var retainedAliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in delta.Changes)
            if (change.Kind == ChangeKind.Delete) Delete(change, rootNo, suppressed, batch, delta.IncludesPath, delta.ChangedObject, retainedAliases,
                exact: delta.ExactPaths?.Contains(change.Path) == true);
        var now = _clock.GetUtcNow();
        foreach (var change in delta.Changes)
            if (change.Kind == ChangeKind.Upsert && (delta.IncludesPath?.Invoke(change.Path) ?? true))
                Step(batch, Upsert(change, rootNo, suppressed, now, batch, delta.IncludesPath, confirmDisplacement, retainedAliases));
        // Inventory is durable before a cursor can be completed. Replay also hands off unchanged
        // upserts, so an interruption after an earlier batch cannot orphan its observation.
        foreach (var page in delta.Changes.Where(change => change.Kind == ChangeKind.Upsert &&
            (delta.IncludesPath?.Invoke(change.Path) ?? true)).Chunk(V2Settings.PlanningObservationBatchRows))
            (observe ?? OnInventoryObserved)?.Invoke(batch.Transaction, rootNo,
                page.Select(change => change.State).OfType<ItemState>().ToArray());
        batch.Flush();
    }

}

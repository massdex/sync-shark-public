namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Collects absent old binding preimages for atomic retirement with their relocated Record.</summary>
internal static class RecordBindings
{
    internal static async Task<StateChange> PrepareAsync(ExecutorContext context, StateChange change,
        CancellationToken ct, IReadOnlyCollection<string>? heldKeys = null)
    {
        // Unknown footprints cannot authorize historical reads or retirement; rename discovery will retry under its locks.
        if (heldKeys is null) return change;
        List<SyncedEntry>? stale = null;
        foreach (var entry in change.Record)
        {
            foreach (var before in context.Store.SyncedBindingsById(context.Pair.PairId, Side.Local, entry.LocalId))
            {
                if (!entry.IsRelocationOf(before) || stale?.Contains(before) == true ||
                    !heldKeys.Contains(PathLocks.LocalKey(context.Pair, before.LocalPath), PathLocks.KeyComparer) ||
                    !heldKeys.Contains(PathLocks.DropboxKey(context.Pair, before.DropboxPath), PathLocks.KeyComparer)) continue;
                // A live old endpoint may be a hard link or copy, not stale rename history.
                if (await context.Local.ReadOneAsync(before.LocalPath, ct) is not null ||
                    await context.Dropbox.ReadOneAsync(before.DropboxPath, ct) is not null) continue;
                (stale ??= []).Add(before);
            }
        }
        return stale is null ? change : change with { StaleBindings = stale };
    }
}

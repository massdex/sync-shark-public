namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Owns the one identity check for automatic folder deletes (B-213).</summary>
internal static class FolderIdentity
{
    // An inferred folder delete is a move or a duplicate, never a delete, when the saved identity on the absent side is
    // in scope anywhere, or the deleting side's identity is bound by another binding. A failed read throws (RQ-034).
    // A root this run restored is asked about its original identity, read now: found where the plan keeps the restored
    // folder it is that folder, and found anywhere else in scope it moved.
    internal static async Task<bool> KeptElsewhereAsync(ExecutorContext context, SyncedEntry? saved, Side deletingFrom,
        CancellationToken ct, string? originalId = null, string? keptPath = null)
    {
        if (saved is null) return false;
        if (context.Store.SyncedBindingsById(context.Pair.PairId, deletingFrom, saved.IdOn(deletingFrom))
            .Any(binding => binding != saved)) return true;
        var absent = PathNames.Other(deletingFrom);
        return context.For(absent) is not IIdentitySide identities ||
            await identities.ReadByIdAsync(originalId ?? saved.IdOn(absent), ct) is { InScope: true } found &&
            !(originalId is not null && found.Location.Entry.Id == originalId);
    }
}

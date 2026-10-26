namespace Massdex.SyncShark.V2;

public sealed partial class LocalInventory
{
    private async Task<IReadOnlyList<string>?> ResolveJournalAsync(IReadOnlyList<NtfsUsnChange> changes,
        LocalJournalNames names, Action<string> fallback, Action<NtfsUsnChange, ItemState?, string?, string> defer,
        Func<bool> overflow, CancellationToken ct)
    {
        var lookup = new LocalJournalLookup(_known, _root, CatchUp);
        names.StoredMultipleNames = lookup.MultipleNames;
        var context = new ResolveContext(_rootFileId, _volume, lookup.ById, _settings.LocalDeltaMaxPaths,
            names.Find, names.Ambiguous, fallback, defer, overflow, lookup.Prepare);
        try { return await LocalDeltaResolver.ResolveAsync(changes, context, ct).ConfigureAwait(false); }
        finally { names.StoredMultipleNames = null; lookup.Clear(); }
    }
}

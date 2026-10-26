namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private async Task<ScopeRead> ReadDropboxExactScopesAsync(RootDef root, IInventorySource inventory,
        DropboxInventory dropbox, IReadOnlyList<string> scopes, string cursor, long gen,
        Func<string, bool> include, Action<string> changed, CancellationToken ct)
    {
        try
        {
            var delta = RehearsalEndpoints.Apply(inventory,
                await dropbox.ReadExactAsync(scopes, cursor, gen, include, ct), include);
            PublishScopeDelta(delta with { ChangedObject = changed });
            return CompleteScopeRead(delta.Changes.Select(change => change.Path), scopes, include,
                delta.IncompleteSubtrees, delta.IncompleteReasons);
        }
        catch (Exception error) when (ScopeFailure(error, ct))
        {
            // Root/account verification failure is unknown for every requested row, never absence.
            return new([], scopes, scopes.ToDictionary(path => path, _ => error.Message), false, []);
        }
    }
}

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Observes one subject through its saved bilateral binding, without sibling discovery.</summary>
public sealed class Observer(ExecutorContext context, Parents parents)
{
    /// <summary>Reads valid Local counterparts without interpreting a skipped Dropbox name as absence. The row carries
    /// only what observation found; every other fact is its context owner's.</summary>
    public async Task<Row> ObserveAsync(Side side, string sidePath, CancellationToken ct)
    {
        var local = await ObserveLocalAsync(side, sidePath, ct).ConfigureAwait(false);
        return await ObserveDropboxAsync(side, sidePath, local, ct).ConfigureAwait(false);
    }

    internal async Task<(string Key, Entry? Local, SyncedEntry? Synced)> ObserveLocalAsync(
        Side side, string sidePath, CancellationToken ct)
    {
        var key = PathNames.KeyFor(side, sidePath);
        var synced = context.Store.SyncedByKey(context.Pair.PairId, side, key);
        var localPath = side == Side.Local ? sidePath : parents.Counterpart(Side.Local, sidePath, synced);
        var local = PathNames.IsTransferable(localPath)
            ? await context.Local.ReadOneAsync(localPath, ct) : null;
        return (key, local, synced);
    }

    internal async Task<Row> ObserveDropboxAsync(Side side, string sidePath,
        (string Key, Entry? Local, SyncedEntry? Synced) observed, CancellationToken ct)
    {
        var dropboxPath = side == Side.Dropbox ? sidePath : parents.Counterpart(Side.Dropbox, sidePath, observed.Synced);
        var local = observed.Local;
        var dropbox = await context.Dropbox.ReadOneAsync(dropboxPath, ct);
        var destination = local is not null && dropbox is null ? parents.Destination(Side.Dropbox, local.Path) :
            dropbox is not null && local is null ? parents.Destination(Side.Local, dropbox.Path) : null;
        return new Row(observed.Key, local, dropbox, observed.Synced, [], null, RowContext.Neutral with
        {
            Destination = destination,
            Skipped = local?.Skip is not null || dropbox?.Skip is not null
        });
    }
}

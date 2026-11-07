using Massdex.SyncShark.Engine.Planner;

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
        Side side, string sidePath, CancellationToken ct, (string Path, Entry? Entry)? freshLocal = null)
    {
        var key = PathNames.KeyFor(side, sidePath);
        var synced = context.Store.SyncedByKey(context.Pair.PairId, side, key);
        var localPath = side == Side.Local ? sidePath : parents.Counterpart(Side.Local, sidePath, synced);
        var local = PathNames.IsTransferable(localPath)
            ? side == Side.Local && freshLocal is { } fresh && string.Equals(fresh.Path, sidePath, StringComparison.Ordinal)
                ? fresh.Entry : await context.Local.ReadOneAsync(localPath, ct)
            : null;
        return (key, local, synced);
    }

    internal async Task<Row> ObserveDropboxAsync(Side side, string sidePath,
        (string Key, Entry? Local, SyncedEntry? Synced) observed, CancellationToken ct,
        (string Path, Entry? Entry)? freshDropbox = null)
    {
        var dropboxPath = side == Side.Dropbox ? sidePath : parents.Counterpart(Side.Dropbox, sidePath, observed.Synced);
        var local = observed.Local;
        var dropbox = side == Side.Dropbox && freshDropbox is { } fresh &&
            string.Equals(fresh.Path, sidePath, StringComparison.Ordinal)
                ? fresh.Entry : await context.Dropbox.ReadOneAsync(dropboxPath, ct);
        var synced = observed.Synced;
        var counterpart = side == Side.Local ? dropbox : local;
        if (synced is null && counterpart is not null)
        {
            var other = PathNames.Other(side);
            var binding = context.Store.SyncedByKey(context.Pair.PairId, other,
                PathNames.KeyFor(other, counterpart.Path));
            if (binding is not null && counterpart.Id == binding.IdOn(other) &&
                binding.KeyOn(side) != observed.Key)
            {
                if (Decide.CrossedFar(local, dropbox, binding) == side) synced = binding;
                else if (side == Side.Local) dropbox = null;
                else local = null;
            }
        }
        var destination = local is not null && dropbox is null ? parents.Destination(Side.Dropbox, local.Path) :
            dropbox is not null && local is null ? parents.Destination(Side.Local, dropbox.Path) : null;
        return new Row(observed.Key, local, dropbox, synced, [], null, RowContext.Neutral with
        {
            Destination = destination,
            Skipped = local?.Skip is not null || dropbox?.Skip is not null
        });
    }
}

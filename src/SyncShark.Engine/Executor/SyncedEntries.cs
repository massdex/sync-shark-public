using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Builds a SyncedEntry from two entries (section 7.1 From).</summary>
public static class SyncedEntries
{
    /// <summary>Records each side's path and identity with local stamps and the Dropbox content hash.</summary>
    public static SyncedEntry From(Entry local, Entry dropbox) => new(
        PathNames.Key(local.Path), local.Path, PathNames.DropboxKey(dropbox.Path), dropbox.Path,
        local.IsFolder, local.Id, local.Size, local.Modified.UtcTicks, dropbox.Id, dropbox.ContentHash);

    /// <summary>Records a binding from one side's entry and its counterpart, ordering them for that side.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SyncedEntry From(Side side, Entry entry, Entry counterpart) =>
        side == Side.Local ? From(entry, counterpart) : From(counterpart, entry);

    /// <summary>Projects a one-sided rename through its current mapped counterpart without changing content evidence.</summary>
    internal static SyncedEntry RenameTarget(RenameCandidate incoming, string counterpartPath)
    {
        var local = incoming.Side == Side.Local ? incoming.Moved.Path : counterpartPath;
        var dropbox = incoming.Side == Side.Dropbox ? incoming.Moved.Path : counterpartPath;
        return incoming.Synced with { LocalPath = local, LocalKey = PathNames.Key(local),
            DropboxPath = dropbox, DropboxKey = PathNames.DropboxKey(dropbox) };
    }

    /// <summary>Rewrites paths, keys and ids only; content stamps survive so the row pass still sees unreconciled
    /// edits (sections 3.5, 6.3).</summary>
    public static SyncedEntry Relink(SyncedEntry synced, Entry local, Entry dropbox) => synced with
    {
        LocalPath = local.Path, LocalKey = PathNames.Key(local.Path), LocalId = local.Id,
        DropboxPath = dropbox.Path, DropboxKey = PathNames.DropboxKey(dropbox.Path), DropboxId = dropbox.Id
    };
}

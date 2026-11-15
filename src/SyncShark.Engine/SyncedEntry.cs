using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine;

/// <summary>The last synced facts of one item. LocalKey = PathNames.Key(LocalPath);
/// DropboxKey = PathNames.DropboxKey(DropboxPath). They differ only for keep-both copies (D16).</summary>
public sealed record SyncedEntry(string LocalKey, string LocalPath, string DropboxKey, string DropboxPath,
    bool IsFolder, string LocalId, long LocalSize, long LocalModifiedTicks, string DropboxId,
    string DropboxContentHash)
{
    /// <summary>The stored path on a side; any non-local side selects the Dropbox path.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string PathOn(Side side) => side == Side.Local ? LocalPath : DropboxPath;

    /// <summary>The stored key on a side; any non-local side selects the Dropbox key. Never recomputes a key.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string KeyOn(Side side) => side == Side.Local ? LocalKey : DropboxKey;

    /// <summary>Both identities survive at different keys; old-path absence still needs provider evidence.</summary>
    public bool IsRelocationOf(SyncedEntry before) => LocalId.Length != 0 && DropboxId.Length != 0 &&
        LocalId == before.LocalId && DropboxId == before.DropboxId && IsFolder == before.IsFolder &&
        LocalKey != before.LocalKey && DropboxKey != before.DropboxKey;

    /// <summary>The stored identity on a side; any non-local side selects the Dropbox identity.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string IdOn(Side side) => side == Side.Local ? LocalId : DropboxId;
}

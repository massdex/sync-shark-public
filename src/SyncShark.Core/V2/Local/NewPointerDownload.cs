namespace Massdex.SyncShark.V2;

/// <summary>Metadata-only CopyNew effect, executed after the ordinary durable action intent.</summary>
internal sealed class NewPointerDownload(Func<string, bool> onlineOnly,
    Func<ItemState, string, CancellationToken, Task<ItemState>> create)
{
    internal static bool IsNewFile(SyncAction action) => action.Kind == ActionKind.CopyNew &&
        action.ExpectedTarget is null && !action.RecoveryFirst && action.Source is { Kind: ItemKind.File };

    internal bool WantsPointer(SyncAction action) => IsNewFile(action) && onlineOnly(action.Path);

    internal async Task<ItemState?> TryCreateAsync(SyncAction action, ISyncDirectDownloadSource source,
        CancellationToken ct)
    {
        if (!WantsPointer(action)) return null;
        var planned = action.Source!;
        if (source is not IEndpointWriter reader)
            throw new IOException("Online-only creation needs confirmed remote metadata. Retry or choose available offline.");
        var confirmed = await reader.ObserveAsync(planned.Path, ct).ConfigureAwait(false);
        RequireExactRemote(planned, confirmed);
        ct.ThrowIfCancellationRequested();
        if (!WantsPointer(action))
            throw new ExpectedTargetMismatchException("Storage policy changed before pointer creation. Resync to apply the current policy.");
        // No OpenReadAsync/DownloadToAsync, staging bytes, or alternate effect journal.
        return await create(confirmed!, action.Path, ct).ConfigureAwait(false);
    }

    internal static void RequireExactRemote(ItemState planned, ItemState? actual)
    {
        if (actual is not { Kind: ItemKind.File, Hash: { Length: 64 }, VersionId: not null } ||
            string.IsNullOrWhiteSpace(actual.ObjectId) || string.IsNullOrWhiteSpace(actual.VersionId) ||
            !actual.Hash.All(Uri.IsHexDigit) || actual.Size < 0 || actual.Flags != ItemFlags.None ||
            actual.Path != planned.Path || actual.ObjectId != planned.ObjectId || actual.VersionId != planned.VersionId ||
            !string.Equals(actual.Hash, planned.Hash, StringComparison.OrdinalIgnoreCase) ||
            actual.Size != planned.Size || actual.Mtime != planned.Mtime)
            throw new ExpectedTargetMismatchException("The remote file changed or lacks exact pointer metadata. Resync before creating an online-only file.");
    }
}

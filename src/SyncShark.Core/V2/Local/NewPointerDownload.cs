namespace Massdex.SyncShark.V2;

/// <summary>Metadata-only CopyNew effect, executed after the ordinary durable action intent.</summary>
/// <remarks>
/// A pair-level cause (no host, wrong volume, foreign/overlapping registration) is decided once per endpoint/run and
/// passed as <c>pairUnavailableCause</c>. It never fails the item: the caller falls back to an ordinary byte download
/// and the cause is reported once as the pair notice. Only a failure before any pointer creation may fall back; a
/// native creation failure is propagated as an actionable item failure, because a pointer may already exist.
/// </remarks>
internal sealed class NewPointerDownload(Func<string, bool> onlineOnly,
    StoragePairException? pairUnavailableCause,
    Func<CancellationToken, WindowsCloudFilesProvider> acquireProvider,
    Func<ItemState, string, WindowsCloudFilesProvider, CancellationToken, Task<ItemState>> create,
    Action<StoragePairException>? pairUnavailable = null)
{
    private int _pairReported;

    internal static bool IsNewFile(SyncAction action) => action.Kind == ActionKind.CopyNew &&
        action.ExpectedTarget is null && !action.RecoveryFirst && action.Source is { Kind: ItemKind.File };

    internal bool WantsPointer(SyncAction action) => IsNewFile(action) && onlineOnly(action.Path);

    internal async Task<ItemState?> TryCreateAsync(SyncAction action, ISyncDirectDownloadSource source,
        CancellationToken ct)
    {
        if (!WantsPointer(action)) return null;
        // The pair was found storage-effective (or not) once for this run. A blocked pair is an ordinary download:
        // no metadata observation, no native call, and exactly one pair notice per run.
        if (pairUnavailableCause is { } unavailable)
        {
            ReportPair(unavailable);
            return null;
        }
        var planned = action.Source!;
        if (source is not IEndpointWriter reader)
            throw new IOException("Online-only creation needs confirmed remote metadata. Retry or choose available offline.");
        var confirmed = await reader.ObserveAsync(planned.Path, ct).ConfigureAwait(false);
        RequireExactRemote(planned, confirmed);
        ct.ThrowIfCancellationRequested();
        if (!WantsPointer(action))
            throw new ExpectedTargetMismatchException("Storage policy changed before pointer creation. Resync to apply the current policy.");
        // Provider acquisition is pair-level and happens before any pointer exists. Mapping only this failure to an
        // ordinary download keeps the fallback safe; creation below may have produced a native effect.
        WindowsCloudFilesProvider provider;
        try { provider = acquireProvider(ct); }
        catch (StoragePairException error)
        {
            ReportPair(error);
            return null;
        }
        // No OpenReadAsync/DownloadToAsync, staging bytes, or alternate effect journal.
        return await create(confirmed!, action.Path, provider, ct).ConfigureAwait(false);
    }

    // Once per endpoint/run: the same blocked pair must not write one notice per new file.
    private void ReportPair(StoragePairException error)
    {
        if (Interlocked.Exchange(ref _pairReported, 1) != 0) return;
        pairUnavailable?.Invoke(error);
    }

    internal static void RequireExactRemote(ItemState planned, ItemState? actual)
    {
        if (actual is not { Kind: ItemKind.File, Hash: { Length: 64 }, VersionId: not null } ||
            string.IsNullOrWhiteSpace(actual.ObjectId) || string.IsNullOrWhiteSpace(actual.VersionId) ||
            !actual.Hash.All(Uri.IsHexDigit) || actual.Size < 0 || actual.Flags != ItemFlags.None ||
            !PairPath.Same(actual.Path, planned.Path) || actual.ObjectId != planned.ObjectId || actual.VersionId != planned.VersionId ||
            Content.SameContent(actual, planned) != true ||
            actual.Size != planned.Size || actual.Mtime != planned.Mtime)
            throw new ExpectedTargetMismatchException("The remote file changed or lacks exact pointer metadata. Resync before creating an online-only file.");
    }
}

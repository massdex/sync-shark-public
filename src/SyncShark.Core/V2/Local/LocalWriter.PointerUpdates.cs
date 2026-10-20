namespace Massdex.SyncShark.V2;

/// <summary>A metadata effect inside the ordinary Update intent/commit pipeline, never a transfer.</summary>
internal interface IOwnedPointerUpdateTarget
{
    Task<ItemState> UpdateOwnedPointerAsync(SyncAction action, IEndpointWriter source, CancellationToken ct);
}

internal sealed class OwnedPointerDeferredException(StoragePairException cause) : IOException(cause.Message, cause)
{
    internal StoragePairException Cause { get; } = cause;
}

internal static class OwnedPointerUpdates
{
    // Only unchanged-target propagation. Conflict choices and Recovery-first replacements must
    // never borrow this path: their destination can contain independent user bytes.
    internal static bool IsMetadataUpdate(SyncAction action) => action is
    {
        Kind: ActionKind.Update, Rule: 2 or 28, Target: Side.A, RecoveryFirst: false,
        Source: { Kind: ItemKind.File, Flags: ItemFlags.None, Hash: not null, VersionId: not null },
        ExpectedTarget: { Kind: ItemKind.File, Flags: ItemFlags.OwnedPointer, Hash: not null }
    };

    /// <summary>Finds a pair cause anywhere in an exception chain. A native callback may wrap the
    /// <see cref="StoragePairException"/> thrown by the provider recheck (for example in an
    /// <see cref="AggregateException"/> or another wrapper), so the deferral must not depend on the
    /// outermost type.</summary>
    internal static StoragePairException? PairCause(Exception? error)
    {
        if (error is null) return null;
        if (error is StoragePairException pair) return pair;
        if (error is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
                if (PairCause(inner) is { } found) return found;
            return null;
        }
        return PairCause(error.InnerException);
    }
}

public sealed partial class LocalWriter : IOwnedPointerUpdateTarget
{
    // Composition rechecks the exact pair/account/root binding on every invocation, including
    // the invocation under the native exclusive hold. This works after policy changes to offline.
    internal Func<CancellationToken, WindowsCloudFilesProvider>? PointerUpdateProvider { get; set; }

    async Task<ItemState> IOwnedPointerUpdateTarget.UpdateOwnedPointerAsync(SyncAction action,
        IEndpointWriter source, CancellationToken ct)
    {
        try { return await UpdatePointerCoreAsync(action, source, ct).ConfigureAwait(false); }
        catch (OwnedPointerDeferredException) { throw; }
        catch (Exception error)
        {
            // The provider recheck runs again inside the native callback; if the provider wraps that
            // exception, the outer type is not StoragePairException. Walk the chain so the action is
            // deferred with a pair notice instead of becoming an item failure. Any other exception is
            // rethrown unchanged.
            if (OwnedPointerUpdates.PairCause(error) is { } pairCause)
                throw new OwnedPointerDeferredException(pairCause);
            throw;
        }
    }

    private async Task<ItemState> UpdatePointerCoreAsync(SyncAction action, IEndpointWriter source, CancellationToken ct)
    {
        if (!OwnedPointerUpdates.IsMetadataUpdate(action) || _side != action.Target)
            throw new InvalidOperationException("The action is not an owned pointer metadata update.");
        var getProvider = PointerUpdateProvider ?? throw new StoragePairException("host",
            "The online-only update is not connected to its owned root. Start the host and retry; no bytes were read.");
        // Pair availability precedes remote observation and native target inspection, including direct CLI runs.
        var provider = getProvider(ct);
        var planned = action.Source!;
        var expected = action.ExpectedTarget!;
        var confirmed = await source.ObserveAsync(planned.Path, ct).ConfigureAwait(false);
        NewPointerDownload.RequireExactRemote(planned, confirmed);
        using var root = PilotDirectoryLease.Open(_rootPath, _rootIdentity);
        var parents = new List<PilotDirectoryLease>();
        try
        {
            var segments = PilotPath.NormalizeRelative(action.Path).Split('/');
            var parent = root;
            foreach (var segment in segments.Take(segments.Length - 1))
            {
                parent = PilotDirectoryLease.Open(Path.Combine(parent.Path, segment));
                parents.Add(parent);
            }
            var full = Path.Combine(parent.Path, segments[^1]);
            if (!WindowsCloudFilesProvider.TryReadOwnedPlaceholder(full, out var pointer) || pointer is null ||
                !pointer.Identity.IsBoundTo(_pairId, SyncProviders.Dropbox) || pointer.Identity.ObjectKind != "file" ||
                pointer.SyncRootFileId != root.Identity.FileId || pointer.Identity.DropboxId != planned.ObjectId ||
                pointer.Identity.ContentHash != expected.Hash || pointer.Identity.LogicalSize != expected.Size ||
                pointer.Identity.ModifiedUtc != expected.Mtime ||
                SyncLocalEndpointStore.ObjectId(_rootIdentity.VolumeSerialNumber, pointer.FileId) != expected.ObjectId)
                throw new ExpectedTargetMismatchException("The online-only target identity changed. Rescan and retry; no bytes were read.");
            var previous = DropboxItems.ToEntry(expected with { Path = action.Path,
                ObjectId = pointer.Identity.DropboxId!, VersionId = pointer.Identity.DropboxRevision,
                Flags = ItemFlags.None });
            var (volume, id) = LocalItems.ParseObjectId(expected.ObjectId);
            var result = provider.UpdateDehydratedPlaceholder(previous,
                DropboxItems.ToEntry(confirmed! with { Path = action.Path }), new(volume, id), () =>
                {
                    ct.ThrowIfCancellationRequested();
                    root.VerifyBinding();
                    foreach (var lease in parents) lease.VerifyBinding();
                    if (!ReferenceEquals(provider, getProvider(ct)))
                        throw new ExpectedTargetMismatchException("The Cloud Files connection changed. Rescan and retry.");
                });
            return PointerState(action.Path, result, volume);
        }
        finally { for (var i = parents.Count - 1; i >= 0; i--) parents[i].Dispose(); }
    }
}

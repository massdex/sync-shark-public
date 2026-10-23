using System.ComponentModel;

namespace Massdex.SyncShark.V2;

public sealed partial class LocalInventory
{
    internal Func<string, CloudFilesRootRegistration?>? ReadRootRegistrationForTest { get; set; }

    private void CompletePointerVerification(ItemState item)
    {
        if (_expectedPairId is not { } pairId || _known is not SqliteStateStore store ||
            store.Pair(pairId) is not { } pair) return;
        var remote = pair.A.RootId == _root.RootId ? pair.B : pair.A;
        StorageNotices.Complete(store, pairId, new(_root.RootId, item.ObjectId, remote.RootId, "pointer-verify"));
    }

    private string? KnownOwnedPointerPath(ulong fileId)
    {
        if (_expectedPairId is null) return null;
        var item = _known.ByObject(_root.RootId, SyncLocalEndpointStore.ObjectId(_volume, fileId));
        if (item is null) return null;
        if (item.Flags.HasFlag(ItemFlags.OwnedPointer)) return item.Path;
        // A conversion may already have changed the native object but not its cached flags.
        if (_known is not SqliteStateStore store || store.Pair(_expectedPairId) is not { } pair) return null;
        var remote = pair.A.RootId == _root.RootId ? pair.B : pair.A;
        return store.StoragePointerFor(_root.RootId, item.ObjectId, remote.RootId) is not null ||
            store.StorageSettlementFor(_root.RootId, item.ObjectId, remote.RootId) is not null ? item.Path : null;
    }

    internal static string RegistrationPath(string path)
    {
        // Registration rows historically trimmed the slash from drive roots. E: is drive-relative
        // to Path.GetFullPath, so restore its root slash before normalising it.
        if (path.Length == 2 && char.IsLetter(path[0]) && path[1] == ':') path += Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private Func<string, WindowsCloudPlaceholderInfo, long, OwnedPointerObservation>? OwnedPointerClassifier()
    {
        if (_expectedPairId is not { } pairId || _known is not SqliteStateStore store ||
            store.Pair(pairId) is not { } pair) return null;
        var remoteRoot = pair.A.RootId == _root.RootId ? pair.B : pair.A;
        var recorded = store.StorageRegistrations().FirstOrDefault(entry => entry.RootId == _root.RootId);
        // One native registration read per scan, not per file; no item collection is retained.
        var registration = new Lazy<CloudFilesRootRegistration?>(() =>
        {
            if (recorded is null || !string.Equals(RegistrationPath(recorded.Path),
                    RegistrationPath(_root.RootPath), StringComparison.OrdinalIgnoreCase)) return null;
            try { return (ReadRootRegistrationForTest ?? WindowsCloudFilesProvider.ReadRootRegistration)(_root.RootPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
            { return null; }
        });
        return (path, native, physicalSize) =>
        {
            var objectId = SyncLocalEndpointStore.ObjectId(_volume, native.FileId);
            var stored = store.ByObject(_root.RootId, objectId);
            var pointer = store.StoragePointerFor(_root.RootId, objectId, remoteRoot.RootId);
            var receipt = pointer?.Remote;
            var result = OwnedPointerProof.Classify(native, recorded?.RegistrationId, registration.Value,
                pairId, remoteRoot.Provider, _volume, stored, receipt, physicalSize, allowDirtyRename: true);
            // Refresh changes the native identity before its receipt transaction. Only an exact
            // pending target match explains that window; arbitrary identity changes stay protected.
            if (result == OwnedPointerObservation.NeedsVerification && pointer is { JobId: > 0 } &&
                OwnedPointerProof.IsDehydrated(native) && native.Identity.DropboxId is { } remoteId &&
                store.ByObject(remoteRoot.RootId, remoteId) is { } target && target.Kind == ItemKind.File &&
                target.VersionId == native.Identity.DropboxRevision && Content.SameHash(target.Hash, native.Identity.ContentHash) == true &&
                target.Size == native.Identity.LogicalSize && physicalSize == target.Size &&
                native.State.LogicalSize == target.Size)
                return OwnedPointerObservation.NotObserved;
            if (result == OwnedPointerObservation.NeedsVerification && receipt is not null &&
                OwnedPointerProof.IsDehydrated(native) && native.Identity.DropboxId == receipt.Id &&
                Content.SameHash(native.Identity.ContentHash, receipt.ContentHash) == true && native.Identity.LogicalSize == receipt.Size &&
                physicalSize == receipt.Size && native.State.LogicalSize == receipt.Size &&
                store.StorageSettlementFor(_root.RootId, objectId, remoteRoot.RootId)?.Kind == StorageSettlement.AcknowledgeMove)
                return OwnedPointerObservation.NotObserved;
            // An ack may finish between native observation and the receipt query. A lost ack row
            // has the same shape: unchanged content, old identity path, both indexed sides moved.
            if (result == OwnedPointerObservation.NeedsVerification && receipt is not null && stored is not null &&
                stored.Path == path && receipt.RelativePath == path && native.Identity.RelativePath != path &&
                OwnedPointerProof.IsDehydrated(native) && native.Identity.DropboxId == receipt.Id &&
                Content.SameHash(native.Identity.ContentHash, receipt.ContentHash) == true && native.Identity.LogicalSize == receipt.Size &&
                physicalSize == receipt.Size && native.State.LogicalSize == receipt.Size &&
                store.ByObject(remoteRoot.RootId, receipt.Id!) is { } committed &&
                committed.Path == path && committed.VersionId == receipt.Revision &&
                Content.SameContent(committed.Size, committed.Hash, receipt.Size, receipt.ContentHash) == true)
            {
                store.QueueVerifiedPointerAcknowledgment(_root.RootId, objectId, remoteRoot.RootId, receipt);
                return OwnedPointerObservation.NotObserved;
            }
            if (result == OwnedPointerObservation.Pointer && stored is not null && receipt is not null)
            {
                // Journal flags/cache hashes are not new byte evidence. Metadata-only proof heals
                // an unchanged pointer; no hydration is needed to close a stale verification notice.
                if (stored.Flags.HasFlag(ItemFlags.NeedsRehash) &&
                    store.CompleteRehash(_root.RootId, stored, receipt.ContentHash!))
                    CompletePointerVerification(stored);
                if ((!native.State.IsInSync || !string.Equals(native.Identity.RelativePath, path, StringComparison.Ordinal)) &&
                    string.Equals(stored.Path, path, StringComparison.Ordinal))
                    store.QueueVerifiedPointerAcknowledgment(_root.RootId, objectId, remoteRoot.RootId,
                        receipt with { RelativePath = path });
            }
            if (result == OwnedPointerObservation.NeedsVerification && stored is not null)
            {
                // Do not persist a mismatch from a native snapshot overtaken by a refresh/ack.
                // This second metadata-only read is limited to suspect items, never normal scans.
                var fullPath = PilotPath.ResolveContainedPath(_root.RootPath, path);
                WindowsCloudPlaceholderInfo? latest;
                try
                {
                    if (new FileInfo(fullPath).Length != physicalSize) return OwnedPointerObservation.NotObserved;
                    if (ReadOwnedPlaceholderForTest is { } read) latest = read(fullPath);
                    else if (!WindowsCloudFilesProvider.TryReadOwnedPlaceholder(fullPath, out latest))
                        return OwnedPointerObservation.NotObserved;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
                { return OwnedPointerObservation.NotObserved; }
                if (latest is null || latest != native) return OwnedPointerObservation.NotObserved;
                // Recheck the durable side too: conversion/refresh may commit while the native
                // probe is in flight, so neither half of an old/new pair is failure evidence.
                if (store.StoragePointerFor(_root.RootId, objectId, remoteRoot.RootId)?.Remote != receipt)
                    return OwnedPointerObservation.NotObserved;
                // True byte/identity evidence holds the entity before publishing its new path.
                // Avoid repeatedly writing the same obligation on every scan.
                if (!stored.Flags.HasFlag(ItemFlags.NeedsRehash))
                    store.RequireRehash(_root.RootId, stored, OwnedPointerProof.VerificationMessage);
                if (receipt is not null)
                    store.RequestPointerVerification(pairId, _root.RootId, objectId, path, receipt);
            }
            return result;
        };
    }
}

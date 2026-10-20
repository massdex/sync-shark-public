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

    private Func<string, WindowsCloudPlaceholderInfo, long, OwnedPointerObservation>? OwnedPointerClassifier()
    {
        if (_expectedPairId is not { } pairId || _known is not SqliteStateStore store ||
            store.Pair(pairId) is not { } pair) return null;
        var remoteRoot = pair.A.RootId == _root.RootId ? pair.B : pair.A;
        var recorded = store.StorageRegistrations().SingleOrDefault(entry => entry.RootId == _root.RootId);
        // One native registration read per scan, not per file; no item collection is retained.
        // The root lease holds physical identity during this advisory observation. Every effect
        // must recheck registration and entity under its own native hold.
        var registration = new Lazy<CloudFilesRootRegistration?>(() =>
        {
            if (recorded is null || !string.Equals(Path.GetFullPath(recorded.Path),
                    Path.GetFullPath(_root.RootPath), StringComparison.OrdinalIgnoreCase)) return null;
            try { return (ReadRootRegistrationForTest ?? WindowsCloudFilesProvider.ReadRootRegistration)(_root.RootPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
            { return null; }
        });
        return (path, native, physicalSize) =>
        {
            var objectId = SyncLocalEndpointStore.ObjectId(_volume, native.FileId);
            var stored = store.ByObject(_root.RootId, objectId);
            var receipt = store.StoragePointerFor(_root.RootId, objectId, remoteRoot.RootId)?.Remote;
            var result = OwnedPointerProof.Classify(native, recorded?.RegistrationId, registration.Value,
                pairId, remoteRoot.Provider, _volume, stored, receipt, physicalSize, allowDirtyRename: true);
            if (result == OwnedPointerObservation.NeedsVerification && stored is not null)
            {
                // Hold the entity before publishing its new path. A canceled scan cannot turn
                // the old path into deletion evidence or lose the obligation to verify bytes.
                store.RequireRehash(_root.RootId, stored, OwnedPointerProof.VerificationMessage);
                if (receipt is not null)
                    store.RequestPointerVerification(pairId, _root.RootId, objectId, path, receipt);
                else
                    store.AddNotices([new Notice(pairId, RehashProtection.Rule, NoticeKind.UserDecision,
                        path, OwnedPointerProof.VerificationMessage + "; its stored cloud receipt is missing. " +
                        "Restore the receipt and Retry; Ignore leaves the file protected.", "sync-retry")]);
            }
            return result;
        };
    }
}

using Massdex.SyncShark;
using Massdex.SyncShark.V2;

internal static class OwnedPointerProofChecks
{
    internal static void Run()
    {
        const string pair = "pair-a";
        const string token = "11111111-1111-4111-8111-111111111111";
        var remote = new PilotRemoteEntry("old/file.txt", PilotEntryKind.File, "remote-file", "revision-1",
            10, "hash", DateTimeOffset.UnixEpoch);
        var identity = WindowsCloudFileIdentity.FromRemote(pair, remote);
        var registration = new CloudFilesRootRegistration(7,
            WindowsCloudFileIdentity.Root(pair) with { V2OwnershipToken = token });
        var state = new SyncCloudFileState("new/file.txt", SyncCloudFileAvailability.OnlineOnly,
            false, false, 10, 0, remote.Id, remote.Revision, remote.ContentHash);
        var native = new WindowsCloudPlaceholderInfo(identity, state, 9, 7, false, 0x31);
        var stored = new ItemState(remote.RelativePath, ItemKind.File,
            SyncLocalEndpointStore.ObjectId(3, 9), null, remote.ContentHash, 10,
            DateTimeOffset.UnixEpoch, ItemFlags.OwnedPointer);
        OwnedPointerObservation Classify(WindowsCloudPlaceholderInfo value, bool allow = true,
            ItemState? row = null, PilotRemoteEntry? receipt = null) => OwnedPointerProof.Classify(value,
                token, registration, pair, SyncProviders.Dropbox, 3, row ?? stored, receipt ?? remote,
                10, allowDirtyRename: allow);
        Require(Classify(native) == OwnedPointerObservation.Pointer,
            "same entity, receipt, size and empty data ranges admit a renamed dirty pointer");
        Require(Classify(native, allow: false) == OwnedPointerObservation.NeedsVerification,
            "dirty rename remains protected until durable acknowledgment is wired");
        Require(Classify(native with { State = state with { IsInSync = true } }, allow: false) == OwnedPointerObservation.Pointer,
            "unchanged in-sync owned pointer does not need rename acknowledgment");
        Require(Classify(native with { FileId = 11 }) == OwnedPointerObservation.Foreign,
            "file-id mismatch is foreign, never an owned pointer");
        Require(Classify(native with { SyncRootFileId = 11 }) == OwnedPointerObservation.Foreign,
            "root file-id mismatch is foreign");
        Require(OwnedPointerProof.Classify(native, "22222222-2222-4222-8222-222222222222", registration,
            pair, SyncProviders.Dropbox, 3, stored, remote, 10, true) == OwnedPointerObservation.Foreign,
            "same root path with a different registration GUID is foreign");
        Require(OwnedPointerProof.Classify(native, token, registration, pair, SyncProviders.Dropbox,
            3, null, remote, 10, true) == OwnedPointerObservation.Foreign,
            "serialized identity alone cannot invent a stored owned entity");
        foreach (var changed in new[] {
            native with { Identity = identity with { DropboxId = "other" } },
            native with { Identity = identity with { DropboxRevision = "revision-2" } },
            native with { Identity = identity with { LogicalSize = 11 } },
            native with { State = state with { Availability = SyncCloudFileAvailability.PartiallyHydrated } },
            native with { State = state with { OnDiskBytes = 1 } },
            native with { ModifiedDataSize = 1 },
            native with { RawPlaceholderState = uint.MaxValue },
            native with { RawPlaceholderState = 1 } })
            Require(Classify(changed) == OwnedPointerObservation.NeedsVerification,
                "receipt/size/range/state mismatch is protected verification debt");
        Require(OwnedPointerProof.Classify(native, token, registration, pair, SyncProviders.Dropbox,
            3, stored, remote, 11, true) == OwnedPointerObservation.NeedsVerification,
            "logical EOF mismatch is not a harmless rename");
        var resident = native with { State = state with {
            Availability = SyncCloudFileAvailability.AvailableOffline, OnDiskBytes = 10 } };
        Require(Classify(resident with { Identity = identity with { DropboxRevision = "old-receipt" } }) ==
            OwnedPointerObservation.Resident, "fully resident bytes are ordinary hashable content");
        var entry = new PilotLocalEntry("new/file.txt", PilotEntryKind.File, "unused", 10,
            DateTime.UnixEpoch, null, 3, 9, SyncCloudFileAvailability.PartiallyHydrated,
            ContentUnreadable: true, NeedsRehash: true);
        var observed = LocalItems.FromScan(entry);
        Require(observed.Flags.HasFlag(ItemFlags.NeedsRehash) && observed.Flags.HasFlag(ItemFlags.Placeholder) &&
            observed.Flags.HasFlag(ItemFlags.Unreadable) && observed.Hash is null,
            "partial observation survives inventory but cannot upload or auto-recall");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Owned pointer proof: " + message);
    }
}

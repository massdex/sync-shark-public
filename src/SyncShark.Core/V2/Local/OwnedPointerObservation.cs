namespace Massdex.SyncShark.V2;

internal enum OwnedPointerObservation { Foreign, Resident, Pointer, NeedsVerification }

/// <summary>Metadata-only proof. Paths and IsInSync alone never establish ownership.</summary>
internal static class OwnedPointerProof
{
    internal const string VerificationMessage = "This online-only file changed locally; Sync Shark will verify it";

    internal static OwnedPointerObservation Classify(WindowsCloudPlaceholderInfo native,
        string? expectedRegistration, CloudFilesRootRegistration? registration, string pairId,
        string? provider, uint volume, ItemState? stored, PilotRemoteEntry? receipt,
        long physicalSize, bool allowDirtyRename = false)
    {
        if (expectedRegistration is null || registration is null || registration.OwnershipToken != expectedRegistration ||
            registration.FileId != native.SyncRootFileId ||
            registration.Identity?.IsBoundTo(pairId, provider) != true ||
            !native.Identity.IsBoundTo(pairId, provider) || native.IsDirectory ||
            native.Identity.ObjectKind != "file" || native.FileId == 0)
            return OwnedPointerObservation.Foreign;

        // Resident bytes are ordinary data, even when their old cloud receipt is stale.
        if (native.State.Availability == SyncCloudFileAvailability.AvailableOffline &&
            native.State.OnDiskBytes == physicalSize)
            return OwnedPointerObservation.Resident;

        if (stored is null || stored.ObjectId != SyncLocalEndpointStore.ObjectId(volume, native.FileId))
            return OwnedPointerObservation.Foreign;

        if (stored.Kind != ItemKind.File || stored.Flags != ItemFlags.OwnedPointer || receipt is null ||
            receipt.Kind != PilotEntryKind.File || stored.Size < 0 ||
            string.IsNullOrWhiteSpace(receipt.Id) || string.IsNullOrWhiteSpace(receipt.Revision) ||
            string.IsNullOrWhiteSpace(receipt.ContentHash) ||
            native.Identity.DropboxId != receipt.Id || native.Identity.DropboxRevision != receipt.Revision ||
            native.Identity.ContentHash != receipt.ContentHash || stored.Hash != receipt.ContentHash ||
            native.Identity.LogicalSize != stored.Size || receipt.Size != stored.Size ||
            physicalSize != stored.Size || native.State.LogicalSize != stored.Size ||
            native.State.Availability != SyncCloudFileAvailability.OnlineOnly ||
            native.State.OnDiskBytes != 0 || native.ModifiedDataSize != 0 ||
            native.RawPlaceholderState == uint.MaxValue || (native.RawPlaceholderState & 1) == 0 ||
            (native.RawPlaceholderState & 0x10) == 0)
            return OwnedPointerObservation.NeedsVerification;

        // Production must leave this false until a recorded remote Move can replay the
        // CfSetInSyncState acknowledgment after a crash. Never clear dirty state in a scan.
        return native.State.IsInSync || allowDirtyRename
            ? OwnedPointerObservation.Pointer : OwnedPointerObservation.NeedsVerification;
    }
}

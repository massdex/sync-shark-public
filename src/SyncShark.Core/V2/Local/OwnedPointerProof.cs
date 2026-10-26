namespace Massdex.SyncShark.V2;

/// <summary>Metadata-only proof. Paths and IsInSync alone never establish ownership.</summary>
internal static class OwnedPointerProof
{
    internal const string VerificationMessage = "This online-only file changed locally; Sync Shark will verify it";
    internal const string ObservationMessage = "Online-only metadata is temporarily unavailable; Retry on the next sync without downloading the file";

    // Windows can report PARTIALLY_ON_DISK even when no data is resident. Native byte
    // counts decide dehydration; in-sync is a separate acknowledgment, not byte evidence.
    internal static bool IsDehydrated(uint rawState, long onDiskBytes, long modifiedBytes) =>
        rawState != uint.MaxValue && (rawState & 1) != 0 && (rawState & 0x10) != 0 &&
        onDiskBytes == 0 && modifiedBytes == 0;

    internal static bool IsDehydrated(WindowsCloudPlaceholderInfo native) =>
        native.State.Availability == SyncCloudFileAvailability.OnlineOnly &&
        (IsDehydrated(native.RawPlaceholderState, native.State.OnDiskBytes, native.ModifiedDataSize) ||
         // A successfully decoded CF standard-info record remains authoritative when
         // compatibility mode hides all placeholder bits. Ownership is checked separately.
         native.HasStandardInfo && native.RawPlaceholderState == 0 &&
         native.State.OnDiskBytes == 0 && native.ModifiedDataSize == 0);

    internal static OwnedPointerObservation Classify(WindowsCloudPlaceholderInfo native,
        string? expectedRegistration, CloudFilesRootRegistration? registration, string pairId,
        string? provider, uint volume, ItemState? stored, PilotRemoteEntry? receipt,
        long physicalSize, bool allowDirtyRename = false)
    {
        if (registration is null || native.RawPlaceholderState == uint.MaxValue ||
            native.State.OnDiskBytes < 0 || native.ModifiedDataSize < 0)
            return OwnedPointerObservation.NotObserved;
        if (expectedRegistration is null || registration.OwnershipToken != expectedRegistration ||
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

        // The receipt belongs to the entity. Journal/cache flags do not invalidate it.
        // Missing durable metadata can be the native-change/DB-commit window, not a byte edit.
        if (receipt is null || receipt.Kind != PilotEntryKind.File ||
            string.IsNullOrWhiteSpace(receipt.Id) || string.IsNullOrWhiteSpace(receipt.Revision) ||
            string.IsNullOrWhiteSpace(receipt.ContentHash))
            return OwnedPointerObservation.NotObserved;
        if (stored.Kind != ItemKind.File || native.Identity.DropboxId != receipt.Id ||
            native.Identity.DropboxRevision != receipt.Revision ||
            native.Identity.ContentHash != receipt.ContentHash ||
            native.Identity.LogicalSize != receipt.Size || physicalSize != receipt.Size ||
            native.State.LogicalSize != receipt.Size || !IsDehydrated(native))
            return OwnedPointerObservation.NeedsVerification;

        // A dirty, receipt-identical empty pointer needs a durable ack, never a download.
        return native.State.IsInSync || allowDirtyRename
            ? OwnedPointerObservation.Pointer : OwnedPointerObservation.NotObserved;
    }
}

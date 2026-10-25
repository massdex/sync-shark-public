namespace Massdex.SyncShark;

public sealed partial class WindowsCloudFilesProvider
{
    internal static void RequireObservedResidencyForUnregistration(WindowsCloudPlaceholderInfo placeholder)
    {
        if (placeholder.RawPlaceholderState == uint.MaxValue ||
            placeholder.State.Availability == SyncCloudFileAvailability.NotManaged ||
            placeholder.State.OnDiskBytes < 0 || placeholder.ModifiedDataSize < 0)
            throw new IOException("Cloud Files unregistration stopped because owned file residency could not be inspected safely.");
    }

    // Called only after a successful CF_PLACEHOLDER_STANDARD_INFO query. Attribute/tag
    // virtualization can hide PARTIAL and even the cloud reparse tag; absence of those
    // hints is never proof that the bytes are present. Pinning is intent, not residency.
    internal static SyncCloudFileAvailability DecodePlaceholderAvailability(bool directory,
        long physicalSize, long onDiskBytes, long modifiedBytes, uint attributes, uint rawState)
    {
        if (physicalSize < 0 || onDiskBytes < 0 || modifiedBytes < 0)
            throw new IOException("Cloud Files returned contradictory residency metadata; retry metadata inspection without reading file contents.");
        if (directory) return SyncCloudFileAvailability.AvailableOffline;

        const uint offline = 0x00001000;
        const uint recallOnDataAccess = 0x00400000;
        var recallRequired = (attributes & (offline | recallOnDataAccess)) != 0;
        if (onDiskBytes == 0 && (physicalSize > 0 || recallRequired))
            return SyncCloudFileAvailability.OnlineOnly;
        if (onDiskBytes < physicalSize || recallRequired ||
            (rawState != uint.MaxValue && (rawState & (0x10u | 0x20u)) != 0))
            return SyncCloudFileAvailability.PartiallyHydrated;
        if (rawState == uint.MaxValue)
            throw new IOException("Cloud Files placeholder state is unavailable; retry metadata inspection without reading file contents.");
        return SyncCloudFileAvailability.AvailableOffline;
    }
}

namespace Massdex.SyncShark.V2;

internal static class NewPointerNativeChecks
{
    // Invoke only inside the explicitly disposable registered native fixture. This method registers nothing.
    internal static PilotRemoteEntry CheckCreateOnly(WindowsCloudFilesProvider provider, PilotDirectoryIdentity rootIdentity,
        PilotRemoteEntry remote, string hydratedPath, Action assertNoAdditionalDownloads)
    {
        var newRemote = remote with { RelativePath = "direct-new-pointer.bin", ModifiedUtc = DateTimeOffset.UnixEpoch.AddDays(1) };
        var created = provider.CreateNewPlaceholder(newRemote, rootIdentity);
        if (created.State.Availability != SyncCloudFileAvailability.OnlineOnly || !created.State.IsInSync ||
            created.Identity.DropboxId != newRemote.Id || created.Identity.DropboxRevision != newRemote.Revision ||
            created.Identity.ContentHash != newRemote.ContentHash || created.Identity.LogicalSize != newRemote.Size)
            throw new IOException("Direct creation did not retain exact online-only remote metadata.");
        assertNoAdditionalDownloads();
        // A same-name hydrated owned placeholder is not a create success, even if its identity matches.
        var bytes = File.ReadAllBytes(hydratedPath);
        var rejected = false;
        try { provider.CreateNewPlaceholder(remote with { ModifiedUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(hydratedPath)) }, rootIdentity); }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        { rejected = true; }
        if (!rejected || !File.ReadAllBytes(hydratedPath).SequenceEqual(bytes))
            throw new IOException("Create-only pointer publication overwrote a hydrated local file.");
        return newRemote;
    }
}

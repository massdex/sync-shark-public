namespace Massdex.SyncShark;

public sealed partial class SyncLocalEndpointStore
{
    // A checkpoint can describe an already-published download from an older build. Verify under the
    // same exclusive, identity-bound authority as publication before reconciling its metadata.
    internal async Task<PilotRemoteEntry> StampPublishedDownloadAsync(string relativePath, string objectId,
        long size, string hash, DateTimeOffset time, CancellationToken ct)
    {
        using var root = OpenRoot();
        using var path = new HeldPath(root, relativePath);
        path.Parent.VerifyBinding();
        using var file = SyncLocalFileAuthority.OpenExistingExclusive(path.FullPath, FileAccess.ReadWrite);
        file.VerifySingleLink();
        if (!string.Equals(file.FinalPath, path.FullPath, StringComparison.OrdinalIgnoreCase))
            throw new DownloadStageMismatchException("The download path changed; its date was left alone.");
        var current = await DescribeFileAsync(file, path.Relative, ct).ConfigureAwait(false);
        if (current.Id != objectId || current.Size != size ||
            !string.Equals(current.ContentHash, hash, StringComparison.OrdinalIgnoreCase))
            throw new DownloadStageMismatchException("The published download changed; its date was left alone.");
        ct.ThrowIfCancellationRequested();
        File.SetLastWriteTimeUtc(file.Handle, time.UtcDateTime);
        return current; // LocalWriter reads the observed filesystem time after this exclusive handle closes.
    }
}

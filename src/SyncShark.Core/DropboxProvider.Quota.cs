namespace Massdex.SyncShark;

public sealed partial class DropboxPilotClient
{
    internal async Task<bool> HasUploadSpaceAsync(long needBytes, CancellationToken ct)
    {
        var token = await tokenSource.GetAccessTokenAsync(ct).ConfigureAwait(false);
        var usage = await DropboxOAuth.GetSpaceUsageAsync(httpClient, token, ct).ConfigureAwait(false);
        return usage.AllocatedBytes > usage.UsedBytes && usage.AllocatedBytes - usage.UsedBytes >= needBytes;
    }
}

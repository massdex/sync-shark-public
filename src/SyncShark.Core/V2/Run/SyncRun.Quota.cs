namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private async Task RefreshDropboxQuotaAsync(PairDef pair, CancellationToken ct)
    {
        var row = _store.NoticeById(pair.PairId, DropboxQuota.Id(pair.PairId));
        if (row is not { Notice: { UploadSuspended: true } notice }) return;
        // Legacy holds without a known size stay held until explicit Retry.
        var needBytes = notice.UploadQuotaNeedBytes ?? long.MaxValue;
        // Recheck only the account that refused this pair's upload, at most once per run.
        // A timeout or failed probe preserves the hold and good inventory.
        var root = notice.UploadQuotaSide == Side.A ? pair.A : pair.B;
        if (root.Provider != SyncProviders.Dropbox || _endpoints.Writer(root) is not DropboxWriter writer) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            if (!await writer.HasUploadSpaceAsync(needBytes, timeout.Token).ConfigureAwait(false)) return;
        }
        catch (Exception error) when (RunFailure(error, ct))
        { RunLog.Trace("Dropbox quota check deferred: " + error.Message); return; }
        _store.AddNotices([notice with { UploadSuspended = false }]);
        _store.CloseNotice(DropboxQuota.Id(pair.PairId), "space-available");
        foreach (var retry in _store.ReadSyncRetries(pair.PairId))
            if (retry.LastError == DropboxQuota.Detail)
                _store.SaveSyncRetry(pair.PairId, retry with { RetryAfter = DateTimeOffset.MinValue });
    }
}

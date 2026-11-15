namespace Massdex.SyncShark.Engine.Dropbox;

public sealed partial class DropboxWriter
{
    /// <summary>Submits only the persisted member identities, without overwrite or ownership transfer.</summary>
    public async Task<FolderSubmitResult> SubmitMoveBatchAsync(IReadOnlyList<(string From, string To)> members,
        RequestAttempt attempt, CancellationToken ct)
    {
        if (members.Count == 0 || members.Count > DropboxApiLimits.BatchMaxEntries)
            throw new ArgumentException(null, nameof(members));
        var entries = members.Select(member => new
        {
            from_path = member.From,
            to_path = DropboxPaths.Remote(pair, member.To)
        }).ToArray();
        FolderSubmitResult result;
        try
        {
            result = await api.RpcAsync(DropboxProtocol.MoveBatch,
                new { entries, autorename = false, allow_ownership_transfer = false },
                DropboxReplyShapes.FolderSubmit(pair, members.Count, move: true), attempt, ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested && attempt.Completion is { Result: RequestResult.UnknownWrite })
        {
            return new FolderSubmitResult.Unknown(TextKey.DropboxRefused);
        }
        catch (Exception error) when (attempt.Completion is { NoEffectProven: true } &&
            DropboxErrors.NoUsableReply(error, attempt, ct))
        {
            return new FolderSubmitResult.KnownNoEffect(TextKey.DropboxRefused);
        }
        if (result is FolderSubmitResult.Terminal terminal) ReportMoveOutcome(terminal.Results, attempt);
        return result;
    }

    /// <summary>An invalid job leaves all ordinals unattributed for identity resolution by the custody owner.</summary>
    public async Task<FolderJobResult> ReadMoveBatchAsync(string jobId, int expectedCount,
        RequestAttempt attempt, CancellationToken ct)
    {
        if (expectedCount <= 0 || expectedCount > DropboxApiLimits.BatchMaxEntries)
            throw new ArgumentOutOfRangeException(nameof(expectedCount));
        FolderJobResult result;
        try
        {
            result = await api.RpcAsync(DropboxProtocol.MoveBatchCheck, new { async_job_id = jobId },
                DropboxReplyShapes.FolderJob(pair, expectedCount, move: true), attempt, ct).ConfigureAwait(false);
        }
        catch (DropboxApiException error) when (DropboxErrors.IsInvalidFolderJob(error))
        {
            return new FolderJobResult.Terminal(Enumerable.Range(0, expectedCount)
                .Select(ordinal => (FolderMemberResult)new FolderMemberResult.Unknown(ordinal, null)).ToArray());
        }
        if (result is FolderJobResult.Terminal terminal) ReportMoveOutcome(terminal.Results, attempt);
        return result;
    }

    private void ReportMoveOutcome(IReadOnlyList<FolderMemberResult> results, RequestAttempt attempt)
    {
        if (results.Any(result => result is FolderMemberResult.KnownNoEffect { WriteThrottled: true }))
            api.ReportBatchOutcome(DropboxProtocol.MoveBatch, true, attempt.Completion?.ProviderWait);
    }
}

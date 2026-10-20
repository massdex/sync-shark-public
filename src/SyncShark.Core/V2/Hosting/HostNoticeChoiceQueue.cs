namespace Massdex.SyncShark.V2;

/// <summary>Host-owned bounded choice lifetime, independent of disconnected clients.</summary>
internal sealed class HostNoticeChoiceQueue(IStateStore store,
    Func<AutoSyncHostRequest, CancellationToken, Task<object>> execute)
{
    private readonly IStateStore _store = store;
    private readonly Func<AutoSyncHostRequest, CancellationToken, Task<object>> _execute = execute;
    private readonly object _pendingGate = new();
    private readonly Dictionary<Task, Task> _pendingChoices = [];
    private bool _stopping;

    public async Task<object> ExecuteAsync(AutoSyncHostRequest request, CancellationToken ct)
    {
        var invocation = SyncCliRegistry.Parse(request.CliArgs!);
        if (invocation.Command != "conflicts resolve" ||
            invocation.Choice?.ToLowerInvariant() is not (NoticeChoices.Ignore or NoticeChoices.Exclude))
            return await _execute(request, ct).ConfigureAwait(false);
        var selected = invocation.PairId is not null && invocation.Key is not null
            ? _store.NoticeById(invocation.PairId, invocation.Key) : null;
        if (selected is not { Closed: false } || NoticeChoices.IsPairRetryNotice(selected.Value.Notice))
            return await _execute(request, ct).ConfigureAwait(false);
        Task<object> work;
        lock (_pendingGate)
        {
            if (_stopping || _pendingChoices.Count >= 64)
                throw new SyncCliUsageException("Notice choices are still draining. The notice is unchanged; retry shortly.");
            // Execute synchronously through validation/capture, then let the SAME runtime wait on
            // its run gate. A client disconnect or safe stop must not cancel accepted exclusions.
            var args = request.CliArgs!.ToList();
            var timeout = args.IndexOf("--timeout-seconds");
            if (timeout >= 0) args.RemoveRange(timeout, 2);
            work = _execute(request with { CliArgs = args.ToArray() }, CancellationToken.None);
            if (!work.IsCompleted)
            {
                _pendingChoices.Add(work, Task.CompletedTask);
                _pendingChoices[work] = ObserveChoiceAsync(work, selected.Value.Notice);
            }
        }
        if (work.IsCompleted) return await work.ConfigureAwait(false);
        var detail = $"Queued behind the active run of pair {invocation.PairId}. The exclusion will apply at the safe point independently of the client deadline; no files are deleted.";
        return new HostCliOutput(0, invocation.Json
            ? System.Text.Json.JsonSerializer.Serialize(new { queued = true, pairId = invocation.PairId, detail })
            : detail, string.Empty);
    }

    private async Task ObserveChoiceAsync(Task<object> work, Notice notice)
    {
        await Task.Yield(); // publish the observer before it can remove its entry
        try
        {
            if (await work.ConfigureAwait(false) is HostCliOutput { ExitCode: not 0 } result)
                throw new IOException(result.Stderr + result.Stdout);
        }
        catch (Exception error)
        {
            RunOutput.Observe(error);
            try
            {
                // Explicit failed choice recovery must reopen this item even if a previous
                // partial write closed it as Ignore. Never reuse the pair-outage identity.
                _store.CloseNotice(notice.NoticeId!, "choice-failed");
                _store.AddNotices([notice with { Detail = "The queued item choice could not be saved: " +
                    error.Message + ". Check disk space and store access, then Retry the item or choose Ignore again." }]);
            }
            catch (Exception noticeError) { RunOutput.Observe(noticeError); }
        }
        finally { lock (_pendingGate) _pendingChoices.Remove(work); }
    }

    public async Task StopAsync(Func<Task> stop)
    {
        lock (_pendingGate) _stopping = true;
        try { await stop().ConfigureAwait(false); }
        finally
        {
            Task[] pending;
            lock (_pendingGate) pending = _pendingChoices.Values.ToArray();
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }
}

using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Owns ordinary and staged-upload admission for one prepared item.</summary>
internal readonly struct ItemDispatch(ExecutorContext _context, ActionExecutor _executor, ItemSync _items)
{
    // One dispatch of the prepared action: folder replacement, ordinary admission and upload admission each route the same prepared action; the upload reservation is released through staging, and a stage without an outcome falls back to ordinary admission after reobserving the row.
    internal async Task<SyncOutcome> DispatchPreparedAsync(SyncAction prepared, CancellationToken ct, Action? releaseOrdinaryCapacity = null,
        Action<Row, SyncOutcome>? completed = null,
        Guid? attemptId = null, ItemRetryCandidate? expectedAttempt = null, WorkSelection? selection = null)
    {
        var owned = _context.Evidence.CaptureIfHashed(prepared.Row);
        try
        {
            if (ItemReplacement.RequiresFolderReplacement(prepared))
                return await _items.Replacement.ReplaceDropboxFolderRegisteredAsync(prepared, ct, attemptId, expectedAttempt, completed, selection, null);
            await using var work = await _context.Admission.EnterWorkAsync(prepared.Kind == ActionKind.Upload, ct);
            if (prepared.Kind != ActionKind.Upload)
            {
                await using var permit = await _context.EnterOperationAsync(ct);
                return await _items.Execution.RunAdmittedAsync(prepared.Row, ct, preparedAction: prepared,
                    completed: completed, attemptId: attemptId, expectedAttempt: expectedAttempt,
                    reobserve: prepared.Kind == ActionKind.Record);
            }
            var dispatch = this;
            var uploaded = await AdmitUploadAsync<SyncOutcome?>(releaseOrdinaryCapacity,
                staged => dispatch.ExecuteUploadAsync(prepared, ct, staged, completed, attemptId, expectedAttempt), ct);
            if (uploaded is not null) return uploaded;
            _context.Admission.DisallowDirectUpload();
            await using var ordinary = await _context.EnterOperationAsync(ct);
            return await _items.Execution.RunAdmittedAsync(prepared.Row, ct, completed: completed,
                attemptId: attemptId, expectedAttempt: expectedAttempt, reobserve: true);
        }
        finally { _context.Evidence.Release(prepared.Row, owned); }
    }

    internal async Task<T> AdmitUploadAsync<T>(Action? release, Func<Func<ValueTask>, Task<T>> run,
        CancellationToken ct)
    {
        var context = _context;
        release?.Invoke();
        IAsyncDisposable? upload = await _context.Admission.AdmitUploadAsync(_context.UploadAdmissionIdentity, ct);
        try
        {
            async ValueTask Staged()
            {
                var owned = upload;
                upload = null;
                if (owned is null) return;
                context.Admission.UploadStaged();
                await owned.DisposeAsync();
            }
            return await run(Staged);
        }
        finally
        {
            if (upload is not null) await upload.DisposeAsync();
        }
    }

    private async Task<SyncOutcome?> ExecuteUploadAsync(SyncAction prepared, CancellationToken ct,
        Func<ValueTask> staged, Action<Row, SyncOutcome>? completed, Guid? attemptId, ItemRetryCandidate? expectedAttempt)
    {
        var keys = LockKeys.For(prepared, _context.Pair, null);
        await using (await PathLocks.Shared.EnterAsync(keys, ct))
        {
            (SyncOutcome? Outcome, bool Reobserve, Entry? ObservedLocal) result;
            try
            {
                result = await _items.Admission.ExecuteLockedAsync(prepared, null, keys, keys, ct, staged, null,
                    attemptId: attemptId, expectedAttempt: expectedAttempt);
            }
            catch (ChangedUnderneathException error) when (IssuePolicy.CanRetryChanged(error)) { result = (null, true, null); }
            catch (Exception error) when (ItemFailures.IsItemError(error))
            {
                // A freshness read's item error is this row's issue through the one executor mapping.
                var failed = await _executor.Failures.FailAsync(prepared.Row, prepared, error, ct,
                    attemptId: attemptId, expectedAttempt: expectedAttempt);
                _items.Finish(prepared, failed, completed);
                return failed;
            }
            if (result.Outcome is { } outcome)
            {
                _items.Finish(prepared, outcome, completed);
                return outcome;
            }
        }
        return null;
    }
}

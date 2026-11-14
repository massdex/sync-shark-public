using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Dispatches one selection branch through its attempt and fresh admission lifetimes.</summary>
internal readonly struct SelectionExecution(SelectionPass _pass, ExecutorContext _context, ItemSync _items,
    ActionExecutor _executor, ILiveExecutionBoundary _boundary)
{
    // One prepared branch chooses exactly one settlement, replacement or admitted-execution outcome.
    internal async ValueTask<(bool Ready, bool DiscardPeer, bool MayRetain)> RunAsync(WorkSelection selection, ChangeNotice notice,
        ChangeNotice? original, string key, Row row, SyncedEntry? before, SelectionPeer? peer,
        LiveEffectScope selectedScope, LocalEvidence.DirtyProof? echoEvidence, Action<Row, Issue> Retain,
        Action? release, CancellationToken ct)
    {
        var context = _context;
        var executor = _executor;
        // One owner prepares the action, its label set and its phase; this caller only sequences live admission.
        // A replaced file's wait retires before the capture, so the capture never takes a token it then retires (B-195).
        (IssueSubject Subject, ItemRetryCandidate? Expected) captured = default;
        ItemSync.LivePreparation preparation;
        try
        {
            await _items.Preparation.RetireReplacedAsync(row, ct).ConfigureAwait(false);
            captured = _items.CaptureAttempt(row);
            preparation = await _items.Preparation.PrepareLiveAsync(row, ct, echoEvidence).ConfigureAwait(false);
        }
        catch (Exception error) when (ItemFailures.IsItemError(error) && error is not ChangedUnderneathException)
        {
            var failedRow = row;
            var retain = Retain;
            var outcome = await executor.Failures.FailAsync(row, null, error, ct, issue => retain(failedRow, issue),
                expectedAttempt: captured.Expected, selection: selection).ConfigureAwait(false);
            Succeeded(outcome, selection);
            return (false, false, true);
        }
        var (subject, expected) = captured;
        var prepared = preparation.Action;
        row = prepared.Row;
        var owned = context.Evidence.CaptureIfHashed(row);
        SelectionAdmission? admission = null;
        try
        {
            // Settled before any coverage capture or lease: a held branch waits on its notice, its siblings carry on.
            if (prepared is { Kind: ActionKind.None, None: NoneReason.Held })
            {
                var holder = context.Store.HoldingIssue(selection.PairId, prepared.Row);
                _pass.RememberHeld(original, key, prepared.Row, before, holder);
                return (_pass.AttachHeld(selection, original ?? notice, prepared.Row, before, holder), false, false);
            }
            if (prepared is { Kind: ActionKind.None, None: NoneReason.Skipped or NoneReason.OutOfScope })
            {
                return (true, key == selection.Key, false);
            }
            // No file effect needs admission, but the success still closes its outstanding issue.
            if (prepared.IsInSync())
            {
                var record = prepared;
                var recordExecutor = executor;
                var recordExpected = expected;
                var recordSelection = selection;
                return (Succeeded(await context.ItemAttempts.ExecuteAsync(selection, expected, subject,
                    (attemptId, token) => recordExecutor.ExecuteAsync(record, null, token,
                        attemptId: attemptId, expectedAttempt: recordExpected, selection: recordSelection), ct)
                    .ConfigureAwait(false), selection), false, false);
            }
            if (preparation.TakeReplacementPhase)
            {
                return (Succeeded(await _items.Replacement.RunLiveReplacementAsync(preparation, selection with { Peer = peer }, subject, expected, Retain, ct)
                    .ConfigureAwait(false), selection), false, false);
            }
            admission = new SelectionAdmission(context, _items, _boundary, selection, key, peer, selectedScope,
                row, owned, preparation, expected, Retain, release);
            var outcome = await context.ItemAttempts.ExecuteAsync(selection, expected, subject, admission.RunAsync, ct)
                .ConfigureAwait(false);
            if (outcome.Deferred)
            {
                // The folder plan acquires its own footprint after the outer lease and attempt have ended.
                var planned = selection;
                var pass = _pass;
                return (await _items.Folders.RunLiveFolderPlanAsync(admission.Row, Retain,
                    selection with { Peer = peer }, admission.Coverage, admission.Footprint, ct,
                    wait => pass.RecordTemporary?.Invoke(planned, wait)).ConfigureAwait(false), false, false);
            }
            return (Succeeded(outcome, selection), false, true);
        }
        // B-195b: this refusal already paid its probes; a vanished file's wait ends here, before the next capture.
        catch (LiveAdmissionStaleException error) when (error.WaitKey is { } waitKey && (admission?.Row ?? row).Local is null)
        {
            await _items.Preparation.RetireVanishedAsync(admission?.Row ?? row, waitKey, ct).ConfigureAwait(false);
            throw;
        }
        finally
        {
            context.Evidence.Release(admission is null ? row : admission.Row, admission is null ? owned : admission.Owned);
        }
    }

    // The owning selection is recorded, not the branch: a relocated or child branch is retried by that selection.
    private bool Succeeded(SyncOutcome outcome, WorkSelection selection)
    {
        if (outcome.None == NoneReason.Unfinished) _pass.RecordTemporary?.Invoke(selection, outcome.RetryWait);
        return outcome.Succeeded();
    }
}

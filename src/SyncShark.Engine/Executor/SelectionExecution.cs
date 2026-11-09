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
        var items = _items;
        var executor = _executor;
        var pass = _pass;
        var (subject, expected) = items.CaptureAttempt(row);
        // One owner prepares the action, its label set and its phase; this caller only sequences live admission.
        ItemSync.LivePreparation preparation;
        try { preparation = await items.Preparation.PrepareLiveAsync(row, ct, echoEvidence).ConfigureAwait(false); }
        catch (Exception error) when (ItemFailures.IsItemError(error) && error is not ChangedUnderneathException)
        {
            var failedRow = row;
            var retain = Retain;
            await executor.Failures.FailAsync(row, null, error, ct, issue => retain(failedRow, issue),
                expectedAttempt: expected, selection: selection).ConfigureAwait(false);
            return (false, false, true);
        }
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
                pass.RememberHeld(original, key, prepared.Row, before, holder);
                return (pass.AttachHeld(selection, original ?? notice, prepared.Row, before, holder), false, false);
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
                return ((await context.ItemAttempts.ExecuteAsync(selection, expected, subject,
                    (attemptId, token) => recordExecutor.ExecuteAsync(record, null, token,
                        attemptId: attemptId, expectedAttempt: recordExpected, selection: recordSelection), ct)
                    .ConfigureAwait(false)).Succeeded(), false, false);
            }
            if (preparation.TakeReplacementPhase)
            {
                return ((await items.Replacement.RunLiveReplacementAsync(preparation, selection with { Peer = peer }, subject, expected, Retain, ct)
                    .ConfigureAwait(false)).Succeeded(), false, false);
            }
            admission = new SelectionAdmission(context, items, _boundary, selection, key, peer, selectedScope,
                row, owned, preparation, expected, Retain, release);
            var outcome = await context.ItemAttempts.ExecuteAsync(selection, expected, subject, admission.RunAsync, ct)
                .ConfigureAwait(false);
            if (outcome.Deferred)
            {
                // The folder plan acquires its own footprint after the outer lease and attempt have ended.
                return (await items.Folders.RunLiveFolderPlanAsync(admission.Row, Retain,
                    selection with { Peer = peer }, admission.Coverage, admission.Footprint, ct).ConfigureAwait(false), false, false);
            }
            return (outcome.Succeeded(), false, true);
        }
        finally
        {
            context.Evidence.Release(admission is null ? row : admission.Row, admission is null ? owned : admission.Owned);
        }
    }
}

using System.Runtime.ExceptionServices;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Local;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>Owns one Repair's acquisition, dependent work and source completion.</summary>
public static class Repair
{
    /// <summary>Runs a fresh Repair, retaining recovery and unfinished-item responsibility on every exit.</summary>
    public static async Task<RepairSummary> RunAsync(PairSession session, CancellationToken ct,
        IReadOnlySet<long>? retryIssues = null)
    {
        var pair = session.Pair;
        var store = session.Store;
        var log = session.Log ?? throw new ArgumentException(null, nameof(session));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = new TaskSet(cancel);
        Stage(log, TextKey.StageAdmission);
        await using var reservation = await session.Scans.EnterAsync([pair.PairId],
            () => session.Host.TryHoldGeneration(session.Registration), ct);
        log.Event(RunEvent.Stage(TextKey.StageAdmission, ended: true, count: RunLog.CompletedOperation));
        reservation.Require(session.Scans, pair.PairId);
        PairSafety.RequireRunnable(session);
        var started = session.Time.GetUtcNow();
        var firstRun = false;
        DriveEvidence? start = null;
        var blockedAtStart = false;
        var recoveredPending = false;
        string? reason = null;
        Func<RepairSummary, bool>? complete = null;
        var completed = false;
        var returning = false;
        Exception? secondary = null;
        Exception? failure = null;
        Guid? scanId = null;
        try
        {
            try
            {
                Stage(log, TextKey.StageRoots);
                var initial = await session.ReadRootsAsync(true, cancel.Token);
                log.Event(RunEvent.Stage(TextKey.StageRoots, ended: true, count: RunLog.CompletedOperation));
                start = initial.Drive;
                PairSafety.AdmitRoots(session, reservation, initial.Roots);
                if (start is not null && DriveEvidence.Change(pair.Drive, start) is { } cause)
                {
                    blockedAtStart = true;
                    store.Commit(pair.PairId, StateChange.Empty with
                    {
                        Raise = [(IssueKind.DriveChange, cause, string.Empty, new IssueSubject([], null))]
                    });
                    log.Event(RunEvent.Issue(IssueKind.DriveChange, cause, string.Empty));
                    throw new EngineRequestException(TextKey.PairAwaitingDecision, (TextArg.Pair, pair.PairId));
                }

                var outstanding = await session.Syncer.SettleOutstandingAsync(cancel.Token);
                RequireSpace(log);
                firstRun = outstanding.FirstRun;
                recoveredPending = outstanding.RecoveredPending;
                const bool includeDeleted = true;
                Stage(log, TextKey.StageScanReset);
                var remote = store.ReadSource(pair.PairId, Side.Dropbox) ?? throw Stale();
                session.Host.ValidateSourceBase(session.Registration, pair.PairId, session.ExpectedGeneration,
                    remote.Provenance, remote.Cursor);
                var baselineCount = store.SyncedCount(pair.PairId);
                var token = store.BeginScan(pair.PairId, reservation.ScanId, session.ExpectedGeneration, includeDeleted);
                scanId = token;
                if (outstanding.NeedsDeletedNames && !store.DeletedHistoryOwed(pair.PairId))
                    store.RestoreDeletedHistory(pair.PairId, token,
                        restored => log.Event(RunEvent.Stage(TextKey.StageScanReset, count: restored, inProgress: true)));
                var historyCursor = store.ContinueDeletedHistory(pair.PairId, token);
                var batchRows = session.Settings.StoreBatchRows;
                var localSink = store.OpenScan(pair.PairId, token, Side.Local, batchRows);
                IScanSink remoteSink;
                try { remoteSink = store.OpenScan(pair.PairId, token, Side.Dropbox, batchRows); }
                catch (Exception error)
                {
                    try { await localSink.AbortAsync(); }
                    catch (Exception cleanup) { log.Failed(cleanup); }
                    ExceptionDispatchInfo.Capture(error).Throw();
                    throw;
                }
                var scan = new PairScan(session.Local, session.PrerequisiteDropbox, localSink, remoteSink,
                    store, pair.PairId, token, historyCursor, batchRows);
                log.Event(RunEvent.Stage(TextKey.StageScanReset, ended: true, count: baselineCount));
                Stage(log, TextKey.StageListing);
                reason = await scan.RunAsync(tasks, log, includeDeleted, baselineCount, cancel.Token);
                if (reason is null) log.Event(RunEvent.Stage(TextKey.StageListing, ended: true, count: scan.Observations));
                if (reason is null)
                {
                    Stage(log, TextKey.StageRoots);
                    var after = await session.ReadRootsAsync(false, cancel.Token);
                    PairSafety.AdmitRoots(session, reservation, after.Roots);
                    if (scan.DeletedHistoryCursor is { } completedCursor)
                        store.SaveDeletedHistory(pair.PairId, token, completedCursor);
                    log.Event(RunEvent.Stage(TextKey.StageRoots, ended: true, count: RunLog.CompletedOperation));
                    var effectsBeforeExecution = log.Effects.Values.Sum();
                    Stage(log, TextKey.StageExecution);
                    await session.Syncer.SyncScannedAsync(token,
                        new ListedSkips(scan.LocalSkipped, scan.DropboxSkipped,
                            scan.LocalSkippedFolders, scan.DropboxSkippedFolders), retryIssues, cancel.Token);
                    RequireSpace(log);
                    log.Event(RunEvent.Stage(TextKey.StageExecution, ended: true,
                        count: log.Effects.Values.Sum() - effectsBeforeExecution));
                    store.SaveLimiterLevel(pair.ConnectionId, session.Admission.Level);
                    await tasks.WhenAllAsync();
                    cancel.Token.ThrowIfCancellationRequested();
                }
                if (reason is null)
                {
                    Stage(log, TextKey.StageCheckpoint);
                    var localCatchUp = await session.Host.CheckpointAfterRepairAsync(session.Registration,
                        pair.PairId, session.ExpectedGeneration, cancel.Token);
                    log.Event(RunEvent.Stage(TextKey.StageCheckpoint, ended: true, count: RunLog.CompletedOperation));
                    Stage(log, TextKey.StageCatchUp);
                    var remoteCatchUp = await session.Host.CatchUpDropboxAsync(session.Registration,
                        pair.PairId, session.ExpectedGeneration, cancel.Token);
                    log.Event(RunEvent.Stage(TextKey.StageCatchUp, ended: true, count: RunLog.CompletedOperation));
                    complete = summary => store.FinishRepair(pair.PairId, summary, session.ExpectedGeneration, token,
                        localCatchUp, remoteCatchUp, out secondary);
                }
            }
            catch (Exception error) { failure = error; }

            failure = await tasks.JoinAsync(failure, failure is not null || reason is not null,
                joined => log.Failed(joined), ignoreJoinedFailure: reason is not null);
            failure = ResourceFailures.Prefer(failure, Recorded(log));
            if (start is not null && !blockedAtStart)
            {
                try { store.MergeDriveEvidence(pair.PairId, session.ExpectedGeneration, start); }
                catch (Exception merge)
                {
                    if (failure is null) failure = merge;
                    else log.Failed(merge);
                }
            }
            if (failure is not null)
            {
                failure = Primary(failure);
                log.Failed(failure);
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
            Stage(log, TextKey.StageFinish);
            var summary = new RepairSummary(log.RunId, started, session.Time.GetUtcNow(), complete is not null,
                firstRun, reason, recoveredPending, log.Effects, log.IssuesByKind, log.Path, log.HasFailed);
            try
            {
                // A disk-full the log already holds, even one a Monitor write recorded, stops before any summary saves.
                RequireSpace(log);
                // Probe summary delivery without asserting durable completion.
                log.Provisional(summary);
                RequireSpace(log);
                summary = summary with { LogFailed = log.HasFailed };
                if (complete is null) store.FinishRepair(pair.PairId, summary);
                else if (!(completed = complete(summary)))
                {
                    summary = summary with { Completed = false, Superseded = true,
                        IncompleteReason = Text.Get(TextKey.RepairSuperseded) };
                    store.FinishRepair(pair.PairId, summary);
                }
            }
            catch (Exception error)
            {
                var primary = Primary(error);
                log.Failed(primary);
                if (ReferenceEquals(primary, error)) throw;
                throw primary;
            }
            // After the authoritative commit every later failure is secondary; it never revokes completion.
            if (secondary is not null) log.Failed(secondary, TextKey.RepairCompletedSecondaryFailure);
            try
            {
                if (scanId is { } finishedScan)
                {
                    scanId = null;
                    try { await store.EndScanAsync(pair.PairId, finishedScan, session.Settings.StoragePageSize).ConfigureAwait(false); }
                    catch (Exception cleanup) { log.Failed(cleanup); }
                }
                log.FinishRepair(summary);
                if (summary.Completed && Path.GetDirectoryName(log.Path) is { } logs)
                {
                    try { LogFiles.Prune(logs, TimeSpan.FromDays(session.Settings.LogRetentionDays), summary.Finished); }
                    catch (OverflowException error) { log.Failed(error); }
                }
                if (summary.LogFailed != log.HasFailed)
                {
                    summary = summary with { LogFailed = log.HasFailed };
                    // Diagnostic persistence cannot revoke the already committed Repair outcome.
                    try { store.MarkRepairLogFailed(pair.PairId, summary.RunId); }
                    catch (Exception error) { log.Failed(error); }
                }
            }
            catch (Exception error) when (completed) { log.Failed(error, TextKey.RepairCompletedSecondaryFailure); }
            catch (Exception error) { log.Failed(error); throw; }
            returning = true;
            return summary;
        }
        finally
        {
            // A true completion already installed owed history in its own transaction.
            try { if (!completed) store.RequireRepairForOwedHistory(pair.PairId, session.ExpectedGeneration); }
            // An earlier primary in flight keeps priority; the failed handoff is still reported.
            catch (Exception handoff) when (!returning) { log.Failed(handoff); }
            finally
            {
                if (scanId is { } token)
                    try { await store.EndScanAsync(pair.PairId, token, session.Settings.StoragePageSize).ConfigureAwait(false); }
                    catch (Exception cleanup) { log.Failed(cleanup); }
            }
        }
    }

    private static LiveAdmissionStaleException Stale() => new(LiveAdmissionStaleReason.CoverageChanged);

    // A disk-full a result-only route recorded on this Repair, or the log's own first file failure when it was one.
    private static Exception? Recorded(RunLog log) => RepairScope.Recorded ?? ResourceFailures.Find(log.Failure);

    private static void RequireSpace(RunLog log)
    {
        if (Recorded(log) is { } resource) ExceptionDispatchInfo.Capture(Primary(resource)).Throw();
    }

    // A disk-full primary carries the resource wording; the original failure stays inner.
    private static Exception Primary(Exception failure) =>
        failure is InsufficientSpaceException || ResourceFailures.Find(failure) is null
            ? failure : new InsufficientSpaceException(failure);
    private static void Stage(RunLog log, TextKey key) =>
        log.Event(RunEvent.Stage(key));
}

using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Resolves one notice (section 7.9, D29): plans the choice, completes any folder replacement through the
/// folder owner outside this scope's admission, executes the root actions under the caller's selected lease and
/// closes the issue, then syncs its independent follow-up paths.</summary>
internal sealed class ResolutionRunner(
    ExecutorContext context, ActionExecutor executor, KeepBothRunner keepBoth,
    ItemSync items, ISelectedExecutionBoundary selectedBoundary,
    Func<WorkSelection?, Side, string, IReadOnlySet<long>?, bool, CancellationToken, Task>? runBatch = null)
{
    /// <summary>True once this resolution closed its notice, or dismissed it.</summary>
    internal bool Resolved { get; private set; }

    internal async Task<SyncOutcome> ResolveAdmittedAsync(IAdmittedExecution execution,
        Issue expected, IReadOnlyList<SyncAction> actions, KeepBothLabels? labels, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var outcome = SyncOutcome.InSync;
        foreach (var action in actions)
        {
            outcome = await items.ExecuteSelectedAsync(execution, action, labels, ct);
            if (!outcome.Succeeded()) return outcome;
        }
        Close(expected);
        return outcome;
    }

    /// <summary>Closes a successful root choice before syncing its independent follow-up paths.</summary>
    public async Task<SyncOutcome> ResolveAsync(Issue issue, ResolutionChoice choice, string? newName,
        IReadOnlyList<SyncIgnoreRule>? keepRules, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (choice == ResolutionChoice.Dismiss)
        {
            Resolutions.For(issue, choice);
            context.Store.DismissIssue(context.Pair.PairId, issue.Id);
            Resolved = true;
            return SyncOutcome.InSync;
        }
        if (choice == ResolutionChoice.KeepFolderAlways)
            return await KeepFolderAlwaysAsync(issue, keepRules, ct);
        if (choice == ResolutionChoice.Retry)
        {
            if (!Resolutions.Allows(issue.Kind, issue.Cause, choice))
                throw new EngineRequestException(TextKey.ResolveBadChoice);
            if (context.Store.IssueById(context.Pair.PairId, issue.Id) is null)
            {
                Resolved = true;
                return SyncOutcome.InSync;
            }
            var retried = SyncOutcome.InSync;
            IReadOnlySet<long> retryIssues = new HashSet<long> { issue.Id };
            if (issue.Subject.Items.Count == 0)
                retried = await SyncFollowUpAsync(issue.Subject.Incoming ??
                    (issue.Cause == TextKey.ReadDropboxFailed ? Side.Dropbox : Side.Local),
                    issue.Path, issue, false, ct, retryIssues);
            foreach (var item in issue.Subject.Items)
                retried = FollowUpOutcome(retried, await SyncFollowUpAsync(item.Side, item.Path, issue, false, ct, retryIssues));
            Resolved = context.Store.IssueById(context.Pair.PairId, issue.Id) is null;
            return retried;
        }

        (Side Side, Entry Entry)? folder = null;
        IReadOnlyList<(Side Side, string Path)> paths = [];
        var outcome = SyncOutcome.InSync;
        try
        {
            var (actions, labels) = await PlanAsync(issue, choice, newName, ct);
            if (actions.Any(action => RemovesFolder(action) || action.Kind == ActionKind.ReplaceKind &&
                action.Row.EntryOn(action.WrittenSide()) is { IsFolder: true }))
                foreach (var action in actions)
                    if (context.Store.HeldByOtherIssue(context.Pair.PairId, action.Row, issue.Id))
                        return await executor.ExecuteAsync(Decide.Held(action.Row), null, ct);

            // A Dropbox folder replacement owns its own folder phase and admission: complete it before this scope
            // takes a lease, and never replay the prepared action.
            if (actions is [var replacement] && ItemSync.RequiresFolderReplacement(replacement))
            {
                var removed = await items.Folders.RemoveSelectedReplacementAsync(replacement.Row, ct);
                var (fresh, failed) = await items.AfterReplacementRemovalAsync(removed, replacement.Row, replacement, ct, null, null, null, null);
                if (failed is not null) return failed;
                var remaining = await items.PrepareAsync(fresh, ct);
                return await SelectedAsync(issue, [remaining], labels, ct);
            }
            // A same-kind folder replacement removes a whole folder: only a Complete folder operation authorizes
            // the remaining root action.
            outcome = FolderRemoval(actions) is { } removal
                ? await RemoveFolderAsync(issue, removal, actions, labels, ct)
                : await SelectedAsync(issue, actions, labels, ct);
            if (!outcome.Succeeded()) return outcome;
            foreach (var action in actions)
            {
                folder = Resolutions.FolderToSync(action, folder);
                var followUps = Resolutions.PathsToSync(action);
                if (followUps.Count > 0) paths = followUps;
            }
        }
        catch (Exception error) when (ActionExecutor.IsItemError(error))
        {
            return await executor.FailAsync(Unobserved(issue.Path), null, error, ct);
        }

        // Follow-ups acquire their own admission and locks; the successful root is already durable.
        if (folder is { } incoming)
            outcome = await SyncDescendantsAsync(incoming.Side, incoming.Entry.Path, issue, outcome, ct);
        foreach (var path in paths)
        {
            var followed = await SyncFollowUpAsync(path.Side, path.Path, issue, true, ct);
            outcome = FollowUpOutcome(outcome, followed);
        }
        return outcome;
    }

    // The keep-folder choice has no sync action: its one atomic transaction validates the exact issue and
    // generation and unions the supplied rules, so the ordinary loop must not close the notice instead.
    private Task<SyncOutcome> KeepFolderAlwaysAsync(Issue issue,
        IReadOnlyList<SyncIgnoreRule>? keepRules, CancellationToken ct)
    {
        Resolutions.For(issue, ResolutionChoice.KeepFolderAlways);
        var rules = keepRules ?? throw new EngineRequestException(TextKey.ResolveBadChoice);
        return selectedBoundary.ExecuteAsync(issue, [],
            (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                context.Store.KeepFolderAlways(context.Pair.PairId, context.ExpectedGeneration, issue, rules);
                Resolved = true;
                return Task.FromResult(SyncOutcome.InSync);
            }, ct);
    }

    // The folder owner takes its own admission and item locks, so this runs only outside the selected lease; an
    // absent or non-Complete result is not success and never authorizes the replacement.
    private async Task<SyncOutcome> RemoveFolderAsync(Issue issue, SyncAction action,
        IReadOnlyList<SyncAction> actions, KeepBothLabels? labels, CancellationToken ct)
    {
        var removed = await items.Folders.RemoveChosenAsync(action, actions, ct);
        if (await items.ReplacementRemovalFailureAsync(removed, action.Row, action, ct, null, null, null, null) is { } failed)
            return failed;
        // The occupant is gone, so the remaining root action runs on what is left and closes the still-open notice.
        return await SelectedAsync(issue, Resolutions.AfterRemoval(actions, action), labels, ct);
    }

    private Task<SyncOutcome> SelectedAsync(Issue issue, IReadOnlyList<SyncAction> actions, KeepBothLabels? labels,
        CancellationToken ct) => selectedBoundary.ExecuteAsync(issue, Footprint(actions, labels),
            (execution, token) => ResolveAdmittedAsync(execution, issue, actions, labels, token), ct);

    private async Task<SyncOutcome> SyncDescendantsAsync(Side side, string path, Issue issue,
        SyncOutcome outcome, CancellationToken ct)
    {
        PrefixSet? failedFolders = null;
        try
        {
            await using var listing = context.For(side).ReadAllAsync(path, new ListOptions(true, false), ct)
                .GetAsyncEnumerator(ct);
            while (true)
            {
                // Advancing may fetch another page; children must never inherit its admission permit.
                await using (var permit = side == Side.Dropbox
                    ? await context.EnterOperationAsync(ct) : null)
                {
                    if (!await listing.MoveNextAsync()) break;
                }
                var listed = listing.Current;
                if (listed.Entry is not { Skip: null } entry) continue;
                var key = failedFolders is null ? null : PathNames.Key(listed.Path);
                if (key is not null && failedFolders!.Covers(key)) continue;
                var child = await SyncFollowUpAsync(side, listed.Path, issue, false, ct);
                if (child.Succeeded()) continue;
                outcome = FollowUpOutcome(outcome, child);
                if (entry.IsFolder) (failedFolders ??= new PrefixSet()).Add(key ?? PathNames.Key(listed.Path));
            }
            return outcome;
        }
        catch (Exception error) when (error is InventoryIncompleteException || ActionExecutor.IsItemError(error))
        {
            return await FollowUpFailureAsync(side, path, error, ct);
        }
    }

    private async Task<SyncOutcome> SyncFollowUpAsync(Side side, string path, Issue issue,
        bool syncDescendants, CancellationToken ct, IReadOnlySet<long>? retryIssues = null)
    {
        try
        {
            Row? completed = null;
            var outcome = await items.SyncPathAsync(side, path, ct, retryIssues,
                retryIssues is null ? null : (row, _) => completed = row);
            if (retryIssues is not null && outcome.Raised != IssueKind.Failure &&
                completed is { } observed && observed.IsFolder() && runBatch is not null &&
                SyncPorts.AdmitsBatch(context.Store, context.Pair.PairId))
            {
                var retries = context.Store.OpenIssuesUnder(context.Pair.PairId, PathNames.KeyFor(side, path),
                    [IssueKind.Failure]).Select(notice => notice.Id).ToHashSet();
                retries.UnionWith(retryIssues);
                await runBatch(null, side, path, retries, true, ct);
                if (context.Store.OpenIssuesUnder(context.Pair.PairId, PathNames.KeyFor(side, path),
                    [IssueKind.Failure]).FirstOrDefault() is { } failure)
                    return new SyncOutcome(ActionKind.Notice, null, null, IssueKind.Failure, false, failure.Cause);
                return SyncOutcome.InSync;
            }
            if (!syncDescendants || !outcome.Succeeded()) return outcome;
            Entry? entry;
            await using (var permit = side == Side.Dropbox
                ? await context.EnterOperationAsync(ct) : null)
            {
                entry = await context.For(side).ReadOneAsync(path, ct);
            }
            return entry is { IsFolder: true, Skip: null }
                ? await SyncDescendantsAsync(side, entry.Path, issue, outcome, ct) : outcome;
        }
        catch (Exception error) when (error is InventoryIncompleteException || ActionExecutor.IsItemError(error))
        {
            return await FollowUpFailureAsync(side, path, error, ct);
        }
    }

    private Task<SyncOutcome> FollowUpFailureAsync(Side side, string path, Exception error, CancellationToken ct) =>
        executor.FailAsync(Unobserved(path), null, error is InventoryIncompleteException incomplete
            ? ActionExecutor.InventoryFailure(incomplete, path, side) : error, ct);

    private static Row Unobserved(string path) =>
        Row.ForOneSide(Side.Local, path, null, null, [], null, RowContext.Neutral);

    private async Task<(IReadOnlyList<SyncAction> Actions, KeepBothLabels? Labels)> PlanAsync(
        Issue issue, ResolutionChoice choice, string? newName, CancellationToken ct)
    {
        var local = new List<Entry>();
        var dropbox = new List<Entry>();
        foreach (var item in issue.Subject.Items)
        {
            var entry = await context.For(item.Side).ReadOneAsync(item.Path, ct);
            if (entry is not null) (item.Side == Side.Local ? local : dropbox).Add(entry);
        }
        var actions = Resolutions.For(issue, choice, newName,
            ObservedRow(Side.Local, local, issue),
            ObservedRow(Side.Dropbox, dropbox, issue) with { Local = local.Count > 1 ? local[1] : null });
        var action = actions.FirstOrDefault(planned => planned.Kind == ActionKind.KeepBoth);
        var labels = action is null ? null : await keepBoth.ChooseLabelsAsync(action.Row, ct);
        return (actions, labels);
    }

    private Row ObservedRow(Side side, List<Entry> entries, Issue issue)
    {
        var entry = entries.FirstOrDefault();
        var path = entry?.Path ?? issue.Subject.Items.FirstOrDefault(item => item.Side == side)?.Path ?? issue.Path;
        var key = PathNames.KeyFor(side, path);
        var synced = context.Store.SyncedByKey(context.Pair.PairId, side, key);
        return Row.ForOneSide(side, key, entry, synced,
            side == Side.Dropbox && entries.Count > 1 ? entries[1..] : [], null,
            RowContext.Neutral);
    }

    // The selected lease converts this footprint to lock keys; LockKeys owns the action-path projection.
    private IReadOnlyList<LiveEffectScope> Footprint(IReadOnlyList<SyncAction> actions, KeepBothLabels? labels) =>
        [.. actions.SelectMany(action => LockKeys.ForLiveAction(action, context.Pair, labels))];

    private static SyncAction? FolderRemoval(IReadOnlyList<SyncAction> actions) => actions.FirstOrDefault(RemovesFolder);

    private static bool RemovesFolder(SyncAction action) =>
        action.Kind is ActionKind.DeleteLocal or ActionKind.DeleteDropbox &&
        action.Row.EntryOn(action.WrittenSide()) is { IsFolder: true };

    private static SyncOutcome FollowUpOutcome(SyncOutcome previous, SyncOutcome next) =>
        previous.Raised != IssueKind.Failure &&
        (next.Raised == IssueKind.Failure || previous.Succeeded() && !next.Succeeded()) ? next : previous;

    // A resolution settles at most the exact local items its selected outcome acted on; a folder parent carrying
    // unscanned children keeps its obligation. The separate first-run Failure may close only after that
    // acknowledgement, and its own lifecycle guards still refuse a retained row.
    private void Close(Issue issue)
    {
        IssueItem[] acknowledged = [];
        if (issue.Subject.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.Item)
            acknowledged = issue.Subject.Items.Where(item => item.Side == Side.Local).ToArray();
        IReadOnlyList<IssueKind> kinds = acknowledged.Length > 0
            ? new[] { issue.Kind, IssueKind.Failure } : new[] { issue.Kind };
        context.Store.Commit(context.Pair.PairId, StateChange.Empty with
        {
            Close = [(issue.Path, kinds)],
            FirstRunAcknowledged = acknowledged
        });
        Resolved = true;
    }
}

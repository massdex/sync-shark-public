using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Gathers rename observations, decides with Renames.For and applies the decision (section 7.4).</summary>
internal sealed class RenameRunner(ExecutorContext context, Parents parents, RowContexts rows, AncestorFence ancestors,
    bool completedScan = false)
{
    internal Task<RenameDecision> RunAdmittedAsync(IAdmittedExecution execution, WorkSelection? selection, object opportunity,
        IReadOnlyList<RenameCandidate> group, Entry? localById,
        Action<Issue>? retainUnfinished, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(execution);
        return RunCoreAsync(execution, selection, group, localById, retainUnfinished, opportunity, ct);
    }

    /// <summary>Owns ordinary path holds, or borrows an admitted footprint without reacquiring it.</summary>
    public Task<RenameDecision> RunAsync(IReadOnlyList<RenameCandidate> group, Entry? localById, Action<Issue>? retainUnfinished, CancellationToken ct) =>
        RunCoreAsync(null, null, group, localById, retainUnfinished, context.Opportunity, ct);

    private async Task<RenameDecision> RunCoreAsync(IAdmittedExecution? execution, WorkSelection? selection,
        IReadOnlyList<RenameCandidate> group, Entry? localById,
        Action<Issue>? retainUnfinished, object opportunity, CancellationToken ct)
    {
        var decision = await RunUnrecordedAsync(execution, selection, group, localById,
            retainUnfinished, opportunity, ct);
        if (decision.Kind is RenameKind.Hold or RenameKind.Notice)
            rows.HoldRenamePaths(Paths(group).Select(path => (path.Side, path.Path)));
        return decision;
    }

    // The lock re-acquire loop, binding resolution and retry lookup are one admission preparation sharing its held-path state.
    private async Task<RenameDecision> RunUnrecordedAsync(IAdmittedExecution? execution, WorkSelection? selection,
        IReadOnlyList<RenameCandidate> group, Entry? localById,
        Action<Issue>? retainUnfinished, object opportunity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(opportunity);
        var reacquired = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var heldPaths = Paths(group).ToArray();
            var keys = LockKeys.ForPaths(context.Pair, LockPaths(heldPaths, localById)).ToArray();
            execution?.Require(context.Pair.PairId, context.ExpectedGeneration, keys);
            // Permit precedes path: listing-only returns NeedsFresh, releasing this hold before caller admission.
            await using var hold = execution is null ? await PathLocks.Shared.EnterAsync(keys, ct) : null;
            var first = group[0];
            var synced = context.Store.SyncedByKey(context.Pair.PairId, Side.Local, first.Synced.LocalKey);
            if (synced?.IdOn(first.Side) != first.Moved.Id)
                synced = context.Store.SyncedById(context.Pair.PairId, first.Side, first.Moved.Id);
            if (synced is null) return Renames.None();
            var current = group.Where(candidate => synced.PathOn(candidate.Side) != candidate.Moved.Path)
                .Select(candidate => candidate with { Synced = synced }).ToArray();
            if (current.Length == 0) return Renames.None();
            var localSubject = Renames.FirstOn(current, Side.Local)?.Moved ?? localById;
            var subjectItems = current.Where(candidate => candidate.Side == Side.Dropbox)
                .Select(candidate => new IssueItem(Side.Dropbox, candidate.Moved.Path, candidate.Moved.Id)).ToList();
            // A Dropbox-origin rename still names the retained Local endpoint from its saved binding, so a Local
            // in-use refusal is registered on that file's own path, identity and epoch.
            if (localSubject is not null)
                subjectItems.Add(new IssueItem(Side.Local, localSubject.Path, localSubject.Id));
            else if (synced.LocalId.Length != 0 && synced.LocalPath.Length != 0)
                subjectItems.Add(new IssueItem(Side.Local, synced.LocalPath, synced.LocalId));
            var subject = IssueSubject.FromDistinct(subjectItems, null);
            var retryPath = localSubject?.Path ?? (synced.LocalId.Length != 0 ? synced.LocalPath : null);
            var expected = retryPath is null ? null : context.Store.ReadItemRetryForSubject(
                context.Pair.PairId, context.ExpectedGeneration, retryPath, subject);
            var paths = Paths(current).ToArray();
            if (!heldPaths.SequenceEqual(paths))
            {
                var required = LockKeys.ForPaths(context.Pair, LockPaths(paths, localById)).ToArray();
                if (execution is not null)
                {
                    execution.Require(context.Pair.PairId, context.ExpectedGeneration, required);
                    keys = required;
                }
                else if (required.Any(key => !keys.Contains(key, PathLocks.KeyComparer)))
                {
                    if (IssuePolicy.StaleRetryFailed(reacquired, false, false))
                    {
                        return await context.ItemAttempts.ExecuteAsync(opportunity, expected, subject,
                            (attempt, _) => Task.FromResult(Failure(current, null, new ChangedUnderneathException(synced.LocalPath),
                                retainUnfinished, attempt, expected, subject, selection)), ct);
                    }
                    group = current;
                    reacquired = true;
                    continue;
                }
            }
            var result = await context.ItemAttempts.ExecuteAsync(opportunity, expected, subject,
                (attempt, token) => RunCurrentAsync(execution, selection, current, synced, localById, paths, keys,
                    (refreshed, hint) =>
                    {
                        if (IssuePolicy.StaleRetryFailed(reacquired, false, false))
                            throw new ChangedUnderneathException(synced.LocalPath);
                        group = refreshed;
                        localById = hint;
                        reacquired = true;
                    }, retainUnfinished, attempt, expected, subject, token), ct);
            if (!completedScan && result.Kind == RenameKind.NeedsFresh) continue;
            return result;
        }
    }

    // Observation, decision, provider effect and clash or retry settlement share one set of locals and one item-attempt lifetime.
    private async Task<RenameDecision> RunCurrentAsync(IAdmittedExecution? execution, WorkSelection? selection,
        IReadOnlyList<RenameCandidate> current, SyncedEntry synced,
        Entry? localById, IReadOnlyList<(Side Side, string Path, bool Destination)> paths, IReadOnlyList<string> heldKeys,
        Action<IReadOnlyList<RenameCandidate>, Entry?> reacquire,
        Action<Issue>? retainUnfinished, Guid attempt, ItemRetryCandidate? expected,
        IssueSubject registeredSubject, CancellationToken ct)
    {
        var callbackFailed = false;
        var notification = retainUnfinished;
        if (notification is not null) retainUnfinished = issue =>
        {
            try { notification(issue); }
            catch { callbackFailed = true; throw; }
        };
        Observations? observations = null;
        RenameDecision? decision = null;
        try
        {
            // A rename can rebase protected bindings even when its provider paths are readable.
            var roots = paths.Where(item => item.Side == Side.Local)
                .Select(item => (item.Side, PathNames.Key(item.Path))).ToList();
            if (localById is not null) roots.Add((Side.Local, PathNames.Key(localById.Path)));
            roots.Add((Side.Local, synced.LocalKey));
            roots.Add((Side.Dropbox, synced.DropboxKey));
            ActionExecutor.RequireReadable(context.Store, context.Pair.PairId, synced.LocalPath, roots, synced.IsFolder, ct);
            var listed = ListedAdoption(current, synced, localById, paths);
            if (completedScan && listed is null) return new RenameDecision(RenameKind.NeedsFresh);
            if (listed is not null) observations = listed.Value.Observations;
            else
            {
                var refreshed = await ObserveAsync(current, synced, localById, ct);
                current = refreshed.Group;
                localById = refreshed.LocalById;
                if (current.Count == 0) return Renames.None();
                paths = Paths(current).ToArray();
                observations = Observe(current, synced, paths, refreshed.Local, refreshed.Dropbox);
                var required = LockKeys.ForPaths(context.Pair, LockPaths(paths, localById)).ToArray();
                if (required.Any(key => !heldKeys.Contains(key, PathLocks.KeyComparer)))
                {
                    if (execution is not null) execution.Require(context.Pair.PairId, context.ExpectedGeneration, required);
                    else
                    {
                        reacquire(current, localById);
                        return new RenameDecision(RenameKind.NeedsFresh);
                    }
                }
            }
            var evidence = listed is null && observations.LocalAtSynced is { IsFolder: false } observed
                ? context.Evidence.Read(observed, synced) : default;
            decision = listed?.Decision ?? Renames.For(current, localById, observations,
                context.Settings.CaseRenameSuffix, context.Time.GetUtcNow(), evidence.Dirty);
            // Hash only the uncertain local counterpart, never a held item or case-rename recovery.
            if (Renames.NeedsLocalHash(decision, synced, observations.LocalAtSynced, evidence.Dirty))
            {
                var hashed = await context.Evidence.HashAsync(context.Local, observations.LocalAtSynced!, synced, evidence, ct);
                observations = observations with { LocalHash = hashed.Hash };
                decision = Renames.For(current, localById, observations, context.Settings.CaseRenameSuffix,
                    context.Time.GetUtcNow(), evidence.Dirty);
            }
            ItemSync.RequireLiveWrite(context, decision.WrittenSide() == Side.Local);
            if (decision.WrittenSide() is { } written)
            {
                var row = new Row(PathNames.KeyFor(current[0].Side, current[0].Moved.Path),
                    Renames.FirstOn(current, Side.Local)?.Moved ?? observations.LocalAtSynced,
                    Renames.FirstOn(current, Side.Dropbox)?.Moved ?? observations.DropboxAtSynced,
                    synced, [], observations.LocalHash, RowContext.Neutral);
                if (!ancestors.AncestorsClear(row, decision.Target, written)) return Renames.None();
            }
            await ApplyAsync(selection, decision, current, synced, observations, retainUnfinished, attempt, expected, ct);
            return decision;
        }
        catch (PathOccupiedException error) when (!callbackFailed && decision is { Kind: RenameKind.Follow or RenameKind.Adopt })
        {
            var written = error.CollidingSide ?? decision.WrittenSide() ?? Side.Local;
            Entry? occupant;
            try
            {
                if (completedScan) return new RenameDecision(RenameKind.NeedsFresh);
                occupant = await context.For(written).ReadOneAsync(error.Path, ct);
            }
            catch (Exception readError) when (readError is ChangedUnderneathException or ItemFailedException)
            {
                return Failure(current, observations, readError, retainUnfinished, attempt, expected, registeredSubject, selection);
            }
            var subject = Subject(current, observations, decision.MovedSide);
            if (occupant is not null)
                subject = IssueSubject.FromDistinct(
                    subject.Items.Append(new IssueItem(written, occupant.Path, occupant.Id)), subject.Incoming);
            var change = StateChange.Empty with { Raise = [(IssueKind.NameClash, TextKey.ClashNameTaken, error.Path, subject)] };
            ActionExecutor.Settle(context.Store, context.Pair.PairId, context.ItemAttempts, change,
                attempt, expected, selection, retainUnfinished);
            ActionExecutor.EmitIssues(context.Events, change);
            return new RenameDecision(RenameKind.Hold);
        }
        catch (ChangedUnderneathException) when (completedScan)
        {
            return new RenameDecision(RenameKind.NeedsFresh);
        }
        catch (Exception error) when (!callbackFailed && ActionExecutor.IsItemError(error))
        {
            return Failure(current, observations, error, retainUnfinished, attempt, expected, registeredSubject, selection);
        }
    }

    private (Observations Observations, RenameDecision Decision)? ListedAdoption(IReadOnlyList<RenameCandidate> group, SyncedEntry synced,
        Entry? localById, IReadOnlyList<(Side Side, string Path, bool Destination)> paths)
    {
        if (!completedScan || context.ScanId == Guid.Empty) return null;
        var local = Renames.FirstOn(group, Side.Local)?.Moved;
        var dropbox = Renames.FirstOn(group, Side.Dropbox)?.Moved;
        local = context.Store.ScanEntry(context.Pair.PairId, context.ScanId, Side.Local,
            local is null ? synced.LocalKey : PathNames.KeyFor(Side.Local, local.Path));
        dropbox = context.Store.ScanEntry(context.Pair.PairId, context.ScanId, Side.Dropbox,
            dropbox is null ? synced.DropboxKey : PathNames.KeyFor(Side.Dropbox, dropbox.Path));
        if (!Matches(local, synced, Side.Local) || !Matches(dropbox, synced, Side.Dropbox) ||
            group.Any(candidate => candidate.Moved != (candidate.Side == Side.Local ? local : dropbox))) return null;
        var observations = Observe(group, synced, paths, local, dropbox);
        // Listing evidence may relink history, but never authorizes a provider effect.
        var decision = Renames.For(group, localById, observations, context.Settings.CaseRenameSuffix,
            context.Time.GetUtcNow(), false);
        return decision.Kind == RenameKind.Adopt ? (observations, decision) : null;
    }

    private async Task<(IReadOnlyList<RenameCandidate> Group, Entry? LocalById, Entry? Local, Entry? Dropbox)> ObserveAsync(
        IReadOnlyList<RenameCandidate> group, SyncedEntry synced, Entry? localById, CancellationToken ct)
    {
        var local = await context.Local.ReadOneAsync(synced.LocalPath, ct);
        var dropbox = await context.Dropbox.ReadOneAsync(synced.DropboxPath, ct);
        var current = new List<RenameCandidate>(group.Count);
        foreach (var candidate in group)
        {
            var saved = candidate.Side == Side.Local ? local : dropbox;
            // Point reads preserve requested ancestor case; the saved address owns that spelling.
            var moved = PathNames.Same(candidate.Moved.Path, synced.PathOn(candidate.Side)) ? saved
                : await context.For(candidate.Side).ReadOneAsync(candidate.Moved.Path, ct);
            if (!Matches(moved, synced, candidate.Side))
            {
                if (Matches(saved, synced, candidate.Side)) continue;
                throw new ChangedUnderneathException(candidate.Moved.Path);
            }
            if (Renames.Candidate(candidate.Side, moved, synced) is { } refreshed) current.Add(refreshed);
        }
        if (current.Count != 0 && localById is not null)
        {
            var moved = Renames.FirstOn(current, Side.Local)?.Moved;
            localById = moved ?? (Matches(local, synced, Side.Local) ? local
                : await context.Local.ReadOneAsync(localById.Path, ct));
            if (!Matches(localById, synced, Side.Local))
                throw new ChangedUnderneathException(synced.LocalPath);
        }
        return (current, localById, local, dropbox);
    }

    private static bool Matches(Entry? entry, SyncedEntry synced, Side side) =>
        entry is not null && entry.Id.Length != 0 && entry.Id == synced.IdOn(side) && entry.IsFolder == synced.IsFolder;

    private Observations Observe(IReadOnlyList<RenameCandidate> group, SyncedEntry synced,
        IReadOnlyList<(Side Side, string Path, bool Destination)> paths, Entry? local, Entry? dropbox)
    {
        var skipped = local?.Skip is not null || dropbox?.Skip is not null || group.Any(candidate => candidate.Moved.Skip is not null);
        var held = Holds(paths);
        var newLocal = Renames.FirstOn(group, Side.Local)?.Moved.Path;
        var newDropbox = Renames.FirstOn(group, Side.Dropbox)?.Moved.Path;
        var localDestination = MappedDestination(paths, Side.Local);
        var dropboxDestination = MappedDestination(paths, Side.Dropbox);
        var outOfScope = context.Scope.LeavesScope(newLocal ?? localDestination,
            newDropbox ?? dropboxDestination, synced.IsFolder, context,
            static (owner, side, key) => owner.Store.SyncedByKey(owner.Pair.PairId, side, key));
        var carries = synced.IsFolder && paths.Any(item => context.Scope.HoldsNestedRoot(item.Side, item.Path));

        return new Observations(local, dropbox, skipped, held, outOfScope, carries, null,
            localDestination, dropboxDestination);
    }

    // One apply lifetime: every RenameKind branch shares the Save closure, the success flag and the one Dropbox
    // admission completion released in the finally; Storage owns folder and scan rebasing.
    private async Task ApplyAsync(WorkSelection? selection, RenameDecision decision, IReadOnlyList<RenameCandidate> group, SyncedEntry synced,
        Observations observations, Action<Issue>? retainUnfinished, Guid attempt,
        ItemRetryCandidate? expected, CancellationToken ct)
    {
        async Task Save(StateChange change, EffectKind? effect = null, Side? side = null,
            string path = "", string detail = "")
        {
            if (decision.Kind is RenameKind.Adopt or RenameKind.Follow or RenameKind.FinishCaseRename)
                change = change with { FirstRunAcknowledged =
                    [new IssueItem(Side.Local, synced.LocalPath, synced.LocalId)] };
            if (completedScan && decision.Kind == RenameKind.Adopt && !synced.IsFolder &&
                expected is null && selection is null && change.Renames.Count != 0)
                await context.Store.CommitAsync(context.Pair.PairId, change);
            else
                ActionExecutor.Settle(context.Store, context.Pair.PairId, context.ItemAttempts, change,
                    attempt, expected, selection, retainUnfinished);
            if (effect is { } kind && side is { } written)
                context.Events(RunEvent.Effect(kind, path, detail, written));
            ActionExecutor.EmitIssues(context.Events, change);
        }

        var writtenSide = decision.WrittenSide();
        var touchesDropbox = writtenSide == Side.Dropbox;
        var success = false;
        try
        {
            var path = group[0].Moved.Path;
            switch (decision.Kind)
            {
                case RenameKind.None:
                case RenameKind.Hold:
                    return;
                case RenameKind.Notice:
                    await Save(StateChange.Empty with { Raise = [(decision.NoticeKind!.Value, decision.Cause!.Value,
                        path, Subject(group, observations, null))] });
                    return;
                case RenameKind.Leave:
                    return;
                case RenameKind.Split:
                    await Save(StateChange.Empty with
                    {
                        ForgetSelf = [(Side.Local, synced.LocalKey)],
                        Raise = [(decision.NoticeKind!.Value, decision.Cause!.Value, path,
                            Subject(group, observations, decision.MovedSide))]
                    });
                    return;
                case RenameKind.Adopt:
                    var adopted = SyncedEntries.Relink(synced,
                        Renames.FirstOn(group, Side.Local)?.Moved ?? observations.LocalAtSynced!,
                        Renames.FirstOn(group, Side.Dropbox)?.Moved ?? observations.DropboxAtSynced!);
                    var destination = context.Store.SyncedByKey(context.Pair.PairId, Side.Local, adopted.LocalKey);
                    if (destination is not null && destination.IsFolder == adopted.IsFolder && destination.LocalKey == adopted.LocalKey &&
                        destination.DropboxKey == adopted.DropboxKey && destination.LocalId == adopted.LocalId &&
                        destination.DropboxId == adopted.DropboxId && destination.IsRelocationOf(synced))
                    {
                        // Retirement probes old endpoints; return through the ordinary permit-before-path handoff.
                        if (completedScan) throw new ChangedUnderneathException(synced.LocalPath);
                        var retirement = await RecordBindings.PrepareAsync(context, StateChange.Empty with
                        { Record = [destination], Close = Closes(synced, group) }, ct,
                            LockKeys.ForPaths(context.Pair, Paths(group).Select(item => (item.Side, item.Path))));
                        if (retirement.StaleBindings.Contains(synced))
                        {
                            // Retire only the proven-absent root; descendants keep their independent work.
                            await Save(retirement);
                            return;
                        }
                    }
                    await Save(StateChange.Empty with
                    {
                        Renames = [new RenameApplied(synced, adopted, null, null, null)],
                        Close = Closes(synced, group)
                    });
                    return;
                case RenameKind.FinishCaseRename:
                case RenameKind.Follow:
                    var finishing = decision.Kind == RenameKind.FinishCaseRename;
                    var movedSide = finishing ? Side.Local : decision.MovedSide!.Value;
                    var written = writtenSide!.Value;
                    var counterpart = Renames.FirstOn(group, written)?.Moved ??
                        observations.AtSynced(written) ??
                        throw new ChangedUnderneathException(synced.PathOn(written));
                    var moved = finishing ? null : Renames.RequireOn(group, movedSide).Moved;
                    path = decision.Target!;
                    if (!finishing) await parents.EnsureAsync(written, path, ct);
                    ItemSync.RequireLiveWrite(context, written == Side.Local);
                    var result = await context.For(written).MoveAsync(counterpart, path, ct);
                    var after = finishing ? synced with
                    {
                        DropboxPath = result.Path, DropboxKey = PathNames.DropboxKey(result.Path), DropboxId = result.Id
                    } : written == Side.Local ? SyncedEntries.Relink(synced, result, moved!) : SyncedEntries.Relink(synced, moved!, result);
                    await Save(StateChange.Empty with
                    {
                        Renames = [new RenameApplied(synced, after, written, counterpart.Path, result.Path)],
                        Close = Closes(synced, group)
                    }, EffectKinds.Moved(written), written, result.Path, counterpart.Path);
                    success = true;
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(decision));
            }
        }
        finally
        {
            if (touchesDropbox) context.Admission.ActionCompleted(success);
        }
    }

    private RenameDecision Failure(IReadOnlyList<RenameCandidate> group, Observations? observations, Exception error, Action<Issue>? retainUnfinished,
        Guid attempt, ItemRetryCandidate? expected, IssueSubject registeredSubject, WorkSelection? selection)
    {
        var cause = ActionExecutor.FailureCause(error);
        var inUse = IssuePolicy.IsLocalAccessRetryCause(cause);
        // The refusal came from the Local move, so its retry work names the Local counterpart, never the Dropbox
        // endpoint; both endpoints stay in the subject.
        var localItem = inUse ? registeredSubject.Items.FirstOrDefault(item => item.Side == Side.Local) : null;
        var path = localItem?.Path ?? group[0].Moved.Path;
        var subject = expected?.Token.Subject ?? (inUse ? registeredSubject : Subject(group, observations, null));
        var change = StateChange.Empty with { Raise = [(IssueKind.Failure, cause, path, subject)] };
        ActionExecutor.SettleFailure(context.Store, context.Pair.PairId, context.ItemAttempts, change,
            attempt, expected, inUse && expected is null ? selection?.SourceEpoch ?? context.Evidence.ReadEpoch() : default,
            ItemRetryDispatch.OrdinaryItem, null, retainUnfinished, selection);
        ActionExecutor.EmitIssues(context.Events, change);
        return new RenameDecision(RenameKind.Hold);
    }

    private static IReadOnlyList<(string Path, IReadOnlyList<IssueKind> Kinds)> Closes(SyncedEntry synced,
        IReadOnlyList<RenameCandidate> group) =>
        new[] { synced.LocalPath, synced.DropboxPath }.Concat(group.Select(candidate => candidate.Moved.Path)).Distinct()
            .Select(path => (path, IssueClosure.AfterEffect)).ToArray();

    private static IssueSubject Subject(IReadOnlyList<RenameCandidate> group, Observations? observations, Side? incoming)
    {
        var items = group.Select(candidate => new IssueItem(candidate.Side, candidate.Moved.Path, candidate.Moved.Id)).ToList();
        if (observations?.LocalAtSynced is { } local) items.Add(new IssueItem(Side.Local, local.Path, local.Id));
        if (observations?.DropboxAtSynced is { } dropbox) items.Add(new IssueItem(Side.Dropbox, dropbox.Path, dropbox.Id));
        return IssueSubject.FromDistinct(items, incoming);
    }

    private bool Holds(IEnumerable<(Side Side, string Path, bool Destination)> paths) =>
        paths.Any(path => rows.Covers(PathNames.KeyFor(path.Side, path.Path)));

    internal bool HoldBeforeAdmission(IReadOnlyList<RenameCandidate> group, out Issue? holder)
    {
        holder = null;
        var paths = Paths(group).ToArray();
        if (!Holds(paths)) return false;
        foreach (var path in paths)
        {
            var row = Row.ForOneSide(path.Side, PathNames.KeyFor(path.Side, path.Path),
                group[0].Moved with { Path = path.Path }, group[0].Synced, [], null, RowContext.Neutral);
            var issue = context.Store.HoldingIssue(context.Pair.PairId, row);
            if (issue is not null && (holder is null || issue.Id < holder.Id)) holder = issue;
        }
        rows.HoldRenamePaths(paths.Select(path => (path.Side, path.Path)));
        return true;
    }

    internal IEnumerable<(Side Side, string Path)> Footprint(IReadOnlyList<RenameCandidate> group, Entry? localById) =>
        LockPaths(Paths(group), localById);

    private IEnumerable<(Side Side, string Path, bool Destination)> Paths(IReadOnlyList<RenameCandidate> group)
    {
        foreach (var candidate in group)
        {
            yield return (Side.Local, candidate.Synced.LocalPath, false);
            yield return (Side.Dropbox, candidate.Synced.DropboxPath, false);
            yield return (candidate.Side, candidate.Moved.Path, false);
            yield return (PathNames.Other(candidate.Side), parents.Destination(PathNames.Other(candidate.Side), candidate.Moved.Path), true);
        }
    }

    private static IEnumerable<(Side Side, string Path)> LockPaths(
        IEnumerable<(Side Side, string Path, bool Destination)> paths, Entry? localById)
    {
        foreach (var path in paths)
        {
            yield return (path.Side, path.Path);
        }
        if (localById is not null)
        {
            yield return (Side.Local, localById.Path);
            yield return (Side.Dropbox, localById.Path);
        }
    }

    private static string? MappedDestination(IReadOnlyList<(Side Side, string Path, bool Destination)> paths, Side side)
    {
        foreach (var path in paths)
        {
            if (path.Destination && path.Side == side) return path.Path;
        }
        return null;
    }
}

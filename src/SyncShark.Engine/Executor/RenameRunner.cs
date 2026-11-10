using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Gathers rename observations, decides with Renames.For and applies the decision (section 7.4).</summary>
internal sealed class RenameRunner(ExecutorContext context, Parents parents, RowContexts rows, AncestorFence ancestors,
    bool completedScan = false)
{
    private long? _selectedIssue;
    private SyncedEntry? _listedAdoption;

    internal void SelectClash(WorkSelection selection)
    {
        var continuation = context.Store.ReadRenameContinuation(selection);
        if (continuation?.Clash is not { } clash) return;
        var current = context.Store.IssueById(context.Pair.PairId, clash.Id);
        if (current is not null && current.Matches(clash) && !current.Ignored && !current.Dismissed)
            _selectedIssue = clash.Id;
    }

    internal Task<RenameDecision> RunAdmittedAsync(IAdmittedExecution execution, WorkSelection? selection, object opportunity,
        IReadOnlyList<RenameCandidate> group, Entry? localById,
        Action<Issue>? retainUnfinished, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (selection is not null) SelectClash(selection);
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
        if (decision.Kind == RenameKind.Notice ||
            decision.Kind == RenameKind.Hold && decision.Destination?.Identity != DestinationIdentity.Moving)
            rows.HoldRenamePaths(Paths(group).Select(path => (path.Side, path.Path)));
        return decision;
    }

    // The next pass reads captured retry locals after hold release; an async extraction cannot take them by ref.
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
            var (subject, expected) = ReadRetry(current, synced, localById);
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

    private (IssueSubject Subject, ItemRetryCandidate? Expected) ReadRetry(IReadOnlyList<RenameCandidate> current,
        SyncedEntry synced, Entry? localById)
    {
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
        return (subject, expected);
    }

    // Observation, decision and application share one item-attempt lifetime and callback-aware failure boundary.
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
            RequireReadable(paths, localById, synced, ct);
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
            if ((selection is not null || context.ScanId != Guid.Empty) &&
                ReadDestinationOwner(current, synced, observations, decision) is { } destination)
            {
                var location = await destination.Identities.ReadByIdAsync(destination.Binding.IdOn(destination.Moved.Side), ct);
                var identity = location is null or { InScope: false } ? DestinationIdentity.Gone :
                    PathNames.Same(location.Value.Location.Entry.Path, destination.Moved.Moved.Path)
                        ? DestinationIdentity.Here : DestinationIdentity.Moving;
                observations = observations with
                { Destination = new DestinationFacts(destination.Binding, destination.Moved.Side, identity) };
                decision = Renames.For(current, localById, observations, context.Settings.CaseRenameSuffix,
                    context.Time.GetUtcNow(), evidence.Dirty);
            }
            await ApplyAsync(selection, decision, current, synced, observations, retainUnfinished, attempt, expected, ct);
            return decision;
        }
        catch (PathOccupiedException error) when (!callbackFailed && decision is { Kind: RenameKind.Follow or RenameKind.Adopt })
        {
            var written = error.CollidingSide ?? decision.WrittenSide() ?? Side.Local;
            var read = written; // the side whose read failed owns a temporary failure's epoch
            Entry? occupant;
            Entry? destinationOccupant = null;
            try
            {
                if (completedScan) return new RenameDecision(RenameKind.NeedsFresh);
                occupant = await context.For(written).ReadOneAsync(error.Path, ct);
                if (written == Side.Local && decision.MovedSide == Side.Local &&
                    Renames.FirstOn(current, Side.Local) is { } incoming && occupant is not null &&
                    occupant.Id == incoming.Moved.Id && PathNames.Same(occupant.Path, incoming.Moved.Path))
                    destinationOccupant = await context.For(read = Side.Dropbox).ReadOneAsync(decision.Target!, ct);
            }
            catch (Exception readError) when (readError is ChangedUnderneathException or ItemFailedException)
            {
                return Failure(current, observations, readError, retainUnfinished, attempt, expected, registeredSubject, selection,
                    read);
            }
            SettleOccupied(selection, decision, current, observations, retainUnfinished, attempt, expected, error, written,
                occupant, destinationOccupant, synced.DropboxId);
            return new RenameDecision(RenameKind.Hold, NoticeKind: IssueKind.NameClash);
        }
        catch (ChangedUnderneathException) when (completedScan)
        {
            return new RenameDecision(RenameKind.NeedsFresh);
        }
        catch (Exception error) when (!callbackFailed && ItemFailures.IsItemError(error))
        {
            return Failure(current, observations, error, retainUnfinished, attempt, expected, registeredSubject, selection,
                decision?.WrittenSide());
        }
    }

    private (IIdentitySide Identities, RenameCandidate Moved, SyncedEntry Binding)? ReadDestinationOwner(
        IReadOnlyList<RenameCandidate> current, SyncedEntry synced, Observations observations, RenameDecision decision)
    {
        if (current.Count != 1 || decision.Kind != RenameKind.Follow ||
            context.For(current[0].Side) is not IIdentitySide identities) return null;
        var moved = current[0];
        var counterpartSide = PathNames.Other(moved.Side);
        var counterpart = observations.AtSynced(counterpartSide)!;
        var owners = DestinationOwners(synced, counterpart, moved.Moved, counterpartSide, decision.Target!, out _);
        return owners.Count == 1 ? (identities, moved, owners[0].Binding) : null;
    }

    private void SettleOccupied(WorkSelection? selection, RenameDecision decision,
        IReadOnlyList<RenameCandidate> current, Observations? observations, Action<Issue>? retainUnfinished,
        Guid attempt, ItemRetryCandidate? expected, PathOccupiedException error, Side written, Entry? occupant,
        Entry? destinationOccupant, string ownDropboxId)
    {
        var subject = Subject(current, observations, decision.MovedSide);
        var occupants = new List<IssueItem>();
        if (occupant is not null) occupants.Add(new IssueItem(written, occupant.Path, occupant.Id));
        // A landed peer move puts our own Dropbox item at the destination; it is not an occupant.
        if (destinationOccupant is not null && destinationOccupant.Id != ownDropboxId)
            occupants.Add(new IssueItem(Side.Dropbox, destinationOccupant.Path, destinationOccupant.Id));
        if (occupants.Count > 0)
            subject = IssueSubject.FromDistinct(subject.Items.Concat(occupants), subject.Incoming);
        var change = StateChange.Empty with { Raise = [(IssueKind.NameClash, TextKey.ClashNameTaken, error.Path, subject)] };
        List<RunEvent>? settled = null;
        ActionExecutor.Settle(context.Store, context.Pair.PairId, context.ItemAttempts, change,
            attempt, expected, selection, retainUnfinished,
            settledIssue: (issue, isNew) => ActionChanges.CollectSettlement(ref settled, issue, isNew));
        ActionChanges.EmitSettled(context.Events, settled);
    }

    private void RequireReadable(IReadOnlyList<(Side Side, string Path, bool Destination)> paths,
        Entry? localById, SyncedEntry synced, CancellationToken ct)
    {
        // A rename can rebase protected bindings even when its provider paths are readable.
        var roots = paths.Where(item => item.Side == Side.Local)
            .Select(item => (item.Side, PathNames.Key(item.Path))).ToList();
        if (localById is not null) roots.Add((Side.Local, PathNames.Key(localById.Path)));
        roots.Add((Side.Local, synced.LocalKey));
        roots.Add((Side.Dropbox, synced.DropboxKey));
        ActionExecutor.RequireReadable(context.Store, context.Pair.PairId, synced.LocalPath, roots, synced.IsFolder, ct);
    }

    private (Observations Observations, RenameDecision Decision)? ListedAdoption(IReadOnlyList<RenameCandidate> group, SyncedEntry synced,
        Entry? localById, IReadOnlyList<(Side Side, string Path, bool Destination)> paths)
    {
        if (!completedScan || context.ScanId == Guid.Empty) return null;
        var localCandidate = Renames.FirstOn(group, Side.Local);
        var savedLocal = context.Store.ScanEntry(context.Pair.PairId, context.ScanId, Side.Local, synced.LocalKey);
        if (Renames.IsLocalAlias(localCandidate, savedLocal)) return null;
        var local = localCandidate?.Moved;
        var dropbox = Renames.FirstOn(group, Side.Dropbox)?.Moved;
        local = local is null ? savedLocal : context.Store.ScanEntry(context.Pair.PairId, context.ScanId, Side.Local,
            PathNames.KeyFor(Side.Local, local.Path));
        dropbox = context.Store.ScanEntry(context.Pair.PairId, context.ScanId, Side.Dropbox,
            dropbox is null ? synced.DropboxKey : PathNames.KeyFor(Side.Dropbox, dropbox.Path));
        if (!Matches(local, synced, Side.Local) || !Matches(dropbox, synced, Side.Dropbox) ||
            group.Any(candidate => candidate.Moved != (candidate.Side == Side.Local ? local : dropbox))) return null;
        var observations = Observe(group, synced, paths, local, dropbox);
        // Listing evidence may relink history, but never authorizes a provider effect.
        var decision = Renames.For(group, localById, observations, context.Settings.CaseRenameSuffix,
            context.Time.GetUtcNow(), false);
        if (decision.Kind != RenameKind.Adopt) return null;
        var adopted = SyncedEntries.Relink(synced, local!, dropbox!);
        if (!context.Store.CanUseListedEvidence(context.Pair.PairId, context.ScanId,
            adopted.LocalKey, adopted.DropboxKey, adopted.IsFolder, synced)) return null;
        _listedAdoption = adopted;
        return (observations, decision);
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

    private Task ApplyAsync(WorkSelection? selection, RenameDecision decision, IReadOnlyList<RenameCandidate> group, SyncedEntry synced,
        Observations observations, Action<Issue>? retainUnfinished, Guid attempt,
        ItemRetryCandidate? expected, CancellationToken ct)
    {
        if (decision.Kind is RenameKind.Follow or RenameKind.FinishCaseRename)
            return FollowAsync(selection, decision, group, synced, observations, retainUnfinished, attempt, expected, ct);
        var path = group[0].Moved.Path;
        switch (decision.Kind)
        {
            case RenameKind.None:
            case RenameKind.Vacate:
            case RenameKind.Hold:
            case RenameKind.Leave:
                return Task.CompletedTask;
            case RenameKind.Notice:
                return SaveAsync(selection, decision, synced, retainUnfinished, attempt, expected,
                    StateChange.Empty with { Raise = [(decision.NoticeKind!.Value, decision.Cause!.Value,
                    path, Subject(group, observations, null))] });
            case RenameKind.Split:
                return SaveAsync(selection, decision, synced, retainUnfinished, attempt, expected, StateChange.Empty with
                {
                    ForgetSelf = [(Side.Local, synced.LocalKey)],
                    Raise = [(decision.NoticeKind!.Value, decision.Cause!.Value, path,
                        Subject(group, observations, decision.MovedSide))]
                });
            case RenameKind.Adopt:
                return AdoptAsync(selection, decision, group, synced, observations, retainUnfinished, attempt, expected, ct);
            default:
                throw new ArgumentOutOfRangeException(nameof(decision));
        }
    }

    private async Task AdoptAsync(WorkSelection? selection, RenameDecision decision, IReadOnlyList<RenameCandidate> group,
        SyncedEntry synced, Observations observations, Action<Issue>? retainUnfinished, Guid attempt,
        ItemRetryCandidate? expected, CancellationToken ct)
    {
        var adopted = _listedAdoption ?? SyncedEntries.Relink(synced,
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
                await SaveAsync(selection, decision, synced, retainUnfinished, attempt, expected, retirement);
                return;
            }
        }
        await SaveAsync(selection, decision, synced, retainUnfinished, attempt, expected, StateChange.Empty with
        {
            Renames = [new RenameApplied(synced, adopted, null, null, null)],
            Close = Closes(synced, group),
            ListedScanId = _listedAdoption is null ? null : context.ScanId
        });
    }

    // The provider move and settlement share the success flag reported by this one completion boundary.
    private async Task FollowAsync(WorkSelection? selection, RenameDecision decision, IReadOnlyList<RenameCandidate> group,
        SyncedEntry synced, Observations observations, Action<Issue>? retainUnfinished, Guid attempt,
        ItemRetryCandidate? expected, CancellationToken ct)
    {
        var writtenSide = decision.WrittenSide();
        var touchesDropbox = writtenSide == Side.Dropbox;
        var success = false;
        try
        {
            var path = group[0].Moved.Path;
            var finishing = decision.Kind == RenameKind.FinishCaseRename;
            var movedSide = finishing ? Side.Local : decision.MovedSide!.Value;
            var written = writtenSide!.Value;
            var counterpart = Renames.FirstOn(group, written)?.Moved ??
                observations.AtSynced(written) ??
                throw new ChangedUnderneathException(synced.PathOn(written));
            var moved = finishing ? null : Renames.RequireOn(group, movedSide).Moved;
            path = decision.Target!;
            if (!finishing)
            {
                RequireUnblockedDestination(synced, counterpart, moved!, written, path);
                await parents.EnsureAsync(written, path, ct);
            }
            ItemSync.RequireLiveWrite(context, written == Side.Local);
            var result = await context.For(written).MoveAsync(counterpart, path, ct);
            var after = finishing ? synced with
            {
                DropboxPath = result.Path, DropboxKey = PathNames.DropboxKey(result.Path), DropboxId = result.Id
            } : written == Side.Local ? SyncedEntries.Relink(synced, result, moved!) : SyncedEntries.Relink(synced, moved!, result);
            await SaveAsync(selection, decision, synced, retainUnfinished, attempt, expected, StateChange.Empty with
            {
                Renames = [new RenameApplied(synced, after, written, counterpart.Path, result.Path)],
                Close = Closes(synced, group)
            }, EffectKinds.Moved(written), written, result.Path, counterpart.Path);
            success = true;
        }
        finally
        {
            if (touchesDropbox) context.Admission.ActionCompleted(success);
        }
    }

    private void RequireUnblockedDestination(SyncedEntry synced, Entry counterpart, Entry moved, Side written, string path)
    {
        var blockers = DestinationOwners(synced, counterpart, moved, written, path, out var proposed);
        if (blockers.Count > 1)
        {
            var blockedSide = blockers[0].LocalKeyMatched ? Side.Local : Side.Dropbox;
            throw new PathOccupiedException(proposed.PathOn(blockedSide), blockedSide);
        }
    }

    private IReadOnlyList<(SyncedEntry Binding, bool LocalKeyMatched, bool DropboxKeyMatched)> DestinationOwners(
        SyncedEntry synced, Entry counterpart, Entry moved, Side written, string path, out SyncedEntry proposed)
    {
        proposed = written == Side.Local
            ? SyncedEntries.Relink(synced, counterpart with { Path = path }, moved)
            : SyncedEntries.Relink(synced, moved, counterpart with { Path = path });
        return context.Store.RenameDestinationOwners(context.Pair.PairId, synced, proposed);
    }

    private async Task SaveAsync(WorkSelection? selection, RenameDecision decision, SyncedEntry synced,
        Action<Issue>? retainUnfinished, Guid attempt, ItemRetryCandidate? expected, StateChange change,
        EffectKind? effect = null, Side? side = null, string path = "", string detail = "")
    {
        if (decision.Kind is RenameKind.Adopt or RenameKind.Follow or RenameKind.FinishCaseRename)
            change = change with { FirstRunAcknowledged =
                [new IssueItem(Side.Local, synced.LocalPath, synced.LocalId)] };
        List<RunEvent>? settled = null;
        Action<Issue, bool>? collect = change.Raise.Count == 0 ? null :
            (issue, isNew) => ActionChanges.CollectSettlement(ref settled, issue, isNew);
        if (completedScan && decision.Kind == RenameKind.Adopt && !synced.IsFolder &&
            expected is null && selection is null && change.Renames.Count != 0)
            await context.Store.CommitAsync(context.Pair.PairId, change, collect);
        else
            ActionExecutor.Settle(context.Store, context.Pair.PairId, context.ItemAttempts, change,
                attempt, expected, selection, retainUnfinished, settledIssue: collect);
        if (effect is { } kind && side is { } written)
            context.Events(RunEvent.Effect(kind, path, detail, written));
        ActionChanges.EmitSettled(context.Events, settled);
    }

    private RenameDecision Failure(IReadOnlyList<RenameCandidate> group, Observations? observations, Exception error, Action<Issue>? retainUnfinished,
        Guid attempt, ItemRetryCandidate? expected, IssueSubject registeredSubject, WorkSelection? selection, Side? written = null)
    {
        var cause = ItemFailures.FailureCause(error);
        var failed = error as ItemFailedException;
        var disposition = failed?.Disposition ?? FailureDisposition.Permanent;
        var temporary = disposition == FailureDisposition.TemporaryNoEffect;
        var inUse = IssuePolicy.IsLocalAccessRetryCause(cause);
        // The refusal came from the Local move, so its retry work names the Local counterpart, never the Dropbox
        // endpoint; both endpoints stay in the subject.
        var localItem = inUse ? registeredSubject.Items.FirstOrDefault(item => item.Side == Side.Local) : null;
        var path = localItem?.Path ?? group[0].Moved.Path;
        // The side the move was submitted on owns the retry, so its source fences a temporary turn.
        var endpoint = localItem is null && temporary ? written ?? group[0].Side : Side.Local;
        var subject = ItemFailures.WithUnknownMove(
            expected?.Token.Subject ?? (inUse ? registeredSubject : Subject(group, observations, null)), disposition, failed);
        var change = temporary ? StateChange.Empty : StateChange.Empty with { Raise = [(IssueKind.Failure, cause, path, subject)] };
        List<RunEvent>? settled = null;
        var accepted = ItemFailures.SettleFailure(context.Store, context.Pair.PairId, context.ItemAttempts, change,
            attempt, expected, temporary || inUse && expected is null ? selection?.SourceEpoch ?? context.Evidence.ReadEpoch(endpoint) : default,
            ItemRetryDispatch.OrdinaryItem, null, retainUnfinished, selection,
            disposition, cause, path, subject, null, failed?.ProviderWait, failed?.ProviderWaitStarted,
            (issue, isNew) => ActionChanges.CollectSettlement(ref settled, issue, isNew, error, cause));
        if (temporary) ItemFailures.EmitTemporary(context.Events, accepted, cause, path, error, null);
        else ActionChanges.EmitSettled(context.Events, settled);
        return new RenameDecision(RenameKind.Hold, NoticeKind: temporary ? null : IssueKind.Failure, Unfinished: temporary);
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
        paths.Any(path => rows.Covers(PathNames.KeyFor(path.Side, path.Path), _selectedIssue));

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

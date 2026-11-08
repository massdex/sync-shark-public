using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Retains a finite selection until its admitted branches and exact residual registrations settle.</summary>
internal sealed class SelectionPass
{
    private readonly ExecutorContext _context;
    private readonly ItemSync _items;
    private readonly ActionExecutor _executor;
    private readonly AncestorFence _ancestors;
    private readonly ILiveExecutionBoundary _boundary;
    private readonly RowContexts _rows;
    private readonly Observer _observer;
    private readonly Parents _parents;
    private readonly Dictionary<string, (Row? Held, SyncedEntry? Before, Issue? Holder)> _visited = new(StringComparer.Ordinal);
    private readonly Func<WorkSelection?, Side, string, IReadOnlySet<long>?, bool, CancellationToken, Task>? _runBatch;
    // Roots this selection already batched; a folder notice under one is entry-only.
    private readonly PrefixSet _batched = new();
    // D-1105-1: folder batches start after the pair's first Repair, which cannot complete during a selection.
    private bool _batches;
    // The selection's counterpart peer once its root is observed; its batch lists that side too.
    private SelectionPeer? _peer;

    internal SelectionPass(ExecutorContext context, ItemSync items, AncestorFence ancestors, ILiveExecutionBoundary liveBoundary,
        ActionExecutor executor, RowContexts rows, Action<string, bool> progress,
        Func<WorkSelection?, Side, string, IReadOnlySet<long>?, bool, CancellationToken, Task>? runBatch = null)
    {
        _runBatch = runBatch;
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _ancestors = ancestors;
        _executor = executor;
        _boundary = liveBoundary ?? throw new ArgumentNullException(nameof(liveBoundary));
        _rows = rows;
        _progress = progress;
        _parents = new Parents(context);
        _observer = new Observer(context, _parents);
    }

    private readonly Action<string, bool> _progress;
    private Action? _release;
    private string? _activeBranch;
    private WorkSelection? _prerequisite;

    internal bool Refused { get; private set; }

    internal async Task<(bool? Completed, WorkSelection? Prerequisite)> RunAsync(WorkSelection selection, CancellationToken ct, Action? release = null)
    {
        _items.SelectRows(_rows);
        _release = release;
        _prerequisite = null;
        _visited.Clear();
        _batched.Clear();
        _peer = null;
        _batches = SyncPorts.AdmitsBatch(_context.Store, selection.PairId);
        _rows.ForSelection();
        if (!_context.Store.BeginSelection(selection)) return (null, null);
        Refused = false;
        _ancestors.RunSelection(selection, () => Refused = true);
        try { return (await RunSelectionAsync(selection, ct).ConfigureAwait(false), _prerequisite); }
        catch (ChangedUnderneathException)
        {
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged);
        }
        finally
        {
            try
            {
                if (_activeBranch is { } path) _progress(path, true);
            }
            finally
            {
                _activeBranch = null;
                _context.Store.EndSelection(selection, _prerequisite);
                _ancestors.RunSelection(null);
            }
        }
    }

    private async Task<bool> RunSelectionAsync(WorkSelection selection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.PairId != _context.Pair.PairId)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
        var ready = true;
        var found = false;
        SelectionPeer? peer = null;
        LiveEffectScope? selectedScope = null;
        Func<Side, string, string?, bool>? separateWork = null;
        Func<SyncedEntry, long, bool>? localEcho = null;
        await foreach (var (work, walkFailure, original, initialPrimary) in ReadBranchesAsync(selection, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            found = true;
            if (work.PairId != selection.PairId || work.PairGeneration != selection.PairGeneration ||
                work.SourceEpoch != selection.SourceEpoch)
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
            var notice = work.Notice;
            var key = PathNames.KeyFor(notice.Side, notice.Path);
            if (original is not null) separateWork = null;
            if (original is null && walkFailure is null &&
                (separateWork ??= _context.Store.CreateSeparateWorkProbe(selection))(notice.Side, key, notice.Id)) continue;
            selectedScope ??= LockKeys.ForLivePaths(_context.Pair, [(selection.Side, (original ?? notice).Path)], false).Single();
            if (original is not null)
            {
                if (_visited.TryGetValue(key, out var prior))
                {
                    if (prior.Held is not null)
                        ready &= AttachHeld(selection, original, prior.Held, prior.Before, prior.Holder);
                    continue;
                }
                _visited.Add(key, default);
            }
            void RememberHeld(Row held, SyncedEntry? saved, Issue? holder)
            {
                if (original is not null) _visited[key] = (held, saved, holder);
            }
            var throughPeer = peer is null && key == selection.Key
                ? _context.Store.ReadWorkWatermark() : (long?)null;
            SyncedEntry? before = work.Before;
            var attached = false;
            void Retain(Row effective, Issue issue)
            {
                var own = effective.Key == key || notice.Id is not null && effective.EntryOn(notice.Side)?.Id == notice.Id;
                var residual = own
                    ? original is not null && original.Path != notice.Path ? original :
                        notice with { Path = issue.Path, Observed = effective.EntryOn(notice.Side) }
                    : ChildNotice(selection.Side, effective.PathOn(selection.Side) ??
                        _parents.Counterpart(selection.Side, effective.PathOn(PathNames.Other(selection.Side)) ?? issue.Path,
                            effective.Synced), null);
                attached |= RetainIssue(selection, effective, residual, own ? before : effective.Synced, issue,
                    retained => { if (own) RememberHeld(effective, before, retained); });
            }
            var freshPrimary = initialPrimary;
            LocalEvidence.DirtyProof? echoEvidence = null;
            // One observation of both sides projects one row; the error paths fail that same observation.
            async ValueTask<Row?> ObserveProjectedAsync(ItemFailedException? failedWalk, Action<Row, string?>? observed)
            {
                Row? raw = null;
                echoEvidence = null;
                try
                {
                    if (failedWalk is not null) throw failedWalk;
                    var suppliedPrimary = freshPrimary;
                    freshPrimary = null;
                    var local = await _observer.ObserveLocalAsync(notice.Side, notice.Path, ct, suppliedPrimary).ConfigureAwait(false);
                    // Repeating Excludes for in-scope rows is the accepted cost of saving excluded rows' Dropbox read.
                    if (notice is { Side: Side.Local, Kind: ChangeKind.Changed, OldPath: null } &&
                        local is { Local: { IsFolder: false } entry, Synced: null } &&
                        _context.Scope.Excludes(entry.Path, null, entry.IsFolder) &&
                        _context.Store.SyncedById(selection.PairId, Side.Local, entry.Id) is null) return null;
                    if (notice is { Side: Side.Local, Kind: not ChangeKind.Deleted, IdentityDeleted: false })
                    {
                        if (Decide.LocalMatchesBaseline(local.Local, local.Synced) &&
                            (localEcho ??= _context.Store.CreateLocalEchoProbe(selection))(local.Synced!,
                                throughPeer ?? _context.Store.ReadWorkWatermark()))
                        {
                            if (local.Local!.IsFolder) return null;
                            var evidence = _context.Evidence.Read(local.Local, local.Synced);
                            if (!evidence.Dirty) return null; // No observation means no peer settlement authority.
                            echoEvidence = new LocalEvidence.DirtyProof(evidence.Dirty, evidence.Boundary, evidence.Version);
                        }
                    }
                    raw = await _observer.ObserveDropboxAsync(notice.Side, notice.Path, local, ct, suppliedPrimary).ConfigureAwait(false);
                    var other = PathNames.Other(notice.Side);
                    var otherKey = original is null || peer is null && throughPeer is not null
                        ? PathNames.KeyFor(other, raw.PathOn(other) ?? _parents.Counterpart(other, notice.Path, raw.Synced)) : null;
                    if (original is null && separateWork!(other, otherKey!, raw.EntryOn(other)?.Id)) return null;
                    observed?.Invoke(raw, otherKey);
                    return raw with { Context = await _rows.ForSelectionAsync(raw, ct).ConfigureAwait(false) };
                }
                catch (Exception error) when (ActionExecutor.IsItemError(error) && error is not ChangedUnderneathException)
                {
                    raw ??= Row.ForOneSide(notice.Side, key, notice.Observed, work.Before, [], null, RowContext.Neutral);
                    await _executor.FailAsync(raw, null, error, ct, issue => Retain(raw, issue),
                        selection: selection).ConfigureAwait(false);
                    ready &= attached;
                    return null;
                }
            }
            // A walked child or old path refuses from its notice alone, before any provider call; the enumerator
            // already refused the selection's own primary branch.
            if (walkFailure is null && key != selection.Key) RefuseBeforeProvider(notice, work.Before);
            var row = await ObserveProjectedAsync(walkFailure, (raw, otherKey) =>
            {
                if (throughPeer is { } through)
                {
                    _peer = peer = new SelectionPeer(otherKey!, through);
                    _context.Store.ClaimSelectionPeer(selection, peer);
                    _ancestors.RunSelection(selection with { Peer = peer });
                }
                before = work.Before;
            });
            if (row is null) continue;
            var observed = row.EntryOn(notice.Side);
            var candidate = Renames.Candidate(notice.Side, observed,
                observed is null ? null : _context.Store.SyncedById(selection.PairId, notice.Side, observed.Id));
            if (candidate is not null)
            {
                var rename = await RenameAsync(selection, notice, original, row, candidate,
                    selectedScope, peer, ct).ConfigureAwait(false);
                if (!rename.Renamed)
                {
                    if (_prerequisite is not null) return false;
                    ready &= rename.Ready;
                    continue;
                }
                row = await ObserveProjectedAsync(null, (raw, _) => before = raw.Synced);
                if (row is null) continue;
            }
            var (subject, expected) = _items.CaptureAttempt(row);
            // One owner prepares the action, its label set and its phase; this caller only sequences live admission.
            ItemSync.LivePreparation preparation;
            try { preparation = await _items.PrepareLiveAsync(row, ct, echoEvidence).ConfigureAwait(false); }
            catch (Exception error) when (ActionExecutor.IsItemError(error) && error is not ChangedUnderneathException)
            {
                await _executor.FailAsync(row, null, error, ct, issue => Retain(row, issue),
                    expectedAttempt: expected, selection: selection).ConfigureAwait(false);
                ready &= attached;
                continue;
            }
            var prepared = preparation.Action;
            row = prepared.Row;
            var owned = row.LocalHash is null ? null : _context.Evidence.Capture(row);
            try
            {
                // Settled before any coverage capture or lease: a held branch waits on its notice, its siblings carry on.
                if (prepared is { Kind: ActionKind.None, None: NoneReason.Held })
                {
                    var holder = _context.Store.HoldingIssue(selection.PairId, prepared.Row);
                    RememberHeld(prepared.Row, before, holder);
                    ready &= AttachHeld(selection, original ?? notice, prepared.Row, before, holder);
                    continue;
                }
                if (prepared is { Kind: ActionKind.None, None: NoneReason.Skipped or NoneReason.OutOfScope })
                {
                    if (key == selection.Key) peer = null;
                    continue;
                }
                // No file effect needs admission, but the success still closes its outstanding issue.
                if (prepared.IsInSync())
                {
                    ready &= (await _context.ItemAttempts.ExecuteAsync(selection, expected, subject,
                        (attemptId, token) => _executor.ExecuteAsync(prepared, null, token,
                            attemptId: attemptId, expectedAttempt: expected, selection: selection), ct)
                        .ConfigureAwait(false)).Succeeded();
                    continue;
                }
                if (preparation.TakeReplacementPhase)
                {
                    ready &= (await _items.RunLiveReplacementAsync(preparation, selection with { Peer = peer }, subject, expected, Retain, ct)
                        .ConfigureAwait(false)).Succeeded();
                    continue;
                }
                var labels = preparation.Labels;
                var footprint = LockKeys.ForLiveAction(prepared, _context.Pair, labels);
                if (key != selection.Key) footprint = footprint.Union([selectedScope]).ToArray();
                LiveCoverage coverage = null!;
                var outcome = await _context.ItemAttempts.ExecuteAsync(selection, expected, subject, async (attemptId, token) =>
                {
                    coverage = (await _boundary.CaptureCoverageAsync(selection, footprint, token).ConfigureAwait(false)) with { Peer = peer };
                    Task<SyncOutcome> ExecuteAsync(Func<ValueTask>? staged) =>
                        _boundary.ExecuteAsync([new LiveExecutionRequest(selection, coverage, footprint)],
                            async (execution, admittedToken) =>
                            {
                                if (staged is not null) await execution.ReleaseAdmissionAsync();
                                return await _items.RunAdmittedAsync(row, admittedToken, execution: execution,
                                    selection: selection with { Peer = peer }, retainUnfinished: Retain,
                                    preparedLabels: labels, attemptId: attemptId, expectedAttempt: expected,
                                    preparation: preparation, uploadStaged: staged);
                            }, token);
                    try
                    {
                        return prepared.Kind == ActionKind.Upload
                            ? await _items.AdmitUploadAsync(_release, staged => ExecuteAsync(staged), token)
                            : await ExecuteAsync(null);
                    }
                    catch (LiveAdmissionStaleException stale) when
                        (stale.Reason == LiveAdmissionStaleReason.FootprintChanged && stale.HandoffRow is not null)
                    {
                        _context.Evidence.Release(row, owned);
                        row = stale.HandoffRow;
                        owned = _context.Evidence.Capture(row);
                    }
                    // Recovery is outside the protected first call: a second handoff takes the ordinary refusal route.
                    preparation = await _items.PrepareLiveAsync(row, token,
                        previous: preparation with { Retried = true }).ConfigureAwait(false);
                    prepared = preparation.Action;
                    row = prepared.Row;
                    owned = row.LocalHash is null ? null : _context.Evidence.Capture(row);
                    labels = preparation.Labels;
                    footprint = LockKeys.ForLiveAction(prepared, _context.Pair, labels);
                    if (key != selection.Key) footprint = footprint.Union([selectedScope]).ToArray();
                    coverage = (await _boundary.CaptureCoverageAsync(selection, footprint, token).ConfigureAwait(false)) with { Peer = peer };
                    return await ExecuteAsync(null);
                }, ct).ConfigureAwait(false);
                if (outcome.Deferred)
                {
                    // The folder plan acquires its own footprint after the outer lease and attempt have ended.
                    ready &= await _items.Folders.RunLiveFolderPlanAsync(row, Retain,
                        selection with { Peer = peer }, coverage, footprint, ct).ConfigureAwait(false);
                }
                else ready &= outcome.Succeeded() || attached;
            }
            finally { _context.Evidence.Release(row, owned); }
        }
        if (!found) throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
        if (!ready) return false;
        return _context.Store.CompleteWork(selection, peer);
    }

    private bool RetainIssue(WorkSelection selection, Row row, ChangeNotice notice, SyncedEntry? before,
        Issue issue, Action<Issue> remember)
    {
        if (!_context.Store.AttachIssueWork(selection, issue,
            [(notice, row.IsFolder(), before)])) return false;
        remember(issue);
        return true;
    }

    private async Task<(bool Renamed, bool Ready)> RenameAsync(WorkSelection selection,
        ChangeNotice notice, ChangeNotice? original, Row row, RenameCandidate candidate,
        LiveEffectScope selectedScope, SelectionPeer? peer, CancellationToken ct)
    {
        var observed = candidate.Moved;
        var savedIdentity = candidate.Synced;
        var held = row with { Synced = savedIdentity };
        void Remember(Issue? issue)
        {
            if (original is null) return;
            var retained = (held, savedIdentity, issue);
            _visited[PathNames.KeyFor(notice.Side, notice.Path)] = retained;
            _visited[PathNames.KeyFor(notice.Side, notice.OldPath ?? savedIdentity.PathOn(notice.Side))] = retained;
        }
        var renamer = new RenameRunner(_context, _parents, _rows, _ancestors);
        renamer.SelectClash(selection);
        if (renamer.HoldBeforeAdmission([candidate], out var holder))
        {
            Remember(holder);
            return (false, AttachHeld(selection, notice, held, savedIdentity, holder));
        }
        if (original is not null && PathNames.KeyFor(notice.Side, notice.Path) == selection.Key &&
            candidate.Moved.Id == selection.Identity)
        {
            var owners = _context.Store.ReconcileRenameContinuation(selection, savedIdentity);
            if (owners.Count > 1)
            {
                RefusePrerequisite(selection, owners.Select(owner => owner.Binding));
                _rows.HoldRename([candidate]);
                return (false, false);
            }
        }
        var footprint = LockKeys.ForRenamePaths(_context.Pair,
            renamer.Footprint([candidate], notice.Side == Side.Local ? observed : null), observed.IsFolder);
        if (PathNames.KeyFor(notice.Side, notice.Path) != selection.Key)
            footprint = footprint.Union([selectedScope]).ToArray();
        var coverage = (await _boundary.CaptureCoverageAsync(selection, footprint, ct).ConfigureAwait(false)) with { Peer = peer };
        var retained = false;
        var decision = await _boundary.ExecuteAsync([new LiveExecutionRequest(selection, coverage, footprint)],
            (execution, token) => _items.RenameAdmittedAsync(execution,
                original is null ? null : selection with { Peer = peer },
                selection, [candidate], notice.Side == Side.Local ? observed : null,
                token, issue => retained |= RetainIssue(selection, held, notice, savedIdentity, issue, Remember)),
            ct).ConfigureAwait(false);
        if (decision is { Kind: RenameKind.Vacate, Destination: { } vacated })
        {
            if (original is null) throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
            var selected = selection with { Peer = peer };
            if (!await _items.RemoveVacatedAsync(candidate, vacated.Binding, selected, ct))
                RefusePrerequisite(selection);
            // Removal evidence reaches the next selection; ordinary old-path and child branches must not inherit its admission.
            _rows.HoldRename([candidate]);
            return (false, false);
        }
        if (decision is { Kind: RenameKind.Hold, Destination.Identity: DestinationIdentity.Moving } && original is not null)
        {
            var destination = decision.Destination.Binding;
            _prerequisite = _context.Store.PendingAncestors(selection.PairId, selection.PairGeneration,
                candidate.Side, [destination.KeyOn(candidate.Side)]).FirstOrDefault(work =>
                    work.Identity == destination.IdOn(candidate.Side));
            if (_prerequisite is not null && _prerequisite.AnchorWorkId != selection.AnchorWorkId)
                return (false, false);
            _prerequisite = null;
            RefusePrerequisite(selection);
            _rows.HoldRename([candidate]);
            return (false, false);
        }
        if (decision.Kind is RenameKind.None or RenameKind.Leave &&
            _context.Store.ReadRenameContinuation(selection) is not null)
        {
            _rows.HoldRename([candidate]);
            return (false, false);
        }
        if (decision.Kind is not (RenameKind.Hold or RenameKind.Notice)) return (true, true);
        if (retained) return (false, true);
        var current = _context.Store.HoldingIssue(selection.PairId, held);
        Remember(current);
        return (false, AttachHeld(selection, notice, held, savedIdentity, current));
    }

    internal void RefusePrerequisite(WorkSelection selection, IEnumerable<SyncedEntry>? blockers = null)
    {
        try
        {
            foreach (var work in _context.Store.ReadWork(selection))
            {
                var notice = work.Notice;
                var subjects = new List<IssueItem> { new(notice.Side, notice.Path, notice.Id ?? string.Empty) };
                if (work.Before is { } saved)
                {
                    subjects.Add(new IssueItem(Side.Local, saved.LocalPath, saved.LocalId));
                    subjects.Add(new IssueItem(Side.Dropbox, saved.DropboxPath, saved.DropboxId));
                }
                if (blockers is not null)
                    foreach (var blocker in blockers)
                    {
                        subjects.Add(new IssueItem(Side.Local, blocker.LocalPath, blocker.LocalId));
                        subjects.Add(new IssueItem(Side.Dropbox, blocker.DropboxPath, blocker.DropboxId));
                    }
                var subject = IssueSubject.FromDistinct(subjects, notice.Side);
                var change = StateChange.Empty with
                { Raise = [(IssueKind.NameClash, TextKey.ClashNameTaken, notice.Path, subject)] };
                _context.Store.Commit(selection.PairId, change, selection, issue =>
                    _context.Store.AttachIssueWork(selection, issue, [(notice, notice.Observed?.IsFolder == true, work.Before)]));
                ActionExecutor.EmitIssues(_context.Events, change);
            }
        }
        finally { _context.Store.EndSelection(selection); }
    }

    // An absent primary follows its identity: Local by id within scope; Dropbox to the selection's own last member
    // (B-101, D19), whose membership read and point read happen once per selection and are returned for reuse.
    private async Task<(Entry? Moved, (string? Key, Entry? Entry)? Later)> RelocateAsync(WorkSelection selection,
        ChangeNotice notice, string primaryKey, (string? Key, Entry? Entry)? later, CancellationToken ct)
    {
        if (notice is not { IdentityDeleted: false, Id: { } id }) return (null, later);
        if (notice.Side == Side.Local)
            return (_context.Local is IIdentitySide identities && await identities.ReadByIdAsync(id, ct)
                .ConfigureAwait(false) is { InScope: true } result ? result.Location.Entry : null, later);
        if (later is null)
        {
            var last = _context.Store.ReadWork(selection).LastOrDefault(member =>
                member.Notice is { Side: Side.Dropbox, IdentityDeleted: false } other && other.Id == id)?.Notice;
            var key = last is null ? null : PathNames.KeyFor(Side.Dropbox, last.Path);
            later = key is null || key == primaryKey ? (null, null)
                : (key, await _context.Dropbox.ReadOneAsync(last!.Path, ct).ConfigureAwait(false));
        }
        return (later.Value.Entry is { } moved && moved.Id == id ? moved : null, later);
    }

    // A holder that changed or closed since the lookup refuses exactly; the branch stays unfinished and reachable.
    private bool AttachHeld(WorkSelection selection, ChangeNotice notice, Row row, SyncedEntry? before, Issue? holder)
    {
        if (holder is null) return false;
        try
        {
            return _context.Store.AttachIssueWork(selection, holder,
                [(notice, row.IsFolder(), before)]);
        }
        catch (LiveAdmissionStaleException error) when (error.Reason == LiveAdmissionStaleReason.RelevantEvidenceChanged)
        {
            return false;
        }
    }

    private async IAsyncEnumerable<(PendingChange Work, ItemFailedException? Failure, ChangeNotice? Original,
        (string Path, Entry? Entry)? FreshPrimary)> ReadBranchesAsync(
        WorkSelection selection, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var branch in EnumerateBranchesAsync(selection, ct).ConfigureAwait(false))
        {
            _activeBranch = branch.Work.Notice.Path;
            _progress(_activeBranch, false);
            try { yield return branch; }
            finally
            {
                _activeBranch = null;
                _progress(branch.Work.Notice.Path, true);
            }
        }
    }

    private async IAsyncEnumerable<(PendingChange Work, ItemFailedException? Failure, ChangeNotice? Original,
        (string Path, Entry? Entry)? FreshPrimary)> EnumerateBranchesAsync(
        WorkSelection selection,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        (string? Key, Entry? Entry)? later = null;
        foreach (var work in _context.Store.ReadWork(selection))
        {
            var notice = work.Notice;
            _activeBranch = notice.Path;
            _progress(notice.Path, false);
            // Refuse the primary branch before its own observation; a walked child is checked in the loop below.
            var primaryKey = PathNames.KeyFor(notice.Side, notice.Path);
            RefuseBeforeProvider(notice, work.Before);
            var saved = work.Before;
            var first = !_visited.ContainsKey(primaryKey);
            Entry? current = null;
            ItemFailedException? readFailure = null;
            try
            {
                current = !first || notice.IdentityDeleted ? null
                    : later is { Key: { } seen } && seen == primaryKey ? later.Value.Entry
                    : await _context.For(notice.Side).ReadOneAsync(notice.Path, ct).ConfigureAwait(false);
                if (first && current is null)
                {
                    (var moved, later) = await RelocateAsync(selection, notice, primaryKey, later, ct).ConfigureAwait(false);
                    if (moved is not null) notice = notice with { Path = (current = moved).Path, Observed = moved };
                }
            }
            catch (ItemFailedException error) { readFailure = error; }
            // Classified from this observation, before the entry's own sync records it.
            var batch = _batches && first && current is { IsFolder: true } && !notice.IdentityDeleted &&
                NeedsBatch(selection, notice, current, work);
            if (batch && !work.DiscoveryRequired) _context.Store.RequireDiscovery(selection, work.WorkId);
            yield return (work with { Notice = notice, Before = saved }, readFailure, work.Notice,
                first && !notice.IdentityDeleted && readFailure is null
                    ? (notice.Path, current) : null);
            if (readFailure is not null) continue;
            var oldPath = notice.OldPath ?? saved?.PathOn(notice.Side);
            if (oldPath is not null && !string.Equals(oldPath, notice.Path, StringComparison.Ordinal))
                yield return (work with
                {
                    Notice = notice with { Path = oldPath, OldPath = null, Id = null, Observed = null,
                        IdentityDeleted = false, LocalContentDirty = false },
                    Before = notice.Id is null ? saved : _context.Store.SyncedById(selection.PairId, notice.Side, notice.Id)
                }, null, notice, null);
            if (!first || current is not { IsFolder: true } || notice.IdentityDeleted) continue;
            // After the first Repair a folder's contents come from one add-only batch (D-1105-22), not this walk.
            if (_batches)
            {
                if (!batch) continue;
                ItemFailedException? batchFailure = null;
                try
                {
                    await (_runBatch ?? throw new InvalidOperationException())(selection with { Peer = _peer }, notice.Side, notice.Path, null, false, ct).ConfigureAwait(false);
                    _batched.Add(PathNames.Key(notice.Path));
                }
                catch (InventoryIncompleteException error)
                {
                    batchFailure = ActionExecutor.InventoryFailure(error, notice.Path, notice.Side);
                }
                catch (ItemFailedException error) { batchFailure = error; }
                // As a failed walk: the folder keeps one retained failure and its selection retries.
                if (batchFailure is not null) yield return (work with { Notice = notice }, batchFailure, null, null);
                continue;
            }
            // Only a Local walk of a plain folder can prove access; a skipped folder or a Dropbox notice proves nothing.
            var recorded = notice.Side == Side.Local && current.Skip is null
                ? _context.Store.UnreadableUnder(selection.PairId, PathNames.Key(current.Path)) : [];
            var options = new ListOptions(Recursive: true, IncludeOutOfScope: false, IncludeDeleted: false);
            if (recorded.Count == 0)
            {
                await foreach (var branch in ListAsync(work, current.Path, options, ct).ConfigureAwait(false))
                    yield return (branch.Work, branch.Failure, null, null);
                continue;
            }
            var recordedKeys = recorded.ToHashSet(StringComparer.Ordinal);
            var acquired = new Dictionary<string, (string Path, string Id)>(StringComparer.Ordinal);
            var failed = false;
            await foreach (var branch in ListAsync(work, current.Path, options with
            {
                DirectoryAcquired = (path, id) =>
                {
                    if (PathNames.Key(path) is var key && recordedKeys.Contains(key)) acquired[key] = (path, id);
                }
            }, ct).ConfigureAwait(false))
            {
                failed |= branch.Failure is not null;
                yield return (branch.Work, branch.Failure, null, null);
            }
            if (failed) continue;
            var released = new List<(Side Side, string Key)>();
            foreach (var (key, proof) in acquired)
            {
                ct.ThrowIfCancellationRequested();
                if (_context.Store.ReleaseUnreadable(selection.PairId, selection.PairGeneration, key, proof.Id))
                    released.Add((Side.Local, key));
            }
            // Fresh ordinary branches for the released footprint only; the option is null, so one continuation.
            foreach (var (_, key) in PathNames.DisjointRoots(released))
            {
                var path = acquired[key].Path;
                Entry? entry = null;
                ItemFailedException? failure = null;
                try { entry = await _context.Local.ReadOneAsync(path, ct).ConfigureAwait(false); }
                catch (ItemFailedException error) { failure = error; }
                if (failure is not null)
                {
                    yield return (work with { Notice = work.Notice with { Side = Side.Local, Path = path,
                        Observed = null, Id = acquired[key].Id }, Before = null }, failure, null, null);
                    continue;
                }
                if (entry is null) continue;
                var root = work with { Notice = ChildNotice(Side.Local, path, entry), Before = null };
                yield return (root, null, null, null);
                if (!entry.IsFolder) continue;
                await foreach (var branch in ListAsync(root, path, options, ct).ConfigureAwait(false))
                    yield return (branch.Work, branch.Failure, null, null);
            }
        }
    }

    // From the current observation: a known folder (same id) that was only touched, or that moved under its own rename,
    // brings no new contents; any other folder is new or replaced and its contents are added.
    private bool NeedsBatch(WorkSelection selection, ChangeNotice notice, Entry current, PendingChange work)
    {
        if (current.Skip is not null || _batched.Covers(PathNames.Key(notice.Path))) return false;
        // Retry releases the retained subtree even though its root was recorded before discovery failed.
        if (work.IsResidual || work.DiscoveryRequired) return true;
        var known = _context.Store.SyncedById(selection.PairId, notice.Side, current.Id);
        return known is not { IsFolder: true } || known.PathOn(notice.Side) == notice.Path &&
            (notice.Kind != ChangeKind.Changed ||
                _context.Store.UnreadableUnder(selection.PairId, PathNames.Key(notice.Path)).Count != 0);
    }

    private static ChangeNotice ChildNotice(Side side, string path, Entry? entry) =>
        new(side, ChangeKind.Changed, path, null, entry?.Id, entry, false, false);

    // A held branch never reaches the fence: it attaches to its holder, so refusing it early would replace that.
    // The notice's own side and path are the evidence here: an unobserved notice still names the path it is about.
    private void RefuseBeforeProvider(ChangeNotice notice, SyncedEntry? before)
    {
        var path = notice.Observed?.Path ?? notice.Path;
        if (_rows.Covers(PathNames.KeyFor(notice.Side, path))) return;
        _ancestors.RequireClear(notice, before, _parents);
    }

    // One pass over one inventory enumerator: it must retain each failure and dispose the enumerator once.
    // Every incomplete walk retains its folder's own Failure rather than stopping unrelated pairs.
    private async IAsyncEnumerable<(PendingChange Work, ItemFailedException? Failure)> ListAsync(PendingChange work,
        string path, ListOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var walk = _context.For(work.Notice.Side).ReadAllAsync(path, options, ct).GetAsyncEnumerator(ct);
        var disposed = false;
        ItemFailedException? failure = null;
        try
        {
            while (true)
            {
                bool more;
                try { more = await walk.MoveNextAsync().ConfigureAwait(false); }
                catch (InventoryIncompleteException error)
                {
                    more = false;
                    failure = ActionExecutor.InventoryFailure(error, path, work.Notice.Side);
                }
                if (!more) break;
                ct.ThrowIfCancellationRequested();
                if (walk.Current.Entry is not { } entry) continue;
                yield return (work with { Notice = ChildNotice(work.Notice.Side, walk.Current.Path, entry), Before = null }, null);
            }
            disposed = true;
            try { await walk.DisposeAsync().ConfigureAwait(false); }
            catch (InventoryIncompleteException error)
            {
                failure ??= ActionExecutor.InventoryFailure(error, path, work.Notice.Side);
            }
            ct.ThrowIfCancellationRequested();
            if (failure is not null) yield return (work, failure);
        }
        finally
        {
            if (!disposed)
            {
                try { await walk.DisposeAsync().ConfigureAwait(false); }
                catch (InventoryIncompleteException) when (ct.IsCancellationRequested)
                {
                    ct.ThrowIfCancellationRequested();
                    throw;
                }
            }
        }
    }
}

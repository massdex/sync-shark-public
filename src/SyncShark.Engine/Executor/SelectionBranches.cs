using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Streams primary, old-path and discovered branches with their exact acquisition failures.</summary>
internal readonly struct SelectionBranches(SelectionPass _pass, ExecutorContext _context)
{
    internal async IAsyncEnumerable<(PendingChange Work, ItemFailedException? Failure, ChangeNotice? Original,
        (string Path, Entry? Entry)? FreshPrimary, bool Excluded, bool Gone)> ReadBranchesAsync(
        WorkSelection selection, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var branch in EnumerateBranchesAsync(selection, ct).ConfigureAwait(false))
        {
            var row = Stage(branch.Work);
            _pass.ActiveRow = row;
            _pass.Progress(row, false);
            try { yield return branch; }
            finally
            {
                _pass.ActiveRow = null;
                _pass.Progress(row, true);
            }
        }
    }

    private static RowStage Stage(PendingChange work) => Stage(work.Notice, work.WorkId);

    private static RowStage Stage(ChangeNotice notice, long workId) => new(notice.Side, workId, notice.Path);

    // An absent primary follows its identity: Local by id within scope; Dropbox to the selection's own last member
    // (B-101, D19), whose membership read and point read happen once per selection and are returned for reuse.
    private async Task<(Entry? Moved, (string? Key, Entry? Entry)? Later)> RelocateAsync(WorkSelection selection,
        ChangeNotice notice, string primaryKey, (string? Key, Entry? Entry)? later,
        Dictionary<string, IdentityRead> identities, CancellationToken ct)
    {
        if (notice is not { IdentityDeleted: false, Id: { } id }) return (null, later);
        // B-179: a by-id read this selection already made answers here too.
        if (notice.Side == Side.Local)
            return (identities.TryGetValue(id, out var known) && known.Read ? known.Here
                : _context.Local is IIdentitySide side && await side.ReadByIdAsync(id, ct)
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

    // B-179: one by-id answer for a selection's identity. Read is false when no read was made or it failed; Here is
    // the identity's current in-scope entry when it is present inside the pair.
    private readonly record struct IdentityRead(bool Read, bool Gone, Entry? Here);

    // B-179: the by-id probe of an unsaved Local file branch: an identity with no stored preimage and no saved
    // binding, no old path, not residual, discovery-owed, identity-deleted (its path is never read) or a folder.
    // Gone needs a proven absence: ReadByIdDocumentedAsync answers null only for file or path not found, no links
    // left, or an 87 confirmed by a control open of the root (LocalSide.ReadByIdDocumentedAsync,
    // NtfsIdentityPaths.Retired). Present, in scope or not, and every failure (locked, unreadable, volume error) take
    // today's path; a probe failure is never a branch failure or an issue. One answer serves every member of the
    // identity in this selection.
    private async Task<IdentityRead> ProbeAsync(WorkSelection selection, PendingChange work,
        Dictionary<string, IdentityRead> identities, CancellationToken ct)
    {
        if (work is { IsResidual: true } or { DiscoveryRequired: true } || work.Before is not null ||
            work.Notice is not { Side: Side.Local, OldPath: null, IdentityDeleted: false, Id: { } id } notice ||
            notice.Observed?.IsFolder == true || _context.Local is not IIdentitySide side)
            return default;
        if (identities.TryGetValue(id, out var known)) return known;
        // The binding check is the one indexed read that keeps a concurrently saved identity on its delete path.
        if (_context.Store.SyncedById(selection.PairId, Side.Local, id) is not null) known = default;
        else
            try
            {
                known = await side.ReadByIdDocumentedAsync(id, ct).ConfigureAwait(false) is { } found
                    ? new(true, false, found.InScope ? found.Location.Entry : null) : new(true, true, null);
            }
            catch (Exception error) when (error is not OperationCanceledException) { known = default; }
        return identities[id] = known;
    }

    private async IAsyncEnumerable<(PendingChange Work, ItemFailedException? Failure, ChangeNotice? Original,
        (string Path, Entry? Entry)? FreshPrimary, bool Excluded, bool Gone)> EnumerateBranchesAsync(
        WorkSelection selection,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        (string? Key, Entry? Entry)? later = null;
        var identities = new Dictionary<string, IdentityRead>(StringComparer.Ordinal);
        Func<Side, string, string?, bool>? separateWork = null;
        foreach (var work in _context.Store.ReadWork(selection))
        {
            var notice = work.Notice;
            _pass.ActiveRow = Stage(work);
            // A failure before the yield (fence, relocation read) names this branch, never the selection frame.
            _pass.RefusedRow = _pass.ActiveRow;
            _pass.Progress(_pass.ActiveRow.Value, false);
            // Refuse the primary branch before its own observation; a walked child is checked in the loop below.
            var primaryKey = PathNames.KeyFor(notice.Side, notice.Path);
            _pass.RefuseBeforeProvider(notice, work.Before);
            var saved = work.Before;
            var first = !_pass.Visited.ContainsKey(primaryKey);
            var probe = await ProbeAsync(selection, work, identities, ct).ConfigureAwait(false);
            Entry? current = null;
            ItemFailedException? readFailure = null;
            var gone = false;
            string? leftBehind = null;
            try
            {
                current = !first || notice.IdentityDeleted ? null
                    : later is { Key: { } seen } && seen == primaryKey ? later.Value.Entry
                    : await _context.For(notice.Side).ReadOneAsync(notice.Path, ct).ConfigureAwait(false);
                // B-179: the path's other occupant may have no row of its own (an omitted acquisition); this branch
                // keeps observing it then, as before the probe. For a probed notice null is read-empty or observed.
                var occupantOwned = current is null || current.Id == notice.Id ||
                    !probe.Gone && probe.Here is null ||
                    (separateWork ??= _context.Store.CreateSeparateWorkProbe(selection))(notice.Side, primaryKey, current.Id);
                // B-179: an unsaved Local identity proven gone, with nothing unowned at its path, owes nothing here:
                // the branch is yielded as gone and the selection completes through CompleteWork.
                gone = probe.Gone && occupantOwned;
                // B-179: an unsaved identity now elsewhere follows itself even when another item holds its old path.
                if (first && current is not null && current.Id != notice.Id && probe.Here is { } here)
                {
                    if (!occupantOwned) leftBehind = notice.Path;
                    notice = notice with { Path = (current = here).Path, Observed = here };
                    _pass.ActiveRow = _pass.RefusedRow = Stage(notice, work.WorkId);
                }
                if (!gone && first && current is null)
                {
                    (var moved, later) = await RelocateAsync(selection, notice, primaryKey, later, identities, ct).ConfigureAwait(false);
                    if (moved is not null)
                    {
                        notice = notice with { Path = (current = moved).Path, Observed = moved };
                        // The relocated subject, not the notice's stale path, is what later refusals name.
                        _pass.ActiveRow = _pass.RefusedRow = Stage(notice, work.WorkId);
                    }
                }
            }
            catch (ItemFailedException error) { readFailure = error; }
            if (gone && false)
            {
                yield return (work, null, null, null, false, true);
                continue;
            }
            // One scope answer for this first observation, from the fresh primary only: it gates the batch and walk
            // and settles an unbound Local entry early. PathScope stays the only exclusion owner.
            var excluded = first && current is not null &&
                SelectionPass.ExcludedForAdmission(_context.Scope, notice.Side, current, notice);
            // An excluded Local root keeps its batch or walk when a recorded member still needs releasing.
            var unreadableUnder = excluded && current is { IsFolder: true } && current.Skip is null && notice.Side == Side.Local
                ? _context.Store.UnreadableUnder(selection.PairId, PathNames.Key(current.Path)) : null;
            var releaseUnreadable = unreadableUnder is { Count: > 0 };
            // Classified from this observation, before the entry's own sync records it.
            var batch = _pass.Batches && first && current is { IsFolder: true } && !notice.IdentityDeleted &&
                (!excluded || releaseUnreadable) && NeedsBatch(selection, notice, current, work);
            if (batch && !work.DiscoveryRequired) _context.Store.RequireDiscovery(selection, work.WorkId);
            yield return (work with { Notice = notice, Before = saved }, readFailure, work.Notice,
                first && !notice.IdentityDeleted && readFailure is null
                    ? (notice.Path, current) : null, excluded, false);
            if (readFailure is not null) continue;
            var oldPath = notice.OldPath ?? saved?.PathOn(notice.Side) ?? leftBehind;
            if (oldPath is not null && !string.Equals(oldPath, notice.Path, StringComparison.Ordinal))
                yield return (work with
                {
                    Notice = notice with { Path = oldPath, OldPath = null, Id = null, Observed = null,
                        IdentityDeleted = false, LocalContentDirty = false },
                    Before = notice.Id is null ? saved : _context.Store.SyncedById(selection.PairId, notice.Side, notice.Id)
                }, null, notice, null, false, false);
            if (!first || current is not { IsFolder: true } || notice.IdentityDeleted ||
                excluded && !releaseUnreadable) continue;
            // After the first Repair a folder's contents come from one add-only batch (D-1105-22), not this walk.
            if (_pass.Batches)
            {
                if (!batch) continue;
                ItemFailedException? batchFailure = null;
                try
                {
                    await (_pass.RunBatch ?? throw new InvalidOperationException())(selection with { Peer = _pass.Peer }, notice.Side, notice.Path, null, false, ct).ConfigureAwait(false);
                    _pass.Batched.Add(PathNames.Key(notice.Path));
                }
                catch (InventoryIncompleteException error)
                {
                    batchFailure = ItemFailures.InventoryFailure(error, notice.Path, notice.Side);
                }
                catch (ItemFailedException error) { batchFailure = error; }
                // As a failed walk: the folder keeps one retained failure and its selection retries.
                if (batchFailure is not null) yield return (work with { Notice = notice }, batchFailure, null, null, false, false);
                continue;
            }
            // Only a Local walk of a plain folder can prove access; a skipped folder or a Dropbox notice proves nothing.
            var recorded = notice.Side == Side.Local && current.Skip is null
                ? unreadableUnder ?? _context.Store.UnreadableUnder(selection.PairId, PathNames.Key(current.Path)) : [];
            var options = new ListOptions(Recursive: true, IncludeOutOfScope: false, IncludeDeleted: false);
            if (recorded.Count == 0)
            {
                await foreach (var branch in ListAsync(work, current.Path, options, ct).ConfigureAwait(false))
                    yield return (branch.Work, branch.Failure, null, null, false, false);
                continue;
            }
            var unreadable = new SelectionUnreadable(recorded);
            var failed = false;
            await foreach (var branch in ListAsync(work, current.Path, options with
            {
                DirectoryAcquired = unreadable.Acquire
            }, ct).ConfigureAwait(false))
            {
                failed |= branch.Failure is not null;
                yield return (branch.Work, branch.Failure, null, null, false, false);
            }
            if (failed) continue;
            var released = unreadable.Release(_context.Store, selection.PairId, selection.PairGeneration, ct);
            // Fresh ordinary branches for the released footprint only; the option is null, so one continuation.
            foreach (var (_, key) in PathNames.DisjointRoots(released))
            {
                var path = unreadable[key].Path;
                Entry? entry = null;
                ItemFailedException? failure = null;
                try { entry = await _context.Local.ReadOneAsync(path, ct).ConfigureAwait(false); }
                catch (ItemFailedException error) { failure = error; }
                if (failure is not null)
                {
                    yield return (work with { Notice = work.Notice with { Side = Side.Local, Path = path,
                        Observed = null, Id = unreadable[key].Id }, Before = null }, failure, null, null, false, false);
                    continue;
                }
                if (entry is null) continue;
                var root = work with { Notice = ChildNotice(Side.Local, path, entry), Before = null };
                yield return (root, null, null, null, false, false);
                if (!entry.IsFolder) continue;
                await foreach (var branch in ListAsync(root, path, options, ct).ConfigureAwait(false))
                    yield return (branch.Work, branch.Failure, null, null, false, false);
            }
        }
    }

    // From the current observation: a known folder (same id) that was only touched, or that moved under its own rename,
    // brings no new contents; any other folder is new or replaced and its contents are added.
    private bool NeedsBatch(WorkSelection selection, ChangeNotice notice, Entry current, PendingChange work)
    {
        if (current.Skip is not null || _pass.Batched.Covers(PathNames.Key(notice.Path))) return false;
        // Retry releases the retained subtree even though its root was recorded before discovery failed.
        if (work.IsResidual || work.DiscoveryRequired) return true;
        var known = _context.Store.SyncedById(selection.PairId, notice.Side, current.Id);
        return known is not { IsFolder: true } || known.PathOn(notice.Side) == notice.Path &&
            (notice.Kind != ChangeKind.Changed ||
                _context.Store.UnreadableUnder(selection.PairId, PathNames.Key(notice.Path)).Count != 0);
    }

    internal static ChangeNotice ChildNotice(Side side, string path, Entry? entry) =>
        new(side, ChangeKind.Changed, path, null, entry?.Id, entry, false, false);

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
                    failure = ItemFailures.InventoryFailure(error, path, work.Notice.Side);
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
                failure ??= ItemFailures.InventoryFailure(error, path, work.Notice.Side);
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

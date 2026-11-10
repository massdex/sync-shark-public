using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Streams primary, old-path and discovered branches with their exact acquisition failures.</summary>
internal readonly struct SelectionBranches(SelectionPass _pass, ExecutorContext _context)
{
    internal async IAsyncEnumerable<(PendingChange Work, ItemFailedException? Failure, ChangeNotice? Original,
        (string Path, Entry? Entry)? FreshPrimary)> ReadBranchesAsync(
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

    private async IAsyncEnumerable<(PendingChange Work, ItemFailedException? Failure, ChangeNotice? Original,
        (string Path, Entry? Entry)? FreshPrimary)> EnumerateBranchesAsync(
        WorkSelection selection,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        (string? Key, Entry? Entry)? later = null;
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
                    if (moved is not null)
                    {
                        notice = notice with { Path = (current = moved).Path, Observed = moved };
                        // The relocated subject, not the notice's stale path, is what later refusals name.
                        _pass.ActiveRow = _pass.RefusedRow = Stage(notice, work.WorkId);
                    }
                }
            }
            catch (ItemFailedException error) { readFailure = error; }
            // Classified from this observation, before the entry's own sync records it.
            var batch = _pass.Batches && first && current is { IsFolder: true } && !notice.IdentityDeleted &&
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
            var unreadable = new SelectionUnreadable(recorded);
            var failed = false;
            await foreach (var branch in ListAsync(work, current.Path, options with
            {
                DirectoryAcquired = unreadable.Acquire
            }, ct).ConfigureAwait(false))
            {
                failed |= branch.Failure is not null;
                yield return (branch.Work, branch.Failure, null, null);
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
                        Observed = null, Id = unreadable[key].Id }, Before = null }, failure, null, null);
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

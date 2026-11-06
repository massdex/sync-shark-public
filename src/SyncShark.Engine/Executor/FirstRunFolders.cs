using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Settles original first-run folders after their children and current readable contents permit it.</summary>
internal sealed class FirstRunFolders(ExecutorContext _context, Parents _parents, ActionExecutor _executor, ItemSync _items)
{
    internal async Task<SyncOutcome> FinalizeFirstRunFolderAsync(Row folder, bool unfinishedChildren, CancellationToken ct, Action<Row, SyncOutcome>? completed)
    {
        if (folder.Local is not { IsFolder: true }) throw new ArgumentException(nameof(folder));
        var row = folder;
        var observer = new Observer(_context, _parents);
        SyncOutcome? outcome = null;
        await using (var work = await _context.Admission.EnterWorkAsync(false, ct))
        await using (var permit = await _context.EnterOperationAsync(ct))
        {
            var paths = Row.Paths(row);
            var lockPaths = new List<(Side Side, string Path)> { (Side.Local, paths.Local), (Side.Dropbox, paths.Dropbox) };
            if (row.Synced is { } saved)
            {
                lockPaths.Add((Side.Local, saved.LocalPath));
                lockPaths.Add((Side.Dropbox, saved.DropboxPath));
            }
            IReadOnlyCollection<string> held = LockKeys.ForPaths(_context.Pair, lockPaths);
            Row? lastStale = null;
            while (outcome is null)
            {
                await using var hold = await PathLocks.Shared.EnterAsync(held, ct);
                row = await _items.ReobserveAsync(row, observer, ct);
                if (!IssuePolicy.SameFirstRunLocal(folder.Local, row.Local)) break;
                if (unfinishedChildren ||
                    _context.Store.UnreadableTouches(_context.Pair.PairId, PathNames.Key(row.Local!.Path), true))
                {
                    outcome = await _executor.ExecuteAsync(Decide.Held(folder), null, ct);
                    break;
                }
                SyncAction action;
                if (row.Synced is not null || row.Dropbox is not null)
                    action = await _items.PrepareAsync(row, ct);
                else
                {
                    var empty = true;
                    var ignored = false;
                    var covered = true;
                    try
                    {
                        await foreach (var listed in _context.Local.ReadAllAsync(row.Local!.Path, new ListOptions(true, true), ct))
                        {
                            if (listed.Entry is not { } entry) continue;
                            empty = false;
                            if (entry.Skip is not null)
                            {
                                ignored = true;
                                covered &= _context.Scope.CoversIgnoredContents([entry]);
                                if (IssuePolicy.ProtectsSubtree(entry.Skip))
                                    throw new ItemFailedException(entry.Path, TextKey.ReadLocalFailed, null);
                            }
                        }
                    }
                    catch (Exception error) when (error is InventoryIncompleteException or ItemFailedException)
                    {
                        var failed = folder with { Context = folder.Context with
                        {
                            FirstRunDeletion = folder.Context.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice
                                ? folder.Context.FirstRunDeletion : IssueSubject.FirstRunDeletionKind.UnscannedSubtree
                        } };
                        outcome = await _executor.FailAsync(failed, null, error as ItemFailedException ??
                            ActionExecutor.InventoryFailure((InventoryIncompleteException)error, row.Local.Path, Side.Local), ct);
                        break;
                    }
                    row = row with { Context = row.Context with { Destination = _parents.Destination(Side.Dropbox, row.Local.Path) } };
                    if (row.Context.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice)
                        action = Decide.ResumePreservedFolder(row, _context.Pair.FirstRun, _context.Time.GetUtcNow());
                    else
                    {
                        action = FolderDeletes.FirstRun(row, empty, ignored, covered);
                        if (action.Kind == ActionKind.NeedDeletionEvidence)
                        {
                            row = row with { Deletion = await _context.ReadDeletionEvidenceAsync(action.Target!, true, ct) };
                            if (row.Deletion is DeletionEvidence.LiveAtTarget live)
                            {
                                row = IssuePolicy.ReobserveFirstRun(row, row with { Dropbox = live.Location.Entry });
                                action = await _items.PrepareAsync(row, ct);
                            }
                            else if (row.Deletion is DeletionEvidence.LiveElsewhere)
                                throw new ChangedUnderneathException(Row.Path(row));
                            else action = FolderDeletes.FirstRun(row, empty, ignored, covered);
                        }
                    }
                }
                if (action.Kind is ActionKind.None or ActionKind.Notice)
                {
                    outcome = await _executor.ExecuteAsync(action, null, ct, firstRunFolder: action.IsInSync() ? folder : null);
                    break;
                }
                var required = LockKeys.For(action, _context.Pair, null);
                var result = await _items.ExecuteLockedAsync(action, null, required, held, ct, null, null, folder);
                if (result.Outcome is { } finished) { outcome = finished; break; }
                // The iteration disposes its hold before re-entry; the outer permit remains singly owned.
                if (lastStale is not null && lastStale.Local == row.Local && lastStale.Dropbox == row.Dropbox &&
                    lastStale.Synced == row.Synced && required.All(key => held.Contains(key, PathLocks.KeyComparer)))
                    throw new ChangedUnderneathException(Row.Path(row));
                lastStale = row;
                held = required;
            }
        }
        if (outcome is null)
            return await _items.RunRowAttemptAsync(row, ct, completed: completed);
        completed?.Invoke(row, outcome);
        return outcome;
    }
}

using System.Runtime.CompilerServices;
using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Applies, previews and removes exclusions (section 7.7, D26).</summary>
internal sealed class ExclusionRunner(ExecutorContext context, Parents parents, ActionExecutor actions,
    KeepBothRunner keepBoth, ItemSync items)
{
    /// <summary>Syncs before removal; false leaves the exclusion pending while preserving completed copies.</summary>
    // Ordered exclusion application delegates copy preservation and retirement while retaining one refusal boundary.
    public async Task<bool> ApplyAsync(Exclusion exclusion,
        IReadOnlySet<long>? retryIssues, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (exclusion.Keep) return true;
        await using var permit = await context.EnterOperationAsync(ct);
        await using var hold = await HoldAsync(exclusion, ct);
        Entry? entry = null;
        try
        {
            entry = await ReadTargetAsync(exclusion, ct);
            if (entry is null) return true;
            var scope = context.Scope.Without(exclusion);
            var protection = context.Scope.ProtectionWithout(exclusion);
            var root = Inspect(exclusion, entry, scope, protection);
            var rootRow = RowFor(exclusion.Side, entry, exclusion.Path, root.Key, root.Saved);
            if (context.Store.HeldKind(context.Pair.PairId, rootRow, retryIssues) is not null) return false;
            var forget = Forget(exclusion, root.Key, root.Saved);
            ActionExecutor.RequireReadable(context.Store, context.Pair.PairId, exclusion.Path, forget,
                entry.IsFolder || root.Saved is { IsFolder: true }, ct);
            var nested = HoldsNestedRoot(exclusion, entry, scope);
            if (nested || root.Protected)
            {
                await actions.ExecuteAsync(Exclusions.Blocked(rootRow), null, ct);
                return false;
            }

            using var contents = OpenPlan(exclusion, entry);
            await CaptureAsync(contents, exclusion, entry, ct);
            foreach (var item in contents.Entries(ct))
            {
                if (!Inspect(exclusion, item, scope, protection).Protected) continue;
                await actions.ExecuteAsync(Exclusions.Blocked(rootRow), null, ct);
                return false;
            }

            if (!await PreserveCopiesAsync(exclusion, entry, rootRow, contents, scope, protection, retryIssues, ct))
                return false;
            if (entry.IsFolder)
            {
                // The folder boundary reacquires admission and path locks, so release this operation's hold first.
                await hold.DisposeAsync();
                await permit.DisposeAsync();
                var folderRow = rootRow with
                {
                    Synced = context.Store.SyncedByKey(context.Pair.PairId, exclusion.Side, root.Key) ?? root.Saved
                };
                var removal = await items.Folders.RemoveExcludedAsync(folderRow, exclusion, contents, ct);
                return removal?.Status == FolderRemovalStatus.Complete;
            }
            await RetireFileAsync(exclusion, entry, rootRow, root.Saved, forget, ct);
            return true;
        }
        catch (Exception error) when (ItemFailures.IsItemError(error))
        {
            await actions.Failures.FailAsync(RowFor(exclusion.Side, entry, exclusion.Path), null, error, ct);
            return false;
        }
    }

    private async ValueTask<bool> PreserveCopiesAsync(Exclusion exclusion, Entry root, Row rootRow,
        FolderPlan contents, PathScope scope, PathScope protection, IReadOnlySet<long>? retryIssues, CancellationToken ct)
    {
        var failed = false;
        var dependent = new PrefixSet();
        Observer? observer = null;
        foreach (var item in contents.Entries(ct))
        {
            ct.ThrowIfCancellationRequested();
            var nativeKey = PathNames.Key(item.Path);
            if (dependent.Covers(nativeKey)) continue;
            var facts = Inspect(exclusion, item, scope, protection, nativeKey);
            if (facts.Protected)
            {
                failed = true;
                if (item.IsFolder) dependent.Add(nativeKey);
                await actions.ExecuteAsync(Exclusions.Blocked(rootRow), null, ct);
                continue;
            }
            if (!facts.Copy) continue;
            var row = RowFor(exclusion.Side, item, item.Path, facts.Key, facts.Saved);
            if (context.Store.HeldKind(context.Pair.PairId, row, retryIssues) is not null)
            {
                failed = true;
                if (item.IsFolder) dependent.Add(nativeKey);
                continue;
            }
            SyncAction? action = null;
            LocalEvidence.DirtyProof? owned = null;
            try
            {
                if (facts.Saved is not null)
                {
                    observer ??= new Observer(context, parents);
                    row = await observer.ObserveAsync(exclusion.Side, item.Path, ct);
                    row = row with { Context = CopyContext(row, scope) };
                    action = await items.Preparation.PrepareAsync(row, ct);
                    row = action.Row;
                    owned = context.Evidence.CaptureIfHashed(row);
                }
                else row = row with { Context = row.Context with { Destination = facts.Other } };
                // Null preserves a kept-side edit or deletion without executing an issue-closing no-op.
                action = Exclusions.For(exclusion.Side, item, row, action);
                if (action is null) continue;
                var labels = action.Kind == ActionKind.KeepBoth
                    ? await keepBoth.ChooseLabelsAsync(row, ct, PathNames.Other(exclusion.Side)) : null;
                // A successful root copy alone does not complete a still-pending exclusion.
                var keys = LockKeys.For(action, context.Pair, labels);
                var result = await items.Admission.ExecuteLockedAsync(action, labels, keys, keys, ct, null, null,
                    closeIssues: !PathNames.Same(item.Path, root.Path));
                if (result.Outcome is { } outcome && !outcome.RaisedBlocks() && !outcome.Deferred) continue;
                if (result.Outcome is null) throw new ChangedUnderneathException(item.Path);
            }
            catch (Exception error) when (ItemFailures.IsItemError(error))
            {
                await actions.Failures.FailAsync(row, action, error, ct);
            }
            finally { context.Evidence.Release(row, owned); }
            failed = true;
            if (item.IsFolder) dependent.Add(nativeKey);
        }
        return !failed;
    }

    private async ValueTask RetireFileAsync(Exclusion exclusion, Entry entry, Row rootRow,
        SyncedEntry? originalSaved, IReadOnlyList<(Side Side, string Key)> forget, CancellationToken ct)
    {
        var saved = context.Store.SyncedByKey(context.Pair.PairId, exclusion.Side, rootRow.Key) ?? originalSaved;
        var selection = await actions.SelectRemovalAsync(rootRow with { Synced = saved }, exclusion.Side, entry, ct);
        // Applied retirement acknowledges the exact Local item; a pending exclusion acknowledges nothing.
        IssueItem[] acknowledged = [];
        if (exclusion.Side == Side.Local)
            acknowledged = [new IssueItem(Side.Local, entry.Path, entry.Id)];
        else if (saved is { } binding)
            acknowledged = [new IssueItem(Side.Local, binding.LocalPath, binding.LocalId)];
        var removed = await actions.RemoveAsync(exclusion.Side, entry, selection,
            StateChange.Empty with
            {
                ForgetSelf = forget,
                Close = [(exclusion.Path, IssueClosure.AfterEffect)],
                FirstRunAcknowledged = acknowledged
            }, ct);
        if (removed.Outcome != RemovalOutcome.Removed)
            throw new ItemFailedException(exclusion.Path, removed.Cause ?? TextKey.FailChanged, null);
    }

    /// <summary>Observes only the configured target; the caller owns admission and any effect locks.</summary>
    internal Task<Entry?> ReadTargetAsync(Exclusion exclusion, CancellationToken ct) =>
        context.For(exclusion.Side).ReadOneAsync(exclusion.Path, ct);

    /// <summary>Reports potential copies and removals without hashing or changing durable state.</summary>
    public async Task<ExcludePreview> PreviewAsync(Exclusion exclusion, CancellationToken ct)
    {
        var recovery = exclusion.Side == Side.Local ? TextKey.RecoveryRecycleBin : TextKey.RecoveryDropboxHistory;
        ct.ThrowIfCancellationRequested();
        if (exclusion.Keep)
            return new ExcludePreview(exclusion.Side, exclusion.Path, 0, 0, 0, 0, recovery, false);
        await using var permit = await context.EnterOperationAsync(ct);
        await using var hold = await HoldAsync(exclusion, ct);
        var entry = await context.For(exclusion.Side).ReadOneAsync(exclusion.Path, ct);
        if (entry is null)
            return new ExcludePreview(exclusion.Side, exclusion.Path, 0, 0, 0, 0, recovery, false);

        var scope = context.Scope.Without(exclusion);
        var protection = context.Scope.ProtectionWithout(exclusion);
        var nested = HoldsNestedRoot(exclusion, entry, scope);
        using var contents = nested ? null : OpenPlan(exclusion, entry);
        if (contents is not null) await CaptureAsync(contents, exclusion, entry, ct);
        var copy = (Count: 0, Bytes: 0L);
        var remove = (Count: 0, Bytes: 0L);
        var blocked = nested;
        foreach (var item in contents?.Entries(ct) ?? [entry])
        {
            var facts = Inspect(exclusion, item, scope, protection);
            blocked |= facts.Protected;
            if (item.IsFolder) continue;
            if (facts.Copy && Exclusions.MayCopyFile(item, facts.Saved, exclusion.Side))
            {
                copy.Count++;
                copy.Bytes = checked(copy.Bytes + item.Size);
            }
            remove.Count++;
            remove.Bytes = checked(remove.Bytes + item.Size);
        }
        return new ExcludePreview(exclusion.Side, exclusion.Path, copy.Count, copy.Bytes,
            remove.Count, remove.Bytes, recovery, blocked);
    }

    // D26: the exclusion being applied must not stop its own preservation copy, so the row is judged over the scope
    // without it. Holds were asked before this observation; the observed skip and destination stay.
    private static RowContext CopyContext(Row row, PathScope scope) => RowContext.ForRow(row, scope, RowContext.Neutral,
        row.Context.Skipped, null, null, false, IssueSubject.FirstRunDeletionKind.None) with
    {
        Destination = row.Context.Destination
    };

    // The single dirty answer for the observed Local subject; a lost boundary is stale coverage, not clean.
    private Task<IAsyncDisposable> HoldAsync(Exclusion exclusion, CancellationToken ct) =>
        PathLocks.Shared.EnterAsync(LockKeys.ForPaths(context.Pair,
            [(exclusion.Side, exclusion.Path), (PathNames.Other(exclusion.Side), parents.Destination(PathNames.Other(exclusion.Side), exclusion.Path))]), ct);

    private bool HoldsNestedRoot(Exclusion exclusion, Entry entry, PathScope scope) =>
        scope.HoldsNestedRoot(exclusion.Side, entry.Path) ||
        scope.HoldsNestedRoot(PathNames.Other(exclusion.Side), parents.Destination(PathNames.Other(exclusion.Side), entry.Path));

    private FolderPlan OpenPlan(Exclusion exclusion, Entry entry)
    {
        try
        {
            return context.Store.OpenFolderPlan(context.Pair.PairId,
                RowFor(exclusion.Side, entry, exclusion.Path), exclusion.Side);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ItemFailedException(exclusion.Path, Text.ReadFailedOn(Side.Local), error);
        }
    }

    private async Task CaptureAsync(FolderPlan plan, Exclusion exclusion, Entry entry, CancellationToken ct)
    {
        try
        {
            await plan.CaptureAsync(SourceAsync(exclusion, entry, ct), ct);
        }
        catch (InventoryIncompleteException error)
        {
            throw ItemFailures.InventoryFailure(error, exclusion.Path, exclusion.Side);
        }
    }

    private async IAsyncEnumerable<Listed> SourceAsync(Exclusion exclusion, Entry entry,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new Listed(entry.Path, entry);
        if (!Exclusions.ShouldList(entry)) yield break;
        await foreach (var listed in context.For(exclusion.Side).ReadAllAsync(exclusion.Path,
            new ListOptions(Recursive: true, IncludeOutOfScope: true), ct))
            yield return listed;
    }

    private (string Key, SyncedEntry? Saved, string? Other, bool Copy, bool Protected) Inspect(Exclusion exclusion,
        Entry item, PathScope scope, PathScope protection, string? nativeKey = null)
    {
        var key = PathNames.KeyFor(exclusion.Side, item.Path, nativeKey ?? PathNames.Key(item.Path));
        var saved = context.Store.SyncedByKey(context.Pair.PairId, exclusion.Side, key);
        if (item.Skip is not null) return (key, saved, null, false, true);
        var local = exclusion.Side == Side.Local ? item.Path : parents.Counterpart(Side.Local, item.Path, saved);
        var dropbox = exclusion.Side == Side.Dropbox ? item.Path : parents.Counterpart(Side.Dropbox, item.Path, saved);
        // Ignored entries travel with the folder; an overlapping protection still prevents removal.
        var protectedItem = protection.Excludes(local, dropbox, item.IsFolder);
        return (key, saved, exclusion.Side == Side.Local ? dropbox : local,
            !protectedItem && !scope.Excludes(local, dropbox, item.IsFolder), protectedItem);
    }

    private IReadOnlyList<(Side Side, string Key)> Forget(Exclusion exclusion, string key, SyncedEntry? saved)
    {
        var other = PathNames.Other(exclusion.Side);
        var otherPath = parents.Counterpart(other, exclusion.Path, saved);
        return [(exclusion.Side, key), (other, PathNames.KeyFor(other, otherPath))];
    }

    // An entryless failure names the exception's path; Destination retains the requested exclusion path.
    private static Row RowFor(Side side, Entry? entry, string path, string? key = null, SyncedEntry? saved = null) =>
        Row.ForOneSide(side, key ?? (entry is null ? path : PathNames.KeyFor(side, entry.Path)), entry, saved, [], null,
            RowContext.Neutral with { Destination = path });
}

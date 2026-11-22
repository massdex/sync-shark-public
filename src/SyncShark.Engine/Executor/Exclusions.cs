namespace Massdex.SyncShark.Engine.Executor;

/// <summary>What applying a no-keep exclusion would do (decision 58(h), section 7.7). BlockedByProtected: skipped,
/// other-excluded or nested-pair content prevents the removal.</summary>
public sealed record ExcludePreview(Side Side, string Path, int CopyFiles, long CopyBytes, int RemoveFiles,
    long RemoveBytes, TextKey RecoveryPlace, bool BlockedByProtected);

/// <summary>The classified result of one exclusion application: copies completed and the removal succeeded
/// (Applied), or a block, failure or still-running provider operation left the exclusion pending (Pending), keeping
/// every completed copy.</summary>
public enum ExclusionOutcome { Applied, Pending }

/// <summary>Exclusions operations for the executor module.</summary>
public sealed class Exclusions
{
    /// <summary>Sync eligible changes to the kept side, then remove the excluded copy; Pending keeps completed copies.</summary>
    public async Task<ExclusionOutcome> ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Exclusion exclusion, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (exclusion.Keep) return ExclusionOutcome.Applied;
        var side = exclusion.Side == Side.Local ? local : dropbox;
        var entry = await side.ReadOneAsync(exclusion.Path, ct);
        if (entry is null) return ExclusionOutcome.Applied;
        var preview = await PreviewAsync(pair, exclusion, local, dropbox, store, paths, ct);
        if (preview.BlockedByProtected)
        {
            await RaiseProtectedAsync(store, pair.PairId, exclusion, entry, ct);
            return ExclusionOutcome.Pending;
        }
        if (!await PreserveCopiesAsync(pair, settings, events, paths, exclusion, entry, local, dropbox, store,
                populate, compareOldBinding, ct))
            return ExclusionOutcome.Pending;
        return await RemoveAsync(pair, settings, events, paths, exclusion, entry, local, dropbox, store,
            populate, compareOldBinding, ct);
    }

    /// <summary>Reports the copies and removals an application would make without hashing or changing durable state;
    /// the same eligibility as ApplyAsync.</summary>
    public async Task<ExcludePreview> PreviewAsync(PairConfig pair, Exclusion exclusion, ISide local, ISide dropbox, IStore store, PathScope paths, CancellationToken ct)
    {
        var recovery = exclusion.Side == Side.Local ? TextKey.RecoveryRecycleBin : TextKey.RecoveryDropboxHistory;
        ct.ThrowIfCancellationRequested();
        if (exclusion.Keep) return new ExcludePreview(exclusion.Side, exclusion.Path, 0, 0, 0, 0, recovery, false);
        var side = exclusion.Side == Side.Local ? local : dropbox;
        var entry = await side.ReadOneAsync(exclusion.Path, ct);
        if (entry is null) return new ExcludePreview(exclusion.Side, exclusion.Path, 0, 0, 0, 0, recovery, false);

        // The exclusion being applied must not stop its own preservation copy, so eligibility is judged over the scope
        // without it (D26); protection additionally ignores ignore rules so ignored children travel with the folder.
        var scope = paths.Without(exclusion);
        var protection = paths.ProtectionWithout(exclusion);
        var nested = scope.HoldsNestedRoot(exclusion.Side, entry.Path);
        var blocked = nested;
        int copyFiles = 0, removeFiles = 0;
        long copyBytes = 0, removeBytes = 0;
        await foreach (var item in TargetItemsAsync(side, entry, nested, ct))
        {
            var saved = await store.FindAsync(pair.PairId, exclusion.Side,
                PathNames.KeyFor(exclusion.Side, item.Path), ct);
            var facts = Facts(exclusion, item, scope, protection, saved);
            blocked |= facts.Protected;
            if (item.IsFolder) continue;
            if (facts.Copy && MayCopyFile(item, saved, exclusion.Side))
            {
                copyFiles++;
                copyBytes += item.Size;
            }
            removeFiles++;
            removeBytes += item.Size;
        }
        return new ExcludePreview(exclusion.Side, exclusion.Path, copyFiles, copyBytes, removeFiles, removeBytes,
            recovery, blocked);
    }

    // The target and, when it is a listable folder, its subtree; a refused or nested root is judged on the root alone.
    private static async IAsyncEnumerable<Entry> TargetItemsAsync(ISide side, Entry entry, bool nested,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return entry;
        if (nested || !entry.IsFolder || entry.Skip is not null) yield break;
        await foreach (var listed in side.ReadAllAsync(entry.Path, new ListOptions(true, true), ct))
            if (listed.Entry is { } child) yield return child;
    }

    /// <summary>Copies eligible changes to the kept side; false leaves the exclusion pending and keeps completed copies.</summary>
    private async Task<bool> PreserveCopiesAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Exclusion exclusion, Entry entry, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct)
    {
        var side = exclusion.Side == Side.Local ? local : dropbox;
        var scope = paths.Without(exclusion);
        var protection = paths.ProtectionWithout(exclusion);
        var failed = false;
        await foreach (var item in TargetItemsAsync(side, entry, scope.HoldsNestedRoot(exclusion.Side, entry.Path), ct))
        {
            ct.ThrowIfCancellationRequested();
            var key = PathNames.KeyFor(exclusion.Side, item.Path);
            var saved = await store.FindAsync(pair.PairId, exclusion.Side, key, ct);
            var facts = Facts(exclusion, item, scope, protection, saved);
            if (facts.Protected) { failed = true; continue; }
            if (!facts.Copy) continue;
            if (!item.IsFolder && !MayCopyFile(item, saved, exclusion.Side)) continue;
            var row = Row.ForOneSide(exclusion.Side, key, item, saved, [], null,
                RowContext.Neutral with { Destination = item.Path });
            var action = Override(exclusion, item, row, Planner.Decide.For(row, pair.FirstRun, item.Modified, false));
            // A null action is a kept-side edit or deletion: preserve it without a no-op effect.
            if (action is null) continue;
            await new Effects().ApplyAsync(pair, settings, events, paths, action, local, dropbox, store, populate,
                compareOldBinding, ct);
        }
        return !failed;
    }

    /// <summary>The excluded profile of one decided action, or null to skip it (port of the 0.3.95 override).</summary>
    private static Planner.SyncAction? Override(Exclusion exclusion, Entry item, Row row, Planner.SyncAction decided)
    {
        var copyKind = item.IsFolder
            ? exclusion.Side == Side.Local ? ActionKind.EnsureDropboxFolder : ActionKind.EnsureLocalFolder
            : exclusion.Side == Side.Local ? ActionKind.Upload : ActionKind.Download;
        // The kept side already has the newer state (or the deletion): do not sync it back to the excluded side.
        if (exclusion.Side == Side.Local && decided.Kind is ActionKind.Download or ActionKind.DeleteLocal) return null;
        if (exclusion.Side == Side.Dropbox && decided.Kind is ActionKind.Upload or ActionKind.DeleteDropbox) return null;
        if (decided.Kind == ActionKind.DeferFolder &&
            (exclusion.Side == Side.Local ? row.Dropbox is null : row.Local is null))
            return null;
        // A file/folder swap met by a no-keep exclusion blocks the removal until its existing choice resolves it.
        if (decided.Kind == ActionKind.ReplaceKind)
            return new Planner.SyncAction(ActionKind.Notice, row, NoticeKind: NoticeKind.NameClash, Cause: TextKey.ClashFileFolder);
        var inSync = decided.Kind == ActionKind.Record ||
            decided.Kind == ActionKind.None && decided.None == Planner.NoneReason.InSync;
        if (decided.Kind != copyKind && decided.Kind is not (ActionKind.Record or ActionKind.KeepBoth or ActionKind.Notice) && !inSync)
            return new Planner.SyncAction(ActionKind.Notice, row, NoticeKind: NoticeKind.Failure, Cause: TextKey.FailChanged);
        if (inSync) return decided;
        return decided.Kind == copyKind ? decided : new Planner.SyncAction(copyKind, row, Target: row.Context.Destination);
    }

    /// <summary>Removes the excluded copy (through the folder owner for a folder) and reports whether it settled.</summary>
    private async Task<ExclusionOutcome> RemoveAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Exclusion exclusion, Entry entry, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct)
    {
        var side = exclusion.Side == Side.Local ? local : dropbox;
        var saved = await store.FindAsync(pair.PairId, exclusion.Side,
            PathNames.KeyFor(exclusion.Side, entry.Path), ct);
        if (entry.IsFolder)
        {
            var binding = saved ?? SyncedEntries.From(exclusion.Side, entry, entry);
            await new FolderDelete().ApplyAsync(pair, settings, events, paths, binding, exclusion.Side, local, dropbox,
                store, _ => Array.Empty<Change>(), populate, compareOldBinding, ct);
            // A folder removal is settled only once the source is gone; a survivor or pending job leaves it pending.
            return await side.ReadOneAsync(entry.Path, ct) is null ? ExclusionOutcome.Applied : ExclusionOutcome.Pending;
        }
        var removed = await side.DeleteAsync(entry, null, ct);
        return removed.Outcome == RemovalOutcome.Removed ? ExclusionOutcome.Applied : ExclusionOutcome.Pending;
    }

    // Skipped, other-excluded or nested content refuses the removal (product-0247/0250); no other exclusion does.
    private Task RaiseProtectedAsync(IStore store, string pairId, Exclusion exclusion, Entry entry, CancellationToken ct) =>
        new Notices().RaiseAsync(store, pairId,
            new Notice(0, NoticeKind.Failure, TextKey.FailExcludeHoldsProtected, exclusion.Path,
                new IssueSubject([new NoticeItem(exclusion.Side, entry.Path, entry.Id)], exclusion.Side),
                DateTimeOffset.UtcNow, false),
            ct);

    // The stored exclusion rules applied to one item without the exclusion itself (port of Planner/Exclusions.cs:11-46).
    private static (bool Protected, bool Copy) Facts(Exclusion exclusion, Entry item, PathScope scope,
        PathScope protection, SyncedEntry? saved)
    {
        var local = exclusion.Side == Side.Local ? item.Path : saved?.LocalPath ?? item.Path;
        var dropbox = exclusion.Side == Side.Dropbox ? item.Path : saved?.DropboxPath ?? item.Path;
        var protectedItem = item.Skip is not null || protection.Excludes(local, dropbox, item.IsFolder);
        return (protectedItem, !protectedItem && !scope.Excludes(local, dropbox, item.IsFolder));
    }

    /// <summary>True when the item may still need a copy: no saved record or a saved folder, otherwise the change
    /// test. Local content without a hash counts as unknown, so it counts as a potential copy.</summary>
    private static bool MayCopyFile(Entry item, SyncedEntry? saved, Side excludedSide)
    {
        if (saved is null || saved.IsFolder) return true;
        if (excludedSide == Side.Dropbox) return !item.IsFolder && item.ContentHash != saved.DropboxContentHash;
        if (item.IsFolder) return false;
        if (item.Size != saved.LocalSize) return true;
        return item.Id != saved.LocalId || item.Modified.UtcTicks != saved.LocalModifiedTicks;
    }
}

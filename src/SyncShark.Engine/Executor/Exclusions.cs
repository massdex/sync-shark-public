namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Expected effects of applying the exclusion.</summary>
public sealed record ExcludePreview(Side Side, string Path, int CopyFiles, long CopyBytes, int RemoveFiles,
    long RemoveBytes, TextKey RecoveryPlace, bool BlockedByProtected);

/// <summary>Whether the exclusion settled (R31: only Applied permits a removal).</summary>
public enum ExclusionOutcome { Applied, Pending, Refused }

/// <summary>Exclusions operations for the executor module.</summary>
public sealed class Exclusions
{
    private readonly record struct Plan(bool Blocked, int CopyFiles, long CopyBytes, int RemoveFiles, long RemoveBytes,
        NoticeItem? Protected);

    /// <summary>Copy eligible changes to the kept side through the shared effect owner, then remove the excluded copy;
    /// a block, refusal or unsettled copy leaves it pending/refused with the source in place.</summary>
    public async Task<ExclusionOutcome> ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Exclusion exclusion, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (exclusion.Keep) return ExclusionOutcome.Applied;
        var side = exclusion.Side == Side.Local ? local : dropbox;
        var entry = await side.ReadOneAsync(exclusion.Path, ct);
        if (entry is null) return ExclusionOutcome.Applied;
        var plan = await PlanAsync(pair, exclusion, local, dropbox, store, paths, ct);
        if (plan.Blocked)
        {
            await RaiseProtectedAsync(store, pair.PairId, exclusion, entry, plan.Protected, ct);
            return ExclusionOutcome.Refused;
        }
        // Any entry that may still need a copy is copied first; the removal never follows a refused or unsettled copy.
        if (plan.CopyFiles > 0)
        {
            var copy = await PreserveAsync(pair, settings, events, paths, exclusion, entry, local, dropbox, store,
                populate, compareOldBinding, ct);
            if (copy != ExclusionOutcome.Applied) return copy;
        }
        return await RemoveAsync(pair, settings, events, paths, exclusion, entry, local, dropbox, store, populate,
            compareOldBinding, ct);
    }

    /// <summary>Reports the copies and removals an application would make without hashing or changing durable state.</summary>
    public async Task<ExcludePreview> PreviewAsync(PairConfig pair, Exclusion exclusion, ISide local, ISide dropbox, IStore store, PathScope paths, CancellationToken ct)
    {
        var recovery = Recovery(exclusion.Side);
        ct.ThrowIfCancellationRequested();
        if (exclusion.Keep) return new ExcludePreview(exclusion.Side, exclusion.Path, 0, 0, 0, 0, recovery, false);
        var plan = await PlanAsync(pair, exclusion, local, dropbox, store, paths, ct);
        return new ExcludePreview(exclusion.Side, exclusion.Path, plan.CopyFiles, plan.CopyBytes, plan.RemoveFiles,
            plan.RemoveBytes, recovery, plan.Blocked);
    }

    // One scoped pass: counts and the protected verdict, judged on both mapped endpoints so a kept-side nested root or
    // exclusion beneath a differently named folder is seen. A protected root is not walked into.
    private async Task<Plan> PlanAsync(PairConfig pair, Exclusion exclusion, ISide local, ISide dropbox, IStore store,
        PathScope paths, CancellationToken ct)
    {
        var side = exclusion.Side == Side.Local ? local : dropbox;
        var entry = await side.ReadOneAsync(exclusion.Path, ct);
        if (entry is null) return default;
        var scope = paths.Without(exclusion);
        var protection = paths.ProtectionWithout(exclusion);
        var blocked = false;
        var copyFiles = 0;
        long copyBytes = 0;
        var removeFiles = 0;
        long removeBytes = 0;
        NoticeItem? protectedItem = null;
        async Task See(Entry item)
        {
            var saved = await store.FindAsync(pair.PairId, exclusion.Side, PathNames.KeyFor(exclusion.Side, item.Path), ct);
            var (localPath, dropboxPath) = Endpoints(exclusion, item.Path, saved);
            if (Protected(exclusion, item, localPath, dropboxPath, scope, protection))
            {
                blocked = true;
                protectedItem ??= new NoticeItem(exclusion.Side, item.Path, item.Id);
            }
            if (item.IsFolder) return;
            if (!blocked && !scope.Excludes(localPath, dropboxPath, item.IsFolder) &&
                MayCopyFile(item, saved, exclusion.Side))
            {
                copyFiles++;
                copyBytes += item.Size;
            }
            removeFiles++;
            removeBytes += item.Size;
        }
        await See(entry);
        if (!blocked && entry.IsFolder && entry.Skip is null)
            await foreach (var listed in side.ReadAllAsync(entry.Path, new ListOptions(true, true), ct))
                if (listed.Entry is { } child) await See(child);
        return new Plan(blocked, copyFiles, copyBytes, removeFiles, removeBytes, protectedItem);
    }

    // Streams the target, copies each eligible change to the kept side through the shared owner, and reports the worst
    // outcome; a protected item met here raises the same refusal notice as the preview.
    private async Task<ExclusionOutcome> PreserveAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Exclusion exclusion, Entry entry, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct)
    {
        var side = exclusion.Side == Side.Local ? local : dropbox;
        var scope = paths.Without(exclusion);
        var protection = paths.ProtectionWithout(exclusion);
        var owed = pair.OwedScopes is { } scopes && scopes.Any(s =>
            PathNames.IsSelfOrUnder(s.LocalPath, exclusion.Path) || PathNames.IsSelfOrUnder(s.DropboxPath, exclusion.Path));
        var copyKind = exclusion.Side == Side.Local ? ActionKind.Upload : ActionKind.Download;
        await foreach (var item in TargetItemsAsync(side, entry, ct))
        {
            ct.ThrowIfCancellationRequested();
            var saved = await store.FindAsync(pair.PairId, exclusion.Side, PathNames.KeyFor(exclusion.Side, item.Path), ct);
            var (localPath, dropboxPath) = Endpoints(exclusion, item.Path, saved);
            // Protection met here, including a protected folder that arrived after the plan, raises the same notice.
            if (Protected(exclusion, item, localPath, dropboxPath, scope, protection))
            {
                await RaiseProtectedAsync(store, pair.PairId, exclusion, item, new NoticeItem(exclusion.Side, item.Path, item.Id), ct);
                return ExclusionOutcome.Refused;
            }
            if (item.IsFolder) continue;
            if (scope.Excludes(localPath, dropboxPath, false) || !MayCopyFile(item, saved, exclusion.Side)) continue;
            var row = await ObserveAsync(pair, exclusion, item, saved, local, dropbox, ct);
            var action = Planner.Decide.For(row, pair.FirstRun, DateTimeOffset.UtcNow, false, (exclusion.Side, owed));
            if (action.Kind == ActionKind.NeedLocalHash)
            {
                row = row with { LocalHash = await side.ContentHashAsync(item, ct) };
                action = Planner.Decide.For(row, pair.FirstRun, DateTimeOffset.UtcNow, false, (exclusion.Side, owed));
            }
            if (action.Kind == ActionKind.None && action.None == Planner.NoneReason.InSync) continue;
            // A warning or clash never authorizes removal: raise it and stop. Kept-side-newer deletes need no copy.
            if (action.Kind == ActionKind.Notice)
            {
                await new Effects().ApplyAsync(pair, settings, events, paths, action, local, dropbox, store, populate, compareOldBinding, ct);
                return ExclusionOutcome.Refused;
            }
            if (action.Kind is ActionKind.DeleteLocal or ActionKind.DeleteDropbox or ActionKind.Record or
                ActionKind.ReplaceKind or ActionKind.DeferFolder) continue;
            if (action.Kind != copyKind && action.Kind != ActionKind.KeepBoth) return ExclusionOutcome.Refused;
            var outcome = await new Effects().ApplyAsync(pair, settings, events, paths, action, local, dropbox, store,
                populate, compareOldBinding, ct);
            if (outcome != EffectOutcome.Applied)
                return outcome == EffectOutcome.Pending ? ExclusionOutcome.Pending : ExclusionOutcome.Refused;
        }
        return ExclusionOutcome.Applied;
    }

    // The removal runs through the shared effect owner's selected delete, which owns the selection proof, retirement,
    // folder held input and notice/retry routing; only Applied settles the exclusion.
    private async Task<ExclusionOutcome> RemoveAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Exclusion exclusion, Entry entry, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct)
    {
        var key = PathNames.KeyFor(exclusion.Side, entry.Path);
        var saved = await store.FindAsync(pair.PairId, exclusion.Side, key, ct);
        var deletion = exclusion.Side == Side.Local ? ActionKind.DeleteLocal : ActionKind.DeleteDropbox;
        var action = new Planner.SyncAction(deletion, Row.ForOneSide(exclusion.Side, key, entry, saved, [], null, RowContext.Neutral),
            Side: exclusion.Side);
        var outcome = await new Effects().ApplyAsync(pair, settings, events, paths, action, local, dropbox, store,
            populate, compareOldBinding, ct);
        return outcome switch
        {
            EffectOutcome.Applied => ExclusionOutcome.Applied,
            EffectOutcome.Pending => ExclusionOutcome.Pending,
            _ => ExclusionOutcome.Refused
        };
    }

    private static async IAsyncEnumerable<Entry> TargetItemsAsync(ISide side, Entry entry,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return entry;
        if (!entry.IsFolder || entry.Skip is not null) yield break;
        await foreach (var listed in side.ReadAllAsync(entry.Path, new ListOptions(true, true), ct))
            if (listed.Entry is { } child) yield return child;
    }

    // The two-sided observation for one row: both mapped endpoints observed (the excluded side's own item wins on its
    // side) with the saved binding, so a kept-side edit, relocation or folder is visible to Planner.Decide.
    private static async Task<Row> ObserveAsync(PairConfig pair, Exclusion exclusion, Entry item, SyncedEntry? saved,
        ISide local, ISide dropbox, CancellationToken ct)
    {
        var (localPath, dropboxPath) = Endpoints(exclusion, item.Path, saved);
        var localEntry = await local.ReadOneAsync(localPath, ct);
        var dropboxEntry = await dropbox.ReadOneAsync(dropboxPath, ct);
        if (exclusion.Side == Side.Local) localEntry = item; else dropboxEntry = item;
        var key = saved?.LocalKey ?? PathNames.Key(localPath);
        return new Row(key, localEntry, dropboxEntry, saved, [], null, RowContext.Neutral);
    }

    private static Task RaiseProtectedAsync(IStore store, string pairId, Exclusion exclusion, Entry entry,
        NoticeItem? protectedItem, CancellationToken ct) =>
        new Notices().RaiseAsync(store, pairId,
            new Notice(0, NoticeKind.Failure, TextKey.FailExcludeHoldsProtected, exclusion.Path,
                new IssueSubject([protectedItem ?? new NoticeItem(exclusion.Side, entry.Path, entry.Id)], exclusion.Side),
                DateTimeOffset.UtcNow, false),
            ct);

    private static bool Protected(Exclusion exclusion, Entry item, string localPath, string dropboxPath,
        PathScope scope, PathScope protection) =>
        item.Skip is not null || protection.Excludes(localPath, dropboxPath, item.IsFolder) ||
        scope.HoldsNestedRoot(Side.Local, localPath) || scope.HoldsNestedRoot(Side.Dropbox, dropboxPath);

    private static (string Local, string Dropbox) Endpoints(Exclusion exclusion, string path, SyncedEntry? saved)
    {
        if (saved is null) return (path, path);
        return exclusion.Side == Side.Local ? (path, saved.DropboxPath) : (saved.LocalPath, path);
    }

    private static TextKey Recovery(Side side) =>
        side == Side.Local ? TextKey.RecoveryRecycleBin : TextKey.RecoveryDropboxHistory;

    private static bool MayCopyFile(Entry item, SyncedEntry? saved, Side excludedSide)
    {
        if (saved is null || saved.IsFolder) return true;
        if (excludedSide == Side.Dropbox) return !item.IsFolder && item.ContentHash != saved.DropboxContentHash;
        if (item.IsFolder) return false;
        if (item.Size != saved.LocalSize) return true;
        return item.Id != saved.LocalId || item.Modified.UtcTicks != saved.LocalModifiedTicks;
    }
}

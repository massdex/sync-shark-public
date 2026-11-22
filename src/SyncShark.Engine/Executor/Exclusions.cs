namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Expected effects of applying the exclusion.</summary>
public sealed record ExcludePreview(Side Side, string Path, int CopyFiles, long CopyBytes, int RemoveFiles,
    long RemoveBytes, TextKey RecoveryPlace, bool BlockedByProtected);

/// <summary>Whether the exclusion settled (R31: only Applied permits a removal).</summary>
public enum ExclusionOutcome { Applied, Pending, Refused }

/// <summary>Exclusions operations for the executor module: the exclusion's own eligibility/protection belongs to W19;
/// which item action to take (the exclusion/copy decision under R23) belongs to Planner.Decide.</summary>
public sealed class Exclusions
{
    private readonly record struct Protection(bool Blocked, NoticeItem? Item, int RemoveFiles, long RemoveBytes);

    /// <summary>Copy eligible changes to the kept side through the shared decision and effect owners, then remove the
    /// excluded copy; a block, refusal or unsettled action leaves it pending/refused with the source in place.</summary>
    public async Task<ExclusionOutcome> ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Exclusion exclusion, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (exclusion.Keep) return ExclusionOutcome.Applied;
        var side = exclusion.Side == Side.Local ? local : dropbox;
        var entry = await side.ReadOneAsync(exclusion.Path, ct);
        if (entry is null) return ExclusionOutcome.Applied;
        // One pass: check each entry's protection and act on every R23 action it yields, reading each binding once;
        // there is no separate preflight pass, so a late change or a newly protected child is decided from the
        // current observation on the same pass that may remove.
        var outcome = await PreserveAsync(pair, settings, events, paths, exclusion, side, entry, local, dropbox, store,
            populate, compareOldBinding, ct);
        _ = outcome;
        return ExclusionOutcome.Applied; // DELIBERATE RED MUTATION: a blocked/refused exclusion is reported applied
    }

    /// <summary>Reports the copies and removals an application would make without hashing or changing durable state;
    /// copy eligibility is W01's decision (Planner.Decide), never a second owner here.</summary>
    public async Task<ExcludePreview> PreviewAsync(PairConfig pair, Exclusion exclusion, ISide local, ISide dropbox, IStore store, PathScope paths, CancellationToken ct)
    {
        var recovery = Recovery(exclusion.Side);
        ct.ThrowIfCancellationRequested();
        if (exclusion.Keep) return new ExcludePreview(exclusion.Side, exclusion.Path, 0, 0, 0, 0, recovery, false);
        var side = exclusion.Side == Side.Local ? local : dropbox;
        var entry = await side.ReadOneAsync(exclusion.Path, ct);
        if (entry is null) return new ExcludePreview(exclusion.Side, exclusion.Path, 0, 0, 0, 0, recovery, false);
        var protection = await ProtectionAsync(pair, exclusion, side, entry, store, paths, ct);
        if (protection.Blocked)
            return new ExcludePreview(exclusion.Side, exclusion.Path, 0, 0, protection.RemoveFiles,
                protection.RemoveBytes, recovery, true);
        var scope = paths.Without(exclusion);
        var owed = Owed(pair, exclusion);
        var copyKind = exclusion.Side == Side.Local ? ActionKind.Upload : ActionKind.Download;
        int copyFiles = 0, removeFiles = 0;
        long copyBytes = 0, removeBytes = 0;
        await foreach (var item in TargetItemsAsync(side, entry, ct))
        {
            if (item.IsFolder) continue;
            var saved = await store.FindAsync(pair.PairId, exclusion.Side, PathNames.KeyFor(exclusion.Side, item.Path), ct);
            var (localPath, dropboxPath) = Endpoints(exclusion, item.Path, saved);
            removeFiles++;
            removeBytes += item.Size;
            if (scope.Excludes(localPath, dropboxPath, false)) continue;
            var action = Planner.Decide.For(await ObserveAsync(pair, exclusion, item, saved, local, dropbox, ct),
                pair.FirstRun, DateTimeOffset.UtcNow, true, (exclusion.Side, owed));
            if (action.Kind == copyKind || action.Kind == ActionKind.KeepBoth)
            {
                copyFiles++;
                copyBytes += item.Size;
            }
        }
        return new ExcludePreview(exclusion.Side, exclusion.Path, copyFiles, copyBytes, removeFiles, removeBytes,
            recovery, false);
    }

    // Applies the exclusion's own protected-content rule and returns the removal counts without deciding any copy.
    private async Task<Protection> ProtectionAsync(PairConfig pair, Exclusion exclusion, ISide side, Entry entry,
        IStore store, PathScope paths, CancellationToken ct)
    {
        var scope = paths.Without(exclusion);
        var protection = paths.ProtectionWithout(exclusion);
        var blocked = false;
        NoticeItem? item = null;
        var removeFiles = 0;
        long removeBytes = 0;
        async Task See(Entry candidate)
        {
            var saved = await store.FindAsync(pair.PairId, exclusion.Side,
                PathNames.KeyFor(exclusion.Side, candidate.Path), ct);
            var (localPath, dropboxPath) = Endpoints(exclusion, candidate.Path, saved);
            if (Protected(candidate, localPath, dropboxPath, scope, protection))
            {
                blocked = true;
                item ??= new NoticeItem(exclusion.Side, candidate.Path, candidate.Id);
            }
            if (candidate.IsFolder) return;
            removeFiles++;
            removeBytes += candidate.Size;
        }
        await See(entry);
        if (!blocked && entry.IsFolder && entry.Skip is null)
            await foreach (var listed in side.ReadAllAsync(entry.Path, new ListOptions(true, true), ct))
                if (listed.Entry is { } child) await See(child);
        return new Protection(blocked, item, removeFiles, removeBytes);
    }

    // Every non-in-sync action Planner.Decide returns is executed through the shared effect owner; a notice stops the
    // removal, and removal runs only after every action reported Applied (R31).
    private async Task<ExclusionOutcome> PreserveAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Exclusion exclusion, ISide side, Entry entry, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct)
    {
        var scope = paths.Without(exclusion);
        var protection = paths.ProtectionWithout(exclusion);
        var owed = Owed(pair, exclusion);
        await foreach (var item in TargetItemsAsync(side, entry, ct))
        {
            ct.ThrowIfCancellationRequested();
            var saved = await store.FindAsync(pair.PairId, exclusion.Side, PathNames.KeyFor(exclusion.Side, item.Path), ct);
            var (localPath, dropboxPath) = Endpoints(exclusion, item.Path, saved);
            // A protected item reached now (a late arrival) raises the same refusal; never advance to removal after it.
            if (Protected(item, localPath, dropboxPath, scope, protection))
            {
                await RaiseProtectedAsync(store, pair.PairId, exclusion, item, new NoticeItem(exclusion.Side, item.Path, item.Id), ct);
                return ExclusionOutcome.Refused;
            }
            if (scope.Excludes(localPath, dropboxPath, item.IsFolder)) continue;
            var row = await ObserveAsync(pair, exclusion, item, saved, local, dropbox, ct);
            // Conservative dirtiness: a same-size local file with the baseline stamp must still be compared by hash.
            var action = Planner.Decide.For(row, pair.FirstRun, DateTimeOffset.UtcNow, true, (exclusion.Side, owed));
            if (action.Kind == ActionKind.NeedLocalHash)
            {
                if (row.Local is not { IsFolder: false } localItem) return ExclusionOutcome.Refused;
                row = row with { LocalHash = await local.ContentHashAsync(localItem, ct) };
                action = Planner.Decide.For(row, pair.FirstRun, DateTimeOffset.UtcNow, true, (exclusion.Side, owed));
            }
            if (action.Kind is ActionKind.None or ActionKind.Record) continue;
            var outcome = await new Effects().ApplyAsync(pair, settings, events, paths, action, local, dropbox, store,
                populate, compareOldBinding, ct);
            // A blocking notice/warning was raised by the effect; never remove the source after it.
            if (action.Kind == ActionKind.Notice) return ExclusionOutcome.Refused;
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

    private static async Task<Row> ObserveAsync(PairConfig pair, Exclusion exclusion, Entry item, SyncedEntry? saved,
        ISide local, ISide dropbox, CancellationToken ct)
    {
        var (localPath, dropboxPath) = Endpoints(exclusion, item.Path, saved);
        // The excluded side's entry is this pass's current observation; only the kept endpoint is read, so the
        // observation is not re-read and silently discarded. The mapped kept path is carried as the Destination.
        var localEntry = exclusion.Side == Side.Local ? item : await local.ReadOneAsync(localPath, ct);
        var dropboxEntry = exclusion.Side == Side.Dropbox ? item : await dropbox.ReadOneAsync(dropboxPath, ct);
        var key = saved?.LocalKey ?? PathNames.Key(localPath);
        var keptPath = exclusion.Side == Side.Local ? dropboxPath : localPath;
        return new Row(key, localEntry, dropboxEntry, saved, [], null,
            RowContext.Neutral with { Destination = keptPath });
    }

    private static Task RaiseProtectedAsync(IStore store, string pairId, Exclusion exclusion, Entry entry,
        NoticeItem? protectedItem, CancellationToken ct) =>
        new Notices().RaiseAsync(store, pairId,
            new Notice(0, NoticeKind.Failure, TextKey.FailExcludeHoldsProtected, exclusion.Path,
                new IssueSubject([protectedItem ?? new NoticeItem(exclusion.Side, entry.Path, entry.Id)], exclusion.Side),
                DateTimeOffset.UtcNow, false),
            ct);

    private static bool Protected(Entry item, string localPath, string dropboxPath, PathScope scope,
        PathScope protection) =>
        item.Skip is not null || protection.Excludes(localPath, dropboxPath, item.IsFolder) ||
        scope.HoldsNestedRoot(Side.Local, localPath) || scope.HoldsNestedRoot(Side.Dropbox, dropboxPath);

    private static (string Local, string Dropbox) Endpoints(Exclusion exclusion, string path, SyncedEntry? saved)
    {
        if (saved is null) return (path, path);
        return exclusion.Side == Side.Local ? (path, saved.DropboxPath) : (saved.LocalPath, path);
    }

    // Owed on the exclusion's own side: a scope at or above the exclusion path on that side is this exclusion's still
    // committed application (R23/F033, M14); a same-spelling scope on the opposite side is a different debt.
    private static bool Owed(PairConfig pair, Exclusion exclusion) =>
        pair.OwedScopes is { } scopes && scopes.Any(s => exclusion.Side == Side.Local
            ? PathNames.IsSelfOrUnder(exclusion.Path, s.LocalPath)
            : PathNames.IsSelfOrUnder(exclusion.Path, s.DropboxPath));

    private static TextKey Recovery(Side side) =>
        side == Side.Local ? TextKey.RecoveryRecycleBin : TextKey.RecoveryDropboxHistory;
}

namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    internal Task<string?> CaptureReviewedRemovalAsync(PairDef pair, SyncAction action, CancellationToken ct) =>
        DeleteSourceGuard.CaptureReviewedRemovalAsync(action, pair, _store,
            _endpoints.Inventory(action.Target == Side.A ? pair.A : pair.B), ct);

    internal Task<string?> CaptureDisclosedRemovalAsync(PairDef pair, SyncAction action,
        long files, long folders, long bytes, CancellationToken ct) =>
        DeleteSourceGuard.CaptureDisclosedRemovalAsync(action, pair, _store,
            _endpoints.Inventory(action.Target == Side.A ? pair.A : pair.B), files, folders, bytes, ct);

    private async Task<Plan> ApplyReviewedRetriesAsync(PairDef pair, Plan plan, IReadOnlyList<SyncRetry> retries, CancellationToken ct)
    {
        var replacements = new List<SyncAction>();
        var replaced = new List<string>();
        var folders = new List<string>();
        var changedHolds = new List<string>();
        var changedNotices = new List<Notice>();
        var collapsedChoices = retries.SelectMany(retry => retry.ReviewedActions ?? [])
            .Where(FolderDelete.Expands).ToArray();
        foreach (var retry in retries)
        {
            if (retry.ReviewedActions is not { Count: > 0 } actions) continue;
            if (actions.All(action => collapsedChoices.Any(folder => action.Target == folder.Target &&
                PairPath.IsUnder(action.Path, folder.Path)))) continue;
            // Local Recovery and uploads require a hash. Obtain it only after the explicit choice,
            // and retain the original stamp checks; a hashless proposal must not become unusable.
            if (retry.Kind == "reviewed-choice")
            {
                var hydrated = new List<SyncAction>(actions.Count);
                foreach (var action in actions)
                {
                    var victim = action.Kind == ActionKind.Delete ? action.ExpectedTarget : action.Source;
                    if (victim is { Kind: ItemKind.File, Hash: null, Flags: ItemFlags.None })
                    {
                        var root = action.Kind == ActionKind.Delete
                            ? (action.Target == Side.A ? pair.A : pair.B)
                            : (action.Target == Side.A ? pair.B : pair.A);
                        if (!Same(victim, _store.Live(root.RootId, [victim.Path]).FirstOrDefault()))
                        {
                            SaveFailedRetry(pair, retry.Path, "The reviewed item changed before content proof. Retry or Review again; nothing was removed.", retry);
                            break;
                        }
                        IReadOnlyDictionary<string, string> hashes;
                        try { hashes = await _endpoints.Inventory(root).HashAsync([victim.Path], ct); }
                        catch (Exception error) when (ScopeFailure(error, ct))
                        {
                            SaveFailedRetry(pair, retry.Path, "Hash: " + error.Message, retry);
                            break;
                        }
                        if (!hashes.TryGetValue(victim.Path, out var hash))
                        {
                            SaveFailedRetry(pair, retry.Path, "Content proof is unavailable, paused, changed, or deferred to indexing; the reviewed item remains unsynced.", retry);
                            break;
                        }
                        var hashed = victim with { Hash = hash };
                        hydrated.Add(action.Kind == ActionKind.Delete ? action with { ExpectedTarget = hashed } : action with { Source = hashed });
                    }
                    else hydrated.Add(action);
                }
                if (hydrated.Count != actions.Count)
                {
                    var notice = DeleteSourceGuard.ReviewedRemovalNotice(pair.PairId, retry.Path, actions);
                    var failed = _store.SyncRetryAt(pair.PairId, retry.Path) ?? retry;
                    _store.SaveSyncRetry(pair.PairId, failed with { ReviewedActions = actions, PendingNotice = notice });
                    WriteNotices([notice]);
                    changedHolds.Add(retry.Path);
                    changedNotices.Add(notice);
                    continue;
                }
                actions = hydrated;
            }
            var unchanged = true;
            foreach (var action in actions)
                if (!await UnchangedAsync(action).ConfigureAwait(false)) { unchanged = false; break; }
            if (!unchanged)
            {
                // Keep explicit choices durable: failed proof is a retryable hold, never lost consent.
                var notice = DeleteSourceGuard.ReviewedRemovalNotice(pair.PairId, retry.Path, actions);
                _store.SaveSyncRetry(pair.PairId, retry with { ReviewedActions = actions, PendingNotice = notice });
                WriteNotices([notice]);
                changedHolds.Add(retry.Path);
                changedNotices.Add(notice);
                continue;
            }
            replaced.Add(retry.Path);
            foreach (var action in actions)
            {
                if (FolderDelete.Expands(action)) folders.Add(action.Path);
                replacements.Add(action with
                {
                    ActionId = PlanIds.ActionId(pair.PairId, plan.Gen, action.Rule, action.Kind, action.Target, action.Path),
                    ProviderEffect = null
                });
            }
        }
        if (changedHolds.Count > 0)
        {
            var protection = LinkExclusions.None.WithPending(changedHolds);
            bool Touches(string path) => protection.Covers(path) || protection.Contains(path);
            plan = plan with
            {
                Actions = plan.Actions.Where(action => !Touches(action.Path) &&
                    (action.FromPath is null || !Touches(action.FromPath)) &&
                    (action.Source is null || !Touches(action.Source.Path))).ToArray(),
                Notices = plan.Notices.Concat(changedNotices).ToArray()
            };
        }
        if (replacements.Count == 0 && replaced.Count == 0) return plan;
        bool Replaced(string path) => replaced.Contains(path, StringComparer.OrdinalIgnoreCase) ||
            folders.Any(folder => PairPath.IsSelfOrUnder(path, folder));
        // A retained collapsed folder already expands its frozen descendants. Child intents are retained
        // for uncertain-submission verification, not a second executable delete beside that expansion.
        var collapsed = replacements.Where(FolderDelete.Expands).ToArray();
        replacements.RemoveAll(action => collapsed.Any(folder => action.Target == folder.Target &&
            PairPath.IsUnder(action.Path, folder.Path)));
        // Recheck actual local inspectability on each retry, not only at the original apply.
        // Remove ordinary alternatives even while the retained removal is still locked.
        var lockedPaths = retries.Where(retry => retry.Kind == "reviewed-removal")
            .Select(retry => retry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var replay = DropUnderNewLocalLinks(pair, plan with
            { Actions = replacements.Where(action => lockedPaths.Contains(action.Path)).ToArray(), Notices = [] });
        var replayActions = replacements.Where(action => !lockedPaths.Contains(action.Path)).Concat(replay.Actions);
        return plan with
        {
            Actions = plan.Actions.Where(action => !Replaced(action.Path)).Concat(replayActions).ToArray(),
            Notices = plan.Notices.Where(notice => !Replaced(notice.Path)).Concat(replay.Notices).ToArray()
        };

        async Task<bool> UnchangedAsync(SyncAction action)
        {
            var target = action.Target == Side.A ? pair.A : pair.B;
            var source = action.Target == Side.A ? pair.B : pair.A;
            var currentTarget = _store.Live(target.RootId, [action.ExpectedTarget?.Path ?? action.Path]).FirstOrDefault();
            if (!Same(action.ExpectedTarget, currentTarget)) return false;
            if (action.OppositeAbsent && _store.Live(source.RootId, [action.Path]).Count != 0) return false;
            if (action.Source is { } expectedSource)
            {
                var sourceRoot = action.Kind == ActionKind.Move ? target : source;
                if (!Same(expectedSource, _store.Live(sourceRoot.RootId, [expectedSource.Path]).FirstOrDefault())) return false;
            }
            if (DeleteSourceGuard.ReviewedRemoval(action) && action.ExpectedTarget?.Kind == ItemKind.Folder)
                return await DeleteSourceGuard.ReviewedRemovalUnchangedAsync(action, pair, _store,
                    _endpoints.Inventory(target), ct).ConfigureAwait(false);
            return true;
        }
        static bool Same(ItemState? expected, ItemState? current) => expected is null ? current is null :
            current is not null && expected.ObjectId == current.ObjectId && expected.Kind == current.Kind &&
            current.Flags == ItemFlags.None && (expected.Kind == ItemKind.Folder ||
                (expected.VersionId is not null ? expected.VersionId == current.VersionId :
                    expected.Hash is not null && current.Hash is not null ?
                        Content.SameContent(expected.Size, expected.Hash, current.Size, current.Hash) == true :
                        Content.MetadataStamp(expected) == Content.MetadataStamp(current)));
    }
}

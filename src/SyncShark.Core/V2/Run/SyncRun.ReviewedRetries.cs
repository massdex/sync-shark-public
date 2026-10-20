namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
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
            if (!actions.All(Unchanged))
            {
                // A changed collapsed choice is held in its entirety, never expanded to newly observed children.
                if (actions.Any(FolderDelete.Expands) || retry.Kind == "reviewed-removal")
                {
                    var notice = new Notice(pair.PairId, 40, NoticeKind.UserDecision, retry.Path,
                        "The reviewed removal no longer matches its saved item or contents. Nothing was removed or copied back." + NoticeChoices.RemovalAttentionHelp,
                        actions.Any(FolderDelete.Expands) ? "reviewed-folder-grew" : "reviewed-removal-skipped");
                    _store.AddNotices([notice]);
                    _store.ClearSyncRetry(pair.PairId, retry.Path);
                    changedHolds.Add(retry.Path);
                    changedNotices.Add(notice);
                }
                else _store.SaveSyncRetry(pair.PairId, retry with { ReviewedActions = null });
                continue;
            }
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
                        var hashes = await _endpoints.Inventory(root).HashAsync([victim.Path], ct);
                        if (!hashes.TryGetValue(victim.Path, out var hash)) break;
                        var hashed = victim with { Hash = hash };
                        hydrated.Add(action.Kind == ActionKind.Delete ? action with { ExpectedTarget = hashed } : action with { Source = hashed });
                    }
                    else hydrated.Add(action);
                }
                if (hydrated.Count != actions.Count)
                {
                    replaced.Add(retry.Path); // No ordinary alternative may reverse the explicit choice.
                    continue;
                }
                actions = hydrated;
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

        bool Unchanged(SyncAction action)
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
            if (FolderDelete.Expands(action))
            {
                if (action.FrozenVictimSignature is null) return false;
                var refused = ProviderRefusedPaths.Load(_store, pair);
                if (action.FrozenVictimSignature != FolderDelete.Signature(FolderDelete.SyncedDescendants(
                    _store, pair, target.RootId, action.Path, action.Target == Side.A ? refused.A : refused.B))) return false;
            }
            return true;
        }
        static bool Same(ItemState? expected, ItemState? current) => expected is null ? current is null :
            current is not null && expected.ObjectId == current.ObjectId && current.Flags == ItemFlags.None &&
            Content.SideStamp(expected) == Content.SideStamp(current);
    }
}

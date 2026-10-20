namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private Plan ApplyReviewedRetries(PairDef pair, Plan plan, IReadOnlyList<SyncRetry> retries)
    {
        var replacements = new List<SyncAction>();
        var replaced = new List<string>();
        var folders = new List<string>();
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
                if (actions.Any(FolderDelete.Expands))
                {
                    _store.ClearSyncRetry(pair.PairId, retry.Path);
                    _store.AddNotices([new Notice(pair.PairId, 40, NoticeKind.UserDecision, retry.Path,
                        "The reviewed folder removal no longer matches its saved contents. Nothing under it was removed." + NoticeChoices.RemovalAttentionHelp,
                        "reviewed-folder-grew", RemovalSides: actions.Select(action => action.Target).Distinct().ToArray())]);
                }
                else _store.SaveSyncRetry(pair.PairId, retry with { ReviewedActions = null });
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
        if (replacements.Count == 0) return plan;
        bool Replaced(string path) => replaced.Contains(path, StringComparer.OrdinalIgnoreCase) ||
            folders.Any(folder => PairPath.IsSelfOrUnder(path, folder));
        // A retained collapsed folder already expands its frozen descendants. Child intents are retained
        // for uncertain-submission verification, not a second executable delete beside that expansion.
        var collapsed = replacements.Where(FolderDelete.Expands).ToArray();
        replacements.RemoveAll(action => collapsed.Any(folder => action.Target == folder.Target &&
            PairPath.IsUnder(action.Path, folder.Path)));
        return plan with
        {
            Actions = plan.Actions.Where(action => !Replaced(action.Path)).Concat(replacements).ToArray(),
            Notices = plan.Notices.Where(notice => !Replaced(notice.Path)).ToArray()
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

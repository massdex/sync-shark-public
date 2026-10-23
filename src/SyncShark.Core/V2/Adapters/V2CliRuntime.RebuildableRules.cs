namespace Massdex.SyncShark.V2;

public sealed partial class V2CliRuntime
{
    // One durable notice per scoped rebuildable rule the last prepare matched no folder for. A rule that now
    // matches (or is no longer configured) closes its old notice; Keep suppresses only the same rule identity.
    private void RaiseRebuildableRuleNotices(PairDef pair, IReadOnlyList<RebuildableChoice> choices,
        IReadOnlyList<RebuildableFolder> detected)
    {
        var configured = choices.Where(choice => choice.Prefix.Length > 0).ToArray();
        foreach (var notice in _store.UnresolvedNotices(pair.PairId)
                     .Where(item => item.ReasonKey == NoticeChoices.RebuildableRuleKey).ToArray())
        {
            if (notice.RebuildableRule is not { } rule)
            {
                _store.CloseNotice(notice.NoticeId!, "resolved");
                continue;
            }
            var stillConfigured = configured.Any(choice => SameRule(choice, rule));
            var matched = detected.Any(folder => string.Equals(folder.Category, rule.Category, StringComparison.OrdinalIgnoreCase) &&
                PairPath.IsSelfOrUnder(folder.Path, rule.Prefix));
            if (!stillConfigured || matched) _store.CloseNotice(notice.NoticeId!, "resolved");
        }
        var raised = configured
            .Where(choice => !detected.Any(folder => choice.Matches(folder)))
            .Select(choice => new Notice(pair.PairId, 40, NoticeKind.Info, choice.Prefix,
                $"Rebuildable rule '{choice.Id}' under '{choice.Prefix}/' matched no folders. Preview matches, " +
                "choose a folder inside this pair, remove the rule, or keep it. No files were changed or deleted.",
                NoticeChoices.RebuildableRuleKey,
                RebuildableRule: new NoticeRebuildableRule(choice.Id, choice.Prefix)))
            .ToArray();
        if (raised.Length > 0) _store.AddNotices(raised);
    }

    // The prepare record is the pair's retained first-sync scope; only its rule list and digest change here.
    private string UpdateRebuildableRule(PairDef pair, RebuildableChoice current, RebuildableChoice? replacement) =>
        UpdateRebuildableRule(_productRoot, pair, _store, current, replacement);

    internal static string UpdateRebuildableRule(string productRoot, PairDef pair, IStateStore store,
        RebuildableChoice current, RebuildableChoice? replacement)
    {
        if (!FirstSyncChoicesState.UpdateRebuildableRule(productRoot, pair.PairId, store, current, replacement))
            throw new SyncRunRefusedException(
                "The retained first-sync rule is no longer available. Run prepare again; nothing was changed.");
        return replacement is null
            ? $"Removed rebuildable rule '{current.Id}' under '{current.Prefix}/'. No files were deleted or moved; ordinary sync is unchanged."
            : $"Rule '{replacement.Id}' now applies under '{replacement.Prefix}/'. Run prepare again to detect its folders; no files were changed.";
    }

    // First sync completed: the retained rule list is gone, so its pending notices would have no action.
    private void CloseRebuildableRuleNotices(string pairId)
    {
        foreach (var notice in _store.UnresolvedNotices(pairId)
                     .Where(item => item.ReasonKey == NoticeChoices.RebuildableRuleKey).ToArray())
            _store.CloseNotice(notice.NoticeId!, "superseded-by-first-sync");
    }

    private static bool SameRule(RebuildableChoice choice, NoticeRebuildableRule rule) =>
        string.Equals(choice.Id, rule.Category, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(choice.Prefix.TrimEnd('/'), rule.Prefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}

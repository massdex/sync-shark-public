namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private static bool Allowed(ActionGuard guard, SyncAction action)
    {
        var directory = (action.ExpectedTarget ?? action.Source)?.Kind == ItemKind.Folder || action.Kind == ActionKind.MkDir;
        if (guard.IgnoreRules is { Count: > 0 } rules &&
            (SyncIgnorePolicy.IsIgnored(action.Path, directory, rules) ||
             action.FromPath is { } from && SyncIgnorePolicy.IsIgnored(from, directory, rules) ||
             action.Source is { } source && SyncIgnorePolicy.IsIgnored(source.Path, source.Kind == ItemKind.Folder, rules))) return false;
        // A copy can name an allowed destination but still read an excluded source (e.g. a rebased move).
        var sourceInclude = action.Target == Side.A ? guard.B : guard.A;
        if (action.Source is { } sourceState && !sourceInclude(sourceState.Path)) return false;
        // Only a reviewed rebuildable folder's cloud-only removal ignores local links; every other guard still applies.
        if (IsApprovedRebuildableDelete(guard, action)) return guard.RebuildableDelete!(action.Path);
        var strict = action.Kind is ActionKind.Move or ActionKind.Delete;
        var include = (action.Target, strict) switch
        {
            (Side.A, true) => guard.StrictA,
            (Side.A, false) => guard.A,
            (_, true) => guard.StrictB,
            _ => guard.B
        };
        return include(action.Path) && (action.FromPath is null || include(action.FromPath));
    }

    internal Plan FilterNarrowedPrepare(PairDef pair, Plan plan, IReadOnlyList<string> rules) =>
        rules.Count == 0 ? plan : ApplySafetyFilter(pair, null, plan with { NarrowedIgnoreRules = rules });

    private bool CarriesNewlyIgnored(PairDef pair, SyncAction action, IReadOnlyList<SyncIgnoreRule> rules)
    {
        if (rules.Count == 0 || action.Kind is not (ActionKind.Delete or ActionKind.Move) ||
            (action.ExpectedTarget ?? action.Source)?.Kind != ItemKind.Folder) return false;
        var root = action.Target == Side.A ? pair.A : pair.B;
        return _store.AnyLiveIgnoredUnder(root.RootId, action.Path, rules) ||
            action.FromPath is { } from && _store.AnyLiveIgnoredUnder(root.RootId, from, rules);
    }

    private Dictionary<SyncAction, ItemEligibility.Result> BlockedReasons(PairDef pair, ActionGuard? guard,
        IReadOnlyList<SyncAction> actions, IReadOnlyList<SyncIgnoreRule> rules)
    {
        var reasons = new Dictionary<SyncAction, ItemEligibility.Result>();
        foreach (var action in actions.Where(action => CarriesNewlyIgnored(pair, action, rules))) reasons[action] = new("ignored");
        if (guard is not null)
            foreach (var entry in GuardedActions(actions, guard))
                if (!Allowed(entry.Guard, entry.Action))
                    reasons.TryAdd(entry.Action, entry.Guard.Explain?.Invoke(entry.Action) ?? new("held:protection"));
        return reasons;
    }

    private Plan ApplySafetyFilter(PairDef pair, ActionGuard? predicates, Plan plan)
    {
        var kept = new List<SyncAction>();
        var skipped = new List<SyncAction>(plan.SkippedActions);
        var rules = SyncIgnorePolicy.Normalize(plan.NarrowedIgnoreRules.SelectMany(SyncIgnorePolicy.Parse).ToArray());
        var notices = new List<Notice>(plan.Notices);
        var reasons = BlockedReasons(pair, predicates, plan.Actions, rules);
        var blocked = reasons.Keys.ToHashSet();
        // Collision cleanup can have the occupant's ObjectKey rather than the move's. Follow shared paths
        // as well as keys, transitively, so no destination delete or baseline forget runs without its move.
        // Index only the skipped component so a single skip cannot multiply whole-plan memory.
        if (blocked.Count > 0 || plan.SkippedActions.Count > 0)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var action in blocked.Concat(plan.SkippedActions)) Index(action);
            // Keep only the blocked connected component, never dictionaries of the whole plan.
            // Repeat a streaming pass for reverse-ordered links; most plans settle in one pass.
            bool changed;
            do
            {
                changed = false;
                foreach (var action in plan.Actions)
                {
                    if (blocked.Contains(action) ||
                        !(action.ObjectKey is { } key && keys.Contains(key) || RowPaths(action).Any(paths.Contains))) continue;
                    blocked.Add(action);
                    Index(action);
                    changed = true;
                }
            } while (changed);
            void Index(SyncAction action)
            {
                foreach (var path in RowPaths(action)) paths.Add(path);
                if (action.ObjectKey is { } key) keys.Add(key);
            }
        }
        foreach (var action in plan.Actions)
        {
            if (!blocked.Contains(action))
            {
                kept.Add(action);
                continue;
            }
            var reason = reasons.GetValueOrDefault(action, new("held:related-effect")).Reason!;
            PlanProvenance.Current.Value?.Rejected(action, reason);
            skipped.Add(action);
            if (action.RebuildableRemove && CarriesNewlyIgnored(pair, action, rules))
                notices.Add(new Notice(pair.PairId, 40, NoticeKind.UserDecision, action.Path,
                    "The reviewed cloud removal was skipped because new ignore rules protect files inside. Nothing was removed." +
                    NoticeChoices.RemovalAttentionHelp, "reviewed-removal-skipped", RemovalSides: [action.Target]));
            // Holds already have a decision, evidence or retry notice owner. Eligibility only
            // explains the rejection; it must not create another problem for the same hold.
            if (reason.StartsWith("held:", StringComparison.Ordinal)) continue;
            notices.Add(EligibilityNotice(pair.PairId, action.Path, reason));
        }
        // An active exclusion is one pair-level notice, raised by every plan so it stays open while it applies.
        if (pair.Excluded.Count > 0 && !notices.Any(notice => notice.ReasonKey == PlanNotices.PairExcluded.Key))
            notices.Add(new Notice(pair.PairId, 40, NoticeKind.Info, string.Empty,
                PlanNotices.PairExcluded.Detail + ": " + string.Join(", ", pair.Excluded), PlanNotices.PairExcluded.Key));
        return plan with { Actions = kept, Notices = notices, SkippedActions = skipped };
    }

    private static Notice EligibilityNotice(string pairId, string path, string reason)
    {
        var boundary = reason is "outside-pair" or "excluded" or "ignored" or "product-owned" or "link" or "stub";
        return new(pairId, boundary ? OutsidePairRule : 40, NoticeKind.Info, path,
            (boundary ? PlanNotices.OutsidePairArea.Detail :
                string.Format(System.Globalization.CultureInfo.InvariantCulture, MessageCatalog.Default.Message("eligibility-rejected"), reason)) +
            " Entire planned effect, including any folder contents and related collision cleanup, skipped; no changes applied." +
            " Normal subsequent sync may retain or copy included contents. Reviewed rebuildable exclusions are retained at completion." +
            " Leave the protection in place, or include the item and resync.",
            boundary ? PlanNotices.OutsidePairArea.Key : "eligibility-" + reason);
    }
}

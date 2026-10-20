namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    internal const string ExcludedIntentReason = "excluded after planning; not applied";
    internal const string ExcludedIntentNoticeKey = "excluded-outstanding-intents";

    private static bool ReconcileAllowed(ActionGuard guard, SyncAction action)
    {
        // Group recovery bypasses any guard (exclusions, nested pairs) — restore-only, never delete.
        // Keep anchors and members together: per-path absence cannot prove unexpected children were restored.
        if (action.ProviderEffect?.Kind is "whole-folder-group" or "whole-folder-member") return true;
        if (!Allowed(guard, action)) return false;
        var source = action.Target == Side.A ? guard.B : guard.A;
        return action.Source is null || source(action.Source.Path);
    }

    private async Task<IReadOnlyList<ActionResult>> SettleExcludedIntentsAsync(PairDef pair,
        IReadOnlyList<SyncAction> intents, CancellationToken ct)
    {
        var uncertain = new List<ActionResult>();
        if (intents.Count == 0) return uncertain;
        var gen = _store.BeginGeneration(pair.A.RootId, RunKind);
        var jobChecks = new ProviderJobCheckCache();
        foreach (var intent in intents)
        {
            ct.ThrowIfCancellationRequested();
            var target = _endpoints.Writer(intent.Target == Side.A ? pair.A : pair.B);
            var source = _endpoints.Writer(intent.Target == Side.A ? pair.B : pair.A);
            // Reconciliation's normal positive proof rules, without even a Recovery preservation write
            // in the newly protected area. No executor apply/child expansion is reachable here.
            var grace = _settings.UnknownCommitGracePeriod;
            if ((intent.ExpectedTarget?.Kind == ItemKind.Folder || intent.Source?.Kind == ItemKind.Folder ||
                 intent.Kind == ActionKind.MkDir) && grace < TimeSpan.FromHours(2))
                grace = TimeSpan.FromHours(2);
            var observed = await EffectResolver.ResolveAsync(intent with { RecoveryFirst = false }, target, source,
                ct, grace, _time, jobChecks);
            ct.ThrowIfCancellationRequested();
            if (observed.Outcome == Outcome.Done)
                _store.CommitResult(pair.PairId, gen, intent, observed);
            else if (observed.Outcome == Outcome.Uncertain)
            {
                var pending = observed with { Reason = "excluded, awaiting provider confirmation: " + observed.Reason };
                _store.CommitResult(pair.PairId, gen, intent, pending);
                uncertain.Add(pending);
            }
            else
            {
                // Notice first: interruption can leave an extra warning, never a silently settled intent.
                RaiseExcludedIntentNotice(pair, [intent]);
                _store.CommitResult(pair.PairId, gen, intent,
                    intent.ProviderEffect?.Kind == SyncDirectDownloadCheckpoint.Kind ? observed :
                    new ActionResult(intent.ActionId, Outcome.Skipped, null, null, null, ExcludedIntentReason));
            }
        }
        if (uncertain.Count > 0)
            await SyncByteProgressStream.ReportCurrentProcessAsync("First-sync completion",
                $"{uncertain.Count} excluded, awaiting provider confirmation: " +
                string.Join(", ", intents.Where(intent => uncertain.Any(result => result.ActionId == intent.ActionId))
                    .SelectMany(RowPaths).Distinct(StringComparer.OrdinalIgnoreCase)), ProcessMessageKind.Notice);
        return uncertain;
    }

    private void RaiseExcludedIntentNotice(PairDef pair, IReadOnlyList<SyncAction> intents)
    {
        var paths = intents.SelectMany(RowPaths).Concat(pair.Excluded)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        var detail = ExcludedIntentReason +
            ". Options: include the folder and resync / leave excluded.\n" + string.Join("\n", paths) + "\n";
        var open = _store.OpenNotices(pair.PairId).FirstOrDefault(n => n.ReasonKey == ExcludedIntentNoticeKey);
        // Retain earlier paths across interrupted passes or later batches, using the same stable notice identity.
        if (open is not null)
        {
            var added = paths.Where(path => !open.Detail.Contains("\n" + path + "\n",
                StringComparison.OrdinalIgnoreCase)).ToArray();
            if (added.Length == 0) return;
            detail = open.Detail + string.Join("\n", added) + "\n";
            if (open.NoticeId is not null) _store.CloseNotice(open.NoticeId, "refreshed");
        }
        _store.AddNotices([new Notice(pair.PairId, OutsidePairRule, NoticeKind.Info, string.Empty,
            detail, ExcludedIntentNoticeKey)]);
    }

    private async Task<int> CompletionIntentPreflightAsync(PairDef pair)
    {
        var outstanding = _store.UnresolvedIntents(pair.PairId).Where(action => !IsQuarantinedIntent(pair, action)).ToArray();
        if (outstanding.Length == 0) return 0;
        var guard = ActionPredicates(pair, LeftAlone(pair));
        var excluded = GuardedActions(outstanding, guard).Where(entry => !ReconcileAllowed(entry.Guard, entry.Action))
            .Select(entry => entry.Action).ToArray();
        await SyncByteProgressStream.ReportCurrentProcessAsync("First-sync completion",
            $"{outstanding.Length} unresolved effect(s), including {excluded.Length} excluded, awaiting provider confirmation; " +
            "saved for later verification without blocking first-sync completion. " + string.Join(", ", excluded.SelectMany(RowPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)), ProcessMessageKind.Notice);
        return outstanding.Length;
    }
}

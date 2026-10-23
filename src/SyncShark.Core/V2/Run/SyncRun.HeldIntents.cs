namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    // Legacy summary from the first held-intents build; the combined verification summary (RehashProtection.Reason)
    // now carries both counts. The key is still recognized so an older open notice can be dismissed/read.
    private const string HeldIntentsKey = "held-intents";
    private const string LinkRetiredReason =
        "Under a link (junction/symlink) that is not followed; the pending effect was retired without applying it. See the links notice.";
    private const string StubRetiredReason =
        "A left-alone online-only placeholder; the pending effect was retired without applying it. See the stubs notice.";

    private string? PermanentIntentBoundary(PairDef pair, SyncAction action,
        IReadOnlyList<PairDef> pairs, IReadOnlyList<string> owned)
    {
        // Whole-folder restore receipts must stay together until positively reconciled.
        if (action.ProviderEffect?.Kind is "whole-folder-group" or "whole-folder-member") return null;
        // A boundary change cannot abandon an identity at a provider-owned intermediate.
        // Keep the intent until identity reconciliation positively resolves its location.
        if (action.ProviderEffect is not null &&
            _endpoints.Writer(action.Target == Side.A ? pair.A : pair.B) is IProviderEffectReconciler reconciler &&
            reconciler.CanResumeProviderEffect(action)) return null;
        var strict = action.Kind is ActionKind.Move or ActionKind.Delete;
        return PairInclude.PermanentBoundary(pair, action.Target, action.Path, strict, pairs, owned)
            ?? (action.FromPath is { } from
                ? PairInclude.PermanentBoundary(pair, action.Target, from, strict, pairs, owned) : null)
            ?? (action.Source is { } source
                ? PairInclude.PermanentBoundary(pair, action.Target == Side.A ? Side.B : Side.A,
                    source.Path, false, pairs, owned) : null);
    }

    private void RetireBoundaryIntent(PairDef pair, SyncAction action, string boundary, long generation)
    {
        // Notice precedes retirement, so interruption never silently discards the explanation.
        // Skipped retires the intent only: it does not commit a baseline or call an endpoint.
        _store.AddNotices([new Notice(pair.PairId, OutsidePairRule, NoticeKind.Info, boundary,
            "This path belongs to a nested pair or a product-owned folder. Its pending effect was retired without applying it or marking the item synced. " +
            "Sync it through the owning pair, or change the pair boundaries explicitly if ownership is wrong. Ignore dismisses this information; the boundary stays protected.",
            "permanent-boundary")]);
        // A tagged conflict successor needs the explicit-retirement mark so the store does not re-coerce its
        // retirement into Failed and retain it forever. Untagged intents keep their existing contract.
        // No effect and no baseline is written either way.
        var retiring = action.ConflictTransitionKey is null ? action : action with { SuppressReconciledBaseline = true };
        _store.CommitResult(pair.PairId, generation, retiring,
            new ActionResult(action.ActionId, Outcome.Skipped, null, null, null, "Permanent ownership boundary; not applied."));
        if (_store.SyncRetryAt(pair.PairId, action.Path) is { Kind: "sync", Held: null })
            _store.ClearSyncRetry(pair.PairId, action.Path);
    }

    // Retires an intent whose path can never sync while a link or left-alone stub sits at or above it. The standing
    // link/stub summary already explains the cause, so no new notice is raised; only the pending effect is retired.
    private void RetireProtectedIntent(PairDef pair, SyncAction action, long generation, string reason)
    {
        // Tagged successors stay pending in the caller: a temporary link/stub must not discard the
        // authority protecting the original conflict. Keep the store's retention backstop here too.
        _store.CommitResult(pair.PairId, generation, action,
            new ActionResult(action.ActionId, Outcome.Skipped, null, null, null, reason));
        if (_store.SyncRetryAt(pair.PairId, action.Path) is { Kind: "sync", Held: null })
            _store.ClearSyncRetry(pair.PairId, action.Path);
    }

    // M3: the held count merges into the existing local-rehash-pending summary, which the run raises/refreshes later
    // from the post-classification store count. Any legacy held-intents summary is superseded and closed here.
    private int RefreshHeldIntentSummary(PairDef pair)
    {
        _store.CloseNoticeFamily(pair.PairId, HeldIntentsKey);
        return _store.CountHeldIntents(pair.PairId);
    }

    private string ShowHeldIntentItems(PairDef pair, NoticeActionInput? input)
    {
        if (input?.Offset is > 0)
            return "Use the next cursor (--name) from the previous page, not an offset. No pending changes were modified.";
        if (input?.Name?.StartsWith("deferred:", StringComparison.Ordinal) == true && _store is SqliteStateStore deferredStore)
        {
            try
            {
                var cursor = System.Text.Json.JsonSerializer.Deserialize<string[]>(
                    Convert.FromBase64String(input.Name[9..]));
                if (cursor is not { Length: 2 } || cursor[1] is null || !long.TryParse(cursor[0],
                    System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var root))
                    throw new FormatException("Invalid deferred cursor");
                var remaining = deferredStore.DeferredPlanningDisplayPage(pair.PairId, root, cursor[1]).Take(50).ToArray();
                var deferredLines = remaining.Select(item => $"Deferred: {item.Path} ({item.Reason}; not synced)").ToList();
                if (remaining.Length == 0) deferredLines.Add("No remaining changes on this page.");
                else deferredLines.Add("Next cursor (--name): " + DeferredCursor(remaining[^1].Root, remaining[^1].ObjectId));
                return string.Join(Environment.NewLine, deferredLines);
            }
            catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or IndexOutOfRangeException)
            { return "That remaining-items cursor is invalid. Start Show remaining again without a cursor; no pending changes were modified."; }
        }
        IntentPage page;
        try { page = _store.HeldIntentsPage(pair.PairId, input?.Name, 50); }
        catch (ArgumentException)
        {
            return "That remaining-items cursor is invalid. Start Show remaining again without a cursor; no pending changes were modified.";
        }
        var lines = page.Items.Select(action => $"{action.Kind}: {action.Path} (pending verification; not synced)").ToList();
        if (page.NextCursor is { } next) lines.Add("Next cursor (--name): " + next);
        else if (_store is SqliteStateStore store)
        {
            var remaining = store.DeferredPlanningDisplayPage(pair.PairId).Take(Math.Max(0, 50 - lines.Count)).ToArray();
            lines.AddRange(remaining.Select(item => $"Deferred: {item.Path} ({item.Reason}; not synced)"));
            if (remaining.Length > 0)
                lines.Add("Next cursor (--name): " + DeferredCursor(remaining[^1].Root, remaining[^1].ObjectId));
            else if (lines.Count == 50 && store.CountDeferredPlanning(pair.PairId) > 0)
                lines.Add("Next cursor (--name): " + DeferredCursor(-1, ""));
        }
        if (lines.Count == 0) lines.Add("No remaining changes on this page. Counts refresh on the next run.");
        return string.Join(Environment.NewLine, lines);

        static string DeferredCursor(long root, string id) => "deferred:" + Convert.ToBase64String(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new[] {
                root.ToString(System.Globalization.CultureInfo.InvariantCulture), id }));
    }
}

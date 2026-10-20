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

    private static string? PermanentIntentBoundary(PairDef pair, SyncAction action,
        IReadOnlyList<PairDef> pairs, IReadOnlyList<string> owned)
    {
        // Whole-folder restore receipts must stay together until positively reconciled.
        if (action.ProviderEffect?.Kind is "whole-folder-group" or "whole-folder-member") return null;
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
        _store.CommitResult(pair.PairId, generation, action,
            new ActionResult(action.ActionId, Outcome.Skipped, null, null, null, "Permanent ownership boundary; not applied."));
        if (_store.SyncRetryAt(pair.PairId, action.Path) is { Kind: "sync", Held: null })
            _store.ClearSyncRetry(pair.PairId, action.Path);
    }

    // Retires an intent whose path can never sync while a link or left-alone stub sits at or above it. The standing
    // link/stub summary already explains the cause, so no new notice is raised; only the pending effect is retired.
    private void RetireProtectedIntent(PairDef pair, SyncAction action, long generation, string reason)
    {
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
        IntentPage page;
        try { page = _store.HeldIntentsPage(pair.PairId, input?.Name, 50); }
        catch (ArgumentException)
        {
            return "That remaining-items cursor is invalid. Start Show remaining again without a cursor; no pending changes were modified.";
        }
        var lines = page.Items.Select(action => $"{action.Kind}: {action.Path} (pending verification; not synced)").ToList();
        if (lines.Count == 0) lines.Add("No remaining changes on this page. Counts refresh on the next run.");
        if (page.NextCursor is { } next) lines.Add("Next cursor (--name): " + next);
        return string.Join(Environment.NewLine, lines);
    }
}

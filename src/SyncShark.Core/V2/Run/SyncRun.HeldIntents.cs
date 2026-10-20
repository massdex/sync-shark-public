namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private const string HeldIntentsKey = "held-intents";

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

    private int RefreshHeldIntentSummary(PairDef pair)
    {
        var count = _store.CountHeldIntents(pair.PairId);
        if (count == 0)
            _store.CloseNoticeFamily(pair.PairId, HeldIntentsKey);
        else
            _store.AddNotices([new Notice(pair.PairId, 0, NoticeKind.Info, string.Empty,
                $"{count} changes are waiting for background verification or protected evidence. " +
                "After the change journal is lost, background verification must confirm affected items before they can sync. " +
                "Leave background verification running; for quarantine or evidence holds, follow the existing item notice. " +
                "Show remaining lists the pending changes. They are not marked synced; other changes complete normally, and these are checked again on the normal interval.",
                HeldIntentsKey)]);
        return count;
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

namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    // Explicit action only: inventory never invokes this callback or the native hydrator.
    internal Func<RootDef, ItemState, CancellationToken, Task>? HydratePlaceholderForTest { get; init; }

    private string ShowOnlineOnlyItems(PairDef pair, int offset)
    {
        if (offset < 0 || offset > int.MaxValue - 50) throw new SyncRunRefusedException("Invalid page offset.");
        const int size = 50;
        var lines = new List<string> { $"Online-only files, offset {offset}. Refresh notices to act on these individual files. Scanning has not downloaded anything." };
        var notices = new List<Notice>(size);
        var skipped = 0;
        var more = false;
        foreach (var (root, side) in new[] { (pair.A, Side.A), (pair.B, Side.B) })
        {
            if (root.Provider != SyncProviders.Local) continue;
            ItemState? after = null;
            while (true)
            {
                var page = _store.LiveFlaggedPage(root.RootId, ItemFlags.Placeholder, after, size);
                if (page.Count == 0) break;
                foreach (var item in page)
                {
                    if (item.Kind != ItemKind.File || pair.Excluded.Any(path => PairPath.IsSelfOrUnder(item.Path, path))) continue;
                    if (skipped++ < offset) continue;
                    if (notices.Count == size) { more = true; break; }
                    notices.Add(new(pair.PairId, LinkExclusions.StubRule, NoticeKind.Info, item.Path,
                        "Online-only file left untouched. Make available offline downloads only this file (up to 256 MiB); the next run syncs it. Leave online-only excludes this path on both sides without deleting it.",
                        "online-only-item", Repair: new NoticeRepair(side, item)));
                    lines.Add($"{side}: {item.Path}");
                }
                if (more) break;
                var next = page[^1];
                if (after is not null && after.Path == next.Path && after.ObjectId == next.ObjectId) break;
                after = next;
            }
            if (more) break;
        }
        if (notices.Count > 0) _store.AddNotices(notices);
        if (more) lines.Add($"More items: next offset {offset + size}.");
        if (notices.Count == 0) lines.Add("No included online-only files on this page. Retry sync to refresh the inventory.");
        return string.Join(Environment.NewLine, lines);
    }

    private async Task<string> MakeNoticeAvailableOfflineAsync(PairDef pair, Notice notice, CancellationToken ct)
    {
        var repair = notice.Repair ?? throw new SyncRunRefusedException("Refresh the online-only file list first.");
        var root = repair.Side == Side.A ? pair.A : pair.B;
        if (root.Provider != SyncProviders.Local || !pair.Enabled)
            throw new SyncRunRefusedException("Enable the pair and choose a local online-only file. Nothing was downloaded.");
        RefuseLayout(pair);
        var include = BuildPredicates(pair, PairInclude.ForAction);
        if (!(repair.Side == Side.A ? include.A : include.B)(notice.Path))
            throw new SyncRunRefusedException("This path is excluded or belongs to a nested pair. Review folder settings first.");
        await _endpoints.PrepareAsync(pair, ct).ConfigureAwait(false);
        // LocalWriter observation may hash content; never use it on an online-only file.
        // The native hydrator verifies the captured identity through metadata-only handles.
        var current = _store.ByObject(root.RootId, repair.Item.ObjectId);
        if (!SameRepairItem(current, repair.Item))
            throw new SyncRunRefusedException("This file changed since the notice. Refresh the online-only list before downloading; nothing was changed.");
        OnlineOnlyHydration.Validate(current!);
        _store.AddNotices([notice with { Detail = "Making this file available offline; waiting for its cloud provider. Other files are not downloaded. This notice remains open until verified sync." }]);
        try
        {
            var demand = new SyncAction(ShortId.Make("hyd", pair.PairId, notice.NoticeId!), notice.Rule,
                ActionKind.CopyNew, repair.Side, notice.Path, null, current, null, false, null);
            var admission = _endpoints.Writer(root) is ILocalFreeSpaceAdmission space
                ? await space.AdmitFreeSpaceAsync(demand, ct).ConfigureAwait(false) : FreeSpaceAdmission.Granted;
            using var reservation = admission.Reservation;
            if (admission.Refusal is { } refusal) throw new IOException(refusal.Detail);
            if (HydratePlaceholderForTest is { } hydrate) await hydrate(root, current!, ct).ConfigureAwait(false);
            else await OnlineOnlyHydration.HydrateAsync(root, current!, _store, ct).ConfigureAwait(false);
            var hydrated = current! with { Flags = current!.Flags & ~ItemFlags.Placeholder, Hash = null };
            // Release only this freshly observed placeholder from inventory protection. Without this
            // metadata delta, the prior stub flag would exclude it from every later scan forever.
            var gen = _store.BeginGeneration(root.RootId, "explicit-hydration");
            var cursor = _store.Cursor(root.RootId) ?? "";
            _store.WriteDelta(new(root.RootId, repair.Side, gen, cursor, cursor,
                [new(ChangeKind.Upsert, hydrated.ObjectId, hydrated.Path, hydrated, null)], false, []));
            _store.CompleteGeneration(gen, cursor, false); // A single item never promotes root scan authority.
            _store.SaveSyncRetry(pair.PairId, new(notice.Path, "sync", 0, "Explicit hydration completed; re-observe and sync this file.", DateTimeOffset.MinValue));
            _store.AddNotices([notice with { Detail = "This file is available offline. The next sync will re-observe it; this notice closes only after verified sync." }]);
            return "Made available offline. The next run will sync this file; no file was deleted or replaced.";
        }
        catch (Exception error) when (RunFailure(error, ct))
        {
            _store.AddNotices([notice with { Detail = "Could not make this file available offline: " + BlockerDiagnostic.Describe(error) + " Retry Make available offline, or Leave online-only. Nothing was deleted." }]);
            return "Hydration was not verified. The notice remains open with the failure and available options.";
        }
    }
}

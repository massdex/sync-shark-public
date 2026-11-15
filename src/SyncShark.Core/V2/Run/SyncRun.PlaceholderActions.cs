namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    // Explicit action only: inventory never invokes these callbacks or the native hydrator.
    internal Func<RootDef, ItemState, CancellationToken, Task>? HydratePlaceholderForTest { get; init; }
    internal Action? AfterHydrationForTest { get; init; }
    private sealed record OnlineOnlyCursor(Side Side, ItemState Item);

    private string ShowOnlineOnlyItems(PairDef pair, string? pageToken)
    {
        OnlineOnlyCursor? cursor = null;
        if (pageToken is not null)
        {
            try
            {
                if (pageToken.Length > 16384) throw new FormatException();
                cursor = System.Text.Json.JsonSerializer.Deserialize<OnlineOnlyCursor>(Convert.FromBase64String(pageToken));
                if (cursor is null || cursor.Side is not (Side.A or Side.B) || cursor.Item is null ||
                    !PilotPath.IsWindowsTransferPath(cursor.Item.Path)) throw new FormatException();
            }
            catch (Exception error) when (error is FormatException or System.Text.Json.JsonException)
            { throw new SyncRunRefusedException("Invalid online-only page token. Review the first page again."); }
        }
        const int size = 50;
        var lines = new List<string> { "Online-only files. Refresh notices to act on these individual files. Scanning has not downloaded anything." };
        var notices = new List<Notice>(size);
        OnlineOnlyCursor? last = null;
        var more = false;
        foreach (var (root, side) in new[] { (pair.A, Side.A), (pair.B, Side.B) })
        {
            if (root.Provider != SyncProviders.Local || cursor?.Side == Side.B && side == Side.A) continue;
            ItemState? after = cursor is not null && cursor.Side == side ? cursor.Item : null;
            while (true)
            {
                var page = _store.LiveFlaggedPage(root.RootId, ItemFlags.Placeholder, after, size);
                if (page.Count == 0) break;
                foreach (var item in page)
                {
                    if (item.Kind != ItemKind.File || pair.Excluded.Any(path => PairPath.IsSelfOrUnder(item.Path, path))) continue;
                    if (notices.Count == size) { more = true; break; }
                    notices.Add(new(pair.PairId, LinkExclusions.StubRule, NoticeKind.Info, item.Path,
                        "Online-only file left untouched. Make available offline downloads only this file; large downloads ask for confirmation. Leave online-only excludes this path without deleting it.",
                        "online-only-item", Repair: new NoticeRepair(side, item)));
                    last = new(side, item);
                    lines.Add($"{side}: {item.Path}");
                }
                if (more) break;
                after = page[^1];
            }
            if (more) break;
        }
        if (notices.Count > 0) _store.AddNotices(notices);
        if (more) lines.Add("Next page token: " + Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(last)));
        if (notices.Count == 0) lines.Add("No included online-only files on this page.");
        return string.Join(Environment.NewLine, lines);
    }

    private async Task<string> MakeNoticeAvailableOfflineAsync(PairDef pair, Notice notice, CancellationToken ct, bool confirmed = false)
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
        var current = _store.ByObject(root.RootId, repair.Item.ObjectId);
        if (current is not null && current.Flags.HasFlag(ItemFlags.NeedsRehash) && !current.Flags.HasFlag(ItemFlags.Placeholder))
        {
            _store.SaveSyncRetry(pair.PairId, new(notice.Path, "sync", 0, "Re-verify hydrated file.", DateTimeOffset.MinValue));
            return "Hydrated but unverified. The next run will re-observe identity, path and content before syncing.";
        }
        if (!SameRepairItem(current, repair.Item))
            throw new SyncRunRefusedException("This file changed since the notice. Refresh the online-only list before downloading; nothing was changed.");
        OnlineOnlyHydration.Validate(current!);
        if (current!.Size > OnlineOnlyHydration.ConfirmationBytes && !confirmed)
        {
            var warning = $"This downloads {current.Size:N0} bytes and sync waits while the provider downloads. Choose Confirm large download to continue, or Leave online-only.";
            _store.AddNotices([notice with { Detail = warning }]);
            return warning;
        }
        var downloaded = false;
        void RecordHydration()
        {
            downloaded = true;
            // Hash debt protects effects, but allows metadata-first inventory to rediscover changes.
            // Never retain a stale stub flag after the provider successfully hydrated the handle.
            var hydrated = current! with { Flags = (current!.Flags & ~ItemFlags.Placeholder) | ItemFlags.NeedsRehash, Hash = null };
            _store.RequireRehash(root.RootId, current!, "Hydrated but unverified; identity and content verification pending.");
            var gen = _store.BeginGeneration(root.RootId, "explicit-hydration-unverified");
            var cursor = _store.Cursor(root.RootId) ?? "";
            _store.WriteDelta(new(root.RootId, repair.Side, gen, cursor, cursor,
                [new(ChangeKind.Upsert, hydrated.ObjectId, hydrated.Path, hydrated, null)], false, []));
            _store.CompleteGeneration(gen, cursor, false);
            _store.SaveSyncRetry(pair.PairId, new(notice.Path, "sync", 0, "Hydrated but unverified; re-observe before syncing.", DateTimeOffset.MinValue));
        }
        _store.AddNotices([notice with { Detail = "Making this file available offline; waiting for its cloud provider. Other files are not downloaded. This notice remains open until verified sync." }]);
        try
        {
            var demand = new SyncAction(ShortId.Make("hyd", pair.PairId, notice.NoticeId!), notice.Rule,
                ActionKind.CopyNew, repair.Side, notice.Path, null, current, null, false, null);
            var admission = _endpoints.Writer(root) is ILocalFreeSpaceAdmission space
                ? await space.AdmitFreeSpaceAsync(demand, ct).ConfigureAwait(false) : FreeSpaceAdmission.Granted;
            using var reservation = admission.Reservation;
            if (admission.Refusal is { } refusal) throw new IOException(refusal.Detail);
            if (HydratePlaceholderForTest is { } hydrate)
            {
                await hydrate(root, current!, ct).ConfigureAwait(false);
                RecordHydration();
                AfterHydrationForTest?.Invoke();
            }
            else await OnlineOnlyHydration.HydrateAsync(root, current!, _store, ct, RecordHydration).ConfigureAwait(false);
            _store.AddNotices([notice with { Detail = "This file is available offline. The next sync will re-observe it; this notice closes only after verified sync." }]);
            RaiseLinksSummary(pair, LeftAlone(pair));
            return "Made available offline. The next run will sync this file; no file was deleted or replaced.";
        }
        catch (Exception error) when (error is SyncRunRefusedException || RunFailure(error, ct))
        {
            var state = downloaded ? "Hydrated but unverified: " : "Could not make this file available offline: ";
            _store.AddNotices([notice with { Detail = state + BlockerDiagnostic.Describe(error) + " Retry to re-verify the file, or Leave online-only. Nothing was deleted." }]);
            return "Hydration was not verified. The notice remains open with the failure and available options.";
        }
        catch (OperationCanceledException)
        {
            _store.AddNotices([notice with { Detail = downloaded ? "Hydrated but unverified. Retry to re-verify." : "Download cancelled. Retry or Leave online-only." }]);
            throw;
        }
    }
}

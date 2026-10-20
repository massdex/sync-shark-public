namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private string ShowLinkItems(PairDef pair, int offset)
    {
        if (offset < 0 || offset > int.MaxValue - 50) throw new SyncRunRefusedException("Invalid page offset.");
        var notices = new List<Notice>(50);
        var lines = new List<string> { $"Links, offset {offset}. Refresh notices for individual actions. No targets have been scanned or copied." };
        var skipped = 0;
        var more = false;
        foreach (var (root, side) in new[] { (pair.A, Side.A), (pair.B, Side.B) })
        {
            if (root.Provider != SyncProviders.Local) continue;
            ItemState? after = null;
            while (true)
            {
                var page = _store.LiveFlaggedPage(root.RootId, ItemFlags.Link, after, 50);
                if (page.Count == 0) break;
                foreach (var item in page)
                {
                    if (pair.Excluded.Any(path => PairPath.IsSelfOrUnder(item.Path, path))) continue;
                    if (skipped++ < offset) continue;
                    if (notices.Count == 50) { more = true; break; }
                    notices.Add(new(pair.PairId, LinkExclusions.Rule, NoticeKind.Info, item.Path,
                        "Link left untouched. Show target is read-only; Sync target separately gives guidance. Create regular copy explicitly copies at most 256 MiB / 4096 entries beside this link. Nested links are never followed.",
                        "link-item", Repair: new NoticeRepair(side, item)));
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
        if (more) lines.Add($"More items: next offset {offset + 50}.");
        if (notices.Count == 0) lines.Add("No included links on this page. Retry sync to refresh inventory.");
        return string.Join(Environment.NewLine, lines);
    }

    private async Task<string> LinkNoticeActionAsync(PairDef pair, Notice notice, string choice, CancellationToken ct)
    {
        var repair = notice.Repair ?? throw new SyncRunRefusedException("Refresh the link list first.");
        var root = repair.Side == Side.A ? pair.A : pair.B;
        if (root.Provider != SyncProviders.Local || !PilotPath.IsWindowsTransferPath(repair.Item.Path))
            throw new SyncRunRefusedException("Choose a local link with a safe relative path. Use its provider to inspect other links.");
        try
        {
            var link = LocalItems.FullPath(root.RootPath, repair.Item.Path);
            if (choice is NoticeChoices.ShowTarget or NoticeChoices.SyncTargetSeparately)
            {
                var target = LinkContentCopy.Show(root.RootPath, link);
                var detail = $"Target: {target.Path}{Environment.NewLine}Inside pair root: {target.InsideRoot}. No files changed.";
                return choice == NoticeChoices.ShowTarget ? detail : detail + Environment.NewLine +
                    "Create a separate pair for the regular target folder (for a file, select its parent folder). Choose a non-overlapping cloud destination and review nested-pair rules. No pair was created." + Environment.NewLine +
                    "CLI hint: SyncShark.exe --cli pairs create --account <exact-account-id> --path <new-remote-path> --local <target-folder> --yes";
            }
            if (!pair.Enabled) throw new IOException("Enable the pair before copying, or sync the target separately.");
            RefuseLayout(pair);
            var include = BuildPredicates(pair, PairInclude.ForAction);
            if (!(repair.Side == Side.A ? include.A : include.B)(notice.Path))
                throw new IOException("The link is excluded or belongs to a nested pair. Review folder settings or Ignore.");
            await _endpoints.PrepareAsync(pair, ct).ConfigureAwait(false);
            var destination = (notice.Path.LastIndexOf('/') is var cut && cut >= 0 ? notice.Path[..(cut + 1)] : "") +
                "Link-copy-" + Guid.NewGuid().ToString("N") + (repair.Item.Kind == ItemKind.File ? Path.GetExtension(notice.Path) : "");
            if (!include.A(destination) || !include.B(destination)) throw new IOException("Copy destination is excluded. Review folder settings.");
            foreach (var endpoint in new[] { pair.A, pair.B })
                if (_store.ProtectionBatch(endpoint.RootId, ItemFlags.Link | ItemFlags.Placeholder | ItemFlags.NeedsRehash, [destination]).Any(p => p.Covered || p.Contains) ||
                    await _endpoints.Writer(endpoint).ObserveAsync(destination, ct).ConfigureAwait(false) is not null)
                    throw new IOException("Copy destination is occupied or protected. Choose Create regular copy again for a new name, or Ignore.");
            using var prepared = new LinkContentCopy.Prepared(root, repair.Item, choice == NoticeChoices.CopyLinkOutsideRoot, ct);
            var writer = _endpoints.Writer(root);
            var demand = new SyncAction(ShortId.Make("lnk", pair.PairId, destination), notice.Rule, ActionKind.CopyNew,
                repair.Side, destination, null, repair.Item with { Size = prepared.Bytes }, null, false, null);
            var admission = writer is ILocalFreeSpaceAdmission space
                ? await space.AdmitFreeSpaceAsync(demand, ct).ConfigureAwait(false) : FreeSpaceAdmission.Granted;
            using var reservation = admission.Reservation;
            if (admission.Refusal is { } refusal) throw new IOException(refusal.Detail);
            _store.SaveSyncRetry(pair.PairId, new(destination, "sync", 0, "Observe explicit regular link copy.", DateTimeOffset.MinValue));
            await prepared.CopyAsync(LocalItems.FullPath(root.RootPath, destination), ct).ConfigureAwait(false);
            var result = "Created regular copy: " + destination + ". It syncs normally on the next run. The link was not deleted or changed.";
            _store.AddNotices([notice with { Detail = result }]);
            return result;
        }
        catch (Exception error) when (RunFailure(error, ct))
        {
            var result = "Link action not completed: " + BlockerDiagnostic.Describe(error) +
                " Options: Show target, Sync target separately, Retry, or Ignore. Any already-created partial copy is retained; the link is unchanged.";
            // Read-only choices do not mutate even notice state.
            if (choice is not (NoticeChoices.ShowTarget or NoticeChoices.SyncTargetSeparately)) _store.AddNotices([notice with { Detail = result }]);
            return result;
        }
    }
}

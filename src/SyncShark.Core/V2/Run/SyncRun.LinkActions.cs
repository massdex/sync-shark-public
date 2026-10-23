using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private sealed record LinkPageCursor(Side Side, ItemState Item, long? DirNo = null);

    /// <summary>The most staging folders one link notice records; older entries fall off, none is ever deleted.</summary>
    private const int MaximumRetainedStaging = 20;

    /// <summary>Runs after publication and before the retry is queued, so a test can inject an interruption there.</summary>
    internal Action? AfterLinkPublishForTest { get; init; }

    private string ShowLinkItems(PairDef pair, NoticeActionInput? input)
    {
        if (input?.Offset is > 0) throw new SyncRunRefusedException("Link pages use the returned cursor with --name, not an offset. Start without a cursor to refresh.");
        LinkPageCursor? cursor = null;
        if (input?.Name is { } encoded)
        {
            try { cursor = JsonSerializer.Deserialize<LinkPageCursor>(Convert.FromBase64String(encoded)); }
            catch (Exception e) when (e is FormatException or JsonException) { throw new SyncRunRefusedException("Invalid link page cursor. Refresh without a cursor."); }
            if (cursor?.Item is null || !Enum.IsDefined(cursor.Side) || !Enum.IsDefined(cursor.Item.Kind) ||
                string.IsNullOrWhiteSpace(cursor.Item.Path) || !PilotPath.IsWindowsTransferPath(cursor.Item.Path))
                throw new SyncRunRefusedException("Invalid link page cursor. Refresh without a cursor.");
        }
        var notices = new List<Notice>(50);
        var lines = new List<string> { "Links (up to 50 per page). No targets have been scanned or copied." };
        LinkPageCursor? next = null;
        var budget = 200; // Bound excluded-row work as well as visible rows.
        // Only link notices are loaded (in SQL), and only this page stays open; repairs holding a location stay open.
        var openLinks = _store.OpenLinkNotices(pair.PairId);
        var priorByItem = new Dictionary<(Side, string), Notice>();
        foreach (var open in openLinks)
            if (open.Repair is { } prior) priorByItem[(prior.Side, prior.Item.ObjectId)] = open;
        foreach (var (root, side) in new[] { (pair.A, Side.A), (pair.B, Side.B) })
        {
            if (root.Provider != SyncProviders.Local || cursor?.Side == Side.B && side == Side.A) continue;
            var after = cursor?.Side == side ? cursor.Item : null;
            var afterDirNo = cursor?.Side == side ? cursor.DirNo : null;
            while (budget > 0 && notices.Count < 50)
            {
                var page = _store.LiveFlaggedPage(root.RootId, ItemFlags.Link, after, Math.Min(50 - notices.Count, budget), afterDirNo);
                afterDirNo = null; // Only the supplied cursor needs its carried key; later pages read live rows.
                if (page.Count == 0) { next = null; break; }
                foreach (var item in page)
                {
                    budget--;
                    after = item;
                    next = new(side, item);
                    if (pair.Excluded.Any(path => PairPath.IsSelfOrUnder(item.Path, path))) continue;
                    var notice = new Notice(pair.PairId, LinkExclusions.Rule, NoticeKind.Info, item.Path,
                        "Link left untouched. Show target is read-only. Create regular copy copies at most 256 MiB / 4096 entries beside this link. Nested links are never followed.",
                        "link-item", Repair: new NoticeRepair(side, item));
                    // Refresh carries the entity's published/retained locations; a stale in-progress text becomes interrupted.
                    if (priorByItem.TryGetValue((side, item.ObjectId), out var prior) && prior.Repair is { } priorRepair)
                        notice = notice with
                        {
                            Detail = RefreshedLinkDetail(root, prior, priorRepair),
                            Repair = new NoticeRepair(side, item, priorRepair.Destination, priorRepair.RetainedStaging)
                        };
                    notices.Add(notice);
                    lines.Add($"{side}: {item.Path}");
                }
            }
            // Keep the folder number with the cursor: a folder deleted before the next page must not end the side.
            if (next is { } lastOnSide && lastOnSide.Side == side && lastOnSide.DirNo is null)
                next = lastOnSide with { DirNo = _store.DirectoryNumber(root.RootId, RowCodec.Split(lastOnSide.Item.Path).Parent) };
            if (budget == 0 || notices.Count == 50) break;
        }
        // Only the current page stays open, except repairs that retain a copy/staging location.
        var current = notices.Select(n => (n.Repair!.Side, n.Repair.Item.ObjectId)).ToHashSet();
        foreach (var old in openLinks.Where(n => n.Repair?.Destination is null))
            if (old.Repair is { } repair && !current.Contains((repair.Side, repair.Item.ObjectId))) _store.CloseNotice(old.NoticeId!, "page-changed");
        if (notices.Count > 0) _store.AddNotices(notices);
        if (next is not null) lines.Add("Next cursor (--name): " + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(next))));
        if (notices.Count == 0) lines.Add("No included links on this page. Use the next cursor if present, or retry sync to refresh inventory.");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// A page refresh turns a stale "copy in progress" record into an interrupted one. No copy can be running here:
    /// listing holds the same exclusive gate as a copy. The staging folder is named only when it still exists.
    /// </summary>
    private static string RefreshedLinkDetail(RootDef root, Notice prior, NoticeRepair repair)
    {
        if (!prior.Detail.StartsWith("Copy in progress", StringComparison.Ordinal)) return prior.Detail;
        var stage = repair.Destination is { } destination && LocalItems.IsArtifactPath(destination) ? destination : null;
        var exists = stage is not null && root.Provider == SyncProviders.Local &&
            Directory.Exists(LocalItems.FullPath(root.RootPath, stage));
        return "The previous copy attempt was interrupted; no copy is running. " +
            (exists ? "Excluded staging: " + stage! + ". " : "") +
            "Retry rebuilds it; Remove staging gives manual cleanup guidance. The link is unchanged.";
    }

    /// <summary>Adds a created staging location to the bounded retained list, newest last.</summary>
    private static IReadOnlyList<string> RetainStage(IReadOnlyList<string>? retained, string stage)
    {
        var paths = new List<string>(retained ?? Array.Empty<string>());
        paths.RemoveAll(path => string.Equals(path, stage, StringComparison.OrdinalIgnoreCase));
        paths.Add(stage);
        if (paths.Count > MaximumRetainedStaging) paths.RemoveRange(0, paths.Count - MaximumRetainedStaging);
        return paths;
    }

    private async Task<string> LinkNoticeActionAsync(PairDef pair, Notice notice, string choice, CancellationToken ct, NoticeActionInput? input)
    {
        var repair = notice.Repair ?? throw new SyncRunRefusedException("Refresh the link list first.");
        var root = repair.Side == Side.A ? pair.A : pair.B;
        if (_store.ByObject(root.RootId, repair.Item.ObjectId) is { } live)
        {
            repair = repair with { Item = live };
            notice = notice with { Path = live.Path, Repair = repair };
        }
        if (root.Provider != SyncProviders.Local || !PilotPath.IsWindowsTransferPath(repair.Item.Path))
            throw new SyncRunRefusedException("Choose a local link with a safe relative path. Use its provider to inspect other links.");
        string? stage = null;
        try
        {
            if (choice == NoticeChoices.RemoveLinkStaging)
            {
                var retained = new List<string>(repair.RetainedStaging ?? Array.Empty<string>());
                if (repair.Destination is { } current && LocalItems.IsArtifactPath(current)) retained.Add(current);
                var paths = retained.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (paths.Length == 0)
                    return "No incomplete staging is recorded. A published copy is not staging and must not be removed by this option. " +
                        "Look for hidden .syncshark-transfer-*.partial folders beside the link and review them before deleting anything. No files changed.";
                return "Remove staging manually in Explorer after reviewing its contents:" + Environment.NewLine +
                    string.Join(Environment.NewLine, paths.Select(path => "- " + LocalItems.FullPath(root.RootPath, path))) + Environment.NewLine +
                    "These folders are excluded from sync, so Sync Shark never uploads them. Look for any other hidden " +
                    ".syncshark-transfer-*.partial folders beside the link. Sync Shark has not deleted anything.";
            }
            var link = LocalItems.FullPath(root.RootPath, repair.Item.Path);
            if (choice is NoticeChoices.ShowTarget or NoticeChoices.SyncTargetSeparately)
            {
                var target = LinkContentCopy.Show(root.RootPath, link);
                var detail = $"Target: {target.Path}{Environment.NewLine}Inside pair root: {target.InsideRoot}. No files changed.";
                return choice == NoticeChoices.ShowTarget ? detail : detail + Environment.NewLine +
                    "Create a separate pair for the regular target folder (for a file, select its parent folder). Choose a non-overlapping cloud destination and review nested-pair rules. No pair was created." + Environment.NewLine +
                    "CLI hint: SyncShark.exe --cli pairs create --account <exact-account-id> --path <new-remote-path> --local <target-folder> --yes";
            }
            if (repair.Destination is { } existing && !LocalItems.IsArtifactPath(existing) && choice != NoticeChoices.CopyLinkAgain)
                return "A copy was published at " + existing + ". Choose Copy again to create another; the previous copy is never overwritten.";
            if (!pair.Enabled) throw new IOException("Enable the pair before copying, or sync the target separately.");
            RefuseLayout(pair);
            var include = BuildPredicates(pair, PairInclude.ForAction);
            var included = repair.Side == Side.A ? include.A : include.B;
            if (!included(notice.Path)) throw new IOException("The link is excluded or belongs to a nested pair. Review folder settings or Ignore.");
            await _endpoints.PrepareAsync(pair, ct).ConfigureAwait(false);
            bool RequiresConfirmation(string path)
            {
                if (!LinkContentCopy.Under(path, root.RootPath)) return true;
                var relative = Path.GetRelativePath(root.RootPath, path).Replace('\\', '/');
                return LocalItems.IsArtifactPath(relative) || !included(relative) ||
                    _store.ProtectionBatch(root.RootId, ItemFlags.Link | ItemFlags.Placeholder | ItemFlags.NeedsRehash, [relative]).Any(p => p.Covered);
            }
            var confirm = choice == NoticeChoices.CopyLinkOutsideRoot || input?.Name is not null;
            using var prepared = new LinkContentCopy.Prepared(root, repair.Item, confirm, ct, input?.Name, RequiresConfirmation);
            var prefix = notice.Path.LastIndexOf('/') is var cut && cut >= 0 ? notice.Path[..(cut + 1)] : "";
            var leaf = Path.GetFileName(notice.Path);
            string destination = "";
            for (var suffix = 1; suffix <= 10000; suffix++)
            {
                destination = prefix + LinkContentCopy.CopyName(leaf, repair.Item.Kind, suffix);
                PilotPath.NormalizeTopLevelName(Path.GetFileName(destination));
                if (!include.A(destination) || !include.B(destination)) throw new IOException("Copy destination is excluded. Review folder settings.");
                var localDestination = LocalItems.FullPath(root.RootPath, destination);
                var occupied = File.Exists(localDestination) || Directory.Exists(localDestination);
                foreach (var endpoint in new[] { pair.A, pair.B })
                    occupied |= _store.ProtectionBatch(endpoint.RootId, ItemFlags.Link | ItemFlags.Placeholder | ItemFlags.NeedsRehash, [destination]).Any(p => p.Covered || p.Contains) ||
                        await _endpoints.Writer(endpoint).ObserveAsync(destination, ct).ConfigureAwait(false) is not null;
                if (!occupied) break;
                if (suffix == 10000) throw new IOException("No free copy name. Rename the link or review existing copies.");
            }
            var writer = _endpoints.Writer(root);
            var demand = new SyncAction(ShortId.Make("lnk", pair.PairId, destination), notice.Rule, ActionKind.CopyNew,
                repair.Side, destination, null, repair.Item with { Size = prepared.Bytes }, null, false, null);
            var admission = writer is ILocalFreeSpaceAdmission space
                ? await space.AdmitFreeSpaceAsync(demand, ct).ConfigureAwait(false) : FreeSpaceAdmission.Granted;
            using var reservation = admission.Reservation;
            if (admission.Refusal is { } refusal) throw new IOException(refusal.Detail);
            stage = prefix + LinkContentCopy.Prepared.StagingName();
            var retainedStaging = RetainStage(repair.RetainedStaging, stage);
            notice = notice with
            {
                Repair = repair with { Destination = stage, RetainedStaging = retainedStaging },
                Detail = "Copy in progress. Excluded staging: " + stage + ". Retry rebuilds; Remove staging gives manual cleanup guidance." +
                    (repair.Destination is null ? "" : " Earlier retained staging: " + repair.Destination)
            };
            _store.AddNotices([notice]); // Persist before any staging writes, including process interruption.
            await prepared.CopyAsync(LocalItems.FullPath(root.RootPath, destination), ct, LocalItems.FullPath(root.RootPath, stage)).ConfigureAwait(false);
            AfterLinkPublishForTest?.Invoke();
            var result = "Created regular copy: " + destination + ". It syncs normally on the next run. The link was not deleted or changed. Empty excluded staging: " + stage;
            // The copy already exists. Stop must not abandon its retained-location receipt or observation debt.
            using (FairStoreGate.WithCancellation(CancellationToken.None))
            {
                _store.AddNotices([notice with { Detail = result, Repair = repair with { Destination = destination, RetainedStaging = retainedStaging } }]);
                _store.SaveSyncRetry(pair.PairId, new(destination, "sync", 0, "Observe explicit regular link copy.", DateTimeOffset.MinValue));
            }
            return result;
        }
        catch (Exception error) when (error is OperationCanceledException || RunFailure(error, ct))
        {
            var retainedStage = stage is { } created && root.Provider == SyncProviders.Local &&
                Directory.Exists(LocalItems.FullPath(root.RootPath, created));
            var result = "Link action not completed: " + BlockerDiagnostic.Describe(error) +
                " Options: Show target, Sync target separately, Retry (rebuild), Remove staging, or Ignore. The link is unchanged." +
                (retainedStage ? " Retained excluded staging (never uploaded): " + stage! + "." : "") +
                (repair.Destination is null ? "" : " Previous retained location: " + repair.Destination);
            if (choice is not (NoticeChoices.ShowTarget or NoticeChoices.SyncTargetSeparately))
            {
                // Cancellation is the outcome being recorded, not authority to discard its staging/retry guidance.
                using var persistence = FairStoreGate.WithCancellation(CancellationToken.None);
                _store.AddNotices([notice with { Detail = result }]);
            }
            return result;
        }
    }
}

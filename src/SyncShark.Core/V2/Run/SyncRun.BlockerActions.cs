namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private async Task<string> RenameNoticeItemAsync(PairDef pair, Notice notice, string? name, CancellationToken ct)
    {
        var repair = notice.Repair ?? throw new SyncRunRefusedException("Refresh this notice to capture its item identity.");
        if (!pair.Enabled) throw new SyncRunRefusedException("Enable this pair before renaming.");
        RefuseLayout(pair);
        var destination = repair.Destination ?? BlockerNames.Destination(repair.Item.Path, name);
        if (repair.Destination is not null && name is not null && destination != BlockerNames.Destination(repair.Item.Path, name))
            throw new SyncRunRefusedException("The earlier rename must be verified before choosing a different name.");
        if ((pair.A.Provider == SyncProviders.Dropbox || pair.B.Provider == SyncProviders.Dropbox) &&
            BlockerNames.DropboxRefuses(destination))
            throw new SyncRunRefusedException("Dropbox does not accept that name; choose another. Nothing was renamed.");
        await _endpoints.PrepareAsync(pair, ct).ConfigureAwait(false);
        var a = _endpoints.Writer(pair.A);
        var b = _endpoints.Writer(pair.B);
        var writer = repair.Side == Side.A ? a : b;
        var root = repair.Side == Side.A ? pair.A : pair.B;
        var oppositeRoot = repair.Side == Side.A ? pair.B : pair.A;
        string InRoot(RootDef endpoint, string path) => root.Provider == SyncProviders.Dropbox && endpoint.Provider == SyncProviders.Local
            ? DropboxNameMapping.Map(path) : path;
        var oppositeDestination = InRoot(oppositeRoot, destination);
        var actionId = ShortId.Make("rnm", pair.PairId, notice.NoticeId!, destination);
        var action = new SyncAction(actionId, notice.Rule, ActionKind.Move, repair.Side, destination,
            repair.Item.Path, null, repair.Item, false, null)
        { SuppressReconciledBaseline = true, RequireMoveRevision = true, CallerOwnsNotice = true };
        // Use normal scope guards, including nested pairs, product-owned paths, links and placeholders.
        // A refused-name flag itself is not exclusion permission for a different entity.
        var guards = ActionPredicates(pair, LinkExclusions.None);
        foreach (var endpoint in new[] { pair.A, pair.B })
            if (_store.ProtectionBatch(endpoint.RootId, ItemFlags.Link | ItemFlags.Placeholder | ItemFlags.NeedsRehash,
                [InRoot(endpoint, repair.Item.Path), InRoot(endpoint, destination)]).Any(match => match.Covered || match.Contains))
                throw new SyncRunRefusedException("This rename would carry protected links, placeholders or unverified content. Nothing was changed.");
        if (GuardedActions([action, action with { Target = repair.Side == Side.A ? Side.B : Side.A, Path = oppositeDestination, FromPath = InRoot(oppositeRoot, repair.Item.Path) }], guards)
            .Any(entry => !Allowed(entry.Guard, entry.Action)))
            throw new SyncRunRefusedException("This path is protected by selection, exclusions, links or a nested pair. Review folder settings first.");
        if (_store.IntentById(pair.PairId, actionId) is { } pending)
        {
            var reconciled = await _executor.ReconcileAsync([pending], _store.BeginGeneration(root.RootId, RunKind), a, b, ct);
            if (reconciled.Single().Outcome != Outcome.Done)
                return "The earlier rename is not verified yet. The notice remains open; retry to check its outcome.";
        }
        else
        {
            var current = await writer.ObserveAsync(repair.Item.Path, ct);
            var existing = await writer.ObserveAsync(destination, ct);
            // Crash after commit, before returning: recognize only our captured exact identity and revision.
            if (repair.Destination is not null && current is null && SameRepairItem(existing, repair.Item))
                return ScheduleRenamedItem(pair, notice, destination);
            if (!SameRepairItem(current, repair.Item) && repair.Destination is null &&
                current is not null && current.ObjectId == repair.Item.ObjectId && current.Kind == repair.Item.Kind)
            {
                _store.AddNotices([notice with { Repair = repair with { Item = current } }]);
                return "This item changed since the notice was shown; the notice now shows the current version. Choose Rename again. Nothing was renamed.";
            }
            if (!SameRepairItem(current, repair.Item))
                throw new SyncRunRefusedException("The item changed identity or revision since this notice. Nothing was renamed. Rename the current item in its source folder, then Retry, or choose Keep excluded.");
            if (existing is not null || await (repair.Side == Side.A ? b : a).ObserveAsync(oppositeDestination, ct) is not null ||
                _store.LiveSpellings(pair.A.RootId, InRoot(pair.A, destination)).Count != 0 || _store.LiveSpellings(pair.B.RootId, InRoot(pair.B, destination)).Count != 0)
                throw new SyncRunRefusedException("That name is occupied in one of the two folders. Choose another name; nothing was overwritten.");
            if ((current!.Flags & (ItemFlags.Link | ItemFlags.Placeholder | ItemFlags.Locked)) != 0)
                throw new SyncRunRefusedException("This item is a link, online-only placeholder or locked item. It was left unchanged.");
            // Persist the destination BEFORE the effect. A restart cannot choose a second destination.
            _store.AddNotices([notice with { Repair = repair with { Destination = destination } }]);
            var plan = new Plan(pair.PairId, _store.BeginGeneration(root.RootId, RunKind), [action], []);
            await foreach (var result in _executor.ApplyAsync(plan, a, b, ct))
                if (result.Outcome != Outcome.Done)
                    return "Rename was not verified: " + result.Reason + ". The notice stays open; retry Rename to verify or resume.";
        }
        return ScheduleRenamedItem(pair, notice, destination);
    }

    private string ScheduleRenamedItem(PairDef pair, Notice notice, string destination)
    {
        _store.SaveSyncRetry(pair.PairId, new(destination, "sync", 0, "Renamed item awaiting verified sync.", DateTimeOffset.MinValue));
        _store.SaveSyncRetry(pair.PairId, new(notice.Path, "sync", 0, "Re-observe the renamed source path.", DateTimeOffset.MinValue));
        return "Renamed to " + destination + ". The old notice stays open until this identity is verified synced at its new name.";
    }

    private static bool SameRepairItem(ItemState? current, ItemState expected) => current is not null &&
        current.ObjectId == expected.ObjectId && current.Kind == expected.Kind && current.VersionId == expected.VersionId &&
        current.Hash == expected.Hash && current.Size == expected.Size && current.Mtime == expected.Mtime;

    private string RemainingItems(PairDef pair, Notice notice, int offset)
    {
        if (offset < 0 || offset > int.MaxValue - 100) throw new SyncRunRefusedException("Invalid page offset.");
        const int size = 50;
        var lines = new List<string> { $"Last observed direct children (offset {offset}, up to {size} per side). Inventory is not a fresh disk listing; Retry re-observes the folder. Nothing is deleted." };
        var more = false;
        foreach (var (root, side) in new[] { (pair.A, Side.A), (pair.B, Side.B) })
        {
            var children = _store.LiveChildrenPage(root.RootId, notice.Path, offset, size + 1);
            more |= children.Count > size;
            foreach (var child in children.Take(size))
            {
                var retry = _store.SyncRetryAt(pair.PairId, child.Path);
                var reason = retry?.LastError;
                reason ??= child.Flags != ItemFlags.None ? "Item state: " + child.Flags :
                    pair.Excluded.Any(prefix => PairPath.IsSelfOrUnder(child.Path, prefix)) ? "Excluded from sync" :
                    SyncIgnorePolicy.IsIgnored(child.Path, child.Kind == ItemKind.Folder, PairInclude.IgnoreRules(pair)) ? "Matches an ignore rule" :
                    child.Kind == ItemKind.Folder ? "Folder remains; inspect its children or retry for current evidence" :
                    "Still present in inventory; no verified removal recorded";
                lines.Add($"{side}: {child.Path} — {reason}");
            }
        }
        if (lines.Count == 1) lines.Add("No children recorded on this page. This is not proof that the folder is empty.");
        if (more) lines.Add($"More items: next offset {offset + size}.");
        return string.Join(Environment.NewLine, lines);
    }

    private void CloseObservedRepairs(PairDef pair, BothScan scan)
    {
        foreach (var notice in _store.OpenNotices(pair.PairId))
        {
            if (notice.NoticeId is null || notice.Repair is not { } repair) continue;
            var sideScan = repair.Side == Side.A ? scan.A : scan.B;
            if (sideScan.IncompleteSubtrees.Any(path => path.Length == 0 || PairPath.IsSelfOrUnder(repair.Item.Path, path))) continue;
            var root = repair.Side == Side.A ? pair.A : pair.B;
            var live = _store.ByObject(root.RootId, repair.Item.ObjectId);
            if (live is null)
                _store.CloseNotice(notice.NoticeId, "item-gone");
            else if (notice.ReasonKey == "link-item" && !PairPath.Same(live.Path, repair.Item.Path))
                _store.AddNotices([notice with { Path = live.Path, Repair = repair with { Item = live } }]);
            else if (notice.ReasonKey != "link-item" && repair.Destination is null && !PairPath.Same(live.Path, repair.Item.Path) &&
                !live.Flags.HasFlag(ItemFlags.ProviderNameRefused) &&
                PilotPath.IsWindowsTransferPath(DropboxNameMapping.MatchPath(live)) && !BlockerNames.DropboxRefuses(live.Path))
                _store.CloseNotice(notice.NoticeId, "name-repaired");
        }
    }

    private bool RepairSynced(Notice notice, Plan plan, Func<string, bool> evaluated, HashSet<string> unsettled)
    {
        if (notice.Repair is not { } repair || _store.Pair(plan.PairId) is not { } pair) return false;
        var root = repair.Side == Side.A ? pair.A : pair.B;
        var live = _store.ByObject(root.RootId, repair.Item.ObjectId);
        if (live is null || live.Flags.HasFlag(ItemFlags.ProviderNameRefused)) return false;
        return _store.BaselinesTouching(plan.PairId, repair.Side, [repair.Item.ObjectId], []).Any(basis =>
        {
            var item = repair.Side == Side.A ? basis.A : basis.B;
            return SameRepairItem(live, item) && PairPath.Same(live.Path, item.Path) &&
                evaluated(item.Path) && !unsettled.Contains(item.Path) &&
                (repair.Destination is null || PairPath.Same(item.Path, repair.Destination)) &&
                PairPath.Same(basis.A.Path, basis.B.Path) &&
                (basis.A.Kind == ItemKind.Folder && basis.B.Kind == ItemKind.Folder || Content.SameContent(basis.A, basis.B) == true);
        });
    }
}

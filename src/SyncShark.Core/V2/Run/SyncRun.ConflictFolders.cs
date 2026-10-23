using System.Globalization;

namespace Massdex.SyncShark.V2;

// Decisions 87/88 folder removals for explicit conflict choices. A folder's contents are frozen once in SQLite when
// the choice is made, then settled in bounded pages of one depth at a time, deepest first. A retry continues the same
// snapshot: an item added after the freeze is never in it, and an item changed after the freeze fails its conditional
// delete and is kept, so the folder itself stays. Every file is preserved (Recovery or provider history) first.
public sealed partial class SyncRun
{
    private const int ChoicePageSize = 256;

    private static int Depth(string path) => path.Count(ch => ch == '/');

    private static SyncAction FrozenChild(SyncAction folder, long ord, ItemState item) =>
        new(folder.ActionId + ":" + ord.ToString(CultureInfo.InvariantCulture), folder.Rule, ActionKind.Delete,
            folder.Target, item.Path, null, null, item, item.Kind == ItemKind.File, null)
        {
            SuppressReconciledBaseline = true,
            FrozenChoiceId = folder.ActionId,
            CallerOwnsNotice = true,
            // Each frozen folder is removed only after its own frozen contents; it is never expanded again, and its
            // contents were settled one by one, so no subtree-wide carried-ignored snapshot is taken for it.
            DescendantsPlanned = item.Kind == ItemKind.Folder,
            ContentsSettled = item.Kind == ItemKind.Folder,
        };

    // Metadata-only revocation must also run when the enclosing action is now excluded.
    // It cannot authorize an effect; it changes only previously carried rows to Kept.
    private void RevokeProtectedCarriedChoice(SyncAction folder, ActionGuard guards)
    {
        long after = -1;
        while (true)
        {
            var page = _store.ChoiceVictims(folder.ActionId, ChoiceVictimState.Carried, ChoicePageSize,
                false, after, ordinalOrder: true);
            if (page.Count == 0) break;
            _store.MarkChoiceVictims(folder.ActionId, page.Where(row => !Allowed(guards, FrozenChild(folder, row.Ord, row.Item)))
                .Select(row => (row.Ord, ChoiceVictimState.Kept)).ToArray());
            after = page[^1].Ord;
        }
    }

    /// <summary>Removes one folder's frozen contents; null when every frozen item is gone, else why it stopped.</summary>
    private async Task<ActionResult?> RemoveFrozenContentsAsync(PairDef pair, SyncAction folder, long gen,
        IEndpointWriter a, IEndpointWriter b, CancellationToken ct)
    {
        var op = folder.ActionId;
        // The manifest transaction owns the freeze. A legacy/incomplete manifest must never capture a later
        // tree on retry; dismiss and review a fresh choice instead, with existing Recovery untouched.
        var rootId = folder.Target == Side.A ? pair.A.RootId : pair.B.RootId;
        if (!_store.HasChoiceSnapshot(op))
            return new ActionResult(op, Outcome.Skipped, null, null, null,
                "The saved choice has no frozen contents. Dismiss it and review a fresh choice; nothing further was removed.");
        // Decision 80(c): content that appeared after the freeze (ignored or not) is never carried away by a folder
        // removal. One bounded SQLite query; when anything grew, frozen files still go but every folder is kept.
        var grown = _store.CountChoiceGrowth(op, rootId, folder.Path);
        var target = folder.Target == Side.A ? a : b;
        var other = folder.Target == Side.A ? b : a;
        // Ignore rules are handled as parent-carried content, not standalone deletes. All other guards remain.
        // Refusal/retained-counterpart checks use indexed existence queries, never a root-wide list per page.
        var guards = ActionPredicates(pair, LeftAlone(pair));
        var carryGuards = guards with { IgnoreRules = null };
        var carriedChanged = _store.ChoiceCarriedChanged(op, rootId);
        while (true)
        {
            var fetched = _store.ChoiceVictims(op, ChoiceVictimState.Pending, ChoicePageSize, deepestFirst: true);
            if (fetched.Count == 0) break;
            // One depth per batch: a folder is reached only after everything frozen beneath it was settled.
            var depth = Depth(fetched[0].Item.Path);
            var page = fetched.TakeWhile(victim => Depth(victim.Item.Path) == depth).ToArray();
            var protectedPage = _store.ChoiceRemovalProtectedBatch(pair, folder.Target, page.Select(row => row.Item).ToArray());
            var protectedByOrd = page.Select((row, index) => (row.Ord, Protected: protectedPage[index])).ToDictionary(row => row.Ord, row => row.Protected);
            var marks = new List<(long Ord, ChoiceVictimState State)>(page.Length);
            var apply = new List<SyncAction>();
            var byAction = new Dictionary<string, (long Ord, ItemState Item)>(StringComparer.Ordinal);
            ActionResult? stop = null;
            // An attempted item (it has a durable intent) is observed, never blindly repeated.
            var observed = await Task.WhenAll(page.Select(async victim =>
            {
                var action = FrozenChild(folder, victim.Ord, victim.Item);
                var intent = _store.IntentById(pair.PairId, action.ActionId);
                var result = intent is null ? null
                    : await EffectResolver.ResolveAsync(intent, target, other, ct,
                        preserved: RecoverySafety.PreservedReceipt(_store, pair.PairId, intent)).ConfigureAwait(false);
                return (victim, Action: (intent ?? action) with { CallerOwnsNotice = true }, Result: result);
            })).ConfigureAwait(false);
            foreach (var (victim, action, result) in observed)
            {
                byAction[action.ActionId] = victim;
                var mayWrite = result is null ||
                    result.Outcome == Outcome.Skipped && result.Reason == EffectResolver.NotApplied;
                // An intent authorizes verification only. A proven not-applied attempt must pass today's guards
                // before another write, just like an item never attempted. Uncertain effects remain pending.
                if (mayWrite)
                {
                    if (!Allowed(carryGuards, action) ||
                        protectedByOrd[victim.Ord] ||
                        victim.Item.Kind == ItemKind.Folder &&
                        (grown > 0 || carriedChanged))
                    {
                        if (result is not null) _store.CommitResult(pair.PairId, gen, action, result);
                        marks.Add((victim.Ord, ChoiceVictimState.Kept));
                        continue;
                    }
                    if (!Allowed(guards, action))
                    {
                        // No standalone effect for ignored content. The nearest nonignored parent removes it
                        // under the existing writer policy (Recovery, except trivial filename matches).
                        if (result is not null) _store.CommitResult(pair.PairId, gen, action, result);
                        marks.Add((victim.Ord, ChoiceVictimState.Carried));
                        continue;
                    }
                    apply.Add(action with { ProviderEffect = null });
                    continue;
                }
                if (result!.Outcome == Outcome.Done)
                {
                    _store.CommitResult(pair.PairId, gen, action, result);
                    marks.Add((victim.Ord, ChoiceVictimState.Removed));
                }
                else if (result.Outcome == Outcome.Skipped)
                {
                    _store.CommitResult(pair.PairId, gen, action, result);
                    marks.Add((victim.Ord, ChoiceVictimState.Kept));
                }
                else stop ??= result; // Still unverified: it stays pending for a later retry.
            }
            if (apply.Count > 0)
                await foreach (var result in ApplyAdmittedAsync(pair, new Plan(pair.PairId, gen, apply, []), a, b, ct)
                    .ConfigureAwait(false))
                {
                    if (!byAction.TryGetValue(result.ActionId, out var victim)) continue;
                    if (result.Outcome == Outcome.Done) marks.Add((victim.Ord, ChoiceVictimState.Removed));
                    // A conditional delete that did not match: the item changed (kept) or was already gone.
                    else if (result.Outcome == Outcome.Skipped)
                        marks.Add((victim.Ord, await target.ObserveAsync(victim.Item.Path, ct).ConfigureAwait(false) is null
                            ? ChoiceVictimState.Absent : ChoiceVictimState.Kept));
                    else stop ??= result; // A failed or uncertain effect stays pending; a retry observes it first.
                }
            _store.MarkChoiceVictims(op, marks);
            if (stop is not null) return stop;
            if (marks.Count == 0)
                return new ActionResult(op, Outcome.Skipped, null, null, null,
                    "Folder removal made no progress. Retry the same choice; nothing else was removed.");
        }
        var kept = _store.CountChoiceVictims(op, ChoiceVictimState.Kept);
        if (kept == 0 && grown == 0 && !carriedChanged &&
            !_store.ChoiceRemovalProtected(pair, folder.Target, folder.Path, true)) return null;
        var removed = _store.CountChoiceVictims(op, ChoiceVictimState.Removed);
        return new ActionResult(op, Outcome.Skipped, null, null, null,
            (grown > 0 ? $"{(grown >= 1000 ? "1000+" : grown.ToString(CultureInfo.InvariantCulture))} item(s) appeared under {folder.Path} after this choice. " : "") +
            $"{kept} item(s) under {folder.Path} changed or were kept, so the folder was kept. " +
            $"{removed} item(s) were removed after their content was preserved. Keep both to stop here, " +
            "or restore removed items from Recovery.");
    }

    private void CloseConflictNotice(string pairId, string noticeId, ConflictNoticeOperation operation, string choice)
    {
        // Close durably before reclaiming checkpoints. A crash may leave inert rows, never an open notice
        // whose safe retry/restore evidence has already been discarded.
        _store.CloseNotice(noticeId, choice);
        foreach (var action in operation.Actions.Where(action => action.Kind == ActionKind.Delete))
        {
            // Frozen rows own their receipts. Reclaim in bounded batches only after the notice is closed.
            string[] ReceiptIds(string key) => operation.RestorePath is { } copy
                ? [ShortId.Make("nrx", pairId, key), ShortId.Make("nrx", pairId, key + ":copy:" + copy)]
                : [ShortId.Make("nrx", pairId, key)];
            _store.ForgetRestoreReceipts(pairId, ReceiptIds(action.ActionId).Concat(ReceiptIds(action.ActionId + ":root")).ToArray());
            if (!FolderDelete.Expands(action)) continue;
            foreach (var state in new[] { ChoiceVictimState.Removed, ChoiceVictimState.Restored, ChoiceVictimState.RestoredCopy })
            {
                long after = 0;
                while (true)
                {
                    var page = _store.ChoiceVictims(action.ActionId, state, ChoicePageSize, false, after, ordinalOrder: true);
                    if (page.Count == 0) break;
                    _store.ForgetRestoreReceipts(pairId, page.SelectMany(row => ReceiptIds(action.ActionId + ":" + row.Ord.ToString(CultureInfo.InvariantCulture))).ToArray());
                    after = page[^1].Ord;
                }
            }
            _store.ForgetChoiceVictims(action.ActionId);
        }
        _store.ForgetChoiceVictims(noticeId + ":arrival");
        _store.ForgetConflictPayload(noticeId);
    }

    /// <summary>The manifest marker recording the generation at which a replace removed the occupant.</summary>
    internal static string ReplacedAtMarker(long gen) => "replaced-at-gen:" + gen.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// After a sync, closes a finished replace only on positive evidence that the exact incoming entity arrived: a
    /// sync link written after the replace places that entity at the destination, and both sides currently hold
    /// exactly that link's versions. Missing, stale or mismatched evidence keeps the notice open with Restore.
    /// </summary>
    private async Task CloseArrivedReplacementsAsync(PairDef pair, IReadOnlyList<Notice> open, long currentGen, CancellationToken ct)
    {
        foreach (var notice in open)
        {
            if (!NoticeChoices.AwaitsArrival(notice) || notice.NoticeId is null ||
                notice.ConflictVersions?.Incoming is not { } incoming) continue;
            var operation = notice.ConflictOperation!;
            if (operation.Restoring) continue;
            var markers = operation.Completed.Where(entry => entry.StartsWith("replaced-at-gen:", StringComparison.Ordinal))
                .Select(entry => long.Parse(entry["replaced-at-gen:".Length..], CultureInfo.InvariantCulture)).ToArray();
            if (markers.Length == 0)
            {
                // Interrupted between the last removal and its marker: start the evidence window now (never earlier).
                _store.SaveConflictOperation(pair.PairId, notice.NoticeId,
                    operation with { Completed = operation.Completed.Append(ReplacedAtMarker(currentGen)).ToArray() });
                continue;
            }
            var replacedAt = markers.Max();
            var sourceSide = incoming.Target == Side.A ? Side.B : Side.A;
            var source = sourceSide == Side.A ? notice.ConflictVersions.A : notice.ConflictVersions.B;
            // Called after the successful scan/apply: source deletion settles arrival, not
            // evidence of a transfer. Recovery is retained by closing the notice normally.
            if (_store.ByObject((sourceSide == Side.A ? pair.A : pair.B).RootId, source.ObjectId) is null)
            {
                CloseConflictNotice(pair.PairId, notice.NoticeId, operation, "source-deleted");
                continue;
            }
            var link = _store.BaselinesTouching(pair.PairId, sourceSide, [source.ObjectId], []).FirstOrDefault(basis =>
                basis.Gen > replacedAt &&
                (sourceSide == Side.A ? basis.A : basis.B).ObjectId == source.ObjectId &&
                PairPath.Same(DropboxNameMapping.MatchPath(incoming.Target == Side.A ? basis.A : basis.B),
                    (incoming.Target == Side.A ? pair.A : pair.B).Provider == SyncProviders.Local ? DropboxNameMapping.Map(incoming.Path) : incoming.Path));
            if (link is null) continue;
            var atDestination = incoming.Target == Side.A ? link.A : link.B;
            var atSource = sourceSide == Side.A ? link.A : link.B;
            ItemState?[] observed;
            try
            {
                observed = await Task.WhenAll(
                    _endpoints.Writer(incoming.Target == Side.A ? pair.A : pair.B).ObserveAsync(atDestination.Path, ct),
                    _endpoints.Writer(sourceSide == Side.A ? pair.A : pair.B).ObserveAsync(atSource.Path, ct)).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or System.Net.Http.HttpRequestException)
            {
                continue; // No evidence this run: the notice stays open with Restore.
            }
            static bool Holds(ItemState? current, ItemState linked) => current is not null &&
                current.Kind == linked.Kind && current.ObjectId == linked.ObjectId &&
                (linked.Kind == ItemKind.Folder || Content.SameVersion(current, linked) == true);
            if (!Holds(observed[0], atDestination) || !Holds(observed[1], atSource)) continue;
            if (source.Kind == ItemKind.Folder &&
                !FolderContentsArrived(pair, notice.NoticeId, sourceSide, source, incoming.Path, replacedAt, currentGen, ct)) continue;
            CloseConflictNotice(pair.PairId, notice.NoticeId, operation, "arrived");
        }
    }

    private bool FolderContentsArrived(PairDef pair, string noticeId, Side sourceSide,
        ItemState source, string destination, long replacedAt, long currentGen, CancellationToken ct)
    {
        var snapshot = noticeId + ":arrival";
        var sourceRoot = sourceSide == Side.A ? pair.A : pair.B;
        var targetRoot = sourceSide == Side.A ? pair.B : pair.A;
        var guards = ActionPredicates(pair, LeftAlone(pair));
        string DestinationPath(ItemState item)
        {
            var path = destination + item.Path[source.Path.Length..];
            return sourceRoot.Provider == SyncProviders.Dropbox && targetRoot.Provider == SyncProviders.Local
                ? DropboxNameMapping.Map(path) : path;
        }
        bool Syncable(ItemState item) => (item.Flags & (ItemFlags.Ignored | ItemFlags.ProviderNameRefused)) == 0 &&
            Allowed(guards, new SyncAction("arrival", 87, item.Kind == ItemKind.Folder ? ActionKind.MkDir : ActionKind.CopyNew,
                sourceSide == Side.A ? Side.B : Side.A, DestinationPath(item), null, item, null, false, null));
        if (!_store.HasChoiceSnapshot(snapshot) || _store.HasSyncableChoiceGrowth(snapshot, sourceRoot.RootId, source.Path, Syncable))
            return false;
        long after = -1;
        var allArrived = true;
        while (true)
        {
            var page = _store.ChoiceVictims(snapshot, ChoiceVictimState.Pending, ChoicePageSize, false, after, ordinalOrder: true);
            if (page.Count == 0) return allArrived;
            var marks = new List<(long, ChoiceVictimState)>();
            var links = _store.BaselinesTouching(pair.PairId, sourceSide, page.Select(row => row.Item.ObjectId), [])
                .ToLookup(basis => (sourceSide == Side.A ? basis.A : basis.B).ObjectId);
            foreach (var row in page)
            {
                ct.ThrowIfCancellationRequested();
                var expected = row.Item;
                var path = DestinationPath(expected);
                var currentSource = _store.ByObject(sourceRoot.RootId, expected.ObjectId);
                if (currentSource is null || !Syncable(expected with { Flags = currentSource.Flags }))
                {
                    marks.Add((row.Ord, ChoiceVictimState.Removed));
                    continue;
                }
                var link = links[expected.ObjectId].FirstOrDefault(basis => basis.Gen > replacedAt &&
                    RecoverySafety.MatchesHistoricalArrival(sourceSide == Side.A ? basis.A : basis.B, expected, sourceRoot.Provider) &&
                    PairPath.Same((sourceSide == Side.A ? basis.B : basis.A).Path, path));
                if (link is null) { allArrived = false; continue; }
                // A link committed this generation is positive verified-write/adoption evidence. Older links
                // additionally need today's inventory to retain both exact identities/versions. This avoids
                // two provider requests per descendant (2M requests for a 1M-item folder).
                if (link.Gen != currentGen)
                {
                    var linkedSource = sourceSide == Side.A ? link.A : link.B;
                    var linkedTarget = sourceSide == Side.A ? link.B : link.A;
                    var a = _store.ByObject(sourceRoot.RootId, linkedSource.ObjectId);
                    var b = _store.ByObject(targetRoot.RootId, linkedTarget.ObjectId);
                    if (a is null || b is null || !FrozenArrivalMatches(linkedSource, a) ||
                        !FrozenArrivalMatches(linkedTarget, b)) { allArrived = false; continue; }
                }
                marks.Add((row.Ord, ChoiceVictimState.Removed));
            }
            _store.MarkChoiceVictims(snapshot, marks);
            after = page[^1].Ord;
        }
    }

    private static bool FrozenArrivalMatches(ItemState linked, ItemState frozen) =>
        PairPath.Same(linked.Path, frozen.Path) && linked.Kind == frozen.Kind && linked.ObjectId == frozen.ObjectId &&
        linked.VersionId == frozen.VersionId && (frozen.Kind == ItemKind.Folder ||
            linked.Size == frozen.Size && linked.Mtime == frozen.Mtime &&
            Content.SameContent(linked, frozen) == true);

    /// <summary>Puts a replaced (removed) occupant back from Recovery while its path is still free.</summary>
    private async Task<ActionResult> RestoreReplacedAsync(PairDef pair, Notice notice, bool asCopy, CancellationToken ct)
    {
        var id = ShortId.Make("nrx", pair.PairId, notice.NoticeId!);
        ActionResult Result(Outcome outcome, string reason) => new(id, outcome, null, null, null, reason);
        if (!pair.Enabled) return Result(Outcome.Skipped, "Enable the pair to restore; nothing was changed.");
        var removal = notice.ConflictOperation!.Actions.Single(action => action.Kind == ActionKind.Delete);
        var victim = removal.ExpectedTarget!;
        var root = removal.Target == Side.A ? pair.A : pair.B;
        var writer = _endpoints.Writer(root);
        var gen = _store.BeginGeneration(root.RootId, RunKind);
        var guards = ActionPredicates(pair, LeftAlone(pair));
        if (!Allowed(guards, removal with { Kind = ActionKind.MkDir, ExpectedTarget = null, Source = victim }))
            return Result(Outcome.Skipped, "This path is excluded or protected now. Include it before restoring.");
        var operation = notice.ConflictOperation!;
        var copying = asCopy || operation.RestorePath is not null;
        var restorePath = operation.RestorePath ?? victim.Path;
        // Reuse only receipt-owned destinations. An unreceipted root OR child may belong
        // to an interrupted attempt, but cannot safely be adopted; restart at a fresh sibling.
        var freshCopy = copying && (operation.RestorePath is null ||
            !await CopyDestinationOwnedAsync(pair, removal, restorePath, root, writer, ct).ConfigureAwait(false));
        if (freshCopy)
        {
            AbandonRestoreIntents(pair, operation);
            // An explicit copy gets a unique sibling; CopyNew/MkDir still enforce no overwrite
            // if another process wins the name after this observation.
            var name = Path.GetFileName(victim.Path);
            var extension = victim.Kind == ItemKind.File && !(name.StartsWith(".", StringComparison.Ordinal) && name.IndexOf('.', 1) < 0)
                ? Path.GetExtension(victim.Path) : "";
            var stem = extension.Length == 0 ? victim.Path : victim.Path[..^extension.Length];
            do { restorePath = stem + " (restored copy " + Guid.NewGuid().ToString("N")[..12] + ")" + extension; }
            while (await writer.ObserveAsync(restorePath, ct).ConfigureAwait(false) is not null);
        }
        ItemState AtDestination(ItemState item) => item with { Path = restorePath + item.Path[victim.Path.Length..] };
        string RestoreKey(string key) => restorePath == victim.Path ? key : key + ":copy:" + restorePath;
        void Started()
        {
            if (operation.Restoring && operation.RestorePath == (copying ? restorePath : null)) return;
            operation = operation with { Restoring = true, RestorePath = copying ? restorePath : operation.RestorePath };
            _store.SaveConflictOperation(pair.PairId, notice.NoticeId!, operation);
        }
        if (victim.Kind == ItemKind.File)
        {
            if (await RestoreFileAsync(pair, removal.Target, root, writer, removal.ActionId, victim, gen, ct, AtDestination(victim).Path, RestoreKey(removal.ActionId), Started)
                    .ConfigureAwait(false) is { } failed) return failed;
        }
        else
        {
            // A receipt is written in the same transaction as the created result. A marker or matching hash
            // is not ownership evidence: a later occupant must never become a restore destination.
            if (await MakeFolderAsync(pair, removal.Target, writer, RestoreKey(removal.ActionId + ":root"), AtDestination(victim), gen, ct, Started)
                    .ConfigureAwait(false) is { } notCreated) return notCreated;
            // On resumption, already-created parents must still be the exact receipt identities before we
            // place more children beneath them. Scan receipts through the frozen rows, never a subtree list.
            long afterRestored = -1;
            while (true)
            {
                var restoredPage = _store.ChoiceVictims(removal.ActionId, copying ? ChoiceVictimState.RestoredCopy : ChoiceVictimState.Restored, ChoicePageSize, false, afterRestored, ordinalOrder: true);
                if (restoredPage.Count == 0) break;
                var folders = restoredPage.Where(row => row.Item.Kind == ItemKind.Folder).ToArray();
                var observations = writer is IEndpointMetadataPages metadata && folders.Length > 0
                    ? await metadata.ObservePageAsync(folders.Select(row => AtDestination(row.Item).Path).ToArray(), ct).ConfigureAwait(false) : null;
                foreach (var (ord, item) in folders)
                {
                    var childId = removal.ActionId + ":" + ord.ToString(CultureInfo.InvariantCulture);
                    if (await MakeFolderAsync(pair, removal.Target, writer, RestoreKey(childId), AtDestination(item), gen, ct, observations: observations).ConfigureAwait(false) is { } changed)
                        return changed;
                }
                afterRestored = restoredPage[^1].Ord;
            }
            // Shallowest first, so each parent exists before its contents; one bounded page at a time.
            while (true)
            {
                var page = _store.ChoiceVictims(removal.ActionId, ChoiceVictimState.Removed, ChoicePageSize, deepestFirst: false);
                if (copying)
                    page = page.Concat(_store.ChoiceVictims(removal.ActionId, ChoiceVictimState.Restored, ChoicePageSize, deepestFirst: false))
                        .OrderBy(row => Depth(row.Item.Path)).ThenBy(row => row.Ord).Take(ChoicePageSize).ToArray();
                if (page.Count == 0) break;
                page = page.TakeWhile(row => Depth(row.Item.Path) == Depth(page[0].Item.Path)).ToArray();
                var marks = new List<(long, ChoiceVictimState)>(page.Count);
                var actions = new List<SyncAction>(page.Count);
                var recoveries = new Dictionary<string, RecoveryRecord>(StringComparer.Ordinal);
                var ordinals = new Dictionary<string, long>(StringComparer.Ordinal);
                ActionResult? stop = null;
                // Fresh receipt checks are batched. Unattempted creates use SQLite evidence here,
                // then the executor performs its single fresh no-overwrite check at the effect boundary.
                var receiptPaths = page.Where(row => _store.ConflictRestoreReceipt(pair.PairId,
                    ShortId.Make("nrx", pair.PairId, RestoreKey(removal.ActionId + ":" + row.Ord.ToString(CultureInfo.InvariantCulture)))) is not null)
                    .Select(row => AtDestination(row.Item).Path).ToArray();
                Dictionary<string, ItemState?>? observations = null;
                if (root.Provider == SyncProviders.Dropbox)
                {
                    observations = page.ToDictionary(row => AtDestination(row.Item).Path, _ => (ItemState?)null, StringComparer.OrdinalIgnoreCase);
                    foreach (var item in _store.Live(root.RootId, observations.Keys.ToArray()))
                        observations[item.Path] = item;
                    if (receiptPaths.Length > 0)
                    {
                        if (writer is IEndpointMetadataPages metadata)
                            foreach (var observed in await metadata.ObservePageAsync(receiptPaths, ct).ConfigureAwait(false))
                                observations[observed.Key] = observed.Value;
                        else
                            foreach (var path in receiptPaths)
                                observations[path] = await writer.ObserveAsync(path, ct).ConfigureAwait(false);
                    }
                }
                foreach (var (ord, item) in page)
                {
                    var childId = removal.ActionId + ":" + ord.ToString(CultureInfo.InvariantCulture);
                    var restoreId = ShortId.Make("nrx", pair.PairId, RestoreKey(childId));
                    var action = new SyncAction(restoreId, 87, item.Kind == ItemKind.Folder ? ActionKind.MkDir : ActionKind.CopyNew,
                        removal.Target, AtDestination(item).Path, null, item, null, false, null)
                        { SuppressReconciledBaseline = true, ConflictRestore = true, CallerOwnsNotice = true };
                    var check = await CheckRestoreStepAsync(pair, root, writer, action, gen, ct, observations).ConfigureAwait(false);
                    if (check.Block is not null) { stop ??= check.Block; continue; }
                    if (check.Done) { marks.Add((ord, copying ? ChoiceVictimState.RestoredCopy : ChoiceVictimState.Restored)); continue; }
                    if (item.Kind == ItemKind.File)
                    {
                        var recovery = _store.RecoveryForAction(pair.PairId, childId);
                        if (!RecoverySafety.MatchesRemovalProvenance(recovery, pair.PairId, childId, removal.Target, item))
                        {
                            stop ??= new(restoreId, Outcome.Skipped, null, null, null, "Recovery does not match " + item.Path + "; nothing was overwritten.");
                            continue;
                        }
                        action = action with { Source = item with { Hash = recovery.Hash, Size = recovery.Size, VersionId = recovery.VersionId } };
                        recoveries[item.ObjectId] = recovery;
                    }
                    actions.Add(action);
                    ordinals[restoreId] = ord;
                }
                var reader = new RestoreBatchReader(root, writer, recoveries, actions.Select(action => action.Source!).ToArray());
                await foreach (var result in ApplyAdmittedAsync(pair, new Plan(pair.PairId, gen, actions, []),
                    removal.Target == Side.A ? writer : reader, removal.Target == Side.B ? writer : reader, ct).ConfigureAwait(false))
                {
                    if (result.Outcome == Outcome.Done && ordinals.TryGetValue(result.ActionId, out var ord))
                        marks.Add((ord, copying ? ChoiceVictimState.RestoredCopy : ChoiceVictimState.Restored));
                    else if (result.Outcome != Outcome.Done) stop ??= result;
                }
                _store.MarkChoiceVictims(removal.ActionId, marks);
                if (stop is not null)
                    return Result(stop.Outcome == Outcome.Refused ? Outcome.Refused : Outcome.Skipped,
                        (stop.Reason ?? "A restore step did not complete.") +
                        " Restored items so far stay; retry restore to continue. Nothing was overwritten.");
            }
        }
        CloseConflictNotice(pair.PairId, notice.NoticeId!, operation, copying ? NoticeChoices.RestoreAsCopy : NoticeChoices.RestoreExisting);
        return Result(Outcome.Done, "Restored the replaced item from Recovery. The incoming item stays where it was; " +
            "ignored files remain in Recovery. If both still clash, a new notice will ask again.");
    }

    private async Task<bool> CopyDestinationOwnedAsync(PairDef pair, SyncAction removal, string path,
        RootDef root, IEndpointWriter writer, CancellationToken ct)
    {
        async Task<bool> Owned(string key, string destination, IReadOnlyDictionary<string, ItemState?>? observations = null)
        {
            var current = observations is not null ? observations[destination]
                : await writer.ObserveAsync(destination, ct).ConfigureAwait(false);
            var receipt = _store.ConflictRestoreReceipt(pair.PairId,
                ShortId.Make("nrx", pair.PairId, key + ":copy:" + path));
            return current is null ? receipt is null : receipt is not null && SameConflictItem(current, receipt, root);
        }
        if (!await Owned(removal.ActionId + (removal.ExpectedTarget!.Kind == ItemKind.Folder ? ":root" : ""), path)) return false;
        if (removal.ExpectedTarget.Kind != ItemKind.Folder) return true;
        foreach (var state in new[] { ChoiceVictimState.Removed, ChoiceVictimState.Restored, ChoiceVictimState.RestoredCopy })
        {
            long after = 0;
            while (true)
            {
                var page = _store.ChoiceVictims(removal.ActionId, state, ChoicePageSize, false, after, ordinalOrder: true);
                if (page.Count == 0) break;
                var observations = writer is IEndpointMetadataPages metadata
                    ? await metadata.ObservePageAsync(page.Select(row => path + row.Item.Path[removal.Path.Length..]).ToArray(), ct).ConfigureAwait(false)
                    : null;
                // Fresh observations are needed only on an interrupted copy, not on normal first writes.
                foreach (var row in page)
                    if (!await Owned(removal.ActionId + ":" + row.Ord.ToString(CultureInfo.InvariantCulture),
                        path + row.Item.Path[removal.Path.Length..], observations)) return false;
                after = page[^1].Ord;
            }
        }
        return true;
    }

    // Only the executor schedules transfers. Each worker opens/verifies its own Recovery stream lazily,
    // so a page of 256 large files never means 256 open streams or buffers.
    private sealed class RestoreBatchReader(RootDef root, IEndpointWriter writer,
        IReadOnlyDictionary<string, RecoveryRecord> recoveries, IReadOnlyList<ItemState> sources) : IEndpointWriter
    {
        public Task<Stream> OpenReadAsync(ItemState state, CancellationToken ct) =>
            OpenRetainedNoticeBytesAsync(root, writer, recoveries[state.ObjectId], state, ct);
        public Task<ItemState?> ObserveAsync(string path, CancellationToken ct) =>
            Task.FromResult(sources.FirstOrDefault(item => PairPath.Same(item.Path, path)));
        public Task<ItemState> PutAsync(SyncAction action, Stream content, CancellationToken ct) => throw new NotSupportedException();
        public Task<ItemState> MoveAsync(SyncAction action, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(SyncAction action, CancellationToken ct) => throw new NotSupportedException();
        public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct) => throw new NotSupportedException();
    }

    private async Task<ActionResult?> RestoreFileAsync(PairDef pair, Side side, RootDef root, IEndpointWriter writer,
        string removalId, ItemState item, long gen, CancellationToken ct, string? destination = null, string? restoreKey = null, Action? started = null)
    {
        var restoreId = ShortId.Make("nrx", pair.PairId, restoreKey ?? removalId);
        var probe = new SyncAction(restoreId, 87, ActionKind.CopyNew, side, destination ?? item.Path, null, item, null, false, null)
            { SuppressReconciledBaseline = true, ConflictRestore = true, CallerOwnsNotice = true };
        var check = await CheckRestoreStepAsync(pair, root, writer, probe, gen, ct).ConfigureAwait(false);
        if (check.Done || check.Block is not null) return check.Block;
        var recovery = _store.RecoveryForAction(pair.PairId, removalId);
        if (recovery is null)
            return new(removalId, Outcome.Skipped, null, null, null, $"No Recovery record exists for {item.Path}.");
        if (!RecoverySafety.MatchesRemovalProvenance(recovery, pair.PairId, removalId, side, item))
            return new(removalId, Outcome.Skipped, null, null, null,
                "The Recovery record does not match this removed item. Nothing was restored or overwritten.");
        var source = item with { Hash = recovery.Hash, Size = recovery.Size, VersionId = recovery.VersionId };
        Stream bytes;
        try { bytes = await OpenRetainedNoticeBytesAsync(root, writer, recovery, source, ct).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new(removalId, Outcome.Skipped, null, null, null,
                $"The preserved copy of {item.Path} could not be read or verified: {error.Message}");
        }
        await using (bytes)
        {
            started?.Invoke();
            var action = probe with { Source = source };
            var reader = new RetainedBytesWriter(source, bytes);
            await foreach (var result in ApplyAdmittedAsync(pair, new Plan(pair.PairId, gen, [action], []),
                action.Target == Side.A ? writer : reader, action.Target == Side.B ? writer : reader, ct).ConfigureAwait(false))
                return result.Outcome == Outcome.Done ? null : result;
        }
        return new(removalId, Outcome.Skipped, null, null, null, "The restore stopped before completion.");
    }

    private bool ConflictChoicePending(PairDef pair, ConflictNoticeOperation operation, CancellationToken ct) =>
        operation.Actions.Any(action => _store.ConflictChoiceHasPending(pair.PairId, action.ActionId));

    private void AbandonRestoreIntents(PairDef pair, ConflictNoticeOperation operation)
    {
        void Abandon(string key)
        {
            foreach (var candidate in operation.RestorePath is { } copy ? new[] { key, key + ":copy:" + copy } : new[] { key })
            {
                var id = ShortId.Make("nrx", pair.PairId, candidate);
                if (_store.IntentById(pair.PairId, id) is { ConflictRestore: true } action)
                    _store.CommitResult(pair.PairId, 0, action,
                        new(id, Outcome.Skipped, null, null, null, EffectResolver.RestoreAbandoned));
            }
        }
        foreach (var action in operation.Actions.Where(action => action.Kind == ActionKind.Delete))
        {
            Abandon(action.ActionId);
            Abandon(action.ActionId + ":root");
            foreach (var state in Enum.GetValues<ChoiceVictimState>())
            {
                long after = 0;
                while (true)
                {
                    var page = _store.ChoiceVictims(action.ActionId, state, ChoicePageSize, false, after, ordinalOrder: true);
                    if (page.Count == 0) break;
                    foreach (var row in page) Abandon(action.ActionId + ":" + row.Ord.ToString(CultureInfo.InvariantCulture));
                    after = page[^1].Ord;
                }
            }
        }
    }

    private async Task<(bool Done, ActionResult? Block)> CheckRestoreStepAsync(PairDef pair, RootDef root,
        IEndpointWriter writer, SyncAction action, long gen, CancellationToken ct, IReadOnlyDictionary<string, ItemState?>? observations = null)
    {
        ActionResult Block(string reason) => new(action.ActionId, Outcome.Skipped, null, null, null, reason);
        var pending = _store.IntentById(pair.PairId, action.ActionId);
        if (pending is not null)
            _store.CommitResult(pair.PairId, gen, pending,
                new(pending.ActionId, Outcome.Skipped, null, null, null, EffectResolver.RestoreAbandoned));
        if (!Allowed(ActionPredicates(pair, LeftAlone(pair)), action))
            return (false, Block("This restore path is excluded or protected. Include it or leave its Recovery copy untouched."));
        var receipt = _store.ConflictRestoreReceipt(pair.PairId, action.ActionId);
        var current = observations is not null && observations.TryGetValue(action.Path, out var observed) ? observed
            : root.Provider == SyncProviders.Dropbox && receipt is null
            ? _store.Live(root.RootId, [action.Path]).FirstOrDefault()
            : await writer.ObserveAsync(action.Path, ct).ConfigureAwait(false);
        if (receipt is not null)
            return current is not null && SameConflictItem(current, receipt, root)
                ? (true, null) : (false, Block("A restored item changed or disappeared. Nothing was overwritten; the preserved copy remains in Recovery."));
        // Without a committed receipt an occupant is never ours, even when its bytes match.
        if (current is not null)
            return (false, Block("An item (possibly the incoming item or an interrupted restore) occupies " + action.Path +
                ". Choose Restore as a copy or Dismiss; nothing was overwritten."));
        return (false, null);
    }

    private async Task<ActionResult?> MakeFolderAsync(PairDef pair, Side side, IEndpointWriter writer, string removalId,
        ItemState folder, long gen, CancellationToken ct, Action? started = null, IReadOnlyDictionary<string, ItemState?>? observations = null)
    {
        var action = new SyncAction(ShortId.Make("nrx", pair.PairId, removalId), 87, ActionKind.MkDir, side,
            folder.Path, null, folder, null, false, null) { SuppressReconciledBaseline = true, ConflictRestore = true, CallerOwnsNotice = true };
        var check = await CheckRestoreStepAsync(pair, side == Side.A ? pair.A : pair.B, writer, action, gen, ct, observations).ConfigureAwait(false);
        if (check.Done || check.Block is not null) return check.Block;
        started?.Invoke();
        await foreach (var result in ApplyAdmittedAsync(pair, new Plan(pair.PairId, gen, [action], []), writer, writer, ct)
            .ConfigureAwait(false))
            return result.Outcome == Outcome.Done ? null : result;
        return new(removalId, Outcome.Skipped, null, null, null, "The folder could not be created.");
    }
}

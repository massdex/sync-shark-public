using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private const string RemovalRows = "removal-items";
    private const int RemovalPageSize = 50;
    private sealed record RemovalItem(Side Side, ItemState Item, string? Protected);
    private sealed record RemovalReview(string Path, string OriginReason, Side[] Sides, string Signature,
        string Policy, long Files, long Folders, long Bytes, long Protected, long Total, int MaxDepth, long ProtectedFolders = 0, bool Excluded = false, bool RetryNeeded = false);

    private async Task RemovalChoiceAsync(PairDef pair, Notice notice, string choice, CancellationToken ct,
        Action<SyncAction, ActionResult>? removalResult, string? observationSession)
    {
        if (_store is not SqliteStateStore db)
            throw new SyncRunRefusedException("Removal review needs the SQLite store. You can still open folder settings or dismiss.");
        if (choice is NoticeChoices.ReviewRemovalA or NoticeChoices.ReviewRemovalB)
        {
            notice = notice with { RemovalSides = [choice == NoticeChoices.ReviewRemovalA ? Side.A : Side.B] };
            choice = NoticeChoices.ReviewRemoval;
        }
        if (notice.RemovalSides is null && notice.ReasonKey == "reviewed-removal-skipped" && RebuildableSelection.Sides(pair) is { } rebuildable)
            notice = notice with { RemovalSides = [rebuildable.Cloud] };
        var header = db.ReadRemovalReview(pair, notice.Path);
        if (header?.Digest == "consumed")
        {
            // A previous cleanup may have timed out. Retire old consent before any new review;
            // the durable attention notice remains, and an old confirmation cannot replay it.
            var consumedGeneration = header.Generation;
            FairStoreGate.Cleanup(() => db.ClearRemovalReview(consumedGeneration));
            header = null;
        }
        var binding = notice.ReasonKey.StartsWith(NoticeChoices.RemovalReviewPrefix, StringComparison.Ordinal)
            ? notice.ReasonKey[NoticeChoices.RemovalReviewPrefix.Length..].Split(':') : [];
        var valid = binding.Length == 2 && header is not null && header.Generation == binding[0] && header.Digest != "consumed";
        if (choice == NoticeChoices.Dismiss)
        {
            var canceled = header is null ? null : JsonSerializer.Deserialize<RemovalReview>(header.MetadataJson);
            if (header is not null && binding.Length == 2 && binding[0] == header.Generation)
                db.ClearRemovalReview(header.Generation);
            _store.AddNotices([new Notice(pair.PairId, 40, NoticeKind.UserDecision, notice.Path,
                "Removal review canceled. No removal was authorized." + NoticeChoices.RemovalAttentionHelp,
                canceled?.OriginReason ?? notice.RemovalOriginReason ?? "reviewed-folder-grew", RemovalSides: canceled?.Sides ?? notice.RemovalSides)]);
            _store.CloseNotice(notice.NoticeId!, choice);
            return;
        }
        if (choice != NoticeChoices.ReviewRemoval && !valid)
        {
            _store.AddNotices([new Notice(pair.PairId, 40, NoticeKind.UserDecision, notice.Path,
                "This snapshot was replaced, invalidated by settings, or already consumed. Nothing new was removed." + NoticeChoices.RemovalAttentionHelp,
                notice.RemovalOriginReason ?? "reviewed-folder-grew", RemovalSides: notice.RemovalSides)]);
            _store.CloseNotice(notice.NoticeId!, "review-invalidated");
            return;
        }
        if (choice is NoticeChoices.RemovalNextPage or NoticeChoices.RemovalPreviousPage)
        {
            var review = JsonSerializer.Deserialize<RemovalReview>(header!.MetadataJson)!;
            var page = long.Parse(binding[1], System.Globalization.CultureInfo.InvariantCulture);
            page = Math.Clamp(page + (choice == NoticeChoices.RemovalNextPage ? 1 : -1), 0, Math.Max(0, (review.Total - 1) / RemovalPageSize));
            if (page == long.Parse(binding[1], System.Globalization.CultureInfo.InvariantCulture)) return;
            ShowRemovalPage(db, pair, header!, review, page);
            _store.CloseNotice(notice.NoticeId!, choice);
            return;
        }
        if (!_store.FirstSyncDone(pair.PairId))
            throw new SyncRunRefusedException("Finish the initial review first, or open folder settings. Nothing was removed.");
        RefuseLayout(pair);
        await _endpoints.PrepareAsync(pair, ct);
        var observation = pair with { Excluded = pair.Excluded.Where(path => !PairPath.Same(path, notice.Path)).ToArray() };
        var session = observationSession ?? throw new InvalidOperationException("Missing removal observation.");
        // Isolated, prefix-only observations never overwrite sync inventory, cursors or exclusions.
        if (OutstandingFootprints(pair.PairId).Any(path => PairPath.IsSelfOrUnder(path, notice.Path) || PairPath.IsSelfOrUnder(notice.Path, path)))
            throw new SyncRunRefusedException("An earlier effect is still being verified here. Retry review after verification; nothing new was removed.");
        var policy = JsonSerializer.Serialize(new { pair.A, pair.B,
            Excluded = pair.Excluded.Where(Touches).OrderBy(p => p, StringComparer.Ordinal),
            pair.IgnoreRules, Owned = _productOwned(pair).Where(Touches).OrderBy(p => p, StringComparer.Ordinal) });
        bool Touches(string path) => PairPath.IsSelfOrUnder(path, notice.Path) || PairPath.IsSelfOrUnder(notice.Path, path);

        if (choice == NoticeChoices.ReviewRemoval)
        {
            var sides = notice.RemovalSides?.Distinct().ToArray();
            // Legacy notices did not retain the target side. Never guess a both-sided removal.
            sides ??= new[] { Side.A, Side.B }.Where(side => db.RemovalObservation(session, side).Any(item => PairPath.Same(item.Path, notice.Path))).ToArray();
            if (notice.RemovalSides is null && sides.Length != 1)
                throw new SyncRunRefusedException("This older notice has no saved removal side. Choose Review removal on A (left) or B (right), or open folder settings to inspect both copies; neither was removed.");
            RemovalReview? review = null;
            var published = db.WritePrepare(pair.PairId, writer =>
            {
                long ordinal = 0, files = 0, folders = 0, bytes = 0, kept = 0, blockedOrdinal = 0;
                var maxDepth = 0;
                var retryNeeded = false;
                using var hash = new DeleteSourceGuard.ReviewSignature();
                foreach (var row in Snapshot())
                {
                    ct.ThrowIfCancellationRequested();
                    retryNeeded |= sides.Contains(row.Side) && (row.Item.IncompleteReason is not null ||
                        (row.Item.Flags & (ItemFlags.Unreadable | ItemFlags.Locked | ItemFlags.NeedsRehash)) != 0 ||
                        row.Item.Kind == ItemKind.File && row.Item.Flags == ItemFlags.None && row.Item.Hash is null);
                    var json = JsonSerializer.Serialize(row);
                    hash.Add(json);
                    writer.Add(RemovalRows, ordinal, FolderDelete.VictimKey(row.Side, row.Item.Path), json);
                    var depth = row.Item.Path.Count(ch => ch == '/');
                    maxDepth = Math.Max(maxDepth, depth);
                    if (row.Protected is null && row.Item.Kind == ItemKind.Folder)
                        writer.Add("removal-folders-" + depth, ordinal, FolderDelete.VictimKey(row.Side, row.Item.Path), json);
                    ordinal++;
                    if (row.Protected is not null)
                    {
                        kept++;
                        foreach (var ancestor in PairPath.Ancestors(row.Item.Path).Where(p => PairPath.IsSelfOrUnder(p, notice.Path)))
                            writer.AddUnique("removal-protected-parents", blockedOrdinal++, FolderDelete.VictimKey(row.Side, ancestor), "true");
                    }
                    else if (row.Item.Kind == ItemKind.File) { files++; bytes += row.Item.Size; }
                    else folders++;
                }
                var origin = NoticeChoices.IsRemovalAttention(notice) ? notice.ReasonKey :
                    notice.RemovalOriginReason ?? (header is not null ? JsonSerializer.Deserialize<RemovalReview>(header.MetadataJson)!.OriginReason : "reviewed-folder-grew");
                review = new(notice.Path, origin,
                    sides, hash.Finish(), policy, files, folders, bytes, kept, ordinal, maxDepth, Excluded: pair.Excluded.Any(p => PairPath.Same(p, notice.Path)), RetryNeeded: retryNeeded);
                writer.Commit(JsonSerializer.Serialize(review), review.Signature);
            }, SqliteStateStore.RemovalKey(pair, notice.Path));
            review = review! with { ProtectedFolders = db.ProtectedRemovalFolders(published.Generation) };
            db.SetRemovalMetadata(published.Generation, JsonSerializer.Serialize(review));
            ShowRemovalPage(db, pair, published, review!, 0);
            _store.CloseNotice(notice.NoticeId!, choice);
            return;
        }
        var approvedHeader = header!;
        var frozen = JsonSerializer.Deserialize<RemovalReview>(approvedHeader.MetadataJson)!;
        if (frozen.Path != notice.Path || frozen.Policy != policy ||
            !DeleteSourceGuard.ReviewedRowsUnchanged(frozen.Signature, Snapshot().Select(row => JsonSerializer.Serialize(row))))
        {
            Hold("The folder or its rules changed after review. Nothing was removed. Review the new contents before confirming.");
            return;
        }
        // Keep a durable hold through interruption. Reconciliation may verify submitted intents,
        // but neither a restart nor an old page can replay this confirmation.
        _store.AddNotices([new Notice(pair.PairId, 40, NoticeKind.UserDecision, notice.Path,
            "Confirmed removal is in progress or was interrupted. Verify existing effects before reviewing the remainder." + NoticeChoices.RemovalAttentionHelp,
            frozen.OriginReason, RemovalSides: frozen.Sides)]);
        db.ConsumeRemovalReview(approvedHeader.Generation);
        _store.CloseNotice(notice.NoticeId!, choice);
        var failed = false;
        Exception? primaryError = null;
        try
        {
            await ApplyRows(RemovalRows, filesOnly: true);
            // Real dependency: only emptied folders may be removed, deepest first. Independent files
            // and both directions within each bounded page use the normal executor's concurrent lanes.
            for (var depth = frozen.MaxDepth; depth >= 0; depth--)
                await ApplyRows("removal-folders-" + depth, filesOnly: false);
        }
        catch (Exception error) { primaryError = error; throw; }
        finally { FairStoreGate.Cleanup(() => db.ClearRemovalReview(approvedHeader.Generation), primaryError: primaryError); }

        async Task ApplyRows(string kind, bool filesOnly)
        {
            long after = -1;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = db.ReadPrepareRows(approvedHeader.Generation, kind, after);
                if (page.Count == 0) break;
                var actions = new List<SyncAction>();
                foreach (var stored in page)
                {
                    var row = JsonSerializer.Deserialize<RemovalItem>(stored.Json)!;
                    if (ProtectedReason(db, approvedHeader, row) is not null || filesOnly && row.Item.Kind != ItemKind.File) continue;
                    var item = row.Item;
                    actions.Add(new SyncAction(PlanIds.ActionId(pair.PairId + ":removal:" + approvedHeader.Generation, 0, 40,
                        ActionKind.Delete, row.Side, item.Path, null), 40, ActionKind.Delete, row.Side, item.Path,
                        null, null, item, item.Kind == ItemKind.File, null)
                    { DescendantsPlanned = true, PreserveIgnoredChildren = true, RemovalReviewChoice = true });
                }
                var plan = new Plan(pair.PairId, 0, actions, []);
                var byId = actions.ToDictionary(a => a.ActionId);
                var reported = 0;
                await foreach (var result in ApplyAdmittedAsync(pair, plan, _endpoints.Writer(pair.A), _endpoints.Writer(pair.B), ct))
                {
                    reported++;
                    failed |= result.Outcome != Outcome.Done;
                    if (removalResult is not null && byId.TryGetValue(result.ActionId, out var executed))
                        removalResult(executed, result);
                }
                ct.ThrowIfCancellationRequested();
                failed |= reported != actions.Count;
                after = page[^1].Ordinal;
            }
        }
        db.ClearRemovalReview(approvedHeader.Generation);
        if (frozen.RetryNeeded && !failed) Hold("Readable reviewed items completed. Some children could not be verified and were kept with their ancestors. Fix access and Retry review, or dismiss (Ignore) to keep them.");
        else if (failed) Hold("Some reviewed items changed or could not be removed. Completed removals remain recoverable. Review the remaining contents again.");
        else foreach (var old in _store.UnresolvedNotices(pair.PairId).Where(item =>
            PairPath.Same(item.Path, notice.Path) && (NoticeChoices.IsRemovalAttention(item) || NoticeChoices.IsRemovalReview(item))))
            _store.CloseNotice(old.NoticeId!, "removed-reviewed-content");
        return;

        IEnumerable<RemovalItem> Snapshot()
        {
            var selected = choice == NoticeChoices.ReviewRemoval ? notice.RemovalSides?.ToArray() :
                JsonSerializer.Deserialize<RemovalReview>(header!.MetadataJson)!.Sides;
            selected ??= new[] { Side.A, Side.B }.Where(side => db.RemovalObservation(session, side).Any(item => PairPath.Same(item.Path, notice.Path))).ToArray();
            var rules = PairInclude.IgnoreRules(pair);
            var protectedPaths = LeftAlone(pair).WithPending(_store.UnresolvedNotices(pair.PairId)
                .Where(item => PlanNotices.HasPathScope(item) && item.Kind == NoticeKind.UserDecision &&
                    !(PairPath.Same(item.Path, notice.Path) && (NoticeChoices.IsRemovalAttention(item) || NoticeChoices.IsRemovalReview(item))))
                .Select(item => item.Path));
            foreach (var side in new[] { Side.A, Side.B })
            {
                var root = side == Side.A ? pair.A : pair.B;
                var allowed = PairInclude.ForAction(observation, side, _store.Pairs(), _productOwned(pair));
                foreach (var item in db.RemovalObservation(session, side))
                {
                    ct.ThrowIfCancellationRequested();
                    var reason = !selected.Contains(side) ? "other side (not selected)" :
                        SyncIgnorePolicy.IsIgnored(item.Path, item.Kind == ItemKind.Folder, rules) ? "ignore rule" :
                        item.IncompleteReason is not null ? "could not be read: " + item.IncompleteReason + "; fix access and retry review, or Ignore (keep)" :
                        item.Flags != ItemFlags.None ? "item flags: " + item.Flags :
                        !allowed(item.Path) || protectedPaths.Contains(item.Path) || protectedPaths.Covers(item.Path) ? "protected path" :
                        item.Kind == ItemKind.File && item.Hash is null ? "content not verified" : null;
                    yield return new(side, item, reason);
                }
            }
        }
        void Hold(string detail)
        {
            db.ClearRemovalReview(approvedHeader.Generation);
            foreach (var old in _store.UnresolvedNotices(pair.PairId).Where(item => PairPath.Same(item.Path, notice.Path) &&
                (NoticeChoices.IsRemovalAttention(item) || NoticeChoices.IsRemovalReview(item))))
                _store.CloseNotice(old.NoticeId!, "held");
            _store.AddNotices([new Notice(pair.PairId, 40, NoticeKind.UserDecision, notice.Path,
                detail + NoticeChoices.RemovalAttentionHelp, frozen.OriginReason, RemovalSides: frozen.Sides)]);
            _store.CloseNotice(notice.NoticeId!, "held");
        }
    }

    private static string? ProtectedReason(SqliteStateStore db, PrepareHeader header, RemovalItem row) =>
        row.Protected ?? (row.Item.Kind == ItemKind.Folder && db.FindPrepareRow(header.Generation,
            "removal-protected-parents", FolderDelete.VictimKey(row.Side, row.Item.Path)) is not null
            ? "contains protected children" : null);

    private void ShowRemovalPage(SqliteStateStore db, PairDef pair, PrepareHeader header, RemovalReview review, long page)
    {
        var rows = db.ReadPrepareRows(header.Generation, RemovalRows, page * RemovalPageSize - 1, RemovalPageSize);
        var protectedFolders = review.ProtectedFolders;
        var text = $"Remove {review.Files} files, {review.Folders - protectedFolders} folders, {review.Bytes} bytes. Keep {review.Protected + protectedFolders} protected items. " +
            $"Sides: {string.Join(", ", review.Sides)}. Recovery is required for every removed file. " +
            $"Page {page + 1}/{Math.Max(1, (review.Total + RemovalPageSize - 1) / RemovalPageSize)}. Signature {review.Signature}.\n";
        if (review.Excluded) text = "This folder is excluded from sync; confirming removes it anyway (Recovery-first)." + Environment.NewLine + text;
        foreach (var row in rows)
        {
            var item = JsonSerializer.Deserialize<RemovalItem>(row.Json)!;
            var reason = ProtectedReason(db, header, item);
            var size = item.Item.ObservationKindUnknown ? "type unknown; could not be read" : $"{item.Item.Size} bytes";
            text += $"{item.Side} {item.Item.Path} ({size}): {(reason is null ? "REMOVE with Recovery" : "KEEP: " + reason)}\n";
        }
        _store.AddNotices([new Notice(pair.PairId, 40, NoticeKind.UserDecision, review.Path, text,
            NoticeChoices.RemovalReviewPrefix + header.Generation + ":" + page, RemovalSides: review.Sides, RemovalOriginReason: review.OriginReason)]);
    }
}

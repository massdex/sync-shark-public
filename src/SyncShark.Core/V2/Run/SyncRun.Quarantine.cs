namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    /// <summary>Shared CLI/app authority; no background code calls the attended elevation launcher.</summary>
    public async Task<ActionResult> ResolveQuarantineAsync(string pairId, string noticeId, string choice,
        CancellationToken ct, IElevatedAction? elevation = null, IDefenderExclusions? exclusions = null, string? itemPath = null)
    {
        if (choice is NoticeChoices.DefenderReleaseAllPairs or NoticeChoices.DefenderKeepShared)
            return await ResolveSharedDefenderReleaseAsync(pairId, noticeId, choice, ct).ConfigureAwait(false);
        if (choice is NoticeChoices.DefenderProceed or NoticeChoices.DefenderHold)
            return await ResolveDefenderBatchAsync(pairId, noticeId, choice, ct).ConfigureAwait(false);
        if (NoticeChoices.IsHeldChoice(choice))
            return await ResolveDefenderHeldAsync(pairId, noticeId, choice, itemPath, ct).ConfigureAwait(false);
        if (choice == NoticeChoices.DefenderKeep)
            return await AcceptDefenderFileAsync(pairId, noticeId, ct).ConfigureAwait(false);
        if (choice is NoticeChoices.QuarantineRetry or NoticeChoices.DefenderVerify &&
            _store.NoticeById(pairId, noticeId)?.Notice.Quarantine?.ConflictingGrantId is { } grantId)
            return await VerifyRemovedExclusionAsync(pairId, noticeId, grantId, choice, elevation, exclusions, ct).ConfigureAwait(false);
        if (choice == NoticeChoices.DefenderVerify)
            return new(noticeId, Outcome.Skipped, null, null, null, "No current exclusion grant is attached; refresh the notice.");
        string? verifiedPath = null;
        QuarantineNotice? reviewedHold = null;
        if (choice == NoticeChoices.DefenderExclude)
        {
            var saved = _store.NoticeById(pairId, noticeId);
            var pair = _store.Pair(pairId);
            if (saved is null || saved.Value.Closed && !_store.QuarantineRecoveryRetained(pairId, noticeId) || saved.Value.Notice.Quarantine is not { } hold || pair is null)
                return new(noticeId, Outcome.Skipped, null, null, null, "Notice changed; refresh before requesting an exclusion.");
            try
            {
                var side = pair.A.Provider == SyncProviders.Local ? Side.A : Side.B;
                var root = side == Side.A ? pair.A : pair.B;
                var item = hold.Target == side ? hold.ExpectedTarget : hold.Source;
                if ((item ?? hold.Source ?? hold.ExpectedTarget)?.Kind != ItemKind.File)
                    return new(noticeId, Outcome.Skipped, null, null, null, "Folder exclusions are not offered. Restore, ignore or exclude from sync instead.");
                if (item is not null && !string.Equals(item.Path, saved.Value.Notice.Path, StringComparison.OrdinalIgnoreCase))
                    return new(noticeId, Outcome.Skipped, null, null, null, "The notice path differs from the local entity path. No exclusion requested; retry without exclusion or refresh the notice.");
                verifiedPath = AttendedElevatedAction.ValidatePath(root.RootPath, item?.Path ?? saved.Value.Notice.Path);
                reviewedHold = hold;
                var cloudItem = hold.Target == side ? hold.Source : hold.ExpectedTarget;
                var owner = item?.ObjectId ?? cloudItem!.ObjectId;
                var grant = new DefenderGrant(ShortId.Make("dg", root.RootObjectId, owner, verifiedPath, Guid.NewGuid().ToString("N")), owner,
                    cloudItem?.ObjectId, item?.ObjectId, verifiedPath, AttendedElevatedAction.DownloadStagePath(verifiedPath));
                var recorded = await RunExclusiveAsync(null, (_, _) =>
                {
                    if (_store.NoticeById(pairId, noticeId) != saved || _store.Pair(pairId) is not { } currentPair ||
                        currentPair.A != pair.A || currentPair.B != pair.B)
                        return Task.FromResult(false);
                    _store.StoreDefenderGrant(grant);
                    return Task.FromResult(true);
                }, ct, admissionPairId: pairId).ConfigureAwait(false);
                if (!recorded) return new(noticeId, Outcome.Skipped, null, null, null, "Notice changed; no exclusion requested.");
                // A human UAC response must not occupy the sync gate or delay unrelated work.
                var exit = await (elevation ?? new AttendedElevatedAction()).RunAsync(
                    AttendedElevatedAction.DefenderExclude, [root.RootPath, verifiedPath], ct).ConfigureAwait(false);
                if (exit != 0) return await RecordElevationFailureAsync("Exclusion was not verified; the item remains held.");
                await RunExclusiveAsync(null, (_, _) =>
                {
                    _store.StoreDefenderGrant(grant with { Verified = true });
                    return Task.FromResult(true);
                }, ct, admissionPairId: pairId).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { return await RecordElevationFailureAsync("Exclusion did not complete; item remains held: " + error.Message); }
        }
        Task<ActionResult> RecordElevationFailureAsync(string detail) => RunExclusiveAsync(null, (_, _) =>
        {
            if (verifiedPath is not null) detail += " Defender exclusions may remain for " + verifiedPath + " and " +
                AttendedElevatedAction.DownloadStagePath(verifiedPath) + ". Review/remove these exact paths in Windows Security Exclusions if no longer needed.";
            var current = _store.NoticeById(pairId, noticeId);
            if (current is { Closed: false } && current.Value.Notice.Quarantine == reviewedHold)
                _store.AddNotices([current.Value.Notice with { Detail = detail }]);
            return Task.FromResult(new ActionResult(noticeId, Outcome.Skipped, null, null, null, detail));
        }, ct, admissionPairId: pairId);
        return await ResolveQuarantineUnderGateAsync(pairId, noticeId, choice, verifiedPath, reviewedHold, ct).ConfigureAwait(false);
    }

    private Task<ActionResult> ResolveDefenderBatchAsync(string pairId, string noticeId, string choice, CancellationToken ct)
    {
        var reviewed = _store.NoticeById(pairId, noticeId);
        var pair = _store.Pair(pairId);
        return RunExclusiveAsync(null, (_, _) =>
        {
            if (reviewed is null || reviewed.Value.Closed || reviewed.Value.Notice.DefenderHistory is not { } batch ||
                batch.Cause is DefenderReview.HeldCause or DefenderReview.SharedReleaseCause || !SameReviewedRoot(pairId, pair, batch) ||
                _store.NoticeById(pairId, noticeId) != reviewed)
                return Task.FromResult(new ActionResult(noticeId, Outcome.Skipped, null, null, null, "The reviewed deletion batch changed; refresh before choosing."));
            var proceed = choice == NoticeChoices.DefenderProceed;
            _store.DecideDefenderBatch(batch.Batch, proceed, batch.RootId);
            // Either choice resolves this review; Keep held then stands in its own notice, so new deletions get a new review.
            foreach (var binding in _store.Pairs())
                foreach (var notice in _store.UnresolvedNotices(binding.PairId).Where(n => n.DefenderHistory?.Batch == batch.Batch))
                    _store.CloseNotice(notice.NoticeId!, choice);
            if (!proceed) DefenderReview.RefreshHeld(_store, batch.RootId);
            return Task.FromResult(new ActionResult(noticeId, Outcome.Done, null, null, null, proceed
                ? "Reviewed UNKNOWN deferrals may proceed on next sync after rechecking positive evidence. No files changed by this choice."
                : "Held by your choice. They are listed in the 'Held by your choice' notice, where you can release them or stop syncing them. New deletions get their own review."));
        }, ct);
    }

    // The pair still binds the reviewed local root exactly as the notice recorded it.
    private bool SameReviewedRoot(string pairId, PairDef? pair, DefenderHistoryNotice batch) =>
        pair is not null && _store.Pair(pairId) is { } current && current.A == pair.A && current.B == pair.B &&
        (pair.A.RootId == batch.RootId && pair.A.Provider == SyncProviders.Local && pair.A == batch.ReviewedRoot ||
         pair.B.RootId == batch.RootId && pair.B.Provider == SyncProviders.Local && pair.B == batch.ReviewedRoot);

    /// <summary>
    /// The standing "Held by your choice" notice: release all, release one item, or stop syncing one item. A release
    /// becomes Proceed, so the next pass still rechecks positive evidence before any deletion. Stop syncing records the
    /// exclusion first, then forgets the hold, so an interruption between them never permits a deletion.
    /// </summary>
    private Task<ActionResult> ResolveDefenderHeldAsync(string pairId, string noticeId, string choice, string? itemPath, CancellationToken ct)
    {
        var reviewed = _store.NoticeById(pairId, noticeId);
        var pair = _store.Pair(pairId);
        return RunExclusiveAsync(null, (_, _) =>
        {
            ActionResult Skipped(string reason) => new(noticeId, Outcome.Skipped, null, null, null, reason);
            if (reviewed is null || reviewed.Value.Closed || reviewed.Value.Notice.DefenderHistory is not { Cause: DefenderReview.HeldCause } held ||
                !SameReviewedRoot(pairId, pair, held) || _store.NoticeById(pairId, noticeId) != reviewed)
                return Task.FromResult(Skipped("The held items changed; refresh before choosing."));
            ItemState? item = null;
            if (choice != NoticeChoices.DefenderReleaseHeld)
            {
                var rows = itemPath is { Length: > 0 } ? _store.Live(held.RootId, [itemPath]) : [];
                item = rows.FirstOrDefault(row => string.Equals(row.Path, itemPath, StringComparison.Ordinal)) ??
                    (rows.Count == 1 ? rows[0] : null);
                if (item is null || pair!.Excluded.Any(prefix => PairPath.IsSelfOrUnder(item.Path, prefix)) ||
                    _store.DefenderDeferrals([item.ObjectId]).GetValueOrDefault(item.ObjectId) is not
                    { Consent: DefenderConsent.Keep } row || row.Batch != DefenderReview.HeldBatch(held.RootId))
                    return Task.FromResult(Skipped("Choose one item that is still held by your choice; refresh the list."));
            }
            string result;
            if (choice == NoticeChoices.DefenderExcludeHeld)
            {
                var current = _store.Pair(pairId)!;
                _store.SavePair(current with { Excluded = current.Excluded.Append(item!.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() });
                _store.ForgetDefenderHeld(held.RootId, item.ObjectId);
                result = "Stopped syncing " + item.Path + ". Neither copy was deleted.";
            }
            else
            {
                if (item is not null && DefenderReview.SharedRelease(_store, held.RootId, pairId, item) is { OtherPairNames.Length: > 0 } shared)
                {
                    _store.AddNotices([SharedDefenderReleaseNotice(pair!, held.RootId, item, shared)]);
                    return Task.FromResult(new ActionResult(noticeId, Outcome.Done, null, null, null,
                        "Nothing released. This item's hold is shared, so a single-pair release is not available. Choose Release in all pairs or Keep held in the new item notice."));
                }
                var count = _store.ReleaseDefenderHeld(held.RootId, item?.ObjectId);
                if (count == 0) return Task.FromResult(Skipped("Nothing is held by your choice any more; refresh."));
                result = "Released " + count + " held item(s). The next sync rechecks Defender evidence, then applies ordinary deletion rules. No files changed by this choice.";
            }
            DefenderReview.RefreshHeld(_store, held.RootId);
            if (_store.NoticeById(pairId, noticeId) is { Closed: true }) _store.CloseNotice(noticeId, choice);
            return Task.FromResult(new ActionResult(noticeId, Outcome.Done, null, null, null, result));
        }, ct, admissionPairId: pairId);
    }

    private static Notice SharedDefenderReleaseNotice(PairDef pair, string rootId, ItemState item, DefenderSharedRelease shared) =>
        new(pair.PairId, 30, NoticeKind.UserDecision, item.Path,
            shared.OtherPairNames.Length == 0 ? "Only this pair still holds the item; confirm release or keep it held." :
                "This item's hold is shared by multiple pairs; a single-pair release is not available.",
            DefenderReview.SharedReleaseKey(rootId, item.ObjectId),
            DefenderHistory: new(rootId, DefenderReview.HeldBatch(rootId), 1, DefenderReview.SharedReleaseCause,
                DefenderReview.LocalRoot(pair, rootId), shared));

    private Task<ActionResult> ResolveSharedDefenderReleaseAsync(string pairId, string noticeId, string choice, CancellationToken ct)
    {
        var reviewed = _store.NoticeById(pairId, noticeId);
        var pair = _store.Pair(pairId);
        return RunExclusiveAsync(null, (_, _) =>
        {
            ActionResult Result(Outcome outcome, string reason) => new(noticeId, outcome, null, null, null, reason);
            if (reviewed is null || reviewed.Value.Closed || reviewed.Value.Notice.DefenderHistory is not
                { Cause: DefenderReview.SharedReleaseCause, SharedRelease: { } shared } held ||
                !SameReviewedRoot(pairId, pair, held) || _store.NoticeById(pairId, noticeId) != reviewed)
                return Task.FromResult(Result(Outcome.Skipped, "The shared-item review changed. Refresh before choosing; nothing released."));
            if (choice == NoticeChoices.DefenderKeepShared)
            {
                _store.CloseNotice(noticeId, choice);
                return Task.FromResult(Result(Outcome.Done, "Kept held in every pair. No consent or files changed."));
            }
            var item = _store.Live(held.RootId, [reviewed.Value.Notice.Path]).FirstOrDefault(row => row.ObjectId == shared.ObjectId);
            if (item is null || pair!.Excluded.Any(prefix => PairPath.IsSelfOrUnder(item.Path, prefix)) ||
                _store.DefenderDeferrals([item.ObjectId]).GetValueOrDefault(item.ObjectId) is not { Consent: DefenderConsent.Keep } state ||
                state.Batch != DefenderReview.HeldBatch(held.RootId))
                return Task.FromResult(Result(Outcome.Skipped, "The item is no longer held by this pair. Refresh; nothing released."));
            var current = DefenderReview.SharedRelease(_store, held.RootId, pairId, item);
            if (current != shared)
            {
                _store.AddNotices([SharedDefenderReleaseNotice(pair, held.RootId, item, current)]);
                return Task.FromResult(Result(Outcome.Skipped, "The pairs sharing this hold changed. Review the updated pair names and choose again; nothing released."));
            }
            _store.ReleaseDefenderHeld(held.RootId, item.ObjectId);
            foreach (var binding in _store.Pairs().Where(binding => binding.A.RootId == held.RootId || binding.B.RootId == held.RootId))
                foreach (var notice in _store.DefenderNotices(binding.PairId, held.RootId).Where(notice =>
                    notice.DefenderHistory?.SharedRelease?.ObjectId == item.ObjectId))
                    _store.CloseNotice(notice.NoticeId!, choice);
            DefenderReview.RefreshHeld(_store, held.RootId);
            return Task.FromResult(Result(Outcome.Done,
                "Released this item in all pairs. The next sync rechecks Defender evidence before ordinary deletion rules apply; no files changed by this choice."));
        }, ct);
    }

    private Task<ActionResult> AcceptDefenderFileAsync(string pairId, string noticeId, CancellationToken ct)
    {
        var reviewed = _store.NoticeById(pairId, noticeId);
        var pair = _store.Pair(pairId);
        var grant = reviewed?.Notice.Quarantine?.ConflictingGrantId is { } id ? _store.DefenderGrantById(id) : null;
        return RunExclusiveAsync(null, async (_, token) =>
        {
            ActionResult Held() => new(noticeId, Outcome.Skipped, null, null, null, "Current file, pair, notice or exclusion grant changed. Refresh and review; nothing rebound.");
            if (pair is null || reviewed is null || grant is null || reviewed.Value.Notice.Quarantine is not { } hold ||
                reviewed.Value.Closed && !_store.QuarantineRecoveryRetained(pairId, noticeId)) return Held();
            var side = pair.A.Provider == SyncProviders.Local ? Side.A : Side.B;
            var root = side == Side.A ? pair.A : pair.B;
            var remote = side == Side.A ? pair.B : pair.A;
            var expected = hold.Target == side ? hold.ExpectedTarget : hold.Source;
            var expectedCloud = hold.Target == side ? hold.Source : hold.ExpectedTarget;
            if (root.Provider != SyncProviders.Local || expected is not { Kind: ItemKind.File } ||
                !string.Equals(AttendedElevatedAction.ValidatePath(root.RootPath, expected.Path), grant.Path, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(grant.StagePath, AttendedElevatedAction.DownloadStagePath(grant.Path), StringComparison.OrdinalIgnoreCase)) return Held();
            await _endpoints.PrepareAsync(pair, token).ConfigureAwait(false);
            var local = await _endpoints.Writer(root).ObserveAsync(expected.Path, token).ConfigureAwait(false);
            var cloud = expectedCloud is null ? null : await _endpoints.Writer(remote).ObserveAsync(expectedCloud.Path, token).ConfigureAwait(false);
            if (expected.Hash is null && local is { Kind: ItemKind.File, Hash: not null } &&
                local.ObjectId == expected.ObjectId && PairPath.Same(local.Path, expected.Path) &&
                (local.Flags & (ItemFlags.Quarantined | ItemFlags.Unreadable | ItemFlags.Locked | ItemFlags.Link)) == 0 &&
                _store.Pair(pairId) is { } refreshPair && refreshPair.A == pair.A && refreshPair.B == pair.B &&
                _store.NoticeById(pairId, noticeId) == reviewed && _store.DefenderGrantById(grant.GrantId) == grant)
            {
                var refreshed = hold.Target == side ? hold with { ExpectedTarget = local } : hold with { Source = local };
                _store.AddNotices([reviewed.Value.Notice with { Quarantine = refreshed,
                    Detail = "This older notice had no reviewed content hash. The current file is now recorded; review and confirm Keep again. No grant changed." }]);
                var savedRefresh = _store.NoticeById(pairId, noticeId);
                var recorded = savedRefresh?.Notice.Quarantine is { } savedHold &&
                    Content.SameHash((savedHold.Target == side ? savedHold.ExpectedTarget : savedHold.Source)?.Hash, local.Hash) == true;
                return new ActionResult(noticeId, Outcome.Skipped, null, null, null, recorded
                    ? "The notice now records the current content hash. Review it and confirm Keep again; nothing rebound."
                    : "The closed notice was not refreshed. Nothing rebound. Refresh the review or verify removal of the exclusion before retrying.");
            }
            if (!SameReviewedFile(expected, local) || expectedCloud is not null && !SameReviewedFile(expectedCloud, cloud) ||
                local is null || (local.Flags & (ItemFlags.Quarantined | ItemFlags.Unreadable | ItemFlags.Locked | ItemFlags.Link)) != 0 ||
                _store.Pair(pairId) is not { } current || current.A != pair.A || current.B != pair.B ||
                _store.NoticeById(pairId, noticeId) != reviewed || _store.DefenderGrantById(grant.GrantId) != grant) return Held();
            // The final observation narrows external replacement races; later effects remain conditional on this identity.
            if (!SameReviewedFile(local, await _endpoints.Writer(root).ObserveAsync(local.Path, token).ConfigureAwait(false)) ||
                _store.Pair(pairId) is not { } finalPair || finalPair.A != pair.A || finalPair.B != pair.B ||
                _store.NoticeById(pairId, noticeId) != reviewed || _store.DefenderGrantById(grant.GrantId) != grant) return Held();
            if (!_store.AcceptDefenderGrant(grant, local, cloud)) return Held();
            _store.CloseNotice(noticeId, NoticeChoices.DefenderKeep);
            return new ActionResult(noticeId, Outcome.Done, null, null, null,
                "Existing exclusion kept for the reviewed current file only. No Defender setting changed; no UAC. Historical final/stage paths remain guarded.");
        }, ct, admissionPairId: pairId);
    }

    // Metadata is never a substitute for reviewed bytes, even when an in-place writer restores the timestamp.
    private static bool SameReviewedFile(ItemState expected, ItemState? current) => current is { Kind: ItemKind.File } &&
        expected.Kind == ItemKind.File && current.ObjectId == expected.ObjectId && current.VersionId == expected.VersionId &&
        current.Size == expected.Size && PairPath.Same(current.Path, expected.Path) &&
        Content.SameContent(current, expected) == true;

    private async Task<ActionResult> VerifyRemovedExclusionAsync(string pairId, string noticeId, string grantId, string choice,
        IElevatedAction? elevation, IDefenderExclusions? exclusions, CancellationToken ct)
    {
        var reviewed = _store.NoticeById(pairId, noticeId);
        var pair = _store.Pair(pairId);
        var grant = _store.DefenderGrantById(grantId);
        if (reviewed is null || pair is null || grant is null || reviewed.Value.Closed && !_store.QuarantineRecoveryRetained(pairId, noticeId))
            return new(noticeId, Outcome.Skipped, null, null, null, "Grant or notice changed; refresh before verification.");
        var absent = false;
        try
        {
            if (choice == NoticeChoices.DefenderVerify)
                absent = await (elevation ?? new AttendedElevatedAction()).RunAsync(AttendedElevatedAction.DefenderVerify,
                    [Path.GetDirectoryName(grant.Path)!, grant.Path, grant.StagePath], ct).ConfigureAwait(false) == AttendedElevatedAction.ExclusionsConfirmedAbsent;
            else
            {
                var view = await (exclusions ?? new DefenderExclusions()).ReadAsync(ct).ConfigureAwait(false);
                absent = view.Authoritative && !view.Paths.Any(p => string.Equals(p, grant.Path, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p, grant.StagePath, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested) { }
        return await RunExclusiveAsync(null, (_, _) =>
        {
            if (_store.Pair(pairId) is not { } currentPair || currentPair.A != pair.A || currentPair.B != pair.B ||
                _store.NoticeById(pairId, noticeId) != reviewed || _store.DefenderGrantById(grantId) != grant)
                return Task.FromResult(new ActionResult(noticeId, Outcome.Skipped, null, null, null, "Grant or entity changed during verification; nothing released."));
            if (!absent)
            {
                var detail = QuarantineFailure.GrantWarning(grant.Path) +
                    " Removal could not be authoritatively verified (exclusions remain, visibility is hidden, permission is insufficient, or the check failed). Choose Verify with admin for a separate read-only UAC check, keep held, or exclude from sync.";
                _store.AddNotices([reviewed.Value.Notice with { Detail = detail }]);
                return Task.FromResult(new ActionResult(noticeId, Outcome.Skipped, null, null, null, detail));
            }
            // Persist the wake-up before retiring the grant. Preserve any existing retry's
            // evidence and decision hold; a crash or failed retirement retains the grant guard.
            var retryPath = reviewed.Value.Notice.Path;
            var retry = _store.SyncRetryAt(pairId, retryPath);
            if (retry?.Kind != "decision")
                _store.SaveSyncRetry(pairId, (retry ?? new SyncRetry(retryPath, "sync", 0,
                    "Rechecking this path after exclusion verification.", DateTimeOffset.MinValue))
                    with { RetryAfter = DateTimeOffset.MinValue });
            if (!_store.RetireDefenderGrant(grant))
                return Task.FromResult(new ActionResult(noticeId, Outcome.Skipped, null, null, null, "Grant changed; nothing released."));
            _store.CloseNotice(noticeId, choice);
            // Identity-keyed reviews can describe several entities affected by this same grant.
            // Verified retirement resolves this grant's guards, not unrelated antivirus holds.
            foreach (var related in _store.UnresolvedNotices(pairId).Where(n => n.Quarantine?.ConflictingGrantId == grant.GrantId))
                _store.CloseNotice(related.NoticeId!, choice);
            return Task.FromResult(new ActionResult(noticeId, Outcome.Done, null, null, null,
                "Both recorded exact exclusions are confirmed absent. Guard retired; next sync will replan the current entity. No Defender setting was changed."));
        }, ct, admissionPairId: pairId).ConfigureAwait(false);
    }

    private Task<ActionResult> ResolveQuarantineUnderGateAsync(string pairId, string noticeId, string choice,
        string? verifiedPath, QuarantineNotice? reviewedHold, CancellationToken ct) => RunExclusiveAsync(null, async (_, token) =>
    {
        var id = "quarantine-" + ShortId.Hex16(pairId, noticeId, choice);
        ActionResult Result(Outcome outcome, string reason) => new(id, outcome, null, null, null, reason);
        var saved = _store.NoticeById(pairId, noticeId);
        if (saved is null || saved.Value.Closed && !_store.QuarantineRecoveryRetained(pairId, noticeId) || saved.Value.Notice.Quarantine is not { } hold ||
            _store.Pair(pairId) is not { } pair)
            return Result(Outcome.Skipped, "The quarantine notice changed. Refresh and choose again; nothing was changed.");
        var notice = saved.Value.Notice;
        if (!NoticeChoices.IsQuarantineChoice(choice)) return Result(Outcome.Skipped, "Choose an offered quarantine action.");
        if (choice is NoticeChoices.QuarantineIgnore or NoticeChoices.QuarantineExclude)
        {
            if (choice == NoticeChoices.QuarantineExclude)
                _store.SavePair(pair with { Excluded = pair.Excluded.Concat(new[] { notice.Path,
                    hold.Source?.Path, hold.ExpectedTarget?.Path }.OfType<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() });
            if (choice == NoticeChoices.QuarantineExclude) ClearQuarantineHold(pair, hold);
            _store.CloseNotice(noticeId, choice); // ignore remains held; exclusion follows enclosing folder effects
            return Result(Outcome.Done, choice == NoticeChoices.QuarantineIgnore
                ? "Files left untouched; the quarantine hold remains. No automatic retry. Retained recovery key: " + noticeId +
                    ". Retain this key for conflicts resolve with generation 0 and explicit --yes. Pair and path are data, not a shell command: " +
                    System.Text.Json.JsonSerializer.Serialize(new { Pair = pairId, Path = notice.Path, Key = noticeId })
                : "Files left untouched and excluded from sync. Enclosing folder effects follow normal exclusion rules.");
        }
        var localSide = pair.A.Provider == SyncProviders.Local ? Side.A : Side.B;
        var localRoot = localSide == Side.A ? pair.A : pair.B;
        var cloudRoot = localSide == Side.A ? pair.B : pair.A;
        if (localRoot.Provider != SyncProviders.Local || cloudRoot.Provider != SyncProviders.Dropbox)
            return Result(Outcome.Skipped, "These recovery options need a local/Dropbox pair. Leave held or exclude instead.");
        var localItem = hold.Target == localSide ? hold.ExpectedTarget : hold.Source;
        var cloudItem = hold.Target == localSide ? hold.Source : hold.ExpectedTarget;
        var localPath = localItem?.Path ?? notice.Path;
        try
        {
            if (choice == NoticeChoices.DefenderExclude)
            {
                var path = AttendedElevatedAction.ValidatePath(localRoot.RootPath, localPath);
                if (!string.Equals(path, verifiedPath, StringComparison.OrdinalIgnoreCase) || hold != reviewedHold)
                    return Result(Outcome.Skipped, "The pair or notice changed. Defender exclusions were added for " + verifiedPath + " and " +
                        AttendedElevatedAction.DownloadStagePath(verifiedPath!) + ". No retry occurred. Review these exact paths in Windows Security > Virus & threat protection > Manage settings > Exclusions; remove them there if no longer needed.");
                hold = hold with { GrantedExclusionPath = path, GrantedStagePath = AttendedElevatedAction.DownloadStagePath(path) };
                notice = notice with { Quarantine = hold, Detail = notice.Detail + ExclusionRemoval(hold) };
                _store.AddNotices([notice]);
            }
            await _endpoints.PrepareAsync(pair, token).ConfigureAwait(false);
            if (hold.PendingActionId is { } previousId && _store.IntentById(pairId, previousId) is { } previous)
            {
                if (choice == NoticeChoices.QuarantineDeleteDropbox)
                    return Result(Outcome.Skipped, "An interrupted download must be verified first. Choose Retry before requesting deletion.");
                var reconciled = (await _executor.ReconcileAsync([previous], _store.BeginGeneration(localRoot.RootId, "quarantine-recheck"),
                    _endpoints.Writer(pair.A), _endpoints.Writer(pair.B), token)).Single();
                if (reconciled.Outcome != Outcome.Done)
                    return Result(Outcome.Skipped, reconciled.Reason ?? "Interrupted effect remains held. Retry verification later.");
                if (hold.Source is { } resolvedSource) _store.ClearQuarantine(hold.Target == Side.A ? pair.B.RootId : pair.A.RootId, resolvedSource);
                if (hold.ExpectedTarget is { } resolvedTarget) _store.ClearQuarantine(hold.Target == Side.A ? pair.A.RootId : pair.B.RootId, resolvedTarget);
                _store.CloseNotice(noticeId, choice);
                return reconciled;
            }
            var local = _endpoints.Writer(localRoot);
            var cloud = _endpoints.Writer(cloudRoot);
            if (choice == NoticeChoices.QuarantineDeleteDropbox)
            {
                if (cloudItem is not { Kind: ItemKind.File, VersionId: not null })
                    return Result(Outcome.Skipped, "No reviewed Dropbox file revision is attached. Refresh the notice; nothing was deleted.");
                var current = await cloud.ObserveAsync(cloudItem.Path, token).ConfigureAwait(false);
                if (current is not null && (current.ObjectId != cloudItem.ObjectId || current.VersionId != cloudItem.VersionId))
                    return Result(Outcome.Skipped, "Dropbox changed since this notice. Nothing was deleted; refresh and review the new revision.");
                // DropboxWriter.DeleteAsync uses parent_rev; folder deletion is never offered here.
                var cloudSide = localSide == Side.A ? Side.B : Side.A;
                var baseline = _store.BaselinesAt(pairId, cloudSide, [cloudItem.Path]).FirstOrDefault(b =>
                    (cloudSide == Side.A ? b.A : b.B).ObjectId == cloudItem.ObjectId);
                var delete = new SyncAction(id, 30, current is null ? ActionKind.Forget : ActionKind.Delete, cloudSide,
                    cloudItem.Path, null, null, cloudItem, true, baseline?.ObjectKey ?? hold.ObjectKey)
                    // Explicit approval belongs only to this reviewed file revision, never a subtree.
                    // Persist it with the intent so crash reconciliation retains the same authority.
                    { RemovalReviewChoice = true };
                var deleted = await ExecuteQuarantineAction(pair, delete, token).ConfigureAwait(false);
                if (deleted.Outcome != Outcome.Done && deleted.Reason is { } deleteReason)
                    _store.RecordNoticeChoiceFailure(notice, deleteReason);
                if (deleted.Outcome == Outcome.Done)
                {
                    ClearQuarantineHold(pair, hold);
                    _store.CloseNotice(noticeId, choice);
                }
                return deleted;
            }
            var observedLocal = await local.ObserveAsync(localPath, token).ConfigureAwait(false);
            // A verified exclusion permits one explicit download retry even when quarantine removed the target.
            // Existing local bytes are checked for readability before any replacement below.
            var cloudPath = cloudItem?.Path ?? notice.Path;
            var observedCloud = await cloud.ObserveAsync(cloudPath, token).ConfigureAwait(false);
            if (hold.ScanOnly && observedLocal is { Kind: ItemKind.Folder })
            {
                if (!await RefreshQuarantinePathAsync(localRoot, localSide, observedLocal, token).ConfigureAwait(false))
                    return Result(Outcome.Skipped, "Scoped refresh is still unreadable; the item remains held.");
                if (localItem is not null) _store.ClearQuarantine(localRoot.RootId, localItem);
                _store.ClearQuarantine(localRoot.RootId, observedLocal);
                _store.CloseNotice(noticeId, choice);
                return Result(Outcome.Done, "Folder identity is readable again. Its scoped inventory was refreshed; no deletion inferred.");
            }
            var targetSide = hold.Target;
            if (hold.ScanOnly && observedLocal is { Kind: ItemKind.File })
            {
                await using var readable = await local.OpenReadAsync(observedLocal, token).ConfigureAwait(false);
                await readable.ReadAsync(new byte[1], token).ConfigureAwait(false);
                // Scan-only holds have no executable direction. Recheck without overwriting either version;
                // retain existing baselines while the next full scan plans normally.
                if (!await RefreshQuarantinePathAsync(localRoot, localSide, observedLocal, token).ConfigureAwait(false))
                    return Result(Outcome.Skipped, "Scoped refresh is still unreadable; the item remains held.");
                if (localItem is not null) _store.ClearQuarantine(localRoot.RootId, localItem);
                _store.ClearQuarantine(localRoot.RootId, observedLocal);
                _store.CloseNotice(noticeId, choice);
                return Result(Outcome.Done, "The local item is readable again. Next sync will replan it; neither version was overwritten.");
            }
            var source = targetSide == localSide ? observedCloud : observedLocal;
            var expected = hold.Target == localSide ? observedLocal : observedCloud;
            var targetPath = hold.Target == localSide ? localPath : cloudPath;
            if (expected is not null && (hold.ExpectedTarget is null || expected.ObjectId != hold.ExpectedTarget.ObjectId ||
                expected.VersionId != hold.ExpectedTarget.VersionId || expected.Size != hold.ExpectedTarget.Size || expected.Mtime != hold.ExpectedTarget.Mtime))
                return Result(Outcome.Skipped, "The destination changed since this notice. Nothing was overwritten; preserve or exclude it before retrying.");
            if (source is not { Kind: ItemKind.File })
                return Result(Outcome.Skipped, "No readable source file is available. Restore the item or exclude it; it stays held.");
            if (hold.Source is { } reviewed && (source.ObjectId != reviewed.ObjectId || source.VersionId != reviewed.VersionId))
            {
                var refreshed = notice with { Quarantine = hold with { Source = source, HeldSource = hold.HeldSource ?? hold.Source },
                    Detail = "The source changed. Review the current revision and choose Retry again; nothing was overwritten." };
                _store.AddNotices([refreshed]);
                // A replacement owns a separate review. Publish its inherited hold evidence before
                // retiring the prior review; a crash between these writes only leaves both notices.
                if (NoticeTable.Id(refreshed) != noticeId)
                    _store.CloseNotice(noticeId, "quarantine-superseded");
                return Result(Outcome.Skipped, "The source changed. The notice now names the current revision; review it and choose again.");
            }
            if (choice == NoticeChoices.DefenderExclude && observedLocal is { Kind: ItemKind.File })
            {
                await using var readable = await local.OpenReadAsync(observedLocal, token).ConfigureAwait(false);
                var buffer = new byte[1];
                await readable.ReadAsync(buffer, token).ConfigureAwait(false);
            }
            var retry = new SyncAction(id, 30, expected is null ? ActionKind.CopyNew : ActionKind.Update,
                hold.Target, targetPath, null, source with { Flags = source.Flags & ~ItemFlags.Quarantined }, expected,
                expected is not null, hold.ObjectKey ?? PlanIds.ObjectKey(pairId,
                    targetSide == Side.A ? Side.B : Side.A, source.ObjectId))
                { QuarantineStage = targetSide == localSide && string.Equals(hold.GrantedStagePath, AttendedElevatedAction.DownloadStagePath(
                    AttendedElevatedAction.ValidatePath(localRoot.RootPath, targetPath)), StringComparison.OrdinalIgnoreCase) };
            var result = await ExecuteQuarantineAction(pair, retry, token).ConfigureAwait(false);
            if (result.Outcome != Outcome.Done && result.Reason is { } reason)
                _store.RecordNoticeChoiceFailure(notice, reason);
            if (result.Outcome == Outcome.Done)
            {
                _store.ClearQuarantine(hold.Target == Side.A ? pair.B.RootId : pair.A.RootId, source);
                if ((hold.Target == Side.A ? result.NewA : result.NewB) is { } newTarget) _store.ClearQuarantine(hold.Target == Side.A ? pair.A.RootId : pair.B.RootId, newTarget);
                if ((hold.HeldSource ?? hold.Source) is { } oldSource) _store.ClearQuarantine(hold.Target == Side.A ? pair.B.RootId : pair.A.RootId, oldSource);
                if (hold.ExpectedTarget is { } oldTarget) _store.ClearQuarantine(hold.Target == Side.A ? pair.A.RootId : pair.B.RootId, oldTarget);
                _store.CloseNotice(noticeId, choice);
            }
            return result;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            var detail = QuarantineFailure.IsQuarantine(error) ? QuarantineFailure.Describe(localPath, error) :
                "The requested action did not complete: " + error.Message + ". The item stays held; retry, ignore or exclude instead.";
            _store.AddNotices([notice with { Detail = detail }]);
            return Result(Outcome.Skipped, detail);
        }
    }, ct, admissionPairId: pairId);

    private static string ExclusionRemoval(QuarantineNotice hold) => hold.GrantedExclusionPath is { } path
        ? " Defender exclusions verified for these exact paths: " + path + "; " + hold.GrantedStagePath +
          ". Review/remove them in Windows Security > Virus & threat protection > Manage settings > Exclusions when no longer needed. Sync Shark never removes potentially pre-existing exclusions automatically."
        : "";

    private async Task<bool> RefreshQuarantinePathAsync(RootDef root, Side side, ItemState observed, CancellationToken ct)
    {
        var cursor = _store.Cursor(root.RootId) ?? "";
        var gen = _store.BeginGeneration(root.RootId, "quarantine-refresh");
        var delta = _endpoints.Inventory(root) is LocalInventory local
            ? await local.ChangesForPathsAsync([observed.Path], cursor, gen, _ => true, ct).ConfigureAwait(false)
            : new Delta(root.RootId, side, gen, cursor, cursor,
                [new Change(ChangeKind.Upsert, observed.ObjectId, observed.Path, observed, null)], false, []);
        _store.WriteDelta(delta);
        var complete = !delta.CursorInvalid && delta.IncompleteSubtrees.Count == 0 &&
            !delta.Changes.Any(c => c.State?.Flags.HasFlag(ItemFlags.Quarantined) == true) &&
            delta.Changes.Any(c => c.Kind == ChangeKind.Upsert && PairPath.Same(c.Path, observed.Path) && c.ObjectId == observed.ObjectId);
        _store.CompleteGeneration(gen, cursor, complete);
        return complete;
    }

    private void ClearQuarantineHold(PairDef pair, QuarantineNotice hold)
    {
        var sourceRoot = hold.Target == Side.A ? pair.B.RootId : pair.A.RootId;
        var targetRoot = hold.Target == Side.A ? pair.A.RootId : pair.B.RootId;
        if (hold.Source is { } source) _store.ClearQuarantine(sourceRoot, source);
        if (hold.HeldSource is { } original) _store.ClearQuarantine(sourceRoot, original);
        if (hold.ExpectedTarget is { } target) _store.ClearQuarantine(targetRoot, target);
    }

    private bool IsQuarantinedIntent(PairDef pair, SyncAction action)
    {
        bool Held(ItemState? item, RootDef root) => item is not null &&
            _store.Live(root.RootId, [item.Path]).Any(row => row.ObjectId == item.ObjectId && row.Flags.HasFlag(ItemFlags.Quarantined));
        return Held(action.Source, action.Target == Side.A ? pair.B : pair.A) ||
            Held(action.ExpectedTarget, action.Target == Side.A ? pair.A : pair.B);
    }

    private async Task<ActionResult> ExecuteQuarantineAction(PairDef pair, SyncAction action, CancellationToken ct)
    {
        action = action with { CallerOwnsNotice = true };
        var competing = _store.UnresolvedIntents(pair.PairId).FirstOrDefault(p =>
            p.ActionId != action.ActionId && (PairPath.IsSelfOrUnder(p.Path, action.Path) || PairPath.IsSelfOrUnder(action.Path, p.Path)));
        if (competing is not null)
            return new ActionResult(action.ActionId, Outcome.Skipped, null, null, null,
                "Another effect on this item needs verification first. No new mutation was submitted.");
        var gen = _store.BeginGeneration(pair.A.RootId, "quarantine-choice");
        // A previous uncertain effect must be observed, never blindly resubmitted.
        if (_store.IntentById(pair.PairId, action.ActionId) is { } pending)
            return (await _executor.ReconcileAsync([pending], gen, _endpoints.Writer(pair.A), _endpoints.Writer(pair.B), ct)).Single();
        ActionResult? result = null;
        await foreach (var item in ApplyAdmittedAsync(pair, new Plan(pair.PairId, gen, [action], []),
            _endpoints.Writer(pair.A), _endpoints.Writer(pair.B), ct)) result = item;
        return result ?? new ActionResult(action.ActionId, Outcome.Skipped, null, null, null, "Cancelled; the item stays held.");
    }
}

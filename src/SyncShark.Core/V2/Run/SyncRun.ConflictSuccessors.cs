namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private static SyncAction BindConflictCorrespondence(SyncAction action, string objectKey, bool winner) =>
        winner ? action with { ObjectKey = objectKey, SuppressReconciledBaseline = false }
            : action with { ObsoleteBaselineKey = objectKey };

    private async Task<ActionResult?> CheckConflictOriginalsAsync(PairDef pair, Notice notice,
        ConflictNoticeVersions versions, string noticeId, string choice, CancellationToken ct)
    {
        var observed = await Task.WhenAll(_endpoints.Writer(pair.A).ObserveAsync(versions.A.Path, ct),
            _endpoints.Writer(pair.B).ObserveAsync(versions.B.Path, ct)).ConfigureAwait(false);
        if (notice.Rule == 27 && (choice == NoticeChoices.KeepB && observed[0] is null ||
            choice == NoticeChoices.KeepA && observed[1] is null))
            return await RestoreConflictSuccessorAsync(pair, notice, versions, noticeId, choice, ct).ConfigureAwait(false);
        for (var index = 0; index < observed.Length; index++)
        {
            if (observed[index] is not { Kind: ItemKind.File, Hash: null } current) continue;
            observed[index] = await ProveConflictContentAsync(index == 0 ? pair.A : pair.B, current, ct).ConfigureAwait(false);
            if (observed[index] is null) return RefuseConflictSuccessor(pair, notice, noticeId, "proof");
        }
        if (observed[0] is not null && observed[1] is not null &&
            SameConflictItem(observed[0]!, versions.A, pair.A) && SameConflictItem(observed[1]!, versions.B, pair.B)) return null;
        return RefuseConflictSuccessor(pair, notice, noticeId,
            observed.Any(item => item is null) ? "missing" : "changed");
    }

    // A displaced original is resolved by the two frozen successor keys, never by label matching.
    private async Task<ActionResult> RestoreConflictSuccessorAsync(PairDef pair, Notice notice,
        ConflictNoticeVersions versions, string noticeId, string choice, CancellationToken ct)
    {
        var winner = choice == NoticeChoices.KeepA ? Side.A : Side.B;
        var target = winner == Side.A ? Side.B : Side.A;
        var destination = target == Side.A ? versions.A.Path : versions.B.Path;
        var originals = versions.Base;
        if (originals is null) return RefuseConflictSuccessor(pair, notice, noticeId, "provenance");
        var (occupants, reason) = await ConflictSuccessorOccupantsAsync(pair, versions, target, ct).ConfigureAwait(false);
        if (occupants is null) return RefuseConflictSuccessor(pair, notice, noticeId, reason!);
        var source = occupants.SingleOrDefault(item => item.Side == winner &&
            PairPath.Same(item.Item.Path, winner == Side.A ? versions.A.Path : versions.B.Path)).Item;
        if (source is null) return RefuseConflictSuccessor(pair, notice, noticeId, "missing");
        SyncAction Action(ActionKind kind, Side side, string path, ItemState? from, ItemState? victim) =>
            new(ShortId.Make("nch", pair.PairId, noticeId, side.ToString(), path, kind.ToString()), 27,
                kind, side, path, null, from, victim, victim is not null, null)
            { SuppressReconciledBaseline = true, CallerOwnsNotice = true };
        var restore = Action(ActionKind.CopyNew, target, destination, source, null) with
        {
            ObjectKey = PlanIds.Split(originals.ObjectKey, winner),
            ObsoleteBaselineKey = PlanIds.Split(originals.ObjectKey, target),
            SuppressReconciledBaseline = false
        };
        var actions = new List<SyncAction> { restore };
        actions.AddRange(occupants.Where(item => item.Side != winner || item.Item != source)
            .Select(item => Action(ActionKind.Delete, item.Side, item.Item.Path, null, item.Item)));
        var guards = ActionPredicates(pair, LeftAlone(pair));
        if (actions.Any(action => !Allowed(guards, action)))
            return RefuseConflictSuccessor(pair, notice, noticeId, "protected");
        var operation = new ConflictNoticeOperation(choice, null, actions, []);
        _store.SaveConflictOperation(pair.PairId, noticeId, operation);
        return await ApplyConflictOperationAsync(pair, noticeId, operation, ct).ConfigureAwait(false);
    }

    private async Task<(List<(Side Side, ItemState Item)>? Occupants, string? Reason)> ConflictSuccessorOccupantsAsync(
        PairDef pair, ConflictNoticeVersions versions, Side absentSide, CancellationToken ct)
    {
        var occupants = new List<(Side, ItemState)>();
        foreach (var originalSide in new[] { Side.A, Side.B })
        {
            var original = originalSide == Side.A ? versions.A : versions.B;
            var key = PlanIds.Split(versions.Base!.ObjectKey, originalSide);
            var baseline = _store.Baselines(pair.PairId, [key]).SingleOrDefault();
            if (baseline is null || original.Kind != ItemKind.File) return (null, "provenance");
            if (original.Hash is null || baseline.A.Hash is null || baseline.B.Hash is null) return (null, "provenance");
            if (Content.SameContent(baseline.A, original) != true || Content.SameContent(baseline.B, original) != true)
                return (null, "changed");
            var originalRoot = originalSide == Side.A ? pair.A : pair.B;
            var savedOriginal = originalSide == Side.A ? baseline.A : baseline.B;
            if (savedOriginal.ObjectId != original.ObjectId) return (null, "provenance");
            if (!SameConflictItem(savedOriginal, original with { Path = savedOriginal.Path }, originalRoot)) return (null, "changed");
            foreach (var side in new[] { Side.A, Side.B })
            {
                var root = side == Side.A ? pair.A : pair.B;
                var saved = side == Side.A ? baseline.A : baseline.B;
                var current = await _endpoints.Writer(root).ObserveAsync(saved.Path, ct).ConfigureAwait(false);
                var absent = absentSide == Side.A ? versions.A : versions.B;
                if (current is null && side == absentSide && originalSide == absentSide &&
                    PairPath.Same(saved.Path, absent.Path)) continue;
                if (current is null) return (null, "missing");
                if (current.Flags != ItemFlags.None) return (null, "protected");
                if (current.ObjectId != saved.ObjectId) return (null, "foreign");
                if (current.Hash is null)
                {
                    current = await ProveConflictContentAsync(root, current, ct).ConfigureAwait(false);
                    if (current is null) return (null, "proof");
                }
                if (!SameConflictItem(current, saved, root) || Content.SameContent(current, original) != true)
                    return (null, "changed");
                occupants.Add((side, current));
            }
        }
        return (occupants, null);
    }

    private async Task<(ItemState? Item, ActionResult? Failure)> CheckConflictCopyAsync(PairDef pair, Notice notice,
        string noticeId, Side side, string path, ItemState saved, CancellationToken ct)
    {
        var root = side == Side.A ? pair.A : pair.B;
        var current = await _endpoints.Writer(root).ObserveAsync(path, ct).ConfigureAwait(false);
        if (current is { Kind: ItemKind.File, Hash: null })
        {
            current = await ProveConflictContentAsync(root, current, ct).ConfigureAwait(false);
            if (current is null) return (null, RefuseConflictSuccessor(pair, notice, noticeId, "proof"));
        }
        if (current is null || !SameConflictItem(current, saved, root))
            return (null, RefuseConflictSuccessor(pair, notice, noticeId, current is null ? "missing" : "changed"));
        return (current, null);
    }

    private async Task<ItemState?> ProveConflictContentAsync(RootDef root, ItemState current, CancellationToken ct)
    {
        try
        {
            var hashes = await _endpoints.Inventory(root).HashAsync([current.Path], ct).ConfigureAwait(false);
            if (!hashes.TryGetValue(current.Path, out var hash)) return null;
            var observed = await _endpoints.Writer(root).ObserveAsync(current.Path, ct).ConfigureAwait(false);
            var proved = current with { Hash = hash };
            // The adapter owns hashing; reject any identity/version change around that proof.
            return observed is { Flags: ItemFlags.None } &&
                SameConflictItem(observed with { Hash = observed.Hash ?? hash }, proved, root) ? proved : null;
        }
        catch (Exception error) when (ScopeFailure(error, ct))
        {
            System.Diagnostics.Trace.TraceWarning("Conflict content verification failed: {0}", error);
            return null;
        }
    }

    private ActionResult RefuseConflictSuccessor(PairDef pair, Notice notice, string noticeId, string cause)
    {
        var reason = MessageCatalog.Default.Message("conflict-successor-" + cause);
        _store.RecordNoticeChoiceFailure(notice, reason);
        return new(ShortId.Make("nch", pair.PairId, noticeId), Outcome.Skipped, null, null, null, reason);
    }
}

namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private async Task<ActionResult?> CheckConflictOriginalsAsync(PairDef pair, Notice notice,
        ConflictNoticeVersions versions, string noticeId, string choice, CancellationToken ct)
    {
        var observed = await Task.WhenAll(_endpoints.Writer(pair.A).ObserveAsync(versions.A.Path, ct),
            _endpoints.Writer(pair.B).ObserveAsync(versions.B.Path, ct)).ConfigureAwait(false);
        if (notice.Rule == 27 && (choice == NoticeChoices.KeepB && observed[0] is null ||
            choice == NoticeChoices.KeepA && observed[1] is null))
            return await RestoreConflictSuccessorAsync(pair, notice, noticeId, choice, ct).ConfigureAwait(false);
        if (observed[0] is not null && observed[1] is not null &&
            SameConflictItem(observed[0]!, versions.A, pair.A) && SameConflictItem(observed[1]!, versions.B, pair.B)) return null;
        return notice.Rule == 27 ? RefuseConflictSuccessor(pair, notice, noticeId) :
            new(ShortId.Make("nch", pair.PairId, noticeId), Outcome.Skipped, null, null, null,
                "An item changed since this notice. Refresh or dismiss it and review the new state; no files changed.");
    }

    // A displaced original is resolved by the two frozen successor keys, never by label matching.
    private async Task<ActionResult> RestoreConflictSuccessorAsync(PairDef pair, Notice notice,
        string noticeId, string choice, CancellationToken ct)
    {
        var versions = notice.ConflictVersions!;
        var winner = choice == NoticeChoices.KeepA ? Side.A : Side.B;
        var target = winner == Side.A ? Side.B : Side.A;
        var destination = target == Side.A ? versions.A.Path : versions.B.Path;
        var originals = versions.Base;
        var occupants = originals is null ? null : await ConflictSuccessorOccupantsAsync(pair, versions, target, ct).ConfigureAwait(false);
        if (occupants is null)
            return RefuseConflictSuccessor(pair, notice, noticeId);
        var source = occupants.SingleOrDefault(item => item.Side == winner &&
            PairPath.Same(item.Item.Path, winner == Side.A ? versions.A.Path : versions.B.Path)).Item;
        if (source is null) return RefuseConflictSuccessor(pair, notice, noticeId);
        SyncAction Action(ActionKind kind, Side side, string path, ItemState? from, ItemState? victim) =>
            new(ShortId.Make("nch", pair.PairId, noticeId, side.ToString(), path, kind.ToString()), 27,
                kind, side, path, null, from, victim, victim is not null, null)
            { SuppressReconciledBaseline = true, CallerOwnsNotice = true };
        var restore = Action(ActionKind.CopyNew, target, destination, source, null) with
        {
            ObjectKey = PlanIds.Split(originals!.ObjectKey, winner),
            ObsoleteBaselineKey = PlanIds.Split(originals.ObjectKey, target),
            SuppressReconciledBaseline = false
        };
        var actions = new List<SyncAction> { restore };
        actions.AddRange(occupants.Where(item => item.Side != winner || item.Item != source)
            .Select(item => Action(ActionKind.Delete, item.Side, item.Item.Path, null, item.Item)));
        var guards = ActionPredicates(pair, LeftAlone(pair));
        if (actions.Any(action => !Allowed(guards, action)))
            return RefuseConflictSuccessor(pair, notice, noticeId);
        var operation = new ConflictNoticeOperation(choice, null, actions, []);
        _store.SaveConflictOperation(pair.PairId, noticeId, operation);
        return await ApplyConflictOperationAsync(pair, noticeId, operation, ct).ConfigureAwait(false);
    }

    private async Task<List<(Side Side, ItemState Item)>?> ConflictSuccessorOccupantsAsync(
        PairDef pair, ConflictNoticeVersions versions, Side absentSide, CancellationToken ct)
    {
        var occupants = new List<(Side, ItemState)>();
        foreach (var originalSide in new[] { Side.A, Side.B })
        {
            var original = originalSide == Side.A ? versions.A : versions.B;
            var key = PlanIds.Split(versions.Base!.ObjectKey, originalSide);
            var baseline = _store.Baselines(pair.PairId, [key]).SingleOrDefault();
            if (baseline is null || original.Kind != ItemKind.File ||
                Content.SameContent(baseline.A, original) != true || Content.SameContent(baseline.B, original) != true)
                return null;
            var originalRoot = originalSide == Side.A ? pair.A : pair.B;
            var savedOriginal = originalSide == Side.A ? baseline.A : baseline.B;
            if (!SameConflictItem(savedOriginal, original with { Path = savedOriginal.Path }, originalRoot)) return null;
            foreach (var side in new[] { Side.A, Side.B })
            {
                var root = side == Side.A ? pair.A : pair.B;
                var saved = side == Side.A ? baseline.A : baseline.B;
                var current = await _endpoints.Writer(root).ObserveAsync(saved.Path, ct).ConfigureAwait(false);
                var absent = absentSide == Side.A ? versions.A : versions.B;
                if (current is null && side == absentSide && originalSide == absentSide &&
                    PairPath.Same(saved.Path, absent.Path)) continue;
                if (current is null || current.Flags != ItemFlags.None || !SameConflictItem(current, saved, root) ||
                    Content.SameContent(current, original) != true) return null;
                occupants.Add((side, current));
            }
        }
        return occupants;
    }

    private ActionResult RefuseConflictSuccessor(PairDef pair, Notice notice, string noticeId)
    {
        const string reason = "A conflict successor is missing, changed, foreign, protected, or lacks content proof. Keep both or refresh and review the versions; no files changed.";
        _store.RecordNoticeChoiceFailure(notice, reason);
        return new(ShortId.Make("nch", pair.PairId, noticeId), Outcome.Skipped, null, null, null, reason);
    }
}

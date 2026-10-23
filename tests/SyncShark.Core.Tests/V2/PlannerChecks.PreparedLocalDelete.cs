namespace Massdex.SyncShark.V2;

/// <summary>
/// Prepare-matched post-prepare deletion (decision 71). A local file is deleted only on known-equal content under
/// the prepared identity: known-different keeps the RQ-043 copy-back, unknown holds with one notice and never
/// deletes. A prepared Delete without a trusted opposite identity also holds instead of running path-only. Local
/// folders carry no hash and still propagate recovery-first; remote targets keep their ordinary delete.
/// </summary>
internal static partial class PlannerChecks
{
    private static void PreparedLocalDelete_Checks()
    {
        PreparedLocalDelete_KnownEqualEmitsRecoveryFirstDelete();
        PreparedLocalDelete_UnknownHashHoldsWithoutDelete();
        PreparedLocalDelete_UnknownHashWithPreparedHashStampHolds();
        PreparedLocalDelete_UnprovenHashHoldsWithoutDelete();
        PreparedLocalDelete_KnownDifferentCopiesBack();
        PreparedLocalDelete_EqualHashDifferentIdentityCopiesBack();
        PreparedLocalDelete_LocalFolderEmitsRecoveryFirstDelete();
        PreparedLocalDelete_FolderPreservesChildIdentity();
        PreparedLocalDelete_RemoteTargetKeepsOrdinaryDelete();
        PreparedLocalDelete_MalformedOppositeStampHoldsWithoutDelete();
    }

    private static PlanOptions PreparedOptions(string path, string stampA, string stampB) =>
        Opt(firstSync: true) with
        {
            MatchedAtPrepare = new Dictionary<string, PrepareMatch>(StringComparer.OrdinalIgnoreCase)
            {
                [path] = new PrepareMatch(stampA, stampB),
            },
        };

    private static Plan PreparedLocal(ItemState local, string stampA, string stampB) =>
        Planner.Decide(PairId, Gen, new[] { Row(null, local, null, Ev(B: true)) },
            PreparedOptions(local.Path, stampA, stampB));

    // Safe case: the local survivor still carries the hash recorded at prepare, so the remote deletion propagates
    // as an ordinary recovery-first local delete with no notice.
    private static void PreparedLocalDelete_KnownEqualEmitsRecoveryFirstDelete()
    {
        var local = F("d/x", "loc:x", "hx");
        var plan = PreparedLocal(local, Content.SideStamp(local), "dbx-gone|v:1");
        AssertActions(plan, new Exp(ActionKind.Delete, Side.A, "d/x", null, true, 6));
        AssertEmptyNotices(plan);
        // The missing side's prepare stamp prefix is the trusted deletion-source identity.
        Require(plan.Actions.Single().DeletionSourceObjectId == "dbx-gone",
            "the Delete carries the missing side's prepare stamp identity");
        Console.WriteLine("PASS PreparedLocalDelete_KnownEqualEmitsRecoveryFirstDelete");
    }

    // No hash is available now and the prepare recorded only metadata: identity is unchanged but content is not
    // proven, so the local file is held (one Info notice) rather than deleted.
    private static void PreparedLocalDelete_UnknownHashHoldsWithoutDelete()
    {
        var local = F("d/x", "loc:x", hash: null);
        var plan = PreparedLocal(local, Content.SideStamp(local), "dbx-gone|v:1");
        AssertActions(plan, new Exp(ActionKind.Hold, Side.A, "d/x", null, false, 6));
        Require(plan.Notices.Count == 1 && plan.Notices[0] is
            { Rule: 6, Kind: NoticeKind.Info, Path: "d/x" } &&
            plan.Notices[0].ReasonKey == PlanNotices.ContentUnconfirmed.Key,
            "unknown local content raises exactly one dedup content-unconfirmed notice");
        Require(plan.Actions.Single().DeletionSourceObjectId is null, "a Hold carries no deletion-source identity");
        Console.WriteLine("PASS PreparedLocalDelete_UnknownHashHoldsWithoutDelete");
    }

    // The prepare recorded a hash, but the current hash is unavailable: Unchanged is false, yet this must hold
    // rather than fall through to the RQ-043 copy-back.
    private static void PreparedLocalDelete_UnknownHashWithPreparedHashStampHolds()
    {
        var local = F("d/x", "loc:x", hash: null);
        var plan = PreparedLocal(local, "loc:x|h:hx", "dbx-gone|v:1");
        AssertActions(plan, new Exp(ActionKind.Hold, Side.A, "d/x", null, false, 6));
        Require(plan.Notices.Count == 1 && plan.Notices[0].ReasonKey == PlanNotices.ContentUnconfirmed.Key,
            "an unknown current hash holds instead of copying the survivor back");
        Console.WriteLine("PASS PreparedLocalDelete_UnknownHashWithPreparedHashStampHolds");
    }

    // A hash is known now, but the prepare stamp recorded metadata only, so no matched hash can prove equality:
    // the conservative outcome is the same one notice and no delete.
    private static void PreparedLocalDelete_UnprovenHashHoldsWithoutDelete()
    {
        var local = F("d/x", "loc:x", "hy");
        var plan = PreparedLocal(local, Content.MetadataStamp(local), "dbx-gone|v:1");
        AssertActions(plan, new Exp(ActionKind.Hold, Side.A, "d/x", null, false, 6));
        Require(plan.Notices.Count == 1 && plan.Notices[0].ReasonKey == PlanNotices.ContentUnconfirmed.Key,
            "unproven local content raises exactly one dedup content-unconfirmed notice");
        Console.WriteLine("PASS PreparedLocalDelete_UnprovenHashHoldsWithoutDelete");
    }

    // A known hash that differs from the prepare stamp is an edit after prepare: RQ-043 keeps the existing
    // copy-back and never deletes, whether or not the absent side's identity is well formed.
    private static void PreparedLocalDelete_KnownDifferentCopiesBack()
    {
        var local = F("d/x", "loc:x", "hy");
        foreach (var missing in new[] { "dbx-gone|v:1", "no-separator" })
        {
            var plan = PreparedLocal(local, "loc:x|h:hx", missing);
            AssertActions(plan, new Exp(ActionKind.CopyNew, Side.B, "d/x", null, false, 7,
                SourcePath: "d/x", Key: NK(Side.A, "loc:x")));
            AssertNotices(plan, new NExp(7, NoticeKind.EditBeatsDelete, "d/x"));
        }
        Console.WriteLine("PASS PreparedLocalDelete_KnownDifferentCopiesBack");
    }

    // Equal bytes under a new object identity is not the prepared survivor: RQ-043 keeps it (copy-back), never
    // deletes it, even though the content hash matches.
    private static void PreparedLocalDelete_EqualHashDifferentIdentityCopiesBack()
    {
        var local = F("d/x", "loc:new", "hx");
        var plan = PreparedLocal(local, "loc:old|h:hx", "dbx-gone|v:1");
        AssertActions(plan, new Exp(ActionKind.CopyNew, Side.B, "d/x", null, false, 7,
            SourcePath: "d/x", Key: NK(Side.A, "loc:new")));
        AssertNotices(plan, new NExp(7, NoticeKind.EditBeatsDelete, "d/x"));
        Console.WriteLine("PASS PreparedLocalDelete_EqualHashDifferentIdentityCopiesBack");
    }

    // Folders carry no content hash: an unchanged prepared folder still propagates recovery-first (the empty
    // folder collapses to rule 23), with the trusted opposite identity for the executor's source probe.
    private static void PreparedLocalDelete_LocalFolderEmitsRecoveryFirstDelete()
    {
        var folder = D("d", "loc:d");
        var plan = PreparedLocal(folder, Content.SideStamp(folder), "dbx-gone|d");
        AssertActions(plan, new Exp(ActionKind.Delete, Side.A, "d", null, true, 23));
        AssertEmptyNotices(plan);
        Require(plan.Actions.Single().DeletionSourceObjectId == "dbx-gone",
            "the local folder Delete carries the missing side's prepare stamp identity");
        Console.WriteLine("PASS PreparedLocalDelete_LocalFolderEmitsRecoveryFirstDelete");
    }

    private static void PreparedLocalDelete_FolderPreservesChildIdentity()
    {
        var folder = D("d", "loc:d");
        var child = F("d/x", "loc:x", "hx");
        var options = PreparedOptions(folder.Path, Content.SideStamp(folder), "dbx-folder-gone|d") with
        {
            MatchedAtPrepare = new Dictionary<string, PrepareMatch>(StringComparer.OrdinalIgnoreCase)
            {
                [folder.Path] = new(Content.SideStamp(folder), "dbx-folder-gone|d"),
                [child.Path] = new(Content.SideStamp(child), "dbx-child-gone|v:1"),
            },
        };
        var plan = Planner.Decide(PairId, Gen,
            new[] { Row(null, folder, null, Ev(B: true)), Row(null, child, null, Ev(B: true)) }, options);
        Require(plan.Actions.Count == 2 && plan.Actions.All(action => action.Kind == ActionKind.Delete),
            "prepared descendant deletes are not discarded by folder collapse");
        Require(plan.Actions.Single(action => action.Path == "d") is
            { DescendantsPlanned: true, DeletionSourceObjectId: "dbx-folder-gone" },
            "the folder retains its own source identity and cannot expand over children");
        Require(plan.Actions.Single(action => action.Path == "d/x") is
            { RecoveryFirst: true, DeletionSourceObjectId: "dbx-child-gone" },
            "the child retains its distinct trusted source identity and Recovery-first protection");
        Console.WriteLine("PASS PreparedLocalDelete_FolderPreservesChildIdentity");
    }

    // Remote-target semantics are unchanged: a local deletion propagates to Dropbox even when the prepare recorded
    // only a version stamp, because Dropbox keeps the deleted revision.
    private static void PreparedLocalDelete_RemoteTargetKeepsOrdinaryDelete()
    {
        var remote = F("d/x", "dbx:x", hash: null, versionId: "rev1");
        var plan = Planner.Decide(PairId, Gen, new[] { Row(null, null, remote, Ev(A: true)) },
            PreparedOptions(remote.Path, "loc-gone|v:0", Content.SideStamp(remote)));
        AssertActions(plan, new Exp(ActionKind.Delete, Side.B, "d/x", null, true, 6));
        AssertEmptyNotices(plan);
        Require(plan.Actions.Single().DeletionSourceObjectId == "loc-gone",
            "the remote Delete carries the missing local side's prepare stamp identity");
        Console.WriteLine("PASS PreparedLocalDelete_RemoteTargetKeepsOrdinaryDelete");
    }

    // A missing or malformed opposite identity cannot support a prepared Delete: it holds with one notice and
    // never runs path-only. This includes an empty id, an empty type and an invalid metadata payload.
    private static void PreparedLocalDelete_MalformedOppositeStampHoldsWithoutDelete()
    {
        var local = F("d/x", "loc:x", "hx");
        foreach (var missing in new[] { "no-separator", "|h:hx", "loc:gone|", "loc:gone|q:hx", "loc:gone|m:x:y" })
        {
            var plan = PreparedLocal(local, Content.SideStamp(local), missing);
            AssertActions(plan, new Exp(ActionKind.Hold, Side.A, "d/x", null, false, 6));
            Require(plan.Notices.Count == 1 &&
                plan.Notices[0].ReasonKey == PlanNotices.ContentUnconfirmed.Key,
                "malformed opposite stamp yields exactly one unconfirmed notice: " + missing);
            Require(plan.Actions.Single().DeletionSourceObjectId is null,
                "a malformed missing-side stamp never invents a deletion-source identity");
        }
        // The same rule protects a remote target: with no trusted opposite identity it holds instead of deleting.
        var remote = F("d/x", "dbx:x", hash: null, versionId: "rev1");
        var remotePlan = Planner.Decide(PairId, Gen, new[] { Row(null, null, remote, Ev(A: true)) },
            PreparedOptions(remote.Path, "not-a-stamp", Content.SideStamp(remote)));
        AssertActions(remotePlan, new Exp(ActionKind.Hold, Side.B, "d/x", null, false, 6));
        Require(remotePlan.Notices.Count == 1 &&
            remotePlan.Notices[0].ReasonKey == PlanNotices.ContentUnconfirmed.Key,
            "a remote prepared Delete without a trusted identity holds with one notice");
        Console.WriteLine("PASS PreparedLocalDelete_MalformedOppositeStampHoldsWithoutDelete");
    }
}

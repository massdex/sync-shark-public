namespace Massdex.SyncShark.V2;

/// <summary>
/// Prepare-matched post-prepare deletion (decision 71). A local file is deleted only on known-equal content:
/// known-different keeps the RQ-043 copy-back, unknown holds with one notice and never deletes. Local folders
/// carry no hash and still propagate recovery-first; remote targets keep their ordinary delete.
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
        PreparedLocalDelete_LocalFolderEmitsRecoveryFirstDelete();
        PreparedLocalDelete_RemoteTargetKeepsOrdinaryDelete();
        PreparedLocalDelete_MalformedMissingStampCarriesNoIdentity();
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
    // copy-back and never deletes.
    private static void PreparedLocalDelete_KnownDifferentCopiesBack()
    {
        var local = F("d/x", "loc:x", "hy");
        var plan = PreparedLocal(local, "loc:x|h:hx", "dbx-gone|v:1");
        AssertActions(plan, new Exp(ActionKind.CopyNew, Side.B, "d/x", null, false, 7,
            SourcePath: "d/x", Key: NK(Side.A, "loc:x")));
        AssertNotices(plan, new NExp(7, NoticeKind.EditBeatsDelete, "d/x"));
        Console.WriteLine("PASS PreparedLocalDelete_KnownDifferentCopiesBack");
    }

    // Folders carry no content hash: an unchanged prepared folder still propagates recovery-first, with the
    // trusted opposite identity for the executor's source probe.
    private static void PreparedLocalDelete_LocalFolderEmitsRecoveryFirstDelete()
    {
        var folder = D("d", "loc:d");
        var plan = PreparedLocal(folder, Content.SideStamp(folder), "dbx-gone|d");
        AssertActions(plan, new Exp(ActionKind.Delete, Side.A, "d", null, true, 6));
        AssertEmptyNotices(plan);
        Require(plan.Actions.Single().DeletionSourceObjectId == "dbx-gone",
            "the local folder Delete carries the missing side's prepare stamp identity");
        Console.WriteLine("PASS PreparedLocalDelete_LocalFolderEmitsRecoveryFirstDelete");
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

    // A missing or malformed prepare stamp cannot yield an identity: the Delete still propagates under the safe
    // known-hash rule, but the optional field stays null rather than being invented.
    private static void PreparedLocalDelete_MalformedMissingStampCarriesNoIdentity()
    {
        var local = F("d/x", "loc:x", "hx");
        var plan = PreparedLocal(local, Content.SideStamp(local), "no-separator");
        AssertActions(plan, new Exp(ActionKind.Delete, Side.A, "d/x", null, true, 6));
        AssertEmptyNotices(plan);
        Require(plan.Actions.Single().DeletionSourceObjectId is null,
            "a malformed missing-side stamp never invents a deletion-source identity");
        Console.WriteLine("PASS PreparedLocalDelete_MalformedMissingStampCarriesNoIdentity");
    }
}

namespace Massdex.SyncShark.V2;

internal static class LowerPredicateChecks
{
    internal static void Run()
    {
        var empty = new PendingPathIndex([]);
        LocalChecks.Require(!empty.Contains("") && !empty.Covers("Parent"), "empty pending index does not hold root");
        var paths = new[] { "Parent/Child", "Other", "" };
        var lower = new PendingPathIndex(paths);
        var original = LinkExclusions.None.WithPending(paths);
        foreach (var path in new[] { "", "Parent", "PARENT/CHILD", "Parent/Child/Leaf", "Other", "Missing" })
            LocalChecks.Require(lower.Covers(path) == original.Covers(path) && lower.Contains(path) == original.Contains(path),
                "pending path membership preserves set semantics");
        LocalChecks.Require(!DecisionHolds.HasRootDecisionHold(new InMemoryStateStore(), "missing"), "missing pair never holds root");
        var action = new SyncAction("a", 23, ActionKind.Delete, Side.A, "Parent", null, null, null, false, null);
        LocalChecks.Require(!FolderExpansion.Expands(action), "null target never expands");
        var folder = new ItemState("Parent", ItemKind.Folder, "id", null, null, 0, DateTimeOffset.UnixEpoch, ItemFlags.None);
        LocalChecks.Require(FolderExpansion.Expands(action with { ExpectedTarget = folder }) &&
            !FolderExpansion.Expands(action with { ExpectedTarget = folder, DescendantsPlanned = true }), "folder expansion truth table");
        LocalChecks.Require(DeleteSourceNotice.NoticeId("pair", "Parent/Child") ==
            ShortId.Make("n", "pair", "0", PathKey.Of("Parent/Child"), "delete-source-unconfirmed"), "notice id stays exact");
        LocalChecks.Require(EffectReasons.NotApplied == "Not applied; re-planned this run" &&
            EffectReasons.Superseded == "Superseded by a later change" &&
            EffectReasons.RestoreAbandoned == "Restore attempt abandoned; any created item stays in place and Recovery remains available." &&
            InterruptedDelete.RestoreKind == "interrupted-delete-restore" && InterruptedDelete.UnverifiedReason == "interrupted-delete-unverified",
            "persisted vocabulary stays exact");
        LocalChecks.Require(ExclusionPaths.Validate(["Parent/", "parent", "Other"]).SequenceEqual(new[] { "Parent", "Other" }),
            "exclusions retain first spelling and remove one trailing slash");
        foreach (var path in new[] { "", "/Parent", "Parent//", "Parent/../Child", "Parent\\Child" })
        {
            string? lowerMessage = null;
            string? edgeMessage = null;
            try { ExclusionPaths.Validate([path]); } catch (ExclusionPathValidationException error) { lowerMessage = error.Message; }
            try { CreateExclusions.Validate([path]); } catch (SyncCliUsageException error) { edgeMessage = error.Message; }
            LocalChecks.Require(lowerMessage is not null && lowerMessage == edgeMessage, "only validation exception is translated with exact text");
        }
        LocalChecks.Require(ExclusionPaths.ValidateSelection("Parent/") == "Parent", "selection uses the same normalization");
        foreach (var reprobe in new[] { false, true })
            foreach (var sleeping in new[] { false, true })
                foreach (var restored in new[] { false, true })
                {
                    var retry = new SyncRetry("Parent", "sync", 0, "", sleeping ? DateTimeOffset.MaxValue : DateTimeOffset.MinValue)
                        { ReprobeEvidence = reprobe };
                    LocalChecks.Require(RetryProtectionDomain.Includes(retry, restored) ==
                        ((!reprobe || !sleeping) && (!restored || reprobe)), "retry domain matches SQL condition truth table");
                }
    }
}

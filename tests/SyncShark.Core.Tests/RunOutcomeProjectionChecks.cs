using Massdex.SyncShark;

// SS-D02: pure checks on the ONE shared run-outcome classification. The decision under test is the real
// production SyncRunOutcomeProjection and every input is either an explicit fact set or a real
// SyncRunResult built from real SyncDecision values. Nothing here creates state, calls a provider, reads
// a credential or touches a synced folder.
internal static class RunOutcomeProjectionChecks
{
    internal static Task<int> RunAsync(string root)
    {
        var checks = 0;
        void Require(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("Run outcome projection: " + name);
            checks++;
            Console.WriteLine("PASS " + name);
        }

        // A real complete clean result: one upload, one unchanged file and one deliberate skip.
        var clean = new SyncRunResult("pair-clean", [
            new SyncDecision("upload.txt", SyncDecisionKind.Uploaded, "Uploaded the reviewed change."),
            new SyncDecision("unchanged.txt", SyncDecisionKind.Identical, "Already identical."),
            new SyncDecision("later.txt", SyncDecisionKind.Skipped, "Skipped for now at the reviewer's request.")]);
        var cleanOutcome = SyncRunOutcomeProjection.FromResult(clean);
        Require(cleanOutcome.Kind == SyncRunOutcomeKind.Success && cleanOutcome.IsSuccess && !cleanOutcome.Attention,
            "a complete clean run is the only success");
        Require(cleanOutcome.StatusWord == "Finished" && cleanOutcome.Headline == "Sync Completed",
            "a success keeps the finished status word and the completion headline");
        Require(cleanOutcome.CountLines.Contains("Skipped: 1") && cleanOutcome.UnresolvedItems == 0 &&
                cleanOutcome.AttentionItems == 0,
            "an intentional skip is displayed separately and is not unresolved work");

        // Needs verification only: no blocked or conflicting item at all, and the run must still not be a
        // success and must never wear the completion headline.
        var verifying = new SyncRunResult("pair-verifying", [
            new SyncDecision("unknown.txt", SyncDecisionKind.ResponseUnknown, "The provider response was lost."),
            new SyncDecision("cleanup.txt", SyncDecisionKind.CleanupPending, "A cleanup still needs verification.")]);
        var verifyingOutcome = SyncRunOutcomeProjection.FromResult(verifying);
        Require(verifyingOutcome.Kind == SyncRunOutcomeKind.NeedsAttention && verifyingOutcome.Attention &&
                verifyingOutcome.Headline != "Sync Completed" && verifyingOutcome.StatusWord == "Needs Attention",
            "an unverified run is never a success and never states the completion headline");
        Require(verifyingOutcome.AttentionItems == 0 && verifyingOutcome.NeedsVerification == 2 &&
                verifyingOutcome.CountLines.Contains("Needs verification: 2"),
            "unverified items stay visible beside a zero attention count");

        // Blocked and conflicting items: the attention count is the failed/blocked plus conflicting count,
        // and the failed subset is never counted twice.
        var blocked = new SyncRunResult("pair-blocked", [
            new SyncDecision("failed-a.txt", SyncDecisionKind.Blocked, "The upload was refused."),
            new SyncDecision("failed-b.txt", SyncDecisionKind.Blocked, "The download was refused."),
            new SyncDecision("conflict.txt", SyncDecisionKind.Conflict, "Both folders changed.")]);
        var blockedOutcome = SyncRunOutcomeProjection.FromResult(blocked);
        Require(blockedOutcome.Kind == SyncRunOutcomeKind.NeedsAttention &&
                blockedOutcome.Failed == 2 && blockedOutcome.Blocked == 2 && blockedOutcome.Conflicts == 1,
            "real blocked and conflicting decisions are classified as attention");
        Require(blockedOutcome.AttentionItems == 3 && blockedOutcome.UnresolvedItems == 3,
            "the recorded failure subset is counted once in the attention and unresolved totals");
        Require(blockedOutcome.CountLines.Contains("Needs attention: 3"),
            "the shared count lines state the attention total every consumer shows");

        // Not attempted work on its own: never attempted is unresolved even when nothing failed.
        var deferred = SyncRunOutcomeProjection.Classify(new SyncRunOutcomeInput { NotAttempted = 2 });
        Require(deferred.Kind == SyncRunOutcomeKind.NeedsAttention && deferred.AttentionItems == 0 &&
                deferred.UnresolvedItems == 2 && deferred.CountLines.Contains("Not attempted: 2"),
            "not-attempted work alone is unresolved and stays visible");

        // Stopped plus unresolved work: the stop is stated AND the unresolved counts stay visible with it.
        var stopped = new SyncRunResult("pair-stopped", [
            new SyncDecision("uploaded.txt", SyncDecisionKind.Uploaded, "Uploaded before the stop."),
            new SyncDecision("unknown.txt", SyncDecisionKind.ResponseUnknown, "The response was lost before the stop."),
            new SyncDecision("<run>", SyncDecisionKind.Stopped, "Stopped at the user's request.")]);
        var stoppedOutcome = SyncRunOutcomeProjection.FromResult(stopped);
        Require(stoppedOutcome.Kind == SyncRunOutcomeKind.Stopped && stoppedOutcome.Attention &&
                stoppedOutcome.Headline == "Sync Stopped" && stoppedOutcome.StatusWord == "Stopped",
            "a stopped run states the stop and never the completion headline");
        Require(stoppedOutcome.NeedsVerification == 1 && stoppedOutcome.UnresolvedItems == 1 &&
                stoppedOutcome.CountLines.Contains("Needs verification: 1") &&
                stoppedOutcome.CountLines.Contains("Uploaded: 1"),
            "a stopped run keeps its unresolved and completed counts visible beside the stop");

        // A skipped-only in-memory run is still a success: a deliberate skip is not a failure.
        var skippedOnly = SyncRunOutcomeProjection.Classify(new SyncRunOutcomeInput { Skipped = 4 });
        Require(skippedOnly.Kind == SyncRunOutcomeKind.Success && skippedOnly.CountLines.Contains("Skipped: 4"),
            "deliberate skips alone do not turn a completed run into attention");

        // Absent, malformed or incomplete retained counts can never be a success.
        var unrecorded = SyncRunOutcomeProjection.Classify(new SyncRunOutcomeInput { CountsRecorded = false });
        Require(unrecorded.Kind == SyncRunOutcomeKind.NotRecorded && !unrecorded.IsSuccess && unrecorded.Attention &&
                unrecorded.StatusWord == "Outcome not recorded" &&
                unrecorded.Headline == "Sync Outcome Not Recorded",
            "unrecorded counts are never a success and never state a completion");
        Require(SyncRunOutcomeProjection.Classify(new SyncRunOutcomeInput { CountsRecorded = false, Stopped = true })
                .Kind == SyncRunOutcomeKind.Stopped,
            "a recorded stop is still stated even when the counts beside it are not recorded");

        // An unfinished run cannot be a success, and the fake input never substitutes for the real result.
        Require(SyncRunOutcomeProjection.FromResult(clean, completed: false).Kind == SyncRunOutcomeKind.NotRecorded,
            "a run that has not completed is not classified as a success");
        Require(SyncRunOutcomeProjection.FromResult(clean).Uploaded == clean.Uploaded &&
                SyncRunOutcomeProjection.FromResult(clean).Unchanged == clean.Identical &&
                SyncRunOutcomeProjection.FromResult(clean).Skipped == clean.Skipped,
            "the projection reads the real result's own counts rather than a supplied summary");

        Console.WriteLine("{\"result\":\"PASS\",\"checks\":" + checks + "," +
            "\"boundary\":\"real production projection and real SyncRunResult inputs; no state, provider, " +
            "credential or file effect\"}");
        return Task.FromResult(0);
    }
}


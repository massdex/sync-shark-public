namespace Massdex.SyncShark.V2;

internal static partial class AutoSyncChecks
{
    private static void StateMonitorSignals()
    {
        var journal = new NtfsUsnCheckpoint(7, 100, 300, 120);
        string Cursor(ulong id, long next) => (journal with { JournalId = id, NextUsn = next }).Cursor;
        var caughtUp = StateMonitor.Journal(journal, Cursor(7, 300));
        Require(caughtUp.ReplayCursor is null && caughtUp.UnknownScope is null,
            "caught-up journal needs neither replay nor repair");
        var lagged = StateMonitor.Journal(journal, Cursor(7, 180));
        Require(lagged.ReplayCursor == Cursor(7, 180) && lagged.UnknownScope is null,
            "retained journal lag replays without repair");
        foreach (var saved in new[] { Cursor(8, 180), Cursor(7, 90), Cursor(7, 110), Cursor(7, 301) })
        {
            var gap = StateMonitor.Journal(journal, saved);
            Require(gap.UnknownScope?.Contains("Parent/Child") == true && gap.ReplayCursor == Cursor(7, 120),
                "changed journal, wrapped history or future cursor repairs root while replaying retained history");
        }
        Require(StateMonitor.Journal(journal, Cursor(7, 90)).Trigger == ReconciliationTrigger.GapSameJournal &&
            StateMonitor.Journal(journal, Cursor(7, 90)).SavedCursor == Cursor(7, 90) &&
            StateMonitor.Journal(journal, Cursor(8, 180)).Trigger == ReconciliationTrigger.JournalReplaced &&
            StateMonitor.Journal(journal, Cursor(7, 301)).Trigger == ReconciliationTrigger.JournalPositionInvalid,
            "selection receives the trigger and original cursor, not the replay start");
        var overflow = StateMonitor.Journal(journal, Cursor(7, 300), watcherFault: true);
        Require(overflow.ReplayCursor == Cursor(7, 300) && overflow.UnknownScope is null,
            "watcher overflow first checks replay rather than declaring unknown files");
        Require(StateMonitor.ReplayGap([]) is null, "successful replay creates no repair scope");
        var partial = StateMonitor.ReplayGap(["Parent/Gap"]);
        Require(partial?.Contains("Parent/Gap/Child") == true && !partial.Contains("Parent/Known"),
            "partial replay gap does not promote its known sibling");
        Require(StateMonitor.DropboxReset().UnknownScope?.Contains("Parent/Child") == true &&
            StateMonitor.DropboxReset("Parent/Gap").UnknownScope?.Contains("Parent/Known") == false &&
            StateMonitor.DropboxReset().Trigger == ReconciliationTrigger.DropboxReset,
            "Dropbox reset preserves reported subtree scope or uses root when unknown");
        var exact = new ReconciliationScope(["Parent/File"], ["Other/Folder"]);
        Require(exact.Contains("parent/file") && !exact.Contains("Parent/File/Child") &&
            exact.Contains("Other/Folder/Child") && !exact.Contains("Other/FolderSibling"),
            "exact paths and subtree boundaries have distinct coverage");
        Require(exact.Touches("Parent", folder: true) && !exact.Touches("Parent", folder: false) &&
            exact.Touches("", folder: true), "folder actions include scoped descendant dependencies");
        var durable = new LocalGapEpoch("epoch", Cursor(7, 120), 90, true, "", false);
        var restarted = StateMonitor.FromGap(durable);
        Require(restarted.Trigger == ReconciliationTrigger.GapSameJournal && restarted.SavedCursor == Cursor(7, 90) &&
            restarted.UnknownScope?.Contains("Parent/Child") == true,
            "restart reconstructs the lost boundary rather than the advanced replay position");
        Require(StateMonitor.FromGap(durable with { TrustFileUsn = false }).Trigger == ReconciliationTrigger.JournalReplaced &&
            StateMonitor.FromGap(durable with { JournalCursor = "invalid" }).Trigger == ReconciliationTrigger.JournalPositionInvalid &&
            StateMonitor.PlanningBacklog().Trigger == ReconciliationTrigger.PlanningBacklog,
            "untrusted legacy gaps and planning backlog retain their selection policies");
        Pass("StateMonitorSignals");
    }
}

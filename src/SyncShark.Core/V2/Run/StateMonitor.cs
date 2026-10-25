namespace Massdex.SyncShark.V2;

/// <summary>Classifies existing change-feed signals without reading inventory or planning effects.</summary>
internal static class StateMonitor
{
    /// <summary>A journal query is constant work; replay available history even when an older part was lost.</summary>
    internal static StateMonitorDecision Journal(NtfsUsnCheckpoint current, string? savedCursor,
        bool watcherFault = false)
    {
        if (current.RequiresFullReconciliation(savedCursor))
        {
            var first = Math.Max(current.FirstUsn, current.LowestValidUsn);
            var replay = first < current.NextUsn
                ? (current with { NextUsn = first }).Cursor : null;
            var parsed = NtfsUsnCheckpoint.TryParseCursor(savedCursor, out var journalId, out var saved);
            var trigger = !parsed ? ReconciliationTrigger.JournalPositionInvalid :
                journalId != current.JournalId ? ReconciliationTrigger.JournalReplaced :
                saved > current.NextUsn ? ReconciliationTrigger.JournalPositionInvalid :
                ReconciliationTrigger.GapSameJournal;
            return new(replay, ReconciliationScope.Root, trigger, savedCursor);
        }
        NtfsUsnCheckpoint.TryParseCursor(savedCursor, out _, out var next);
        return new(next < current.NextUsn || watcherFault ? savedCursor : null, null,
            watcherFault ? ReconciliationTrigger.OverflowReplay : ReconciliationTrigger.JournalReplay, savedCursor);
    }

    /// <summary>After replay, only the uncovered subtrees need repair; known changes remain AutoSync input.</summary>
    internal static ReconciliationScope? ReplayGap(IReadOnlyCollection<string> uncoveredSubtrees) =>
        uncoveredSubtrees.Count == 0 ? null : new([], uncoveredSubtrees);

    /// <summary>A provider cursor reset invalidates only its reported scope, or the root when unnamed.</summary>
    internal static StateMonitorDecision DropboxReset(string affectedSubtree = "") =>
        new(null, new([], [affectedSubtree]), ReconciliationTrigger.DropboxReset);
}

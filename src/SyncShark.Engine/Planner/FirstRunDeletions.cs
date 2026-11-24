namespace Massdex.SyncShark.Engine.Planner;

/// <summary>Decides local-only rows without a sync record, using deletion evidence when required.</summary>
internal static class FirstRunDeletions
{
    /// <summary>A new local item is copied unless the row requires deletion handling.</summary>
    internal static SyncAction ForLocalOnly(Row row, Entry local, FirstRunPolicy policy, DateTimeOffset comparedAt)
    {
        var context = row.Context;
        var preserve = context.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice;
        if (preserve && !local.IsFolder)
            return Decide.Target(row, ActionKind.Upload, context.Destination, TextKey.InfoEditBeatsDelete);
        if (!preserve && !context.DeletionEligible && context.FirstRunDeletion != IssueSubject.FirstRunDeletionKind.Item)
            return Decide.Target(row, SyncAction.CopyOn(local.IsFolder, Side.Dropbox), context.Destination);
        if (!context.CompleteInventory) return Decide.None(row, NoneReason.Held);
        // The ordered folder pass owns the content mark and unreadable cause; never infer them from this row.
        return local.IsFolder ? Decide.DeferFolder(row) : FirstRunDeletion(row, local, policy, comparedAt);
    }

    /// <summary>Remove only when the exact target has matching deleted content and a confirmed later server deletion.</summary>
    private static SyncAction FirstRunDeletion(Row row, Entry local, FirstRunPolicy policy, DateTimeOffset comparedAt)
    {
        if (row.Context.Destination is not { } destination)
            return Decide.Notice(row, NoticeKind.Failure, TextKey.FailChanged);
        if (row.Deletion is not { } evidence)
            return new SyncAction(ActionKind.NeedDeletionEvidence, row, Target: destination);
        if (!PathNames.Same(evidence.Path, destination))
            return Decide.Notice(row, NoticeKind.Failure, TextKey.FailChanged);
        if (evidence is DeletionEvidence.LiveAtTarget live)
        {
            var observed = row with { Dropbox = live.Location.Entry, Deletion = null };
            if (observed.Dropbox.Skip is not null) return Decide.None(observed, NoneReason.Skipped);
            // Only this observed row carries first-sync policy into a possible hash redecision.
            if (!observed.Context.FirstRun) observed = observed with { Context = observed.Context with { FirstRun = true } };
            return Decide.NoRecord(observed, policy, comparedAt);
        }
        if (evidence is DeletionEvidence.LiveElsewhere) return Decide.Notice(row, NoticeKind.Failure, TextKey.FailChanged);
        if (evidence is DeletionEvidence.DeletedFile { ContentHash: { } hash, DeletedAt: { } deleted } &&
            !TimeRules.Unreliable(local.Modified, comparedAt) && TimeRules.DefinitelyDeletedLater(local.Modified, deleted, row.DeletionClock))
        {
            if (row.LocalHash is null) return Decide.NeedHash(row);
            if (row.LocalHash == hash) return new SyncAction(ActionKind.DeleteLocal, row, Target: local.Path);
        }
        return Decide.Target(row, ActionKind.Upload, destination, TextKey.InfoEditBeatsDelete);
    }
}

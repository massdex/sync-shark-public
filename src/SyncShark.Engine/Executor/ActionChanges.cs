using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Builds an action's state changes and renders its committed outcome.</summary>
internal static class ActionChanges
{
    internal static IssueSubject.FirstRunDeletionKind Owed(Row row, SyncAction? action)
    {
        var kept = row.Context.FirstRunDeletion;
        if (kept == IssueSubject.FirstRunDeletionKind.None &&
            !(row.Context.CompleteInventory && row.Context.FirstRun && IssuePolicy.UnbaselinedLocal(row)))
            return kept;
        if (action is { Kind: ActionKind.Upload or ActionKind.EnsureDropboxFolder,
            InfoAfter: TextKey.InfoDropboxHistoryUnchecked })
            return IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice;
        return kept == IssueSubject.FirstRunDeletionKind.None ? IssueSubject.FirstRunDeletionKind.Item : kept;
    }

    internal static StateChange Decorate(StateChange change, Row row, SyncAction? action, bool succeeded, Row? firstRunFolder)
    {
        var original = firstRunFolder ?? row;
        var owed = Owed(original, action);
        if (owed == IssueSubject.FirstRunDeletionKind.None || original.Local is not { } local) return change;
        if (succeeded && (!local.IsFolder || firstRunFolder is not null))
        {
            var raises = change.Raise;
            if (owed == IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice &&
                !raises.Any(item => item.Cause == TextKey.InfoDropboxHistoryUnchecked))
                raises = [.. raises, (IssueKind.Info, TextKey.InfoDropboxHistoryUnchecked, local.Path, Subject(original))];
            return change with { FirstRunAcknowledged = [new IssueItem(Side.Local, local.Path, local.Id)], Raise = raises };
        }
        if (succeeded || action?.Kind == ActionKind.DeferFolder ||
            action is { None: NoneReason.OutOfScope or NoneReason.RemovedWithFolder }) return change;
        // A definite refusal already carries the retained obligation; a waiting cause would replace it and free the row.
        if (action is { None: NoneReason.Held } && original.Context.HeldKind == IssueKind.Failure &&
            original.Context.FirstRunDeletion != IssueSubject.FirstRunDeletionKind.None) return change;
        var subject = Subject(original) with { FirstRunDeletion = owed };
        var failures = change.Raise.ToList();
        var index = failures.FindIndex(item => item.Kind == IssueKind.Failure);
        if (index >= 0) failures[index] = (failures[index].Kind, failures[index].Cause, failures[index].Path, subject);
        else failures.Add((IssueKind.Failure, TextKey.FailFirstSyncCheckWaiting, local.Path, subject));
        return change with { Raise = failures };
    }

    internal static StateChange WithInfo(StateChange change, SyncAction action) => WithInfo(change, action.InfoAfter, action.Row);

    internal static StateChange WithInfo(StateChange change, TextKey? cause, Row row) => cause is { } info &&
        !(row.Local is { IsFolder: true } &&
          row.Context.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice)
        ? change with { Raise = [.. change.Raise, (IssueKind.Info, info, Row.Path(row), Subject(row))] } : change;

    internal static IReadOnlyList<(string Path, IReadOnlyList<IssueKind> Kinds)> Closures(SyncAction action, bool all, bool closeIssues)
    {
        if (!closeIssues) return [];
        var row = action.Row;
        var paths = new[] { row.Local?.Path, row.Dropbox?.Path, row.Synced?.LocalPath, row.Synced?.DropboxPath, action.Target }
            .OfType<string>().DefaultIfEmpty(Row.Path(row)).Distinct();
        var inSync = action.Kind == ActionKind.Record || action.IsInSync();
        var kinds = all ? IssueClosure.AfterHeldInSync
            : inSync ? IssueClosure.AfterInSync : IssueClosure.AfterEffect;
        return paths.Select(path => (path, kinds)).ToArray();
    }

    internal static IReadOnlyList<(Side Side, string Key)> Subtrees(Row row) =>
        [(Side.Local, row.Synced?.LocalKey ?? PathNames.Key(row.Local!.Path)),
         (Side.Dropbox, row.Synced?.DropboxKey ?? PathNames.DropboxKey(row.Dropbox!.Path))];

    internal static IssueSubject Subject(Row row)
    {
        var items = new List<IssueItem>();
        if (row.Local is { } local) items.Add(new IssueItem(Side.Local, local.Path, local.Id));
        if (row.Dropbox is { } dropbox) items.Add(new IssueItem(Side.Dropbox, dropbox.Path, dropbox.Id));
        // Extra candidates are always Dropbox entries: local names can't collide case-insensitively on Windows.
        items.AddRange(row.Extra.Select(extra => new IssueItem(Side.Dropbox, extra.Path, extra.Id)));
        var proof = row.LocalHash is { } hash && row.Local is { IsFolder: false, Skip: null } observed
            ? new LocalHashProof(observed.Size, observed.Modified.UtcTicks, hash) : null;
        return new IssueSubject(items, null, proof);
    }

    // Event table (engine-files.md): a state-only effect (Recorded, Forgotten) has Outcome ""; keep-both writes one
    // Effect per side, Path = the label, Detail = the original it copies.
    internal static void Emit(Action<RunEvent> events, EffectKind? effect, Side? written, string path, string detail,
        KeepBothLabels? labels, Row row, List<RunEvent>? settled)
    {
        if (effect == EffectKind.KeptBoth)
        {
            if (labels!.DropboxLabel is { } dropboxLabel)
                events(RunEvent.Effect(EffectKind.KeptBoth, dropboxLabel,
                    row.Local?.Path ?? "", Side.Dropbox));
            if (labels.LocalLabel is { } localLabel)
                events(RunEvent.Effect(EffectKind.KeptBoth, localLabel,
                    row.Dropbox?.Path ?? "", Side.Local));
        }
        else if (effect != null)
            events(RunEvent.Effect(effect.Value, path, detail, written));
        EmitSettled(events, settled);
    }

    internal static void CollectSettlement(ref List<RunEvent>? settled, Issue issue, bool isNew,
        Exception? error = null, TextKey? attemptedCause = null, ActionKind? attemptedOperation = null)
    {
        if (attemptedCause != issue.Cause) error = null;
        if (isNew) (settled ??= []).Add(RunEvent.Issue(issue.Kind, issue.Cause, issue.Path, error));
        CollectSettlement(ref settled, issue.Cause, issue.Path, error, attemptedOperation);
    }

    internal static void CollectSettlement(ref List<RunEvent>? settled, TextKey cause, string path,
        Exception? error = null, ActionKind? attemptedOperation = null, DateTimeOffset? retryAt = null) =>
        (settled ??= []).Add(RunEvent.Wait(cause, path, error, attemptedOperation, retryAt));

    internal static void EmitSettled(Action<RunEvent> events, List<RunEvent>? settled)
    {
        if (settled is null) return;
        foreach (var item in settled)
            if (item.Kind == RunEventKind.Issue) events(item);
        foreach (var item in settled)
            if (item.Kind == RunEventKind.Wait) events(item);
    }

    internal static SyncOutcome Outcome(SyncAction action, EffectKind? effect, StateChange change) =>
        action.IsInSync() ? SyncOutcome.InSync :
        new(action.Kind, action.Kind == ActionKind.None ? action.None : null, effect,
            change.Raise.Count == 0 ? null : change.Raise[0].Kind, false,
            change.Raise.Count == 0 ? null : change.Raise[0].Cause);

}

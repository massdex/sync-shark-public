using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Storage;

public static partial class StateStoreChecks
{
    private static Task FolderMoveCustodyCases(string path)
    {
        var saved = Saved(Parent) with { IsFolder = true };
        var armed = new FolderMove(saved.DropboxId, saved.DropboxId, null, Parent, Moved, null,
            FolderMovePhase.Armed, null, null, 0);
        FolderMove filling;
        using (var store = StateStore.Open(path))
        {
            store.SavePair(BoundPair());
            store.Commit(PairId, StateChange.Empty with { Record = [saved] });
            var generation = Generation(store);
            Refuses<LiveAdmissionStaleException>(() => store.SetFolderMove(PairId, generation + 1, null, armed));
            Check.Require(store.FolderMoves(PairId, armed.RootId).Count == 0, "stale execution cannot arm custody");
            store.SetFolderMove(PairId, generation, null, armed);
            Refuses<ChangedUnderneathException>(() => store.SetFolderMove(PairId, generation, null, armed));
            store.HoldFolderMoves(PairId);
            var hold = store.MoveResponsibility(PairId, armed.SourceId)!;
            Check.Require(hold.Cause == TextKey.FailMoveInParts && hold.Kind == IssueKind.Failure &&
                hold.Subject.Items.Contains(new IssueItem(Side.Local, saved.LocalPath, saved.LocalId)) &&
                hold.Subject.Move == new IssueSubject.UnknownMove(saved.DropboxId, Parent, Moved, null, true),
                "recovery restores the split hold with both identities");
            Check.Require(IssuePolicy.PublicKind(hold.Kind, hold.Cause) == IssueKind.Info &&
                store.OpenIssuePage(PairId, null, null, [IssueKind.Info], Single, 0).Total == 1 &&
                store.OpenIssuePage(PairId, null, null, [IssueKind.Failure], Single, 0).Total == 0,
                "split progress has one public informational category while stored responsibility stays Failure");
            store.DismissIssue(PairId, hold.Id);
            store.HoldFolderMoves(PairId);
            Check.Require(store.MoveResponsibility(PairId, armed.SourceId) is { Dismissed: true },
                "dismissal remains responsibility and the prelude does not reopen it");
            filling = armed with { DestinationId = "destination-id", Phase = FolderMovePhase.Filling,
                Launch = new[] { ("second-id", "Second"), ("first-id", "First") }, JobId = "job-id", Generation = 1 };
            store.SetFolderMove(PairId, generation, armed, filling);
            Refuses<ChangedUnderneathException>(() => store.SetFolderMove(PairId, generation, armed, filling));
            store.Commit(PairId, StateChange.Empty with { Close = [(Parent, new[] { IssueKind.Failure })] });
            Check.Require(store.MoveResponsibility(PairId, armed.SourceId) is not null,
                "a partial split retains its hold against ordinary closure");
            store.Commit(PairId, Raise(TextKey.FailDropboxNoPermission, Parent, hold.Subject));
            Check.Require(store.MoveResponsibility(PairId, armed.SourceId) is
                { Cause: TextKey.FailDropboxNoPermission, Dismissed: false }, "split owner can re-cause and reopen its hold");
            Refuses<ChangedUnderneathException>(() => store.Commit(PairId, StateChange.Empty with { SplitRelabel = armed.RootId }));
            Check.Require(store.SyncedById(PairId, Side.Dropbox, armed.SourceId) == saved,
                "unfilled relabel rolls back without touching the binding");
        }
        using (var store = StateStore.Open(path))
        {
            var recovered = store.FolderMoves(PairId, armed.RootId).Single();
            Check.Require(recovered.Launch!.SequenceEqual(filling.Launch!) && recovered.JobId == filling.JobId &&
                recovered.Generation == filling.Generation, "reopen preserves original wire order and job custody");
            using (var roots = store.FolderMoveRoots(PairId, default).GetEnumerator())
            {
                Check.Require(roots.MoveNext() && roots.Current.SourceId == armed.SourceId, "root keyset yields custody");
                store.SaveLimiterLevel(Account, Single);
                Check.Require(!roots.MoveNext(), "a yielded root keeps no active reader");
            }
            var filled = recovered with { Phase = FolderMovePhase.Filled, Launch = null, JobId = null, Generation = recovered.Generation + 1 };
            store.SetFolderMove(PairId, Generation(store), recovered, filled);
            var hold = store.MoveResponsibility(PairId, armed.SourceId)!;
            var subject = hold.Subject with
            {
                Move = hold.Subject.Move! with { ExpectedId = filled.DestinationId! },
                Items = hold.Subject.Items.Select(item => item.Side == Side.Dropbox
                    ? item with { Id = filled.DestinationId! } : item).ToArray()
            };
            store.Commit(PairId, Raise(hold.Cause, Parent, subject) with { SplitRelabel = armed.RootId });
            var relabelled = store.FolderMoves(PairId, armed.RootId).Single();
            Check.Require(relabelled.Phase == FolderMovePhase.Relabelled && relabelled.Generation == filled.Generation + 1 &&
                store.SyncedById(PairId, Side.Dropbox, armed.SourceId) is null &&
                store.SyncedById(PairId, Side.Dropbox, filled.DestinationId!) == saved with { DropboxId = filled.DestinationId! } &&
                store.MoveResponsibility(PairId, armed.SourceId) is null && store.MoveResponsibility(PairId, filled.DestinationId!) is not null,
                "relabel changes bindings, receipts and hold identity in one commit");
            store.Commit(PairId, StateChange.Empty with { Close = [(Parent, new[] { IssueKind.Failure })] });
            store.HoldFolderMoves(PairId);
            Check.Require(store.Issues(PairId).Count == 0, "relabel releases retention and never restores source custody");
            store.ForgetAllSynced(PairId);
            Check.Require(store.FolderMoves(PairId, armed.RootId).Count == 1, "history reset preserves custody");
            store.RemovePair(PairId);
            store.SavePair(BoundPair());
            Check.Require(!store.FolderMoveRoots(PairId, default).Any(), "pair removal cascades split rows");
        }
        return Task.CompletedTask;
    }

    private static Task FolderMoveUpgradeCases(string path)
    {
        using (var store = StateStore.Open(path))
        {
            store.SavePair(BoundPair());
            store.Commit(PairId, StateChange.Empty with { Record = [Saved()] });
        }
        using (var connection = Inspect(path)) Execute(connection, "DROP TABLE folder_move;PRAGMA user_version=15;");
        RefuseOldRead(path);
        using (var upgraded = StateStore.Open(path))
            Check.Require(upgraded.SyncedById(PairId, Side.Dropbox, Saved().DropboxId) == Saved() &&
                !upgraded.FolderMoveRoots(PairId, default).Any(), "version 15 upgrade preserves bindings and creates empty custody");
        using (var reopened = StateStore.OpenReadOnly(path)!)
            Check.Require(!reopened.FolderMoveRoots(PairId, default).Any(), "version 16 opens read-only");
        return Task.CompletedTask;
    }

    private static Task FolderMovePendingCases(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(BoundPair());
        store.Commit(PairId, Raise(TextKey.FailChanged, Parent));
        Check.Require(!store.HasPendingUnder(PairId, Parent), "cleanup's own root notice does not block cleanup");
        store.Commit(PairId, Raise(TextKey.FailChanged, Sibling));
        Check.Require(!store.HasPendingUnder(PairId, Parent), "a lexical sibling is not a descendant");
        store.Commit(PairId, Raise(TextKey.FailChanged, Child));
        Check.Require(store.HasPendingUnder(PairId, Parent), "an undischarged descendant notice blocks cleanup");
        store.DismissIssue(PairId, store.Issues(PairId).Single(issue => issue.Path == Child).Id);
        Check.Require(!store.HasPendingUnder(PairId, Parent), "a dismissed notice without retry debt is discharged");
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var members = new[] { (PairId, Generation(store), local.Epoch, local.Revision, false) };
        var position = new JournalPosition(Single, 0);
        var notice = new ChangeNotice(Side.Local, ChangeKind.Changed, Other, Child, LocalId, Entry(Other), true, false);
        store.AcquireJournalPage(Domain, null, position, members, [(PairId, notice)]);
        Check.Require(store.HasPendingUnder(PairId, Parent) && store.HasPendingUnder(PairId, Other),
            "current work blocks on its old descendant path and its exact current path");
        Check.Require(!store.HasPendingUnder(PairId, Moved), "unrelated current work does not block cleanup");
        return Task.CompletedTask;
    }
}

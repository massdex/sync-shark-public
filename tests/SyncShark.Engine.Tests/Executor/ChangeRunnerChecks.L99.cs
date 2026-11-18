using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

public static partial class ChangeRunnerChecks
{
    private const string ElsewhereId = "elsewhere-id";

    // L99-b (B): a wait whose subject records only a Local file at another path is discovered by the replaced-file
    // retry read, so a fresh read of that path can end it. The same wait names no candidate for a third, unrelated
    // path. Both stores must answer alike on both the positive and the negative lookup.
    internal static void L99ElsewherePathWait()
    {
        foreach (var real in new[] { false, true })
        {
            var attempt = Guid.NewGuid();
            using var fixture = new Fixture(realStore: real, registeredAttempt: attempt);
            TrustEchoSources(fixture);
            // The acquired selection renames the file found at the old path over the saved one; its attempt finds the
            // next replacement open, so the wait's subject names only that file elsewhere.
            var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Renamed, Item, Item + ".tmp",
                ElsewhereId, LocalEntry(Item, ElsewhereId), false, false));
            var epoch = fixture.Inner.ReadSource(PairId, Side.Local)!.Value.Epoch;
            fixture.Inner.RecordInUseFailure(PairId, Generation, epoch, Item,
                new IssueSubject([new IssueItem(Side.Local, Item + ".tmp", ElsewhereId)], null),
                TextKey.FailLocalInUse, selection, ItemRetryDispatch.OrdinaryItem, null, null, attempt);
            var replaced = fixture.Inner.ReadReplacedItemRetry(PairId, Generation, Item, LocalId);
            Check.Require(replaced.Count == 1 && replaced[0].Token.Subject.OnlyLocalElsewhere(Item).Length == 1,
                "L99-b (B): a wait recording only a Local file elsewhere is a replacement candidate " +
                $"(real={real}); candidates {replaced.Count}");
            // The wait is keyed at Item, so a read at an unrelated path finds no episode member and no candidate.
            var unrelated = Item + "-unrelated";
            var elsewhere = fixture.Inner.ReadReplacedItemRetry(PairId, Generation, unrelated, LocalId);
            Check.Require(elsewhere.Count == 0,
                "L99-b (B): a wait keyed elsewhere names no candidate for an unrelated path " +
                $"(real={real}); candidates {elsewhere.Count}");
            // F1: an older member records the old file at Item, while the current episode records a file only at
            // Item+".tmp". Combined items put the old id at Item, so neither predicate matches: no candidate.
            const string OldId = "old-id";
            const string NewId = "new-id";
            var oldAttempt = Guid.NewGuid();
            using (var history = new Fixture(realStore: real, registeredAttempt: oldAttempt))
            {
                TrustEchoSources(history);
                var oldEpoch = history.Inner.ReadSource(PairId, Side.Local)!.Value.Epoch;
                var oldSelection = Seed(history, new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null,
                    OldId, LocalEntry(Item, OldId), false, false));
                history.Inner.RecordInUseFailure(PairId, Generation, oldEpoch, Item,
                    new IssueSubject([new IssueItem(Side.Local, Item, OldId)], null),
                    TextKey.FailLocalInUse, oldSelection, ItemRetryDispatch.OrdinaryItem, null, null, oldAttempt);
                // The replacement arrives: its preparation retires the old wait, then reports a wait recorded only
                // elsewhere. The old member at Item is retained, as StateStore keeps it.
                var replacing = Seed(history, new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null,
                    NewId, LocalEntry(Item, NewId), false, false), page: 2);
                history.Inner.RetireReplacedItemRetry(
                    history.Inner.ReadReplacedItemRetry(PairId, Generation, Item, NewId).Single(), Item, NewId);
                history.Inner.RecordInUseFailure(PairId, Generation, oldEpoch, Item,
                    new IssueSubject([new IssueItem(Side.Local, Item + ".tmp", NewId)], null),
                    TextKey.FailLocalInUse, replacing, ItemRetryDispatch.OrdinaryItem, null, null, oldAttempt);
                var retained = history.Inner.ReadReplacedItemRetry(PairId, Generation, Item, OldId);
                Check.Require(retained.Count == 0,
                    "L99-b (F1): an old member at the path keeps the episode from matching the old id " +
                    $"(real={real}); candidates {retained.Count}");
            }
            // F1 (attach path): unfinished Local work at Item is attached to an ordinary Failure whose subject names
            // (Item, AttachId). A later elsewhere-only in-use episode on the same issue keeps that attached member.
            // SQLite reads the attached member's recorded (Item, AttachId), so neither predicate matches the id.
            const string AttachId = "attached-id";
            var attachAttempt = Guid.NewGuid();
            using (var attached = new Fixture(realStore: real, registeredAttempt: attachAttempt))
            {
                TrustEchoSources(attached);
                var attachEpoch = attached.Inner.ReadSource(PairId, Side.Local)!.Value.Epoch;
                attached.Inner.Commit(PairId, StateChange.Empty with
                {
                    Raise = [(IssueKind.Failure, TextKey.FailChanged, Item,
                        new IssueSubject([new IssueItem(Side.Local, Item, AttachId)], null))]
                });
                var failure = attached.Inner.Issues(PairId).Single(issue => issue.Kind == IssueKind.Failure);
                var owner = Seed(attached, new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null,
                    AttachId, LocalEntry(Item, AttachId), false, false));
                Check.Require(attached.Inner.AttachIssueWork(owner, failure,
                    [(new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null, AttachId,
                        LocalEntry(Item, AttachId), false, false), false, null)]),
                    $"L99-b (F1 attach): the unfinished work attaches to the Failure (real={real})");
                // A later in-use Failure at Item on another acquired selection names only (Item+".tmp", NewId).
                var inuse = Seed(attached, new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null,
                    NewId, LocalEntry(Item, NewId), false, false), page: 2);
                attached.Inner.RecordInUseFailure(PairId, Generation, attachEpoch, Item,
                    new IssueSubject([new IssueItem(Side.Local, Item + ".tmp", NewId)], null),
                    TextKey.FailLocalInUse, inuse, ItemRetryDispatch.OrdinaryItem, null, null, attachAttempt);
                var kept = attached.Inner.ReadReplacedItemRetry(PairId, Generation, Item, AttachId);
                Check.Require(kept.Count == 0,
                    "L99-b (F1 attach): an attached member at the path keeps the episode from matching its id " +
                    $"(real={real}); candidates {kept.Count}");
            }
        }
    }

    // L99-b (A): a selection whose last member removes its identity takes no destination, so an occupied target path
    // no longer refuses it; every other admission check still runs. Both stores must answer alike.
    internal static void L99FinalIdentityAdmission()
    {
        foreach (var real in new[] { false, true })
        {
            using var fixture = new Fixture(realStore: real);
            TrustEchoSources(fixture);
            // The selection's identity is saved at the old path; another file now holds the target path.
            fixture.Inner.Commit(PairId, StateChange.Empty with
            {
                Record =
                [
                    SyncedEntries.From(LocalEntry(OldPath, LocalId), RemoteEntry(OldPath, DropboxId)),
                    SyncedEntries.From(LocalEntry(DestinationPath, "occupant-local"),
                        RemoteEntry(DestinationPath, "occupant-dropbox")),
                ]
            });
            var selected = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Deleted, DestinationPath, null,
                LocalId, null, false, true));
            ClearObligations(fixture);
            Check.Require(fixture.Inner.BeginSelection(selected) is not null,
                $"L99-b (A): the identity-deleted selection begins (real={real})");
            var coverage = fixture.Inner.CaptureLiveCoverage(selected, Coverage(selected).Local, Coverage(selected).Dropbox);
            LiveAdmissionStaleException? refused = null;
            try
            {
                fixture.Inner.ValidateLiveCoverage(selected, coverage,
                    [new LiveEffectScope(Side.Local, Path.Combine(fixture.Pair.LocalRoot!, DestinationPath), false)]);
            }
            catch (LiveAdmissionStaleException error) { refused = error; }
            Check.Require(refused is null,
                "L99-b (A): a final-identity-deletion selection takes no occupied destination " +
                $"(real={real}); refused {refused?.Reason}");
        }
    }
}

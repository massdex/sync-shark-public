using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

public static partial class ChangeRunnerChecks
{
    private static async Task SplitMoveOpportunityAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var (context, row) = SplitCleanupContext(fixture);
        var source = row.Dropbox!;
        var destination = "Destination";
        var local = FolderEntry(destination, row.Synced!.LocalId);
        fixture.Local.Seed(local, null);
        for (var index = 0; index < 2; index++)
        {
            var remote = RemoteEntry(PathNames.Child(source.Path, "File" + index), "remote-" + index);
            var savedLocal = LocalEntry(remote.Path, "local-" + index);
            fixture.Dropbox.Seed(remote, [1]);
            fixture.Local.Seed(savedLocal with { Path = PathNames.Child(destination, "File" + index) }, [1]);
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(savedLocal, remote)] });
        }
        fixture.Dropbox.MoveEntryLimit = 1;
        var root = new FolderMove(source.Id, source.Id, null, source.Path, destination, null,
            FolderMovePhase.Armed, null, null, 0);
        fixture.Inner.SetFolderMove(PairId, Generation, null, root);
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailMoveInParts, source.Path,
                new IssueSubject([new(Side.Local, destination, local.Id), new(Side.Dropbox, source.Path, source.Id)], Side.Dropbox)
                { Move = new(source.Id, source.Path, destination, null, true) })]
        });
        var events = new List<RunEvent>();
        await LiveSyncer(fixture, (WorkSelection)context.Opportunity, new Boundary(_ => new Lease()), events,
            configure: live => live with { Local = context.Local, Dropbox = context.Dropbox }).SyncWorkAsync(default);
        var moved = await fixture.Dropbox.ReadOneAsync(destination, default);
        var saved = fixture.Inner.SyncedById(PairId, Side.Local, local.Id);
        Check.Require(moved is not null && moved.Id != source.Id && saved?.DropboxId == moved.Id &&
            saved.LocalPath == destination && saved.DropboxPath == destination &&
            fixture.Inner.Issues(PairId).Count == 0 && fixture.Inner.FolderMoves(PairId, source.Id).Count == 0 &&
            await fixture.Dropbox.ReadOneAsync(source.Path, default) is null,
            "one opportunity fills, atomically relabels, settles the ordinary rename and cleans the empty source");
        for (var index = 0; index < 2; index++)
            Check.Require(fixture.Dropbox.FindById("remote-" + index)?.Path == PathNames.Child(destination, "File" + index) &&
                fixture.Inner.SyncedById(PairId, Side.Dropbox, "remote-" + index)?.DropboxPath ==
                    PathNames.Child(destination, "File" + index),
                "ordinary rename settlement rebases each existing child binding without changing its identity");
    }

    private static async Task SplitMoveReceiptsAsync(string mode)
    {
        using var fixture = new Fixture(realStore: true);
        var (context, row) = SplitCleanupContext(fixture);
        var source = row.Dropbox!;
        var child = FolderEntry(PathNames.Child(source.Path, "Child"), "child-id");
        fixture.Dropbox.Seed(child, null);
        var target = "Destination";
        var childTarget = PathNames.Child(target, "Child");
        var root = new FolderMove(source.Id, source.Id, null, source.Path, target, null,
            FolderMovePhase.Armed, null, null, 0);
        fixture.Inner.SetFolderMove(PairId, Generation, null, root);
        var submitted = 0;
        var identities = 0;
        var polls = 0;
        IReadOnlyList<(string From, string To)>? launch = null;
        var stopped = false;
        var side = new ProxySide(fixture.Dropbox)
        {
            SubmitMove = async (members, attempt, ct) =>
            {
                submitted++;
                launch = members;
                var durable = fixture.Inner.FolderMoves(PairId, root.SourceId).Single();
                Check.Require(durable.Launch is { Count: 1 } && durable.Launch[0].Id == child.Id && durable.JobId is null,
                    "wire-order launch custody precedes the provider send");
                switch (mode)
                {
                    case "accepted": return new FolderSubmitResult.Accepted("move-job");
                    case "refused": return new FolderSubmitResult.KnownNoEffect(TextKey.DropboxRefused);
                    case "unknown-source": return new FolderSubmitResult.Unknown(TextKey.DropboxRefused);
                    case "unknown-absent":
                        await fixture.Dropbox.DeleteAsync(child, null, ct);
                        return new FolderSubmitResult.Unknown(TextKey.DropboxRefused);
                    case "foreign-conflict":
                        fixture.Dropbox.Seed(FolderEntry(childTarget, "foreign-id"), null);
                        return new FolderSubmitResult.Terminal([new FolderMemberResult.KnownNoEffect(0, TextKey.ClashNameTaken)]);
                    default:
                        await fixture.Dropbox.SubmitMoveBatchAsync(members, attempt, ct);
                        return mode == "own-conflict"
                            ? new FolderSubmitResult.Terminal([new FolderMemberResult.KnownNoEffect(0, TextKey.ClashNameTaken)])
                            : new FolderSubmitResult.Unknown(TextKey.DropboxRefused);
                }
            },
            ReadMove = async (job, count, attempt, ct) =>
            {
                polls++;
                var durable = fixture.Inner.FolderMoves(PairId, root.SourceId).Single();
                Check.Require(job == "move-job" && count == 1 && durable.JobId == job && durable.Launch is { Count: 1 },
                    "accepted job and launch are durable before polling");
                if (!stopped) { stopped = true; throw new OperationCanceledException(); }
                var result = (FolderSubmitResult.Terminal)await fixture.Dropbox.SubmitMoveBatchAsync(launch!, attempt, ct);
                return new FolderJobResult.Terminal(result.Results);
            }
        };
        context = context with { Dropbox = IdentitySide.Truthful(side, fixture.Dropbox, () => identities++) };
        if (mode == "accepted")
        {
            try
            {
                await new FolderMoveRunner(context).FinishAsync([root], default);
                throw new Exception("poll interruption did not propagate");
            }
            catch (OperationCanceledException) { }
            root = fixture.Inner.FolderMoves(PairId, source.Id).Single();
            Check.Require(root.JobId == "move-job" && root.Launch is { Count: 1 },
                "interruption retains the accepted launch for receipt recovery, not resubmission");
        }
        ItemFailedException? failed = null;
        Entry? destination = null;
        try { destination = await new FolderMoveRunner(context).FinishAsync([root], default); }
        catch (ItemFailedException error) { failed = error; }
        var final = fixture.Inner.FolderMoves(PairId, source.Id).Single();
        Check.Require(submitted == 1 && final.Launch is null && final.JobId is null,
            "one send is accounted for and never replayed after an uncertain receipt");
        if (mode is "unknown-source" or "refused")
            Check.Require(failed?.Cause == TextKey.FailMoveInParts && final.Phase == FolderMovePhase.Filling &&
                fixture.Dropbox.FindById(child.Id)?.Path == child.Path,
                "no-effect without progress interrupts with custody, rather than spinning or replaying");
        else if (mode == "foreign-conflict")
            Check.Require(failed?.Cause == TextKey.FailChanged && fixture.Dropbox.FindById(child.Id)?.Path == child.Path &&
                fixture.Dropbox.FindById("foreign-id")?.Path == childTarget,
                "a foreign destination identity is not adopted as successful movement");
        else if (mode == "unknown-absent")
            Check.Require(destination?.Path == target && final.Phase == FolderMovePhase.Filled &&
                fixture.Inner.MoveResponsibility(PairId, child.Id) is { Cause: TextKey.DropboxRefused },
                "unresolved member custody is held before its launch is cleared");
        else
            Check.Require(failed is null && destination?.Path == target && final.Phase == FolderMovePhase.Filled &&
                fixture.Dropbox.FindById(child.Id)?.Path == childTarget,
                "own conflict and by-ID proof complete the same member movement");
        Check.Require(mode == "accepted" ? polls == 2 && identities == 0 :
            identities == (mode.StartsWith("unknown-", StringComparison.Ordinal) ? 1 : 0),
            "accepted jobs poll across interruption; only uncertain members require by-ID reads");
    }
}

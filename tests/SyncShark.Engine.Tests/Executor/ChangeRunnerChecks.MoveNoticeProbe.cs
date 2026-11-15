using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

public static partial class ChangeRunnerChecks
{
    public static void MoveNoticeBaseProbe() => MoveNoticeBaseProbeAsync().GetAwaiter().GetResult();

    private static async Task MoveNoticeBaseProbeAsync()
    {
        foreach (var real in new[] { false, true })
        foreach (var scenario in new[] { "same-key", "clash" })
        {
            using var fixture = new Fixture(realStore: real);
            var bindings = new List<SyncedEntry>();
            for (var index = 0; index <= 3; index++)
            {
                var suffix = index == 0 ? "" : "/Child" + index;
                var local = index == 0 ? FolderEntry("Source", LocalId) : LocalEntry("Source" + suffix, LocalId + index);
                var remote = index == 0 ? FolderEntry("Source", DropboxId) : RemoteEntry("Source" + suffix, DropboxId + index);
                bindings.Add(SyncedEntries.From(local, remote));
                fixture.Local.Seed(scenario == "clash" ? local : local with { Path = "Destination" + suffix }, index == 0 ? null : [1]);
                fixture.Dropbox.Seed(remote with { Path = "Destination" + suffix }, index == 0 ? null : [1]);
            }
            if (scenario == "clash") fixture.Local.Seed(FolderEntry("Destination", OccupantId), null);
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = bindings });
            var selection = SeedRemote(fixture, fixture.Dropbox.FindById(DropboxId)!);
            Check.Require(fixture.Inner.CompleteWork(selection, null), "The probe retires its setup-only anchor.");
            SourceFixture.AcquireJournal(fixture.Inner, fixture.Pair);
            ClearObligations(fixture);
            var move = new IssueSubject.UnknownMove(DropboxId, "Source", "Destination", null, true);
            var subject = new IssueSubject([new IssueItem(Side.Local, scenario == "clash" ? "Source" : "Destination", LocalId),
                new IssueItem(Side.Dropbox, "Source", DropboxId)], Side.Dropbox) { Move = move };
            fixture.Inner.Commit(PairId, StateChange.Empty with
                { Raise = [(IssueKind.Failure, TextKey.DropboxRefused, "Source", subject)] });
            var issue = fixture.Inner.Issues(PairId).Single();
            var byId = 0;
            fixture.DropboxView = new IdentitySide(fixture.Dropbox, (id, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                byId++;
                var entry = fixture.Dropbox.FindById(id)!;
                return Task.FromResult<(IdentityLocation Location, bool InScope)?>(
                    (new IdentityLocation("1", entry.Path, entry), true));
            });
            var boundary = new Boundary(_ => new Lease())
                { Capture = (chosen, _, _) => Task.FromResult(Coverage(chosen)) };
            var context = fixture.Context(selection);
            var ports = new SyncPorts(new RecoveryBoundary(), boundary, new AdmittedSelection(), 0);
            var syncer = new Syncer(context, ports);
            Issue? replacement = null;
            fixture.Hooks.BeforeCallWith = (method, arguments) =>
            {
                if (scenario != "same-key" || replacement is not null || method.Name != nameof(IStateStore.Commit) ||
                    !arguments.OfType<StateChange>().Any(change => change.Close.Any(close => close.Path == issue.Path))) return;
                fixture.Inner.Commit(PairId, StateChange.Empty with
                    { Raise = [(issue.Kind, issue.Cause, "SOURCE", subject)] });
                replacement = fixture.Inner.Issues(PairId).Single(item => item.Id != issue.Id);
                Check.Require(fixture.Inner.IssueById(PairId, issue.Id)?.Matches(issue) == true,
                    "B is raised while A remains exact and open.");
            };
            await syncer.ResolveAsync(issue, ResolutionChoice.Retry, null, null, default);
            Console.WriteLine("BASE MOVE PROBE real=" + real + " scenario=" + scenario + " after-A byId=" + byId +
                " B-open=" + (replacement is not null && fixture.Inner.IssueById(PairId, replacement.Id) is not null) +
                " issues=" + string.Join(";", fixture.Inner.Issues(PairId).Select(item => item.Kind + " " + item.Path)) +
                " bindings=" + string.Join(";", bindings.Select(saved => fixture.Inner.SyncedById(PairId, Side.Dropbox, saved.DropboxId)?.DropboxPath)));
            if (scenario == "same-key")
            {
                Check.Require(replacement is not null, "The exact overlapping-holder race fired.");
                if (fixture.Inner.IssueById(PairId, replacement!.Id) is not null)
                    await new Syncer(context, ports).ResolveAsync(replacement, ResolutionChoice.Retry, null, null, default);
                Console.WriteLine("BASE MOVE PROBE after-B byId=" + byId + " issues=" +
                    string.Join(";", fixture.Inner.Issues(PairId).Select(item => item.Kind + " " + item.Path)));
            }
            else
            {
                var clash = fixture.Inner.Issues(PairId).Single(item => item.Kind == IssueKind.NameClash);
                try
                {
                    var result = await new Syncer(context, ports).ResolveAsync(clash, ResolutionChoice.RenameIncoming, "Renamed", null, default);
                    Console.WriteLine("BASE MOVE PROBE RenameIncoming result=" + result);
                }
                catch (LiveAdmissionStaleException error)
                {
                    Console.WriteLine("BASE MOVE PROBE RenameIncoming refused=" + error.Reason + " " + error);
                }
                Check.Require(bindings.All(saved => fixture.Local.FindById(saved.LocalId) is not null &&
                    fixture.Dropbox.FindById(saved.DropboxId) is not null), "The probe loses no item.");
            }
        }
    }
}

using System.Globalization;
using System.Text;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Planner;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Own-folder acceptance cases (`--engine-checks syncer`), run as their own check entry so an
/// unrelated earlier failure in the larger SyncerChecks class cannot mask them.</summary>
public static class OwnFolderChecks
{
    private static readonly DateTimeOffset CompareAt = DateTimeOffset.UnixEpoch.AddDays(1);

    public static void Run()
    {
        SyncerChecks.OwnFolderActionCasesAsync().GetAwaiter().GetResult();
        SyncerChecks.OwnFolderRestatedFreshnessAsync().GetAwaiter().GetResult();
        UnreadableSiblingAsync().GetAwaiter().GetResult();
        RefusalVariantsAsync().GetAwaiter().GetResult();
        RenameAncestorChainAsync().GetAwaiter().GetResult();
        WithheldTransplant();
        TargetAfterFreshRead();
    }

    // A220: an unreadable sibling does not cover a healthy child; the create under a matching notice proceeds and
    // the exception adds no descendant range to the sibling's protection.
    private static async Task UnreadableSiblingAsync()
    {
        foreach (var target in Enum.GetValues<Side>())
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var box = await context.Local.EnsureFolderAsync("Box", default);
            var remoteBox = await context.Dropbox.EnsureFolderAsync("Box", default);
            Save(store, context.Pair, box, remoteBox);
            UnreadableMember.Record(store, context.Pair.PairId, "Box/Blocked");
            AcquireJournalNotice(store, context.Pair, FolderNotice("Box", box.Id, box));
            var protectedBefore = store.UnreadableUnder(context.Pair.PairId, PathNames.Key("Box"))
                .OrderBy(key => key, StringComparer.Ordinal).ToArray();
            var origin = PathNames.Other(target);
            var incoming = await PutAsync(context.For(origin), "Box/Child");
            var row = Row(origin == Side.Local ? incoming : null, origin == Side.Dropbox ? incoming : null, null);
            row = row with { Context = row.Context with { Destination = "Box/Child" } };
            var action = new SyncAction(SyncAction.CopyOn(false, target), row, Target: "Box/Child");
            var jobs = NewJobs(context);
            jobs.Ancestors.CoverListedWork(0);
            var keys = LockKeys.ForPaths(context.Pair, [(Side.Local, incoming.Path), (Side.Dropbox, incoming.Path)]);
            var result = await jobs.Items.ExecuteLockedAsync(action, null, keys, keys, default, null, null);
            var landed = await context.For(target).ReadOneAsync("Box/Child", default);
            Check.Require(result.Outcome?.Effect == (target == Side.Local ? EffectKind.Downloaded : EffectKind.Uploaded) &&
                landed is not null,
                "A220: the healthy child proceeds beside an unreadable sibling, target=" + target);
            var protectedAfter = store.UnreadableUnder(context.Pair.PairId, PathNames.Key("Box"))
                .OrderBy(key => key, StringComparer.Ordinal).ToArray();
            Check.Require(protectedBefore.SequenceEqual(protectedAfter) &&
                !store.UnreadableTouches(context.Pair.PairId, PathNames.Key("Box/Child"), false) &&
                store.UnreadableTouches(context.Pair.PairId, PathNames.Key("Box/Blocked"), true),
                "A220: the exception adds no descendant range; the unreadable sibling stays protected, target=" + target);
        }
    }

    // A81: every refused provider or notice variant leaves the target unchanged.
    private static async Task RefusalVariantsAsync()
    {
        var variants = new (string Label, Func<Entry, ChangeNotice> Notice)[]
        {
            ("a moved-away row", box => FolderNotice("Renamed", box.Id, box with { Path = "Renamed" },
                ChangeKind.Renamed, oldPath: "Box")),
            ("a deleted row", box => FolderNotice("Box", box.Id, box, ChangeKind.Deleted)),
            ("a different identity", box => FolderNotice("Box", "other-id", box with { Id = "other-id" })),
            ("a file observation", box => FolderNotice("Box", box.Id, box with { IsFolder = false })),
            ("a skipped observation", box => FolderNotice("Box", box.Id, box with { Skip = "skipped" })),
            ("no observation", _ => FolderNotice("Box", "box-id", null)),
            ("an identity-deleted row", box => FolderNotice("Box", box.Id, box, identityDeleted: true))
        };
        foreach (var (label, make) in variants)
            await RequireRefusedAsync("A81: " + label, make);

        // A residual, obligated or issue-associated later row refuses before the target.
        await RequireRefusedAsync("A81: a residual work row", null, (store, context, box, matching) =>
        {
            var generation = store.ReadPairExecution(context.Pair.PairId)!.Value.Generation;
            var parent = store.PendingAncestors(context.Pair.PairId, generation, Side.Local,
                [PathNames.Key("Box")], 0).First();
            store.Commit(context.Pair.PairId, StateChange.Empty with
            {
                Raise = [(IssueKind.Failure, TextKey.FailChanged, "Box",
                    new IssueSubject([new IssueItem(Side.Local, "Box", box.Id)], null))]
            });
            var issue = store.Issues(context.Pair.PairId, [IssueKind.Failure]).Single();
            store.AttachIssueWork(parent, issue, [(matching, true, null)]);
        });

        // The WorkIssue clause alone: the anchor notice row gains an issue, with no residual.
        await RequireRefusedAsync("A81: an issue-associated anchor row", null, (store, context, box, matching) =>
        {
            var generation = store.ReadPairExecution(context.Pair.PairId)!.Value.Generation;
            var source = store.ReadSource(context.Pair.PairId, Side.Local)!.Value;
            var parent = store.PendingAncestors(context.Pair.PairId, generation, Side.Local,
                [PathNames.Key("Box")], 0).First();
            store.RecordInUseFailure(context.Pair.PairId, generation, source.Epoch, "Box",
                new IssueSubject([new IssueItem(Side.Local, "Box", box.Id)], null), TextKey.FailLocalInUse,
                parent, ItemRetryDispatch.OrdinaryItem, null, null, Guid.NewGuid());
        });

        // The target appears before the fresh read: the exception does not fire; a file and an adopted folder both refuse.
        foreach (var folder in new[] { false, true })
        {
            using var store = new InMemoryStateStore();
            var context = Context(store);
            var box = await context.Local.EnsureFolderAsync("Box", default);
            var remoteBox = await context.Dropbox.EnsureFolderAsync("Box", default);
            Save(store, context.Pair, box, remoteBox);
            AcquireJournalNotice(store, context.Pair, FolderNotice("Box", box.Id, box));
            var source = folder
                ? await context.Local.EnsureFolderAsync("Box/Child", default)
                : await PutAsync(context.Local, "Box/Child");
            var target = folder
                ? await context.Dropbox.EnsureFolderAsync("Box/Child", default)
                : await PutAsync(context.Dropbox, "Box/Child");
            var row = Row(source, null, null) with { Context = Facts() with { Destination = "Box/Child" } };
            var action = new SyncAction(folder ? ActionKind.EnsureDropboxFolder : ActionKind.Upload, row,
                Target: "Box/Child");
            var jobs = NewJobs(context);
            jobs.Ancestors.CoverListedWork(0);
            var keys = LockKeys.ForPaths(context.Pair, [(Side.Local, "Box/Child"), (Side.Dropbox, "Box/Child")]);
            var result = await jobs.Items.ExecuteLockedAsync(action, null, keys, keys, default, null, null);
            var after = await context.Dropbox.ReadOneAsync("Box/Child", default);
            Check.Require(result.Outcome?.Raised == IssueKind.Failure &&
                store.Issues(context.Pair.PairId).Single(issue => issue.Path == "Box/Child").Cause == TextKey.FailChanged &&
                after == target,
                "A81: a target present before the fresh read refuses the create and stays unchanged, folder=" + folder);
        }

        // A failed target-side freshness read is the row's own Failure, not the exception.
        using (var store = new InMemoryStateStore())
        {
            var context = Context(store);
            var box = await context.Local.EnsureFolderAsync("Box", default);
            var remoteBox = await context.Dropbox.EnsureFolderAsync("Box", default);
            Save(store, context.Pair, box, remoteBox);
            AcquireJournalNotice(store, context.Pair, FolderNotice("Box", box.Id, box));
            var source = await PutAsync(context.Local, "Box/Child");
            ((FakeSide)context.Dropbox).FailNext("Box/Child",
                new ItemFailedException("Box/Child", TextKey.ReadDropboxFailed, null));
            var row = Row(source, null, null) with { Context = Facts() with { Destination = "Box/Child" } };
            var jobs = NewJobs(context);
            jobs.Ancestors.CoverListedWork(0);
            var outcome = await jobs.Items.RunRowAttemptAsync(row, default);
            Check.Require(outcome.Raised == IssueKind.Failure &&
                store.Issues(context.Pair.PairId).Single(issue => issue.Path == "Box/Child").Cause == TextKey.ReadDropboxFailed &&
                await context.Dropbox.ReadOneAsync("Box/Child", default) is null,
                "A81: a failed fresh read is the row's Failure; the create exception never fires");
        }

        // Cancellation before the fresh read propagates and writes nothing.
        using (var store = new InMemoryStateStore())
        {
            var context = Context(store);
            var box = await context.Local.EnsureFolderAsync("Box", default);
            var remoteBox = await context.Dropbox.EnsureFolderAsync("Box", default);
            Save(store, context.Pair, box, remoteBox);
            AcquireJournalNotice(store, context.Pair, FolderNotice("Box", box.Id, box));
            var source = await PutAsync(context.Local, "Box/Child");
            var row = Row(source, null, null) with { Context = Facts() with { Destination = "Box/Child" } };
            var action = new SyncAction(ActionKind.Upload, row, Target: "Box/Child");
            var jobs = NewJobs(context);
            jobs.Ancestors.CoverListedWork(0);
            var keys = LockKeys.ForPaths(context.Pair, [(Side.Local, "Box/Child"), (Side.Dropbox, "Box/Child")]);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancelled = false;
            try
            {
                await jobs.Items.ExecuteLockedAsync(action, null, keys, keys, cancellation.Token, null, null);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            Check.Require(cancelled && await context.Dropbox.ReadOneAsync("Box/Child", default) is null,
                "A81: cancellation before the effect writes nothing");
        }
    }

    private static async Task RequireRefusedAsync(string what, Func<Entry, ChangeNotice>? make,
        Action<IStateStore, ExecutorContext, Entry, ChangeNotice>? arrange = null)
    {
        using var store = new InMemoryStateStore();
        var context = Context(store);
        var box = await context.Local.EnsureFolderAsync("Box", default);
        var remoteBox = await context.Dropbox.EnsureFolderAsync("Box", default);
        Save(store, context.Pair, box, remoteBox);
        var matching = FolderNotice("Box", box.Id, box);
        AcquireJournalNotice(store, context.Pair, make?.Invoke(box) ?? matching);
        arrange?.Invoke(store, context, box, matching);
        var source = await PutAsync(context.Local, "Box/Child");
        var row = Row(source, null, null) with { Context = Facts() with { Destination = "Box/Child" } };
        var action = new SyncAction(ActionKind.Upload, row, Target: "Box/Child");
        var jobs = NewJobs(context);
        jobs.Ancestors.CoverListedWork(0);
        var keys = LockKeys.ForPaths(context.Pair, [(Side.Local, "Box/Child"), (Side.Dropbox, "Box/Child")]);
        var result = await jobs.Items.ExecuteLockedAsync(action, null, keys, keys, default, null, null);
        Check.Require(result.Outcome?.Raised == IssueKind.Failure &&
            await context.Dropbox.ReadOneAsync("Box/Child", default) is null,
            what + " refuses the create and writes nothing");
    }

    // A79: the Rename producer chain. Rename through Parents creates and records the destination ancestor; a later
    // notice at that binding then lets a new child create through the own-folder exception.
    private static async Task RenameAncestorChainAsync()
    {
        using var store = new InMemoryStateStore();
        var context = Context(store);
        var localSource = await context.Local.EnsureFolderAsync("src", default);
        var dropboxSource = await context.Dropbox.EnsureFolderAsync("src", default);
        var saved = Save(store, context.Pair, localSource, dropboxSource);
        await context.Dropbox.EnsureFolderAsync("new", default);
        var moved = await context.Dropbox.MoveAsync(dropboxSource, "new/nested", default);
        var jobs = NewJobs(context);
        var decision = await jobs.Items.RenameAsync([new RenameCandidate(Side.Dropbox, moved, saved)], null, default);
        Check.Require(decision.Kind == RenameKind.Follow,
            "A79: the Dropbox folder rename follows to Local through the shared Parents");
        var createdAncestor = store.SyncedByKey(context.Pair.PairId, Side.Local, PathNames.Key("new"));
        var createdEntry = await context.Local.ReadOneAsync("new", default);
        Check.Require(createdAncestor is { IsFolder: true } && createdEntry is { IsFolder: true },
            "A79: Rename's Parents call creates and records the Local destination ancestor");
        AcquireJournalNotice(store, context.Pair, FolderNotice("new", createdEntry!.Id, createdEntry));
        var source = await PutAsync(context.Dropbox, "new/file.txt");
        var row = Row(null, source, null) with { Context = Facts() with { Destination = "new/file.txt" } };
        var action = new SyncAction(ActionKind.Download, row, Target: "new/file.txt");
        jobs.Ancestors.CoverListedWork(0);
        var keys = LockKeys.ForPaths(context.Pair, [(Side.Local, "new/file.txt"), (Side.Dropbox, "new/file.txt")]);
        var result = await jobs.Items.ExecuteLockedAsync(action, null, keys, keys, default, null, null);
        var landed = await context.Local.ReadOneAsync("new/file.txt", default);
        Check.Require(result.Outcome?.Effect == EffectKind.Downloaded &&
            landed is not null,
            "A79: the new child proceeds under the rename-produced ancestor binding and downloads");
    }

    // A221: a folder transplanted after its matching notice is caught up, with its swap notice withheld, still takes
    // the new child; the saved folder binding keeps the old identity (owner decision one, documented).
    private static void WithheldTransplant()
    {
        ScenarioCase("own-withheld-transplant-local", scenario =>
        {
            scenario.DropboxFile("made/a.txt", "a");
            scenario.AcquireJournalAfterFolder(afterNotice: () =>
            {
                scenario.RemoveLocal("made");
                scenario.LocalFolder("made");
            });
            var summary = scenario.RepairAsync(requireCompleted: false);
            scenario.Require(summary.Completed, "A221: a withheld local transplant still lets the new child in");
            var bytes = scenario.LocalFiles().GetValueOrDefault("made/a.txt");
            scenario.Require(bytes is not null && bytes.SequenceEqual(Encoding.UTF8.GetBytes("a")),
                "A221: the child lands in the replaced local folder");
            var saved = scenario.Store.SyncedByKey(scenario.Pair.PairId, Side.Local, PathNames.Key("made"));
            var now = scenario.Local.ReadOneAsync("made", Scenario.Cancellation).GetAwaiter().GetResult();
            scenario.Require(saved is not null && now is not null && saved.LocalId != now.Id,
                "A221: the saved binding still names the replaced local folder identity");
            scenario.Require(scenario.Store.SyncedByKey(scenario.Pair.PairId, Side.Local,
                PathNames.Key("made/a.txt")) is not null, "A221: the new child is recorded");
        });

        ScenarioCase("own-withheld-transplant-dropbox", scenario =>
        {
            scenario.RepairAsync();
            scenario.LocalFile("made/a.txt", "a");
            scenario.AcquireFeedAfterFolder(afterNotice: () =>
            {
                scenario.RemoveDropbox("made");
                scenario.DropboxFolder("made");
            });
            var summary = scenario.RepairAsync(requireCompleted: false);
            scenario.Require(summary.Completed, "A221: a withheld Dropbox transplant still lets the new child in");
            scenario.Require(scenario.DropboxFiles().ContainsKey("made/a.txt"),
                "A221: the child lands in the replaced Dropbox folder");
            var saved = scenario.Store.SyncedByKey(scenario.Pair.PairId, Side.Dropbox, PathNames.DropboxKey("made"));
            var now = scenario.Dropbox.ReadOneAsync("made", Scenario.Cancellation).GetAwaiter().GetResult();
            scenario.Require(saved is not null && now is not null && saved.DropboxId != now.Id,
                "A221: the saved binding still names the replaced Dropbox folder identity");
        });
    }

    // A238: a target folder that appears after the fresh read is adopted and recorded with its content untouched;
    // a target file that appears there remains a clash.
    private static void TargetAfterFreshRead()
    {
        ScenarioCase("own-target-folder-after-fresh-read", scenario =>
        {
            scenario.RepairAsync();
            scenario.LocalFile("made/a.txt", "a");
            scenario.LocalFolder("made/sub");
            scenario.AcquireFeedAfterFolder();
            scenario.AfterFreshRead(Side.Dropbox, "made/sub", () =>
            {
                scenario.DropboxFolder("made/sub");
                scenario.DropboxFile("made/sub/kept.txt", "kept");
            });
            var summary = scenario.RepairAsync(requireCompleted: false);
            scenario.Require(summary.Completed, "A238: an adopted target folder completes the Repair");
            scenario.RequireTree(["made/", "made/a.txt=a", "made/sub/"],
                ["made/", "made/a.txt=a", "made/sub/", "made/sub/kept.txt=kept"]);
            scenario.Require(scenario.Store.SyncedByKey(scenario.Pair.PairId, Side.Dropbox,
                PathNames.DropboxKey("made/sub")) is { IsFolder: true },
                "A238: the adopted folder is recorded");
            var kept = scenario.DropboxFiles().GetValueOrDefault("made/sub/kept.txt");
            scenario.Require(kept is not null && kept.SequenceEqual(Encoding.UTF8.GetBytes("kept")),
                "A238: the adopted folder's existing content is unmodified");
        });

        ScenarioCase("own-target-file-after-fresh-read", scenario =>
        {
            scenario.RepairAsync();
            scenario.LocalFile("made/a.txt", "a");
            scenario.LocalFolder("made/sub");
            scenario.AcquireFeedAfterFolder();
            scenario.AfterFreshRead(Side.Dropbox, "made/sub", () => scenario.DropboxFile("made/sub", "late"));
            scenario.RepairAsync(requireCompleted: false);
            var issues = scenario.Store.Issues(scenario.Pair.PairId)
                .Select(issue => issue.Kind + ":" + issue.Cause + "@" + issue.Path).ToArray();
            scenario.Require(scenario.Store.Issues(scenario.Pair.PairId).Any(issue =>
                    issue.Path == "made/sub" && issue.Kind == IssueKind.NameClash),
                "A238: a target file appearing after the fresh read remains a clash (issues: [" +
                string.Join(",", issues) + "])");
            var late = scenario.DropboxFiles().GetValueOrDefault("made/sub");
            scenario.Require(late is not null && late.SequenceEqual(Encoding.UTF8.GetBytes("late")),
                "A238: the late target file is not overwritten");
        });
    }

    private static void ScenarioCase(string name, Action<Scenario> body)
    {
        Scenario? scenario = null;
        try
        {
            scenario = Scenario.Begin(name);
            body(scenario);
            scenario.RequireOnlyEngineRoutes();
        }
        catch (Exception error)
        {
            scenario?.Fail(error);
            throw;
        }
        finally
        {
            scenario?.Dispose();
        }
    }

    private static ExecutorContext Context(IStateStore store)
    {
        var pair = SourceFixture.Bound(new PairConfig("own-pair", "Own Pair", @"C:\OwnFolderFixture",
            "/OwnFolderFixture", "connection-a", [], [], FirstRunPolicy.NewestWins, true, null));
        store.SavePair(pair);
        SourceFixture.AcquireJournal(store, pair);
        var settings = new EngineSettings("{name} ({side} {time}){ext}", "yyyyMMddHHmmss", "Local", "Dropbox",
            ".case", RecycleFallback.Fail, 1, FirstRunPolicy.NewestWins, 1, "", 262144, ScanTestSettings.BatchRows);
        var local = new FakeSide(Side.Local);
        return new ExecutorContext(local, new FakeSide(Side.Dropbox), new FakeRecovery(), store, pair,
            new PathScope(pair, [pair]), settings, new ReplaceTurn(), new FakeAdmission { Level = 1 }, false, _ => { },
            ReserveDownloadAsync: (_, _, _, _) => ValueTask.FromResult<IAsyncDisposable>(NoopDisposable.Instance),
            Time: TimeProvider.System,
            ReadDeletionEvidenceAsync: (path, _, _) => Task.FromResult<DeletionEvidence>(
                new DeletionEvidence.Unknown(path, TextKey.ReadLocalFailed)),
            BeginDownloadAttempt: operation => new RequestAttempt(operation, AttemptOwner.Transfer),
            CompleteDownloadAttemptAsync: (_, _) => Task.FromResult(true),
            UploadAdmissionIdentity: ("account-a", "route-a", "source-a"),
            ItemAttempts: new NoAttempts(), Opportunity: new object(), ExpectedGeneration: 0);
    }

    private static (ItemSync Items, FirstRunFolders FirstRunFolders, AncestorFence Ancestors, Recovery Recovery,
        ExclusionRunner Exclusions, KeepBothRunner KeepBoth, ActionExecutor Actions) NewJobs(ExecutorContext context) =>
        SyncJobs.Create(context, new SyncPorts(RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance,
            RefusingExecutionBoundary.Instance, 0));

    private static Row Row(Entry? local, Entry? dropbox, SyncedEntry? saved) =>
        new(local is not null ? PathNames.Key(local.Path) : dropbox is not null ? PathNames.DropboxKey(dropbox.Path) : saved!.LocalKey,
            local, dropbox, saved, [], null, Facts());

    private static RowContext Facts() => new(false, false, false, null, false, false, true, false);

    private static async Task<Entry> PutAsync(ISide side, string path)
    {
        var source = new Entry(path, false, 1, DateTimeOffset.UnixEpoch, path, "", "", null);
        using var content = new MemoryStream([1]);
        await using var stage = await side.StageAsync(path, content, source, CancellationToken.None);
        return (await side.PublishAsync(stage, null, null, CancellationToken.None)).Final!;
    }

    private static SyncedEntry Save(IStateStore store, PairConfig pair, Entry local, Entry dropbox)
    {
        var saved = SyncedEntries.From(local, dropbox);
        store.Commit(pair.PairId, StateChange.Empty with { Record = [saved] });
        return saved;
    }

    private static ChangeNotice FolderNotice(string path, string id, Entry? observed,
        ChangeKind kind = ChangeKind.Changed, string? oldPath = null, bool identityDeleted = false) =>
        new(Side.Local, kind, path, oldPath, id, observed, false, identityDeleted);

    private static void AcquireJournalNotice(IStateStore store, PairConfig pair, ChangeNotice notice)
    {
        var generation = store.ReadPairExecution(pair.PairId)!.Value.Generation;
        var source = store.ReadSource(pair.PairId, Side.Local)!.Value;
        var volume = pair.Roots!.Local.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture);
        var position = store.ReadJournal(volume);
        store.AcquireJournalPage(volume, position, position!,
            [(pair.PairId, generation, source.Epoch, source.Revision, false)],
            [(pair.PairId, notice)]);
    }

    private sealed class NoopDisposable : IAsyncDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public ValueTask DisposeAsync() => default;
    }

    private sealed class NoAttempts : IItemAttempts
    {
        public Task<T> ExecuteAsync<T>(object opportunity, ItemRetryCandidate? expected, IssueSubject subject,
            Func<Guid, CancellationToken, Task<T>> execute, CancellationToken ct) => execute(Guid.NewGuid(), ct);

        public ItemRetryCandidate ReportFailure(Guid attemptId, long sourceEpoch, string path, IssueSubject subject,
            TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch, PendingEffect? expectedPending,
            ItemRetryToken? expectedEpisode) => throw new NotSupportedException();

        public void Complete(Guid attemptId, ItemRetryCandidate expected, StateChange change,
            PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null) { }
    }
}

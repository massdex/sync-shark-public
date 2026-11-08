using System.Runtime.CompilerServices;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Checks for RowContexts (`--engine-checks repair`).</summary>
public static class RowContextsChecks
{
    private static readonly DateTimeOffset Stamp = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    public static void Run()
    {
        var scene = Scene.Full();

        CarriesRunFacts(scene);
        SharesOrdinaryContext(scene);
        MarksOutOfScope(scene);
        MarksSkipped(scene);
        MarksSkippedJoinKeyPrefixes(scene);
        ReusesExactProof(scene);
        UnmatchedProofsAllocateNothing(scene);
        MarksHeld(scene);
        RetryRefusalHoldsChildren().GetAwaiter().GetResult();
        MarksStoredHoldKinds(scene);
        HoldsDependentRows(scene);
        FindsDeepHeldAncestor(scene);
        PrefersMostSpecificHold(scene);
        MarksFolderDeletes(scene);
        MarksNestedRoots(scene);
        SelectsDeletionEvidence(scene);
        RetainedProjection(scene);
        ComparisonProjection(scene);
        AttemptCompletion();
        SelectsSideSets(scene);
        SideSelectorsAllocateNothing(scene);
        OrdinaryPathAllocatesNothing();
    }

    // The pair and scope every case runs against; each context gets its own store, seeded as the case needs.
    private sealed class Scene
    {
        private Scene(PairConfig pair, IReadOnlyList<PairConfig> allPairs)
        {
            Pair = pair;
            Scope = new PathScope(pair, allPairs);
        }

        public PairConfig Pair { get; }

        public PathScope Scope { get; }

        public static Scene Full()
        {
            var pair = new PairConfig("pair", "pair",
                Path.Combine(Path.GetTempPath(), nameof(RowContextsChecks), "local"), "dropbox", "connection",
                ["FileExtension:.tmp"],
                [new Exclusion(Side.Local, "Excluded", true), new Exclusion(Side.Dropbox, "Hidden", false)],
                FirstRunPolicy.NewestWins, true, null);
            var inner = pair with
            {
                PairId = "inner", Name = "inner",
                LocalRoot = Path.Combine(pair.LocalRoot, "inner"), DropboxRoot = "dropbox/inner"
            };
            return new Scene(pair, [pair, inner]);
        }

        public static Scene Bare()
        {
            var pair = new PairConfig("bare", "bare",
                Path.Combine(Path.GetTempPath(), nameof(RowContextsChecks), "bare-local"),
                "bare-dropbox", "connection", [], [], FirstRunPolicy.NewestWins, true, null);
            return new Scene(pair, [pair]);
        }

        public InMemoryStateStore Store()
        {
            var store = new InMemoryStateStore();
            store.SavePair(Pair);
            return store;
        }

        public RowContexts Contexts(bool firstRun, bool completeInventory, IStateStore? store = null,
            IReadOnlySet<long>? retryIssues = null, IReadOnlyDictionary<string, IssueSubject>? subjects = null,
            Func<string, bool>? deletedName = null)
        {
            store ??= Store();
            var settings = new EngineSettings("", "", "", "", ".case", RecycleFallback.Fail, 1,
                FirstRunPolicy.NewestWins, 1, "", 1024, ScanTestSettings.BatchRows);
            var context = new ExecutorContext(new FakeSide(Side.Local), new FakeSide(Side.Dropbox),
                new FakeRecovery(), store, Pair, Scope, settings, new ReplaceTurn(), new FakeAdmission(),
                false, _ => { }, RefuseReserve, TimeProvider.System, RefuseEvidence, RefuseBegin, RefuseComplete,
                ("", "", ""), new RefusingItemAttempts(), new object(), 0)
            { ScanId = ScanFixture.TokenOrNone(store, Pair.PairId) };
            return new RowContexts(context, firstRun, completeInventory, retryIssues, subjects, deletedName);
        }
    }

    private static ValueTask<IAsyncDisposable> RefuseReserve(string account, string route, long length,
        CancellationToken ct) => throw new InvalidOperationException("RowContexts reserve no download.");

    private static Task<DeletionEvidence> RefuseEvidence(string path, bool isFolder, CancellationToken ct) =>
        throw new InvalidOperationException("RowContexts read no deletion evidence.");

    private static RequestAttempt RefuseBegin(object operation) =>
        throw new InvalidOperationException("RowContexts begin no download attempt.");

    private static Task<bool> RefuseComplete(RequestAttempt attempt, CancellationToken ct) =>
        throw new InvalidOperationException("RowContexts complete no download attempt.");

    private sealed class RefusingItemAttempts : IItemAttempts
    {
        public Task<T> ExecuteAsync<T>(object opportunity, ItemRetryCandidate? expected, IssueSubject subject,
            Func<Guid, CancellationToken, Task<T>> execute, CancellationToken ct) => execute(Guid.NewGuid(), ct);

        public ItemRetryCandidate ReportFailure(Guid attemptId, long sourceEpoch, string path,
            IssueSubject subject, TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch,
            PendingEffect? expectedPending, ItemRetryToken? expectedEpisode) =>
            throw new InvalidOperationException("RowContexts report no item failure.");

        public void Complete(Guid attemptId, ItemRetryCandidate expected, StateChange change,
            PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null) =>
            throw new InvalidOperationException("RowContexts complete no item attempt.");
    }

    // A real store hold: the projection is what RowContexts reads, so the case seeds the issue it would see.
    private static void Hold(InMemoryStateStore store, Scene scene, IssueKind kind, string path,
        Side side = Side.Local)
    {
        var subject = new IssueSubject([new IssueItem(side, path, string.Empty)], null);
        store.Commit(scene.Pair.PairId, StateChange.Empty with
        {
            Raise = [(kind, TextKey.ClashNameTaken, path, subject)]
        });
    }

    // A retained first-run subject. None is not retained by the projection, which is the point of that case.
    private static void Retain(InMemoryStateStore store, Scene scene, string path,
        IssueSubject.FirstRunDeletionKind kind) =>
        store.Commit(scene.Pair.PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailChanged, path, Retained(path, kind))]
        });

    // A completed scan's covering-root identity evidence: the real durable scan lifetime, as production records it.
    private static void Scanned(InMemoryStateStore store, Scene scene, string path)
    {
        store.BeginScan(scene.Pair.PairId, 0, includeDeleted: false);
        var sink = store.OpenScan(scene.Pair.PairId, Side.Local, ScanTestSettings.BatchRows);
        sink.AddAsync(new Listed(path, Folder(path)), default).GetAwaiter().GetResult();
        sink.CompleteAsync().GetAwaiter().GetResult();
    }

    // The side selectors add no allocation over the inline ternary they replaced.
    private static void SideSelectorsAllocateNothing(Scene scene)
    {
        var contexts = scene.Contexts(false, true);
        Consume(contexts.SkippedLocal);
        var baseline = AllocProbe.Measure(i =>
        {
            Consume(i % 2 == 0 ? contexts.SkippedLocal : contexts.SkippedDropbox);
            Consume(i % 2 == 0 ? contexts.DeletingLocal : contexts.DeletingDropbox);
        }, 1_000, 0);
        var owner = AllocProbe.Measure(i =>
        {
            var side = i % 2 == 0 ? Side.Local : Side.Dropbox;
            Consume(contexts.Skipped(side));
            Consume(contexts.Deleting(side));
        }, 1_000, 0);
        Check.Require(owner == baseline && owner == 0,
            "Skipped(side) and Deleting(side) add no allocation over the inline ternary (owner=" + owner +
            " B, baseline=" + baseline + " B per 1,000 calls)");
    }

    // The ordinary row's context and folder fact add no allocation per item. A rule-free scope
    // isolates the migrated boundary; the ignore-rule matching it would run is the same in the inline baseline.
    private static void OrdinaryPathAllocatesNothing()
    {
        var scene = Scene.Bare();
        var folder = Path.Combine(Path.GetTempPath(), nameof(RowContextsChecks), "alloc-" + Guid.NewGuid().ToString("N"));
        var store = StateStore.Open(Path.Combine(folder, "state.db"));
        try
        {
            var pair = SourceFixture.Bound(scene.Pair);
            store.SavePair(pair);
            SourceFixture.AcquireJournal(store, pair);
            var contexts = scene.Contexts(false, true, store);
            var rows = new Row[64];
            for (var i = 0; i < rows.Length; i++)
            {
                var path = (i % 2 == 0 ? "" : "Folder/") + "file" + i + ".txt";
                rows[i] = Make(path, File(path));
            }
            Consume(contexts.For(rows[0]));
            var forAllocated = AllocProbe.Measure(i => Consume(contexts.For(rows[i % rows.Length])), 1_000, 0);
            var folderAllocated = AllocProbe.Measure(i => Consume(rows[i % rows.Length].IsFolder()), 1_000, 0);
            var refreshAllocated = AllocProbe.Measure(i => Consume(contexts.RefreshDeletionEligibility(rows[i % rows.Length]).Context), 1_000, 0);
            Check.Require(forAllocated == 0 && folderAllocated == 0 && refreshAllocated == 0,
                "with no held rows, ordinary projection and freshness allocate nothing (observed For=" +
                forAllocated + " B, IsFolder=" + folderAllocated + " B, Refresh=" + refreshAllocated + " B per 1,000 calls)");
        }
        finally
        {
            store.Dispose();
            Directory.Delete(folder, true);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Consume(RowContext context) => _ = context.Destination;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Consume(PrefixSet set) => _ = set;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Consume(bool value) => _ = value;

    private static void CarriesRunFacts(Scene scene)
    {
        var first = scene.Contexts(true, true).For(Make("plain.txt", File("plain.txt")));
        Check.Require(first.FirstRun && first.CompleteInventory,
            "RowContexts carries FirstRun and CompleteInventory");
        Check.Require(!first.OutOfScope && !first.Skipped && !first.Held && first.UnderFolderDelete is null &&
            !first.HoldsNestedRoot && !first.DeletionEligible && first.Destination is null,
            "a plain row has no context flags and no Destination");
        Check.Require(!scene.Contexts(false, true).For(Make("plain.txt", File("plain.txt"))).FirstRun,
            "FirstRun is false after the first run");
        Check.Require(!scene.Contexts(true, false).For(Make("plain.txt", File("plain.txt"))).CompleteInventory,
            "CompleteInventory is false while the acquisition is incomplete");
        var store = scene.Store();
        Hold(store, scene, IssueKind.Tie, "Held");
        var exceptional = scene.Contexts(true, true, store).For(Make("Held/child.txt", File("Held/child.txt")));
        Check.Require(exceptional.Held && exceptional.FirstRun && exceptional.CompleteInventory,
            "an exceptional context copies FirstRun and CompleteInventory from the neutral");
    }

    private static void SharesOrdinaryContext(Scene scene)
    {
        var contexts = scene.Contexts(true, true);
        var plain = contexts.For(Make("plain.txt", File("plain.txt")));
        Check.Require(ReferenceEquals(plain, contexts.For(Make("other.txt", File("other.txt")))),
            "RowContexts reuses one context for ordinary rows");
        var changed = plain with { Destination = "other.txt" };
        Check.Require(plain.Destination is null && changed.Destination == "other.txt" &&
            ReferenceEquals(contexts.For(Make("third.txt", File("third.txt"))), plain),
            "a with-copy leaves the shared ordinary context unchanged");
        Check.Require(!ReferenceEquals(contexts.For(Make("link.txt", File("link.txt", "SkipLink"))), plain),
            "a non-ordinary row gets its own context");
    }

    private static void MarksOutOfScope(Scene scene)
    {
        var contexts = scene.Contexts(false, true);
        (Row Row, bool Expected, string What)[] cases =
        [
            (Make("Excluded/a.txt", File("Excluded/a.txt")), true, "a local exclusion"),
            (Make("Excluded/a.txt", null, File("Excluded/a.txt")), true, "a local exclusion seen from the Dropbox path"),
            (Make("Hidden/a.txt", null, File("Hidden/a.txt")), true, "a Dropbox exclusion"),
            (Make("docs/report.tmp", File("docs/report.tmp")), true, "an ignore rule"),
            (Make("inner/file.txt", File("inner/file.txt")), true, "a nested pair's root"),
            (Make("Excluded/a.txt", synced: Synced("Excluded/a.txt")), true, "a local exclusion on a saved-only row"),
            (Make("docs/report.tmp", synced: Synced("docs/report.tmp")), true, "an ignore rule on a saved-only row"),
            (Make("inner/file.txt", synced: Synced("inner/file.txt")), true, "a nested pair's root on a saved-only row"),
            (Make("ExcludedSibling/a.txt", File("ExcludedSibling/a.txt")), false, "an exclusion ends at a segment boundary"),
            (Make("plain.txt", File("plain.txt")), false, "a plain path"),
        ];
        foreach (var (row, expected, what) in cases)
            Check.Require(contexts.For(row).OutOfScope == expected, "OutOfScope: " + what);
    }

    private static void MarksSkipped(Scene scene)
    {
        var contexts = scene.Contexts(false, true);
        contexts.SkippedLocal.Add("LocalSkip");
        contexts.SkippedDropbox.Add("DropSkip");
        (Row Row, bool Expected, string What)[] cases =
        [
            (Make("LocalSkip/a.txt", File("LocalSkip/a.txt")), true, "a skipped local folder prefix"),
            (Make("LocalSkipSibling/a.txt", File("LocalSkipSibling/a.txt")), false, "a skipped prefix ends at a segment boundary"),
            (Make("DropSkip/a.txt", null, File("DropSkip/a.txt")), true, "a skipped Dropbox folder prefix"),
            (Make("LocalSkip/a.txt", null, File("LocalSkip/a.txt")), true, "a local skipped folder on a Dropbox-only row"),
            (Make("link.txt", File("link.txt", "SkipLink")), true, "a local entry's own Skip"),
            (Make("stub.txt", null, File("stub.txt", "SkipNotDownloadable")), true, "a Dropbox entry's own Skip"),
            (Make("plain.txt", File("plain.txt")), false, "an ordinary row"),
        ];
        foreach (var (row, expected, what) in cases)
            Check.Require(contexts.For(row).Skipped == expected, "Skipped: " + what);
    }

    private static void MarksSkippedJoinKeyPrefixes(Scene scene)
    {
        var local = "LocalCopy/child.txt";
        const string remote = "Folder?/child.txt";
        var key = PathNames.Key(local);
        var saved = new SyncedEntry(key, local, PathNames.DropboxKey(remote), remote,
            false, "local-id", 0, 0, "dropbox-id", "");
        var contexts = scene.Contexts(false, true);
        contexts.SkippedLocal.Add(PathNames.Key(PathNames.Parent(local)));
        Check.Require(local != remote, "the fixture's saved binding has distinct Local and Dropbox spellings");
        Row[] protectedRows =
        [
            Make(key, File(local), File(remote), saved),
            Make(key, null, File(remote), saved),
            Make(key, synced: saved),
        ];
        foreach (var row in protectedRows)
            Check.Require(contexts.For(row).Skipped,
                "a row under a skipped Local prefix is skipped with or without a live Local entry");
        var foreignPrefix = scene.Contexts(false, true);
        foreignPrefix.SkippedLocal.Add(PathNames.Key(PathNames.Parent(remote)));
        Check.Require(!foreignPrefix.For(protectedRows[1]).Skipped,
            "a skip set matches the row's join key, never its Dropbox entry's spelling");
    }

    private static void ReusesExactProof(Scene scene)
    {
        const string remote = "Folder?/file.txt";
        var local = File("LocalCopy/file.txt") with { Id = "local-id", Size = 7, Modified = Stamp };
        var item = new IssueItem(Side.Local, local.Path, local.Id);
        var proof = new LocalHashProof(local.Size, local.Modified.UtcTicks, new string('a', 64));
        var subject = new IssueSubject([item, new IssueItem(Side.Dropbox, remote, "dropbox-id")], null, proof);
        var key = PathNames.Key(local.Path);
        (Entry? Local, IssueSubject Subject, bool Reuse, string What)[] cases =
        [
            (local, subject, true, "an exact Local subject"),
            (local, subject with { LocalHash = null }, false, "no proof"),
            (null, subject, false, "no current Local entry"),
            (local with { Id = "replacement-id" }, subject, false, "same size/time but different identity"),
        ];
        foreach (var (entry, candidate, reuse, what) in cases)
        {
            var contexts = scene.Contexts(false, true,
                subjects: new Dictionary<string, IssueSubject> { [key] = candidate });
            var row = Make(key, entry, File(remote));
            var result = contexts.WithProof(row);
            Check.Require(reuse ? result.LocalHash == proof.Hash && result == (row with { LocalHash = proof.Hash })
                : ReferenceEquals(result, row), "WithProof: " + what);
            Check.Require(row.LocalHash is null && ReferenceEquals(result.Context, row.Context),
                "WithProof preserves the input row and its context: " + what);
        }
        var wrongKey = scene.Contexts(false, true,
            subjects: new Dictionary<string, IssueSubject> { [PathNames.DropboxKey(remote)] = subject });
        var keptBoth = Make(key, local, File(remote));
        Check.Require(ReferenceEquals(wrongKey.WithProof(keptBoth), keptBoth),
            "WithProof looks up the row's own Local key, never its Dropbox entry's key");
    }

    private static void UnmatchedProofsAllocateNothing(Scene scene)
    {
        var row = Make(PathNames.Key("plain.txt"), File("plain.txt"));
        var subject = new IssueSubject([new IssueItem(Side.Local, "unrelated.txt", "")], null,
            new LocalHashProof(0, default, "hash"));
        IReadOnlyDictionary<string, IssueSubject>?[] proofs =
        [
            null,
            new Dictionary<string, IssueSubject>(),
            new Dictionary<string, IssueSubject> { [PathNames.Key("unrelated.txt")] = subject },
        ];
        foreach (var proofSet in proofs)
        {
            var contexts = scene.Contexts(false, true, subjects: proofSet);
            Check.Require(ReferenceEquals(contexts.WithProof(row), row),
                "absent, empty or non-matching proofs return the original Row");
            var allocated = AllocProbe.Measure(_ => Consume(contexts.WithProof(row).Context), 1_000);
            Check.Require(allocated == 0,
                "absent, empty or non-matching proofs allocate no per-row objects (" + allocated + " B for 1,000 rows)");
        }
    }

    private static void MarksHeld(Scene scene)
    {
        var store = scene.Store();
        Hold(store, scene, IssueKind.NameClash, "Held.txt");
        Hold(store, scene, IssueKind.NameClash, "Dropbox/Held.txt", Side.Dropbox);
        Hold(store, scene, IssueKind.Tie, "HeldFolder");
        var contexts = scene.Contexts(false, true, store);
        var keepBoth = new SyncedEntry(PathNames.Key("Local.txt"), "Local.txt",
            PathNames.DropboxKey("Dropbox/Held.txt"), "Dropbox/Held.txt", false, "", 0, 0, "", "");
        Check.Require(contexts.For(Make("Held.txt", File("Held.txt"))) is { Held: true, HeldKind: IssueKind.NameClash },
            "Held: a held local key carries the kind of its issue");
        Check.Require(contexts.For(Make("HeldFolder/child.txt", File("HeldFolder/child.txt"))) is
            { Held: true, HeldKind: IssueKind.Tie }, "Held: an inherited folder hold carries the hold's kind");
        Check.Require(contexts.For(Make("HeldFolderSibling/child.txt", File("HeldFolderSibling/child.txt"))).Held == false,
            "Held: a held folder prefix ends at a segment boundary");
        Check.Require(contexts.For(Make("Local.txt", File("Local.txt"), synced: keepBoth)) is
            { Held: true, HeldKind: IssueKind.NameClash }, "Held: a held Dropbox key of a keep-both row");
        Check.Require(contexts.For(Make("Free.txt", File("Free.txt"))) is { Held: false, HeldKind: null },
            "Held: an unheld row has no kind");
    }

    private static async Task RetryRefusalHoldsChildren()
    {
        var scene = Scene.Bare();
        var pair = scene.Pair with { Roots = new RootBinding(scene.Pair.LocalRoot, scene.Pair.DropboxRoot,
            scene.Pair.ConnectionId, new DirectoryIdentity(1, 1, null),
            new DropboxRootIdentity("account", "root", "home", "folder")) };
        using var store = new InMemoryStateStore();
        store.SavePair(pair);
        var local = new FakeSide(Side.Local);
        var remote = new FakeSide(Side.Dropbox);
        var parent = Folder("Parent") with { Id = "parent-id" };
        local.Seed(parent, null);
        local.Seed(File("Parent/child") with { Id = "child-id", Size = 1 }, [1]);
        var subject = new IssueSubject([new IssueItem(Side.Local, parent.Path, parent.Id)], null);
        store.Commit(pair.PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailDropboxNoPermission, parent.Path, subject)]
        });
        var original = store.Issues(pair.PairId).Single();
        var attempts = 0;
        var failing = new ProxySide(remote)
        {
            EnsureFault = path =>
            {
                attempts++;
                return new ItemFailedException(path, TextKey.FailDropboxNoPermission, null);
            }
        };
        var folder = Path.Combine(Path.GetTempPath(), "syncshark-retry-hold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var log = RunLog.Open(folder, pair.PairId, "retry", _ => { }, 1024);
            var rig = new Massdex.SyncShark.Engine.Tests.Executor.ScanPassChecks.Rig(pair, store, local, remote,
                new FakeRecovery(), new FakeAdmission(), new PathScope(pair, [pair]));
            await Massdex.SyncShark.Engine.Tests.Executor.ScanPassChecks.RunRepairAsync(rig, log,
                new EngineSettings("", "", "", "", ".case", RecycleFallback.Fail, 1,
                    FirstRunPolicy.NewestWins, 1, "", 1024, ScanTestSettings.BatchRows), dropbox: failing,
                retryIssues: new HashSet<long> { original.Id });
            Check.Require(store.IssueById(pair.PairId, original.Id) is not null && attempts == 1,
                "a selected retry that re-raises its continuing refusal holds later descendants in the same run");
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void MarksStoredHoldKinds(Scene scene)
    {
        var store = scene.Store();
        Hold(store, scene, IssueKind.Tie, "F/stored");
        Hold(store, scene, IssueKind.NameClash, "F");
        var contexts = scene.Contexts(false, true, store);
        Check.Require(contexts.For(Make("F", Folder("F"))) is { Held: true, HeldKind: IssueKind.NameClash },
            "a stored hold covers its root with its kind");
        Check.Require(contexts.For(Make("F/child", File("F/child"))) is { Held: true, HeldKind: IssueKind.NameClash },
            "a stored hold covers descendants with its kind");
        Check.Require(contexts.For(Make("F2/x", File("F2/x"))) is { Held: false, HeldKind: null },
            "a stored hold ends at a segment boundary");
        Check.Require(contexts.For(Make("F/stored/child", File("F/stored/child"))) is
            { Held: true, HeldKind: IssueKind.Tie }, "the nearest stored hold gives the kind");
        Hold(store, scene, IssueKind.NameClash, "Native", Side.Dropbox);
        var mapped = Make("MAPPED/CHILD", dropbox: File("Native/child"));
        var mappedContext = contexts.For(mapped);
        Check.Require(mappedContext is { Held: true, HeldKind: IssueKind.NameClash },
            "a Dropbox-only mapped child inherits its native parent's stored hold: " + mappedContext +
            "; native=" + store.HeldCovers(scene.Pair.PairId, PathNames.DropboxKey("Native/child"), null) +
            "; row=" + store.HeldKind(scene.Pair.PairId, mapped, null));
    }

    // P1-C2-01: the dependents of a failed Local-folder-to-file replacement stay held as Failure for the run,
    // whatever kind their stored issue carries.
    private static void HoldsDependentRows(Scene scene)
    {
        var store = scene.Store();
        Hold(store, scene, IssueKind.Tie, "Dependent/current");
        Hold(store, scene, IssueKind.NameClash, "Other");
        Hold(store, scene, IssueKind.NameClash, "Raised");
        var contexts = scene.Contexts(false, true, store);
        var saved = new SyncedEntry("SAVEDLOCAL", "SavedLocal", "SAVEDDBX", "SavedDropbox", false, "", 0, 0, "", "");
        var failed = Make("JoinKey", File("Native/Local"), File("Native/Remote"), saved);
        contexts.HoldDependents(failed);
        contexts.HoldDependents(failed);
        Check.Require(contexts.For(Make("JoinKey", File("JoinKey"))) is { Held: true, HeldKind: IssueKind.Failure },
            "HoldDependents: the failed row's own key is held as Failure");
        Check.Require(contexts.For(Make(PathNames.Key("Native/Local"), File("Native/Local"))) is
            { Held: true, HeldKind: IssueKind.Failure }, "HoldDependents: a current Local alias is held as Failure");
        Check.Require(contexts.For(Make("Native/Remote", null, Folder("Native/Remote"))) is
            { Held: true, HeldKind: IssueKind.Failure }, "HoldDependents: a current Dropbox alias is held as Failure");
        Check.Require(contexts.For(Make("SavedLocalKey", synced: saved)) is { Held: true, HeldKind: IssueKind.Failure },
            "HoldDependents: a saved Local alias holds a row with a different key");
        Check.Require(contexts.For(Make("SavedDropboxKey", synced: (saved with { LocalKey = "unrelated" }))) is
            { Held: true, HeldKind: IssueKind.Failure }, "HoldDependents: a saved Dropbox alias holds a row");
        Check.Require(contexts.For(Make("JoinKey/child/x.txt", File("JoinKey/child/x.txt"))) is
            { Held: true, HeldKind: IssueKind.Failure }, "HoldDependents: descendants inherit the Failure hold");
        Check.Require(contexts.For(Make("Other/x", File("Other/x"))) is { Held: true, HeldKind: IssueKind.NameClash },
            "HoldDependents: an unrelated row keeps its stored kind");
        Check.Require(contexts.For(Make("Free/x", File("Free/x"))) is { Held: false, HeldKind: null },
            "HoldDependents: an unrelated row stays unheld");
        Check.Require(contexts.For(Make("JoinKeySibling/x", File("JoinKeySibling/x"))).Held == false,
            "HoldDependents: a dependent root ends at a segment boundary");
        contexts.HoldDependents(Make("Dependent/current", File("Dependent/current")));
        Check.Require(contexts.For(Make("Dependent/current", File("Dependent/current"))) is
            { Held: true, HeldKind: IssueKind.Failure },
            "HoldDependents: Failure overrides a hold stored as Tie");
        contexts.HoldDependents(Make("Raised/current", File("Raised/current")));
        Check.Require(contexts.For(Make("Raised/current", File("Raised/current"))) is
            { Held: true, HeldKind: IssueKind.Failure },
            "HoldDependents: Failure overrides a stored NameClash hold");
    }

    private static void FindsDeepHeldAncestor(Scene scene)
    {
        const string deep = "Deep/A/B/C/D/E/F/G";
        var store = scene.Store();
        Hold(store, scene, IssueKind.NameClash, deep);
        for (var i = 0; i < 256; i++) Hold(store, scene, IssueKind.Tie, "Noise/Folder" + i);
        var contexts = scene.Contexts(false, true, store);
        Check.Require(contexts.For(Make(deep + "/child.txt", File(deep + "/child.txt"))) is
            { Held: true, HeldKind: IssueKind.NameClash }, "Held: a deep held ancestor gives its kind among many held keys");
        Check.Require(contexts.For(Make("Unrelated/Folder7/child.txt", File("Unrelated/Folder7/child.txt"))).Held == false,
            "Held: another held key does not hold an unrelated row");
    }

    private static void PrefersMostSpecificHold(Scene scene)
    {
        PrefersMostSpecificHold(scene, parentFirst: true);
        PrefersMostSpecificHold(scene, parentFirst: false);
    }

    private static void PrefersMostSpecificHold(Scene scene, bool parentFirst)
    {
        var store = scene.Store();
        void Add(string key, IssueKind kind) => Hold(store, scene, kind, key);
        if (parentFirst)
        {
            Add("P", IssueKind.Tie);
            Add("P/c", IssueKind.NameClash);
        }
        else
        {
            Add("P/c", IssueKind.NameClash);
            Add("P", IssueKind.Tie);
        }

        var contexts = scene.Contexts(false, true, store);
        var order = parentFirst ? "parent first" : "child first";
        Check.Require(contexts.For(Make("P/c", File("P/c"))).HeldKind == IssueKind.NameClash,
            "Held precedence: a row's own hold beats its held parent's (" + order + ")");
        Check.Require(contexts.For(Make("P/c/g", File("P/c/g"))).HeldKind == IssueKind.NameClash,
            "Held precedence: the nearest held ancestor gives the kind (" + order + ")");
        Check.Require(contexts.For(Make("P/d", File("P/d"))).HeldKind == IssueKind.Tie,
            "Held precedence: a sibling keeps the parent's kind (" + order + ")");
    }

    private static void MarksFolderDeletes(Scene scene)
    {
        var local = scene.Contexts(false, true);
        local.DeletingLocal.Add("Deleted");
        Check.Require(local.For(Make("Deleted/a.txt", File("Deleted/a.txt"))).UnderFolderDelete == Side.Local,
            "UnderFolderDelete: under a local folder delete");
        Check.Require(local.For(Make("DeletedSibling/a.txt", File("DeletedSibling/a.txt"))).UnderFolderDelete is null,
            "UnderFolderDelete: a deleting prefix ends at a segment boundary");
        Check.Require(local.For(Make("plain.txt", File("plain.txt"))).UnderFolderDelete is null,
            "UnderFolderDelete: a row outside every deleting folder");

        var dropbox = scene.Contexts(false, true);
        dropbox.DeletingDropbox.Add("Gone");
        Check.Require(dropbox.For(Make("Gone/a.txt", null, File("Gone/a.txt"))).UnderFolderDelete == Side.Dropbox,
            "UnderFolderDelete: under a Dropbox folder delete");

        var both = scene.Contexts(false, true);
        both.DeletingLocal.Add("Both");
        both.DeletingDropbox.Add("Both");
        Check.Require(both.For(Make("Both/a.txt", File("Both/a.txt"), File("Both/a.txt"))).UnderFolderDelete == Side.Local,
            "UnderFolderDelete: a local deleting prefix wins over an overlapping Dropbox one");
    }

    private static void SelectsSideSets(Scene scene)
    {
        var contexts = scene.Contexts(false, true);
        Check.Require(ReferenceEquals(contexts.Skipped(Side.Local), contexts.SkippedLocal) &&
            ReferenceEquals(contexts.Skipped(Side.Dropbox), contexts.SkippedDropbox),
            "Skipped(side) returns that side's existing set, never a copy");
        Check.Require(ReferenceEquals(contexts.Deleting(Side.Local), contexts.DeletingLocal) &&
            ReferenceEquals(contexts.Deleting(Side.Dropbox), contexts.DeletingDropbox),
            "Deleting(side) returns that side's existing set, never a copy");
        contexts.Skipped(Side.Dropbox).Add("DropSkip");
        Check.Require(contexts.For(Make("DropSkip/a.txt", null, File("DropSkip/a.txt"))).Skipped,
            "a key added through Skipped(side) skips that side's rows");
        contexts.Deleting(Side.Local).Add("Deleted");
        Check.Require(contexts.For(Make("Deleted/a.txt", File("Deleted/a.txt"))).UnderFolderDelete == Side.Local,
            "a key added through Deleting(side) marks folder deletes for that side");
    }

    private static void MarksNestedRoots(Scene scene)
    {
        var local = scene.Contexts(false, true);
        local.DeletingLocal.Add("inner");
        local.DeletingLocal.Add("Deleted");
        Check.Require(local.For(Make("inner", Folder("inner"))).HoldsNestedRoot,
            "HoldsNestedRoot: the inner pair's local root is protected under a local folder delete");
        var other = local.For(Make("Deleted/a.txt", File("Deleted/a.txt")));
        Check.Require(other.UnderFolderDelete == Side.Local && !other.HoldsNestedRoot,
            "HoldsNestedRoot: an ordinary folder delete is not protected");
        Check.Require(!scene.Contexts(false, true).For(Make("inner", Folder("inner"))).HoldsNestedRoot,
            "HoldsNestedRoot: no protection without a folder delete");

        var dropbox = scene.Contexts(false, true);
        dropbox.DeletingDropbox.Add("inner");
        Check.Require(dropbox.For(Make("inner", null, Folder("inner"))).HoldsNestedRoot,
            "HoldsNestedRoot: the inner pair's Dropbox root is protected under a Dropbox folder delete");
        Check.Require(local.For(Make("inner", synced: Synced("inner"))).HoldsNestedRoot,
            "HoldsNestedRoot: a row with no observation uses its saved path for the nested-root check");
    }

    // P4-04c's row projection: a completed scan's deleted-name marker or a retained first-run obligation selects
    // a local-only file for deletion investigation; it is never a deletion decision (P4-03 keeps hold/skip/scope).
    private static void SelectsDeletionEvidence(Scene scene)
    {
        var deleted = new HashSet<string>(StringComparer.Ordinal)
        {
            PathNames.DropboxKey("Folder/Gone.txt"),
            PathNames.DropboxKey("Deep/A/B/C"),
            PathNames.DropboxKey("Keep_1"),
            PathNames.DropboxKey("FolderMatched"),
        };
        var probes = new List<string>();
        bool Probe(string key)
        {
            probes.Add(key);
            return deleted.Contains(key);
        }

        var contexts = scene.Contexts(true, true, deletedName: Probe);
        (Row Row, bool Expected, string What)[] cases =
        [
            (Make(PathNames.Key("Folder/Gone.txt"), File("Folder/Gone.txt")), true, "an exact deleted name"),
            (Make(PathNames.Key("Folder/gone.TXT"), File("Folder/gone.TXT")), true, "a case-only deleted name"),
            (Make(PathNames.Key("Deep/A/B/C/lost.txt"), File("Deep/A/B/C/lost.txt")), true, "a proper deleted ancestor"),
            (Make(PathNames.Key("Keep_1/file.txt"), File("Keep_1/file.txt")), true, "a valid underscore deleted name"),
            (Make(PathNames.Key("Folder/GoneSibling.txt"), File("Folder/GoneSibling.txt")), false,
                "a segment sibling is not a deleted ancestor"),
            (Make(PathNames.Key("Deep/A/B/CD/lost.txt"), File("Deep/A/B/CD/lost.txt")), false,
                "a longer prefix segment is not a deleted ancestor"),
            (Make(PathNames.Key("plain.txt"), File("plain.txt")), false, "no marker"),
            (Make(PathNames.Key("Folder/Gone.txt"), File("Folder/Gone.txt", "SkipReservedName")), false,
                "an invalid-name skip is never a deletion candidate"),
            (Make(PathNames.Key("Folder"), Folder("Folder")), false,
                "a folder with no deleted-name match syncs ordinarily"),
            (Make(PathNames.Key("FolderMatched"), Folder("FolderMatched")), true,
                "a folder with a deleted-name match is selected"),
            (Make(PathNames.Key("Folder/Gone.txt"), File("Folder/Gone.txt"), File("Folder/Gone.txt")), false,
                "an exact live counterpart is not a local-only candidate"),
            (Make(PathNames.Key("Folder/Gone.txt"), synced: Synced("Folder/Gone.txt")), false,
                "a saved binding is not an unbaselined local candidate"),
        ];
        foreach (var (row, expected, what) in cases)
            Check.Require(contexts.For(row).DeletionEligible == expected, "DeletionEligible: " + what);

        probes.Clear();
        Check.Require(!scene.Contexts(true, true)
                .For(Make(PathNames.Key("Deep/A/B/C/lost.txt"), File("Deep/A/B/C/lost.txt"))).DeletionEligible,
            "DeletionEligible: an empty deleted-name relation selects nothing");

        probes.Clear();
        Check.Require(!scene.Contexts(false, false, deletedName: Probe)
                .For(Make(PathNames.Key("Folder/Gone.txt"), File("Folder/Gone.txt"))).DeletionEligible &&
            probes.Count == 0,
            "DeletionEligible: an incomplete acquisition supplies no membership and probes nothing");

        probes.Clear();
        Check.Require(contexts.For(Make(PathNames.Key("Deep/A/B/C/lost.txt"), File("Deep/A/B/C/lost.txt")))
                .DeletionEligible && probes.Count == 1 &&
            probes[0] == PathNames.DropboxKey("Deep/A/B/C"),
            "DeletionEligible: a proper ancestor marker selects on the first probe, deepest ancestor first");

        probes.Clear();
        Check.Require(!contexts.For(Make(PathNames.Key("Deep/A/B/X/file.txt"), File("Deep/A/B/X/file.txt")))
                .DeletionEligible &&
            probes.Count == 5 && probes[^1] == PathNames.DropboxKey("Deep/A/B/X/file.txt") &&
            !probes.Contains(""),
            "DeletionEligible: a non-matching deep row probes every non-root ancestor then the row, never the root entry");

        var retainedStore = scene.Store();
        Retain(retainedStore, scene, "Rename/file.txt", IssueSubject.FirstRunDeletionKind.Item);
        Retain(retainedStore, scene, "Subtree", IssueSubject.FirstRunDeletionKind.UnscannedSubtree);
        Retain(retainedStore, scene, "Notice/file.txt", IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice);
        Retain(retainedStore, scene, "Quiet/file.txt", IssueSubject.FirstRunDeletionKind.None);
        Scanned(retainedStore, scene, "Subtree");
        var obligations = scene.Contexts(false, false, retainedStore);
        Check.Require(obligations.For(Make(PathNames.Key("Rename/file.txt"), File("Rename/file.txt"))).DeletionEligible,
            "DeletionEligible: a retained Item obligation selects its file without a completed scan");
        Check.Require(obligations.For(Make(PathNames.Key("Subtree/deep/file.txt"), File("Subtree/deep/file.txt")))
                .DeletionEligible,
            "DeletionEligible: a retained UnscannedSubtree obligation covers its descendants");
        Check.Require(!obligations.For(Make(PathNames.Key("Notice/file.txt"), File("Notice/file.txt"))).DeletionEligible,
            "DeletionEligible: PreserveWithHistoryNotice keeps its non-destructive disposition");
        Check.Require(!obligations.For(Make(PathNames.Key("Quiet/file.txt"), File("Quiet/file.txt"))).DeletionEligible,
            "DeletionEligible: an ordinary subject obligation selects nothing");
        Check.Require(!obligations.For(Make(PathNames.Key("Rename/file.txt"),
                    synced: Synced("Rename/file.txt"))).DeletionEligible,
            "DeletionEligible: a retained obligation does not select a baselined row");

        Check.Require(ReferenceEquals(contexts.For(Make(PathNames.Key("a.txt"), File("a.txt"))),
                contexts.For(Make(PathNames.Key("b.txt"), File("b.txt")))),
            "an ordinary row without a marker reuses the neutral context");

        var heldStore = scene.Store();
        Hold(heldStore, scene, IssueKind.Tie, "Folder");
        var held = scene.Contexts(true, true, heldStore, deletedName: Probe)
            .For(Make(PathNames.Key("Folder/Gone.txt"), File("Folder/Gone.txt")));
        Check.Require(held is { Held: true, HeldKind: IssueKind.Tie, DeletionEligible: true },
            "DeletionEligible: an exceptional row keeps both its hold and its deletion selection");
    }

    private const string ParentPath = "Parent";
    private const string ChildPath = "Parent/Child";
    private const string OtherId = "other-id";
    private const string ProofHash = "proof";

    private static void ComparisonProjection(Scene scene)
    {
        var reliable = Stamp.AddTicks(-TimeSpan.TicksPerDay);
        foreach (var first in new[] { false, true })
        foreach (var localTime in new[] { Stamp.AddTicks(1), DateTimeOffset.MinValue, DateTimeOffset.FromFileTime(0) })
        foreach (var remoteTime in new[] { localTime, reliable })
        foreach (var reverse in new[] { false, true })
        {
            var local = File(ParentPath) with { Modified = reverse ? remoteTime : localTime };
            var remote = File(ParentPath) with { Modified = reverse ? localTime : remoteTime, ContentHash = ProofHash };
            var contexts = scene.Contexts(first, true);
            var row = Make(PathNames.Key(ParentPath), local, remote);
            row = row with { Context = contexts.For(row) };
            Check.Require(Massdex.SyncShark.Engine.Planner.Decide.For(row, FirstRunPolicy.NewestWins, Stamp, localContentDirty: false).Kind ==
                ActionKind.NeedLocalHash, nameof(ComparisonProjection));
            var equal = Massdex.SyncShark.Engine.Planner.Decide.For(row with { LocalHash = ProofHash },
                FirstRunPolicy.NewestWins, Stamp, localContentDirty: false);
            var different = Massdex.SyncShark.Engine.Planner.Decide.For(row with { LocalHash = OtherId },
                FirstRunPolicy.NewestWins, Stamp, localContentDirty: false);
            Check.Require(equal.Kind == ActionKind.Record && equal.InfoAfter is null &&
                different.Kind == ActionKind.KeepBoth && different.InfoAfter == TextKey.InfoKeptBoth,
                nameof(ComparisonProjection));
            var saved = new SyncedEntry(row.Key, ParentPath, row.Key, ParentPath, false, local.Id,
                local.Size, local.Modified.UtcTicks, remote.Id, ProofHash);
            var recorded = row with { Synced = saved };
            recorded = recorded with { Context = contexts.For(recorded) };
            Check.Require(Massdex.SyncShark.Engine.Planner.Decide.For(recorded, FirstRunPolicy.NewestWins, Stamp,
                localContentDirty: false)
                .IsInSync(), nameof(ComparisonProjection));
        }
    }

    private static void AttemptCompletion()
    {
        var operation = new object();
        foreach (var owner in Enum.GetValues<AttemptOwner>())
        foreach (var result in Enum.GetValues<RequestResult>())
        {
            var attempt = new RequestAttempt(operation, owner);
            var error = new IOException();
            Check.Require(ReferenceEquals(attempt.Operation, operation) && attempt.Owner == owner &&
                attempt.Completion is null && !attempt.TryClaimCompletion(), nameof(AttemptCompletion));
            Check.Require(attempt.TryComplete(result, error, TimeSpan.Zero, false) &&
                !attempt.TryComplete(RequestResult.Success, null, null, true), nameof(AttemptCompletion));
            Check.Require(attempt.Completion is { } completed && completed.Result == result &&
                ReferenceEquals(completed.Error, error) && completed.ProviderWait == TimeSpan.Zero &&
                !completed.NoEffectProven && attempt.TryClaimCompletion() && !attempt.TryClaimCompletion(),
                nameof(AttemptCompletion));
            var next = new RequestAttempt(operation, owner);
            Check.Require(next.Completion is null && !next.TryClaimCompletion(), nameof(AttemptCompletion));
        }
    }

    private static void RetainedProjection(Scene scene)
    {
        foreach (var first in new[] { false, true })
        foreach (var complete in new[] { false, true })
        foreach (var folder in new[] { false, true })
        foreach (var kind in Enum.GetValues<IssueSubject.FirstRunDeletionKind>())
        foreach (var matches in new[] { false, true })
        {
            var probes = 0;
            var store = scene.Store();
            if (kind == IssueSubject.FirstRunDeletionKind.UnscannedSubtree) Scanned(store, scene, ParentPath);
            Retain(store, scene, ParentPath, kind);
            var contexts = scene.Contexts(first, complete, store,
                deletedName: _ => { probes++; return matches; });
            var entry = folder ? Folder(ParentPath) : File(ParentPath);
            var row = Make(PathNames.Key(ParentPath), entry);
            var result = contexts.For(row);
            var selectedScope = kind != IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice &&
                (first || kind != IssueSubject.FirstRunDeletionKind.None);
            Check.Require(result.FirstRunDeletion == kind && result.FirstRun == first &&
                result.CompleteInventory == complete &&
                result.DeletionEligible == (selectedScope && (!complete || matches)) &&
                (selectedScope && complete ? probes == 1 : probes == 0),
                nameof(RetainedProjection));
            probes = 0;
            var recorded = contexts.For(row with { Synced = Synced(ParentPath) });
            var bilateral = contexts.For(row with { Dropbox = entry });
            var skipped = contexts.For(row with { Local = entry with { Skip = nameof(TextKey.SkipUnreadable) } });
            Check.Require(!recorded.DeletionEligible && recorded.FirstRunDeletion == kind &&
                !bilateral.DeletionEligible && bilateral.FirstRunDeletion == kind &&
                !skipped.DeletionEligible && skipped.FirstRunDeletion == kind && skipped.Skipped && probes == 0,
                nameof(RetainedProjection));
        }
        foreach (var kind in Enum.GetValues<IssueSubject.FirstRunDeletionKind>())
        foreach (var folder in new[] { false, true })
        {
            var store = scene.Store();
            Retain(store, scene, ParentPath, kind);
            Scanned(store, scene, ParentPath);
            var child = Make(PathNames.Key(ChildPath), folder ? Folder(ChildPath) : File(ChildPath));
            var contexts = scene.Contexts(false, true, store, deletedName: _ => true);
            var result = contexts.For(child);
            var covered = kind == IssueSubject.FirstRunDeletionKind.UnscannedSubtree;
            Check.Require(result.DeletionEligible == covered && result.FirstRunDeletion ==
                (covered ? IssueSubject.FirstRunDeletionKind.Item : IssueSubject.FirstRunDeletionKind.None),
                nameof(RetainedProjection));
            var nonmatch = scene.Contexts(false, true, store).For(child);
            Check.Require(!nonmatch.DeletionEligible &&
                nonmatch.FirstRunDeletion == result.FirstRunDeletion, nameof(RetainedProjection));
        }
        var retainedStore = scene.Store();
        Retain(retainedStore, scene, ParentPath, IssueSubject.FirstRunDeletionKind.Item);
        var local = File(ParentPath);
        var rowWithProof = Make(PathNames.Key(ParentPath), local);
        var proof = new LocalHashProof(local.Size, local.Modified.UtcTicks, ProofHash);
        var proofs = new Dictionary<string, IssueSubject>
        { [rowWithProof.Key] = Retained(ParentPath, IssueSubject.FirstRunDeletionKind.None) with { LocalHash = proof } };
        foreach (var proofFirst in new[] { false, true })
        {
            var contexts = scene.Contexts(false, true, retainedStore, subjects: proofs);
            var projected = proofFirst ? contexts.WithProof(rowWithProof) : rowWithProof;
            projected = projected with { Context = contexts.For(projected) };
            if (!proofFirst) projected = contexts.WithProof(projected);
            Check.Require(projected.LocalHash == ProofHash &&
                projected.Context.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.Item &&
                !projected.Context.DeletionEligible, nameof(RetainedProjection));
            var replacement = rowWithProof with { Local = local with { Id = OtherId } };
            Check.Require(contexts.For(replacement).FirstRunDeletion == IssueSubject.FirstRunDeletionKind.None &&
                contexts.WithProof(replacement).LocalHash is null, nameof(RetainedProjection));
        }
        var incomplete = scene.Contexts(true, false, deletedName: _ => throw new InvalidOperationException());
        foreach (var entry in new[] { File(ParentPath), Folder(ParentPath) })
        {
            var row = Make(PathNames.Key(ParentPath), entry);
            row = row with { Context = incomplete.For(row) };
            var action = Massdex.SyncShark.Engine.Planner.Decide.For(row, FirstRunPolicy.NewestWins, Stamp, localContentDirty: false);
            Check.Require(row.Context.DeletionEligible && action.Kind == ActionKind.None &&
                action.None == NoneReason.Held, nameof(RetainedProjection));
        }
    }

    private static IssueSubject Retained(string path, IssueSubject.FirstRunDeletionKind kind) =>
        new([new IssueItem(Side.Local, path, string.Empty)], null, null, kind);

    private static SyncedEntry Synced(string path) =>
        new(PathNames.Key(path), path, PathNames.DropboxKey(path), path, false, "", 0, 0, "", "");

    private static Entry File(string path, string? skip = null) =>
        new(path, false, 0, default, "", "", "", skip);

    private static Entry Folder(string path, string? skip = null) =>
        new(path, true, 0, default, "", "", "", skip);

    private static Row Make(string key, Entry? local = null, Entry? dropbox = null, SyncedEntry? synced = null) =>
        new(PathNames.Key(key), local, dropbox, synced, [], null,
            new RowContext(false, false, false, null, false, false, false, false));
}

using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Scenarios;

/// <summary>Real journal notices through production composition, host start and AutoSync wake.</summary>
public static class LiveRunChecks
{
    private const string FilePath = "live/a.txt";
    private const string First = "first";
    private const string Second = "second content";
    private const string Third = "third content, longer";
    private const string LogPattern = "*";

    /// <summary>Checks opportunity exits without running unrelated live scenarios.</summary>
    public static void RunWake()
    {
        var wakeFailures = new List<Exception>();
        foreach (var exit in new[] { "hash-provider-clone", "folder-hash-unrelated", "folder-hash-change", "folder-admission-change", "folder-replacement-admission", "hash-unrelated", "hash-change", "attempt-change", "attempt-unrelated", "progress", "progress-false", "progress-ancestor", "resolve", "failed-install", "reopen", "prerequisite", "global-resume", "platform-wake", "host-stop", "selection-refusal", "pass-refusal", "request", "pause", "host", "stop", "fault" })
        {
            Console.WriteLine("START live-last-selection-wake-" + exit);
            try
            {
                using var scenario = Scenario.Begin("live-last-selection-wake-" + exit);
                LastSelectionWake(scenario, exit);
                Console.WriteLine("PASS live-last-selection-wake-" + exit);
            }
            catch (Exception error)
            {
                Console.WriteLine("FAIL live-last-selection-wake-" + exit + ": " + error.Message);
                wakeFailures.Add(error);
            }
        }
        using (var scenario = Scenario.Begin("live-persistent-refusal"))
        {
            try { PersistentRefusal(scenario); Console.WriteLine("PASS live-persistent-refusal"); }
            catch (Exception error) { wakeFailures.Add(error); }
        }
        if (wakeFailures.Count != 0) throw new AggregateException(wakeFailures);
    }

    public static void Run()
    {
        RunWake();
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            using var scenario = Scenario.Begin("live-folder-peer-" + side);
            MovedFolderPeer(scenario, side);
            using var withChild = Scenario.Begin("live-folder-peer-child-" + side);
            FolderPeerCase(withChild, side, true, false);
        }
        using (var unnoticed = Scenario.Begin("live-folder-peer-unnoticed"))
            FolderPeerCase(unnoticed, Side.Dropbox, true, true);
        RunCore();
        using (var scenario = Scenario.Begin("live-run-two-closes")) EffectRuns(scenario, twoCloses: true);
        Console.WriteLine("PASS live-run-two-closes: second content hash");
        using (var scenario = Scenario.Begin("live-run-refusal")) RefusalIsLogged(scenario);
        using (var scenario = Scenario.Begin("live-receipt-beside-notice")) ReceiptBesideNotice(scenario);
        Console.WriteLine("PASS live-receipt-beside-notice");
        using (var scenario = Scenario.Begin("live-held-join-beside-selection")) HeldJoinBesideSelection(scenario);
        Console.WriteLine("PASS live-held-join-beside-selection");
    }

    /// <summary>Focused real-journal acceptance for ordinary and same-metadata edits.</summary>
    public static void RunCore()
    {
        using (var scenario = Scenario.Begin("live-run-effect")) EffectRuns(scenario, twoCloses: false);
        Console.WriteLine("PASS live-run-effect");
        using (var scenario = Scenario.Begin("live-run-same-metadata")) SameMetadataEdit(scenario);
        Console.WriteLine("PASS live-run-same-metadata");
    }

    /// <summary>CL-02: a locked file in one pair is that pair's Failure; the host and its other pair keep running.</summary>
    public static void RunLockPair()
    {
        using var scenario = Scenario.Begin("live-lock-pair");
        if (scenario.SkipWhenScaled()) return;
        const string locked = "lock/a.txt";
        const string ordinary = "b.txt";
        const string baseline = "AAAA";
        const string edited = "BBBB";
        const string changed = "CCCC";
        var second = scenario.AddSecondPair("pair-b");
        scenario.LocalFile(locked, baseline, Scenario.T0);
        scenario.SecondLocalFile(ordinary, baseline, Scenario.T0);
        scenario.DropboxFile(locked, baseline, Scenario.T0);
        scenario.DropboxFile("pair-b/" + ordinary, baseline, Scenario.T0);
        scenario.RepairAsync();
        scenario.SecondRepairAsync();
        var pairA = scenario.Pair.PairId;
        var pairB = second.PairId;
        var generationA = scenario.Store.ReadPairExecution(pairA)!.Value.Generation;
        var savedA = scenario.Store.SyncedByKey(pairA, Side.Local, PathNames.Key(locked))!;
        var domainA = scenario.Pair.Roots!.Local.VolumeSerialNumber.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        var uploadedHash = DropboxContentHash.Compute(System.Text.Encoding.UTF8.GetBytes(edited));
        // The retry is settled only when the upload is recorded and its work is retired at a pass end.
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.PassEnded = () =>
        {
            try
            {
                if (scenario.Store.SyncedByKey(pairA, Side.Local, PathNames.Key(locked))?.DropboxContentHash == uploadedHash &&
                    !PendingWork.ByIdentity(scenario.Store, Side.Local, domainA, savedA.LocalId).Any())
                    settled.TrySetResult();
            }
            catch { }
        };

        // The edit preserves identity, size and timestamp; a real handle with no share then refuses the hash.
        scenario.LocalFile(locked, edited, Scenario.T0);
        var current = scenario.Local.ReadOneAsync(locked, Scenario.Cancellation).GetAwaiter().GetResult()!;
        scenario.Require(current.Id == savedA.LocalId && current.Size == savedA.LocalSize &&
            current.Modified.UtcTicks == savedA.LocalModifiedTicks,
            "the actual edit preserves identity, size and timestamp");
        using var held = scenario.DenyLocalContent(locked);
        scenario.SecondLocalFile(ordinary, changed, Scenario.T1);

        var completedB = scenario.ObserveCompletion(ordinary, changed);
        var inUse = scenario.ObserveInUseFailure();
        scenario.Start();
        completedB.WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
        scenario.Require(scenario.DropboxFiles().TryGetValue("pair-b/" + ordinary, out var uploaded) &&
            System.Text.Encoding.UTF8.GetString(uploaded) == changed,
            "CL-02: the other pair's ordinary edit reaches Dropbox while the first pair's file is locked");
        inUse.WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
        var failure = scenario.Store.Issues(pairA, [IssueKind.Failure])
            .FirstOrDefault(issue => issue.Cause == TextKey.FailLocalInUse)
            ?? throw new InvalidOperationException("no FailLocalInUse Failure for " + pairA);
        scenario.Require(failure.Path == locked, "CL-02: the locked file's Failure names its path");
        scenario.Require(scenario.Host.Status().Status is not
                (null or Massdex.SyncShark.V2.AutoStatus.Off or Massdex.SyncShark.V2.AutoStatus.Held),
            "CL-02: the host is still running after the item failure");

        var completedA = scenario.ObserveCompletion(locked, edited);
        held.Dispose();
        // Stop automatic attempts before the next notice, so the retry reads one stable file.
        scenario.Host.PauseAsync(null, Scenario.Cancellation).GetAwaiter().GetResult();
        scenario.LocalFile(locked, edited, Scenario.T2);
        scenario.Host.CatchUpLocalAsync(pairA, generationA, Scenario.Cancellation).GetAwaiter().GetResult();
        scenario.Host.ResumeAsync(null, Scenario.Cancellation).GetAwaiter().GetResult();
        completedA.WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
        scenario.Require(scenario.DropboxFiles().TryGetValue(locked, out var uploadedEdit) &&
            System.Text.Encoding.UTF8.GetString(uploadedEdit) == edited,
            "CL-02: after the handle closes and the next notice arrives, the first pair's edit uploads");

        var savedB = scenario.Store.SyncedByKey(pairB, Side.Local, PathNames.Key(ordinary))!;
        var domainB = second.Roots!.Local.VolumeSerialNumber.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        settled.Task.WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
        scenario.Require(!PendingWork.ByIdentity(scenario.Store, Side.Local, domainA, savedA.LocalId).Any(),
            "CL-02: the retried item leaves no stranded work");
        scenario.Require(!PendingWork.ByIdentity(scenario.Store, Side.Local, domainB, savedB.LocalId).Any(),
            "CL-02: the other pair's completed edit leaves no stranded work");
        scenario.Require(scenario.Store.Issues(pairA, [IssueKind.Failure]).All(
                issue => issue.Cause != TextKey.FailLocalInUse),
            "CL-02: the successful retry closes its in-use Failure");
        scenario.Host.PauseAsync(null, Scenario.Cancellation).GetAwaiter().GetResult();
    }

    private static void MovedFolderPeer(Scenario scenario, Side first) => FolderPeerCase(scenario, first, false, false);

    private static void FolderPeerCase(Scenario scenario, Side first, bool remoteChild, bool unnoticed)
    {
        const string folder = "incoming";
        const string child = folder + "/child.txt";
        scenario.RepairAsync();
        var outside = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(scenario.LocalRoot)!, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "child.txt"), First);
        var generation = scenario.Store.ReadPairExecution(scenario.Pair.PairId)!.Value.Generation;
        // Consume the child's outside-root creation before the move; only the folder move belongs to this case.
        scenario.Host.CatchUpLocalAsync(scenario.Pair.PairId, generation, Scenario.Cancellation).GetAwaiter().GetResult();
        Directory.Move(outside, Path.Combine(scenario.LocalRoot, folder));
        scenario.DropboxFolder(folder);
        if (remoteChild) scenario.DropboxFile(child, First);
        var remoteMember = remoteChild ? scenario.Dropbox.ReadOneAsync(child, Scenario.Cancellation).GetAwaiter().GetResult() : null;
        var local = scenario.Local.ReadOneAsync(folder, Scenario.Cancellation).GetAwaiter().GetResult()!;
        var remote = scenario.Dropbox.ReadOneAsync(folder, Scenario.Cancellation).GetAwaiter().GetResult()!;
        foreach (var side in new[] { first, PathNames.Other(first) })
        {
            var source = scenario.Store.ReadSource(scenario.Pair.PairId, side)!.Value;
            var entry = side == Side.Local ? local : remote;
            var notice = new ChangeNotice(side, ChangeKind.Changed, folder, null, entry.Id, entry, false, false);
            if (side == Side.Local)
            {
                var volume = scenario.Pair.Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var position = scenario.Store.ReadJournal(volume)!;
                scenario.Store.AcquireJournalPage(volume, position, position,
                    [(scenario.Pair.PairId, generation, source.Epoch, source.Revision, false)],
                    [(scenario.Pair.PairId, notice)]);
            }
            else scenario.Store.AcquireDropboxPage(scenario.Pair.PairId, generation, source.Epoch, source.Revision,
                source.Provenance!, source.Cursor, remoteMember is null ? [notice] :
                    [notice, new ChangeNotice(Side.Dropbox, ChangeKind.Changed, child, null,
                        remoteMember.Id, remoteMember, false, false)], source.Cursor!);
        }
        scenario.Host.CatchUpLocalAsync(scenario.Pair.PairId, generation, Scenario.Cancellation).GetAwaiter().GetResult();
        var localDomain = scenario.Pair.Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (unnoticed)
        {
            // Model an already-present Local folder with no surviving acquisition, without reconciling it.
            foreach (var work in PendingWork.ByIdentity(scenario.Store, Side.Local, localDomain, local.Id).ToArray())
                scenario.Store.CompleteWork(work, null);
            scenario.Require(!PendingWork.ByIdentity(scenario.Store, Side.Local, localDomain, local.Id).Any(),
                "PR-02: Local folder has no acquired notice before host dispatch");
        }
        var completed = scenario.ObserveCompletion(child, First);
        var remoteDomain = scenario.Pair.Roots!.Dropbox.RootNamespaceId;
        bool Settled() => scenario.Store.SyncedByKey(scenario.Pair.PairId, Side.Local, PathNames.Key(child)) is not null &&
            !PendingWork.ByIdentity(scenario.Store, Side.Local, localDomain, local.Id).Any() &&
            !PendingWork.ByIdentity(scenario.Store, Side.Dropbox, remoteDomain, remote.Id).Any() &&
            (remoteMember is null || !PendingWork.ByIdentity(scenario.Store, Side.Dropbox, remoteDomain, remoteMember.Id).Any());
        var initialLogs = Directory.EnumerateFiles(scenario.Logs, LogPattern, SearchOption.AllDirectories).ToHashSet();
        Side? selectedFirst = null;
        // Register the existing observation callback, but run the host-owned AutoSync that receives source wakes.
        _ = scenario.CreateAutoSync(() => { }, selected =>
        {
            if (selected is not null) selectedFirst ??= selected.Side;
        });
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Child completion can precede the peer's serial follow-up; pausing must not cancel that remaining work.
        scenario.PassEnded = () => { if (completed.IsCompleted && Settled()) drained.TrySetResult(); };
        scenario.Start();
        drained.Task.WaitAsync(Scenario.Cancellation).GetAwaiter().GetResult();
        scenario.Host.PauseAsync(scenario.Pair.PairId, Scenario.Cancellation).GetAwaiter().GetResult();
        scenario.Require(selectedFirst == first &&
            System.Text.Encoding.UTF8.GetString(scenario.DropboxFiles()[child]) == First,
            "G18-03: moved-in disk child uploads through AutoSync without Repair, first side " + first);
        scenario.Require(Settled(),
            "PR-02: both folder rows and the opposite-side child settle, including unnoticed Local folder");
        scenario.Require(Directory.EnumerateFiles(scenario.Logs, LogPattern, SearchOption.AllDirectories)
            .Where(path => !initialLogs.Contains(path)).All(path => !ReadShared(path).Contains(nameof(TextKey.StageListing))),
            "PR-02: host did not run a Repair to settle the moved-in folder");
    }

    public static void RunHeldTwice()
    {
        using var scenario = Scenario.Begin("live-held-two-changes");
        const string sibling = "live/b.txt";
        scenario.LocalFile(FilePath, First);
        scenario.RepairAsync();
        var saved = scenario.Store.SyncedByKey(scenario.Pair.PairId, Side.Local, PathNames.Key(FilePath))!;
        scenario.Store.Commit(scenario.Pair.PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.NameClash, TextKey.ClashNameTaken, FilePath,
                new IssueSubject([new IssueItem(Side.Local, FilePath, saved.LocalId)], null))]
        });
        scenario.LocalFile(FilePath, Second, Scenario.T1);
        scenario.LocalFile(FilePath, Third, Scenario.T2);
        var completed = scenario.ObserveCompletion(sibling, Third);
        scenario.LocalFile(sibling, Third, Scenario.T2);
        scenario.Start();
        completed.WaitAsync(Scenario.Cancellation).GetAwaiter().GetResult();
        scenario.Host.PauseAsync(scenario.Pair.PairId, Scenario.Cancellation).GetAwaiter().GetResult();
        scenario.Require(System.Text.Encoding.UTF8.GetString(scenario.DropboxFiles()[sibling]) == Third,
            "a held item changing twice does not stop its sibling's opportunity");
        var domain = scenario.Pair.Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        scenario.Require(PendingWork.ByIdentity(scenario.Store, Side.Local, domain, saved.LocalId).Any(),
            "the held changes retain reachable evidence");
    }

    private static void Prepare(Scenario scenario)
    {
        scenario.LocalFile(FilePath, First);
        scenario.RepairAsync();
        scenario.Start();
    }

    private static void EffectRuns(Scenario scenario, bool twoCloses)
    {
        Prepare(scenario);
        var completed = scenario.ObserveCompletion(FilePath, Second);
        if (twoCloses) scenario.LocalFile(FilePath, Second, Scenario.T1);
        else File.WriteAllText(Path.Combine(scenario.LocalRoot, FilePath), Second);
        completed.WaitAsync(Scenario.Cancellation).GetAwaiter().GetResult();
        scenario.Host.PauseAsync(scenario.Pair.PairId, Scenario.Cancellation).GetAwaiter().GetResult();
        scenario.Require(scenario.DropboxFiles().TryGetValue(FilePath, out var bytes) &&
            System.Text.Encoding.UTF8.GetString(bytes) == Second,
            "the real notice ran its upload through AutoSync, ChangeRunner and the real store");
        scenario.Require(scenario.Store.SelectWork(scenario.Pair.PairId,
                scenario.Store.ReadPairExecution(scenario.Pair.PairId)!.Value.Generation,
                new PathScope(scenario.Pair, scenario.Store.Pairs(), Scenario.FixtureSettings.DefaultIgnoreRules)) is null,
            "the completed selection retired its work");
    }

    private static void SameMetadataEdit(Scenario scenario)
    {
        const string edited = "other";
        scenario.LocalFile(FilePath, First, Scenario.T0);
        scenario.RepairAsync();
        var before = scenario.Store.SyncedByKey(scenario.Pair.PairId, Side.Local, PathNames.Key(FilePath))!;
        var completed = scenario.ObserveCompletion(FilePath, edited);
        scenario.LocalFile(FilePath, edited, Scenario.T0);
        var current = scenario.Local.ReadOneAsync(FilePath, Scenario.Cancellation).GetAwaiter().GetResult()!;
        scenario.Require(current.Id == before.LocalId && current.Size == before.LocalSize &&
            current.Modified.UtcTicks == before.LocalModifiedTicks,
            "the actual edit preserves identity, size and timestamp");
        scenario.Start();
        completed.WaitAsync(Scenario.Cancellation).GetAwaiter().GetResult();
        scenario.Host.PauseAsync(scenario.Pair.PairId, Scenario.Cancellation).GetAwaiter().GetResult();
        scenario.Require(System.Text.Encoding.UTF8.GetString(scenario.DropboxFiles()[FilePath]) == edited,
            "A72: the real journal wake hashes and uploads the unchanged-metadata edit");
    }

    private static void RefusalIsLogged(Scenario scenario)
    {
        scenario.LocalFile(FilePath, First);
        scenario.RepairAsync();
        scenario.Host.PauseAsync(scenario.Pair.PairId, Scenario.Cancellation).GetAwaiter().GetResult();
        scenario.Start();
        scenario.LocalFile(FilePath, Third, Scenario.T1);
        var key = PathNames.Key(FilePath);
        var saved = scenario.Store.SyncedByKey(scenario.Pair.PairId, Side.Local, key)!;
        var entry = scenario.Local.ReadOneAsync(FilePath, Scenario.Cancellation).GetAwaiter().GetResult()!;
        var note = new PendingEffect(1, Guid.NewGuid().ToString(), 0, PendingKind.LocalDelete, PendingPhase.Armed, FilePath,
            new RemovalProof(entry with { Path = saved.LocalPath, Id = saved.LocalId }, "volume", "hash"), saved,
            null, null, null, null, false, null);
        scenario.Store.ArmPending(scenario.Pair.PairId, note);
        var refused = scenario.ObserveRefusal();
        scenario.Host.ResumeAsync(scenario.Pair.PairId, Scenario.Cancellation).GetAwaiter().GetResult();
        var reason = refused.WaitAsync(Scenario.Cancellation).GetAwaiter().GetResult();
        scenario.Host.PauseAsync(scenario.Pair.PairId, Scenario.Cancellation).GetAwaiter().GetResult();
        scenario.Store.SettlePending(scenario.Pair.PairId, note, StateChange.Empty);
        scenario.Require(reason == LiveAdmissionStaleReason.RelevantEvidenceChanged,
            "a pending effect in the footprint refuses admission (observed " + reason + ")");
        var logged = Directory.EnumerateFiles(scenario.Logs, LogPattern, SearchOption.AllDirectories)
            .Any(path => ReadShared(path).Contains(nameof(LiveAdmissionStaleReason.RelevantEvidenceChanged)));
        scenario.Require(logged, "the refused opportunity leaves its reason in the run log");
    }

    private static void DrainedHostWake(Scenario scenario, bool stop)
    {
        scenario.LocalFile(FilePath, First);
        scenario.RepairAsync();
        scenario.LocalFile(FilePath, Second, Scenario.T1);
        var generation = scenario.Store.ReadPairExecution(scenario.Pair.PairId)!.Value.Generation;
        scenario.Host.CatchUpLocalAsync(scenario.Pair.PairId, generation, Scenario.Cancellation).GetAwaiter().GetResult();
        var completed = scenario.ObserveCompletion(FilePath, Second);
        var transition = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var injected = false;
        scenario.CreateAutoSync(() => { }, selection =>
        {
            if (selection is null || injected) return;
            injected = true;
            transition.SetResult(stop ? scenario.Host.StopAsync(Scenario.Cancellation) :
                scenario.Host.ChangeGenerationAsync(scenario.Pair, async (_, _, current, token) =>
                {
                    held.TrySetResult();
                    await release.Task.WaitAsync(token);
                    return current[scenario.Pair.PairId].Pair;
                }, Scenario.Cancellation));
        });
        try
        {
            scenario.Start();
            if (!stop)
            {
                held.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                var refused = false;
                try { scenario.Host.RecomposeCurrentAsync(scenario.Pair.PairId, Scenario.Cancellation).GetAwaiter().GetResult(); }
                catch (LiveAdmissionStaleException) { refused = true; }
                scenario.Require(refused && !completed.IsCompleted,
                    "R6: the held configuration transition refuses dispatch and preserves unfinished work");
            }
        }
        finally { release.TrySetResult(); }
        transition.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult()
            .WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        if (stop) return;
        scenario.Require(scenario.Store.ReadPairExecution(scenario.Pair.PairId)!.Value.Generation == generation,
            "R1: the configuration drain leaves the pair generation unchanged");
        completed.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        scenario.Host.PauseAsync(scenario.Pair.PairId, Scenario.Cancellation).GetAwaiter().GetResult();
        scenario.Require(System.Text.Encoding.UTF8.GetString(scenario.DropboxFiles()[FilePath]) == Second,
            "R1: reopen resumes unfinished work without another source event");
    }

    private static void PrerequisiteWake(Scenario scenario)
    {
        scenario.LocalFile(FilePath, First);
        scenario.RepairAsync();
        scenario.Start();
        scenario.Host.PauseAsync(scenario.Pair.PairId, Scenario.Cancellation).GetAwaiter().GetResult();
        var repaired = new TaskCompletionSource<RepairSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.RepairFinished = finished => repaired.TrySetResult(finished);
        var generation = scenario.Store.ReadPairExecution(scenario.Pair.PairId)!.Value.Generation;
        var source = scenario.Store.ReadSource(scenario.Pair.PairId, Side.Local)!.Value;
        scenario.Store.AdvanceSource(scenario.Pair.PairId, Side.Local, generation, source.Epoch, source.Revision,
            source.Provenance, source.Cursor, source.Epoch, source.Revision + 1, source.Provenance, source.Cursor,
            SourceObligation.NormalRepair);
        var completed = scenario.ObserveCompletion(FilePath, Third);
        var skipped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var injecting = new AsyncLocal<bool>();
        scenario.BeforeRepairCheckpoint = () =>
        {
            scenario.BeforeRepairCheckpoint = null;
            injecting.Value = true;
            scenario.ObligationObserved = () =>
            {
                if (!injecting.Value) skipped.TrySetResult();
            };
            try
            {
                scenario.LocalFile(FilePath, Third, Scenario.T2);
                scenario.Host.CatchUpLocalAsync(scenario.Pair.PairId, generation, Scenario.Cancellation).GetAwaiter().GetResult();
                skipped.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            }
            finally { scenario.ObligationObserved = null; injecting.Value = false; }
        };
        scenario.Host.ResumeAsync(scenario.Pair.PairId, Scenario.Cancellation).GetAwaiter().GetResult();
        var summary = repaired.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        scenario.Require(summary is { Completed: true }, "R2: prerequisite Repair completed after the guarded arrival");
        completed.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        scenario.Host.PauseAsync(scenario.Pair.PairId, Scenario.Cancellation).GetAwaiter().GetResult();
        scenario.Require(System.Text.Encoding.UTF8.GetString(scenario.DropboxFiles()[FilePath]) == Third,
            "R2: completion wakes work committed after the scan without another source event");
    }

    // CL-06: a global resume must start an owed Repair that no pair resume reaches.
    private static void GlobalResumeRepair(Scenario scenario)
    {
        scenario.LocalFile(FilePath, First);
        scenario.RepairAsync();
        var pairId = scenario.Pair.PairId;
        scenario.Start();
        scenario.Host.PauseAsync(null, Scenario.Cancellation).GetAwaiter().GetResult();
        var repaired = new TaskCompletionSource<RepairSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.RepairFinished = finished => repaired.TrySetResult(finished);
        var generation = scenario.Store.ReadPairExecution(pairId)!.Value.Generation;
        var source = scenario.Store.ReadSource(pairId, Side.Local)!.Value;
        scenario.Store.AdvanceSource(pairId, Side.Local, generation, source.Epoch, source.Revision,
            source.Provenance, source.Cursor, source.Epoch, source.Revision + 1, source.Provenance, source.Cursor,
            SourceObligation.NormalRepair);
        scenario.Host.ResumeAsync(null, Scenario.Cancellation).GetAwaiter().GetResult();
        var summary = repaired.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        scenario.Require(summary is { Completed: true }, "CL-06: global resume starts the owed Repair");
        scenario.Host.PauseAsync(pairId, Scenario.Cancellation).GetAwaiter().GetResult();
    }

    // CL-06: a platform wake must reach AutoSync and consume a retained selection, not Monitor alone.
    private static void PlatformWakeSelection(Scenario scenario)
    {
        scenario.LocalFile(FilePath, First);
        scenario.RepairAsync();
        using var wake = new FakeWake();
        scenario.Host.BindPlatformWake(wake);
        var selected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = scenario.CreateAutoSync(() => { }, selection => { if (selection is not null) selected.TrySetResult(); });
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.PassEnded = () => idle.TrySetResult();
        scenario.Start();
        idle.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        scenario.PassEnded = null;
        var pairId = scenario.Pair.PairId;
        var generation = scenario.Store.ReadPairExecution(pairId)!.Value.Generation;
        scenario.Host.CatchUpLocalAsync(pairId, generation, Scenario.Cancellation).GetAwaiter().GetResult();
        // A retained selection seeded through the store: the journal's own wake is not involved.
        var volume = scenario.Pair.Roots!.LocalVolumeKey();
        var position = scenario.Store.ReadJournal(volume) ?? throw new InvalidOperationException("no journal position");
        var source = scenario.Store.ReadSource(pairId, Side.Local)!.Value;
        var saved = scenario.Store.SyncedByKey(pairId, Side.Local, PathNames.Key(FilePath))!;
        Check.Require(scenario.Store.AcquireJournalPage(volume, position, position,
                [(pairId, generation, source.Epoch, source.Revision, false)],
                [(pairId, new ChangeNotice(Side.Local, ChangeKind.Changed, FilePath, null, saved.LocalId, null, false, false))])
            is not null, "CL-06: the retained selection is installed");
        wake.Raise();
        selected.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        scenario.Host.PauseAsync(pairId, Scenario.Cancellation).GetAwaiter().GetResult();
    }

    private sealed class FakeWake : Massdex.SyncShark.V2.ISystemWakeSource
    {
        public event Action? Woke;
        internal void Raise() => Woke?.Invoke();
        public void Dispose() { }
    }

    private static void FailedInstall(Scenario scenario)
    {
        scenario.RepairAsync();
        scenario.Start();
        var fault = new InvalidDataException("synthetic install failure");
        var installs = 0;
        try
        {
            scenario.Host.ChangeGenerationAsync(scenario.Pair, (_, _, current, _) =>
            {
                scenario.BeforePairs = () =>
                {
                    Interlocked.Increment(ref installs);
                    throw fault;
                };
                return Task.FromResult(current[scenario.Pair.PairId].Pair);
            }, Scenario.Cancellation).GetAwaiter().GetResult();
            throw new InvalidOperationException("R7: the synthetic install must fail");
        }
        catch (InvalidDataException error) when (ReferenceEquals(error, fault)) { }
        scenario.Require(installs == 1 && scenario.Store.ReadPrerequisiteRetry(scenario.Pair.PairId) is not null,
            "R7: before any pause, failed install is attempted once and leaves its prerequisite retry armed");
        scenario.Host.PauseAsync(scenario.Pair.PairId, Scenario.Cancellation).GetAwaiter().GetResult();
        scenario.BeforePairs = null;
        scenario.Host.StopAsync(Scenario.Cancellation).GetAwaiter().GetResult();
    }

    private static void ProgressPass(Scenario scenario, string mode)
    {
        const string first = "progress-a.txt";
        const string second = "progress-b.txt";
        scenario.RepairAsync();
        scenario.LocalFile(first, First);
        scenario.LocalFile(second, Second);
        var generation = scenario.Store.ReadPairExecution(scenario.Pair.PairId)!.Value.Generation;
        var source = scenario.Store.ReadSource(scenario.Pair.PairId, Side.Local)!.Value;
        var domain = scenario.Pair.Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var position = scenario.Store.ReadJournal(domain)!;
        var entries = new[] { first, second }.Select(path =>
            scenario.Local.ReadOneAsync(path, Scenario.Cancellation).GetAwaiter().GetResult()!).ToArray();
        scenario.Store.AcquireJournalPage(domain, position, position,
            [(scenario.Pair.PairId, generation, source.Epoch, source.Revision, false)],
            entries.Select(entry => (scenario.Pair.PairId,
                new ChangeNotice(Side.Local, ChangeKind.Changed, entry.Path, null, entry.Id, entry, false, false))));
        scenario.Host.CatchUpLocalAsync(scenario.Pair.PairId, generation, Scenario.Cancellation).GetAwaiter().GetResult();
        bool SecondPending() => PendingWork.ByIdentity(scenario.Store, Side.Local, domain, entries[1].Id).Any();
        var unfinished = mode == "progress-false";
        var ancestor = mode == "progress-ancestor";
        var refused = false;
        if (ancestor) scenario.SelectionAncestors = () =>
            PendingWork.ByIdentity(scenario.Store, Side.Local, domain, entries[1].Id);
        if (unfinished) scenario.HeldWithoutIssue = row => row.Local?.Id == entries[0].Id && SecondPending();
        else scenario.BeforeLiveValidation = selection =>
        {
            if (selection.Identity == entries[0].Id && (ancestor ? !refused : SecondPending()))
            {
                refused = true;
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged);
            }
        };
        var completed = scenario.ObserveCompletion(first, First);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opportunities = 0;
        var anchors = new List<long>();
        scenario.PassEnded = () => { if (completed.IsCompleted) ended.TrySetResult(); };
        var auto = scenario.CreateAutoSync(() => opportunities++, selection =>
        {
            if (selection is not null) anchors.Add(selection.AnchorWorkId);
        });
        try
        {
            auto.Start();
            ended.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        }
        finally { auto.StopAsync().GetAwaiter().GetResult(); }
        // Net retirement also earns the final empty pass that confirms there is no newly released work.
        scenario.Require(opportunities == 1 && scenario.EndedWorkPasses == 3 && anchors.SequenceEqual(new long[] { 1, 2, 1 }),
            "progress: one opportunity executes refused anchor1, retires2, finishes1, then confirms an empty pass without a wake" +
            "; opportunities=" + opportunities + " passes=" + scenario.EndedWorkPasses +
            " anchors=" + string.Join(",", anchors));
        scenario.Require((unfinished || scenario.ObserveRefusal().GetAwaiter().GetResult() == LiveAdmissionStaleReason.RelevantEvidenceChanged) &&
            System.Text.Encoding.UTF8.GetString(scenario.DropboxFiles()[first]) == First &&
            System.Text.Encoding.UTF8.GetString(scenario.DropboxFiles()[second]) == Second,
            "progress: store-level refusal clears only when the competing selection retires");
    }

    private static void HashEvidenceChange(Scenario scenario, bool related, int changedRead)
    {
        scenario.LocalFile(FilePath, First);
        scenario.RepairAsync();
        scenario.LocalFile(FilePath, Second, Scenario.T1);
        var pairId = scenario.Pair.PairId;
        var generation = scenario.Store.ReadPairExecution(pairId)!.Value.Generation;
        scenario.Host.CatchUpLocalAsync(pairId, generation, Scenario.Cancellation).GetAwaiter().GetResult();
        var completed = scenario.ObserveCompletion(FilePath, related ? Third : Second);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.PassEnded = () => ended.TrySetResult();
        var reads = 0;
        var opportunities = 0;
        scenario.BeforeDirtyRead = () =>
        {
            if (++reads != changedRead) return;
            scenario.BeforeDirtyRead = null;
            if (related) scenario.LocalFile(FilePath, Third, Scenario.T2);
            else scenario.LocalFile("outside-evidence.txt", First);
            scenario.Host.CatchUpLocalAsync(pairId, generation, Scenario.Cancellation).GetAwaiter().GetResult();
        };
        var auto = scenario.CreateAutoSync(() => opportunities++, _ => { });
        try
        {
            auto.Start();
            ended.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            scenario.Require(reads == changedRead && opportunities == 1 &&
                scenario.Store.Issues(pairId, [IssueKind.Failure]).Count == 0,
                "W1: evidence changes at the chosen proof read without raising a Failure");
            if (related)
            {
                scenario.Require(!completed.IsCompleted &&
                    PendingWork.ByIdentity(scenario.Store, Side.Local,
                        scenario.Pair.Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        scenario.Store.SyncedByKey(pairId, Side.Local, PathNames.Key(FilePath))!.LocalId).Any(),
                    "W1: a real change is retained rather than stopping the host");
                ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                auto.Kick(pairId);
                ended.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            }
            scenario.Require(completed.IsCompleted && opportunities == (related ? 2 : 1),
                "W1: unrelated volume progress completes immediately; a real change settles on its following wake");
        }
        finally { auto.StopAsync().GetAwaiter().GetResult(); }
    }

    private static void ResolveWake(Scenario scenario)
    {
        scenario.LocalFile(FilePath, First);
        scenario.RepairAsync();
        var pairId = scenario.Pair.PairId;
        var saved = scenario.Store.SyncedByKey(pairId, Side.Local, PathNames.Key(FilePath))!;
        scenario.Store.Commit(pairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Tie, TextKey.TieEqualTimes, FilePath,
                new IssueSubject([new IssueItem(Side.Local, FilePath, saved.LocalId),
                    new IssueItem(Side.Dropbox, FilePath, saved.DropboxId)], null))]
        });
        var issue = scenario.Store.Issues(pairId, [IssueKind.Tie]).Single();
        scenario.LocalFile(FilePath, Second, Scenario.T1);
        var generation = scenario.Store.ReadPairExecution(pairId)!.Value.Generation;
        scenario.Host.CatchUpLocalAsync(pairId, generation, Scenario.Cancellation).GetAwaiter().GetResult();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.PassEnded = () => ended.TrySetResult();
        scenario.Start();
        ended.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        var completed = scenario.ObserveCompletion(FilePath, Second);
        var operations = new EngineOperations(scenario.Host.Authority.ProductRoot, _ => { }, scenario.Host);
        var result = operations.ResolveAsync(pairId, issue.Id, ResolutionChoice.UseLocal, null, null, false, null,
            Scenario.Cancellation).GetAwaiter().GetResult();
        scenario.Require(result.Resolved, "W3: the selected held issue is resolved");
        completed.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        scenario.Host.PauseAsync(pairId, Scenario.Cancellation).GetAwaiter().GetResult();
        scenario.Require(System.Text.Encoding.UTF8.GetString(scenario.DropboxFiles()[FilePath]) == Second,
            "W3: resolving a hold wakes and settles its retained selection without another source notice");
    }

    private static void FolderHashEvidence(Scenario scenario, bool related, bool admitted = false)
    {
        const string folder = "planned";
        const string child = folder + "/child.txt";
        scenario.LocalFile(child, First, Scenario.T0);
        scenario.RepairAsync();
        var pairId = scenario.Pair.PairId;
        var generation = scenario.Store.ReadPairExecution(pairId)!.Value.Generation;
        var saved = scenario.Store.SyncedByKey(pairId, Side.Local, PathNames.Key(child))!;
        // Same bytes with an uncertain saved stamp require the plan's child hash, not an item upload.
        scenario.Store.Commit(pairId, StateChange.Empty with
        {
            Record = [saved with { LocalModifiedTicks = Scenario.T1.UtcTicks }]
        });
        var remote = scenario.Dropbox.ReadOneAsync(folder, Scenario.Cancellation).GetAwaiter().GetResult()!;
        scenario.RemoveDropbox(folder);
        var source = scenario.Store.ReadSource(pairId, Side.Dropbox)!.Value;
        scenario.Store.AcquireDropboxPage(pairId, generation, source.Epoch, source.Revision,
            source.Provenance!, source.Cursor,
            [new ChangeNotice(Side.Dropbox, ChangeKind.Deleted, folder, null, remote.Id, null, false, false)], source.Cursor!);
        scenario.Host.CatchUpLocalAsync(pairId, generation, Scenario.Cancellation).GetAwaiter().GetResult();
        var reads = 0;
        void Change()
        {
            scenario.LocalFile(related ? child : "unrelated.txt", related ? Second : First, Scenario.T2);
            var localSource = scenario.Store.ReadSource(pairId, Side.Local)!.Value;
            var volume = scenario.Pair.Roots!.LocalVolumeKey();
            var position = scenario.Store.ReadJournal(volume)!;
            var entry = scenario.Local.ReadOneAsync(related ? child : "unrelated.txt", Scenario.Cancellation).GetAwaiter().GetResult()!;
            scenario.Store.AcquireJournalPage(volume, position, position,
                [(pairId, generation, localSource.Epoch, localSource.Revision, false)],
                [(pairId, new ChangeNotice(Side.Local, ChangeKind.Changed, entry.Path, null, entry.Id, entry, true, false))]);
        }
        if (admitted) scenario.AfterFreshRead(Side.Local, folder, () => { reads = 2; Change(); }, occurrence: 2);
        else scenario.BeforeDirtyRead = () =>
        {
            if (++reads != 2) return;
            scenario.BeforeDirtyRead = null;
            Console.WriteLine("X1 folder hash proof changed: related=" + related);
            Change();
        };
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.PassEnded = () => ended.TrySetResult();
        var opportunities = 0;
        var auto = scenario.CreateAutoSync(() => opportunities++, selection => Console.WriteLine("X1 selected " + selection?.Key));
        try
        {
            auto.Start();
            ended.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            if (admitted)
            {
                scenario.Require(reads == 2 && opportunities == 1 &&
                    scenario.Store.Issues(pairId, [IssueKind.Failure]).Count == 0,
                    "X1: the live folder plan hashes its child and neither evidence change stops the host or raises Failure");
                var retainedBeforeWake = PendingWork.ByIdentity(scenario.Store, Side.Dropbox,
                    scenario.Pair.Roots!.Dropbox.RootNamespaceId, remote.Id).Any();
                var refused = scenario.ObserveRefusal();
                scenario.Require(related && refused.IsCompleted &&
                    refused.Result == LiveAdmissionStaleReason.RelevantEvidenceChanged && retainedBeforeWake &&
                    scenario.LocalFiles().ContainsKey(child),
                    "X1: a related change acquired after admission refuses the live folder plan and retains its selection");
                // The child's own change must settle first, then the retained folder selection clears.
                for (var wake = 0; wake < 3; wake++)
                {
                    ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    auto.Kick(pairId);
                    ended.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                    if (!PendingWork.ByIdentity(scenario.Store, Side.Dropbox,
                            scenario.Pair.Roots!.Dropbox.RootNamespaceId, remote.Id).Any())
                        break;
                }
                scenario.Require(!PendingWork.ByIdentity(scenario.Store, Side.Dropbox,
                        scenario.Pair.Roots!.Dropbox.RootNamespaceId, remote.Id).Any() &&
                    scenario.DropboxFiles().ContainsKey(child) &&
                    scenario.Store.Issues(pairId, [IssueKind.Failure]).Count == 0,
                    "X1: the refused plan converges on its following wake; opportunities=" + opportunities);
            }
        }
        finally { auto.StopAsync().GetAwaiter().GetResult(); }
        if (admitted) return;
        scenario.Require(reads == 2 && opportunities == 1 && scenario.Store.Issues(pairId, [IssueKind.Failure]).Count == 0,
            "X1: the live folder plan hashes its child and neither evidence change stops the host or raises Failure");
        var retained = PendingWork.ByIdentity(scenario.Store, Side.Dropbox, scenario.Pair.Roots!.Dropbox.RootNamespaceId, remote.Id).Any();
        scenario.Require(related ? retained && scenario.LocalFiles().ContainsKey(child) :
                !retained && !Directory.Exists(Path.Combine(scenario.LocalRoot, folder)),
            "X1: unrelated volume progress completes the plan; changed child evidence refuses and retains its selection");
    }

    private static void FolderReplacementEvidence(Scenario scenario)
    {
        const string folder = "planned-swap";
        const string replacement = "replacement bytes";
        scenario.LocalFolder(folder);
        scenario.RepairAsync();
        var pairId = scenario.Pair.PairId;
        var generation = scenario.Store.ReadPairExecution(pairId)!.Value.Generation;
        var domain = scenario.Pair.Roots!.LocalVolumeKey();
        // The synced folder becomes a local file: its Dropbox folder needs the live replacement phase.
        scenario.RemoveLocal(folder);
        scenario.LocalFile(folder, replacement);
        var entry = scenario.Local.ReadOneAsync(folder, Scenario.Cancellation).GetAwaiter().GetResult()!;
        void Acquire()
        {
            var localSource = scenario.Store.ReadSource(pairId, Side.Local)!.Value;
            var position = scenario.Store.ReadJournal(domain)!;
            var current = scenario.Local.ReadOneAsync(folder, Scenario.Cancellation).GetAwaiter().GetResult()!;
            scenario.Store.AcquireJournalPage(domain, position, position,
                [(pairId, generation, localSource.Epoch, localSource.Revision, false)],
                [(pairId, new ChangeNotice(Side.Local, ChangeKind.Changed, current.Path, null, current.Id, current, true, false))]);
        }
        // The removal owns the Dropbox folder while it is admitted; a change acquired after that admission must
        // refuse its re-observation, so watch every observation and inject once, at the first one after it.
        var armed = false;
        var injected = 0;
        void Watch() => scenario.AfterFreshRead(Side.Local, folder, () =>
        {
            Watch();
            if (!armed || injected != 0) return;
            injected++;
            Acquire();
        });
        Watch();
        scenario.BeforeLiveValidation = _ =>
        {
            if (injected == 0 &&
                scenario.Dropbox.ReadOneAsync(folder, Scenario.Cancellation).GetAwaiter().GetResult() is { IsFolder: true })
                armed = true;
        };
        scenario.InSession("replace-folder-live", session =>
        {
            session.Host.CatchUpLocalAsync(pairId, generation, Scenario.Cancellation).GetAwaiter().GetResult();
            session.Store.EndWorkPass(pairId);
            var retainedAtRefusal = false;
            // DESIGN-114 §3.6 permits serial follow-up before SyncWorkAsync returns; inspect the first joined round.
            scenario.PassEnded = () =>
            {
                scenario.PassEnded = null;
                var refusal = scenario.ObserveRefusal();
                retainedAtRefusal = refusal.IsCompleted && refusal.Result == LiveAdmissionStaleReason.RelevantEvidenceChanged &&
                    PendingWork.ByIdentity(scenario.Store, Side.Local, domain, entry.Id).Any() &&
                    scenario.LocalFiles().ContainsKey(folder) && scenario.Snapshot().Contains("D:" + folder + "/");
            };
            session.Syncer.SyncWorkAsync(Scenario.Cancellation).GetAwaiter().GetResult();
            var refused = scenario.ObserveRefusal();
            scenario.Require(injected == 1 && scenario.LiveValidations >= 3 &&
                scenario.Store.Issues(pairId, [IssueKind.Failure]).Count == 0,
                "R4: the live replacement re-observes its row after its admission; injected=" + injected +
                " validations=" + scenario.LiveValidations);
            scenario.Require(retainedAtRefusal,
                "R4: a related change acquired after admission refuses the live replacement and retains its selection; reason=" +
                (refused.IsCompleted ? refused.Result.ToString() : "none"));
        });
        for (var pass = 0; pass < 3; pass++)
        {
            if (scenario.DropboxFiles().ContainsKey(folder)) break;
            scenario.InSession("replace-folder-next-opportunity", session =>
            {
                session.Store.EndWorkPass(pairId);
                session.Syncer.SyncWorkAsync(Scenario.Cancellation).GetAwaiter().GetResult();
            });
        }
        scenario.Require(scenario.DropboxFiles().TryGetValue(folder, out var content) &&
            content.SequenceEqual(System.Text.Encoding.UTF8.GetBytes(replacement)) &&
            !PendingWork.ByIdentity(scenario.Store, Side.Local, domain, entry.Id).Any() &&
            scenario.Store.Issues(pairId, [IssueKind.Failure]).Count == 0,
            "R4: the refused replacement converges on its following opportunity");
    }

    private static void CurrentHashProvider(Scenario scenario)
    {
        scenario.LocalFile(FilePath, First);
        scenario.RepairAsync();
        scenario.InSession("hash-provider-clone", session =>
        {
            var failure = new IOException("synthetic current-provider failure");
            var provider = new Massdex.SyncShark.Engine.Tests.TestDoubles.ProxySide(session.Context.Local)
            {
                Hash = (_, _) => Task.FromException<string>(failure)
            };
            var clone = session.Context with { Local = provider };
            var entry = clone.Local.ReadOneAsync(FilePath, Scenario.Cancellation).GetAwaiter().GetResult()!;
            var saved = scenario.Store.SyncedByKey(scenario.Pair.PairId, Side.Local, PathNames.Key(FilePath));
            var proof = clone.Evidence.Read(entry, saved);
            Exception? observed = null;
            try { clone.Evidence.HashAsync(clone.Local, entry, saved, proof, Scenario.Cancellation).GetAwaiter().GetResult(); }
            catch (IOException error) { observed = error; }
            scenario.Require(ReferenceEquals(clone.Evidence, session.Context.Evidence) &&
                provider.Hashes == 1 && ReferenceEquals(observed, failure),
                "X1: a cloned context retains its source cache but hashes through its current provider");
        });
    }

    private static void LastSelectionWake(Scenario scenario, string exit)
    {
        if (exit == "hash-provider-clone")
        {
            CurrentHashProvider(scenario);
            return;
        }
        if (exit is "folder-hash-unrelated" or "folder-hash-change" or "folder-admission-change")
        {
            FolderHashEvidence(scenario, exit != "folder-hash-unrelated", exit == "folder-admission-change");
            return;
        }
        if (exit == "folder-replacement-admission")
        {
            FolderReplacementEvidence(scenario);
            return;
        }
        if (exit == "resolve")
        {
            ResolveWake(scenario);
            return;
        }
        if (exit is "hash-unrelated" or "hash-change" or "attempt-change" or "attempt-unrelated")
        {
            HashEvidenceChange(scenario, exit is "hash-change" or "attempt-change", exit.StartsWith("attempt-", StringComparison.Ordinal) ? 3 : 2);
            return;
        }
        if (exit is "progress" or "progress-false" or "progress-ancestor")
        {
            ProgressPass(scenario, exit);
            return;
        }
        if (exit == "failed-install")
        {
            FailedInstall(scenario);
            return;
        }
        if (exit is "reopen" or "host-stop")
        {
            DrainedHostWake(scenario, exit == "host-stop");
            return;
        }
        if (exit == "prerequisite")
        {
            PrerequisiteWake(scenario);
            return;
        }
        if (exit == "global-resume")
        {
            GlobalResumeRepair(scenario);
            return;
        }
        if (exit == "platform-wake")
        {
            PlatformWakeSelection(scenario);
            return;
        }
        scenario.LocalFile(FilePath, First);
        scenario.RepairAsync();
        if (exit == "host") Console.WriteLine("live-wake-host: initial Repair complete");
        scenario.LocalFile(FilePath, Second, Scenario.T1);
        var generation = scenario.Store.ReadPairExecution(scenario.Pair.PairId)!.Value.Generation;
        scenario.Host.CatchUpLocalAsync(scenario.Pair.PairId, generation, Scenario.Cancellation).GetAwaiter().GetResult();
        var completed = scenario.ObserveCompletion(FilePath, Third);
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opportunities = 0;
        // DESIGN-114 section 3.1: the producer selects again while a lane runs, so only the joined pass end is final.
        scenario.PassEnded = () =>
        {
            if ((exit == "host" ? scenario.EndedWorkPasses >= 2 : Volatile.Read(ref opportunities) > 1) &&
                !completed.IsCompleted) lost.TrySetResult();
        };
        var pausing = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var injected = false;
        var fault = new InvalidDataException("synthetic opportunity failure");
        WorkSelection? abandoned = null;
        AutoSync? auto = null;
        auto = scenario.CreateAutoSync(() => opportunities++, selection =>
        {
            if (selection is null || injected) return;
            injected = true;
            scenario.LocalFile(FilePath, Third, Scenario.T2);
            if (exit == "host") Console.WriteLine("live-wake-host: selected; catching up injected change");
            scenario.Host.CatchUpLocalAsync(scenario.Pair.PairId, generation, Scenario.Cancellation).GetAwaiter().GetResult();
            if (exit == "host") Console.WriteLine("live-wake-host: injected change acquired");
            if (exit != "host") auto!.Kick(scenario.Pair.PairId);
            if (exit == "pass-refusal") throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
            if (exit == "request") throw new EngineRequestException(TextKey.PairDisabled);
            if (exit == "pause") pausing.SetResult(scenario.Host.DrainDispatchAsync(scenario.Pair.PairId,
                () => auto!.PublishPause(scenario.Pair.PairId, true), Scenario.Cancellation));
            if (exit is "stop" or "fault")
            {
                abandoned = selection;
                pausing.SetResult(auto!.StopAsync());
                if (exit == "fault") throw fault;
            }
        });
        try
        {
            if (exit == "host")
            {
                Console.WriteLine("live-wake-host: starting host");
                scenario.Start();
                Console.WriteLine("live-wake-host: host started");
            }
            else auto.Start();
            if (exit == "pause")
            {
                pausing.Task.WaitAsync(Scenario.Cancellation).GetAwaiter().GetResult().GetAwaiter().GetResult();
                scenario.Host.ResumeDispatchAsync(scenario.Pair.PairId,
                    () => auto.PublishPause(scenario.Pair.PairId, false), Scenario.Cancellation).GetAwaiter().GetResult();
                auto.Kick(scenario.Pair.PairId);
            }
            if (exit is "stop" or "fault")
            {
                pausing.Task.WaitAsync(Scenario.Cancellation).GetAwaiter().GetResult().GetAwaiter().GetResult();
                var next = scenario.Store.SelectWork(scenario.Pair.PairId, generation,
                    new PathScope(scenario.Pair, scenario.Store.Pairs(), Scenario.FixtureSettings.DefaultIgnoreRules));
                scenario.Require(next is not null && next.AnchorWorkId == abandoned!.AnchorWorkId &&
                    next.ThroughWorkId > abandoned.ThroughWorkId && !completed.IsCompleted,
                    "F04: shutdown preserves unfinished work and the next pass includes arrivals after " + exit);
                scenario.Store.EndWorkPass(scenario.Pair.PairId);
                return;
            }
            var result = Task.WhenAny(completed, lost.Task).WaitAsync(Scenario.Cancellation).GetAwaiter().GetResult();
            scenario.Require(result == completed,
                "F04: the only newer-work wake opens a fresh pass after " + exit);
            completed.GetAwaiter().GetResult();
            if (exit == "host")
            {
                scenario.Host.PauseAsync(scenario.Pair.PairId, Scenario.Cancellation).GetAwaiter().GetResult();
                scenario.Require(scenario.EndedWorkPasses > 0, "R3: the real host completed its opportunity");
            }
            scenario.Require(System.Text.Encoding.UTF8.GetString(scenario.DropboxFiles()[FilePath]) == Third,
                "F04: new work executes without another notice after " + exit);
        }
        finally
        {
            auto.StopAsync().GetAwaiter().GetResult();
            if (exit == "fault")
            {
                try { scenario.Dispose(); }
                catch (AggregateException error) when (error.InnerExceptions.Count == 1 &&
                    ReferenceEquals(error.InnerExceptions[0], fault)) { }
            }
        }
    }

    private static void PersistentRefusal(Scenario scenario)
    {
        scenario.LocalFile(FilePath, First);
        scenario.RepairAsync();
        scenario.LocalFile(FilePath, Second, Scenario.T1);
        var generation = scenario.Store.ReadPairExecution(scenario.Pair.PairId)!.Value.Generation;
        scenario.Host.CatchUpLocalAsync(scenario.Pair.PairId, generation, Scenario.Cancellation).GetAwaiter().GetResult();
        var saved = scenario.Store.SyncedByKey(scenario.Pair.PairId, Side.Local, PathNames.Key(FilePath))!;
        var entry = scenario.Local.ReadOneAsync(FilePath, Scenario.Cancellation).GetAwaiter().GetResult()!;
        var pending = new PendingEffect(1, Guid.NewGuid().ToString(), generation, PendingKind.LocalDelete,
            PendingPhase.Armed, FilePath, new RemovalProof(entry, "volume", "hash"), saved,
            null, null, null, null, false, null);
        scenario.Store.ArmPending(scenario.Pair.PairId, pending);
        var selected = 0;
        scenario.CreateAutoSync(() => { }, selection => { if (selection is not null) selected++; });
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.PassEnded = () => ended.TrySetResult();
        scenario.Start();
        ended.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        scenario.Host.PauseAsync(scenario.Pair.PairId, Scenario.Cancellation).GetAwaiter().GetResult();
        var retained = PendingWork.ByIdentity(scenario.Store, Side.Local,
            scenario.Pair.Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), saved.LocalId).Any();
        Console.WriteLine($"R3 startup: passes={scenario.EndedWorkPasses}, selected={selected}, retained={retained}");
        scenario.Require(scenario.EndedWorkPasses == 1 && selected == 1,
            "R3: one Start schedules exactly one refusal opportunity");
        scenario.Require(retained, "R3: the finished refusal pass preserves work without rearming");
        ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Host.ResumeAsync(scenario.Pair.PairId, Scenario.Cancellation).GetAwaiter().GetResult();
        ended.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        scenario.Host.PauseAsync(scenario.Pair.PairId, Scenario.Cancellation).GetAwaiter().GetResult();
        Console.WriteLine($"R3 resumed: passes={scenario.EndedWorkPasses}, selected={selected}");
        scenario.Require(scenario.EndedWorkPasses == 2 && selected == 2,
            "R3: one real resume wake opens exactly one further refusal opportunity");
        scenario.Store.SettlePending(scenario.Pair.PairId, pending, StateChange.Empty);
    }

    /// <summary>A127/A131b: one Repair reads a shared job once, and one real notice still finishes beside a job
    /// whose observation is refused, which stays Accepted.</summary>
    private static void ReceiptBesideNotice(Scenario scenario)
    {
        scenario.LocalFile(FilePath, First);
        scenario.RepairAsync();

        var shared = SeedJob(scenario, "shared-job", "live/shared-a", "live/shared-b");
        var reads = JobReads(scenario);
        scenario.RepairAsync();
        scenario.Require(JobReads(scenario) == reads + 1,
            "A127: one Repair over a submission shared by two operations reads the job once");
        scenario.Require(scenario.Store.FolderSubmission(shared).Phase == FolderSubmission.SubmissionPhase.Terminal,
            "A127: the shared job's one receipt is recorded");

        var bad = SeedJob(scenario, "bad-job", "live/bad-job");
        scenario.Account.FailFolderJobChecks(1);
        var generation = scenario.Store.ReadPairExecution(scenario.Pair.PairId)!.Value.Generation;
        scenario.LocalFile(FilePath, Second, Scenario.T1);
        var completed = scenario.ObserveCompletion(FilePath, Second);
        scenario.Host.CatchUpLocalAsync(scenario.Pair.PairId, generation, Scenario.Cancellation)
            .GetAwaiter().GetResult();
        reads = JobReads(scenario);
        var auto = scenario.CreateAutoSync(() => { }, _ => { });
        try
        {
            auto.Start();
            completed.WaitAsync(Scenario.Cancellation).GetAwaiter().GetResult();
        }
        finally { auto.StopAsync().GetAwaiter().GetResult(); }
        scenario.Require(System.Text.Encoding.UTF8.GetString(scenario.DropboxFiles()[FilePath]) == Second,
            "A131b: the real notice's effect completes beside a bad Accepted job");
        scenario.Require(JobReads(scenario) == reads + 1,
            "A126: the opportunity observes the bad job once");
        var kept = scenario.Store.FolderSubmission(bad);
        scenario.Require(kept.Phase == FolderSubmission.SubmissionPhase.Accepted && kept.Cause is not null,
            "A131b: the refused job stays Accepted with its cause");
        scenario.Require(scenario.Store.SelectWork(scenario.Pair.PairId, generation,
                    new PathScope(scenario.Pair, scenario.Store.Pairs(), Scenario.FixtureSettings.DefaultIgnoreRules)) is null,
            "A131b: the notice's selection completed its work");
    }

    /// <summary>A133a: a running join's one provider read is not repeated by the opportunity beside it, the
    /// selection still finishes, and no stale write escapes when the join settles after release.</summary>
    private static void HeldJoinBesideSelection(Scenario scenario)
    {
        scenario.LocalFile(FilePath, First);
        scenario.RepairAsync();
        var submission = SeedJob(scenario, "held-job", "live/held-job");
        var operation = scenario.Store.FolderSubmissionEffects(submission)[0].OperationId;
        var target = new RecoveryTarget(operation,
            scenario.Store.FolderRemoval(scenario.Pair.PairId, operation).Generation, null, null);
        scenario.LocalFile(FilePath, Second, Scenario.T1);
        var generation = scenario.Store.ReadPairExecution(scenario.Pair.PairId)!.Value.Generation;
        scenario.Host.CatchUpLocalAsync(scenario.Pair.PairId, generation, Scenario.Cancellation).GetAwaiter().GetResult();
        var completed = scenario.ObserveCompletion(FilePath, Second);
        var reads = JobReads(scenario);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.BeforeDeleteJobCheck = async () => { entered.TrySetResult(); await release.Task; };
        var join = Task.Run(() =>
        {
            try
            {
                scenario.InSession("held-join", session =>
                    session.Syncer.RecoverAsync(target, RecoveryChoice.Check, Scenario.Cancellation)
                        .GetAwaiter().GetResult());
                return (Exception?)null;
            }
            catch (Exception error) { return error; }
        });
        try
        {
            var gated = true;
            try { Task.WhenAny(entered.Task, join).WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult(); }
            catch (TimeoutException) { gated = false; }
            Check.Require(gated && entered.Task.IsCompleted,
                "A133a: the join reaches its held provider read (reads=" + JobReads(scenario) +
                ", joinCompleted=" + join.IsCompleted + ")");
            var auto = scenario.CreateAutoSync(() => { }, _ => { });
            try
            {
                auto.Start();
                Check.Require(completed.Wait(TimeSpan.FromSeconds(60)),
                    "A133a: the unrelated notice's selection finishes beside the held join");
            }
            finally { auto.StopAsync().GetAwaiter().GetResult(); }
            scenario.Require(System.Text.Encoding.UTF8.GetString(scenario.DropboxFiles()[FilePath]) == Second,
                "A133a: the unrelated notice's selection finishes while the join holds the job");
            scenario.Require(JobReads(scenario) == reads + 1,
                "A133a: the held join's one provider read is not repeated by the opportunity");
        }
        finally
        {
            release.TrySetResult();
            scenario.BeforeDeleteJobCheck = null;
        }
        Check.Require(join.Wait(TimeSpan.FromSeconds(30)),
            "A133a: releasing the join settles the receipt without a stale write escaping");
        scenario.Require(join.Result is null, "A133a: no stale write escapes the released join (" + join.Result + ")");
    }

    /// <summary>Seeds one durable Dropbox folder removal per path, all under one Accepted submission, and returns it.</summary>
    private static string SeedJob(Scenario scenario, string job, params string[] paths)
    {
        var effects = new List<FolderRemovalEffect>();
        foreach (var path in paths)
        {
            var side = new Massdex.SyncShark.Engine.Tests.TestDoubles.FakeSide(Side.Dropbox);
            var entry = side.EnsureFolderAsync(path, Scenario.Cancellation).GetAwaiter().GetResult();
            var row = new Row(PathNames.DropboxKey(path), null, entry, null, [], null,
                new RowContext(false, false, false, null, false, false, true, true));
            using var plan = scenario.Store.OpenFolderPlan(scenario.Pair.PairId, row, Side.Dropbox);
            plan.CaptureAsync(side.ReadAllAsync(path, new ListOptions(Recursive: true, IncludeOutOfScope: true),
                Scenario.Cancellation), Scenario.Cancellation).GetAwaiter().GetResult();
            Check.Require(!plan.Children(Scenario.Cancellation).Any(), "the seeded job folder is empty");
            plan.Complete(true, Scenario.Cancellation);
            var operation = scenario.Store.BeginFolderRemoval(scenario.Pair.PairId, plan, StateSchema.OneRow, Scenario.Cancellation);
            var current = scenario.Store.FolderRemoval(scenario.Pair.PairId, operation);
            scenario.Store.AdvanceFolderRemoval(scenario.Pair.PairId, operation, current.Generation,
                current.Phase, current with { Generation = current.Generation + 1, Cursor = "cursor" });
            effects.AddRange(scenario.Store.ReadyFolderEffects(scenario.Pair.PairId, operation, -1, 1));
        }
        var submission = scenario.Store.ArmFolderSubmission(scenario.Pair.PairId, effects);
        var armed = scenario.Store.FolderSubmission(submission);
        scenario.Store.AdvanceFolderSubmission(armed, armed with
        {
            Generation = armed.Generation + 1, Phase = FolderSubmission.SubmissionPhase.Accepted, JobId = job
        }, []);
        return submission;
    }

    private static int JobReads(Scenario scenario) => scenario.Account.Calls.Count(call =>
        call == "/2/" + Massdex.SyncShark.Engine.Dropbox.DropboxProtocol.DeleteBatchCheck);

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
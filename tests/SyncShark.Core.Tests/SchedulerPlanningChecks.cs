using System.Diagnostics;
using System.Text.Json;
using Massdex.SyncShark;

// SS-D14 scheduling/bulk-read proof: the pump's immutable per-operation descriptor, its
// fail-closed admission of malformed retained state, and the one reviewed batch read that
// binds a real planning pass's selected retained operations.
internal static class SchedulerPlanningChecks
{
    private const int DescriptorOperationCount = 2500;
    private static readonly List<Dictionary<string, object?>> Cases = [];
    private static int _assertions;

    internal static async Task RunAsync(string root)
    {
        var isolated = Path.GetFullPath(root);
        if (Directory.Exists(isolated) && Directory.EnumerateFileSystemEntries(isolated).Any())
            throw new InvalidOperationException("Use a fresh isolated scheduler-planning fixture root.");
        Directory.CreateDirectory(isolated);
        Cases.Clear();
        _assertions = 0;
        await CaseAsync("A-descriptor-parse-bound",
            "One descriptor per operation parses each member expected state once, independent of the active window",
            DescriptorParseBoundChecksAsync);
        await CaseAsync("B-descriptor-admission",
            "Malformed member state stays non-independent and a non-object source state keeps its byte refusal",
            DescriptorAdmissionChecksAsync);
        await CaseAsync("C-plan-bound-batch-read",
            "The real planning pass binds its selected operations through one reviewed batch read and refuses empty, cancelled, missing, foreign and duplicate identities",
            () => PlanBoundBatchReadChecksAsync(isolated));
        var report = new Dictionary<string, object?>
        {
            ["generated_by"] = "SchedulerPlanningChecks (tests/SyncShark.Core.Tests)",
            ["fixture_root"] = isolated,
            ["assertions"] = _assertions,
            ["cases"] = Cases
        };
        await File.WriteAllTextAsync(Path.Combine(isolated, "scheduler-planning-checks.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS scheduler planning: " + _assertions + " assertions; descriptor parsing bounded by" +
            " total members at three active windows, malformed state never permissive, and the real plan bound" +
            " through one reviewed batch read with empty, cancelled, missing, foreign and duplicate cases.");
    }

    private static async Task CaseAsync(string id, string label, Func<Task> body)
    {
        var before = _assertions;
        try
        {
            await body();
            Cases.Add(new Dictionary<string, object?>
            {
                ["case"] = id, ["label"] = label, ["result"] = "PASS", ["assertions"] = _assertions - before
            });
            Console.WriteLine("PASS " + id + ": " + label + " (" + (_assertions - before) + " assertions)");
        }
        catch (Exception exception)
        {
            Cases.Add(new Dictionary<string, object?>
            {
                ["case"] = id, ["label"] = label, ["result"] = "FAIL", ["assertions"] = _assertions - before,
                ["error"] = exception.GetType().Name + ": " + exception.Message
            });
            Console.WriteLine("FAIL " + id + ": " + exception.GetType().Name + ": " + exception.Message);
            throw;
        }
    }

    // The descriptor must parse each member expected state once per operation, however many
    // active-window comparisons that operation takes part in. The same immutable 2500-operation
    // batch is pumped at three window sizes: the comparison count grows with the window, the
    // parse count must not.
    private static async Task DescriptorParseBoundChecksAsync()
    {
        var operations = Enumerable.Range(0, DescriptorOperationCount).Select(index =>
            Operation("file-" + index, $"dir-{index % 64}/file-{index:D4}.bin")).ToArray();
        var members = operations.Sum(operation => operation.Members.Count);
        foreach (var window in new[] { 1, 4, 16 })
        {
            var parses = 0;
            var normalizations = 0;
            var accepted = 0;
            var completed = false;
            SyncAuthorityOperationFootprint.MemberStateParsedForTest = _ => { parses++; };
            SyncAuthorityOperationFootprint.MemberPathNormalizedForTest = _ => { normalizations++; };
            try
            {
                using var scope = SyncAuthorityOperationScheduler.OverrideMaximumInFlightForTest(window);
                completed = await SyncAuthorityOperationScheduler.RunAsync(operations, item => item,
                    item => Task.FromResult(item.OperationId),
                    (_, _) => { accepted++; return Task.FromResult(true); }, CancellationToken.None);
            }
            finally
            {
                SyncAuthorityOperationFootprint.MemberStateParsedForTest = null;
                SyncAuthorityOperationFootprint.MemberPathNormalizedForTest = null;
            }
            Require(completed && accepted == operations.Length,
                "the real pump completed every prepared operation at window " + window);
            Require(parses == members,
                "descriptor parsing is bounded by total members, not active-window comparisons: window " +
                window + " parsed " + parses + " of " + members + " members");
            Require(normalizations <= members,
                "descriptor path normalization is bounded by total members at window " + window);
        }
        Console.WriteLine("PASS descriptor parsing bounded at " + members + " member states for " +
            DescriptorOperationCount + " operations at windows 1, 4 and 16");
    }

    // Malformed retained member state stays non-independent without becoming permissive, and the
    // non-object source state keeps the previous byte-estimate refusal instead of a zero estimate.
    private static async Task DescriptorAdmissionChecksAsync()
    {
        var healthy = Operation("healthy", "healthy/file.bin");
        var damaged = Damaged("damaged", "damaged/file.bin");
        var active = 0;
        var peak = 0;
        var accepted = 0;
        using (var scope = SyncAuthorityOperationScheduler.OverrideMaximumInFlightForTest(4))
        {
            await SyncAuthorityOperationScheduler.RunAsync(new[] { healthy, damaged }, item => item,
                async item =>
                {
                    var count = Interlocked.Increment(ref active);
                    int observed;
                    do { observed = Volatile.Read(ref peak); }
                    while (observed < count && Interlocked.CompareExchange(ref peak, count, observed) != observed);
                    try { await Task.Delay(20); return item.OperationId; }
                    finally { Interlocked.Decrement(ref active); }
                }, (_, _) => { accepted++; return Task.FromResult(true); }, CancellationToken.None);
        }
        Require(accepted == 2 && active == 0 && peak == 1,
            "invalid member state is never admitted beside another effect and the pump still drains it");

        var nonObject = SyncAuthorityOperationFootprint.Create(Operation("non-object", "non-object/file.bin") with
        {
            Members =
            [new("source-non-object", "pair", "object-non-object", "source", "remote", null, null, null,
                "non-object/file.bin", "[]"),
             new("target-non-object", "pair", "object-non-object", "destination", "local", null, null, null,
                "non-object/file.bin", "{\"present\":false}")]
        });
        Require(!nonObject.FileEligible, "a non-object member state is not file-eligible");
        var raised = false;
        try { nonObject.ExpectedBytes(); }
        catch (InvalidOperationException) { raised = true; }
        Require(raised, "a non-object source state keeps the previous byte-estimate refusal instead of zero bytes");

        var readable = SyncAuthorityOperationFootprint.Create(healthy);
        Require(readable.FileEligible && readable.ExpectedBytes() == 0,
            "a readable source state stays eligible with its exact byte estimate");
        Require(!readable.DirectoryEligible && readable.OperationId == healthy.OperationId,
            "a file transfer is never directory-create eligible");
        Require(!SyncAuthorityOperationFootprint.Independent(readable,
                SyncAuthorityOperationFootprint.Create(damaged)),
            "descriptor comparison keeps the malformed-operation barrier");
    }

    private static async Task PlanBoundBatchReadChecksAsync(string isolated)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The real file-identity planning fixture requires Windows.");

        // 1. A real planning pass binds its selected operations through exactly one reviewed batch read.
        var bound = await BuildPlanningFixtureAsync(Path.Combine(isolated, "bound"));
        var reads = new List<(int Index, string Id)>();
        var connections = 0;
        var watch = Stopwatch.StartNew();
        SyncAuthorityPairRunner.PlannedOperationBatchReadForTest = (index, id) =>
        {
            reads.Add((index, id));
            return Task.CompletedTask;
        };
        SyncAuthorityDatabase.TimingForTest = (phase, _) =>
        {
            if (phase == "connection-open") connections++;
        };
        SyncAuthorityPairPlanningResult plan;
        try
        {
            plan = await bound.Runner.PlanBoundAsync(bound.ObservationSet, "scheduler-planning-observation");
        }
        finally
        {
            SyncAuthorityPairRunner.PlannedOperationBatchReadForTest = null;
            SyncAuthorityDatabase.TimingForTest = null;
        }
        watch.Stop();
        Require(plan.Operations.Count > 0, "the real planning pass selected retained operations to bind");
        var orderedIds = plan.Operations.Select(item => item.Operation.OperationId)
            .Distinct(StringComparer.Ordinal).ToArray();
        Require(reads.Count == orderedIds.Length && reads.Select(item => item.Index)
                .SequenceEqual(Enumerable.Range(0, reads.Count)),
            "one reviewed batch read covers every selected identity exactly once, in order");
        Require(reads.Select(item => item.Id).SequenceEqual(orderedIds),
            "the batch read receives exactly the selected distinct identities in the planned order");
        var reproduced = new List<SyncAuthorityOperationSnapshot>();
        foreach (var planned in plan.Operations)
            reproduced.Add(await bound.Authority.LoadOperationAsync(planned.Operation.OperationId));
        Require(reproduced.Select(item => item.OperationId).SequenceEqual(reads.Select(item => item.Id)),
            "each planned operation keeps the identity the batch read returned for it, in the selected order");
        Require(JsonSerializer.Serialize(reproduced) == JsonSerializer.Serialize(
                plan.Operations.Select(item => item.Operation).ToArray()),
            "the batch binding returns exactly the per-identity snapshots the previous per-operation read returned");
        Require(!plan.Decisions.Any(decision => plan.Operations.Any(operation =>
                string.Equals(operation.RelativePath, decision.RelativePath, StringComparison.OrdinalIgnoreCase))),
            "each planned path is either a decision or a retained operation, never both");
        Console.WriteLine("scheduler-planning bound " + plan.Operations.Count + " operations through " +
            reads.Count + " batch identities in " + connections + " authority connections and " +
            watch.ElapsedMilliseconds + " ms");

        // 2. A pass whose selected paths were all already attempted binds nothing and reads nothing.
        var filtered = await BuildPlanningFixtureAsync(Path.Combine(isolated, "filtered"));
        var attempted = plan.Operations.Select(item => item.RelativePath)
            .Concat(filtered.Paths).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filteredReads = 0;
        SyncAuthorityPairRunner.PlannedOperationBatchReadForTest = (_, _) =>
        {
            filteredReads++;
            return Task.CompletedTask;
        };
        SyncAuthorityPairPlanningResult empty;
        try
        {
            empty = await filtered.Runner.PlanBoundAsync(filtered.ObservationSet,
                "scheduler-planning-filtered", pathsAttemptedThisRun: attempted);
        }
        finally { SyncAuthorityPairRunner.PlannedOperationBatchReadForTest = null; }
        Require(empty.Operations.Count == 0 && filteredReads == 0,
            "a pass whose selected paths were already attempted binds no operation and reads no retained batch");

        // 3. A cancelled planning pass reads no retained batch and returns no partial plan.
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelReads = 0;
        SyncAuthorityPairRunner.PlannedOperationBatchReadForTest = (_, _) =>
        {
            cancelReads++;
            return Task.CompletedTask;
        };
        var cancelObserved = false;
        try
        {
            await bound.Runner.PlanBoundAsync(bound.ObservationSet, "scheduler-planning-cancelled",
                cancellationToken: cancelled.Token);
        }
        catch (OperationCanceledException) { cancelObserved = true; }
        finally { SyncAuthorityPairRunner.PlannedOperationBatchReadForTest = null; }
        Require(cancelObserved && cancelReads == 0,
            "a cancelled planning pass opens no retained batch read and returns no partial plan");

        var anyId = plan.Operations[0].Operation.OperationId;
        var batchCancelled = false;
        try { await bound.Authority.LoadOperationsAsync([anyId], cancelled.Token); }
        catch (OperationCanceledException) { batchCancelled = true; }
        Require(batchCancelled, "a pre-cancelled retained batch read returns no partial result");

        // 4. A selected identity the retained authority does not hold is refused, not skipped.
        var missingRefused = false;
        try { await bound.Authority.LoadOperationsAsync([anyId, "not-a-retained-operation"]); }
        catch (InvalidDataException) { missingRefused = true; }
        Require(missingRefused, "the reviewed batch read refuses a missing retained identity with no partial success");

        // 5. Duplicate, foreign and ambiguous identities exercise the production binding decision
        //    itself; only the input list is supplied by this check.
        var loaded = await bound.Authority.LoadOperationsAsync([anyId]);
        var duplicated = new[] { Planned("duplicate-a.txt", anyId), Planned("duplicate-b.txt", anyId) };
        var distinct = SyncAuthorityPairRunner.DistinctOperationIds(duplicated);
        Require(distinct.Count == 1 && distinct[0] == anyId, "a repeated planned identity is read once");
        var rebound = SyncAuthorityPairRunner.BindPlannedOperations(duplicated, loaded);
        Require(rebound.Count == 2 && rebound[0].RelativePath == "duplicate-a.txt" &&
                rebound[1].RelativePath == "duplicate-b.txt" &&
                ReferenceEquals(rebound[0].Operation, loaded[0]) &&
                ReferenceEquals(rebound[1].Operation, loaded[0]),
            "both duplicate outcomes keep their own order and path and reuse the one loaded snapshot");
        var foreignRefused = false;
        try { SyncAuthorityPairRunner.BindPlannedOperations([Planned("foreign.txt", "operation-foreign")], loaded); }
        catch (InvalidDataException) { foreignRefused = true; }
        Require(foreignRefused, "an identity outside the read batch is refused instead of bound positionally");
        var ambiguousRefused = false;
        try
        {
            SyncAuthorityPairRunner.BindPlannedOperations([Planned("ambiguous.txt", anyId)],
                [loaded[0], loaded[0]]);
        }
        catch (InvalidDataException) { ambiguousRefused = true; }
        Require(ambiguousRefused, "a batch that returns one identity twice is refused as ambiguous");
    }

    // One real isolated authority, one legacy pair and the exact observation set a real planning
    // pass consumes. Only the remote provider is simulated; the authority, planner, runner and
    // retained reads are the product's own.
    private sealed record PlanningFixture(SyncAuthorityPairRunner Runner,
        SyncAuthorityPairObservationSet ObservationSet, SyncAuthorityDatabase Authority,
        IReadOnlyList<string> Paths);

    private static async Task<PlanningFixture> BuildPlanningFixtureAsync(string fixtureRoot)
    {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Directory.CreateDirectory(fixtureRoot);
        var localRoot = Path.Combine(fixtureRoot, "pair");
        Directory.CreateDirectory(localRoot);
        var paths = new List<string>();
        for (var index = 0; index < 3; index++)
        {
            var name = $"upload-{index}.txt";
            await File.WriteAllTextAsync(Path.Combine(localRoot, name), "local payload " + index);
            paths.Add(name);
        }
        Directory.CreateDirectory(Path.Combine(localRoot, "folder-0"));
        paths.Add("folder-0");
        var remote = new FakeSyncRemoteStore();
        for (var index = 0; index < 2; index++)
        {
            var name = $"download-{index}.txt";
            remote.Put(name, "remote payload " + index);
            paths.Add(name);
        }
        PilotDirectoryIdentity identity;
        using (var lease = PilotDirectoryLease.Open(localRoot)) identity = lease.Identity;
        const string installationId = "installation-scheduler-planning";
        const string namespaceId = "scheduler-planning-control";
        SyncLocalControlNamespaceBinding control;
        using (var lease = PilotDirectoryLease.Open(localRoot, identity))
            control = SyncControlNamespace.BindLocalForTest(lease, installationId, namespaceId);
        var pair = new SyncPair("pair-scheduler-planning", "Documents", localRoot,
            identity.VolumeSerialNumber, identity.FileId, "/Documents", "id:documents",
            ControlNamespace: new SyncControlNamespaceBinding(installationId, namespaceId, control));
        var authority = new SyncAuthorityDatabase(Path.Combine(fixtureRoot, "authority.db"));
        await authority.InitializeAsync(installationId, "profile-scheduler-planning", "scheduler-planning", "{}");
        await authority.RegisterPairAsync(pair.Id, pair.PolicyRevision);
        await authority.RegisterRootBindingAsync("root-scheduler-local", pair.Id, "local", 1,
            JsonSerializer.Serialize(new
            {
                volumeSerial = identity.VolumeSerialNumber.ToString("x8"),
                fileId = identity.FileId.ToString("x16")
            }), localRoot);
        await authority.RegisterRootBindingAsync("root-scheduler-dropbox", pair.Id, "dropbox", 1,
            "{\"folderId\":\"id:documents\"}", "/Documents");
        var options = new SyncAuthorityPlannerOptions(1, "profile-scheduler-planning", namespaceId, namespaceId,
            EnableDeletionEffects: true, EnableMoveEffects: true, DeletionMinimumInterval: TimeSpan.Zero);
        var admission = await authority.CreateAcceptedCommandAdmissionAsync(null, "run_pair", pair.Id, null, null,
            "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await authority.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
        var runner = new SyncAuthorityPairRunner(pair, remote, authority, "root-scheduler-local",
            "root-scheduler-dropbox", options, new PilotRunControl(), admission);
        var observer = new SyncAuthorityObservationCoordinator(pair, remote, authority,
            "root-scheduler-local", "root-scheduler-dropbox");
        await observer.ObserveAsync("initial");
        var observed = await observer.ObserveAsync("initial");
        await new SyncAuthorityInitialPlanner(authority).AdoptAsync(pair.Id);
        var observationSet = await authority.LoadPlanningObservationSetAsync(pair.Id,
            options.ConfigurationRevision, "root-scheduler-local", "root-scheduler-dropbox",
            observed.LocalScan.ScanGenerationId, observed.DropboxScan.ScanGenerationId);
        return new PlanningFixture(runner, observationSet, authority, paths);
    }

    private static SyncAuthorityPlannerOutcome Planned(string path, string? operationId) =>
        new(path, "object-" + path, SyncAuthorityPlannerOutcomeKind.Operation, operationId, null, null,
            "scheduler-planning synthetic binding input");

    private static SyncAuthorityOperationSnapshot Operation(string id, string path) => new(id,
        SyncAuthorityOperationKind.CreateLocal, SyncAuthorityOperationStatus.Ready, "pair", "object-" + id,
        1, "profile", "idempotency-" + id, "{}", null, null,
        [new("source-" + id, "pair", "object-" + id, "source", "remote", null, null, null, path,
             "{\"objectKind\":\"file\"}"),
         new("target-" + id, "pair", "object-" + id, "destination", "local", null, null, null, path,
             "{\"present\":false}")],
        [], [], [], [], []);

    private static SyncAuthorityOperationSnapshot Damaged(string id, string path)
    {
        var healthy = Operation(id, path);
        return healthy with
        {
            Members = healthy.Members.Select(member => member with { ExpectedStateJson = "invalid-json" })
                .ToArray()
        };
    }

    private static void Require(bool condition, string name)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(name);
    }
}

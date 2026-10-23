using Massdex.SyncShark;
using System.Reflection;
using System.Text;
using System.Text.Json;

// CC3F-3: the actual SQLite + shared coordinator checks for the one shared cloud conflict graph.
// Every case drives the REAL retained rows through the REAL coordinator plan/execute entry points and the
// REAL shared cloud update and copy engines. Provider input comes from a scripted ISyncRemoteStore which
// supplies provider input only and never replaces the coordinator, the authority database, the allocation
// policy or the runner routing. The four ordered provider combinations and the same-provider distinct
// account row are enumerated as DATA. STAGED AND UNVERIFIED: not compiled or run in this packet.
//
internal static class CloudConflictExecutionChecks
{
    internal const string InstallationId = "installation-cloud-conflict-execution-318";
    internal const string SourceRevision = "cloud-conflict-execution-318";
    internal const string ProfileId = "profile-cloud-conflict-execution-318";
    internal const string Path = "shared/notes.txt";
    private static int _assertions;
    internal static string Root = "";

    internal static async Task RunAsync(string root)
    {
        Root = System.IO.Path.GetFullPath(root);
        if (Directory.Exists(Root) && Directory.EnumerateFileSystemEntries(Root).Any())
            throw new InvalidOperationException("Use a fresh isolated cloud conflict execution fixture root.");
        Directory.CreateDirectory(Root);
        _assertions = 0;
        Console.WriteLine("CC3F cloud conflict route entered");
        await CaseAsync("A-ordered-provider-matrix", OrderedProviderMatrixAsync);
        await CaseAsync("B-keep-left-and-keep-right", ResolutionsAsync);
        await CaseAsync("C-stale-and-crossed-authority", StaleAndCrossedAsync);
        await CaseAsync("D-changed-source-and-occupied-target", ChangedAndOccupiedAsync);
        await CaseAsync("E-unknown-receipt-and-restart", UnknownReceiptRestartAsync);
        await CaseAsync("F-cancellation-and-repeat", CancellationAndRepeatAsync);
        await CaseAsync("G-public-and-queued-runner", RunnerRoutingAsync);
        await CaseAsync("H-target-upload-allocation", UploadAllocationAsync);
        Console.WriteLine("PASS cloud conflict execution: " + _assertions + " assertions");
    }

    private static async Task CaseAsync(string id, Func<Task> body)
    {
        var before = _assertions;
        try
        {
            await body();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine("FAIL " + id + ": " + error.GetType().FullName + ": " + error.Message);
            throw;
        }
        Console.WriteLine("PASS " + id + " (" + (_assertions - before) + " assertions)");
    }

    internal static void Assert(bool condition, string label)
    {
        _assertions++;
        if (condition) return;
        throw new InvalidDataException("CC3F assertion failed: " + label);
    }

    private static async Task ExpectRefusalAsync<T>(Func<Task> body, string message, string label)
        where T : Exception
    {
        try
        {
            await body();
        }
        catch (T error) when (error.Message.Contains(message, StringComparison.Ordinal))
        {
            Assert(true, label + ": refused with the exact exception type and reason");
            return;
        }
        throw new InvalidDataException("CC3F assertion failed: " + label + " was not refused");
    }

    internal static IEnumerable<(string Label, SyncEndpointKind Left, SyncEndpointKind Right)> Matrix() => new[]
    {
        ("dropbox-dropbox", SyncEndpointKind.Dropbox, SyncEndpointKind.Dropbox),
        ("dropbox-drive", SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive),
        ("drive-dropbox", SyncEndpointKind.GoogleDrive, SyncEndpointKind.Dropbox),
        ("drive-drive", SyncEndpointKind.GoogleDrive, SyncEndpointKind.GoogleDrive)
    };

    private static async Task OrderedProviderMatrixAsync()
    {
        foreach (var row in Matrix())
        {
            await using (var site = await ConflictFixture.CreateAsync(row.Label, row.Left, row.Right))
            {
                var keepRight = await site.Coordinator.PlanAsync(site.ConflictId, site.Generation,
                    SyncAuthorityConflictResolutionKind.KeepDropbox);
                Assert(keepRight.ConflictGeneration == site.Generation && keepRight.ConflictCopyPath is null,
                    row.Label + ": keep-right carries the retained generation and plans no visible copy");
                var rightResult = await site.Coordinator.ExecuteAsync(keepRight, site.Worker);
                Assert(rightResult.Status == SyncAuthorityOperationStatus.Completed,
                    row.Label + ": keep-right completed, observed " + rightResult.Status + " " + rightResult.Reason);
                Assert(site.Left.Updates == 1 && site.Right.Copies.Count == 0 && site.Right.Updates == 0,
                    row.Label + ": keep-right issued exactly one left-side replacement and no copy");
                Assert(site.Left.RecoveryRetained == 1 && site.Right.RecoveryRetained == 0 &&
                       site.Left.BytesAt(Path).SequenceEqual(site.Right.BytesAt(Path)),
                    row.Label + ": the losing left predecessor was retained and right bytes won");
            }
            await using (var both = await ConflictFixture.CreateAsync(row.Label + "-both", row.Left, row.Right))
            {
                var preserve = await both.Coordinator.PlanAsync(both.ConflictId, both.Generation,
                    SyncAuthorityConflictResolutionKind.PreserveBoth);
                Assert(preserve.PreserveOperationId is not null && preserve.ConflictCopyPath is not null,
                    row.Label + ": preserve-both plans one visible copy prerequisite");
                var bothResult = await both.Coordinator.ExecuteAsync(preserve, both.Worker);
                Assert(bothResult.Status == SyncAuthorityOperationStatus.Completed,
                    row.Label + ": preserve-both completed, observed " + bothResult.Status + " " + bothResult.Reason);
                Assert(both.Right.Copies.Count == 1 && both.Right.Copies[0] == preserve.ConflictCopyPath,
                    row.Label + ": exactly one create-only copy was issued at the deterministic path");
                Assert(both.Right.Updates == 1 && both.Right.RecoveryRetained == 1,
                    row.Label + ": the shared update replaced the right target once and retained it");
            }
        }
    }

    private static async Task UploadAllocationAsync()
    {
        foreach (var winner in new[] { SyncAuthorityConflictResolutionKind.KeepLocal,
                     SyncAuthorityConflictResolutionKind.KeepDropbox })
        {
            await using var site = await ConflictFixture.CreateAsync("allocation-" + winner,
                SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive, simpleUploadLimit: 8);
            var plan = await site.Coordinator.PlanAsync(site.ConflictId, site.Generation, winner);
            var operation = await site.Db.LoadOperationAsync(plan.OperationId);
            var target = winner == SyncAuthorityConflictResolutionKind.KeepLocal
                ? site.Endpoints.Right : site.Endpoints.Left;
            Assert(operation.Allocations.Count(item => item.Kind == SyncAuthorityAllocationKind.UploadSession) == 1,
                winner + ": retained nonempty source size reserves the target-required upload session");
            Assert(operation.Allocations.Single(item => item.Kind == SyncAuthorityAllocationKind.UploadSession)
                       .IntendedParentIdentity == target.RootBindingId,
                winner + ": the upload allocation names the losing target's exact root");
            Assert(site.Left.Updates == 0 && site.Right.Updates == 0,
                winner + ": allocation planning makes no provider request");
        }
    }

    private static async Task RunnerRoutingAsync()
    {
        foreach (var row in Matrix())
        {
            await using var direct = await ConflictFixture.CreateAsync(row.Label + "-direct", row.Left, row.Right);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var resolved = await direct.Runner().ResolveConflictAsync(Path,
                SyncAuthorityConflictResolutionKind.KeepDropbox, direct.ConflictId, direct.Generation,
                deadline.Token);
            Assert(resolved.Decisions.Single().Kind == SyncDecisionKind.Recovered &&
                   direct.Left.Updates == 1 && direct.Right.Updates == 0 &&
                   direct.Left.RecoveryRetained == 1,
                row.Label + ": public runner keep-right uses the shared left replacement and recovery");
            Assert((await direct.Db.LoadConflictAsync(direct.ConflictId)).Status == "resolved",
                row.Label + ": public runner closed the exact conflict generation");

            await using var queued = await ConflictFixture.CreateAsync(row.Label + "-queued", row.Left, row.Right);
            var plan = await queued.Coordinator.PlanAsync(queued.ConflictId, queued.Generation,
                SyncAuthorityConflictResolutionKind.PreserveBoth);
            var recovered = await queued.Runner().RecoverAsync(deadline.Token);
            Assert((await queued.Db.LoadOperationAsync(plan.PreserveOperationId!)).Status ==
                       SyncAuthorityOperationStatus.Completed &&
                   (await queued.Db.LoadOperationAsync(plan.OperationId)).Status ==
                       SyncAuthorityOperationStatus.Completed,
                row.Label + ": queued runner completed the copy prerequisite before the primary update");
            Assert(queued.Right.Copies.Count == 1 && queued.Right.Updates == 1 &&
                   queued.Right.RecoveryRetained == 1 && queued.Left.Updates == 0 &&
                   queued.Left.BytesAt(Path).SequenceEqual(queued.Right.BytesAt(Path)),
                row.Label + ": queued effects target only their retained endpoint and preserve the losing bytes");
            Assert((await queued.Db.LoadConflictAsync(queued.ConflictId)).Status == "resolved",
                row.Label + ": queued completion closed the exact conflict generation");
            await queued.Runner().RecoverAsync(deadline.Token);
            Assert(queued.Right.Copies.Count == 1 && queued.Right.Updates == 1,
                row.Label + ": repeated recovery never duplicates completed conflict effects");
        }
    }

    private static async Task ResolutionsAsync()
    {
        await using var site = await ConflictFixture.CreateAsync("resolutions", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var keepLocal = await site.Coordinator.PlanAsync(site.ConflictId, site.Generation,
            SyncAuthorityConflictResolutionKind.KeepLocal);
        var result = await site.Coordinator.ExecuteAsync(keepLocal, site.Worker);
        Assert(result.Status == SyncAuthorityOperationStatus.Completed,
            "keep-left completed through the shared update, observed " + result.Status);
        Assert(site.Right.Updates == 1 && site.Right.Copies.Count == 0,
            "keep-left replaced the logical-right target once and copied nothing");
        Assert(site.Right.BytesAt(Path).SequenceEqual(site.Left.BytesAt(Path)),
            "keep-left converged the logical-right target onto the logical-left bytes");
        Assert(site.Left.Downloads > 0 && site.Right.Downloads == 0,
            "the shared update read the logical-left endpoint, not any local folder");
    }

    private static async Task StaleAndCrossedAsync()
    {
        await using var site = await ConflictFixture.CreateAsync("stale-crossed", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        await ExpectRefusalAsync<InvalidOperationException>(() => site.Coordinator.PlanAsync(site.ConflictId, site.Generation + 1,
            SyncAuthorityConflictResolutionKind.KeepLocal), "selected conflict generation is stale", "a stale conflict generation");
        Assert(site.Left.Updates == 0 && site.Right.Updates == 0 && site.Right.Copies.Count == 0,
            "a stale generation issued no effect of any kind");
        await ExpectRefusalAsync<SyncAuthorityCloudConflictCopyException>(() => site.ExecuteWithCrossedAuthorityAsync(),
            "authority names another provider account", "a crossed account or root conflict authority");
        Assert(site.Right.Updates == 0 && site.Right.Copies.Count == 0,
            "a crossed authority issued no copy and no update");
    }

    private static async Task ChangedAndOccupiedAsync()
    {
        await using var site = await ConflictFixture.CreateAsync("changed-occupied", SyncEndpointKind.GoogleDrive,
            SyncEndpointKind.Dropbox);
        var plan = await site.Coordinator.PlanAsync(site.ConflictId, site.Generation,
            SyncAuthorityConflictResolutionKind.PreserveBoth);
        site.Right.Seed(plan.ConflictCopyPath!, "id-occupied", "rev-occupied", Encoding.UTF8.GetBytes("occupied"));
        var blocked = await site.Coordinator.ExecuteAsync(plan, site.Worker);
        Assert(blocked.Status == SyncAuthorityOperationStatus.Blocked && site.Right.Copies.Count == 0,
            "an occupied deterministic copy path blocks with zero copy requests, observed " + blocked.Status);
        await using var changed = await ConflictFixture.CreateAsync("changed-source", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var changedPlan = await changed.Coordinator.PlanAsync(changed.ConflictId, changed.Generation,
            SyncAuthorityConflictResolutionKind.PreserveBoth);
        changed.Right.MutateSourceRevision();
        await ExpectRefusalAsync<SyncAuthorityCloudConflictCopyException>(
            () => changed.Coordinator.ExecuteAsync(changedPlan, changed.Worker),
            "no longer carries the exact retained identity, revision", "changed source revision");
        Assert(changed.Right.Copies.Count == 0 && changed.Right.Updates == 0,
            "a changed source revision is refused before any request");
    }

    private static async Task UnknownReceiptRestartAsync()
    {
        await using var site = await ConflictFixture.CreateAsync("unknown-restart", SyncEndpointKind.Dropbox,
            SyncEndpointKind.Dropbox);
        var plan = await site.Coordinator.PlanAsync(site.ConflictId, site.Generation,
            SyncAuthorityConflictResolutionKind.PreserveBoth);
        site.Right.FailAfterApplyOnCopy = true;
        var unknown = await site.Coordinator.ExecuteAsync(plan, site.Worker);
        Assert(unknown.Status == SyncAuthorityOperationStatus.EffectUnknown && site.Right.Copies.Count == 1,
            "a lost copy response is unknown with exactly one request, observed " + unknown.Status);
        var before = site.Right.Copies.Count;
        var replay = await site.Coordinator.ExecuteAsync(plan, site.Worker);
        Assert(site.Right.Copies.Count == before && replay.Status != SyncAuthorityOperationStatus.Completed,
            "an unverified retained copy is never replayed and never completed from absence");
        await using var known = await ConflictFixture.CreateAsync("known-restart", SyncEndpointKind.GoogleDrive,
            SyncEndpointKind.Dropbox);
        var knownPlan = await known.Coordinator.PlanAsync(known.ConflictId, known.Generation,
            SyncAuthorityConflictResolutionKind.PreserveBoth);
        SyncAuthorityCloudConflictCopyExecutor.PhaseForTest = phase =>
        {
            if (phase == "after-receipt-before-readback") throw new IOException("CC3F injected receipt boundary");
        };
        try
        {
            await ExpectRefusalAsync<IOException>(() => known.Coordinator.ExecuteAsync(knownPlan, known.Worker),
                "CC3F injected receipt boundary", "known-success interruption");
        }
        finally { SyncAuthorityCloudConflictCopyExecutor.PhaseForTest = null; }
        var retained = await known.Db.LoadOperationAsync(knownPlan.PreserveOperationId!);
        Assert(retained.Status == SyncAuthorityOperationStatus.EffectObserved && known.Right.Copies.Count == 1,
            "the real provider receipt was persisted before the injected interruption");
        var restarted = new SyncAuthorityCloudConflictCopyExecutor(
            new SyncAuthorityDatabase(known.Db.DatabasePath), known.Context);
        var copyIndex = known.Right.Entries.FindIndex(item => item.RelativePath == knownPlan.ConflictCopyPath);
        var originalCopy = known.Right.Entries[copyIndex];
        foreach (var replacedCopy in new[] { originalCopy with { Id = "different-object-same-bytes" },
                     originalCopy with { Revision = "different-revision-same-bytes" } })
        {
            known.Right.Entries[copyIndex] = replacedCopy;
            var mismatch = await restarted.ReconcileAsync(knownPlan.PreserveOperationId!, known.Worker);
            Assert(mismatch.Status != SyncAuthorityOperationStatus.Completed && known.Right.Copies.Count == 1 &&
                   known.Right.Updates == 0,
                "matching bytes with a different identity or revision never satisfy the retained copy receipt");
        }
        known.Right.Entries[copyIndex] = originalCopy;
        SyncAuthorityCloudConflictCopyExecutor.PhaseForTest = phase =>
        {
            if (phase == "after-membership-before-auxiliary-commit")
                throw new IOException("CC3F injected membership boundary");
        };
        try
        {
            await ExpectRefusalAsync<IOException>(
                () => restarted.ReconcileAsync(knownPlan.PreserveOperationId!, known.Worker),
                "CC3F injected membership boundary", "partial durable binding interruption");
        }
        finally { SyncAuthorityCloudConflictCopyExecutor.PhaseForTest = null; }
        var resumed = await restarted.ReconcileAsync(knownPlan.PreserveOperationId!, known.Worker);
        Assert(resumed.Status == SyncAuthorityOperationStatus.Completed && known.Right.Copies.Count == 1,
            "a new executor resumes exact known-success readback and commit without copying again");
        var complete = await known.Coordinator.ExecuteAsync(knownPlan, known.Worker);
        Assert(complete.Status == SyncAuthorityOperationStatus.Completed && known.Right.Updates == 1,
            "the primary replacement starts only after preservation completed");
        var repeatComplete = await known.Coordinator.ExecuteAsync(knownPlan, known.Worker);
        Assert(repeatComplete.Status == SyncAuthorityOperationStatus.Completed && known.Right.Updates == 1 &&
               known.Right.Copies.Count == 1,
            "repeating a completed preserve-both resolution is a no-effect success");
    }

    private static async Task CancellationAndRepeatAsync()
    {
        await using var site = await ConflictFixture.CreateAsync("cancel-repeat", SyncEndpointKind.GoogleDrive,
            SyncEndpointKind.Dropbox);
        var plan = await site.Coordinator.PlanAsync(site.ConflictId, site.Generation,
            SyncAuthorityConflictResolutionKind.PreserveBoth);
        using var cancellation = new CancellationTokenSource();
        site.Right.DuringCopy = _ => cancellation.Cancel();
        var cancelled = false;
        try
        {
            await site.Coordinator.ExecuteAsync(plan, site.Worker, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        Assert(cancelled, "the copy observed its cancellation token");
        site.Right.DuringCopy = null;
        Assert(site.Right.Updates == 0, "a cancelled copy issued no replacement request");
        var repeat = await site.Coordinator.ExecuteAsync(plan, site.Worker);
        Assert(site.Right.Updates == 0 && site.Right.Copies.Count == 0 &&
               repeat.Status != SyncAuthorityOperationStatus.Completed,
            "cancelled dispatched copy stays unresolved with zero replay or replacement, observed " + repeat.Status);
    }
}

/// <summary>
/// The one scripted cloud conflict fixture, modelled on the CC3D Site shape. It retains the real database,
/// the real pair and root bindings, the real conflict rows, the real shared allocation configuration and the
/// real coordinator. Provider input comes from a scripted adapter on each logical side.
/// </summary>
internal sealed class ConflictFixture : IAsyncDisposable
{
    internal required SyncAuthorityConflictResolutionCoordinator Coordinator { get; init; }
    internal required SyncPair Pair { get; init; }
    internal required string ConflictId { get; init; }
    internal required long Generation { get; init; }
    internal required string Worker { get; init; }
    internal required ScriptedCloudStore Left { get; init; }
    internal required ScriptedCloudStore Right { get; init; }
    internal required SyncAuthorityDatabase Db { get; init; }
    internal required SyncAuthorityCommandAdmission Admission { get; init; }
    internal required SyncControlNamespaceBinding Control { get; init; }
    internal required SyncAuthorityCloudEndpointSet Endpoints { get; init; }
    internal required IReadOnlyList<SyncAuthorityCloudSideAuthority> SideAuthorities { get; init; }
    internal required SyncAuthorityPlannerOptions Options { get; init; }
    internal required SyncAuthorityCloudConflictContext Context { get; init; }
    private PilotDirectoryLease? _anchor;
    private PilotDirectoryLease? _controlRoot;

    internal PilotDirectoryLease ControlRoot => _controlRoot ??= SyncControlNamespace.OpenLocal(Control, _anchor!);

    internal SyncAuthorityPairRunner Runner()
    {
        // Direct executor proofs own this lease; the public runner must acquire its own, just as
        // production does. Do not keep a fixture-only exclusive handle across the ownership handoff.
        _controlRoot?.Dispose();
        _controlRoot = null;
        var prepared = new SyncAuthorityPreparedCloudPair(Pair, Endpoints, Control,
            SideAuthorities.Single(item => item.CompatibilitySide == SyncAuthorityCloudSides.LogicalLeft),
            SideAuthorities.Single(item => item.CompatibilitySide == SyncAuthorityCloudSides.LogicalRight),
            Options, Options.CloudAllocations!, Array.Empty<SyncAuthorityCloudHierarchySnapshot>());
        return new SyncAuthorityPairRunner(Pair, Right.Store, Db, Endpoints.Left.RootBindingId,
            Endpoints.Right.RootBindingId, Options, new PilotRunControl(), Admission).AttachCloudContext(
            new SyncAuthorityCloudRunnerContext(prepared, () => SyncControlNamespace.OpenLocal(Control, _anchor!)));
    }

    internal static async Task<ConflictFixture> CreateAsync(string label, SyncEndpointKind leftKind,
        SyncEndpointKind rightKind, long simpleUploadLimit = 1L << 30)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "The shared cloud conflict proofs require the Windows private staging allocator.");
        var slug = leftKind.ToString().ToLowerInvariant() + "-" + rightKind.ToString().ToLowerInvariant();
        var caseRoot = System.IO.Path.Combine(CloudConflictExecutionChecks.Root, label);
        Directory.CreateDirectory(caseRoot);
        var anchorRoot = System.IO.Path.Combine(caseRoot, "anchor");
        Directory.CreateDirectory(anchorRoot);
        var anchor = PilotDirectoryLease.Open(anchorRoot);
        var namespaceId = "cloud-conflict-execution-" + slug;
        var control = new SyncControlNamespaceBinding(CloudConflictExecutionChecks.InstallationId, namespaceId,
            SyncControlNamespace.BindLocalForTest(anchor, CloudConflictExecutionChecks.InstallationId,
                namespaceId));
        var leftAccount = "account-" + slug + "-left";
        var rightAccount = "account-" + slug + "-right";
        var leftProvider = SyncEndpointProviders.Get(leftKind).ProviderId!;
        var rightProvider = SyncEndpointProviders.Get(rightKind).ProviderId!;
        var leftFolder = "id:" + slug + "-left";
        var rightFolder = "id:" + slug + "-right";
        var leftNamespace = leftKind == SyncEndpointKind.GoogleDrive ? "id:" + leftAccount
            : "ns-root-" + slug + "-left";
        var rightNamespace = rightKind == SyncEndpointKind.GoogleDrive ? "id:" + rightAccount
            : "ns-root-" + slug + "-right";
        var leftRoot = "root-" + slug + "-left";
        var rightRoot = "root-" + slug + "-right";
        var leftRecoveryRoot = "recovery-" + slug + "-left";
        var rightRecoveryRoot = "recovery-" + slug + "-right";
        var leftRecoveryFolder = "id:" + slug + "-rec-left";
        var rightRecoveryFolder = "id:" + slug + "-rec-right";
        var leftCorpus = "corpus-" + slug + "-left";
        var rightCorpus = "corpus-" + slug + "-right";
        var leftRecoveryPath = "/Recovery/" + slug + "/left";
        var rightRecoveryPath = "/Recovery/" + slug + "/right";
        var left = ScriptedCloudStore.Create(leftProvider, leftAccount, leftNamespace, leftFolder);
        var right = ScriptedCloudStore.Create(rightProvider, rightAccount, rightNamespace, rightFolder);
        left.ConfigureRecovery(leftRecoveryPath, leftRecoveryFolder, leftNamespace, leftRecoveryRoot);
        right.ConfigureRecovery(rightRecoveryPath, rightRecoveryFolder, rightNamespace, rightRecoveryRoot);
        var db = new SyncAuthorityDatabase(System.IO.Path.Combine(caseRoot, "syncshark.db"));
        await db.InitializeAsync(CloudConflictExecutionChecks.InstallationId,
            CloudConflictExecutionChecks.ProfileId, CloudConflictExecutionChecks.SourceRevision, "{}");
        await db.RegisterProviderAccountAsync(leftAccount, CloudConflictExecutionChecks.InstallationId,
            "id:" + leftAccount, "ns-root-" + slug + "-left", "ns-home-" + slug + "-left",
            "CC3F left account", 1, cancellationToken: default, provider: leftProvider);
        await db.RegisterProviderAccountAsync(rightAccount, CloudConflictExecutionChecks.InstallationId,
            "id:" + rightAccount, "ns-root-" + slug + "-right", "ns-home-" + slug + "-right",
            "CC3F right account", 1, cancellationToken: default, provider: rightProvider);
        var draft = SyncCloudPairConfiguration.CreateDraft(new SyncPairEndpoints(
            SyncEndpoint.Cloud(leftKind, "conn-" + slug + "-left", leftNamespace, leftFolder, "/left"),
            SyncEndpoint.Cloud(rightKind, "conn-" + slug + "-right", rightNamespace, rightFolder, "/right"),
            SyncMode.TwoWay), "CC3F " + label);
        var pair = SyncPairStore.NormalizePair(draft);
        await db.RegisterPairAsync(pair.Id, pair.PolicyRevision);
        await db.RetainPairDefinitionAsync(pair, true);
        var hierarchies = new[]
        {
            Chain(leftProvider, leftAccount, leftNamespace, leftFolder, leftRoot),
            Chain(rightProvider, rightAccount, rightNamespace, rightFolder, rightRoot),
            Chain(leftProvider, leftAccount, leftNamespace, leftRecoveryFolder, leftRecoveryRoot),
            Chain(rightProvider, rightAccount, rightNamespace, rightRecoveryFolder, rightRecoveryRoot)
        };
        await db.RegisterCloudEndpointRootBindingAsync(leftRoot, pair.Id, SyncAuthorityCloudSides.LogicalLeft,
            1, CloudConflictExecutionChecks.InstallationId, leftProvider, leftAccount, leftNamespace,
            leftFolder, "/left", cancellationToken: default, hierarchies: hierarchies.Take(1).ToArray());
        await db.RegisterCloudEndpointRootBindingAsync(rightRoot, pair.Id, SyncAuthorityCloudSides.LogicalRight,
            1, CloudConflictExecutionChecks.InstallationId, rightProvider, rightAccount, rightNamespace,
            rightFolder, "/right", cancellationToken: default, hierarchies: hierarchies.Take(2).ToArray());
        await db.RegisterCloudRecoveryRootAsync(leftRecoveryRoot, CloudConflictExecutionChecks.InstallationId,
            leftProvider, leftAccount, leftNamespace, leftRecoveryFolder, leftCorpus, leftRecoveryPath, 1,
            cancellationToken: default, hierarchies: hierarchies.Take(3).ToArray());
        await db.RegisterCloudRecoveryRootAsync(rightRecoveryRoot, CloudConflictExecutionChecks.InstallationId,
            rightProvider, rightAccount, rightNamespace, rightRecoveryFolder, rightCorpus, rightRecoveryPath, 1,
            cancellationToken: default, hierarchies: hierarchies);
        var allocations = new SyncAuthorityCloudAllocationConfiguration(control.NamespaceId,
            new SyncAuthorityCloudTargetAllocation(leftRecoveryRoot, simpleUploadLimit),
            new SyncAuthorityCloudTargetAllocation(rightRecoveryRoot, simpleUploadLimit));
        var leftPayload = Encoding.UTF8.GetBytes("left version " + label);
        var rightPayload = Encoding.UTF8.GetBytes("right version " + label);
        const string leftFileId = "id-conflict-left";
        const string rightFileId = "id-conflict-right";
        left.Seed(CloudConflictExecutionChecks.Path, leftFileId, "rev-left-1", leftPayload);
        right.Seed(CloudConflictExecutionChecks.Path, rightFileId, "rev-right-1", rightPayload);
        var objectId = "object-conflict-" + slug;
        var leftVersion = "version-" + slug + "-left";
        var rightVersion = "version-" + slug + "-right";
        var leftMembership = "membership-" + slug + "-left";
        var rightMembership = "membership-" + slug + "-right";
        await db.RegisterObjectAsync(objectId, "file");
        await db.RegisterObjectVersionAsync(leftVersion, objectId, SyncAuthorityCloudSides.LogicalLeft,
            left.IdentityFor(leftFileId, "rev-left-1"), SyncContentIdentity.Compute(leftProvider, leftPayload),
            leftPayload.Length);
        await db.RegisterObjectVersionAsync(rightVersion, objectId, SyncAuthorityCloudSides.LogicalRight,
            right.IdentityFor(rightFileId, "rev-right-1"), SyncContentIdentity.Compute(rightProvider, rightPayload),
            rightPayload.Length);
        await db.RegisterMembershipAsync(leftMembership, objectId, leftRoot,
            CloudConflictExecutionChecks.Path, 1);
        await db.RegisterMembershipAsync(rightMembership, objectId, rightRoot,
            CloudConflictExecutionChecks.Path, 1);
        var conflictId = "conflict-" + slug;
        await db.RecordConflictAsync(conflictId, pair.Id, objectId, "both sides changed independently",
            leftVersion, leftMembership, rightVersion, rightMembership);
        var conflict = await db.LoadConflictAsync(conflictId);
        var endpoints = SyncAuthorityCloudEndpointSet.FromPair(pair, left.Store, right.Store, leftRoot,
            rightRoot, leftAccount, rightAccount);
        var options = new SyncAuthorityPlannerOptions(1, CloudConflictExecutionChecks.ProfileId,
            control.NamespaceId, control.NamespaceId, Direction: SyncMode.TwoWay,
            CloudAllocations: allocations);
        var authorities = new[]
        {
            new SyncAuthorityCloudSideAuthority(SyncAuthorityCloudSides.LogicalLeft, leftProvider,
                "conn-" + slug + "-left", leftAccount, leftNamespace, leftFolder, "/left", leftRoot,
                leftRecoveryRoot, leftRecoveryFolder, leftNamespace, leftCorpus, leftRecoveryPath),
            new SyncAuthorityCloudSideAuthority(SyncAuthorityCloudSides.LogicalRight, rightProvider,
                "conn-" + slug + "-right", rightAccount, rightNamespace, rightFolder, "/right", rightRoot,
                rightRecoveryRoot, rightRecoveryFolder, rightNamespace, rightCorpus, rightRecoveryPath)
        };
        var cloudContext = new SyncAuthorityCloudExecutionContext(endpoints, control,
            SyncControlNamespace.OpenLocal(control, anchor));
        var conflictContext = new SyncAuthorityCloudConflictContext(cloudContext, authorities);
        await db.VerifyHealthAsync();
        var admission = await db.CreateAcceptedCommandAdmissionAsync(null, "resolve_conflict", pair.Id,
            conflictId, null, JsonSerializer.Serialize(new { pairIds = new[] { pair.Id } }),
            SyncAuthorityCommandPurpose.NormalEffect);
        await db.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
        var coordinator = new SyncAuthorityConflictResolutionCoordinator(pair, right.Store, db, options,
            admission, conflictContext);
        return new ConflictFixture
        {
            Pair = pair,
            Coordinator = coordinator,
            Admission = admission,
            ConflictId = conflictId,
            Generation = conflict.ConflictGeneration,
            Worker = "worker-cc3f-" + slug,
            Left = left,
            Right = right,
            Db = db,
            Control = control,
            Endpoints = endpoints,
            SideAuthorities = authorities,
            Options = options,
            Context = conflictContext,
            _anchor = anchor,
            _controlRoot = cloudContext.ControlRoot
        };
    }

    /// <summary>
    /// Drives the copy engine through a context whose side authority names another provider account, so the
    /// typed port and the retained authority disagree and the effect must refuse before any request.
    /// </summary>
    internal async Task ExecuteWithCrossedAuthorityAsync()
    {
        var crossed = SideAuthorities.ToArray();
        crossed[1] = crossed[1] with { ProviderAccountId = "account-foreign-" + Worker };
        var foreign = new SyncAuthorityCloudConflictContext(Context.Cloud, crossed);
        var plan = await Coordinator.PlanAsync(ConflictId, Generation,
            SyncAuthorityConflictResolutionKind.PreserveBoth);
        var engine = new SyncAuthorityCloudConflictCopyExecutor(Db, foreign);
        await engine.ExecuteAsync(plan.PreserveOperationId!, Worker);
    }

    private static SyncAuthorityCloudHierarchySnapshot Chain(string provider, string providerAccountId,
        string namespaceId, string folderId, string rootBindingId)
    {
        var dropbox = provider == "dropbox";
        var chain = new[] { dropbox ? PilotPath.DropboxNamespaceRootIdentity(namespaceId)
            : "corpus-root-" + namespaceId, folderId };
        return new SyncAuthorityCloudHierarchySnapshot(provider, providerAccountId, namespaceId,
            rootBindingId, folderId, chain)
        {
            CompleteToCorpusRoot = true,
            CorpusRootId = chain[0],
            ChainNodeKeys = chain.Select(id => new SyncAuthorityCloudHierarchyNodeKeys(id,
                new[] { SyncCloudObjectKey.Native(provider, id) })).ToArray(),
            ObjectKeyCoverage = dropbox ? SyncCloudObjectKeyCoverage.MountAliasesComplete
                : SyncCloudObjectKeyCoverage.NativeOnly
        };
    }

    public async ValueTask DisposeAsync()
    {
        _controlRoot?.Dispose();
        _anchor?.Dispose();
        await Db.TransitionCommandRequestAsync(Admission.CommandRequestId, "completed");
    }
}

/// <summary>
/// The scripted adapter surface: the real ISyncRemoteStore plus the exact range capability the shared
/// common-digest resolver reads, proxied so one scripted store can serve both.
/// </summary>
internal interface IConflictFixtureCloudStore : ISyncRemoteStore, ISyncRemoteRangeStore
{
}

/// <summary>
/// The one scripted provider store. It supplies provider input and records exact request counts; it refuses
/// every effect it was not given, including the legacy unbound recovery overloads.
/// </summary>
internal class ScriptedCloudStore : DispatchProxy
{
    internal string Provider = "";
    internal string ProviderAccountId = "";
    internal string NamespaceId = "";
    internal string FolderId = "";
    internal List<PilotRemoteEntry> Entries { get; } = new();
    internal Dictionary<string, byte[]> Payloads { get; } = new(StringComparer.Ordinal);
    internal List<(string Path, byte[] Bytes, string ExpectedRevision)> Uploads { get; } = new();
    internal List<string> Copies { get; } = new();
    internal Dictionary<string, (SyncArchiveResult Result, byte[] Payload)> RecoveryCopies { get; } =
        new(StringComparer.Ordinal);
    internal (string RootPath, string RootFolderId, string RootNamespaceId, string RootBindingId)? Recovery;
    internal int RevisionCounter;
    internal int Downloads;
    internal int RecoveryRetained;
    internal bool FailAfterApplyOnCopy;
    internal Action<string>? DuringCopy;

    internal int Updates => Uploads.Count;
    internal byte[] SeedBytes = Array.Empty<byte>();

    internal ISyncRemoteStore Store => (ISyncRemoteStore)(object)this;

    internal static ScriptedCloudStore Create(string provider, string providerAccountId, string namespaceId,
        string folderId)
    {
        var fixture = (ScriptedCloudStore)(object)DispatchProxy.Create<IConflictFixtureCloudStore,
            ScriptedCloudStore>();
        fixture.Provider = provider;
        fixture.ProviderAccountId = providerAccountId;
        fixture.NamespaceId = namespaceId;
        fixture.FolderId = folderId;
        return fixture;
    }

    internal void ConfigureRecovery(string path, string folderId, string namespaceId, string rootBindingId) =>
        Recovery = (path, folderId, namespaceId, rootBindingId);

    internal string IdentityFor(string fileId, string? revision) =>
        new SyncAuthorityCloudObservationIdentity(Provider, ProviderAccountId, NamespaceId, fileId, revision,
            SyncEndpointProviders.TryGet(Provider, out var definition)
                ? definition!.ContentHashAlgorithm
                : SyncContentIdentity.Sha256).Serialize();

    internal void Seed(string relativePath, string fileId, string revision, byte[] payload)
    {
        var hash = SyncContentIdentity.Compute(Provider, payload);
        var entry = new PilotRemoteEntry(relativePath, PilotEntryKind.File, fileId, revision, payload.Length,
            hash);
        var index = Index(relativePath);
        if (index >= 0) Entries[index] = entry; else Entries.Add(entry);
        Payloads[relativePath] = payload;
        SeedBytes = payload;
    }

    internal byte[] BytesAt(string relativePath) => Payloads.TryGetValue(relativePath, out var payload)
        ? payload
        : throw new InvalidDataException("This scripted store retains no payload at " + relativePath + ".");

    internal void MutateSourceRevision()
    {
        var index = Index(CloudConflictExecutionChecks.Path);
        Entries[index] = Entries[index] with { Revision = "changed-revision" };
    }

    private int Index(string relativePath) => Entries.FindIndex(entry =>
        string.Equals(entry.RelativePath, relativePath, StringComparison.Ordinal));

    protected override object? Invoke(MethodInfo? method, object?[]? arguments) => method?.Name switch
    {
        "get_SupportsConditionalDirectoryDelete" => false,
        nameof(ISyncRemoteStore.VerifyRootIdentityAsync) => Task.CompletedTask,
        nameof(ISyncRemoteStore.ListEntriesAsync) =>
            Task.FromResult<IReadOnlyList<PilotRemoteEntry>>(Entries.ToArray()),
        nameof(ISyncRemoteStore.DownloadAsync) => Download(arguments),
        nameof(ISyncRemoteRangeStore.DownloadRangeAsync) => DownloadRange(arguments),
        nameof(ISyncRemoteStore.UploadAsync) => Upload(arguments),
        nameof(ISyncRemoteStore.CopyCreateOnlyAsync) => Copy(arguments),
        nameof(ISyncRemoteStore.VerifyRecoveryRootAsync) => VerifyRecovery(arguments!),
        nameof(ISyncRemoteStore.CopyToRecoveryAsync) => arguments is [_, SyncRemoteRecoveryRootBinding, ..]
            ? CopyToRecovery(arguments!)
            : throw new NotSupportedException("This scripted store uses only the bound recovery-root overload."),
        nameof(ISyncRemoteStore.GetRecoveryAsync) => arguments is [SyncRemoteRecoveryRootBinding, ..]
            ? GetRecovery(arguments!)
            : throw new NotSupportedException("This scripted store uses only the bound recovery-root overload."),
        _ => throw new NotSupportedException(
            "This cloud conflict fixture performs no such provider effect: " + (method?.Name ?? "unknown"))
    };

    private object Download(object?[]? arguments)
    {
        var expected = (PilotRemoteEntry)arguments![0]!;
        var destination = (Stream)arguments[1]!;
        ((CancellationToken)arguments[2]!).ThrowIfCancellationRequested();
        if (!Payloads.TryGetValue(expected.RelativePath, out var payload))
            throw new FileNotFoundException("The scripted source retains no payload.", expected.RelativePath);
        var index = Index(expected.RelativePath);
        if (index < 0 || Entries[index].Id != expected.Id || Entries[index].Revision != expected.Revision ||
            Entries[index].Size != expected.Size || Entries[index].ContentHash != expected.ContentHash)
            throw new IOException("Download refuses a changed source identity or revision.");
        Downloads++;
        destination.Position = 0;
        destination.SetLength(0);
        if (payload.Length > 0) destination.Write(payload, 0, payload.Length);
        destination.Flush();
        return Task.FromResult(new SyncDownloadResult(expected.Id!, expected.Revision ?? "", payload.Length,
            SyncContentIdentity.Compute(Provider, payload)));
    }

    private object Upload(object?[]? arguments)
    {
        ((CancellationToken)arguments![4]!).ThrowIfCancellationRequested();
        var relativePath = (string)arguments![0]!;
        var source = (Stream)arguments[1]!;
        var expectedRevision = arguments[3] as string;
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        var bytes = buffer.ToArray();
        Uploads.Add((relativePath, bytes, expectedRevision ?? ""));
        var current = Index(relativePath);
        if (current < 0 || !string.Equals(Entries[current].Revision, expectedRevision, StringComparison.Ordinal))
            throw new IOException(
                "The scripted target refused the conditional replacement because its revision moved.");
        var revision = "rev-" + (++RevisionCounter);
        var nativeHash = SyncContentIdentity.Compute(Provider, bytes);
        var id = Entries[current].Id!;
        Entries[current] = new PilotRemoteEntry(relativePath, PilotEntryKind.File, id, revision, bytes.Length,
            nativeHash);
        Payloads[relativePath] = bytes;
        return Task.FromResult(new PilotUploadResult(id, revision, bytes.Length, nativeHash));
    }

    private object Copy(object?[]? arguments)
    {
        var expected = (PilotRemoteEntry)arguments![0]!;
        var destinationPath = (string)arguments[1]!;
        DuringCopy?.Invoke(destinationPath);
        ((CancellationToken)arguments[2]!).ThrowIfCancellationRequested();
        if (!Payloads.TryGetValue(expected.RelativePath, out var payload))
            throw new FileNotFoundException("The scripted copy source retains no payload.", expected.RelativePath);
        var current = Index(expected.RelativePath);
        if (current < 0 || !string.Equals(Entries[current].Revision, expected.Revision, StringComparison.Ordinal) ||
            !string.Equals(Entries[current].Id, expected.Id, StringComparison.Ordinal) ||
            Entries[current].Size != expected.Size || Entries[current].ContentHash != expected.ContentHash)
            throw new IOException("The scripted copy source no longer carries the retained revision.");
        if (Index(destinationPath) >= 0) throw new IOException("Create-only copy refuses an occupied destination.");
        Copies.Add(destinationPath);
        var revision = "rev-copy-" + (++RevisionCounter);
        var hash = SyncContentIdentity.Compute(Provider, payload);
        var copyEntry = new PilotRemoteEntry(destinationPath, PilotEntryKind.File,
            "id-copy-" + Copies.Count, revision, payload.Length, hash);
        Entries.Add(copyEntry);
        Payloads[destinationPath] = payload;
        if (FailAfterApplyOnCopy)
            throw new IOException("The scripted provider applied the copy and then lost the response.");
        return Task.FromResult(new PilotUploadResult(copyEntry.Id!, revision, payload.Length, hash));
    }

    private object CopyToRecovery(object[] arguments)
    {
        var expected = (PilotRemoteEntry)arguments[0]!;
        RequireBoundRecovery((SyncRemoteRecoveryRootBinding)arguments[1]!);
        ((CancellationToken)arguments[3]!).ThrowIfCancellationRequested();
        var name = (string)arguments[2]!;
        if (!Payloads.TryGetValue(expected.RelativePath, out var payload))
            throw new FileNotFoundException("The scripted recovery source retains no payload.");
        var current = Entries[Index(expected.RelativePath)];
        if (current.Id != expected.Id || current.Revision != expected.Revision ||
            current.ContentHash != expected.ContentHash || current.Size != expected.Size)
            throw new IOException("Recovery refuses a changed predecessor.");
        var revision = "rev-recovery-" + (++RevisionCounter);
        var hash = SyncContentIdentity.Compute(Provider, payload);
        var result = new SyncArchiveResult("id-recovery-" + name, revision,
            (Recovery?.RootPath ?? "/Recovery") + "/" + name, payload.Length, hash);
        RecoveryCopies[name] = (result, payload);
        RecoveryRetained++;
        return Task.FromResult(result);
    }

    private object GetRecovery(object[] arguments)
    {
        RequireBoundRecovery((SyncRemoteRecoveryRootBinding)arguments[0]!);
        var name = (string)arguments[1]!;
        return Task.FromResult<SyncArchiveResult?>(RecoveryCopies.TryGetValue(name, out var retained)
            ? retained.Result
            : null);
    }
    private object VerifyRecovery(object?[] arguments)
    {
        RequireBoundRecovery((SyncRemoteRecoveryRootBinding)arguments[0]!);
        return Task.CompletedTask;
    }

    private void RequireBoundRecovery(SyncRemoteRecoveryRootBinding binding)
    {
        if (Recovery is not { } expected || expected.RootPath != binding.RootPath ||
            expected.RootFolderId != binding.RootFolderId || expected.RootNamespaceId != binding.RootNamespaceId)
            throw new InvalidOperationException("Recovery must use this endpoint's own bound recovery root.");
    }

    private object DownloadRange(object?[]? arguments)
    {
        var expected = (PilotRemoteEntry)arguments![0]!;
        var offset = (long)arguments[1]!;
        var length = (long)arguments[2]!;
        var stream = (Stream)arguments[3]!;
        var actual = Entries[Index(expected.RelativePath)];
        var payload = BytesAt(expected.RelativePath);
        if (actual.Id != expected.Id || actual.Revision != expected.Revision ||
            offset < 0 || length < 0 || offset > payload.Length || length > payload.Length - offset)
            throw new IOException("Range download refused changed identity/revision or invalid bounds.");
        ((CancellationToken)arguments[4]!).ThrowIfCancellationRequested();
        stream.Write(payload, (int)offset, (int)length);
        return Task.FromResult(new SyncRangeDownloadResult(actual.Id!, actual.Revision!, payload.Length,
            actual.ContentHash!, offset, length));
    }
}

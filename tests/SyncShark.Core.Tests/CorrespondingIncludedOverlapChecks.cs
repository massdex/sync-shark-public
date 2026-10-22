using System.Text.Json;
using Massdex.SyncShark;

// OV637: the corresponding INCLUDED overlap correction. Every admission runs through the real guarded
// authority root-binding writer (SyncAuthorityDatabase.RegisterRootBindingAsync) and the real durable
// reservation writer (SyncAuthorityDatabase.CreateOperationAsync); the policy decision is the same shared
// SyncPairPartitionPolicy the authoring, endpoint, editor and validation consumers call. Fixtures supply
// pair definitions and synthetic provider-free local identities only; they never replace a guard, planner
// or scheduler.
internal static class CorrespondingIncludedOverlapChecks
{
    private const string Installation = "installation-ov637";
    private const string Profile = "profile-ov637";
    private const string ParentPair = "pair-ov637-parent";
    private const string ChildPair = "pair-ov637-child";
    private const string ParentLeftRoot = "root-ov637-parent-local";
    private const string ParentRightRoot = "root-ov637-parent-right";
    private const string ChildLeftRoot = "root-ov637-child-local";
    private const string ChildRightRoot = "root-ov637-child-right";
    private const string NestedChildName = "Child";
    private static readonly string ControlName = ".syncshark-control-" + ChildPair;
    private static readonly string RightControlName =
        ".syncshark-control-" + SyncEndpointPairRuntime.RightControlNamespaceId(ChildPair);
    private const string OverlapMessage =
        "overlaps this exact root/path or retained subtree prefix";
    private static int _assertions;

    internal static async Task RunAsync(string root)
    {
        root = Path.GetFullPath(root);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException("Use an empty isolated corresponding-overlap fixture root.");
        Directory.CreateDirectory(root);
        _assertions = 0;
        var cases = new List<string>();

        await PolicyAsync(root);
        cases.Add("OV637-1/2 shared policy: corresponding included vs excluded partition");

        await RealAdmissionPositiveAsync(Path.Combine(root, "positive"));
        cases.Add("OV637-1/6 real host admission admits the corresponding included child on both endpoints");

        await RealAdmissionRefusalsAsync(root);
        cases.Add("OV637-2/6 real host admission refuses unequal/mixed/unpublished/excluded/private/equal");

        await ReservationAliasFenceAsync(root);
        cases.Add("OV637-3 real durable reservation collision fences the aliased path and ancestor subtree");

        await SwappedEndpointAliasAsync(root);
        cases.Add("OV637-5 swapped-endpoint local/local alias follows the proven endpoint bijection");

        await ReservationFenceDurabilityAsync(root);
        cases.Add("OV637-3 aliased fence survives restart and covers issued/unknown aliased reservations");

        await OpaqueCorrespondingAsync(root);
        cases.Add("OV637-4/6 bound opaque corresponding admission, fence and related group; absent/forged refuses");

        await OpaqueRefusalAsync(root);
        cases.Add("OV637-2 absent and forged opaque hierarchies refuse without writing a candidate root");

        await SafeSentinelAsync(root);
        cases.Add("OV637-6 safe sentinel: a bypassed proof and a skipped Host cannot pass");

        await OpaqueAliasedCollisionAsync(root);
        cases.Add("OV637-4/6 the retained context admits the opaque correspondence and fences the aliased reservation in both orders and after restart");

        await RealHostRelatedRunAsync(root);
        cases.Add("OV637-4 real Host.RunPairsAsync completes two related ready pairs without a planning block");

        await RelatedGroupingAsync(root);
        cases.Add("OV637-4 related components derived from current definitions; unrelated pair stays separate");

        await File.WriteAllTextAsync(Path.Combine(root, "corresponding-included-overlap-checks.json"),
            JsonSerializer.Serialize(new
            {
                assertions = _assertions,
                cases,
                entryPoints = new[]
                {
                    "SyncPairPartitionPolicy.DecideCorrespondingIncluded",
                    "SyncAuthorityDatabase.RegisterRootBindingAsync",
                    "SyncAuthorityDatabase.CreateOperationAsync"
                },
                productionDataTouched = false
            }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS corresponding included overlap: " + _assertions + " assertions, " +
            cases.Count + " cases.");
    }

    // --------------------------------------------------------------------------------------------
    // OV637-1 / OV637-2: the shared policy relation, distinct from the excluded partition.
    // --------------------------------------------------------------------------------------------
    private static Task PolicyAsync(string root)
    {
        var parentLeft = Path.Combine(root, "policy", "Parent");
        var parentRight = Path.Combine(root, "policy", "ParentMirror");
        var parent = LocalPair(ParentPair, parentLeft, parentRight,
            [new SyncSelectionRule(ControlName, false), new SyncSelectionRule(RightControlName, false)]);
            var child = LocalPair(ChildPair, Path.Combine(parentLeft, NestedChildName), Path.Combine(parentRight, NestedChildName), [])
            with { ControlNamespace = Control(parentLeft, ChildPair), RightControlNamespace = Control(parentRight, ChildPair) };

        Require(SyncPairPartitionPolicy.Decide(parent, child) == SyncPairPartitionRelation.NestedRefused,
            "OV637-1 the excluded-partition decision no longer refuses the included overlap; observed " +
            SyncPairPartitionPolicy.Decide(parent, child));
        Require(SyncPairPartitionPolicy.DecideCorrespondingIncluded(parent, child) ==
                SyncPairPartitionRelation.CorrespondingIncluded,
            "OV637-1 the corresponding included relation is admitted from both verified endpoints");
        Require(SyncPairPartitionPolicy.DecideCorrespondingIncluded(child, parent) ==
                SyncPairPartitionRelation.CorrespondingIncluded,
            "OV637-1 the relation is generic over endpoint order");
        Require(!SyncPairPartitionPolicy.ExcludesWholeSubtree(parent, NestedChildName),
            "OV637-1 the enclosing pair does not exclude the nested Child data subtree");
        Require(SyncPairPartitionPolicy.IncludesWholeSubtree(parent, NestedChildName),
            "OV637-1 the enclosing pair includes the whole relative subtree as ordinary data");
        var pinned = SyncPairPartitionPolicy.TryCorrespondingIncluded(parent, child,
            out var correspondingEnclosing, out var correspondingNested, out var correspondingRelative);
        Require(pinned && ReferenceEquals(correspondingEnclosing, parent) &&
                ReferenceEquals(correspondingNested, child) &&
                string.Equals(correspondingRelative, NestedChildName, StringComparison.OrdinalIgnoreCase),
            "OV637-1 the admitted relation pins the enclosing pair, the nested pair and the exact relative " +
            "subtree (the component relative is taken from the case-normalised local identity key)");

        // Unequal translation: the two endpoints name different relative subtrees.
        var unequal = child with
        {
            RightEndpoint = LocalEndpoint(Path.Combine(parentRight, "Child2")).Record()
        };
        Require(SyncPairPartitionPolicy.DecideCorrespondingIncluded(parent, unequal) ==
                SyncPairPartitionRelation.NestedRefused,
            "OV637-2 an unequal component-relative translation is refused");

        // Missing second nested endpoint: the right endpoint is not nested at all.
        var missing = child with
        {
            RightEndpoint = LocalEndpoint(Path.Combine(root, "policy", "Other")).Record()
        };
        Require(SyncPairPartitionPolicy.DecideCorrespondingIncluded(parent, missing) ==
                SyncPairPartitionRelation.NestedRefused,
            "OV637-2 a missing second nested endpoint is refused");

        // The excluded partition keeps its exact meaning.
        var excluded = parent with
        {
            SelectionRules =
            [
                new SyncSelectionRule(NestedChildName, false), new SyncSelectionRule(ControlName, false),
                new SyncSelectionRule(RightControlName, false)
            ]
        };
        Require(SyncPairPartitionPolicy.Decide(excluded, child) == SyncPairPartitionRelation.NestedAllowed &&
                SyncPairPartitionPolicy.DecideCorrespondingIncluded(excluded, child) ==
                SyncPairPartitionRelation.NestedRefused,
            "OV637-2 the fully excluded partition stays NestedAllowed and is not the included relation");

        // The nested pair's private control ownership must stay excluded by the enclosing pair.
        var openParent = parent with { SelectionRules = [] };
        Require(SyncPairPartitionPolicy.DecideCorrespondingIncluded(openParent, child) ==
                SyncPairPartitionRelation.NestedRefused,
            "OV637-2 an enclosing pair that does not exclude the nested private control ownership is refused");

        // Equal whole roots are never a corresponding relation.
        var equal = LocalPair("pair-ov637-equal", parentLeft, parentRight, []);
        Require(SyncPairPartitionPolicy.DecideCorrespondingIncluded(equal, parent) ==
                SyncPairPartitionRelation.NestedRefused,
            "OV637-2 equal whole roots are refused");
        return Task.CompletedTask;
    }

    // --------------------------------------------------------------------------------------------
    // OV637-1 / OV637-6: the real guarded root-admission writer admits the correlated child.
    // --------------------------------------------------------------------------------------------
    private static async Task RealAdmissionPositiveAsync(string root)
    {
        using var fixture = await Fixture.CreateAsync(root, publishParent: true);
        await fixture.Db.RegisterRootBindingAsync(ChildLeftRoot, ChildPair, "local", 1, "{}",
            fixture.ChildLeft, partitionCandidate: fixture.Child);
        await fixture.Db.RegisterRootBindingAsync(ChildRightRoot, ChildPair, "dropbox", 1, "{}",
            fixture.ChildRight, partitionCandidate: fixture.Child);
        Require(await fixture.ActiveRootCountAsync() == 4,
            "OV637-1 the real host admitted the corresponding child on both endpoints");
        Require(await fixture.ScalarAsync(
                "SELECT count(*) FROM root_binding WHERE pair_id='" + ChildPair + "' AND state='active';") == "2",
            "OV637-1 the child owns two active verified roots alongside the retained parent");
    }

    private static async Task RealAdmissionRefusalsAsync(string root)
    {
        // Unequal translation on the right endpoint.
        using (var fixture = await Fixture.CreateAsync(Path.Combine(root, "unequal"), publishParent: true,
                   childRightLeaf: "Child2"))
            await RefuseAsync(() => fixture.Db.RegisterRootBindingAsync(ChildLeftRoot, ChildPair, "local", 1,
                "{}", fixture.ChildLeft, partitionCandidate: fixture.Child));

        // Unpublished parent: no retained definition can authorise the overlap.
        using (var fixture = await Fixture.CreateAsync(Path.Combine(root, "unpublished"), publishParent: false))
            await RefuseAsync(() => fixture.Db.RegisterRootBindingAsync(ChildLeftRoot, ChildPair, "local", 1,
                "{}", fixture.ChildLeft, partitionCandidate: fixture.Child));

        // The existing excluded partition keeps its exact meaning and stays admitted, as NestedAllowed
        // rather than the corresponding included relation.
        using (var fixture = await Fixture.CreateAsync(Path.Combine(root, "excluded"), publishParent: true,
                   includeChild: false))
        {
            Require(SyncPairPartitionPolicy.Decide(fixture.Parent, fixture.Child) ==
                    SyncPairPartitionRelation.NestedAllowed &&
                    SyncPairPartitionPolicy.DecideCorrespondingIncluded(fixture.Parent, fixture.Child) ==
                    SyncPairPartitionRelation.NestedRefused,
                "OV637-2 the excluded partition stays NestedAllowed and is never the included relation");
            await fixture.Db.RegisterRootBindingAsync(ChildLeftRoot, ChildPair, "local", 1, "{}",
                fixture.ChildLeft, partitionCandidate: fixture.Child);
            Require(await fixture.ActiveRootCountAsync() == 3,
                "OV637-2 the unchanged excluded nested partition still registers");
        }

        // Private control ownership not excluded by the enclosing pair.
        using (var fixture = await Fixture.CreateAsync(Path.Combine(root, "private"), publishParent: true,
                   excludeControl: false))
            await RefuseAsync(() => fixture.Db.RegisterRootBindingAsync(ChildLeftRoot, ChildPair, "local", 1,
                "{}", fixture.ChildLeft, partitionCandidate: fixture.Child));
    }

    // --------------------------------------------------------------------------------------------
    // OV637-3: the durable reservation collision boundary across the verified alias.
    // --------------------------------------------------------------------------------------------
    private static async Task ReservationAliasFenceAsync(string root)
    {
        // Alias path fence, both directions.
        using (var fixture = await Fixture.CreateAsync(Path.Combine(root, "fence-forward"), publishParent: true,
                   publishChild: true))
        {
            await fixture.RegisterRootsAsync();
            var historyLookups = 0;
            var candidateSnapshots = 0;
            var previousSnapshot = SyncAuthorityDatabase.ReservationCandidateSnapshotLoadedForTest;
            SyncAuthorityDatabase.ReservationCandidateSnapshotLoadedForTest = count =>
            {
                Interlocked.Increment(ref candidateSnapshots);
                previousSnapshot?.Invoke(count);
            };
            var previousTiming = SyncAuthorityDatabase.TimingForTest;
            SyncAuthorityDatabase.TimingForTest = (phase, elapsed) =>
            {
                if (phase == "opaque-hierarchy-history-lookup") Interlocked.Increment(ref historyLookups);
                previousTiming?.Invoke(phase, elapsed);
            };
            try
            {
            await using var planning = fixture.Db.BeginExecutionConnectionScope(planningReservations: true);
            await fixture.CreatePathOperationAsync("op-parent-bridge-a", ParentPair, ParentLeftRoot,
                NestedChildName + "/a");
            await RefuseAsync(() => fixture.CreatePathOperationAsync("op-child-a", ChildPair, ChildLeftRoot,
                "a"), OverlapMessage);
            // An unrelated child path shares no physical object and stays admissible.
            await fixture.CreatePathOperationAsync("op-child-z", ChildPair, ChildLeftRoot, "z");

            Require(historyLookups == 0,
                "path-descriptive overlapping pairs retain both reservation outcomes without searching opaque hierarchy history per file");
            Require(candidateSnapshots > 0,
                "overlapping-pair planning uses the bounded reservation candidate path, not every retained sibling plan");
            var snapshotsBeforeSiblings = candidateSnapshots;
            for (var index = 0; index < 32; index++)
                await fixture.CreatePathOperationAsync("op-child-independent-" + index, ChildPair,
                    ChildLeftRoot, "independent-" + index);
            Require(candidateSnapshots == snapshotsBeforeSiblings,
                "independent aliased paths reuse one complete candidate snapshot across committed planning batches");
            await fixture.CreatePathOperationAsync("op-parent-unicode", ParentPair, ParentLeftRoot,
                NestedChildName + "/Café");
            await RefuseAsync(() => fixture.CreatePathOperationAsync("op-child-unicode", ChildPair,
                ChildLeftRoot, "café"), OverlapMessage);
            }
            finally
            {
                SyncAuthorityDatabase.TimingForTest = previousTiming;
                SyncAuthorityDatabase.ReservationCandidateSnapshotLoadedForTest = previousSnapshot;
            }
        }

        // Alias path fence, reversed creation order.
        using (var fixture = await Fixture.CreateAsync(Path.Combine(root, "fence-reverse"), publishParent: true,
                   publishChild: true))
        {
            await fixture.RegisterRootsAsync();
            await using var planning = fixture.Db.BeginExecutionConnectionScope(planningReservations: true);
            await fixture.CreatePathOperationAsync("op-child-a2", ChildPair, ChildLeftRoot, "a");
            await RefuseAsync(() => fixture.CreatePathOperationAsync("op-parent-bridge-a2", ParentPair,
                ParentLeftRoot, NestedChildName + "/a"), OverlapMessage);
        }

        // Ancestor/subtree fence across the alias.
        using (var fixture = await Fixture.CreateAsync(Path.Combine(root, "fence-subtree"), publishParent: true,
                   publishChild: true))
        {
            await fixture.RegisterRootsAsync();
            await using var planning = fixture.Db.BeginExecutionConnectionScope(planningReservations: true);
            await fixture.CreateDirectoryCrossPairMoveAsync("op-parent-subtree", ParentPair, ParentLeftRoot);
            await RefuseAsync(() => fixture.CreatePathOperationAsync("op-child-sub", ChildPair, ChildLeftRoot,
                "a"), OverlapMessage);
        }
    }

    private static Task RelatedGroupingAsync(string root)
    {
        var parentLeft = Path.Combine(root, "grouping", "Parent");
        var parentRight = Path.Combine(root, "grouping", "ParentMirror");
        var parent = LocalPair(ParentPair, parentLeft, parentRight,
            [new SyncSelectionRule(ControlName, false), new SyncSelectionRule(RightControlName, false)]);
        var child = LocalPair(ChildPair, Path.Combine(parentLeft, NestedChildName), Path.Combine(parentRight, NestedChildName), [])
            with
            {
                ControlNamespace = Control(parentLeft, ChildPair),
                RightControlNamespace = Control(parentRight, ChildPair)
            };
        var unrelated = LocalPair("pair-ov637-unrelated", Path.Combine(root, "grouping", "Separate"),
            Path.Combine(root, "grouping", "SeparateMirror"), []);
        var groups = SyncPairPartitionPolicy.RelatedOverlapGroups([parent, child, unrelated]);
        Require(groups.Count == 1 && groups[0].Count == 2 &&
                groups[0].Contains(ParentPair, StringComparer.Ordinal) &&
                groups[0].Contains(ChildPair, StringComparer.Ordinal),
            "OV637-4 the corresponding parent/child form one related component and the disjoint pair is excluded");
        return Task.CompletedTask;
    }

    // --------------------------------------------------------------------------------------------
    // fixtures
    // --------------------------------------------------------------------------------------------
    // --------------------------------------------------------------------------------------------
    // OV637-5: the root mapping follows the correspondence decision's endpoint bijection.
    // --------------------------------------------------------------------------------------------
    private static async Task SwappedEndpointAliasAsync(string root)
    {
        // The child stores its two local endpoints in REVERSE order: its compatibility "local" port names the
        // mirror root and its "dropbox" port names the nested root inside the enclosing pair's local root. A
        // same-side mapping (inner local -> outer local) would fence the wrong pair of roots and miss the real
        // aliased object; the bijection carried from the shared decision must fence the crossed pair instead.
        using var fixture = await SwappedFixture.CreateAsync(Path.Combine(root, "swapped"));
        await fixture.RegisterRootsAsync();
        await fixture.CreatePathOperationAsync("op-swap-parent", SwappedFixture.ParentPairId,
            SwappedFixture.ParentLeftRoot, NestedChildName + "/a");
        // The child's "dropbox" side root really reaches the enclosing local root's Child/a object.
        await RefuseAsync(() => fixture.CreatePathOperationAsync("op-swap-child-crossed",
            SwappedFixture.ChildPairId, SwappedFixture.ChildRightRoot, "a"), OverlapMessage);
        // The child's "local" side root reaches the enclosing DROPBOX root, which holds no reservation, so the
        // corrected projection must not over-fence it.
        await fixture.CreatePathOperationAsync("op-swap-child-other", SwappedFixture.ChildPairId,
            SwappedFixture.ChildLeftRoot, "a");

    }

    // --------------------------------------------------------------------------------------------
    // OV637-3: the alias fence is durable, and covers an issued/nonterminal aliased reservation.
    // --------------------------------------------------------------------------------------------
    private static async Task ReservationFenceDurabilityAsync(string root)
    {
        using var fixture = await Fixture.CreateAsync(Path.Combine(root, "fence-restart"), publishParent: true,
            publishChild: true);
        await fixture.RegisterRootsAsync();
        // An ISSUED (leased, nonterminal) aliased reservation must fence the translated child path.
        await fixture.CreatePathOperationAsync("op-issued-parent", ParentPair, ParentLeftRoot, NestedChildName + "/a");
        await fixture.Db.AcquireOperationLeaseAsync((await fixture.Db.FindRetainedActionIdAsync("idempotency-op-issued-parent"))!, "worker-ov637-issued",
            TimeSpan.FromMinutes(5));
        await RefuseAsync(() => fixture.CreatePathOperationAsync("op-issued-child", ChildPair, ChildLeftRoot,
            "a"), OverlapMessage);

        // A FRESH database instance on the same retained file must re-derive the same alias fence.
        var reopened = new SyncAuthorityDatabase(fixture.DatabasePath);
        // A restart retains the same profile identity and its exact revision.
        await reopened.InitializeAsync(Installation, Profile, "ov637", "{}");
        await fixture.CreatePathOperationAsync("op-restart-child", ChildPair, ChildLeftRoot, "r1", reopened);
        await RefuseAsync(() => fixture.CreatePathOperationAsync("op-restart-parent", ParentPair, ParentLeftRoot,
            NestedChildName + "/r1", reopened), OverlapMessage);
        await fixture.CreatePathOperationAsync("op-restart-parent2", ParentPair, ParentLeftRoot, NestedChildName + "/r2",
            reopened);
        await RefuseAsync(() => fixture.CreatePathOperationAsync("op-restart-child2", ChildPair, ChildLeftRoot,
            "r2", reopened), OverlapMessage);

        // The ancestor/subtree fence across the alias also survives the restart, on its own retained file so
        // the subtree reservation cannot collide with the earlier exact-path reservations.
        using var subtreeFixture = await Fixture.CreateAsync(Path.Combine(root, "fence-restart-subtree"),
            publishParent: true, publishChild: true);
        await subtreeFixture.RegisterRootsAsync();
        await subtreeFixture.CreateDirectoryCrossPairMoveAsync("op-subtree-parent", ParentPair, ParentLeftRoot);
        var subtreeReopened = new SyncAuthorityDatabase(subtreeFixture.DatabasePath);
        await subtreeReopened.InitializeAsync(Installation, Profile, "ov637", "{}");
        await RefuseAsync(() => subtreeFixture.CreatePathOperationAsync("op-subtree-child", ChildPair,
            ChildLeftRoot, "r3", subtreeReopened), OverlapMessage);

    }

    // --------------------------------------------------------------------------------------------
    // OV637-4/OV637-6: an independently bound opaque corresponding overlap carries the shared fence and group.
    // --------------------------------------------------------------------------------------------
    private static async Task OpaqueCorrespondingAsync(string root)
    {
        using var fixture = await OpaqueFixture.CreateAsync(Path.Combine(root, "opaque"));
        // POSITIVE: an independently bound opaque corresponding overlap is admitted on both endpoints.
        await fixture.RegisterAdmittedAsync(fixture.ParentSnapshot, fixture.ChildSnapshot, fixture.Child);
        Require(await fixture.ActiveRootCountAsync() == 3,
            "OV637-4 the bound opaque corresponding child is admitted alongside the retained parent");
        var pinned = SyncPairPartitionPolicy.TryCorrespondingIncluded(fixture.Parent, fixture.Child,
            out var opaqueEnclosing, out var opaqueNested, out var opaqueRelative,
            out var opaqueMatches, fixture.ParentProofs(), fixture.ChildProofs());
        Require(pinned && ReferenceEquals(opaqueEnclosing, fixture.Parent) &&
                ReferenceEquals(opaqueNested, fixture.Child) && opaqueMatches.Count == 2 &&
                string.Equals(opaqueRelative, NestedChildName, StringComparison.OrdinalIgnoreCase),
            "OV637-4 the bound opaque correspondence pins the enclosing pair, the nested pair, both endpoint " +
            "matches and the shared relative translation");
        Require(!SyncPairPartitionPolicy.TryCorrespondingIncluded(fixture.Parent, fixture.Child,
                out _, out _, out _, out _, null, null),
            "OV637-4 the same opaque pair is never admitted without the required bound hierarchy proof");

        // OV637-6 swapped-side: the cloud compatibility port is chosen by the endpoint's ROLE, not by its ordered
        // position, so an opaque pair cannot move its cloud root to another port by reversing its endpoints. The
        // swapped-side concern therefore reduces to the endpoint bijection the shared decision already carries
        // (proved by the local/local swapped-endpoint case); there is no second port for an opaque cloud root.
        Require(SyncEndpointContext.CompatibilityPort(SyncEndpointKind.Local, SyncEndpointKind.GoogleDrive,
                    SyncPairSide.Right) == SyncAuthorityCloudSides.LogicalRight &&
                SyncEndpointContext.CompatibilityPort(SyncEndpointKind.GoogleDrive, SyncEndpointKind.Local,
                    SyncPairSide.Left) == SyncAuthorityCloudSides.LogicalRight,
            "OV637-6 the cloud compatibility port is role-based, so reversing the pair's ordered endpoints " +
            "cannot move the opaque root to another port or defeat the aliased fence");

        // The durable reservation projection reads PUBLISHED pair documents. An opaque corresponding pair
        // cannot be published through the unchanged pair-set validator (SyncPairStore.Normalize) because that
        // validator calls the correspondence entry without any hierarchy proof and refuses the opaque
        // nesting, so the opaque alias fence is exercised here through the SAME two production decision
        // entries the projection uses, with the same independently bound proofs (see OPAQUE-FENCE-OPEN in the
        // result ledger). Real reservations on both retained cloud roots stay available for the fence itself.
        await RefuseAsync(() => fixture.RegisterLocalPlanningRootAsync(),
            "The root overlaps another active pair or recovery authority boundary.");
        await fixture.RetainRootObservationsAsync();
        Require(await fixture.ActiveRootCountAsync() == 4,
            "OV637-4 local planning root requires current bound opaque hierarchy, not display ancestry");
        await fixture.CreateParentGoogleOperationAsync("op-opaque-parent", NestedChildName + "/a");
        await fixture.CreateChildGoogleOperationAsync("op-opaque-child-z", "z", fixture.Evidence());

        // GROUP: the related-overlap grouping admits the relation only from the bound proof.
        var proofs = new Dictionary<string, IReadOnlyList<SyncPairPartitionPolicy.SyncOpaqueCloudEndpointProof>>
            (StringComparer.Ordinal)
        {
            [fixture.Parent.Id] = fixture.ParentProofs(),
            [fixture.Child.Id] = fixture.ChildProofs()
        };
        var grouped = SyncPairPartitionPolicy.RelatedOverlapGroups([fixture.Parent, fixture.Child],
            pair => proofs.TryGetValue(pair.Id, out var value) ? value : null);
        Require(grouped.Count == 1 && grouped[0].Count == 2 &&
                grouped[0].Contains(fixture.Parent.Id, StringComparer.Ordinal) &&
                grouped[0].Contains(fixture.Child.Id, StringComparer.Ordinal),
            "OV637-4 the bound opaque overlap forms one related component from independently bound proofs");
        var unproven = SyncPairPartitionPolicy.RelatedOverlapGroups([fixture.Parent, fixture.Child], _ => null);
        Require(unproven.Count == 0,
            "OV637-4 the same opaque pair is never grouped without the required bound hierarchy proof");
    }

    private static async Task OpaqueRefusalAsync(string root)
    {
        // ABSENT hierarchy: no proof, so the opaque registration and the opaque group both fail closed.
        using (var fixture = await OpaqueFixture.CreateAsync(Path.Combine(root, "opaque-absent")))
        {
            await RefuseAsync(() => fixture.RegisterAdmittedAsync(null, null, fixture.Child));
            Require(await fixture.ActiveRootCountAsync() == 2,
                "OV637-2 an absent opaque hierarchy writes no candidate root");
        }
        // FORGED hierarchy: a snapshot bound to another root/namespace is refused, not used as authority.
        using (var fixture = await OpaqueFixture.CreateAsync(Path.Combine(root, "opaque-forged")))
        {
            var forged = fixture.ChildSnapshot with { RootBindingId = "forged-root-binding" };
            await RefuseAsync(() => fixture.RegisterAdmittedAsync(fixture.ParentSnapshot, forged, fixture.Child));
            Require(await fixture.ActiveRootCountAsync() == 2,
                "OV637-2 a forged or unbound opaque hierarchy writes no candidate root");
        }
    }

    // --------------------------------------------------------------------------------------------
    // OV637-6 sentinel: a bypassed proof, and a reservation written without the shared Host publication
    // gate, cannot pass; a path with no aliased competitor is never refused.
    // --------------------------------------------------------------------------------------------
    private static async Task SafeSentinelAsync(string root)
    {
        using var fixture = await OpaqueFixture.CreateAsync(Path.Combine(root, "safe-sentinel"));
        await fixture.RegisterAdmittedAsync(fixture.ParentSnapshot, fixture.ChildSnapshot, fixture.Child);
        // 1. A BYPASSED PROOF: with no bound hierarchy proof the shared decision never admits or pins the
        // opaque correspondence, in either ordering, and never forms the related component.
        Require(SyncPairPartitionPolicy.DecideCorrespondingIncluded(fixture.Parent, fixture.Child) !=
                SyncPairPartitionRelation.CorrespondingIncluded &&
            SyncPairPartitionPolicy.DecideCorrespondingIncluded(fixture.Child, fixture.Parent) !=
                SyncPairPartitionRelation.CorrespondingIncluded,
            "OV637-6 sentinel: a bypassed proof never admits the opaque correspondence in either ordering");
        Require(!SyncPairPartitionPolicy.TryCorrespondingIncluded(fixture.Parent, fixture.Child,
                out _, out _, out _, out _, null, null),
            "OV637-6 sentinel: a bypassed proof never pins the opaque correspondence");
        Require(SyncPairPartitionPolicy.RelatedOverlapGroups([fixture.Parent, fixture.Child], _ => null).Count == 0,
            "OV637-6 sentinel: a bypassed proof never forms the related component");
        // 2. A SKIPPED HOST: with both pairs published but the bound context stale, the durable reservation
        // writer reached DIRECTLY still refuses the aliased collision instead of treating the missing alias
        // edges as independent roots.
        await fixture.RetainRootObservationsAsync(registerLocalRoot: false);
        await fixture.Db.RetainPairDefinitionAsync(fixture.Child, select: false);
        await fixture.ExpireRootObservationsAsync();
        Require(await fixture.ActiveRootCountAsync() == 3,
            "OV637-6 the local planning root has not already been registered");
        await RefuseAsync(() => fixture.RegisterLocalPlanningRootAsync(),
            "The root overlaps another active pair or recovery authority boundary.");
        Require(await fixture.ActiveRootCountAsync() == 3,
            "OV637-6 stale proof refusal writes no local planning root");
        await fixture.RestoreRootObservationsAsync();
        await fixture.RegisterLocalPlanningRootAsync();
        Require(await fixture.ActiveRootCountAsync() == 4,
            "OV637-6 the identical local root registration succeeds after proof restoration");
        // Operations require both endpoints. Stale the observations again only after
        // establishing that the missing local root is admitted by the restored proof.
        await fixture.ExpireRootObservationsAsync();
        await fixture.CreateChildGoogleOperationAsync("op-sentinel-child", "z", null);
        await RefuseAsync(() => fixture.CreateParentGoogleOperationAsync("op-sentinel-parent", NestedChildName + "/z"),
            "no bound provider hierarchy context is available");
        Require(await fixture.ActiveRootCountAsync() == 4,
            "OV637-6 sentinel: the refused bypass wrote no authority row");
        // 3. NOT OVER-BLOCKING: once the bound context is current again, a path with no aliased competitor is
        // admitted exactly as before.
        await fixture.RestoreRootObservationsAsync();
        await fixture.CreateParentGoogleOperationAsync("op-sentinel-control", NestedChildName + "/a");
        Require(await fixture.Db.FindRetainedActionIdAsync("idempotency-op-sentinel-control") is not null,
            "OV637-6 the unrelated control operation is durably admitted");
    }
    // OV637-4/OV637-6: the retained observation supplies the bound context the opaque load/Retain/Save path
    // needs, and the real durable reservation then fences the aliased object in BOTH creation orders and across
    // a reopen of the same authority database. Caller evidence is deliberately EMPTY throughout, so the only
    // context is the retained observation of each already-published cloud root.
    private static async Task OpaqueAliasedCollisionAsync(string root)
    {
        using var fixture = await OpaqueFixture.CreateAsync(Path.Combine(root, "opaque-collision"));
        await fixture.RegisterAdmittedAsync(fixture.ParentSnapshot, fixture.ChildSnapshot, fixture.Child);
        // DISCRIMINATION: with no usable retained observation the unchanged pair-set validator refuses the
        // opaque corresponding child on the real Retain path.
        await RefuseAsync(() => fixture.Db.RetainPairDefinitionAsync(fixture.Child, select: false),
            "does not exclude the whole nested subtree");
        // The retained observation of each published cloud root supplies the context, and the SAME production
        // Retain then admits the corresponding pair with no caller-supplied evidence.
        await fixture.RetainRootObservationsAsync();
        await fixture.Db.RetainPairDefinitionAsync(fixture.Child, select: false);
        Require(await fixture.ChildRetainedAsync(),
            "OV637-4 the opaque corresponding child is retained from the retained observation alone");
        // ORDER 1: the nested child reserves the object first; the enclosing parent alias of the same
        // physical object must then refuse.
        await fixture.CreateChildGoogleOperationAsync("op-collide-child-first", "z", null);
        await RefuseAsync(() => fixture.CreateParentGoogleOperationAsync("op-collide-parent-second",
            NestedChildName + "/z"), OverlapMessage);
        // ORDER 2: the enclosing parent reserves first; the nested child alias must then refuse.
        await fixture.CreateParentGoogleOperationAsync("op-collide-parent-first", NestedChildName + "/q");
        await RefuseAsync(() => fixture.CreateChildGoogleOperationAsync("op-collide-child-second", "q", null),
            OverlapMessage);
        // RESTART: a NEW database instance over the same file still fences the identical aliased collision.
        var reopened = fixture.Reopen();
        await RefuseAsync(() => fixture.CreateParentGoogleOperationAsync(reopened,
            "op-collide-parent-reopened", NestedChildName + "/z"), OverlapMessage);
        // OV637-6 ISSUED: a leased, nonterminal aliased reservation is fenced across the aliased translation.
        await fixture.CreateChildGoogleOperationAsync("op-collide-issued", "z4", null);
        await fixture.Db.AcquireOperationLeaseAsync((await fixture.Db.FindRetainedActionIdAsync("idempotency-op-collide-issued"))!, "worker-ov637-issued",
            TimeSpan.FromMinutes(5));
        await RefuseAsync(() => fixture.CreateParentGoogleOperationAsync("op-collide-parent-issued",
            NestedChildName + "/z4"), OverlapMessage);
        // OV637-6 ANCESTOR: a retained child SUBTREE reservation fences the aliased enclosing descendant.
        await fixture.CreateChildSubtreeOperationAsync("op-collide-ancestor", "z6");
        await RefuseAsync(() => fixture.CreateParentGoogleOperationAsync("op-collide-parent-ancestor",
            NestedChildName + "/z6/inner"));
        // D3/OV637-5: with the SAME published pair set but NO retained observation anywhere, a live competing
        // reservation on the corresponding opaque peer root makes the missing context itself the refusal - and
        // the identical reservation is admitted once the retained context exists. A pair with no live competitor
        // is never refused for merely existing.
        using (var noContext = await OpaqueFixture.CreateAsync(Path.Combine(root, "opaque-no-context")))
        {
            await noContext.RegisterAdmittedAsync(noContext.ParentSnapshot, noContext.ChildSnapshot,
                noContext.Child);
            // The child is published legitimately from the bound context; the context then goes stale.
            await noContext.RetainRootObservationsAsync();
            await noContext.Db.RetainPairDefinitionAsync(noContext.Child, select: false);
            await noContext.ExpireRootObservationsAsync();
            await noContext.CreateChildGoogleOperationAsync("op-nocx-child", "z", null);
            await RefuseAsync(() => noContext.CreateParentGoogleOperationAsync("op-nocx-parent", NestedChildName + "/a"),
                "no bound provider hierarchy context is available");
            // The identical reservation is admitted again once the bound context is current.
            await noContext.RestoreRootObservationsAsync();
            await noContext.CreateParentGoogleOperationAsync("op-nocx-parent-ctx", NestedChildName + "/a");
            Require(await noContext.ActiveRootCountAsync() == 4,
                "OV637-5 the refused no-context reservation wrote no authority row");
        }
    }

    // --------------------------------------------------------------------------------------------
    // OV637-4: a real coordinated Host run of two HEALTHY related ready pairs must complete both pairs
    // without a spurious bounded-rounds planning block and without stale replay.
    // --------------------------------------------------------------------------------------------
    private static async Task RealHostRelatedRunAsync(string root)
    {
        if (!OperatingSystem.IsWindows()) return;
        var rigRoot = Path.Combine(root, "host-run");
        Directory.CreateDirectory(rigRoot);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(rigRoot, "state", "instance.lock"));
        using var http = new HttpClient(new NoProviderRequests());
        var product = new SyncProductStateStore(instance.StateAuthority);
        await product.UpdateAsync(current => current with { InstallationId = "installation-ov637-host" });
        using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, http);
        await host.Authority.InitializeAsync("installation-ov637-host", Profile, "ov637-host", "{}");
        var parentLocal = Directory.CreateDirectory(Path.Combine(rigRoot, "local")).FullName;
        var parentMirror = Directory.CreateDirectory(Path.Combine(rigRoot, "mirror")).FullName;
        var childLocal = Directory.CreateDirectory(Path.Combine(parentLocal, NestedChildName)).FullName;
        var childMirror = Directory.CreateDirectory(Path.Combine(parentMirror, NestedChildName)).FullName;

        // The child is bound first, so its own determined private control names can be excluded by the
        // enclosing pair. The enclosing pair is then admitted by the REAL local overlap guard as a
        // corresponding INCLUDED nesting.
        SyncPair child;
        var admissionDiagnostics = new List<string>();
        SyncPairPartitionPolicy.CorrespondingAdmissionDiagnosticForTest = admissionDiagnostics.Add;
        try
        {
            child = (await host.BindLocalEndpointPairAsync(LocalDraft("Child pair", childLocal, childMirror,
                []))).Pair;
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException)
        {
            throw new InvalidDataException(
                "OV637-3/D1 the real Host could not bind the CHILD pair: " + error.Message);
        }
        Require(admissionDiagnostics.Count == 0 || child.Id.Length > 0,
            "OV637-3/D1 the child bind produced no admission tuple, so no retained row was compared");
        var names = ChildControlFolderNames(host.Authority.DatabasePath, child.Id);
        var rules = names.Select(name => new SyncSelectionRule(name, false)).ToArray();
        admissionDiagnostics.Clear();
        SyncPair parent;
        try
        {
            parent = (await host.BindLocalEndpointPairAsync(LocalDraft("Parent pair", parentLocal, parentMirror,
                rules))).Pair;
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException)
        {
            throw new InvalidDataException(
                "OV637-3/D1 the real Host could not bind the ENCLOSING parent pair: " + error.Message +
                " || admission tuple: " + (admissionDiagnostics.Count == 0 ? "(none captured)"
                    : string.Join(" ; ", admissionDiagnostics)));
        }
        SyncPairPartitionPolicy.CorrespondingAdmissionDiagnosticForTest = null;
        Require(SyncPairPartitionPolicy.DecideCorrespondingIncluded(parent, child) ==
                SyncPairPartitionRelation.CorrespondingIncluded,
            "OV637-4 the real host retained both halves of one corresponding included nesting");
        var grouped = SyncPairPartitionPolicy.RelatedOverlapGroups([parent, child]);
        Require(grouped.Count == 1 && grouped[0].Count == 2,
            "OV637-4 the real host groups the two related ready pairs into one component");

        // Fixture approval only: the first-sync review gate is a separate user decision and is not the
        // scheduling behaviour this case proves. No guard, planner or scheduler seam is replaced.
        await host.Authority.RetainPairDefinitionAsync(parent with { InitialReviewApproved = true }, true);
        await host.Authority.RetainPairDefinitionAsync(child with { InitialReviewApproved = true }, false);

        var first = await host.RunPairsAsync([parent, child], "OV637 related fair run");
        var parentItem = first.Items.Single(item => item.Pair.Id == parent.Id);
        var childItem = first.Items.Single(item => item.Pair.Id == child.Id);
        Require(!BoundedRoundsBlock(parentItem.Result) && !BoundedRoundsBlock(childItem.Result),
            "OV637-4 both healthy related pairs finish without a spurious bounded-rounds planning block");
        Require(parentItem.Result is not null && childItem.Result is not null,
            "OV637-4 the real coordinated run returns a result for both related pairs");

        // A second run over unchanged content must not replay any effect for either pair.
        var second = await host.RunPairsAsync([parent, child], "OV637 related fair rerun");
        foreach (var id in new[] { parent.Id, child.Id })
        {
            var item = second.Items.Single(entry => entry.Pair.Id == id);
            Require(item.Result is not null && item.Result.Uploaded == 0 && item.Result.Downloaded == 0,
                "OV637-4 the related rerun replayed no effect for " + id);
        }
    }

    private static bool BoundedRoundsBlock(SyncRunResult? result) =>
        result is not null && result.Decisions.Any(decision => decision.Kind == SyncDecisionKind.Blocked &&
            decision.Reason.Contains("did not become effect-free within its bounded planning rounds",
                StringComparison.Ordinal));

    private static SyncPair LocalDraft(string name, string left, string right,
        IReadOnlyList<SyncSelectionRule> rules)
    {
        using var leftLease = PilotDirectoryLease.Open(left);
        using var rightLease = PilotDirectoryLease.Open(right);
        var endpoints = new SyncPairEndpoints(
            SyncEndpoint.Local(leftLease.Path, new SyncLocalEndpointIdentity(
                leftLease.Identity.VolumeSerialNumber, leftLease.Identity.FileId)),
            SyncEndpoint.Local(rightLease.Path, new SyncLocalEndpointIdentity(
                rightLease.Identity.VolumeSerialNumber, rightLease.Identity.FileId)), SyncMode.TwoWay);
        var template = new SyncPair("template", name, leftLease.Path, leftLease.Identity.VolumeSerialNumber,
            leftLease.Identity.FileId, rightLease.Path, rightLease.Identity.FileId.ToString("x16"),
            SelectionRules: rules, SelectionDefaultIncluded: true, Provider: SyncProviders.Local);
        return SyncEndpointPairDraft.Create(endpoints, name, template);
    }

    private static IReadOnlyList<string> ChildControlFolderNames(string databasePath, string pairId)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var json = ScalarText(databasePath, "SELECT pair_json FROM sync_pair WHERE pair_id='" + pairId + "';");
        if (json.Length > 0)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
            foreach (var member in new[] { "ControlNamespace", "RightControlNamespace" })
                foreach (var side in new[] { "Local", "Remote" })
                    if (node[member]?[side]?["RootPath"]?.GetValue<string>() is { Length: > 0 } rootPath)
                        names.Add(Path.GetFileName(rootPath.TrimEnd('\\', '/')));
        }
        names.Add(ControlFolderName(pairId));
        names.Add(RightControlFolderName(pairId));
        return names.Where(name => name.Length > 0).ToArray();
    }

    private static string ScalarText(string databasePath, string sql)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + databasePath);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return value is null or DBNull ? string.Empty
            : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private sealed class NoProviderRequests : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A local related-pair run must not contact a provider.");
    }

    // The swapped-endpoint local/local fixture: the child stores its mirror root on the compatibility

    // "local" port and its nested root on the "dropbox" port.
    private sealed class SwappedFixture : IDisposable
    {
        internal const string ParentPairId = "pair-swap-parent";
        internal const string ChildPairId = "pair-swap-child01";
        internal const string ParentLeftRoot = "root-swap-parent-local";
        internal const string ParentRightRoot = "root-swap-parent-right";
        internal const string ChildLeftRoot = "root-swap-child-local";
        internal const string ChildRightRoot = "root-swap-child-right";

        internal SyncAuthorityDatabase Db = null!;
        internal SyncAuthorityCommandAdmission Admission = null!;
        internal string ParentLeft = "";
        internal string ParentRight = "";

        internal static async Task<SwappedFixture> CreateAsync(string root)
        {
            var fixture = new SwappedFixture
            {
                ParentLeft = Path.Combine(root, "Parent"),
                ParentRight = Path.Combine(root, "ParentMirror")
            };
            var childLocal = Path.Combine(root, "ParentMirror", NestedChildName);
            var childNested = Path.Combine(root, "Parent", NestedChildName);
            Directory.CreateDirectory(childLocal);
            Directory.CreateDirectory(childNested);
            fixture.Db = new SyncAuthorityDatabase(Path.Combine(root, "authority.db"));
            await fixture.Db.InitializeAsync(Installation, Profile, "ov637-swap", "{}");
            // The child's LEFT endpoint is the mirror root and its RIGHT endpoint is the nested root.
            var child = LocalPair(ChildPairId, childLocal, childNested, []) with
            {
                ControlNamespace = Control(fixture.ParentLeft, ChildPairId)
            };
            var parent = LocalPair(ParentPairId, fixture.ParentLeft, fixture.ParentRight,
                [new SyncSelectionRule(ControlFolderName(ChildPairId), false),
                    new SyncSelectionRule(RightControlFolderName(ChildPairId), false)]);
            await fixture.Db.RegisterPairAsync(ParentPairId, 1);
            await fixture.Db.RegisterPairAsync(ChildPairId, 1);
            fixture.Admission = await fixture.Db.CreateAcceptedCommandAdmissionAsync(null, "run_all", null,
                null, null, System.Text.Json.JsonSerializer.Serialize(new { pairIds = new[] { ParentPairId, ChildPairId } }),
                SyncAuthorityCommandPurpose.NormalEffect);
            await fixture.Db.TransitionCommandRequestAsync(fixture.Admission.CommandRequestId, "executing");
            await fixture.Db.RegisterRootBindingAsync(ParentLeftRoot, ParentPairId, "local", 1, "{}",
                fixture.ParentLeft);
            await fixture.Db.RegisterRootBindingAsync(ParentRightRoot, ParentPairId, "dropbox", 1, "{}",
                fixture.ParentRight);
            await fixture.Db.RetainPairDefinitionAsync(parent, true);
            await fixture.Db.RegisterRootBindingAsync(ChildRightRoot, ChildPairId, "dropbox", 1, "{}",
                childNested, partitionCandidate: child);
            await fixture.Db.RegisterRootBindingAsync(ChildLeftRoot, ChildPairId, "local", 1, "{}",
                childLocal, partitionCandidate: child);
            await fixture.Db.RetainPairDefinitionAsync(child, false);
            return fixture;
        }

        internal Task RegisterRootsAsync() => Task.CompletedTask;

        internal async Task CreatePathOperationAsync(string operationId, string pairId, string rootBindingId,
            string path)
        {
            var objectId = "object-" + operationId;
            await Db.RegisterObjectAsync(objectId, "file");
            await CreateFixtureOperationAsync(Db, new SyncAuthorityOperationPlan(operationId, "idempotency-" + operationId,
                SyncAuthorityOperationKind.UpdateRemote, pairId, objectId, 1, Profile, "{}", 1,
                [new SyncAuthorityMember("member-" + operationId, pairId, objectId, "lock", rootBindingId,
                    null, null, null, path, "{}")], [], []), admission: Admission);
        }

        public void Dispose() { }
    }

    // One local+Google-Drive corresponding pair set, admitted only from an independently bound provider chain.
    private sealed class OpaqueFixture : IDisposable
    {
        private const string ProviderAccount = "account-ov637-opaque";
        private const string RootNs = "ns-ov637-opaque";
        private const string HomeNs = "ns-ov637-opaque-home";
        private const string Connection = "conn-ov637-opaque";
        private const string Corpus = "corpus-ov637-opaque";
        private const string ParentLocalRootId = "rootbinding-opaque-parent-local";
        private const string ParentCloudRootId = "rootbinding-opaque-parent-cloud";
        private const string ChildLocalRootId = "rootbinding-opaque-child-local";
        internal const string ChildCloudRootId = "rootbinding-opaque-child-cloud";

        internal SyncAuthorityDatabase Db = null!;
        internal SyncAuthorityCommandAdmission Admission = null!;
        internal SyncPair Parent = null!;
        internal SyncPair Child = null!;
        internal SyncAuthorityCloudHierarchySnapshot ParentSnapshot = null!;
        internal SyncAuthorityCloudHierarchySnapshot ChildSnapshot = null!;
        private string _installation = "";
        private string _provider = "";
        private string _providerAccountId = "";
        private string _namespaceId = "";

        internal static async Task<OpaqueFixture> CreateAsync(string root)
        {
            var fixture = new OpaqueFixture { _installation = "installation-ov637-opaque" };
            Directory.CreateDirectory(root);
            var provider = SyncEndpoints.ProviderLiteral(SyncEndpointKind.GoogleDrive)!;
            var providerAccountId = SyncAuthorityProviderAccountIdentity.DeriveId(provider, ProviderAccount,
                RootNs, HomeNs);
            var namespaceId = ProviderAccount;
            fixture._provider = provider;
            fixture._providerAccountId = providerAccountId;
            fixture._namespaceId = namespaceId;
            fixture.Child = OpaquePair(root, "child", Path.Combine(root, "parentLocal", NestedChildName),
                "folder-child", "/P/Child", []);
            fixture.Child = fixture.Child with
            {
                ControlNamespace = Control(Path.Combine(root, "parentLocal"), fixture.Child.Id)
            };
            fixture.Parent = OpaquePair(root, "parent", Path.Combine(root, "parentLocal"),
                "folder-parent", "/P", [new SyncSelectionRule(ControlFolderName(fixture.Child.Id), false),
                    new SyncSelectionRule(RightControlFolderName(fixture.Child.Id), false)]);
            fixture.Db = new SyncAuthorityDatabase(Path.Combine(root, "authority.db"));
            await fixture.Db.InitializeAsync(fixture._installation, Profile, "ov637-opaque", "{}");
            await fixture.Db.RegisterProviderAccountAsync(providerAccountId, fixture._installation, ProviderAccount,
                RootNs, HomeNs, "OV637 opaque fixture", 1, default, provider);
            fixture.ParentSnapshot = Snapshot(ParentCloudRootId, providerAccountId, namespaceId,
                [Corpus, "folder-parent"], ["Corpus", "Parent"], provider);
            fixture.ChildSnapshot = Snapshot(ChildCloudRootId, providerAccountId, namespaceId,
                [Corpus, "folder-parent", "folder-child"], ["Corpus", "Parent", NestedChildName], provider);
            await fixture.Db.RegisterPairAsync(fixture.Parent.Id, fixture.Parent.PolicyRevision,
                SyncAuthorityPairConfiguration.FromPair(fixture.Parent));
            await fixture.Db.RetainPairDefinitionAsync(fixture.Parent, select: false);
            await fixture.Db.RegisterRootBindingAsync(ParentLocalRootId, fixture.Parent.Id, "local", 1, "{}",
                fixture.Parent.LocalRoot);
            await fixture.Db.RegisterCloudEndpointRootBindingAsync(ParentCloudRootId, fixture.Parent.Id, "dropbox",
                1, fixture._installation, provider, providerAccountId, namespaceId, "folder-parent", "/P",
                default, [fixture.ParentSnapshot]);
            await fixture.Db.RegisterPairAsync(fixture.Child.Id, fixture.Child.PolicyRevision,
                SyncAuthorityPairConfiguration.FromPair(fixture.Child));
            fixture.Admission = await fixture.Db.CreateAcceptedCommandAdmissionAsync(null, "run_all", null,
                null, null, System.Text.Json.JsonSerializer.Serialize(new { pairIds = new[] { fixture.Parent.Id, fixture.Child.Id } }),
                SyncAuthorityCommandPurpose.NormalEffect);
            await fixture.Db.TransitionCommandRequestAsync(fixture.Admission.CommandRequestId, "executing");
            return fixture;
        }

        internal IReadOnlyList<SyncAuthorityCloudHierarchySnapshot> Evidence() =>
            [ParentSnapshot, ChildSnapshot];

        internal IReadOnlyList<SyncPairPartitionPolicy.SyncOpaqueCloudEndpointProof> ParentProofs() =>
            [Proof(Parent, ParentSnapshot, ParentCloudRootId)];

        internal IReadOnlyList<SyncPairPartitionPolicy.SyncOpaqueCloudEndpointProof> ChildProofs() =>
            [Proof(Child, ChildSnapshot, ChildCloudRootId)];

        private SyncPairPartitionPolicy.SyncOpaqueCloudEndpointProof Proof(SyncPair pair,
            SyncAuthorityCloudHierarchySnapshot snapshot, string rootBindingId)
        {
            var endpoints = SyncCloudPairConfiguration.SavedEndpoints(pair)!;
            var endpoint = endpoints.Left.IsLocal ? endpoints.Right : endpoints.Left;
            var identity = new SyncAuthorityCloudRootIdentity(snapshot.Provider, snapshot.ProviderAccountId,
                snapshot.NamespaceId, endpoint.FolderId!, endpoint.FolderPath, rootBindingId,
                SyncAuthorityCloudRootIdentity.PairRootAuthority, "dropbox");
            return new SyncPairPartitionPolicy.SyncOpaqueCloudEndpointProof(snapshot,
                snapshot.ProviderAccountId, rootBindingId, identity);
        }

        internal async Task RegisterAdmittedAsync(SyncAuthorityCloudHierarchySnapshot? parent,
            SyncAuthorityCloudHierarchySnapshot? child, SyncPair candidate)
        {
            var evidence = new List<SyncAuthorityCloudHierarchySnapshot>();
            if (parent is not null) evidence.Add(parent);
            if (child is not null) evidence.Add(child);
            // The opaque proof is exercised through the CLOUD endpoints, so only the child's typed cloud root is
            // registered here; the published pair definitions still carry both local endpoints for the shared
            // correspondence decision, and no unproven local-row waiver is fabricated.
            await Db.RegisterCloudEndpointRootBindingForCandidateAsync(ChildCloudRootId, Child.Id, "dropbox", 1,
                _installation, _provider, _providerAccountId, _namespaceId, "folder-child", "/P/Child",
                candidate, default, evidence);
        }

        internal Task CreateParentGoogleOperationAsync(string operationId, string path) =>
            CreateParentGoogleOperationAsync(Db, operationId, path, null);

        internal async Task CreateParentGoogleOperationAsync(SyncAuthorityDatabase db, string operationId,
            string path, IReadOnlyList<SyncAuthorityCloudHierarchySnapshot>? evidence = null)
        {
            var objectId = "object-" + operationId;
            await db.RegisterObjectAsync(objectId, "file");
            await CreateFixtureOperationAsync(db, new SyncAuthorityOperationPlan(operationId, "idempotency-" + operationId,
                SyncAuthorityOperationKind.UpdateRemote, Parent.Id, objectId, Parent.PolicyRevision, Profile, "{}",
                1, [new SyncAuthorityMember("member-" + operationId, Parent.Id, objectId, "lock",
                    ParentCloudRootId, null, null, null, path, "{}")], [], []), Admission, evidence);
        }

        internal async Task CreateChildGoogleOperationAsync(string operationId, string path,
            IReadOnlyList<SyncAuthorityCloudHierarchySnapshot>? evidence)
        {
            var objectId = "object-" + operationId;
            await Db.RegisterObjectAsync(objectId, "file");
            await CreateFixtureOperationAsync(Db, new SyncAuthorityOperationPlan(operationId, "idempotency-" + operationId,
                SyncAuthorityOperationKind.UpdateRemote, Child.Id, objectId, Child.PolicyRevision, Profile, "{}",
                1, [new SyncAuthorityMember("member-" + operationId, Child.Id, objectId, "lock",
                    ChildCloudRootId, null, null, null, path, "{}")], [], []), Admission, evidence);
        }

        /// <summary>D3/OV637-5: make both published cloud observations stale by advancing their root generation.</summary>
        internal async Task ExpireRootObservationsAsync() =>
            await ExecuteSqlAsync("UPDATE root_binding SET generation=generation+1 WHERE root_binding_id IN ('" +
                ParentCloudRootId + "','" + ChildCloudRootId + "');");

        /// <summary>Restore the exact retained generations so the bound context is current again.</summary>
        internal async Task RestoreRootObservationsAsync() =>
            await ExecuteSqlAsync("UPDATE root_binding SET generation=generation-1 WHERE root_binding_id IN ('" +
                ParentCloudRootId + "','" + ChildCloudRootId + "');");

        internal async Task ExecuteSqlAsync(string sql)
        {
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Db.DatabasePath }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>OV637-6: a retained child SUBTREE reservation, the one kind allowed to hold prefix authority.</summary>
        internal async Task CreateChildSubtreeOperationAsync(string operationId, string path)
        {
            var objectId = "object-" + operationId;
            await Db.RegisterObjectAsync(objectId, "directory");
            await CreateFixtureOperationAsync(Db, new SyncAuthorityOperationPlan(operationId, "idempotency-" + operationId,
                SyncAuthorityOperationKind.CrossPairMove, Child.Id, objectId, Child.PolicyRevision, Profile,
                System.Text.Json.JsonSerializer.Serialize(new { entryKind = "Directory", sourcePairId = Child.Id, destinationPairId = Parent.Id }), 1,
                [new SyncAuthorityMember("member-" + operationId, Child.Id, objectId, "lock", ChildCloudRootId,
                    null, null, null, path, "{}")], [], [])
            {
                SubtreeReservations =
                [
                    new SyncAuthoritySubtreeReservation(Child.Id, ChildCloudRootId, path),
                    new SyncAuthoritySubtreeReservation(Child.Id, ChildCloudRootId, path + "/a"),
                    new SyncAuthoritySubtreeReservation(Child.Id, ChildCloudRootId, path + "/b"),
                    new SyncAuthoritySubtreeReservation(Child.Id, ChildCloudRootId, path + "/c")
                ]
            }, admission: Admission);
        }

        /// <summary>D2/OV637-4: retain each published cloud root's own provider-verified observation.</summary>
        internal async Task RetainRootObservationsAsync(bool registerLocalRoot = true)
        {
            await Db.RetainOpaqueHierarchyObservationAsync(ParentSnapshot, Parent.Id, "dropbox", 1);
            await Db.RetainOpaqueHierarchyObservationAsync(ChildSnapshot, Child.Id, "dropbox", 1);
            // Planning requires both endpoints. Admit the local endpoint only after the
            // independently bound cloud observations can prove the complete correspondence.
            if (registerLocalRoot) await RegisterLocalPlanningRootAsync();
        }

        internal Task RegisterLocalPlanningRootAsync() =>
            Db.RegisterRootBindingAsync(ChildLocalRootId, Child.Id, "local", 1, "{}",
                Child.LocalRoot, partitionCandidate: Child);

        /// <summary>A NEW authority database instance over the SAME file, for the restart leg.</summary>
        internal SyncAuthorityDatabase Reopen() => new SyncAuthorityDatabase(Db.DatabasePath);

        /// <summary>True when the child pair definition is really retained (pair_json written).</summary>
        internal async Task<bool> ChildRetainedAsync()
        {
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = Db.DatabasePath,
                    Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly
                }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sync_pair WHERE pair_id='" + Child.Id +
                "' AND pair_json IS NOT NULL;";
            return Convert.ToInt32(await command.ExecuteScalarAsync(),
                System.Globalization.CultureInfo.InvariantCulture) == 1;
        }

        internal async Task<int> ActiveRootCountAsync()
        {
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = Db.DatabasePath,
                    Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly
                }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM root_binding WHERE state='active';";
            return Convert.ToInt32(await command.ExecuteScalarAsync(),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        public void Dispose() { }

        private static SyncPair OpaquePair(string root, string name, string localPath, string folderId,
            string folderPath, IReadOnlyList<SyncSelectionRule> rules)
        {
            Directory.CreateDirectory(localPath);
            using var lease = PilotDirectoryLease.Open(localPath);
            var local = SyncEndpoint.Local(lease.Path, new SyncLocalEndpointIdentity(
                lease.Identity.VolumeSerialNumber, lease.Identity.FileId));
            var cloud = SyncEndpoint.Cloud(SyncEndpointKind.GoogleDrive, Connection, ProviderAccount, folderId,
                folderPath);
            var endpoints = new SyncPairEndpoints(local, cloud, SyncMode.TwoWay);
            var template = new SyncPair("template", name, lease.Path, lease.Identity.VolumeSerialNumber,
                lease.Identity.FileId, folderPath, folderId, SelectionRules: rules,
                SelectionDefaultIncluded: true);
            return SyncEndpointPairDraft.Create(endpoints, name, template);
        }

        private static SyncAuthorityCloudHierarchySnapshot Snapshot(string rootBindingId, string providerAccountId,
            string namespaceId, IReadOnlyList<string> chain, IReadOnlyList<string> names, string provider) =>
            new(provider, providerAccountId, namespaceId, rootBindingId, chain[^1], chain)
            {
                CompleteToCorpusRoot = true,
                CorpusRootId = chain[0],
                ChainFolderNames = names,
                ObjectKeyCoverage = SyncCloudObjectKeySemantics.RequiredCoverage(provider),
                ChainNodeKeys = chain.Select(id => new SyncAuthorityCloudHierarchyNodeKeys(id,
                    [SyncCloudObjectKey.Native(provider, id)])).ToArray()
            };
    }

    private sealed class Fixture : IDisposable

    {
        internal SyncAuthorityDatabase Db = null!;
        internal SyncAuthorityCommandAdmission Admission = null!;
        internal string DatabasePath = "";
        internal string ParentLeft = "";
        internal string ParentRight = "";
        internal string ChildLeft = "";
        internal string ChildRight = "";
        internal SyncPair Parent = null!;
        internal SyncPair Child = null!;

        internal static async Task<Fixture> CreateAsync(string root, bool publishParent,
            string childRightLeaf = NestedChildName, bool includeChild = true, bool excludeControl = true,
            bool publishChild = false)
        {
            var fixture = new Fixture
            {
                ParentLeft = Path.Combine(root, "Parent"),
                ParentRight = Path.Combine(root, "ParentMirror"),
                ChildLeft = Path.Combine(root, "Parent", NestedChildName),
                ChildRight = Path.Combine(root, "ParentMirror", childRightLeaf)
            };
            Directory.CreateDirectory(fixture.ChildLeft);
            Directory.CreateDirectory(fixture.ChildRight);
            var rules = new List<SyncSelectionRule>();
            if (!includeChild) rules.Add(new SyncSelectionRule(NestedChildName, false));
            if (excludeControl)
            {
                rules.Add(new SyncSelectionRule(ControlName, false));
                rules.Add(new SyncSelectionRule(RightControlName, false));
            }
            fixture.Parent = LocalPair(ParentPair, fixture.ParentLeft, fixture.ParentRight, rules);
            fixture.Child = LocalPair(ChildPair, fixture.ChildLeft, fixture.ChildRight, []) with
            {
                ControlNamespace = Control(fixture.ParentLeft, ChildPair)
            };
            fixture.Db = new SyncAuthorityDatabase(Path.Combine(root, "authority.db"));
            fixture.DatabasePath = Path.Combine(root, "authority.db");
            await fixture.Db.InitializeAsync(Installation, Profile, "ov637", "{}");
            await fixture.Db.RegisterPairAsync(ParentPair, 1);
            await fixture.Db.RegisterPairAsync(ChildPair, 1);
            fixture.Admission = await fixture.Db.CreateAcceptedCommandAdmissionAsync(null, "run_all", null,
                null, null, System.Text.Json.JsonSerializer.Serialize(new { pairIds = new[] { ParentPair, ChildPair } }),
                SyncAuthorityCommandPurpose.NormalEffect);
            await fixture.Db.TransitionCommandRequestAsync(fixture.Admission.CommandRequestId, "executing");
            await fixture.Db.RegisterRootBindingAsync(ParentLeftRoot, ParentPair, "local", 1, "{}",
                fixture.ParentLeft);
            await fixture.Db.RegisterRootBindingAsync(ParentRightRoot, ParentPair, "dropbox", 1, "{}",
                fixture.ParentRight);
            if (publishParent) await fixture.Db.RetainPairDefinitionAsync(fixture.Parent, true);
            if (publishChild) await fixture.Db.RetainPairDefinitionAsync(fixture.Child, true);
            return fixture;
        }

        internal async Task RegisterRootsAsync()
        {
            await Db.RegisterRootBindingAsync(ChildLeftRoot, ChildPair, "local", 1, "{}", ChildLeft,
                partitionCandidate: Child);
            await Db.RegisterRootBindingAsync(ChildRightRoot, ChildPair, "dropbox", 1, "{}", ChildRight,
                partitionCandidate: Child);
        }

        internal async Task CreatePathOperationAsync(string operationId, string pairId, string rootBindingId,
            string path, SyncAuthorityDatabase? database = null)
        {
            var db = database ?? Db;
            var objectId = "object-" + operationId;
            await db.RegisterObjectAsync(objectId, "file");
            await CreateFixtureOperationAsync(db, new SyncAuthorityOperationPlan(operationId,
                "idempotency-" + operationId, SyncAuthorityOperationKind.UpdateRemote, pairId, objectId, 1,
                Profile, "{}", 1,
                [new SyncAuthorityMember("member-" + operationId, pairId, objectId, "lock", rootBindingId,
                    null, null, null, path, "{}")], [], []), admission: Admission);
        }

        // A retained directory cross-pair move is the one operation kind allowed to hold subtree-prefix
        // authority; its prefix over the enclosing root must fence the aliased child path.
        internal async Task CreateDirectoryCrossPairMoveAsync(string operationId, string pairId,
            string rootBindingId, SyncAuthorityDatabase? database = null)
        {
            var db = database ?? Db;
            var objectId = "object-" + operationId;
            await db.RegisterObjectAsync(objectId, "directory");
            await CreateFixtureOperationAsync(db, new SyncAuthorityOperationPlan(operationId,
                "idempotency-" + operationId, SyncAuthorityOperationKind.CrossPairMove, pairId, objectId, 1,
                Profile, System.Text.Json.JsonSerializer.Serialize(new { entryKind = "Directory", sourcePairId = pairId, destinationPairId = ChildPair }), 1,
                [new SyncAuthorityMember("member-" + operationId, pairId, objectId, "lock", rootBindingId,
                    null, null, null, NestedChildName, "{}")], [], [])
            {
                SubtreeReservations =
                [
                    new SyncAuthoritySubtreeReservation(pairId, rootBindingId, NestedChildName),
                    new SyncAuthoritySubtreeReservation(pairId, rootBindingId, NestedChildName + "/a"),
                    new SyncAuthoritySubtreeReservation(pairId, rootBindingId, NestedChildName + "/b"),
                    new SyncAuthoritySubtreeReservation(pairId, rootBindingId, NestedChildName + "/c")
                ]
            }, admission: Admission);
        }

        internal async Task<int> ActiveRootCountAsync()
        {
            var value = await ScalarAsync("SELECT count(*) FROM root_binding WHERE state='active';");
            return int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        internal async Task<string> ScalarAsync(string sql)
        {
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = Db.DatabasePath,
                    Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly
                }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(await command.ExecuteScalarAsync(),
                System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }

        public void Dispose() { }
    }

    // Reservation checks deliberately use synthetic lock-only objects, not transfer effects.
    // They still need genuine completed observation cuts and a freshly minted action ticket;
    // otherwise action admission rejects them before the alias fence under test is reached.
    private static readonly Dictionary<(string Database, string Pair), (string Local, string Cloud)> PlanningCuts = new();

    private static async Task CreateFixtureOperationAsync(SyncAuthorityDatabase db,
        SyncAuthorityOperationPlan plan, SyncAuthorityCommandAdmission admission,
        IReadOnlyList<SyncAuthorityCloudHierarchySnapshot>? evidence = null)
    {
        var pairId = plan.PairId ?? throw new InvalidOperationException("A reservation fixture requires a pair.");
        var roots = new Dictionary<string, string>();
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                         new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = db.DatabasePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT side,root_binding_id FROM root_binding WHERE pair_id=$pair AND side IN ('local','dropbox') AND state='active';";
            command.Parameters.AddWithValue("$pair", pairId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) roots.Add(reader.GetString(0), reader.GetString(1));
        }
        var key = (db.DatabasePath, pairId);
        if (!PlanningCuts.TryGetValue(key, out var cuts))
        {
            var local = await db.BeginScanGenerationAsync(pairId, roots["local"], "local", "manual", "{}");
            await db.CompleteScanGenerationAsync(local);
            var cloud = await db.BeginScanGenerationAsync(pairId, roots["dropbox"], "dropbox", "manual", "{}");
            await db.CompleteScanGenerationAsync(cloud);
            cuts = (local.ScanGenerationId, cloud.ScanGenerationId);
            PlanningCuts.Add(key, cuts);
        }
        var set = await db.LoadPlanningObservationSetAsync(pairId, plan.ConfigurationRevision,
            roots["local"], roots["dropbox"], cuts.Local, cuts.Cloud);
        var registration = await db.ReserveActionAsync(plan.IdempotencyKey, admission);
        plan = plan with
        {
            OperationId = registration.OperationId,
            PlanningAuthorities = [set.Bind(SyncAuthorityReconciliationPlanner.ObservationSetIdentity(set), plan.ObjectId)]
        };
        await db.CreateOperationAsync(plan, admission, cloudHierarchyEvidence: evidence,
            reservation: registration.Reservation);
    }

    private static SyncControlNamespaceBinding Control(string root, string pairId) => new(Installation, pairId,
        new SyncLocalControlNamespaceBinding(Path.Combine(root, ControlFolderName(pairId)), 0x5000, 0x6001,
            IdentityToken(new SyncLocalEndpointIdentity(0x5000, 0x6001)), new string('a', 64)));

    // The private control folder names the product's own deterministic formula produces for one pair id.
    private static string ControlFolderName(string pairId) =>
        SyncControlNamespace.LocalPrefix + SyncControlNamespace.NormalizeNamespaceId(pairId);

    private static string RightControlFolderName(string pairId) =>
        SyncControlNamespace.LocalPrefix +
        SyncControlNamespace.NormalizeNamespaceId(SyncEndpointPairRuntime.RightControlNamespaceId(pairId));

    private static SyncPair LocalPair(string id, string left, string right,
        IReadOnlyList<SyncSelectionRule> rules)
    {
        var leftEndpoint = LocalEndpoint(left);
        var rightEndpoint = LocalEndpoint(right);
        var endpoints = new SyncPairEndpoints(leftEndpoint.Endpoint, rightEndpoint.Endpoint, SyncMode.TwoWay);
        var document = SyncEndpointPersistence.Symmetric(endpoints);
        return new SyncPair(id, id, left, 0x4321, leftEndpoint.Endpoint.LocalIdentity!.RootFileId, right,
            IdentityToken(rightEndpoint.Endpoint.LocalIdentity!),
            SelectionRules: rules, SelectionDefaultIncluded: true, Provider: SyncProviders.Local,
            EndpointSchemaVersion: document.Version, LeftEndpoint: document.Left, RightEndpoint: document.Right);
    }

    private readonly record struct LocalSide(SyncEndpoint Endpoint)
    {
        internal SyncEndpointRecord Record() => SyncEndpointRecord.From(Endpoint);
    }

    private static LocalSide LocalEndpoint(string path) =>
        new(SyncEndpoint.Local(path, new SyncLocalEndpointIdentity(0x4321, FileIdFor(path))));

    // The retained runtime binding token for a local identity: "<volume x8>:<fileId x16>".
    private static string IdentityToken(SyncLocalEndpointIdentity identity) =>
        identity.VolumeSerialNumber.ToString("x8", System.Globalization.CultureInfo.InvariantCulture) + ":" +
        identity.RootFileId.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);

    // One stable non-zero identity per physical path, so a nested child root has its own identity.
    private static ulong FileIdFor(string path)
    {
        var hash = 1469598103934665603UL;
        foreach (var character in path.ToUpperInvariant())
        {
            hash ^= character;
            hash *= 1099511628211UL;
        }
        return hash == 0 ? 1UL : hash;
    }

    private static async Task RefuseAsync(Func<Task> action, string? expected = null)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException)
        {
            _assertions++;
            if (expected is not null && !error.Message.Contains(expected, StringComparison.Ordinal))
                throw new InvalidDataException("Refused for the wrong reason: " + error.Message);
            return;
        }
        throw new InvalidDataException("Expected a refusal but the production entry point accepted the input.");
    }

    private static void Require(bool value, string label)
    {
        _assertions++;
        if (!value) throw new InvalidDataException("Corresponding included overlap: " + label);
    }
}

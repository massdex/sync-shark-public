using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;
using IOPath = System.IO.Path;

// The nested-pair partition regression for the Parent overlap boundary. It drives the one shared
// SyncPairPartitionPolicy decision plus the real production admission consumers: the strict endpoint-only
// cross-pair contract (which must keep refusing) and the real public SyncAuthorityRuntimeHost pair binding
// over a real isolated SyncAuthorityDatabase. The decisive success counts successful effects from the real
// coordinated runner; the decisive rejections read what the production path actually retained.
// No provider, credential, installed state or user file is reached.
internal static class ExcludedNestedPairPartitionChecks
{
    private const string Installation = "installation-nested-partition-431";
    private const string SourceRevision = "nested-partition-431";
    private const string ProfileId = "profile-nested-partition-431";
    private const string CapabilityJson = "{\"name\":\"nested-partition-431\",\"local\":true}";
    private const string AccountId = "account-nested-partition-431";
    private const string RootName = "Parent";
    private const string ChildName = "Child";
    private const string CousinName = "ChildOther";

    private static readonly List<Dictionary<string, object?>> Cases = [];
    private static int _assertions;
    private static string _root = "";

    internal static async Task RunAsync(string root)
    {
        _root = IOPath.GetFullPath(root);
        if (Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any())
            throw new InvalidOperationException("Use a fresh isolated nested-partition fixture root.");
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Nested-partition checks require Windows NTFS identities.");
        Directory.CreateDirectory(_root);
        Cases.Clear();
        _assertions = 0;

        await CaseAsync("ENP-01-exclusion-admits-nested-partition-over-strict-endpoint-refusal",
            ExclusionAdmitsNestedPartitionAsync);
        await CaseAsync("ENP-02-prefix-cousin-remains-a-normal-parent-participant", CousinIsDistinctAsync);
        await CaseAsync("ENP-21-safe-name-component-accepts-ordinary-and-unicode-names",
            OpaqueSafeNameComponentsAcceptedAsync);
        await CaseAsync("ENP-22-safe-name-component-refuses-padded-traversal-path-and-control-names",
            OpaqueUnsafeNameComponentsRefusedAsync);
        await CaseAsync("ENP-23-opaque-provider-without-hierarchy-evidence-stays-refused",
            OpaqueWithoutEvidenceRefusedAsync);
        await CaseAsync("ENP-24-opaque-complete-chains-distinct-without-observed-names",
            OpaqueDistinctWithoutNamesAsync);
        await CaseAsync("ENP-25-provider-global-object-same-across-account-and-root-routes",
            OpaqueSameAcrossRoutesAsync);
        await CaseAsync("ENP-26-wrong-held-account-proof-refused", OpaqueWrongAccountRefusedAsync);
        await CaseAsync("ENP-27-incomplete-chain-refused", OpaqueIncompleteChainRefusedAsync);
        await CaseAsync("ENP-28-ambiguous-duplicate-proof-refused", OpaqueAmbiguousProofRefusedAsync);
        await CaseAsync("ENP-29-misbound-snapshot-with-intersecting-ancestor-key-refused",
            OpaqueMisboundIntersectingKeyRefusedAsync);
        await CaseAsync("ENP-30-opaque-nested-allowed-both-orientations",
            OpaqueNestedAllowedBothOrientationsAsync);
        await CaseAsync("ENP-31-opaque-nested-missing-observed-names-refused",
            OpaqueNestedMissingNamesRefusedAsync);
        await CaseAsync("ENP-32-opaque-nested-omitted-exclusion-refused",
            OpaqueNestedOmittedExclusionRefusedAsync);
        await CaseAsync("ENP-33-opaque-nested-included-exception-refused",
            OpaqueNestedIncludedExceptionRefusedAsync);
        await CaseAsync("ENP-34-opaque-nested-uncovered-private-control-refused",
            OpaqueNestedUncoveredControlRefusedAsync);
        await CaseAsync("ENP-35-known-local-refusal-is-not-hidden-by-missing-opaque-proof",
            KnownLocalRefusalWithoutOpaqueProofAsync);
        await CaseAsync("ENP-03-unexcluded-nested-overlap-refused", UnexcludedNestedRefusedAsync);
        await CaseAsync("ENP-04-included-exception-under-excluded-child-refused",
            IncludedExceptionRefusedAsync);
        await CaseAsync("ENP-05-equal-roots-refused", EqualRootsRefusedAsync);
        await CaseAsync("ENP-06-absent-local-identity-refused", AbsentIdentityRefusedAsync);
        await CaseAsync("ENP-07-reserved-child-control-ownership-refused", ReservedOwnershipRefusedAsync);
        await CaseAsync("ENP-54-candidate-control-requires-exact-enclosing-exclusion",
            CandidateControlExclusionAsync);
        await CaseAsync("ENP-55-verified-control-publication-resumes-across-builds",
            VerifiedControlPublicationResumeAsync);
        await CaseAsync("ENP-08-ancestor-directory-effect-fenced", AncestorDirectoryEffectFencedAsync);
        await CaseAsync("ENP-09-removal-of-exclusion-while-child-retained-refused",
            RemovalOfExclusionRefusedAsync);
        await CaseAsync("ENP-10-real-host-binds-nested-parent-and-child-with-real-effects",
            RealHostNestedSuccessAsync);
        await CaseAsync("ENP-11-real-host-refuses-unexcluded-nested-pair", RealHostUnexcludedRefusedAsync);
        await CaseAsync("ENP-12-real-directory-effect-admission-refuses-ancestor-of-child",
            RealDirectoryEffectFenceAsync);
        await CaseAsync("ENP-13-equal-display-paths-on-different-namespaces-are-not-one-object",
            EqualDisplayPathsDistinctAsync);
        await CaseAsync("ENP-14-real-authority-refuses-stale-broad-control-orientation",
            RealAuthorityRefusesStaleBroadControlAsync);
        await CaseAsync("ENP-15-real-store-refuses-saved-policy-change-that-breaks-the-partition",
            RealStoreRefusesPolicyChangeAsync);
        await CaseAsync("ENP-16-real-authority-refuses-a-swapped-side-stale-row",
            RealAuthorityRefusesSwappedSideRowAsync);
        await CaseAsync("ENP-17-pair-aware-runtime-resolution-admits-and-refuses",
            PairAwareRuntimeResolutionAsync);
        await CaseAsync("ENP-18-a-foreign-candidate-cannot-waive-the-overlap",
            ForeignCandidateCannotWaiveAsync);
        await CaseAsync("ENP-19-strict-runtime-entry-refuses-an-unresolved-retained-pair",
            UnresolvedRetainedPairRefusedAsync);
        await CaseAsync("ENP-20-real-authority-reopen-and-stale-policy-refusal",
            ReopenAndStalePolicyRefusalAsync);
        await CaseAsync("ENP-36-document-less-retained-parent-baseline-is-unverified",
            DocumentLessParentBaselineAsync);
        await CaseAsync("ENP-37-own-retained-authority-projects-the-namespace-to-nested-allowed",
            OwnAuthorityProjectsNestedAllowedAsync);
        await CaseAsync("ENP-38-absent-or-mismatched-own-retained-tuple-stays-fail-closed",
            RetainedTupleFailsClosedAsync);
        await CaseAsync("ENP-39-same-projection-with-the-exclusion-removed-refuses",
            ProjectedWithoutExclusionRefusedAsync);
        await CaseAsync("ENP-40-real-authoring-consumer-refuses-then-admits-the-same-nested-child",
            RealConsumerNestedAdmissionAsync);
        await CaseAsync("ENP-41-policy-only-edit-preserves-the-explicit-endpoint-document",
            PolicyOnlyEditPreservesDocumentAsync);
        await CaseAsync("ENP-42-preserved-parent-document-admits-the-nested-child-end-to-end",
            PreservedParentDocumentAdmitsNestedChildAsync);
        await CaseAsync("ENP-43-nested-child-registers-completely-against-a-documentless-parent",
            CompleteNestedChildRegistrationAsync);
        await CaseAsync("ENP-44-missing-or-mismatched-own-retained-tuple-refuses-registration",
            RegistrationTupleFailsClosedAsync);
        await CaseAsync("ENP-45-legacy-document-without-namespace-candidate-is-projected-and-admitted",
            LegacyDocumentWithoutNamespaceAdmitsNestedChildAsync);
        await CaseAsync("ENP-46-real-host-mixed-edit-removing-only-child-exclusion",
            RealHostExplicitMixedEditAsync);
        await CaseAsync("ENP-56-real-host-documentless-mixed-edit-and-readback",
            RealHostDocumentlessMixedEditAsync);
        await CaseAsync("ENP-47-real-host-retained-control-atomic-mixed-edit",
            RealHostRetainedControlAtomicMixedEditAsync);
        await CaseAsync("ENP-48-injected-cloud-stage-fault-after-preceding-writes",
            InjectedCloudStageFaultAsync);
        await CaseAsync("ENP-49-real-cli-mixed-edit-removing-only-child-exclusion",
            RealCliMixedEditRemovingOnlyChildExclusionAsync);
        await CaseAsync("ENP-50-real-host-interrupted-publication-recovery",
            RealHostInterruptedPublicationRecoveryAsync);
        await CaseAsync("ENP-51-real-host-prepared-control-publication",
            PreparedControlPublicationAsync);
        await CaseAsync("ENP-52-real-cli-mixed-edit-prepare-and-full-paged-plan",
            RealCliMixedEditPrepareAndPagedPlanAsync);
        var report = new Dictionary<string, object?>
        {
            ["generated_by"] = "ExcludedNestedPairPartitionChecks (tests/SyncShark.Core.Tests)",
            ["fixture_root"] = _root,
            ["assertions"] = _assertions,
            ["cases"] = Cases
        };
        await File.WriteAllTextAsync(IOPath.Combine(_root, "excluded-nested-pair-partition-checks.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS excluded nested pair partition: " + _assertions +
            " assertions; a strict nested relation is admitted only when the enclosing pair excludes the whole" +
            " child subtree and its private control ownership, the raw endpoint-only contract still refuses, and" +
            " the real public Host admits the nested parent while the parent never enters the child subtree.");
    }

    // ENP-01: the real shared policy admits the exact parent/excludes-child partition, while the unchanged
    // endpoint-only cross-pair contract (a raw caller with no policy proof) still refuses the same nesting.
    private static Task ExclusionAdmitsNestedPartitionAsync()
    {
        var (parent, child) = ParentChildPairs(ChildName, excludeControlOwnership: true, includeException: false);
        Require(SyncPairPartitionPolicy.Decide(parent, child) == SyncPairPartitionRelation.NestedAllowed,
            "ENP-01 the shared policy admits the nested partition when the parent excludes the whole child subtree");
        Require(SyncPairPartitionPolicy.ExcludesWholeSubtree(parent, ChildName),
            "ENP-01 the parent policy proves the whole Child subtree stays excluded");
        Require(!SyncPairPartitionPolicy.ExcludesWholeSubtree(parent, CousinName),
            "ENP-01 the sibling ChildOther is not covered by the Child exclusion");
        var parentEndpoints = SyncCloudPairConfiguration.SavedEndpoints(parent)
            ?? throw new InvalidDataException("ENP-01 the fixture parent endpoints did not resolve.");
        var childEndpoints = SyncCloudPairConfiguration.SavedEndpoints(child)
            ?? throw new InvalidDataException("ENP-01 the fixture child endpoints did not resolve.");
        var strict = SyncEndpointContracts.ValidatePairAgainstExisting(
            parentEndpoints.Left, parentEndpoints.Right, new[] { childEndpoints });
        Require(!strict.IsValid && strict.Errors.Contains(SyncEndpointError.OverlappingEndpointAcrossPairs),
            "ENP-01 the raw endpoint-only cross-pair contract still refuses the same nesting: " + strict.Describe());
        var policy = SyncEndpointContracts.ValidatePairAgainstExisting(parent, new[] { child });
        Require(policy.IsValid, "ENP-01 the policy-aware cross-pair contract accepts the proven partition: " + policy.Describe());
        return Task.CompletedTask;
    }

    // ENP-02: a prefix cousin (a sibling whose name begins with the excluded name) is a normal participant.
    private static Task CousinIsDistinctAsync()
    {
        var (parent, cousin) = ParentChildPairs(CousinName, excludeControlOwnership: false, includeException: false,
            excludedName: ChildName);
        // The parent root really contains ChildOther, so the cousin is a nested pair. The Child exclusion
        // must not swallow it: ChildOther stays ordinary included participant data, and a cousin pair is
        // still refused as an unexcluded nested overlap rather than silently adopted.
        Require(!SyncPairPartitionPolicy.ExcludesWholeSubtree(parent, CousinName) &&
                SyncSelectionPolicy.IsIncluded(CousinName, parent),
            "ENP-02 the sibling ChildOther stays ordinary included parent participant data, not swept into the Child exclusion");
        Require(SyncPairPartitionPolicy.Decide(parent, cousin) == SyncPairPartitionRelation.NestedRefused,
            "ENP-02 a prefix-cousin pair is refused as an unexcluded nested overlap, never silently excluded");
        return Task.CompletedTask;
    }

    // ENP-03: a nested relation the enclosing pair does not exclude is refused.
    private static Task UnexcludedNestedRefusedAsync()
    {
        var (parent, child) = ParentChildPairs(ChildName, excludeControlOwnership: true, includeException: false,
            excludeChildSubtree: false);
        Require(SyncPairPartitionPolicy.Decide(parent, child) == SyncPairPartitionRelation.NestedRefused,
            "ENP-03 an unexcluded nested overlap is refused");
        return Task.CompletedTask;
    }

    // ENP-04: an included exception strictly below the excluded child invalidates the whole-subtree proof.
    private static Task IncludedExceptionRefusedAsync()
    {
        var (parent, child) = ParentChildPairs(ChildName, excludeControlOwnership: true, includeException: true);
        Require(SyncPairPartitionPolicy.Decide(parent, child) == SyncPairPartitionRelation.NestedRefused,
            "ENP-04 an included exception under the excluded child is refused; the whole subtree is not excluded");
        return Task.CompletedTask;
    }

    // ENP-05: equal roots are never allowed by an exclusion.
    private static Task EqualRootsRefusedAsync()
    {
        var endpoints = LocalLocalEndpoints(ChildName);
        var left = LocalPair("pair-equal-left", endpoints.Left, endpoints.Right, []);
        var right = LocalPair("pair-equal-right", endpoints.Left, endpoints.Right, [new SyncSelectionRule(ChildName, false)]);
        Require(SyncPairPartitionPolicy.Decide(left, right) == SyncPairPartitionRelation.Same,
            "ENP-05 equal local/cloud roots are Same and refused regardless of exclusion");
        return Task.CompletedTask;
    }

    // ENP-06: a nested relation without a verified local identity cannot be proven and is refused.
    private static Task AbsentIdentityRefusedAsync()
    {
        var (parent, child) = ParentChildPairs(ChildName, excludeControlOwnership: true, includeException: false);
        var strippedChild = child with
        {
            ControlNamespace = null,
            RightControlNamespace = null,
            LeftEndpoint = child.LeftEndpoint is null ? null : StripLocalIdentity(child.LeftEndpoint),
            RightEndpoint = child.RightEndpoint is null ? null : StripLocalIdentity(child.RightEndpoint)
        };
        Require(SyncPairPartitionPolicy.Decide(parent, strippedChild) == SyncPairPartitionRelation.Unverified,
            "ENP-06 a nested relation whose local identity is absent is Unverified and refused, not assumed safe");
        return Task.CompletedTask;
    }

    // ENP-07: a nested pair's private control ownership inside the enclosing root is reserved ownership. The
    // partition is refused unless the enclosing pair excludes it too, so a parent scan never adopts another
    // pair's private staging as ordinary participant data.
    private static Task ReservedOwnershipRefusedAsync()
    {
        var (parent, child) = ParentChildPairs(ChildName, excludeControlOwnership: false, includeException: false);
        Require(child.ControlNamespace is not null, "ENP-07 the fixture child really carries private control ownership");
        Require(SyncPairPartitionPolicy.Decide(parent, child) == SyncPairPartitionRelation.NestedRefused,
            "ENP-07 reserved child control ownership inside the enclosing root is refused when the parent does not exclude it");
        return Task.CompletedTask;
    }

    private static Task CandidateControlExclusionAsync()
    {
        foreach (var excluded in new[] { true, false })
        {
            var (parent, child) = ParentChildPairs(ChildName, true, false, excludeChildSubtree: excluded);
            // Included ownership checks also protect the deterministic, not-yet-published namespaces.
            // This pure fixture's held names predate that formula; represent both exclusions explicitly.
            parent = parent with { SelectionRules = (parent.SelectionRules ?? []).Concat(new[]
            {
                new SyncSelectionRule(SyncControlNamespace.LocalPrefix + SyncControlNamespace.NormalizeNamespaceId(child.Id), false),
                new SyncSelectionRule(SyncControlNamespace.LocalPrefix +
                    SyncControlNamespace.NormalizeNamespaceId(SyncEndpointPairRuntime.RightControlNamespaceId(child.Id)), false)
            }).ToArray() };
            var owned = child.ControlNamespace!.Local.RootPath;
            bool Allows(SyncPair candidate, SyncPair retained, string existing, string path, string side) =>
                excluded
                    ? SyncPairPartitionPolicy.AllowsNestedOverlapPath(candidate, retained, existing, path, retained.Id, side)
                    : SyncPairPartitionPolicy.AllowsCorrespondingIncludedPath(candidate, retained, existing, path, retained.Id, side);
            Require(Allows(child, parent, parent.LocalRoot, owned, "local"),
                "ENP-54 exact owned candidate control under excluded enclosing data subtree is admitted");
            Require(!Allows(child with { ControlNamespace = null }, parent, parent.LocalRoot, owned, "local"),
                "ENP-54 unrepresented candidate control is refused");
            Require(!Allows(child, parent, parent.LocalRoot, owned + "-unrelated", "local"),
                "ENP-54 sibling control-looking path is refused");
            Require(!Allows(child, parent with { SelectionRules = excluded ? [new(ChildName, false)] : [] },
                parent.LocalRoot, owned, "local"), "ENP-54 missing exact control exclusion is refused");
            var privateParent = parent with { ControlNamespace = child.ControlNamespace };
            Require(!Allows(child, privateParent, owned, owned, "local_control"),
                "ENP-54 competing private control is never waived");
            Require(!Allows(child, parent, parent.LocalRoot, owned, "recovery"),
                "ENP-54 unknown/recovery row is never waived");
        }
        return Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    private static async Task VerifiedControlPublicationResumeAsync()
    {
        using var fixture = await FixtureAsync("verified-control-resume");
        using var selected = PilotDirectoryLease.Open(Directory.CreateDirectory(
            IOPath.Combine(fixture.Root, "selected")).FullName);
        const string pairId = "pair-control-resume";
        const string rootId = "root-control-resume";
        await fixture.Database.RegisterPairAsync(pairId, 1);
        var identity = JsonSerializer.Serialize(new
        {
            volumeSerial = selected.Identity.VolumeSerialNumber.ToString("x8"),
            fileId = selected.Identity.FileId.ToString("x16")
        });
        await fixture.Database.RegisterRootBindingAsync(rootId, pairId, "local", 1, identity, selected.Path);
        var allocator = new SyncAuthorityLocalControlNamespaceAllocator(fixture.Database);
        var original = await allocator.BindAsync(selected, Installation, pairId, rootId, 1, ProfileId,
            deferActivePublication: true);
        Require(await ScalarAsync(fixture.DatabasePath,
            "SELECT status FROM operation WHERE pair_id='" + pairId + "'") == "committed",
            "ENP-55 real native creation is committed but not published");
        var receiptCount = await CountAsync(fixture.DatabasePath, "SELECT count(*) FROM effect_receipt");
        const string successor = "profile-control-resume-successor";
        await fixture.Database.InitializeAsync(Installation, successor, "control-resume-successor", CapabilityJson);
        foreach (var invalid in new[] { "revision", "root-binding", "installation" })
        {
            var refused = false;
            try
            {
                await allocator.BindAsync(selected, invalid == "installation" ? Installation + "-foreign" : Installation,
                    pairId, invalid == "root-binding" ? rootId + "-foreign" : rootId,
                    invalid == "revision" ? 2 : 1, successor);
            }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or IOException)
            { refused = true; }
            Require(refused, "ENP-55 changed " + invalid + " cannot borrow prior creation authority");
        }
        var resumed = await allocator.BindAsync(selected, Installation, pairId, rootId, 1, successor);
        Require(resumed == original && await ScalarAsync(fixture.DatabasePath,
                "SELECT status FROM operation WHERE pair_id='" + pairId + "'") == "completed",
            "ENP-55 successor publishes the original exact native binding");
        Require(await allocator.BindAsync(selected, Installation, pairId, rootId, 1, successor) == original,
            "ENP-55 completed cross-build reopen is idempotent");
        Require(await CountAsync(fixture.DatabasePath, "SELECT count(*) FROM effect_receipt") == receiptCount &&
                await ScalarAsync(fixture.DatabasePath,
                    "SELECT capability_profile_id FROM operation WHERE pair_id='" + pairId + "'") == ProfileId,
            "ENP-55 original authority/receipt remain unchanged; no second creation");
    }

    // ENP-08: the effect-admission fence refuses a directory move/delete at the excluded child subtree or any
    // ancestor of it, while ordinary sibling and deeper paths are unaffected.
    private static Task AncestorDirectoryEffectFencedAsync()
    {
        var (parent, child) = ParentChildPairs(ChildName, excludeControlOwnership: true, includeException: false);
        var known = new[] { parent, child };
        Require(SyncPairPartitionPolicy.RefusesAncestorDirectoryEffect(parent, known, ChildName, isDirectory: true),
            "ENP-08 a directory effect at the child subtree itself is refused");
        Require(!SyncPairPartitionPolicy.RefusesAncestorDirectoryEffect(parent, known, ChildName + "/Inner", isDirectory: true),
            "ENP-08 a directory effect deeper than the child is not an ancestor and is not fenced by this rule");
        Require(!SyncPairPartitionPolicy.RefusesAncestorDirectoryEffect(parent, known, CousinName, isDirectory: true),
            "ENP-08 a sibling directory is not fenced");
        Require(!SyncPairPartitionPolicy.RefusesAncestorDirectoryEffect(parent, known, ChildName, isDirectory: false),
            "ENP-08 a file effect is not the directory ancestor fence");
        return Task.CompletedTask;
    }

    // ENP-09: removing the exclusion while the child pair is retained reopens the refusal instead of silently
    // keeping the earlier nested admission.
    private static Task RemovalOfExclusionRefusedAsync()
    {
        var (parent, child) = ParentChildPairs(ChildName, excludeControlOwnership: true, includeException: false);
        Require(SyncPairPartitionPolicy.Decide(parent, child) == SyncPairPartitionRelation.NestedAllowed,
            "ENP-09 the partition holds while the exclusion is retained");
        var edited = parent with
        {
            SelectionRules = [new SyncSelectionRule(ChildName + "-other", false)],
            PolicyRevision = parent.PolicyRevision + 1
        };
        Require(SyncPairPartitionPolicy.Decide(edited, child) == SyncPairPartitionRelation.NestedRefused,
            "ENP-09 removing the exclusion while the child is retained reopens the refusal");
        return Task.CompletedTask;
    }

    // ENP-10: the real public Host binds the nested parent and child over a real isolated authority database,
    // and the real coordinated runner performs the parent's effect outside the excluded child subtree while the
    // child independently reaches its own child-local target. Successful effects are counted from the run.
    private static async Task RealHostNestedSuccessAsync()
    {
        using var fixture = await FixtureAsync("real-host-success");
        var (childLeft, childRight, parentLeft, parentRight) = NestedDirectories(fixture.Root);
        await File.WriteAllTextAsync(IOPath.Combine(parentLeft, "Outside.txt"), "parent outside payload");
        await File.WriteAllTextAsync(IOPath.Combine(childLeft, "Inner.txt"), "child inner payload");

        var child = await BindAsync(fixture, childLeft, childRight, "Child pair", []);
        var childOwnership = OwnershipRelativeNames(child, parentLeft, parentRight);
        Require(childOwnership.Count > 0,
            "ENP-10 the real child binding retained private control ownership: " + string.Join(",", childOwnership));

        var rules = new List<SyncSelectionRule> { new(ChildName, false) };
        rules.AddRange(childOwnership.Select(name => new SyncSelectionRule(name, false)));
        var parent = await BindAsync(fixture, parentLeft, parentRight, "Parent pair", rules);
        Require(await CountAsync(fixture.DatabasePath, "SELECT count(*) FROM root_binding WHERE pair_id='" +
                parent.Id + "' AND state='active' AND side IN ('local','dropbox')") == 2,
            "ENP-10 the real Host retained both parent root bindings through the guarded admission");
        var retainedParent = (await fixture.Database.LoadAllPairDefinitionsAsync())
            .Single(item => item.Id == parent.Id);
        Require(!SyncSelectionPolicy.IsIncluded(ChildName, retainedParent),
            "ENP-10 the parent's retained policy excludes the child subtree");

        var parentRun = await RunAsync(fixture, parent);
        Require(parentRun.Result is not null && parentRun.Result.Uploaded >= 1,
            "ENP-10 the parent's real run counted a successful upload outside the child subtree: " +
            (parentRun.Result is null ? "no result" : parentRun.Result.Uploaded.ToString(CultureInfo.InvariantCulture)));
        Require(File.Exists(IOPath.Combine(parentRight, "Outside.txt")),
            "ENP-10 the parent's effect reached its real target outside the child subtree");
        Require(!File.Exists(IOPath.Combine(parentRight, ChildName, "Inner.txt")),
            "ENP-10 the parent never entered the excluded child subtree");

        var childRun = await RunAsync(fixture, child);
        Require(childRun.Result is not null && childRun.Result.Uploaded + childRun.Result.Downloaded >= 1,
            "ENP-10 the child's real run counted a successful effect inside its own subtree: " +
            (childRun.Result is null ? "no result" : (childRun.Result.Uploaded + childRun.Result.Downloaded).ToString(CultureInfo.InvariantCulture)));
        Require(File.Exists(IOPath.Combine(childRight, "Inner.txt")),
            "ENP-10 the child independently reached its own child-local target");
    }

    // ENP-11: the real public Host still refuses the unexcluded nested pair and retains no root binding for it.
    private static async Task RealHostUnexcludedRefusedAsync()
    {
        using var fixture = await FixtureAsync("real-host-refusal");
        var (childLeft, childRight, parentLeft, parentRight) = NestedDirectories(fixture.Root);
        var child = await BindAsync(fixture, childLeft, childRight, "Child pair", []);
        var refused = "";
        try
        {
            await BindAsync(fixture, parentLeft, parentRight, "Parent pair", []);
        }
        catch (Exception error) when (error is SyncEndpointRuntimeRefusedException or InvalidOperationException)
        {
            refused = error.Message;
        }
        Require(refused.Length > 0,
            "ENP-11 the real Host refuses the unexcluded nested parent pair: " + refused);
        Require(await CountAsync(fixture.DatabasePath,
                "SELECT count(*) FROM root_binding WHERE pair_id=(SELECT pair_id FROM sync_pair WHERE pair_json LIKE '%Parent pair%') AND state='active'") == 0,
            "ENP-11 the refused parent retained no active root binding");
        Require(await CountAsync(fixture.DatabasePath, "SELECT count(*) FROM root_binding WHERE pair_id='" +
                child.Id + "' AND state='active'") > 0,
            "ENP-11 the refused parent left the retained child untouched");
    }

    // ---------------------------------------------------------------------------------------------
    // pure policy fixtures
    // ---------------------------------------------------------------------------------------------

    // ENP-15: the real SyncPairStore path refuses a SAVED policy edit that would break the retained nested
    // partition (removing the child exclusion, or adding an included exception below it), and leaves the
    // persisted parent policy and revision unchanged.
    private static async Task RealStoreRefusesPolicyChangeAsync()
    {
        await RealStoreRefusesPolicyChangeAsync(corresponding: true);
        await RealStoreRefusesPolicyChangeAsync(corresponding: false);
    }

    private static async Task RealStoreRefusesPolicyChangeAsync(bool corresponding)
    {
        using var fixture = await FixtureAsync("real-store-policy-change-" + corresponding);
        var (childLeft, childRight, parentLeft, parentRight) = NestedDirectories(fixture.Root);
        var rightChildName = corresponding ? ChildName : ChildName + "Mirror";
        if (!corresponding)
            childRight = Directory.CreateDirectory(IOPath.Combine(parentRight, rightChildName)).FullName;
        var child = await BindAsync(fixture, childLeft, childRight, "Child pair", []);
        var ownership = OwnershipRelativeNames(child, parentLeft, parentRight);
        var rules = new List<SyncSelectionRule> { new(ChildName, false) };
        if (!corresponding) rules.Add(new SyncSelectionRule(rightChildName, false));
        rules.AddRange(ownership.Select(name => new SyncSelectionRule(name, false)));
        var parent = await BindAsync(fixture, parentLeft, parentRight, "Parent pair", rules);
        var store = new SyncPairStore(fixture.Instance.StateAuthority);
        var set = await fixture.Database.LoadPairSetAsync();
        var retainedParent = set.Pairs.Single(item => item.Id == parent.Id);
        var retainedChild = set.Pairs.Single(item => item.Id == child.Id);
        var beforeJson = await ScalarAsync(fixture.DatabasePath,
            "SELECT pair_json FROM sync_pair WHERE pair_id='" + parent.Id + "'");
        var beforeRevision = await ScalarAsync(fixture.DatabasePath,
            "SELECT configuration_revision FROM sync_pair WHERE pair_id='" + parent.Id + "'");

        var includedWithOwnership = retainedParent with
        {
            SelectionRules = ownership.Select(name => new SyncSelectionRule(name, false)).ToArray()
        };
        Require((SyncPairPartitionPolicy.DecideCorrespondingIncluded(includedWithOwnership, retainedChild) ==
                SyncPairPartitionRelation.CorrespondingIncluded) == corresponding,
            "ENP-15 removing only the data exclusion preserves the supported corresponding overlap");
        var noExclusion = retainedParent with
        {
            // Included corresponding overlaps are supported only with private control
            // ownership still excluded. Removing all exclusions must remain unsafe.
            SelectionRules = corresponding ? [] : includedWithOwnership.SelectionRules,
            PolicyRevision = retainedParent.PolicyRevision + 1
        };
        Require(SyncPairPartitionPolicy.Decide(retainedParent, retainedChild) == SyncPairPartitionRelation.NestedAllowed &&
                SyncPairPartitionPolicy.Decide(noExclusion, retainedChild) == SyncPairPartitionRelation.NestedRefused &&
                SyncPairPartitionPolicy.DecideCorrespondingIncluded(noExclusion, retainedChild) != SyncPairPartitionRelation.CorrespondingIncluded,
            "ENP-15 removing the required exclusions makes this nested partition unsafe");
        var refusedRemoval = await TrySaveAsync(store,
            new SyncPairSet([noExclusion, retainedChild], noExclusion.Id));
        Require(refusedRemoval.Length > 0,
            "ENP-15 the real Store refuses the unsafe saved policy edit (corresponding=" + corresponding + "): " +
            (refusedRemoval.Length == 0 ? "not refused" : refusedRemoval));

        var exceptionRules = new List<SyncSelectionRule> { new(ChildName, false), new(ChildName + "/Included", true) };
        if (!corresponding) exceptionRules.Add(new SyncSelectionRule(rightChildName, false));
        exceptionRules.AddRange(ownership.Select(name => new SyncSelectionRule(name, false)));
        var withException = retainedParent with
        {
            SelectionRules = exceptionRules.ToArray(),
            PolicyRevision = retainedParent.PolicyRevision + 1
        };
        var refusedException = await TrySaveAsync(store,
            new SyncPairSet([withException, retainedChild], withException.Id));
        Require(refusedException.Length > 0,
            "ENP-15 the real Store refuses an included exception below the excluded child: " +
            (refusedException.Length == 0 ? "not refused" : refusedException));

        Require(await ScalarAsync(fixture.DatabasePath,
                "SELECT pair_json FROM sync_pair WHERE pair_id='" + parent.Id + "'") == beforeJson &&
            await ScalarAsync(fixture.DatabasePath,
                "SELECT configuration_revision FROM sync_pair WHERE pair_id='" + parent.Id + "'") == beforeRevision,
            "ENP-15 the refused saved policy edits left the persisted parent policy and revision unchanged");
    }

    private static async Task<string> TrySaveAsync(SyncPairStore store, SyncPairSet set)
    {
        try
        {
            await store.SaveAsync(set);
            return string.Empty;
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException)
        {
            return error.Message;
        }
    }

    // ENP-20 (r22-6 state half): the real authority reopens the saved nested partition and accepts it, and a
    // changed/stale exclusion policy (the enclosing pair no longer excludes the child) is refused at the
    // authority load boundary before any effect or retry can be admitted, leaving the child rows unchanged.
    private static async Task ReopenAndStalePolicyRefusalAsync()
    {
        using var fixture = await FixtureAsync("reopen-stale-policy");
        var (childLeft, childRight, parentLeft, parentRight) = NestedDirectories(fixture.Root);
        var child = await BindAsync(fixture, childLeft, childRight, "Child pair", []);
        var ownership = OwnershipRelativeNames(child, parentLeft, parentRight);
        var rules = new List<SyncSelectionRule> { new(ChildName, false) };
        rules.AddRange(ownership.Select(name => new SyncSelectionRule(name, false)));
        var parent = await BindAsync(fixture, parentLeft, parentRight, "Parent pair", rules);

        // A fresh authority instance over the same database accepts the saved nested partition.
        var reopened = new SyncAuthorityDatabase(fixture.DatabasePath);
        var reopenedSet = await reopened.LoadPairSetAsync();
        Require(reopenedSet.Pairs.Count == 2 &&
                reopenedSet.Pairs.Any(item => item.Id == parent.Id) &&
                reopenedSet.Pairs.Any(item => item.Id == child.Id),
            "ENP-20 the saved nested partition is accepted when the real authority reopens");

        var childJsonBefore = await ScalarAsync(fixture.DatabasePath,
            "SELECT pair_json FROM sync_pair WHERE pair_id='" + child.Id + "'");
        Require(childJsonBefore.Length > 0, "ENP-20 the retained child definition is present before the policy change");

        // Simulate a changed policy that no longer excludes the child subtree, directly in pair_json.
        var parentJson = await ScalarAsync(fixture.DatabasePath,
            "SELECT pair_json FROM sync_pair WHERE pair_id='" + parent.Id + "'");
        var node = System.Text.Json.Nodes.JsonNode.Parse(parentJson)!.AsObject();
        foreach (var rule in node["SelectionRules"]!.AsArray())
            if (string.Equals(rule!["FolderPath"]!.GetValue<string>(), ChildName, StringComparison.Ordinal))
                rule["Included"] = true;
        await ExecuteAsync(fixture.DatabasePath, "UPDATE sync_pair SET pair_json='" +
            node.ToJsonString().Replace("'", "''") + "' WHERE pair_id='" + parent.Id + "' AND state<>'retired';");

        // OV637-1: that change is now the corresponding INCLUDED overlap - the child subtree becomes ordinary
        // data while the child keeps its own private control ownership excluded - so the real authority
        // accepts it, as the included relation rather than the excluded partition, without touching any row.
        var includedPairs = (await new SyncAuthorityDatabase(fixture.DatabasePath).LoadPairSetAsync()).Pairs;
        var includedParent = includedPairs.Single(item => item.Id == parent.Id);
        var includedChild = includedPairs.Single(item => item.Id == child.Id);
        Require(SyncPairPartitionPolicy.Decide(includedParent, includedChild) ==
                SyncPairPartitionRelation.NestedRefused &&
                SyncPairPartitionPolicy.DecideCorrespondingIncluded(includedParent, includedChild) ==
                SyncPairPartitionRelation.CorrespondingIncluded,
            "ENP-20 the changed policy is the corresponding included overlap, not the excluded partition");
        Require(await ScalarAsync(fixture.DatabasePath,
                "SELECT pair_json FROM sync_pair WHERE pair_id='" + child.Id + "'") == childJsonBefore,
            "ENP-20 the accepted included-policy load left the retained child rows unchanged");

        // A policy change that ALSO stops excluding the child's private control ownership no longer proves
        // the corresponding relation and is refused at the real authority load boundary with no row change.
        foreach (var rule in node["SelectionRules"]!.AsArray()) rule!["Included"] = true;
        await ExecuteAsync(fixture.DatabasePath, "UPDATE sync_pair SET pair_json='" +
            node.ToJsonString().Replace("'", "''") + "' WHERE pair_id='" + parent.Id + "' AND state<>'retired';");
        var staleRefusal = string.Empty;
        try
        {
            var stale = new SyncAuthorityDatabase(fixture.DatabasePath);
            await stale.LoadPairSetAsync();
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
        {
            staleRefusal = error.Message;
        }
        Require(staleRefusal.Length > 0,
            "ENP-20 the real authority refuses a changed/stale policy that drops the nested private-control " +
            "exclusion before any effect or retry: " + (staleRefusal.Length == 0 ? "accepted" : staleRefusal));
        Require(await ScalarAsync(fixture.DatabasePath,
                "SELECT pair_json FROM sync_pair WHERE pair_id='" + child.Id + "'") == childJsonBefore,
            "ENP-20 the refused stale-policy load left the retained child rows unchanged");
        Require(await CountAsync(fixture.DatabasePath, "SELECT count(*) FROM root_binding WHERE pair_id='" +
                parent.Id + "' AND state='active' AND side IN ('local','dropbox')") == 2,
            "ENP-20 the refused stale-policy load left the retained parent root bindings unchanged");
    }

    // ENP-19: the strict full-pair runtime entry point refuses an unresolved/malformed retained pair instead of
    // treating its relation as accepted; an Unverified relation never waives the established strict check.
    private static async Task UnresolvedRetainedPairRefusedAsync()
    {
        using var fixture = await FixtureAsync("unresolved-retained");
        var (childLeft, childRight, parentLeft, parentRight) = NestedDirectories(fixture.Root);
        var child = await BindAsync(fixture, childLeft, childRight, "Child pair", []);
        var ownership = OwnershipRelativeNames(child, parentLeft, parentRight);
        var rules = new List<SyncSelectionRule> { new(ChildName, false) };
        rules.AddRange(ownership.Select(name => new SyncSelectionRule(name, false)));
        var parent = await BindAsync(fixture, parentLeft, parentRight, "Parent pair", rules);
        var definitions = await fixture.Database.LoadAllPairDefinitionsAsync();
        var retainedParent = definitions.Single(item => item.Id == parent.Id);
        var retainedChild = definitions.Single(item => item.Id == child.Id);
        Require(SyncEndpointPairRuntime.ResolveAgainstRetainedPairs(retainedParent, retainedParent.EndpointDocument,
                new[] { retainedChild }).IsResolved,
            "ENP-19 the healthy retained pair resolves at the strict runtime entry point");
        var malformed = retainedChild with { EndpointSchemaVersion = 999 };
        var refused = SyncEndpointPairRuntime.ResolveAgainstRetainedPairs(retainedParent,
            retainedParent.EndpointDocument, new[] { malformed });
        Require(!refused.IsResolved,
            "ENP-19 an unresolved/malformed retained pair is refused at the strict runtime entry point: " +
            (refused.IsResolved ? "accepted" : refused.Validation.Describe()));
    }

    // ENP-18: the candidate definition is bound to the ACTUAL path being registered. A candidate pair for a
    // different root cannot waive this registration's overlap because it does not represent the registered
    // path on any of its own sides.
    private static async Task ForeignCandidateCannotWaiveAsync()
    {
        using var fixture = await FixtureAsync("foreign-candidate");
        var (childLeft, childRight, parentLeft, parentRight) = NestedDirectories(fixture.Root);
        var child = await BindAsync(fixture, childLeft, childRight, "Child pair", []);
        var ownership = OwnershipRelativeNames(child, parentLeft, parentRight);
        var rules = new List<SyncSelectionRule> { new(ChildName, false) };
        rules.AddRange(ownership.Select(name => new SyncSelectionRule(name, false)));
        var parent = await BindAsync(fixture, parentLeft, parentRight, "Parent pair", rules);
        var definitions = await fixture.Database.LoadAllPairDefinitionsAsync();
        var retainedParent = definitions.Single(item => item.Id == parent.Id);
        var retainedChild = definitions.Single(item => item.Id == child.Id);
        var childRoot = parentLeft + IOPath.DirectorySeparatorChar + ChildName;
        // The candidate that really owns the registered path is admitted.
        Require(SyncPairPartitionPolicy.AllowsNestedOverlapPath(retainedParent, retainedChild, childRoot, parentLeft,
                retainedChild.Id, "local"),
            "ENP-18 the candidate that owns the registered path is admitted");
        // A foreign candidate for a different root cannot waive the same overlap.
        Require(!SyncPairPartitionPolicy.AllowsNestedOverlapPath(retainedChild, retainedParent, childRoot, parentLeft,
                retainedParent.Id, "local"),
            "ENP-18 a foreign candidate that does not represent the registered path cannot waive the overlap");
    }

    // ENP-17: the full-pair runtime resolution (the production SyncEndpointPairRuntime API the host Prepare
    // path uses) admits the retained excluded nested parent, refuses the same pair without the exclusion, an
    // included exception, and unknown/equal relations, while the strict raw endpoint-only resolution still
    // refuses the nesting.
    private static async Task PairAwareRuntimeResolutionAsync()
    {
        using var fixture = await FixtureAsync("pair-aware-resolution");
        var (childLeft, childRight, parentLeft, parentRight) = NestedDirectories(fixture.Root);
        var child = await BindAsync(fixture, childLeft, childRight, "Child pair", []);
        var ownership = OwnershipRelativeNames(child, parentLeft, parentRight);
        var rules = new List<SyncSelectionRule> { new(ChildName, false) };
        rules.AddRange(ownership.Select(name => new SyncSelectionRule(name, false)));
        var parent = await BindAsync(fixture, parentLeft, parentRight, "Parent pair", rules);
        var definitions = await fixture.Database.LoadAllPairDefinitionsAsync();
        var retainedParent = definitions.Single(item => item.Id == parent.Id);
        var others = definitions.Where(item => item.Id != retainedParent.Id).ToArray();

        var resolved = SyncEndpointPairRuntime.ResolveAgainstRetainedPairs(retainedParent,
            retainedParent.EndpointDocument, others);
        Require(resolved.IsResolved,
            "ENP-17 the full-pair runtime resolution admits the retained excluded nested parent: " +
            resolved.Validation.Describe());

        var noExclusion = retainedParent with
        { SelectionRules = ownership.Select(name => new SyncSelectionRule(name, false)).ToArray() };
        // OV637-1: removing the child-subtree exclusion no longer refuses by itself. The same nesting is now
        // the corresponding INCLUDED overlap - the enclosing pair includes the whole relative subtree on both
        // endpoints and the child keeps its own private control ownership excluded - so the policy-aware
        // resolution admits it, while the strict raw endpoint-only resolution below still refuses it.
        var included = SyncEndpointPairRuntime.ResolveAgainstRetainedPairs(noExclusion,
            noExclusion.EndpointDocument, others);
        Require(others.Length == 1 && SyncPairPartitionPolicy.Decide(noExclusion, others[0]) ==
                SyncPairPartitionRelation.NestedRefused &&
                SyncPairPartitionPolicy.DecideCorrespondingIncluded(noExclusion, others[0]) ==
                SyncPairPartitionRelation.CorrespondingIncluded,
            "ENP-17 without the exclusion the relation is the corresponding included overlap, not the excluded partition");
        Require(included.IsResolved,
            "ENP-17 the full-pair runtime resolution admits the corresponding included overlap without the " +
            "child-subtree exclusion: " + included.Validation.Describe());

        var exceptionRules = new List<SyncSelectionRule> { new(ChildName, false), new(ChildName + "/Included", true) };
        exceptionRules.AddRange(ownership.Select(name => new SyncSelectionRule(name, false)));
        var withException = retainedParent with { SelectionRules = exceptionRules.ToArray() };
        var refusedException = SyncEndpointPairRuntime.ResolveAgainstRetainedPairs(withException,
            withException.EndpointDocument, others);
        Require(!refusedException.IsResolved,
            "ENP-17 the full-pair runtime resolution refuses an included exception below the excluded child: " +
            refusedException.Validation.Describe());

        var rawStrict = SyncEndpointPairRuntime.Resolve(retainedParent, retainedParent.EndpointDocument,
            others.Select(SyncCloudPairConfiguration.SavedEndpoints).Where(item => item is not null)
                .Select(item => item!).ToArray());
        Require(!rawStrict.IsResolved,
            "ENP-17 the strict raw endpoint-only resolution still refuses the same nesting");
    }

    // ENP-16: the real authority binds a row to the EXACT side it represents. A row whose own side no longer
    // represents that endpoint (a swapped-side stale row) cannot waive the overlap even though its path is
    // still one of the pair's real roots.
    private static async Task RealAuthorityRefusesSwappedSideRowAsync()
    {
        using var fixture = await FixtureAsync("real-host-swapped-side");
        var (childLeft, childRight, parentLeft, parentRight) = NestedDirectories(fixture.Root);
        var secondName = "ChildTwo";
        var secondLeft = Directory.CreateDirectory(IOPath.Combine(parentLeft, secondName)).FullName;
        var secondRight = Directory.CreateDirectory(IOPath.Combine(parentRight, secondName)).FullName;
        var child = await BindAsync(fixture, childLeft, childRight, "Child pair", []);
        var ownership = OwnershipRelativeNames(child, parentLeft, parentRight);
        var rules = new List<SyncSelectionRule> { new(ChildName, false), new(secondName, false) };
        rules.AddRange(ownership.Select(name => new SyncSelectionRule(name, false)));
        var parent = await BindAsync(fixture, parentLeft, parentRight, "Parent pair", rules);
        // Swap the parent's LOCAL row to its RIGHT (remote) root: a stale row that no longer represents the
        // side it is retained under, but whose path is still one of the pair's real roots.
        await ExecuteAsync(fixture.DatabasePath,
            "UPDATE root_binding SET display_path='" + parentRight.Replace("'", "''") +
            "' WHERE pair_id='" + parent.Id + "' AND side='local' AND state='active';");
        var refused = string.Empty;
        try
        {
            await BindAsync(fixture, secondLeft, secondRight, secondName + " child pair",
                [new SyncSelectionRule(secondName, false)]);
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException)
        {
            refused = error.Message;
        }
        Require(refused.Contains("overlaps another active", StringComparison.Ordinal),
            "ENP-16 the real authority refuses a swapped-side stale row instead of waiving the overlap: " +
            (refused.Length == 0 ? "not refused" : refused));
    }

    // ENP-12: the real guarded database admission refuses a recursive directory effect whose path is the
    // excluded child subtree or an ancestor of it, and does not fence an ordinary sibling directory.
    private static async Task RealDirectoryEffectFenceAsync()
    {
        using var fixture = await FixtureAsync("real-host-directory-fence");
        var (childLeft, childRight, parentLeft, parentRight) = NestedDirectories(fixture.Root);
        var child = await BindAsync(fixture, childLeft, childRight, "Child pair", []);
        var ownership = OwnershipRelativeNames(child, parentLeft, parentRight);
        var rules = new List<SyncSelectionRule> { new(ChildName, false) };
        rules.AddRange(ownership.Select(name => new SyncSelectionRule(name, false)));
        var parent = await BindAsync(fixture, parentLeft, parentRight, "Parent pair", rules);
        var admission = await fixture.Database.CreateAcceptedCommandAdmissionAsync(null, "run_pair", parent.Id,
            null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await fixture.Database.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
        // A recursive directory delete at the excluded child subtree is the child's own root: refused.
        var fenced = await DirectoryDeletePlanRefusalAsync(fixture, parent, ChildName, admission);
        Require(fenced is not null && fenced.Contains("excluded nested child subtree", StringComparison.Ordinal),
            "ENP-12 a real recursive directory delete at the excluded child subtree is refused at admission: " +
            (fenced ?? "no refusal"));
        // An ancestor query: the parent root itself is above the child, but the relative path is empty, so
        // only the child path (and any deeper ancestor relationship) can be the fenced case here.
        var sibling = await DirectoryDeletePlanRefusalAsync(fixture, parent, "Outside", admission);
        Require(sibling is null || !sibling.Contains("excluded nested child subtree", StringComparison.Ordinal),
            "ENP-12 an ordinary sibling directory effect is not fenced by the nested-partition rule: " +
            (sibling ?? "no refusal"));
    }

    private static async Task<string?> DirectoryDeletePlanRefusalAsync(Fixture fixture, SyncPair pair,
        string relativePath, SyncAuthorityCommandAdmission admission)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var operationId = "operation-fence-" + suffix;
        var objectId = "object-fence-" + suffix;
        var memberId = "member-fence-" + suffix;
        var rootBindingId = await ScalarAsync(fixture.DatabasePath,
            "SELECT root_binding_id FROM root_binding WHERE pair_id='" + pair.Id +
            "' AND side='local' AND state='active'");
        var preconditions = "{\"entryKind\":\"Directory\",\"action\":\"delete\"}";
        var state = "{\"ObjectKind\":\"directory\",\"present\":true}";
        var plan = new SyncAuthorityOperationPlan(operationId, "idem-" + operationId,
            SyncAuthorityOperationKind.DeleteRemote, pair.Id, objectId, pair.PolicyRevision, ProfileId,
            preconditions, 1000,
            [
                new SyncAuthorityMember(memberId, pair.Id, objectId, "source", rootBindingId, null, null, null,
                    PilotPath.NormalizeRelative(relativePath), state)
            ], [], []);
        try
        {
            await fixture.Database.CreateOperationAsync(plan, admission: admission);
            return null;
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or SqliteException)
        {
            return error.Message;
        }
    }

    // ENP-13: two verified namespaces may legitimately carry an equal presentation path; that is not one
    // provider object, so the partition decision never reports Same from display text alone.
    private static Task EqualDisplayPathsDistinctAsync()
    {
        var localA = LocalEndpoint(IOPath.Combine(_root, "pure", "nsA", RootName + "A"), 0x3001);
        var localB = LocalEndpoint(IOPath.Combine(_root, "pure", "nsB", RootName + "B"), 0x3002);
        var first = LocalCloudPair("pair-ns-a", localA,
            SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, "connection-a", "namespace-a", null,
                "/Parent/Child"), []);
        var second = LocalCloudPair("pair-ns-b", localB,
            SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, "connection-b", "namespace-b", null,
                "/Parent/Child"), []);
        var relation = SyncPairPartitionPolicy.Decide(first, second);
        Require(relation != SyncPairPartitionRelation.Same,
            "ENP-13 equal display paths on two verified namespaces are never one object (Same): " + relation);
        Require(!SyncEndpoints.ReferencesSameStore(
                SyncCloudPairConfiguration.SavedEndpoints(first)!.Right,
                SyncCloudPairConfiguration.SavedEndpoints(second)!.Right),
            "ENP-13 the two same-path cloud endpoints do not reference one store");
        return Task.CompletedTask;
    }

    private static SyncPair LocalCloudPair(string id, SyncEndpoint local, SyncEndpoint cloud,
        IReadOnlyList<SyncSelectionRule> rules)
    {
        var endpoints = new SyncPairEndpoints(local, cloud, SyncMode.TwoWay);
        var document = SyncEndpointPersistence.Symmetric(endpoints);
        return new SyncPair(id, id, local.LocalPath!, local.LocalIdentity!.VolumeSerialNumber,
            local.LocalIdentity.RootFileId, cloud.FolderPath ?? string.Empty, cloud.FolderId ?? string.Empty,
            SelectionRules: rules, SelectionDefaultIncluded: true, ConnectionId: cloud.ConnectionId!,
            Provider: SyncProviders.Dropbox,
            EndpointSchemaVersion: document.Version, LeftEndpoint: document.Left, RightEndpoint: document.Right);
    }

    // ENP-14: the real guarded authority refuses a candidate root that would sit inside a stale or
    // unrepresented private-control row, even though the enclosing pair really excludes that candidate root.
    // Only the exact represented enclosing data root may be waived by candidate-path coverage.
    private static async Task RealAuthorityRefusesStaleBroadControlAsync()
    {
        using var fixture = await FixtureAsync("real-host-stale-control");
        var (childLeft, childRight, parentLeft, parentRight) = NestedDirectories(fixture.Root);
        var secondName = "ChildTwo";
        var secondLeft = Directory.CreateDirectory(IOPath.Combine(parentLeft, secondName)).FullName;
        var secondRight = Directory.CreateDirectory(IOPath.Combine(parentRight, secondName)).FullName;
        var child = await BindAsync(fixture, childLeft, childRight, "Child pair", []);
        var ownership = OwnershipRelativeNames(child, parentLeft, parentRight);
        var rules = new List<SyncSelectionRule> { new(ChildName, false), new(secondName, false) };
        rules.AddRange(ownership.Select(name => new SyncSelectionRule(name, false)));
        var parent = await BindAsync(fixture, parentLeft, parentRight, "Parent pair", rules);
        var retainedParent = (await fixture.Database.LoadAllPairDefinitionsAsync())
            .Single(item => item.Id == parent.Id);
        var secondDraft = DraftFor(secondLeft, secondRight, secondName + " child pair",
            [new SyncSelectionRule(secondName, false)]);
        Require(SyncPairPartitionPolicy.Decide(secondDraft, retainedParent) == SyncPairPartitionRelation.NestedAllowed,
            "ENP-14 the enclosing parent really excludes the second nested candidate root (so the rejection is not an unexcluded-nesting refusal)");
        // Make the parent's own retained private-control row stale/broad: its path becomes the grandparent
        // (containing the parent root and the candidate root) and no longer one of the parent's represented
        // owned control subtrees. This is exactly a stale/unrepresented broad-control row.
        var grandParent = IOPath.GetDirectoryName(parentLeft)!;
        await ExecuteAsync(fixture.DatabasePath,
            "UPDATE root_binding SET display_path='" + grandParent.Replace("'", "''") +
            "\\' WHERE pair_id='" + parent.Id + "' AND side='local_control' AND state='active';");
        Require(await CountAsync(fixture.DatabasePath,
                "SELECT count(*) FROM root_binding WHERE pair_id='" + parent.Id +
                "' AND side='local_control' AND state='active'") == 1,
            "ENP-14 the stale broad private-control row is really retained and active");
        var refused = "";
        try
        {
            await BindAsync(fixture, secondLeft, secondRight, secondName + " child pair",
                [new SyncSelectionRule(secondName, false)]);
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException)
        {
            refused = error.Message;
        }
        Require(refused.Contains("overlaps another active pair", StringComparison.Ordinal),
            "ENP-14 the real authority entry refuses the candidate root inside the stale broad control row: " + refused);
        Require(await CountAsync(fixture.DatabasePath,
                "SELECT count(*) FROM root_binding WHERE pair_id=(SELECT pair_id FROM sync_pair WHERE pair_json LIKE '%" +
                secondName + " child pair%') AND state='active'") == 0,
            "ENP-14 the refused second child retained no active root binding");
    }

    private static async Task ExecuteAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
        SqliteConnection.ClearAllPools();
    }


    // ---------------------------------------------------------------------------------------------
    // CN636: the pair's OWN retained-authority endpoint projection (A) and the explicit
    // endpoint document across a policy-only legacy edit (B).
    // ---------------------------------------------------------------------------------------------

    private const string CloudNamespaceId = "ns-nested-431";
    private const string ParentCloudRootPath = "/Parent";
    private const string ChildCloudRootPath = "/Parent/Child";
    private const string OtherCloudRootPath = "/Documents/Other";
    private const string ParentCloudFolder = "id:parent-parent-431";
    private const string ChildCloudFolder = "id:parent-child-431";
    private const string OtherCloudFolder = "id:parent-other-431";
    private const string RecoveryContainerPath = "/Apps/Sync Shark/Recovery";
    private const string RecoveryRootFolderId = "id:recovery-root-431";

    private static readonly DropboxFolderEntry PolicyEditRecovery =
        new("Recovery", RecoveryContainerPath, RecoveryRootFolderId);

    private static string CloudAccountConnectionId => DropboxConnectionBinding.CreateConnectionId(AccountId);

    private static string RetainedParentRootBindingId(SyncPair parent) => "root-dropbox-" + parent.Id;

    private static SyncEndpoint CloudEndpointAt(string folderPath, string folderId, string? namespaceId) =>
        SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, CloudAccountConnectionId, namespaceId, folderId, folderPath);

    /// <summary>The document-less local+cloud pair exactly as the pre-document legacy shape persists it.</summary>
    private static SyncPair LegacyParentPair(string localRoot, IReadOnlyList<SyncSelectionRule> rules)
    {
        using var lease = PilotDirectoryLease.Open(localRoot);
        return new SyncPair("pair-parent-parent-431", "Parent pair", lease.Path,
            lease.Identity.VolumeSerialNumber, lease.Identity.FileId, ParentCloudRootPath, ParentCloudFolder,
            Enabled: false, SelectionRules: rules, SelectionDefaultIncluded: true,
            ConnectionId: CloudAccountConnectionId, Provider: SyncProviders.Dropbox,
            Direction: SyncMode.UploadOnly, PolicyRevision: 1);
    }

    private static SyncPair ChildCandidate(string localRoot, SyncMode direction) =>
        SyncEndpointPairDraft.Create(new SyncPairEndpoints(LocalEndpointFromLease(localRoot),
            CloudEndpointAt(ChildCloudRootPath, ChildCloudFolder, CloudNamespaceId), direction),
            "Child pair");

    private static SyncPair AuthoredDraft(string localRoot, string cloudPath, string cloudFolder, SyncMode direction) =>
        SyncEndpointPairDraft.Create(new SyncPairEndpoints(LocalEndpointFromLease(localRoot),
            CloudEndpointAt(cloudPath, cloudFolder, CloudNamespaceId), direction), IOPath.GetFileName(localRoot));

    private static SyncPair AuthoredReversedDraft(string localRoot, string cloudPath, string cloudFolder) =>
        SyncEndpointPairDraft.Create(new SyncPairEndpoints(
            CloudEndpointAt(cloudPath, cloudFolder, CloudNamespaceId), LocalEndpointFromLease(localRoot),
            SyncMode.TwoWay), IOPath.GetFileName(localRoot));

    /// <summary>Real directories plus the exact parent/child pair objects the shared decision consumes.</summary>
    private static (SyncPair Parent, SyncPair Child) PolicyInputPair(string name, bool excludeChild = true)
    {
        var root = Directory.CreateDirectory(IOPath.Combine(_root, "cn636-" + name)).FullName;
        var parentLocal = Directory.CreateDirectory(IOPath.Combine(root, RootName)).FullName;
        var childLocal = Directory.CreateDirectory(IOPath.Combine(parentLocal, ChildName)).FullName;
        return (LegacyParentPair(parentLocal, excludeChild ? [new SyncSelectionRule(ChildName, false)] : []),
            ChildCandidate(childLocal, SyncMode.DownloadOnly));
    }

    private static string ProviderAccountIdentity() =>
        SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.Dropbox, AccountId, CloudNamespaceId,
            CloudNamespaceId);

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private static NestedPartitionDropboxHandler NestedPartitionHandler() =>
        new NestedPartitionDropboxHandler()
            .Folder(ParentCloudFolder, ParentCloudRootPath)
            .Folder(ChildCloudFolder, ChildCloudRootPath)
            .Folder(OtherCloudFolder, OtherCloudRootPath)
            .Folder(RecoveryRootFolderId, RecoveryContainerPath);

    private sealed record CloudRig(string Root, string DatabasePath, SyncAuthorityDatabase Database,
        SyncAuthorityRuntimeHost Host, PilotInstanceLock Instance) : IDisposable
    {
        public void Dispose()
        {
            Host.Dispose();
            Instance.Dispose();
        }
    }

    /// <summary>
    /// A real isolated authority database plus the one shared runtime host, with an exact saved Dropbox
    /// connection. The provider double is supplied only where the shipped path performs its own provider
    /// read; a case that must never reach a provider keeps the guarded handler.
    /// </summary>
    private static async Task<CloudRig> CloudRigAsync(string name, HttpMessageHandler? handler = null,
        FakeSyncRemoteStore? remote = null)
    {
        var root = IOPath.Combine(_root, name);
        Directory.CreateDirectory(root);
        var state = Directory.CreateDirectory(IOPath.Combine(root, "state")).FullName;
        var instance = PilotInstanceLock.Acquire(IOPath.Combine(state, "instance.lock"));
        var databasePath = IOPath.Combine(root, "authority.db");
        var database = new SyncAuthorityDatabase(databasePath);
        await database.InitializeAsync(Installation, ProfileId, SourceRevision, CapabilityJson);
        var product = new SyncProductStateStore(instance.StateAuthority);
        await product.UpdateAsync(current => current with { InstallationId = Installation });
        var connections = new DropboxConnectionStore(instance.StateAuthority);
        await connections.SaveNewAsync(new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            AccountId, CloudNamespaceId, CloudNamespaceId, DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes, DisplayName: "Nested partition cloud fixture",
            Email: "nested-partition@example.invalid").Normalize());
        var host = new SyncAuthorityRuntimeHost(instance.StateAuthority,
            new HttpClient(handler ?? new NoNetwork()), new SyncPairStore(instance.StateAuthority),
            new SyncRuntimeStateStore(instance.StateAuthority), connections, new FixtureCredentials(), database,
            (_, _) => remote ?? new FakeSyncRemoteStore());
        await host.LoadPairsAsync();
        return new CloudRig(root, databasePath, database, host, instance);
    }

    /// <summary>ENP-36: baseline. The legacy document-less parent carries no namespace of its own.</summary>
    private static Task DocumentLessParentBaselineAsync()
    {
        var (parent, child) = PolicyInputPair("baseline");
        Require(parent.EndpointDocument is null && parent.LocalRoot.Length != 0,
            "ENP-36 the retained parent really is the document-less legacy local/cloud shape");
        var retainedCloud = SyncCloudPairConfiguration.SavedEndpoints(parent)!.Right;
        Require(retainedCloud.Kind == SyncEndpointKind.Dropbox && string.IsNullOrWhiteSpace(retainedCloud.NamespaceId),
            "ENP-36 the legacy compatibility projection of the retained cloud side carries NO namespace");
        Require(SyncPairPartitionPolicy.Decide(child, parent) == SyncPairPartitionRelation.Unverified,
            "ENP-36 the shared decision refuses the excluded nested pair as Unverified while that namespace is absent");
        return Task.CompletedTask;
    }

    /// <summary>ENP-37: the same retained pair, projected from its OWN authority, yields NestedAllowed.</summary>
    private static async Task OwnAuthorityProjectsNestedAllowedAsync()
    {
        var (parent, child) = PolicyInputPair("projection");
        using var rig = await CloudRigAsync("projection");
        await rig.Database.RegisterPairAsync(parent.Id, parent.PolicyRevision);
        await rig.Database.RetainPairDefinitionAsync(parent, select: true);

        var unprojected = await rig.Host.ProjectRetainedPairEndpointsAsync(parent, default);
        Require(unprojected.Comparison.EndpointDocument is null,
            "ENP-37 a retained pair with no active root binding of its own is never projected");
        Require(SyncPairPartitionPolicy.Decide(child, unprojected.Comparison) ==
                SyncPairPartitionRelation.Unverified,
            "ENP-37 without the pair's OWN retained authority the shared decision stays Unverified");

        await RegisterOwnRetainedAuthorityAsync(rig, parent, ProviderAccountIdentity(), CloudNamespaceId,
            parent.DropboxFolderId);

        var projected = await rig.Host.ProjectRetainedPairEndpointsAsync(parent, default);
        Require(projected.Comparison.EndpointDocument is not null &&
                projected.Comparison.LeftEndpoint is { IsLocal: true } &&
                projected.Comparison.RightEndpoint is { NamespaceId: CloudNamespaceId },
            "ENP-37 the projection carries the pair's OWN verified namespace on its cloud side and preserves the ordered local side");
        Require(SyncPairPartitionPolicy.Decide(child, projected.Comparison) ==
                SyncPairPartitionRelation.NestedAllowed,
            "ENP-37 the shared decision admits the excluded nested pair as NestedAllowed");
        Require(SyncPairPartitionPolicy.DecideWithHierarchy(child, [],
                projected.Comparison, []) == SyncPairPartitionRelation.NestedAllowed,
            "ENP-37 the provider-verified decision entry point reaches the same NestedAllowed relation");
        Require(SyncPairPartitionPolicy.ExcludesWholeSubtree(projected.Comparison, ChildName),
            "ENP-37 the enclosing pair really excludes the whole Child subtree");
        Require(await projected.StillHeldAsync(rig.Database, parent.Id, default),
            "ENP-37 the retained facts the projection was derived from are still held");
    }

    /// <summary>ENP-38: every missing or mismatched own-retained fact leaves the pair fail-closed.</summary>
    private static async Task RetainedTupleFailsClosedAsync()
    {
        var (parent, child) = PolicyInputPair("failclosed");
        var account = ProviderAccountIdentity();
        var binding = RetainedParentRootBindingId(parent);
        await RefusedProjectionAsync("failclosed-absent-root", parent, child, async rig =>
        {
            await rig.Database.RegisterPairAsync(parent.Id, parent.PolicyRevision);
            await rig.Database.RetainPairDefinitionAsync(parent, select: true);
        }, "no active root binding for the pair's own cloud side");
        await RefusedProjectionAsync("failclosed-inactive-account", parent, child, async rig =>
        {
            await rig.Database.RegisterPairAsync(parent.Id, parent.PolicyRevision);
            await RegisterOwnRetainedAuthorityAsync(rig, parent, account, CloudNamespaceId, parent.DropboxFolderId);
            await ExecuteAsync(rig.DatabasePath, "UPDATE provider_account SET status='revoked' WHERE provider_account_id='" + account + "';");
        }, "an inactive provider account behind the root binding");
        await RefusedProjectionAsync("failclosed-foreign-folder", parent, child, async rig =>
        {
            await rig.Database.RegisterPairAsync(parent.Id, parent.PolicyRevision);
            await RegisterOwnRetainedAuthorityAsync(rig, parent, account, CloudNamespaceId, "id:foreign-folder-431");
        }, "a root binding naming a different folder identity");
        await RefusedProjectionAsync("failclosed-foreign-namespace", parent, child, async rig =>
        {
            await rig.Database.RegisterPairAsync(parent.Id, parent.PolicyRevision);
            await RegisterOwnRetainedAuthorityAsync(rig, parent, account, "ns-foreign-431",
                parent.DropboxFolderId);
        }, "a retained account whose namespace is not the pair's own");
        await RefusedProjectionAsync("failclosed-explicit-namespace", parent, child, async rig =>
        {
            await rig.Database.RegisterPairAsync(parent.Id, parent.PolicyRevision);
            await rig.Database.RegisterProviderAccountAsync(account, Installation, AccountId, CloudNamespaceId,
                CloudNamespaceId, "Dropbox", 1);
            await rig.Database.RegisterRootBindingAsync(binding, parent.Id, "dropbox", 1,
                SyncAuthorityCloudRootPersistence.Serialize(SyncProviders.Dropbox, account, "ns-foreign-431",
                    parent.DropboxFolderId), parent.DropboxRoot);
            await rig.Database.RetainPairDefinitionAsync(parent, select: true);
        }, "an explicit cloud identity whose namespace is not the retained one");
    }

    /// <summary>ENP-39: the projection alone is not an admission - the enclosing exclusion still binds.</summary>
    [SupportedOSPlatform("windows")]
    private static async Task ProjectedWithoutExclusionRefusedAsync()
    {
        var (parent, child) = PolicyInputPair("no-exclusion", excludeChild: false);
        using var rig = await CloudRigAsync("no-exclusion");
        await rig.Database.RegisterPairAsync(parent.Id, parent.PolicyRevision);
        // The retained parent's OWN cloud root must use the production-derived root id (the Host's own Id
        // helper), so the real Host edit re-registers the SAME binding instead of colliding with a fixture alias.
        await rig.Database.RegisterProviderAccountAsync(ProviderAccountIdentity(), Installation, AccountId,
            CloudNamespaceId, CloudNamespaceId, "Dropbox", 1);
        await rig.Database.RegisterDropboxRootBindingAsync(
            HostId("root-dropbox", parent.Id, CloudNamespaceId, parent.DropboxFolderId), parent.Id, 1,
            Installation, ProviderAccountIdentity(), CloudNamespaceId, parent.DropboxFolderId, parent.DropboxRoot,
            cancellationToken: default, partitionCandidate: parent);
        await EnsureHostDerivedLocalRootAsync(rig, parent);
        await rig.Database.RetainPairDefinitionAsync(parent, select: true);
        var projected = await rig.Host.ProjectRetainedPairEndpointsAsync(parent, default);
        Require(projected.Comparison.EndpointDocument is not null,
            "ENP-39 the same projection is still applied to the unexcluded parent (so the refusal is not a projection failure)");
        Require(SyncPairPartitionPolicy.Decide(child, projected.Comparison) ==
                SyncPairPartitionRelation.NestedRefused,
            "ENP-39 with the Child exclusion removed the same nested pair is refused, not admitted");
        var refused = await CaptureAsync(() => rig.Host.BindEndpointPairAsync(child,
            cancellationToken: default, recoveryFolder: PolicyEditRecovery));
        Require(refused is SyncPairAuthoringReviewException,
            "ENP-39 the real authoring consumer refuses the unexcluded nested child: " + refused);
        Require((await rig.Database.LoadAllPairDefinitionsAsync()).All(item => item.Id != child.Id),
            "ENP-39 the refused nested child retained no pair definition");
    }

    /// <summary>ENP-40: the real authoring consumer, before and after the parent's own authority exists.</summary>
    [SupportedOSPlatform("windows")]
    private static async Task RealConsumerNestedAdmissionAsync()
    {
        var (parent, child) = PolicyInputPair("consumer");
        using var rig = await CloudRigAsync("consumer", NestedPartitionHandler());
        await rig.Database.RegisterPairAsync(parent.Id, parent.PolicyRevision);
        await rig.Database.RetainPairDefinitionAsync(parent, select: true);

        var refused = await CaptureAsync(() => rig.Host.BindEndpointPairAsync(child,
            cancellationToken: default, recoveryFolder: PolicyEditRecovery));
        Require(refused is SyncPairAuthoringReviewException review &&
                review.Message.Contains("could not be verified", StringComparison.Ordinal),
            "ENP-40 the real authoring consumer refuses the nested child while the retained parent has no own namespace: " + refused);
        Require((await rig.Database.LoadAllPairDefinitionsAsync()).All(item => item.Id != child.Id),
            "ENP-40 the first refusal retained no pair and no effect");

        await RegisterOwnRetainedAuthorityAsync(rig, parent, ProviderAccountIdentity(), CloudNamespaceId,
            parent.DropboxFolderId);

        // The same call now clears the partition gate AND the verified local-overlap waiver, and is stopped
        // only by a deliberately invalid recovery container - a boundary strictly AFTER the decision, reached
        // before any provider read and before any pair or effect is written.
        var pastGate = await CaptureAsync(() => rig.Host.BindEndpointPairAsync(child,
            cancellationToken: default,
            recoveryFolder: new DropboxFolderEntry("Recovery", "/Documents/Recovery", "id:not-the-container")));
        Require(pastGate is InvalidDataException &&
                pastGate.Message.Contains("Recovery folder must be", StringComparison.Ordinal),
            "ENP-40 with the parent's own retained namespace the SAME call passes the partition gate and the " +
            "verified local-overlap waiver, and is stopped only by the invalid recovery container: " + pastGate);
        Require((await rig.Database.LoadAllPairDefinitionsAsync()).All(item => item.Id != child.Id),
            "ENP-40 the post-gate refusal retained no pair and no effect");
    }

    /// <summary>ENP-42: with the parent's document preserved (B) the shared store accepts the nested set.</summary>
    [SupportedOSPlatform("windows")]
    private static async Task PreservedParentDocumentAdmitsNestedChildAsync()
    {
        var root = Directory.CreateDirectory(IOPath.Combine(_root, "cn636-documented")).FullName;
        var parentLocal = Directory.CreateDirectory(IOPath.Combine(root, RootName)).FullName;
        var childLocal = Directory.CreateDirectory(IOPath.Combine(parentLocal, ChildName)).FullName;
        var child = ChildCandidate(childLocal, SyncMode.DownloadOnly);
        using var rig = await CloudRigAsync("consumer-documented", NestedPartitionHandler());

        // Author the nested child first, so its OWN private control ownership path is known exactly.
        var bound = await rig.Host.BindEndpointPairAsync(child, cancellationToken: default,
            recoveryFolder: PolicyEditRecovery);
        Require(bound.Pair.EndpointDocument is not null && bound.Pair.ControlNamespace is not null,
            "ENP-42 the nested child is really bound with its own document and private control namespace");
        var ownership = OwnershipRelativeNames(bound.Pair, parentLocal, parentLocal);
        Require(ownership.Count != 0,
            "ENP-42 the nested child's own private control ownership really sits inside the enclosing root");

        // The enclosing parent KEEPS its explicit document (what correction B preserves) and excludes the
        // whole nested subtree INCLUDING the child's own private control ownership.
        var rules = new List<SyncSelectionRule> { new(ChildName, false) };
        rules.AddRange(ownership.Select(item => new SyncSelectionRule(item, false)));
        var documentLess = LegacyParentPair(parentLocal, rules);
        var document = SyncEndpointPersistence.Symmetric(new SyncPairEndpoints(
            LocalEndpointFromLease(parentLocal),
            CloudEndpointAt(ParentCloudRootPath, ParentCloudFolder, CloudNamespaceId), documentLess.Direction));
        var parent = documentLess with
        {
            EndpointSchemaVersion = document.Version,
            LeftEndpoint = document.Left,
            RightEndpoint = document.Right
        };
        await rig.Database.RegisterPairAsync(parent.Id, parent.PolicyRevision);
        await RegisterOwnRetainedAuthorityAsync(rig, parent, ProviderAccountIdentity(), CloudNamespaceId,
            parent.DropboxFolderId);

        Require(SyncPairPartitionPolicy.Decide(bound.Pair, parent) == SyncPairPartitionRelation.NestedAllowed,
            "ENP-42 with the enclosing policy excluding the child's own private ownership the nested pair is NestedAllowed");
        var set = await rig.Database.LoadPairSetAsync();
        Require(set.Pairs.Count == 2 && set.Pairs.Any(item => item.Id == bound.Pair.Id) &&
                set.Pairs.Any(item => item.Id == parent.Id && item.EndpointDocument is not null),
            "ENP-42 the shared retained pair set really accepts the nested parent and child with the document intact");

        // Single variable: losing the document (the observed degraded state) makes the same pair Unverified,
        // and the own-authority projection restores the same admitted relation from the parent's own rows.
        await rig.Database.RetainPairDefinitionAsync(documentLess, select: true);
        var degraded = (await rig.Database.LoadAllPairDefinitionsAsync()).Single(item => item.Id == parent.Id);
        Require(degraded.EndpointDocument is null &&
                SyncPairPartitionPolicy.Decide(bound.Pair, degraded) == SyncPairPartitionRelation.Unverified,
            "ENP-42 once the parent's document is absent the same nested pair is Unverified again (the observed defect)");
        var restored = await rig.Host.ProjectRetainedPairEndpointsAsync(degraded, default);
        Require(SyncPairPartitionPolicy.Decide(bound.Pair, restored.Comparison) ==
                SyncPairPartitionRelation.NestedAllowed,
            "ENP-42 and the own-authority projection restores the same NestedAllowed relation from the parent's own retained rows");
    }


    /// <summary>The child's own private control boundary name, exactly as the allocator creates it beside the
    /// child's local root: <c>.syncshark-control-</c> plus the child's own derived pair id.</summary>
    private static string ChildControlBoundaryName(SyncPair child) =>
        SyncControlNamespace.LocalPrefix + child.Id;

    private static Task<string> ParentJsonAsync(CloudRig rig, string pairId) =>
        ScalarAsync(rig.DatabasePath, "SELECT pair_json FROM sync_pair WHERE pair_id='" + pairId + "'");

    /// <summary>ENP-43: the nested child is REALLY registered through the real Host against a document-less
    /// parent that keeps its raw definition, and the missing-authority baseline refuses before any mutation.</summary>
    [SupportedOSPlatform("windows")]
    private static async Task CompleteNestedChildRegistrationAsync()
    {
        var root = Directory.CreateDirectory(IOPath.Combine(_root, "cn636-registration")).FullName;
        var parentLocal = Directory.CreateDirectory(IOPath.Combine(root, RootName)).FullName;
        var childLocal = Directory.CreateDirectory(IOPath.Combine(parentLocal, ChildName)).FullName;
        var child = ChildCandidate(childLocal, SyncMode.DownloadOnly);
        var parent = LegacyParentPair(parentLocal,
            [new SyncSelectionRule(ChildName, false),
             new SyncSelectionRule(ChildControlBoundaryName(child), false)]);

        // Baseline: the parent has no own retained authority at all.
        using (var rig = await CloudRigAsync("registration-baseline", NestedPartitionHandler()))
        {
            await rig.Database.RegisterPairAsync(parent.Id, parent.PolicyRevision);
            await rig.Database.RetainPairDefinitionAsync(parent, select: true);
            var before = await ParentJsonAsync(rig, parent.Id);
            var refused = await CaptureAsync(() => rig.Host.BindEndpointPairAsync(child,
                cancellationToken: default, recoveryFolder: PolicyEditRecovery));
            Require(refused is SyncPairAuthoringReviewException,
                "ENP-43 without the parent's own retained tuple the real Host refuses the nested child: " + refused);
            Require((await rig.Database.LoadAllPairDefinitionsAsync()).All(item => item.Id != child.Id) &&
                    await CountAsync(rig.DatabasePath, "SELECT count(*) FROM root_binding WHERE pair_id='" +
                        child.Id + "'") == 0,
                "ENP-43 the baseline refusal created no child pair definition and no child root binding");
            Require(await ParentJsonAsync(rig, parent.Id) == before,
                "ENP-43 the baseline refusal left the parent's raw pair_json byte-identical");
        }

        // Complete registration: the SAME child against the same document-less parent whose OWN retained
        // authority is present. The parent's raw definition must stay byte-identical.
        using (var rig = await CloudRigAsync("registration-complete", NestedPartitionHandler()))
        {
            await rig.Database.RegisterPairAsync(parent.Id, parent.PolicyRevision);
            await RegisterOwnRetainedAuthorityAsync(rig, parent, ProviderAccountIdentity(), CloudNamespaceId,
                parent.DropboxFolderId);
            var before = await ParentJsonAsync(rig, parent.Id);
            var admitted = await rig.Host.BindEndpointPairAsync(child, cancellationToken: default,
                recoveryFolder: PolicyEditRecovery);
            Require((await rig.Database.LoadAllPairDefinitionsAsync()).Any(item => item.Id == child.Id &&
                    item.EndpointDocument is not null && item.ControlNamespace is not null),
                "ENP-43 the real Host completes the nested child registration against the document-less parent");
            Require(admitted.Pair.DropboxFolderId == ChildCloudFolder &&
                    admitted.Pair.ConnectionId == CloudAccountConnectionId,
                "ENP-43 the registered child keeps its own exact endpoint identity");
            Require(await CountAsync(rig.DatabasePath, "SELECT count(*) FROM root_binding WHERE pair_id='" +
                    child.Id + "' AND side='dropbox' AND state='active'") == 1,
                "ENP-43 the registered child really retained its own active cloud root binding");
            var set = await rig.Database.LoadPairSetAsync();
            Require(set.Pairs.Count == 2 && set.Pairs.Any(item => item.Id == child.Id),
                "ENP-43 the shared retained pair set loads with the registered nested child");
            Require(await ParentJsonAsync(rig, parent.Id) == before,
                "ENP-43 the child registration never rewrote the parent's raw pair_json");
            Require(await CountAsync(rig.DatabasePath, "SELECT count(*) FROM operation WHERE pair_id='" + child.Id +
                    "' AND status NOT IN ('completed','cancelled','superseded')") == 0,
                "ENP-43 the registration left no nonterminal operation and started no transfer");
        }
    }

    /// <summary>ENP-44: every missing or mismatched own-retained fact refuses the real registration with no
    /// child definition, no child root binding and no change to the parent's raw definition.</summary>
    [SupportedOSPlatform("windows")]
    private static async Task RegistrationTupleFailsClosedAsync()
    {
        var account = ProviderAccountIdentity();
        var outcomes = new List<string>
        {
            "foreign-connection: " + await AttemptRegistrationAsync("reg-foreign-connection", async (rig, parent) =>
            {
                await rig.Database.RegisterPairAsync(parent.Id, parent.PolicyRevision);
                await RegisterOwnRetainedAuthorityAsync(rig, parent, account, CloudNamespaceId, parent.DropboxFolderId);
            }, parent => parent with { ConnectionId = "connection-not-the-account-route-431" }),
            "foreign-derivation: " + await AttemptRegistrationAsync("reg-foreign-derivation", async (rig, parent) =>
            {
                await rig.Database.RegisterPairAsync(parent.Id, parent.PolicyRevision);
                await rig.Database.RegisterProviderAccountAsync("provider-account-bogus-431", Installation,
                    AccountId, CloudNamespaceId, CloudNamespaceId, "Dropbox", 1);
                await rig.Database.RegisterDropboxRootBindingAsync(RetainedParentRootBindingId(parent), parent.Id,
                    1, Installation, "provider-account-bogus-431", CloudNamespaceId, parent.DropboxFolderId,
                    parent.DropboxRoot, cancellationToken: default, partitionCandidate: parent);
                await rig.Database.RetainPairDefinitionAsync(parent, select: true);
            }),
            "foreign-folder: " + await AttemptRegistrationAsync("reg-foreign-folder", async (rig, parent) =>
            {
                await rig.Database.RegisterPairAsync(parent.Id, parent.PolicyRevision);
                await RegisterOwnRetainedAuthorityAsync(rig, parent, account, CloudNamespaceId,
                    "id:foreign-folder-431");
            }),
            "foreign-provider: " + await AttemptRegistrationAsync("reg-foreign-provider", async (rig, parent) =>
            {
                await rig.Database.RegisterPairAsync(parent.Id, parent.PolicyRevision);
                await rig.Database.RegisterProviderAccountAsync(account, Installation, AccountId, CloudNamespaceId,
                    CloudNamespaceId, "Google", 1, default, SyncProviders.GoogleDrive);
                await rig.Database.RegisterDropboxRootBindingAsync(RetainedParentRootBindingId(parent), parent.Id,
                    1, Installation, account, CloudNamespaceId, parent.DropboxFolderId, parent.DropboxRoot,
                    cancellationToken: default, partitionCandidate: parent);
                await rig.Database.RetainPairDefinitionAsync(parent, select: true);
            }),
            "inactive-authority: " + await AttemptRegistrationAsync("reg-inactive-authority", async (rig, parent) =>
            {
                await rig.Database.RegisterPairAsync(parent.Id, parent.PolicyRevision);
                await RegisterOwnRetainedAuthorityAsync(rig, parent, account, CloudNamespaceId, parent.DropboxFolderId);
                await ExecuteAsync(rig.DatabasePath,
                    "UPDATE provider_account SET status='revoked' WHERE provider_account_id='" + account + "';");
            }),
            "foreign-namespace: " + await AttemptRegistrationAsync("reg-foreign-namespace", async (rig, parent) =>
            {
                await rig.Database.RegisterPairAsync(parent.Id, parent.PolicyRevision);
                await RegisterOwnRetainedAuthorityAsync(rig, parent, account, "ns-foreign-431", parent.DropboxFolderId);
            }),
            "removed-exclusion: " + await AttemptRegistrationAsync("reg-removed-exclusion", async (rig, parent) =>
            {
                await rig.Database.RegisterPairAsync(parent.Id, parent.PolicyRevision);
                await RegisterOwnRetainedAuthorityAsync(rig, parent, account, CloudNamespaceId, parent.DropboxFolderId);
            }, parent => parent with { SelectionRules = [] })
        };
        foreach (var outcome in outcomes) Console.WriteLine("ENP-44 observed: " + outcome);
        foreach (var outcome in outcomes)
            Require(outcome.Contains("refused=SyncPairAuthoringReviewException", StringComparison.Ordinal) ||
                    outcome.Contains("refused=InvalidOperationException", StringComparison.Ordinal) ||
                    outcome.Contains("refused=InvalidDataException", StringComparison.Ordinal),
                "ENP-44 the real registration refuses: " + outcome);
        foreach (var outcome in outcomes)
            Require(outcome.Contains("registered=False", StringComparison.Ordinal) &&
                    outcome.Contains("childRoots=0", StringComparison.Ordinal) &&
                    outcome.Contains("parentUnchanged=True", StringComparison.Ordinal),
                "ENP-44 the refusal had no effect on the child or the parent: " + outcome);
    }

    /// <summary>One real nested-registration attempt against a shaped document-less parent, returning the exact
    /// observed outcome as a compact diagnostic string.</summary>
    [SupportedOSPlatform("windows")]
    private static async Task<string> AttemptRegistrationAsync(string name,
        Func<CloudRig, SyncPair, Task> arrange, Func<SyncPair, SyncPair>? shape = null)
    {
        var root = Directory.CreateDirectory(IOPath.Combine(_root, "cn636-" + name)).FullName;
        var parentLocal = Directory.CreateDirectory(IOPath.Combine(root, RootName)).FullName;
        var childLocal = Directory.CreateDirectory(IOPath.Combine(parentLocal, ChildName)).FullName;
        var child = ChildCandidate(childLocal, SyncMode.DownloadOnly);
        var parent = (shape ?? (item => item))(LegacyParentPair(parentLocal,
            [new SyncSelectionRule(ChildName, false),
             new SyncSelectionRule(ChildControlBoundaryName(child), false)]));
        using var rig = await CloudRigAsync(name, NestedPartitionHandler());
        await arrange(rig, parent);
        // root723: this driver also reaches the Host prepared path, and a real published pair carries both own
        // roots, so register the Host-derived local root here rather than in the shared policy-case helper.
        await EnsureHostDerivedLocalRootAsync(rig, parent);
        var before = await ParentJsonAsync(rig, parent.Id);
        var refused = await CaptureAsync(() => rig.Host.BindEndpointPairAsync(child,
            cancellationToken: default, recoveryFolder: PolicyEditRecovery));
        var registered = (await rig.Database.LoadAllPairDefinitionsAsync()).Any(item => item.Id == child.Id);
        var childRoots = await CountAsync(rig.DatabasePath,
            "SELECT count(*) FROM root_binding WHERE pair_id='" + child.Id + "'");
        return name + ": refused=" + (refused?.GetType().Name ?? "NONE") + " registered=" + registered +
            " childRoots=" + childRoots + " parentUnchanged=" +
            (await ParentJsonAsync(rig, parent.Id) == before) + " detail=" +
            (refused?.Message.Replace('\n', ' ').Replace('\r', ' ') ?? "-");
    }

    /// <summary>ENP-41: a policy-only legacy edit never discards the explicit endpoint document.</summary>
    [SupportedOSPlatform("windows")]
    private static async Task PolicyOnlyEditPreservesDocumentAsync()
    {
        var root = Directory.CreateDirectory(IOPath.Combine(_root, "cn636-edit")).FullName;
        var parentLocal = Directory.CreateDirectory(IOPath.Combine(root, "Parent")).FullName;
        var otherLocal = Directory.CreateDirectory(IOPath.Combine(root, "Other")).FullName;

        // Forward: local-left / cloud-right.
        using (var rig = await CloudRigAsync("edit-forward", NestedPartitionHandler()))
        {
            var authored = await rig.Host.BindEndpointPairAsync(
                AuthoredDraft(parentLocal, ParentCloudRootPath, ParentCloudFolder, SyncMode.TwoWay),
                cancellationToken: default, recoveryFolder: PolicyEditRecovery);
            Require(authored.Pair.EndpointDocument is not null &&
                    authored.Pair.LeftEndpoint is { IsLocal: true } &&
                    authored.Pair.RightEndpoint?.Kind == SyncEndpointKind.Dropbox,
                "ENP-41 the explicit local/cloud pair really retains its endpoint document on creation");
            var edited = await rig.Host.BindPairAsync(PolicyOnlyEdit(authored.Pair, parentLocal,
                ParentCloudRootPath, ParentCloudFolder, SyncMode.DownloadOnly), default);
            Require(edited.Pair.Id == authored.Pair.Id,
                "ENP-41 the policy-only edit keeps the same pair identity");
            Require(edited.Pair.EndpointDocument is not null &&
                    edited.Pair.LeftEndpoint == authored.Pair.LeftEndpoint &&
                    edited.Pair.RightEndpoint == authored.Pair.RightEndpoint,
                "ENP-41 the policy-only legacy edit preserves the explicit endpoint document exactly");
            Require(edited.Pair.Direction == SyncMode.DownloadOnly,
                "ENP-41 the policy-only edit really applied its direction change");
            var retained = (await rig.Database.LoadAllPairDefinitionsAsync())
                .Single(item => item.Id == authored.Pair.Id);
            Require(retained.EndpointDocument is not null &&
                    retained.RightEndpoint == authored.Pair.RightEndpoint &&
                    retained.Direction == SyncMode.DownloadOnly,
                "ENP-41 the retained database definition still carries the document and the new direction");

            var before = JsonSerializer.Serialize((await rig.Database.LoadAllPairDefinitionsAsync())
                .Single(item => item.Id == authored.Pair.Id));
            var stale = await CaptureAsync(() => rig.Host.BindPairAsync(PolicyOnlyEdit(edited.Pair, parentLocal,
                ParentCloudRootPath, ParentCloudFolder, SyncMode.TwoWay,
                edited.Pair.PolicyRevision - 1), default));
            Require(stale is not null && JsonSerializer.Serialize((
                    await rig.Database.LoadAllPairDefinitionsAsync()).Single(item => item.Id == authored.Pair.Id)) == before,
                "ENP-41 a stale expected revision refuses with no effect on the retained definition: " + stale);
        }

        // Reversed: cloud-left / local-right, and the ordered document must survive a policy-only edit.
        using (var rig = await CloudRigAsync("edit-reversed", NestedPartitionHandler()))
        {
            var authored = await rig.Host.BindEndpointPairAsync(
                AuthoredReversedDraft(otherLocal, OtherCloudRootPath, OtherCloudFolder),
                cancellationToken: default, recoveryFolder: PolicyEditRecovery);
            Require(authored.Pair.LeftEndpoint?.Kind == SyncEndpointKind.Dropbox &&
                    authored.Pair.RightEndpoint is { IsLocal: true },
                "ENP-41 the reversed creation really retains a cloud-left/local-right document");
            var edited = await rig.Host.BindPairAsync(PolicyOnlyEdit(authored.Pair, otherLocal,
                OtherCloudRootPath, OtherCloudFolder, SyncMode.UploadOnly), default);
            Require(edited.Pair.Id == authored.Pair.Id &&
                    edited.Pair.LeftEndpoint == authored.Pair.LeftEndpoint &&
                    edited.Pair.RightEndpoint == authored.Pair.RightEndpoint &&
                    edited.Pair.LeftEndpoint?.Kind == SyncEndpointKind.Dropbox,
                "ENP-41 a policy-only edit of a REVERSED pair preserves the ordered document and its identity");

            // The shared selector itself: a changed binding identity never inherits the document.
            Require(SyncAuthorityRuntimeHost.RetainedPolicyEditEndpointDocument(authored.Pair, null, null) is null,
                "ENP-41 a pair with no retained and no requested document carries none");
            Require(SyncAuthorityRuntimeHost.RetainedPolicyEditEndpointDocument(authored.Pair, null,
                    authored.Pair.EndpointDocument) == authored.Pair.EndpointDocument,
                "ENP-41 a caller-supplied explicit document always wins");
            Require(SyncAuthorityRuntimeHost.RetainedPolicyEditEndpointDocument(authored.Pair, authored.Pair, null)
                    == authored.Pair.EndpointDocument,
                "ENP-41 an unchanged binding carries its retained document");
            Require(SyncAuthorityRuntimeHost.RetainedPolicyEditEndpointDocument(
                    authored.Pair with { DropboxFolderId = ParentCloudFolder }, authored.Pair, null) is null,
                "ENP-41 a changed binding identity never inherits the retained document");
        }
    }

    /// <summary>ENP-46 (root707 d): the real Host/CLI-shaped mixed parent edit - a retained legacy parent
    /// UploadOnly -> TwoWay that removes ONLY the Child exclusion while the nested child stays retained - with
    /// the child's real legacy namespace identities, full reopened-pair equality, an ALL-STATE rollback on a
    /// refused edit, cancellation, and the interrupted-publication recovery success/replay/refusals.</summary>
    [SupportedOSPlatform("windows")]
    private static Task RealHostExplicitMixedEditAsync() =>
        RealHostMixedEditRemovingOnlyChildExclusionAsync(false);

    [SupportedOSPlatform("windows")]
    private static Task RealHostDocumentlessMixedEditAsync() =>
        RealHostMixedEditRemovingOnlyChildExclusionAsync(true);

    [SupportedOSPlatform("windows")]
    private static async Task RealHostMixedEditRemovingOnlyChildExclusionAsync(bool documentLessParent)
    {
        var suffix = documentLessParent ? "-documentless" : "";
        var root = Directory.CreateDirectory(IOPath.Combine(_root, "cn636-mixed-edit" + suffix)).FullName;
        var parentLocal = Directory.CreateDirectory(IOPath.Combine(root, RootName)).FullName;
        var childLocal = Directory.CreateDirectory(IOPath.Combine(parentLocal, ChildName)).FullName;
        var child = ChildCandidate(childLocal, SyncMode.DownloadOnly);
        using var rig = await CloudRigAsync("mixed-edit" + suffix, NestedPartitionHandler());

        // 1) The real nested child, bound through the real Host: real legacy namespace identities + its own control.
        var bound = await rig.Host.BindEndpointPairAsync(child, cancellationToken: default,
            recoveryFolder: PolicyEditRecovery);
        Require(bound.Pair.EndpointDocument is not null && bound.Pair.ControlNamespace is not null,
            "ENP-46 the real nested child carries its own document and private control namespace");
        var ownership = OwnershipRelativeNames(bound.Pair, parentLocal, parentLocal);
        Require(ownership.Count != 0,
            "ENP-46 the child's private control ownership really sits inside the enclosing root");
        var childJsonBefore = await ParentJsonAsync(rig, bound.Pair.Id);

        // 2) The parent in the legacy shape: UploadOnly, excluding the child subtree AND the child's ownership.
        var rules = new List<SyncSelectionRule> { new(ChildName, false) };
        rules.AddRange(ownership.Select(item => new SyncSelectionRule(item, false)));
        var documentLess = LegacyParentPair(parentLocal, rules);
        var document = SyncEndpointPersistence.Symmetric(new SyncPairEndpoints(
            LocalEndpointFromLease(parentLocal),
            CloudEndpointAt(ParentCloudRootPath, ParentCloudFolder, CloudNamespaceId), documentLess.Direction));
        var registrationCandidate = documentLess with
        {
            EndpointSchemaVersion = document.Version, LeftEndpoint = document.Left, RightEndpoint = document.Right
        };
        var parent = documentLessParent ? documentLess : registrationCandidate;
        await rig.Database.RegisterPairAsync(parent.Id, parent.PolicyRevision);
        // The retained parent's OWN cloud root must use the production-derived root id (the Host's own Id
        // helper) so the real Host edit re-registers the SAME binding instead of colliding with a fixture alias.
        await rig.Database.RegisterProviderAccountAsync(ProviderAccountIdentity(), Installation, AccountId,
            CloudNamespaceId, CloudNamespaceId, "Dropbox", 1);
        await rig.Database.RegisterDropboxRootBindingAsync(
            HostId("root-dropbox", parent.Id, CloudNamespaceId, parent.DropboxFolderId), parent.Id, 1,
            Installation, ProviderAccountIdentity(), CloudNamespaceId, parent.DropboxFolderId, parent.DropboxRoot,
            cancellationToken: default, partitionCandidate: registrationCandidate);
        // root723: this case drives the Host prepared path, so the pair needs BOTH own roots registered exactly as
        // the Host derives them - the allocator's operation member references the local root binding by that id.
        // Bootstrap the fixture's known root tuple explicitly; a NEW unresolved document-less
        // candidate must still refuse. The retained definition below deliberately drops the
        // endpoint document, so the actual edit/readback exercises legacy own-authority projection.
        await EnsureHostDerivedLocalRootAsync(rig, registrationCandidate);
        await rig.Database.RetainPairDefinitionAsync(parent, select: true);

        // 3) The real Host mixed edit: UploadOnly -> TwoWay, removing ONLY the Child exclusion.
        var onlyOwnership = ownership.Select(item => new SyncSelectionRule(item, false)).ToList();
        var edited = await rig.Host.BindPairAsync(MixedEdit(parent, parentLocal, ParentCloudRootPath,
            ParentCloudFolder, SyncMode.TwoWay, onlyOwnership, parent.PolicyRevision), default);
        Require(edited.Pair.Id == parent.Id, "ENP-46 the mixed edit keeps the SAME parent identity");
        Require(edited.Pair.Direction == SyncMode.TwoWay && !edited.Pair.Enabled,
            "ENP-46 the mixed edit applies TwoWay while the pair stays disabled/unapproved");
        Require(edited.Pair.ControlNamespace is not null &&
                edited.Pair.ControlNamespace.NamespaceId == parent.Id,
            "ENP-46(root708-1) the RETURNED pair retains its private-control ownership");
        Require(!SyncPairPartitionPolicy.ExcludesWholeSubtree(edited.Pair, ChildName) &&
                SyncPairPartitionPolicy.ExcludesWholeSubtree(edited.Pair, ownership[0]),
            "ENP-46 only the Child exclusion was removed; the child's private ownership stays excluded");

        // 4) Reopen the FULL pair set: equality with the returned pair, the control retained, the child unchanged.
        var reopened = await rig.Database.LoadPairSetAsync();
        var reopenedParent = reopened.Pairs.Single(item => item.Id == parent.Id);
        Require(reopenedParent.ControlNamespace is not null &&
                reopenedParent.ControlNamespace.NamespaceId == parent.Id,
            "ENP-46(root708-1) the REOPENED definition retains the private-control ownership");
        Require(JsonSerializer.Serialize(reopenedParent) == JsonSerializer.Serialize(edited.Pair),
            "ENP-46 the reopened full definition equals the returned edited pair exactly");
        Require(reopened.Pairs.Any(item => item.Id == bound.Pair.Id) &&
                await ParentJsonAsync(rig, bound.Pair.Id) == childJsonBefore,
            "ENP-46 the nested child is retained and its definition is unchanged");
        // The mixed edit allocated the parent's private control namespace, so every LATER edit of this pair takes
        // the retained-control ATOMIC branch - which makes the refusal below a proof of the incident branch's
        // full-state rollback (a non-atomic path would have advanced the revision before its root refusal).
        Require(await CountAsync(rig.DatabasePath, "SELECT count(*) FROM pair_control_namespace WHERE pair_id='" +
                parent.Id + "' AND status='active'") == 1,
            "ENP-46 the mixed edit allocated the parent's private control namespace");
        var baseline = await PairStateAsync(rig.DatabasePath);

        var hostReopened = await rig.Host.LoadPairsAsync();
        Require(JsonSerializer.Serialize(hostReopened.Pairs.Single(item => item.Id == parent.Id)) ==
                JsonSerializer.Serialize(reopenedParent),
            "ENP-56 runtime readback retains the same authority-bound parent definition");
        var storage = await rig.Host.LoadStorageInventoryAsync();
        Require(storage.Rows.Any(item => item.PairId == parent.Id) &&
                storage.Rows.Any(item => item.PairId == bound.Pair.Id),
            "ENP-56 read-only Storage renders both retained nested pair roots");
        Require(await PairStateAsync(rig.DatabasePath) == baseline,
            "ENP-56 runtime and Storage readback leave published pair state unchanged");

        // 5) ALL-STATE rollback: removing the child's ownership exclusion too must refuse with nothing written.
        var pendingSelection = await CaptureAsync(() => rig.Host.BindPairAsync(MixedEdit(reopenedParent,
            parentLocal, ParentCloudRootPath, ParentCloudFolder, SyncMode.TwoWay, [],
            reopenedParent.PolicyRevision),
            default));
        Require(pendingSelection is not null,
            "ENP-46 removing the child's private ownership exclusion is refused: " + pendingSelection);
        Require(await PairStateAsync(rig.DatabasePath) == baseline,
            "ENP-46(root702 incident branch) the refused retained-control ATOMIC edit left the FULL state unchanged (definition/selection/roots/control/config)");

        // 6) Cancellation: a cancelled token must change nothing.
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var stopped = await CaptureAsync(() => rig.Host.BindPairAsync(MixedEdit(reopenedParent, parentLocal,
                ParentCloudRootPath, ParentCloudFolder, SyncMode.TwoWay, onlyOwnership, reopenedParent.PolicyRevision),
                cancelled.Token));
            Require(stopped is not null, "ENP-46 the cancelled mixed edit throws: " + stopped);
        }
        Require(await PairStateAsync(rig.DatabasePath) == baseline,
            "ENP-46 the cancelled mixed edit left the FULL state unchanged");

        // 7) root702(c) recovery at the DB level on this same real fixture: interrupt, recover, replay, refuse.
        var revision = reopenedParent.PolicyRevision;
        await rig.Database.RegisterPairAsync(parent.Id, revision + 1, PairConfig(reopenedParent));
        Require(await rig.Database.RecoverInterruptedSameRootPublicationAsync(parent.Id),
            "ENP-46(c) the interrupted publication is recovered on the real fixture");
        Require(await ParentJsonAsync(rig, parent.Id) == JsonSerializer.Serialize(reopenedParent),
            "ENP-46(c) the recovery restored the last coherent published definition exactly");
        Require(!await rig.Database.RecoverInterruptedSameRootPublicationAsync(parent.Id),
            "ENP-46(c) replaying the recovery on a coherent pair is a plain no-op");
    }

    /// <summary>ENP-47 (Architect371-r2 / root715): the positive real Host mixed edit that STARTS WITH an
    /// already-retained private control namespace, so it must publish through the retained-control ATOMIC branch;
    /// plus a controlled in-flight cancellation at the pre-commit boundary and a reopen consistency check.</summary>
    [SupportedOSPlatform("windows")]
    private static async Task RealHostRetainedControlAtomicMixedEditAsync()
    {
        var root = Directory.CreateDirectory(IOPath.Combine(_root, "cn636-atomic-mixed")).FullName;
        var parentLocal = Directory.CreateDirectory(IOPath.Combine(root, RootName)).FullName;
        var childLocal = Directory.CreateDirectory(IOPath.Combine(parentLocal, ChildName)).FullName;
        var child = ChildCandidate(childLocal, SyncMode.DownloadOnly);
        using var rig = await CloudRigAsync("atomic-mixed", NestedPartitionHandler());

        var boundChild = await rig.Host.BindEndpointPairAsync(child, cancellationToken: default,
            recoveryFolder: PolicyEditRecovery);
        var ownership = OwnershipRelativeNames(boundChild.Pair, parentLocal, parentLocal);
        Require(ownership.Count != 0,
            "ENP-47 the child's private control ownership sits inside the enclosing root");
        var childJsonBefore = await ParentJsonAsync(rig, boundChild.Pair.Id);

        // Bind the PARENT through the real Host, so it STARTS WITH its own retained private control namespace.
        var parentRules = new List<SyncSelectionRule> { new(ChildName, false) };
        parentRules.AddRange(ownership.Select(item => new SyncSelectionRule(item, false)));
        var template = LegacyParentPair(parentLocal, parentRules) with { Direction = SyncMode.UploadOnly };
        var parentDraft = SyncEndpointPairDraft.Create(new SyncPairEndpoints(
            LocalEndpointFromLease(parentLocal),
            CloudEndpointAt(ParentCloudRootPath, ParentCloudFolder, CloudNamespaceId), SyncMode.UploadOnly),
            "Parent pair", template);
        var boundParent = await rig.Host.BindEndpointPairAsync(parentDraft, cancellationToken: default,
            recoveryFolder: PolicyEditRecovery);
        Require(boundParent.Pair.ControlNamespace is not null && !boundParent.Pair.Enabled &&
                boundParent.Pair.Direction == SyncMode.UploadOnly,
            "ENP-47 the parent starts as a real disabled UploadOnly pair WITH an already-retained control namespace");
        Require(await CountAsync(rig.DatabasePath, "SELECT count(*) FROM pair_control_namespace WHERE pair_id='" +
                boundParent.Pair.Id + "' AND status='active'") == 1,
            "ENP-47 the parent's retained private control namespace really exists BEFORE the edit (atomic branch)");

        // The positive mixed edit starts WITH that retained control, so it must publish atomically.
        var onlyOwnership = ownership.Select(item => new SyncSelectionRule(item, false)).ToList();
        var atomicHits = 0;
        SyncAuthorityDatabase.PublishedSameRootEditAfterWritesForTest = () => atomicHits++;
        SyncPair edited;
        try
        {
            edited = (await rig.Host.BindPairAsync(MixedEdit(boundParent.Pair, parentLocal, ParentCloudRootPath,
                ParentCloudFolder, SyncMode.TwoWay, onlyOwnership, boundParent.Pair.PolicyRevision), default)).Pair;
        }
        finally { SyncAuthorityDatabase.PublishedSameRootEditAfterWritesForTest = null; }
        Require(atomicHits == 1,
            "ENP-47 the positive retained-control edit really published through the ATOMIC branch (in-transaction boundary reached)");
        Require(edited.Id == boundParent.Pair.Id && edited.Direction == SyncMode.TwoWay &&
                !edited.Enabled,
            "ENP-47 the retained-control mixed edit keeps the SAME id, applies TwoWay and stays disabled/unapproved");
        Require(edited.ControlNamespace is not null &&
                edited.ControlNamespace.NamespaceId == boundParent.Pair.Id,
            "ENP-47 the atomic publication preserved the retained private-control ownership");
        Require(!SyncPairPartitionPolicy.ExcludesWholeSubtree(edited, ChildName),
            "ENP-47 only the Child exclusion was removed");
        var reopened = await rig.Database.LoadPairSetAsync();
        var reopenedParent = reopened.Pairs.Single(item => item.Id == boundParent.Pair.Id);
        Require(JsonSerializer.Serialize(reopenedParent) == JsonSerializer.Serialize(edited),
            "ENP-47 the reopened full definition equals the returned edited pair exactly");
        Require(await ParentJsonAsync(rig, boundChild.Pair.Id) == childJsonBefore,
            "ENP-47 the nested child is retained and unchanged");

        // LABELLED ACCURATELY: this cancels from the Host's PRE-REGISTRATION progress callback, which fires
        // before RegisterPairAuthorityAsync is entered - it is therefore a pre-registration boundary check and
        // proves nothing about cancellation after transactional writes begin. The in-transaction proof follows.
        var baseline = await PairStateAsync(rig.DatabasePath);
        using (var cts = new CancellationTokenSource())
        {
            var fired = false;
            var stopped = await CaptureAsync(() => rig.Host.BindPairAsync(
                MixedEdit(reopenedParent, parentLocal, ParentCloudRootPath, ParentCloudFolder, SyncMode.TwoWay,
                    onlyOwnership, reopenedParent.PolicyRevision),
                cts.Token, progress: message =>
                {
                    if (!fired && message.Contains("policy revision in the retained database",
                            StringComparison.Ordinal))
                    { fired = true; cts.Cancel(); }
                }));
            Require(fired, "ENP-47 (pre-registration boundary) the cancellation fired before the write began");
            Require(stopped is not null, "ENP-47 the controlled cancellation throws: " + stopped);
        }
        Require(await PairStateAsync(rig.DatabasePath) == baseline,
            "ENP-47 (pre-registration boundary) nothing had been written, so the whole state is unchanged");
        Require((await rig.Database.LoadPairSetAsync()).Pairs
                .Single(item => item.Id == boundParent.Pair.Id).Direction == SyncMode.TwoWay,
            "ENP-47 the pair set still reopens coherently after the cancelled edit");

        // The real proof: cancel from INSIDE the production atomic transaction, after the revision/config/root
        // writes and before the definition write, using the existing narrow fault boundary. The following
        // ThrowIfCancellationRequested observes the cancelled token, so the commit really aborts.
        var baseline2 = await PairStateAsync(rig.DatabasePath);
        using (var inner = new CancellationTokenSource())
        {
            var reached = false;
            SyncAuthorityDatabase.PublishedSameRootEditAfterWritesForTest = () =>
            { reached = true; inner.Cancel(); };
            var cancelled = false;
            try
            {
                await rig.Host.BindPairAsync(MixedEdit(reopenedParent, parentLocal, ParentCloudRootPath,
                    ParentCloudFolder, SyncMode.UploadOnly, onlyOwnership, reopenedParent.PolicyRevision),
                    inner.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }
            finally { SyncAuthorityDatabase.PublishedSameRootEditAfterWritesForTest = null; }
            Require(reached, "ENP-47 the in-transaction boundary was really reached after the preceding writes");
            Require(cancelled, "ENP-47 the cancelled token was observed before commit and aborted the edit");
        }
        Require(await PairStateAsync(rig.DatabasePath) == baseline2,
            "ENP-47 the in-transaction cancellation rolled the preceding revision/config/root/control writes back " +
            "(all affected authoritative rows unchanged)");
        Require((await rig.Database.LoadPairSetAsync()).Pairs
                .Single(item => item.Id == boundParent.Pair.Id).Direction == SyncMode.TwoWay,
            "ENP-47 the pair set still reopens coherently after the in-transaction cancellation");
    }

    /// <summary>The deliberate cloud/second-side fault: an ACTIVE dropbox root binding that already carries the
    /// parent's exact (account, namespace, folder) identity under a different root-binding id, so the parent's own
    /// cloud registration fails the duplicate guard at that stage - deliberately injected, not asserted from a
    /// diagnostic.</summary>
    private static async Task InjectCloudIdentityCollisionAsync(string dbPath, string ownerPairId)
    {
        var identity = "{\"providerAccountId\":\"" + ProviderAccountIdentity() +
            "\",\"rootNamespaceId\":\"" + CloudNamespaceId + "\",\"folderId\":\"" + ParentCloudFolder + "\"}";
        // An inert owner: a sync_pair row with NULL pair_json is filtered out of every published-pair reader,
        // so this row is genuine authority for the duplicate guard only and invisible to every other consumer.
        await ExecuteAsync(dbPath, "INSERT INTO sync_pair(pair_id,configuration_revision,state,created_utc) " +
            "VALUES('fixture-cloud-owner',1,'discovery_required','2026-10-09T00:00:00Z');");
        await ExecuteAsync(dbPath, "INSERT INTO root_binding(root_binding_id,pair_id,side,generation," +
            "identity_json,display_path,state,observed_utc) VALUES('fixture-cloud-collision','fixture-cloud-owner" +
            "','dropbox',1,'" + identity + "','/Documents/Collision-431','active','2026-10-09T00:00:00Z');");
    }

    /// <summary>ENP-48 (item 2): a fault deliberately injected at the cloud / second-side stage. On the retained-
    /// control ATOMIC pair it must reach exactly that boundary and roll the preceding revision/local-root writes
    /// back (full thirteen-table state unchanged). On the NO-retained-control pair the SAME injected fault is
    /// reached at the same stage but the preceding writes are already committed - which is the root715 gap,
    /// recorded here as evidence rather than asserted away. If a later correction makes the no-control path
    /// atomic, the second half of this case is expected to FAIL and should then be updated.</summary>
    [SupportedOSPlatform("windows")]
    private static async Task InjectedCloudStageFaultAsync()
    {
        // (A) retained-control pair -> atomic branch -> the cloud-stage fault must roll everything back.
        var root = Directory.CreateDirectory(IOPath.Combine(_root, "cn636-fault-atomic")).FullName;
        var parentLocal = Directory.CreateDirectory(IOPath.Combine(root, RootName)).FullName;
        var childLocal = Directory.CreateDirectory(IOPath.Combine(parentLocal, ChildName)).FullName;
        using (var rig = await CloudRigAsync("fault-atomic", NestedPartitionHandler()))
        {
            var boundChild = await rig.Host.BindEndpointPairAsync(ChildCandidate(childLocal, SyncMode.DownloadOnly),
                cancellationToken: default, recoveryFolder: PolicyEditRecovery);
            var ownership = OwnershipRelativeNames(boundChild.Pair, parentLocal, parentLocal);
            var parentRules = new List<SyncSelectionRule> { new(ChildName, false) };
            parentRules.AddRange(ownership.Select(item => new SyncSelectionRule(item, false)));
            var template = LegacyParentPair(parentLocal, parentRules) with { Direction = SyncMode.UploadOnly };
            var boundParent = await rig.Host.BindEndpointPairAsync(SyncEndpointPairDraft.Create(
                new SyncPairEndpoints(LocalEndpointFromLease(parentLocal),
                    CloudEndpointAt(ParentCloudRootPath, ParentCloudFolder, CloudNamespaceId), SyncMode.UploadOnly),
                "Parent pair", template), cancellationToken: default, recoveryFolder: PolicyEditRecovery);
            Require(boundParent.Pair.ControlNamespace is not null,
                "ENP-48(A) the parent really starts WITH a retained control namespace (atomic branch)");
            await InjectCloudIdentityCollisionAsync(rig.DatabasePath, boundChild.Pair.Id);
            var baseline = await PairStateAsync(rig.DatabasePath);
            var onlyOwnership = ownership.Select(item => new SyncSelectionRule(item, false)).ToList();
            var fault = await CaptureAsync(() => rig.Host.BindPairAsync(MixedEdit(boundParent.Pair, parentLocal,
                ParentCloudRootPath, ParentCloudFolder, SyncMode.TwoWay, onlyOwnership,
                boundParent.Pair.PolicyRevision), default));
            Require(fault is not null && fault.Message.Contains(
                    "The Dropbox folder identity is already bound to another active pair root.",
                    StringComparison.Ordinal),
                "ENP-48(A) the injected fault is reached at the cloud/second-side stage: " + fault);
            Require(await PairStateAsync(rig.DatabasePath) == baseline,
                "ENP-48(A) the atomic branch rolled the preceding revision and local-root writes back (full state unchanged)");
        }

        // (B) no-retained-control pair -> non-atomic path -> the SAME fault leaves the preceding writes committed.
        var root2 = Directory.CreateDirectory(IOPath.Combine(_root, "cn636-fault-nonatomic")).FullName;
        var parentLocal2 = Directory.CreateDirectory(IOPath.Combine(root2, RootName)).FullName;
        var childLocal2 = Directory.CreateDirectory(IOPath.Combine(parentLocal2, ChildName)).FullName;
        using (var rig2 = await CloudRigAsync("fault-nonatomic", NestedPartitionHandler()))
        {
            var boundChild2 = await rig2.Host.BindEndpointPairAsync(ChildCandidate(childLocal2, SyncMode.DownloadOnly),
                cancellationToken: default, recoveryFolder: PolicyEditRecovery);
            var ownership2 = OwnershipRelativeNames(boundChild2.Pair, parentLocal2, parentLocal2);
            var rules2 = new List<SyncSelectionRule> { new(ChildName, false) };
            rules2.AddRange(ownership2.Select(item => new SyncSelectionRule(item, false)));
            var documentLess2 = LegacyParentPair(parentLocal2, rules2);
            var document2 = SyncEndpointPersistence.Symmetric(new SyncPairEndpoints(
                LocalEndpointFromLease(parentLocal2),
                CloudEndpointAt(ParentCloudRootPath, ParentCloudFolder, CloudNamespaceId),
                documentLess2.Direction));
            var parent2 = documentLess2 with
            {
                EndpointSchemaVersion = document2.Version, LeftEndpoint = document2.Left,
                RightEndpoint = document2.Right
            };
            await rig2.Database.RegisterPairAsync(parent2.Id, parent2.PolicyRevision);
            await rig2.Database.RegisterProviderAccountAsync(ProviderAccountIdentity(), Installation, AccountId,
                CloudNamespaceId, CloudNamespaceId, "Dropbox", 1);
            await rig2.Database.RegisterDropboxRootBindingAsync(
                HostId("root-dropbox", parent2.Id, CloudNamespaceId, parent2.DropboxFolderId), parent2.Id, 1,
                Installation, ProviderAccountIdentity(), CloudNamespaceId, parent2.DropboxFolderId,
                parent2.DropboxRoot, cancellationToken: default, partitionCandidate: parent2);
        await EnsureHostDerivedLocalRootAsync(rig2, parent2);
            await rig2.Database.RetainPairDefinitionAsync(parent2, select: true);
            await InjectCloudIdentityCollisionAsync(rig2.DatabasePath, boundChild2.Pair.Id);
            var before2 = await PublishedAuthorityAsync(rig2.DatabasePath, parent2.Id);
            var onlyOwnership2 = ownership2.Select(item => new SyncSelectionRule(item, false)).ToList();
            var fault2 = await CaptureAsync(() => rig2.Host.BindPairAsync(MixedEdit(parent2, parentLocal2,
                ParentCloudRootPath, ParentCloudFolder, SyncMode.TwoWay, onlyOwnership2,
                parent2.PolicyRevision), default));
            Require(fault2 is not null && fault2.Message.Contains(
                    "The Dropbox folder identity is already bound to another active pair root.",
                    StringComparison.Ordinal),
                "ENP-48(B) the same injected fault is reached at the cloud stage on the no-control path: " + fault2);
            Require(await PublishedAuthorityAsync(rig2.DatabasePath, parent2.Id) == before2,
                "ENP-48(B) the injected cloud fault left the PUBLISHED authority unchanged (definition, selection and active control ownership)");
            Require(await ScalarAsync(rig2.DatabasePath, "SELECT count(*) FROM pair_control_namespace WHERE pair_id='" + parent2.Id + "' AND status='active';") == "0",
                "ENP-48(B) preparation published NO active pair control binding");
            Require(await ScalarAsync(rig2.DatabasePath, "SELECT count(*) FROM operation WHERE pair_id='" + parent2.Id + "' AND kind='allocate_control';") != "0",
                "ENP-48(B) the truthful retained preparation journal is present for this pair");
            Require(await ScalarAsync(rig2.DatabasePath, "SELECT count(*) FROM namespace_allocation a JOIN operation o ON o.operation_id=a.operation_id WHERE o.pair_id='" + parent2.Id + "' AND a.status='verified' AND a.bound_identity IS NOT NULL;") == "1",
                "ENP-48(B) exactly one VERIFIED prepared allocation carrying its exact physical identity is retained");
            Require(await ScalarAsync(rig2.DatabasePath, "SELECT count(*) FROM effect_receipt r JOIN operation_attempt t ON t.attempt_id=r.attempt_id JOIN operation o ON o.operation_id=t.operation_id WHERE o.pair_id='" + parent2.Id + "';") != "0",
                "ENP-48(B) the truthful effect receipt for the preparation is retained");
            Require(await ScalarAsync(rig2.DatabasePath, "SELECT count(*) FROM operation WHERE pair_id='" + parent2.Id + "' AND kind='allocate_control' AND status='completed';") == "0",
                "ENP-48(B) the prepared operation was NOT completed - the completion rolled back with the failed publication");
            var afterRestart2 = await new SyncAuthorityDatabase(rig2.DatabasePath).LoadPairSetAsync();
            Require(afterRestart2.Pairs.Any(item => item.Id == parent2.Id) &&
                    await PublishedAuthorityAsync(rig2.DatabasePath, parent2.Id) == before2,
                "ENP-48(B) a restart after physical creation but before publication reopens the SAME coherent published authority");
        }
    }



    /// <summary>The fixture CLI runtime: the REAL SyncCliOperationsBase surface over this rig's Host and state,
    /// so SyncCliRegistry.Parse + DispatchAsync drive the shipped command path rather than a test double.</summary>
    /// <summary>root729 final item: the complete isolated CLI journey - a real CLI mixed edit, the real
    /// first-sync prepare, and the FULL retained plan read page by page - all through the real registry and
    /// dispatcher against the real Host, authority and provider transport.</summary>
    [SupportedOSPlatform("windows")]
    private static async Task RealCliMixedEditPrepareAndPagedPlanAsync()
    {
        var root = Directory.CreateDirectory(IOPath.Combine(_root, "cn636-cli-journey")).FullName;
        var parentLocal = Directory.CreateDirectory(IOPath.Combine(root, RootName)).FullName;
        var childLocal = Directory.CreateDirectory(IOPath.Combine(parentLocal, ChildName)).FullName;
        var journeyRemote = new FakeSyncRemoteStore { ExactPathMetadataForTest = true };
        journeyRemote.PutBytes(ChildName + "/Inner.txt", System.Text.Encoding.UTF8.GetBytes("child inner payload"));
        using var rig = await CloudRigAsync("cli-journey", NestedPartitionHandler(), journeyRemote);

        var boundChild = await rig.Host.BindEndpointPairAsync(ChildCandidate(childLocal, SyncMode.DownloadOnly),
            cancellationToken: default, recoveryFolder: PolicyEditRecovery);
        var ownership = OwnershipRelativeNames(boundChild.Pair, parentLocal, parentLocal);
        Require(ownership.Count != 0, "the child's private control ownership sits inside the enclosing root");
        // ENP-49-style real preimage: the child's retained definition and its OWN configuration revision are
        // captured BEFORE the parent mixed edit and must be identical after the complete edit/prepare/paged plan.
        var childJsonBefore = await ParentJsonAsync(rig, boundChild.Pair.Id);
        var childRevisionBefore = await CountAsync(rig.DatabasePath,
            "SELECT configuration_revision FROM sync_pair WHERE pair_id='" + boundChild.Pair.Id + "';");

        var parentRules = new List<SyncSelectionRule> { new(ChildName, false) };
        parentRules.AddRange(ownership.Select(item => new SyncSelectionRule(item, false)));
        var template = LegacyParentPair(parentLocal, parentRules) with { Direction = SyncMode.UploadOnly };
        var boundParent = await rig.Host.BindEndpointPairAsync(SyncEndpointPairDraft.Create(
            new SyncPairEndpoints(LocalEndpointFromLease(parentLocal),
                CloudEndpointAt(ParentCloudRootPath, ParentCloudFolder, CloudNamespaceId), SyncMode.UploadOnly),
            "Parent pair", template), cancellationToken: default, recoveryFolder: PolicyEditRecovery);
        var parentId = boundParent.Pair.Id;
        await File.WriteAllTextAsync(IOPath.Combine(parentLocal, "Outside.txt"), "parent outside payload");

        var runtime = new PartitionCliRuntime(rig);
        var onlyOwnership = ownership.Select(item => new SyncSelectionRule(item, false)).ToList();
        var settings = JsonSerializer.Serialize(new { direction = "TwoWay", selectionRules = onlyOwnership });
        var atomicHits = 0;
        SyncAuthorityDatabase.PublishedSameRootEditAfterWritesForTest = () => atomicHits++;
        try
        {
            await SyncCliRegistry.DispatchAsync(runtime, SyncCliRegistry.Parse(["--cli", "pairs", "edit",
                "--pair", parentId, "--account", CloudAccountConnectionId, "--generation",
                boundParent.Pair.PolicyRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--settings", settings, "--disable", "--yes"]), System.IO.TextWriter.Null, default);
        }
        finally { SyncAuthorityDatabase.PublishedSameRootEditAfterWritesForTest = null; }
        Require(atomicHits == 1, "the real CLI mixed edit published through the atomic branch");

        var prepared = await SyncCliRegistry.DispatchAsync(runtime, SyncCliRegistry.Parse(["--cli",
            "first-sync", "prepare", "--pair", parentId, "--yes"]), System.IO.TextWriter.Null, default);
        Require(prepared is not null, "the real CLI first-sync prepare completed and returned its preparation data");

        var first = (SyncCliPlanData)await SyncCliRegistry.DispatchAsync(runtime, SyncCliRegistry.Parse(["--cli",
            "plan", "--pair", parentId, "--limit", "2", "--offset", "0"]), System.IO.TextWriter.Null, default);
        var seen = new List<string>();
        var page = first;
        var offset = 0;
        while (true)
        {
            seen.AddRange(page.Items.Select(item => item.RelativePath));
            if (!page.HasMore || page.Items.Count == 0) break;
            offset += page.Items.Count;
            page = (SyncCliPlanData)await SyncCliRegistry.DispatchAsync(runtime, SyncCliRegistry.Parse(["--cli",
                "plan", "--pair", parentId, "--limit", "2", "--offset",
                offset.ToString(System.Globalization.CultureInfo.InvariantCulture)]), System.IO.TextWriter.Null, default);
        }
        Require(seen.Count == first.TotalItems && seen.Distinct(StringComparer.OrdinalIgnoreCase).Count() == seen.Count,
            "the full paged plan returns exactly the retained total exactly once across every page");
        Require(seen.Any(item => string.Equals(item, "Outside.txt", StringComparison.OrdinalIgnoreCase)),
            "the prepared plan carries the seeded local payload outside the excluded child subtree");
        var full = (SyncCliPlanData)await SyncCliRegistry.DispatchAsync(runtime, SyncCliRegistry.Parse(["--cli",
            "plan", "--pair", parentId, "--limit", "200", "--offset", "0"]), System.IO.TextWriter.Null, default);
        string Norm(string path) => path.Replace('\\', '/');
        Require(full.Items.Any(item => Norm(item.RelativePath) == "Outside.txt" && item.Group == "Local only"),
            "the mixed plan CLASSIFIES the outside payload as a LOCAL-only item (plan classification only, not a completed transfer)");
        Require(full.Items.Any(item => Norm(item.RelativePath) == ChildName + "/Inner.txt" &&
                item.Group == "Dropbox only"),
            "the mixed plan CLASSIFIES the nested child payload as a DROPBOX-only item under the included child subtree (plan classification only, not a completed transfer or conflict-winner outcome)");
        var reopenedParent = (await rig.Database.LoadPairSetAsync()).Pairs.Single(item => item.Id == parentId);
        Require(ownership.All(name => SyncPairPartitionPolicy.ExcludesWholeSubtree(reopenedParent, name)),
            "the mixed plan keeps every child private-ownership exclusion and the privacy selection intact");
        Require((await rig.Database.LoadPairSetAsync()).Pairs.Any(item => item.Id == boundChild.Pair.Id),
            "the nested child keeps its retained identity (presence) through the mixed journey");
        Require(await ParentJsonAsync(rig, boundChild.Pair.Id) == childJsonBefore &&
                await CountAsync(rig.DatabasePath, "SELECT configuration_revision FROM sync_pair WHERE pair_id='" +
                    boundChild.Pair.Id + "';") == childRevisionBefore,
            "the nested child's retained definition AND its own configuration revision are unchanged after the complete edit, prepare and paged plan");
    }

    private sealed class PartitionCliRuntime(CloudRig rig) : SyncCliOperationsBase
    {
        protected override SyncAuthorityRuntimeHost Host => rig.Host;
        protected override PilotStateAuthority StateAuthority => rig.Instance.StateAuthority;
        protected override bool ApplicationOwnsRuntime => false;
        public override string Mode => "fixture";
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>ENP-49 (root707 d, CLI leg): the same mixed parent edit driven through the REAL registry and
    /// dispatcher (SyncCliRegistry.Parse + DispatchAsync, `pairs edit`) instead of the Host API directly - with
    /// the retained nested child, only the Child exclusion removed, and the child unchanged afterwards.</summary>
    [SupportedOSPlatform("windows")]
    private static async Task RealCliMixedEditRemovingOnlyChildExclusionAsync()
    {
        var root = Directory.CreateDirectory(IOPath.Combine(_root, "cn636-cli-edit")).FullName;
        var parentLocal = Directory.CreateDirectory(IOPath.Combine(root, RootName)).FullName;
        var childLocal = Directory.CreateDirectory(IOPath.Combine(parentLocal, ChildName)).FullName;
        using var rig = await CloudRigAsync("cli-edit", NestedPartitionHandler());

        var boundChild = await rig.Host.BindEndpointPairAsync(ChildCandidate(childLocal, SyncMode.DownloadOnly),
            cancellationToken: default, recoveryFolder: PolicyEditRecovery);
        var ownership = OwnershipRelativeNames(boundChild.Pair, parentLocal, parentLocal);
        Require(ownership.Count != 0, "ENP-49 the child's private control ownership sits inside the enclosing root");
        var childJsonBefore = await ParentJsonAsync(rig, boundChild.Pair.Id);

        var parentRules = new List<SyncSelectionRule> { new(ChildName, false) };
        parentRules.AddRange(ownership.Select(item => new SyncSelectionRule(item, false)));
        var template = LegacyParentPair(parentLocal, parentRules) with { Direction = SyncMode.UploadOnly };
        var boundParent = await rig.Host.BindEndpointPairAsync(SyncEndpointPairDraft.Create(
            new SyncPairEndpoints(LocalEndpointFromLease(parentLocal),
                CloudEndpointAt(ParentCloudRootPath, ParentCloudFolder, CloudNamespaceId), SyncMode.UploadOnly),
            "Parent pair", template), cancellationToken: default, recoveryFolder: PolicyEditRecovery);
        Require(boundParent.Pair.ControlNamespace is not null,
            "ENP-49 the CLI-driven parent starts WITH a retained control namespace (atomic branch)");

        var onlyOwnership = ownership.Select(item => new SyncSelectionRule(item, false)).ToList();
        var settings = JsonSerializer.Serialize(new { direction = "TwoWay", selectionRules = onlyOwnership });
        var invocation = SyncCliRegistry.Parse(["--cli", "pairs", "edit", "--pair", boundParent.Pair.Id,
            "--account", CloudAccountConnectionId, "--generation",
            boundParent.Pair.PolicyRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--settings", settings, "--disable", "--yes"]);
        Require(invocation.Command == "pairs edit" && invocation.PairId == boundParent.Pair.Id,
            "ENP-49 the real parser resolves the exact pairs edit command and pair");

        var atomicHits = 0;
        SyncAuthorityDatabase.PublishedSameRootEditAfterWritesForTest = () => atomicHits++;
        try
        {
            await SyncCliRegistry.DispatchAsync(new PartitionCliRuntime(rig), invocation,
                System.IO.TextWriter.Null, default);
        }
        finally { SyncAuthorityDatabase.PublishedSameRootEditAfterWritesForTest = null; }
        Require(atomicHits == 1,
            "ENP-49 the CLI-driven edit published through the SAME atomic branch as the Host API");

        var reopened = await rig.Database.LoadPairSetAsync();
        var parent = reopened.Pairs.Single(item => item.Id == boundParent.Pair.Id);
        Require(parent.Direction == SyncMode.TwoWay && !parent.Enabled,
            "ENP-49 the CLI-driven mixed edit applied TwoWay while the pair stays disabled/unapproved");
        Require(parent.ControlNamespace is not null && parent.ControlNamespace.NamespaceId == parent.Id,
            "ENP-49 the CLI-driven edit retained the private control ownership");
        Require(!SyncPairPartitionPolicy.ExcludesWholeSubtree(parent, ChildName) &&
                SyncPairPartitionPolicy.ExcludesWholeSubtree(parent, ownership[0]),
            "ENP-49 the CLI-driven edit removed ONLY the Child exclusion");
        Require(reopened.Pairs.Any(item => item.Id == boundChild.Pair.Id) &&
                await ParentJsonAsync(rig, boundChild.Pair.Id) == childJsonBefore,
            "ENP-49 the nested child is retained and unchanged after the CLI edit");
    }

    private static SyncPairBindingRequest MixedEdit(SyncPair prior, string localRoot, string cloudPath,
        string cloudFolder, SyncMode direction, IReadOnlyList<SyncSelectionRule> rules, long revision) =>
        new(prior.Id, prior.Name, localRoot, new DropboxFolderEntry(prior.Name, cloudPath, cloudFolder),
            PolicyEditRecovery, Enabled: false, Priority: prior.Priority, PropagateDeletions: false,
            SelectionRules: rules, Direction: direction, ConnectionId: CloudAccountConnectionId,
            ExpectedPolicyRevision: revision);

    /// <summary>The whole authority state of the fixture DB (every row of the definition/selection/root/control/config
    /// tables), so a refusal can be proved to have left ALL of it unchanged.</summary>
    /// <summary>The Host's own production root-id derivation (role + '-' + first 32 hex of
    /// SHA256(role + '\n' + join('\n', parts))), so a fixture root uses the SAME id the real Host does.</summary>
    private static string HostId(string role, params string[] parts) => role + "-" +
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(role + "\n" + string.Join("\n", parts))))
            .ToLowerInvariant()[..32];

    private static SyncAuthorityPairConfiguration PairConfig(SyncPair pair) =>
        new("recoverable", 0, 1, "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true,
            AutomaticPruning: false, RecoveryRetentionDays: pair.RecoveryRetentionDays ?? 30);

    private static async Task<string> PairStateAsync(string dbPath)
    {
        // Every authoritative row this journey can touch: definition, selection (sync_pair.selected), roots,
        // the pair's control binding, the control namespace and its allocation, the provider recovery root,
        // the config revisions, and the operation/attempt/receipt/lease/history rows a refusal must not create.
        // Each table is read with an explicit deterministic ORDER BY and a stable column join, so the
        // comparison is byte-exact rather than order-dependent.
        var tables = new[] { "sync_pair", "pair_config_revision", "root_binding", "pair_control_namespace",
            "control_namespace", "namespace_allocation", "recovery_root_binding", "history_event", "operation",
            "operation_attempt", "effect_receipt", "lease", "resource_allocation" };
        var parts = new List<string>();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { DataSource = dbPath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync();
        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM " + table + " ORDER BY 1;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                parts.Add(table + ":" + string.Join("|", Enumerable.Range(0, reader.FieldCount)
                    .Select(i => reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i),
                        System.Globalization.CultureInfo.InvariantCulture))));
        }
        return string.Join("\n", parts);
    }

    private static SyncPairBindingRequest PolicyOnlyEdit(SyncPair prior, string localRoot, string cloudPath,
        string cloudFolder, SyncMode direction, long? revision = null) =>
        new(prior.Id, prior.Name, localRoot, new DropboxFolderEntry(prior.Name, cloudPath, cloudFolder),
            PolicyEditRecovery, Enabled: false, Priority: prior.Priority, PropagateDeletions: false,
            Direction: direction, ConnectionId: CloudAccountConnectionId,
            ExpectedPolicyRevision: revision ?? prior.PolicyRevision);

    /// <summary>ENP-45 (root702 b): a retained candidate whose explicit endpoint document carries NO cloud
    /// namespace - the legacy edit shape - is projected from its OWN retained authority, so the nested child is
    /// ADMITTED rather than refused, while the candidate's requested policy is preserved and the default
    /// (flag off) retained-peer contract is unchanged.</summary>
    [SupportedOSPlatform("windows")]
    private static async Task LegacyDocumentWithoutNamespaceAdmitsNestedChildAsync()
    {
        var root = Directory.CreateDirectory(IOPath.Combine(_root, "cn636-legacy-edit")).FullName;
        var parentLocal = Directory.CreateDirectory(IOPath.Combine(root, RootName)).FullName;
        var childLocal = Directory.CreateDirectory(IOPath.Combine(parentLocal, ChildName)).FullName;
        var child = ChildCandidate(childLocal, SyncMode.DownloadOnly);
        var documentLess = LegacyParentPair(parentLocal, [new SyncSelectionRule(ChildName, false)]);
        // The legacy-edit shape: an explicit endpoint document whose cloud side carries NO namespace of its own.
        var document = SyncEndpointPersistence.Symmetric(new SyncPairEndpoints(
            LocalEndpointFromLease(parentLocal), CloudEndpointAt(ParentCloudRootPath, ParentCloudFolder, null),
            documentLess.Direction));
        var candidate = documentLess with
        {
            EndpointSchemaVersion = document.Version,
            LeftEndpoint = document.Left,
            RightEndpoint = document.Right
        };
        Require(candidate.EndpointDocument is not null &&
                candidate.RightEndpoint is { Kind: SyncEndpointKind.Dropbox, NamespaceId: null or "" },
            "ENP-45 the candidate really carries an explicit document with NO cloud namespace");

        using var rig = await CloudRigAsync("legacy-edit", NestedPartitionHandler());
        await rig.Database.RegisterPairAsync(candidate.Id, candidate.PolicyRevision);
        await RegisterOwnRetainedAuthorityAsync(rig, candidate, ProviderAccountIdentity(), CloudNamespaceId,
            candidate.DropboxFolderId);

        // Default contract (flag off): an explicit document is its own source of truth and is returned UNCHANGED.
        var asRetainedPeer = await rig.Host.ProjectRetainedPairEndpointsAsync(candidate, default);
        Require(asRetainedPeer.Comparison.RightEndpoint is { NamespaceId: null or "" },
            "ENP-45 the default projection leaves an explicit document exactly as it is");
        Require(SyncPairPartitionPolicy.Decide(child, asRetainedPeer.Comparison) ==
                SyncPairPartitionRelation.Unverified,
            "ENP-45 without the candidate projection the missing namespace keeps the decision Unverified");

        // The new branch: project the candidate's namespace-less cloud side from its OWN retained authority.
        var asCandidate = await rig.Host.ProjectRetainedPairEndpointsAsync(candidate, default,
            projectSidesLackingNamespaceEvenWithDocument: true);
        Require(asCandidate.IsComplete && asCandidate.Comparison.RightEndpoint is { NamespaceId: CloudNamespaceId },
            "ENP-45(b) the candidate projection fills the cloud namespace from the pair's own retained authority");
        Require(asCandidate.Comparison.Direction == candidate.Direction &&
                string.Equals(asCandidate.Comparison.Name, candidate.Name, StringComparison.Ordinal),
            "ENP-45(b) the candidate's requested policy is preserved by the projection");
        Require(SyncPairPartitionPolicy.Decide(child, asCandidate.Comparison) ==
                SyncPairPartitionRelation.NestedAllowed,
            "ENP-45(b) the shared decision now ADMITS the excluded nested child instead of refusing");
        Require(await asCandidate.StillHeldAsync(rig.Database, candidate.Id, default),
            "ENP-45(b) the retained facts the candidate projection used are still held");
    }

    private static async Task RegisterOwnRetainedAuthorityAsync(CloudRig rig, SyncPair parent, string account,
        string accountNamespace, string folderId, string? identityNamespace = null)
    {
        await rig.Database.RegisterProviderAccountAsync(account, Installation, AccountId, accountNamespace,
            accountNamespace, "Dropbox", 1);
        await rig.Database.RegisterDropboxRootBindingAsync(RetainedParentRootBindingId(parent), parent.Id, 1,
            Installation, account, identityNamespace ?? accountNamespace, folderId, parent.DropboxRoot,
            cancellationToken: default, partitionCandidate: parent);
        await rig.Database.RetainPairDefinitionAsync(parent, select: true);
    }

    /// <summary>root723: a real published pair carries BOTH of its own roots, and the allocator's operation member
    /// references the local root by its production-derived id. A hand-built fixture pair must therefore register
    /// that local root exactly as the Host does - only in the cases that actually drive the Host prepared path.</summary>

    /// <summary>ENP-50 (root723): the (c) interrupted-publication recovery driven through the REAL Host action on a
    /// real isolated authority database - the same repair the CLI/GUI will call, not the DB type directly.</summary>
    [SupportedOSPlatform("windows")]
    private static async Task RealHostInterruptedPublicationRecoveryAsync()
    {
        var root = Directory.CreateDirectory(IOPath.Combine(_root, "cn636-host-recovery")).FullName;
        var local = Directory.CreateDirectory(IOPath.Combine(root, RootName)).FullName;
        using var rig = await CloudRigAsync("host-recovery", NestedPartitionHandler());
        var parent = LegacyParentPair(local, [new SyncSelectionRule(ChildName, false)]);
        // The published definition's own revision must match the retained configuration row, or the definition
        // write's configuration_revision predicate matches nothing (the earlier ENP-50 setup defect).
        await rig.Database.RegisterPairAsync(parent.Id, parent.PolicyRevision, PairConfig(parent));
        await EnsureHostDerivedLocalRootAsync(rig, parent);
        await rig.Database.RegisterProviderAccountAsync(ProviderAccountIdentity(), Installation, AccountId,
            CloudNamespaceId, CloudNamespaceId, "Dropbox", 1);
        await rig.Database.RegisterDropboxRootBindingAsync(
            HostId("root-dropbox", parent.Id, CloudNamespaceId, parent.DropboxFolderId), parent.Id, 1,
            Installation, ProviderAccountIdentity(), CloudNamespaceId, parent.DropboxFolderId, parent.DropboxRoot,
            cancellationToken: default, partitionCandidate: parent);
        await rig.Database.RetainPairDefinitionAsync(parent, select: true);
        // Interrupt the publication: advance the revision with no matching definition write.
        await rig.Database.RegisterPairAsync(parent.Id, parent.PolicyRevision + 1, PairConfig(parent));
        var incoherent = await CaptureAsync(() => rig.Host.LoadPairsAsync());
        Require(incoherent is not null,
            "ENP-50 the interrupted pair set does not read back through the real Host: " + incoherent);
        Require(await rig.Host.RecoverInterruptedPublicationAsync(parent.Id, default),
            "ENP-50 the REAL Host recovery restores the last coherent published revision");
        Require(await ScalarAsync(rig.DatabasePath, "SELECT configuration_revision FROM sync_pair WHERE pair_id='" +
                parent.Id + "';") == parent.PolicyRevision.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            "ENP-50 the Host-driven recovery restored the published revision");
        var reopened = await rig.Host.LoadPairsAsync();
        Require(reopened.Pairs.Any(item => item.Id == parent.Id),
            "ENP-50 the pair set reads back coherently through the real Host after the recovery");
        Require(!await rig.Host.RecoverInterruptedPublicationAsync(parent.Id, default),
            "ENP-50 replaying the Host recovery on a coherent pair is a plain no-op");
    }

    private static async Task EnsureHostDerivedLocalRootAsync(CloudRig rig, SyncPair parent)
    {
        var serial = parent.LocalVolumeSerialNumber.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
        var fileId = parent.LocalRootFileId.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
        var volumeId = await rig.Database.ResolveVolumeIdAsync(Installation, serial, "NTFS", null,
            supportsFileId128: false, supportsUsn: false, supportsAtomicRename: true, default);
        await rig.Database.RegisterLocalRootBindingAsync(HostId("root-local", parent.Id, volumeId, fileId),
            parent.Id, 1, Installation, volumeId, serial, fileId, parent.LocalRoot,
            cancellationToken: default, partitionCandidate: parent);
    }

    /// <summary>root729 obligation (2): a PUBLISHED same-root pair with NO active control namespace takes the
    /// Host's PREPARED-control publication - the real allocator creates the owned private control during the
    /// edit - so the ACTUAL returned definition carries the created control and a FRESHLY reopened database
    /// reads it back. No pair_control_namespace row and no control directory exist beforehand.</summary>
    [SupportedOSPlatform("windows")]
    private static async Task PreparedControlPublicationAsync()
    {
        var root = Directory.CreateDirectory(IOPath.Combine(_root, "cn636-prepared-control")).FullName;
        var parentLocal = Directory.CreateDirectory(IOPath.Combine(root, RootName)).FullName;
        using var rig = await CloudRigAsync("prepared-control", NestedPartitionHandler());
        // The published pair must carry an explicit endpoint document (the public edit path requires it) while
        // NO control namespace exists yet, so the edit takes the Host's PREPARED-control publication.
        var parent = SyncEndpointPairDraft.Create(new SyncPairEndpoints(
            LocalEndpointFromLease(parentLocal),
            CloudEndpointAt(ParentCloudRootPath, ParentCloudFolder, CloudNamespaceId), SyncMode.TwoWay),
            "Parent pair",
            LegacyParentPair(parentLocal, [new SyncSelectionRule(ChildName, false)]) with { Direction = SyncMode.TwoWay });
        await rig.Database.RegisterPairAsync(parent.Id, parent.PolicyRevision);
        await rig.Database.RegisterProviderAccountAsync(ProviderAccountIdentity(), Installation, AccountId,
            CloudNamespaceId, CloudNamespaceId, "Dropbox", 1);
        await rig.Database.RegisterDropboxRootBindingAsync(
            HostId("root-dropbox", parent.Id, CloudNamespaceId, parent.DropboxFolderId), parent.Id, 1,
            Installation, ProviderAccountIdentity(), CloudNamespaceId, parent.DropboxFolderId, parent.DropboxRoot,
            cancellationToken: default, partitionCandidate: parent);
        await EnsureHostDerivedLocalRootAsync(rig, parent);
        await rig.Database.RetainPairDefinitionAsync(parent, select: true);
        Require(await rig.Database.LoadPairControlNamespacePreimageAsync(parent.Id, "local", default) is null,
            "obligation(2) the published pair starts with NO active control namespace");
        var edited = parent with { Priority = parent.Priority + 1 };
        var bound = await rig.Host.BindEndpointPairAsync(edited, parent.Id, parent.PolicyRevision,
            recoveryFolder: PolicyEditRecovery);
        Require(bound.Pair.Id == parent.Id && bound.Pair.ControlNamespace is { } created &&
                created.NamespaceId == parent.Id && created.InstallationId == Installation,
            "obligation(2) the ACTUAL returned definition carries the control created by the prepared publication");
        Require(await rig.Database.LoadPairControlNamespacePreimageAsync(parent.Id, "local", default) is not null,
            "obligation(2) the retained authority now carries the active control binding");
        var reopened = new SyncAuthorityDatabase(rig.Database.DatabasePath);
        var reopenedPair = (await reopened.LoadPairSetAsync()).Pairs.Single(p => p.Id == parent.Id);
        Require(reopenedPair.ControlNamespace is { } reopenedControl &&
                reopenedControl.NamespaceId == parent.Id && reopenedControl.InstallationId == Installation,
            "obligation(2) a FRESHLY reopened database reads the same published control ownership");
    }

    /// <summary>root723 + root724-r2: the exact affected PUBLISHED-ROW projection - the pair definition/revision/
    /// selection, EVERY local/cloud/control root row's id, generation, state, identity and display path, the pair's
    /// control bindings, the referenced control-namespace ownership CONTENT (identity, marker, hash, status) and
    /// the recovery root rows. Counts alone cannot prove identities and generation/state are unchanged, so the
    /// projection carries row content; the truthful preparation journal (operation/member/allocation/attempt/
    /// receipt/lease/history) stays OUT and is accounted separately.</summary>
    internal static async Task<string> PublishedAuthorityAsync(string dbPath, string pairId)
    {
        var parts = new List<string>
        {
            "pair=" + await RowsAsync(dbPath, "SELECT configuration_revision, pair_json, selected, state " +
                "FROM sync_pair WHERE pair_id='" + pairId + "';"),
            "config=" + await RowsAsync(dbPath, "SELECT revision, deletion_policy, deletion_grace_seconds, " +
                "required_absence_observations, conflict_policy, metadata_policy, recursive_mode, " +
                "automatic_pruning, recovery_retention_days FROM pair_config_revision WHERE pair_id='" + pairId +
                "' ORDER BY revision;"),
            "roots=" + await RowsAsync(dbPath, "SELECT root_binding_id, side, generation, state, identity_json, " +
                "display_path FROM root_binding WHERE pair_id='" + pairId + "' ORDER BY side, root_binding_id;"),
            "bindings=" + await RowsAsync(dbPath, "SELECT side, control_namespace_id, binding_generation, status " +
                "FROM pair_control_namespace WHERE pair_id='" + pairId + "' ORDER BY side, binding_generation;"),
            "ownership=" + await RowsAsync(dbPath, "SELECT c.control_namespace_id, c.namespace_allocation_id, " +
                "c.side, c.identity_json, c.marker_identity_json, c.owner_marker_hash, c.status FROM control_namespace c " +
                "JOIN namespace_allocation a ON a.namespace_allocation_id=c.namespace_allocation_id " +
                "JOIN operation o ON o.operation_id=a.operation_id WHERE o.pair_id='" + pairId +
                "' ORDER BY c.control_namespace_id;"),
            "recovery=" + await RowsAsync(dbPath, "SELECT recovery_root_binding_id, installation_id, " +
                "provider_account_id, side, display_path, identity_json, generation, status " +
                "FROM recovery_root_binding ORDER BY recovery_root_binding_id;")
        };
        return string.Join(" ;; ", parts);
    }

    /// <summary>Deterministic row rendering for the published projection: every column, in the query's own order.</summary>
    private static async Task<string> RowsAsync(string dbPath, string sql)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            "Data Source=" + dbPath + ";Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var builder = new System.Text.StringBuilder();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            for (var index = 0; index < reader.FieldCount; index++)
                // Typed, deterministic cell rendering: JSON distinguishes NULL from an empty string and escapes
                // any separator that happens to sit inside a value, so columns can never collapse or shift.
                builder.Append(reader.IsDBNull(index) ? "null" : JsonSerializer.Serialize(reader.GetValue(index)))
                    .Append(',');
            builder.Append('\n');
        }
        return builder.ToString();
    }

    private static async Task RefusedProjectionAsync(string name, SyncPair parent, SyncPair child,
        Func<CloudRig, Task> arrange, string because)
    {
        using var rig = await CloudRigAsync(name);
        await arrange(rig);
        var projection = await rig.Host.ProjectRetainedPairEndpointsAsync(parent, default);
        Require(projection.Comparison.EndpointDocument is null,
            "ENP-38 " + because + ": the pair is left unprojected");
        Require(SyncPairPartitionPolicy.Decide(child, projection.Comparison) ==
                SyncPairPartitionRelation.Unverified,
            "ENP-38 " + because + ": the shared decision stays Unverified");
    }

    /// <summary>
    /// The bounded provider double the two real-consumer proofs use. It answers only the three provider
    /// reads the shipped local/cloud binding path performs, for the exact folder set it was configured with;
    /// an unknown folder is refused and no provider effect is ever performed.
    /// </summary>
    private sealed class NestedPartitionDropboxHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _pathByFolderId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _folderIdByPath = new(StringComparer.OrdinalIgnoreCase);

        internal NestedPartitionDropboxHandler Folder(string folderId, string path)
        {
            _pathByFolderId[folderId] = path;
            _folderIdByPath[path] = folderId;
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var route = request.RequestUri!.AbsolutePath;
            if (route.EndsWith("/oauth2/token", StringComparison.Ordinal))
                return Json(new Dictionary<string, object?>
                {
                    ["access_token"] = "nested-partition-access-token",
                    ["token_type"] = "bearer",
                    ["expires_in"] = 14400,
                    ["account_id"] = AccountId
                });
            if (route.EndsWith("/users/get_current_account", StringComparison.Ordinal))
                return Json(new Dictionary<string, object?>
                {
                    ["account_id"] = AccountId,
                    ["root_info"] = new Dictionary<string, object?>
                    {
                        ["root_namespace_id"] = CloudNamespaceId,
                        ["home_namespace_id"] = CloudNamespaceId
                    }
                });
            if (route.EndsWith("/files/get_metadata", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(
                    await request.Content!.ReadAsStringAsync(cancellationToken));
                var requested = body.RootElement.TryGetProperty("path", out var value) ? value.GetString() : null;
                string? folderId = null;
                if (requested is not null && _pathByFolderId.ContainsKey(requested)) folderId = requested;
                else if (requested is not null && _folderIdByPath.TryGetValue(requested, out var byPath))
                    folderId = byPath;
                if (folderId is null) return Status(HttpStatusCode.Conflict, "path/not_found");
                var folderPath = _pathByFolderId[folderId];
                return Json(new Dictionary<string, object?>
                {
                    [".tag"] = "folder",
                    ["name"] = folderPath.Split('/')[^1],
                    ["id"] = folderId,
                    ["path_lower"] = folderPath.ToLowerInvariant(),
                    ["path_display"] = folderPath
                });
            }
            if (route.EndsWith("/files/list_folder", StringComparison.Ordinal) ||
                route.EndsWith("/files/list_folder/continue", StringComparison.Ordinal))
                return Json(new Dictionary<string, object?>
                {
                    ["entries"] = Array.Empty<object>(),
                    ["cursor"] = "nested-partition-cursor",
                    ["has_more"] = false
                });
            return Status(HttpStatusCode.BadRequest, "unsupported_route");
        }

        private static HttpResponseMessage Json(object value) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8,
                    "application/json")
            };

        private static HttpResponseMessage Status(HttpStatusCode code, string tag) =>
            new(code)
            {
                Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["error_summary"] = tag,
                    ["error"] = new Dictionary<string, object?> { [".tag"] = tag }
                }), System.Text.Encoding.UTF8, "application/json")
            };
    }

    private static (SyncPair Parent, SyncPair Child) ParentChildPairs(string nestedName,
        bool excludeControlOwnership, bool includeException, bool excludeChildSubtree = true,
        string? excludedName = null)
    {
        var exclusion = excludedName ?? nestedName;
        var baseLeft = IOPath.Combine(_root, "pure", RootName);
        var baseRight = IOPath.Combine(_root, "pure", RootName + "Mirror");
        var childLeft = IOPath.Combine(baseLeft, nestedName);
        var childRight = IOPath.Combine(baseRight, nestedName);
        var rules = new List<SyncSelectionRule>();
        if (excludeChildSubtree) rules.Add(new SyncSelectionRule(exclusion, false));
        if (includeException) rules.Add(new SyncSelectionRule(nestedName + "/Included", true));
        if (excludeControlOwnership) rules.Add(new SyncSelectionRule(".syncshark-control-child", false));
        var childOwnership = IOPath.Combine(baseLeft, ".syncshark-control-child");
        var childRightOwnership = IOPath.Combine(baseRight, ".syncshark-control-child");
        var parent = LocalPair("pair-parent-" + nestedName, LocalEndpoint(baseLeft, 0x1001),
            LocalEndpoint(baseRight, 0x1002), rules);
        var child = LocalPair("pair-child-" + nestedName, LocalEndpoint(childLeft, 0x2001),
            LocalEndpoint(childRight, 0x2002), [])
            with
            {
                ControlNamespace = new SyncControlNamespaceBinding(Installation, "pair-child-" + nestedName,
                    new SyncLocalControlNamespaceBinding(childOwnership, 0x5000, 0x6001, "marker-1", "hash-1")),
                RightControlNamespace = new SyncControlNamespaceBinding(Installation, "pair-child-right-" + nestedName,
                    new SyncLocalControlNamespaceBinding(childRightOwnership, 0x5000, 0x6002, "marker-2", "hash-2"))
            };
        return (parent, child);
    }

    private static SyncEndpointRecord StripLocalIdentity(SyncEndpointRecord record) => record with
    {
        LocalVolumeSerialNumber = null,
        LocalRootFileId = null
    };

    private static SyncPairEndpoints LocalLocalEndpoints(string nestedName)
    {
        var baseLeft = IOPath.Combine(_root, "pure", RootName);
        var baseRight = IOPath.Combine(_root, "pure", RootName + "Mirror");
        return new SyncPairEndpoints(LocalEndpoint(IOPath.Combine(baseLeft, nestedName), 0x2001),
            LocalEndpoint(IOPath.Combine(baseRight, nestedName), 0x2002), SyncMode.TwoWay);
    }

    private static SyncEndpoint LocalEndpoint(string path, ulong fileId) =>
        SyncEndpoint.Local(path, new SyncLocalEndpointIdentity(0x4321, fileId));

    private static SyncPair LocalPair(string id, SyncEndpoint left, SyncEndpoint right,
        IReadOnlyList<SyncSelectionRule> rules)
    {
        var endpoints = new SyncPairEndpoints(left, right, SyncMode.TwoWay);
        var document = SyncEndpointPersistence.Symmetric(endpoints);
        return new SyncPair(id, id, left.LocalPath!, left.LocalIdentity!.VolumeSerialNumber,
            left.LocalIdentity.RootFileId, right.LocalPath!, right.LocalIdentity!.RootFileId.ToString("x16"),
            SelectionRules: rules, SelectionDefaultIncluded: true, Provider: SyncProviders.Local,
            EndpointSchemaVersion: document.Version, LeftEndpoint: document.Left, RightEndpoint: document.Right);
    }

    // ---------------------------------------------------------------------------------------------
    // real host fixture
    // ---------------------------------------------------------------------------------------------

    private sealed record Fixture(string Root, string DatabasePath, SyncAuthorityDatabase Database,
        SyncAuthorityRuntimeHost Host, PilotInstanceLock Instance) : IDisposable
    {
        public void Dispose()
        {
            Host.Dispose();
            Instance.Dispose();
        }
    }

    private static async Task<Fixture> FixtureAsync(string name)
    {
        var root = IOPath.Combine(_root, name);
        Directory.CreateDirectory(root);
        var state = Directory.CreateDirectory(IOPath.Combine(root, "state")).FullName;
        var instance = PilotInstanceLock.Acquire(IOPath.Combine(state, "instance.lock"));
        var databasePath = IOPath.Combine(root, "authority.db");
        var database = new SyncAuthorityDatabase(databasePath);
        await database.InitializeAsync(Installation, ProfileId, SourceRevision, CapabilityJson);
        // The product aggregate and the authority database describe the same installation.
        // Saving a connection otherwise creates an unrelated default installation identity.
        var product = new SyncProductStateStore(instance.StateAuthority);
        await product.UpdateAsync(current => current with { InstallationId = Installation });
        var connections = new DropboxConnectionStore(instance.StateAuthority);
        var connection = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId, AccountId,
            "ns-nested-431", "ns-nested-431", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes, DisplayName: "Nested partition fixture",
            Email: "nested-partition@example.invalid").Normalize();
        await connections.SaveNewAsync(connection);
        var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, new HttpClient(new NoNetwork()),
            new SyncPairStore(instance.StateAuthority), new SyncRuntimeStateStore(instance.StateAuthority),
            connections, new FixtureCredentials(), database,
            (_, _) => throw new InvalidOperationException("Nested-partition checks never reach a provider."));
        await host.LoadPairsAsync();
        return new Fixture(root, databasePath, database, host, instance);
    }

    private static (string ChildLeft, string ChildRight, string ParentLeft, string ParentRight)
        NestedDirectories(string root)
    {
        var parentLeft = Directory.CreateDirectory(IOPath.Combine(root, "Parent")).FullName;
        var parentRight = Directory.CreateDirectory(IOPath.Combine(root, "ParentMirror")).FullName;
        var childLeft = Directory.CreateDirectory(IOPath.Combine(parentLeft, ChildName)).FullName;
        var childRight = Directory.CreateDirectory(IOPath.Combine(parentRight, ChildName)).FullName;
        return (childLeft, childRight, parentLeft, parentRight);
    }

    private static async Task<SyncPair> BindAsync(Fixture fixture, string left, string right, string name,
        IReadOnlyList<SyncSelectionRule> rules)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Nested-partition binding requires Windows NTFS identities.");
        var draft = DraftFor(left, right, name, rules);
        var result = await fixture.Host.BindEndpointPairAsync(draft);
        return result.Pair;
    }

    private static SyncPair DraftFor(string left, string right, string name,
        IReadOnlyList<SyncSelectionRule> rules)
    {
        var endpoints = new SyncPairEndpoints(LocalEndpointFromLease(left), LocalEndpointFromLease(right),
            SyncMode.TwoWay);
        var template = new SyncPair("template", name, left, 0, 0, right, string.Empty,
            SelectionRules: rules, SelectionDefaultIncluded: true);
        return SyncEndpointPairDraft.Create(endpoints, name, template);
    }

    private static SyncEndpoint LocalEndpointFromLease(string path)
    {
        using var lease = PilotDirectoryLease.Open(path);
        return SyncEndpoint.Local(lease.Path,
            new SyncLocalEndpointIdentity(lease.Identity.VolumeSerialNumber, lease.Identity.FileId));
    }

    private static IReadOnlyList<string> OwnershipRelativeNames(SyncPair pair, string parentLeft,
        string parentRight)
    {
        var names = new List<string>();
        foreach (var owned in new[] { pair.ControlNamespace?.Local.RootPath,
                     pair.RightControlNamespace?.Local.RootPath })
        {
            if (string.IsNullOrWhiteSpace(owned)) continue;
            foreach (var parentRoot in new[] { parentLeft, parentRight })
            {
                if (owned!.StartsWith(parentRoot + IOPath.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    names.Add(PilotPath.NormalizeRelative(owned[(parentRoot.Length + 1)..]));
            }
        }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task<SyncAuthorityBatchItemResult> RunAsync(Fixture fixture, SyncPair pair)
    {
        await fixture.Host.RefreshBootstrapInventoryAsync(pair.Id);
        var approved = await fixture.Host.ApproveBootstrapMergeAsync(pair.Id);
        var batch = await fixture.Host.RunPairsAsync([approved], "Nested partition checks");
        var item = batch.Items.SingleOrDefault(entry => string.Equals(entry.Pair.Id, pair.Id, StringComparison.Ordinal));
        Require(item is not null && item.FailureType is null,
            "the real coordinated run for " + pair.Name + " reported no pair failure: " +
            (item is null ? "missing item" : item.FailureType ?? "no failure"));
        return item!;
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new InvalidOperationException("Nested-partition checks attempted a provider operation.");
    }

    private sealed class FixtureCredentials : IDropboxRefreshTokenStore
    {
        public Task<string?> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("fixture-not-a-credential");
        public Task WriteAsync(string refreshToken, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task DeleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public IDropboxRefreshTokenStore ForConnection(DropboxConnectionBinding binding) => this;
    }

    private static async Task<long> CountAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        SqliteConnection.ClearAllPools();
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        SqliteConnection.ClearAllPools();
        return value is null or DBNull ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>ENP-21: the actual public safe-name validator accepts ordinary component names.</summary>
    private static Task OpaqueSafeNameComponentsAcceptedAsync()
    {
        Require(SyncAuthorityCloudHierarchySnapshot.IsSafeNameComponent("Child"),
            "the safe-name validator accepts an ordinary component name");
        Require(SyncAuthorityCloudHierarchySnapshot.IsSafeNameComponent("Documents"),
            "the safe-name validator accepts another ordinary component name");
        Require(SyncAuthorityCloudHierarchySnapshot.IsSafeNameComponent("Übersicht-2026"),
            "the safe-name validator accepts a Unicode component name");
        return Task.CompletedTask;
    }

    /// <summary>ENP-22: the actual public safe-name validator refuses unsafe component names.</summary>
    private static Task OpaqueUnsafeNameComponentsRefusedAsync()
    {
        Require(!SyncAuthorityCloudHierarchySnapshot.IsSafeNameComponent(" padded"),
            "a leading-space component is not a safe name");
        Require(!SyncAuthorityCloudHierarchySnapshot.IsSafeNameComponent("padded "),
            "a trailing-space component is not a safe name");
        Require(!SyncAuthorityCloudHierarchySnapshot.IsSafeNameComponent("."),
            "a current-directory component is not a safe name");
        Require(!SyncAuthorityCloudHierarchySnapshot.IsSafeNameComponent(".."),
            "a parent-traversal component is not a safe name");
        Require(!SyncAuthorityCloudHierarchySnapshot.IsSafeNameComponent("a/b"),
            "a slash-bearing component is not a safe name");
        Require(!SyncAuthorityCloudHierarchySnapshot.IsSafeNameComponent("a\\b"),
            "a backslash-bearing component is not a safe name");
        Require(!SyncAuthorityCloudHierarchySnapshot.IsSafeNameComponent("name."),
            "a trailing-dot component is not a safe name");
        Require(!SyncAuthorityCloudHierarchySnapshot.IsSafeNameComponent("a\u0001b"),
            "a control-bearing component is not a safe name");
        Require(!SyncAuthorityCloudHierarchySnapshot.IsSafeNameComponent(new string('a', 256)),
            "an over-length component is not a safe name");
        Require(!SyncAuthorityCloudHierarchySnapshot.IsSafeNameComponent("   ") &&
                !SyncAuthorityCloudHierarchySnapshot.IsSafeNameComponent(null),
            "a blank or absent component is not a safe name");
        return Task.CompletedTask;
    }

    /// <summary>
    /// ENP-23: an opaque (non-path-descriptive) provider pair whose hierarchy was never proven keeps the
    /// fail-closed refusal instead of being reported as an independent, distinct pair.
    /// </summary>
    private static Task OpaqueWithoutEvidenceRefusedAsync()
    {
        var candidate = OpaqueCloudPair("opaque candidate", IOPath.Combine(_root, "opaque-candidate"),
            "folder-opaque-one", "/OpaqueOne");
        var retained = OpaqueCloudPair("opaque retained", IOPath.Combine(_root, "opaque-retained"),
            "folder-opaque-two", "/OpaqueTwo");
        Require(SyncPairPartitionPolicy.DecideWithHierarchy(candidate, null, retained, null) ==
                SyncPairPartitionRelation.Unverified,
            "an opaque provider pair with no hierarchy proof stays Unverified rather than being guessed distinct");
        return Task.CompletedTask;
    }

    /// <summary>
    /// One local/opaque-cloud pair built through the real endpoint document. Google Drive is used because it
    /// is the registered provider whose object ids are provider-global and whose paths do not describe
    /// ancestry, so only real hierarchy proof can relate two of its endpoints.
    /// </summary>
    /// <summary>
    /// One opaque parent/child input: the local parent root contains the local Child child root, and the
    /// verified Google chains place the child leaf inside the parent leaf. The display paths are deliberately
    /// unrelated, so only the verified ids and provider-observed names decide the nesting.
    /// </summary>
    private static (SyncPair Parent, SyncPair Child, string ControlName) OpaqueNestedPairs(bool excludeChild,
        bool excludeControl, bool includeException)
    {
        var parentRoot = IOPath.Combine(_root, "opaque-parent-root");
        var childRoot = IOPath.Combine(parentRoot, ChildName);
        var child = OpaqueCloudPair("opaque nested child", childRoot, "opaque-bridge-id", "/UnrelatedChild");
        // SyncEndpointPairDraft.Create deliberately resets ControlNamespace and RightControlNamespace to null,
        // so the child's private control ownership must be attached explicitly with the same shape the
        // existing ParentChildPairs fixture uses. The local sibling binding is what the parent must exclude.
        var childOwnership = IOPath.Combine(parentRoot, ".syncshark-control-child");
        child = child with
        {
            ControlNamespace = new SyncControlNamespaceBinding(Installation, "opaque-child-control",
                new SyncLocalControlNamespaceBinding(childOwnership, 0x5000, 0x6001, "marker-opaque-1",
                    "hash-opaque-1"))
        };
        var controlName = IOPath.GetRelativePath(parentRoot,
            child.ControlNamespace!.Local.RootPath).Replace('\\', '/');
        var rules = new List<SyncSelectionRule>();
        if (excludeChild) rules.Add(new SyncSelectionRule(ChildName, false));
        if (excludeControl) rules.Add(new SyncSelectionRule(controlName, false));
        if (includeException) rules.Add(new SyncSelectionRule(ChildName + "/Included", true));
        var parent = OpaqueCloudPair("opaque nested parent", parentRoot, "opaque-parent-id", "/UnrelatedParent",
            rules);
        return (parent, child, controlName);
    }

    private static readonly IReadOnlyList<string> OpaqueNestedParentChain = [OpaqueCorpusRootId, "opaque-parent-id"];
    private static readonly IReadOnlyList<string> OpaqueNestedChildChain =
        [OpaqueCorpusRootId, "opaque-parent-id", "opaque-bridge-id"];

    private static (SyncPairPartitionPolicy.SyncOpaqueCloudEndpointProof Parent,
        SyncPairPartitionPolicy.SyncOpaqueCloudEndpointProof Child) OpaqueNestedProofs(bool childNames)
    {
        var parentProof = OpaqueProof(OpaqueSnapshot("root-opaque-parent", "account-opaque", "opaque-parent-id",
            OpaqueNestedParentChain, ["Root", "Parent"]), "account-opaque", "root-opaque-parent");
        var childProof = OpaqueProof(OpaqueSnapshot("root-opaque-child", "account-opaque", "opaque-bridge-id",
            OpaqueNestedChildChain, childNames ? ["Root", "Parent", ChildName] : null),
            "account-opaque", "root-opaque-child");
        return (parentProof, childProof);
    }

    /// <summary>ENP-30: the real policy admits the opaque nested partition in BOTH argument orientations.</summary>
    private static Task OpaqueNestedAllowedBothOrientationsAsync()
    {
        var (parent, child, controlName) = OpaqueNestedPairs(excludeChild: true, excludeControl: true,
            includeException: false);
        var (parentProof, childProof) = OpaqueNestedProofs(childNames: true);
        Require(child.ControlNamespace is not null &&
                SyncPairPartitionPolicy.ExcludesWholeSubtree(parent, controlName),
            "the positive fixture really carries the child's local sibling control binding and the parent policy " +
            "really excludes it");
        var forward = SyncPairPartitionPolicy.DecideWithHierarchy(parent, [parentProof], child, [childProof]);
        var reversed = SyncPairPartitionPolicy.DecideWithHierarchy(child, [childProof], parent, [parentProof]);
        Require(forward == SyncPairPartitionRelation.NestedAllowed && reversed == SyncPairPartitionRelation.NestedAllowed,
            "the real policy admits the opaque nested partition in both argument orientations (observed " +
            forward + " then " + reversed + ")");
        return Task.CompletedTask;
    }

    private static Task KnownLocalRefusalWithoutOpaqueProofAsync()
    {
        var (safeParent, safeChild, _) = OpaqueNestedPairs(excludeChild: true, excludeControl: true,
            includeException: false);
        Require(SyncPairPartitionPolicy.Decide(safeParent, safeChild) == SyncPairPartitionRelation.Unverified,
            "ENP-35 a safe local exclusion never establishes the missing cloud ancestry");
        foreach (var fault in new[]
        {
            (Label: "missing data exclusion", ExcludeChild: false, ExcludeControl: true, Exception: false),
            (Label: "included child exception", ExcludeChild: true, ExcludeControl: true, Exception: true),
            (Label: "uncovered private control", ExcludeChild: true, ExcludeControl: false, Exception: false)
        })
        {
            var (parent, child, _) = OpaqueNestedPairs(fault.ExcludeChild, fault.ExcludeControl, fault.Exception);
            Require(SyncPairPartitionPolicy.Decide(parent, child) == SyncPairPartitionRelation.NestedRefused &&
                    SyncPairPartitionPolicy.Decide(child, parent) == SyncPairPartitionRelation.NestedRefused,
                "ENP-35 " + fault.Label + " is already locally unsafe in both orientations despite absent cloud proof");
            var availability = SyncPairPartitionPolicy.ValidateAgainstRetainedPairs(parent, [child],
                refuseUnverified: false);
            Require(!availability.IsValid && availability.Errors.Contains(SyncEndpointError.OverlappingEndpointAcrossPairs),
                "ENP-35 deferring unknown cloud ancestry cannot waive the known " + fault.Label);
        }
        return Task.CompletedTask;
    }

    /// <summary>ENP-31: a proven nested identity without usable observed names stays Unverified.</summary>
    private static Task OpaqueNestedMissingNamesRefusedAsync()
    {
        var (parent, child, _) = OpaqueNestedPairs(excludeChild: true, excludeControl: true, includeException: false);
        var (parentProof, childProof) = OpaqueNestedProofs(childNames: false);
        var relation = SyncPairPartitionPolicy.DecideWithHierarchy(parent, [parentProof], child, [childProof]);
        Require(relation == SyncPairPartitionRelation.Unverified,
            "a nested opaque identity without provider-observed names stays Unverified (observed " + relation + ")");
        return Task.CompletedTask;
    }

    /// <summary>ENP-32 to ENP-34: one altered exclusion fact each, all NestedRefused.</summary>
    private static Task OpaqueNestedOmittedExclusionRefusedAsync() =>
        RequireOpaqueNestedRefusedAsync(excludeChild: false, excludeControl: true, includeException: false,
            "a parent that does not exclude the whole child subtree is NestedRefused");

    private static Task OpaqueNestedIncludedExceptionRefusedAsync() =>
        RequireOpaqueNestedRefusedAsync(excludeChild: true, excludeControl: true, includeException: true,
            "an included exception inside the excluded child subtree is NestedRefused");

    private static Task OpaqueNestedUncoveredControlRefusedAsync() =>
        RequireOpaqueNestedRefusedAsync(excludeChild: true, excludeControl: false, includeException: false,
            "a parent that leaves the child's own private control ownership uncovered is NestedRefused");

    private static Task RequireOpaqueNestedRefusedAsync(bool excludeChild, bool excludeControl, bool includeException,
        string message)
    {
        var (parent, child, _) = OpaqueNestedPairs(excludeChild, excludeControl, includeException);
        var (parentProof, childProof) = OpaqueNestedProofs(childNames: true);
        var relation = SyncPairPartitionPolicy.DecideWithHierarchy(parent, [parentProof], child, [childProof]);
        Require(relation == SyncPairPartitionRelation.NestedRefused, message + " (observed " + relation + ")");
        return Task.CompletedTask;
    }

    private const string OpaqueProviderLiteral = SyncProviders.GoogleDrive;
    private const string OpaqueNamespaceId = "namespace-opaque-431";
    private const string OpaqueCorpusRootId = "corpus-opaque-root";

    /// <summary>
    /// One provider-verified complete chain for the registered opaque provider: every node carries its own
    /// native object key, so the declared NativeOnly coverage is exactly the provider's required coverage.
    /// </summary>
    private static SyncAuthorityCloudHierarchySnapshot OpaqueSnapshot(string rootBindingId, string account,
        string folderId, IReadOnlyList<string> chain, IReadOnlyList<string>? names, bool complete = true) =>
        new(OpaqueProviderLiteral, account, OpaqueNamespaceId, rootBindingId, folderId, chain)
        {
            CompleteToCorpusRoot = complete,
            CorpusRootId = OpaqueCorpusRootId,
            ObjectKeyCoverage = SyncCloudObjectKeyCoverage.NativeOnly,
            ChainNodeKeys = chain.Select(id => new SyncAuthorityCloudHierarchyNodeKeys(id,
                [SyncCloudObjectKey.Native(OpaqueProviderLiteral, id)])).ToArray(),
            ChainFolderNames = names
        };

    private static SyncPairPartitionPolicy.SyncOpaqueCloudEndpointProof OpaqueProof(
        SyncAuthorityCloudHierarchySnapshot snapshot, string account, string rootBindingId) =>
        new(snapshot, account, rootBindingId);

    private static readonly IReadOnlyList<string> OpaqueDistinctChainA = [OpaqueCorpusRootId, "folder-a"];
    private static readonly IReadOnlyList<string> OpaqueDistinctChainB = [OpaqueCorpusRootId, "folder-b"];

    /// <summary>ENP-24: two complete, mutually non-containing opaque chains are Distinct without any names.</summary>
    private static Task OpaqueDistinctWithoutNamesAsync()
    {
        var candidateProof = OpaqueProof(OpaqueSnapshot("root-a", "account-a", "folder-a",
            OpaqueDistinctChainA, names: null), "account-a", "root-a");
        var retainedProof = OpaqueProof(OpaqueSnapshot("root-b", "account-b", "folder-b",
            OpaqueDistinctChainB, names: null), "account-b", "root-b");
        Require(SyncPairPartitionPolicy.DecideWithHierarchy(
                OpaqueCloudPair("opaque distinct candidate", IOPath.Combine(_root, "opaque-distinct-a"),
                    "folder-a", "/OpaqueA"), [candidateProof],
                OpaqueCloudPair("opaque distinct retained", IOPath.Combine(_root, "opaque-distinct-b"),
                    "folder-b", "/OpaqueB"), [retainedProof]) == SyncPairPartitionRelation.Distinct,
            "two complete mutually non-containing opaque chains are Distinct without observed names");
        return Task.CompletedTask;
    }

    /// <summary>ENP-25: one provider-global object is the same object across account and root routes.</summary>
    private static Task OpaqueSameAcrossRoutesAsync()
    {
        var candidateProof = OpaqueProof(OpaqueSnapshot("root-one", "account-one", "folder-shared",
            [OpaqueCorpusRootId, "folder-shared"], names: null), "account-one", "root-one");
        var retainedProof = OpaqueProof(OpaqueSnapshot("root-two", "account-two", "folder-shared",
            [OpaqueCorpusRootId, "folder-shared"], names: null), "account-two", "root-two");
        Require(SyncPairPartitionPolicy.DecideWithHierarchy(
                OpaqueCloudPair("opaque same candidate", IOPath.Combine(_root, "opaque-same-a"),
                    "folder-shared", "/Shared"), [candidateProof],
                OpaqueCloudPair("opaque same retained", IOPath.Combine(_root, "opaque-same-b"),
                    "folder-shared", "/Shared"), [retainedProof]) == SyncPairPartitionRelation.Same,
            "a provider-global folder identity is Same whichever account, namespace or root reaches it");
        return Task.CompletedTask;
    }

    /// <summary>
    /// ENP-26 to ENP-29: every malformed, misbound, incomplete or ambiguous proof stays refused. Each case
    /// keeps the same nested-shaped input and changes exactly one proof fact, so the refusal is attributable.
    /// </summary>
    private static Task OpaqueWrongAccountRefusedAsync()
    {
        var account = OpaqueSnapshot("root-a", "account-a", "folder-a", OpaqueDistinctChainA, names: null);
        return RequireOpaqueRelationAsync(account, "account-DIFFERENT", "root-a",
            OpaqueSnapshot("root-b", "account-b", "folder-b", OpaqueDistinctChainB, names: null),
            "account-b", "root-b",
            "a proof whose independently held account does not name the snapshot's account is refused");
    }

    private static Task OpaqueIncompleteChainRefusedAsync()
    {
        return RequireOpaqueRelationAsync(
            OpaqueSnapshot("root-a", "account-a", "folder-a", OpaqueDistinctChainA, names: null, complete: false),
            "account-a", "root-a",
            OpaqueSnapshot("root-b", "account-b", "folder-b", OpaqueDistinctChainB, names: null),
            "account-b", "root-b",
            "a chain that is not complete to its corpus root is refused");
    }

    private static Task OpaqueAmbiguousProofRefusedAsync()
    {
        var snapshot = OpaqueSnapshot("root-a", "account-a", "folder-a", OpaqueDistinctChainA, names: null);
        var candidateProof = OpaqueProof(snapshot, "account-a", "root-a");
        var retainedProof = OpaqueProof(OpaqueSnapshot("root-b", "account-b", "folder-b",
            OpaqueDistinctChainB, names: null), "account-b", "root-b");
        Require(SyncPairPartitionPolicy.DecideWithHierarchy(
                OpaqueCloudPair("opaque ambiguous candidate", IOPath.Combine(_root, "opaque-ambiguous-a"),
                    "folder-a", "/OpaqueA"), [candidateProof, candidateProof],
                OpaqueCloudPair("opaque ambiguous retained", IOPath.Combine(_root, "opaque-ambiguous-b"),
                    "folder-b", "/OpaqueB"), [retainedProof]) == SyncPairPartitionRelation.Unverified,
            "two matching proofs let no proof be chosen, so the ambiguous relation stays refused");
        return Task.CompletedTask;
    }

    private static Task OpaqueMisboundIntersectingKeyRefusedAsync()
    {
        // The snapshot's own bound root is root-a, but the proof claims held root-binding root-DIFFERENT, and
        // the candidate chain's ancestor key names the retained root. The binding gate must refuse before the
        // comparator may treat that intersecting ancestor key as containment.
        var candidateSnapshot = OpaqueSnapshot("root-a", "account-a", "folder-b",
            [OpaqueCorpusRootId, "folder-a", "folder-b"], names: null);
        return RequireOpaqueRelationAsync(candidateSnapshot, "account-a", "root-DIFFERENT",
            OpaqueSnapshot("root-b", "account-b", "folder-a",
                [OpaqueCorpusRootId, "folder-a"], names: null), "account-b", "root-b",
            "a misbound snapshot whose ancestor key intersects the other root is refused, never Nested");
    }

    private static Task RequireOpaqueRelationAsync(SyncAuthorityCloudHierarchySnapshot candidateSnapshot,
        string candidateAccount, string candidateRootBinding, SyncAuthorityCloudHierarchySnapshot retainedSnapshot,
        string retainedAccount, string retainedRootBinding, string message)
    {
        var candidate = OpaqueCloudPair("opaque refusal candidate",
            IOPath.Combine(_root, "opaque-refusal-" + candidateSnapshot.FolderId), candidateSnapshot.FolderId,
            "/" + candidateSnapshot.FolderId);
        var retained = OpaqueCloudPair("opaque refusal retained",
            IOPath.Combine(_root, "opaque-refusal-" + retainedSnapshot.FolderId), retainedSnapshot.FolderId,
            "/" + retainedSnapshot.FolderId);
        var relation = SyncPairPartitionPolicy.DecideWithHierarchy(candidate,
            [OpaqueProof(candidateSnapshot, candidateAccount, candidateRootBinding)], retained,
            [OpaqueProof(retainedSnapshot, retainedAccount, retainedRootBinding)]);
        Require(relation != SyncPairPartitionRelation.Distinct && relation != SyncPairPartitionRelation.NestedAllowed,
            message + " (observed " + relation + ")");
        return Task.CompletedTask;
    }

    private static SyncPair OpaqueCloudPair(string name, string localPath, string folderId, string folderPath,
        IReadOnlyList<SyncSelectionRule>? rules = null)
    {
        const string opaqueConnection = "conn-opaque-431";
        const string opaqueNamespace = "namespace-opaque-431";
        Directory.CreateDirectory(localPath);
        using var lease = PilotDirectoryLease.Open(localPath);
        var local = SyncEndpoint.Local(lease.Path,
            new SyncLocalEndpointIdentity(lease.Identity.VolumeSerialNumber, lease.Identity.FileId));
        var cloud = SyncEndpoint.Cloud(SyncEndpointKind.GoogleDrive, opaqueConnection, opaqueNamespace,
            folderId, folderPath);
        var endpoints = new SyncPairEndpoints(local, cloud, SyncMode.TwoWay);
        var template = new SyncPair("template", name, lease.Path, lease.Identity.VolumeSerialNumber,
            lease.Identity.FileId, folderPath, folderId, SelectionRules: rules ?? [],
            SelectionDefaultIncluded: true);
        return SyncEndpointPairDraft.Create(endpoints, name, template);
    }

    private static async Task CaseAsync(string id, Func<Task> body)
    {
        var entry = new Dictionary<string, object?> { ["id"] = id, ["state"] = "running" };
        Cases.Add(entry);
        try
        {
            await body();
            entry["state"] = "observed";
        }
        catch (Exception error)
        {
            entry["state"] = "failed";
            entry["error"] = error.GetType().Name + ": " + error.Message;
            throw;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
        _assertions++;
        Cases[^1]["observed"] = (Cases[^1].TryGetValue("observed", out var prior) && prior is string text
            ? text + " | " : string.Empty) + message;
        Console.WriteLine("PASS " + message);
    }
}

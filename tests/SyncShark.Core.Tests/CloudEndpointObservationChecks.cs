using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// CC2C focused proofs over real isolated SQLite authority state and the actual observation coordinator.
//
// Every assertion below reads a database this run created under the supplied fixture root, using the real
// SyncAuthorityDatabase, the real typed cloud endpoint set and the real SyncAuthorityObservationCoordinator.
// The provider side is a synthetic ISyncRemoteStore input; no live provider, credential, transport or
// installed database is reachable, and the fixture store records every call so an unexpected provider effect
// fails the run. Nothing here claims effect dispatch, a real transport or Host completion.
internal static class CloudEndpointObservationChecks
{
    private const string InstallationId = "installation-cloud-endpoint-316";
    private const string SourceRevision = "cloud-endpoint-316";
    private static readonly List<Dictionary<string, object?>> Cases = [];
    private static int _assertions;
    private static string _root = "";

    internal static async Task RunAsync(string root)
    {
        _root = Path.GetFullPath(root);
        if (Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any())
            throw new InvalidOperationException("Use a fresh isolated cloud endpoint observation fixture root.");
        Directory.CreateDirectory(_root);
        Cases.Clear();
        _assertions = 0;

        await ConcurrentReadLifetimeAsync();
        await ProviderPairingsAsync();
        await IdentityRejectionsAsync();
        await HierarchyRejectionsAsync();
        await DuplicateRootRejectionsAsync();
        await PartialListingAndDriftAsync();
        await RoutingFencesAsync();
        await SharedObjectOverlapAsync();
        await FailedSaveClosureAsync();

        var report = new Dictionary<string, object?>
        {
            ["generated_by"] = "CloudEndpointObservationChecks (tests/SyncShark.Core.Tests)",
            ["fixture_root"] = _root,
            ["assertions"] = _assertions,
            ["cases"] = Cases
        };
        await File.WriteAllTextAsync(Path.Combine(_root, "cloud-endpoint-observation-checks.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS cloud endpoint observation: " + _assertions +
            " assertions; four ordered provider pairings through real SQLite and the actual coordinator," +
            " stable native identities without cross-side aliasing, protected and excluded path evidence," +
            " typed cloud root rejection and fail-closed legacy routing.");
    }


    internal static async Task RunFailedSaveClosureAsync(string root)
    {
        _root=Path.GetFullPath(root);Directory.CreateDirectory(_root);
        await FailedSaveClosureAsync();
    }

    private static async Task FailedSaveClosureAsync()
    {
        const string name="failed-save";
        var authority=await OpenAuthorityAsync(name);
        var path=DatabasePath(name);
        var left=Dropbox("closure-left","id:closure-left");
        var right=Drive("closure-right","closure-right");
        await RegisterAccountAsync(authority,left);await RegisterAccountAsync(authority,right);
        var pair=await RetainPairAsync(authority,CreatePair(left,right,"Failed cloud inventory"));
        const string leftRoot="closure-left-root",rightRoot="closure-right-root";
        await RegisterCloudRootAsync(authority,pair,left,leftRoot,SyncAuthorityCloudSides.LogicalLeft,
            new[]{CorpusChain(left,leftRoot)});
        await RegisterCloudRootAsync(authority,pair,right,rightRoot,SyncAuthorityCloudSides.LogicalRight,
            new[]{CorpusChain(left,leftRoot),CorpusChain(right,rightRoot)});
        var leftStore=CreateStore(SideEntries("closure-left","left.txt"));
        var rightStore=CreateStore(SideEntries("closure-right","right.txt"));
        var set=SyncAuthorityCloudEndpointSet.FromPair(pair,leftStore.Store,rightStore.Store,leftRoot,rightRoot,
            left.ProviderAccountId,right.ProviderAccountId);
        Task Fail(SyncComparisonProgress p)=>p.Phase=="Committing the completed inventory snapshots"
            ?Task.FromException(new IOException("fixture cloud publication failure")):Task.CompletedTask;
        var failed=false;
        try{await new SyncAuthorityObservationCoordinator(pair,leftStore.Store,authority,leftRoot,rightRoot,
            Fail,set).ObserveAsync("initial");}catch(IOException){failed=true;}
        Require(failed && await ScalarAsync(path,"SELECT count(*) FROM scan_generation WHERE status='failed' AND ended_utc IS NOT NULL;")==2 &&
            await ScalarAsync(path,"SELECT count(*) FROM scan_generation WHERE status='complete';")==0 &&
            await ScalarAsync(path,"SELECT count(*) FROM observation_cut WHERE state='sealed';")==2,
            "typed cloud failure closes both sealed candidates without promoting either to complete");
        await new SyncAuthorityObservationCoordinator(pair,leftStore.Store,authority,leftRoot,rightRoot,
            cloudEndpoints:set).ObserveAsync("manual");
        Require(await ScalarAsync(path,"SELECT count(*) FROM scan_generation WHERE status='complete';")==2 &&
            await ScalarAsync(path,"SELECT count(*) FROM scan_generation WHERE status='failed';")==2,
            "typed cloud retry uses fresh complete cuts while abandoned generations remain failed");
        await authority.VerifyHealthAsync();
    }

    // ----- fixtures ------------------------------------------------------------------------------

    private sealed record EndpointSpec(SyncEndpointKind Kind, string Provider, string ConnectionId,
        string AccountId, string RootNamespaceId, string HomeNamespaceId, string EndpointNamespaceId,
        string ProviderAccountId, string FolderId, string FolderPath);

    private static EndpointSpec Dropbox(string index, string folderId, string? folderPath = null) => new(
        SyncEndpointKind.Dropbox, SyncProviders.Dropbox, "conn-d" + index, "dbid-" + index, "ns-root-" + index,
        "ns-home-" + index, "ns-root-" + index,
        SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.Dropbox, "dbid-" + index,
            "ns-root-" + index, "ns-home-" + index),
        folderId, folderPath ?? "/Dropbox " + index);

    private static EndpointSpec Drive(string index, string folderId, string? folderPath = null) => new(
        SyncEndpointKind.GoogleDrive, SyncProviders.GoogleDrive, "conn-g" + index, "perm-" + index,
        "key-" + index, "key-" + index, "perm-" + index,
        SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.GoogleDrive, "perm-" + index,
            "key-" + index, "key-" + index),
        folderId, folderPath ?? "/Drive " + index);

    private static SyncEndpoint Endpoint(EndpointSpec spec) => SyncEndpoint.Cloud(spec.Kind, spec.ConnectionId,
        spec.EndpointNamespaceId, spec.FolderId, spec.FolderPath);

    private static SyncPair CreatePair(EndpointSpec left, EndpointSpec right, string name) =>
        SyncCloudPairConfiguration.CreateDraft(new SyncPairEndpoints(Endpoint(left), Endpoint(right),
            SyncMode.TwoWay), name);

    private static async Task<SyncAuthorityDatabase> OpenAuthorityAsync(string caseName)
    {
        var directory = Path.Combine(_root, caseName);
        Directory.CreateDirectory(directory);
        var authority = new SyncAuthorityDatabase(Path.Combine(directory, "syncshark.db"));
        await authority.InitializeAsync(InstallationId, "profile-cloud-endpoint-316", SourceRevision, "{}");
        return authority;
    }

    private static string DatabasePath(string caseName) =>
        Path.Combine(_root, caseName, "syncshark.db");

    private static async Task RegisterAccountAsync(SyncAuthorityDatabase authority, EndpointSpec spec)
    {
        await authority.RegisterProviderAccountAsync(spec.ProviderAccountId, InstallationId, spec.AccountId,
            spec.RootNamespaceId, spec.HomeNamespaceId, spec.Provider + " account", 1,
            cancellationToken: default, provider: spec.Provider);
    }

    private static async Task<SyncPair> RetainPairAsync(SyncAuthorityDatabase authority, SyncPair pair)
    {
        var normalized = SyncPairStore.NormalizePair(pair);
        await authority.RegisterPairAsync(normalized.Id, normalized.PolicyRevision);
        await authority.RetainPairDefinitionAsync(normalized, true);
        return normalized;
    }

    private static async Task RegisterCloudRootAsync(SyncAuthorityDatabase authority, SyncPair pair,
        EndpointSpec spec, string rootBindingId, string side,
        IReadOnlyList<SyncAuthorityCloudHierarchySnapshot>? hierarchies = null)
    {
        await authority.RegisterCloudEndpointRootBindingAsync(rootBindingId, pair.Id, side, 1, InstallationId,
            spec.Provider, spec.ProviderAccountId, spec.EndpointNamespaceId, spec.FolderId, spec.FolderPath,
            cancellationToken: default, hierarchies: hierarchies);
    }

    private static async Task RegisterCloudRootAtPathAsync(SyncAuthorityDatabase authority, SyncPair pair,
        EndpointSpec spec, string rootBindingId, string side, string displayPath,
        IReadOnlyList<SyncAuthorityCloudHierarchySnapshot>? hierarchies)
    {
        await authority.RegisterCloudEndpointRootBindingAsync(rootBindingId, pair.Id, side, 1, InstallationId,
            spec.Provider, spec.ProviderAccountId, spec.EndpointNamespaceId, spec.FolderId, displayPath,
            cancellationToken: default, hierarchies: hierarchies);
    }

    // One complete provider-issued chain for one fixture root. The verified corpus root is the existing
    // synthetic namespace identity for a path-descriptive provider, and a distinct opaque id for a
    // path-opaque provider; the chain carries that corpus root and the selected root, and it asserts its own
    // completeness because the shared comparison no longer treats an unasserted chain as ancestry evidence.
    // One complete provider-issued chain for one fixture root. CC2G adds the object-key evidence the shared
    // model requires: an ordinary (non-mount) folder carries exactly its own native key, and the readback
    // declares the coverage its provider needs, so a complete fixture chain stays usable as disjointness
    // evidence instead of falling to Unverified.
    private static SyncAuthorityCloudHierarchySnapshot CorpusChain(EndpointSpec spec, string rootBindingId) =>
        new(spec.Provider, spec.ProviderAccountId, spec.EndpointNamespaceId, rootBindingId, spec.FolderId,
            new[] { CorpusRoot(spec), spec.FolderId })
        {
            CompleteToCorpusRoot = true,
            CorpusRootId = CorpusRoot(spec),
            ChainNodeKeys = new[]
            {
                NodeKeys(spec.Provider, CorpusRoot(spec)),
                NodeKeys(spec.Provider, spec.FolderId)
            },
            ObjectKeyCoverage = SyncCloudObjectKeySemantics.RequiredCoverage(spec.Provider)
        };

    private static SyncAuthorityCloudHierarchyNodeKeys NodeKeys(string provider, string folderId) =>
        new(folderId, new[] { SyncCloudObjectKey.Native(provider, folderId) });

    private static string CorpusRoot(EndpointSpec spec) =>
        SyncEndpointProviders.Get(spec.Kind).PathsDescribeAncestry
            ? PilotPath.DropboxNamespaceRootIdentity(spec.RootNamespaceId)
            : "corpus-" + spec.FolderId;

    // Disjoint complete chains for two roots of one provider: neither chain contains the other's root.
    private static IReadOnlyList<SyncAuthorityCloudHierarchySnapshot> DisjointChains(EndpointSpec candidate,
        string candidateRoot, EndpointSpec retained, string retainedRoot) =>
        new[] { CorpusChain(candidate, candidateRoot), CorpusChain(retained, retainedRoot) };

    private static IReadOnlyList<PilotRemoteEntry> SideEntries(string sharedPrefix, string ownPath)
    {
        var sharedHash = new string('a', 64);
        return new[]
        {
            new PilotRemoteEntry("shared.txt", PilotEntryKind.File, sharedPrefix + "-shared", "rev-1", 7,
                sharedHash, DateTimeOffset.UnixEpoch),
            new PilotRemoteEntry(ownPath, PilotEntryKind.File, sharedPrefix + "-own", "rev-1", 3,
                new string('b', 64), DateTimeOffset.UnixEpoch),
            new PilotRemoteEntry("native.doc", PilotEntryKind.File, sharedPrefix + "-native", "rev-1", 0, null,
                DateTimeOffset.UnixEpoch, "This provider item is not a downloadable byte file."),
            new PilotRemoteEntry("excluded/skip.txt", PilotEntryKind.File, sharedPrefix + "-skip", "rev-1", 1,
                new string('c', 64), DateTimeOffset.UnixEpoch)
        };
    }

    public class FixtureStore : DispatchProxy
    {
        private IReadOnlyList<PilotRemoteEntry> _entries = Array.Empty<PilotRemoteEntry>();
        internal int VerifyCalls;
        internal int ListCalls;
        internal int UnexpectedCalls;
        internal bool ThrowOnList;
        internal int ThrowOnVerifyCall = -1;
        internal Func<CancellationToken, Task>? BeforeList;

        internal IReadOnlyList<PilotRemoteEntry> Entries
        {
            get => _entries;
            set => _entries = value;
        }

        internal ISyncRemoteStore Store => (ISyncRemoteStore)(object)this;

        protected override object? Invoke(MethodInfo? method, object?[]? arguments) => method?.Name switch
        {
            nameof(ISyncRemoteStore.VerifyRootIdentityAsync) => Verify(),
            nameof(ISyncRemoteStore.ListEntriesAsync) => ListAsync(
                arguments?.OfType<CancellationToken>().LastOrDefault() ?? default),
            _ => Unexpected(method)
        };

        private object Verify()
        {
            VerifyCalls++;
            if (ThrowOnVerifyCall == VerifyCalls)
                throw new IOException("The provider root changed while it was being listed.");
            return Task.CompletedTask;
        }

        private async Task<IReadOnlyList<PilotRemoteEntry>> ListAsync(CancellationToken cancellationToken)
        {
            ListCalls++;
            if (BeforeList is not null) await BeforeList(cancellationToken);
            if (ThrowOnList) throw new IOException("The provider listing was partial.");
            return _entries;
        }

        private object Unexpected(MethodInfo? method)
        {
            UnexpectedCalls++;
            throw new InvalidOperationException(
                "Unexpected provider effect in a cloud observation fixture: " + method?.Name);
        }
    }

    private static FixtureStore CreateStore(IReadOnlyList<PilotRemoteEntry> entries)
    {
        var store = (FixtureStore)(object)DispatchProxy.Create<ISyncRemoteStore, FixtureStore>();
        store.Entries = entries;
        return store;
    }

    // ----- 1. the four ordered provider pairings --------------------------------------------------

    private static async Task ProviderPairingsAsync()
    {
        var orderings = new (string Name, EndpointSpec Left, EndpointSpec Right)[]
        {
            ("dropbox-dropbox", Dropbox("1", "id:left-dd"), Dropbox("2", "id:right-dd")),
            ("dropbox-google", Dropbox("3", "id:left-dg"), Drive("4", "gd-right-dg")),
            ("google-dropbox", Drive("5", "gd-left-gd"), Dropbox("6", "id:right-gd")),
            ("google-google", Drive("7", "gd-left-gg"), Drive("8", "gd-right-gg"))
        };
        foreach (var ordering in orderings)
            await PairingAsync(ordering.Name, ordering.Left, ordering.Right);
    }

    private static async Task PairingAsync(string name, EndpointSpec leftSpec, EndpointSpec rightSpec)
    {
        Claim("CC2C-4/" + name, "Real SQLite observation through the actual coordinator for " + name);
        var authority = await OpenAuthorityAsync("pairing-" + name);
        var databasePath = DatabasePath("pairing-" + name);
        await RegisterAccountAsync(authority, leftSpec);
        await RegisterAccountAsync(authority, rightSpec);
        var pair = await RetainPairAsync(authority, CreatePair(leftSpec, rightSpec, "CC2C " + name)
            with { SelectionRules = new[] { new SyncSelectionRule("excluded", false) } });
        var leftRoot = "root-left-" + name;
        var rightRoot = "root-right-" + name;
        await RegisterCloudRootAsync(authority, pair, leftSpec, leftRoot, SyncAuthorityCloudSides.LogicalLeft,
            new[] { CorpusChain(leftSpec, leftRoot) });
        await RegisterCloudRootAsync(authority, pair, rightSpec, rightRoot, SyncAuthorityCloudSides.LogicalRight,
            new[] { CorpusChain(rightSpec, rightRoot), CorpusChain(leftSpec, leftRoot) });

        var leftStore = CreateStore(SideEntries("left-" + name, "left-only.txt"));
        var rightStore = CreateStore(SideEntries("right-" + name, "right-only.txt"));
        var leftStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rightStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        leftStore.BeforeList = async token =>
        {
            leftStarted.TrySetResult();
            await rightStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        };
        rightStore.BeforeList = async token =>
        {
            rightStarted.TrySetResult();
            await leftStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        };
        var set = SyncAuthorityCloudEndpointSet.FromPair(pair, leftStore.Store, rightStore.Store, leftRoot,
            rightRoot, leftSpec.ProviderAccountId, rightSpec.ProviderAccountId);
        Require(set.Left.CompatibilitySide == SyncAuthorityCloudSides.LogicalLeft &&
                set.Right.CompatibilitySide == SyncAuthorityCloudSides.LogicalRight,
            name + " maps logical left to the left port and logical right to the right port");
        Require(set.Left.Provider == leftSpec.Provider && set.Right.Provider == rightSpec.Provider,
            name + " binds each port to its actual registered provider");
        var feedbackActive = 0;
        var feedbackPaths = new List<string>();
        async Task Feedback(SyncComparisonProgress progress)
        {
            if (Interlocked.Increment(ref feedbackActive) != 1)
                throw new InvalidDataException("Concurrent endpoints overlapped an inventory callback.");
            try
            {
                await Task.Yield();
                feedbackPaths.Add(progress.RelativePath);
            }
            finally { Interlocked.Decrement(ref feedbackActive); }
        }
        var coordinator = new SyncAuthorityObservationCoordinator(pair, leftStore.Store, authority, leftRoot,
            rightRoot, onInventory: Feedback, cloudEndpoints: set);
        Require(coordinator.UsesCloudEndpointRouting, name + " routes to the typed cloud component");
        await coordinator.ObserveAsync("initial");
        Require(leftStarted.Task.IsCompletedSuccessfully && rightStarted.Task.IsCompletedSuccessfully,
            name + " enters both real endpoint listings before either may finish");
        Require(feedbackPaths.Contains(leftSpec.FolderPath) && feedbackPaths.Contains(rightSpec.FolderPath),
            name + " retains serialized detailed progress for both endpoints");

        var left = await authority.LoadLatestCompleteObservationAsync(pair.Id,
            SyncAuthorityCloudSides.LogicalLeft);
        var right = await authority.LoadLatestCompleteObservationAsync(pair.Id,
            SyncAuthorityCloudSides.LogicalRight);
        Require(left.Count == 2 && right.Count == 2,
            name + " retains one byte observation per ordinary file on each side");
        Require(left.All(item => item.RelativePath != "native.doc") &&
                right.All(item => item.RelativePath != "native.doc"),
            name + " never hands a provider-native item to the byte planner");
        var sharedLeft = left.Single(item => item.RelativePath == "shared.txt");
        var sharedRight = right.Single(item => item.RelativePath == "shared.txt");
        Require(sharedLeft.ObjectId == sharedRight.ObjectId,
            name + " retains one logical object for the same path on both sides");
        Require(sharedLeft.ContentHash == sharedRight.ContentHash && sharedLeft.Size == sharedRight.Size,
            name + " keeps each side's native content identity and size exactly as the provider reported them");
        var leftOnly = left.Single(item => item.RelativePath == "left-only.txt");
        var rightOnly = right.Single(item => item.RelativePath == "right-only.txt");
        // CC2G: the CC2C fixture used one identical same-provider file id on both sides; that input is a real
        // shared backing object and is now refused before retention (see SharedObjectOverlapAsync below), so
        // the positive pairing uses two genuinely distinct provider file ids.
        Require(leftOnly.ObjectId != rightOnly.ObjectId,
            name + " keeps two genuinely distinct provider file ids on the two sides as two objects");
        Require(leftOnly.ObjectVersionId != rightOnly.ObjectVersionId,
            name + " keeps the two sides' own versions distinct");

        var leftUncertain = await authority.LoadLatestCompleteUncertainPathsAsync(pair.Id,
            SyncAuthorityCloudSides.LogicalLeft);
        Require(leftUncertain.SetEquals(new[] { "native.doc" }),
            name + " retains a provider-native item as explicit present-but-protected evidence");
        Require(!leftUncertain.Contains("excluded/skip.txt"),
            name + " keeps an excluded path out of the uncertainty set");
        Require(await ScalarAsync(databasePath,
                "SELECT count(*) FROM observation o JOIN scan_generation g ON g.scan_generation_id=o.scan_generation_id " +
                "WHERE g.pair_id=$pair AND g.side='local' AND json_extract(o.evidence_json,'$.state')='selective_sync_excluded';",
                ("$pair", pair.Id)) == 1,
            name + " retains the excluded path as explicit evidence on the left side");
        Require(await ScalarAsync(databasePath,
                "SELECT count(*) FROM observation o JOIN scan_generation g ON g.scan_generation_id=o.scan_generation_id " +
                "WHERE g.pair_id=$pair AND g.side='dropbox' AND json_extract(o.evidence_json,'$.state')='selective_sync_excluded';",
                ("$pair", pair.Id)) == 1,
            name + " protects the same excluded path on the right side");
        Require(await ScalarAsync(databasePath,
                "SELECT count(*) FROM sync_path_index WHERE pair_id=$pair AND relative_path_key='shared.txt' " +
                "AND local_present=1 AND dropbox_present=1 AND online_only=0 AND local_stable_identity IS NULL;",
                ("$pair", pair.Id)) == 1,
            name + " records logical presence on both sides without a device-residency claim");

        await coordinator.ObserveAsync("initial");
        var leftAgain = await authority.LoadLatestCompleteObservationAsync(pair.Id,
            SyncAuthorityCloudSides.LogicalLeft);
        var sharedLeftAgain = leftAgain.Single(item => item.RelativePath == "shared.txt");
        var leftOnlyAgain = leftAgain.Single(item => item.RelativePath == "left-only.txt");
        Require(sharedLeftAgain.ObjectId == sharedLeft.ObjectId &&
                sharedLeftAgain.ObjectVersionId == sharedLeft.ObjectVersionId,
            name + " keeps the same object and own version identity across an unchanged repeat");
        Require(leftOnlyAgain.ObjectId == leftOnly.ObjectId,
            name + " keeps the same object for one genuine file id across a repeat instead of aliasing the other side");
        Require(leftStore.VerifyCalls == 4 && rightStore.VerifyCalls == 4 && leftStore.ListCalls == 2 &&
                rightStore.ListCalls == 2,
            name + " verifies each root before and after every listing");
        Require(leftStore.UnexpectedCalls == 0 && rightStore.UnexpectedCalls == 0,
            name + " performs no provider effect beyond root verification and listing");

        var planner = new SyncAuthorityInitialPlanner(authority,
            new SyncAuthorityProviderAwareContentComparison(leftSpec.Provider, rightSpec.Provider));
        var decisions = await planner.AdoptAsync(pair.Id);
        var sharedDecision = decisions.SingleOrDefault(item => item.RelativePath == "shared.txt");
        Require(sharedDecision is not null, name + " initial planner consumes the actual cloud scans");
        var sameAlgorithm = string.Equals(SyncContentIdentity.AlgorithmFor(leftSpec.Provider),
            SyncContentIdentity.AlgorithmFor(rightSpec.Provider), StringComparison.Ordinal);
        if (sameAlgorithm)
            Require(sharedDecision!.Kind == SyncDecisionKind.Baseline,
                name + " adopts unchanged same-algorithm content without copying either side");
        else
            Require(sharedDecision!.Kind == SyncDecisionKind.Blocked,
                name + " refuses to treat incomparable native algorithms as equality");
        Require(decisions.Any(item => item.RelativePath == "native.doc" && item.Kind == SyncDecisionKind.Blocked),
            name + " blocks the provider-native path instead of adopting absence");
        Require(pair.LocalRoot.Length == 0 && pair.LocalVolumeSerialNumber == 0 && pair.LocalRootFileId == 0,
            name + " carries no selected NTFS root");
        Require(!Directory.EnumerateDirectories(Path.Combine(_root, "pairing-" + name)).Any(),
            name + " materialises no local folder for the pair");
    }

    // ----- 2. identity rejection ---------------------------------------------------------------

    private static async Task IdentityRejectionsAsync()
    {
        Claim("CC2C-4/identity-rejections",
            "Spoofed, mixed, missing and mismatched cloud identities are refused without a write");
        var authority = await OpenAuthorityAsync("identity-rejections");
        var databasePath = DatabasePath("identity-rejections");
        var spec = Dropbox("20", "id:left-identity");
        await RegisterAccountAsync(authority, spec);
        var pair = await RetainPairAsync(authority, CreatePair(spec, Dropbox("21", "id:right-identity"),
            "CC2C identity"));
        await RegisterCloudRootAsync(authority, pair, spec, "root-left-identity",
            SyncAuthorityCloudSides.LogicalLeft);
        var scan = await authority.BeginScanGenerationAsync(pair.Id, "root-left-identity",
            SyncAuthorityCloudSides.LogicalLeft, "initial", "{\"scope\":\"complete\"}");
        var provider = spec.Provider;
        var account = spec.ProviderAccountId;
        var ns = spec.EndpointNamespaceId;
        var algorithm = SyncContentIdentity.AlgorithmFor(provider);
        await RejectAppendAsync(authority, databasePath, scan, "a spoofed provider account",
            Entry("a.txt", Cloud(provider, "another-account", ns, "f1", algorithm)));
        await RejectAppendAsync(authority, databasePath, scan, "a spoofed native namespace",
            Entry("b.txt", Cloud(provider, account, "ns-other", "f2", algorithm)));
        await RejectAppendAsync(authority, databasePath, scan, "a spoofed provider",
            Entry("c.txt", Cloud(SyncProviders.GoogleDrive, account, ns, "f3",
                SyncContentIdentity.AlgorithmFor(SyncProviders.GoogleDrive))));
        await RejectAppendAsync(authority, databasePath, scan, "an unregistered provider",
            Entry("d.txt", Cloud("not-a-registered-provider", account, ns, "f4", algorithm)));
        await RejectAppendAsync(authority, databasePath, scan, "an unsupported identity kind",
            "{\"identityKind\":\"cloud-v9\",\"provider\":\"" + provider + "\",\"providerAccountId\":\"" +
            account + "\",\"namespaceId\":\"" + ns + "\",\"fileId\":\"f5\",\"contentAlgorithm\":\"" +
            algorithm + "\"}");
        await RejectAppendAsync(authority, databasePath, scan, "a missing file identity",
            "{\"identityKind\":\"cloud\",\"provider\":\"" + provider + "\",\"providerAccountId\":\"" +
            account + "\",\"namespaceId\":\"" + ns + "\",\"contentAlgorithm\":\"" + algorithm + "\"}");
        await RejectAppendAsync(authority, databasePath, scan, "a missing content algorithm",
            "{\"identityKind\":\"cloud\",\"provider\":\"" + provider + "\",\"providerAccountId\":\"" +
            account + "\",\"namespaceId\":\"" + ns + "\",\"fileId\":\"f6\"}");
        await RejectAppendAsync(authority, databasePath, scan, "a mixed local member",
            "{\"identityKind\":\"cloud\",\"provider\":\"" + provider + "\",\"providerAccountId\":\"" +
            account + "\",\"namespaceId\":\"" + ns + "\",\"fileId\":\"f7\",\"volumeSerial\":\"1234\"," +
            "\"contentAlgorithm\":\"" + algorithm + "\"}");
        await RejectAppendAsync(authority, databasePath, scan, "a legacy identity on a cloud root",
            "{\"fileId\":\"f8\",\"revision\":\"r1\"}");
        Require(await ScalarAsync(databasePath,
                "SELECT count(*) FROM observation WHERE scan_generation_id=$scan;",
                ("$scan", scan.ScanGenerationId)) == 0,
            "every refused identity leaves the scan generation empty");
        Require(await ScalarAsync(databasePath, "SELECT count(*) FROM object_version;") == 0,
            "every refused identity leaves no object version behind");

        await authority.AppendObservationEntriesAsync(scan, new[]
        {
            Entry("valid.txt", Cloud(provider, account, ns, "valid-file", algorithm))
        });
        Require(await ScalarAsync(databasePath,
                "SELECT count(*) FROM observation WHERE scan_generation_id=$scan;",
                ("$scan", scan.ScanGenerationId)) == 1,
            "the exact matching cloud identity is accepted and writes its observation");
        Require(await ScalarAsync(databasePath,
                "SELECT count(*) FROM object_version WHERE json_extract(identity_json,'$.identityKind')='cloud' " +
                "AND json_extract(identity_json,'$.providerAccountId')=$account;", ("$account", account)) == 1,
            "the accepted observation retains the full native cloud identity");

        // Every explicit cloud observation identity must be refused on a root that is not a cloud root.
        var legacyPair = await RetainPairAsync(authority, CreatePair(Dropbox("22", "id:legacy-left"),
            Dropbox("23", "id:legacy-right"), "CC2C legacy"));
        await authority.RegisterRootBindingAsync("root-legacy", legacyPair.Id,
            SyncAuthorityCloudSides.LogicalLeft, 1,
            "{\"volumeId\":\"vol-legacy\",\"volumeSerial\":\"aa\",\"fileId\":\"bb\"}",
            Path.Combine(_root, "identity-rejections", "legacy-local"));
        var legacyScan = await authority.BeginScanGenerationAsync(legacyPair.Id, "root-legacy",
            SyncAuthorityCloudSides.LogicalLeft, "initial", "{\"scope\":\"complete\"}");
        await RejectAppendAsync(authority, databasePath, legacyScan, "a cloud identity on a legacy root",
            Entry("cloud-on-legacy.txt", Cloud(provider, account, ns, "f9", algorithm)));
    }

    private static async Task RejectAppendAsync(SyncAuthorityDatabase authority, string databasePath,
        SyncAuthorityScanGeneration scan, string label, SyncAuthorityObservationInput entry)
    {
        var refused = false;
        try
        {
            await authority.AppendObservationEntriesAsync(scan, new[] { entry });
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or
                                          ArgumentException or FormatException)
        {
            refused = true;
        }
        Require(refused, "the cloud observation append is refused for " + label);
        Require(await ScalarAsync(databasePath,
                "SELECT count(*) FROM observation WHERE scan_generation_id=$scan;",
                ("$scan", scan.ScanGenerationId)) == 0,
            "the refused " + label + " observation writes no row");
    }

    private static Task RejectAppendAsync(SyncAuthorityDatabase authority, string databasePath,
        SyncAuthorityScanGeneration scan, string label, string identityJson) =>
        RejectAppendAsync(authority, databasePath, scan, label, Entry("refused.txt", identityJson));

    private static string Cloud(string provider, string account, string ns, string fileId, string algorithm) =>
        "{\"identityKind\":\"cloud\",\"provider\":\"" + provider + "\",\"providerAccountId\":\"" +
        account + "\",\"namespaceId\":\"" + ns + "\",\"fileId\":\"" + fileId +
        "\",\"revision\":\"r1\",\"contentAlgorithm\":\"" + algorithm + "\"}";

    private static SyncAuthorityObservationInput Entry(string path, string identityJson) =>
        new(path, "file", identityJson, null, 0, "{\"path\":\"" + path + "\",\"kind\":\"file\"}");

    // ----- 3. hierarchy rejection ---------------------------------------------------------------

    private static async Task HierarchyRejectionsAsync()
    {
        Claim("CC2C-4/hierarchy", "A path-opaque provider proves distinctness only through a bound native chain");
        var authority = await OpenAuthorityAsync("hierarchy-rejections");
        var leftSpec = Drive("40", "gd-left-h");
        var rightSpec = Drive("41", "gd-right-h");
        await RegisterAccountAsync(authority, leftSpec);
        await RegisterAccountAsync(authority, rightSpec);
        var pair = await RetainPairAsync(authority, CreatePair(leftSpec, rightSpec, "CC2C hierarchy"));
        var leftRoot = "root-left-hierarchy";
        var rightRoot = "root-right-hierarchy";
        await RegisterCloudRootAsync(authority, pair, leftSpec, leftRoot, SyncAuthorityCloudSides.LogicalLeft);
        var missing = await RefusedAsync(() => RegisterCloudRootAsync(authority, pair, rightSpec, rightRoot,
            SyncAuthorityCloudSides.LogicalRight));
        Require(missing, "a path-opaque root with no supplied native chain is refused rather than assumed disjoint");
        var unbound = await RefusedAsync(() => RegisterCloudRootAsync(authority, pair, rightSpec, rightRoot,
            SyncAuthorityCloudSides.LogicalRight, new[]
            {
                new SyncAuthorityCloudHierarchySnapshot(rightSpec.Provider, rightSpec.ProviderAccountId,
                    rightSpec.EndpointNamespaceId, "root-does-not-exist", rightSpec.FolderId,
                    new[] { rightSpec.FolderId })
            }));
        Require(unbound, "a hierarchy snapshot bound to no retained root is refused");
        var nested = await RefusedAsync(() => RegisterCloudRootAsync(authority, pair, rightSpec, rightRoot,
            SyncAuthorityCloudSides.LogicalRight, new[]
            {
                new SyncAuthorityCloudHierarchySnapshot(rightSpec.Provider, rightSpec.ProviderAccountId,
                    rightSpec.EndpointNamespaceId, rightRoot, rightSpec.FolderId,
                    new[] { leftSpec.FolderId, rightSpec.FolderId })
                {
                    CompleteToCorpusRoot = true,
                    CorpusRootId = leftSpec.FolderId
                },
                new SyncAuthorityCloudHierarchySnapshot(leftSpec.Provider, leftSpec.ProviderAccountId,
                    leftSpec.EndpointNamespaceId, leftRoot, leftSpec.FolderId, new[] { leftSpec.FolderId })
                {
                    CompleteToCorpusRoot = true,
                    CorpusRootId = leftSpec.FolderId
                }
            }));
        Require(nested, "a native chain that places one root inside the other is refused");
        Require(await ScalarAsync(DatabasePath("hierarchy-rejections"),
                "SELECT count(*) FROM root_binding WHERE pair_id=$pair AND state='active';",
                ("$pair", pair.Id)) == 1,
            "only the first native root is retained while every provable refusal stands");
        // A Google display path that LOOKS nested under the other root is not ancestry: the provider path is
        // presentation only for a path-opaque provider, so the bound native chain decides and this is accepted.
        await RegisterCloudRootAtPathAsync(authority, pair, rightSpec, rightRoot,
            SyncAuthorityCloudSides.LogicalRight, "/Drive 40/nested-looking",
            DisjointChains(rightSpec, rightRoot, leftSpec, leftRoot));
        Require(await ScalarAsync(DatabasePath("hierarchy-rejections"),
                "SELECT count(*) FROM root_binding WHERE pair_id=$pair AND state='active';",
                ("$pair", pair.Id)) == 2,
            "two provably distinct native roots are both retained");
        Require(await ScalarAsync(DatabasePath("hierarchy-rejections"),
                "SELECT count(*) FROM root_binding WHERE root_binding_id=$root " +
                "AND display_path='/Drive 40/nested-looking';", ("$root", rightRoot)) == 1,
            "a nested-looking display path is retained as presentation and never used as ancestry");
    }

    // ----- 4. duplicate root rejection ----------------------------------------------------------

    private static async Task DuplicateRootRejectionsAsync()
    {
        Claim("CC2C-4/duplicates",
            "One global folder identity is refused across every active pair root regardless of side or account");
        var authority = await OpenAuthorityAsync("duplicate-roots");
        var leftSpec = Dropbox("50", "id:shared-dup");
        var rightSpec = Dropbox("51", "id:right-dup");
        await RegisterAccountAsync(authority, leftSpec);
        await RegisterAccountAsync(authority, rightSpec);
        var pair = await RetainPairAsync(authority, CreatePair(leftSpec, rightSpec, "CC2C duplicates"));
        await RegisterCloudRootAsync(authority, pair, leftSpec, "root-left-dup",
            SyncAuthorityCloudSides.LogicalLeft);
        Require(await RefusedAsync(() => RegisterCloudRootAsync(authority, pair,
                rightSpec with { FolderId = leftSpec.FolderId }, "root-right-dup",
                SyncAuthorityCloudSides.LogicalRight)),
            "the exact same global folder identity on the opposite port of one pair is refused");
        var sameAccount = Dropbox("52", "id:dup-parent");
        await RegisterAccountAsync(authority, sameAccount);
        var nestedSpec = sameAccount with { FolderId = "id:dup-child", FolderPath = "/Dropbox 52/Child" };
        var duplicateScan = await RetainPairAsync(authority, CreatePair(sameAccount, Dropbox("53", "id:dup-other"),
            "CC2C nested duplicates"));
        await RegisterCloudRootAsync(authority, duplicateScan, sameAccount, "root-nested-parent",
            SyncAuthorityCloudSides.LogicalLeft,
            new[] { CorpusChain(sameAccount, "root-nested-parent"), CorpusChain(leftSpec, "root-left-dup") });
        Require(await RefusedAsync(() => RegisterCloudRootAsync(authority, duplicateScan, nestedSpec,
                "root-nested-child", SyncAuthorityCloudSides.LogicalRight)),
            "a provider path nested inside an active root of the same account and namespace is refused");
        Require(await ScalarAsync(DatabasePath("duplicate-roots"),
                "SELECT count(*) FROM root_binding WHERE state='active';") == 2,
            "every refused cloud root leaves the retained active roots unchanged");

        var google = await OpenAuthorityAsync("duplicate-google");
        var leftDrive = Drive("54", "gd-shared-dup");
        var rightDrive = Drive("55", "gd-right-dup");
        await RegisterAccountAsync(google, leftDrive);
        await RegisterAccountAsync(google, rightDrive);
        var googlePair = await RetainPairAsync(google, CreatePair(leftDrive, rightDrive, "CC2C google duplicates"));
        await RegisterCloudRootAsync(google, googlePair, leftDrive, "root-left-gdup",
            SyncAuthorityCloudSides.LogicalLeft);
        Require(await RefusedAsync(() => RegisterCloudRootAsync(google, googlePair,
                rightDrive with { FolderId = leftDrive.FolderId }, "root-right-gdup",
                SyncAuthorityCloudSides.LogicalRight)),
            "the same global Google folder identity on another account is refused");
        Require(await RefusedAsync(() => RegisterCloudRootAsync(google, googlePair, rightDrive,
                "root-right-gdup", SyncAuthorityCloudSides.LogicalRight, new[]
                {
                    new SyncAuthorityCloudHierarchySnapshot(rightDrive.Provider, rightDrive.ProviderAccountId,
                        rightDrive.EndpointNamespaceId, "root-right-gdup", rightDrive.FolderId,
                        new[] { leftDrive.FolderId, rightDrive.FolderId })
                    {
                        CompleteToCorpusRoot = true,
                        CorpusRootId = leftDrive.FolderId
                    },
                    new SyncAuthorityCloudHierarchySnapshot(leftDrive.Provider, leftDrive.ProviderAccountId,
                        leftDrive.EndpointNamespaceId, "root-left-gdup", leftDrive.FolderId,
                        new[] { leftDrive.FolderId })
                    {
                        CompleteToCorpusRoot = true,
                        CorpusRootId = leftDrive.FolderId
                    }
                })),
            "a Google native chain that places one root inside the other is refused");
        Require(await ScalarAsync(DatabasePath("duplicate-google"),
                "SELECT count(*) FROM root_binding WHERE state='active';") == 1,
            "the refused Google roots leave only the one retained active root");

        var recovery = await OpenAuthorityAsync("duplicate-recovery");
        var recoverySpec = Dropbox("56", "id:recovery-cloud");
        await RegisterAccountAsync(recovery, recoverySpec);
        var recoveryPair = await RetainPairAsync(recovery, CreatePair(recoverySpec,
            Dropbox("57", "id:recovery-other"), "CC2C recovery boundary"));
        await recovery.RegisterDropboxRecoveryRootAsync("recovery-root-56", InstallationId,
            recoverySpec.ProviderAccountId, "/Recovery", "id:recovery-artifact",
            recoverySpec.RootNamespaceId, 1);
        Require(await RefusedAsync(() => RegisterCloudRootAsync(recovery, recoveryPair,
                recoverySpec with { FolderId = "id:recovery-artifact" }, "root-recovery-dup",
                SyncAuthorityCloudSides.LogicalLeft)),
            "a cloud root that is the same provider folder as an active recovery boundary is refused");
        Require(await ScalarAsync(DatabasePath("duplicate-recovery"),
                "SELECT count(*) FROM root_binding WHERE state='active';") == 0,
            "the refused recovery-boundary duplicate leaves no active pair root");
    }

    // ----- 5. partial listing and root drift ----------------------------------------------------

    private static async Task PartialListingAndDriftAsync()
    {
        Claim("CC2C-4/partial", "A partial listing or a drifted root never becomes absence authority");
        await UnretainedAfterFaultAsync("partial-listing", store => store.ThrowOnList = true);
        await UnretainedAfterFaultAsync("root-drift", store => store.ThrowOnVerifyCall = 2);
    }

    private static async Task UnretainedAfterFaultAsync(string caseName, Action<FixtureStore> fault)
    {
        var authority = await OpenAuthorityAsync(caseName);
        var databasePath = DatabasePath(caseName);
        var leftSpec = Dropbox("60", "id:fault-left");
        var rightSpec = Dropbox("61", "id:fault-right");
        await RegisterAccountAsync(authority, leftSpec);
        await RegisterAccountAsync(authority, rightSpec);
        var pair = await RetainPairAsync(authority, CreatePair(leftSpec, rightSpec, "CC2C " + caseName));
        var leftRoot = "root-left-" + caseName;
        var rightRoot = "root-right-" + caseName;
        await RegisterCloudRootAsync(authority, pair, leftSpec, leftRoot, SyncAuthorityCloudSides.LogicalLeft,
            new[] { CorpusChain(leftSpec, leftRoot) });
        await RegisterCloudRootAsync(authority, pair, rightSpec, rightRoot, SyncAuthorityCloudSides.LogicalRight,
            new[] { CorpusChain(rightSpec, rightRoot), CorpusChain(leftSpec, leftRoot) });
        var leftStore = CreateStore(SideEntries("left-fault", "left-only.txt"));
        var rightStore = CreateStore(SideEntries("right-fault", "right-only.txt"));
        var siblingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var siblingDrained = false;
        leftStore.BeforeList = async token =>
        {
            siblingStarted.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { siblingDrained = true; }
        };
        rightStore.BeforeList = token => siblingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        fault(rightStore);
        var set = SyncAuthorityCloudEndpointSet.FromPair(pair, leftStore.Store, rightStore.Store, leftRoot,
            rightRoot, leftSpec.ProviderAccountId, rightSpec.ProviderAccountId);
        var coordinator = new SyncAuthorityObservationCoordinator(pair, leftStore.Store, authority, leftRoot,
            rightRoot, onInventory: null, cloudEndpoints: set);
        var failed = await RefusedAsync(() => coordinator.ObserveAsync("initial"));
        Require(failed, caseName + " fails the whole observation instead of retaining a partial inventory");
        Require(siblingDrained, caseName + " cancels and drains the in-flight sibling before returning");
        Require(await ScalarAsync(databasePath, "SELECT count(*) FROM scan_generation WHERE pair_id=$pair;",
                ("$pair", pair.Id)) == 0,
            caseName + " begins no scan generation at all for either side");
        Require(await ScalarAsync(databasePath, "SELECT count(*) FROM observation;") == 0,
            caseName + " retains no observation and therefore no absence authority");
        Require(await ScalarAsync(databasePath, "SELECT count(*) FROM operation;") == 0,
            caseName + " enqueues no effect for the failed listing");
    }

    // ----- 6. routing fences --------------------------------------------------------------------

    private static async Task RoutingFencesAsync()
    {
        Claim("CC2C-4/routing",
            "Only an explicit cloud/cloud port set selects the cloud path, and a bound fence is refused");
        var authority = await OpenAuthorityAsync("routing-fences");
        var databasePath = DatabasePath("routing-fences");
        var leftSpec = Dropbox("70", "id:route-left");
        var rightSpec = Dropbox("71", "id:route-right");
        await RegisterAccountAsync(authority, leftSpec);
        await RegisterAccountAsync(authority, rightSpec);
        var pair = await RetainPairAsync(authority, CreatePair(leftSpec, rightSpec, "CC2C routing"));
        var leftRoot = "root-left-routing";
        var rightRoot = "root-right-routing";
        await RegisterCloudRootAsync(authority, pair, leftSpec, leftRoot, SyncAuthorityCloudSides.LogicalLeft,
            new[] { CorpusChain(leftSpec, leftRoot) });
        await RegisterCloudRootAsync(authority, pair, rightSpec, rightRoot, SyncAuthorityCloudSides.LogicalRight,
            new[] { CorpusChain(rightSpec, rightRoot), CorpusChain(leftSpec, leftRoot) });
        var leftStore = CreateStore(SideEntries("left-route", "left-only.txt"));
        var rightStore = CreateStore(SideEntries("right-route", "right-only.txt"));
        var legacy = new SyncAuthorityObservationCoordinator(pair, leftStore.Store, authority, leftRoot,
            rightRoot);
        Require(!legacy.UsesCloudEndpointRouting, "a coordinator without a typed port set keeps the physical route");
        Require(await RefusedAsync(() => legacy.ObserveAsync("initial")),
            "a cloud/cloud pair is never observed through the legacy physical route");
        Require(await ScalarAsync(databasePath, "SELECT count(*) FROM scan_generation WHERE pair_id=$pair;",
                ("$pair", pair.Id)) == 0,
            "the refused legacy route retains no scan generation");
        Require(leftStore.VerifyCalls == 0 && leftStore.ListCalls == 0,
            "the legacy route reaches no cloud adapter call");

        var set = SyncAuthorityCloudEndpointSet.FromPair(pair, leftStore.Store, rightStore.Store, leftRoot,
            rightRoot, leftSpec.ProviderAccountId, rightSpec.ProviderAccountId);
        var coordinator = new SyncAuthorityObservationCoordinator(pair, leftStore.Store, authority, leftRoot,
            rightRoot, onInventory: null, cloudEndpoints: set);
        var lease = new SyncAuthorityLease("lease-route", "operation-route", "worker-route", 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5));
        var binding = new SyncAuthorityObservationBindingAuthority("operation-route", lease,
            Array.Empty<SyncAuthorityObservationObjectBinding>());
        Require(await RefusedAsync(() => coordinator.ObserveBoundAsync("initial", binding)),
            "a bound logical-object fence is refused for a cloud/cloud pair");
        Require(await ScalarAsync(databasePath, "SELECT count(*) FROM scan_generation WHERE pair_id=$pair;",
                ("$pair", pair.Id)) == 0,
            "the refused bound fence retains no scan generation");

        var refusedSet = !SyncAuthorityCloudEndpointSet.TryFromPair(
            new SyncPair("legacy-pair", "Legacy", Path.Combine(_root, "routing-fences", "local-folder"), 1, 1,
                "/Folder", "id:legacy"), leftStore.Store, rightStore.Store, leftRoot, rightRoot,
            leftSpec.ProviderAccountId, rightSpec.ProviderAccountId, out _, out var validation);
        Require(refusedSet && validation.Refusals.Any(item => item.Error == SyncEndpointError.EndpointKindMismatch),
            "a local-containing pair is refused by the typed cloud endpoint set");
        var sameRoot = !SyncAuthorityCloudEndpointSet.TryFromPair(pair, leftStore.Store, rightStore.Store,
            leftRoot, leftRoot, leftSpec.ProviderAccountId, rightSpec.ProviderAccountId, out _,
            out var rootValidation);
        Require(sameRoot && rootValidation.Refusals.Any(item => item.Error == SyncEndpointError.SameEndpoint),
            "one authority root binding id cannot serve both logical sides");
        await authority.RegisterPairAsync("legacy-path-pair", 1);
        await authority.RegisterRootBindingAsync("root-legacy-path", "legacy-path-pair",
            SyncAuthorityCloudSides.LogicalRight, 1,
            "{\"providerAccountId\":\"" + rightSpec.ProviderAccountId + "\",\"rootNamespaceId\":\"" +
            rightSpec.RootNamespaceId + "\",\"folderId\":\"id:legacy-path\"}", "/Dropbox 71");
        Require(await ScalarAsync(databasePath,
                "SELECT count(*) FROM root_binding WHERE root_binding_id='root-legacy-path' AND state='active';") == 1,
            "a legacy cloud root is decided only against legacy roots, never by a typed cloud root");
        Require(await ScalarAsync(databasePath,
                "SELECT count(*) FROM root_binding WHERE state='active' AND " +
                "json_extract(identity_json,'$.identityKind')='cloud';") == 2,
            "both typed cloud roots stay active with their explicit cloud identity");
    }

    // ----- 7. the obsolete identical-id positive, retained as an explicit rejection ----------------

    // CC2G: the CC2C positive pairing used to place one identical same-provider file id at two different
    // paths on the two sides and assert the two stayed separate objects. Under the shared cloud object
    // overlap correction that exact input is one backing object reachable from both sides, so it is retained
    // here as an explicit pre-retention rejection while the positive pairings above use two genuinely
    // distinct provider file ids. The original failure evidence therefore stays visible in this file.
    private static async Task SharedObjectOverlapAsync()
    {
        Claim("CC2G-4/obsolete-positive",
            "The obsolete identical same-provider item id fixture is now an explicit pre-scan overlap rejection");
        var authority = await OpenAuthorityAsync("shared-object-overlap");
        var databasePath = DatabasePath("shared-object-overlap");
        var leftSpec = Dropbox("80", "id:overlap-left");
        var rightSpec = Dropbox("81", "id:overlap-right");
        await RegisterAccountAsync(authority, leftSpec);
        await RegisterAccountAsync(authority, rightSpec);
        var pair = await RetainPairAsync(authority, CreatePair(leftSpec, rightSpec, "CC2C overlap"));
        var leftRoot = "root-left-overlap";
        var rightRoot = "root-right-overlap";
        await RegisterCloudRootAsync(authority, pair, leftSpec, leftRoot, SyncAuthorityCloudSides.LogicalLeft,
            new[] { CorpusChain(leftSpec, leftRoot) });
        await RegisterCloudRootAsync(authority, pair, rightSpec, rightRoot, SyncAuthorityCloudSides.LogicalRight,
            new[] { CorpusChain(rightSpec, rightRoot), CorpusChain(leftSpec, leftRoot) });
        var leftStore = CreateStore(new[]
        {
            new PilotRemoteEntry("left-only.txt", PilotEntryKind.File, "SAME-FILE-ID", "rev-1", 3,
                new string('b', 64), DateTimeOffset.UnixEpoch)
        });
        var rightStore = CreateStore(new[]
        {
            new PilotRemoteEntry("right-only.txt", PilotEntryKind.File, "SAME-FILE-ID", "rev-1", 3,
                new string('b', 64), DateTimeOffset.UnixEpoch)
        });
        var set = SyncAuthorityCloudEndpointSet.FromPair(pair, leftStore.Store, rightStore.Store, leftRoot,
            rightRoot, leftSpec.ProviderAccountId, rightSpec.ProviderAccountId);
        var coordinator = new SyncAuthorityObservationCoordinator(pair, leftStore.Store, authority, leftRoot,
            rightRoot, onInventory: null, cloudEndpoints: set);
        var refused = false;
        try
        {
            await coordinator.ObserveAsync("initial");
        }
        catch (SyncAuthorityCloudObjectOverlapException error)
        {
            refused = error.Message.Contains("SAME-FILE-ID", StringComparison.Ordinal);
        }
        Require(refused,
            "one identical same-provider file id reachable from both sides at two different paths is refused with an explicit overlap reason");
        Require(await ScalarAsync(databasePath,
                "SELECT count(*) FROM scan_generation WHERE pair_id=$pair;", ("$pair", pair.Id)) == 0,
            "the refused overlap begins no scan generation for either side");
        Require(await ScalarAsync(databasePath, "SELECT count(*) FROM observation;") == 0,
            "the refused overlap retains no observation and therefore no absence authority");
        Require(await ScalarAsync(databasePath, "SELECT count(*) FROM operation;") == 0,
            "the refused overlap enqueues no effect");
        Require(leftStore.VerifyCalls == 2 && leftStore.ListCalls == 1 && rightStore.VerifyCalls == 2 &&
                rightStore.ListCalls == 1,
            "the refusal happens after both roots were listed and verified and before any retention");
    }
    // ----- helpers ------------------------------------------------------------------------------

    private static async Task ConcurrentReadLifetimeAsync()
    {
        Claim("inventory-concurrency/lifetime", "Bounded paired reads preserve original errors and drain on cancellation");
        foreach (var failedSide in new[] { "left", "right" })
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var drained = false;
            var expected = new IOException("exact " + failedSide + " inventory failure");
            async Task<int> Waiting(CancellationToken token)
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return 0; }
                finally { drained = true; }
            }
            async Task<int> Failing(CancellationToken token)
            {
                await started.Task.WaitAsync(token);
                throw expected;
            }
            Exception? observed = null;
            try
            {
                await SyncEndpointInventoryRead.BothAsync<int, int>(
                    failedSide == "left" ? Failing : Waiting,
                    failedSide == "left" ? Waiting : Failing, limit.Token);
            }
            catch (Exception error) { observed = error; }
            Require(ReferenceEquals(observed, expected) && drained,
                failedSide + " failure survives sibling cancellation and both tasks have stopped");
        }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var stopped = 0;
        async Task<int> WaitForCancellation(CancellationToken token)
        {
            if (Interlocked.Increment(ref active) == 2) bothStarted.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return 0; }
            finally { Interlocked.Increment(ref stopped); }
        }
        var read = SyncEndpointInventoryRead.BothAsync(WaitForCancellation, WaitForCancellation, cancellation.Token);
        await bothStarted.Task.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        var cancelled = false;
        try { await read; }
        catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled && stopped == 2, "external deadline cancellation drains both endpoint reads");
        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        var invoked = 0;
        try
        {
            await SyncEndpointInventoryRead.BothAsync(_ => Task.FromResult(++invoked),
                _ => Task.FromResult(++invoked), alreadyCancelled.Token);
        }
        catch (OperationCanceledException) { }
        Require(invoked == 0, "an already expired deadline dispatches no endpoint work");
    }

    private static async Task<bool> RefusedAsync(Func<Task> action)
    {
        try
        {
            await action();
            return false;
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or
                                          ArgumentException or IOException or FormatException)
        {
            return true;
        }
    }

    private static async Task<long> ScalarAsync(string databasePath, string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static void Claim(string id, string description) =>
        Cases.Add(new Dictionary<string, object?> { ["id"] = id, ["description"] = description });

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidDataException("Cloud endpoint observation assertion failed: " + message);
        _assertions++;
    }
}

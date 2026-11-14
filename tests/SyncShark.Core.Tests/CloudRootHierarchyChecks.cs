using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

// CC2D focused proofs: the real provider adapters' cloud root hierarchy capability over deterministic HTTP
// protocol fixtures, plus the real CC2C relationship validator over real isolated SQLite state.
//
// The fixtures supply provider metadata bytes only. Every account, namespace, corpus, parent-chain,
// terminal-root, drift, depth and comparison decision below is made by the production adapter and the
// production comparison code. No live provider, credential, transport or installed database is reachable,
// and each fixture refuses listing, content and effect routes so an unexpected provider call fails the run
// instead of being silently tolerated.
internal static class CloudRootHierarchyChecks
{
    private const string InstallationId = "installation-cloud-root-hierarchy-316";
    private const string SourceRevision = "cloud-root-hierarchy-316";
    private const string FolderMime = GoogleDrivePilotClient.FolderMimeType;
    private static readonly List<Dictionary<string, object?>> Cases = [];
    private static int _assertions;
    private static string _root = "";

    internal static async Task RunAsync(string root)
    {
        _root = Path.GetFullPath(root);
        if (Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any())
            throw new InvalidOperationException("Use a fresh isolated cloud root hierarchy fixture root.");
        Directory.CreateDirectory(_root);
        Cases.Clear();
        _assertions = 0;

        await GoogleMyDriveChainAsync();
        await GoogleSharedDriveChainAsync();
        await GoogleRefusalsAsync();
        await DropboxChainsAsync();
        await DropboxRelationshipAsync();
        await ComparisonEvidenceAsync();
        await BindingRefusalsAsync();

        var report = new Dictionary<string, object?>
        {
            ["generated_by"] = "CloudRootHierarchyChecks (tests/SyncShark.Core.Tests)",
            ["fixture_root"] = _root,
            ["assertions"] = _assertions,
            ["cases"] = Cases
        };
        await File.WriteAllTextAsync(Path.Combine(_root, "cloud-root-hierarchy-checks.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS cloud root hierarchy: " + _assertions +
            " assertions; real Google Drive and Dropbox adapter hierarchy readback over HTTP protocol" +
            " fixtures, two-pass drift refusal, positively identified corpus terminals, and the real CC2C" +
            " relationship validator over real isolated SQLite state.");
    }

    // ----- 1. Google Drive My Drive ancestry -----------------------------------------------------

    private static async Task GoogleMyDriveChainAsync()
    {
        Claim("CC2D-2/my-drive", "A real My Drive walk resolves the actual files/root id as the terminal");
        using var wire = new DriveWire();
        wire.Add("gd-root", []);
        wire.Add("gd-mid", ["gd-root"]);
        wire.Add("gd-child", ["gd-mid"]);
        var binding = DriveBinding("perm-one", shared: false);
        using var http = new HttpClient(wire);
        var client = DriveClient(http, binding, "gd-child");
        var evidence = await client.ReadRootHierarchyAsync(DriveRequest(binding, "perm-one", "gd-child"));

        Require(evidence.Provider == SyncProviders.GoogleDrive, "the evidence carries the registered provider literal");
        Require(evidence.ConnectionId == binding.ConnectionId, "the evidence binds the retained connection");
        Require(evidence.NamespaceId == "perm-one", "the evidence binds the retained native account namespace");
        Require(evidence.SelectedRootId == "gd-child", "the selected root is the exact bound folder identity");
        Require(evidence.CorpusRootId == "gd-root", "the corpus root is the opaque id returned by files/root");
        Require(evidence.FolderPath == "/Selected", "the retained display path is carried as presentation only");
        Require(evidence.CorpusRootToSelectedRootIds.SequenceEqual(["gd-root", "gd-mid", "gd-child"]),
            "the chain is ordered from the verified corpus root down to the selected root");
        Require(evidence.VerifiedPassCount == 2, "two independent readback passes agreed");
        Require(evidence.ObservedAtUtc > DateTimeOffset.UtcNow.AddMinutes(-5) && evidence.ObservedAtUtc <= DateTimeOffset.UtcNow,
            "the evidence carries its own observation time");
        Require(wire.AboutCalls == 1 && wire.RootAliasCalls == 2 && wire.NodeReads == 6 && wire.ForbiddenCalls == 0,
            "only metadata reads happened: one account read, files/root and three folders per pass, and no list, content or effect call");
        var snapshot = evidence.ToSnapshot("root-g-my-drive");
        Require(snapshot.CompleteToCorpusRoot && snapshot.CorpusRootId == "gd-root" &&
                snapshot.ChainFolderIds.SequenceEqual(["gd-root", "gd-mid", "gd-child"]),
            "the one CC2C hierarchy value is produced with its verified completeness and corpus root");
        Require(snapshot.IsCompleteFor(new SyncAuthorityCloudRootIdentity(SyncProviders.GoogleDrive,
                DriveAccount("perm-one"), "perm-one", "gd-child", "/Selected", "root-g-my-drive",
                SyncAuthorityCloudRootIdentity.PairRootAuthority, SyncAuthorityCloudSides.LogicalLeft)),
            "the produced snapshot is complete and bound for exactly that retained root identity");

        // The alias is not the returned opaque id, and an alias-only files/root answer is refused.
        using var aliasWire = new DriveWire { RootAliasIsAlias = true, PermissionId = "perm-two" };
        aliasWire.Add("gd-root", []);
        aliasWire.Add("gd-child", ["gd-root"]);
        using var aliasHttp = new HttpClient(aliasWire);
        Require(await RefusedAsync(() => DriveClient(aliasHttp, DriveBinding("perm-two", false), "gd-child")
                .ReadRootHierarchyAsync(DriveRequest(DriveBinding("perm-two", false), "perm-two", "gd-child"))),
            "a files/root response that only repeats the root alias is refused instead of becoming a corpus root");

        using var boundAliasWire = new DriveWire { PermissionId = "perm-three" };
        boundAliasWire.Add("gd-root", []);
        using var boundAliasHttp = new HttpClient(boundAliasWire);
        Require(await RefusedAsync(() => DriveClient(boundAliasHttp, DriveBinding("perm-three", false), "root")
                .ReadRootHierarchyAsync(DriveRequest(DriveBinding("perm-three", false), "perm-three", "root"))),
            "a My Drive root bound to the alias is refused rather than treated as an opaque id");
    }

    // ----- 2. Google Drive shared-drive ancestry -------------------------------------------------

    private static async Task GoogleSharedDriveChainAsync()
    {
        Claim("CC2D-2/shared-drive", "An enabled shared-drive corpus terminates at the positively read drive id");
        using var wire = new DriveWire { PermissionId = "perm-shared" };
        wire.Add("drive-one", [], driveId: "drive-one");
        wire.Add("sd-child", ["drive-one"], driveId: "drive-one");
        var binding = DriveBinding("perm-shared", shared: true);
        using var http = new HttpClient(wire);
        var client = DriveClient(http, binding, "sd-child", sharedDriveId: "drive-one");
        var evidence = await client.ReadRootHierarchyAsync(DriveRequest(binding, "perm-shared", "sd-child"));

        Require(evidence.CorpusRootId == "drive-one", "the shared-drive terminal is the exact drive id");
        Require(evidence.CorpusRootToSelectedRootIds.SequenceEqual(["drive-one", "sd-child"]),
            "the shared-drive chain is ordered from the drive root folder to the selected root");
        Require(wire.RootAliasCalls == 0, "a shared-drive corpus never consults the My Drive root alias");
        Require(wire.ForbiddenCalls == 0, "the shared-drive walk listed nothing, downloaded nothing and issued no effect");

        using var disabledWire = new DriveWire { PermissionId = "perm-shared" };
        disabledWire.Add("drive-one", [], driveId: "drive-one");
        disabledWire.Add("sd-child", ["drive-one"], driveId: "drive-one");
        using var disabledHttp = new HttpClient(disabledWire);
        var disabledBinding = DriveBinding("perm-shared", shared: false);
        Require(await RefusedAsync(() => DriveClient(disabledHttp, disabledBinding, "sd-child")
                .ReadRootHierarchyAsync(DriveRequest(disabledBinding, "perm-shared", "sd-child"))),
            "a shared-drive root without the already enabled capability is refused, never treated as My Drive");

        using var rebindWire = new DriveWire { PermissionId = "perm-shared" };
        rebindWire.Add("drive-two", [], driveId: "drive-two");
        rebindWire.Add("sd-child", ["drive-two"], driveId: "drive-two");
        using var rebindHttp = new HttpClient(rebindWire);
        var rebindBinding = DriveBinding("perm-shared", shared: true);
        Require(await RefusedAsync(() => DriveClient(rebindHttp, rebindBinding, "sd-child", sharedDriveId: "drive-one")
                .ReadRootHierarchyAsync(DriveRequest(rebindBinding, "perm-shared", "sd-child"))),
            "a selected root in another shared drive than the bound one is refused");
    }

    // ----- 3. Google Drive safe refusals ---------------------------------------------------------

    private static async Task GoogleRefusalsAsync()
    {
        Claim("CC2D-2/google-refusals", "Incomplete, ambiguous, cyclic, drifted and foreign ancestry refuses");
        Require(await RefusedGoogleAsync("multiple parents", wire =>
        {
            wire.Add("gd-root", []);
            wire.Add("gd-other", ["gd-root"]);
            wire.Add("gd-child", ["gd-root", "gd-other"]);
        }, "gd-child"),
            "an item with more than one parent refuses rather than choosing an ancestor");
        Require(await RefusedGoogleAsync("cyclic ancestry", wire =>
        {
            wire.Add("gd-cycle-a", ["gd-cycle-b"]);
            wire.Add("gd-cycle-b", ["gd-cycle-a"]);
        }, "gd-cycle-a"),
            "a cyclic parent chain refuses");
        Require(await RefusedGoogleAsync("unknown parentless root", wire => wire.Add("gd-orphan", []), "gd-orphan"),
            "a parentless item that is not the verified files/root id refuses rather than becoming a corpus root");
        Require(await RefusedGoogleAsync("trashed folder", wire =>
        {
            wire.Add("gd-root", []);
            wire.Add("gd-child", ["gd-root"], trashed: true);
        }, "gd-child"),
            "a trashed folder on the chain refuses");
        Require(await RefusedGoogleAsync("shortcut", wire =>
        {
            wire.Add("gd-root", []);
            wire.Add("gd-child", ["gd-root"], shortcutTarget: "gd-root");
        }, "gd-child"),
            "a shortcut on the chain refuses instead of following its target");
        Require(await RefusedGoogleAsync("non-folder", wire =>
        {
            wire.Add("gd-root", []);
            wire.Add("gd-child", ["gd-root"], mime: "application/octet-stream");
        }, "gd-child"),
            "a non-folder item on the chain refuses");
        Require(await RefusedGoogleAsync("missing provider version", wire =>
        {
            wire.Add("gd-root", []);
            wire.Add("gd-child", ["gd-root"], version: null);
        }, "gd-child"),
            "a node without a verifiable provider version refuses");
        Require(await RefusedGoogleAsync("wrong corpus", wire =>
        {
            wire.Add("gd-root", [], driveId: "drive-other");
            wire.Add("gd-child", ["gd-root"], driveId: "drive-other");
        }, "gd-child"),
            "a My Drive binding whose root belongs to a shared drive refuses");
        Require(await RefusedGoogleAsync("inaccessible ancestor", wire => wire.Add("gd-child", ["gd-missing"]), "gd-child"),
            "an ancestor the account cannot read refuses");
        using (var depthWire = new DriveWire())
        {
            depthWire.PermissionId = "perm-depth";
            depthWire.Add("gd-root", []);
            for (var index = 1; index <= 200; index++)
                depthWire.Add("gd-d" + index, index == 1 ? ["gd-root"] : ["gd-d" + (index - 1)]);
            using var depthHttp = new HttpClient(depthWire);
            var depthBinding = DriveBinding("perm-depth", false);
            Require(await RefusedAsync(() => DriveClient(depthHttp, depthBinding, "gd-d200")
                    .ReadRootHierarchyAsync(DriveRequest(depthBinding, "perm-depth", "gd-d200"))),
                "an ancestry deeper than the bounded 128 levels refuses");
        }
        using (var driftWire = new DriveWire())
        {
            driftWire.PermissionId = "perm-drift";
            driftWire.Add("gd-root", []);
            driftWire.Add("gd-mid", ["gd-root"]);
            driftWire.Add("gd-child", ["gd-mid"]);
            var midReads = 0;
            driftWire.AfterRead = node =>
            {
                if (node.Id != "gd-mid") return;
                midReads++;
                if (midReads == 2) node.Version = "2";
            };
            using var driftHttp = new HttpClient(driftWire);
            var driftBinding = DriveBinding("perm-drift", false);
            Require(await RefusedAsync(() => DriveClient(driftHttp, driftBinding, "gd-child")
                    .ReadRootHierarchyAsync(DriveRequest(driftBinding, "perm-drift", "gd-child"))),
                "a chain that drifts between the two readbacks refuses");
        }
        using (var cancelledWire = new DriveWire())
        {
            cancelledWire.PermissionId = "perm-cancel";
            cancelledWire.Add("gd-root", []);
            cancelledWire.Add("gd-child", ["gd-root"]);
            using var cancelledHttp = new HttpClient(cancelledWire);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var cancelledBinding = DriveBinding("perm-cancel", false);
            Require(await RefusedAsync(() => DriveClient(cancelledHttp, cancelledBinding, "gd-child")
                    .ReadRootHierarchyAsync(DriveRequest(cancelledBinding, "perm-cancel", "gd-child"), cancelled.Token)),
                "a cancelled hierarchy read refuses without claiming ancestry");
        }
    }

    private static async Task<bool> RefusedGoogleAsync(string label, Action<DriveWire> arrange, string selected)
    {
        using var wire = new DriveWire();
        arrange(wire);
        var permission = "perm-" + label.Replace(' ', '-');
        wire.PermissionId = permission;
        using var http = new HttpClient(wire);
        var binding = DriveBinding(permission, shared: false);
        var refused = await RefusedAsync(() => DriveClient(http, binding, selected)
            .ReadRootHierarchyAsync(DriveRequest(binding, permission, selected)));
        Require(wire.ForbiddenCalls == 0, label + ": the refusal used metadata reads only");
        return refused;
    }

    // ----- 4. Dropbox ancestry -------------------------------------------------------------------

    private static async Task DropboxChainsAsync()
    {
        Claim("CC2D-3/dropbox-chains", "A real Dropbox walk reads actual ancestor ids inside the verified namespace");
        using var wire = new DropboxWire("dbid-a", "ns-root-a", "ns-home-a");
        wire.Add("/Shared", "id:shared");
        wire.Add("/Shared/Leaf", "id:leaf");
        using var http = new HttpClient(wire);
        var binding = DropboxBinding("dbid-a", "ns-root-a", "ns-home-a");
        var client = DropboxClient(http, binding, "/Shared/Leaf", "id:leaf");
        var evidence = await client.ReadRootHierarchyAsync(DropboxRequest(binding, "dbid-a", "ns-root-a",
            "ns-home-a", "id:leaf", "/Shared/Leaf"));

        Require(evidence.Provider == SyncProviders.Dropbox, "the evidence carries the registered provider literal");
        Require(evidence.NamespaceId == "ns-root-a", "the evidence binds the retained root namespace");
        Require(evidence.SelectedRootId == "id:leaf", "the selected root is the exact bound folder identity");
        Require(evidence.CorpusRootId == PilotPath.DropboxNamespaceRootIdentity("ns-root-a"),
            "the terminal is the existing synthetic namespace-root identity");
        Require(evidence.CorpusRootToSelectedRootIds.SequenceEqual(
                [PilotPath.DropboxNamespaceRootIdentity("ns-root-a"), "id:shared", "id:leaf"]),
            "the chain carries the namespace root and every actual ancestor stable id");
        Require(evidence.VerifiedPassCount == 2 && wire.AccountCalls == 1 && wire.MetadataCalls == 4 &&
                wire.ForbiddenCalls == 0,
            "two fresh passes agreed, only the account and folder metadata were read, and no listing, content or effect call happened");
        Require(wire.PathRootNamespaces.All(value => value == "ns-root-a"),
            "every folder read carried the retained Dropbox root namespace");

        using var namespaceWire = new DropboxWire("dbid-a", "ns-root-a", "ns-home-a");
        using var namespaceHttp = new HttpClient(namespaceWire);
        var namespaceBinding = DropboxBinding("dbid-a", "ns-root-a", "ns-home-a");
        var namespaceIdentity = PilotPath.DropboxNamespaceRootIdentity("ns-root-a");
        var namespaceEvidence = await DropboxClient(namespaceHttp, namespaceBinding, "/", namespaceIdentity)
            .ReadRootHierarchyAsync(DropboxRequest(namespaceBinding, "dbid-a", "ns-root-a", "ns-home-a",
                namespaceIdentity, "/"));
        Require(namespaceEvidence.CorpusRootToSelectedRootIds.SequenceEqual([namespaceIdentity]),
            "the namespace-root pair terminates at its own synthetic identity");
        Require(namespaceWire.MetadataCalls == 0, "the namespace-root terminal needs no folder metadata read");

        using var movedWire = new DropboxWire("dbid-a", "ns-root-a", "ns-home-a");
        movedWire.Add("/Shared", "id:shared");
        movedWire.Add("/Shared/Leaf", "id:leaf");
        using var movedHttp = new HttpClient(movedWire);
        Require(await RefusedAsync(() => DropboxClient(movedHttp, binding, "/Elsewhere", "id:leaf")
                .ReadRootHierarchyAsync(DropboxRequest(binding, "dbid-a", "ns-root-a", "ns-home-a", "id:leaf", "/Shared/Leaf"))),
            "a root that left its retained path refuses, consistent with the existing root verification");
        Require(await RefusedAsync(() => DropboxClient(movedHttp, binding, "/Shared/Leaf", "id:leaf")
                .ReadRootHierarchyAsync(DropboxRequest(binding, "dbid-a", "ns-root-a", "ns-home-a", "id:leaf", "/Stale"))),
            "a stale caller display path refuses against the current provider readback");

        using (var depthWire = new DropboxWire("dbid-a", "ns-root-a", "ns-home-a"))
        {
            var path = "";
            for (var index = 1; index <= 200; index++)
            {
                path += "/L" + index;
                depthWire.Add(path, "id:l" + index);
            }
            using var depthHttp = new HttpClient(depthWire);
            Require(await RefusedAsync(() => DropboxClient(depthHttp, binding, path, "id:l200")
                    .ReadRootHierarchyAsync(DropboxRequest(binding, "dbid-a", "ns-root-a", "ns-home-a", "id:l200", path))),
                "an ancestry deeper than the bounded 128 levels refuses");
        }
    }

    // ----- 5. Dropbox relationship proofs through the real CC2C validator ------------------------

    private static async Task DropboxRelationshipAsync()
    {
        Claim("CC2D-3/relationships", "Real adapter evidence drives the real CC2C cloud root relationship validator");
        var authority = await OpenAuthorityAsync("relationships");
        var leftSpec = new EndpointSpec("dbid-left", "ns-root-left", "ns-home-left", "id:shared", "/Shared");
        var rightSpec = new EndpointSpec("dbid-right", "ns-root-right", "ns-home-right", "id:leaf", "/Shared/Leaf");
        var siblingSpecA = new EndpointSpec("dbid-right", "ns-root-right", "ns-home-right", "id:sa", "/Parent/A");
        var siblingSpecB = new EndpointSpec("dbid-right", "ns-root-right", "ns-home-right", "id:sb", "/Parent/B");
        await RegisterAccountAsync(authority, leftSpec);
        await RegisterAccountAsync(authority, rightSpec);
        var pair = await RetainPairAsync(authority, CreatePair(leftSpec, rightSpec, "CC2D relationships"));
        var leftStore = new DropboxWireHandler();
        var rightStore = new DropboxWireHandler("dbid-right", "ns-root-right", "ns-home-right");
        rightStore.Add("/Shared", "id:shared");
        rightStore.Add("/Shared/Leaf", "id:leaf");
        rightStore.Add("/Parent", "id:parent");
        rightStore.Add("/Parent/A", "id:sa");
        rightStore.Add("/Parent/B", "id:sb");
        rightStore.Add("/Recovery", "id:recovery-artifact");
        rightStore.Add("/Recovery/Child", "id:rec-child");

        // The same provider-global folder identity reached through two different account namespaces is not
        // separate evidence: the left root is bound first and the right root must prove its own ancestry.
        leftStore.Add("/Shared", "id:shared");
        var leftEvidence = await ReadDropboxEvidenceAsync(leftStore, leftSpec, "id:shared", "/Shared");
        await RegisterCloudRootAsync(authority, pair, leftSpec, "root-left-shared", SyncAuthorityCloudSides.LogicalLeft,
            [leftEvidence.ToSnapshot("root-left-shared")]);
        var nestedEvidence = await ReadDropboxEvidenceAsync(rightStore, rightSpec, "id:leaf", "/Shared/Leaf");
        var nested = await RefusedAsync(() => RegisterCloudRootAsync(authority, pair, rightSpec, "root-right-leaf",
            SyncAuthorityCloudSides.LogicalRight, [nestedEvidence.ToSnapshot("root-right-leaf")]));
        Require(nested,
            "a root whose complete chain contains the other root's provider folder id is refused even though the two accounts and namespaces differ");
        Require(await ScalarAsync(DatabasePath("relationships"),
                "SELECT count(*) FROM root_binding WHERE pair_id=$pair AND state='active';", ("$pair", pair.Id)) == 1,
            "the refused nested root leaves only the first retained root");

        // A different route (account and namespace) with no usable evidence must never be accepted as distinct.
        var otherSpec = new EndpointSpec("dbid-right", "ns-root-right", "ns-home-right", "id:other", "/Other");
        rightStore.Add("/Other", "id:other");
        Require(await RefusedAsync(() => RegisterCloudRootAsync(authority, pair, otherSpec, "root-right-other",
                SyncAuthorityCloudSides.LogicalRight)),
            "a root with no supplied chain is refused even though its account and namespace differ from the retained root");
        var otherEvidence = await ReadDropboxEvidenceAsync(rightStore, otherSpec, "id:other", "/Other");
        Require(await RefusedAsync(() => RegisterCloudRootAsync(authority, pair, otherSpec, "root-right-other",
                SyncAuthorityCloudSides.LogicalRight,
                [otherEvidence.ToSnapshot("root-right-other") with { CompleteToCorpusRoot = false }])),
            "a supplied but incomplete chain is refused rather than accepted as distinct across two routes");
        await RegisterCloudRootAsync(authority, pair, otherSpec, "root-right-other",
            SyncAuthorityCloudSides.LogicalRight,
            [otherEvidence.ToSnapshot("root-right-other"), leftEvidence.ToSnapshot("root-left-shared")]);
        Require(await ScalarAsync(DatabasePath("relationships"),
                "SELECT count(*) FROM root_binding WHERE pair_id=$pair AND state='active';", ("$pair", pair.Id)) == 2,
            "a genuinely distinct root is accepted only with complete exact-bound evidence for both routes");

        // Sibling roots that share an ancestor are provably distinct. Each side owns one root, so the two
        // siblings are the two sides of a second pair and are still compared against the first pair's root.
        var siblingAuthority = await OpenAuthorityAsync("siblings");
        await RegisterAccountAsync(siblingAuthority, siblingSpecA);
        var siblingPair = await RetainPairAsync(siblingAuthority,
            CreatePair(siblingSpecA, siblingSpecB, "CC2D sibling pair"));
        var siblingEvidenceA = await ReadDropboxEvidenceAsync(rightStore, siblingSpecA, "id:sa", "/Parent/A");
        await RegisterCloudRootAsync(siblingAuthority, siblingPair, siblingSpecA, "root-sibling-a",
            SyncAuthorityCloudSides.LogicalLeft, [siblingEvidenceA.ToSnapshot("root-sibling-a")]);
        var siblingEvidenceB = await ReadDropboxEvidenceAsync(rightStore, siblingSpecB, "id:sb", "/Parent/B");
        await RegisterCloudRootAsync(siblingAuthority, siblingPair, siblingSpecB, "root-sibling-b",
            SyncAuthorityCloudSides.LogicalRight,
            [siblingEvidenceB.ToSnapshot("root-sibling-b"), siblingEvidenceA.ToSnapshot("root-sibling-a")]);
        Require(await ScalarAsync(DatabasePath("siblings"),
                "SELECT count(*) FROM root_binding WHERE pair_id=$pair AND state='active';",
                ("$pair", siblingPair.Id)) == 2,
            "two complete chains that merely share an ancestor are both retained as distinct roots");

        // An active recovery boundary is compared with the same evidence.
        await authority.RegisterDropboxRecoveryRootAsync("recovery-root-cc2d", InstallationId,
            DerivedAccount("dbid-right", "ns-root-right", "ns-home-right"), "/Recovery", "id:recovery-artifact",
            "ns-root-right", 1);
        var recoveryChildSpec = new EndpointSpec("dbid-right", "ns-root-right", "ns-home-right", "id:rec-child",
            "/Recovery/Child");
        var recoveryEvidence = await ReadDropboxEvidenceAsync(rightStore, recoveryChildSpec, "id:rec-child",
            "/Recovery/Child");
        Require(await RefusedAsync(() => RegisterCloudRootAsync(authority, pair, recoveryChildSpec,
                "root-recovery-child", SyncAuthorityCloudSides.LogicalRight,
                [recoveryEvidence.ToSnapshot("root-recovery-child")])),
            "a pair root nested inside an active recovery boundary is refused");
        var recoveryDuplicate = new EndpointSpec("dbid-right", "ns-root-right", "ns-home-right",
            "id:recovery-artifact", "/Recovery");
        var recoveryRootEvidence = await ReadDropboxEvidenceAsync(rightStore, recoveryDuplicate,
            "id:recovery-artifact", "/Recovery");
        Require(await RefusedAsync(() => RegisterCloudRootAsync(authority, pair, recoveryDuplicate,
                "root-recovery-same", SyncAuthorityCloudSides.LogicalRight,
                [recoveryRootEvidence.ToSnapshot("root-recovery-same")])),
            "a pair root naming the exact recovery boundary folder identity is refused");
        Require(await ScalarAsync(DatabasePath("relationships"),
                "SELECT count(*) FROM root_binding WHERE state='active';") == 2,
            "every refused recovery-boundary comparison leaves the retained roots unchanged");

        // A path-opaque provider proves nothing without a real chain, and everything with one.
        var google = await OpenAuthorityAsync("google-relationship");
        var googleLeft = new EndpointSpec("perm-left", "key-left", "key-left", "gd-left-child", "/Drive Left/Child",
            SyncEndpointKind.GoogleDrive);
        var googleRight = new EndpointSpec("perm-right", "key-right", "key-right", "gd-right-child", "/Drive Right/Child",
            SyncEndpointKind.GoogleDrive);
        await RegisterAccountAsync(google, googleLeft);
        await RegisterAccountAsync(google, googleRight);
        var googlePair = await RetainPairAsync(google, CreatePair(googleLeft, googleRight, "CC2D google relationship"));
        var leftDriveWire = new DriveWire { PermissionId = "perm-left", MyDriveRootId = "gd-root-left" };
        leftDriveWire.Add("gd-root-left", []);
        leftDriveWire.Add("gd-left-child", ["gd-root-left"]);
        using var leftDriveHttp = new HttpClient(leftDriveWire);
        var leftDriveBinding = DriveBinding("perm-left", false);
        var leftDriveEvidence = await DriveClient(leftDriveHttp, leftDriveBinding, "gd-left-child")
            .ReadRootHierarchyAsync(DriveRequest(leftDriveBinding, "perm-left", "gd-left-child"));
        await RegisterCloudRootAsync(google, googlePair, googleLeft, "root-g-left",
            SyncAuthorityCloudSides.LogicalLeft, [leftDriveEvidence.ToSnapshot("root-g-left")]);
        Require(await RefusedAsync(() => RegisterCloudRootAsync(google, googlePair, googleRight, "root-g-right",
                SyncAuthorityCloudSides.LogicalRight)),
            "a path-opaque root without a supplied native chain is refused rather than assumed disjoint");
        var googleWire = new DriveWire { PermissionId = "perm-right", MyDriveRootId = "gd-root-right" };
        googleWire.Add("gd-root-right", []);
        googleWire.Add("gd-right-child", ["gd-root-right"]);
        using var googleHttp = new HttpClient(googleWire);
        var googleBinding = DriveBinding("perm-right", false);
        var googleEvidence = await DriveClient(googleHttp, googleBinding, "gd-right-child")
            .ReadRootHierarchyAsync(DriveRequest(googleBinding, "perm-right", "gd-right-child"));
        Require(await RefusedAsync(() => RegisterCloudRootAsync(google, googlePair, googleRight, "root-g-right",
                SyncAuthorityCloudSides.LogicalRight, [leftDriveEvidence.ToSnapshot("root-g-right")])),
            "a chain that is not bound to the root identity it is offered for is refused");
        Require(await RefusedAsync(() => RegisterCloudRootAsync(google, googlePair, googleRight, "root-g-right",
                SyncAuthorityCloudSides.LogicalRight, [googleEvidence.ToSnapshot("root-g-right")])),
            "a path-opaque candidate whose retained peer has no supplied chain stays unverified rather than disjoint");
        await RegisterCloudRootAsync(google, googlePair, googleRight, "root-g-right",
            SyncAuthorityCloudSides.LogicalRight,
            [googleEvidence.ToSnapshot("root-g-right"), leftDriveEvidence.ToSnapshot("root-g-left")]);
        Require(await ScalarAsync(DatabasePath("google-relationship"),
                "SELECT count(*) FROM root_binding WHERE pair_id=$pair AND state='active';", ("$pair", googlePair.Id)) == 2,
            "the real adapter chain and the real validator retain two provably distinct opaque roots");
    }

    // ----- 6. Comparison semantics over the real evidence value ----------------------------------

    private static async Task ComparisonEvidenceAsync()
    {
        Claim("CC2D-3/compare", "Complete native chains decide Same, Nested and Distinct by object identity");
        var parentWire = new DropboxWireHandler("dbid-x", "ns-x", "ns-xh");
        parentWire.Add("/Shared", "id:shared");
        var sharedEvidence = await ReadDropboxEvidenceAsyncRaw(parentWire, "dbid-x", "ns-x", "ns-xh", "id:shared",
            "/Shared");
        var parentSnapshot = sharedEvidence.ToSnapshot("root-parent");
        var parentIdentity = RootIdentity(sharedEvidence, "root-parent", SyncAuthorityCloudSides.LogicalLeft);

        var childWire = new DropboxWireHandler("dbid-y", "ns-y", "ns-yh");
        childWire.Add("/Shared", "id:shared");
        childWire.Add("/Shared/Leaf", "id:leaf");
        childWire.Add("/Shared/Sibling", "id:sibling");
        childWire.Add("/Shared/Other", "id:other");
        var leafEvidence = await ReadDropboxEvidenceAsyncRaw(childWire, "dbid-y", "ns-y", "ns-yh", "id:leaf",
            "/Shared/Leaf");
        var leafSnapshot = leafEvidence.ToSnapshot("root-leaf");
        var leafIdentity = RootIdentity(leafEvidence, "root-leaf", SyncAuthorityCloudSides.LogicalRight);
        var siblingEvidence = await ReadDropboxEvidenceAsyncRaw(childWire, "dbid-y", "ns-y", "ns-yh", "id:sibling",
            "/Shared/Sibling");
        var siblingSnapshot = siblingEvidence.ToSnapshot("root-sibling");
        var siblingIdentity = RootIdentity(siblingEvidence, "root-sibling", SyncAuthorityCloudSides.LogicalRight);
        var otherEvidence = await ReadDropboxEvidenceAsyncRaw(childWire, "dbid-y", "ns-y", "ns-yh", "id:other",
            "/Shared/Other");
        var otherSnapshot = otherEvidence.ToSnapshot("root-other");
        var otherIdentity = RootIdentity(otherEvidence, "root-other", SyncAuthorityCloudSides.LogicalRight);

        Require(parentSnapshot.IsCompleteFor(parentIdentity) && leafSnapshot.IsCompleteFor(leafIdentity) &&
                siblingSnapshot.IsCompleteFor(siblingIdentity) && !parentSnapshot.BindsTo(leafIdentity),
            "only a chain anchored at its own corpus root and ending at that root identity is complete, and evidence never binds another root");

        // 1. The same provider-global folder identity is the same object before any account or namespace.
        var sameElsewhere = new SyncAuthorityCloudRootIdentity(SyncProviders.Dropbox, "account-other", "ns-other",
            sharedEvidence.SelectedRootId, "/Elsewhere", "root-other", SyncAuthorityCloudRootIdentity.PairRootAuthority,
            SyncAuthorityCloudSides.LogicalRight);
        Require(SyncAuthorityCloudRootComparison.Compare(sameElsewhere, null, parentIdentity, parentSnapshot) ==
                SyncAuthorityCloudRootRelation.Same,
            "the same provider-global folder identity reached through another account and namespace is the same object");
        Require(SyncAuthorityCloudRootComparison.Compare(parentIdentity, parentSnapshot, sameElsewhere, null) ==
                SyncAuthorityCloudRootRelation.Same,
            "the same-object decision is not order dependent");

        // 2. A complete chain proves containment across different accounts and namespaces.
        Require(SyncAuthorityCloudRootComparison.Compare(leafIdentity, leafSnapshot, parentIdentity, parentSnapshot) ==
                SyncAuthorityCloudRootRelation.Nested,
            "a complete chain proves containment across different accounts and namespaces");
        Require(SyncAuthorityCloudRootComparison.Compare(parentIdentity, parentSnapshot, leafIdentity, leafSnapshot) ==
                SyncAuthorityCloudRootRelation.Nested,
            "the containment decision is not order dependent");
        Require(SyncAuthorityCloudRootComparison.Compare(otherIdentity, otherSnapshot, parentIdentity, parentSnapshot) ==
                SyncAuthorityCloudRootRelation.Nested,
            "any other descendant in another namespace is still contained by the globally identified ancestor");

        // 3. Two complete chains that share an ancestor but contain neither root prove distinctness.
        Require(SyncAuthorityCloudRootComparison.Compare(siblingIdentity, siblingSnapshot, otherIdentity, otherSnapshot) ==
                SyncAuthorityCloudRootRelation.Distinct,
            "two complete chains that share an ancestor but contain neither root prove distinctness");
        Require(SyncAuthorityCloudRootComparison.Compare(otherIdentity, otherSnapshot, siblingIdentity, siblingSnapshot) ==
                SyncAuthorityCloudRootRelation.Distinct,
            "the distinctness decision is not order dependent");
        var opaqueElsewhere = new SyncAuthorityCloudRootIdentity(SyncProviders.Dropbox, "account-z", "ns-z",
            "id:opaque", "/Opaque", "root-z", SyncAuthorityCloudRootIdentity.PairRootAuthority,
            SyncAuthorityCloudSides.LogicalRight);
        Require(SyncAuthorityCloudRootComparison.Compare(opaqueElsewhere, null, leafIdentity, leafSnapshot) !=
                SyncAuthorityCloudRootRelation.Distinct,
            "a different account or namespace alone never proves disjointness for a provider-global folder identity");
        Require(SyncAuthorityCloudRootComparison.Compare(opaqueElsewhere, null, leafIdentity, leafSnapshot) ==
                SyncAuthorityCloudRootRelation.Unverified,
            "two different folder identities with no complete evidence stay unverified across routes");
        Require(SyncAuthorityCloudRootComparison.Validate(opaqueElsewhere, null, leafIdentity, leafSnapshot, "right")
                .Errors.Contains(SyncEndpointError.UnverifiedCloudNamespace),
            "the route-only case is refused through the shared validator");

        // A non-null but incomplete chain is never a distinctness proof either.
        var incompleteElsewhere = new SyncAuthorityCloudHierarchySnapshot(SyncProviders.Dropbox, "account-z", "ns-z",
            "root-z", "id:opaque", [PilotPath.DropboxNamespaceRootIdentity("ns-z"), "id:opaque"]);
        Require(SyncAuthorityCloudRootComparison.Compare(opaqueElsewhere, incompleteElsewhere, leafIdentity,
                leafSnapshot) == SyncAuthorityCloudRootRelation.Unverified,
            "a supplied but incomplete chain never falls through to a distinctness decision");
        Require(SyncAuthorityCloudRootComparison.Compare(parentIdentity, null, leafIdentity, leafSnapshot) ==
                SyncAuthorityCloudRootRelation.Nested,
            "a verified complete chain still proves containment when the other side supplies no usable chain");
        // 4. Evidence that does not assert its own completeness is never used as ancestry.
        var misAnchored = new SyncAuthorityCloudHierarchySnapshot(SyncProviders.Dropbox,
            leafEvidence.ProviderAccountId, leafEvidence.NamespaceId, "root-leaf", "id:leaf",
            [PilotPath.DropboxNamespaceRootIdentity("ns-y"), "id:leaf"]);
        Require(!misAnchored.IsCompleteFor(leafIdentity),
            "a chain that does not assert completeness is never treated as complete ancestry");

        // 5. A path-opaque provider with no chain is never disjointness.
        var googleIdentity = new SyncAuthorityCloudRootIdentity(SyncProviders.GoogleDrive, "account-g", "perm-g",
            "gd-one", "/Drive", "root-g1", SyncAuthorityCloudRootIdentity.PairRootAuthority,
            SyncAuthorityCloudSides.LogicalLeft);
        var googleOther = new SyncAuthorityCloudRootIdentity(SyncProviders.GoogleDrive, "account-g", "perm-h",
            "gd-two", "/Drive Two", "root-g2", SyncAuthorityCloudRootIdentity.PairRootAuthority,
            SyncAuthorityCloudSides.LogicalRight);
        Require(SyncAuthorityCloudRootComparison.Compare(googleOther, null, googleIdentity, null) ==
                SyncAuthorityCloudRootRelation.Unverified,
            "an opaque provider with no native chain stays unverified rather than being called disjoint");
        Require(SyncAuthorityCloudRootComparison.Validate(googleOther, null, googleIdentity, null, "right")
                .Errors.Contains(SyncEndpointError.UnverifiedCloudNamespace),
            "the unverified relation is refused through the shared validator");
        var googleIncomplete = new SyncAuthorityCloudHierarchySnapshot(SyncProviders.GoogleDrive, "account-g", "perm-h",
            "root-g2", "gd-two", ["gd-one"]);
        Require(SyncAuthorityCloudRootComparison.Compare(googleOther, googleIncomplete, googleIdentity, null) ==
                SyncAuthorityCloudRootRelation.Unverified,
            "a path-opaque provider with a non-null but unbound chain is still never distinct");
    }

    // ----- 7. Endpoint binding refusals before any provider read ----------------------------------

    private static async Task BindingRefusalsAsync()
    {
        Claim("CC2D-1/binding", "A foreign provider, account, namespace or root refuses before any provider read");
        using var driveWire = new DriveWire();
        driveWire.Add("gd-root", []);
        driveWire.Add("gd-child", ["gd-root"]);
        using var driveHttp = new HttpClient(driveWire);
        var driveBinding = DriveBinding("perm-bind", false);
        var driveClient = DriveClient(driveHttp, driveBinding, "gd-child");
        Require(await RefusedAsync(() => driveClient.ReadRootHierarchyAsync(new SyncAuthorityCloudRootHierarchyRequest(
                SyncProviders.Dropbox, driveBinding.ConnectionId, DriveAccount("perm-bind"), "perm-bind",
                "gd-child", "/Selected"))),
            "a request naming another provider is refused");
        Require(await RefusedAsync(() => driveClient.ReadRootHierarchyAsync(new SyncAuthorityCloudRootHierarchyRequest(
                SyncProviders.GoogleDrive, "gdrive-connection-other", DriveAccount("perm-bind"), "perm-bind",
                "gd-child", "/Selected"))),
            "a request naming another retained connection is refused");
        Require(await RefusedAsync(() => driveClient.ReadRootHierarchyAsync(new SyncAuthorityCloudRootHierarchyRequest(
                SyncProviders.GoogleDrive, driveBinding.ConnectionId, DriveAccount("perm-bind"), "perm-other",
                "gd-child", "/Selected"))),
            "a request naming another native namespace is refused");
        Require(await RefusedAsync(() => driveClient.ReadRootHierarchyAsync(new SyncAuthorityCloudRootHierarchyRequest(
                SyncProviders.GoogleDrive, driveBinding.ConnectionId, DriveAccount("perm-other"), "perm-bind",
                "gd-child", "/Selected"))),
            "a request naming another retained provider account is refused");
        Require(await RefusedAsync(() => driveClient.ReadRootHierarchyAsync(new SyncAuthorityCloudRootHierarchyRequest(
                SyncProviders.GoogleDrive, driveBinding.ConnectionId, DriveAccount("perm-bind"), "perm-bind",
                "gd-other", "/Selected"))),
            "a request naming another provider folder identity is refused");
        Require(driveWire.AboutCalls == 0 && driveWire.NodeReads == 0 && driveWire.RootAliasCalls == 0,
            "every refused request happened before any Google provider read");

        using var dropboxWire = new DropboxWire("dbid-bind", "ns-bind", "ns-bind-home");
        dropboxWire.Add("/Shared", "id:bind");
        using var dropboxHttp = new HttpClient(dropboxWire);
        var dropboxBinding = DropboxBinding("dbid-bind", "ns-bind", "ns-bind-home");
        var dropboxClient = DropboxClient(dropboxHttp, dropboxBinding, "/Shared", "id:bind");
        Require(await RefusedAsync(() => dropboxClient.ReadRootHierarchyAsync(
                DropboxRequest(dropboxBinding, "dbid-bind", "ns-other", "ns-bind-home", "id:bind", "/Shared"))),
            "a Dropbox request naming another root namespace is refused");
        Require(await RefusedAsync(() => dropboxClient.ReadRootHierarchyAsync(
                DropboxRequest(dropboxBinding, "dbid-bind", "ns-bind", "ns-bind-home", "id:other", "/Shared"))),
            "a Dropbox request naming another folder identity is refused");
        Require(dropboxWire.AccountCalls == 0 && dropboxWire.MetadataCalls == 0,
            "every refused Dropbox request happened before any provider read");

        // An adapter without the capability fails explicitly and is never routed through a default branch.
        var store = DispatchProxy.Create<ISyncRemoteStore, NoHierarchyStore>();
        Require(await RefusedAsync(() => SyncAuthorityCloudRootHierarchy.ReadAsync(store,
                DropboxRequest(dropboxBinding, "dbid-bind", "ns-bind", "ns-bind-home", "id:bind", "/Shared"))),
            "an adapter that does not implement the capability is refused explicitly");
    }

    // ----- fixtures ------------------------------------------------------------------------------

    private sealed class DriveNode
    {
        internal required string Id { get; init; }
        internal string Name { get; init; } = "";
        internal string[] Parents { get; set; } = [];
        internal string MimeType { get; set; } = FolderMime;
        internal string? Version { get; set; } = "1";
        internal string? DriveId { get; set; }
        internal bool Trashed { get; set; }
        internal string? ShortcutTarget { get; set; }
    }

    // Deterministic Google Drive v3 metadata endpoint. It serves only account and file metadata; any
    // listing, content or effect route fails the run.
    private sealed class DriveWire : HttpMessageHandler
    {
        internal Dictionary<string, DriveNode> Nodes { get; } = new(StringComparer.Ordinal);
        internal string MyDriveRootId { get; init; } = "gd-root";
        internal string PermissionId { get; set; } = "perm-one";
        internal bool RootAliasIsAlias { get; init; }
        internal Action<DriveNode>? AfterRead { get; set; }
        internal int AboutCalls { get; private set; }
        internal int RootAliasCalls { get; private set; }
        internal int NodeReads { get; private set; }
        internal int ForbiddenCalls { get; private set; }

        internal void Add(string id, string[] parents, string? driveId = null, string mime = FolderMime,
            bool trashed = false, string? version = "1", string? shortcutTarget = null) =>
            Nodes[id] = new DriveNode
            {
                Id = id,
                Name = id,
                Parents = parents,
                MimeType = mime,
                DriveId = driveId,
                Trashed = trashed,
                Version = version,
                ShortcutTarget = shortcutTarget
            };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Method != HttpMethod.Get)
                return Task.FromResult(Forbidden("a Google Drive provider effect is not part of a hierarchy read"));
            var uri = request.RequestUri!;
            if (uri.Scheme != "https" || uri.Host != "www.googleapis.com" ||
                !uri.AbsolutePath.StartsWith("/drive/v3/", StringComparison.Ordinal))
                return Task.FromResult(Forbidden("the hierarchy read used an unexpected provider origin"));
            var route = uri.AbsolutePath["/drive/v3/".Length..];
            if (route.StartsWith("about", StringComparison.Ordinal))
            {
                AboutCalls++;
                return Task.FromResult(Json("{\"user\":{\"permissionId\":\"" + PermissionId +
                    "\",\"emailAddress\":\"fixture@example.invalid\",\"displayName\":\"Fixture\"},\"storageQuota\":{}}"));
            }
            if (route == "files/root")
            {
                RootAliasCalls++;
                var id = RootAliasIsAlias ? "root" : MyDriveRootId;
                return Task.FromResult(Json("{\"id\":\"" + id + "\",\"mimeType\":\"" + FolderMime + "\"}"));
            }
            if (route.StartsWith("files?", StringComparison.Ordinal))
                return Task.FromResult(Forbidden("an entire-drive listing is not part of a hierarchy read"));
            if (route.StartsWith("files/", StringComparison.Ordinal))
            {
                var id = Uri.UnescapeDataString(route["files/".Length..]);
                if (id.Contains('?'))
                    return Task.FromResult(Forbidden("a content read is not part of a hierarchy read"));
                if (!Nodes.TryGetValue(id, out var node))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent("{\"error\":{\"code\":404}}", Encoding.UTF8, "application/json")
                    });
                NodeReads++;
                AfterRead?.Invoke(node);
                var payload = Serialize(node);
                return Task.FromResult(Json(payload));
            }
            return Task.FromResult(Forbidden("an unexpected Google Drive route was requested"));
        }

        private static string Serialize(DriveNode node)
        {
            var builder = new StringBuilder();
            builder.Append("{\"id\":\"").Append(node.Id).Append("\",\"name\":\"").Append(node.Name).Append('"');
            if (node.Version is not null) builder.Append(",\"version\":\"").Append(node.Version).Append('"');
            builder.Append(",\"mimeType\":\"")
                .Append(node.ShortcutTarget is null ? node.MimeType : GoogleDrivePilotClient.ShortcutMimeType)
                .Append('"');
            builder.Append(",\"parents\":[").Append(string.Join(',', node.Parents.Select(Quote))).Append(']');
            builder.Append(",\"trashed\":").Append(node.Trashed ? "true" : "false");
            if (node.DriveId is not null) builder.Append(",\"driveId\":\"").Append(node.DriveId).Append('"');
            if (node.ShortcutTarget is not null)
                builder.Append(",\"shortcutDetails\":{\"targetId\":\"").Append(node.ShortcutTarget)
                    .Append("\",\"targetMimeType\":\"").Append(FolderMime).Append("\"}");
            builder.Append(",\"capabilities\":{\"canDownload\":true}}");
            return builder.ToString();
        }

        private static string Quote(string value) => "\"" + value + "\"";

        private HttpResponseMessage Forbidden(string reason)
        {
            ForbiddenCalls++;
            throw new HierarchyFixtureException("Cloud root hierarchy checks: " + reason);
        }

        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json")
        };
    }

    // Deterministic Dropbox endpoint. It serves the account read and folder metadata only; any other route
    // fails the run.
    private sealed class DropboxWire : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _byPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _byId = new(StringComparer.Ordinal);
        internal string AccountId { get; }
        internal string RootNamespaceId { get; }
        internal string HomeNamespaceId { get; }
        internal List<string> PathRootNamespaces { get; } = [];
        internal int AccountCalls { get; private set; }
        internal int MetadataCalls { get; private set; }
        internal int ForbiddenCalls { get; private set; }

        internal DropboxWire(string accountId, string rootNamespaceId, string homeNamespaceId)
        {
            AccountId = accountId;
            RootNamespaceId = rootNamespaceId;
            HomeNamespaceId = homeNamespaceId;
        }

        internal void Add(string path, string id)
        {
            var normalized = PilotPath.NormalizeDropboxRoot(path);
            _byPath[normalized] = id;
            _byId[id] = normalized;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Method != HttpMethod.Post)
                return Forbidden("a Dropbox provider effect is not part of a hierarchy read");
            if (request.Headers.TryGetValues("Dropbox-API-Path-Root", out var roots))
            {
                using var pathRoot = JsonDocument.Parse(roots.Single());
                PathRootNamespaces.Add(pathRoot.RootElement.GetProperty("root").GetString() ?? string.Empty);
            }
            var route = request.RequestUri!.AbsolutePath;
            if (!string.Equals(request.RequestUri.Host, "api.dropboxapi.com", StringComparison.Ordinal))
                return Forbidden("the hierarchy read used an unexpected provider origin");
            if (route.EndsWith("/users/get_current_account", StringComparison.Ordinal))
            {
                AccountCalls++;
                return Json("{\"account_id\":\"" + AccountId + "\",\"root_info\":{\"root_namespace_id\":\"" +
                            RootNamespaceId + "\",\"home_namespace_id\":\"" + HomeNamespaceId + "\"}}");
            }
            if (route.EndsWith("/files/get_metadata", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var path = body.RootElement.GetProperty("path").GetString() ?? string.Empty;
                MetadataCalls++;
                string? id = null;
                string? display = null;
                if (path.StartsWith("id:", StringComparison.Ordinal))
                {
                    if (_byId.TryGetValue(path, out var found)) { id = path; display = found; }
                }
                else if (_byPath.TryGetValue(path, out var foundId))
                {
                    id = foundId;
                    display = PilotPath.NormalizeDropboxRoot(path);
                }
                if (id is null || display is null)
                    return new HttpResponseMessage(HttpStatusCode.Conflict)
                    {
                        Content = new StringContent("{\"error_summary\":\"not_found\"}", Encoding.UTF8,
                            "application/json")
                    };
                var name = display == "/" ? "" : display[(display.LastIndexOf('/') + 1)..];
                return Json("{\".tag\":\"folder\",\"name\":\"" + name + "\",\"id\":\"" + id +
                            "\",\"path_lower\":\"" + display.ToLowerInvariant() + "\",\"path_display\":\"" +
                            display + "\"}");
            }
            return Forbidden("an unexpected Dropbox route was requested");
        }

        private HttpResponseMessage Forbidden(string reason)
        {
            ForbiddenCalls++;
            throw new HierarchyFixtureException("Cloud root hierarchy checks: " + reason);
        }

        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json")
        };
    }

    // A reusable Dropbox wire for relationship proofs, kept as a handler so the real adapter stays reachable.
    private sealed class DropboxWireHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _byPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _byId = new(StringComparer.Ordinal);
        private readonly string _accountId;
        private readonly string _rootNamespaceId;
        private readonly string _homeNamespaceId;

        internal DropboxWireHandler(string accountId = "dbid-left", string rootNamespaceId = "ns-root-left",
            string homeNamespaceId = "ns-home-left")
        {
            _accountId = accountId;
            _rootNamespaceId = rootNamespaceId;
            _homeNamespaceId = homeNamespaceId;
        }

        internal void Add(string path, string id)
        {
            var normalized = PilotPath.NormalizeDropboxRoot(path);
            _byPath[normalized] = id;
            _byId[id] = normalized;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Method != HttpMethod.Post)
                throw new HierarchyFixtureException(
                    "Cloud root hierarchy checks: a Dropbox provider effect is not part of a hierarchy read");
            var route = request.RequestUri!.AbsolutePath;
            if (route.EndsWith("/users/get_current_account", StringComparison.Ordinal))
                return Json("{\"account_id\":\"" + _accountId + "\",\"root_info\":{\"root_namespace_id\":\"" +
                            _rootNamespaceId + "\",\"home_namespace_id\":\"" + _homeNamespaceId + "\"}}");
            if (route.EndsWith("/files/get_metadata", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var path = body.RootElement.GetProperty("path").GetString() ?? string.Empty;
                string? id = null;
                string? display = null;
                if (path.StartsWith("id:", StringComparison.Ordinal))
                {
                    if (_byId.TryGetValue(path, out var found)) { id = path; display = found; }
                }
                else if (_byPath.TryGetValue(path, out var foundId))
                {
                    id = foundId;
                    display = PilotPath.NormalizeDropboxRoot(path);
                }
                if (id is null || display is null)
                    return new HttpResponseMessage(HttpStatusCode.Conflict)
                    {
                        Content = new StringContent("{\"error_summary\":\"not_found\"}", Encoding.UTF8,
                            "application/json")
                    };
                var name = display == "/" ? "" : display[(display.LastIndexOf('/') + 1)..];
                return Json("{\".tag\":\"folder\",\"name\":\"" + name + "\",\"id\":\"" + id +
                            "\",\"path_lower\":\"" + display.ToLowerInvariant() + "\",\"path_display\":\"" +
                            display + "\"}");
            }
            throw new HierarchyFixtureException(
                "Cloud root hierarchy checks: an unexpected Dropbox route was requested");
        }

        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json")
        };
    }

    private sealed class SyntheticTokens : IGoogleDriveAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult("synthetic-not-a-credential");
        }
    }

    private sealed class FixedTokens : IDropboxAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult("synthetic-not-a-credential");
        }
    }

    // An ISyncRemoteStore that does not implement the hierarchy capability.
    public class NoHierarchyStore : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException("This fixture store has no provider behaviour.");
    }

    private sealed class HierarchyFixtureException(string message) : Exception(message);

    // The root identity a real evidence value describes, for direct comparison proofs.
    private static SyncAuthorityCloudRootIdentity RootIdentity(SyncAuthorityCloudRootHierarchyEvidence evidence,
        string rootBindingId, string side) =>
        new(evidence.Provider, evidence.ProviderAccountId, evidence.NamespaceId, evidence.SelectedRootId,
            evidence.FolderPath, rootBindingId, SyncAuthorityCloudRootIdentity.PairRootAuthority, side);

    private static GoogleDrivePilotClient DriveClient(HttpClient http, GoogleDriveConnectionBinding binding,
        string rootFolderId, string? sharedDriveId = null) =>
        new(http, new SyntheticTokens(), binding, rootFolderId, "/Selected", sharedDriveId: sharedDriveId,
            allowSharedDrives: binding.SharedDrivesEnabled);

    private static GoogleDriveConnectionBinding DriveBinding(string permissionId, bool shared) =>
        new GoogleDriveConnectionBinding(GoogleDriveProviderConfiguration.RegisteredClientId,
            GoogleDriveConnectionBinding.CreateAccountKey(permissionId), permissionId, "fixture@example.invalid",
            "Synthetic cloud root hierarchy account", [GoogleDriveProviderConfiguration.DriveScope],
            CredentialReference: "synthetic-only", SharedDrivesEnabled: shared).Normalize();

    private static string DriveAccount(string permissionId) =>
        SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.GoogleDrive, permissionId,
            GoogleDriveConnectionBinding.CreateAccountKey(permissionId),
            GoogleDriveConnectionBinding.CreateAccountKey(permissionId));

    private static SyncAuthorityCloudRootHierarchyRequest DriveRequest(GoogleDriveConnectionBinding binding,
        string permissionId, string folderId) =>
        new(SyncProviders.GoogleDrive, binding.ConnectionId, DriveAccount(permissionId), permissionId, folderId,
            "/Selected");

    private static DropboxConnectionBinding DropboxBinding(string accountId, string rootNamespaceId,
        string homeNamespaceId) =>
        new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId, accountId, rootNamespaceId,
            homeNamespaceId,
            DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes).Normalize();

    private static string DerivedAccount(string accountId, string rootNamespaceId, string homeNamespaceId) =>
        SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.Dropbox, accountId, rootNamespaceId,
            homeNamespaceId);

    private static SyncAuthorityCloudRootHierarchyRequest DropboxRequest(DropboxConnectionBinding binding,
        string accountId, string rootNamespaceId, string homeNamespaceId, string folderId, string folderPath) =>
        new(SyncProviders.Dropbox, binding.ConnectionId, DerivedAccount(accountId, rootNamespaceId, homeNamespaceId),
            rootNamespaceId, folderId, folderPath);

    private static DropboxPilotClient DropboxClient(HttpClient http, DropboxConnectionBinding binding, string root,
        string rootId) => new(http, new FixedTokens(), root, binding, rootId);

    private static async Task<SyncAuthorityCloudRootHierarchyEvidence> ReadDropboxEvidenceAsync(
        DropboxWireHandler wire, EndpointSpec spec, string folderId, string folderPath)
    {
        using var http = new HttpClient(wire);
        var binding = DropboxBinding(spec.AccountId, spec.RootNamespaceId, spec.HomeNamespaceId);
        return await DropboxClient(http, binding, folderPath, folderId).ReadRootHierarchyAsync(
            DropboxRequest(binding, spec.AccountId, spec.RootNamespaceId, spec.HomeNamespaceId, folderId,
                folderPath));
    }

    private static async Task<SyncAuthorityCloudRootHierarchyEvidence> ReadDropboxEvidenceAsyncRaw(
        DropboxWireHandler wire, string accountId, string rootNamespaceId, string homeNamespaceId, string folderId,
        string folderPath)
    {
        using var http = new HttpClient(wire);
        var binding = DropboxBinding(accountId, rootNamespaceId, homeNamespaceId);
        return await DropboxClient(http, binding, folderPath, folderId).ReadRootHierarchyAsync(
            DropboxRequest(binding, accountId, rootNamespaceId, homeNamespaceId, folderId, folderPath));
    }

    private static async Task<bool> RefusedAsync(Func<Task> action)
    {
        try
        {
            await action();
            return false;
        }
        catch (HierarchyFixtureException)
        {
            throw;
        }
        catch (Exception error) when (error is SyncAuthorityCloudRootHierarchyException or
                                          GoogleDriveBlockedException or GoogleDriveApiException or
                                          DropboxApiException or HttpRequestException or IOException or
                                          InvalidDataException or NotSupportedException or
                                          OperationCanceledException or ArgumentException or
                                          InvalidOperationException or FormatException)
        {
            return true;
        }
    }

    private static void Claim(string id, string label) => Cases.Add(new Dictionary<string, object?>
    {
        ["id"] = id,
        ["label"] = label,
        ["assertions"] = 0
    });

    private static void Require(bool condition, string reason)
    {
        _assertions++;
        Cases[^1]["assertions"] = (int)Cases[^1]["assertions"]! + 1;
        if (!condition) throw new HierarchyFixtureException("Cloud root hierarchy checks: " + reason);
    }

    // ----- real authority database fixtures ------------------------------------------------------

    private sealed record EndpointSpec(string AccountId, string RootNamespaceId, string HomeNamespaceId,
        string FolderId, string FolderPath, SyncEndpointKind Kind = SyncEndpointKind.Dropbox)
    {
        internal string Provider => Kind == SyncEndpointKind.Dropbox ? SyncProviders.Dropbox : SyncProviders.GoogleDrive;

        internal string EndpointNamespaceId => Kind == SyncEndpointKind.Dropbox ? RootNamespaceId : AccountId;

        internal string ProviderAccountId => Kind == SyncEndpointKind.Dropbox
            ? DerivedAccount(AccountId, RootNamespaceId, HomeNamespaceId)
            : DriveAccount(AccountId);

        internal string ConnectionId => Kind == SyncEndpointKind.Dropbox
            ? DropboxConnectionBinding.CreateConnectionId(AccountId)
            : GoogleDriveConnectionBinding.CreateConnectionId(
                GoogleDriveConnectionBinding.CreateAccountKey(AccountId));
    }

    private static SyncPair CreatePair(EndpointSpec left, EndpointSpec right, string name) =>
        SyncCloudPairConfiguration.CreateDraft(new SyncPairEndpoints(Endpoint(left), Endpoint(right),
            SyncMode.TwoWay), name);

    private static SyncEndpoint Endpoint(EndpointSpec spec) => SyncEndpoint.Cloud(spec.Kind, spec.ConnectionId,
        spec.EndpointNamespaceId, spec.FolderId, spec.FolderPath);

    private static async Task<SyncAuthorityDatabase> OpenAuthorityAsync(string caseName)
    {
        var directory = Path.Combine(_root, caseName);
        Directory.CreateDirectory(directory);
        var authority = new SyncAuthorityDatabase(Path.Combine(directory, "syncshark.db"));
        await authority.InitializeAsync(InstallationId, "profile-cloud-root-hierarchy-316", SourceRevision, "{}");
        return authority;
    }

    private static string DatabasePath(string caseName) => Path.Combine(_root, caseName, "syncshark.db");

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

    private static async Task<long> ScalarAsync(string databasePath, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        var scalar = await command.ExecuteScalarAsync();
        return scalar is null or DBNull ? 0 : Convert.ToInt64(scalar, System.Globalization.CultureInfo.InvariantCulture);
    }
}

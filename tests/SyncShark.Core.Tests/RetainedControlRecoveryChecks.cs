using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// RC639-7 proof for the retained-control recovery tail. A tiny real ORIGINAL authority database is created
// through the real native allocator and then sealed into a single pinned file (checkpointed, pools cleared and
// integrity-checked), and a fresh CURRENT database is created for the same installation, pair and physical
// root so its deterministic private-staging allocation really collides with the owned object. The pinned
// derivation, the authentic held-native reopen, the collision validation, the scoped resolver, the refusal
// cases and the exact replay all run through production entry points; only the tiny fixture input is
// fixture-supplied. No live archive, database or provider state is touched.
[SupportedOSPlatform("windows")]
internal static class RetainedControlRecoveryChecks
{
    private const string Installation = "rc639-retained-recovery";
    private const string OtherInstallation = "rc639-other-installation";
    private const string Profile = "profile-rc639-recovery";
    private static int _assertions;

    internal static async Task RunAsync(string root)
    {
        root = Path.GetFullPath(root);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException("Use an empty isolated retained-control-recovery fixture root.");
        Directory.CreateDirectory(root);
        _assertions = 0;
        var cases = new List<string>();
        await PositiveAsync(Path.Combine(root, "positive"));
        cases.Add("pinned derivation, held-native reopen, scoped resolver, restart and exact replay");
        await HostInitialCliAsync(Path.Combine(root, "host-initial-cli"));
        cases.Add("the ACTUAL registry/dispatcher pairs recover-control --draft --yes drives a fresh unpublished local/cloud candidate to its initial publication, with fresh pre-recovery zero-write refusals, a stale-revision refusal and a published replay");
        await HostReversedAsync(Path.Combine(root, "host-reversed"));
        await HostPublicationDriftCorrectedAsync(Path.Combine(root, "host-publication-drift"));
        await ExpectationRefusalsAsync(Path.Combine(root, "expectation-refusals"));
        cases.Add("the explicit expectation contract is enforced on the real DB: a supplied wrong revision and a supplied published expectation both refuse with the retained metadata unchanged, and an exact unpublished expectation succeeds and replays");
        cases.Add("a REAL permitted publication-mode drift is refused inside the retention transaction with the exact mismatch, and the callback mutation is verified inside the callback");
        cases.Add("a legitimate REVERSED local/cloud pair (cloud left, local right, normal derived id) completes recovery and ordinary publication through the shared Host action with the same identity and no effect");
        await HostAuthoringAsync(Path.Combine(root, "host-authoring"));
        cases.Add("the shared Host recovery drives the unpublished local/cloud candidate to a successful publication over a synthetic provider transport");
        await RecoveredBlockedOwnRootAsync(Path.Combine(root, "host-recovered-blocked"));
        await RecoveredBlockedOwnRootAsync(Path.Combine(root, "host-recovered-prior-build-profile"), true);
        await CrossBuildRefusalsAsync(Path.Combine(root, "cross-build-refusals"));
        cases.Add("R686 refusal: actual allocator rejects wrong caller installation, root and valid-but-different namespace with all authority rows and fixture files unchanged");
        cases.Add("R686 refusal: tampered recovery digest cannot authorize old-profile allocation; generic deterministic equality still refuses without effects");
        cases.Add("R686 refusal: replaced native marker and replaced native control directory are rejected even when their contents are identical, without effects");
        cases.Add("R686 refusal: genuine retained recovery authority from another pair cannot authorize this pair; both fixtures remain unchanged");
        cases.Add("R686: the recovered publication where the retained allocate_control operation carries a DIFFERENT prior-build capability_profile_id (source a506e3b6, identical capabilities JSON) - baseline collides on CreateOperationAsync; the fix reopens before allocation registration");
        cases.Add("R668: a real unpublished local/cloud candidate whose OWN local root is left in the exact BLOCKED state by the real release transition is published once by the guarded recovery path and replays without duplicating the recovered binding");
        await RecoveredBlockedRefusalsAsync(Path.Combine(root, "host-recovered-refusals"));
        cases.Add("R668: a changed display path, a changed own root identity, a stale current revision and a conflicting second own root are all refused with the own root still blocked and no pair definition published");
        await RecoveredGoogleBlockedOwnRootsAsync(Path.Combine(root, "google-recovered-blocked"));
        cases.Add("R675: the real normal GOOGLE recovered publication starts from BOTH own blocked roots with the retained-control metadata already committed, publishes the SAME pair once and activates both exact preimages");
        await RecoveredGoogleSecondSideAsync(Path.Combine(root, "google-recovered-second-side"));
        cases.Add("R675: a conflicting typed Google cloud root refuses inside the recovered-publication transaction and rolls back the local activation with no pair definition published");
        await RecoveredGoogleRevisionInvariantAsync(Path.Combine(root, "google-recovered-revision"));
        cases.Add("R675: a non-1 unpublished recovered revision is refused with the roots, configuration and recovered metadata unchanged");
        await RecoveredGoogleReversedAsync(Path.Combine(root, "google-recovered-reversed"));
        cases.Add("R675: the REVERSED Google placement (cloud left, local right) completes the same recovered publication with the same side normalization");
        await RecoveredGoogleWrongIdentityAsync(Path.Combine(root, "google-recovered-wrong-identity"));
        cases.Add("R675: a wrong Google account, endpoint namespace or carried pair id refuses before any new recovery record with no extra effect");
        cases.Add("the family-aware Host resolver selects the LOCAL root of a local/cloud pair on the pair's own local compatibility port and refuses a pair with no cloud endpoint");


        await PreRecoveryAsync(Path.Combine(root, "pre-recovery"));
        cases.Add("before recovery the pair has no scoped ownership and no reopened control");
        await RejectAsync(root, "changed-source", null, f => f.Db.RetainRecoveredControlAsync(
                new SyncAuthorityDatabase.RetainedControlSource(f.PriorPath, new string('a', 64), f.Pair,
                    "local", "root-child-local"), f.Root),
            "changed source");
        cases.Add("changed pinned source refuses");
        await RejectAsync(root, "wrong-selected-root", null, f => f.Db.RetainRecoveredControlAsync(
                new SyncAuthorityDatabase.RetainedControlSource(f.PriorPath, f.SourceSha256, f.Pair,
                    "local", "root-child-local"), f.ParentRoot),
            "physically corresponds to the held root");
        cases.Add("wrong held selected root refuses");
        await RejectAsync(root, "unpinned-wal", async f =>
            await File.WriteAllTextAsync(f.PriorPath + "-wal", "unpinned"),
            f => f.Db.RetainRecoveredControlAsync(new SyncAuthorityDatabase.RetainedControlSource(
                f.PriorPath, f.SourceSha256, f.Pair, "local", "root-child-local"), f.Root),
            "unbound WAL");
        cases.Add("unpinned WAL sidecar refuses");
        await RejectAsync(root, "active-lease", async f =>
            await ExecuteAsync(f.Db.DatabasePath,
                "INSERT INTO lease(lease_id,scope_kind,scope_id,worker_id,fencing_token,acquired_utc,expires_utc," +
                "status) VALUES('lease-extra-active','pair','" + f.Pair + "','fixture',1," +
                "strftime('%Y-%m-%dT%H:%M:%fZ','now'),strftime('%Y-%m-%dT%H:%M:%fZ','now','+1 hour'),'active');"),
            f => f.Db.RetainRecoveredControlAsync(new SyncAuthorityDatabase.RetainedControlSource(
                f.PriorPath, f.SourceSha256, f.Pair, "local", "root-child-local"), f.Root),
            "immutable non-creating collision");
        cases.Add("an active lease on the pair refuses");
        await RejectAsync(root, "other-unresolved-work", async f =>
        {
            await f.Db.RegisterObjectAsync("object-extra-work", "directory");
            await f.Db.CreateOperationAsync(new SyncAuthorityOperationPlan("operation-extra-work",
                "idempotency-extra-work", SyncAuthorityOperationKind.Cleanup, f.Pair, "object-extra-work", 1,
                Profile, "{}", 1,
                [new SyncAuthorityMember("member-extra-work", f.Pair, "object-extra-work", "lock",
                    "root-child-local", null, null, null, null, "{}")], [], []), admission: f.Admission);
        }, f => f.Db.RetainRecoveredControlAsync(new SyncAuthorityDatabase.RetainedControlSource(
                f.PriorPath, f.SourceSha256, f.Pair, "local", "root-child-local"), f.Root),
            "planning-referenced pair work remains");
        cases.Add("other unresolved pair work refuses");
        await RejectAsync(root, "ambiguous-collision", async f =>
        {
            await f.Db.RegisterObjectAsync("object-extra-control", "directory");
            await f.Db.CreateOperationAsync(new SyncAuthorityOperationPlan("operation-extra-control",
                "idempotency-extra-control", SyncAuthorityOperationKind.AllocateControl, f.Pair,
                "object-extra-control", 1, Profile, "{}", 1,
                [new SyncAuthorityMember("member-extra-control", f.Pair, "object-extra-control", "lock",
                    "root-child-local", null, null, null, null, "{}")], [], []), admission: f.Admission);
        }, f => f.Db.RetainRecoveredControlAsync(new SyncAuthorityDatabase.RetainedControlSource(
                f.PriorPath, f.SourceSha256, f.Pair, "local", "root-child-local"), f.Root),
            "exactly one current private staging allocation operation");
        cases.Add("an ambiguous second allocation refuses");
        await FencedAppendAsync(Path.Combine(root, "fenced-append"));
        cases.Add("the lawful fenced append path cannot add an attempt to the sealed collision without its own evidence");
        await RetiredRootAsync(Path.Combine(root, "retired-root"));
        await SourceHardlinkAsync(Path.Combine(root, "source-hardlink"));
        cases.Add("a hardlinked pinned source refuses through the held single-link guard with no effect");
        await ControlReparseAsync(Path.Combine(root, "control-reparse"));
        cases.Add("a reparse-point control directory is rejected with no effect");
        cases.Add("a retired or superseded current root refuses before any recovered metadata is written");
        await ForeignSourceAsync(Path.Combine(root, "foreign-source"));
        cases.Add("a source database from another installation refuses");
        await File.WriteAllTextAsync(Path.Combine(root, "retained-control-recovery-checks.json"),
            JsonSerializer.Serialize(new
            {
                assertions = _assertions,
                cases,
                // Honest labelling: only the DB-level entry points actually invoked by this suite are listed
                // as exercised. The Host publication path, the two publication guards and the CLI surface are
                // listed separately as planned and NOT exercised here; assertion counts never close a path.
                exercisedEntryPoints = new[]
                {
                    "SyncAuthorityDatabase.RetainRecoveredControlAsync (pinned derivation, held-native marker-guarded reopen, in-write recheck, history insert, internal typed registration)",
                    "SyncAuthorityDatabase.ReopenRetainedControlAsync and HasRecoveredControlOwnershipAsync (central scoped resolver at the DB level)",
                    "SyncControlNamespace.VerifyLocalMarker held-guard overload"
                    ,"SyncAuthorityRuntimeHost.RecoverRetainedControlAndAuthorAsync normal recovered Host publication (reached by HostTargetAsync, HostAuthoringAsync, RecoveredBlockedOwnRootAsync and the R686 cross-build case; the baseline failure stack proves it is entered)"
                    ,"SyncAuthorityRuntimeHost BindEndpointPairAsync recovered reopen before the allocator fallback (reached by the recovered publication cases)"
                    ,"The pairs recover-control CLI surface (HostInitialCliAsync drives the actual SyncCliRegistry.Parse + DispatchAsync pairs recover-control --draft --yes)"
                },
                plannedNotExercisedEntryPoints = new[]
                {
                    // R689: classified INDIVIDUALLY from the actual assertion and entry-point evidence.
                    // Only these two remain genuinely unproved: no assertion isolates either guard.
                    "SyncAuthorityDatabase.RequireNoPublishBlockingWorkAsync recovered exclusion (no assertion isolates this guard; remains unproved)",
                    "SyncAuthorityDatabase.RequireCandidateOperationsAsync recovered exclusion (no assertion isolates this guard; remains unproved)"
                },
                productionDataTouched = false
            }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS retained control recovery: " + _assertions + " assertions, " + cases.Count +
            " cases.");
    }

    private static async Task PositiveAsync(string root)
    {
        using var f = await Fixture.CreateAsync(root, Installation, Installation);
        var controlBefore = await ControlSnapshotAsync(f.Db.DatabasePath);
        var markerHash = f.MarkerHash();
        var outcome = await f.Db.RetainRecoveredControlAsync(new SyncAuthorityDatabase.RetainedControlSource(
            f.PriorPath, f.SourceSha256, f.Pair, "local", "root-child-local"), f.Root);
        Assert(!outcome.Replayed, "first recovery is not a replay");
        Assert(outcome.CollisionDigest.Length == 64, "recovery rebinds a compact 64-hex collision digest");
        Assert(outcome.RootGeneration == f.Generation, "recovery binds the held root generation");
        Assert(controlBefore == await ControlSnapshotAsync(f.Db.DatabasePath),
            "operation, member, allocation, attempt, receipt and lease rows stay byte-identical");
        Assert(markerHash == f.MarkerHash(), "the physical owner marker is unchanged");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT count(*) FROM history_event WHERE event_kind='retained_control_recovered';") == "1",
            "exactly one retained_control_recovered history record is appended");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT count(*) FROM control_namespace WHERE status='active';") == "1",
            "the retained active control namespace is registered");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT count(*) FROM pair_control_namespace WHERE status='active';") == "1",
            "the retained pair control-namespace binding is registered");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT count(*) FROM control_namespace WHERE namespace_allocation_id=(SELECT namespace_allocation_id " +
            "FROM namespace_allocation WHERE status='blocked');") == "1",
            "the recovered control is anchored on the inert blocked current allocation");
        Assert(await f.Db.HasRecoveredControlOwnershipAsync(f.Pair, "local", "root-child-local", f.Generation),
            "the scoped resolver resolves the exact pair, side, root and generation");
        Assert(!await f.Db.HasRecoveredControlOwnershipAsync(f.Pair, "local", "root-child-cloud", f.Generation),
            "the scoped resolver does not resolve a different root of the same pair");
        Assert(!await f.Db.HasRecoveredControlOwnershipAsync(f.Pair, "local", "root-child-local", f.Generation + 1),
            "the scoped resolver does not resolve a different root generation");
        using (var reopened = await f.Db.ReopenRetainedControlAsync(f.Pair, "local", "root-child-local",
                   f.Generation, f.Root))
        {
            Assert(string.Equals(reopened.Path, f.ControlPath, StringComparison.OrdinalIgnoreCase),
                "the authentic reopen returns the exact owned private staging root");
            Assert(Directory.EnumerateFileSystemEntries(f.ControlPath).Count() == 1,
                "the reopen creates no directory and writes no extra entry");
        }
        Assert(markerHash == f.MarkerHash(), "the reopen writes no marker");
        // A fresh database object over the same file models restart: the retained authority and the authentic
        // reopen must still resolve with no new row and no filesystem effect.
        var restarted = new SyncAuthorityDatabase(f.Db.DatabasePath);
        Assert(await restarted.HasRecoveredControlOwnershipAsync(f.Pair, "local", "root-child-local", f.Generation),
            "after restart the scoped resolver still resolves the recovered ownership");
        using (var reopened = await restarted.ReopenRetainedControlAsync(f.Pair, "local", "root-child-local",
                   f.Generation, f.Root))
            Assert(string.Equals(reopened.Path, f.ControlPath, StringComparison.OrdinalIgnoreCase),
                "after restart the authentic reopen still returns the exact owned root");
        Assert(markerHash == f.MarkerHash(), "restart reuse writes no marker");
        var retainedRows = await AllSnapshotAsync(f.Db.DatabasePath);
        await restarted.EnsureReviewedRunCanStartAsync(f.Pair, CancellationToken.None);
        await restarted.EnsureFirstSyncCanFinalizeAsync(f.Pair, CancellationToken.None);
        Assert(retainedRows == await AllSnapshotAsync(f.Db.DatabasePath),
            "reviewed execution and completion recognise exact recovered ownership without rewriting the blocked collision");
        var replay = await f.Db.RetainRecoveredControlAsync(new SyncAuthorityDatabase.RetainedControlSource(
            f.PriorPath, f.SourceSha256, f.Pair, "local", "root-child-local"), f.Root);
        Assert(replay.Replayed, "the exact same source replays as a replay");
        Assert(replay.ControlNamespaceId == outcome.ControlNamespaceId &&
               replay.CollisionDigest == outcome.CollisionDigest &&
               replay.RootGeneration == outcome.RootGeneration &&
               replay.HistoryEventId == outcome.HistoryEventId,
            "the replay returns the same control, digest, generation and exact history event");
        Assert(retainedRows == await AllSnapshotAsync(f.Db.DatabasePath),
            "the replay adds no duplicate history, binding or effect");
        await RefuseAsync(async () =>
        {
            using var wrong = await f.Db.ReopenRetainedControlAsync(f.Pair, "local", "root-child-local",
                f.Generation, f.ParentRoot);
        }, "identity verification");
        Assert(markerHash == f.MarkerHash(), "a refused reopen changes no physical control");

        // Actual Child cancellation left ready downloads beside this recovered collision.
        // Normal retry admission must retire only unissued plans before authenticating
        // the recovered control, not require the user to reset the database.
        await restarted.RegisterObjectAsync("object-reviewed-unissued", "file");
        await restarted.CreateOperationAsync(new SyncAuthorityOperationPlan("operation-reviewed-unissued",
            "idempotency-reviewed-unissued", SyncAuthorityOperationKind.CreateLocal, f.Pair,
            "object-reviewed-unissued", 1, Profile, "{}", 1,
            [new SyncAuthorityMember("member-reviewed-unissued", f.Pair, "object-reviewed-unissued",
                "destination", "root-child-local", null, null, null, "queued-download.txt", "{\"present\":false}")], [], []), admission: f.Admission);
        Assert((await restarted.LoadOperationAsync("operation-reviewed-unissued")).Status ==
            SyncAuthorityOperationStatus.Ready, "interrupted reviewed download is retained ready before admission");
        await restarted.EnsureReviewedRunCanStartAsync(f.Pair, CancellationToken.None);
        await restarted.EnsureReviewedRunCanStartAsync(f.Pair, CancellationToken.None);
        var retired = await restarted.LoadOperationAsync("operation-reviewed-unissued");
        Assert(retired.Status == SyncAuthorityOperationStatus.Superseded && retired.Attempts.Count == 0,
            "retry retires the exact unissued reviewed download without dispatch or reset");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT count(*) FROM history_event WHERE operation_id='operation-reviewed-unissued' AND event_kind='unissued_lease_recovered';") == "1",
            "repeated retry retains one immutable no-effect retirement event");
        Assert(await restarted.HasRecoveredControlOwnershipAsync(f.Pair, "local", "root-child-local", f.Generation)
            && markerHash == f.MarkerHash(), "retry preserves the exact recovered control and native marker");
        await restarted.RegisterObjectAsync("object-reviewed-cleanup", "file");
        await restarted.CreateOperationAsync(new SyncAuthorityOperationPlan("operation-reviewed-cleanup",
            "idempotency-reviewed-cleanup", SyncAuthorityOperationKind.Cleanup, f.Pair,
            "object-reviewed-cleanup", 1, Profile, "{}", 1,
            [new SyncAuthorityMember("member-reviewed-cleanup", f.Pair, "object-reviewed-cleanup",
                "lock", "root-child-local", null, null, null, null, "{}")], [], []), admission: f.Admission);
        var unresolvedBefore = await AllSnapshotAsync(f.Db.DatabasePath);
        await RefuseAsync(() => restarted.EnsureReviewedRunCanStartAsync(f.Pair, CancellationToken.None),
            "planning-referenced pair work remains");
        Assert(unresolvedBefore == await AllSnapshotAsync(f.Db.DatabasePath),
            "nonstandard unresolved cleanup still refuses and retains every authority row unchanged");
    }

    /// <summary>
    /// LC653-3/5 (first leg): the family-aware Host recovery target. A local/Dropbox draft resolves to the LOCAL
    /// root identity on the pair's own local compatibility port, derived by the SAME shared
    /// LocalRecoveryRootId the normal authoring uses, and a pair with no cloud endpoint is refused rather than
    /// coerced into a cloud projection. The full publication leg needs the provider stub and lands with LC653-5.
    /// </summary>
    private static async Task HostTargetAsync(string root)
    {
        using var f = await Fixture.CreateHostAsync(root);
        var local = SyncEndpoint.Local(f.Local, new SyncLocalEndpointIdentity(
            f.Root.Identity.VolumeSerialNumber, f.Root.Identity.FileId));
        var cloud = SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, "conn-rc639-host", "id:rc639-child",
            "/Recovery/Child");
        var draft = SyncPairStore.NormalizePair(new SyncPair(f.Pair, "rc639 local cloud", f.Local,
            f.Root.Identity.VolumeSerialNumber, f.Root.Identity.FileId, "/Recovery/Child", "id:rc639-child",
            Enabled: false, Direction: SyncMode.TwoWay, InitialReviewApproved: false, Provider: "dropbox",
            ConnectionId: "conn-rc639-host")
        {
            EndpointSchemaVersion = SyncEndpointSchema.SymmetricEndpoints,
            LeftEndpoint = SyncEndpointRecord.From(local),
            RightEndpoint = SyncEndpointRecord.From(cloud)
        });
        var target = await f.Host!.ResolveRetainedControlRecoveryTargetAsync(draft, f.Root);
        Assert(target.Side == "local",
            "the family-aware resolver selects the LOCAL side of a local/cloud pair");
        Assert(target.RootBindingId == f.LocalRootId,
            "the resolver target is the exact retained local root identity the normal authoring derives");
        // A pair with no cloud endpoint (two local endpoints) is not a supported recovery family and refuses
        // rather than being coerced into a cloud projection.
        var bothLocal = draft with
        {
            EndpointSchemaVersion = SyncEndpointSchema.SymmetricEndpoints,
            LeftEndpoint = SyncEndpointRecord.From(local),
            RightEndpoint = SyncEndpointRecord.From(local)
        };
        var familyRefused = false;
        try
        {
            await f.Host!.ResolveRetainedControlRecoveryTargetAsync(bothLocal, f.Root);
        }
        catch (NotSupportedException)
        {
            familyRefused = true;
        }
        Assert(familyRefused,
            "a pair with no cloud endpoint is refused rather than coerced into a cloud projection");
        // LC653-5: the shared Host recovery commits the ownership metadata FIRST and only then runs the guarded
        // ordinary authoring. With no registered provider connection the authoring leg refuses, so the metadata
        // commit is the resumable safe boundary: exactly one authenticated recovered binding exists, the current
        // collision records stay byte-identical and the original native control marker is unchanged.
        var collisionBefore = await ControlSnapshotAsync(f.Db.DatabasePath);
        var markerHash = f.MarkerHash();
        var rootsBefore = await ScalarAsync(f.Db.DatabasePath, "SELECT count(*) FROM root_binding WHERE pair_id='" +
            f.Pair + "';");
        Exception? authoringFailure = null;
        try
        {
            await f.Host!.RecoverRetainedControlAndAuthorAsync(draft,
                new SyncAuthorityDatabase.RetainedControlSource(f.PriorPath, f.SourceSha256, f.Pair, target.Side,
                    target.RootBindingId), f.Root, replacingPairId: null, expectedPolicyRevision: null);
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or IOException or
                                          NotSupportedException or HttpRequestException)
        {
            authoringFailure = error;
        }
        Assert(authoringFailure is not null,
            "the guarded ordinary authoring leg refuses without registered provider replies");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT count(*) FROM history_event WHERE event_kind='retained_control_recovered';") == "1",
            "the shared Host recovery committed exactly one authenticated recovered binding before the refusal");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT count(*) FROM control_namespace WHERE status='active';") == "1",
            "exactly one active recovered control namespace is registered");
        Assert(rootsBefore == await ScalarAsync(f.Db.DatabasePath,
                   "SELECT count(*) FROM root_binding WHERE pair_id='" + f.Pair + "';"),
            "the refused publication wrote no new root row");
        Assert(collisionBefore == await ControlSnapshotAsync(f.Db.DatabasePath),
            "the current collision records stay byte-identical through the Host recovery");
        Assert(markerHash == f.MarkerHash(), "the original native control marker is unchanged and no marker is written");
    }

    /// <summary>
    /// R668 helper: the real local/cloud Host fixture with its registered provider account and connection. The
    /// pair row, the root rows and the non-creating control collision all come from real production code; nothing
    /// is fabricated and no test-only production path is used.
    /// </summary>
    private static async Task<(Fixture Fixture, SyncPair Draft, string RootBindingId)>
        BuildBlockedOwnRootFixtureAsync(string root, bool seedPriorBuildProfile = false)
    {
        var wire = new CloudPairHostBindingChecks.HostWire();
        var spec = CloudPairHostBindingChecks.HostDropbox("rc668", "/Recovery/Child");
        var f = await Fixture.CreateHostAsync(root, wire,
            (state, db, client) => new SyncAuthorityRuntimeHost(state, client,
                new SyncPairStore(state), new SyncRuntimeStateStore(state), new DropboxConnectionStore(state),
                new CloudPairHostBindingChecks.FixtureDropboxCredentials(), db,
                googleDriveCredentialStore: new CloudPairHostBindingChecks.FixtureGoogleCredentials(),
                googleDriveTokenEndpoint: new CloudPairHostBindingChecks.FixtureGoogleEndpoint(wire)),
            pairIdFactory: lease => SyncPair.FromPilot(new PilotSettings(lease.Path,
                lease.Identity.VolumeSerialNumber, lease.Identity.FileId, spec.FolderPath, spec.FolderId),
                "rc668 local cloud", spec.EndpointNamespaceId).Id,
            pairConfigurationFactory: lease => SyncAuthorityPairConfiguration.FromPair(SyncPair.FromPilot(
                new PilotSettings(lease.Path, lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
                    spec.FolderPath, spec.FolderId), "rc668 local cloud", spec.EndpointNamespaceId)),
            seedPriorBuildProfile: seedPriorBuildProfile);
        await File.WriteAllTextAsync(Path.Combine(f.Local, "business-sentinel.txt"),
            "Recovery must neither rewrite nor transfer this fixture file.");
        var installation = await f.Db.LoadInstallationIdAsync()
            ?? throw new InvalidDataException("The Host established no installation identity.");
        await f.Db.RegisterProviderAccountAsync(spec.ProviderAccountId, installation, spec.AccountId,
            spec.RootNamespaceId, spec.HomeNamespaceId, "rc668 host " + spec.AccountId, 1,
            cancellationToken: default, provider: spec.Provider);
        var store = new DropboxConnectionStore(f.Instance!.StateAuthority);
        var binding = CloudPairHostBindingChecks.HostRig.DropboxConnection(spec, primary: store.Load() is null);
        if (store.Load(binding.ConnectionId) is null) await store.SaveAsync(binding);
        wire.AddDropboxFolder(binding, spec.FolderPath, spec.FolderId);
        var local = SyncEndpoint.Local(f.Local, new SyncLocalEndpointIdentity(
            f.Root.Identity.VolumeSerialNumber, f.Root.Identity.FileId));
        var cloud = SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, spec.ConnectionId, spec.EndpointNamespaceId,
            spec.FolderId, spec.FolderPath);
        var draft = SyncPairStore.NormalizePair(new SyncPair(f.Pair, "rc668 local cloud", f.Local,
            f.Root.Identity.VolumeSerialNumber, f.Root.Identity.FileId, spec.FolderPath, spec.FolderId,
            Enabled: false, Direction: SyncMode.TwoWay, InitialReviewApproved: false, Provider: "dropbox",
            ConnectionId: spec.ConnectionId)
        {
            EndpointSchemaVersion = SyncEndpointSchema.SymmetricEndpoints,
            LeftEndpoint = SyncEndpointRecord.From(local),
            RightEndpoint = SyncEndpointRecord.From(cloud)
        });
        var target = await f.Host!.ResolveRetainedControlRecoveryTargetAsync(draft, f.Root);
        // R671: the ACTUAL incident state has BOTH own roots blocked and the retained-control metadata already
        // committed before the recovered publication runs. Register the account root through the real public
        // registrar, run the REAL production release transition so both own roots become the exact blocked
        // preimages, then commit the retained-control metadata so the recovery starts at that boundary.
        f.CloudRootId = "root-dropbox-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            "root-dropbox\n" + f.Pair + "\n" + spec.EndpointNamespaceId + "\n" + spec.FolderId)))
            .ToLowerInvariant()[..32];
        await f.Db.RegisterDropboxRootBindingAsync(f.CloudRootId, f.Pair, 1, installation, spec.ProviderAccountId,
            spec.EndpointNamespaceId, spec.FolderId, spec.FolderPath, cancellationToken: default,
            partitionCandidate: draft);
        var released = await f.Db.ReleaseFailedUnpublishedPairRootsAsync();
        Assert(released >= 2, "the real release transition blocked BOTH of the pair's own roots");
        f.RecoverySource = new SyncAuthorityDatabase.RetainedControlSource(f.PriorPath, f.SourceSha256, f.Pair,
            "local", target.RootBindingId);
        Assert(await f.Db.RetainRecoveredControlAsync(f.RecoverySource!, f.Root) is not null,
            "the retained-control metadata is committed BEFORE the recovered publication");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT count(*) FROM history_event WHERE event_kind='retained_control_recovered';") == "1",
            "exactly one recovered binding exists before the recovered publication");
        return (f, draft, target.RootBindingId);
    }

    /// <summary>
    /// R668: the real unpublished local/cloud candidate whose OWN local root is left in the exact BLOCKED state
    /// the real production release transition writes - not an active fixture - with the already-committed
    /// retained-control recovery metadata. The guarded recovery publication must activate that exact own preimage
    /// and publish the SAME pair once; the replay must not duplicate the recovered binding.
    /// </summary>
    private static async Task RecoveredBlockedOwnRootAsync(string root, bool seedPriorBuildProfile = false)
    {
        var (f, draft, rootBindingId) = await BuildBlockedOwnRootFixtureAsync(root, seedPriorBuildProfile);
        using var fixture = f;
        var cloudRootId = f.CloudRootId;
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT state FROM root_binding WHERE root_binding_id='" + rootBindingId + "';") == "blocked",
            "the pair's own LOCAL root is retained in the exact blocked preimage state");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT state FROM root_binding WHERE root_binding_id='" + cloudRootId + "';") == "blocked",
            "the pair's own ACCOUNT root is retained in the exact blocked preimage state");
        // R671: ordinary PUBLIC authoring must still deny the blocked own roots even though the retained-control
        // metadata is already committed, and it must leave the exact own preimage unchanged.
        var preimageBefore = await ScalarAsync(f.Db.DatabasePath, "SELECT identity_json||'|'||display_path " +
            "FROM root_binding WHERE root_binding_id='" + rootBindingId + "';");
        var ordinaryRefused = false;
        try { await f.Host!.BindEndpointPairAsync(draft); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException)
        { ordinaryRefused = true; }
        Assert(ordinaryRefused, "ordinary public authoring still refuses the blocked own roots");
        Assert(await ScalarAsync(f.Db.DatabasePath, "SELECT identity_json||'|'||display_path " +
            "FROM root_binding WHERE root_binding_id='" + rootBindingId + "';") == preimageBefore,
            "the ordinary refusal left the exact own preimage unchanged");
        var controlBefore = await ControlSnapshotAsync(f.Db.DatabasePath);
        var markerHash = f.MarkerHash();
        var source = f.RecoverySource!;
        var filesBefore = CrossBuildFilesSnapshot(f);
        var published = await f.Host!.RecoverRetainedControlAndAuthorAsync(draft, source, f.Root,
            replacingPairId: null, expectedPolicyRevision: null);
        Assert(published.Pair.Id == f.Pair, "the recovered publication keeps the SAME pair id");
        Assert(published.Pair.Enabled == false && published.Pair.InitialReviewApproved == false,
            "the recovered publication is disabled with fresh review");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT state FROM root_binding WHERE root_binding_id='" + rootBindingId + "';") == "active",
            "the exact own blocked preimage is activated by the guarded recovery publication");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT state FROM root_binding WHERE root_binding_id='" + cloudRootId + "';") == "active",
            "the exact own ACCOUNT blocked preimage is activated too");
        Assert(await ScalarAsync(f.Db.DatabasePath, "SELECT count(*) FROM sync_pair WHERE pair_id='" + f.Pair +
            "' AND pair_json IS NOT NULL;") == "1", "the pair definition is published once");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT count(*) FROM history_event WHERE event_kind='retained_control_recovered';") == "1",
            "exactly one authenticated recovered binding is retained");
        Assert(controlBefore == await ControlSnapshotAsync(f.Db.DatabasePath),
            "the current collision records stay byte-identical through the recovered publication");
        Assert(markerHash == f.MarkerHash(), "the original native control marker is unchanged");
        var replay = await f.Host!.RecoverRetainedControlAndAuthorAsync(draft, source, f.Root,
            replacingPairId: f.Pair, expectedPolicyRevision: published.Pair.PolicyRevision);
        Assert(replay.Pair.Id == f.Pair, "the replay keeps the SAME pair id");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT count(*) FROM history_event WHERE event_kind='retained_control_recovered';") == "1",
            "the replay does not duplicate the recovered binding");
        Assert(await ScalarAsync(f.Db.DatabasePath, "SELECT count(*) FROM root_binding WHERE pair_id='" +
            f.Pair + "';") == "2", "the published pair retains exactly its own two roots after the replay");
        Assert(controlBefore == await ControlSnapshotAsync(f.Db.DatabasePath),
            "publication replay preserves every original control allocation/operation/receipt/lease row");
        Assert(filesBefore == CrossBuildFilesSnapshot(f),
            "publication and replay preserve all business/control paths and bytes");
    }

    /// <summary>
    /// R668: every refusal of the guarded recovered publication starts from the SAME real blocked own preimage,
    /// so a wrong path, a wrong own identity, a stale revision and a conflicting second root are all refused with
    /// the own root still blocked and no pair definition published.
    /// </summary>
    private static async Task RecoveredBlockedRefusalsAsync(string root)
    {
        // (a) an exact own blocked preimage whose display path was changed refuses BEFORE any rewrite.
        {
            var (f, draft, rootBindingId) = await BuildBlockedOwnRootFixtureAsync(Path.Combine(root, "path"));
            using var fixture = f;
            await f.Db.ReleaseFailedUnpublishedPairRootsAsync();
            await ExecuteAsync(f.Db.DatabasePath, "UPDATE root_binding SET display_path='Z:/wrong/path' " +
                "WHERE root_binding_id='" + rootBindingId + "';");
            var source = new SyncAuthorityDatabase.RetainedControlSource(f.PriorPath, f.SourceSha256, f.Pair,
                "local", rootBindingId);
            var refused = false;
            try { await f.Host!.RecoverRetainedControlAndAuthorAsync(draft, source, f.Root, null, null); }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException)
            { refused = true; }
            Assert(refused, "a changed display path refuses before any rewrite");
            Assert(await ScalarAsync(f.Db.DatabasePath, "SELECT state FROM root_binding WHERE root_binding_id='" +
                rootBindingId + "';") == "blocked", "the changed-path refusal leaves the own root unactivated");
            Assert(await ScalarAsync(f.Db.DatabasePath, "SELECT count(*) FROM sync_pair WHERE pair_id='" + f.Pair +
                "' AND pair_json IS NOT NULL;") == "0",
                "the changed-path refusal publishes no pair definition");
        }
        // (b) a changed own root identity refuses and the stored row keeps its original bytes.
        {
            var (f, draft, rootBindingId) = await BuildBlockedOwnRootFixtureAsync(Path.Combine(root, "identity"));
            using var fixture = f;
            await f.Db.ReleaseFailedUnpublishedPairRootsAsync();
            await ExecuteAsync(f.Db.DatabasePath, "UPDATE root_binding SET identity_json=identity_json||' ' " +
                "WHERE root_binding_id='" + rootBindingId + "';");
            var source = new SyncAuthorityDatabase.RetainedControlSource(f.PriorPath, f.SourceSha256, f.Pair,
                "local", rootBindingId);
            var refused = false;
            try { await f.Host!.RecoverRetainedControlAndAuthorAsync(draft, source, f.Root, null, null); }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException)
            { refused = true; }
            Assert(refused, "a changed own root identity refuses before any rewrite");
            Assert(await ScalarAsync(f.Db.DatabasePath, "SELECT state FROM root_binding WHERE root_binding_id='" +
                rootBindingId + "';") == "blocked", "the changed-identity refusal leaves the own root unactivated");
        }

        // (c) a stale current revision refuses and nothing is activated.
        {
            var (f, draft, rootBindingId) = await BuildBlockedOwnRootFixtureAsync(Path.Combine(root, "revision"));
            using var fixture = f;
            await f.Db.ReleaseFailedUnpublishedPairRootsAsync();
            await ExecuteAsync(f.Db.DatabasePath, "UPDATE sync_pair SET configuration_revision=2 WHERE pair_id='" +
                f.Pair + "';");
            var source = new SyncAuthorityDatabase.RetainedControlSource(f.PriorPath, f.SourceSha256, f.Pair,
                "local", rootBindingId);
            var refused = false;
            try { await f.Host!.RecoverRetainedControlAndAuthorAsync(draft, source, f.Root, null, null); }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException)
            { refused = true; }
            Assert(refused, "a stale current revision refuses");
            Assert(await ScalarAsync(f.Db.DatabasePath, "SELECT state FROM root_binding WHERE root_binding_id='" +
                rootBindingId + "';") == "blocked", "the stale-revision refusal leaves the own root unactivated");
        }

        // (d) a conflicting second own root refuses and rolls back the first side and the pair publication.
        {
            var (f, draft, rootBindingId) = await BuildBlockedOwnRootFixtureAsync(Path.Combine(root, "second"));
            using var fixture = f;
            await f.Db.ReleaseFailedUnpublishedPairRootsAsync();
            var folderId = draft.DropboxFolderId;
            var account = await ScalarAsync(f.Db.DatabasePath,
                "SELECT provider_account_id FROM provider_account LIMIT 1;");
            var namespaceId = await ScalarAsync(f.Db.DatabasePath,
                "SELECT root_namespace_id FROM provider_account LIMIT 1;");
            await ExecuteAsync(f.Db.DatabasePath, "INSERT INTO sync_pair(pair_id,configuration_revision,state," +
                "created_utc) VALUES('pair-rc668-foreign',1,'active','2026-01-01T00:00:00Z');");
            var foreignIdentity = "{\"providerAccountId\":\"" + account + "\",\"rootNamespaceId\":\"" +
                namespaceId + "\",\"folderId\":\"" + folderId + "\"}";
            await ExecuteAsync(f.Db.DatabasePath, "INSERT INTO root_binding(root_binding_id,pair_id,side," +
                "generation,identity_json,display_path,state,observed_utc) VALUES('root-rc668-foreign'," +
                "'pair-rc668-foreign','dropbox',1,'" + foreignIdentity +
                "','/Documents/Other','active','2026-01-01T00:00:00Z');");
            var source = new SyncAuthorityDatabase.RetainedControlSource(f.PriorPath, f.SourceSha256, f.Pair,
                "local", rootBindingId);
            var refused = false;
            string? refusalMessage = null;
            try { await f.Host!.RecoverRetainedControlAndAuthorAsync(draft, source, f.Root, null, null); }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException)
            { refused = true; refusalMessage = error.Message; }
            Assert(refused, "a conflicting second own root refuses");
            Assert(refusalMessage is not null &&
                refusalMessage.Contains("already bound to another active pair root"),
                "the refusal came from the account ROOT GUARD inside the recovered-publication transaction, not a Host preflight");
            Assert(await ScalarAsync(f.Db.DatabasePath, "SELECT state FROM root_binding WHERE root_binding_id='" +
                rootBindingId + "';") == "blocked",
                "the second-root refusal rolls back the first own root activation");
            Assert(await ScalarAsync(f.Db.DatabasePath, "SELECT state FROM root_binding WHERE root_binding_id='" +
                f.CloudRootId + "';") == "blocked",
                "the second-root refusal leaves the account root blocked too");
            Assert(await ScalarAsync(f.Db.DatabasePath, "SELECT count(*) FROM sync_pair WHERE pair_id='" + f.Pair +
                "' AND pair_json IS NOT NULL;") == "0",
                "the second-root refusal publishes no pair definition");
            Assert(await ScalarAsync(f.Db.DatabasePath, "SELECT configuration_revision FROM sync_pair WHERE pair_id='" +
                f.Pair + "';") == "1",
                "the second-root refusal left the pair configuration revision unchanged");
        }
    }

    /// <summary>
    /// LC653-5: the full real-path leg on the full-constructor Host fixture. A synthetic provider transport
    /// <summary>
    /// R674: the isolated GOOGLE local/cloud recovered-branch fixture. The Google connection binding is saved into
    /// the same fixture state root through the real GoogleDriveConnectionStore, the retained provider account and
    /// the pair's own local and typed-cloud roots are registered through real registrars, the REAL production
    /// release transition blocks both own roots, and the retained-control metadata is committed before recovery.
    /// Only provider INPUT is synthetic (the existing fake credential/token/HTTP responders).
    /// </summary>
    private static async Task<Fixture> BuildGoogleBlockedOwnRootsAsync(string root, bool reversed = false)
    {
        var wire = new CloudPairHostBindingChecks.HostWire();
        var spec = CloudPairHostBindingChecks.HostDrive("rc674", "/Recovery/Child");
        // The Host derives a Google pair id and its cloud endpoint namespace from the connection ACCOUNT KEY,
        // not the endpoint permission id, so the draft must carry that exact namespace for the recovered path.
        var google = CloudPairHostBindingChecks.HostRig.GoogleConnection(spec);
        // Fixture INPUT only: the same test-only registered-client hook HostRig uses. No production client id,
        // credential or network is supplied; the Host, database, registrars and recovery path stay real.
        GoogleDriveProviderConfiguration.RegisteredClientIdForTest = "test-client";
        var f = await Fixture.CreateHostAsync(root, wire,
            (state, db, client) => new SyncAuthorityRuntimeHost(state, client,
                new SyncPairStore(state), new SyncRuntimeStateStore(state), new DropboxConnectionStore(state),
                new CloudPairHostBindingChecks.FixtureDropboxCredentials(), db,
                googleDriveCredentialStore: new CloudPairHostBindingChecks.FixtureGoogleCredentials(),
                googleDriveTokenEndpoint: new CloudPairHostBindingChecks.FixtureGoogleEndpoint(wire)),
            pairIdFactory: lease => SyncEndpointEngineLayout.DerivePairId(new PilotSettings(lease.Path,
                lease.Identity.VolumeSerialNumber, lease.Identity.FileId, spec.FolderPath, spec.FolderId),
                google.AccountKey, reversed),
            pairConfigurationFactory: lease => SyncAuthorityPairConfiguration.FromPair(SyncPair.FromPilot(
                new PilotSettings(lease.Path, lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
                    spec.FolderPath, spec.FolderId), "rc674 local drive", google.AccountKey)));
        var installation = await f.Db.LoadInstallationIdAsync()
            ?? throw new InvalidDataException("The Host established no installation identity.");
        f.GoogleInstallation = installation;
        // R675: the explicit endpoint namespace stays the PermissionId; only the pair-id salt is the account key.
        f.GoogleNamespaceId = spec.EndpointNamespaceId;
        f.GoogleFolderId = spec.FolderId;
        var googleStore = new GoogleDriveConnectionStore(f.Instance!.StateAuthority);
        if (googleStore.Load(google.ConnectionId) is null) await googleStore.SaveAsync(google);
        wire.AddDriveAccount(google, "drive-root-" + spec.AccountId);
        wire.AddDriveFolder(google, spec.FolderPath, spec.FolderId);
        var providerAccountId = SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.GoogleDrive,
            google.PermissionId, google.AccountKey, google.AccountKey);
        f.GoogleProviderAccountId = providerAccountId;
        await f.Db.RegisterProviderAccountAsync(providerAccountId, installation, google.PermissionId,
            google.AccountKey, google.AccountKey, "rc674 drive", 1, cancellationToken: default,
            provider: SyncProviders.GoogleDrive);
        var local = SyncEndpoint.Local(f.Local, new SyncLocalEndpointIdentity(
            f.Root.Identity.VolumeSerialNumber, f.Root.Identity.FileId));
        var cloud = SyncEndpoint.Cloud(SyncEndpointKind.GoogleDrive, google.ConnectionId,
            spec.EndpointNamespaceId, spec.FolderId, spec.FolderPath);
        var cloudRecord = SyncEndpointRecord.From(cloud);
        var localRecord = SyncEndpointRecord.From(local);
        var draft = SyncPairStore.NormalizePair(new SyncPair(f.Pair, "rc674 local drive", f.Local,
            f.Root.Identity.VolumeSerialNumber, f.Root.Identity.FileId, spec.FolderPath, spec.FolderId,
            Enabled: false, Direction: SyncMode.TwoWay, InitialReviewApproved: false,
            Provider: SyncProviders.GoogleDrive, ConnectionId: google.ConnectionId)
        {
            EndpointSchemaVersion = SyncEndpointSchema.SymmetricEndpoints,
            LeftEndpoint = reversed ? cloudRecord : localRecord,
            RightEndpoint = reversed ? localRecord : cloudRecord
        });
        f.GoogleDraft = draft;
        f.FixtureSerialHex = f.Root.Identity.VolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture);
        f.FixtureFileIdHex = f.Root.Identity.FileId.ToString("x16", CultureInfo.InvariantCulture);
        f.FixtureVolumeId = await f.Db.ResolveVolumeIdAsync(installation, f.FixtureSerialHex, "NTFS", null,
            supportsFileId128: false, supportsUsn: NtfsUsnJournal.TryReadCheckpoint(f.Local, out _),
            supportsAtomicRename: true, CancellationToken.None);
        f.LocalRootId = SyncAuthorityRuntimeHost.LocalRecoveryRootId(f.Pair, f.FixtureVolumeId,
            f.FixtureFileIdHex);
        f.GoogleCloudRootId = "root-dropbox-" + Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("root-dropbox\n" + f.Pair + "\n" + google.AccountKey + "\n" +
                spec.FolderId))).ToLowerInvariant()[..32];
        f.GoogleCloudSide = reversed
            ? SyncEndpointContext.CompatibilityPort(SyncEndpointKind.GoogleDrive, SyncEndpointKind.Local,
                SyncPairSide.Left)
            : SyncEndpointContext.CompatibilityPort(SyncEndpointKind.Local, SyncEndpointKind.GoogleDrive,
                SyncPairSide.Right);
        f.GoogleCloudIdentityJson = SyncAuthorityCloudRootPersistence.Serialize(SyncProviders.GoogleDrive,
            providerAccountId, spec.EndpointNamespaceId, spec.FolderId);
        await f.Db.RegisterRootBindingAsync(f.LocalRootId, f.Pair, "local", 1, JsonSerializer.Serialize(new
        {
            volumeId = f.FixtureVolumeId,
            volumeSerial = f.FixtureSerialHex,
            fileId = f.FixtureFileIdHex
        }), f.Local, cancellationToken: default, partitionCandidate: draft);
        await f.Db.RegisterRootBindingAsync(f.GoogleCloudRootId, f.Pair, f.GoogleCloudSide, 1,
            f.GoogleCloudIdentityJson, SyncAuthorityCloudRootPersistence.NormalizeDisplayPath(spec.FolderPath),
            cancellationToken: default, partitionCandidate: draft);
        var released = await f.Db.ReleaseFailedUnpublishedPairRootsAsync();
        Assert(released >= 2, "the real release transition blocked BOTH of the Google pair's own roots");
        f.RecoverySource = new SyncAuthorityDatabase.RetainedControlSource(f.PriorPath, f.SourceSha256, f.Pair,
            "local", f.LocalRootId);
        Assert(await f.Db.RetainRecoveredControlAsync(f.RecoverySource, f.Root) is not null,
            "the Google retained-control metadata is committed BEFORE the recovered publication");
        return f;
    }

    /// <summary>
    /// R686: exercise the actual allocator against prior-build retained authority. Faults are deliberately
    /// installed only in isolated fixtures; snapshots are taken AFTER injection and BEFORE the consumer call.
    /// The consumer must refuse for the intended reason without repairing, adopting or rewriting the fault.
    /// </summary>
    private static async Task CrossBuildRefusalsAsync(string root)
    {
        var (f, _, rootBindingId) = await BuildBlockedOwnRootFixtureAsync(Path.Combine(root, "caller"), true);
        using var fixture = f;
        var installation = await f.Db.LoadInstallationIdAsync()
            ?? throw new InvalidDataException("The fixture established no installation identity.");
        var currentProfile = await CurrentCrossBuildProfileAsync(f);
        var allocator = new SyncAuthorityLocalControlNamespaceAllocator(f.Db);
        await CrossBuildRefusalWithoutEffectsAsync(f, () => allocator.BindAsync(f.Root,
            "installation-wrong", f.Pair, rootBindingId, 1, currentProfile), "different installation");
        await CrossBuildRefusalWithoutEffectsAsync(f, () => allocator.BindAsync(f.Root,
            installation, f.Pair, "root-binding-wrong", 1, currentProfile), "different root binding");
        await CrossBuildRefusalWithoutEffectsAsync(f, () => allocator.BindAsync(f.Root,
            installation, f.Pair, rootBindingId, 1, currentProfile, namespaceId: "namespace-wrong-valid"),
            "different namespace");
        var before = await AllSnapshotAsync(f.Db.DatabasePath);
        var filesBefore = CrossBuildFilesSnapshot(f);
        var exact = await allocator.BindAsync(f.Root, installation, f.Pair, rootBindingId, 1, currentProfile);
        Assert(exact.RootPath == f.ControlPath, "the exact caller still reopens the legitimate old-profile recovery");
        Assert(before == await AllSnapshotAsync(f.Db.DatabasePath), "exact allocator reopen writes no authority rows");
        Assert(filesBefore == CrossBuildFilesSnapshot(f), "exact allocator reopen changes no fixture file");

        var (tampered, _, _) = await BuildBlockedOwnRootFixtureAsync(Path.Combine(root, "digest"), true);
        using var tamperedFixture = tampered;
        await ExecuteAsync(tampered.Db.DatabasePath,
            "INSERT INTO history_event(history_event_id,operation_id,event_kind,detail_json,occurred_utc) " +
            "SELECT history_event_id||'-fault',operation_id,event_kind," +
            "json_set(detail_json,'$.CollisionDigest','tampered-digest'),occurred_utc " +
            "FROM history_event WHERE event_kind='retained_control_recovered';");
        await CrossBuildRefusalWithoutEffectsAsync(tampered, () => ReopenCrossBuildAsync(tampered),
            "deterministic operation identity collides with different retained authority");

        foreach (var replaceDirectory in new[] { false, true })
        {
            var (replaced, _, _) = await BuildBlockedOwnRootFixtureAsync(Path.Combine(root,
                replaceDirectory ? "replaced-directory" : "replaced-marker"), true);
            using var replacedFixture = replaced;
            var markerBytes = await File.ReadAllBytesAsync(replaced.Marker);
            // Preserve the real original under another fixture-only name so NTFS cannot reuse its identity.
            if (replaceDirectory)
            {
                Directory.Move(replaced.ControlPath, replaced.ControlPath + ".retained-original");
                Directory.CreateDirectory(replaced.ControlPath);
            }
            else File.Move(replaced.Marker, replaced.Marker + ".retained-original");
            await File.WriteAllBytesAsync(replaced.Marker, markerBytes);
            await CrossBuildRefusalWithoutEffectsAsync(replaced, () => ReopenCrossBuildAsync(replaced),
                "identity");
        }

        var (unrelated, _, _) = await BuildBlockedOwnRootFixtureAsync(Path.Combine(root, "unrelated"), true);
        using var unrelatedFixture = unrelated;
        var genuineOtherProof = await ScalarAsync(f.Db.DatabasePath,
            "SELECT detail_json FROM history_event WHERE event_kind='retained_control_recovered';")
            ?? throw new InvalidDataException("The other real fixture has no retained recovery proof.");
        Assert(f.Pair != unrelated.Pair, "the independent native root produces a genuinely different pair");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = unrelated.Db.DatabasePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            // Append-only history remains protected. Inject a newer claim for this root using another
            // genuine pair's ownership evidence; its foreign installation/native identities must not pass.
            command.CommandText = "INSERT INTO history_event(history_event_id,operation_id,event_kind,detail_json,occurred_utc) " +
                "SELECT history_event_id||'-foreign',operation_id,event_kind," +
                "json_set($proof,'$.PairId',$pair,'$.RootBindingId',$root),occurred_utc " +
                "FROM history_event WHERE event_kind='retained_control_recovered';";
            command.Parameters.AddWithValue("$proof", genuineOtherProof);
            command.Parameters.AddWithValue("$pair", unrelated.Pair);
            command.Parameters.AddWithValue("$root", unrelated.LocalRootId);
            Assert(await command.ExecuteNonQueryAsync() == 1, "exactly one isolated foreign proof claim is appended");
        }
        await CrossBuildRefusalWithoutEffectsAsync(unrelated, () => ReopenCrossBuildAsync(unrelated),
            "deterministic operation identity collides with different retained authority");
        Assert(before == await AllSnapshotAsync(f.Db.DatabasePath), "unrelated consumer leaves original authority unchanged");
        Assert(filesBefore == CrossBuildFilesSnapshot(f), "unrelated consumer leaves original files unchanged");
    }

    private static async Task<string> CurrentCrossBuildProfileAsync(Fixture f) =>
        await ScalarAsync(f.Db.DatabasePath, "SELECT capability_profile_id FROM capability_profile " +
            "WHERE capability_profile_id<>'profile-db6773a0edafab96973cb260ee145826' LIMIT 1;")
        ?? throw new InvalidDataException("No current-build profile in the cross-build fixture.");

    private static async Task ReopenCrossBuildAsync(Fixture f)
    {
        var installation = await f.Db.LoadInstallationIdAsync()
            ?? throw new InvalidDataException("No installation in the cross-build fixture.");
        await new SyncAuthorityLocalControlNamespaceAllocator(f.Db).BindAsync(f.Root, installation, f.Pair,
            f.LocalRootId, 1, await CurrentCrossBuildProfileAsync(f));
    }

    private static async Task CrossBuildRefusalWithoutEffectsAsync(Fixture f, Func<Task> action, string reason)
    {
        var before = await AllSnapshotAsync(f.Db.DatabasePath);
        var filesBefore = CrossBuildFilesSnapshot(f);
        await RefuseAsync(action, reason);
        Assert(before == await AllSnapshotAsync(f.Db.DatabasePath), reason + ": all retained authority unchanged");
        Assert(filesBefore == CrossBuildFilesSnapshot(f), reason + ": all business/control fixture paths and bytes unchanged");
    }

    private static string CrossBuildFilesSnapshot(Fixture f) => JsonSerializer.Serialize(
        Directory.EnumerateFileSystemEntries(f.ParentRoot.Path, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new { path = Path.GetRelativePath(f.ParentRoot.Path, path),
                hash = File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "directory" }));

    private static async Task RecoveredGoogleBlockedOwnRootsAsync(string root)
    {
        using var f = await BuildGoogleBlockedOwnRootsAsync(root);
        var draft = f.GoogleDraft!;
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT state FROM root_binding WHERE root_binding_id='" + f.LocalRootId + "';") == "blocked",
            "the Google pair's own LOCAL root is blocked");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT state FROM root_binding WHERE root_binding_id='" + f.GoogleCloudRootId + "';") == "blocked",
            "the Google pair's own TYPED CLOUD root is blocked");
        var controlBefore = await ControlSnapshotAsync(f.Db.DatabasePath);
        var markerHash = f.MarkerHash();
        Assert(await ScalarAsync(f.Db.DatabasePath, "SELECT count(*) FROM sync_pair WHERE pair_id='" + f.Pair +
            "' AND configuration_revision=" + draft.PolicyRevision +
            " AND pair_json IS NULL AND selected=0;") == "1",
            "the Google fixture retains the unpublished candidate at the draft identity and revision");
        var published = await f.Host!.RecoverRetainedControlAndAuthorAsync(draft, f.RecoverySource!, f.Root,
            replacingPairId: null, expectedPolicyRevision: null);
        Assert(published.Pair.Id == f.Pair, "the Google recovered publication keeps the SAME pair id");
        Assert(published.Pair.Enabled == false && published.Pair.InitialReviewApproved == false,
            "the Google recovered publication is disabled with fresh review");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT state FROM root_binding WHERE root_binding_id='" + f.LocalRootId + "';") == "active",
            "the Google own LOCAL blocked preimage is activated");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT state FROM root_binding WHERE root_binding_id='" + f.GoogleCloudRootId + "';") == "active",
            "the Google own TYPED CLOUD blocked preimage is activated");
        Assert(await ScalarAsync(f.Db.DatabasePath, "SELECT count(*) FROM sync_pair WHERE pair_id='" + f.Pair +
            "' AND pair_json IS NOT NULL;") == "1", "the Google pair definition is published once");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT count(*) FROM history_event WHERE event_kind='retained_control_recovered';") == "1",
            "exactly one recovered binding is retained");
        Assert(controlBefore == await ControlSnapshotAsync(f.Db.DatabasePath),
            "the Google collision records stay byte-identical");
        Assert(markerHash == f.MarkerHash(), "the Google native control marker is unchanged");
    }

    /// <summary>
    /// R674: a foreign ACTIVE typed cloud root carrying this pair's exact cloud identity makes the typed cloud
    /// guard refuse INSIDE the recovered-publication transaction, after the local activation; both own roots and
    /// the pair publication must be unchanged.
    /// </summary>
    private static async Task RecoveredGoogleSecondSideAsync(string root)
    {
        using var f = await BuildGoogleBlockedOwnRootsAsync(root);
        var draft = f.GoogleDraft!;
        await ExecuteAsync(f.Db.DatabasePath, "INSERT INTO sync_pair(pair_id,configuration_revision,state," +
            "created_utc) VALUES('pair-rc674-foreign',1,'active','2026-01-01T00:00:00Z');");
        await ExecuteAsync(f.Db.DatabasePath, "INSERT INTO root_binding(root_binding_id,pair_id,side,generation," +
            "identity_json,display_path,state,observed_utc) VALUES('root-rc674-foreign','pair-rc674-foreign','" +
            f.GoogleCloudSide + "',1,'" + f.GoogleCloudIdentityJson +
            "','/Documents/Other','active','2026-01-01T00:00:00Z');");
        var refused = false;
        try { await f.Host!.RecoverRetainedControlAndAuthorAsync(draft, f.RecoverySource!, f.Root, null, null); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException)
        { refused = true; }
        Assert(refused, "a conflicting typed cloud root refuses");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT state FROM root_binding WHERE root_binding_id='" + f.LocalRootId + "';") == "blocked",
            "the typed cloud refusal rolled back the local activation");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT state FROM root_binding WHERE root_binding_id='" + f.GoogleCloudRootId + "';") == "blocked",
            "the typed cloud refusal left the cloud root blocked too");
        Assert(await ScalarAsync(f.Db.DatabasePath, "SELECT count(*) FROM sync_pair WHERE pair_id='" + f.Pair +
            "' AND pair_json IS NOT NULL;") == "0",
            "the typed cloud refusal published no pair definition");
    }

    /// <summary>
    /// R674: the supported-revision invariant is enforced rather than assumed. Normal authoring retains a fresh
    /// unpublished candidate at revision 1 with root generation 1; a non-1 recovered revision is refused with the
    /// roots, configuration and recovered metadata unchanged.
    /// </summary>
    private static async Task RecoveredGoogleRevisionInvariantAsync(string root)
    {
        using var f = await BuildGoogleBlockedOwnRootsAsync(root);
        var draft = f.GoogleDraft!;
        var roots = new SyncAuthorityDatabase.RecoveredCandidateOwnRoots(f.GoogleInstallation, f.Pair, 2,
            f.LocalRootId, f.FixtureVolumeId, f.FixtureSerialHex, f.FixtureFileIdHex, f.GoogleCloudRootId,
            f.GoogleProviderAccountId, f.GoogleNamespaceId, f.GoogleFolderId);
        var refused = false;
        try
        {
            await f.Db.ActivateRecoveredCandidateOwnRootsAsync(draft, roots,
                SyncAuthorityPairConfiguration.FromPair(draft));
        }
        catch (InvalidDataException) { refused = true; }
        Assert(refused, "a non-1 unpublished recovered revision is refused");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT state FROM root_binding WHERE root_binding_id='" + f.LocalRootId + "';") == "blocked",
            "the revision refusal leaves the local root unchanged");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT state FROM root_binding WHERE root_binding_id='" + f.GoogleCloudRootId + "';") == "blocked",
            "the revision refusal leaves the cloud root unchanged");
        Assert(await ScalarAsync(f.Db.DatabasePath, "SELECT count(*) FROM sync_pair WHERE pair_id='" + f.Pair +
            "' AND pair_json IS NOT NULL;") == "0", "the revision refusal publishes no pair definition");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT count(*) FROM history_event WHERE event_kind='retained_control_recovered';") == "1",
            "the revision refusal adds no recovered binding");
    }

    /// <summary>
    /// R675: the REVERSED Google placement (cloud left, local right) completes the same recovered publication with
    /// the same local-side normalization and the same pair identity normal authoring derives.
    /// </summary>
    private static async Task RecoveredGoogleReversedAsync(string root)
    {
        using var f = await BuildGoogleBlockedOwnRootsAsync(root, reversed: true);
        var draft = f.GoogleDraft!;
        Assert(draft.EndpointDocument!.Left!.IsLocal == false && draft.EndpointDocument.Right!.IsLocal,
            "the reversed Google fixture really carries its cloud endpoint on the left");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT state FROM root_binding WHERE root_binding_id='" + f.LocalRootId + "';") == "blocked",
            "the reversed Google own LOCAL root is blocked");
        var published = await f.Host!.RecoverRetainedControlAndAuthorAsync(draft, f.RecoverySource!, f.Root,
            replacingPairId: null, expectedPolicyRevision: null);
        Assert(published.Pair.Id == f.Pair, "the reversed Google recovered publication keeps the SAME pair id");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT state FROM root_binding WHERE root_binding_id='" + f.LocalRootId + "';") == "active",
            "the reversed Google own LOCAL blocked preimage is activated");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT state FROM root_binding WHERE root_binding_id='" + f.GoogleCloudRootId + "';") == "active",
            "the reversed Google own TYPED CLOUD blocked preimage is activated");
    }

    /// <summary>
    /// R675: a wrong Google connection, a wrong endpoint namespace and a wrong carried pair id each refuse with no
    /// new recovery record, both own roots still blocked and no pair definition published.
    /// </summary>
    private static async Task RecoveredGoogleWrongIdentityAsync(string root)
    {
        async Task RefuseAsync(string sub, Func<SyncPair, SyncPair> mutate, string label)
        {
            using var f = await BuildGoogleBlockedOwnRootsAsync(Path.Combine(root, sub));
            var history = await ScalarAsync(f.Db.DatabasePath,
                "SELECT count(*) FROM history_event WHERE event_kind='retained_control_recovered';");
            var refused = false;
            try
            {
                await f.Host!.RecoverRetainedControlAndAuthorAsync(mutate(f.GoogleDraft!), f.RecoverySource!,
                    f.Root, replacingPairId: null, expectedPolicyRevision: null);
            }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or IOException)
            { refused = true; }
            Assert(refused, label + " refuses");
            Assert(await ScalarAsync(f.Db.DatabasePath,
                "SELECT count(*) FROM history_event WHERE event_kind='retained_control_recovered';") == history,
                label + " adds no new recovered binding");
            Assert(await ScalarAsync(f.Db.DatabasePath,
                "SELECT state FROM root_binding WHERE root_binding_id='" + f.LocalRootId + "';") == "blocked",
                label + " leaves the own local root blocked");
            Assert(await ScalarAsync(f.Db.DatabasePath, "SELECT count(*) FROM sync_pair WHERE pair_id='" +
                f.Pair + "' AND pair_json IS NOT NULL;") == "0",
                label + " publishes no pair definition");
        }
        await RefuseAsync("wrong-connection",
            d => d with { RightEndpoint = d.RightEndpoint! with { ConnectionId = "conn-missing" } },
            "a wrong Google connection");
        await RefuseAsync("wrong-namespace",
            d => d with { RightEndpoint = d.RightEndpoint! with { NamespaceId = "ns-wrong" } },
            "a wrong Google endpoint namespace");
        await RefuseAsync("wrong-id", d => d with { Id = "00000000000000000000" },
            "a wrong carried pair id");
    }


    /// supplies provider replies only; the allocator, the authority, the Host and the ordinary guarded authoring
    /// stay real. The unpublished local/cloud candidate is driven through the shared Host recovery to a
    /// successful publication with the recovered ownership reopened by the real local consumer.
    /// </summary>
    private static async Task HostAuthoringAsync(string root)
    {
        var wire = new CloudPairHostBindingChecks.HostWire();
        var spec = CloudPairHostBindingChecks.HostDropbox("rc639", "/Recovery/Child");
        using var f = await Fixture.CreateHostAsync(root, wire,
            (state, db, client) => new SyncAuthorityRuntimeHost(state, client,
                new SyncPairStore(state), new SyncRuntimeStateStore(state), new DropboxConnectionStore(state),
                new CloudPairHostBindingChecks.FixtureDropboxCredentials(), db,
                googleDriveCredentialStore: new CloudPairHostBindingChecks.FixtureGoogleCredentials(),
                googleDriveTokenEndpoint: new CloudPairHostBindingChecks.FixtureGoogleEndpoint(wire)),
            pairIdFactory: lease => SyncPair.FromPilot(new PilotSettings(lease.Path,
                lease.Identity.VolumeSerialNumber, lease.Identity.FileId, spec.FolderPath, spec.FolderId),
                "rc639 local cloud host", spec.EndpointNamespaceId).Id,
            pairConfigurationFactory: lease => SyncAuthorityPairConfiguration.FromPair(SyncPair.FromPilot(
                new PilotSettings(lease.Path, lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
                    spec.FolderPath, spec.FolderId), "rc639 local cloud host", spec.EndpointNamespaceId)));
        var installation = await f.Db.LoadInstallationIdAsync()
            ?? throw new InvalidDataException("The Host established no installation identity.");
        await f.Db.RegisterProviderAccountAsync(spec.ProviderAccountId, installation, spec.AccountId,
            spec.RootNamespaceId, spec.HomeNamespaceId, "rc639 host " + spec.AccountId, 1,
            cancellationToken: default, provider: spec.Provider);
        var store = new DropboxConnectionStore(f.Instance!.StateAuthority);
        var binding = CloudPairHostBindingChecks.HostRig.DropboxConnection(spec, primary: store.Load() is null);
        if (store.Load(binding.ConnectionId) is null) await store.SaveAsync(binding);
        wire.AddDropboxFolder(binding, spec.FolderPath, spec.FolderId);
        var local = SyncEndpoint.Local(f.Local, new SyncLocalEndpointIdentity(
            f.Root.Identity.VolumeSerialNumber, f.Root.Identity.FileId));
        var cloud = SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, spec.ConnectionId, spec.EndpointNamespaceId,
            spec.FolderId, spec.FolderPath);
        var draft = SyncPairStore.NormalizePair(new SyncPair(f.Pair, "rc639 local cloud host", f.Local,
            f.Root.Identity.VolumeSerialNumber, f.Root.Identity.FileId, spec.FolderPath, spec.FolderId,
            Enabled: false, Direction: SyncMode.TwoWay, InitialReviewApproved: false, Provider: "dropbox",
            ConnectionId: spec.ConnectionId)
        {
            EndpointSchemaVersion = SyncEndpointSchema.SymmetricEndpoints,
            LeftEndpoint = SyncEndpointRecord.From(local),
            RightEndpoint = SyncEndpointRecord.From(cloud)
        });
        var target = await f.Host!.ResolveRetainedControlRecoveryTargetAsync(draft, f.Root);
        var controlBefore = await ControlSnapshotAsync(f.Db.DatabasePath);
        var markerHash = f.MarkerHash();
        var published = await f.Host!.RecoverRetainedControlAndAuthorAsync(draft,
            new SyncAuthorityDatabase.RetainedControlSource(f.PriorPath, f.SourceSha256, f.Pair, target.Side,
                target.RootBindingId), f.Root, replacingPairId: null, expectedPolicyRevision: null);
        Assert(published.Pair.Id == f.Pair, "the recovered publication keeps the SAME pair id");
        Assert(published.Pair.Enabled == false && published.Pair.InitialReviewApproved == false,
            "the recovered publication is disabled with fresh review");
        Assert(published.Pair.Direction == SyncMode.TwoWay, "the current TwoWay direction is preserved");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT count(*) FROM history_event WHERE event_kind='retained_control_recovered';") == "1",
            "exactly one authenticated recovered binding is retained");
        Assert(controlBefore == await ControlSnapshotAsync(f.Db.DatabasePath),
            "the current collision records stay byte-identical through the publication");
        Assert(markerHash == f.MarkerHash(), "the original native control marker is unchanged");
        // LC653-5 restart/prepare: a NEW Host over the SAME authority database, state root, installation and
        // native control prepares the recovered pair in place. Nothing is duplicated and no marker or transfer
        // is created.
        f.Host!.Dispose();
        f.Host = new SyncAuthorityRuntimeHost(f.Instance!.StateAuthority, f.Client!,
            new SyncPairStore(f.Instance!.StateAuthority), new SyncRuntimeStateStore(f.Instance!.StateAuthority),
            new DropboxConnectionStore(f.Instance!.StateAuthority),
            new CloudPairHostBindingChecks.FixtureDropboxCredentials(), f.Db,
            googleDriveCredentialStore: new CloudPairHostBindingChecks.FixtureGoogleCredentials(),
            googleDriveTokenEndpoint: new CloudPairHostBindingChecks.FixtureGoogleEndpoint(wire));
        var prepared = await f.Host.BindEndpointPairAsync(draft with { Priority = draft.Priority + 1 },
            f.Pair, published.Pair.PolicyRevision);
        Assert(prepared.Pair.Id == f.Pair, "the restarted Host prepares the SAME pair identity");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT count(*) FROM history_event WHERE event_kind='retained_control_recovered';") == "1",
            "restart/prepare adds no duplicate recovered binding");
        Assert(controlBefore == await ControlSnapshotAsync(f.Db.DatabasePath),
            "restart/prepare leaves the collision records byte-identical");
        Assert(markerHash == f.MarkerHash(), "restart/prepare creates no marker");
        // LC653-5 CLI leg: the SAME guarded Host action through the REAL pairs recover-control CLI surface with
        // the optional --draft for an unpublished current intent. The CLI shares the Host decision path; the
        // fixture only supplies the pinned source, the held local root and the current draft JSON.
        var cli = new CloudPublicPairAuthoringChecks.RigCliOperations(f.Host, f.Instance!.StateAuthority);
        var cliResult = await cli.RecoverRetainedControlAsync(f.Pair, f.PriorPath, f.SourceSha256.ToLowerInvariant(),
            PilotPath.NormalizeLocalRoot(f.Local),
            JsonSerializer.Serialize(prepared.Pair with { Enabled = false, InitialReviewApproved = false }),
            _ => { },
            CancellationToken.None);
        Assert(cliResult.Id == f.Pair, "the CLI recover-control publishes the SAME pair identity");
        Assert(cliResult.Enabled == false && cliResult.InitialReviewApproved == false,
            "the CLI recovery keeps the pair disabled with fresh review");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT count(*) FROM history_event WHERE event_kind='retained_control_recovered';") == "1",
            "the CLI recovery adds no duplicate recovered binding");
        Assert(controlBefore == await ControlSnapshotAsync(f.Db.DatabasePath),
            "the CLI recovery leaves the collision records byte-identical");
        Assert(markerHash == f.MarkerHash(), "the CLI recovery creates no marker");
        // LC653-6: decisive safe refusals through the REAL Host/CLI consumers, each leaving the current collision
        // records, the physical control and the single recovered binding untouched.
        var staleRefused = false;
        try
        {
            await cli.RecoverRetainedControlAsync(f.Pair, f.PriorPath, f.SourceSha256.ToLowerInvariant(),
                PilotPath.NormalizeLocalRoot(f.Local), JsonSerializer.Serialize(draft), _ => { },
                CancellationToken.None);
        }
        catch (SyncCliUsageException) { staleRefused = true; }
        Assert(staleRefused, "a stale --draft revision is refused by the CLI current-authority CAS");
        var wrongIdRefused = false;
        try
        {
            await cli.RecoverRetainedControlAsync(f.Pair, f.PriorPath, f.SourceSha256.ToLowerInvariant(),
                PilotPath.NormalizeLocalRoot(f.Local),
                JsonSerializer.Serialize(prepared.Pair with { Id = "pair-not-this-one" }), _ => { },
                CancellationToken.None);
        }
        catch (Exception error) when (error is SyncCliUsageException or InvalidDataException or
                                          InvalidOperationException) { wrongIdRefused = true; }
        Assert(wrongIdRefused, "a --draft whose exact id is not --pair is refused");
        var wrongHashRefused = false;
        try
        {
            await cli.RecoverRetainedControlAsync(f.Pair, f.PriorPath, new string('a', 64),
                PilotPath.NormalizeLocalRoot(f.Local),
                JsonSerializer.Serialize(prepared.Pair with { Enabled = false, InitialReviewApproved = false }),
                _ => { }, CancellationToken.None);
        }
        catch (Exception error) when (error is SyncCliUsageException or InvalidDataException or
                                          InvalidOperationException or IOException)
        { wrongHashRefused = true; }
        Assert(wrongHashRefused, "a changed pinned source hash is refused");
        using var wrongRoot = PilotDirectoryLease.Open(f.ParentRoot.Path);
        var wrongRootRefused = false;
        try
        {
            await f.Host!.RecoverRetainedControlAndAuthorAsync(
                prepared.Pair with { Priority = prepared.Pair.Priority + 1 },
                new SyncAuthorityDatabase.RetainedControlSource(f.PriorPath, f.SourceSha256, f.Pair, "local",
                    f.LocalRootId), wrongRoot, replacingPairId: f.Pair,
                expectedPolicyRevision: prepared.Pair.PolicyRevision);
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
        { wrongRootRefused = true; }
        Assert(wrongRootRefused, "a wrong held local root is refused before any recovery metadata is written");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT count(*) FROM history_event WHERE event_kind='retained_control_recovered';") == "1",
            "the safe refusals add no recovered binding");
        Assert(controlBefore == await ControlSnapshotAsync(f.Db.DatabasePath),
            "the safe refusals leave the collision records byte-identical");
        Assert(markerHash == f.MarkerHash(), "the safe refusals create no marker");
        // LC653-6 regressions: the REVERSED local/cloud placement (cloud left, local right) must resolve the
        // SAME local side and local root identity on the pair's own local compatibility port, and an existing
        // cloud/cloud draft must still resolve its cloud logical-left target.
        var reversedDraft = draft with { LeftEndpoint = draft.RightEndpoint, RightEndpoint = draft.LeftEndpoint };
        var reversedTarget = await f.Host!.ResolveRetainedControlRecoveryTargetAsync(reversedDraft, f.Root);
        Assert(reversedTarget.Side == target.Side && reversedTarget.RootBindingId == target.RootBindingId,
            "the reversed local/cloud placement resolves the same local side and local root identity");
        // LC653-6: an existing cloud/cloud draft still resolves its cloud logical-left target. The draft must be
        // fully self-consistent: the pair id is SyncCloudPairConfiguration.DeriveId over the ordered endpoints
        // and the saved Dropbox root/folder agree with the RIGHT endpoint.
        var otherCloud = SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, spec.ConnectionId,
            spec.EndpointNamespaceId, "id:other-folder-rc639", "/Other");
        var cloudEndpoints = new SyncPairEndpoints(cloud, otherCloud, SyncMode.TwoWay);
        var cloudDraft = SyncPairStore.NormalizePair(new SyncPair(
            SyncCloudPairConfiguration.DeriveId(cloudEndpoints), "rc639 cloud cloud", string.Empty, 0, 0,
            "/Other", "id:other-folder-rc639",
            Enabled: false, Direction: SyncMode.TwoWay, InitialReviewApproved: false, Provider: "dropbox",
            ConnectionId: spec.ConnectionId)
        {
            EndpointSchemaVersion = SyncEndpointSchema.SymmetricEndpoints,
            LeftEndpoint = SyncEndpointRecord.From(cloud),
            RightEndpoint = SyncEndpointRecord.From(otherCloud)
        });
        var cloudTarget = await f.Host!.ResolveRetainedControlRecoveryTargetAsync(cloudDraft, f.Root);
        Assert(cloudTarget.Side == SyncAuthorityCloudSides.LogicalLeft &&
               cloudTarget.RootBindingId.StartsWith("root-dropbox", StringComparison.Ordinal),
            "an existing cloud/cloud draft still resolves its cloud logical-left target");

    }

    /// <summary>
    /// LC653-4 correction 3/3b: a SEPARATE fresh genuine UNPUBLISHED local/cloud fixture enters through the ACTUAL
    /// command registry and dispatcher (pairs recover-control --draft --yes) plus the ordinary shared Host - never
    /// by driving direct Host publication first. The fresh pre-recovery refusals prove zero writes, and the initial
    /// publication, stale-revision refusal and published replay all run through the same real path.
    /// </summary>
    private static async Task HostInitialCliAsync(string root)
    {
        var wire = new CloudPairHostBindingChecks.HostWire();
        var spec = CloudPairHostBindingChecks.HostDropbox("rc639i", "/Recovery/Child");
        using var f = await Fixture.CreateHostAsync(root, wire,
            (state, db, client) => new SyncAuthorityRuntimeHost(state, client,
                new SyncPairStore(state), new SyncRuntimeStateStore(state), new DropboxConnectionStore(state),
                new CloudPairHostBindingChecks.FixtureDropboxCredentials(), db,
                googleDriveCredentialStore: new CloudPairHostBindingChecks.FixtureGoogleCredentials(),
                googleDriveTokenEndpoint: new CloudPairHostBindingChecks.FixtureGoogleEndpoint(wire)),
            pairIdFactory: lease => SyncPair.FromPilot(new PilotSettings(lease.Path,
                lease.Identity.VolumeSerialNumber, lease.Identity.FileId, spec.FolderPath, spec.FolderId),
                "rc639 initial cli", spec.EndpointNamespaceId).Id,
            pairConfigurationFactory: lease => SyncAuthorityPairConfiguration.FromPair(SyncPair.FromPilot(
                new PilotSettings(lease.Path, lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
                    spec.FolderPath, spec.FolderId), "rc639 initial cli", spec.EndpointNamespaceId)));
        var installation = await f.Db.LoadInstallationIdAsync()
            ?? throw new InvalidDataException("The Host established no installation identity.");
        await f.Db.RegisterProviderAccountAsync(spec.ProviderAccountId, installation, spec.AccountId,
            spec.RootNamespaceId, spec.HomeNamespaceId, "rc639 initial " + spec.AccountId, 1,
            cancellationToken: default, provider: spec.Provider);
        var store = new DropboxConnectionStore(f.Instance!.StateAuthority);
        var binding = CloudPairHostBindingChecks.HostRig.DropboxConnection(spec, primary: store.Load() is null);
        if (store.Load(binding.ConnectionId) is null) await store.SaveAsync(binding);
        wire.AddDropboxFolder(binding, spec.FolderPath, spec.FolderId);
        var local = SyncEndpoint.Local(f.Local, new SyncLocalEndpointIdentity(
            f.Root.Identity.VolumeSerialNumber, f.Root.Identity.FileId));
        var cloud = SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, spec.ConnectionId, spec.EndpointNamespaceId,
            spec.FolderId, spec.FolderPath);
        var draft = SyncPairStore.NormalizePair(new SyncPair(f.Pair, "rc639 initial cli", f.Local,
            f.Root.Identity.VolumeSerialNumber, f.Root.Identity.FileId, spec.FolderPath, spec.FolderId,
            Enabled: false, Direction: SyncMode.TwoWay, InitialReviewApproved: false, Provider: "dropbox",
            ConnectionId: spec.ConnectionId)
        {
            EndpointSchemaVersion = SyncEndpointSchema.SymmetricEndpoints,
            LeftEndpoint = SyncEndpointRecord.From(local),
            RightEndpoint = SyncEndpointRecord.From(cloud)
        });
        var cli = new CloudPublicPairAuthoringChecks.RigCliOperations(f.Host!, f.Instance!.StateAuthority);
        var hash = f.SourceSha256.ToLowerInvariant();
        var rootPath = PilotPath.NormalizeLocalRoot(f.Local);
        // The fresh UNPUBLISHED preimage: no pair_json, nothing selected, no published definition yet.
        Assert(await ScalarAsync(f.Db.DatabasePath, "SELECT count(*) FROM sync_pair WHERE pair_id='" + f.Pair +
                   "' AND pair_json IS NOT NULL;") == "0",
            "the fresh fixture is an UNPUBLISHED candidate with no pair_json");
        Assert(await ScalarAsync(f.Db.DatabasePath, "SELECT count(*) FROM sync_pair WHERE pair_id='" + f.Pair +
                   "' AND selected<>0;") == "0",
            "the fresh fixture has nothing selected");
        // LC653 correction 3b: fresh PRE-recovery zero-write refusals through the ACTUAL dispatcher.
        var metadataBefore = await AllSnapshotAsync(f.Db.DatabasePath);
        var collisionBefore = await ControlSnapshotAsync(f.Db.DatabasePath);
        var markerHash = f.MarkerHash();
        async Task<Exception?> DispatchRefusal(string[] args)
        {
            try
            {
                var inv = SyncCliRegistry.Parse(args);
                await SyncCliRegistry.DispatchAsync(cli, inv, TextWriter.Null, CancellationToken.None);
                return null;
            }
            catch (Exception error) when (error is not OutOfMemoryException) { return error; }
        }
        string[] Args(string pairId, string source, string sourceHash, string localRoot, string draftJson) =>
            ["--cli", "pairs", "recover-control", "--pair", pairId, "--source", source, "--source-sha256",
                sourceHash, "--local-root", localRoot, "--draft", draftJson, "--yes"];
        Assert(await DispatchRefusal(Args(f.Pair, f.PriorPath, new string('a', 64), rootPath,
                   JsonSerializer.Serialize(draft))) is not null,
            "a wrong source hash is refused through the real registry/dispatcher");
        // The CLI deliberately authors the CURRENT disabled, fresh-review intent, so an enabled input is tested at
        // the shared Host action, which refuses it before any recovery metadata is written.
        var enabledRefused = false;
        try
        {
            await f.Host!.RecoverRetainedControlAndAuthorAsync(draft with { Enabled = true },
                new SyncAuthorityDatabase.RetainedControlSource(f.PriorPath, f.SourceSha256, f.Pair, "local",
                    f.LocalRootId), f.Root, replacingPairId: null, expectedPolicyRevision: null);
        }
        catch (InvalidDataException) { enabledRefused = true; }
        Assert(enabledRefused,
            "an enabled draft is refused by the shared Host action before any recovery metadata is written");
        var wrongSourceRefused = false;
        try
        {
            await f.Host!.RecoverRetainedControlAndAuthorAsync(draft,
                new SyncAuthorityDatabase.RetainedControlSource(f.PriorPath, f.SourceSha256, "pair-other",
                    "local", f.LocalRootId), f.Root, replacingPairId: null, expectedPolicyRevision: null);
        }
        catch (InvalidDataException) { wrongSourceRefused = true; }
        Assert(wrongSourceRefused,
            "a source bound to another pair is refused by the shared Host action before retention");
        var wrongSideRefused = false;
        try
        {
            await f.Host!.RecoverRetainedControlAndAuthorAsync(draft,
                new SyncAuthorityDatabase.RetainedControlSource(f.PriorPath, f.SourceSha256, f.Pair,
                    "dropbox", f.LocalRootId), f.Root, replacingPairId: null, expectedPolicyRevision: null);
        }
        catch (InvalidDataException) { wrongSideRefused = true; }
        Assert(wrongSideRefused,
            "a source bound to another side is refused by the shared Host action before retention");
        Assert(await DispatchRefusal(Args("pair-not-this-one", f.PriorPath, hash, rootPath,
                   JsonSerializer.Serialize(draft))) is not null,
            "a wrong pair identity is refused through the real registry/dispatcher");
        Assert(await DispatchRefusal(Args(f.Pair, f.PriorPath, hash, rootPath,
                   JsonSerializer.Serialize(draft with { PolicyRevision = draft.PolicyRevision + 5 }))) is not null,
            "a wrong draft revision is refused through the real registry/dispatcher");
        Assert(await DispatchRefusal(Args(f.Pair, f.PriorPath, hash, rootPath,
                   JsonSerializer.Serialize(draft with { Id = "pair-not-this-one" }))) is not null,
            "a draft identity that is not --pair is refused through the real registry/dispatcher");
        Assert(metadataBefore == await AllSnapshotAsync(f.Db.DatabasePath),
            "the fresh pre-recovery refusals wrote no scoped metadata row");
        Assert(collisionBefore == await ControlSnapshotAsync(f.Db.DatabasePath),
            "the fresh pre-recovery refusals wrote no collision or effect row");
        Assert(markerHash == f.MarkerHash(), "the fresh pre-recovery refusals wrote no marker");
        var wrongCloudRefused = false;
        try
        {
            await f.Host!.RecoverRetainedControlAndAuthorAsync(draft with
                {
                    RightEndpoint = SyncEndpointRecord.From(SyncEndpoint.Cloud(SyncEndpointKind.Dropbox,
                        spec.ConnectionId, spec.EndpointNamespaceId, "id:other-folder-rc639", "/Other"))
                },
                new SyncAuthorityDatabase.RetainedControlSource(f.PriorPath, f.SourceSha256, f.Pair, "local",
                    f.LocalRootId), f.Root, replacingPairId: null, expectedPolicyRevision: null);
        }
        catch (InvalidDataException) { wrongCloudRefused = true; }
        Assert(wrongCloudRefused,
            "a consistent draft with an altered cloud folder is refused before retention");
        Assert(metadataBefore == await AllSnapshotAsync(f.Db.DatabasePath),
            "the altered-cloud refusal wrote no scoped metadata row");
        var publishedDraft = draft;
        // The REAL initial publication through the same dispatcher.
        var published = (SyncCliPairData)(await SyncCliRegistry.DispatchAsync(cli,
            SyncCliRegistry.Parse(Args(f.Pair, f.PriorPath, hash, rootPath,
                JsonSerializer.Serialize(publishedDraft))),
            TextWriter.Null, CancellationToken.None))!;
        Assert(published.Id == f.Pair, "the real dispatcher initial publication keeps the SAME pair id");
        Assert(published.Enabled == false && published.InitialReviewApproved == false,
            "the real dispatcher initial publication is disabled with fresh review");
        Assert(string.Equals(published.Direction, "TwoWay", StringComparison.Ordinal),
            "the real dispatcher initial publication keeps the current TwoWay policy");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT count(*) FROM history_event WHERE event_kind='retained_control_recovered';") == "1",
            "the real dispatcher initial publication retains exactly one authenticated recovered binding");
        Assert(collisionBefore == await ControlSnapshotAsync(f.Db.DatabasePath),
            "the real dispatcher initial publication leaves the collision records byte-identical");
        Assert(markerHash == f.MarkerHash(), "the real dispatcher initial publication creates no marker");
        // A drifted draft revision is refused by the real dispatcher's current-authority CAS.
        Assert(await DispatchRefusal(Args(f.Pair, f.PriorPath, hash, rootPath,
                   JsonSerializer.Serialize(draft with { PolicyRevision = draft.PolicyRevision + 5 }))) is not null,
            "a drifted draft revision is refused by the real dispatcher current-authority CAS");
        // The published replay through the same path keeps exactly one binding.
        var currentJson = JsonSerializer.Serialize(draft with { PolicyRevision = published.PolicyRevision });
        var replayed = (SyncCliPairData)(await SyncCliRegistry.DispatchAsync(cli,
            SyncCliRegistry.Parse(Args(f.Pair, f.PriorPath, hash, rootPath, currentJson)), TextWriter.Null,
            CancellationToken.None))!;
        Assert(replayed.Id == f.Pair, "the published replay keeps the SAME pair id");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT count(*) FROM history_event WHERE event_kind='retained_control_recovered';") == "1",
            "the published replay adds no duplicate recovered binding");
        // item 6 negative: a published repair naming a DIFFERENT replacement identity is refused before any
        // metadata is written.
        var wrongReplacementRefused = false;
        try
        {
            await f.Host!.RecoverRetainedControlAndAuthorAsync(publishedDraft,
                new SyncAuthorityDatabase.RetainedControlSource(f.PriorPath, f.SourceSha256, f.Pair, "local",
                    f.LocalRootId), f.Root, replacingPairId: "pair-not-this-one",
                expectedPolicyRevision: published.PolicyRevision);
        }
        catch (InvalidDataException) { wrongReplacementRefused = true; }
        Assert(wrongReplacementRefused,
            "a published repair naming a different replacement identity is refused before any metadata is written");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT count(*) FROM history_event WHERE event_kind='retained_control_recovered';") == "1",
            "the mismatched replacement refusal adds no recovered binding");
    }

    /// <summary>
    /// LC653 correction 4: a legitimate REVERSED local/cloud pair - cloud endpoint on the LEFT, local endpoint on
    /// the RIGHT, with the NORMAL reversed pair id derivation - driven through the actual shared Host recovery and
    /// ordinary authoring/publication. Swapped endpoints carrying the old forward id are not used; this is the
    /// genuine reversed normal-authoring identity.
    /// </summary>
    private static async Task HostReversedAsync(string root)
    {
        var wire = new CloudPairHostBindingChecks.HostWire();
        var spec = CloudPairHostBindingChecks.HostDropbox("rc639r", "/Recovery/Child");
        using var f = await Fixture.CreateHostAsync(root, wire,
            (state, db, client) => new SyncAuthorityRuntimeHost(state, client,
                new SyncPairStore(state), new SyncRuntimeStateStore(state), new DropboxConnectionStore(state),
                new CloudPairHostBindingChecks.FixtureDropboxCredentials(), db,
                googleDriveCredentialStore: new CloudPairHostBindingChecks.FixtureGoogleCredentials(),
                googleDriveTokenEndpoint: new CloudPairHostBindingChecks.FixtureGoogleEndpoint(wire)),
            pairIdFactory: lease => SyncEndpointEngineLayout.DerivePairId(new PilotSettings(lease.Path,
                lease.Identity.VolumeSerialNumber, lease.Identity.FileId, spec.FolderPath, spec.FolderId),
                spec.EndpointNamespaceId, reversed: true),
            pairConfigurationFactory: lease => SyncAuthorityPairConfiguration.FromPair(SyncPair.FromPilot(
                new PilotSettings(lease.Path, lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
                    spec.FolderPath, spec.FolderId), "rc639 reversed", spec.EndpointNamespaceId)));
        var installation = await f.Db.LoadInstallationIdAsync()
            ?? throw new InvalidDataException("The Host established no installation identity.");
        await f.Db.RegisterProviderAccountAsync(spec.ProviderAccountId, installation, spec.AccountId,
            spec.RootNamespaceId, spec.HomeNamespaceId, "rc639 reversed " + spec.AccountId, 1,
            cancellationToken: default, provider: spec.Provider);
        var store = new DropboxConnectionStore(f.Instance!.StateAuthority);
        var binding = CloudPairHostBindingChecks.HostRig.DropboxConnection(spec, primary: store.Load() is null);
        if (store.Load(binding.ConnectionId) is null) await store.SaveAsync(binding);
        wire.AddDropboxFolder(binding, spec.FolderPath, spec.FolderId);
        var local = SyncEndpoint.Local(f.Local, new SyncLocalEndpointIdentity(
            f.Root.Identity.VolumeSerialNumber, f.Root.Identity.FileId));
        var cloud = SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, spec.ConnectionId, spec.EndpointNamespaceId,
            spec.FolderId, spec.FolderPath);
        var draft = SyncPairStore.NormalizePair(new SyncPair(f.Pair, "rc639 reversed", f.Local,
            f.Root.Identity.VolumeSerialNumber, f.Root.Identity.FileId, spec.FolderPath, spec.FolderId,
            Enabled: false, Direction: SyncMode.TwoWay, InitialReviewApproved: false, Provider: "dropbox",
            ConnectionId: spec.ConnectionId)
        {
            EndpointSchemaVersion = SyncEndpointSchema.SymmetricEndpoints,
            LeftEndpoint = SyncEndpointRecord.From(cloud),
            RightEndpoint = SyncEndpointRecord.From(local)
        });
        var target = await f.Host!.ResolveRetainedControlRecoveryTargetAsync(draft, f.Root);
        Assert(target.Side == SyncAuthorityCloudSides.LogicalLeft && target.RootBindingId == f.LocalRootId,
            "the reversed placement resolves the local root on the local compatibility port");
        var collisionBefore = await ControlSnapshotAsync(f.Db.DatabasePath);
        var markerHash = f.MarkerHash();
        var published = await f.Host!.RecoverRetainedControlAndAuthorAsync(draft,
            new SyncAuthorityDatabase.RetainedControlSource(f.PriorPath, f.SourceSha256, f.Pair, target.Side,
                target.RootBindingId), f.Root, replacingPairId: null, expectedPolicyRevision: null);
        Assert(published.Pair.Id == f.Pair, "the reversed placement publishes the SAME pair identity");
        Assert(published.Pair.Enabled == false && published.Pair.InitialReviewApproved == false,
            "the reversed placement publishes disabled with fresh review");
        Assert(published.Pair.Direction == SyncMode.TwoWay, "the reversed placement keeps the current TwoWay policy");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT count(*) FROM history_event WHERE event_kind='retained_control_recovered';") == "1",
            "the reversed placement retains exactly one authenticated recovered binding");
        Assert(collisionBefore == await ControlSnapshotAsync(f.Db.DatabasePath),
            "the reversed placement leaves the collision records byte-identical");
        Assert(markerHash == f.MarkerHash(), "the reversed placement creates no marker");
    }


    /// <summary>
    /// /-4: a REAL permitted publication-mode drift with the legitimate provider setup.
    /// The pre-retention callback performs the existing permitted Null -> pair_json transition for the current
    /// disabled draft, verifies INSIDE the callback that the SAME database and pair row now carries pair_json at
    /// the same revision, and captures the post-mutation snapshot. The retention transaction must then refuse with
    /// its exact mode/revision mismatch, and a callback exception or a missing invocation FAILS the case.
    /// </summary>
    private static async Task HostPublicationDriftCorrectedAsync(string root)
    {
        var wire = new CloudPairHostBindingChecks.HostWire();
        var spec = CloudPairHostBindingChecks.HostDropbox("rc639d", "/Recovery/Child");
        using var f = await Fixture.CreateHostAsync(root, wire,
            (state, db, client) => new SyncAuthorityRuntimeHost(state, client,
                new SyncPairStore(state), new SyncRuntimeStateStore(state), new DropboxConnectionStore(state),
                new CloudPairHostBindingChecks.FixtureDropboxCredentials(), db,
                googleDriveCredentialStore: new CloudPairHostBindingChecks.FixtureGoogleCredentials(),
                googleDriveTokenEndpoint: new CloudPairHostBindingChecks.FixtureGoogleEndpoint(wire)),
            pairIdFactory: lease => SyncPair.FromPilot(new PilotSettings(lease.Path,
                lease.Identity.VolumeSerialNumber, lease.Identity.FileId, spec.FolderPath, spec.FolderId),
                "rc639 drift", spec.EndpointNamespaceId).Id,
            pairConfigurationFactory: lease => SyncAuthorityPairConfiguration.FromPair(SyncPair.FromPilot(
                new PilotSettings(lease.Path, lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
                    spec.FolderPath, spec.FolderId), "rc639 drift", spec.EndpointNamespaceId)));
        var installation = await f.Db.LoadInstallationIdAsync()
            ?? throw new InvalidDataException("The Host established no installation identity.");
        await f.Db.RegisterProviderAccountAsync(spec.ProviderAccountId, installation, spec.AccountId,
            spec.RootNamespaceId, spec.HomeNamespaceId, "rc639 drift " + spec.AccountId, 1,
            cancellationToken: default, provider: spec.Provider);
        var store = new DropboxConnectionStore(f.Instance!.StateAuthority);
        var binding = CloudPairHostBindingChecks.HostRig.DropboxConnection(spec, primary: store.Load() is null);
        if (store.Load(binding.ConnectionId) is null) await store.SaveAsync(binding);
        wire.AddDropboxFolder(binding, spec.FolderPath, spec.FolderId);
        var local = SyncEndpoint.Local(f.Local, new SyncLocalEndpointIdentity(
            f.Root.Identity.VolumeSerialNumber, f.Root.Identity.FileId));
        var cloud = SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, spec.ConnectionId, spec.EndpointNamespaceId,
            spec.FolderId, spec.FolderPath);
        var draft = SyncPairStore.NormalizePair(new SyncPair(f.Pair, "rc639 drift", f.Local,
            f.Root.Identity.VolumeSerialNumber, f.Root.Identity.FileId, spec.FolderPath, spec.FolderId,
            Enabled: false, Direction: SyncMode.TwoWay, InitialReviewApproved: false, Provider: "dropbox",
            ConnectionId: spec.ConnectionId)
        {
            EndpointSchemaVersion = SyncEndpointSchema.SymmetricEndpoints,
            LeftEndpoint = SyncEndpointRecord.From(local),
            RightEndpoint = SyncEndpointRecord.From(cloud)
        });
        var markerHash = f.MarkerHash();
        Assert(await ScalarAsync(f.Db.DatabasePath, "SELECT count(*) FROM sync_pair WHERE pair_id='" + f.Pair +
                   "' AND pair_json IS NOT NULL;") == "0",
            "the drift fixture starts with no pair_json published definition");
        var callbackInvoked = false;
        Exception? callbackError = null;
        var insideCount = "";
        var afterSnapshot = "";
        var driftRefused = false;
        Exception? retentionError = null;
        try
        {
            await f.Host!.RecoverRetainedControlAndAuthorAsync(draft,
                new SyncAuthorityDatabase.RetainedControlSource(f.PriorPath, f.SourceSha256, f.Pair, "local",
                    f.LocalRootId), f.Root, replacingPairId: null,
                expectedPolicyRevision: draft.PolicyRevision, progress: message =>
                {
                    if (!message.Contains("installing the retained", StringComparison.Ordinal)) return;
                    callbackInvoked = true;
                    try
                    {
                        f.Db.RetainPairDefinitionAsync(draft, select: false).GetAwaiter().GetResult();
                        insideCount = ScalarAsync(f.Db.DatabasePath,
                            "SELECT count(*) FROM sync_pair WHERE pair_id='" + f.Pair +
                            "' AND pair_json IS NOT NULL AND configuration_revision=" + draft.PolicyRevision + ";")
                            .GetAwaiter().GetResult();
                        afterSnapshot = AllSnapshotAsync(f.Db.DatabasePath).GetAwaiter().GetResult();
                    }
                    catch (Exception mutation) { callbackError = mutation; }
                });
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException)
        { driftRefused = true; retentionError = error; }
        Assert(callbackInvoked, "the pre-retention progress callback was actually invoked");
        Assert(callbackError is null,
            "the real RetainPairDefinitionAsync returned without error: " + (callbackError?.Message ?? "none"));
        Assert(insideCount == "1",
            "inside the callback the SAME database and pair row carries pair_json at the same revision (count=" +
            insideCount + ")");
        Assert(driftRefused, "the retention transaction refused the drifted recovery");
        Assert(retentionError is not null && retentionError.Message.Contains("mode or revision changed",
                   StringComparison.Ordinal),
            "the refusal is the exact retention-transaction mismatch: " + (retentionError?.Message ?? "none"));
        Assert(afterSnapshot != "" && afterSnapshot == await AllSnapshotAsync(f.Db.DatabasePath),
            "the refused drift wrote nothing after the intentional mutation");
        Assert(markerHash == f.MarkerHash(), "the refused drift created no marker");
    }

    /// <summary>
    /// clarification: the explicit expectation contract on the real DB. A supplied wrong revision
    /// and a supplied published expectation are both ENFORCED by the retention transaction on an unpublished
    /// candidate, leaving the retained metadata unchanged; a legitimate exact unpublished expectation succeeds and
    /// its exact replay is a replay.
    /// </summary>
    private static async Task ExpectationRefusalsAsync(string root)
    {
        using var f = await Fixture.CreateAsync(root, Installation, Installation);
        var metadataBefore = await AllSnapshotAsync(f.Db.DatabasePath);
        var markerHash = f.MarkerHash();
        var wrongRevisionRefused = false;
        try
        {
            await f.Db.RetainRecoveredControlAsync(new SyncAuthorityDatabase.RetainedControlSource(f.PriorPath,
                f.SourceSha256, f.Pair, "local", "root-child-local"), f.Root, default,
                expectedUnpublishedRevision: 6);
        }
        catch (InvalidOperationException) { wrongRevisionRefused = true; }
        Assert(wrongRevisionRefused,
            "a supplied WRONG expected revision is enforced by the retention transaction");
        var publishedExpectationRefused = false;
        try
        {
            await f.Db.RetainRecoveredControlAsync(new SyncAuthorityDatabase.RetainedControlSource(f.PriorPath,
                f.SourceSha256, f.Pair, "local", "root-child-local"), f.Root,
                new SyncAuthorityDatabase.RetainedControlRecoveryExpectation(f.Pair, 1, MustBeUnpublished: false));
        }
        catch (InvalidOperationException) { publishedExpectationRefused = true; }
        Assert(publishedExpectationRefused,
            "a supplied PUBLISHED expectation is refused on an unpublished candidate");
        Assert(metadataBefore == await AllSnapshotAsync(f.Db.DatabasePath),
            "the expectation refusals left the retained metadata unchanged");
        Assert(markerHash == f.MarkerHash(), "the expectation refusals wrote no marker");
        var ok = await f.Db.RetainRecoveredControlAsync(new SyncAuthorityDatabase.RetainedControlSource(
            f.PriorPath, f.SourceSha256, f.Pair, "local", "root-child-local"), f.Root, default,
            expectedUnpublishedRevision: 1);
        Assert(!ok.Replayed, "a legitimate exact unpublished expectation succeeds");
        var replay = await f.Db.RetainRecoveredControlAsync(new SyncAuthorityDatabase.RetainedControlSource(
            f.PriorPath, f.SourceSha256, f.Pair, "local", "root-child-local"), f.Root, default,
            expectedUnpublishedRevision: 1);
        Assert(replay.Replayed, "the legitimate exact unpublished expectation replays");
    }

    private static async Task PreRecoveryAsync(string root)
    {
        await PreRecoveryBodyAsync(root);
    }

    private static async Task PreRecoveryBodyAsync(string root)
    {
        using var f = await Fixture.CreateAsync(root, Installation, Installation);
        Assert(!await f.Db.HasRecoveredControlOwnershipAsync(f.Pair, "local", "root-child-local", f.Generation),
            "no scoped ownership exists before the recovery is retained");
        var beforeAdmission = await AllSnapshotAsync(f.Db.DatabasePath);
        await RefuseAsync(() => f.Db.EnsureReviewedRunCanStartAsync(f.Pair, CancellationToken.None),
            "Verify the retained outcome");
        await RefuseAsync(() => f.Db.EnsureFirstSyncCanFinalizeAsync(f.Pair, CancellationToken.None),
            "retained work remains unresolved");
        Assert(beforeAdmission == await AllSnapshotAsync(f.Db.DatabasePath),
            "unrecovered collision still refuses both execution and completion without changing history");
        await RefuseAsync(async () =>
        {
            using var reopened = await f.Db.ReopenRetainedControlAsync(f.Pair, "local", "root-child-local",
                f.Generation, f.Root);
        }, "retains no current authenticated recovered control ownership");
    }

    private static async Task ForeignSourceAsync(string root)
    {
        using var f = await Fixture.CreateAsync(root, OtherInstallation, Installation);
        var controlBefore = await ControlSnapshotAsync(f.Db.DatabasePath);
        var markerHash = f.MarkerHash();
        await RefuseAsync(() => f.Db.RetainRecoveredControlAsync(
            new SyncAuthorityDatabase.RetainedControlSource(f.PriorPath, f.SourceSha256, f.Pair, "local",
                "root-child-local"), f.Root), "another installation");
        Assert(controlBefore == await ControlSnapshotAsync(f.Db.DatabasePath),
            "foreign-source refusal changes no control row");
        Assert(markerHash == f.MarkerHash(), "foreign-source refusal changes no physical control");
    }

    private static async Task RejectAsync(string root, string name, Func<Fixture, Task>? setup,
        Func<Fixture, Task> action, string reason)
    {
        using var f = await Fixture.CreateAsync(Path.Combine(root, name), Installation, Installation);
        if (setup is not null) await setup(f);
        var controlBefore = await ControlSnapshotAsync(f.Db.DatabasePath);
        var allBefore = await AllSnapshotAsync(f.Db.DatabasePath);
        var markerHash = f.MarkerHash();
        await RefuseAsync(() => action(f), reason);
        Assert(controlBefore == await ControlSnapshotAsync(f.Db.DatabasePath), name + " changes no control row");
        Assert(allBefore == await AllSnapshotAsync(f.Db.DatabasePath), name + " changes no retained row");
        Assert(markerHash == f.MarkerHash(), name + " changes no physical control");
    }

    /// <summary>
    /// ISS-RC-H: the real production append path cannot add a second attempt to the sealed blocked
    /// non-creating collision operation without its own evidence. Whether the refusal happens at the lease
    /// boundary or at the attempt boundary, the retained attempt set and the recovery state stay unchanged,
    /// and the recovery still refuses any tuple that is no longer exactly the non-creating collision.
    /// </summary>
    private static async Task FencedAppendAsync(string root)
    {
        using var f = await Fixture.CreateAsync(root, Installation, Installation);
        var operationId = await ScalarAsync(f.Db.DatabasePath,
            "SELECT operation_id FROM operation WHERE pair_id='" + f.Pair + "' AND kind='allocate_control';");
        Assert(!string.IsNullOrEmpty(operationId), "the collision operation is retained");
        var attemptsBefore = await ScalarAsync(f.Db.DatabasePath,
            "SELECT count(*) FROM operation_attempt WHERE operation_id='" + operationId + "';");
        var refused = false;
        try
        {
            var lease = await f.Db.AcquireOperationLeaseAsync(operationId!, "fixture-fenced-append",
                TimeSpan.FromMinutes(1));
            await f.Db.StartDispatchedAttemptAsync(operationId!, lease, new string('e', 64),
                "local-control-directory-precondition");
            await f.Db.ReleaseLeaseAsync(lease);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or InvalidDataException or
                                          Microsoft.Data.Sqlite.SqliteException)
        {
            refused = true;
        }
        var attemptsAfter = await ScalarAsync(f.Db.DatabasePath,
            "SELECT count(*) FROM operation_attempt WHERE operation_id='" + operationId + "';");
        if (refused)
            Assert(attemptsAfter == attemptsBefore,
                "a refused lawful append adds no attempt to the sealed collision operation");
        else
            Assert(attemptsAfter != attemptsBefore,
                "an accepted fenced append is visible and must be refused by the tuple guard below");
        var controlBefore = await ControlSnapshotAsync(f.Db.DatabasePath);
        await RefuseAsync(() => f.Db.RetainRecoveredControlAsync(
            new SyncAuthorityDatabase.RetainedControlSource(f.PriorPath, f.SourceSha256, f.Pair, "local",
                "root-child-local"), f.Root), "non-creating collision");
        Assert(controlBefore == await ControlSnapshotAsync(f.Db.DatabasePath),
            "the precluding invariant leaves the collided rows byte-identical");
    }

    /// <summary>
    /// RC639-2.AUTH-2 / RC639-4.AUTH-4: a retained root that is no longer in a legitimate authoring state
    /// refuses before any recovered metadata is written.
    /// </summary>
    private static async Task RetiredRootAsync(string root)
    {
        using var f = await Fixture.CreateAsync(root, Installation, Installation);
        await ExecuteAsync(f.Db.DatabasePath,
            "UPDATE root_binding SET state='superseded' WHERE root_binding_id='root-child-local';");
        var before = await AllSnapshotAsync(f.Db.DatabasePath);
        var markerHash = f.MarkerHash();
        await RefuseAsync(() => f.Db.RetainRecoveredControlAsync(
            new SyncAuthorityDatabase.RetainedControlSource(f.PriorPath, f.SourceSha256, f.Pair, "local",
                "root-child-local"), f.Root), "owned selected-root binding");
        Assert(before == await AllSnapshotAsync(f.Db.DatabasePath),
            "a superseded root refusal changes no retained row");
        Assert(markerHash == f.MarkerHash(), "a superseded root refusal changes no physical control");
    }

    private sealed class Fixture : IDisposable
    {
        internal SyncAuthorityDatabase Db = null!;
        internal SyncAuthorityCommandAdmission Admission = null!;
        internal PilotDirectoryLease Root = null!;
        internal PilotDirectoryLease ParentRoot = null!;
        internal string Pair = "pair-rc639-recovery";
        internal string PriorPath = "";
        internal string SourceSha256 = "";
        internal string ControlPath = "";
        internal string Marker = "";
        internal long Generation = 1;
        internal SyncAuthorityRuntimeHost? Host;
        internal PilotInstanceLock? Instance;
        internal HttpClient? Client;
        internal string LocalRootId = "root-child-local";

        internal static async Task<Fixture> CreateAsync(string root, string priorInstallation,
            string currentInstallation)
        {
            var f = new Fixture { Local = Path.Combine(root, "parent", "child") };
            Directory.CreateDirectory(f.Local);
            f.Root = PilotDirectoryLease.Open(f.Local);
            f.ParentRoot = PilotDirectoryLease.Open(Path.Combine(root, "parent"));
            var localIdentity = JsonSerializer.Serialize(new
            {
                volumeSerial = f.Root.Identity.VolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture),
                fileId = f.Root.Identity.FileId.ToString("x16", CultureInfo.InvariantCulture)
            });
            // The ORIGINAL database really creates and owns the private staging namespace.
            f.PriorPath = Path.Combine(root, "original.db");
            var prior = new SyncAuthorityDatabase(f.PriorPath);
            await InitializeAsync(prior, f, localIdentity, priorInstallation);
            var binding = await new SyncAuthorityLocalControlNamespaceAllocator(prior).BindAsync(
                f.Root, priorInstallation, f.Pair, "root-child-local", f.Generation, Profile);
            f.ControlPath = binding.RootPath;
            f.Marker = Path.Combine(binding.RootPath, SyncControlNamespace.MarkerName);
            await SealSourceAsync(f.PriorPath);
            f.SourceSha256 = Convert.ToHexString(
                SHA256.HashData(await File.ReadAllBytesAsync(f.PriorPath)));
            // The CURRENT database retains no creation authority and really collides with the owned object.
            f.Db = new SyncAuthorityDatabase(Path.Combine(root, "current.db"));
            await InitializeAsync(f.Db, f, localIdentity, currentInstallation);
            await RefuseEitherAsync(async () =>
            {
                await new SyncAuthorityLocalControlNamespaceAllocator(f.Db).BindAsync(f.Root,
                    currentInstallation, f.Pair, "root-child-local", f.Generation, Profile);
            }, "cannot be adopted without exact retained creation authority",
                "collided with an unowned object");
            Assert(await ScalarAsync(f.Db.DatabasePath,
                "SELECT count(*) FROM operation_attempt WHERE status='blocked' " +
                "AND effect_kind='local-control-directory-precondition';") == "1",
                "the real allocator recorded a precondition-only non-creating refusal");
            f.Admission = await f.Db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", f.Pair,
                null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
            await f.Db.TransitionCommandRequestAsync(f.Admission.CommandRequestId, "executing");
            return f;
        }

        private static async Task InitializeAsync(SyncAuthorityDatabase db, Fixture f, string localIdentity,
            string installation)
        {
            await db.InitializeAsync(installation, Profile, "retained-control-recovery", "{}");
            await RegisterRowsAsync(db, f, localIdentity);
        }

        private static async Task RegisterHostRowsAsync(SyncAuthorityDatabase db, Fixture f, string localIdentity,
            SyncAuthorityPairConfiguration? configuration = null, string? volumeId = null)
        {
            if (configuration is null) await db.RegisterPairAsync(f.Pair, 1);
            else await db.RegisterPairAsync(f.Pair, 1, configuration);
            // The local root identity must be the EXACT shape RegisterLocalRootBindingCoreAsync writes
            // (volumeId, volumeSerial, fileId) with the same normalized display path, or the deterministic root
            // binding collides with different authority.
            var identity = volumeId is null
                ? localIdentity
                : JsonSerializer.Serialize(new
                {
                    volumeId,
                    volumeSerial = f.Root.Identity.VolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture),
                    fileId = f.Root.Identity.FileId.ToString("x16", CultureInfo.InvariantCulture)
                });
            await db.RegisterRootBindingAsync(f.LocalRootId, f.Pair, "local", f.Generation, identity,
                PilotPath.NormalizeLocalRoot(f.Local));
        }

        private static async Task RegisterRowsAsync(SyncAuthorityDatabase db, Fixture f, string localIdentity)
        {
            await db.RegisterPairAsync(f.Pair, 1);
            await db.RegisterRootBindingAsync("root-child-local", f.Pair, "local", f.Generation, localIdentity,
                f.Local);
            await db.RegisterRootBindingAsync("root-child-cloud", f.Pair, "dropbox", f.Generation,
                "{\"folderId\":\"id:rc639-child\"}", "/Recovery/Child");
        }

        /// <summary>
        /// LC653-5: the same real local fixture with a REAL Host built over the fixture's OWN authority database
        /// through the full-constructor seam, so a caller can supply the synthetic provider transport, the
        /// retained connection store and the credential store exactly as the sibling host-binding rig does. The
        /// Host decisions, the allocator, the authority and the CLI stay REAL; only provider replies may be
        /// synthetic. The authority is passed explicitly, so no state-root relocation is needed.
        /// </summary>
        internal static async Task<Fixture> CreateHostAsync(string root, HttpMessageHandler providerTransport,
            Func<PilotStateAuthority, SyncAuthorityDatabase, HttpClient, SyncAuthorityRuntimeHost> hostFactory,
            Func<PilotDirectoryLease, string>? pairIdFactory = null,
            Func<PilotDirectoryLease, SyncAuthorityPairConfiguration>? pairConfigurationFactory = null,
            bool seedPriorBuildProfile = false)
        {
            ArgumentNullException.ThrowIfNull(providerTransport);
            ArgumentNullException.ThrowIfNull(hostFactory);
            var f = new Fixture { Local = Path.Combine(root, "parent", "child") };
            Directory.CreateDirectory(f.Local);
            f.Root = PilotDirectoryLease.Open(f.Local);
            f.ParentRoot = PilotDirectoryLease.Open(Path.Combine(root, "parent"));
            // The pair id must be the EXACT production-derived id the normal authoring computes from the same
            // endpoints, or the local-root duplicate guard legitimately refuses the derived root.
            if (pairIdFactory is not null) f.Pair = pairIdFactory(f.Root);
            var pairConfiguration = pairConfigurationFactory?.Invoke(f.Root);
            f.Instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
            f.Client = new HttpClient(providerTransport, disposeHandler: false);
            f.Db = new SyncAuthorityDatabase(Path.Combine(root, "current.db"));
            f.Host = hostFactory(f.Instance.StateAuthority, f.Db, f.Client);
            await f.Host.LoadPairsAsync();
            var installation = await f.Db.LoadInstallationIdAsync()
                ?? throw new InvalidDataException("The Host established no installation identity.");
            var hostProfile = await ScalarAsync(f.Db.DatabasePath,
                    "SELECT capability_profile_id FROM capability_profile LIMIT 1;")
                ?? throw new InvalidDataException("The Host established no capability profile.");
            var localIdentity = JsonSerializer.Serialize(new
            {
                volumeSerial = f.Root.Identity.VolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture),
                fileId = f.Root.Identity.FileId.ToString("x16", CultureInfo.InvariantCulture)
            });
            f.PriorPath = Path.Combine(root, "original.db");
            var serialHex = f.Root.Identity.VolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture);
            var fileIdHex = f.Root.Identity.FileId.ToString("x16", CultureInfo.InvariantCulture);
            var volumeId = await f.Db.ResolveVolumeIdAsync(installation, serialHex, "NTFS", null,
                supportsFileId128: false, supportsUsn: NtfsUsnJournal.TryReadCheckpoint(f.Local, out _),
                supportsAtomicRename: true, CancellationToken.None);
            f.LocalRootId = SyncAuthorityRuntimeHost.LocalRecoveryRootId(f.Pair, volumeId, fileIdHex);
            var prior = new SyncAuthorityDatabase(f.PriorPath);
            await prior.InitializeAsync(installation, Profile, "retained-control-recovery", "{}");
            await RegisterHostRowsAsync(prior, f, localIdentity, pairConfiguration, volumeId);
            var binding = await new SyncAuthorityLocalControlNamespaceAllocator(prior).BindAsync(
                f.Root, installation, f.Pair, f.LocalRootId, f.Generation, Profile);
            f.ControlPath = binding.RootPath;
            f.Marker = Path.Combine(binding.RootPath, SyncControlNamespace.MarkerName);
            await SealSourceAsync(f.PriorPath);
            f.SourceSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(f.PriorPath)));
            await RegisterHostRowsAsync(f.Db, f, localIdentity, pairConfiguration, volumeId);
            // R686: the real cross-build collision field is the capability_profile_id. Register a distinct
            // PRIOR-BUILD profile (old source revision, byte-identical capabilities JSON) through the normal
            // supported existing-database InitializeAsync path, then seed the deterministic allocate_control
            // operation under it, exactly as the retained database carries the pre-reset build profile.
            var allocatorProfile = hostProfile;
            if (seedPriorBuildProfile)
            {
                var capabilityJson = await ScalarAsync(f.Db.DatabasePath,
                    "SELECT profile_json FROM capability_profile WHERE capability_profile_id='" + hostProfile + "';")
                    ?? throw new InvalidDataException("The Host capability profile JSON was not available.");
                var priorProfile = "profile-" + Convert.ToHexString(SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(string.Join("\n", new[] { "profile",
                        "a506e3b6e861c7e1c48a2433b3e07d07064cc620", capabilityJson })))).ToLowerInvariant()[..32];
                Assert(priorProfile == "profile-db6773a0edafab96973cb260ee145826",
                    "the prior-build profile id derives to the retained profile id");
                Assert(priorProfile != hostProfile, "the prior-build profile differs from the current host profile");
                await f.Db.InitializeAsync(installation, priorProfile,
                    "a506e3b6e861c7e1c48a2433b3e07d07064cc620", capabilityJson);
                Assert(await ScalarAsync(f.Db.DatabasePath,
                    "SELECT count(*) FROM capability_profile WHERE capability_profile_id='" + hostProfile + "';") == "1",
                    "the current host profile is still retained after registering the prior-build profile");
                Assert(await ScalarAsync(f.Db.DatabasePath,
                    "SELECT count(*) FROM capability_profile WHERE capability_profile_id='" + priorProfile + "';") == "1",
                    "the prior-build profile is present");
                allocatorProfile = priorProfile;
            }
            await RefuseEitherAsync(async () =>
            {
                await new SyncAuthorityLocalControlNamespaceAllocator(f.Db).BindAsync(f.Root, installation,
                    f.Pair, f.LocalRootId, f.Generation, allocatorProfile);
            }, "cannot be adopted without exact retained creation authority", "collided with an unowned object");
            Assert(await ScalarAsync(f.Db.DatabasePath,
                "SELECT count(*) FROM operation_attempt WHERE status='blocked' " +
                "AND effect_kind='local-control-directory-precondition';") == "1",
                "the real allocator recorded a precondition-only non-creating refusal");
            return f;
        }

        /// <summary>
        /// LC653-3/5: the same real local fixture, but with a real SyncAuthorityRuntimeHost over the SAME current
        /// authority database. The Host resolves its database from the PilotInstanceLock state root, so the
        /// current database is created at the Host-derived path and the installation id is read back from the
        /// Host initialisation and reused for the prior database, keeping both sides on one installation.
        /// </summary>
        internal static async Task<Fixture> CreateHostAsync(string root,
            HttpMessageHandler? providerTransport = null)
        {
            var f = new Fixture { Local = Path.Combine(root, "parent", "child") };
            Directory.CreateDirectory(f.Local);
            f.Root = PilotDirectoryLease.Open(f.Local);
            f.ParentRoot = PilotDirectoryLease.Open(Path.Combine(root, "parent"));
            f.Instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
            // Provider replies only may be synthetic; the allocator, authority, Host and CLI decisions stay real.
            f.Client = providerTransport is null
                ? new HttpClient()
                : new HttpClient(providerTransport, disposeHandler: false);
            f.Host = new SyncAuthorityRuntimeHost(f.Instance.StateAuthority, f.Client);
            f.Db = new SyncAuthorityDatabase(Path.Combine(root, "vNext", "state", "syncshark.db"));
            // The Host owns the initialisation; read the installation id it established so the prior database
            // uses the exact same installation.
            await f.Host.LoadPairsAsync();
            var installation = await f.Db.LoadInstallationIdAsync()
                ?? throw new InvalidDataException("The Host established no installation identity.");
            var hostProfile = await ScalarAsync(f.Db.DatabasePath, "SELECT capability_profile_id FROM capability_profile LIMIT 1;")
                ?? throw new InvalidDataException("The Host established no capability profile.");
            var localIdentity = JsonSerializer.Serialize(new
            {
                volumeSerial = f.Root.Identity.VolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture),
                fileId = f.Root.Identity.FileId.ToString("x16", CultureInfo.InvariantCulture)
            });
            f.PriorPath = Path.Combine(root, "original.db");
            var serialHex = f.Root.Identity.VolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture);
            var fileIdHex = f.Root.Identity.FileId.ToString("x16", CultureInfo.InvariantCulture);
            var volumeId = await f.Db.ResolveVolumeIdAsync(installation, serialHex, "NTFS", null,
                supportsFileId128: false, supportsUsn: NtfsUsnJournal.TryReadCheckpoint(f.Local, out _),
                supportsAtomicRename: true, CancellationToken.None);
            f.LocalRootId = SyncAuthorityRuntimeHost.LocalRecoveryRootId(f.Pair, volumeId, fileIdHex);
            var prior = new SyncAuthorityDatabase(f.PriorPath);
            await prior.InitializeAsync(installation, Profile, "retained-control-recovery", "{}");
            await RegisterHostRowsAsync(prior, f, localIdentity);
            var binding = await new SyncAuthorityLocalControlNamespaceAllocator(prior).BindAsync(
                f.Root, installation, f.Pair, f.LocalRootId, f.Generation, Profile);
            f.ControlPath = binding.RootPath;
            f.Marker = Path.Combine(binding.RootPath, SyncControlNamespace.MarkerName);
            await SealSourceAsync(f.PriorPath);
            f.SourceSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(f.PriorPath)));
            // The current database is already initialised by the Host; only the fixture rows and the real
            // collision are added.
            await RegisterHostRowsAsync(f.Db, f, localIdentity);
            await RefuseEitherAsync(async () =>
            {
                await new SyncAuthorityLocalControlNamespaceAllocator(f.Db).BindAsync(f.Root, installation,
                    f.Pair, f.LocalRootId, f.Generation, hostProfile);
            }, "cannot be adopted without exact retained creation authority",
                "collided with an unowned object");
            Assert(await ScalarAsync(f.Db.DatabasePath,
                "SELECT count(*) FROM operation_attempt WHERE status='blocked' " +
                "AND effect_kind='local-control-directory-precondition';") == "1",
                "the real allocator recorded a precondition-only non-creating refusal");
            return f;
        }

        internal string CloudRootId { get; set; } = "";
        internal SyncAuthorityDatabase.RetainedControlSource? RecoverySource { get; set; }
        internal SyncPair? GoogleDraft { get; set; }
        internal string GoogleCloudRootId { get; set; } = "";
        internal string GoogleCloudSide { get; set; } = "";
        internal string GoogleInstallation { get; set; } = "";
        internal string GoogleProviderAccountId { get; set; } = "";
        internal string GoogleCloudIdentityJson { get; set; } = "";
        internal string GoogleNamespaceId { get; set; } = "";
        internal string GoogleFolderId { get; set; } = "";
        internal string FixtureVolumeId { get; set; } = "";
        internal string FixtureSerialHex { get; set; } = "";
        internal string FixtureFileIdHex { get; set; } = "";
        internal string Local { get; private set; } = "";
        internal string MarkerHash() => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Marker)));
        public void Dispose()
        {
            Host?.Dispose();
            Client?.Dispose();
            Instance?.Dispose();
            Root.Dispose();
            ParentRoot.Dispose();
        }
    }

    /// <summary>Seals the tiny source into one pinned file: checkpoint, clear pools, remove empty sidecars and
    /// prove integrity, so the pinned read cannot be contributed to by an unbound WAL.</summary>
    private static async Task SealSourceAsync(string path)
    {
        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection(
                         new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await command.ExecuteNonQueryAsync();
        }
        await using (var verify = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString()))
        {
            await verify.OpenAsync();
            await using var check = verify.CreateCommand();
            check.CommandText = "PRAGMA quick_check;";
            var result = Convert.ToString(await check.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            if (!string.Equals(result, "ok", StringComparison.Ordinal))
                throw new InvalidDataException("The sealed source fixture is not a clean single-file database.");
        }
        // The integrity read may leave a transient shared-memory or empty log file; close every connection
        // and remove those sidecars LAST so the pinned source is one clean file whose bytes are hashed next.
        SqliteConnection.ClearAllPools();
        foreach (var sidecar in new[] { path + "-wal", path + "-shm" })
            if (File.Exists(sidecar)) File.Delete(sidecar);
    }

    /// <summary>
    /// RC639-7 hardlink substitution: the pinned source is opened through the existing trusted read guard,
    /// whose VerifySingleLink refuses a file with more than one link, so a hardlinked source can never
    /// contribute retained-control authority. No fixture-produced copy is used as the source.
    /// </summary>
    private static async Task SourceHardlinkAsync(string root)
    {
        using var f = await Fixture.CreateAsync(root, Installation, Installation);
        var link = f.PriorPath + ".link";
        CreateHardlink(link, f.PriorPath);
        Assert(File.Exists(link), "the fixture created a second hardlink to the pinned source");
        var before = await AllSnapshotAsync(f.Db.DatabasePath);
        var markerHash = f.MarkerHash();
        await RefuseAnyAsync(() => f.Db.RetainRecoveredControlAsync(
            new SyncAuthorityDatabase.RetainedControlSource(f.PriorPath, f.SourceSha256, f.Pair, "local",
                "root-child-local"), f.Root), "a hardlinked pinned source");
        Assert(before == await AllSnapshotAsync(f.Db.DatabasePath),
            "a hardlinked-source refusal changes no retained row");
        Assert(markerHash == f.MarkerHash(), "a hardlinked-source refusal changes no physical control");
    }

    /// <summary>
    /// RC639-7 reparse substitution, unprivileged variant: the owned control directory is replaced by a real
    /// NTFS DIRECTORY JUNCTION pointing at the moved original. No privilege is needed for a junction, so this is
    /// the lawful reparse proof available to this grant. The recovery must reject the reparse substitution with
    /// no retained-row change.
    /// </summary>
    private static async Task ControlReparseAsync(string root)
    {
        using var f = await Fixture.CreateAsync(root, Installation, Installation);
        var controlPath = f.ControlPath;
        var moved = controlPath + "-real";
        Directory.Move(controlPath, moved);
        CreateJunction(controlPath, moved);
        Assert(Directory.Exists(controlPath), "the fixture created a junction at the control path");
        var reparse = (new DirectoryInfo(controlPath).Attributes & FileAttributes.ReparsePoint) != 0;
        Assert(reparse, "the control path really is a reparse point");
        var before = await AllSnapshotAsync(f.Db.DatabasePath);
        var markerHash = f.MarkerHash();
        await RefuseAnyAsync(() => f.Db.RetainRecoveredControlAsync(
            new SyncAuthorityDatabase.RetainedControlSource(f.PriorPath, f.SourceSha256, f.Pair, "local",
                "root-child-local"), f.Root), "a reparse-point control directory");
        Assert(before == await AllSnapshotAsync(f.Db.DatabasePath),
            "a reparse control refusal changes no retained row");
        Assert(markerHash == f.MarkerHash(), "a reparse control refusal changes no physical control");
    }

    /// <summary>Creates a real NTFS directory junction (no privilege required) through the platform tool.</summary>
    private static void CreateJunction(string link, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe",
            "/c mklink /J \"" + link + "\" \"" + target + "\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidDataException("The junction process could not start.");
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(link))
            throw new InvalidDataException("The fixture could not create the junction: " +
                process.StandardError.ReadToEnd());
    }

    /// <summary>A refusal of any production refusal class, recorded without weakening the no-effect assertion.</summary>
    private static async Task RefuseAnyAsync(Func<Task> action, string label)
    {
        try
        {
            await action();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or InvalidDataException)
        {
            Assert(!string.IsNullOrWhiteSpace(error.Message), label + " refused with a diagnostic");
            return;
        }
        throw new InvalidDataException("Expected a refusal from " + label);
    }

    /// <summary>Creates a real NTFS hardlink through the platform tool; the fixture owns both names.</summary>
    private static void CreateHardlink(string link, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe",
            "/c mklink /H \"" + link + "\" \"" + target + "\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidDataException("The hardlink process could not start.");
        process.WaitForExit();
        if (process.ExitCode != 0 || !File.Exists(link))
            throw new InvalidDataException("The fixture could not create the hardlink: " +
                process.StandardError.ReadToEnd());
    }

    /// <summary>    /// <summary>Either genuine allocator precondition refusal is accepted as the fixture is built; both
    /// leave the same blocked non-creating attempt and no adopted authority.</summary>
    private static async Task RefuseEitherAsync(Func<Task> action, params string[] reasons)
    {
        try
        {
            await action();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or InvalidDataException)
        {
            Assert(reasons.Any(reason => error.Message.Contains(reason, StringComparison.Ordinal)),
                "exact expected collision gate (observed: " + error.Message + ")");
            return;
        }
        throw new InvalidDataException("Expected a collision refusal.");
    }

    private static async Task RefuseAsync(Func<Task> action, string reason)
    {
        try
        {
            await action();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or InvalidDataException)
        {
            Assert(error.Message.Contains(reason, StringComparison.Ordinal),
                "exact expected gate: " + reason + " (observed: " + error.Message + ")");
            return;
        }
        throw new InvalidDataException("Expected refusal: " + reason);
    }

    private static void Assert(bool value, string label)
    {
        _assertions++;
        if (!value) throw new InvalidDataException("Retained control recovery: " + label);
    }

    private static async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ScalarAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    // Only the immutably collided control rows, so a recovery that adds its own retained binding still proves
    // the original operation, member, allocation, attempt, receipt and lease rows are untouched.
    private static Task<string> ControlSnapshotAsync(string path) => SnapshotAsync(path,
        ["operation", "operation_member", "namespace_allocation", "operation_attempt", "effect_receipt", "lease"]);

    private static Task<string> AllSnapshotAsync(string path) => SnapshotAsync(path,
        ["sync_pair", "root_binding", "logical_object", "operation", "operation_member", "namespace_allocation",
            "operation_attempt", "effect_receipt", "control_namespace", "pair_control_namespace",
            "history_event", "lease"]);

    private static async Task<string> SnapshotAsync(string path, string[] tables)
    {
        var output = new List<object>();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync();
        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM " + table + " ORDER BY 1;";
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<string?[]>();
            while (await reader.ReadAsync())
                rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null :
                    Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)).ToArray());
            output.Add(new { table, rows });
        }
        return JsonSerializer.Serialize(output);
    }
}

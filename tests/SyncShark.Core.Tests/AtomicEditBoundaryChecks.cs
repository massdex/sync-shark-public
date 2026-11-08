using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

/// <summary>
/// R702/R703: the minimal REAL SQLite proof for the atomic published same-root edit boundary
/// (SyncAuthorityDatabase.CommitPublishedSameRootEditAsync). Every dependency - volume, provider
/// account, local and cloud root records, private control namespace and the published definition - is
/// created through the shipped production registration/allocation methods. Only the never-dispatched
/// setup operation is moved to its legal terminal 'cancelled' state so the pair carries no unresolved
/// work (the same transition CloudPairAuthoringChecks uses). It then exercises the new SQL end to end on
/// a fresh isolated database: one positive commit, the exact wrong-control/generation/CAS/namespace/
/// same-root refusals, and a post-advance second-side fault that must roll the revision back.
/// </summary>
internal static class AtomicEditBoundaryChecks
{
    private const string Installation = "install-atomic-edit";
    private const string Profile = "profile-atomic-edit";
    private const string ProfileJson = "{\"name\":\"atomic-edit-proof\"}";
    private const string PairId = "pair-atomic-edit-1";
    private const string LocalRootBinding = "root-local-1";
    private const string CloudRootBinding = "root-cloud-1";
    private const string ProviderAccount = "acct-atomic-edit";
    private const string RootNamespace = "namespace-atomic-edit";
    private const string FolderId = "id:folder-atomic-edit";
    private const string ControlNamespace = PairId;
    private const string RecoveryRoot = "recovery-atomic-edit";
    private const string RecoveryPath = "/Recovery/Parent";
    private const string ControlAllocation = "allocation-atomic-edit";
    private const string ControlOperation = "operation-atomic-edit-control";
    private static int _assertions;

    internal static async Task<int> RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "authority.db");
        var db = new SyncAuthorityDatabase(dbPath);
        await db.InitializeAsync(Installation, Profile, "atomic-edit", ProfileJson);

        var serialHex = "00a1b2c3";
        var fileIdHex = "00000000000000ff";
        var volumeId = await db.ResolveVolumeIdAsync(Installation, serialHex, "NTFS", null,
            supportsFileId128: false, supportsUsn: false, supportsAtomicRename: true, CancellationToken.None);
        var localRoot = Path.Combine(root, "pairs", "parent");
        Directory.CreateDirectory(localRoot);
        var configuration = new SyncAuthorityPairConfiguration("recoverable", 0, 1,
            "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true,
            AutomaticPruning: false, RecoveryRetentionDays: 30);

        // The provider account must exist before the cloud root is registered, and both roots before the
        // pair is published: exactly the ordinary authoring order.
        await db.RegisterProviderAccountAsync(ProviderAccount, Installation, "dbid-atomic-edit",
            RootNamespace, "home-atomic-edit", "Atomic Edit", 1);
        await db.RegisterPairAsync(PairId, 2, configuration);
        await db.RegisterLocalRootBindingAsync(LocalRootBinding, PairId, 1, Installation, volumeId,
            serialHex, fileIdHex, localRoot);
        await db.RegisterDropboxRootBindingAsync(CloudRootBinding, PairId, 1, Installation,
            ProviderAccount, RootNamespace, FolderId, "/Parent/Child");
        var published = MakePair(localRoot, 2);
        await db.RetainPairDefinitionAsync(published, select: true);

        // The private control namespace is created through the real allocation/authorisation registrars.
        var controlIdentityJson = JsonSerializer.Serialize(new { volumeSerial = serialHex, fileId = "0000000000000042" });
        var controlMarkerJson = JsonSerializer.Serialize(new { fileId = "0000000000000043" });
        var controlMarkerHash = new string('b', 64);
        await db.RegisterObjectAsync("object-atomic-edit-control", "directory");
        var admission = await db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", PairId,
            null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await db.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
        await db.CreateOperationAsync(new SyncAuthorityOperationPlan(ControlOperation,
            "idempotency-atomic-edit-control", SyncAuthorityOperationKind.AllocateControl, PairId,
            "object-atomic-edit-control", 2, Profile, "{}", 1,
            [new SyncAuthorityMember("member-atomic-edit-control", PairId, "object-atomic-edit-control",
                "lock", LocalRootBinding, null, null, null, null, "{}")], [], []), admission: admission);
        var lease = await db.AcquireOperationLeaseAsync(ControlOperation, "worker-atomic-edit",
            TimeSpan.FromMinutes(5));
        await db.AuthorizeNamespaceAllocationAsync(new SyncAuthorityNamespaceAllocation(ControlAllocation,
            ControlOperation, "local", "volume-parent-atomic-edit", ".syncshark-control-atomic-edit",
            new string('a', 64)), lease);
        await db.TransitionNamespaceAllocationAsync(ControlAllocation, lease, SyncAuthorityAllocationStatus.Creating);
        await db.TransitionNamespaceAllocationAsync(ControlAllocation, lease, SyncAuthorityAllocationStatus.Created);
        await db.TransitionNamespaceAllocationAsync(ControlAllocation, lease, SyncAuthorityAllocationStatus.Bound,
            controlIdentityJson);
        await db.TransitionNamespaceAllocationAsync(ControlAllocation, lease, SyncAuthorityAllocationStatus.Verified);
        await db.RegisterControlNamespaceAsync(new SyncAuthorityControlNamespace(ControlNamespace,
            ControlAllocation, "local", controlIdentityJson, controlMarkerJson, controlMarkerHash, "active"));
        await db.BindPairControlNamespaceAsync(PairId, "local", ControlNamespace, 1);
        await db.ReleaseLeaseAsync(lease);
        // Fixture-only: the setup operation was leased by the real allocation flow and released, so it sits at
        // the legal state 'leased'. The transition allowlist permits exactly 'leased'->'superseded' as its
        // terminal edge, and the definition/configuration guards already discount a superseded operation, so
        // the pair carries no unresolved work. No production path is replaced and no guard is weakened.
        await ExecuteSqlAsync(dbPath,
            "UPDATE operation SET status='superseded',terminal_utc='2026-10-07T00:00:00Z' WHERE operation_id='"
            + ControlOperation + "';");

        var before = await SnapshotAsync(dbPath);
        Assert(before == "2|1|0", "the fresh published pair starts at revision 2 with one config row and no recovery root");

        // 1) POSITIVE: a same-root edit that exercises the revision CAS, both guarded root registrations,
        //    the joined control-preimage read and the in-transaction readback.
        var committed = await db.CommitPublishedSameRootEditAsync(MakePair(localRoot, 3), configuration,
            published, expectedPublishedRevision: 2, localRootBindingId: LocalRootBinding, localGeneration: 1,
            volumeId: volumeId, volumeSerialHex: serialHex, localFileIdHex: fileIdHex,
            dropboxRootBindingId: CloudRootBinding, cloudGeneration: 1, installationId: Installation,
            providerAccountId: ProviderAccount, rootNamespaceId: RootNamespace, folderId: FolderId, select: true,
            controlNamespaceId: ControlNamespace, controlNamespaceAllocationId: ControlAllocation,
            controlIdentityJson: controlIdentityJson, controlMarkerIdentityJson: controlMarkerJson,
            controlOwnerMarkerHash: controlMarkerHash, controlGeneration: 1,
            recoveryRootBindingId: RecoveryRoot, recoveryDisplayPath: RecoveryPath, recoveryFolderId: FolderId,
            recoveryNamespaceId: RootNamespace, recoveryGeneration: 1);
        Assert(committed.Pairs.Any(p => p.Id == PairId && p.PolicyRevision == 3),
            "the positive same-root edit commits revision 3");
        Assert(committed.Pairs.Single(p => p.Id == PairId).ControlNamespace is { } retainedControl &&
            retainedControl.NamespaceId == PairId && retainedControl.InstallationId == Installation,
            "root708-r8(1): the committed definition carries the retained private-control ownership");
        Assert(await SnapshotAsync(dbPath) == "3|2|1",
            "the positive edit advances the revision, appends one config row and commits the recovery root in the same transaction");
        // root708-r8(1): a FRESH database instance must load the same full definition the atomic commit returned,
        // with the retained private-control ownership intact - the persisted definition must not silently drop it.
        var restarted = new SyncAuthorityDatabase(dbPath);
        var restartedPair = (await restarted.LoadPairSetAsync()).Pairs.Single(p => p.Id == PairId);
        Assert(restartedPair.ControlNamespace is { } restartedControl && restartedControl.NamespaceId == PairId,
            "root708-r8(1): the restart-loaded definition retains the private-control ownership");
        Assert(JsonSerializer.Serialize(restartedPair) ==
            JsonSerializer.Serialize(committed.Pairs.Single(p => p.Id == PairId)),
            "root708-r8(1): the restart-loaded full definition equals the committed pair exactly");

        // 2) REFUSALS: each must reach its exact intended diagnostic and leave the revision-row state
        //    exactly as the last commit ("3|2").
        const string publishedState = "3|2|1";
        var currentPrior = MakePair(localRoot, 3);

        await RefusesAsync("wrong control identity",
            "The retained private-control ownership no longer matches the exact preimage this edit was prepared against.",
            dbPath, publishedState, () => db.CommitPublishedSameRootEditAsync(MakePair(localRoot, 4), configuration,
                currentPrior, 3, LocalRootBinding, 1, volumeId, serialHex, fileIdHex, CloudRootBinding, 1,
                Installation, ProviderAccount, RootNamespace, FolderId, true,
                "pair-other-control", ControlAllocation, controlIdentityJson, controlMarkerJson,
                controlMarkerHash, 1));

        await RefusesAsync("wrong local generation",
            "The retained local root record no longer matches the exact preimage this edit was prepared against.",
            dbPath, publishedState, () => db.CommitPublishedSameRootEditAsync(MakePair(localRoot, 4), configuration,
                currentPrior, 3, LocalRootBinding, 99, volumeId, serialHex, fileIdHex, CloudRootBinding, 1,
                Installation, ProviderAccount, RootNamespace, FolderId, true,
                ControlNamespace, ControlAllocation, controlIdentityJson, controlMarkerJson, controlMarkerHash, 1));

        await RefusesAsync("stale CAS revision",
            "The published pair definition or revision changed since this edit was prepared; reload before saving.",
            dbPath, publishedState, () => db.CommitPublishedSameRootEditAsync(MakePair(localRoot, 4), configuration,
                currentPrior, expectedPublishedRevision: 2, localRootBindingId: LocalRootBinding, localGeneration: 1,
                volumeId: volumeId, volumeSerialHex: serialHex, localFileIdHex: fileIdHex,
                dropboxRootBindingId: CloudRootBinding, cloudGeneration: 1, installationId: Installation,
                providerAccountId: ProviderAccount, rootNamespaceId: RootNamespace, folderId: FolderId, select: true,
                controlNamespaceId: ControlNamespace, controlNamespaceAllocationId: ControlAllocation,
                controlIdentityJson: controlIdentityJson, controlMarkerIdentityJson: controlMarkerJson,
                controlOwnerMarkerHash: controlMarkerHash, controlGeneration: 1));

        await RefusesAsync("mismatched cloud namespace",
            "The retained cloud root record no longer matches the exact preimage this edit was prepared against.",
            dbPath, publishedState, () => db.CommitPublishedSameRootEditAsync(MakePair(localRoot, 4), configuration,
                currentPrior, 3, LocalRootBinding, 1, volumeId, serialHex, fileIdHex, CloudRootBinding, 1,
                Installation, ProviderAccount, "namespace-OTHER", FolderId, true,
                ControlNamespace, ControlAllocation, controlIdentityJson, controlMarkerJson, controlMarkerHash, 1));

        await RefusesAsync("changed local root (not same-root)",
            "The edited definition must keep the published pair's exact local root, cloud root and folder id.",
            dbPath, publishedState, () => db.CommitPublishedSameRootEditAsync(
                MakePair(Path.Combine(root, "pairs", "elsewhere"), 4), configuration, currentPrior, 3,
                LocalRootBinding, 1, volumeId, serialHex, fileIdHex, CloudRootBinding, 1, Installation,
                ProviderAccount, RootNamespace, FolderId, true, ControlNamespace, ControlAllocation,
                controlIdentityJson, controlMarkerJson, controlMarkerHash, 1));

        // 3) POST-ADVANCE ROLLBACK: a second-side fault reached AFTER the in-transaction revision advance
        //    root708-r8(2): a recovery identity/overlap refusal is now raised INSIDE the atomic publication,
        //    so it rolls the revision advance back instead of reporting failure after the revision and
        //    definition already committed.
        await RefusesAsync("recovery root overlapping the pair root",
            "The dedicated Dropbox recovery root overlaps an active pair root.",
            dbPath, publishedState, () => db.CommitPublishedSameRootEditAsync(MakePair(localRoot, 4), configuration,
                currentPrior, 3, LocalRootBinding, 1, volumeId, serialHex, fileIdHex, CloudRootBinding, 1,
                Installation, ProviderAccount, RootNamespace, FolderId, true,
                ControlNamespace, ControlAllocation, controlIdentityJson, controlMarkerJson, controlMarkerHash, 1,
                RecoveryRoot, "/Parent/Child/Nested", FolderId, RootNamespace, 1));

        //    and config-row write, proving the refusal rolls BOTH back. The wrong installation still passes
        //    all three preimage reads; the guarded local core then rejects the volume identity.
        await RefusesAsync("post-advance local-side fault (wrong installation)",
            "The local root is not bound to the active installation and NTFS volume identity.",
            dbPath, publishedState, () => db.CommitPublishedSameRootEditAsync(MakePair(localRoot, 4), configuration,
                currentPrior, 3, LocalRootBinding, 1, volumeId, serialHex, fileIdHex, CloudRootBinding, 1,
                "install-atomic-edit-other", ProviderAccount, RootNamespace, FolderId, true,
                ControlNamespace, ControlAllocation, controlIdentityJson, controlMarkerJson, controlMarkerHash, 1));
        // ------------------------------------------------------------------------------------------------
        // (c) root702(c): narrowly guarded interrupted same-root publication recovery (isolated staging only).
        // ------------------------------------------------------------------------------------------------
        await db.RegisterPairAsync(PairId, 4, configuration);          // interrupt: revision ahead of pair_json
        Assert(await SnapshotAsync(dbPath) == "4|3|1",
            "(c) the interrupted publication leaves the revision ahead of the published definition");
        var detected = await db.DetectInterruptedSameRootPublicationsAsync();
        Assert(detected.Count == 1 && detected[0].PairId == PairId && detected[0].OrphanRevision == 4 &&
            detected[0].PublishedRevision == 3,
            "(c) the READ-ONLY detector reports the interrupted publication by name without repairing it");
        Assert(await ScalarAsync(dbPath,
                "SELECT configuration_revision FROM sync_pair WHERE pair_id='" + PairId + "';") == "4",
            "(c) the detector wrote nothing: the revision is still ahead of the published definition");
        await ThrowsAsync("(c) the interrupted pair set does not read back",
            "does not match its authority identity or revision", () => db.LoadPairSetAsync());
        Assert(await db.RecoverInterruptedSameRootPublicationAsync(PairId),
            "(c) the guarded recovery restores the last coherent published revision");
        Assert(await SnapshotAsync(dbPath) == "3|3|1",
            "(c) the revision is restored while the orphan config row is retained as history");
        Assert((await db.LoadPairSetAsync()).Pairs.Single(p => p.Id == PairId).PolicyRevision == 3,
            "(c) the pair set reads back coherently at the published revision");
        Assert(!await db.RecoverInterruptedSameRootPublicationAsync(PairId),
            "(c) an already-coherent pair is a plain no-op");
        Assert((await db.DetectInterruptedSameRootPublicationsAsync()).Count == 0,
            "(c) the detector reports no incident once the pair is coherent again");

        // (c) refusal: the pair's own root identity drifted since the interruption.
        await db.RegisterPairAsync(PairId, 5, configuration);
        await ExecuteSqlAsync(dbPath, "UPDATE root_binding SET state='out_of_scope' WHERE root_binding_id='" +
            LocalRootBinding + "';");
        await RefusesAsync("(c) interrupted recovery with a drifted root identity",
            "The pair's own root identity changed since the interruption",
            dbPath, "5|4|1", () => db.RecoverInterruptedSameRootPublicationAsync(PairId));
        await ExecuteSqlAsync(dbPath, "UPDATE root_binding SET state='active' WHERE root_binding_id='" +
            LocalRootBinding + "';");
        Assert(await db.RecoverInterruptedSameRootPublicationAsync(PairId),
            "(c) with the identity restored the same recovery succeeds");

        // (c) refusal: the orphan revision is USED by nonterminal authority.
        await db.RegisterPairAsync(PairId, 6, configuration);
        await db.RegisterObjectAsync("object-recovery-pending", "directory");
        await db.CreateOperationAsync(new SyncAuthorityOperationPlan("operation-recovery-pending",
            "idempotency-recovery-pending", SyncAuthorityOperationKind.Reconcile, PairId, "object-recovery-pending",
            6, Profile, "{}", 1,
            [new SyncAuthorityMember("member-recovery-pending", PairId, "object-recovery-pending", "affected",
                LocalRootBinding, null, null, null, "recovery.txt", "{}")], [], []), admission: admission);
        await RefusesAsync("(c) interrupted recovery with nonterminal authority",
            "carries nonterminal authority", dbPath, "6|5|1",
            () => db.RecoverInterruptedSameRootPublicationAsync(PairId));

        // root718 boundary: clear the pending operation, recover to the coherent published revision, then prove
        // that a precommitted NONTERMINAL allocation operation blocks the atomic transaction's OWN configuration
        // write. That is the concrete reason the operation/lease/attempt rows cannot stay outside the transaction:
        // they must move inside it together with the control publication.
        await ExecuteSqlAsync(dbPath, "UPDATE operation SET status='cancelled',terminal_utc='2026-10-09T00:00:00Z' " +
            "WHERE operation_id='operation-recovery-pending';");
        Assert(await db.RecoverInterruptedSameRootPublicationAsync(PairId),
            "(root718) the pair is recovered to its coherent published revision before the boundary proof");
        var coherent = await SnapshotAsync(dbPath);
        Assert(coherent == "3|5|1",
            "(root718) the recovered pair sits at its published revision with the orphan rows retained as history");
        await db.RegisterObjectAsync("object-root718-boundary", "directory");
        await db.CreateOperationAsync(new SyncAuthorityOperationPlan("operation-root718-boundary",
            "idempotency-root718-boundary", SyncAuthorityOperationKind.AllocateControl, PairId,
            "object-root718-boundary", 3, Profile, "{}", 1,
            [new SyncAuthorityMember("member-root718-boundary", PairId, "object-root718-boundary", "lock",
                LocalRootBinding, null, null, null, null, "{}")], [], []), admission: admission);
        await RefusesAsync("(root718 boundary) a nonterminal allocation operation blocks the atomic write",
            "Pair configuration changed while nonterminal authority was retained.",
            dbPath, coherent, () => db.CommitPublishedSameRootEditAsync(MakePair(localRoot, 4), configuration,
                MakePair(localRoot, 3), 3, LocalRootBinding, 1, volumeId, serialHex, fileIdHex, CloudRootBinding, 1,
                Installation, ProviderAccount, RootNamespace, FolderId, true,
                ControlNamespace, ControlAllocation, controlIdentityJson, controlMarkerJson, controlMarkerHash, 1));

        // ---- step (4): the DB-level prepared-control proof, isolated from the Host ----------------------
        // ================================================================================================
        // root727: the selection-preservation and cloud/cloud atomicity contract of the shared policy
        // publication - the two concrete defects Root confirmed against the live 3/2 incident:
        //  (1) the atomic publication hardcoded select:true and the caller then wrote a SECOND, stale
        //      definition with its own select value, losing the OTHER pair selection and able to overwrite a
        //      newly created private-control binding;
        //  (2) the cloud/cloud policy edit advanced the revision and wrote the definition in SEPARATE
        //      transactions, so an interruption could leave the revision ahead of the definition.
        // ================================================================================================
        await ExecuteSqlAsync(dbPath, "UPDATE operation SET status='cancelled',terminal_utc='2026-10-09T00:00:00Z' " +
            "WHERE operation_id='operation-root718-boundary';");
        const string secondPairId = "pair-atomic-edit-2";
        const string secondLocalRootBinding = "root-local-2";
        const string secondCloudRootBinding = "root-cloud-2";
        const string secondFolderId = "id:folder-atomic-edit-two";
        const string secondFileIdHex = "00000000000000ae";
        var secondLocalRoot = Path.Combine(root, "pairs", "parent-two");
        Directory.CreateDirectory(secondLocalRoot);
        await db.RegisterPairAsync(secondPairId, 1, configuration);
        await db.RegisterLocalRootBindingAsync(secondLocalRootBinding, secondPairId, 1, Installation, volumeId,
            serialHex, secondFileIdHex, secondLocalRoot);
        await db.RegisterDropboxRootBindingAsync(secondCloudRootBinding, secondPairId, 1, Installation,
            ProviderAccount, RootNamespace, secondFolderId, "/Parent/Child Two");
        var secondPair = SyncPairStore.NormalizePair(new SyncPair(secondPairId, "Parent Two", secondLocalRoot,
            0x00a1b2c3u, 0xaeUL, "/Parent/Child Two", secondFolderId, Enabled: false,
            Direction: SyncMode.UploadOnly, InitialReviewApproved: false, Provider: SyncProviders.Dropbox,
            ConnectionId: ProviderAccount) { PolicyRevision = 1 });
        var selectedSecond = await db.RetainPairDefinitionAsync(secondPair, select: true);
        Assert(selectedSecond.SelectedPairId == secondPairId && selectedSecond.Pairs.Count == 2,
            "root727(1) the second published pair becomes the SELECTED pair alongside the first");
        // Editing the UNSELECTED first pair through the ONE atomic publication must PRESERVE the second pair
        // selection and carry the retained private-control ownership into the committed definition.
        var unselectedEdit = await db.CommitPublishedSameRootEditAsync(MakePair(localRoot, 4), configuration,
            MakePair(localRoot, 3), expectedPublishedRevision: 3, localRootBindingId: LocalRootBinding,
            localGeneration: 1, volumeId: volumeId, volumeSerialHex: serialHex, localFileIdHex: fileIdHex,
            dropboxRootBindingId: CloudRootBinding, cloudGeneration: 1, installationId: Installation,
            providerAccountId: ProviderAccount, rootNamespaceId: RootNamespace, folderId: FolderId, select: false,
            controlNamespaceId: ControlNamespace, controlNamespaceAllocationId: ControlAllocation,
            controlIdentityJson: controlIdentityJson, controlMarkerIdentityJson: controlMarkerJson,
            controlOwnerMarkerHash: controlMarkerHash, controlGeneration: 1);
        Assert(unselectedEdit.Pairs.Single(p => p.Id == PairId).PolicyRevision == 4,
            "root727(1) the UNSELECTED pair published edit still advances its revision");
        Assert(unselectedEdit.SelectedPairId == secondPairId,
            "root727(1) editing an UNSELECTED pair PRESERVES the other pair selection (no selection loss)");
        Assert(unselectedEdit.Pairs.Single(p => p.Id == PairId).ControlNamespace is { } unselectedControl &&
            unselectedControl.NamespaceId == PairId,
            "root727(1) the committed definition still carries the private-control ownership (no overwrite)");
        var reopened = await new SyncAuthorityDatabase(dbPath).LoadPairSetAsync();
        Assert(reopened.SelectedPairId == secondPairId &&
            reopened.Pairs.Single(p => p.Id == PairId).ControlNamespace is { } reopenedControl &&
            reopenedControl.NamespaceId == PairId,
            "root727(1) a reopened pair set reads the same selection and control ownership back");
        // The SELECTED-pair edit still selects the edited pair - the legitimate success is unchanged.
        var selectedEdit = await db.CommitPublishedSameRootEditAsync(MakePair(localRoot, 5), configuration,
            MakePair(localRoot, 4), expectedPublishedRevision: 4, localRootBindingId: LocalRootBinding,
            localGeneration: 1, volumeId: volumeId, volumeSerialHex: serialHex, localFileIdHex: fileIdHex,
            dropboxRootBindingId: CloudRootBinding, cloudGeneration: 1, installationId: Installation,
            providerAccountId: ProviderAccount, rootNamespaceId: RootNamespace, folderId: FolderId, select: true,
            controlNamespaceId: ControlNamespace, controlNamespaceAllocationId: ControlAllocation,
            controlIdentityJson: controlIdentityJson, controlMarkerIdentityJson: controlMarkerJson,
            controlOwnerMarkerHash: controlMarkerHash, controlGeneration: 1);
        Assert(selectedEdit.SelectedPairId == PairId,
            "root727(1) an explicit SELECTED edit selects the edited pair exactly as before");

        // ---- (2) CLOUD/CLOUD: revision advance and definition commit in ONE transaction, on the pair OWN
        //          path, with no local root invented and the same published-definition CAS.
        var cloudDbPath = Path.Combine(root, "cloud-policy-authority.db");
        var cloudDb = new SyncAuthorityDatabase(cloudDbPath);
        await cloudDb.InitializeAsync(Installation, Profile, "atomic-edit", ProfileJson);
        const string cloudPairId = "pair-cloud-policy";
        await cloudDb.RegisterPairAsync(cloudPairId, 1, configuration);
        var cloudPrior = SyncPairStore.NormalizePair(new SyncPair(cloudPairId, "Cloud Policy", "/staging/cloud-policy",
            0x00a1b2c3u, 0x11UL, "/Documents/CloudPolicy", "id:folder-cloud-policy", Enabled: false,
            Direction: SyncMode.UploadOnly, InitialReviewApproved: false, Provider: SyncProviders.GoogleDrive,
            ConnectionId: "connection-cloud-policy") { PolicyRevision = 1 });
        await cloudDb.RetainPairDefinitionAsync(cloudPrior, select: true);
        var cloudEdited = cloudPrior with { Direction = SyncMode.TwoWay, PolicyRevision = 2 };
        var cloudCommitted = await cloudDb.CommitCloudPairPolicyEditAsync(cloudEdited, configuration, cloudPrior,
            expectedPublishedRevision: 1, select: true);
        Assert(cloudCommitted.Pairs.Single(p => p.Id == cloudPairId).PolicyRevision == 2 &&
            cloudCommitted.Pairs.Single(p => p.Id == cloudPairId).Direction == SyncMode.TwoWay,
            "root727(2) the cloud/cloud published edit commits the revision AND the definition together");
        Assert(await PairStateAsync(cloudDbPath, cloudPairId) == "2|2|1",
            "root727(2) the cloud edit advanced the revision, appended one config-revision row and kept the selection");
        var cloudStale = false;
        try
        {
            await cloudDb.CommitCloudPairPolicyEditAsync(cloudEdited with { PolicyRevision = 3 }, configuration,
                cloudPrior, expectedPublishedRevision: 1, select: true);
        }
        catch (InvalidOperationException error) when (error.Message.Contains("reload before saving",
            StringComparison.Ordinal))
        {
            cloudStale = true;
        }
        Assert(cloudStale && await PairStateAsync(cloudDbPath, cloudPairId) == "2|2|1",
            "root727(2) the cloud edit refuses a stale published revision and commits nothing");
        var prepLocalRoot = Path.Combine(root, "prepared-pairs", "parent");
        Directory.CreateDirectory(prepLocalRoot);
        var prepDbPath = Path.Combine(root, "prepared-authority.db");
        var prepDb = new SyncAuthorityDatabase(prepDbPath);
        await prepDb.InitializeAsync(Installation, Profile, "atomic-edit", ProfileJson);
        var prepVolumeId = await prepDb.ResolveVolumeIdAsync(Installation, serialHex, "NTFS", null,
            supportsFileId128: false, supportsUsn: false, supportsAtomicRename: true, CancellationToken.None);
        await prepDb.RegisterProviderAccountAsync(ProviderAccount, Installation, "dbid-atomic-edit",
            RootNamespace, "home-atomic-edit", "Atomic Edit", 1);
        await prepDb.RegisterPairAsync(PairId, 2, configuration);
        await prepDb.RegisterLocalRootBindingAsync(LocalRootBinding, PairId, 1, Installation, prepVolumeId,
            serialHex, fileIdHex, prepLocalRoot);
        await prepDb.RegisterDropboxRootBindingAsync(CloudRootBinding, PairId, 1, Installation,
            ProviderAccount, RootNamespace, FolderId, "/Parent/Child");
        var prepPublished = MakePair(prepLocalRoot, 2);
        await prepDb.RetainPairDefinitionAsync(prepPublished, select: true);

        const string prepOperation = "operation-prepared-control";
        const string prepAllocation = "allocation-prepared-control";
        var prepIdentity = JsonSerializer.Serialize(new { volumeSerial = serialHex, fileId = "0000000000000042" });
        var prepMarkerHash = new string('f', 64);
        await prepDb.RegisterObjectAsync("object-prepared-control", "directory");
        var prepAdmission = await prepDb.CreateAcceptedCommandAdmissionAsync(null, "run_pair", PairId,
            null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await prepDb.TransitionCommandRequestAsync(prepAdmission.CommandRequestId, "executing");
        await prepDb.CreateOperationAsync(new SyncAuthorityOperationPlan(prepOperation,
            "idempotency-prepared-control", SyncAuthorityOperationKind.AllocateControl, PairId,
            "object-prepared-control", 2, Profile, "{}", 1,
            [new SyncAuthorityMember("member-prepared-control", PairId, "object-prepared-control", "lock",
                LocalRootBinding, null, null, null, null, "{}")], [], []), admission: prepAdmission);
        var prepLease = await prepDb.AcquireOperationLeaseAsync(prepOperation, "worker-prepared",
            TimeSpan.FromMinutes(5));
        // The product's control directory is the SIBLING of the pair's local root (verified by reading a real
        // bound fixture's root_binding), never inside it - so the fixture must use the same shape.
        var prepControlPath = Path.Combine(Path.GetDirectoryName(prepLocalRoot)!,
            ".syncshark-control-prepared");
        Directory.CreateDirectory(prepControlPath);
        await prepDb.AuthorizeNamespaceAllocationAsync(new SyncAuthorityNamespaceAllocation(prepAllocation,
            prepOperation, "local", "volume-parent-prepared", ".syncshark-control-prepared",
            new string('c', 64)), prepLease);
        await prepDb.TransitionNamespaceAllocationAsync(prepAllocation, prepLease,
            SyncAuthorityAllocationStatus.Creating);
        await prepDb.TransitionNamespaceAllocationAsync(prepAllocation, prepLease,
            SyncAuthorityAllocationStatus.Created);
        await prepDb.TransitionNamespaceAllocationAsync(prepAllocation, prepLease,
            SyncAuthorityAllocationStatus.Bound, prepIdentity);
        await prepDb.TransitionNamespaceAllocationAsync(prepAllocation, prepLease,
            SyncAuthorityAllocationStatus.Verified);
        var prepAttempt = await prepDb.StartDispatchedAttemptAsync(prepOperation, prepLease, new string('d', 64),
            "local-control-directory-create");
        await prepDb.RecordReceiptAsync(prepAttempt.AttemptId, prepLease, SyncAuthorityReceiptCertainty.KnownSuccess,
            "{}", prepIdentity, "{}", new string('e', 64));
        await prepDb.ReconcileAttemptAsync(prepAttempt.AttemptId, prepLease,
            SyncAuthorityAttemptStatus.VerifiedSuccess, "{}");
        Assert(await ScalarAsync(prepDbPath, "SELECT status FROM operation WHERE operation_id='" + prepOperation +
            "';") == "committed",
            "(4) the prepared allocation's OWN operation is genuinely committed before publication");
        Assert(await ScalarAsync(prepDbPath, "SELECT count(*) FROM control_namespace;") == "0",
            "(4) preparation published NO active control ownership");

        var prepCommitted = await prepDb.CommitPublishedSameRootEditAsync(MakePair(prepLocalRoot, 3), configuration,
            prepPublished, 2, LocalRootBinding, 1, prepVolumeId, serialHex, fileIdHex, CloudRootBinding, 1,
            Installation, ProviderAccount, RootNamespace, FolderId, true, PairId, "control-prepared", "{}", "{}",
            new string('0', 64), 1, preparedIdentityJson: prepIdentity,
            preparedMarkerFileId: "0000000000000043", preparedMarkerHash: prepMarkerHash,
            preparedControlRootPath: prepControlPath, preparedLease: prepLease);
        await prepDb.ReleaseLeaseAsync(prepLease);
        Assert(prepCommitted.Pairs.Single(p => p.Id == PairId).PolicyRevision == 3,
            "(4) the atomic edit published the prepared control AND advanced the revision");
        Assert(await ScalarAsync(prepDbPath, "SELECT status FROM operation WHERE operation_id='" + prepOperation +
            "';") == "completed",
            "(4) the prepared operation was terminalised under its own live fence in the same transaction");
        Assert(await ScalarAsync(prepDbPath, "SELECT count(*) FROM control_namespace WHERE status='active';") == "1",
            "(4) the active control namespace was published by the same atomic transaction");
        Assert(await ScalarAsync(prepDbPath, "SELECT count(*) FROM pair_control_namespace WHERE status='active';") == "1",
            "(4) the pair control binding was published by the same atomic transaction");
        Assert(await ScalarAsync(prepDbPath, "SELECT count(*) FROM root_binding WHERE side='local_control' AND state='active';") == "1",
            "(4) the local_control root binding was published by the same atomic transaction");
        Assert((await new SyncAuthorityDatabase(prepDbPath).LoadPairSetAsync()).Pairs.Single(p => p.Id == PairId)
            .ControlNamespace is { } prepControl && prepControl.NamespaceId == PairId,
            "(4) the reopened pair set carries the newly published private-control ownership");

        // ================================================================================================
        // obligation (4): the TYPED cloud branch of the atomic publication. The cloud side is a real typed
        // GoogleDrive root retained through the PUBLIC RegisterCloudEndpointRootBindingAsync (the SAME identity
        // serializer the publication uses), and the private control namespace is retained through the real
        // allocation/authorisation registrars, so the control preimage matches. The three account/namespace
        // refusals are reached by drifting the retained provider_account row, because the public registrar
        // applies the SAME guards upstream (SyncAuthorityDatabase.CloudEndpoints.cs:871/874/878).
        // ================================================================================================
        var typedDbPath = Path.Combine(root, "typed-cloud-authority.db");
        var typedDb = new SyncAuthorityDatabase(typedDbPath);
        await typedDb.InitializeAsync(Installation, Profile, "atomic-edit", ProfileJson);
        var typedVolumeId = await typedDb.ResolveVolumeIdAsync(Installation, serialHex, "NTFS", null,
            supportsFileId128: false, supportsUsn: false, supportsAtomicRename: true, CancellationToken.None);
        const string typedPair = "pair-typed-cloud";
        const string typedLocalBinding = "root-typed-local";
        const string typedCloudBinding = "root-typed-cloud";
        const string typedAccount = "acct-typed-google";
        const string typedNamespace = "namespace-typed-google";
        const string otherNamespace = "namespace-typed-other";
        const string typedFolder = "id:folder-typed-google";
        const string wrongFolder = "id:folder-typed-wrong";
        const string typedControl = "control-typed-google";
        const string typedAllocation = "allocation-typed-google";
        const string typedControlOperation = "operation-typed-control";
        const string typedPath = "/Typed/Google";
        var typedLocalRoot = Path.Combine(root, "typed-pairs", "parent");
        Directory.CreateDirectory(typedLocalRoot);
        // The retained endpoint namespace of a GoogleDrive account is its accountId, so the fixture's account
        // id IS the endpoint namespace it will present.
        await typedDb.RegisterProviderAccountAsync(typedAccount, Installation, typedNamespace, typedNamespace,
            typedNamespace, "Typed Google", 1, default, SyncProviders.GoogleDrive);
        await typedDb.RegisterPairAsync(typedPair, 2, configuration);
        await typedDb.RegisterLocalRootBindingAsync(typedLocalBinding, typedPair, 1, Installation, typedVolumeId,
            serialHex, fileIdHex, typedLocalRoot);
        await typedDb.RegisterCloudEndpointRootBindingAsync(typedCloudBinding, typedPair,
            SyncAuthorityCloudSides.LogicalRight, 1, Installation, SyncProviders.GoogleDrive, typedAccount,
            typedNamespace, typedFolder, typedPath);
        var typedPublished = SyncPairStore.NormalizePair(new SyncPair(typedPair, "Typed Cloud", typedLocalRoot,
            0x00a1b2c3u, 0xffUL, typedPath, typedFolder, Enabled: false, Direction: SyncMode.UploadOnly,
            InitialReviewApproved: false, Provider: SyncProviders.GoogleDrive, ConnectionId: typedAccount)
            { PolicyRevision = 2 });
        await typedDb.RetainPairDefinitionAsync(typedPublished, select: true);
        var typedControlIdentity = JsonSerializer.Serialize(new { volumeSerial = serialHex, fileId = "0000000000000077" });
        var typedControlMarker = JsonSerializer.Serialize(new { fileId = "0000000000000078" });
        var typedControlHash = new string('c', 64);
        await typedDb.RegisterObjectAsync("object-typed-control", "directory");
        var typedAdmission = await typedDb.CreateAcceptedCommandAdmissionAsync(null, "run_pair", typedPair,
            null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await typedDb.TransitionCommandRequestAsync(typedAdmission.CommandRequestId, "executing");
        await typedDb.CreateOperationAsync(new SyncAuthorityOperationPlan(typedControlOperation,
            "idempotency-typed-control", SyncAuthorityOperationKind.AllocateControl, typedPair,
            "object-typed-control", 2, Profile, "{}", 1,
            [new SyncAuthorityMember("member-typed-control", typedPair, "object-typed-control", "lock",
                typedLocalBinding, null, null, null, null, "{}")], [], []), admission: typedAdmission);
        var typedLease = await typedDb.AcquireOperationLeaseAsync(typedControlOperation, "worker-typed",
            TimeSpan.FromMinutes(5));
        await typedDb.AuthorizeNamespaceAllocationAsync(new SyncAuthorityNamespaceAllocation(typedAllocation,
            typedControlOperation, "local", "volume-parent-typed", ".syncshark-control-typed",
            new string('d', 64)), typedLease);
        await typedDb.TransitionNamespaceAllocationAsync(typedAllocation, typedLease, SyncAuthorityAllocationStatus.Creating);
        await typedDb.TransitionNamespaceAllocationAsync(typedAllocation, typedLease, SyncAuthorityAllocationStatus.Created);
        await typedDb.TransitionNamespaceAllocationAsync(typedAllocation, typedLease,
            SyncAuthorityAllocationStatus.Bound, typedControlIdentity);
        await typedDb.TransitionNamespaceAllocationAsync(typedAllocation, typedLease, SyncAuthorityAllocationStatus.Verified);
        await typedDb.RegisterControlNamespaceAsync(new SyncAuthorityControlNamespace(typedControl, typedAllocation,
            "local", typedControlIdentity, typedControlMarker, typedControlHash, "active"));
        await typedDb.BindPairControlNamespaceAsync(typedPair, "local", typedControl, 1);
        await typedDb.ReleaseLeaseAsync(typedLease);
        await ExecuteSqlAsync(typedDbPath,
            "UPDATE operation SET status='superseded',terminal_utc='2026-10-09T00:00:00Z' WHERE operation_id='"
            + typedControlOperation + "';");

        async Task<SyncPairSet> CommitTypedAsync(string provider, string account, string ns, string folder) =>
            await typedDb.CommitPublishedSameRootEditAsync(
                SyncPairStore.NormalizePair(typedPublished with { PolicyRevision = 3 }), configuration,
                typedPublished, 2, typedLocalBinding, 1, typedVolumeId, serialHex, fileIdHex, typedCloudBinding, 1,
                Installation, typedAccount, typedNamespace, typedFolder, true, typedControl, typedAllocation,
                typedControlIdentity, typedControlMarker, typedControlHash, 1,
                typedCloudRootBindingId: typedCloudBinding,
                typedCloudCompatibilitySide: SyncAuthorityCloudSides.LogicalRight,
                typedCloudProvider: provider, typedCloudProviderAccountId: account, typedCloudNamespaceId: ns,
                typedCloudFolderId: folder, typedCloudDisplayPath: typedPath);

        async Task TypedRefusalAsync(string label, string expected, string provider, string account, string ns,
            string folder)
        {
            Assert(await ScalarAsync(typedDbPath, "SELECT configuration_revision FROM sync_pair WHERE pair_id='"
                    + typedPair + "';") == "2",
                label + ": the typed pair is at its published revision before the attempt");
            var caught = false;
            try { await CommitTypedAsync(provider, account, ns, folder); }
            catch (Exception error) when (error.Message.Contains(expected, StringComparison.Ordinal)) { caught = true; }
            Assert(caught && await ScalarAsync(typedDbPath, "SELECT configuration_revision FROM sync_pair WHERE pair_id='"
                    + typedPair + "';") == "2",
                label + ": the exact intended refusal was reached and the published revision is unchanged");
        }

        await TypedRefusalAsync("obligation(4) an unregistered typed provider is refused before any write",
            "not a registered cloud provider", "not-a-provider", typedAccount, typedNamespace, typedFolder);
        await TypedRefusalAsync("obligation(4) the typed cloud root preimage no longer matches",
            "The retained typed cloud root record no longer matches the exact preimage",
            SyncProviders.GoogleDrive, typedAccount, typedNamespace, wrongFolder);
        await ExecuteSqlAsync(typedDbPath, "UPDATE provider_account SET status='revoked' WHERE provider_account_id='"
            + typedAccount + "';");
        await TypedRefusalAsync("obligation(4) the typed cloud account is not retained active",
            "The cloud root is not bound to an active provider account of this installation.",
            SyncProviders.GoogleDrive, typedAccount, typedNamespace, typedFolder);
        await ExecuteSqlAsync(typedDbPath, "UPDATE provider_account SET status='active' WHERE provider_account_id='"
            + typedAccount + "';");
        await ExecuteSqlAsync(typedDbPath, "UPDATE provider_account SET provider='dropbox' WHERE provider_account_id='"
            + typedAccount + "';");
        await TypedRefusalAsync("obligation(4) the retained account belongs to another provider",
            "The retained provider account does not belong to this provider; the cloud root is refused.",
            SyncProviders.GoogleDrive, typedAccount, typedNamespace, typedFolder);
        await ExecuteSqlAsync(typedDbPath, "UPDATE provider_account SET provider='google-drive' WHERE provider_account_id='"
            + typedAccount + "';");
        await ExecuteSqlAsync(typedDbPath, "UPDATE provider_account SET account_id='" + otherNamespace
            + "' WHERE provider_account_id='" + typedAccount + "';");
        await TypedRefusalAsync("obligation(4) the endpoint namespace is not the retained namespace",
            "The endpoint namespace is not the retained namespace of this provider account; the cloud root is refused.",
            SyncProviders.GoogleDrive, typedAccount, typedNamespace, typedFolder);
        await ExecuteSqlAsync(typedDbPath, "UPDATE provider_account SET account_id='" + typedNamespace
            + "' WHERE provider_account_id='" + typedAccount + "';");
        var typedCommitted = await CommitTypedAsync(SyncProviders.GoogleDrive, typedAccount, typedNamespace,
            typedFolder);
        Assert(typedCommitted.Pairs.Single(p => p.Id == typedPair).PolicyRevision == 3,
            "obligation(4) the typed cloud branch of the atomic publication commits the revision positively");
        Assert(await ScalarAsync(typedDbPath, "SELECT configuration_revision FROM sync_pair WHERE pair_id='"
                + typedPair + "';") == "3",
            "obligation(4) the typed publication advanced the stored configuration revision");

        Console.WriteLine("PASS atomic edit boundary: " + _assertions + " assertions");
        return 0;
    }

    private static async Task ThrowsAsync(string label, string expected, Func<Task> action)
    {
        try { await action(); }
        catch (InvalidDataException error) when (error.Message.Contains(expected, StringComparison.Ordinal))
        { Assert(true, label + ": the exact intended refusal was reached"); return; }
        throw new InvalidDataException("Expected the exact refusal: " + label);
    }

    private static async Task<string> ScalarAsync(string dbPath, string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + dbPath + ";Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    private static SyncPair MakePair(string localRoot, long revision) =>
        SyncPairStore.NormalizePair(new SyncPair(PairId, "Parent", localRoot, 0x00a1b2c3u, 0xffUL, "/Parent/Child",
            FolderId, Enabled: false, Direction: SyncMode.UploadOnly,
            InitialReviewApproved: false, Provider: "dropbox", ConnectionId: ProviderAccount)
        {
            PolicyRevision = revision,
            ControlNamespace = new SyncControlNamespaceBinding(Installation, PairId,
                new SyncLocalControlNamespaceBinding(
                    Path.Combine(Path.GetDirectoryName(Path.GetFullPath(localRoot))!,
                        ".syncshark-control-" + PairId), 0x00a1b2c3u, 0x42UL, "00a1b2c3:0000000000000043",
                    new string('b', 64)))
        });

    private static async Task<string> SnapshotAsync(string dbPath)
    {
        await using var connection = new SqliteConnection("Data Source=" + dbPath + ";Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT configuration_revision || '|' || (SELECT count(*) FROM " +
            "pair_config_revision WHERE pair_id=$p) || '|' || (SELECT count(*) FROM recovery_root_binding) " +
            "FROM sync_pair WHERE pair_id=$p;";
        command.Parameters.AddWithValue("$p", PairId);
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    private static async Task ExecuteSqlAsync(string dbPath, string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + dbPath + ";Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RefusesAsync(string label, string expected, string dbPath, string unchanged,
        Func<Task> action)
    {
        Assert(await SnapshotAsync(dbPath) == unchanged, label + ": the revision state is unchanged before the attempt");
        try
        {
            await action();
        }
        catch (InvalidOperationException error)
        {
            Assert(error.Message.Contains(expected, StringComparison.Ordinal),
                label + ": the exact intended refusal was reached");
            Assert(await SnapshotAsync(dbPath) == unchanged,
                label + ": the revision advance and the config-revision row were rolled back / left untouched");
            return;
        }
        throw new InvalidDataException("The boundary accepted a case it must refuse: " + label);
    }

    private static async Task<string> PairStateAsync(string dbPath, string pairId)
    {
        await using var connection = new SqliteConnection("Data Source=" + dbPath + ";Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT configuration_revision || '|' || (SELECT count(*) FROM " +
            "pair_config_revision WHERE pair_id=$p) || '|' || (SELECT count(*) FROM sync_pair WHERE selected=1) " +
            "FROM sync_pair WHERE pair_id=$p;";
        command.Parameters.AddWithValue("$p", pairId);
        return Convert.ToString(await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    private static void Assert(bool value, string label)
    {
        if (!value) throw new InvalidDataException("Atomic edit proof failed: " + label);
        _assertions++;
        Console.WriteLine("PASS " + label);
    }
}


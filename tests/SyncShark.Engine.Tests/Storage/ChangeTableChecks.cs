using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Independent durable acquisition and finite-selection contract checks through the real store.</summary>
public static partial class ChangeTableChecks
{
    // Neutral fixture names distinguish independent pairs, identities, cursors and semantic branches.
    private const string PairId = "change-contract";
    private const string OtherPair = "other-contract";
    private const string LocalRoot = "C:\\Synthetic";
    private const string RemoteRoot = "/Synthetic";
    private const string Connection = "connection";
    private const string Account = "account";
    private const string Domain = "namespace";
    private const string Root = "root-id";
    private const string Volume = "volume";
    private const string File = "Parent/File";
    private const string Renamed = "Parent/Renamed";
    private const string OtherFile = "Sibling/File";
    private const string Identity = "identity";
    private const string OtherIdentity = "other-identity";
    private const string InitialCursor = "cursor-initial";
    private const string NextCursor = "cursor-next";
    private const string LastCursor = "cursor-last";
    private const string FolderPrefix = "syncshark-change-contract-";
    private const string Database = "state.db";
    private const string GuidFormat = "N";
    private const string AtomicPage = "atomic-page";
    private const string OrderedPage = "ordered-page";
    private const string FiniteMembership = "finite-membership";
    private const string GapResponsibility = "gap-responsibility";
    private const string DirtyEvidence = "dirty-evidence";
    private const string ResidualOwnership = "residual-ownership";
    private const string ExpectedFailure = "expected-failure";
    private const string ListedBoundary = "listed-boundary";
    private const string LostInterval = "lost-interval";

    public static void Run()
    {
        WithStore(RenameResponsibilitySurvives);
        WithStore(store => PersistedRenameBootstrap(store, false));
        WithStore(store => PersistedRenameBootstrap(store, true));
        WithStore(LivePeerClaimSurvives);
        WithStore(PageRollback);
        WithStore(store => ReturningRelease(store));
        using (var memory = new InMemoryStateStore()) ReturningRelease(memory);
        RepairHeldSelection();
        WithStore(StaleFenceCases);
        WithStore(OrderedAndFinite);
        SeparateWorkProbeCounts();
        WithStore(JournalRollbackAndGap);
        WithStore(LostIntervalCases);
        WithStore(DirtyOutsideSelection);
        foreach (var echo in Enum.GetValues<PublishedEcho>()) WithStore(store => OwnPublicationEcho(store, echo));
        WithStore(DirtyMemoryTransitions);
        WithStore(DirtyTransfersOnConfiguration);
        WithStore(DirtyDroppedOnDomainChange);
        WithStore(DirtyDroppedOnReset);
        WithStore(ResidualRegistration);
        foreach (var priorDirty in new[] { false, true })
        foreach (var incomingDirty in new[] { false, true })
        foreach (var priorUsn in new ulong?[] { null, 400, 600 })
        foreach (var incomingUsn in new ulong?[] { null, 400, 600 })
            WithStore((store, database) => ResidualContentProvenance(store, database,
                priorDirty, priorUsn, incomingDirty, incomingUsn));
        WithStore((_, database) => PublishedEpisodeProvenance(database));
        foreach (var generationChange in new[] { false, true })
            WithStore(store => RegistrationLifecycle(store, generationChange));
        WithStore(FailurePassProgress);
        WithStore(AncestorAttemptOnce);
        AncestorProbeCounts();
        WithStore(ReleasedResidualSelectionRefresh);
        ReopenCommittedCursor();
        ItemResponsibility();
        foreach (var acquired in new[] { false, true })
            foreach (var dirty in new[] { false, true })
                WithStore((_, database) => InUseNoticeOpportunity(database, acquired, dirty));
        foreach (var acquired in new[] { false, true })
        {
            WithStore((_, database) => OlderDirtyEpisode(database, acquired));
            WithStore((_, database) => OlderDirtyEpisodeAttachment(database, acquired));
        }
        foreach (var scenario in new[] { "Rollback", "ForeignMember", "PendingOverlap", "PendingElsewhere",
            "Rename", "HardLink", "ExistingChild" })
            WithStore((_, database) => EpisodeTransferGuard(database, scenario));
        WithStore((_, database) => ReplacedInUseFile(database));
        A1NoticeRetirement();
        WithStore((_, database) => ReplacedWithStaleMember(database));
        WithStore((_, database) => ReplacedWithStaleMember(database, vanished: true));
        WithStore((_, database) => VanishedHeldFile(database, Identity));
        WithStore((_, database) => VanishedHeldFile(database, OtherIdentity));
        var b203H2Failures = new List<string>();
        B203H2Cases(b203H2Failures);
        Check.Require(b203H2Failures.Count == 0, "B-203 H2 reproduced: " + string.Join(" | ", b203H2Failures));
        foreach (var mixed in new[] { false, true })
            foreach (var own in new[] { false, true })
                WithStore((_, database) => InUseMemberWithUnknownItem(database, mixed, own));
        WithStore((_, database) => EpisodeElsewhereCompletes(database, Identity));
        WithStore((_, database) => EpisodeElsewhereCompletes(database, OtherIdentity));
        WithStore((_, database) => EpisodeElsewhereCompletes(database, Identity, memberOnly: true));
        WithStore((_, database) => CaseRenameHeldCompletes(database));
        AcquiredRenameRetry();
        WithStore(SelectionAndCoverageCases);
        WithStore(RenameBaselineCases);
        WithStore(OwnRecordBaseline);
        WithStore(ReplacedIdentityBaseline);
        WithStore(ExactFolderAdmission);
        foreach (var dirty in new[] { false, true }) WithStore((store, database) => DeletedChildAdmission(store, database, dirty));
        WithStore(ObservedIdentityAdmission);
        foreach (var mapped in new[] { false, true })
            WithStore((store, database) => LeaveAncestorPrecedence(store, database, mapped));
        foreach (var side in new[] { Side.Local, Side.Dropbox })
            foreach (var deleting in new[] { false, true })
                WithStore((store, database) => OppositeDeletionPrecedence(store, database, side, deleting));
        SelectionRefreshAfterFolderMoveCounts();
        foreach (var subtree in new[] { false, true })
            WithStore(store => ForgottenSelectionBaseline(store, subtree));
        WithStore(ListedAncestorBoundary);
        WithStore(StaleSourceAncestorCase);
        WithStore(PersistentForeignCase);
        WithStore(MoveBackCase);
        WithStore(CompetingCommitCase);
        RestartAndDeletedCase();
        MatchingAncestorCases();
        WithStore(ProbedKeySafetyCase);
        WithStore((_, database) =>
        {
            var attempt = Guid.NewGuid();
            using var registered = StateStore.Open(database, (id, _, _, _, _) => id == attempt);
            WorkIssueSafetyCase(registered, attempt);
        });
        WithStore(ResidualSafetyCase);
        WithStore(IsolatedResidualSafetyCases);
        foreach (var unknown in new[] { false, true })
        {
            WithStore(store => UnknownMoveWork(store, unknown));
            using var memory = new InMemoryStateStore();
            memory.SavePair(Pair(PairId));
            UnknownMoveWork(memory, unknown);
        }
        WithStore(store => ClashRefusalCases(store));
        using (var memory = new InMemoryStateStore())
        {
            memory.SavePair(Pair(PairId));
            ClashRefusalCases(memory);
        }
        IdentityGroupBehindBacklog();
        PassTotal();
        foreach (var check in new Action<IStateStore>[]
                     { OutOfScopeSelection, OutOfScopeProgress, OutOfScopeCustody, OutOfScopeLocal,
                       OutOfScopeIdentityBaseline, OutOfScopeCleanThenDirty, OutOfScopeUnreadable })
        {
            WithStore(store => check(store));
            using var memory = new InMemoryStateStore();
            memory.SavePair(Pair(PairId));
            check(memory);
        }
        WithStore(OutOfScopeDirtyCost);
    }

    // keepRepairObligated holds Repair debt owed after the first Repair (R6): released work must still be selected.
    internal static void ReturningRelease(IStateStore store, bool keepRepairObligated = false,
        Action? firstRepairDone = null)
    {
        var pair = Pair(PairId);
        store.SavePair(pair);
        if (keepRepairObligated) (firstRepairDone ?? throw new ArgumentNullException(nameof(firstRepairDone)))();
        var generation = store.ReadPairExecution(PairId)!.Value.Generation;
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            if (keepRepairObligated) continue;
            var source = store.ReadSource(PairId, side)!.Value;
            store.AdvanceSource(PairId, side, generation, source.Epoch, source.Revision,
                source.Provenance, source.Cursor, source.Epoch, source.Revision, source.Provenance, source.Cursor, null);
        }
        Check.Require(store.Obligated(PairId) == keepRepairObligated,
            "Advancing a source to a null obligation clears it equally in SQLite and the fake.");
        var a = new SyncedEntry(PathNames.Key("Original"), "Original", PathNames.DropboxKey("RemotePartner"),
            "RemotePartner", false, "local-original", 1, 0, "remote-partner", "hash");
        var b = a with { LocalKey = PathNames.Key("LocalPartner"), LocalPath = "LocalPartner",
            DropboxKey = PathNames.DropboxKey("Original"), DropboxPath = "Original",
            LocalId = "local-partner", DropboxId = "remote-original" };
        store.Commit(PairId, StateChange.Empty with { Record = [a, b] });
        var after = a with { DropboxKey = b.DropboxKey, DropboxPath = b.DropboxPath, DropboxId = b.DropboxId };
        var start = store.ReadWorkWatermark();
        try
        {
            store.Commit(PairId, StateChange.Empty with { ForgetSelf = [(Side.Local, a.LocalKey)],
                Record = [after], FirstRunAcknowledged = [new IssueItem(Side.Dropbox, "Original", null!)] });
            throw new InvalidOperationException("Invalid acknowledgement must roll back.");
        }
        catch (ArgumentException) { }
        Check.Require(store.ReadWorkWatermark() == start && store.SyncedByKey(PairId, Side.Local, a.LocalKey) == a,
            "A refused change publishes neither replacement nor release work.");
        store.Commit(PairId, StateChange.Empty with { ForgetSelf = [(Side.Local, a.LocalKey)], Record = [after] });
        Check.Require(store.ReadWorkWatermark() == start + 2 && store.SyncedByKey(PairId, Side.Local, after.LocalKey) == after,
            "Both exact RETURNING result sets register one released endpoint, including ForgetSelf deduplication.");
        var scope = new PathScope(pair, [pair]);
        var first = store.SelectWork(PairId, generation, scope)!;
        Check.Require(first is not null, "A committed released endpoint is selectable in ordinary acquired work.");
        var observed = new List<PendingChange>();
        for (var selection = first; selection is not null; selection = store.SelectWork(PairId, generation, scope))
            observed.AddRange(store.ReadWork(selection));
        Check.Require(observed.Count == 2 && observed.All(work => work.Before is null && work.Notice.Kind == ChangeKind.Changed) &&
            observed.Any(work => work.Notice.Side == Side.Local && work.Notice.Path == b.LocalPath && work.Notice.Id == b.LocalId) &&
            observed.Any(work => work.Notice.Side == Side.Dropbox && work.Notice.Path == a.DropboxPath && work.Notice.Id == a.DropboxId),
            "Release work carries each endpoint's own identity, path and side with no inherited baseline.");
        // No selected work is retired: only a new release can request another pass here.
        var c = a with { LocalKey = PathNames.Key("Second"), LocalPath = "Second", LocalId = "second-local",
            DropboxKey = PathNames.DropboxKey("SecondPartner"), DropboxPath = "SecondPartner", DropboxId = "second-remote" };
        store.Commit(PairId, StateChange.Empty with { Record = [c] });
        var next = c with { DropboxKey = c.LocalKey, DropboxPath = c.LocalPath, DropboxId = "returned" };
        store.Commit(PairId, StateChange.Empty with { Record = [next] });
        Check.Require(store.SelectWork(PairId, generation, scope) is null && store.EndWorkPass(PairId),
            "A release beyond the captured watermark requests a fresh pass with zero net retirements.");
        var watermark = store.ReadWorkWatermark();
        store.Commit(PairId, StateChange.Empty with { Record = [next] });
        Check.Require(store.ReadWorkWatermark() == watermark, "Re-recording an ordinary binding creates no release loop.");
    }

    public static void RepairOverlap()
    {
        foreach (var residualBeforeScan in new[] { false, true })
            WithStore((store, database) =>
            {
                var provenance = Install(store);
                CompletePrerequisite(database);
                Acquire(store, provenance, NextCursor, [Notice(Side.Dropbox, File, Identity)]);
                var selection = store.SelectWork(PairId, provenance.PairGeneration,
                    new PathScope(store.Pair(PairId)!, store.Pairs()))!;
                Check.Require(selection is not null, "S-G1 selects live work before Repair completion");
                Check.Require(store.BeginSelection(selection!) is not null, "Selection setup must be admitted.");
                var subject = new IssueSubject([new IssueItem(Side.Dropbox, OtherFile, OtherIdentity)], null);
                store.Commit(PairId, StateChange.Empty with
                    { Raise = [(IssueKind.Failure, TextKey.FailChanged, OtherFile, subject)] });
                var issue = store.Issues(PairId).Single();
                void Residual() => store.AttachIssueWork(selection!, issue,
                    [(Notice(Side.Dropbox, OtherFile, OtherIdentity), false, null)]);
                if (residualBeforeScan) Residual();
                var token = store.BeginScan(PairId, Guid.NewGuid(), provenance.PairGeneration, false);
                if (!residualBeforeScan) Residual();
                var local = store.ReadSource(PairId, Side.Local)!.Value;
                var through = store.ReadWorkWatermark();
                store.AcquireJournalPage("0", null, new JournalPosition(0, 0),
                    [(PairId, provenance.PairGeneration, local.Epoch, local.Revision, false)], []);
                store.FinishRepair(PairId, CompactStorageChecks.Summary(), provenance.PairGeneration, token,
                    new LocalCatchUp(PairId, provenance.PairGeneration, local.Epoch,
                        new NtfsUsnCheckpoint(0, 0, 0, 0), through),
                    new DropboxCatchUp(PairId, provenance.PairGeneration, provenance.SourceEpoch, NextCursor, through), out _);
                Check.Require(store.ReadWork(selection!).Any(),
                    "S-G1 Repair retirement preserves the active finite selection");
                store.CompleteWork(selection!, null);
                var residual = PendingWork.ByIdentity(store, Side.Dropbox, Domain, OtherIdentity).Single();
                Check.Require(store.ReadWork(residual).Single().Notice.Path == OtherFile,
                    "S-G1 residual survives on either side of the captured scan watermark");
                store.EndSelection(selection!);
                store.EndWorkPass(PairId);
                store.EndScanAsync(PairId, token, 1).GetAwaiter().GetResult();
            });
    }

    private static void WithStore(Action<StateStore> check) => WithStore((store, _) => check(store));

    private static void WithStore(Action<StateStore, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), FolderPrefix + Guid.NewGuid().ToString(GuidFormat));
        Directory.CreateDirectory(directory);
        try
        {
            using var store = StateStore.Open(Path.Combine(directory, Database));
            store.SavePair(Pair(PairId));
            check(store, Path.Combine(directory, Database));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static PairConfig Pair(string id) => new(id, id, LocalRoot, RemoteRoot, Connection,
        [], [], FirstRunPolicy.NewestWins, true, null,
        new RootBinding(LocalRoot, RemoteRoot, Connection, new DirectoryIdentity(0, 0, null),
            new DropboxRootIdentity(Account, Domain, Domain, Root)));

    /// <summary>B148-RC-R3: one acquired selection's projection and admission, bound and unbound, cost the same store
    /// commands, reader connections, rows and baseline-lookup VM work as unrelated saved bindings grow (W08 regression
    /// sizes). No seam sees the statements inside a store call: StoreCounters forwards whole commands, StorageCommand
    /// only creates them and the store's connection is private, so the added saved-binding statement is measured
    /// through CostProbe on its own.</summary>
    public static void SelectionProjectionCosts()
    {
        var previous = new Dictionary<bool, (long Commands, long Reads, long Rows, long VmSteps)>();
        foreach (var background in new[] { 5_110, 20_440 })
        foreach (var bound in new[] { false, true })
            WithStore((store, database) =>
            {
                var provenance = InstallWithoutObligations(store);
                if (bound)
                    store.Commit(PairId, StateChange.Empty with { Record = [new SyncedEntry(PathNames.Key(File), File,
                        PathNames.DropboxKey(File), File, false, "local-identity", 1, 0, Identity, "hash")] });
                using (var seed = new CostProbe(database))
                    // Fixture-only bulk setup, as in ActionExecutorChecks.FileSizedForgetCosts.
                    seed.Execute("WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<$count) " +
                        "INSERT INTO synced_entry(pair_id,local_path,is_folder,local_id,local_size,local_mtime,dropbox_id,dropbox_hash) " +
                        "SELECT (SELECT id FROM pair WHERE pair_id=$p),'background/'||i,0,'l'||i,1,0,'d'||i,'hash' FROM n;",
                        ("$count", background), ("$p", PairId));
                Acquire(store, provenance, NextCursor, [Notice(Side.Dropbox, File, Identity)]);
                var counted = new StoreCounters(store);
                var scope = new PathScope(store.Pair(PairId)!, store.Pairs());
                counted.Reset();
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var selection = counted.SelectWork(PairId, provenance.PairGeneration, scope);
                var begun = selection is null ? null : counted.BeginSelection(selection);
                allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                Check.Require(selection?.Obligation is { } key && begun?.Obligation == key,
                    "An unchanged baseline gives the projected and begun selection the same key; bound=" + bound);
                using var probe = new CostProbe(database);
                var lookup = probe.ExecuteFirst(StateSchema.SelectSyncedByDropboxId,
                    (StateSchema.ParamPair, PairId), (StateSchema.ParamId, Identity));
                var cost = (counted.Commands, Reads: counted.ReadConnections, Rows: counted.RowsRead, lookup.VmSteps);
                Check.Require(cost.Commands == 2, "Projection and admission are two store commands; commands=" + cost.Commands);
                if (previous.TryGetValue(bound, out var before))
                    Check.Require(cost == before, "One acquired selection costs the same commands, reads, rows and " +
                        "baseline-lookup VM " +
                        "steps as saved bindings grow; bound=" + bound + "; " + cost + " vs " + before);
                previous[bound] = cost;
                Console.WriteLine($"selection-projection background={background}; bound={bound}; commands={cost.Commands}; " +
                    $"reads={cost.Reads}; rows={cost.Rows}; baseline-lookup={lookup}; " +
                    $"thread-allocated={allocated}");
            });
    }

    private static SourceProvenance Install(IStateStore store, string pairId = PairId)
    {
        var generation = store.ReadPairExecution(pairId)!.Value.Generation;
        var source = store.ReadSource(pairId, Side.Dropbox)!.Value;
        var provenance = new SourceProvenance(Account, Domain, Root, RemoteRoot,
            true, true, true, true, generation, source.Epoch);
        store.AdvanceSource(pairId, Side.Dropbox, generation, source.Epoch, source.Revision,
            source.Provenance, source.Cursor, source.Epoch, source.Revision,
            provenance, InitialCursor, source.Obligation);
        return provenance;
    }

    // Installs the Dropbox source and clears both sides' Repair obligations, so acquired work is selectable.
    private static SourceProvenance InstallWithoutObligations(IStateStore store, string pairId = PairId)
    {
        var provenance = Install(store, pairId);
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            var source = store.ReadSource(pairId, side)!.Value;
            store.AdvanceSource(pairId, side, provenance.PairGeneration, source.Epoch, source.Revision,
                source.Provenance, source.Cursor, source.Epoch, source.Revision, source.Provenance, source.Cursor, null);
        }
        return provenance;
    }

    private static long Acquire(IStateStore store, SourceProvenance provenance, string cursor,
        IEnumerable<ChangeNotice> notices, string pairId = PairId)
    {
        var source = store.ReadSource(pairId, Side.Dropbox)!.Value;
        return store.AcquireDropboxPage(pairId, provenance.PairGeneration, source.Epoch,
            source.Revision, provenance, source.Cursor, notices, cursor);
    }

    private static ChangeNotice Notice(Side side, string path, string? identity,
        bool dirty = false, ChangeKind kind = ChangeKind.Changed, string? oldPath = null) =>
        new(side, kind, path, oldPath, identity, null, dirty, false);

    private static WorkSelection Selection(SourceProvenance provenance, long anchor, long through,
        string? identity = Identity, string path = File) => new(PairId, provenance.PairGeneration,
            provenance.SourceEpoch, Side.Dropbox, Domain, identity, PathNames.DropboxKey(path), anchor, through);

    // BRIEF-UNKNOWN-MOVE-IMPL U3d/U3f: work linked to an unknown move stays unselected and the move's destination refuses
    // live admission; the same fixture with an ordinary Failure admits and selects. Run on both stores.
    private static void UnknownMoveWork(IStateStore store, bool unknown)
    {
        var name = (store is StateStore ? " real" : " in-memory") + (unknown ? ", unknown move" : ", ordinary Failure");
        const string destination = "Moved/File";
        var provenance = InstallWithoutObligations(store);
        var through = Acquire(store, provenance, NextCursor, [Notice(Side.Dropbox, File, Identity)]);
        var scope = new PathScope(store.Pair(PairId)!, store.Pairs());
        var parent = store.SelectWork(PairId, provenance.PairGeneration, scope) ??
            throw new InvalidOperationException(FiniteMembership + name);
        Check.Require(store.BeginSelection(parent) is not null, "Selection setup must be admitted." + name);
        var subject = new IssueSubject([new IssueItem(Side.Dropbox, OtherFile, OtherIdentity)], null)
            { Move = unknown ? new IssueSubject.UnknownMove(OtherIdentity, OtherFile, destination, null, false) : null };
        store.Commit(PairId, StateChange.Empty with { Raise = [(IssueKind.Failure, TextKey.FailChanged, OtherFile, subject)] });
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var volume = Pair(PairId).Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        store.AcquireJournalPage(volume, null, new JournalPosition(0, 0),
            [(PairId, provenance.PairGeneration, local.Epoch, local.Revision, false)], []);
        var coverage = store.CaptureLiveCoverage(parent,
            new LocalCatchUp(PairId, provenance.PairGeneration, local.Epoch, new NtfsUsnCheckpoint(0, 0, 0, 0), through),
            new DropboxCatchUp(PairId, provenance.PairGeneration, provenance.SourceEpoch, NextCursor, through));
        LiveEffectScope[] footprint =
        [
            new(Side.Dropbox, PathNames.RootedDropboxPath(RemoteRoot, File), false),
            new(Side.Dropbox, PathNames.RootedDropboxPath(RemoteRoot, destination), false)
        ];
        var refused = false;
        try { store.ValidateLiveCoverage(parent, coverage, footprint); }
        catch (LiveAdmissionStaleException) { refused = true; }
        Check.Require(refused == unknown, "U3f live admission refuses only an unknown move's destination" + name);
        store.AttachIssueWork(parent, store.Issues(PairId).Single(),
            [(Notice(Side.Dropbox, OtherFile, OtherIdentity), false, null)]);
        store.CompleteWork(parent, null);
        store.EndSelection(parent);
        store.EndWorkPass(PairId);
        Check.Require((store.SelectWork(PairId, provenance.PairGeneration, scope) is null) == unknown,
            "U3d work linked to an unknown move stays unselected" + name);
        store.EndWorkPass(PairId);
    }

    // DESIGN-B123-REFUSAL-REPEAT-1: a repeated name-clash refusal is Covered only while the open clash continues the
    // subject and the member's registration holds current fences and evidence. Run on both stores.
    private static void ClashRefusalCases(IStateStore store)
    {
        var name = store is StateStore ? " real" : " in-memory";
        var provenance = InstallWithoutObligations(store);
        var id = Acquire(store, provenance, NextCursor, [Notice(Side.Dropbox, File, Identity)]);
        var parent = Selection(provenance, id, id);
        var subject = new IssueSubject([new IssueItem(Side.Dropbox, Renamed, OtherIdentity),
            new IssueItem(Side.Local, Renamed, "occupant")], Side.Dropbox);
        var clean = Notice(Side.Dropbox, Renamed, OtherIdentity);
        ClashRefusal State(WorkSelection selection, ChangeNotice notice, IssueSubject? raised = null,
            string path = Renamed, long? issue = null)
        {
            var (state, existing) = store.ClashRefusalState(selection, path, raised ?? subject, (notice, false, null));
            Check.Require(state == ClashRefusal.Raise ? existing is null : existing is not null &&
                (issue is null || existing.Id == issue), "B-123 only a continuing refusal carries its issue" + name);
            return state;
        }
        Check.Require(State(parent, clean) == ClashRefusal.Raise, "B-123 Raise with no open clash" + name);
        store.Commit(PairId, StateChange.Empty with
            { Raise = [(IssueKind.NameClash, TextKey.ClashNameTaken, Renamed, subject)] });
        var clash = store.Issues(PairId).Single(issue => issue.Kind == IssueKind.NameClash);
        Check.Require(State(parent, clean, issue: clash.Id) == ClashRefusal.Attach,
            "B-123 Attach a member missing from the continuing clash" + name);
        store.AttachIssueWork(parent, clash, [(clean, false, null)]);
        var watermark = store.ReadWorkWatermark();
        Check.Require(State(parent, clean, issue: clash.Id) == ClashRefusal.Covered &&
            store.ReadWorkWatermark() == watermark, "B-123 Covered once the member is registered" + name);
        var instant = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var observed = clean with { Observed = new Entry(Renamed, false, 1, instant, OtherIdentity, "", "", null) };
        store.AttachIssueWork(parent, clash, [(observed, false, null)]);
        Check.Require(State(parent, observed with { Observed = observed.Observed! with
                { Modified = instant.ToOffset(TimeSpan.FromHours(2)) } }) == ClashRefusal.Attach,
            "B-123 Attach a notice whose stored form differs only in its time offset" + name);
        store.AttachIssueWork(parent, clash, [(clean, false, null)]);
        var dirty = clean with { LocalContentDirty = true, Usn = 9 };
        Check.Require(State(parent, dirty) == ClashRefusal.Attach, "B-123 Attach newer dirty evidence" + name);
        store.AttachIssueWork(parent, clash, [(dirty, false, null)]);
        Check.Require(State(parent, dirty) == ClashRefusal.Covered && State(parent, clean) == ClashRefusal.Covered,
            "B-123 registered dirt is Covered, and a clean hint never erases it" + name);
        Check.Require(State(parent, dirty with { Usn = 12 }) == ClashRefusal.Attach,
            "B-123 Attach a newer USN" + name);
        Check.Require(State(parent, clean, new IssueSubject([new IssueItem(Side.Dropbox, Renamed, OtherIdentity),
                new IssueItem(Side.Local, Renamed, "other-occupant")], Side.Dropbox)) == ClashRefusal.Raise,
            "B-123 Raise a changed occupant" + name);
        Check.Require(State(parent, clean, path: OtherFile) == ClashRefusal.Raise,
            "B-123 Raise a different path" + name);
        var source = store.ReadSource(PairId, Side.Dropbox)!.Value;
        var next = provenance with { SourceEpoch = source.Epoch + 1 };
        store.AdvanceSource(PairId, Side.Dropbox, provenance.PairGeneration, source.Epoch, source.Revision,
            source.Provenance, source.Cursor, next.SourceEpoch, source.Revision + 1, next, source.Cursor,
            source.Obligation);
        var nextId = Acquire(store, next, LastCursor, [Notice(Side.Dropbox, File, Identity)]);
        var nextParent = Selection(next, nextId, nextId);
        Check.Require(State(nextParent, dirty, issue: clash.Id) == ClashRefusal.Attach,
            "B-123 Attach a registration from a changed epoch" + name);
        store.AttachIssueWork(nextParent, clash, [(dirty, false, null)]);
        Check.Require(State(nextParent, dirty) == ClashRefusal.Covered, "B-123 Covered at the current epoch" + name);
        bool Stale(WorkSelection selection)
        {
            try { store.ClashRefusalState(selection, Renamed, subject, (dirty, false, null)); }
            catch (LiveAdmissionStaleException) { return true; }
            return false;
        }
        Check.Require(Stale(nextParent with { AnchorWorkId = nextId + 1 }) &&
            Stale(nextParent with { Key = PathNames.DropboxKey(OtherFile) }),
            "B-123 a missing anchor or a changed anchor descriptor is a stale selection" + name);
        var replaced = new IssueSubject([new IssueItem(Side.Dropbox, Renamed, OtherIdentity),
            new IssueItem(Side.Local, Renamed, "replacement-occupant")], Side.Dropbox);
        store.Commit(PairId, StateChange.Empty with
            { Raise = [(IssueKind.NameClash, TextKey.ClashNameTaken, Renamed, replaced)] });
        Check.Require(State(nextParent, dirty, replaced) == ClashRefusal.Attach,
            "B-123 Attach a member still associated with the clash's replaced subject" + name);
        var current = store.Issues(PairId).Single(issue => issue.Kind == IssueKind.NameClash);
        store.AttachIssueWork(nextParent, current, [(dirty, false, null)]);
        Check.Require(State(nextParent, dirty, replaced, issue: current.Id) == ClashRefusal.Covered,
            "B-123 Covered once the member is associated with the replacement" + name);
        store.DismissIssue(PairId, current.Id);
        Check.Require(State(nextParent, dirty, replaced) == ClashRefusal.Raise, "B-123 Raise a dismissed clash" + name);
    }

    // B-133: the executor offers each selection to CompleteOutOfScope after its ancestor scheduling and before
    // observing it. This loop does the same; selections it keeps are ended (complete: false) or completed.
    private static (List<string?> Completed, List<string?> Selected) DrainScoped(IStateStore store, long generation,
        PathScope scope, bool complete = true)
    {
        var completed = new List<string?>();
        var selected = new List<string?>();
        while (store.SelectWork(PairId, generation, scope) is { } selection)
        {
            if (store.CompleteOutOfScope(selection, scope))
            {
                completed.Add(selection.Identity);
                continue;
            }
            selected.Add(selection.Identity);
            if (!complete) store.EndSelection(selection);
            else Check.Require(store.BeginSelection(selection) is not null && store.CompleteWork(selection, null),
                "B-133 a kept selection completes on its ordinary path");
        }
        return (completed, selected);
    }

    private static PathScope Excluding(IStateStore store, params Exclusion[] exclusions) =>
        new(store.Pair(PairId)! with { Exclusions = exclusions }, store.Pairs());

    // B-133: Dropbox work naming only excluded paths, dirty or clean and under a Keep or a pending non-Keep
    // exclusion, completes without observation. A move into the excluded folder, a move out of it, a binding whose
    // Local side is in scope and a same-text sibling keep their ordinary path; nothing returns on a later wake.
    private static void OutOfScopeSelection(IStateStore store)
    {
        var name = store is StateStore ? " real" : " in-memory";
        const string bound = "Excluded/Bound";
        var provenance = InstallWithoutObligations(store);
        store.Commit(PairId, StateChange.Empty with { Record = [new SyncedEntry(PathNames.Key("Kept/Bound"),
            "Kept/Bound", PathNames.DropboxKey(bound), bound, false, "bound-local", 0, 0, "bound", "")] });
        Acquire(store, provenance, NextCursor,
        [
            Notice(Side.Dropbox, "Excluded/File", "inside"), Notice(Side.Dropbox, "Excluded/Folder", "folder"),
            Notice(Side.Dropbox, "Excluded/Moved", "crossing", kind: ChangeKind.Renamed, oldPath: "Kept/Moved"),
            Notice(Side.Dropbox, "Excluded/Dirty", "dirty", dirty: true),
            Notice(Side.Dropbox, "Kept/MovedOut", "moved-out", kind: ChangeKind.Renamed,
                oldPath: "Excluded/MovedOut"),
            Notice(Side.Dropbox, "Pending/File", "pending"), Notice(Side.Dropbox, bound, "bound"),
            Notice(Side.Dropbox, "ExcludedSibling/File", "sibling")
        ]);
        var scope = Excluding(store, new Exclusion(Side.Dropbox, "Excluded", true),
            new Exclusion(Side.Dropbox, "Pending", false));
        var (completed, selected) = DrainScoped(store, provenance.PairGeneration, scope);
        Check.Require(completed.SequenceEqual(["inside", "folder", "dirty", "pending"]) &&
            selected.SequenceEqual(["crossing", "moved-out", "bound", "sibling"]),
            "B-133 only wholly excluded Dropbox work completes without observation: completed " +
            string.Join(",", completed) + "; selected " + string.Join(",", selected) + name);
        store.EndWorkPass(PairId);
        Check.Require(store.SelectWork(PairId, provenance.PairGeneration, new PathScope(store.Pair(PairId)!,
            store.Pairs())) is null, "B-133 completed excluded work does not return, even re-included" + name);
        store.EndWorkPass(PairId);
        Acquire(store, provenance, LastCursor, [Notice(Side.Dropbox, "Excluded/File", "inside")]);
        var reincluded = new PathScope(store.Pair(PairId)!, store.Pairs());
        Check.Require(store.SelectWork(PairId, provenance.PairGeneration, reincluded) is { Identity: "inside" } again &&
            !store.CompleteOutOfScope(again, reincluded),
            "B-133 the same path is selected and kept once it is back in scope" + name);
        store.EndWorkPass(PairId);
    }

    // B-133: a pass of only excluded work selects nothing runnable and reports progress; the next unchanged pass
    // reports none, so the excluded completion alone drives the repeat.
    private static void OutOfScopeProgress(IStateStore store)
    {
        var name = store is StateStore ? " real" : " in-memory";
        var provenance = InstallWithoutObligations(store);
        Acquire(store, provenance, NextCursor,
            [Notice(Side.Dropbox, "Excluded/One", "one"), Notice(Side.Dropbox, "Excluded/Two", "two")]);
        var scope = Excluding(store, new Exclusion(Side.Dropbox, "Excluded", true));
        var (completed, selected) = DrainScoped(store, provenance.PairGeneration, scope);
        Check.Require(completed.SequenceEqual(["one", "two"]) && selected.Count == 0 && store.EndWorkPass(PairId),
            "B-133 an excluded-only pass completes its work and reports progress" + name);
        var again = DrainScoped(store, provenance.PairGeneration, scope);
        Check.Require(again.Completed.Count == 0 && again.Selected.Count == 0 && !store.EndWorkPass(PairId),
            "B-133 the next unchanged pass finds nothing and reports no progress" + name);
    }

    // B-133: excluded work under issue custody keeps it. A residual of a retryable Failure is selected and kept, a
    // residual of an unknown move stays held and unselected, and work awaiting discovery is selected and kept.
    private static void OutOfScopeCustody(IStateStore store)
    {
        var name = store is StateStore ? " real" : " in-memory";
        var provenance = InstallWithoutObligations(store);
        var parentId = Acquire(store, provenance, NextCursor, [Notice(Side.Dropbox, File, Identity)]);
        var parent = Selection(provenance, parentId, parentId);
        store.Commit(PairId, StateChange.Empty with { Raise = [(IssueKind.Failure, TextKey.FailChanged,
            "Excluded/Retry", new IssueSubject([new IssueItem(Side.Dropbox, "Excluded/Retry", "retry")], null))] });
        var move = new IssueSubject.UnknownMove("unknown", "Excluded/Unknown", "Excluded/UnknownTo", null, false);
        store.Commit(PairId, StateChange.Empty with { Raise = [(IssueKind.Failure, TextKey.FailChanged,
            "Excluded/Unknown", new IssueSubject([new IssueItem(Side.Dropbox, "Excluded/Unknown", "unknown")], null)
            { Move = move })] });
        foreach (var (path, id) in new[] { ("Excluded/Retry", "retry"), ("Excluded/Unknown", "unknown") })
            store.AttachIssueWork(parent, store.Issues(PairId).Single(issue => issue.Path == path),
                [(Notice(Side.Dropbox, path, id), false, null)]);
        var unknownId = store.ReadWorkWatermark();
        store.CompleteWork(parent, null);
        var discoveryId = Acquire(store, provenance, LastCursor,
            [Notice(Side.Dropbox, "Excluded/Discovering", "discovering")]);
        var discovery = Selection(provenance, discoveryId, discoveryId, "discovering", "Excluded/Discovering");
        store.RequireDiscovery(discovery, discoveryId);
        var (completed, selected) = DrainScoped(store, provenance.PairGeneration,
            Excluding(store, new Exclusion(Side.Dropbox, "Excluded", true)), complete: false);
        store.EndWorkPass(PairId);
        Check.Require(completed.Count == 0 && selected.SequenceEqual(["retry", "discovering"]),
            "B-133 retryable residual and discovery work are selected and kept, the held one is not selected: " +
            string.Join(",", selected) + name);
        Check.Require(store.Issues(PairId).Count == 2 &&
            store.ReadWork(Selection(provenance, unknownId, discoveryId, "unknown", "Excluded/Unknown")).Count() == 1 &&
            store.ReadWork(discovery).Count() == 1,
            "B-133 both issues and the held residual and discovery work survive" + name);
    }

    // B-133: Local journal work. Clean excluded work completes without observation; Local dirty evidence, excluded or
    // not, keeps its ordinary path, including after the exclusion is saved and the generation carries it forward.
    // B-133 R3.2: excluded Local dirty work is refused on its member's own flag, so the refusal costs no more
    // statements than reading that first member.
    private static void OutOfScopeDirtyCost(StateStore store)
    {
        var provenance = InstallWithoutObligations(store);
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var volume = Pair(PairId).Roots!.Local.VolumeSerialNumber.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        store.AcquireJournalPage(volume, null, new JournalPosition(0, 0),
            [(PairId, provenance.PairGeneration, local.Epoch, local.Revision, false)],
            [(PairId, Notice(Side.Local, "Excluded/Dirty", "local-dirty", dirty: true))]);
        var scope = Excluding(store, new Exclusion(Side.Local, "Excluded", true));
        var selection = store.SelectWork(PairId, provenance.PairGeneration, scope) ??
            throw new InvalidOperationException("B-133 the dirty Local work selects");
        var completed = true;
        var refusal = Statements(store, () => completed = store.CompleteOutOfScope(selection, scope));
        var member = Statements(store, () => _ = store.ReadWork(selection).First());
        // R3.4: 072eb2fc1 read the membership straight away; the candidate adds only this refusal before that read.
        var membership = Statements(store, () => _ = store.ReadWork(selection).ToList());
        Console.WriteLine($"B-133 rejected Local dirty membership-read accounting: baseline read={membership}, " +
            $"candidate refusal+read={refusal}+{membership}, first member read={member}");
        Check.Require(!completed && refusal > 0 && refusal <= member,
            "B-133 Local dirty refusal reads only up to its first member: refusal=" + refusal + "; member=" + member);
        store.EndSelection(selection);
        store.EndWorkPass(PairId);
    }

    // B-133 R3.5: a binding reached only by identity refuses after the first member's flags and one baseline read,
    // before the path walk of any member, and leaves every member queued. Counted on the real store.
    private static void OutOfScopeIdentityBaseline(IStateStore store)
    {
        var name = store is StateStore ? " real" : " in-memory";
        var provenance = InstallWithoutObligations(store);
        const string kept = "Kept/Bound";
        store.Commit(PairId, StateChange.Empty with { Record = [new SyncedEntry(PathNames.Key(kept), kept,
            PathNames.DropboxKey(kept), kept, false, "bound-local", 0, 0, "bound", "")] });
        var cursors = new[] { NextCursor, LastCursor, "cursor-third" };
        for (var index = 0; index < cursors.Length; index++)
            Acquire(store, provenance, cursors[index], [Notice(Side.Dropbox, "Excluded/Member" + index, "bound")]);
        var scope = Excluding(store, new Exclusion(Side.Dropbox, "Excluded", true));
        var selection = store.SelectWork(PairId, provenance.PairGeneration, scope) ??
            throw new InvalidOperationException("B-133 the identity group selects" + name);
        var completed = true;
        var refusal = Statements(store, () => completed = store.CompleteOutOfScope(selection, scope));
        var member = Statements(store, () => _ = store.ReadWork(selection).First());
        Console.WriteLine($"B-133 identity-baseline refusal statements{name}: refusal={refusal} first-member={member}");
        Check.Require(!completed && store.ReadWork(selection).Count() == cursors.Length &&
            (store is not StateStore || refusal > 0 && refusal <= member + 1),
            "B-133 an in-scope identity binding refuses after one baseline read and keeps every member" + name +
            ": refusal=" + refusal + "; member=" + member);
        store.EndSelection(selection);
        store.EndWorkPass(PairId);
    }

    // B-133 R3.4a (accepted, bounded): clean excluded members ahead of a dirty one cost one pass over those members,
    // then the dirty flag refuses; every member stays queued for the ordinary path.
    private static void OutOfScopeCleanThenDirty(IStateStore store)
    {
        var name = store is StateStore ? " real" : " in-memory";
        var provenance = InstallWithoutObligations(store);
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var volume = Pair(PairId).Roots!.Local.VolumeSerialNumber.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        const int clean = 3;
        store.AcquireJournalPage(volume, null, new JournalPosition(0, 0),
            [(PairId, provenance.PairGeneration, local.Epoch, local.Revision, false)],
            [.. Enumerable.Range(0, clean).Select(index =>
                    (PairId, Notice(Side.Local, "Excluded/Clean" + index, "clean-then-dirty"))),
                (PairId, Notice(Side.Local, "Excluded/Dirty", "clean-then-dirty", dirty: true))]);
        var scope = Excluding(store, new Exclusion(Side.Local, "Excluded", true));
        var selection = store.SelectWork(PairId, provenance.PairGeneration, scope) ??
            throw new InvalidOperationException("B-133 the clean-then-dirty group selects" + name);
        var members = store.ReadWork(selection).Count();
        var completed = true;
        var refusal = Statements(store, () => completed = store.CompleteOutOfScope(selection, scope));
        var read = Statements(store, () => _ = store.ReadWork(selection).ToList());
        Console.WriteLine($"B-133 clean-then-dirty refusal statements{name}: refusal={refusal} " +
            $"membership read={read} members={members}");
        Check.Require(!completed && members == clean + 1 && store.ReadWork(selection).Count() == members,
            "B-133 a dirty member after clean ones refuses and leaves every member queued" + name);
        store.EndSelection(selection);
        store.EndWorkPass(PairId);
    }

    // Statements the real store runs for one action, counted by sqlite3_trace; 0 on the double.
    private static int Statements(IStateStore store, Action action, string? traceLabel = null)
    {
        if (store is not StateStore real)
        {
            action();
            return 0;
        }
        var connection = (Microsoft.Data.Sqlite.SqliteConnection)typeof(StateStore).GetField("_connection",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(real)!;
        var statements = 0;
        var groups = traceLabel is null ? null : new Dictionary<string, int>();
        SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)((_, sql) =>
        {
            statements++;
            if (groups is null) return;
            var text = sql.utf8_to_string();
            var kind = text.TrimStart().StartsWith("--", StringComparison.Ordinal) ? "subprogram" : "statement";
            text = System.Text.RegularExpressions.Regex.Replace(text, @"'(?:''|[^'])*'|\b\d+\b", "?");
            text = kind + " " + System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
            groups[text] = groups.GetValueOrDefault(text) + 1;
        }), null);
        try { action(); }
        finally { SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null); }
        if (groups is not null)
            foreach (var (sql, count) in groups.OrderBy(group => group.Key, StringComparer.Ordinal))
                Console.WriteLine($"B214 SQL {traceLabel} count={count} {sql}");
        return statements;
    }

    private static string AcquireLocal(IStateStore store, SourceProvenance provenance, params ChangeNotice[] notices)
    {
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var volume = Pair(PairId).Roots!.Local.VolumeSerialNumber.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        store.AcquireJournalPage(volume, null, new JournalPosition(0, 0),
            [(PairId, provenance.PairGeneration, local.Epoch, local.Revision, false)],
            notices.Select(notice => (PairId, notice)));
        return volume;
    }

    // P2-1: a clean excluded Local notice with a recorded protecting member at its path or below
    // keeps its ordinary path, where the proving walk can release that member; one without such a member, a member
    // only beside it, still completes without observation.
    private static void OutOfScopeUnreadable(IStateStore store)
    {
        var name = store is StateStore ? " real" : " in-memory";
        var provenance = InstallWithoutObligations(store);
        UnreadableMember.Record(store, PairId, "Excluded/Recovered/Locked");
        UnreadableMember.Record(store, PairId, "Excluded/Self", "67890");
        UnreadableMember.Record(store, PairId, "Excluded/Besides", "24680");
        AcquireLocal(store, provenance, Notice(Side.Local, "Excluded/Recovered", "recovered"),
            Notice(Side.Local, "Excluded/Self", "self"), Notice(Side.Local, "Excluded/Beside", "plain"));
        var (completed, selected) = DrainScoped(store, provenance.PairGeneration,
            Excluding(store, new Exclusion(Side.Local, "Excluded", true)), complete: false);
        store.EndWorkPass(PairId);
        Check.Require(completed.SequenceEqual(["plain"]) && selected.SequenceEqual(["recovered", "self"]) &&
            store.UnreadableUnder(PairId, PathNames.Key("Excluded")).Count == 3,
            "B-133 a recorded protecting member at or under clean excluded Local work keeps it on its ordinary path: " +
            "completed " + string.Join(",", completed) + "; selected " + string.Join(",", selected) + name);
    }

    private static void OutOfScopeLocal(IStateStore store)
    {
        var name = store is StateStore ? " real" : " in-memory";
        var provenance = InstallWithoutObligations(store);
        var volume = AcquireLocal(store, provenance, Notice(Side.Local, "Excluded/Clean", "local-clean"),
            Notice(Side.Local, "Excluded/Dirty", "local-dirty", dirty: true),
            Notice(Side.Local, "Kept/Dirty", "kept-dirty", dirty: true));
        var exclusion = new Exclusion(Side.Local, "Excluded", true);
        var (completed, selected) = DrainScoped(store, provenance.PairGeneration, Excluding(store, exclusion),
            complete: false);
        store.EndWorkPass(PairId);
        bool Dirty(long generation, string identity, string path) => store.ReadLocalDirty(PairId, generation,
            store.ReadSource(PairId, Side.Local)!.Value.Epoch, volume, identity, PathNames.Key(path), null).Dirty;
        Check.Require(completed.SequenceEqual(["local-clean"]) &&
            selected.SequenceEqual(["local-dirty", "kept-dirty"]) &&
            Dirty(provenance.PairGeneration, "local-dirty", "Excluded/Dirty") &&
            Dirty(provenance.PairGeneration, "kept-dirty", "Kept/Dirty"),
            "B-133 clean excluded Local work completes; Local dirty evidence stays on its ordinary path" + name);
        var pair = store.Pair(PairId)!;
        store.SavePairDecision(pair, provenance.PairGeneration, pair with { Exclusions = [exclusion] }, null, false,
            null);
        var carried = InstallWithoutObligations(store);
        // The decision leaves the Local evidence gap owed until a Repair (StateSchema.AdvanceSource keeps it), and R6
        // refuses live work before the first Repair, so the fixture marks that Repair done before the live drain.
        if (store is InMemoryStateStore memory) memory.MarkFirstRepairDone(PairId);
        else MarkFirstRepairDone(((Microsoft.Data.Sqlite.SqliteConnection)typeof(StateStore).GetField(
            "_connection", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(store)!).DataSource);
        var after = DrainScoped(store, carried.PairGeneration, new PathScope(store.Pair(PairId)!, store.Pairs()),
            complete: false);
        store.EndWorkPass(PairId);
        Check.Require(carried.PairGeneration == provenance.PairGeneration + 1 && after.Completed.Count == 0 &&
            after.Selected.SequenceEqual(["local-dirty", "kept-dirty"]) &&
            Dirty(carried.PairGeneration, "local-dirty", "Excluded/Dirty") &&
            Dirty(carried.PairGeneration, "kept-dirty", "Kept/Dirty"),
            "B-133 carried Local dirty evidence under a saved exclusion keeps its ordinary path" + name);
    }

    private static void PersistedRenameBootstrap(StateStore store, bool replaceClash)
    {
        var provenance = InstallWithoutObligations(store);
        var incoming = new SyncedEntry(PathNames.Key(File), File, PathNames.DropboxKey(File), File,
            false, "incoming-local", 0, 0, Identity, "");
        var destination = new SyncedEntry(PathNames.Key(Renamed), Renamed, PathNames.DropboxKey(Renamed), Renamed,
            false, "occupant-local", 0, 0, OtherIdentity, "");
        store.Commit(PairId, StateChange.Empty with { Record = [incoming, destination] });
        var acquired = Acquire(store, provenance, NextCursor, [Notice(Side.Dropbox, Renamed, Identity, kind: ChangeKind.Renamed, oldPath: File)]);
        var parent = Selection(provenance, acquired, acquired, path: Renamed);
        store.BeginSelection(parent);
        store.Commit(PairId, StateChange.Empty with { Raise = [(IssueKind.NameClash, TextKey.ClashNameTaken, Renamed,
            new IssueSubject([new IssueItem(Side.Dropbox, Renamed, Identity),
                new IssueItem(Side.Local, Renamed, destination.LocalId)], Side.Dropbox))] });
        var clash = store.Issues(PairId).Single(issue => issue.Kind == IssueKind.NameClash);
        store.AttachIssueWork(parent, clash,
            [(Notice(Side.Dropbox, Renamed, Identity, kind: ChangeKind.Renamed, oldPath: File), false, incoming)]);
        store.CompleteWork(parent, null);
        store.EndWorkPass(PairId);
        var selected = store.SelectWork(PairId, provenance.PairGeneration, new PathScope(store.Pair(PairId)!, store.Pairs()));
        Check.Require(selected is not null && store.ReadRenameContinuation(selected) is { Clash: not null } continuation &&
            continuation.Destination == destination,
            "Persisted acquired clash reaches exact bootstrap before Held without a new source notice.");
        store.BeginSelection(selected!);
        if (replaceClash)
            store.Commit(PairId, StateChange.Empty with { Raise = [(IssueKind.NameClash, TextKey.ClashNameTaken, Renamed,
                new IssueSubject([new IssueItem(Side.Dropbox, Renamed, "new-subject")], Side.Dropbox))] });
        store.Commit(PairId, StateChange.Empty with { ForgetSelf = [(Side.Local, destination.LocalKey)] });
        var after = incoming with { LocalPath = Renamed, LocalKey = PathNames.Key(Renamed),
            DropboxPath = Renamed, DropboxKey = PathNames.DropboxKey(Renamed) };
        store.Commit(PairId, StateChange.Empty with { Renames = [new RenameApplied(incoming, after, null, null, null)] }, selected, null);
        Check.Require((store.IssueById(PairId, clash.Id) is not null) == replaceClash &&
            store.ReadRenameContinuation(selected!) is null,
            "Incoming rename settlement closes only the captured unchanged clash; a replacement survives.");
    }

    private static void RenameResponsibilitySurvives(StateStore store)
    {
        var provenance = Install(store);
        var incoming = new SyncedEntry(PathNames.Key(File), File, PathNames.DropboxKey(File), File,
            false, "local-incoming", 0, 0, Identity, "");
        var destination = new SyncedEntry(PathNames.Key(Renamed), Renamed, PathNames.DropboxKey(Renamed), Renamed,
            false, "local-destination", 0, 0, OtherIdentity, "");
        store.Commit(PairId, StateChange.Empty with { Record = [incoming, destination] });
        var id = Acquire(store, provenance, NextCursor,
            [Notice(Side.Dropbox, Renamed, Identity, kind: ChangeKind.Renamed, oldPath: File)]);
        var selection = Selection(provenance, id, id, path: Renamed);
        store.BeginSelection(selection);
        Check.Require(store.ReadRenameContinuation(selection) is null, "Ordinary work starts without rename responsibility.");
        var owners = store.ReconcileRenameContinuation(selection, incoming);
        Check.Require(owners.Count == 1 && owners[0].Binding == destination,
            "Reconciliation records the complete single destination owner.");
        store.ReconcileRenameContinuation(selection, incoming);
        var replacement = destination with { LocalId = "replacement-local" };
        store.Commit(PairId, StateChange.Empty with { Record = [replacement] });
        store.ReconcileRenameContinuation(selection, incoming);
        Check.Require(store.ReadRenameContinuation(selection)?.Destination == replacement,
            "Reconciliation replaces a stale no-issue destination rather than permanently refusing it.");
        store.Commit(PairId, StateChange.Empty with { Record = [destination] });
        store.ReconcileRenameContinuation(selection, incoming);
        Check.Require(store.ReadRenameContinuation(selection) == new RenameContinuation(destination, null),
            "Repeated exact registration preserves one historical destination responsibility.");
        // Unresolved rename dispatch does not call CompleteWork; only ready branches may retire the no-issue payload.
        store.Commit(PairId, StateChange.Empty with { ForgetSelf = [(Side.Local, destination.LocalKey)] });
        Check.Require(store.ReconcileRenameContinuation(selection, incoming).Count == 0 &&
                store.ReadRenameContinuation(selection) is null,
            "Reconciliation clears no-issue permission once the complete blocker set is empty.");
        var after = incoming with { LocalPath = Renamed, LocalKey = PathNames.Key(Renamed),
            DropboxPath = Renamed, DropboxKey = PathNames.DropboxKey(Renamed) };
        store.Commit(PairId, StateChange.Empty with { Renames = [new RenameApplied(incoming, after, null, null, null)] }, selection, null);
        Check.Require(store.ReadRenameContinuation(selection) is null &&
            store.SyncedByKey(PairId, Side.Local, after.LocalKey) == after,
            "Committed incoming rename clears only its completed continuation in the same transaction.");
        Check.Require(store.CompleteWork(selection, null), "Settled incoming work can retire normally.");
        store.EndWorkPass(PairId);
        store.Commit(PairId, StateChange.Empty with { ForgetSelf = [(Side.Local, after.LocalKey)], Record = [incoming, destination] });
        id = Acquire(store, provenance, NextCursor,
            [Notice(Side.Dropbox, Renamed, Identity, kind: ChangeKind.Renamed, oldPath: File)]);
        selection = Selection(provenance, id, id, path: Renamed);
        store.BeginSelection(selection);
        store.RegisterRenameContinuation(selection, incoming, destination, null);
        Check.Require(store.CompleteWork(selection, null),
            "Ready selection clears an unlinked no-issue continuation and retires its acquired work.");
        store.EndWorkPass(PairId);
        Check.Require(store.SelectWork(PairId, provenance.PairGeneration,
                new PathScope(store.Pair(PairId)!, store.Pairs())) is null,
            "No continuation-only work remains after ready completion.");
    }

    private static void LivePeerClaimSurvives(StateStore store)
    {
        var provenance = Install(store);
        var remoteId = Acquire(store, provenance, NextCursor,
            [Notice(Side.Dropbox, File, Identity), Notice(Side.Dropbox, OtherFile, OtherIdentity)]);
        var source = store.ReadSource(PairId, Side.Local)!.Value;
        var localId = store.AcquireJournalPage(Volume, null, new JournalPosition(1, 1),
            [(PairId, provenance.PairGeneration, source.Epoch, source.Revision, false)],
            [(PairId, Notice(Side.Local, File, "local-identity"))])!.Value;
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            var current = store.ReadSource(PairId, side)!.Value;
            store.AdvanceSource(PairId, side, provenance.PairGeneration, current.Epoch, current.Revision,
                current.Provenance, current.Cursor, current.Epoch, current.Revision, current.Provenance,
                current.Cursor, null);
        }
        var scope = new PathScope(Pair(PairId), [Pair(PairId)]);
        var selections = new List<WorkSelection>();
        while (store.SelectWork(PairId, provenance.PairGeneration, scope) is { } selection)
        {
            Check.Require(store.BeginSelection(selection) is not null, "Selection setup must be admitted.");
            selections.Add(selection);
        }
        Check.Require(selections.Count == 3, "M: three independent live claims are installed");
        var local = selections.Single(selection => selection.Side == Side.Local);
        var remote = selections.Single(selection => selection.Side == Side.Dropbox && selection.Identity == Identity);
        Check.Require(store.CompleteWork(local, new SelectionPeer(remote.Key, localId)),
            "M: the completing claim retires itself");
        Check.Require(store.ReadWork(remote).Any(), "M: peer retirement never erases another live claim");
        Check.Require(store.CompleteWork(remote, null), "M: only the peer's own completion retires its claim");
        var unrelated = selections.Single(selection => selection.Identity == OtherIdentity);
        Check.Require(store.ReadWork(unrelated).Any() && store.CompleteWork(unrelated, null),
            "M: unrelated live membership remains independently completable");
        store.EndWorkPass(PairId);
    }

    private static IEnumerable<T> FailsAfter<T>(T first, Exception failure)
    {
        yield return first;
        throw failure;
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(ExpectedFailure);
    }

    private static void PageRollback(StateStore store)
    {
        var provenance = Install(store);
        var source = store.ReadSource(PairId, Side.Dropbox);
        var watermark = store.ReadWorkWatermark();
        var notice = Notice(Side.Dropbox, File, Identity);
        foreach (var failure in new Exception[] { new IOException(), new OperationCanceledException() })
        {
            try
            {
                Acquire(store, provenance, NextCursor, FailsAfter(notice, failure));
                throw new InvalidOperationException(ExpectedFailure);
            }
            catch (Exception actual) when (ReferenceEquals(actual, failure)) { }
            Check.Require(store.ReadSource(PairId, Side.Dropbox) == source &&
                store.ReadWorkWatermark() == watermark &&
                !PendingWork.ByIdentity(store, Side.Dropbox, Domain, Identity).Any(), AtomicPage);
        }
        Throws<ArgumentException>(() => Acquire(store, provenance, NextCursor,
            [notice, Notice(Side.Local, OtherFile, OtherIdentity)]));
        Check.Require(store.ReadSource(PairId, Side.Dropbox) == source &&
            store.ReadWorkWatermark() == watermark, AtomicPage);
        var before = source!.Value;
        foreach (var mismatch in new (long Generation, long Epoch, long Revision,
            SourceProvenance Provenance, string? Cursor)[]
        {
            (provenance.PairGeneration + 1, before.Epoch, before.Revision, provenance, before.Cursor),
            (provenance.PairGeneration, before.Epoch + 1, before.Revision, provenance, before.Cursor),
            (provenance.PairGeneration, before.Epoch, before.Revision + 1, provenance, before.Cursor),
            (provenance.PairGeneration, before.Epoch, before.Revision, provenance with { AccountId = OtherIdentity }, before.Cursor),
            (provenance.PairGeneration, before.Epoch, before.Revision, provenance, (string?)LastCursor),
        })
        {
            Throws<LiveAdmissionStaleException>(() => store.AcquireDropboxPage(PairId,
                mismatch.Generation, mismatch.Epoch, mismatch.Revision, mismatch.Provenance,
                mismatch.Cursor, [notice], NextCursor));
            Check.Require(store.ReadSource(PairId, Side.Dropbox) == source &&
                store.ReadWorkWatermark() == watermark, AtomicPage);
        }
        Acquire(store, provenance, NextCursor, []);
        Check.Require(store.ReadSource(PairId, Side.Dropbox)!.Value.Cursor == NextCursor &&
            store.ReadWorkWatermark() == watermark, AtomicPage);
    }

    private static void OrderedAndFinite(StateStore store)
    {
        var provenance = Install(store);
        var start = store.ReadWorkWatermark();
        ChangeNotice[] page =
        [
            Notice(Side.Dropbox, File, Identity),
            Notice(Side.Dropbox, File, null, kind: ChangeKind.Deleted),
            Notice(Side.Dropbox, File, OtherIdentity),
            Notice(Side.Dropbox, Renamed, Identity, kind: ChangeKind.Renamed, oldPath: File),
        ];
        var through = Acquire(store, provenance, NextCursor, page);
        var selected = Selection(provenance, start + 1, through);
        var ordered = store.ReadWork(selected).ToArray();
        Check.Require(ordered.Select(work => work.Notice).SequenceEqual(new[] { page[0], page[^1] }) &&
            ordered.All(work => work.PairId == PairId && work.PairGeneration == provenance.PairGeneration &&
                work.SourceEpoch == provenance.SourceEpoch), OrderedPage);
        using (var members = store.ReadWork(selected).GetEnumerator())
        {
            Check.Require(members.MoveNext() && members.Current.Notice == page[0], OrderedPage);
            var before = new SyncedEntry(PathNames.Key(File), File, PathNames.DropboxKey(File), File,
                false, Identity, 0, 0, Identity, string.Empty);
            store.Commit(PairId, StateChange.Empty with { Record = [before] }, selected, null);
            Check.Require(members.MoveNext() && members.Current.Notice == page[^1] &&
                members.Current.Before == before && !members.MoveNext(), FiniteMembership);
        }
        var tombstone = Selection(provenance, start + 1 + 1, through, identity: null);
        Check.Require(store.ReadWork(tombstone).Single().Notice == page[1], OrderedPage);
        var later = Acquire(store, provenance, LastCursor, [page[0]]);
        store.CompleteWork(selected, null);
        Check.Require(store.ReadWork(Selection(provenance, later, later)).Single().Notice == page[0] &&
            store.ReadWork(tombstone).Single().Notice == page[1] &&
            PendingWork.ByIdentity(store, Side.Dropbox, Domain, OtherIdentity).Single().AnchorWorkId <= through,
            FiniteMembership);
        var saved = new SyncedEntry(PathNames.Key(File), File, PathNames.DropboxKey(File), File,
            false, Identity, 0, 0, Identity, string.Empty);
        var remaining = Selection(provenance, later, later);
        Check.Require(store.BeginSelection(remaining) is not null, "Selection setup must be admitted.");
        Check.Require(store.BeginSelection(remaining) is not null, "Selection setup must be admitted.");
        Check.Require(store.ReadWork(remaining).Single().Before == saved, FiniteMembership);
    }

    private static void JournalRollbackAndGap(StateStore store)
    {
        store.SavePair(Pair(OtherPair));
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var other = store.ReadSource(OtherPair, Side.Local)!.Value;
        var generation = store.ReadPairExecution(PairId)!.Value.Generation;
        var otherGeneration = store.ReadPairExecution(OtherPair)!.Value.Generation;
        var next = new JournalPosition(0, 0);
        var members = new[]
        {
            (PairId, generation, local.Epoch, local.Revision, false),
            (OtherPair, otherGeneration, other.Epoch, other.Revision, false),
        };
        var before = store.ReadWorkWatermark();
        Throws<IOException>(() => store.AcquireJournalPage(Volume, null, next, members,
            FailsAfter((PairId, Notice(Side.Local, File, Identity)), new IOException())));
        Check.Require(store.ReadJournal(Volume) is null && store.ReadWorkWatermark() == before, AtomicPage);
        Check.Require(store.AcquireJournalPage(Volume, next, next, members, []) is null, AtomicPage);
        Check.Require(store.AcquireJournalPage(Volume, null, next, members, []) == before, AtomicPage);
        var evidence = store.ReadLocalDirty(PairId, generation, local.Epoch, Volume, Identity,
            PathNames.Key(File), null);
        Check.Require(!evidence.Dirty && evidence.EvidenceVersion == 0 && evidence.SourceBoundary == next,
            DirtyEvidence);
        Check.Require(store.AcquireJournalPage(Volume, next, next, members,
            [(PairId, Notice(Side.Local, OtherFile, OtherIdentity))]) == before + 1, GapResponsibility);
        var pair = store.Pair(PairId)!;
        // A configuration change hiding retained Local work creates skipped coverage.
        store.SavePairDecision(pair, generation, pair with { Name = "renamed" }, null, false, null);
        generation = store.ReadPairExecution(PairId)!.Value.Generation;
        var watermark = store.ReadWorkWatermark();
        var gap = store.ReadSource(PairId, Side.Local)!.Value;
        Check.Require(gap.Obligation == SourceObligation.LocalSkippedCoverage && gap.Epoch == local.Epoch + 1 &&
            gap.Revision == local.Revision + 1 && store.ReadLocalGap(PairId, gap.Epoch) == gap.Revision,
            GapResponsibility);
        store.AcquireJournalPage(Volume, next, next,
            [(PairId, generation, gap.Epoch, gap.Revision, false)], []);
        Check.Require(store.ReadSource(PairId, Side.Local) == gap, GapResponsibility);
        store.AcquireJournalPage(Volume, next, next,
            [(PairId, generation, gap.Epoch, gap.Revision, true)], []);
        var requested = store.ReadSource(PairId, Side.Local)!.Value;
        Check.Require(requested.Obligation == SourceObligation.LocalSkippedCoverage &&
            requested.Revision == gap.Revision + 1 && store.ReadWorkWatermark() == watermark,
            GapResponsibility);
    }

    // F04: one source owner answers a lost interval from first_repair_done, and an existing obligation is preserved.
    private static void LostIntervalCases(StateStore store, string database)
    {
        var generation = store.ReadPairExecution(PairId)!.Value.Generation;
        var volume = Domain;
        var position = new JournalPosition(0, 0);
        ClearLocalObligation(store, generation);
        Acquire(store, generation, volume, null, lostInterval: true);
        Check.Require(store.ReadSource(PairId, Side.Local)!.Value.Obligation == SourceObligation.Bootstrap,
            LostInterval);
        MarkFirstRepairDone(database);
        ClearLocalObligation(store, generation);
        Acquire(store, generation, volume, position, lostInterval: true);
        Check.Require(store.ReadSource(PairId, Side.Local)!.Value.Obligation == SourceObligation.NormalRepair,
            LostInterval);
        ClearLocalObligation(store, generation);
        var beforeLoss = store.ReadSource(PairId, Side.Local)!.Value;
        store.RequireRepairAfterLoss(PairId, generation);
        var afterLoss = store.ReadSource(PairId, Side.Local)!.Value;
        Check.Require(afterLoss.Obligation == SourceObligation.NormalRepair &&
            afterLoss.Revision == beforeLoss.Revision + 1 && afterLoss.Epoch == beforeLoss.Epoch &&
            afterLoss.Cursor == beforeLoss.Cursor && afterLoss.Provenance == beforeLoss.Provenance, LostInterval);
        store.RequireRepairAfterLoss(PairId, generation);
        Check.Require(store.ReadSource(PairId, Side.Local)!.Value.Revision == afterLoss.Revision + 1, LostInterval);
        try
        {
            store.RequireRepairAfterLoss(PairId, generation + 1);
            Check.Require(false, LostInterval);
        }
        catch (LiveAdmissionStaleException) { }
        Check.Require(store.ReadSource(PairId, Side.Local)!.Value.Revision == afterLoss.Revision + 1, LostInterval);
        var configuration = store.ReadSource(PairId, Side.Local)!.Value;
        store.AdvanceSource(PairId, Side.Local, generation, configuration.Epoch, configuration.Revision,
            configuration.Provenance, configuration.Cursor, configuration.Epoch, configuration.Revision + 1,
            configuration.Provenance, configuration.Cursor, SourceObligation.Configuration);
        Acquire(store, generation, volume, position, lostInterval: true);
        Check.Require(store.ReadSource(PairId, Side.Local)!.Value.Obligation == SourceObligation.NormalRepair,
            LostInterval + "-replaces-configuration");
        var bootstrap = store.ReadSource(PairId, Side.Local)!.Value;
        store.AdvanceSource(PairId, Side.Local, generation, bootstrap.Epoch, bootstrap.Revision,
            bootstrap.Provenance, bootstrap.Cursor, bootstrap.Epoch, bootstrap.Revision + 1,
            bootstrap.Provenance, bootstrap.Cursor, SourceObligation.Bootstrap);
        Acquire(store, generation, volume, position, lostInterval: true);
        Check.Require(store.ReadSource(PairId, Side.Local)!.Value.Obligation == SourceObligation.Bootstrap,
            LostInterval + "-preserved");
    }

    private static void ClearLocalObligation(StateStore store, long generation)
    {
        var source = store.ReadSource(PairId, Side.Local)!.Value;
        store.AdvanceSource(PairId, Side.Local, generation, source.Epoch, source.Revision, source.Provenance,
            source.Cursor, source.Epoch, source.Revision + 1, source.Provenance, source.Cursor, null);
    }

    private static void Acquire(StateStore store, long generation, string volume, JournalPosition? expected,
        bool lostInterval)
    {
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        store.AcquireJournalPage(volume, expected, new JournalPosition(0, 0),
            [(PairId, generation, local.Epoch, local.Revision, lostInterval)], []);
    }

    // B-140B: owed Repair never refuses live selection, including before the pair's first completed Repair; Local
    // skipped coverage still selects, because only Local effects without their own authority are refused. A failed
    // required Repair keeps its obligation and retry episode while both sources keep committing selectable changes.
    public static void RepairHeldSelection()
    {
        WithStore((store, database) =>
        {
            var provenance = Install(store);
            Acquire(store, provenance, NextCursor, [Notice(Side.Dropbox, File, Identity)]);
            var scope = new PathScope(store.Pair(PairId)!, store.Pairs());
            Check.Require(store.Obligated(PairId) && store.ReadLiveRefusal(PairId) != LiveRefusal.Pair &&
                store.SelectWork(PairId, provenance.PairGeneration, scope) is not null,
                "B-140B a pair before its first Repair selects live work");
            store.EndWorkPass(PairId);
            MarkFirstRepairDone(database);
            Check.Require(store.Obligated(PairId) && store.ReadLiveRefusal(PairId) == LiveRefusal.None &&
                store.SelectWork(PairId, provenance.PairGeneration, scope) is not null,
                "R6 owed Repair after the first completion selects live work");
            store.EndWorkPass(PairId);
            var local = store.ReadSource(PairId, Side.Local)!.Value;
            store.AdvanceSource(PairId, Side.Local, provenance.PairGeneration, local.Epoch, local.Revision,
                local.Provenance, local.Cursor, local.Epoch, local.Revision, local.Provenance, local.Cursor,
                SourceObligation.LocalSkippedCoverage);
            Check.Require(store.ReadLiveRefusal(PairId) == LiveRefusal.LocalWrites &&
                store.SelectWork(PairId, provenance.PairGeneration, scope) is not null,
                "R6 Local skipped coverage still selects; only Local-writing effects are refused");
            store.EndWorkPass(PairId);
            FailedRepairKeepsIntake(store, provenance, scope);
            // R6-F5: the refusal reads the pair and each source once; the gap owner reuses the Local row.
            var connection = (Microsoft.Data.Sqlite.SqliteConnection)typeof(StateStore).GetField("_connection",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
            int Count(Action action)
            {
                var statements = 0;
                SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)((_, _) => statements++), null);
                try { action(); }
                finally { SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null); }
                return statements;
            }
            var refusal = Count(() => store.ReadLiveRefusal(PairId));
            var gap = Count(() => store.ReadLocalGap(PairId, local.Epoch));
            Console.WriteLine($"R6-F5 statements refusal={refusal} gap={gap}");
            Check.Require(refusal == 3 && gap == 2,
                "R6-F5 ReadLiveRefusal costs three statements and ReadLocalGap two: refusal=" + refusal + "; gap=" + gap);
        });
        WithStore((store, database) => ReturningRelease(store, keepRepairObligated: true,
            () => MarkFirstRepairDone(database)));
        using var memory = new InMemoryStateStore();
        ReturningRelease(memory, keepRepairObligated: true, () => memory.MarkFirstRepairDone(PairId));
        WithStore((store, database) => LossOverConfiguration(store, () => MarkFirstRepairDone(database)));
        using var fake = new InMemoryStateStore();
        fake.SavePair(Pair(PairId));
        LossOverConfiguration(fake, () => fake.MarkFirstRepairDone(PairId));
    }

    // RQ-179: a failed required Repair records its retry episode and protected notice and keeps its obligation; both
    // sources still commit available changes, live selection offers them, a pair sharing the Local volume progresses,
    // and nothing clears the debt, episode or notice.
    private static void FailedRepairKeepsIntake(StateStore store, SourceProvenance provenance, PathScope scope)
    {
        var generation = provenance.PairGeneration;
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var remote = store.ReadSource(PairId, Side.Dropbox)!.Value;
        var episode = new Massdex.SyncShark.V2.FailureRetryOwner.State(1, DateTimeOffset.UnixEpoch.AddSeconds(1));
        store.RecordPrerequisiteFailure(PairId, generation, local.Epoch, remote.Epoch, local.Revision, remote.Revision,
            null, episode, StateChange.Empty with
            {
                Raise = [(IssueKind.Failure, TextKey.RequiredRepairRetry, string.Empty, new IssueSubject([], null))]
            });
        store.SavePair(Pair(OtherPair));
        var other = store.ReadSource(OtherPair, Side.Local)!.Value;
        var otherGeneration = store.ReadPairExecution(OtherPair)!.Value.Generation;
        var before = store.ReadWorkWatermark();
        Acquire(store, provenance, LastCursor, [Notice(Side.Dropbox, OtherFile, OtherIdentity)]);
        local = store.ReadSource(PairId, Side.Local)!.Value;
        var volume = Pair(PairId).Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var journal = store.AcquireJournalPage(volume, null, new JournalPosition(0, 1),
            [(PairId, generation, local.Epoch, local.Revision, false),
                (OtherPair, otherGeneration, other.Epoch, other.Revision, false)],
            [(PairId, Notice(Side.Local, Renamed, "local-identity")),
                (OtherPair, Notice(Side.Local, OtherFile, "other-pair-identity"))]);
        var otherScope = new PathScope(store.Pair(OtherPair)!, store.Pairs());
        Check.Require(journal is not null && store.ReadWorkWatermark() > before &&
            PendingWork.ByIdentity(store, Side.Dropbox, Domain, OtherIdentity).Count == 1 &&
            PendingWork.ByIdentity(store, Side.Local, volume, "local-identity").Count == 1 &&
            store.SelectWork(PairId, generation, scope) is not null &&
            store.ReadPrerequisiteRetry(PairId) == episode && store.Obligated(PairId) &&
            store.ReadSource(PairId, Side.Dropbox)!.Value.Cursor == LastCursor &&
            store.Issues(PairId).Count(issue => issue.Cause == TextKey.RequiredRepairRetry) == 1,
            "B-140B a failed required Repair keeps intake from both sources, its episode, notice and obligation");
        store.EndWorkPass(PairId);
        Check.Require(PendingWork.ByIdentity(store, Side.Local, volume, "other-pair-identity").Count == 1 &&
            store.SelectWork(OtherPair, otherGeneration, otherScope) is not null,
            "B-140B a pair sharing the Local volume keeps its intake and selection while another pair's Repair failed");
        store.EndWorkPass(OtherPair);
    }

    // Live coverage refuses a stale other-side catch-up epoch while the selection's own fences are current, and
    // settlement refuses a selection whose source epoch moved after it was selected; neither retires the work.
    private static void StaleFenceCases(StateStore store, string database)
    {
        var provenance = Install(store);
        var through = Acquire(store, provenance, NextCursor, [Notice(Side.Dropbox, File, Identity)]);
        var generation = provenance.PairGeneration;
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var volume = Pair(PairId).Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        store.AcquireJournalPage(volume, null, new JournalPosition(0, 0),
            [(PairId, generation, local.Epoch, local.Revision, false)], []);
        var scope = new PathScope(store.Pair(PairId)!, store.Pairs());
        var selection = store.SelectWork(PairId, generation, scope) ?? throw new InvalidOperationException(FiniteMembership);
        Check.Require(store.BeginSelection(selection) is not null, "Selection setup must be admitted.");
        LiveAdmissionStaleReason? reason = null;
        try
        {
            store.CaptureLiveCoverage(selection,
                new LocalCatchUp(PairId, generation, local.Epoch + 1, new NtfsUsnCheckpoint(0, 0, 0, 0), through),
                new DropboxCatchUp(PairId, generation, provenance.SourceEpoch, NextCursor, through));
        }
        catch (LiveAdmissionStaleException error) { reason = error.Reason; }
        Check.Require(reason == LiveAdmissionStaleReason.CoverageChanged && store.ReadWork(selection).Any(),
            "a stale other-side catch-up epoch refuses live coverage and keeps the work: reason=" + reason);
        var persisted = GenerationCount(database, generation);
        var source = store.ReadSource(PairId, Side.Dropbox)!.Value;
        var next = provenance with { SourceEpoch = source.Epoch + 1 };
        store.AdvanceSource(PairId, Side.Dropbox, generation, source.Epoch, source.Revision, source.Provenance,
            source.Cursor, next.SourceEpoch, source.Revision + 1, next, source.Cursor, source.Obligation);
        Throws<LiveAdmissionStaleException>(() => store.CompleteWork(selection, null));
        // The old-epoch row is no longer dispatchable, so count the persisted rows rather than eligible work.
        Check.Require(persisted > 0 && GenerationCount(database, generation) == persisted,
            "a selection settled after its source epoch moved is refused and retires nothing: persisted=" + persisted);
    }

    // R6-F1: a continuity loss over Configuration debt still refuses live Local writes, at both loss entry points.
    private static void LossOverConfiguration(IStateStore store, Action firstRepairDone)
    {
        firstRepairDone();
        var generation = store.ReadPairExecution(PairId)!.Value.Generation;
        foreach (var page in new[] { false, true })
        {
            var owed = store.ReadSource(PairId, Side.Local)!.Value;
            store.AdvanceSource(PairId, Side.Local, generation, owed.Epoch, owed.Revision, owed.Provenance,
                owed.Cursor, owed.Epoch, owed.Revision + 1, owed.Provenance, owed.Cursor, SourceObligation.Configuration);
            Check.Require(store.ReadLiveRefusal(PairId) == LiveRefusal.None,
                "R6-F1 Configuration debt alone after the first Repair refuses nothing");
            var local = store.ReadSource(PairId, Side.Local)!.Value;
            if (page)
                store.AcquireJournalPage(Domain, null, new JournalPosition(0, 0),
                    [(PairId, generation, local.Epoch, local.Revision, true)], []);
            else store.RequireRepairAfterLoss(PairId, generation);
            Check.Require(store.ReadSource(PairId, Side.Local)!.Value.Obligation == SourceObligation.NormalRepair &&
                store.ReadLiveRefusal(PairId) == LiveRefusal.LocalWrites,
                "R6-F1 a Local continuity loss over Configuration debt refuses live Local writes " +
                (page ? "(journal page)" : "(after loss)"));
        }
        // R6-1: the reverse order, through the real configuration transition, keeps the loss's responsibility.
        var pair = store.Pair(PairId)!;
        store.SavePairDecision(pair, generation, pair, null, false, null);
        var after = store.ReadSource(PairId, Side.Local)!.Value.Obligation;
        Check.Require(after == SourceObligation.NormalRepair && store.ReadLiveRefusal(PairId) == LiveRefusal.LocalWrites,
            "R6-1 a configuration change after a Local continuity loss keeps the Local-write refusal: obligation=" +
            after + "; refusal=" + store.ReadLiveRefusal(PairId));
    }

    private static void MarkFirstRepairDone(string database)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { DataSource = database, Pooling = false }.ToString());
        connection.Open();
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE pair SET first_repair_done=1;";
        update.ExecuteNonQuery();
    }

    private static void DirtyMemoryTransitions(StateStore store, string database)
    {
        var generation = store.ReadPairExecution(PairId)!.Value.Generation;
        var source = store.ReadSource(PairId, Side.Local)!.Value;
        using var observer = StateStore.OpenReadOnly(database)!;
        (bool Dirty, JournalPosition? SourceBoundary, long EvidenceVersion) Read(IStateStore reader,
            string domain = Volume, long? expected = null, long? epoch = null) => reader.ReadLocalDirty(PairId,
                expected ?? generation, epoch ?? source.Epoch, domain, Identity, PathNames.Key(File), null);
        void Compare(string label, string domain = Volume) =>
            Check.Require(Read(store, domain) == Read(observer, domain), label);
        Compare("empty memory matches read-only SQL");
        var connection = (Microsoft.Data.Sqlite.SqliteConnection)typeof(StateStore).GetField("_connection",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
        int Count(Action action)
        {
            var statements = 0;
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)((_, _) => statements++), null);
            try { action(); }
            finally { SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null); }
            return statements;
        }
        Check.Require(Count(() => Read(store)) == 0, "empty cached pair needs no SQL");
        Check.Require(Read(store, expected: generation + 1) == (false, null, 0L) &&
            Read(store, epoch: source.Epoch + 1) == (false, null, 0L), "cached entry never accepts stale arguments");
        var position = new JournalPosition(1, 1);
        store.AcquireJournalPage(Volume, null, position,
            [(PairId, generation, source.Epoch, source.Revision, false)], []);
        Check.Require(Count(() => Read(store)) == 1, "position insert reads only the position");
        Compare("position insert");
        var next = new JournalPosition(1, 2);
        store.AcquireJournalPage(Volume, position, next,
            [(PairId, generation, source.Epoch, source.Revision, false)], []);
        Check.Require(Count(() => Read(store)) == 1, "position update does not re-probe evidence");
        position = next;
        Compare("position advance");
        IEnumerable<(string PairId, ChangeNotice Notice)> FailingPage()
        {
            yield return (PairId, Notice(Side.Local, File, Identity, dirty: true));
            throw new InvalidOperationException(ExpectedFailure);
        }
        try
        {
            store.AcquireJournalPage(Volume, position, next,
                [(PairId, generation, source.Epoch, source.Revision, false)], FailingPage());
            throw new Exception("the page must fail");
        }
        catch (InvalidOperationException error) when (error.Message == ExpectedFailure) { }
        Check.Require(Count(() => Read(store)) == 3, "rollback invalidates evidence but preserves the position");
        Compare("rollback clean answer");
        Check.Require(!Read(observer).Dirty, "read-only reader starts clean");
        var work = store.AcquireJournalPage(Volume, position, next,
            [(PairId, generation, source.Epoch, source.Revision, false)],
            [(PairId, Notice(Side.Local, File, Identity, dirty: true))])!.Value;
        Check.Require(Read(store).Dirty && Read(observer).Dirty, "next scan row and immediate reader see committed arrival");
        Compare("arrival during scan");
        Compare("other domain stays clean with pair-wide evidence", Domain);
        Check.Require(!Read(store, Domain).Dirty, "other domain does not inherit dirty");
        Check.Require(Count(() => Read(store, Domain)) == 8, "lingering Acquired work retains the full probes");
        store.CompleteWork(new WorkSelection(PairId, generation, source.Epoch, Side.Local, Volume,
            Identity, PathNames.Key(File), work, work), null);
        Compare("last Acquired row removed");
        Check.Require(Count(() => Read(store)) == 0, "last deletion restores the free path");
        store.AdvanceSource(PairId, Side.Local, generation, source.Epoch, source.Revision,
            source.Provenance, source.Cursor, source.Epoch + 1, source.Revision + 1,
            source.Provenance, source.Cursor, source.Obligation);
        Check.Require(Read(store) == (false, null, 0L), "epoch invalidates cached old arguments");
        source = store.ReadSource(PairId, Side.Local)!.Value;
        Compare("new epoch");
        var pair = store.Pair(PairId)!;
        store.SavePairDecision(pair, generation, pair, null, false, null);
        Check.Require(Read(store) == (false, null, 0L), "generation invalidates cached old arguments");
        generation = store.ReadPairExecution(PairId)!.Value.Generation;
        source = store.ReadSource(PairId, Side.Local)!.Value;
        Compare("new generation");
        store.RemovePair(PairId);
        Check.Require(Read(store) == (false, null, 0L), "pair removal invalidates cached entry");
        Compare("removed pair");
    }

    private static void DirtyOutsideSelection(StateStore store)
    {
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var generation = store.ReadPairExecution(PairId)!.Value.Generation;
        var position = new JournalPosition(0, 0);
        var first = store.AcquireJournalPage(Volume, null, position,
            [(PairId, generation, local.Epoch, local.Revision, false)],
            [(PairId, Notice(Side.Local, File, Identity))])!.Value;
        var selected = new WorkSelection(PairId, generation, local.Epoch, Side.Local, Volume,
            Identity, PathNames.Key(File), first, first);
        var dirty = store.AcquireJournalPage(Volume, position, position,
            [(PairId, generation, local.Epoch, local.Revision, false)],
            [(PairId, Notice(Side.Local, File, Identity, dirty: true))])!.Value;
        var evidence = store.ReadLocalDirty(PairId, generation, local.Epoch, Volume, Identity,
            PathNames.Key(File), null);
        Check.Require(evidence.Dirty && evidence.EvidenceVersion == dirty, DirtyEvidence);
        store.CompleteWork(selected, null);
        Check.Require(store.ReadLocalDirty(PairId, generation, local.Epoch, Volume, Identity,
            PathNames.Key(File), null).Dirty, DirtyEvidence);
        Check.Require(!store.ReadLocalDirty(PairId, generation, local.Epoch, Volume, OtherIdentity,
            PathNames.Key(File), null).Dirty, DirtyEvidence);
        Check.Require(store.ReadLocalDirty(PairId, generation + 1, local.Epoch, Volume, Identity,
            PathNames.Key(File), null).SourceBoundary is null, DirtyEvidence);
    }

    private enum PublishedEcho
    {
        Own, LaterWrite, Unproved, ReRecorded, RecreatedJournal, JournalBehind, OtherIdentity, NoRecord, RenamedIdentity
    }

    // #90b: a content write of the published identity is the engine's own echo only at or before the boundary saved
    // with that identity, in the journal it was proved in, once that journal is read past it. Everything else is dirty.
    private static void OwnPublicationEcho(StateStore store, PublishedEcho echo)
    {
        const ulong Journal = 7;
        const ulong Boundary = 500;
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var generation = store.ReadPairExecution(PairId)!.Value.Generation;
        var members = new[] { (PairId, generation, local.Epoch, local.Revision, false) };
        var saved = new SyncedEntry(PathNames.Key(File), File, PathNames.DropboxKey(File), File,
            false, Identity, 1, 0, "remote", "hash");
        store.Commit(PairId, StateChange.Empty with
        {
            Record = [saved],
            LocalPublished = echo == PublishedEcho.Unproved ? null : (Identity, new JournalPosition(Journal, Boundary))
        });
        if (echo == PublishedEcho.ReRecorded) store.Commit(PairId, StateChange.Empty with { Record = [saved] });
        if (echo == PublishedEcho.RenamedIdentity)
            store.Commit(PairId, StateChange.Empty with
            {
                Renames = [new RenameApplied(saved, saved with { LocalId = OtherIdentity }, null, null, null)]
            });
        var identity = echo is PublishedEcho.OtherIdentity or PublishedEcho.RenamedIdentity ? OtherIdentity : Identity;
        var usn = echo == PublishedEcho.LaterWrite ? Boundary + 1 : Boundary;
        var next = echo switch
        {
            PublishedEcho.RecreatedJournal => new JournalPosition(Journal + 1, Boundary + 1),
            PublishedEcho.JournalBehind => new JournalPosition(Journal, Boundary),
            _ => new JournalPosition(Journal, Boundary + 2)
        };
        var notice = Notice(Side.Local, File, identity, dirty: true) with
        {
            Usn = echo == PublishedEcho.NoRecord ? null : usn
        };
        store.AcquireJournalPage(Volume, null, next, members, [(PairId, notice)]);
        var dirty = store.ReadLocalDirty(PairId, generation, local.Epoch, Volume, identity, PathNames.Key(File),
            saved.LocalKey).Dirty;
        Check.Require(dirty == (echo != PublishedEcho.Own), "90b own-publication echo " + echo);
    }

    // A52: retained Local dirty evidence follows a configuration change to the next generation and epoch as one
    // ordinary row, and the old row is left untouched.
    private static void DirtyTransfersOnConfiguration(StateStore store, string database)
    {
        var (generation, volume, work) = SeedDirty(store);
        var pair = store.Pair(PairId)!;
        store.SavePairDecision(pair, generation, pair, null, false, null);
        var advanced = store.ReadSource(PairId, Side.Local)!.Value;
        var evidence = store.ReadLocalDirty(PairId, generation + 1, advanced.Epoch, volume, Identity,
            PathNames.Key(File), null);
        var carried = new WorkSelection(PairId, generation + 1, advanced.Epoch, Side.Local, volume, Identity,
            PathNames.Key(File), work + 1, work + 1);
        Check.Require(evidence.Dirty && evidence.EvidenceVersion == work &&
            store.ReadWork(carried).Single().Notice == Notice(Side.Local, File, Identity, dirty: true) &&
            GenerationCount(database, generation) == 1 && GenerationCount(database, generation + 1) == 1,
            DirtyEvidence);
    }

    // A53: a changed Local volume keeps nothing; no row reaches the new generation.
    private static void DirtyDroppedOnDomainChange(StateStore store, string database)
    {
        var (generation, volume, _) = SeedDirty(store);
        var pair = store.Pair(PairId)!;
        var moved = pair with { Roots = pair.Roots! with { Local = new DirectoryIdentity(1, 1, null) } };
        store.SavePairDecision(pair, generation, moved, null, false, null);
        var advanced = store.ReadSource(PairId, Side.Local)!.Value;
        Check.Require(!store.ReadLocalDirty(PairId, generation + 1, advanced.Epoch, "1", Identity,
                PathNames.Key(File), null).Dirty &&
            !store.ReadLocalDirty(PairId, generation + 1, advanced.Epoch, volume, Identity,
                PathNames.Key(File), null).Dirty &&
            GenerationCount(database, generation) == 1 && GenerationCount(database, generation + 1) == 0,
            DirtyEvidence);
    }

    // A54: a history reset keeps nothing; no row reaches the new generation.
    private static void DirtyDroppedOnReset(StateStore store, string database)
    {
        var (generation, volume, _) = SeedDirty(store);
        var pair = store.Pair(PairId)!;
        store.SavePairDecision(pair, generation, pair, null, true, null);
        var advanced = store.ReadSource(PairId, Side.Local)!.Value;
        Check.Require(!store.ReadLocalDirty(PairId, generation + 1, advanced.Epoch, volume, Identity,
                PathNames.Key(File), null).Dirty &&
            GenerationCount(database, generation) == 1 && GenerationCount(database, generation + 1) == 0,
            DirtyEvidence);
    }

    private static (long Generation, string Volume, long Work) SeedDirty(StateStore store)
    {
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var generation = store.ReadPairExecution(PairId)!.Value.Generation;
        var volume = store.Pair(PairId)!.Roots!.Local.VolumeSerialNumber.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        var work = store.AcquireJournalPage(volume, null, new JournalPosition(0, 0),
            [(PairId, generation, local.Epoch, local.Revision, false)],
            [(PairId, Notice(Side.Local, File, Identity, dirty: true))])!.Value;
        return (generation, volume, work);
    }

    private static long GenerationCount(string database, long generation)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { DataSource = database, Pooling = false }.ToString());
        StateStore.OpenConnection(connection);
        using var command = StorageCommand.Create(connection, null,
            "SELECT count(*) FROM change_work w JOIN pair p ON p.id=w.pair_id WHERE p.pair_id=$p " +
            "AND w.pair_generation=$generation;",
            (StateSchema.ParamPair, PairId), (StateSchema.ParamGeneration, generation));
        return (long)command.ExecuteScalar()!;
    }

    private static void ReleasedResidualSelectionRefresh(StateStore store)
    {
        var provenance = Install(store);
        var parentId = Acquire(store, provenance, NextCursor, [Notice(Side.Dropbox, File, Identity)]);
        var parent = Selection(provenance, parentId, parentId);
        var before = new SyncedEntry(PathNames.Key(File), File, PathNames.DropboxKey(File), File,
            false, Identity, 0, 0, Identity, string.Empty);
        foreach (var (path, identity) in new[] { (OtherFile, OtherIdentity), (Renamed, Identity) })
        {
            var subject = new IssueSubject([new IssueItem(Side.Dropbox, path, identity)], null);
            store.Commit(PairId, StateChange.Empty with { Raise = [(IssueKind.Tie, TextKey.TieEqualTimes, path, subject)] });
            var issue = store.Issues(PairId).Single(value => value.Path == path);
            store.AttachIssueWork(parent, issue, [(Notice(Side.Dropbox, path, identity) with { LocalContentDirty = true },
                false, before)]);
        }
        store.CompleteWork(parent, null);
        store.Commit(PairId, StateChange.Empty with { Close = [(OtherFile, new[] { IssueKind.Tie })] });
        var released = PendingWork.ByIdentity(store, Side.Dropbox, Domain, OtherIdentity).Single();
        var untouched = PendingWork.ByIdentity(store, Side.Dropbox, Domain, Identity).Single();
        Check.Require(store.BeginSelection(released) is not null, "Selection setup must be admitted.");
        var releasedRow = store.ReadWork(released).Single();
        var untouchedRow = store.ReadWork(untouched).Single();
        Check.Require(releasedRow.Before is null && releasedRow.Notice.LocalContentDirty &&
            untouchedRow.Before == before && untouchedRow.Notice.LocalContentDirty &&
            store.Issues(PairId).Single().Path == Renamed,
            "starting released work refreshes only its identity baseline and preserves dirty evidence and other holds");
    }

    private static void AncestorProbeCounts()
    {
        // Existing eightfold workload distinguishes one per-pass range from a range per descendant.
        foreach (var count in new[] { 8, 64 }) WithStore((store, database) =>
        {
            var provenance = Install(store);
            CompletePrerequisite(database);
            Acquire(store, provenance, NextCursor,
                new[] { Notice(Side.Dropbox, File, Identity, kind: ChangeKind.Deleted) }.Concat(
                    Enumerable.Range(0, count).Select(index => Notice(Side.Dropbox, File + "/" + index,
                        OtherIdentity + index, dirty: true))));
            _ = store.SelectWork(PairId, provenance.PairGeneration, new PathScope(store.Pair(PairId)!, store.Pairs()));
            var connection = (Microsoft.Data.Sqlite.SqliteConnection)typeof(StateStore).GetField("_connection",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
            var ranges = 0;
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)((_, sql) =>
            {
                var text = sql.utf8_to_string();
                if (text.Contains("FROM change_work", StringComparison.Ordinal) &&
                    (text.Contains("w.path_key>=", StringComparison.Ordinal) ||
                     text.Contains("w.old_path_key>=", StringComparison.Ordinal))) ranges++;
            }), null);
            try
            {
                for (var index = 0; index < count; index++)
                    Check.Require(store.PendingAncestors(PairId, provenance.PairGeneration, Side.Dropbox,
                        [PathNames.DropboxKey(File)]).Any(), "the deleted ancestor stays an ancestor of every child");
            }
            finally { SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null); }
            Check.Require(ranges == 0, "ancestor discovery runs no descendant range per child");
            Console.WriteLine($"ancestor-deletion children={count} subtree-probes={ranges}");
        });
    }

    private static void AncestorAttemptOnce(StateStore store, string database)
    {
        var provenance = Install(store);
        CompletePrerequisite(database);
        Acquire(store, provenance, NextCursor,
            [Notice(Side.Dropbox, File, Identity),
             Notice(Side.Dropbox, File + "/Child", OtherIdentity)]);
        var ancestor = store.SelectWork(PairId, provenance.PairGeneration, new PathScope(store.Pair(PairId)!, store.Pairs()))!;
        Check.Require(store.BeginSelection(ancestor) is not null, "Selection setup must be admitted.");
        var child = store.SelectWork(PairId, provenance.PairGeneration, new PathScope(store.Pair(PairId)!, store.Pairs()))!;
        Check.Require(child.AnchorWorkId != ancestor.AnchorWorkId, "the independent child is offered after the refused parent");
        foreach (var query in new[] { 0, 1 })
        {
            var blocked = store.PendingAncestors(PairId, provenance.PairGeneration, Side.Dropbox,
                [PathNames.DropboxKey(File)]).Single();
            Check.Require(store.BeginSelection(blocked) is null, "A repeated admission skips without hiding the pending ancestor.");
        }
        store.EndWorkPass(PairId);
        var next = store.SelectWork(PairId, provenance.PairGeneration, new PathScope(store.Pair(PairId)!, store.Pairs()))!;
        Check.Require(store.BeginSelection(next) is not null, "Selection setup must be admitted.");
        Check.Require(next.AnchorWorkId == ancestor.AnchorWorkId, "a real new opportunity reaches the refused parent again");
    }

    private static void FailurePassProgress(StateStore store, string database)
    {
        var provenance = Install(store);
        CompletePrerequisite(database);
        Acquire(store, provenance, NextCursor, [Notice(Side.Dropbox, File, Identity)]);
        var subject = new IssueSubject([new IssueItem(Side.Dropbox, File, Identity)], null);
        store.Commit(PairId, StateChange.Empty with { Raise = [(IssueKind.Failure, TextKey.FailChanged, File, subject)] });
        var issue = store.Issues(PairId).Single();
        for (var wake = 0; wake < 2; wake++)
        {
            var selected = store.SelectWork(PairId, provenance.PairGeneration, new PathScope(store.Pair(PairId)!, store.Pairs()))!;
            Check.Require(selected is not null, "LG05: persistent failure is reachable on each real wake");
            if (wake == 0)
                Acquire(store, provenance, LastCursor, [Notice(Side.Dropbox, File, Identity)]);
            store.AttachIssueWork(selected!, issue, [(Notice(Side.Dropbox, File, Identity), false, null)]);
            store.CompleteWork(selected!, null);
            Check.Require(store.EndWorkPass(PairId) == (wake != 0),
                "LG05: only consuming a later notice into the existing residual is net progress");
        }
        store.Commit(PairId, StateChange.Empty with { Close = [(File, new[] { IssueKind.Failure })] });
        var recovered = store.SelectWork(PairId, provenance.PairGeneration, new PathScope(store.Pair(PairId)!, store.Pairs()))!;
        Check.Require(recovered is not null, "LG05: recovery retains a reachable row");
        store.CompleteWork(recovered!, null);
        Check.Require(store.EndWorkPass(PairId), "LG05: actual consumption earns another pass");
    }

    private static void ResidualRegistration(StateStore store)
    {
        var provenance = Install(store);
        var parentId = Acquire(store, provenance, NextCursor, [Notice(Side.Dropbox, File, Identity)]);
        var parent = Selection(provenance, parentId, parentId);
        var subject = new IssueSubject([new IssueItem(Side.Dropbox, OtherFile, OtherIdentity)], null);
        store.Commit(PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailChanged, OtherFile, subject)]
        });
        var issue = store.Issues(PairId).Single();
        var branch = (Notice(Side.Dropbox, OtherFile, OtherIdentity), false, (SyncedEntry?)null);
        store.AttachIssueWork(parent, issue, [branch]);
        var watermark = store.ReadWorkWatermark();
        store.AttachIssueWork(parent, issue, [branch]);
        Check.Require(store.ReadWorkWatermark() == watermark && store.ReadWork(parent).Count() == 1 &&
            !PendingWork.ByIdentity(store, Side.Dropbox, Domain, OtherIdentity).Any(), ResidualOwnership);
        Throws<IOException>(() => store.AttachIssueWork(parent, issue,
            FailsAfter((Notice(Side.Dropbox, Renamed, OtherIdentity), false, (SyncedEntry?)null),
                new IOException())));
        Check.Require(store.ReadWorkWatermark() == watermark, AtomicPage);
        store.CompleteWork(parent, null);
        var residual = PendingWork.ByIdentity(store, Side.Dropbox, Domain, OtherIdentity).Single();
        Check.Require(store.ReadWork(residual).Single().Notice == branch.Item1, ResidualOwnership);
        store.AttachIssueWork(residual, issue, [branch]);
        Check.Require(store.ReadWorkWatermark() == watermark && !store.CompleteWork(residual, null),
            "an attached branch refreshes itself instead of creating a successor or retiring");
        var nextId = Acquire(store, provenance, LastCursor, [Notice(Side.Dropbox, File, Identity)]);
        var next = Selection(provenance, nextId, nextId);
        store.AttachIssueWork(next, issue, [(branch.Item1 with { LocalContentDirty = true }, false, null)]);
        Check.Require(store.ReadWorkWatermark() == nextId &&
            store.ReadWork(residual).Single().Notice.LocalContentDirty,
            "later parents refresh the same issue-side-path-identity row and preserve dirty evidence");
        store.AttachIssueWork(next, issue, [(branch.Item1 with { Usn = 5 }, false, null)]);
        Check.Require(store.ReadWork(residual).Single().Notice is { LocalContentDirty: true, Usn: null },
            "90b: a kept edit without a journal record never takes a later clean hint's record");
        store.AttachIssueWork(next, issue, [(branch.Item1 with { LocalContentDirty = true, Usn = 9 }, false, null)]);
        store.AttachIssueWork(next, issue, [(branch.Item1 with { Usn = 5 }, false, null)]);
        Check.Require(store.ReadWork(residual).Single().Notice is { LocalContentDirty: true, Usn: null },
            "90b: later known dirt and clean hints cannot replace unknown dirty provenance");
    }

    private static void ResidualContentProvenance(StateStore store, string database,
        bool priorDirty, ulong? priorUsn, bool incomingDirty, ulong? incomingUsn)
    {
        var provenance = Install(store);
        var parentId = Acquire(store, provenance, NextCursor, [Notice(Side.Dropbox, File, Identity)]);
        var parent = Selection(provenance, parentId, parentId);
        var subject = new IssueSubject([new IssueItem(Side.Dropbox, OtherFile, OtherIdentity)], null);
        store.Commit(PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailChanged, OtherFile, subject)]
        });
        var issue = store.Issues(PairId).Single();
        var prior = Notice(Side.Dropbox, OtherFile, OtherIdentity, dirty: priorDirty) with { Usn = priorUsn };
        var incoming = prior with { LocalContentDirty = incomingDirty, Usn = incomingUsn };
        store.AttachIssueWork(parent, issue, [(prior, false, null)]);
        var watermark = store.ReadWorkWatermark();
        store.AttachIssueWork(parent, issue, [(incoming, false, null)]);
        Check.Require(store.ReadWorkWatermark() == watermark, "content refresh reuses one residual");
        store.CompleteWork(parent, null);
        var residual = PendingWork.ByIdentity(store, Side.Dropbox, Domain, OtherIdentity).Single();
        ulong? expectedUsn = !priorDirty ? incomingUsn : !incomingDirty ? priorUsn :
            priorUsn is null || incomingUsn is null ? null : Math.Max(priorUsn.Value, incomingUsn.Value);
        using (var reopened = StateStore.OpenReadOnly(database)!)
        {
            var retained = reopened.ReadWork(residual).Single().Notice;
            Check.Require(retained.LocalContentDirty == (priorDirty || incomingDirty) && retained.Usn == expectedUsn,
                $"content provenance survives retirement/reopen: {priorDirty}/{priorUsn} -> {incomingDirty}/{incomingUsn}");
        }
        store.Commit(PairId, StateChange.Empty with { Close = [(OtherFile, new[] { IssueKind.Failure })] });
        Check.Require(store.CompleteWork(residual, null), "closed residual retires without another Repair");
    }

    private static void PublishedEpisodeProvenance(string database)
    {
        const ulong Journal = 7;
        var attempt = Guid.NewGuid();
        using var store = StateStore.Open(database, (id, _, _, _, _) => id == attempt);
        var provenance = Install(store);
        CompletePrerequisite(database);
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var volume = Pair(PairId).Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var generation = provenance.PairGeneration;
        var members = new[] { (PairId, generation, local.Epoch, local.Revision, false) };
        var saved = new SyncedEntry(PathNames.Key(File), File, PathNames.DropboxKey(File), File,
            false, Identity, 1, 0, "remote", "hash");
        store.Commit(PairId, StateChange.Empty with
        {
            Record = [saved], LocalPublished = (Identity, new JournalPosition(Journal, 500))
        });
        var own = Notice(Side.Local, File, Identity, dirty: true) with { Usn = 400 };
        var checkpoint = new JournalPosition(Journal, 501);
        store.AcquireJournalPage(volume, null, checkpoint, members, [(PairId, own)]);
        var initial = store.SelectWork(PairId, generation, new PathScope(store.Pair(PairId)!, store.Pairs()))!;
        Check.Require(!store.ReadLocalDirty(PairId, generation, local.Epoch, volume, Identity,
            saved.LocalKey, saved.LocalKey).Dirty, "the episode's own write is below its saved publication boundary");
        var subject = new IssueSubject([new IssueItem(Side.Local, File, Identity)], null);
        var episode = store.RecordInUseFailure(PairId, generation, local.Epoch, File, subject,
            TextKey.FailLocalInUse, initial, ItemRetryDispatch.OrdinaryItem, null, null, attempt);
        store.EndWorkPass(PairId);
        long child = 0;
        foreach (var usn in new ulong[] { 600, 700, 650 })
        {
            var notice = own with { Usn = usn };
            var nextCheckpoint = new JournalPosition(Journal, Math.Max(checkpoint.NextUsn + 1, usn + 1));
            var through = store.AcquireJournalPage(volume, checkpoint, nextCheckpoint, members, [(PairId, notice)])!.Value;
            checkpoint = nextCheckpoint;
            var selected = store.SelectWork(PairId, generation, new PathScope(store.Pair(PairId)!, store.Pairs()))!;
            Check.Require(store.BeginSelection(selected) is not null, "Selection setup must be admitted.");
            store.AttachIssueWork(selected, store.IssueById(PairId, episode.Token.IssueId)!, [(notice, false, null)]);
            if (child == 0) child = selected.AnchorWorkId;
            Check.Require(store.ReadWorkWatermark() == through && store.CompleteWork(selected, null) == (child != selected.AnchorWorkId),
                "foreign writes retain one episode child; subsequent selected triggers retire");
            store.EndWorkPass(PairId);
            using var reopened = StateStore.OpenReadOnly(database)!;
            Check.Require(reopened.ReadLocalDirty(PairId, generation, local.Epoch, volume, Identity,
                saved.LocalKey, saved.LocalKey).Dirty, "foreign dirty provenance survives selection retirement and reopen");
        }
        store.CompleteItemAttempt(episode, attempt,
            StateChange.Empty with { Close = [(File, new[] { IssueKind.Failure })] }, null, null);
        store.EndWorkPass(PairId);
        var released = store.SelectWork(PairId, generation, new PathScope(store.Pair(PairId)!, store.Pairs()))!;
        Check.Require(released.AnchorWorkId == child &&
            store.ReadWork(released).Single().Notice is { LocalContentDirty: true, Usn: 700 },
            "retry release exposes the single retained child with the newest foreign write");
        Check.Require(store.BeginSelection(released) is not null, "Selection setup must be admitted.");
        Check.Require(store.CompleteWork(released, null), "the released child completes without a Repair sweep");
    }

    private static void RegistrationLifecycle(StateStore store, bool generationChange)
    {
        var provenance = Install(store);
        var id = Acquire(store, provenance, NextCursor, [Notice(Side.Dropbox, File, Identity)]);
        var parent = Selection(provenance, id, id);
        var subject = new IssueSubject([new IssueItem(Side.Dropbox, OtherFile, OtherIdentity)], null);
        store.Commit(PairId, StateChange.Empty with { Raise =
            [(IssueKind.Failure, TextKey.FailChanged, OtherFile, subject),
             (IssueKind.Tie, TextKey.TieEqualTimes, OtherFile, subject)] });
        var branch = (Notice(Side.Dropbox, OtherFile, OtherIdentity, dirty: true), false, (SyncedEntry?)null);
        foreach (var issue in store.Issues(PairId)) store.AttachIssueWork(parent, issue, [branch]);
        var residualId = store.ReadWorkWatermark();
        Check.Require(residualId == id + 1, "two raises in one commit reuse the parent registration");
        store.Commit(PairId, StateChange.Empty with { Close = [(OtherFile, new[] { IssueKind.Failure, IssueKind.Tie })] });
        store.Commit(PairId, StateChange.Empty with { Raise = [(IssueKind.Failure, TextKey.FailChanged, OtherFile, subject)] });
        var currentIssue = store.Issues(PairId).Single();
        store.AttachIssueWork(parent, currentIssue, [branch]);
        Check.Require(store.ReadWorkWatermark() == residualId, "close then refail under a live parent reuses its row");
        SourceProvenance next;
        if (generationChange)
        {
            var pair = store.Pair(PairId)!;
            store.SavePairDecision(pair, provenance.PairGeneration, pair with { Enabled = false }, null, false, null);
            next = Install(store);
        }
        else
        {
            var source = store.ReadSource(PairId, Side.Dropbox)!.Value;
            next = provenance with { SourceEpoch = source.Epoch + 1 };
            store.AdvanceSource(PairId, Side.Dropbox, provenance.PairGeneration, source.Epoch, source.Revision,
                source.Provenance, source.Cursor, next.SourceEpoch, source.Revision + 1, next, source.Cursor, source.Obligation);
        }
        var nextId = Acquire(store, next, LastCursor, [Notice(Side.Dropbox, File, Identity)]);
        var nextParent = Selection(next, nextId, nextId);
        store.AttachIssueWork(nextParent, currentIssue,
            [(Notice(Side.Dropbox, OtherFile, OtherIdentity), false, null)]);
        Check.Require(store.ReadWorkWatermark() == nextId, "epoch migration keeps the existing registration id");
        store.CompleteWork(nextParent, null);
        var residual = Selection(next, residualId, nextId, OtherIdentity, OtherFile);
        Check.Require(store.ReadWork(residual).Single().Notice.LocalContentDirty,
            "epoch migration makes the original dirty registration reachable at current fences");
    }

    private static void SelectionAndCoverageCases(StateStore store, string database)
    {
        var provenance = Install(store);
        CompletePrerequisite(database);
        var through = Acquire(store, provenance, NextCursor,
            [Notice(Side.Dropbox, File, Identity), Notice(Side.Dropbox, Renamed, Identity,
                kind: ChangeKind.Renamed, oldPath: File)]);
        var selection = store.SelectWork(PairId, provenance.PairGeneration, new PathScope(store.Pair(PairId)!, store.Pairs())) ??
            throw new InvalidOperationException(FiniteMembership);
        Check.Require(selection.ThroughWorkId == through &&
            store.SelectWork(PairId, provenance.PairGeneration, new PathScope(store.Pair(PairId)!, store.Pairs())) is null, FiniteMembership);
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var volume = Pair(PairId).Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        store.AcquireJournalPage(volume, null, new JournalPosition(0, 0),
            [(PairId, provenance.PairGeneration, local.Epoch, local.Revision, false)], []);
        var localCoverage = new LocalCatchUp(PairId, provenance.PairGeneration, local.Epoch,
            new NtfsUsnCheckpoint(0, 0, 0, 0), through);
        var remoteCoverage = new DropboxCatchUp(PairId, provenance.PairGeneration, provenance.SourceEpoch,
            NextCursor, through);
        Throws<LiveAdmissionStaleException>(() => store.CaptureLiveCoverage(selection,
            localCoverage with { ThroughWorkId = through + 1 }, remoteCoverage with { ThroughWorkId = 0 }));
        var coverage = store.CaptureLiveCoverage(selection, localCoverage, remoteCoverage);
        LiveEffectScope[] footprint =
        [
            new(Side.Dropbox, PathNames.RootedDropboxPath(RemoteRoot, File), false),
            new(Side.Dropbox, PathNames.RootedDropboxPath(RemoteRoot, Renamed), false)
        ];
        store.ValidateLiveCoverage(selection, coverage, footprint);
        Throws<LiveAdmissionStaleException>(() => store.ValidateLiveCoverage(selection, coverage, []));
        Acquire(store, provenance, LastCursor, [Notice(Side.Dropbox, OtherFile, OtherIdentity)]);
        store.ValidateLiveCoverage(selection, coverage, footprint);
        var saved = new SyncedEntry(PathNames.Key(File), File, PathNames.DropboxKey(File), File,
            false, Identity, 0, 0, Identity, string.Empty);
        store.Commit(PairId, StateChange.Empty with { Record = [saved] });
        Throws<LiveAdmissionStaleException>(() => store.ValidateLiveCoverage(selection, coverage, footprint));
        Check.Require(store.BeginSelection(selection) is not null, "Selection setup must be admitted.");
        store.ValidateLiveCoverage(selection, coverage, footprint);
        store.Commit(PairId, StateChange.Empty with { Record = [saved with { LocalSize = saved.LocalSize + 1 }] });
        Throws<LiveAdmissionStaleException>(() => store.ValidateLiveCoverage(selection, coverage, footprint));
        store.Commit(PairId, StateChange.Empty with { Record = [saved] });
        foreach (var dismissed in new[] { false, true })
        {
            store.Commit(PairId, StateChange.Empty with
            {
                Raise = [(IssueKind.Tie, TextKey.TieEqualTimes, File,
                    new IssueSubject([new IssueItem(Side.Dropbox, File, Identity)], null))]
            });
            var issue = store.Issues(PairId).Single();
            if (dismissed) store.DismissIssue(PairId, issue.Id);
            Throws<LiveAdmissionStaleException>(() => store.ValidateLiveCoverage(selection, coverage, footprint));
            store.Commit(PairId, StateChange.Empty with { Close = [(File, [IssueKind.Tie])] });
        }

        // An ancestor hold and a subtree-descendant hold both reject through the same held-key owner.
        foreach (var (heldPath, subtree) in new[] { (heldPath: "Parent", subtree: false), (heldPath: "Parent/File/Child", subtree: true) })
        {
            store.Commit(PairId, StateChange.Empty with
            {
                Raise = [(IssueKind.Tie, TextKey.TieEqualTimes, heldPath,
                    new IssueSubject([new IssueItem(Side.Dropbox, heldPath, Identity)], null))]
            });
            LiveEffectScope[] heldFootprint = subtree
                ? new[] { new LiveEffectScope(Side.Dropbox, PathNames.RootedDropboxPath(RemoteRoot, File), true) }
                : footprint;
            Throws<LiveAdmissionStaleException>(() => store.ValidateLiveCoverage(selection, coverage, heldFootprint));
            store.Commit(PairId, StateChange.Empty with { Close = [(heldPath, [IssueKind.Tie])] });
        }

        using var identities = PendingWork.ByIdentity(store, Side.Dropbox, Domain, Identity).GetEnumerator();
        Check.Require(identities.MoveNext(), FiniteMembership);
        var captured = identities.Current.ThroughWorkId;
        var later = Acquire(store, provenance, LastCursor, [Notice(Side.Dropbox, File, Identity)]);
        while (identities.MoveNext())
            Check.Require(identities.Current.AnchorWorkId <= identities.Current.ThroughWorkId &&
                identities.Current.ThroughWorkId == captured, FiniteMembership);
        Throws<LiveAdmissionStaleException>(() => store.ValidateLiveCoverage(selection, coverage, footprint));
        store.CompleteWork(selection, null);
        Check.Require(store.ReadWork(Selection(provenance, later, later)).Single().WorkId == later,
            FiniteMembership);
        using var ancestors = store.PendingAncestors(PairId, provenance.PairGeneration, Side.Dropbox,
            [PathNames.DropboxKey(File)]).GetEnumerator();
        Check.Require(ancestors.MoveNext(), FiniteMembership);
        var ancestorThrough = ancestors.Current.ThroughWorkId;
        var arrived = Acquire(store, provenance, LastCursor, [Notice(Side.Dropbox, File, OtherIdentity)]);
        while (ancestors.MoveNext())
            Check.Require(ancestors.Current.AnchorWorkId <= ancestors.Current.ThroughWorkId &&
                ancestors.Current.ThroughWorkId == ancestorThrough, FiniteMembership);
        Check.Require(store.ReadWork(Selection(provenance, arrived, arrived, OtherIdentity)).Single().WorkId == arrived,
            FiniteMembership);
    }

    // Seeds a completed source prerequisite, not a fake completion token or replacement algorithm.
    private static void CompletePrerequisite(string database)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = database }.ToString());
        StateStore.OpenConnection(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE source_state SET repair_reason=NULL";
        command.ExecuteNonQuery();
    }

    // Same identity on both rows, so a later arrival shares the earlier row's owner and must still refuse.
    private static void ListedAncestorBoundary(StateStore store)
    {
        var provenance = Install(store);
        var ancestor = new[] { PathNames.DropboxKey(File) };
        bool Refuses(long? listedThrough) => store.PendingAncestors(PairId, provenance.PairGeneration, Side.Dropbox,
            ancestor, listedThrough).Any();
        var listed = Acquire(store, provenance, NextCursor, [Notice(Side.Dropbox, File, Identity)]);
        Check.Require(Refuses(null) && !Refuses(listed), ListedBoundary);
        Check.Require(Refuses(listed - 1), ListedBoundary);
        Acquire(store, provenance, LastCursor, [Notice(Side.Dropbox, File, Identity)]);
        Check.Require(Refuses(listed), ListedBoundary);
    }

    // A source epoch advance hides earlier acquired rows, so an old matching notice is not read as current evidence.
    private static void StaleSourceAncestorCase(StateStore store)
    {
        var provenance = Install(store);
        var folder = new Entry(File, true, 0, DateTimeOffset.UnixEpoch, Identity, "", "", null);
        var matching = Notice(Side.Dropbox, File, Identity) with { Observed = folder };
        var through = Acquire(store, provenance, NextCursor, [matching]);
        var key = PathNames.DropboxKey(File);
        Check.Require(store.LaterAncestorNews(PairId, provenance.PairGeneration, Side.Dropbox, [key], through - 1).News
            == AncestorNews.Unbound, "A216: a matching unbound folder notice classifies");
        var source = store.ReadSource(PairId, Side.Dropbox)!.Value;
        var next = new SourceProvenance(Account, Domain, Root, RemoteRoot, true, true, true, true,
            provenance.PairGeneration, source.Epoch + 1);
        store.AdvanceSource(PairId, Side.Dropbox, provenance.PairGeneration, source.Epoch, source.Revision,
            source.Provenance, source.Cursor, source.Epoch + 1, source.Revision + 1, next, source.Cursor,
            source.Obligation);
        Check.Require(store.LaterAncestorNews(PairId, provenance.PairGeneration, Side.Dropbox, [key], through - 1).News
            == AncestorNews.None, "A216: a stale source epoch hides the earlier rows");
    }

    // A226: a foreign change with no matching binding stays visible on every later ask and is never retired.
    private static void PersistentForeignCase(StateStore store)
    {
        var provenance = Install(store);
        var key = PathNames.DropboxKey(File);
        var through = Acquire(store, provenance, NextCursor, [Notice(Side.Dropbox, File, OtherIdentity)]);
        for (var attempt = 0; attempt < 2; attempt++)
            Check.Require(store.LaterAncestorNews(PairId, provenance.PairGeneration, Side.Dropbox, [key], through - 1).News
                == AncestorNews.Changed, "A226: a persistent foreign change stays visible");
        Check.Require(store.PendingAncestors(PairId, provenance.PairGeneration, Side.Dropbox, [key]).Any(),
            "A226: classification never retires the foreign work");
    }

    // A237: a fully acquired matching echo restates the saved binding; a retained Deleted row still refuses.
    private static void MoveBackCase(StateStore store)
    {
        var provenance = Install(store);
        var folder = new Entry(File, true, 0, DateTimeOffset.UnixEpoch, Identity, "", "", null);
        var matching = Notice(Side.Dropbox, File, Identity) with { Observed = folder };
        store.Commit(PairId, StateChange.Empty with
        {
            Record = [new SyncedEntry(PathNames.Key(File), File, PathNames.DropboxKey(File), File,
                true, Identity, 0, 0, Identity, "")]
        });
        var key = PathNames.DropboxKey(File);
        var through = Acquire(store, provenance, NextCursor, [matching]);
        Check.Require(store.LaterAncestorNews(PairId, provenance.PairGeneration, Side.Dropbox, [key], through - 1).News
            == AncestorNews.Restated, "A237: a fully acquired matching echo restates the saved binding");
        Acquire(store, provenance, LastCursor, [matching with { Kind = ChangeKind.Deleted }]);
        Check.Require(store.LaterAncestorNews(PairId, provenance.PairGeneration, Side.Dropbox, [key], through - 1).News
            == AncestorNews.Changed, "A237: a retained Deleted row still refuses");
    }

    // A217 smoke check: a Commit racing the answer still leaves a consistent answer. Atomicity itself is
    // read-verified at the one StateStore lock around LaterAncestorNews; this fixture cannot force a mixed answer
    // with one key and one row, so it is not a failing check for the lock.
    private static void CompetingCommitCase(StateStore store)
    {
        var provenance = Install(store);
        var folder = new Entry(File, true, 0, DateTimeOffset.UnixEpoch, Identity, "", "", null);
        var matching = Notice(Side.Dropbox, File, Identity) with { Observed = folder };
        var key = PathNames.DropboxKey(File);
        var through = Acquire(store, provenance, NextCursor, [matching]);
        var saved = new SyncedEntry(PathNames.Key(File), File, PathNames.DropboxKey(File), File,
            true, Identity, 0, 0, Identity, "");
        using var gate = new Barrier(2);
        var answer = Task.Run(() =>
        {
            gate.SignalAndWait();
            return store.LaterAncestorNews(PairId, provenance.PairGeneration, Side.Dropbox, [key], through - 1).News;
        });
        var commit = Task.Run(() =>
        {
            gate.SignalAndWait();
            store.Commit(PairId, StateChange.Empty with { Record = [saved] });
        });
        Task.WaitAll(answer, commit);
        Check.Require(answer.Result is AncestorNews.Unbound or AncestorNews.Restated,
            "A217: a racing Commit never produces a mixed answer");
        Check.Require(store.LaterAncestorNews(PairId, provenance.PairGeneration, Side.Dropbox, [key], through - 1).News
            == AncestorNews.Restated, "A217: after the commit the same key matches");
    }

    // A222: a matching echo survives restart; an earlier attempt's Deleted row still refuses.
    private static void RestartAndDeletedCase()
    {
        var directory = Path.Combine(Path.GetTempPath(), FolderPrefix + Guid.NewGuid().ToString(GuidFormat));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, Database);
            var key = PathNames.DropboxKey(File);
            var folder = new Entry(File, true, 0, DateTimeOffset.UnixEpoch, Identity, "", "", null);
            var matching = Notice(Side.Dropbox, File, Identity) with { Observed = folder };
            long through;
            SourceProvenance provenance;
            using (var store = StateStore.Open(path))
            {
                store.SavePair(Pair(PairId));
                provenance = Install(store);
                through = Acquire(store, provenance, NextCursor, [matching]);
                Check.Require(store.LaterAncestorNews(PairId, provenance.PairGeneration, Side.Dropbox, [key], through - 1).News
                    == AncestorNews.Unbound, "A222: the matching echo is unbound before restart");
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            using (var store = StateStore.Open(path))
            {
                Check.Require(store.LaterAncestorNews(PairId, provenance.PairGeneration, Side.Dropbox, [key], through - 1).News
                    == AncestorNews.Unbound, "A222: the matching echo survives restart");
                Acquire(store, provenance, LastCursor, [matching with { Kind = ChangeKind.Deleted }]);
                Check.Require(store.LaterAncestorNews(PairId, provenance.PairGeneration, Side.Dropbox, [key], through - 1).News
                    == AncestorNews.Changed, "A222: an earlier Deleted row refuses after restart");
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void MatchingAncestorCases()
    {
        var folder = new Entry(File, true, 0, DateTimeOffset.UnixEpoch, Identity, "", "", null);
        var matching = Notice(Side.Dropbox, File, Identity) with { Observed = folder };
        var variants = new[]
        {
            matching,
            matching with { Kind = ChangeKind.Deleted },
            matching with { Kind = ChangeKind.Renamed },
            matching with { OldPath = OtherFile },
            matching with { IdentityDeleted = true },
            matching with { Observed = null },
            matching with { Observed = folder with { IsFolder = false } },
            matching with { Observed = folder with { Skip = "skipped" } },
            matching with { Observed = folder with { Id = OtherIdentity } }
        };
        foreach (var notice in variants)
        foreach (var bound in new[] { false, true })
            WithStore(store =>
            {
                var provenance = Install(store);
                var saved = new SyncedEntry(PathNames.Key(File), File, PathNames.DropboxKey(File), File,
                    true, Identity, 0, 0, Identity, "");
                if (bound) store.Commit(PairId, StateChange.Empty with { Record = [saved] });
                var through = Acquire(store, provenance, NextCursor, [notice]);
                AncestorAnswer Answer(long generation, long covered) => store.LaterAncestorNews(PairId, generation,
                    Side.Dropbox, [PathNames.DropboxKey(File)], covered);
                var shapeMatches = notice == matching || notice.Observed?.Id == OtherIdentity;
                var expected = !shapeMatches ? AncestorNews.Changed : !bound ? AncestorNews.Unbound :
                    notice == matching ? AncestorNews.Restated : AncestorNews.Changed;
                Check.Require(Answer(provenance.PairGeneration, through - 1).News == expected, "A216 matching shape");
                Check.Require(Answer(provenance.PairGeneration, through).News == AncestorNews.None, "A216 watermark");
                Check.Require(Answer(provenance.PairGeneration + 1, 0).News == AncestorNews.None, "A216 stale generation");
                Check.Require(store.ReadWork(Selection(provenance, through, through, notice.Id, notice.Path))
                    .Single().Notice == notice, "A216 classification never retires notices");
                Acquire(store, provenance, LastCursor, [matching with { Kind = ChangeKind.Deleted }]);
                Check.Require(Answer(provenance.PairGeneration, through - 1).News == AncestorNews.Changed,
                    "A216/A237 deleted evidence wins over matching or unbound evidence");
            });
    }

    // One bound folder and a matching notice, the base every safety-clause case starts from.
    private static (SourceProvenance Provenance, ChangeNotice Matching, string Key) MatchingFolder(StateStore store)
    {
        var folder = new Entry(File, true, 0, DateTimeOffset.UnixEpoch, Identity, "", "", null);
        var matching = Notice(Side.Dropbox, File, Identity) with { Observed = folder };
        store.Commit(PairId, StateChange.Empty with
        {
            Record = [new SyncedEntry(PathNames.Key(File), File, PathNames.DropboxKey(File), File,
                true, Identity, 0, 0, Identity, "")]
        });
        return (Install(store), matching, PathNames.DropboxKey(File));
    }

    // A216/OFC-03 old-path regression, not an independent probed-key guard test: EvidencePathExact guarantees
    // Key == key; EvidenceOldPathExact guarantees OldKey != null. The redundant Key guard is read-verified.
    private static void ProbedKeySafetyCase(StateStore store)
    {
        var (provenance, _, key) = MatchingFolder(store);
        var renamed = new Entry(Renamed, true, 0, DateTimeOffset.UnixEpoch, Identity, "", "", null);
        var through = Acquire(store, provenance, NextCursor,
        [
            new ChangeNotice(Side.Dropbox, ChangeKind.Renamed, Renamed, File, Identity, renamed, false, false)
        ]);
        Check.Require(store.LaterAncestorNews(PairId, provenance.PairGeneration, Side.Dropbox, [key], through - 1).News
            == AncestorNews.Changed,
            "A216/OFC-03: a row whose key differs from the probed key (its old path) refuses");
    }

    // A216/OFC-03: an issue-associated matching row refuses (the WorkIssue clause), with no residual row.
    private static void WorkIssueSafetyCase(StateStore store, Guid attempt)
    {
        var (provenance, matching, key) = MatchingFolder(store);
        var through = Acquire(store, provenance, NextCursor, [matching]);
        var selection = store.PendingAncestors(PairId, provenance.PairGeneration, Side.Dropbox, [key], 0).Single();
        store.RecordInUseFailure(PairId, provenance.PairGeneration, selection.SourceEpoch, File,
            new IssueSubject([new IssueItem(Side.Dropbox, File, Identity)], null), TextKey.FailLocalInUse,
            selection, ItemRetryDispatch.OrdinaryItem, null, null, attempt);
        Check.Require(store.LaterAncestorNews(PairId, provenance.PairGeneration, Side.Dropbox, [key], through - 1).News
            == AncestorNews.Changed,
            "A216/OFC-03: an issue-associated matching row refuses");
    }

    // A216/OFC-03: a residual child row at the probed key refuses. The residual carries a parent anchor and an
    // obligation key together; no store operation creates one with exactly one of them.
    private static void ResidualSafetyCase(StateStore store)
    {
        var (provenance, matching, key) = MatchingFolder(store);
        var through = Acquire(store, provenance, NextCursor, [matching]);
        var selection = store.PendingAncestors(PairId, provenance.PairGeneration, Side.Dropbox, [key], 0).Single();
        store.Commit(PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailChanged, File,
                new IssueSubject([new IssueItem(Side.Dropbox, File, Identity)], null))]
        });
        var issue = store.Issues(PairId).Single();
        store.AttachIssueWork(selection, issue, [(matching, true, null)]);
        Check.Require(store.LaterAncestorNews(PairId, provenance.PairGeneration, Side.Dropbox, [key], through - 1).News
            == AncestorNews.Changed,
            "A216/OFC-03: a residual or obligated row at the probed key refuses");
    }

    // Schema checks couple Parent and Obligation in valid rows. Corrupt only the disposable store fixture to
    // isolate each defensive guard, leaving the notice, binding and issue association unchanged.
    private static void IsolatedResidualSafetyCases(StateStore store, string database)
    {
        var (provenance, matching, key) = MatchingFolder(store);
        var through = Acquire(store, provenance, NextCursor, [matching]);
        AncestorNews Answer() => store.LaterAncestorNews(PairId, provenance.PairGeneration,
            Side.Dropbox, [key], through - 1).News;
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA ignore_check_constraints=ON";
        command.ExecuteNonQuery();
        command.Parameters.AddWithValue("$work", through);
        try
        {
            foreach (var column in new[] { "parent_anchor", "obligation_key" })
            {
                Check.Require(Answer() == AncestorNews.Restated, "A216: the unassociated matching row restates");
                command.CommandText = "UPDATE change_work SET " + column + "=$value WHERE work_id=$work";
                command.Parameters.AddWithValue("$value", column == "parent_anchor" ? (object)through : key);
                Check.Require(command.ExecuteNonQuery() == 1, "A216: exactly one fixture row changes");
                Check.Require(Answer() == AncestorNews.Changed,
                    "A216/OFC-03: " + column + " alone refuses the matching row");
                command.Parameters.RemoveAt("$value");
                command.CommandText = "UPDATE change_work SET " + column + "=NULL WHERE work_id=$work";
                Check.Require(command.ExecuteNonQuery() == 1, "A216: exactly one fixture row is restored");
                Check.Require(Answer() == AncestorNews.Restated, "A216: removing only the guard input restores restatement");
            }
        }
        finally
        {
            command.CommandText = "PRAGMA ignore_check_constraints=OFF";
            command.ExecuteNonQuery();
        }
    }

    private static void RenameBaselineCases(StateStore store, string database)
    {
        var provenance = Install(store);
        CompletePrerequisite(database);
        var through = Acquire(store, provenance, NextCursor,
            [Notice(Side.Dropbox, Renamed, Identity, kind: ChangeKind.Renamed, oldPath: File)]);
        var rename = Selection(provenance, through, through, identity: Identity, path: Renamed);
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var volume = Pair(PairId).Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        store.AcquireJournalPage(volume, null, new JournalPosition(0, 0),
            [(PairId, provenance.PairGeneration, local.Epoch, local.Revision, false)], []);
        var coverage = store.CaptureLiveCoverage(rename,
            new LocalCatchUp(PairId, provenance.PairGeneration, local.Epoch,
                new NtfsUsnCheckpoint(0, 0, 0, 0), through),
            new DropboxCatchUp(PairId, provenance.PairGeneration, provenance.SourceEpoch, NextCursor, through));
        var footprint = new[] { new LiveEffectScope(Side.Dropbox, PathNames.RootedDropboxPath(RemoteRoot, Renamed), false) };
        var old = new SyncedEntry(PathNames.Key(File), File, PathNames.DropboxKey(File), File,
            false, Identity, 0, 0, Identity, string.Empty);
        store.Commit(PairId, StateChange.Empty with { Record = [old] });
        Check.Require(store.BeginSelection(rename) is not null, "Selection setup must be admitted.");
        // The same identity may keep its saved binding at the old path through the rename.
        store.ValidateLiveCoverage(rename, coverage, footprint);
        var after = old with { LocalKey = PathNames.Key(Renamed), LocalPath = Renamed,
            DropboxKey = PathNames.DropboxKey(Renamed), DropboxPath = Renamed };
        var change = StateChange.Empty with { Renames = [new RenameApplied(old, after, null, null, null)] };
        var foreign = old with { LocalId = OtherIdentity };
        store.Commit(PairId, StateChange.Empty with { Record = [foreign] });
        Throws<LiveAdmissionStaleException>(() => store.Commit(PairId, change, rename, null));
        Check.Require(store.ReadWork(rename).All(row => row.Before == old) &&
            store.SyncedByKey(PairId, Side.Local, old.LocalKey) == foreign,
            "a foreign replacement refuses without advancing selected preimages");
        store.Commit(PairId, StateChange.Empty with { Record = [old] });
        var laterThrough = Acquire(store, provenance, LastCursor,
            [Notice(Side.Dropbox, Renamed, Identity, kind: ChangeKind.Changed)]);
        var later = Selection(provenance, laterThrough, laterThrough, path: Renamed);
        Check.Require(store.BeginSelection(later) is not null, "Selection setup must be admitted.");
        store.Commit(PairId, change, rename, null);
        Check.Require(store.ReadWork(later).Single().Before == old,
            "a rename advances no later arrival outside its selected watermark");
        store.CompleteWork(later, null);
        coverage = store.CaptureLiveCoverage(rename, coverage.Local with { ThroughWorkId = laterThrough },
            coverage.Dropbox with { Cursor = LastCursor, ThroughWorkId = laterThrough });
        Check.Require(store.ReadWork(rename).All(row => row.Before == after),
            "a rename advances selected preimages with its saved row");
        store.ValidateLiveCoverage(rename, coverage, footprint);
        // A different binding already occupying the new target refuses the rename.
        store.Commit(PairId, StateChange.Empty with
        {
            Record = [new SyncedEntry(PathNames.Key(Renamed), Renamed, PathNames.DropboxKey(Renamed), Renamed,
                false, OtherIdentity, 0, 0, OtherIdentity, string.Empty)]
        });
        Throws<LiveAdmissionStaleException>(() => store.ValidateLiveCoverage(rename, coverage, footprint));
    }

    private static void SeparateWorkProbeCounts()
    {
        foreach (var count in new[] { 8, 64 }) WithStore((store, _) =>
        {
            var provenance = Install(store);
            var through = Acquire(store, provenance, NextCursor, [Notice(Side.Dropbox, File, Identity)]);
            var selection = Selection(provenance, through, through);
            var probe = store.CreateSeparateWorkProbe(selection);
            Check.Require(!probe(Side.Dropbox, PathNames.DropboxKey(File), Identity),
                "R07: own membership is not separate work");
            Acquire(store, provenance, LastCursor, [Notice(Side.Dropbox, OtherFile, OtherIdentity)]);
            Check.Require(probe(Side.Dropbox, PathNames.DropboxKey(OtherFile), OtherIdentity),
                "R07: probe still sees newly acquired separate work");
            var connection = (Microsoft.Data.Sqlite.SqliteConnection)typeof(StateStore).GetField("_connection",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
            var statements = 0;
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)((_, _) => statements++), null);
            try
            {
                for (var index = 0; index < count; index++)
                {
                    Check.Require(!probe(Side.Local, PathNames.Key("Missing"), "999"), "R07: absent Local work");
                    Check.Require(!probe(Side.Dropbox, PathNames.DropboxKey("Missing"), "missing-id"), "R07: absent Dropbox work");
                }
            }
            finally { SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null); }
            Check.Require(statements == count * 6, "R07: two-side walk pays only six indexed evidence statements per child");
            Console.WriteLine($"separate-work children={count} statements={statements}");
        });
    }

    private static void SelectionRefreshAfterFolderMoveCounts()
    {
        (int Reads, int Updates)? expected = null;
        // Eightfold subtree growth with the same two retained rows detects per-child statement growth.
        foreach (var count in new[] { 8, 64 }) WithStore((store, database) =>
        {
            var provenance = Install(store);
            CompletePrerequisite(database);
            var folder = new SyncedEntry(PathNames.Key("Tree"), "Tree", PathNames.DropboxKey("Tree"), "Tree",
                true, "root-local", 0, 0, "root-remote", string.Empty);
            var children = Enumerable.Range(0, count).Select(index => new SyncedEntry(PathNames.Key("Tree/" + index),
                "Tree/" + index, PathNames.DropboxKey("Tree/" + index), "Tree/" + index, false,
                "local-" + index, 0, 0, "remote-" + index, string.Empty)).ToArray();
            store.Commit(PairId, StateChange.Empty with { Record = [folder, .. children] });
            var through = Acquire(store, provenance, NextCursor,
                children.Take(2).Select(child => Notice(Side.Dropbox, child.DropboxPath, child.DropboxId)));
            var first = Selection(provenance, through - 1, through - 1, children[0].DropboxId, children[0].DropboxPath);
            var foreign = Selection(provenance, through, through, children[1].DropboxId, children[1].DropboxPath);
            Check.Require(store.BeginSelection(first) is not null, "Selection setup must be admitted.");
            var different = children[1] with { LocalSize = 1 };
            store.Commit(PairId, StateChange.Empty with { Record = [different] });
            Check.Require(store.BeginSelection(foreign) is not null, "Selection setup must be admitted.");
            store.Commit(PairId, StateChange.Empty with { Record = [children[1]] });
            var connection = (Microsoft.Data.Sqlite.SqliteConnection)typeof(StateStore).GetField("_connection",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
            var reads = 0;
            var updates = 0;
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)((_, sql) =>
            {
                var text = sql.utf8_to_string();
                if (text.StartsWith("SELECT", StringComparison.Ordinal) && text.Contains("change_work", StringComparison.Ordinal)) reads++;
                if (text.StartsWith("UPDATE change_work SET before_json=", StringComparison.Ordinal)) updates++;
            }), null);
            try
            {
                store.Commit(PairId, StateChange.Empty with { Renames = [new RenameApplied(folder,
                    folder with { LocalKey = PathNames.Key("Moved"), LocalPath = "Moved",
                        DropboxKey = PathNames.DropboxKey("Moved"), DropboxPath = "Moved" }, null, null, null)] });
            }
            finally { SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null); }
            Check.Require(store.ReadWork(first).Single().Before == children[0] &&
                store.ReadWork(foreign).Single().Before == different,
                "folder move does no eager work-preimage rewriting");
            Check.Require(store.BeginSelection(first) is not null, "Selection setup must be admitted.");
            Check.Require(store.BeginSelection(foreign) is not null, "Selection setup must be admitted.");
            Check.Require(store.ReadWork(first).Single().Before?.LocalPath == "Moved/0" &&
                store.ReadWork(foreign).Single().Before == store.SyncedById(PairId, Side.Dropbox, children[1].DropboxId),
                "selection start refreshes both old and incompatible preimages to their current identity bindings");
            expected ??= (reads, updates);
            Check.Require(reads == 0 && updates == 0 && expected == (reads, updates),
                "folder move pays no descendant work reads or preimage writes at either size");
            Console.WriteLine($"selection-refresh subtree={count} affected=2 move-reads={reads} move-updates={updates}");
        });
    }

    private static void ExactFolderAdmission(StateStore store, string database)
    {
        var provenance = Install(store);
        CompletePrerequisite(database);
        var through = Acquire(store, provenance, NextCursor, [Notice(Side.Dropbox, File, Identity)]);
        var selection = Selection(provenance, through, through);
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var volume = Pair(PairId).Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        store.AcquireJournalPage(volume, null, new JournalPosition(0, 0),
            [(PairId, provenance.PairGeneration, local.Epoch, local.Revision, false)], []);
        var coverage = store.CaptureLiveCoverage(selection,
            new LocalCatchUp(PairId, provenance.PairGeneration, local.Epoch, new NtfsUsnCheckpoint(0, 0, 0, 0), through),
            new DropboxCatchUp(PairId, provenance.PairGeneration, provenance.SourceEpoch, NextCursor, through));
        var exact = new LiveEffectScope(Side.Dropbox, PathNames.RootedDropboxPath(RemoteRoot, File), false);
        Acquire(store, provenance, LastCursor, [Notice(Side.Dropbox, File + "/Child", OtherIdentity)]);
        store.ValidateLiveCoverage(selection, coverage, [exact]);
        Throws<LiveAdmissionStaleException>(() => store.ValidateLiveCoverage(selection, coverage, [exact with { IncludesDescendants = true }]));
        Acquire(store, provenance, LastCursor, [Notice(Side.Dropbox, File, OtherIdentity)]);
        Throws<LiveAdmissionStaleException>(() => store.ValidateLiveCoverage(selection, coverage, [exact]));
    }

    private static void DeletedChildAdmission(StateStore store, string database, bool dirty)
    {
        var provenance = Install(store);
        CompletePrerequisite(database);
        store.Commit(PairId, StateChange.Empty with { Record = [new SyncedEntry(PathNames.Key(File), File,
            PathNames.DropboxKey(File), File, true, "local-folder", 0, 0, Identity, string.Empty)] });
        var through = Acquire(store, provenance, NextCursor,
            [Notice(Side.Dropbox, File, Identity),
             Notice(Side.Dropbox, File, Identity, kind: ChangeKind.Deleted),
             Notice(Side.Dropbox, File + "/Child", OtherIdentity, dirty: dirty, kind: ChangeKind.Deleted)]);
        var selection = Selection(provenance, through - 2, through);
        Check.Require(store.BeginSelection(selection) is not null, "Selection setup must be admitted.");
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var volume = Pair(PairId).Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        store.AcquireJournalPage(volume, null, new JournalPosition(0, 0),
            [(PairId, provenance.PairGeneration, local.Epoch, local.Revision, false)], []);
        var coverage = store.CaptureLiveCoverage(selection,
            new LocalCatchUp(PairId, provenance.PairGeneration, local.Epoch, new NtfsUsnCheckpoint(0, 0, 0, 0), through),
            new DropboxCatchUp(PairId, provenance.PairGeneration, provenance.SourceEpoch, NextCursor, through));
        var scope = new LiveEffectScope(Side.Dropbox, PathNames.RootedDropboxPath(RemoteRoot, File), true);
        // The survivor-aware folder plan classifies every child itself (changed content survives, unchanged is
        // removed), so a child's pending evidence - dirty or not - is covered by the folder selection.
        store.ValidateLiveCoverage(selection, coverage, [scope]);
        Acquire(store, provenance, LastCursor,
            [Notice(Side.Dropbox, File + "/Later", "later-id", dirty: true)]);
        Throws<LiveAdmissionStaleException>(() => store.ValidateLiveCoverage(selection, coverage, [scope]));
        Check.Require(store.PendingAncestors(PairId, provenance.PairGeneration, Side.Dropbox,
                [PathNames.DropboxKey(File)]).Any(),
            "a pending folder deletion stays an ancestor; its plan covers the children, dirty or not");
        Check.Require(store.PendingAncestors(PairId, provenance.PairGeneration, Side.Dropbox,
                [PathNames.DropboxKey(File)], 0).Any(),
            "Repair still refuses later ancestor evidence even when a live dirty child has precedence");
    }

    private static void LeaveAncestorPrecedence(StateStore store, string database, bool mapped)
    {
        var provenance = Install(store);
        CompletePrerequisite(database);
        var pair = store.Pair(PairId)!;
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var volume = pair.Roots!.LocalVolumeKey();
        store.Commit(PairId, StateChange.Empty with { Record = [new SyncedEntry(PathNames.Key(File), File,
            PathNames.DropboxKey(File), File, true, Identity, 0, 0, OtherIdentity, string.Empty)] });
        var moved = new Entry(Renamed, true, 0, DateTimeOffset.UnixEpoch, Identity, string.Empty, string.Empty, null);
        store.AcquireJournalPage(volume, null, new JournalPosition(0, 1),
            [(PairId, provenance.PairGeneration, local.Epoch, local.Revision, false)],
            [(PairId, Notice(Side.Local, Renamed, Identity, kind: ChangeKind.Renamed, oldPath: File) with { Observed = moved }),
             (PairId, Notice(Side.Local, File + "/Child", OtherIdentity, dirty: true))]);
        if (mapped)
            store.Commit(PairId, StateChange.Empty with { Record = [new SyncedEntry(PathNames.Key("Parent"), "Parent",
                PathNames.DropboxKey("Mapped"), "Mapped", true, "parent-id", 0, 0, "remote-parent-id", string.Empty)] });
        var excluded = mapped
            ? new PathScope(pair with { Exclusions = [new Exclusion(Side.Dropbox, "Mapped/Renamed", true)] }, [])
            : new PathScope(pair, [], Massdex.SyncShark.SyncIgnorePolicy.Format(
                new Massdex.SyncShark.SyncIgnoreRule(Massdex.SyncShark.SyncIgnoreKind.FolderName, PathNames.Name(Renamed))));
        Check.Require(excluded.Excludes(Renamed, null, true) != mapped,
            "mapped exclusion is visible only at the counterpart destination");
        var preparedReads = 0;
        Check.Require(excluded.LeavesScope(Renamed, mapped ? "Mapped/Renamed" : Renamed, true, store,
            (owner, side, key) =>
            {
                preparedReads++;
                return owner.SyncedByKey(PairId, side, key);
            }) && preparedReads == 0,
            "the rename runner's already prepared destinations require no additional mapping reads");
        _ = store.SelectWork(PairId, provenance.PairGeneration, excluded);
        var connection = (Microsoft.Data.Sqlite.SqliteConnection)typeof(StateStore).GetField("_connection",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
        var mappings = 0;
        SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)((_, sql) =>
        {
            var text = sql.utf8_to_string();
            if (text.Contains("FROM synced_entry WHERE pair_id", StringComparison.Ordinal) &&
                text.Contains(" AND pathkey(local_path) =", StringComparison.Ordinal)) mappings++;
        }), null);
        try
        {
            foreach (var repeat in new[] { 0, 1 })
                Check.Require(store.PendingAncestors(PairId, provenance.PairGeneration, Side.Local,
                    [PathNames.Key(File)]).Any(),
                    "a pending Leave stays an ancestor; its plan classifies the children instead of refusing them");
        }
        finally { SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null); }
        Console.WriteLine($"ancestor-leave mapped={mapped} mapping-probes={mappings}");
        Check.Require(mappings == 0, "ancestor discovery no longer classifies Leave or maps its parent; measured=" + mappings);
        store.EndWorkPass(PairId);
        _ = store.SelectWork(PairId, provenance.PairGeneration, new PathScope(pair, []));
        Check.Require(store.PendingAncestors(PairId, provenance.PairGeneration, Side.Local,
            [PathNames.Key(File)]).Any(), "an in-scope rename keeps normal ancestor precedence");
    }

    private static void OppositeDeletionPrecedence(StateStore store, string database, Side restating, bool deleting)
    {
        var provenance = Install(store);
        CompletePrerequisite(database);
        var pair = store.Pair(PairId)!;
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var saved = new SyncedEntry(PathNames.Key(File), File, PathNames.DropboxKey(Renamed), Renamed,
            true, Identity, 0, 0, OtherIdentity, string.Empty);
        store.Commit(PairId, StateChange.Empty with { Record = [saved] });
        ChangeNotice RootNotice(Side side)
        {
            var removed = side != restating && deleting;
            var path = saved.PathOn(side);
            return Notice(side, path, removed && side == Side.Dropbox ? null : saved.IdOn(side),
                kind: removed ? ChangeKind.Deleted : ChangeKind.Changed) with
            {
                Observed = removed ? null : new Entry(path, true, 0, DateTimeOffset.UnixEpoch,
                    saved.IdOn(side), string.Empty, string.Empty, null)
            };
        }
        var child = Notice(restating, saved.PathOn(restating) + "/Child", "child-identity", dirty: restating == Side.Local);
        store.AcquireJournalPage(pair.Roots!.LocalVolumeKey(), null, new JournalPosition(0, 1),
            [(PairId, provenance.PairGeneration, local.Epoch, local.Revision, false)],
            restating == Side.Local ? [(PairId, RootNotice(Side.Local)), (PairId, child)] : [(PairId, RootNotice(Side.Local))]);
        Acquire(store, provenance, NextCursor,
            restating == Side.Dropbox ? [RootNotice(Side.Dropbox), child] : [RootNotice(Side.Dropbox)]);
        _ = store.SelectWork(PairId, provenance.PairGeneration, new PathScope(pair, []));
        var pending = store.PendingAncestors(PairId, provenance.PairGeneration, restating,
            [saved.KeyOn(restating)]).Count();
        Console.WriteLine($"ancestor-restatement side={restating} deleting={deleting} owners={pending}");
        Check.Require(pending == 1,
            "the folder plan owns deletion precedence even when its counterpart restates");
    }

    private static void ObservedIdentityAdmission(StateStore store, string database)
    {
        var provenance = Install(store);
        CompletePrerequisite(database);
        var through = Acquire(store, provenance, NextCursor,
            [Notice(Side.Dropbox, File, Identity, kind: ChangeKind.Deleted),
             Notice(Side.Dropbox, File, OtherIdentity)]);
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var volume = Pair(PairId).Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        store.AcquireJournalPage(volume, null, new JournalPosition(0, 0),
            [(PairId, provenance.PairGeneration, local.Epoch, local.Revision, false)], []);
        foreach (var (id, identity) in new[] { (through - 1, Identity), (through, OtherIdentity) })
        {
            var selection = Selection(provenance, id, through, identity);
            Check.Require(store.BeginSelection(selection) is not null, "Selection setup must be admitted.");
            var coverage = store.CaptureLiveCoverage(selection,
                new LocalCatchUp(PairId, provenance.PairGeneration, local.Epoch, new NtfsUsnCheckpoint(0, 0, 0, 0), through),
                new DropboxCatchUp(PairId, provenance.PairGeneration, provenance.SourceEpoch, NextCursor, through));
            store.ValidateLiveCoverage(selection, coverage,
                [new LiveEffectScope(Side.Dropbox, PathNames.RootedDropboxPath(RemoteRoot, File), false)]);
        }
    }

    private static void ReplacedIdentityBaseline(StateStore store, string database)
    {
        var provenance = Install(store);
        CompletePrerequisite(database);
        var saved = new SyncedEntry(PathNames.Key(File), File, PathNames.DropboxKey(File), File,
            false, Identity, 0, 0, Identity, string.Empty);
        store.Commit(PairId, StateChange.Empty with { Record = [saved] });
        var start = store.ReadWorkWatermark();
        var through = Acquire(store, provenance, NextCursor,
            [Notice(Side.Dropbox, File, Identity, kind: ChangeKind.Deleted), Notice(Side.Dropbox, File, OtherIdentity)]);
        var previous = Selection(provenance, through - 1, through, identity: Identity);
        var selection = Selection(provenance, through, through, identity: OtherIdentity);
        Check.Require(store.BeginSelection(previous) is not null, "Selection setup must be admitted.");
        Check.Require(store.BeginSelection(selection) is not null, "Selection setup must be admitted.");
        Check.Require(through - start == 2 && store.ReadWork(previous).Count() == 1 && store.ReadWork(selection).Count() == 1,
            "R05: exactly two same-path rows represent the deleted identity and its replacement");
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var volume = Pair(PairId).Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        store.AcquireJournalPage(volume, null, new JournalPosition(0, 0),
            [(PairId, provenance.PairGeneration, local.Epoch, local.Revision, false)], []);
        var coverage = store.CaptureLiveCoverage(selection,
            new LocalCatchUp(PairId, provenance.PairGeneration, local.Epoch, new NtfsUsnCheckpoint(0, 0, 0, 0), through),
            new DropboxCatchUp(PairId, provenance.PairGeneration, provenance.SourceEpoch, NextCursor, through));
        var footprint = new[] { new LiveEffectScope(Side.Dropbox, PathNames.RootedDropboxPath(RemoteRoot, File), false) };
        store.ValidateLiveCoverage(selection, coverage, footprint);
        var replacement = saved with { DropboxId = OtherIdentity };
        store.Commit(PairId, StateChange.Empty with { Record = [replacement] }, selection, null);
        Check.Require(store.CompleteWork(selection, null), "R05: replacement row retires after its own Record");
        Check.Require(store.BeginSelection(previous) is not null, "Selection setup must be admitted.");
        store.ValidateLiveCoverage(previous, coverage, footprint);
        Check.Require(store.ReadWork(previous).Single().Before is null && store.CompleteWork(previous, null) &&
            !PendingWork.ByIdentity(store, Side.Dropbox, Domain, Identity).Any() &&
            !PendingWork.ByIdentity(store, Side.Dropbox, Domain, OtherIdentity).Any() &&
            store.SyncedByKey(PairId, Side.Dropbox, saved.DropboxKey) == replacement,
            "R05: old and replacement identity work both drain without removing the replacement binding");
    }

    private static void OwnRecordBaseline(StateStore store)
    {
        var provenance = Install(store);
        var through = Acquire(store, provenance, NextCursor, [Notice(Side.Dropbox, File, Identity)]);
        var selection = Selection(provenance, through, through);
        var own = new SyncedEntry(PathNames.Key(File), File, PathNames.DropboxKey(File), File,
            true, Identity, 0, 0, Identity, string.Empty);
        store.Commit(PairId, StateChange.Empty with { Record = [own] });
        Check.Require(store.BeginSelection(selection) is not null, "Selection setup must be admitted.");
        var foreign = own with { LocalId = OtherIdentity };
        store.Commit(PairId, StateChange.Empty with { Record = [foreign] });
        Throws<LiveAdmissionStaleException>(() => store.Commit(PairId, StateChange.Empty with { Record = [own] }, selection, null));
        Check.Require(store.ReadWork(selection).Single().Before == own && store.SyncedByKey(PairId, Side.Local, own.LocalKey) == foreign,
            "own record refuses a real foreign baseline and leaves both facts unchanged");
        store.Commit(PairId, StateChange.Empty with { ForgetSelf = [(Side.Local, own.LocalKey)] });
        Check.Require(store.BeginSelection(selection) is not null, "Selection setup must be admitted.");
        Check.Require(store.ReadWork(selection).Single().Before is null,
            "INV-A: a new selection heals a binding forgotten by another operation");
        var unrelated = own with { LocalKey = PathNames.Key(OtherFile), LocalPath = OtherFile,
            DropboxKey = PathNames.DropboxKey(OtherFile), DropboxPath = OtherFile, LocalId = OtherIdentity, DropboxId = OtherIdentity };
        store.Commit(PairId, StateChange.Empty with { Record = [unrelated] }, selection, null);
        Check.Require(store.ReadWork(selection).Single().Before is null, "another row's record never advances the anchor");
        store.Commit(PairId, StateChange.Empty with { Record = [own] }, selection, null);
        Check.Require(store.ReadWork(selection).Single().Before == own, "own record advances the selected baseline atomically");
        var moved = own with { LocalKey = PathNames.Key(Renamed), LocalPath = Renamed,
            DropboxKey = PathNames.DropboxKey(Renamed), DropboxPath = Renamed };
        store.Commit(PairId, StateChange.Empty with { Renames = [new RenameApplied(own, moved, null, null, null)] }, selection, null);
        var uploaded = moved with { DropboxContentHash = "updated" };
        store.Commit(PairId, StateChange.Empty with { Record = [uploaded], ForgetSelf = [(Side.Local, moved.LocalKey)] }, selection, null);
        Check.Require(store.ReadWork(selection).Single().Before == uploaded,
            "R04: own identity record advances after its acquired path became stale");
        var pathWork = Acquire(store, provenance, LastCursor, [Notice(Side.Dropbox, File, null)]);
        var pathSelection = Selection(provenance, pathWork, pathWork, identity: null);
        store.Commit(PairId, StateChange.Empty with { Record = [uploaded] }, pathSelection, null);
        Check.Require(store.ReadWork(pathSelection).Single().Before is null,
            "R04: path-only selection does not advance for a record at another path");
    }

    private static void ForgottenSelectionBaseline(StateStore store, bool subtree)
    {
        var provenance = Install(store);
        var through = Acquire(store, provenance, NextCursor,
            [Notice(Side.Dropbox, Renamed, Identity, kind: ChangeKind.Renamed, oldPath: File)]);
        var selection = Selection(provenance, through, through, path: Renamed);
        var before = new SyncedEntry(PathNames.Key(File), File, PathNames.DropboxKey(File), File,
            false, Identity, 0, 0, Identity, string.Empty);
        store.Commit(PairId, StateChange.Empty with { Record = [before] });
        Check.Require(store.BeginSelection(selection) is not null, "Selection setup must be admitted.");
        var change = subtree
            ? StateChange.Empty with { ForgetSubtrees = [(Side.Local, PathNames.Key("Parent"))] }
            : StateChange.Empty with { ForgetSelf = [(Side.Local, before.LocalKey)] };
        store.Commit(PairId, change, selection, null);
        Check.Require(store.ReadWork(selection).Single().Before is null &&
            store.SyncedById(PairId, Side.Dropbox, Identity) is null,
            "F01: forgetting the selected binding clears its preimage atomically");
    }

    private static void InUseNoticeOpportunity(string database, bool acquired, bool dirty)
    {
        var attempt = Guid.NewGuid();
        using var store = StateStore.Open(database, (id, _, _, _, _) => id == attempt);
        var provenance = Install(store);
        CompletePrerequisite(database);
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var volume = Pair(PairId).Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var generation = provenance.PairGeneration;
        var first = store.AcquireJournalPage(volume, null, new JournalPosition(0, 1),
            [(PairId, generation, local.Epoch, local.Revision, false)],
            acquired ? [(PairId, Notice(Side.Local, File, Identity))] : [])!.Value;
        var initial = acquired ? store.SelectWork(PairId, generation, new PathScope(store.Pair(PairId)!, store.Pairs())) : null;
        var subject = new IssueSubject([new IssueItem(Side.Local, File, Identity)], null);
        var episode = store.RecordInUseFailure(PairId, generation, local.Epoch, File, subject,
            TextKey.FailLocalInUse, initial, ItemRetryDispatch.OrdinaryItem, null, null, attempt);
        store.EndWorkPass(PairId);
        Check.Require(store.SelectWork(PairId, generation, new PathScope(store.Pair(PairId)!, store.Pairs())) is null,
            "an existing in-use episode has no restart-only opportunity");
        var through = store.AcquireJournalPage(volume, new JournalPosition(0, 1), new JournalPosition(0, 2),
            [(PairId, generation, local.Epoch, local.Revision, false)],
            [(PairId, Notice(Side.Local, File, Identity, dirty: dirty))])!.Value;
        var next = store.SelectWork(PairId, generation, new PathScope(store.Pair(PairId)!, store.Pairs())) ?? throw new InvalidOperationException(ExpectedFailure);
        Check.Require(next.AnchorWorkId == through && next.AnchorWorkId > episode.Token.WorkId,
            "a real next notice passes an older acquired or Repair-created episode owner");
        Check.Require(store.BeginSelection(next) is not null, "Selection setup must be admitted.");
        var coverage = store.CaptureLiveCoverage(next,
            new LocalCatchUp(PairId, generation, local.Epoch, new NtfsUsnCheckpoint(0, 0, 0, 2), through),
            new DropboxCatchUp(PairId, generation, provenance.SourceEpoch, InitialCursor, through));
        store.ValidateLiveCoverage(next, coverage,
            [new LiveEffectScope(Side.Local, LocalRoot + "\\" + File.Replace('/', '\\'), false)]);
        var refreshed = store.RecordInUseFailure(PairId, generation, local.Epoch, File, subject,
            TextKey.FailLocalInUse, next, ItemRetryDispatch.OrdinaryItem, null, episode.Token, attempt);
        Check.Require(refreshed.Token.WorkId == episode.Token.WorkId && store.ReadWorkWatermark() == through,
            "retry refresh retains the original episode association and creates no second owner");
        store.AttachIssueWork(next, store.IssueById(PairId, episode.Token.IssueId)!,
            [(Notice(Side.Local, File, Identity, dirty: dirty), false, null)]);
        Check.Require(store.CompleteWork(next, null) != dirty,
            "clean trigger retires; dirty trigger remains parented to its episode");
        store.EndWorkPass(PairId);
        Check.Require(store.SelectWork(PairId, generation, new PathScope(store.Pair(PairId)!, store.Pairs())) is null &&
            store.ReadItemRetryState(PairId, [episode.Token.IssueId])[episode.Token.IssueId] is not null,
            "persistent in-use failure waits visibly, without another attempt on an unrelated wake");
        if (!dirty) return;
        var repeatedId = store.AcquireJournalPage(volume, new JournalPosition(0, 2), new JournalPosition(0, 3),
            [(PairId, generation, local.Epoch, local.Revision, false)],
            [(PairId, Notice(Side.Local, File, Identity, dirty: true))])!.Value;
        var repeated = store.SelectWork(PairId, generation, new PathScope(store.Pair(PairId)!, store.Pairs())) ?? throw new InvalidOperationException(ExpectedFailure);
        Check.Require(store.BeginSelection(repeated) is not null, "Selection setup must be admitted.");
        store.AttachIssueWork(repeated, store.IssueById(PairId, episode.Token.IssueId)!,
            [(Notice(Side.Local, File, Identity, dirty: true), false, null)]);
        Check.Require(store.ReadWorkWatermark() == repeatedId && store.CompleteWork(repeated, null),
            "repeated dirty notices refresh the one episode child without accumulating work");
        store.EndWorkPass(PairId);
        using (var reopened = StateStore.OpenReadOnly(database)!)
            Check.Require(reopened.ReadLocalDirty(PairId, generation, local.Epoch, volume, Identity,
                PathNames.Key(File), null).Dirty, "the episode's dirty child survives a reopen");
        store.CompleteItemAttempt(refreshed, attempt,
            StateChange.Empty with { Close = [(File, new[] { IssueKind.Failure })] }, null, null);
        store.EndWorkPass(PairId);
        var released = store.SelectWork(PairId, generation, new PathScope(store.Pair(PairId)!, store.Pairs())) ?? throw new InvalidOperationException(ExpectedFailure);
        Check.Require(released.AnchorWorkId == next.AnchorWorkId && store.ReadWork(released).Single().Notice.LocalContentDirty,
            "closing the episode releases the same durable dirty child for ordinary selection");
        Check.Require(store.BeginSelection(released) is not null, "Selection setup must be admitted.");
        Check.Require(store.CompleteWork(released, null), "the released dirty child can finish normally");
    }

    /// <summary>Measure-only (B-198 member cost): the in-use check (inside ValidateLiveCoverage) and the source-less
    /// projection (ReadWork) on a path carrying N in-use member rows, per member shape, plus a path with no member.
    /// Prints only; asserts nothing about cost.</summary>
    public static void B198MemberCostProbe()
    {
        WithStore((_, database) => B198MemberCost(database, "none", 0));
        foreach (var shape in new[] { "valid", "mixed", "null-only", "later-work" })
            foreach (var members in new[] { 1, 100, 1000 })
                WithStore((_, database) => B198MemberCost(database, shape, members));
    }

    private static void B198MemberCost(string database, string shape, int members)
    {
        var attempt = Guid.NewGuid();
        using var store = StateStore.Open(database, (id, _, _, _, _) => id == attempt);
        var provenance = Install(store);
        CompletePrerequisite(database);
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var volume = Pair(PairId).Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var generation = provenance.PairGeneration;
        const int Calls = 20;
        store.AcquireJournalPage(volume, null, new JournalPosition(0, 1),
            [(PairId, generation, local.Epoch, local.Revision, false)], []);
        string? heldIdentity = shape == "null-only" ? null : Identity;
        ItemRetryCandidate? held = null;
        long rows = 0;
        if (members > 0)
        {
            IssueItem[] items = shape switch
            {
                "mixed" => [new IssueItem(Side.Local, File, Identity), new IssueItem(Side.Local, OtherFile, null!)],
                "null-only" => [new IssueItem(Side.Local, File, null!)],
                _ => [new IssueItem(Side.Local, File, Identity)]
            };
            // A Repair-created (source-less) in-use wait at the path: its member is what both readers decode.
            held = store.RecordInUseFailure(PairId, generation, local.Epoch, File, new IssueSubject(items, null),
                TextKey.FailLocalInUse, null, ItemRetryDispatch.OrdinaryItem, null, null, attempt);
            store.EndWorkPass(PairId);
            // Fixture-only: clone that member row so the path carries N in-use members (fake work ids, foreign keys
            // off). Negative ids sit before any selection anchor (decoded); later-work ids sit after it (skipped).
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { DataSource = database, Pooling = false }.ToString());
            connection.Open();
            using (var off = connection.CreateCommand())
            {
                off.CommandText = "PRAGMA foreign_keys=OFF;";
                off.ExecuteNonQuery();
            }
            using (var transaction = connection.BeginTransaction())
            {
                using var clone = connection.CreateCommand();
                clone.Transaction = transaction;
                clone.CommandText = "INSERT INTO issue_member(issue_id,pair_id,work_id,side,path_key,identity,subject_json,skip_cause) " +
                    "SELECT issue_id,pair_id,$work,side,path_key,identity,subject_json,skip_cause FROM issue_member " +
                    "WHERE path_key=$path AND work_id IS NOT NULL ORDER BY member_id LIMIT 1;";
                var work = clone.Parameters.Add("$work", Microsoft.Data.Sqlite.SqliteType.Integer);
                clone.Parameters.AddWithValue("$path", PathNames.Key(File));
                for (var index = 1; index < members; index++)
                {
                    work.Value = shape == "later-work" ? 1_000_000_000L + index : -index;
                    clone.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM issue_member WHERE path_key=$path AND work_id IS NOT NULL;";
            count.Parameters.AddWithValue("$path", PathNames.Key(File));
            rows = (long)count.ExecuteScalar()!;
        }
        // Store-call elapsed time is the store lock hold: each call holds the store lock for its whole body.
        string Measure(Func<string> call)
        {
            string outcome;
            try { outcome = call(); }
            catch (Exception error) { return "error=" + error.GetType().Name; }
            var statements = Statements(store, () => call());
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var allocated = AllocProbe.Measure(_ => call(), calls: Calls, warmup: 0);
            clock.Stop();
            return $"{outcome} sql-statements={statements} per-call-us={clock.Elapsed.TotalMilliseconds * 1000 / Calls:F1} " +
                $"per-call-bytes={allocated / Calls}";
        }
        var project = held is null ? "n/a" : Measure(() => store.ReadWork(new WorkSelection(PairId, generation,
                local.Epoch, Side.Local, volume, heldIdentity, PathNames.Key(File), held.Token.WorkId, held.Token.WorkId))
            .ToList() is [{ Notice.Path: File }] ? "projected" : "other");
        // The held file's own next notice: the in-use check reads every member row at the path.
        var noticed = store.AcquireJournalPage(volume, new JournalPosition(0, 1), new JournalPosition(0, 2),
            [(PairId, generation, local.Epoch, local.Revision, false)], [(PairId, Notice(Side.Local, File, Identity))])!.Value;
        var next = store.SelectWork(PairId, generation, new PathScope(store.Pair(PairId)!, store.Pairs()));
        var admit = "no-selection";
        if (next is not null && store.BeginSelection(next) is not null)
        {
            var coverage = store.CaptureLiveCoverage(next,
                new LocalCatchUp(PairId, generation, local.Epoch, new NtfsUsnCheckpoint(0, 0, 0, 2), noticed),
                new DropboxCatchUp(PairId, generation, provenance.SourceEpoch, InitialCursor, noticed));
            LiveEffectScope[] footprint = [new LiveEffectScope(Side.Local, LocalRoot + "\\" + File.Replace('/', '\\'), false)];
            admit = Measure(() =>
            {
                try
                {
                    store.ValidateLiveCoverage(next, coverage, footprint);
                    return "admitted";
                }
                catch (LiveAdmissionStaleException error) { return "refused=" + error.Reason; }
            });
        }
        Console.WriteLine($"b198-member-cost shape={shape} members={members} member-rows-at-path={rows} " +
            $"in-use-check(ValidateLiveCoverage): {admit} | projection(ReadWork): {project}");
    }

    // B-198: a held member whose stored subject also has an item with no file id (B-195's shape). The in-use check
    // (InUseBlocks) and the source-less projection (ProjectIssueWork) decide on the valid item exactly as with a
    // valid-only member, and nothing throws. own: the next notice is the held file itself, else another file id.
    private static void InUseMemberWithUnknownItem(string database, bool mixed, bool own)
    {
        var attempt = Guid.NewGuid();
        using var store = StateStore.Open(database, (id, _, _, _, _) => id == attempt);
        var provenance = Install(store);
        CompletePrerequisite(database);
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var volume = Pair(PairId).Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var generation = provenance.PairGeneration;
        var name = $" (mixed={mixed}, own={own})";
        store.AcquireJournalPage(volume, null, new JournalPosition(0, 1),
            [(PairId, generation, local.Epoch, local.Revision, false)], []);
        IssueItem[] valid = [new IssueItem(Side.Local, File, Identity)];
        var held = store.RecordInUseFailure(PairId, generation, local.Epoch, File,
            new IssueSubject(mixed ? [.. valid, new IssueItem(Side.Local, OtherFile, null!)] : valid, null),
            TextKey.FailLocalInUse, null, ItemRetryDispatch.OrdinaryItem, null, null, attempt);
        store.EndWorkPass(PairId);
        WorkSelection SourceLess(long work, string? identity, string path) =>
            new(PairId, generation, local.Epoch, Side.Local, volume, identity, PathNames.Key(path), work, work);
        Check.Require(store.ReadWork(SourceLess(held.Token.WorkId, Identity, File)).Single().Notice is
            { Side: Side.Local, Path: File, Id: Identity, Kind: ChangeKind.Changed },
            "B-198: the source-less work projects its valid member item" + name);
        var noticed = store.AcquireJournalPage(volume, new JournalPosition(0, 1), new JournalPosition(0, 2),
            [(PairId, generation, local.Epoch, local.Revision, false)],
            [(PairId, Notice(Side.Local, File, own ? Identity : OtherIdentity))])!.Value;
        var next = store.SelectWork(PairId, generation, new PathScope(store.Pair(PairId)!, store.Pairs())) ??
            throw new InvalidOperationException(ExpectedFailure);
        Check.Require(store.BeginSelection(next) is not null, "Selection setup must be admitted.");
        LiveAdmissionStaleException? refused = null;
        try
        {
            store.ValidateLiveCoverage(next, store.CaptureLiveCoverage(next,
                    new LocalCatchUp(PairId, generation, local.Epoch, new NtfsUsnCheckpoint(0, 0, 0, 2), noticed),
                    new DropboxCatchUp(PairId, generation, provenance.SourceEpoch, InitialCursor, noticed)),
                [new LiveEffectScope(Side.Local, LocalRoot + "\\" + File.Replace('/', '\\'), false)]);
        }
        catch (LiveAdmissionStaleException error) { refused = error; }
        Check.Require((refused is null) == own,
            "B-198: the held file's own notice is admitted and another file id at its path refused while held" + name +
            ": " + refused?.Reason);
        if (!mixed) return;
        // A member whose only item has no file id: the projection still reads its side and path.
        const string Unknown = File + ".unknown";
        var unknown = store.RecordInUseFailure(PairId, generation, local.Epoch, Unknown,
            new IssueSubject([new IssueItem(Side.Local, Unknown, null!)], null), TextKey.FailLocalInUse, null,
            ItemRetryDispatch.LocalBranch, null, null, attempt);
        Check.Require(store.ReadWork(SourceLess(unknown.Token.WorkId, null, Unknown)).Single().Notice is
            { Path: Unknown, Id: null }, "B-198: a member item with no file id projects its path and never throws" + name);
    }

    // B-177: an in-use file replaced under the same path by a new file id while its old episode is live.
    private static void ReplacedInUseFile(string database)
    {
        var attempt = Guid.NewGuid();
        using var store = StateStore.Open(database, (id, _, _, _, _) => id == attempt);
        var provenance = Install(store);
        CompletePrerequisite(database);
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var volume = Pair(PairId).Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var generation = provenance.PairGeneration;
        var scope = new PathScope(store.Pair(PairId)!, store.Pairs());
        store.AcquireJournalPage(volume, null, new JournalPosition(0, 1),
            [(PairId, generation, local.Epoch, local.Revision, false)], []);
        var old = store.RecordInUseFailure(PairId, generation, local.Epoch, File,
            new IssueSubject([new IssueItem(Side.Local, File, Identity)], null), TextKey.FailLocalInUse, null,
            ItemRetryDispatch.OrdinaryItem, null, null, attempt);
        store.EndWorkPass(PairId);
        var noticed = store.AcquireJournalPage(volume, new JournalPosition(0, 1), new JournalPosition(0, 2),
            [(PairId, generation, local.Epoch, local.Revision, false)], [(PairId, Notice(Side.Local, File, OtherIdentity))])!.Value;
        var replacing = store.SelectWork(PairId, generation, scope) ?? throw new InvalidOperationException(ExpectedFailure);
        Check.Require(store.BeginSelection(replacing) is not null, "Selection setup must be admitted.");
        var subject = new IssueSubject([new IssueItem(Side.Local, File, OtherIdentity)], null);
        Exception? escaped = null;
        try
        {
            // The live order: capture, then the still-locked replacement reports its in-use failure.
            var captured = store.ReadItemRetryForSubject(PairId, generation, File, subject);
            store.RecordInUseFailure(PairId, generation, local.Epoch, File, subject,
                TextKey.FailLocalInUse, replacing, ItemRetryDispatch.OrdinaryItem, null, captured?.Token, attempt);
            escaped = new InvalidOperationException(ExpectedFailure);
        }
        catch (LiveAdmissionStaleException) { }
        catch (Exception error) { escaped = error; }
        Check.Require(escaped is null,
            "B-177: a locked file replaced under its path is refused as a changed selection, never an exit 6: " +
            escaped?.GetType().Name);
        var issue = store.Issues(PairId).Single();
        Check.Require(issue.Id == old.Token.IssueId &&
            store.ReadItemRetryState(PairId, [issue.Id])[issue.Id]?.Token.EpisodeId == old.Token.EpisodeId,
            "B-177: a capture on stale evidence never retires the held episode (INT-B177-1)");
        var footprint = new[] { new LiveEffectScope(Side.Local, LocalRoot + "\\" + File.Replace('/', '\\'), false) };
        LiveAdmissionStaleException? Admit()
        {
            try
            {
                store.ValidateLiveCoverage(replacing, store.CaptureLiveCoverage(replacing,
                    new LocalCatchUp(PairId, generation, local.Epoch, new NtfsUsnCheckpoint(0, 0, 0, 2), noticed),
                    new DropboxCatchUp(PairId, generation, provenance.SourceEpoch, InitialCursor, noticed)), footprint);
                return null;
            }
            catch (LiveAdmissionStaleException error) { return error; }
        }
        Check.Require(Admit() is not null && store.ReadItemRetryState(PairId, [old.Token.IssueId])[old.Token.IssueId] is not null,
            "B-177: live admission never retires on a notice; it refuses while the replaced file's episode is held");
        // The episode is read first, then the path's fresh Local read shows the new file id.
        store.RetireReplacedItemRetry(store.ReadReplacedItemRetry(PairId, generation, File, OtherIdentity).Single(),
            File, OtherIdentity);
        var refused = Admit();
        Check.Require(refused is null && store.ReadItemRetryState(PairId, [old.Token.IssueId])[old.Token.IssueId] is null,
            "B-177: a fresh Local read of another file id ends the replaced file's episode and admits the new file: " +
            refused?.Reason);
        // The still-locked replacement now keeps its own retry; the next pass, once unlocked, syncs it.
        var held = store.RecordInUseFailure(PairId, generation, local.Epoch, File, subject,
            TextKey.FailLocalInUse, replacing, ItemRetryDispatch.OrdinaryItem, null, null, attempt);
        store.EndWorkPass(PairId);
        var through = store.AcquireJournalPage(volume, new JournalPosition(0, 2), new JournalPosition(0, 3),
            [(PairId, generation, local.Epoch, local.Revision, false)], [(PairId, Notice(Side.Local, File, OtherIdentity))])!.Value;
        var unlocked = store.SelectWork(PairId, generation, scope) ?? throw new InvalidOperationException(ExpectedFailure);
        Check.Require(store.BeginSelection(unlocked) is not null, "Selection setup must be admitted.");
        store.ValidateLiveCoverage(unlocked, store.CaptureLiveCoverage(unlocked,
            new LocalCatchUp(PairId, generation, local.Epoch, new NtfsUsnCheckpoint(0, 0, 0, 3), through),
            new DropboxCatchUp(PairId, generation, provenance.SourceEpoch, InitialCursor, through)), footprint);
        var current = store.ReadItemRetryForSubject(PairId, generation, File, subject) ??
            throw new InvalidOperationException(ExpectedFailure);
        store.CompleteItemAttempt(current, attempt, StateChange.Empty with
        {
            Close = [(File, new[] { IssueKind.Failure })],
            Record = [new SyncedEntry(PathNames.Key(File), File, PathNames.DropboxKey(File), File,
                false, OtherIdentity, 0, 0, OtherIdentity, string.Empty)]
        }, null, null);
        Check.Require(current.Token.EpisodeId == held.Token.EpisodeId && store.Issues(PairId).Count == 0 &&
            store.SyncedById(PairId, Side.Local, OtherIdentity) is not null,
            "B-177 (a): the replacement syncs on the next pass and no waiting episode or notice remains");
        // The new file is decided against the saved baseline as an ordinary Local change.
        var baseline = new SyncedEntry(PathNames.Key(File), File, PathNames.DropboxKey(File), File, false, Identity,
            1, 0, "remote", "saved");
        ActionKind Decided(string dropboxHash) => Massdex.SyncShark.Engine.Planner.Decide.For(new Row(baseline.LocalKey,
                new Entry(File, false, 2, DateTimeOffset.UnixEpoch, OtherIdentity, "", "", null),
                new Entry(File, false, 3, DateTimeOffset.UnixEpoch, "remote", "rev", dropboxHash, null), baseline, [], null,
                new RowContext(false, false, false, null, false, false, false, false)),
            FirstRunPolicy.NewestWins, DateTimeOffset.UnixEpoch, false).Kind;
        Check.Require(Decided("saved") == ActionKind.Upload,
            "B-177 (a): a replaced file with an unchanged Dropbox side uploads");
        Check.Require(Decided("changed") == ActionKind.KeepBoth,
            "B-177 (b): a replaced file whose Dropbox side also changed keeps both versions; nothing is overwritten");
    }

    // B-195 R2 (C38), built explicitly rather than by timing: the old file's held selection keeps its member at the
    // path while the replacement, still held, holds its own wait there. That shape refused every later attempt.
    // heldPasses > 0 (ReplacedRetirementCosts) repeats the still-held pass and counts its store work (R3).
    // vanished (B-195b): the path is empty when the stale member is released, so the fresh read finds no file (null).
    private static void ReplacedWithStaleMember(string database, int heldPasses = 0, bool vanished = false)
    {
        var attempt = Guid.NewGuid();
        using var store = StateStore.Open(database, (id, _, _, _, _) => id == attempt);
        var provenance = Install(store);
        CompletePrerequisite(database);
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var volume = Pair(PairId).Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var generation = provenance.PairGeneration;
        var scope = new PathScope(store.Pair(PairId)!, store.Pairs());
        var footprint = new[] { new LiveEffectScope(Side.Local, LocalRoot + "\\" + File.Replace('/', '\\'), false) };
        var page = 0UL;
        long Arrive(string identity) => store.AcquireJournalPage(volume, page == 0 ? (JournalPosition?)null : new JournalPosition(0, page),
            new JournalPosition(0, ++page), [(PairId, generation, local.Epoch, local.Revision, false)],
            [(PairId, Notice(Side.Local, File, identity))])!.Value;
        WorkSelection Begin()
        {
            var selection = store.SelectWork(PairId, generation, scope) ?? throw new InvalidOperationException(ExpectedFailure);
            Check.Require(store.BeginSelection(selection) is not null, "Selection setup must be admitted.");
            return selection;
        }
        LiveAdmissionStaleException? Admit(WorkSelection selection, long noticed)
        {
            try
            {
                store.ValidateLiveCoverage(selection, store.CaptureLiveCoverage(selection,
                    new LocalCatchUp(PairId, generation, local.Epoch, new NtfsUsnCheckpoint(0, 0, 0, (long)page), noticed),
                    new DropboxCatchUp(PairId, generation, provenance.SourceEpoch, InitialCursor, noticed)), footprint);
                return null;
            }
            catch (LiveAdmissionStaleException error) { return error; }
        }
        // The old file, held: its selection's wait and member name the old id.
        Arrive(Identity);
        var oldSelection = Begin();
        store.RecordInUseFailure(PairId, generation, local.Epoch, File,
            new IssueSubject([new IssueItem(Side.Local, File, Identity)], null), TextKey.FailLocalInUse, oldSelection,
            ItemRetryDispatch.OrdinaryItem, null, null, attempt);
        store.EndWorkPass(PairId);
        // The replacement arrives: its preparation retires the old wait, then, still held, it reports its own.
        var noticed = Arrive(OtherIdentity);
        var replacing = Begin();
        store.RetireReplacedItemRetry(store.ReadReplacedItemRetry(PairId, generation, File, OtherIdentity).Single(),
            File, OtherIdentity);
        var held = store.RecordInUseFailure(PairId, generation, local.Epoch, File,
            new IssueSubject([new IssueItem(Side.Local, File, OtherIdentity)], null), TextKey.FailLocalInUse, replacing,
            ItemRetryDispatch.OrdinaryItem, null, null, attempt);
        var stale = store.ReadReplacedItemRetry(PairId, generation, File, OtherIdentity);
        Check.Require(stale is [var both] && both.Token.EpisodeId == held.Token.EpisodeId &&
            Admit(replacing, noticed)?.WaitKey == PathNames.Key(File),
            "B-195 setup: the old id's member and the replacement's current wait coexist at the path and refuse admission " +
            "(B-195b: the refusal names the wait's Local key)");
        Check.Require(store.Issues(PairId) is [{ Kind: IssueKind.Failure, Cause: TextKey.FailLocalInUse }],
            "B-195: while held, exactly one in-use notice is retained");
        // Fixture-only read of the path's member history: the engine's own ItemEpisodesAtPath statement, row-counted.
        (long PairRow, int Rows) History()
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { DataSource = database, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            using var pairRow = connection.CreateCommand();
            pairRow.CommandText = "SELECT id FROM pair WHERE pair_id=$p;";
            pairRow.Parameters.AddWithValue("$p", PairId);
            var row = (long)pairRow.ExecuteScalar()!;
            using var members = connection.CreateCommand();
            members.CommandText = StateSchema.ItemEpisodesAtPath;
            members.Parameters.AddWithValue(StateSchema.ParamPairRow, row);
            members.Parameters.AddWithValue(StateSchema.ParamSide, (int)Side.Local);
            members.Parameters.AddWithValue(StateSchema.ParamPath, PathNames.Key(File));
            using var reader = members.ExecuteReader();
            var rows = 0;
            while (reader.Read()) rows++;
            return (row, rows);
        }
        StatementCost Seek()
        {
            using var probe = new CostProbe(database);
            return probe.Execute(StateSchema.ItemEpisodesAtPath, (StateSchema.ParamPairRow, History().PairRow),
                (StateSchema.ParamSide, (int)Side.Local), (StateSchema.ParamPath, PathNames.Key(File)));
        }
        long? heldSeek = null;
        for (var pass = 0; pass < heldPasses; pass++)
        {
            // One AutoSync pass while the replacement is still locked: preparation reads the candidates and retires
            // against the fresh id, then the content read fails again and re-raises the wait. The counts below are
            // outer store-call counts (StoreCounters), not production SQL statement counts.
            var counted = new StoreCounters(store);
            var allocated = AllocProbe.Measure(_ =>
            {
                foreach (var candidate in counted.ReadReplacedItemRetry(PairId, generation, File, OtherIdentity))
                    counted.RetireReplacedItemRetry(candidate, File, OtherIdentity);
            }, calls: 1, warmup: 0);
            store.RecordInUseFailure(PairId, generation, local.Epoch, File,
                new IssueSubject([new IssueItem(Side.Local, File, OtherIdentity)], null), TextKey.FailLocalInUse, replacing,
                ItemRetryDispatch.OrdinaryItem, null, null, attempt);
            var reads = counted.MemberCalls.GetValueOrDefault("ReadReplacedItemRetry");
            var retirements = counted.MemberCalls.GetValueOrDefault("RetireReplacedItemRetry");
            var notices = store.Issues(PairId).Count;
            var history = History().Rows;
            var seek = Seek();
            heldSeek ??= seek.VmSteps;
            Console.WriteLine($"b195-held-pass side=candidate pass={pass} allocated-bytes={allocated} outer-reads={reads} " +
                $"outer-retirements={retirements} notices={notices} member-history-rows={history} episode-seek-vm={seek.VmSteps}");
            Check.Require(counted.Commands == 2 && reads == 1 && retirements == 1 && notices == 1 && history == 2 &&
                seek.VmSteps == heldSeek && Admit(replacing, noticed) is not null,
                "B-195 R3: every still-held pass makes one outer read and one outer retirement, keeps one notice, and the " +
                "path's member history stays at the stale and current members; pass=" + pass + " seek=" + seek);
        }
        // Fixture-only rewrite of the stale member's stored subject (the member whose items name the old id).
        int SetStaleMember(IssueSubject subject)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { DataSource = database, Pooling = false }.ToString());
            connection.Open();
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE issue_member SET subject_json=$subject WHERE pair_id=$pair AND path_key=$path AND " +
                "work_id IS NOT NULL AND EXISTS(SELECT 1 FROM json_each(subject_json,'$.Value.Items') WHERE json_extract(value,'$.Id')=$old);";
            update.Parameters.AddWithValue("$subject", StateEncoding.Envelope(subject));
            update.Parameters.AddWithValue("$old", Identity);
            update.Parameters.AddWithValue("$pair", History().PairRow);
            update.Parameters.AddWithValue("$path", PathNames.Key(File));
            return update.ExecuteNonQuery();
        }
        // Mixed member (R7): the stale member also stores an item with no file id at the path. That item is skipped
        // and the old id beside it still proves the replacement.
        Check.Require(SetStaleMember(new IssueSubject(
                [new IssueItem(Side.Local, File, null!), new IssueItem(Side.Local, File, Identity)], null)) == 1,
            "B-195 R7 setup: exactly the stale member is made mixed");
        // Released: the fresh read of the replacement retires the wait the stale member pinned.
        var listed = vanished ? "" : OtherIdentity;
        var mixedCandidates = store.ReadReplacedItemRetry(PairId, generation, File, listed);
        Check.Require(mixedCandidates.Count == 1,
            "B-195 R7: a member with a no-file-id item beside the old id is still one replacement candidate; count=" +
            mixedCandidates.Count);
        store.RetireReplacedItemRetry(mixedCandidates[0], File, vanished ? null : OtherIdentity);
        // Restore the stored shape so the rest of the flow (outside C38) reads the member it recorded.
        Check.Require(SetStaleMember(new IssueSubject([new IssueItem(Side.Local, File, Identity)], null)) == 1,
            "B-195 R7 setup: the stale member is restored");
        var refused = Admit(replacing, noticed);
        Check.Require(refused is null && store.ReadReplacedItemRetry(PairId, generation, File, listed).Count == 0,
            $"B-195 (vanished {vanished}): the fresh read retires the wait pinned by the stale member and admits the " +
            "replacement: " + refused?.Reason);
        store.Commit(PairId, StateChange.Empty with
        {
            Close = [(File, new[] { IssueKind.Failure })],
            Record = [new SyncedEntry(PathNames.Key(File), File, PathNames.DropboxKey(File), File,
                false, OtherIdentity, 0, 0, OtherIdentity, string.Empty)]
        });
        Check.Require(store.CompleteWork(replacing, null), "B-195: the replacement's work completes");
        store.EndWorkPass(PairId);
        // The old file's selection, released with its wait, drains by its own selection.
        var drained = Begin();
        Check.Require(store.CompleteWork(drained, null), "B-195: the old file's selection drains");
        store.EndWorkPass(PairId);
        Check.Require(store.Issues(PairId).Count == 0 && store.SelectWork(PairId, generation, scope) is null &&
            store.SyncedById(PairId, Side.Local, OtherIdentity) is not null,
            "B-195: after convergence no notice and no stranded work remain, and the replacement is saved");
        // The probe really reads the path's member rows: they are gone after convergence.
        Check.Require(heldSeek is null || Seek().VmSteps < heldSeek,
            "B-195 R3: the episode seek probe reads the path's member rows; held=" + heldSeek);
        // A held member stored with no file id (the other legitimate member shape) never throws and never retires.
        const string Unknown = File + ".unknown";
        var unknown = store.RecordInUseFailure(PairId, generation, local.Epoch, Unknown,
            new IssueSubject([new IssueItem(Side.Local, Unknown, null!)], null), TextKey.FailLocalInUse, null,
            ItemRetryDispatch.LocalBranch, null, null, attempt);
        Check.Require(store.ReadReplacedItemRetry(PairId, generation, Unknown, OtherIdentity).Count == 0 &&
            store.ReadReplacedItemRetry(PairId, generation, Unknown, "").Count == 0 &&
            store.ReadItemRetryState(PairId, [unknown.Token.IssueId])[unknown.Token.IssueId] is not null,
            "B-195 R6: a member naming no file id is not a replacement candidate and its episode is kept");
    }

    /// <summary>B-195 R3 (projection-cost group): the C38 retirement's store work on an episode-bearing path over
    /// repeated still-held passes, on the real StateStore with deterministic member history.</summary>
    public static void ReplacedRetirementCosts() => WithStore((_, database) => ReplacedWithStaleMember(database, heldPasses: 3));

    // B-195 (cleanup): a held file whose path is now empty on both sides. A fresh read that finds no file there ends
    // its wait, as a deletion; a read of the same file, or of a file with no id, keeps it. Nothing is deleted.
    // B-195b: then a file returns there (the same id or another one), still held: it raises its own wait under the same
    // notice, and once unlocked its retry syncs it and closes the notice.
    private static void VanishedHeldFile(string database, string returned)
    {
        var attempt = Guid.NewGuid();
        using var store = StateStore.Open(database, (id, _, _, _, _) => id == attempt);
        var provenance = Install(store);
        CompletePrerequisite(database);
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var volume = Pair(PairId).Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var generation = provenance.PairGeneration;
        store.AcquireJournalPage(volume, null, new JournalPosition(0, 1),
            [(PairId, generation, local.Epoch, local.Revision, false)], []);
        var held = store.RecordInUseFailure(PairId, generation, local.Epoch, File,
            new IssueSubject([new IssueItem(Side.Local, File, Identity)], null), TextKey.FailLocalInUse, null,
            ItemRetryDispatch.OrdinaryItem, null, null, attempt);
        bool Kept() => store.ReadItemRetryState(PairId, [held.Token.IssueId])[held.Token.IssueId] is not null;
        // The row lists no Local file at the path, so the read asks for any recorded id ("").
        var candidate = store.ReadReplacedItemRetry(PairId, generation, File, "").Single();
        store.RetireReplacedItemRetry(candidate, File, Identity);
        store.RetireReplacedItemRetry(candidate, File, "");
        Check.Require(Kept(), "B-195: a fresh read of the held file itself, or of a file with no id, keeps its wait");
        store.RetireReplacedItemRetry(candidate, File, null);
        Check.Require(!Kept() && store.ReadReplacedItemRetry(PairId, generation, File, "").Count == 0 &&
            store.Issues(PairId).Count == 0,
            "B-195: a fresh read that finds no file ends the vanished file's wait and its obsolete notice closes " +
            "with the path's own work");
        var subject = new IssueSubject([new IssueItem(Side.Local, File, returned)], null);
        var again = store.RecordInUseFailure(PairId, generation, local.Epoch, File, subject, TextKey.FailLocalInUse, null,
            ItemRetryDispatch.OrdinaryItem, null, null, attempt);
        Check.Require(store.Issues(PairId) is [{ Cause: TextKey.FailLocalInUse }] &&
            store.ReadReplacedItemRetry(PairId, generation, File, returned).Count == 0 &&
            store.ReadItemRetryState(PairId, [again.Token.IssueId])[again.Token.IssueId] is not null,
            $"B-195b (returned {returned}, still held): one notice, the returned file's own wait, nothing stale at the path");
        var current = store.ReadItemRetryForSubject(PairId, generation, File, subject) ??
            throw new InvalidOperationException(ExpectedFailure);
        Check.Require(store.TryClaimItemRetry(current, attempt), "B-195b: the unlocked file's retry is claimed");
        store.CompleteItemAttempt(current, attempt, StateChange.Empty with
        {
            Close = [(File, IssueClosure.AfterEffect)],
            Record = [new SyncedEntry(PathNames.Key(File), File, PathNames.DropboxKey(File), File,
                false, returned, 0, 0, returned, string.Empty)]
        }, null, null);
        Check.Require(store.Issues(PairId).Count == 0 && store.SyncedById(PairId, Side.Local, returned) is not null,
            $"B-195b (returned {returned}): once unlocked, its retry syncs it and no wait or notice remains");
    }

    private static void OlderDirtyEpisode(string database, bool acquired)
    {
        TransferredOlderDirtyEpisode(database, acquired);
    }

    private static void TransferredOlderDirtyEpisode(string database, bool acquired)
    {
        var attempt = Guid.NewGuid();
        using var store = StateStore.Open(database, (id, _, _, _, _) => id == attempt);
        var provenance = Install(store);
        CompletePrerequisite(database);
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var volume = Pair(PairId).Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var generation = provenance.PairGeneration;
        var saved = new SyncedEntry(PathNames.Key(File), File, PathNames.DropboxKey(File), File,
            false, Identity, 1, 1, "remote", "hash");
        store.Commit(PairId, StateChange.Empty with { Record = [saved] });
        var fences = new[] { (PairId, generation, local.Epoch, local.Revision, false) };
        var first = store.AcquireJournalPage(volume, null, new JournalPosition(0, 1), fences,
            [(PairId, Notice(Side.Local, File, Identity, dirty: true) with { Usn = 4 })])!.Value;
        var initial = store.SelectWork(PairId, generation, new PathScope(store.Pair(PairId)!, store.Pairs()))!;
        initial = store.BeginSelection(initial)!;
        var latest = store.AcquireJournalPage(volume, new JournalPosition(0, 1), new JournalPosition(0, 2), fences,
            [(PairId, Notice(Side.Local, File, Identity, dirty: true) with { Usn = 9 }),
             (PairId, Notice(Side.Local, File, Identity) with { Usn = 3 })])!.Value;
        var subject = new IssueSubject([new IssueItem(Side.Local, File, Identity)], null);
        WorkSelection? acquiredOwner = null;
        if (acquired)
        {
            var ownerId = store.AcquireJournalPage(volume, new JournalPosition(0, 2), new JournalPosition(0, 3), fences,
                [(PairId, Notice(Side.Local, File, Identity))])!.Value;
            acquiredOwner = initial with { AnchorWorkId = ownerId, ThroughWorkId = ownerId };
        }
        var episode = store.RecordInUseFailure(PairId, generation, local.Epoch, File, subject,
            TextKey.FailLocalInUse, acquiredOwner, ItemRetryDispatch.OrdinaryItem, null, null, attempt);
        var after = store.AcquireJournalPage(volume, new JournalPosition(0, acquired ? 3UL : 2UL),
            new JournalPosition(0, 4), fences,
            [(PairId, Notice(Side.Local, File, Identity) with { Usn = 12 })])!.Value;
        initial = initial with { ThroughWorkId = after };
        var unrelatedId = store.AcquireJournalPage(volume, new JournalPosition(0, 4), new JournalPosition(0, 5), fences,
            [(PairId, Notice(Side.Local, OtherFile, OtherIdentity, dirty: true))])!.Value;
        var unrelated = initial with { AnchorWorkId = unrelatedId, ThroughWorkId = unrelatedId,
            Identity = OtherIdentity, Key = PathNames.Key(OtherFile) };
        var otherSubject = new IssueSubject([new IssueItem(Side.Local, OtherFile, OtherIdentity)], null);
        var otherEpisode = store.RecordInUseFailure(PairId, generation, local.Epoch, OtherFile, otherSubject,
            TextKey.FailLocalInUse, unrelated, ItemRetryDispatch.OrdinaryItem, null, null, attempt);
        var watermark = store.ReadWorkWatermark();
        var coverage = store.CaptureLiveCoverage(initial,
            new LocalCatchUp(PairId, generation, local.Epoch, new NtfsUsnCheckpoint(0, 0, 0, 5), watermark),
            new DropboxCatchUp(PairId, generation, provenance.SourceEpoch, InitialCursor, watermark));
        ItemRetryCandidate? captured = null;
        try
        {
            store.ValidateLiveCoverage(initial, coverage,
                [new LiveEffectScope(Side.Local, LocalRoot + "\\" + File.Replace('/', '\\'), false)]);
        }
        catch (LiveAdmissionStaleException error) { captured = error.NewerEpisode?.Episode; }
        Check.Require(captured?.Token.WorkId == episode.Token.WorkId,
            "B1: episode-newer refusal captures exact same-item authority without admitting the old selection");
        var changed = episode with { Token = episode.Token with { Revision = episode.Token.Revision + 1 } };
        Check.Require(!store.TransferOlderEpisodeWork(initial, changed) && store.ReadWorkWatermark() == watermark &&
            store.ReadWork(initial).Any(row => row.WorkId == first),
            "B1: changed episode token refuses transfer without retirement");
        Check.Require(store.TransferOlderEpisodeWork(initial, captured!),
            "B1: convergence starts by atomically transferring the older selected work");
        var childId = store.ReadWorkWatermark();
        var child = initial with { AnchorWorkId = childId, ThroughWorkId = childId };
        var transferred = store.ReadWork(child).Single();
        Check.Require(childId == watermark + 1 && childId > episode.Token.WorkId &&
            transferred.Before == saved && transferred.Notice is { LocalContentDirty: true, Usn: 9 } &&
            store.ReadLocalDirty(PairId, generation, local.Epoch, volume, Identity, PathNames.Key(File), null)
                .EvidenceVersion == after,
            "B1: multiple acquisitions coalesce into one later child with earliest before, dirty OR and max USN");
        var close = initial with { AnchorWorkId = after, ThroughWorkId = after };
        PendingChange? remainingClose = null;
        try { remainingClose = store.ReadWork(close).SingleOrDefault(); }
        catch (LiveAdmissionStaleException) { /* A retired anchor is absent, not an assertion bypass. */ }
        Check.Require(remainingClose?.WorkId == after &&
            store.ReadWork(unrelated).Single().WorkId == unrelatedId &&
            store.ReadItemRetryState(PairId, [otherEpisode.Token.IssueId])[otherEpisode.Token.IssueId] is not null,
            "B1: post-episode CLOSE stays independent and an unrelated member keeps its own owner");
        Check.Require(!store.TransferOlderEpisodeWork(initial, episode) && store.ReadWorkWatermark() == childId,
            "B1: repeated transfer is idempotent and creates no work or retry");
        using (var reopened = StateStore.OpenReadOnly(database)!)
        {
            Check.Require(reopened.ReadWork(child).Single() == transferred &&
                reopened.ReadItemRetryState(PairId, [episode.Token.IssueId])[episode.Token.IssueId] is not null,
                "B1: restart retains the child and exact waiting episode without authorizing upload");
        }
        if (!acquired)
        {
            // N2 (REVIEW-0388-INT-1): B1 transfer, then A1 retirement of the same source-less episode. The moved
            // child survives as ordinary pending work with its notice and before-image intact; only the exact
            // source-less episode row retires. Explicit Retry settling a source-less episode stays covered by
            // OlderDirtyEpisodeAttachment.
            store.RetireReplacedItemRetry(episode, File, null);
            Check.Require(store.ReadItemRetryState(PairId, [episode.Token.IssueId])[episode.Token.IssueId] is null &&
                store.ReadWork(child).Single() == transferred,
                "B1xA1: A1 retires the source-less episode but keeps the transferred child (notice and before-image)");
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + database + ";Pooling=False"))
            {
                connection.Open();
                using var gone = connection.CreateCommand();
                gone.CommandText = "SELECT count(*) FROM change_work WHERE work_id=$owner;";
                gone.Parameters.AddWithValue("$owner", episode.Token.WorkId);
                Check.Require((long)gone.ExecuteScalar()! == 0,
                    "B1xA1: only the exact source-less episode row is gone");
            }
            store.EndWorkPass(PairId);
            // The child is now a top-level ordinary row (its parent episode row is gone). It stays readable as
            // pending work across the pass boundary; it need not be the next selection's anchor because the fixture
            // also holds the post-episode CLOSE and the unrelated member.
            Check.Require(store.SelectWork(PairId, generation, new PathScope(store.Pair(PairId)!, store.Pairs())) is not null &&
                store.ReadWork(child).Single() == transferred,
                "B1xA1: the transferred child stays ordinary pending work across the pass boundary (notice and before intact)");
            return;
        }
        store.CompleteItemAttempt(episode, attempt, StateChange.Empty with { Close = [(File, IssueClosure.AfterEffect)] }, null, null);
        // Explicit Retry settles its episode owner itself, whether acquired or source-less.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + database + ";Pooling=False"))
        {
            connection.Open();
            using var remaining = connection.CreateCommand();
            remaining.CommandText = "SELECT count(*) FROM change_work WHERE work_id=$owner;";
            remaining.Parameters.AddWithValue("$owner", episode.Token.WorkId);
            Check.Require((long)remaining.ExecuteScalar()! == 0,
                "B1: explicit Retry settles its acquired or source-less episode owner before ordinary completion");
        }
        store.CompleteWork(close, null);
        store.EndWorkPass(PairId);
        Check.Require(store.ReadLocalDirty(PairId, generation, local.Epoch, volume, Identity,
            PathNames.Key(File), null).EvidenceVersion == latest && store.ReadWork(child).Single().Before == saved,
            "B1: transferred child retains maximum original acquisition, not its later registration id");
        Check.Require(store.CompleteWork(child, null) && store.IssueById(PairId, episode.Token.IssueId) is null,
            "B1: the episode's explicit Retry releases its child and closes its notice");
    }

    private static void EpisodeTransferGuard(string database, string scenario)
    {
        var attempt = Guid.NewGuid();
        using var store = StateStore.Open(database, (id, _, _, _, _) => id == attempt);
        var provenance = Install(store);
        CompletePrerequisite(database);
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var volume = Pair(PairId).Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var fences = new[] { (PairId, provenance.PairGeneration, local.Epoch, local.Revision, false) };
        var last = store.AcquireJournalPage(volume, null, new JournalPosition(0, 1), fences,
            [(PairId, Notice(Side.Local, File, Identity, dirty: true) with { Usn = 4 }),
             (PairId, Notice(Side.Local, scenario == "HardLink" ? OtherFile : File, Identity, dirty: true) with { Usn = 9 })])!.Value;
        var selection = store.SelectWork(PairId, provenance.PairGeneration, new PathScope(store.Pair(PairId)!, store.Pairs()))!;
        var episode = store.RecordInUseFailure(PairId, provenance.PairGeneration, local.Epoch, File,
            new IssueSubject([new IssueItem(Side.Local, File, Identity)], null), TextKey.FailLocalInUse,
            null, ItemRetryDispatch.OrdinaryItem, null, null, attempt);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + database + ";Pooling=False");
        connection.Open();
        void Execute(string sql, params (string Name, object? Value)[] arguments)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in arguments) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        string Snapshot()
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM change_work ORDER BY work_id;";
            using var reader = command.ExecuteReader();
            var rows = new List<object[]>();
            while (reader.Read())
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add(values);
            }
            return System.Text.Json.JsonSerializer.Serialize(rows);
        }
        var before = new SyncedEntry(PathNames.Key(File), File, PathNames.DropboxKey(File), File,
            false, Identity, 1, 1, "remote", "earlier");
        var observed = new Entry(File, false, 2, DateTimeOffset.UnixEpoch, Identity, "", "", null);
        long? child = null;
        if (scenario is "Rollback" or "ExistingChild")
        {
            store.AttachIssueWork(selection, store.IssueById(PairId, episode.Token.IssueId)!,
                [(Notice(Side.Local, File, Identity, dirty: true) with { Usn = 12, Observed = observed },
                    false, before with { DropboxContentHash = "later" })]);
            child = store.ReadWorkWatermark();
            // Model a child acquired after the episode, rather than debt copied from the old anchor.
            Execute("UPDATE change_work SET acquisition_work_id=work_id WHERE work_id=$child;", ("$child", child));
        }
        if (scenario == "ExistingChild")
            Execute("UPDATE change_work SET before_json=$before WHERE work_id=$first;",
                ("$before", StateEncoding.Envelope(before)), ("$first", selection.AnchorWorkId));
        if (scenario == "Rollback")
        {
            // Inject a custody change after the first retirement, so the next representation must roll everything back.
            Execute("CREATE TRIGGER transfer_fault AFTER DELETE ON change_work WHEN OLD.work_id=" + selection.AnchorWorkId +
                " BEGIN UPDATE change_work SET old_path_key='incompatible' WHERE work_id=" + child + "; END;");
        }
        if (scenario == "ForeignMember")
        {
            var other = store.RecordInUseFailure(PairId, provenance.PairGeneration, local.Epoch, OtherFile,
                new IssueSubject([new IssueItem(Side.Local, OtherFile, OtherIdentity)], null), TextKey.FailLocalInUse,
                null, ItemRetryDispatch.OrdinaryItem, null, null, attempt);
            Execute("INSERT INTO issue_member(issue_id,pair_id,work_id,side,path_key,identity,subject_json) " +
                "SELECT issue_id,pair_id,$work,side,path_key,identity,subject_json FROM issue_member WHERE issue_id=$issue;",
                ("$work", last), ("$issue", other.Token.IssueId));
        }
        if (scenario is "PendingOverlap" or "PendingElsewhere")
        {
            var path = scenario == "PendingOverlap" ? File : OtherFile;
            var pending = new PendingEffect(1, "pending", provenance.PairGeneration, PendingKind.LocalDelete,
                PendingPhase.Armed, path, new RemovalProof(observed with { Path = path }, volume, "hash"),
                null, null, null, null, null, false, null);
            store.ArmPending(PairId, pending);
        }
        if (scenario == "Rename")
            Execute("UPDATE change_work SET rename_continuation_json=$rename WHERE work_id=$first;",
                ("$rename", StateEncoding.Envelope(new RenameContinuation(before, null))), ("$first", selection.AnchorWorkId));
        var original = Snapshot();
        var watermark = store.ReadWorkWatermark();
        var transferred = store.TransferOlderEpisodeWork(selection, episode);
        if (scenario is "Rollback" or "PendingOverlap" or "Rename")
        {
            Check.Require(!transferred && store.ReadWorkWatermark() == watermark && Snapshot() == original,
                "B1: " + scenario + " preserves every original byte and watermark with no new episode child");
            return;
        }
        Check.Require(transferred, "B1: " + scenario + " does not block eligible item debt");
        using var remaining = connection.CreateCommand();
        remaining.CommandText = "SELECT work_id FROM change_work WHERE work_id IN ($first,$last) ORDER BY work_id;";
        remaining.Parameters.AddWithValue("$first", selection.AnchorWorkId);
        remaining.Parameters.AddWithValue("$last", last);
        using (var reader = remaining.ExecuteReader())
        {
            var kept = new List<long>();
            while (reader.Read()) kept.Add(reader.GetInt64(0));
            Check.Require(kept.SequenceEqual(scenario is "ForeignMember" or "HardLink" ? new[] { last } : []),
                "B1: " + scenario + " retains foreign custody and hard links while retiring only represented rows");
        }
        if (scenario == "ExistingChild")
        {
            var stored = store.ReadWork(selection with { AnchorWorkId = child!.Value, ThroughWorkId = child.Value }).Single();
            Check.Require(stored.Before == before && stored.Notice.Observed == observed && stored.Notice.Usn == 12,
                "B1: pre-episode before-image replaces later child before while chronological notice retains later observation");
        }
    }

    private static void OlderDirtyEpisodeAttachment(string database, bool acquired)
    {
        var attempt = Guid.NewGuid();
        using var store = StateStore.Open(database, (id, _, _, _, _) => id == attempt);
        var provenance = Install(store);
        CompletePrerequisite(database);
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var volume = Pair(PairId).Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var generation = provenance.PairGeneration;
        store.AcquireJournalPage(volume, null, new JournalPosition(0, 1),
            [(PairId, generation, local.Epoch, local.Revision, false)],
            acquired ? [(PairId, Notice(Side.Local, File, Identity)),
                (PairId, Notice(Side.Local, File, Identity, dirty: true))] :
                [(PairId, Notice(Side.Local, File, Identity, dirty: true))]);
        var initial = store.SelectWork(PairId, generation, new PathScope(store.Pair(PairId)!, store.Pairs()))!;
        var subject = new IssueSubject([new IssueItem(Side.Local, File, Identity)], null);
        var episode = store.RecordInUseFailure(PairId, generation, local.Epoch, File, subject,
            TextKey.FailLocalInUse, acquired ? initial : null, ItemRetryDispatch.OrdinaryItem, null, null, attempt);
        var owner = initial with { AnchorWorkId = episode.Token.WorkId, ThroughWorkId = episode.Token.WorkId };
        store.AttachIssueWork(initial, store.IssueById(PairId, episode.Token.IssueId)!,
            [(Notice(Side.Local, File, Identity, dirty: true), false, null)]);
        var child = store.ReadWorkWatermark();
        Check.Require(child > episode.Token.WorkId && store.ReadLocalDirty(PairId, generation, local.Epoch,
            volume, Identity, PathNames.Key(File), null).Dirty,
            "same-anchor and older-than-episode dirty observations create a later child, never a backwards parent");
        store.CompleteWork(initial, null);
        if (acquired)
        {
            var dirtyNotice = store.SelectWork(PairId, generation, new PathScope(store.Pair(PairId)!, store.Pairs()))!;
            Check.Require(store.BeginSelection(dirtyNotice) is not null, "Selection setup must be admitted.");
            var coverage = store.CaptureLiveCoverage(dirtyNotice,
                new LocalCatchUp(PairId, generation, local.Epoch, new NtfsUsnCheckpoint(0, 0, 0, 1), child),
                new DropboxCatchUp(PairId, generation, provenance.SourceEpoch, InitialCursor, child));
            store.ValidateLiveCoverage(dirtyNotice, coverage,
                [new LiveEffectScope(Side.Local, LocalRoot + "\\" + File.Replace('/', '\\'), false)]);
            store.AttachIssueWork(dirtyNotice, store.IssueById(PairId, episode.Token.IssueId)!,
                [(Notice(Side.Local, File, Identity, dirty: true), false, null)]);
            Check.Require(store.ReadWorkWatermark() == child && store.CompleteWork(dirtyNotice, null),
                "the later member refreshes the one dirty child after the clean anchor failed");
        }
        var change = acquired ? StateChange.Empty with { Close = [(File, new[] { IssueKind.Failure })] } :
            StateChange.Empty with { Raise = [(IssueKind.Failure, TextKey.FailChanged, File, subject)] };
        store.CompleteItemAttempt(episode, attempt, change, null, null);
        store.EndWorkPass(PairId);
        if (!acquired) Throws<LiveAdmissionStaleException>(() => store.ReadWork(owner).ToArray());
        var released = store.SelectWork(PairId, generation, new PathScope(store.Pair(PairId)!, store.Pairs())) ?? throw new InvalidOperationException(ExpectedFailure);
        Check.Require(released.AnchorWorkId == child && store.ReadWork(released).Single().Notice.LocalContentDirty,
            "ending the episode releases its dirty child without requiring a Repair");
        if (!acquired)
        {
            var ordinary = store.Issues(PairId).Single();
            store.AttachIssueWork(released, ordinary,
                [(Notice(Side.Local, File, Identity, dirty: true), false, null)]);
            Check.Require(store.CompleteWork(released, null),
                "a non-access failure registers acquired residual work, never a source-less NULL notice");
        }
    }

    private static void ItemResponsibility()
    {
        foreach (var cause in new[] { TextKey.FailLocalInUse, TextKey.FailLocalInUseOrAccessDenied })
        {
            var directory = Path.Combine(Path.GetTempPath(), FolderPrefix + Guid.NewGuid().ToString(GuidFormat));
            Directory.CreateDirectory(directory);
            try
            {
                var attempt = Guid.NewGuid();
                var subject = new IssueSubject([new IssueItem(Side.Local, File, Identity)], null);
                ItemRetryToken? authority = null;
                long generation = 0;
                using var store = StateStore.Open(Path.Combine(directory, Database),
                    (id, pair, current, actualSubject, token) => id == attempt && pair == PairId &&
                        current == generation && actualSubject.HasSameItems(subject) &&
                        (authority is null ? token is null : token is not null &&
                            token with { Subject = authority.Subject } == authority &&
                            token.Subject.HasSameItems(authority.Subject)));
                store.SavePair(Pair(PairId));
                generation = store.ReadPairExecution(PairId)!.Value.Generation;
                var local = store.ReadSource(PairId, Side.Local)!.Value;
                var watermark = store.ReadWorkWatermark();
                Throws<LiveAdmissionStaleException>(() => store.RecordInUseFailure(PairId, generation,
                    local.Epoch, File, subject, cause, null, ItemRetryDispatch.OrdinaryItem,
                    null, null, Guid.NewGuid()));
                Check.Require(store.Issues(PairId).Count == 0 && store.ReadWorkWatermark() == watermark,
                    ResidualOwnership);
                Throws<InvalidOperationException>(() => store.RecordInUseFailure(PairId, generation,
                    local.Epoch + 1, File, subject, cause, null, ItemRetryDispatch.OrdinaryItem,
                    null, null, attempt));
                Check.Require(store.Issues(PairId).Count == 0 && store.ReadWorkWatermark() == watermark,
                    AtomicPage);
                var candidate = store.RecordInUseFailure(PairId, generation, local.Epoch, File,
                    subject, cause, null, ItemRetryDispatch.OrdinaryItem, null, null, attempt);
                authority = candidate.Token;
                Check.Require(store.ReadItemRetryState(PairId, [candidate.Token.IssueId])
                    [candidate.Token.IssueId]?.Token.WorkId == candidate.Token.WorkId &&
                    !PendingWork.ByIdentity(store, Side.Local, Volume, Identity).Any(), ResidualOwnership);
                var evidence = store.ReadLocalDirty(PairId, generation, local.Epoch, Volume, Identity,
                    PathNames.Key(File), null);
                Check.Require(!evidence.Dirty && evidence.EvidenceVersion == 0, DirtyEvidence);
                store.Commit(PairId, StateChange.Empty with { Close = [(File, [IssueKind.Failure])] });
                Check.Require(store.IssueById(PairId, candidate.Token.IssueId) is not null, ResidualOwnership);
                Throws<LiveAdmissionStaleException>(() => store.TryClaimItemRetry(candidate with
                {
                    Token = candidate.Token with { Revision = candidate.Token.Revision + 1 }
                }, attempt));
                Check.Require(store.TryClaimItemRetry(candidate, attempt), ResidualOwnership);
                var failure = StateChange.Empty with { Raise = [(IssueKind.Failure, cause, File, subject)] };
                store.CompleteItemAttempt(candidate, attempt, failure, null, null);
                var updated = store.ReadItemRetryState(PairId, [candidate.Token.IssueId])[candidate.Token.IssueId]!;
                Check.Require(updated.Token.WorkId == candidate.Token.WorkId &&
                    updated.Token.EpisodeId == candidate.Token.EpisodeId &&
                    updated.Token.Revision == candidate.Token.Revision + 1, ResidualOwnership);
                Throws<LiveAdmissionStaleException>(() => store.CompleteItemAttempt(candidate, attempt, failure, null, null));
                authority = updated.Token;
                var provenance = Install(store);
                var through = Acquire(store, provenance, NextCursor,
                    [Notice(Side.Dropbox, Renamed, Identity, kind: ChangeKind.Renamed, oldPath: File)]);
                var selection = Selection(provenance, through, through, path: Renamed);
                var before = new SyncedEntry(PathNames.Key(File), File, PathNames.DropboxKey(File), File,
                    false, Identity, 0, 0, Identity, string.Empty);
                var after = before with { LocalKey = PathNames.Key(Renamed), LocalPath = Renamed,
                    DropboxKey = PathNames.DropboxKey(Renamed), DropboxPath = Renamed };
                store.Commit(PairId, StateChange.Empty with { Record = [before] });
                Check.Require(store.BeginSelection(selection) is not null, "Selection setup must be admitted.");
                var completed = StateChange.Empty with { Close = [(File, [IssueKind.Failure])],
                    Renames = [new RenameApplied(before, after, null, null, null)] };
                store.Commit(PairId, StateChange.Empty with { Record = [before with { LocalId = OtherIdentity }] });
                Throws<LiveAdmissionStaleException>(() => store.CompleteItemAttempt(updated, attempt, completed, null, selection));
                Check.Require(store.IssueById(PairId, candidate.Token.IssueId) is not null &&
                    store.ReadWork(selection).Single().Before == before,
                    "a refused retry keeps both episode and selected preimage");
                store.Commit(PairId, StateChange.Empty with { Record = [before] });
                store.CompleteItemAttempt(updated, attempt, completed, null, selection);
                Check.Require(store.ReadWork(selection).Single().Before == after &&
                    store.SyncedByKey(PairId, Side.Local, after.LocalKey) == after,
                    "retry completion advances the rename preimage in its transaction");
                Check.Require(store.IssueById(PairId, candidate.Token.IssueId) is null &&
                    store.ReadSource(PairId, Side.Local) == local, GapResponsibility);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static void AcquiredRenameRetry()
    {
        var directory = Path.Combine(Path.GetTempPath(), FolderPrefix + Guid.NewGuid().ToString(GuidFormat));
        Directory.CreateDirectory(directory);
        try
        {
            var attempt = Guid.NewGuid();
            var database = Path.Combine(directory, Database);
            using var store = StateStore.Open(database, (id, _, _, _, _) => id == attempt);
            store.SavePair(Pair(PairId));
            var provenance = Install(store);
            CompletePrerequisite(database);
            var local = store.ReadSource(PairId, Side.Local)!.Value;
            var volume = Pair(PairId).Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var through = store.AcquireJournalPage(volume, null, new JournalPosition(0, 0),
                [(PairId, provenance.PairGeneration, local.Epoch, local.Revision, false)],
                [(PairId, Notice(Side.Local, Renamed, Identity, dirty: true, kind: ChangeKind.Renamed, oldPath: File))])!.Value;
            var selection = new WorkSelection(PairId, provenance.PairGeneration, local.Epoch, Side.Local,
                volume, Identity, PathNames.Key(Renamed), through, through);
            var before = new SyncedEntry(PathNames.Key(File), File, PathNames.DropboxKey(File), File,
                false, Identity, 0, 0, Identity, string.Empty);
            var after = before with { LocalKey = PathNames.Key(Renamed), LocalPath = Renamed,
                DropboxKey = PathNames.DropboxKey(Renamed), DropboxPath = Renamed };
            store.Commit(PairId, StateChange.Empty with { Record = [before] });
            Check.Require(store.BeginSelection(selection) is not null, "Selection setup must be admitted.");
            var subject = new IssueSubject([new IssueItem(Side.Local, Renamed, Identity)], null);
            var retry = store.RecordInUseFailure(PairId, provenance.PairGeneration, local.Epoch, Renamed,
                subject, TextKey.FailLocalInUse, selection, ItemRetryDispatch.OrdinaryItem, null, null, attempt);
            Check.Require(retry.Token.WorkId == selection.AnchorWorkId, "retry owns the acquired selection anchor");
            Check.Require(store.TryClaimItemRetry(retry, attempt), "acquired rename retry is claimed");
            store.CompleteItemAttempt(retry, attempt, StateChange.Empty with
            {
                Renames = [new RenameApplied(before, after, null, null, null)],
                Close = [(Renamed, IssueClosure.AfterEffect)]
            }, null, selection);
            Check.Require(store.ReadWork(selection).Single().Before == after,
                "retry completion leaves the advanced acquired anchor for its follow-on");
            var coverage = store.CaptureLiveCoverage(selection,
                new LocalCatchUp(PairId, provenance.PairGeneration, local.Epoch, new NtfsUsnCheckpoint(0, 0, 0, 0), through),
                new DropboxCatchUp(PairId, provenance.PairGeneration, provenance.SourceEpoch, InitialCursor, through));
            store.ValidateLiveCoverage(selection, coverage,
                [new LiveEffectScope(Side.Local, Path.Combine(LocalRoot, Renamed), false)]);
            Throws<LiveAdmissionStaleException>(() => store.CompleteWork(selection with { SourceEpoch = local.Epoch + 1 }, null));
            Check.Require(store.ReadWork(selection).Single().Before == after,
                "a refused selection completion preserves unfinished rename work");
            store.CompleteWork(selection, null);
            Check.Require(!PendingWork.ByIdentity(store, Side.Local, volume, Identity).Any(),
                "only the selection completion retires acquired rename work");
            Throws<LiveAdmissionStaleException>(() => store.CompleteWork(selection, null));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void ReopenCommittedCursor()
    {
        var directory = Path.Combine(Path.GetTempPath(), FolderPrefix + Guid.NewGuid().ToString(GuidFormat));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, Database);
            long watermark;
            SourceProvenance provenance;
            using (var store = StateStore.Open(path))
            {
                store.SavePair(Pair(PairId));
                provenance = Install(store);
                watermark = Acquire(store, provenance, NextCursor, [Notice(Side.Dropbox, File, Identity)]);
            }
            using var reopened = StateStore.Open(path);
            Check.Require(reopened.ReadSource(PairId, Side.Dropbox)!.Value.Cursor == NextCursor &&
                reopened.ReadWorkWatermark() == watermark &&
                reopened.ReadWork(Selection(provenance, watermark, watermark)).Count() == 1, AtomicPage);
            Throws<LiveAdmissionStaleException>(() => reopened.AcquireDropboxPage(PairId,
                provenance.PairGeneration, provenance.SourceEpoch,
                reopened.ReadSource(PairId, Side.Dropbox)!.Value.Revision, provenance,
                InitialCursor, [Notice(Side.Dropbox, File, Identity)], NextCursor));
            Check.Require(reopened.ReadWorkWatermark() == watermark, AtomicPage);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}

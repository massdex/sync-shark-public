using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Independent durable acquisition and finite-selection contract checks through the real store.</summary>
public static class ChangeTableChecks
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
        WithStore(LivePeerClaimSurvives);
        WithStore(PageRollback);
        WithStore(store => ReturningRelease(store));
        using (var memory = new InMemoryStateStore()) ReturningRelease(memory);
        RepairHeldSelection();
        WithStore(OrderedAndFinite);
        SeparateWorkProbeCounts();
        WithStore(JournalRollbackAndGap);
        WithStore(LostIntervalCases);
        WithStore(DirtyOutsideSelection);
        WithStore(DirtyMemoryTransitions);
        WithStore(DirtyTransfersOnConfiguration);
        WithStore(DirtyDroppedOnDomainChange);
        WithStore(DirtyDroppedOnReset);
        WithStore(ResidualRegistration);
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
            WithStore((_, database) => OlderDirtyEpisode(database, acquired));
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
                store.BeginSelection(selection!);
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
                    new DropboxCatchUp(PairId, provenance.PairGeneration, provenance.SourceEpoch, NextCursor, through));
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

    private static SourceProvenance Install(StateStore store)
    {
        var generation = store.ReadPairExecution(PairId)!.Value.Generation;
        var source = store.ReadSource(PairId, Side.Dropbox)!.Value;
        var provenance = new SourceProvenance(Account, Domain, Root, RemoteRoot,
            true, true, true, true, generation, source.Epoch);
        store.AdvanceSource(PairId, Side.Dropbox, generation, source.Epoch, source.Revision,
            source.Provenance, source.Cursor, source.Epoch, source.Revision,
            provenance, InitialCursor, source.Obligation);
        return provenance;
    }

    private static long Acquire(StateStore store, SourceProvenance provenance, string cursor,
        IEnumerable<ChangeNotice> notices)
    {
        var source = store.ReadSource(PairId, Side.Dropbox)!.Value;
        return store.AcquireDropboxPage(PairId, provenance.PairGeneration, source.Epoch,
            source.Revision, provenance, source.Cursor, notices, cursor);
    }

    private static ChangeNotice Notice(Side side, string path, string? identity,
        bool dirty = false, ChangeKind kind = ChangeKind.Changed, string? oldPath = null) =>
        new(side, kind, path, oldPath, identity, null, dirty, false);

    private static WorkSelection Selection(SourceProvenance provenance, long anchor, long through,
        string? identity = Identity, string path = File) => new(PairId, provenance.PairGeneration,
            provenance.SourceEpoch, Side.Dropbox, Domain, identity, PathNames.DropboxKey(path), anchor, through);

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
            store.BeginSelection(selection);
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
        store.BeginSelection(remaining);
        store.BeginSelection(remaining);
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

    // R6-Q1: owed Repair refuses live selection only before the pair's first completed Repair; Local skipped
    // coverage still selects, because only its Local-writing effects are refused.
    public static void RepairHeldSelection()
    {
        WithStore((store, database) =>
        {
            var provenance = Install(store);
            Acquire(store, provenance, NextCursor, [Notice(Side.Dropbox, File, Identity)]);
            var scope = new PathScope(store.Pair(PairId)!, store.Pairs());
            Check.Require(store.Obligated(PairId) && store.ReadLiveRefusal(PairId) == LiveRefusal.Pair &&
                store.SelectWork(PairId, provenance.PairGeneration, scope) is null,
                "R6 a pair before its first Repair still refuses live selection");
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
        store.BeginSelection(released);
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
        store.BeginSelection(ancestor);
        var child = store.SelectWork(PairId, provenance.PairGeneration, new PathScope(store.Pair(PairId)!, store.Pairs()))!;
        Check.Require(child.AnchorWorkId != ancestor.AnchorWorkId, "the independent child is offered after the refused parent");
        foreach (var query in new[] { 0, 1 })
        {
            var blocked = store.PendingAncestors(PairId, provenance.PairGeneration, Side.Dropbox,
                [PathNames.DropboxKey(File)]).Single();
            Throws<LiveAdmissionStaleException>(() => store.BeginSelection(blocked));
        }
        store.EndWorkPass(PairId);
        var next = store.SelectWork(PairId, provenance.PairGeneration, new PathScope(store.Pair(PairId)!, store.Pairs()))!;
        store.BeginSelection(next);
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
        store.BeginSelection(selection);
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
        store.BeginSelection(rename);
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
        store.BeginSelection(later);
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
            store.BeginSelection(first);
            var different = children[1] with { LocalSize = 1 };
            store.Commit(PairId, StateChange.Empty with { Record = [different] });
            store.BeginSelection(foreign);
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
            store.BeginSelection(first);
            store.BeginSelection(foreign);
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
        store.BeginSelection(selection);
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
            store.BeginSelection(selection);
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
        store.BeginSelection(previous);
        store.BeginSelection(selection);
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
        store.BeginSelection(previous);
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
        store.BeginSelection(selection);
        var foreign = own with { LocalId = OtherIdentity };
        store.Commit(PairId, StateChange.Empty with { Record = [foreign] });
        Throws<LiveAdmissionStaleException>(() => store.Commit(PairId, StateChange.Empty with { Record = [own] }, selection, null));
        Check.Require(store.ReadWork(selection).Single().Before == own && store.SyncedByKey(PairId, Side.Local, own.LocalKey) == foreign,
            "own record refuses a real foreign baseline and leaves both facts unchanged");
        store.Commit(PairId, StateChange.Empty with { ForgetSelf = [(Side.Local, own.LocalKey)] });
        store.BeginSelection(selection);
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
        store.BeginSelection(selection);
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
        store.BeginSelection(next);
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
        store.BeginSelection(repeated);
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
        store.BeginSelection(released);
        Check.Require(store.CompleteWork(released, null), "the released dirty child can finish normally");
    }

    private static void OlderDirtyEpisode(string database, bool acquired)
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
            store.BeginSelection(dirtyNotice);
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
                Throws<InvalidOperationException>(() => store.CompleteItemAttempt(candidate, attempt, failure, null, null));
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
                store.BeginSelection(selection);
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
            store.BeginSelection(selection);
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

using System.Reflection;
using System.Runtime.InteropServices;
using Massdex.SyncShark;
using Massdex.SyncShark.Engine.Local;
using RunLog = Massdex.SyncShark.Engine.Run.RunLog;
using EngineHost = Massdex.SyncShark.Engine.Run.EngineHost;

namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>Checks for JournalSource (`--engine-checks local`), driven through the production native reader
/// against a real NTFS dummy boundary and the production Store (`Storage.StateStore`).
/// Cases: lifecycle rev2 J1-J14 and final-review JF1-JF3, OPTIONS-F083-rev2 acceptance 1-9, and the
/// finite-checkpoint terminal-records case (F091.fix4). The host snapshot/refresh owner is a test double that
/// keeps the proposed contract: immutable list under one gate, side-effect-free accessor, synchronous refresh
/// under the same (reentrant) gate, no native I/O and no lifecycle reentry. The store is the production
/// `Storage.StateStore` behind a forwarding double that can observe or fault one exact call, so the journal CAS,
/// source precedence and notice rows stay the real ones. The row's single physical file owns every case, so the
/// file is not split.</summary>
public static class JournalSourceChecks
{
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    /// <summary>Work committed by another source after the last journal publish is inside both catch-up watermarks.
    /// The foreign page lands right after a publish, so no later journal page can refresh a cached value.</summary>
    public static void QuietVolumeWatermark() => QuietVolumeWatermarkAsync().GetAwaiter().GetResult();

    private static async Task QuietVolumeWatermarkAsync()
    {
        var baseRoot = Directory.CreateTempSubdirectory("SyncSharkJournalQuiet-");
        try
        {
            using var scenario = new Scenario(baseRoot.FullName, "quiet-watermark", ("A", false));
            var cursor = 0;
            void ForeignPageAfterPublish() => scenario.StoreHook.After = method =>
            {
                if (method.Name != nameof(IStateStore.AcquireJournalPage)) return;
                scenario.StoreHook.After = null;
                var source = scenario.Store.ReadSource("A", Side.Dropbox)!.Value;
                var provenance = source.Provenance ?? new SourceProvenance("account", "namespace", "root", "/root",
                    true, true, true, true, 0, source.Epoch);
                if (source.Provenance is null)
                    scenario.Store.AdvanceSource("A", Side.Dropbox, 0, source.Epoch, source.Revision, null, null,
                        source.Epoch, source.Revision, provenance, "cursor", source.Obligation);
                var current = scenario.Store.ReadSource("A", Side.Dropbox)!.Value;
                scenario.Store.AcquireDropboxPage("A", 0, current.Epoch, current.Revision, provenance, current.Cursor,
                    [new ChangeNotice(Side.Dropbox, ChangeKind.Changed, "foreign.txt", null, "foreign", null, false, false)],
                    "cursor" + (++cursor).ToString(System.Globalization.CultureInfo.InvariantCulture));
            };
            ForeignPageAfterPublish();
            File.WriteAllText(Path.Combine(scenario.Root, "quiet-one.txt"), "one");
            using var log = RunLog.Open(baseRoot.FullName, "A", "catch-up", _ => { },
                EngineHost.LoadSettings(baseRoot.FullName).LogBufferCharacters);
            var catchUp = await scenario.Source.CatchUpForPairAsync("A", 0, default, log);
            using (var stream = new FileStream(log.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
                Check.Require(reader.ReadToEnd().Contains(nameof(TextKey.StageJournal), StringComparison.Ordinal),
                    "finite journal catch-up publishes its stage before returning");
            Check.Require(scenario.StoreHook.After is null && catchUp.ThroughWorkId == scenario.Store.ReadWorkWatermark(),
                "A live catch-up covers work another source committed after its last publish.");
            ForeignPageAfterPublish();
            File.WriteAllText(Path.Combine(scenario.Root, "quiet-two.txt"), "two");
            var checkpoint = await scenario.Source.CheckpointForPairAsync("A", 0, default);
            Check.Require(scenario.StoreHook.After is null && checkpoint.ThroughWorkId == scenario.Store.ReadWorkWatermark(),
                "A Repair checkpoint covers work another source committed after its last publish.");
        }
        finally { baseRoot.Delete(recursive: true); }
    }

    private static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var baseRoot = Directory.CreateTempSubdirectory("SyncSharkJournal-");
        try
        {
            if (!NtfsUsnJournal.TryReadState(baseRoot.FullName, out _, out _, out _, out _, out _, out _, out _))
            {
                Console.WriteLine("SKIP JournalSource checks: the fixture volume has no readable NTFS USN journal.");
                Check.Skip();
                return;
            }
            await HealthyPagePublishesBeforeAcknowledgmentAsync(baseRoot.FullName);
            await PairCatchUpReturnsTheCommittedAcquisitionAsync(baseRoot.FullName);
            await SuspendedPageCarriesItsGapOnceAsync(baseRoot.FullName);
            await FrozenLastPageDebtIsCarriedAsync(baseRoot.FullName);
            await FrozenOlderDebtIsRetainedAsync(baseRoot.FullName);
            await FrozenOwnDebtKeepsTheCallersTokenAsync(baseRoot.FullName);
            await FrozenForeignSourceRevisionRefusesAsync(baseRoot.FullName);
            await FrozenForeignMembershipAtRefreshRefusesAsync(baseRoot.FullName);
            await ClosedMembershipAtCheckpointEntryFailsFastAsync(baseRoot.FullName);
            await ReopenedDebtBeforeEntryIsCoveredAsync(baseRoot.FullName);
            await UpdateWinningTheGateRefusesTheObsoleteCheckpointAsync(baseRoot.FullName);
            await OrdinaryHistoryLossInstallsABaseAsync(baseRoot.FullName);
            await FrozenHistoryLossRefusesWithoutABaseAsync(baseRoot.FullName);
            await OrdinaryStalePublicationRefreshesAndReadsFromDurableAsync(baseRoot.FullName);
            await FrozenStalePublicationRefusesWithoutRefreshAsync(baseRoot.FullName);
            await RefreshFailureKeepsTheAcknowledgedPositionAsync(baseRoot.FullName);
            await PublicationHoldsTheGateThroughRefreshAsync(baseRoot.FullName);
            await FiniteCheckpointTerminalRecordsCommitBeforeAcknowledgmentAsync(baseRoot.FullName);
            await StopThenStartAcquiresAgainAsync(baseRoot.FullName);
            await CancellationBeforePublicationKeepsThePositionAsync(baseRoot.FullName);
            await ClosedAndEmptyMembershipDoNotSpinAsync(baseRoot.FullName);
            await IrrelevantProgressStillCommitsAsync(baseRoot.FullName);
            await OwnWorkNotifiesOnlyItsPairAsync(baseRoot.FullName);
            await DriveEvidenceMismatchRaisesOneNoticeAsync(baseRoot.FullName);
            await FrozenHealthyOtherDebtIsAdoptedAsync(baseRoot.FullName);
            await FrozenMiddlePageOtherDebtIsAdoptedAsync(baseRoot.FullName);
            await DelayedSuffixIsReplayedAfterRejoinAsync(baseRoot.FullName);
            await ObsoleteSuffixAtCheckpointEntryIsDiscardedAsync(baseRoot.FullName);
            await StaleStopKeepsTheDurablePositionAsync(baseRoot.FullName);
            await HistoryLossKeepsSkippedCoverageOverLostIntervalAsync(baseRoot.FullName);
            await FrozenRemovedMembershipAtRefreshRefusesAsync(baseRoot.FullName);
            await RemovedMemberBeforePublicationRefusesAsync(baseRoot.FullName);
            await CancellationAfterCommitKeepsTheNewPositionAsync(baseRoot.FullName);
            await LostAcknowledgmentRefusesWithoutReplayAsync(baseRoot.FullName);
            await BackgroundFailureIsObservableAsync(baseRoot.FullName);
            await UnavailablePageKeepsDiagnosticAsync(baseRoot.FullName);
            await BusyVolumeCoverageIsFiniteAsync(baseRoot.FullName);
            await LiveCoverageRefreshesForeignStaleFenceAsync(baseRoot.FullName);
            await LiveEmptyCoverageRepublishesOnceAsync(baseRoot.FullName, false);
            await LiveEmptyCoverageRepublishesOnceAsync(baseRoot.FullName, true);
            await GenuineStoreFailureIsNotStaleNullAsync(baseRoot.FullName);
            await GenuineFenceReadFailureIsNotStaleNullAsync(baseRoot.FullName);
            await MappedUnreadableRecordDeliversRemainingNoticesAsync(baseRoot.FullName);
        }
        finally
        {
            baseRoot.Delete(recursive: true);
        }
    }

    // F083 acceptance 1/7, J3 healthy pages and J14: one acknowledged page publishes its notices and position,
    // and a page with no debt candidate performs zero host refresh reads.
    private static async Task HealthyPagePublishesBeforeAcknowledgmentAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "healthy", ("A", false));
        File.WriteAllText(Path.Combine(scenario.Root, "one.txt"), "one");
        var position = await scenario.Source.CatchUpAsync(default);
        Check.Require(position is not null && scenario.Store.ReadJournal(scenario.VolumeKey) == position,
            "An ordinary page acknowledges only after its notices and position are durable.");
        Check.Require(scenario.Host.RefreshCalls == 0,
            "A healthy page with no debt candidate performs zero refresh reads.");
        var pending = FirstNotice(scenario, "A");
        Check.Require(pending is not null && pending.Notice.Path == "one.txt" && pending.Notice.Side == Side.Local,
            "The healthy page's Local notice is durable before the acknowledged position is returned.");
        Check.Require(scenario.Source.Position == position, "The source resumes at the acknowledged position.");
    }

    // The live host's finite per-pair acquisition: a committed catch-up under the current member fence returns the
    // exact applicability facts it attests, and every refusal is the typed stale coverage admission.
    private static async Task PairCatchUpReturnsTheCommittedAcquisitionAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "pair-catchup", ("A", false));
        Check.Require(scenario.Source.Completion.IsCompleted,
            "An idle source exposes a completed lifetime task to its host.");
        File.WriteAllText(Path.Combine(scenario.Root, "one.txt"), "one");
        var catchUp = await scenario.Source.CatchUpForPairAsync("A", 0, default);
        var position = scenario.Source.Position;
        Check.Require(catchUp.PairId == "A" && catchUp.PairGeneration == 0 && position is not null &&
            catchUp.Checkpoint.JournalId == position.JournalId && (ulong)catchUp.Checkpoint.NextUsn == position.NextUsn,
            "A finite catch-up captures the pair, generation and committed journal boundary it attests.");
        Check.Require(catchUp.SourceEpoch == SourceRow(scenario, "A").Epoch,
            "The captured acquisition names the pair's current source epoch.");
        Check.Require(catchUp.ThroughWorkId == scenario.Store.ReadWorkWatermark(),
            "The captured acquisition names the committed work watermark of its own page.");
        Check.Require(FirstNotice(scenario, "A")?.Notice.Path == "one.txt",
            "The page's work is durable before the live acquisition is returned.");

        foreach (var (pairId, generation) in new[] { ("missing", 0L), ("A", 1L) })
        {
            try
            {
                await scenario.Source.CatchUpForPairAsync(pairId, generation, default);
                throw new InvalidOperationException("An unknown pair or generation must refuse the live acquisition.");
            }
            catch (LiveAdmissionStaleException error)
            {
                Check.Require(error.Reason == LiveAdmissionStaleReason.CoverageChanged,
                    "A refused live acquisition is a stale coverage admission: " + pairId);
            }
        }

        using var closed = new Scenario(baseRoot, "pair-closed", ("A", false));
        closed.Host.Close();
        try
        {
            await closed.Source.CatchUpForPairAsync("A", 0, default);
            throw new InvalidOperationException("A closed volume must refuse the live acquisition.");
        }
        catch (LiveAdmissionStaleException error)
        {
            Check.Require(error.Reason == LiveAdmissionStaleReason.CoverageChanged,
                "A null catch-up refuses CoverageChanged rather than claiming healthy coverage.");
        }

        using var suspended = new Scenario(baseRoot, "pair-suspended", ("S", true));
        try
        {
            await suspended.Source.CatchUpForPairAsync("S", 0, default);
            throw new InvalidOperationException("A suspended member must refuse the live acquisition.");
        }
        catch (LiveAdmissionStaleException error)
        {
            Check.Require(error.Reason == LiveAdmissionStaleReason.CoverageChanged,
                "A suspended member is not a live acquisition.");
        }
    }

    // F083 acceptance 6, J3 debt continuation and J14: one acknowledged debt page refreshes exactly the K
    // candidates once (not twice), and a repeated suspended hint does not create another revision.
    private static async Task SuspendedPageCarriesItsGapOnceAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "debt", ("A", false), ("B", true));
        // The shared volume's unrelated traffic is read by this volume's one source, and every acknowledged page
        // carrying a suspended candidate refreshes that candidate once. Count publications so the invariant is
        // "one refresh per acknowledged page, never two", independent of how many pages the volume produced.
        var publications = new int[1];
        scenario.StoreHook.After = method =>
        {
            if (method.Name == nameof(IStateStore.AcquireJournalPage)) Interlocked.Increment(ref publications[0]);
        };
        var before = Revision(scenario, "B");
        var aRevision = Revision(scenario, "A");
        File.WriteAllText(Path.Combine(scenario.Root, "one.txt"), "one");
        var position = await scenario.Source.CatchUpAsync(default);
        Check.Require(position is not null, "A debt page is acknowledged.");
        Check.Require(scenario.Host.RefreshCalls == publications[0],
            "Every acknowledged page refreshes its candidates once: pages=" + publications[0] +
            " calls=" + scenario.Host.RefreshCalls);
        Check.Require(scenario.Host.RefreshReads == publications[0],
            "Every acknowledged page reads its single candidate once: pages=" + publications[0] +
            " reads=" + scenario.Host.RefreshReads);
        Check.Require(Revision(scenario, "B") == before + 1 && Obligation(scenario, "B") == SourceObligation.LocalSkippedCoverage,
            "A suspended member gains exactly one skipped-coverage revision from the acknowledged page.");
        Check.Require(Revision(scenario, "A") == aRevision, "A healthy member's revision is untouched by another member's debt.");

        File.WriteAllText(Path.Combine(scenario.Root, "two.txt"), "two");
        var next = await scenario.Source.CatchUpAsync(default);
        Check.Require(next is not null, "A repeated suspended publication is acknowledged.");
        Check.Require(scenario.Host.RefreshCalls == publications[0],
            "A repeated suspended publication adds exactly one refresh per acknowledged page: pages=" +
            publications[0] + " calls=" + scenario.Host.RefreshCalls);
        Check.Require(scenario.Host.RefreshReads == publications[0],
            "A repeated suspended publication reads once per acknowledged page: pages=" + publications[0] +
            " reads=" + scenario.Host.RefreshReads);
        Check.Require(Revision(scenario, "B") == before + 1,
            "A repeated hint inside one suspension interval adds no further revision.");
    }

    // J4/JR2-01: another member's debt created by the checkpoint's own acknowledged page is carried into the
    // private working vector, so the checkpoint succeeds while the issuer owes nothing new.
    private static async Task FrozenLastPageDebtIsCarriedAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "frozen-last", ("A", false), ("C", true));
        var frozen = scenario.Host.Snapshot().ToList();
        var aRevision = Revision(scenario, "A");
        var cRevision = Revision(scenario, "C");
        File.WriteAllText(Path.Combine(scenario.Root, "one.txt"), "one");
        var position = await scenario.Source.CheckpointAfterRepairAsync(frozen, default);
        Check.Require(position is not null && scenario.Store.ReadJournal(scenario.VolumeKey) == position,
            "A checkpoint acknowledges a page whose debt belongs to another member.");
        Check.Require(Revision(scenario, "C") == cRevision + 1 &&
            Obligation(scenario, "C") == SourceObligation.LocalSkippedCoverage,
            "The other member's page-caused successor responsibility stays durable.");
        Check.Require(Revision(scenario, "A") == aRevision,
            "The repair issuer gains no responsibility from the other member's debt.");
        Check.Require(frozen.All(member => member.Revision ==
            (member.Pair.PairId == "A" ? aRevision : cRevision)),
            "The caller's captured list is never rewritten by the checkpoint.");
        Check.Require(!scenario.Store.Issues("A", [IssueKind.Failure])
                .Any(issue => issue.Cause == TextKey.RequiredRepairRetry),
            "The other member's debt raises no failed-Repair notice for the issuer.");
    }

    // J5/JR2-01: an older debt interval survives a later page that repeats the same suspension hint.
    private static async Task FrozenOlderDebtIsRetainedAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "frozen-old", ("A", false), ("C", true));
        File.WriteAllText(Path.Combine(scenario.Root, "one.txt"), "one");
        await scenario.Source.CatchUpAsync(default);
        var owed = Revision(scenario, "C");
        Check.Require(Obligation(scenario, "C") == SourceObligation.LocalSkippedCoverage,
            "The earlier page installs the other member's gap responsibility.");
        var frozen = scenario.Host.Snapshot().ToList();
        File.WriteAllText(Path.Combine(scenario.Root, "two.txt"), "two");
        var position = await scenario.Source.CheckpointAfterRepairAsync(frozen, default);
        Check.Require(position is not null && Revision(scenario, "C") == owed &&
            Obligation(scenario, "C") == SourceObligation.LocalSkippedCoverage,
            "A later page with the same suspension keeps the older responsibility without another revision.");
        Check.Require(frozen.All(member => member.Pair.PairId != "C" || member.Revision == owed),
            "The checkpoint's captured caller list is unchanged.");
    }

    // J6/JR2-02: the issuer's own new debt is acknowledged as a durable boundary, but the caller's captured
    // completion token still predates it and cannot clear it.
    private static async Task FrozenOwnDebtKeepsTheCallersTokenAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "frozen-own", ("A", false));
        var frozen = scenario.Host.Snapshot().ToList();
        var captured = Revision(scenario, "A");
        var file = Path.Combine(scenario.Root, "one.txt");
        File.WriteAllText(file, "one");
        Check.Require(CreateHardLink(Path.Combine(scenario.Root, "alias.txt"), file),
            "The native dummy boundary creates a hard link for the unclassifiable-history case.");
        var position = await scenario.Source.CheckpointAfterRepairAsync(frozen, default);
        Check.Require(position is not null, "The checkpoint may acknowledge a boundary that makes its own issuer's token obsolete.");
        Check.Require(Revision(scenario, "A") == captured + 1 && Obligation(scenario, "A") is not null,
            "The issuer's own page-caused debt is durable.");
        Check.Require(frozen[0].Revision == captured,
            "The captured pre-list completion token is immutable; the separate completion owner must refuse it.");
    }

    // J8/JF3 frozen part: a foreign source revision committed before the next page refuses without a page
    // publication, refresh, retry or caller-list change.
    private static async Task FrozenForeignSourceRevisionRefusesAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "frozen-foreign", ("A", false), ("B", false));
        var frozen = scenario.Host.Snapshot().ToList();
        var journal = scenario.Store.ReadJournal(scenario.VolumeKey);
        File.WriteAllText(Path.Combine(scenario.Root, "one.txt"), "one");
        var source = SourceRow(scenario, "B");
        scenario.Store.AdvanceSource("B", Side.Local, 0, source.Epoch, source.Revision, source.Provenance,
            source.Cursor, source.Epoch, source.Revision + 1, source.Provenance, source.Cursor, null);
        var position = await scenario.Source.CheckpointAfterRepairAsync(frozen, default);
        Check.Require(position is null && scenario.Store.ReadJournal(scenario.VolumeKey) == journal,
            "A foreign source-fence change refuses the frozen checkpoint with no page publication.");
        Check.Require(scenario.Host.RefreshCalls == 0, "A frozen foreign fence change performs no refresh read.");
        Check.Require(frozen.All(member => member.Pair.PairId != "B" || member.Revision == source.Revision),
            "The caller's captured fences are unchanged by the refusal.");
    }

    // J8/JR2-04: a foreign membership change that wins the protected commit/refresh span is refused rather
    // than absorbed; the already-acknowledged page stays committed.
    private static async Task FrozenForeignMembershipAtRefreshRefusesAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "frozen-refresh", ("A", false), ("B", true));
        var frozen = scenario.Host.Snapshot().ToList();
        var journal = scenario.Store.ReadJournal(scenario.VolumeKey);
        var extra = scenario.AddPair("X", false);
        var foreign = scenario.Host.Snapshot().Append(extra).ToArray();
        scenario.Host.BeforeRefresh = _ =>
        {
            scenario.Host.Install(foreign);
            return true;
        };
        File.WriteAllText(Path.Combine(scenario.Root, "one.txt"), "one");
        var position = await scenario.Source.CheckpointAfterRepairAsync(frozen, default);
        Check.Require(position is null,
            "A foreign membership change inside the protected span refuses the checkpoint.");
        Check.Require(scenario.Store.ReadJournal(scenario.VolumeKey) != journal,
            "The already-acknowledged page stays committed; the refusal does not pretend it rolled back.");
        Check.Require(frozen.All(member => member.Pair.PairId != "X"),
            "The foreign member is never adopted into the caller's captured list.");
    }

    // JF1/F083 acceptance 5: a volume fence that is closed when the checkpoint starts has no OPEN snapshot to
    // capture and fails fast without waiting for reopen or touching Storage.
    private static async Task ClosedMembershipAtCheckpointEntryFailsFastAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "closed", ("A", false));
        var frozen = scenario.Host.Snapshot().ToList();
        var journal = scenario.Store.ReadJournal(scenario.VolumeKey);
        File.WriteAllText(Path.Combine(scenario.Root, "one.txt"), "one");
        scenario.Host.Close();
        var position = await scenario.Source.CheckpointAfterRepairAsync(frozen, default);
        Check.Require(position is null && scenario.Store.ReadJournal(scenario.VolumeKey) == journal,
            "A closed volume fence at checkpoint entry fails fast with no page publication.");
        Check.Require(scenario.Host.RefreshCalls == 0, "A closed entry neither refreshes nor waits for reopen.");
    }

    // JF2: a member change that fully committed (and reopened) before checkpoint entry is covered by the entry
    // vector, and that member's successor debt survives.
    private static async Task ReopenedDebtBeforeEntryIsCoveredAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "reopened", ("A", false), ("C", true));
        File.WriteAllText(Path.Combine(scenario.Root, "one.txt"), "one");
        await scenario.Source.CatchUpAsync(default);
        var owed = Revision(scenario, "C");
        var frozen = scenario.Host.Snapshot().ToList();
        scenario.Host.Install(frozen);
        File.WriteAllText(Path.Combine(scenario.Root, "two.txt"), "two");
        var position = await scenario.Source.CheckpointAfterRepairAsync(frozen, default);
        Check.Require(position is not null && Revision(scenario, "C") == owed &&
            Obligation(scenario, "C") == SourceObligation.LocalSkippedCoverage,
            "A debt committed and reopened before entry is covered, and its responsibility survives.");
    }

    // F083 acceptance 4: when a membership update wins the gate before the checkpoint publishes, the obsolete
    // captured vector refuses and no page is written.
    private static async Task UpdateWinningTheGateRefusesTheObsoleteCheckpointAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "update-first", ("A", false), ("B", false));
        var frozen = scenario.Host.Snapshot().ToList();
        var journal = scenario.Store.ReadJournal(scenario.VolumeKey);
        File.WriteAllText(Path.Combine(scenario.Root, "one.txt"), "one");
        scenario.Host.Install(frozen.Select(member => member with { Suspended = true }).ToArray());
        var position = await scenario.Source.CheckpointAfterRepairAsync(frozen, default);
        Check.Require(position is null && scenario.Store.ReadJournal(scenario.VolumeKey) == journal,
            "An update that wins the gate makes the obsolete checkpoint refuse with no page publication.");
    }

    // J10/F083 acceptance 2: an ordinary history loss installs one fresh base with every enabled member's
    // bootstrap responsibility, then acquisition continues; nothing is reported as clean.
    private static async Task OrdinaryHistoryLossInstallsABaseAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "history", ("A", false));
        var bogus = new JournalPosition(scenario.Start.JournalId ^ 1UL, scenario.Start.NextUsn);
        scenario.Store.AcquireJournalPage(scenario.VolumeKey, scenario.Start, bogus, Fences(scenario), []);
        var before = Revision(scenario, "A");
        var source = new JournalSource(scenario.VolumeKey, scenario.Root, scenario.Store, bogus,
            scenario.Host.Gate, scenario.Host.Read, scenario.Host.Refresh);
        var position = await source.CatchUpAsync(default);
        var now = NtfsUsnJournal.ReadCheckpoint(scenario.Root);
        Check.Require(position is not null && position.JournalId == (ulong)now.JournalId &&
            position.NextUsn >= (ulong)now.FirstUsn && position.NextUsn <= (ulong)now.NextUsn,
            "History loss installs one fresh base at the journal's current identity and position.");
        Check.Require(Revision(scenario, "A") == before + 1 &&
            Obligation(scenario, "A") == SourceObligation.Bootstrap,
            "A never-repaired pair's fresh base installs the bootstrap obligation for every enabled member.");
    }

    // J10/JF3 frozen part: a frozen checkpoint never installs a replacement base or resets the captured debt.
    private static async Task FrozenHistoryLossRefusesWithoutABaseAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "history-frozen", ("A", false));
        var bogus = new JournalPosition(scenario.Start.JournalId ^ 1UL, scenario.Start.NextUsn);
        scenario.Store.AcquireJournalPage(scenario.VolumeKey, scenario.Start, bogus, Fences(scenario), []);
        var frozen = scenario.Host.Snapshot().ToList();
        var source = new JournalSource(scenario.VolumeKey, scenario.Root, scenario.Store, bogus,
            scenario.Host.Gate, scenario.Host.Read, scenario.Host.Refresh);
        var position = await source.CheckpointAfterRepairAsync(frozen, default);
        Check.Require(position is null && scenario.Store.ReadJournal(scenario.VolumeKey) == bogus,
            "A frozen history loss refuses and installs no fresh base or replacement responsibility.");
        Check.Require(frozen[0].Revision == Revision(scenario, "A"),
            "The refused frozen checkpoint leaves the caller's captured fences unchanged.");
    }

    // J9/F083 acceptance 3: an ordinary stale publication explicitly refreshes the current fences from the same
    // authoritative reads and restarts from the durable position, reusing one read per member.
    private static async Task OrdinaryStalePublicationRefreshesAndReadsFromDurableAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "stale", ("A", false));
        File.WriteAllText(Path.Combine(scenario.Root, "one.txt"), "one");
        // A durable journal position is always a native record boundary; a start inside a record is rejected
        // with ERROR_INVALID_PARAMETER, so the synthetic position comes from the journal itself.
        var checkpoint = NtfsUsnJournal.ReadCheckpoint(scenario.Root);
        var durable = new JournalPosition((ulong)checkpoint.JournalId, (ulong)checkpoint.NextUsn);
        scenario.Store.AcquireJournalPage(scenario.VolumeKey, scenario.Start, durable, Fences(scenario), []);
        var position = await scenario.Source.CatchUpAsync(default);
        Check.Require(position is not null && scenario.Store.ReadJournal(scenario.VolumeKey) == position &&
            position.NextUsn >= durable.NextUsn,
            "An ordinary stale publication recovers from the authoritative durable position.");
        Check.Require(scenario.Host.RefreshCalls == 1 && scenario.Host.RefreshReads == 1,
            "The stale path refreshes each current member once and reuses that read for the private copy.");
    }

    // J9 frozen half: a frozen SQL-stale page returns null without a refresh, replay or guessed revision.
    private static async Task FrozenStalePublicationRefusesWithoutRefreshAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "stale-frozen", ("A", false));
        var frozen = scenario.Host.Snapshot().ToList();
        File.WriteAllText(Path.Combine(scenario.Root, "one.txt"), "one");
        var durable = new JournalPosition(scenario.Start.JournalId, scenario.Start.NextUsn + 1UL);
        scenario.Store.AcquireJournalPage(scenario.VolumeKey, scenario.Start, durable, Fences(scenario), []);
        var position = await scenario.Source.CheckpointAfterRepairAsync(frozen, default);
        Check.Require(position is null && scenario.Store.ReadJournal(scenario.VolumeKey) == durable,
            "A frozen SQL-stale publication refuses without replaying the old page.");
        Check.Require(scenario.Host.RefreshCalls == 0,
            "A frozen SQL-stale publication performs no refresh and keeps its captured fences.");
    }

    // JR2-05/rev2 line 68: a refresh failure after an earlier read succeeds installs no partial snapshot,
    // keeps the already-committed position and returns no successful checkpoint.
    private static async Task RefreshFailureKeepsTheAcknowledgedPositionAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "refresh-fail", ("A", false), ("B", true));
        var frozen = scenario.Host.Snapshot().ToList();
        var journal = scenario.Store.ReadJournal(scenario.VolumeKey);
        scenario.Host.BeforeRefresh = _ => throw new IOException("refresh-fixture");
        File.WriteAllText(Path.Combine(scenario.Root, "one.txt"), "one");
        try
        {
            await scenario.Source.CheckpointAfterRepairAsync(frozen, default);
            throw new InvalidOperationException("A refresh failure must not return a successful checkpoint.");
        }
        catch (IOException)
        {
        }
        Check.Require(scenario.Store.ReadJournal(scenario.VolumeKey) != journal &&
            scenario.Store.ReadJournal(scenario.VolumeKey) == scenario.Source.Position,
            "The acknowledged page stays durable after the later refresh failure; no rollback or replay is claimed.");
        Check.Require(!scenario.Source.Available, "A refresh failure after commit is surfaced, never silently healthy.");
        Check.Require(scenario.Host.Snapshot().Count == frozen.Count,
            "A failed refresh installs no partial member snapshot.");
    }

    // F083 acceptance 5/rev2 line 65: the successful publication comparison, the Store call, the authoritative
    // reads and the private adoption are one protected span. A competing membership update cannot enter it.
    private static async Task PublicationHoldsTheGateThroughRefreshAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "gate", ("A", false), ("B", true));
        // As in the debt case, the shared volume's unrelated traffic is published by this one source and each
        // acknowledged page carrying the suspended candidate refreshes it once; count pages, not the assumed one.
        var publications = new int[1];
        scenario.StoreHook.After = method =>
        {
            if (method.Name == nameof(IStateStore.AcquireJournalPage)) Interlocked.Increment(ref publications[0]);
        };
        var frozen = scenario.Host.Snapshot().ToList();
        using var insideSpan = new ManualResetEventSlim(false);
        using var releaseSpan = new ManualResetEventSlim(false);
        scenario.Host.BeforeRefresh = _ =>
        {
            insideSpan.Set();
            releaseSpan.Wait();
            return false;
        };
        File.WriteAllText(Path.Combine(scenario.Root, "one.txt"), "one");
        var checkpoint = scenario.Source.CheckpointAfterRepairAsync(frozen, default);
        try
        {
            Check.Require(insideSpan.Wait(TimeSpan.FromSeconds(30)),
                "The checkpoint reaches its protected publication/refresh span.");
            if (Monitor.TryEnter(scenario.Host.Gate))
            {
                Monitor.Exit(scenario.Host.Gate);
                throw new InvalidOperationException(
                    "A competing membership update entered while the protected span was held.");
            }
        }
        finally
        {
            releaseSpan.Set();
        }
        var position = await checkpoint;
        Check.Require(position is not null && scenario.Host.RefreshCalls == publications[0],
            "Publication wins: every acknowledged page refreshes once before a competing update can publish: pages=" +
            publications[0] + " calls=" + scenario.Host.RefreshCalls);
    }

    // F091.fix4 (coordinator addition): a checkpoint read is finite to the boundary captured at entry. Sustained
    // records keep arriving beyond that boundary while the checkpoint runs; the terminal finite response may itself
    // be Complete while delivering records. Its notices must be durable before the acknowledgment, and the traffic
    // beyond the captured boundary must remain for ordinary acquisition rather than being folded into the checkpoint.
    private static async Task FiniteCheckpointTerminalRecordsCommitBeforeAcknowledgmentAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "finite", ("A", false), ("C", true));
        File.WriteAllText(Path.Combine(scenario.Root, "early.txt"), "early");
        var frozen = scenario.Host.Snapshot().ToList();
        var entry = NtfsUsnJournal.ReadCheckpoint(scenario.Root);
        var boundary = new JournalPosition((ulong)entry.JournalId, (ulong)entry.NextUsn);
        const int burstFiles = 200;
        using var startBurst = new ManualResetEventSlim(false);
        var first = new int[1];
        scenario.Host.BeforeRefresh = _ =>
        {
            if (Interlocked.Increment(ref first[0]) == 1) startBurst.Set();
            return false;
        };
        var writer = Task.Run(() =>
        {
            startBurst.Wait();
            for (var index = 0; index < burstFiles; index++)
                File.WriteAllText(Path.Combine(scenario.Root, "burst-" + index + ".txt"), "burst");
        });
        var position = await scenario.Source.CheckpointAfterRepairAsync(frozen, default);
        await writer;
        Check.Require(position is not null && position.NextUsn >= boundary.NextUsn &&
            scenario.Store.ReadJournal(scenario.VolumeKey) == position,
            "A checkpoint acknowledges the finite boundary captured at entry while records keep arriving beyond it.");
        Check.Require(FirstNotice(scenario, "A")?.Notice.Path == "early.txt",
            "The terminal finite response's notices are durable before the checkpoint acknowledgment is returned.");
        var tail = await scenario.Source.CatchUpAsync(default);
        Check.Require(tail is not null && tail.NextUsn > position!.NextUsn,
            "Records arriving beyond the captured boundary remain for ordinary acquisition, not the checkpoint.");
    }

    // J11 last clause: an intentional stop must not leave the source permanently unavailable; the next start
    // actually acquires again.
    private static async Task StopThenStartAcquiresAgainAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "stop-start", ("A", false));
        await scenario.Source.StartAsync(default);
        await scenario.Source.StopAsync();
        Check.Require(scenario.Source.Available, "An intentional cancellation leaves acquisition available.");
        File.WriteAllText(Path.Combine(scenario.Root, "later.txt"), "later");
        var position = await scenario.Source.CatchUpAsync(default);
        Check.Require(position is not null && position.NextUsn > scenario.Start.NextUsn,
            "A stopped source acquires again on the next run instead of exiting silently.");
    }

    // J10 cancellation half: a cancellation before publication preserves the old position.
    private static async Task CancellationBeforePublicationKeepsThePositionAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "cancel", ("A", false));
        File.WriteAllText(Path.Combine(scenario.Root, "one.txt"), "one");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await scenario.Source.CatchUpAsync(cancelled.Token);
            throw new InvalidOperationException("A cancelled acquisition cannot publish a page.");
        }
        catch (OperationCanceledException)
        {
        }
        Check.Require(scenario.Store.ReadJournal(scenario.VolumeKey) == scenario.Start,
            "Cancellation before publication changes nothing.");
    }

    // J13/rev2 line 49: an acquisition-closed snapshot and an open empty snapshot both return null and never
    // spin into a retry loop.
    private static async Task ClosedAndEmptyMembershipDoNotSpinAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "closed-empty", ("A", false));
        var journal = scenario.Store.ReadJournal(scenario.VolumeKey);
        scenario.Host.Close();
        Check.Require(await scenario.Source.CatchUpAsync(default) is null &&
            scenario.Store.ReadJournal(scenario.VolumeKey) == journal,
            "Closed membership returns null and publishes nothing.");
        scenario.Host.Install([]);
        Check.Require(await scenario.Source.CatchUpAsync(default) is null &&
            scenario.Store.ReadJournal(scenario.VolumeKey) == journal,
            "An open empty membership returns null instead of polling or claiming coverage.");
    }

    // F083 acceptance 7/J3: unrelated progress is still valid progress with a nonnull watermark, including zero.
    private static async Task IrrelevantProgressStillCommitsAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "irrelevant", ("A", false));
        var outside = Directory.CreateTempSubdirectory("SyncSharkJournalOutside-");
        try
        {
            for (var index = 0; index < 3; index++)
                File.WriteAllText(Path.Combine(outside.FullName, "outside-" + index + ".txt"), "outside");
            var position = await scenario.Source.CatchUpAsync(default);
            Check.Require(position is not null && position.NextUsn > scenario.Start.NextUsn,
                "Irrelevant progress commits a valid nonnull watermark without inventing work.");
            Check.Require(scenario.Host.RefreshCalls == 0 && FirstNotice(scenario, "A") is null,
                "Unrelated volume traffic produces no notice and no refresh.");
        }
        finally
        {
            outside.Delete(recursive: true);
        }
    }

    // The host's work notification: a page with notices wakes its owning pair (a repeated wake is harmless to the
    // host), while unrelated volume progress wakes nobody, so a change/CLOSE reaches the opportunity without polling.
    private static async Task OwnWorkNotifiesOnlyItsPairAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "wake", ("A", false));
        var woken = new List<string>();
        scenario.Source.WorkCommitted = woken.Add;
        File.WriteAllText(Path.Combine(scenario.Root, "one.txt"), "one");
        Check.Require(await scenario.Source.CatchUpAsync(default) is not null,
            "The page carrying the pair's work is acknowledged.");
        Check.Require(woken.Count > 0 && woken.All(id => id == "A"),
            "A committed page wakes only the pair that owns its work.");
        woken.Clear();
        var outside = Directory.CreateTempSubdirectory("SyncSharkJournalOutside-");
        try
        {
            File.WriteAllText(Path.Combine(outside.FullName, "outside.txt"), "outside");
            Check.Require(await scenario.Source.CatchUpAsync(default) is not null,
                "Unrelated volume progress is still acknowledged.");
        }
        finally
        {
            outside.Delete(recursive: true);
        }
        Check.Require(woken.Count == 0, "Unrelated volume progress wakes no pair.");
    }

    // Compare's drive evidence: the member's current evidence is checked against the pair's saved evidence and
    // raises one typed DriveChange notice, while matching evidence adds nothing.
    private static async Task DriveEvidenceMismatchRaisesOneNoticeAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "drive", ("A", false));
        scenario.Host.Install(scenario.Snapshot().Select(member => member with
        {
            Pair = member.Pair with { Drive = new DriveEvidence(1, null, null) },
            Drive = new DriveEvidence(2, null, null),
        }).ToArray());
        Check.Require(await scenario.Source.CatchUpForPairAsync("A", 0, default) is not null,
            "The seeded base position is current after the evidence change.");
        var raised = scenario.Store.Issues("A", [IssueKind.DriveChange]);
        Check.Require(raised.Count == 1 && raised[0].Cause == TextKey.DriveChanged,
            "A replaced drive raises exactly one typed DriveChange notice.");
        scenario.Host.Install(scenario.Snapshot().Select(member => member with
        {
            Pair = member.Pair with { Drive = new DriveEvidence(2, null, null) },
            Drive = new DriveEvidence(2, null, null),
        }).ToArray());
        Check.Require(await scenario.Source.CatchUpForPairAsync("A", 0, default) is not null,
            "The seeded base position is current after the matching evidence.");
        Check.Require(scenario.Store.Issues("A", [IssueKind.DriveChange]).Count == 1,
            "Matching drive evidence adds no second notice.");
    }

    // J4: a HEALTHY other member's repair debt created by a hard-link record on the checkpoint's last page
    // is adopted from the same authoritative read, so the checkpoint succeeds while the repair issuer owes
    // nothing new. One publication proves the debt page was the terminal page.
    private static async Task FrozenHealthyOtherDebtIsAdoptedAsync(string baseRoot)
    {
        using var scenario = Scenario.WithSeparateRoots(baseRoot, "frozen-healthy", ("A", false), ("C", false));
        var frozen = scenario.Host.Snapshot().ToList();
        var aRevision = Revision(scenario, "A");
        var cRevision = Revision(scenario, "C");
        var publications = new int[1];
        scenario.StoreHook.After = method =>
        {
            if (method.Name == nameof(IStateStore.AcquireJournalPage)) Interlocked.Increment(ref publications[0]);
        };
        var file = Path.Combine(scenario.RootOf("C"), "one.txt");
        File.WriteAllText(file, "one");
        Check.Require(CreateHardLink(Path.Combine(scenario.RootOf("C"), "alias.txt"), file),
            "The native dummy boundary creates a hard link for the healthy member's unclassifiable history.");
        var position = await scenario.Source.CheckpointAfterRepairAsync(frozen, default);
        Check.Require(position is not null && scenario.Store.ReadJournal(scenario.VolumeKey) == position,
            "A checkpoint acknowledges a page whose debt belongs to a healthy other member.");
        Check.Require(publications[0] == 1,
            "The other member's debt page is the checkpoint's last page, so no later page is published.");
        Check.Require(Revision(scenario, "C") == cRevision + 1 &&
            Obligation(scenario, "C") == SourceObligation.Bootstrap,
            "The healthy other member's page-caused bootstrap responsibility stays durable.");
        Check.Require(Revision(scenario, "A") == aRevision,
            "The repair issuer gains no responsibility from the other member's debt.");
        Check.Require(frozen.All(member => member.Revision == (member.Pair.PairId == "A" ? aRevision : cRevision)),
            "The caller's captured list is never rewritten by the checkpoint.");
        Check.Require(!scenario.Store.Issues("A", [IssueKind.Failure])
                .Any(issue => issue.Cause == TextKey.RequiredRepairRetry),
            "The other member's debt raises no failed-Repair notice for the issuer.");
        Check.Require(scenario.Host.RefreshCalls == 1 && scenario.Host.RefreshReads == 1,
            "The adopted fence comes from exactly one authoritative read of the single candidate member.");
    }

    // J5: the same healthy other-member debt on a MIDDLE checkpoint page. The remaining pages are read before the
    // finite boundary, so the terminal acknowledgment must send the adopted r+1; exactly two publications prove
    // the debt page was not the terminal page.
    private static async Task FrozenMiddlePageOtherDebtIsAdoptedAsync(string baseRoot)
    {
        using var scenario = Scenario.WithSeparateRoots(baseRoot, "frozen-middle", ("A", false), ("C", false));
        RequireRetainedSpan(scenario);
        // The native retained span keeps later pages uncommitted, so only the debt page and the terminal
        // acknowledgment reach the store.
        var aRevision = Revision(scenario, "A");
        var cRevision = Revision(scenario, "C");
        var publications = new int[1];
        scenario.StoreHook.After = method =>
        {
            if (method.Name == nameof(IStateStore.AcquireJournalPage)) Interlocked.Increment(ref publications[0]);
        };
        var file = Path.Combine(scenario.RootOf("C"), "one.txt");
        File.WriteAllText(file, "one");
        Check.Require(CreateHardLink(Path.Combine(scenario.RootOf("C"), "alias.txt"), file),
            "The native dummy boundary creates a hard link for the healthy member's unclassifiable history.");
        WriteBurst(Path.Combine(scenario.Root, "outside"), "middle");
        await scenario.Source.CheckpointForPairAsync("A", 0, default);
        Check.Require(scenario.Source.Position is { } committed &&
            scenario.Store.ReadJournal(scenario.VolumeKey) == committed,
            "A checkpoint acknowledges a middle-page debt and a later page that reuses the adopted successor fence.");
        Check.Require(publications[0] == 2,
            "The debt page and the terminal page are both published, so the debt was not the last page.");
        Check.Require(Revision(scenario, "C") == cRevision + 1 &&
            Obligation(scenario, "C") == SourceObligation.Bootstrap,
            "Each genuine page-caused successor responsibility is carried exactly once.");
        Check.Require(Revision(scenario, "A") == aRevision,
            "The repair issuer gains no responsibility from the other member's debt.");
        Check.Require(!scenario.Store.Issues("A", [IssueKind.Failure])
                .Any(issue => issue.Cause == TextKey.RequiredRepairRetry),
            "The middle-page debt raises no failed-Repair notice for the issuer.");
    }

    // J1: two delayed irrelevant pages hold a suspended member's record in the first page. When that member
    // rejoins with unchanged generation/epoch/revision, the stale suffix is discarded and the ordinary path
    // rereads both pages from P0, so the record is replayed and no old empty publication blesses the interval.
    private static async Task DelayedSuffixIsReplayedAfterRejoinAsync(string baseRoot)
    {
        using var scenario = Scenario.WithSeparateRoots(baseRoot, "delayed", ("A", false), ("B", true));
        var (start, reopened, _) = await CancelDelayedSuffixAsync(scenario, haltAt: 5, withBurst: true);
        var aRevision = Revision(scenario, "A");
        var bRevision = Revision(scenario, "B");
        scenario.Host.Install(reopened);
        var position = await scenario.Source.CatchUpAsync(default);
        Check.Require(position is not null && position.NextUsn > start.NextUsn &&
            scenario.Store.ReadJournal(scenario.VolumeKey) == position,
            "The ordinary path rereads both delayed pages from P0 under the rejoined membership.");
        var notice = FirstNotice(scenario, "B");
        Check.Require(notice is not null && notice.Notice.Path == "early.txt",
            "The first page's suspended record is replayed, never dropped by an old empty publication.");
        Check.Require(Revision(scenario, "B") == bRevision && Obligation(scenario, "B") is null,
            "The superseded suspended suffix never became skipped coverage.");
        Check.Require(Revision(scenario, "A") == aRevision, "The other member gains nothing from the replay.");
    }

    // J11 first clause: an ordinary suffix whose classification vector is obsolete when the checkpoint starts is
    // discarded to the durable position before the frozen operation, which then catches up from P0 itself.
    private static async Task ObsoleteSuffixAtCheckpointEntryIsDiscardedAsync(string baseRoot)
    {
        using var scenario = Scenario.WithSeparateRoots(baseRoot, "obsolete-entry", ("A", false), ("B", true));
        var (start, reopened, _) = await CancelDelayedSuffixAsync(scenario, haltAt: 5, withBurst: true);
        var bRevision = Revision(scenario, "B");
        scenario.Host.Install(reopened);
        var position = await scenario.Source.CheckpointAfterRepairAsync(reopened, default);
        Check.Require(position is not null && position.NextUsn > start.NextUsn &&
            scenario.Store.ReadJournal(scenario.VolumeKey) == position,
            "The checkpoint discards the obsolete suffix and catches up from the durable position.");
        var notice = FirstNotice(scenario, "B");
        Check.Require(notice is not null && notice.Notice.Path == "early.txt",
            "The checkpoint reclassifies the discarded interval under its entry vector.");
        Check.Require(Revision(scenario, "B") == bRevision,
            "The obsolete suffix never became skipped coverage.");
    }

    // J11 second clause: a stop with a stale irrelevant suffix keeps the durable position and never publishes it.
    private static async Task StaleStopKeepsTheDurablePositionAsync(string baseRoot)
    {
        using var scenario = Scenario.WithSeparateRoots(baseRoot, "stale-stop", ("A", false), ("B", true));
        var (start, reopened, publications) = await CancelDelayedSuffixAsync(scenario, haltAt: 2, withBurst: false);
        var bRevision = Revision(scenario, "B");
        scenario.Host.Install(reopened);
        await scenario.Source.StopAsync();
        Check.Require(scenario.Source.Position == start && scenario.Store.ReadJournal(scenario.VolumeKey) == start,
            "A stale stop keeps the last durable position instead of blessing the obsolete suffix.");
        Check.Require(publications[0] == 0, "The stale suffix is discarded, never published.");
        Check.Require(Revision(scenario, "B") == bRevision,
            "The discarded suffix's suspended coverage is never published.");
        Check.Require(FirstNotice(scenario, "B") is null, "The discarded suffix publishes no work.");
    }

    // J10/R-FREEZE batch6-3: a history reset installs LocalSkippedCoverage for a suspended member, which wins
    // over the lost-interval request the fresh base makes for every enabled member.
    private static async Task HistoryLossKeepsSkippedCoverageOverLostIntervalAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "history-gap", ("A", false), ("C", true));
        File.WriteAllText(Path.Combine(scenario.Root, "one.txt"), "one");
        Check.Require(await scenario.Source.CatchUpAsync(default) is not null, "The first page is acknowledged.");
        var cRevision = Revision(scenario, "C");
        Check.Require(Obligation(scenario, "C") == SourceObligation.LocalSkippedCoverage,
            "The acknowledged page installs the suspended member's skipped coverage.");
        var current = scenario.Store.ReadJournal(scenario.VolumeKey)!;
        var bogus = new JournalPosition(current.JournalId ^ 1UL, current.NextUsn);
        Check.Require(scenario.Store.AcquireJournalPage(scenario.VolumeKey, current, bogus, Fences(scenario), [])
            is not null, "The fixture rewrites the journal identity to force a reset.");
        var source = new JournalSource(scenario.VolumeKey, scenario.Root, scenario.Store, bogus,
            scenario.Host.Gate, scenario.Host.Read, scenario.Host.Refresh);
        var position = await source.CatchUpAsync(default);
        Check.Require(position is not null && position.JournalId != bogus.JournalId,
            "History loss installs one fresh base at the journal's current identity.");
        Check.Require(Obligation(scenario, "C") == SourceObligation.LocalSkippedCoverage &&
            Revision(scenario, "C") == cRevision + 1,
            "The reset's lost-interval request never downgrades the suspended member's skipped coverage.");
        Check.Require(Obligation(scenario, "A") == SourceObligation.Bootstrap,
            "A healthy member gains the bootstrap obligation from a never-repaired pair's fresh base.");
    }

    // F083 acceptance 4 (remove): a member removed inside the protected commit/refresh span refuses the frozen
    // checkpoint even though every surviving fence is unchanged; the acknowledged page stays committed.
    private static async Task FrozenRemovedMembershipAtRefreshRefusesAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "frozen-remove", ("A", false), ("B", false), ("C", true));
        var frozen = scenario.Host.Snapshot().ToList();
        var journal = scenario.Store.ReadJournal(scenario.VolumeKey);
        var bRevision = Revision(scenario, "B");
        var cRevision = Revision(scenario, "C");
        scenario.Host.BeforeRefresh = _ =>
        {
            scenario.Host.Install(frozen.Where(member => member.Pair.PairId != "B").ToArray());
            return true;
        };
        File.WriteAllText(Path.Combine(scenario.Root, "one.txt"), "one");
        var position = await scenario.Source.CheckpointAfterRepairAsync(frozen, default);
        Check.Require(position is null,
            "Removing a member inside the protected span refuses the checkpoint.");
        Check.Require(scenario.Store.ReadJournal(scenario.VolumeKey) != journal,
            "The already-acknowledged page stays committed; the refusal does not pretend it rolled back.");
        Check.Require(Revision(scenario, "C") == cRevision + 1 &&
            Obligation(scenario, "C") == SourceObligation.LocalSkippedCoverage,
            "The page's own debt for the surviving member stays durable.");
        Check.Require(Revision(scenario, "B") == bRevision,
            "The removed member gains nothing from the refused checkpoint.");
    }

    // F083 acceptance 4 (remove): a member removed after the page was classified refuses before the page is
    // committed; every surviving fence is unchanged, so only the complete-vector comparison can refuse.
    private static async Task RemovedMemberBeforePublicationRefusesAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "removed", ("A", false), ("B", false));
        var frozen = scenario.Host.Snapshot().ToList();
        var journal = scenario.Store.ReadJournal(scenario.VolumeKey);
        var publications = new int[1];
        scenario.StoreHook.After = method =>
        {
            if (method.Name == nameof(IStateStore.AcquireJournalPage)) Interlocked.Increment(ref publications[0]);
        };
        var halt = new ReadHalt();
        scenario.Host.Reading = halt.Observe;
        // The entry check, the page start and then the classified page's vector comparison are the first three
        // accessor calls; the removal lands at that comparison.
        halt.Arm(3, () => scenario.Host.Install(frozen.Where(member => member.Pair.PairId != "B").ToArray()));
        File.WriteAllText(Path.Combine(scenario.Root, "one.txt"), "one");
        var position = await scenario.Source.CheckpointAfterRepairAsync(frozen, default);
        Check.Require(position is null && scenario.Store.ReadJournal(scenario.VolumeKey) == journal,
            "A removed member refuses the frozen checkpoint before any page is committed.");
        Check.Require(publications[0] == 0, "The refused page makes no store publication.");
        Check.Require(frozen.Any(member => member.Pair.PairId == "B"),
            "The caller's captured list still names the removed member.");
    }

    // J10 cancellation half: a cancellation after the page's durable commit preserves the new position and
    // cannot undo it; no replay or rollback is claimed.
    private static async Task CancellationAfterCommitKeepsTheNewPositionAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "cancel-after", ("A", false));
        using var cancelled = new CancellationTokenSource();
        scenario.StoreHook.After = method =>
        {
            if (method.Name == nameof(IStateStore.AcquireJournalPage)) cancelled.Cancel();
        };
        File.WriteAllText(Path.Combine(scenario.Root, "one.txt"), "one");
        try
        {
            await scenario.Source.CatchUpAsync(cancelled.Token);
            throw new InvalidOperationException("A post-commit cancellation must still stop acquisition.");
        }
        catch (OperationCanceledException)
        {
        }
        var position = scenario.Source.Position;
        Check.Require(position is not null && position.NextUsn > scenario.Start.NextUsn &&
            scenario.Store.ReadJournal(scenario.VolumeKey) == position,
            "A cancellation after commit keeps the new durable position.");
        Check.Require(scenario.Source.Available,
            "An intentional cancellation does not make acquisition permanently unavailable.");
    }

    // JF3: a frozen publication commits but its reply is lost. The source proves the commit from the
    // authoritative readback, refuses further acquisition and never replays or guesses attribution.
    private static async Task LostAcknowledgmentRefusesWithoutReplayAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "lost-ack", ("A", false), ("C", true));
        var frozen = scenario.Host.Snapshot().ToList();
        var entry = scenario.Store.ReadJournal(scenario.VolumeKey);
        var cRevision = Revision(scenario, "C");
        var publications = new int[1];
        scenario.StoreHook.After = method =>
        {
            if (method.Name != nameof(IStateStore.AcquireJournalPage)) return;
            Interlocked.Increment(ref publications[0]);
            throw new IOException("lost-acknowledgment-fixture");
        };
        File.WriteAllText(Path.Combine(scenario.Root, "one.txt"), "one");
        try
        {
            await scenario.Source.CheckpointAfterRepairAsync(frozen, default);
            throw new InvalidOperationException("A lost acknowledgment must not return a successful checkpoint.");
        }
        catch (IOException)
        {
        }
        var position = scenario.Source.Position;
        Check.Require(publications[0] == 1 && position is not null && position != entry &&
            scenario.Store.ReadJournal(scenario.VolumeKey) == position,
            "The committed page is proved from the authoritative readback and never replayed.");
        Check.Require(!scenario.Source.Available,
            "An unknown acknowledgment stops acquisition instead of guessing page attribution.");
        Check.Require(Revision(scenario, "C") == cRevision + 1 &&
            Obligation(scenario, "C") == SourceObligation.LocalSkippedCoverage,
            "The committed page's other-member debt remains durable.");
        Check.Require(frozen.All(member => member.Pair.PairId != "C" || member.Revision == cRevision),
            "The caller's captured list is never rewritten by the lost acknowledgment.");
        var refused = await scenario.Source.CheckpointAfterRepairAsync(frozen, default);
        Check.Require(refused is null && publications[0] == 1,
            "A further frozen checkpoint refuses without replaying the acknowledged page.");
    }

    private static async Task LiveEmptyCoverageRepublishesOnceAsync(string baseRoot, bool staleAgain)
    {
        using var scenario = Scenario.WithSeparateRoots(baseRoot, "live-empty-stale-" + staleAgain,
            ("A", false), ("B", false));
        var attempts = 0;
        scenario.StoreHook.Before = method =>
        {
            if (method.Name != nameof(IStateStore.AcquireJournalPage)) return;
            attempts++;
            if (attempts > 1 && !staleAgain) return;
            var source = scenario.Store.ReadSource("B", Side.Local)!.Value;
            scenario.Store.AdvanceSource("B", Side.Local, 0, source.Epoch, source.Revision,
                source.Provenance, source.Cursor, source.Epoch, source.Revision + 1,
                source.Provenance, source.Cursor, source.Obligation);
        };
        try
        {
            var captured = await scenario.Source.CatchUpForPairAsync("A", 0, default);
            Check.Require(!staleAgain && captured.PairId == "A",
                "A terminal stale publication refreshes and republishes an empty live capture.");
        }
        catch (LiveAdmissionStaleException)
        {
            Check.Require(staleAgain, "Only the second stale publication may refuse this capture.");
        }
        Check.Require(attempts == 2 && scenario.Host.RefreshCalls == 1,
            "Empty live coverage attempts one refresh and one republish, never a stale retry loop.");
        Check.Require(FirstNotice(scenario, "A") is null,
            "Empty capture recovery invents no change records.");
    }

    private static async Task LiveCoverageRefreshesForeignStaleFenceAsync(string baseRoot)
    {
        using var scenario = Scenario.WithSeparateRoots(baseRoot, "live-stale-member", ("A", false), ("B", false));
        var source = scenario.Store.ReadSource("B", Side.Local)!.Value;
        scenario.Store.AdvanceSource("B", Side.Local, 0, source.Epoch, source.Revision,
            source.Provenance, source.Cursor, source.Epoch, source.Revision + 1,
            source.Provenance, source.Cursor, source.Obligation);
        File.WriteAllText(Path.Combine(scenario.RootOf("A"), "one.txt"), "one");
        var captured = await scenario.Source.CatchUpForPairAsync("A", 0, default);
        Check.Require(captured.PairId == "A" && scenario.Host.RefreshCalls == 1 &&
            scenario.Host.RefreshReads == 2,
            "Live coverage refreshes another member's stale storage fence once, without stranding its own selection.");
        Check.Require(FirstNotice(scenario, "A")?.Notice.Path == "one.txt",
            "The live stale-fence refresh retains the captured pair's notice.");
    }

    private static async Task BusyVolumeCoverageIsFiniteAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "busy-finite", ("A", false));
        using var stop = new CancellationTokenSource();
        var publications = 0;
        File.WriteAllText(Path.Combine(scenario.Root, "before.txt"), "before");
        scenario.StoreHook.After = method =>
        {
            if (method.Name != nameof(IStateStore.AcquireJournalPage)) return;
            publications++;
            File.WriteAllText(Path.Combine(scenario.Root, "after.txt"), publications.ToString());
            // A diagnostic tripwire: the old head-chasing loop publishes a new page on every callback.
            if (publications == 8) stop.Cancel();
        };
        var captured = await scenario.Source.CatchUpForPairAsync("A", 0, stop.Token);
        Check.Require(publications == 1 && !stop.IsCancellationRequested,
            "Coverage stops at its captured bound despite new records after every publication.");
        scenario.StoreHook.After = null;
        await scenario.Source.CatchUpAsync(default);
        Check.Require(scenario.Store.ReadWorkWatermark() > captured.ThroughWorkId,
            "Records beyond captured coverage are acquired afterwards, not dropped.");
        var selected = false;
        while (scenario.Store.SelectWork("A", 0, new PathScope(scenario.Store.Pair("A")!, scenario.Store.Pairs())) is { } selection)
            selected |= scenario.Store.ReadWork(selection).Any(row => row.Notice.Path == "after.txt");
        scenario.Store.EndWorkPass("A");
        Check.Require(selected, "A change arriving after finite coverage reaches an ordinary selection.");
    }

    private static async Task UnavailablePageKeepsDiagnosticAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "unavailable-page", ("A", false));
        // A UNC path is rejected before native I/O, deterministically producing an unavailable page.
        var source = new JournalSource(scenario.VolumeKey, @"\\fixture\share", scenario.Store, scenario.Start,
            scenario.Host.Gate, scenario.Host.Read, scenario.Host.Refresh);
        try
        {
            await source.RunAsync(default);
            throw new InvalidOperationException("An unavailable journal page must not complete as healthy.");
        }
        catch (IOException error)
        {
            Check.Require(!source.Available && error.Message.Contains(nameof(NtfsUsnPageStatus.Unavailable),
                StringComparison.Ordinal), "An unavailable page exposes its classified diagnostic and stopped state.");
        }
        Check.Require(scenario.Store.ReadJournal(scenario.VolumeKey) == scenario.Start,
            "An unavailable page never advances the last saved journal position.");
    }

    // J11 failure accounting: a background acquisition that faults after a durable commit escapes to the task's
    // observer and leaves acquisition visibly unavailable.
    private static async Task BackgroundFailureIsObservableAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "background-fault", ("A", false), ("C", true));
        using var reached = new ManualResetEventSlim(false);
        scenario.Host.BeforeRefresh = _ =>
        {
            reached.Set();
            throw new IOException("background-fixture");
        };
        File.WriteAllText(Path.Combine(scenario.Root, "one.txt"), "one");
        await scenario.Source.StartAsync(default);
        Check.Require(reached.Wait(TimeSpan.FromSeconds(30)),
            "Background acquisition reaches its durable page's candidate refresh.");
        var completion = scenario.Source.Completion;
        try
        {
            await completion;
            throw new InvalidOperationException("An escaped background failure must fault the source lifetime task.");
        }
        catch (IOException)
        {
        }
        Check.Require(completion.IsFaulted,
            "The source lifetime task exposes the background fault to its observing host.");
        try
        {
            await scenario.Source.StopAsync();
            throw new InvalidOperationException("An escaped background failure must not be silently swallowed.");
        }
        catch (IOException)
        {
        }
        Check.Require(!scenario.Source.Available,
            "An escaped background durable failure leaves acquisition visibly unavailable.");
        Check.Require(scenario.Store.ReadJournal(scenario.VolumeKey) == scenario.Source.Position,
            "The committed page stays durable even though its refresh failed.");
        scenario.Host.BeforeRefresh = null;
        var failedPosition = scenario.Source.Position;
        var failedRevision = Revision(scenario, "A");
        try
        {
            await scenario.Source.CheckpointForPairAsync("A", 0, default);
            throw new InvalidOperationException("A failed source must not replace a base after Repair has listed.");
        }
        catch (LiveAdmissionStaleException)
        {
            Check.Require(!scenario.Source.Available && scenario.Source.Position == failedPosition &&
                Revision(scenario, "A") == failedRevision,
                "A failed post-list checkpoint refuses without inventing a new base or completion revision.");
        }
        var recovered = await scenario.Source.CatchUpForPairAsync("A", 0, default);
        Check.Require(scenario.Source.Available && recovered.Checkpoint.NextUsn >= (long)scenario.Start.NextUsn &&
            Obligation(scenario, "A") is not null,
            "A failed source installs a fresh pre-list base with Repair debt instead of remaining stopped.");
        File.WriteAllText(Path.Combine(scenario.Root, "after-failure.txt"), "later");
        await scenario.Source.CatchUpAsync(default);
        Check.Require(scenario.Store.ReadWorkWatermark() > recovered.ThroughWorkId,
            "A recovered journal publishes a later real change.");
    }

    // J9/F083 acceptance 3: a genuine store failure is not a stale null. The fault is surfaced and the durable
    // position is unchanged; a stale fence would instead return null with no publication.
    private static async Task GenuineStoreFailureIsNotStaleNullAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "store-fault", ("A", false));
        File.WriteAllText(Path.Combine(scenario.Root, "one.txt"), "one");
        var journal = scenario.Store.ReadJournal(scenario.VolumeKey);
        scenario.StoreHook.Before = method =>
        {
            if (method.Name == nameof(IStateStore.AcquireJournalPage)) throw new IOException("store-fixture");
        };
        try
        {
            await scenario.Source.CatchUpAsync(default);
            throw new InvalidOperationException("A genuine store failure must not return a clean acquisition.");
        }
        catch (IOException)
        {
        }
        Check.Require(scenario.Store.ReadJournal(scenario.VolumeKey) == journal,
            "A genuine store failure changes no durable position.");
        Check.Require(!scenario.Source.Available,
            "A genuine store failure is surfaced, never returned as a stale null.");
    }

    // J9: a genuine source-fence read failure is not a stale null either; it propagates from the frozen
    // checkpoint and publishes nothing.
    private static async Task GenuineFenceReadFailureIsNotStaleNullAsync(string baseRoot)
    {
        using var scenario = new Scenario(baseRoot, "fence-fault", ("A", false));
        var frozen = scenario.Host.Snapshot().ToList();
        File.WriteAllText(Path.Combine(scenario.Root, "one.txt"), "one");
        var journal = scenario.Store.ReadJournal(scenario.VolumeKey);
        scenario.StoreHook.Before = method =>
        {
            if (method.Name == nameof(IStateStore.ReadSource)) throw new IOException("read-fixture");
        };
        try
        {
            await scenario.Source.CheckpointAfterRepairAsync(frozen, default);
            throw new InvalidOperationException("A genuine fence read failure must not return a null checkpoint.");
        }
        catch (IOException)
        {
        }
        Check.Require(scenario.Store.ReadJournal(scenario.VolumeKey) == journal,
            "A genuine fence read failure publishes no page.");
    }

    // J12/F098.fix2: one mapped unreadable record must not drop the page's other notices, and a NeedsRepair
    // raised later in the same page still installs the Repair obligation while the typed item failure is
    // reported. The member's listing is bound to a separate root where "sub" is a plain file, so only the
    // sub/ record cannot be re-observed; every other record reads normally.
    private static async Task MappedUnreadableRecordDeliversRemainingNoticesAsync(string baseRoot)
    {
        // (A) With no NeedsRepair the page stays selectable, so both neighbours of the failing record are durable.
        using (var scenario = new Scenario(baseRoot, "failed-delivery", ("A", false)))
        {
            var listingRoot = InstallUnreadableListing(scenario);
            try
            {
                WriteUnreadablePage(scenario.Members[0].Pair.LocalRoot);
                using var blocked = BlockSubListing(listingRoot);
                Check.Require(await scenario.Source.CatchUpAsync(default) is not null,
                    "A page with a failed item still acknowledges its other work.");
                var published = DeliveredPaths(scenario);
                Check.Require(published.Contains("before.txt") && published.Contains("after.txt"),
                    "The unreadable record does not drop the notices that precede or follow it in the page.");
                Check.Require(!published.Contains("sub/fail.txt"),
                    "The unreadable record itself publishes no notice.");
                Check.Require(ReportedFailure(scenario),
                    "The mapped unreadable record still reports its typed item failure.");
            }
            finally { listingRoot.Delete(recursive: true); }
        }

        // (B) A hard link asks for Repair after the failing record in the same page; the obligation still lands.
        using (var scenario = new Scenario(baseRoot, "failed-repair", ("A", false)))
        {
            var listingRoot = InstallUnreadableListing(scenario);
            try
            {
                var root = scenario.Members[0].Pair.LocalRoot;
                WriteUnreadablePage(root);
                using var blocked = BlockSubListing(listingRoot);
                Check.Require(CreateHardLink(Path.Combine(root, "alias.txt"), Path.Combine(root, "before.txt")),
                    "The native boundary creates the hard link that asks for Repair after the failing record.");
                Check.Require(await scenario.Source.CatchUpAsync(default) is not null,
                    "A page with a failed item still acknowledges its work.");
                Check.Require(ReportedFailure(scenario),
                    "The mapped unreadable record still reports its typed item failure.");
                Check.Require(Obligation(scenario, "A") == SourceObligation.Bootstrap,
                    "A NeedsRepair raised later in the page still installs the repair obligation.");
            }
            finally { listingRoot.Delete(recursive: true); }
        }
    }

    // Binds the member's listing to a separate root where "sub" is a plain file, so only the sub/ record of the
    // page cannot be re-observed; the pair root itself keeps the real sub/ directory and its file.
    private static DirectoryInfo InstallUnreadableListing(Scenario scenario)
    {
        var pair = scenario.Members[0].Pair;
        var listingRoot = Directory.CreateTempSubdirectory("SyncSharkUsnListing-");
        Directory.CreateDirectory(Path.Combine(pair.LocalRoot, "sub"));
        File.WriteAllText(Path.Combine(listingRoot.FullName, "before.txt"), "before");
        File.WriteAllText(Path.Combine(listingRoot.FullName, "after.txt"), "after");
        File.WriteAllText(Path.Combine(listingRoot.FullName, "sub"), "not a directory");
        var listingPair = pair with { LocalRoot = listingRoot.FullName };
        var listing = new LocalListing(listingPair, new PathScope(listingPair, [listingPair]), (_, _, _, _) => { });
        var paths = new UsnPaths(pair, scenario.Store, listing, NtfsIdentityPaths.ReadRootIdentity(pair.LocalRoot));
        var source = SourceRow(scenario, "A");
        scenario.Host.Install(new[] { new JournalSource.Member(pair, 0, source.Epoch, source.Revision, false, paths) });
        return listingRoot;
    }

    // Holds the listing root's "sub" file with no sharing, so opening it as the folder of a record fails with a
    // sharing violation: that is the one record the member cannot re-observe.
    private static FileStream BlockSubListing(DirectoryInfo listingRoot) =>
        File.Open(Path.Combine(listingRoot.FullName, "sub"), FileMode.Open, FileAccess.Read, FileShare.None);

    private static void WriteUnreadablePage(string root)
    {
        File.WriteAllText(Path.Combine(root, "before.txt"), "before");
        File.WriteAllText(Path.Combine(root, "sub", "fail.txt"), "fail");
        File.WriteAllText(Path.Combine(root, "after.txt"), "after");
    }

    // Every acquired notice the pair may select, enumerated through the same anchor pass the engine uses.
    private static HashSet<string> DeliveredPaths(Scenario scenario)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        while (scenario.Store.SelectWork("A", 0, new PathScope(scenario.Store.Pair("A")!, scenario.Store.Pairs())) is { } selection)
            foreach (var item in scenario.Store.ReadWork(selection)) paths.Add(item.Notice.Path);
        scenario.Store.EndWorkPass("A");
        return paths;
    }

    // The failing record reaches the issue owner as a failure for its mapped path, whatever the OS error kind.
    private static bool ReportedFailure(Scenario scenario) =>
        scenario.Store.Issues("A", [IssueKind.Failure]).Any(issue => issue.Path == "sub/fail.txt");

    // Writes the first-page record and an irrelevant batch, installs the native retained span, and cancels the
    // catch-up at the given host-accessor call so the interval stays uncommitted under the suspended vector.
    // The count is this test double's own; if a page sequence changes, the cancel does not happen and the
    // publication assertion fails loudly.
    private static async Task<(JournalPosition Start, IReadOnlyList<JournalSource.Member> Reopened, int[] Publications)>
        CancelDelayedSuffixAsync(Scenario scenario, int haltAt, bool withBurst)
    {
        RequireRetainedSpan(scenario);
        // The suspended member's own record is the first of the batch, so it falls in the first native page.
        File.WriteAllText(Path.Combine(scenario.RootOf("B"), "early.txt"), "early");
        var outside = Path.Combine(scenario.Root, "outside");
        if (withBurst) WriteBurst(outside, "page");
        else
        {
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "pending.txt"), "pending");
        }
        var start = scenario.Source.Position!;
        var reopened = scenario.Host.Snapshot()
            .Select(member => member.Pair.PairId == "B" ? member with { Suspended = false } : member).ToArray();
        var publications = new int[1];
        scenario.StoreHook.After = method =>
        {
            if (method.Name == nameof(IStateStore.AcquireJournalPage)) Interlocked.Increment(ref publications[0]);
        };
        using var cancelled = new CancellationTokenSource();
        var halt = new ReadHalt();
        scenario.Host.Reading = halt.Observe;
        // Compare and finite-entry validation precede the page's membership reads.
        halt.Arm(haltAt + 2, cancelled.Cancel);
        try
        {
            await scenario.Source.CatchUpForPairAsync("A", 0, cancelled.Token);
            throw new InvalidOperationException("The cancelled acquisition must not publish the delayed suffix.");
        }
        catch (OperationCanceledException)
        {
        }
        Check.Require(publications[0] == 0, "The delayed suffix is never published under the suspended vector.");
        return (start, reopened, publications);
    }

    // The delayed-suffix cases need the native retained span (MaximumSize - AllocationDelta) that holds
    // irrelevant progress; without it every page commits immediately and there is no suffix to discard.
    private static void RequireRetainedSpan(Scenario scenario) =>
        Check.Require(
            NtfsUsnJournal.TryReadState(scenario.Root, out _, out _, out _, out _, out var maximumSize,
                out var allocationDelta, out _) && maximumSize - allocationDelta > RetainedSpanFloor,
            "The fixture volume exposes a native retained span wide enough for the delayed suffix.");

    // One created file yields create, write and close records of about ninety bytes and one native page holds at
    // most one mebibyte, so this batch spans more than one native page. The floor leaves headroom above it.
    private const int PageBurstFiles = 8000;
    private const ulong RetainedSpanFloor = 4UL * 1024 * 1024;

    private static void WriteBurst(string directory, string prefix)
    {
        Directory.CreateDirectory(directory);
        for (var index = 0; index < PageBurstFiles; index++)
            File.WriteAllText(Path.Combine(directory, prefix + index + ".txt"), "burst");
    }

    private static JournalSource.Member Member(Scenario scenario, PairConfig pair, bool suspended)
    {
        var identity = NtfsIdentityPaths.ReadRootIdentity(pair.LocalRoot);
        var listing = new LocalListing(pair, new PathScope(pair, [pair]), (_, _, _, _) => { });
        var paths = new UsnPaths(pair, scenario.Store, listing, identity);
        var source = SourceRow(scenario, pair.PairId);
        return new JournalSource.Member(pair, 0, source.Epoch, source.Revision, suspended, paths);
    }

    private static (long Epoch, SourceProvenance? Provenance, string? Cursor, SourceObligation? Obligation, long Revision)
        SourceRow(Scenario scenario, string pairId) =>
        scenario.Store.ReadSource(pairId, Side.Local) ?? throw new InvalidOperationException(
            "The pair's Local source row is missing.");

    private static long Revision(Scenario scenario, string pairId) => SourceRow(scenario, pairId).Revision;

    private static SourceObligation? Obligation(Scenario scenario, string pairId) =>
        SourceRow(scenario, pairId).Obligation;

    private static PendingChange? FirstNotice(Scenario scenario, string pairId)
    {
        var selection = scenario.Store.SelectWork(pairId, 0, new PathScope(scenario.Store.Pair(pairId)!, scenario.Store.Pairs()));
        return selection is null ? null : scenario.Store.ReadWork(selection).FirstOrDefault();
    }

    private static IReadOnlyList<(string PairId, long Generation, long Epoch, long Revision, bool Suspended,
        bool LostInterval)> Fences(Scenario scenario) =>
        scenario.Members.Select(member =>
        {
            var source = SourceRow(scenario, member.Pair.PairId);
            return (member.Pair.PairId, (long)0, source.Epoch, source.Revision, false, false);
        }).ToArray();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName,
        IntPtr securityAttributes);

    private static bool CreateHardLink(string newFileName, string existingFileName) =>
        OperatingSystem.IsWindows() && CreateHardLinkW(newFileName, existingFileName, IntPtr.Zero);

    /// <summary>One isolated pair-volume fixture on the shared native volume: a Store database outside the
    /// classified root, the enrolled member snapshot, and one JournalSource over the production reader.</summary>
    private sealed class Scenario : IDisposable
    {
        private readonly string _database;
        private readonly bool _separateRoots;
        public string Root { get; }
        public string VolumeKey { get; }
        public IStateStore Store { get; }
        public Volume Host { get; } = new();
        public List<(PairConfig Pair, bool Suspended)> Members { get; } = [];
        public JournalPosition Start { get; private set; } = null!;
        public JournalSource Source { get; private set; } = null!;

        public Scenario(string baseRoot, string name, params (string Id, bool Suspended)[] members)
            : this(baseRoot, name, false, members)
        {
        }

        /// <summary>A scenario whose pairs own distinct local roots, so a record belongs to exactly one member.</summary>
        public static Scenario WithSeparateRoots(string baseRoot, string name,
            params (string Id, bool Suspended)[] members) => new(baseRoot, name, true, members);

        private Scenario(string baseRoot, string name, bool separateRoots, (string Id, bool Suspended)[] members)
        {
            _separateRoots = separateRoots;
            Root = Path.Combine(baseRoot, name);
            Directory.CreateDirectory(Root);
            VolumeKey = Root;
            _database = Path.Combine(Path.GetTempPath(), "SyncSharkJournal-" + Guid.NewGuid().ToString("N") + ".db");
            Store = HookedStore.Wrap(Massdex.SyncShark.Engine.Storage.StateStore.Open(_database));
            Host.Bind(pairId => SourceRow(this, pairId));
            foreach (var (id, suspended) in members) AddPair(id, suspended);
            Seed();
        }

        /// <summary>The forwarding store double that can observe or fault one exact call.</summary>
        public HookedStore StoreHook => (HookedStore)Store;

        /// <summary>The pair's local root; a distinct directory per pair when the scenario uses separate roots.</summary>
        public string RootOf(string pairId) => Path.Combine(Root, pairId);

        /// <summary>Installs one enrolled pair and clears both seeded first-run obligations so ordinary selection
        /// can expose its acquired notices.</summary>
        public JournalSource.Member AddPair(string id, bool suspended)
        {
            var localRoot = _separateRoots ? RootOf(id) : Root;
            Directory.CreateDirectory(localRoot);
            var pair = new PairConfig(id, id, localRoot, "/" + id, "account", [], [],
                FirstRunPolicy.NewestWins, true, null);
            Store.SavePair(pair);
            foreach (var side in new[] { Side.Local, Side.Dropbox })
            {
                var seeded = Store.ReadSource(id, side)
                    ?? throw new InvalidOperationException("SavePair must seed both source rows.");
                Store.AdvanceSource(id, side, 0, seeded.Epoch, seeded.Revision, seeded.Provenance,
                    seeded.Cursor, seeded.Epoch, seeded.Revision + 1, seeded.Provenance, seeded.Cursor, null);
            }
            var member = Member(this, pair, suspended);
            Members.Add((pair, suspended));
            return member;
        }

        private void Seed()
        {
            var checkpoint = NtfsUsnJournal.ReadCheckpoint(Root);
            Start = new JournalPosition((ulong)checkpoint.JournalId, (ulong)checkpoint.NextUsn);
            Store.AcquireJournalPage(VolumeKey, null, Start, Fences(this), []);
            Host.Install(Snapshot());
            Source = new JournalSource(VolumeKey, Root, Store, Start, Host.Gate, Host.Read, Host.Refresh);
            // The host always supplies the item-failure reporter; this double raises the same typed Failure
            // issue the production reporter records, so the page's other notices still publish.
            Source.ReportItemFailureAsync = (member, failed, _) =>
            {
                var subject = new IssueSubject([new IssueItem(Side.Local, failed.Path, failed.LocalId!)], null);
                Store.Commit(member.Pair.PairId, StateChange.Empty with
                { Raise = [(IssueKind.Failure, failed.Cause, failed.Path, subject)] });
                return Task.CompletedTask;
            };
        }

        public IReadOnlyList<JournalSource.Member> Snapshot() =>
            Members.Select(member => Member(this, member.Pair, member.Suspended)).ToArray();

        public void Dispose()
        {
            Store.Dispose();
            if (File.Exists(_database)) File.Delete(_database);
            foreach (var path in new[] { _database + "-wal", _database + "-shm" })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>The host's one volume gate and immutable member snapshot. The accessor is side-effect free and
    /// called only under the gate; refresh is synchronous, reentrant and never enters native I/O or lifecycle.
    /// The optional reading hook is test synchronisation only and never changes the snapshot it returns.</summary>
    private sealed class Volume
    {
        private readonly object _gate = new();
        private IReadOnlyList<JournalSource.Member>? _snapshot;
        private int _refreshCalls;
        private int _refreshReads;

        public object Gate => _gate;

        /// <summary>Test synchronisation around each accessor call; it observes but never edits the snapshot.</summary>
        public Action? Reading { get; set; }

        public int RefreshCalls => Volatile.Read(ref _refreshCalls);

        public int RefreshReads => Volatile.Read(ref _refreshReads);

        /// <summary>Optional test hook run inside the protected span. Returning true means the hook installed
        /// its own replacement snapshot and the default authoritative refresh is skipped.</summary>
        public Func<IReadOnlyList<string>?, bool>? BeforeRefresh { get; set; }

        public IReadOnlyList<JournalSource.Member>? Read()
        {
            lock (_gate)
            {
                Reading?.Invoke();
                return _snapshot;
            }
        }

        public IReadOnlyList<JournalSource.Member> Snapshot()
        {
            lock (_gate) return _snapshot ?? throw new InvalidOperationException("The volume fence is closed.");
        }

        public void Install(IReadOnlyList<JournalSource.Member> members)
        {
            lock (_gate) _snapshot = members;
        }

        public void Close()
        {
            lock (_gate) _snapshot = null;
        }

        public void Refresh(IReadOnlyList<JournalSource.Member> expected, IReadOnlyList<string>? candidateIds)
        {
            lock (_gate)
            {
                Interlocked.Increment(ref _refreshCalls);
                if (_snapshot is not { } installed) throw new InvalidOperationException("Refresh while closed.");
                if (!Same(expected, installed))
                    throw new InvalidOperationException("The expected snapshot is no longer installed.");
                if (BeforeRefresh is { } hook && hook(candidateIds)) return;
                var updated = new List<JournalSource.Member>(installed.Count);
                foreach (var member in installed)
                {
                    if (candidateIds is not null && !candidateIds.Contains(member.Pair.PairId))
                    {
                        updated.Add(member);
                        continue;
                    }
                    var source = SourceOf(member.Pair.PairId);
                    Interlocked.Increment(ref _refreshReads);
                    updated.Add(member with { Epoch = source.Epoch, Revision = source.Revision });
                }
                _snapshot = updated;
            }
        }

        private Func<string, (long Epoch, SourceProvenance? Provenance, string? Cursor, SourceObligation? Obligation,
            long Revision)>? _source;

        public void Bind(Func<string, (long Epoch, SourceProvenance? Provenance, string? Cursor,
            SourceObligation? Obligation, long Revision)> source) => _source = source;

        private (long Epoch, SourceProvenance? Provenance, string? Cursor, SourceObligation? Obligation, long Revision)
            SourceOf(string pairId) => (_source ?? throw new InvalidOperationException("The host source accessor is unbound."))(pairId);

        private static bool Same(IReadOnlyList<JournalSource.Member> expected,
            IReadOnlyList<JournalSource.Member> installed)
        {
            if (expected.Count != installed.Count) return false;
            var byPair = new Dictionary<string, JournalSource.Member>(installed.Count, StringComparer.Ordinal);
            foreach (var member in installed)
                if (!byPair.TryAdd(member.Pair.PairId, member)) return false;
            foreach (var member in expected)
            {
                if (!byPair.Remove(member.Pair.PairId, out var other)) return false;
                if (other.Generation != member.Generation || other.Epoch != member.Epoch ||
                    other.Revision != member.Revision || other.Suspended != member.Suspended ||
                    !ReferenceEquals(other.Paths, member.Paths) || !ReferenceEquals(other.Pair, member.Pair))
                    return false;
            }
            return true;
        }
    }

    /// <summary>Runs one action at a numbered host-accessor call, so a case can cancel at an exact page boundary
    /// without a sleep. The count is this test double's own call count; if the sequence changes, the action does
    /// not run and the case's outcome assertion fails loudly instead of passing silently.</summary>
    private sealed class ReadHalt
    {
        private int _reads;
        private int _ordinal;
        private Action? _atHalt;

        public void Arm(int ordinal, Action atHalt)
        {
            _atHalt = atHalt;
            Volatile.Write(ref _ordinal, ordinal);
        }

        public void Observe()
        {
            if (Interlocked.Increment(ref _reads) != Volatile.Read(ref _ordinal)) return;
            _atHalt?.Invoke();
        }
    }

    /// <summary>Forwards every store call to the real engine database unchanged, so the journal CAS, source
    /// precedence and notice rows stay the production ones, while letting a case observe one exact call or make
    /// it fail. A fault before the forwarded call models a genuine failure; a fault after it models a lost reply.</summary>
    public class HookedStore : DispatchProxy
    {
        private IStateStore? _inner;

        /// <summary>The real store every call is forwarded to.</summary>
        public IStateStore Inner
        {
            get => _inner ?? throw new InvalidOperationException("The store double is unbound.");
            set => _inner = value;
        }

        public Action<MethodInfo>? Before { get; set; }
        public Action<MethodInfo>? After { get; set; }

        public static IStateStore Wrap(IStateStore inner)
        {
            var proxy = DispatchProxy.Create<IStateStore, HookedStore>();
            ((HookedStore)(object)proxy).Inner = inner;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            Before?.Invoke(targetMethod);
            object? result;
            try
            {
                result = targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException error)
            {
                throw error.InnerException ?? error;
            }
            After?.Invoke(targetMethod);
            return result;
        }
    }
}

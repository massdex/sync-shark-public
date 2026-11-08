using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Planner;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Real seams of live admission and generation change (`--engine-checks live-admission`): production
/// LockKeys.ForLiveAction output into the real StateStore, one pass and one refusal per fence; in-use lifecycles
/// across SavePairDecision; and the re-include refusal. Every case runs and all failures are reported together.</summary>
public static class LiveFenceChecks
{
    private const string PairId = "live-fence";
    private const string LocalRoot = "C:\\Synthetic";
    private const string RemoteRoot = "/Synthetic";
    private const string Connection = "connection";
    private const string Account = "account";
    private const string Domain = "namespace";
    private const string Root = "root-id";
    private const string Parent = "Parent";
    private const string File = "Parent/File";
    private const string Child = "Parent/Child";
    private const string Sibling = "Sibling";
    private const string Elsewhere = "Sibling/File";
    private const string Renamed = "Parent/Renamed";
    private const string Identity = "1234";
    private const string OtherIdentity = "other-identity";
    private const string Cursor = "cursor-";
    private const string Hash = "hash";
    private const ulong Volume = 7;
    private const int PendingVersion = 1;

    private static readonly DateTimeOffset Stamp = DateTimeOffset.UnixEpoch;
    private static readonly RowContext Neutral = new(false, false, false, null, false, false, false, false);
    private static readonly string VolumeKey = Volume.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static void Run()
    {
        var failures = new List<string>();
        void Case(string name, Action<Fixture> body)
        {
            try
            {
                WithFixture(fixture =>
                {
                    body(fixture);
                    Check.Require(fixture.Problems.Count == 0, string.Join("; ", fixture.Problems));
                });
            }
            catch (Exception error) { failures.Add(name + ": " + error.GetType().Name + ": " + error.Message); }
        }
        foreach (var (name, body) in FenceCases()) Case(name, body);
        Case("exact observed peer admits and retires", fixture =>
        {
            fixture.Evidence(File, null);
            fixture.ObservePeer();
            fixture.Expect(LockKeys.ForLiveAction(FileAction(File), Pair(), null), null, "observed peer");
            fixture.CompletePeer(expectRemaining: false);
        });
        Case("peer after observation refuses", fixture =>
        {
            fixture.ObservePeer();
            fixture.Evidence(File, null);
            fixture.Expect(LockKeys.ForLiveAction(FileAction(File), Pair(), null),
                LiveAdmissionStaleReason.RelevantEvidenceChanged, "later peer");
            fixture.CompletePeer(expectRemaining: true);
            fixture.NextOpportunitySettlesKeptRow();
        });
        Case("observed renamed peer admits without consumption", fixture =>
        {
            fixture.Evidence(File, Renamed);
            fixture.ObservePeer();
            fixture.Expect(LockKeys.ForLiveAction(FileAction(File), Pair(), null),
                null, "rename peer");
            fixture.CompletePeer(expectRemaining: true);
        });
        Case("descendant evidence is not a peer", fixture =>
        {
            fixture.Evidence(Child, null);
            fixture.ObservePeer();
            fixture.Expect(LockKeys.ForLiveAction(FolderAction(Parent), Pair(), null),
                LiveAdmissionStaleReason.RelevantEvidenceChanged, "child peer");
        });
        Case("folder spelling move carries pending child evidence", fixture =>
        {
            fixture.Evidence(Child, null);
            fixture.ObservePeer();
            var move = FolderAction(Parent) with { Kind = ActionKind.MoveDropbox, Target = "parent" };
            var scopes = LockKeys.ForLiveAction(move, Pair(), null);
            Check.Require(scopes.All(scope => scope.IncludesDescendants && scope.ExactEvidence),
                "all folder move routes retain subtree locks but admit child evidence");
            fixture.Expect(scopes, null, "folder spelling move");
        });
        foreach (var same in new[] { false, true })
            Case("peer preimage " + same, fixture =>
            {
                fixture.Evidence(File, null);
                var saved = new SyncedEntry(PathNames.Key(File), File, PathNames.DropboxKey(File), File,
                    false, Identity, 1, 0, OtherIdentity, Hash);
                fixture.Store.Commit(PairId, StateChange.Empty with { Record = [saved] });
                fixture.Store.BeginSelection(fixture.Selection);
                if (!same) fixture.Store.Commit(PairId, StateChange.Empty with { Record = [saved with { LocalSize = 2 }] });
                fixture.Store.BeginSelection(fixture.PeerSelection());
                if (!same) fixture.Store.Commit(PairId, StateChange.Empty with { Record = [saved] });
                fixture.ObservePeer();
                fixture.Expect(LockKeys.ForLiveAction(FileAction(File), Pair(), null),
                    null, "observed peer preimage cannot refuse the fresh observation");
                // DESIGN-114 §3.5: a live peer claim survives even when its preimage matches.
                fixture.CompletePeer(expectRemaining: true);
            });
        Case("observed peer owning a child admits without consumption", fixture =>
        {
            fixture.Evidence(File, null);
            fixture.Store.Commit(PairId, StateChange.Empty with
            {
                Raise = [(IssueKind.Failure, TextKey.FailChanged, Elsewhere,
                    new IssueSubject([new IssueItem(Side.Dropbox, Elsewhere, OtherIdentity)], null))]
            });
            var issue = fixture.Store.Issues(PairId).Single();
            fixture.Store.AttachIssueWork(fixture.PeerSelection(), issue,
                [(new ChangeNotice(Side.Dropbox, ChangeKind.Changed, Elsewhere, null, OtherIdentity, null,
                    false, false), false, null)]);
            fixture.ObservePeer();
            fixture.Expect(LockKeys.ForLiveAction(FileAction(File), Pair(), null),
                null, "peer child");
            fixture.CompletePeer(expectRemaining: true);
        });
        Case("issue-associated peer still refuses", fixture =>
        {
            fixture.Evidence(File, null);
            var (store, attempt, subject) = fixture.Episodes();
            var peer = fixture.PeerSelection();
            store.RecordInUseFailure(PairId, 0, peer.SourceEpoch, File, subject, TextKey.FailLocalInUse,
                peer, ItemRetryDispatch.OrdinaryItem, null, null, attempt);
            fixture.ObservePeer();
            fixture.Expect(LockKeys.ForLiveAction(FileAction(File), Pair(), null),
                LiveAdmissionStaleReason.RelevantEvidenceChanged, "associated peer");
            fixture.CompletePeer(expectRemaining: true);
        });
        Case("repeated held notices preserve dirty evidence", fixture =>
        {
            fixture.Hold(File);
            var issue = fixture.Store.Issues(PairId).Single();
            var notice = fixture.Store.ReadWork(fixture.Selection).Single().Notice;
            foreach (var dirty in new[] { false, true, false })
                fixture.Store.AttachIssueWork(fixture.Selection, issue,
                    [(notice with { LocalContentDirty = dirty }, false, null)]);
            fixture.Store.CompleteWork(fixture.Selection, null);
            Check.Require(PendingWork.ByIdentity(fixture.Store, Side.Local, VolumeKey, Identity).Count() == 1 &&
                fixture.Store.ReadLocalDirty(PairId, 0, fixture.Selection.SourceEpoch, VolumeKey,
                    Identity, PathNames.Key(File), null).Dirty,
                "one residual retains dirty evidence from every repeated notice");
        });
        Case("in-use issue-origin episode is rebound", IssueOriginEpisodeRebinds);
        Case("in-use acquired episode is ended", AcquiredEpisodeEnds);
        Case("in-use episode on a changed domain is ended", ChangedDomainEndsEpisode);
        Case("re-include refuses a pending effect", ReincludeRefusesPending);
        foreach (var failure in failures) Console.Error.WriteLine("FAIL " + failure);
        Check.Require(failures.Count == 0, string.Join(" | ", failures));
    }

    private static IEnumerable<(string Name, Action<Fixture> Body)> FenceCases()
    {
        (string, Action<Fixture>) Fence(string name, SyncAction action, Func<Fixture, Action> arrange) => (name, fixture =>
        {
            var footprint = LockKeys.ForLiveAction(action, Pair(), null);
            fixture.Expect(footprint, null, "pass before");
            var clear = arrange(fixture);
            fixture.Expect(footprint, LiveAdmissionStaleReason.RelevantEvidenceChanged, "refusal");
            clear();
            fixture.Expect(footprint, null, "pass after");
        });
        yield return ("covers the selection", fixture =>
        {
            fixture.Expect(LockKeys.ForLiveAction(FileAction(File), Pair(), null), null, "exact");
            fixture.Expect(LockKeys.ForLiveAction(FolderAction(Parent), Pair(), null), null, "subtree");
            fixture.Expect(LockKeys.ForLiveAction(FileAction(Elsewhere), Pair(), null),
                LiveAdmissionStaleReason.FootprintChanged, "another file");
            fixture.Expect(LockKeys.ForLiveAction(FolderAction(Sibling), Pair(), null),
                LiveAdmissionStaleReason.FootprintChanged, "another subtree");
        });
        yield return Fence("held at the path", FileAction(File), fixture => fixture.Hold(File));
        yield return Fence("held above the path", FileAction(File), fixture => fixture.Hold(Parent));
        yield return Fence("held below the subtree", FolderAction(Parent), fixture => fixture.Hold(Child));
        yield return Fence("evidence at the path", FileAction(File), fixture => fixture.Evidence(File, null));
        yield return Fence("evidence below the subtree", FolderAction(Parent), fixture => fixture.Evidence(Child, null));
        yield return Fence("evidence at the old path", FileAction(File), fixture => fixture.Evidence(Renamed, File));
        yield return Fence("pending at the path", FileAction(File), fixture => fixture.Pending());
    }

    private static void IssueOriginEpisodeRebinds(Fixture fixture)
    {
        var (store, id, subject) = fixture.Episodes();
        var before = store.ReadSource(PairId, Side.Local)!.Value;
        var candidate = store.RecordInUseFailure(PairId, 0, before.Epoch, File, subject, TextKey.FailLocalInUse,
            null, ItemRetryDispatch.LocalBranch, null, null, id);
        var pair = store.Pair(PairId)!;
        fixture.Generation = 1;
        store.SavePairDecision(pair, 0, pair with { Name = "renamed" }, null, false, null);
        var after = store.ReadSource(PairId, Side.Local)!.Value;
        var loaded = store.ReadItemRetryForSubject(PairId, 1, File, subject);
        Check.Require(loaded is not null && loaded.Token.PairGeneration == 1 && loaded.Token.SourceEpoch == after.Epoch &&
            loaded.Token.EpisodeId == candidate.Token.EpisodeId && loaded.Token.WorkId == candidate.Token.WorkId,
            "the episode follows the new generation and epoch");
        fixture.Authority = loaded!.Token;
        Check.Require(store.TryClaimItemRetry(loaded, id), "the rebound episode can be claimed");
        store.CompleteItemAttempt(loaded, id, StateChange.Empty with { Close = [(File, IssueClosure.AfterEffect)] }, null, null);
        Check.Require(store.Issues(PairId).Count == 0, "the rebound episode settles normally");
    }

    private static void AcquiredEpisodeEnds(Fixture fixture)
    {
        var (store, id, subject) = fixture.Episodes();
        var issue = store.RecordInUseFailure(PairId, 0, fixture.Selection.SourceEpoch, File, subject,
            TextKey.FailLocalInUse, fixture.Selection, ItemRetryDispatch.OrdinaryItem, null, null, id).Token.IssueId;
        var pair = store.Pair(PairId)!;
        store.SavePairDecision(pair, 0, pair with { Name = "renamed" }, null, false, null);
        Check.Require(store.ReadItemRetryForSubject(PairId, 1, File, subject) is null,
            "an attempt at the new generation is not refused by the old selection's episode");
        Check.Require(store.IssueById(PairId, issue) is not null, "the Failure stays visible for the next Repair");
        Check.Require(store.ReadSource(PairId, Side.Local)!.Value.Obligation == SourceObligation.LocalSkippedCoverage,
            "the retained work is carried by the gap responsibility");
        Reports(fixture);
    }

    private static void ChangedDomainEndsEpisode(Fixture fixture)
    {
        var (store, id, subject) = fixture.Episodes();
        store.RecordInUseFailure(PairId, 0, store.ReadSource(PairId, Side.Local)!.Value.Epoch, File, subject,
            TextKey.FailLocalInUse, null, ItemRetryDispatch.LocalBranch, null, null, id);
        var pair = store.Pair(PairId)!;
        var moved = pair.Roots! with { Local = new DirectoryIdentity(Volume + 1, 1, null) };
        store.SavePairDecision(pair, 0, pair with { Roots = moved }, null, false, null);
        Check.Require(store.ReadItemRetryForSubject(PairId, 1, File, subject) is null,
            "a lifecycle bound to another volume does not cross the rebind");
        Reports(fixture);
    }

    // A later in-use refusal binds a fresh lifecycle rather than colliding with the ended one.
    private static void Reports(Fixture fixture)
    {
        var (store, id, subject) = fixture.Episodes();
        fixture.Generation = 1;
        var fresh = store.RecordInUseFailure(PairId, 1, store.ReadSource(PairId, Side.Local)!.Value.Epoch, File, subject,
            TextKey.FailLocalInUse, null, ItemRetryDispatch.LocalBranch, null, null, id);
        Check.Require(fresh.Token.PairGeneration == 1, "a new report binds at the new generation");
    }

    private static void ReincludeRefusesPending(Fixture fixture)
    {
        var store = fixture.Store;
        var excluded = Pair() with { Exclusions = [new Exclusion(Side.Local, Parent, false)] };
        var plain = store.Pair(PairId)!;
        store.SavePairDecision(plain, 0, excluded, null, false, null);
        var saved = new SyncedEntry(PathNames.Key(File), File, PathNames.DropboxKey(File), File, false, Identity, 1, 0,
            Identity, Hash);
        store.Commit(PairId, StateChange.Empty with { Record = [saved] });
        var pending = PendingNote(saved);
        store.ArmPending(PairId, pending);
        var refused = false;
        try { store.SavePairDecision(excluded, 1, plain, null, false, PathNames.Key(Parent)); }
        catch (InvalidOperationException) { refused = true; }
        Check.Require(refused, "re-include refuses while a pending effect is unsettled");
        Check.Require(store.ReadPairExecution(PairId)!.Value.Generation == 1 &&
            store.SyncedByKey(PairId, Side.Local, PathNames.Key(File)) == saved,
            "the refused re-include keeps the generation and the record");
        store.SettlePending(PairId, pending, StateChange.Empty);
        store.SavePairDecision(excluded, 1, plain, null, false, PathNames.Key(Parent));
        Check.Require(store.SyncedByKey(PairId, Side.Local, PathNames.Key(File)) == saved,
            "after settlement the re-include preserves the record");
    }

    private static PairConfig Pair() => new(PairId, PairId, LocalRoot, RemoteRoot, Connection, [], [],
        FirstRunPolicy.NewestWins, true, null,
        new RootBinding(LocalRoot, RemoteRoot, Connection, new DirectoryIdentity(Volume, 1, null),
            new DropboxRootIdentity(Account, Domain, Domain, Root)));

    private static SyncAction FileAction(string path) => new(ActionKind.Upload,
        new Row(PathNames.Key(path), LocalEntry(path, false), RemoteEntry(path, false), null, [], null, Neutral));

    private static SyncAction FolderAction(string path) => new(ActionKind.DeferFolder,
        new Row(PathNames.Key(path), LocalEntry(path, true), RemoteEntry(path, true), null, [], null, Neutral));

    private static Entry LocalEntry(string path, bool folder) => new(path, folder, 0, Stamp, Identity, "", "", null);

    private static Entry RemoteEntry(string path, bool folder) =>
        new(path, folder, 0, Stamp, OtherIdentity, "rev", Hash, null);

    private static PendingEffect PendingNote(SyncedEntry? saved) => new(PendingVersion, Guid.NewGuid().ToString(), 0,
        PendingKind.LocalDelete, PendingPhase.Armed, File, new RemovalProof(LocalEntry(File, false), Domain, Hash),
        saved, null, null, null, null, false, null);

    private static void WithFixture(Action<Fixture> body)
    {
        var directory = Path.Combine(Path.GetTempPath(), "syncshark-live-fence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var fixture = new Fixture(Path.Combine(directory, "state.db"));
            body(fixture);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly IssueSubject EpisodeSubject = new([new IssueItem(Side.Local, File, Identity)], null);
        private readonly SourceProvenance _provenance;
        private LiveCoverage _coverage;
        private int _cursor;

        internal Fixture(string database)
        {
            Store = StateStore.Open(database, IsRegistered);
            Store.SavePair(Pair());
            var source = Store.ReadSource(PairId, Side.Dropbox)!.Value;
            _provenance = new SourceProvenance(Account, Domain, Root, RemoteRoot, true, true, true, true, 0, source.Epoch);
            Store.AdvanceSource(PairId, Side.Dropbox, 0, source.Epoch, source.Revision, source.Provenance, source.Cursor,
                source.Epoch, source.Revision, _provenance, Cursor, source.Obligation);
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = database }.ToString()))
            {
                StateStore.OpenConnection(connection);
                using var clear = connection.CreateCommand();
                clear.CommandText = "UPDATE source_state SET repair_reason=NULL";
                clear.ExecuteNonQuery();
            }
            var local = Store.ReadSource(PairId, Side.Local)!.Value;
            var position = new JournalPosition(Volume, 0);
            var through = Store.AcquireJournalPage(VolumeKey, null, position,
                [(PairId, 0, local.Epoch, local.Revision, false)],
                [(PairId, new ChangeNotice(Side.Local, ChangeKind.Changed, File, null, Identity, null, true, false))])!.Value;
            Selection = Store.SelectWork(PairId, 0, new PathScope(Pair(), [])) ?? throw new InvalidOperationException("selection");
            _coverage = Store.CaptureLiveCoverage(Selection,
                new LocalCatchUp(PairId, 0, local.Epoch, new NtfsUsnCheckpoint(Volume, 0, 0, 0), through),
                new DropboxCatchUp(PairId, 0, source.Epoch, Cursor, through));
        }

        internal StateStore Store { get; }
        internal WorkSelection Selection { get; }
        internal long Generation { get; set; }
        internal ItemRetryToken? Authority { get; set; }
        internal List<string> Problems { get; } = [];
        private Guid Attempt { get; } = Guid.NewGuid();

        private bool IsRegistered(Guid attempt, string pair, long generation, IssueSubject subject,
            ItemRetryToken? expected) => attempt == Attempt && pair == PairId && generation == Generation &&
            expected == Authority;

        internal (StateStore Store, Guid Attempt, IssueSubject Subject) Episodes() => (Store, Attempt, EpisodeSubject);

        internal void Expect(IReadOnlyList<LiveEffectScope> footprint, LiveAdmissionStaleReason? expected, string what)
        {
            LiveAdmissionStaleReason? observed = null;
            try { Store.ValidateLiveCoverage(Selection, _coverage, footprint); }
            catch (LiveAdmissionStaleException error) { observed = error.Reason; }
            if (observed != expected)
                Problems.Add(what + ": expected " + (expected?.ToString() ?? "admission") +
                    ", observed " + (observed?.ToString() ?? "admission"));
        }

        internal WorkSelection PeerSelection() =>
            PendingWork.ByIdentity(Store, Side.Dropbox, Domain, OtherIdentity).Single();

        internal void CompletePeer(bool expectRemaining)
        {
            Store.CompleteWork(Selection, _coverage.Peer);
            Check.Require(PendingWork.ByIdentity(Store, Side.Dropbox, Domain, OtherIdentity).Any() == expectRemaining,
                "completion retires only the consumed peer");
            Check.Require(!PendingWork.ByIdentity(Store, Side.Local, VolumeKey, Identity).Any(),
                "completion retires the selected membership");
        }

        internal void NextOpportunitySettlesKeptRow()
        {
            Check.Require(Store.SelectWork(PairId, 0, new PathScope(Pair(), [])) is null,
                "A43: the refused opportunity's finite pass ends");
            Store.EndWorkPass(PairId);
            var kept = Store.SelectWork(PairId, 0, new PathScope(Pair(), [])) ?? throw new InvalidOperationException("A43: kept row selection");
            Store.CompleteWork(kept, null);
            Check.Require(!PendingWork.ByIdentity(Store, Side.Dropbox, Domain, OtherIdentity).Any(),
                "A43: the next opportunity settles the kept row");
        }

        internal void ObservePeer() => _coverage = _coverage with
        {
            Peer = new SelectionPeer(PathNames.KeyFor(Side.Dropbox, File), Store.ReadWorkWatermark())
        };

        internal Action Hold(string path)
        {
            Store.Commit(PairId, StateChange.Empty with
            {
                Raise = [(IssueKind.Tie, TextKey.TieEqualTimes, path,
                    new IssueSubject([new IssueItem(Side.Dropbox, path, OtherIdentity)], null))]
            });
            return () => Store.Commit(PairId, StateChange.Empty with { Close = [(path, [IssueKind.Tie])] });
        }

        internal Action Evidence(string path, string? oldPath)
        {
            var source = Store.ReadSource(PairId, Side.Dropbox)!.Value;
            var next = Cursor + (++_cursor).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Store.AcquireDropboxPage(PairId, 0, source.Epoch, source.Revision, _provenance, source.Cursor,
                [new ChangeNotice(Side.Dropbox, oldPath is null ? ChangeKind.Changed : ChangeKind.Renamed, path, oldPath,
                    OtherIdentity, null, false, false)], next);
            return () =>
            {
                foreach (var selection in PendingWork.ByIdentity(Store, Side.Dropbox, Domain, OtherIdentity).ToArray())
                    Store.CompleteWork(selection, null);
            };
        }

        internal Action Pending()
        {
            var note = PendingNote(null);
            Store.ArmPending(PairId, note);
            return () => Store.SettlePending(PairId, note, StateChange.Empty);
        }

        public void Dispose() => Store.Dispose();
    }
}

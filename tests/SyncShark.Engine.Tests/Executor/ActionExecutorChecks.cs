using System.Runtime.CompilerServices;
using System.Text;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Planner;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Checks for ActionExecutor (`--engine-checks syncer`), written by ActionExecutor's writer.</summary>
public static class ActionExecutorChecks
{
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    public static void AttemptCost()
    {
        // Two sizes exercise linear growth; the smaller is the diagnosed saved-row population.
        var saved = new SyncedEntry("ABSENT", "Absent", "absent", "Absent", false, "local-id", 0, 0, "remote-id", "");
        var history = new Row(saved.LocalKey, null, null, saved, [], null,
            new RowContext(false, false, false, null, false, false, false, false));
        var current = history with { Local = new Entry("Absent", false, 0, default, "local-id", "", "", null) };
        var subject = typeof(ActionExecutor).GetMethod("Subject", System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static)!.CreateDelegate<Func<Row, IssueSubject>>();
        var owed = typeof(ActionExecutor).GetMethod("Owed", System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static)!.CreateDelegate<Func<Row, SyncAction?, IssueSubject.FirstRunDeletionKind>>();
        foreach (var path in new[] { "capture", "failure-core", "failure-register" })
        foreach (var row in new[] { current, history, history with { Synced = null } })
        foreach (var count in new[] { 990_469, 1_980_938 })
        foreach (var compare in new[] { false, true })
        {
            foreach (var candidate in new[] { false, true })
            {
                IssueSubject Capture()
                {
                    var result = candidate ? ActionExecutor.AttemptSubject(row) : subject(row);
                    return !candidate && path != "failure-core"
                        ? result with { FirstRunDeletion = owed(row, null) } : result;
                }
                var previous = Capture();
                for (var warm = 0; warm < 1000; warm++) Capture();
                var retainedBefore = GC.GetTotalMemory(true);
                var sampler = new Run.CallBudgetChecks.Sampler();
                var before = GC.GetAllocatedBytesForCurrentThread();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                for (var index = 0; index < count; index++)
                {
                    var next = Capture();
                    if (compare)
                    {
                        Check.Require(previous.HasSameItems(next), "equal attempt identity survives admission comparison");
                        Check.Require(previous.Items.Intersect(next.Items).Any() == (next.Items.Count != 0),
                            "active overlap comparison preserves the subject footprint");
                    }
                    previous = next;
                }
                var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                var elapsed = clock.Elapsed.TotalMilliseconds;
                // Fixed involved cohort: an upper-bound scenario where all recorded open issues retain authority.
                var retained = new IssueSubject[461];
                for (var index = 0; index < retained.Length; index++) retained[index] = Capture();
                var peak = sampler.Stop();
                var retainedBytes = GC.GetTotalMemory(true) - retainedBefore;
                Console.WriteLine($"attempt-cost path={path} row={(row.Local is not null ? "current" : row.Synced is not null ? "saved" : "empty")} count={count} compare={compare} candidate={candidate} bytes={bytes} milliseconds={elapsed:F3} retained-subjects={retained.Length} retained-bytes={retainedBytes} peak-managed={peak.Managed} peak-working-set={peak.WorkingSet} owed-per-call={(candidate || path != "failure-core" ? 1 : 0)}");
                GC.KeepAlive(retained);
                GC.KeepAlive(previous);
                Array.Clear(retained);
            }
        }
    }

    private static void CaptureAttemptCases()
    {
        var fresh = new IssueSubject([new IssueItem(Side.Local, "File.txt", "local-id")], null);
        var retained = fresh with { Incoming = Side.Local };
        var candidate = new ItemRetryCandidate(new ItemRetryToken(1, "episode", 1, 1, 0, 0, retained),
            "pair-a", ItemRetryDispatch.LocalBranch, null, null);
        foreach (var expected in new ItemRetryCandidate?[] { null, candidate })
        {
            using var store = new CaptureAttemptStore(expected);
            var absent = ActionExecutor.CaptureAttempt(store, "pair-a", 0, null, fresh);
            Check.Require(ReferenceEquals(absent.Subject, fresh) && absent.Expected is null && store.Reads == 0,
                "C2: no Local path preserves the fresh subject without reading retry state");
            var captured = ActionExecutor.CaptureAttempt(store, "pair-a", 0, "File.txt", fresh);
            Check.Require(ReferenceEquals(captured.Subject, expected is null ? fresh : retained) &&
                ReferenceEquals(captured.Expected, expected) && store.Reads == 1 &&
                store.Request == ("pair-a", 0L, "File.txt", fresh),
                "C2: one exact retry lookup preserves the retained subject or the fresh fallback");
        }
    }

    private sealed class CaptureAttemptStore(ItemRetryCandidate? expected) : ForwardingStateStore(new InMemoryStateStore())
    {
        internal int Reads;
        internal (string Pair, long Generation, string Path, IssueSubject Subject) Request;
        public override ItemRetryCandidate? ReadItemRetryForSubject(string pairId, long generation,
            string path, IssueSubject subject)
        {
            Reads++;
            Request = (pairId, generation, path, subject);
            return expected;
        }
    }

    private static async Task RunAsync()
    {
        CaptureAttemptCases();
        await FailureTextAsync();
        await FolderReplacementKeepsChildrenAsync();
        await CopiesAsync();
        await CopyParentPreparationAsync();
        await DeletesAndMovesAsync();
        await FreshRemovalProofAsync();
        await UnknownRemovalProofAsync();
        await UnreadableAsync();
        await CommitRoutingAsync();
        await AsyncDurabilityAsync();
        await CommittedIssueAsync();
        await UploadStagedAsync();
        await FirstRunUnresolvedAsync();
        await FirstRunNoticeAsync();
        await FirstRunFailureAsync();
        await FirstRunSuccessAsync();
        await ItemAttemptAsync();
        await RegisteredNoticeCallbackAsync();
        await MovesPreserveContentAsync();
        await ClosuresWithoutIssueReadsAsync();
        await OutOfScopeClosuresAsync();
        await RemoveAsync();
        await StateActionsAsync();
        await BilateralForgetAsync();
        await ExclusionClosuresAsync();
        await NoticesAsync();
        await KeepBothAsync();
        await ObserverEffectsAsync();
        await FailuresAsync();
        await InjectedFailuresAsync();
        await CommitFailuresAsync();
        await OccupantReadFailuresAsync();
        await TakenKeepBothLabelAsync();
        Measured();
    }

    private static async Task FailureTextAsync()
    {
        // Every cause passed to ItemFailedException, including the LocalWriter.Map causes.
        foreach (var cause in new[] { TextKey.LocalNameInvalid, TextKey.ReadLocalFailed, TextKey.ReadDropboxFailed,
            TextKey.DropboxRefused, TextKey.RecycleUnavailable, TextKey.RecycleFailed, TextKey.PublishFailed,
            TextKey.ParentMissing, TextKey.DownloadIncomplete, TextKey.FailEarlierReplace, TextKey.MoveFailed })
        foreach (var inner in new Exception?[] { null, new IOException("Original provider detail") })
        {
            using var fixture = new Fixture();
            var entry = new Entry("Item", false, 0, default, "item-id", "", "", null);
            var error = new ItemFailedException(entry.Path, cause, inner);
            Check.Require(!error.Message.Contains('{') &&
                (inner is null || !Text.All[cause].Contains("{Message}") || error.Message.Contains(inner.Message)),
                $"The {cause} exception fills every placeholder and retains the original error.");
            await fixture.Executor.FailAsync(MakeRow(entry, null), null, error, default);
            Check.Require(ReferenceEquals(fixture.Events.Single().Error, error) && fixture.Events.Single().Cause == cause,
                $"The {cause} failure event retains the original exception and typed cause for the logging owner.");
            fixture.Events.Clear();
            ActionExecutor.EmitIssues(fixture.Events.Add, StateChange.Empty with
            {
                Raise = [(IssueKind.Failure, cause, entry.Path, new IssueSubject([], null))]
            });
            Check.Require(!Massdex.SyncShark.Engine.Run.RunLog.Present(fixture.Events.Single()).Detail.Contains('{'),
                $"A {cause} issue without exception details still fills every placeholder.");
        }
    }

    private static async Task FolderReplacementKeepsChildrenAsync()
    {
        foreach (var real in new[] { false, true })
        foreach (var written in Enum.GetValues<Side>())
        {
            var directory = Directory.CreateTempSubdirectory("folder-replacement-").FullName;
            try
            {
                using var fixture = new Fixture();
                var database = Path.Combine(directory, "state.db");
                using IStateStore store = real ? StateStore.Open(database) : new InMemoryStateStore();
                store.SavePair(fixture.Pair);
                var context = fixture.Context with
                {
                    Store = store,
                    Events = e => fixture.RecordEvent(store, e)
                };
                var localRoot = await fixture.Local.EnsureFolderAsync("LocalRoot", default);
                var remoteRoot = await fixture.Dropbox.EnsureFolderAsync("RemoteRoot", default);
                var savedRoot = SyncedEntries.From(localRoot, remoteRoot);
                await fixture.Local.EnsureFolderAsync("Outside", default);
                await fixture.Dropbox.EnsureFolderAsync("Outside", default);
                var children = new List<SyncedEntry>();
                var survivors = new List<(Side Side, Entry Entry)>();
                foreach (var (localPath, remotePath, liveSide) in new[]
                {
                    ("LocalRoot/Inside", "RemoteRoot/Inside", Other(written)),
                    ("LocalRoot/CrossLocal", "Outside/CrossLocal", Side.Dropbox),
                    ("Outside/CrossRemote", "RemoteRoot/CrossRemote", Side.Local)
                })
                {
                    var local = await SeedAsync(fixture.Local, localPath, false, "child-content");
                    var remote = await SeedAsync(fixture.Dropbox, remotePath, false, "child-content");
                    children.Add(SyncedEntries.From(local, remote));
                    survivors.Add((liveSide, liveSide == Side.Local ? local : remote));
                    await context.For(Other(liveSide)).DeleteAsync(liveSide == Side.Local ? remote : local, null, default);
                }
                store.Commit(fixture.Pair.PairId, StateChange.Empty with { Record = [savedRoot, .. children] });
                var oldRoot = written == Side.Local ? localRoot : remoteRoot;
                await context.For(written).DeleteAsync(oldRoot, null, default);
                var losing = await SeedAsync(context.For(written), oldRoot.Path, false, "losing-file");
                var row = MakeRow(written == Side.Local ? losing : localRoot,
                    written == Side.Dropbox ? losing : remoteRoot) with
                {
                    Synced = savedRoot,
                    LocalHash = written == Side.Local ? await fixture.Local.ContentHashAsync(losing, default) : null
                };
                var replacement = new SyncAction(ActionKind.ReplaceKind, row, Target: oldRoot.Path, Side: written);
                ((FakeSide)context.For(written)).FailNext(oldRoot.Path,
                    new ItemFailedException(oldRoot.Path, TextKey.FailChanged, null));
                var refused = await ExecutorFor(context).ExecuteAsync(replacement, null, default);
                Check.Require(refused.Effect != EffectKind.Replaced && children.All(child =>
                    store.SyncedByKey(fixture.Pair.PairId, Side.Local, child.LocalKey) == child),
                    "A refused folder-winning replacement leaves every child binding unchanged.");
                using (var cancel = new CancellationTokenSource())
                {
                    cancel.Cancel();
                    var cancelled = false;
                    try { await ExecutorFor(context).ExecuteAsync(replacement, null, cancel.Token); }
                    catch (OperationCanceledException) { cancelled = true; }
                    Check.Require(cancelled && children.All(child =>
                        store.SyncedByKey(fixture.Pair.PairId, Side.Local, child.LocalKey) == child),
                        "Cancellation before folder creation preserves every child binding.");
                }
                var allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
                var outcome = await ExecutorFor(context).ExecuteAsync(replacement, null, default);
                Console.WriteLine($"folder-replacement real={real}; side={written}; allocated={GC.GetTotalAllocatedBytes(precise: true) - allocationBefore}");
                Check.Require(outcome.Effect == EffectKind.Replaced && outcome.Raised is null &&
                    await context.For(written).ReadOneAsync(oldRoot.Path, default) is { IsFolder: true } &&
                    store.SyncedByKey(fixture.Pair.PairId, written, Key(written, oldRoot.Path)) is { IsFolder: true } &&
                    store.Pending(fixture.Pair.PairId) is NoPending,
                    "Folder-winning replacement completes before child history is checked.");
                foreach (var child in children)
                {
                    Check.Require(store.SyncedByKey(fixture.Pair.PairId, Side.Local, child.LocalKey) == child &&
                        store.SyncedByKey(fixture.Pair.PairId, Side.Dropbox, child.DropboxKey) == child,
                        $"Folder-winning replacement retains same-area and cross-moved child history ({real}, {written}, {child.LocalPath}).");
                    if (real)
                    {
                        using var reopened = StateStore.Open(database);
                        Check.Require(reopened.SyncedByKey(fixture.Pair.PairId, Side.Local, child.LocalKey) == child,
                            "Retained child history survives reopening real storage.");
                    }
                }
                foreach (var (side, entry) in survivors)
                    Check.Require(await context.For(side).ReadOneAsync(entry.Path, default) == entry &&
                        await context.For(side).ContentHashAsync(entry, default) ==
                        children.Single(child => child.LocalPath == entry.Path || child.DropboxPath == entry.Path).DropboxContentHash,
                        "Replacing the parent leaves every surviving child and its content untouched.");
                var deleted = children[1];
                var observed = await new Observer(context, new Parents(context)).ObserveAsync(
                    Side.Local, deleted.LocalPath, default);
                var action = Decide.For(observed, context.Pair.FirstRun, DateTimeOffset.UtcNow, false);
                Check.Require(action.Kind == ActionKind.DeleteDropbox,
                    "Normal sync remembers the Local child deletion rather than restoring it.");
                var removed = await ExecutorFor(context).ExecuteAsync(action, null, default);
                Check.Require(removed.Effect == EffectKind.DeletedDropbox &&
                    await context.Dropbox.ReadOneAsync(deleted.DropboxPath, default) is null &&
                    await context.Local.ReadOneAsync(deleted.LocalPath, default) is null &&
                    store.SyncedByKey(fixture.Pair.PairId, Side.Local, deleted.LocalKey) is null,
                    "The retained binding reaches normal deletion settlement without recreating the Local child.");
            }
            finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
        }
    }

    /// <summary>Measures root-only versus subtree retirement with fixed affected rows and growing saved history.</summary>
    public static void FileSizedForgetCosts()
    {
        // W08 supplies regression sizes; the million-row point checks the representative saved-row workload.
        var previous = new Dictionary<bool, StatementCost>();
        foreach (var background in new[] { 5_110, 20_440, 1_000_000 })
        foreach (var exact in new[] { false, true })
        {
            var directory = Directory.CreateTempSubdirectory("file-forget-cost-").FullName;
            try
            {
                using var fixture = new Fixture();
                var database = Path.Combine(directory, "state.db");
                using (var store = StateStore.Open(database))
                {
                    store.SavePair(fixture.Pair);
                    var root = new SyncedEntry(PathNames.Key("root"), "root", PathNames.DropboxKey("remote"), "remote",
                        false, "local", 1, 0, "remote", "hash");
                    SyncedEntry[] rows = [root,
                        root with { LocalPath = "root/inside", LocalKey = PathNames.Key("root/inside"),
                            DropboxPath = "remote/inside", DropboxKey = PathNames.DropboxKey("remote/inside") },
                        root with { LocalPath = "root/cross", LocalKey = PathNames.Key("root/cross"),
                            DropboxPath = "outside/local", DropboxKey = PathNames.DropboxKey("outside/local") },
                        root with { LocalPath = "outside/remote", LocalKey = PathNames.Key("outside/remote"),
                            DropboxPath = "remote/cross", DropboxKey = PathNames.DropboxKey("remote/cross") }];
                    store.Commit(fixture.Pair.PairId, StateChange.Empty with { Record = rows });
                }
                using var probe = new CostProbe(database);
                // Fixture-only bulk setup, as in StorageCostChecks.SeedRetryPairsSql; no million-row managed collection.
                probe.Execute("WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<$count) " +
                    "INSERT INTO synced_entry(pair_id,local_path,is_folder,local_id,local_size,local_mtime,dropbox_id,dropbox_hash) " +
                    "SELECT (SELECT id FROM pair WHERE pair_id=$p),'background/'||i,0,'l'||i,1,0,'d'||i,'hash' FROM n;",
                    ("$count", background), ("$p", fixture.Pair.PairId));
                long Bytes(string suffix) => File.Exists(database + suffix) ? new FileInfo(database + suffix).Length : 0;
                var walBefore = Bytes("-wal");
                var databaseBefore = Bytes("");
                probe.Begin();
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var local = probe.Execute(exact ? StateSchema.DeleteSyncedLocalKey : StateSchema.DeleteSyncedSubtreeLocal,
                    ("$p", fixture.Pair.PairId), ("$k", PathNames.Key("root")), (StateSchema.ParamRetained, "[]"));
                var remote = probe.Execute(exact ? StateSchema.DeleteSyncedDropboxKey : StateSchema.DeleteSyncedSubtreeDropbox,
                    ("$p", fixture.Pair.PairId), ("$k", PathNames.DropboxKey("remote")), (StateSchema.ParamRetained, "[]"));
                allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                var cost = local + remote;
                probe.Commit();
                Check.Require(cost.ProbePrepares == (exact ? 2 : 8) && cost.ChangedRows == (exact ? 1 : 8),
                    "Exact SQL deletes only the root; subtree SQL spends and deletes exactly four bindings at every background size.");
                if (previous.TryGetValue(exact, out var before))
                    Check.Require(cost.VmSteps == before.VmSteps && cost.PathKeyCalls == before.PathKeyCalls,
                        "Fixed involved rows cost the same VM work and path-key callbacks as background history grows.");
                previous[exact] = cost;
                Console.WriteLine($"file-forget background={background}; exact={exact}; involved=4; {cost}; probe-allocated={allocated}; " +
                    $"commits=1; database-growth={Bytes("") - databaseBefore}; wal-growth={Bytes("-wal") - walBefore}");
            }
            finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
        }
    }

    private static async Task CopiesAsync()
    {
        foreach (var kind in new[] { ActionKind.Upload, ActionKind.Download, ActionKind.EnsureDropboxFolder,
                     ActionKind.EnsureLocalFolder, ActionKind.ReplaceKind })
        foreach (var replacing in new[] { false, true })
        foreach (var replaceSide in new[] { Side.Local, Side.Dropbox })
        foreach (var folderWinner in new[] { false, true })
        foreach (var closeIssues in new[] { false, true })
        {
            if (kind != ActionKind.ReplaceKind && (replaceSide != Side.Local || folderWinner)) continue;
            using var fixture = new Fixture();
            var written = kind switch
            {
                ActionKind.Upload or ActionKind.EnsureDropboxFolder => Side.Dropbox,
                ActionKind.ReplaceKind => replaceSide,
                _ => Side.Local
            };
            var folder = kind is ActionKind.EnsureLocalFolder or ActionKind.EnsureDropboxFolder ||
                kind == ActionKind.ReplaceKind && folderWinner;
            var source = await SeedAsync(fixture.Context.For(Other(written)), "Source", folder, "new");
            Entry? old = replacing
                ? await SeedAsync(fixture.Context.For(written), "Target", kind == ActionKind.ReplaceKind ? !folder : folder, "old")
                : null;
            var row = MakeRow(written == Side.Local ? old : source, written == Side.Dropbox ? old : source);
            if (old != null) row = row with { Synced = SyncedEntries.From(row.Local!, row.Dropbox!) };
            else if (kind == ActionKind.ReplaceKind)
                row = row with { Synced = written == Side.Local ? Saved("Target", "Source") : Saved("Source", "Target") };
            // A Dropbox folder counterpart has no saved content hash; the losing local file's own hash is the proof.
            if (old is { IsFolder: false } && row.Dropbox is { IsFolder: true })
                row = row with { LocalHash = await fixture.Local.ContentHashAsync(old, CancellationToken.None) };
            if (row.Synced != null) fixture.Store.Commit(fixture.Pair.PairId, StateChange.Empty with { Record = [row.Synced] });
            if (kind == ActionKind.ReplaceKind)
            {
                var child = Saved("Target/Child", "Source/Child");
                if (written == Side.Dropbox) child = Saved("Source/Child", "Target/Child");
                fixture.Store.Commit(fixture.Pair.PairId, StateChange.Empty with { Record = [child] });
            }
            RaiseAll(fixture, row);
            var priorIssues = fixture.Store.Issues(fixture.Pair.PairId);
            var info = replacing && !folder ? TextKey.InfoNewestWins : (TextKey?)null;
            var action = new SyncAction(kind, row, Target: "Target", InfoAfter: info,
                Side: kind == ActionKind.ReplaceKind ? written : null);
            if (kind == ActionKind.ReplaceKind && written == Side.Dropbox && old is { IsFolder: true })
            {
                var refused = false;
                try { await fixture.Executor.ExecuteAsync(action, null, CancellationToken.None, closeIssues); }
                catch (LiveAdmissionStaleException) { refused = true; }
                Check.Require(refused &&
                    await fixture.Context.For(written).ReadOneAsync("Target", CancellationToken.None) == old &&
                    fixture.Events.All(e => e.Kind != RunEventKind.Effect),
                    "A Dropbox folder kind replacement refuses inside the caller's footprint instead of removing it.");
                continue;
            }
            var outcome = await fixture.Executor.ExecuteAsync(action, null, CancellationToken.None, closeIssues);
            var result = await fixture.Context.For(written).ReadOneAsync("Target", CancellationToken.None);
            Check.Require(result != null && result.IsFolder == folder, nameof(CopiesAsync) + $" {kind} {written} replacing={replacing} folder={folder} result={result} outcome={outcome} issues=" + string.Join("|", fixture.Store.Issues(fixture.Pair.PairId).Select(i => i.Kind + ":" + i.Cause + ":" + i.Path)));
            var record = fixture.Store.SyncedByKey(fixture.Pair.PairId, written, Key(written, "Target"));
            Check.Require(record != null && (written == Side.Local ? record.LocalPath : record.DropboxPath) == "Target", nameof(SyncedEntry));
            Check.Require(fixture.Store.Pending(fixture.Pair.PairId) is NoPending, nameof(NoPending));
            Check.Require(fixture.Admission.Admitted == 0 && fixture.Admission.Completed >= 1, nameof(IAdmission));
            var expectedEffect = kind == ActionKind.ReplaceKind ? EffectKind.Replaced : folder ? EffectKind.FolderCreated :
                written == Side.Local ? EffectKind.Downloaded : EffectKind.Uploaded;
            Check.Require(outcome.Effect == expectedEffect && outcome.Raised == (info == null ? (IssueKind?)null : IssueKind.Info), nameof(SyncOutcome));
            var issues = fixture.Store.Issues(fixture.Pair.PairId);
            Check.Require(closeIssues
                ? !issues.Any(issue => issue.Kind is IssueKind.Failure or IssueKind.Tie or IssueKind.NameClash)
                : priorIssues.All(prior => issues.Any(issue => issue.Id == prior.Id)), nameof(StateChange.Close));
            Check.Require(fixture.Events.Any(e => e.Kind == RunEventKind.Effect && e.Path == "Target" &&
                Massdex.SyncShark.Engine.Run.RunLog.Present(e).Outcome == written.ToString() && Massdex.SyncShark.Engine.Run.RunLog.Present(e).What == expectedEffect.ToString()), nameof(RunEvent));
            if (!folder)
                Check.Require(await fixture.Context.For(written).ContentHashAsync(result!, CancellationToken.None) ==
                    await fixture.Context.For(Other(written)).ContentHashAsync(source, CancellationToken.None), nameof(ISide.ContentHashAsync));
            if (kind == ActionKind.ReplaceKind && row.Synced != null)
                Check.Require((fixture.Store.SyncedByKey(fixture.Pair.PairId, written, Key(written, "Target/Child")) is not null) == folder,
                    "Folder-winning replacement retains descendants; file-winning replacement retires the removed folder's descendants.");
        }
    }

    private static async Task CopyParentPreparationAsync()
    {
        foreach (var written in Enum.GetValues<Side>())
        foreach (var mode in new[] { "existing", "missing", "replace", "disappeared" })
        {
            using var fixture = new Fixture();
            var destination = fixture.Context.For(written);
            var source = await SeedAsync(fixture.Context.For(Other(written)), "Source", false, "new");
            Entry? expected = null;
            if (mode != "missing")
            {
                await destination.EnsureFolderAsync("Parent", default);
                expected = await SeedAsync(destination, "Parent/Target", mode == "replace", "old");
            }
            var row = MakeRow(written == Side.Local ? expected : source, written == Side.Dropbox ? expected : source);
            if (expected is not null)
            {
                row = row with { Synced = SyncedEntries.From(row.Local!, row.Dropbox!) };
                fixture.Store.Commit(fixture.Pair.PairId, StateChange.Empty with { Record = [row.Synced] });
            }
            if (mode == "disappeared")
            {
                var parent = await destination.ReadOneAsync("Parent", default);
                await destination.DeleteAsync(expected!, null, default);
                await destination.DeleteAsync(parent!, null, default);
            }
            var counter = new ReadCounter(destination);
            var context = written == Side.Local ? fixture.Context with { Local = counter }
                : fixture.Context with { Dropbox = counter };
            var kind = mode == "replace" ? ActionKind.ReplaceKind
                : written == Side.Local ? ActionKind.Download : ActionKind.Upload;
            SyncOutcome? outcome = null;
            var stale = false;
            try
            {
                outcome = await ExecutorFor(context).ExecuteAsync(new SyncAction(kind, row,
                    Target: "Parent/Target", Side: mode == "replace" ? written : null), null, default);
            }
            catch (LiveAdmissionStaleException) { stale = true; }
            catch (ChangedUnderneathException) when (mode == "disappeared") { stale = true; }
            var dropboxFolderReplace = mode == "replace" && written == Side.Dropbox;
            Check.Require(dropboxFolderReplace
                    ? stale && counter.Reads == 0 && counter.Deletes == 0 && counter.Publishes == 0
                    : mode == "missing" ? counter.Reads > 0 : counter.Reads == 0,
                "Only a missing target prepares its parent folders; a Dropbox folder replacement leaves the caller's footprint.");
            var result = await destination.ReadOneAsync("Parent/Target", default);
            if (dropboxFolderReplace)
                Check.Require(result == expected && fixture.Events.Count == 0,
                    "Refusing a Dropbox folder kind replacement keeps the observed folder and reports no effect.");
            else if (mode == "disappeared")
            {
                Check.Require(stale && outcome is null && result is null && fixture.Events.Count == 0 &&
                    await destination.ReadOneAsync("Parent", default) is null &&
                    fixture.Store.SyncedByKey(fixture.Pair.PairId, written, Key(written, "Parent/Target")) == row.Synced,
                    "A target and parent disappearing propagate freshness to the caller without an effect, notice, parent creation or saved-link change.");
            }
            else
            {
                Check.Require(result is { IsFolder: false } && outcome!.Raised is null &&
                    await destination.ContentHashAsync(result, default) ==
                    await fixture.Context.For(Other(written)).ContentHashAsync(source, default),
                    "Existing overwrites, missing parents and kind replacements still publish the source content.");
            }
        }
    }

    private static async Task UnknownRemovalProofAsync()
    {
        foreach (var (kind, missingVolume) in new[]
        {
            (ActionKind.DeleteLocal, false), (ActionKind.DeleteLocal, true), (ActionKind.Download, true)
        })
        {
            using var fixture = new Fixture();
            var local = await SeedAsync(fixture.Local, "unknown-proof", false, "content");
            var remote = await SeedAsync(fixture.Dropbox, local.Path, false, "content");
            var saved = SyncedEntries.From(local, remote);
            if (missingVolume) fixture.Recovery.Evidence = new DriveEvidence(null, null, null);
            else saved = saved with { DropboxContentHash = "" };
            fixture.Store.Commit(fixture.Pair.PairId, StateChange.Empty with { Record = [saved] });
            var counted = new ReadCounter(fixture.Local);
            fixture.Recovery.Hash = (_, _) => throw new InvalidOperationException("Refused evidence must not adopt current bytes.");
            var outcome = await ExecutorFor(fixture.Context with { Local = counted }).ExecuteAsync(
                new SyncAction(kind, MakeRow(local, kind == ActionKind.Download ? remote : null) with { Synced = saved },
                    Target: local.Path), null, default);
            var issue = fixture.Store.Issues(fixture.Pair.PairId).Single();
            var notice = fixture.Events.Single();
            Check.Require(counted.Deletes == 0 && counted.Publishes == 0 && counted.Opens == 0 &&
                await fixture.Local.ReadOneAsync(local.Path, default) == local &&
                fixture.Store.SyncedByKey(fixture.Pair.PairId, Side.Local, PathNames.Key(local.Path)) == saved &&
                outcome.Effect is null && issue.Kind == IssueKind.Failure && issue.Cause == TextKey.FailChanged &&
                issue.Path == local.Path && notice.Kind == RunEventKind.Issue && notice.IssueKind == IssueKind.Failure &&
                notice.Cause == TextKey.FailChanged && notice.Path == local.Path,
                "AJ-01: unknown proof keeps the file and binding, raises the original FailChanged notice, and performs no hash, removal, publication or Wait.");
        }
    }

    private static async Task FreshRemovalProofAsync()
    {
        foreach (var mode in new[] { "success", "read-failed", "changed" })
        {
            using var fixture = new Fixture();
            var local = await SeedAsync(fixture.Local, "fresh-removal", false, "content");
            var reads = 0;
            var race = new ChangedUnderneathException(local.Path);
            fixture.Recovery.Hash = (expected, ct) =>
            {
                reads++;
                Check.Require(expected == local, "Ordinary removal proves the captured entry.");
                return mode switch
                {
                    "changed" => Task.FromException<string>(race),
                    "read-failed" => Task.FromException<string>(new ItemFailedException(expected.Path, TextKey.ReadLocalFailed, null)),
                    _ => fixture.Local.ContentHashAsync(expected, ct)
                };
            };
            ChangedUnderneathException? propagated = null;
            try
            {
                await fixture.Executor.ExecuteAsync(new SyncAction(ActionKind.DeleteLocal, MakeRow(local, null)),
                    null, default);
            }
            catch (ChangedUnderneathException error) { propagated = error; }
            Check.Require(reads == 1 && ((await fixture.Local.ReadOneAsync(local.Path, default)) is not null) == (mode != "success"),
                "Missing ordinary proof hashes once; a failed read never removes the file.");
            var issues = fixture.Store.Issues(fixture.Pair.PairId);
            if (mode == "changed")
                Check.Require(ReferenceEquals(propagated, race) && IssuePolicy.CanRetryChanged(propagated!) &&
                    issues.Count == 0 && fixture.Events.Count == 0,
                    "A checked proof read race reaches the item retry owner unchanged, without deletion or a premature notice.");
            else
                Check.Require(propagated is null && (mode != "read-failed" ||
                    issues.Any(issue => issue.Kind == IssueKind.Failure && issue.Cause == TextKey.ReadLocalFailed)),
                    "A proof read failure remains the item's failure cause.");
        }
    }

    private static async Task DeletesAndMovesAsync()
    {
        foreach (var kind in new[] { ActionKind.DeleteLocal, ActionKind.DeleteDropbox, ActionKind.MoveLocal,
                     ActionKind.MoveDropbox, ActionKind.MoveUnlinked })
        foreach (var folder in new[] { false, true })
        foreach (var unlinkedSide in new[] { Side.Local, Side.Dropbox })
        foreach (var closeIssues in new[] { false, true })
        {
            if (kind != ActionKind.MoveUnlinked && unlinkedSide != Side.Local) continue;
            var deleting = kind is ActionKind.DeleteLocal or ActionKind.DeleteDropbox;
            if (deleting && folder) continue;
            using var fixture = new Fixture();
            var local = await SeedAsync(fixture.Local, "Local", folder, "local");
            var dropbox = await SeedAsync(fixture.Dropbox, "Remote", folder, "remote");
            var saved = SyncedEntries.From(local, dropbox);
            var row = MakeRow(local, dropbox) with { Synced = kind == ActionKind.MoveUnlinked ? null : saved };
            if (row.Synced != null)
                fixture.Store.Commit(fixture.Pair.PairId, StateChange.Empty with { Record = [saved] });
            if (folder && row.Synced != null)
                fixture.Store.Commit(fixture.Pair.PairId, StateChange.Empty with { Record = [Saved("Local/Child", "Remote/Child")] });
            RaiseAll(fixture, row);
            var priorIssues = fixture.Store.Issues(fixture.Pair.PairId);
            var written = kind switch
            {
                ActionKind.DeleteDropbox or ActionKind.MoveDropbox => Side.Dropbox,
                ActionKind.MoveUnlinked => unlinkedSide,
                _ => Side.Local
            };
            var old = written == Side.Local ? local : dropbox;
            var outcome = await fixture.Executor.ExecuteAsync(new SyncAction(kind, row,
                Target: deleting ? null : "Moved", Side: kind == ActionKind.MoveUnlinked ? written : null), null, CancellationToken.None, closeIssues);
            Check.Require(await fixture.Context.For(written).ReadOneAsync(old.Path, CancellationToken.None) == null, nameof(ISide.MoveAsync));
            var issues = fixture.Store.Issues(fixture.Pair.PairId);
            Check.Require(closeIssues ? issues.All(i => i.Kind is IssueKind.Info or IssueKind.Skipped or
                    IssueKind.DriveChange or IssueKind.RootIdentity)
                : priorIssues.SequenceEqual(issues), nameof(StateChange.Close));
            var completions = written == Side.Dropbox ? 1 : 0;
            Check.Require(fixture.Admission.Admitted == 0 && (completions == 0
                ? fixture.Admission.Completed == 0 : fixture.Admission.Completed >= 1), nameof(IAdmission));
            if (deleting)
                Check.Require(fixture.Store.SyncedByKey(fixture.Pair.PairId, Side.Local, saved.LocalKey) == null, nameof(StateChange.ForgetSelf));
            else
            {
                Check.Require(await fixture.Context.For(written).ReadOneAsync("Moved", CancellationToken.None) != null, nameof(ISide.MoveAsync));
                var record = fixture.Store.SyncedByKey(fixture.Pair.PairId, written, Key(written, "Moved"));
                Check.Require(kind == ActionKind.MoveUnlinked ? record == null : record != null, nameof(StateChange.Renames));
                if (folder && kind != ActionKind.MoveUnlinked)
                    Check.Require(fixture.Store.SyncedByKey(fixture.Pair.PairId, written, Key(written, "Moved/Child")) != null, nameof(RenameApplied));
            }
            var expectedEffect = deleting ? EffectKinds.Deleted(written) : EffectKinds.Moved(written);
            Check.Require(outcome.Effect == expectedEffect && fixture.Events.Any(e => e.Kind == RunEventKind.Effect &&
                e.Path == (deleting ? old.Path : "Moved") && e.Detail == (deleting ? "" : old.Path) &&
                Massdex.SyncShark.Engine.Run.RunLog.Present(e).What == expectedEffect.ToString() && Massdex.SyncShark.Engine.Run.RunLog.Present(e).Outcome == written.ToString()), nameof(RunEvent));
        }
        using var spelling = new Fixture();
        var l = await SeedAsync(spelling.Local, "Folder", true, "");
        var d = await SeedAsync(spelling.Dropbox, "folder", true, "");
        await spelling.Executor.ExecuteAsync(new SyncAction(ActionKind.MoveDropbox, MakeRow(l, d),
            Target: l.Path, InfoAfter: TextKey.InfoFolderSpelling), null, CancellationToken.None);
        Check.Require(spelling.Store.SyncedByKey(spelling.Pair.PairId, Side.Local, PathNames.Key(l.Path)) != null &&
            spelling.Store.Issues(spelling.Pair.PairId).Single().Cause == TextKey.InfoFolderSpelling, nameof(TextKey.InfoFolderSpelling));
    }

    private static async Task UnreadableAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            foreach (var kind in new[] { ActionKind.DeleteDropbox, ActionKind.ForgetOnly,
                         ActionKind.ReplaceKind, ActionKind.MoveDropbox })
            foreach (var crossLinked in new[] { false, true })
            {
                using var fixture = new Fixture();
                using var store = StateStore.Open(Path.Combine(root, Path.GetRandomFileName()));
                store.SavePair(fixture.Pair);
                var local = await SeedAsync(fixture.Local, "Local", kind == ActionKind.ReplaceKind, "local");
                var dropbox = await SeedAsync(fixture.Dropbox, "Remote", false, "remote");
                var saved = SyncedEntries.From(local, dropbox);
                var child = Saved("Unreadable/Child", "Remote/Child");
                store.Commit(fixture.Pair.PairId, StateChange.Empty with { Record = [saved, child] });
                var row = MakeRow(local, dropbox) with { Synced = saved };
                UnreadableMember.Record(store, fixture.Pair.PairId, crossLinked ? "Unreadable" : local.Path);
                var localCounter = new ReadCounter(fixture.Local);
                var dropboxCounter = new ReadCounter(fixture.Dropbox);
                var admission = new TrackedAdmission();
                var context = fixture.Context with
                {
                    Store = store, Local = localCounter, Dropbox = dropboxCounter,
                    Admission = admission, Events = fixture.Events.Add
                };
                var outcome = await ExecutorFor(context).ExecuteAsync(new SyncAction(kind, row,
                    Target: kind == ActionKind.MoveDropbox ? "Moved" : dropbox.Path,
                    Side: kind == ActionKind.ReplaceKind ? Side.Dropbox : null), null, default);
                var issue = store.Issues(fixture.Pair.PairId).Single(item => item.Kind != IssueKind.Skipped);
                Check.Require(outcome.Raised == IssueKind.Failure && outcome.Effect is null &&
                    issue.Kind == IssueKind.Failure && issue.Cause == TextKey.ReadLocalFailed && issue.Path == local.Path,
                    $"{kind}, cross-linked={crossLinked}: unreadable content raises Failure at the row path.");
                Check.Require(localCounter.Deletes == 0 && dropboxCounter.Deletes == 0 &&
                    localCounter.Moves == 0 && dropboxCounter.Moves == 0 &&
                    localCounter.Publishes == 0 && dropboxCounter.Publishes == 0 &&
                    await fixture.Local.ReadOneAsync(local.Path, default) == local &&
                    await fixture.Dropbox.ReadOneAsync(dropbox.Path, default) == dropbox &&
                    fixture.Events.All(e => e.Kind != RunEventKind.Effect),
                    "Unreadable refusal precedes every delete, move and publication and emits no effect.");
                foreach (var binding in new[] { saved, child })
                    Check.Require(store.SyncedByKey(fixture.Pair.PairId, Side.Local, binding.LocalKey) == binding &&
                        store.SyncedByKey(fixture.Pair.PairId, Side.Dropbox, binding.DropboxKey) == binding,
                        "Unreadable refusal preserves both keys of every saved binding.");
                Check.Require(admission.Success == (kind == ActionKind.ForgetOnly ? (bool?)null : false),
                    "Blocked Dropbox actions report one failed completion; forgetting alone reports none.");
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task CommitRoutingAsync()
    {
        foreach (var firstRun in new[] { false, true })
        foreach (var folder in new[] { false, true })
        foreach (var record in new[] { false, true })
        {
            using var fixture = new Fixture();
            var local = await SeedAsync(fixture.Local, "Local", folder, "same");
            var dropbox = record ? await SeedAsync(fixture.Dropbox, "Remote", folder, "same") : null;
            var row = MakeRow(local, dropbox);
            row = row with { Context = row.Context with { FirstRun = firstRun } };
            using var store = new CommitStore(fixture.Store,
                change => fixture.Store.Commit(fixture.Pair.PairId, change));
            var kind = record ? ActionKind.Record : folder ? ActionKind.EnsureDropboxFolder : ActionKind.Upload;
            var outcome = await ExecutorFor(fixture.Context with { Store = store }).ExecuteAsync(
                new SyncAction(kind, row, Target: record ? null : "Remote"), null, default);
            // CommitRecordAsync owns the file/folder split: a file Record joins the queue, a folder Record commits now
            // (IStateStore.CommitRecordAsync; the caller no longer restates the owner's eligibility rule).
            var queued = record && firstRun;
            var asynchronous = !queued && kind == ActionKind.Upload;
            var plain = !queued && !asynchronous;
            Check.Require(store.RecordCommits == (queued ? 1 : 0) && store.AsyncCommits == (asynchronous ? 1 : 0) &&
                store.Commits == (plain ? 1 : 0) && outcome.Raised is null && outcome.Effect is not null &&
                fixture.Store.SyncedByKey(fixture.Pair.PairId, Side.Local, row.Key) is not null,
                $"{kind}, first-run={firstRun}, folder={folder}: a first-run Record enters CommitRecordAsync, an acknowledged " +
                "Dropbox upload awaits CommitAsync, and every other effect commits synchronously.");
        }
    }

    private static async Task MovesPreserveContentAsync()
    {
        foreach (var side in Enum.GetValues<Side>())
        foreach (var folder in new[] { false, true })
        foreach (var linked in new[] { false, true })
        {
            using var fixture = new Fixture();
            var local = await SeedAsync(fixture.Local, "Local", folder, "edited-local");
            var dropbox = await SeedAsync(fixture.Dropbox, "Remote", folder, "edited-remote");
            var saved = SyncedEntries.From(local, dropbox) with
            {
                LocalId = "prior-local", DropboxId = "prior-remote", LocalSize = 1,
                LocalModifiedTicks = DateTimeOffset.UnixEpoch.AddDays(-1).UtcTicks, DropboxContentHash = "prior-hash"
            };
            var row = MakeRow(local, dropbox) with { Synced = linked ? saved : null };
            if (linked) fixture.Store.Commit(fixture.Pair.PairId, StateChange.Empty with { Record = [saved] });
            StateChange? committed = null;
            using var store = new CommitStore(fixture.Store, change =>
            {
                committed = change;
                fixture.Store.Commit(fixture.Pair.PairId, change);
            });
            await ExecutorFor(fixture.Context with { Store = store }).ExecuteAsync(
                new SyncAction(side == Side.Local ? ActionKind.MoveLocal : ActionKind.MoveDropbox,
                    row, Target: "Moved"), null, CancellationToken.None);
            var afterLocal = side == Side.Local ? local with { Path = "Moved" } : local;
            var afterDropbox = side == Side.Dropbox ? dropbox with { Path = "Moved" } : dropbox;
            var expected = linked ? saved with
            {
                LocalPath = afterLocal.Path, LocalKey = PathNames.Key(afterLocal.Path), LocalId = afterLocal.Id,
                DropboxPath = afterDropbox.Path, DropboxKey = PathNames.DropboxKey(afterDropbox.Path), DropboxId = afterDropbox.Id
            } : SyncedEntries.From(afterLocal, afterDropbox);
            Check.Require(committed?.Renames.Single().After == expected &&
                fixture.Store.SyncedByKey(fixture.Pair.PairId, side, Key(side, "Moved")) == expected,
                "Linked moves preserve unreconciled content stamps in RenameApplied; unlinked spelling moves record current entries.");
        }
    }

    private static async Task ClosuresWithoutIssueReadsAsync()
    {
        foreach (var kind in new[] { ActionKind.Record, ActionKind.None, ActionKind.ForgetOnly })
        foreach (var held in new[] { false, true })
        foreach (var close in new[] { false, true })
        {
            using var fixture = new Fixture();
            var row = MakeRow(await SeedAsync(fixture.Local, "Local", false, "same"),
                await SeedAsync(fixture.Dropbox, "Remote", false, "same"));
            row = row with { Synced = kind == ActionKind.ForgetOnly ? Saved("Local", "Remote") : null,
                Context = row.Context with { Held = held } };
            StateChange? committed = null;
            using var store = new CommitStore(change => committed = change);
            var executor = ExecutorFor(fixture.Context with { Store = store });
            await executor.ExecuteAsync(new SyncAction(kind, row, None: NoneReason.InSync), null, CancellationToken.None, close);
            IReadOnlyList<IssueKind> expected = kind == ActionKind.ForgetOnly ? [IssueKind.Failure]
                : held ? [IssueKind.Failure, IssueKind.Tie, IssueKind.NameClash] : [IssueKind.Failure, IssueKind.NameClash];
            Check.Require(committed != null && (close
                ? committed.Close.Select(c => c.Path).SequenceEqual(new[] { "Local", "Remote" }) &&
                    committed.Close.All(c => c.Kinds.SequenceEqual(expected))
                : committed.Close.Count == 0), "State-only closures need no issue query and use the exact closure kinds.");
        }
    }

    private static async Task OutOfScopeClosuresAsync()
    {
        foreach (var close in new[] { false, true })
        {
            using var fixture = new Fixture();
            var row = MakeRow(await SeedAsync(fixture.Local, "Local", false, "same"),
                await SeedAsync(fixture.Dropbox, "Remote", false, "same"));
            using var store = new CommitStore(_ => throw new InvalidOperationException("Out-of-scope rows commit nothing."));
            var outcome = await ExecutorFor(fixture.Context with { Store = store }).ExecuteAsync(new SyncAction(ActionKind.None,
                row, None: NoneReason.OutOfScope), null, default, close);
            Check.Require(outcome == new SyncOutcome(ActionKind.None, NoneReason.OutOfScope, null, null, false) &&
                fixture.Events.Count == 0 && fixture.Admission.Admitted == 0 && fixture.Admission.Completed == 0,
                "Out-of-scope rows need no issue reads, effects or admission, even when closure is enabled.");
        }
    }

    private static async Task RemoveAsync()
    {
        foreach (var side in Enum.GetValues<Side>())
        foreach (var failure in new[] { "", "delete", "commit", "event", "cancel" })
        {
            using var fixture = new Fixture();
            var entry = await SeedAsync(fixture.Context.For(side), "Folder", true, "");
            var saved = Saved("Folder", "Folder");
            var child = Saved("Folder/Child", "Folder/Child");
            fixture.Store.Commit(fixture.Pair.PairId, StateChange.Empty with { Record = [saved, child] });
            var subject = new IssueSubject([new IssueItem(side, entry.Path, entry.Id)], null);
            fixture.Store.Commit(fixture.Pair.PairId, StateChange.Empty with
            {
                Raise = [(IssueKind.Failure, TextKey.FailChanged, entry.Path, subject)]
            });
            var priorIssue = fixture.Store.Issues(fixture.Pair.PairId).Single();
            var change = StateChange.Empty with
            {
                ForgetSubtrees = [(side, Key(side, entry.Path))],
                Close = [(entry.Path, IssueClosure.AfterEffect)]
            };
            var destination = new ReadCounter(fixture.Context.For(side));
            var error = new ItemFailedException(entry.Path, TextKey.FailChanged, null);
            if (failure == "delete") ((FakeSide)fixture.Context.For(side)).FailNext(entry.Path, error);
            var commits = 0;
            var events = new List<RunEvent>();
            var admission = new TrackedAdmission();
            using var store = new CommitStore(actual =>
            {
                Check.Require(ReferenceEquals(change, actual) && destination.Deletes == 1 &&
                    fixture.Store.Issues(fixture.Pair.PairId).Single() == priorIssue,
                    "Remove deletes first, then commits the supplied forget and closure together.");
                if (failure == "commit") throw error;
                fixture.Store.Commit(fixture.Pair.PairId, actual);
                commits++;
            });
            var context = fixture.Context with { Store = store, Admission = admission,
                Local = side == Side.Local ? destination : fixture.Local,
                Dropbox = side == Side.Dropbox ? destination : fixture.Dropbox, Events = e =>
            {
                Check.Require(commits == 1 && fixture.Store.SyncedByKey(fixture.Pair.PairId, side,
                    Key(side, entry.Path)) is null && fixture.Store.SyncedByKey(fixture.Pair.PairId, side,
                    Key(side, "Folder/Child")) is null && fixture.Store.Issues(fixture.Pair.PairId).Count == 0,
                    "The effect event follows the committed subtree removal and Failure closure.");
                if (failure == "event") throw error;
                events.Add(e);
            } };
            using var cancellation = new CancellationTokenSource();
            if (failure == "cancel") cancellation.Cancel();
            var failed = false;
            try { await ExecutorFor(context).RemoveAsync(side, entry, null, change, cancellation.Token); }
            catch (Exception actual) when (ReferenceEquals(actual, error) || failure == "cancel" && actual is OperationCanceledException)
            { failed = true; }
            var deleted = failure is not ("delete" or "cancel");
            var committed = failure is "" or "event";
            Check.Require(failed == (failure != "") &&
                (await fixture.Context.For(side).ReadOneAsync(entry.Path, CancellationToken.None) == null) == deleted &&
                commits == (committed ? 1 : 0) &&
                (fixture.Store.SyncedByKey(fixture.Pair.PairId, side, Key(side, entry.Path)) == null) == committed &&
                (fixture.Store.SyncedByKey(fixture.Pair.PairId, side, Key(side, "Folder/Child")) == null) == committed &&
                (committed ? fixture.Store.Issues(fixture.Pair.PairId).Count == 0
                    : fixture.Store.Issues(fixture.Pair.PairId).SequenceEqual(new[] { priorIssue })),
                "A refused deletion or commit preserves the saved subtree and Failure; only a committed deletion closes it.");
            var completes = side == Side.Dropbox && failure != "cancel";
            Check.Require(admission.Success == (completes ? failure == "" : (bool?)null),
                "Inner removes never admit and retain the original Dropbox completion success on every exit.");
            var expected = RunEvent.Effect(
                side == Side.Local ? EffectKind.DeletedLocal : EffectKind.DeletedDropbox, entry.Path, "", side);
            Check.Require(failure == "" ? events.SequenceEqual(new[] { expected }) : events.Count == 0,
                "Only a successful remove emits its single deletion effect.");
        }
    }

    private static ActionExecutor ExecutorFor(ExecutorContext context)
    {
        var transfers = new Transfers(context);
        return new ActionExecutor(context, transfers, new Parents(context), new KeepBothRunner(context, transfers));
    }

    private static async Task StateActionsAsync()
    {
        SyncOutcome? inSync = null;
        foreach (var held in new[] { false, true })
        foreach (var kind in new[] { ActionKind.Record, ActionKind.ForgetOnly, ActionKind.DeferFolder, ActionKind.None })
        foreach (var none in Enum.GetValues<NoneReason>())
        foreach (var closeIssues in new[] { false, true })
        {
            if (kind != ActionKind.None && none != NoneReason.InSync) continue;
            using var fixture = new Fixture();
            var l = await SeedAsync(fixture.Local, "Local", false, "same");
            var d = await SeedAsync(fixture.Dropbox, "Remote", false, "same");
            var saved = SyncedEntries.From(l, d);
            var row = MakeRow(l, d) with { Synced = saved, Context = MakeRow(l, d).Context with { Held = held } };
            fixture.Store.Commit(fixture.Pair.PairId, StateChange.Empty with { Record = [saved, Saved("Local/Child", "Remote/Child")] });
            RaiseAll(fixture, row);
            var priorIssues = fixture.Store.Issues(fixture.Pair.PairId);
            var outcome = await fixture.Executor.ExecuteAsync(new SyncAction(kind, row, None: none), null, CancellationToken.None, closeIssues);
            if (kind == ActionKind.None && none == NoneReason.InSync)
            {
                inSync ??= outcome;
                Check.Require(ReferenceEquals(inSync, outcome) && outcome ==
                    new SyncOutcome(ActionKind.None, NoneReason.InSync, null, null, false),
                    "Successful in-sync actions share the immutable result without changing its fields.");
            }
            var issues = fixture.Store.Issues(fixture.Pair.PairId);
            var closesFailure = closeIssues && (kind is ActionKind.Record or ActionKind.ForgetOnly || kind == ActionKind.None &&
                none == NoneReason.InSync);
            var closesHeld = closeIssues && held && (kind == ActionKind.Record || kind == ActionKind.None && none == NoneReason.InSync);
            Check.Require(closeIssues || priorIssues.SequenceEqual(issues), "Deferred closure preserves every state-only issue.");
            var effect = kind == ActionKind.Record ? EffectKind.Recorded : EffectKind.Forgotten;
            RunEvent[] expectedEvents = kind is ActionKind.Record or ActionKind.ForgetOnly
                ? new[] { RunEvent.Effect(effect, l.Path, "") } : Array.Empty<RunEvent>();
            Check.Require(fixture.Events.SequenceEqual(expectedEvents), "State-only effects have no written side or detail; None and Defer emit nothing.");
            Check.Require(issues.Any(i => i.Kind == IssueKind.Failure) != closesFailure, nameof(IssueKind.Failure));
            var closesClash = closeIssues && (kind == ActionKind.Record || kind == ActionKind.None && none == NoneReason.InSync);
            Check.Require(issues.Any(i => i.Kind == IssueKind.Tie) != closesHeld &&
                issues.Any(i => i.Kind == IssueKind.NameClash) != closesClash, nameof(RowContext.Held));
            Check.Require(issues.Any(i => i.Kind == IssueKind.Info) && issues.Any(i => i.Kind == IssueKind.Skipped) &&
                issues.Any(i => i.Kind == IssueKind.DriveChange), "Ordinary closures preserve Info, Skipped and DriveChange.");
            Check.Require(outcome.Deferred == (kind == ActionKind.DeferFolder), nameof(ActionKind.DeferFolder));
            Check.Require(fixture.Admission.Admitted == 0 && fixture.Admission.Completed == 0, nameof(IAdmission));
            if (kind == ActionKind.ForgetOnly)
                Check.Require(fixture.Store.SyncedByKey(fixture.Pair.PairId, Side.Local, saved.LocalKey) is null &&
                    fixture.Store.SyncedByKey(fixture.Pair.PairId, Side.Local, Key(Side.Local, "Local/Child")) is null,
                    nameof(ActionKind.ForgetOnly));
            else
                Check.Require(fixture.Store.SyncedByKey(fixture.Pair.PairId, Side.Local, saved.LocalKey) == saved, nameof(ActionKind.Record));
        }
    }

    private static async Task BilateralForgetAsync()
    {
        foreach (var real in new[] { false, true })
        {
            var directory = Path.Combine(Path.GetTempPath(), "bilateral-forget-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                using var fixture = new Fixture();
                using IStateStore store = real ? StateStore.Open(Path.Combine(directory, "state.db")) : new InMemoryStateStore();
                store.SavePair(fixture.Pair);
                var saved = Saved("Old", "Remote") with { IsFolder = true };
                var crossLocal = Saved("Old/Child", "Elsewhere/Child");
                var crossRemote = Saved("Elsewhere/Other", "Remote/Other");
                var destination = Saved("Archive", "Archive");
                var ordinary = Saved("Old/Gone", "Remote/Gone");
                store.Commit(fixture.Pair.PairId, StateChange.Empty with
                { Record = [saved, crossLocal, crossRemote, destination, ordinary] });
                await fixture.Dropbox.EnsureFolderAsync("Elsewhere", default);
                await SeedAsync(fixture.Dropbox, crossLocal.DropboxPath, false, "live");
                var row = MakeRow(null, null) with { Synced = saved };
                var action = new SyncAction(ActionKind.ForgetOnly, row);
                await using var held = await PathLocks.Shared.EnterAsync(LockKeys.For(action, fixture.Pair, null), default);
                var outcome = await ExecutorFor(fixture.Context with { Store = store }).ExecuteAsync(action, null, default);
                Check.Require(outcome.Raised is null && outcome.Effect == EffectKind.Forgotten,
                    "Absent bilateral roots complete metadata-only forgetting.");
                foreach (var kept in new[] { crossLocal, crossRemote, destination })
                    Check.Require(store.SyncedByKey(fixture.Pair.PairId, Side.Local, kept.LocalKey) == kept,
                        "Bilateral forgetting retains cross-moved children and the destination binding.");
                foreach (var gone in new[] { saved, ordinary })
                    Check.Require(store.SyncedByKey(fixture.Pair.PairId, Side.Local, gone.LocalKey) is null,
                        "Bilateral forgetting removes root and children within both roots.");
                Check.Require(await fixture.Local.ReadOneAsync(crossLocal.LocalPath, default) is null &&
                    await fixture.Dropbox.ReadOneAsync(crossLocal.DropboxPath, default) is not null,
                    "Forgetting never deletes live cross-moved content.");
            }
            finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
        }
    }

    private static async Task ExclusionClosuresAsync()
    {
        foreach (var path in new[] { "Local", "Remote", "local", "remote", "Target" })
        foreach (var cause in new[] { TextKey.FailExcludeHoldsProtected, TextKey.FailChanged })
        foreach (var issueKind in new[] { IssueKind.Failure, IssueKind.Info })
        foreach (var none in new[] { NoneReason.OutOfScope, NoneReason.InSync })
        {
            using var fixture = new Fixture();
            var local = await SeedAsync(fixture.Local, "Local", false, "same");
            var dropbox = await SeedAsync(fixture.Dropbox, "Remote", false, "same");
            var saved = Saved("local", "remote");
            var row = MakeRow(local, dropbox) with { Synced = saved };
            row = row with { Context = row.Context with { OutOfScope = none == NoneReason.OutOfScope, Held = true } };
            var pair = fixture.Pair;
            fixture.Store.Commit(pair.PairId, StateChange.Empty with { Record = [saved] });
            RaiseAll(fixture, row);
            var subject = new IssueSubject([new IssueItem(Side.Local, path, "excluded")], null);
            fixture.Store.Commit(pair.PairId, StateChange.Empty with
            {
                Raise = new[] { "local", "remote", "Target", "Unrelated" }
                    .Select(p => (IssueKind.Failure, TextKey.FailChanged, p, subject)).ToArray()
            });
            fixture.Store.Commit(pair.PairId, StateChange.Empty with
            {
                Raise = [(issueKind, cause, path, subject)]
            });
            var priorIssues = fixture.Store.Issues(pair.PairId);
            var outcome = await fixture.Executor.ExecuteAsync(new SyncAction(ActionKind.None, row, None: none, Target: "Target"),
                null, CancellationToken.None);
            var expected = priorIssues.Where(issue => none == NoneReason.OutOfScope || issue.Path == "Unrelated" ||
                issue.Kind is not (IssueKind.Failure or IssueKind.Tie or IssueKind.NameClash));
            Check.Require(fixture.Store.Issues(pair.PairId).SequenceEqual(expected),
                "Out-of-scope rows preserve every pending-exclusion issue; held in-sync rows close only their operational conflicts.");
            Check.Require(outcome == new SyncOutcome(ActionKind.None, none, null, null, false) &&
                fixture.Events.Count == 0 && fixture.Admission.Admitted == 0 &&
                fixture.Store.SyncedByKey(pair.PairId, Side.Local, saved.LocalKey) == saved,
                "Exclusion closure changes only issues, not the saved link, events, or admission.");
        }
    }

    private static async Task NoticesAsync()
    {
        foreach (var cause in new[] { TextKey.ClashFileFolder, TextKey.ClashMappedDiffers, TextKey.TieEqualTimes,
                     TextKey.TieUnreliableTime, TextKey.TieKindBoth })
        foreach (var hasHash in new[] { false, true })
        {
            using var fixture = new Fixture();
            var local = await SeedAsync(fixture.Local, "Local", false, "a");
            var row = MakeRow(local, await SeedAsync(fixture.Dropbox, "Remote", false, "b")) with
            {
                LocalHash = hasHash ? await fixture.Local.ContentHashAsync(local, default) : null
            };
            var kind = cause is TextKey.ClashFileFolder or TextKey.ClashMappedDiffers ? IssueKind.NameClash : IssueKind.Tie;
            var action = new SyncAction(ActionKind.Notice, row, NoticeKind: kind, Cause: cause);
            var outcome = await fixture.Executor.ExecuteAsync(action, null, CancellationToken.None);
            var issue = fixture.Store.Issues(fixture.Pair.PairId).Single();
            Check.Require(outcome.Raised == kind && issue.Cause == cause && issue.Subject.Items.Count == 2 &&
                issue.Subject.Incoming == null, nameof(ActionKind.Notice));
            Check.Require(issue.Subject.LocalHash == (hasHash
                    ? new LocalHashProof(local.Size, local.Modified.UtcTicks, row.LocalHash!) : null) &&
                issue.Subject.Items.Contains(new IssueItem(Side.Local, local.Path, local.Id)),
                "Conflict notices persist only an available local hash, bound to its exact observed identity and stamp.");
            fixture.Store.DismissIssue(fixture.Pair.PairId, issue.Id);
            await fixture.Executor.ExecuteAsync(action, null, CancellationToken.None);
            Check.Require(fixture.Store.Issues(fixture.Pair.PairId).Single().Dismissed, nameof(Issue.Dismissed));
            await fixture.Executor.ExecuteAsync(new SyncAction(ActionKind.Record,
                row with { Context = row.Context with { Held = true } }), null, CancellationToken.None);
            Check.Require(fixture.Store.Issues(fixture.Pair.PairId).Count == 0, nameof(StateChange.Close));
            await fixture.Executor.ExecuteAsync(action, null, CancellationToken.None);
            Check.Require(!fixture.Store.Issues(fixture.Pair.PairId).Single().Dismissed, nameof(Issue));
            if (cause == TextKey.ClashMappedDiffers)
            {
                await fixture.Executor.ExecuteAsync(new SyncAction(ActionKind.Record, row), null, CancellationToken.None);
                Check.Require(fixture.Store.Issues(fixture.Pair.PairId).Count == 0, nameof(TextKey.ClashMappedDiffers));
            }
        }
        foreach (var hasPrimary in new[] { false, true })
        foreach (var hasLocal in new[] { false, true })
        {
            using var fixture = new Fixture();
            var local = new Entry("item_", false, 0, DateTimeOffset.UnixEpoch, "local", "", "", null);
            var primary = local with { Id = "primary" };
            var extras = new[] { local with { Path = "item:", Id = "colon" }, local with { Path = "item?", Id = "question" } };
            if (hasLocal) fixture.Local.Seed(local, null);
            if (hasPrimary) fixture.Dropbox.Seed(primary, null);
            foreach (var extra in extras) fixture.Dropbox.Seed(extra, null);
            var row = MakeRow(hasLocal ? local : null, hasPrimary ? primary : null) with { Extra = extras };
            var result = await fixture.Executor.ExecuteAsync(new SyncAction(ActionKind.Notice, row,
                NoticeKind: IssueKind.NameClash, Cause: TextKey.ClashMappedNames), null, CancellationToken.None);
            var issue = fixture.Store.Issues(fixture.Pair.PairId).Single();
            var expected = new List<IssueItem>();
            if (hasLocal) expected.Add(new IssueItem(Side.Local, local.Path, local.Id));
            if (hasPrimary) expected.Add(new IssueItem(Side.Dropbox, primary.Path, primary.Id));
            expected.AddRange(extras.Select(entry => new IssueItem(Side.Dropbox, entry.Path, entry.Id)));
            Check.Require(result.Raised == IssueKind.NameClash && result.Effect is null &&
                issue.Cause == TextKey.ClashMappedNames && issue.Subject.Incoming is null &&
                issue.Subject.Items.SequenceEqual(expected),
                "A mapped-name notice includes every candidate, with every extra on Dropbox.");
            Check.Require(fixture.Admission.Admitted == 0 && fixture.Events.Single().Kind == RunEventKind.Issue,
                "A mapped-name notice commits its issue without transfers or admission.");
        }
    }

    private static async Task KeepBothAsync()
    {
        foreach (var reuse in new[] { false, true })
        foreach (var closeIssues in new[] { false, true })
        {
            using var fixture = new Fixture();
            var local = await SeedAsync(fixture.Local, "Local", false, "local");
            var dropbox = await SeedAsync(fixture.Dropbox, "Remote", false, "remote");
            var row = MakeRow(local, dropbox) with { Synced = SyncedEntries.From(local, dropbox) };
            fixture.Store.Commit(fixture.Pair.PairId, StateChange.Empty with { Record = [row.Synced!] });
            if (reuse)
            {
                await SeedAsync(fixture.Dropbox, "RemoteCopy", false, "local");
                await SeedAsync(fixture.Local, "LocalCopy", false, "remote");
            }
            RaiseAll(fixture, row);
            var priorIssues = fixture.Store.Issues(fixture.Pair.PairId);
            var labels = new KeepBothLabels("RemoteCopy", reuse, "LocalCopy", reuse);
            var result = await fixture.Executor.ExecuteAsync(new SyncAction(ActionKind.KeepBoth, row), labels, CancellationToken.None, closeIssues);
            var issues = fixture.Store.Issues(fixture.Pair.PairId);
            Check.Require(closeIssues ? !issues.Any(i => i.Kind is IssueKind.Failure or IssueKind.Tie)
                : priorIssues.All(prior => issues.Any(issue => issue.Id == prior.Id)), nameof(StateChange.Close));
            Check.Require(fixture.Events.SequenceEqual(new[]
            {
                RunEvent.Effect(EffectKind.KeptBoth, labels.DropboxLabel!, local.Path, Side.Dropbox),
                RunEvent.Effect(EffectKind.KeptBoth, labels.LocalLabel!, dropbox.Path, Side.Local),
                RunEvent.Issue(IssueKind.Info, TextKey.InfoKeptBoth, local.Path)
            }), "Keep-both emits one effect per written side, then its issue, with the label and copied original.");
            Check.Require(result.Effect == EffectKind.KeptBoth && result.Raised == IssueKind.Info, nameof(ActionKind.KeepBoth));
            Check.Require(fixture.Store.SyncedByKey(fixture.Pair.PairId, Side.Local, PathNames.Key(local.Path))?.DropboxPath == labels.DropboxLabel &&
                fixture.Store.SyncedByKey(fixture.Pair.PairId, Side.Dropbox, PathNames.DropboxKey(dropbox.Path))?.LocalPath == labels.LocalLabel, nameof(KeepBothLabels));
            Check.Require(fixture.Admission.Admitted == 0 && fixture.Admission.Completed == 1, nameof(IAdmission));
        }
    }

    private static async Task ObserverEffectsAsync()
    {
        foreach (var failure in new[] { false, true })
        {
            using var fixture = new Fixture();
            var source = await SeedAsync(fixture.Local, "Source", false, "new");
            var row = MakeRow(source, null);
            var counter = new ReadCounter(fixture.Dropbox);
            var context = fixture.Context with { Dropbox = counter };
            var error = new ItemFailedException("Copy_", TextKey.PublishFailed, null);
            if (failure) fixture.Dropbox.FailNext("Copy_", error);
            SyncOutcome? outcome = null;
            var propagated = false;
            try
            {
                outcome = await ExecutorFor(context).ExecuteAsync(
                    new SyncAction(ActionKind.Upload, row, Target: "Copy_"), null, default);
            }
            catch (Exception actual) when (ReferenceEquals(actual, error)) { propagated = true; }
            var observed = await new Observer(context, new Parents(context)).ObserveAsync(
                Side.Local, source.Path, default);
            var copied = await fixture.Dropbox.ReadOneAsync("Copy_", default);
            Check.Require(!propagated && counter.Listings == 0 && observed.Extra.Count == 0 && observed.Dropbox == copied,
                "Observation reads only the exact subject; the executor keeps no sibling listing or cache.");
            Check.Require(failure
                    ? outcome?.Raised == IssueKind.Failure && outcome.Effect is null &&
                        fixture.Events.All(e => e.Kind == RunEventKind.Issue)
                    : outcome?.Effect == EffectKind.Uploaded && copied is not null &&
                        fixture.Events.Single().Kind == RunEventKind.Effect,
                "A committed upload reports its effect; a staged failure reports only its issue.");
        }
    }

    private static async Task FailuresAsync()
    {
        foreach (var beforeDecision in new[] { false, true })
        foreach (var error in new Exception[] { new ChangedUnderneathException("Item"),
                     new ItemFailedException("Item", TextKey.ReadLocalFailed, null), new PathOccupiedException("Target") })
        {
            using var fixture = new Fixture();
            var incoming = await SeedAsync(fixture.Local, "Item", false, "new");
            var occupant = await SeedAsync(fixture.Dropbox, "Target", false, "old");
            var row = MakeRow(incoming, null);
            var action = new SyncAction(ActionKind.Upload, row, Target: "Target/Child");
            var outcome = await fixture.Executor.FailAsync(row, beforeDecision ? null : action, error, CancellationToken.None);
            var issue = fixture.Store.Issues(fixture.Pair.PairId).Single();
            var clash = !beforeDecision && error is PathOccupiedException;
            Check.Require(issue.Kind == (clash ? IssueKind.NameClash : IssueKind.Failure) && outcome.Raised == issue.Kind, nameof(ActionExecutor.FailAsync));
            Check.Require(issue.Cause == (error is ItemFailedException ? TextKey.ReadLocalFailed :
                error is PathOccupiedException ? TextKey.ClashNameTaken : TextKey.FailChanged), nameof(Issue.Cause));
            if (clash)
                Check.Require(issue.Subject.Incoming == Side.Local && issue.Subject.Items.SequenceEqual(new[]
                {
                    new IssueItem(Side.Local, incoming.Path, incoming.Id),
                    new IssueItem(Side.Dropbox, occupant.Path, occupant.Id)
                }), nameof(IssueSubject));
            Check.Require(issue.Path == (clash ? "Target" : incoming.Path), nameof(Issue.Path));
            Check.Require(Massdex.SyncShark.Engine.Run.RunLog.Present(fixture.Events.Single()).What == issue.Kind.ToString(), nameof(RunEvent));
        }
        foreach (var side in Enum.GetValues<Side>())
        {
            using var fixture = new Fixture();
            var incoming = await SeedAsync(fixture.Context.For(side), "Incoming", false, "new");
            await SeedAsync(fixture.Context.For(side), "Target", false, "old");
            var row = MakeRow(side == Side.Local ? incoming : null, side == Side.Dropbox ? incoming : null);
            var result = await fixture.Executor.ExecuteAsync(new SyncAction(ActionKind.MoveUnlinked, row, Target: "Target", Side: side), null, CancellationToken.None);
            Check.Require(result.Raised == IssueKind.NameClash && fixture.Store.Issues(fixture.Pair.PairId).Single().Subject.Incoming == side, nameof(ActionKind.MoveUnlinked));
            Check.Require(await fixture.Context.For(side).ReadOneAsync(incoming.Path, CancellationToken.None) != null, nameof(ISide.MoveAsync));
        }
        foreach (var error in new Exception[] { new ChangedUnderneathException("Child"),
            new ItemFailedException("Child", TextKey.ReadDropboxFailed, null), new PathOccupiedException("Child") })
        {
            using var fixture = new Fixture();
            var outcome = await fixture.Executor.FailAsync(MakeRow(null, null) with { Key = "Parent" },
                null, error, default);
            var issue = fixture.Store.Issues(fixture.Pair.PairId).Single();
            Check.Require(issue.Path == "Child" && issue.Kind == IssueKind.Failure &&
                outcome.Raised == IssueKind.Failure && issue.Subject.Items.Count == 0,
                "EX-03: an entryless observation failure names its exception path without becoming a name clash.");
        }
        using var failedCopy = new Fixture();
        var source = await SeedAsync(failedCopy.Local, "Incoming", false, "new");
        await SeedAsync(failedCopy.Dropbox, "Target", false, "old");
        var failed = await failedCopy.Executor.ExecuteAsync(new SyncAction(ActionKind.Upload, MakeRow(source, null), Target: "Target"), null, CancellationToken.None);
        Check.Require(failed.Raised == IssueKind.NameClash && failedCopy.Admission.Admitted == 0 && failedCopy.Admission.Completed == 1, nameof(IAdmission.ActionCompleted));
        Check.Require(failedCopy.Store.SyncedByKey(failedCopy.Pair.PairId, Side.Local, PathNames.Key(source.Path)) == null, nameof(StateChange.Record));

        foreach (var error in new Exception[] { new InvalidOperationException(), new OperationCanceledException(), new IOException() })
        {
            using var fixture = new Fixture();
            var caught = false;
            try { await fixture.Executor.FailAsync(MakeRow(null, null), null, error, CancellationToken.None); }
            catch (Exception actual) { caught = ReferenceEquals(actual, error); }
            Check.Require(caught && fixture.Store.Issues(fixture.Pair.PairId).Count == 0, nameof(Exception));
        }
        using var cancelled = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var propagated = false;
        try { await cancelled.Executor.ExecuteAsync(new SyncAction(ActionKind.None, MakeRow(null, null)), null, cancellation.Token); }
        catch (OperationCanceledException) { propagated = true; }
        Check.Require(propagated && cancelled.Admission.Admitted == 0 && cancelled.Events.Count == 0, nameof(CancellationToken));
    }

    private static async Task InjectedFailuresAsync()
    {
        foreach (var kind in new[] { ActionKind.Upload, ActionKind.Download, ActionKind.EnsureLocalFolder,
                     ActionKind.EnsureDropboxFolder, ActionKind.DeleteLocal, ActionKind.DeleteDropbox,
                     ActionKind.MoveLocal, ActionKind.MoveDropbox, ActionKind.MoveUnlinked, ActionKind.ReplaceKind })
        foreach (var side in Enum.GetValues<Side>())
        foreach (var failOnSource in new[] { false, true })
        foreach (var error in new Exception[]
        {
            new ChangedUnderneathException(nameof(InjectedFailuresAsync)),
            new ItemFailedException(nameof(InjectedFailuresAsync), TextKey.FailEarlierReplace, null),
            new PathOccupiedException(nameof(InjectedFailuresAsync)),
            new OperationCanceledException(), new InvalidOperationException()
        })
        {
            if (kind is not (ActionKind.MoveUnlinked or ActionKind.ReplaceKind) && side != Side.Local) continue;
            if (failOnSource && kind is not (ActionKind.Upload or ActionKind.Download)) continue;
            using var fixture = new Fixture();
            var written = kind switch
            {
                ActionKind.Upload or ActionKind.EnsureDropboxFolder or ActionKind.DeleteDropbox or ActionKind.MoveDropbox => Side.Dropbox,
                ActionKind.MoveUnlinked or ActionKind.ReplaceKind => side,
                _ => Side.Local
            };
            var folder = kind is ActionKind.EnsureLocalFolder or ActionKind.EnsureDropboxFolder;
            var local = await SeedAsync(fixture.Local, "Local", folder || kind == ActionKind.ReplaceKind && written == Side.Dropbox, "local");
            var dropbox = await SeedAsync(fixture.Dropbox, "Remote", folder || kind == ActionKind.ReplaceKind && written == Side.Local, "remote");
            var saved = SyncedEntries.From(local, dropbox);
            var row = MakeRow(local, dropbox) with { Synced = kind == ActionKind.MoveUnlinked ? null : saved };
            if (row.Synced is not null)
                fixture.Store.Commit(fixture.Pair.PairId, StateChange.Empty with { Record = [row.Synced] });
            RaiseAll(fixture, row);
            var priorIssues = fixture.Store.Issues(fixture.Pair.PairId);
            var destination = written == Side.Local ? local : dropbox;
            var source = written == Side.Local ? dropbox : local;
            var moving = kind is ActionKind.MoveLocal or ActionKind.MoveDropbox or ActionKind.MoveUnlinked;
            var target = moving ? "Moved" : destination.Path;
            ((FakeSide)fixture.Context.For(failOnSource ? Other(written) : written))
                .FailNext(failOnSource ? source.Path : destination.Path, error);
            SyncOutcome? outcome = null;
            var propagated = false;
            try
            {
                outcome = await fixture.Executor.ExecuteAsync(new SyncAction(kind, row, Target: target,
                    Side: kind is ActionKind.MoveUnlinked or ActionKind.ReplaceKind ? written : null), null, CancellationToken.None);
            }
            catch (Exception actual) when (ReferenceEquals(actual, error)) { propagated = true; }
            var handled = error is ItemFailedException or PathOccupiedException;
            Check.Require(propagated == !handled && (outcome is not null) == handled,
                "Changed-underneath reaches the reobservation owner; definite item failures alone become outcomes.");
            var completions = kind is ActionKind.DeleteLocal or ActionKind.MoveLocal ||
                kind == ActionKind.MoveUnlinked && written == Side.Local ? 0 : 1;
            Check.Require(fixture.Admission.Admitted == 0 && (completions == 0
                    ? fixture.Admission.Completed == 0 : fixture.Admission.Completed >= 1),
                "Failed inner actions take no permit and retain their Dropbox completion count.");
            Check.Require(await fixture.Local.ReadOneAsync(local.Path, CancellationToken.None) == local &&
                await fixture.Dropbox.ReadOneAsync(dropbox.Path, CancellationToken.None) == dropbox &&
                fixture.Store.SyncedByKey(fixture.Pair.PairId, Side.Local, saved.LocalKey) == row.Synced,
                "Failure before a mutation preserves both entries and their prior binding.");
            Check.Require(!moving || await fixture.Context.For(written).ReadOneAsync(target, CancellationToken.None) is null,
                "A refused move leaves no new target.");
            var issues = fixture.Store.Issues(fixture.Pair.PairId);
            Check.Require(priorIssues.All(prior => issues.Any(issue => issue.Id == prior.Id)),
                "A failed action does not close existing issues.");
            if (handled)
            {
                var issueKind = error is PathOccupiedException ? IssueKind.NameClash : IssueKind.Failure;
                var cause = error is ItemFailedException failed ? failed.Cause :
                    error is PathOccupiedException ? TextKey.ClashNameTaken : TextKey.FailChanged;
                var issuePath = error is PathOccupiedException occupied ? occupied.Path : local.Path;
                var issue = issues.Single(i => i.Kind == issueKind && i.Path == issuePath && i.Cause == cause);
                Check.Require(outcome!.Raised == issueKind && outcome.Effect is null &&
                    fixture.Events.Single() is { Kind: RunEventKind.Issue } notice &&
                    notice.Path == issuePath && notice.Cause == cause,
                    "Handled failures commit their mapped notice and emit no effect.");
                if (error is PathOccupiedException)
                    Check.Require(issue.Subject.Incoming == (kind == ActionKind.MoveUnlinked ? written : Other(written)),
                        "A taken target identifies the action's incoming side.");
            }
            else Check.Require(fixture.Events.Count == 0 && issues.Count == priorIssues.Count,
                "Propagated errors create no notices or success events.");
        }
    }

    private static async Task CommitFailuresAsync()
    {
        foreach (var side in Enum.GetValues<Side>())
        foreach (var failCommit in new[] { false, true })
        {
            using var fixture = new Fixture();
            var local = await SeedAsync(fixture.Local, "Local", false, "same");
            var dropbox = await SeedAsync(fixture.Dropbox, "Remote", false, "same");
            var saved = SyncedEntries.From(local, dropbox);
            var row = MakeRow(local, dropbox) with { Synced = saved };
            fixture.Store.Commit(fixture.Pair.PairId, StateChange.Empty with { Record = [saved] });
            RaiseAll(fixture, row);
            var priorIssues = fixture.Store.Issues(fixture.Pair.PairId);
            var destination = new ReadCounter(fixture.Context.For(side));
            var error = new InvalidOperationException("Commit refused");
            var commits = 0;
            using var store = new CommitStore(fixture.Store, change =>
            {
                Check.Require(destination.Deletes == 1 &&
                    fixture.Store.Issues(fixture.Pair.PairId).SequenceEqual(priorIssues) &&
                    change.ForgetSelf.SequenceEqual(new[] { (side, Key(side, row.EntryOn(side)!.Path)) }) &&
                    change.Close.Select(item => item.Path).SequenceEqual(new[] { local.Path, dropbox.Path }) &&
                    change.Close.All(item => item.Kinds.SequenceEqual(IssueClosure.AfterHeldInSync)),
                    "Deletion happens before its single commit; that commit forgets and closes every exact row path together.");
                commits++;
                if (failCommit) throw error;
                fixture.Store.Commit(fixture.Pair.PairId, change);
            });
            var admission = new TrackedAdmission(strict: false);
            var context = fixture.Context with
            {
                Store = store, Admission = admission,
                Local = side == Side.Local ? destination : fixture.Local,
                Dropbox = side == Side.Dropbox ? destination : fixture.Dropbox
            };
            SyncOutcome? outcome = null;
            var failed = false;
            try
            {
                outcome = await ExecutorFor(context).ExecuteAsync(new SyncAction(
                    side == Side.Local ? ActionKind.DeleteLocal : ActionKind.DeleteDropbox, row), null, default);
            }
            catch (InvalidOperationException actual) when (ReferenceEquals(actual, error)) { failed = true; }
            var issues = fixture.Store.Issues(fixture.Pair.PairId);
            Check.Require(failed == failCommit && commits == 1 && destination.Deletes == 1 &&
                await fixture.Context.For(side).ReadOneAsync(row.EntryOn(side)!.Path, default) is null &&
                (fixture.Store.SyncedByKey(fixture.Pair.PairId, Side.Local, saved.LocalKey) is null) == !failCommit &&
                issues.SequenceEqual(failCommit ? priorIssues : priorIssues.Where(issue =>
                    issue.Kind is not (IssueKind.Failure or IssueKind.Tie or IssueKind.NameClash))),
                "A commit fault after deletion retains the saved binding and all issues; success closes only ordinary conflicts.");
            Check.Require(failCommit ? outcome is null && fixture.Events.Count == 0
                : outcome?.Effect == EffectKinds.Deleted(side) && fixture.Events.Single().Kind == RunEventKind.Effect,
                "A commit fault is never reported as a completed effect.");
            Check.Require(admission.Success == (side == Side.Dropbox ? !failCommit : (bool?)null),
                "Completion reflects the commit result without inner admission.");
        }
    }

    private static async Task OccupantReadFailuresAsync()
    {
        foreach (var written in Enum.GetValues<Side>())
        foreach (var error in new Exception[]
        {
            new ChangedUnderneathException(nameof(OccupantReadFailuresAsync)),
            new ItemFailedException(nameof(OccupantReadFailuresAsync), TextKey.ReadDropboxFailed, null)
        })
        {
            using var fixture = new Fixture();
            var source = await SeedAsync(fixture.Context.For(Other(written)), "Incoming", false, "new");
            var occupant = await SeedAsync(fixture.Context.For(written), "Target", false, "old");
            var row = MakeRow(written == Side.Dropbox ? source : null, written == Side.Local ? source : null);
            ((FakeSide)fixture.Context.For(written)).FailNext(occupant.Path, error);
            var result = await fixture.Executor.FailAsync(row,
                new SyncAction(written == Side.Local ? ActionKind.Download : ActionKind.Upload, row, Target: occupant.Path),
                new PathOccupiedException(occupant.Path), CancellationToken.None);
            var issue = fixture.Store.Issues(fixture.Pair.PairId).Single();
            Check.Require(result.Raised == IssueKind.Failure && issue.Kind == IssueKind.Failure &&
                issue.Cause == (error is ItemFailedException failed ? failed.Cause : TextKey.FailChanged) &&
                issue.Path == source.Path && issue.Subject.Incoming is null &&
                issue.Subject.Items.SequenceEqual(new[] { new IssueItem(Other(written), source.Path, source.Id) }),
                "Failure reading the occupant becomes the incoming item's Failure, not an incomplete NameClash.");
            Check.Require(Massdex.SyncShark.Engine.Run.RunLog.Present(fixture.Events.Single()).What == IssueKind.Failure.ToString() &&
                await fixture.Context.For(written).ReadOneAsync(occupant.Path, CancellationToken.None) == occupant,
                "The unreadable occupant is preserved and only the fallback Failure is emitted.");
        }
    }

    private static async Task TakenKeepBothLabelAsync()
    {
        foreach (var written in Enum.GetValues<Side>())
        {
            using var fixture = new Fixture();
            var local = await SeedAsync(fixture.Local, "Local", false, "local");
            var dropbox = await SeedAsync(fixture.Dropbox, "Remote", false, "remote");
            var saved = SyncedEntries.From(local, dropbox);
            var row = MakeRow(local, dropbox) with { Synced = saved };
            fixture.Store.Commit(fixture.Pair.PairId, StateChange.Empty with { Record = [saved] });
            var labels = new KeepBothLabels("RemoteCopy", false, "LocalCopy", false);
            var label = labels.On(written).Label!;
            var occupant = await SeedAsync(fixture.Context.For(written), label, false, "occupant");
            var result = await fixture.Executor.ExecuteAsync(new SyncAction(ActionKind.KeepBoth, row), labels, CancellationToken.None);
            var issue = fixture.Store.Issues(fixture.Pair.PairId).Single();
            Check.Require(result.Raised == IssueKind.Failure && result.Effect is null && issue.Cause == TextKey.FailChanged &&
                issue.Kind == IssueKind.Failure && issue.Path == local.Path && issue.Subject.Incoming is null &&
                issue.Subject.Items.SequenceEqual(new[]
                {
                    new IssueItem(Side.Local, local.Path, local.Id), new IssueItem(Side.Dropbox, dropbox.Path, dropbox.Id)
                }),
                "A keep-both label taken after selection is a retryable change on the original row, not a NameClash.");
            Check.Require(fixture.Store.SyncedByKey(fixture.Pair.PairId, Side.Local, saved.LocalKey) == saved &&
                await fixture.Context.For(written).ReadOneAsync(label, CancellationToken.None) == occupant &&
                fixture.Events.Single() is { Kind: RunEventKind.Issue, IssueKind: IssueKind.Failure,
                    Cause: TextKey.FailChanged } notice && notice.Path == local.Path &&
                fixture.Admission.Admitted == 0 && fixture.Admission.Completed == 1,
                "An incomplete keep-both preserves its old link and the label occupant without a success event.");
        }
    }

    private static Row WithFirstRun(Row row, IssueSubject.FirstRunDeletionKind kept,
        bool firstRun = true, bool complete = true, bool held = false) =>
        row with
        {
            Context = row.Context with
            {
                FirstRun = firstRun, CompleteInventory = complete, Held = held, FirstRunDeletion = kept
            }
        };

    // P4/F107 first-run marks: A1-A16, Q2/Q3/Q8/Q13/Q16-Q20 in the executor's own commit and callback paths.
    private static async Task FirstRunUnresolvedAsync()
    {
        foreach (var complete in new[] { false, true })
        foreach (var kept in new[] { IssueSubject.FirstRunDeletionKind.None, IssueSubject.FirstRunDeletionKind.Item })
        {
            using var fixture = new Fixture();
            var local = await SeedAsync(fixture.Local, "Item", false, "new");
            var row = WithFirstRun(MakeRow(local, null), kept, complete: complete, held: true);
            var outcome = await fixture.Executor.ExecuteAsync(
                new SyncAction(ActionKind.None, row, None: NoneReason.Held), null, CancellationToken.None);
            var issues = fixture.Store.Issues(fixture.Pair.PairId);
            var owed = kept != IssueSubject.FirstRunDeletionKind.None || complete;
            Check.Require(owed
                    ? outcome.Raised == IssueKind.Failure && issues.Single().Cause == TextKey.FailFirstSyncCheckWaiting &&
                        issues[0].Subject.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.Item &&
                        issues[0].Path == local.Path
                    : issues.Count == 0,
                "A1/A6: only an owed held row enrolls one waiting Failure; an incomplete unmarked row invents no marker.");
        }

        foreach (var none in new[] { NoneReason.OutOfScope, NoneReason.RemovedWithFolder })
        {
            using var fixture = new Fixture();
            var local = await SeedAsync(fixture.Local, "Item", false, "new");
            var row = WithFirstRun(MakeRow(local, null), IssueSubject.FirstRunDeletionKind.None);
            var outcome = await fixture.Executor.ExecuteAsync(new SyncAction(ActionKind.None, row, None: none),
                null, CancellationToken.None);
            Check.Require(outcome.Raised is null && fixture.Store.Issues(fixture.Pair.PairId).Count == 0,
                "A16: out-of-scope and removed-with-folder rows create no unresolved enrollment.");
        }

        using var held = new Fixture();
        var folder = await SeedAsync(held.Local, "Folder", true, "");
        var marked = WithFirstRun(MakeRow(folder, null), IssueSubject.FirstRunDeletionKind.Item, held: true);
        var heldOutcome = await held.Executor.ExecuteAsync(
            new SyncAction(ActionKind.None, marked, None: NoneReason.Held), null, CancellationToken.None);
        var heldIssue = held.Store.Issues(held.Pair.PairId).Single();
        Check.Require(heldOutcome.Raised == IssueKind.Failure &&
            heldIssue.Cause == TextKey.FailFirstSyncCheckWaiting &&
            heldIssue.Subject.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.Item,
            "Q13: an own-held marked folder remains one unresolved Failure with its retained provenance.");
    }

    private static async Task FirstRunNoticeAsync()
    {
        foreach (var cause in new[] { TextKey.ClashFileFolder, TextKey.TieEqualTimes })
        {
            using var fixture = new Fixture();
            var local = await SeedAsync(fixture.Local, "Item", false, "new");
            var row = WithFirstRun(MakeRow(local, null), IssueSubject.FirstRunDeletionKind.None);
            var kind = cause == TextKey.TieEqualTimes ? IssueKind.Tie : IssueKind.NameClash;
            var outcome = await fixture.Executor.ExecuteAsync(
                new SyncAction(ActionKind.Notice, row, NoticeKind: kind, Cause: cause), null, CancellationToken.None);
            var issues = fixture.Store.Issues(fixture.Pair.PairId);
            Check.Require(outcome.Raised == kind && issues.Any(issue => issue.Kind == kind && issue.Cause == cause) &&
                issues.Any(issue => issue.Kind == IssueKind.Failure &&
                    issue.Cause == TextKey.FailFirstSyncCheckWaiting &&
                    issue.Subject.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.Item),
                "A2/A15: a notice on an owed row keeps its own kind and adds the marked waiting Failure in one change.");
        }
    }

    private static async Task FirstRunFailureAsync()
    {
        foreach (var kept in new[] { IssueSubject.FirstRunDeletionKind.Item,
                     IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice })
        foreach (var cause in new[] { TextKey.ReadLocalFailed, TextKey.PublishFailed })
        {
            using var fixture = new Fixture();
            var local = await SeedAsync(fixture.Local, "Item", false, "new");
            var row = WithFirstRun(MakeRow(local, null), kept);
            var outcome = await fixture.Executor.FailAsync(row,
                new SyncAction(ActionKind.Upload, row, Target: "Remote"),
                new ItemFailedException(local.Path, cause, null), CancellationToken.None);
            var issue = fixture.Store.Issues(fixture.Pair.PairId).Single();
            Check.Require(outcome.Raised == IssueKind.Failure && issue.Cause == cause &&
                issue.Path == local.Path && issue.Subject.FirstRunDeletion == kept &&
                !fixture.Store.Issues(fixture.Pair.PairId).Any(item => item.Kind == IssueKind.Info),
                "A3/Q2/Q3/A8: a failed owed upload keeps its actual cause and disposition, never a completion Info.");
        }

        using (var subtree = new Fixture())
        {
            var child = await SeedAsync(subtree.Local, "Tree/Child", false, "new");
            var row = WithFirstRun(MakeRow(child, null), IssueSubject.FirstRunDeletionKind.Item);
            await subtree.Executor.FailAsync(row, new SyncAction(ActionKind.Upload, row, Target: "Remote"),
                new ItemFailedException(child.Path, TextKey.PublishFailed, null), CancellationToken.None);
            var issue = subtree.Store.Issues(subtree.Pair.PairId).Single();
            Check.Require(issue.Path == child.Path && issue.Cause == TextKey.PublishFailed &&
                issue.Subject.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.Item,
                "A9/A10: a projected subtree child owns its own Item Failure at its own path.");
        }

        using (var occupied = new Fixture())
        {
            var incoming = await SeedAsync(occupied.Local, "Item", false, "new");
            var occupant = await SeedAsync(occupied.Dropbox, "Target", false, "old");
            var row = WithFirstRun(MakeRow(incoming, null), IssueSubject.FirstRunDeletionKind.Item);
            var outcome = await occupied.Executor.ExecuteAsync(
                new SyncAction(ActionKind.Upload, row, Target: occupant.Path), null, CancellationToken.None);
            var issues = occupied.Store.Issues(occupied.Pair.PairId);
            Check.Require(outcome.Raised == IssueKind.NameClash &&
                issues.Any(issue => issue.Kind == IssueKind.NameClash && issue.Path == occupant.Path &&
                    issue.Subject.Incoming == Side.Local) &&
                issues.Any(issue => issue.Kind == IssueKind.Failure && issue.Path == incoming.Path &&
                    issue.Subject.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.Item),
                "A11: an occupied target keeps its NameClash at the actual target and the marked Failure at the subject.");
        }

        using (var readFail = new Fixture())
        {
            var incoming = await SeedAsync(readFail.Local, "Item", false, "new");
            await SeedAsync(readFail.Dropbox, "Target", false, "old");
            readFail.Dropbox.FailNext("Target", new ItemFailedException("Target", TextKey.ReadDropboxFailed, null));
            var row = WithFirstRun(MakeRow(incoming, null), IssueSubject.FirstRunDeletionKind.Item);
            var outcome = await readFail.Executor.FailAsync(row,
                new SyncAction(ActionKind.Upload, row, Target: "Target"),
                new PathOccupiedException("Target"), CancellationToken.None);
            var issue = readFail.Store.Issues(readFail.Pair.PairId).Single();
            Check.Require(outcome.Raised == IssueKind.Failure && issue.Cause == TextKey.ReadDropboxFailed &&
                issue.Path == incoming.Path &&
                issue.Subject.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.Item,
                "A11: an unreadable occupant retains the selected disposition and its actual read cause.");
        }

        using (var ownFailed = new Fixture())
        {
            var folder = await SeedAsync(ownFailed.Local, "Folder", true, "");
            var row = WithFirstRun(MakeRow(folder, null), IssueSubject.FirstRunDeletionKind.Item, held: true);
            var outcome = await ownFailed.Executor.FailAsync(row, null,
                new ItemFailedException(folder.Path, TextKey.ReadLocalFailed, null), CancellationToken.None);
            Check.Require(outcome.Raised == IssueKind.Failure &&
                ownFailed.Store.Issues(ownFailed.Pair.PairId).Single().Subject.FirstRunDeletion ==
                    IssueSubject.FirstRunDeletionKind.Item &&
                ownFailed.Events.All(item => item.Kind == RunEventKind.Issue),
                "Q13: an own-failed marked folder keeps its disposition and reports no successful result.");
        }

        using (var refused = new Fixture())
        {
            var local = await SeedAsync(refused.Local, "Item", false, "new");
            var row = WithFirstRun(MakeRow(local, null), IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice);
            var error = new InvalidOperationException("record refused");
            using var store = new CommitStore(refused.Store, _ => throw error);
            var propagated = false;
            try
            {
                await ExecutorFor(refused.Context with { Store = store }).ExecuteAsync(
                    new SyncAction(ActionKind.Upload, row, Target: "Remote"), null, CancellationToken.None);
            }
            catch (InvalidOperationException actual) when (ReferenceEquals(actual, error)) { propagated = true; }
            Check.Require(propagated && store.AsyncCommits == 1 &&
                refused.Store.Issues(refused.Pair.PairId).Count == 0 &&
                refused.Events.All(item => item.Kind != RunEventKind.Effect) &&
                refused.Store.SyncedByKey(refused.Pair.PairId, Side.Local, row.Key) is null,
                "A5/Q8: a failed durable record propagates, writes no partial acknowledgement and reports no effect.");
        }
    }

    private static async Task FirstRunSuccessAsync()
    {
        using (var fixture = new Fixture())
        {
            var local = await SeedAsync(fixture.Local, "Item", false, "new");
            var row = WithFirstRun(MakeRow(local, null), IssueSubject.FirstRunDeletionKind.Item);
            StateChange? committed = null;
            using var store = new CommitStore(fixture.Store, change => committed = change);
            await ExecutorFor(fixture.Context with { Store = store }).ExecuteAsync(
                new SyncAction(ActionKind.Upload, row, Target: "Remote"), null, CancellationToken.None);
            Check.Require(committed is { Record.Count: 1, FirstRunAcknowledged.Count: 1 } &&
                committed.FirstRunAcknowledged[0] == new IssueItem(Side.Local, local.Path, local.Id) &&
                committed.Raise.Count == 0,
                "A7: a successful retained file upload acknowledges its exact marker in the record transaction.");
        }

        using (var fixture = new Fixture())
        {
            var local = await SeedAsync(fixture.Local, "Item", false, "new");
            var row = WithFirstRun(MakeRow(local, null), IssueSubject.FirstRunDeletionKind.None,
                firstRun: false, complete: false);
            StateChange? committed = null;
            using var store = new CommitStore(fixture.Store, change => committed = change);
            await ExecutorFor(fixture.Context with { Store = store }).ExecuteAsync(
                new SyncAction(ActionKind.Upload, row, Target: "Remote"), null, CancellationToken.None);
            Check.Require(committed is not null && committed.FirstRunAcknowledged.Count == 0 &&
                committed.Raise.Count == 0,
                "A12: an ordinary unmarked upload adds no first-run marker or notice.");
        }

        foreach (var kind in new[] { ActionKind.Record, ActionKind.None })
        {
            using var fixture = new Fixture();
            var local = await SeedAsync(fixture.Local, "Folder", true, "");
            var dropbox = await SeedAsync(fixture.Dropbox, "Folder", true, "");
            var row = WithFirstRun(MakeRow(local, dropbox),
                IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice);
            StateChange? committed = null;
            using var store = new CommitStore(fixture.Store, change => committed = change);
            await ExecutorFor(fixture.Context with { Store = store }).ExecuteAsync(
                kind == ActionKind.Record ? new SyncAction(kind, row) :
                    new SyncAction(kind, row, None: NoneReason.InSync), null, CancellationToken.None);
            Check.Require(committed is not null && committed.FirstRunAcknowledged.Count == 0 &&
                committed.Raise.All(item => item.Cause != TextKey.InfoDropboxHistoryUnchecked),
                "A13: an ordinary folder Record/InSync never acknowledges a retained folder or its history notice.");
        }

        using (var fixture = new Fixture())
        {
            var local = await SeedAsync(fixture.Local, "Item", false, "new");
            var row = WithFirstRun(MakeRow(local, null),
                IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice);
            StateChange? committed = null;
            using var store = new CommitStore(fixture.Store, change =>
            {
                committed = change;
                fixture.Store.Commit(fixture.Pair.PairId, change);
            });
            await ExecutorFor(fixture.Context with { Store = store }).ExecuteAsync(
                new SyncAction(ActionKind.Upload, row, Target: "Remote"), null, CancellationToken.None);
            Check.Require(store.AsyncCommits == 1 && committed is not null &&
                committed.FirstRunAcknowledged.Count == 1 &&
                committed.Raise.Any(item => item.Kind == IssueKind.Info &&
                    item.Cause == TextKey.InfoDropboxHistoryUnchecked),
                "A14: a successful Preserve upload writes its history Info and acknowledgement in one result.");
        }

        using (var fixture = new Fixture())
        {
            var local = await SeedAsync(fixture.Local, "Item", false, "same");
            var dropbox = await SeedAsync(fixture.Dropbox, "Remote", false, "same");
            var row = WithFirstRun(MakeRow(local, dropbox),
                IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice);
            StateChange? committed = null;
            using var store = new CommitStore(fixture.Store, change =>
            {
                committed = change;
                fixture.Store.Commit(fixture.Pair.PairId, change);
            });
            await ExecutorFor(fixture.Context with { Store = store }).ExecuteAsync(
                new SyncAction(ActionKind.None, row, None: NoneReason.InSync), null, CancellationToken.None);
            Check.Require(store.Commits == 1 && committed is not null && committed.FirstRunAcknowledged.Count == 1 &&
                committed.Raise.Any(item => item.Cause == TextKey.InfoDropboxHistoryUnchecked) &&
                committed.Close.Count > 0,
                "A14: an InSync Preserve file settles its history Info and acknowledgement in the closure commit.");
        }

        using (var fixture = new Fixture())
        {
            var local = await SeedAsync(fixture.Local, "Item", false, "same");
            var dropbox = await SeedAsync(fixture.Dropbox, "Remote", false, "same");
            var row = WithFirstRun(MakeRow(local, dropbox),
                IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice);
            StateChange? committed = null;
            using var store = new CommitStore(fixture.Store, change =>
            {
                committed = change;
                fixture.Store.Commit(fixture.Pair.PairId, change);
            });
            await ExecutorFor(fixture.Context with { Store = store }).ExecuteAsync(
                new SyncAction(ActionKind.Record, row), null, CancellationToken.None);
            Check.Require(store.RecordCommits == 1 && store.Commits == 0 && committed is not null &&
                committed.FirstRunAcknowledged.Count == 1 &&
                committed.Raise.Any(item => item.Kind == IssueKind.Info &&
                    item.Cause == TextKey.InfoDropboxHistoryUnchecked),
                "Q20: a retained Preserve file Record decorates the Info and acknowledgement and enters the first-run entry.");
        }

        using (var fixture = new Fixture())
        {
            var local = await SeedAsync(fixture.Local, "Item", false, "same");
            var dropbox = await SeedAsync(fixture.Dropbox, "Remote", false, "same");
            var row = WithFirstRun(MakeRow(local, dropbox), IssueSubject.FirstRunDeletionKind.Item);
            StateChange? committed = null;
            using var store = new CommitStore(fixture.Store, change => committed = change);
            await ExecutorFor(fixture.Context with { Store = store }).ExecuteAsync(
                new SyncAction(ActionKind.Record, row), null, CancellationToken.None);
            Check.Require(store.RecordCommits == 1 && store.Commits == 0 && committed is not null &&
                committed.FirstRunAcknowledged.Count == 1 && committed.Raise.Count == 0,
                "Q20: an ack-only retained Item Record keeps the existing first-run queue.");
        }

        using (var fixture = new Fixture())
        {
            var local = await SeedAsync(fixture.Local, "Item", false, "same");
            var dropbox = await SeedAsync(fixture.Dropbox, "Remote", false, "same");
            var row = WithFirstRun(MakeRow(local, dropbox), IssueSubject.FirstRunDeletionKind.None);
            using var store = new CommitStore(fixture.Store, _ => { });
            await ExecutorFor(fixture.Context with { Store = store }).ExecuteAsync(
                new SyncAction(ActionKind.Record, row), null, CancellationToken.None);
            Check.Require(store.RecordCommits == 1 && store.Commits == 0,
                "Q20: an unmarked first-run file Record stays queued.");
        }

        using (var fixture = new Fixture())
        {
            var local = await SeedAsync(fixture.Local, "Item", false, "same");
            var dropbox = await SeedAsync(fixture.Dropbox, "Remote", false, "same");
            var row = WithFirstRun(MakeRow(local, dropbox),
                IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice);
            var error = new InvalidOperationException("record refused");
            using var store = new CommitStore(fixture.Store, _ => throw error);
            var failed = false;
            try
            {
                await ExecutorFor(fixture.Context with { Store = store }).ExecuteAsync(
                    new SyncAction(ActionKind.Record, row), null, CancellationToken.None);
            }
            catch (InvalidOperationException actual) when (ReferenceEquals(actual, error)) { failed = true; }
            Check.Require(failed && store.Commits == 0 && store.RecordCommits == 1 &&
                fixture.Store.Issues(fixture.Pair.PairId).Count == 0 &&
                fixture.Store.SyncedByKey(fixture.Pair.PairId, Side.Local, row.Key) is null,
                "Q20: a failed Preserve Record keeps the marker and writes no partial Info, acknowledgement or record.");
        }

        using (var fixture = new Fixture())
        {
            var local = await SeedAsync(fixture.Local, "Folder", true, "");
            var row = WithFirstRun(MakeRow(local, null),
                IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice);
            StateChange? committed = null;
            using var store = new CommitStore(fixture.Store, change =>
            {
                committed = change;
                fixture.Store.Commit(fixture.Pair.PairId, change);
            });
            var observed = row with { Dropbox = await SeedAsync(fixture.Dropbox, "Folder", true, "") };
            await ExecutorFor(fixture.Context with { Store = store }).ExecuteAsync(
                new SyncAction(ActionKind.EnsureDropboxFolder, observed, Target: "Folder"),
                null, CancellationToken.None, firstRunFolder: row);
            Check.Require(committed is not null && committed.FirstRunAcknowledged.Count == 1 &&
                committed.Raise.Any(item => item.Cause == TextKey.InfoDropboxHistoryUnchecked),
                "Q16: only the settled original-folder completion operand acknowledges the folder and its history Info.");
        }

        using (var fixture = new Fixture())
        {
            var local = await SeedAsync(fixture.Local, "Folder", true, "");
            var row = WithFirstRun(MakeRow(local, null), IssueSubject.FirstRunDeletionKind.Item);
            var different = row with { Local = local with { Path = "Other", Id = "other" } };
            var refused = false;
            try
            {
                await fixture.Executor.ExecuteAsync(
                    new SyncAction(ActionKind.EnsureDropboxFolder, row, Target: "Folder"),
                    null, CancellationToken.None, firstRunFolder: different);
            }
            catch (ChangedUnderneathException) { refused = true; }
            Check.Require(refused && fixture.Store.Issues(fixture.Pair.PairId).Count == 0,
                "Q16: an original-folder completion with a different identity refuses before any effect or acknowledgement.");
        }

        using (var fixture = new Fixture())
        {
            var local = await SeedAsync(fixture.Local, "Item", false, "new");
            var row = WithFirstRun(MakeRow(local, null),
                IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancelled = false;
            try
            {
                await fixture.Executor.ExecuteAsync(
                    new SyncAction(ActionKind.Upload, row, Target: "Remote"), null, cancellation.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }
            Check.Require(cancelled && fixture.Store.Issues(fixture.Pair.PairId).Count == 0 &&
                fixture.Store.SyncedByKey(fixture.Pair.PairId, Side.Local, row.Key) is null,
                "Q8: cancellation before an effect acknowledges nothing and replays nothing.");
        }
    }

    private static async Task AsyncDurabilityAsync()
    {
        using var fixture = new Fixture();
        var local = await SeedAsync(fixture.Local, "Item", false, "new");
        var row = MakeRow(local, null);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        StateChange? committed = null;
        using var store = new CommitStore(fixture.Store, change => committed = change,
            async change =>
            {
                await gate.Task;
                committed = change;
            });
        var execution = ExecutorFor(fixture.Context with { Store = store }).ExecuteAsync(
            new SyncAction(ActionKind.Upload, row, Target: "Remote"), null, CancellationToken.None);
        Check.Require(store.AsyncCommits == 1 && !execution.IsCompleted && fixture.Events.Count == 0,
            "CV-RECORD: an acknowledged upload awaits its own durable result before any event or outcome.");
        gate.SetResult();
        var outcome = await execution;
        Check.Require(committed is not null && outcome.Effect == EffectKind.Uploaded &&
            fixture.Events.Single().Kind == RunEventKind.Effect,
            "CV-RECORD: the effect and outcome follow the completed durable result.");
    }

    private static async Task CommittedIssueAsync()
    {
        using (var fixture = new Fixture())
        {
            var incoming = await SeedAsync(fixture.Local, "Item", false, "new");
            var occupant = await SeedAsync(fixture.Dropbox, "Target", false, "old");
            var row = MakeRow(incoming, null);
            Issue? delivered = null;
            using var store = new CommitStore(fixture.Store, _ => { });
            await ExecutorFor(fixture.Context with { Store = store }).FailAsync(row,
                new SyncAction(ActionKind.Upload, row, Target: occupant.Path),
                new PathOccupiedException(occupant.Path), CancellationToken.None, issue => delivered = issue);
            var stored = fixture.Store.Issues(fixture.Pair.PairId).Single(issue => issue.Kind == IssueKind.NameClash);
            Check.Require(delivered is not null && delivered.Id == stored.Id && delivered.Path == occupant.Path &&
                delivered.Subject.Incoming == Side.Local &&
                delivered.Subject.Items.Any(item => item == new IssueItem(Side.Dropbox, occupant.Path, occupant.Id)),
                "Q17: the committed-issue callback receives the exact stored NameClash, occupant included.");
        }

        using (var fixture = new Fixture())
        {
            var local = await SeedAsync(fixture.Local, "Item", false, "new");
            var row = MakeRow(local, null);
            var error = new InvalidOperationException("commit refused");
            var delivered = false;
            using var store = new CommitStore(_ => throw error);
            var failed = false;
            try
            {
                await ExecutorFor(fixture.Context with { Store = store }).FailAsync(row, null,
                    new ItemFailedException(row.Local!.Path, TextKey.ReadLocalFailed, null),
                    CancellationToken.None, _ => delivered = true);
            }
            catch (InvalidOperationException actual) when (ReferenceEquals(actual, error)) { failed = true; }
            Check.Require(failed && !delivered, "Q17: a failed commit delivers no committed issue.");
        }

        using (var fixture = new Fixture())
        {
            var local = await SeedAsync(fixture.Local, "Item", false, "new");
            var row = MakeRow(local, null);
            var error = new InvalidOperationException("attachment refused");
            var propagated = false;
            using var store = new CommitStore(fixture.Store, _ => { });
            try
            {
                await ExecutorFor(fixture.Context with { Store = store }).FailAsync(row, null,
                    new ItemFailedException(row.Local!.Path, TextKey.ReadLocalFailed, null),
                    CancellationToken.None, _ => throw error);
            }
            catch (Exception actual) when (ReferenceEquals(actual, error)) { propagated = true; }
            var issue = fixture.Store.Issues(fixture.Pair.PairId).Single();
            Check.Require(propagated && issue.Kind == IssueKind.Failure,
                "Q17: an attachment failure propagates unchanged after the issue is durably committed.");
        }
    }

    private static async Task UploadStagedAsync()
    {
        foreach (var staged in new[] { false, true })
        {
            using var fixture = new Fixture();
            var local = await SeedAsync(fixture.Local, "Item", false, "new");
            var row = MakeRow(local, null);
            var callbacks = 0;
            Func<ValueTask>? onStaged = staged
                ? () => { callbacks++; return ValueTask.CompletedTask; }
                : null;
            var source = new ReadCounter(fixture.Local);
            var outcome = await ExecutorFor(fixture.Context with { Local = source }).ExecuteAsync(
                new SyncAction(ActionKind.Upload, row, Target: "Remote"), null, CancellationToken.None,
                uploadStaged: onStaged);
            Check.Require(source.Opens == 1 && source.LastAttempt is null,
                "B-14.2: an upload's Local source read forwards the null request attempt.");
            Check.Require(staged
                    ? callbacks == 1 && fixture.Admission.Completed == 0
                    : callbacks == 0 && fixture.Admission.Completed == 1 && outcome.Effect == EffectKind.Uploaded,
                "Q18: a supplied upload-stage callback runs once and suppresses ordinary completion; null keeps it.");
        }

        using var download = new Fixture();
        var remote = await SeedAsync(download.Dropbox, "Remote", false, "new");
        var rowToWrite = MakeRow(null, remote);
        var read = new ReadCounter(download.Dropbox);
        var written = new ReadCounter(download.Local);
        var outcome2 = await ExecutorFor(download.Context with { Dropbox = read, Local = written })
            .ExecuteAsync(new SyncAction(ActionKind.Download, rowToWrite, Target: "Local"), null, CancellationToken.None);
        Check.Require(read.Opens == 1 && read.LastAttempt is not null &&
            outcome2.Effect == EffectKind.Downloaded,
            "B-14.2: a download forwards the non-null request attempt to the Dropbox source read.");
    }

    private static async Task ItemAttemptAsync()
    {
        foreach (var cause in new[] { TextKey.FailLocalInUse, TextKey.FailLocalInUseOrAccessDenied })
        {
            using var fixture = new Fixture(BoundPair());
            var local = await SeedAsync(fixture.Local, "Item", false, "new");
            var row = MakeRow(local, null);
            var first = await fixture.Executor.FailAsync(row, new SyncAction(ActionKind.Upload, row, Target: "Remote"),
                new ItemFailedException(local.Path, cause, null), CancellationToken.None);
            var issue = fixture.Store.Issues(fixture.Pair.PairId).Single();
            var state = fixture.Store.ReadItemRetryState(fixture.Pair.PairId, [issue.Id])[issue.Id];
            Check.Require(first.Raised == IssueKind.Failure && issue.Kind == IssueKind.Failure &&
                issue.Cause == cause && issue.Path == local.Path && state is not null &&
                state.Token.Revision == 0 && fixture.Store.ReadPrerequisiteRetry(fixture.Pair.PairId) is null,
                "R-INUSE: an in-use failure retains one exact issue/work/lifecycle and no pair retry timer.");

            var notified = new List<Issue>();
            var second = await fixture.Executor.FailAsync(row, new SyncAction(ActionKind.Upload, row, Target: "Remote"),
                new ItemFailedException(local.Path, cause, null), CancellationToken.None, notified.Add);
            Check.Require(notified.Count == 1 && notified[0].Id == issue.Id,
                "A continuing registered failure delivers its committed issue exactly once.");
            var after = fixture.Store.ReadItemRetryState(fixture.Pair.PairId, [issue.Id])[issue.Id];
            Check.Require(second.Raised == IssueKind.Failure &&
                fixture.Store.Issues(fixture.Pair.PairId).Count == 1 && after is not null &&
                after.Token.IssueId == issue.Id && after.Token.Revision == 1,
                "R-INUSE: a repeated identical failure reuses the same lifecycle and advances only its CAS revision.");

            var completed = await fixture.Executor.ExecuteAsync(
                new SyncAction(ActionKind.Upload, row, Target: "Remote"), null, CancellationToken.None);
            Check.Require(completed.Effect == EffectKind.Uploaded &&
                fixture.Store.ReadItemRetryState(fixture.Pair.PairId, [issue.Id])[issue.Id] is null &&
                fixture.Store.SyncedByKey(fixture.Pair.PairId, Side.Dropbox,
                    PathNames.DropboxKey("Remote")) is not null,
                "R-INUSE: a genuine success settles its exact lifecycle and mapping atomically through the attempt.");
        }
    }

    private static async Task RegisteredNoticeCallbackAsync()
    {
        using var fixture = new Fixture(BoundPair());
        var local = await SeedAsync(fixture.Local, "Item", false, "new");
        var row = MakeRow(local, null);
        await fixture.Executor.FailAsync(row, null, new ItemFailedException(local.Path, TextKey.FailLocalInUse, null), default);
        var prior = fixture.Store.Issues(fixture.Pair.PairId).Single();
        var expected = fixture.Store.ReadItemRetryState(fixture.Pair.PairId, [prior.Id])[prior.Id]!;
        var notified = new List<Issue>();
        await fixture.Context.ItemAttempts.ExecuteAsync(new object(), expected, expected.Token.Subject,
            (attempt, token) => fixture.Executor.ExecuteAsync(
                new SyncAction(ActionKind.Notice, row, NoticeKind: IssueKind.Tie, Cause: TextKey.TieEqualTimes), null, token,
                committedIssue: notified.Add, attemptId: attempt, expectedAttempt: expected), default);
        Check.Require(notified is [{ Kind: IssueKind.Tie }] &&
            fixture.Store.IssueById(fixture.Pair.PairId, notified[0].Id)?.Kind == IssueKind.Tie &&
            fixture.Store.ReadItemRetryState(fixture.Pair.PairId, [prior.Id])[prior.Id] is null,
            "A registered in-use attempt that becomes a Tie delivers that committed issue and retires its old episode.");
    }

    // Per-action selector costs the executor now calls instead of inline expressions.
    private static void Measured()
    {
        var local = new Entry("Local", false, 1, DateTimeOffset.UnixEpoch, "local-id", "rev", "hash", null);
        var dropbox = local with { Path = "Remote", Id = "dropbox-id" };
        var row = new Row(PathNames.Key(local.Path), local, dropbox, null, [], null,
            new RowContext(false, false, false, null, false, false, false, false));
        var items = new[] { new IssueItem(Side.Local, local.Path, local.Id) };
        var actions = new[]
        {
            new SyncAction(ActionKind.Upload, row), new SyncAction(ActionKind.Download, row),
            new SyncAction(ActionKind.DeleteLocal, row), new SyncAction(ActionKind.DeleteDropbox, row),
            new SyncAction(ActionKind.MoveLocal, row), new SyncAction(ActionKind.MoveDropbox, row),
            new SyncAction(ActionKind.MoveUnlinked, row, Side: Side.Dropbox),
            new SyncAction(ActionKind.ReplaceKind, row, Side: Side.Local)
        };
        var settled = new SyncAction(ActionKind.None, row);
        var upload = new SyncAction(ActionKind.Upload, row);

        // A measured equality: the owner must allocate exactly what its inline expression did.
        static void Equal(string label, Action<int> migrated, Action<int> baseline)
        {
            var actual = AllocProbe.Measure(migrated);
            var expected = AllocProbe.Measure(baseline);
            Console.WriteLine($"{label}: migrated={actual} B, baseline={expected} B per 1000 calls");
            Check.Require(actual == expected, $"{label} allocates exactly as its inline baseline.");
        }

        // A measured ceiling for the event factory: no added allocation is the gate.
        static void AtMost(string label, Action<int> migrated, Action<int> baseline)
        {
            var actual = AllocProbe.Measure(migrated);
            var expected = AllocProbe.Measure(baseline);
            Console.WriteLine($"{label}: migrated={actual} B, baseline={expected} B per 1000 calls");
            Check.Require(actual <= expected, $"{label} adds no allocation over its constructor.");
        }

        Equal("SyncAction.WrittenSide", index =>
        {
            for (var i = 0; i < 1_000; i++) Sink(actions[i % actions.Length].WrittenSide());
        }, index =>
        {
            for (var i = 0; i < 1_000; i++)
            {
                var action = actions[i % actions.Length];
                Sink(action.Kind switch
                {
                    ActionKind.Upload or ActionKind.EnsureDropboxFolder or ActionKind.DeleteDropbox
                        or ActionKind.MoveDropbox => Side.Dropbox,
                    ActionKind.Download or ActionKind.EnsureLocalFolder or ActionKind.DeleteLocal
                        or ActionKind.MoveLocal => Side.Local,
                    ActionKind.ReplaceKind or ActionKind.MoveUnlinked =>
                        action.Side ?? throw new ArgumentException(nameof(action.Side)),
                    _ => throw new ArgumentOutOfRangeException(nameof(action))
                });
            }
        });

        Equal("Row.EntryOn", index =>
        {
            for (var i = 0; i < 1_000; i++) Sink(row.EntryOn(i % 2 == 0 ? Side.Local : Side.Dropbox));
        }, index =>
        {
            for (var i = 0; i < 1_000; i++) Sink(i % 2 == 0 ? row.Local : row.Dropbox);
        });

        Equal("EffectKinds.Deleted", index =>
        {
            for (var i = 0; i < 1_000; i++) Sink(EffectKinds.Deleted(i % 2 == 0 ? Side.Local : Side.Dropbox));
        }, index =>
        {
            for (var i = 0; i < 1_000; i++) Sink(i % 2 == 0 ? EffectKind.DeletedLocal : EffectKind.DeletedDropbox);
        });

        Equal("EffectKinds.Moved", index =>
        {
            for (var i = 0; i < 1_000; i++) Sink(EffectKinds.Moved(i % 2 == 0 ? Side.Local : Side.Dropbox));
        }, index =>
        {
            for (var i = 0; i < 1_000; i++) Sink(i % 2 == 0 ? EffectKind.MovedLocal : EffectKind.MovedDropbox);
        });

        Equal("SyncAction.IsInSync", index =>
        {
            for (var i = 0; i < 1_000; i++) Sink((i % 2 == 0 ? settled : upload).IsInSync());
        }, index =>
        {
            for (var i = 0; i < 1_000; i++)
            {
                var action = i % 2 == 0 ? settled : upload;
                Sink(action.Kind == ActionKind.None && action.None == NoneReason.InSync);
            }
        });

        Equal("IssueSubject.FromDistinct", index =>
        {
            for (var i = 0; i < 1_000; i++) Sink(IssueSubject.FromDistinct(items, Side.Local));
        }, index =>
        {
            for (var i = 0; i < 1_000; i++) Sink(new IssueSubject(items.Distinct().ToArray(), Side.Local));
        });

        AtMost("RunEvent.Effect, ordinary", index =>
        {
            for (var i = 0; i < 1_000; i++)
                Sink(RunEvent.Effect(i % 2 == 0 ? EffectKind.DeletedLocal : EffectKind.Uploaded, "Path", "Detail",
                    i % 3 == 0 ? null : i % 2 == 0 ? Side.Local : Side.Dropbox));
        }, index =>
        {
            for (var i = 0; i < 1_000; i++)
            {
                var kind = i % 2 == 0 ? EffectKind.DeletedLocal : EffectKind.Uploaded;
                var side = i % 3 == 0 ? (Side?)null : i % 2 == 0 ? Side.Local : Side.Dropbox;
                Sink(new RunEvent(RunEventKind.Effect, kind.ToString(), "Path", "Detail", side?.ToString() ?? ""));
            }
        });

        AtMost("RunEvent.Effect, keep-both", index =>
        {
            for (var i = 0; i < 1_000; i++)
                Sink(RunEvent.Effect(EffectKind.KeptBoth, "Label", "Original", Side.Dropbox));
        }, index =>
        {
            for (var i = 0; i < 1_000; i++)
                Sink(new RunEvent(RunEventKind.Effect, nameof(EffectKind.KeptBoth), "Label", "Original",
                    nameof(Side.Dropbox)));
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sink(string? value)
    {
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sink(Side? value)
    {
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sink(EffectKind value)
    {
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sink(Entry? value)
    {
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sink(bool value)
    {
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sink(IssueSubject value)
    {
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Sink(RunEvent value)
    {
    }

    private static async Task<Entry> SeedAsync(ISide side, string path, bool folder, string content)
    {
        if (folder) return await side.EnsureFolderAsync(path, CancellationToken.None);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var source = new Entry(path, false, stream.Length, DateTimeOffset.UnixEpoch, path, "", "", null);
        await using var stage = await side.StageAsync(path, stream, source, CancellationToken.None);
        return (await side.PublishAsync(stage, null, null, CancellationToken.None)).Final!;
    }

    private static Row MakeRow(Entry? local, Entry? dropbox) => new(PathNames.Key(local?.Path ?? dropbox?.Path ?? "Item"),
        local, dropbox, null, [], null, new RowContext(false, false, false, null, false, false, false, false));

    private static SyncedEntry Saved(string local, string dropbox) => new(PathNames.Key(local), local,
        PathNames.DropboxKey(dropbox), dropbox, false, local, 0, 0, dropbox, "");

    private static PairConfig BoundPair() => new("pair", "Pair A", "C:/Fixture", "/Fixture", "account", [], [],
        FirstRunPolicy.NewestWins, true, null,
        new RootBinding("C:/Fixture", "/Fixture", "account", new DirectoryIdentity(1, 2, null),
            new DropboxRootIdentity("account", "namespace", "home", "folder")));

    private static void RaiseAll(Fixture fixture, Row row)
    {
        var subject = new IssueSubject(new[] { new IssueItem(Side.Local, row.Local?.Path ?? "Local", row.Local?.Id ?? "") }, null);
        foreach (var path in new[] { row.Local?.Path, row.Dropbox?.Path }.OfType<string>().Distinct())
            fixture.Store.Commit(fixture.Pair.PairId, StateChange.Empty with
            {
                Raise = Enum.GetValues<IssueKind>().Select(kind => (kind, TextKey.FailChanged, path, subject)).ToArray()
            });
    }

    private static Side Other(Side side) => side == Side.Local ? Side.Dropbox : Side.Local;
    private static string Key(Side side, string path) => side == Side.Local ? PathNames.Key(path) : PathNames.DropboxKey(path);

    private sealed class TrackedAdmission(bool strict = true) : IAdmission
    {
        public int Level => 1;
        public int WorkCapacity => 0;
        public int Admitted { get; private set; }
        public int Completed { get; private set; }
        public bool? Success { get; private set; }
        public Task<IAsyncDisposable> AdmitAsync(bool priority, CancellationToken ct)
        {
            Admitted++;
            throw new InvalidOperationException("Inner actions must not acquire admission.");
        }
        public void ActionCompleted(bool success)
        {
            if (strict) Check.Require(Success is null, "Each Dropbox action reports completion exactly once.");
            Success = success;
            Completed++;
        }
        public Task<IAsyncDisposable> EnterWorkAsync(bool singleFile, CancellationToken ct) =>
            throw new NotSupportedException("These checks never enter an outer work scope.");
        public void DisallowDirectUpload() =>
            throw new NotSupportedException("These checks never revoke direct upload.");
        public Task<IAsyncDisposable> AdmitUploadAsync(
            (string AccountId, string RouteName, string SourceIdentity) identity, CancellationToken ct) =>
            throw new NotSupportedException("These checks never acquire an upload permit.");
        public void UploadStaged() =>
            throw new NotSupportedException("These checks never report a staged upload.");
    }

    private sealed class ReadCounter(ISide inner) : ISide
    {
        public int Reads { get; private set; }
        public int Opens { get; private set; }
        public int Deletes { get; private set; }
        public int Listings { get; private set; }
        public int Moves { get; private set; }
        public int Publishes { get; private set; }
        public RequestAttempt? LastAttempt { get; private set; }
        public Side Side => inner.Side;
        public IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options, CancellationToken ct)
        {
            Listings++;
            return inner.ReadAllAsync(under, options, ct);
        }
        public Task<Entry?> ReadOneAsync(string path, CancellationToken ct)
        {
            Reads++;
            return inner.ReadOneAsync(path, ct);
        }
        public Task<Stream> OpenReadAsync(Entry entry, RequestAttempt? attempt, CancellationToken ct)
        {
            Opens++;
            LastAttempt = attempt;
            return inner.OpenReadAsync(entry, attempt, ct);
        }
        public Task<string> ContentHashAsync(Entry entry, CancellationToken ct) => inner.ContentHashAsync(entry, ct);
        public Task<IStaged> StageAsync(string path, Stream content, Entry source, CancellationToken ct) =>
            inner.StageAsync(path, content, source, ct);
        public Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection,
            CancellationToken ct)
        {
            Publishes++;
            return inner.PublishAsync(staged, expected, selection, ct);
        }
        public Task<Entry> EnsureFolderAsync(string path, CancellationToken ct) => inner.EnsureFolderAsync(path, ct);
        public async Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct)
        {
            var result = await inner.DeleteAsync(expected, selection, ct);
            Deletes++;
            return result;
        }
        public Task<Entry> MoveAsync(Entry expected, string newPath, CancellationToken ct)
        {
            Moves++;
            return inner.MoveAsync(expected, newPath, ct);
        }
        public Task<FolderSubmitResult> SubmitFolderRemovalAsync(FolderSubmission submission,
            IReadOnlyList<FolderRemovalEffect> effects, RequestAttempt attempt, CancellationToken ct) =>
            throw new NotSupportedException("These checks submit no folder removal.");
        public Task<FolderJobResult> ReadFolderRemovalAsync(string jobId, int expectedCount,
            RequestAttempt attempt, CancellationToken ct) =>
            throw new NotSupportedException("These checks observe no folder removal.");
    }

    /// <summary>One submission's exact durable and callback semantics over an optional inner store.</summary>
    private sealed class CommitStore(IStateStore? inner, Action<StateChange> commit,
        Func<StateChange, Task>? asyncCommit = null) : IStateStore
    {
        public CommitStore(Action<StateChange> commit) : this(null, commit) { }

        public int Commits { get; private set; }
        public int AsyncCommits { get; private set; }
        public int RecordCommits { get; private set; }

        public void Commit(string pairId, StateChange change, WorkSelection? selection, Action<Issue>? committedIssue) =>
            (inner ?? throw new NotSupportedException()).Commit(pairId, change, selection, committedIssue);

        public void Commit(string pairId, StateChange change, Action<Issue>? committedIssue = null)
        {
            Commits++;
            if (committedIssue is not null && inner is not null)
            {
                inner.Commit(pairId, change, committedIssue);
                return;
            }
            commit(change);
            if (committedIssue is not null)
                throw new NotSupportedException("Only an inner store supplies the exact committed issue.");
        }

        public Task CommitAsync(string pairId, StateChange change)
        {
            AsyncCommits++;
            if (asyncCommit is not null) return asyncCommit(change);
            commit(change);
            return Task.CompletedTask;
        }

        public Task CommitRecordAsync(string pairId, StateChange change, Action<Issue>? committedIssue)
        {
            RecordCommits++;
            commit(change);
            return Task.CompletedTask;
        }

        public (PairConfig Pair, long Generation)? ReadPairExecution(string pairId) =>
            throw new NotSupportedException();
        public ItemRetryCandidate? ReadItemRetryForSubject(string pairId, long expectedGeneration,
            string path, IssueSubject subject) =>
            inner is null
                ? throw new NotSupportedException()
                : inner.ReadItemRetryForSubject(pairId, expectedGeneration, path, subject);
        public long ReadWorkWatermark() => throw new NotSupportedException();
        public void RecordPrerequisiteFailure(string pairId, long expectedGeneration,
            long localEpoch, long dropboxEpoch, long localRevision, long dropboxRevision,
            Massdex.SyncShark.V2.FailureRetryOwner.State? expected,
            Massdex.SyncShark.V2.FailureRetryOwner.State next, StateChange failureNotice) => throw new NotSupportedException();
        public void CompleteRecomposition(string pairId, long expectedGeneration, long localEpoch, long dropboxEpoch,
            long localRevision, long dropboxRevision, Massdex.SyncShark.V2.FailureRetryOwner.State expected) =>
            throw new NotSupportedException();
        public void SaveImportedPair(PairConfig pair, bool legacyPaused, IEnumerable<SyncedEntry>? baselines = null) => throw new NotSupportedException();
        public void MergeDriveEvidence(string pairId, long expectedGeneration, DriveEvidence acquired) =>
            throw new NotSupportedException();
        public void ReconcileRootIdentity(string pairId, long expectedGeneration, RootBinding? expectedBinding,
            Issue? expectedIssue, StateChange issueOnlyChange) => throw new NotSupportedException();
        public void SavePairDecision(PairConfig expected, long expectedGeneration, PairConfig next,
            Issue? expectedIssue, bool resetHistory, string? reincludedKey) => throw new NotSupportedException();

        public void Dispose() { }
        public IReadOnlyList<PairConfig> Pairs() => throw new NotSupportedException();
        public PairConfig? Pair(string pairId) => throw new NotSupportedException();
        public void SavePair(PairConfig pair) => throw new NotSupportedException();
        public void RemovePair(string pairId) => throw new NotSupportedException();
        public bool FirstRepairDone(string pairId) => throw new NotSupportedException();
        public SourceObligation LostIntervalObligation(string pairId) => throw new NotSupportedException();
        public void RequireRepairAfterLoss(string pairId, long expectedGeneration) => throw new NotSupportedException();
        public void RequireRepairForOwedHistory(string pairId, long expectedGeneration) => throw new NotSupportedException();
        public RepairSummary? LastRepair(string pairId) => throw new NotSupportedException();
        public void MarkRepairLogFailed(string pairId, string runId) => throw new NotSupportedException();
        public void FinishRepair(string pairId, RepairSummary summary) => throw new NotSupportedException();
        public bool FinishRepair(string pairId, RepairSummary summary, long expectedGeneration, Guid scanToken,
            LocalCatchUp local, DropboxCatchUp dropbox) => throw new NotSupportedException();
        public ScanRecord ReadScan(string pairId, Guid scanToken) => throw new NotSupportedException();
        public void ForgetAllSynced(string pairId) => throw new NotSupportedException();
        public int? LimiterLevel(string connectionId) => throw new NotSupportedException();
        public void SaveLimiterLevel(string connectionId, int level) => throw new NotSupportedException();
        public SyncedEntry? SyncedByKey(string pairId, Side side, string key) => throw new NotSupportedException();
        public SyncedEntry? SyncedById(string pairId, Side side, string id) => throw new NotSupportedException();
        public IReadOnlyList<SyncedEntry> SyncedBindingsById(string pairId, Side side, string id) =>
            inner?.SyncedBindingsById(pairId, side, id) ?? [];
        public RenameContinuation? ReadRenameContinuation(WorkSelection selection) =>
            inner?.ReadRenameContinuation(selection) ?? throw new NotSupportedException();
        public void RegisterRenameContinuation(WorkSelection selection, SyncedEntry incoming, SyncedEntry destination, Issue? clash) =>
            (inner ?? throw new NotSupportedException()).RegisterRenameContinuation(selection, incoming, destination, clash);
        public IReadOnlyList<(SyncedEntry Binding, bool LocalKeyMatched, bool DropboxKeyMatched)> RenameDestinationOwners(
            string pairId, SyncedEntry? before, SyncedEntry after) =>
            inner?.RenameDestinationOwners(pairId, before, after) ?? throw new NotSupportedException();
        public PendingRead Pending(string pairId) => throw new NotSupportedException();
        public void ArmPending(string pairId, PendingEffect value) => throw new NotSupportedException();
        public void AdvancePending(string pairId, PendingEffect expected, PendingEffect next) =>
            throw new NotSupportedException();
        public void SettlePending(string pairId, PendingEffect expected, StateChange change, Action<Issue>? committedIssue = null) =>
            throw new NotSupportedException();
        public void SettlePending(string pairId, PendingEffect expected, StateChange change, WorkSelection? selection, Action<Issue>? committedIssue) =>
            throw new NotSupportedException();
        public void SettleLegacyPending(string pairId, string rawToken, StateChange issueOnlyChange) =>
            throw new NotSupportedException();
        public JournalPosition? ReadJournal(string volumeKey) => throw new NotSupportedException();
        public long? AcquireJournalPage(string volumeKey, JournalPosition? expected, JournalPosition next,
            IReadOnlyList<(string PairId, long Generation, long Epoch, long Revision,
                bool LostInterval)> members,
            IEnumerable<(string PairId, ChangeNotice Notice)> notices) => throw new NotSupportedException();
        public (long Epoch, SourceProvenance? Provenance, string? Cursor, SourceObligation? Obligation, long Revision)?
            ReadSource(string pairId, Side side) =>
            inner is null ? throw new NotSupportedException() : inner.ReadSource(pairId, side);
        public bool Obligated(string pairId) => throw new NotSupportedException();
        public LiveRefusal ReadLiveRefusal(string pairId) => throw new NotSupportedException();
        public long? ReadLocalGap(string pairId, long localEpoch) => throw new NotSupportedException();
        public long AcquireDropboxPage(string pairId, long expectedGeneration, long expectedEpoch,
            long expectedRevision, SourceProvenance provenance, string? expectedCursor,
            IEnumerable<ChangeNotice> notices, string cursor) => throw new NotSupportedException();
        public void AdvanceSource(string pairId, Side side, long expectedGeneration,
            long expectedEpoch, long expectedRevision, SourceProvenance? expectedProvenance,
            string? expectedCursor, long nextEpoch, long nextRevision, SourceProvenance? provenance,
            string? cursor, SourceObligation? obligation) => throw new NotSupportedException();
        public WorkSelection? SelectWork(string pairId, long expectedGeneration, PathScope scope) =>
            throw new NotSupportedException();
        public bool EndWorkPass(string pairId) => throw new NotSupportedException();
        public IEnumerable<PendingChange> ReadWork(WorkSelection selection) => throw new NotSupportedException();
        public IEnumerable<WorkSelection> PendingAncestors(string pairId, long expectedGeneration, Side side,
            IReadOnlyList<string> ancestorKeys, long? coveredThrough = null) => throw new NotSupportedException();
        public AncestorAnswer LaterAncestorNews(string pairId, long expectedGeneration, Side side,
            IReadOnlyList<string> ancestorKeys, long coveredThrough) => throw new NotSupportedException();
        public Func<Side, string, string?, bool> CreateSeparateWorkProbe(WorkSelection selection) => throw new NotSupportedException();
        public Func<SyncedEntry, long, bool> CreateLocalEchoProbe(WorkSelection selection) => throw new NotSupportedException();
        public void ClaimSelectionPeer(WorkSelection selection, SelectionPeer peer) { }
        public void EndSelection(WorkSelection selection) { }
        public void BeginSelection(WorkSelection selection) => throw new NotSupportedException();
        public void RequireDiscovery(WorkSelection selection, long workId) => throw new NotSupportedException();
        public bool CompleteWork(WorkSelection selection, SelectionPeer? peer) => throw new NotSupportedException();
        public bool AttachIssueWork(WorkSelection parent, Issue exactIssue,
            IEnumerable<(ChangeNotice Notice, bool IsFolder, SyncedEntry? Before)> unfinished) =>
            throw new NotSupportedException();
        public (bool Dirty, JournalPosition? SourceBoundary, long EvidenceVersion) ReadLocalDirty(
            string pairId, long expectedGeneration, long localSourceEpoch, string nativeDomain,
            string? fullIdentity, string? currentPathKey, string? recordedPathKey) =>
            throw new NotSupportedException();
        public PendingRead ReadPendingForFolderRecovery(string pairId, FolderRemovalState operation) =>
            throw new NotSupportedException();
        public void ValidatePendingForFolderRecovery(string pairId, FolderRemovalState operation,
            PendingRead expectedPending) => throw new NotSupportedException();
        public LiveCoverage CaptureLiveCoverage(WorkSelection selection, LocalCatchUp local,
            DropboxCatchUp dropbox) => throw new NotSupportedException();
        public void ValidateLiveCoverage(WorkSelection selection, LiveCoverage coverage,
            IReadOnlyList<LiveEffectScope> footprint) => throw new NotSupportedException();
        public Massdex.SyncShark.V2.FailureRetryOwner.State? ReadPrerequisiteRetry(string pairId, Issue? expected = null) =>
            throw new NotSupportedException();
        public IEnumerable<(long PairRow, string PairId, long Generation,
            Massdex.SyncShark.V2.FailureRetryOwner.State State)> ReadPrerequisiteRetries(long? afterTicks,
            long? afterPairRow, int pageSize) => throw new NotSupportedException();
        public Issue RecordPrerequisitePageWait(string pairId, long expectedGeneration, object operation,
            StateChange failureNotice) => throw new NotSupportedException();
        public void CompletePrerequisitePageWait(string pairId, long expectedGeneration, object operation,
            Issue expectedNotice) => throw new NotSupportedException();
        public ItemRetryCandidate RecordInUseFailure(string pairId, long expectedGeneration, long sourceEpoch,
            string path, IssueSubject subject, TextKey cause, WorkSelection? selection,
            ItemRetryDispatch dispatch, PendingEffect? expectedPending, ItemRetryToken? expectedEpisode,
            Guid registeredAttemptId) => throw new NotSupportedException();
        public bool TryClaimItemRetry(ItemRetryCandidate expected, Guid registeredAttemptId) =>
            throw new NotSupportedException();
        public void CompleteItemAttempt(ItemRetryCandidate expected, Guid registeredAttemptId,
            StateChange change, PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null) => throw new NotSupportedException();
        public IReadOnlyDictionary<long, ItemRetryCandidate?> ReadItemRetryState(string pairId,
            IReadOnlyList<long> issueIds, Issue? expected = null) => throw new NotSupportedException();
        public FolderRemovalItem InsertFolderMember(string pairId, string operationId, long expectedGeneration,
            FolderRemovalItem.ProvenanceKind provenance, string path, Entry? observed, Entry? selected) =>
            throw new NotSupportedException();
        public string BeginFolderRemoval(string pairId, FolderPlan plan, int pageSize, CancellationToken ct, WorkSelection renameWork) =>
        (inner ?? throw new NotSupportedException()).BeginFolderRemoval(pairId, plan, pageSize, ct, renameWork);
    public string? RenameRemoval(WorkSelection selection) => (inner ?? throw new NotSupportedException()).RenameRemoval(selection);
    public long? RenameRemovalWork(string pairId, string operationId) => (inner ?? throw new NotSupportedException()).RenameRemovalWork(pairId, operationId);
    public string BeginFolderRemoval(string pairId, FolderPlan plan, int pageSize, CancellationToken ct) => throw new NotSupportedException();
        public FolderRemovalState FolderRemoval(string pairId, string operationId) =>
            throw new NotSupportedException();
        public IEnumerable<FolderRemovalState> ActiveFolderRemovals(string pairId, CancellationToken ct) =>
            throw new NotSupportedException();
        public IEnumerable<FolderRemovalEffect> ReadyFolderEffects(string pairId, string operationId,
            long afterOrdinal, int pageSize) => throw new NotSupportedException();
        public IEnumerable<FolderRemovalItem> PendingFolderFollowUps(string pairId, string operationId,
            long afterOrdinal, int pageSize) => throw new NotSupportedException();
        public void CompleteFolderFollowUp(string pairId, string operationId, long ordinal,
            long expectedGeneration) => throw new NotSupportedException();
        public IEnumerable<FolderRemovalItem> FolderRemovalItems(string pairId, string operationId,
            long afterOrdinal, int pageSize) => throw new NotSupportedException();
        public FolderRemovalItem? FolderRemovalItemByKey(string pairId, string operationId, string key) =>
            throw new NotSupportedException();
        public void AdvanceFolderRemoval(string pairId, string operationId, long expectedGeneration,
            FolderRemovalPhase expectedPhase, FolderRemovalState next) => throw new NotSupportedException();
        public void SettleFolderRemovalItem(string pairId, string operationId, long ordinal,
            long expectedGeneration, FolderRemovalItem next, StateChange verifiedChange) =>
            throw new NotSupportedException();
        public IReadOnlyList<bool> SettleFolderRemovalItems(string pairId, string operationId,
            IReadOnlyList<(long ExpectedGeneration, FolderRemovalItem Next, StateChange Change)> items) =>
            throw new NotSupportedException();
        public void CompleteFolderRemoval(string pairId, string operationId, long expectedGeneration,
            StateChange verifiedRootChange, WorkSelection? selection) => throw new NotSupportedException();
        public void RetireFolderRemoval(string pairId, string operationId, long expectedGeneration,
            FolderRetirementDisposition disposition, StateChange issueOnlyAcknowledgement) =>
            throw new NotSupportedException();
        public string ArmFolderSubmission(string pairId, IReadOnlyList<FolderRemovalEffect> effects) =>
            throw new NotSupportedException();
        public FolderSubmission FolderSubmission(string submissionId) => throw new NotSupportedException();
        public IReadOnlyList<FolderRemovalEffect> FolderSubmissionEffects(string submissionId) =>
            throw new NotSupportedException();
        public IEnumerable<FolderSubmission> PendingFolderSubmissions(string pairId, CancellationToken ct, bool progressOnly = false) =>
            throw new NotSupportedException();
        public bool HasFolderJobProgress(string pairId, CancellationToken ct) => throw new NotSupportedException();
        public void AdvanceFolderSubmission(FolderSubmission expected, FolderSubmission next,
            IReadOnlyList<FolderMemberResult> results) => throw new NotSupportedException();
        public void RecordFolderJobObservation(FolderSubmission expected,
            FolderJobResult? observation, TextKey? failureCause) => throw new NotSupportedException();
        public bool HasInvalidFolderJob(string pairId, string path) => throw new NotSupportedException();
        public IReadOnlyList<Issue> Issues(string pairId, IReadOnlyList<IssueKind>? kinds = null) =>
            throw new NotSupportedException("An ordinary closure must not load issues.");
        public (IReadOnlyList<(string PairId, Issue Issue)> Page, int Total) OpenIssuePage(string? pairId,
            string? account, string? search, IReadOnlyList<IssueKind>? kinds, int limit, int offset, bool ignored = false) =>
            throw new NotSupportedException();
        public (IReadOnlyList<(string PairId, Issue Issue)> Rows, int Total) OpenIssueSnapshot(
            IReadOnlyList<IssueKind>? kinds, int pageSize) => throw new NotSupportedException();
        public bool SetIssueIgnored(string pairId, Issue expected, bool ignored) => throw new NotSupportedException();
        public IEnumerable<Issue> OpenIssuesUnder(string pairId, string scopeKey, IReadOnlyList<IssueKind>? kinds = null) =>
            throw new NotSupportedException();
        public Issue? IssueById(string pairId, long id) => throw new NotSupportedException();
        public IssueKind? HeldKind(string pairId, Row row, IReadOnlySet<long>? exceptIssues) =>
            throw new NotSupportedException();
        public bool HeldCovers(string pairId, string key, IReadOnlySet<long>? exceptIssues) =>
            throw new NotSupportedException();
        public bool HeldByOtherIssue(string pairId, Row row, long selectedIssueId) =>
            throw new NotSupportedException();
        public Issue? HoldingIssue(string pairId, Row row) => throw new NotSupportedException();
        public IReadOnlyDictionary<string, IssueSubject> RetainedFirstRun(string pairId) =>
            throw new NotSupportedException();
        public IReadOnlyDictionary<IssueKind, int> IssueCounts(string pairId, bool? ignored = null) => throw new NotSupportedException();
        public long SyncedCount(string pairId) => throw new NotSupportedException();
        public void ReplaceSkipped(string pairId, Guid scanToken,
            IEnumerable<(string Path, TextKey Cause, IssueSubject Subject)> skipped) =>
            throw new NotSupportedException();
        public void ObserveSkipped(string pairId, long expectedGeneration, Side side, string path,
            (TextKey Cause, IssueSubject Subject)? currentSkip) => throw new NotSupportedException();
        public bool RetryNoticeProtected(string pairId, long issueId) => throw new NotSupportedException();
        public void DismissIssue(string pairId, long id) => throw new NotSupportedException();
        public void KeepFolderAlways(string pairId, long expectedGeneration, Issue expected,
            IReadOnlyList<Massdex.SyncShark.SyncIgnoreRule> keepRules) => throw new NotSupportedException();
        public Task EndScanAsync(string pairId, Guid scanId, int pageSize) => throw new NotSupportedException();
        public bool CloseScanIssue(string pairId, Guid scanId, Issue expected) => throw new NotSupportedException();
        public Guid BeginScan(string pairId, Guid scanId, long expectedGeneration, bool includeDeleted, string? scope = null) =>
            throw new NotSupportedException();
        public bool HasDeletedNames(string pairId, Guid scanId) => throw new NotSupportedException();
        public bool DeletedNameExact(string pairId, Guid scanId, string key) => throw new NotSupportedException();
        public bool SavedDeletedNameExact(string pairId, string key) => (inner ?? throw new NotSupportedException()).SavedDeletedNameExact(pairId, key);
        public bool DeletedNameSpent(string pairId, string key) => throw new NotSupportedException();
        public IScanSink OpenScan(string pairId, Guid scanId, Side side, int batchRows) => throw new NotSupportedException();
        public bool DeletedHistoryOwed(string pairId) => (inner ?? throw new NotSupportedException()).DeletedHistoryOwed(pairId);
        public string? ContinueDeletedHistory(string pairId, Guid scanToken) => (inner ?? throw new NotSupportedException()).ContinueDeletedHistory(pairId, scanToken);

        public string? RestoreDeletedHistory(string pairId, Guid scanToken, Action<long>? restored = null) => (inner ?? throw new NotSupportedException()).RestoreDeletedHistory(pairId, scanToken, restored);
        public void ResetDeletedHistory(string pairId, Guid scanToken, bool discardNames) => (inner ?? throw new NotSupportedException()).ResetDeletedHistory(pairId, scanToken, discardNames);
        public void SaveDeletedHistory(string pairId, Guid scanToken, string cursor) => (inner ?? throw new NotSupportedException()).SaveDeletedHistory(pairId, scanToken, cursor);
        public FolderPlan OpenFolderPlan(string pairId, Row folder, Side deletingFrom) =>
            throw new NotSupportedException();
        public bool UnreadableTouches(string pairId, string localKey, bool isFolder) => false;
        public IReadOnlyList<string> UnreadableUnder(string pairId, string localKey) => [];
        public bool ReleaseUnreadable(string pairId, long expectedGeneration, string localKey, string acquiredId) =>
            throw new NotSupportedException();
        public bool ForgetTouchesUnreadable(string pairId, IReadOnlyList<(Side Side, string Key)> roots,
            CancellationToken ct) => false;
        public IEnumerable<Row> ScanRows(string pairId, Guid scanId, Func<Row, RowContext> context) =>
            throw new NotSupportedException();
        public IReadOnlyList<RenameCandidate> ScanRenames(string pairId, Guid scanId) => throw new NotSupportedException();
        public Entry? ScanEntry(string pairId, Guid scanId, Side side, string key) => throw new NotSupportedException();
        public bool InScan(string pairId, Guid scanId, Side side, string key) => throw new NotSupportedException();
    }

    /// <summary>The real item-attempt authority over the in-memory store; it never mints a token itself.</summary>
    private sealed class ItemAttemptStore(InMemoryStateStore store, string pairId, long generation) : IItemAttempts
    {
        public Task<T> ExecuteAsync<T>(object opportunity, ItemRetryCandidate? expected, IssueSubject subject,
            Func<Guid, CancellationToken, Task<T>> execute, CancellationToken ct) =>
            execute(Guid.NewGuid(), ct);
        public ItemRetryCandidate ReportFailure(Guid attemptId, long sourceEpoch, string path,
            IssueSubject subject, TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch,
            PendingEffect? expectedPending, ItemRetryToken? expectedEpisode) =>
            store.RecordInUseFailure(pairId, generation, sourceEpoch, path, subject, cause, selection, dispatch,
                expectedPending, expectedEpisode, attemptId);
        public void Complete(Guid attemptId, ItemRetryCandidate expected, StateChange change,
            PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null) =>
            store.CompleteItemAttempt(expected, attemptId, change, completedPending, selection, committedIssue);
    }

    private sealed class NoLease : IAsyncDisposable
    {
        public static readonly NoLease Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Fixture : IDisposable
    {
        public FakeSide Local { get; } = new(Side.Local);
        public FakeSide Dropbox { get; } = new(Side.Dropbox);
        public InMemoryStateStore Store { get; } = new();
        public FakeRecovery Recovery { get; } = new() { Evidence = new DriveEvidence(1, null, null) };
        public TrackedAdmission Admission { get; } = new(strict: false);
        public List<RunEvent> Events { get; } = [];
        public PairConfig Pair { get; }
        public ExecutorContext Context { get; }
        public ActionExecutor Executor { get; }

        public Fixture(PairConfig? pair = null)
        {
            Pair = pair ?? new PairConfig("pair", "Pair A", "C:/Fixture", "/Fixture", "account", [], [],
                FirstRunPolicy.NewestWins, true, null);
            Store.SavePair(Pair);
            Recovery.Hash = Local.ContentHashAsync;
            var settings = new EngineSettings("{name} ({side} {time}){ext}", "yyyyMMddHHmmss", "Local", "Dropbox",
                ".case", RecycleFallback.Fail, 1, FirstRunPolicy.NewestWins, 1, "", 262144, ScanTestSettings.BatchRows);
            Context = new ExecutorContext(
                Local: Local, Dropbox: Dropbox, Recovery: Recovery, Store: Store, Pair: Pair,
                Scope: new PathScope(Pair, [Pair]), Settings: settings, Runtime: new ReplaceTurn(),
                Admission: Admission, Priority: false, Events: e => RecordEvent(Store, e),
                ReserveDownloadAsync: (_, _, _, _) => ValueTask.FromResult<IAsyncDisposable>(NoLease.Instance),
                Time: TimeProvider.System,
                ReadDeletionEvidenceAsync: (_, _, _) => throw new NotSupportedException("These checks read no deletion evidence."),
                BeginDownloadAttempt: source => new RequestAttempt(source, AttemptOwner.Transfer),
                CompleteDownloadAttemptAsync: (_, _) => Task.FromResult(false),
                UploadAdmissionIdentity: (AccountId: "account", RouteName: "route", SourceIdentity: "source"),
                ItemAttempts: new ItemAttemptStore(Store, Pair.PairId, 0), Opportunity: new object(),
                ExpectedGeneration: 0);
            var transfers = new Transfers(Context);
            Executor = new ActionExecutor(Context, transfers, new Parents(Context), new KeepBothRunner(Context, transfers));
        }

        public void RecordEvent(IStateStore store, RunEvent e)
        {
            if (e.Kind == RunEventKind.Issue)
                Check.Require(store.Issues(Pair.PairId).Any(i => i.Path == e.Path && i.Kind.ToString() == Massdex.SyncShark.Engine.Run.RunLog.Present(e).What), nameof(IStateStore.Commit));
            Events.Add(e);
        }

        public void Dispose() => Store.Dispose();
    }
}

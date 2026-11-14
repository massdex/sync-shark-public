using System.Globalization;
using Microsoft.Data.Sqlite;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Planner;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Checks the durable folder-removal protocol: frozen selected effects, immutable submission receipts,
/// receipt-only reconciliation, lost-receipt custody and guarded pair removal.</summary>
public static class FolderDeleteRunnerChecks
{
    /// <summary>V16: the required eleventh settings argument, the production parser default.</summary>
    private const int LogBufferCharacters = 262144;

    private const int PageSize = 2;

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    public static void AbsentEpisode()
    {
        AbsentEpisodeAsync().GetAwaiter().GetResult();
        HeldIdentityEpisodesAsync().GetAwaiter().GetResult();
        HeldFolderMembershipAsync().GetAwaiter().GetResult();
    }

    public static void AbsentEpisodeCosts()
    {
        AbsentEpisodeCostsAsync().GetAwaiter().GetResult();
        HeldIdentityCostsAsync().GetAwaiter().GetResult();
    }

    private static async Task AbsentEpisodeCostsAsync()
    {
        foreach (var mode in new[] { "flow", "settlement" })
        foreach (var count in new[] { 3, 12 })
        foreach (var wait in new[] { false, true })
        foreach (var proven in new[] { false, true })
        // Warm the same call before recording SQL and allocations in separate fresh fixtures.
        foreach (var measure in new[] { "warm", "sql", "allocation" })
        {
            if (mode == "settlement" && (count != 3 || wait)) continue;
            var folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(folder);
            try
            {
                var attempt = Guid.NewGuid();
                using var f = new Fixture(folder, "Pair A", localEvidence: true,
                    registry: (id, _, _, _, _) => id == attempt, pageSize: count + 2);
                SourceFixture.AcquireJournal(f.Store, f.Pair);
                var root = await f.DropboxEntries.EnsureFolderAsync("Folder", default);
                var savedRoot = Saved(root.Path, root.Path);
                var records = new List<SyncedEntry> { savedRoot };
                await f.DropboxEntries.EnsureFolderAsync("Folder/Keep", default);
                for (var index = 0; index < count; index++)
                {
                    var child = await f.DropboxEntries.EnsureFolderAsync("Folder/Child-" + index, default);
                    records.Add(Saved(child.Path, child.Path));
                }
                f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = records });
                var operation = await BeginAsync(f, root, savedRoot, pageSize: count + 2);
                var members = f.Store.FolderRemovalItems(f.Pair.PairId, operation, -1, count + 2).ToArray();
                Check.Require(members.Length == count + 2 &&
                    !f.Store.FolderRemovalItems(f.Pair.PairId, operation, members[^1].Ordinal, count + 2).Any(),
                    "B222 B4/B5: every frozen member fits in the same single page");
                var target = members.Single(item => item.Path == "Folder/Child-1");
                var subject = new IssueSubject([new IssueItem(Side.Local, target.Path, "absent-id")], null);
                if (wait) f.Store.RecordInUseFailure(f.Pair.PairId, 0,
                    f.Store.ReadSource(f.Pair.PairId, Side.Local)!.Value.Epoch, target.Path, subject,
                    TextKey.FailLocalInUse, null, ItemRetryDispatch.OrdinaryItem, null, null, attempt);
                else f.Store.Commit(f.Pair.PairId, StateChange.Empty with
                {
                    Raise = [(IssueKind.Failure, TextKey.FailLocalInUse, target.Path, subject)]
                });
                var issue = f.Store.Issues(f.Pair.PairId).Single();
                var effects = f.Store.ReadyFolderEffects(f.Pair.PairId, operation, -1, count + 2).ToArray();
                Check.Require(effects.Length == count && effects.Any(effect => effect.ItemOrdinal == target.Ordinal),
                    "B222 B4/B5: M independent child effects include the notice target");
                f.Dropbox.Returns(new FolderSubmitResult.Terminal(effects.Select((effect, index) =>
                    (FolderMemberResult)new FolderMemberResult.Receipt(index, effect.Expected!)).ToArray()));
                var statements = 0;
                var commits = 0;
                var clears = 0;
                var connection = (SqliteConnection)typeof(StateStore).GetField("_connection",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(f.RawStore)!;
                var clearPrefix = StateSchema.ClearItemEpisode[..StateSchema.ClearItemEpisode.IndexOf('$')];
                void Trace(bool enabled) => SQLitePCL.raw.sqlite3_trace(connection.Handle, enabled && measure == "sql"
                    ? (SQLitePCL.delegate_trace)((_, text) =>
                    {
                        var sql = text.utf8_to_string().TrimStart();
                        statements++;
                        if (sql.StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase)) commits++;
                        if (sql.StartsWith(clearPrefix, StringComparison.Ordinal)) clears++;
                    }) : null!, null);
                long allocated = 0;
                ISet<string> absent;
                Trace(mode == "flow");
                var before = GC.GetTotalAllocatedBytes(precise: true);
                try { absent = await f.Runner.RunPreparedAsync([effects], default); }
                finally
                {
                    if (mode == "flow") allocated += GC.GetTotalAllocatedBytes(precise: true) - before;
                    Trace(false);
                }
                Check.Require(absent.Contains(operation), "B222 B5: the measured removal has null source evidence");
                foreach (var effect in effects) await f.DropboxEntries.DeleteAsync(effect.Expected!, null, default);
                var current = f.Store.FolderRemoval(f.Pair.PairId, operation);
                target = Item(f, operation, target.Ordinal);
                var change = StateChange.Empty with
                {
                    ForgetSubtrees = [(Side.Local, target.Saved!.LocalKey), (Side.Dropbox, target.Saved.DropboxKey)],
                    Close = [(target.Path, IssueClosure.AfterEffect)], CloseProvenAbsent = proven
                };
                (long ExpectedGeneration, FolderRemovalItem Next, StateChange Change)[] settlement =
                [(target.Generation, target with
                {
                    Generation = target.Generation + 1, EffectGeneration = target.EffectGeneration + 1,
                    EffectState = FolderRemovalItem.EffectStatus.Settled,
                    Phase = FolderRemovalItem.RecoveryPhase.Settled, Cause = null
                }, change)];
                IReadOnlyList<bool>? settled = null;
                Trace(true);
                before = mode == "flow" ? GC.GetTotalAllocatedBytes(precise: true) : GC.GetAllocatedBytesForCurrentThread();
                try
                {
                    if (mode == "flow") await f.Runner.ResumeAsync(current, null, default, proven ? absent : null);
                    else settled = f.RawStore.SettleFolderRemovalItems(f.Pair.PairId, operation, settlement);
                }
                finally
                {
                    allocated += (mode == "flow" ? GC.GetTotalAllocatedBytes(precise: true) :
                        GC.GetAllocatedBytesForCurrentThread()) - before;
                    Trace(false);
                }
                Check.Require(mode == "flow" ? effects.All(effect =>
                    Item(f, operation, effect.ItemOrdinal).EffectState == FolderRemovalItem.EffectStatus.Settled) :
                    settled is not null && settled.SequenceEqual(new[] { true }),
                    "B222 B4/B5: every selected effect settles while the unsaved survivor stays untouched");
                Check.Require(await f.DropboxEntries.ReadOneAsync("Folder/Keep", default) is not null &&
                    f.Store.FolderRemoval(f.Pair.PairId, operation).Active,
                    "B222 B4/B5: the unsaved survivor and its unfinished root are preserved");
                var remains = f.Store.Issues(f.Pair.PairId).Any(row => row.Id == issue.Id);
                var episodeRemains = f.Store.ReadItemRetryState(f.Pair.PairId, [issue.Id])[issue.Id] is not null;
                Check.Require(wait ? remains == episodeRemains : !remains,
                    "B222 B4/B5: ordinary notices close and live notice/episode lifetimes agree");
                if (measure == "sql") Check.Require(clears == (wait && !episodeRemains ? 1 : 0),
                    "B222 B4: each ended wait executes exactly one episode clear");
                if (measure != "warm") Console.WriteLine("B222 B4/B5 mode=" + mode + " M=" + count +
                    " W=" + (wait ? 1 : 0) + " proven=" + proven + " measure=" + measure +
                    (measure == "sql" ? " statements=" + statements + " commits=" + commits + " clear=" + clears :
                        " allocated-bytes=" + allocated) + " notice-remains=" + remains);
            }
            finally { Directory.Delete(folder, true); }
        }
    }

    private static bool SameStoredToken(ItemRetryToken? actual, ItemRetryToken expected) =>
        actual is not null && actual.Subject.HasSameItems(expected.Subject) &&
        actual with { Subject = actual.Subject with { Items = expected.Subject.Items } } == expected;

    private static async Task AbsentEpisodeAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var scenario in new[] { "R1", "R2", "R2-control", "R3", "R4", "RL3a", "RL1", "RL3b", "R5", "R7", "R10", "R11", "R12", "RL2b", "RL2b-unreadable", "RL4b", "F1", "F2", "F3", "F4" })
            {
                var attempt = Guid.NewGuid();
                using var f = new Fixture(folder, scenario, localEvidence: true,
                    registry: (id, _, _, subject, _) => id == attempt || scenario == "R5" ||
                        scenario == "R12" && subject.Items.Any(item => item.Path == "Folder/Held"));
                SourceFixture.AcquireJournal(f.Store, f.Pair);
                var sourceEpoch = f.Store.ReadSource(f.Pair.PairId, Side.Local)!.Value.Epoch;
                var root = await f.DropboxEntries.EnsureFolderAsync("Folder", default);
                var rootWait = scenario is "RL3a" or "RL3b" or "F1" or "F2" or "F3" or "F4";
                Entry target;
                if (rootWait) target = root;
                else
                {
                    await f.DropboxEntries.EnsureFolderAsync("Folder/Kept", default);
                    target = await f.DropboxEntries.EnsureFolderAsync("Folder/Gone", default);
                }
                var revoked = scenario is "RL2b" or "RL2b-unreadable";
                var heldMember = scenario is "R7" or "R12" || revoked
                    ? await f.DropboxEntries.EnsureFolderAsync("Folder/Held", default) : null;
                var savedRoot = Saved(scenario == "R10" ? "Other" : root.Path, root.Path);
                f.Store.Commit(f.Pair.PairId, StateChange.Empty with
                {
                    Record = rootWait ? [savedRoot] : [savedRoot, Saved(target.Path, target.Path)]
                });
                if (heldMember is not null) f.Store.Commit(f.Pair.PairId,
                    StateChange.Empty with { Record = [Saved(heldMember.Path, heldMember.Path)] });
                string operation;
                if (scenario == "R3")
                {
                    var counterpart = await f.Local.EnsureFolderAsync(root.Path, default);
                    using var captured = f.Store.OpenFolderPlan(f.Pair.PairId,
                        Row(root, savedRoot) with { Local = counterpart }, Side.Dropbox);
                    await captured.CaptureAsync(f.Dropbox.ReadAllAsync(root.Path,
                        new ListOptions(Recursive: true, IncludeOutOfScope: true), default), default);
                    var excluded = await new FolderPlanBuilder(f.Context).BuildExcludedAsync(captured,
                        new Exclusion(Side.Dropbox, root.Path, false), default);
                    operation = f.Store.BeginFolderRemoval(f.Pair.PairId, excluded, StateSchema.OneRow, default);
                }
                else operation = await BeginAsync(f, root, savedRoot);
                var effects = f.Store.ReadyFolderEffects(f.Pair.PairId, operation, -1, PageSize).ToArray();
                Check.Require(effects.Length == (heldMember is null ? 1 : 2),
                    "B222 removal fixture selects exactly its saved branches");
                var subject = new IssueSubject([new IssueItem(Side.Local, target.Path, "old-id")], null);
                if (scenario == "R11") subject = subject with { Items = [.. subject.Items, new(Side.Dropbox, "Elsewhere", "remote-id")] };
                if (scenario == "R7") subject = subject with
                {
                    Move = new IssueSubject.UnknownMove("remote-id", "Source", "Destination", "Intermediate", false)
                };
                WorkSelection? acquired = null;
                var openPass = scenario is "R1" or "R2" or "R2-control" or "R7";
                if (openPass)
                {
                    var local = f.Store.ReadSource(f.Pair.PairId, Side.Local)!.Value;
                    var path = scenario == "R1" ? target.Path : "Unrelated";
                    var identity = scenario == "R1" ? "old-id" : "unrelated-id";
                    Check.Require(f.Store.AcquireJournalPage(
                        f.Pair.Roots!.Local.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture),
                        new JournalPosition(1, 1), new JournalPosition(1, 2),
                        [(f.Pair.PairId, 0, local.Epoch, local.Revision, false)],
                        [(f.Pair.PairId, new ChangeNotice(Side.Local, ChangeKind.Changed, path, null, identity, null, false, false))]) is not null,
                        "B222 acquired fixture publishes its Local notice");
                    var selected = f.RawStore.SelectWork(f.Pair.PairId, 0, f.Context.Scope);
                    Check.Require(selected is { } row && row.Key == PathNames.Key(path),
                        "B222 " + scenario + ": runnable selection opens the work pass");
                    if (scenario == "R1") acquired = selected;
                }
                var episode = f.Store.RecordInUseFailure(f.Pair.PairId, 0, sourceEpoch, target.Path, subject,
                    TextKey.FailLocalInUse, acquired, ItemRetryDispatch.OrdinaryItem, null, null, attempt);
                var untouched = new List<ItemRetryCandidate>();
                if (scenario == "R4")
                    foreach (var path in new[] { "Folder/Kept", "Folder/NonMember" })
                        untouched.Add(f.Store.RecordInUseFailure(f.Pair.PairId, 0, sourceEpoch, path,
                            new IssueSubject([new IssueItem(Side.Local, path, "unselected-id")], null),
                            TextKey.FailLocalInUse, null, ItemRetryDispatch.OrdinaryItem, null, null, attempt));
                var heldEpisode = scenario != "R12" ? null : f.Store.RecordInUseFailure(f.Pair.PairId, 0, sourceEpoch,
                    heldMember!.Path, new IssueSubject([new IssueItem(Side.Local, heldMember.Path, "held-id")], null),
                    TextKey.FailLocalInUse, null, ItemRetryDispatch.OrdinaryItem, null, null, attempt);
                var composed = scenario is "F1" or "F2" or "F3" or "F4";
                var jobs = SyncJobs.Create(f.Context, new SyncPorts(new PassthroughBoundary(),
                    RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
                var folders = jobs.Items.Folders;
                if (scenario is "F2" or "F3" or "F4")
                {
                    f.Dropbox.Returns(new FolderSubmitResult.Accepted("job"));
                    // RunPrepared and the first Resume each observe a failure; F3/F4 defer the second Resume too.
                    var joins = scenario == "F2" ? 2 : 3;
                    for (var join = 0; join < joins; join++)
                        f.Dropbox.Throws(new ItemFailedException(root.Path, TextKey.DropboxRefused, null));
                    if (scenario == "F2")
                        f.Dropbox.Returns(new FolderJobResult.Terminal([new FolderMemberResult.Receipt(0, target)]));
                }
                var submitted = revoked ? effects.Take(1).ToArray() : effects;
                if (scenario is not ("F2" or "F3" or "F4"))
                    f.Dropbox.Returns(new FolderSubmitResult.Terminal(submitted.Select((effect, index) =>
                        (FolderMemberResult)new FolderMemberResult.Receipt(index, effect.Expected!)).ToArray()));
                var absent = composed ? await folders.RunPreparedFolderRemovalsAsync([operation], default)
                    : await f.Runner.RunPreparedAsync([submitted], default);
                Check.Require(absent.Contains(operation) == (scenario is not ("R3" or "R10")),
                    "B222 pre-send proof is confined to the deleting root's own opposite path");
                await f.DropboxEntries.DeleteAsync(target, null, default);
                if (heldMember is not null && !revoked) await f.DropboxEntries.DeleteAsync(heldMember, null, default);
                if (scenario == "RL2b") await f.Local.EnsureFolderAsync(root.Path, default);
                if (scenario == "RL2b-unreadable") f.LocalEntries.FailNext(root.Path,
                    new ItemFailedException(root.Path, TextKey.FailLocalInUse, null));
                var restarted = scenario is "RL1" or "RL3b";
                if (restarted) await f.Local.EnsureFolderAsync(target.Path, default);
                if (scenario == "RL4b")
                {
                    var connection = (SqliteConnection)typeof(StateStore).GetField("_connection",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(f.RawStore)!;
                    using var fault = StateStore.Connect(connection.DataSource, SqliteOpenMode.ReadWrite);
                    using var corrupt = fault.CreateCommand();
                    corrupt.CommandText = "UPDATE change_work SET issue_work_key='fixture-mismatch' WHERE work_id=$work;";
                    corrupt.Parameters.AddWithValue("$work", episode.Token.WorkId);
                    Check.Require(corrupt.ExecuteNonQuery() == 1, "B222 RL4b alters exactly the owned issue-work key");
                    var refused = false;
                    try { await f.Runner.ResumeAsync(f.Store.FolderRemoval(f.Pair.PairId, operation), null, default, absent); }
                    catch (InvalidOperationException) { refused = true; }
                    Check.Require(refused, "B222 RL4b: a mismatched issue-work key refuses settlement");
                    Check.Require(SameStoredToken(f.Store.ReadItemRetryState(f.Pair.PairId, [episode.Token.IssueId])[episode.Token.IssueId]?.Token, episode.Token) &&
                        f.Store.Issues(f.Pair.PairId).Any(issue => issue.Id == episode.Token.IssueId) &&
                        f.Store.CountWork(f.Pair.PairId, 0, f.Store.ReadWorkWatermark()) == (0, 0) &&
                        Item(f, operation, effects[0].ItemOrdinal).EffectState == FolderRemovalItem.EffectStatus.Verified &&
                        f.Store.SyncedByKey(f.Pair.PairId, Side.Dropbox, PathNames.DropboxKey(target.Path)) is not null,
                        "B222 RL4b: failed work deletion rolls back member, binding, notice and episode together");
                    continue;
                }
                if (composed)
                {
                    await folders.FinishFolderRemovalAsync(operation, null, default, absent: absent);
                    if (scenario is "F3" or "F4")
                    {
                        Check.Require(f.Store.ReadItemRetryState(f.Pair.PairId, [episode.Token.IssueId])[episode.Token.IssueId] is not null,
                            "B222 deferred first and second joins keep their wait");
                        if (scenario == "F3")
                        {
                            f.Dropbox.Returns(new FolderJobResult.InProgress());
                            await jobs.Recovery.ReconcileReceiptsAsync(default);
                            f.Dropbox.Returns(new FolderJobResult.Terminal([new FolderMemberResult.Receipt(0, target)]));
                            var finishes = 0;
                            await jobs.Recovery.ReconcileReceiptsAsync(default, progressOnly: true, async (id, ct) =>
                            {
                                finishes++;
                                await folders.FinishFolderRemovalAsync(id, null, ct);
                            });
                            Check.Require(finishes == 1, "B222 F3 reaches its progress-only finish callback");
                        }
                        else
                        {
                            f.Dropbox.Returns(new FolderJobResult.Terminal([new FolderMemberResult.Receipt(0, target)]));
                            var pending = f.Store.FolderRemoval(f.Pair.PairId, operation);
                            await jobs.Recovery.RecoverAsync(new RecoveryTarget(operation, pending.Generation, null, null),
                                RecoveryChoice.Check, default);
                        }
                    }
                }
                else
                {
                    var statements = new List<string>();
                    var connection = scenario is not ("R1" or "R2") ? null : (SqliteConnection)typeof(StateStore).GetField("_connection",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(f.RawStore)!;
                    if (connection is not null) SQLitePCL.raw.sqlite3_trace(connection.Handle,
                        (SQLitePCL.delegate_trace)((_, sql) => statements.Add(sql.utf8_to_string().Trim())), null);
                    try
                    {
                        await (restarted ? f.NewRunner() : f.Runner).ResumeAsync(
                            f.Store.FolderRemoval(f.Pair.PairId, operation), null, default,
                            restarted || scenario == "R2-control" ? null : absent);
                    }
                    finally
                    {
                        if (connection is not null) SQLitePCL.raw.sqlite3_trace(connection.Handle,
                            (SQLitePCL.delegate_trace)null!, null);
                    }
                    if (connection is not null)
                    {
                        static bool Matches(string actual, string template) =>
                            actual.StartsWith(template[..template.IndexOf('$')], StringComparison.Ordinal);
                        var clear = statements.FindIndex(sql => Matches(sql, StateSchema.ClearItemEpisode));
                        Check.Require(clear >= 0 && statements.Count(sql => Matches(sql, StateSchema.ClearItemEpisode)) == 1,
                            "B222 RL4: the exact episode is cleared once");
                        var begin = statements.FindLastIndex(clear, sql => sql.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase));
                        var commit = statements.FindIndex(clear, sql => sql.StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase));
                        var transaction = statements.Skip(begin).Take(commit - begin + 1).ToArray();
                        Check.Require(begin >= 0 && commit > clear &&
                            transaction.Any(sql => Matches(sql, StateSchema.SettleEffect)) &&
                            (scenario != "R2" || transaction.Any(sql => Matches(sql, StateSchema.DeleteSettledIssueWork))) &&
                            transaction.Any(sql => Matches(sql, StateSchema.DeleteSyncedDropboxKey)) &&
                            transaction.Any(sql => Matches(sql, StateSchema.CloseIssueExact)) &&
                            transaction.Count(sql => sql.StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase)) == 1,
                            "B222 RL4 " + scenario + ": member, binding, episode, applicable source-less work and notice settle in one transaction");
                    }
                }
                if (revoked) Check.Require(!absent.Contains(operation) &&
                    f.Store.ReadyFolderEffects(f.Pair.PairId, operation, -1, PageSize).Count() == 1,
                    "B222 " + scenario + ": a failed fresh opposite check revokes proof and leaves its effect unsent");
                if (scenario == "RL2b-unreadable")
                    Check.Require(await f.LocalEntries.ReadOneAsync(root.Path, default) is null,
                        "B222 RL2b-unreadable: the fresh check consumed the queued root fault");
                var retained = scenario is not ("R1" or "R2" or "R4" or "R12" or "RL3a" or "F1");
                Check.Require(f.Store.FolderRemovalItems(f.Pair.PairId, operation, -1, int.MaxValue)
                    .Single(item => item.Ordinal == effects[0].ItemOrdinal).EffectState == FolderRemovalItem.EffectStatus.Settled,
                    "B222 " + scenario + ": ordinary verified settlement still completes");
                Check.Require((f.Store.ReadItemRetryState(f.Pair.PairId, [episode.Token.IssueId])[episode.Token.IssueId] is not null) == retained,
                    "B222 " + scenario + ": episode retirement obeys proof lifetime and retention");
                Check.Require(f.Store.Issues(f.Pair.PairId).Any(issue => issue.Id == episode.Token.IssueId) == retained,
                    "B222 " + scenario + ": notice and episode retire together");
                if (openPass)
                {
                    Check.Require(f.RawStore.EndWorkPass(f.Pair.PairId) == !retained,
                        "B222 " + scenario + ": only an ended episode requests a repeat pass");
                    if (scenario == "R1")
                    {
                        var drain = f.RawStore.SelectWork(f.Pair.PairId, 0, f.Context.Scope);
                        Check.Require(drain is { } row && row.AnchorWorkId == episode.Token.WorkId,
                            "B222 R1: acquired work is unheld and selected after episode retirement");
                        Check.Require(f.RawStore.CompleteWork(drain!, null),
                            "B222 R1: ordinary completion drains acquired work");
                    }
                }
                if (scenario == "R7")
                {
                    Check.Require(effects.All(effect => Item(f, operation, effect.ItemOrdinal).EffectState ==
                            FolderRemovalItem.EffectStatus.Settled &&
                            f.Store.SyncedByKey(f.Pair.PairId, Side.Dropbox, PathNames.DropboxKey(effect.Expected!.Path)) is null),
                        "B222 R7: both verified members settle and forget their bindings despite retained responsibility");
                    Check.Require(SameStoredToken(f.Store.ReadItemRetryState(f.Pair.PairId, [episode.Token.IssueId])[episode.Token.IssueId]?.Token, episode.Token) &&
                        f.Store.Issues(f.Pair.PairId).Single(issue => issue.Id == episode.Token.IssueId).Subject.Move == subject.Move,
                        "B222 R7: the unknown-move notice and exact episode token remain");
                }
                if (heldEpisode is not null)
                {
                    Check.Require(SameStoredToken(f.Store.ReadItemRetryState(f.Pair.PairId, [heldEpisode.Token.IssueId])[heldEpisode.Token.IssueId]?.Token, heldEpisode.Token) &&
                        f.Store.Issues(f.Pair.PairId).Any(issue => issue.Id == heldEpisode.Token.IssueId),
                        "B222 R12: a previous close never authorizes a retained member's notice close");
                    Check.Require(f.Store.FolderRemovalItems(f.Pair.PairId, operation, -1, int.MaxValue)
                        .Single(item => item.Path == heldMember!.Path).EffectState == FolderRemovalItem.EffectStatus.Settled,
                        "B222 R12: retained member still settles its verified effect");
                }
                if (scenario == "R4")
                {
                    var syncer = new Syncer(f.Context with { ItemAttempts = new PassingItemAttempts() },
                        new SyncPorts(new PassthroughBoundary(), RefusingExecutionBoundary.Instance,
                            RefusingExecutionBoundary.Instance, 0));
                    await syncer.SyncWorkAsync(default);
                }
                if (scenario is "R1" or "R2")
                    Check.Require(f.Store.SyncedByKey(f.Pair.PairId, Side.Dropbox, PathNames.DropboxKey(target.Path)) is null,
                        "B222 " + scenario + ": settled member forgets its saved binding");
                foreach (var unselected in untouched)
                    Check.Require(SameStoredToken(f.Store.ReadItemRetryState(f.Pair.PairId, [unselected.Token.IssueId])[unselected.Token.IssueId]?.Token, unselected.Token) &&
                        f.Store.Issues(f.Pair.PairId).Any(issue => issue.Id == unselected.Token.IssueId),
                        "B222 R4: survivor and non-member waits never retire by root prefix");
                // U is Acquired and counted; P is Issue work excluded by StateSchema.EligibilitySuffix (:775) and CountWork (:793-797).
                var expected = scenario == "R2" ? (1L, 0L) : (0L, 0L);
                if (!retained) Check.Require(f.Store.CountWork(f.Pair.PairId, 0, f.Store.ReadWorkWatermark()) == expected,
                    "B222 " + scenario + ": ended episode leaves only unrelated work");
                else if (openPass)
                {
                    Check.Require(SameStoredToken(f.Store.ReadItemRetryState(f.Pair.PairId,
                        [episode.Token.IssueId])[episode.Token.IssueId]?.Token, episode.Token),
                        "B222 " + scenario + ": joined retry candidate retains its exact episode and associated work");
                    var count = f.Store.CountWork(f.Pair.PairId, 0, f.Store.ReadWorkWatermark());
                    Check.Require(count == (1, 0), "B222 " + scenario +
                        ": unrelated acquired work remains beside the joined retained episode; count=" + count);
                }
            }
        }
        finally { Directory.Delete(folder, true); }
    }

    private static async Task HeldIdentityCostsAsync()
    {
        foreach (var (branches, descendants, held) in new[] { (1, 3, 0), (1, 12, 0), (4, 3, 0), (1, 3, 1), (1, 3, 2) })
        {
            var baseline = 0;
            foreach (var capable in new[] { false, true })
            {
                var folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
                Directory.CreateDirectory(folder);
                try
                {
                    var attempt = Guid.NewGuid();
                    var calls = new List<(Side Side, string Id)>();
                    Task<(IdentityLocation Location, bool InScope)?> ReadIdentity(Side side, string id, bool documented, CancellationToken ct)
                    {
                        calls.Add((side, id));
                        Check.Require(side != Side.Local || documented, "B222 B9: Local lookup uses documented absence");
                        return Task.FromResult<(IdentityLocation Location, bool InScope)?>(null);
                    }
                    using var f = new Fixture(folder, "B9", localEvidence: true,
                        registry: (id, _, _, _, _) => id == attempt, pageSize: 3, byId: capable ? ReadIdentity : null);
                    SourceFixture.AcquireJournal(f.Store, f.Pair);
                    var source = f.Store.ReadSource(f.Pair.PairId, Side.Local)!.Value;
                    var root = await f.DropboxEntries.EnsureFolderAsync("Folder", default);
                    await f.DropboxEntries.EnsureFolderAsync("Folder/Keep", default);
                    var savedRoot = Saved(root.Path, root.Path) with { LocalId = "local-root", DropboxId = "dropbox-root" };
                    var records = new List<SyncedEntry> { savedRoot };
                    var hash = DropboxContentHash.Compute(new byte[] { 1 });
                    for (var branch = 0; branch < branches; branch++)
                    {
                        var path = "Folder/Branch-" + branch;
                        await f.DropboxEntries.EnsureFolderAsync(path, default);
                        records.Add(Saved(path, path) with { LocalId = "local-branch-" + branch, DropboxId = "dropbox-branch-" + branch });
                    }
                    var targets = new List<SyncedEntry>();
                    foreach (var path in Enumerable.Range(0, descendants).Select(index => "Folder/Branch-0/Child-" + index)
                        .Append("Folder/File"))
                    {
                        var entry = new Entry(path, false, 1, default, "dropbox-" + path, "revision", hash, null);
                        f.DropboxEntries.Seed(entry, [1]);
                        var saved = Saved(path, path) with { IsFolder = false, LocalId = "local-" + path,
                            DropboxId = entry.Id, DropboxContentHash = hash };
                        records.Add(saved);
                        if (path != "Folder/File") targets.Add(saved);
                    }
                    records.Add(Saved("Folder/Absent", "Folder/Absent") with { IsFolder = false,
                        LocalId = "local-absent", DropboxId = "dropbox-absent", DropboxContentHash = hash });
                    f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = records });
                    var operation = await BeginAsync(f, root, savedRoot, pageSize: 3);
                    var members = f.Store.FolderRemovalItems(f.Pair.PairId, operation, -1, int.MaxValue).ToArray();
                    Check.Require(members.Count(item => item.EffectKind == FolderEffectKind.RecursiveFolder) == branches &&
                        members.Single(item => item.Path == "Folder/File").EffectKind == FolderEffectKind.ConditionalFile &&
                        members.Single(item => item.Path == "Folder/Absent").EffectKind == FolderEffectKind.StateOnly,
                        "B222 B9: R recursive branches share their window with conditional-file and state-only controls");
                    var episodes = targets.Take(held).Select(saved => f.Store.RecordInUseFailure(f.Pair.PairId, 0,
                        source.Epoch, saved.LocalPath, new IssueSubject([new IssueItem(Side.Local, saved.LocalPath, saved.LocalId)], null),
                        TextKey.FailLocalInUse, null, ItemRetryDispatch.OrdinaryItem, null, null, attempt)).ToArray();
                    var effects = f.Store.ReadyFolderEffects(f.Pair.PairId, operation, -1, int.MaxValue).ToArray();
                    var submitted = effects.Where(effect => Item(f, operation, effect.ItemOrdinal).EffectKind != FolderEffectKind.StateOnly).ToArray();
                    f.Dropbox.Returns(new FolderSubmitResult.Terminal(submitted.Select((effect, index) =>
                        (FolderMemberResult)new FolderMemberResult.Receipt(index, effect.Expected!)).ToArray()));
                    var connection = (SqliteConnection)typeof(StateStore).GetField("_connection",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(f.RawStore)!;
                    var statements = 0;
                    var identityReads = 0;
                    var writes = 0;
                    SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)((_, text) =>
                    {
                        var sql = text.utf8_to_string().TrimStart();
                        statements++;
                        if (sql.StartsWith("SELECT i.issue_id,i.kind,i.cause,i.path,i.subject_json,i.at_ticks,i.dismissed,i.ignored,m.saved_json", StringComparison.Ordinal)) identityReads++;
                        if (sql.StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase)) writes++;
                    }), null);
                    try
                    {
                        await f.Runner.RunPreparedAsync([effects], default);
                        foreach (var effect in submitted) await f.DropboxEntries.DeleteAsync(effect.Expected!, null, default);
                        await f.Runner.ResumeAsync(f.Store.FolderRemoval(f.Pair.PairId, operation), null, default);
                    }
                    finally { SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null); }
                    Check.Require(members.Where(item => item.SelectedBranch).All(item =>
                        Item(f, operation, item.Ordinal).EffectState == FolderRemovalItem.EffectStatus.Settled),
                        "B222 B9: all selected recursive and non-recursive controls completed");
                    Check.Require(episodes.All(episode =>
                        (f.Store.ReadItemRetryState(f.Pair.PairId, [episode.Token.IssueId])[episode.Token.IssueId] is null) == capable),
                        "B222 B9: only identity-capable settlement retires descendant episodes");
                    Check.Require(calls.SequenceEqual(capable ? targets.Take(held).SelectMany(saved =>
                        new[] { (Side.Local, saved.LocalId), (Side.Dropbox, saved.DropboxId) }) : []),
                        "B222 B9: exactly two logical calls per held member, no control or unheld IDs");
                    Check.Require(identityReads == (capable ? branches + (held == 0 ? 0 : 1) : 0),
                        "B222 B9: only completed recursive effects enumerate; incapable sides execute no query");
                    if (!capable) baseline = statements;
                    else if (held == 0) Check.Require(statements - baseline == branches,
                        "B222 B9: H=0 costs exactly R reads, flat in descendant count and excluding non-recursive controls");
                    Console.WriteLine("B222 B9 Measured R=" + branches + " m=" + descendants + " H=" + held +
                        " capable=" + capable + " statements=" + statements + " delta=" + (statements - baseline) +
                        " identity-reads=" + identityReads + " commits=" + writes + " logical-calls=" + calls.Count);
                }
                finally { Directory.Delete(folder, true); }
            }
        }
    }

    private static async Task HeldFolderMembershipAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(folder);
        try
        {
            using var f = new Fixture(folder, "T8");
            var root = await f.DropboxEntries.EnsureFolderAsync("Folder", default);
            var child = await f.DropboxEntries.EnsureFolderAsync("Folder/Child", default);
            var savedRoot = Saved(root.Path, root.Path);
            var saved = Saved(child.Path, child.Path) with { LocalId = "local-child", DropboxId = "dropbox-child" };
            var absent = Saved("Folder/Absent", "Folder/Absent") with { LocalId = "local-absent", DropboxId = "dropbox-absent" };
            f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [savedRoot, saved, absent] });
            var operation = await BeginAsync(f, root, savedRoot);
            f.Store.InsertFolderMember(f.Pair.PairId, operation, f.Store.FolderRemoval(f.Pair.PairId, operation).Generation,
                FolderRemovalItem.ProvenanceKind.Unexpected, child.Path, child, null);
            foreach (var item in new[] { saved, absent })
                f.Store.Commit(f.Pair.PairId, StateChange.Empty with
                { Raise = [(IssueKind.Failure, TextKey.FailLocalInUse, item.LocalPath,
                    new IssueSubject([new IssueItem(Side.Local, item.LocalPath, item.LocalId)], null))] });
            var members = f.Store.FolderRemovalItems(f.Pair.PairId, operation, -1, int.MaxValue).ToArray();
            Check.Require(members.Count(item => item.Path == child.Path) == 2 &&
                members.Single(item => item.Path == absent.LocalPath) is { Expected: null, Saved: not null,
                    Provenance: FolderRemovalItem.ProvenanceKind.Planned },
                "B222 T8: public paths create a same-key Planned/Unexpected pair and a distinct saved-only Planned control");
            var (low, high) = PathNames.DescendantBounds(PathNames.Key(root.Path));
            var page = f.Store.HeldFolderMembers(f.Pair.PairId, operation, IssueKind.Failure, (low, -1), high, int.MaxValue);
            Check.Require(page.Count == 1 && page[0].Issue.Path == child.Path && page[0].Saved == saved,
                "B222 T8: exactly the present Planned binding joins each held issue; Unexpected and saved-only rows never join");
        }
        finally { Directory.Delete(folder, true); }
    }

    private static async Task HeldIdentityEpisodesAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var scenario in new[] { "T1-root", "T1-branches", "T2", "T3a", "T3b", "T4a", "T4b", "T4c",
                "T4d", "T4e", "T4f", "T4g", "T5a", "T5b", "T5c", "T7", "T9", "T10", "T11" })
            {
                var attempt = Guid.NewGuid();
                using var cancellation = new CancellationTokenSource();
                var calls = new List<(Side Side, string Id, bool Documented)>();
                Action? duringRead = null;
                Task<(IdentityLocation Location, bool InScope)?> ReadIdentity(Side side, string id, bool documented,
                    CancellationToken ct)
                {
                    calls.Add((side, id, documented));
                    if (side == Side.Local)
                    {
                        duringRead?.Invoke();
                        if (scenario == "T5c") { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); }
                        if (scenario == "T5a" || scenario == "T9" && documented) throw new IOException();
                    }
                    else if (scenario == "T5b") throw new IOException();
                    (IdentityLocation Location, bool InScope)? found = null;
                    if (side == Side.Local && scenario is "T2" or "T3a" || side == Side.Dropbox && scenario == "T3b")
                        found = (new IdentityLocation("domain", "Elsewhere", new Entry("Elsewhere", true, 0, default,
                            id, "revision", "", null)), scenario == "T2");
                    return Task.FromResult(found);
                }
                using var f = new Fixture(folder, scenario, localEvidence: true,
                    registry: (id, _, _, _, _) => id == attempt,
                    pageSize: scenario == "T5c" ? 2 : 1, byId: scenario == "T10" ? null : ReadIdentity);
                SourceFixture.AcquireJournal(f.Store, f.Pair);
                var source = f.Store.ReadSource(f.Pair.PairId, Side.Local)!.Value;
                var root = await f.DropboxEntries.EnsureFolderAsync("Folder", default);
                var savedRoot = Saved(root.Path, root.Path) with { LocalId = "local-root", DropboxId = "dropbox-root" };
                var partial = scenario is "T1-branches" or "T7";
                var records = new List<SyncedEntry> { savedRoot };
                var targets = new List<(Entry Entry, SyncedEntry Saved)>();
                var branches = scenario == "T1-branches" ? 2 : 1;
                for (var branch = 0; branch < branches; branch++)
                {
                    var parent = "Folder/Branch-" + branch;
                    await f.DropboxEntries.EnsureFolderAsync(parent, default);
                    records.Add(Saved(parent, parent) with { LocalId = "local-branch-" + branch, DropboxId = "dropbox-branch-" + branch });
                    var target = await f.DropboxEntries.EnsureFolderAsync(parent + "/Child", default);
                    var saved = Saved(target.Path, target.Path) with { LocalId = "local-" + branch, DropboxId = "dropbox-" + branch };
                    if (scenario == "T4a") saved = saved with { LocalId = "" };
                    if (scenario == "T4b") saved = saved with { DropboxId = "" };
                    if (scenario != "T4d") records.Add(saved);
                    targets.Add((target, saved));
                }
                if (partial) await f.DropboxEntries.EnsureFolderAsync("Folder/Keep", default);
                f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = records });
                string operation;
                if (scenario == "T4d")
                {
                    using var plan = f.Store.OpenFolderPlan(f.Pair.PairId, Row(root, savedRoot), Side.Dropbox);
                    await plan.CaptureAsync(f.Dropbox.ReadAllAsync(root.Path,
                        new ListOptions(Recursive: true, IncludeOutOfScope: true), default), default);
                    foreach (var child in plan.Children(default))
                        plan.MarkClassified(child.Key, child.NativeKey,
                            new FolderChild(child.Entry, child.Synced, FolderVerdict.Removed), false, default);
                    plan.Complete(true, default);
                    operation = f.Store.BeginFolderRemoval(f.Pair.PairId, plan, 1, default);
                }
                else operation = await BeginAsync(f, root, savedRoot);
                var effects = f.Store.ReadyFolderEffects(f.Pair.PairId, operation, -1, int.MaxValue).ToArray();
                Check.Require(effects.Length == (partial ? branches : 1) &&
                    effects.All(effect => Item(f, operation, effect.ItemOrdinal).EffectKind == FolderEffectKind.RecursiveFolder),
                    "B222 " + scenario + ": only maximal recursive effects enclose the held descendants");
                WorkSelection? acquired = null;
                if (scenario.StartsWith("T1", StringComparison.Ordinal))
                {
                    Check.Require(f.Store.AcquireJournalPage(
                        f.Pair.Roots!.Local.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture),
                        new JournalPosition(1, 1), new JournalPosition(1, 2),
                        [(f.Pair.PairId, 0, source.Epoch, source.Revision, false)],
                        [(f.Pair.PairId, new ChangeNotice(Side.Local, ChangeKind.Changed, targets[0].Entry.Path,
                            null, targets[0].Saved.LocalId, null, false, false))]) is not null,
                        "B222 T1: acquired child work is published");
                    acquired = f.RawStore.SelectWork(f.Pair.PairId, 0, f.Context.Scope);
                    Check.Require(acquired is not null && acquired.Key == PathNames.Key(targets[0].Entry.Path),
                        "B222 T1: selecting acquired work opens the retirement pass");
                }
                var episodes = new List<ItemRetryCandidate>();
                var expected = new List<Issue>();
                foreach (var (target, saved) in targets)
                {
                    var subject = new IssueSubject([new IssueItem(Side.Local,
                        scenario == "T4e" ? "Elsewhere" : target.Path,
                        scenario == "T4c" ? "other-id" : saved.LocalId)], null);
                    if (scenario == "T4f") subject = subject with { Items = [] };
                    if (scenario == "T4g") subject = subject with
                    {
                        Move = new IssueSubject.UnknownMove(saved.DropboxId, "Source", "Destination", "Intermediate", false)
                    };
                    if (scenario is "T4f" or "T4g" or "T11")
                        f.Store.Commit(f.Pair.PairId, StateChange.Empty with
                        { Raise = [(IssueKind.Failure, TextKey.FailLocalInUse, target.Path, subject)] });
                    else episodes.Add(f.Store.RecordInUseFailure(f.Pair.PairId, 0, source.Epoch, target.Path, subject,
                        TextKey.FailLocalInUse, expected.Count == 0 ? acquired : null,
                        ItemRetryDispatch.OrdinaryItem, null, null, attempt));
                    expected.Add(f.Store.Issues(f.Pair.PairId).Single(issue => issue.Path == target.Path));
                    if (scenario.StartsWith("T1", StringComparison.Ordinal) || scenario == "T5c")
                    {
                        var variant = target.Path.ToLowerInvariant();
                        f.Store.Commit(f.Pair.PairId, StateChange.Empty with
                        { Raise = [(IssueKind.Failure, TextKey.FailLocalInUse, variant,
                            new IssueSubject([new IssueItem(Side.Local, variant, saved.LocalId)], null))] });
                        expected.Add(f.Store.Issues(f.Pair.PairId).Single(issue => issue.Path == variant));
                    }
                }
                var excluded = new List<long>();
                if (partial)
                    foreach (var path in scenario == "T7"
                        ? new[] { "Folder/Keep", "Folder/NonMember", "Folder2/x", "Folder" } : new[] { "Folder/Keep" })
                    {
                        f.Store.Commit(f.Pair.PairId, StateChange.Empty with
                        { Raise = [(IssueKind.Failure, TextKey.FailLocalInUse, path,
                            new IssueSubject([new IssueItem(Side.Local, path, "excluded-" + path)], null))] });
                        excluded.Add(f.Store.Issues(f.Pair.PairId).Single(issue => issue.Path == path).Id);
                    }
                if (scenario == "T7")
                {
                    var branch = records.Single(saved => saved.LocalPath == "Folder/Branch-0");
                    var branchWait = f.Store.RecordInUseFailure(f.Pair.PairId, 0, source.Epoch, branch.LocalPath,
                        new IssueSubject([new IssueItem(Side.Local, branch.LocalPath, branch.LocalId)], null),
                        TextKey.FailLocalInUse, null, ItemRetryDispatch.OrdinaryItem, null, null, attempt);
                    excluded.Add(branchWait.Token.IssueId);
                }
                if (scenario == "T11") duringRead = () =>
                {
                    f.Store.Commit(f.Pair.PairId, StateChange.Empty with
                    {
                        Raise = [(IssueKind.Failure, TextKey.FailLocalInUse, targets[0].Entry.Path,
                            new IssueSubject([new IssueItem(Side.Local, targets[0].Entry.Path, "changed-id")], null))]
                    });
                    Check.Require(f.Store.Issues(f.Pair.PairId).Single(issue => issue.Id == expected[0].Id)
                        .Subject.Items.Single().Id == "changed-id", "B222 T11: the stored issue changes semantically during proof");
                };
                f.Dropbox.Returns(new FolderSubmitResult.Terminal(effects.Select((effect, index) =>
                    (FolderMemberResult)new FolderMemberResult.Receipt(index, effect.Expected!)).ToArray()));
                await f.Runner.RunPreparedAsync([effects], default);
                foreach (var effect in effects) await f.DropboxEntries.DeleteAsync(effect.Expected!, null, default);
                var cancelled = false;
                Exception? escaped = null;
                try { await f.Runner.ResumeAsync(f.Store.FolderRemoval(f.Pair.PairId, operation), null, cancellation.Token); }
                catch (OperationCanceledException) when (scenario == "T5c") { cancelled = true; }
                catch (Exception error) when (scenario is "T5a" or "T5b") { escaped = error; }
                Check.Require(escaped is null, "B222 " + scenario + ": identity inspection failure never escapes the continuation; " + escaped);
                Check.Require(cancelled == (scenario == "T5c"), "B222 T5c: entry-point cancellation propagates");
                if (scenario is "T5a" or "T5b") Check.Require(!f.Store.FolderRemoval(f.Pair.PairId, operation).Active,
                    "B222 " + scenario + ": failed identity inspection retains the issue without preventing operation conclusion");
                var closes = scenario.StartsWith("T1", StringComparison.Ordinal) || scenario == "T7";
                foreach (var issue in expected)
                    Check.Require(f.Store.Issues(f.Pair.PairId).Any(row => row.Id == issue.Id) != closes,
                        "B222 " + scenario + ": only trustworthy frozen-identity absence closes the exact held issue");
                foreach (var episode in episodes)
                    Check.Require((f.Store.ReadItemRetryState(f.Pair.PairId, [episode.Token.IssueId])[episode.Token.IssueId] is null) == closes,
                        "B222 " + scenario + ": the notice and its episode share the identity-proof outcome");
                Check.Require(excluded.All(id => f.Store.Issues(f.Pair.PairId).Any(issue => issue.Id == id)),
                    "B222 T7: root, prefix sibling, non-member and survivor issues stay untouched");
                var localCalls = calls.Count(call => call.Side == Side.Local);
                var dropboxCalls = calls.Count - localCalls;
                var noCalls = scenario is "T4a" or "T4b" or "T4c" or "T4d" or "T4f" or "T4g" or "T10";
                var shortCircuit = scenario is "T2" or "T3a" or "T5a" or "T5c" or "T9";
                Check.Require(localCalls == (noCalls ? 0 : scenario == "T5c" ? 1 : expected.Count) &&
                    dropboxCalls == (noCalls || shortCircuit ? 0 : expected.Count),
                    "B222 " + scenario + ": identity guards and Local-first short circuit bound logical calls");
                if (scenario == "T7")
                    Check.Require(calls.Select(call => call.Id).OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(
                        targets.SelectMany(target => new[] { target.Saved.LocalId, target.Saved.DropboxId })
                            .OrderBy(id => id, StringComparer.Ordinal)),
                        "B222 T7: delegates receive exactly the eligible frozen IDs, never excluded IDs");
                if (scenario.StartsWith("T1", StringComparison.Ordinal))
                {
                    Check.Require(f.RawStore.EndWorkPass(f.Pair.PairId), "B222 T1: identity retirement requests the repeat pass");
                    var drain = f.RawStore.SelectWork(f.Pair.PairId, 0, f.Context.Scope);
                    Check.Require(drain is not null && drain.AnchorWorkId == acquired!.AnchorWorkId &&
                        f.RawStore.CompleteWork(drain, null), "B222 T1: the ordinary selector drains acquired held-child work");
                    Check.Require(f.Store.CountWork(f.Pair.PairId, 0, f.Store.ReadWorkWatermark()) == (0, 0),
                        "B222 T1: no acquired work remains after the repeat pass");
                }
            }
        }
        finally { Directory.Delete(folder, true); }
    }

    public static void RunRepairHeld()
    {
        var folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(folder);
        try { ReceiptSurvivorRefusedAsync(folder).GetAwaiter().GetResult(); }
        finally { Directory.Delete(folder, true); }
    }

    // R6-2/B-140B: the survivor a receipt-only opportunity reaches after a terminal receipt is an unsaved child of a
    // saved folder missing on Local. Its own create is new, but its parent is a restoration: under Local lost-interval
    // debt Parents refuses the saved ancestor before any mkdir, the follow-up stays pending and the receipt settles.
    private static async Task ReceiptSurvivorRefusedAsync(string folder)
    {
        using var f = new Fixture(folder, nameof(ReceiptSurvivorRefusedAsync));
        var root = await f.DropboxEntries.EnsureFolderAsync("Folder", default);
        var keep = await f.DropboxEntries.EnsureFolderAsync("Folder/Keep", default);
        var gone = await f.DropboxEntries.EnsureFolderAsync("Folder/Gone", default);
        var saved = Saved(root.Path, root.Path);
        f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [saved, Saved(gone.Path, gone.Path)] });
        var operation = await BeginAsync(f, root, saved);
        var effects = f.Store.ReadyFolderEffects(f.Pair.PairId, operation, -1, PageSize).ToArray();
        var id = Accepted(f, effects);
        var followUps = f.Store.PendingFolderFollowUps(f.Pair.PairId, operation, -1, PageSize).Count();
        Check.Require(effects.Length == 1 && followUps == 1,
            "R6-2 fixture: one removed branch and one survivor follow-up: effects=" + effects.Length +
            "; followUps=" + followUps);
        await f.DropboxEntries.DeleteAsync(gone, null, default);
        f.Dropbox.Returns(new FolderJobResult.Terminal([new FolderMemberResult.Receipt(0, gone)]));
        var connection = (SqliteConnection)typeof(StateStore).GetField("_connection",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(f.RawStore)!;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE pair SET first_repair_done=1;";
            command.ExecuteNonQuery();
        }
        var local = f.Store.ReadSource(f.Pair.PairId, Side.Local)!.Value;
        f.Store.AdvanceSource(f.Pair.PairId, Side.Local, f.Context.ExpectedGeneration, local.Epoch, local.Revision,
            local.Provenance, local.Cursor, local.Epoch, local.Revision, local.Provenance, local.Cursor,
            SourceObligation.NormalRepair);
        Check.Require(f.Store.ReadLiveRefusal(f.Pair.PairId) == LiveRefusal.LocalWrites,
            "R6-2 fixture: Local lost-interval debt after the first Repair refuses live Local writes");
        // The survivor is ordinary item work, so it runs inside an ordinary attempt up to the parent decision.
        var syncer = new Syncer(f.Context with { ItemAttempts = new PassingItemAttempts() }, new SyncPorts(new PassthroughBoundary(),
            RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
        Exception? error = null;
        try { await syncer.ReconcileReceiptsAsync(default, progressOnly: true); }
        catch (Exception caught) { error = caught; }
        var pending = f.Store.PendingFolderFollowUps(f.Pair.PairId, operation, -1, PageSize).Count();
        var restored = await f.LocalEntries.ReadOneAsync(keep.Path, default) is not null ||
            await f.LocalEntries.ReadOneAsync(root.Path, default) is not null;
        var refusals = f.Events.Where(value => value.Kind == RunEventKind.Failed &&
            value.Error is LiveAdmissionStaleException stale && ItemSync.LiveWriteRefused(stale)).ToArray();
        // R4: a survivor with no durable work row is reported once with the held-write cause and never invents a work id.
        Check.Require(error is null && f.Store.FolderSubmission(id).Phase == FolderSubmission.SubmissionPhase.Terminal &&
            pending == 1 && !restored && refusals.Length == 1 && refusals[0].Cause == TextKey.LocalWriteHeld &&
            refusals[0].WorkId is null &&
            string.Equals(refusals[0].Path, keep.Path, StringComparison.OrdinalIgnoreCase),
            "R6-2 a receipt-terminal survivor under Local debt is refused once with the held-write cause and stays " +
            "pending: error=" + error?.GetType().Name + " " + error?.Message + "; pending=" + pending +
            "; restored=" + restored + "; refusals=" + refusals.Length);
    }

    public static async Task SettledReadyNeverSendsAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(folder);
        try
        {
            using var f = new Fixture(folder, nameof(SettledReadyNeverSendsAsync));
            var root = await f.DropboxEntries.EnsureFolderAsync("Folder", CancellationToken.None);
            var saved = Saved(root.Path, root.Path);
            f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [saved] });
            var id = await BeginAsync(f, root, saved);
            var effect = f.Store.ReadyFolderEffects(f.Pair.PairId, id, -1, PageSize).Single();
            var operation = f.Store.FolderRemoval(f.Pair.PairId, id);
            f.Store.AdvanceFolderRemoval(f.Pair.PairId, id, operation.Generation, operation.Phase,
                operation with { Generation = operation.Generation + 1, Phase = FolderRemovalPhase.Retained, Cursor = "cursor" });
            operation = f.Store.FolderRemoval(f.Pair.PairId, id);
            var member = Item(f, id, effect.ItemOrdinal);
            var syncer = new Syncer(f.Context, new SyncPorts(new PassthroughBoundary(),
                RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
            await syncer.RecoverAsync(new RecoveryTarget(id, operation.Generation, member.Ordinal, member.Generation),
                RecoveryChoice.AcceptCurrent, CancellationToken.None);
            Check.Require(Item(f, id, member.Ordinal).IsTerminal, "AcceptCurrent settles the selected Ready member.");
            f.Dropbox.Returns(new FolderSubmitResult.Terminal([new FolderMemberResult.Receipt(0, root)]));
            await f.Runner.ResumeAsync(f.Store.FolderRemoval(f.Pair.PairId, id), null, CancellationToken.None);
            Check.Require(f.Dropbox.SubmitCalls == 0 && Item(f, id, member.Ordinal).IsTerminal &&
                await f.DropboxEntries.ReadOneAsync(root.Path, CancellationToken.None) == root &&
                !Gone(f.Store, f.Pair.PairId, saved), "A terminal Ready member never sends or forgets its live folder.");
            Check.Require(!f.Store.ReadyFolderEffects(f.Pair.PairId, id, -1, PageSize).Any() &&
                !f.Store.ReadyFolderEffects(f.Pair.PairId, id, member.Ordinal - 1, PageSize).Any(),
                "Both initial and continuation ready pages exclude terminal members.");
            Check.Require(Throws<InvalidOperationException>(() => f.Store.ArmFolderSubmission(f.Pair.PairId, [effect])),
                "An already captured Ready effect cannot arm a terminal member.");
        }
        finally { Directory.Delete(folder, true); }
    }

    public static async Task SettledUnknownRejectsLateReceiptAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(folder);
        try
        {
            using var f = new Fixture(folder, nameof(SettledUnknownRejectsLateReceiptAsync));
            var root = await f.DropboxEntries.EnsureFolderAsync("Folder", default);
            await f.DropboxEntries.EnsureFolderAsync("Folder/Keep", default);
            var first = await f.DropboxEntries.EnsureFolderAsync("Folder/First", default);
            var second = await f.DropboxEntries.EnsureFolderAsync("Folder/Second", default);
            var saved = Saved(root.Path, root.Path);
            f.Store.Commit(f.Pair.PairId, StateChange.Empty with
                { Record = [saved, Saved(first.Path, first.Path), Saved(second.Path, second.Path)] });
            var id = await BeginAsync(f, root, saved);
            var effects = f.Store.ReadyFolderEffects(f.Pair.PairId, id, -1, PageSize).ToArray();
            Check.Require(effects.Length == 2, "The late receipt covers two independently owned members.");
            f.Dropbox.Returns(new FolderSubmitResult.Unknown(TextKey.DropboxRefused));
            await f.Runner.RunGroupAsync(effects, default);
            var operation = f.Store.FolderRemoval(f.Pair.PairId, id);
            var member = Item(f, id, effects[0].ItemOrdinal);
            var syncer = new Syncer(f.Context, new SyncPorts(new PassthroughBoundary(),
                RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
            await syncer.RecoverAsync(new RecoveryTarget(id, operation.Generation, member.Ordinal, member.Generation),
                RecoveryChoice.AcceptCurrent, default);
            var submission = f.Store.FolderSubmission(member.SubmissionId!);
            var terminal = Item(f, id, member.Ordinal);
            f.Store.AdvanceFolderSubmission(submission,
                submission with { Generation = submission.Generation + 1, Phase = FolderSubmission.SubmissionPhase.Terminal },
                [new FolderMemberResult.Receipt(0, effects[0].Expected!), new FolderMemberResult.Receipt(1, effects[1].Expected!)]);
            Check.Require(Item(f, id, member.Ordinal) == terminal && !Gone(f.Store, f.Pair.PairId, saved),
                "Late receipts leave acknowledged members exactly unchanged, including mappings.");
            Check.Require(Item(f, id, effects[1].ItemOrdinal).EffectState == FolderRemovalItem.EffectStatus.Verified,
                "An acknowledged sibling does not block a live member's verified receipt.");
        }
        finally { Directory.Delete(folder, true); }
    }

    public static async Task SettledStateOnlyNeverForgetsAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(folder);
        try
        {
            using var f = new Fixture(folder, nameof(SettledStateOnlyNeverForgetsAsync));
            var root = await f.DropboxEntries.EnsureFolderAsync("Folder", default);
            await f.DropboxEntries.EnsureFolderAsync("Folder/Keep", default);
            var saved = Saved("Folder/Absent", "Folder/Absent");
            var savedRoot = Saved(root.Path, root.Path);
            f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [savedRoot, saved] });
            var id = await BeginAsync(f, root, savedRoot);
            var member = f.Store.FolderRemovalItems(f.Pair.PairId, id, -1, int.MaxValue)
                .Single(item => item.EffectKind == FolderEffectKind.StateOnly);
            var operation = f.Store.FolderRemoval(f.Pair.PairId, id);
            var syncer = new Syncer(f.Context, new SyncPorts(new PassthroughBoundary(),
                RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
            await syncer.RecoverAsync(new RecoveryTarget(id, operation.Generation, member.Ordinal, member.Generation),
                RecoveryChoice.AcceptCurrent, default);
            var terminal = Item(f, id, member.Ordinal);
            await f.Runner.ResumeAsync(f.Store.FolderRemoval(f.Pair.PairId, id), null, default);
            Check.Require(Item(f, id, member.Ordinal) == terminal && !Gone(f.Store, f.Pair.PairId, saved) &&
                f.Store.Issues(f.Pair.PairId).All(issue => issue.Cause != TextKey.FailChanged),
                "Acknowledged saved-only work neither forgets mappings nor retries terminal settlement as a failure.");
        }
        finally { Directory.Delete(folder, true); }
    }

    public static async Task RealRecoveryAdmissionAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var grow in new[] { false, true })
            foreach (var advance in new[] { false, true })
            {
                using var f = new Fixture(folder, nameof(RealRecoveryAdmissionAsync));
                var root = await f.DropboxEntries.EnsureFolderAsync("Folder", default);
                var id = await BeginAsync(f, root, Saved(root.Path, root.Path));
                var operation = f.Store.FolderRemoval(f.Pair.PairId, id);
                var member = Item(f, id, 0);
                if (grow)
                    for (var index = 0; index < PageSize + 1; index++)
                        f.Store.InsertFolderMember(f.Pair.PairId, id, operation.Generation,
                            FolderRemovalItem.ProvenanceKind.Unexpected, "Folder/Extra" + index, null, null);
                var connectionId = DropboxConnectionBinding.CreateConnectionId("account-id");
                var localRoot = Directory.CreateDirectory(Path.Combine(folder, Guid.NewGuid().ToString("N"))).FullName;
                var pair = f.Pair with { LocalRoot = localRoot, DropboxRoot = "/Pilot", ConnectionId = connectionId,
                    Roots = new RootBinding(localRoot, "/Pilot", connectionId, WindowsDirectoryListing.ReadIdentity(localRoot),
                        new DropboxRootIdentity("account-id", "namespace-a", "namespace-a", "id:pilot-folder")) };
                f.Store.SavePair(pair);
                var proxy = System.Reflection.DispatchProxy.Create<IStateStore, MemberReadCounter>();
                var counts = (MemberReadCounter)(object)proxy;
                counts.Inner = f.Store;
                using var instance = Massdex.SyncShark.PilotInstanceLock.AcquireForMaintenance(
                    Path.Combine(folder, Guid.NewGuid().ToString("N"), "state", "lock"));
                await new DropboxConnectionStore(instance.StateAuthority).SaveNewAsync(new DropboxConnectionBinding(
                    DropboxProviderConfiguration.RegisteredClientId, "account-id", "namespace-a", "namespace-a",
                    DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes,
                    ConnectionId: pair.ConnectionId, CredentialReference: WindowsDropboxRefreshTokenStore.RegisteredTarget));
                using var wire = new Massdex.SyncShark.V2.DropboxWire
                {
                    AccountBinding = new DropboxConnectionStore(instance.StateAuthority).Load(pair.ConnectionId)
                };
                var advanced = false;
                wire.BeforeMetadataResponse = _ =>
                {
                    if (!advance || advanced) return;
                    advanced = true;
                    f.Store.AdvanceFolderRemoval(pair.PairId, id, operation.Generation, operation.Phase,
                        operation with { Generation = operation.Generation + 1, Cause = TextKey.FailChanged });
                };
                using var http = new HttpClient(wire, disposeHandler: false);
                var settings = Massdex.SyncShark.Engine.Run.EngineHost.LoadSettings(instance.StateAuthority.ProductRoot);
                await using var host = Massdex.SyncShark.Engine.Run.EngineHost.Compose(instance.StateAuthority.ProductRoot,
                    _ => { }, instance.StateAuthority, settings, http, TimeProvider.System, _ => null,
                    _ => proxy, Massdex.SyncShark.Engine.Run.EngineHost.Mode.Direct,
                    new Massdex.SyncShark.Engine.Run.EngineExternals(new SyntheticCredentials(),
                        _ => new HttpClient(wire, disposeHandler: false), f.Context.Recovery));
                await host.RecomposeCurrentAsync(pair.PairId, default);
                await host.DispatchAsync(pair.PairId,
                    async (registration, admitted, generation, token) =>
                    {
                        var session = Massdex.SyncShark.Engine.Run.PairSession.Open(proxy, admitted, settings,
                            http, false, null, instance.StateAuthority, host, registration, generation, TimeProvider.System);
                        var target = new RecoveryTarget(id, operation.Generation, member.Ordinal, member.Generation);
                        try
                        {
                            await session.Syncer.RecoverAsync(target with { ItemGeneration = member.Generation + 1 },
                                RecoveryChoice.AcceptCurrent, token);
                            throw new InvalidOperationException("A stale member must refuse through the production entry.");
                        }
                        catch (LiveAdmissionStaleException error) when (error.Reason == LiveAdmissionStaleReason.SelectionChanged) { }
                        Check.Require(Item(f, id, member.Ordinal) == member, "Stale member selection has no effect.");
                        counts.Reads = 0;
                        try
                        {
                            await session.Syncer.RecoverAsync(target, RecoveryChoice.AcceptCurrent, token);
                            Check.Require(!advance, "A real admitted operation advance must refuse.");
                        }
                        catch (LiveAdmissionStaleException error) when (advance &&
                            error.Reason == LiveAdmissionStaleReason.SelectionChanged) { }
                        Check.Require(advance ? Item(f, id, member.Ordinal) == member :
                            Item(f, id, member.Ordinal).IsTerminal, "Real admission preserves stale work and settles current work.");
                        if (!advance) Check.Require(counts.Reads == 4,
                            "The production boundary plus Recovery reads exactly four selected members at both sizes.");
                        if (!advance && !grow)
                        {
                            var protectedRoot = await f.DropboxEntries.EnsureFolderAsync("Protected", token);
                            var savedRoot = Saved(protectedRoot.Path, protectedRoot.Path);
                            f.Store.Commit(pair.PairId, StateChange.Empty with { Record = [savedRoot] });
                            var protectedId = await BeginAsync(f, protectedRoot, savedRoot);
                            UnreadableMember.Record(f.Store, pair.PairId, protectedRoot.Path);
                            var context = f.Context with { Pair = pair, ExpectedGeneration = generation };
                            var transfers = new Transfers(context);
                            var runner = new FolderDeleteRunner(context,
                                new ActionExecutor(context, transfers, new Parents(context), new KeepBothRunner(context, transfers)),
                                new Massdex.SyncShark.Engine.Run.RecoveryExecutionBoundary(() => session));
                            f.Dropbox.Returns(new FolderSubmitResult.Unknown(TextKey.DropboxRefused));
                            await runner.ResumeAsync(f.Store.FolderRemoval(pair.PairId, protectedId), null, token);
                            Check.Require(f.Dropbox.SubmitCalls == 0 && !Gone(f.Store, pair.PairId, savedRoot) &&
                                f.Store.Issues(pair.PairId).Any(issue => issue is { Kind: IssueKind.Failure, Path: "Protected" }),
                                "Production recovery admission preserves a protection recorded after freezing.");
                        }
                        return true;
                    }, default);
            }
        }
        finally { Directory.Delete(folder, true); }
    }

    /// <summary>Counts the production member reads while forwarding every authority check unchanged.</summary>
    public class MemberReadCounter : System.Reflection.DispatchProxy
    {
        public IStateStore Inner { get; set; } = null!;
        public int Reads { get; set; }
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IStateStore.FolderRemovalItems)) Reads++;
            if (method.Name == nameof(IDisposable.Dispose)) return null;
            try { return method.Invoke(Inner, args); }
            catch (System.Reflection.TargetInvocationException error) when (error.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
    }

    public static async Task ReplacementPlanFailureAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(folder);
        try
        {
            using var f = new Fixture(folder, nameof(ReplacementPlanFailureAsync));
            var remote = await f.DropboxEntries.EnsureFolderAsync("Folder", default);
            var local = new Entry(remote.Path, false, 1, DateTimeOffset.UnixEpoch, "local-file", "", "", null);
            f.LocalEntries.Seed(local, [1]);
            var saved = Saved(local.Path, remote.Path) with { DropboxId = remote.Id };
            f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [saved] });
            var row = new Row(PathNames.Key(remote.Path), local, remote, saved, [], null, RowContext.Neutral);
            var failure = new ItemFailedException(remote.Path, TextKey.ReadDropboxFailed, null);
            var planReached = false;
            f.Dropbox.OnReadAll = options =>
            {
                if (!options.Recursive || !options.IncludeOutOfScope) return;
                planReached = true;
                throw failure;
            };
            var syncer = SyncJobs.Create(f.Context, new SyncPorts(new PassthroughBoundary(),
                RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0)).Items;
            try
            {
                await syncer.Dispatch.DispatchPreparedAsync(new SyncAction(ActionKind.ReplaceKind, row,
                    Side: Side.Dropbox, Target: row.Dropbox!.Path), default);
                throw new InvalidOperationException("A replacement plan's item error must reach its caller unchanged.");
            }
            catch (ItemFailedException error)
            {
                Check.Require(planReached && ReferenceEquals(error, failure) && f.Store.Issues(f.Pair.PairId).Count == 0,
                    "Replacement preparation does not commit a competing Failure before the caller maps its error.");
                var transfers = new Transfers(f.Context);
                var actions = new ActionExecutor(f.Context, transfers, new Parents(f.Context),
                    new KeepBothRunner(f.Context, transfers));
                await actions.Failures.FailAsync(row, null, error, default);
            }
            var issues = f.Store.Issues(f.Pair.PairId);
            Check.Require(issues.Count == 1 && issues[0].Cause == TextKey.ReadDropboxFailed &&
                f.Events.Count(value => value.Kind == RunEventKind.Issue) == 1,
                "The caller's shared mapping records exactly one Failure with the original specific cause.");
        }
        finally { Directory.Delete(folder, true); }
    }

    private static async Task RunAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var afterDiscard in new[] { false, true })
            foreach (var cancellation in new[] { false, true })
                await TerminalDiscardInterruptionAsync(folder, afterDiscard, cancellation);
            await LocalProofRefusalAsync(folder);
            await ProtectionAfterFreezeAsync(folder);
            await DeleteWholeAsync(folder);
            await BatchBoundSplitsAsync(folder);
            await PagePipelineBatchesAsync(folder);
            await UnfinishedRootReadsDoNotScaleAsync(folder);
            await WrappedRecoveryCauseAsync(folder);
            await SettledMemberIsTerminalAsync(folder);
            await TerminalDiscardBesideUnknownAsync(folder);
            await PartialBranchesAsync(folder);
            await MismatchAsync(folder);
            await DescendantChangedBeforeCursorAsync(folder, edit: true, delayedResume: false);
            await DescendantChangedBeforeCursorAsync(folder, edit: false, delayedResume: false);
            await DescendantChangedBeforeCursorAsync(folder, edit: true, delayedResume: true);
            await MemberAttributionAsync(folder);
            await ReceiptOnlyAsync(folder);
            await SharedReceiptPassAsync(folder);
            await CancelReceiptPassAsync(folder);
            await CancelSecondReceiptAsync(folder);
            await ReceiptStorageFailureAsync(folder);
            await StorageRefusalAsync(folder);
            await FreezeCancellationDisposesScratchAsync(folder);
            await ClaimedReceiptPassAsync(folder);
            await JoinRereadsClaimAsync(folder, terminal: true);
            await JoinRereadsClaimAsync(folder, terminal: false);
            await JoinOwnsReceiptAsync(folder);
            await ReceiptCountsDoNotScaleAsync(folder);
            await ProgressReceiptCasesAsync(folder);
            await TerminalContinuationClaimsAsync(folder);
            await ReceiptRecoveryOwnerAsync(folder);
            await SelectionNeverRepeatsReceiptsAsync(folder);
            await InvalidJobSecondPassAsync(folder);
            await LostReceiptAsync(folder);
            await PairRemovalCustodyAsync(folder);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    /// <summary>A-T1/A-T2: a clean whole-folder removal sends exactly the frozen root effect as ordinal zero, settles
    /// its verified receipt and completes with the whole subtree forgotten.</summary>
    private static async Task DeleteWholeAsync(string folder)
    {
        using var f = new Fixture(folder, nameof(DeleteWholeAsync));
        var root = await f.DropboxEntries.EnsureFolderAsync("Folder", CancellationToken.None);
        var child = await f.DropboxEntries.EnsureFolderAsync(
            PathNames.Child(root.Path, nameof(FolderChild)), CancellationToken.None);
        var leaf = await f.DropboxEntries.EnsureFolderAsync(
            PathNames.Child(child.Path, nameof(Entry)), CancellationToken.None);
        var savedRoot = Saved(root.Path, root.Path);
        var savedChild = Saved(child.Path, child.Path);
        var savedLeaf = Saved(leaf.Path, leaf.Path);
        var unrelated = Saved(nameof(StateChange), nameof(StateChange));
        f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [savedRoot, savedChild, savedLeaf, unrelated] });

        var operationId = await BeginAsync(f, root, savedRoot);
        var effects = f.Store.ReadyFolderEffects(f.Pair.PairId, operationId, -1, PageSize).ToArray();
        Check.Require(effects.Length == 1 && effects[0].ItemOrdinal == 0 &&
            effects[0].Kind == FolderEffectKind.RecursiveFolder && effects[0].Expected?.Path == root.Path &&
            effects[0].Expected?.Id == root.Id, nameof(FolderRemovalEffect));
        var items = f.Store.FolderRemovalItems(f.Pair.PairId, operationId, -1, int.MaxValue).ToArray();
        Check.Require(items.Length == 3 && items[0].Ordinal == 0 && items.Skip(1).All(item => item.Ordinal > 0),
            nameof(FolderRemovalItem.Ordinal));

        f.Dropbox.Returns(new FolderSubmitResult.Terminal([new FolderMemberResult.Receipt(0, root)]));
        var results = await f.Runner.RunGroupAsync(effects, CancellationToken.None);
        Check.Require(results.Count == 1 && results[0].OperationId == operationId &&
            results[0].RootPath == root.Path, nameof(FolderRemovalResult));
        Check.Require(f.Dropbox.SubmitCalls == 1 && f.Dropbox.Submissions.Count == 1 &&
            f.Dropbox.Submitted.Count == 1 && f.Dropbox.Submitted[0].Count == 1 &&
            f.Dropbox.Submitted[0][0].Expected?.Path == root.Path, nameof(ISide.SubmitFolderRemovalAsync));
        Check.Require(f.Store.FolderSubmission(f.Dropbox.Submissions[0].SubmissionId).Phase ==
            FolderSubmission.SubmissionPhase.Terminal, nameof(FolderSubmission.SubmissionPhase));
        Check.Require(Item(f, operationId, 0).EffectState == FolderRemovalItem.EffectStatus.Verified,
            nameof(FolderRemovalItem.EffectStatus));

        await f.Runner.ResumeAsync(f.Store.FolderRemoval(f.Pair.PairId, operationId), null, CancellationToken.None);
        var operation = f.Store.FolderRemoval(f.Pair.PairId, operationId);
        Check.Require(operation.Phase == FolderRemovalPhase.Complete && !operation.Active,
            nameof(FolderRemovalPhase.Complete));
        foreach (var gone in new[] { savedRoot, savedChild, savedLeaf })
            Check.Require(Gone(f.Store, f.Pair.PairId, gone), nameof(StateChange.ForgetSubtrees));
        Check.Require(!Gone(f.Store, f.Pair.PairId, unrelated), nameof(StateChange.ForgetSubtrees));
        Check.Require(f.Dropbox.SubmitCalls == 1, nameof(ISide.SubmitFolderRemovalAsync));
    }

    /// <summary>A-T1/A-T2: a protected survivor keeps the root open; selected branches are paged deepest-first,
    /// only their own verified receipts settle, and no whole-root forget is permitted.</summary>
    private static async Task PartialBranchesAsync(string folder)
    {
        using var f = new Fixture(folder, nameof(PartialBranchesAsync));
        var root = await f.DropboxEntries.EnsureFolderAsync("Folder", CancellationToken.None);
        var keep = await f.DropboxEntries.EnsureFolderAsync(
            PathNames.Child(root.Path, nameof(FolderVerdict.Survives)), CancellationToken.None);
        var first = await f.DropboxEntries.EnsureFolderAsync(
            PathNames.Child(root.Path, nameof(Entry)), CancellationToken.None);
        var next = await f.DropboxEntries.EnsureFolderAsync(
            PathNames.Child(root.Path, nameof(FolderChild)), CancellationToken.None);
        var savedRoot = Saved(root.Path, root.Path);
        var savedFirst = Saved(first.Path, first.Path);
        var savedNext = Saved(next.Path, next.Path);
        f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [savedRoot, savedFirst, savedNext] });

        var operationId = await BeginAsync(f, root, savedRoot);
        var effects = new List<FolderRemovalEffect>();
        var after = -1L;
        while (true)
        {
            var page = f.Store.ReadyFolderEffects(f.Pair.PairId, operationId, after, 1).ToArray();
            if (page.Length == 0) break;
            effects.AddRange(page);
            after = page[^1].ItemOrdinal;
        }
        Check.Require(effects.Select(effect => effect.Expected!.Path).SequenceEqual(new[] { first.Path, next.Path }),
            nameof(IStateStore.ReadyFolderEffects));

        f.Dropbox.Returns(new FolderSubmitResult.Terminal(
        [
            new FolderMemberResult.Receipt(0, first),
            new FolderMemberResult.KnownNoEffect(1, TextKey.FailChanged)
        ]));
        var results = await f.Runner.RunGroupAsync(effects, CancellationToken.None);
        Check.Require(results.Single().Status == FolderRemovalStatus.Partial, nameof(FolderRemovalStatus.Partial));
        Check.Require(f.Dropbox.Submitted.Single().Count == 2, nameof(ISide.SubmitFolderRemovalAsync));

        await f.Runner.ResumeAsync(f.Store.FolderRemoval(f.Pair.PairId, operationId), null, CancellationToken.None);
        var operation = f.Store.FolderRemoval(f.Pair.PairId, operationId);
        Check.Require(!operation.Active && operation.Phase != FolderRemovalPhase.Complete,
            "known-no-effect refusal releases the frozen claim without completing its root");
        Check.Require(Gone(f.Store, f.Pair.PairId, savedFirst), nameof(StateChange.ForgetSubtrees));
        Check.Require(!Gone(f.Store, f.Pair.PairId, savedNext), nameof(FolderRemovalItem.EffectStatus.NoEffect));
        Check.Require(!Gone(f.Store, f.Pair.PairId, savedRoot), nameof(StateChange.ForgetSubtrees));
        Check.Require(await f.DropboxEntries.ReadOneAsync(keep.Path, CancellationToken.None) == keep,
            nameof(FolderVerdict.Survives));
        Check.Require(Item(f, operationId, 0).Verdict == FolderVerdict.Survives,
            nameof(FolderVerdict.Survives));
    }

    /// <summary>A-T1/A-T4: a branch whose observed identity changed after freezing is not sent; only the still-matching
    /// branch is affected and refused work stays visible instead of being deleted.</summary>
    private static async Task MismatchAsync(string folder)
    {
        using var f = new Fixture(folder, nameof(MismatchAsync));
        var root = await f.DropboxEntries.EnsureFolderAsync("Folder", CancellationToken.None);
        var keep = await f.DropboxEntries.EnsureFolderAsync(
            PathNames.Child(root.Path, nameof(FolderVerdict.Survives)), CancellationToken.None);
        var changed = await f.DropboxEntries.EnsureFolderAsync(
            PathNames.Child(root.Path, nameof(FolderChild)), CancellationToken.None);
        var matching = await f.DropboxEntries.EnsureFolderAsync(
            PathNames.Child(root.Path, nameof(Entry)), CancellationToken.None);
        var savedRoot = Saved(root.Path, root.Path);
        var savedChanged = Saved(changed.Path, changed.Path);
        var savedMatching = Saved(matching.Path, matching.Path);
        f.Store.Commit(f.Pair.PairId, StateChange.Empty with
        {
            Record = [savedRoot, savedChanged, savedMatching]
        });
        var operationId = await BeginAsync(f, root, savedRoot);
        var effects = f.Store.ReadyFolderEffects(f.Pair.PairId, operationId, -1, PageSize).ToArray();

        f.DropboxEntries.Seed(changed with { Id = nameof(MismatchAsync) }, null);
        f.Dropbox.Returns(new FolderSubmitResult.Terminal([new FolderMemberResult.Receipt(0, matching)]));
        await f.Runner.RunGroupAsync(effects, CancellationToken.None);
        Check.Require(f.Dropbox.SubmitCalls == 1 && f.Dropbox.Submitted.Count == 1 &&
            f.Dropbox.Submitted[0].Single().Expected?.Path == matching.Path &&
            f.Dropbox.Submitted[0].Single().ItemOrdinal ==
                effects.Single(effect => effect.Expected!.Path == matching.Path).ItemOrdinal,
            nameof(ISide.SubmitFolderRemovalAsync));

        await f.Runner.ResumeAsync(f.Store.FolderRemoval(f.Pair.PairId, operationId), null, CancellationToken.None);
        var operation = f.Store.FolderRemoval(f.Pair.PairId, operationId);
        Check.Require(!operation.Active && operation.Phase != FolderRemovalPhase.Complete,
            "a refused branch releases the obsolete claim without claiming whole-folder completion");
        Check.Require(!Gone(f.Store, f.Pair.PairId, savedChanged) &&
            await f.DropboxEntries.ReadOneAsync(changed.Path, CancellationToken.None) is not null,
            nameof(FolderEffectKind.RecursiveFolder));
        Check.Require(Gone(f.Store, f.Pair.PairId, savedMatching), nameof(StateChange.ForgetSubtrees));
        Check.Require(Item(f, operationId, effects.Single(effect => effect.Expected!.Path == changed.Path)
            .ItemOrdinal).EffectState == FolderRemovalItem.EffectStatus.Ready,
            nameof(FolderRemovalItem.EffectStatus.Ready));
        Check.Require(await f.DropboxEntries.ReadOneAsync(keep.Path, CancellationToken.None) == keep,
            nameof(FolderVerdict.Survives));
    }

    /// <summary>A-T1: an edit or addition under a frozen recursive root before the cursor exists is outside the feed,
    /// so the complete comparison taken after the cursor refuses the delete and never refreshes the victims.</summary>
    private static async Task DescendantChangedBeforeCursorAsync(string folder, bool edit, bool delayedResume)
    {
        using var f = new Fixture(folder, nameof(DescendantChangedBeforeCursorAsync));
        var root = await f.DropboxEntries.EnsureFolderAsync("Folder", CancellationToken.None);
        var file = new Entry(PathNames.Child(root.Path, nameof(Entry)), false, 1, default, nameof(Entry),
            nameof(Side.Dropbox), nameof(Entry), null);
        f.DropboxEntries.Seed(file, [1]);
        var savedRoot = Saved(root.Path, root.Path);
        var savedFile = Saved(file.Path, file.Path) with { IsFolder = false, DropboxContentHash = file.ContentHash };
        f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [savedRoot, savedFile] });
        var operationId = await BeginAsync(f, root, savedRoot);
        var effects = f.Store.ReadyFolderEffects(f.Pair.PairId, operationId, -1, PageSize).ToArray();
        Check.Require(effects.Single().ItemOrdinal == 0, nameof(FolderRemovalEffect));

        var added = file with { Path = PathNames.Child(root.Path, nameof(FolderChild)), Id = nameof(FolderChild) };
        f.DropboxEntries.Seed(edit ? file with { Revision = nameof(Side.Local), Size = 2 } : added, [2]);
        string? cursorAtComparison = null;
        f.Dropbox.OnList = () => cursorAtComparison = f.Store.FolderRemoval(f.Pair.PairId, operationId).Cursor;
        f.Dropbox.Returns(new FolderSubmitResult.Terminal([new FolderMemberResult.Receipt(0, root)]));
        if (!delayedResume) await f.Runner.RunGroupAsync(effects, CancellationToken.None);
        await f.Runner.ResumeAsync(f.Store.FolderRemoval(f.Pair.PairId, operationId), null, CancellationToken.None);

        Check.Require(f.Dropbox.SubmitCalls == 0, nameof(ISide.SubmitFolderRemovalAsync));
        Check.Require(cursorAtComparison is not null, nameof(FolderRemovalState.Cursor));
        var operation = f.Store.FolderRemoval(f.Pair.PairId, operationId);
        Check.Require(operation.Phase != FolderRemovalPhase.Complete, nameof(FolderRemovalPhase.Complete));
        Check.Require(Item(f, operationId, 0).EffectState == FolderRemovalItem.EffectStatus.Ready,
            nameof(FolderRemovalItem.EffectStatus.Ready));
        Check.Require(!Gone(f.Store, f.Pair.PairId, savedFile), nameof(StateChange.ForgetSubtrees));
        Check.Require(await f.DropboxEntries.ReadOneAsync(file.Path, CancellationToken.None) is not null,
            nameof(FolderEffectKind.RecursiveFolder));
    }

    /// <summary>A-T3/A-T4: member outcomes are attributed by immutable wire ordinal, never by path or list position;
    /// duplicate or missing members stay unknown and authorize no settlement or replay.</summary>
    private static async Task MemberAttributionAsync(string folder)
    {
        using (var f = new Fixture(folder, nameof(MemberAttributionAsync)))
        {
            var root = await f.DropboxEntries.EnsureFolderAsync("Folder", CancellationToken.None);
            await f.DropboxEntries.EnsureFolderAsync(
                PathNames.Child(root.Path, nameof(FolderVerdict.Survives)), CancellationToken.None);
            var first = await f.DropboxEntries.EnsureFolderAsync(
                PathNames.Child(root.Path, nameof(Entry)), CancellationToken.None);
            var next = await f.DropboxEntries.EnsureFolderAsync(
                PathNames.Child(root.Path, nameof(FolderChild)), CancellationToken.None);
            var last = await f.DropboxEntries.EnsureFolderAsync(
                PathNames.Child(root.Path, nameof(StateChange)), CancellationToken.None);
            var savedRoot = Saved(root.Path, root.Path);
            var savedFirst = Saved(first.Path, first.Path);
            var savedNext = Saved(next.Path, next.Path);
            var savedLast = Saved(last.Path, last.Path);
            f.Store.Commit(f.Pair.PairId, StateChange.Empty with
            {
                Record = [savedRoot, savedFirst, savedNext, savedLast]
            });
            var operationId = await BeginAsync(f, root, savedRoot);
            var effects = new List<FolderRemovalEffect>();
            var after = -1L;
            while (true)
            {
                var page = f.Store.ReadyFolderEffects(f.Pair.PairId, operationId, after, PageSize).ToArray();
                if (page.Length == 0) break;
                effects.AddRange(page);
                after = page[^1].ItemOrdinal;
            }

            f.Dropbox.Returns(new FolderSubmitResult.Terminal(
            [
                new FolderMemberResult.Receipt(2, last),
                new FolderMemberResult.Receipt(0, first),
                new FolderMemberResult.Receipt(1, next)
            ]));
            await f.Runner.RunGroupAsync(effects, CancellationToken.None);
            foreach (var (path, ordinal) in new[] { (first, 0), (next, 1), (last, 2) })
            {
                var item = Item(f, operationId,
                    effects.Single(effect => effect.Expected!.Path == path.Path).ItemOrdinal);
                Check.Require(item.EffectState == FolderRemovalItem.EffectStatus.Verified &&
                    item.SubmissionMember == ordinal && item.EffectResult is FolderMemberResult.Receipt receipt &&
                    receipt.Ordinal == ordinal && receipt.Entry.Path == path.Path,
                    nameof(FolderMemberResult.Receipt));
            }

            await f.Runner.ResumeAsync(f.Store.FolderRemoval(f.Pair.PairId, operationId), null, CancellationToken.None);
            foreach (var gone in new[] { savedFirst, savedNext, savedLast })
                Check.Require(Gone(f.Store, f.Pair.PairId, gone), nameof(StateChange.ForgetSubtrees));
            Check.Require(!Gone(f.Store, f.Pair.PairId, savedRoot), nameof(StateChange.ForgetSubtrees));
        }

        using (var f = new Fixture(folder, nameof(MemberAttributionAsync) + nameof(TextKey.FailChanged)))
        {
            var root = await f.DropboxEntries.EnsureFolderAsync("Folder", CancellationToken.None);
            await f.DropboxEntries.EnsureFolderAsync(
                PathNames.Child(root.Path, nameof(FolderVerdict.Survives)), CancellationToken.None);
            var first = await f.DropboxEntries.EnsureFolderAsync(
                PathNames.Child(root.Path, nameof(Entry)), CancellationToken.None);
            var next = await f.DropboxEntries.EnsureFolderAsync(
                PathNames.Child(root.Path, nameof(FolderChild)), CancellationToken.None);
            var savedRoot = Saved(root.Path, root.Path);
            var savedFirst = Saved(first.Path, first.Path);
            var savedNext = Saved(next.Path, next.Path);
            f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [savedRoot, savedFirst, savedNext] });
            var operationId = await BeginAsync(f, root, savedRoot);
            var effects = f.Store.ReadyFolderEffects(f.Pair.PairId, operationId, -1, PageSize).ToArray();

            f.Dropbox.Returns(new FolderSubmitResult.Terminal(
            [
                new FolderMemberResult.KnownNoEffect(1, TextKey.FailChanged),
                new FolderMemberResult.KnownNoEffect(1, TextKey.FailChanged)
            ]));
            await f.Runner.RunGroupAsync(effects, CancellationToken.None);
            foreach (var effect in effects)
                Check.Require(Item(f, operationId, effect.ItemOrdinal).EffectState ==
                    FolderRemovalItem.EffectStatus.Unknown, nameof(FolderMemberResult.Unknown));
            Check.Require(!Gone(f.Store, f.Pair.PairId, savedFirst) && !Gone(f.Store, f.Pair.PairId, savedNext),
                nameof(StateChange.ForgetSubtrees));

            await f.Runner.ResumeAsync(f.Store.FolderRemoval(f.Pair.PairId, operationId), null, CancellationToken.None);
            Check.Require(f.Store.FolderRemoval(f.Pair.PairId, operationId).Phase == FolderRemovalPhase.Unknown,
                nameof(FolderRemovalPhase.Unknown));
            Check.Require(f.Dropbox.SubmitCalls == 1, nameof(ISide.SubmitFolderRemovalAsync));
            Check.Require(!Gone(f.Store, f.Pair.PairId, savedFirst) && !Gone(f.Store, f.Pair.PairId, savedNext),
                nameof(StateChange.ForgetSubtrees));
        }
    }

    /// <summary>A-T2/A-T4: receipt-only reconciliation persists provider-certain terminal facts but performs no
    /// mapping settlement, follow-up, recovery effect, retirement or root completion.</summary>
    private static async Task ReceiptOnlyAsync(string folder)
    {
        using var f = new Fixture(folder, nameof(ReceiptOnlyAsync));
        var root = await f.DropboxEntries.EnsureFolderAsync("Folder", CancellationToken.None);
        var child = await f.DropboxEntries.EnsureFolderAsync(
            PathNames.Child(root.Path, nameof(FolderChild)), CancellationToken.None);
        var savedRoot = Saved(root.Path, root.Path);
        var savedChild = Saved(child.Path, child.Path);
        f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [savedRoot, savedChild] });
        var operationId = await BeginAsync(f, root, savedRoot);
        var effects = f.Store.ReadyFolderEffects(f.Pair.PairId, operationId, -1, PageSize).ToArray();
        var submissionId = Accepted(f, effects);

        f.Dropbox.Returns(new FolderJobResult.Terminal([new FolderMemberResult.Receipt(0, root)]));
        await f.Runner.ReconcileFolderRemovalsAsync(
            CancellationToken.None);
        Check.Require(f.Dropbox.SubmitCalls == 0 && f.Dropbox.ReadCalls == 1,
            nameof(IRecoveryExecutionBoundary));
        Check.Require(f.Store.FolderSubmission(submissionId).Phase == FolderSubmission.SubmissionPhase.Terminal,
            nameof(FolderSubmission.SubmissionPhase));
        var item = Item(f, operationId, 0);
        Check.Require(item.EffectState == FolderRemovalItem.EffectStatus.Verified &&
            item.Phase == FolderRemovalItem.RecoveryPhase.Deleted, nameof(FolderRemovalItem.EffectStatus.Verified));
        Check.Require(!Gone(f.Store, f.Pair.PairId, savedRoot) && !Gone(f.Store, f.Pair.PairId, savedChild),
            nameof(StateChange.ForgetSubtrees));
        var reconciled = f.Store.FolderRemoval(f.Pair.PairId, operationId);
        Check.Require(reconciled.Active && reconciled.Phase == FolderRemovalPhase.Prepared,
            nameof(FolderRemovalPhase.Prepared));
        Check.Require(!f.Store.PendingFolderFollowUps(f.Pair.PairId, operationId, -1, PageSize).Any(),
            nameof(IStateStore.PendingFolderFollowUps));

        await f.Runner.ResumeAsync(reconciled, null, CancellationToken.None);
        Check.Require(Item(f, operationId, 0).EffectState == FolderRemovalItem.EffectStatus.Settled,
            nameof(FolderRemovalItem.EffectStatus.Settled));
        var continued = f.Store.FolderRemoval(f.Pair.PairId, operationId);
        Check.Require(!continued.Active && continued.Phase == FolderRemovalPhase.Complete,
            nameof(FolderRemovalPhase.Complete));
        Check.Require(Gone(f.Store, f.Pair.PairId, savedRoot), nameof(StateChange.ForgetSubtrees));
        Check.Require(f.Dropbox.SubmitCalls == 0 && f.Dropbox.ReadCalls == 1, nameof(ISide.SubmitFolderRemovalAsync));
    }

    private static async Task SharedReceiptPassAsync(string folder)
    {
        using var f = new Fixture(folder, nameof(SharedReceiptPassAsync));
        var effects = new List<FolderRemovalEffect>();
        foreach (var path in new[] { "First", "Second" })
        {
            var root = await f.DropboxEntries.EnsureFolderAsync(path, default);
            var saved = Saved(path, path);
            f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [saved] });
            var operation = await BeginAsync(f, root, saved);
            effects.AddRange(f.Store.ReadyFolderEffects(f.Pair.PairId, operation, -1, PageSize));
        }
        var id = Accepted(f, effects);
        f.Dropbox.Returns(new FolderJobResult.Terminal(effects.Select((effect, ordinal) =>
            (FolderMemberResult)new FolderMemberResult.Receipt(ordinal, effect.Expected!)).ToArray()));
        await f.Runner.ReconcileFolderRemovalsAsync(default);
        await f.Runner.ReconcileFolderRemovalsAsync(default);
        Check.Require(f.Dropbox.ReadCalls == 1 && f.Dropbox.SubmitCalls == 0 &&
            f.Store.FolderSubmission(id).Phase == FolderSubmission.SubmissionPhase.Terminal &&
            effects.All(effect => Item(f, effect.OperationId, effect.ItemOrdinal).EffectState ==
                FolderRemovalItem.EffectStatus.Verified),
            "A shared submission is observed once; later passes never reread terminal receipts.");
        Check.Require(effects.All(effect => !Gone(f.Store, f.Pair.PairId,
            Saved(effect.Expected!.Path, effect.Expected.Path))), "Receipts do not settle either root.");
    }

    private static async Task CancelReceiptPassAsync(string folder)
    {
        using var f = new Fixture(folder, nameof(CancelReceiptPassAsync));
        var root = await f.DropboxEntries.EnsureFolderAsync("Folder", default);
        var operation = await BeginAsync(f, root, Saved(root.Path, root.Path));
        var id = Accepted(f, f.Store.ReadyFolderEffects(f.Pair.PairId, operation, -1, PageSize).ToArray());
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        try
        {
            await f.Runner.ReconcileFolderRemovalsAsync(cancel.Token);
            throw new Exception("cancelled receipt pass continued");
        }
        catch (OperationCanceledException) { }
        Check.Require(f.Dropbox.ReadCalls == 0 && f.Store.FolderSubmission(id).Phase ==
            FolderSubmission.SubmissionPhase.Accepted, "Cancellation before observation preserves accepted custody.");
    }

    private static async Task ReceiptStorageFailureAsync(string folder)
    {
        using var f = new Fixture(folder, nameof(ReceiptStorageFailureAsync));
        var root = await f.DropboxEntries.EnsureFolderAsync("Folder", default);
        var operation = await BeginAsync(f, root, Saved(root.Path, root.Path));
        var id = Accepted(f, f.Store.ReadyFolderEffects(f.Pair.PairId, operation, -1, PageSize).ToArray());
        var accepted = f.Store.FolderSubmission(id);
        foreach (var error in new Exception[]
            { new FolderStateRefusedException(), new InvalidOperationException(), new InvalidDataException() })
        {
            var context = f.Context with { Store = new ReceiptObservationStore(f.Store, error) };
            var syncer = new Syncer(context, new SyncPorts(new PassthroughBoundary(),
                RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
            f.Dropbox.Returns(new FolderJobResult.InProgress());
            Exception? caught = null;
            try { await syncer.ReconcileReceiptsAsync(default); }
            catch (Exception actual) { caught = actual; }
            Check.Require(error is FolderStateRefusedException ? caught is null : ReferenceEquals(error, caught),
                "Only a stale observation refusal is consumed; programmer and storage failures escape.");
            Check.Require(f.Store.FolderSubmission(id) == accepted, "Refused observation leaves custody unchanged.");
        }
        Check.Require(f.Dropbox.ReadCalls == 3, "Every failed pass releases its observation claim.");
    }

    // EX-11/A136-A137: the runner absorbs only the typed stale refusal; a programmer error escapes it.
    private static async Task StorageRefusalAsync(string folder)
    {
        foreach (var real in new[] { true, false })
        foreach (var side in Enum.GetValues<Side>())
        foreach (var onSettle in new[] { true, false })
        foreach (var typed in new[] { true, false })
        {
            using var f = new Fixture(folder, nameof(StorageRefusalAsync));
            using var fake = real ? null : new InMemoryStateStore();
            IStateStore store = real ? f.Store : fake!;
            if (!real) store.SavePair(f.Pair);
            var entries = side == Side.Local ? f.LocalEntries : f.DropboxEntries;
            var root = await entries.EnsureFolderAsync("Folder", default);
            var saved = Saved(root.Path, root.Path);
            f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [saved] });
            if (!real) store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [saved] });
            var row = Engine.Row.ForOneSide(side, PathNames.KeyFor(side, root.Path), root, saved, [], null, Row(root, saved).Context);
            using var plan = await new FolderPlanBuilder(f.Context).BuildAsync(row, side,
                new HashSet<string>(StringComparer.Ordinal), live: false, CancellationToken.None);
            var operationId = store.BeginFolderRemoval(f.Pair.PairId, plan, PageSize, default);
            var prepared = store.FolderRemoval(f.Pair.PairId, operationId);
            store.AdvanceFolderRemoval(f.Pair.PairId, operationId, prepared.Generation, prepared.Phase,
                prepared with { Generation = prepared.Generation + 1, Phase = FolderRemovalPhase.Submitted, Cursor = "cursor" });
            var effects = store.ReadyFolderEffects(f.Pair.PairId, operationId, -1, PageSize).ToArray();
            var submissionId = store.ArmFolderSubmission(f.Pair.PairId, effects);
            var submission = store.FolderSubmission(submissionId);
            store.AdvanceFolderSubmission(submission, submission with
            {
                Generation = submission.Generation + 1, Phase = FolderSubmission.SubmissionPhase.Terminal
            }, effects.Select((effect, index) => (FolderMemberResult)new FolderMemberResult.Receipt(index, effect.Expected!)).ToArray());
            await entries.DeleteAsync(root, null, default);
            Exception error = typed ? new FolderStateRefusedException() : new InvalidOperationException();
            var context = f.Context with { Store = new RefusingFolderStore(store, onSettle, error) };
            var transfers = new Transfers(context);
            var runner = new FolderDeleteRunner(context,
                new ActionExecutor(context, transfers, new Parents(context), new KeepBothRunner(context, transfers)),
                new PassthroughBoundary());
            Exception? escaped = null;
            try { await runner.ResumeAsync(store.FolderRemoval(f.Pair.PairId, operationId), null, default); }
            catch (Exception caught) { escaped = caught; }
            var retained = store.FolderRemoval(f.Pair.PairId, operationId);
            Check.Require(typed
                    ? escaped is null && retained.Active && retained.Phase == FolderRemovalPhase.Retained &&
                        retained.Cause == TextKey.FailChanged
                    : escaped?.GetType() == typeof(InvalidOperationException),
                typed
                    ? $"A stale settle or complete is retained with its cause ({real}, {side}, {onSettle})."
                    : "A programmer error in settle or complete escapes the runner.");
            Check.Require(!Gone(store, f.Pair.PairId, saved) &&
                !f.Events.Any(e => e.Kind == RunEventKind.Effect && e.Path == root.Path),
                "A refused settle or complete forgets no mapping and reports no root success.");
        }
    }

    // F-04/A147 (BD-4): through the real preparation entry, a boundary cancellation disposes the plan's scratch
    // and leaves no operation or effect behind.
    private static async Task FreezeCancellationDisposesScratchAsync(string folder)
    {
        using var f = new Fixture(folder, nameof(FreezeCancellationDisposesScratchAsync));
        var root = await f.DropboxEntries.EnsureFolderAsync("Folder", default);
        await f.DropboxEntries.EnsureFolderAsync("Folder/Child", default);
        var saved = Saved(root.Path, root.Path);
        f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [saved] });
        using var cancel = new CancellationTokenSource();
        var store = new CancellingFreezeStore(f.Store, cancel);
        var context = f.Context with { Store = store };
        var parents = new Parents(context);
        var transfers = new Transfers(context);
        var keepBoth = new KeepBothRunner(context, transfers);
        var actions = new ActionExecutor(context, transfers, parents, keepBoth);
        var runner = new FolderDeleteRunner(context, actions, new PassthroughBoundary());
        var ancestors = new AncestorFence(context);
        var ports = new SyncPorts(new PassthroughBoundary(), RefusingExecutionBoundary.Instance,
            RefusingExecutionBoundary.Instance, 0);
        var items = new ItemSync(context, parents, actions, keepBoth, runner, ports, ancestors);
        Exception? caught = null;
        try
        {
            await items.Folders.PrepareFolderRemovalAsync(Row(root, saved), Side.Dropbox,
                new HashSet<string>(), new HashSet<string>(), cancel.Token);
        }
        catch (Exception error) { caught = error; }
        Check.Require(caught is OperationCanceledException && store.Plan is not null,
            "A freeze cancelled through preparation stops with its plan captured.");
        var disposed = false;
        try { _ = store.Plan!.DeleteWhole; }
        catch (ObjectDisposedException) { disposed = true; }
        Check.Require(disposed &&
            !Directory.EnumerateFiles(folder, "*" + StateSchema.PlanFileSuffix + "*" + StateSchema.DatabaseExtension).Any(),
            "Preparation disposes the real scratch plan on a cancelled freeze.");
        Check.Require(!f.Store.ActiveFolderRemovals(f.Pair.PairId, default).Any() &&
            f.Events.Count == 0 && f.Dropbox.SubmitCalls == 0,
            "A cancelled freeze records no operation and emits no effect.");
    }

    private sealed class CancellingFreezeStore(IStateStore inner, CancellationTokenSource cancel)
        : ForwardingStateStore(inner)
    {
        public FolderPlan? Plan { get; private set; }

        public override string BeginFolderRemoval(string pairId, FolderPlan plan, int pageSize, CancellationToken ct,
            Action? admit = null)
        {
            Plan = plan;
            return base.BeginFolderRemoval(pairId, new CancellingFolderPlan(plan, cancel, after: 1), pageSize,
                cancel.Token, admit);
        }
    }

    private sealed class RefusingFolderStore(IStateStore inner, bool onSettle, Exception error)
        : ForwardingStateStore(inner)
    {
        public override IReadOnlyList<bool> SettleFolderRemovalItems(string pairId, string operationId,
            IReadOnlyList<(long ExpectedGeneration, FolderRemovalItem Next, StateChange Change)> items)
        {
            if (!onSettle) return base.SettleFolderRemovalItems(pairId, operationId, items);
            if (error is FolderStateRefusedException) return new bool[items.Count];
            throw error;
        }

        public override void CompleteFolderRemoval(string pairId, string operationId, long expectedGeneration,
            StateChange verifiedRootChange, WorkSelection? selection)
        {
            if (!onSettle) throw error;
            base.CompleteFolderRemoval(pairId, operationId, expectedGeneration, verifiedRootChange, selection);
        }
    }

    private sealed class ReceiptObservationStore(IStateStore inner, Exception error) : ForwardingStateStore(inner)
    {
        public override void RecordFolderJobObservation(FolderSubmission expected, FolderJobResult? observation,
            TextKey? failureCause) => throw error;
    }

    private static async Task CancelSecondReceiptAsync(string folder)
    {
        using var f = new Fixture(folder, nameof(CancelSecondReceiptAsync));
        foreach (var path in new[] { "First", "Second" })
        {
            var root = await f.DropboxEntries.EnsureFolderAsync(path, default);
            var operation = await BeginAsync(f, root, Saved(path, path));
            Accepted(f, f.Store.ReadyFolderEffects(f.Pair.PairId, operation, -1, PageSize).ToArray());
        }
        var pending = f.Store.PendingFolderSubmissions(f.Pair.PairId, default).ToArray();
        f.Dropbox.Returns(new FolderJobResult.Terminal([new FolderMemberResult.KnownNoEffect(0, null)]));
        f.Dropbox.Returns(new TaskCompletionSource<FolderJobResult>().Task);
        using var cancel = new CancellationTokenSource();
        var pass = f.Runner.ReconcileFolderRemovalsAsync(cancel.Token);
        Check.Require(f.Dropbox.ReadCalls == 2 && !pass.IsCompleted, "The second observation is in flight.");
        cancel.Cancel();
        try { await pass; throw new Exception("cancelled observation completed"); }
        catch (OperationCanceledException) { }
        Check.Require(f.Store.FolderSubmission(pending[0].SubmissionId).Phase == FolderSubmission.SubmissionPhase.Terminal &&
            f.Store.FolderSubmission(pending[1].SubmissionId) == pending[1],
            "Cancellation keeps the first known result and leaves the second receipt untouched.");
        f.Dropbox.Returns(new FolderJobResult.Terminal([new FolderMemberResult.KnownNoEffect(0, null)]));
        await f.Runner.ReconcileFolderRemovalsAsync(default);
        Check.Require(f.Dropbox.ReadCalls == 3 && f.Dropbox.SubmitCalls == 0,
            "The next pass observes only the unfinished job, with its cancelled claim released.");
    }

    private static async Task JoinRereadsClaimAsync(string folder, bool terminal)
    {
        using var f = new Fixture(folder, nameof(JoinRereadsClaimAsync));
        var root = await f.DropboxEntries.EnsureFolderAsync("Folder", default);
        var saved = Saved(root.Path, root.Path);
        f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [saved] });
        var operation = await BeginAsync(f, root, saved);
        var id = Accepted(f, f.Store.ReadyFolderEffects(f.Pair.PairId, operation, -1, PageSize).ToArray());
        var clock = new FakeTimeProvider();
        var context = f.Context with
        {
            Time = clock,
            Settings = f.Context.Settings with { FolderDeleteMinimumPollSeconds = 1 }
        };
        var syncer = new Syncer(context, new SyncPorts(new PassthroughBoundary(),
            RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
        var join = syncer.RecoverAsync(new RecoveryTarget(operation,
            f.Store.FolderRemoval(f.Pair.PairId, operation).Generation, null, null), RecoveryChoice.Check, default);
        Check.Require(!join.IsCompleted && f.Dropbox.ReadCalls == 0,
            "The join captures Accepted before its first interval and claim.");
        if (terminal) f.Dropbox.Returns(new FolderJobResult.Terminal([new FolderMemberResult.Receipt(0, root)]));
        else f.Dropbox.Throws(new ItemFailedException(root.Path, TextKey.FailDropboxDeleteJobInvalid, null));
        await f.Runner.ReconcileFolderRemovalsAsync(default);
        if (!terminal) f.Dropbox.Returns(new FolderJobResult.Terminal([new FolderMemberResult.Receipt(0, root)]));
        clock.Advance(TimeSpan.FromSeconds(1));
        await join;
        Check.Require(f.Dropbox.ReadCalls == (terminal ? 1 : 2) &&
            f.Store.FolderSubmission(id).Phase == FolderSubmission.SubmissionPhase.Terminal,
            "A join rereads terminal or changed-generation state after acquiring its first claim.");
    }

    private static async Task JoinOwnsReceiptAsync(string folder)
    {
        using var f = new Fixture(folder, nameof(JoinOwnsReceiptAsync));
        var root = await f.DropboxEntries.EnsureFolderAsync("Folder", default);
        var operation = await BeginAsync(f, root, Saved(root.Path, root.Path));
        Accepted(f, f.Store.ReadyFolderEffects(f.Pair.PairId, operation, -1, PageSize).ToArray());
        var clock = new FakeTimeProvider();
        var context = f.Context with
        {
            Time = clock,
            Settings = f.Context.Settings with { FolderDeleteMinimumPollSeconds = 1 }
        };
        var syncer = new Syncer(context, new SyncPorts(new PassthroughBoundary(),
            RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
        using var cancel = new CancellationTokenSource();
        f.Dropbox.Returns(new TaskCompletionSource<FolderJobResult>().Task);
        var join = syncer.RecoverAsync(new RecoveryTarget(operation,
            f.Store.FolderRemoval(f.Pair.PairId, operation).Generation, null, null), RecoveryChoice.Check, cancel.Token);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Task.WhenAny(join, f.Dropbox.ReadStarted.Task);
        Check.Require(f.Dropbox.ReadCalls == 1 && !join.IsCompleted, "The running join holds its provider observation.");
        await f.Runner.ReconcileFolderRemovalsAsync(default);
        Check.Require(f.Dropbox.ReadCalls == 1, "A receipt opportunity does not read a running join's job.");
        var reads = new ClaimReadStore(f.Store);
        var subscriber = new Syncer(context with { Store = reads }, new SyncPorts(new PassthroughBoundary(),
            RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
        var second = subscriber.RecoverAsync(new RecoveryTarget(operation,
            f.Store.FolderRemoval(f.Pair.PairId, operation).Generation, null, null), RecoveryChoice.Check, default);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Task.WhenAny(second, reads.Read.Task);
        Check.Require(!second.IsCompleted && reads.Read.Task.IsCompleted && f.Dropbox.ReadCalls == 1,
            "A second join rereads durable state but does not duplicate the holder's provider read.");
        cancel.Cancel();
        try { await join; throw new Exception("cancelled join completed"); }
        catch (OperationCanceledException) { }
        f.Dropbox.Returns(new FolderJobResult.Terminal([new FolderMemberResult.Receipt(0, root)]));
        while (!second.IsCompleted)
        {
            if (clock.HasArmedTimer) clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }
        await second;
        await f.Runner.ReconcileFolderRemovalsAsync(default);
        Check.Require(f.Dropbox.ReadCalls == 2, "A subscribed join takes over after cancellation, without a resend.");
    }

    private sealed class ClaimReadStore(IStateStore inner) : ForwardingStateStore(inner)
    {
        internal readonly TaskCompletionSource Read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override FolderSubmission FolderSubmission(string submissionId)
        {
            var current = base.FolderSubmission(submissionId);
            Read.TrySetResult();
            return current;
        }
    }

    private static async Task ClaimedReceiptPassAsync(string folder)
    {
        using var f = new Fixture(folder, nameof(ClaimedReceiptPassAsync));
        var root = await f.DropboxEntries.EnsureFolderAsync("Folder", default);
        var operation = await BeginAsync(f, root, Saved(root.Path, root.Path));
        var id = Accepted(f, f.Store.ReadyFolderEffects(f.Pair.PairId, operation, -1, PageSize).ToArray());
        var held = new TaskCompletionSource<FolderJobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Dropbox.Returns(held.Task);
        var first = f.Runner.ReconcileFolderRemovalsAsync(default);
        Check.Require(f.Dropbox.ReadCalls == 1 && !first.IsCompleted, "The first pass owns its outstanding read.");
        await f.Runner.ReconcileFolderRemovalsAsync(default);
        Check.Require(f.Dropbox.ReadCalls == 1, "An overlapping pass skips the claimed job without waiting.");
        held.SetResult(new FolderJobResult.InProgress());
        await first;
        f.Dropbox.Returns(new FolderJobResult.Terminal([new FolderMemberResult.Receipt(0, root)]));
        await f.Runner.ReconcileFolderRemovalsAsync(default);
        Check.Require(f.Dropbox.ReadCalls == 2 && f.Store.FolderSubmission(id).Phase ==
            FolderSubmission.SubmissionPhase.Terminal, "The claim releases for the next finite opportunity.");
    }

    /// <summary>A133f: with J held fixed, one pass counts the same work at both pair sizes (KF-2026-11-01-1 W01/W02
    /// anchors; the relation has no pair-size term). One enumeration, J keyset rows, J row rereads, J effect reads,
    /// J observations and J provider reads.</summary>
    private static async Task ReceiptCountsDoNotScaleAsync(string folder)
    {
        var small = await ReceiptPassCountsAsync(Path.Combine(folder, "anchor-S"), unrelatedRows: 0);
        var large = await ReceiptPassCountsAsync(Path.Combine(folder, "anchor-L"), unrelatedRows: 240);
        Check.Require(small == large, "the receipt pass counts are equal at both sizes: " + small + " vs " + large);
        Check.Require(small == (1, 2, 2, 2, 2, 2),
            "one enumeration, two keyset rows, two row rereads, two effect reads, two observations, two reads: " + small);
    }

    private static async Task<(int Passes, int Rows, int RowReads, int Effects, int Observations, int Provider)>
        ReceiptPassCountsAsync(string folder, int unrelatedRows)
    {
        Directory.CreateDirectory(folder);
        using var f = new Fixture(folder, nameof(ReceiptCountsDoNotScaleAsync));
        var effects = new List<FolderRemovalEffect>();
        foreach (var path in new[] { "Folder0", "Folder1" })
        {
            var root = await f.DropboxEntries.EnsureFolderAsync(path, default);
            var operation = await BeginAsync(f, root, Saved(path, path));
            var selected = f.Store.ReadyFolderEffects(f.Pair.PairId, operation, -1, PageSize).ToArray();
            Accepted(f, selected);
            effects.AddRange(selected);
        }
        if (unrelatedRows != 0)
            f.Store.Commit(f.Pair.PairId, StateChange.Empty with
            {
                Record = Enumerable.Range(0, unrelatedRows)
                    .Select(index => Saved("unrelated/" + index.ToString(CultureInfo.InvariantCulture) + ".txt",
                        "unrelated/" + index.ToString(CultureInfo.InvariantCulture) + ".txt")).ToArray()
            });
        foreach (var effect in effects)
            f.Dropbox.Returns(new FolderJobResult.Terminal([new FolderMemberResult.Receipt(0, effect.Expected!)]));
        var counts = new ReceiptCountStore(f.Store);
        var syncer = new Syncer(f.Context with { Store = counts }, new SyncPorts(new PassthroughBoundary(),
            RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
        await syncer.ReconcileReceiptsAsync(default);
        return (counts.Passes, counts.Yielded, counts.RowReads, counts.EffectReads, counts.Observations,
            f.Dropbox.ReadCalls);
    }

    /// <summary>A133b owner half: selections run inside one opportunity, so none of them enters the receipt pass and
    /// N selections add no provider job reads.</summary>
    private static async Task SelectionNeverRepeatsReceiptsAsync(string folder)
    {
        using var f = new Fixture(folder, nameof(SelectionNeverRepeatsReceiptsAsync));
        var root = await f.DropboxEntries.EnsureFolderAsync("Folder", default);
        var operation = await BeginAsync(f, root, Saved(root.Path, root.Path));
        Accepted(f, f.Store.ReadyFolderEffects(f.Pair.PairId, operation, -1, PageSize).ToArray());
        var counts = new ReceiptCountStore(f.Store);
        var boundary = new PassthroughBoundary();
        var syncer = new Syncer(f.Context with { Store = counts }, new SyncPorts(boundary,
            boundary, RefusingExecutionBoundary.Instance, 0));
        for (var index = 0; index < 3; index++)
            counts.Selections.Enqueue(new WorkSelection(f.Pair.PairId, f.Context.ExpectedGeneration, 0, Side.Local,
                "local", null, "unrelated" + index.ToString(CultureInfo.InvariantCulture) + ".txt", 0, 0));
        f.Dropbox.Returns(new FolderJobResult.InProgress());
        await syncer.SyncWorkAsync(default);
        // DESIGN-114 §3.6: multiple refused lanes with no progress require one serial fallback round.
        Check.Require(counts.Selected == 3 && counts.Ended == 2 && boundary.Holds == 3,
            "three refused selections trigger one empty serial fallback without losing the generation hold");
        Check.Require(counts.Passes == 1 && counts.Yielded == 1 && f.Dropbox.ReadCalls == 1,
            "three selections share the opportunity's single receipt pass and add no per-selection job reads.");
    }

    private static async Task ProgressReceiptCasesAsync(string folder)
    {
        using var f = new Fixture(folder, nameof(ProgressReceiptCasesAsync));
        var counts = new ReceiptCountStore(f.Store);
        var syncer = new Syncer(f.Context with { Store = counts }, new SyncPorts(new PassthroughBoundary(),
            RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
        var connection = (SqliteConnection)typeof(StateStore).GetField("_connection",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(f.RawStore)!;
        async Task CountAsync(string name, bool progressOnly)
        {
            var statements = new List<string>();
            var reads = f.Dropbox.ReadCalls;
            SQLitePCL.raw.sqlite3_trace(connection.Handle,
                (SQLitePCL.delegate_trace)((_, sql) => statements.Add(sql.utf8_to_string())), null);
            try
            {
                var before = statements.Count;
                f.Store.HasFolderJobProgress(f.Pair.PairId, default);
                Check.Require(statements.Count - before == 1, "eligibility is one SQL statement, even for failed-only jobs");
                await syncer.ReconcileReceiptsAsync(default, progressOnly);
            }
            finally { SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null); }
            Console.WriteLine($"folder-progress SQL {name}: statements={statements.Count}, " +
                $"provider={f.Dropbox.ReadCalls - reads}, transactions={statements.Count(sql => sql.StartsWith("BEGIN", StringComparison.Ordinal))}");
        }
        await CountAsync("no-job", true);
        Check.Require(f.Dropbox.ReadCalls == 0 && counts.RowReads == 0 && counts.EffectReads == 0,
            "no eligible job causes no provider, submission or member reads");
        var failedRoot = await f.DropboxEntries.EnsureFolderAsync("Failed", default);
        var failedOperation = await BeginAsync(f, failedRoot, Saved("Failed", "Failed"));
        var failedId = Accepted(f, f.Store.ReadyFolderEffects(f.Pair.PairId, failedOperation, -1, PageSize).ToArray());
        f.Dropbox.Throws(new ItemFailedException("Failed", TextKey.DropboxRefused, null));
        await CountAsync("failed-only-failure", false);
        Check.Require(f.Store.Issues(f.Pair.PairId, [IssueKind.Failure]).Count() == 1 &&
            Massdex.SyncShark.Engine.Run.EngineOperations.ResolutionChoices(
                f.Store.Issues(f.Pair.PairId, [IssueKind.Failure]).Single())
                .Contains(ResolutionChoice.Retry) &&
            f.Store.FolderSubmission(failedId).Cause == TextKey.DropboxRefused,
            "a failed-only observation exits automatic waiting through one visible Failure");
        var failedReads = f.Dropbox.ReadCalls;
        await CountAsync("failed-only-progress", true);
        Check.Require(f.Dropbox.ReadCalls == failedReads, "a failed-only job has no synthetic progress read");
        var root = await f.DropboxEntries.EnsureFolderAsync("Progress", default);
        var operation = await BeginAsync(f, root, Saved("Progress", "Progress"));
        var id = Accepted(f, f.Store.ReadyFolderEffects(f.Pair.PairId, operation, -1, PageSize).ToArray());
        for (var poll = 0; poll < 2; poll++)
        {
            f.Dropbox.Returns(new FolderJobResult.InProgress());
            await CountAsync("mixed-inprogress-" + poll, true);
            Check.Require(f.Dropbox.ReadCalls == failedReads + poll + 1 &&
                f.Store.FolderSubmission(failedId).Cause == TextKey.DropboxRefused,
                "mixed repeated progress reads only its eligible job, not unrelated failures");
        }
        await f.DropboxEntries.DeleteAsync(root, null, default);
        f.Dropbox.Returns(new FolderJobResult.Terminal([new FolderMemberResult.Receipt(0, root)]));
        await CountAsync("mixed-terminal", true);
        Check.Require(f.Store.FolderSubmission(id).Phase == FolderSubmission.SubmissionPhase.Terminal &&
            Item(f, operation, 0).EffectState == FolderRemovalItem.EffectStatus.Settled &&
            f.Store.FolderRemoval(f.Pair.PairId, operation).Phase == FolderRemovalPhase.Complete &&
            Item(f, failedOperation, 0).EffectState == FolderRemovalItem.EffectStatus.Accepted,
            "a cardinality-correct receipt settles only its operation, not the unrelated failed job");
        f.Dropbox.Returns(new FolderJobResult.InProgress());
        await CountAsync("failed-only-recovery", false);
        Check.Require(f.Store.FolderSubmission(failedId).Cause is null &&
            f.Store.HasFolderJobProgress(f.Pair.PairId, default),
            "Retry/opportunity can recover the failed-only job and restore its progress cadence");
        Check.Require(f.Dropbox.SubmitCalls == 0, "all receipt paths preserve the no-resend boundary");
        Console.WriteLine($"folder-progress counts: passes={counts.Passes}, rows={counts.Yielded}, " +
            $"rowReads={counts.RowReads}, effects={counts.EffectReads}, observations={counts.Observations}, " +
            $"provider={f.Dropbox.ReadCalls}, sends={f.Dropbox.SubmitCalls}");
    }

    private static async Task TerminalContinuationClaimsAsync(string folder)
    {
        using var f = new Fixture(folder, nameof(TerminalContinuationClaimsAsync));
        var effects = new List<FolderRemovalEffect>();
        foreach (var path in new[] { "First", "Second" })
        {
            var root = await f.DropboxEntries.EnsureFolderAsync(path, default);
            await f.DropboxEntries.EnsureFolderAsync(path + "/Keep", default);
            var first = await f.DropboxEntries.EnsureFolderAsync(path + "/One", default);
            var second = await f.DropboxEntries.EnsureFolderAsync(path + "/Two", default);
            var saved = Saved(path, path);
            f.Store.Commit(f.Pair.PairId, StateChange.Empty with
                { Record = [saved, Saved(first.Path, first.Path), Saved(second.Path, second.Path)] });
            var operation = await BeginAsync(f, root, saved);
            effects.AddRange(f.Store.ReadyFolderEffects(f.Pair.PairId, operation, -1, PageSize));
        }
        Check.Require(effects.Count == 4, "two shared operations each have two selected effects");
        var batches = new[] { new[] { effects[0], effects[2] }, new[] { effects[1], effects[3] } }
            .Select(batch => (Id: Accepted(f, batch), Effects: batch)).OrderBy(batch => batch.Id, StringComparer.Ordinal).ToArray();
        foreach (var batch in batches)
            f.Dropbox.Returns(new FolderJobResult.Terminal(batch.Effects.Select((effect, ordinal) =>
                (FolderMemberResult)new FolderMemberResult.Receipt(ordinal, effect.Expected!)).ToArray()));
        var finished = new List<string>();
        await f.Runner.ReconcileFolderRemovalsAsync(default, progressOnly: true, finish: (operationId, _) =>
        {
            foreach (var batch in batches)
            {
                Check.Require(PairRuntimes.TryClaimFolderJob(batch.Id), "the receipt claim is released before terminal continuation");
                PairRuntimes.ReleaseFolderJob(batch.Id);
            }
            finished.Add(operationId);
            return Task.CompletedTask;
        });
        Check.Require(batches.All(batch => f.Store.FolderSubmission(batch.Id).Phase == FolderSubmission.SubmissionPhase.Terminal) &&
            finished.Count == 2 && finished.Distinct(StringComparer.Ordinal).Count() == 2 &&
            effects.All(effect => finished.Contains(effect.OperationId)),
            "terminal continuation is invoked exactly once per distinct operation across submissions, not once per member");
    }

    private static async Task ReceiptRecoveryOwnerAsync(string folder)
    {
        using var f = new Fixture(folder, nameof(ReceiptRecoveryOwnerAsync));
        var root = await f.DropboxEntries.EnsureFolderAsync("Folder", default);
        var operation = await BeginAsync(f, root, Saved(root.Path, root.Path));
        var id = Accepted(f, f.Store.ReadyFolderEffects(f.Pair.PairId, operation, -1, PageSize).ToArray());
        var calls = 0;
        object? episode = null;
        var context = f.Context with
        {
            CompleteRecoveryAttemptAsync = (attempt, _) =>
            {
                Check.Require(attempt.Owner == AttemptOwner.Recovery &&
                    (episode is null || ReferenceEquals(episode, attempt.Operation)),
                    "consecutive retryable receipt observations belong to one Recovery episode");
                episode = attempt.Operation;
                calls++;
                return Task.FromResult(calls <= 2);
            }
        };
        var syncer = new Syncer(context, new SyncPorts(new PassthroughBoundary(),
            RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
        f.Dropbox.Throws(new IOException());
        f.Dropbox.Throws(new IOException());
        f.Dropbox.Returns(new FolderJobResult.InProgress());
        await syncer.ReconcileReceiptsAsync(default);
        Check.Require(calls == 3 && f.Dropbox.ReadCalls == 3 && f.Dropbox.SubmitCalls == 0 &&
            f.Store.FolderSubmission(id).Cause is null && !f.Store.Issues(f.Pair.PairId).Any(),
            "retryable receipt failures reach the existing completion owner until recovery, without a new timeout");
    }

    private sealed class ReceiptCountStore(IStateStore inner) : ForwardingStateStore(inner)
    {
        internal int Passes;
        internal int Yielded;
        internal int RowReads;
        internal int EffectReads;
        internal int Observations;
        internal readonly Queue<WorkSelection> Selections = new();
        internal int Selected;
        internal int Ended;

        public override WorkSelection? SelectWork(string pairId, long expectedGeneration, PathScope scope)
        {
            if (!Selections.TryDequeue(out var selection)) return base.SelectWork(pairId, expectedGeneration, scope);
            Selected++;
            return selection;
        }

        public override bool EndWorkPass(string pairId)
        {
            Ended++;
            return base.EndWorkPass(pairId);
        }

        public override IEnumerable<FolderSubmission> PendingFolderSubmissions(string pairId, CancellationToken ct, bool progressOnly = false)
        {
            Passes++;
            foreach (var submission in base.PendingFolderSubmissions(pairId, ct, progressOnly))
            {
                Yielded++;
                yield return submission;
            }
        }

        public override FolderSubmission FolderSubmission(string submissionId)
        {
            RowReads++;
            return base.FolderSubmission(submissionId);
        }

        public override IReadOnlyList<FolderRemovalEffect> FolderSubmissionEffects(string submissionId)
        {
            EffectReads++;
            return base.FolderSubmissionEffects(submissionId);
        }

        public override void RecordFolderJobObservation(FolderSubmission expected, FolderJobResult? observation,
            TextKey? failureCause)
        {
            Observations++;
            base.RecordFolderJobObservation(expected, observation, failureCause);
        }
    }

    /// <summary>T178 durable second pass: an invalid delete-job remains a protected blocker with unchanged custody
    /// and no mutation resend, is checked again at later opportunities, and clears only on a valid answer.</summary>
    private static async Task InvalidJobSecondPassAsync(string folder)
    {
        using var f = new Fixture(folder, nameof(InvalidJobSecondPassAsync));
        var root = await f.DropboxEntries.EnsureFolderAsync("Folder", CancellationToken.None);
        var child = await f.DropboxEntries.EnsureFolderAsync(
            PathNames.Child(root.Path, nameof(FolderChild)), CancellationToken.None);
        var savedRoot = Saved(root.Path, root.Path);
        var savedChild = Saved(child.Path, child.Path);
        f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [savedRoot, savedChild] });
        var operationId = await BeginAsync(f, root, savedRoot);
        var effects = f.Store.ReadyFolderEffects(f.Pair.PairId, operationId, -1, PageSize).ToArray();
        var submissionId = Accepted(f, effects);

        var invalid = new ItemFailedException(root.Path, TextKey.FailDropboxDeleteJobInvalid, null);
        f.Dropbox.Throws(invalid);
        await f.Runner.ReconcileFolderRemovalsAsync(
            CancellationToken.None);
        Check.Require(f.Dropbox.SubmitCalls == 0 && f.Dropbox.ReadCalls == 1,
            nameof(ISide.ReadFolderRemovalAsync));
        var blocked = f.Store.FolderSubmission(submissionId);
        Check.Require(blocked.Phase == FolderSubmission.SubmissionPhase.Accepted &&
            blocked.Cause == TextKey.FailDropboxDeleteJobInvalid, nameof(FolderSubmission.SubmissionPhase.Accepted));
        Check.Require(Item(f, operationId, 0).EffectState == FolderRemovalItem.EffectStatus.Accepted,
            nameof(FolderRemovalItem.EffectStatus.Accepted));
        Check.Require(f.Store.HasInvalidFolderJob(f.Pair.PairId, root.Path), nameof(IStateStore.HasInvalidFolderJob));
        Check.Require(f.Store.Issues(f.Pair.PairId).Single(issue => issue.Cause == TextKey.FailDropboxDeleteJobInvalid)
            .Path == root.Path, nameof(IssueKind.Failure));
        Check.Require(!Gone(f.Store, f.Pair.PairId, savedRoot) && !Gone(f.Store, f.Pair.PairId, savedChild) &&
            await f.DropboxEntries.ReadOneAsync(root.Path, CancellationToken.None) == root,
            nameof(FolderRemovalItem.EffectStatus.Accepted));

        f.Dropbox.Throws(invalid);
        await f.Runner.ReconcileFolderRemovalsAsync(
            CancellationToken.None);
        Check.Require(f.Dropbox.SubmitCalls == 0 && f.Dropbox.ReadCalls == 2,
            nameof(ISide.ReadFolderRemovalAsync));
        Check.Require(f.Store.HasInvalidFolderJob(f.Pair.PairId, root.Path) &&
            f.Store.Issues(f.Pair.PairId).Count(issue => issue.Cause == TextKey.FailDropboxDeleteJobInvalid) == 1,
            nameof(IStateStore.HasInvalidFolderJob));

        f.Dropbox.Throws(new InvalidOperationException());
        await f.Runner.ReconcileFolderRemovalsAsync(
            CancellationToken.None);
        Check.Require(f.Store.HasInvalidFolderJob(f.Pair.PairId, root.Path),
            "A failed provider read preserves invalid-job custody and does not abort the pass.");

        f.Dropbox.Returns(new FolderJobResult.InProgress());
        await f.Runner.ReconcileFolderRemovalsAsync(
            CancellationToken.None);
        Check.Require(f.Dropbox.SubmitCalls == 0 && f.Dropbox.ReadCalls == 4,
            nameof(ISide.ReadFolderRemovalAsync));
        var checkedAgain = f.Store.FolderSubmission(submissionId);
        Check.Require(checkedAgain.Phase == FolderSubmission.SubmissionPhase.Accepted &&
            checkedAgain.Cause is null && checkedAgain.JobId == blocked.JobId,
            nameof(FolderSubmission.SubmissionPhase.Accepted));
        Check.Require(!f.Store.HasInvalidFolderJob(f.Pair.PairId, root.Path) &&
            f.Store.Issues(f.Pair.PairId).All(issue => issue.Cause != TextKey.FailDropboxDeleteJobInvalid),
            nameof(IStateStore.HasInvalidFolderJob));
        Check.Require(Item(f, operationId, 0).EffectState == FolderRemovalItem.EffectStatus.Accepted &&
            !Gone(f.Store, f.Pair.PairId, savedRoot) && f.Dropbox.SubmitCalls == 0,
            nameof(FolderRemovalItem.EffectStatus.Accepted));
    }

    /// <summary>A-T4: an armed effect without a receipt becomes unknown custody, is never resent, and is visible for
    /// exact reconciliation rather than inferred from path absence.</summary>
    private static async Task LostReceiptAsync(string folder)
    {
        using var f = new Fixture(folder, nameof(LostReceiptAsync));
        var root = await f.DropboxEntries.EnsureFolderAsync("Folder", CancellationToken.None);
        var child = await f.DropboxEntries.EnsureFolderAsync(
            PathNames.Child(root.Path, nameof(FolderChild)), CancellationToken.None);
        var savedRoot = Saved(root.Path, root.Path);
        var savedChild = Saved(child.Path, child.Path);
        f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [savedRoot, savedChild] });
        var operationId = await BeginAsync(f, root, savedRoot);
        var effects = f.Store.ReadyFolderEffects(f.Pair.PairId, operationId, -1, PageSize).ToArray();
        var submissionId = f.Store.ArmFolderSubmission(f.Pair.PairId, effects);
        Check.Require(f.Store.FolderSubmission(submissionId).Phase == FolderSubmission.SubmissionPhase.Armed,
            nameof(FolderSubmission.SubmissionPhase.Armed));

        await f.Runner.ReconcileFolderRemovalsAsync(
            CancellationToken.None);
        Check.Require(f.Dropbox.ReadCalls == 0 && f.Dropbox.SubmitCalls == 0,
            nameof(FolderSubmission.SubmissionPhase.Armed));

        await f.Runner.ResumeAsync(f.Store.FolderRemoval(f.Pair.PairId, operationId), null, CancellationToken.None);
        Check.Require(f.Store.FolderSubmission(submissionId).Phase == FolderSubmission.SubmissionPhase.Unknown,
            nameof(FolderSubmission.SubmissionPhase.Unknown));
        var item = Item(f, operationId, 0);
        Check.Require(item.EffectState == FolderRemovalItem.EffectStatus.Unknown &&
            item.Phase == FolderRemovalItem.RecoveryPhase.Unknown, nameof(FolderRemovalItem.EffectStatus.Unknown));
        Check.Require(f.Store.FolderRemoval(f.Pair.PairId, operationId).Phase == FolderRemovalPhase.Unknown,
            nameof(FolderRemovalPhase.Unknown));
        Check.Require(f.Dropbox.SubmitCalls == 0 && f.Dropbox.ReadCalls == 0,
            nameof(ISide.SubmitFolderRemovalAsync));
        Check.Require(!Gone(f.Store, f.Pair.PairId, savedRoot) &&
            await f.DropboxEntries.ReadOneAsync(root.Path, CancellationToken.None) == root,
            nameof(StateChange.ForgetSubtrees));
    }

    /// <summary>FREEZE X1 guarded pair removal: an active operation or live submission custody refuses removal with
    /// the whole transaction rolled back; only an exact acknowledged terminal disposition permits it.</summary>
    private static async Task PairRemovalCustodyAsync(string folder)
    {
        using var f = new Fixture(folder, nameof(PairRemovalCustodyAsync));
        var root = await f.DropboxEntries.EnsureFolderAsync("Folder", CancellationToken.None);
        var child = await f.DropboxEntries.EnsureFolderAsync(
            PathNames.Child(root.Path, nameof(FolderChild)), CancellationToken.None);
        var savedRoot = Saved(root.Path, root.Path);
        var savedChild = Saved(child.Path, child.Path);
        f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [savedRoot, savedChild] });
        var operationId = await BeginAsync(f, root, savedRoot);
        var effects = f.Store.ReadyFolderEffects(f.Pair.PairId, operationId, -1, PageSize).ToArray();

        Check.Require(Throws<InvalidOperationException>(() => f.Store.RemovePair(f.Pair.PairId)) &&
            f.Store.Pair(f.Pair.PairId) is not null && !Gone(f.Store, f.Pair.PairId, savedRoot),
            nameof(IStateStore.RemovePair));

        var submissionId = f.Store.ArmFolderSubmission(f.Pair.PairId, effects);
        Check.Require(Throws<InvalidOperationException>(() => f.Store.RemovePair(f.Pair.PairId)),
            nameof(FolderSubmission.SubmissionPhase.Armed));

        var armed = f.Store.FolderSubmission(submissionId);
        f.Store.AdvanceFolderSubmission(armed,
            armed with { Generation = checked(armed.Generation + 1), Phase = FolderSubmission.SubmissionPhase.Unknown }, []);
        Check.Require(Throws<InvalidOperationException>(() => f.Store.RemovePair(f.Pair.PairId)),
            nameof(FolderRemovalPhase.Unknown));

        var current = f.Store.FolderRemoval(f.Pair.PairId, operationId);
        f.Store.RetireFolderRemoval(f.Pair.PairId, operationId, current.Generation,
            FolderRetirementDisposition.Acknowledged, StateChange.Empty);
        var retired = f.Store.FolderRemoval(f.Pair.PairId, operationId);
        Check.Require(!retired.Active && retired.RetirementDisposition == FolderRetirementDisposition.Acknowledged,
            nameof(FolderRetirementDisposition.Acknowledged));

        f.Store.RemovePair(f.Pair.PairId);
        Check.Require(f.Store.Pair(f.Pair.PairId) is null, nameof(IStateStore.RemovePair));
    }

    /// <summary>Freezes one completed plan into its durable operation, then disposes the disposable scratch.</summary>
    private static async Task ProtectionAfterFreezeAsync(string folder)
    {
        foreach (var crossLinked in new[] { false, true })
        foreach (var partial in new[] { false, true })
        foreach (var stateOnly in new[] { false, true })
        {
            using var f = new Fixture(folder, nameof(ProtectionAfterFreezeAsync));
            var root = await f.DropboxEntries.EnsureFolderAsync("Folder", default);
            if (!stateOnly) await f.DropboxEntries.EnsureFolderAsync("Folder/Child", default);
            if (partial) await f.DropboxEntries.EnsureFolderAsync("Folder/Keep", default);
            var savedRoot = Saved(root.Path, root.Path);
            var savedChild = Saved(crossLinked ? "Elsewhere/Child" : "Folder/Child", "Folder/Child");
            f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [savedRoot, savedChild] });
            var id = await BeginAsync(f, root, savedRoot);
            UnreadableMember.Record(f.Store, f.Pair.PairId, savedChild.LocalPath);
            f.Dropbox.Returns(new FolderSubmitResult.Unknown(TextKey.DropboxRefused));
            await f.Runner.ResumeAsync(f.Store.FolderRemoval(f.Pair.PairId, id), null, default);
            Check.Require(f.Dropbox.SubmitCalls == 0 && !Gone(f.Store, f.Pair.PairId, savedRoot) &&
                !Gone(f.Store, f.Pair.PairId, savedChild),
                $"After freeze: cross={crossLinked}, partial={partial}, stateOnly={stateOnly}, submits={f.Dropbox.SubmitCalls}.");
            Check.Require(f.Store.Issues(f.Pair.PairId).Any(issue => issue.Kind == IssueKind.Failure),
                "The refused durable folder effect remains a visible failure.");
            UnreadableMember.Release(f.Store, f.Pair.PairId, savedChild.LocalPath);
            if (!f.Store.FolderRemoval(f.Pair.PairId, id).Active) id = await BeginAsync(f, root, savedRoot);
            await f.Runner.ResumeAsync(f.Store.FolderRemoval(f.Pair.PairId, id), null, default);
            Check.Require(stateOnly && partial ? Gone(f.Store, f.Pair.PairId, savedChild) : f.Dropbox.SubmitCalls == 1,
                "Authorized release permits the ordinary send or state-only settlement.");
        }
        using (var f = new Fixture(folder, nameof(ProtectionAfterFreezeAsync)))
        {
            var root = await f.DropboxEntries.EnsureFolderAsync("Folder", default);
            var child = await f.DropboxEntries.EnsureFolderAsync("Folder/Child", default);
            await f.DropboxEntries.EnsureFolderAsync("Folder/Keep", default);
            var savedRoot = Saved(root.Path, root.Path);
            f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [savedRoot, Saved(child.Path, child.Path)] });
            var id = await BeginAsync(f, root, savedRoot);
            UnreadableMember.Record(f.Store, f.Pair.PairId, "Folder/Keep");
            f.Dropbox.Returns(new FolderSubmitResult.Unknown(TextKey.DropboxRefused));
            await f.Runner.ResumeAsync(f.Store.FolderRemoval(f.Pair.PairId, id), null, default);
            Check.Require(f.Dropbox.SubmitCalls == 1 && f.Dropbox.Submitted.Single().Single().Expected!.Path == child.Path,
                "A protected surviving sibling outside a partial selection does not block its selected effect.");
        }
        using (var f = new Fixture(folder, nameof(ProtectionAfterFreezeAsync)))
        {
            var root = await f.DropboxEntries.EnsureFolderAsync("Folder", default);
            var savedRoot = Saved(root.Path, root.Path);
            f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [savedRoot] });
            var id = await BeginAsync(f, root, savedRoot);
            var effects = f.Store.ReadyFolderEffects(f.Pair.PairId, id, -1, PageSize).ToArray();
            var submissionId = Accepted(f, effects);
            var submission = f.Store.FolderSubmission(submissionId);
            f.Store.AdvanceFolderSubmission(submission, submission with
            {
                Generation = submission.Generation + 1, Phase = FolderSubmission.SubmissionPhase.Terminal
            }, [new FolderMemberResult.Receipt(0, root)]);
            var member = Item(f, id, 0);
            f.Store.SettleFolderRemovalItem(f.Pair.PairId, id, member.Ordinal, member.Generation, member with
            {
                Generation = member.Generation + 1, EffectGeneration = member.EffectGeneration + 1,
                EffectState = FolderRemovalItem.EffectStatus.Settled, Phase = FolderRemovalItem.RecoveryPhase.Settled
            }, StateChange.Empty);
            var operation = f.Store.FolderRemoval(f.Pair.PairId, id);
            f.Store.AdvanceFolderRemoval(f.Pair.PairId, id, operation.Generation, operation.Phase, operation with
            {
                Generation = operation.Generation + 1, Phase = FolderRemovalPhase.Checking, Cursor = null
            });
            UnreadableMember.Record(f.Store, f.Pair.PairId, root.Path);
            await f.Runner.ResumeAsync(f.Store.FolderRemoval(f.Pair.PairId, id), null, default);
            Check.Require(!Gone(f.Store, f.Pair.PairId, savedRoot) && f.Dropbox.SubmitCalls == 0 &&
                f.Store.FolderRemoval(f.Pair.PairId, id).Phase == FolderRemovalPhase.Retained,
                "New protection prevents final root settlement after its receipts have already settled.");
            UnreadableMember.Release(f.Store, f.Pair.PairId, root.Path);
            await f.Runner.ResumeAsync(f.Store.FolderRemoval(f.Pair.PairId, id), null, default);
            Check.Require(Gone(f.Store, f.Pair.PairId, savedRoot) && f.Dropbox.SubmitCalls == 0,
                "Root settlement continues after release without replaying any sent effect.");
        }
    }

    // A provider failure carried by an inventory read keeps its wrapped cause at the recovery catch.
    private static async Task WrappedRecoveryCauseAsync(string folder)
    {
        var (f, _, member, id) = await BesideFileAsync(folder, nameof(WrappedRecoveryCauseAsync),
            new StagingRecovery(folder),
            root => new FolderSubmitResult.Terminal([new FolderMemberResult.Receipt(0, root)]));
        using (f)
        {
            var live = member.Expected!;
            Move(f, member, member with { Phase = FolderRemovalItem.RecoveryPhase.RecoverySelected, Selected = live });
            f.DropboxEntries.FailNext(live.Path, new InventoryIncompleteException(nameof(WrappedRecoveryCauseAsync),
                new ItemFailedException(live.Path, TextKey.ReadDropboxFailed, null)));
            await f.Runner.ResumeAsync(f.Store.FolderRemoval(f.Pair.PairId, id), null, CancellationToken.None);
            Check.Require(f.Store.Issues(f.Pair.PairId).Any(issue => issue.Kind == IssueKind.Failure &&
                    issue.Path == live.Path && issue.Cause == TextKey.ReadDropboxFailed),
                "A wrapped typed provider failure keeps its cause through the recovery catch.");
        }
    }

    /// <summary>A recovery double that hands out one allocation so the select step reaches its provider read.</summary>
    private sealed class StagingRecovery(string folder) : ILocalRecovery
    {
        public Task<RemovalResult> ResolvePendingAsync(PendingRead pending, RecoveryChoice choice, CancellationToken ct) =>
            throw new NotSupportedException();
        public IReadOnlyList<(string Path, Exception Error)> RemoveStaleStages(IReadOnlySet<string> names) => [];
        public DriveEvidence Drive() => new(null, null, null);
        public bool CanArmReplace() => throw new NotSupportedException();
        public Task<RemovalProof> ProveRemovalAsync(Entry expected, string? hash, CancellationToken ct) =>
            throw new NotSupportedException();
        public StageAllocation PlanRecoveryStage(string operationId, long ordinal) =>
            new(operationId, ordinal, "allocation", Path.Combine(folder, "stage"));
        public Task<IRecoveryStage> StageRecoveryAsync(StageAllocation allocation, Entry selected, Stream content,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<ILocalRecovery.AllocationOccupancy> InspectRecoveryAllocationAsync(StageAllocation allocation,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> OpenRecoveryStageAsync(StageProof proof, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task DiscardRecoveryStageAsync(StageProof proof, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    // EX-07/A137: the batch owner splits a ready page above the provider bound instead of letting it be refused.
    // S-12: every planned root continues its unfinished operation from one active-set read, not one scan per root.
    private static async Task UnfinishedRootReadsDoNotScaleAsync(string folder)
    {
        using var f = new Fixture(folder, nameof(UnfinishedRootReadsDoNotScaleAsync));
        var roots = new List<(Entry Root, SyncedEntry Saved, string OperationId)>();
        for (var index = 0; index < 3; index++)
        {
            var root = await f.DropboxEntries.EnsureFolderAsync($"Folder{index}", default);
            var saved = Saved(root.Path, root.Path);
            f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [saved] });
            roots.Add((root, saved, await BeginAsync(f, root, saved)));
        }
        var counting = new CountingRootReads(f.Store);
        var jobs = SyncJobs.Create(f.Context with { Store = counting }, new SyncPorts(new PassthroughBoundary(),
            RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
        foreach (var (root, saved, operationId) in roots)
        {
            var found = await jobs.Items.Folders.PrepareFolderRemovalAsync(Row(root, saved), Side.Dropbox,
                new HashSet<string>(), new HashSet<string>(), CancellationToken.None);
            Check.Require(found == operationId, "A planned root continues its own unfinished operation.");
        }
        Check.Require(counting.ActiveRootReads == 1,
            $"Three planned roots cost one active-set read, not one per root ({counting.ActiveRootReads}).");
    }

    private sealed class CountingRootReads(IStateStore inner) : ForwardingStateStore(inner)
    {
        public int ActiveRootReads { get; private set; }

        public override IEnumerable<FolderRemovalState> ActiveFolderRemovals(string pairId, CancellationToken ct)
        {
            ActiveRootReads++;
            return base.ActiveFolderRemovals(pairId, ct);
        }
    }

    // R3-02/HI-03: the real streamed pipeline batches at the bound; a per-page flush would change these counts.
    private static async Task PagePipelineBatchesAsync(string folder)
    {
        var bound = FolderDeleteRunner.MaxEffectsPerSubmission;
        await AssertPipelineAsync(folder, "single", [1_800],
            [bound, 800], [new Dictionary<int, int> { [0] = bound }, new Dictionary<int, int> { [0] = 800 }]);
        await AssertPipelineAsync(folder, "cross", [800, 450], [bound, 250],
            [new Dictionary<int, int> { [0] = 800, [1] = 200 }, new Dictionary<int, int> { [1] = 250 }]);
    }

    private static async Task AssertPipelineAsync(string folder, string label, int[] files, int[] expectedCounts,
        Dictionary<int, int>[] expectedByOperation)
    {
        using var f = new Fixture(folder, "pipeline-" + label);
        var ids = new List<string>();
        for (var index = 0; index < files.Length; index++) ids.Add(await PlanFilesAsync(f, $"P{index}", files[index]));
        var ready = ids.SelectMany(id => f.Store.ReadyFolderEffects(f.Pair.PairId, id, -1, int.MaxValue)).ToArray();
        Check.Require(ready.Length == files.Sum(), $"{label}: every removed child is a ready effect ({ready.Length}).");
        var bound = FolderDeleteRunner.MaxEffectsPerSubmission;
        for (var index = 0; index < 8; index++) f.Dropbox.Returns(new FolderSubmitResult.Terminal([]));
        var context = f.Context with { Settings = f.Context.Settings with { StoragePageSize = 600 } };
        var folders = SyncJobs.Create(context, new SyncPorts(new PassthroughBoundary(),
            RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0)).Items.Folders;
        await folders.RunPreparedFolderRemovalsAsync(ids, CancellationToken.None);
        var batches = f.Dropbox.Submitted;
        Check.Require(batches.Select(batch => batch.Count).SequenceEqual(expectedCounts),
            $"{label}: the pipeline flushes {string.Join("+", expectedCounts)} ({string.Join("+", batches.Select(b => b.Count))}).");
        Check.Require(batches.All(batch => batch.Count <= bound), $"{label}: every batch is within the bound.");
        var sent = batches.SelectMany(batch => batch).Select(effect => (effect.OperationId, effect.ItemOrdinal)).ToArray();
        Check.Require(sent.Length == ready.Length && sent.Distinct().Count() == sent.Length &&
            ready.Select(effect => (effect.OperationId, effect.ItemOrdinal)).OrderBy(pair => pair)
                .SequenceEqual(sent.OrderBy(pair => pair)),
            $"{label}: every effect is submitted exactly once.");
        for (var index = 0; index < expectedByOperation.Length; index++)
        {
            var counts = batches[index].GroupBy(effect => effect.OperationId)
                .ToDictionary(group => group.Key, group => group.Count());
            for (var operation = 0; operation < ids.Count; operation++)
                Check.Require(counts.GetValueOrDefault(ids[operation]) ==
                        (expectedByOperation[index].TryGetValue(operation, out var value) ? value : 0),
                    $"{label}: batch {index} carries {expectedByOperation[index].GetValueOrDefault(operation)} from operation {operation}.");
        }
    }

    private static async Task<string> PlanFilesAsync(Fixture f, string rootPath, int files)
    {
        var root = await f.DropboxEntries.EnsureFolderAsync(rootPath, default);
        await f.DropboxEntries.EnsureFolderAsync(PathNames.Child(rootPath, nameof(FolderVerdict.Survives)), default);
        var savedRoot = Saved(root.Path, root.Path);
        var records = new List<SyncedEntry> { savedRoot };
        var hash = new string('a', 64);
        for (var index = 0; index < files; index++)
        {
            var path = PathNames.Child(rootPath, $"F{index}");
            f.DropboxEntries.Seed(new Entry(path, false, 1, default, nameof(Entry), nameof(Side.Dropbox), hash, null), [1]);
            records.Add(Saved(path, path) with { IsFolder = false, DropboxContentHash = hash });
        }
        f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = records });
        return await BeginAsync(f, root, savedRoot, 600);
    }

    private static async Task BatchBoundSplitsAsync(string folder)
    {
        using var f = new Fixture(folder, nameof(BatchBoundSplitsAsync));
        var root = await f.DropboxEntries.EnsureFolderAsync("Folder", default);
        await f.DropboxEntries.EnsureFolderAsync(PathNames.Child(root.Path, nameof(FolderVerdict.Survives)), default);
        var savedRoot = Saved(root.Path, root.Path);
        var records = new List<SyncedEntry> { savedRoot };
        var bound = FolderDeleteRunner.MaxEffectsPerSubmission;
        for (var index = 0; index <= bound; index++)
        {
            var child = await f.DropboxEntries.EnsureFolderAsync($"Folder/C{index}", default);
            records.Add(Saved(child.Path, child.Path));
        }
        f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = records });
        var id = await BeginAsync(f, root, savedRoot);
        var effects = f.Store.ReadyFolderEffects(f.Pair.PairId, id, -1, int.MaxValue).ToArray();
        Check.Require(effects.Length == bound + 1, $"The frozen plan holds one effect above the bound ({effects.Length}).");
        f.Dropbox.Returns(new FolderSubmitResult.Terminal([]));
        f.Dropbox.Returns(new FolderSubmitResult.Terminal([]));
        await f.Runner.RunGroupAsync(effects, default);
        Check.Require(f.Dropbox.Submitted.Count == 2 &&
            f.Dropbox.Submitted.All(batch => batch.Count <= bound) &&
            f.Dropbox.Submitted.Sum(batch => batch.Count) == effects.Length &&
            f.Store.FolderRemovalItems(f.Pair.PairId, id, -1, int.MaxValue)
                .Count(item => item.SubmissionId is not null) == effects.Length,
            "A page above the provider bound is split into provider-legal submissions and no member is lost.");
    }

    private static async Task LocalProofRefusalAsync(string folder)
    {
        foreach (var (unknownSaved, liveEpisode) in new[] { (false, false), (true, false), (false, true) })
        {
            var attempt = Guid.NewGuid();
            var recovery = new FakeRecovery { Evidence = new DriveEvidence(1, null, null) };
            using var f = new Fixture(folder, nameof(LocalProofRefusalAsync), recovery,
                localEvidence: liveEpisode, registry: liveEpisode ? (id, _, _, _, _) => id == attempt : null);
            var root = await f.Local.EnsureFolderAsync("Folder", default);
            var child = new Entry("Folder/Child", false, 1, DateTimeOffset.UnixEpoch, "child", "", "", null);
            f.LocalEntries.Seed(child, [1]);
            var kept = await f.Local.EnsureFolderAsync("Folder/Kept", default);
            var savedRoot = Saved(root.Path, root.Path) with { LocalId = root.Id };
            if (unknownSaved)
                f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record =
                    [SyncedEntries.From(child, child) with { DropboxContentHash = "" }] });
            var hashCalls = 0;
            recovery.Hash = (_, _) =>
            {
                hashCalls++;
                return Task.FromException<string>(unknownSaved
                    ? new InvalidOperationException("An unknown saved hash must not adopt current bytes.")
                    : new ItemFailedException(child.Path, TextKey.FailLocalInUse, null));
            };
            var row = new Row(PathNames.Key(root.Path), root, null, savedRoot, [], null,
                new RowContext(false, false, false, null, false, false, false, false));
            using var plan = f.Store.OpenFolderPlan(f.Pair.PairId, row, Side.Local);
            await plan.CaptureAsync(f.Local.ReadAllAsync(root.Path, new ListOptions(false, false), default), default);
            foreach (var entry in plan.Children(default))
                plan.MarkClassified(entry.Key, entry.NativeKey, new FolderChild(entry.Entry, entry.Synced,
                    entry.Entry?.Path == child.Path ? FolderVerdict.Removed : FolderVerdict.Survives), false, default);
            plan.PrepareBranches(default);
            foreach (var candidate in plan.RemovedChildren(default)) plan.SelectBranch(candidate.Id, default);
            plan.Complete(false, default);
            var id = f.Store.BeginFolderRemoval(f.Pair.PairId, plan, StateSchema.OneRow, default);
            var effects = f.Store.ReadyFolderEffects(f.Pair.PairId, id, -1, PageSize).ToArray();
            Check.Require(effects.Length == 1 && effects[0].Kind == FolderEffectKind.ConditionalFile,
                "The refusal fixture reaches a frozen conditional file.");
            if (liveEpisode) SourceFixture.AcquireJournal(f.Store, f.Pair);
            var sourceEpoch = liveEpisode ? f.Store.ReadSource(f.Pair.PairId, Side.Local)!.Value.Epoch : 0;
            var episode = liveEpisode ? f.Store.RecordInUseFailure(f.Pair.PairId, 0, sourceEpoch, child.Path,
                new IssueSubject([new IssueItem(Side.Local, child.Path, child.Id)], null), TextKey.FailLocalInUse,
                null, ItemRetryDispatch.OrdinaryItem, null, null, attempt) : null;
            ISet<string>? absent = null;
            if (liveEpisode)
            {
                absent = await f.Runner.RunPreparedAsync([effects], default);
                Check.Require(absent.Contains(id), "B222 AJ-E: own-path opposite absence was freshly proven");
            }
            else await f.Runner.RunGroupAsync(effects, default);
            await f.Runner.ResumeAsync(f.Store.FolderRemoval(f.Pair.PairId, id), null, default, absent);
            if (episode is not null) Check.Require(
                SameStoredToken(f.Store.ReadItemRetryState(f.Pair.PairId, [episode.Token.IssueId])[episode.Token.IssueId]?.Token, episode.Token) &&
                f.Store.Issues(f.Pair.PairId).Any(issue => issue.Id == episode.Token.IssueId) &&
                f.RawStore.SelectWork(f.Pair.PairId, 0, f.Context.Scope) is null,
                "B222 AJ-E: KnownNoEffect keeps the episode, notice and suppressed work");
            var member = Item(f, id, effects[0].ItemOrdinal);
            Console.WriteLine($"B222 AJ proof: saved={unknownSaved}; episode={liveEpisode}; hashes={hashCalls}; " +
                $"events={string.Join(',', f.Events.Select(e => e.Kind))}; effect={member.EffectResult}; " +
                $"stream={Check.IsSettlementStream(f.Events, f.Store.Issues(f.Pair.PairId).Single())}");
            Check.Require(hashCalls == (unknownSaved ? 0 : 1) &&
                Check.IsSettlementStream(f.Events, f.Store.Issues(f.Pair.PairId).Single(), isNew: !liveEpisode) &&
                member.EffectResult is FolderMemberResult.KnownNoEffect receipt &&
                receipt.Cause == (unknownSaved ? TextKey.FailChanged : TextKey.FailLocalInUse),
                "AJ-01 never hashes an unknown saved binding; AJ-03 retains the missing-binding read refusal cause.");
            Check.Require(f.Local.Deletes == 0 && await f.Local.ReadOneAsync(child.Path, default) == child &&
                member.EffectResult is FolderMemberResult.KnownNoEffect &&
                f.Store.FolderRemoval(f.Pair.PairId, id).Phase != FolderRemovalPhase.Unknown &&
                f.Store.Issues(f.Pair.PairId).Any(issue => issue.Kind == IssueKind.Failure),
                $"AJ-01/AJ-03: refused proof keeps the file, records known no-effect and Failure, with zero removal calls: saved={unknownSaved}, deletes={f.Local.Deletes}, effect={member.EffectResult}, phase={f.Store.FolderRemoval(f.Pair.PairId, id).Phase}, issues={string.Join(',', f.Store.Issues(f.Pair.PairId).Select(issue => issue.Kind))}.");
        }
    }

    /// <summary>B-164: content that arrives in a Local folder while it is being deleted is read back from the shell's
    /// receipt, kept as an unexpected survivor with a durable warning, and never reported as a clean deletion.</summary>
    public static void RunLateLocalChild()
    {
        var folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(folder);
        try { LateLocalChildAsync(folder).GetAwaiter().GetResult(); }
        finally { Directory.Delete(folder, true); }
    }

    private static async Task LateLocalChildAsync(string folder)
    {
        await LateNewFileAsync(folder, empty: false);
        await LateNewFileAsync(folder, empty: true);
        await EditedFrozenFileAsync(folder);
        await CleanReceiptAsync(folder, empty: false);
        await CleanReceiptAsync(folder, empty: true);
        foreach (var call in new[] { 1, 3, 4 }) await InterruptedInspectionAsync(folder, call);
        foreach (var late in new[] { false, true }) await InterruptedAfterTerminalAsync(folder, late);
        await UncheckedReceiptAsync(folder);
        await PermitReleasedAsync(folder);
        await DropboxPermitHeldAsync(folder);
        await GroupPermitReleasedAsync(folder);
        foreach (var size in new[] { 1, 64 }) await InspectionCountsAsync(folder, size);
    }

    // A synced Local folder whose Dropbox side is already gone, frozen as one whole recursive effect.
    private static async Task<(string Id, Entry Root, SyncedEntry SavedRoot)> LocalOperationAsync(Fixture f,
        params string[] children)
    {
        var root = await f.Local.EnsureFolderAsync("Folder", default);
        var savedRoot = Saved(root.Path, root.Path) with { LocalId = root.Id };
        var records = new List<SyncedEntry> { savedRoot };
        foreach (var path in children)
        {
            var child = new Entry(path, false, 1, DateTimeOffset.UnixEpoch, "id-" + path, "", "", null);
            f.LocalEntries.Seed(child, [1]);
            records.Add(SyncedEntries.From(child, child));
        }
        f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = records });
        var id = await BeginAsync(f, root, savedRoot, deletingSide: Side.Local);
        var effects = f.Store.ReadyFolderEffects(f.Pair.PairId, id, -1, PageSize).ToArray();
        Check.Require(effects is [{ ItemOrdinal: 0, Kind: FolderEffectKind.RecursiveFolder }],
            "B-164 fixture: a clean Local folder freezes one whole recursive effect");
        return (id, root, savedRoot);
    }

    private static FolderRemovalEffect[] Effects(Fixture f, string id) =>
        f.Store.ReadyFolderEffects(f.Pair.PairId, id, -1, PageSize).ToArray();

    private static Entry LateFile(string path) => new(path, false, 2, DateTimeOffset.UnixEpoch, "late", "", "", null);

    private static Issue? LateWarning(Fixture f, string path) => f.Store.Issues(f.Pair.PairId).SingleOrDefault(issue =>
        issue is { Kind: IssueKind.Failure, Cause: TextKey.FailRecycledLate } && PathNames.Key(issue.Path) == PathNames.Key(path));

    private static int DeletedLocal(Fixture f, string path) => f.Events.Count(value => value.Kind == RunEventKind.Effect &&
        value.EffectKind == EffectKind.DeletedLocal && value.Path == path);

    private static bool UnexpectedSurvivor(Fixture f, string id, string path) =>
        f.Store.FolderRemovalItems(f.Pair.PairId, id, -1, int.MaxValue).Any(item =>
            item is { Provenance: FolderRemovalItem.ProvenanceKind.Unexpected, Verdict: FolderVerdict.Survives } &&
            PathNames.Key(item.Path) == PathNames.Key(path));

    // T1 (late new file) and T2b (empty frozen folder): the late arrival is an Unexpected survivor with a warning that
    // names the late Local item; no deleted event; the root stays incomplete with its binding; the receipt settles.
    private static async Task LateNewFileAsync(string folder, bool empty)
    {
        using var f = new Fixture(folder, nameof(LateNewFileAsync) + empty, localEvidence: true);
        var (id, root, savedRoot) = await LocalOperationAsync(f, empty ? Array.Empty<string>() : new[] { "Folder/A" });
        var late = LateFile("Folder/Late");
        f.Local.OnDelete = () => f.LocalEntries.Seed(late, [2, 2]);
        await f.Runner.RunGroupAsync(Effects(f, id), default);
        await f.Runner.ResumeAsync(f.Store.FolderRemoval(f.Pair.PairId, id), null, default);
        var name = empty ? "T2b" : "T1";
        Check.Require(UnexpectedSurvivor(f, id, late.Path),
            name + "(a) the late arrival is an Unexpected member with verdict Survives");
        Check.Require(LateWarning(f, late.Path) is { } warning &&
            warning.Subject.Items.Any(item => item.Side == Side.Local && item.Id == late.Id),
            name + "(b) a FailRecycledLate Failure names the late Local item");
        Check.Require(DeletedLocal(f, root.Path) == 0, name + "(c) no DeletedLocal event for the folder");
        Check.Require(f.Store.FolderRemoval(f.Pair.PairId, id).Phase != FolderRemovalPhase.Complete &&
            f.Store.SyncedByKey(f.Pair.PairId, Side.Local, savedRoot.LocalKey) is not null,
            name + "(d) the root is not completed and its saved binding is kept");
        if (!empty)
            Check.Require(Item(f, id, 0).EffectState == FolderRemovalItem.EffectStatus.Settled,
                "T1(e) the frozen receipt settles");
    }

    // T2: an edited frozen file is a late change; its warning survives settlement and a restart.
    private static async Task EditedFrozenFileAsync(string folder)
    {
        using var f = new Fixture(folder, nameof(EditedFrozenFileAsync), localEvidence: true);
        var (id, _, _) = await LocalOperationAsync(f, "Folder/A");
        var edited = (await f.LocalEntries.ReadOneAsync("Folder/A", default))! with { Size = 2 };
        f.Local.OnDelete = () => f.LocalEntries.Seed(edited, [1, 2]);
        await f.Runner.RunGroupAsync(Effects(f, id), default);
        Check.Require(UnexpectedSurvivor(f, id, edited.Path) && LateWarning(f, edited.Path) is not null,
            "T2 an edited frozen file is an Unexpected member with its own warning");
        await f.Runner.ResumeAsync(f.Store.FolderRemoval(f.Pair.PairId, id), null, default);
        Check.Require(LateWarning(f, edited.Path) is not null, "T2 the warning survives settlement");
        await f.NewRunner().ResumeAsync(f.Store.FolderRemoval(f.Pair.PairId, id), null, default);
        Check.Require(LateWarning(f, edited.Path) is not null, "T2 the warning survives a restart");
    }

    // T3: a clean receipt (with and without children) records nothing, raises nothing, and reports exactly one
    // deletion, emitted only after the submission is durably Terminal.
    private static async Task CleanReceiptAsync(string folder, bool empty)
    {
        using var f = new Fixture(folder, nameof(CleanReceiptAsync) + empty, localEvidence: true);
        var (id, root, _) = await LocalOperationAsync(f, empty ? Array.Empty<string>() : new[] { "Folder/A" });
        var terminalAtEvent = new List<bool>();
        f.OnEvent = value =>
        {
            if (value is { Kind: RunEventKind.Effect, EffectKind: EffectKind.DeletedLocal } && value.Path == root.Path)
                terminalAtEvent.Add(f.Store.FolderSubmission(Item(f, id, 0).SubmissionId!).Phase ==
                    FolderSubmission.SubmissionPhase.Terminal);
        };
        await f.Runner.RunGroupAsync(Effects(f, id), default);
        await f.Runner.ResumeAsync(f.Store.FolderRemoval(f.Pair.PairId, id), null, default);
        var name = "T3" + (empty ? " empty" : "");
        Check.Require(!f.Store.FolderRemovalItems(f.Pair.PairId, id, -1, int.MaxValue)
                .Any(item => item.Provenance == FolderRemovalItem.ProvenanceKind.Unexpected) &&
            f.Store.Issues(f.Pair.PairId).Count == 0, name + " a clean receipt records no member and raises no issue");
        Check.Require(terminalAtEvent is [true] && DeletedLocal(f, root.Path) == 1,
            name + " exactly one DeletedLocal, observed after the submission is Terminal: " + string.Join(",", terminalAtEvent));
        Check.Require(f.Store.FolderRemoval(f.Pair.PairId, id).Phase == FolderRemovalPhase.Complete,
            name + " the root completes");
    }

    // Throws on the inner enumeration's call-th MoveNextAsync; the call after the last entry is the one returning false.
    private static Func<IAsyncEnumerable<Listed>, IAsyncEnumerable<Listed>> ThrowAt(int call, Exception error) =>
        recycled => Throwing(recycled, call, error);

    private static async IAsyncEnumerable<Listed> Throwing(IAsyncEnumerable<Listed> inner, int call, Exception error)
    {
        var calls = 0;
        await foreach (var listed in inner)
        {
            if (++calls == call) throw error;
            yield return listed;
        }
        if (++calls == call) throw error;
    }

    // T4 (i)-(iii): an interruption before Terminal leaves the submission armed; a fresh runner makes it Unknown with a
    // visible root Failure, never resends, never reports a deletion, and a warning already raised persists.
    // The receipt holds Folder/A, Folder/Late, Folder/Z: call 1 is the first, 3 follows the late entry, 4 is the last.
    private static async Task InterruptedInspectionAsync(string folder, int call)
    {
        using var f = new Fixture(folder, nameof(InterruptedInspectionAsync) + call, localEvidence: true);
        var (id, root, _) = await LocalOperationAsync(f, "Folder/A", "Folder/Z");
        var late = LateFile("Folder/Late");
        f.Local.OnDelete = () => f.LocalEntries.Seed(late, [2, 2]);
        f.Local.WrapRecycled = ThrowAt(call, new OperationCanceledException());
        var name = "T4(" + (call == 1 ? "i" : call == 3 ? "ii" : "iii") + ")";
        Exception? caught = null;
        try { await f.Runner.RunGroupAsync(Effects(f, id), default); }
        catch (OperationCanceledException error) { caught = error; }
        Check.Require(caught is not null && f.Store.FolderSubmission(Item(f, id, 0).SubmissionId!).Phase ==
            FolderSubmission.SubmissionPhase.Armed, name + " the interruption leaves the submission armed");
        f.Local.WrapRecycled = null;
        await f.NewRunner().ResumeAsync(f.Store.FolderRemoval(f.Pair.PairId, id), null, default);
        var operation = f.Store.FolderRemoval(f.Pair.PairId, id);
        Check.Require(f.Local.Deletes == 1 && f.Store.FolderSubmission(Item(f, id, 0).SubmissionId!).Phase ==
            FolderSubmission.SubmissionPhase.Unknown, name + " a fresh runner makes it Unknown without a resend");
        Check.Require(operation.Phase is not (FolderRemovalPhase.Complete or FolderRemovalPhase.Submitted) &&
            f.Store.Issues(f.Pair.PairId).Any(issue => issue.Kind == IssueKind.Failure &&
                PathNames.Key(issue.Path) == PathNames.Key(root.Path)) && DeletedLocal(f, root.Path) == 0,
            name + " the root is not completed, has a visible Failure and no deletion report: " + operation.Phase);
        if (call != 1)
            Check.Require(LateWarning(f, late.Path) is not null, name + " the raised warning persists");
    }

    private static async IAsyncEnumerable<Listed> CancelAtEnd(IAsyncEnumerable<Listed> inner, CancellationTokenSource cancel)
    {
        await foreach (var listed in inner) yield return listed;
        cancel.Cancel();
    }

    // T4 (iv): the interruption after Terminal (the runner's own cancellation check) keeps the durable receipt.
    private static async Task InterruptedAfterTerminalAsync(string folder, bool late)
    {
        using var f = new Fixture(folder, nameof(InterruptedAfterTerminalAsync) + late, localEvidence: true);
        var (id, root, _) = await LocalOperationAsync(f, "Folder/A");
        var arrival = LateFile("Folder/Late");
        if (late) f.Local.OnDelete = () => f.LocalEntries.Seed(arrival, [2, 2]);
        using var cancel = new CancellationTokenSource();
        f.Local.WrapRecycled = recycled => CancelAtEnd(recycled, cancel);
        var name = "T4(iv) " + (late ? "late" : "clean");
        Exception? caught = null;
        try { await f.Runner.RunGroupAsync(Effects(f, id), cancel.Token); }
        catch (OperationCanceledException error) { caught = error; }
        Check.Require(caught is not null && f.Store.FolderSubmission(Item(f, id, 0).SubmissionId!).Phase ==
            FolderSubmission.SubmissionPhase.Terminal, name + " the interruption follows the durable Terminal receipt");
        Check.Require(DeletedLocal(f, root.Path) == (late ? 0 : 1), name + " a deletion is reported only for a clean receipt");
        f.Local.WrapRecycled = null;
        await f.NewRunner().ResumeAsync(f.Store.FolderRemoval(f.Pair.PairId, id), null, default);
        var operation = f.Store.FolderRemoval(f.Pair.PairId, id);
        Check.Require(f.Local.Deletes == 1, name + " no resend");
        if (!late)
            Check.Require(operation.Phase == FolderRemovalPhase.Complete, name + " a fresh resume completes the root");
        else
            Check.Require(LateWarning(f, arrival.Path) is not null && operation.Phase != FolderRemovalPhase.Complete &&
                (!operation.Active || operation.Phase == FolderRemovalPhase.Retained),
                name + " the warning persists and the operation retires or is retained: " + operation.Phase);
    }

    // T5: a receipt that cannot be checked is Unknown with RecycleUnchecked, never a deletion.
    private static async Task UncheckedReceiptAsync(string folder)
    {
        using var f = new Fixture(folder, nameof(UncheckedReceiptAsync), localEvidence: true);
        var (id, root, _) = await LocalOperationAsync(f, "Folder/A");
        f.Local.WrapRecycled = ThrowAt(1, new ChangedUnderneathException(root.Path));
        await f.Runner.RunGroupAsync(Effects(f, id), default);
        await f.Runner.ResumeAsync(f.Store.FolderRemoval(f.Pair.PairId, id), null, default);
        Check.Require(Item(f, id, 0) is { EffectState: FolderRemovalItem.EffectStatus.Unknown,
                EffectResult: FolderMemberResult.Unknown { Cause: TextKey.RecycleUnchecked } } &&
            DeletedLocal(f, root.Path) == 0 && f.Store.FolderRemoval(f.Pair.PairId, id).Phase != FolderRemovalPhase.Complete,
            "T5 an unchecked receipt is Unknown with RecycleUnchecked and no deletion");
    }

    // T7: a Local operation runs its shell effect and receipt check without the account permit.
    private static async Task PermitReleasedAsync(string folder)
    {
        using var f = new Fixture(folder, nameof(PermitReleasedAsync), localEvidence: true);
        var (id, _, _) = await LocalOperationAsync(f, "Folder/A");
        var released = new List<bool>();
        f.Local.OnDelete = () => released.Add(f.Boundary.Last!.Released);
        await f.Runner.RunGroupAsync(Effects(f, id), default);
        Check.Require(released is [true], "T7 the account permit is released before the Local shell effect");
    }

    // T7 Dropbox variant: a Dropbox submission still holds the permit when it is sent.
    private static async Task DropboxPermitHeldAsync(string folder)
    {
        using var f = new Fixture(folder, nameof(DropboxPermitHeldAsync));
        var root = await f.DropboxEntries.EnsureFolderAsync("Folder", default);
        var savedRoot = Saved(root.Path, root.Path);
        f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [savedRoot] });
        var id = await BeginAsync(f, root, savedRoot);
        f.Dropbox.Returns(new FolderSubmitResult.Terminal([new FolderMemberResult.Receipt(0, root)]));
        var released = new List<bool>();
        f.Dropbox.OnSubmit = () => released.Add(f.Boundary.Last!.Released);
        await f.Runner.RunGroupAsync(Effects(f, id), default);
        Check.Require(released is [false], "T7 a Dropbox submission holds the account permit when it is sent");
    }

    // T7b: one submission with a recursive folder and a conditional file releases the permit before each effect.
    private static async Task GroupPermitReleasedAsync(string folder)
    {
        var recovery = new FakeRecovery { Evidence = new DriveEvidence(1, null, null) };
        using var f = new Fixture(folder, nameof(GroupPermitReleasedAsync), recovery, localEvidence: true);
        var root = await f.Local.EnsureFolderAsync("Folder", default);
        var branch = await f.Local.EnsureFolderAsync("Folder/Sub", default);
        var savedRoot = Saved(root.Path, root.Path) with { LocalId = root.Id };
        var records = new List<SyncedEntry> { savedRoot, Saved(branch.Path, branch.Path) with { LocalId = branch.Id } };
        foreach (var path in new[] { "Folder/Sub/S", "Folder/F", "Folder/Keep" })
        {
            var child = new Entry(path, false, 1, DateTimeOffset.UnixEpoch, "id-" + path, "", "", null);
            f.LocalEntries.Seed(child, [1]);
            var saved = SyncedEntries.From(child, child) with { DropboxContentHash = DropboxContentHash.Compute(new byte[] { 1 }) };
            // The edited survivor keeps the root open, so the branch and the file are separate effects.
            records.Add(path == "Folder/Keep" ? saved with { LocalSize = 5 } : saved);
        }
        f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = records });
        var id = await BeginAsync(f, root, savedRoot, deletingSide: Side.Local);
        var effects = Effects(f, id);
        Check.Require(effects.Select(effect => effect.Kind).Order().SequenceEqual(
                [FolderEffectKind.RecursiveFolder, FolderEffectKind.ConditionalFile]),
            "T7b fixture: one recursive folder and one conditional file: " + string.Join(",", effects.Select(e => e.Kind)));
        var released = new List<bool>();
        f.Local.OnDelete = () => released.Add(f.Boundary.Last!.Released);
        await f.Runner.RunGroupAsync(effects, default);
        Check.Require(released is [true, true], "T7b the account permit is released before each Local effect: " +
            string.Join(",", released));
    }

    private static async IAsyncEnumerable<Listed> Measured(IAsyncEnumerable<Listed> inner,
        Func<(long Lookups, long Inserts, long Commits)> read, (long Lookups, long Inserts, long Commits)[] at)
    {
        at[0] = read();
        await foreach (var listed in inner) yield return listed;
        at[1] = read();
    }

    // T8 (M2): a clean inspection adds exactly one member lookup per descendant and no write.
    private static async Task InspectionCountsAsync(string folder, int size)
    {
        using var f = new Fixture(folder, nameof(InspectionCountsAsync) + size, localEvidence: true);
        var children = Enumerable.Range(0, size).Select(index => "Folder/F" + index.ToString("D2", CultureInfo.InvariantCulture))
            .ToArray();
        var (id, _, _) = await LocalOperationAsync(f, children);
        var at = new (long Lookups, long Inserts, long Commits)[2];
        (long, long, long) Read() => (f.Store.MemberCalls.GetValueOrDefault(nameof(IStateStore.FolderRemovalItemByKey)),
            f.Store.MemberCalls.GetValueOrDefault(nameof(IStateStore.InsertFolderMember)), f.Store.CommitCalls);
        f.Local.WrapRecycled = recycled => Measured(recycled, Read, at);
        await f.Runner.RunGroupAsync(Effects(f, id), default);
        Check.Require(at[1].Lookups - at[0].Lookups == size && at[1].Inserts == at[0].Inserts &&
            at[1].Commits == at[0].Commits,
            $"T8 the inspection of {size} descendants adds exactly {size} member lookups and no write: " +
            $"lookups={at[1].Lookups - at[0].Lookups} inserts={at[1].Inserts - at[0].Inserts} commits={at[1].Commits - at[0].Commits}");
    }

    /// <summary>G7: while a Local folder's receipt is being checked, the account permit and disjoint paths are free,
    /// so an unrelated upload completes; only the folder's own footprint waits.</summary>
    public static void UnrelatedSyncDuringBinCheck()
    {
        var folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(folder);
        try { UnrelatedSyncDuringBinCheckAsync(folder).GetAwaiter().GetResult(); }
        finally { Directory.Delete(folder, true); }
    }

    private static async Task UnrelatedSyncDuringBinCheckAsync(string folder)
    {
        var network = new FakeNetworkSignal();
        network.SetAvailable(true);
        // This path never backs off; the values only satisfy the limiter's constructor.
        var limiter = new Massdex.SyncShark.Engine.Dropbox.AdaptiveLimiter(1, 1, TimeProvider.System, network, _ => { },
            new Massdex.SyncShark.V2.FailureRetryOwner.BackoffSettings(1, 1, 0, 1));
        using var f = new Fixture(folder, nameof(UnrelatedSyncDuringBinCheck), limiter: limiter, localEvidence: true);
        var (id, root, _) = await LocalOperationAsync(f, "Folder/A");
        var other = await f.Local.EnsureFolderAsync("Other", default);
        await f.Dropbox.EnsureFolderAsync("Other", default);
        f.Store.Commit(f.Pair.PairId, StateChange.Empty with
            { Record = [Saved(other.Path, other.Path) with { LocalId = other.Id }] });
        var upload = new Entry("Other/new.txt", false, 3, DateTimeOffset.UnixEpoch, "id-new", "", "", null);
        f.LocalEntries.Seed(upload, [1, 2, 3]);
        var action = new SyncAction(ActionKind.Upload, new Row(PathNames.Key(upload.Path), upload, null, null, [], null,
            RowContext.Neutral), Target: upload.Path);
        var context = f.Context with { ItemAttempts = new StoreItemAttempts(f.Store, f.Pair.PairId, 0) };
        var transfers = new Transfers(context);
        var uploads = new ActionExecutor(context, transfers, new Parents(context), new KeepBothRunner(context, transfers));
        var order = new List<string>();
        f.Local.WrapRecycled = recycled => DuringInspection(recycled, ProbeAsync, order);
        await f.Runner.RunGroupAsync(Effects(f, id), default);
        var binding = f.Store.SyncedByKey(f.Pair.PairId, Side.Local, PathNames.Key(upload.Path));
        Check.Require(order.SequenceEqual(["upload completed", "inspection resumed"]),
            "G7d the upload completes before the receipt check resumes: " + string.Join(",", order));
        await f.Runner.ResumeAsync(f.Store.FolderRemoval(f.Pair.PairId, id), null, default);
        Check.Require(f.Store.FolderRemoval(f.Pair.PairId, id).Phase == FolderRemovalPhase.Complete &&
            DeletedLocal(f, root.Path) == 1 && binding is not null &&
            f.Store.SyncedByKey(f.Pair.PairId, Side.Local, PathNames.Key(upload.Path)) == binding,
            "G7e the folder completes clean and the upload's binding is unchanged");

        async Task ProbeAsync()
        {
            var probes = new List<(CancellationTokenSource Source, Task<IAsyncDisposable> Task)>();
            var released = new HashSet<Task<IAsyncDisposable>>();
            var leases = new List<IAsyncDisposable>();
            // Each acquired probe result is disposed exactly once: here when the case releases it, or in finally.
            async Task Release(Task<IAsyncDisposable> probe)
            {
                if (probe.IsCompletedSuccessfully && released.Add(probe)) await probe.Result.DisposeAsync();
            }
            try
            {
                var permitSource = new CancellationTokenSource();
                var permit = limiter.AdmitAsync(false, permitSource.Token);
                probes.Add((permitSource, permit));
                Check.Require(permit.IsCompletedSuccessfully, "G7a the bin check holds the account permit");
                await Release(permit);

                var disjointSource = new CancellationTokenSource();
                var disjoint = PathLocks.Shared.EnterAsync(LockKeys.ForPaths(f.Pair, [(Side.Local, upload.Path)])
                    .Select(key => new PathLockScope(key, false)).ToArray(), disjointSource.Token);
                probes.Add((disjointSource, disjoint));
                Check.Require(disjoint.IsCompletedSuccessfully, "G7b a disjoint path is free during the bin check");
                await Release(disjoint);

                var overlapSource = new CancellationTokenSource();
                var overlap = PathLocks.Shared.EnterAsync(LockKeys.ForPaths(f.Pair, [(Side.Local, "Folder/A")])
                    .Select(key => new PathLockScope(key, false)).ToArray(), overlapSource.Token);
                probes.Add((overlapSource, overlap));
                Check.Require(!overlap.IsCompleted, "G7c a path inside the folder waits for its footprint");
                overlapSource.Cancel();
                try { await overlap; }
                catch (OperationCanceledException) { }
                Check.Require(overlap.IsCanceled, "G7c the waiting overlap ends cancelled");

                var lease = await AdmittedExecution.AcquireAsync([new ExecutionRequest(f.Pair.PairId, 0,
                    f.Pair.ConnectionId, limiter, false, LockKeys.ForScopes(f.Pair, LockKeys.ForLiveAction(action, f.Pair, null)))],
                    static () => true, default);
                leases.Add(lease);
                await uploads.ExecuteAsync(action, null, default);
                Check.Require(await f.DropboxEntries.ReadOneAsync(upload.Path, default) is not null &&
                    f.Store.SyncedByKey(f.Pair.PairId, Side.Local, PathNames.Key(upload.Path)) is not null,
                    "G7d the unrelated file uploads and is bound during the bin check");
                order.Add("upload completed");
            }
            finally
            {
                // Every pending probe is cancelled and awaited, and every lease released, whatever failed first.
                foreach (var (source, task) in probes)
                {
                    if (!task.IsCompleted) source.Cancel();
                    try { await task; }
                    catch (OperationCanceledException) { }
                    await Release(task);
                    source.Dispose();
                }
                foreach (var lease in leases) await lease.DisposeAsync();
            }
        }
    }

    private static async IAsyncEnumerable<Listed> DuringInspection(IAsyncEnumerable<Listed> inner, Func<Task> probe,
        List<string> order)
    {
        var probed = false;
        await foreach (var listed in inner)
        {
            yield return listed;
            if (probed) continue;
            probed = true;
            await probe();
            order.Add("inspection resumed");
        }
    }

    /// <summary>Admits each recovery callback through the real admission owner, limiter and shared path locks.</summary>
    private sealed class AdmittingBoundary(PairConfig pair, IAdmission limiter) : IRecoveryExecutionBoundary
    {
        public void RequireCurrent() { }

        public Task<T> ExecuteAsync<T>(FolderRemovalState operation, FolderRemovalItem member,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) =>
            ExecuteAsync([operation], execute, ct);

        public Task<T> ExecuteAsync<T>(FolderRemovalState operation,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) =>
            ExecuteAsync([operation], execute, ct);

        public async Task<T> ExecuteAsync<T>(IReadOnlyList<FolderRemovalState> operations,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct)
        {
            var footprint = operations.SelectMany(operation => LockKeys.ForRecovery(pair, operation, new NoPending()))
                .ToArray();
            await using var lease = await AdmittedExecution.AcquireAsync(
                [new ExecutionRequest(pair.PairId, 0, pair.ConnectionId, limiter, false, footprint)], static () => true, ct);
            return await execute(lease, ct);
        }
    }

    private static async Task<string> BeginAsync(Fixture fixture, Entry root, SyncedEntry savedRoot,
        int pageSize = StateSchema.OneRow, Side deletingSide = Side.Dropbox)
    {
        var row = Row(root, savedRoot, deletingSide);
        // A Local plan reads Local evidence, which a real host only allows after its journal boundary is acquired.
        if (deletingSide == Side.Local) SourceFixture.AcquireJournal(fixture.RawStore, fixture.Pair);
        using var plan = await new FolderPlanBuilder(fixture.Context).BuildAsync(row, deletingSide,
            new HashSet<string>(StringComparer.Ordinal), live: false, CancellationToken.None);
        return fixture.Store.BeginFolderRemoval(fixture.Pair.PairId, plan, pageSize, CancellationToken.None);
    }

    /// <summary>Arms the selected effects and advances their submission to an accepted provider job.</summary>
    private static string Accepted(Fixture fixture, IReadOnlyList<FolderRemovalEffect> effects)
    {
        // A submission is never armed before its operation records the post-window cursor.
        foreach (var id in effects.Select(effect => effect.OperationId).Distinct(StringComparer.Ordinal))
        {
            var operation = fixture.Store.FolderRemoval(fixture.Pair.PairId, id);
            if (operation.Cursor is null)
                fixture.Store.AdvanceFolderRemoval(fixture.Pair.PairId, id, operation.Generation, operation.Phase,
                    operation with { Generation = checked(operation.Generation + 1), Cursor = "cursor" });
        }
        var submissionId = fixture.Store.ArmFolderSubmission(fixture.Pair.PairId, effects);
        var armed = fixture.Store.FolderSubmission(submissionId);
        fixture.Store.AdvanceFolderSubmission(armed, armed with
        {
            Generation = checked(armed.Generation + 1),
            Phase = FolderSubmission.SubmissionPhase.Accepted,
            JobId = nameof(FolderSubmission.JobId)
        }, []);
        return submissionId;
    }

    private static FolderRemovalItem Item(Fixture fixture, string operationId, long ordinal) =>
        fixture.Store.FolderRemovalItems(fixture.Pair.PairId, operationId, -1, int.MaxValue)
            .Single(item => item.Ordinal == ordinal);

    private static Row Row(Entry root, SyncedEntry savedRoot, Side side = Side.Dropbox)
    {
        var context = new RowContext(false, false, false, null, false, false, true, true);
        return side == Side.Local
            ? new(PathNames.Key(root.Path), root, null, savedRoot, [], null, context)
            : new(PathNames.DropboxKey(root.Path), null, root, savedRoot, [], null, context);
    }

    private static SyncedEntry Saved(string localPath, string dropboxPath) => new(
        PathNames.Key(localPath), localPath, PathNames.DropboxKey(dropboxPath), dropboxPath,
        true, nameof(Side.Local), default, default, nameof(Side.Dropbox), "");

    private static bool Gone(IStateStore store, string pairId, SyncedEntry saved) =>
        store.SyncedByKey(pairId, Side.Local, saved.LocalKey) is null &&
        store.SyncedByKey(pairId, Side.Dropbox, saved.DropboxKey) is null;

    private static bool Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (T)
        {
            return true;
        }
    }

    private static async IAsyncEnumerable<Listed> NoChanges()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async Task TerminalDiscardInterruptionAsync(string folder, bool afterDiscard, bool cancellation)
    {
        using var cancel = new CancellationTokenSource();
        var recovery = new InterruptedRecovery(afterDiscard, cancellation, cancel);
        using var f = new Fixture(folder, nameof(TerminalDiscardInterruptionAsync), recovery);
        var root = new Entry("Folder", true, 0, DateTimeOffset.UnixEpoch, "folder-a", "rev-a", "", null);
        using var plan = f.Store.OpenFolderPlan(f.Pair.PairId, Row(root, Saved(root.Path, root.Path)), Side.Dropbox);
        await plan.CaptureAsync(Survivor(), CancellationToken.None);
        foreach (var child in plan.Children(CancellationToken.None))
            plan.MarkClassified(child.Key, child.NativeKey, new FolderChild(child.Entry, child.Synced, FolderVerdict.Survives), false, CancellationToken.None);
        plan.PrepareBranches(CancellationToken.None);
        foreach (var candidate in plan.RemovedChildren(CancellationToken.None)) _ = candidate;
        plan.Complete(false, CancellationToken.None);
        var id = f.Store.BeginFolderRemoval(f.Pair.PairId, plan, StateSchema.OneRow, CancellationToken.None);
        var operation = f.Store.FolderRemoval(f.Pair.PairId, id);
        static async IAsyncEnumerable<Listed> Survivor()
        {
            var entry = new Entry("Folder/Kept", false, 4, DateTimeOffset.UnixEpoch, "kept-a", "rev-a", "hash-a", null);
            yield return new Listed(entry.Path, entry);
            await Task.CompletedTask;
        }
        var selected = new Entry("Folder/Child", false, 4, DateTimeOffset.UnixEpoch, "child-a", "rev-a", "hash-a", null);
        var item = f.Store.InsertFolderMember(f.Pair.PairId, id, operation.Generation,
            FolderRemovalItem.ProvenanceKind.Unexpected, selected.Path, selected, selected);
        var allocated = item with { Generation = item.Generation + 1, Phase = FolderRemovalItem.RecoveryPhase.AllocateArmed,
            StageAllocation = new StageAllocation(id, item.Ordinal, "allocation-a", Path.Combine(folder, "stage-a")) };
        f.Store.SettleFolderRemovalItem(f.Pair.PairId, id, item.Ordinal, item.Generation, allocated, StateChange.Empty);
        var staged = allocated with { Generation = allocated.Generation + 1, Phase = FolderRemovalItem.RecoveryPhase.Staged,
            Stage = new StageProof(allocated.StageAllocation!.Path, "volume-a", "file-a", 4, "hash-a") };
        f.Store.SettleFolderRemovalItem(f.Pair.PairId, id, item.Ordinal, allocated.Generation, staged, StateChange.Empty);
        var armed = staged.BeginTerminalDiscard() with { Generation = staged.Generation + 1 };
        f.Store.SettleFolderRemovalItem(f.Pair.PairId, id, item.Ordinal, staged.Generation, armed, StateChange.Empty);
        try { await f.Runner.ResumeAsync(operation, null, cancel.Token); }
        catch (OperationCanceledException) when (cancellation) { }
        catch (InvalidOperationException error) when (error.Message == nameof(InterruptedRecovery)) { }
        Check.Require(recovery.Calls == 1 && recovery.StageExists == !afterDiscard && Item(f, id, item.Ordinal).IsTerminalDiscard,
            "interrupted discard retains terminal intent at either side of await");
        recovery.Interrupt = false;
        await f.Runner.ResumeAsync(f.Store.FolderRemoval(f.Pair.PairId, id), null, CancellationToken.None);
        var settled = Item(f, id, item.Ordinal);
        Check.Require(settled.Phase == FolderRemovalItem.RecoveryPhase.Settled && settled.Stage is null &&
            settled.StageAllocation is null && settled.Result is null && recovery.Calls == 2,
            "resumed explicit abandonment clears only custody without staging or publishing abandoned bytes");
    }

    private static async Task<(Fixture Fixture, Entry Root, FolderRemovalItem Member, string OperationId)> BesideFileAsync(
        string folder, string name, ILocalRecovery recovery, Func<Entry, FolderSubmitResult> outcome)
    {
        var f = new Fixture(folder, name, recovery);
        var root = await f.DropboxEntries.EnsureFolderAsync("Folder", CancellationToken.None);
        var file = new Entry(PathNames.Child(root.Path, nameof(Entry)), false, 1, default, nameof(Entry),
            nameof(Side.Dropbox), new string('a', 64), null);
        f.DropboxEntries.Seed(file, [1]);
        var savedRoot = Saved(root.Path, root.Path);
        var savedFile = Saved(file.Path, file.Path) with { IsFolder = false, DropboxContentHash = file.ContentHash };
        f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [savedRoot, savedFile] });
        var id = await BeginAsync(f, root, savedRoot);
        f.Dropbox.Returns(outcome(root));
        await f.Runner.RunGroupAsync(f.Store.ReadyFolderEffects(f.Pair.PairId, id, -1, PageSize).ToArray(),
            CancellationToken.None);
        return (f, root, f.Store.FolderRemovalItems(f.Pair.PairId, id, 0, 1).Single(), id);
    }

    private static FolderRemovalItem Move(Fixture f, FolderRemovalItem item, FolderRemovalItem change)
    {
        var next = change with { Generation = item.Generation + 1 };
        f.Store.SettleFolderRemovalItem(f.Pair.PairId, item.OperationId, item.Ordinal, item.Generation, next,
            StateChange.Empty);
        return next;
    }

    /// <summary>A member settled by a user's terminal choice stays settled when a retained cursor replays its abandoned
    /// version: nothing is staged or published, and Storage itself refuses any way out of Settled.</summary>
    private static async Task SettledMemberIsTerminalAsync(string folder)
    {
        using var cancel = new CancellationTokenSource();
        var recovery = new InterruptedRecovery(false, false, cancel) { Interrupt = false };
        var (f, _, member, id) = await BesideFileAsync(folder, nameof(SettledMemberIsTerminalAsync), recovery,
            root => new FolderSubmitResult.Terminal([new FolderMemberResult.Receipt(0, root)]));
        using (f)
        {
            var live = member.Expected! with { Revision = nameof(Side.Local), Size = 2 };
            var selected = Move(f, member, member with { Phase = FolderRemovalItem.RecoveryPhase.RecoverySelected, Selected = live });
            var settled = Move(f, selected, selected with
            {
                Phase = FolderRemovalItem.RecoveryPhase.Settled, Cause = TextKey.RecoveryAbandonIncomingPending
            });
            f.Changes.Add(new Listed(live.Path, live));
            f.Evidence = new DeletionEvidence.DeletedFile(live.Path, "domain", live.Id, live.Revision,
                DateTimeOffset.UnixEpoch, null, live.ContentHash, null);

            await f.Runner.ResumeAsync(f.Store.FolderRemoval(f.Pair.PairId, id), null, CancellationToken.None);

            var after = Item(f, id, member.Ordinal);
            Check.Require(after.Phase == FolderRemovalItem.RecoveryPhase.Settled && after.Stage is null &&
                after.StageAllocation is null && after.Result is null && recovery.Calls == 0,
                "a replayed cursor never reselects a settled member");
            Check.Require(Throws<InvalidOperationException>(() => Move(f, after, after with
            {
                Phase = FolderRemovalItem.RecoveryPhase.RecoverySelected, Selected = live, Cause = null
            })), "Storage refuses any transition out of Settled");
        }
    }

    /// <summary>A user's terminal discard finishes automatically even while another member's effect is Unknown.</summary>
    private static async Task TerminalDiscardBesideUnknownAsync(string folder)
    {
        using var cancel = new CancellationTokenSource();
        var recovery = new InterruptedRecovery(false, false, cancel) { Interrupt = false };
        var (f, _, member, id) = await BesideFileAsync(folder, nameof(TerminalDiscardBesideUnknownAsync), recovery,
            _ => new FolderSubmitResult.Unknown(TextKey.DropboxRefused));
        using (f)
        {
            var allocation = new StageAllocation(id, member.Ordinal, "allocation-a", Path.Combine(folder, "stage-b"));
            var selected = Move(f, member, member with
            {
                Phase = FolderRemovalItem.RecoveryPhase.AllocateArmed, Selected = member.Expected, StageAllocation = allocation
            });
            var staged = Move(f, selected, selected with
            {
                Phase = FolderRemovalItem.RecoveryPhase.Staged,
                Stage = new StageProof(allocation.Path, "volume-a", "file-a", 1, member.Expected!.ContentHash)
            });
            Move(f, staged, staged.BeginTerminalDiscard());

            await f.Runner.ResumeAsync(f.Store.FolderRemoval(f.Pair.PairId, id), null, CancellationToken.None);

            var after = Item(f, id, member.Ordinal);
            Check.Require(after.Phase == FolderRemovalItem.RecoveryPhase.Settled && after.Stage is null &&
                recovery.Calls == 1, "terminal discard does not wait on an unrelated Unknown effect");
            Check.Require(f.Store.FolderRemoval(f.Pair.PairId, id).Phase == FolderRemovalPhase.Unknown,
                nameof(FolderRemovalPhase.Unknown));
        }
    }

    private sealed class InterruptedRecovery(bool afterDiscard, bool cancellation, CancellationTokenSource cancel) : ILocalRecovery
    {
        public bool Interrupt = true;
        public int Calls;
        public bool StageExists = true;
        public Task DiscardRecoveryStageAsync(StageProof proof, CancellationToken ct)
        {
            Calls++;
            if (!Interrupt) { StageExists = false; return Task.CompletedTask; }
            if (afterDiscard) StageExists = false;
            if (cancellation) { cancel.Cancel(); return Task.FromCanceled(cancel.Token); }
            return Task.FromException(new InvalidOperationException(nameof(InterruptedRecovery)));
        }
        public Task<RemovalResult> ResolvePendingAsync(PendingRead pending, RecoveryChoice choice, CancellationToken ct) => throw new InvalidOperationException();
        public IReadOnlyList<(string Path, Exception Error)> RemoveStaleStages(IReadOnlySet<string> names) => [];
        public bool CanArmReplace() => throw new NotSupportedException();
        public Task<RemovalProof> ProveRemovalAsync(Entry expected, string? hash, CancellationToken ct) =>
            throw new NotSupportedException();
        public DriveEvidence Drive() => throw new InvalidOperationException();
        public StageAllocation PlanRecoveryStage(string operationId, long ordinal) => throw new InvalidOperationException("abandoned bytes must not be restaged");
        public Task<IRecoveryStage> StageRecoveryAsync(StageAllocation allocation, Entry selected, Stream content, CancellationToken ct) => throw new InvalidOperationException();
        public Task<ILocalRecovery.AllocationOccupancy> InspectRecoveryAllocationAsync(StageAllocation allocation, CancellationToken ct) => throw new InvalidOperationException();
        public Task<Stream> OpenRecoveryStageAsync(StageProof proof, CancellationToken ct) => throw new InvalidOperationException("abandoned bytes must not be published");
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(string folder, string name, ILocalRecovery? recovery = null, IAdmission? limiter = null,
            bool localEvidence = false, Func<Guid, string, long, IssueSubject, ItemRetryToken?, bool>? registry = null,
            int pageSize = PageSize,
            Func<Side, string, bool, CancellationToken, Task<(IdentityLocation Location, bool InScope)?>>? byId = null)
        {
            Pair = new PairConfig(name, name, Path.GetFullPath(nameof(FakeSide)), nameof(Side.Dropbox),
                nameof(PairConfig.ConnectionId), [], [], FirstRunPolicy.NewestWins, true, null);
            // A Local deletion reads Local evidence, which needs the bound root a real host saves.
            if (localEvidence) Pair = SourceFixture.Bound(Pair);
            var settings = new EngineSettings("", "", "", "", "", RecycleFallback.Fail, default,
                FirstRunPolicy.NewestWins, default, "", LogBufferCharacters, ScanTestSettings.BatchRows)  { JournalPageBufferBytes = ScanTestSettings.JournalPageBufferBytes, LogReadBufferBytes = ScanTestSettings.LogReadBufferBytes }with { StoragePageSize = pageSize };
            var database = Path.Combine(folder, Path.GetRandomFileName());
            RawStore = registry is null ? StateStore.Open(database) : StateStore.Open(database, registry);
            Store = new StoreCounters(RawStore);
            Store.SavePair(Pair);
            LocalEntries = new FakeSide(Side.Local);
            DropboxEntries = new FakeSide(Side.Dropbox);
            Local = new ScriptedFolderSide(LocalEntries);
            Dropbox = new ScriptedFolderSide(DropboxEntries);
            Context = new ExecutorContext(
                Local: byId is null ? Local : new IdentitySide(Local,
                    (id, ct) => byId(Side.Local, id, false, ct), (id, ct) => byId(Side.Local, id, true, ct)),
                Dropbox: byId is null ? Dropbox : new IdentitySide(Dropbox, (id, ct) => byId(Side.Dropbox, id, false, ct)),
                Recovery: recovery ?? new FakeRecovery(), Store: Store, Pair: Pair,
                Scope: new PathScope(Pair, [Pair]), Settings: settings, Runtime: new ReplaceTurn(),
                Admission: new FakeAdmission(), Priority: false, Events: value =>
                {
                    OnEvent?.Invoke(value);
                    Events.Add(value);
                },
                ReserveDownloadAsync: static (_, _, _, _) =>
                    throw new InvalidOperationException(nameof(ExecutorContext.ReserveDownloadAsync)),
                Time: new FakeTimeProvider(),
                ReadDeletionEvidenceAsync: (_, _, _) => Evidence is { } evidence ? Task.FromResult(evidence) :
                    throw new InvalidOperationException(nameof(ExecutorContext.ReadDeletionEvidenceAsync)),
                BeginDownloadAttempt: static _ =>
                    throw new InvalidOperationException(nameof(ExecutorContext.BeginDownloadAttempt)),
                CompleteDownloadAttemptAsync: static (_, _) =>
                    throw new InvalidOperationException(nameof(ExecutorContext.CompleteDownloadAttemptAsync)),
                UploadAdmissionIdentity: (nameof(PairConfig.ConnectionId), nameof(Side.Dropbox), nameof(Fixture)),
                ItemAttempts: new NeverItemAttempts(),
                Opportunity: new object(),
                ExpectedGeneration: 0)
            {
                CaptureFolderCursorAsync = static _ => Task.FromResult("cursor"),
                ReadFolderChangesAsync = (_, _) => Replayed()
            };
            _recovery = limiter is null ? Boundary : new AdmittingBoundary(Pair, limiter);
            Runner = NewRunner();
        }

        private readonly IRecoveryExecutionBoundary _recovery;

        /// <summary>A runner over the same context and store, as a restart sees it.</summary>
        public FolderDeleteRunner NewRunner()
        {
            var transfers = new Transfers(Context);
            return new FolderDeleteRunner(Context,
                new ActionExecutor(Context, transfers, new Parents(Context), new KeepBothRunner(Context, transfers)),
                _recovery);
        }

        public PassthroughBoundary Boundary { get; } = new();
        public Action<RunEvent>? OnEvent { get; set; }

        public StateStore RawStore { get; }
        public PairConfig Pair { get; }
        public StoreCounters Store { get; }
        public FakeSide LocalEntries { get; }
        public FakeSide DropboxEntries { get; }
        public ScriptedFolderSide Local { get; }
        public ScriptedFolderSide Dropbox { get; }
        public ExecutorContext Context { get; }
        public FolderDeleteRunner Runner { get; }
        public List<RunEvent> Events { get; } = [];
        public List<Listed> Changes { get; } = [];
        public DeletionEvidence? Evidence { get; set; }

        private async IAsyncEnumerable<Listed> Replayed()
        {
            await Task.CompletedTask;
            foreach (var change in Changes) yield return change;
        }

        public void Dispose() => Store.Dispose();
    }

    /// <summary>An ISide over an in-memory FakeSide with scripted folder-submission and job-observation results.</summary>
    private sealed class ScriptedFolderSide(FakeSide inner) : ISide
    {
        private readonly Queue<FolderSubmitResult> _submits = new();
        private readonly Queue<object> _reads = new();

        public int SubmitCalls { get; private set; }
        public Action? OnList { get; set; }
        public Action<ListOptions>? OnReadAll { get; set; }
        public int ReadCalls { get; private set; }
        public TaskCompletionSource<bool> ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<FolderSubmission> Submissions { get; } = [];
        public List<IReadOnlyList<FolderRemovalEffect>> Submitted { get; } = [];

        public Side Side => inner.Side;

        public void Returns(FolderSubmitResult result) => _submits.Enqueue(result);

        public void Returns(FolderJobResult result) => _reads.Enqueue(result);

        public void Returns(Task<FolderJobResult> result) => _reads.Enqueue(result);

        public void Throws(Exception error) => _reads.Enqueue(error);

        public IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options, CancellationToken ct)
        {
            OnList?.Invoke();
            OnReadAll?.Invoke(options);
            return inner.ReadAllAsync(under, options, ct);
        }

        public Task<Entry?> ReadOneAsync(string path, CancellationToken ct) => inner.ReadOneAsync(path, ct);

        public Task<Stream> OpenReadAsync(Entry entry, RequestAttempt? attempt, CancellationToken ct) =>
            inner.OpenReadAsync(entry, attempt, ct);

        public Task<string> ContentHashAsync(Entry entry, CancellationToken ct) => inner.ContentHashAsync(entry, ct);

        public Task<IStaged> StageAsync(string path, Stream content, Entry source, CancellationToken ct) =>
            inner.StageAsync(path, content, source, ct);

        public Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection,
            CancellationToken ct) => inner.PublishAsync(staged, expected, selection, ct);

        public Task<Entry> EnsureFolderAsync(string path, CancellationToken ct) =>
            inner.EnsureFolderAsync(path, ct);

        public int Deletes { get; private set; }
        public Action? OnDelete { get; set; }
        public Action? OnSubmit { get; set; }
        public Func<IAsyncEnumerable<Listed>, IAsyncEnumerable<Listed>>? WrapRecycled { get; set; }

        public async Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct)
        {
            Deletes++;
            OnDelete?.Invoke();
            var result = await inner.DeleteAsync(expected, selection, ct);
            return WrapRecycled is { } wrap && result.Recycled is { } recycled
                ? result with { Recycled = wrap(recycled) } : result;
        }

        public Task<Entry> MoveAsync(Entry expected, string newPath, CancellationToken ct) =>
            inner.MoveAsync(expected, newPath, ct);

        public Task<FolderSubmitResult> SubmitFolderRemovalAsync(FolderSubmission submission,
            IReadOnlyList<FolderRemovalEffect> effects, RequestAttempt attempt, CancellationToken ct)
        {
            if (Side == Side.Local) throw new NotSupportedException();
            OnSubmit?.Invoke();
            SubmitCalls++;
            Submissions.Add(submission);
            Submitted.Add(effects);
            return Task.FromResult(_submits.Dequeue());
        }

        public Task<FolderJobResult> ReadFolderRemovalAsync(string jobId, int expectedCount,
            RequestAttempt attempt, CancellationToken ct)
        {
            if (Side == Side.Local) throw new NotSupportedException();
            ReadCalls++;
            ReadStarted.TrySetResult(true);
            var next = _reads.Dequeue();
            if (next is Exception error) throw error;
            return next is Task<FolderJobResult> pending ? pending.WaitAsync(ct) : Task.FromResult((FolderJobResult)next);
        }
    }

    /// <summary>Runs a recovery callback with the operation's own identity, as the real admission seam does.</summary>
    private sealed class PassthroughBoundary : IRecoveryExecutionBoundary, ILiveExecutionBoundary
    {
        internal int Holds;

        /// <summary>The lease of the latest admitted callback.</summary>
        internal Lease? Last { get; private set; }
        public bool TryHoldGeneration(string pairId) { Holds++; return true; }
        public Task<T> ExecuteAsync<T>(IReadOnlyList<LiveExecutionRequest> requests,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<LiveCoverage> CaptureCoverageAsync(WorkSelection selection,
            IReadOnlyList<LiveEffectScope> footprint, CancellationToken ct) => throw new NotSupportedException();
        public void RequireCurrent() { }

        public Task<T> ExecuteAsync<T>(FolderRemovalState operation, FolderRemovalItem member,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) =>
            ExecuteAsync(operation, execute, ct);

        public int Calls { get; private set; }

        public async Task<T> ExecuteAsync<T>(IReadOnlyList<FolderRemovalState> operations,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct)
        {
            Calls++;
            await using var lease = Last = new Lease();
            return await execute(lease, ct);
        }

        public async Task<T> ExecuteAsync<T>(FolderRemovalState operation,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct)
        {
            Calls++;
            await using var lease = Last = new Lease();
            return await execute(lease, ct);
        }

        public async Task ExecuteAsync(FolderRemovalState operation,
            Func<IAdmittedExecution, CancellationToken, Task> execute, CancellationToken ct)
        {
            Calls++;
            await using var lease = new Lease();
            await execute(lease, ct);
        }

        internal sealed class Lease : IAdmittedExecution
        {
            public bool Released { get; private set; }

            public void Require(string pairId, long generation, IReadOnlyList<string> requiredLockKeys) { }

            public void RequireScopes(string pairId, long generation, IReadOnlyList<PathLockScope> scopes) { }

            public void Revalidate() { }

            public ValueTask ReleaseAdmissionAsync()
            {
                Released = true;
                return default;
            }

            public ValueTask DisposeAsync() => default;
        }
    }

    /// <summary>Runs an ordinary item attempt with no retry episode; a reported failure or completion is a defect.</summary>
    private sealed class PassingItemAttempts : IItemAttempts
    {
        public Task<T> ExecuteAsync<T>(object opportunity, ItemRetryCandidate? expected, IssueSubject subject,
            Func<Guid, CancellationToken, Task<T>> execute, CancellationToken ct) => execute(Guid.NewGuid(), ct);

        public ItemRetryCandidate ReportFailure(Guid attemptId, long sourceEpoch, string path,
            IssueSubject subject, TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch,
            PendingEffect? expectedPending, ItemRetryToken? expectedEpisode, Action<Issue, bool>? settledIssue = null, Action<Issue>? committedIssue = null) =>
            throw new InvalidOperationException(nameof(IItemAttempts));

        public void Complete(Guid attemptId, ItemRetryCandidate expected, StateChange change,
            PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null, Action<Issue, bool>? settledIssue = null) =>
            throw new InvalidOperationException(nameof(IItemAttempts));
    }

    /// <summary>The folder path never runs an ordinary item attempt, so any use here is a defect under test.</summary>
    private sealed class NeverItemAttempts : IItemAttempts
    {
        public Task<T> ExecuteAsync<T>(object opportunity, ItemRetryCandidate? expected, IssueSubject subject,
            Func<Guid, CancellationToken, Task<T>> execute, CancellationToken ct) =>
            throw new InvalidOperationException(nameof(IItemAttempts));

        public ItemRetryCandidate ReportFailure(Guid attemptId, long sourceEpoch, string path,
            IssueSubject subject, TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch,
            PendingEffect? expectedPending, ItemRetryToken? expectedEpisode, Action<Issue, bool>? settledIssue = null, Action<Issue>? committedIssue = null) =>
            throw new InvalidOperationException(nameof(IItemAttempts));

        public void Complete(Guid attemptId, ItemRetryCandidate expected, StateChange change,
            PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null, Action<Issue, bool>? settledIssue = null) =>
            throw new InvalidOperationException(nameof(IItemAttempts));
    }
}

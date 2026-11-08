using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Massdex.SyncShark.Engine.Tests.Executor;
using Massdex.SyncShark.V2;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>The call-budget check (`--engine-checks budget`): a Repair over a large fake pair costs listings only, and
/// its provider calls, store commands, commits, allocation and database bytes per item stay flat as the pair grows.
/// It also runs the section 9.4 storage cost shapes, which assert operation counts rather than elapsed times.
/// The executable keeps the aggregate; budget-1 through budget-4 partition it, and hosted CI expands budget.</summary>
public static partial class CallBudgetChecks
{
    // Both sizes are 5,110 and 20,440 items, exactly fourfold, but no longer the same folder count: the large size
    // spreads 20,000 files over 40 top folders with 10 subfolders each (440 folders), so a cost per folder shows in
    // the two-size comparison. The previous 110-folder shape stays as the dense-folder adverse case at 20,440 items.
    private const int SmallFiles = 5_000;
    private const int SmallTops = 10;
    private const int SubFolders = 10;
    private const int LargeFiles = SmallFiles * 4;
    private const int LargeTops = SmallTops * 4;
    private const int DenseFiles = SmallFiles * 4 + 330;
    private const int UnequalTimePercent = 40;
    // Historical measured mean in ; the generated distribution is reported, not assumed.
    private const int TypicalPathLength = 144;
    private const string TreeRoot = "Tree";
    private const int SampleMilliseconds = 50;
    private static readonly DateTimeOffset BaseTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly Shape SmallShape = new("small", SmallFiles, SmallTops);
    private static readonly Shape LargeShape = new("large", LargeFiles, LargeTops);
    private static readonly Shape DenseShape = new("dense", DenseFiles, SmallTops);

    private static readonly EngineSettings Settings = BudgetSettings();

    private static EngineSettings BudgetSettings()
    {
        using var document = CliCatalogResource.Load(typeof(SettingsParser).Assembly, ResourceNames.Settings);
        var shipped = SettingsParser.Parse(document.RootElement.GetRawText(), null);
        return new EngineSettings("{name} ({side} {time}){ext}", "yyyyMMddHHmmss",
            "Local", "Dropbox", ".case", RecycleFallback.Fail, 1, FirstRunPolicy.NewestWins, 1, "", 262144, ScanTestSettings.BatchRows)
        {
        JournalPageBufferBytes = ScanTestSettings.JournalPageBufferBytes,
        LogReadBufferBytes = ScanTestSettings.LogReadBufferBytes,
            Backoff = new FailureRetryOwner.BackoffSettings(1, 1.6, 0.2, 120),
            StoragePageSize = shipped.StoragePageSize
        };
    }

    public static void RunBudgetCommon()
    {
        foreach (var size in new[] { SmallFiles, LargeFiles })
            Massdex.SyncShark.Engine.Tests.Storage.ForgetUnreadableChecks.Run(size, budget: true);
        LocalDirtyCosts();
        StorageCostChecks.Run();
        AsciiCosts();
        PageAttemptCosts();
        RecordAtomicityCosts();
        InBudgetFolder(folder =>
            ParentsChecks.OwnFolderBudgetAsync(folder, SmallShape.Items, LargeShape.Items).GetAwaiter().GetResult());
    }

    public static void RunBudgetPair() => InBudgetFolder(folder =>
    {
        var small = PairCosts(Path.Combine(folder, "small"), SmallShape);
        var large = PairCosts(Path.Combine(folder, "large"), LargeShape);
        var dense = PairCosts(Path.Combine(folder, "dense"), DenseShape);
        AssertGrowth("identical first run", small.First, large.First with { AllocatedBytes = long.MaxValue / 2 }, elapsedGate: true);
        AssertGrowth("unchanged pair", small.Unchanged, large.Unchanged, elapsedGate: true);
        AssertGrowth("1%-changed run", small.Changed, large.Changed, elapsedGate: true);
        AssertGrowth("mixed-type 1%-changed run", small.Mixed, large.Mixed);
        // The same 1% share changes four times the items; these counts must follow the items exactly.
        Check.Require(large.Changed.ProviderCalls == small.Changed.ProviderCalls * 4 &&
            large.Changed.Hashes == small.Changed.Hashes * 4 &&
            large.Changed.CommitCalls == small.Changed.CommitCalls * 4,
            "1%-changed run: provider calls, hashes and commits grow exactly with the item count: calls=" +
            large.Changed.ProviderCalls + "/" + small.Changed.ProviderCalls + ", hashes=" +
            large.Changed.Hashes + "/" + small.Changed.Hashes + ", commits=" + large.Changed.CommitCalls +
            "/" + small.Changed.CommitCalls);
        AssertGrowth("dense-folder unchanged pair", small.Unchanged, dense.Unchanged, elapsedGate: true);
    });

    public static void RunBudgetWholeFolder() => InBudgetFolder(folder =>
        GrowthBySize("whole folder", shape => FolderCosts(Path.Combine(folder, shape.Name + "-delete"),
            shape with { Rooted = true })));

    public static void RunBudgetDeepFolder() => InBudgetFolder(folder =>
        GrowthBySize("deep folder", shape => FolderCosts(Path.Combine(folder, shape.Name + "-deep"),
            shape with { Rooted = true, Deep = true })));

    public static void RunBudgetManyFolders() => InBudgetFolder(folder =>
        GrowthBySize("many folders", shape => ManyFolderCosts(Path.Combine(folder, shape.Name + "-many-roots"), shape)));

    public static void RunBudgetPartialFolder() => InBudgetFolder(folder =>
        GrowthBySize("partial folder", shape => FolderCosts(Path.Combine(folder, shape.Name + "-partial"),
            Flat(shape), partial: true)));

    public static void RunBudgetExclusion() => InBudgetFolder(folder =>
        GrowthBySize("dense exclusion", shape => ExclusionCosts(Path.Combine(folder, shape.Name + "-exclude"), Flat(shape))));

    public static void RunBudgetPlanSql() => InBudgetFolder(folder =>
    {
        Dictionary<string, StatementCost>? smallSql = null;
        foreach (var shape in new[] { SmallShape, LargeShape })
        {
            var flat = Flat(shape);
            var sql = PlanSqlCosts(Path.Combine(folder, shape.Name + "-sql"), flat);
            if (shape == LargeShape) sql["capture"] = sql["capture"] with { VmSteps = 1_000_000_000_000 };
            if (smallSql is not null)
                foreach (var (phase, cost) in sql)
                {
                    var before = smallSql[phase];
                    // Partial phases visit M-1 removed children, so their ratio is slightly greater than M's.
                    var smallRemoved = SmallShape.Items - 2;
                    var largeRemoved = flat.Files - 1;
                    Check.Require(cost.VmSteps * smallRemoved <= before.VmSteps * largeRemoved &&
                        cost.PathKeyCalls * smallRemoved <= before.PathKeyCalls * largeRemoved &&
                        cost.DepthCalls * smallRemoved <= before.DepthCalls * largeRemoved &&
                        cost.ChangedRows * smallRemoved <= before.ChangedRows * largeRemoved,
                        phase + ": executed VM and callback counts grow no faster than children");
                }
            smallSql = sql;
        }
    });

    public static void RunBudgetAdverse() => InBudgetFolder(folder =>
    {
        foreach (var shape in new[] { SmallShape, LargeShape })
            FailedListingCosts(Path.Combine(folder, shape.Name + "-failed"), shape);
    });

    public static void RunBudgetComposedProvider() => InBudgetFolder(folder =>
        ComposedProviderCostsAsync(folder).GetAwaiter().GetResult());

    private static void GrowthBySize(string name, Func<Shape, Cost> run)
    {
        var small = run(SmallShape);
        var large = run(LargeShape) with { AllocatedBytes = long.MaxValue / 2 };
        AssertGrowth(name, small, large);
    }

    private static Shape Flat(Shape shape) => new(shape.Name + "-flat", shape.Items - 1, 0, Rooted: true);

    private static void InBudgetFolder(Action<string> run)
    {
        var folder = Path.Combine(Path.GetTempPath(), "syncshark-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            run(folder);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void LocalDirtyCosts()
    {
        var counts = new List<int>();
        foreach (var shape in new[] { SmallShape, LargeShape })
        {
            var directory = Path.Combine(Path.GetTempPath(), "syncshark-dirty-budget-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                using var store = StateStore.Open(Path.Combine(directory, "state.db"));
                var pair = new PairConfig("dirty-budget", "Pair A", "X:/Parent", "/Parent", "account", [], [],
                    FirstRunPolicy.NewestWins, true, null);
                store.SavePair(pair);
                var generation = store.ReadPairExecution(pair.PairId)!.Value.Generation;
                var source = store.ReadSource(pair.PairId, Side.Local)!.Value;
                var connection = (SqliteConnection)typeof(StateStore).GetField("_connection",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
                var statements = 0;
                SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)((_, _) => statements++), null);
                for (var index = 0; index < shape.Files; index++)
                    Check.Require(store.ReadLocalDirty(pair.PairId, generation, source.Epoch, "volume", "identity",
                        PathNames.Key("Parent/File"), null) == (false, null, 0L), "unchanged dirty answer");
                SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null);
                Console.WriteLine($"ReadLocalDirty items={shape.Items} files={shape.Files} statements={statements}");
                counts.Add(statements);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(directory, recursive: true);
            }
        }
        Check.Require(counts.All(count => count == 4),
            "dirty-free reads cost four statements independent of file count");
    }

    private static async Task ComposedProviderCostsAsync(string folder)
    {
        Dictionary<string, int>[]? previous = null;
        foreach (var shape in new[] { SmallShape, LargeShape })
        {
            var root = Path.Combine(folder, shape.Name + "-composed");
            var phase = Stopwatch.StartNew();
            await using var rig = await RepairChecks.ComposeRigAsync(root);
            Console.WriteLine("composed " + shape.Name + " compose_ms=" + phase.ElapsedMilliseconds);
            phase.Restart();
            for (var index = 0; index < shape.Items; index++)
            {
                var name = "item-" + index.ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
                var path = Path.Combine(rig.Pair.LocalRoot, name);
                await File.WriteAllTextAsync(path, "content");
                File.SetLastWriteTimeUtc(path, BaseTime.UtcDateTime);
                rig.Wire.SeedFile(name, "content", BaseTime);
            }
            Console.WriteLine("composed " + shape.Name + " seed_ms=" + phase.ElapsedMilliseconds);
            var counts = new Dictionary<string, int>[2];
            for (var run = 0; run < counts.Length; run++)
            {
                rig.Wire.Calls.Clear();
                using var log = rig.Log(root, "run-" + run);
                phase.Restart();
                var summary = await rig.RunAsync(log, CancellationToken.None);
                Console.WriteLine("composed " + shape.Name + " run=" + run + " elapsed_ms=" + phase.ElapsedMilliseconds);
                Check.Require(summary.Completed && summary.IssuesByKind.Count == 0 &&
                    !summary.Effects.ContainsKey(nameof(EffectKind.Uploaded)) &&
                    !summary.Effects.ContainsKey(nameof(EffectKind.Downloaded)),
                    "composed equal-content first and unchanged runs complete without transfers or issues");
                counts[run] = rig.Wire.Calls.GroupBy(route => route, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
                Check.Require(rig.Wire.Calls.Count(route => route.EndsWith("/list_folder", StringComparison.Ordinal)) == 1 &&
                    rig.Wire.Calls.Count(route => route.EndsWith("/get_metadata", StringComparison.Ordinal)) == 2 &&
                    rig.Wire.AttemptedWritePaths.Count == 0,
                    "composed Repair has one inventory and two root checks, with no per-item metadata or writes");
                if (previous is not null) counts[run][counts[run].Keys.First()]++;
                if (previous is not null)
                    Check.Require(counts[run].Count == previous[run].Count && counts[run].All(pair =>
                        previous[run].GetValueOrDefault(pair.Key) == pair.Value),
                        "every HTTP route count stays constant across the fourfold whole-Repair sizes");
                Console.WriteLine("composed " + shape.Name + " run=" + run + " items=" + shape.Items + " HTTP=" +
                    string.Join(",", counts[run].OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value)));
            }
            previous = counts;
        }
    }

    // Reusing one pair separates first-run work from unchanged rows, file edits and a mixed-type change.
    private static (Cost First, Cost Unchanged, Cost Changed, Cost Mixed) PairCosts(string folder, Shape shape)
    {
        var local = new FakeSide(Side.Local);
        var dropbox = new FakeSide(Side.Dropbox);
        var unequalTimes = shape.Files * UnequalTimePercent / 100;
        Seed(local, dropbox, shape, changedTimes: unequalTimes);
        // NAME-SKIP mapping is deferred, so only the unequal-time first-run rows need a comparison hash.
        var hashed = unequalTimes;
        using var rig = NewRig(folder, local, dropbox);
        var total = shape.Items;

        var firstRun = Measure(shape.Name + " identical first run", shape, rig, "run-1");
        var first = firstRun.Cost;
        Check.Require(firstRun.Summary.Completed, "the identical first Repair completes");
        Check.Require(rig.Local.Listings == 1 && rig.Dropbox.Listings == 1,
            "the identical first Repair costs its two listings only: local=" + rig.Local.Listings +
            " dropbox=" + rig.Dropbox.Listings);
        Check.Require(rig.Local.Reads == 0 && rig.Dropbox.Reads == 0,
            "the identical first Repair makes no per-item ReadOne calls: local=" + rig.Local.Reads +
            " dropbox=" + rig.Dropbox.Reads);
        Check.Require(rig.Local.Hashes == hashed,
            "the unequal-time or mapped-name files are hashed once each: local hashes=" + rig.Local.Hashes +
            " expected=" + hashed);
        Check.Require(rig.Dropbox.Hashes == 0,
            "the Dropbox side reads its recorded hashes instead of recomputing them: dropbox hashes=" +
            rig.Dropbox.Hashes);
        Check.Require(rig.Local.Work == 0 && rig.Dropbox.Work == 0,
            "identical content is never copied: local=" + rig.Local.Work + " dropbox=" + rig.Dropbox.Work);
        Check.Require(first.ProviderCalls == first.Hashes,
            "the identical first Repair's only per-item provider calls are the designed hashes: calls=" +
            first.ProviderCalls + " hashes=" + first.Hashes);
        Check.Require(firstRun.Summary.IssuesByKind.Count == 0 && rig.Store.Issues(rig.Pair.PairId).Count == 0,
            "the identical first Repair raises no notice");
        Check.Require(Recorded(firstRun.Summary) == total,
            "the identical first Repair records every item: recorded=" + Recorded(firstRun.Summary) +
            " of " + total);
        // rev3h G05: every first-run Record enters CommitRecordAsync; files queue and folder Records commit immediately.
        Check.Require(first.CommitCalls == total && rig.Counters.RecordCommitCalls == total &&
            rig.Counters.RecordCommittedMembers == total && rig.Counters.RecordCommitFailures == 0,
            "every first-run Record takes one entry and completes; folder Records still commit immediately: "
            + $"commits={first.CommitCalls} entries={rig.Counters.RecordCommitCalls} completed={rig.Counters.RecordCommittedMembers} total={total} files={shape.Files}");
        Check.Require(rig.Counters.ObservedCommittedGroups >= 1 &&
            rig.Counters.ObservedCommittedGroups <= total,
            "observed committed-visibility groups are between one and the submitted entry count");
        Check.Require(first.Permits == 0 && first.Completions == 0,
            "provider-free first-run Records and hashes need no admission or effect completion");
        AssertScan(rig, total);

        rig.Local.Reset();
        rig.Dropbox.Reset();
        var unchangedRun = Measure(shape.Name + " unchanged second run", shape, rig, "run-2");
        var unchanged = unchangedRun.Cost with { UnchangedFiles = shape.Files };

        Check.Require(unchangedRun.Summary.Completed, "the unchanged Repair completes");
        Check.Require(rig.Local.Listings == 1 && rig.Dropbox.Listings == 1,
            "the unchanged Repair costs its two listings only: local=" + rig.Local.Listings +
            " dropbox=" + rig.Dropbox.Listings);
        Check.Require(rig.Local.Reads == 0 && rig.Dropbox.Reads == 0,
            "the unchanged Repair makes no per-item ReadOne calls: local=" + rig.Local.Reads +
            " dropbox=" + rig.Dropbox.Reads);
        Check.Require(rig.Local.Hashes == 0 && rig.Dropbox.Hashes == 0,
            "the unchanged Repair hashes nothing: local=" + rig.Local.Hashes + " dropbox=" + rig.Dropbox.Hashes);
        Check.Require(rig.Local.Work == 0 && rig.Dropbox.Work == 0,
            "the unchanged Repair transfers, deletes and moves nothing: local=" + rig.Local.Work +
            " dropbox=" + rig.Dropbox.Work);
        Check.Require(unchanged.ProviderCalls == 0,
            "the unchanged Repair makes no per-item provider call at " + total + " items: calls=" +
            unchanged.ProviderCalls);
        Check.Require(unchangedRun.Summary.Effects.Count == 0 && unchangedRun.Summary.IssuesByKind.Count == 0,
            "the unchanged Repair records no effect and raises no notice");
        Check.Require(rig.Store.Issues(rig.Pair.PairId).Count == 0, "the unchanged Repair leaves no issue behind");
        Check.Require(unchanged.Permits == 0 && unchanged.Completions == 0 &&
            rig.Counters.RecordCommitCalls == 0 && rig.Counters.PlanPhases.Count == 0,
            "unchanged rows acquire no permits, submit no Records and open no folder plans");
        AssertClosureCalls(unchanged);
        var rejected = false;
        try { AssertClosureCalls(unchanged with { CommitCalls = unchanged.CommitCalls + 1 }); }
        catch (InvalidOperationException) { rejected = true; }
        Check.Require(rejected, "one extra closure call must fail the absolute budget");
        AssertScan(rig, total);

        var changedCount = Math.Max(1, total / 100);
        ChangeLocal(local, shape, changedCount);
        rig.Local.Reset();
        rig.Dropbox.Reset();
        var measured = Measure(shape.Name + " 1%-changed run", shape, rig, "run-3");
        var changed = measured.Cost;
        var summary = measured.Summary;

        Check.Require(summary.Completed, "the 1%-changed Repair completes");
        Check.Require(rig.Local.Listings == 1 && rig.Dropbox.Listings == 1,
            "the 1%-changed Repair costs its two listings only: local=" + rig.Local.Listings +
            " dropbox=" + rig.Dropbox.Listings);
        Check.Require(rig.Local.Hashes == changedCount && rig.Dropbox.Hashes == 0,
            "one local hash per changed file and none on Dropbox: local=" + rig.Local.Hashes +
            " dropbox=" + rig.Dropbox.Hashes + " changed=" + changedCount);
        Check.Require(rig.Local.Work + rig.Dropbox.Work == 3 * changedCount,
            "one read, one stage and one publish per changed file: local=" + rig.Local.Work +
            " dropbox=" + rig.Dropbox.Work + " changed=" + changedCount);
        Check.Require(changed.ProviderCalls == 6 * changedCount && changed.Hashes == changedCount,
            "the 1%-changed Repair makes six provider calls per changed file: reads=" +
            (rig.Local.Reads + rig.Dropbox.Reads) + " hashes=" + changed.Hashes + " work=" +
            (rig.Local.Work + rig.Dropbox.Work) + " changed=" + changedCount);
        Check.Require(changed.Permits == changedCount && changed.Completions == changedCount,
            "each changed upload has one outer permit and one Dropbox-effect completion: permits=" +
            changed.Permits + " completions=" + changed.Completions + " changed=" + changedCount);
        Check.Require(rig.Counters.RecordCommitCalls == 0,
            "effect-bearing changes are never queued as first-run Records");
        AssertClosureCalls(changed);
        AssertScan(rig, total);
        Check.Require(summary.Effects.GetValueOrDefault(EffectKind.Uploaded.ToString()) == changedCount,
            "the 1%-changed Repair uploads exactly the changed files: uploaded=" +
            summary.Effects.GetValueOrDefault(EffectKind.Uploaded.ToString()) + " changed=" + changedCount);
        Check.Require(summary.IssuesByKind.Count == 0 && rig.Store.Issues(rig.Pair.PairId).Count == 0,
            "the 1%-changed Repair raises no notice");
        ChangeLocal(local, shape, changedCount, revision: 2);
        var replacedPath = PathNames.LocalSpelling(PathOf(0, shape));
        local.Seed(new Entry(replacedPath, true, 0, default, "replacement-folder", "", "", null), null);
        rig.Local.Reset();
        rig.Dropbox.Reset();
        var mixed = Measure(shape.Name + " 1%-changed files and kind replacement", shape, rig, "run-4");
        Check.Require(mixed.Summary.Completed && mixed.Summary.IssuesByKind.Count == 0 &&
            mixed.Summary.Effects.GetValueOrDefault(EffectKind.Replaced.ToString()) == 1 &&
            mixed.Summary.Effects.GetValueOrDefault(EffectKind.Uploaded.ToString()) == changedCount - 1,
            "mixed 1% changes upload C-1 files and replace one file with a folder");
        Check.Require(rig.Dropbox.Publishes == changedCount - 1 && rig.Dropbox.Deletes == 1 &&
            rig.Dropbox.Ensures == 1 && mixed.Cost.Hashes == changedCount - 1 &&
            mixed.Cost.Permits == changedCount && mixed.Cost.Completions == changedCount,
            "mixed changes perform only the requested transfers and kind replacement: permits=" +
            mixed.Cost.Permits + " completions=" + mixed.Cost.Completions + " changed=" + changedCount);
        // The one file-to-folder replacement commits its removal and its folder record as two row calls.
        AssertClosureCalls(mixed.Cost, kindReplacements: 1);
        AssertScan(rig, total);
        UnchangedClosures(rig, shape);
        return (first, unchanged, changed, mixed.Cost);
    }

    // Four times the items may not cost more than four times the counted work.
    private static void AssertGrowth(string name, Cost small, Cost large, bool elapsedGate = false)
    {
        Check.Require(large.Items >= small.Items * 4,
            name + ": the larger size has at least four times the items: " + small.Items + " and " + large.Items);

        // shortcut: tolerance for B-tree page fill, tighten if it proves stable
        var databaseLimit = small.DatabaseBytesPerItem * 1.15;
        Check.Require(large.DatabaseBytesPerItem <= databaseLimit,
            name + ": database bytes per item at " + large.Items + " items (" +
            large.DatabaseBytesPerItem.ToString("F1") + ") must not exceed " + small.Items + " items (" +
            small.DatabaseBytesPerItem.ToString("F1") + ") by more than 15%");

        if (small.WrittenBytesPerItemLimit is { } smallLimit && large.WrittenBytesPerItemLimit is { } largeLimit)
            Check.Require(small.WrittenBytesPerItem <= smallLimit && large.WrittenBytesPerItem <= largeLimit,
                name + ": bytes written per item must not exceed the derived page-write plus plan-byte ceiling: " +
                small.WrittenBytesPerItem.ToString("F1") + "/" + smallLimit.ToString("F1") + " and " +
                large.WrittenBytesPerItem.ToString("F1") + "/" + largeLimit.ToString("F1"));
        else
        {
            // shortcut: tolerance for B-tree page fill, tighten if it proves stable. Growing writes per item fail here.
            var writtenLimit = small.WrittenBytesPerItem * 1.15;
            Check.Require(large.WrittenBytesPerItem <= writtenLimit,
                name + ": bytes written per item at " + large.Items + " items (" +
                large.WrittenBytesPerItem.ToString("F1") + ") must not exceed " + small.Items + " items (" +
                small.WrittenBytesPerItem.ToString("F1") + ") by more than 15%");
        }

        // shortcut: the sampled heap peak depends on GC timing, tighten if it proves stable
        var heapLimit = small.PeakManagedBytesPerItem * 1.15;
        Check.Require(large.PeakManagedBytesPerItem <= heapLimit,
            name + ": peak managed heap per item at " + large.Items + " items (" +
            large.PeakManagedBytesPerItem.ToString("F1") + ") must not exceed " + small.Items + " items (" +
            small.PeakManagedBytesPerItem.ToString("F1") + ") by more than 15%");

        // Allocation tolerance from measured variance: the per-item figure differs by under 0.25% between the two
        // sizes (identical first run ~0.23%). 5% is a conservative growth tolerance, not proof of detecting a uniform
        // extra per-item allocation, and covers process and GC noise.
        var allocatedLimit = small.AllocatedBytesPerItem * 1.05;
        Check.Require(large.AllocatedBytesPerItem <= allocatedLimit,
            name + ": allocated bytes per item at " + large.Items + " items (" +
            large.AllocatedBytesPerItem.ToString("F1") + ") must not exceed " + small.Items + " items (" +
            small.AllocatedBytesPerItem.ToString("F1") + ") by more than 5%");

        if (small.UnchangedFiles is { } smallFiles && large.UnchangedFiles is { } largeFiles)
        {
            // Dense trees have more files at the same item count; the dirty-free call is memory-only (LocalDirtyCosts).
            Check.Require(small.Members[nameof(IStateStore.ReadLocalDirty)] == smallFiles &&
                large.Members[nameof(IStateStore.ReadLocalDirty)] == largeFiles,
                name + ": exactly one dirty-evidence call per file");
            foreach (var (member, calls) in large.Members)
                if (member != nameof(IStateStore.ReadLocalDirty))
                    Check.Require(calls * small.Items <= small.Members.GetValueOrDefault(member) * large.Items,
                        name + ": " + member + " calls grow no faster than items");
        }
        else
            Check.Require(large.Commands * small.Items <= small.Commands * large.Items,
                name + ": top-level store calls grow no faster than the item count");
        // shortcut: absolute command totals await an integrated measurement; growth alone cannot detect a uniform extra call.
        if (elapsedGate)
            Check.Require(large.Elapsed.TotalMilliseconds / large.Items <=
                small.Elapsed.TotalMilliseconds / small.Items * 3,
                name + ": elapsed time per item stays within the existing threefold growth limit");

        // StateStore.FinishRepair checkpoints the write log after every completed run.
        Check.Require(small.WalBytes == 0 && large.WalBytes == 0,
            name + ": the write log is empty after the run: " + small.WalBytes + " and " + large.WalBytes + " bytes");
    }

    private static void AssertClosureCalls(Cost cost, int kindReplacements = 0) =>
        Check.Require(cost.CommitCalls == cost.Items + kindReplacements,
            "one commit/closure call per row, not transactions (plus " + kindReplacements +
            " for kind replacements): " + cost.CommitCalls + " / " + cost.Items);

    private static void UnchangedClosures(Rig rig, Shape shape)
    {
        using var observer = StateStore.Connect(rig.Database, SqliteOpenMode.ReadOnly);
        using var version = observer.CreateCommand();
        version.CommandText = "PRAGMA data_version;";
        var before = version.ExecuteScalar();
        var calls = rig.Counters.CommitCalls;
        foreach (var path in FolderPaths(shape).Concat(Enumerable.Range(0, shape.Files)
                     .Select(index => PathOf(index, shape))))
        {
            rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with
            {
                Close = new[] { path, PathNames.LocalSpelling(path) }.Distinct()
                    .Select(value => (value, IssueClosure.AfterInSync)).ToArray()
            });
        }
        // Repair also saves metadata; isolate closure commits so those legitimate writes cannot mask this check.
        Check.Require(rig.Counters.CommitCalls - calls == shape.Items && Equals(before, version.ExecuteScalar()),
            "unchanged closure commits leave the durable data version unchanged");
        rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailChanged, TreeRoot, new IssueSubject([], null))]
        });
        Check.Require(!Equals(before, version.ExecuteScalar()), "the observer detects a real durable write");
        rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with { Close = [(TreeRoot, IssueClosure.AfterEffect)] });
        // shortcut: data_version detects committed mutations, not empty transactions; a transaction counter needs a separate instrument.
    }

    private static void AsciiCosts()
    {
        const int MeasuredRuns = 5;
        // Reported ~0.23 ms passes need this power-of-two batch to exceed the ~16 ms CPU timer tick.
        const int PassesPerRun = 128;
        using var process = Process.GetCurrentProcess();
        foreach (var shape in new[] { SmallShape, LargeShape })
        {
            var paths = Enumerable.Range(0, shape.Files).Select(index => PathOf(index, shape)).ToArray();
            var upper = paths.Select(Massdex.SyncShark.V2.PathKey.Of).ToArray();
            Check.Require(paths.All(path => System.Text.Ascii.IsValid(path)), "the focused path workload is entirely ASCII");
            for (var index = 0; index < paths.Length; index++)
                Check.Require(PathNames.Key(paths[index]) == upper[index] && PathNames.Same(paths[index], upper[index]) &&
                    PathNames.Comparer.GetHashCode(paths[index]) == StringComparer.OrdinalIgnoreCase.GetHashCode(paths[index]),
                    "the ASCII baseline and candidate agree for every measured path");
            foreach (var (name, baseline, current) in new (string, Action<int>, Action<int>)[]
            {
                ("Key", index => GC.KeepAlive(Massdex.SyncShark.V2.PathKey.Of(paths[index])),
                    index => GC.KeepAlive(PathNames.Key(paths[index]))),
                ("Same", index => GC.KeepAlive(StringComparer.OrdinalIgnoreCase.Equals(paths[index], upper[index])),
                    index => GC.KeepAlive(PathNames.Same(paths[index], upper[index]))),
                ("Hash", index => GC.KeepAlive(StringComparer.OrdinalIgnoreCase.GetHashCode(paths[index])),
                    index => GC.KeepAlive(PathNames.Comparer.GetHashCode(paths[index])))
            })
            {
                Console.WriteLine("ASCII " + name + " items=" + paths.Length + " baseline=" + Time(baseline) +
                    " candidate=" + Time(current) + " median elapsed/cpu ms per pass (" + MeasuredRuns +
                    " runs of " + PassesPerRun + " passes; reported, not gated)");
            }
            string Time(Action<int> action)
            {
                var elapsed = new double[MeasuredRuns];
                var cpu = new double[MeasuredRuns];
                RunPasses();
                for (var run = 0; run < MeasuredRuns; run++)
                {
                    process.Refresh();
                    var cpuBefore = process.TotalProcessorTime;
                    var watch = Stopwatch.StartNew();
                    RunPasses();
                    watch.Stop();
                    process.Refresh();
                    elapsed[run] = watch.Elapsed.TotalMilliseconds / PassesPerRun;
                    cpu[run] = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds / PassesPerRun;
                }
                Array.Sort(elapsed);
                Array.Sort(cpu);
                return elapsed[MeasuredRuns / 2].ToString("F3") + "/" + cpu[MeasuredRuns / 2].ToString("F3");

                void RunPasses()
                {
                    for (var pass = 0; pass < PassesPerRun; pass++)
                        for (var index = 0; index < paths.Length; index++) action(index);
                }
            }
        }
    }

    // The multiplier is coprime with every file count used here, so IDs are unique without tracking a second set.
    // Widen before multiplying: the product overflows Int32 at the million-item budget scale.
    private static int NativeId(int index, Shape shape) => (int)((long)index * 7919 % shape.Files) + 1;

    // V13-A28: a transient page failure costs one page attempt at the fixed involved set, never a whole
    // acquisition, and a stale attempt writes nothing durable. The counts are identical at both pair sizes.
    private static void PageAttemptCosts()
    {
        foreach (var shape in new[] { SmallShape, LargeShape })
        {
            var folder = Path.Combine(Path.GetTempPath(), "syncshark-page-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                // This case advances the source position itself, so it needs the pair's initial empty source.
                using var rig = NewRig(folder, new FakeSide(Side.Local), new FakeSide(Side.Dropbox),
                    seedSources: false);
                SeedSaved(rig, shape);
                var pairId = rig.Pair.PairId;
                var generation = rig.Store.ReadPairExecution(pairId)?.Generation ?? 0;
                var seeded = rig.Store.ReadSource(pairId, Side.Dropbox)!;
                var provenance = new SourceProvenance("account-a", "namespace-a", "root-a", "/BudgetFixture",
                    true, true, false, false, generation, seeded.Value.Epoch);
                // Seed the binding first; without provenance the source and the page attempt cannot match.
                rig.Store.AdvanceSource(pairId, Side.Dropbox, generation, seeded.Value.Epoch, seeded.Value.Revision,
                    expectedProvenance: null, expectedCursor: seeded.Value.Cursor, seeded.Value.Epoch,
                    seeded.Value.Revision + 1, provenance, seeded.Value.Cursor, obligation: null);
                var installed = rig.Store.ReadSource(pairId, Side.Dropbox)!;
                rig.Counters.Reset();
                const int Attempts = 3;
                for (var attempt = 0; attempt < Attempts; attempt++)
                    Check.Require(Throws<LiveAdmissionStaleException>(() => rig.Store.AcquireDropboxPage(pairId,
                            generation, installed.Value.Epoch, installed.Value.Revision, provenance, "stale-cursor",
                            [], "never")),
                        "a stale page attempt refuses without a write");
                Check.Require(rig.Counters.Commands == Attempts && rig.Counters.CommitCalls == 0,
                    "each stale page attempt costs exactly one store command and no durable write: commands=" +
                    rig.Counters.Commands);
                Check.Require(rig.Store.Issues(pairId).Count == 0, "stale page attempts raise no notice");
                var unchanged = rig.Store.ReadSource(pairId, Side.Dropbox)!;
                Check.Require(unchanged.Value.Cursor == installed.Value.Cursor &&
                    unchanged.Value.Revision == installed.Value.Revision,
                    "stale page attempts leave the source position unchanged");
                rig.Counters.Reset();
                var notice = new ChangeNotice(Side.Dropbox, ChangeKind.Changed, PathOf(0, shape), null, "id-1",
                    null, false, false);
                rig.Store.AcquireDropboxPage(pairId, generation, installed.Value.Epoch, installed.Value.Revision,
                    provenance, installed.Value.Cursor, [notice], "cursor-1");
                Check.Require(rig.Counters.Commands == 1 && rig.Counters.CommitCalls == 0,
                    "one fresh page is one store command, never a whole acquisition");
                var advanced = rig.Store.ReadSource(pairId, Side.Dropbox)!;
                // repair_revision is the obligation fence FinishRepair captures before the scan (BothSourceFences);
                // one page moves only the cursor, so that pre-scan fence stays valid.
                Check.Require(advanced.Value.Cursor == "cursor-1" &&
                    advanced.Value.Revision == installed.Value.Revision,
                    "one fresh page advances the source position once");
                Check.Require(rig.Local.Listings == 0 && rig.Dropbox.Listings == 0,
                    "page attempts perform no Local or Dropbox listing");
                Console.WriteLine("  page attempts items=" + shape.Items + " stale=" + Attempts +
                    " fresh=1 commands=" + Attempts + "+1 listings=0");
            }
            finally
            {
                try { Directory.Delete(folder, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    // CV-RECORD: one multi-row first-run Record stays atomic; a refused member leaves no earlier row behind,
    // and a later good Record succeeds once. Exact internal record-drain transaction counts are not observable
    // through the store's public surface (MEASURE G1), so they are reported, not asserted.
    private static void RecordAtomicityCosts()
    {
        foreach (var shape in new[] { SmallShape, LargeShape })
        {
            var folder = Path.Combine(Path.GetTempPath(), "syncshark-record-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                using var rig = NewRig(folder, new FakeSide(Side.Local), new FakeSide(Side.Dropbox));
                SeedSaved(rig, shape);
                var pairId = rig.Pair.PairId;
                rig.Counters.Reset();
                var good = SyncedEntries.From(new Entry("good.txt", false, 1, BaseTime, "local:good", "", "", null),
                    new Entry("good.txt", false, 1, BaseTime, "dropbox:good", "rev:1", "", null));
                // The second row's required id is null, so its insert fails after the first row is inserted and
                // the whole StateChange must roll back.
                var duplicate = good with { DropboxId = null! };
                Check.Require(Throws<SqliteException>(() => rig.Store.CommitRecordAsync(pairId,
                        StateChange.Empty with { Record = [good, duplicate] }, null).GetAwaiter().GetResult()),
                    "a multi-row Record with a duplicate key refuses atomically");
                Check.Require(rig.Store.SyncedByKey(pairId, Side.Local, PathNames.Key("good.txt")) is null &&
                    rig.Counters.RecordCommitFailures == 1,
                    "the refused member's earlier row is rolled back and one member faults once");
                rig.Store.CommitRecordAsync(pairId, StateChange.Empty with { Record = [good] }, null)
                    .GetAwaiter().GetResult();
                Check.Require(rig.Store.SyncedByKey(pairId, Side.Local, PathNames.Key("good.txt")) is not null &&
                    rig.Counters.RecordCommitCalls == 2 && rig.Counters.CommitCalls == 2,
                    "a later good Record is attempted once and becomes durable: calls=" +
                    rig.Counters.RecordCommitCalls);
                Console.WriteLine("  record atomicity items=" + shape.Items + " submissions=" +
                    rig.Counters.RecordCommitCalls + " faults=" + rig.Counters.RecordCommitFailures);
            }
            finally
            {
                try { Directory.Delete(folder, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    // Varies only total pair size for the targeted cases: the same records are saved at each shared size.
    private static void SeedSaved(Rig rig, Shape shape)
    {
        var rows = new List<SyncedEntry>(shape.Files);
        for (var index = 0; index < shape.Files; index++)
        {
            var path = PathOf(index, shape);
            rows.Add(new SyncedEntry(PathNames.Key(path), path, PathNames.DropboxKey(path), path, false,
                "local:" + index, 1, BaseTime.UtcTicks, "dropbox:" + index, ""));
        }
        rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with { Record = rows });
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    // ScanPass owns reconciliation; production Repair checks separately cover its two root reads.
    private static void AssertScan(Rig rig, int items)
    {
        Check.Require(rig.Counters.RowsRead == items && rig.Counters.ReadConnections == 2,
            "one row per key and only the rename/row reader connections");
        Check.Require(rig.Counters.ScanAccepted == 2L * items &&
            rig.Counters.ScanAppliedOnCompletion == 2L * items && rig.Counters.ScanCompleted == 2 &&
            rig.Counters.ScanFailedCompletions == 0 && rig.Counters.ScanAborted == 0,
            "both complete listings apply every accepted event exactly once");
        Check.Require(rig.Counters.FilteredIssuesCalls == 1,
            "one reconciliation snapshot per scan without exclusions: filtered=" + rig.Counters.FilteredIssuesCalls);
        Console.WriteLine("  comparison pages (derived from completed one-key recipe): " + (2L * items + 1));
    }

    private static void Seed(FakeSide local, FakeSide dropbox, Shape shape, int changedTimes)
    {
        ReportShape(shape);
        var folderId = shape.Folders;
        foreach (var folder in FolderPaths(shape))
        {
            local.Seed(new Entry(folder, true, 0, default, "local-folder:" + folderId, "", "", null), null);
            dropbox.Seed(new Entry(folder, true, 0, default, "dropbox-folder:" + folderId--, "", "", null), null);
        }
        for (var index = 0; index < shape.Files; index++)
        {
            var path = PathOf(index, shape);
            var content = Content(index, changed: false);
            var time = BaseTime.AddSeconds(index);
            var id = NativeId(index, shape);
            local.Seed(new Entry(PathNames.LocalSpelling(path), false, content.Length, time,
                "local:" + id, "", "", null), content);
            dropbox.Seed(new Entry(path, false, content.Length, index < changedTimes ? time.AddHours(1) : time,
                "dropbox:" + id, "rev:" + id, DropboxContentHash.Compute(content), null), content);
        }
    }

    // The 1%-changed run edits one file every ~100 items, spread across the tree, on the local side only.
    private static void ChangeLocal(FakeSide local, Shape shape, int count, int revision = 1)
    {
        foreach (var index in ChangedIndices(shape, count))
        {
            var path = PathNames.LocalSpelling(PathOf(index, shape));
            var content = Content(index, changed: revision % 2 != 0);
            local.Seed(new Entry(path, false, content.Length, BaseTime.AddSeconds(index).AddHours(revision),
                "local:" + NativeId(index, shape), "", "", null), content);
        }
    }

    private static IEnumerable<int> ChangedIndices(Shape shape, int count)
    {
        var step = Math.Max(1, shape.Files / count);
        for (var index = 0; index < count; index++) yield return index * step;
    }

    private static Cost FolderCosts(string folder, Shape shape, bool partial = false)
    {
        var local = new FakeSide(Side.Local);
        var dropbox = new FakeSide(Side.Dropbox);
        Seed(local, dropbox, shape, changedTimes: 0);
        using var rig = NewRig(folder, local, dropbox);
        Check.Require(RepairAsync(rig, "seed").GetAwaiter().GetResult().Summary.Completed,
            "folder budget starts from a completed identical Repair");
        var localRoot = local.ReadOneAsync(TreeRoot, CancellationToken.None).GetAwaiter().GetResult()!;
        var remoteRoot = dropbox.ReadOneAsync(TreeRoot, CancellationToken.None).GetAwaiter().GetResult()!;
        var saved = rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local, PathNames.Key(TreeRoot));
        Check.Require(saved is not null, "folder budget has a saved root");
        dropbox.DeleteAsync(remoteRoot, null, CancellationToken.None).GetAwaiter().GetResult();
        if (partial) ChangeLocal(local, shape, 1);
        var row = new Row(PathNames.Key(TreeRoot), localRoot, null, saved, [], null,
            new RowContext(false, false, false, null, false, false, CompleteInventory: true, DeletionEligible: false));
        if (!partial)
        {
            rig.Local.Reset();
            rig.Dropbox.Reset();
            rig.Local.FailSubtreeAfterListing = true;
            var failed = Measure(shape.Name + " late failed folder capture", shape, rig, "failed-plan",
                session => session.Folders.RemoveFolderAsync(row, Side.Local,
                    CancellationToken.None));
            Check.Require(rig.Local.SubtreeListings == 1 && rig.Local.Hashes == 0 &&
                rig.Local.Work + rig.Dropbox.Work == 0 &&
                failed.Summary.IssuesByKind.GetValueOrDefault(IssueKind.Failure.ToString()) == 1,
                "late capture failure produces one failure and no hashes or effects");
            foreach (var path in FolderPaths(shape).Concat(Enumerable.Range(0, shape.Files)
                         .Select(index => PathNames.LocalSpelling(PathOf(index, shape)))))
                Check.Require(rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local, PathNames.Key(path)) is not null,
                    "late capture failure preserves every saved binding");
            rig.Local.FailSubtreeAfterListing = false;
        }
        rig.Local.Reset();
        rig.Dropbox.Reset();
        var measured = Measure(shape.Name + (partial ? " partial folder plan" : " whole folder plan"),
            shape, rig, "plan", session => session.Folders.RemoveFolderAsync(row, Side.Local,
                CancellationToken.None), folderPlans: 1);
        var children = shape.Items - 1;
        var phases = rig.Counters.PlanPhases;
        Check.Require(phases[nameof(FolderPlan.CaptureAsync)].Succeeded == 1 &&
            phases[nameof(FolderPlan.CaptureAsync)].Items == children &&
            phases[nameof(FolderPlan.Children)].Succeeded == 1 &&
            phases[nameof(FolderPlan.Children)].Items == children,
            "one complete capture and classification visit every child once");
        // The durable-freeze contract adds a paged frozen-evidence pass after capture and classification.
        var frozenPages = (children + Settings.StoragePageSize - 1L) / Settings.StoragePageSize + 1;
        if (!partial)
        {
            Check.Require(measured.Summary.IssuesByKind.Count == 0, "the whole folder plan raises no issue");
            Check.Require(!rig.Counters.PlanStatementCountIncomplete &&
                phases[nameof(FolderPlan.FrozenEvidence)].Succeeded == 1 &&
                phases[nameof(FolderPlan.FrozenEvidence)].Items == children &&
                rig.Counters.DerivedPlanDataStatements == 3L * children + frozenPages + 2,
                "whole-plan statements are exactly three per child, frozen pages and two classification terminal pages: " +
                rig.Counters.DerivedPlanDataStatements + " for " + children);
            Check.Require(rig.Local.Deletes == 1 && rig.Dropbox.Deletes == 0 && measured.Cost.Hashes == 0 &&
                rig.Counters.CommitCalls == 1 && measured.Cost.Permits == 1,
                "whole deletion has one root delete, one commit, one outer permit and no child hashes");
            Check.Require(local.ReadOneAsync(TreeRoot, CancellationToken.None).GetAwaiter().GetResult() is null &&
                rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local, PathNames.Key(TreeRoot)) is null,
                "whole deletion removes and forgets its root");
        }
        else
        {
            // The flat partial plan's sole survivor is the changed local file at index 0; Decide's changed/absent row
            // uploads it with an edit-beats-delete notice, so the plan raises exactly that one notice.
            var survivorPath = PathNames.LocalSpelling(PathOf(0, shape));
            var issues = rig.Store.Issues(rig.Pair.PairId);
            Check.Require(issues.Count == 1 && issues[0].Kind == IssueKind.Info && issues[0].Path == survivorPath &&
                measured.Summary.IssuesByKind.GetValueOrDefault(nameof(IssueKind.Info)) == 1,
                "the partial folder plan raises only its survivor's info notice");
            Check.Require(phases[nameof(FolderPlan.MarkClassified)].Succeeded == children &&
                phases[nameof(FolderPlan.SelectBranch)].Succeeded == children - 1 &&
                phases[nameof(FolderPlan.FrozenEvidence)].Items == children &&
                rig.Counters.MemberCalls.GetValueOrDefault(nameof(IStateStore.CompleteFolderFollowUp)) == 1,
                "flat partial plan freezes one survivor and removes every other child once");
            Check.Require(rig.Local.Deletes == children - 1 && rig.Dropbox.Publishes == 1 &&
                local.ReadOneAsync(TreeRoot, CancellationToken.None).GetAwaiter().GetResult() is not null,
                "partial deletion preserves the root and follows up its sole changed survivor");
            Check.Require(measured.Cost.Permits == 1,
                "the survivor owns one upload admission; folder work scopes and the harness recovery lease are not counted permits");
            Check.Require(rig.Counters.CommitCalls == children + 1,
                "partial plan commits R removals, one recreated parent and one survivor upload");
            // Capture, classify, removed rows, survivor probes and selection cost 6M+3, then the frozen pages.
            Check.Require(rig.Counters.DerivedPlanDataStatements == 6L * children + frozenPages + 3,
                "the derived flat partial recipe is exactly 6M+frozenPages+3 data statements");
            // Flat children have no strict ancestor below the root, so the unobserved ancestor-probe count is zero.
        }
        return measured.Cost;
    }

    private static Dictionary<string, StatementCost> PlanSqlCosts(string folder, Shape shape)
    {
        using var rig = NewRig(folder, new FakeSide(Side.Local), new FakeSide(Side.Dropbox));
        var entries = Enumerable.Range(0, shape.Files).Select(index => new Entry(
            PathNames.LocalSpelling(PathOf(index, shape)), false, 1, BaseTime, "local:" + NativeId(index, shape),
            "", "", null)).OrderBy(entry => PathNames.Key(entry.Path), StringComparer.Ordinal).ToArray();
        var saved = entries.Select(entry => Massdex.SyncShark.Engine.Executor.SyncedEntries.From(entry,
            entry with { Id = "remote:" + entry.Id })).ToArray();
        var savedByKey = saved.ToDictionary(record => record.LocalKey, StringComparer.Ordinal);
        rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with { Record = saved });
        var path = Path.Combine(folder, "probe" + StateSchema.PlanFileSuffix);
        using var probe = new CostProbe(":memory:").AttachPlan(rig.Database, path);
        var phases = new Dictionary<string, StatementCost>();
        var root = PathNames.Key(TreeRoot);
        var common = new (string Name, object? Value)[]
        {
            (StateSchema.ParamPair, rig.Pair.PairId), (StateSchema.ParamSide, (int)Side.Local),
            (StateSchema.ParamOwnKey, root), (StateSchema.ParamTargetKey, root)
        };
        void Add(string phase, StatementCost cost) => phases[phase] = phases.GetValueOrDefault(phase) + cost;
        probe.Begin();
        foreach (var entry in entries)
        {
            var key = PathNames.Key(entry.Path);
            Add("capture", probe.Execute(StateSchema.ScanUpsert,
            (StateSchema.ParamScan, Guid.Empty.ToByteArray()),
                (StateSchema.ParamPair, rig.Pair.PairId), (StateSchema.ParamSide, (int)Side.Local),
                (StateSchema.ParamJoinKey, key), (StateSchema.ParamOwnKey, key), (StateSchema.ParamPath, entry.Path),
                (StateSchema.ParamIsFolder, entry.IsFolder), (StateSchema.ParamSize, entry.Size),
                (StateSchema.ParamMtime, entry.Modified.UtcTicks),
                (StateSchema.ParamId, StateEncoding.IdFor(Side.Local, entry.Id)),
                (StateSchema.ParamRev, entry.Revision), (StateSchema.ParamHash, StateEncoding.Hash(entry.ContentHash)),
                (StateSchema.ParamSkip, entry.Skip)));
        }
        probe.Commit();
        probe.Begin();
        var after = "";
        foreach (var entry in entries)
        {
            Add("candidates", probe.ExecuteFirst(StateSchema.PlanPresent,
                ((string Name, object? Value)[])[(StateSchema.ParamScan, Guid.Empty.ToByteArray()), .. common, (StateSchema.ParamAfter, after)]));
            after = PathNames.Key(entry.Path);
        }
        Add("candidates", probe.ExecuteFirst(StateSchema.PlanPresent,
            ((string Name, object? Value)[])[(StateSchema.ParamScan, Guid.Empty.ToByteArray()), .. common, (StateSchema.ParamAfter, after)]));
        Add("anti-join", probe.ExecuteFirst(StateSchema.PlanSaved,
            ((string Name, object? Value)[])[(StateSchema.ParamScan, Guid.Empty.ToByteArray()), .. common, (StateSchema.ParamAfter, root), (StateSchema.PlanParamSavedOverride, false)]));
        var whole = phases.Values.Aggregate(default(StatementCost), (sum, cost) => sum + cost);
        Check.Require(whole.ProbePrepares == 2L * entries.Length + 2 && whole.ChangedRows == entries.Length,
            "executed all-linked plan SQL is 2M+2 statements with M fresh capture inserts");
        Check.Require(phases["candidates"].PairSourceOccurrences > 0,
            "executed plan candidate text reports its durable pair-subquery source occurrences");

        // Freeze every child once, then replay the flat partial recipe: every non-survivor is selected once.
        var survivor = entries[0];
        var survivorKey = PathNames.Key(survivor.Path);
        foreach (var (entry, index) in entries.Select((entry, index) => (entry, index)))
        {
            var key = PathNames.Key(entry.Path);
            var record = savedByKey[key];
            Add("classification", probe.Execute(StateSchema.ClassifiedInsert,
                (StateSchema.ParamPolicyKey, key), (StateSchema.ParamNativeKey, key),
                (StateSchema.PlanParamPresent, true), (StateSchema.ParamEntryPath, entry.Path),
                (StateSchema.ParamEntryFolder, false), (StateSchema.ParamEntrySize, entry.Size),
                (StateSchema.ParamEntryMtime, entry.Modified.UtcTicks),
                (StateSchema.ParamEntryId, StateEncoding.IdFor(Side.Local, entry.Id)),
                (StateSchema.ParamEntryRev, entry.Revision),
                (StateSchema.ParamEntryHash, StateEncoding.Hash(entry.ContentHash)),
                (StateSchema.ParamEntrySkip, null), (StateSchema.ParamSavedLocalKey, record.LocalKey),
                (StateSchema.ParamSavedLocalPath, record.LocalPath),
                (StateSchema.ParamSavedDropboxDelta, record.DropboxKey == record.LocalKey ? null : record.DropboxKey),
                (StateSchema.ParamSavedDropboxPathDelta,
                    record.DropboxPath == record.LocalPath ? null : record.DropboxPath),
                (StateSchema.ParamSavedFolder, record.IsFolder),
                (StateSchema.ParamSavedLocalId, StateEncoding.LocalId(record.LocalId)),
                (StateSchema.ParamSavedSize, record.LocalSize),
                (StateSchema.ParamSavedMtime, record.LocalModifiedTicks),
                (StateSchema.ParamSavedDropboxId, record.DropboxId),
                (StateSchema.ParamSavedHash, StateEncoding.Hash(record.DropboxContentHash)),
                (StateSchema.ParamVerdict, (index == 0 ? FolderVerdict.Survives : FolderVerdict.Removed).ToString()),
                (StateSchema.ParamFollowUp, index == 0)));
        }
        var removed = entries.Length - 1;
        var depth = -PathNames.Depth(entries[1].Path);
        Add("depth pages", probe.ExecuteFirst(StateSchema.ClassifiedNextDepth, (StateSchema.ParamNegativeDepth, 0)));
        var cursorKey = "";
        var cursorPresent = StateSchema.BeforeFirstPresence;
        for (var index = 1; index < entries.Length; index++)
        {
            Add("removed pages", probe.ExecuteFirst(StateSchema.ClassifiedRemoved,
                (StateSchema.ParamNegativeDepth, depth), (StateSchema.ParamPolicy, cursorKey),
                (StateSchema.ParamNative, cursorKey), (StateSchema.PlanParamPresent, cursorPresent),
                (StateSchema.ParamTake, StateSchema.OneRow)));
            var key = PathNames.Key(entries[index].Path);
            var (low, high) = PathNames.DescendantBounds(key);
            Add("survivor probes", probe.ExecuteFirst(StateSchema.ClassifiedHasSurvivor,
                (StateSchema.ParamJoinKey, key), (StateSchema.ParamLow, low), (StateSchema.ParamHigh, high)));
            Add("selection", probe.Execute(StateSchema.ClassifiedSelect, (StateSchema.ParamId, index + 1L)));
            cursorKey = key;
            cursorPresent = 1;
        }
        Add("removed pages", probe.ExecuteFirst(StateSchema.ClassifiedRemoved,
            (StateSchema.ParamNegativeDepth, depth), (StateSchema.ParamPolicy, cursorKey),
            (StateSchema.ParamNative, cursorKey), (StateSchema.PlanParamPresent, cursorPresent),
            (StateSchema.ParamTake, StateSchema.OneRow)));
        Add("depth pages", probe.ExecuteFirst(StateSchema.ClassifiedNextDepth,
            (StateSchema.ParamNegativeDepth, depth)));
        Check.Require(phases["classification"].ChangedRows == entries.Length &&
            phases["selection"].ChangedRows == removed &&
            phases["removed pages"].ProbePrepares == removed + 1,
            "classification freezes every child once, selection marks every removed branch once and its " +
            "keyset pages are M-1 plus one terminal empty page");
        probe.Commit();

        // Selected-branch and survivor readbacks each cost their rows plus one terminal page.
        Add("selected pages", probe.ExecuteFirst(StateSchema.ClassifiedSelectedFirst,
            (StateSchema.ParamTake, StateSchema.OneRow)));
        // ClassifiedSelectedFirst already returned the first selected child; resume after it.
        cursorKey = PathNames.Key(entries[1].Path);
        cursorPresent = 1;
        for (var index = 1; index < removed; index++)
        {
            Add("selected pages", probe.ExecuteFirst(StateSchema.ClassifiedSelected,
                (StateSchema.ParamNegativeDepth, depth), (StateSchema.ParamPolicy, cursorKey),
                (StateSchema.ParamNative, cursorKey), (StateSchema.PlanParamPresent, cursorPresent),
                (StateSchema.ParamTake, StateSchema.OneRow)));
            cursorKey = PathNames.Key(entries[index + 1].Path);
            cursorPresent = 1;
        }
        Add("selected pages", probe.ExecuteFirst(StateSchema.ClassifiedSelected,
            (StateSchema.ParamNegativeDepth, depth), (StateSchema.ParamPolicy, cursorKey),
            (StateSchema.ParamNative, cursorKey), (StateSchema.PlanParamPresent, cursorPresent),
            (StateSchema.ParamTake, StateSchema.OneRow)));
        foreach (var key in new[] { "", survivorKey })
            Add("survivor pages", probe.ExecuteFirst(StateSchema.ClassifiedSurvivors,
                (StateSchema.ParamPolicy, key), (StateSchema.ParamNative, key),
                (StateSchema.PlanParamPresent, key.Length == 0 ? StateSchema.BeforeFirstPresence : 1),
                (StateSchema.ParamTake, StateSchema.OneRow)));
        Check.Require(phases["selected pages"].ProbePrepares == removed + 1 &&
            phases["survivor pages"].ProbePrepares == 2,
            "selected and survivor readbacks visit each row once plus one terminal empty page");
        var partial = phases.Values.Aggregate(default(StatementCost), (sum, cost) => sum + cost);
        Console.WriteLine("  executed SQL recipe totals: whole=" + whole.ProbePrepares +
            " classified-replay=" + partial.ProbePrepares + "; not a trace of production statements");
        // The flat recipe needs no ancestor or unreadable probes; exercise their SQL separately, not in its ceiling.
        var (rootLow, rootHigh) = PathNames.DescendantBounds(survivorKey);
        Add("ancestor probe", probe.ExecuteFirst(StateSchema.ClassifiedHasSelected,
            (StateSchema.ParamJoinKey, root)));
        Add("guard", probe.ExecuteFirst(StateSchema.ClassifiedLocalRootForLocal, (StateSchema.ParamJoinKey, survivorKey)));
        Add("guard", probe.ExecuteFirst(StateSchema.ClassifiedDropboxRootForLocal, (StateSchema.ParamJoinKey, survivorKey)));
        Add("guard", probe.ExecuteFirst(StateSchema.ClassifiedLocalBelowForLocal,
            (StateSchema.ParamLow, rootLow), (StateSchema.ParamHigh, rootHigh)));
        Add("guard pair", probe.ExecuteFirst(StateSchema.PairFence, (StateSchema.ParamPair, rig.Pair.PairId)));
        // shortcut: this guard probe covers plan indexes, not a complete non-empty unreadable traversal; extend with that workload.
        Add("inventory", probe.ExecuteFirst(StateSchema.PlanEntries,
            ((string Name, object? Value)[])[(StateSchema.ParamScan, Guid.Empty.ToByteArray()), .. common, (StateSchema.ParamAfter, "")]));
        Add("saved-only setup", probe.ExecuteFirst(StateSchema.ScanDeleteSelf,
            (StateSchema.ParamScan, Guid.Empty.ToByteArray()), (StateSchema.ParamPair, rig.Pair.PairId), (StateSchema.ParamSide, (int)Side.Local),
            (StateSchema.ParamTargetKey, survivorKey)));
        Add("saved-only", probe.ExecuteFirst(StateSchema.PlanSaved,
            ((string Name, object? Value)[])[(StateSchema.ParamScan, Guid.Empty.ToByteArray()), .. common, (StateSchema.ParamAfter, root), (StateSchema.PlanParamSavedOverride, false)]));
        Check.Require(phases["selected pages"].DepthCalls > 0,
            "selected branches execute the owner-backed depth callback");
        foreach (var (phase, cost) in phases)
            Console.WriteLine("plan SQL " + shape.Name + " " + phase + ": executed VM=" + cost.VmSteps +
                " pathkey=" + cost.PathKeyCalls + " depth=" + cost.DepthCalls +
                " changed=" + cost.ChangedRows + " probe-prepares=" + cost.ProbePrepares +
                " pair-source=" + cost.PairSourceOccurrences + " JSON-source=" + cost.JsonSourceOccurrences);
        Console.WriteLine("  probe plan retained bytes=" + FileSize(path) +
            "; production re-prepares each page; probe prepares are not production measurements");
        return phases;
    }

    private static Cost ManyFolderCosts(string folder, Shape shape)
    {
        var local = new FakeSide(Side.Local);
        var dropbox = new FakeSide(Side.Dropbox);
        Seed(local, dropbox, shape, changedTimes: 0);
        using var rig = NewRig(folder, local, dropbox);
        Check.Require(RepairAsync(rig, "seed").GetAwaiter().GetResult().Summary.Completed,
            "independent deletions start from a completed Repair");
        for (var top = 0; top < shape.TopFolders; top++)
        {
            var entry = dropbox.ReadOneAsync(TopPath(top, shape), CancellationToken.None).GetAwaiter().GetResult()!;
            dropbox.DeleteAsync(entry, null, CancellationToken.None).GetAwaiter().GetResult();
        }
        rig.Local.Reset();
        rig.Dropbox.Reset();
        var result = Measure(shape.Name + " independent folder deletions", shape, rig, "delete",
            folderPlans: shape.TopFolders);
        Check.Require(result.Summary.Completed && result.Summary.IssuesByKind.Count == 0 &&
            rig.Local.Deletes == shape.TopFolders && rig.Dropbox.Deletes == 0 &&
            rig.Counters.PlanPhases[nameof(FolderPlan.CaptureAsync)].Succeeded == shape.TopFolders,
            "each independent deferred root is planned and deleted exactly once");
        for (var top = 0; top < shape.TopFolders; top++)
            Check.Require(local.ReadOneAsync(TopPath(top, shape), CancellationToken.None).GetAwaiter().GetResult() is null,
                "every independent root is removed");
        return result.Cost;
    }

    private static Cost ExclusionCosts(string folder, Shape shape)
    {
        var local = new FakeSide(Side.Local);
        var dropbox = new FakeSide(Side.Dropbox);
        Seed(local, dropbox, shape, changedTimes: 0);
        using var original = NewRig(folder, local, dropbox);
        Check.Require(RepairAsync(original, "seed").GetAwaiter().GetResult().Summary.Completed,
            "dense exclusion starts from a completed identical Repair");
        ChangeLocal(local, shape, shape.Items / 100);
        var exclusion = new Exclusion(Side.Local, TreeRoot, Keep: false);
        var pair = original.Pair with { Exclusions = [exclusion] };
        original.Store.SavePair(pair);
        var rig = original with { Pair = pair, Scope = new PathScope(pair, [pair]) };
        rig.Local.Reset();
        rig.Dropbox.Reset();
        rig.Dropbox.RefuseStagePath = PathOf(0, shape);
        var failed = Measure(shape.Name + " dense exclusion failed child", shape, rig, "exclude-failed", async session =>
        {
            Check.Require(!await session.Exclusions.ApplyAsync(exclusion, null, CancellationToken.None), "one refused child leaves the exclusion pending");
        });
        var preservedCopies = rig.Dropbox.Publishes;
        Check.Require(preservedCopies == shape.Items / 100 - 1 && rig.Local.Deletes == 0 &&
            rig.Local.SubtreeListings == 1 &&
            failed.Summary.IssuesByKind.GetValueOrDefault(IssueKind.Failure.ToString()) == 1,
            "independent changed siblings finish after an early child failure, but the root is never removed");
        rig.Local.Reset();
        rig.Dropbox.Reset();
        var measured = Measure(shape.Name + " dense exclusion retry", shape, rig, "exclude", async session =>
        {
            Check.Require(await session.Exclusions.ApplyAsync(exclusion, null, CancellationToken.None), "dense exclusion completes");
        }, folderPlans: 1);
        Check.Require(rig.Counters.PlanPhases[nameof(FolderPlan.CaptureAsync)].Succeeded == 1 &&
            rig.Counters.PlanPhases[nameof(FolderPlan.CaptureAsync)].Items == shape.Items,
            "dense exclusion captures its known root and complete subtree once");
        // One preservation capture plus one pre-effect frozen-inventory validation; exact observations list no siblings.
        Check.Require(rig.Local.SubtreeListings == 2 && rig.Dropbox.SiblingListings == 0,
            "dense exclusion reuses its capture and validates frozen victims once before removal");
        // Preservation releases its permit before folder preparation takes the second; neither is nested.
        Check.Require(rig.Dropbox.Publishes == 1 && preservedCopies + rig.Dropbox.Publishes == shape.Items / 100 &&
            rig.Local.Deletes == 1 &&
            measured.Cost.Permits == 2 && measured.Summary.IssuesByKind.Count == 0,
            "dense exclusion copies only changed files before one root removal under sequential owner admission");
        Check.Require(local.ReadOneAsync(TreeRoot, CancellationToken.None).GetAwaiter().GetResult() is null &&
            dropbox.ReadOneAsync(TreeRoot, CancellationToken.None).GetAwaiter().GetResult() is not null,
            "dense exclusion removes only the excluded side");
        return measured.Cost;
    }

    private static async Task RealFailedListingCostsAsync(string folder, Shape shape)
    {
        await using var rig = await RepairChecks.ComposeRigAsync(folder);
        for (var index = 0; index < shape.Items; index++)
        {
            var name = "item-" + index.ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
            var path = Path.Combine(rig.Pair.LocalRoot, name);
            await File.WriteAllTextAsync(path, "content");
            File.SetLastWriteTimeUtc(path, BaseTime.UtcDateTime);
            rig.Wire.SeedFile(name, "content", BaseTime);
        }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            rig.Wire.Calls.Clear();
            rig.Wire.RequestException = route => route.EndsWith("/list_folder", StringComparison.Ordinal)
                ? new InventoryIncompleteException("synthetic incomplete listing", null) : null;
            using var log = rig.Log(folder, "failed-" + attempt);
            var summary = await rig.RunAsync(log, CancellationToken.None);
            Check.Require(!summary.Completed && summary.Effects.Count == 0 &&
                rig.Wire.AttemptedWritePaths.Count == 0,
                "real Repair contains failed listing with zero row effects or provider writes");
            Check.Require(rig.Wire.Calls.Count(route => route.EndsWith("/list_folder", StringComparison.Ordinal)) == 1 &&
                rig.Wire.Calls.Count(route => route.EndsWith("/get_metadata", StringComparison.Ordinal)) == 1 &&
                rig.Wire.Calls.Count(route => route.EndsWith("/get_current_account", StringComparison.Ordinal)) == 1 &&
                rig.Wire.Calls.Count == 3,
                "failed listing performs only one account/root check and one listing request");
            Console.WriteLine("failed listing real items=" + shape.Items + " attempt=" + attempt +
                " effects=0 HTTP=" + string.Join(",", rig.Wire.Calls));
        }
        rig.Wire.RequestException = null;
        using var recoveredLog = rig.Log(folder, "recovered");
        var recovered = await rig.RunAsync(recoveredLog, CancellationToken.None);
        Check.Require(recovered.Completed && rig.Wire.AttemptedWritePaths.Count == 0,
            "real Repair recovers failed acquisition without transferring equal content");
    }

    private static void FailedListingCosts(string folder, Shape shape)
    {
        RealFailedListingCostsAsync(Path.Combine(folder, "real-acquisition"), shape).GetAwaiter().GetResult();
        var local = new FakeSide(Side.Local);
        var dropbox = new FakeSide(Side.Dropbox);
        Seed(local, dropbox, shape, changedTimes: 0);
        using var rig = NewRig(folder, local, dropbox);
        Check.Require(RepairAsync(rig, "seed").GetAwaiter().GetResult().Summary.Completed,
            "adverse budget starts from a completed Repair");
        var changed = shape.Items / 100;
        ChangeLocal(local, shape, changed);
        rig.Local.Reset();
        rig.Dropbox.Reset();
        var recovered = Measure(shape.Name + " recovered listing", shape, rig, "recovered");
        recovered = recovered with { Cost = recovered.Cost with { ProviderCalls = recovered.Cost.ProviderCalls + 1 } };
        Check.Require(recovered.Summary.Completed && recovered.Cost.ProviderCalls == 6 * changed &&
            rig.Dropbox.Publishes == changed && recovered.Summary.Effects.GetValueOrDefault(
                EffectKind.Uploaded.ToString()) == changed,
            "recovery uploads each outstanding change exactly once");
        AssertScan(rig, shape.Items);
        rig.Local.Reset();
        rig.Dropbox.Reset();
        ChangeLocal(local, shape, changed, revision: 2);
        var heldPaths = ChangedIndices(shape, changed).Select(index =>
            PathNames.LocalSpelling(PathOf(index, shape))).ToArray();
        rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with
        {
            Raise = heldPaths.Select(path =>
            {
                var entry = local.ReadOneAsync(path, CancellationToken.None).GetAwaiter().GetResult()!;
                return (IssueKind.Failure, TextKey.FailDropboxNoPermission, path,
                    new IssueSubject([new IssueItem(Side.Local, path, entry.Id)], null));
            }).ToArray()
        });
        for (var attempt = 0; attempt < 2; attempt++)
        {
            rig.Local.Reset();
            rig.Dropbox.Reset();
            var held = Measure(shape.Name + " held changes", shape, rig, "held-" + attempt);
            Check.Require(held.Summary.Completed && held.Cost.ProviderCalls == 0 && held.Cost.Permits == 0 &&
                held.Summary.Effects.Count == 0 &&
                rig.Store.Issues(rig.Pair.PairId).Count(issue => IssuePolicy.Holds(issue.Kind, issue.Cause)) == changed,
                "repeated held runs preserve pending changes without hashes, transfers or permits");
        }
        rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with
        {
            Close = heldPaths.Select(path => (path, IssueClosure.AfterEffect)).ToArray()
        });
        rig.Local.Reset();
        rig.Dropbox.Reset();
        using var cancelled = new CancellationTokenSource();
        rig.Dropbox.CancelOnStage = cancelled;
        var interrupted = Measure(shape.Name + " cancelled during staging", shape, rig, "cancelled",
            ct: cancelled.Token);
        Check.Require(interrupted.Cancelled && rig.Dropbox.Stages == 1 && rig.Dropbox.Publishes == 0 &&
            interrupted.Summary.Effects.Count == 0,
            "mid-run cancellation reaches staging but publishes no content");
        rig.Dropbox.CancelOnStage = null;
    }

    private static byte[] Content(int index, bool changed) =>
        Enumerable.Repeat((byte)((index + (changed ? 1 : 0)) % 256), index % 256 + 1).ToArray();

    private static IEnumerable<string> FolderPaths(Shape shape)
    {
        if (shape.Rooted) yield return TreeRoot;
        for (var top = 0; top < shape.TopFolders; top++)
        {
            var parent = TopPath(top, shape);
            yield return parent;
            for (var sub = 0; sub < SubFolders; sub++)
            {
                // Reparenting replaces the last subfolder with Tree and redistributes its files, keeping N unchanged.
                if (shape.Rooted && top == shape.TopFolders - 1 && sub == SubFolders - 1) continue;
                var child = PathNames.Child(parent, "sub_" + sub.ToString("D2"));
                yield return child;
                if (shape.Deep) parent = child;
            }
        }
    }

    private static string TopPath(int top, Shape shape) =>
        PathNames.Child(shape.Rooted ? TreeRoot : "", "group_" + top.ToString("D2"));

    private static string PathOf(int index, Shape shape)
    {
        var parent = TreeRoot;
        if (shape.TopFolders != 0)
        {
            var top = index % shape.TopFolders;
            var sub = index / shape.TopFolders % SubFolders;
            if (shape.Rooted && top == shape.TopFolders - 1 && sub == SubFolders - 1) sub--;
            parent = TopPath(top, shape);
            for (var level = shape.Deep ? 0 : sub; level <= sub; level++)
                parent = PathNames.Child(parent, "sub_" + level.ToString("D2"));
        }
        // Every generated name is Windows-transferable: NAME-SKIP's invalid-name budget case is separate below.
        var name = "file_" + index.ToString("D5") + "_";
        return PathNames.Child(parent, name.PadRight(TypicalPathLength - parent.Length - 1 - ".txt".Length, 'x') + ".txt");
    }

    private static void ReportShape(Shape shape)
    {
        var paths = FolderPaths(shape).Concat(Enumerable.Range(0, shape.Files)
            .Select(index => PathNames.LocalSpelling(PathOf(index, shape)))).ToArray();
        var lengths = paths.Select(path => path.Length).Order().ToArray();
        Check.Require(paths.Length == shape.Items, "the generated workload includes every declared item");
        Console.WriteLine(shape.Name + " paths: min=" + lengths[0] + " median=" + lengths[lengths.Length / 2] +
            " max=" + lengths[^1] + " mean=" + lengths.Average().ToString("F1") + "; depths=" +
            string.Join(",", paths.GroupBy(PathNames.Depth).OrderBy(group => group.Key)
                .Select(group => group.Key + ":" + group.Count())) + "; file sizes=1..256 bytes; changed files=" +
            shape.Items / 100 + " (floor(items/100)); folders unchanged");
    }

    private static Rig NewRig(string folder, FakeSide local, FakeSide dropbox, bool seedSources = true)
    {
        var database = Path.Combine(folder, "engine", "state.db");
        var root = Path.Combine(folder, "roots", "BudgetFixture");
        Directory.CreateDirectory(root);
        var counters = new StoreCounters(StateStore.Open(database));
        var pair = new PairConfig("budget-pair", "budget-pair", root, "/BudgetFixture",
            "connection-a", [], [], FirstRunPolicy.NewestWins, true, null,
            new RootBinding(root, "/BudgetFixture", "connection-a",
                WindowsDirectoryListing.ReadIdentity(root),
                new DropboxRootIdentity("account-a", "ns-budget", "ns-budget", "folder-budget")));
        counters.SavePair(pair);
        // Numeric local/hash/admission budgets belong to the scan module's own ports.
        if (seedSources) SourceFixture.AcquireJournal(counters, pair);
        var countedLocal = new CountingSide(local);
        using var lease = PilotDirectoryLease.Open(root);
        return new Rig(folder, database, counters, pair, countedLocal, new CountingSide(dropbox),
            new FakeRecovery
            {
                Evidence = new DriveEvidence(lease.Identity.VolumeSerialNumber, null, null),
                Hash = countedLocal.ContentHashAsync
            }, new FakeAdmission { Level = 3 }, new PathScope(pair, [pair]));
    }

    private static async Task<(RepairSummary Summary, string LogPath, bool Cancelled)> RepairAsync(Rig rig, string runId,
        CancellationToken ct = default, Func<(Syncer Syncer, FolderSync Folders, ExclusionRunner Exclusions), Task>? operation = null)
    {
        using var log = RunLog.Open(rig.Folder, rig.Pair.PairId, runId, _ => { }, Settings.LogBufferCharacters);
        var scanRig = new ScanPassChecks.Rig(rig.Pair, rig.Store, rig.Local, rig.Dropbox,
            rig.Recovery, rig.Admission, rig.Scope);
        var started = DateTimeOffset.UtcNow;
        RepairSummary summary;
        var cancelled = false;
        try
        {
            if (operation is null)
                summary = await ScanPassChecks.RunRepairAsync(scanRig, log, Settings, ct: ct).ConfigureAwait(false);
            else
            {
                var token = rig.Store.BeginScan(rig.Pair.PairId,
                    rig.Store.ReadPairExecution(rig.Pair.PairId)!.Value.Generation, includeDeleted: false);
                await rig.Store.OpenScan(rig.Pair.PairId, Side.Local, ScanTestSettings.BatchRows).CompleteAsync();
                await rig.Store.OpenScan(rig.Pair.PairId, Side.Dropbox, ScanTestSettings.BatchRows).CompleteAsync();
                await ScanPassChecks.RunOperationAsync(scanRig, log, Settings, operation).ConfigureAwait(false);
                // Completion closes the harness scan/WAL lifecycle; issue counts still report failed operations.
                summary = new RepairSummary(runId, started, DateTimeOffset.UtcNow, true, false, null, false,
                    log.Effects, log.IssuesByKind, log.Path, log.HasFailed);
                log.Complete(summary);
                ScanPassChecks.CompleteScan(scanRig, rig.Store, token, log);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation has no returned Repair summary; retain log facts without completing or saving the run.
            summary = new RepairSummary(runId, started, DateTimeOffset.UtcNow, false, false,
                "cancelled", false, log.Effects, log.IssuesByKind, log.Path, log.HasFailed);
            cancelled = true;
        }
        return (summary, log.Path, cancelled);
    }

    private static int Recorded(RepairSummary summary) =>
        summary.Effects.GetValueOrDefault(EffectKind.Recorded.ToString());

    // One run, measured: bytes written come from the process I/O counters, the heap and working set from sampling while
    // the run executes (PeakWorkingSet64 is process-lifetime). Durable flushes are not observable through
    // IStateStore's public surface, which has no counter, so per-run flush counts are not reported.
    private static Measured Measure(string label, Shape shape, Rig rig, string runId,
        Func<(Syncer Syncer, FolderSync Folders, ExclusionRunner Exclusions), Task>? operation = null, CancellationToken ct = default, int folderPlans = 0)
    {
        long pageWriteLimit = 0;
        long planBytes = 0;
        var plansRemaining = folderPlans;
        if (folderPlans > 0)
        {
            using var connection = StateStore.Connect(rig.Database, SqliteOpenMode.ReadOnly);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA page_size;";
            var pageSize = (long)command.ExecuteScalar()!;
            command.CommandText = "SELECT count(*) FROM sqlite_master WHERE tbl_name = 'synced_entry' AND type IN ('table', 'index');";
            var trees = (long)command.ExecuteScalar()!;
            Check.Require(pageSize > 0 && trees > 0, "the folder write ceiling uses the store's page size and B-trees");
            // Random ids dirty at most one leaf per deleted row per B-tree; WAL plus checkpoint write each page.
            pageWriteLimit = pageSize * trees * 2;
            rig.Counters.BeforePlanDispose = () =>
            {
                // Capture-only exclusions and frozen removals retain committed scratch until disposal.
                var path = Directory.EnumerateFiles(Path.GetDirectoryName(rig.Database)!,
                    Path.GetFileName(rig.Database) + StateSchema.PlanFileSuffix + "*" + StateSchema.DatabaseExtension).Single();
                var bytes = FileSize(path);
                Check.Require(bytes > 0, "each folder plan has measurable bytes before deletion");
                planBytes += bytes;
                if (--plansRemaining == 0) rig.Counters.BeforePlanDispose = null;
            };
        }
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        rig.Counters.Reset();
        var admittedBefore = rig.Admission.Admitted;
        var completedBefore = rig.Admission.Completed;
        var stagedBefore = rig.Admission.StageAcquired;
        var stageReportsBefore = rig.Admission.StageReports;
        var cpuBefore = process.TotalProcessorTime;
        var writtenBefore = Io().WriteTransferCount;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var sqlCounts = new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        SqliteConnection? traced = null;
        if (folderPlans > 0)
        {
            var inner = typeof(StoreCounters).GetFields(System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic).Single(field => field.FieldType == typeof(IStateStore))
                .GetValue(rig.Counters)!;
            traced = (SqliteConnection)typeof(StateStore).GetField("_connection",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(inner)!;
            SQLitePCL.raw.sqlite3_trace(traced.Handle, (SQLitePCL.delegate_trace)((_, sql) =>
            {
                var match = System.Text.RegularExpressions.Regex.Match(sql.utf8_to_string(),
                    @"^\s*(INSERT(?:\s+OR\s+\w+)?\s+INTO|UPDATE|DELETE\s+FROM|BEGIN|COMMIT|ROLLBACK)\s*([\w.]+)?",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                    sqlCounts.AddOrUpdate(match.Groups[1].Value.ToUpperInvariant() + " " + match.Groups[2].Value,
                        1, static (_, count) => count + 1);
            }), null);
        }
        var sampler = new Sampler();
        var watch = Stopwatch.StartNew();
        (RepairSummary Summary, string LogPath, bool Cancelled) run;
        (long Managed, long WorkingSet) peaks;
        try
        {
            run = RepairAsync(rig, runId, ct, operation).GetAwaiter().GetResult();
        }
        finally
        {
            watch.Stop();
            peaks = sampler.Stop();
            rig.Counters.BeforePlanDispose = null;
            if (traced is not null)
                SQLitePCL.raw.sqlite3_trace(traced.Handle, (SQLitePCL.delegate_trace)null!, null);
            Console.WriteLine("actual durable SQL " + label + ": " + string.Join(", ", sqlCounts.OrderBy(entry => entry.Key)
                .Select(entry => entry.Key + "=" + entry.Value)));
        }
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        // An upload takes the stage permit and its staged report, not the ordinary permit/completion
        // (RepairChecks uses the same FakeAdmission contract); sum both kinds as the outer permit/completion.
        var permits = rig.Admission.Admitted - admittedBefore + rig.Admission.StageAcquired - stagedBefore;
        var completions = rig.Admission.Completed - completedBefore + rig.Admission.StageReports - stageReportsBefore;
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuBefore;
        var written = (long)(Io().WriteTransferCount - writtenBefore);

        var database = FileSize(rig.Database);
        var wal = FileSize(rig.Database + "-wal");
        var log = FileSize(run.LogPath);
        var scan = Directory.EnumerateFiles(Path.GetDirectoryName(rig.Database)!,
            Path.GetFileName(rig.Database) + StateSchema.ScanFileSuffix + "*" + StateSchema.DatabaseExtension)
            .Sum(FileSize);
        var calls = ProviderCalls(rig);
        var hashes = rig.Local.Hashes + rig.Dropbox.Hashes;
        var items = shape.Items;

        Console.WriteLine(label + " (items=" + items + ")");
        Row("metric", "total", "per item");
        Row("elapsed", watch.Elapsed.TotalSeconds.ToString("F2") + " s", MillisecondsPerItem(watch.Elapsed, items) + " ms");
        Row("provider calls", calls.ToString(), PerItem(calls, items));
        Row("hashes", hashes.ToString(), PerItem(hashes, items));
        Row("database", Bytes(database), PerItem(database, items) + " B");
        Row("write log", Bytes(wal), PerItem(wal, items) + " B");
        Row("run log", Bytes(log), PerItem(log, items) + " B");
        Row("scan after run", Bytes(scan), PerItem(scan, items) + " B");
        Row("written to disk", Bytes(written), PerItem(written, items) + " B");
        Check.Require(plansRemaining == 0, "the folder write ceiling measures every expected plan");
        double? writtenLimit = folderPlans > 0 ? pageWriteLimit + (double)planBytes / items : null;
        if (writtenLimit is { } limit)
        {
            Row("plans before delete", Bytes(planBytes), PerItem(planBytes, items) + " B");
            Row("write ceiling", "per item", limit.ToString("F1") + " B");
        }
        Row("allocated", Bytes(allocated), PerItem(allocated, items) + " B");
        Row("store commands", rig.Counters.Commands.ToString(), PerItem(rig.Counters.Commands, items));
        Check.Require(rig.Counters.MemberCalls.Values.Sum() == rig.Counters.Commands,
            "per-member attribution accounts for every top-level store call");
        Console.WriteLine("  store members: " + string.Join(", ", rig.Counters.MemberCalls
            .OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => item.Key + "=" + item.Value)));
        Row("commit attempts", rig.Counters.CommitCalls.ToString(), PerItem(rig.Counters.CommitCalls, items));
        Row("Record submissions", rig.Counters.RecordCommitCalls.ToString(), PerItem(rig.Counters.RecordCommitCalls, items));
        Row("visible Records", rig.Counters.RecordCommittedMembers.ToString(), PerItem(rig.Counters.RecordCommittedMembers, items));
        Row("visibility groups*", rig.Counters.ObservedCommittedGroups.ToString(), "derived");
        Row("permits", permits.ToString(), PerItem(permits, items));
        Row("effect completions", completions.ToString(), PerItem(completions, items));
        Row("scan accepted", rig.Counters.ScanAccepted.ToString(), PerItem(rig.Counters.ScanAccepted, items));
        Row("scan applied*", rig.Counters.ScanAppliedOnCompletion.ToString(), "on completion");
        foreach (var (phase, counts) in rig.Counters.PlanPhases.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            Console.WriteLine("  plan " + phase + ": attempts=" + counts.Attempts + " succeeded=" +
                counts.Succeeded + " items=" + counts.Items);
        Row("plan statements*", rig.Counters.DerivedPlanDataStatements.ToString(),
            rig.Counters.PlanStatementCountIncomplete ? "partial recipe" : "derived recipe");
        Console.WriteLine("  * Derived Q: groups may merge transactions or split notifications; not a transaction or flush count.");
        Console.WriteLine("  * Failed/aborted scan applied/discarded events and failed plan SQL are unavailable.");
        Console.WriteLine("  Pair/JSON execution counts, locks, Observer allocations, exact native/queue peaks and flush counts: unavailable here.");
        // shortcut: process peaks do not prove back-pressure; add producer/consumer queue instrumentation before claiming it.
        // shortcut: fake listings have no pages; measure per-page admission with the provider adapter.
        Console.WriteLine("  Per-file cumulative writes, phase-boundary scan/plan bytes, transfer bytes and producer/consumer-only costs: unavailable here.");
        Row("cpu", cpu.TotalSeconds.ToString("F2") + " s", MillisecondsPerItem(cpu, items) + " ms");
        Row("peak managed heap", Bytes(peaks.Managed), PerItem(peaks.Managed, items) + " B");
        Row("peak working set", Bytes(peaks.WorkingSet), PerItem(peaks.WorkingSet, items) + " B");
        Console.WriteLine("  " + Counts("local", rig.Local) + "; " + Counts("dropbox", rig.Dropbox));

        return new Measured(run.Summary,
            new Cost(items, database, wal, calls, hashes, written, peaks.Managed, allocated,
                rig.Counters.Commands, rig.Counters.CommitCalls, permits, completions, watch.Elapsed, writtenLimit)
                { Members = rig.Counters.MemberCalls.ToDictionary(entry => entry.Key, entry => entry.Value) }, run.Cancelled);
    }

    private static int ProviderCalls(Rig rig) =>
        rig.Local.Reads + rig.Dropbox.Reads + rig.Local.Hashes + rig.Dropbox.Hashes +
        rig.Local.Work + rig.Dropbox.Work;

    private static string Counts(string name, CountingSide side) =>
        name + " list=" + side.Listings + " read=" + side.Reads + " hash=" + side.Hashes + " open=" + side.Opens +
        " stage=" + side.Stages + " publish=" + side.Publishes + " ensure=" + side.Ensures +
        " delete=" + side.Deletes + " move=" + side.Moves;

    private static long FileSize(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? file.Length : 0;
    }

    private static string PerItem(long value, int items) => (value / (double)items).ToString("F1");

    private static string MillisecondsPerItem(TimeSpan elapsed, int items) =>
        (elapsed.TotalMilliseconds / items).ToString("F3");

    private static string Bytes(long bytes)
    {
        if (bytes >= 1_000_000) return (bytes / 1_000_000.0).ToString("F1") + " MB";
        if (bytes >= 1_000) return (bytes / 1_000.0).ToString("F1") + " kB";
        return bytes + " B";
    }

    private static void Row(string name, string total, string perItem) =>
        Console.WriteLine("  " + name.PadRight(18) + total.PadLeft(12) + perItem.PadLeft(14));

    // Bytes written to disk by the whole process: the delta around one run is that run's write volume (nothing else in
    // this check writes to disk while a run is in progress).
    private static IoCounters Io()
    {
        Check.Require(GetProcessIoCounters(GetCurrentProcess(), out var counters),
            "the process I/O counters are readable");
        return counters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr processHandle, out IoCounters counters);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    // Samples the managed heap and the working set while a run executes; the working-set peak is not available per run
    // from Process.PeakWorkingSet64, which spans the process's whole life.
    internal sealed class Sampler
    {
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly object _gate = new();
        private readonly Timer _timer;
        private long _peakManaged;
        private long _peakWorkingSet;
        private int _stopped;

        public Sampler()
        {
            _peakManaged = GC.GetTotalMemory(false);
            _process.Refresh();
            _peakWorkingSet = _process.WorkingSet64;
            _timer = new Timer(_ => Sample(), null, SampleMilliseconds, SampleMilliseconds);
        }

        public (long Managed, long WorkingSet) Stop()
        {
            lock (_gate)
            {
                if (_stopped == 0)
                {
                    _timer.Dispose();
                    Sample();
                    _stopped = 1;
                    _process.Dispose();
                }
                return (_peakManaged, _peakWorkingSet);
            }
        }

        private void Sample()
        {
            // Process.Refresh and its cached properties are not safe across overlapping timer and Stop callbacks.
            lock (_gate)
            {
                if (_stopped != 0) return;
                Track(ref _peakManaged, GC.GetTotalMemory(false));
                _process.Refresh();
                Track(ref _peakWorkingSet, _process.WorkingSet64);
            }
        }

        private static void Track(ref long peak, long value)
        {
            if (value > Interlocked.Read(ref peak)) Interlocked.Exchange(ref peak, value);
        }
    }

    /// <summary>One run's measured cost, compared with the same case at the other size.</summary>
    private sealed record Cost(int Items, long DatabaseBytes, long WalBytes,
        int ProviderCalls, int Hashes, long WrittenBytes, long PeakManagedBytes, long AllocatedBytes,
        long Commands, long CommitCalls, int Permits, int Completions, TimeSpan Elapsed, double? WrittenBytesPerItemLimit)
    {
        public IReadOnlyDictionary<string, long> Members { get; init; } = new Dictionary<string, long>();
        public int? UnchangedFiles { get; init; }
        public double DatabaseBytesPerItem => (double)DatabaseBytes / Items;
        public double WrittenBytesPerItem => (double)WrittenBytes / Items;
        public double PeakManagedBytesPerItem => (double)PeakManagedBytes / Items;
        public double AllocatedBytesPerItem => (double)AllocatedBytes / Items;
    }

    /// <summary>One workload shape: its file count, its top-folder count and the items they make with the subfolders.</summary>
    private sealed record Shape(string Name, int Files, int TopFolders, bool Rooted = false, bool Deep = false)
    {
        public int Folders => TopFolders == 0 ? 1 : TopFolders * (1 + SubFolders);
        public int Items => Files + Folders;
    }

    private sealed record Measured(RepairSummary Summary, Cost Cost, bool Cancelled);

    private sealed record Rig(string Folder, string Database, StoreCounters Counters, PairConfig Pair,
        CountingSide Local, CountingSide Dropbox, FakeRecovery Recovery, FakeAdmission Admission, PathScope Scope)
        : IDisposable
    {
        public IStateStore Store => Counters;
        public void Dispose() => Counters.Dispose();
    }

    /// <summary>Counts every provider call on one side; the doubles themselves do not.</summary>
    private sealed class CountingSide(ISide inner) : ISide
    {
        private int _listings;
        private int _subtreeListings;
        private int _siblingListings;
        private int _reads;
        private int _hashes;
        private int _opens;
        private int _stages;
        private int _publishes;
        private int _ensures;
        private int _deletes;
        private int _moves;

        public Side Side => inner.Side;
        public int Listings => _listings;
        public int SubtreeListings => _subtreeListings;
        public int SiblingListings => _siblingListings;
        public int Reads => _reads;
        public int Hashes => _hashes;
        public int Opens => _opens;
        public int Stages => _stages;
        public int Publishes => _publishes;
        public int Ensures => _ensures;
        public int Deletes => _deletes;
        public int Moves => _moves;
        public bool FailSubtreeAfterListing { get; set; }
        public string? RefuseStagePath { get; set; }
        public CancellationTokenSource? CancelOnStage { get; set; }

        /// <summary>Every call that would copy, create or delete content.</summary>
        public int Work => _opens + _stages + _publishes + _ensures + _deletes + _moves;

        public void Reset() =>
            _listings = _subtreeListings = _siblingListings = _reads = _hashes = _opens = _stages =
                _publishes = _ensures = _deletes = _moves = 0;

        public async IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Interlocked.Increment(ref _listings);
            if (!options.Recursive) Interlocked.Increment(ref _siblingListings);
            else if (under.Length != 0) Interlocked.Increment(ref _subtreeListings);
            await foreach (var listed in inner.ReadAllAsync(under, options, ct)) yield return listed;
            if (FailSubtreeAfterListing && options.Recursive && under.Length != 0)
                throw new InventoryIncompleteException("synthetic late subtree failure", null);
        }

        public Task<Entry?> ReadOneAsync(string path, CancellationToken ct)
        {
            Interlocked.Increment(ref _reads);
            return inner.ReadOneAsync(path, ct);
        }

        public Task<Stream> OpenReadAsync(Entry entry, RequestAttempt? attempt, CancellationToken ct)
        {
            Interlocked.Increment(ref _opens);
            return inner.OpenReadAsync(entry, attempt, ct);
        }

        public Task<string> ContentHashAsync(Entry entry, CancellationToken ct)
        {
            Interlocked.Increment(ref _hashes);
            return inner.ContentHashAsync(entry, ct);
        }

        public Task<IStaged> StageAsync(string path, Stream content, Entry source, CancellationToken ct)
        {
            Interlocked.Increment(ref _stages);
            CancelOnStage?.Cancel();
            ct.ThrowIfCancellationRequested();
            if (RefuseStagePath == path)
            {
                RefuseStagePath = null;
                throw new ItemFailedException(path, TextKey.FailChanged, null);
            }
            return inner.StageAsync(path, content, source, ct);
        }

        public Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _publishes);
            return inner.PublishAsync(staged, expected, selection, ct);
        }

        public Task<Entry> EnsureFolderAsync(string path, CancellationToken ct)
        {
            Interlocked.Increment(ref _ensures);
            return inner.EnsureFolderAsync(path, ct);
        }

        public Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct)
        {
            Interlocked.Increment(ref _deletes);
            return inner.DeleteAsync(expected, selection, ct);
        }

        public Task<Entry> MoveAsync(Entry expected, string newPath, CancellationToken ct)
        {
            Interlocked.Increment(ref _moves);
            return inner.MoveAsync(expected, newPath, ct);
        }

        public Task<FolderSubmitResult> SubmitFolderRemovalAsync(FolderSubmission submission,
            IReadOnlyList<FolderRemovalEffect> effects, RequestAttempt attempt, CancellationToken ct) =>
            inner.SubmitFolderRemovalAsync(submission, effects, attempt, ct);

        public Task<FolderJobResult> ReadFolderRemovalAsync(string jobId, int expectedCount,
            RequestAttempt attempt, CancellationToken ct) =>
            inner.ReadFolderRemovalAsync(jobId, expectedCount, attempt, ct);
    }
}

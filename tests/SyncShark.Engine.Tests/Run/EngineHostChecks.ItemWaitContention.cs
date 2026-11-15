using System.Diagnostics;
using System.Globalization;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.Scenarios;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>G7 (B-161): contention between three pairs on one host. Pairs A and B run concurrent ordinary Repairs over
/// the W08 tree while pair C, with or without a pending item wait over K_C retained keys, receives one unrelated local
/// edit. It reports measurements only; tools/ci/Run-ItemWaitContention.ps1 interleaves baseline and candidate builds
/// and compares them. It fails only when the workload itself does not run.</summary>
public static partial class EngineHostChecks
{
    // KF W08: 10 top folders of 10 subfolders of 50 files, so 5,000 files and 110 folders: 5,110 items per pair.
    private const int ContentionTops = 10;
    private const int ContentionSubfolders = 10;
    private const int ContentionFiles = 50;
    private const int ContentionItems = ContentionTops * (1 + ContentionSubfolders * (1 + ContentionFiles));
    // DROP-5 G7: K_C, the keys pair C's item wait retains.
    private const int ContentionKeys = 1_000;
    private const string ContentionEdit = "g7-edit.txt";
    // The barrier between windows writes its own file, so a held window edit stays observable until it lands.
    private const string ContentionSettle = "g7-settle.txt";

    /// <summary>Not a CheckRegistry group, so no full CI run executes it: only the dispatch workflow
    /// item-wait-contention.yml does, through Program's --measurement switch. One process sets the three pairs up once,
    /// then runs both arms per repetition, alternating which arm runs first. G7_BUILD labels the lines;
    /// G7_REPETITIONS (default 1) sets the repetitions.</summary>
    internal static void RunItemWaitContention()
    {
        var build = Environment.GetEnvironmentVariable("G7_BUILD") ?? "unlabelled";
        var repetitions = int.TryParse(Environment.GetEnvironmentVariable("G7_REPETITIONS"), NumberStyles.None,
            CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : 1;
        using var scenario = Scenario.Begin("g7-item-wait-contention");
        // A skip prints no G7 row, which the runner refuses.
        if (scenario.SkipWhenScaled()) return;
        var a = scenario.Pair;
        var b = scenario.AddSecondPair("pair-b");
        var c = scenario.AddSecondPair("pair-c");
        foreach (var pair in new[] { a, b })
            for (var top = 0; top < ContentionTops; top++)
                for (var sub = 0; sub < ContentionSubfolders; sub++)
                    for (var file = 0; file < ContentionFiles; file++)
                        WriteContentionFile(pair, $"t{top}/s{sub}/f{file}.txt", $"w08 {top} {sub} {file}");
        for (var key = 0; key < ContentionKeys; key++) WriteContentionFile(c, RetainedName(key), "retained " + key);
        WriteContentionFile(c, ContentionEdit, "edit 0");
        WriteContentionFile(c, ContentionSettle, "settle 0");
        scenario.RepairAsync();
        scenario.SecondRepairAsync();
        scenario.SecondRepairAsync(c);
        scenario.CountedPair = c.PairId;
        scenario.Start();
        Console.WriteLine("G7 missing-evidence: host _gate hold time (no probe exists and none is added); Hk, the " +
            "HeldForRetry decisions that reach LiveItemWait (no call-site counter without a production hook). " +
            "read_item_retry_raw counts every ReadItemRetry through the fixture store, all callers and all three pairs; " +
            "c_item_retry_reads counts only pair C's, from every caller; c_progress_checks counts C's folder-job " +
            "progress checks, which AutoSync makes after an opportunity's work returns, and is not a dispatch count; " +
            "both are window totals, and one retained-key size is measured; " +
            "lock_contention_delta is the process-wide runtime count, not host _gate wait or hold time. W08 is " +
            "regression-size, not real-size evidence.");
        // The startup opportunities and journal must be quiet before the first window.
        SettleContentionAsync(scenario, c, pending: false, null, "startup").GetAwaiter().GetResult();
        for (var repetition = 1; repetition <= repetitions; repetition++)
            foreach (var pending in repetition % 2 == 1 ? new[] { true, false } : new[] { false, true })
                ContentionWindowAsync(scenario, a, b, c, pending, repetition, build).GetAwaiter().GetResult();
    }

    private static string RetainedName(int key) => $"retained/r{key:0000}.txt";

    private static void WriteContentionFile(PairConfig pair, string path, string content)
    {
        var full = Path.Combine(pair.LocalRoot, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    // One measured window. Every timestamp is a Stopwatch reading taken where the event happens: each Repair's start
    // and end, the journal page commit carrying C's edit notice, and the fixture's matching completion of C's upload.
    // The window is valid only when C's notice falls inside the intersection of A's and B's Repair intervals.
    private static async Task ContentionWindowAsync(Scenario scenario, PairConfig a, PairConfig b, PairConfig c,
        bool pending, int repetition, string build)
    {
        var arm = pending ? "pending-wait" : "no-wait";
        if (pending)
        {
            // A provider-named wait keeps C's deadline far beyond the window under the scenario's real clock.
            var keys = Enumerable.Range(0, ContentionKeys).Select(RetainedName).ToArray();
            await TemporaryTurnAsync(scenario.Host, c.PairId, keys[0], null, ItemRig.Far, null, then: keys[1..])
                .ConfigureAwait(false);
            Check.Require(scenario.Store.ReadItemRetry(c.PairId) is not null,
                "G7 setup: pair C holds a pending item wait over its retained keys");
        }
        var begunA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var begunB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.ScanBegun = pairId =>
        {
            if (pairId == a.PairId) begunA.TrySetResult();
            else if (pairId == b.PairId) begunB.TrySetResult();
        };
        long noticeAt = 0;
        scenario.NoticeCommitted = (pairId, notice) =>
        {
            if (pairId == c.PairId && notice.Path.EndsWith(ContentionEdit, StringComparison.OrdinalIgnoreCase))
                Interlocked.CompareExchange(ref noticeAt, Stopwatch.GetTimestamp(), 0);
        };
        var content = $"edit {build} {repetition} {arm}";
        var uploaded = scenario.ObserveCompletion(ContentionEdit, content);
        var contention = System.Threading.Monitor.LockContentionCount;
        var reads = scenario.ItemRetryReads;
        var (cReads, cChecks) = (scenario.CountedReads, scenario.CountedProgressChecks);
        var repairA = TimedRepairAsync(scenario.Host, a.PairId);
        var repairB = TimedRepairAsync(scenario.Host, b.PairId);
        await Task.WhenAll(begunA.Task, begunB.Task).WaitAsync(HookBound).ConfigureAwait(false);
        File.WriteAllText(Path.Combine(c.LocalRoot, ContentionEdit), content);
        var (startA, endA, completedA) = await repairA.ConfigureAwait(false);
        var (startB, endB, completedB) = await repairB.ConfigureAwait(false);
        // An upload still owed after both Repairs gets one hook bound; a held edit (the baseline's whole-pair hold)
        // does not land.
        await Task.WhenAny(uploaded, Task.Delay(HookBound)).ConfigureAwait(false);
        var contentionDelta = System.Threading.Monitor.LockContentionCount - contention;
        var readDelta = scenario.ItemRetryReads - reads;
        var (cReadDelta, cCheckDelta) = (scenario.CountedReads - cReads, scenario.CountedProgressChecks - cChecks);
        scenario.ScanBegun = null;
        scenario.NoticeCommitted = null;
        Check.Require(completedA && completedB, "G7 both ordinary Repairs complete");
        var notice = Interlocked.Read(ref noticeAt);
        Check.Require(notice != 0, "G7 setup: pair C's edit is committed as a journal notice");
        var upload = uploaded.IsCompletedSuccessfully ? uploaded.Result : 0;
        Check.Require(pending || upload != 0, "G7 with no item wait, pair C's edit uploads");
        var (overlapStart, overlapEnd) = (Math.Max(startA, startB), Math.Min(endA, endB));
        var noticeInOverlap = notice >= overlapStart && notice <= overlapEnd;
        var uploadInOverlap = upload != 0 && upload >= overlapStart && upload <= overlapEnd;
        var latency = upload == 0 ? "held" : Milliseconds(notice, upload);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"G7 build={build} arm={arm} rep={repetition} workload={(noticeInOverlap ? "valid" : "invalid")} " +
            $"overlap_ms={Milliseconds(overlapStart, overlapEnd)} c_notice_in_overlap={noticeInOverlap} " +
            $"c_upload_in_overlap={uploadInOverlap} c_latency_ms={latency} lock_contention_delta={contentionDelta} " +
            $"read_item_retry_raw={readDelta} repair_a_ms={Milliseconds(startA, endA)} " +
            $"repair_b_ms={Milliseconds(startB, endB)} items_per_pair={ContentionItems} " +
            $"retained_keys={(pending ? ContentionKeys : 0)} c_item_retry_reads={cReadDelta} c_progress_checks={cCheckDelta}"));
        await SettleContentionAsync(scenario, c, pending, uploaded, $"{build} {repetition} {arm}").ConfigureAwait(false);
    }

    // Negative when the interval is empty (for the overlap: the Repairs did not run at the same time).
    private static string Milliseconds(long from, long to) =>
        ((to - from) * 1000.0 / Stopwatch.Frequency).ToString("F1", CultureInfo.InvariantCulture);

    private static Task<(long Start, long End, bool Completed)> TimedRepairAsync(EngineHost host, string pairId) =>
        Task.Run(async () =>
        {
            var start = Stopwatch.GetTimestamp();
            var summary = await host.Operations.RepairAsync(pairId, Scenario.Cancellation).ConfigureAwait(false);
            return (start, Stopwatch.GetTimestamp(), summary.Completed);
        });

    // Between windows C's wait is cleared outside the host, as an ordinary Repair's FinishRepair clears it. A write to the
    // settle file then wakes C. The barrier is the settle upload, the window's own edit (a held one lands now) and a
    // successful completion of every distinct retained key, so no window inherits the previous one's work. The bound is
    // on progress, so the 1,000 retained completions are not raced against a clock.
    private static async Task SettleContentionAsync(Scenario scenario, PairConfig c, bool pending, Task? window,
        string label)
    {
        var missing = new HashSet<string>(StringComparer.Ordinal);
        if (pending)
            missing.UnionWith(Enumerable.Range(0, ContentionKeys).Select(key => PathNames.KeyFor(Side.Local, RetainedName(key))));
        scenario.WorkCompleted = selection =>
        {
            if (selection.PairId != c.PairId || selection.Side != Side.Local) return;
            lock (missing) missing.Remove(selection.Key);
        };
        try
        {
            // The observer is installed before the row is cleared, so no retained completion can land unobserved.
            var generation = scenario.Store.ReadPairExecution(c.PairId)!.Value.Generation;
            if (scenario.Store.ReadItemRetry(c.PairId) is { } row) scenario.Store.CompleteItemRetry(c.PairId, generation, row);
            var content = "settle " + label;
            var settled = scenario.ObserveCompletion(ContentionSettle, content);
            File.WriteAllText(Path.Combine(c.LocalRoot, ContentionSettle), content);
            var seen = (-1, false, false);
            var progress = Stopwatch.GetTimestamp();
            while (true)
            {
                int left;
                lock (missing) left = missing.Count;
                var now = (left, settled.IsCompleted, window?.IsCompleted ?? true);
                if (now is (0, true, true)) break;
                if (now != seen) (seen, progress) = (now, Stopwatch.GetTimestamp());
                else if (Stopwatch.GetElapsedTime(progress) >= HookBound)
                    throw new InvalidOperationException($"G7 setup: pair C settles before the next window ({label}; " +
                        $"{left} of {(pending ? ContentionKeys : 0)} retained keys not completed, settle uploaded " +
                        $"{now.Item2}, window edit uploaded {now.Item3})");
                await Task.Yield();
            }
            // The settle's last dispatch may still be running after its completions land; the next window's read and
            // dispatch samples start only once C's AutoSync has nothing running or queued.
            await UntilAsync(() => AutoSyncIdle(scenario.Host, c.PairId), "G7 setup: pair C's settle dispatch has ended")
                .ConfigureAwait(false);
        }
        finally { scenario.WorkCompleted = null; }
    }
}

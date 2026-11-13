using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Massdex.SyncShark.Engine.Run;

namespace Massdex.SyncShark.Engine.Tests.Scenarios;

/// <summary>B-205 offline reproduction: Phase 1 runs the live sequence through AutoSync (a NEW Local-only file first
/// sighted while held with no read share, released, never changed); Phase 2 asks production admission the question
/// the live logs could not answer, with the live ordering (older acquired work, later source-less episode). Both
/// print what they observed; neither asserts which arm fires, only that the state was reached.</summary>
public static partial class ScenarioChecks
{
    private const string B205Content = "held-at-creation";
    private const string B205ProbeContent = "held-at-probe";

    private static readonly (string Name, Action<Scenario> Body, string[] IgnoreRules)[] B205ReproCases =
    [
        ("b205-a-single-acquisition", BodyB205ReproSingle, []),
        ("b205-b-two-acquisitions", BodyB205ReproTwo, []),
        ("b205-c-emoji-parent", BodyB205ReproEmoji, []),
    ];

    private static IReadOnlyList<Action> B205ReproOfflineActions() =>
        B205ReproCases.Select(entry =>
            (Action)(() => Run(entry.Name, entry.Body, entry.IgnoreRules, throwOnFailure: true))).ToArray();

    // (a) one acquisition while held; (b) a second acquisition while still held; (c) two under an emoji parent.
    private static void BodyB205ReproSingle(Scenario scenario) =>
        BodyB205Repro(scenario, "a-single-acquisition", "held/held.txt", "held/probe.txt", false);

    private static void BodyB205ReproTwo(Scenario scenario) =>
        BodyB205Repro(scenario, "b-two-acquisitions", "held/held.txt", "held/probe.txt", true);

    private static void BodyB205ReproEmoji(Scenario scenario) =>
        BodyB205Repro(scenario, "c-emoji-parent", "Folder ❤️/held.txt", "Folder ❤️/probe.txt", true);

    private static void BodyB205Repro(Scenario scenario, string variant, string path, string probePath,
        bool secondAcquisition)
    {
        if (scenario.SkipWhenScaled()) return;
        var pairId = scenario.Pair.PairId;
        // A completed first Repair installs the sources the probe's acquisitions fence against.
        scenario.RepairAsync();
        if (scenario.Store.ReadPairExecution(pairId) is null || scenario.Store.ReadSource(pairId, Side.Local) is null)
            throw new InvalidOperationException("B-205 repro: the first Repair installed no pair sources");
        B205Probe(scenario, variant, probePath, secondAcquisition);
        B205EndToEnd(scenario, variant, path, secondAcquisition);
    }

    // The live sequence through AutoSync: held first sighting, a second acquisition while held, release, wakes.
    private static void B205EndToEnd(Scenario scenario, string variant, string path, bool secondAcquisition)
    {
        var ct = Scenario.Cancellation;
        var pairId = scenario.Pair.PairId;
        var inUse = scenario.ObserveInUseFailure();
        scenario.Start();
        using var held = CreateHeldFile(scenario, path, B205Content);
        var episode = B205HeldFailure(scenario, variant, inUse);
        var identity = episode.Token.Subject.Items
            .FirstOrDefault(item => item.Side == Side.Local && item.Path == path)?.Id
            ?? throw new InvalidOperationException("B-205 repro: the in-use episode names no local identity");
        scenario.Require(!scenario.DropboxFiles().ContainsKey(path),
            "B-205 repro " + variant + ": the held file must not upload while it is held");
        scenario.Require(InUseNotices(scenario).Any(issue => PathNames.Key(issue.Path) == PathNames.Key(path)),
            "B-205 repro " + variant + ": the in-use failure raised no visible notice");
        var heldRefusals = B205SecondHeldAcquisition(scenario, path, secondAcquisition);
        // The app releases it and the file never changes again. A fresh read is the release proof.
        held.Dispose();
        var released = scenario.Local.ReadOneAsync(path, ct).GetAwaiter().GetResult();
        scenario.Require(released is not null && released.Id == identity,
            "B-205 repro " + variant + ": the file is readable under the same identity after release");
        B205ReleasedWakes(scenario, pairId);
        // A Repair is the permitted opportunity the live run used; its outcome separates a stuck path from a stuck host.
        for (var pass = 1; pass <= 2; pass++)
            scenario.RepairAsync(requireCompleted: false);
        scenario.Host.PauseAsync(pairId, ct).GetAwaiter().GetResult();
        ReportB205(scenario, variant, path, identity, episode.Token.WorkId, heldRefusals);
    }

    private static ItemRetryCandidate B205HeldFailure(Scenario scenario, string variant,
        Task<ItemRetryCandidate> inUse)
    {
        try { return inUse.WaitAsync(TimeSpan.FromSeconds(60), Scenario.Cancellation).GetAwaiter().GetResult(); }
        catch (TimeoutException)
        {
            scenario.Require(false, "B-205 repro " + variant + ": the held file never produced an in-use failure");
            throw;
        }
    }

    private static int B205SecondHeldAcquisition(Scenario scenario, string path, bool secondAcquisition)
    {
        var heldRefusals = B205Refusals(scenario).Count(detail => B205Names(detail, path));
        if (!secondAcquisition) return heldRefusals;
        var passes = scenario.EndedWorkPasses;
        // A second change event arrives while the file is still held, the later acquisition of the live race.
        scenario.LocalFile("held-wake/second.txt", "second acquisition while held");
        B205WaitFor(scenario, () => scenario.EndedWorkPasses > passes, 30);
        return B205Refusals(scenario).Count(detail => B205Names(detail, path));
    }

    private static void B205ReleasedWakes(Scenario scenario, string pairId)
    {
        var ct = Scenario.Cancellation;
        for (var wake = 1; wake <= 3; wake++)
        {
            scenario.Host.PauseAsync(pairId, ct).GetAwaiter().GetResult();
            scenario.LocalFile("unrelated/wake-" + wake.ToString(CultureInfo.InvariantCulture) + ".txt",
                "wake-" + wake.ToString(CultureInfo.InvariantCulture));
            scenario.Host.CatchUpLocalAsync(pairId, scenario.Store.ReadPairExecution(pairId)!.Value.Generation, ct)
                .GetAwaiter().GetResult();
            var passes = scenario.EndedWorkPasses;
            scenario.Host.ResumeAsync(pairId, ct).GetAwaiter().GetResult();
            B205WaitFor(scenario, () => scenario.EndedWorkPasses > passes, 30);
        }
    }

    /// <summary>The live ordering the logs could not prove: an older acquired notice work, then a source-less in-use
    /// episode on a later work (the journal item-failure path), then admission for the older, already released work.</summary>
    private static void B205Probe(Scenario scenario, string variant, string path, bool secondAcquisition)
    {
        var ct = Scenario.Cancellation;
        var store = scenario.Store;
        var pairId = scenario.Pair.PairId;
        scenario.LocalFile(path, B205ProbeContent);
        var entry = scenario.Local.ReadOneAsync(path, ct).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("B-205 probe: the arranged file cannot be observed");
        using var held = scenario.DenyLocalContent(path);
        var generation = store.ReadPairExecution(pairId)!.Value.Generation;
        var local = store.ReadSource(pairId, Side.Local)!.Value;
        var volume = scenario.Pair.Roots!.Local.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture);
        var position = store.ReadJournal(volume)
            ?? throw new InvalidOperationException("B-205 probe: the first Repair recorded no journal position");
        var acquired = store.AcquireJournalPage(volume, position, position,
            [(pairId, generation, local.Epoch, local.Revision, false)],
            B205Notices(pairId, path, entry, secondAcquisition))
            ?? throw new InvalidOperationException("B-205 probe: the journal notice was refused");
        var selection = store.SelectWork(pairId, generation, scenario.Scope)
            ?? throw new InvalidOperationException("B-205 probe: the acquired notice is not selectable");
        Check.Require(store.BeginSelection(selection) is not null, "B-205 probe: the acquired notice admitted");
        try
        {
            var episodeWork = B205ProbeEpisode(scenario, pairId, path, entry.Id, local.Epoch);
            // The probe admits a RELEASED file: the stored episode, not the open handle, is what refuses it.
            held.Dispose();
            Check.Require(scenario.Local.ReadOneAsync(path, ct).GetAwaiter().GetResult()?.Id == entry.Id,
                "B-205 probe: the file is readable before admission");
            B205ProbeAdmit(scenario, variant, path, selection, acquired, position, episodeWork);
        }
        finally { store.EndSelection(selection); }
        scenario.RemoveLocal(path);
    }

    private static (string PairId, ChangeNotice Notice)[] B205Notices(string pairId, string path, Entry entry,
        bool secondAcquisition)
    {
        ChangeNotice Notice(ChangeKind kind) => new(Side.Local, kind, path, null, entry.Id, entry, false, false);
        return secondAcquisition
            ? [(pairId, Notice(ChangeKind.Changed)), (pairId, Notice(ChangeKind.Changed))]
            : [(pairId, Notice(ChangeKind.Changed))];
    }

    // The journal item-failure path: a source-less in-use episode on a work later than the selection's.
    private static long B205ProbeEpisode(Scenario scenario, string pairId, string path, string identity, long epoch)
    {
        var store = scenario.Store;
        var subject = new IssueSubject([new IssueItem(Side.Local, path, identity)], null);
        long episodeWork = 0;
        scenario.Host.DispatchAsync(pairId, async (registration, _, generation, token) =>
        {
            await scenario.Host.ExecuteItemAttemptAsync(registration, registration, null, subject,
                (attemptId, attemptToken) =>
                {
                    episodeWork = store.RecordInUseFailure(pairId, generation, epoch, path, subject,
                        TextKey.FailLocalInUse, null, ItemRetryDispatch.OrdinaryItem, null, null, attemptId)
                        .Token.WorkId;
                    return Task.FromResult(true);
                }, token);
            return true;
        }, Scenario.Cancellation).GetAwaiter().GetResult();
        return episodeWork;
    }

    private static void B205ProbeAdmit(Scenario scenario, string variant, string path, WorkSelection selection,
        long noticed, JournalPosition position, long episodeWork)
    {
        var store = scenario.Store;
        var pairId = scenario.Pair.PairId;
        var local = store.ReadSource(pairId, Side.Local)!.Value;
        var dropbox = store.ReadSource(pairId, Side.Dropbox)!.Value;
        var footprint = new[]
        {
            new LiveEffectScope(Side.Local, scenario.LocalRoot + "\\" + path.Replace('/', '\\'), false),
        };
        var coverage = store.CaptureLiveCoverage(selection,
            new LocalCatchUp(pairId, selection.PairGeneration, local.Epoch,
                new NtfsUsnCheckpoint(position.JournalId, 0, (long)position.NextUsn, 0), noticed),
            new DropboxCatchUp(pairId, selection.PairGeneration, dropbox.Epoch,
                dropbox.Cursor ?? throw new InvalidOperationException("B-205 probe: no Dropbox cursor"), noticed));
        var prefix = "B205-REPRO variant=" + variant + " probe=";
        try
        {
            store.ValidateLiveCoverage(selection, coverage, footprint);
            Console.WriteLine(prefix + "admitted path=" + path + " anchorWork=" +
                selection.AnchorWorkId.ToString(CultureInfo.InvariantCulture) + " episodeWork=" +
                episodeWork.ToString(CultureInfo.InvariantCulture));
        }
        catch (LiveAdmissionStaleException error)
        {
            var diagnostic = error.HoldDiagnostic;
            Console.WriteLine(prefix + "refused path=" + path + " anchorWork=" +
                selection.AnchorWorkId.ToString(CultureInfo.InvariantCulture) + " episodeWork=" +
                episodeWork.ToString(CultureInfo.InvariantCulture) + " reason=" + error.Reason +
                " arm=" + (diagnostic is null ? "none" : B205Arm(diagnostic) ?? "none") +
                " detail=" + (diagnostic ?? error.Detail ?? string.Empty));
        }
    }

    private static List<Issue> InUseNotices(Scenario scenario) =>
        scenario.Store.Issues(scenario.Pair.PairId, [IssueKind.Failure])
            .Where(issue => issue.Cause == TextKey.FailLocalInUse).ToList();

    // Waits on pass ends, never a bare clock tick; the timeout only bounds a workload that does not run.
    private static bool B205WaitFor(Scenario scenario, Func<bool> condition, int seconds)
    {
        if (condition()) return true;
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.PassEnded = () =>
        {
            try { if (condition()) signal.TrySetResult(); } catch { }
        };
        try
        {
            return Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(seconds), Scenario.Cancellation))
                .GetAwaiter().GetResult() == signal.Task;
        }
        finally { scenario.PassEnded = null; }
    }

    // Every admission refusal this scenario logged, as the log's own detail text (it carries arm=/anchorWork=).
    private static List<string> B205Refusals(Scenario scenario)
    {
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(scenario.Logs, RunLog.NameSeparator + "*" +
                     ResourceNames.LogExtension, SearchOption.AllDirectories))
        {
            foreach (var fields in LogFields(file))
                if (fields.Length == RunLog.RecordFieldCount && fields[RunLog.FieldKind] == nameof(RunEventKind.Failed) &&
                    fields[RunLog.FieldWhat] == nameof(LiveAdmissionStaleException))
                    found.Add(fields[RunLog.FieldDetail]);
        }
        return found;
    }

    private static bool B205Names(string detail, string path) =>
        detail.Contains(path, StringComparison.Ordinal) || detail.Contains(path.Replace('/', '\\'), StringComparison.Ordinal);

    private static void ReportB205(Scenario scenario, string variant, string path, string identity, long episodeWork,
        int heldRefusals)
    {
        var refusals = B205Refusals(scenario).Where(detail => B205Names(detail, path)).ToList();
        var arms = refusals.Select(B205Arm).Where(arm => arm is not null)
            .GroupBy(arm => arm!, StringComparer.Ordinal)
            .Select(group => group.Key + "=" + group.Count().ToString(CultureInfo.InvariantCulture));
        var uploaded = scenario.DropboxFiles().ContainsKey(path);
        var notices = InUseNotices(scenario).Count(issue => PathNames.Key(issue.Path) == PathNames.Key(path));
        Console.WriteLine("B205-REPRO variant=" + variant + " identity=" + identity + " watermark=" +
            scenario.Store.ReadWorkWatermark().ToString(CultureInfo.InvariantCulture) + " episodeWork=" +
            episodeWork.ToString(CultureInfo.InvariantCulture) + " refusals=" +
            heldRefusals.ToString(CultureInfo.InvariantCulture) + "+" +
            (refusals.Count - heldRefusals).ToString(CultureInfo.InvariantCulture) + " arms=[" +
            string.Join(",", arms) + "] diagnostic=" +
            (refusals.Any(detail => B205Arm(detail) is not null) ? "yes" : "no") + " uploaded=" + uploaded +
            " in-use-notices=" + notices.ToString(CultureInfo.InvariantCulture));
        foreach (var detail in refusals.Take(20))
            Console.WriteLine("B205-REPRO variant=" + variant + " refusal: " + detail);
        if (refusals.Count > 20)
            Console.WriteLine("B205-REPRO variant=" + variant + " (" +
                (refusals.Count - 20).ToString(CultureInfo.InvariantCulture) + " more refusals not printed)");
    }

    private static string? B205Arm(string detail)
    {
        var match = Regex.Match(detail, @"arm=([a-z-]+)", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }

    // The app creates the file and keeps its own handle open with no share, so the first sighting is while held.
    private static FileStream CreateHeldFile(Scenario scenario, string path, string content)
    {
        var full = Path.Combine(scenario.LocalRoot, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
        return stream;
    }
}

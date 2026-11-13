using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Tests.Scenarios;

/// <summary>End-to-end dummy-file scenarios (`--engine-checks scenarios`): real LocalSide, real StateStore, real
/// DropboxSide and real Repair, against the owner's real account (live) and the in-memory account (offline).
/// One catalogue feeds the offline registry, the offline phase and the concurrent live phase.</summary>
public static class ScenarioChecks
{
    /// <summary>The one scenario catalogue: name, arrangement/assertions body, ignore rules.</summary>
    private static readonly (string Name, Action<Scenario> Body, string[] IgnoreRules)[] Cases =
    [
        ("01-new-local-upload", BodyNewLocalUploads, []),
        ("02-new-dropbox-download", BodyNewDropboxDownloads, []),
        ("03-new-folders", BodyNewFolders, []),
        ("03b-own-folder-echo", BodyOwnFolderEcho, []),
        ("03c-changed-folder-refuses-child", BodyChangedFolderRefusesChild, []),
        ("03d-replaced-folder-refuses-child", BodyReplacedFolderRefusesChild, []),
        ("03e-own-dropbox-folder-echo", BodyOwnDropboxFolderEcho, []),
        ("03f-replaced-dropbox-folder-refuses-child", BodyReplacedDropboxFolderRefusesChild, []),
        ("04-equal-different-dates", BodyEqualFilesDifferentDates, []),
        ("05-same-second-recorded", BodySameSecondIsRecorded, []),
        ("05b-different-second-different-content", BodyDifferentSecondDifferentContent, []),
        ("05b-different-second-identical-content", BodyDifferentSecondIdenticalContent, []),
        ("05c-different-second-locked-content", scenario => BodyLockedComparison(scenario, false), []),
        ("06-edit-each-side", BodyEditEachSide, []),
        ("07-both-edited-keep-both", BodyBothEditedKeepBoth, []),
        ("08-delete-each-side", BodyDeleteEachSide, []),
        ("09-edit-beats-delete", BodyEditBeatsDelete, []),
        ("10-folder-delete-edited-child", scenario => BodyFolderDeleteEditedChild(scenario, Side.Local, "docs"), []),
        ("10b-folder-delete-local-edited-child", scenario => BodyFolderDeleteEditedChild(scenario, Side.Dropbox, "docs"), []),
        ("11a-rename-local", BodyRenameLocal, []),
        ("11b-rename-dropbox", BodyRenameDropbox, []),
        ("11c-case-only-local", BodyCaseOnlyLocal, []),
        ("11d-case-only-dropbox", BodyCaseOnlyDropbox, []),
        ("12a-exclude-local", BodyExcludeLocal, []),
        ("12b-exclude-dropbox", BodyExcludeDropbox, []),
        ("12c-exclusion-copy-fails", scenario => BodyExclusionCopyFails(scenario, "fail/f.txt", "fail/z.txt"), []),
        ("12e-exclusion-late-copy-fails", scenario => BodyExclusionCopyFails(scenario, "fail/z.txt", "fail/f.txt"), []),
        ("12d-keep-local-exclusion", BodyKeepLocalExclusion, []),
        ("12f-excluded-saved-only", BodyExcludedSavedOnly, []),
        ("13a-resolve-use-local", scenario => Resolve(scenario, ResolutionChoice.UseLocal), []),
        ("13b-resolve-use-dropbox", scenario => Resolve(scenario, ResolutionChoice.UseDropbox), []),
        ("13c-resolve-keep-both", scenario => Resolve(scenario, ResolutionChoice.KeepBoth), []),
        ("14-pending-replace", BodyPendingReplace, []),
        ("15-failed-listing", BodyFailedListing, []),
        ("16-ignore-rule", BodyIgnoreRule, ["FileExtension:.tmp"]),
        ("17-gzip-file-byte-integrity", BodyGzipFileByteIntegrity, []),
        ("18-injected-definite-failure-retry", BodyDefiniteFailureRetry, []),
        ("19-mapped-conflict-proof-retirement", BodyMappedConflictProofRetirement, []),
        ("20-folder-delete-saved-only-child", scenario => BodyFolderDeleteSavedOnlyChild(scenario, Side.Local), []),
        ("20b-folder-delete-local-saved-only-child", scenario => BodyFolderDeleteSavedOnlyChild(scenario, Side.Dropbox), []),
        // D37 phase 2: the mapped spelling is dormant, so these use the landed transferable spelling; the mapped
        // folder-name coverage returns with the phase-2 mapping trigger.
        ("21-mapped-folder-delete-edited-child", scenario => BodyFolderDeleteEditedChild(scenario, Side.Local, "docs"), []),
        ("21b-mapped-folder-delete-local-edited-child", scenario => BodyFolderDeleteEditedChild(scenario, Side.Dropbox, "docs"), []),
        ("22-folder-delete-ignored-local", scenario => BodyFolderDeleteIgnoredContents(scenario, Side.Dropbox), ["FileExtension:.tmp"]),
        ("22b-folder-delete-ignored-dropbox", scenario => BodyFolderDeleteIgnoredContents(scenario, Side.Local), ["FileExtension:.tmp"]),
        ("23-folder-delete-local-order", scenario => BodyFolderDeleteOrder(scenario, Side.Dropbox), []),
        ("23b-folder-delete-dropbox-order", scenario => BodyFolderDeleteOrder(scenario, Side.Local), []),
        ("24-cross-linked-folder-survivor", BodyCrossLinkedFolderSurvivor, []),
        ("24b-held-folder-survivor", BodyHeldFolderSurvivor, []),
        ("25-mapped-collision-retirement", BodyMappedCollisionRetirement, []),
        ("26-stage-orphan-cleanup", scenario => BodyStageCleanup(scenario, false), []),
        ("26b-stage-cleanup-recovery", scenario => BodyStageCleanup(scenario, true), []),
        ("27-move-unlinked-file", scenario => BodyMoveUnlinked(scenario, Side.Local), []),
        ("27b-move-unlinked-folder", scenario => BodyMoveUnlinked(scenario, Side.Dropbox), []),
        ("28-nested-local-folder-rename", scenario => BodyFolderRename(scenario, Side.Local, false), []),
        ("28b-nested-dropbox-folder-rename", scenario => BodyFolderRename(scenario, Side.Dropbox, false), []),
        ("29-cross-linked-local-folder-rename", scenario => BodyFolderRename(scenario, Side.Local, true), []),
        ("29b-cross-linked-dropbox-folder-rename", scenario => BodyFolderRename(scenario, Side.Dropbox, true), []),
        ("30-frozen-unicode-identity", BodyUnicodeIdentity, []),
        ("31-folder-delete-listing-recovery", BodyFolderDeleteListingRecovery, []),
        ("32-pair-failure-retry", BodyPairFailureRetry, []),
        ("33-folder-delete-carried-link", BodyFolderDeleteCarriedLink, []),
        ("34-resolution-child-recovery", BodyResolutionChildRecovery, []),
        ("35-stale-resolution-new-occupant", BodyStaleResolution, []),
        ("36-exclusion-one-leg-keep-both", BodyExclusionKeepBoth, []),
        ("37-other-pair-pending-stage", BodyOtherPairPendingStage, []),
        ("38-local-stage-length-validation", BodyStageLengthValidation, []),
        ("39a-legacy-tie-local-old", scenario => BodyLegacyTie(scenario, localOlder: true, dismissed: false), []),
        ("39b-legacy-tie-dropbox-old", scenario => BodyLegacyTie(scenario, localOlder: false, dismissed: false), []),
        ("39c-legacy-tie-equal-content", BodyLegacyTieEqualContent, []),
        ("39d-legacy-tie-dismissed", scenario => BodyLegacyTie(scenario, localOlder: true, dismissed: true), []),
        ("39d2-legacy-tie-dismissed-dropbox-old", scenario => BodyLegacyTie(scenario, localOlder: false, dismissed: true), []),
        ("39e-unreliable-time-local-future", scenario => BodyUnreliableFirstRun(scenario, localFuture: true), []),
        ("39f-unreliable-time-dropbox-future", scenario => BodyUnreliableFirstRun(scenario, localFuture: false), []),
        ("39h-unreliable-time-equal-content", BodyUnreliableEqualContent, []),
        ("39i-unreliable-time-locked-content", scenario => BodyLockedComparison(scenario, true), []),
        ("40-remote-folder-removal-before-repair", BodyRemoteFolderRemovalBeforeRepair, []),
        ("41-nested-empty-folders-one-repair", BodyNestedEmptyFolders, []),
        ("42-nested-new-tree-one-repair", BodyNestedNewTree, []),
        ("43-own-folder-echo-settles", BodyOwnFolderEchoSettles, []),
        ("44-own-folder-target-before-fresh-read", BodyTargetBeforeFreshReadRefuses, []),
        ("45-pause-during-prerequisite", scenario => BodyPauseDuringPrerequisite(scenario, admissionFirst: false), []),
        ("45b-pause-admission-fence", scenario => BodyPauseDuringPrerequisite(scenario, admissionFirst: true), []),
        ("45c-pause-genuine-failure", scenario => BodyPauseDuringPrerequisite(scenario, admissionFirst: true,
            new IOException("synthetic prerequisite failure")), []),
        ("45d-pause-unrelated-stale", scenario => BodyPauseDuringPrerequisite(scenario, admissionFirst: true,
            new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged)), []),
        ("45e-explicit-prerequisite-cancel", BodyExplicitPrerequisiteCancellation, []),
        ("45f-pause-before-prerequisite-dispatch", scenario => BodyPauseDuringPrerequisite(scenario,
            admissionFirst: true, acquisition: true), []),
        ("46-recycled-folder-receipt", BodyRecycledReceipt, []),
        // shortcut: native drive changes and policy choices need a configurable session, add cases when the fixture exposes it.
        // shortcut: NoSpace needs a typed provider refusal, add suppression coverage when the account port exposes it.
        // shortcut: denied-folder snapshots fail before Repair, add ACL coverage when snapshots tolerate unreadable entries.
        // shortcut: targeted subtree faults, late arrivals and tombstones need a listing port, add cases when it exists.
        // shortcut: old-schema refusal needs a storage-owned fixture, add the episode when that fixture is exposed.
        // shortcut: pair Retry supplies all Failure IDs here, test CLI selection when its offline session port exists.
        // shortcut: Copies scales roots rather than a deletion subtree, add a large case when its workload is specified.
    ];

    /// <summary>`scenarios`: the offline cases first, then the same cases live in parallel; both report separately.</summary>
    public static void RunAll()
    {
        RunOffline();
        var live = RunLiveCore();
        Console.WriteLine(Summary("LIVE", live.Passed, live.Skipped, live.Failed.Count, live.ElapsedMs));
        var failures = OfflineFailures.Concat(live.Failed).ToArray();
        Check.Require(failures.Length == 0, "scenarios failed: " + string.Join(" | ", failures));
    }

    /// <summary>`scenarios-live`: the live cases only (parallel, one pair and folder set per case).</summary>
    public static void RunLive()
    {
        var live = RunLiveCore();
        Console.WriteLine(Summary("LIVE", live.Passed, live.Skipped, live.Failed.Count, live.ElapsedMs));
        Check.Require(live.Failed.Count == 0, "live cases failed: " + string.Join(" | ", live.Failed));
    }

    /// <summary>`scenarios-offline`: one action per catalogue entry plus the local-root safety harness.</summary>
    public static IReadOnlyList<Action> OfflineCases()
    {
        var actions = new List<Action> { HarnessSafety, LiveCompositionSeamChecks, RetainedStartupChecks };
        actions.AddRange(Cases.Select(entry =>
            (Action)(() => Run(entry.Name, entry.Body, entry.IgnoreRules, throwOnFailure: true))));
        return actions;
    }

    /// <summary>The harness: a pre-existing empty directory and a junction are both refused.</summary>
    public static void HarnessSafety()
    {
        var root = Path.Combine(Scenario.RunRoot, "harness-safety");
        Directory.CreateDirectory(root);
        var junction = Path.Combine(root, "junction");
        try
        {
            var empty = Path.Combine(root, "empty");
            Directory.CreateDirectory(empty);
            Check.Require(Refused(() => Scenario.RequireFreshRoot(empty)),
                "an existing empty directory is refused");

            var file = Path.Combine(root, "file");
            File.WriteAllText(file, "x");
            Check.Require(Refused(() => Scenario.RequireFreshRoot(file)), "an existing file is refused");

            var target = Path.Combine(root, "target");
            Directory.CreateDirectory(target);
            Check.Require(CreateJunction(junction, target) && Directory.Exists(junction),
                "the harness created a junction");
            Check.Require(Refused(() => Scenario.RequireFreshRoot(junction)), "an existing junction is refused");
            Check.Require(Refused(() => Scenario.RequireRealDirectory(junction)),
                "a junction is not a real directory");
            Scenario.RequireRealDirectory(target);
        }
        finally
        {
            // Deleting the junction removes the link, not the target.
            if (Directory.Exists(junction)) Directory.Delete(junction);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void RunOffline()
    {
        lock (OfflineGate)
        {
            OfflineFailures.Clear();
            OfflinePasses = 0;
            OfflineSkipped = 0;
        }
        var started = DateTime.UtcNow;
        reportOffline = true;
        try
        {
            try
            {
                HarnessSafety();
                lock (OfflineGate) OfflinePasses++;
                Console.WriteLine("PASS harness-safety");
            }
            catch (Exception error)
            {
                lock (OfflineGate) OfflineFailures.Add("harness-safety: " + error.Message);
                Console.Error.WriteLine("FAIL harness-safety: " + error.GetType().Name + ": " + error.Message);
            }
            foreach (var entry in Cases) Run(entry.Name, entry.Body, entry.IgnoreRules, throwOnFailure: false);
        }
        finally
        {
            reportOffline = false;
        }
        foreach (var failure in Scenario.TakeCleanupFailures())
        {
            lock (OfflineGate) OfflineFailures.Add("cleanup: " + failure);
            Console.Error.WriteLine("FAIL cleanup: " + failure);
        }
        var elapsed = (int)(DateTime.UtcNow - started).TotalMilliseconds;
        Console.WriteLine(Summary("OFFLINE", OfflinePasses, OfflineSkipped, OfflineFailures.Count, elapsed));
    }

    private static (int Passed, int Skipped, List<string> Failed, int ElapsedMs) RunLiveCore()
    {
        lock (LiveGate)
        {
            LiveFailures.Clear();
            LivePasses = 0;
            LiveSkipped = 0;
        }
        var started = DateTime.UtcNow;
        Scenario.StartLive();
        try
        {
            // SYNCSHARK_LIVE_CASES (comma-separated names, or "all") runs those cases, SYNCSHARK_LIVE_PARALLEL at a
            // time (default 1), so a small batch stays under Dropbox's rate limit.
            var only = Environment.GetEnvironmentVariable("SYNCSHARK_LIVE_CASES")?
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var width = int.TryParse(Environment.GetEnvironmentVariable("SYNCSHARK_LIVE_PARALLEL"), out var n) && n > 0
                ? n : 1;
            if (only is { Length: > 0 })
                Parallel.ForEach(Cases.Where(entry => only.Contains("all") || only.Contains(entry.Name)),
                    new ParallelOptions { MaxDegreeOfParallelism = width },
                    entry => Run(entry.Name, entry.Body, entry.IgnoreRules, throwOnFailure: false));
            else
            {
                var tasks = Cases.Select(entry => Task.Factory.StartNew(
                        () => Run(entry.Name, entry.Body, entry.IgnoreRules, throwOnFailure: false),
                        TaskCreationOptions.LongRunning))
                    .ToArray();
                Task.WaitAll(tasks);
            }
        }
        finally
        {
            foreach (var failure in Scenario.EndLive())
                lock (LiveGate) LiveFailures.Add("cleanup: " + failure);
            foreach (var failure in Scenario.TakeCleanupFailures())
                lock (LiveGate) LiveFailures.Add("cleanup: " + failure);
        }
        var elapsed = (int)(DateTime.UtcNow - started).TotalMilliseconds;
        return (LivePasses, LiveSkipped, LiveFailures.ToList(), elapsed);
    }

    private static string Summary(string mode, int passed, int skipped, int failed, int elapsedMs) =>
        mode + " summary: " + passed + " passed, " + skipped + " skipped, " + failed + " failed, elapsed " +
        elapsedMs.ToString(CultureInfo.InvariantCulture) + " ms";

    private static readonly object LiveGate = new();
    private static readonly List<string> LiveFailures = new();
    private static int LivePasses;
    private static int LiveSkipped;
    private static readonly object OfflineGate = new();
    private static readonly List<string> OfflineFailures = new();
    private static int OfflinePasses;
    private static int OfflineSkipped;
    private static bool reportOffline;

    private static void Run(string name, Action<Scenario> body, string[] ignoreRules, bool throwOnFailure)
    {
        Scenario? scenario = null;
        try
        {
            scenario = Scenario.Begin(name, ignoreRules);
            body(scenario);
            if (scenario.Skipped)
            {
                Check.Skip();
                if (Scenario.LiveMode) lock (LiveGate) LiveSkipped++;
                else if (reportOffline) lock (OfflineGate) OfflineSkipped++;
                if (Scenario.LiveMode || reportOffline)
                    Console.WriteLine("SKIP " + name + ": expectations do not expand at multiplier " +
                        Scenario.Copies);
                return;
            }
            scenario.RequireOnlyEngineRoutes();
            if (Scenario.LiveMode)
            {
                lock (LiveGate)
                {
                    LivePasses++;
                    Console.WriteLine("PASS " + name);
                }
            }
            else if (reportOffline)
            {
                lock (OfflineGate)
                {
                    OfflinePasses++;
                    Console.WriteLine("PASS " + name);
                }
            }
        }
        catch (OperationCanceledException error)
        {
            scenario?.Fail(error);
            Record(name, scenario?.Root, "cancelled: " + error.Message, throwOnFailure);
        }
        catch (Exception error)
        {
            scenario?.Fail(error);
            Record(name, scenario?.Root, error.GetType().Name + ": " + error.Message, throwOnFailure);
        }
        finally
        {
            scenario?.Dispose();
        }
    }

    private static void Record(string name, string? root, string message, bool throwOnFailure)
    {
        if (throwOnFailure)
            throw new InvalidOperationException(name + (root is null ? "" : " (kept: " + root + ")") + ": " + message);
        if (Scenario.LiveMode)
        {
            lock (LiveGate)
            {
                LiveFailures.Add(name + ": " + message);
                Console.Error.WriteLine("FAIL " + name + ": " + message);
            }
            return;
        }
        lock (OfflineGate)
        {
            OfflineFailures.Add(name + ": " + message);
            Console.Error.WriteLine("FAIL " + name + ": " + message);
        }
    }

    private static bool Refused(Action action)
    {
        try { action(); return false; }
        catch (InvalidOperationException) { return true; }
    }

    /// <summary>The real selected-resolution entry, under the session's root/authority admission.</summary>
    private static SyncOutcome ResolveSelected(Scenario scenario, string runId, Issue issue,
        ResolutionChoice choice, string? newName = null) =>
        scenario.InSession(runId, session =>
            PairSafety.ResolveSelectedAsync(session, issue, choice, newName, null, Scenario.Cancellation)
                .GetAwaiter().GetResult().Outcome);

    // 1. A new local file uploads.
    private static void BodyNewLocalUploads(Scenario scenario)
    {
        scenario.LocalFile("new.txt", "hello");
        var summary = scenario.RepairAsync();
        scenario.RequireTree(local: ["new.txt=hello"], dropbox: ["new.txt=hello"]);
        scenario.Require(summary.Effects.ContainsKey(nameof(EffectKind.Uploaded)),
            "the new local file was uploaded (" + Effects(summary) + ")");
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    // 2. A new Dropbox file downloads.
    private static void BodyOwnFolderEcho(Scenario scenario)
    {
        if (scenario.SkipWhenScaled()) return;
        scenario.DropboxFile("made/a.txt", "a");
        scenario.AcquireJournalAfterFolder();
        var summary = scenario.RepairAsync(requireCompleted: false);
        scenario.Require(summary.Completed, "a matching folder echo permits a new child in one Repair");
        scenario.RequireTree(["made/", "made/a.txt=a"], ["made/", "made/a.txt=a"]);
        scenario.RequireIssues();
    }

    private static void BodyChangedFolderRefusesChild(Scenario scenario)
    {
        if (scenario.SkipWhenScaled()) return;
        scenario.DropboxFile("made/a.txt", "a");
        scenario.AcquireJournalAfterFolder(scenario.RemoveDropbox);
        var summary = scenario.RepairAsync(requireCompleted: false);
        scenario.Require(summary.Completed, "a refused child does not prevent Repair completion");
        scenario.Require(!scenario.Store.Obligated(scenario.Pair.PairId), "the completed Repair clears its obligation");
        scenario.Require(!scenario.LocalFiles().ContainsKey("made/a.txt"), "the refused child wrote nothing");
        scenario.Require(scenario.Store.Issues(scenario.Pair.PairId).Any(issue => issue.Path == "made/a.txt" &&
            issue.Kind == IssueKind.Failure && issue.Cause == TextKey.FailChanged),
            "the refused child stays visible");
    }

    private static void BodyReplacedFolderRefusesChild(Scenario scenario)
    {
        if (scenario.SkipWhenScaled()) return;
        scenario.DropboxFile("made/a.txt", "a");
        scenario.AcquireJournalAfterFolder(path =>
        {
            scenario.RemoveDropbox(path);
            scenario.DropboxFile("made/a.txt", "a");
        });
        var summary = scenario.RepairAsync(requireCompleted: false);
        scenario.Require(summary.Completed, "a replaced ancestor's refused child does not prevent Repair completion");
        scenario.Require(!scenario.Store.Obligated(scenario.Pair.PairId), "the completed Repair clears its obligation");
        scenario.Require(!scenario.LocalFiles().ContainsKey("made/a.txt"), "no child write crossed the replacement");
        scenario.Require(scenario.Store.Issues(scenario.Pair.PairId).Any(issue => issue.Path == "made/a.txt" &&
            issue.Kind == IssueKind.Failure && issue.Cause == TextKey.FailChanged),
            "the replaced ancestor's child is decided as a changed row");
    }

    private static void BodyOwnDropboxFolderEcho(Scenario scenario)
    {
        if (scenario.SkipWhenScaled()) return;
        // Finish the first run so the new local folder is created inline by its own row, before the child row
        // reaches its fence. A first-run folder defers creation into the child only when a deleted name selects it.
        scenario.RepairAsync();
        scenario.LocalFile("made/a.txt", "a");
        scenario.AcquireFeedAfterFolder();
        var summary = scenario.RepairAsync(requireCompleted: false);
        scenario.Require(summary.Completed, "a matching feed echo permits a new child in one Repair");
        scenario.RequireTree(["made/", "made/a.txt=a"], ["made/", "made/a.txt=a"]);
        scenario.RequireIssues();
    }

    private static void BodyReplacedDropboxFolderRefusesChild(Scenario scenario)
    {
        if (scenario.SkipWhenScaled()) return;
        // Same first-run reason as the own echo: create the folder in its own row, before the child's fence;
        // the first-run deferral needs a deleted name to select it.
        scenario.RepairAsync();
        scenario.LocalFile("made/a.txt", "a");
        scenario.AcquireFeedAfterFolder(replaced: true);
        var summary = scenario.RepairAsync(requireCompleted: false);
        scenario.Require(summary.Completed, "a replaced Dropbox ancestor's refused child does not prevent Repair completion");
        scenario.Require(!scenario.Store.Obligated(scenario.Pair.PairId), "the completed Repair clears its obligation");
        var generation = scenario.Store.ReadPairExecution(scenario.Pair.PairId)!.Value.Generation;
        scenario.Require(scenario.Store.PendingAncestors(scenario.Pair.PairId, generation, Side.Dropbox,
                [PathNames.KeyFor(Side.Dropbox, "made")]).Any(),
            "the replaced ancestor's notice stays pending for ordinary selection");
        scenario.Require(!scenario.DropboxFiles().ContainsKey("made/a.txt"), "no child write crossed the replacement");
        scenario.Require(scenario.Store.Issues(scenario.Pair.PairId).Any(issue => issue.Path == "made/a.txt" &&
            issue.Kind == IssueKind.Failure && issue.Cause == TextKey.FailChanged),
            "the replaced Dropbox ancestor's child is decided as a changed row");
    }

    private static void BodyNewDropboxDownloads(Scenario scenario)
    {
        scenario.DropboxFile("cloud.txt", "hello");
        var summary = scenario.RepairAsync();
        scenario.RequireTree(local: ["cloud.txt=hello"], dropbox: ["cloud.txt=hello"]);
        scenario.Require(summary.Effects.ContainsKey(nameof(EffectKind.Downloaded)),
            "the new Dropbox file was downloaded (" + Effects(summary) + ")");
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    // 3. New folders travel both ways, including an empty one.
    private static void BodyNewFolders(Scenario scenario)
    {
        scenario.LocalFolder("from-local");
        scenario.LocalFile("from-local/a.txt", "a");
        scenario.DropboxFolder("from-dropbox");
        scenario.RepairAsync();
        scenario.RequireTree(
            local: ["from-local/", "from-local/a.txt=a", "from-dropbox/"],
            dropbox: ["from-local/", "from-local/a.txt=a", "from-dropbox/"]);
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    // A236: an empty nested tree completes in one Repair and keeps its echoes pending. It also fails without the
    // matching-notice exception: "a/b" meets the echo of "a" as a create, so it is a regression case too, not a
    // non-discriminating one.
    private static void BodyNestedEmptyFolders(Scenario scenario)
    {
        if (scenario.SkipWhenScaled()) return;
        scenario.LocalFolder("a/b/c");
        scenario.AcquireFeedAfterEachFolder();
        var summary = scenario.RepairAsync(requireCompleted: false);
        scenario.Require(summary.Completed, "a nested empty folder tree completes in one Repair");
        scenario.RequireTree(["a/", "a/b/", "a/b/c/"], ["a/", "a/b/", "a/b/c/"]);
        scenario.Require(scenario.PendingAncestorKeys(Side.Dropbox, "a", "a/b", "a/b/c").Count != 0,
            "A236: the folder echoes stay pending after the completed Repair");
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    // A78: a chain of new folders with a child at every level completes in one Repair with no issues. The echoes
    // come from the ActionExecutor's own folder rows, not Parents-created ones: a full Repair over the Parents
    // Unbound-then-join path is not exercised here. NestedCreationChecksAsync proves that handshake with the real
    // fence and Parents on the in-memory store, plus the real-store answers.
    // B-164 T6 (live only; offline the Bury seam returns no receipt): a Dropbox folder delete recycles the local folders
    // through the Shell, and the receipt walk proves each recycled folder's identity and complete contents before the
    // clean delete is reported. The recycled folders stay in the Recycle Bin, recorded in the test log only.
    private static void BodyRecycledReceipt(Scenario scenario)
    {
        if (scenario.SkipUnlessLive()) return;
        const string full = "Lösch Ordner", empty = "Leer";
        scenario.LocalFile(full + "/Top.TXT", "1");
        scenario.LocalFile(full + "/Sub Mappe/zwei.txt", "22");
        scenario.LocalFile(full + "/Sub Mappe/Tiefe Ébène/drei.bin", "333");
        scenario.LocalFolder(empty);
        scenario.RepairAsync();
        var listing = new LocalListing(scenario.Pair, scenario.Scope, (_, _, _, _) => { });
        var recorded = new[] { full, empty }.ToDictionary(folder => folder, folder => (Folder: listing.One(folder)!,
            Contents: Collect(listing.Walk(folder, new ListOptions(true, true), Scenario.Cancellation))));
        scenario.Require(recorded[full].Contents.Count(listed => !listed.Entry!.IsFolder) == 3 &&
            recorded[empty].Contents.Count == 0, "the recorded folders hold three files and nothing");
        var previous = LocalRecycleReceipt.Observed;
        scenario.Require(previous is null, "the receipt hook is unused");
        var receipts = new System.Collections.Concurrent.ConcurrentQueue<(string Path, string Receipt, DateTimeOffset At)>();
        LocalRecycleReceipt.Observed = (path, receipt) =>
        {
            if (recorded.ContainsKey(path)) receipts.Enqueue((path, receipt, DateTimeOffset.UtcNow));
        };
        try
        {
            scenario.RemoveDropbox(full);
            scenario.RemoveDropbox(empty);
            var summary = scenario.RepairAsync();
            scenario.RequireIssues();
            scenario.Require(summary.Effects.GetValueOrDefault(nameof(EffectKind.DeletedLocal)) == 2,
                "one local delete is reported per recycled folder (" + Effects(summary) + ")");
            scenario.Require(!Directory.Exists(Path.Combine(scenario.LocalRoot, full)) &&
                !Directory.Exists(Path.Combine(scenario.LocalRoot, empty)), "both local folders are gone");
            scenario.Require(receipts.Count == 2 && receipts.Select(receipt => receipt.Path).Distinct().Count() == 2,
                "the receipt was inspected once per folder");
            foreach (var (path, receipt, _) in receipts)
            {
                var contents = Collect(LocalRecycleReceipt.ContentsAsync(scenario.Pair, scenario.Scope, receipt, path,
                    recorded[path].Folder.Id, Scenario.Cancellation));
                scenario.Require(contents.Select(Fact).Order(StringComparer.Ordinal)
                        .SequenceEqual(recorded[path].Contents.Select(Fact).Order(StringComparer.Ordinal)),
                    "the recycled receipt of " + path + " holds exactly the recorded contents");
            }
            scenario.RequireOnlyNumberedRoots();
        }
        finally
        {
            LocalRecycleReceipt.Observed = previous;
            var volume = scenario.Pair.Roots?.Local.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture);
            foreach (var (path, receipt, at) in receipts)
                scenario.Note("B-164 recycled test folder left in the Recycle Bin: receipt=" + receipt + "; volume=" +
                    volume + "; file-id=" + recorded[path].Folder.Id + "; recycled-utc=" +
                    at.ToString("O", CultureInfo.InvariantCulture));
        }

        static string Fact(Listed listed) => string.Join("|", listed.Path, listed.Entry!.Id, listed.Entry.IsFolder,
            listed.Entry.Size.ToString(CultureInfo.InvariantCulture),
            listed.Entry.Modified.UtcTicks.ToString(CultureInfo.InvariantCulture));
    }

    private static List<Listed> Collect(IAsyncEnumerable<Listed> items) => CollectAsync(items).GetAwaiter().GetResult();

    private static async Task<List<Listed>> CollectAsync(IAsyncEnumerable<Listed> items)
    {
        var collected = new List<Listed>();
        await foreach (var item in items.ConfigureAwait(false)) collected.Add(item);
        return collected;
    }

    private static void BodyNestedNewTree(Scenario scenario)
    {
        if (scenario.SkipWhenScaled()) return;
        scenario.LocalFolder("a/b/c");
        scenario.LocalFile("a/one.txt", "1");
        scenario.LocalFile("a/b/two.txt", "2");
        scenario.LocalFile("a/b/c/three.txt", "3");
        scenario.AcquireFeedAfterEachFolder();
        var summary = scenario.RepairAsync(requireCompleted: false);
        scenario.Require(summary.Completed, "a nested new tree completes in one Repair");
        scenario.Require(summary.IssuesByKind.Values.Sum() == 0, "the nested new tree raised no issues");
        scenario.RequireTree(
            ["a/", "a/b/", "a/b/c/", "a/one.txt=1", "a/b/two.txt=2", "a/b/c/three.txt=3"],
            ["a/", "a/b/", "a/b/c/", "a/one.txt=1", "a/b/two.txt=2", "a/b/c/three.txt=3"]);
        scenario.Require(scenario.PendingAncestorKeys(Side.Dropbox, "a", "a/b", "a/b/c").Count != 0,
            "A78: the folder echoes stay pending after the completed Repair");
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    // A214/A77: the own-folder echo left pending after a completed Repair is settled by the ordinary production
    // selection with no effect, and settling it acquires no new work.
    private static void BodyOwnFolderEchoSettles(Scenario scenario)
    {
        if (scenario.SkipWhenScaled()) return;
        // The explicit feed contains only the folder echo; a local download would also generate child journal work.
        scenario.LocalFile("made/a.txt", "a");
        long? watermark = null;
        scenario.AcquireFeedAfterFolder(afterNotice: () => watermark = scenario.Store.ReadWorkWatermark());
        var summary = scenario.RepairAsync(requireCompleted: false);
        // Live catch-up may acquire later notices (e.g. the child upload's own echo); only the echo's boundary is fixed.
        scenario.Require(watermark.HasValue && scenario.Store.ReadWorkWatermark() >= watermark.Value,
            "A214/A77: the echo acquisition watermark remains valid after Repair");
        scenario.Require(summary.Completed, "a matching echo permits a new child in one Repair");
        scenario.RequireTree(["made/", "made/a.txt=a"], ["made/", "made/a.txt=a"]);
        scenario.RequireIssues();
        scenario.Require(scenario.PendingAncestorKeys(Side.Dropbox, "made").Count != 0,
            "A214/A77: the matching echo stays pending after the completed Repair");
        var before = scenario.Snapshot();
        var beforeSettlement = scenario.Store.ReadWorkWatermark();
        var settled = scenario.SettleSelections();
        scenario.Require(settled != 0, "A214/A77: the ordinary production selection settles the pending echo");
        scenario.Require(scenario.Snapshot().SequenceEqual(before),
            "A214/A77: settling the echo leaves both trees unchanged");
        scenario.Require(scenario.PendingAncestorKeys(Side.Dropbox, "made").Count == 0,
            "A214/A77: the settled echo is retired");
        scenario.Require(scenario.Store.ReadWorkWatermark() == beforeSettlement,
            "A214/A77: settling the echo acquires no new work");
        scenario.RequireIssues();
    }

    // A235: a target that appears before the fresh read under a matching echo refuses the create exception.
    private static void BodyTargetBeforeFreshReadRefuses(Scenario scenario)
    {
        if (scenario.SkipWhenScaled()) return;
        scenario.DropboxFile("made/a.txt", "a");
        scenario.AcquireJournalAfterFolder(_ => scenario.LocalFile("made/a.txt", "local"));
        var summary = scenario.RepairAsync(requireCompleted: false);
        scenario.Require(summary.Completed, "an occupied target's refused create does not prevent Repair completion");
        scenario.Require(!scenario.Store.Obligated(scenario.Pair.PairId), "the completed Repair clears its obligation");
        scenario.Require(scenario.Store.Issues(scenario.Pair.PairId).Any(issue => issue.Path == "made/a.txt" &&
            issue.Kind == IssueKind.Failure && issue.Cause == TextKey.FailChanged),
            "the refused create retains its exact changed-row Failure");
        scenario.Require(scenario.LocalFiles().TryGetValue("made/a.txt", out var bytes) &&
            bytes.SequenceEqual(System.Text.Encoding.UTF8.GetBytes("local")),
            "the existing target file is left unchanged");
    }

    // 4. First run, equal files with different dates: no transfer.
    private static void BodyEqualFilesDifferentDates(Scenario scenario)    {
        scenario.LocalFile("same.txt", "same", Scenario.T1);
        scenario.DropboxFile("same.txt", "same", Scenario.T0);
        var summary = scenario.RepairAsync();
        scenario.RequireTree(local: ["same.txt=same"], dropbox: ["same.txt=same"]);
        RequireNoTransfer(scenario, summary);
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    // 5. D13: first run, equal size and the same whole second = the same file: recorded, no content read.
    private static void BodySameSecondIsRecorded(Scenario scenario)
    {
        scenario.LocalFile("tie.txt", "AAA", Scenario.T0);
        scenario.DropboxFile("tie.txt", "BBB", Scenario.T0);
        var downloads = scenario.Account.ContentDownloads;
        var summary = scenario.RepairAsync(denyContentAt: "tie.txt");
        scenario.RequireTree(local: ["tie.txt=AAA"], dropbox: ["tie.txt=BBB"]);
        scenario.Require(summary.Effects.GetValueOrDefault(nameof(EffectKind.Recorded)) == ScaledRows,
            "D13 records the equal size, equal second pair (" + Effects(summary) + ")");
        RequireNoTransfer(scenario, summary);
        scenario.Require(scenario.Account.ContentDownloads == downloads,
            "D13 downloads no Dropbox content (" +
            (scenario.Account.ContentDownloads - downloads) + " downloads)");
        scenario.RequireIssues();
        scenario.RequireQuiet();
        scenario.Require(scenario.Store.SyncedByKey(scenario.Pair.PairId, Side.Dropbox, PathNames.DropboxKey("tie.txt"))
                is not null,
            "the D13 record exists after the follow-up Repair");
        scenario.RequireIssues();
    }

    // 5b. First run, equal size, different whole second: content is compared, then the first-run policy applies.
    private static void BodyDifferentSecondDifferentContent(Scenario scenario)
    {
        scenario.LocalFile("b.txt", "AAA", Scenario.T1);
        scenario.DropboxFile("b.txt", "BBB", Scenario.T0);
        var summary = scenario.RepairAsync();
        scenario.RequireTree(local: ["b.txt=AAA"], dropbox: ["b.txt=AAA"]);
        scenario.Require(summary.Effects.GetValueOrDefault(nameof(EffectKind.Uploaded)) == Scenario.Copies,
            "the first-run policy picked the newer local file (" + Effects(summary) + ")");
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    private static void BodyLockedComparison(Scenario scenario, bool unreliableTime)
    {
        if (scenario.SkipWhenScaled()) return;
        scenario.LocalFile("compare.txt", "AAA", unreliableTime
            ? new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero) : Scenario.T1);
        scenario.DropboxFile("compare.txt", "BBB", Scenario.T0);
        scenario.LocalFile("other.txt", "independent", Scenario.T1);
        scenario.RepairAsync(denyContentAt: "compare.txt");
        scenario.RequireTree(["compare.txt=AAA", "other.txt=independent"],
            ["compare.txt=BBB", "other.txt=independent"]);
        scenario.RequireIssues("Failure:FailLocalInUse@compare.txt");
    }

    private static void BodyDifferentSecondIdenticalContent(Scenario scenario)
    {
        scenario.LocalFile("b.txt", "same", Scenario.T1);
        scenario.DropboxFile("b.txt", "same", Scenario.T0);
        var summary = scenario.RepairAsync();
        scenario.RequireTree(local: ["b.txt=same"], dropbox: ["b.txt=same"]);
        scenario.Require(summary.Effects.GetValueOrDefault(nameof(EffectKind.Recorded)) == ScaledRows,
            "identical content is recorded (" + Effects(summary) + ")");
        RequireNoTransfer(scenario, summary);
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    // 6. An edit on each side propagates.
    private static void BodyEditEachSide(Scenario scenario)
    {
        scenario.LocalFile("a.txt", "A1");
        scenario.LocalFile("b.txt", "B1");
        scenario.DropboxFile("a.txt", "A1");
        scenario.DropboxFile("b.txt", "B1");
        scenario.RepairAsync();

        scenario.LocalFile("a.txt", "A2", Scenario.T1);
        scenario.DropboxFile("b.txt", "B2", Scenario.T1);
        var summary = scenario.RepairAsync();
        scenario.RequireTree(local: ["a.txt=A2", "b.txt=B2"], dropbox: ["a.txt=A2", "b.txt=B2"]);
        scenario.Require(summary.Effects.GetValueOrDefault(nameof(EffectKind.Uploaded)) == Scenario.Copies &&
            summary.Effects.GetValueOrDefault(nameof(EffectKind.Downloaded)) == Scenario.Copies,
            "one upload and one download ran (" + Effects(summary) + ")");
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    // 7. Both sides edited differently: keep both versions.
    private static void BodyBothEditedKeepBoth(Scenario scenario)
    {
        scenario.LocalFile("a.txt", "A1");
        scenario.DropboxFile("a.txt", "A1");
        scenario.RepairAsync();

        scenario.LocalFile("a.txt", "LOCAL", Scenario.T2);
        scenario.DropboxFile("a.txt", "DROPBOX", Scenario.T1);
        var summary = scenario.RepairAsync();
        scenario.RequireKeepBoth("a", "LOCAL", "DROPBOX");
        scenario.Require(summary.Effects.ContainsKey(nameof(EffectKind.KeptBoth)),
            "keep-both recorded its effect (" + Effects(summary) + ")");
        scenario.RequireIssues("Info:InfoKeptBoth@a.txt");
        scenario.RequireQuiet();
    }

    // 8. A delete on each side propagates.
    private static void BodyDeleteEachSide(Scenario scenario)
    {
        scenario.LocalFile("a.txt", "A");
        scenario.LocalFile("b.txt", "B");
        scenario.DropboxFile("a.txt", "A");
        scenario.DropboxFile("b.txt", "B");
        scenario.RepairAsync();

        scenario.RemoveLocal("a.txt");
        scenario.RemoveDropbox("b.txt");
        var summary = scenario.RepairAsync();
        scenario.RequireOnlyNumberedRoots();
        scenario.Require(summary.Effects.ContainsKey(nameof(EffectKind.DeletedDropbox)) &&
            summary.Effects.ContainsKey(nameof(EffectKind.DeletedLocal)),
            "both deletes propagated (" + Effects(summary) + ")");
        scenario.Require(File.Exists(Path.Combine(scenario.LocalRoot, "b.txt")) == false,
            "the engine's local delete removed b.txt");
        if (!Scenario.LiveMode)
            scenario.Require(File.ReadAllText(Path.Combine(scenario.Recovery, "b.txt")) == "B",
                "the engine's local delete went to the scenario recovery folder, not the real Recycle Bin");
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    // 9. An edit beats a delete.
    private static void BodyEditBeatsDelete(Scenario scenario)
    {
        scenario.LocalFile("a.txt", "v1");
        scenario.DropboxFile("a.txt", "v1");
        scenario.RepairAsync();

        scenario.RemoveLocal("a.txt");
        scenario.DropboxFile("a.txt", "v2", Scenario.T1);
        var summary = scenario.RepairAsync();
        scenario.RequireTree(local: ["a.txt=v2"], dropbox: ["a.txt=v2"]);
        scenario.Require(summary.Effects.ContainsKey(nameof(EffectKind.Downloaded)),
            "the edited Dropbox version was restored (" + Effects(summary) + ")");
        scenario.RequireIssues("Info:InfoEditBeatsDelete@a.txt");
        scenario.RequireQuiet();
    }

    // 10. A folder delete with a surviving edited child.
    private static void BodyFolderDeleteEditedChild(Scenario scenario, Side deletedSide, string cloudFolder)
    {
        var localFolder = PathNames.LocalSpelling(cloudFolder);
        scenario.LocalFile(localFolder + "/a.txt", "a");
        scenario.LocalFile(localFolder + "/b.txt", "b");
        scenario.DropboxFile(cloudFolder + "/a.txt", "a");
        scenario.DropboxFile(cloudFolder + "/b.txt", "b");
        scenario.RepairAsync();

        if (deletedSide == Side.Local)
        {
            scenario.DropboxFile(cloudFolder + "/b.txt", "b-edited", Scenario.T1);
            scenario.RemoveLocal(localFolder);
        }
        else
        {
            scenario.LocalFile(localFolder + "/b.txt", "b-edited", Scenario.T1);
            scenario.RemoveDropbox(cloudFolder);
        }
        scenario.RepairAsync();
        scenario.RequireTree(local: [localFolder + "/", localFolder + "/b.txt=b-edited"],
            dropbox: [cloudFolder + "/", cloudFolder + "/b.txt=b-edited"]);
        scenario.RequireIssues("Info:InfoEditBeatsDelete@" +
            (deletedSide == Side.Local ? cloudFolder : localFolder) + "/b.txt");
        scenario.RequireQuiet();
    }

    // 11. Rename and case-only rename on both sides.
    private static void BodyRenameLocal(Scenario scenario)
    {
        scenario.LocalFile("old.txt", "x");
        scenario.DropboxFile("old.txt", "x");
        scenario.RepairAsync();
        scenario.RenameLocal("old.txt", "new.txt");
        scenario.RepairAsync();
        scenario.RequireTree(local: ["new.txt=x"], dropbox: ["new.txt=x"]);
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    private static void BodyRenameDropbox(Scenario scenario)
    {
        scenario.LocalFile("old.txt", "x");
        scenario.DropboxFile("old.txt", "x");
        scenario.RepairAsync();
        scenario.RenameDropbox("old.txt", "new.txt");
        scenario.RepairAsync();
        scenario.RequireTree(local: ["new.txt=x"], dropbox: ["new.txt=x"]);
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    private static void BodyCaseOnlyLocal(Scenario scenario)
    {
        scenario.LocalFile("case.txt", "x");
        scenario.DropboxFile("case.txt", "x");
        scenario.RepairAsync();
        scenario.RenameLocal("case.txt", "Case.txt");
        scenario.RepairAsync();
        scenario.RequireTree(local: ["Case.txt=x"], dropbox: ["Case.txt=x"]);
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    private static void BodyCaseOnlyDropbox(Scenario scenario)
    {
        scenario.LocalFile("case.txt", "x");
        scenario.DropboxFile("case.txt", "x");
        scenario.RepairAsync();
        scenario.RenameDropbox("case.txt", "Case.txt");
        scenario.RepairAsync();
        scenario.RequireTree(local: ["Case.txt=x"], dropbox: ["Case.txt=x"]);
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    // 12. Exclusions without keep on each side, a failed copy that removes nothing, then re-include; and a keep
    // exclusion whose local edits are not uploaded.
    private static void BodyExcludeLocal(Scenario scenario)
    {
        if (scenario.SkipWhenScaled()) return;
        scenario.LocalFolder("ex");
        scenario.LocalFile("ex/x.txt", "x");
        scenario.SetExclusions(new Exclusion(Side.Local, "ex", false));
        scenario.RepairAsync();
        scenario.RequireTree(local: [], dropbox: ["ex/", "ex/x.txt=x"]);

        scenario.SetExclusions();
        scenario.RepairAsync();
        scenario.RequireTree(local: ["ex/", "ex/x.txt=x"], dropbox: ["ex/", "ex/x.txt=x"]);
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    private static void BodyExcludeDropbox(Scenario scenario)
    {
        if (scenario.SkipWhenScaled()) return;
        scenario.DropboxFolder("dx");
        scenario.DropboxFile("dx/y.txt", "y");
        scenario.SetExclusions(new Exclusion(Side.Dropbox, "dx", false));
        scenario.RepairAsync();
        scenario.RequireTree(local: ["dx/", "dx/y.txt=y"], dropbox: []);

        scenario.SetExclusions();
        scenario.RepairAsync();
        scenario.RequireTree(local: ["dx/", "dx/y.txt=y"], dropbox: ["dx/", "dx/y.txt=y"]);
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    private static void BodyExclusionCopyFails(Scenario scenario, string failedPath, string preservedPath)
    {
        if (scenario.SkipWhenScaled()) return;
        scenario.LocalFolder("fail");
        scenario.LocalFile("fail/f.txt", "f");
        scenario.LocalFile("fail/z.txt", "z");
        scenario.SetExclusions(new Exclusion(Side.Local, "fail", false));
        var preserved = preservedPath + "=" + Path.GetFileNameWithoutExtension(preservedPath);
        scenario.RefuseUpload(failedPath);
        var summary = scenario.RepairAsync();
        scenario.RequireTree(local: ["fail/", "fail/f.txt=f", "fail/z.txt=z"],
            dropbox: ["fail/", preserved]);
        scenario.RequireIssues("Failure:DropboxRefused@" + failedPath);
        scenario.Require(summary.Effects.GetValueOrDefault(nameof(EffectKind.Uploaded)) == 1,
            "a failed preservation copy does not lose its safe sibling (" + Effects(summary) + ")");
        var issue = scenario.Store.Issues(scenario.Pair.PairId).Single();

        scenario.RefuseUpload(failedPath);
        var repeated = scenario.RepairAsync();
        scenario.RequireTree(local: ["fail/", "fail/f.txt=f", "fail/z.txt=z"],
            dropbox: ["fail/", preserved]);
        scenario.RequireIssues("Failure:DropboxRefused@" + failedPath);
        scenario.Require(scenario.Store.Issues(scenario.Pair.PairId).Single().Id == issue.Id,
            "repeated failure keeps its issue identity while the exclusion stays pending");
        scenario.Require(repeated.IssuesByKind.Count == 0,
            "the next opportunity retains its existing notice rather than announcing it again");
        var repeatedOutput = LogFields(repeated.LogPath!)
            .Where(fields => fields[1] is nameof(RunEventKind.Issue) or nameof(RunEventKind.Wait)).ToArray();
        scenario.Require(repeatedOutput.Length == 1 && repeatedOutput[0][1] == nameof(RunEventKind.Wait) &&
            repeatedOutput[0][2] == nameof(TextKey.DropboxRefused) && repeatedOutput[0][3] == failedPath,
            "the repeated failed child emits exactly one cause-worded Wait and no new notice");
        RequireNoTransfer(scenario, repeated);

        scenario.LocalFile("fail/new.txt", "new");
        scenario.ClearWriteRefusals();
        var recovered = scenario.RepairAsync();
        scenario.Require(recovered.Effects.GetValueOrDefault(nameof(EffectKind.Uploaded)) == 2,
            "recovery copies the failed and newly arrived children, not the already preserved sibling");
        scenario.RequireTree(local: [], dropbox: ["fail/", "fail/f.txt=f", "fail/new.txt=new", "fail/z.txt=z"]);
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    private static void BodyKeepLocalExclusion(Scenario scenario)
    {
        if (scenario.SkipWhenScaled()) return;
        scenario.LocalFolder("keep");
        scenario.LocalFile("keep/a.txt", "v1");
        scenario.DropboxFolder("keep");
        scenario.DropboxFile("keep/a.txt", "v1");
        scenario.RepairAsync();

        scenario.LocalFile("keep/a.txt", "v2", Scenario.T1);
        scenario.SetExclusions(new Exclusion(Side.Local, "keep", true));
        scenario.RepairAsync();
        scenario.RequireTree(local: ["keep/", "keep/a.txt=v2"], dropbox: ["keep/", "keep/a.txt=v1"]);
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    private static void BodyExcludedSavedOnly(Scenario scenario)
    {
        if (scenario.SkipWhenScaled()) return;
        scenario.LocalFile("keep/a.txt", "retained");
        scenario.DropboxFile("keep/a.txt", "retained");
        scenario.RepairAsync();
        var before = scenario.Store.SyncedByKey(scenario.Pair.PairId, Side.Local, PathNames.Key("keep/a.txt"));
        scenario.SetExclusions(new Exclusion(Side.Local, "keep", true));
        scenario.RemoveLocal("keep/a.txt");
        var summary = scenario.RepairAsync();
        scenario.Require(summary.Completed, "the production excluded saved-only Repair completes");
        RequireNoTransfer(scenario, summary);
        scenario.RequireTree(["keep/"], ["keep/", "keep/a.txt=retained"]);
        scenario.Require(scenario.Store.SyncedByKey(scenario.Pair.PairId, Side.Local,
            PathNames.Key("keep/a.txt")) == before, "the excluded saved-only baseline is preserved");
        scenario.RequireIssues();
    }

    // 13. Resolve a conflict three ways.
    private static void Resolve(Scenario scenario, ResolutionChoice choice)
    {
        if (scenario.SkipWhenScaled()) return;
        scenario.LocalFile("c.txt", "AAA");
        scenario.DropboxFile("c.txt", "BBBB");
        scenario.RepairAsync();
        scenario.RequireIssues("Tie:TieEqualTimes@c.txt");
        var issue = scenario.Store.Issues(scenario.Pair.PairId)
            .Single(candidate => candidate.Kind == IssueKind.Tie);
        ResolveSelected(scenario, "resolve", issue, choice);
        if (choice == ResolutionChoice.KeepBoth) scenario.RequireIssues("Info:InfoKeptBoth@c.txt");
        else scenario.RequireIssues();

        if (choice == ResolutionChoice.UseLocal)
            scenario.RequireTree(local: ["c.txt=AAA"], dropbox: ["c.txt=AAA"]);
        else if (choice == ResolutionChoice.UseDropbox)
            scenario.RequireTree(local: ["c.txt=BBBB"], dropbox: ["c.txt=BBBB"]);
        else
        {
            scenario.RequireKeepBoth("c", "AAA", "BBBB");
        }
        scenario.RequireQuiet();
    }

    // 14. An interrupted replace (saved pending record + stage file) is finished.
    private static void BodyPendingReplace(Scenario scenario)
    {
        scenario.DropboxFile("a.txt", "v1");
        var source = scenario.Dropbox.ReadOneAsync("a.txt", Scenario.Cancellation).GetAwaiter().GetResult()!;
        using var content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("v1"));
        var staged = scenario.Local.StageAsync("a.txt", content, source, Scenario.Cancellation)
            .GetAwaiter().GetResult();
        var stage = ((LocalStaged)staged).Proof;
        var pending = new PendingEffect(1, "pending-replace", 0, PendingKind.LocalReplace,
            PendingPhase.Armed, "a.txt",
            new RemovalProof(source with { Id = "1" }, scenario.LocalVolumeId, source.ContentHash), null, source,
            stage, null, null, false, null);
        scenario.Store.ArmPending(scenario.Pair.PairId, pending);
        var publishArmed = pending with { Generation = 1, Phase = PendingPhase.PublishArmed };
        scenario.Store.AdvancePending(scenario.Pair.PairId, pending, publishArmed);
        staged.DisposeAsync().AsTask().GetAwaiter().GetResult();

        var summary = scenario.RepairAsync();
        scenario.Require(summary.RecoveredPending, "the interrupted replace was resolved");
        scenario.RequireTree(local: ["a.txt=v1"], dropbox: ["a.txt=v1"]);
        scenario.Require(!File.Exists(stage.Path) && scenario.Store.Pending(scenario.Pair.PairId) is NoPending,
            "the stage file was finished into the final file and the pending note cleared");
        scenario.Require((File.GetAttributes(Path.Combine(scenario.LocalRoot, "a.txt")) & FileAttributes.Hidden) == 0,
            "pending publication clears the stage's Hidden attribute");
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    // 15. A failed listing changes nothing; a later run converges and a final run is quiet.
    private static void BodyFailedListing(Scenario scenario)
    {
        scenario.LocalFile("x.txt", "v1");
        scenario.FailNextListings(2);
        var first = scenario.RepairAsync(requireCompleted: false);
        scenario.Require(!first.Completed, "the failed listing left the run incomplete");
        scenario.RequireTree(local: ["x.txt=v1"], dropbox: []);
        scenario.RequireIssues();
        var second = scenario.RepairAsync(requireCompleted: false);
        scenario.Require(!second.Completed, "the second failed listing also left the run incomplete");
        scenario.RequireTree(local: ["x.txt=v1"], dropbox: []);
        scenario.RequireIssues();

        var convergent = scenario.RepairAsync();
        scenario.Require(convergent.Effects.ContainsKey(nameof(EffectKind.Uploaded)),
            "the convergent Repair uploaded the file (" + Effects(convergent) + ")");
        scenario.RequireTree(local: ["x.txt=v1"], dropbox: ["x.txt=v1"]);
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    // An ignore rule: the ignored file is never synced.
    private static void BodyIgnoreRule(Scenario scenario)
    {
        scenario.LocalFile("note.tmp", "temp");
        scenario.LocalFile("keep.txt", "keep");
        scenario.RepairAsync();
        scenario.RequireTree(local: ["keep.txt=keep", "note.tmp=temp"], dropbox: ["keep.txt=keep"]);
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    private static void BodyGzipFileByteIntegrity(Scenario scenario)
    {
        if (scenario.SkipWhenScaled()) return;
        var plain = System.Text.Encoding.UTF8.GetBytes("ordinary bytes stay ordinary; compressed bytes stay compressed\n");
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(plain);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["payload.gz"] = buffer.ToArray(),
            ["ordinary.txt"] = plain,
        };
        foreach (var (path, bytes) in files)
        {
            File.WriteAllBytes(Path.Combine(scenario.LocalRoot, path), bytes);
            File.SetLastWriteTimeUtc(Path.Combine(scenario.LocalRoot, path), Scenario.T0.UtcDateTime);
        }
        var uploaded = scenario.RepairAsync();
        scenario.Require(uploaded.Effects.GetValueOrDefault(nameof(EffectKind.Uploaded)) == files.Count,
            "both original byte sequences reached Dropbox");
        var remote = scenario.DropboxFiles();
        scenario.Require(remote.Count == files.Count, "Dropbox contains exactly the two integrity fixtures");
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, bytes) in files)
        {
            scenario.Require(remote[path].SequenceEqual(bytes), "uploaded bytes are exact at " + path);
            var entry = scenario.Dropbox.ReadOneAsync(path, Scenario.Cancellation).GetAwaiter().GetResult()!;
            hashes.Add(path, entry.ContentHash);
            scenario.Require(entry.ContentHash.Length != 0, "Dropbox supplied a content hash at " + path);
            scenario.RemoveLocal(path);
        }
        // With no saved deletion authority, the next Repair must download through the case's real handler.
        scenario.Store.ForgetAllSynced(scenario.Pair.PairId);
        var downloaded = scenario.RepairAsync();
        scenario.Require(downloaded.Effects.GetValueOrDefault(nameof(EffectKind.Downloaded)) == files.Count,
            "the gzip-format and ordinary file both exercised the download path");
        var local = scenario.LocalFiles();
        scenario.Require(local.Count == files.Count, "downloads leave exactly the two files and no stage");
        foreach (var (path, bytes) in files)
        {
            scenario.Require(local[path].SequenceEqual(bytes), "downloaded bytes are identical at " + path);
            var entry = scenario.Local.ReadOneAsync(path, Scenario.Cancellation).GetAwaiter().GetResult()!;
            var hash = scenario.Local.ContentHashAsync(entry, Scenario.Cancellation).GetAwaiter().GetResult();
            scenario.Require(hash == hashes[path], "downloaded bytes pass the Dropbox content hash at " + path);
        }
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    private static void BodyDefiniteFailureRetry(Scenario scenario)
    {
        if (scenario.SkipWhenScaled()) return;
        scenario.LocalFile("held.txt", "held");
        var local = scenario.Local.ReadOneAsync("held.txt", Scenario.Cancellation).GetAwaiter().GetResult()!;
        // Inject a durable refusal, not a claim that the live account's access controls were changed.
        scenario.Store.Commit(scenario.Pair.PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailDropboxNoPermission, local.Path,
                new IssueSubject([new IssueItem(Side.Local, local.Path, local.Id)], null))]
        });
        var issue = scenario.Store.Issues(scenario.Pair.PairId).Single();
        scenario.Require(issue.Kind == IssueKind.Failure && issue.Cause == TextKey.FailDropboxNoPermission,
            "injected: a durable permission refusal, not a change to account permissions");
        var held = scenario.RepairAsync(denyContentAt: "held.txt");
        RequireNoTransfer(scenario, held);
        scenario.RequireTree(local: ["held.txt=held"], dropbox: []);
        scenario.RequireIssues("Failure:FailDropboxNoPermission@held.txt");
        scenario.Require(scenario.Store.Issues(scenario.Pair.PairId).Single().Id == issue.Id,
            "an ordinary Repair does not replace the held refusal");
        var retried = scenario.RepairAsync(retryIssues: new HashSet<long> { issue.Id });
        scenario.Require(retried.Completed && retried.Effects.GetValueOrDefault(nameof(EffectKind.Uploaded)) == 1,
            "explicit Retry performs the held transfer through one ordinary Repair");
        scenario.RequireTree(local: ["held.txt=held"], dropbox: ["held.txt=held"]);
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    // D37 phase 2: a Dropbox name Windows cannot store stays Skipped (SkipReservedName) until the mapped spelling
    // is activated; the mapped-conflict proof (ClashMappedDiffers) is deferred to that phase-2 trigger.
    private static void BodyMappedConflictProofRetirement(Scenario scenario)
    {
        if (scenario.SkipWhenScaled()) return;
        const string cloudPath = "mapped?.txt";
        scenario.DropboxFile(cloudPath, "BBB");
        var first = scenario.RepairAsync();
        RequireNoTransfer(scenario, first);
        scenario.RequireTree(local: [], dropbox: [cloudPath + "=BBB"]);
        scenario.RequireIssues("Skipped:SkipReservedName@" + cloudPath);
        scenario.RequireQuiet();

        scenario.RemoveDropbox(cloudPath);
        var retired = scenario.RepairAsync();
        RequireNoTransfer(scenario, retired);
        scenario.RequireTree(local: [], dropbox: []);
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    private static void BodyFolderDeleteSavedOnlyChild(Scenario scenario, Side deletedSide)
    {
        if (scenario.SkipWhenScaled()) return;
        scenario.LocalFile("docs/a.txt", "a");
        scenario.LocalFile("docs/gone.txt", "gone");
        scenario.LocalFile("unrelated.txt", "unrelated");
        scenario.RepairAsync();
        var unrelated = scenario.Store.SyncedByKey(scenario.Pair.PairId, Side.Local, PathNames.Key("unrelated.txt"))
            ?? throw new InvalidOperationException("the unrelated saved counterpart is missing before deletion");
        if (deletedSide == Side.Local)
        {
            scenario.RemoveLocal("docs");
            scenario.RemoveDropbox("docs/gone.txt");
        }
        else
        {
            scenario.RemoveDropbox("docs");
            scenario.RemoveLocal("docs/gone.txt");
        }
        var summary = scenario.RepairAsync();
        scenario.Require(summary.Effects.ContainsKey(deletedSide == Side.Local
                ? nameof(EffectKind.DeletedDropbox) : nameof(EffectKind.DeletedLocal)),
            "the unedited remaining folder is deleted");
        scenario.RequireTree(local: ["unrelated.txt=unrelated"], dropbox: ["unrelated.txt=unrelated"]);
        foreach (var path in new[] { "docs", "docs/a.txt", "docs/gone.txt" })
            foreach (var side in new[] { Side.Local, Side.Dropbox })
                scenario.Require(scenario.Store.SyncedByKey(scenario.Pair.PairId, side, PathNames.KeyFor(side, path)) is null,
                    "folder deletion forgets the present and saved-only binding at " + path + " on " + side);
        scenario.Require(scenario.Store.SyncedByKey(scenario.Pair.PairId, Side.Local, unrelated.LocalKey) == unrelated &&
            scenario.Store.SyncedByKey(scenario.Pair.PairId, Side.Dropbox, unrelated.DropboxKey) == unrelated,
            "forgetting the folder does not alter the unrelated counterpart binding");
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    private static void BodyFolderDeleteIgnoredContents(Scenario scenario, Side deletedSide)
    {
        scenario.LocalFile("docs/a.txt", "a");
        scenario.DropboxFile("docs/a.txt", "a");
        scenario.LocalFile("docs/ignored.tmp", "local ignored");
        scenario.DropboxFile("docs/ignored.tmp", "cloud ignored");
        scenario.RepairAsync();
        scenario.RequireTree(local: ["docs/", "docs/a.txt=a", "docs/ignored.tmp=local ignored"],
            dropbox: ["docs/", "docs/a.txt=a", "docs/ignored.tmp=cloud ignored"]);
        if (deletedSide == Side.Local) scenario.RemoveLocal("docs");
        else scenario.RemoveDropbox("docs");
        var summary = scenario.RepairAsync();
        scenario.Require(summary.Effects.ContainsKey(deletedSide == Side.Local
                ? nameof(EffectKind.DeletedDropbox) : nameof(EffectKind.DeletedLocal)),
            "ignored carried contents do not become unreadable-subtree protection");
        scenario.RequireOnlyNumberedRoots();
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    private static void BodyFolderDeleteOrder(Scenario scenario, Side deletedSide)
    {
        if (scenario.SkipWhenScaled()) return;
        scenario.LocalFile("docs/branch/keep.txt", "keep");
        scenario.LocalFile("docs/branch/deep.txt", "deep");
        scenario.LocalFile("docs/a-shallow.txt", "shallow");
        scenario.RepairAsync();
        if (deletedSide == Side.Local)
        {
            scenario.DropboxFile("docs/branch/keep.txt", "edited", Scenario.T1);
            scenario.RemoveLocal("docs");
        }
        else
        {
            scenario.LocalFile("docs/branch/keep.txt", "edited", Scenario.T1);
            scenario.RemoveDropbox("docs");
        }
        var summary = scenario.RepairAsync();
        scenario.RequireTree(local: ["docs/", "docs/branch/", "docs/branch/keep.txt=edited"],
            dropbox: ["docs/", "docs/branch/", "docs/branch/keep.txt=edited"]);
        var effect = deletedSide == Side.Local ? nameof(EffectKind.DeletedDropbox) : nameof(EffectKind.DeletedLocal);
        var deletions = LogFields(summary.LogPath)
            .Where(fields => fields.Length == 6 && fields[1] == nameof(RunEventKind.Effect) && fields[2] == effect)
            .Select(fields => fields[3]).ToArray();
        scenario.Require(deletions.SequenceEqual(new[] { "docs/branch/deep.txt", "docs/a-shallow.txt" }),
            "the completed log records deep deletion before shallow deletion: " + string.Join(",", deletions));
        scenario.RequireIssues("Info:InfoEditBeatsDelete@docs/branch/keep.txt");
        scenario.RequireQuiet();
    }

    // A26: a held survivor takes no action and costs no read; the delete stays partial with its root binding, each
    // Repair reaches it again without growth, and the Repair after the hold clears retires the exhausted follow-up.
    private static void BodyHeldFolderSurvivor(Scenario scenario)
    {
        if (scenario.SkipWhenScaled()) return;
        const string kept = "docs/kept.txt";
        scenario.LocalFile(kept, "keep");
        scenario.LocalFile("docs/removed.txt", "remove");
        scenario.RepairAsync();
        scenario.DropboxFile(kept, "edited", Scenario.T1);
        scenario.RemoveLocal("docs");
        var edited = scenario.Dropbox.ReadOneAsync(kept, Scenario.Cancellation).GetAwaiter().GetResult()!;
        var pairId = scenario.Pair.PairId;
        scenario.Store.Commit(pairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailDropboxNoPermission, kept,
                new IssueSubject([new IssueItem(Side.Dropbox, kept, edited.Id)], null))]
        });
        string[] Active() => scenario.Store.ActiveFolderRemovals(pairId, Scenario.Cancellation)
            .Select(operation => operation.OperationId + ":" + operation.Generation).ToArray();

        scenario.RepairAsync();
        var waiting = Active();
        scenario.Require(waiting.Length == 1, "the held survivor leaves the delete active: " + waiting.Length);
        scenario.RequireTree(local: [], dropbox: ["docs/", "docs/kept.txt=edited"]);
        scenario.RequireIssues("Failure:FailDropboxNoPermission@docs/kept.txt");
        scenario.Require(scenario.Store.SyncedByKey(pairId, Side.Local, PathNames.Key("docs")) is not null,
            "the partial delete keeps its root binding");

        var repeated = scenario.RepairAsync();
        scenario.Require(Active().SequenceEqual(waiting) && repeated.Effects.Count == 0,
            "a repeated Repair adds no operation and no effect while the hold persists");

        scenario.Store.Commit(pairId, StateChange.Empty with { Close = [(kept, IssueClosure.AfterEffect)] });
        scenario.RepairAsync();
        var operation = waiting[0].Split(':')[0];
        scenario.Require(!scenario.Store.PendingFolderFollowUps(pairId, operation, -1, 1).Any(),
            "the Repair after the hold clears completes the follow-up");
        var retired = scenario.Store.FolderRemoval(pairId, operation);
        scenario.Require(Active().Length == 0, "the exhausted partial delete leaves no active folder operation");
        scenario.Require(retired is { Active: false, Phase: FolderRemovalPhase.Retained },
            "the retired operation stays Retained, not Complete: " + retired.Phase);
        scenario.RequireTree(local: ["docs/", "docs/kept.txt=edited"], dropbox: ["docs/", "docs/kept.txt=edited"]);
    }

    private static void BodyCrossLinkedFolderSurvivor(Scenario scenario)
    {
        if (scenario.SkipWhenScaled()) return;
        scenario.LocalFile("docs/kept.txt", "keep");
        scenario.LocalFile("docs/removed.txt", "remove");
        scenario.DropboxFile("docs/label.txt", "keep");
        var local = scenario.Local.ReadOneAsync("docs/kept.txt", Scenario.Cancellation).GetAwaiter().GetResult()!;
        var remote = scenario.Dropbox.ReadOneAsync("docs/label.txt", Scenario.Cancellation).GetAwaiter().GetResult()!;
        scenario.Store.Commit(scenario.Pair.PairId, StateChange.Empty with { Record = [SyncedEntries.From(local, remote)] });
        scenario.Require(scenario.Store.SyncedByKey(scenario.Pair.PairId, Side.Local, PathNames.Key(local.Path))
                ?.DropboxPath == remote.Path,
            "injected: saved cross-linked history, not an observed rename");
        scenario.RepairAsync();
        scenario.RequireTree(local: ["docs/", "docs/kept.txt=keep", "docs/removed.txt=remove"],
            dropbox: ["docs/", "docs/label.txt=keep", "docs/removed.txt=remove"]);
        scenario.DropboxFile("docs/label.txt", "edited", Scenario.T1);
        scenario.RemoveLocal("docs");
        var summary = scenario.RepairAsync();
        scenario.Require(summary.Effects.GetValueOrDefault(nameof(EffectKind.Downloaded)) == 1,
            "the cross-linked restored survivor is downloaded once, not replayed by the folder plan");
        scenario.RequireTree(local: ["docs/", "docs/kept.txt=edited"],
            dropbox: ["docs/", "docs/label.txt=edited"]);
        var saved = scenario.Store.SyncedByKey(scenario.Pair.PairId, Side.Local, PathNames.Key("docs/kept.txt"));
        scenario.Require(saved is { LocalPath: "docs/kept.txt", DropboxPath: "docs/label.txt" } &&
            scenario.Store.SyncedByKey(scenario.Pair.PairId, Side.Dropbox, PathNames.DropboxKey("docs/label.txt")) == saved,
            "the plan retains both keys of the cross-linked survivor");
        scenario.RequireIssues("Info:InfoEditBeatsDelete@docs/label.txt");
        scenario.RequireQuiet();
    }

    // D37 phase 2: both names Windows cannot store stay Skipped; the mapped-collision reconciliation is deferred to
    // the phase-2 mapping trigger, where these two native names are one local spelling.
    private static void BodyMappedCollisionRetirement(Scenario scenario)
    {
        if (scenario.SkipWhenScaled()) return;
        scenario.DropboxFile("clash?.txt", "first");
        scenario.DropboxFile("clash*.txt", "second");
        var blocked = scenario.RepairAsync();
        RequireNoTransfer(scenario, blocked);
        scenario.RequireTree(local: [], dropbox: ["clash?.txt=first", "clash*.txt=second"]);
        scenario.RequireIssues("Skipped:SkipReservedName@clash?.txt", "Skipped:SkipReservedName@clash*.txt");
        scenario.RequireQuiet();
        scenario.RemoveDropbox("clash*.txt");
        var retired = scenario.RepairAsync();
        RequireNoTransfer(scenario, retired);
        scenario.RequireTree(local: [], dropbox: ["clash?.txt=first"]);
        scenario.RequireIssues("Skipped:SkipReservedName@clash?.txt");
        scenario.RequireQuiet();
    }

    private static void BodyStageCleanup(Scenario scenario, bool failCleanup)
    {
        if (scenario.SkipWhenScaled()) return;
        scenario.LocalFile("keep.txt", "keep");
        scenario.LocalFile("ordinary.stage", "ordinary");
        var stage = StageNames.For(Path.Combine(scenario.LocalRoot, "keep.txt"));
        File.WriteAllText(stage, "orphan");
        File.SetAttributes(stage, FileAttributes.Hidden);
        scenario.Require(File.Exists(stage), "injected: an orphaned engine stage beside its target");
        RepairSummary summary;
        using (var locked = failCleanup
            ? new FileStream(stage, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) : null)
        {
            summary = scenario.RepairAsync();
            scenario.Require(File.Exists(stage) == failCleanup,
                "only a deletion-denied orphan survives cleanup");
            scenario.Require(LogFields(summary.LogPath).Any(fields => fields.Length == 6 &&
                    fields[1] == nameof(RunEventKind.Failed) && fields[2] == nameof(IOException) &&
                    fields[5] == nameof(TextKey.StageCleanupFailed))
                == failCleanup, "failed stage cleanup is reported in the run log");
        }
        scenario.Require(summary.Effects.GetValueOrDefault(nameof(EffectKind.Uploaded)) == 2,
            "ordinary files upload while the reserved stage is never uploaded");
        if (failCleanup)
        {
            RequireNoTransfer(scenario, scenario.RepairAsync());
            scenario.Require(!File.Exists(stage), "the next Repair removes the unlocked orphan without retransferring files");
        }
        scenario.RequireTree(local: ["keep.txt=keep", "ordinary.stage=ordinary"],
            dropbox: ["keep.txt=keep", "ordinary.stage=ordinary"]);
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    private static void BodyMoveUnlinked(Scenario scenario, Side movedSide)
    {
        if (scenario.SkipWhenScaled()) return;
        scenario.LocalFile("item", "local");
        scenario.DropboxFile("item/child.txt", "remote");
        scenario.RepairAsync();
        scenario.RequireIssues("NameClash:ClashFileFolder@item");
        var issue = scenario.Store.Issues(scenario.Pair.PairId).Single();
        var outcome = ResolveSelected(scenario, "rename-resolution", issue,
            movedSide == Side.Local ? ResolutionChoice.RenameLocal : ResolutionChoice.RenameDropbox, "renamed");
        scenario.Require(!outcome.Deferred && !outcome.RaisedBlocks(), "the unlinked move and both follow-ups succeed");
        var expected = movedSide == Side.Local
            ? new[] { "renamed=local", "item/", "item/child.txt=remote" }
            : new[] { "item=local", "renamed/", "renamed/child.txt=remote" };
        // Check before another Repair so a missing freed-name follow-up cannot repair itself unnoticed.
        scenario.RequireTree(local: expected, dropbox: expected);
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    private static void BodyFolderRename(Scenario scenario, Side movedSide, bool crossLinked)
    {
        if (scenario.SkipWhenScaled()) return;
        var cloudRoot = crossLinked ? "cloud" : "docs";
        scenario.LocalFile("docs/branch/child.txt", "child");
        scenario.DropboxFile(cloudRoot + "/branch/child.txt", "child");
        if (crossLinked)
        {
            var records = new List<SyncedEntry>();
            foreach (var suffix in new[] { "", "/branch", "/branch/child.txt" })
            {
                var local = scenario.Local.ReadOneAsync("docs" + suffix, Scenario.Cancellation).GetAwaiter().GetResult()!;
                var remote = scenario.Dropbox.ReadOneAsync(cloudRoot + suffix, Scenario.Cancellation).GetAwaiter().GetResult()!;
                records.Add(SyncedEntries.From(local, remote));
            }
            scenario.Store.Commit(scenario.Pair.PairId, StateChange.Empty with { Record = records });
            scenario.Require(records.All(record => scenario.Store.SyncedByKey(scenario.Pair.PairId,
                Side.Local, record.LocalKey) == record), "injected: cross-linked folder and descendant history");
        }
        scenario.RepairAsync();
        if (movedSide == Side.Local)
        {
            scenario.RenameLocal("docs", "moved");
            scenario.RenameLocal("moved/branch", "moved/renamed");
        }
        else
        {
            scenario.RenameDropbox(cloudRoot, "moved");
            scenario.RenameDropbox("moved/branch", "moved/renamed");
        }
        var summary = scenario.RepairAsync();
        scenario.Require(summary.Effects.GetValueOrDefault(movedSide == Side.Local
                ? nameof(EffectKind.MovedDropbox) : nameof(EffectKind.MovedLocal)) == 2,
            "the parent and independently renamed child folder each propagate once");
        scenario.RequireTree(local: ["moved/", "moved/renamed/", "moved/renamed/child.txt=child"],
            dropbox: ["moved/", "moved/renamed/", "moved/renamed/child.txt=child"]);
        var saved = scenario.Store.SyncedByKey(scenario.Pair.PairId, Side.Local,
            PathNames.Key("moved/renamed/child.txt"));
        scenario.Require(saved is { LocalPath: "moved/renamed/child.txt", DropboxPath: "moved/renamed/child.txt" } &&
            scenario.Store.SyncedByKey(scenario.Pair.PairId, Side.Dropbox, saved.DropboxKey) == saved,
            "nested rename rebases both saved descendant paths");
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    private static void BodyUnicodeIdentity(Scenario scenario)
    {
        if (scenario.SkipWhenScaled()) return;
        scenario.LocalFile("σ.txt", "same");
        scenario.DropboxFile("Σ.txt", "same");
        var first = scenario.RepairAsync();
        RequireNoTransfer(scenario, first);
        scenario.RequireTree(local: ["σ.txt=same"], dropbox: ["Σ.txt=same"]);
        var saved = scenario.Store.SyncedByKey(scenario.Pair.PairId, Side.Local, PathNames.Key("σ.txt"));
        scenario.Require(saved is not null && scenario.Store.SyncedByKey(scenario.Pair.PairId,
                Side.Dropbox, PathNames.DropboxKey("Σ.txt")) == saved,
            "frozen Unicode case equivalents share one saved binding");
        scenario.LocalFile("σ.txt", "changed", Scenario.T1);
        scenario.RepairAsync();
        scenario.RequireTree(local: ["σ.txt=changed"], dropbox: ["Σ.txt=changed"]);
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    private static void BodyFolderDeleteListingRecovery(Scenario scenario)
    {
        if (scenario.SkipWhenScaled()) return;
        scenario.LocalFile("docs/child.txt", "child");
        scenario.RepairAsync();
        scenario.RemoveDropbox("docs");
        scenario.FailNextListings(2);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var failed = scenario.RepairAsync(requireCompleted: false);
            scenario.Require(!failed.Completed, "a failed inventory cannot authorize the pending folder deletion");
            RequireNoTransfer(scenario, failed);
            scenario.RequireTree(local: ["docs/", "docs/child.txt=child"], dropbox: []);
            scenario.RequireIssues();
        }
        var recovered = scenario.RepairAsync();
        scenario.Require(recovered.Effects.GetValueOrDefault(nameof(EffectKind.DeletedLocal)) == 1,
            "a complete inventory permits the pending whole-folder deletion once");
        scenario.RequireTree(local: [], dropbox: []);
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    private static void BodyPairFailureRetry(Scenario scenario)
    {
        if (scenario.SkipWhenScaled()) return;
        foreach (var path in new[] { "first.txt", "second.txt" })
        {
            scenario.LocalFile(path, path);
            var local = scenario.Local.ReadOneAsync(path, Scenario.Cancellation).GetAwaiter().GetResult()!;
            scenario.Store.Commit(scenario.Pair.PairId, StateChange.Empty with
            {
                Raise = [(IssueKind.Failure, TextKey.FailDropboxNoPermission, path,
                    new IssueSubject([new IssueItem(Side.Local, path, local.Id)], null))]
            });
        }
        var issues = scenario.Store.Issues(scenario.Pair.PairId, [IssueKind.Failure]);
        scenario.Require(issues.Count == 2, "injected: two durable permission refusals for pair-wide Retry");
        RequireNoTransfer(scenario, scenario.RepairAsync());
        scenario.RequireIssues("Failure:FailDropboxNoPermission@first.txt", "Failure:FailDropboxNoPermission@second.txt");
        var retried = scenario.RepairAsync(retryIssues: issues.Select(issue => issue.Id).ToHashSet());
        scenario.Require(retried.Effects.GetValueOrDefault(nameof(EffectKind.Uploaded)) == 2,
            "one pair-wide retry opportunity performs both held transfers");
        scenario.RequireTree(local: ["first.txt=first.txt", "second.txt=second.txt"],
            dropbox: ["first.txt=first.txt", "second.txt=second.txt"]);
        scenario.RequireIssues();
        RequireNoTransfer(scenario, scenario.RepairAsync(retryIssues: new HashSet<long>()));
        scenario.RequireQuiet();
    }

    private static void BodyFolderDeleteCarriedLink(Scenario scenario)
    {
        if (scenario.SkipWhenScaled()) return;
        scenario.LocalFile("docs/child.txt", "child");
        scenario.RepairAsync();
        var target = Path.Combine(scenario.Removed, "link-target");
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "sentinel.txt");
        File.WriteAllText(sentinel, "outside");
        var junction = Path.Combine(scenario.LocalRoot, "docs", "link");
        try
        {
            scenario.Require(CreateJunction(junction, target) && Directory.Exists(junction),
                "the synthetic folder contains a local junction");
            scenario.RemoveDropbox("docs");
            scenario.RepairAsync();
            scenario.RequireTree(local: [], dropbox: []);
            scenario.Require(File.ReadAllText(sentinel) == "outside",
                "the enclosing deletion carries the link without deleting its target");
            // Skipped notices reflect the completed listing; the next listing retires the carried link.
            scenario.RequireIssues("Skipped:SkipLink@docs/link");
            scenario.RepairAsync();
            scenario.RequireIssues();
            scenario.RequireQuiet();
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction);
            var recovered = Path.Combine(scenario.Recovery, "docs", "link");
            if (Directory.Exists(recovered)) Directory.Delete(recovered);
        }
    }

    private static void BodyResolutionChildRecovery(Scenario scenario)
    {
        if (scenario.SkipWhenScaled()) return;
        scenario.LocalFile("item/a.txt", "a");
        scenario.LocalFile("item/z.txt", "z");
        scenario.DropboxFile("item", "occupant");
        scenario.RepairAsync();
        // RQ-178: the first-sync check for the children cannot finish while the file/folder clash is open.
        scenario.RequireIssues("NameClash:ClashFileFolder@item",
            "Failure:FailFirstSyncCheckWaiting@item/a.txt",
            "Failure:FailFirstSyncCheckWaiting@item/z.txt");
        var issue = scenario.Store.Issues(scenario.Pair.PairId).Single(candidate => candidate.Kind == IssueKind.NameClash);
        scenario.RefuseUpload("item/a.txt");
        var outcome = ResolveSelected(scenario, "folder-resolution", issue, ResolutionChoice.KeepLocal);
        scenario.Require(outcome.Raised == IssueKind.Failure, "the resolution reports its failed child");
        scenario.RequireTree(local: ["item/", "item/a.txt=a", "item/z.txt=z"],
            dropbox: ["item/", "item/z.txt=z"]);
        scenario.RequireIssues("Failure:DropboxRefused@item/a.txt");
        scenario.ClearWriteRefusals();
        var recovered = scenario.RepairAsync();
        scenario.Require(recovered.Effects.GetValueOrDefault(nameof(EffectKind.Uploaded)) == 1 &&
            !recovered.Effects.ContainsKey(nameof(EffectKind.DeletedDropbox)),
            "ordinary Repair retries only the failed child, not the resolved root or successful sibling");
        scenario.RequireTree(local: ["item/", "item/a.txt=a", "item/z.txt=z"],
            dropbox: ["item/", "item/a.txt=a", "item/z.txt=z"]);
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    private static void BodyStaleResolution(Scenario scenario)
    {
        if (scenario.SkipWhenScaled()) return;
        scenario.LocalFile("item", "local");
        scenario.DropboxFolder("item");
        scenario.RepairAsync();
        scenario.RequireIssues("NameClash:ClashFileFolder@item");
        var issue = scenario.Store.Issues(scenario.Pair.PairId).Single();
        scenario.RemoveLocal("item");
        scenario.LocalFile("item", "new occupant");
        TextKey? refusal = null;
        try
        {
            ResolveSelected(scenario, "stale-resolution", issue, ResolutionChoice.RenameLocal, "renamed");
        }
        catch (EngineRequestException error)
        {
            refusal = error.Key;
        }
        scenario.Require(refusal == TextKey.ResolveSubjectStale, "the old resolution refuses the new occupant");
        scenario.RequireTree(local: ["item=new occupant"], dropbox: ["item/"]);
        scenario.RepairAsync();
        var refreshed = scenario.Store.Issues(scenario.Pair.PairId).Single();
        var local = scenario.Local.ReadOneAsync("item", Scenario.Cancellation).GetAwaiter().GetResult()!;
        scenario.Require(refreshed.Subject.Items.Any(item => item.Side == Side.Local && item.Id == local.Id),
            "trusted reconciliation replaces the stale participant with the current occupant");
        scenario.RequireIssues("NameClash:ClashFileFolder@item");
        scenario.RequireQuiet();
    }

    private static void BodyExclusionKeepBoth(Scenario scenario)
    {
        if (scenario.SkipWhenScaled()) return;
        scenario.LocalFile("docs/item.txt", "same");
        scenario.DropboxFile("docs/item.txt", "same");
        scenario.RepairAsync();
        scenario.LocalFile("docs/item.txt", "local", Scenario.T2);
        scenario.DropboxFile("docs/item.txt", "remote", Scenario.T1);
        scenario.SetExclusions(new Exclusion(Side.Local, "docs", false));
        scenario.RepairAsync();
        scenario.Require(scenario.LocalFiles().Count == 0 && scenario.Directories(local: true).Count == 0,
            "the excluded folder is removed without a local label");
        var remote = scenario.DropboxFiles();
        scenario.Require(remote.Count == 2 && System.Text.Encoding.UTF8.GetString(remote["docs/item.txt"]) == "remote",
            "the retained side keeps its original and exactly one conflict copy");
        var copy = remote.Single(item => item.Key != "docs/item.txt");
        scenario.Require(copy.Key.StartsWith("docs/item (Local ", StringComparison.Ordinal) &&
            System.Text.Encoding.UTF8.GetString(copy.Value) == "local", "the retained conflict label preserves local bytes");
        scenario.RequireIssues("Info:InfoKeptBoth@docs/item.txt");
        scenario.RequireQuiet();
    }

    private static void BodyOtherPairPendingStage(Scenario scenario)
    {
        if (scenario.SkipWhenScaled()) return;
        scenario.DropboxFile("item.txt", "content");
        var source = scenario.Dropbox.ReadOneAsync("item.txt", Scenario.Cancellation).GetAwaiter().GetResult()!;
        var stagePath = StageNames.For(Path.Combine(scenario.LocalRoot, "item.txt"));
        File.WriteAllText(stagePath, "content");
        File.SetAttributes(stagePath, File.GetAttributes(stagePath) | FileAttributes.Hidden);
        var stage = new StageProof(stagePath, scenario.LocalVolumeId, "stage-id", source.Size, source.ContentHash);
        var otherPair = scenario.Pair with { PairId = scenario.Pair.PairId + "-other", Enabled = false };
        scenario.Store.SavePair(otherPair);
        var pending = new PendingEffect(1, "other-pair-pending", 0, PendingKind.LocalReplace,
            PendingPhase.Armed, "item.txt",
            new RemovalProof(source with { Id = "1" }, scenario.LocalVolumeId, source.ContentHash), null, source,
            stage, null, null, false, null);
        scenario.Store.ArmPending(otherPair.PairId, pending);
        var publishArmed = pending with { Generation = 1, Phase = PendingPhase.PublishArmed };
        scenario.Store.AdvancePending(otherPair.PairId, pending, publishArmed);
        scenario.Require(scenario.Store.Pending(otherPair.PairId) is CurrentPending current &&
                current.Value.Stage?.Path == stage.Path,
            "injected: a different pair owns the stage found during this pair's listing");
        scenario.RepairAsync();
        scenario.Require(File.Exists(stage.Path) && scenario.Store.Pending(otherPair.PairId) is not NoPending,
            "cleanup preserves another pair's pending stage and ownership");
        scenario.Store.SettlePending(otherPair.PairId, publishArmed, StateChange.Empty);
        scenario.Require(scenario.Store.Pending(otherPair.PairId) is NoPending,
            "injected: the other pair's pending ownership has been released");
        RequireNoTransfer(scenario, scenario.RepairAsync());
        scenario.Require(!File.Exists(stage.Path), "the next full listing cleans the now-unowned stage");
        scenario.RequireTree(local: ["item.txt=content"], dropbox: ["item.txt=content"]);
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    private static void BodyStageLengthValidation(Scenario scenario)
    {
        if (scenario.SkipWhenScaled()) return;
        scenario.LocalFile("item.txt", "keep");
        scenario.DropboxFile("item.txt", "remote", Scenario.T1);
        var source = scenario.Dropbox.ReadOneAsync("item.txt", Scenario.Cancellation).GetAwaiter().GetResult()!;
        foreach (var content in new[] { "short", "overrun" })
        {
            using var bytes = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
            scenario.Require(bytes.Length != source.Size, "injected: transfer length differs from source metadata");
            TextKey? refusal = null;
            try
            {
                var staged = scenario.Local.StageAsync("item.txt", bytes, source, Scenario.Cancellation)
                    .GetAwaiter().GetResult();
                staged.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (ItemFailedException error)
            {
                refusal = error.Cause;
            }
            scenario.Require(refusal == TextKey.DownloadIncomplete,
                "local staging refuses a short or overlong stream before publication");
            scenario.RequireTree(local: ["item.txt=keep"], dropbox: ["item.txt=remote"]);
            scenario.Require(scenario.Store.Pending(scenario.Pair.PairId) is NoPending,
                "a rejected stage leaves no pending publication");
        }
        scenario.RepairAsync();
        scenario.RequireTree(local: ["item.txt=remote"], dropbox: ["item.txt=remote"]);
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    // 39. A stored legacy time tie preserves both copies instead of letting elapsed time pick one.
    private static void BodyLegacyTie(Scenario scenario, bool localOlder, bool dismissed)
    {
        if (scenario.SkipWhenScaled()) return;
        var old = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
        var recent = new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);
        scenario.LocalFile("legacy.txt", localOlder ? "OLD" : "NEW", localOlder ? old : recent);
        scenario.DropboxFile("legacy.txt", localOlder ? "NEW" : "OLD", localOlder ? recent : old);
        var issue = SeedLegacyTie(scenario, "legacy.txt");
        if (dismissed) scenario.Store.DismissIssue(scenario.Pair.PairId, issue);
        var summary = scenario.RepairAsync();
        // D15/D29: a stored legacy tie keeps its hold and choices; no clock or content picks a winner.
        scenario.Require(summary.Effects.Count == 0,
            "the stored legacy tie stays held with no effect (" + Effects(summary) + ")");
        scenario.RequireTree(local: ["legacy.txt=" + (localOlder ? "OLD" : "NEW")],
            dropbox: ["legacy.txt=" + (localOlder ? "NEW" : "OLD")]);
        scenario.RequireIssues("Tie:TieUnreliableTime@legacy.txt");
    }

    private static void BodyLegacyTieEqualContent(Scenario scenario)
    {
        if (scenario.SkipWhenScaled()) return;
        scenario.LocalFile("same.txt", "same", new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero));
        scenario.DropboxFile("same.txt", "same", new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero));
        SeedLegacyTie(scenario, "same.txt");
        scenario.RepairAsync();
        scenario.RequireTree(local: ["same.txt=same"], dropbox: ["same.txt=same"]);
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    // 39e/f. A first-run equal-size pair with an unreliable time is content-compared, then kept both.
    private static void BodyUnreliableFirstRun(Scenario scenario, bool localFuture)
    {
        if (scenario.SkipWhenScaled()) return;
        var future = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var past = new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);
        scenario.LocalFile("clock.txt", "AAA", localFuture ? future : past);
        scenario.DropboxFile("clock.txt", "BBB", localFuture ? past : future);
        var summary = scenario.RepairAsync();
        scenario.RequireKeepBoth("clock", "AAA", "BBB");
        scenario.Require(summary.Effects.ContainsKey(nameof(EffectKind.KeptBoth)),
            "an unreliable-time first-run pair keeps both copies (" + Effects(summary) + ")");
        scenario.RequireIssues("Info:InfoKeptBoth@clock.txt");
        scenario.RequireQuiet();
    }

    private static void BodyUnreliableEqualContent(Scenario scenario)
    {
        if (scenario.SkipWhenScaled()) return;
        scenario.LocalFile("equal.txt", "same", new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero));
        scenario.DropboxFile("equal.txt", "same", new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero));
        var summary = scenario.RepairAsync();
        scenario.RequireTree(local: ["equal.txt=same"], dropbox: ["equal.txt=same"]);
        scenario.Require(summary.Effects.GetValueOrDefault(nameof(EffectKind.Recorded)) == ScaledRows,
            "identical unreliable-time content is recorded (" + Effects(summary) + ")");
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    private static long SeedLegacyTie(Scenario scenario, string path)
    {
        var local = scenario.Local.ReadOneAsync(path, Scenario.Cancellation).GetAwaiter().GetResult()!;
        var dropbox = scenario.Dropbox.ReadOneAsync(path, Scenario.Cancellation).GetAwaiter().GetResult()!;
        scenario.Store.Commit(scenario.Pair.PairId, StateChange.Empty with
        {
            Raise =
            [
                (IssueKind.Tie, TextKey.TieUnreliableTime, path,
                    new IssueSubject(
                        [new IssueItem(Side.Local, local.Path, local.Id),
                         new IssueItem(Side.Dropbox, dropbox.Path, dropbox.Id)], null))
            ]
        });
        return scenario.Store.Issues(scenario.Pair.PairId).Single(issue => issue.Kind == IssueKind.Tie).Id;
    }

    // 40. A remote folder removed by the arrangement route is absent before Repair; the local tree and the
    // saved evidence stay unchanged until Repair propagates the deletion.
    private static void BodyRemoteFolderRemovalBeforeRepair(Scenario scenario)
    {
        if (scenario.SkipWhenScaled()) return;
        scenario.LocalFile("docs/a.txt", "a");
        scenario.LocalFile("docs/b.txt", "b");
        scenario.DropboxFile("docs/a.txt", "a");
        scenario.DropboxFile("docs/b.txt", "b");
        scenario.RepairAsync();
        var localBefore = scenario.LocalFiles().OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Key + "=" + Convert.ToBase64String(item.Value)).ToArray();
        var savedBefore = scenario.Store.SyncedByKey(scenario.Pair.PairId, Side.Dropbox,
            PathNames.DropboxKey("docs/a.txt"));
        scenario.Require(savedBefore is not null, "the pre-removal mapping exists");

        scenario.RemoveDropbox("docs");
        scenario.Require(scenario.Account.Snapshot().All(item =>
                !item.Path.Equals("docs", StringComparison.OrdinalIgnoreCase) &&
                !item.Path.StartsWith("docs/", StringComparison.OrdinalIgnoreCase)),
            "the remote folder and its contents are absent before Repair");
        var localAfter = scenario.LocalFiles().OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Key + "=" + Convert.ToBase64String(item.Value)).ToArray();
        scenario.Require(localBefore.SequenceEqual(localAfter),
            "the local tree is unchanged until Repair");
        scenario.Require(scenario.Store.SyncedByKey(scenario.Pair.PairId, Side.Dropbox,
                PathNames.DropboxKey("docs/a.txt")) == savedBefore,
            "the saved mapping is unchanged until Repair");
        scenario.RequireIssues();

        scenario.RepairAsync();
        scenario.RequireOnlyNumberedRoots();
        scenario.RequireIssues();
        scenario.RequireQuiet();
    }

    // BUG-132: only Pause stops the Monitor-admitted attempt; the next checkpoint proves its catch has finished.
    private static void BodyPauseDuringPrerequisite(Scenario scenario, bool admissionFirst, Exception? failure = null,
        bool acquisition = false)
    {
        scenario.LocalFile("pause/note.txt", "pause fixture");
        using var inFlight = new ManualResetEventSlim();
        using var released = new ManualResetEventSlim();
        using var diagnosed = new ManualResetEventSlim();
        using var nextCheckpoint = new ManualResetEventSlim();
        using var nextReleased = new ManualResetEventSlim();
        using var cancellationDelivered = new ManualResetEventSlim();
        var checkpoints = 0;
        var drain = new DeferredSynchronizationContext();
        Task? paused = null;
        Task? cancellationWitness = null;
        scenario.BeforeRepairCheckpoint = () =>
        {
            if (Interlocked.Increment(ref checkpoints) == 1 && !acquisition)
            {
                inFlight.Set();
                released.Wait(Scenario.Cancellation);
                if (failure is not null) throw failure;
            }
            else
            {
                nextCheckpoint.Set();
                nextReleased.Wait(Scenario.Cancellation);
            }
        };
        scenario.Progress = message =>
        {
            // This heading is emitted inside Acquisition, outside the host lock and before source-base work.
            if (acquisition && message.Contains(Text.Get(TextKey.StageSourceBase), StringComparison.OrdinalIgnoreCase) &&
                !inFlight.IsSet)
            {
                inFlight.Set();
                released.Wait(Scenario.Cancellation);
            }
            if (message.Contains(Text.Get(TextKey.HumanStopped), StringComparison.Ordinal) ||
                message.Contains(Text.Get(TextKey.HumanError), StringComparison.Ordinal)) diagnosed.Set();
        };
        try
        {
            scenario.Start();
            scenario.Require(inFlight.Wait(TimeSpan.FromMinutes(1)),
                "Monitor's prerequisite reached its held acquisition or Repair checkpoint");
            var retryBefore = scenario.Store.ReadPrerequisiteRetry(scenario.Pair.PairId);
            if (!admissionFirst)
                cancellationWitness = scenario.Host.DispatchAsync(scenario.Pair.PairId, async (_, _, _, token) =>
                {
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { cancellationDelivered.Set(); }
                    return true;
                }, Scenario.Cancellation);
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(drain);
            try { paused = scenario.Host.PauseAsync(scenario.Pair.PairId, Scenario.Cancellation); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            scenario.Require(scenario.Host.IsPaused(scenario.Pair.PairId), "Pause has closed dispatch admission");
            if (acquisition)
            {
                drain.Release();
                paused.GetAwaiter().GetResult();
                var refused = false;
                try
                {
                    scenario.Host.DispatchAsync(scenario.Pair.PairId, (_, _, _, _) => Task.FromResult(true),
                        Scenario.Cancellation).GetAwaiter().GetResult();
                }
                catch (LiveAdmissionStaleException) { refused = true; }
                scenario.Require(refused, "ordinary Dispatch remains stale even with a paused prerequisite pending");
            }
            if (!admissionFirst)
            {
                drain.Release();
                // Drain cancels the held Repair before this later dispatch; its linked cancellation has completed.
                scenario.Require(cancellationDelivered.Wait(TimeSpan.FromMinutes(1)), "Pause delivered cancellation");
            }
            released.Set();
            scenario.Require(diagnosed.Wait(TimeSpan.FromMinutes(1)), "the old attempt recorded its diagnosis");
            drain.Release();
            paused.GetAwaiter().GetResult();
            scenario.Host.ResumeAsync(scenario.Pair.PairId, Scenario.Cancellation).GetAwaiter().GetResult();
            scenario.Require(nextCheckpoint.Wait(TimeSpan.FromMinutes(1)),
                "the next Monitor prerequisite reached its checkpoint after the old Monitor catch completed");
            var lines = Directory.EnumerateFiles(scenario.Logs, "*", SearchOption.AllDirectories)
                .SelectMany(LogFields).ToArray();
            scenario.Require(lines.Count(fields => fields.Length == RunLog.RecordFieldCount &&
                    fields[RunLog.FieldOutcome] == nameof(TextKey.RequiredRepairRetry)) == (failure is null ? 0 : 1),
                "Pause suppresses only its own stop, not a genuine failure or unrelated stale refusal");
            if (failure is null)
                scenario.Require(lines.Count(fields => fields.Length == RunLog.RecordFieldCount &&
                        fields[RunLog.FieldKind] == nameof(RunEventKind.Stage) &&
                        fields[RunLog.FieldWhat] == nameof(TextKey.StageCheckpoint) &&
                        fields[RunLog.FieldOutcome].Length == 0) == (admissionFirst && !acquisition ? 1 : 0),
                    "the old Repair reached its checkpoint fence only in the dispatch-admission-first ordering");
            scenario.Require(scenario.Store.ReadPrerequisiteRetry(scenario.Pair.PairId) == retryBefore,
                "Pause and resume leave prerequisite retry accounting unchanged");
        }
        finally
        {
            released.Set();
            nextReleased.Set();
            drain.Release();
            paused?.GetAwaiter().GetResult();
            cancellationWitness?.GetAwaiter().GetResult();
            scenario.BeforeRepairCheckpoint = null;
            scenario.Progress = null;
            scenario.Host.PauseAsync(scenario.Pair.PairId, Scenario.Cancellation).GetAwaiter().GetResult();
        }
    }

    private static void BodyExplicitPrerequisiteCancellation(Scenario scenario)
    {
        scenario.LocalFile("pause/note.txt", "pause fixture");
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Scenario.Cancellation);
        var stoppedDiagnoses = 0;
        scenario.Progress = message =>
        {
            if (message.Contains(Text.Get(TextKey.HumanStopped), StringComparison.Ordinal))
                Interlocked.Increment(ref stoppedDiagnoses);
        };
        scenario.BeforeRepairCheckpoint = caller.Cancel;
        Exception? stopped = null;
        try { scenario.Host.Operations.RepairAsync(scenario.Pair.PairId, caller.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException error) { stopped = error; }
        finally { scenario.BeforeRepairCheckpoint = null; scenario.Progress = null; }
        scenario.Require(stopped is PrerequisiteStoppedException cancellation &&
                cancellation.InnerException is OperationCanceledException original &&
                cancellation.CancellationToken == original.CancellationToken && cancellation.CancellationToken.IsCancellationRequested,
            "explicit prerequisite Repair retains its classified stop and underlying cancellation token");
        scenario.Require(stoppedDiagnoses == 1, "explicit prerequisite Repair emits exactly one stopped diagnosis");
        var lines = Directory.EnumerateFiles(scenario.Root, "*" + ResourceNames.LogExtension, SearchOption.AllDirectories)
            .SelectMany(LogFields).ToArray();
        scenario.Require(lines.Count(fields => fields.Length == RunLog.RecordFieldCount &&
                fields[RunLog.FieldKind] == nameof(RunEventKind.Failed) &&
                fields[RunLog.FieldWhat] == nameof(OperationCanceledException)) == 1,
            "the explicit Repair's canonical log also contains exactly one stopped diagnosis");
        scenario.Require(scenario.Store.ReadPrerequisiteRetry(scenario.Pair.PairId) is null,
            "explicit cancellation records no prerequisite retry");
    }

    /// <summary>The actual live-account construction seam, asserted over the in-memory wire and fake credentials.</summary>
    private static void LiveCompositionSeamChecks()
    {
        var cases = new (string Name, Action Body)[]
        {
            ("folder-delete-once", SeamFolderDeleteOnce),
            ("initial-absence-no-op", SeamInitialAbsenceNoOp),
            ("publication-and-removal-required", SeamPublicationAndRemovalRequired),
            ("refused-scope-zero-send", SeamRefusedScopeZeroSend),
            ("stale-target-zero-send", SeamStaleTargetZeroSend),
            ("wrong-kind-zero-send", SeamWrongKindZeroSend),
            ("lost-delete-reply-one-send", SeamLostDeleteReplyOneSend),
            ("delete-refused-one-send", SeamDeleteRefusedOneSend),
            ("malformed-receipt-one-send", SeamMalformedReceiptOneSend),
            ("mismatched-receipt-one-send", SeamMismatchedReceiptOneSend),
            ("live-id-elsewhere-fails", SeamLiveIdElsewhereFails),
            ("observation-error-fails", SeamObservationErrorFails),
            ("observation-malformed-fails", SeamObservationMalformedFails),
            ("path-still-present-fails", SeamPathStillPresentFails),
            ("token-failure-zero-send", SeamTokenFailureZeroSend),
            ("observer-outcomes", SeamObserverOutcomes),
            ("live-root-cleanup", SeamLiveRootCleanup),
        };
        var failures = new List<string>();
        foreach (var item in cases)
        {
            try
            {
                item.Body();
                Console.WriteLine("PASS seam " + item.Name);
            }
            catch (Exception error)
            {
                failures.Add(item.Name + ": " + error.Message);
                Console.Error.WriteLine("FAIL seam " + item.Name + ": " + error.Message);
            }
        }
        Check.Require(failures.Count == 0, "live arrangement seam failures: " + string.Join(" | ", failures));
    }

    private static void SeamFolderDeleteOnce()
    {
        using var rig = new ArrangementRig();
        var docs = rig.SeedFolder("docs");
        rig.Account.Delete("docs");
        Seam(rig.Read("docs") is null, true, "the arranged folder is absent after the external-actor delete");
        var deletes = rig.Route(DropboxProtocol.Delete);
        Seam(deletes.Count, 1, "exactly one delete request");
        var delete = deletes[0];
        Seam(delete.Method, "POST", "the delete method");
        Seam(delete.Url, Scenario.DropboxApiRoot + DropboxProtocol.Delete, "the exact delete URL");
        Seam(delete.Authorization ?? "", "Bearer fixture-access", "the per-request Bearer header");
        Seam(PathRoot(delete.PathRoot), ArrangementRig.PathRootJson, "the per-request path-root header");
        using (var body = JsonDocument.Parse(delete.Body))
        {
            Seam(body.RootElement.GetProperty(DropboxProtocol.Path).GetString() ?? "", docs.Id,
                "the delete body names the frozen full id");
            Seam(body.RootElement.EnumerateObject().Count(), 1, "the delete body carries only the frozen id");
        }
        var observations = rig.Route(DropboxProtocol.GetMetadata)
            .Where(request => ArrangementRig.BodyPath(request.Body) == docs.Id).ToArray();
        Seam(observations.Length, 1, "one frozen-id get_metadata observation");
        using (var body = JsonDocument.Parse(observations[0].Body))
        {
            Seam(body.RootElement.GetProperty(DropboxProtocol.Path).GetString() ?? "", docs.Id,
                "get_metadata addresses the frozen full id directly");
            Seam(body.RootElement.GetProperty(DropboxProtocol.IncludeDeleted).GetBoolean(), false,
                "get_metadata sends include_deleted false");
        }
        Seam(observations[0].Authorization ?? "", "Bearer fixture-access", "the observation's Bearer header");
        Seam(PathRoot(observations[0].PathRoot), ArrangementRig.PathRootJson,
            "the observation's path-root header");
        Seam(rig.Route(DropboxProtocol.GetMetadata).Any(request =>
                ArrangementRig.BodyPath(request.Body) == ArrangementRig.Remote("docs")), true,
            "the original-path read is a separate metadata request");
        Seam(rig.Recorder.Requests.Any(request =>
                ArrangementRig.BodyPath(request.Body).Contains("/root/", StringComparison.Ordinal) ||
                ArrangementRig.BodyPath(request.Body).StartsWith("/root/id:", StringComparison.Ordinal)), false,
            "no frozen id is spelled as a /root/id path");
        Seam(rig.Route(DropboxProtocol.DeleteBatch).Count, 0, "the production submission port is not used");
        rig.Account.Delete("docs");
        Seam(rig.Route(DropboxProtocol.Delete).Count, 1, "a second arrangement of the absent folder sends nothing");
    }

    private static void SeamInitialAbsenceNoOp()
    {
        using var rig = new ArrangementRig();
        rig.Account.Delete("missing");
        Seam(rig.Route(DropboxProtocol.Delete).Count, 0, "an initially absent folder sends no delete");
    }

    private static void SeamPublicationAndRemovalRequired()
    {
        using (var rig = new ArrangementRig())
        {
            rig.Wire.NameWriteErrors["file.txt"] = "conflict";
            var refused = false;
            try { rig.Account.SeedFile("file.txt", "content", Scenario.T0); }
            catch (Exception) { refused = true; }
            Seam(refused, true, "a file publication that is not Published fails setup");
        }
        using (var rig = new ArrangementRig())
        {
            rig.Account.SeedFile("file.txt", "content", Scenario.T0);
            rig.Wire.Script("/2/" + DropboxProtocol.Delete, HttpStatusCode.Conflict, 1);
            var refused = false;
            try { rig.Account.Delete("file.txt"); }
            catch (Exception) { refused = true; }
            Seam(refused, true, "a file removal that is not Removed fails setup");
            Seam(rig.Read("file.txt") is not null, true, "the retained file is untouched");
        }
    }

    private static void SeamRefusedScopeZeroSend()
    {
        using var rig = new ArrangementRig();
        var sandbox = rig.SeedFolder("sandbox");
        var outside = rig.SeedFolder("outside");
        Seam(Throws(() => rig.Complete("sandbox", sandbox.Id, "outside", outside.Id)), true,
            "an outside-sandbox target is refused before any delete");
        Seam(rig.Route(DropboxProtocol.Delete).Count, 0, "a refused scope sends zero deletes");
    }

    private static void SeamStaleTargetZeroSend()
    {
        using var rig = new ArrangementRig();
        var docs = rig.SeedFolder("docs");
        var other = rig.SeedFolder("other");
        Seam(Throws(() => rig.Complete("", rig.RootId, "docs", other.Id)), true,
            "a stale expected folder identity is refused before any delete");
        Seam(rig.Route(DropboxProtocol.Delete).Count, 0, "a stale target sends zero deletes");
        Seam(rig.Read("docs")?.Id ?? "", docs.Id, "the current folder is unchanged");
    }

    private static void SeamWrongKindZeroSend()
    {
        using var rig = new ArrangementRig();
        rig.Account.SeedFile("notfolder.txt", "content", Scenario.T0);
        var file = rig.Read("notfolder.txt")!;
        Seam(Throws(() => rig.Complete("", rig.RootId, "notfolder.txt", file.Id)), true,
            "a file at the arrangement target is refused before any delete");
        Seam(rig.Route(DropboxProtocol.Delete).Count, 0, "a wrong-kind target sends zero deletes");
    }

    private static void SeamLostDeleteReplyOneSend()
    {
        using var rig = new ArrangementRig();
        rig.SeedFolder("docs");
        rig.Recorder.Intercept = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/" + DropboxProtocol.Delete, StringComparison.Ordinal))
                throw new HttpRequestException("lost reply");
            return null;
        };
        Seam(Throws(() => rig.Account.Delete("docs")), true, "a lost delete reply fails setup");
        Seam(rig.Route(DropboxProtocol.Delete).Count, 1, "a lost delete reply is not resent");
    }

    private static void SeamDeleteRefusedOneSend()
    {
        using var rig = new ArrangementRig();
        rig.SeedFolder("docs");
        rig.Recorder.Intercept = (request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("/" + DropboxProtocol.Delete, StringComparison.Ordinal)
                ? ArrangementRig.Json(new { error = new { path = new { tag = "no_write_permission" } } },
                    HttpStatusCode.Forbidden)
                : null;
        Seam(Throws(() => rig.Account.Delete("docs")), true, "a provider refusal fails setup");
        Seam(rig.Route(DropboxProtocol.Delete).Count, 1, "a provider refusal is not retried");
    }

    private static void SeamMalformedReceiptOneSend()
    {
        using var rig = new ArrangementRig();
        rig.SeedFolder("docs");
        rig.Recorder.Intercept = (request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("/" + DropboxProtocol.Delete, StringComparison.Ordinal)
                ? ArrangementRig.Raw("{}")
                : null;
        Seam(Throws(() => rig.Account.Delete("docs")), true, "a malformed delete receipt fails setup");
        Seam(rig.Route(DropboxProtocol.Delete).Count, 1, "a malformed receipt is not retried");
    }

    private static void SeamMismatchedReceiptOneSend()
    {
        using var rig = new ArrangementRig();
        rig.SeedFolder("docs");
        rig.Recorder.Intercept = (request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("/" + DropboxProtocol.Delete, StringComparison.Ordinal)
                ? ArrangementRig.Json(ArrangementRig.Wrapped(
                    ArrangementRig.Folder("id:other", ArrangementRig.Remote("docs")), DropboxProtocol.Metadata))
                : null;
        Seam(Throws(() => rig.Account.Delete("docs")), true, "a mismatched delete receipt fails setup");
        Seam(rig.Route(DropboxProtocol.Delete).Count, 1, "a mismatched receipt is not retried");
    }

    private static void SeamLiveIdElsewhereFails()
    {
        using var rig = new ArrangementRig();
        var docs = rig.SeedFolder("docs");
        rig.Recorder.Intercept = (request, body) =>
            request.RequestUri!.AbsolutePath.EndsWith("/" + DropboxProtocol.GetMetadata, StringComparison.Ordinal) &&
            ArrangementRig.BodyPath(body) == docs.Id
                ? ArrangementRig.Json(ArrangementRig.Folder(docs.Id, "/Elsewhere"))
                : null;
        Seam(Throws(() => rig.Account.Delete("docs")), true,
            "a live id at another path fails the arrangement");
        Seam(rig.Route(DropboxProtocol.Delete).Count, 1, "a live id at another path is not deleted twice");
    }

    private static void SeamObservationErrorFails()
    {
        using var rig = new ArrangementRig();
        var docs = rig.SeedFolder("docs");
        rig.Recorder.Intercept = (request, body) =>
            request.RequestUri!.AbsolutePath.EndsWith("/" + DropboxProtocol.GetMetadata, StringComparison.Ordinal) &&
            ArrangementRig.BodyPath(body) == docs.Id
                ? ArrangementRig.Raw("{}", HttpStatusCode.InternalServerError)
                : null;
        Seam(Throws(() => rig.Account.Delete("docs")), true, "a non-not-found provider error fails setup");
        Seam(rig.Route(DropboxProtocol.Delete).Count, 1, "an uncertain observation is not retried");
    }

    private static void SeamObservationMalformedFails()
    {
        using var rig = new ArrangementRig();
        var docs = rig.SeedFolder("docs");
        rig.Recorder.Intercept = (request, body) =>
            request.RequestUri!.AbsolutePath.EndsWith("/" + DropboxProtocol.GetMetadata, StringComparison.Ordinal) &&
            ArrangementRig.BodyPath(body) == docs.Id
                ? ArrangementRig.Raw("not-json")
                : null;
        Seam(Throws(() => rig.Account.Delete("docs")), true, "a malformed observation fails setup");
        Seam(rig.Route(DropboxProtocol.Delete).Count, 1, "a malformed observation is not retried");
    }

    private static void SeamPathStillPresentFails()
    {
        using var rig = new ArrangementRig();
        var docs = rig.SeedFolder("docs");
        rig.Recorder.Intercept = (request, body) =>
            request.RequestUri!.AbsolutePath.EndsWith("/" + DropboxProtocol.GetMetadata, StringComparison.Ordinal) &&
            ArrangementRig.BodyPath(body) == ArrangementRig.Remote("docs") &&
            rig.Route(DropboxProtocol.Delete).Count > 0
                ? ArrangementRig.Json(ArrangementRig.Folder(docs.Id, ArrangementRig.Remote("docs")))
                : null;
        Seam(Throws(() => rig.Account.Delete("docs")), true,
            "a folder still present at the original path fails setup");
        Seam(rig.Route(DropboxProtocol.Delete).Count, 1, "a still-present path is not deleted twice");
    }

    private static void SeamTokenFailureZeroSend()
    {
        using var rig = new ArrangementRig();
        var docs = rig.SeedFolder("docs");
        Seam(Throws(() => LiveDropboxAccount.CompleteFolderArrangementAsync("", rig.RootId, "docs", docs.Id,
            Scenario.FolderDeleteSender(rig.Http, _ => throw new InvalidOperationException("no token"),
                ArrangementRig.Namespace),
            Scenario.FolderIdObserver(rig.Http, rig.AccessToken, ArrangementRig.Namespace),
            rig.ReadOnce, Scenario.Cancellation).GetAwaiter().GetResult()), true,
            "a token failure fails setup");
        Seam(rig.Route(DropboxProtocol.Delete).Count, 0, "a token failure sends zero deletes");
    }

    private static void SeamObserverOutcomes()
    {
        using var rig = new ArrangementRig();
        var docs = rig.SeedFolder("docs");
        var observer = Scenario.FolderIdObserver(rig.Http, rig.AccessToken, ArrangementRig.Namespace);
        Seam(observer("id:absent", Scenario.Cancellation).GetAwaiter().GetResult(),
            new FolderIdObservation.AuthoritativeNotFound(), "an absent id is authoritative absence");
        var live = observer(docs.Id, Scenario.Cancellation).GetAwaiter().GetResult();
        Seam(live, new FolderIdObservation.Live(docs.Id, true, ArrangementRig.Remote("docs")),
            "a live folder id is a location");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Seam(observer(docs.Id, cancelled.Token).GetAwaiter().GetResult(),
                new FolderIdObservation.Uncertain(), "cancellation is uncertain, never absence");
        }
        rig.Recorder.Intercept = (request, body) =>
            request.RequestUri!.AbsolutePath.EndsWith("/" + DropboxProtocol.GetMetadata, StringComparison.Ordinal) &&
            ArrangementRig.BodyPath(body) == docs.Id
                ? ArrangementRig.Raw("{}", HttpStatusCode.Unauthorized)
                : null;
        Seam(observer(docs.Id, Scenario.Cancellation).GetAwaiter().GetResult(),
            new FolderIdObservation.Uncertain(), "a wrong-account/provider error is uncertain");
        rig.Recorder.Intercept = (request, body) =>
            request.RequestUri!.AbsolutePath.EndsWith("/" + DropboxProtocol.GetMetadata, StringComparison.Ordinal) &&
            ArrangementRig.BodyPath(body) == docs.Id
                ? ArrangementRig.Json(ArrangementRig.Folder(docs.Id, "/Elsewhere"))
                : null;
        Seam(observer(docs.Id, Scenario.Cancellation).GetAwaiter().GetResult(),
            new FolderIdObservation.Live(docs.Id, true, "/Elsewhere"), "a live id at another path is reported as live");
    }

    private static void SeamLiveRootCleanup()
    {
        using (var rig = new ArrangementRig("live-root"))
        {
            Scenario.CompleteLiveRootAsync(rig.Side, rig.Http, rig.Tokens, ArrangementRig.Namespace, rig.RootId,
                Scenario.Cancellation).GetAwaiter().GetResult();
            var deletes = rig.Route(DropboxProtocol.Delete);
            Seam(deletes.Count, 1, "a valid owned top-level root is deleted once");
            Seam(ArrangementRig.BodyPath(deletes[0].Body), rig.RootId, "the root delete names the frozen top-level id");
        }
        using (var rig = new ArrangementRig())
        {
            Seam(Throws(() => Scenario.CompleteLiveRootAsync(rig.Side, rig.Http, rig.Tokens,
                ArrangementRig.Namespace, "id:replaced", Scenario.Cancellation).GetAwaiter().GetResult()), true,
                "a replaced top-level root is refused");
            Seam(rig.Route(DropboxProtocol.Delete).Count, 0, "a replaced root sends zero deletes");
        }
    }

    private static void RetainedStartupChecks()
    {
        using var rig = new ArrangementRig();
        var created = new Entry("/", true, 0, Scenario.T0, "id:created", "", "", null);
        var diagnostic = Scenario.RetainedRootDiagnostic(created);
        Seam(diagnostic.Contains("path=/Apps/Sync Shark/Testing/engine-scenarios", StringComparison.Ordinal), true,
            "the retained-root diagnostic names the full provider root path");
        Seam(diagnostic.Contains("id=id:created", StringComparison.Ordinal), true,
            "the retained-root diagnostic names the actual creation receipt id");
        Seam(diagnostic.Contains("no folder cleanup was attempted", StringComparison.Ordinal), true,
            "the retained-root diagnostic states that no cleanup was attempted");
        Seam(rig.Route(DropboxProtocol.Delete).Count, 0, "the retained-root path sends zero folder deletes");
        var original = new InvalidOperationException("original startup failure");
        var failure = Scenario.StartupFailure(original, [diagnostic]);
        Seam(failure.Message.StartsWith(original.Message, StringComparison.Ordinal), true,
            "the original startup error is preserved");
        Seam(ReferenceEquals(failure.InnerException, original), true,
            "the original startup exception is retained as the cause");
        Seam(failure.Message.Contains(diagnostic, StringComparison.Ordinal), true,
            "the retained-root diagnostic is reported with the original error");
        Seam(diagnostic.Contains("path=/;", StringComparison.Ordinal), false,
            "the diagnostic never uses the pair-relative receipt path");
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (Exception) { return true; }
    }

    private static void Seam<T>(T observed, T expected, string what) =>
        Check.Require(EqualityComparer<T>.Default.Equals(observed, expected),
            what + " (expected: " + expected + "; observed: " + observed + ")");

    private static string PathRoot(string? header)
    {
        if (header is null) return "<missing>";
        using var document = JsonDocument.Parse(header);
        return JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [DropboxProtocol.Tag] = DropboxProtocol.Root,
            [DropboxProtocol.Root] = document.RootElement.GetProperty(DropboxProtocol.Root).GetString()!,
        });
    }

    /// <summary>One in-memory Dropbox side plus T232's actual authenticated delegates, over fake credentials.</summary>
    private sealed class ArrangementRig : IDisposable
    {
        public const string Namespace = "root-namespace";
        private readonly string folder = Path.Combine(Path.GetTempPath(),
            "syncshark-arrangement-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient http;

        public ArrangementRig(string configuredRoot = "")
        {
            Directory.CreateDirectory(folder);
            Recorder = new RecordingHandler(new DropboxErrorSummaryHandler(Wire));
            Handler = new LiveDropboxHandler(Recorder);
            http = new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan };
            var root = configuredRoot.Length == 0 ? "/Pilot" : "/Pilot/" + configuredRoot;
            Pair = new PairConfig("arrangement", "arrangement", folder, root, "connection-a", [], [],
                FirstRunPolicy.NewestWins, true, null);
            var scope = new PathScope(Pair, [Pair]);
            var settings = Scenario.FixtureSettings;
            var limiter = new AdaptiveLimiter(1, 1, TimeProvider.System, Available(), _ => { }, settings.Backoff);
            var spool = Path.Combine(folder, "spool");
            Directory.CreateDirectory(spool);
            var api = new DropboxApi(http, Tokens, Namespace, limiter,
                ct => LocalSide.CreatePageSpoolAsync(spool, ct), settings.Backoff, TimeProvider.System,
                "account-id", "arrangement");
            Side = new DropboxSide(api, Pair, scope, settings, new DropboxListing(api, Pair, scope));
            if (configuredRoot.Length > 0) Wire.SeedFolder(configuredRoot);
            RootId = configuredRoot.Length == 0 ? DropboxWire.RootId : ReadOnce("", Scenario.Cancellation)
                .GetAwaiter().GetResult()!.Id;
            Account = new LiveDropboxAccount(() => Side, () => Pair, Handler,
                Scenario.FolderDeleteSender(http, AccessToken, Namespace),
                Scenario.FolderIdObserver(http, AccessToken, Namespace));
            Account.BindFolderArrangementScope("", RootId);
        }

        public DropboxWire Wire { get; } = new();
        public RecordingHandler Recorder { get; }
        public LiveDropboxHandler Handler { get; }
        public PairConfig Pair { get; }
        public DropboxSide Side { get; }
        public LiveDropboxAccount Account { get; }
        public StaticTokens Tokens { get; } = new();
        public HttpClient Http => http;
        public string RootId { get; }

        public static string PathRootJson => JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [DropboxProtocol.Tag] = DropboxProtocol.Root,
            [DropboxProtocol.Root] = Namespace,
        });

        public static Dictionary<string, object> Folder(string id, string path) => new()
        {
            [DropboxProtocol.Tag] = DropboxProtocol.TagFolder,
            [DropboxProtocol.Id] = id,
            [DropboxProtocol.PathDisplay] = path,
        };

        public static Dictionary<string, object> Wrapped(object value, string key) => new() { [key] = value };

        public Func<CancellationToken, Task<string>> AccessToken => Tokens.GetAccessTokenAsync;

        public void Dispose()
        {
            http.Dispose();
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }

        public Entry SeedFolder(string path)
        {
            Wire.SeedFolder(path);
            return Read(path) ?? throw new InvalidOperationException("missing seeded folder " + path);
        }

        public Entry? Read(string path) => Side.ReadOneAsync(path, Scenario.Cancellation).GetAwaiter().GetResult();

        public Task<Entry?> ReadOnce(string path, CancellationToken ct) => Side.ReadOneAsync(path, ct);

        public void Complete(string rootPath, string rootId, string path, string expectedFolderId) =>
            LiveDropboxAccount.CompleteFolderArrangementAsync(rootPath, rootId, path, expectedFolderId,
                Scenario.FolderDeleteSender(http, AccessToken, Namespace),
                Scenario.FolderIdObserver(http, AccessToken, Namespace), ReadOnce, Scenario.Cancellation)
                .GetAwaiter().GetResult();

        public IReadOnlyList<Recorded> Route(string route) => Recorder.Requests
            .Where(request => request.Url == Scenario.DropboxApiRoot + route).ToArray();

        public static string Remote(string path) => "/Pilot/" + path;

        public static string BodyPath(string body)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                return document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty(DropboxProtocol.Path, out var path) &&
                    path.ValueKind == JsonValueKind.String ? path.GetString()! : "";
            }
            catch (JsonException) { return ""; }
        }

        public static HttpResponseMessage Json(object payload, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload),
                    System.Text.Encoding.UTF8, "application/json"),
            };

        public static HttpResponseMessage Raw(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

        private static FakeNetworkSignal Available()
        {
            var signal = new FakeNetworkSignal();
            signal.SetAvailable(true);
            return signal;
        }
    }

    private sealed class StaticTokens : Massdex.SyncShark.IDropboxAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("fixture-access");
    }

    /// <summary>Captures each request's method, URL, credential/path-root headers and body, then forwards it.</summary>
    private sealed class RecordingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public List<Recorded> Requests { get; } = [];
        public Func<HttpRequestMessage, string, HttpResponseMessage?>? Intercept { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Requests.Add(new Recorded(request.Method.Method, request.RequestUri!.ToString(),
                request.Headers.TryGetValues("Authorization", out var credentials) ? credentials.Single() : null,
                request.Headers.TryGetValues(DropboxProtocol.PathRootHeader, out var values)
                    ? values.Single() : null, body));
            if (Intercept?.Invoke(request, body) is { } scripted) return scripted;
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record Recorded(string Method, string Url, string? Authorization, string? PathRoot, string Body);

    private static bool CreateJunction(string junction, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe",
            "/c mklink /J \"" + junction + "\" \"" + target + "\"")
            { UseShellExecute = false, CreateNoWindow = true })!;
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    private static IEnumerable<string[]> LogFields(string path)
    {
        // A scenario retains the writer until disposal, so readers must share its write handle.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line) yield return line.Split(RunLog.FieldSeparator);
    }

    /// <summary>Rows recorded when the multiplier adds one numbered folder row per copy.</summary>
    private static int ScaledRows => 2 * Scenario.Copies - 1;

    private static void RequireNoTransfer(Scenario scenario, RepairSummary summary) =>
        scenario.Require(!summary.Effects.Keys.Any(key => key is nameof(EffectKind.Uploaded) or
                nameof(EffectKind.Downloaded) or nameof(EffectKind.FolderCreated) or nameof(EffectKind.DeletedLocal) or
                nameof(EffectKind.DeletedDropbox) or nameof(EffectKind.MovedLocal) or nameof(EffectKind.MovedDropbox)),
            "no file or folder was transferred, moved or deleted (" + Effects(summary) + ")");

    private static string Effects(RepairSummary summary) =>
        string.Join(";", summary.Effects.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Key + "=" + item.Value.ToString(CultureInfo.InvariantCulture)));
}

using System.Globalization;
using System.Text.Json;
using Massdex.SyncShark.Engine;
using Massdex.SyncShark.Engine.Edge;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Tests.Edge;

/// <summary>Bug #92: `autosync status`, `autosync history`, `conflicts list` and `attention list` are answered by the
/// running Engine host from its own store, while that host already holds the product authority. The reads take no
/// lock, start no runtime, page through Storage and report the host's real state.</summary>
public static class EngineHostReadChecks
{
    private const string PairId = "pair-a";
    private const string PairName = "Pair A";
    private const string ItemPath = "Documents/Item.txt";
    private const string InfoPath = "Documents/Info.txt";
    private const string FailPath = "Documents/Fail.txt";
    private const string GonePath = "Documents/Gone.txt";
    private const string InfoBPath = "Documents/InfoB.txt";
    private const string RunId = "run-a";

    private static readonly EngineSettings Settings = new("{name} ({side} {time}){ext}", "yyyyMMddHHmmss",
        "Local", "Dropbox", ".case", RecycleFallback.Fail, 1, FirstRunPolicy.NewestWins, 1, "", 262144, ScanTestSettings.BatchRows)
    {
        JournalPageBufferBytes = ScanTestSettings.JournalPageBufferBytes,
        LogReadBufferBytes = ScanTestSettings.LogReadBufferBytes,
        Backoff = new FailureRetryOwner.BackoffSettings(1, 1.6, 0.2, 120),
        StoragePageSize = 4, BatchPollIntervalSeconds = 0.1, FolderDeleteMinimumPollSeconds = 1
    };

    private static readonly HttpClient Http = new();

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "syncshark-host-reads-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var productRoot = OverrideProductRoot(root);
            Massdex.SyncShark.Engine.Tests.Run.EngineHostChecks.StepAsync(nameof(EngineHostReadChecks), () => RunAsync(root))
                .GetAwaiter().GetResult();
            Massdex.SyncShark.Engine.Tests.Run.EngineHostChecks.PrintSlowestStep();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // Core's test hook redirects the product root to a temporary root; Engine.Tests is not on Core's
    // InternalsVisibleTo list.
    private static IDisposable OverrideProductRoot(string root) =>
        (IDisposable)typeof(SyncSharkRuntimePaths)
            .GetMethod("OverrideProductRootForTest", System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { root })!;

    private static async Task RunAsync(string root)
    {
        OwnsOnlyTheFourReads();
        foreach (var name in new string?[] { null, string.Empty, PairName })
        {
            var expected = string.IsNullOrEmpty(name) ? Text.Get(TextKey.StatusStillQuiet)
                : Text.Get(TextKey.StatusStillWorking, (TextArg.Name, name));
            Check.Require(IssuePresentation.WorkingStatus(name) == expected &&
                IssuePresentation.RenderHostStatus(AutoStatus.Running, name, null) == expected,
                "working status uses the name-free sentence only when no name is supplied");
        }
        Check.Require(IssuePresentation.RenderHostStatus(AutoStatus.Waiting, PairName, null) ==
            Text.Get(TextKey.StatusStillWaiting, (TextArg.Reason, PairName)),
            "waiting status retains its reason");
        Check.Require(IssuePresentation.RenderHostStatus(AutoStatus.Running, null, PairName) == PairName,
            "an explicit host message still takes precedence");
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var store = new InMemoryStateStore();
        var counters = new StoreCounters(store);
        var retries = new RecordingStore(counters);
        await using var host = EngineHost.Compose(root, _ => { }, instance.StateAuthority, Settings, Http,
            TimeProvider.System, _ => null, _ => retries, EngineHost.Mode.Persistent);
        await host.StartAsync(CancellationToken.None).ConfigureAwait(false);
        // Seeded after readiness so the idle host performs no pair work; the reads still see the same store.
        var (tieId, infoId, failureId, infoBId) = Seed(store);
        var runtime = new EngineCliRuntime(host.Operations);

        var status = await ReadAsync(runtime, ["--cli", "autosync", "status", "--json"]);
        Check.Require(status.GetProperty("state").GetString() is { Length: > 0 } &&
            status.GetProperty("message").GetString() is { Length: > 0 },
            "autosync status reports the running host's own state and text");

        var history = await ReadAsync(runtime, ["--cli", "autosync", "history", "--pair", PairId, "--json"]);
        Check.Require(history.GetProperty("count").GetInt32() == 1 &&
            history.GetProperty("runs")[0].GetProperty("id").GetString() == RunId &&
            !history.TryGetProperty("warning", out _),
            "autosync history reports the pair's retained Repair summary with no invented cursor warning");

        var cursored = await ReadAsync(runtime,
            ["--cli", "autosync", "history", "--pair", PairId, "--after", "1", "--json"]);
        Check.Require(cursored.GetProperty("count").GetInt32() == 1 &&
            cursored.GetProperty("warning").GetString() == Text.Get(TextKey.ResultHistoryNoCursor),
            "an Engine history cursor request returns the latest summary with the explicit no-cursor warning");

        var conflicts = await ReadAsync(runtime,
            ["--cli", "conflicts", "list", "--pair", PairId, "--limit", "1", "--json"]);
        Check.Require(conflicts.GetProperty("returned").GetInt32() == 1 &&
            conflicts.GetProperty("totalOpen").GetInt32() == 3 &&
            conflicts.GetProperty("items")[0].GetProperty("relativePath").GetString() == ItemPath &&
            conflicts.GetProperty("items")[0].GetProperty("group").GetString() == nameof(IssueKind.Tie) &&
            Choices(conflicts.GetProperty("items")[0]).Contains("UseLocal"),
            "conflicts list pages open issues only, keeps the total and carries the Tie's choices");

        var all = await ReadAsync(runtime,
            ["--cli", "conflicts", "list", "--pair", PairId, "--limit", "10", "--json"]);
        Check.Require(all.GetProperty("returned").GetInt32() == 3 &&
            all.GetProperty("items").EnumerateArray().Any(item =>
                item.GetProperty("group").GetString() == nameof(IssueKind.Failure) &&
                Choices(item).Contains("Retry")),
            "a Failure row carries its Retry choice and dismissed rows never page");

        var notice = store.Issues(PairId).Single(issue => issue.Id == infoId);
        var infoRow = all.GetProperty("items").EnumerateArray().Single(item =>
            item.GetProperty("relativePath").GetString() == InfoPath);
        Check.Require(infoRow.GetProperty("recommendation").GetString() ==
            IssuePresentation.Render(notice, new(RetryPresentationState.Unavailable)) &&
            Choices(infoRow).SequenceEqual(["Dismiss", "Ignore"]), "Host conflicts use the shared enriched notice renderer.");

        var account = await ReadAsync(runtime,
            ["--cli", "conflicts", "list", "--account", "connection-b", "--limit", "10", "--json"]);
        Check.Require(account.GetProperty("returned").GetInt32() == 1 &&
            account.GetProperty("items")[0].GetProperty("relativePath").GetString() == InfoBPath,
            "the account filter pages only that account's pairs in Storage");

        var attention = await ReadAsync(runtime, ["--cli", "attention", "list", "--json"]);
        Check.Require(attention.GetProperty("pairIds").GetArrayLength() == 1 &&
            attention.GetProperty("pairIds")[0].GetString() == PairId &&
            attention.GetProperty("items").GetArrayLength() == 2 &&
            attention.GetProperty("pendingCount").GetInt32() == 2 &&
            attention.GetProperty("pendingLabel").GetString() == Text.Get(TextKey.ResultAttentionNoticeCount),
            "attention list lists problem notices under the truthful open-notice label");

        // The reads went through Storage: conflicts page once each; attention reads every problem notice and its
        // one count in one read-only snapshot, then projects each notice's retry once per page of rows. Dismissed
        // rows never page.
        Check.Require(counters.MemberCalls.GetValueOrDefault("OpenIssuePage") == 3 &&
            counters.MemberCalls.GetValueOrDefault("OpenIssueSnapshot") == 1 &&
            !counters.MemberCalls.ContainsKey("Issues") &&
            retries.RetryReads.Count == 4 &&
            retries.RetryReads[0].SequenceEqual([tieId]) &&
            retries.RetryReads[1].SequenceEqual([tieId, infoId, failureId]) &&
            retries.RetryReads[2].SequenceEqual([infoBId]) &&
            retries.RetryReads[3].SequenceEqual([tieId, failureId]),
            "conflicts pages once per read and attention reads one snapshot; retries cover row pages only; " +
            "counts=" + counters.MemberCalls.GetValueOrDefault("OpenIssuePage") +
            " snapshots=" + counters.MemberCalls.GetValueOrDefault("OpenIssueSnapshot") +
            " retries=" + string.Join("|", retries.RetryReads.Select(ids => string.Join(",", ids))));

        var visibility = await host.Operations.ResolveAsync(PairId, tieId, ResolutionChoice.Ignore,
            null, null, false, null, CancellationToken.None);
        Check.Require(!visibility.Resolved && store.IssueById(PairId, tieId)!.Ignored,
            "Ignore completes without claiming closure");
        var hidden = await ReadAsync(runtime,
            ["--cli", "conflicts", "list", "--pair", PairId, "--ignored", "--json"]);
        Check.Require(hidden.GetProperty("totalOpen").GetInt32() == 1 &&
            Choices(hidden.GetProperty("items")[0]).Contains("Unignore"), "IPC ignored view offers Restore");
        var restored = await host.Operations.ResolveAsync(PairId, tieId, ResolutionChoice.Unignore,
            null, null, false, null, CancellationToken.None);
        var result = new Massdex.SyncShark.Engine.Edge.Results.ResolveResult(tieId, ResolutionChoice.Unignore, restored.Resolved, restored.Outcome);
        Check.Require(result.ExitCode == 0 && !result.Done && result.RenderHuman().Contains("restored", StringComparison.Ordinal),
            "visibility success is not reported as failed or as closing the notice");

        // Counted work: a full attention listing reads every problem notice with its one count in one snapshot,
        // collects the distinct pair ids, and projects each notice's retry exactly once per page of rows.
        var snapshotsBefore = counters.MemberCalls.GetValueOrDefault("OpenIssueSnapshot");
        var countsBefore = counters.MemberCalls.GetValueOrDefault("OpenIssuePage");
        var retriesBefore = retries.RetryReads.Count;
        for (var i = 0; i < 9; i++)
            Raise(store, PairId, IssueKind.Failure, TextKey.FailDropboxNameRejected, $"Documents/Bulk{i}.txt");
        var counted = await ReadAsync(runtime, ["--cli", "attention", "list", "--json"]);
        var notices = counted.GetProperty("items").GetArrayLength();
        var pageSize = Settings.StoragePageSize;
        var snapshots = counters.MemberCalls.GetValueOrDefault("OpenIssueSnapshot") - snapshotsBefore;
        var counts = counters.MemberCalls.GetValueOrDefault("OpenIssuePage") - countsBefore;
        var retryIds = retries.RetryReads.Skip(retriesBefore).SelectMany(ids => ids).ToArray();
        Check.Require(notices == 11 && counted.GetProperty("pendingCount").GetInt32() == notices &&
            counted.GetProperty("pairIds").GetArrayLength() == 1 &&
            snapshots == 1 && counts == 0 &&
            retries.RetryReads.Count - retriesBefore == (notices + pageSize - 1) / pageSize &&
            retryIds.Length == notices && retryIds.Distinct().Count() == notices,
            "attention reads one snapshot, one count and one retry lookup per page of items; notices=" + notices +
            " snapshots=" + snapshots + " counts=" + counts + " retryIds=" + retryIds.Length);

        // R1: a notice with no retry candidate is Unavailable, not the enum's default Running.
        var retryViews = host.Operations.ReadIssueRetryViews(PairId, [failureId]);
        Check.Require(retryViews[failureId].State == RetryPresentationState.Unavailable,
            "a notice without a retry candidate reports Unavailable");

        // The reads ran while the test still owns the exclusive product authority: taking it again must fail,
        // so none of them acquired or released a second product lock.
        var contended = false;
        try
        {
            using var second = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        }
        catch (IOException) { contended = true; }
        Check.Require(contended, "the host-owned reads take no product lock of their own");
    }

    private static void OwnsOnlyTheFourReads()
    {
        Check.Require(EngineHostReadCatalog.KindOf("autosync status") == EngineHostReadKind.AutoSyncStatus &&
            EngineHostReadCatalog.KindOf("autosync history") == EngineHostReadKind.AutoSyncHistory &&
            EngineHostReadCatalog.KindOf("conflicts list") == EngineHostReadKind.ConflictsList &&
            EngineHostReadCatalog.KindOf("attention list") == EngineHostReadKind.AttentionList &&
            !EngineHostReadCatalog.Owns("autosync start") && !EngineHostReadCatalog.Owns("autosync stop") &&
            !EngineHostReadCatalog.Owns("engine status") && !EngineHostReadCatalog.Owns("status"),
            "the command registry is the one owner of which rows the Engine host answers as reads");
    }

    private static (long TieId, long InfoId, long FailureId, long InfoBId) Seed(IStateStore store)
    {
        store.SavePair(new PairConfig(PairId, PairName, "C:/PairA", "/PairA", "connection-a", [], [],
            FirstRunPolicy.NewestWins, true, null));
        // A second pair with only an Informational notice must not appear in attention.
        store.SavePair(new PairConfig("pair-b", "Pair B", "C:/PairB", "/PairB", "connection-b", [], [],
            FirstRunPolicy.NewestWins, true, null));
        Raise(store, PairId, IssueKind.Tie, TextKey.TieEqualTimes, ItemPath);
        store.Commit(PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Info, TextKey.InfoKeptBoth, InfoPath, new IssueSubject([
                new(Side.Local, InfoPath, "local"), new(Side.Dropbox, InfoPath, "remote"),
                new(Side.Dropbox, "Documents/LocalCopy.txt", "upload"),
                new(Side.Local, "Documents/RemoteCopy.txt", "download")], null))]
        });
        Raise(store, PairId, IssueKind.Failure, TextKey.FailDropboxNameRejected, FailPath);
        Raise(store, PairId, IssueKind.Tie, TextKey.TieEqualTimes, GonePath);
        Raise(store, "pair-b", IssueKind.Info, TextKey.InfoKeptBoth, InfoBPath);
        var issues = store.Issues(PairId);
        var gone = issues.Single(issue => issue.Path == GonePath);
        store.DismissIssue(PairId, gone.Id);
        store.FinishRepair(PairId, new RepairSummary(RunId, new DateTimeOffset(2026, 11, 5, 1, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 5, 1, 5, 0, TimeSpan.Zero), true, false, null, false,
            new Dictionary<string, int>(), new Dictionary<string, int>(), "run-a.log", false));
        return (issues.Single(issue => issue.Path == ItemPath).Id,
            issues.Single(issue => issue.Path == InfoPath).Id,
            issues.Single(issue => issue.Path == FailPath).Id,
            store.Issues("pair-b").Single(issue => issue.Path == InfoBPath).Id);
    }

    private static void Raise(IStateStore store, string pairId, IssueKind kind, TextKey cause, string path) =>
        store.Commit(pairId, StateChange.Empty with
        {
            Raise = [(kind, cause, path, new IssueSubject([], null))]
        });

    private static IEnumerable<string?> Choices(JsonElement item) =>
        item.GetProperty("options").EnumerateArray().Select(option => option.GetProperty("choice").GetString());

    private static async Task<JsonElement> ReadAsync(EngineCliRuntime runtime, string[] args)
    {
        using var stdout = new StringWriter(CultureInfo.InvariantCulture);
        using var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var exit = await SyncCli.ExecuteAsync(args, stdout, stderr, () => runtime, CancellationToken.None)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(stdout.ToString());
        var envelope = document.RootElement;
        Check.Require(exit == (int)SyncCliExitCode.Success && envelope.GetProperty("ok").GetBoolean() &&
            envelope.GetProperty("data").ValueKind == JsonValueKind.Object,
            "a host read returns its data envelope: " + string.Join(' ', args) + "; stderr=" + stderr);
        return envelope.GetProperty("data").Clone();
    }

    // Records the issue ids each retry projection was asked for, proving only page rows are projected.
    private sealed class RecordingStore(IStateStore inner) : ForwardingStateStore(inner)
    {
        public List<long[]> RetryReads { get; } = [];

        public override IReadOnlyDictionary<long, ItemRetryCandidate?> ReadItemRetryState(string pairId,
            IReadOnlyList<long> issueIds, Issue? expected = null)
        {
            RetryReads.Add(issueIds.ToArray());
            return base.ReadItemRetryState(pairId, issueIds, expected);
        }
    }
}

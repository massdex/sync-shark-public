using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Scenarios;

/// <summary>One dummy-file scenario: a real local folder, a real SQLite store and the real Dropbox side, against the
/// in-memory account (offline) or the owner's real account (live). Offline runs entirely under
/// %TEMP%\syncshark-scenarios\&lt;run&gt;\&lt;scenario&gt;.</summary>
internal sealed class Scenario : IDisposable
{
    // Snapshots treat links as opaque, as the engine does; reading through a fresh junction can fail on some volumes.
    private static readonly EnumerationOptions LocalTreeOptions = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false,
    };
    private const string RootNamespace = "root-namespace";
    private const int InitialLimiterLevel = 4;
    private const string LiveDropboxRoot = "/Apps/Sync Shark/Testing/engine-scenarios";
    private const string LiveLocalFolderName = "SyncShark-EngineTest";
    private const string LiveLocalRootVariable = "SYNCSHARK_LIVE_LOCAL_ROOT";
    private const string LiveConnectionVariable = "SYNCSHARK_LIVE_CONNECTION";
    /// <summary>The neutral key every live scenario pair uses; the injected API carries the real binding.</summary>
    private const string LivePairKey = "live-scenario";

    /// <summary>Every offline scenario of this process shares one run folder.</summary>
    public static readonly string RunRoot = Path.Combine(Path.GetTempPath(), "syncshark-scenarios",
        DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" +
        Guid.NewGuid().ToString("N")[..8]);

    /// <summary>The fixed past timestamps all dummy files use.</summary>
    public static readonly DateTimeOffset T0 = new(2025, 3, 1, 12, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset T1 = new(2025, 3, 2, 12, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset T2 = new(2025, 3, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Optional scale knob: each arranged item is also copied under 1/, 2/, ... (default 1 = nothing extra).</summary>
    private static readonly int Multiplier = ReadMultiplier();

    private static readonly CancellationTokenSource Stop = CreateStopSource();

    private static readonly HashSet<string> EngineRoutes = new[]
    {
        DropboxProtocol.ListFolder, DropboxProtocol.ListFolderContinue, DropboxProtocol.ListFolderGetLatestCursor,
        DropboxProtocol.GetMetadata,
        DropboxProtocol.Download, DropboxProtocol.Upload, DropboxProtocol.UploadSessionStart,
        DropboxProtocol.UploadSessionAppend, DropboxProtocol.UploadSessionFinish,
        DropboxProtocol.UploadSessionFinishBatch,
        DropboxProtocol.CreateFolder, DropboxProtocol.Delete, DropboxProtocol.DeleteBatch, DropboxProtocol.DeleteBatchCheck,
        DropboxProtocol.Move,
        DropboxProtocol.ListRevisions,
    }.Select(name => "/2/" + name).Append("/oauth2/token").Append("/2/users/get_current_account")
        .ToHashSet(StringComparer.Ordinal);

    private static readonly EngineSettings DefaultSettings = new("{name} ({side} {time}){ext}", "yyyyMMddHHmmss",
        "Local", "Dropbox", ".case", RecycleFallback.Fail, 1, FirstRunPolicy.NewestWins,
        InitialLimiterLevel, "", 262144, ScanTestSettings.BatchRows)
    {
        // The production defaults from engine-settings.json, so hosts and adapters receive loaded values.
        StoragePageSize = 100,
        LogReadBufferBytes = ScanTestSettings.LogReadBufferBytes,
        JournalPageBufferBytes = ScanTestSettings.JournalPageBufferBytes,
        Backoff = new Massdex.SyncShark.V2.FailureRetryOwner.BackoffSettings(1, 1.6, 0.2, 120),
        BatchPollIntervalSeconds = 0.1,
        FolderDeleteMinimumPollSeconds = 1,
    };

    private const int MaximumObservationReplyBytes = 64 * 1024;
    internal const string DropboxApiRoot = "https://api.dropboxapi.com/2/";
    /// <summary>The bare reason tag inside a 409 path/not_found error object.</summary>
    private const string NotFoundTag = "not_found";

    private static readonly object CleanupGate = new();
    private static readonly List<string> PendingCleanupFailures = new();
    private static string? activeLocalRoot;
    private static string? activeRecovery;
    private static LiveContext? live;

    private readonly List<RunLog> logs = new();
    private readonly List<string> runs = new();
    private HttpClient? http;
    private HttpMessageHandler? transport;
    private ScenarioStore observedStore = null!;
    private LiveDropboxHandler? handlerForCase;
    private Massdex.SyncShark.PilotInstanceLock? authority;
    private EngineHost? host;
    private string? failure;
    private int runNumber;
    private string[]? cachedTree;
    private int callStart;
    private bool disposed;
    private Massdex.SyncShark.V2.DropboxWire? wire;
    // Local roots of the pairs AddSecondPair added, in order; the first is SecondPair's.
    private readonly List<string> addedLocalRoots = [];
    private PairConfig? second;
    private object? sessionRegistration;

    // The account limiter's starting level is per scenario; the limiter itself stays adaptive.
    private readonly EngineSettings Settings;

    private Scenario(string root, string name, IReadOnlyList<string>? ignoreRules, int? limiterLevel)
    {
        Settings = limiterLevel is { } level ? DefaultSettings with { InitialLimiterLevel = level } : DefaultSettings;
        Root = root;
        LocalRoot = Path.Combine(root, "local");
        Recovery = Path.Combine(root, "recovery");
        Removed = Path.Combine(root, "removed");
        Logs = Path.Combine(root, "logs");
        Staging = Path.Combine(root, "staging");
        try
        {
            Directory.CreateDirectory(LocalRoot);
            Directory.CreateDirectory(Recovery);
            Directory.CreateDirectory(Removed);
            Directory.CreateDirectory(Logs);
            Directory.CreateDirectory(Staging);

            if (live is { } context)
            {
                // One client, handler and API per case: concurrent cases cannot see each other's calls or faults.
                var handler = new LiveDropboxHandler(new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                });
                handlerForCase = handler;
                transport = handler;
                http = new HttpClient(transport) { Timeout = Timeout.InfiniteTimeSpan };
                // A per-run folder keeps earlier runs' deleted paths out of first-run deletion history.
                var dropboxRoot = LiveDropboxRoot + "/" + Path.GetFileName(RunRoot) + "/" + name;
                Pair = new PairConfig(name, name, LocalRoot, dropboxRoot, context.Binding.ConnectionId,
                    ignoreRules ?? [], [], FirstRunPolicy.NewestWins, true, null,
                    BoundRoots(LocalRoot, dropboxRoot, context.Binding.ConnectionId, context.RootNamespaceId,
                        WindowsDirectoryListing.ReadIdentity(LocalRoot)));
            }
            else
            {
                wire = new Massdex.SyncShark.V2.DropboxWire();
                transport = new DropboxErrorSummaryHandler(wire);
                http = new HttpClient(transport);
                var connection = DropboxConnectionBinding.CreateConnectionId("account-id");
                Pair = new PairConfig(name, name, LocalRoot, "/Pilot", connection,
                    ignoreRules ?? [], [], FirstRunPolicy.NewestWins, true, null,
                    BoundRoots(LocalRoot, "/Pilot", connection, RootNamespace,
                        WindowsDirectoryListing.ReadIdentity(LocalRoot)));
                Account = new FakeDropboxAccount(wire);
                callStart = Account.Calls.Count;
            }
            host = ComposeHost();
            BuildSides();
            if (live is { } liveContext)
            {
                var delete = FolderDeleteSender(http!, ct => liveContext.Tokens.GetAccessTokenAsync(ct),
                    liveContext.RootNamespaceId);
                var observe = FolderIdObserver(http!, ct => liveContext.Tokens.GetAccessTokenAsync(ct),
                    liveContext.RootNamespaceId);
                var account = new LiveDropboxAccount(() => Dropbox, () => Pair, handlerForCase!, delete, observe);
                // Bind this generation's owned folder from its creation receipt before the account is exposed.
                var created = Dropbox.EnsureFolderAsync("", Cancellation).GetAwaiter().GetResult();
                account.BindFolderArrangementScope("", created.Id);
                Account = account;
                callStart = Account.Calls.Count;
            }
            var observedRoots = Dropbox.ReadRootIdentityAsync(Cancellation).GetAwaiter().GetResult();
            Pair = Pair with { Roots = Pair.Roots! with { Dropbox = observedRoots } };
            Store.SavePair(Pair);
            BuildSides();
            if (live is null)
            {
                activeLocalRoot = LocalRoot;
                activeRecovery = Recovery;
            }
        }
        catch
        {
            Release();
            throw;
        }
    }

    public string Root { get; }
    public string LocalRoot { get; }
    public string Recovery { get; }
    public string Removed { get; }
    public string Logs { get; }
    /// <summary>Local page/stage scratch for this case; never a pair destination.</summary>
    public string Staging { get; }
    public PairConfig Pair { get; private set; } = null!;
    /// <summary>The optional second pair added to this case's one host.</summary>
    public PairConfig SecondPair => second ?? throw new InvalidOperationException("no second pair");
    public PathScope Scope { get; private set; } = null!;
    public StateStore Store { get; private set; } = null!;
    internal EngineHost Host => host!;
    public IDropboxAccount Account { get; private set; } = null!;
    public ISide Local { get; private set; } = null!;
    public DropboxSide Dropbox { get; private set; } = null!;
    public static bool LiveMode => live is not null;
    public static int Copies => Multiplier;
    /// <summary>The one cancellation token, cancelled on Ctrl+C or process exit.</summary>
    public static CancellationToken Cancellation => Stop.Token;

    /// <summary>Denies content access while metadata remains available to the production listing.</summary>
    public IDisposable DenyLocalContent(string path) => new FileStream(
        Path.Combine(LocalRoot, path.Replace('/', Path.DirectorySeparatorChar)), FileMode.Open,
        FileAccess.Read, FileShare.None);

    internal void Start()
    {
        host!.DrainGenerationAsync(Pair.PairId, Cancellation).GetAwaiter().GetResult();
        host.StartAsync(Cancellation).GetAwaiter().GetResult();
    }

    // Completes with the Stopwatch timestamp taken at the matching completion, before any continuation is scheduled.
    internal Task<long> ObserveCompletion(string path, string expectedContent) => observedStore.ObserveCompletion(
        PathNames.Key(path), DropboxContentHash.Compute(Encoding.UTF8.GetBytes(expectedContent)));

    internal Task<LiveAdmissionStaleReason> ObserveRefusal() => observedStore.Refused.Task;
    /// <summary>The first in-use item failure the host records, so a test waits on the event, not the clock.</summary>
    internal Task<ItemRetryCandidate> ObserveInUseFailure() => observedStore.InUseFailure.Task;
    internal int LiveValidations => observedStore.LiveValidations;
    internal int EndedWorkPasses => Volatile.Read(ref observedStore.EndedWorkPasses);
    internal Action? PassEnded { set => observedStore.PassEnded = value; }
    internal Action? BeforeRepairCheckpoint { set => observedStore.BeforeRepairCheckpoint = value; }
    internal Action<string>? Progress { get; set; }
    internal Action<RepairSummary>? RepairFinished { set => observedStore.RepairFinished = value; }
    internal Action? BeforePairs { set => observedStore.BeforePairs = value; }
    internal Action<WorkSelection>? BeforeLiveValidation { set => observedStore.BeforeLiveValidation = value; }
    internal Action? BeforeDirtyRead { set => observedStore.BeforeDirtyRead = value; }
    internal Func<Row, bool>? HeldWithoutIssue { set => observedStore.HeldWithoutIssue = value; }
    internal Func<IEnumerable<WorkSelection>>? SelectionAncestors { set => observedStore.SelectionAncestors = value; }
    // G7 (B-161): when a Repair's scan begins, when a journal page commits each notice, when work completes successfully,
    // and every ReadItemRetry through the host's store (all callers, all pairs).
    internal Action<string>? ScanBegun { set => observedStore.ScanBegun = value; }
    internal Action<string, ChangeNotice>? NoticeCommitted { set => observedStore.NoticeCommitted = value; }
    internal Action<WorkSelection>? WorkCompleted { set => observedStore.WorkCompleted = value; }
    internal int ItemRetryReads => Volatile.Read(ref observedStore.ItemRetryReads);
    // G7: one pair's own ReadItemRetry calls, and its HasFolderJobProgress calls (AutoSync makes one after an
    // opportunity's work returns; a cancelled or failed one makes none). Neither is a count of dispatches.
    internal string? CountedPair { set => Volatile.Write(ref observedStore.CountedPair, value); }
    internal int CountedReads => Volatile.Read(ref observedStore.CountedReads);
    internal int CountedProgressChecks => Volatile.Read(ref observedStore.CountedProgressChecks);

    /// <summary>Offline only: holds each delete-job check until the gate opens, so a join can be observed while
    /// its provider read is in flight.</summary>
    internal Func<Task>? BeforeDeleteJobCheck
    {
        set => (wire ?? throw new InvalidOperationException(
            "holding a delete-job check needs the in-memory Dropbox account")).BeforeDeleteJobCheck = value;
    }

    /// <summary>Offline only: runs before each provider request, so a check can hold one in flight.</summary>
    internal Func<string, Exception?>? RequestException
    {
        set => (wire ?? throw new InvalidOperationException(
            "holding a provider request needs the in-memory Dropbox account")).RequestException = value;
    }

    internal AutoSync CreateAutoSync(Action opportunity, Action<WorkSelection?> selected)
    {
        observedStore.Selected = selected;
        return new AutoSync(observedStore, _ => { opportunity(); return null; }, host!);
    }

    /// <summary>True when the case's expectations cannot expand with the multiplier and it was skipped.</summary>
    public bool Skipped { get; private set; }

    public bool SkipWhenScaled()
    {
        Skipped = Multiplier > 1;
        return Skipped;
    }

    /// <summary>Skips a case that needs the real provider and the real Shell, and whose counts do not expand.</summary>
    public bool SkipUnlessLive()
    {
        Skipped = !LiveMode || Multiplier > 1;
        return Skipped;
    }

    /// <summary>Adds one line to the case's retained test log (trees.txt), never to product logs or CI output.</summary>
    public void Note(string line) => runs.Add("note: " + line);

    public static Scenario Begin(string name, IReadOnlyList<string>? ignoreRules = null, int? limiterLevel = null)
    {
        var scenarioRoot = live is null ? Path.Combine(RunRoot, name) : Path.Combine(live.LocalRoot, name);
        if (live is null)
        {
            var scenariosRoot = Path.Combine(Path.GetTempPath(), "syncshark-scenarios");
            Check.Require(scenarioRoot.StartsWith(scenariosRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase),
                "a scenario runs only under " + scenariosRoot + " (refused: " + scenarioRoot + ")");
            if (LocalBin.SendOverride is null) LocalBin.SendOverride = Bury;
        }
        return new Scenario(scenarioRoot, name, ignoreRules, limiterLevel);
    }

    /// <summary>Retains the expected and observed condition before asserting it.</summary>
    public void Require(bool condition, string what)
    {
        runs.Add("expected: True; observed: " + condition + "; " + what);
        Check.Require(condition, what);
    }

    /// <summary>Records the failure for the retained summary.</summary>
    public void Fail(Exception error) => failure = error.ToString();

    /// <summary>Replaces the pair's exclusions and rebuilds the two sides on the new scope.</summary>
    public void SetExclusions(params Exclusion[] exclusions)
    {
        Pair = Pair with { Exclusions = exclusions };
        Store.SavePair(Pair);
        BuildSides();
    }

    /// <summary>Adds another enabled pair under this case's one host, rooted at a subfolder of the same in-memory
    /// account so the first pair's scope holds it out as a nested pair. The first call's pair is SecondPair; a later
    /// call adds one more (G7 runs three pairs). Offline only.</summary>
    public PairConfig AddSecondPair(string name)
    {
        if (live is not null) throw new NotSupportedException("a second pair shares the in-memory account");
        var localRoot = Path.Combine(Root, name + "-local");
        addedLocalRoots.Add(localRoot);
        Directory.CreateDirectory(localRoot);
        var dropboxRoot = Pair.DropboxRoot.TrimEnd('/') + "/" + name;
        DropboxFolder(name);
        var candidate = Pair with
        {
            PairId = Pair.PairId + "-" + name,
            Name = name,
            LocalRoot = localRoot,
            DropboxRoot = dropboxRoot,
            Roots = BoundRoots(localRoot, dropboxRoot, Pair.ConnectionId, RootNamespace,
                WindowsDirectoryListing.ReadIdentity(localRoot)),
        };
        Store.SavePair(candidate);
        host!.RecomposeCurrentAsync(candidate.PairId, Cancellation).GetAwaiter().GetResult();
        var observed = host.DispatchAsync(candidate.PairId,
            (registration, pair, generation, _) =>
                host.BorrowGeneration(registration, pair.PairId, generation).Dropbox.ReadRootIdentityAsync(Cancellation),
            Cancellation).GetAwaiter().GetResult();
        candidate = candidate with { Roots = candidate.Roots! with { Dropbox = observed } };
        Store.SavePair(candidate);
        host.DrainGenerationAsync(candidate.PairId, Cancellation).GetAwaiter().GetResult();
        host.RecomposeCurrentAsync(candidate.PairId, Cancellation).GetAwaiter().GetResult();
        // The first pair's saved scope is immutable; rebuild it so the new pair is its nested root, not its work.
        BuildSides();
        second ??= candidate;
        return candidate;
    }

    /// <summary>Runs one Repair; a successful repair must complete unless the case is exercising a failed listing.
    /// A case that owns a shorter lifetime cancels the Repair with it.</summary>
    public RepairSummary RepairAsync(bool requireCompleted = true, IReadOnlySet<long>? retryIssues = null,
        string? denyContentAt = null, CancellationToken lifetime = default) =>
        RepairAsync(Pair, requireCompleted, retryIssues, denyContentAt, lifetime);

    /// <summary>Runs an added pair's first Repair (SecondPair's by default), which a live opportunity requires. The pair
    /// stays installed so the host's start installs it once with no second membership change on the shared volume.</summary>
    public RepairSummary SecondRepairAsync(PairConfig? added = null) => RepairAsync(added ?? SecondPair, true, null, null, default);

    private RepairSummary RepairAsync(PairConfig target, bool requireCompleted, IReadOnlySet<long>? retryIssues,
        string? denyContentAt, CancellationToken lifetime)
    {
        using var owned = CancellationTokenSource.CreateLinkedTokenSource(Cancellation, lifetime);
        var cancellation = owned.Token;
        cancellation.ThrowIfCancellationRequested();
        var runId = "run-" + (++runNumber).ToString(CultureInfo.InvariantCulture);
        var before = cachedTree ?? Snapshot();
        cachedTree = null;
        // The admission a real Repair performs before its first session (EngineOperations.RepairCoreAsync).
        host!.RecomposeCurrentAsync(target.PairId, cancellation).GetAwaiter().GetResult();
        host.EnsureSourceBasesAsync(target.PairId, cancellation).GetAwaiter().GetResult();
        using var denied = denyContentAt is null ? null : DenyLocalContent(denyContentAt);
        var summary = host.DispatchAsync(target.PairId,
                (registration, pair, generation, ct) =>
                    Repair.RunAsync(BuildSession(runId, pair, registration, generation), ct, retryIssues),
                cancellation)
            .GetAwaiter().GetResult();
        runs.Add("== " + runId + " expected completed: " + (requireCompleted ? "True" : "either") +
            "; observed completed: " + summary.Completed);
        Check.Require(!requireCompleted || summary.Completed,
            "the Repair completed" + (summary.IncompleteReason is { } reason ? " (" + reason + ")" : ""));
        denied?.Dispose();
        Check.Require(observedStore.AfterFolder is null, "the expected durable folder record triggered acquisition");
        var after = Snapshot();
        cachedTree = after;
        runs.Add("== " + runId + " effects: " + Effects(summary) + " issues: " + Issues(summary) +
            " issues-now: " + string.Join(",", IssueLines(target.PairId)));
        runs.Add("== " + runId + " before: " + string.Join(" | ", before));
        runs.Add("== " + runId + " after:  " + string.Join(" | ", after));
        return summary;
    }

    /// <summary>Runs one body with the host's real registration and a session built from it.</summary>
    public T InSession<T>(string runId, Func<PairSession, T> body)
    {
        cachedTree = null;
        host!.RecomposeCurrentAsync(Pair.PairId, Cancellation).GetAwaiter().GetResult();
        return host.DispatchAsync(Pair.PairId,
                (registration, pair, generation, _) => Task.FromResult(body(
                    BuildSession(runId, pair, registration, generation))),
                Cancellation)
            .GetAwaiter().GetResult();
    }

    public void InSession(string runId, Action<PairSession> body) =>
        InSession(runId, session => { body(session); return true; });

    private PairSession BuildSession(string runId, PairConfig pair, object registration, long generation)
    {
        var log = RunLog.Open(Logs, pair.PairId, runId, _ => { }, Settings.LogBufferCharacters);
        logs.Add(log);
        sessionRegistration = registration;
        return PairSession.Open(observedStore, pair, Settings, http!, false, log,
            authority!.StateAuthority, host!, registration, generation, TimeProvider.System);
    }

    /// <summary>Runs one Repair that must complete, leave both trees alone, and raise or close no issue.</summary>
    public void RequireQuiet()
    {
        var before = cachedTree ?? Snapshot();
        var issuesBefore = IssueSnapshot();
        var summary = RepairAsync();
        Require(summary.Effects.Count == 0,
            "a quiet Repair transfers, moves or deletes nothing (effects: " + Effects(summary) + ")");
        var after = Snapshot();
        Require(before.SequenceEqual(after),
            "a quiet Repair leaves both trees unchanged (" + FirstMismatch(before, after) + ")");
        var issuesAfter = IssueSnapshot();
        Require(issuesBefore.SequenceEqual(issuesAfter),
            "a quiet Repair raises and closes no issue (before: [" + string.Join(",", issuesBefore) +
            "]; after: [" + string.Join(",", issuesAfter) + "])");
    }

    /// <summary>Asserts the exact open issues as "Kind:Cause@Path" lines; paths expand with the multiplier.</summary>
    public void RequireIssues(params string[] expected)
    {
        var actual = IssueLines();
        var wanted = expected.SelectMany(ExpandIssue).OrderBy(line => line, StringComparer.Ordinal).ToArray();
        Require(actual.SequenceEqual(wanted),
            "the open issues are [" + string.Join(",", wanted) + "] (actual: [" + string.Join(",", actual) + "])");
    }

    /// <summary>Asserts every Dropbox call is a route the engine declares.</summary>
    public void RequireOnlyEngineRoutes()
    {
        var calls = Account.Calls.Skip(callStart).ToArray();
        Require(calls.Length != 0, "the scenario reached the Dropbox account");
        var unexpected = calls.Where(call => !EngineRoutes.Contains(call)).Distinct().ToArray();
        Require(unexpected.Length == 0,
            "every Dropbox call is a route the engine declares (unexpected: " + string.Join(",", unexpected) + ")");
    }

    /// <summary>The next durable folder record triggers this change and a real journal catch-up before its child row.
    /// afterNotice runs once the matching notice is committed, the moment a withheld transplant needs.</summary>
    public void AcquireJournalAfterFolder(Action<string>? change = null, Action? afterNotice = null)
    {
        observedStore.AfterFolder = entry =>
        {
            var before = Store.ReadWorkWatermark();
            change?.Invoke(entry.LocalPath);
            var generation = Store.ReadPairExecution(Pair.PairId)!.Value.Generation;
            host!.CatchUpLocalAsync(Pair.PairId, generation, Cancellation).GetAwaiter().GetResult();
            Check.Require(Store.ReadWorkWatermark() > before, "the journal committed a notice for " + entry.LocalPath);
            afterNotice?.Invoke();
        };
    }

    /// <summary>The next folder the engine creates on Dropbox comes back through the real feed before its child row.
    /// afterNotice runs once the matching notice is committed, the moment a withheld transplant needs.</summary>
    public void AcquireFeedAfterFolder(bool replaced = false, Action? afterNotice = null)
    {
        observedStore.AfterFolder = created =>
        {
            var before = Store.ReadWorkWatermark();
            if (replaced)
            {
                RemoveDropbox(created.DropboxPath);
                DropboxFolder(created.DropboxPath);
            }
            SeedCreatedFolder(created.DropboxPath);
            var generation = Store.ReadPairExecution(Pair.PairId)!.Value.Generation;
            host!.CatchUpDropboxAsync(sessionRegistration!, Pair.PairId, generation, Cancellation).GetAwaiter().GetResult();
            Check.Require(Store.ReadWorkWatermark() > before, "the feed committed a notice for " + created.DropboxPath);
            afterNotice?.Invoke();
        };
    }

    /// <summary>Every folder record, including a Parents-created ancestor in its child's row, comes back through
    /// the real feed. Unlike AcquireFeedAfterFolder it neither fires once nor requires the folder's own scan row.</summary>
    public void AcquireFeedAfterEachFolder()
    {
        observedStore.EachFolder = created =>
        {
            if (created.DropboxPath.Length == 0) return;
            SeedCreatedFolder(created.DropboxPath);
            var generation = Store.ReadPairExecution(Pair.PairId)!.Value.Generation;
            host!.CatchUpDropboxAsync(sessionRegistration!, Pair.PairId, generation, Cancellation).GetAwaiter().GetResult();
        };
    }

    /// <summary>Runs the change between a row's freshness read and its effect, after the target side's binding read.</summary>
    public void AfterFreshRead(Side side, string path, Action change, int occurrence = 1)
    {
        var key = side == Side.Local ? PathNames.Key(path) : PathNames.DropboxKey(path);
        var reads = 0;
        observedStore.AfterSyncedByKey = (readSide, readKey) =>
        {
            if (readSide != side || readKey != key || ++reads != occurrence) return;
            observedStore.AfterSyncedByKey = null;
            change();
        };
    }

    /// <summary>The pending ancestor keys at these paths, proving a later notice arrived and was not retired.</summary>
    public IReadOnlyList<string> PendingAncestorKeys(Side side, params string[] paths)
    {
        var generation = Store.ReadPairExecution(Pair.PairId)!.Value.Generation;
        var keys = paths.Select(path => PathNames.KeyFor(side, path)).ToArray();
        return Store.PendingAncestors(Pair.PairId, generation, side, keys)
            .Select(selection => selection.Key).ToArray();
    }

    /// <summary>Runs the ordinary production selection over every selectable notice, as AutoSync does; a selection
    /// its coverage refuses comes back on the next opportunity.</summary>
    public int SettleSelections()
    {
        cachedTree = null;
        return InSession("settle", session =>
        {
            var effectRoutes = new[]
            {
                DropboxProtocol.Download, DropboxProtocol.Upload, DropboxProtocol.UploadSessionStart,
                DropboxProtocol.UploadSessionAppend, DropboxProtocol.UploadSessionFinish,
                DropboxProtocol.UploadSessionFinishBatch, DropboxProtocol.CreateFolder,
                DropboxProtocol.Delete, DropboxProtocol.DeleteBatch, DropboxProtocol.Move,
            }.Select(route => "/2/" + route).ToHashSet(StringComparer.Ordinal);
            // Live calls are per-route counts sorted by name, not a timeline, so compare counts rather than a suffix.
            var effectCallsBefore = Account.Calls.Count(effectRoutes.Contains);
            var effectsBefore = session.Log!.Effects.Values.Sum();
            var opportunities = session.Store.SelectWork(Pair.PairId, session.ExpectedGeneration, session.Scope) is null ? 0 : 1;
            session.Store.EndWorkPass(Pair.PairId);
            session.Syncer.SyncWorkAsync(Cancellation).GetAwaiter().GetResult();
            Require(session.Log.Effects.Values.Sum() == effectsBefore,
                "A214/A77: settling the echo emits no committed effect on either side");
            Require(Account.Calls.Count(effectRoutes.Contains) == effectCallsBefore,
                "A214/A77: settling the echo makes no provider transfer or mutation call");
            return opportunities;
        });
    }

    /// <summary>The fake account needs the created folder seeded into its page; the live account's own feed already
    /// carries it. Both then drive the same product catch-up.</summary>
    internal void EditRemoteFile(string path, string content)
    {
        if (wire is null) throw new NotSupportedException();
        wire.EditFile(path, content);
        QueueRemoteChanges(path);
    }

    internal void QueueRemoteChanges(params string[] paths)
    {
        if (wire is null) return;
        var cursor = Store.ReadSource(Pair.PairId, Side.Dropbox)!.Value.Cursor!;
        wire.SeedChangePage(cursor, paths.Select(wire.ChangeExistingFile).ToArray(), false, cursor + "-changed");
    }

    internal void QueueRemoteMove(string oldPath, params string[] newPaths)
    {
        if (wire is null) return;
        var cursor = Store.ReadSource(Pair.PairId, Side.Dropbox)!.Value.Cursor!;
        wire.SeedChangePage(cursor, [wire.ChangeDeleted(Pair.DropboxRoot + "/" + oldPath),
            .. newPaths.Select(wire.ChangeExistingFile)], false, cursor + "-moved");
    }

    private void SeedCreatedFolder(string path)
    {
        if (wire is null) return;
        var entry = Dropbox.ReadOneAsync(path, Cancellation).GetAwaiter().GetResult()!;
        var cursor = Store.ReadSource(Pair.PairId, Side.Dropbox)!.Value.Cursor!;
        wire.SeedChangePage(cursor,
        [
            new Dictionary<string, object>
            {
                [".tag"] = "folder", ["name"] = entry.Path, ["id"] = entry.Id,
                ["path_lower"] = (Pair.DropboxRoot + "/" + entry.Path).ToLowerInvariant(),
                ["path_display"] = Pair.DropboxRoot + "/" + entry.Path,
            },
        ], false, cursor + "-echo");
    }

    public void RefuseUpload(string path)
    {
        runs.Add("injected: refuse upload " + path);
        Account.RefuseUpload(path);
    }

    public void ClearWriteRefusals()
    {
        runs.Add("injected: clear write refusals");
        Account.ClearWriteRefusals();
    }

    public void FailNextListings(int count)
    {
        runs.Add("injected: fail next " + count + " listings");
        Account.FailListings(count);
    }

    // Arrange: local.
    public void LocalFile(string path, string content, DateTimeOffset? modified = null) =>
        WriteLocal(LocalRoot, path, content, modified);

    /// <summary>Writes one file into the second pair's local root.</summary>
    public void SecondLocalFile(string path, string content, DateTimeOffset? modified = null) =>
        WriteLocal(addedLocalRoots.Count != 0 ? addedLocalRoots[0] : throw new InvalidOperationException("no second pair"),
            path, content, modified);

    private void WriteLocal(string root, string path, string content, DateTimeOffset? modified)
    {
        Cancellation.ThrowIfCancellationRequested();
        cachedTree = null;
        var bytes = Encoding.UTF8.GetBytes(content);
        foreach (var variant in Variants(path))
        {
            var full = Path.Combine(root, variant.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
            File.SetLastWriteTimeUtc(full, (modified ?? T0).UtcDateTime);
        }
    }

    public void LocalFolder(string path)
    {
        Cancellation.ThrowIfCancellationRequested();
        cachedTree = null;
        foreach (var variant in Variants(path))
            Directory.CreateDirectory(Path.Combine(LocalRoot, variant.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>The user removes a local file or folder (moved aside, never destroyed).</summary>
    public void RemoveLocal(string path)
    {
        Cancellation.ThrowIfCancellationRequested();
        cachedTree = null;
        foreach (var variant in Variants(path))
        {
            var full = Path.Combine(LocalRoot, variant.Replace('/', Path.DirectorySeparatorChar));
            var destination = Path.Combine(Removed, variant.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (Directory.Exists(full)) Directory.Move(full, destination);
            else if (File.Exists(full)) File.Move(full, destination);
        }
    }

    public void RenameLocal(string from, string to)
    {
        Cancellation.ThrowIfCancellationRequested();
        cachedTree = null;
        foreach (var (source, target) in VariantPairs(from, to))
        {
            var full = Path.Combine(LocalRoot, source.Replace('/', Path.DirectorySeparatorChar));
            var moved = Path.Combine(LocalRoot, target.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
            if (Directory.Exists(full)) Directory.Move(full, moved);
            else File.Move(full, moved);
        }
    }

    // Arrange: Dropbox.
    public void DropboxFile(string path, string content, DateTimeOffset? modified = null)
    {
        Cancellation.ThrowIfCancellationRequested();
        cachedTree = null;
        foreach (var variant in Variants(path))
        {
            EnsureDropboxParents(variant);
            Account.SeedFile(variant, content, modified ?? T0);
        }
    }

    public void DropboxFolder(string path)
    {
        Cancellation.ThrowIfCancellationRequested();
        cachedTree = null;
        foreach (var variant in Variants(path))
        {
            EnsureDropboxParents(variant);
            Account.SeedFolder(variant);
        }
    }

    public void RemoveDropbox(string path)
    {
        Cancellation.ThrowIfCancellationRequested();
        cachedTree = null;
        foreach (var variant in Variants(path)) Account.Delete(variant);
    }

    public void RenameDropbox(string from, string to)
    {
        Cancellation.ThrowIfCancellationRequested();
        cachedTree = null;
        foreach (var (source, target) in VariantPairs(from, to)) Account.Rename(source, target);
    }

    /// <summary>Local file bytes keyed by relative path.</summary>
    public IReadOnlyDictionary<string, byte[]> LocalFiles()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var full in Directory.EnumerateFiles(LocalRoot, "*", LocalTreeOptions))
            files[Relative(full)] = File.ReadAllBytes(full);
        return files;
    }

    /// <summary>Dropbox file bytes keyed by relative path.</summary>
    public IReadOnlyDictionary<string, byte[]> DropboxFiles()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (path, folder, bytes) in Account.Snapshot())
            if (!folder) files[path] = bytes;
        return files;
    }

    /// <summary>Both trees as sorted lines with exact bytes: "L:path=base64" / "L:path/" and "D:...".</summary>
    public string[] Snapshot()
    {
        Cancellation.ThrowIfCancellationRequested();
        var lines = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(LocalRoot, "*", LocalTreeOptions))
            lines.Add("L:" + Relative(directory) + "/");
        foreach (var file in Directory.EnumerateFiles(LocalRoot, "*", LocalTreeOptions))
            lines.Add("L:" + Relative(file) + "=" + Convert.ToBase64String(File.ReadAllBytes(file)));
        foreach (var (path, folder, bytes) in Account.Snapshot())
            lines.Add("D:" + path + (folder ? "/" : "=" + Convert.ToBase64String(bytes)));
        lines.Sort(StringComparer.Ordinal);
        return lines.ToArray();
    }

    /// <summary>Asserts the exact file and directory sets on both sides; an entry without '=' is a folder.</summary>
    public void RequireTree(string[] local, string[] dropbox)
    {
        var expected = Expand(local, "L:").Concat(Expand(dropbox, "D:")).ToList();
        expected.Sort(StringComparer.Ordinal);
        var actual = Snapshot();
        runs.Add("expected tree: " + string.Join(" | ", expected));
        runs.Add("observed tree: " + string.Join(" | ", actual));
        Require(expected.SequenceEqual(actual), "the trees match (" + FirstMismatch(expected.ToArray(), actual) + ")");
    }

    /// <summary>Asserts only the empty numbered roots remain on both sides (a scaled delete leaves them).</summary>
    public void RequireOnlyNumberedRoots()
    {
        var expected = Enumerable.Range(1, Copies - 1)
            .SelectMany(index => new[] { "L:" + index + "/", "D:" + index + "/" })
            .OrderBy(line => line, StringComparer.Ordinal).ToArray();
        var actual = Snapshot();
        Require(expected.SequenceEqual(actual),
            "the trees match (" + FirstMismatch(expected, actual) + ")");
    }

    /// <summary>Takes per-case cleanup failures so the runner can fail the run for them.</summary>
    public static IReadOnlyList<string> TakeCleanupFailures()
    {
        lock (CleanupGate)
        {
            var result = PendingCleanupFailures.ToArray();
            PendingCleanupFailures.Clear();
            return result;
        }
    }

    private static void RecordCleanupFailure(string message)
    {
        lock (CleanupGate) PendingCleanupFailures.Add(message);
    }

    /// <summary>Stable issue identity (id, kind, cause, path, subject) for before/after comparison.</summary>
    private string[] IssueSnapshot() =>
        Store.Issues(Pair.PairId).OrderBy(issue => issue.Id)
            .Select(issue => issue.Id + "|" + issue.Kind + "|" + issue.Cause + "|" + issue.Path + "|" +
                (issue.Subject.Incoming?.ToString() ?? "-") + "|" +
                string.Join(",", issue.Subject.Items.Select(item => item.Side + ":" + item.Path + ":" + item.Id)))
            .ToArray();

    /// <summary>Asserts the complete keep-both mapping: own bytes at the name, the incoming side's label with its bytes.</summary>
    public void RequireKeepBoth(string name, string localContent, string dropboxContent)
    {
        RequireKeepBothSide(LocalFiles(), Directories(local: true), name, localContent, dropboxContent, "Dropbox");
        RequireKeepBothSide(DropboxFiles(), Directories(local: false), name, dropboxContent, localContent, "Local");
    }

    private void RequireKeepBothSide(IReadOnlyDictionary<string, byte[]> files,
        IReadOnlyCollection<string> directories, string name, string own, string incoming, string incomingSide)
    {
        var ownBytes = System.Text.Encoding.UTF8.GetBytes(own);
        var incomingBytes = System.Text.Encoding.UTF8.GetBytes(incoming);
        var prefixes = Enumerable.Range(0, Copies).Select(index => index == 0 ? "" : index + "/").ToArray();
        var expectedDirectories = prefixes.Skip(1).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var actualDirectories = directories.OrderBy(path => path, StringComparer.Ordinal).ToArray();
        Require(actualDirectories.SequenceEqual(expectedDirectories),
            "the directory set is [" + string.Join(",", expectedDirectories) + "] (actual: [" +
            string.Join(",", actualDirectories) + "])");

        var basePaths = prefixes.Select(prefix => prefix + name + ".txt").ToHashSet(StringComparer.Ordinal);
        var labels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, bytes) in files)
        {
            if (basePaths.Contains(path))
            {
                Require(bytes.SequenceEqual(ownBytes), "own bytes at " + path);
                continue;
            }
            var match = System.Text.RegularExpressions.Regex.Match(path,
                @"^(?<prefix>(?:\d+/)?)" + System.Text.RegularExpressions.Regex.Escape(name) +
                @" \((?<side>Local|Dropbox) \d{14}\)\.txt$");
            Require(match.Success, "a keep-both file matches the label pattern at " + path);
            Require(match.Groups["side"].Value == incomingSide,
                "the label at " + path + " names the incoming " + incomingSide + " side");
            Require(bytes.SequenceEqual(incomingBytes), "incoming bytes at " + path);
            labels.Add(match.Groups["prefix"].Value);
        }
        Require(files.Count == basePaths.Count + prefixes.Length &&
            prefixes.All(labels.Contains),
            "exactly one labelled copy per item (files: [" + string.Join(",", files.Keys) + "])");
    }

    /// <summary>Relative directory paths with a trailing slash.</summary>
    public IReadOnlyCollection<string> Directories(bool local) =>
        local
            ? Directory.EnumerateDirectories(LocalRoot, "*", LocalTreeOptions)
                .Select(directory => Relative(directory) + "/").ToArray()
            : Account.Snapshot().Where(item => item.Folder).Select(item => item.Path + "/").ToArray();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (Store is null)
        {
            Release();
            return;
        }
        var artifacts = live is null ? Root : Path.Combine(live.ArtifactsRoot, Path.GetFileName(Root));
        try
        {
            foreach (var log in logs) log.Dispose();
            Directory.CreateDirectory(artifacts);
            File.WriteAllLines(Path.Combine(artifacts, "calls.txt"), Account.Calls.Skip(callStart));
            File.WriteAllText(Path.Combine(artifacts, "trees.txt"), string.Join(Environment.NewLine, runs));
            File.WriteAllText(Path.Combine(artifacts, "summary.txt"),
                (live is null ? "OFFLINE: in-memory provider" : "LIVE: real provider") +
                Environment.NewLine +
                (failure is null ? (Skipped ? "SKIP" : "PASS") : "FAIL: " + failure +
                    Environment.NewLine + IssuesText()));
            if (live is not null)
            {
                var logCopies = Path.Combine(artifacts, "logs");
                Directory.CreateDirectory(logCopies);
                foreach (var log in Directory.EnumerateFiles(Logs))
                    File.Copy(log, Path.Combine(logCopies, Path.GetFileName(log)), true);
            }
            if (live is not null && failure is not null)
                Console.Error.WriteLine("live artifacts: " + artifacts);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            RecordCleanupFailure("scenario artifacts " + artifacts + ": " + error.Message);
        }
        finally
        {
            Release();
            if (live is null && failure is null)
                foreach (var data in new[] { LocalRoot, Recovery, Removed }.Concat(addedLocalRoots))
                {
                    try { Directory.Delete(data, recursive: true); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        RecordCleanupFailure("scenario data " + data + ": " + error.Message);
                    }
                }
        }
    }

    /// <summary>Refuses any pre-existing local root: file, directory (even empty) or reparse point.</summary>
    public static void RequireFreshRoot(string root)
    {
        if (TryAttributes(root) is not null || Path.Exists(root))
            throw new InvalidOperationException("refusing to run: " + root +
                " already exists in some form; remove it first");
    }

    /// <summary>Verifies the root is a real directory, not a reparse point, before writing into it.</summary>
    public static void RequireRealDirectory(string root)
    {
        Check.Require(TryAttributes(root) is { } attributes && (attributes & FileAttributes.Directory) != 0 &&
            (attributes & FileAttributes.ReparsePoint) == 0,
            root + " is a real directory (no reparse point)");
    }

    /// <summary>Authorizes the live roots, refuses pre-existing content and creates the two owner-approved folders.</summary>
    public static void StartLive()
    {
        // Offline cases replace the shell seam; live deletions must reach the real Recycle Bin.
        LocalBin.SendOverride = null;
        var connectionId = Environment.GetEnvironmentVariable(LiveConnectionVariable)?.Trim();
        Check.Require(!string.IsNullOrEmpty(connectionId),
            "set " + LiveConnectionVariable + " to the live connection id");
        // A packaged host redirects LocalAppData, so the live local root can be placed elsewhere.
        var localRoot = Environment.GetEnvironmentVariable(LiveLocalRootVariable)?.Trim() is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LiveLocalFolderName);
        var authority = Massdex.SyncShark.PilotInstanceLock.AcquireForMaintenance(
            Massdex.SyncShark.SyncSharkRuntimePaths.InstanceLockFile);
        HttpClient? tokenHttp = null;
        HttpClient? probeHttp = null;
        DropboxSide? probe = null;
        Entry? createdRoot = null;
        var createdLocal = false;
        try
        {
            var binding = new Massdex.SyncShark.DropboxConnectionStore(authority.StateAuthority).Load(connectionId)
                ?? throw new InvalidOperationException(
                    "the live connection id is not in the engine's credential store");
            tokenHttp = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
                { Timeout = Timeout.InfiniteTimeSpan };
            var tokens = new Massdex.SyncShark.RefreshingDropboxAccessTokenSource(tokenHttp,
                new Massdex.SyncShark.WindowsDropboxRefreshTokenStore(), binding);
            var limiter = new AdaptiveLimiter(InitialLimiterLevel, InitialLimiterLevel, TimeProvider.System,
                AvailableSignal(), _ => { }, DefaultSettings.Backoff);
            probeHttp = new HttpClient(new LiveDropboxHandler(new SocketsHttpHandler { AllowAutoRedirect = false }))
                { Timeout = Timeout.InfiniteTimeSpan };
            var scratch = Path.Combine(RunRoot, "live-spool");
            Directory.CreateDirectory(scratch);
            var api = new DropboxApi(probeHttp, tokens, binding.RootNamespaceId, limiter,
                ct => LocalSide.CreatePageSpoolAsync(scratch, ct), DefaultSettings.Backoff, TimeProvider.System,
                binding.AccountId, "live-probe");
            var probePair = new PairConfig("live-probe", "live-probe", localRoot, LiveDropboxRoot, LivePairKey,
                [], [], FirstRunPolicy.NewestWins, true, null,
                BoundRoots(localRoot, LiveDropboxRoot, LivePairKey, binding.RootNamespaceId,
                    new DirectoryIdentity(1, 1, null)));
            var probeScope = new PathScope(probePair, [probePair]);
            probe = new DropboxSide(api, probePair, probeScope, DefaultSettings,
                new DropboxListing(api, probePair, probeScope));

            // shortcut: another process can create or replace the dedicated root between this check and the
            // create/delete below; accepted residual risk (dedicated, owner-approved folders, recoverable
            // deletions) - no directory lease or conditional delete. Revisit before supporting shared or
            // non-dedicated roots or concurrent external mutation of the root.
            RequireFreshRoot(localRoot);
            var root = probe.ReadOneAsync("", Cancellation).GetAwaiter().GetResult();
            if (root is not null && AnyEntry(probe))
                throw new InvalidOperationException("refusing to run: Dropbox " + LiveDropboxRoot +
                    " already exists with content; remove it first");
            if (root is null) createdRoot = probe.EnsureFolderAsync("", Cancellation).GetAwaiter().GetResult();
            var rootId = (root ?? createdRoot!).Id;
            // Concurrent cases would otherwise race to create their shared per-run parent folder.
            probe.EnsureFolderAsync(Path.GetFileName(RunRoot), Cancellation).GetAwaiter().GetResult();
            Directory.CreateDirectory(localRoot);
            createdLocal = true;
            RequireRealDirectory(localRoot);
            live = new LiveContext(localRoot, tokens, binding.RootNamespaceId, limiter, authority, probe, probeHttp,
                tokenHttp, rootId, Directory.GetCreationTimeUtc(localRoot), binding);
            tokenHttp = null;
            probeHttp = null;
        }
        catch (Exception error)
        {
            var cleanup = new List<string>();
            if (createdRoot is not null) cleanup.Add(RetainedRootDiagnostic(createdRoot));
            if (createdLocal)
            {
                var attributes = TryAttributes(localRoot);
                if (attributes is { } real && (real & FileAttributes.Directory) != 0 &&
                    (real & FileAttributes.ReparsePoint) == 0)
                {
                    try
                    {
                        Directory.Delete(localRoot, recursive: true);
                        cleanup.Add("the partially created local root was removed");
                    }
                    catch (Exception failure)
                    { cleanup.Add("the partially created local root was left at " + localRoot + ": " + failure.Message); }
                }
                else
                {
                    cleanup.Add("the partially created local root was left at " + localRoot +
                        " (not a real directory)");
                }
            }
            tokenHttp?.Dispose();
            probeHttp?.Dispose();
            authority.Dispose();
            throw StartupFailure(error, cleanup);
        }
    }

    /// <summary>Deletes both owner-approved folders when they are still the ones this run created; returns any failure.</summary>
    public static IReadOnlyList<string> EndLive()
    {
        if (live is not { } context) return [];
        var failures = new List<string>();
        try
        {
            var root = context.Probe.ReadOneAsync("", Cancellation).GetAwaiter().GetResult();
            if (root is null)
                failures.Add("the Dropbox test folder is missing; nothing deleted");
            else if (root.Id != context.RootId)
                failures.Add("the Dropbox test folder id changed (moved or replaced); not deleted");
            else
                CompleteLiveRootAsync(context.Probe, context.ProbeHttp, context.Tokens, context.RootNamespaceId,
                    context.RootId, Cancellation).GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            failures.Add("the Dropbox test folder was not deleted: " + error.Message);
        }
        try
        {
            if (!Directory.Exists(context.LocalRoot))
                failures.Add("the local test folder is missing; nothing recycled");
            else if (TryAttributes(context.LocalRoot) is { } attributes &&
                     (attributes & FileAttributes.ReparsePoint) != 0)
                failures.Add("the local test folder became a reparse point; not recycled");
            else if (Directory.GetCreationTimeUtc(context.LocalRoot) != context.LocalCreatedUtc)
                failures.Add("the local test folder is not the one this run created; not recycled");
            else
                LocalRecycle.SendAsync(context.LocalRoot, null, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            failures.Add("the local test folder was not recycled: " + error.Message);
        }
        context.ProbeHttp.Dispose();
        context.TokenHttp.Dispose();
        context.Authority.Dispose();
        live = null;
        return failures;
    }

    private static bool AnyEntry(DropboxSide side) => AnyEntryAsync(side).GetAwaiter().GetResult();

    private static async Task<bool> AnyEntryAsync(DropboxSide side)
    {
        await foreach (var _ in side.WithListingContext(BeginListing).ReadAllAsync("",
            new ListOptions(false, false), Cancellation))
            return true;
        return false;
    }

    /// <summary>One ordinary request-attempt context per listing read, as the pair session composes it.</summary>
    internal static ListingReadContext BeginListing()
    {
        var operation = new object();
        return new ListingReadContext(() => new RequestAttempt(operation, AttemptOwner.Request),
            static (attempt, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                attempt.TryClaimCompletion();
                return Task.FromResult(false);
            }, static () => ValueTask.CompletedTask);
    }

    /// <summary>The fixture's loaded settings, so the composition seam checks use the same values.</summary>
    internal static EngineSettings FixtureSettings => DefaultSettings;

    /// <summary>The local root's actual volume serial, as a Local proof records it.</summary>
    internal string LocalVolumeId =>
        Massdex.SyncShark.Engine.Local.VolumeSpace.Serial(LocalRoot).ToString(CultureInfo.InvariantCulture);

    /// <summary>The one authenticated delete-v2 sender: one frozen full folder id and cancellation, one POST.</summary>
    internal static Func<string, CancellationToken, Task<HttpResponseMessage>> FolderDeleteSender(HttpClient http,
        Func<CancellationToken, Task<string>> accessToken, string rootNamespaceId)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(accessToken);
        return async (folderId, ct) =>
        {
            var token = await accessToken(ct).ConfigureAwait(false);
            using var request = AuthenticatedRequest(DropboxProtocol.Delete, token, rootNamespaceId,
                new Dictionary<string, object> { [DropboxProtocol.Path] = folderId });
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        };
    }

    /// <summary>The one authenticated get_metadata observer: one frozen full folder id, one bounded reply.</summary>
    internal static Func<string, CancellationToken, Task<FolderIdObservation>> FolderIdObserver(HttpClient http,
        Func<CancellationToken, Task<string>> accessToken, string rootNamespaceId)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(accessToken);
        return async (folderId, ct) =>
        {
            try
            {
                var token = await accessToken(ct).ConfigureAwait(false);
                using var request = AuthenticatedRequest(DropboxProtocol.GetMetadata, token, rootNamespaceId,
                    new Dictionary<string, object>
                    {
                        [DropboxProtocol.Path] = folderId,
                        [DropboxProtocol.IncludeDeleted] = false,
                    });
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Conflict && await IsNotFoundAsync(response, ct)
                        .ConfigureAwait(false))
                    return new FolderIdObservation.AuthoritativeNotFound();
                if (!response.IsSuccessStatusCode) return new FolderIdObservation.Uncertain();
                using var reply = await ReadBoundedAsync(response, ct).ConfigureAwait(false);
                var root = reply.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty(DropboxProtocol.Tag, out var tag) || tag.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty(DropboxProtocol.Id, out var id) || id.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty(DropboxProtocol.PathDisplay, out var path) ||
                    path.ValueKind != JsonValueKind.String)
                    return new FolderIdObservation.Uncertain();
                return new FolderIdObservation.Live(id.GetString()!, tag.GetString() == DropboxProtocol.TagFolder,
                    path.GetString()!);
            }
            catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException
                                             or JsonException or InvalidOperationException)
            {
                return new FolderIdObservation.Uncertain();
            }
        };
    }

    /// <summary>The one bounded external-actor body EndLive uses for the top-level owned root.</summary>
    internal static Task CompleteLiveRootAsync(DropboxSide probe, HttpClient probeHttp,
        Massdex.SyncShark.IDropboxAccessTokenSource tokens, string rootNamespaceId, string rootId,
        CancellationToken ct) =>
        LiveDropboxAccount.CompleteFolderArrangementAsync("", rootId, "", rootId,
            FolderDeleteSender(probeHttp, tokens.GetAccessTokenAsync, rootNamespaceId),
            FolderIdObserver(probeHttp, tokens.GetAccessTokenAsync, rootNamespaceId),
            (candidate, token) => probe.ReadOneAsync(candidate, token), ct);

    /// <summary>The retained-root diagnostic naming the real provider folder, never the pair-relative receipt path.</summary>
    internal static string RetainedRootDiagnostic(Entry createdRoot) =>
        "startup failed; the partially created Dropbox root is retained (path=" + LiveDropboxRoot +
        "; id=" + createdRoot.Id + "); no folder cleanup was attempted";

    /// <summary>Preserves the original startup error and appends the existing cleanup failures.</summary>
    internal static InvalidOperationException StartupFailure(Exception error, IReadOnlyList<string> cleanup) =>
        new(error.Message + (cleanup.Count == 0 ? "" : "; " + string.Join("; ", cleanup)), error);

    private static HttpRequestMessage AuthenticatedRequest(string route, string accessToken,
        string rootNamespaceId, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, DropboxApiRoot + route);
        request.Headers.TryAddWithoutValidation("Authorization", DropboxProtocol.BearerScheme + " " + accessToken);
        request.Headers.TryAddWithoutValidation(DropboxProtocol.PathRootHeader, PathRoot(rootNamespaceId));
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }

    private static string PathRoot(string rootNamespaceId) =>
        JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [DropboxProtocol.Tag] = DropboxProtocol.Root,
            [DropboxProtocol.Root] = rootNamespaceId,
        });

    private static async Task<bool> IsNotFoundAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var reply = await ReadBoundedAsync(response, ct).ConfigureAwait(false);
            var root = reply.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty(DropboxProtocol.ErrorSummary, out var summary) &&
                summary.ValueKind == JsonValueKind.String &&
                (summary.GetString() ?? "").StartsWith(DropboxProtocol.PathNotFound, StringComparison.Ordinal))
                return true;
            if (!root.TryGetProperty(DropboxProtocol.Error, out var error) ||
                error.ValueKind != JsonValueKind.Object) return false;
            if (error.TryGetProperty(DropboxProtocol.Path, out var detail) &&
                detail.ValueKind == JsonValueKind.Object &&
                detail.TryGetProperty(DropboxProtocol.Tag, out var detailTag) &&
                detailTag.ValueKind == JsonValueKind.String &&
                detailTag.GetString() == NotFoundTag)
                return true;
            return error.TryGetProperty(DropboxProtocol.Tag, out var tag) && tag.ValueKind == JsonValueKind.String &&
                tag.GetString() == NotFoundTag;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<JsonDocument> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content is not { } content)
            throw new InvalidOperationException("the metadata reply has no body");
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var bytes = new byte[MaximumObservationReplyBytes + 1];
        var read = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, ct).ConfigureAwait(false);
        if (read > MaximumObservationReplyBytes)
            throw new InvalidOperationException("the metadata reply is too large");
        return JsonDocument.Parse(bytes.AsMemory(0, read));
    }

    private void BuildSides()
    {
        host!.DrainGenerationAsync(Pair.PairId, Cancellation).GetAwaiter().GetResult();
        host.RecomposeCurrentAsync(Pair.PairId, Cancellation).GetAwaiter().GetResult();
        host.DispatchAsync(Pair.PairId,
            (registration, pair, generation, _) =>
            {
                var session = PairSession.Open(observedStore, pair, Settings, http!, false, null,
                    authority!.StateAuthority, host, registration, generation, TimeProvider.System);
                Scope = session.Scope;
                Local = session.Local;
                Dropbox = host.BorrowGeneration(registration, pair.PairId, generation).Dropbox;
                return Task.FromResult(true);
            }, Cancellation).GetAwaiter().GetResult();
    }

    private EngineHost ComposeHost()
    {
        authority = Massdex.SyncShark.PilotInstanceLock.AcquireForMaintenance(
            Path.Combine(Root, "authority", "state", "lock"));
        var binding = live?.Binding ?? new DropboxConnectionBinding(
            DropboxProviderConfiguration.RegisteredClientId, "account-id", RootNamespace, "home-namespace",
            DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes,
            ConnectionId: Pair.ConnectionId);
        new DropboxConnectionStore(authority.StateAuthority).SaveNewAsync(binding with
            { ConnectionId = Pair.ConnectionId }).GetAwaiter().GetResult();
        return EngineHost.Compose(authority.StateAuthority.ProductRoot, message => Progress?.Invoke(message),
            authority.StateAuthority, Settings,
            http!, TimeProvider.System, _ => RunLog.Open(Logs, Pair.PairId, ResourceNames.NewId(),
                message => Progress?.Invoke(message), Settings.LogBufferCharacters), validator =>
                {
                    Store = StateStore.Open(Path.Combine(Root, "state.db"), validator);
                    Store.SavePair(Pair);
                    return observedStore = new ScenarioStore(Store);
                }, EngineHost.Mode.Persistent,
            new EngineExternals(live is null ? new SyntheticCredentials() : new WindowsDropboxRefreshTokenStore(),
                _ => new HttpClient(transport!, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan }));
    }

    private static RootBinding BoundRoots(string localRoot, string dropboxRoot, string connectionId,
        string rootNamespaceId, DirectoryIdentity localIdentity) =>
        new(localRoot, dropboxRoot, connectionId, localIdentity,
            new DropboxRootIdentity("account-id", rootNamespaceId, "home-namespace", "folder-id"));

    private void Release()
    {
        foreach (var log in logs) log.Dispose();
        logs.Clear();
        try
        {
            if (host is not null)
            {
                host.DisposeAsync().AsTask().GetAwaiter().GetResult();
                host = null;
            }
            else
            {
                Store?.Dispose();
            }
        }
        finally
        {
            http?.Dispose();
            http = null;
            authority?.Dispose();
            authority = null;
        }
    }

    private sealed class ScenarioStore(IStateStore inner) : ForwardingStateStore(inner)
    {
        internal Action<SyncedEntry>? AfterFolder;
        internal Action<SyncedEntry>? EachFolder;
        internal Action<Side, string>? AfterSyncedByKey;
        internal Action<WorkSelection?>? Selected;
        internal int LiveValidations;
        internal int EndedWorkPasses;
        internal Action? PassEnded;
        internal Action? BeforeRepairCheckpoint;
        internal Action<RepairSummary>? RepairFinished;
        internal Action? BeforePairs;
        internal Action<WorkSelection>? BeforeLiveValidation;
        internal Action? BeforeDirtyRead;
        internal Func<Row, bool>? HeldWithoutIssue;
        internal Func<IEnumerable<WorkSelection>>? SelectionAncestors;
        internal Action<string>? ScanBegun;
        internal Action<string, ChangeNotice>? NoticeCommitted;
        internal Action<WorkSelection>? WorkCompleted;
        internal int ItemRetryReads;
        internal string? CountedPair;
        internal int CountedReads;
        internal int CountedProgressChecks;
        internal TaskCompletionSource<ItemRetryCandidate> InUseFailure { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Guid BeginScan(string pairId, Guid scanId, long expectedGeneration, bool includeDeleted,
            string? scope = null)
        {
            var token = base.BeginScan(pairId, scanId, expectedGeneration, includeDeleted, scope);
            ScanBegun?.Invoke(pairId);
            return token;
        }

        public override Massdex.SyncShark.V2.FailureRetryOwner.State? ReadItemRetry(string pairId)
        {
            Interlocked.Increment(ref ItemRetryReads);
            if (pairId == Volatile.Read(ref CountedPair)) Interlocked.Increment(ref CountedReads);
            return base.ReadItemRetry(pairId);
        }

        public override bool HasFolderJobProgress(string pairId, CancellationToken ct)
        {
            if (pairId == Volatile.Read(ref CountedPair)) Interlocked.Increment(ref CountedProgressChecks);
            return base.HasFolderJobProgress(pairId, ct);
        }

        public override long? AcquireJournalPage(string volumeKey, JournalPosition? expected, JournalPosition next,
            IReadOnlyList<(string PairId, long Generation, long Epoch, long Revision, bool LostInterval)> members,
            IEnumerable<(string PairId, ChangeNotice Notice)> notices)
        {
            if (NoticeCommitted is not { } observe) return base.AcquireJournalPage(volumeKey, expected, next, members, notices);
            var page = notices.ToList();
            var committed = base.AcquireJournalPage(volumeKey, expected, next, members, page);
            if (committed is not null) foreach (var (pairId, notice) in page) observe(pairId, notice);
            return committed;
        }

        public override ItemRetryCandidate RecordInUseFailure(string pairId, long expectedGeneration, long sourceEpoch,
            string path, IssueSubject subject, TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch,
            PendingEffect? expectedPending, ItemRetryToken? expectedEpisode, Guid registeredAttemptId, Action<Issue, bool>? settledIssue = null)
        {
            var candidate = base.RecordInUseFailure(pairId, expectedGeneration, sourceEpoch, path, subject, cause,
                selection, dispatch, expectedPending, expectedEpisode, registeredAttemptId, settledIssue);
            if (cause == TextKey.FailLocalInUse) InUseFailure.TrySetResult(candidate);
            return candidate;
        }

        public override IEnumerable<WorkSelection> PendingAncestors(string pairId, long expectedGeneration, Side side,
            IReadOnlyList<string> ancestorKeys, long? coveredThrough = null) =>
            SelectionAncestors?.Invoke() ?? base.PendingAncestors(pairId, expectedGeneration, side, ancestorKeys, coveredThrough);

        public override IssueKind? HeldKind(string pairId, Row row, IReadOnlySet<long>? exceptIssues) =>
            HeldWithoutIssue?.Invoke(row) == true ? IssueKind.Tie : base.HeldKind(pairId, row, exceptIssues);

        public override (bool Dirty, JournalPosition? SourceBoundary, long EvidenceVersion) ReadLocalDirty(
            string pairId, long expectedGeneration, long localSourceEpoch, string nativeDomain, string? fullIdentity,
            string? currentPathKey, string? recordedPathKey)
        {
            BeforeDirtyRead?.Invoke();
            return base.ReadLocalDirty(pairId, expectedGeneration, localSourceEpoch, nativeDomain, fullIdentity,
                currentPathKey, recordedPathKey);
        }

        public override IReadOnlyList<PairConfig> Pairs()
        {
            BeforePairs?.Invoke();
            return base.Pairs();
        }

        public override bool FinishRepair(string pairId, RepairSummary summary, long expectedGeneration,
            Guid scanToken, LocalCatchUp local, DropboxCatchUp dropbox, out Exception? secondaryFailure)
        {
            var committed = base.FinishRepair(pairId, summary, expectedGeneration, scanToken, local, dropbox, out secondaryFailure);
            RepairFinished?.Invoke(summary);
            return committed;
        }

        public override bool EndWorkPass(string pairId)
        {
            var retired = base.EndWorkPass(pairId);
            Interlocked.Increment(ref EndedWorkPasses);
            PassEnded?.Invoke();
            return retired;
        }

        public override void SaveLimiterLevel(string connectionId, int level)
        {
            BeforeRepairCheckpoint?.Invoke();
            base.SaveLimiterLevel(connectionId, level);
        }

        public override SyncedEntry? SyncedByKey(string pairId, Side side, string key)
        {
            var saved = base.SyncedByKey(pairId, side, key);
            AfterSyncedByKey?.Invoke(side, key);
            return saved;
        }

        public override WorkSelection? SelectWork(string pairId, long expectedGeneration, PathScope scope)
        {
            var selection = base.SelectWork(pairId, expectedGeneration, scope);
            Selected?.Invoke(selection);
            return selection;
        }
        internal TaskCompletionSource<LiveAdmissionStaleReason> Refused { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void ValidateLiveCoverage(WorkSelection selection, LiveCoverage coverage,
            IReadOnlyList<LiveEffectScope> footprint)
        {
            Interlocked.Increment(ref LiveValidations);
            try
            {
                BeforeLiveValidation?.Invoke(selection);
                base.ValidateLiveCoverage(selection, coverage, footprint);
            }
            catch (LiveAdmissionStaleException error)
            {
                Refused.TrySetResult(error.Reason);
                throw;
            }
        }
        private string? scanKey;
        private readonly object gate = new();
        private readonly Dictionary<string, (string Hash, TaskCompletionSource<long> Signal)> completions = new(StringComparer.Ordinal);

        internal Task<long> ObserveCompletion(string key, string hash)
        {
            lock (gate)
            {
                var signal = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
                completions.Add(key, (hash, signal));
                return signal.Task;
            }
        }

        public override IEnumerable<Row> ScanRows(string pairId, Guid scanId, Func<Row, RowContext> context)
        {
            foreach (var row in base.ScanRows(pairId, scanId, context))
            {
                scanKey = row.Key;
                yield return row;
            }
        }

        public override void Commit(string pairId, StateChange change, Action<Issue>? committedIssue = null, Action<Issue, bool>? settledIssue = null)
        {
            base.Commit(pairId, change, committedIssue, settledIssue);
            ObserveFolder(change);
        }

        public override void Commit(string pairId, StateChange change, WorkSelection? selection, Action<Issue>? committedIssue, Action<Issue, bool>? settledIssue = null)
        {
            base.Commit(pairId, change, selection, committedIssue, settledIssue);
            ObserveFolder(change);
        }

        public override async Task CommitRecordAsync(string pairId, StateChange change, Action<Issue>? committedIssue, Action<Issue, bool>? settledIssue = null)
        {
            await base.CommitRecordAsync(pairId, change, committedIssue, settledIssue);
            ObserveFolder(change);
        }

        // A folder record reaches the store through whichever commit path the product uses; the fixture hook must
        // observe all of them, not only the plain Commit.
        private void ObserveFolder(StateChange change)
        {
            foreach (var saved in change.Record.Where(saved => saved.IsFolder))
            {
                if (AfterFolder is { } after)
                {
                    Check.Require(scanKey == saved.LocalKey,
                        "the folder record is committed in its own row, before any child row");
                    AfterFolder = null;
                    after(saved);
                }
                EachFolder?.Invoke(saved);
            }
        }

        private void SignalCompleted(string pairId, string completedKey)
        {
            lock (gate)
            {
                foreach (var (key, expected) in completions.ToArray())
                    if (PathNames.IsSelfOrUnderKey(key, completedKey) &&
                        Inner.SyncedByKey(pairId, Side.Local, key)?.DropboxContentHash == expected.Hash)
                    {
                        completions.Remove(key);
                        expected.Signal.TrySetResult(System.Diagnostics.Stopwatch.GetTimestamp());
                    }
            }
        }

        public override bool CompleteWork(WorkSelection selection, SelectionPeer? peer)
        {
            var completed = base.CompleteWork(selection, peer);
            if (completed) WorkCompleted?.Invoke(selection);
            var saved = Inner.SyncedByKey(selection.PairId, selection.Side, selection.Key);
            if (saved is not null) SignalCompleted(selection.PairId, saved.LocalKey);
            return completed;
        }

        // A released in-use file settles through Settle → CompleteItemAttempt, not CompleteWork; the same durable
        // hash is the completion. The base call throws on failure, so reaching here means the change committed.
        public override void CompleteItemAttempt(ItemRetryCandidate expected, Guid registeredAttemptId, StateChange change,
            PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null,
            Action<Issue, bool>? settledIssue = null)
        {
            base.CompleteItemAttempt(expected, registeredAttemptId, change, completedPending, selection, committedIssue,
                settledIssue);
            foreach (var saved in change.Record)
                SignalCompleted(expected.PairId, saved.LocalKey);
        }
    }

    private static CancellationTokenSource CreateStopSource()
    {
        var source = new CancellationTokenSource();
        Console.CancelKeyPress += (_, args) =>
        {
            args.Cancel = true;
            source.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => source.Cancel();
        return source;
    }

    private static FakeNetworkSignal AvailableSignal()
    {
        var signal = new FakeNetworkSignal();
        signal.SetAvailable(true);
        return signal;
    }

    private static bool Bury(string fullPath)
    {
        var localRoot = activeLocalRoot;
        var recovery = activeRecovery;
        if (localRoot is null || recovery is null) return false;
        var full = Path.GetFullPath(fullPath);
        if (!full.StartsWith(localRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        var relative = full[(localRoot.Length + 1)..];
        var destination = Path.Combine(recovery, relative);
        for (var attempt = 1; File.Exists(destination) || Directory.Exists(destination); attempt++)
            destination = Path.Combine(recovery, attempt.ToString(CultureInfo.InvariantCulture) + "-" + relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (Directory.Exists(full)) Directory.Move(full, destination);
        else if (File.Exists(full)) File.Move(full, destination);
        return true;
    }

    private static FileAttributes? TryAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private void EnsureDropboxParents(string path)
    {
        var parts = path.Split('/');
        for (var index = 1; index < parts.Length; index++)
            Account.SeedFolder(string.Join('/', parts[..index]));
    }

    private static IEnumerable<string> ExpandIssue(string line)
    {
        var at = line.LastIndexOf('@');
        if (at < 0) { yield return line; yield break; }
        var head = line[..(at + 1)];
        foreach (var variant in Variants(line[(at + 1)..])) yield return head + variant;
    }

    private string Relative(string full) =>
        Path.GetRelativePath(LocalRoot, full).Replace(Path.DirectorySeparatorChar, '/');

    private static IEnumerable<string> Variants(string path)
    {
        yield return path;
        for (var copy = 1; copy < Multiplier; copy++)
            yield return copy.ToString(CultureInfo.InvariantCulture) + "/" + path;
    }

    private static IEnumerable<(string From, string To)> VariantPairs(string from, string to)
    {
        var targets = Variants(to).ToArray();
        var index = 0;
        foreach (var source in Variants(from)) yield return (source, targets[index++]);
    }

    private static IEnumerable<string> Expand(string[] entries, string prefix)
    {
        var lines = new HashSet<string>(StringComparer.Ordinal);
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var cut = entry.IndexOf('=');
            var path = cut < 0 ? entry.TrimEnd('/') : entry[..cut];
            var content = cut < 0 ? null : entry[(cut + 1)..];
            foreach (var variant in Variants(path))
            {
                lines.Add(prefix + (content is null
                    ? variant + "/"
                    : variant + "=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(content))));
                var slash = variant.IndexOf('/');
                if (slash > 0) roots.Add(variant[..slash] + "/");
            }
        }
        foreach (var root in roots) lines.Add(prefix + root);
        return lines;
    }

    private static int ReadMultiplier()
    {
        var text = Environment.GetEnvironmentVariable("SYNCSHARK_SCENARIO_MULTIPLIER");
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 1
            ? value : 1;
    }

    private static string Effects(RepairSummary summary) =>
        string.Join(";", summary.Effects.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Key + "=" + item.Value));

    private static string Issues(RepairSummary summary) =>
        string.Join(";", summary.IssuesByKind.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Key + "=" + item.Value));

    private string[] IssueLines() => IssueLines(Pair.PairId);

    private string[] IssueLines(string pairId) =>
        Store.Issues(pairId).Select(issue => issue.Kind + ":" + issue.Cause + "@" + issue.Path)
            .OrderBy(line => line, StringComparer.Ordinal).ToArray();

    private string IssuesText() => string.Join(Environment.NewLine, IssueLines());

    private static string FirstMismatch(string[] expected, string[] actual)
    {
        var index = 0;
        while (index < expected.Length && index < actual.Length && expected[index] == actual[index]) index++;
        var expectedText = index < expected.Length ? expected[index] : "<none>";
        var actualText = index < actual.Length ? actual[index] : "<none>";
        return "expected " + expectedText + ", actual " + actualText;
    }

    private sealed class LiveContext(string localRoot, Massdex.SyncShark.IDropboxAccessTokenSource tokens,
        string rootNamespaceId, AdaptiveLimiter limiter, Massdex.SyncShark.PilotInstanceLock authority,
        DropboxSide probe, HttpClient probeHttp, HttpClient tokenHttp, string rootId, DateTime localCreatedUtc,
        DropboxConnectionBinding binding)
    {
        public DropboxConnectionBinding Binding { get; } = binding;
        public string LocalRoot { get; } = localRoot;
        /// <summary>Live artifacts live outside the two approved folders (the local root is recycled at the end).</summary>
        public string ArtifactsRoot { get; } = Path.Combine(Path.GetTempPath(), "syncshark-scenarios",
            "live-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        public Massdex.SyncShark.IDropboxAccessTokenSource Tokens { get; } = tokens;
        public string RootNamespaceId { get; } = rootNamespaceId;
        public AdaptiveLimiter Limiter { get; } = limiter;
        public Massdex.SyncShark.PilotInstanceLock Authority { get; } = authority;
        public DropboxSide Probe { get; } = probe;
        public HttpClient ProbeHttp { get; } = probeHttp;
        public HttpClient TokenHttp { get; } = tokenHttp;
        /// <summary>The id of the Dropbox root folder this run created (or adopted empty).</summary>
        public string RootId { get; } = rootId;
        public DateTime LocalCreatedUtc { get; } = localCreatedUtc;
    }


}

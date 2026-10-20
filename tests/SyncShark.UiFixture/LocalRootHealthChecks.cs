using Massdex.SyncShark;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SyncShark.UiFixture;

// LH5: focused real-path checks for the background readonly local-root health pass.
//
// Every check drives the production RefreshLocalRootHealthAsync on a real MainWindow, with real captured
// endpoint configurations, the real production aggregation and the real UpdatePairAttentionPresentation,
// against disposable folders this fixture creates and owns. The only substituted input is a counting wrapper
// that delegates to the real ObserveLocalRoot drive-attribute check, so "no probe" and "exactly these probes"
// are proven rather than assumed. It forces a value only where an inaccessible or offline source cannot be
// constructed safely, which is the one input the order allows a seam to supply.
//
// No provider, credential, signing, installation, sync or user-file effect is performed; no existing folder
// outside this fixture's own root is read, and nothing outside it is moved or deleted.
internal static class LocalRootHealthChecks
{
    private const string Selector = "--local-root-health-checks";
    private static uint _volumeSerialNumber;
    private static ulong _rootFileId;

    internal static int Run(string root)
    {
        Directory.CreateDirectory(root);
        // Every synthetic local endpoint carries a real stable local identity from this fixture's own folder:
        // a persisted local endpoint without one is refused by production validation, which is a real refusal
        // this check must not paper over.
        using var fixtureLease = PilotDirectoryLease.Open(root);
        _volumeSerialNumber = fixtureLease.Identity.VolumeSerialNumber;
        _rootFileId = fixtureLease.Identity.FileId;
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var theme = new ThemeManager(new UiPreferencesStore(instance.StateAuthority));
        theme.Initialize();
        MainWindow.SuppressLoadedStartupForTest = true;
        // LH4: this check owns its disposable folders, so it opts in to the readonly pass for its own
        // duration and restores the flag below. No other suppressed startup, provider, shell-registration or
        // monitor path is enabled.
        MainWindow.LocalRootHealthEnabledForTest = true;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var harness = new ProbeHarness();
        MainWindow.LocalRootObserverForTest = harness.Observe;
        var passed = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidDataException(label);
            passed++;
            Console.WriteLine("PASS " + label);
        }
        var window = new MainWindow(instance.StateAuthority, theme)
        {
            Width = 1240, Height = 790, ShowInTaskbar = false, ShowActivated = false
        };
        try
        {
            window.Show();
            Drain();
            window.UpdateLayout();
            Drain();
            // The window's own one-second workspace refresh timer also asks for this pass. This check drives
            // the production pass explicitly, so the fixture scopes that timer out for the check's duration
            // rather than racing an unrelated concurrent pass.
            Field<DispatcherTimer>(window, "_workspaceTimer").Stop();

            CheckOrderedSides(window, root, harness, Check);
            CheckRestoration(window, root, harness, Check);
            CheckCloudOnly(window, root, harness, Check);
            CheckUnknownRetention(window, root, harness, Check);
            CheckUnsupportedDocument(window, root, harness, Check);
            CheckLegacyPair(window, root, harness, Check);
            CheckPausedFirstSync(window, root, harness, Check);
            CheckStaleObservation(window, root, harness, Check);
        }
        finally
        {
            MainWindow.LocalRootObserverForTest = null;
            MainWindow.LocalRootHealthEnabledForTest = false;
            window.RequestProductExit();
            MainWindow.SuppressLoadedStartupForTest = false;
            app.Shutdown();
        }
        Console.WriteLine(passed + " " + Selector +
            " checks passed: both ordered local sides, side-labelled repair messages, cloud/cloud zero probes, " +
            "unknown and unsupported retention, legacy compatibility, paused first-sync precedence and stale " +
            "observation rejection, with no provider, credential, signing, installation or user-file effect.");
        return 0;
    }

    // LH1/LH2/LH5: each structurally defined local side is probed, a definitely absent side is named, and a
    // present other side never clears it.
    private static void CheckOrderedSides(MainWindow window, string root, ProbeHarness harness,
        Action<bool, string> check)
    {
        var leftGone = Path.Combine(root, "ordered", "left-gone");
        var rightHere = CreateDirectory(root, "ordered/right-here");
        var leftPair = "lh-ordered-left";
        Load(window, Documented(leftPair, "Left Missing", Local(leftGone), Local(rightHere)));
        harness.Clear();
        Pass(window);
        check(harness.Observed.SequenceEqual([leftGone, rightHere]),
            "LH1: a local/local pair probes exactly its two ordered local folders (" +
            string.Join(", ", harness.Observed) + ")");
        var leftMessage = MissingSides(("left", leftGone));
        check(Stored(window, leftPair) == leftMessage && window.PairKnownUnhealthyForTest(leftPair) &&
              window.PairRows.Single(row => row.Id == leftPair).AttentionReason == leftMessage,
            "LH2: a definitely absent left folder is a side-labelled repair message on the real pair row (" +
            Stored(window, leftPair) + ")");
        check(Stored(window, leftPair)!.Contains("local folder", StringComparison.OrdinalIgnoreCase) &&
              window.PairRows.Single(row => row.Id == leftPair).StatusSummary == "Local folder unavailable",
            "LH2: the repair message keeps the existing attention classification and status summary");

        var rightGone = Path.Combine(root, "ordered", "right-gone");
        var rightPair = "lh-ordered-right";
        Load(window, Documented(rightPair, "Right Missing", Local(rightHere), Local(rightGone)));
        harness.Clear();
        Pass(window);
        check(harness.Observed.SequenceEqual([rightHere, rightGone]) &&
              Stored(window, rightPair) == MissingSides(("right", rightGone)),
            "LH2: a definitely absent right folder is named as the right side even while the left is present (" +
            Stored(window, rightPair) + ")");

        var bothGone = Path.Combine(root, "ordered", "both-gone");
        var bothPair = "lh-ordered-both";
        Load(window, Documented(bothPair, "Both Missing", Local(bothGone), Local(Path.Combine(root, "ordered", "both-gone-two"))));
        harness.Clear();
        Pass(window);
        check(harness.Observed.Count == 2 && Stored(window, bothPair) ==
                MissingSides(("left", bothGone), ("right", Path.Combine(root, "ordered", "both-gone-two"))),
            "LH2: two definitely absent local folders are both named in one repair message (" +
            Stored(window, bothPair) + ")");
    }

    // LH5: a real owned folder pair clears, a real removal re-reports the exact side, and restoring the exact
    // folder clears again.
    private static void CheckRestoration(MainWindow window, string root, ProbeHarness harness,
        Action<bool, string> check)
    {
        var left = CreateDirectory(root, "restore/left");
        var right = CreateDirectory(root, "restore/right");
        var aside = Path.Combine(root, "restore", "right-aside");
        var id = "lh-restore";
        Load(window, Documented(id, "Restore", Local(left), Local(right)));
        harness.Clear();
        Pass(window);
        check(harness.Observed.SequenceEqual([left, right]) && Stored(window, id) is null &&
              !window.PairKnownUnhealthyForTest(id),
            "LH5: two owned folders observed present clear the pair with exactly two real probes");

        Directory.Move(right, aside);
        harness.Clear();
        Pass(window);
        check(harness.Observed.SequenceEqual([left, right]) &&
              Stored(window, id) == MissingSides(("right", right)) && window.PairKnownUnhealthyForTest(id),
            "LH5: removing one real owned folder reports that exact side, and the present left side does not clear it");

        Directory.Move(aside, right);
        harness.Clear();
        Pass(window);
        check(harness.Observed.SequenceEqual([left, right]) && Stored(window, id) is null &&
              !window.PairKnownUnhealthyForTest(id) &&
              window.PairRows.Single(row => row.Id == id).StatusSummary != "Local folder unavailable",
            "LH5: restoring the exact folder clears the recorded problem on the real pair row");
    }

    // LH1/LH2: a valid cloud/cloud pair probes nothing at all, even when a cloud member's recorded path looks
    // like a Windows folder, and it can only clear a stale problem for its own current configuration.
    private static void CheckCloudOnly(MainWindow window, string root, ProbeHarness harness,
        Action<bool, string> check)
    {
        var id = "lh-cloud-only";
        var cloud = Documented(id, "Cloud Cloud", Cloud("conn-lh-a", "id:lh-a"),
            Cloud("conn-lh-b", "id:lh-b", @"C:\NotALocalEndpoint"));
        Load(window, cloud);
        Problems(window)[id] = "Prior local folder evidence for a previous configuration.";
        harness.Clear();
        Pass(window);
        check(harness.Observed.Count == 0,
            "LH1: a valid cloud/cloud pair performs zero filesystem probes, including for a cloud path that " +
            "resembles a local folder");
        check(Stored(window, id) is null && !window.PairKnownUnhealthyForTest(id),
            "LH2: a configuration that records no local endpoint clears the stale local-root problem");
    }

    // LH2: an unknown observation is not proof of absence and never clears unresolved prior evidence.
    private static void CheckUnknownRetention(MainWindow window, string root, ProbeHarness harness,
        Action<bool, string> check)
    {
        var left = CreateDirectory(root, "unknown/left");
        var right = CreateDirectory(root, "unknown/right");
        var id = "lh-unknown";
        Load(window, Documented(id, "Unknown", Local(left), Local(right)));
        Problems(window)[id] = "Prior local folder evidence must survive an unknown observation.";
        // The right folder is genuinely present; only its observation is forced unknown, because an offline
        // or inaccessible source cannot be constructed safely in this fixture.
        harness.Force(right, null);
        harness.Clear();
        Pass(window);
        check(harness.Observed.SequenceEqual([left, right]) && Stored(window, id) ==
                "Prior local folder evidence must survive an unknown observation." &&
              window.PairKnownUnhealthyForTest(id),
            "LH2: an unknown observation retains the prior problem instead of clearing it");
        harness.Unforce();
        harness.Clear();
        Pass(window);
        check(Stored(window, id) is null, "LH2: the same pair clears once both real folders are observed present");
    }

    // LH2: an unsupported endpoint document stays unknown rather than falling back to the legacy local root.
    private static void CheckUnsupportedDocument(MainWindow window, string root, ProbeHarness harness,
        Action<bool, string> check)
    {
        var legacyRoot = CreateDirectory(root, "unsupported/legacy-root");
        var id = "lh-unsupported";
        var unsupported = new SyncPair(id, "Unsupported", legacyRoot, 1, 1, "/Root", "id:root",
            ConnectionId: "conn-lh-unsupported", EndpointSchemaVersion: 99,
            LeftEndpoint: Local(legacyRoot), RightEndpoint: Cloud("conn-lh-unsupported", "id:lh-unsupported"));
        Problems(window)[id] = "Prior local folder evidence must survive an unreadable document.";
        Load(window, unsupported);
        harness.Clear();
        Pass(window);
        check(harness.Observed.Count == 0 && Stored(window, id) ==
                "Prior local folder evidence must survive an unreadable document.",
            "LH2: an unsupported endpoint document probes nothing and is never read as the legacy local root");
    }

    // LH1/LH5: a legacy pair with no endpoint document keeps its one real saved local-root check.
    private static void CheckLegacyPair(MainWindow window, string root, ProbeHarness harness,
        Action<bool, string> check)
    {
        var presentRoot = CreateDirectory(root, "legacy/present");
        var id = "lh-legacy";
        Load(window, new SyncPair(id, "Legacy", presentRoot, 1, 1, "/Root", "id:root",
            ConnectionId: "conn-lh-legacy"));
        harness.Clear();
        Pass(window);
        check(harness.Observed.SequenceEqual([presentRoot]) && Stored(window, id) is null &&
              !window.PairKnownUnhealthyForTest(id),
            "LH5: a legacy pair with no endpoint document keeps exactly one real local-root check and clears");

        var goneRoot = Path.Combine(root, "legacy", "gone");
        Load(window, new SyncPair(id, "Legacy", goneRoot, 1, 1, "/Root", "id:root",
            ConnectionId: "conn-lh-legacy"));
        harness.Clear();
        Pass(window);
        check(harness.Observed.SequenceEqual([goneRoot]) &&
              Stored(window, id) == MissingSides(("left", goneRoot)),
            "LH5: a legacy pair whose saved local root is gone reports the side-labelled repair message");
    }

    // LH2/LH5: a definitely absent local folder outranks the first-sync suggestion, including while sync is
    // globally paused, and the tray attention cases carry the problem instead of a first-sync case.
    private static void CheckPausedFirstSync(MainWindow window, string root, ProbeHarness harness,
        Action<bool, string> check)
    {
        var gone = Path.Combine(root, "first-sync", "gone");
        var id = "lh-first-sync";
        Load(window, new SyncPair(id, "First Sync", gone, 1, 1, "/Root", "id:root",
            ConnectionId: "conn-lh-first-sync", InitialReviewApproved: false));
        harness.Clear();
        Pass(window);
        var message = MissingSides(("left", gone));
        var row = window.PairRows.Single(item => item.Id == id);
        check(Stored(window, id) == message && row.AttentionReason == message &&
              !row.AttentionReason.Contains("First sync", StringComparison.OrdinalIgnoreCase),
            "LH2: a known missing local root outranks the first-sync suggestion on the real pair row");

        Pump(window.SetSyncPauseAsync(TimeSpan.FromMinutes(15), false));
        Drain();
        check(window.TrayPaused, "LH5: the fixture reached the real global paused state");
        harness.Clear();
        Pass(window);
        row = window.PairRows.Single(item => item.Id == id);
        check(Stored(window, id) == message && row.AttentionReason == message &&
              window.PairKnownUnhealthyForTest(id),
            "LH2: the repair message is produced and keeps precedence while sync is globally paused");
        var cases = window.TrayAttentionCases;
        check(cases.Any(item => item.IsProblem && item.Description == message) &&
              !cases.Any(item => item.InternalIcon == InternalAttentionIcon.FirstSyncReview),
            "LH2: the shared attention cases carry the local problem and not a first-sync suggestion");
        check(cases.Single(item => item.IsProblem && item.Pair.Id == id).Title == "Local folder unavailable",
            "LH2: the problem case keeps the existing short attention summary");
        Pump(window.SetSyncPauseAsync(null, false));
        Drain();
        check(!window.TrayPaused && window.GlobalPauseActive == false,
            "LH5: the fixture restored its own pause state after the precedence check");
    }

    // LH3/LH5: an observation is applied only while the exact pair id and captured configuration still hold.
    private static void CheckStaleObservation(MainWindow window, string root, ProbeHarness harness,
        Action<bool, string> check)
    {
        // (a) The pair's own configuration changes while its observation is in flight.
        var id = "lh-stale";
        var stalePath = Path.Combine(root, "stale", "gone");
        Load(window, Documented(id, "Stale", Local(stalePath), Cloud("conn-lh-stale", "id:lh-stale")));
        Problems(window).Remove(id);
        harness.BlockAt(stalePath);
        var task = StartPass(window);
        PumpUntil(() => harness.IsBlocked, "the stale observation to be in flight");
        var changedPath = CreateDirectory(root, "stale/changed");
        Load(window, Documented(id, "Stale", Local(changedPath), Cloud("conn-lh-stale", "id:lh-stale")));
        harness.Release();
        AwaitPass(task);
        check(Stored(window, id) is null,
            "LH3: an observation captured for a configuration that changed before it returned is discarded");
        harness.Clear();
        Pass(window);
        check(harness.Observed.SequenceEqual([changedPath]) && Stored(window, id) is null,
            "LH3: the next pass observes the pair's own new configuration");

        // (b) The pair is removed entirely while its observation is in flight.
        var removedId = "lh-removed";
        var removedPath = Path.Combine(root, "removed", "gone");
        Load(window, Documented(removedId, "Removed", Local(removedPath), Cloud("conn-lh-removed", "id:lh-removed")));
        Problems(window).Remove(removedId);
        harness.BlockAt(removedPath);
        task = StartPass(window);
        PumpUntil(() => harness.IsBlocked, "the removed pair's observation to be in flight");
        Load(window, Documented("lh-kept", "Kept", Local(changedPath), Cloud("conn-lh-kept", "id:lh-kept")));
        harness.Release();
        AwaitPass(task);
        check(!Problems(window).ContainsKey(removedId) && !Problems(window).ContainsKey("lh-kept"),
            "LH3: an observation for a pair removed before it returned is ignored rather than applied");
    }

    private static SyncPair Documented(string id, string name, SyncEndpointRecord left, SyncEndpointRecord right) =>
        // The legacy fields stay populated because the pair row's own editor and rewind presentation read
        // them, but this pair's declared document is the only description its health plan may use. The legacy
        // local root below deliberately does not exist, so a check that read it instead would be caught here.
        new(id, name, LegacyUnusedRoot(id), 1, 1, "/Root", "id:" + id,
            EndpointSchemaVersion: SyncEndpointSchema.SymmetricEndpoints, LeftEndpoint: left, RightEndpoint: right);

    private static string LegacyUnusedRoot(string id) =>
        Path.Combine(Path.GetTempPath(), "sync-shark-lh-legacy-unused", id);

    private static SyncEndpointRecord Local(string path) =>
        new(SyncEndpointKind.Local, path, _volumeSerialNumber, _rootFileId);

    private static SyncEndpointRecord Cloud(string connection, string folderId, string? folderPath = null) =>
        new(SyncEndpointKind.Dropbox, Provider: SyncProviders.Dropbox, ConnectionId: connection,
            NamespaceId: "ns:" + connection, FolderId: folderId, FolderPath: folderPath);

    private static string MissingSides(params (string Side, string Path)[] sides) =>
        string.Join(" ", sides.Select(side => $"Local folder unavailable on the {side.Side}: {side.Path}.")) +
        " Choose another folder to repair this pair.";

    private static string CreateDirectory(string root, string relative)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Load(MainWindow window, params SyncPair[] pairs)
    {
        window.LoadPairSetForTest(new SyncPairSet(pairs, pairs.Length > 0 ? pairs[0].Id : null));
        Drain();
    }

    private static Task StartPass(MainWindow window) =>
        (Task)Call(window, "RefreshLocalRootHealthAsync", true)!;

    private static void Pass(MainWindow window) => AwaitPass(StartPass(window));

    private static void AwaitPass(Task task)
    {
        var started = Environment.TickCount64;
        while (!task.IsCompleted)
        {
            Drain();
            Thread.Sleep(5);
            if (Environment.TickCount64 - started > 20000)
                throw new InvalidOperationException("Timed out waiting for the local-root health pass to complete");
        }
        task.GetAwaiter().GetResult();
        Drain();
    }

    private static Dictionary<string, string> Problems(MainWindow window) =>
        Field<Dictionary<string, string>>(window, "_localRootProblems");

    private static string? Stored(MainWindow window, string pairId) => Problems(window).GetValueOrDefault(pairId);

    private static void Pump(Task task)
    {
        var started = Environment.TickCount64;
        while (!task.IsCompleted)
        {
            Drain();
            Thread.Sleep(1);
            if (Environment.TickCount64 - started > 20000)
                throw new InvalidOperationException("Timed out waiting for a workspace task to complete");
        }
        task.GetAwaiter().GetResult();
    }

    private static void PumpUntil(Func<bool> condition, string label)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            Drain();
            Thread.Sleep(5);
            if (Environment.TickCount64 - started > 20000)
                throw new InvalidOperationException("Timed out waiting for " + label);
        }
    }

    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static T Field<T>(object target, string name) => (T)target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(target)!;

    private static object? Call(object target, string name, params object?[] args) => target.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    // The one substituted input: every probe delegates to the real production ObserveLocalRoot drive-attribute
    // check, and a value is forced only where an offline or inaccessible source cannot be constructed safely.
    private sealed class ProbeHarness
    {
        private readonly List<string> _observed = [];
        private readonly Dictionary<string, bool?> _forced = new(StringComparer.Ordinal);
        private readonly ManualResetEventSlim _gate = new(false);
        private volatile string? _blockAt;
        private volatile bool _blocked;

        internal IReadOnlyList<string> Observed
        {
            get { lock (_observed) return _observed.ToArray(); }
        }

        internal bool IsBlocked => _blocked;

        internal void Clear()
        {
            lock (_observed) _observed.Clear();
            _blocked = false;
        }

        internal void Force(string path, bool? value) => _forced[path] = value;

        internal void Unforce() => _forced.Clear();

        internal void BlockAt(string path)
        {
            _gate.Reset();
            _blocked = false;
            _blockAt = path;
        }

        internal void Release()
        {
            _blockAt = null;
            _gate.Set();
        }

        internal bool? Observe(string path)
        {
            lock (_observed) _observed.Add(path);
            if (_blockAt is { } blocked && string.Equals(blocked, path, StringComparison.Ordinal))
            {
                _blocked = true;
                _gate.Wait(TimeSpan.FromSeconds(30));
            }
            return _forced.TryGetValue(path, out var forced) ? forced : MainWindow.ObserveLocalRoot(path);
        }
    }
}

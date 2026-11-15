using System.Diagnostics;

namespace Massdex.SyncShark.V2;

/// <summary>
/// Checks the v2 host's continuous-sync monitor wiring (continuous-sync.md section 3, brief Task E/F) over a real
/// local-only composition: configure starts continuous sync, stop stops it, the projection maps its states, a pause
/// suppresses runs through a Kick, and a system wake kicks every pair.
/// </summary>
internal static class MonitorWiringChecks
{
    private const string Connection = "connection-fixture-3";
    private static readonly TimeSpan SettleBound = TimeSpan.FromSeconds(2);

    public static async Task RunAsync(string root)
    {
        var clock = Stopwatch.StartNew();
        await Configure_OneMonitoringPerEnabledPairAsync(root);
        await Stop_StopsAndConfigureRestartsAsync(root);
        await Pause_KickKeepsItPausedNoRunAsync(root);
        await Wake_KicksAutoSyncAsync(root);
        await AccountKick_OnlyWakesThatAccountsPairsAsync(root);
        AccountExpiry_ReportsOnlyLapsedDeadlines();
        Projection_MapsEveryStatus();
        Console.WriteLine($"v2 monitor wiring checks passed in {clock.ElapsedMilliseconds} ms.");
    }

    // Configure answers one "Monitoring" result per enabled pair and starts continuous sync for it; a second
    // configure does not duplicate the pair's loop or its state.
    private static async Task Configure_OneMonitoringPerEnabledPairAsync(string root)
    {
        using var composition = Composition(root, "configure");
        var store = composition.Store;
        store.SavePair(Pair("p1", enabled: true));
        store.SavePair(Pair("p2", enabled: false));
        using var host = Host(composition);

        var pairs = (await host.LoadPairsAsync()).Pairs;
        var results = await host.ConfigureMonitorsAsync(pairs, _ => Task.FromResult(true));
        Require(results.Count == 1 && results[0].PairId == "p1" && results[0].State == "Monitoring",
            "one Monitoring result per enabled pair");
        await Until(() => host.LoadMonitorProjectionAsync().GetAwaiter().GetResult().Any(item => item.PairId == "p1"),
            "continuous sync publishes the enabled pair's state");
        var again = await host.ConfigureMonitorsAsync(pairs, _ => Task.FromResult(true));
        Require(again.Count == 1, "a second configure still answers one result per enabled pair");
        var projection = await host.LoadMonitorProjectionAsync();
        Require(projection.Count(item => item.PairId == "p1") == 1 &&
                projection.Single(item => item.PairId == "p2").State == "Off",
            "the pair is started once and a disabled pair reports Off");
        Pass("Configure_OneMonitoringPerEnabledPair");
    }

    // Stop stops continuous sync and its projection; a later configure restarts it.
    private static async Task Stop_StopsAndConfigureRestartsAsync(string root)
    {
        using var composition = Composition(root, "stop");
        composition.Store.SavePair(Pair("p1", enabled: true));
        using var host = Host(composition);

        var pairs = (await host.LoadPairsAsync()).Pairs;
        await host.ConfigureMonitorsAsync(pairs, _ => Task.FromResult(true));
        await Until(() => host.LoadMonitorProjectionAsync().GetAwaiter().GetResult().Count != 0,
            "continuous sync reports state while configured");
        await host.StopMonitorsAsync();
        Require((await host.LoadMonitorProjectionAsync()).Count == 0, "stop stops: the projection is empty");
        await host.ConfigureMonitorsAsync(pairs, _ => Task.FromResult(true));
        await Until(() => host.LoadMonitorProjectionAsync().GetAwaiter().GetResult().Count != 0,
            "a later configure restarts continuous sync");
        Pass("Stop_StopsAndConfigureRestarts");
    }

    // A paused pair never runs: it reports Paused, and a Kick (pause, resume or network) keeps it paused rather
    // than starting an automatic run.
    private static async Task Pause_KickKeepsItPausedNoRunAsync(string root)
    {
        using var composition = Composition(root, "pause");
        composition.Store.SavePair(Pair("p1", enabled: true));
        composition.Store.MarkFirstSyncDone("p1"); // otherwise the pair is held on its first sync, not paused
        using var host = Host(composition, pause: () => new UiPreferences("System", PauseUntilResumed: true));

        var pairs = (await host.LoadPairsAsync()).Pairs;
        await host.ConfigureMonitorsAsync(pairs, _ => Task.FromResult(true));
        await Until(() => Projected(host, "p1") == "Paused", "a paused pair reports Paused and never runs");
        host.KickAutoSync(null);
        host.KickAutoSync("p1");
        await Task.Yield();
        Require(Projected(host, "p1") == "Paused", "a Kick keeps a paused pair paused (no automatic run)");
        Pass("Pause_KickKeepsItPausedNoRun");
    }

    // Power resume and a returning network are an injectable wake source; a wake kicks every pair, and the host
    // detaches it on dispose.
    private static async Task Wake_KicksAutoSyncAsync(string root)
    {
        using var composition = Composition(root, "wake");
        using var host = Host(composition);
        using var wake = new FakeWakeSource();
        host.WatchSystemWakes(wake);

        // No pair yet: continuous sync is running but has nothing to do, so the projection is empty.
        await host.ConfigureMonitorsAsync([], _ => Task.FromResult(true));
        Require((await host.LoadMonitorProjectionAsync()).Count == 0, "no pair is monitored before a wake");

        // A pair saved after configure is picked up only by a Kick; a wake is that Kick.
        composition.Store.SavePair(Pair("p1", enabled: true));
        wake.Raise();
        await Until(() => host.LoadMonitorProjectionAsync().GetAwaiter().GetResult().Any(item => item.PairId == "p1"),
            "a system wake kicks continuous sync, which picks up the new pair");
        Pass("Wake_KicksAutoSync");
    }

    // An account-scoped Kick re-evaluates only that account's pairs. A lapsed account pause leaves the pair's loop
    // parked in Paused (a deadline sends no signal), so detection must kick that exact account: a Kick for another
    // account must not wake it, and the matching one must.
    private static async Task AccountKick_OnlyWakesThatAccountsPairsAsync(string root)
    {
        using var composition = Composition(root, "account-kick");
        composition.Store.SavePair(Pair("p1", enabled: true, connection: Connection));
        composition.Store.MarkFirstSyncDone("p1");
        var preferences = new UiPreferences("System",
            AccountPauses: [new SyncAccountPause(Connection, true, null, "UTC")]);
        using var host = Host(composition, pause: () => preferences);

        var pairs = (await host.LoadPairsAsync()).Pairs;
        await host.ConfigureMonitorsAsync(pairs, _ => Task.FromResult(true));
        await Until(() => Projected(host, "p1") == "Paused", "the account-paused pair reports Paused");

        // The deadline lapses; nothing but a Kick can move the loop, so it stays Paused until one arrives.
        preferences = new UiPreferences("System");
        await Task.Yield();
        Require(Projected(host, "p1") == "Paused", "a lapsed account pause leaves the pair parked until a Kick");
        host.KickAutoSyncForAccount("connection-fixture-5");
        await Task.Yield();
        Require(Projected(host, "p1") == "Paused", "a Kick for another account does not wake this account's pair");
        host.KickAutoSyncForAccount(Connection);
        await Until(() => Projected(host, "p1") != "Paused", "a Kick for the account wakes its pair");
        Pass("AccountKick_OnlyWakesThatAccountsPairs");
    }

    // The core detection that continuous sync's check uses: only an account pause whose deadline has passed is
    // expired; a live deadline and an indefinitely paused account are never.
    private static void AccountExpiry_ReportsOnlyLapsedDeadlines()
    {
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        var lapsed = "connection-" + new string('c', 24);
        var live = "connection-" + new string('d', 24);
        var indefinite = "connection-" + new string('e', 24);
        var preferences = new UiPreferences("System", AccountPauses:
        [
            new SyncAccountPause(lapsed, false, now.AddMinutes(-1), "UTC"),
            new SyncAccountPause(live, false, now.AddMinutes(1), "UTC"),
            new SyncAccountPause(indefinite, true, null, "UTC")
        ]);
        var expired = PauseStateProjection.ExpiredAccountPauses(preferences, now);
        Require(expired.Count == 1 && expired[0].ConnectionId == lapsed,
            "only an account pause whose deadline passed is expired");
        Require(PauseStateProjection.ExpiredAccountPauses(new UiPreferences("System"), now).Count == 0,
            "an account with no pause has nothing expired");
        Pass("AccountExpiry_ReportsOnlyLapsedDeadlines");
    }

    // The projection wording is one value per status; a held pair states its own reason.
    private static void Projection_MapsEveryStatus()
    {
        Require(Text(AutoStatus.Idle) == "Watching for changes", "idle maps to its one wording");
        Require(Text(AutoStatus.Waiting) == "Waiting for changes to settle", "waiting maps to its one wording");
        Require(Text(AutoStatus.Running) == "Syncing", "running maps to its one wording");
        Require(Text(AutoStatus.Paused) == "Paused", "paused maps to its one wording");
        Require(Text(AutoStatus.Off) == "Off", "off maps to its one wording");
        Require(MonitorMapping.StateText(new PairAutoState("p", AutoStatus.Held,
                new NoticeReason("watch-unavailable", "a synced folder is not available"), null, null))
            == "a synced folder is not available", "a held pair states its own reason detail");
        Require(MonitorMapping.StateText(new PairAutoState("p", AutoStatus.Held, null, null, null)) == "Needs attention",
            "a held pair with no reason falls back to the generic wording");
        Require(MonitorMapping.ToProjection(new PairAutoState("p", AutoStatus.Running, null, null, null),
            DateTimeOffset.UnixEpoch).PairId == "p", "a projection carries the pair id");
        Pass("Projection_MapsEveryStatus");
    }

    private static string Text(AutoStatus status) =>
        MonitorMapping.StateText(new PairAutoState("p", status, null, null, null));

    private static string? Projected(V2SyncHost host, string pairId) =>
        host.LoadMonitorProjectionAsync().GetAwaiter().GetResult().FirstOrDefault(item => item.PairId == pairId)?.State;

    private static EngineComposition Composition(string root, string name)
    {
        var productRoot = Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(productRoot);
        return EngineComposition.Open(
            new EngineHostInputs(productRoot, null, new HttpClient(new FailingHandler()),
                LongpollHandler: new FailingHandler()), V2Settings.Default, TimeProvider.System);
    }

    private static V2SyncHost Host(EngineComposition composition, Func<UiPreferences>? pause = null) =>
        new(composition.Store, composition.Run, composition.Observed, v1: null!, composition.Settings,
            TimeProvider.System, composition.DatabasePath) { Composition = composition, PauseSource = pause };

    private static PairDef Pair(string id, bool enabled, string? connection = null) => new(id, id,
        new RootDef("r:local:" + id, SyncProviders.Local, null, @"C:\root\" + id, "id:local:0a1b2c3d:deadbeefcafebabe"),
        new RootDef("r:dropbox:" + id, SyncProviders.Dropbox, connection ?? Connection, "/root/" + id, "id:dropbox:" + id),
        [], [], enabled);

    private static async Task Until(Func<bool> condition, string label)
    {
        var bound = Stopwatch.StartNew();
        while (!condition())
        {
            if (bound.Elapsed > SettleBound) throw new InvalidOperationException("Timed out: " + label);
            await Task.Yield();
        }
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }

    private static void Pass(string name) => Console.WriteLine("PASS " + name);

    private sealed class FakeWakeSource : ISystemWakeSource
    {
        public event Action? Woke;

        public void Dispose() { }

        public void Raise() => Woke?.Invoke();
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("No HTTP request is allowed in the monitor wiring checks: " +
                request.RequestUri);
    }
}

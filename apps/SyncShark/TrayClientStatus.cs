using System.Globalization;
using System.Text.Json;
using Massdex.SyncShark.V2;
using static Massdex.SyncShark.TrayJson;

namespace Massdex.SyncShark;

/// <summary>One pair exactly as the host status reply reported it. Read-only and never inferred.</summary>
internal sealed record TrayPairSnapshot(string PairId, string Name, string State, string? HeldDetail,
    string? LastResultLine, long UrgentPending, long PlanningBacklog);

/// <summary>The host status reply in the tray's own terms. It carries only values the host actually sent.</summary>
internal sealed record TrayStatusSnapshot(bool Stopping, bool AutomationEnabled, int IntervalMinutes,
    int? OpenConflicts, int HostPid, double UptimeSeconds, IReadOnlyList<TrayPairSnapshot> Pairs);

/// <summary>One pair row of the menu. Its wording is derived once so the icon and menu cannot disagree.</summary>
internal sealed record TrayPairView(string PairId, string Name, string StateLine, string LastResultLine,
    long UrgentPending, long PlanningBacklog)
{
    internal string WaitingLine => "Work waiting: " + UrgentPending.ToString(CultureInfo.InvariantCulture) +
        " (planning backlog " + PlanningBacklog.ToString(CultureInfo.InvariantCulture) + ")";
}

/// <summary>
/// The whole tray surface as words and one icon state. Derivation never invents a value: a missing read keeps
/// the last known view and says "Updating…", and a host that has not answered yet says "starting", never "not running".
/// </summary>
internal sealed record TrayStatusView(string IconState, string Status, string HostLine,
    IReadOnlyList<TrayPairView> Pairs, int? OpenConflicts, long UrgentPending, long PlanningBacklog, bool Reachable,
    bool CanAct)
{
    internal static TrayStatusView Starting { get; } = new("starting", "Starting / not yet reachable",
        "Background sync: starting / not yet reachable", [], null, 0, 0, false, false);

    internal static TrayStatusView Derive(TrayStatusSnapshot? snapshot, bool updating, bool alternate)
    {
        if (snapshot is null) return Starting;
        var paused = !snapshot.AutomationEnabled || snapshot.Pairs.Any(pair => Is(pair.State, "Paused"));
        var working = snapshot.Pairs.Any(pair => Is(pair.State, "Running") || Is(pair.State, "Waiting"));
        var allStarting = snapshot.Pairs.Count > 0 && snapshot.Pairs.All(pair => Is(pair.State, "starting"));
        var icon = snapshot.Stopping ? "starting"
            : snapshot.OpenConflicts > 0 ? "attention"
            : paused ? "paused"
            : working ? alternate ? "working-light" : "working"
            : allStarting ? "starting" : "idle";
        var status = snapshot.Stopping ? "Stopping"
            : snapshot.OpenConflicts > 0 ? "Needs attention: " + ConflictCountWord(snapshot.OpenConflicts.GetValueOrDefault())
            : paused ? "Paused"
            : working ? "Working"
            : allStarting ? "Starting" : "Idle";
        if (updating) status += " (Updating…)";
        var host = snapshot.Stopping ? "Background sync: stopping"
            : "Background sync: running (PID " + snapshot.HostPid.ToString(CultureInfo.InvariantCulture) +
              ", up " + FormatUptime(snapshot.UptimeSeconds) + ")" +
              (snapshot.AutomationEnabled ? " · every " + snapshot.IntervalMinutes.ToString(CultureInfo.InvariantCulture) + " min"
                  : " · automatic sync off");
        if (updating) host = "Updating… — last known: " + host;
        var pairs = snapshot.Pairs.Select(pair => new TrayPairView(pair.PairId, pair.Name,
            pair.HeldDetail is { Length: > 0 } detail ? "Held: " + detail : StateWord(pair.State),
            pair.LastResultLine ?? "Last sync: not yet run", pair.UrgentPending, pair.PlanningBacklog)).ToArray();
        return new TrayStatusView(icon, status, host, pairs, snapshot.OpenConflicts,
            pairs.Sum(pair => pair.UrgentPending), pairs.Sum(pair => pair.PlanningBacklog), true,
            !updating && !snapshot.Stopping);
    }

    /// <summary>Reads one host status reply. A non-answer or a failed reply is a miss, never a state.</summary>
    internal static TrayStatusSnapshot? Parse(AutoSyncHostReply? reply, int? previousConflicts = null)
    {
        if (reply is null || !reply.Ok || reply.Data is not { } element ||
            element.ValueKind != JsonValueKind.Object) return null;
        var data = element;
        var pairs = new List<TrayPairSnapshot>();
        if (data.TryGetProperty("pairs", out var array) && array.ValueKind == JsonValueKind.Array)
            foreach (var item in array.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object) pairs.Add(ParsePair(item));
        return new TrayStatusSnapshot(Is(reply.State, "stopping"), Bool(data, "automationEnabled"),
            Int(data, "intervalMinutes"),
            data.TryGetProperty("openConflicts", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var conflicts)
                ? conflicts : previousConflicts, reply.Host.Pid, reply.Host.UptimeSeconds, pairs);
    }

    private static TrayPairSnapshot ParsePair(JsonElement item)
    {
        var state = Text(item, "state") ?? "starting";
        var reason = item.TryGetProperty("reason", out var value) && value.ValueKind == JsonValueKind.Object
            ? Text(value, "detail") : null;
        var lastChecked = Time(item, "lastChecked");
        string? lastResult = null;
        if (item.TryGetProperty("lastResult", out var run) && run.ValueKind == JsonValueKind.Object)
            lastResult = DescribeRun(run, lastChecked);
        return new TrayPairSnapshot(Text(item, "pairId") ?? string.Empty, Text(item, "name") ?? string.Empty,
            state, Is(state, "Held") ? reason : null, lastResult,
            Long(item, "urgentPending"), Long(item, "planningBacklog"));
    }

    // The outcome word, the counts that are actually nonzero, and the last checked instant, in that order.
    private static string DescribeRun(JsonElement run, DateTimeOffset? lastChecked)
    {
        var counts = new List<string>();
        void Add(string label, string name)
        {
            var value = Long(run, name);
            if (value != 0) counts.Add(value.ToString(CultureInfo.InvariantCulture) + " " + label);
        }
        Add("done", "done"); Add("uploaded", "uploaded"); Add("downloaded", "downloaded"); Add("deleted", "deleted");
        Add("moved", "moved"); Add("kept both", "keptBoth"); Add("held", "held"); Add("failed", "failed");
        Add("uncertain", "uncertain"); Add("skipped", "skipped"); Add("deferred", "deferred");
        var when = lastChecked ?? Time(run, "completedUtc");
        return "Last sync: " + OutcomeWord(Text(run, "outcome") ?? string.Empty) + " — " +
            (counts.Count == 0 ? "no changes" : string.Join(", ", counts)) +
            (when is { } instant ? " — " + FormatTime(instant) : string.Empty);
    }

    private static string StateWord(string state) => state switch
    {
        "Idle" => "Idle", "Waiting" => "Waiting", "Running" => "Running", "Paused" => "Paused",
        "Off" => "Off", "Held" => "Held", "starting" => "Starting", _ => state
    };

    private static string OutcomeWord(string outcome) => outcome switch
    {
        "complete" => "Complete", "partial" => "Partial", "failed" => "Failed", "stopped" => "Stopped",
        "paused" => "Paused", "plan_bound_exceeded" => "Plan bound exceeded", "state_damaged" => "State damaged",
        "" => "Unknown", _ => char.ToUpperInvariant(outcome[0]) + outcome[1..]
    };

    private static string FormatUptime(double seconds) => seconds switch
    {
        < 60 => ((long)seconds).ToString(CultureInfo.InvariantCulture) + "s",
        < 3600 => ((long)seconds / 60).ToString(CultureInfo.InvariantCulture) + "m",
        _ => ((long)seconds / 3600).ToString(CultureInfo.InvariantCulture) + "h " +
             ((long)seconds % 3600 / 60).ToString(CultureInfo.InvariantCulture) + "m"
    };

    private static string FormatTime(DateTimeOffset instant) =>
        instant.ToLocalTime().ToString("ddd d MMM yyyy HH:mm", CultureInfo.InvariantCulture);

    private static string Count(int value, string label) => value == 1
        ? "1 " + label : value.ToString(CultureInfo.InvariantCulture) + " " + label + "s";

    // The shared count is capped, and past 99 the exact number is noise; the menu and tooltip say 99+.
    private static bool ConflictCountCapped(int value) => value > 99;

    internal static string ConflictCountShort(int value) =>
        ConflictCountCapped(value) ? "99+" : value.ToString(CultureInfo.InvariantCulture);

    private static string ConflictCountWord(int value) => ConflictCountCapped(value)
        ? ConflictCountShort(value) + " open conflicts" : Count(value, "open conflict");

    private static bool Is(string? value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}

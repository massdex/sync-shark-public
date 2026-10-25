using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using Massdex.SyncShark;
using Massdex.SyncShark.V2;

namespace SyncShark.UiFixture;

/// <summary>
/// Focused checks for the pure background tray client: the derived view from canned host replies, and the
/// fact that tray mode uses its own owner and never takes the product lock.
/// </summary>
internal static class TrayClientChecks
{
    internal static int Run()
    {
        DeriveViewFromCannedReplies();
        TrayNeverTakesTheProductLock();
        Console.WriteLine("PASS tray client: derived view from canned replies and no product-lock acquisition.");
        return 0;
    }

    private static void DeriveViewFromCannedReplies()
    {
        // Running: the outcome word, its nonzero counts and the last checked instant are all rendered.
        var running = TrayStatusView.Derive(TrayStatusView.Parse(Reply("running",
            Pair("pair-a", "Pair A", "Running", lastResult: true, urgent: 2, planning: 1))), updating: false, alternate: false);
        Require(running.Reachable && running.IconState == "working" && running.Status == "Working" && running.CanAct,
            "a running pair shows working and offers its actions");
        Require(running.Pairs.Count == 1 && running.Pairs[0].StateLine == "Running" &&
                running.Pairs[0].LastResultLine.Contains("Complete", StringComparison.Ordinal) &&
                running.Pairs[0].LastResultLine.Contains("3 done", StringComparison.Ordinal) &&
                running.Pairs[0].LastResultLine.Contains("1 uploaded", StringComparison.Ordinal) &&
                running.Pairs[0].LastResultLine.Contains("2026", StringComparison.Ordinal),
            "the last finished result keeps its outcome, counts and last checked time");
        Require(running.UrgentPending == 2 && running.PlanningBacklog == 1 &&
                running.Pairs[0].WaitingLine == "Work waiting: 2 (planning backlog 1)",
            "work waiting keeps both internal backlog counts");

        // Held: the host's own reason is shown, never invented.
        var held = TrayStatusView.Derive(TrayStatusView.Parse(Reply("running",
            Pair("pair-a", "Pair A", "Held", reason: "The paired folder is unavailable."))), updating: false, alternate: false);
        Require(held.Pairs[0].StateLine == "Held: The paired folder is unavailable." && held.IconState == "idle",
            "a held pair carries the host reason and does not claim the conflict colour");

        // Paused: automation off is a pause, and it says so.
        var paused = TrayStatusView.Derive(TrayStatusView.Parse(Reply("running",
            Pair("pair-a", "Pair A", "Paused"), automation: false)), updating: false, alternate: false);
        Require(paused.IconState == "paused" && paused.Status == "Paused" && paused.CanAct, "paused automation shows paused");

        // Starting: the pair fallback is starting, and an unanswered host is starting / not yet reachable.
        var starting = TrayStatusView.Derive(TrayStatusView.Parse(Reply("running",
            Pair("pair-a", "Pair A", "starting"))), updating: false, alternate: false);
        Require(starting.IconState == "starting" && starting.Status == "Starting", "a pair with no published state shows starting");
        var never = TrayStatusView.Derive(null, updating: false, alternate: false);
        Require(never.IconState == "starting" && never.Status == "Starting / not yet reachable" && !never.CanAct &&
                !never.Status.Contains("not running", StringComparison.OrdinalIgnoreCase),
            "an unanswered host says starting / not yet reachable, never not running, and offers no action");

        // A miss keeps the last known state and says Updating…; the icon never falls back to starting.
        var updating = TrayStatusView.Derive(TrayStatusView.Parse(Reply("running",
            Pair("pair-a", "Pair A", "Running", lastResult: true))), updating: true, alternate: false);
        Require(updating.IconState == "working" && updating.Status == "Working (Updating…)" && !updating.CanAct &&
                updating.HostLine.StartsWith("Updating…", StringComparison.Ordinal),
            "a status miss keeps the last known view, says Updating… and offers no action");

        // Conflicts: the host count alone turns the icon to needs-attention.
        var conflicts = TrayStatusView.Derive(TrayStatusView.Parse(Reply("running",
            Pair("pair-a", "Pair A", "Idle"), conflicts: 2)), updating: false, alternate: false);
        Require(conflicts.IconState == "attention" && conflicts.OpenConflicts == 2 &&
                conflicts.Status == "Needs attention: 2 open conflicts",
            "an open-conflict count above zero turns the icon to needs attention");
    }

    private static void TrayNeverTakesTheProductLock()
    {
        var root = Path.Combine(Path.GetTempPath(), "tray-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var productRoot = SyncSharkRuntimePaths.OverrideProductRootForTest(root);
        // The tray runs with the product lock already held by the application. Acquiring the same
        // lock opens the file with FileShare.None, so an acquisition attempt here would throw.
        using var productLock = PilotInstanceLock.Acquire(SyncSharkRuntimePaths.InstanceLockFile);
        using var trayOwnership = TrayClient.TryTakeOwnership() ??
            throw new InvalidDataException("The tray could not take its own single-instance owner.");
        // A second tray owner on another thread must be refused; mutex ownership is per thread.
        IDisposable? second = null;
        var thread = new Thread(() => second = TrayClient.TryTakeOwnership());
        thread.Start();
        thread.Join();
        if (second is not null) { second.Dispose(); throw new InvalidDataException("A second tray icon was admitted."); }
        using var client = new TrayClient(BackgroundHostEntry.Client(), () => { });
        client.Start();
        Require(client.View.IconState == "starting" && client.View.Status == "Starting / not yet reachable" &&
                !client.View.CanAct,
            "tray mode starts while the product lock is held and never acquires it");
    }

    private static AutoSyncHostReply Reply(string state, string pairs, int conflicts = 0, bool automation = true)
    {
        var data = "{\"running\":true,\"automationEnabled\":" + (automation ? "true" : "false") +
            ",\"intervalMinutes\":15,\"openConflicts\":" + conflicts.ToString(CultureInfo.InvariantCulture) +
            ",\"pairs\":[" + pairs + "]}";
        using var document = JsonDocument.Parse(data);
        return new AutoSyncHostReply(AutoSyncHostProtocol.Version, Guid.NewGuid().ToString("N"),
            new AutoSyncHostIdentity("tray-fixture", 4321, DateTimeOffset.UtcNow.AddMinutes(-5)),
            state, true, document.RootElement.Clone(), null);
    }

    private static string Pair(string id, string name, string state, string? reason = null, bool lastResult = false,
        long urgent = 0, long planning = 0)
    {
        var reasonJson = reason is null ? "null"
            : "{\"key\":\"fixture\",\"detail\":\"" + reason + "\",\"awaitsAcknowledgement\":false}";
        var result = lastResult
            ? "{\"outcome\":\"complete\",\"done\":3,\"uploaded\":1,\"downloaded\":0,\"deleted\":0,\"moved\":0," +
              "\"keptBoth\":0,\"held\":0,\"failed\":0,\"uncertain\":0,\"skipped\":0,\"deferred\":0," +
              "\"completedUtc\":\"2026-10-25T09:00:00Z\"}"
            : "null";
        return "{\"pairId\":\"" + id + "\",\"name\":\"" + name + "\",\"enabled\":true,\"state\":\"" + state +
            "\",\"lastResult\":" + result + ",\"lastChecked\":\"2026-10-25T09:00:00Z\",\"urgentPending\":" +
            urgent.ToString(CultureInfo.InvariantCulture) + ",\"planningBacklog\":" +
            planning.ToString(CultureInfo.InvariantCulture) + ",\"reason\":" + reasonJson + "}";
    }

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidDataException(description);
        Console.WriteLine("PASS " + description);
    }
}

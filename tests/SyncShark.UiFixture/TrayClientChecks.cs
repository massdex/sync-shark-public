using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using Massdex.SyncShark;
using Massdex.SyncShark.V2;

namespace SyncShark.UiFixture;

/// <summary>
/// Focused checks for the pure background tray client: the derived view from canned host replies, the
/// production startup route with a controlled host, pending/failed actions, and the fact that tray mode
/// uses its own owner and never takes the product lock.
/// </summary>
internal static class TrayClientChecks
{
    internal static int Run()
    {
        DeriveViewFromCannedReplies();
        CappedConflictTotalShows99Plus();
        TrayNeverTakesTheProductLock();
        ProductionStartupRoute();
        FailedStatusPollKeepsLastKnownView();
        ExitStopsOnlyTheTray();
        Console.WriteLine("PASS tray client: bounded view, production startup route, single-send actions, " +
            "Updating… on every failed poll, and no product-lock acquisition.");
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

    // The shared store count is capped, and past 99 the menu says 99+ rather than inventing a number.
    private static void CappedConflictTotalShows99Plus()
    {
        using var client = new TrayClient((_, _, _) =>
            Task.FromResult<AutoSyncHostReply?>(Reply("running", Pair("pair-a", "Pair A", "Idle"), conflicts: 1000)),
            () => { });
        WaitFor(client.RefreshNowAsync());
        Require(client.ConflictStatusText.Contains("99+", StringComparison.Ordinal) &&
                !client.ConflictStatusText.Contains("1000", StringComparison.Ordinal) &&
                client.View.Status.Contains("99+", StringComparison.Ordinal) &&
                !client.View.Status.Contains("1000", StringComparison.Ordinal),
            "a capped conflict count renders 99+ instead of a fabricated exact number");
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
        using var client = new TrayClient(BackgroundHostEntry.Client().TrySendAsync, () => { });
        client.Start();
        Require(client.View.IconState == "starting" && client.View.Status == "Starting / not yet reachable" &&
                !client.View.CanAct,
            "tray mode starts while the product lock is held and never acquires it");
    }

    // The production startup decision (the branch App.OnStartup calls) with the product lock held by the
    // test, an isolated root and a controlled host: the first reply is real, the next is a miss.
    private static void ProductionStartupRoute()
    {
        var root = Path.Combine(Path.GetTempPath(), "tray-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var productRoot = SyncSharkRuntimePaths.OverrideProductRootForTest(root);
        using var productLock = PilotInstanceLock.Acquire(SyncSharkRuntimePaths.InstanceLockFile);
        // The same first two startup decisions OnStartup makes, before any lock or WPF type is touched.
        var (selectedV2, trayArgs, coreError) = CoreSwitch.Select(["--core", "v2", "--tray"], null);
        Require(coreError is null && selectedV2 && TrayStartup.IsTrayRequest(trayArgs),
            "the startup branch selects core v2 and recognises --tray before the product lock");
        var diagnostics = new List<string>();
        var statusCalls = 0;
        var mutationSends = 0;
        var exited = false;
        // Inline continuations keep the menu-item updates on this (fixture) thread.
        var actionGate = new TaskCompletionSource<AutoSyncHostReply?>();
        TrayHostSend send = (request, _, token) =>
        {
            if (request.Command != "status")
            {
                Interlocked.Increment(ref mutationSends);
                return actionGate.Task;
            }
            // The first two polls answer immediately; the third stays in flight at the 2 s cap and can
            // only complete as a miss when the tray's own deadline cancels it.
            return ++statusCalls == 3
                ? DelayedStatusAsync(token)
                : Task.FromResult<AutoSyncHostReply?>(Reply("running", Pair("pair-a", "Pair A", "Running")));
        };

        var result = TrayStartup.Start(selectedV2, trayArgs, send, () => exited = true, diagnostics.Add);
        Require(result.ExitCode is null && result.Ownership is not null && result.Client is not null,
            "the production startup branch starts one tray client while the product lock is held");
        using var ownership = result.Ownership;
        using var client = result.Client!;
        client.StopPollingForTest();
        WaitFor(client.InitialPoll);
        Require(client.View.Status == "Working" && client.View.CanAct,
            "the first status reply is applied through the production startup route");

        // One activation forwards exactly one command; a repeat while it is outstanding does nothing, and its
        // own menu item is disabled immediately although the view otherwise offers actions.
        var pause = TrayClient.PauseRequest(PauseOption.All[0]);
        var key = TrayClient.MutationKey(pause);
        var first = client.SendActionForTestAsync(pause, "Pause");
        var repeated = client.SendActionForTestAsync(pause, "Pause");
        Require(mutationSends == 1 && client.View.CanAct && client.IsActionPending(key) &&
                !client.ActionEnabledForTest(key),
            "one activation sends once and disables that action while it is outstanding");
        // A forwarded CLI result keeps its envelope: Ok with a nonzero exit code and stderr.
        actionGate.SetResult(CliResult(exitCode: 4, stderr: "Pause was refused: the run is at a safe point."));
        WaitFor(first);
        WaitFor(repeated);
        Require(mutationSends == 1 && !client.IsActionPending(key) && client.ActionEnabledForTest(key),
            "the outstanding action clears after its single reply and the item is usable again");
        Require(client.LastNotification is { } notice &&
                notice.Title.Contains("rejected", StringComparison.Ordinal) &&
                notice.Message.Contains("Pause was refused: the run is at a safe point.", StringComparison.Ordinal),
            "a nonzero forwarded CLI exit shows the host's stderr diagnostic, distinct from uncertainty");

        // A reply still in flight at the 2 s cap leaves the poll pending, then applies as a miss.
        var delayed = client.RefreshNowAsync();
        PumpFor(TimeSpan.FromMilliseconds(750));
        Require(!delayed.IsCompleted && client.View.Status == "Working" && client.View.CanAct,
            "a status reply still in flight is pending, not a miss, until the 2 s cap");
        WaitFor(delayed);
        Require(client.View.Status.EndsWith("(Updating…)", StringComparison.Ordinal) && !client.View.CanAct,
            "a status reply that completes past the 2 s cap is a miss: last known view plus Updating…");

        client.ExitTrayForTest();
        Require(exited, "the tray exit action disposes only the client");
    }

    // A reply that is deliberately slower than the tray's own two-second status cap. It honours the cap's
    // token, so the poll is still pending at the cap and then records a miss rather than a late success.
    private static async Task<AutoSyncHostReply?> DelayedStatusAsync(CancellationToken token)
    {
        await Task.Delay(TimeSpan.FromSeconds(2) + TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
        return Reply("running", Pair("pair-a", "Pair A", "Running"));
    }

    // Every failed observation — I/O, peer verification, parse, uncertain, cancel, unexpected — keeps the last
    // known view and says Updating… with actions disabled; only an unexpected failure is logged.
    private static void FailedStatusPollKeepsLastKnownView()
    {
        var calls = 0;
        var diagnostics = new List<string>();
        Exception[] failures =
        [
            new IOException("the pipe closed"),
            new UnauthorizedAccessException("peer verification failed"),
            new InvalidDataException("malformed reply"),
            new OperationCanceledException("status deadline cancelled"),
            new AutoSyncHostUncertainException("fixture", new TimeoutException("slow reply")),
            new InvalidOperationException("unexpected provider failure")
        ];
        TrayHostSend send = (request, _, _) =>
        {
            if (request.Command != "status") return Task.FromResult<AutoSyncHostReply?>(OkReply());
            var call = calls++;
            return Task.FromResult<AutoSyncHostReply?>(call == 0
                ? Reply("running", Pair("pair-a", "Pair A", "Running"))
                : throw failures[call - 1]);
        };
        using var client = new TrayClient(send, () => { }, diagnostics.Add);
        WaitFor(client.RefreshNowAsync());
        Require(client.View.Status == "Working", "a successful status read sets the view");
        foreach (var failure in failures)
        {
            WaitFor(client.RefreshNowAsync());
            Require(client.View.Status.EndsWith("(Updating…)", StringComparison.Ordinal) && !client.View.CanAct,
                "a failed status poll (" + failure.GetType().Name + ") keeps the last-known plus Updating… view");
        }
        Require(diagnostics.Count == 1 &&
                diagnostics[0].Contains("tray status read failed", StringComparison.Ordinal),
            "only the unexpected status failure is logged through the existing diagnostic path, never an empty catch");
    }

    // Exiting the tray never sends the host's stop command and leaves the host transport answering.
    private static void ExitStopsOnlyTheTray()
    {
        var stopped = 0;
        var exited = false;
        TrayHostSend send = (request, _, _) =>
        {
            if (request.Command == "stop") Interlocked.Increment(ref stopped);
            return Task.FromResult<AutoSyncHostReply?>(OkReply());
        };
        var client = new TrayClient(send, () => exited = true);
        client.ExitTrayForTest();
        client.Dispose();
        Require(exited && stopped == 0, "the tray's Exit sends no host stop command and stops only the client");
        Require(WaitForResult(send(AutoSyncHostClient.Request("status"), TimeSpan.FromMilliseconds(300),
                CancellationToken.None)) is not null,
            "the background host keeps answering after the tray exits");
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

    private static AutoSyncHostReply OkReply()
    {
        using var document = JsonDocument.Parse("{\"queued\":true}");
        return new AutoSyncHostReply(AutoSyncHostProtocol.Version, Guid.NewGuid().ToString("N"),
            new AutoSyncHostIdentity("tray-fixture", 4321, DateTimeOffset.UtcNow.AddMinutes(-5)),
            "running", true, document.RootElement.Clone(), null);
    }

    private static AutoSyncHostReply CliResult(int exitCode, string stderr)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { exitCode, stdout = "", stderr }));
        return new AutoSyncHostReply(AutoSyncHostProtocol.Version, Guid.NewGuid().ToString("N"),
            new AutoSyncHostIdentity("tray-fixture", 4321, DateTimeOffset.UtcNow.AddMinutes(-5)),
            "running", true, document.RootElement.Clone(), null);
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

    // Pump this thread's queues for a bounded instant without waiting for completion, so the fixture can
    // observe that an in-flight poll is still pending at a chosen point before its deadline.
    private static void PumpFor(TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < until)
        {
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(1);
        }
    }

    // Pump this thread's WinForms and WPF queues while a task completes, so a continuation posted to a
    // synchronization context installed by the tray's own components can never deadlock the blocking fixture.
    private static void WaitFor(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The tray task did not complete within the fixture deadline.");
            // WinForms components install their own message-based context; pump those messages. A WPF
            // dispatcher context needs a dispatcher frame instead (never pumped here, so the tray's own
            // DispatcherTimer cannot fire during a scripted check).
            System.Windows.Forms.Application.DoEvents();
            if (SynchronizationContext.Current is System.Windows.Threading.DispatcherSynchronizationContext)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background, new Action(() => frame.Continue = false));
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }
            Thread.Sleep(1);
        }
        task.GetAwaiter().GetResult();
    }

    private static T WaitForResult<T>(Task<T> task)
    {
        WaitFor((Task)task);
        return task.GetAwaiter().GetResult();
    }

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidDataException(description);
        Console.WriteLine("PASS " + description);
    }
}

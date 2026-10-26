using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// monitor-failure correction (Claude H5 + H6): real pump, cursor and journal success and
// rejection proofs for the two existing authority monitors over the real isolated SQLite monitor
// journal. The native FileSystemWatcher and the in-process provider fixture only supply input; every
// durable decision is made by the production monitor against monitor_cursor/monitor_event/dirty_item.
// Nothing here touches a live account, credential, provider network, installed database, product
// state, UI or pair root. This file is callable source: the owner registers it (see the exact
// Program.cs snippet in the batch result) and runs it under the granted Core.Tests phase.
internal static class MonitorFailureCorrectionChecks
{
    private static readonly List<Dictionary<string, object?>> Cases = [];
    private static int _assertions;
    private static string _root = "";

    internal static async Task RunAsync(string root)
    {
        _root = Path.GetFullPath(root);
        if (Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any())
            throw new InvalidOperationException("Use a fresh isolated monitor-failure fixture root.");
        Directory.CreateDirectory(_root);
        Cases.Clear();
        _assertions = 0;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "The monitor failure-correction proofs require Windows NTFS directory identities and the native watcher.");

        await CaseAsync("A-local-callback-nonblocking-non-writing",
            "A native watcher overflow callback publishes bounded hints, blocks nothing, performs no durable write and throws nothing while the pump is parked",
            LocalCallbackNonBlockingAsync);
        await CaseAsync("B-local-overflow-across-database-failure",
            "Repeated authority commit failure retains durable uncertainty and never advances the cursor; the pair-dirty obligation commits and the cursor advances only with the real journal commit",
            LocalOverflowAcrossDatabaseFailureAsync);
        await CaseAsync("B0-local-concurrent-overflow-ordering",
            "Out-of-order callback sequences retain the newest uncertainty generation in the same production coalescer",
            () =>
            {
                Assert(SyncAuthorityLocalMonitor.SelectOverflowGenerationForTest((9, 2), (7, 3)) == 3,
                    "a later callback sequence cannot replace newer overflow-generation evidence with an older generation");
                return Task.CompletedTask;
            });
        await CaseAsync("C-local-restart-never-trusts-predecessor",
            "A new directory-watcher session durably establishes full-reconciliation uncertainty before StartAsync returns, so a clean predecessor cursor cannot masquerade as continuity",
            LocalRestartUncertaintyAsync);
        await CaseAsync("D-local-disposal-contains-failed-pump",
            "DisposeAsync contains a failed pump, releases its tasks and retains a safe failure reason instead of throwing or reporting success",
            LocalDisposalContainmentAsync);
        await CaseAsync("E-dropbox-reset-then-provider-failure",
            "A cursor reset whose fresh-cursor read fails keeps the old cursor, retains uncertainty and converges on the next attempt with one atomic reset event",
            DropboxResetThenProviderFailureAsync);
        await CaseAsync("F-dropbox-reset-then-database-failure",
            "A cursor reset whose authority commit fails never advances the retained cursor and converges once the reset event and fresh cursor commit together",
            DropboxResetThenDatabaseFailureAsync);
        await CaseAsync("G-dropbox-disposal-contains-failed-pump",
            "A permanently failing Dropbox reset recovery is contained by DisposeAsync with a retained safe failure reason and no fabricated reset event",
            DropboxDisposalContainmentAsync);

        var report = new Dictionary<string, object?>
        {
            ["generated_by"] = "MonitorFailureCorrectionChecks (tests/SyncShark.Core.Tests)",
            ["fixture_root"] = _root,
            ["assertions"] = _assertions,
            ["cases"] = Cases
        };
        await File.WriteAllTextAsync(Path.Combine(_root, "monitor-failure-correction-checks.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS monitor failure correction: " + _assertions +
            " assertions; native watcher callbacks publish nonblocking, non-writing uncertainty, reset recovery and" +
            " persistence failures stay inside the handled pump boundary, and restart/disposal cannot hide them.");
    }

    // ----- A: native callback containment (H6) ------------------------------------------------------

    private static async Task LocalCallbackNonBlockingAsync()
    {
        SyncAuthorityLocalMonitor.EventQueueCapacityForTest = 8;
        SyncAuthorityLocalMonitor.FailureRetryDelayForTest = TimeSpan.FromMilliseconds(5);
        SyncAuthorityLocalMonitor.BeforeCommitForTest = null;
        try
        {
            var fixture = await CreateFixtureAsync("local-callback");
            var monitor = new SyncAuthorityLocalMonitor(fixture.Pair, fixture.Authority,
                fixture.LocalBindingId, _ => Task.CompletedTask);
            await monitor.StartAsync();

            var started = await fixture.Authority.LoadMonitorCursorAsync(monitor.CursorId);
            Assert(started is { Status: "unavailable" },
                "the new session's full-reconciliation uncertainty is durable before any native event is enabled");
            await WaitUntilAsync(
                async () => (await fixture.Authority.LoadMonitorCursorAsync(monitor.CursorId))?.Status == "active",
                TimeSpan.FromSeconds(15), "the session-start sentinel commits and returns the cursor to active");

            var eventsBefore = await CountAsync(fixture.Authority.DatabasePath,
                "SELECT count(*) FROM monitor_event WHERE pair_id=$pair;", ("$pair", fixture.Pair.Id));

            // Park the pump inside its handled iteration, before it can touch the authority. Any
            // durable write observed after this point could only have come from the native callback.
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SyncAuthorityLocalMonitor.BeforeCommitForTest = async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                await parked.Task.WaitAsync(cancellationToken);
            };
            monitor.EnqueueForTest("park-the-pump.txt");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));

            var watch = Stopwatch.StartNew();
            for (var index = 0; index < 20_000; index++) monitor.EnqueueForTest("flood-" + index + ".txt");
            for (var index = 0; index < 50; index++) monitor.EnqueueForTest(null, "overflow");
            watch.Stop();

            Assert(watch.Elapsed < TimeSpan.FromSeconds(5),
                "the callback path published 20,050 hints in bounded nonblocking time (observed " +
                watch.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + "ms)");
            var parkedCursor = await fixture.Authority.LoadMonitorCursorAsync(monitor.CursorId);
            Assert(parkedCursor is { Status: "active" },
                "the callback performed no synchronous durable write while the pump was parked");
            Assert(await CountAsync(fixture.Authority.DatabasePath,
                       "SELECT count(*) FROM monitor_event WHERE pair_id=$pair;", ("$pair", fixture.Pair.Id)) ==
                   eventsBefore,
                "the callback added no durable monitor event of its own");

            parked.TrySetResult();
            SyncAuthorityLocalMonitor.BeforeCommitForTest = null;
            await WaitUntilAsync(async () => await fixture.Authority.HasPendingPairDirtyHintForTestAsync(fixture.Pair.Id),
                TimeSpan.FromSeconds(15),
                "the deferred overflow persistence commits the pair-dirty obligation");
            await monitor.DisposeAsync();
        }
        finally
        {
            SyncAuthorityLocalMonitor.BeforeCommitForTest = null;
            SyncAuthorityLocalMonitor.EventQueueCapacityForTest = null;
            SyncAuthorityLocalMonitor.FailureRetryDelayForTest = null;
        }
    }

    // ----- B: overflow across a failing authority journal (H6) ---------------------------------------

    private static async Task LocalOverflowAcrossDatabaseFailureAsync()
    {
        SyncAuthorityLocalMonitor.EventQueueCapacityForTest = 4;
        SyncAuthorityLocalMonitor.FailureRetryDelayForTest = TimeSpan.FromMilliseconds(5);
        try
        {
            var fixture = await CreateFixtureAsync("local-overflow-failure");
            var observations = new List<MonitorObservation>();
            var gate = new object();
            var attempts = 0;
            SyncAuthorityLocalMonitor? monitor = null;
            SyncAuthorityLocalMonitor.BeforeCommitForTest = async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref attempts) > 3) return;
                var cursor = await fixture.Authority.LoadMonitorCursorAsync(monitor!.CursorId, cancellationToken);
                lock (gate)
                    observations.Add(new MonitorObservation(cursor?.Status, cursor?.CursorValue,
                        monitor!.LastFailureReason));
                throw new IOException("simulated authority failure publishing monitor uncertainty");
            };
            monitor = new SyncAuthorityLocalMonitor(fixture.Pair, fixture.Authority, fixture.LocalBindingId,
                _ => Task.CompletedTask);
            await monitor.StartAsync();
            for (var index = 0; index < 5_000; index++) monitor.EnqueueForTest("overflow-failure-" + index + ".txt");
            monitor.EnqueueForTest(null, "overflow");
            var requiredOverflowGeneration = monitor.OverflowGenerationForTest;
            Assert(requiredOverflowGeneration > 1,
                "the quiet sender retained a newer overflow obligation than the session-start sentinel");

            await WaitUntilAsync(() => Task.FromResult(monitor.LastFailureReason is not null),
                TimeSpan.FromSeconds(15), "the first handled authority failure is recorded");
            await WaitUntilAsync(async () =>
                    (await fixture.Authority.LoadMonitorCursorAsync(monitor.CursorId))?.Status == "active" &&
                    await fixture.Authority.HasPendingPairDirtyHintForTestAsync(fixture.Pair.Id) &&
                    await CountAsync(fixture.Authority.DatabasePath,
                        "SELECT count(*) FROM monitor_event WHERE monitor_cursor_id=$id AND event_kind='overflow' AND json_extract(event_json,'$.overflowGeneration')=$generation;",
                        ("$id", monitor.CursorId), ("$generation", requiredOverflowGeneration)) == 1,
                TimeSpan.FromSeconds(20),
                "the overflow generation commits its pair-dirty obligation and clears the uncertainty");

            lock (gate)
            {
                Assert(observations.Count >= 3, "the commit boundary observed every failing attempt");
                Assert(observations.All(item => item.CursorValue is null),
                    "no uncommitted cursor advancement was ever retained while the journal commit failed");
            }
            var converged = await fixture.Authority.LoadMonitorCursorAsync(monitor.CursorId);
            Assert(converged is { Status: "active" } && !string.IsNullOrWhiteSpace(converged.CursorValue),
                "the cursor advanced only together with the real journal commit");
            Assert(await fixture.Authority.HasPendingPairDirtyHintForTestAsync(fixture.Pair.Id),
                "the retained overflow generation became a durable pair-dirty obligation");
            Assert(monitor.LastFailureReason is null, "a successful real commit cleared the retained failure reason");
            Assert(await CountAsync(fixture.Authority.DatabasePath,
                       "SELECT count(*) FROM monitor_event WHERE monitor_cursor_id=$id AND event_kind='overflow' AND json_extract(event_json,'$.overflowGeneration')=$generation;",
                       ("$id", monitor.CursorId), ("$generation", requiredOverflowGeneration)) == 1,
                "the latest discarded overflow generation committed exactly once without any later callback to wake the pump");
            await monitor.DisposeAsync();
        }
        finally
        {
            SyncAuthorityLocalMonitor.BeforeCommitForTest = null;
            SyncAuthorityLocalMonitor.EventQueueCapacityForTest = null;
            SyncAuthorityLocalMonitor.FailureRetryDelayForTest = null;
        }
    }

    // ----- C: restart never trusts a predecessor cursor (H6/R3) --------------------------------------

    private static async Task LocalRestartUncertaintyAsync()
    {
        SyncAuthorityLocalMonitor.EventQueueCapacityForTest = null;
        SyncAuthorityLocalMonitor.FailureRetryDelayForTest = TimeSpan.FromMilliseconds(5);
        SyncAuthorityLocalMonitor.BeforeCommitForTest = null;
        try
        {
            var fixture = await CreateFixtureAsync("local-restart");
            var predecessor = new SyncAuthorityLocalMonitor(fixture.Pair, fixture.Authority,
                fixture.LocalBindingId, _ => Task.CompletedTask);
            await predecessor.StartAsync();
            await WaitUntilAsync(
                async () => (await fixture.Authority.LoadMonitorCursorAsync(predecessor.CursorId))?.Status == "active",
                TimeSpan.FromSeconds(15), "the clean predecessor committed its session obligation");
            Assert(await fixture.Authority.LoadMonitorCursorAsync(predecessor.CursorId) is { Status: "active" },
                "the predecessor stopped with a healthy-looking active cursor");
            var eventsBefore = await CountAsync(fixture.Authority.DatabasePath,
                "SELECT count(*) FROM monitor_event WHERE pair_id=$pair;", ("$pair", fixture.Pair.Id));
            await predecessor.DisposeAsync();

            var successor = new SyncAuthorityLocalMonitor(fixture.Pair, fixture.Authority,
                fixture.LocalBindingId, _ => Task.CompletedTask);
            await successor.StartAsync();
            // No filesystem change is made anywhere in this case.
            var successorCursor = await fixture.Authority.LoadMonitorCursorAsync(successor.CursorId);
            Assert(successorCursor is { Status: "unavailable" },
                "the successor durably established full-reconciliation uncertainty inside StartAsync, before any native event");
            await WaitUntilAsync(async () =>
                    await CountAsync(fixture.Authority.DatabasePath,
                        "SELECT count(*) FROM monitor_event WHERE pair_id=$pair;", ("$pair", fixture.Pair.Id)) >
                    eventsBefore &&
                    await fixture.Authority.HasPendingPairDirtyHintForTestAsync(fixture.Pair.Id),
                TimeSpan.FromSeconds(15),
                "the successor committed a pair-dirty obligation without any filesystem event");
            await WaitUntilAsync(
                async () => (await fixture.Authority.LoadMonitorCursorAsync(successor.CursorId))?.Status == "active",
                TimeSpan.FromSeconds(15), "the successor returned to a healthy pump after the obligation committed");
            await successor.DisposeAsync();
        }
        finally
        {
            SyncAuthorityLocalMonitor.BeforeCommitForTest = null;
            SyncAuthorityLocalMonitor.EventQueueCapacityForTest = null;
            SyncAuthorityLocalMonitor.FailureRetryDelayForTest = null;
        }
    }

    // ----- D: disposal contains a failed pump (H5) ---------------------------------------------------

    private static async Task LocalDisposalContainmentAsync()
    {
        SyncAuthorityLocalMonitor.EventQueueCapacityForTest = 4;
        SyncAuthorityLocalMonitor.FailureRetryDelayForTest = TimeSpan.FromMilliseconds(5);
        try
        {
            var fixture = await CreateFixtureAsync("local-disposal");
            SyncAuthorityLocalMonitor.BeforeCommitForTest = (_, _) =>
                Task.FromException(new IOException("simulated terminal monitor database failure"));
            var monitor = new SyncAuthorityLocalMonitor(fixture.Pair, fixture.Authority, fixture.LocalBindingId,
                _ => Task.CompletedTask);
            await monitor.StartAsync();
            monitor.EnqueueForTest("disposal.txt");
            await WaitUntilAsync(() => Task.FromResult(monitor.LastFailureReason is not null),
                TimeSpan.FromSeconds(15), "the handled pump failure is recorded before disposal");
            monitor.EnqueueForTest("disposal-after-failure.txt");

            var watch = Stopwatch.StartNew();
            var disposalFailure = await TryAsync(() => monitor.DisposeAsync().AsTask());
            watch.Stop();
            Assert(disposalFailure is null,
                "DisposeAsync contained the failed pump instead of rethrowing it (" +
                (disposalFailure?.GetType().Name ?? "none") + ")");
            Assert(watch.Elapsed < TimeSpan.FromSeconds(15), "disposal released the owned watcher and tasks in bounded time");
            Assert(monitor.LastFailureReason is not null,
                "the safe failure reason survived disposal instead of being concealed as success");

            await File.WriteAllTextAsync(Path.Combine(fixture.LocalRoot, "after-disposal.txt"), "after");
            var afterDisposal = await CountAsync(fixture.Authority.DatabasePath,
                "SELECT count(*) FROM monitor_event WHERE pair_id=$pair;", ("$pair", fixture.Pair.Id));
            await Task.Delay(300);
            Assert(await CountAsync(fixture.Authority.DatabasePath,
                       "SELECT count(*) FROM monitor_event WHERE pair_id=$pair;", ("$pair", fixture.Pair.Id)) ==
                   afterDisposal,
                "a disposed monitor accepted no further hint");
            await monitor.DisposeAsync();
        }
        finally
        {
            SyncAuthorityLocalMonitor.BeforeCommitForTest = null;
            SyncAuthorityLocalMonitor.EventQueueCapacityForTest = null;
            SyncAuthorityLocalMonitor.FailureRetryDelayForTest = null;
        }
    }

    // ----- E: cursor reset followed by provider failure (H5) ----------------------------------------

    private static async Task DropboxResetThenProviderFailureAsync()
    {
        SyncAuthorityDropboxMonitor.FailureRetryDelayForTest = TimeSpan.FromMilliseconds(10);
        SyncAuthorityDropboxMonitor.BeforeCommitForTest = null;
        try
        {
            var fixture = await CreateFixtureAsync("dropbox-reset-provider");
            var remote = new FakeSyncRemoteStore();
            var feed = new ScriptedChangeFeed { LatestCursor = "cursor-old" };
            var predecessor = new SyncAuthorityDropboxMonitor(fixture.Pair, remote, feed, fixture.Authority,
                fixture.DropboxBindingId, _ => Task.CompletedTask, TimeSpan.FromMilliseconds(20));
            await predecessor.StartAsync();
            var stale = (await fixture.Authority.LoadMonitorCursorAsync(predecessor.CursorId))!.CursorValue!;
            Assert(stale == "cursor-old", "the retained cursor is the exact old provider cursor");
            await predecessor.DisposeAsync();

            feed.LatestCursor = "cursor-new";
            feed.ConflictCursor = stale;
            feed.FailLatestCursorTimes = 1;
            var observations = new List<MonitorObservation>();
            var gate = new object();
            SyncAuthorityDropboxMonitor? monitor = null;
            Func<string, Task> onHealth = async _ =>
            {
                var cursor = await fixture.Authority.LoadMonitorCursorAsync(monitor!.CursorId);
                lock (gate)
                    observations.Add(new MonitorObservation(cursor?.Status, cursor?.CursorValue,
                        monitor!.LastFailureReason));
            };
            monitor = new SyncAuthorityDropboxMonitor(fixture.Pair, remote, feed, fixture.Authority,
                fixture.DropboxBindingId, _ => Task.CompletedTask, TimeSpan.FromMilliseconds(20), onHealth);
            await monitor.StartAsync();
            await WaitUntilAsync(async () =>
                    (await fixture.Authority.LoadMonitorCursorAsync(monitor.CursorId)) is
                    { Status: "active", CursorValue: "cursor-new" },
                TimeSpan.FromSeconds(20), "the reset converged on the fresh provider cursor");

            lock (gate)
            {
                var unavailable = observations.Where(item => item.Status == "unavailable").ToList();
                Assert(unavailable.Count > 0, "the handled provider failure was reported through health");
                Assert(unavailable.All(item => item.CursorValue == stale),
                    "the retained cursor was never advanced before the reset event committed");
                Assert(unavailable.Any(item => item.Failure is not null),
                    "the handled provider failure retained an actionable safe reason");
            }
            Assert(await CountAsync(fixture.Authority.DatabasePath,
                       "SELECT count(*) FROM monitor_event WHERE pair_id=$pair AND event_kind='cursor_reset';",
                       ("$pair", fixture.Pair.Id)) == 1,
                "exactly one atomic full-reconciliation reset event was committed");
            Assert(await fixture.Authority.HasPendingPairDirtyHintForTestAsync(fixture.Pair.Id),
                "the reset durably forced whole-pair reconciliation");
            Assert(monitor.LastFailureReason is null, "the successful reset cleared the retained failure reason");
            await monitor.DisposeAsync();
        }
        finally
        {
            SyncAuthorityDropboxMonitor.BeforeCommitForTest = null;
            SyncAuthorityDropboxMonitor.FailureRetryDelayForTest = null;
        }
    }

    // ----- F: cursor reset followed by authority failure (H5) ---------------------------------------

    private static async Task DropboxResetThenDatabaseFailureAsync()
    {
        SyncAuthorityDropboxMonitor.FailureRetryDelayForTest = TimeSpan.FromMilliseconds(10);
        try
        {
            var fixture = await CreateFixtureAsync("dropbox-reset-database");
            var remote = new FakeSyncRemoteStore();
            var feed = new ScriptedChangeFeed { LatestCursor = "cursor-old" };
            var predecessor = new SyncAuthorityDropboxMonitor(fixture.Pair, remote, feed, fixture.Authority,
                fixture.DropboxBindingId, _ => Task.CompletedTask, TimeSpan.FromMilliseconds(20));
            await predecessor.StartAsync();
            var stale = (await fixture.Authority.LoadMonitorCursorAsync(predecessor.CursorId))!.CursorValue!;
            await predecessor.DisposeAsync();

            feed.LatestCursor = "cursor-new";
            feed.ConflictCursor = stale;
            var observations = new List<MonitorObservation>();
            var gate = new object();
            var attempts = 0;
            SyncAuthorityDropboxMonitor? monitor = null;
            SyncAuthorityDropboxMonitor.BeforeCommitForTest = async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref attempts) > 2) return;
                var cursor = await fixture.Authority.LoadMonitorCursorAsync(monitor!.CursorId, cancellationToken);
                lock (gate)
                    observations.Add(new MonitorObservation(cursor?.Status, cursor?.CursorValue,
                        monitor!.LastFailureReason));
                throw new IOException("simulated authority failure committing the reset event");
            };
            monitor = new SyncAuthorityDropboxMonitor(fixture.Pair, remote, feed, fixture.Authority,
                fixture.DropboxBindingId, _ => Task.CompletedTask, TimeSpan.FromMilliseconds(20));
            await monitor.StartAsync();
            await WaitUntilAsync(async () =>
                    (await fixture.Authority.LoadMonitorCursorAsync(monitor.CursorId)) is
                    { Status: "active", CursorValue: "cursor-new" },
                TimeSpan.FromSeconds(20), "the reset event and the fresh cursor committed together");

            Assert(attempts >= 3, "the reset commit was retried after its handled failures");
            lock (gate)
            {
                Assert(observations.Count >= 2, "the commit boundary observed every failing reset attempt");
                Assert(observations.All(item => item.CursorValue == stale),
                    "no uncommitted reset ever advanced the retained cursor");
                Assert(observations.Any(item => item.Status == "unavailable" && item.Failure is not null),
                    "the failed reset retained durable uncertainty and an actionable reason");
            }
            Assert(await CountAsync(fixture.Authority.DatabasePath,
                       "SELECT count(*) FROM monitor_event WHERE pair_id=$pair AND event_kind='cursor_reset';",
                       ("$pair", fixture.Pair.Id)) == 1,
                "the converged reset committed exactly one full-reconciliation event");
            Assert(await fixture.Authority.HasPendingPairDirtyHintForTestAsync(fixture.Pair.Id),
                "the converged reset durably forced whole-pair reconciliation");
            Assert(monitor.LastFailureReason is null, "the successful reset cleared the retained failure reason");
            await monitor.DisposeAsync();
        }
        finally
        {
            SyncAuthorityDropboxMonitor.BeforeCommitForTest = null;
            SyncAuthorityDropboxMonitor.FailureRetryDelayForTest = null;
        }
    }

    // ----- G: disposal contains a permanently failing reset recovery (H5) ---------------------------

    private static async Task DropboxDisposalContainmentAsync()
    {
        SyncAuthorityDropboxMonitor.FailureRetryDelayForTest = TimeSpan.FromMilliseconds(10);
        try
        {
            var fixture = await CreateFixtureAsync("dropbox-disposal");
            var remote = new FakeSyncRemoteStore();
            var feed = new ScriptedChangeFeed { LatestCursor = "cursor-live" };
            var monitor = new SyncAuthorityDropboxMonitor(fixture.Pair, remote, feed, fixture.Authority,
                fixture.DropboxBindingId, _ => Task.CompletedTask, TimeSpan.FromMilliseconds(20));
            await monitor.StartAsync();
            var retained = (await fixture.Authority.LoadMonitorCursorAsync(monitor.CursorId))!.CursorValue!;
            feed.ConflictCursor = retained;
            feed.FailLatestCursorTimes = int.MaxValue;
            await WaitUntilAsync(() => Task.FromResult(monitor.LastFailureReason is not null),
                TimeSpan.FromSeconds(15), "the failing reset recovery is handled and recorded");

            var watch = Stopwatch.StartNew();
            var failure = await TryAsync(() => monitor.DisposeAsync().AsTask());
            watch.Stop();
            Assert(failure is null,
                "DisposeAsync contained the failed reset recovery instead of rethrowing it (" +
                (failure?.GetType().Name ?? "none") + ")");
            Assert(watch.Elapsed < TimeSpan.FromSeconds(15), "disposal released the failed pump in bounded time");
            Assert(monitor.LastFailureReason is not null, "the safe failure reason survived disposal");
            Assert(await CountAsync(fixture.Authority.DatabasePath,
                       "SELECT count(*) FROM monitor_event WHERE pair_id=$pair AND event_kind='cursor_reset';",
                       ("$pair", fixture.Pair.Id)) == 0,
                "a reset that could never read a fresh cursor committed no reset event");
            Assert(await CountAsync(fixture.Authority.DatabasePath,
                       "SELECT count(*) FROM monitor_cursor WHERE monitor_cursor_id=$id AND status='unavailable';",
                       ("$id", monitor.CursorId)) == 1,
                "the retained cursor stayed durably unavailable with its exact old value");
        }
        finally
        {
            SyncAuthorityDropboxMonitor.BeforeCommitForTest = null;
            SyncAuthorityDropboxMonitor.FailureRetryDelayForTest = null;
        }
    }

    // ----- fixture and helpers ----------------------------------------------------------------------

    private static async Task<Fixture> CreateFixtureAsync(string name)
    {
        var root = Path.Combine(_root, name);
        Directory.CreateDirectory(root);
        var localRoot = Path.Combine(root, "monitored");
        Directory.CreateDirectory(localRoot);
        PilotDirectoryIdentity identity;
        using (var lease = PilotDirectoryLease.Open(localRoot)) identity = lease.Identity;
        var pair = new SyncPair("pair-" + name, "Monitor failure correction " + name, localRoot,
            identity.VolumeSerialNumber, identity.FileId, "/Monitor Contract", "id:monitor-" + name);
        var authority = new SyncAuthorityDatabase(Path.Combine(root, "authority", "syncshark.db"));
        await authority.InitializeAsync("installation-" + name, "profile-" + name, name, "{}");
        await authority.RegisterPairAsync(pair.Id, pair.PolicyRevision);
        await authority.RegisterRootBindingAsync("root-" + name + "-local", pair.Id, "local", 1,
            JsonSerializer.Serialize(new { identity.VolumeSerialNumber, identity.FileId }), localRoot);
        await authority.RegisterRootBindingAsync("root-" + name + "-dropbox", pair.Id, "dropbox", 1,
            JsonSerializer.Serialize(new { folderId = pair.DropboxFolderId }), pair.DropboxRoot);
        return new Fixture(localRoot, pair, authority, "root-" + name + "-local", "root-" + name + "-dropbox");
    }

    private static async Task CaseAsync(string id, string label, Func<Task> body)
    {
        var before = _assertions;
        try
        {
            await body();
            Cases.Add(new Dictionary<string, object?>
            {
                ["case"] = id, ["label"] = label, ["result"] = "PASS", ["assertions"] = _assertions - before
            });
            Console.WriteLine("PASS " + id + ": " + (_assertions - before) + " assertions");
        }
        catch (Exception exception)
        {
            Cases.Add(new Dictionary<string, object?>
            {
                ["case"] = id, ["label"] = label, ["result"] = "FAIL", ["assertions"] = _assertions - before,
                ["error"] = exception.GetType().Name + ": " + exception.Message
            });
            Console.WriteLine("FAIL " + id + ": " + exception.GetType().Name + ": " + exception.Message);
            throw;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string label)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (await condition()) return;
            if (DateTime.UtcNow >= deadline)
                throw new InvalidOperationException("timed out waiting for: " + label);
            await Task.Delay(20);
        }
    }

    private static async Task<Exception?> TryAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task<long> CountAsync(string databasePath, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidDataException("monitor failure correction assertion failed: " + message);
    }

    private sealed record Fixture(string LocalRoot, SyncPair Pair, SyncAuthorityDatabase Authority,
        string LocalBindingId, string DropboxBindingId);

    private sealed record MonitorObservation(string? Status, string? CursorValue, string? Failure);

    // Deterministic provider change feed: the caller scripts the retained-cursor conflict and the
    // fresh-cursor read failure. It supplies transport/error input only; the reset decision, the
    // atomic commit and the cursor retention remain production monitor behaviour.

    internal static async Task RunBatchContinuationAsync(string root)
    {
        _root=Path.GetFullPath(root);Directory.CreateDirectory(_root);_assertions=0;
        SyncAuthorityDropboxMonitor.FailureRetryDelayForTest=TimeSpan.FromMilliseconds(10);
        try
        {
            foreach(var reset in new[]{false,true})
            {
                var fixture=await CreateFixtureAsync(reset?"lost-reset-ack":"lost-page-ack");
                var feed=new ScriptedChangeFeed {LatestCursor="old"};
                var cursorId="monitor-dropbox-"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(fixture.Pair.Id+"\n"+fixture.DropboxBindingId))).ToLowerInvariant()[..32];
                await fixture.Authority.RegisterMonitorCursorAsync(cursorId,fixture.Pair.Id,fixture.DropboxBindingId,
                    "dropbox_cursor",1,JsonSerializer.Serialize(new{folderId=fixture.Pair.DropboxFolderId}),"old");
                if(reset){feed.ConflictCursor="old";feed.LatestCursor="next";}
                else feed.Page=cursor=>new SyncRemoteChangePage("next",false,
                    [new SyncRemoteChangeEntry("changed.txt","changed","{}")]);
                var faults=0;
                fixture.Authority.MonitorCommittedBeforeAcknowledgementForTest=()=>
                {
                    if(Interlocked.Increment(ref faults)==1)throw new IOException("fixture acknowledgement lost after real commit");
                };
                var committed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await using var monitor=new SyncAuthorityDropboxMonitor(fixture.Pair,new FakeSyncRemoteStore(),feed,
                    fixture.Authority,fixture.DropboxBindingId,_=>{committed.TrySetResult();return Task.CompletedTask;},
                    TimeSpan.FromMinutes(1));
                await monitor.StartAsync();
                await committed.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await monitor.DisposeAsync();
                var retained=await fixture.Authority.LoadMonitorCursorAsync(cursorId);
                Assert(retained is {Status:"active",CursorValue:"next"}&&monitor.LastFailureReason is null,
                    "lost "+(reset?"reset":"page")+" acknowledgement resolves retained cursor and monitor health");
                Assert(feed.OldCursorCalls==1&&(!reset||feed.LatestCalls==1),
                    "ambiguous commit retries the exact batch without refetching provider input");
                Assert(await CountAsync(fixture.Authority.DatabasePath,"SELECT count(*) FROM monitor_event;")==1&&
                    await CountAsync(fixture.Authority.DatabasePath,"SELECT sum(event_count) FROM dirty_item;")==1,
                    "actual producer lost acknowledgement retains exactly one event and one dirty hint");
            }
            Console.WriteLine("PASS actual Dropbox page/reset lost-ack continuation: "+_assertions+" assertions");
        }
        finally{SyncAuthorityDropboxMonitor.FailureRetryDelayForTest=null;}
    }

    private sealed class ScriptedChangeFeed : ISyncRemoteChangeFeed
    {
        public string LatestCursor { get; set; } = "cursor-0";
        public string? ConflictCursor { get; set; }
        public int FailLatestCursorTimes { get; set; }
        public int ContinueCalls,OldCursorCalls,LatestCalls;
        public Func<string,SyncRemoteChangePage>? Page { get; set; }

        public Task<string> GetLatestCursorAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref LatestCalls);
            if (FailLatestCursorTimes > 0)
            {
                FailLatestCursorTimes--;
                throw new IOException("simulated provider fresh-cursor read failure");
            }
            return Task.FromResult(LatestCursor);
        }

        public Task<SyncRemoteChangePage> ContinueChangesAsync(string cursor,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ContinueCalls);
            if(cursor=="old")Interlocked.Increment(ref OldCursorCalls);
            if (ConflictCursor is { } stale && string.Equals(cursor, stale, StringComparison.Ordinal))
                throw new DropboxApiException(HttpStatusCode.Conflict, "fixture cursor reset");
            return Task.FromResult(Page?.Invoke(cursor)??new SyncRemoteChangePage(cursor, false, []));
        }
    }
}


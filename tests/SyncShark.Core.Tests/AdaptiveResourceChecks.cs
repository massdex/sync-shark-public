using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

internal static class AdaptiveResourceChecks
{
    internal static async Task RunAsync(string root)
    {
        if (Directory.Exists(root)) throw new IOException("Use a fresh adaptive fixture.");
        Directory.CreateDirectory(root);
        await SettingsAsync(root);
        await HashCacheAsync(Path.Combine(root, "hash-cache"));
        Adaptation();
        CommitAdmissionBound();
        PerformanceMemoryPolicy();
        ReadOnlyPressureRecovery();
        InspectionTransferIsolation();
        PerformanceCpuPolicy();
        if (OperatingSystem.IsWindows())
        {
            var machine = new SyncTransferResourceController.MachinePressure().Read();
            Require(machine.AvailableBytes > 0 && machine.MemoryFraction is > 0 and < 1,
                "the Windows controller reads current physical-memory pressure, not last-GC state");
            Require(machine.SystemCommitFraction > 0 && double.IsFinite(machine.SystemCommitFraction) &&
                    machine.FreeCommitBytes >= 0 && machine.FreeCommitBytes < long.MaxValue,
                "the Windows controller reads live system commit and free commit bytes");
        }
        ProviderIsolation("Auto");
        ProviderIsolation("Performance");
        ProviderBurstIsolation("Auto");
        ProviderBurstIsolation("Performance");
        await SchedulerAsync("Auto", Calm);
        await SchedulerAsync("Performance", new(.4, .97, 512L * 1024 * 1024));
        await SchedulerAsync("Performance", new(1, .5, 8L * 1024 * 1024 * 1024));
        await BandwidthReservationAsync();
        await ProviderOperationIsolationAsync();
        await DropboxRouteLanesAsync();
        await AccountThrottleScopeAsync();
        await ProviderRequestFifoAsync();
        await ProviderRequestsAsync();
        Console.WriteLine("PASS adaptive-resource focused suite complete");
    }

    private static async Task SettingsAsync(string root)
    {
        var legacy = JsonSerializer.Deserialize<SyncTransferSettings>("{\"MaximumConcurrentPairs\":3}")!;
        Require(legacy.ResourceMode == "Auto" && legacy.MaximumConcurrentFiles == 0, "older settings migrate additively to Auto");
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var store = new SyncTransferSettingsStore(instance.StateAuthority);
        foreach (var mode in new[] { "Auto", "Performance", "Low impact" })
        {
            var value = new SyncTransferSettings(3, 120, 480, mode, 40);
            await store.SaveAsync(value);
            Require(new SyncTransferSettingsStore(instance.StateAuthority).Load() == value,
                "durable settings reopen preserves " + mode + ", caps and file ceiling");
        }
        foreach (var value in new[] { new SyncTransferSettings(ResourceMode: "Fastish"),
            new SyncTransferSettings(MaximumConcurrentFiles: -1), new SyncTransferSettings(MaximumConcurrentFiles: 1025) })
        {
            var rejected = false;
            try { await store.SaveAsync(value); } catch (InvalidDataException) { rejected = true; }
            Require(rejected && store.Load().ResourceMode == "Low impact", "invalid resource settings fail before replacing saved values");
        }
    }

    private static async Task HashCacheAsync(string root)
    {
        var db = new SyncAuthorityDatabase(Path.Combine(root, "authority.db"));
        await db.InitializeAsync("hash-cache-installation", "hash-cache-profile", "focused-cache", "{}");
        var time = new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);
        var hash = new string('a', 64);
        var alternate = new string('b', 64);
        var fingerprints = Enumerable.Range(1, 24).Select(i =>
            new PilotLocalFileFingerprint($"file-{i}.txt", 1, (ulong)i, i * 1024, time)).ToArray();
        async Task Add(string id, PilotLocalFileFingerprint file, string content, string? algorithm = null)
        {
            await db.RegisterObjectAsync("object-" + id, "file");
            var identity = JsonSerializer.Serialize(new {
                volumeSerial = file.VolumeSerialNumber.ToString("x8"), fileId = file.FileId.ToString("x16"),
                lastWriteUtc = file.LastWriteUtc.ToString("O"), contentAlgorithm = algorithm });
            await db.RegisterObjectVersionAsync("version-" + id, "object-" + id, "local", identity, content, file.Size);
        }
        foreach (var file in fingerprints) await Add(file.FileId.ToString(), file, hash);
        await Add("other-algorithm", fingerprints[0], alternate, SyncContentIdentity.Sha256);
        var opened = 0;
        SyncLocalContentHashCache cache;
        SyncAuthorityDatabase.TimingForTest = (phase, _) => { if (phase == "connection-open") opened++; };
        try
        {
            cache = await db.LoadLocalContentHashCacheAsync(1, SyncContentIdentity.DropboxBlockSha256);
            var exact = true;
            for (var pass = 0; pass < 20; pass++)
                foreach (var file in fingerprints)
                    exact &= await cache.ResolveAsync(file, default) == hash;
            Require(exact && opened == 1, "480 exact hash lookups use one checked database read, not 480 table scans/connections");
        }
        finally { SyncAuthorityDatabase.TimingForTest = null; }
        Require(await db.LoadCachedLocalContentHashAsync(fingerprints[0]) == await cache.ResolveAsync(fingerprints[0], default),
            "bounded cache agrees with the prior real lookup for a legacy Dropbox identity");
        Require(await cache.ResolveAsync(fingerprints[0] with { RelativePath = "renamed.txt" }, default) == hash &&
                await cache.ResolveAsync(fingerprints[0] with { Size = 9 }, default) is null &&
                await cache.ResolveAsync(fingerprints[0] with { VolumeSerialNumber = 2 }, default) is null &&
                await cache.ResolveAsync(fingerprints[0] with { LastWriteUtc = time.AddSeconds(1) }, default) is null,
            "a rename keeps physical identity; size, volume and timestamp changes force fresh hashing");
        var drive = await db.LoadLocalContentHashCacheAsync(1, SyncContentIdentity.Sha256);
        Require(await drive.ResolveAsync(fingerprints[0], default) == alternate &&
                await drive.ResolveAsync(fingerprints[1], default) is null,
            "Drive and legacy Dropbox content algorithms never cross-reuse a hash");
        var bounded = await db.LoadLocalContentHashCacheAsync(1, null, maximumEntries: 2);
        Require(bounded.Count == 2, "cache memory stays bounded; absent fingerprints fall back to file hashing");
        var direct = new SyncLocalContentHashCache(1, 1);
        direct.Add("0000000000000001", 1024, time.ToString("O"), hash);
        direct.Add("0000000000000002", 2048, time.ToString("O"), hash);
        direct.Add("0000000000000001", 1024, time.ToString("O"), alternate);
        await RejectConflict(direct);
        await Add("conflict", fingerprints[0], alternate);
        await RejectConflict(await db.LoadLocalContentHashCacheAsync(1, null));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var cancellationObserved = false;
        try { await cache.ResolveAsync(fingerprints[0], canceled.Token); }
        catch (OperationCanceledException) { cancellationObserved = true; }
        Require(cancellationObserved, "cached lookups retain cancellation responsiveness");
        async Task RejectConflict(SyncLocalContentHashCache value)
        {
            var rejected = false;
            try { await value.ResolveAsync(fingerprints[0], default); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "conflicting retained hashes reject even after the cache reaches its memory bound");
        }
    }

    private static void Adaptation()
    {
        var clock = new Clock();
        var pressure = Calm;
        var controller = new SyncTransferResourceController(new(), clock, () => pressure);
        var scope = controller.ForEndpoints([]);
        Require(controller.Snapshot() is { AllowedFiles: 16, MaximumFiles: 64, Learning: true }, "Auto starts at16 with a64 automatic ceiling, not a claimed optimum");
        void Sample()
        {
            var batch = Enumerable.Range(0, controller.Snapshot().AllowedFiles)
                .Select(_ => scope.TryAcquire(1024, true) ?? throw new Exception("sample admission")).ToArray();
            foreach (var file in batch) { clock.Advance(.2); file.Complete(true); }
        }
        Sample();
        Require(controller.Snapshot().AllowedFiles == 20, "real successful-work samples grow Auto cautiously");
        Sample();
        Require(controller.Snapshot().AllowedFiles == 16 && controller.Snapshot().Reason.Contains("lighter"),
            "unchanged useful throughput rolls the probe back despite more workers");
        for (var i = 0; i < 3; i++) Sample();
        Require(controller.Snapshot().AllowedFiles == 16, "settled workload does not oscillate immediately after plateau");
        pressure = new(.95, .5, 4L * 1024 * 1024 * 1024); clock.Advance(4);
        using var pressureLease = scope.TryAcquire(1024, true);
        Require(controller.Snapshot().AllowedFiles == 8, "CPU pressure reduces new admission without cancelling active work");
        Require(controller.Snapshot().Reason.Contains("CPU") && controller.Snapshot().Reason.Contains("GiB available"),
            "a resource reduction explains the measured machine pressure rather than a vague busy status");
        controller.Update(new(ResourceMode: "Low impact"));
        Require(controller.Snapshot() is { AllowedFiles: 2, MaximumFiles: 8 }, "Low impact has a smaller starting window and ceiling");
        controller.Update(new(ResourceMode: "Performance", MaximumConcurrentFiles: 96));
        Require(controller.Snapshot() is { AllowedFiles: 96, MaximumFiles: 96 }, "Performance starts at its explicit ceiling; real admission still checks pressure");
        var before = controller.Snapshot(); controller.Update(new(ResourceMode: "Performance", MaximumConcurrentFiles: 96));
        Require(controller.Snapshot() == before, "unchanged settings reads preserve adaptation");
        // Memory backoff uses genuinely low AVAILABLE memory or near-limit system
        // commit, never a cold working set or high physical-memory percentage alone.
        var lowClock = new Clock();
        var low = new SyncTransferResourceController(new(), lowClock,
            () => new(.1, .94, 384L * 1024 * 1024, 5L * 1024 * 1024 * 1024));
        var lowScope = low.ForEndpoints([]);
        for (var i = 0; i < 8; i++)
        {
            using var file = lowScope.TryAcquire(1024, true) ?? throw new Exception("low-memory admission");
            lowClock.Advance(6); file.Complete(true);
        }
        Require(low.Snapshot().AllowedFiles == 1 && low.Snapshot().FilesPerSecond > 0,
            "available memory below 512 MiB keeps protective admission AND measures slower completed work");
        foreach (var mode in new[] { "Auto", "Performance", "Low impact" })
        {
            var commitClock = new Clock();
            var freeCommit = 8L * 1024 * 1024 * 1024;
            var commitController = new SyncTransferResourceController(new(ResourceMode: mode), commitClock,
                () => new(.1, .4, 8L * 1024 * 1024 * 1024, 5L * 1024 * 1024 * 1024, .98, freeCommit));
            var initial = commitController.Snapshot().AllowedFiles;
            var commitScope = commitController.ForEndpoints([]);
            for (var poll = 0; poll < 4; poll++)
            {
                using var lease = commitScope.TryAcquire(1024, true);
                Require(lease is not null && commitController.Snapshot().AllowedFiles == initial,
                    $"{mode}: 98% commit with 8 GiB free commit must retain the high window");
                commitClock.Advance(4);
            }
            freeCommit = 256L * 1024 * 1024;
            using (var lease = commitScope.TryAcquire(1024, true))
                Require(lease is not null && commitController.Snapshot().AllowedFiles == 1 &&
                    commitController.Snapshot().Reason.Contains("system commit"),
                    "low absolute commit headroom reduces admission and explains why");
            for (var poll = 0; poll < 3; poll++)
            {
                commitClock.Advance(30);
                using var lease = commitScope.TryAcquire(1024, true);
                Require(lease is not null && commitController.Snapshot().AllowedFiles == 1,
                    "the window cannot regrow while free commit remains below the reserve");
            }
            freeCommit = 8L * 1024 * 1024 * 1024;
            for (var expected = 2; expected <= initial; expected *= 2)
            {
                commitClock.Advance(30);
                using var lease = commitScope.TryAcquire(1024, true);
                Require(lease is not null && commitController.Snapshot().AllowedFiles == expected,
                    $"{mode}: recovered commit doubles the window every 30 seconds without completed files");
            }
        }
        var fine = new SyncTransferResourceController(new(), new Clock(),
            () => new(.1, .94, 1200L * 1024 * 1024, 64L * 1024 * 1024));
        using var fineLease = fine.ForEndpoints([]).TryAcquire(1024, true);
        Require(fineLease is not null && fine.Snapshot().AllowedFiles == 16,
            "94% memory used with over 512 MiB available and a small working set is not reduced");
    }

    private static void CommitAdmissionBound()
    {
        var clock = new Clock();
        var freeCommit = 800L * 1024 * 1024;
        var controller = new SyncTransferResourceController(new(ResourceMode: "Performance"), clock,
            () => Calm with { FreeCommitBytes = freeCommit });
        var scope = controller.ForEndpoints([]);
        var leases = new List<SyncTransferResourceController.Lease>();
        try
        {
            while (leases.Count < 256 && scope.TryAcquire(64L * 1024 * 1024, true) is { } lease)
                leases.Add(lease);
            Require(leases.Count == 18 && controller.Snapshot().AllowedFiles == 18,
                "800 MiB free commit admits only eighteen 16 MiB streams after its 512 MiB reserve");
            freeCommit = 512L * 1024 * 1024 + 64 * 1024;
            clock.Advance(4);
            Require(scope.TryAcquire(1, true) is null && controller.Snapshot().ActiveFiles == 18,
                "commit loss blocks new work without revoking admitted streams");
        }
        finally { foreach (var lease in leases) lease.Dispose(); }

        // Inspection can bypass the learned file window, but not the shared byte budget.
        var inspection = controller.ForEndpoints([]);
        using var first = inspection.TryAcquireInspection();
        Require(first is not null && inspection.TryAcquireInspection() is null,
            "per-lease reservations use free commit even when physical memory is plentiful");
    }

    private static void PerformanceMemoryPolicy()
    {
        // Reproduce the Child machine's high RAM percentage and 0.5-0.7 GiB
        // available. It has room for concurrent streamed files, not just one.
        // The memory window divides the headroom by the admission reservation floor,
        // so pin the floor to 16 MiB and the expected window is exact (32/16/8).
        foreach (var (availableMiB, expected) in new[] { (768, 32), (512, 16), (384, 8) })
        {
            var clock = new Clock();
            var controller = new SyncTransferResourceController(new(ResourceMode: "Performance",
                TransferReservationFloorBytes: 16L * 1024 * 1024), clock,
                () => new(.4, .97, availableMiB * 1024L * 1024));
            var scope = controller.ForEndpoints([]);
            var leases = Enumerable.Range(0, expected)
                .Select(_ => scope.TryAcquire(1024, true) ?? throw new Exception("Performance admission")).ToArray();
            try
            {
                for (var poll = 0; poll < 4; poll++)
                {
                    clock.Advance(4);
                    using var excess = scope.TryAcquire(1024, true);
                    Require(excess is null && controller.Snapshot().AllowedFiles == expected,
                        $"Performance retains {expected} concurrent files at 97% RAM / {availableMiB} MiB available");
                }
            }
            finally { foreach (var lease in leases) lease.Complete(true); }
            Require(controller.Snapshot().ActiveFiles == 0, "Performance releases all transfer allowances");
        }
        foreach (var mode in new[] { "Auto", "Low impact" })
        {
            var controller = new SyncTransferResourceController(new(ResourceMode: mode), new Clock(),
                () => new(.4, .97, 256L * 1024 * 1024));
            using var lease = controller.ForEndpoints([]).TryAcquire(1024, true);
            Require(lease is not null && controller.Snapshot().AllowedFiles == 1,
                mode + " reduces admission when available memory falls below 512 MiB");
        }
        var emergencyClock = new Clock();
        var pressure = Calm;
        var emergency = new SyncTransferResourceController(new(ResourceMode: "Performance"), emergencyClock, () => pressure);
        var emergencyScope = emergency.ForEndpoints([]);
        var admitted = Enumerable.Range(0, 4).Select(_ => emergencyScope.TryAcquire(1024, true)!).ToArray();
        pressure = new(.4, .995, 128L * 1024 * 1024); emergencyClock.Advance(4);
        Require(emergencyScope.TryAcquire(1024, true) is null &&
                emergency.Snapshot() is { ActiveFiles: 4, AllowedFiles: 1 },
            "Performance critical headroom stops added concurrency without revoking admitted effects");
        foreach (var lease in admitted) lease.Complete(true);
        Require(emergency.Snapshot().ActiveFiles == 0, "low-memory effects drain normally");
        var unknown = new SyncTransferResourceController(new(ResourceMode: "Performance"), new Clock(),
            () => new(.1, 0, 0));
        using var single = unknown.ForEndpoints([]).TryAcquire(1024, true);
        Require(single is not null && unknown.Snapshot().AllowedFiles == 1,
            "unknown available memory never gives Performance an unbounded allowance");

        // A user can raise the Performance ceiling while effects are active.
        // Reusing the previous pressure poll would over-admit until its timer ran.
        var resizePressure = Calm;
        var resize = new SyncTransferResourceController(
            new(ResourceMode: "Performance", MaximumConcurrentFiles: 96,
                TransferReservationFloorBytes: 16L * 1024 * 1024), new Clock(), () => resizePressure);
        var resizeScope = resize.ForEndpoints([]);
        var retained = Enumerable.Range(0, 96).Select(_ =>
            resizeScope.TryAcquire(1024, true) ?? throw new Exception("Performance explicit-ceiling admission")).ToArray();
        try
        {
            resizePressure = new(.4, .97, 512L * 1024 * 1024);
            resize.Update(new(ResourceMode: "Performance", MaximumConcurrentFiles: 128,
                TransferReservationFloorBytes: 16L * 1024 * 1024));
            using var rejected = resizeScope.TryAcquire(1024, true);
            Require(rejected is null && resize.Snapshot() is
                    { ActiveFiles: 96, AllowedFiles: 16, MaximumFiles: 128 },
                "Performance ceiling changes recheck memory immediately and retain already-admitted effects");
        }
        finally { foreach (var retainedLease in retained) retainedLease.Complete(true); }
        Require(resize.Snapshot().ActiveFiles == 0, "all pre-resize Performance effects drain safely");
    }

    private static void ReadOnlyPressureRecovery()
    {
        foreach (var ceiling in new[] { 12, 32 })
        {
            var clock = new Clock();
            var pressure = new SyncResourcePressure(.5, .995, 128L * 1024 * 1024);
            var controller = new SyncTransferResourceController(new(ResourceMode: "Performance",
                MaximumConcurrentFiles: ceiling, TransferReservationFloorBytes: 16L * 1024 * 1024), clock, () => pressure);
            var scope = controller.ForEndpoints([]);
            var admitted = new List<SyncTransferResourceController.Lease>();
            try
            {
                admitted.Add(scope.TryAcquireInspection() ?? throw new Exception("critical-memory inspection"));
                Require(scope.TryAcquireInspection() is null && controller.Snapshot().AllowedInspections == 1,
                    "Critical-memory read-only inspection remains bounded and never revokes its admitted handle");
                pressure = new(.5, .97, 512L * 1024 * 1024);
                clock.Advance(4);
                var expected = Math.Min(ceiling, 16);
                Require(scope.TryAcquire(0, file: false) is null && controller.Snapshot().AllowedFiles == 1,
                    "A generic non-file effect cannot select inspection recovery or enlarge transfer admission");
                for (var index = 1; index < expected; index++)
                    admitted.Add(scope.TryAcquireInspection() ??
                        throw new InvalidOperationException("Recovered physical memory must restore bounded read-only inspection without waiting for a transferred file."));
                Require(scope.TryAcquireInspection() is null && controller.Snapshot() is
                        { AllowedFiles: 1, ActiveTransfers: 0, FilesPerSecond: 0, BytesPerSecond: 0 } &&
                        controller.Snapshot().AllowedInspections == expected && controller.Snapshot().ActiveInspections == expected,
                    $"Recovered inspection reaches its actual memory/user ceiling {expected}, with no invented transfer throughput");
                pressure = new(.5, .995, 128L * 1024 * 1024);
                clock.Advance(4);
                Require(scope.TryAcquireInspection() is null && controller.Snapshot().ActiveFiles == expected &&
                        controller.Snapshot().AllowedInspections == 1,
                    "Recurring memory pressure stops new inspection but lets every existing native handle drain");
            }
            finally { foreach (var lease in admitted) lease.Dispose(); }
            Require(controller.Snapshot() is { ActiveFiles: 0, FilesPerSecond: 0, BytesPerSecond: 0 },
                "Read-only recovery drains all admissions without counting scans as synced files");
        }
        foreach (var mode in new[] { "Auto", "Low impact" })
        {
            var clock = new Clock();
            var pressure = new SyncResourcePressure(.5, .995, 128L * 1024 * 1024);
            var controller = new SyncTransferResourceController(new(ResourceMode: mode), clock, () => pressure);
            var scope = controller.ForEndpoints([]);
            using var retained = scope.TryAcquireInspection() ?? throw new Exception("conservative-mode inspection");
            pressure = Calm; clock.Advance(4);
            Require(scope.TryAcquireInspection() is null && controller.Snapshot().AllowedInspections == 1,
                mode + " inspection retains its existing responsiveness-first recovery policy");
        }
        foreach (var available in new[] { 0L, 128L * 1024 * 1024 })
        {
            var controller = new SyncTransferResourceController(new(ResourceMode: "Performance"), new Clock(),
                () => new(.1, .99, available));
            var scope = controller.ForEndpoints([]);
            using var retained = scope.TryAcquireInspection() ?? throw new Exception("unknown/critical memory inspection");
            Require(scope.TryAcquireInspection() is null && controller.Snapshot().AllowedInspections == 1,
                "Unknown or sub-reserve memory never grants an enlarged inspection allowance");
        }
        var providerClock = new Clock();
        var providerController = new SyncTransferResourceController(new(ResourceMode: "Performance"), providerClock, () => Calm);
        var providerScope = providerController.ForEndpoints(["inspection-provider"]);
        providerController.ProviderThrottled(new("inspection-provider", SyncProviderOperation.Read), TimeSpan.FromSeconds(4));
        Require(providerScope.TryAcquireInspection() is null, "Explicit inspection obeys the exact shared provider cooldown");
        providerClock.Advance(4);
        var providerAdmissions = Enumerable.Range(0, 128).Select(_ => providerScope.TryAcquireInspection() ??
            throw new Exception("provider-limited inspection")).ToArray();
        try
        {
            Require(providerScope.TryAcquireInspection() is null && providerController.Snapshot().ActiveInspections == 128,
                "A recovered inspection allowance cannot bypass a provider's reduced admission window");
        }
        finally { foreach (var lease in providerAdmissions) lease.Dispose(); }
    }

    private static void InspectionTransferIsolation()
    {
        var clock = new Clock();
        var controller = new SyncTransferResourceController(new(ResourceMode: "Performance", MaximumConcurrentFiles: 8,
            TransferReservationFloorBytes: 16L * 1024 * 1024),
            clock, () => new(.1, .97, 320L * 1024 * 1024));
        var scope = controller.ForEndpoints([]);
        var inspections = Enumerable.Range(0, 3).Select(_ => scope.TryAcquireInspection() ??
            throw new Exception("mixed inspection admission")).ToArray();
        try
        {
            clock.Advance(20); // Scan time is not transferred-file measurement time.
            for (var index = 0; index < 4; index++)
            {
                using var file = scope.TryAcquire(1024, true) ?? throw new Exception("mixed transfer admission");
                clock.Advance(1); file.Complete(true);
            }
            Require(controller.Snapshot() is { ActiveFiles: 3, ActiveInspections: 3, ActiveTransfers: 0,
                    AllowedFiles: 4, FilesPerSecond: 1, BytesPerSecond: 1024, Learning: true } &&
                    controller.Snapshot().Reason.Contains("queue"),
                "Inspection time/occupancy neither fabricates transfer saturation nor dilutes actual useful throughput");
        }
        finally { foreach (var lease in inspections) lease.Dispose(); }
        Require(controller.Snapshot() is { ActiveFiles: 0, ActiveInspections: 0, ActiveTransfers: 0 },
            "Mixed inspection and transfer admissions release their exact independent accounting");
    }

    private static void PerformanceCpuPolicy()
    {
        var clock = new Clock();
        var controller = new SyncTransferResourceController(new(ResourceMode: "Performance"), clock,
            () => new(1, .5, 8L * 1024 * 1024 * 1024));
        var scope = controller.ForEndpoints([]);
        var leases = Enumerable.Range(0, 256).Select(_ =>
            scope.TryAcquire(1024, true) ?? throw new Exception("Performance CPU admission")).ToArray();
        try
        {
            for (var poll = 0; poll < 5; poll++)
            {
                clock.Advance(4);
                using var excess = scope.TryAcquire(1024, true);
                Require(excess is null && controller.Snapshot() is { ActiveFiles: 256, AllowedFiles: 256 },
                    "100% total machine CPU alone does not serialize Performance network transfers");
            }
        }
        finally { foreach (var lease in leases) lease.Complete(true); }
        Require(controller.Snapshot().ActiveFiles == 0, "CPU-heavy Performance transfers drain every lease");

        // The throughput-based limit is still real: higher concurrency must earn
        // its place, even though total machine CPU no longer blocks that probe.
        clock = new Clock();
        var probePressure = new SyncResourcePressure(1, .5, 512L * 1024 * 1024);
        controller = new(new(ResourceMode: "Performance", TransferReservationFloorBytes: 16L * 1024 * 1024), clock,
            () => probePressure);
        scope = controller.ForEndpoints([]);
        // Real memory admission initially clips Performance to16; when memory
        // becomes available the existing useful-throughput probe must still grow.
        using (var warmup = scope.TryAcquire(1024, true)
               ?? throw new Exception("Performance memory-constrained warmup")) warmup.Complete(true);
        Require(controller.Snapshot().AllowedFiles == 16, "memory admission, not an Auto cold-start, sets the probe baseline");
        probePressure = new(1, .5, 8L * 1024 * 1024 * 1024);
        void Sample()
        {
            var batch = Enumerable.Range(0, controller.Snapshot().AllowedFiles).Select(_ =>
                scope.TryAcquire(1024, true) ?? throw new Exception("Performance throughput probe")).ToArray();
            foreach (var lease in batch) { clock.Advance(.2); lease.Complete(true); }
        }
        Sample();
        Require(controller.Snapshot().AllowedFiles == 24,
            "Performance probes useful concurrency under high total machine CPU");
        Sample();
        Require(controller.Snapshot().AllowedFiles == 16 && controller.Snapshot().Reason.Contains("lighter"),
            "Performance retracts an unhelpful throughput probe instead of blindly maximizing workers");
        var learned = controller.Snapshot();
        using (var inspection = scope.TryAcquireInspection())
            Require(inspection is not null && controller.Snapshot().AllowedFiles == 16 &&
                    controller.Snapshot().AllowedInspections == 256 && controller.Snapshot().Reason == learned.Reason,
                "Inspection's current-memory allowance leaves a measured transfer plateau and probe state unchanged");
        probePressure = new(1, .995, 128L * 1024 * 1024);
        clock.Advance(4);
        using (var inspection = scope.TryAcquireInspection())
            Require(inspection is not null && controller.Snapshot().AllowedInspections == 1,
                "A learned Performance plateau still respects later critical-memory inspection pressure");
        probePressure = new(1, .5, 8L * 1024 * 1024 * 1024);
        clock.Advance(4);
        var transferLimit = controller.Snapshot().AllowedFiles;
        using (var inspection = scope.TryAcquireInspection())
            Require(inspection is not null && controller.Snapshot().AllowedInspections == 256 &&
                    controller.Snapshot().AllowedFiles == transferLimit,
                "Read-only pressure recovery never overwrites the transfer learner's separate limit");
    }

    private static void ProviderIsolation(string mode)
    {
        var clock = new Clock();
        var controller = new SyncTransferResourceController(new(ResourceMode: mode, MaximumConcurrentFiles: 4), clock, () => Calm);
        var dropbox = controller.ForEndpoints(["dropbox:a"]);
        var drive = controller.ForEndpoints(["drive:b"]);
        var cross = controller.ForEndpoints(["dropbox:a", "drive:b"]);
        controller.ProviderThrottled(new("dropbox:a", SyncProviderOperation.Read), TimeSpan.FromSeconds(20));
        Require(dropbox.TryAcquire(1, true) is null && cross.TryAcquire(1, true) is null,
            "a shared provider cooldown blocks either endpoint of a cloud pair");
        using var independent = drive.TryAcquire(1, true);
        Require(independent is not null, "another account/provider can keep transferring during Dropbox cooldown");
        clock.Advance(10); controller.ProviderThrottled(new("dropbox:a", SyncProviderOperation.Read), TimeSpan.FromSeconds(30));
        clock.Advance(15);
        Require(controller.ProviderDelay(new("dropbox:a", SyncProviderOperation.Read)) == TimeSpan.FromSeconds(15), "a later throttle extends, never shortens, the account cooldown");
        clock.Advance(15);
        using var resumed = dropbox.TryAcquire(1, true);
        Require(resumed is not null, "provider work resumes only after the exact deadline");
        using var third = drive.TryAcquire(1, true);
        using var fourth = drive.TryAcquire(1, true);
        Require(drive.TryAcquire(1, true) is null && controller.Snapshot().ActiveFiles == 4,
            "all pairs share one aggregate file allowance");
        controller.Update(new(MaximumConcurrentFiles: 1));
        Require(controller.Snapshot().ActiveFiles == 4 && drive.TryAcquire(1, true) is null,
            "reducing the ceiling drains admitted work rather than revoking it");
    }

    private static void ProviderBurstIsolation(string mode)
    {
        var clock = new Clock();
        var controller = new SyncTransferResourceController(new(ResourceMode: mode, MaximumConcurrentFiles: 64),
            clock, () => Calm);
        var initial = controller.Snapshot().AllowedFiles;
        var throttled = controller.ForEndpoints(["dropbox:burst"]);
        var independent = controller.ForEndpoints(["drive:independent"]);
        var cross = controller.ForEndpoints(["dropbox:burst", "drive:independent"]);
        var reduced = initial / 2;
        for (var response = 0; response < 10; response++)
            controller.ProviderThrottled(new("dropbox:burst", SyncProviderOperation.Read), TimeSpan.FromSeconds(300));
        Require(controller.ProviderDelay(new("dropbox:burst", SyncProviderOperation.Read)) == TimeSpan.FromSeconds(300) &&
                throttled.TryAcquire(1, true) is null && cross.TryAcquire(1, true) is null,
            mode + " ten responses in one burst retain the full server cooldown and both endpoint gates");
        Require(Admissions(independent) == initial,
            mode + " one account's throttling does not shrink another account's actual admissions");
        for (var response = 0; response < 64; response++) controller.ProviderSucceeded(new("dropbox:burst", SyncProviderOperation.Read));
        clock.Advance(1);
        controller.ProviderThrottled(new("dropbox:burst", SyncProviderOperation.Read), TimeSpan.FromSeconds(300));
        clock.Advance(299);
        Require(controller.ProviderDelay(new("dropbox:burst", SyncProviderOperation.Read)) == TimeSpan.FromSeconds(1) &&
                throttled.TryAcquire(1, true) is null,
            mode + " a late burst response extends the exact wait without admitting work early");
        clock.Advance(1);
        Require(Admissions(throttled) == reduced,
            mode + " one cooldown burst produces one reduction; in-flight successes cannot undo it");
        for (var response = 0; response < 32; response++) controller.ProviderSucceeded(new("dropbox:burst", SyncProviderOperation.Read));
        Require(Admissions(throttled) == reduced,
            mode + " a post-cooldown response burst cannot immediately erase learned pacing");
        clock.Advance(29);
        for (var response = 0; response < 320; response++) controller.ProviderSucceeded(new("dropbox:burst", SyncProviderOperation.Read));
        Require(Admissions(throttled) == reduced,
            mode + " capacity cannot regrow before the sustained quiet interval");
        clock.Advance(1);
        var recovered = initial;
        Require(Admissions(throttled) == recovered,
            mode + " 30 quiet seconds double capacity without requiring successful responses");
        for (var response = 0; response < 320; response++) controller.ProviderSucceeded(new("dropbox:burst", SyncProviderOperation.Read));
        Require(Admissions(throttled) == recovered,
            mode + " a probe cannot repeat within the same quiet interval");
        controller.ProviderThrottled(new("dropbox:burst", SyncProviderOperation.Read), TimeSpan.FromSeconds(10));
        for (var response = 0; response < 9; response++)
            controller.ProviderThrottled(new("dropbox:burst", SyncProviderOperation.Read), TimeSpan.FromSeconds(10));
        clock.Advance(10);
        Require(Admissions(throttled) == recovered / 2,
            mode + " a genuinely new throttling episode makes one additional reduction, not none or ten");
        Require(controller.Snapshot().ActiveFiles == 0,
            mode + " burst rejection and probing release every exact admission without revoking live work");

        static int Admissions(SyncTransferSchedulingScope scope)
        {
            var leases = new List<SyncTransferResourceController.Lease>();
            try
            {
                for (var index = 0; index < 64; index++)
                {
                    var lease = scope.TryAcquire(1, true);
                    if (lease is null) break;
                    leases.Add(lease);
                }
                return leases.Count;
            }
            finally { foreach (var lease in leases) lease.Dispose(); }
        }
    }

    private static async Task SchedulerAsync(string mode, SyncResourcePressure pressure)
    {
        var controller = new SyncTransferResourceController(new(ResourceMode: mode, MaximumConcurrentFiles: 3), new Clock(), () => pressure);
        var reports = new List<SyncTransferResourceStatus>();
        var scope = controller.ForEndpoints([], status => { reports.Add(status); return Task.CompletedTask; });
        var snapshots = Enumerable.Range(0, 12).Select(index => Snapshot(index.ToString())).ToArray();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var active = 0; var completed = 0;
        var work = SyncAuthorityOperationScheduler.RunAsync(snapshots, item => item, async _ =>
        {
            if (Interlocked.Increment(ref active) == 3) started.TrySetResult();
            try { await release.Task; return true; }
            finally { Interlocked.Decrement(ref active); Interlocked.Increment(ref completed); }
        }, (_, result) => Task.FromResult(result), cancellation.Token, adaptive: scope, successful: result => result);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Require(controller.Snapshot().ActiveFiles == 3, mode + ": real production scheduler obeys aggregate adaptive admission");
        cancellation.Cancel(); release.SetResult();
        try { await work; throw new Exception("cancel should surface"); } catch (OperationCanceledException) { }
        Require(active == 0 && completed == 3 && controller.Snapshot().ActiveFiles == 0 && reports.Count > 0,
            "cancellation drains every admitted effect and releases every resource lease");
        var failures = 0; var accepted = 0;
        await SyncAuthorityOperationScheduler.RunAsync(snapshots, item => item,
            item => Task.FromResult(item.OperationId != "0"), (_, result) =>
            { accepted++; if (!result) failures++; return Task.FromResult(true); }, CancellationToken.None,
            adaptive: scope, successful: result => result);
        Require(accepted == 12 && failures == 1 && controller.Snapshot().ActiveFiles == 0,
            "a safely isolated failed file does not starve unrelated work or leak its allowance");
        Require(!SyncAuthorityOperationScheduler.Independent(snapshots[0], snapshots[1] with { Members = snapshots[0].Members }),
            "adaptive admission leaves identity and dependency fences intact");
    }

    private static async Task BandwidthReservationAsync()
    {
        var settings = new SyncTransferSettings(UploadKiBPerSecond: 32);
        var limiter = new SyncTransferRateLimiter(settings);
        var watch = Stopwatch.StartNew();
        var first = limiter.ThrottleAsync(SyncTransferDirection.Upload, 4096).AsTask();
        limiter.Update(settings);
        var second = limiter.ThrottleAsync(SyncTransferDirection.Upload, 4096).AsTask();
        await Task.WhenAll(first, second);
        Require(watch.Elapsed >= TimeSpan.FromMilliseconds(235), "reading Settings cannot erase the first outstanding bandwidth reservation");
    }

    private static async Task DropboxRouteLanesAsync()
    {
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "lane-fixture", "root", "home", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes).Normalize();
        var reads = new[] { "files/download", "files/get_metadata", "files/list_folder", "files/list_folder/continue",
            "files/list_revisions", "files/upload_session/finish_batch/check", "files/delete_batch/check",
            "files/create_folder_batch/check" };
        var writes = new[] { "files/upload", "files/upload_session/start", "files/upload_session/append_v2",
            "files/upload_session/finish", "files/upload_session/finish_batch_v2", "files/copy_v2", "files/move_v2",
            "files/delete_v2", "files/create_folder_v2", "files/delete_batch", "files/create_folder_batch" };
        foreach (var name in reads.Concat(writes))
        {
            var expected = reads.Contains(name) ? SyncProviderOperation.Read : SyncProviderOperation.Write;
            var route = DropboxRouteTable.Embedded.Find(name) ?? throw new Exception("missing route " + name);
            var limiter = new SyncTransferRateLimiter(new());
            var key = "dropbox:" + binding.ConnectionId;
            // A misclassified call cannot reach the handler: the other lane is in a long cooldown.
            limiter.Resources.ProviderThrottled(new(key, expected == SyncProviderOperation.Read
                ? SyncProviderOperation.Write : SyncProviderOperation.Read), TimeSpan.FromMinutes(6));
            var handler = new RouteLaneHandler(request =>
            {
                Require(request.RequestUri?.AbsolutePath == "/2/" + name, "expected Dropbox route " + name);
                Require(limiter.Resources.ProviderDelay(new(key, expected)) == TimeSpan.Zero,
                    name + " uses the " + expected + " lane");
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
                response.Headers.TryAddWithoutValidation("Dropbox-API-Result", "{}");
                return response;
            });
            using var http = new HttpClient(handler);
            var client = new DropboxPilotClient(http, new Tokens(), "/Fixture", binding, transferLimiter: limiter);
            using var input = new MemoryStream([1, 2, 3]);
            using var output = new MemoryStream();
            var call = new DropboxRouteCall(route, "{}", ContentIn: route.Style == DropboxRouteStyle.Upload ? input : null,
                ContentOut: route.Style == DropboxRouteStyle.Download ? output : null);
            using var result = await client.CallRouteAsync(call, new(0, 0, 0, TimeSpan.FromSeconds(1)), default);
            Require(handler.Calls == 1, name + " sends once on its own lane despite opposite-lane throttling");
        }
    }

    private static async Task AccountThrottleScopeAsync()
    {
        foreach (var scenario in new[] { "too_many_requests", "unknown", "malformed", "too_many_write_operations", "server", "google",
            "flat_write", "summary_write", "nested_read_conflict" })
        foreach (var operation in new[] { SyncProviderOperation.Read, SyncProviderOperation.Write })
        {
            var limiter = new SyncTransferRateLimiter(new());
            var handler = new Handler(_ =>
            {
                var response = scenario == "google"
                    ? Error(HttpStatusCode.Forbidden, "userRateLimitExceeded")
                    : new HttpResponseMessage(scenario == "server" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.TooManyRequests)
                    { Content = new StringContent(scenario switch
                    {
                        "malformed" => "not-json",
                        "flat_write" => """{"error":{".tag":"too_many_write_operations"}}""",
                        "summary_write" => """{"error_summary":"too_many_write_operations/..."}""",
                        "nested_read_conflict" => """{"error_summary":"too_many_write_operations/...","error":{"reason":{".tag":"too_many_requests"},".tag":"too_many_write_operations","retry_after":1}}""",
                        _ => JsonSerializer.Serialize(new {
                            error_summary = scenario + "/...",
                            error = new { reason = new Dictionary<string, string> { [".tag"] = scenario }, retry_after = 1 }
                        })
                    }) };
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(1));
                return response;
            });
            using var http = new HttpClient(handler);
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://fixture.invalid/route");
            using var received = await SyncProviderRequestPacing.SendAsync(limiter, new("scope", operation),
                scenario == "google", http, request, HttpCompletionOption.ResponseHeadersRead, default);
            var readDelay = limiter.Resources.ProviderDelay(new("scope", SyncProviderOperation.Read));
            var writeDelay = limiter.Resources.ProviderDelay(new("scope", SyncProviderOperation.Write));
            Require(writeDelay > TimeSpan.Zero && (scenario is "too_many_write_operations" or "flat_write" or "summary_write"
                ? readDelay == TimeSpan.Zero : readDelay > TimeSpan.Zero),
                scenario + " on " + operation + " applies the correct cross-lane cooldown scope");
        }
    }

    private sealed class RouteLaneHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(response(request));
        }
    }

    private static async Task ProviderOperationIsolationAsync()
    {
        var clock = new Clock();
        var controller = new SyncTransferResourceController(new(ResourceMode: "Performance", MaximumConcurrentFiles: 16),
            clock, () => Calm);
        var read = new SyncProviderBudgetKey("dropbox:same-account", SyncProviderOperation.Read);
        var write = new SyncProviderBudgetKey(read.Provider, SyncProviderOperation.Write);
        var reads = controller.ForOperations([read]);
        var writes = controller.ForOperations([write]);
        for (var episode = 0; episode < 4; episode++)
        {
            controller.ProviderThrottled(write, TimeSpan.FromSeconds(10));
            Require(Count(reads) == 16 && Count(writes) == 0,
                "write throttles leave same-account download admission unchanged");
            clock.Advance(10);
        }
        Require(Count(writes) == 1, "separate write episodes reduce only the write window to one");
        for (var expected = 2; expected <= 16; expected *= 2)
        {
            clock.Advance(30);
            Require(Count(writes) == expected && Count(reads) == 16,
                "quiet write budget doubles back to its previous window without any successful responses");
        }
        controller.ProviderThrottled(read, TimeSpan.FromSeconds(10));
        Require(Count(reads) == 0 && Count(writes) == 16,
            "a read throttle stops reads without reducing write admission");
        clock.Advance(10);
        Require(Count(reads) == 8, "read throttle halves the read window after cooldown");

        // The HTTP FIFO is also split: a queued write must not hold up a read request.
        var pacing = new SyncTransferResourceController(new(), TimeProvider.System, () => Calm);
        pacing.ProviderThrottled(write, TimeSpan.FromMinutes(1));
        using var cancel = new CancellationTokenSource();
        var blockedWrite = pacing.WaitForRequestAsync(write, cancel.Token);
        await pacing.WaitForRequestAsync(read, default).WaitAsync(TimeSpan.FromSeconds(2));
        Require(!blockedWrite.IsCompleted, "reads bypass a same-account write cooldown/FIFO");
        cancel.Cancel();
        try { await blockedWrite; throw new Exception("expected cancellation"); }
        catch (OperationCanceledException) { }

        static int Count(SyncTransferSchedulingScope scope)
        {
            var leases = new List<SyncTransferResourceController.Lease>();
            try
            {
                while (leases.Count < 32 && scope.TryAcquire(1, true) is { } lease) leases.Add(lease);
                return leases.Count;
            }
            finally { foreach (var lease in leases) lease.Dispose(); }
        }
    }

    private static async Task ProviderRequestFifoAsync()
    {
        foreach (var cancelled in new[] { -1, 0, 9 })
        {
            var controller = new SyncTransferResourceController(new(), TimeProvider.System, () => Calm);
            controller.ProviderThrottled(new("fifo", SyncProviderOperation.Read), TimeSpan.FromMilliseconds(200));
            using var cancellation = new CancellationTokenSource();
            var order = new List<int>();
            async Task Wait(int index)
            {
                try
                {
                    await controller.WaitForRequestAsync(new("fifo", SyncProviderOperation.Read), index == cancelled ? cancellation.Token : default);
                    lock (order) order.Add(index);
                }
                catch (OperationCanceledException) when (index == cancelled) { }
            }
            var waiters = Enumerable.Range(0, 20).Select(Wait).ToArray();
            cancellation.Cancel();
            if (cancelled >= 0)
                await waiters[cancelled].WaitAsync(TimeSpan.FromSeconds(2));
            await controller.WaitForRequestAsync(new("independent", SyncProviderOperation.Read), default).WaitAsync(TimeSpan.FromSeconds(2));
            await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(5));
            Require(order.SequenceEqual(Enumerable.Range(0, 20).Where(i => i != cancelled)),
                $"20 request tickets preserve FIFO; cancelled ticket {cancelled} frees its turn without overtaking");
        }
        var failed = new SyncTransferResourceController(new(), TimeProvider.System, () => Calm);
        failed.ProviderThrottled(new("failure", SyncProviderOperation.Read), TimeSpan.FromMinutes(9));
        for (var i = 0; i < 2; i++)
        {
            try { await failed.WaitForRequestAsync(new("failure", SyncProviderOperation.Read), default).WaitAsync(TimeSpan.FromSeconds(2)); throw new Exception("expected backoff"); }
            catch (HttpRequestException) { }
        }
        Require(true, "failed pacing ticket releases its successor without swallowing long backoff");
    }

    private static async Task ProviderRequestsAsync()
    {
        foreach (var reason in new[] { "userRateLimitExceeded", "rateLimitExceeded", "insufficientPermissions", "dailyLimitExceeded" })
        {
            using var response = Error(HttpStatusCode.Forbidden, reason);
            Require(await SyncProviderRequestPacing.IsGoogleQuotaAsync(response, default) ==
                (reason is "userRateLimitExceeded" or "rateLimitExceeded"), "Google403 classification: " + reason);
        }
        var binding = new GoogleDriveConnectionBinding(GoogleDriveProviderConfiguration.RegisteredClientId,
            GoogleDriveConnectionBinding.CreateAccountKey("resource-fixture"), "resource-fixture", "fixture@example.invalid",
            "Fixture", GoogleDriveProviderConfiguration.RequiredScopes).Normalize();
        foreach (var quota in new[] { false, true })
        {
            var handler = new Handler(call => call == 1 ? Error(HttpStatusCode.Forbidden,
                quota ? "userRateLimitExceeded" : "insufficientPermissions", TimeSpan.FromMilliseconds(100)) :
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"user\":{\"permissionId\":\"resource-fixture\"}}") });
            using var http = new HttpClient(handler);
            var client = new GoogleDrivePilotClient(http, new Tokens(), binding, "folder", "/Fixture");
            try { await client.VerifyAccountAsync(); Require(quota, "permission403 must remain a failure"); }
            catch (HttpRequestException) when (!quota) { }
            Require(handler.Calls == (quota ? 2 : 1), "real Google adapter retries quota403, not permission403");
        }
        var longWait = TimeSpan.FromMinutes(9);
        var longHandler = new Handler(_ => Error(HttpStatusCode.TooManyRequests, "rate", longWait));
        using (var http = new HttpClient(longHandler))
        {
            var client = new GoogleDrivePilotClient(http, new Tokens(), binding, "folder", "/Fixture");
            try { await client.VerifyAccountAsync(); throw new Exception("long provider delay should defer"); }
            catch (HttpRequestException error) { Require(error.Message.Contains("deferred"), "long cooldown has an actionable deferred reason"); }
            Require(longHandler.Calls == 1, "long Retry-After is not capped into an early Google retry");
        }
        // Exercise shared cooldown transmission and refusal preservation for long provider waits.
        await SharedCooldownTransmissionAsync();
        var dropboxHandler = new Handler(_ => Error(HttpStatusCode.TooManyRequests, "rate", longWait));
        using (var http = new HttpClient(dropboxHandler))
        {
            var dropboxBinding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
                "resource-fixture", "root", "home", DropboxProviderConfiguration.ContentAccess,
                DropboxProviderConfiguration.RequiredScopes).Normalize();
            var limiter = new SyncTransferRateLimiter(new());
            var client = new DropboxPilotClient(http, new Tokens(), "/Fixture", dropboxBinding, transferLimiter: limiter);
            try { await client.VerifyRootIdentityAsync(); throw new Exception("long Dropbox delay should defer"); }
            // JSON reads preserve the original provider refusal when Retry-After exceeds the
            // read budget; the contract is HTTP 429 and no early retry, not the word "deferred".
            catch (DropboxApiException error)
            {
                Require(error.StatusCode == HttpStatusCode.TooManyRequests,
                    "Dropbox long-backoff read preserves the original HTTP 429 refusal");
            }
            Require(dropboxHandler.Calls == 1, "long Retry-After is not capped into an early Dropbox retry");
            Require(limiter.Resources.ProviderDelay(new("dropbox:" + dropboxBinding.ConnectionId,
                    SyncProviderOperation.Read)) > TimeSpan.FromMinutes(8),
                "Dropbox read lane retains the nine-minute cooldown after the refusal");
        }
    }

    private static async Task SharedCooldownTransmissionAsync()
    {
        var limiter = new SyncTransferRateLimiter(new());
        var pacedHandler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var pacedHttp = new HttpClient(pacedHandler);
        limiter.Resources.ProviderThrottled(new("one", SyncProviderOperation.Read), TimeSpan.FromMilliseconds(150));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://fixture.invalid/read");
        var watch = Stopwatch.StartNew();
        using var paced = await SyncProviderRequestPacing.SendAsync(limiter, new("one", SyncProviderOperation.Read), false, pacedHttp, request,
            HttpCompletionOption.ResponseHeadersRead, default);
        Require(pacedHandler.Calls == 1 && watch.ElapsedMilliseconds >= 130, "real HTTP seam waits for shared cooldown before transmission");
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string reason, TimeSpan? wait = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(JsonSerializer.Serialize(new
            { error = new { errors = new[] { new { reason } } } }), Encoding.UTF8, "application/json") };
        if (wait is { } value) response.Headers.RetryAfter = new RetryConditionHeaderValue(value);
        return response;
    }
    private sealed class Handler(Func<int, HttpResponseMessage> response) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(response(Interlocked.Increment(ref Calls)));
    }
    private sealed class Tokens : IDropboxAccessTokenSource, IGoogleDriveAccessTokenSource
    { public Task<string> GetAccessTokenAsync(CancellationToken token = default) => Task.FromResult("synthetic-not-a-credential"); }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(double seconds) => _now += TimeSpan.FromSeconds(seconds);
    }
    private static SyncResourcePressure Calm => new(.1, .4, 8L * 1024 * 1024 * 1024);
    private static SyncAuthorityOperationSnapshot Snapshot(string id) => new(id,
        SyncAuthorityOperationKind.CreateLocal, SyncAuthorityOperationStatus.Ready, "pair", "object-" + id,
        1, "profile", "idempotency-" + id, "{}", null, null,
        [new("source-" + id, "pair", "object-" + id, "source", "remote", null, null, null, id + ".bin", "{\"objectKind\":\"file\",\"size\":1024}"),
         new("target-" + id, "pair", "object-" + id, "destination", "local", null, null, null, id + ".bin", "{\"present\":false}")],
        [], [], [], [], []);
    private static void Require(bool ok, string name)
    { if (!ok) throw new InvalidOperationException(name); Console.WriteLine("PASS " + name); }
}

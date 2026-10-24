using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Massdex.SyncShark;

public sealed record SyncTransferResourceStatus(string Mode, int ActiveFiles, int AllowedFiles,
    int MaximumFiles, double FilesPerSecond, double BytesPerSecond, bool Learning, string Reason)
{
    // ActiveFiles retains the aggregate admission count for existing consumers.
    // Inspection is not a synced file and has its own explicitly reported limit.
    public int ActiveTransfers { get; init; }
    public int ActiveInspections { get; init; }
    public int AllowedInspections { get; init; }
    public long AvailableMemoryBytes { get; init; }
    public string Description => $"{Mode}: {ActiveTransfers}/{AllowedFiles} file transfers active; " +
        $"{ActiveInspections}/{AllowedInspections} read-only inspections active; " +
        $"ceiling {MaximumFiles} shared admissions; {FilesPerSecond:N1} completed files/s, {BytesPerSecond / 1048576:N2} MiB/s. " +
        (Learning ? "Learning from this workload. " : "") + "Transfer admission: " + Reason;
}




internal readonly record struct SyncResourcePressure(double CpuFraction, double MemoryFraction,
    long AvailableBytes, long ProcessWorkingSetBytes = 0, double SystemCommitFraction = 0,
    long FreeCommitBytes = long.MaxValue);

// One controller per runtime, shared by every pair and both transfer directions.
// This controls admission, never authority, operation order, retry or effect replay.
public sealed class SyncTransferResourceController
{
    // This is an admission allowance for streamed buffers/handles, not the file size.
    private const long TransferMemoryAllowanceBytes = 16L * 1024 * 1024;
    private const long PerformanceMemoryReserveBytes = 256L * 1024 * 1024;
    private const long CommitReserveBytes = 512L * 1024 * 1024;
    // Memory pressure is low AVAILABLE physical memory or low FREE system commit,
    // never the system-wide physical percent used: 94% used with 0.9 GiB free can be fine.
    // A large first sync retains inventories and its prepare record; their cold working set
    // is reclaimable, not pressure. Keep working set diagnostic-only, not an admission gate.
    // Below the reduce threshold new work halves; below the minimum the window is one file.
    private const long ReduceBelowAvailableBytes = 512L * 1024 * 1024;
    private const long MinimumWindowAvailableBytes = 256L * 1024 * 1024;
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly Func<SyncResourcePressure> _pressure;
    private readonly Dictionary<SyncProviderBudgetKey, ProviderBudget> _providers = new();
    private SyncTransferSettings _settings;
    private int _active, _activeTransfers, _activeInspections, _window, _peak, _completed, _failed, _small, _previousWindow;
    private long _bytes, _reservedBytes;
    private double _previousRate, _previousSize, _filesPerSecond, _bytesPerSecond;
    private DateTimeOffset _sampleStart, _probeAfter, _lastPressureCheck, _pressureRecoveryAfter;
    private int _pressureRecoveryTarget;
    private bool _probing, _learning = true;
    private string _reason = "Starting conservatively; no measured recommendation yet.";
    private SyncResourcePressure _lastPressure;

    public SyncTransferResourceController(SyncTransferSettings settings)
        : this(settings, TimeProvider.System, new MachinePressure().Read) { }

    internal SyncTransferResourceController(SyncTransferSettings settings, TimeProvider time,
        Func<SyncResourcePressure> pressure)
    {
        _settings = SyncTransferSettings.Normalize(settings);
        _time = time; _pressure = pressure;
        _window = StartingWindow(_settings);
        _sampleStart = time.GetUtcNow();
    }

    internal static int ModeCeiling(string mode) => mode switch
    { "Performance" => 256, "Low impact" => 8, _ => 64 };
    // Auto learns upward; explicit Performance should not pay Auto's cold-start
    // ramp on every CLI invocation. Actual admission still checks memory, the
    // user ceiling and each provider's current budget before starting an effect.
    private static int StartingWindow(SyncTransferSettings settings) => settings.ResourceMode switch
    {
        "Low impact" => 2,
        "Performance" => settings.MaximumConcurrentFiles == 0
            ? ModeCeiling(settings.ResourceMode) : settings.MaximumConcurrentFiles,
        _ => 16
    };

    public void Update(SyncTransferSettings settings)
    {
        settings = SyncTransferSettings.Normalize(settings);
        lock (_gate)
        {
            if (_settings == settings) return;
            var modeChanged = _settings.ResourceMode != settings.ResourceMode;
            var ceilingChanged = _settings.MaximumConcurrentFiles != settings.MaximumConcurrentFiles;
            _settings = settings;
            if (modeChanged || settings.ResourceMode == "Performance" && ceilingChanged)
            {
                _window = StartingWindow(settings);
                // A newly enlarged window must not reuse a three-second-old
                // pressure sample to admit work above the current memory budget.
                _lastPressureCheck = default;
            }
            _window = Math.Clamp(_window, 1, Ceiling);
            _pressureRecoveryTarget = 0;
            ResetSample(); _previousRate = 0; _probing = false; _learning = true;
            _reason = "Settings changed. Admitted files finish; new work uses these limits.";
        }
    }

    private int Ceiling => _settings.MaximumConcurrentFiles == 0
        ? ModeCeiling(_settings.ResourceMode) : _settings.MaximumConcurrentFiles;

    public SyncTransferResourceStatus Snapshot()
    {
        lock (_gate) return new(_settings.ResourceMode, _active, Math.Min(_window, Ceiling), Ceiling,
            _filesPerSecond, _bytesPerSecond, _learning, _reason)
        {
            ActiveTransfers = _activeTransfers, ActiveInspections = _activeInspections,
            AllowedInspections = InspectionWindow, AvailableMemoryBytes = _lastPressure.AvailableBytes
        };
    }

    internal SyncTransferSchedulingScope ForEndpoints(IEnumerable<string> keys,
        Func<SyncTransferResourceStatus, Task>? report = null) =>
        ForOperations(keys.Select(key => new SyncProviderBudgetKey(key, SyncProviderOperation.Read)), report);

    internal SyncTransferSchedulingScope ForOperations(IEnumerable<SyncProviderBudgetKey> keys,
        Func<SyncTransferResourceStatus, Task>? report = null) => new(this,
            keys.Where(key => !string.IsNullOrWhiteSpace(key.Provider)).Distinct().ToArray(), report);
    internal Func<long, bool, Lease?> ForOperationAdmission(IEnumerable<SyncProviderBudgetKey> keys) => ForOperations(keys).TryAcquire;
    internal Lease? TryAcquire(SyncProviderBudgetKey[] keys, long bytes, bool file, bool inspection = false)
    {
        if (inspection && file) throw new ArgumentException("Inspection cannot be a transferred file.", nameof(inspection));
        lock (_gate)
        {
            CheckPressure();
            var now = _time.GetUtcNow();
            // Only the scanner's explicit read-only path uses this allowance.
            // Non-file effects (moves/deletes/directories) keep ordinary admission.
            // Recovery is local to inspection; it never resets learned transfer limits.
            if (_active >= (inspection ? InspectionWindow : Math.Min(_window, Ceiling))) return null;
            foreach (var key in keys)
            {
                var budget = Provider(key);
                if (budget.CooldownUntil > now || budget.Active >= budget.Window) return null;
            }
            var reserved = Math.Max(_settings.TransferReservationFloorBytes,
                file ? Math.Min(Math.Max(0, bytes), TransferMemoryAllowanceBytes) : 0);
            var available = Math.Max(0, Math.Min(_lastPressure.AvailableBytes - MemoryReserveBytes,
                _lastPressure.FreeCommitBytes - CommitReserveBytes));
            // Always allow one bounded stream to make progress; subsequent streams fit the actual buffer budget.
            if (_active > 0 && reserved > Math.Max(0, available - _reservedBytes)) return null;
            _reservedBytes += reserved;
            // Exclude idle time between batches from the next throughput sample.
            if (file && _activeTransfers == 0 && _completed + _failed == 0) _sampleStart = now;
            _active++;
            if (inspection) _activeInspections++;
            if (file) { _activeTransfers++; _peak = Math.Max(_peak, _activeTransfers); }
            foreach (var key in keys) Provider(key).Active++;
            return new Lease(this, keys, Math.Max(0, bytes), file, inspection, reserved);
        }
    }

    private void Complete(Lease lease, bool success)
    {
        lock (_gate)
        {
            _active--;
            _reservedBytes -= lease.ReservedBytes;
            if (lease.Inspection) _activeInspections--;
            foreach (var key in lease.Keys) Provider(key).Active--;
            if (!lease.File) return;
            _activeTransfers--;
            if (success)
            {
                _completed++; _bytes += lease.Bytes;
                if (lease.Bytes <= 1024 * 1024) _small++;
            }
            else _failed++;
            Evaluate();
        }
    }

    private void Evaluate()
    {
        var now = _time.GetUtcNow();
        var seconds = (now - _sampleStart).TotalSeconds;
        if (seconds < 3 || _completed + _failed < 4) return;
        _filesPerSecond = _completed / seconds; _bytesPerSecond = _bytes / seconds;
        var meanSize = _completed == 0 ? 0 : _bytes / (double)_completed;
        var rate = _small >= _completed * .8 ? _filesPerSecond : _bytesPerSecond;
        var changedMix = _previousSize > 0 && (meanSize > _previousSize * 2 || meanSize * 2 < _previousSize);
        var saturated = _peak >= Math.Min(_window, Ceiling);
        if (_failed > 0)
        {
            _reason = "Unsuccessful files excluded from throughput; holding concurrency until useful work is measured.";
            _probing = false; _previousRate = 0;
        }
        else if (changedMix)
        {
            _reason = "File sizes changed; measuring the new workload before another increase.";
            _previousRate = 0; _probing = false;
        }
        else if (_probing && _previousRate > 0)
        {
            if (rate < _previousRate * 1.08)
            {
                _window = _previousWindow;
                _reason = "More parallel files did not improve useful throughput by 8%; retaining the lighter setting.";
                _probeAfter = now.AddSeconds(30); _learning = false;
            }
            else _reason = "More parallel files improved useful throughput; retaining the increase.";
            _probing = false;
        }
        else if (saturated && rate > 0 && now >= _probeAfter && _window < Ceiling && !UnderPressure())
        {
            _previousWindow = _window; _previousRate = rate;
            _window = Math.Min(Ceiling, _window + Math.Max(1,
                _window / (_settings.ResourceMode == "Performance" ? 2 : 4)));
            _probing = true; _learning = true;
            _reason = "Probing a larger file batch; comparing successful throughput, not just connection count.";
        }
        else if (!saturated)
            _reason = "The queue or a dependency is limiting parallel work; extra workers would not help.";
        else if (_window >= Ceiling)
            _reason = _settings.MaximumConcurrentFiles > 0 ? "At your concurrent-file ceiling." : "At this mode's automatic ceiling; you can raise it in Settings.";
        _previousSize = meanSize;
        ResetSample();
    }

    private void ResetSample()
    {
        _sampleStart = _time.GetUtcNow(); _completed = _failed = _small = 0;
        _bytes = 0; _peak = _activeTransfers;
    }

    private long MemoryReserveBytes => _settings.ResourceMode == "Performance"
        ? PerformanceMemoryReserveBytes : MinimumWindowAvailableBytes;

    private int MemoryWindow => (int)Math.Clamp(
        (_lastPressure.AvailableBytes - MemoryReserveBytes) / _settings.TransferReservationFloorBytes, 1, 1024);

    private int CommitWindow => (int)Math.Clamp(
        (Math.Max(0, _lastPressure.FreeCommitBytes) - CommitReserveBytes) / TransferMemoryAllowanceBytes, 1, 1024);

    private int InspectionWindow => _settings.ResourceMode == "Performance" && !UnderPressure()
        ? Math.Min(Ceiling, Math.Min(MemoryWindow, CommitWindow)) : Math.Min(_window, Ceiling);

    private bool UnderPressure()
    {
        // Total machine CPU is not transfer CPU. In Performance mode, busy
        // unrelated applications must not repeatedly halve asynchronous network
        // admission down to one file. The measured-throughput probe still backs
        // off when additional parallelism does not help. Responsiveness-first
        // modes keep their CPU backoff; memory and provider limits apply to all.
        if (_settings.ResourceMode != "Performance" &&
            _lastPressure.CpuFraction > (_settings.ResourceMode == "Low impact" ? .35 : .72))
            return true;
        // Performance deliberately permits high utilisation. A percentage alone
        // says nothing about how many bounded streaming buffers still fit; do not
        // repeatedly halve its window just because other apps use most of the RAM.
        // Zero/unknown available memory stays conservative, never unlimited.
        if (_lastPressure.FreeCommitBytes < CommitReserveBytes) return true;
        return _settings.ResourceMode == "Performance"
            ? _lastPressure.AvailableBytes < PerformanceMemoryReserveBytes
            : _lastPressure.AvailableBytes is > 0 and < ReduceBelowAvailableBytes;
    }

    private void CheckPressure()
    {
        var now = _time.GetUtcNow();
        if (now - _lastPressureCheck < TimeSpan.FromSeconds(3)) return;
        _lastPressureCheck = now; _lastPressure = _pressure();
        // File transfers stream in bounded chunks. Reserve headroom for those buffers,
        // database handles and the GUI instead of assuming the whole free RAM is ours.
        if (_lastPressure.AvailableBytes > 0 || _settings.ResourceMode == "Performance")
        {
            var memoryWindow = MemoryWindow;
            if (_window > memoryWindow)
            {
                RememberPressureWindow(now);
                _window = memoryWindow;
                _reason = $"Limiting new work to {_window} concurrent files: {_lastPressure.AvailableBytes / 1048576d:N0} MiB available, " +
                    $"leaving {MemoryReserveBytes / 1048576:N0} MiB headroom with size-aware transfer reservations. " +
                    "Active files finish safely.";
            }
        }
        if (_window > CommitWindow)
        {
            RememberPressureWindow(now);
            _window = CommitWindow;
            _reason = $"Limiting new work to {_window} concurrent files: {_lastPressure.FreeCommitBytes / 1048576d:N0} MiB free system commit; reserving 512 MiB.";
        }
        if (UnderPressure())
        {
            RememberPressureWindow(now);
            _window = Math.Max(1, _window / 2); _probing = false; _previousRate = 0;
            _probeAfter = now.AddSeconds(15);
            _reason = $"Reducing new work: CPU {_lastPressure.CpuFraction:P0}, memory {_lastPressure.MemoryFraction:P0} used, " +
                $"{_lastPressure.AvailableBytes / 1073741824d:N1} GiB available, Sync Shark using " +
                $"{_lastPressure.ProcessWorkingSetBytes / 1073741824d:N1} GiB (diagnostic only), " +
                $"system commit {_lastPressure.SystemCommitFraction:P0} of limit, {_lastPressure.FreeCommitBytes / 1073741824d:N1} GiB free. Active files finish safely.";
            // Keep measuring useful work while constrained. Resetting every
            // pressure poll erased slow-file completions before the four-result
            // sample could close, leaving the displayed throughput stale forever.
            // The rejected probe baseline is already cleared above; no pressure
            // or memory limit is relaxed by retaining these measurements.
        }
        else if (_pressureRecoveryTarget > 0 && now >= _pressureRecoveryAfter)
        {
            var target = Math.Min(Ceiling, _pressureRecoveryTarget);
            var recovered = Math.Min(target, Math.Min(MemoryWindow, Math.Min(CommitWindow, _window * 2)));
            if (recovered > _window)
            {
                _window = recovered; _probing = false; _previousRate = 0;
                _reason = "Memory pressure eased for 30 seconds; restoring the previous file window.";
            }
            if (_window >= target) _pressureRecoveryTarget = 0;
            _pressureRecoveryAfter = now.AddSeconds(30);
        }
    }

    private void RememberPressureWindow(DateTimeOffset now)
    {
        _pressureRecoveryTarget = Math.Max(_pressureRecoveryTarget, _window);
        _pressureRecoveryAfter = now.AddSeconds(30);
        _probing = false; _previousRate = 0;
    }

    private ProviderBudget Provider(SyncProviderBudgetKey key)
    {
        if (!_providers.TryGetValue(key, out var value)) _providers[key] = value = new ProviderBudget();
        var now = _time.GetUtcNow();
        // Regrow on lookup so queued admissions recover even when no request completes.
        if (value.RecoveryTarget > 0 && now >= value.NextRecoveryProbe && now >= value.CooldownUntil)
        {
            value.Window = Math.Min(value.RecoveryTarget, value.Window * 2);
            value.Spacing = TimeSpan.FromMilliseconds(value.Spacing.TotalMilliseconds / 2);
            value.NextRecoveryProbe = now.AddSeconds(30);
            if (value.Window >= value.RecoveryTarget)
            {
                value.Window = 1024; // The shared measured/memory window remains authoritative.
                value.RecoveryTarget = 0;
                value.Spacing = TimeSpan.Zero;
            }
        }
        return value;
    }

    internal void ProviderThrottled(SyncProviderBudgetKey key, TimeSpan retryAfter, bool accountWide = false)
    {
        V2.RunInstrumentation.Current?.RecordThrottle();
        lock (_gate)
        foreach (var lane in accountWide
            ? new[] { new SyncProviderBudgetKey(key.Provider, SyncProviderOperation.Read),
                new SyncProviderBudgetKey(key.Provider, SyncProviderOperation.Write) }
            : new[] { key })
        {
            var value = Provider(lane);
            var delay = retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.FromSeconds(1);
            var now = _time.GetUtcNow();
            var newEpisode = value.CooldownUntil <= now;
            var until = now + delay;
            if (until > value.CooldownUntil) value.CooldownUntil = until;
            // Recovery is time-based, not response-count based: a single slow download
            // must not need 32 completions to regain capacity. Never shorten Retry-After.
            value.NextRecoveryProbe = value.CooldownUntil.AddSeconds(30);
            // Concurrent requests can return the same throttling burst. Extend every
            // server deadline, but learn one reduction per cooldown episode, not one
            // per response: ten in-flight 429s must not collapse a healthy window to1.
            if (newEpisode)
            {
                value.RecoveryTarget = Math.Max(value.RecoveryTarget, Math.Min(_window, Ceiling));
                value.Window = Math.Max(1, Math.Min(_window, value.Window) / 2);
                value.Spacing = TimeSpan.FromMilliseconds(Math.Min(2000, Math.Max(25, value.Spacing.TotalMilliseconds * 2)));
            }
        }
    }

    internal void ProviderSucceeded(SyncProviderBudgetKey key)
    {
        lock (_gate) _ = Provider(key);
    }

    internal TimeSpan ProviderDelay(SyncProviderBudgetKey key)
    {
        lock (_gate)
        {
            var value = Provider(key); var now = _time.GetUtcNow();
            return value.CooldownUntil > now ? value.CooldownUntil - now : TimeSpan.Zero;
        }
    }

    internal string? ProviderLimitReason(SyncProviderBudgetKey[] keys)
    {
        lock (_gate)
            return keys.Any(key => Provider(key).Window < Math.Min(_window, Ceiling) ||
                                   Provider(key).Spacing > TimeSpan.Zero)
                ? "A provider operation class's recent throttling is limiting its file window and request pacing; reads and writes keep separate allowances."
                : null;
    }

    internal async Task WaitForRequestAsync(SyncProviderBudgetKey key, CancellationToken token, string route = "provider")
    {
        token.ThrowIfCancellationRequested();
        var ticket = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (_gate)
        {
            var value = Provider(key);
            previous = value.RequestTail;
            // A cancelled ticket completes promptly, but its successor must still wait for all
            // older tickets. No timestamps are reserved: cooldown extensions remain authoritative.
            value.RequestTail = Task.WhenAll(previous, ticket.Task);
        }
        try
        {
            var reported = !previous.IsCompleted;
            if (reported) await SyncByteProgressStream.ReportPacerAsync(route);
            await previous.WaitAsync(token).ConfigureAwait(false);
            // Only the oldest live ticket checks the slot. Re-check after every wait:
            // a concurrent response may extend the cooldown.
            while (true)
            {
                token.ThrowIfCancellationRequested();
                TimeSpan wait;
                lock (_gate)
                {
                    var value = Provider(key); var now = _time.GetUtcNow();
                    var next = value.CooldownUntil > value.NextRequest ? value.CooldownUntil : value.NextRequest;
                    wait = next - now;
                    if (wait <= TimeSpan.Zero) { value.NextRequest = now + value.Spacing; return; }
                }
                if (wait > TimeSpan.FromMinutes(5))
                    throw new HttpRequestException($"Provider requires {wait.TotalSeconds:N0} seconds of backoff. This request was not sent; retry after the cooldown.");
                if (!reported)
                {
                    reported = true;
                    await SyncByteProgressStream.ReportPacerAsync(route);
                }
                await Task.Delay(wait > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : wait, _time, token).ConfigureAwait(false);
            }
        }
        finally { ticket.TrySetResult(); }
    }

    internal sealed class Lease(SyncTransferResourceController owner, SyncProviderBudgetKey[] keys, long bytes,
        bool file, bool inspection, long reservedBytes) : IDisposable
    {
        // The admission of the effect running in this execution flow (set by the operation scheduler), so a
        // file that has finished transferring can free it early. Completing twice is a no-op.
        internal static readonly AsyncLocal<Lease?> Current = new();
        private int _closed;
        private readonly IDisposable? _measurement = V2.RunInstrumentation.Current?.Admitted();
        internal SyncProviderBudgetKey[] Keys => keys;
        internal long Bytes => bytes;
        internal long ReservedBytes => reservedBytes;
        internal bool File => file;
        internal bool Inspection => inspection;
        internal void Complete(bool success)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            try { owner.Complete(this, success); }
            finally { _measurement?.Dispose(); }
        }
        public void Dispose() => Complete(false);
    }

    private sealed class ProviderBudget
    {
        internal Task RequestTail = Task.CompletedTask;
        internal int Active, RecoveryTarget, Window = 1024;
        internal DateTimeOffset CooldownUntil, NextRequest, NextRecoveryProbe;
        internal TimeSpan Spacing;
    }

    internal sealed class MachinePressure
    {
        private ulong _idle, _kernel, _user;
        internal SyncResourcePressure Read()
        {
            var memory = GC.GetGCMemoryInfo();
            double cpu = 0;
            if (OperatingSystem.IsWindows() && GetSystemTimes(out var idle, out var kernel, out var user))
            {
                var total = (kernel - _kernel) + (user - _user);
                if (_kernel != 0 && total > 0) cpu = 1 - (idle - _idle) / (double)total;
                _idle = idle; _kernel = kernel; _user = user;
            }
            var available = Math.Max(0, memory.TotalAvailableMemoryBytes - memory.MemoryLoadBytes);
            var load = memory.TotalAvailableMemoryBytes > 0 ? memory.MemoryLoadBytes / (double)memory.TotalAvailableMemoryBytes : 0;
            // GC memory observations are from the last collection, not a current
            // machine sample. On Windows use the live physical-memory counters.
            var nativeMemory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (OperatingSystem.IsWindows() && GlobalMemoryStatusEx(ref nativeMemory) && nativeMemory.TotalPhysical > 0)
            {
                available = (long)Math.Min(nativeMemory.AvailablePhysical, (ulong)long.MaxValue);
                load = 1 - nativeMemory.AvailablePhysical / (double)nativeMemory.TotalPhysical;
            }
            // GetPerformanceInfo reports SYSTEM commit in pages. GlobalMemoryStatusEx's
            // page-file fields can be constrained by this process's limits instead.
            double commit = 0;
            long freeCommit = long.MaxValue; // Unknown is not evidence of commit exhaustion.
            var performance = new PerformanceInformation { Size = (uint)Marshal.SizeOf<PerformanceInformation>() };
            if (OperatingSystem.IsWindows() && GetPerformanceInfo(ref performance, performance.Size) &&
                performance.CommitLimit != UIntPtr.Zero)
            {
                var limit = performance.CommitLimit.ToUInt64();
                var total = performance.CommitTotal.ToUInt64();
                commit = total / (double)limit;
                var freeBytes = (limit > total ? limit - total : 0) * (double)performance.PageSize.ToUInt64();
                freeCommit = (long)Math.Min(long.MaxValue, freeBytes);
            }
            return new(Math.Clamp(cpu, 0, 1), load, available, Environment.WorkingSet, commit, freeCommit);
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct PerformanceInformation
        {
            public uint Size;
            public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable,
                SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
            public uint HandleCount, ProcessCount, ThreadCount;
        }
        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPerformanceInfo(ref PerformanceInformation information, uint size);
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatus
        {
            public uint Length, Load;
            public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
                TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    }
}

internal sealed class SyncTransferSchedulingScope(SyncTransferResourceController controller,
    SyncProviderBudgetKey[] keys, Func<SyncTransferResourceStatus, Task>? report)
{
    internal int MaximumPending => controller.Snapshot().MaximumFiles;
    private DateTimeOffset _lastReport;
    private string? _lastReason;
    internal SyncTransferResourceController.Lease? TryAcquire(long bytes, bool file)
        => TryAcquireCore(bytes, file, inspection: false);
    internal SyncTransferResourceController.Lease? TryAcquireInspection()
        => TryAcquireCore(0, file: false, inspection: true);
    private SyncTransferResourceController.Lease? TryAcquireCore(long bytes, bool file, bool inspection)
    {
        var delay = keys.Select(controller.ProviderDelay).DefaultIfEmpty().Max();
        if (delay > TimeSpan.FromMinutes(5))
            throw new HttpRequestException($"Provider requires {delay.TotalSeconds:N0} seconds of backoff. Remaining files are deferred, not sent early.");
        return controller.TryAcquire(keys, bytes, file, inspection);
    }
    internal async Task ReportAsync()
    {
        if (report is null) return;
        var status = controller.Snapshot();
        var cooldown = keys.Select(controller.ProviderDelay).DefaultIfEmpty().Max();
        if (cooldown > TimeSpan.Zero) status = status with
        { Reason = $"This pair's provider requires a cooldown: {cooldown.TotalSeconds:N0} seconds remaining. Other providers can continue." };
        else if (controller.ProviderLimitReason(keys) is { } reason) status = status with { Reason = reason };
        if (_lastReason == status.Reason && DateTimeOffset.UtcNow - _lastReport < TimeSpan.FromSeconds(5)) return;
        _lastReport = DateTimeOffset.UtcNow; _lastReason = status.Reason;
        await report(status).ConfigureAwait(false);
    }
}

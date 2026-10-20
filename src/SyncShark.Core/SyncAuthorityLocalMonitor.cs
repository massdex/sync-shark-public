using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Massdex.SyncShark;

public sealed class SyncAuthorityLocalMonitor : IAsyncDisposable, IDisposable
{
    private const int DefaultEventQueueCapacity = 4096;
    private const int MaximumBatchSize = 1000;
    public string PairId => _pair.Id;
    private readonly SyncPair _pair;
    private readonly SyncAuthorityDatabase _authority;
    private readonly string _rootBindingId;
    private readonly bool _enableUsnJournal;
    private readonly Func<string, Task> _onDurableHint;
    private readonly Func<string, Task> _onHealthChanged;
    private readonly Channel<RawEvent> _events;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _overflowPersistenceGate = new(1, 1);
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private FileSystemWatcher? _watcher;
    private Task? _pump;
    private Task? _usnPump;
    private SyncAuthorityMonitorWriter? _writer;
    private SyncAuthorityMonitorWriter? _usnWriter;
    private long _sequence;
    private long _overflowGeneration;
    private long _committedOverflowGeneration;
    private int _overflowDurablyPending;
    private int _disposed;
    private string? _pumpFailure;
    private string? _usnFailure;

    internal static int? EventQueueCapacityForTest { get; set; }
    internal static Func<int, CancellationToken, Task>? BeforeCommitForTest { get; set; }
    internal static TimeSpan? FailureRetryDelayForTest { get; set; }

    public SyncAuthorityLocalMonitor(SyncPair pair, SyncAuthorityDatabase authority, string rootBindingId,
        Func<string, Task> onDurableHint, Func<string, Task>? onHealthChanged = null,
        SyncLocalEndpointBinding? endpoint = null, bool enableUsnJournal = true)
    {
        // A monitor is bound to ONE endpoint, not a provider combination. Keep the pair id for
        // durable hints while giving the existing watcher the exact local endpoint identity.
        _pair = endpoint is null ? pair : pair with
        {
            LocalRoot = endpoint.RootPath,
            LocalVolumeSerialNumber = endpoint.VolumeSerialNumber,
            LocalRootFileId = endpoint.RootFileId
        };
        _authority = authority;
        _rootBindingId = rootBindingId;
        _enableUsnJournal = enableUsnJournal;
        _onDurableHint = onDurableHint;
        _onHealthChanged = onHealthChanged ?? (_ => Task.CompletedTask);
        var capacity = EventQueueCapacityForTest ?? DefaultEventQueueCapacity;
        if (capacity is < 1 or > 65_536)
            throw new InvalidOperationException("The local monitor queue capacity is outside its bounded range.");
        _events = Channel.CreateBounded<RawEvent>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    public string CursorId => "monitor-local-" + Hash(_pair.Id, _rootBindingId)[..32];
    public string UsnCursorId => "monitor-usn-" + Hash(_pair.Id, _rootBindingId)[..32];
    internal SyncPair Pair => _pair;
    internal long OverflowGenerationForTest => Volatile.Read(ref _overflowGeneration);

    /// <summary>
    /// Safe, bounded text for the most recent handled pump failure (directory watcher or USN).
    /// It is published by this monitor's own fault boundary and retained across disposal so an
    /// absorbed error is never presented as success.
    /// </summary>
    public string? LastFailureReason =>
        Volatile.Read(ref _pumpFailure) ?? Volatile.Read(ref _usnFailure);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_watcher is not null) throw new InvalidOperationException("The local monitor is already active.");
        using var root = PilotDirectoryLease.Open(_pair.LocalRoot,
            new PilotDirectoryIdentity(_pair.LocalVolumeSerialNumber, _pair.LocalRootFileId));
        var identityJson = JsonSerializer.Serialize(new
        {
            volumeSerial = _pair.LocalVolumeSerialNumber.ToString("x8"),
            fileId = _pair.LocalRootFileId.ToString("x16")
        });
        var retainedDirectoryCursor = await _authority.LoadMonitorCursorAsync(CursorId, cancellationToken);
        if (retainedDirectoryCursor is null)
            await _authority.RegisterMonitorCursorAsync(CursorId, _pair.Id, _rootBindingId,
                "directory_changes", 1, identityJson, null, cancellationToken);
        else if (retainedDirectoryCursor.PairId != _pair.Id ||
                 retainedDirectoryCursor.RootBindingId != _rootBindingId ||
                 retainedDirectoryCursor.Kind != "directory_changes" ||
                 retainedDirectoryCursor.Generation != 1 ||
                 !string.Equals(retainedDirectoryCursor.SourceIdentityJson, identityJson,
                     StringComparison.Ordinal))
            throw new InvalidOperationException("The retained local monitor cursor does not match this root.");
        _writer=await _authority.BeginMonitorWriterAsync(CursorId,_pair.Id,_rootBindingId,
            "directory_changes",1,identityJson,cancellationToken);
        // Every new directory-watcher session begins with a durable full-reconciliation
        // obligation. A predecessor's continuity cannot be trusted across a restart: a session
        // that discarded a path hint and then died before its bounded background persistence ran
        // leaves the retained cursor looking healthy. Establish that uncertainty through the
        // existing cursor status and monitor-event schema BEFORE any native event is enabled, and
        // schedule the pair-dirty sentinel for the pump to commit. An in-memory flag alone would
        // not survive the restart it is meant to protect against.
        Volatile.Write(ref _overflowDurablyPending, 1);
        var sessionGeneration = Interlocked.Increment(ref _overflowGeneration);
        await _authority.SetMonitorCursorStatusAsync(_writer!, "unavailable", cancellationToken);
        _events.Writer.TryWrite(new RawEvent(Interlocked.Increment(ref _sequence), null, "overflow", null,
            sessionGeneration));

        if (_enableUsnJournal && NtfsUsnJournal.TryReadCheckpoint(root.Path, out var usn))
        {
            var usnIdentity = JsonSerializer.Serialize(new
            {
                volumeSerial = _pair.LocalVolumeSerialNumber.ToString("x8"),
                journalId = usn!.JournalId.ToString("x16")
            });
            var retained = await _authority.LoadMonitorCursorAsync(UsnCursorId, cancellationToken);
            if (retained is null)
                await _authority.RegisterMonitorCursorAsync(UsnCursorId, _pair.Id, _rootBindingId,
                    "usn", 1, usnIdentity, usn.Cursor, cancellationToken);
            else if (retained.PairId != _pair.Id || retained.RootBindingId != _rootBindingId ||
                     retained.Kind != "usn" || retained.Generation != 1)
                throw new InvalidOperationException("The retained USN cursor does not match this local root.");
            _usnWriter=await _authority.BeginMonitorWriterAsync(UsnCursorId,_pair.Id,_rootBindingId,
                "usn",1,retained?.SourceIdentityJson??usnIdentity,cancellationToken);
            _usnPump = PumpUsnAsync(_stop.Token);
        }

        var watcher = new FileSystemWatcher(root.Path)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite |
                           NotifyFilters.Size | NotifyFilters.CreationTime,
            EnableRaisingEvents = false
        };
        watcher.Created += (_, item) => Enqueue(item.FullPath, "created");
        watcher.Changed += (_, item) => Enqueue(item.FullPath, "changed");
        watcher.Deleted += (_, item) => Enqueue(item.FullPath, "deleted");
        watcher.Renamed += (_, item) =>
        {
            Enqueue(item.OldFullPath, "renamed_from", item.FullPath);
            Enqueue(item.FullPath, "renamed_to", item.OldFullPath);
        };
        watcher.Error += (_, _) => Enqueue(null, "overflow");
        _watcher = watcher;
        _pump = PumpAsync(_stop.Token);
        watcher.EnableRaisingEvents = true;
    }

    private async Task PumpUsnAsync(CancellationToken cancellationToken)
    {
        var unavailable = false;
        var failures = 0;
        var writer=_usnWriter ?? throw new InvalidOperationException("The USN writer was not bound.");
        SyncAuthorityMonitorBatch? pendingBatch=null;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                if(pendingBatch is { } retry)
                {
                    await _authority.CommitMonitorBatchAsync(writer,retry,cancellationToken);
                    pendingBatch=null;
                    if(unavailable){unavailable=false;await NotifyAsync(_onHealthChanged);}
                    if(retry.Events.Count>0)await NotifyAsync(_onDurableHint);
                    Volatile.Write(ref _usnFailure,null);failures=0;
                    continue;
                }
                var priorCursor=writer.Cursor;
                if (!NtfsUsnJournal.TryReadCheckpoint(_pair.LocalRoot, out var current))
                    throw new IOException("The NTFS USN journal became unavailable.");
                var checkpoint = current ?? throw new IOException("The NTFS USN journal returned no checkpoint.");
                NtfsUsnCheckpoint.TryParseCursor(priorCursor, out _, out var priorNext);
                var reset = checkpoint.RequiresFullReconciliation(priorCursor);
                if (!reset && checkpoint.NextUsn == priorNext)
                {
                    if (unavailable)
                    {
                        unavailable = false;
                        await NotifyAsync(_onHealthChanged);
                    }
                    Volatile.Write(ref _usnFailure, null);
                    failures = 0;
                    continue;
                }

                IReadOnlyList<string>? changedPaths = null;
                var scoped = !reset && NtfsUsnJournal.TryReadChanges(_pair.LocalRoot,
                    checkpoint with { NextUsn = priorNext }, checkpoint, out var journalChanges,
                    cancellationToken) &&
                    SyncLocalInventoryIndex.TryResolveMonitorPaths(_pair, journalChanges, out changedPaths) &&
                    changedPaths!.Count <= MaximumBatchSize;
                if (scoped && changedPaths!.Count == 0)
                {
                    // Unrelated volume changes advance this bound cursor without dirtying the pair.
                    pendingBatch=_authority.PrepareMonitorBatch(writer,[],priorCursor,checkpoint.Cursor);
                    await _authority.CommitMonitorBatchAsync(writer,pendingBatch,cancellationToken);
                    pendingBatch=null;
                    if (unavailable) { unavailable = false; await NotifyAsync(_onHealthChanged); }
                    Volatile.Write(ref _usnFailure, null);
                    failures = 0;
                    continue;
                }
                var kind = reset ? "journal_gap" : scoped ? "usn_path_changed" : "usn_advanced";
                var sourceKey = checkpoint.Cursor + ":" + kind;
                var evidence = JsonSerializer.Serialize(new
                {
                    kind,
                    priorCursor = priorCursor,
                    nextCursor = checkpoint.Cursor,
                    reconciliation = scoped ? "changed_paths" : "full_pair"
                });
                var items = scoped ? changedPaths!.Select(path => new SyncAuthorityMonitorEventInput(
                    "event-" + Hash(UsnCursorId, sourceKey + path)[..32], sourceKey + ":" + Hash(path)[..16],
                    path, kind, evidence)).ToArray() : [new SyncAuthorityMonitorEventInput(
                    "event-" + Hash(UsnCursorId, sourceKey)[..32], sourceKey, null, kind, evidence)];
                pendingBatch=_authority.PrepareMonitorBatch(writer,items,priorCursor,checkpoint.Cursor);
                await _authority.CommitMonitorBatchAsync(writer,pendingBatch,cancellationToken);
                pendingBatch=null;
                if (unavailable)
                {
                    unavailable = false;
                    await NotifyAsync(_onHealthChanged);
                }
                await NotifyAsync(_onDurableHint);
                Volatile.Write(ref _usnFailure, null);
                failures = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (SyncMonitorWriterReplacedException exception)
            {
                Volatile.Write(ref _usnFailure,SafeFailureText(exception));
                await NotifyAsync(_onHealthChanged);return;
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _usnFailure, SafeFailureText(exception));
                failures++;
                if (!unavailable)
                {
                    unavailable = true;
                    try
                    {
                        await _authority.SetMonitorCursorStatusAsync(writer, "unavailable",
                            CancellationToken.None);
                    }
                    catch { }
                    await NotifyAsync(_onHealthChanged);
                }
                await DelayBeforeRetryAsync(failures, cancellationToken);
            }
        }
    }

    private void Enqueue(string? fullPath, string kind, string? otherPath = null)
    {
        // FileSystemWatcher and watcher.Error callbacks run on a thread-pool callback. They must
        // publish a bounded hint without blocking, without durable I/O and without letting any
        // exception escape. A normalization failure becomes pair-wide overflow uncertainty
        // instead of a silently dropped hint.
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            Publish(Normalize(fullPath), kind, Normalize(otherPath));
        }
        catch
        {
            SignalOverflow();
        }
    }

    internal void EnqueueForTest(string? relativePath, string kind = "changed", string? otherPath = null)
    {
        try
        {
            Publish(relativePath is null ? null : PilotPath.NormalizeRelative(relativePath), kind,
                otherPath is null ? null : PilotPath.NormalizeRelative(otherPath));
        }
        catch
        {
            SignalOverflow();
        }
    }

    private string? Normalize(string? fullPath) => fullPath is null ? null : Relative(fullPath);

    /// <summary>
    /// Bounded, nonblocking publication of one hint. The managed channel is the only
    /// synchronization point: a full queue is itself overflow evidence and never turns into a
    /// synchronous durable write or a wait inside the native callback.
    /// </summary>
    private void Publish(string? relativePath, string kind, string? otherPath)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var generation = string.Equals(kind, "overflow", StringComparison.Ordinal)
            ? RetainOverflowUncertainty()
            : 0;
        var item = new RawEvent(Interlocked.Increment(ref _sequence), relativePath, kind, otherPath,
            generation);
        if (_events.Writer.TryWrite(item)) return;
        SignalOverflow();
    }

    private long RetainOverflowUncertainty()
    {
        Volatile.Write(ref _overflowDurablyPending, 1);
        return Interlocked.Increment(ref _overflowGeneration);
    }

    private void SignalOverflow()
    {
        // The exact hint is discarded, so the pair-dirty obligation is retained atomically. The
        // monotonic overflow generation and pending flag are enough for the pump (and for the next
        // session after a process loss) to publish a pair-wide sentinel.
        var generation = RetainOverflowUncertainty();
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            _events.Writer.TryWrite(new RawEvent(Interlocked.Increment(ref _sequence), null, "overflow", null,
                generation));
        }
        catch
        {
            // A completed or disposed channel is not evidence loss: the generation and pending
            // flag above already retain the obligation for the pump or the restarting session.
        }
    }

    private string? Relative(string fullPath)
    {
        var relative = Path.GetRelativePath(_pair.LocalRoot, Path.GetFullPath(fullPath));
        if (relative == "." || relative.StartsWith(".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return null;
        return PilotPath.NormalizeRelative(relative);
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var pending = new List<RawEvent>(MaximumBatchSize + 1);
        var unavailable = false;
        var failures = 0;
        var writer=_writer ?? throw new InvalidOperationException("The watcher writer was not bound.");
        SyncAuthorityMonitorBatch? retainedBatch=null;
        RawEvent[]? retainedAdmitted=null;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (pending.Count == 0)
                {
                    var retainedOverflow = Volatile.Read(ref _overflowGeneration);
                    // A full channel may discard the overflow sentinel itself. Its
                    // retained generation must wake the pump even after the last
                    // ordinary event was drained and the sender becomes quiet.
                    if (retainedOverflow > Volatile.Read(ref _committedOverflowGeneration) ||
                        Volatile.Read(ref _overflowDurablyPending) != 0)
                        pending.Add(new RawEvent(Interlocked.Increment(ref _sequence), null, "overflow", null,
                            retainedOverflow));
                    else
                        pending.Add(await _events.Reader.ReadAsync(cancellationToken));
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
                while (pending.Count < MaximumBatchSize && _events.Reader.TryRead(out var item)) pending.Add(item);
                if (BeforeCommitForTest is { } beforeCommit)
                    await beforeCommit(Math.Min(pending.Count, MaximumBatchSize), cancellationToken);

                await _overflowPersistenceGate.WaitAsync(cancellationToken);
                try
                {
                    if(retainedBatch is null)
                    {
                    var overflowGeneration = Volatile.Read(ref _overflowGeneration);
                    // Publish a pair-wide sentinel while any overflow generation is uncommitted, and
                    // also while the durable uncertainty flag is still set: that flag covers a
                    // mandatory session-start sentinel that the bounded channel could not retain.
                    if ((overflowGeneration > Volatile.Read(ref _committedOverflowGeneration) ||
                         Volatile.Read(ref _overflowDurablyPending) != 0) &&
                        pending.All(item => !string.Equals(item.Kind, "overflow", StringComparison.Ordinal) ||
                                            item.OverflowGeneration < overflowGeneration))
                        pending.Add(new RawEvent(Interlocked.Increment(ref _sequence), null, "overflow", null,
                            overflowGeneration));

                    // FileSystemWatcher events are hints for a complete authoritative observation, not
                    // an event-sourced filesystem. Coalescing identical hints bounds database pressure
                    // without turning a dropped or repeated event into negative evidence.
                    var coalesced = pending
                        .GroupBy(item => (item.RelativePath, item.Kind, item.OtherPath))
                        .Select(SelectLatestHint)
                        .OrderBy(item => item.Sequence)
                        .ToArray();

                    // The SQLite contract admits at most 1,000 records. When the bounded raw-event
                    // set is full, reserve one of those records for pair-wide uncertainty and retain
                    // the final ordinary group for the next commit. Never construct a 1,001-record
                    // retry that the durable authority must reject forever.
                    var overflow = coalesced.LastOrDefault(item =>
                        string.Equals(item.Kind, "overflow", StringComparison.Ordinal));
                    RawEvent[] admitted;
                    if (overflow is not null && coalesced.Length > MaximumBatchSize)
                    {
                        admitted = coalesced
                            .Where(item => !string.Equals(item.Kind, "overflow", StringComparison.Ordinal))
                            .Take(MaximumBatchSize - 1)
                            .Append(overflow)
                            .OrderBy(item => item.Sequence)
                            .ToArray();
                    }
                    else
                        admitted = coalesced.Take(MaximumBatchSize).ToArray();

                    var batch = admitted.Select(item =>
                    {
                        var sourceKey = _sessionId + ":" + item.Sequence;
                        var eventJson = JsonSerializer.Serialize(new
                        {
                            item.Kind,
                            item.RelativePath,
                            item.OtherPath,
                            reconciliation = "full_pair",
                            absenceAuthority = "none",
                            overflowGeneration = item.OverflowGeneration == 0 ? (long?)null : item.OverflowGeneration
                        });
                        return new SyncAuthorityMonitorEventInput("event-" + Hash(CursorId, sourceKey)[..32],
                            sourceKey, item.RelativePath, item.Kind, eventJson);
                    }).ToArray();
                    var ordinaryCursor = admitted
                        .Where(item => !string.Equals(item.Kind, "overflow", StringComparison.Ordinal))
                        .Select(item => (long?)item.Sequence)
                        .Max();
                    var nextCursor = _sessionId + ":" +
                        (ordinaryCursor ?? admitted.Max(item => item.Sequence));
                    retainedAdmitted=admitted;
                    retainedBatch=_authority.PrepareMonitorBatch(writer,batch,writer.Cursor,nextCursor);
                    }
                    await _authority.CommitMonitorBatchAsync(writer,retainedBatch,cancellationToken);
                    var completed=retainedAdmitted!;
                    var admittedKeys = completed
                        .ToDictionary(item => (item.RelativePath, item.Kind, item.OtherPath),item=>item.Sequence);
                    var committedOverflow = completed
                        .Where(item => string.Equals(item.Kind, "overflow", StringComparison.Ordinal))
                        .Select(item => item.OverflowGeneration)
                        .DefaultIfEmpty(0)
                        .Max();
                    pending.RemoveAll(item => admittedKeys.TryGetValue((item.RelativePath, item.Kind, item.OtherPath),
                        out var through)&&item.Sequence<=through);
                    retainedBatch=null;retainedAdmitted=null;
                    if (committedOverflow > 0)
                        Volatile.Write(ref _committedOverflowGeneration,
                            Math.Max(Volatile.Read(ref _committedOverflowGeneration), committedOverflow));
                    if (Volatile.Read(ref _overflowGeneration) <=
                        Volatile.Read(ref _committedOverflowGeneration))
                        Volatile.Write(ref _overflowDurablyPending, 0);
                    else
                        await _authority.SetMonitorCursorStatusAsync(_writer!, "unavailable",
                            CancellationToken.None);
                }
                finally
                {
                    _overflowPersistenceGate.Release();
                }
                Volatile.Write(ref _pumpFailure, null);
                failures = 0;
                if (unavailable)
                {
                    unavailable = false;
                    await NotifyAsync(_onHealthChanged);
                }
                await NotifyAsync(_onDurableHint);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (SyncMonitorWriterReplacedException exception)
            {
                Volatile.Write(ref _pumpFailure,SafeFailureText(exception));
                await NotifyAsync(_onHealthChanged);return;
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _pumpFailure, SafeFailureText(exception));
                failures++;
                if (!unavailable)
                {
                    unavailable = true;
                    try
                    {
                        await _authority.SetMonitorCursorStatusAsync(_writer!, "unavailable",
                            CancellationToken.None);
                    }
                    catch { }
                    await NotifyAsync(_onHealthChanged);
                }
                await DelayBeforeRetryAsync(failures, cancellationToken);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _watcher?.Dispose();
        _watcher = null;
        _stop.Cancel();
        _events.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        // A failed pump must not escape disposal and must not be concealed as success: its owned
        // watcher/tasks are still released and the safe failure text stays readable for the owner.
        await ContainPumpAsync(_pump, usn: false);
        await ContainPumpAsync(_usnPump, usn: true);
    }

    private async Task ContainPumpAsync(Task? pump, bool usn)
    {
        if (pump is null) return;
        try
        {
            await pump;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (usn) Volatile.Write(ref _usnFailure, SafeFailureText(exception));
            else Volatile.Write(ref _pumpFailure, SafeFailureText(exception));
        }
    }

    private async Task DelayBeforeRetryAsync(int consecutiveFailures, CancellationToken cancellationToken)
    {
        // Bounded exponential backoff: a failing provider/database consumer is retried, but the
        // monitor never spins or waits indefinitely inside a handled fault.
        var delay = FailureRetryDelayForTest ?? TimeSpan.FromMilliseconds(
            Math.Min(30_000, 500 * Math.Pow(2, Math.Min(consecutiveFailures - 1, 6))));
        if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
        else await Task.Yield();
    }

    private static string SafeFailureText(Exception exception)
    {
        var text = SyncActivityText.Failure(exception);
        return text.Length <= 400 ? text : text[..400];
    }

    private async Task NotifyAsync(Func<string, Task> callback)
    {
        try { await callback(_pair.Id); }
        catch { }
    }

    private static string Hash(params string[] parts) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts)))).ToLowerInvariant();

    // Callback threads can obtain generation and sequence in different orders.
    // For overflow, persist the newest uncertainty generation, not merely the
    // last callback sequence. Acknowledgement above uses only admitted evidence.
    private static RawEvent SelectLatestHint(IEnumerable<RawEvent> hints) =>
        hints.MaxBy(item => (string.Equals(item.Kind, "overflow", StringComparison.Ordinal)
            ? item.OverflowGeneration : 0L, item.Sequence))!;

    internal static long SelectOverflowGenerationForTest(params (long Sequence, long Generation)[] hints) =>
        SelectLatestHint(hints.Select(item => new RawEvent(item.Sequence, null, "overflow", null,
            item.Generation))).OverflowGeneration;

    private sealed record RawEvent(long Sequence, string? RelativePath, string Kind, string? OtherPath,
        long OverflowGeneration = 0);
}

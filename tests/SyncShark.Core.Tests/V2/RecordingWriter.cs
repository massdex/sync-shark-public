namespace Massdex.SyncShark.V2;

/// <summary>Test-only IEndpointWriter that records call order, counts, peak concurrency and effect order over a fake.</summary>
internal sealed class RecordingWriter
    : IEndpointWriter, ISyncTransferResourceEndpoint, ILocalFreeSpaceAdmission, IRecoveryAvailability, ISourceAbsenceProbe
{
    public string? RecoveryUnavailable { get; set; }
    private readonly FakeEndpoint _inner;
    private readonly List<string>? _journal;
    private readonly object _gate = new();
    private readonly List<string> _calls = new();
    private int _active;
    private int _peak;
    private readonly TaskCompletionSource _putHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _puts, _moves, _deletes, _mkDirs, _preserves, _observes, _cancelledPuts;

    internal RecordingWriter(FakeEndpoint inner, List<string>? journal = null)
    {
        _inner = inner;
        _journal = journal;
    }

    /// <summary>When set, ObserveAsync throws as an unreachable endpoint would.</summary>
    internal bool ObserveFails { get; set; }
    internal bool AbsenceUnknown { get; set; }

    /// <summary>When set, ObserveAsync throws a cancellation the caller did not request (an endpoint timeout).</summary>
    internal bool ObserveTimesOut { get; set; }

    /// <summary>When set, PreserveAsync throws as a victim that changed since planning would.</summary>
    internal bool PreserveFailsMismatch { get; set; }

    /// <summary>
    /// The free-space verdict for each action. Null grants every action, so existing executor checks are unaffected;
    /// a test supplies a refusal or a reservation to exercise the admission path.
    /// </summary>
    internal Func<SyncAction, FreeSpaceAdmission>? FreeSpaceAdmissionFor { get; set; }
    internal Func<SyncAction, CancellationToken, Task<FreeSpaceAdmission>>? FreeSpaceAdmissionAsyncFor { get; set; }

    /// <inheritdoc />
    public Task<FreeSpaceAdmission> AdmitFreeSpaceAsync(SyncAction action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return FreeSpaceAdmissionAsyncFor?.Invoke(action, ct) ??
            Task.FromResult(FreeSpaceAdmissionFor?.Invoke(action) ?? FreeSpaceAdmission.Granted);
    }

    /// <summary>When set, a Put to any other path waits until it is cancelled (an in-flight effect the test controls).</summary>
    internal string? HoldPutsExcept { get; set; }

    /// <summary>When set, DeleteAsync at this path fails without applying.</summary>
    internal string? FailDeleteAt { get; set; }

    /// <summary>Completes when the first held Put is waiting.</summary>
    internal Task PutHeld => _putHeld.Task;

    internal int CancelledPuts => Volatile.Read(ref _cancelledPuts);
    internal int Active { get { lock (_gate) return _active; } }

    /// <inheritdoc />
    public string TransferResourceKey => "recording";

    internal IReadOnlyList<string> Calls { get { lock (_gate) return _calls.ToArray(); } }
    internal int Puts => Volatile.Read(ref _puts);
    internal int Moves => Volatile.Read(ref _moves);
    internal int Deletes => Volatile.Read(ref _deletes);
    internal int MkDirs => Volatile.Read(ref _mkDirs);
    internal int Preserves => Volatile.Read(ref _preserves);
    internal int Observes => Volatile.Read(ref _observes);
    internal int Peak { get { lock (_gate) return _peak; } }

    public Task<ItemState?> ObserveSourceMetadataAsync(string path, CancellationToken ct) => ObserveAsync(path, ct);

    public Task<bool?> IsHistoricalSourcePresentAsync(ItemState historical, CancellationToken ct)
    {
        if (ObserveFails) throw new EndpointAccessException("Source probe unavailable");
        if (ObserveTimesOut) throw new OperationCanceledException("Source probe timeout");
        return _inner.IsHistoricalSourcePresentAsync(historical, ct);
    }

    public Task<bool?> IsSourceAbsentAsync(ItemState? historical, string path, CancellationToken ct)
    {
        if (ObserveFails) throw new EndpointAccessException("Source probe unavailable");
        if (ObserveTimesOut) throw new OperationCanceledException("Source probe timeout");
        return AbsenceUnknown ? Task.FromResult<bool?>(null) : _inner.IsSourceAbsentAsync(historical, path, ct);
    }

    public async Task<ItemState> PutAsync(SyncAction a, Stream content, CancellationToken ct)
    {
        Enter("Put:" + a.Path);
        Journal("effect:" + a.ActionId);
        Interlocked.Increment(ref _puts);
        try
        {
            if (HoldPutsExcept is { } free && !string.Equals(a.Path, free, StringComparison.OrdinalIgnoreCase))
                await HoldAsync(ct);
            return await _inner.PutAsync(a, content, ct);
        }
        finally { Exit(); }
    }

    private async Task HoldAsync(CancellationToken ct)
    {
        _putHeld.TrySetResult();
        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _cancelledPuts);
            throw;
        }
    }

    public async Task<ItemState> MoveAsync(SyncAction a, CancellationToken ct)
    {
        Enter("Move:" + a.Path);
        Journal("effect:" + a.ActionId);
        Interlocked.Increment(ref _moves);
        try { return await _inner.MoveAsync(a, ct); }
        finally { Exit(); }
    }

    public async Task DeleteAsync(SyncAction a, CancellationToken ct)
    {
        Enter("Delete:" + a.Path);
        Journal("effect:" + a.ActionId);
        Interlocked.Increment(ref _deletes);
        try
        {
            if (string.Equals(a.Path, FailDeleteAt, StringComparison.OrdinalIgnoreCase))
                throw new IOException("RecordingWriter: delete '" + a.Path + "' fails by test request.");
            await _inner.DeleteAsync(a, ct);
        }
        finally { Exit(); }
    }

    public async Task<ItemState> MkDirAsync(string path, CancellationToken ct)
    {
        Enter("MkDir:" + path);
        Interlocked.Increment(ref _mkDirs);
        try { return await _inner.MkDirAsync(path, ct); }
        finally { Exit(); }
    }

    public async Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct)
    {
        Enter("Preserve:" + victim.Path);
        Interlocked.Increment(ref _preserves);
        try
        {
            if (PreserveFailsMismatch)
                throw new ExpectedTargetMismatchException("RecordingWriter: victim '" + victim.Path + "' changed");
            return await _inner.PreserveAsync(victim, actionId, ct);
        }
        finally { Exit(); }
    }

    public async Task<ItemState?> ObserveAsync(string path, CancellationToken ct)
    {
        Enter("Observe:" + path);
        Interlocked.Increment(ref _observes);
        try
        {
            if (ObserveFails) throw new IOException("RecordingWriter: observe fails by test request.");
            ct.ThrowIfCancellationRequested();
            if (ObserveTimesOut) throw new OperationCanceledException("RecordingWriter: observe timed out.");
            return await _inner.ObserveAsync(path, ct);
        }
        finally { Exit(); }
    }

    public async Task<Stream> OpenReadAsync(ItemState s, CancellationToken ct)
    {
        Enter("OpenRead:" + s.Path);
        try { return await _inner.OpenReadAsync(s, ct); }
        finally { Exit(); }
    }

    private void Enter(string call)
    {
        lock (_gate)
        {
            _calls.Add(call);
            _active++;
            if (_active > _peak) _peak = _active;
        }
    }

    private void Exit()
    {
        lock (_gate) _active--;
    }

    private void Journal(string entry)
    {
        if (_journal is null) return;
        lock (_journal) _journal.Add(entry);
    }
}

namespace Massdex.SyncShark;

internal enum ProcessMessageKind { Progress, Notice, Throttle, Pacer }

internal sealed record SyncByteProgress(string Phase, string Path, long Bytes, long? Total,
    DateTimeOffset StartedUtc, DateTimeOffset ReportedUtc, string Detail,
    long ElapsedMilliseconds = 0, string? Unit = "bytes processed", ProcessMessageKind Kind = ProcessMessageKind.Progress, string? Route = null, double WaitSeconds = 0);

// One reporting seam for native/local and provider streams. It does not own the
// stream, alter its identity/position, authorize effects, or claim server commit.
internal sealed class SyncByteProgressStream : Stream
{
    private sealed record Context(string Path, Func<SyncByteProgress, Task> Report);
    private static readonly AsyncLocal<Context?> Current = new();
    private readonly Stream _inner;
    private readonly Context? _context;
    private readonly string _transferPhase, _path;
    private readonly string? _readBackPhase;
    private string _phase;
    private bool _reading;
    private readonly long? _total;
    private DateTimeOffset _started = DateTimeOffset.UtcNow;
    private long _startedTick = Environment.TickCount64;
    private long _bytes, _reported = -1, _lastTick;

    public static IDisposable Begin(string path, Func<SyncByteProgress, Task> report)
    {
        var previous = Current.Value;
        Current.Value = new Context(path, report);
        return new Scope(() => Current.Value = previous);
    }

    public static IDisposable WithPath(string path)
    {
        var previous = Current.Value;
        if (previous is not null) Current.Value = previous with { Path = path };
        return new Scope(() => Current.Value = previous);
    }

    public static async Task ReportProcessAsync(string phase, string path, string detail, ProcessMessageKind kind = ProcessMessageKind.Progress)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            await (Current.Value?.Report(new(phase, path, 0, null, now, now, detail, 0, null, kind)) ?? Task.CompletedTask).ConfigureAwait(false);
        }
        catch (Exception) { /* Display failures must not change a sync or evidence result. */ }
    }

    internal static async Task ReportThrottleAsync(string route, TimeSpan wait)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            await (Current.Value?.Report(new("Provider cooldown", Current.Value.Path, 0, null, now, now, "", 0, null,
                ProcessMessageKind.Throttle, route, wait.TotalSeconds)) ?? Task.CompletedTask).ConfigureAwait(false);
        }
        catch (Exception) { /* Progress is best-effort: a throwing sink never fails a paced request. */ }
    }

    // A request that had to wait for the shared provider rate budget. Kept apart from a real provider throttle
    // (HTTP 429 or a 5xx with Retry-After) so run output never calls ordinary queueing a throttle.
    internal static async Task ReportPacerAsync(string route)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            await (Current.Value?.Report(new("Provider queue", Current.Value.Path, 0, null, now, now, "", 0, null,
                ProcessMessageKind.Pacer, route)) ?? Task.CompletedTask).ConfigureAwait(false);
        }
        catch (Exception) { /* Progress is best-effort: a throwing sink never fails a paced request. */ }
    }

    public static Task ReportCurrentProcessAsync(string phase, string detail, ProcessMessageKind kind = ProcessMessageKind.Progress) =>
        ReportProcessAsync(phase, Current.Value?.Path ?? "<run>", detail, kind);

    public SyncByteProgressStream(Stream inner, string phase, long? total = null, string? path = null,
        string? readBackPhase = null)
    {
        _inner = inner; _phase = _transferPhase = phase; _context = Current.Value;
        _readBackPhase = readBackPhase;
        _path = path ?? _context?.Path ?? (inner as FileStream)?.Name ?? "Current file";
        _total = total ?? (inner.CanSeek ? inner.Length - inner.Position : null);
    }

    public Task StartedAsync() => ReportAsync(true);
    private void SetDirection(bool reading)
    {
        if (_readBackPhase is null || reading == _reading) return;
        // Providers can read back a seekable download destination to verify its
        // hash. That is a separate measured phase, not a second download.
        _reading = reading;
        _phase = reading ? _readBackPhase : _transferPhase;
        _bytes = 0; _reported = -1; _lastTick = 0;
        _started = DateTimeOffset.UtcNow; _startedTick = Environment.TickCount64;
    }
    private async Task ReportAsync(bool force = false)
    {
        if (_context is null || _bytes == _reported) return;
        var tick = Environment.TickCount64;
        if (!force && _reported >= 0 && tick - _lastTick < 250) return;
        _reported = _bytes; _lastTick = tick;
        var detail = $"{_phase}: {_bytes:N0}" + (_total is { } total ? $" of {total:N0}" : "") + " bytes processed";
        if (_phase.Contains("upload", StringComparison.OrdinalIgnoreCase))
            detail += "; bytes read for the request, not yet confirmed by the destination";
        try
        {
            await _context.Report(new(_phase, _path, _bytes, _total, _started, DateTimeOffset.UtcNow, detail,
                tick - _startedTick)).ConfigureAwait(false);
        }
        catch (Exception) { /* Guard only reporting: underlying stream failures still propagate. */ }
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken token) => _inner.FlushAsync(token);
    public override int Read(byte[] buffer, int offset, int count)
    { SetDirection(true); var read = _inner.Read(buffer, offset, count); _bytes += read; V2.ReconciliationBudgetContext.EffectCurrent.Value?.RecordEffectRead(read); return read; }
    public override int Read(Span<byte> buffer)
    { SetDirection(true); var read = _inner.Read(buffer); _bytes += read; V2.ReconciliationBudgetContext.EffectCurrent.Value?.RecordEffectRead(read); return read; }
    public override void Write(byte[] buffer, int offset, int count)
    { SetDirection(false); _inner.Write(buffer, offset, count); _bytes += count; V2.ReconciliationBudgetContext.EffectCurrent.Value?.RecordEffectWrite(count); }
    public override void Write(ReadOnlySpan<byte> buffer)
    { SetDirection(false); _inner.Write(buffer); _bytes += buffer.Length; V2.ReconciliationBudgetContext.EffectCurrent.Value?.RecordEffectWrite(buffer.Length); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        if (_readBackPhase is not null && !_reading) await ReportAsync(true).ConfigureAwait(false);
        SetDirection(true);
        var read = await _inner.ReadAsync(buffer, token).ConfigureAwait(false);
        _bytes += read; V2.ReconciliationBudgetContext.EffectCurrent.Value?.RecordEffectRead(read); await ReportAsync(read == 0).ConfigureAwait(false); return read;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
        ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
    {
        SetDirection(false);
        await _inner.WriteAsync(buffer, token).ConfigureAwait(false);
        _bytes += buffer.Length; V2.ReconciliationBudgetContext.EffectCurrent.Value?.RecordEffectWrite(buffer.Length); await ReportAsync().ConfigureAwait(false);
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
        WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
    public override async ValueTask DisposeAsync()
    { await ReportAsync(true).ConfigureAwait(false); GC.SuppressFinalize(this); }
    protected override void Dispose(bool disposing) { } // The existing authority owns the underlying handle.
    private sealed class Scope(Action restore) : IDisposable
    { public void Dispose() => restore(); }
}

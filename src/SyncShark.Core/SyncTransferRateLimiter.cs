using System.Diagnostics;

namespace Massdex.SyncShark;

public enum SyncTransferDirection
{
    Upload,
    Download
}

public sealed class SyncTransferRateLimiter
{
    private readonly object _gate = new();
    private long _uploadNextTimestamp;
    private long _downloadNextTimestamp;
    private SyncTransferSettings _settings;

    public SyncTransferResourceController Resources { get; }

    public SyncTransferRateLimiter(SyncTransferSettings settings)
    {
        _settings = SyncTransferSettings.Normalize(settings);
        // The cap is process-wide. Ad-hoc clients using default settings must not reset the user's cap;
        // an explicit settings Update (including zero = default) is the configuration change boundary.
        if (_settings.UploadPayloadBudgetBytes > 0)
            UploadPayloadBudget.Capacity = _settings.UploadPayloadBudgetBytes;
        Resources = new SyncTransferResourceController(_settings);
    }

    // Focused checks share one clock between the read retry budget and the provider cooldown it waits on.
    internal SyncTransferRateLimiter(SyncTransferSettings settings, TimeProvider time,
        Func<SyncResourcePressure> pressure)
    {
        _settings = SyncTransferSettings.Normalize(settings);
        if (_settings.UploadPayloadBudgetBytes > 0)
            UploadPayloadBudget.Capacity = _settings.UploadPayloadBudgetBytes;
        Resources = new SyncTransferResourceController(_settings, time, pressure);
    }

    public void Update(SyncTransferSettings settings)
    {
        lock (_gate)
        {
            _settings = SyncTransferSettings.Normalize(settings);
            // Reading Settings must not erase outstanding reservations. A changed cap
            // affects new reservations; already reserved bytes still pay their interval.
        }
        UploadPayloadBudget.Capacity = settings.UploadPayloadBudgetBytes > 0
            ? settings.UploadPayloadBudgetBytes : V2.V2Settings.DefaultUploadPayloadBudgetBytes;
        Resources.Update(settings);
    }

    public async ValueTask ThrottleAsync(SyncTransferDirection direction, int bytes,
        CancellationToken cancellationToken = default)
    {
        if (bytes <= 0) return;
        long delayTicks;
        lock (_gate)
        {
            var kib = direction == SyncTransferDirection.Upload
                ? _settings.UploadKiBPerSecond
                : _settings.DownloadKiBPerSecond;
            if (kib == 0) return;
            var now = Stopwatch.GetTimestamp();
            var next = direction == SyncTransferDirection.Upload
                ? _uploadNextTimestamp
                : _downloadNextTimestamp;
            var start = Math.Max(now, next);
            var duration = checked((long)Math.Ceiling(bytes * (double)Stopwatch.Frequency /
                                                     (kib * 1024d)));
            next = checked(start + duration);
            if (direction == SyncTransferDirection.Upload)
                _uploadNextTimestamp = next;
            else
                _downloadNextTimestamp = next;
            // The caller has already read these bytes into its transfer buffer. Do not
            // release that buffer to the transport until the full reserved byte interval
            // has elapsed; waiting only until the interval starts would make the first and
            // final chunks unthrottled.
            delayTicks = Math.Max(0, next - now);
        }
        if (delayTicks == 0) return;
        await Task.Delay(TimeSpan.FromSeconds(delayTicks / (double)Stopwatch.Frequency), cancellationToken);
    }
}

internal sealed class ThrottledLeaveOpenReadStream(Stream inner, SyncTransferRateLimiter limiter) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        limiter.ThrottleAsync(SyncTransferDirection.Upload, read).AsTask().GetAwaiter().GetResult();
        return read;
    }
    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        limiter.ThrottleAsync(SyncTransferDirection.Upload, read).AsTask().GetAwaiter().GetResult();
        return read;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        await limiter.ThrottleAsync(SyncTransferDirection.Upload, read, cancellationToken);
        return read;
    }
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { }
}

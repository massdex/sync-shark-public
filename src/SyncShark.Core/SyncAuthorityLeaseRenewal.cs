namespace Massdex.SyncShark;

// AUD04: the single shared renewable fenced operation-lease lifetime used by every sync-effect consumer.
/// <summary>
/// The registered fenced-lease policy. Production keeps the five-minute operation lease and the one-minute
/// renewal cadence; both are settable so a focused proof can run a real executor under a short controlled
/// lease without changing the shipped defaults. The effective renewal interval never exceeds half of the
/// acquired lease duration, so even a deliberately short lease is renewed well before it expires.
/// </summary>
public static class SyncAuthorityLeasePolicy
{
    public static TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);
    public static TimeSpan RenewalInterval { get; set; } = TimeSpan.FromMinutes(1);

    internal static void ResetToDefaults()
    {
        LeaseDuration = TimeSpan.FromMinutes(5);
        RenewalInterval = TimeSpan.FromMinutes(1);
    }

    internal static TimeSpan EffectiveRenewalInterval(TimeSpan acquiredDuration)
    {
        var interval = RenewalInterval;
        var half = TimeSpan.FromTicks(Math.Max(1, acquiredDuration.Ticks / 2));
        if (interval > half) interval = half;
        if (interval < TimeSpan.FromMilliseconds(20)) interval = TimeSpan.FromMilliseconds(20);
        return interval;
    }
}

/// <summary>
/// The loss of an active operation lease. It is recorded (never thrown from the background renewal loop) so a
/// consumer can distinguish a lost lease from ordinary caller cancellation and reconcile the uncertain effect.
/// </summary>
public sealed class SyncAuthorityLeaseLostException(string operationId, string leaseId)
    : InvalidOperationException(
        $"Operation {operationId} lost its exact fenced lease {leaseId}; the in-flight effect must be reconciled.")
{
    public string OperationId { get; } = operationId;
    public string LeaseId { get; } = leaseId;
}

/// <summary>
/// One shared renewal lifetime for a fenced operation lease. It runs the conditional same-fence renewal loop for
/// the executor operation, exposes the operation's lease-aware cancellation token, and on any renewal loss stops
/// renewing, records the loss and cancels the operation's future calls. Disposal awaits the loop and stops
/// renewal before the exact lease is released.
/// </summary>
public sealed class SyncAuthorityLeaseRenewal : IAsyncDisposable
{
    private readonly SyncAuthorityDatabase _authority;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _work = new();
    private readonly CancellationTokenRegistration _callerLink;
    private readonly Task _loop;
    private int _released;
    private int _disposed;
    private int _renewals;
    private volatile bool _lost;
    private volatile Exception? _failure;

    private SyncAuthorityLeaseRenewal(SyncAuthorityDatabase authority, SyncAuthorityLease lease,
        TimeSpan interval, CancellationToken callerToken)
    {
        _authority = authority;
        Lease = lease;
        _callerLink = callerToken.CanBeCanceled
            ? callerToken.Register(static state => ((CancellationTokenSource)state!).Cancel(), _work)
            : default;
        _loop = RunAsync(interval);
    }

    public SyncAuthorityLease Lease { get; }

    /// <summary>
    /// The operation's cancellation token. It is cancelled when the caller's own token is cancelled and when
    /// renewal is lost, so a lost lease cancels every future cancellable call of the in-flight operation. A
    /// graceful release leaves it usable, because releasing this lease is not itself a lease loss.
    /// </summary>
    public CancellationToken WorkToken => _work.Token;

    public bool RenewalLost => _lost;
    public Exception? RenewalFailure => _failure;
    public int RenewalCount => Volatile.Read(ref _renewals);

    internal static SyncAuthorityLeaseRenewal Start(SyncAuthorityDatabase authority, SyncAuthorityLease lease,
        CancellationToken callerToken) => new(authority, lease,
        SyncAuthorityLeasePolicy.EffectiveRenewalInterval(lease.ExpiresUtc - lease.AcquiredUtc), callerToken);

    private async Task RunAsync(TimeSpan interval)
    {
        try
        {
            while (true)
            {
                await Task.Delay(interval, _stop.Token).ConfigureAwait(false);
                var renewed = await _authority.TryRenewOperationLeaseAsync(Lease.OperationId, Lease.WorkerId,
                    Lease, _stop.Token).ConfigureAwait(false);
                if (renewed is null)
                {
                    // A refused renewal is terminal: the exact lease is no longer current, so it is never
                    // retried, and the operation's future calls are cancelled with the loss exposed.
                    Fail(new SyncAuthorityLeaseLostException(Lease.OperationId, Lease.LeaseId));
                    return;
                }
                Interlocked.Increment(ref _renewals);
            }
        }
        catch (OperationCanceledException)
        {
            // The lifetime was stopped by disposal/release before the next renewal boundary.
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private void Fail(Exception error)
    {
        _lost = true;
        _failure = error;
        try { _work.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Stops renewal, then releases exactly this lease. A refused release propagates to the caller.</summary>
    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync().ConfigureAwait(false);
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        await _authority.ReleaseLeaseAsync(Lease, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The async-disposable lifetime boundary: renewal is stopped (and the loop awaited) before the exact lease
    /// is released. The teardown release is best effort, because the durable operation state stays authoritative.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await StopAsync().ConfigureAwait(false);
            if (Interlocked.Exchange(ref _released, 1) == 0)
                try { await _authority.ReleaseLeaseAsync(Lease, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { }
        }
        finally { _stop.Dispose(); _work.Dispose(); }
    }

    private async Task StopAsync()
    {
        try { _stop.Cancel(); }
        catch (ObjectDisposedException) { }
        try { await _loop.ConfigureAwait(false); }
        catch (Exception) { }
        _callerLink.Dispose();
    }
}

/// <summary>The shared acquisition entry point: acquire under the registered policy, then start renewal.</summary>
public static class SyncAuthorityLeaseRenewalExtensions
{
    public static async Task<SyncAuthorityLeaseRenewal> BeginFencedOperationLeaseRenewalAsync(
        this SyncAuthorityDatabase authority, string operationId, string workerId, TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var lease = await authority.AcquireOperationLeaseAsync(operationId, workerId, duration, cancellationToken)
            .ConfigureAwait(false);
        return SyncAuthorityLeaseRenewal.Start(authority, lease, cancellationToken);
    }
}

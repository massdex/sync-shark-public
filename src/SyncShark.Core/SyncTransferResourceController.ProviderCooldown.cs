namespace Massdex.SyncShark;

public sealed partial class SyncTransferResourceController
{
    private ProviderBudget Provider(SyncProviderBudgetKey key)
    {
        if (!_providers.TryGetValue(key, out var value)) _providers[key] = value = new ProviderBudget();
        var now = _time.GetUtcNow();
        // Regrow on lookup so queued admissions recover even when no request completes. Additive increase,
        // never a jump back to the previous window: a throttle-free interval buys one fixed step only.
        if (value.RecoveryTarget > 0 && now >= value.NextRecoveryProbe && now >= value.CooldownUntil)
        {
            value.Window = Math.Min(value.RecoveryTarget, value.Window + V2.V2Settings.ProviderRecoveryIncrement);
            value.Spacing = TimeSpan.FromMilliseconds(value.Spacing.TotalMilliseconds / 2);
            value.NextRecoveryProbe = now + V2.V2Settings.ProviderRecoveryInterval;
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
            if (until > value.CooldownUntil)
            {
                value.CooldownUntil = until;
                var changed = value.CooldownChanged;
                value.CooldownChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
                changed.TrySetResult();
            }
            // Recovery is time-based, not response-count based: a single slow download
            // must not need 32 completions to regain capacity. Never shorten Retry-After.
            value.NextRecoveryProbe = value.CooldownUntil + V2.V2Settings.ProviderRecoveryInterval;
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

    /// <summary>
    /// Every positive server cooldown leaves the read attempt. Even short cooldowns must not spend the
    /// transient budget; ordinary request spacing (not a server cooldown) stays in the pacing seam.
    /// </summary>
    internal static bool DefersForCooldown(TimeSpan cooldown) => cooldown > TimeSpan.Zero;

    /// <summary>
    /// Waits one server-requested provider cooldown, the single cooldown path (RQ-090). This wait is never
    /// charged to a read's transient-failure budget, and the caller's admission lease and executor worker
    /// slot are released for the whole wait and re-admitted afterwards. The cooldown itself still gates
    /// every request: re-admission and the next send both re-check <see cref="ProviderDelay"/>.
    /// </summary>
    internal async Task WaitForCooldownAsync(TimeSpan delay, string route,
        Func<TimeSpan, CancellationToken, Task>? delayAsync, CancellationToken token,
        SyncProviderBudgetKey? key = null, TimeSpan? maxBackoff = null)
    {
        if (delay <= TimeSpan.Zero) return;
        token.ThrowIfCancellationRequested();
        // The request is backing off, so it must not occupy capacity other work can use. Release the
        // admission slot (and the executor worker slot, which is released once and stays released) and
        // re-admit after the cooldown; a cancelled wait leaves the lease parked for its owner to finish.
        var lease = Lease.Current.Value;
        var staging = SyncEffectStaging.Current.Value;
        lease?.Park();
        staging?.Release();
        await SyncByteProgressStream.ReportPacerAsync(route).ConfigureAwait(false);
        var started = _time.GetUtcNow();
        var until = started + delay;
        for (var remaining = delay; remaining > TimeSpan.Zero;)
        {
            token.ThrowIfCancellationRequested();
            V2.PairPause.Check();
            ProviderThrottlePolicy.CheckWait(started, _time.GetUtcNow(), remaining,
                maxBackoff ?? ProviderThrottlePolicy.DefaultMaxBackoff, route);
            var slice = remaining < V2.V2Settings.ProviderPauseCheckInterval ? remaining : V2.V2Settings.ProviderPauseCheckInterval;
            if (delayAsync is not null) await delayAsync(slice, token).ConfigureAwait(false);
            else await Task.Delay(slice, _time, token).ConfigureAwait(false);
            remaining = until - _time.GetUtcNow();
            if (key is { } budgetKey && ProviderDelay(budgetKey) > remaining)
                remaining = ProviderDelay(budgetKey);
        }
        token.ThrowIfCancellationRequested();
        if (lease is not null) await lease.ReacquireAsync(delayAsync, _time, token, maxBackoff, route).ConfigureAwait(false);
    }

    internal void CheckCooldownBound(SyncProviderBudgetKey[] keys) =>
        ProviderThrottlePolicy.CheckDelay(keys.Select(ProviderDelay).DefaultIfEmpty().Max(),
            ProviderThrottlePolicy.DefaultMaxBackoff, "admission", _time.GetUtcNow());

    internal async Task WaitForRequestAsync(SyncProviderBudgetKey key, CancellationToken token, string route = "provider",
        bool deferProviderCooldown = false)
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
            while (!previous.IsCompleted)
            {
                Task changed;
                TimeSpan cooldown;
                lock (_gate) { cooldown = ProviderDelay(key); changed = Provider(key).CooldownChanged.Task; }
                CheckRequestCooldown(cooldown, token, route, deferProviderCooldown);
                try { await Task.WhenAny(previous, changed).WaitAsync(V2.V2Settings.ProviderPauseCheckInterval, _time, token).ConfigureAwait(false); }
                catch (TimeoutException) { } // Cooperative pause check; no reserved request time.
            }
            await previous.WaitAsync(token).ConfigureAwait(false);
            await WaitForRequestSlotAsync(key, token, route, deferProviderCooldown, reported).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A read's attempt deadline may race the cooldown wake-up. Hand the cooldown back to the
            // retry owner, which checks the user's token separately; server waiting is not transport time.
            CheckRequestCooldown(ProviderDelay(key), token, route, deferProviderCooldown);
            throw;
        }
        finally { ticket.TrySetResult(); }

    }

    private async Task WaitForRequestSlotAsync(SyncProviderBudgetKey key, CancellationToken token, string route,
        bool deferProviderCooldown, bool reported)
    {
        // Only the oldest live ticket checks the slot. Re-check after every wait:
        // a concurrent response may extend the cooldown.
        while (true)
        {
            CheckRequestCooldown(ProviderDelay(key), token, route, deferProviderCooldown);
            TimeSpan wait, cooldown;
            Task changed;
            lock (_gate)
            {
                cooldown = ProviderDelay(key);
                var value = Provider(key); var now = _time.GetUtcNow();
                var next = value.CooldownUntil > value.NextRequest ? value.CooldownUntil : value.NextRequest;
                wait = next - now;
                changed = value.CooldownChanged.Task;
                if (wait <= TimeSpan.Zero && !(deferProviderCooldown && DefersForCooldown(cooldown)))
                {
                    V2.PairPause.Check();
                    token.ThrowIfCancellationRequested();
                    value.NextRequest = now + value.Spacing;
                }
            }
            CheckRequestCooldown(cooldown, token, route, deferProviderCooldown);
            if (wait <= TimeSpan.Zero) return;
            ProviderThrottlePolicy.CheckDelay(wait, ProviderThrottlePolicy.DefaultMaxBackoff, route, _time.GetUtcNow());
            if (!reported)
            {
                reported = true;
                await SyncByteProgressStream.ReportPacerAsync(route);
            }
            try { await changed.WaitAsync(wait > V2.V2Settings.ProviderPauseCheckInterval ? V2.V2Settings.ProviderPauseCheckInterval : wait, _time, token).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
    }

    private static void CheckRequestCooldown(TimeSpan cooldown, CancellationToken token, string route, bool deferProviderCooldown)
    {
        V2.PairPause.Check();
        if (deferProviderCooldown && DefersForCooldown(cooldown))
        {
            Lease.Current.Value?.Park();
            SyncEffectStaging.Current.Value?.Release();
            throw new ProviderCooldownDeferredException(cooldown, route);
        }
        token.ThrowIfCancellationRequested();
    }

    internal sealed class Lease(SyncTransferResourceController owner, SyncProviderBudgetKey[] keys, long bytes,
        bool file, bool inspection, long reservedBytes) : IDisposable
    {
        // The admission of the effect running in this execution flow (set by the operation scheduler), so a
        // file that has finished transferring can free it early. Completing twice is a no-op.
        internal static readonly AsyncLocal<Lease?> Current = new();
        private int _closed;
        private int _holding = 1;
        private readonly IDisposable? _measurement = V2.RunInstrumentation.Current?.Admitted();
        internal SyncProviderBudgetKey[] Keys => keys;
        internal long Bytes => bytes;
        internal long ReservedBytes => reservedBytes;
        internal bool File => file;
        internal bool Inspection => inspection;
        /// <summary>Whether this lease currently accounts for an admission slot.</summary>
        internal bool Holding => Volatile.Read(ref _holding) == 1;

        /// <summary>
        /// Releases the admission slot for a server-requested cooldown without completing the lease. The
        /// owner still completes it normally, so its throughput/failure result is unchanged.
        /// </summary>
        internal void Park()
        {
            if (Volatile.Read(ref _closed) != 0) return;
            if (Interlocked.Exchange(ref _holding, 0) == 0) return;
            owner.Park(this);
        }

        /// <summary>Re-admits a parked lease once the cooldown has passed and a slot is available.</summary>
        internal async Task ReacquireAsync(Func<TimeSpan, CancellationToken, Task>? delayAsync,
            TimeProvider time, CancellationToken token, TimeSpan? maxBackoff = null, string route = "admission")
        {
            var started = time.GetUtcNow();
            while (Volatile.Read(ref _holding) == 0 && Volatile.Read(ref _closed) == 0)
            {
                token.ThrowIfCancellationRequested();
                V2.PairPause.Check();
                ProviderThrottlePolicy.CheckWait(started, time.GetUtcNow(), keys.Select(owner.ProviderDelay).DefaultIfEmpty().Max(),
                    maxBackoff ?? ProviderThrottlePolicy.DefaultMaxBackoff, route);
                bool admitted;
                lock (owner._gate) admitted = Volatile.Read(ref _closed) == 0 && owner.TryReacquireLocked(this);
                if (admitted)
                {
                    Volatile.Write(ref _holding, 1);
                    return;
                }
                if (delayAsync is not null) await delayAsync(V2.V2Settings.ProviderReacquireDelay, token).ConfigureAwait(false);
                else await Task.Delay(V2.V2Settings.ProviderReacquireDelay, time, token).ConfigureAwait(false);
            }
        }

        internal void Complete(bool success)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            try { owner.Complete(this, success); }
            finally { _measurement?.Dispose(); }
        }
        public void Dispose() => Complete(false);
    }

}

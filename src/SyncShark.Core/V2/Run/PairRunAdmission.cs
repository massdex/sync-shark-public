namespace Massdex.SyncShark.V2;

/// <summary>Pair-owned run exclusion. Only operations with no pair scope join every pair.</summary>
internal sealed class PairRunAdmission
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Task> _tails = new(StringComparer.Ordinal);
    private Task _all = Task.CompletedTask;
    private readonly AsyncLocal<Scope?> _scope = new();
    private sealed class Scope(string? pairId)
    {
        internal readonly string? PairId = pairId;
        internal bool Active = true;
    }

    // Multi-step edge commands retain the same owner's lease across preview, validation and
    // publication. Nested run calls reuse it; callers must await all child work before returning.
    internal async Task<T> WithAsync<T>(string? pairId, Func<Task<T>> body, CancellationToken ct, bool immediate = false,
        Action<string>? waitProgress = null)
    {
        using var lease = await EnterAsync(pairId, ct, immediate, waitProgress).ConfigureAwait(false);
        var previous = _scope.Value;
        var scope = new Scope(pairId);
        _scope.Value = scope;
        try { return await body().ConfigureAwait(false); }
        finally { scope.Active = false; _scope.Value = previous; }
    }

    internal Task<IDisposable> EnterAsync(string? pairId, CancellationToken ct, bool immediate = false) =>
        EnterAsync(pairId, ct, immediate, null);

    internal async Task<IDisposable> EnterAsync(string? pairId, CancellationToken ct, bool immediate,
        Action<string>? waitProgress)
    {
        ct.ThrowIfCancellationRequested();
        if (_scope.Value is { Active: true } scope)
        {
            if (scope.PairId is null || string.Equals(scope.PairId, pairId, StringComparison.Ordinal))
                return new Lease(static () => { });
            throw new InvalidOperationException("A pair-scoped command cannot acquire another pair or widen to all pairs.");
        }
        Task before;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            before = pairId is null ? Task.WhenAll(_tails.Values.Append(_all)) :
                Task.WhenAll(_all, _tails.GetValueOrDefault(pairId, Task.CompletedTask));
            if (immediate && !before.IsCompleted)
                throw new SyncCliBusyException("The requested pair scope has active work. Retry when it has drained.");
            if (pairId is null) { _all = done.Task; _tails.Clear(); }
            else _tails[pairId] = done.Task;
        }
        try
        {
            if (!before.IsCompleted && waitProgress is not null)
            {
                RunOutput.Write(waitProgress, "Waiting for the Sync Shark data lock");
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (!before.IsCompleted)
                {
                    try { await before.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
                    catch (TimeoutException)
                    { RunOutput.Write(waitProgress, $"Still waiting for the Sync Shark data lock ({(long)watch.Elapsed.TotalSeconds}s)"); }
                }
            }
            await before.WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch
        {
            // A withdrawn waiter cannot release its successors ahead of the preceding owner.
            _ = before.ContinueWith(_ => Release(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
        return new Lease(Release);

        void Release()
        {
            lock (_lock)
            {
                done.TrySetResult();
                if (pairId is not null && _tails.GetValueOrDefault(pairId) == done.Task) _tails.Remove(pairId);
            }
        }
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

namespace Massdex.SyncShark.V2;

/// <summary>
/// One seam for invoking progress, display and ambient callbacks from work code. A throwing callback is isolated
/// from the work that reported it: the work's own token still cancels, its store errors still propagate, and only
/// the callback invocation itself is caught. The first failure is traced once; later ones stay silent.
/// </summary>
internal static class ProgressGuard
{
    private static int _reported;

    /// <summary>Reports one value; only the sink call is isolated. A cancellation of the work's own token still
    /// propagates, while a sink's own cancellation is a display fault.</summary>
    internal static void Report<T>(IProgress<T>? sink, T value, CancellationToken workToken = default)
    {
        if (sink is null) return;
        try { sink.Report(value); }
        catch (OperationCanceledException) when (workToken.IsCancellationRequested) { throw; }
        catch (Exception error) { Observe(error); }
    }

    /// <summary>The line-sink form of <see cref="Report{T}"/>.</summary>
    internal static void Report(Action<string>? sink, string value, CancellationToken workToken = default)
    {
        if (sink is null) return;
        try { sink(value); }
        catch (OperationCanceledException) when (workToken.IsCancellationRequested) { throw; }
        catch (Exception error) { Observe(error); }
    }

    /// <summary>Invokes an ambient (non-progress) callback whose only effect is observation.</summary>
    internal static void Invoke(Action? callback)
    {
        if (callback is null) return;
        try { callback(); }
        catch (Exception error) { Observe(error); }
    }

    /// <summary>Invokes an ambient callback carrying one value.</summary>
    internal static void Invoke<T>(Action<T>? callback, T value)
    {
        if (callback is null) return;
        try { callback(value); }
        catch (Exception error) { Observe(error); }
    }

    /// <summary>Invokes an ambient callback carrying two values.</summary>
    internal static void Invoke<TFirst, TSecond>(Action<TFirst, TSecond>? callback, TFirst first, TSecond second)
    {
        if (callback is null) return;
        try { callback(first, second); }
        catch (Exception error) { Observe(error); }
    }

    private static void Observe(Exception error)
    {
        // One trace line per process: a display fault in a hot loop must not flood output or cost an I/O each report.
        if (Interlocked.Exchange(ref _reported, 1) != 0) return;
        RunOutput.Write(Console.Error.WriteLine,
            "Sync Shark trace: progress callback failed and was ignored: " + error.GetType().Name + ": " + error.Message);
    }
}

namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>Section 9 allocation measurement: warm the path, then report the bytes a fixed number of calls
/// allocate on this thread. Every probe measures through here, so the loop exists once.</summary>
/// <remarks>The measured call is a caller-built indexed delegate, created before the measure; the loop and the
/// warm-up allocate nothing (see <see cref="RequireAddsNothing"/>).</remarks>
internal static class AllocProbe
{
    private static readonly Action<int> Nothing = static _ => { };

    /// <summary>Warms with <paramref name="warmup"/> calls of indices 0, 1, ..., then returns the bytes the next
    /// <paramref name="calls"/> calls allocate. A probe whose input alternates reads the index; one whose input
    /// does not ignores it.</summary>
    public static long Measure(Action<int> action, int calls = 1, int warmup = 1)
    {
        for (var index = 0; index < warmup; index++) action(index);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < calls; index++) action(index);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>Fails unless complete warmed helper invocations, including their own allocations, report 0 bytes.
    /// </summary>
    public static void RequireAddsNothing()
    {
        // The first invocation initializes the helper's static delegate state, so keep it outside the bracket.
        _ = Measure(Nothing);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var single = Measure(Nothing);
        var many = Measure(Nothing, 10_000);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check.Require(single == 0 && many == 0 && allocated == 0,
            "the allocation probe's own loop allocates nothing (" + single + " B, " + many + " B, " + allocated
            + " B total)");
    }
}

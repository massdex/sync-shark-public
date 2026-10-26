namespace Massdex.SyncShark.V2;

internal static class JournalPolicy
{
    internal const long Delta = 536870912L;
    internal const long Minimum = 1024L * 1024 * 1024;
    internal const long Maximum = 8 * Minimum;
    internal static string Volume(string value)
    {
        if (value == null || value.Length != 2 || value[1] != ':' || value[0] < 'A' || value[0] > 'Z')
            throw new ArgumentException("An exact uppercase drive letter is required.");
        return value;
    }

    internal static JournalSizingState Observe(JournalSizingState? previous, ulong journal, long next, DateTimeOffset now)
    {
        if (previous is null) return new(journal, next, now);
        var hours = (now - previous.Observed).TotalHours;
        // Multiple roots and preview/apply reopen the same volume. Do not turn these into run gaps.
        if (hours < 1.0 / 60) return previous;
        var comparable = previous.JournalId == journal && next >= previous.NextUsn;
        var rate = comparable ? (next - previous.NextUsn) / hours : 0;
        return previous with { JournalId = journal, NextUsn = next, Observed = now,
            BytesPerHour = comparable ? Math.Max(rate, previous.BytesPerHour * 0.9) : 0,
            LongestGapHours = Math.Max(hours, previous.LongestGapHours * Math.Pow(0.9, hours / 24)) };
    }

    internal static long Target(JournalSizingState state, long current, long free, bool gap, long allocation = Delta)
    {
        // Match the privileged helper: capacity includes allocation overhead. Otherwise
        // the final growth step would request a maximum the helper can never satisfy.
        var cap = Math.Max(0, Math.Min(Maximum, Math.Max(0, free) / 20) - Math.Max(allocation, Delta));
        if (state.Never || current >= cap) return current;
        var window = Math.Max(7 * 24, 3 * state.LongestGapHours);
        var needed = state.BytesPerHour * window;
        if (!gap && current >= Minimum && current >= needed) return current;
        var desired = Math.Max(Minimum, Math.Max(2 * needed, gap ? 2.0 * current : 0));
        return Math.Max(current, (long)Math.Min(cap, desired));
    }

}

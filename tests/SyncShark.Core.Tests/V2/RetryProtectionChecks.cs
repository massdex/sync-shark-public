using System.Diagnostics;

namespace Massdex.SyncShark.V2;

internal static class RetryProtectionChecks
{
    internal static void Run()
    {
        LowerPredicateChecks.Run();
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Parent/active" };
        var paths = new[] { "Parent/active", "Parent/held", "Other", "" };
        var protection = RetryProtection.Create(paths, active, (_, _) => throw new Exception("Unexpected SQL fallback"));
        bool Expected(string path, bool descendants) => paths.Any(candidate => !active.Contains(candidate) &&
            (descendants ? path.Length == 0 || candidate.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase) :
                string.Equals(path, candidate, StringComparison.OrdinalIgnoreCase) ||
                PairPath.Ancestors(path).Contains(candidate, StringComparer.OrdinalIgnoreCase)));
        foreach (var path in new[] { "", "Parent", "PARENT/HELD", "Parent/held/child", "Parent/active", "Parent/held-suffix", "Other", "Other/child", "Absent" })
            foreach (var descendants in new[] { false, true })
                Require(protection(path, descendants) == Expected(path, descendants), "Retry semantics: " + path);
        var fallbackCalls = 0;
        var reports = 0;
        var fallback = RetryProtection.Create(paths, active, (_, _) => { fallbackCalls++; return true; }, pathLimit: 1,
            onFallback: () => reports++);
        Require(reports == 1, "cap fallback is reported once when built");
        Require(fallback("any", false) && fallbackCalls == 1, "Path cap preserves store fallback");
        fallback = RetryProtection.Create(paths, active, (_, _) => { fallbackCalls++; return true; }, characterLimit: 1);
        Require(fallback("any", true) && fallbackCalls == 2, "Character cap preserves store fallback");
        var enumerated = 0;
        IEnumerable<string> RetryPaths()
        {
            for (var n = 0; n < 50_000; n++) { enumerated++; yield return $"retry/{n:D6}"; }
        }
        var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var watch = Stopwatch.StartNew();
        var lookup = RetryProtection.Create(RetryPaths(), empty, (_, _) => throw new Exception("Per-item SQL regression"));
        var buildMs = watch.ElapsedMilliseconds;
        long smallMs = 0;
        foreach (var count in new[] { 100_000, 1_000_000 })
        {
            watch.Restart();
            var hits = 0;
            for (var n = 0; n < count; n++)
            {
                if (lookup($"inventory/folder/{n:D7}", false)) hits++;
                if (lookup($"retry/{n % 50_000:D6}/child", false)) hits++;
                if (lookup("retry", true)) hits++;
            }
            var elapsed = watch.ElapsedMilliseconds;
            if (count == 100_000) smallMs = elapsed;
            else Require(elapsed <= 15 * Math.Max(1, smallMs) + 100, "Million-item timing stays within 15x plus timer tolerance");
            Require(hits == count * 2 && enumerated == 50_000, "One snapshot, no retry rescans at million-item scale");
            Console.WriteLine($"RetryProtection items={count} lookups={count * 3} elapsedMs={watch.ElapsedMilliseconds} snapshotMs={buildMs} retryRowsRead={enumerated} perItemStoreCalls=0");
        }
        var before = GC.GetTotalMemory(true);
        var large = RetryProtection.Create(Enumerable.Range(0, RetryProtection.PathLimit)
            .Select(n => "retry/" + new string('x', 41) + $"{n:D6}"), empty,
            (_, _) => throw new Exception("150k snapshot unexpectedly overflowed"));
        Require(large("retry", true), "150k snapshot built");
        var retained = GC.GetTotalMemory(true) - before;
        GC.KeepAlive(large);
        GC.KeepAlive(lookup);
        Require(retained >= 0 && retained < 40_000_000, "150k near-character-cap snapshot stays below 40 MB retained memory");
        Console.WriteLine($"RetryProtection paths=150000 retainedBytes={retained}");
        Console.WriteLine("PASS RetryProtectionChecks");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

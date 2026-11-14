namespace Massdex.SyncShark.Engine.Tests;

/// <summary>The one assertion helper of the engine checks.</summary>
public static class Check
{
    public static void Require(bool condition, string what,
        [System.Runtime.CompilerServices.CallerFilePath] string file = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        if (!condition) Console.WriteLine("B212-DIAG2 REQUIRE-FAIL " + System.IO.Path.GetFileName(file) + ":" + line + " " + what);
    }

    public static bool IsSettlementStream(IReadOnlyList<RunEvent> events, Issue issue, bool isNew = true) =>
        events.Select(item => item.Kind).SequenceEqual(
            (isNew, issue.Kind == IssueKind.Failure) switch
            {
                (true, true) => new[] { RunEventKind.Issue, RunEventKind.Wait },
                (true, false) => new[] { RunEventKind.Issue },
                (false, true) => new[] { RunEventKind.Wait },
                _ => Array.Empty<RunEventKind>()
            }) &&
        events.All(item => item.Path == issue.Path && item.Cause == issue.Cause &&
            (item.Kind != RunEventKind.Issue || item.IssueKind == issue.Kind) &&
            (item.Kind != RunEventKind.Wait || item.What == issue.Cause.ToString()));

    /// <summary>True when the check just run deliberately skipped itself (scale multiplier, for example).</summary>
    public static bool Skipped { get; private set; }

    public static void Skip() => Skipped = true;

    public static void ResetSkip() => Skipped = false;
}

namespace Massdex.SyncShark.Engine.Tests.Layout;

/// <summary>Every TextKey has exactly one template and every template a key (`--engine-checks strings`).</summary>
public static class StringsChecks
{
    public static void Run()
    {
        Check.Require(Text.All.Count == Enum.GetValues<TextKey>().Length, nameof(StringsChecks));
        Check.Require(Text.All[TextKey.RequestWaiting] ==
            "Provider {Name}: waiting; current {Path}; started {Value}; elapsed {Seconds} seconds." &&
            Text.All.Where(pair => pair.Value.Contains("waiting; current {Path}", StringComparison.Ordinal)).Select(pair => pair.Key)
                .SequenceEqual([TextKey.RequestWaiting]),
            "G8: request retries reuse the unchanged RequestWaiting template, never a near-copy key");
        Check.Require(Text.Get(TextKey.OutcomeUnknown, (TextArg.Path, "Parent/Child")) ==
            "Parent/Child: outcome unknown; Sync Shark will check it again." &&
            !Text.Get(TextKey.WaitItemRetry).Contains('{'),
            "B70: unknown outcome names its path; item retry wording needs no invented deadline or delay");
    }
}

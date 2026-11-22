namespace Massdex.SyncShark.Engine.Tests.Layout;

/// <summary>Loads the real text catalog; missing, repeated or unknown keys are errors.</summary>
public static class StringsChecks
{
    public static Task RunAsync()
    {
        Check.Require(Text.All.Count == Enum.GetValues<TextKey>().Length, "Incomplete text registry");
        Check.Require(Text.Get(TextKey.InfoLinks, (TextArg.Count, "2"), (TextArg.Folders, "Top")) ==
            "2 links or shortcuts are not followed or synced, in: Top.", "Links notice placeholders");
        return Task.CompletedTask;
    }
}

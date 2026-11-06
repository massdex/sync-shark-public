namespace Massdex.SyncShark.Engine.Tests.Layout;

/// <summary>Every TextKey has exactly one template and every template a key (`--engine-checks strings`).</summary>
public static class StringsChecks
{
    public static void Run() =>
        Check.Require(Text.All.Count == Enum.GetValues<TextKey>().Length, nameof(StringsChecks));
}

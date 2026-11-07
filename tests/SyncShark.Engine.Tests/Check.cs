namespace Massdex.SyncShark.Engine.Tests;

/// <summary>The one assertion helper of the engine checks.</summary>
public static class Check
{
    public static void Require(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException(what);
    }

    /// <summary>True when the check just run deliberately skipped itself (scale multiplier, for example).</summary>
    public static bool Skipped { get; private set; }

    public static void Skip() => Skipped = true;

    public static void ResetSkip() => Skipped = false;
}

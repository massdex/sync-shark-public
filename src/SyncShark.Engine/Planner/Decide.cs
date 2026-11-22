namespace Massdex.SyncShark.Engine.Planner;

/// <summary>Select the item action from the observed row and first-run policy.</summary>
public static class Decide
{
    /// <summary>For server deletion only, pass raw local/deletion times and Row.DeletionClock to TimeRules; do not adjust client-modified times.</summary>
    public static SyncAction For(Row row, FirstRunPolicy policy, DateTimeOffset comparedAt, bool localContentDirty) => throw new NotImplementedException();
}

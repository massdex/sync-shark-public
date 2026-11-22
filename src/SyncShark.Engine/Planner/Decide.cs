namespace Massdex.SyncShark.Engine.Planner;

/// <summary>Section 6.2 decision tables;</summary>
public static class Decide
{
    /// <summary>For server deletion only, pass raw local/deletion times and Row.DeletionClock to TimeRules; do not adjust client-modified times.</summary>
    public static SyncAction For(Row row, FirstRunPolicy policy, DateTimeOffset comparedAt, bool localContentDirty) => throw new NotImplementedException();
}

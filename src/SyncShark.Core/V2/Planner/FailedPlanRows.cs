namespace Massdex.SyncShark.V2;

/// <summary>A planning failure holds the whole correspondence, including differently mapped spellings.</summary>
internal static class FailedPlanRows
{
    internal static List<(PlanRow Row, SyncAction Action)> Filter(
        IReadOnlyList<(PlanRow Row, SyncAction Action)> draft, IEnumerable<string> failedPaths)
    {
        var failures = new PendingPathIndex(failedPaths);
        bool Touches(string path) => failures.Covers(path) || failures.Contains(path);
        var failedRows = draft.Where(entry => Touches(entry.Action.Path) ||
            entry.Action.FromPath is { } from && Touches(from) ||
            entry.Action.Source is { } source && Touches(source.Path)).Select(entry => entry.Row).ToHashSet();
        return draft.Where(entry => !failedRows.Contains(entry.Row)).ToList();
    }
}

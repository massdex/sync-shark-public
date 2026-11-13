using Massdex.SyncShark.Engine.Storage;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Route-specific inputs to the single checked folder-removal protocol.</summary>
internal sealed record FolderRemovalRequest(Row Folder, Side DeletingFrom, FolderRemovalRoute Route)
{
    internal string? ExpectedRootId { get; init; }
    internal string? CounterpartPath { get; init; }
    internal WorkSelection? Selection { get; init; }
    internal IReadOnlyList<LiveEffectScope>? Footprint { get; init; }
    internal Func<Row, IAdmittedExecution?, CancellationToken, Task<bool>>? Preconditions { get; init; }
    internal Func<FolderPlan, CancellationToken, bool>? Accepts { get; init; }
    internal bool Live { get; init; }
    internal Exclusion? Exclusion { get; init; }
    internal FolderPlan? Captured { get; init; }
    internal IReadOnlySet<string>? Restored { get; init; }
    internal IReadOnlySet<string>? RestoredAncestors { get; init; }
    internal Action<Row, SyncOutcome>? Completed { get; init; }
    internal Func<Side, string, string?, bool>? Temporary { get; init; }

    internal static bool EmptyPlan(FolderPlan plan, CancellationToken ct) =>
        plan.DeleteWhole && !plan.FrozenEvidence(StateSchema.OneRow, ct).Any();
}

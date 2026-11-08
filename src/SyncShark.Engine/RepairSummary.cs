namespace Massdex.SyncShark.Engine;

/// <summary>The outcome of one Repair. Effects is keyed by EffectKind name, IssuesByKind by IssueKind name.
/// Completed = both listings finished and every row was processed. LogFailed: the run log could not be written.</summary>
public sealed record RepairSummary(string RunId, DateTimeOffset Started, DateTimeOffset Finished,
    bool Completed, bool FirstRun, string? IncompleteReason, bool RecoveredPending,
    IReadOnlyDictionary<string, int> Effects, IReadOnlyDictionary<string, int> IssuesByKind, string LogPath,
    bool LogFailed)
{
    internal bool Superseded { get; init; }
}

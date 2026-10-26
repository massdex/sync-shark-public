namespace Massdex.SyncShark.V2;

/// <summary>A bounded explicit choice checkpoint; its exact actions survive partial completion and restart.</summary>
public sealed record ConflictNoticeOperation(string Choice, string? Destination, IReadOnlyList<SyncAction> Actions,
    IReadOnlyList<string> Completed)
{
    /// <summary>Every saved step finished; a completed replace may still await the incoming item's arrival.</summary>
    public bool Finished => Actions.All(action => Completed.Contains(action.ActionId));
    // Legacy checkpoints are read once and externalized on the next save.
    public byte[]? MergeBytes { get; init; }
    public string? MergeContentId { get; init; }
    public bool Restoring { get; init; }
    public string? RestorePath { get; init; }
}

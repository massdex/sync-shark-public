namespace Massdex.SyncShark.V2;

/// <summary>How complete each side's inventory is for this run.</summary>
public sealed record Completeness(bool CompleteA, bool CompleteB, IReadOnlySet<string> IncompleteSubtreesA, IReadOnlySet<string> IncompleteSubtreesB)
{
    /// <summary>Active reconciliation scope not freshly observed by this run.</summary>
    public Func<Side, string, bool>? Unknown { get; init; }
    public IReadOnlySet<string> ExactCreationObservedA { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> ExactCreationObservedB { get; init; } = new HashSet<string>();
}

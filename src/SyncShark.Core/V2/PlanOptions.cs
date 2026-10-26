namespace Massdex.SyncShark.V2;

/// <summary>Planner inputs: the tie window, first-sync flag, label rule, label-attempt bound and any first-sync user choices.</summary>
public sealed record PlanOptions(TimeSpan TieWindow, bool FirstSync, LabelRule Label, int MaxLabelAttempts,
    IReadOnlyDictionary<string, Side?>? Winners = null, IReadOnlyList<OneSidedChoice>? OneSided = null,
    RebuildableSelection? Rebuildables = null,
    IReadOnlyDictionary<string, PrepareMatch>? MatchedAtPrepare = null,
    IReadOnlySet<string>? HeldPaths = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<ItemState> MappingContextA { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<ItemState> MappingContextB { get; init; } = [];
    // Query-backed protection keeps overflow/backoff retries out of folder collapse without loading them all.
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<string, bool>? ProtectedDescendants { get; init; }
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.MinValue;
    public string? ProviderA { get; init; }
    public string? ProviderB { get; init; }

}

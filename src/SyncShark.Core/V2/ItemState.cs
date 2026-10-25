namespace Massdex.SyncShark.V2;

/// <summary>One item's observed state on one side.</summary>
public sealed record ItemState(string Path, ItemKind Kind, string ObjectId, string? VersionId,
    string? Hash, long Size, DateTimeOffset Mtime, ItemFlags Flags, string? IdentityJson = null)
{
    /// <summary>Planner-only protection of the existing counterpart of a refused move; never persisted as a source flag.</summary>
    public bool ProviderRefusalRetained { get; init; }

    /// <summary>Read-only observation failure: this item and its subtree are kept, never inferred absent.</summary>
    public string? IncompleteReason { get; init; }

    /// <summary>A read-only sentinel whose filesystem kind could not be observed; never an effect target.</summary>
    public bool ObservationKindUnknown { get; init; }
}

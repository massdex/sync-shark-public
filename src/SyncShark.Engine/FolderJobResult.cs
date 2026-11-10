namespace Massdex.SyncShark.Engine;

/// <summary>A typed job observation, not permission to replay an effect or complete a removal root.</summary>
public abstract record FolderJobResult
{
    /// <summary>The provider job has not supplied terminal member outcomes.</summary>
    public sealed record InProgress : FolderJobResult;

    /// <summary>Terminal member evidence still requires immutable submission-link validation and guarded settlement.</summary>
    public sealed record Terminal(IReadOnlyList<FolderMemberResult> Results) : FolderJobResult;
}

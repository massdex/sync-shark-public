namespace Massdex.SyncShark.Engine;

/// <summary>New content prepared for a path but not yet published.</summary>
public interface IStaged : IAsyncDisposable
{
    string Path { get; }
    /// <summary>Exact held PC stage, null for Dropbox content.</summary>
    StageProof? Proof { get; }

    /// <summary>Whether the source must remain open until publication completes.</summary>
    bool ConsumesSourceOnPublish { get; }
}

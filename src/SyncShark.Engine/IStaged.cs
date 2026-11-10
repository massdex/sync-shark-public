namespace Massdex.SyncShark.Engine;

/// <summary>New content prepared for a path but not yet published.</summary>
public interface IStaged : IAsyncDisposable
{
    string Path { get; }

    /// <summary>Whether the source must remain open until publication completes.</summary>
    bool ConsumesSourceOnPublish { get; }
}

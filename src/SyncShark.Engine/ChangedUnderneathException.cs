namespace Massdex.SyncShark.Engine;

/// <summary>A precondition found the item changed or gone → Failure (FailChanged).</summary>
public class ChangedUnderneathException(string path, Exception? inner = null)
    : Exception(Text.Get(TextKey.FailChanged, (TextArg.Path, path)), inner)
{
    /// <summary>The item's relative path that no longer matches the expected version.</summary>
    public string Path { get; } = path;

    /// <summary>The complete selected Local content observed before any effect, when available.</summary>
    public Entry? ObservedLocal { get; init; }
}

namespace Massdex.SyncShark.Engine;

/// <summary>A create or move target is taken by another item → NameClash (ClashNameTaken).</summary>
public sealed class PathOccupiedException(string path, Side? collidingSide = null, Exception? inner = null)
    : Exception(Text.Get(TextKey.ClashNameTaken, (TextArg.Path, path)), inner)
{
    /// <summary>The path whose name was already taken.</summary>
    public string Path { get; } = path;

    /// <summary>The occupied saved key's provider, when the collision originates in Storage.</summary>
    public Side? CollidingSide { get; } = collidingSide;
}

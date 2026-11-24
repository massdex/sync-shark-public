namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The one home of the pair-root mapping between relative paths and Dropbox paths</summary>
public static class DropboxPaths
{
    /// <summary>Maps a pair-relative path to its Dropbox path.</summary>
    public static string Remote(PairConfig pair, string relative) =>
        PathNames.RootedDropboxPath(pair.DropboxRoot, relative);

    /// <summary>The pair-relative path after its one containment check; an invalid path is refused before any
    /// provider read.</summary>
    public static string ContainedRelative(string relative)
    {
        try { return PathNames.NormalizeRelative(relative); }
        catch (ArgumentException error)
        {
            throw new EngineRequestException(TextKey.ExclusionPathInvalid,
                (TextArg.Path, relative), (TextArg.Message, error.Message));
        }
    }

    /// <summary>Maps a Dropbox display path back to the pair, rejecting paths outside its root.</summary>
    public static string Relative(PairConfig pair, string pathDisplay)
    {
        var root = PathNames.TrimDropboxRoot(pair.DropboxRoot);
        if (PathNames.Same(pathDisplay, root)) return "";
        if (PathNames.RelativeUnderDropboxRoot(root, pathDisplay) is { } relative) return relative;

        // Dropbox returned an item outside the folder that was listed: the listing can't be trusted.
        throw new InventoryIncompleteException(Text.Get(TextKey.ReadDropboxFailed,
            (TextArg.Path, pathDisplay), (TextArg.Message, "")), null);
    }
}

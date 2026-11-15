using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The one home of the pair-root mapping between relative paths and Dropbox paths (section 5.2).</summary>
public static class DropboxPaths
{
    /// <summary>The Dropbox path of a relative path.</summary>
    public static string Remote(PairConfig pair, string relative) =>
        PathNames.RootedDropboxPath(pair.DropboxRoot, relative);

    /// <summary>The pair-relative path after its one containment check; an invalid path is refused before any
    /// provider read.</summary>
    public static string ContainedRelative(string relative)
    {
        try { return PathRules.NormalizeDropboxRelative(relative); }
        catch (ArgumentException error)
        {
            throw new EngineRequestException(TextKey.ExclusionPathInvalid,
                (TextArg.Path, relative), (TextArg.Message, error.Message));
        }
    }

    /// <summary>The relative path of a Dropbox path_display under the pair root.</summary>
    public static string Relative(PairConfig pair, string pathDisplay)
    {
        var root = PathNames.TrimDropboxRoot(pair.DropboxRoot);
        if (PathNames.Same(pathDisplay, root)) return "";
        if (PathNames.RelativeUnderDropboxRoot(root, pathDisplay) is { } relative) return relative;

        // Dropbox returned an item outside the folder that was listed: the listing can't be trusted.
        throw new InventoryIncompleteException(Text.Get(TextKey.ReadDropboxFailed, (TextArg.Path, pathDisplay),
            (TextArg.Message, "")), null);
    }
}

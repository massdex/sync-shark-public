using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>Local path helpers (section 4.3).</summary>
public static class LocalPaths
{
    /// <summary>Returns true only when the local root is an existing directory.</summary>
    public static bool RootExists(string root) => Directory.Exists(root);

    /// <summary>Resolves a relative path within the local root using Core's containment rules.</summary>
    public static string Full(string root, string relative)
    {
        try { return PathRules.ResolveContainedPath(root, relative); }
        catch (ArgumentException error)
        {
            throw new ItemFailedException(relative, TextKey.LocalNameInvalid, error);
        }
    }
}

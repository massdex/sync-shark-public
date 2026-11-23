namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The one home of the pair-root mapping between relative paths and Dropbox paths</summary>
public static class DropboxPaths
{
    /// <summary>Remote operation on the supplied facts.</summary>
    public static string Remote(PairConfig pair, string relative) => throw new NotImplementedException();
    /// <summary>Relative operation on the supplied facts.</summary>
    public static string Relative(PairConfig pair, string pathDisplay) => throw new NotImplementedException();

    /// <summary>ContainedRelative for the selected DropboxPaths operation.</summary>
    public static string ContainedRelative(string relative) => throw new NotImplementedException();
}

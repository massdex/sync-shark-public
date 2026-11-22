namespace Massdex.SyncShark.Engine.Local;

/// <summary>DirectoryListing operations for the local module.</summary>
public static class DirectoryListing
{
    /// <summary>Verify the held root identity and prune excluded subtrees before descending; a changed root supplies no coverage.</summary>
    public static IAsyncEnumerable<Listed> ReadAsync(string root, DirectoryIdentity expectedRoot, PathScope paths, string under, ListOptions options, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Read one entry relative to the bound root.</summary>
    public static Entry? One(string root, string relativePath) => throw new NotImplementedException();
}

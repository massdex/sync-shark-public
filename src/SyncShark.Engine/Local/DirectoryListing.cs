namespace Massdex.SyncShark.Engine.Local;

/// <summary>DirectoryListing operations for the local module.</summary>
public static class DirectoryListing
{
    /// <summary>Read operation on the supplied facts.</summary>
    public static IAsyncEnumerable<Listed> ReadAsync(string root, string under, ListOptions options, CancellationToken ct) => throw new NotImplementedException();
}

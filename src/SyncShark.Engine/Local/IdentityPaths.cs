namespace Massdex.SyncShark.Engine.Local;

/// <summary>IdentityPaths operations for the local module.</summary>
public sealed class IdentityPaths
{
    /// <summary>Locate operation on the supplied facts.</summary>
    public Task<IdentityLocation?> LocateAsync(string root, string id, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Verify the starting directory and locate the native identity in its domain.</summary>
    public Task<IdentityLocation?> LocateAsync(string root, DirectoryIdentity expectedRoot, string id, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Resolve the aliases of the selected file under the verified root; absence is distinct from incomplete evidence.</summary>
    public static (string? Path, IReadOnlyList<string>? Links) ResolveLinksById(string rootPath, DirectoryIdentity expectedRoot, ulong fileId, bool forceEnumeration, CancellationToken cancellationToken) => throw new NotImplementedException();
    /// <summary>Locate the identity once under the verified root and report its scope.</summary>
    public Task<(IdentityLocation Location, bool InScope)?> LocateInScopeAsync(string root, DirectoryIdentity expectedRoot, string id, PathScope scope, CancellationToken ct) => throw new NotImplementedException();
}

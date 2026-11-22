namespace Massdex.SyncShark.Engine.Local;

/// <summary>IdentityPaths operations for the local module.</summary>
public sealed class IdentityPaths
{
    /// <summary>Locate operation on the supplied facts.</summary>
    public Task<IdentityLocation?> LocateAsync(string root, string id, CancellationToken ct) => throw new NotImplementedException();
}

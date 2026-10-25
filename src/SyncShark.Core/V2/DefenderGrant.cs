namespace Massdex.SyncShark.V2;

public sealed record DefenderGrant(string GrantId, string OwnerObject, string? SourceObject, string? LocalObject,
    string Path, string StagePath, bool Verified = false);

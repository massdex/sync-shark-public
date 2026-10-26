namespace Massdex.SyncShark.V2;

/// <summary>One endpoint root of a pair.</summary>
public sealed record RootDef(string RootId, string Provider, string? AccountId, string RootPath, string RootObjectId);

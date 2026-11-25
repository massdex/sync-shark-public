namespace Massdex.SyncShark.Engine;

/// <summary>The two endpoint footprints compared together after a scope change.</summary>
public sealed record Scope(string LocalPath, string DropboxPath);

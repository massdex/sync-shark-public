namespace Massdex.SyncShark.Engine;

/// <summary>One file or folder present on one side. Skip != null: present but never synced (D27).</summary>
public sealed record Entry(string Path, bool IsFolder, long Size, DateTimeOffset Modified, string Id,
    string Revision, string ContentHash, string? Skip);

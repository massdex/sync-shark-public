using System.Text.Json.Serialization;

namespace Massdex.SyncShark.Engine;

/// <summary>One file or folder present on one side. Skip != null: present but never synced (D27).
/// SharedRoot: a Dropbox shared-folder mount point (B-217); false is omitted from stored JSON so saved entries keep
/// their bytes.</summary>
public sealed record Entry(string Path, bool IsFolder, long Size, DateTimeOffset Modified, string Id,
    string Revision, string ContentHash, string? Skip,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool SharedRoot = false);

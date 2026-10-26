using System.Text.Json;
using System.Text.Json.Serialization;

namespace Massdex.SyncShark;

/// <summary>The outcome of a download to --out.</summary>
public sealed record DropboxCliDownloadData(string Route, string Out, long Bytes, string? ContentHash, bool Verified,
    DropboxCliEntry? Entry,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Warnings = null)
    : IDropboxCliData;

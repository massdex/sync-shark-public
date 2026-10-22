using System.Text.Json.Serialization;

namespace Massdex.SyncShark;

/// <summary>An oversized result written to a file because it did not fit the reply.</summary>
public sealed record DropboxCliSpilledData(bool Spilled, string ResultFile, long Bytes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Warnings = null)
    : IDropboxCliData;

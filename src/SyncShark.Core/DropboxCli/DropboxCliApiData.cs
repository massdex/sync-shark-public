using System.Text.Json;
using System.Text.Json.Serialization;

namespace Massdex.SyncShark;

/// <summary>The result of one raw `dropbox api` call.</summary>
public sealed record DropboxCliApiData(string Route, string Style, string Policy, JsonElement? Result,
    string? ResultFile, string? Out, long? Bytes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Warnings = null)
    : IDropboxCliData;

using System.Text.Json;

namespace Massdex.SyncShark;

/// <summary>Dropbox's JSON answer to one route, verbatim.</summary>
public sealed record DropboxCliRawData(string Route, JsonElement Result) : IDropboxCliData;

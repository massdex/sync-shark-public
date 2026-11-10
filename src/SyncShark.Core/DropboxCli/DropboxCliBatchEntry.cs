using System.Text.Json;

namespace Massdex.SyncShark;

/// <summary>One batch job's per-entry result: success with an entry, or Dropbox's failure JSON.</summary>
public sealed record DropboxCliBatchEntry(bool Ok, DropboxCliEntry? Entry, JsonElement? Error) : IDropboxCliData;

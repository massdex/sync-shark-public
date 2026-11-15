using System.Text.Json;

namespace Massdex.SyncShark;

/// <summary>One download: the route's metadata, the bytes written, and its content-hash outcome.</summary>
public sealed record DropboxCliDownloadResult(JsonElement Metadata, long Bytes, string? ContentHash, bool Verified);

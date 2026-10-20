using System.Text.Json;

namespace Massdex.SyncShark;

/// <summary>The JSON result of one Dropbox route call and the number of content bytes transferred.</summary>
public sealed record DropboxRouteResult(JsonDocument Result, long ContentBytes) : IDisposable
{
    /// <summary>Releases the parsed result document.</summary>
    public void Dispose() => Result.Dispose();
}

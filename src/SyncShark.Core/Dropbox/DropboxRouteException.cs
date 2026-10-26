using System.Net;

namespace Massdex.SyncShark;

/// <summary>A Dropbox route refused the request with a non-success HTTP status (design dropbox-cli.md §1.3).</summary>
public sealed class DropboxRouteException(HttpStatusCode status, string errorSummary, string? errorJson)
    : HttpRequestException($"Dropbox HTTP {(int)status}: {errorSummary}", null, status)
{
    /// <summary>Dropbox's own error summary, or the raw text when the body was not JSON.</summary>
    public string ErrorSummary { get; } = errorSummary;

    /// <summary>Dropbox's structured error object as JSON, when the body was JSON.</summary>
    public string? ErrorJson { get; } = errorJson;
}

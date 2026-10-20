using System.Text.Json;

namespace Massdex.SyncShark;

/// <summary>The one mapping from a Dropbox exception to an exit code and error code.</summary>
public static class DropboxCliErrors
{
    /// <summary>Maps any exception raised while handling a Dropbox verb to a SyncShark CLI exception.</summary>
    public static SyncCliException Map(Exception error, DropboxRoute? route)
    {
        ArgumentNullException.ThrowIfNull(error);
        switch (error)
        {
            case SyncCliException cli:
                return cli;
            case DropboxCredentialUnavailableException credential:
                return new SyncCliException("reauthorize",
                    DropboxCliCatalog.Message(DropboxCliMessage.CredentialUnavailable,
                        ("account", credential.ConnectionId)),
                    SyncCliExitCode.NeedsReview, credential);
            case DropboxEffectUncertainException uncertain:
                return new SyncCliException("effect_uncertain",
                    DropboxCliCatalog.Message(DropboxCliMessage.EffectUncertain, ("route", route?.Name ?? "the route")),
                    SyncCliExitCode.Failed, uncertain,
                    new DropboxCliErrorData(route?.Name, null, SyncActivityText.Safe(uncertain.Message), null, true));
            case DropboxRouteException provider:
                return MapProvider(provider, route);
            case DropboxApiException api:
                return new SyncCliException("operation_failed", api.SafeReason, SyncCliExitCode.Failed, api);
            case JsonException json:
                return new SyncCliUsageException("The Dropbox response could not be parsed: " + json.Message);
            case IOException io:
                return new SyncCliException("operation_failed", io.Message, SyncCliExitCode.Failed, io);
            case HttpRequestException http:
                return new SyncCliException("operation_failed", SafeOutcome(http), SyncCliExitCode.Failed, http);
            default:
                return new SyncCliException("operation_failed", SyncActivityText.Safe(error.Message),
                    SyncCliExitCode.Failed, error);
        }
    }

    private static SyncCliException MapProvider(DropboxRouteException provider, DropboxRoute? route)
    {
        var status = (int)(provider.StatusCode ?? 0);
        var summary = provider.ErrorSummary;
        var (code, exit) = Classify(status, summary);
        var message = (route is null ? "" : route.Name + ": ") + "Dropbox returned HTTP " + status +
            (string.IsNullOrWhiteSpace(summary) ? "." : ": " + summary);
        return new SyncCliException(code, SyncActivityText.Safe(message), exit, provider,
            new DropboxCliErrorData(route?.Name, status, SyncActivityText.Safe(summary),
                Parse(provider.ErrorJson), false));
    }

    private static (string Code, SyncCliExitCode Exit) Classify(int status, string summary) => status switch
    {
        400 => ("bad_request", SyncCliExitCode.Usage),
        429 => ("rate_limited", SyncCliExitCode.Busy),
        401 or 403 when summary.Contains("missing_scope", StringComparison.OrdinalIgnoreCase) =>
            ("missing_scope", SyncCliExitCode.NeedsReview),
        401 or 403 => ("forbidden", SyncCliExitCode.NeedsReview),
        409 when summary.Contains("not_found", StringComparison.OrdinalIgnoreCase) =>
            ("missing_target", SyncCliExitCode.MissingTarget),
        409 when summary.Contains("conflict", StringComparison.OrdinalIgnoreCase) =>
            ("conflict", SyncCliExitCode.NeedsReview),
        >= 500 => ("operation_failed", SyncCliExitCode.Failed),
        >= 400 => ("usage", SyncCliExitCode.Usage),
        _ => ("operation_failed", SyncCliExitCode.Failed)
    };

    private static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string SafeOutcome(HttpRequestException error) =>
        error.StatusCode is { } status ? "Dropbox returned HTTP " + (int)status + "." : "Dropbox could not be reached.";
}

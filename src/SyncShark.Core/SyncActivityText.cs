using System.Text.RegularExpressions;

namespace Massdex.SyncShark;

public static class SyncActivityText
{
    public static string Failure(Exception error) => Safe(string.Join(" → ",
        ExceptionChain(error).Select(part => part.Error.GetType().Name +
            (part.Error is HttpRequestException { StatusCode: { } code } ? $" (HTTP {(int)code})" : "") +
            (part.Error is HttpRequestException request and not (DropboxApiException or GoogleDriveApiException)
                ? $" (transport {request.HttpRequestError})" : "") +
            (part.Error is System.Net.Sockets.SocketException socket
                ? $" (socket {socket.SocketErrorCode}; native {socket.NativeErrorCode})" : "") +
            $" [0x{part.Error.HResult:X8}]: " +
            (part.PrivateDetail ? "Provider transport detail withheld." : FailureMessage(part.Error)) +
            ApplicationLocation(part.Error))));

    private static string FailureMessage(Exception error) => error switch
    {
        // These sealed application exceptions carry an explicit safe reason, not a raw
        // HTTP message/body. Preserve the actionable cause; Safe still redacts credentials.
        DropboxApiException api => api.SafeReason,
        GoogleDriveApiException api => api.SafeReason,
        HttpRequestException => "The HTTP request failed.",
        _ => error.Message
    };

    private static string ApplicationLocation(Exception error)
    {
        // Method identity only: never serialize a raw stack, source path, arguments,
        // exception Data or provider response. The same safe diagnostic serves CLI and GUI.
        var methods = new System.Diagnostics.StackTrace(error, false).GetFrames()?
            .Select(frame => frame.GetMethod())
            .Where(method => method?.DeclaringType?.Namespace?.StartsWith("Massdex.SyncShark", StringComparison.Ordinal) == true)
            .Select(method => method!.DeclaringType!.FullName + "." + method.Name)
            .Distinct(StringComparer.Ordinal).Take(4).ToArray() ?? [];
        return methods.Length == 0 ? "" : " [application: " + string.Join(" <- ", methods) + "]";
    }

    private static IEnumerable<(Exception Error, bool PrivateDetail)> ExceptionChain(Exception error)
    {
        var seen = new HashSet<Exception>();
        var privateDetail = false;
        for (Exception? item = error; item is not null && seen.Add(item); item = item.InnerException)
        {
            yield return (item, privateDetail);
            // These application-owned messages are safe and actionable. Their inner provider,
            // transport or parser exception may contain arbitrary response/credential bytes.
            privateDetail |= item is DropboxEffectUncertainException or GoogleDriveEffectUncertainException or HttpRequestException;
        }
    }

    // Only presentation text reaches this boundary. Do not pass provider payloads, redirects,
    // stacks or credentials. Redaction is additional defence, not permission to read secrets.
    public static string Safe(string? value)
    {
        var text = value is { Length: > 4096 } ? value[..4096] : value ?? "";
        text = Regex.Replace(text, @"(?i)\bBearer\s+[^\s,;]+", "Bearer [redacted]");
        text = Regex.Replace(text, @"(?i)\b(access_token|refresh_token|id_token|client_secret|authorization|password|code|state)\s*[=:]\s*[^\s,;&]+", "$1=[redacted]");
        text = Regex.Replace(text, @"https?://[^\s<>]+", match =>
            Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Path) : "[link]");
        // Detailed retained records have their own bounded budget. The default 400-character
        // one-line CLI summary budget must not silently erase nested failure causes here.
        return SyncCliSanitize.Message(text, maximumLength: 4096) ?? "";
    }
}


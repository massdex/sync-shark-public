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
            (part.PrivateDetail ? "Provider transport detail withheld." : FailureMessage(part.Error)))) +
        ApplicationLocation(error));

    private static string FailureMessage(Exception error) => error switch
    {
        // These sealed application exceptions carry an explicit safe reason, not a raw
        // HTTP message/body. Preserve the actionable cause; Safe still redacts credentials.
        DropboxApiException api => api.SafeReason,
        GoogleDriveApiException api => api.SafeReason,
        HttpRequestException => "The HTTP request failed.",
        _ => error.Message
    };

    /// <summary>Returns bounded application method identities and available line numbers without source paths.</summary>
    public static string ApplicationLocation(Exception error)
    {
        // Never serialize source paths, arguments, exception Data or provider responses.
        var origins = ExceptionChain(error).Reverse().Select(part =>
            new System.Diagnostics.StackTrace(part.Error, true).GetFrames()?
                .Select(frame => (Method: frame.GetMethod(), Line: frame.GetFileLineNumber()))
                .Where(frame => frame.Method?.DeclaringType?.Namespace?.StartsWith("Massdex.SyncShark", StringComparison.Ordinal) == true)
                .Select(frame => frame.Method!.DeclaringType!.FullName + "." + frame.Method.Name +
                    (frame.Line > 0 ? ":" + frame.Line : "")).ToArray() ?? []).ToArray();
        // Give each original cause a slot before spending the existing bound on callers.
        var methods = origins.Where(frames => frames.Length != 0).Select(frames => frames[0])
            .Concat(origins.SelectMany(frames => frames)).Distinct(StringComparer.Ordinal).Take(4).ToArray();
        return methods.Length == 0 ? "" : " [application: " + string.Join(" <- ", methods) + "]";
    }

    private static IEnumerable<(Exception Error, bool PrivateDetail)> ExceptionChain(Exception error)
    {
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(Exception Error, bool PrivateDetail)>();
        pending.Push((error, false));
        while (pending.TryPop(out var part))
        {
            if (!seen.Add(part.Error)) continue;
            yield return part;
            // These application-owned messages are safe; their descendants may contain provider payloads.
            var privateDetail = part.PrivateDetail || part.Error is DropboxEffectUncertainException or
                GoogleDriveEffectUncertainException or HttpRequestException;
            if (part.Error is AggregateException aggregate)
            {
                for (var index = aggregate.InnerExceptions.Count - 1; index >= 0; index--)
                    pending.Push((aggregate.InnerExceptions[index], privateDetail));
            }
            else if (part.Error.InnerException is { } inner) pending.Push((inner, privateDetail));
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


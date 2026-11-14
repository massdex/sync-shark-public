using System.Globalization;
using System.Text.Json;

namespace Massdex.SyncShark;

// The running-application bridge protocol. The transport is the existing same-product window-message
// boundary; this file owns the bounded, validated envelope so both processes and the focused checks
// share exactly one contract.
public sealed record SyncCliBridgeRequest(string Kind, string Nonce, DateTimeOffset IssuedUtc,
    IReadOnlyList<string> Arguments, string ReplyWindow);

public sealed record SyncCliBridgeReply(string Kind, string Nonce, bool Ok, int ExitCode, string Text,
    string? Error);

public static class SyncCliBridgeProtocol
{
    public const int Version = 1;
    public const string ReceiverWindowTitle = "Sync Shark CLI Bridge";
    public const int MaximumPayloadBytes = 65_536;
    public const int MaximumArguments = 64;
    public const int MaximumArgumentLength = 32_768;
    public static readonly TimeSpan MaximumRequestAge = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaximumFutureClockSkew = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RequestRetention = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static string EncodeRequest(SyncCliBridgeRequest request) => JsonSerializer.Serialize(request, Options);

    public static string EncodeReply(SyncCliBridgeReply reply) => JsonSerializer.Serialize(reply, Options);

    internal static string EncodeProgressReply(SyncCliBridgeReply reply)
    {
        var encoded = EncodeReply(reply);
        if (encoded.Length < MaximumPayloadBytes) return encoded;
        // A transport-limited progress message is never a terminal failure. The authoritative operation
        // continues and its complete retained log stays available; only this optional live delivery is omitted.
        return EncodeReply(reply with { Text = "Live progress delivery exceeded the bridge frame limit. The command is still running; read its retained operation log for the complete entry." });
    }

    // Strict, bounded parsing. Unknown members, wrong kinds, an oversized payload, an expired request,
    // a malformed command vector or a non-CLI argument vector are all rejected before any product work.
    public static SyncCliBridgeRequest ParseRequest(string payload, DateTimeOffset now)
    {
        if (payload.Length == 0 || payload.Length > MaximumPayloadBytes)
            throw new SyncCliBridgeException("oversize", "The bridge request is empty or exceeds its size bound.");
        using var document = Parse(payload);
        var root = document.RootElement;
        RequireExactMembers(root, "kind", "nonce", "issuedUtc", "arguments", "replyWindow");
        var kind = RequireString(root, "kind", 16);
        if (!string.Equals(kind, "invoke", StringComparison.Ordinal))
            throw new SyncCliBridgeException("unsupported", "Only the bounded 'invoke' request is accepted.");
        var nonce = RequireString(root, "nonce", 64);
        if (nonce.Length != 32 || nonce.Any(character => !Uri.IsHexDigit(character)))
            throw new SyncCliBridgeException("malformed", "The request correlation value is not a stable token.");
        if (!DateTimeOffset.TryParse(RequireString(root, "issuedUtc", 64), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal, out var issued))
            throw new SyncCliBridgeException("malformed", "The request timestamp is not an ISO-8601 instant.");
        if (issued > now + MaximumFutureClockSkew || now - issued > MaximumRequestAge)
            throw new SyncCliBridgeException("expired", "The request is outside its accepted window.");
        if (root.GetProperty("arguments").ValueKind != JsonValueKind.Array)
            throw new SyncCliBridgeException("malformed", "The command vector must be an array.");
        var arguments = new List<string>();
        foreach (var element in root.GetProperty("arguments").EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
                throw new SyncCliBridgeException("malformed", "Every command argument must be a string.");
            var value = element.GetString() ?? string.Empty;
            if (value.Length > MaximumArgumentLength)
                throw new SyncCliBridgeException("oversize", "A command argument exceeds its size bound.");
            arguments.Add(value);
        }
        if (arguments.Count is 0 or > MaximumArguments || !SyncCli.IsCliInvocation(arguments))
            throw new SyncCliBridgeException("malformed", "The command vector is not a bounded --cli invocation.");
        var replyWindow = RequireString(root, "replyWindow", 32);
        return new SyncCliBridgeRequest(kind, nonce.ToLowerInvariant(), issued, arguments, replyWindow);
    }

    public static SyncCliBridgeReply ParseReply(string payload, string expectedNonce)
    {
        if (payload.Length == 0 || payload.Length > MaximumPayloadBytes)
            throw new SyncCliBridgeException("oversize", "The bridge reply is empty or exceeds its size bound.");
        using var document = Parse(payload);
        var root = document.RootElement;
        RequireExactMembers(root, "kind", "nonce", "ok", "exitCode", "text", "error");
        var kind = RequireString(root, "kind", 16);
        var nonce = RequireString(root, "nonce", 64);
        if (!string.Equals(nonce, expectedNonce, StringComparison.Ordinal))
            throw new SyncCliBridgeException("mismatch", "The reply correlation value does not match the request.");
        if (kind is not ("result" or "progress" or "ack"))
            throw new SyncCliBridgeException("unsupported", "The reply kind is not part of this protocol.");
        var text = root.GetProperty("text").ValueKind == JsonValueKind.Null
            ? string.Empty
            : RequireString(root, "text", MaximumPayloadBytes);
        var error = root.GetProperty("error").ValueKind == JsonValueKind.Null
            ? null
            : RequireString(root, "error", 1_024);
        var ok = root.GetProperty("ok").ValueKind == JsonValueKind.True;
        var exitCode = root.GetProperty("exitCode").ValueKind == JsonValueKind.Number
            ? root.GetProperty("exitCode").GetInt32()
            : 0;
        return new SyncCliBridgeReply(kind, nonce, ok, exitCode, text, error);
    }

    private static JsonDocument Parse(string payload)
    {
        try
        {
            return JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 4 });
        }
        catch (JsonException error)
        {
            throw new SyncCliBridgeException("malformed", "The bridge payload is not valid JSON: " +
                (SyncCliSanitize.Message(error.Message) ?? "invalid JSON"));
        }
    }

    private static void RequireExactMembers(JsonElement root, params string[] expected)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new SyncCliBridgeException("malformed", "The bridge payload must be a JSON object.");
        foreach (var property in root.EnumerateObject())
            if (!expected.Contains(property.Name, StringComparer.Ordinal))
                throw new SyncCliBridgeException("malformed", "Unknown bridge member '" + property.Name + "'.");
        foreach (var name in expected)
            if (!root.TryGetProperty(name, out _))
                throw new SyncCliBridgeException("malformed", "The bridge payload is missing '" + name + "'.");
    }

    private static string RequireString(JsonElement root, string name, int maximum)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new SyncCliBridgeException("malformed", "The bridge member '" + name + "' must be a string.");
        var text = value.GetString() ?? string.Empty;
        if (text.Length > maximum)
            throw new SyncCliBridgeException("oversize", "The bridge member '" + name + "' exceeds its bound.");
        return text;
    }
}

// Bounded replay protection: a correlation value is accepted once, and only inside its retention
// window, so a duplicate or replayed request can never repeat a product effect.
public sealed class SyncCliBridgeReplayCache(TimeSpan? retention = null)
{
    private readonly TimeSpan _retention = retention ?? SyncCliBridgeProtocol.RequestRetention;
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public bool TryAccept(string nonce, DateTimeOffset now)
    {
        // The receiver dispatches requests concurrently. Expiry and insertion must be one operation:
        // two requests with the same correlation must never both pass the check before either inserts it.
        lock (_gate)
        {
            foreach (var expired in _seen.Where(item => now - item.Value > _retention).Select(item => item.Key).ToArray())
                _seen.Remove(expired);
            if (_seen.ContainsKey(nonce)) return false;
            _seen[nonce] = now;
            return true;
        }
    }

    public int Count { get { lock (_gate) return _seen.Count; } }
}

public sealed class SyncCliBridgeException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

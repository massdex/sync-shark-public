using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Reads the embedded Dropbox route catalog and answers its fixed limits and per-route facts.</summary>
public static class Routes
{
    private const long BytesPerMiB = 1024L * 1024L;

    private static readonly Lazy<Catalog> Embedded = new(Load);

    /// <summary>How a Dropbox route carries its argument and content.</summary>
    public enum Style { Rpc, Upload, Download }

    /// <summary>Whether a Dropbox route reads, writes or is refused before any request.</summary>
    public enum Policy { Read, Write, Denied }

    /// <summary>A named Dropbox route behaviour the typed callers handle instead of a raw caller.</summary>
    public enum Special { Continue, AsyncCheck, UploadSession, Longpoll }

    /// <summary>The maximum ordered members of this batch route, from its provider definition.</summary>
    public static int MaxEntries(string route) =>
        Info(route).MaxEntries ?? throw new ArgumentException(null, nameof(route));

    /// <summary>The content request limit in bytes, converted from the catalog's MiB value.</summary>
    public static long ContentRequestBytes => Embedded.Value.ContentRequestBytes;

    /// <summary>The number of files the provider permits in one relocation.</summary>
    public static int RelocationFiles => Embedded.Value.RelocationFiles;

    /// <summary>Resolve an endpoint without classifying an OAuth bootstrap URL as an API route.</summary>
    public static Uri Endpoint(string name) => name switch
    {
        DropboxProtocol.AuthorizationPath => new UriBuilder(Uri.UriSchemeHttps, DropboxProtocol.AuthorizationHost) { Path = name }.Uri,
        DropboxProtocol.TokenPath => new UriBuilder(Uri.UriSchemeHttps, DropboxProtocol.TokenHost) { Path = name }.Uri,
        _ => Info(name).Endpoint
    };

    /// <summary>The catalog route whose endpoint exactly matches, or null for a non-catalog (bootstrap) endpoint.</summary>
    public static string? NameOf(Uri? endpoint)
    {
        if (endpoint is null) return null;
        return Embedded.Value.NameByEndpoint.TryGetValue(endpoint, out var name) ? name : null;
    }

    /// <summary>Rejects a call whose route does not carry the expected style, before any request is built.</summary>
    public static void RequireStyle(string name, Style style)
    {
        if (Info(name).RouteStyle != style) throw new ArgumentException(null, nameof(name));
    }

    /// <summary>How the route carries its argument and content.</summary>
    public static Style StyleOf(string name) => Info(name).RouteStyle;

    /// <summary>Whether the route reads, writes or is refused before any request.</summary>
    public static Policy PolicyOf(string name) => Info(name).RoutePolicy;

    /// <summary>The route's named provider behaviour, if any.</summary>
    public static Special? SpecialOf(string name) => Info(name).RouteSpecial;

    /// <summary>True when the route is called without any account credential.</summary>
    public static bool IsNoAuth(string name) => Info(name).NoAuth;

    /// <summary>True when the route's argument is the fixed empty object.</summary>
    public static bool VoidArg(string name) => Info(name).VoidArg;

    /// <summary>Encodes a route argument as the ASCII-only value of the Dropbox-API-Arg header.</summary>
    public static string ArgHeader(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        var compact = JsonSerializer.Serialize(document.RootElement);
        var builder = new StringBuilder(compact.Length);
        foreach (var character in compact)
        {
            if (character > '~')
                builder.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
            else
                builder.Append(character);
        }
        return builder.ToString();
    }

    private static RouteInfo Info(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Embedded.Value.ByName.TryGetValue(name, out var info)
            ? info : throw new ArgumentException(null, nameof(name));
    }

    private static Catalog Load()
    {
        using var resource = typeof(Routes).Assembly.GetManifestResourceStream(ResourceNames.Routes)
            ?? throw new InvalidDataException(ResourceNames.Routes);
        using var document = JsonDocument.Parse(resource);
        var root = document.RootElement;
        var hosts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var host in root.GetProperty("hosts").EnumerateObject())
            hosts[host.Name] = host.Value.GetString() ?? string.Empty;
        var byName = new Dictionary<string, RouteInfo>(StringComparer.Ordinal);
        var nameByEndpoint = new Dictionary<Uri, string>();
        foreach (var item in root.GetProperty("routes").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString() ?? throw new InvalidDataException(ResourceNames.Routes);
            var hostText = item.GetProperty("host").GetString();
            if (string.IsNullOrEmpty(hostText)) throw new InvalidDataException(ResourceNames.Routes);
            if (!hosts.TryGetValue(hostText, out var hostBase)) throw new InvalidDataException(ResourceNames.Routes);
            var style = item.GetProperty("style").GetString() switch
            {
                "rpc" => Style.Rpc,
                "upload" => Style.Upload,
                "download" => Style.Download,
                _ => throw new InvalidDataException(ResourceNames.Routes)
            };
            var policy = item.GetProperty("policy").GetString() switch
            {
                "read" => Policy.Read,
                "write" => Policy.Write,
                "denied" => Policy.Denied,
                _ => throw new InvalidDataException(ResourceNames.Routes)
            };
            var special = item.TryGetProperty("special", out var specialElement)
                ? (specialElement.GetString() switch
                {
                    "continue" => Special.Continue,
                    "async-check" => Special.AsyncCheck,
                    "upload-session" => Special.UploadSession,
                    "longpoll" => Special.Longpoll,
                    _ => throw new InvalidDataException(ResourceNames.Routes)
                }) : (Special?)null;
            var noAuth = string.Equals(item.GetProperty("auth").GetString(), "noauth", StringComparison.Ordinal);
            var voidArg = item.TryGetProperty("voidArg", out var voidElement) && voidElement.GetBoolean();
            var maxEntries = item.TryGetProperty("maxEntries", out var maxElement) ? maxElement.GetInt32() : (int?)null;
            var endpoint = new Uri(hostBase + name, UriKind.Absolute);
            byName[name] = new RouteInfo(style, policy, special, noAuth, voidArg, maxEntries, endpoint);
            nameByEndpoint[endpoint] = name;
        }
        var limits = root.GetProperty("limits");
        return new Catalog(byName, nameByEndpoint,
            limits.GetProperty("contentRequestMiB").GetInt64() * BytesPerMiB,
            limits.GetProperty("relocationFiles").GetInt32());
    }

    private sealed record RouteInfo(Style RouteStyle, Policy RoutePolicy, Special? RouteSpecial, bool NoAuth,
        bool VoidArg, int? MaxEntries, Uri Endpoint);

    private sealed record Catalog(IReadOnlyDictionary<string, RouteInfo> ByName,
        IReadOnlyDictionary<Uri, string> NameByEndpoint, long ContentRequestBytes, int RelocationFiles);
}

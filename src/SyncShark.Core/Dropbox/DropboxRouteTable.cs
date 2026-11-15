using System.Text.Json;

namespace Massdex.SyncShark;

/// <summary>The embedded user-token Dropbox route table, loaded from the generated spec data (design §2.2).</summary>
public sealed class DropboxRouteTable
{
    private const string ResourceName = "SyncShark.DropboxRoutes.json";
    private static readonly Lazy<DropboxRouteTable> LazyEmbedded = new(() => Parse(Resource()));
    private readonly IReadOnlyDictionary<string, DropboxRoute> _byName;

    private DropboxRouteTable(string source, string specCommit, IReadOnlyDictionary<DropboxRouteHost, string> hosts,
        IReadOnlyList<DropboxRoute> routes)
    {
        Source = source;
        SpecCommit = specCommit;
        Hosts = hosts;
        Routes = routes;
        _byName = routes.ToDictionary(route => route.Name, StringComparer.Ordinal);
    }

    /// <summary>The route table's source repository.</summary>
    public string Source { get; }

    /// <summary>The exact spec commit the route table was generated from.</summary>
    public string SpecCommit { get; }

    /// <summary>The base URL of each Dropbox host family.</summary>
    public IReadOnlyDictionary<DropboxRouteHost, string> Hosts { get; }

    /// <summary>Every user-token route, in table order.</summary>
    public IReadOnlyList<DropboxRoute> Routes { get; }

    /// <summary>The route table embedded in this assembly, parsed once on first use.</summary>
    public static DropboxRouteTable Embedded => LazyEmbedded.Value;

    /// <summary>Finds a route by its URL name, or null when the table does not contain it.</summary>
    public DropboxRoute? Find(string name) => _byName.TryGetValue(name, out var route) ? route : null;

    /// <summary>Parses a route table from its JSON representation.</summary>
    public static DropboxRouteTable Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var source = root.GetProperty("source").GetString() ?? string.Empty;
        var specCommit = root.GetProperty("specCommit").GetString() ?? string.Empty;
        var hosts = new Dictionary<DropboxRouteHost, string>();
        foreach (var host in root.GetProperty("hosts").EnumerateObject())
            hosts[ParseHost(host.Name)] = host.Value.GetString() ?? string.Empty;
        var routes = new List<DropboxRoute>();
        foreach (var item in root.GetProperty("routes").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString()
                ?? throw new InvalidDataException("A Dropbox route has no name.");
            var host = ParseHost(item.GetProperty("host").GetString() ?? string.Empty);
            var style = ParseStyle(item.GetProperty("style").GetString() ?? string.Empty);
            if (!hosts.TryGetValue(host, out var hostBase))
                throw new InvalidDataException($"The Dropbox route {name} uses an undeclared host.");
            routes.Add(new DropboxRoute(
                name, host, style,
                item.GetProperty("auth").GetString() ?? string.Empty,
                item.GetProperty("scope").GetString() ?? string.Empty,
                ParsePolicy(item.GetProperty("policy").GetString() ?? string.Empty),
                item.TryGetProperty("special", out var special) ? ParseSpecial(special.GetString() ?? string.Empty) : null,
                item.TryGetProperty("voidArg", out var voidArg) && voidArg.GetBoolean(),
                item.TryGetProperty("deprecated", out var deprecated) && deprecated.GetBoolean(),
                item.TryGetProperty("preview", out var preview) && preview.GetBoolean(),
                new Uri(hostBase + name, UriKind.Absolute)));
        }
        return new DropboxRouteTable(source, specCommit, hosts, routes);
    }

    private static Stream Resource() =>
        typeof(DropboxRouteTable).Assembly.GetManifestResourceStream(ResourceName)
        ?? throw new InvalidOperationException("The embedded Dropbox route table is missing.");

    private static DropboxRouteHost ParseHost(string value) => value switch
    {
        "api" => DropboxRouteHost.Api,
        "content" => DropboxRouteHost.Content,
        "notify" => DropboxRouteHost.Notify,
        _ => throw new InvalidDataException($"The Dropbox route table names an unknown host '{value}'.")
    };

    private static DropboxRouteStyle ParseStyle(string value) => value switch
    {
        "rpc" => DropboxRouteStyle.Rpc,
        "upload" => DropboxRouteStyle.Upload,
        "download" => DropboxRouteStyle.Download,
        _ => throw new InvalidDataException($"The Dropbox route table names an unknown style '{value}'.")
    };

    private static DropboxRoutePolicy ParsePolicy(string value) => value switch
    {
        "read" => DropboxRoutePolicy.Read,
        "write" => DropboxRoutePolicy.Write,
        "denied" => DropboxRoutePolicy.Denied,
        _ => throw new InvalidDataException($"The Dropbox route table names an unknown policy '{value}'.")
    };

    private static DropboxRouteSpecial ParseSpecial(string value) => value switch
    {
        "continue" => DropboxRouteSpecial.Continue,
        "async-check" => DropboxRouteSpecial.AsyncCheck,
        "upload-session" => DropboxRouteSpecial.UploadSession,
        "longpoll" => DropboxRouteSpecial.Longpoll,
        _ => throw new InvalidDataException($"The Dropbox route table names an unknown special '{value}'.")
    };
}

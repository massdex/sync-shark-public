using System.Text.Json;

namespace Massdex.SyncShark.V2;

/// <summary>The edge's user-facing wording, loaded once from the embedded catalog (standards §2.2).</summary>
internal static class EdgeMessages
{
    private const string ResourceName = "SyncShark.EdgeMessages.json";
    private static readonly Lazy<Catalog> LazyCatalog = new(() => Catalog.Load(Resource()));

    /// <summary>The full "command not in core v2 yet" message for a stable wording key.</summary>
    internal static NotSupportedException NotInV2(string whatKey) => new(Compose("command", whatKey));

    /// <summary>The short "not in core v2 yet" notice for a stable wording key.</summary>
    internal static string NotInV2Text(string whatKey) => Compose("notice", whatKey);

    /// <summary>A whole edge message for a stable key, with an optional "{detail}" filled in.</summary>
    internal static string Text(string messageKey, string? detail = null) =>
        LazyCatalog.Value.Message(messageKey).Replace("{detail}", detail ?? string.Empty, StringComparison.Ordinal);

    /// <summary>The one continuous-sync monitor wording for a stable key (status words and the projection kind).</summary>
    internal static string Monitor(string monitorKey) => LazyCatalog.Value.Monitor(monitorKey);

    private static string Compose(string templateKey, string whatKey) =>
        LazyCatalog.Value.Template(templateKey)
            .Replace("{what}", LazyCatalog.Value.What(whatKey), StringComparison.Ordinal);

    private static Stream Resource() =>
        typeof(EdgeMessages).Assembly.GetManifestResourceStream(ResourceName)
        ?? throw new InvalidOperationException("The embedded edge message catalog is missing.");

    private sealed class Catalog(IReadOnlyDictionary<string, string> templates, IReadOnlyDictionary<string, string> what,
        IReadOnlyDictionary<string, string> messages, IReadOnlyDictionary<string, string> monitor)
    {
        internal string Template(string key) => Find(templates, "template", key);

        internal string What(string key) => Find(what, "wording", key);

        internal string Message(string key) => Find(messages, "message", key);

        internal string Monitor(string key) => Find(monitor, "monitor", key);

        internal static Catalog Load(Stream stream)
        {
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            return new Catalog(ReadMap(root.GetProperty("templates")), ReadMap(root.GetProperty("what")),
                ReadMap(root.GetProperty("messages")), ReadMap(root.GetProperty("monitor")));
        }

        private static string Find(IReadOnlyDictionary<string, string> map, string kind, string key) =>
            map.TryGetValue(key, out var value)
                ? value
                : throw new InvalidDataException("The edge message catalog has no " + kind + " '" + key + "'.");

        private static IReadOnlyDictionary<string, string> ReadMap(JsonElement element) =>
            element.EnumerateObject().ToDictionary(property => property.Name,
                property => property.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
    }
}

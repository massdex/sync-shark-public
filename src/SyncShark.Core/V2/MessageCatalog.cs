using System.Text.Json;

namespace Massdex.SyncShark.V2;

/// <summary>Shared embedded message lookup; BCL-only so every engine layer can reuse the catalog.</summary>
internal sealed class MessageCatalog(IReadOnlyDictionary<string, string> templates, IReadOnlyDictionary<string, string> what,
    IReadOnlyDictionary<string, string> messages, IReadOnlyDictionary<string, string> monitor)
{
    private const string ResourceName = "SyncShark.EdgeMessages.json";
    private static readonly Lazy<MessageCatalog> LazyCatalog = new(() => Load(Resource()));
    internal static MessageCatalog Default => LazyCatalog.Value;

    private static Stream Resource() =>
        typeof(MessageCatalog).Assembly.GetManifestResourceStream(ResourceName)
        ?? throw new InvalidOperationException("The embedded edge message catalog is missing.");

    internal string Template(string key) => Find(templates, "template", key);

    internal string What(string key) => Find(what, "wording", key);

    internal string Message(string key) => Find(messages, "message", key);

    internal string Monitor(string key) => Find(monitor, "monitor", key);

    internal static MessageCatalog Load(Stream stream)
    {
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        return new MessageCatalog(ReadMap(root.GetProperty("templates")), ReadMap(root.GetProperty("what")),
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

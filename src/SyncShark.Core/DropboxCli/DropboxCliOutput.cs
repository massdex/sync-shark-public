using System.Globalization;
using System.Text.Json;

namespace Massdex.SyncShark;

/// <summary>Maps Dropbox JSON and results into the CLI data shapes.</summary>
public static class DropboxCliOutput
{
    /// <summary>Maps one non-download, non-batch result by its output shape.</summary>
    public static object Map(DropboxCliCatalog.OutputDefinition output, DropboxRoute route, DropboxRouteResult result)
    {
        var root = result.Result.RootElement;
        return output.Kind switch
        {
            "Raw" => new DropboxCliRawData(route.Name, root.Clone()),
            "Page" => Page(output, root),
            "Entry" => Entry(output.At is { } at && root.TryGetProperty(at, out var metadata) ? metadata : root),
            _ => throw new InvalidDataException("Unsupported output kind '" + output.Kind + "'.")
        };
    }

    /// <summary>Maps one Dropbox metadata object to the entry shape.</summary>
    public static DropboxCliEntry Entry(JsonElement metadata) => new(
        Tag: Str(metadata, ".tag") ?? "",
        Name: Str(metadata, "name") ?? "",
        PathDisplay: Str(metadata, "path_display") ?? "",
        PathLower: Str(metadata, "path_lower") ?? "",
        Id: Str(metadata, "id") ?? "",
        Rev: Str(metadata, "rev"),
        Size: Long(metadata, "size"),
        ContentHash: Str(metadata, "content_hash"),
        ClientModified: Time(metadata, "client_modified"),
        ServerModified: Time(metadata, "server_modified"),
        IsDownloadable: Bool(metadata, "is_downloadable"));

    private static DropboxCliPageData Page(DropboxCliCatalog.OutputDefinition output, JsonElement root)
    {
        var entries = new List<DropboxCliEntry>();
        if (output.Items is { } items && root.TryGetProperty(items, out var array) &&
            array.ValueKind == JsonValueKind.Array)
            foreach (var item in array.EnumerateArray())
                entries.Add(Entry(output.EntryPath is null ? item : Resolve(item, output.EntryPath)));
        // A revision page's deletion status is the returned is_deleted, never the verb's static shape flag
        // (which only says the route reports one).
        var isDeleted = output.IsDeleted && Bool(root, "is_deleted") == true;
        return new DropboxCliPageData(entries, Str(root, "cursor"), Bool(root, "has_more") ?? false, isDeleted);
    }

    private static JsonElement Resolve(JsonElement element, string path)
    {
        foreach (var part in path.Split('.'))
            element = element.ValueKind == JsonValueKind.Object && element.TryGetProperty(part, out var next)
                ? next : default;
        return element;
    }

    internal static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static long? Long(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;

    internal static bool? Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    internal static DateTimeOffset? Time(JsonElement element, string name)
    {
        var text = Str(element, name);
        return text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value) ? value : null;
    }
}

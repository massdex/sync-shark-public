using System.Text.Json;
using System.Text.Json.Nodes;

namespace Massdex.SyncShark.V2;

/// <summary>
/// The provider's own export capability facts, carried in the existing identity_json column. A fresh observation of a
/// non-downloadable file always records the property (with an empty array when the provider listed nothing), so an
/// absent property means a notice written before export_info was read, never "the provider supports nothing".
/// </summary>
internal static class DropboxExportFacts
{
    private const string ExportAsProperty = "dropboxExportAs";
    private const string ExportOptionsProperty = "dropboxExportOptions";

    /// <summary>Adds the export facts to a name-mapping identity document; no schema change and no extra column.</summary>
    internal static string? Merge(string? identityJson, string? exportAs, IReadOnlyList<string>? options, bool required)
    {
        if (!required) return identityJson;
        JsonObject facts;
        try { facts = identityJson is null ? new JsonObject() : JsonNode.Parse(identityJson)!.AsObject(); }
        catch (JsonException) { facts = new JsonObject(); }
        if (!string.IsNullOrWhiteSpace(exportAs)) facts[ExportAsProperty] = exportAs;
        var formats = (options ?? [])
            .Where(format => !string.IsNullOrWhiteSpace(format))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(format => (JsonNode?)JsonValue.Create(format)).ToArray();
        facts[ExportOptionsProperty] = new JsonArray(formats);
        return facts.ToJsonString();
    }

    /// <summary>The formats this item's provider listed, or null when the item carries no fresh export facts.
    /// When only <c>export_as</c> is known, that single default format is offered.</summary>
    internal static NoticeExportInfo? ForItem(ItemState? item)
    {
        if (item?.IdentityJson is not { } json) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var exportAs = root.TryGetProperty(ExportAsProperty, out var asElement) &&
                asElement.ValueKind == JsonValueKind.String ? asElement.GetString() : null;
            if (!root.TryGetProperty(ExportOptionsProperty, out var optionsElement)) return null;
            var options = optionsElement.ValueKind == JsonValueKind.Array
                ? optionsElement.EnumerateArray()
                    .Where(option => option.ValueKind == JsonValueKind.String)
                    .Select(option => option.GetString())
                    .Where(format => !string.IsNullOrWhiteSpace(format))
                    .Select(format => format!)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : [];
            IReadOnlyList<string> formats = options.Length > 0
                ? options
                : string.IsNullOrWhiteSpace(exportAs) ? Array.Empty<string>() : new[] { exportAs! };
            return new NoticeExportInfo(exportAs, formats);
        }
        catch (JsonException) { return null; }
    }
}

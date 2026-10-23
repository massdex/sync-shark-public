using System.Text.Json;
using System.Text.Json.Nodes;

namespace Massdex.SyncShark;

/// <summary>Renders one call's argument JSON from its data template and the invocation's typed options.</summary>
public static class DropboxCliTemplate
{
    /// <summary>The compact argument JSON for one call, or "null" when the call carries no body.</summary>
    public static string Render(JsonElement? body, DropboxCliArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (body is null) return "null";
        var node = JsonNode.Parse(body.Value.GetRawText());
        return RenderNode(node, arguments)?.ToJsonString() ?? "null";
    }

    private static JsonNode? RenderNode(JsonNode? node, DropboxCliArguments arguments)
    {
        switch (node)
        {
            case JsonObject source:
                var target = new JsonObject();
                foreach (var property in source)
                {
                    if (property.Value is JsonValue value && value.TryGetValue(out string? text) &&
                        IsTemplate(text, out var option))
                    {
                        if (!arguments.Present(option)) continue;
                        target[property.Key] = arguments.TemplateValue(option);
                        continue;
                    }
                    var child = RenderNode(property.Value, arguments);
                    if (child is null || child is JsonObject { Count: 0 }) continue;
                    target[property.Key] = child;
                }
                return target;
            case JsonArray source:
                var array = new JsonArray();
                foreach (var item in source)
                {
                    var child = RenderNode(item, arguments);
                    if (child is not null) array.Add(child);
                }
                return array;
            default:
                return node?.DeepClone();
        }
    }

    private static bool IsTemplate(string? text, out DropboxCliOption option)
    {
        option = default;
        if (text is null || text.Length < 2 || text[0] != '$') return false;
        if (!Enum.TryParse<DropboxCliOption>(text[1..], ignoreCase: false, out var parsed) ||
            !Enum.IsDefined(parsed)) return false;
        option = parsed;
        return true;
    }
}

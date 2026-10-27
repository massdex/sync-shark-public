using System.Text.Json;

namespace Massdex.SyncShark;

/// <summary>
/// The one bounded scalar reader for host JSON. A missing, null or mistyped member is never invented:
/// every read returns the type's neutral value so the tray cannot turn an absent field into a claim.
/// </summary>
internal static class TrayJson
{
    internal static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static bool Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();

    internal static int Int(JsonElement element, string name) =>
        (int)Math.Clamp(Long(element, name), int.MinValue, int.MaxValue);

    internal static long Long(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;

    internal static DateTimeOffset? Time(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var instant) ? instant : null;
}

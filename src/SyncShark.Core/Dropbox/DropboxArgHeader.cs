using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

/// <summary>Encodes a route argument as the ASCII-only value of the Dropbox-API-Arg header (design §2.4).</summary>
public static class DropboxArgHeader
{
    /// <summary>
    /// Parses the JSON argument, re-serializes it compactly and escapes every character above U+007E as a
    /// <c>\uXXXX</c> UTF-16 unit, because Dropbox requires an ASCII header value. Throws
    /// <see cref="JsonException"/> for invalid JSON so the caller can map it to a usage error.
    /// </summary>
    public static string Encode(string json)
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
}

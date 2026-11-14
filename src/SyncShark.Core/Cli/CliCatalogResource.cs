using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

/// <summary>The single loader of embedded JSON catalogs and filler of {name} templates (engine design 1.3).</summary>
public static class CliCatalogResource
{
    private const char PlaceholderOpen = '{';
    private const char PlaceholderClose = '}';

    /// <summary>Parses an embedded JSON resource; a missing resource throws, naming it.</summary>
    public static JsonDocument Load(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(resourceName);
        return JsonDocument.Parse(stream);
    }

    /// <summary>Fills each {name} placeholder (ordinal) in one pass over the original template, so text inserted for
    /// one placeholder is never read as another.</summary>
    public static string Fill(string template, IReadOnlyList<(string Name, string Value)> values)
    {
        var filled = new StringBuilder(template.Length);
        for (var index = default(int); index < template.Length; index++)
        {
            if (template[index] == PlaceholderOpen)
            {
                var close = template.IndexOf(PlaceholderClose, index + 1);
                if (close > index && ValueFor(values, template.AsSpan(index + 1, close - index - 1)) is { } value)
                {
                    filled.Append(value);
                    index = close;
                    continue;
                }
            }
            filled.Append(template[index]);
        }
        return filled.ToString();
    }

    private static string? ValueFor(IReadOnlyList<(string Name, string Value)> values, ReadOnlySpan<char> name)
    {
        foreach (var (candidate, value) in values)
            if (name.SequenceEqual(candidate)) return value;
        return null;
    }
}

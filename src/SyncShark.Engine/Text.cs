namespace Massdex.SyncShark.Engine;

/// <summary>Every user- or log-visible sentence, from engine-strings.json. Keys and templates must match exactly.</summary>
public static class Text
{
    private static readonly Lazy<IReadOnlyDictionary<TextKey, string>> Templates = new(Load);

    /// <summary>Fills the template for key with the supplied named values.</summary>
    public static string Get(TextKey key, params (TextArg Name, string Value)[] values) =>
        CliCatalogResource.Fill(Templates.Value[key], values.Select(v => (v.Name.ToString(), v.Value)).ToArray());

    /// <summary>All templates; loading throws if a key and a template don't match one to one.</summary>
    public static IReadOnlyDictionary<TextKey, string> All => Templates.Value;

    /// <summary>The read-failure cause for an item on that side (the repeated ternary callers would otherwise copy).</summary>
    public static TextKey ReadFailedOn(Side side) =>
        side == Side.Local ? TextKey.ReadLocalFailed : TextKey.ReadDropboxFailed;

    private static IReadOnlyDictionary<TextKey, string> Load()
    {
        using var doc = CliCatalogResource.Load(typeof(Text).Assembly, ResourceNames.Strings);
        var map = new Dictionary<TextKey, string>();
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            // Exact declared names only (no numeric spellings, no case folding); a repeated key is an error.
            if (!EnumNames.TryExact<TextKey>(property.Name, out var key)) throw new InvalidDataException(property.Name);
            var template = property.Value.GetString() ?? throw new InvalidDataException(property.Name);
            if (!map.TryAdd(key, template)) throw new InvalidDataException(property.Name);
        }
        foreach (var key in Enum.GetValues<TextKey>())
            if (!map.ContainsKey(key)) throw new InvalidDataException(key.ToString());
        return map;
    }
}

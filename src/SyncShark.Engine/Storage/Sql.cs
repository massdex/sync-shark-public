namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Every SQL statement and shared fragment, from engine-sql.json. A {Key} reference resolves once at load.</summary>
public static class Sql
{
    private static readonly Lazy<IReadOnlyDictionary<SqlKey, string>> Statements = new(Load);

    /// <summary>The resolved SQL text for key.</summary>
    public static string Get(SqlKey key) => Statements.Value[key];

    /// <summary>All resolved statements; loading throws if a key, a reference or the file does not match.</summary>
    public static IReadOnlyDictionary<SqlKey, string> All => Statements.Value;

    private static IReadOnlyDictionary<SqlKey, string> Load()
    {
        using var doc = CliCatalogResource.Load(typeof(Sql).Assembly, ResourceNames.Sql);
        var map = new Dictionary<SqlKey, string>();
        // Resolved entries in file order, so a {Key} reference can only name a key already loaded.
        var entries = new List<(string Name, string Value)>();
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            // Exact declared names only (no numeric spellings, no case folding); a repeated key is an error.
            if (!EnumNames.TryExact<SqlKey>(property.Name, out var key)) throw new InvalidDataException(property.Name);
            var text = property.Value.GetString() ?? throw new InvalidDataException(property.Name);
            foreach (System.Text.RegularExpressions.Match placeholder in System.Text.RegularExpressions.Regex.Matches(text, @"\{([A-Za-z][A-Za-z0-9]*)\}"))
                if (!EnumNames.TryExact<SqlKey>(placeholder.Groups[1].Value, out var reference) || !map.ContainsKey(reference))
                    throw new InvalidDataException(placeholder.Value);
            var value = CliCatalogResource.Fill(text, entries);
            if (!map.TryAdd(key, value)) throw new InvalidDataException(property.Name);
            entries.Add((property.Name, value));
        }
        foreach (var key in Enum.GetValues<SqlKey>())
            if (!map.ContainsKey(key)) throw new InvalidDataException(key.ToString());
        return map;
    }
}

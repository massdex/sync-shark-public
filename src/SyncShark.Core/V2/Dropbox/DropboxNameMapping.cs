using System.Text.Json;

namespace Massdex.SyncShark.V2;

/// <summary>Windows spelling of a Dropbox entity. Its provider Path and identity never change.</summary>
internal static class DropboxNameMapping
{
    private const string Property = "dropboxWindowsPath";

    internal static string Map(string path)
    {
        char[]? mapped = null;
        var trailing = true;
        for (var i = path.Length - 1; i >= 0; i--)
        {
            if (path[i] == '/') { trailing = true; continue; }
            var replace = path[i] is ':' or '*' or '?' or '"' or '<' or '>' or '|' ||
                trailing && path[i] is '.' or ' ';
            if (path[i] is not ('.' or ' ')) trailing = false;
            if (replace) (mapped ??= path.ToCharArray())[i] = '_';
        }
        return mapped is null ? path : new string(mapped);
    }

    internal static string? Fact(string path)
    {
        var local = Map(path);
        return local == path ? null : JsonSerializer.Serialize(new Dictionary<string, string> { [Property] = local });
    }

    internal static string? LocalPath(ItemState? item)
    {
        if (item?.IdentityJson is not { } json || !json.Contains(Property, StringComparison.Ordinal)) return null;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(Property, out var value) ? value.GetString() : null;
    }

    // RQ-039: unchanged pre-v7 cached Dropbox rows must work without a manual rescan.
    // Only the old invalid-name block is inferred; unsupported Windows targets remain unreadable.
    internal static ItemState FromStoredDropbox(ItemState item)
    {
        var mapped = Map(item.Path);
        if (mapped == item.Path || LocalPath(item) is not null) return item;
        // Legacy Unreadable conflates name and downloadability. No hash means we cannot safely clear it.
        var transferable = PilotPath.IsWindowsTransferPath(mapped) &&
            (item.Kind == ItemKind.Folder || !string.IsNullOrEmpty(item.Hash));
        return item with
        {
            IdentityJson = WithFact(item.IdentityJson, mapped),
            Flags = transferable ? item.Flags & ~ItemFlags.Unreadable : item.Flags
        };
    }

    private static string WithFact(string? json, string mapped)
    {
        var facts = json is null ? new System.Text.Json.Nodes.JsonObject() :
            System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        facts[Property] = mapped;
        return facts.ToJsonString();
    }

    internal static string MatchPath(ItemState item) => LocalPath(item) ?? item.Path;
    internal static bool SamePlace(ItemState a, ItemState b) => PairPath.Same(MatchPath(a), MatchPath(b));
}

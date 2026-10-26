using System.Security.Cryptography;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

/// <summary>
/// The structured --choices vocabulary and its validator (first-sync-choices.md): the JSON keys, the action
/// names, the prefix and side rules, the digest that binds an apply to its prepare and every usage message
/// live here once, until decision 36 moves CLI vocabulary into data. Every validation failure is a
/// <see cref="SyncCliUsageException"/> (exit 2).
/// </summary>
public static class FirstSyncChoices
{
    /// <summary>The structured document's paths object; the legacy flat map when used alone.</summary>
    public const string PathsKey = "paths";

    /// <summary>The structured document's array of per-folder one-sided choices.</summary>
    public const string OneSidedKey = "oneSided";

    /// <summary>The structured document's optional array of rebuildable category ids to stop syncing.</summary>
    public const string RebuildablesKey = "rebuildables";

    private const string PrefixKey = "prefix";
    private const string SideKey = "side";
    private const string ActionKey = "action";

    /// <summary>The action that removes one-sided items on the side that holds them.</summary>
    public const string RemoveAction = "remove";

    /// <summary>The action that copies one-sided items to the other side.</summary>
    public const string CopyAction = "copy";

    private const string Suffix = " No sync action was applied.";
    private const string NotObject = "The --choices value must be a JSON object of path to upload, download or no-change." + Suffix;
    private const string PathsNotObject = "The '" + PathsKey + "' value must be a JSON object of path to upload, download or no-change." + Suffix;
    private const string OneSidedNotArray = "The '" + OneSidedKey + "' value must be a JSON array of one-sided choices." + Suffix;
    private const string RebuildablesNotArray = "The '" + RebuildablesKey + "' value must be a JSON array of rebuildable folder type ids." + Suffix;
    private const string RebuildableNotString = "Every '" + RebuildablesKey + "' entry must be a rebuildable folder type id or an object with id and prefix." + Suffix;
    private const string RebuildableSides = "The '" + RebuildablesKey + "' choice needs a pair with exactly one local side and one Dropbox side." + Suffix;
    private const string EntryNotObject = "Every '" + OneSidedKey + "' entry must be an object with prefix, side and action." + Suffix;
    private const string ChoiceNotString = "Every --choices entry must be a string choice." + Suffix;
    private const string FieldsRequired = "Every '" + OneSidedKey + "' entry needs a prefix, a side and an action." + Suffix;
    private const string OneLocalSide = "First-sync choices need a pair with exactly one local side." + Suffix;
    private const string MoveRefused = "Core v2 does not offer 'move' first-sync choices yet." + Suffix;
    private const string NeedsPrefix = "The choice prefix cannot start with '/', contain '\\', or hold a '.' or '..' or empty segment." + Suffix;

    /// <summary>What one parsed --choices document holds: per-path winners, per-folder one-sided choices and the
    /// rebuildable folder types to stop syncing.</summary>
    public sealed record Parsed(IReadOnlyDictionary<string, Side?> Winners, IReadOnlyList<OneSidedChoice> OneSided,
        IReadOnlyList<RebuildableChoice> Rebuildables);

    /// <summary>
    /// Parses and fully validates a --choices document against a pair. The legacy flat map is a document whose
    /// "paths"/"oneSided" keys (if any) hold strings; an object under "paths" or an array under "oneSided" is the
    /// structured form. Any repeated key, unknown key or field, bad side, action or prefix, or duplicate
    /// prefix+side is refused before any sync action is planned.
    /// </summary>
    public static Parsed Parse(string json, PairDef pair)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException)
        {
            throw Refuse(NotObject);
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw Refuse(NotObject);
            RejectRepeatedKeys(root, "The choices document");
            var winners = new Dictionary<string, Side?>(StringComparer.Ordinal);
            var oneSided = new List<OneSidedChoice>();
            var rebuildables = new List<RebuildableChoice>();
            if (IsStructured(root))
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in root.EnumerateObject())
                {
                    if (string.Equals(property.Name, PathsKey, StringComparison.Ordinal))
                        ReadPaths(property.Value, pair, winners);
                    else if (string.Equals(property.Name, OneSidedKey, StringComparison.Ordinal))
                        ReadOneSided(property.Value, pair, oneSided, seen);
                    else if (string.Equals(property.Name, RebuildablesKey, StringComparison.Ordinal))
                        ReadRebuildables(property.Value, rebuildables);
                    else
                        throw Refuse($"The choices document has an unknown key '{property.Name}'. Only '{PathsKey}', '{OneSidedKey}' and '{RebuildablesKey}' are allowed." + Suffix);
                }
            }
            else
            {
                ReadPaths(root, pair, winners);
            }
            // Removing the cloud copy needs a cloud side to remove it from and a local side to keep.
            if (rebuildables.Count > 0 && RebuildableSelection.Sides(pair) is null)
                throw Refuse(RebuildableSides);
            return new Parsed(winners, oneSided, rebuildables);
        }
    }

    /// <summary>
    /// The SHA-256 digest of the normalized choices, frozen rebuildable set and exact held-deletion rules (decision 54). The canonical form is
    /// a JSON array of [kind, ...fields] tuples, so no path text can forge a field boundary. Paths keep their original
    /// spelling (no case folding can match OrdinalIgnoreCase exactly), so a casing-only change needs a new prepare; the
    /// tuples are sorted ordinally, so document, key, side and frozen order do not change the digest.
    /// </summary>
    public static string Digest(Parsed parsed, IReadOnlyList<RebuildableFolder> rebuildables,
        IReadOnlyList<HeldDeletion>? heldDeletions = null)
    {
        var entries = new List<string[]>();
        foreach (var winner in parsed.Winners)
            entries.Add(["path", winner.Key, WinnerText(winner.Value)]);
        foreach (var choice in parsed.OneSided)
            entries.Add(["oneSided", SideText(choice.Present), choice.Prefix, ActionText(choice.Remove)]);
        foreach (var rebuildable in parsed.Rebuildables)
            entries.Add(rebuildable.Prefix.Length == 0
                ? ["rebuildable", rebuildable.Id]
                : ["rebuildable", rebuildable.Id, rebuildable.Prefix]);
        foreach (var folder in rebuildables)
            entries.Add(["rebuildablePath", folder.Category, folder.Path]);
        foreach (var held in heldDeletions ?? [])
            entries.Add(["heldDeletion", SideText(held.Local), held.Path, held.Rule]);
        entries.Sort(CompareTuples);
        // Preserve the exact canonical JSON bytes without retaining a second, potentially large byte array.
        using var hash = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        JsonSerializer.Serialize(stream, entries);
        stream.FlushFinalBlock();
        return Convert.ToHexString(hash.Hash!).ToLowerInvariant();
    }

    /// <summary>
    /// The identity of a one-sided choice: its side ("A"/"B") and trimmed prefix. Compare keys with
    /// <see cref="StringComparer.OrdinalIgnoreCase"/>, the comparer path matching uses.
    /// </summary>
    public static string ChoiceKey(string sideText, string prefix) => sideText + ":" + prefix.TrimEnd('/');

    private static int CompareTuples(string[] x, string[] y)
    {
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            var order = string.CompareOrdinal(x[i], y[i]);
            if (order != 0) return order;
        }
        return x.Length.CompareTo(y.Length);
    }

    /// <summary>The persisted short form of a winner side: "A", "B" or "none".</summary>
    public static string WinnerText(Side? side) => side switch { Side.A => "A", Side.B => "B", _ => "none" };

    /// <summary>The persisted short form of a side.</summary>
    public static string SideText(Side side) => side == Side.A ? "A" : "B";

    /// <summary>The persisted action name for a choice.</summary>
    public static string ActionText(bool remove) => remove ? RemoveAction : CopyAction;

    /// <summary>The CLI display name of a pair side ("Local", "Dropbox", "Google Drive").</summary>
    public static string SideName(PairDef pair, Side side) => SideName(side == Side.A ? pair.A.Provider : pair.B.Provider);

    private static bool IsStructured(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, PathsKey, StringComparison.Ordinal) &&
                property.Value.ValueKind == JsonValueKind.Object)
                return true;
            if (string.Equals(property.Name, OneSidedKey, StringComparison.Ordinal) &&
                property.Value.ValueKind == JsonValueKind.Array)
                return true;
            if (string.Equals(property.Name, RebuildablesKey, StringComparison.Ordinal) &&
                property.Value.ValueKind == JsonValueKind.Array)
                return true;
        }
        return false;
    }

    private static void ReadPaths(JsonElement element, PairDef pair, Dictionary<string, Side?> winners)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw Refuse(PathsNotObject);
        RejectRepeatedKeys(element, "The choices path map");
        if (!element.EnumerateObject().Any())
            return;
        var local = LocalSide(pair);
        var other = local == Side.A ? Side.B : Side.A;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            // Matching ignores letter case, so two spellings of one path would be two answers for one file.
            if (!seen.Add(property.Name))
                throw Refuse($"The choices name the path '{property.Name}' more than once (paths differ only in letter case)." + Suffix);
            winners[property.Name] = Winner(property.Name, property.Value, local, other);
        }
    }

    private static Side? Winner(string path, JsonElement value, Side local, Side other)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw Refuse(ChoiceNotString);
        var choice = (value.GetString() ?? string.Empty).Trim().ToLowerInvariant();
        return choice switch
        {
            "upload" => local,
            "download" => other,
            "no-change" => null,
            "move" => throw Refuse(MoveRefused),
            _ => throw Refuse($"The choice for '{path}' must be upload, download or no-change, not '{choice}'." + Suffix)
        };
    }

    private static void ReadOneSided(JsonElement element, PairDef pair, List<OneSidedChoice> oneSided,
        HashSet<string> seen)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw Refuse(OneSidedNotArray);
        foreach (var entry in element.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                throw Refuse(EntryNotObject);
            RejectRepeatedKeys(entry, "A one-sided choice");
            string? prefix = null, side = null, action = null;
            foreach (var field in entry.EnumerateObject())
            {
                if (string.Equals(field.Name, PrefixKey, StringComparison.Ordinal)) prefix = Text(field);
                else if (string.Equals(field.Name, SideKey, StringComparison.Ordinal)) side = Text(field);
                else if (string.Equals(field.Name, ActionKey, StringComparison.Ordinal)) action = Text(field);
                else throw Refuse($"A one-sided choice has an unknown field '{field.Name}'. Only '{PrefixKey}', '{SideKey}' and '{ActionKey}' are allowed." + Suffix);
            }
            if (prefix is null || side is null || action is null)
                throw Refuse(FieldsRequired);
            var present = MatchSide(side, pair);
            ValidatePrefix(prefix);
            var remove = action switch
            {
                RemoveAction => true,
                CopyAction => false,
                _ => throw Refuse($"A one-sided choice action must be '{RemoveAction}' or '{CopyAction}', not '{action}'." + Suffix)
            };
            var normalized = prefix.TrimEnd('/');
            if (!seen.Add(ChoiceKey(SideText(present), normalized)))
                throw Refuse($"The choices hold the same prefix '{prefix}' and side '{side}' more than once." + Suffix);
            oneSided.Add(new OneSidedChoice(normalized, present, remove));
        }
    }

    // Every entry must name a known rebuildable category; unknown ids are refused before any sync action is planned.
    private static void ReadRebuildables(JsonElement element, List<RebuildableChoice> rebuildables)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw Refuse(RebuildablesNotArray);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in element.EnumerateArray())
        {
            string id;
            var prefix = string.Empty;
            if (entry.ValueKind == JsonValueKind.String)
                id = (entry.GetString() ?? string.Empty).Trim();
            else if (entry.ValueKind == JsonValueKind.Object)
            {
                RejectRepeatedKeys(entry, "A rebuildable choice");
                string? objectId = null, objectPrefix = null;
                foreach (var field in entry.EnumerateObject())
                {
                    if (field.Name == "id") objectId = Text(field);
                    else if (field.Name == PrefixKey) objectPrefix = Text(field);
                    else throw Refuse($"A rebuildable choice has an unknown field '{field.Name}'. Only 'id' and '{PrefixKey}' are allowed." + Suffix);
                }
                if (objectId is null || objectPrefix is null) throw Refuse(RebuildableNotString);
                id = objectId.Trim();
                ValidatePrefix(objectPrefix);
                prefix = objectPrefix.TrimEnd('/');
            }
            else throw Refuse(RebuildableNotString);
            if (!RebuildableDetector.IsKnown(id))
                throw Refuse($"'{id}' is not a rebuildable folder type. Choose one of: " +
                    string.Join(", ", RebuildableDetector.Ids) + "." + Suffix);
            if (!seen.Add(ChoiceKey(id, prefix)))
                throw Refuse($"The '{RebuildablesKey}' list names '{id}' more than once." + Suffix);
            rebuildables.Add(new RebuildableChoice(id, prefix));
        }
    }

    private static void RejectRepeatedKeys(JsonElement element, string label)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!seen.Add(property.Name))
                throw Refuse(label + " names '" + property.Name + "' more than once." + Suffix);
    }

    private static string Text(JsonProperty field)
    {
        if (field.Value.ValueKind != JsonValueKind.String)
            throw Refuse(ChoiceNotString);
        return field.Value.GetString() ?? string.Empty;
    }

    private static void ValidatePrefix(string prefix)
    {
        if (prefix.Length == 0)
            return;
        if (prefix[0] == '/' || prefix.Contains('\\'))
            throw Refuse(NeedsPrefix);
        var body = prefix.EndsWith('/') ? prefix[..^1] : prefix;
        foreach (var segment in body.Split('/'))
            if (segment.Length == 0 || segment == "." || segment == "..")
                throw Refuse(NeedsPrefix);
    }

    private static Side MatchSide(string side, PairDef pair)
    {
        var nameA = SideName(pair, Side.A);
        var nameB = SideName(pair, Side.B);
        var a = string.Equals(side, nameA, StringComparison.OrdinalIgnoreCase);
        var b = string.Equals(side, nameB, StringComparison.OrdinalIgnoreCase);
        if (a == b)
            throw Refuse($"A one-sided choice side must name the endpoint that holds the files, '{nameA}' or '{nameB}'." + Suffix);
        return a ? Side.A : Side.B;
    }

    private static string SideName(string? provider)
    {
        SyncEndpoints.TryMapProvider(provider, out var kind);
        return SyncEndpointPresentation.ShortLabel(kind);
    }

    private static Side LocalSide(PairDef pair)
    {
        var a = IsLocal(pair.A.Provider);
        var b = IsLocal(pair.B.Provider);
        if (a == b)
            throw Refuse(OneLocalSide);
        return a ? Side.A : Side.B;
    }

    private static bool IsLocal(string? provider) =>
        string.Equals(provider, SyncProviders.Local, StringComparison.OrdinalIgnoreCase);

    private static SyncCliUsageException Refuse(string message) => new(message);
}

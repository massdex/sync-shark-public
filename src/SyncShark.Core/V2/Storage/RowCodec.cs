using System.Text.Json;
using System.Text.Json.Serialization;

namespace Massdex.SyncShark.V2;

/// <summary>Maps item fields to and from their database columns and compares rows for change.</summary>
internal static class RowCodec
{
    private const int FileKind = 0;
    private const int FolderKind = 1;

    /// <summary>The one JSON options instance for enum-bearing payloads (pair definitions, actions).</summary>
    public static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Splits a path into its parent folder path and leaf name; the parent of a top-level item is "".</summary>
    public static (string Parent, string Name) Split(string path)
    {
        if (path.Length == 0) throw new ArgumentException("An item path must not be empty.", nameof(path));
        var slash = path.LastIndexOf('/');
        return slash < 0 ? ("", path) : (path[..slash], path[(slash + 1)..]);
    }

    /// <summary>The stored case-insensitive key of a path or name: equal keys ⇔ equal under OrdinalIgnoreCase.</summary>
    public static string Key(string value) => PathKey.Of(value);

    /// <summary>Joins a parent folder path and leaf name into a path.</summary>
    public static string Join(string parent, string name) => parent.Length == 0 ? name : parent + "/" + name;

    /// <summary>The stored integer for an item kind.</summary>
    public static long KindCode(ItemKind kind) => kind == ItemKind.Folder ? FolderKind : FileKind;

    /// <summary>The item kind for a stored integer.</summary>
    public static ItemKind KindFrom(long code) => code == FolderKind ? ItemKind.Folder : ItemKind.File;

    /// <summary>Parses a hex hash into its bytes; a non-hex value is rejected.</summary>
    public static byte[]? HashBlob(string? hex)
    {
        if (hex is null) return null;
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            throw new ArgumentException("A hash must be hex.", nameof(hex));
        }
    }

    /// <summary>Formats a hash blob as lower-case hex.</summary>
    public static string? HashHex(byte[]? blob) => blob is null ? null : Convert.ToHexString(blob).ToLowerInvariant();

    /// <summary>The exact UTC ticks of a timestamp.</summary>
    public static long MtimeTicks(DateTimeOffset mtime) => mtime.UtcTicks;

    /// <summary>The timestamp for stored UTC ticks.</summary>
    public static DateTimeOffset MtimeFrom(long ticks) => new(ticks, TimeSpan.Zero);

    /// <summary>Whether two items are the same row; the only "row changed" comparison.</summary>
    public static bool Same(ItemState x, ItemState y) =>
        string.Equals(x.Path, y.Path, StringComparison.Ordinal) && x.Kind == y.Kind &&
        string.Equals(x.ObjectId, y.ObjectId, StringComparison.Ordinal) &&
        string.Equals(x.VersionId, y.VersionId, StringComparison.Ordinal) &&
        string.Equals(x.Hash, y.Hash, StringComparison.OrdinalIgnoreCase) && x.Size == y.Size &&
        x.Mtime.UtcTicks == y.Mtime.UtcTicks && x.Flags == y.Flags &&
        string.Equals(x.IdentityJson, y.IdentityJson, StringComparison.Ordinal);

    /// <summary>Keeps a known hash when a metadata-only inventory omits it for an unchanged item.</summary>
    public static ItemState CarryHash(ItemState incoming, ItemState live)
    {
        // The name, not the content, was refused. A rename (including case-only) starts unmarked.
        if (string.Equals(incoming.Path, live.Path, StringComparison.Ordinal))
            incoming = incoming with { Flags = incoming.Flags | (live.Flags & (ItemFlags.ProviderNameRefused | ItemFlags.Quarantined)) };
        if (incoming.ObjectId == live.ObjectId && (live.Flags & ItemFlags.NeedsRehash) != 0)
            return incoming with { Hash = null, Flags = incoming.Flags | ItemFlags.NeedsRehash };
        if ((incoming.Flags & ItemFlags.NeedsRehash) != 0) return incoming with { Hash = null };
        return incoming.Hash is null && string.Equals(incoming.ObjectId, live.ObjectId, StringComparison.Ordinal) &&
        incoming.Size == live.Size && incoming.Mtime.UtcTicks == live.Mtime.UtcTicks
            ? incoming with { Hash = live.Hash }
            : incoming;
    }
}

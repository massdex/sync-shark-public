using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine;

// One path-identity owner: the helpers share the single case-table load and its fold invariants.
/// <summary>Frozen path identity and key rules (decision 82, , and the engine's facade over
/// the shared transfer-path validation; Core name mapping is retained but dormant.</summary>
public static class PathNames
{
    /// <summary>The engine's relative-path separator, always '/'.</summary>
    public const string PathSeparator = "/";

    private const int AsciiLimit = 0x80;
    private const int CaseFormatVersion = 1;
    private const string CaseFormatVersionProperty = "formatVersion";
    private const string CaseMappingsProperty = "mappings";
    private const int CaseMappingArity = 2;
    private const int MissingBoundary = -1;
    private static readonly (Dictionary<int, int> Map, bool AsciiFastPath) CaseTable = LoadCaseTable();

    /// <summary>The one frozen case-insensitive comparison for raw path spellings.</summary>
    public static IEqualityComparer<string> Comparer { get; } = new FrozenPathComparer();

    internal static int Hash(string path)
    {
        if (CaseTable.AsciiFastPath && Ascii.IsValid(path))
            return path.GetHashCode(StringComparison.OrdinalIgnoreCase);
        var hash = new HashCode();
        for (var index = 0; index < path.Length;)
            hash.Add(FoldNext(path, ref index));
        return hash.ToHashCode();
    }

    /// <summary>Identity under the frozen case table, without name mapping or trimming; two nulls are equal.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Same(string? left, string? right) =>
        ReferenceEquals(left, right) || left is not null && right is not null && SameSpelling(left, right);

    /// <summary>True when the spellings differ only in case.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CaseOnly(string left, string right) => Same(left, right) && left != right;

    /// <summary>A user's case-insensitive text filter against a path; the one owner of the path case rule.</summary>
    public static bool Contains(string path, string text) => path.Contains(text, StringComparison.OrdinalIgnoreCase);

    /// <summary>The raw Dropbox spelling; character substitution is deferred, so no mapping is active here.</summary>
    public static string LocalSpelling(string dropboxPath) => dropboxPath;
    /// <summary>The dormant mapped spelling, solely for the schema 4 to 5 upgrade's mappedspelling callback.</summary>
    internal static string UpgradeMappedSpelling(string dropboxPath) => DropboxNameMapping.Map(dropboxPath);
    /// <summary>The frozen case key; unpaired UTF-16 surrogates keep their original code units.</summary>
    public static string Key(string path)
    {
        var length = path.Length;
        if (CaseTable.AsciiFastPath && Ascii.IsValid(path))
            return string.Create(length, path, static (output, source) =>
            {
                for (var index = 0; index < source.Length; index++) output[index] = UpperAscii(source[index]);
            });
        return string.Create(length, path, static (output, source) =>
        {
            var written = 0;
            for (var index = 0; index < source.Length;)
            {
                var scalar = FoldNext(source, ref index);
                if (scalar <= char.MaxValue) output[written++] = (char)scalar;
                else written += new Rune(scalar).EncodeToUtf16(output[written..]);
            }
        });
    }
    /// <summary>The frozen comparison key of a raw Dropbox path; name mapping never changes a persisted key.</summary>
    public static string DropboxKey(string dropboxPath) => Key(dropboxPath);
    public static string KeyFor(Side side, string path) => side == Side.Local ? Key(path) : DropboxKey(path);

    /// <summary>The join key of one side, reusing the native key the caller already computed for Local.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string KeyFor(Side side, string path, string nativeKey) =>
        side == Side.Local ? nativeKey : DropboxKey(path);

    /// <summary>The spelling a path takes on one side; both sides keep the raw Dropbox spelling in this build.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string SpellingOn(Side side, string dropboxPath) =>
        side == Side.Local ? LocalSpelling(dropboxPath) : dropboxPath;

    /// <summary>True when the local spelling of this Dropbox path differs from the provider spelling.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsMapped(string dropboxPath) => LocalSpelling(dropboxPath) != dropboxPath;

    /// <summary>The same predicate when the caller already computed that path's local spelling.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsMapped(string dropboxPath, string localSpelling) => localSpelling != dropboxPath;

    /// <summary>True when the raw Dropbox spelling is a path Windows can transfer as spelled.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsTransferable(string dropboxPath) =>
        PathRules.IsWindowsTransferPath(dropboxPath);

    /// <summary>The Dropbox spelling of a finished case rename.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string CaseRenamePath(string path, string suffix) => path + suffix;

    public static Side Other(Side side) => side == Side.Local ? Side.Dropbox : Side.Local;
    /// <summary>Appends one name to a relative path, omitting the separator at the root.</summary>
    public static string Child(string parentPath, string name) =>
        parentPath.Length == 0 ? name : parentPath + PathSeparator + name;

    /// <summary>The index of the last '/' in a relative path, or -1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LastSeparator(ReadOnlySpan<char> path) => path.LastIndexOf(PathSeparator[0]);

    /// <summary>The index of the next '/' at or after <paramref name="startIndex"/>, or -1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int NextSeparator(string path, int startIndex) => path.IndexOf(PathSeparator[0], startIndex);

    /// <summary>The leaf name of a relative path; "" for an empty path or a trailing separator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Name(string path)
    {
        _ = path.Length; // the old LastIndexOf threw NullReferenceException for a null path
        var cut = LastSeparator(path);
        return cut < 0 ? path : path[(cut + 1)..];
    }

    /// <summary>The parent of a relative path; "" for a top-level path.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Parent(string path)
    {
        _ = path.Length; // the old LastIndexOf threw NullReferenceException for a null path
        var cut = LastSeparator(path);
        return cut < 0 ? "" : path[..cut];
    }

    /// <summary>Maps a destination through its nearest saved parent; the reader supplies that parent's binding by side and key.</summary>
    public static string MappedDestination<T>(Side to, string fromPath, T state,
        Func<T, Side, string, SyncedEntry?> readSaved)
    {
        if (fromPath.Length == 0) return string.Empty;
        var separator = LastSeparator(fromPath);
        if (separator < 0) return SpellingOn(to, fromPath);
        var parent = fromPath[..separator];
        var name = fromPath[(separator + 1)..];
        var from = Other(to);
        var saved = readSaved(state, from, KeyFor(from, parent));
        return Child(saved?.PathOn(to) ?? MappedDestination(to, parent, state, readSaved), SpellingOn(to, name));
    }

    /// <summary>The non-root folders strictly above a relative path, deepest first.</summary>
    public static IEnumerable<string> Ancestors(string path) => PairPath.Ancestors(path);

    /// <summary>The acquired selection's ancestor keys, preserving its canonical key form.</summary>
    public static IEnumerable<string> Ancestors(WorkSelection selection) => Ancestors(selection.Key);

    /// <summary>True when an ordinal key lies strictly under another, at a segment boundary.
    /// An empty prefix therefore matches "/Child" but not "Child" or "".</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsUnderKey(string key, string prefix) =>
        key.Length > prefix.Length && key[prefix.Length] == PathSeparator[0] &&
        key.AsSpan().StartsWith(prefix, StringComparison.Ordinal);

    /// <summary>True when an ordinal key is the prefix itself or lies strictly under it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSelfOrUnderKey(string key, string prefix) => key == prefix || IsUnderKey(key, prefix);

    /// <summary>Whether responsibility at another key covers this item, or lies below a folder being considered.</summary>
    internal static bool ResponsibilityOverlaps(string key, string otherKey, bool isFolder) =>
        PrefixSet.ResponsibilityOverlaps(key, otherKey, isFolder);

    /// <summary>The tree form of a saved key: a Dropbox key already uses '/', a Local key uses the OS separator.</summary>
    public static string TreeKey(string key) =>
        key.Contains(PathSeparator, StringComparison.Ordinal) ? key :
            key.Replace(Path.DirectorySeparatorChar, PathSeparator[0]);

    /// <summary>The minimal roots: exact repeats, and any key under another root on its side, dropped.</summary>
    public static IReadOnlyList<(Side Side, string Key)> DisjointRoots(IEnumerable<(Side Side, string Key)> roots)
    {
        var distinct = new HashSet<(Side Side, string Key)>(roots);
        return distinct.Where(root => !Ancestors(root.Key).Any(key => distinct.Contains((root.Side, key)))).ToArray();
    }

    /// <summary>The ordinal key range of a path's strict descendants: it starts with key + separator, and the
    /// next code unit after the separator ends that range.</summary>
    public static (string Low, string High) DescendantBounds(string key) =>
        (key + PathSeparator, key + (char)(PathSeparator[0] + 1));

    /// <summary>True when a path is the prefix itself or lies under it, ignoring case. An empty prefix matches
    /// only itself, unlike <see cref="IsUnderKey"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSelfOrUnder(string path, string prefix) =>
        Same(path, prefix) || prefix.Length > 0 && UnderBoundary(path, prefix) >= 0;

    /// <summary>Number of path segments: one plus the '/' count; "" has depth one.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Depth(string path)
    {
        _ = path.Length; // a null path must throw NullReferenceException, as the old separator count did
        return path.AsSpan().Count(PathSeparator[0]) + 1;
    }

    /// <summary>True when a name is one path segment: non-empty, not all dots, and no separator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSingleSegment(string? name) => !string.IsNullOrEmpty(name) && !name.AsSpan().Trim('.').IsEmpty &&
        name.IndexOf(PathSeparator, StringComparison.Ordinal) < 0 &&
        name.IndexOf(Path.DirectorySeparatorChar) < 0 && name.IndexOf(Path.AltDirectorySeparatorChar) < 0;

    /// <summary>A Dropbox root spelling: starts with '/', no backslash, and no drive colon in the first segment.</summary>
    public static bool IsDropboxRoot(string path)
    {
        if (path.Length == 0 || path[0] != PathSeparator[0] || path.Contains('\\')) return false;
        var start = PathSeparator.Length;
        var slash = path.IndexOf(PathSeparator[0], start);
        var end = slash < 0 ? path.Length : slash;
        return path.AsSpan(start, end - start).IndexOf(':') < 0;
    }

    /// <summary>A root without its trailing separators; all-slash and empty roots become "".</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string TrimDropboxRoot(string root) => root.TrimEnd(PathSeparator[0]);

    /// <summary>A Dropbox path under a root: the trimmed root itself for an empty relative path.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string RootedDropboxPath(string root, string relative)
    {
        var trimmed = TrimDropboxRoot(root);
        return relative.Length == 0 ? trimmed : trimmed + PathSeparator + relative;
    }

    /// <summary>The path below a trimmed Dropbox root, or null when it is not strictly inside that root.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string? RelativeUnderDropboxRoot(string root, string path) =>
        UnderBoundary(path, root) is var boundary && boundary >= 0 ? path[(boundary + 1)..] : null;

    private static bool SameSpelling(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        if (left.Length != right.Length) return false;
        if (CaseTable.AsciiFastPath)
        {
            if (Ascii.EqualsIgnoreCase(left, right)) return true;
            if (Ascii.IsValid(left)) return false;
        }
        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
            if (FoldNext(left, ref leftIndex) != FoldNext(right, ref rightIndex)) return false;
        return leftIndex == left.Length && rightIndex == right.Length;
    }

    private static int UnderBoundary(string path, string prefix)
    {
        var prefixLength = prefix.Length;
        return path.Length > prefixLength && path[prefixLength] == PathSeparator[0] &&
            SameSpelling(path.AsSpan()[..prefixLength], prefix) ? prefixLength : MissingBoundary;
    }

    private static int FoldNext(ReadOnlySpan<char> path, ref int index)
    {
        int scalar = path[index++];
        if (char.IsHighSurrogate((char)scalar) && index < path.Length && char.IsLowSurrogate(path[index]))
            scalar = char.ConvertToUtf32((char)scalar, path[index++]);
        if (CaseTable.AsciiFastPath && scalar < AsciiLimit) return UpperAscii((char)scalar);
        return CaseTable.Map.TryGetValue(scalar, out var folded) ? folded : scalar;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static char UpperAscii(char value) => value is >= 'a' and <= 'z' ? (char)(value - ('a' - 'A')) : value;

    private static (Dictionary<int, int> Map, bool AsciiFastPath) LoadCaseTable()
    {
        using var document = CliCatalogResource.Load(typeof(PathNames).Assembly, ResourceNames.PathCase);
        var root = document.RootElement;
        if (root.GetProperty(CaseFormatVersionProperty).GetInt32() != CaseFormatVersion)
            throw new InvalidDataException(ResourceNames.PathCase);
        var map = new Dictionary<int, int>();
        var asciiPartition = true;
        foreach (var entry in root.GetProperty(CaseMappingsProperty).EnumerateArray())
        {
            if (entry.GetArrayLength() != CaseMappingArity) throw new InvalidDataException(ResourceNames.PathCase);
            var source = ReadScalar(entry[0]);
            var target = ReadScalar(entry[1]);
            if (new Rune(source).Utf16SequenceLength != new Rune(target).Utf16SequenceLength ||
                !map.TryAdd(source, target)) throw new InvalidDataException(ResourceNames.PathCase);
            asciiPartition &= (source < AsciiLimit) == (target < AsciiLimit);
        }
        foreach (var target in map.Values)
            if (map.TryGetValue(target, out var next) && next != target)
                throw new InvalidDataException(ResourceNames.PathCase);
        for (var scalar = 0; scalar < AsciiLimit; scalar++)
            asciiPartition &= (map.TryGetValue(scalar, out var target) ? target : scalar) == UpperAscii((char)scalar);
        // Mixing runtime ASCII hashes with folded hashes is safe only while equal inputs stay in one partition.
        return (map, asciiPartition);
    }

    private static int ReadScalar(JsonElement value)
    {
        if (!int.TryParse(value.GetString(), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
                out var scalar) || !Rune.IsValid(scalar))
            throw new InvalidDataException(ResourceNames.PathCase);
        return scalar;
    }

    /// <summary>The real full path without a trailing separator; the volume root keeps its separator.</summary>
    public static string NormalizeLocalPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Returns the normalized relative suffix, or null when the prefix does not match; equal volume roots
    /// return an empty suffix.</summary>
    public static string? RelativeUnderLocalRoot(string root, string candidate)
    {
        var outer = NormalizeLocalPath(root);
        var inner = NormalizeLocalPath(candidate);
        var prefix = outer.EndsWith(Path.DirectorySeparatorChar) ? outer : outer + Path.DirectorySeparatorChar;
        return inner.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? inner[prefix.Length..].Replace(Path.DirectorySeparatorChar, PathSeparator[0])
            : null;
    }
}

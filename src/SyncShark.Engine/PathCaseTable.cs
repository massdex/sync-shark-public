using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.Engine;

internal static class PathCaseTable
{
    private const int AsciiLimit = 0x80;
    private const int CaseFormatVersion = 1; // Version of engine-path-case.json understood by this reader.
    private const string CaseFormatVersionProperty = "formatVersion";
    private const string CaseMappingsProperty = "mappings";
    private const int SourceIndex = 0;
    private const int TargetIndex = SourceIndex + 1;
    private static readonly (Dictionary<int, int> Map, bool AsciiFastPath) CaseTable = LoadCaseTable();

    internal static bool AsciiFastPath => CaseTable.AsciiFastPath;

    internal static int FoldNext(ReadOnlySpan<char> path, ref int index)
    {
        int scalar = path[index++];
        if (char.IsHighSurrogate((char)scalar) && index < path.Length && char.IsLowSurrogate(path[index]))
            scalar = char.ConvertToUtf32((char)scalar, path[index++]);
        if (CaseTable.AsciiFastPath && scalar < AsciiLimit) return UpperAscii((char)scalar);
        return CaseTable.Map.TryGetValue(scalar, out var folded) ? folded : scalar;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static char UpperAscii(char value) => value is >= 'a' and <= 'z' ? (char)(value - ('a' - 'A')) : value;

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
            if (entry.GetArrayLength() != TargetIndex + 1) throw new InvalidDataException(ResourceNames.PathCase);
            var source = ReadScalar(entry[SourceIndex]);
            var target = ReadScalar(entry[TargetIndex]);
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
}

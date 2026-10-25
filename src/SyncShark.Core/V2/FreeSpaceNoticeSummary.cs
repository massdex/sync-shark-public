using System.Globalization;

namespace Massdex.SyncShark.V2;

/// <summary>A display-only volume summary. Members remain the original per-path notices, never stored as a group.</summary>
public sealed record FreeSpaceNoticeSummary(string VolumeRoot, int Count, long RequiredBytes,
    long? FreeBytes, long? ReserveBytes, IReadOnlyList<string> Examples, IReadOnlyList<Notice> Members)
{
    public string Detail => string.Create(CultureInfo.InvariantCulture,
        $"{VolumeRoot}: {Count} pending item(s), required {RequiredBytes} bytes; free {Bytes(FreeBytes)}; reserve {Bytes(ReserveBytes)}; examples: {string.Join(", ", Examples)}. Free space and retry; other work continues.");

    private static string Bytes(long? value) => value is { } bytes
        ? bytes.ToString(CultureInfo.InvariantCulture) + " bytes" : "unknown";
}

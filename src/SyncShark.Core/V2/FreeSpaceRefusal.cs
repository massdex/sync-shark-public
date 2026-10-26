using System.Globalization;

namespace Massdex.SyncShark.V2;

/// <summary>A pending item's stored notice text and stable reason key.</summary>
/// <param name="VolumeRoot">
/// The normalized path root the refusal concerns (for example <c>C:\</c>), stable for per-volume aggregation; null
/// only when the path's root cannot be parsed.
/// </param>
/// <param name="RequiredBytes">The demand held against that volume (the copy's source size, or the victim's size).</param>
/// <param name="FreeBytes">The observed free bytes on that volume, or null when they could not be read.</param>
/// <param name="ReserveBytes">The reserve that must remain on that volume, or null when it could not be established.</param>
public sealed record FreeSpaceRefusal(string Detail, string ReasonKey, string? VolumeRoot = null,
    long RequiredBytes = 0, long? FreeBytes = null, long? ReserveBytes = null)
{
    internal const string InsufficientReasonKey = "insufficient-free-space";
    internal const string UnknownSpaceReasonKey = "free-space-unknown";
    internal const string UnknownReserveReasonKey = "free-space-reserve-unknown";

    internal static FreeSpaceRefusal Shortage(string volume, string? volumeRoot, long needed, long free,
        long reserve) => new(
        string.Format(CultureInfo.InvariantCulture,
            "Not enough free space on {0}: needs {1}, free {2}, reserve {3}. " +
            "Options: free space and retry; exclude this folder",
            volume, Bytes(needed), Bytes(free), Bytes(reserve)), InsufficientReasonKey, volumeRoot, needed, free,
        reserve);

    internal static FreeSpaceRefusal UnknownSpace(string path, string? volumeRoot, long needed) => new(
        "Could not read the target volume's free space for '" + path +
        "'; this item is pending until it can be checked. Options: free space and retry; exclude this folder",
        UnknownSpaceReasonKey, volumeRoot, needed);

    internal static FreeSpaceRefusal UnknownReserve(string volume, string? volumeRoot, long needed,
        long free) => new(
        "Could not read the Sync Shark state database size on " + volume +
        "; this item is pending until the data folder is available. " +
        "Options: free space and retry; exclude this folder", UnknownReserveReasonKey, volumeRoot, needed, free);

    private static string Bytes(long bytes) => bytes >= 1L << 30
        ? (bytes / 1073741824d).ToString("0.0", CultureInfo.InvariantCulture) + " GiB"
        : bytes >= 1L << 20
            ? (bytes / 1048576d).ToString("0.0", CultureInfo.InvariantCulture) + " MiB"
            : bytes.ToString(CultureInfo.InvariantCulture) + " B";
}

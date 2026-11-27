using System.Globalization;

namespace Massdex.SyncShark.Engine;

/// <summary>The native signed text representation of a local file identity.</summary>
internal static class LocalIdentity
{
    // Entry.Id is the listing's signed read of these bits (WindowsDirectoryListing).
    internal static string FileIdText(ulong fileId) =>
        unchecked((long)fileId).ToString(CultureInfo.InvariantCulture);

    // A missing or zero id is what Windows reports when it supplies none; such a value never groups files as one identity.
    internal static bool IsKnown(string? id) => !string.IsNullOrEmpty(id) && id != FileIdText(0);
}

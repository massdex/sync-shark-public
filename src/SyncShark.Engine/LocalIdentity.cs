using System.Globalization;

namespace Massdex.SyncShark.Engine;

/// <summary>The native signed text representation of a local file identity.</summary>
internal static class LocalIdentity
{
    // Entry.Id is the listing's signed read of these bits (WindowsDirectoryListing).
    internal static string FileIdText(ulong fileId) =>
        unchecked((long)fileId).ToString(CultureInfo.InvariantCulture);
}

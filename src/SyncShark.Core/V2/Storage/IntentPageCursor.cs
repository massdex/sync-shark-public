using System.Globalization;

namespace Massdex.SyncShark.V2;

/// <summary>The invariant (updated, rowid) keyset cursor shared by intent pages and the in-memory store.</summary>
internal static class IntentPageCursor
{
    public static string Encode(long updated, long rowid) =>
        updated.ToString(CultureInfo.InvariantCulture) + ":" + rowid.ToString(CultureInfo.InvariantCulture);

    public static (long Updated, long RowId) Parse(string cursor)
    {
        var separator = cursor.IndexOf(':');
        if (separator <= 0 || separator == cursor.Length - 1 ||
            !long.TryParse(cursor.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var updated) ||
            !long.TryParse(cursor.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var rowid))
            throw new ArgumentException("The intent page cursor must be '<updated>:<rowid>' with nonnegative integers.", nameof(cursor));
        return (updated, rowid);
    }
}

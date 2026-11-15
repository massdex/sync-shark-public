namespace Massdex.SyncShark.V2;

/// <summary>
/// One filters-and-limit request for a pair's Recovery index rows. Pages are ordered lexicographically by recovery
/// id; <see cref="AfterId"/> is the exclusive cursor of the previous page, so one keyset scan reads every row once.
/// </summary>
/// <param name="Id">Exact recovery id, or null for any.</param>
/// <param name="Since">Inclusive lower bound on the row's creation time, or null for any. Stored creation times are
/// whole milliseconds, so a sub-millisecond bound rounds up to the next millisecond.</param>
/// <param name="PathPrefix">A literal pair-relative path: the path itself or a descendant, matched ignoring case.</param>
/// <param name="Side">Only copies retained from this side, or null for both.</param>
/// <param name="ActionId">Exact source action id, or null for any.</param>
/// <param name="AfterId">The last recovery id of the previous page; rows at or before it are not returned.</param>
/// <param name="Limit">Page size, 1..500.</param>
public sealed record RecoveryQuery(string? Id = null, DateTimeOffset? Since = null, string? PathPrefix = null,
    Side? Side = null, string? ActionId = null, string? AfterId = null, int Limit = 100)
{
    /// <summary>The largest page a caller may request.</summary>
    public const int MaximumLimit = 500;

    /// <summary>Rejects a page size outside 1..500; every store implementation enforces the same bound.</summary>
    internal void Validate()
    {
        if (Limit is < 1 or > MaximumLimit)
            throw new ArgumentOutOfRangeException(nameof(Limit), Limit, "Recovery pages are limited to 1..500.");
    }

    /// <summary>
    /// The inclusive Since bound in stored whole milliseconds: a sub-millisecond bound rounds up, so SQL and an
    /// in-memory comparison over millisecond-aligned records select exactly the same rows.
    /// </summary>
    internal long? SinceMillisecondsCeiling()
    {
        if (Since is not { } since) return null;
        var milliseconds = since.ToUnixTimeMilliseconds();
        return since == DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) ? milliseconds : milliseconds + 1;
    }
}

namespace Massdex.SyncShark.V2;

/// <summary>
/// The bounded open-conflict catalog SQL: one indexed page, one capped total and one reason-scoped
/// free-space summary. Every statement is ordered by or seeks an index, so a page never materializes the
/// open-notice table and its cost does not grow with the number of open notices.
/// </summary>
internal static class ConflictCatalog
{
    /// <summary>The visible-conflict predicate shared by every bounded statement (exactly the open, non-ignored rows).</summary>
    internal static string VisibleFilter(string alias) =>
        NoticeTable.OpenStateSql(alias + ".state") + " AND NOT (" + NoticeTable.IgnoredSql(alias + ".choice") + ")";

    /// <summary>
    /// One page in the catalog order <c>(created, notice_id)</c>. <paramref name="index"/> is <c>nt_page</c>
    /// for a global/account read and <c>nt_open</c> for an exact pair, whose WITHOUT ROWID primary key appends
    /// <c>notice_id</c> to the index so the same order needs no sort.
    /// </summary>
    internal static string Page(string index, string filters) =>
        "SELECT " + NoticeTable.ReadColumns + " FROM notice n INDEXED BY " + index +
        " JOIN pair p ON p.pair_no=n.pair_no WHERE " + filters +
        " ORDER BY n.created, n.notice_id LIMIT $limit OFFSET $offset;";

    /// <summary>A capped COUNT: the icon and the human total stay bounded even with one million open notices.</summary>
    internal static string Count(string filters) =>
        "SELECT COUNT(*) FROM (SELECT 1 FROM notice n INDEXED BY nt_open JOIN pair p ON p.pair_no=n.pair_no WHERE " +
        filters + " LIMIT $cap);";

    /// <summary>
    /// The free-space notices behind the summary. <see cref="FreeSpacePresentation.Group"/> re-sorts its own
    /// members, so this reason-scoped page needs no ORDER BY and stops at the cap instead of walking the table.
    /// </summary>
    internal static string FreeSpace(string filters) =>
        "SELECT " + NoticeTable.ReadColumns + " FROM notice n INDEXED BY nt_free_space " +
        "JOIN pair p ON p.pair_no=n.pair_no WHERE " + filters + " AND " +
        NoticeTable.FreeSpaceReasonSql("n.reason_key") + " LIMIT $cap;";
}

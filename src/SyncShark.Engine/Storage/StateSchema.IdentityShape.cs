namespace Massdex.SyncShark.Engine.Storage;

/// <summary>The identity membership statements, one per selection shape over one shared predicate.</summary>
public static partial class StateSchema
{
    // Without statistics the planner prefers UNIQUE(parent_anchor,obligation_key), which scans every top-level row in
    // range, so a top-level group seeks work_identity; a residual keeps that unique lookup's single exact entry.
    private const string TopLevelIdentityWork = " INDEXED BY work_identity";
    private const string ReadIdentityFrom = "SELECT " + WorkColumns + " FROM change_work w";
    private const string ReadIdentityWhere = " WHERE " + IdentitySelection + " AND w.work_id>$after ORDER BY w.work_id LIMIT $take;";
    public const string ReadIdentitySelection = ReadIdentityFrom + ReadIdentityWhere;
    public const string ReadResidualIdentitySelection = ReadIdentityFrom + ReadIdentityWhere;
    private const string CompleteIdentityWhere = " WHERE " + IdentitySelection + NoRenameResponsibility + ";";
    public const string CompleteIdentitySelection = "DELETE FROM change_work AS w" + TopLevelIdentityWork + CompleteIdentityWhere;
    public const string CompleteResidualIdentitySelection = "DELETE FROM change_work AS w" + CompleteIdentityWhere;
    private const string SaveBeforeIdentityWhere = " SET before_json=$nextBefore WHERE " + IdentitySelection + ";";
    public const string SaveBeforeIdentity = "UPDATE change_work AS w" + TopLevelIdentityWork + SaveBeforeIdentityWhere;
    public const string SaveBeforeResidualIdentity = "UPDATE change_work AS w" + SaveBeforeIdentityWhere;
}

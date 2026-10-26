namespace Massdex.SyncShark.V2;

internal sealed partial class NoticeTable
{
    internal static string ByIdSql =>
        "SELECT n.notice_id,p.pair_id,n.rule,n.kind,n.path,n.detail,n.reason_key," + ActionableSql("n.state") + " FROM notice n " +
        "JOIN pair p ON p.pair_no=n.pair_no WHERE n.pair_no=$p AND n.notice_id=$id;";
}

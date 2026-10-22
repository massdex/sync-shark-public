using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityDatabase
{
    private sealed record EssentialHistorySelection(string AsOf,SyncActivityRetentionPolicy Policy,string TargetGeneration);
    private static readonly ConditionalWeakTable<SqliteConnection,EssentialHistorySelection> EssentialHistorySelections=new();
    private static void BindEssentialHistorySelection(SqliteConnection source,SyncEssentialSuccessorReceipt receipt)
    {
        if(receipt.HistoryAsOfUtc is null || !DateTimeOffset.TryParse(receipt.HistoryAsOfUtc,CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,out var instant) || Utc(instant)!=receipt.HistoryAsOfUtc ||
            receipt.HistoryPolicy!=SyncActivityRetentionPolicy.Default || !Guid.TryParseExact(receipt.TargetGeneration,"N",out _))
            throw new InvalidDataException("Successor receipt has no supported fixed recent-history selection; use a new isolated target.");
        EssentialHistorySelections.Add(source,new(receipt.HistoryAsOfUtc,receipt.HistoryPolicy,receipt.TargetGeneration));
    }
    private static string ExpandEssentialHistorySql(SqliteConnection source,string sql)
    {
        if(!sql.Contains("__essential_history_as_of__",StringComparison.Ordinal))return sql;
        if(!EssentialHistorySelections.TryGetValue(source,out var selection))
            throw new InvalidDataException("Recent history selection time is not bound.");
        return sql.Replace("__essential_history_as_of__","'"+selection.AsOf+"'",StringComparison.Ordinal)
            .Replace("__essential_history_max_events__",selection.Policy.MaximumEvents.ToString(CultureInfo.InvariantCulture),StringComparison.Ordinal)
            .Replace("__essential_history_max_bytes__",selection.Policy.MaximumEncodedBytes.ToString(CultureInfo.InvariantCulture),StringComparison.Ordinal);
    }
    private static async Task<SyncEssentialSuccessorReceipt> InitialEssentialReceiptAsync(string targetPath,
        SyncEssentialSourceIdentity source,SyncEssentialSuccessorOptions options,CancellationToken token)
    {
        if(!File.Exists(targetPath))
            return new(source,EssentialOptionsHash(options),Guid.NewGuid().ToString("N"),false,[],
                HistoryAsOfUtc:Utc(DateTimeOffset.UtcNow),HistoryPolicy:SyncActivityRetentionPolicy.Default);
        await using var inspect=new SqliteConnection(ConnectionString(targetPath,SqliteOpenMode.ReadOnly,false));
        await inspect.OpenAsync(token);
        await ExecuteAsync(inspect,null,"PRAGMA query_only=ON; PRAGMA trusted_schema=OFF;",token);
        await RequireExactCurrentSchemaAsync(inspect,token,allowIncompleteSuccessor:true);
        var receipt=await ReadEssentialReceiptAsync(inspect,token);
        BindEssentialReceipt(receipt,source,options,
            (await ScalarStringAsync(inspect,null,"SELECT database_generation FROM installation;",token))!);
        return receipt;
    }

    private const string EssentialHistoryContextColumns="context_key,pair_id,run_id,context_json,encoded_bytes,created_utc";
    // Only already-detached terminal display facts qualify. Unknown/undrainable
    // or progress events remain in the original with exact disposition counts.
    private static string EssentialRecentHistorySql=>
        "SELECT "+EssentialHistoryColumns+" FROM (SELECT "+EssentialHistoryColumns+
        ",row_number() OVER(ORDER BY rowid DESC) AS recent_ordinal,"+
        "sum(encoded_bytes+CASE WHEN context_ordinal=1 THEN context_bytes ELSE 0 END) OVER(ORDER BY rowid DESC) AS recent_bytes "+
        "FROM (SELECT "+string.Join(",",EssentialHistoryColumns.Split(',').Select(x=>"h."+x))+
        ",coalesce(c.encoded_bytes,0) AS context_bytes,"+
        "row_number() OVER(PARTITION BY h.activity_context_key ORDER BY h.rowid DESC) AS context_ordinal "+
        "FROM history_event h LEFT JOIN activity_context c ON c.context_key=h.activity_context_key "+
        "WHERE h.retention_class='ordinary' AND h.event_kind='presentation' AND h.operation_id IS NULL "+
        "AND h.producer_command_id IS NOT NULL AND h.producer_ordinal IS NOT NULL "+
        "AND julianday(h.occurred_utc)<=julianday(__essential_history_as_of__) "+
        "AND julianday(h.expires_utc)>julianday(__essential_history_as_of__) "+
        "AND json_type(h.detail_json,'$.Progress') IS NULL "+
        "AND ((h.producer_kind='lifecycle' AND json_extract(h.detail_json,'$.OperationSnapshot.Status') IN ('completed','cancelled','superseded')) "+
        "OR (h.producer_kind='run' AND json_extract(h.detail_json,'$.Severity') IN ('Success','Error','Warning') "+
        "AND EXISTS(SELECT 1 FROM command_request r WHERE r.command_request_id=h.producer_command_id "+
        "AND r.status IN ('completed','cancelled','rejected','stale') AND r.activity_closed=1))))) "+
        "WHERE recent_ordinal<=__essential_history_max_events__ AND recent_bytes<=__essential_history_max_bytes__";
    private const string EssentialPendingHistorySql="SELECT "+EssentialHistoryColumns+
        " FROM (SELECT "+EssentialHistoryColumns+
        ",row_number() OVER(PARTITION BY json_extract(detail_json,'$.RunId') ORDER BY rowid DESC) AS ordinal "+
        "FROM history_event WHERE "+EssentialPendingHistoryPredicate+") WHERE ordinal<=32";
    private static string EssentialSelectedHistorySql=>EssentialPendingHistorySql+" UNION ALL "+EssentialRecentHistorySql;
    private static readonly EssentialConfigurationTable[] EssentialHistoryContextTables=
    [
        new("activity_context",EssentialHistoryContextColumns,"context_key",
            "SELECT "+EssentialHistoryContextColumns+" FROM activity_context WHERE context_key IN "+
            "(SELECT activity_context_key FROM ("+EssentialSelectedHistorySql+"))")
    ];
    private static async Task VerifyEssentialHistoryAccountingAsync(SqliteConnection target,CancellationToken token)
    {
        if(await ScalarLongAsync(target,null,"""
            SELECT count(*) FROM activity_context c WHERE
                c.ordinary_references<>(SELECT count(*) FROM history_event h WHERE h.activity_context_key=c.context_key
                    AND h.retention_class IN ('ordinary','diagnostic'))
                OR c.diagnostic_references<>(SELECT count(*) FROM history_event h WHERE h.activity_context_key=c.context_key
                    AND h.retention_class='diagnostic');
            """,token)!=0)
            throw new InvalidDataException("Successor context reference accounting differs.");
        if(await ScalarLongAsync(target,null,"""
            SELECT count(*) FROM installation WHERE
                history_ordinary_count<>(SELECT count(*) FROM history_event WHERE retention_class IN ('ordinary','diagnostic'))
                OR history_ordinary_bytes<>(SELECT coalesce(sum(encoded_bytes),0) FROM history_event WHERE retention_class IN ('ordinary','diagnostic'))
                    +(SELECT coalesce(sum(encoded_bytes),0) FROM activity_context WHERE ordinary_references>0);
            """,token)!=0)
            throw new InvalidDataException("Successor recent-history budget accounting differs.");
    }
}

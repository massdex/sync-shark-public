using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityDatabase
{
    private const string EssentialLegacyOriginalSelection = "SELECT command_request_id FROM command_request WHERE command_kind='run_pair' "+
        "AND command_request_id IN ("+EssentialBaseCommands+") AND consent_id IS NULL "+
        "AND json_type(payload_json,'$.reviewedPlan')='object' AND json_type(payload_json,'$.retryOfRunId') IS NOT 'text'";
    private const string EssentialLegacyOriginals="SELECT command_request_id FROM _essential_legacy_originals";
    private static string ExpandEssentialLegacyOriginalsSql(string sql)
    {
        if(!sql.Contains("_essential_legacy_originals",StringComparison.Ordinal))return sql;
        var prefix="WITH _essential_legacy_originals AS MATERIALIZED ("+EssentialLegacyOriginalSelection+")";
        return sql.StartsWith("WITH ",StringComparison.Ordinal)?prefix+","+sql[5..]:prefix+" "+sql;
    }
    private const string EssentialLegacyConsentScans =
        "SELECT json_extract(payload_json,'$.reviewedPlan.LocalScanGenerationId') FROM command_request WHERE command_request_id IN ("+EssentialLegacyOriginals+") "+
        "UNION SELECT json_extract(payload_json,'$.reviewedPlan.DropboxScanGenerationId') FROM command_request WHERE command_request_id IN ("+EssentialLegacyOriginals+")";
    private const string EssentialLegacyDescendants = """
        WITH RECURSIVE descendants(id) AS (
            SELECT $root
            UNION SELECT c.command_request_id FROM command_request c JOIN descendants d
                ON json_extract(c.payload_json,'$.retryOfRunId')=d.id WHERE c.command_kind='run_pair'
        ) SELECT id FROM descendants
        """;
    private sealed class EssentialLegacyApproval
    {
        internal required string CommandId, ConsentId, PairId, RequestHash, CompactJson;
        internal required SyncBootstrapActionPlan Plan;
        internal required List<SyncAuthorityConsentPreparedChoice> Choices;
        internal required object?[] Header;
        internal required string[] States;
        internal required string?[] Issued, Completed;
        internal required long[] References;
        internal readonly Dictionary<string,int> Paths = new(StringComparer.Ordinal);
        internal readonly Dictionary<string,int> Operations = new(StringComparer.Ordinal);
    }
    // A single current legacy plan, never an all-history in-memory graph.
    private sealed class EssentialLegacyApprovalCache
    { internal EssentialLegacyApproval? Current; internal bool HasOriginals; }
    private static readonly ConditionalWeakTable<SqliteConnection,EssentialLegacyApprovalCache> EssentialLegacyApprovals=new();

    private static bool EssentialDerivedConsentTable(string name)=>name is "consent_header" or "consent_item";
    private static Task<long> EssentialDerivedConsentCountAsync(SqliteConnection source,SqliteTransaction snapshot,
        string table,CancellationToken token)=>ScalarLongAsync(source,snapshot,EssentialSourceSql(source,
        "SELECT "+(table=="consent_header"?"count(*)":"coalesce(sum(json_array_length(payload_json,'$.reviewedPlan.Choices')),0)")+
        " FROM command_request WHERE command_request_id IN ("+EssentialLegacyOriginals+");"),token);

    private static async Task<string?> EssentialLegacyRootAsync(SqliteConnection source,SqliteTransaction snapshot,
        string commandId,CancellationToken token)
    {
        var visited=new HashSet<string>(StringComparer.Ordinal);
        while(true)
        {
            if(!visited.Add(commandId)||visited.Count>100)
                throw new InvalidDataException("Legacy reviewed ancestry is cyclic or exceeds its existing100-command bound.");
            await using var q=source.CreateCommand();q.Transaction=snapshot;
            q.CommandText="""
                SELECT command_kind,json_extract(payload_json,'$.retryOfRunId'),
                    json_type(payload_json,'$.reviewedPlan'),json_extract(payload_json,'$.consentId')
                FROM command_request WHERE command_request_id=$id;
                """;
            q.Parameters.AddWithValue("$id",commandId);
            await using var row=await q.ExecuteReaderAsync(token);
            if(!await row.ReadAsync(token)||row.GetString(0)!="run_pair")
                throw new InvalidDataException("Legacy reviewed ancestry has a missing or non-run command.");
            if(!row.IsDBNull(1)){commandId=row.GetString(1);continue;}
            return !row.IsDBNull(2)&&row.GetString(2)=="object"&&row.IsDBNull(3)?commandId:null;
        }
    }

    private static async Task<EssentialLegacyApproval> ReadEssentialLegacyApprovalAsync(SqliteConnection source,
        SqliteTransaction snapshot,string rootId,CancellationToken token)
    {
        var cache=EssentialLegacyApprovals.GetOrCreateValue(source);
        if(cache.Current is { } current&&current.CommandId==rootId)return current;
        cache.Current=null;
        string raw,pair,created,status;
        await using(var q=source.CreateCommand())
        {
            q.Transaction=snapshot;q.CommandText="SELECT payload_json,pair_id,requested_utc,status FROM command_request WHERE command_request_id=$id AND command_kind='run_pair';";
            q.Parameters.AddWithValue("$id",rootId);
            await using var row=await q.ExecuteReaderAsync(token);
            if(!await row.ReadAsync(token))throw new InvalidDataException("Legacy original approval is absent.");
            raw=row.GetString(0);pair=row.GetString(1);created=row.GetString(2);status=row.GetString(3);
        }
        if(status is not ("accepted" or "executing" or "completed" or "cancelled"))
            throw new InvalidDataException("Legacy original command has no retained accepted approval.");
        var (plan,hash,id,header,choices,digest)=PrepareLegacyOriginalConsentBody(rootId,raw,token);
        if(plan.PairId!=pair)throw new InvalidDataException("Legacy approval and command name different pairs.");
        if(await ScalarLongAsync(source,snapshot,EssentialSourceSql(source,
            "SELECT count(*) FROM consent_header WHERE consent_id=$id OR originating_command_id=$root OR prepared_command_id=$root;"),
            token,("$id",id),("$root",rootId))!=0)
            throw new InvalidDataException("Legacy original collides with an existing normalized consent identity.");
        if(await ScalarLongAsync(source,snapshot,"""
            SELECT count(*) FROM scan_generation l JOIN scan_generation r
              ON l.scan_generation_id=$l AND r.scan_generation_id=$r
            WHERE l.pair_id=$pair AND r.pair_id=$pair AND l.side='local' AND r.side='dropbox'
              AND l.status='complete' AND r.status='complete';
            """,token,("$l",plan.LocalScanGenerationId),("$r",plan.DropboxScanGenerationId),("$pair",pair))!=1)
            throw new InvalidDataException("Legacy original consent requires its exact complete planning cuts.");
        var compact=JsonNode.Parse(header)!.AsObject();
        compact["originalRequestSha256"]=hash;
        var approval=new EssentialLegacyApproval{CommandId=rootId,ConsentId=id,PairId=pair,RequestHash=hash,
            CompactJson=compact.ToJsonString(),Plan=plan,Choices=choices,Header=[],
            States=Enumerable.Repeat("eligible",choices.Count).ToArray(),
            Issued=new string?[choices.Count],Completed=new string?[choices.Count],References=new long[choices.Count]};
        for(var i=0;i<choices.Count;i++)approval.Paths.Add(PilotPath.CanonicalComparisonKey(choices[i].RelativePath),i);
        var runs=new List<string>();
        await using(var runQuery=source.CreateCommand())
        {
            runQuery.Transaction=snapshot;runQuery.CommandText=EssentialLegacyDescendants;
            runQuery.Parameters.AddWithValue("$root",rootId);
            await using var runRows=await runQuery.ExecuteReaderAsync(token);
            while(await runRows.ReadAsync(token))runs.Add(runRows.GetString(0));
        }
        // The production retry reader owns exact group/history association, including
        // its strictly unissued pre-linkage form. Reuse it, never infer from paths alone.
        var owned=await LoadRunOperationHistoryCoreAsync(source,snapshot,runs,pair,token);
        // A retained outcome pointing at an absent action cannot make its choice eligible.
        if(await ScalarLongAsync(source,snapshot,"""
            SELECT count(*) FROM history_event h WHERE h.event_kind='presentation'
              AND json_extract(h.detail_json,'$.RunId') IN (SELECT value FROM json_each($runs))
              AND coalesce(json_extract(h.detail_json,'$.OperationId'),'')<>''
              AND NOT EXISTS(SELECT 1 FROM operation o WHERE o.operation_id=json_extract(h.detail_json,'$.OperationId'));
            """,token,("$runs",JsonSerializer.Serialize(runs)))!=0)
            throw new InvalidDataException("Legacy choice eligibility is unresolved: retained outcome has no exact action.");
        // Closed source-only actions still mark their original choice completed.
        await using(var q=source.CreateCommand())
        {
            q.Transaction=snapshot;
            q.CommandText=EssentialSourceSql(source,"""
                SELECT o.operation_id,o.pair_id,o.status,o.preconditions_json,o.terminal_utc,
                    (SELECT min(started_utc) FROM operation_attempt a WHERE a.operation_id=o.operation_id),
                    CASE WHEN o.operation_id IN (
                """+EssentialOperations+"""
                    ) THEN 1 ELSE 0 END,o.consent_id,o.consent_item_ordinal
                FROM operation o WHERE o.operation_id IN (SELECT value FROM json_each($operations))
                ORDER BY o.operation_id;
                """);
            q.Parameters.AddWithValue("$operations",JsonSerializer.Serialize(owned.Select(x=>x.OperationId)));
            await using var rows=await q.ExecuteReaderAsync(token);
            while(await rows.ReadAsync(token))
            {
                if(rows.GetString(1)!=pair)throw new InvalidDataException("Legacy reviewed action changed its original pair.");
                if(!rows.IsDBNull(7)||!rows.IsDBNull(8))
                    throw new InvalidDataException("Legacy action already carries a different normalized consent binding.");
                var op=rows.GetString(0);
                var ordinal=await EssentialLegacyOperationOrdinalAsync(source,snapshot,approval,op,rows.GetString(3),token);
                if(approval.Choices[ordinal].Action=="Skip for now")throw new InvalidDataException("Legacy operation claims an originally skipped choice.");
                approval.Operations.Add(op,ordinal);
                if(rows.GetInt64(6)!=0)approval.References[ordinal]++;
                if(!rows.IsDBNull(5))
                {
                    var issued=rows.GetString(5);
                    if(approval.Issued[ordinal] is null||string.CompareOrdinal(issued,approval.Issued[ordinal])<0)
                        approval.Issued[ordinal]=issued;
                    if(approval.States[ordinal]=="eligible")approval.States[ordinal]="issued";
                }
                if(rows.GetString(2)=="completed")
                {
                    if(rows.IsDBNull(4))throw new InvalidDataException("Completed legacy choice lacks its terminal timestamp.");
                    approval.States[ordinal]="completed";
                    var ended=rows.GetString(4);
                    if(approval.Completed[ordinal] is null||string.CompareOrdinal(ended,approval.Completed[ordinal])<0)
                        approval.Completed[ordinal]=ended;
                }
            }
        }
        await using(var summary=source.CreateCommand())
        {
            summary.Transaction=snapshot;summary.CommandText=
                "SELECT command_request_id,result_json,terminal_utc FROM command_request WHERE result_json IS NOT NULL "+
                "AND command_request_id IN ("+EssentialLegacyDescendants+") ORDER BY requested_utc,command_request_id;";
            summary.Parameters.AddWithValue("$root",rootId);
            await using var rows=await summary.ExecuteReaderAsync(token);
            while(await rows.ReadAsync(token))
                ApplyEssentialLegacyCompletedOutcomes(approval,rows.GetString(0),rows.GetString(1),
                    rows.IsDBNull(2)?null:rows.GetString(2));
        }
        var active=await ScalarLongAsync(source,snapshot,EssentialSourceSql(source,
            "SELECT count(*) FROM command_request WHERE command_request_id IN ("+EssentialLegacyDescendants+
            ") AND command_request_id IN ("+EssentialCommands+") AND status IN ('accepted','executing') "+
            "AND ordinary_issuance_closed_utc IS NULL AND admission_purpose IN ('normal_effect','control_allocation');"),
            token,("$root",rootId));
        var eligible=choices.Where((_,i)=>approval.States[i]=="eligible"&&approval.References[i]==0).Sum(x=>x.EncodedBytes);
        approval.Header=[id,rootId,pair,hash,(long)choices.Count,digest,header,(long)Encoding.UTF8.GetByteCount(header),
            (long)choices.Count,choices.Sum(x=>x.EncodedBytes),rootId,"active",0L,created,created,created,
            0L,"[]",0L,0L,1L,eligible,active,1L,null];
        cache.Current=approval;
        return approval;
    }

    private static void ApplyEssentialLegacyCompletedOutcomes(EssentialLegacyApproval approval,string commandId,
        string resultJson,string? completedUtc)
    {
        using var document=JsonDocument.Parse(resultJson);
        var root=document.RootElement;
        if(commandId==approval.CommandId){Apply(root);return;}
        if(root.TryGetProperty("pairId",out var pair)&&pair.GetString()==approval.PairId){Apply(root);return;}
        if(root.TryGetProperty("pairs",out var pairs)&&pairs.ValueKind==JsonValueKind.Array)
            foreach(var scope in pairs.EnumerateArray())
                if(scope.TryGetProperty("pairId",out var id)&&id.GetString()==approval.PairId)Apply(scope);
        void Apply(JsonElement scope)
        {
            if(!scope.TryGetProperty("files",out var files)||files.ValueKind!=JsonValueKind.Array)return;
            foreach(var item in files.EnumerateArray())
            {
                var decision=item.Deserialize<SyncDecision>();
                if(decision is null||!IsCompletedRetryOutcome(decision.Kind)||
                    !approval.Paths.TryGetValue(PilotPath.CanonicalComparisonKey(decision.RelativePath),out var ordinal))continue;
                if(completedUtc is null)
                    throw new InvalidDataException("Legacy choice eligibility is unresolved: successful summary has no terminal command.");
                approval.States[ordinal]="completed";
                if(approval.Completed[ordinal] is null||string.CompareOrdinal(completedUtc,approval.Completed[ordinal])<0)
                    approval.Completed[ordinal]=completedUtc;
            }
        }
    }

    private static async Task<int> EssentialLegacyOperationOrdinalAsync(SqliteConnection source,SqliteTransaction snapshot,
        EssentialLegacyApproval approval,string operationId,string preconditions,CancellationToken token)
    {
        var preferred=ReadReviewedPreconditionPathKey(preconditions);
        if(preferred is not null&&approval.Paths.TryGetValue(preferred,out var ordinal))return ordinal;
        int? match=null;
        await using var q=source.CreateCommand();q.Transaction=snapshot;
        q.CommandText="SELECT DISTINCT expected_path_key FROM operation_member WHERE operation_id=$op AND expected_path_key IS NOT NULL;";
        q.Parameters.AddWithValue("$op",operationId);await using var rows=await q.ExecuteReaderAsync(token);
        while(await rows.ReadAsync(token))
            if(approval.Paths.TryGetValue(PilotPath.CanonicalComparisonKey(rows.GetString(0)),out var found))
            {
                if(match is not null&&match!=found)throw new InvalidDataException("Legacy choice eligibility is unresolved: action maps to multiple original choices.");
                match=found;
            }
        return match??throw new InvalidDataException("Legacy choice eligibility is unresolved: action has no exact original choice.");
    }

    private static string NormalizeEssentialLegacyRequest(EssentialLegacyApproval approval,string commandId,
        string pairId,string requestJson)
    {
        if(pairId!=approval.PairId)throw new InvalidDataException("Legacy retry command changed its original pair.");
        if(commandId==approval.CommandId)
        {
            if(SyncAuthorityConsentEncoding.Sha256Hex(requestJson)!=approval.RequestHash)
                throw new InvalidDataException("Legacy original approval changed during conversion.");
            return approval.CompactJson;
        }
        using var document=JsonDocument.Parse(requestJson);
        using var original=JsonDocument.Parse(approval.CompactJson);
        var root=document.RootElement;var origin=original.RootElement;
        var revision=root.TryGetProperty("policyRevision",out var r)?r.GetInt64():(long?)null;
        var originalRevision=origin.TryGetProperty("policyRevision",out var v)?v.GetInt64():(long?)null;
        var namesPair=root.TryGetProperty("pairIds",out var pairs)&&pairs.ValueKind==JsonValueKind.Array
            ?pairs.EnumerateArray().Any(p=>p.ValueKind==JsonValueKind.String&&p.GetString()==approval.PairId)
            :root.TryGetProperty("pairId",out var single)&&single.ValueKind==JsonValueKind.String&&single.GetString()==approval.PairId;
        if(revision!=originalRevision||!namesPair||
            !root.TryGetProperty("retryOfRunId",out var parent)||parent.ValueKind!=JsonValueKind.String||
            string.IsNullOrEmpty(parent.GetString()))
            throw new InvalidDataException("Legacy retry changed its pair, policy or immediate ancestor.");
        SyncReviewedConsentReference reference;
        var hasPlan=root.TryGetProperty("reviewedPlan",out var inline)&&inline.ValueKind!=JsonValueKind.Null;
        if(root.TryGetProperty("reviewedConsent",out var compact)&&compact.ValueKind!=JsonValueKind.Null)
        {
            if(hasPlan)throw new InvalidDataException("Legacy retry retains contradictory approval representations.");
            reference=SyncReviewedConsentReference.ReadRequired(compact);
            if(reference.CommandId!=approval.CommandId||reference.RequestSha256!=approval.RequestHash||
                (reference.ConsentId is not null&&reference.ConsentId!=approval.ConsentId))
                throw new InvalidDataException("Legacy retry does not bind the exact original approval bytes.");
            SyncReviewedConsentReference.RequireSelectionAgainstRoot(reference,approval.Plan);
        }
        else
        {
            if(!hasPlan||inline.ValueKind!=JsonValueKind.Object||
                inline.GetProperty("PairId").GetString()!=approval.PairId||
                inline.GetProperty("LocalScanGenerationId").GetString()!=approval.Plan.LocalScanGenerationId||
                inline.GetProperty("DropboxScanGenerationId").GetString()!=approval.Plan.DropboxScanGenerationId||
                inline.GetProperty("Choices").GetArrayLength()!=approval.Choices.Count)
                throw new InvalidDataException("Legacy inline retry changed the original reviewed inventories.");
            var bits=new byte[SyncReviewedConsentReference.EncodedByteLength(approval.Choices.Count)];
            var seen=new HashSet<int>();
            foreach(var element in inline.GetProperty("Choices").EnumerateArray())
            {
                var choice=element.Deserialize<SyncBootstrapActionChoice>()??throw new InvalidDataException("Legacy retry choice is unreadable.");
                if(!approval.Paths.TryGetValue(PilotPath.CanonicalComparisonKey(choice.RelativePath),out var ordinal)||!seen.Add(ordinal))
                    throw new InvalidDataException("Legacy retry repeats or changes an original choice.");
                var approved=approval.Plan.Choices[ordinal];
                if(choice.ExpectedGroup!=approved.ExpectedGroup||choice.Move!=approved.Move||
                    (choice.Action!="Skip for now"&&choice.Action!=approved.Action))
                    throw new InvalidDataException("Legacy retry changes an originally reviewed action.");
                if(choice.Action!="Skip for now")bits[ordinal>>3]|=(byte)(1<<(ordinal&7));
            }
            reference=new(approval.CommandId,approval.RequestHash,approval.Choices.Count,Convert.ToBase64String(bits));
        }
        reference=reference with {RequestSha256=SyncAuthorityConsentEncoding.Sha256Hex(approval.CompactJson),ConsentId=approval.ConsentId};
        using var output=new MemoryStream();
        using(var writer=new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            foreach(var property in root.EnumerateObject())
                if(property.Name is not ("reviewedPlan" or "reviewedConsent"))property.WriteTo(writer);
            writer.WritePropertyName("reviewedConsent");JsonSerializer.Serialize(writer,reference);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static async Task<EssentialLegacyApproval?> FindEssentialLegacyOperationApprovalAsync(
        SqliteConnection source,SqliteTransaction snapshot,string operation,CancellationToken token)
    {
        var approval=EssentialLegacyApprovals.GetOrCreateValue(source).Current;
        if(approval is null||!approval.Operations.ContainsKey(operation))
        {
            approval=null;string? matchedRoot=null;
            await using var q=source.CreateCommand();q.Transaction=snapshot;
            q.CommandText=EssentialSourceSql(source,EssentialLegacyOriginals+" ORDER BY command_request_id;");
            await using var rows=await q.ExecuteReaderAsync(token);
            while(await rows.ReadAsync(token))
            {
                var candidate=await ReadEssentialLegacyApprovalAsync(source,snapshot,rows.GetString(0),token);
                if(candidate.Operations.ContainsKey(operation))
                {
                    if(matchedRoot is not null)throw new InvalidDataException("Legacy action belongs to multiple original approvals.");
                    matchedRoot=candidate.CommandId;
                }
            }
            if(matchedRoot is not null)approval=await ReadEssentialLegacyApprovalAsync(source,snapshot,matchedRoot,token);
        }
        return approval;
    }

    private static async Task TransformEssentialLegacyRowAsync(SqliteConnection source,SqliteTransaction snapshot,
        EssentialConfigurationTable table,object?[] row,CancellationToken token)
    {
        var columns=table.Columns.Split(',');
        object? Get(string name)=>row[Array.IndexOf(columns,name)];
        void Set(string name,object? value)=>row[Array.IndexOf(columns,name)]=value;
        if(table.Name=="command_request"&&(string?)Get("command_kind")=="run_pair"&&Get("consent_id") is null)
        {
            var id=(string)Get("command_request_id")!;
            if(await EssentialLegacyRootAsync(source,snapshot,id,token) is not { } root)return;
            var approval=await ReadEssentialLegacyApprovalAsync(source,snapshot,root,token);
            Set("payload_json",NormalizeEssentialLegacyRequest(approval,id,(string)Get("pair_id")!,(string)Get("payload_json")!));
            Set("consent_id",approval.ConsentId);
        }
        else if(table.Name=="operation"&&Get("consent_id") is null&&
            EssentialLegacyApprovals.GetOrCreateValue(source).HasOriginals)
        {
            var operation=(string)Get("operation_id")!;
            var approval=await FindEssentialLegacyOperationApprovalAsync(source,snapshot,operation,token);
            if(approval is not null)
            {
                Set("consent_id",approval.ConsentId);
                Set("consent_item_ordinal",(long)approval.Operations[operation]);
            }
        }
    }

    private static async IAsyncEnumerable<object?[]> ReadEssentialRowsAsync(SqliteConnection source,SqliteTransaction? snapshot,
        EssentialConfigurationTable table,bool selectedSource,[EnumeratorCancellation] CancellationToken token)
    {
        if(selectedSource&&EssentialDerivedConsentTable(table.Name))
        {
            // Merge only scalar identities in SQLite order. Each legacy body is
            // parsed once for this root, then emitted through the existing bounded writer.
            await using var roots=source.CreateCommand();roots.Transaction=snapshot;
            roots.CommandText=EssentialSourceSql(source,
                "SELECT consent_id,NULL AS legacy_command FROM consent_header WHERE consent_id IN ("+EssentialConsents+") "+
                "UNION ALL SELECT 'consent-'||CASE WHEN substr(command_request_id,1,8)='command-' "+
                "THEN substr(command_request_id,9) ELSE command_request_id END,command_request_id "+
                "FROM command_request WHERE command_request_id IN ("+EssentialLegacyOriginals+") ORDER BY 1;");
            await using var identities=await roots.ExecuteReaderAsync(token);
            while(await identities.ReadAsync(token))
            {
                if(identities.IsDBNull(1))
                {
                    await using var q=source.CreateCommand();q.Transaction=snapshot;
                    q.CommandText=EssentialSourceSql(source,"SELECT "+table.Columns+" FROM ("+table.SourceSql+
                        ") WHERE consent_id=$id ORDER BY "+table.Keys+";");
                    q.Parameters.AddWithValue("$id",identities.GetString(0));
                    await using var rows=await q.ExecuteReaderAsync(token);
                    while(await rows.ReadAsync(token))yield return EssentialRow(rows);
                    continue;
                }
                var approval=await ReadEssentialLegacyApprovalAsync(source,snapshot!,identities.GetString(1),token);
                if(table.Name=="consent_header"){yield return approval.Header;continue;}
                for(var i=0;i<approval.Choices.Count;i++)
                {
                    token.ThrowIfCancellationRequested();var choice=approval.Choices[i];
                    yield return [approval.ConsentId,(long)i,PilotPath.CanonicalComparisonKey(choice.RelativePath),
                        choice.RelativePath,choice.ExpectedGroup,choice.Action,choice.ChoiceJson,choice.ChoiceSha256,
                        choice.EncodedBytes,approval.States[i],approval.Issued[i],approval.Completed[i],null,approval.References[i],1L];
                }
            }
            yield break;
        }
        await using var query=source.CreateCommand();query.Transaction=snapshot;
        query.CommandText=selectedSource?EssentialSourceSql(source,EssentialSelect(table,true)):EssentialSelect(table,false);
        await using var reader=await query.ExecuteReaderAsync(token);
        while(await reader.ReadAsync(token))
        {
            var row=EssentialRow(reader);
            if(selectedSource&&(table.Name is "command_request" or "operation"))
                await TransformEssentialLegacyRowAsync(source,snapshot!,table,row,token);
            yield return row;
        }
    }

    private static async Task RequireEssentialLegacyApprovalsAsync(SqliteConnection source,SqliteTransaction snapshot,
        CancellationToken token)
    {
        await RequireEssentialLegacyConsentLineageAsync(source,snapshot,token);
        var cache=EssentialLegacyApprovals.GetOrCreateValue(source);cache.HasOriginals=false;
        await using var q=source.CreateCommand();q.Transaction=snapshot;
        q.CommandText=EssentialSourceSql(source,EssentialLegacyOriginals+" ORDER BY command_request_id;");
        await using var roots=await q.ExecuteReaderAsync(token);
        while(await roots.ReadAsync(token))
        {
            cache.HasOriginals=true;
            var approval=await ReadEssentialLegacyApprovalAsync(source,snapshot,roots.GetString(0),token);
            await using var requests=source.CreateCommand();requests.Transaction=snapshot;
            requests.CommandText="SELECT command_request_id,pair_id,payload_json FROM command_request WHERE command_request_id IN ("+
                EssentialLegacyDescendants+") ORDER BY command_request_id;";
            requests.Parameters.AddWithValue("$root",approval.CommandId);
            await using var rows=await requests.ExecuteReaderAsync(token);
            while(await rows.ReadAsync(token))
            {
                _=await EssentialLegacyRootAsync(source,snapshot,rows.GetString(0),token);
                _=NormalizeEssentialLegacyRequest(approval,rows.GetString(0),rows.GetString(1),rows.GetString(2));
            }
        }
        if(!cache.HasOriginals)return;
        await using var actions=source.CreateCommand();actions.Transaction=snapshot;
        actions.CommandText=EssentialSourceSql(source,"""
            SELECT o.operation_id FROM operation o JOIN operation_group g ON g.operation_group_id=o.operation_group_id
            WHERE o.consent_id IS NULL AND json_extract(g.detail_json,'$.purpose')='reviewed_first_sync'
              AND o.operation_id IN (
            """+EssentialOperations+") ORDER BY o.operation_id;");
        await using var required=await actions.ExecuteReaderAsync(token);
        while(await required.ReadAsync(token))
            if(await FindEssentialLegacyOperationApprovalAsync(source,snapshot,required.GetString(0),token) is null)
                throw new InvalidDataException("Legacy choice eligibility is unresolved: retained reviewed action has no original approval link.");
    }
}

using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityDatabase
{
    // A retained historical proof is never reissued under the successor generation.
    // Its exact immutable graph and certificate must still satisfy the existing
    // production terminal-prerequisite reader on both sides of the copy.
    private static async Task VerifyEssentialClosedActionsAsync(SqliteConnection connection,
        SqliteTransaction snapshot,bool selectedSource,CancellationToken token)
    {
        if(selectedSource&&EssentialLegacySource(connection))return;
        var selection=selectedSource?"operation_id IN ("+EssentialOperations+") AND ":"";
        await using var query=connection.CreateCommand();query.Transaction=snapshot;
        query.CommandText="SELECT operation_id,closure_json,closure_sha256,closure_encoded_bytes "+
            "FROM operation WHERE "+selection+"(closure_json IS NOT NULL OR completion_state_json IS NOT NULL) ORDER BY operation_id;";
        await using var rows=await query.ExecuteReaderAsync(token);
        while(await rows.ReadAsync(token))
        {
            var id=rows.GetString(0);
            var certificate=rows.IsDBNull(1)?null:rows.GetString(1);
            var hash=rows.IsDBNull(2)?null:rows.GetString(2);
            if(certificate is null)
            {
                await RequireEssentialCommittedOutcomeAsync(connection,snapshot,id,token);
                continue;
            }
            if(!ValidateTerminalPrerequisite(certificate,hash,id)||rows.IsDBNull(3))
                throw new InvalidDataException("Retained completed action lacks a supported exact terminal closure proof.");
            using var proof=JsonDocument.Parse(certificate!);
            var graph=await ReadActionGraphCoreAsync(connection,snapshot,id,token);
            if(graph is null||graph.Sha256!=proof.RootElement.GetProperty("immutableGraphSha256").GetString()||
                graph.Bytes+Encoding.UTF8.GetByteCount(certificate!)!=rows.GetInt64(3))
                throw new InvalidDataException("Retained completed action closure differs from its exact immutable graph.");
            if(await ScalarLongAsync(connection,snapshot,"""
                SELECT count(*) FROM operation WHERE operation_id=$id AND
                  (json_extract(closure_json,'$.registrationGeneration') IS NOT registration_generation
                   OR json_extract(closure_json,'$.registrationSequence') IS NOT registration_sequence
                   OR json_extract(closure_json,'$.intentSha256') IS NOT registration_intent_sha256
                   OR json_extract(closure_json,'$.status') IS NOT status
                   OR json_extract(closure_json,'$.kind') IS NOT kind
                   OR json_extract(closure_json,'$.pairId') IS NOT pair_id
                   OR json_extract(closure_json,'$.objectId') IS NOT object_id
                   OR json_extract(closure_json,'$.configurationRevision') IS NOT configuration_revision
                   OR json_extract(closure_json,'$.originCommand') IS NOT origin_command_id
                   OR json_extract(closure_json,'$.executionCommand') IS NOT execution_command_id
                   OR json_extract(closure_json,'$.outcome') IS NOT closure_kind
                   OR json_extract(closure_json,'$.closureUtc') IS NOT closure_utc
                   OR json(json_extract(closure_json,'$.completion')) IS NOT json(completion_state_json));
                """,token,("$id",id))!=0)
                throw new InvalidDataException("Retained completed action certificate differs from its bound action identity.");
        }
    }
    private static Task SetEssentialActionAccountingAsync(SqliteConnection target,CancellationToken token)=>
        ExecuteAsync(target,null,"""
            UPDATE installation SET action_closed_count=(SELECT count(*) FROM operation WHERE closure_utc IS NOT NULL),
                action_closed_bytes=(SELECT coalesce(sum(closure_encoded_bytes),0) FROM operation WHERE closure_utc IS NOT NULL);
            """,token);
    private static async Task VerifyEssentialActionAccountingAsync(SqliteConnection target,CancellationToken token)
    {
        if(await ScalarLongAsync(target,null,"""
            SELECT count(*) FROM installation WHERE
                action_closed_count<>(SELECT count(*) FROM operation WHERE closure_utc IS NOT NULL)
                OR action_closed_bytes<>(SELECT coalesce(sum(closure_encoded_bytes),0) FROM operation WHERE closure_utc IS NOT NULL);
            """,token)!=0)
            throw new InvalidDataException("Essential successor closed-action accounting differs.");
    }
}

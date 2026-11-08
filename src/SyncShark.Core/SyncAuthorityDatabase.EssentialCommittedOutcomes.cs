using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityDatabase
{
    // A committed snapshot is not a terminal closure certificate. Preserve its whole
    // unresolved graph and original identifiers. Ordinary maintenance may close it
    // later only after applying its existing worker, resource and dependency checks.
    private static async Task RequireEssentialCommittedOutcomeAsync(SqliteConnection c,SqliteTransaction tx,
        string id,CancellationToken token)
    {
        if(await ScalarLongAsync(c,tx,"""
            SELECT count(*) FROM operation o
            WHERE o.operation_id=$id AND o.status IN ('committed','cleanup_pending','completed')
              AND o.closure_utc IS NULL AND o.closure_json IS NULL AND o.closure_sha256 IS NULL
              AND o.closure_encoded_bytes IS NULL AND o.closure_kind IS NULL
              AND o.registration_generation IS NOT NULL AND o.registration_sequence IS NOT NULL
              AND o.registration_intent_sha256 IS NOT NULL AND o.origin_command_id IS NOT NULL
              AND o.execution_command_id IS NOT NULL AND json_type(o.completion_state_json)='object'
              AND EXISTS(SELECT 1 FROM operation_attempt a JOIN effect_receipt r ON r.attempt_id=a.attempt_id
                WHERE a.operation_id=o.operation_id AND a.status='verified_success' AND r.certainty='known_success');
            """,token,("$id",id))!=1)
            throw new InvalidDataException("Retained committed outcome has no exact successful original journal or has partial closure fields.");
        var kind=await ScalarStringAsync(c,tx,
            "SELECT json_extract(completion_state_json,'$.kind') FROM operation WHERE operation_id=$id;",token,("$id",id));
        var proof=kind switch
        {
            "synchronization_point"=>"""
                SELECT count(*) FROM operation o JOIN synchronization_point p
                  ON p.synchronization_point_id=json_extract(o.completion_state_json,'$.pointId')
                WHERE o.operation_id=$id AND o.pair_id IS p.pair_id AND o.object_id IS p.object_id
                  AND p.pair_id IS json_extract(o.completion_state_json,'$.pairId')
                  AND p.object_id IS json_extract(o.completion_state_json,'$.objectId')
                  AND p.local_object_version_id IS json_extract(o.completion_state_json,'$.localVersionId')
                  AND p.dropbox_object_version_id IS json_extract(o.completion_state_json,'$.remoteVersionId')
                  AND p.local_membership_id IS json_extract(o.completion_state_json,'$.localMembershipId')
                  AND p.dropbox_membership_id IS json_extract(o.completion_state_json,'$.remoteMembershipId')
                  AND p.authority_generation IS json_extract(o.completion_state_json,'$.generation')
                  AND p.accepted_utc IS json_extract(o.completion_state_json,'$.acceptedUtc');
                """,
            "resolved_tombstone"=>"""
                SELECT count(*) FROM operation o JOIN tombstone t ON t.tombstone_id=json_extract(o.completion_state_json,'$.id')
                WHERE o.operation_id=$id AND o.kind IN ('delete_local','delete_remote') AND t.status='resolved'
                  AND o.pair_id IS t.pair_id AND o.object_id IS t.object_id
                  AND t.pair_id IS json_extract(o.completion_state_json,'$.pairId')
                  AND t.object_id IS json_extract(o.completion_state_json,'$.objectId')
                  AND t.absent_side IS json_extract(o.completion_state_json,'$.absentSide')
                  AND t.prior_membership_id IS json_extract(o.completion_state_json,'$.priorMembershipId')
                  AND t.prior_object_version_id IS json_extract(o.completion_state_json,'$.priorVersionId')
                  AND t.observed_absent_utc IS json_extract(o.completion_state_json,'$.observedAbsentUtc')
                  AND t.complete_observation_count IS json_extract(o.completion_state_json,'$.completeObservations')
                  AND t.status IS json_extract(o.completion_state_json,'$.status');
                """,
            "manual_restore"=>"""
                SELECT count(*) FROM operation o JOIN operation_attempt a
                  ON a.operation_id=o.operation_id AND a.attempt_id=json_extract(o.completion_state_json,'$.attemptId')
                JOIN effect_receipt r ON r.attempt_id=a.attempt_id AND r.effect_receipt_id=json_extract(o.completion_state_json,'$.receiptId')
                WHERE o.operation_id=$id AND a.status='verified_success' AND r.certainty='known_success'
                  AND o.execution_command_id IS json_extract(o.completion_state_json,'$.commandId')
                  AND json(r.destination_identity_json) IS json(json_extract(o.completion_state_json,'$.destination'))
                  AND r.evidence_digest IS json_extract(o.completion_state_json,'$.evidenceDigest')
                  AND EXISTS(SELECT 1 FROM recovery_artifact f
                    WHERE f.recovery_artifact_id=json_extract(o.completion_state_json,'$.artifactId'));
                """,
            _=>throw new InvalidDataException("Retained committed outcome has an unsupported typed snapshot.")
        };
        if(await ScalarLongAsync(c,tx,proof,token,("$id",id))!=1)
            throw new InvalidDataException("Retained committed outcome differs from its original typed evidence.");
    }
}

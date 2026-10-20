namespace Massdex.SyncShark;

// Historical journal aliases are scalar attribution. Only the exact versions,
// memberships and roots needed to verify a current candidate have foreign keys.
internal static class SyncAuthoritySchemaMigration14
{
    public const int PreviousVersion = 13;
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS current_copy_completion (
            pair_id TEXT NOT NULL REFERENCES sync_pair(pair_id) ON DELETE RESTRICT,
            object_id TEXT NOT NULL REFERENCES logical_object(object_id) ON DELETE RESTRICT,
            point_id TEXT NOT NULL,
            point_generation INTEGER NOT NULL CHECK(point_generation>0),
            local_version_id TEXT REFERENCES object_version(object_version_id) ON DELETE RESTRICT,
            remote_version_id TEXT REFERENCES object_version(object_version_id) ON DELETE RESTRICT,
            local_membership_id TEXT REFERENCES membership(membership_id) ON DELETE RESTRICT,
            remote_membership_id TEXT REFERENCES membership(membership_id) ON DELETE RESTRICT,
            local_root_id TEXT REFERENCES root_binding(root_binding_id) ON DELETE RESTRICT,
            remote_root_id TEXT REFERENCES root_binding(root_binding_id) ON DELETE RESTRICT,
            path_key TEXT,
            copy_operation_id TEXT,
            attempt_id TEXT,
            receipt_id TEXT,
            destination_identity_json TEXT CHECK(destination_identity_json IS NULL OR json_valid(destination_identity_json)),
            attempt_status TEXT,
            receipt_evidence_json TEXT CHECK(receipt_evidence_json IS NULL OR json_valid(receipt_evidence_json)),
            coverage_verified INTEGER NOT NULL DEFAULT 0 CHECK(coverage_verified IN (0,1)),
            PRIMARY KEY(pair_id,object_id),
            CHECK(copy_operation_id IS NULL OR
                (local_version_id IS NOT NULL AND remote_version_id IS NOT NULL
                 AND local_membership_id IS NOT NULL AND remote_membership_id IS NOT NULL
                 AND local_root_id IS NOT NULL AND remote_root_id IS NOT NULL AND path_key IS NOT NULL
                 AND attempt_id IS NOT NULL AND receipt_id IS NOT NULL AND destination_identity_json IS NOT NULL
                 AND receipt_evidence_json IS NOT NULL))
        ) STRICT;
        CREATE TABLE IF NOT EXISTS copy_predecessor (
            pair_id TEXT NOT NULL REFERENCES sync_pair(pair_id) ON DELETE RESTRICT,
            object_id TEXT NOT NULL REFERENCES logical_object(object_id) ON DELETE RESTRICT,
            point_id TEXT NOT NULL,
            point_generation INTEGER NOT NULL CHECK(point_generation>0),
            local_version_id TEXT REFERENCES object_version(object_version_id) ON DELETE RESTRICT,
            remote_version_id TEXT REFERENCES object_version(object_version_id) ON DELETE RESTRICT,
            local_membership_id TEXT REFERENCES membership(membership_id) ON DELETE RESTRICT,
            remote_membership_id TEXT REFERENCES membership(membership_id) ON DELETE RESTRICT,
            local_root_id TEXT REFERENCES root_binding(root_binding_id) ON DELETE RESTRICT,
            remote_root_id TEXT REFERENCES root_binding(root_binding_id) ON DELETE RESTRICT,
            path_key TEXT,
            copy_operation_id TEXT,
            attempt_id TEXT,
            receipt_id TEXT,
            destination_identity_json TEXT CHECK(destination_identity_json IS NULL OR json_valid(destination_identity_json)),
            attempt_status TEXT,
            receipt_evidence_json TEXT CHECK(receipt_evidence_json IS NULL OR json_valid(receipt_evidence_json)),
            PRIMARY KEY(pair_id,object_id,local_membership_id),
            CHECK(local_version_id IS NOT NULL AND remote_version_id IS NOT NULL
                AND local_membership_id IS NOT NULL AND remote_membership_id IS NOT NULL
                AND local_root_id IS NOT NULL AND remote_root_id IS NOT NULL AND path_key IS NOT NULL)
        ) STRICT;
        CREATE INDEX IF NOT EXISTS current_copy_by_operation ON current_copy_completion(copy_operation_id);
        CREATE INDEX IF NOT EXISTS current_copy_by_point ON current_copy_completion(pair_id,point_id);
        CREATE INDEX IF NOT EXISTS copy_predecessor_by_path ON copy_predecessor(pair_id,path_key);
        CREATE INDEX IF NOT EXISTS current_copy_completion_local_version_id ON current_copy_completion(local_version_id);
        CREATE INDEX IF NOT EXISTS current_copy_completion_remote_version_id ON current_copy_completion(remote_version_id);
        CREATE INDEX IF NOT EXISTS current_copy_completion_local_membership_id ON current_copy_completion(local_membership_id);
        CREATE INDEX IF NOT EXISTS current_copy_completion_remote_membership_id ON current_copy_completion(remote_membership_id);
        CREATE INDEX IF NOT EXISTS current_copy_completion_local_root_id ON current_copy_completion(local_root_id);
        CREATE INDEX IF NOT EXISTS current_copy_completion_remote_root_id ON current_copy_completion(remote_root_id);
        CREATE INDEX IF NOT EXISTS copy_predecessor_local_version_id ON copy_predecessor(local_version_id);
        CREATE INDEX IF NOT EXISTS copy_predecessor_remote_version_id ON copy_predecessor(remote_version_id);
        CREATE INDEX IF NOT EXISTS copy_predecessor_local_membership_id ON copy_predecessor(local_membership_id);
        CREATE INDEX IF NOT EXISTS copy_predecessor_remote_membership_id ON copy_predecessor(remote_membership_id);
        CREATE INDEX IF NOT EXISTS copy_predecessor_local_root_id ON copy_predecessor(local_root_id);
        CREATE INDEX IF NOT EXISTS copy_predecessor_remote_root_id ON copy_predecessor(remote_root_id);
        CREATE INDEX IF NOT EXISTS operation_copy_object_state ON operation(object_id,status);
        CREATE INDEX IF NOT EXISTS operation_member_copy_object ON operation_member(object_id,operation_id);
        CREATE INDEX IF NOT EXISTS operation_member_copy_source
            ON operation_member(pair_id,object_version_id,operation_id) WHERE role='source';
        CREATE INDEX IF NOT EXISTS operation_member_copy_destination
            ON operation_member(operation_id,expected_path_key) WHERE role='destination';
        """;
}

namespace Massdex.SyncShark;

// Historical accepted points remain immutable. A later accepted object can
// replace the same path only through newer memberships on every retained side.
// Observation alone, wall-clock ordering and object-ID sorting grant nothing.
internal static class SyncCurrentBaselineSchema
{
    internal const string Sql = """
        CREATE VIEW IF NOT EXISTS current_synchronization_point AS
        SELECT s.synchronization_point_id,s.pair_id,s.object_id,
            s.local_object_version_id,s.dropbox_object_version_id,
            s.local_membership_id,s.dropbox_membership_id,
            s.accepted_utc,s.authority_generation
        FROM synchronization_point s
        LEFT JOIN membership lm ON lm.membership_id=s.local_membership_id
        LEFT JOIN membership dm ON dm.membership_id=s.dropbox_membership_id
        WHERE s.authority_generation=(
            SELECT max(latest.authority_generation) FROM synchronization_point latest
            WHERE latest.pair_id=s.pair_id AND latest.object_id=s.object_id)
          AND CASE WHEN lm.membership_id IS NOT NULL THEN NOT EXISTS (
            SELECT 1 FROM membership nl
            CROSS JOIN synchronization_point newer
                INDEXED BY membership_lifetime_synchronization_point_local_membership_id_ref
            CROSS JOIN membership nd
            WHERE nl.root_binding_id=lm.root_binding_id
              AND nl.relative_path_key=lm.relative_path_key AND nl.generation>lm.generation
              AND newer.local_membership_id=nl.membership_id
              AND nd.membership_id=newer.dropbox_membership_id
              AND newer.pair_id=s.pair_id AND newer.object_id<>s.object_id
              AND (dm.membership_id IS NULL OR (
                nd.root_binding_id=dm.root_binding_id
                AND nd.relative_path_key=dm.relative_path_key AND nd.generation>dm.generation))
          ) ELSE NOT EXISTS (
            SELECT 1 FROM membership nd
            CROSS JOIN synchronization_point newer
                INDEXED BY membership_lifetime_synchronization_point_dropbox_membership_id_ref
            CROSS JOIN membership nl
            WHERE nd.root_binding_id=dm.root_binding_id
              AND nd.relative_path_key=dm.relative_path_key AND nd.generation>dm.generation
              AND newer.dropbox_membership_id=nd.membership_id
              AND nl.membership_id=newer.local_membership_id
              AND newer.pair_id=s.pair_id AND newer.object_id<>s.object_id
          ) END;
        """;
}

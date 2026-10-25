namespace Massdex.SyncShark;

public sealed record SyncExplorerItemStatus(string RelativePath, string Status);

public static class SyncExplorerStatus
{
    public static string Combine(string retained, string? recent) =>
        retained is "Needs Attention" or "Not Selected or Ignored" or "Waiting to Sync" ? retained : recent ?? retained;
    public static string Display(string itemStatus, string pairStatus) =>
        pairStatus is "Disconnected" or "Paused" ? pairStatus : itemStatus;
}

public sealed partial class SyncAuthorityDatabase
{
    // Keyset paging over retained metadata only. No filesystem walk, hydration or provider call.
    public async Task<IReadOnlyList<SyncExplorerItemStatus>> LoadExplorerStatusPageAsync(string pairId,
        string? afterPath = null, int limit = 256, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        if (afterPath is not null) afterPath = PilotPath.NormalizeRelative(afterPath);
        await using var connection = await OpenReadOnlyConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.relative_path_key, CASE
              WHEN p.included=0 THEN 'Not Selected or Ignored'
              WHEN EXISTS(SELECT 1 FROM operation_member m JOIN operation o ON o.operation_id=m.operation_id
                WHERE m.pair_id=p.pair_id AND m.expected_path_key=p.relative_path_key COLLATE NOCASE
                  AND o.status IN ('blocked','effect_unknown','cleanup_pending')) THEN 'Needs Attention'
              WHEN EXISTS(SELECT 1 FROM operation_member m JOIN operation o ON o.operation_id=m.operation_id
                WHERE m.pair_id=p.pair_id AND m.expected_path_key=p.relative_path_key COLLATE NOCASE
                  AND o.status NOT IN ('completed','cancelled','superseded','blocked')) THEN 'Waiting to Sync'
              WHEN EXISTS(SELECT 1 FROM synchronization_point s
                JOIN effective_observation local ON local.scan_generation_id=p.local_scan_generation_id
                  AND local.object_version_id=s.local_object_version_id AND local.membership_id=s.local_membership_id
                JOIN effective_observation cloud ON cloud.scan_generation_id=p.dropbox_scan_generation_id
                  AND cloud.object_version_id=s.dropbox_object_version_id AND cloud.membership_id=s.dropbox_membership_id
                JOIN membership m ON m.membership_id=s.local_membership_id
                WHERE s.pair_id=p.pair_id AND m.relative_path_key=p.relative_path_key COLLATE NOCASE
                  AND m.state='active' AND p.local_present=1 AND p.dropbox_present=1) THEN 'Up to Date'
              ELSE 'Ready to Check' END
            FROM sync_path_index p WHERE p.pair_id=$pair AND (p.local_present=1 OR p.online_only=1)
              AND ($after IS NULL OR p.relative_path_key>$after COLLATE NOCASE)
            ORDER BY p.relative_path_key COLLATE NOCASE LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$pair", pairId);
        command.Parameters.AddWithValue("$after", (object?)afterPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<SyncExplorerItemStatus>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetString(0), reader.GetString(1)));
        return result;
    }
}

public sealed partial class SyncAuthorityRuntimeHost
{
    public Task<IReadOnlyList<SyncExplorerItemStatus>> LoadExplorerStatusPageAsync(string pairId,
        string? afterPath = null, CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        return _authority.LoadExplorerStatusPageAsync(pairId, afterPath, cancellationToken: cancellationToken);
    }
}

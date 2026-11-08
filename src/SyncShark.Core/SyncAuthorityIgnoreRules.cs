namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityDatabase
{
    public async Task<IReadOnlyList<string>> LoadIgnoredPathsAsync(string pairId,
        IReadOnlyList<SyncIgnoreRule>? rules, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        var normalized = SyncIgnorePolicy.Normalize(rules);
        if (normalized.Count == 0) return [];
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // Include retained memberships even when a newly ignored file is absent from BOTH
        // inventories. Its old baseline must never be interpreted as a new deletion.
        command.CommandText = """
            SELECT relative_path_key,object_kind FROM sync_path_index WHERE pair_id=$pair
            UNION
            SELECT m.relative_path_key,o.object_kind
            FROM synchronization_point s
            JOIN logical_object o ON o.object_id=s.object_id
            JOIN membership m ON m.membership_id IN (s.local_membership_id,s.dropbox_membership_id)
            WHERE s.pair_id=$pair AND s.authority_generation=(
                SELECT max(n.authority_generation) FROM synchronization_point n
                WHERE n.pair_id=s.pair_id AND n.object_id=s.object_id);
            """;
        command.Parameters.AddWithValue("$pair", pairId);
        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var path = reader.GetString(0);
            if (SyncIgnorePolicy.IsIgnored(path, reader.GetString(1) == "directory", normalized)) matches.Add(path);
        }
        return matches.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

public sealed partial class SyncAuthorityRuntimeHost
{
    public async Task<SyncIgnorePreview> PreviewIgnoreRulesAsync(string pairId,
        IReadOnlyList<SyncIgnoreRule> rules, CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        var pairs = await LoadPairsAsync(cancellationToken);
        if (!pairs.Pairs.Any(pair => pair.Id == pairId)) throw new InvalidOperationException("This folder pair no longer exists.");
        var matches = await _authority.LoadIgnoredPathsAsync(pairId, rules, cancellationToken);
        return new SyncIgnorePreview(matches.Count, matches.Take(8).ToArray());
    }
}

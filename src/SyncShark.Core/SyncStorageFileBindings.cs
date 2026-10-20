using System.Globalization;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed record SyncStorageFileIdentity(string RelativePath, ulong LocalId, string? RemoteId);

public sealed partial class SyncAuthorityDatabase
{
    internal async Task<SyncStorageFileIdentity?> LoadStorageFileIdentityAsync(string pairId, string relativePath,
        CancellationToken cancellationToken)
    {
        RequireIdentifier(pairId, nameof(pairId));
        var path = PilotPath.NormalizeRelative(relativePath);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // Indexed pathname lookup supplies a candidate only. The caller must compare its
        // retained local identity with the actually opened file before using the remote ID.
        command.CommandText = "SELECT relative_path_key,local_stable_identity,dropbox_stable_identity FROM sync_path_index WHERE pair_id=$pair AND relative_path_key=$path AND object_kind='file';";
        command.Parameters.AddWithValue("$pair", pairId); command.Parameters.AddWithValue("$path", path);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadStorageFileIdentity(reader) : null;
    }

    internal async Task<IReadOnlyList<SyncStorageFileIdentity>> LoadStorageFileBindingsAsync(SyncPair pair,
        CancellationToken cancellationToken)
    {
        var rules = SyncDirectoryStoragePolicy.NormalizeFiles(pair.FileStorageRules);
        if (rules.Count == 0) return [];
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // One filtered DB read, not one whole synchronization-point history read per file.
        // Only explicit file preferences are materialized; the full account inventory stays in SQLite.
        command.CommandText = """
            SELECT relative_path_key,local_stable_identity,dropbox_stable_identity FROM sync_path_index
            WHERE pair_id=$pair AND object_kind='file' AND (
                local_stable_identity IN (SELECT value FROM json_each($local)) OR
                dropbox_stable_identity IN (SELECT value FROM json_each($remote))) LIMIT 10001;
            """;
        command.Parameters.AddWithValue("$pair", pair.Id);
        command.Parameters.AddWithValue("$local", JsonSerializer.Serialize(rules.Where(rule => rule.LocalFileId != 0)
            .Select(rule => $"local:{pair.LocalVolumeSerialNumber:x8}:{rule.LocalFileId:x16}")));
        command.Parameters.AddWithValue("$remote", JsonSerializer.Serialize(rules.Select(rule => rule.RemoteFileId).OfType<string>()));
        var result = new List<SyncStorageFileIdentity>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (result.Count == 10000) throw new IOException("File preference identity lookup exceeded its safe bound.");
            result.Add(ReadStorageFileIdentity(reader));
        }
        return result;
    }

    private static SyncStorageFileIdentity ReadStorageFileIdentity(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        var parts = reader.IsDBNull(1) ? [] : reader.GetString(1).Split(':');
        var local = parts.Length == 3 && ulong.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id) ? id : 0;
        return new(reader.GetString(0), local, reader.IsDBNull(2) ? null : reader.GetString(2));
    }
}

internal static class SyncFileStorageBinding
{
    internal static IReadOnlyList<SyncFileStorageRule> Rebind(IReadOnlyList<SyncFileStorageRule>? preferences,
        IReadOnlyList<SyncStorageFileIdentity> identities)
    {
        var rules = SyncDirectoryStoragePolicy.NormalizeFiles(preferences);
        var local = identities.Where(item => item.LocalId != 0).ToLookup(item => item.LocalId);
        var remote = identities.Where(item => item.RemoteId is not null).ToLookup(item => item.RemoteId!, StringComparer.Ordinal);
        var result = new List<SyncFileStorageRule>();
        foreach (var rule in rules)
        {
            var matches = local[rule.LocalFileId].Concat(rule.RemoteFileId is null ? [] : remote[rule.RemoteFileId]).Distinct().ToArray();
            var paths = matches.Select(item => item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length != 1) { result.Add(rule); continue; }
            var localIds = matches.Select(item => item.LocalId).Where(id => id != 0).Distinct().ToArray();
            var remoteIds = matches.Select(item => item.RemoteId).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
            if (localIds.Length > 1 || remoteIds.Length > 1) { result.Add(rule); continue; }
            result.Add(rule with { RelativePath = paths[0], LocalFileId = localIds.FirstOrDefault() is var id && id != 0 ? id : rule.LocalFileId,
                RemoteFileId = remoteIds.FirstOrDefault() ?? rule.RemoteFileId });
        }
        // A case-only rename or temporarily conflicting identity must not silently replace a rule.
        try { return SyncDirectoryStoragePolicy.NormalizeFiles(result); }
        catch (InvalidDataException) { return rules; }
    }
}
